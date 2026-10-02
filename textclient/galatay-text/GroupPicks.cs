// GroupPicks (added 2026-09-25): group info/join/list and profile picks.
// - group info <uuid>: GroupProfileRequest -> name, open enrollment, membership fee, member count
// - group join <uuid>: profile first; refuses unless OpenEnrollment && MembershipFee == 0; JoinGroupRequest -> JoinGroupReply
// - group list: AgentDataUpdateRequest -> AgentGroupDataUpdate (current groups); falls back to the cached last update
// - pick list | pick info <id> | pick lookup <region> <x> <y> <z> (dry run) | pick create <region> <x> <y> <z> | <name> | <desc> | pick delete <id>
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
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
            sb.Append($"active group: {(client.Self.ActiveGroup == UUID.Zero ? "none" : client.Self.ActiveGroup.ToString())}");
            return sb.ToString();
        }
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
        return "usage: group list | group info <group uuid> | group join <group uuid>";
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
