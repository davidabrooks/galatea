// Outfit.cs (2026-09-26 11:10, David: list what Galatay wears; later save it as her first outfit).
// 'worn all' = READ-ONLY listing: wearables (AgentWearables as held by the AppearanceManager + Current Outfit Folder links),
// attachments incl. HUDs (sim prims on her avatar + COF links), and the My Outfits folder contents.
// It only reads inventory (FetchInventoryDescendents2 / FetchItem); it never wears, detaches, links, creates or touches
// anything. It deliberately does NOT instantiate LibreMetaverse's CurrentOutfitFolder helper (that one registers event
// handlers that edit COF links on attach/detach).
using System.Text;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    static bool IsHudPoint(AttachmentPoint p) => (int)p >= 31 && (int)p <= 38;
    static bool IsBodyPartType(WearableType t) => t is WearableType.Shape or WearableType.Skin or WearableType.Hair or WearableType.Eyes;

    static async Task<List<InventoryBase>> ReadFolderRO(UUID folder, CancellationToken ct)
    {
        try { return await client.Inventory.RequestFolderContentsAsync(folder, client.Self.AgentID, true, true, InventorySortOrder.ByName, ct) ?? new(); }
        catch (Exception ex) { Log("outfit", $"folder read {folder} failed: {ex.GetBaseException().Message}"); return new(); }
    }
    static async Task<InventoryItem> FetchItemRO(UUID id, CancellationToken ct)
    {
        try { using var t = CancellationTokenSource.CreateLinkedTokenSource(ct); t.CancelAfter(15000); return await client.Inventory.FetchItemAsync(id, client.Self.AgentID, t.Token); }
        catch { return null; }
    }

    // wait (max 30 s) until the number of own attachments has not changed for 6 s; only in the first 3 min after tracking began
    static async Task<string> SettleAttachments()
    {
        if (attTrackStart == DateTime.MinValue || (DateTime.Now - attTrackStart).TotalSeconds > 180) return "";
        var t0 = DateTime.Now; int last = -1; var stableSince = DateTime.Now;
        while ((DateTime.Now - t0).TotalSeconds < 30)
        {
            int n = TrackedAttachments().Select(r => r.Local).Union(WornPrims().Select(p => p.LocalID)).Count();
            if (n != last) { last = n; stableSince = DateTime.Now; }
            else if ((DateTime.Now - stableSince).TotalSeconds >= 6) break;
            await Task.Delay(500);
        }
        return $"(within 3 min of login: waited {(DateTime.Now - t0).TotalSeconds:F0} s until the attachment count held at {last} for 6 s)";
    }

    static async Task<string> WornAll()
    {
        if (!LoggedIn) return "not logged in";
        using var cts0 = new CancellationTokenSource(90000); var ct = cts0.Token;
        var sb = new StringBuilder();
        var root = client.Inventory.Store?.RootFolder;
        if (root == null) return "inventory root not known yet";
        var rootKids = await ReadFolderRO(root.UUID, ct);
        var cof = rootKids.OfType<InventoryFolder>().FirstOrDefault(f => f.PreferredType == FolderType.CurrentOutfit);
        var myOutfits = rootKids.OfType<InventoryFolder>().FirstOrDefault(f => f.PreferredType == FolderType.MyOutfits);

        // COF links -> target items
        var cofLinks = new List<InventoryItem>(); var targets = new Dictionary<UUID, InventoryItem>();
        if (cof != null)
        {
            // FetchInventoryDescendents2 also returns the linked target items; keep only entries whose parent is the COF
            cofLinks = (await ReadFolderRO(cof.UUID, ct)).OfType<InventoryItem>().Where(i => i.ParentUUID == cof.UUID).ToList();
            foreach (var l in cofLinks.Where(l => l.IsLink())) { var t = await FetchItemRO(l.AssetUUID, ct); if (t != null) targets[l.AssetUUID] = t; }
        }
        string NameOfItem(UUID id) => targets.TryGetValue(id, out var t) ? t.Name : cofLinks.FirstOrDefault(l => l.AssetUUID == id)?.Name;

        // wearables (AppearanceManager's view) + COF wearable links
        List<AppearanceManager.WearableData> wears; try { wears = client.Appearance.GetWearables().ToList(); } catch { wears = new(); }
        var wearIds = wears.Select(w => w.ItemID).ToHashSet();
        var cofWearLinks = cofLinks.Where(l => l.IsLink() && (targets.TryGetValue(l.AssetUUID, out var t) ? t is InventoryWearable : l.InventoryType == InventoryType.Wearable)).ToList();
        foreach (var l in cofWearLinks) if (!wearIds.Contains(l.AssetUUID) && targets.TryGetValue(l.AssetUUID, out var t) && t is InventoryWearable tw)
            wears.Add(new AppearanceManager.WearableData { ItemID = tw.UUID, AssetID = tw.AssetUUID, WearableType = tw.WearableType, AssetType = tw.AssetType });
        string WLine(AppearanceManager.WearableData w)
        {
            var name = NameOfItem(w.ItemID) ?? w.Asset?.Name;
            if (name == null) { var t = FetchItemRO(w.ItemID, ct).GetAwaiter().GetResult(); name = t?.Name ?? "?"; }
            var src = (wearIds.Contains(w.ItemID) ? "wearables" : "") + (cofWearLinks.Any(l => l.AssetUUID == w.ItemID) ? (wearIds.Contains(w.ItemID) ? "+COF" : "COF only") : " (no COF link)");
            var desc = cofWearLinks.FirstOrDefault(l => l.AssetUUID == w.ItemID)?.Description;
            return $"  {w.WearableType,-10} '{name}' item {w.ItemID} [{src}]{(string.IsNullOrEmpty(desc) ? "" : " link desc " + desc)}";
        }
        wears = wears.GroupBy(w => (w.ItemID, w.WearableType)).Select(g => g.First()).ToList(); // AppearanceManager may hold a wearable twice
        var body = wears.Where(w => IsBodyPartType(w.WearableType)).OrderBy(w => (int)w.WearableType).ToList();
        var cloth = wears.Where(w => !IsBodyPartType(w.WearableType)).OrderBy(w => (int)w.WearableType).ToList();
        sb.AppendLine($"BODY PARTS ({body.Count}):"); foreach (var w in body) sb.AppendLine(WLine(w));
        sb.AppendLine($"CLOTHING / system layers ({cloth.Count}):"); foreach (var w in cloth) sb.AppendLine(WLine(w));

        // attachments on her avatar: union of the library's prims (ParentID == our LocalID) and the raw-packet table
        // (AttachTrack.cs). Right after a login the sim is still sending attachments, so wait until the count settles.
        var settle = await SettleAttachments();
        var cofObjLinks = cofLinks.Where(l => l.IsLink() && (targets.TryGetValue(l.AssetUUID, out var t) ? t is InventoryObject : l.InventoryType == InventoryType.Object)).ToList();
        // COF object links we have no attachment for: the sim may have sent them in a packet we lost -> ask again once
        HashSet<UUID> HaveItems() => TrackedAttachments().Select(r => r.Item).Concat(WornPrims().Select(AttachItemId)).Where(u => u != UUID.Zero).ToHashSet();
        var missingBefore = cofObjLinks.Where(l => !HaveItems().Contains(l.AssetUUID)).ToList();
        // (12:38) no automatic re-request any more: the SL sim does not answer RequestMultipleObjects for objects it already
        // sent (control test in 'worn probe' got no answer); worn-but-never-sent items are recovered below from the sim's appearance list
        string recoverNote = missingBefore.Count > 0 ? $"(after the settle wait, {missingBefore.Count} COF object(s) had still not been received from the sim as objects; see the [bracket] notes and the sim's list below)" : "";
        var prims = WornPrims(); await EnsureProperties(Sim, prims);
        var tracked = TrackedAttachments();
        var wornItemIds = new HashSet<UUID>();
        var rowsA = new List<(int point, string line)>();
        var seenLocal = new HashSet<uint>();
        string ALine(int pt, string primName, UUID item, string note)
        {
            if (item != UUID.Zero) wornItemIds.Add(item);
            var link = cofObjLinks.FirstOrDefault(l => l.AssetUUID == item);
            var ap = (AttachmentPoint)(byte)pt;
            return $"  {ap,-15} (#{pt}) '{primName ?? NameOfItem(item) ?? "?"}' item {item}{(link != null ? " [COF link]" : " [NO COF link]")}{note}";
        }
        foreach (var p in prims)
        {
            seenLocal.Add(p.LocalID);
            var item = AttachItemId(p);
            if (item == UUID.Zero && attTable.TryGetValue(p.LocalID, out var tr) && tr.Item != UUID.Zero) item = tr.Item;
            rowsA.Add(((int)p.PrimData.AttachmentPoint, ALine((int)p.PrimData.AttachmentPoint, p.Properties?.Name, item, "")));
        }
        foreach (var r in tracked.Where(r => !seenLocal.Contains(r.Local)))
        {
            int pt = AttachPointFromState(r.State);
            rowsA.Add((pt, ALine(pt, null, r.Item, " (raw-packet table only; not in the library's object list)")));
        }
        // (12:45) The sim sometimes never sends us the object update of one of our own attachments (tee, body, bridge seen so far)
        // and ignores re-requests. Two read-only sources still tell us it is worn:
        // 1) the sim's own attachment list in AvatarAppearance for our avatar (non-HUD attachments, object UUID + point):
        //    an entry we never received as an object is matched to a missing COF object by its last known attach point
        var inferNotes = new List<string>();
        var sal = simAttList;
        if (sal != null)
        {
            var unmatched = sal.Where(x => x.id != UUID.Zero && !attTable.Values.Any(r => r.Full == x.id && r.Killed == null) && !prims.Any(p => p.ID == x.id)).ToList();
            foreach (var grp in unmatched.GroupBy(x => (int)x.point))
            {
                var cands = cofObjLinks.Where(l => !wornItemIds.Contains(l.AssetUUID) && AttPointOf(l.AssetUUID) == grp.Key).ToList();
                if (cands.Count == grp.Count())
                    foreach (var l in cands) rowsA.Add((grp.Key, ALine(grp.Key, null, l.AssetUUID, " [worn per the sim's own attachment list; its object update never reached this client]")));
                else inferNotes.Add($"the sim lists {grp.Count()} attachment(s) at #{grp.Key} ({(AttachmentPoint)(byte)grp.Key}) that never reached us as objects ({string.Join(", ", grp.Select(x => x.id))}); {cands.Count} missing COF object(s) have that last known point, so I cannot tell which");
            }
        }
        // 2) HUD attachments are not in that list: a missing COF object that owner-said to us this session is taken as worn
        foreach (var l in cofObjLinks.Where(l => !wornItemIds.Contains(l.AssetUUID)).ToList())
        {
            var nm = NameOfItem(l.AssetUUID);
            var ch = ownChatters.Where(kv => kv.Value.name == nm).OrderByDescending(kv => kv.Value.t).FirstOrDefault();
            if (ch.Key == UUID.Zero) continue;
            // a detach sent after that chat means the chat no longer proves anything
            if (lastDetachSent.TryGetValue(l.AssetUUID, out var dt) && dt >= ch.Value.t) { inferNotes.Add($"'{nm}' chatted at {ch.Value.t:HH:mm:ss} but a detach was sent at {dt:HH:mm:ss}; not counted as worn"); continue; }
            int pt = Math.Max(0, AttPointOf(l.AssetUUID));
            rowsA.Add((pt, ALine(pt, null, l.AssetUUID, $" [inferred worn: it chatted to us at {ch.Value.t:HH:mm:ss}; HUD attachments are not in the sim's attachment list and its object update never reached this client]")));
        }
        var att = rowsA.Where(r => !IsHudPoint((AttachmentPoint)(byte)r.point)).OrderBy(r => r.point).ThenBy(r => r.line, StringComparer.Ordinal).ToList();
        var hud = rowsA.Where(r => IsHudPoint((AttachmentPoint)(byte)r.point)).OrderBy(r => r.point).ThenBy(r => r.line, StringComparer.Ordinal).ToList();
        sb.AppendLine($"ATTACHMENTS ({att.Count}):"); foreach (var r in att) sb.AppendLine(r.line);
        sb.AppendLine($"HUDs ({hud.Count}):"); foreach (var r in hud) sb.AppendLine(r.line);
        if (!string.IsNullOrEmpty(settle)) sb.AppendLine(settle);
        if (!string.IsNullOrEmpty(recoverNote)) sb.AppendLine(recoverNote);
        sb.AppendLine(sal == null ? "(the sim's own attachment list (AvatarAppearance) has not arrived yet)" : $"(the sim's own attachment list, {simAttTime:HH:mm:ss}: {sal.Count} non-HUD attachment(s); every row above without a [bracket] note was received as an object)");
        foreach (var n in inferNotes) sb.AppendLine("(" + n + ")");
        var stale = cofObjLinks.Where(l => !wornItemIds.Contains(l.AssetUUID)).ToList();
        if (stale.Count > 0) { sb.AppendLine($"COF object links that are NOT attached on the avatar ({stale.Count}) (linked in the Current Outfit folder, but not on her: not received as objects, not in the sim's attachment list, and no chat from them since the last detach):"); foreach (var l in stale) { var t = targets.GetValueOrDefault(l.AssetUUID) as InventoryObject; sb.AppendLine($"  '{l.Name}' item {l.AssetUUID}{(t != null ? $" last point {t.AttachPoint}" : "")}{(l.Name.StartsWith("#Firestorm LSL Bridge", StringComparison.OrdinalIgnoreCase) ? " (kept off in the text client on purpose; Firestorm re-adds it at its own login)" : "")}"); } }
        var otherCof = cofLinks.Where(l => !cofWearLinks.Contains(l) && !cofObjLinks.Contains(l)).ToList();
        if (otherCof.Count > 0) { sb.AppendLine($"other COF entries ({otherCof.Count}):"); foreach (var l in otherCof) sb.AppendLine($"  {l.AssetType}/{l.InventoryType} '{l.Name}' -> {l.AssetUUID} desc '{l.Description}'"); }
        sb.AppendLine($"seated: {(client.Self.SittingOn != 0 ? "yes (seat-off rule may have detached some items)" : "no")}; seat-off rule: {seatAttachState}");

        // COF + My Outfits
        sb.AppendLine($"COF: {(cof == null ? "NOT FOUND" : $"'{cof.Name}' {cof.UUID} v{cof.Version}, {cofLinks.Count} entries ({cofWearLinks.Count} wearable links, {cofObjLinks.Count} object links)")}");
        if (myOutfits == null) sb.AppendLine("My Outfits: NOT FOUND in the inventory root");
        else
        {
            var mo = (await ReadFolderRO(myOutfits.UUID, ct)).Where(k => k.ParentUUID == myOutfits.UUID).ToList();
            sb.AppendLine($"My Outfits: '{myOutfits.Name}' {myOutfits.UUID} (type {myOutfits.PreferredType}), {mo.Count} entries");
            foreach (var e in mo)
            {
                if (e is InventoryFolder f) { var kids = (await ReadFolderRO(f.UUID, ct)).Where(k => k.ParentUUID == f.UUID).ToList(); /* FetchInventoryDescendents2 also returns link targets */ sb.AppendLine($"  folder '{f.Name}' {f.UUID} type {f.PreferredType}, {kids.Count} entries: {string.Join(", ", kids.Take(12).Select(k => k.Name))}{(kids.Count > 12 ? ", ..." : "")}"); }
                else if (e is InventoryItem i) sb.AppendLine($"  item '{i.Name}' {i.AssetType} {i.UUID}");
            }
        }
        Log("outfit", $"worn all: {body.Count} body parts, {cloth.Count} clothing, {att.Count} attachments, {hud.Count} HUDs, COF {cofLinks.Count} entries, My Outfits {(myOutfits == null ? "missing" : "present")}");
        return sb.ToString().TrimEnd();
    }
}
