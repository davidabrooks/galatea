// WearOps.cs (2026-09-26 20:50, David: unpack + wear the VIOLETTE robe from Received Items in the text client).
// inv ls <folder uuid>                  READ-ONLY: direct contents of one folder (fresh server fetch)
// wear add <item uuid> [point]          ADD (never replace): attach an object (ATTACHMENT_ADD) or add a clothing layer,
//                                       then add a COF link to the ORIGINAL item (so logins keep it). Body parts refused.
// wear remove <item uuid>               take off: detach an object / remove a clothing layer, then remove ONLY its COF link(s).
//                                       Objects go through DetachItemAsync (same COF cleanup as `detach`). Body parts refused.
//                                       The inventory item itself is never touched.
// rez <item uuid>                       rez an OBJECT item from her inventory 1.5 m in front of her (e.g. a delivery box)
// take <object uuid>                    take back an object SHE owns into her Objects folder
// offer allow <object name> [minutes]   accept task-inventory offers ONLY from an object with that exact name owned by her,
//                                       for N minutes (default 5, max 15); `offer status`, `offer off`. Everything else stays IGNORED.
using System.Text;
using LibreMetaverse;
using LibreMetaverse.Packets;

namespace GalatayText;

public static partial class Program
{
    static string offerAllowName; static DateTime offerAllowUntil = DateTime.MinValue; static readonly List<string> offerLog = new();

    static async Task<InventoryFolder> CofFolder(CancellationToken ct)
    {
        var root = client.Inventory.Store?.RootFolder; if (root == null) return null;
        return (await ReadFolderRO(root.UUID, ct)).OfType<InventoryFolder>().FirstOrDefault(f => f.PreferredType == FolderType.CurrentOutfit && f.ParentUUID == root.UUID);
    }

    // Remove ONLY the Current Outfit Folder link(s) that point at this original item. The inventory item itself is untouched.
    // Used by detach / wear remove / unexpected self-detach cleanup.
    static async Task<string> RemoveCofLinksForItem(UUID item, string why, CancellationToken ct)
    {
        if (item == UUID.Zero) return "no item";
        try
        {
            var cof = await CofFolder(ct);
            if (cof == null) return "Current Outfit folder not found";
            var mine = (await ReadFolderRO(cof.UUID, ct)).OfType<InventoryItem>()
                .Where(l => l.ParentUUID == cof.UUID && l.IsLink() && l.AssetUUID == item).ToList();
            if (mine.Count == 0) return "no COF link to remove";
            await client.Inventory.RemoveItemsAsync(mine.Select(l => l.UUID), ct);
            var ids = string.Join(", ", mine.Select(l => l.UUID));
            Log("wear", $"COF link(s) removed for item {item} ({why}): {ids} (item itself untouched)");
            return $"COF link(s) removed: {ids} (the item itself is untouched)";
        }
        catch (Exception ex) { return "COF link removal FAILED: " + ex.GetBaseException().Message; }
    }

    // 2026-10-07: re-attaching an item a few seconds after detaching it (The V, after its HUD changed its prims) was
    // silently dropped by the sim: "attach (ADD) sent" but nothing arrived (the sim is still saving the detached copy
    // back to inventory). So: send, wait for the attachment to show up, re-send once for the missing ones, report.
    internal static (List<UUID> resend, List<UUID> missing) AttachVerifyPlan(IEnumerable<UUID> wanted, ISet<UUID> seenFirst, ISet<UUID> seenAfterRetry)
    {
        var w = wanted.Distinct().ToList();
        var resend = w.Where(u => !seenFirst.Contains(u)).ToList();
        return (resend, seenAfterRetry == null ? null : resend.Where(u => !seenAfterRetry.Contains(u)).ToList());
    }

    static async Task<bool> WaitWorn(ICollection<UUID> items, int ms, CancellationToken ct)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            var worn = WornPrims().Select(AttachItemId).ToHashSet();
            if (items.All(worn.Contains)) return true;
            await Task.Delay(1000, ct);
        }
        return false;
    }

    /// <summary>Attach (ADD) each item, verify it arrives (12 s), re-send once for the missing ones (15 s more).
    /// Returns a note per item: "attached", "attached on retry" or "NOT attached after retry".</summary>
    static async Task<Dictionary<UUID, string>> AttachVerified(IList<(InventoryItem inv, AttachmentPoint pt)> items, CancellationToken ct)
    {
        foreach (var (inv, pt) in items) client.Appearance.Attach(inv, pt, false);
        var ids = items.Select(i => i.inv.UUID).ToList();
        await WaitWorn(ids, 12000, ct);
        var seen1 = WornPrims().Select(AttachItemId).ToHashSet();
        var (resend, _) = AttachVerifyPlan(ids, seen1, null);
        HashSet<UUID> seen2 = seen1;
        if (resend.Count > 0)
        {
            Log("wear", $"attach not seen after 12 s, re-sending once: {string.Join(", ", resend)}");
            foreach (var (inv, pt) in items.Where(i => resend.Contains(i.inv.UUID))) client.Appearance.Attach(inv, pt, false);
            await WaitWorn(resend, 15000, ct);
            seen2 = WornPrims().Select(AttachItemId).ToHashSet();
        }
        var (_, missing) = AttachVerifyPlan(ids, seen1, seen2);
        return ids.ToDictionary(u => u, u => missing.Contains(u) ? "NOT attached after retry" : resend.Contains(u) ? "attached on retry" : "attached");
    }

    static async Task<string> WearOpsCmd(string cmd, string[] a)
    {
        if (!LoggedIn) return "not logged in";
        using var cts = new CancellationTokenSource(120000); var ct = cts.Token;
        switch (cmd)
        {
            case "invls":
            {
                if (a.Length < 1 || !UUID.TryParse(a[0], out var fid)) return "usage: inv ls <folder uuid>";
                var kids = (await ReadFolderRO(fid, ct)).Where(k => k.ParentUUID == fid).ToList();
                var sb = new StringBuilder($"folder {fid}: {kids.Count} entries\n");
                foreach (var k in kids.OrderBy(k => k is InventoryFolder ? 0 : 1).ThenBy(k => k.Name))
                    if (k is InventoryFolder f) sb.AppendLine($"  [folder] '{f.Name}' {f.UUID} type {f.PreferredType}");
                    else if (k is InventoryItem i) sb.AppendLine($"  '{i.Name}' {i.AssetType}/{i.InventoryType}{(i is InventoryWearable w ? "/" + w.WearableType : "")}{(i.IsLink() ? " LINK->" + i.AssetUUID : "")} item {i.UUID} perms next={i.Permissions.NextOwnerMask} desc '{i.Description}' created {i.CreationDate:yyyy-MM-dd HH:mm}");
                return sb.ToString().TrimEnd();
            }
            case "wear":
            {
                if (a.Length < 2 || (a[0] != "add" && a[0] != "remove") || !UUID.TryParse(a[1], out var id)) return "usage: wear add|remove <item uuid> [point]";
                var it = await FetchItemRO(id, ct);
                if (it == null) return $"item {id} not returned by inventory fetch";
                if (it.IsLink()) return $"'{it.Name}' is a link; give the ORIGINAL item id ({it.AssetUUID})";
                if (it.AssetType == AssetType.Bodypart) return $"'{it.Name}' is a body part (would replace); refused";
                bool isObj = it is InventoryObject || it is InventoryAttachment; bool isWear = it is InventoryWearable;
                if (!isObj && !isWear) return $"'{it.Name}' is {it.AssetType}/{it.InventoryType}: not wearable";
                var cof = await CofFolder(ct); if (cof == null) return "Current Outfit folder not found";
                var cofLinks = (await ReadFolderRO(cof.UUID, ct)).OfType<InventoryItem>().Where(l => l.ParentUUID == cof.UUID && l.IsLink()).ToList();
                var mine = cofLinks.Where(l => l.AssetUUID == it.UUID).ToList();
                var sb = new StringBuilder();
                if (a[0] == "add")
                {
                    if (isObj)
                    {
                        var pt = AttachmentPoint.Default; if (a.Length > 2 && int.TryParse(a[2], out var n)) pt = (AttachmentPoint)n;
                        var got = await AttachVerified(new List<(InventoryItem, AttachmentPoint)> { (it, pt) }, ct);
                        sb.AppendLine($"attach (ADD) for '{it.Name}' at {pt}: {got[it.UUID]}");
                    }
                    else
                    {
                        client.Appearance.AddToOutfit(it, false);
                        sb.AppendLine($"clothing layer ADDED: '{it.Name}' ({((InventoryWearable)it).WearableType})");
                    }
                    if (mine.Count > 0) sb.AppendLine($"COF link already there ({mine[0].UUID})");
                    else
                    {
                        string desc = "";
                        // numbered from the wearables in memory (2026-10-08: fetching every COF link took ~22 s per layer)
                        if (it is InventoryWearable w) desc = LayerLinkDescs(new[] { (w.UUID, w.WearableType) }, WornLayerTypes())[w.UUID];
                        InventoryItem made = null; string err = "";
                        try { using var lt = CancellationTokenSource.CreateLinkedTokenSource(ct); lt.CancelAfter(20000); made = await client.Inventory.CreateLinkAsync(cof.UUID, it.UUID, it.Name, desc, it.InventoryType, UUID.Random(), lt.Token); }
                        catch (Exception ex) { err = ex.GetBaseException().Message; }
                        sb.AppendLine(made != null ? $"COF link added {made.UUID} desc '{desc}'" : $"COF LINK FAILED: {err}");
                    }
                    Log("wear", $"wear add '{it.Name}' {it.UUID}: {sb.ToString().Replace("\n", "; ").TrimEnd(' ', ';')}");
                }
                else
                {
                    if (isObj)
                    {
                        // DetachItemAsync sends Detach and removes COF link(s) (same as detach command)
                        sb.AppendLine(await DetachItemAsync(it.UUID, "wear remove"));
                        try { var orphan = await DropOrphanAlphasAfterRemove(it, ct); if (orphan != null) sb.AppendLine(orphan); }
                        catch (Exception ex) { sb.AppendLine("orphan alpha check failed: " + ex.GetBaseException().Message); }
                    }
                    else
                    {
                        client.Appearance.RemoveFromOutfit(it);
                        sb.AppendLine($"clothing layer removed: '{it.Name}'");
                        if (mine.Count == 0) sb.AppendLine("no COF link to remove");
                        else
                        {
                            try { await client.Inventory.RemoveItemsAsync(mine.Select(l => l.UUID), ct); sb.AppendLine($"COF link(s) removed: {string.Join(", ", mine.Select(l => l.UUID))} (the item itself is untouched)"); }
                            catch (Exception ex) { sb.AppendLine("COF link removal FAILED: " + ex.GetBaseException().Message); }
                        }
                    }
                    Log("wear", $"wear remove '{it.Name}' {it.UUID}: {sb.ToString().Replace("\n", "; ").TrimEnd(' ', ';')}");
                }
                return sb.ToString().TrimEnd();
            }
            case "rez":
            {
                if (a.Length < 1 || !UUID.TryParse(a[0], out var id)) return "usage: rez <object item uuid>";
                var it = await FetchItemRO(id, ct);
                if (it == null) return $"item {id} not returned by inventory fetch";
                if (it.IsLink() || it.InventoryType != InventoryType.Object) return $"'{it.Name}' is {it.InventoryType}{(it.IsLink() ? " (link)" : "")}: only original Object items can be rezzed";
                var me = client.Self.SimPosition; var f3 = Vector3.UnitX * client.Self.SimRotation; var fwd = new Vector3(f3.X, f3.Y, 0); if (fwd.Length() < 0.1f) fwd = Vector3.UnitX; fwd = Vector3.Normalize(fwd);
                var pos = new Vector3(me.X + fwd.X * 1.5f, me.Y + fwd.Y * 1.5f, me.Z - 0.3f);
                var before = client.Network.CurrentSim.ObjectsPrimitives.Values.Where(p => p.ParentID == 0).Select(p => p.ID).ToHashSet();
                client.Inventory.RequestRezFromInventory(client.Network.CurrentSim, Quaternion.Identity, pos, it);
                Log("wear", $"rez '{it.Name}' {it.UUID} at {P3(pos)}");
                Primitive got = null;
                for (int i = 0; i < 20 && got == null; i++)
                {
                    await Task.Delay(500, ct);
                    got = client.Network.CurrentSim.ObjectsPrimitives.Values.FirstOrDefault(p => p.ParentID == 0 && !before.Contains(p.ID) && Vector3.Distance(p.Position, pos) < 3f && p.OwnerID == client.Self.AgentID);
                }
                return $"rez sent for '{it.Name}' at {P3(pos)}" + (got != null ? $"; new object {got.ID} local {got.LocalID} at {P3(got.Position)}" : "; no new object of hers seen within 10 s (check 'objects 10')");
            }
            case "take":
            {
                if (a.Length < 1 || !UUID.TryParse(a[0], out var oid)) return "usage: take <object uuid>";
                var p = client.Network.CurrentSim.ObjectsPrimitives.Values.FirstOrDefault(x => x.ID == oid);
                if (p == null) return "object not found in this region's object list";
                if (p.OwnerID != client.Self.AgentID) return $"object {oid} is not hers (owner {p.OwnerID}); refused";
                client.Inventory.RequestDeRezToInventory(p.LocalID, DeRezDestination.AgentInventoryTake, client.Inventory.FindFolderForType(AssetType.Object), UUID.Random());
                Log("wear", $"take {oid} local {p.LocalID} '{p.Properties?.Name}' -> Objects folder");
                return $"take sent for {oid} ('{p.Properties?.Name ?? "?"}') into her Objects folder";
            }
            case "offer":
            {
                if (a.Length >= 1 && a[0] == "off") { offerAllowName = null; offerAllowUntil = DateTime.MinValue; return "offer allow: off"; }
                if (a.Length >= 2 && a[0] == "allow")
                {
                    int mins = 5; var parts = a.Skip(1).ToList();
                    if (parts.Count > 1 && int.TryParse(parts[^1], out var m)) { mins = Math.Clamp(m, 1, 15); parts.RemoveAt(parts.Count - 1); }
                    offerAllowName = string.Join(' ', parts); offerAllowUntil = DateTime.Now.AddMinutes(mins);
                    Log("offer", $"allow task-inventory offers from her own object named '{offerAllowName}' until {offerAllowUntil:HH:mm:ss}");
                    return $"will accept task-inventory offers only from her own object named '{offerAllowName}' until {offerAllowUntil:HH:mm:ss}";
                }
                lock (offerLog) return $"offer allow: {(DateTime.Now < offerAllowUntil ? $"'{offerAllowName}' until {offerAllowUntil:HH:mm:ss}" : "off")}\n" + string.Join("\n", offerLog.TakeLast(10));
            }
        }
        return "?";
    }

    // called from the IM handler for InventoryOffered / TaskInventoryOffered; true = accepted here
    static bool OfferIn(InstantMessage im)
    {
        string line = $"{DateTime.Now:HH:mm:ss} {im.Dialog} from '{im.FromAgentName}' ({im.FromAgentID}) session {im.IMSessionID} bucket {im.BinaryBucket?.Length ?? 0} bytes: '{im.Message}'";
        bool ok = im.Dialog == InstantMessageDialog.TaskInventoryOffered && DateTime.Now < offerAllowUntil && offerAllowName != null
                  && string.Equals(im.FromAgentName, offerAllowName, StringComparison.Ordinal) && im.FromAgentID == client.Self.AgentID
                  && im.BinaryBucket?.Length == 1;
        if (ok)
        {
            var type = (AssetType)im.BinaryBucket[0];
            // a Folder (llGiveInventoryList) goes to the inventory root like in the viewer; FindFolderForType(Unknown) picked '#Firestorm' (21:21 run)
            var folder = type == AssetType.Folder ? client.Inventory.Store.RootFolder.UUID : client.Inventory.FindFolderForType(type);
            if (folder == UUID.Zero) folder = client.Inventory.Store.RootFolder.UUID;
            var imp = new ImprovedInstantMessagePacket();
            imp.AgentData.AgentID = client.Self.AgentID; imp.AgentData.SessionID = client.Self.SessionID;
            imp.MessageBlock.FromGroup = false; imp.MessageBlock.ToAgentID = im.FromAgentID; imp.MessageBlock.Offline = 0;
            imp.MessageBlock.ID = im.IMSessionID; imp.MessageBlock.Timestamp = 0; imp.MessageBlock.FromAgentName = Utils.StringToBytes(client.Self.Name);
            imp.MessageBlock.Message = Utils.EmptyBytes; imp.MessageBlock.ParentEstateID = 0; imp.MessageBlock.RegionID = UUID.Zero;
            imp.MessageBlock.Position = client.Self.SimPosition; imp.MessageBlock.Dialog = (byte)InstantMessageDialog.TaskInventoryAccepted;
            imp.MessageBlock.BinaryBucket = folder.GetBytes();
            client.Network.SendPacket(imp, client.Network.CurrentSim);
            line += $" -> ACCEPTED (type {type}, into folder {folder})";
            offerAllowUntil = DateTime.MinValue; // one offer only
        }
        else line += " -> IGNORED";
        lock (offerLog) offerLog.Add(line);
        Log("offer", line);
        return ok;
    }
}
