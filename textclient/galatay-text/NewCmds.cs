// NewCmds.cs (2026-10-02, David: "build out your text client" so Firestorm is rarely needed)
// offers [list] | offers accept <n> [confirm] | offers decline <n>   pending inventory offers + friendship requests (never auto-accepted)
// friend list | friend accept <name> [confirm] | friend decline <name> | friend add <name> [confirm]
//     allow-list (GT_LURE_ALLOW: David Nightingale, SophieJeanneLaDouce) only; anyone else needs 'confirm' = David's explicit OK
// landmark create <name> | landmark list | landmark tp <name|item uuid> [pos] [force]
// sethome                                       set home to the current spot (SetStartLocationRequest); reports the server's AlertMessage reply
// worn links <attachment>                       every prim of a worn attachment/HUD: link no., local id, name, description, faces
// touch-attachment <attachment> <link no.|prim name|local:<id>> [face] [st=u,v]   touch one prim/face of a worn attachment/HUD
// shape get [filter] | shape set <param> <0-100>   read / change one slider of the worn shape (ONLY 'Galatea Petite shape - Jani short neck')
using System.Globalization;
using System.Text;
using LibreMetaverse;
using LibreMetaverse.Assets;
using LibreMetaverse.Packets;
using LibreMetaverse.StructuredData;

namespace GalatayText;

public static partial class Program
{
    const string AllowedShapeName = "Galatea Petite shape - Jani short neck";
    static readonly string ShapeBackupDir = "/home/box/viewers/textclient/shape-backups";
    static readonly CultureInfo IC2 = CultureInfo.InvariantCulture;

    class PendingOffer
    {
        public int N; public DateTime At; public string Kind; public UUID From; public string FromName; public UUID Session;
        public AssetType Type; public UUID ObjId; public string ItemName; public bool Offline; public bool FromTask; public Simulator Sim;
        public string State = "pending";
        public bool Restored, Synthetic; // loaded from the offer store after a restart (OfferStore.cs); selftest entry (never persisted)
    }
    static readonly List<PendingOffer> pendingOffers = new(); static int offerSeq;

    // called from HandleIm for FriendshipOffered / Inventory(Task)Offered that were not accepted by 'offer allow'
    static void RecordOffer(InstantMessage im, bool offline) => RecordOffer(im, offline, true);
    static void RecordOffer(InstantMessage im, bool offline, bool notify)
    {
        try
        {
            var o = new PendingOffer { At = DateTime.Now, From = im.FromAgentID, FromName = im.FromAgentName, Session = im.IMSessionID, Offline = offline, Sim = client.Network.CurrentSim };
            if (im.Dialog == InstantMessageDialog.FriendshipOffered) { o.Kind = "friendship"; o.ItemName = im.Message; }
            else
            {
                o.Kind = "inventory"; o.FromTask = im.Dialog == InstantMessageDialog.TaskInventoryOffered; o.ItemName = im.Message;
                var b = im.BinaryBucket ?? Array.Empty<byte>();
                if (b.Length >= 1) o.Type = (AssetType)b[0];
                if (!o.FromTask && b.Length == 17) o.ObjId = new UUID(b, 1);
            }
            lock (pendingOffers)
            {
                if (pendingOffers.Any(p => p.Session == o.Session && p.Kind == o.Kind && p.State == "pending")) return; // duplicate (offline re-delivery)
                o.N = ++offerSeq; o.Synthetic = !notify; pendingOffers.Add(o);
                while (pendingOffers.Count > 100) pendingOffers.RemoveAt(0);
            }
            if (notify && o.Kind == "friendship" && !offline) Notify("friendship_offer", o.FromName, o.From, $"friendship offer #{o.N} from {o.FromName}: '{o.ItemName}' (pending; never auto-accepted)", null); // urgent webhook
            if (notify) SaveOfferStore(); // survives a restart (OfferStore.cs)
            Log("offer", $"recorded as offer #{o.N}: {o.Kind} from {o.FromName} ({o.From}) '{o.ItemName}' type {o.Type}{(offline ? " [offline]" : "")} - pending; never auto-accepted ('offers accept {o.N}' / 'offers decline {o.N}')");
        }
        catch (Exception ex) { Log("offer", "record failed: " + ex.GetBaseException().Message); }
    }

    static bool OfferSenderAllowed(PendingOffer o) =>
        LureAllowed(o.FromName, o.From) || (o.FromTask && o.From == client.Self.AgentID);

    static string OfferLine(PendingOffer o) =>
        $"#{o.N} {o.At:MM-dd HH:mm} {o.Kind,-10} from '{o.FromName}' ({o.From}){(o.FromTask ? " [object]" : "")}{(o.Offline ? " [offline]" : "")}{(o.Restored ? " [restored]" : "")}: " +
        (o.Kind == "friendship" ? $"message '{o.ItemName}'" : $"'{o.ItemName}' type {o.Type}") +
        $" [{o.State}]{(OfferSenderAllowed(o) ? " (allow-listed)" : " (NOT allow-listed: accept needs 'confirm' = David's OK)")}";

    static async Task<string> OffersCmd(string[] a)
    {
        if (a.Length == 0 || a[0] == "list" || a[0] == "all")
        {
            bool all = a.Length > 0 && a[0] == "all";
            List<PendingOffer> l; lock (pendingOffers) l = pendingOffers.Where(o => all || o.State == "pending").ToList();
            var fr = client.Friends.FriendRequests?.Keys.ToList() ?? new();
            var sb = new StringBuilder($"{l.Count} {(all ? "offers this session" : "pending offers")} (inventory + friendship; kept across restarts in run/pending-offers.json, incl. offline ones fetched at login)\n");
            foreach (var o in l) sb.AppendLine("  " + OfferLine(o));
            if (fr.Count > 0) sb.AppendLine($"  library friend-request table: {string.Join(", ", fr)}");
            sb.AppendLine(GroupInvitesText(all).Replace("\n  ", "\n    ").Insert(0, "  ")); // group invitations (GroupPicks.cs): 'group accept|decline <n>'
            return sb.ToString().TrimEnd();
        }
        if (a[0] == "selftest") return await OffersSelfTest();
        if ((a[0] == "accept" || a[0] == "decline") && a.Length >= 2 && int.TryParse(a[1], out var n))
        {
            PendingOffer o; lock (pendingOffers) o = pendingOffers.FirstOrDefault(x => x.N == n);
            if (o == null) return $"no offer #{n}";
            if (o.State != "pending") return $"offer #{n} is already {o.State}";
            bool confirm = a.Skip(2).Any(x => x.Equals("confirm", StringComparison.OrdinalIgnoreCase));
            return await RespondOffer(o, a[0] == "accept", confirm);
        }
        return "usage: offers [list|all] | offers accept <n> [confirm] | offers decline <n>";
    }

    // offline logic test: synthetic offers are recorded (no webhook), listed, refused without 'confirm', then removed. Nothing is sent to SL.
    static async Task<string> OffersSelfTest()
    {
        var sb = new StringBuilder(); int pass = 0, fail = 0; void C(bool ok, string w) { if (ok) pass++; else fail++; sb.AppendLine($"{(ok ? "PASS" : "FAIL")} {w}"); }
        var stranger = UUID.Random(); var david = new UUID("44ce5a36-c1c7-4a68-ac9a-635ddfff6233"); var itemId = UUID.Random();
        var bucket = new byte[17]; bucket[0] = (byte)AssetType.Object; Buffer.BlockCopy(itemId.GetBytes(), 0, bucket, 1, 16);
        var s1 = UUID.Random(); var s2 = UUID.Random(); var s3 = UUID.Random();
        InstantMessage Im(InstantMessageDialog d, UUID from, string name, UUID sess, string msg, byte[] b) => new InstantMessage { Dialog = d, FromAgentID = from, FromAgentName = name, IMSessionID = sess, Message = msg, BinaryBucket = b ?? Array.Empty<byte>() };
        int before; lock (pendingOffers) before = pendingOffers.Count;
        RecordOffer(Im(InstantMessageDialog.InventoryOffered, stranger, "Selftest Stranger", s1, "Free Gift Box", bucket), false, false);
        RecordOffer(Im(InstantMessageDialog.InventoryOffered, stranger, "Selftest Stranger", s1, "Free Gift Box", bucket), false, false); // duplicate
        RecordOffer(Im(InstantMessageDialog.FriendshipOffered, stranger, "Selftest Stranger", s2, "be my friend", null), false, false);
        RecordOffer(Im(InstantMessageDialog.InventoryOffered, david, "David Nightingale", s3, "Test Shirt", bucket), false, false);
        List<PendingOffer> mine; lock (pendingOffers) mine = pendingOffers.Where(o => o.Session == s1 || o.Session == s2 || o.Session == s3).ToList();
        C(mine.Count == 3, $"3 synthetic offers recorded, duplicate dropped ({mine.Count})");
        var inv = mine.FirstOrDefault(o => o.Session == s1);
        C(inv != null && inv.Type == AssetType.Object && inv.ObjId == itemId && inv.ItemName == "Free Gift Box", "inventory offer parsed: type Object, item id, name");
        C(mine.Any(o => o.Kind == "friendship" && o.FromName == "Selftest Stranger"), "friendship offer recorded");
        var r1 = await RespondOffer(inv, true, false);
        C(r1.StartsWith("refused") && inv.State == "pending", "stranger's inventory offer: accept refused without confirm");
        var fr = mine.First(o => o.Kind == "friendship");
        var r2 = await FriendCmd(new[] { "accept", "Selftest", "Stranger" });
        C(r2.StartsWith("refused") && fr.State == "pending", "stranger's friendship: 'friend accept' refused without confirm");
        C(OfferSenderAllowed(mine.First(o => o.Session == s3)), "David's offer counts as allow-listed");
        C(OffersCmd(Array.Empty<string>()).Result.Contains("Selftest Stranger"), "'offers' lists them");
        lock (pendingOffers) pendingOffers.RemoveAll(o => o.Session == s1 || o.Session == s2 || o.Session == s3);
        return $"offers selftest: {pass} PASS, {fail} FAIL (synthetic offers removed; nothing sent)\n" + sb.ToString().TrimEnd();
    }

    static async Task<string> RespondOffer(PendingOffer o, bool accept, bool confirm)
    {
        if (accept && !OfferSenderAllowed(o) && !confirm)
        {
            Log("offer", $"accept #{o.N} REFUSED: {o.FromName} is not allow-listed and no 'confirm'");
            return $"refused: '{o.FromName}' is not on the allow-list (David Nightingale, Sophie-Jeanne). Only with David's explicit OK: 'offers accept {o.N} confirm'";
        }
        if (o.Kind == "friendship")
        {
            if (accept)
            {
                if (o.Offline) await client.Friends.AcceptFriendshipViaCapAsync(o.From);
                else client.Friends.AcceptFriendship(o.From, o.Session);
            }
            else
            {
                if (o.Offline) { try { await client.Friends.DeclineFriendshipViaCapAsync(o.From); } catch { client.Friends.DeclineFriendship(o.From, o.Session); } }
                else client.Friends.DeclineFriendship(o.From, o.Session);
            }
        }
        else
        {
            var folder = o.Type == AssetType.Folder ? client.Inventory.Store.RootFolder.UUID : client.Inventory.FindFolderForType(o.Type);
            if (folder == UUID.Zero) folder = client.Inventory.Store.RootFolder.UUID;
            var imp = new ImprovedInstantMessagePacket();
            imp.AgentData.AgentID = client.Self.AgentID; imp.AgentData.SessionID = client.Self.SessionID;
            imp.MessageBlock.FromGroup = false; imp.MessageBlock.ToAgentID = o.From; imp.MessageBlock.Offline = 0;
            imp.MessageBlock.ID = o.Session; imp.MessageBlock.Timestamp = 0; imp.MessageBlock.FromAgentName = Utils.StringToBytes(client.Self.Name);
            imp.MessageBlock.Message = Utils.EmptyBytes; imp.MessageBlock.ParentEstateID = 0; imp.MessageBlock.RegionID = UUID.Zero;
            imp.MessageBlock.Position = client.Self.SimPosition;
            imp.MessageBlock.Dialog = (byte)(accept ? (o.FromTask ? InstantMessageDialog.TaskInventoryAccepted : InstantMessageDialog.InventoryAccepted)
                                                    : (o.FromTask ? InstantMessageDialog.TaskInventoryDeclined : InstantMessageDialog.InventoryDeclined));
            imp.MessageBlock.BinaryBucket = accept ? folder.GetBytes() : Utils.EmptyBytes;
            client.Network.SendPacket(imp, client.Network.CurrentSim);
            if (accept && o.ObjId != UUID.Zero) client.Inventory.RequestFetchInventory(o.ObjId, client.Self.AgentID);
        }
        o.State = accept ? "accepted" : "declined";
        if (!o.Synthetic) SaveOfferStore();
        Log("offer", $"offer #{o.N} {o.State}: {o.Kind} from {o.FromName} '{o.ItemName}'{(confirm ? " (confirm = David's OK)" : "")}");
        return $"{o.State} offer #{o.N}: {o.Kind} from '{o.FromName}' ('{o.ItemName}')";
    }

    static async Task<string> FriendCmd(string[] a)
    {
        if (a.Length == 0 || a[0] == "list")
        {
            var fl = client.Friends.FriendList.Values.ToList();
            var sb = new StringBuilder($"{fl.Count} friends\n");
            foreach (var f in fl.OrderByDescending(f => f.IsOnline).ThenBy(f => f.Name))
                sb.AppendLine($"  {(f.IsOnline ? "online " : "offline")} '{(string.IsNullOrEmpty(f.Name) ? "?" : f.Name)}' {f.UUID}");
            return sb.ToString().TrimEnd();
        }
        bool confirm = a.Any(x => x.Equals("confirm", StringComparison.OrdinalIgnoreCase));
        var who = string.Join(' ', a.Skip(1).Where(x => !x.Equals("confirm", StringComparison.OrdinalIgnoreCase))).Trim().Trim('"');
        if (who.Length == 0) return "usage: friend list | friend accept|decline|add <name> [confirm]";
        bool NameMatch(string n) => n != null && (n.Equals(who, StringComparison.OrdinalIgnoreCase) || n.Equals(who + " Resident", StringComparison.OrdinalIgnoreCase) || n.Replace(' ', '.').Equals(who, StringComparison.OrdinalIgnoreCase));
        switch (a[0])
        {
            case "accept": case "decline":
            {
                PendingOffer o; lock (pendingOffers) o = pendingOffers.LastOrDefault(x => x.Kind == "friendship" && x.State == "pending" && (NameMatch(x.FromName) || x.From.ToString() == who));
                if (o == null) return $"no pending friendship offer from '{who}' (see 'offers')";
                return await RespondOffer(o, a[0] == "accept", confirm);
            }
            case "add":
            {
                var id = await ResolveAvatar(who);
                if (id == UUID.Zero) return $"could not resolve '{who}'";
                if (client.Friends.FriendList.ContainsKey(id)) return $"'{who}' ({id}) is already a friend; nothing sent";
                PendingOffer po; lock (pendingOffers) po = pendingOffers.LastOrDefault(x => x.Kind == "friendship" && x.State == "pending" && x.From == id);
                if (po != null) return $"'{who}' already offered friendship (offer #{po.N}); use 'friend accept {who}'";
                if (!LureAllowed(who, id) && !LureAllowed(who + " Resident", id) && !confirm)
                    return $"refused: '{who}' is not on the allow-list; only with David's explicit OK: 'friend add {who} confirm'";
                client.Friends.OfferFriendship(id);
                Log("friend", $"friendship offer sent to {who} ({id}){(confirm ? " (confirm = David's OK)" : "")}");
                return $"friendship offer sent to '{who}' ({id})";
            }
        }
        return "usage: friend list | friend accept|decline|add <name> [confirm]";
    }

    // ---------------- landmarks ----------------
    static async Task<List<InventoryItem>> LandmarkItems(CancellationToken ct)
    {
        var res = new List<InventoryItem>();
        var root = client.Inventory.FindFolderForType(AssetType.Landmark);
        if (root == UUID.Zero) return res;
        var queue = new Queue<UUID>(); queue.Enqueue(root); int guard = 0;
        while (queue.Count > 0 && guard++ < 50)
        {
            var f = queue.Dequeue();
            foreach (var k in (await ReadFolderRO(f, ct)).Where(k => k.ParentUUID == f))
                if (k is InventoryFolder sub) queue.Enqueue(sub.UUID);
                else if (k is InventoryItem it && (it.InventoryType == InventoryType.Landmark || it.AssetType == AssetType.Landmark)) res.Add(it);
        }
        return res;
    }

    // 2026-10-02: right after a teleport/region change the new sim's seed capabilities are not there yet; LibreMetaverse's
    // folder fetch then silently returns NOTHING ("Failed to obtain FetchInventoryDescendents2 capability"), so 'landmark list'
    // showed an empty Landmarks folder. Wait (max 15 s) for the cap; null = ok, else an error text instead of a false "0 landmarks".
    static async Task<string> WaitInventoryCap(CancellationToken ct)
    {
        for (int i = 0; i < 30; i++)
        {
            if (client.Network.CurrentSim?.Caps?.CapabilityURI("FetchInventoryDescendents2") != null) return null;
            await Task.Delay(500, ct);
        }
        return $"inventory not reachable yet: region {client.Network.CurrentSim?.Name ?? "?"} has no FetchInventoryDescendents2 capability (still loading after a teleport?); try again in a few seconds";
    }

    static async Task<AssetLandmark> LandmarkAsset(InventoryItem it, CancellationToken ct)
    {
        try
        {
            if (it.IsLink()) it = await FetchItemRO(it.AssetUUID, ct) ?? it;
            using var t = CancellationTokenSource.CreateLinkedTokenSource(ct); t.CancelAfter(20000);
            var asset = await client.Assets.RequestInventoryAssetAsync(it, true, UUID.Random(), t.Token);
            if (asset is AssetLandmark lm) { lm.Decode(); return lm; }
            if (asset != null) { var l2 = new AssetLandmark(asset.AssetID, asset.AssetData); l2.Decode(); return l2; }
        }
        catch { }
        return null;
    }

    static readonly Dictionary<UUID, (ulong handle, string name)> regionNames = new();
    static async Task<(ulong handle, string name)> RegionById(UUID rid)
    {
        if (rid == UUID.Zero) return (0, "?");
        var sim = client.Network.CurrentSim;
        if (sim != null && sim.RegionID == rid) return (sim.Handle, sim.Name);
        lock (regionNames) if (regionNames.TryGetValue(rid, out var c)) return c;
        var tcs = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
        void H(object s, RegionHandleReplyEventArgs e) { if (e.RegionID == rid) tcs.TrySetResult(e.RegionHandle); }
        client.Grid.RegionHandleReply += H;
        try { client.Grid.RequestRegionHandle(rid); await Task.WhenAny(tcs.Task, Task.Delay(6000)); }
        finally { client.Grid.RegionHandleReply -= H; }
        if (!tcs.Task.IsCompletedSuccessfully) return (0, rid.ToString());
        ulong h = tcs.Task.Result; string name = rid.ToString();
        try { using var t = new CancellationTokenSource(8000); var gr = await client.Grid.GetGridRegionAsync(h, GridLayerType.Objects, t.Token); if (gr != null && !string.IsNullOrEmpty(gr.Value.Name)) name = gr.Value.Name; } catch { }
        lock (regionNames) regionNames[rid] = (h, name);
        return (h, name);
    }

    static async Task<string> LandmarkCmd(string[] a, string rest)
    {
        using var cts = new CancellationTokenSource(120000); var ct = cts.Token;
        string sub = a.Length > 0 ? a[0].ToLowerInvariant() : "list";
        if (sub is "list" or "tp" or "teleport" && await WaitInventoryCap(ct) is string capErr) return capErr;
        if (sub == "list")
        {
            var items = await LandmarkItems(ct);
            var sb = new StringBuilder($"{items.Count} landmarks in the Landmarks folder\n");
            foreach (var it in items.OrderBy(i => i.Name))
            {
                var lm = await LandmarkAsset(it, ct);
                string where = lm == null ? "(asset not readable)" : $"{(await RegionById(lm.RegionID)).name} {P3(lm.Position)}";
                sb.AppendLine($"  '{it.Name}' item {it.UUID} -> {where}{(string.IsNullOrEmpty(it.Description) ? "" : $" desc '{it.Description}'")} created {it.CreationDate.ToLocalTime():yyyy-MM-dd HH:mm} PT");
            }
            return sb.ToString().TrimEnd();
        }
        if (sub == "create")
        {
            var name = rest.Length > 6 ? rest[6..].Trim().Trim('"') : "";
            if (name.Length == 0) return "usage: landmark create <name>";
            var folder = client.Inventory.FindFolderForType(AssetType.Landmark);
            if (folder == UUID.Zero) return "Landmarks folder not found";
            var pos = client.Self.SimPosition; var region = client.Network.CurrentSim?.Name;
            var desc = $"{region} ({pos.X:F0}, {pos.Y:F0}, {pos.Z:F0})";
            InventoryItem made = null; string err = "";
            try { using var t = CancellationTokenSource.CreateLinkedTokenSource(ct); t.CancelAfter(20000);
                  made = await client.Inventory.CreateItemAsync(folder, name, desc, AssetType.Landmark, UUID.Zero, InventoryType.Landmark, PermissionMask.All, t.Token); }
            catch (Exception ex) { err = ex.GetBaseException().Message; }
            if (made == null) { Log("landmark", $"create '{name}' at {region} {P3(pos)} FAILED {err}"); return $"landmark create failed {err}"; }
            await Task.Delay(1500, ct);
            var lm = await LandmarkAsset(await FetchItemRO(made.UUID, ct) ?? made, ct);
            Log("landmark", $"created '{name}' item {made.UUID} at {region} {P3(pos)}; stored {(lm == null ? "?" : P3(lm.Position))}");
            return $"landmark '{name}' created (item {made.UUID}) at {region} {P3(pos)}; stored position {(lm == null ? "(asset not readable yet)" : P3(lm.Position))}";
        }
        if (sub == "raw")
        {
            var key = string.Join(' ', a.Skip(1)).Trim().Trim('"');
            return key.Length == 0 ? "usage: landmark raw <name>" : await LandmarkRaw(key, ct);
        }
        if (sub == "tp" || sub == "teleport")
        {
            var parts = a.Skip(1).ToList();
            bool force = parts.RemoveAll(x => x.Equals("force", StringComparison.OrdinalIgnoreCase)) > 0;
            bool byPos = parts.RemoveAll(x => x.Equals("pos", StringComparison.OrdinalIgnoreCase)) > 0;
            var key = string.Join(' ', parts).Trim().Trim('"');
            if (key.Length == 0) return "usage: landmark tp <name|item uuid> [pos] [force]";
            var items = await LandmarkItems(ct);
            InventoryItem it = UUID.TryParse(key, out var kid) ? items.FirstOrDefault(i => i.UUID == kid) ?? await FetchItemRO(kid, ct)
                : items.FirstOrDefault(i => i.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (it == null) { var m = items.Where(i => i.Name.Contains(key, StringComparison.OrdinalIgnoreCase)).ToList(); if (m.Count == 1) it = m[0]; else if (m.Count > 1) return $"'{key}' matches {m.Count}: {string.Join(", ", m.Select(i => i.Name))}"; }
            if (it == null) return $"no landmark '{key}' (see 'landmark list')";
            if (it.IsLink()) it = await FetchItemRO(it.AssetUUID, ct) ?? it;
            var lm = await LandmarkAsset(it, ct);
            if (lm == null) return $"could not read landmark '{it.Name}'";
            var (handle, rname) = await RegionById(lm.RegionID);
            bool robeOn = RobeMaybeWorn(out var robeHow);
            var block = RobeTpBlock(rname, robeOn, force);
            if (block != null) { Log("robe", $"WARNING landmark tp '{it.Name}' -> {rname} {block} ({robeHow})"); return block; }
            followId = UUID.Zero;
            if (client.Self.SittingOn != 0) { client.Self.Stand(); await Task.Delay(1500, ct); }
            var from = $"{client.Network.CurrentSim?.Name} {P3(client.Self.SimPosition)}";
            bool ok;
            if (byPos)
            {
                if (handle == 0) return $"region handle for {rname} unknown; use the landmark mode";
                var look = lm.Position + new Vector3(0, 1, 0);
                ok = await client.Self.TeleportAsync(handle, lm.Position, look, ct);
            }
            else ok = await client.Self.TeleportAsync(it.AssetUUID, ct);
            await Task.Delay(2500, ct);
            var now = client.Self.SimPosition; var nowR = client.Network.CurrentSim?.Name;
            float miss = string.Equals(nowR, rname, StringComparison.OrdinalIgnoreCase) ? Vector3.Distance(now, lm.Position) : float.NaN;
            var res = $"landmark tp ({(byPos ? "exact position" : "landmark")}) '{it.Name}' -> {rname} {P3(lm.Position)}: {(ok ? "ok" : "FAILED " + client.Self.TeleportMessage)}; from {from}; now {nowR} {P3(now)}" +
                      (float.IsNaN(miss) ? "" : $" ({miss:F1} m from the landmark{(miss > 10 ? " - landing point / teleport routing redirected" : "")})");
            if (!float.IsNaN(miss) && miss > 10 && client.Network.CurrentSim is { } dsim)
            {   // 2026-10-02: say WHY (parcel routing + the parcel group's 'Ignore landing point' ability)
                try { var d = await LandingDiag(dsim, lm.Position); if (d != null) res += $"\n  why: {d}"; } catch (Exception ex) { res += $"\n  why: parcel check failed {ex.GetBaseException().Message}"; }
            }
            Log("landmark", res);
            return res;
        }
        return "usage: landmark create <name> | landmark list | landmark raw <name> | landmark tp <name|item uuid> [pos] [force]";
    }

    // ---------------- sethome ----------------
    // AgentManager.SetHome() sends SetStartLocationRequest for the current position; the sim answers with an AlertMessage
    // (success e.g. "Home position set."; refusal e.g. "You can only set your 'Home Location' on your land or at a mainland
    // Infohub."). The reply text is reported verbatim; no reply within 10 s = unknown.
    static async Task<string> SetHomeCmd()
    {
        if (!LoggedIn) return "not logged in";
        var region = client.Network.CurrentSim?.Name; var pos = client.Self.SimPosition;
        var tcs = new TaskCompletionSource<AlertMessageEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void H(object s, AlertMessageEventArgs e) => tcs.TrySetResult(e);
        client.Self.AlertMessage += H;
        try
        {
            client.Self.SetHome();
            await Task.WhenAny(tcs.Task, Task.Delay(10000));
        }
        finally { client.Self.AlertMessage -= H; }
        string res;
        if (!tcs.Task.IsCompleted) res = $"sethome at {region} {P3(pos)}: sent, but no server reply within 10 s (unknown; check in a viewer)";
        else
        {
            var e = tcs.Task.Result; var m = e.Message ?? "";
            bool refused = System.Text.RegularExpressions.Regex.IsMatch(m, @"\b(can ?not|can't|cannot|only|not allowed|unable|failed)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            bool ok = !refused && m.Contains("home", StringComparison.OrdinalIgnoreCase) && m.Contains("set", StringComparison.OrdinalIgnoreCase);
            res = $"sethome at {region} {P3(pos)}: {(ok ? "OK" : refused ? "REFUSED" : "reply")} - server says '{m}'{(string.IsNullOrEmpty(e.NotificationId) ? "" : $" [{e.NotificationId}]")}";
        }
        Log("sethome", res);
        return res;
    }

    // ---------------- worn links / touch-attachment ----------------
    static List<string> Tokenize(string s)
    {
        var r = new List<string>(); var cur = new StringBuilder(); bool q = false;
        foreach (var ch in s)
        {
            if (ch == '"') { q = !q; continue; }
            if (ch == ' ' && !q) { if (cur.Length > 0) { r.Add(cur.ToString()); cur.Clear(); } continue; }
            cur.Append(ch);
        }
        if (cur.Length > 0) r.Add(cur.ToString());
        return r;
    }

    static List<Primitive> MatchAttachment(string key, List<Primitive> roots)
    {
        if (key.Equals("ao", StringComparison.OrdinalIgnoreCase)) return roots.Where(p => (p.Properties?.Name ?? "").Contains(AoNameMatch, StringComparison.OrdinalIgnoreCase) || (AoItem != UUID.Zero && AttachItemId(p) == AoItem)).ToList();
        if (UUID.TryParse(key, out var u)) return roots.Where(p => p.ID == u || AttachItemId(p) == u).ToList();
        var exact = roots.Where(p => string.Equals(p.Properties?.Name, key, StringComparison.OrdinalIgnoreCase)).ToList();
        return exact.Count > 0 ? exact : roots.Where(p => (p.Properties?.Name ?? "").Contains(key, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    // prims of one linkset: root = link 1, children in local-id order (the sim hands out local ids in link order when it rezzes/attaches)
    static List<Primitive> LinkPrims(Primitive root)
    {
        var sim = Sim; var kids = sim.ObjectsPrimitives.Values.Where(p => p != null && p.ParentID == root.LocalID).OrderBy(p => p.LocalID).ToList();
        kids.Insert(0, root); return kids;
    }

    static int FaceCount(Primitive p) { try { return p.Textures?.FaceTextures?.Count(f => f != null) is int n && n > 0 ? n : 1; } catch { return 1; } }

    static async Task<string> WornLinks(string key)
    {
        var roots = WornPrims(); await EnsureProperties(Sim, roots);
        var m = MatchAttachment(key, roots);
        if (m.Count == 0) return $"no worn attachment matches '{key}'; worn: {string.Join(", ", roots.Select(r => r.Properties?.Name ?? "?"))}";
        if (m.Count > 1) return $"'{key}' matches {m.Count}: {string.Join(", ", m.Select(r => r.Properties?.Name))}";
        var prims = LinkPrims(m[0]); await EnsureProperties(Sim, prims);
        var sb = new StringBuilder($"'{m[0].Properties?.Name}' @{m[0].PrimData.AttachmentPoint}: {prims.Count} prims (link no. = root 1, then children by local id; the sim does not send real link order, so prefer prim names / local:<id> when they differ)\n");
        for (int i = 0; i < prims.Count; i++)
        {
            var p = prims[i];
            sb.AppendLine($"  link {i + 1,-3} local {p.LocalID} '{p.Properties?.Name ?? "?"}' desc '{p.Properties?.Description ?? ""}' faces~{FaceCount(p)} " +
                          $"{((p.Flags & PrimFlags.Scripted) != 0 ? "scripted " : "")}{((p.Flags & PrimFlags.Touch) != 0 ? "touch " : "")}pos {p.Position.X:F3},{p.Position.Y:F3},{p.Position.Z:F3} size {p.Scale.X:F3},{p.Scale.Y:F3},{p.Scale.Z:F3}");
        }
        return sb.ToString().TrimEnd();
    }

    static async Task<string> TouchAttachment(string rest)
    {
        var t = Tokenize(rest);
        Vector3 st = AgentManager.TOUCH_INVALID_TEXCOORD; bool haveSt = false;
        for (int i = t.Count - 1; i >= 0; i--)
            if (t[i].StartsWith("st=", StringComparison.OrdinalIgnoreCase))
            {
                var xy = t[i][3..].Split(','); if (xy.Length == 2 && F(xy[0], out var sx) && F(xy[1], out var sy)) { st = new Vector3(sx, sy, 0); haveSt = true; }
                t.RemoveAt(i);
            }
        if (t.Count < 2) return "usage: touch-attachment <attachment> <link no.|prim name|local:<id>> [face] [st=u,v]   (quote names with spaces; 'ao' = the AO HUD)";
        var roots = WornPrims(); await EnsureProperties(Sim, roots);
        // longest attachment-name prefix that matches exactly one worn attachment and leaves a link spec
        Primitive att = null; int used = 0;
        for (int k = t.Count - 1; k >= 1 && att == null; k--)
        {
            var mm = MatchAttachment(string.Join(' ', t.Take(k)), roots);
            if (mm.Count == 1) { att = mm[0]; used = k; }
        }
        if (att == null) return $"no single worn attachment matches; worn: {string.Join(", ", roots.Select(r => r.Properties?.Name ?? "?"))}";
        var rem = t.Skip(used).ToList();
        int face = -1;
        if (rem.Count >= 2 && int.TryParse(rem[^1], out var fc)) { face = fc; rem.RemoveAt(rem.Count - 1); }
        var spec = string.Join(' ', rem);
        var prims = LinkPrims(att); await EnsureProperties(Sim, prims);
        Primitive target = null; int linkNo = 0;
        if (spec.StartsWith("local:", StringComparison.OrdinalIgnoreCase) && uint.TryParse(spec[6..], out var lid)) { target = prims.FirstOrDefault(p => p.LocalID == lid); }
        else if (int.TryParse(spec, out var ln) && ln >= 1 && ln <= prims.Count) target = prims[ln - 1];
        else
        {
            var byName = prims.Where(p => string.Equals(p.Properties?.Name, spec, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byName.Count == 0) byName = prims.Where(p => string.Equals(p.Properties?.Description, spec, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byName.Count == 0) byName = prims.Where(p => (p.Properties?.Name ?? "").Contains(spec, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byName.Count > 1) return $"'{spec}' matches {byName.Count} prims: {string.Join(", ", byName.Select(p => $"link {prims.IndexOf(p) + 1} '{p.Properties?.Name}'"))}";
            target = byName.FirstOrDefault();
        }
        if (target == null) return $"no prim '{spec}' in '{att.Properties?.Name}' (see 'worn links')";
        linkNo = prims.IndexOf(target) + 1;
        if (face >= 0 && !haveSt) st = new Vector3(0.5f, 0.5f, 0);
        var uv = haveSt || face >= 0 ? st : AgentManager.TOUCH_INVALID_TEXCOORD;
        var none = AgentManager.TOUCH_INVALID_VECTOR;
        client.Self.Grab(target.LocalID, Vector3.Zero, uv, st, face, none, none, none);
        await Task.Delay(150);
        client.Self.DeGrab(target.LocalID, uv, st, face, none, none, none);
        var msg = $"touched '{att.Properties?.Name}' link {linkNo} '{target.Properties?.Name}' (local {target.LocalID}) face {face}{(haveSt || face >= 0 ? $" st {st.X:F2},{st.Y:F2}" : "")}";
        Log("touch", msg);
        return msg + "; any menu shows up as a [dialog] log line";
    }

    // ---------------- shape ----------------
    static float SliderOf(VisualParam vp, float w) => vp.MaxValue - vp.MinValue == 0 ? 0 : (w - vp.MinValue) / (vp.MaxValue - vp.MinValue) * 100f;

    static async Task<(AppearanceManager.WearableData wd, InventoryItem item, byte[] data, AssetBodypart asset, string err)> WornShape(CancellationToken ct)
    {
        var wd = client.Appearance.GetWearables().FirstOrDefault(w => w.WearableType == WearableType.Shape);
        if (wd == null) return (null, null, null, null, "no shape in the appearance manager's worn wearables");
        var item = await FetchItemRO(wd.ItemID, ct);
        if (item == null) return (wd, null, null, null, $"shape item {wd.ItemID} not returned by inventory fetch");
        if (item.IsLink()) item = await FetchItemRO(item.AssetUUID, ct) ?? item;
        Asset asset;
        try { using var t = CancellationTokenSource.CreateLinkedTokenSource(ct); t.CancelAfter(30000); asset = await client.Assets.RequestInventoryAssetAsync(item, true, UUID.Random(), t.Token); }
        catch (Exception ex) { return (wd, item, null, null, "download failed: " + ex.GetBaseException().Message); }
        if (asset == null || asset.AssetData == null || asset.AssetData.Length == 0) return (wd, item, null, null, "shape asset download returned nothing");
        var bp = new AssetBodypart(item.AssetUUID, asset.AssetData); bp.Decode();
        return (wd, item, asset.AssetData, bp, null);
    }

    static int CofVersion(UUID cof) { try { if (client.Inventory.Store.TryGetNodeFor(cof, out var n) && n.Data is InventoryFolder f) return f.Version; } catch { } return -1; }

    // re-create the COF link of the (edited) shape: new link first, then remove the old one(s) -> COF version goes up,
    // so UpdateAvatarAppearance bakes again (the server skips a COF version it has already baked)
    static async Task<string> BumpShapeCofLink(InventoryItem shape, CancellationToken ct)
    {
        try
        {
            var cof = await CofFolder(ct); if (cof == null) return "COF not found (no rebake bump)";
            int v0 = CofVersion(cof.UUID);
            var links = (await ReadFolderRO(cof.UUID, ct)).OfType<InventoryItem>().Where(l => l.ParentUUID == cof.UUID && l.IsLink() && l.AssetUUID == shape.UUID).ToList();
            InventoryItem made;
            using (var t = CancellationTokenSource.CreateLinkedTokenSource(ct)) { t.CancelAfter(20000); made = await client.Inventory.CreateLinkAsync(cof.UUID, shape.UUID, shape.Name, links.FirstOrDefault()?.Description ?? "", shape.InventoryType, UUID.Random(), t.Token); }
            if (made == null) return $"COF link re-create FAILED (COF v{v0}; rebake may not show the change until relog)";
            if (links.Count > 0) await client.Inventory.RemoveItemsAsync(links.Select(l => l.UUID), ct);
            await Task.Delay(1000, ct);
            await ReadFolderRO(cof.UUID, ct);
            int v1 = CofVersion(cof.UUID);
            Log("shape", $"COF link for '{shape.Name}' re-created {made.UUID} (old {string.Join(",", links.Select(l => l.UUID))}); COF v{v0} -> v{v1}");
            return $"COF link re-created (COF v{v0} -> v{v1})";
        }
        catch (Exception ex) { return "COF bump error: " + ex.GetBaseException().Message; }
    }

    static VisualParam? FindShapeParam(string key)
    {
        var shapes = VisualParams.Params.Values.Where(v => v.Group == 0 && string.Equals(v.Wearable, "shape", StringComparison.OrdinalIgnoreCase)).ToList();
        if (int.TryParse(key, out var id)) { var x = shapes.FirstOrDefault(v => v.ParamID == id); return x.Name == null ? null : x; }
        string N(string s) => (s ?? "").Replace("_", " ").Trim();
        var m = shapes.Where(v => N(v.Label).Equals(N(key), StringComparison.OrdinalIgnoreCase) || N(v.Name).Equals(N(key), StringComparison.OrdinalIgnoreCase)).ToList();
        return m.Count == 1 ? m[0] : null;
    }

    static async Task<string> ShapeCmd(string[] a)
    {
        using var cts = new CancellationTokenSource(120000); var ct = cts.Token;
        if (a.Length == 0 || a[0] == "get")
        {
            var filter = a.Length > 1 ? string.Join(' ', a.Skip(1)) : "";
            var (wd, item, data, bp, err) = await WornShape(ct);
            if (err != null) return err;
            IReadOnlyDictionary<int, float> live = null;
            try { var me = Sim.ObjectsAvatars.Values.FirstOrDefault(v => v.ID == client.Self.AgentID); if (me?.VisualParameters?.Length > 0) live = me.DecodeVisualParams(); } catch { }
            var sb = new StringBuilder($"worn shape '{item.Name}' item {item.UUID} asset {item.AssetUUID} modify={((item.Permissions.OwnerMask & PermissionMask.Modify) != 0 ? "yes" : "NO")}{(item.Name == AllowedShapeName ? "" : " (NOT the editable shape: set is refused)")}\n");
            foreach (var vp in VisualParams.Params.Values.Where(v => v.Group == 0 && string.Equals(v.Wearable, "shape", StringComparison.OrdinalIgnoreCase)).OrderBy(v => string.IsNullOrEmpty(v.Label) ? v.Name : v.Label))
            {
                if (filter.Length > 0 && !(vp.Label ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase) && !(vp.Name ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase) && vp.ParamID.ToString() != filter) continue;
                float w = bp.Params.TryGetValue(vp.ParamID, out var pv) ? pv : vp.DefaultValue;
                sb.AppendLine(string.Format(IC2, "  {0,-24} id {1,-4} slider {2,3:F0}  (weight {3:0.###}, range {4:0.##}..{5:0.##}){6}{7}", string.IsNullOrEmpty(vp.Label) ? vp.Name : vp.Label, vp.ParamID, SliderOf(vp, w), w, vp.MinValue, vp.MaxValue,
                    bp.Params.ContainsKey(vp.ParamID) ? "" : " [default]", live != null && live.TryGetValue(vp.ParamID, out var lv) ? string.Format(IC2, " live {0:F0}", SliderOf(vp, lv)) : ""));
            }
            return sb.ToString().TrimEnd();
        }
        if (a[0] == "set" && a.Length >= 3 && float.TryParse(a[^1], NumberStyles.Float, IC2, out var slider))
        {
            var key = string.Join(' ', a.Skip(1).Take(a.Length - 2)).Trim('"');
            var vpn = FindShapeParam(key);
            if (vpn == null) return $"no single shape slider '{key}' (see 'shape get <filter>'; label or param id)";
            var vp = vpn.Value;
            if (slider < 0 || slider > 100) return "slider value must be 0..100";
            var (wd, item, data, bp, err) = await WornShape(ct);
            if (err != null) return err;
            if (item.Name != AllowedShapeName) return $"refused: the worn shape is '{item.Name}', only '{AllowedShapeName}' may be edited";
            if ((item.Permissions.OwnerMask & PermissionMask.Modify) == 0) return $"refused: '{item.Name}' is no-modify";
            float oldW = bp.Params.TryGetValue(vp.ParamID, out var ow) ? ow : vp.DefaultValue;
            float newW = vp.MinValue + slider / 100f * (vp.MaxValue - vp.MinValue);
            // text-level edit of the original asset so every other line stays byte-identical
            var text = Encoding.UTF8.GetString(data).TrimEnd('\0');
            var lines = text.Split('\n').ToList();
            int pIdx = lines.FindIndex(l => l.StartsWith("parameters "));
            int tIdx = lines.FindIndex(l => l.StartsWith("textures "));
            if (pIdx < 0) return "shape asset has no 'parameters' section; not touching it";
            int end = tIdx > pIdx ? tIdx : lines.Count; bool found = false;
            var newVal = newW.ToString("0.######", IC2);
            for (int i = pIdx + 1; i < end; i++)
            {
                var f = lines[i].Split(' ');
                if (f.Length == 2 && int.TryParse(f[0], out var pid) && pid == vp.ParamID) { lines[i] = $"{pid} {newVal}"; found = true; break; }
            }
            if (!found) return $"param {vp.ParamID} not present in the shape asset; not adding lines (refused)";
            var newData = Encoding.UTF8.GetBytes(string.Join('\n', lines));
            Directory.CreateDirectory(ShapeBackupDir);
            var bak = Path.Combine(ShapeBackupDir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{item.AssetUUID}.lnw");
            File.WriteAllBytes(bak, data);
            File.AppendAllText(Path.Combine(ShapeBackupDir, "index.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} item {item.UUID} '{item.Name}' old asset {item.AssetUUID} -> {bak}; change {vp.Label} ({vp.ParamID}) {oldW} -> {newVal}\n");
            var tid = UUID.Random();
            try { await client.Assets.RequestUploadAsync(AssetType.Bodypart, newData, false, tid, ct); }
            catch (Exception ex) { Log("shape", $"upload FAILED: {ex.GetBaseException().Message}"); return "asset upload failed: " + ex.GetBaseException().Message + " (nothing changed)"; }
            var newAsset = UUID.Combine(tid, client.Self.SecureSessionID);
            item.TransactionID = tid;
            bool updated = false; string how; string cofNote = "";
            if (client.AisClient.IsAvailable)
            {
                var upd = (OSDMap)item.GetOSD(); upd.Remove("asset_id"); upd.Remove("shadow_id"); upd["hash_id"] = OSD.FromUUID(tid);
                try { updated = await client.AisClient.UpdateItemAsync(item.UUID, upd, ct); } catch (Exception ex) { Log("shape", "AIS update error " + ex.GetBaseException().Message); }
                how = "AIS";
            }
            else { client.Inventory.RequestUpdateItems(new List<InventoryItem> { item }, tid); updated = true; how = "UDP"; }
            await Task.Delay(2000, ct);
            var check = await FetchItemRO(item.UUID, ct);
            bool assetChanged = check != null && check.AssetUUID != item.AssetUUID && check.AssetUUID != UUID.Zero;
            if (assetChanged)
            {
                bp.Params[vp.ParamID] = newW; wd.Asset = bp; wd.AssetID = check.AssetUUID;
                cofNote = await BumpShapeCofLink(check, ct); // server-side bake only re-runs for a NEW COF version
                try { await client.Appearance.RequestSetAppearance(true); } catch (Exception ex) { Log("shape", "rebake error " + ex.GetBaseException().Message); }
            }
            var res = string.Format(IC2, "shape '{0}': {1} slider {2:F0} -> {3:F0} (weight {4:0.###} -> {5}); upload ok (asset {6}); item update via {7} {8}; item asset now {9}{10}; {12}; backup {11}",
                item.Name, string.IsNullOrEmpty(vp.Label) ? vp.Name : vp.Label, SliderOf(vp, oldW), slider, oldW, newVal, newAsset, how, updated ? "ok" : "FAILED", check?.AssetUUID, assetChanged ? " (changed; rebake requested)" : " (UNCHANGED)", bak, cofNote);
            Log("shape", res);
            return res;
        }
        return "usage: shape get [filter] | shape set <slider label|param id> <0-100>";
    }
}
