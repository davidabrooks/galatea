// GroupPicks (added 2026-09-25): group info/join/list and profile picks.
// - group info <uuid>: GroupProfileRequest -> name, open enrollment, membership fee, member count
// - group join <uuid>: profile first; refuses unless OpenEnrollment && MembershipFee == 0; JoinGroupRequest -> JoinGroupReply
// - group list: AgentDataUpdateRequest -> AgentGroupDataUpdate (current groups, my title in each, active group + title); falls back to the cached last update
// - group invites [all|selftest] | group accept <n|group name> [confirm] [force] | group decline <n|group name>   (2026-10-02)
//   pending group invitations (urgent webhook 'group_invite'); see GroupInvite below for the policy
//   2026-10-03: persisted with the other offers (OfferStore.cs) and re-surfaced after a restart; ONE auto-accept rule (David):
//   group 'Sunrise Suites' (394073e3-...) at L$0 from shadowknight.falconer or andyandroid -> accept, sethome if in Peronaut,
//   urgent webhook 'group_invite_accepted'
// - pick list | pick info <id> | pick lookup <region> <x> <y> <z> (dry run) | pick create <region> <x> <y> <z> | <name> | <desc> | pick delete <id>
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Regex = System.Text.RegularExpressions.Regex;
using RegexOptions = System.Text.RegularExpressions.RegexOptions;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    static Dictionary<UUID, Group> currentGroups = new();
    static DateTime currentGroupsAt = DateTime.MinValue;

    static void HookGroups()
    {
        client.Groups.CurrentGroups += (s, e) =>
        {
            currentGroups = new Dictionary<UUID, Group>(e.Groups);
            currentGroupsAt = DateTime.Now;
        };
        client.Groups.GroupJoinedReply += (s, e) => Log("group", $"JoinGroupReply group {e.GroupID} success={e.Success}");
    }

    static async Task<Group?> FetchGroupProfile(UUID id, int timeoutMs = 15000)
    {
        var tcs = new TaskCompletionSource<Group>(TaskCreationOptions.RunContinuationsAsynchronously);
        void H(object s, GroupProfileEventArgs e) { if (e.Group.ID == id) tcs.TrySetResult(e.Group); }
        client.Groups.GroupProfile += H;
        try
        {
            client.Groups.RequestGroupProfile(id);
            return await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)) == tcs.Task ? tcs.Task.Result : null;
        }
        finally { client.Groups.GroupProfile -= H; }
    }

    // ---- group invitations (2026-10-02, David: stop ignoring them) ----------------------------------------
    // GroupInvitation IM: FromAgentID = the group (what GroupInviteRespond needs), IMSessionID = the invite's transaction id,
    // BinaryBucket = S32 membership fee (network byte order) + role UUID. The text names the inviter/group; the group profile
    // is fetched for the authoritative name and fee. Not auto-accepted, except the Sunrise Suites rule below. Policy: accept only invites from David Nightingale,
    // SophieJeanneLaDouce, or the Peronaut rental group/agent clearly tied to David's rental; anyone else needs David's OK
    // ('confirm'). A join fee > L$0 (or an unknown fee) also needs 'force' (L$ only with David's OK).
    class GroupInvite
    {
        public int N; public DateTime At; public UUID GroupId; public string GroupName; public string FromName; public string Inviter;
        public UUID Session; public UUID RoleId; public int? Fee; public string Message; public bool Offline; public string State = "pending";
        public int? ProfileFee;                 // group profile's membership fee (authoritative), null = not fetched / no reply
        public bool Restored, Redelivered;      // loaded from the offer store after a restart; SL re-sent it at login (fresh session)
        public bool Resurfaced, Synthetic, AutoBusy; // restored invite already handled this process; selftest entry (never persisted); auto-accept running
    }
    static readonly List<GroupInvite> groupInvites = new(); static int groupInviteSeq;

    static readonly Regex InviterRx = new(@"^\s*(?<who>[^\r\n]+?)\s+has invited you to (join|become)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex GroupNameRx = new(@"invited you to (join|become a member of)\s+(the\s+group\s+)?(?<g>[^\r\n.]+?)(\.|\s*$|\r|\n)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // pure parse (selftest-covered): bucket fee/role, inviter and group name from the text
    static GroupInvite ParseGroupInvite(InstantMessage im, bool offline)
    {
        var g = new GroupInvite { At = DateTime.Now, GroupId = im.FromAgentID, FromName = im.FromAgentName, Session = im.IMSessionID, Message = im.Message ?? "", Offline = offline };
        var b = im.BinaryBucket ?? Array.Empty<byte>();
        if (b.Length >= 4) g.Fee = (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
        if (b.Length >= 20) g.RoleId = new UUID(b, 4);
        var m = InviterRx.Match(g.Message); if (m.Success) g.Inviter = m.Groups["who"].Value.Trim();
        var gm = GroupNameRx.Match(g.Message); if (gm.Success) g.GroupName = gm.Groups["g"].Value.Trim().Trim('\'', '"');
        if (g.Inviter == null && !string.IsNullOrWhiteSpace(im.FromAgentName) && !string.Equals(im.FromAgentName, g.GroupName, StringComparison.OrdinalIgnoreCase)) g.Inviter = im.FromAgentName;
        if (g.GroupName == null && !string.IsNullOrWhiteSpace(im.FromAgentName) && g.Inviter != im.FromAgentName) g.GroupName = im.FromAgentName;
        return g;
    }

    static void RecordGroupInvite(InstantMessage im, bool offline, bool notify = true)
    {
        try
        {
            var g = ParseGroupInvite(im, offline);
            lock (groupInvites)
            {
                var dup = groupInvites.FirstOrDefault(x => x.Session == g.Session && x.GroupId == g.GroupId && x.State == "pending");
                if (dup != null)
                {   // offline re-delivery; for a restored invite this means SL still holds it, so its session is current
                    if (dup.Restored && !dup.Redelivered) { dup.Redelivered = true; Log("offer", $"group invite #{dup.N} (restored) re-delivered by SL at login: session still current"); }
                    return;
                }
                g.N = ++groupInviteSeq; g.Synthetic = !notify; groupInvites.Add(g);
                while (groupInvites.Count > 50) groupInvites.RemoveAt(0);
            }
            Log("offer", $"group invitation recorded as invite #{g.N}: {GroupInviteLine(g)}{(offline ? " [offline]" : "")} - pending ('group accept {g.N}' / 'group decline {g.N}')");
            if (!notify) return; // selftest: no webhook, no profile fetch, not persisted
            SaveOfferStore();
            _ = Task.Run(() => ProcessGroupInvite(g, false));
        }
        catch (Exception ex) { Log("offer", "group invite record failed: " + ex.GetBaseException().Message); }
    }

    // new or restored invite: Sunrise Suites candidates go through the auto-accept check first (one webhook: accepted or
    // why not); everything else is notified at once as a pending 'group_invite'. The profile gives the authoritative name/fee.
    static async Task ProcessGroupInvite(GroupInvite g, bool restored)
    {
        try
        {
            string note = restored ? RestoredInviteNote(g) : "";
            bool candidate = SunriseCandidate(g);
            if (!candidate) Notify("group_invite", g.Inviter ?? g.FromName, g.GroupId, $"group invite #{g.N}: {GroupInviteLine(g)} (pending; needs 'group accept'){note}. Text: {g.Message}", null); // urgent webhook
            var p = await FetchGroupProfile(g.GroupId);
            if (p != null)
            {
                lock (groupInvites) { g.GroupName = p.Value.Name; g.ProfileFee = p.Value.MembershipFee; if (g.Fee == null || p.Value.MembershipFee > g.Fee) g.Fee = p.Value.MembershipFee; }
                Log("offer", $"group invite #{g.N}: profile '{p.Value.Name}' fee L${p.Value.MembershipFee} open_enrollment={p.Value.OpenEnrollment}");
                SaveOfferStore();
            }
            if (candidate) await SunriseAutoAccept(g, note);
        }
        catch (Exception ex) { Log("offer", $"group invite #{g.N} processing failed: " + ex.GetBaseException().Message); }
    }

    // ---- Sunrise Suites auto-accept (2026-10-03, David approved) ------------------------------------------
    static readonly UUID SunriseSuitesId = new("394073e3-c51a-90d3-3d22-f04ffb35a471");
    static readonly string[] SunriseInviters = { "shadowknight.falconer", "andyandroid" };
    static readonly string HomeRegion = Env("GT_HOME_REGION", "Peronaut");
    static string NormAvName(string n)
    {
        n = Regex.Replace((n ?? "").Trim().ToLowerInvariant().Replace('.', ' '), @"\s+", " ");
        return n.EndsWith(" resident") ? n[..^9] : n;
    }
    static bool SunriseInviter(string inviter) => !string.IsNullOrWhiteSpace(inviter) && SunriseInviters.Any(x => NormAvName(x) == NormAvName(inviter));
    static bool SunriseCandidate(GroupInvite g) => g.GroupId == SunriseSuitesId && SunriseInviter(g.Inviter);

    // pure (selftest-covered): null = the client may accept on its own, else why not. Fee must be a known L$0 in the invite
    // AND in the group profile; anything else stays pending for 'group accept ... force' (David).
    static string SunriseAutoAcceptBlock(GroupInvite g)
    {
        if (g.GroupId != SunriseSuitesId) return "not the Sunrise Suites group";
        if (!SunriseInviter(g.Inviter)) return $"inviter '{g.Inviter ?? "unknown"}' is not shadowknight.falconer / andyandroid";
        if (g.State != "pending") return $"already {g.State}";
        if (g.Fee == null) return "join fee unknown in the invite (needs David: 'force')";
        if (g.Fee != 0) return $"join fee L${g.Fee} (needs David: 'force')";
        if (g.ProfileFee == null) return "group profile did not answer, so the L$0 fee is not confirmed (needs 'group accept')";
        if (g.ProfileFee != 0) return $"group profile says join fee L${g.ProfileFee} (needs David: 'force')";
        return null;
    }

    static async Task SunriseAutoAccept(GroupInvite g, string note)
    {
        lock (groupInvites) { if (g.AutoBusy) return; g.AutoBusy = true; }
        try
        {
            var block = SunriseAutoAcceptBlock(g);
            if (block != null)
            {
                Log("group", $"Sunrise Suites invite #{g.N}: NOT auto-accepted: {block}");
                Notify("group_invite", g.Inviter ?? g.FromName, g.GroupId, $"group invite #{g.N}: {GroupInviteLine(g)} - Sunrise Suites auto-accept NOT done: {block}; pending{note}. Text: {g.Message}", null);
                return;
            }
            Log("group", $"Sunrise Suites invite #{g.N} from {g.Inviter}: auto-accepting (David's rule: L$0, shadowknight.falconer / andyandroid)");
            var (res, member) = await AcceptGroupInviteCore(g, "auto: Sunrise Suites rule");
            if (!member) { await Task.Delay(5000); var (again, _) = await FetchCurrentGroups(); member = again.ContainsKey(g.GroupId); if (member) res += "; member confirmed on re-check"; }
            string home;
            var region = client.Network.CurrentSim?.Name;
            if (string.Equals(region, HomeRegion, StringComparison.OrdinalIgnoreCase)) home = await SetHomeCmd();
            else home = $"sethome skipped: in '{region ?? "?"}', not {HomeRegion}";
            var text = $"AUTO-ACCEPTED Sunrise Suites group invite #{g.N} from {g.Inviter} (David's rule, L$0): {res}. {home}{note}" +
                       (member ? "" : " WARNING: not a member yet; the invite may have expired (ask the inviter to re-send).");
            Log("group", text);
            Notify("group_invite_accepted", g.Inviter ?? g.FromName, g.GroupId, text, null); // urgent webhook: tell David
        }
        finally { lock (groupInvites) g.AutoBusy = false; }
    }

    static string RestoredInviteNote(GroupInvite g) => g.Redelivered
        ? " [restored after a client restart; SL re-delivered it at login, so the session is current]"
        : $" [restored after a client restart (received {g.At:MM-dd HH:mm} PT); SL may not accept a pre-relog invite session: if accepting does not make me a member, ask the inviter to re-send]";

    static bool GroupInviterAllowed(GroupInvite g) => g.Inviter != null && (LureAllowed(g.Inviter) || LureAllowed(g.Inviter + " Resident"));

    static string GroupInviteLine(GroupInvite g) =>
        $"#{g.N} group '{g.GroupName ?? "?"}' ({g.GroupId}) from {(g.Inviter ?? "?")}" +
        $" role {(g.RoleId == UUID.Zero ? "Everyone" : g.RoleId.ToString())} fee {(g.Fee == null ? "unknown" : "L$" + g.Fee)} session {g.Session}" +
        $" at {g.At:MM-dd HH:mm} PT [{g.State}]{(g.Restored ? (g.Redelivered ? " [restored, re-delivered]" : " [restored: session may be stale]") : "")}" +
        (SunriseCandidate(g) ? " (Sunrise Suites auto-accept rule)" : GroupInviterAllowed(g) ? " (inviter allow-listed)" : " (inviter NOT allow-listed: accept needs 'confirm' = David's OK)");

    // pure (selftest-covered): null = may accept, else the refusal text
    static string GroupAcceptBlock(GroupInvite g, bool confirm, bool force)
    {
        if (g.State != "pending") return $"invite #{g.N} is already {g.State}";
        if (!GroupInviterAllowed(g) && !confirm)
            return $"refused: inviter '{g.Inviter ?? "unknown"}' is not David Nightingale / SophieJeanneLaDouce. Accept only with David's OK (e.g. the Peronaut rental group): 'group accept {g.N} confirm'";
        if ((g.Fee ?? -1) != 0 && !force)
            return $"refused: joining '{g.GroupName ?? g.GroupId.ToString()}' costs {(g.Fee == null ? "an UNKNOWN fee" : "L$" + g.Fee)}; L$ only with David's OK: add 'force'";
        return null;
    }

    static GroupInvite FindGroupInvite(string key)
    {
        lock (groupInvites)
        {
            if (int.TryParse(key, out var n)) return groupInvites.FirstOrDefault(x => x.N == n);
            var p = groupInvites.Where(x => x.State == "pending").ToList();
            return p.LastOrDefault(x => string.Equals(x.GroupName, key, StringComparison.OrdinalIgnoreCase))
                ?? (p.Count(x => x.GroupName?.Contains(key, StringComparison.OrdinalIgnoreCase) == true) == 1 ? p.First(x => x.GroupName?.Contains(key, StringComparison.OrdinalIgnoreCase) == true) : null);
        }
    }

    static string GroupInvitesText(bool all = false)
    {
        List<GroupInvite> l; lock (groupInvites) l = groupInvites.Where(x => all || x.State == "pending").ToList();
        var sb = new StringBuilder($"{l.Count} {(all ? "group invites this session" : "pending group invites")}\n");
        foreach (var g in l) sb.AppendLine("  " + GroupInviteLine(g));
        return sb.ToString().TrimEnd();
    }

    static async Task<string> GroupInviteRespondCmd(string[] a, bool accept)
    {
        var words = a.Skip(1).ToList();
        bool confirm = words.RemoveAll(x => x.Equals("confirm", StringComparison.OrdinalIgnoreCase)) > 0;
        bool force = words.RemoveAll(x => x.Equals("force", StringComparison.OrdinalIgnoreCase)) > 0;
        var key = string.Join(' ', words).Trim().Trim('"');
        if (key.Length == 0) return $"usage: group {(accept ? "accept" : "decline")} <n|group name>{(accept ? " [confirm] [force]" : "")}";
        var g = FindGroupInvite(key);
        if (g == null) return $"no pending group invite '{key}' (see 'group invites')";
        if (!accept)
        {
            if (g.State != "pending") return $"invite #{g.N} is already {g.State}";
            client.Self.GroupInviteRespond(g.GroupId, g.Session, false); g.State = "declined"; SaveOfferStore();
            Log("group", $"declined group invite #{g.N} '{g.GroupName}' ({g.GroupId})");
            return $"declined group invite #{g.N} '{g.GroupName}' ({g.GroupId})";
        }
        var block = GroupAcceptBlock(g, confirm, force);
        if (block != null) { Log("group", $"accept #{g.N} REFUSED: {block}"); return block; }
        return (await AcceptGroupInviteCore(g, $"confirm={confirm} force={force}")).res;
    }

    // sends the accept, waits 3 s, checks membership + balance (shared by 'group accept' and the Sunrise auto-accept)
    static async Task<(string res, bool member)> AcceptGroupInviteCore(GroupInvite g, string why)
    {
        var bal0 = await BalanceAsync();
        Log("group", $"accepting group invite #{g.N} '{g.GroupName}' ({g.GroupId}) fee L${g.Fee} {why}{(g.Restored && !g.Redelivered ? " [restored invite: session may be stale]" : "")} balance_before={bal0?.ToString() ?? "?"}");
        client.Self.GroupInviteRespond(g.GroupId, g.Session, true); g.State = "accepted"; SaveOfferStore();
        await Task.Delay(3000);
        var (after, fresh) = await FetchCurrentGroups();
        var bal1 = await BalanceAsync();
        bool member = after.ContainsKey(g.GroupId);
        var res = $"accepted group invite #{g.N} '{g.GroupName}' ({g.GroupId}); member now: {(member ? "yes" : "not yet")}{(fresh ? "" : " (group list not refreshed)")}" +
                  (member && after.TryGetValue(g.GroupId, out var gg) ? $", title '{gg.MemberTitle}'" : "") + $"; balance L${bal0?.ToString() ?? "?"} -> L${bal1?.ToString() ?? "?"}" +
                  (!member && g.Restored && !g.Redelivered ? "; this invite was restored after a relog and SL may no longer accept its session: ask the inviter to re-send" : "");
        Log("group", res);
        return (res, member);
    }

    // offline logic test: synthetic invites are parsed, listed and refused by the policy checks; nothing is sent to SL
    static string GroupInvitesSelfTest()
    {
        var sb = new StringBuilder(); int pass = 0, fail = 0; void C(bool ok, string w) { if (ok) pass++; else fail++; sb.AppendLine($"{(ok ? "PASS" : "FAIL")} {w}"); }
        byte[] Bucket(int fee, UUID role) { var b = new byte[20]; b[0] = (byte)(fee >> 24); b[1] = (byte)(fee >> 16); b[2] = (byte)(fee >> 8); b[3] = (byte)fee; Buffer.BlockCopy(role.GetBytes(), 0, b, 4, 16); return b; }
        InstantMessage Im(UUID grp, string name, UUID sess, string msg, byte[] b) => new InstantMessage { Dialog = InstantMessageDialog.GroupInvitation, FromAgentID = grp, FromAgentName = name, IMSessionID = sess, Message = msg, BinaryBucket = b, GroupIM = true };
        UUID g1 = UUID.Random(), g2 = UUID.Random(), g3 = UUID.Random(), s1 = UUID.Random(), s2 = UUID.Random(), s3 = UUID.Random(), role = UUID.Random();
        var a = ParseGroupInvite(Im(g1, "David Nightingale", s1, "David Nightingale has invited you to join a group: Peronaut Residents. There is no fee.", Bucket(0, role)), false);
        C(a.Inviter == "David Nightingale" && a.Fee == 0 && a.RoleId == role && a.GroupId == g1 && a.Session == s1, $"parse: inviter '{a.Inviter}', fee L${a.Fee}, role, group id, session");
        var b = ParseGroupInvite(Im(g2, "Selftest Stranger", s2, "Selftest Stranger has invited you to join Fancy Club.", Bucket(250, UUID.Zero)), false);
        C(b.Fee == 250 && b.RoleId == UUID.Zero && b.GroupName == "Fancy Club", $"parse: fee L$250 (big-endian bucket), Everyone role, group name '{b.GroupName}'");
        var c = ParseGroupInvite(Im(g3, "David Nightingale", s3, "David Nightingale has invited you to join Paid Group.", Array.Empty<byte>()), false);
        C(c.Fee == null, "parse: no bucket -> fee unknown");
        a.N = 9001; b.N = 9002; c.N = 9003;
        C(GroupAcceptBlock(a, false, false) == null, "David's L$0 invite: may accept");
        C(GroupAcceptBlock(b, false, true)?.StartsWith("refused: inviter") == true, "stranger's invite: refused without 'confirm'");
        C(GroupAcceptBlock(b, true, false)?.Contains("L$250") == true, "L$250 fee: refused without 'force' even with 'confirm'");
        C(GroupAcceptBlock(b, true, true) == null, "stranger + fee with 'confirm force': allowed");
        C(GroupAcceptBlock(c, false, false)?.Contains("UNKNOWN") == true, "unknown fee: refused without 'force'");
        // record + list + lookup + dedupe (no webhook, no profile fetch)
        RecordGroupInvite(Im(g2, "Selftest Stranger", s2, "Selftest Stranger has invited you to join Fancy Club.", Bucket(250, UUID.Zero)), false, false);
        RecordGroupInvite(Im(g2, "Selftest Stranger", s2, "Selftest Stranger has invited you to join Fancy Club.", Bucket(250, UUID.Zero)), true, false); // offline re-delivery
        int mine; lock (groupInvites) mine = groupInvites.Count(x => x.Session == s2);
        C(mine == 1, $"duplicate (offline re-delivery) dropped ({mine})");
        C(GroupInvitesText().Contains("Fancy Club") && FindGroupInvite("fancy club")?.Session == s2, "'group invites' lists it; found by name");
        lock (groupInvites) groupInvites.RemoveAll(x => x.Session == s2);
        // Sunrise Suites auto-accept rule (pure; nothing sent)
        GroupInvite Sun(string inviter, int? fee, int? pfee, UUID? grp = null) => new GroupInvite { N = 9100, GroupId = grp ?? SunriseSuitesId, GroupName = "Sunrise Suites", Inviter = inviter, Fee = fee, ProfileFee = pfee, Session = UUID.Random() };
        C(SunriseAutoAcceptBlock(Sun("shadowknight.falconer", 0, 0)) == null, "Sunrise: shadowknight.falconer, L$0 (invite + profile) -> auto-accept");
        C(SunriseAutoAcceptBlock(Sun("Shadowknight Falconer", 0, 0)) == null, "Sunrise: 'Shadowknight Falconer' (display form) -> auto-accept");
        C(SunriseAutoAcceptBlock(Sun("andyandroid Resident", 0, 0)) == null && SunriseAutoAcceptBlock(Sun("Andyandroid", 0, 0)) == null, "Sunrise: andyandroid / 'andyandroid Resident' -> auto-accept");
        C(SunriseAutoAcceptBlock(Sun("andyandroid", 10, 10))?.Contains("L$10") == true, "Sunrise: L$10 fee -> NOT auto-accepted (force/David)");
        C(SunriseAutoAcceptBlock(Sun("andyandroid", 0, 25))?.Contains("profile says join fee L$25") == true, "Sunrise: invite L$0 but profile L$25 -> NOT auto-accepted");
        C(SunriseAutoAcceptBlock(Sun("andyandroid", null, 0))?.Contains("unknown") == true, "Sunrise: unknown invite fee -> NOT auto-accepted");
        C(SunriseAutoAcceptBlock(Sun("andyandroid", 0, null))?.Contains("profile did not answer") == true, "Sunrise: no profile reply -> NOT auto-accepted");
        C(SunriseAutoAcceptBlock(Sun("Selftest Stranger", 0, 0))?.Contains("not shadowknight") == true, "Sunrise: other inviter -> NOT auto-accepted");
        C(SunriseAutoAcceptBlock(Sun("andyandroid", 0, 0, UUID.Random()))?.Contains("not the Sunrise") == true && !SunriseCandidate(Sun("andyandroid", 0, 0, UUID.Random())), "andyandroid inviting to another group -> NOT auto-accepted");
        var done = Sun("andyandroid", 0, 0); done.State = "declined";
        C(SunriseAutoAcceptBlock(done)?.StartsWith("already") == true, "Sunrise: already answered -> nothing");
        C(SunriseCandidate(Sun("andyandroid", 0, 0)) && GroupAcceptBlock(Sun("andyandroid", 0, 0), false, false)?.StartsWith("refused: inviter") == true, "manual 'group accept' rules unchanged for Sunrise inviters (David/Sophie list only)");
        C(GroupAcceptBlock(a, false, false) == null && GroupAcceptBlock(new GroupInvite { N = 9101, Inviter = "SophieJeanneLaDouce", Fee = 0 }, false, false) == null, "David / Sophie L$0 invites: still accept without confirm");
        C(GalatayMcp.Webhook.UrgentKinds.Contains("group_invite_accepted") && GalatayMcp.Webhook.UrgentKinds.Contains("group_invite"), "webhook: group_invite + group_invite_accepted are urgent");
        sb.AppendLine(OfferStoreSelfTest(ref pass, ref fail));
        return $"group invites selftest: {pass} PASS, {fail} FAIL (synthetic invites removed; nothing sent)\n" + sb.ToString().TrimEnd();
    }

    static async Task<(Dictionary<UUID, Group> groups, bool fresh)> FetchCurrentGroups(int timeoutMs = 10000)
    {
        var tcs = new TaskCompletionSource<Dictionary<UUID, Group>>(TaskCreationOptions.RunContinuationsAsynchronously);
        void H(object s, CurrentGroupsEventArgs e) => tcs.TrySetResult(new Dictionary<UUID, Group>(e.Groups));
        client.Groups.CurrentGroups += H;
        try
        {
            client.Groups.RequestCurrentGroups();
            if (await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)) == tcs.Task) return (tcs.Task.Result, true);
            return (currentGroups, false);
        }
        finally { client.Groups.CurrentGroups -= H; }
    }

    static string GroupLine(Group g) =>
        $"name='{g.Name}' id={g.ID} open_enrollment={g.OpenEnrollment} membership_fee=L${g.MembershipFee} members={g.GroupMembershipCount} " +
        $"show_in_list={g.ShowInList} mature={g.MaturePublish} founder={g.FounderID}";

    static async Task<string> GroupCmds(string[] a)
    {
        var sub = a.Length > 0 ? a[0].ToLowerInvariant() : "list";
        if (sub == "list")
        {
            var (groups, fresh) = await FetchCurrentGroups();
            var sb = new StringBuilder($"current groups ({groups.Count}; {(fresh ? "fresh server reply" : currentGroupsAt == DateTime.MinValue ? "no reply, no cache" : $"no reply, cached {currentGroupsAt:HH:mm:ss}")}):\n");
            foreach (var g in groups.Values.OrderBy(g => g.Name))
                sb.AppendLine($"  {g.ID} '{g.Name}' title='{g.MemberTitle}' accept_notices={g.AcceptNotices} list_in_profile={g.ListInProfile} contribution={g.Contribution}");
            sb.Append(ActiveGroupLine(groups));
            return sb.ToString();
        }
        if (sub == "activate") return await GroupActivateCmd(a);
        if (sub == "invites") return a.Length > 1 && a[1] == "selftest" ? GroupInvitesSelfTest() : GroupInvitesText(a.Length > 1 && a[1] == "all");
        if (sub == "accept" || sub == "decline") return await GroupInviteRespondCmd(a, sub == "accept");
        if ((sub == "info" || sub == "join") && a.Length >= 2 && UUID.TryParse(a[1], out var id))
        {
            var g = await FetchGroupProfile(id);
            if (g == null) return $"no group profile reply for {id} within 15 s" + (sub == "join" ? "; NOT joining" : "");
            var gp = g.Value;
            if (sub == "info") return GroupLine(gp) + $"\ncharter: {gp.Charter?.Replace("\n", " \\n ")}";
            // join
            var (cur, _) = await FetchCurrentGroups(8000);
            if (cur.ContainsKey(id)) return $"already a member of '{gp.Name}' ({id}); nothing sent";
            if (!gp.OpenEnrollment || gp.MembershipFee != 0)
            {
                Log("group", $"join REFUSED for '{gp.Name}' ({id}): open_enrollment={gp.OpenEnrollment} fee=L${gp.MembershipFee}");
                return $"REFUSED: '{gp.Name}' open_enrollment={gp.OpenEnrollment} membership_fee=L${gp.MembershipFee} (join only if open and L$0); nothing sent";
            }
            var bal0 = await BalanceAsync();
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void H(object s, GroupOperationEventArgs e) { if (e.GroupID == id) tcs.TrySetResult(e.Success); }
            client.Groups.GroupJoinedReply += H;
            bool? ok = null;
            try
            {
                Log("group", $"joining '{gp.Name}' ({id}) open_enrollment=True fee=L$0 balance_before={bal0?.ToString() ?? "?"}");
                client.Groups.RequestJoinGroup(id);
                if (await Task.WhenAny(tcs.Task, Task.Delay(20000)) == tcs.Task) ok = tcs.Task.Result;
            }
            finally { client.Groups.GroupJoinedReply -= H; }
            await Task.Delay(1500);
            var (after, fresh) = await FetchCurrentGroups();
            var bal1 = await BalanceAsync();
            var member = after.ContainsKey(id);
            Log("group", $"join '{gp.Name}' reply={(ok.HasValue ? (ok.Value ? "success" : "failure") : "none")} member_after={member} balance {bal0} -> {bal1}");
            return $"join '{gp.Name}' ({id}): JoinGroupReply={(ok.HasValue ? (ok.Value ? "SUCCESS" : "FAILURE") : "no reply within 20 s")}; " +
                   $"in current groups afterwards: {(member ? "yes" : "no")}{(fresh ? "" : " (group list not refreshed)")}; balance L${bal0?.ToString() ?? "?"} -> L${bal1?.ToString() ?? "?"}";
        }
        return "usage: group list | group activate <none|group name|uuid> | group info <group uuid> | group join <group uuid> | group invites [all|selftest] | group accept <n|group name> [confirm] [force] | group decline <n|group name>";
    }

    // ---- picks ----------------------------------------------------------
    static async Task<Dictionary<UUID, string>> FetchPicks(UUID avatar, int timeoutMs = 10000)
    {
        var tcs = new TaskCompletionSource<Dictionary<UUID, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        void H(object s, AvatarPicksReplyEventArgs e) { if (e.AvatarID == avatar) tcs.TrySetResult(new Dictionary<UUID, string>(e.Picks)); }
        client.Avatars.AvatarPicksReply += H;
        try
        {
            client.Avatars.RequestAvatarPicks(avatar);
            return await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)) == tcs.Task ? tcs.Task.Result : null;
        }
        finally { client.Avatars.AvatarPicksReply -= H; }
    }

    static async Task<ProfilePick?> FetchPickInfo(UUID pick, int timeoutMs = 10000)
    {
        var tcs = new TaskCompletionSource<ProfilePick>(TaskCreationOptions.RunContinuationsAsynchronously);
        void H(object s, PickInfoReplyEventArgs e) { if (e.PickID == pick) tcs.TrySetResult(e.Pick); }
        client.Avatars.PickInfoReply += H;
        try
        {
            client.Avatars.RequestPickInfo(client.Self.AgentID, pick);
            return await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)) == tcs.Task ? tcs.Task.Result : null;
        }
        finally { client.Avatars.PickInfoReply -= H; }
    }

    static string PickText(ProfilePick p) =>
        $"pick {p.PickID}: name='{p.Name}' sim={p.SimName} pos_global=<{p.PosGlobal.X:F1},{p.PosGlobal.Y:F1},{p.PosGlobal.Z:F1}> parcel={p.ParcelID} " +
        $"snapshot={p.SnapshotID} parcel_name='{p.User}' original_name='{p.OriginalName}' top={p.TopPick} enabled={p.Enabled} sort={p.SortOrder}\n" +
        $"  desc: {p.Desc?.Replace("\n", "\\n")}";

    record PickLoc(string Region, ulong Handle, UUID RegionId, Vector3 Local, Vector3d Global, UUID ParcelId, string ParcelName, UUID SnapshotId, string SimName, string Error);

    static async Task<PickLoc> PickLookup(string region, Vector3 local)
    {
        ulong handle; UUID regionId = UUID.Zero; string rname = region;
        var sim = client.Network.CurrentSim;
        if (sim != null && string.Equals(sim.Name, region, StringComparison.OrdinalIgnoreCase)) { handle = sim.Handle; regionId = sim.ID; rname = sim.Name; }
        else
        {
            var gr = await client.Grid.GetGridRegionAsync(region, GridLayerType.Objects);
            if (gr == null) return new PickLoc(region, 0, UUID.Zero, local, Vector3d.Zero, UUID.Zero, null, UUID.Zero, null, $"region '{region}' not found on the map");
            handle = gr.Value.RegionHandle; rname = gr.Value.Name;
        }
        Utils.LongToUInts(handle, out var gx, out var gy);
        var global = new Vector3d(gx + (double)local.X, gy + (double)local.Y, local.Z);
        UUID parcelId;
        try { parcelId = await client.Parcels.RequestRemoteParcelIDAsync(local, handle, regionId); }
        catch (Exception ex) { return new PickLoc(rname, handle, regionId, local, global, UUID.Zero, null, UUID.Zero, null, "RemoteParcelRequest failed: " + ex.GetBaseException().Message); }
        if (parcelId == UUID.Zero) return new PickLoc(rname, handle, regionId, local, global, UUID.Zero, null, UUID.Zero, null, "RemoteParcelRequest returned no parcel id");
        var tcs = new TaskCompletionSource<ParcelInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        void H(object s, ParcelInfoReplyEventArgs e) { if (e.Parcel.ID == parcelId) tcs.TrySetResult(e.Parcel); }
        client.Parcels.ParcelInfoReply += H;
        try
        {
            client.Parcels.RequestParcelInfo(parcelId);
            if (await Task.WhenAny(tcs.Task, Task.Delay(10000)) != tcs.Task)
                return new PickLoc(rname, handle, regionId, local, global, parcelId, null, UUID.Zero, null, "no ParcelInfoReply within 10 s");
            var pi = tcs.Task.Result;
            return new PickLoc(rname, handle, regionId, local, global, parcelId, pi.Name, pi.SnapshotID, pi.SimName, null);
        }
        finally { client.Parcels.ParcelInfoReply -= H; }
    }

    static string PickLocText(PickLoc l) =>
        $"region={l.Region} handle={l.Handle} local=<{l.Local.X:F1},{l.Local.Y:F1},{l.Local.Z:F1}> global=<{l.Global.X:F1},{l.Global.Y:F1},{l.Global.Z:F1}> " +
        $"parcel_id={l.ParcelId} parcel_name='{l.ParcelName}' snapshot_id={l.SnapshotId} sim_name={l.SimName}" + (l.Error != null ? $" ERROR: {l.Error}" : "");

    // "<region words> <x> <y> <z>"
    static bool ParseRegionXYZ(string s, out string region, out Vector3 v)
    {
        region = null; v = Vector3.Zero;
        var t = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (t.Length < 4 || !F(t[^3], out var x) || !F(t[^2], out var y) || !F(t[^1], out var z)) return false;
        if (x < 0 || x > 256 || y < 0 || y > 256) return false;
        region = string.Join(' ', t[..^3]); v = new Vector3(x, y, z); return true;
    }

    static async Task<string> PickCmds(string rest, string[] a)
    {
        var sub = a.Length > 0 ? a[0].ToLowerInvariant() : "list";
        var args = rest.Length > sub.Length ? rest[sub.Length..].Trim() : "";
        switch (sub)
        {
            case "list":
            {
                var picks = await FetchPicks(client.Self.AgentID);
                if (picks == null) return "no AvatarPicksReply within 10 s (the server may send none when there are no picks)";
                var sb = new StringBuilder($"own picks ({picks.Count}):");
                foreach (var kv in picks) sb.Append($"\n  {kv.Key} '{kv.Value}'");
                return sb.ToString();
            }
            case "info":
            {
                if (!UUID.TryParse(args, out var id)) return "usage: pick info <pick id>";
                var p = await FetchPickInfo(id);
                return p == null ? $"no PickInfoReply for {id} within 10 s" : PickText(p.Value);
            }
            case "lookup":
            {
                if (!ParseRegionXYZ(args, out var region, out var v)) return "usage: pick lookup <region> <x> <y> <z>";
                var l = await PickLookup(region, v);
                return "DRY RUN (nothing created): " + PickLocText(l);
            }
            case "create":
            {
                var parts = args.Split('|', 3);
                if (parts.Length != 3 || !ParseRegionXYZ(parts[0].Trim(), out var region, out var v) || parts[1].Trim().Length == 0)
                    return "usage: pick create <region> <x> <y> <z> | <name> | <description>   (literal \\n = line break)";
                var name = parts[1].Trim(); var desc = parts[2].Trim().Replace("\\n", "\n");
                var l = await PickLookup(region, v);
                if (l.Error != null) return "NOT created: " + PickLocText(l);
                var pickId = UUID.Random();
                Log("pick", $"creating pick {pickId} '{name}' at {l.Region} {Fmt(v)} parcel {l.ParcelId} snapshot {l.SnapshotId}");
                client.Self.PickInfoUpdate(pickId, false, l.ParcelId, name, l.Global, l.SnapshotId, desc);
                await Task.Delay(2500);
                var picks = await FetchPicks(client.Self.AgentID);
                var info = await FetchPickInfo(pickId);
                return $"PickInfoUpdate sent: {PickLocText(l)}\nin pick list: {(picks == null ? "no list reply" : picks.ContainsKey(pickId) ? "yes" : "NO")}" +
                       $"\n{(info == null ? $"no PickInfoReply for {pickId}" : PickText(info.Value))}";
            }
            case "delete":
            {
                if (!UUID.TryParse(args, out var id)) return "usage: pick delete <pick id>";
                var before = await FetchPicks(client.Self.AgentID);
                if (before != null && !before.ContainsKey(id)) return $"pick {id} is not in my pick list; nothing sent";
                Log("pick", $"deleting pick {id} '{before?.GetValueOrDefault(id)}'");
                client.Self.PickDelete(id);
                await Task.Delay(2000);
                var after = await FetchPicks(client.Self.AgentID);
                return $"PickDelete sent for {id}; still in list afterwards: {(after == null ? "unknown (no reply; may be empty now)" : after.ContainsKey(id) ? "YES" : "no")}";
            }
        }
        return "usage: pick list | pick info <id> | pick lookup <region> <x> <y> <z> | pick create <region> <x> <y> <z> | <name> | <description> | pick delete <id>";
    }
}
