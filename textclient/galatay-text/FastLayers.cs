// FastLayers.cs (2026-10-08, David 18:36/18:37 PT: "there's a long delay before your legs alpha comes off" / "a long delay
// after you get up from the toilet before you go wash your hands").
// Cause: leg alphas came off / went on one 'wear remove|add' at a time, each one resetting LibreMetaverse's 5 s rebake
// debounce (so the bake that actually hides/shows them only went out after The V was on: 17 s after the jeans on the
// toilet, 8 s before the tub), and every 'wear add' of a layer fetched each COF link (~40) one by one just to number its
// link description (~22 s per alpha, 66 s for three after the toilet).
// Now: every layer goes in ONE RemoveFromOutfit / AddToOutfit, in the same moment as the clothing attachments, the COF
// links go in one batch (descriptions numbered from the wearables already in memory, no inventory reads) and the bake is
// requested right away, before the undress extras (The V, its HUD, rings) or anything slow.
using System.Text;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    internal enum LegStep { LayersOff, DetachClothing, CofLinksOff, RebakeNow, ExtrasOn, OrphanCheck }

    // Pure: the order of an undress (toilet / tub / 'undress'): the alpha layers come off in the same batch as the clothing
    // attachments, the COF links in one go, then the bake right away; the extras (The V, HUD, rings) and the slow orphan
    // check only after the bake request.
    internal static List<LegStep> UndressOrder(int attachments, int layers, int extras, bool orphanCheck)
    {
        var s = new List<LegStep>();
        if (layers > 0) s.Add(LegStep.LayersOff);
        if (attachments > 0) s.Add(LegStep.DetachClothing);
        if (layers > 0 || attachments > 0) s.Add(LegStep.CofLinksOff);
        if (layers > 0) s.Add(LegStep.RebakeNow);
        if (extras > 0) s.Add(LegStep.ExtrasOn);
        if (orphanCheck && attachments > 0) s.Add(LegStep.OrphanCheck);
        return s;
    }

    // Pure: COF link descriptions for layers being added ("@<type*100 + n>", n counting the layers of that type already
    // worn, then the new ones in order) from the wearables in memory: no inventory fetch per COF link.
    internal static Dictionary<UUID, string> LayerLinkDescs(IEnumerable<(UUID id, WearableType type)> adding, IEnumerable<(UUID id, WearableType type)> wornBefore)
    {
        var add = adding.GroupBy(a => a.id).Select(g => g.First()).ToList();
        var addIds = add.Select(a => a.id).ToHashSet();
        var count = wornBefore.Where(w => !addIds.Contains(w.id)).GroupBy(w => w.id).Select(g => g.First())
                              .GroupBy(w => w.type).ToDictionary(g => g.Key, g => g.Count());
        var res = new Dictionary<UUID, string>();
        foreach (var (id, type) in add)
        {
            count.TryGetValue(type, out var n);
            res[id] = $"@{(int)type * 100 + n}";
            count[type] = n + 1;
        }
        return res;
    }

    static List<(UUID, WearableType)> WornLayerTypes()
    {
        try { return client.Appearance.GetWearables().Select(w => (w.ItemID, w.WearableType)).ToList(); } catch { return new(); }
    }

    // remove every COF link pointing at these items in ONE inventory call (one COF read)
    static async Task<string> CofLinksOffBatch(IEnumerable<UUID> items, string why, CancellationToken ct)
    {
        var want = items.Where(i => i != UUID.Zero).ToHashSet(); if (want.Count == 0) return "no COF links to remove";
        try
        {
            var cof = await CofFolder(ct); if (cof == null) return "Current Outfit folder not found";
            var links = (await ReadFolderRO(cof.UUID, ct)).OfType<InventoryItem>().Where(l => l.ParentUUID == cof.UUID && l.IsLink() && want.Contains(l.AssetUUID)).ToList();
            if (links.Count == 0) return "no COF links to remove";
            await client.Inventory.RemoveItemsAsync(links.Select(l => l.UUID), ct);
            Log("wear", $"COF link(s) removed in one batch ({why}): {links.Count} for {want.Count} item(s)");
            return $"{links.Count} COF link(s) removed in one batch";
        }
        catch (Exception ex) { return "COF batch removal FAILED: " + ex.GetBaseException().Message; }
    }

    // add the missing COF links for these items in parallel (layers numbered by LayerLinkDescs)
    static async Task<string> CofLinksOnBatch(IList<InventoryItem> items, IReadOnlyDictionary<UUID, string> descs, string why, CancellationToken ct)
    {
        if (items.Count == 0) return "no COF links to add";
        try
        {
            var cof = await CofFolder(ct); if (cof == null) return "Current Outfit folder not found";
            var have = (await ReadFolderRO(cof.UUID, ct)).OfType<InventoryItem>().Where(l => l.ParentUUID == cof.UUID && l.IsLink()).Select(l => l.AssetUUID).ToHashSet();
            var todo = items.Where(i => !have.Contains(i.UUID)).ToList();
            using var lt = CancellationTokenSource.CreateLinkedTokenSource(ct); lt.CancelAfter(20000);
            var made = await Task.WhenAll(todo.Select(async it =>
            {
                try { return await client.Inventory.CreateLinkAsync(cof.UUID, it.UUID, it.Name, descs != null && descs.TryGetValue(it.UUID, out var d) ? d : "", it.InventoryType, UUID.Random(), lt.Token) != null; }
                catch { return false; }
            }));
            var r = $"{made.Count(m => m)}/{todo.Count} COF link(s) added in one batch{(items.Count > todo.Count ? $" ({items.Count - todo.Count} already linked)" : "")}";
            Log("wear", $"{r} ({why})");
            return r;
        }
        catch (Exception ex) { return "COF batch add FAILED: " + ex.GetBaseException().Message; }
    }

    // ask the sim for the bake now (not 5 s after the last change): this is what makes alpha layers appear / disappear
    static async Task<string> RebakeNow(string why)
    {
        try { await client.Appearance.RequestSetAppearance(true); Log("wear", $"bake requested right away ({why})"); return "bake requested"; }
        catch (Exception ex) { return "bake request failed: " + ex.GetBaseException().Message; }
    }
}
