// OfferStore.cs (2026-10-03, David): pending group invites and pending inventory/friendship offers survive a client
// restart / relog. They are written to run/pending-offers.json (GT_OFFER_STORE) on every change (pending ones only,
// at most GT_OFFER_STORE_DAYS = 14 days old; selftest entries never) and loaded at startup, before login, so an offline
// re-delivery at login is matched to the restored entry instead of being counted twice.
// ~45 s after login (offline IMs have arrived by then) every restored, still-pending group invite is re-surfaced:
//   - already a member of that group -> marked accepted, no webhook
//   - Sunrise Suites rule (GroupPicks.cs) -> auto-accept check as for a live invite
//   - otherwise the urgent 'group_invite' webhook is sent again, noting it was restored.
// Caveat, recorded in the webhook text and in 'group invites': SL's group-invite answer carries the invite's IM session
// id; whether the grid still accepts a session from before the relog is not guaranteed. If SL re-delivers the invite at
// login (offline invite) the session is known to be current; otherwise an accept that does not produce membership means
// the invite is gone and the inviter has to re-send. Restored friendship offers are answered via the friendship
// capability (as offline ones are); restored inventory offers may likewise be stale.
using System.Text.Json;
using System.Text.Json.Serialization;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    static readonly string OfferStoreFile = Env("GT_OFFER_STORE", "/home/box/viewers/textclient/run/pending-offers.json");
    static readonly TimeSpan OfferStoreMaxAge = TimeSpan.FromDays(double.TryParse(Env("GT_OFFER_STORE_DAYS", "14"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var osd) && osd > 0 ? osd : 14);
    static readonly object offerStoreLock = new();
    static string offerStoreLast = "-";

    class StoredInvite
    {
        public int n { get; set; } public DateTime at { get; set; } public string group_id { get; set; } public string group_name { get; set; }
        public string from_name { get; set; } public string inviter { get; set; } public string session { get; set; } public string role_id { get; set; }
        public int? fee { get; set; } public int? profile_fee { get; set; } public string message { get; set; } public bool offline { get; set; }
    }
    class StoredOffer
    {
        public int n { get; set; } public DateTime at { get; set; } public string kind { get; set; } public string from { get; set; } public string from_name { get; set; }
        public string session { get; set; } public int type { get; set; } public string obj_id { get; set; } public string item_name { get; set; }
        public bool offline { get; set; } public bool from_task { get; set; }
    }
    class OfferStoreDoc
    {
        public int version { get; set; } = 1; public DateTime saved { get; set; }
        public List<StoredInvite> group_invites { get; set; } = new(); public List<StoredOffer> offers { get; set; } = new();
    }
    static readonly JsonSerializerOptions OfferStoreJson = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    // pure (selftest-covered)
    static string SerializeOfferStore(IEnumerable<GroupInvite> invites, IEnumerable<PendingOffer> offers, DateTime now)
    {
        var d = new OfferStoreDoc { saved = now };
        foreach (var g in invites.Where(x => x.State == "pending" && !x.Synthetic && now - x.At <= OfferStoreMaxAge))
            d.group_invites.Add(new StoredInvite { n = g.N, at = g.At, group_id = g.GroupId.ToString(), group_name = g.GroupName, from_name = g.FromName, inviter = g.Inviter,
                session = g.Session.ToString(), role_id = g.RoleId.ToString(), fee = g.Fee, profile_fee = g.ProfileFee, message = g.Message, offline = g.Offline });
        foreach (var o in offers.Where(x => x.State == "pending" && !x.Synthetic && now - x.At <= OfferStoreMaxAge))
            d.offers.Add(new StoredOffer { n = o.N, at = o.At, kind = o.Kind, from = o.From.ToString(), from_name = o.FromName, session = o.Session.ToString(), type = (int)o.Type,
                obj_id = o.ObjId.ToString(), item_name = o.ItemName, offline = o.Offline, from_task = o.FromTask });
        return JsonSerializer.Serialize(d, OfferStoreJson);
    }

    static (List<GroupInvite> invites, List<PendingOffer> offers) ParseOfferStore(string json, DateTime now)
    {
        var d = JsonSerializer.Deserialize<OfferStoreDoc>(json) ?? new OfferStoreDoc();
        static UUID U(string s) => UUID.TryParse(s ?? "", out var u) ? u : UUID.Zero;
        var gi = (d.group_invites ?? new()).Where(x => now - x.at <= OfferStoreMaxAge && U(x.group_id) != UUID.Zero).Select(x => new GroupInvite
        {
            N = x.n, At = x.at, GroupId = U(x.group_id), GroupName = x.group_name, FromName = x.from_name, Inviter = x.inviter, Session = U(x.session), RoleId = U(x.role_id),
            Fee = x.fee, ProfileFee = x.profile_fee, Message = x.message ?? "", Offline = x.offline, Restored = true
        }).ToList();
        var po = (d.offers ?? new()).Where(x => now - x.at <= OfferStoreMaxAge && x.kind is "friendship" or "inventory").Select(x => new PendingOffer
        {   // a restored friendship offer is answered like an offline one (friendship capability, no IM session needed)
            N = x.n, At = x.at, Kind = x.kind, From = U(x.from), FromName = x.from_name, Session = U(x.session), Type = (AssetType)x.type, ObjId = U(x.obj_id),
            ItemName = x.item_name, Offline = x.offline || x.kind == "friendship", FromTask = x.from_task, Restored = true
        }).ToList();
        return (gi, po);
    }

    static void SaveOfferStore()
    {
        try
        {
            string json;
            lock (groupInvites) lock (pendingOffers) json = SerializeOfferStore(groupInvites.ToList(), pendingOffers.ToList(), DateTime.Now);
            lock (offerStoreLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(OfferStoreFile)!);
                var tmp = OfferStoreFile + ".tmp";
                File.WriteAllText(tmp, json); File.Move(tmp, OfferStoreFile, true);
            }
        }
        catch (Exception ex) { Log("offer", "offer store save failed: " + ex.GetBaseException().Message); }
    }

    // startup, before login
    static void LoadOfferStore()
    {
        try
        {
            if (!File.Exists(OfferStoreFile)) { offerStoreLast = "no store file"; return; }
            var (gi, po) = ParseOfferStore(File.ReadAllText(OfferStoreFile), DateTime.Now);
            lock (groupInvites) { foreach (var g in gi) if (!groupInvites.Any(x => x.Session == g.Session && x.GroupId == g.GroupId)) groupInvites.Add(g); groupInviteSeq = Math.Max(groupInviteSeq, groupInvites.Select(x => x.N).DefaultIfEmpty(0).Max()); }
            lock (pendingOffers) { foreach (var o in po) if (!pendingOffers.Any(x => x.Session == o.Session && x.Kind == o.Kind)) pendingOffers.Add(o); offerSeq = Math.Max(offerSeq, pendingOffers.Select(x => x.N).DefaultIfEmpty(0).Max()); }
            offerStoreLast = $"{DateTime.Now:HH:mm:ss} restored {gi.Count} group invite(s), {po.Count} offer(s) from {OfferStoreFile}";
            Log("offer", offerStoreLast);
            foreach (var g in gi) Log("offer", $"restored group invite {GroupInviteLine(g)}");
            foreach (var o in po) Log("offer", $"restored offer {OfferLine(o)}");
        }
        catch (Exception ex) { offerStoreLast = "load failed: " + ex.GetBaseException().Message; Log("offer", "offer store " + offerStoreLast); }
    }

    // after every login: re-surface restored, still-pending group invites once per process
    static async Task ResurfaceRestoredOffers()
    {
        try
        {
            List<GroupInvite> todo; lock (groupInvites) todo = groupInvites.Where(x => x.Restored && !x.Resurfaced && x.State == "pending").ToList();
            int offers; lock (pendingOffers) offers = pendingOffers.Count(x => x.Restored && x.State == "pending");
            if (todo.Count == 0 && offers == 0) return;
            await Task.Delay(45000); // let the offline IMs (possible re-deliveries) arrive first
            if (!LoggedIn) return;
            var (groups, fresh) = await FetchCurrentGroups();
            foreach (var g in todo)
            {
                if (g.State != "pending") continue;
                g.Resurfaced = true;
                if (fresh && groups.ContainsKey(g.GroupId))
                {
                    g.State = "accepted (already a member)"; Log("offer", $"restored group invite #{g.N} '{g.GroupName}': already a member, nothing to do");
                    continue;
                }
                Log("offer", $"re-surfacing restored group invite {GroupInviteLine(g)}");
                await ProcessGroupInvite(g, true);
            }
            if (offers > 0) Log("offer", $"{offers} restored pending inventory/friendship offer(s) listed in 'offers' (marked [restored]; inventory ones may be stale after the relog)");
            SaveOfferStore();
        }
        catch (Exception ex) { Log("offer", "re-surface failed: " + ex.GetBaseException().Message); }
    }

    static string OfferStoreSelfTest(ref int pass, ref int fail)
    {
        var sb = new System.Text.StringBuilder(); int p = 0, f = 0; void C(bool ok, string w) { if (ok) p++; else f++; sb.AppendLine($"{(ok ? "PASS" : "FAIL")} {w}"); }
        var now = DateTime.Now; UUID grp = UUID.Random(), sess = UUID.Random(), role = UUID.Random(), from = UUID.Random(), item = UUID.Random();
        var gi = new List<GroupInvite>
        {
            new() { N = 7, At = now.AddHours(-2), GroupId = grp, GroupName = "Store Test", FromName = "Store Test", Inviter = "andyandroid", Session = sess, RoleId = role, Fee = 0, ProfileFee = 0, Message = "andyandroid has invited you to join Store Test." },
            new() { N = 8, At = now, GroupId = UUID.Random(), Session = UUID.Random(), State = "declined" },          // answered: dropped
            new() { N = 9, At = now, GroupId = UUID.Random(), Session = UUID.Random(), Synthetic = true },            // selftest: dropped
            new() { N = 10, At = now.AddDays(-30), GroupId = UUID.Random(), Session = UUID.Random() },                // too old: dropped
        };
        var po = new List<PendingOffer>
        {
            new() { N = 3, At = now, Kind = "friendship", From = from, FromName = "Store Friend", Session = UUID.Random(), ItemName = "hi" },
            new() { N = 4, At = now, Kind = "inventory", From = from, FromName = "Store Friend", Session = UUID.Random(), Type = AssetType.Object, ObjId = item, ItemName = "Box" },
            new() { N = 5, At = now, Kind = "inventory", From = from, FromName = "Store Friend", Session = UUID.Random(), State = "accepted" },
        };
        var json = SerializeOfferStore(gi, po, now);
        var (g2, o2) = ParseOfferStore(json, now.AddMinutes(5));
        C(g2.Count == 1 && o2.Count == 2, $"store keeps only pending, recent, non-selftest entries ({g2.Count} invite, {o2.Count} offers)");
        var r = g2.FirstOrDefault();
        C(r != null && r.N == 7 && r.GroupId == grp && r.Session == sess && r.RoleId == role && r.Fee == 0 && r.ProfileFee == 0 && r.Inviter == "andyandroid" && r.Restored && r.State == "pending",
          "group invite round-trips (n, group, session, role, fees, inviter) and is flagged restored + pending");
        C(r != null && GroupInviteLine(r).Contains("restored: session may be stale") && RestoredInviteNote(r).Contains("ask the inviter to re-send"), "restored invite is labelled 'session may be stale' with the re-send advice");
        if (r != null) r.Redelivered = true;
        C(r != null && RestoredInviteNote(r).Contains("session is current"), "re-delivered at login -> session current");
        var fo = o2.FirstOrDefault(x => x.Kind == "friendship"); var io = o2.FirstOrDefault(x => x.Kind == "inventory");
        C(fo != null && fo.Offline && fo.Restored && io != null && io.ObjId == item && io.Type == AssetType.Object && !io.Offline, "offers round-trip; restored friendship answered via the capability (offline path)");
        var (g3, o3) = ParseOfferStore("{\"version\":1}", now);
        C(g3.Count == 0 && o3.Count == 0, "empty store document -> nothing restored");
        pass += p; fail += f;
        return $"offer store: {p} PASS, {f} FAIL (temp data only; {OfferStoreFile} untouched)\n" + sb.ToString().TrimEnd();
    }
}
