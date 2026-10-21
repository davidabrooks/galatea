// CofPurge.cs (2026-10-21, David): after the box move, three Bimbette leg alphas stayed LINKED in Current Outfit although
// the client no longer listed them as worn, so the server bake kept her thighs/knees invisible. 'cof purge [outfit]'
// removes Current Outfit alpha-layer links that the saved outfit (default: the current one) does not contain, then rebakes.
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    internal sealed record CofLinkInfo(UUID Link, UUID Target, string Name, bool IsAlpha);
    // pure (tested): COF alpha links whose target is not in the saved outfit
    internal static List<CofLinkInfo> CofStrayAlphaLinks(IEnumerable<CofLinkInfo> cof, ISet<UUID> outfitTargets) =>
        cof.Where(l => l.IsAlpha && !outfitTargets.Contains(l.Target)).ToList();

    static async Task<string> CofPurgeCmd(string outfitName)
    {
        using var cts = new CancellationTokenSource(90000); var ct = cts.Token;
        var name = string.IsNullOrWhiteSpace(outfitName) ? currentOutfit : outfitName.Trim();
        if (string.IsNullOrEmpty(name)) return "cof purge: no current outfit known; give the outfit name";
        var folder = await FindOutfitFolder(name, ct); if (folder == null) return $"cof purge: outfit '{name}' not found";
        var targets = (await ReadFolderRO(folder.UUID, ct)).OfType<InventoryItem>().Select(i => i.IsLink() ? i.AssetUUID : i.UUID).ToHashSet();
        var cof = await CofFolder(ct); if (cof == null) return "cof purge: Current Outfit folder not found";
        var infos = new List<CofLinkInfo>();
        foreach (var l in (await ReadFolderRO(cof.UUID, ct, 30000)).OfType<InventoryItem>().Where(i => i.ParentUUID == cof.UUID && i.IsLink() && i.InventoryType == InventoryType.Wearable))
        {
            var t = await FetchItemRO(l.AssetUUID, ct);
            infos.Add(new CofLinkInfo(l.UUID, l.AssetUUID, l.Name, t is InventoryWearable w && w.WearableType == WearableType.Alpha));
        }
        var stray = CofStrayAlphaLinks(infos, targets);
        if (stray.Count > 0)
        {
            try { var items = new List<InventoryItem>(); foreach (var s in stray) if (await FetchItemRO(s.Target, ct) is InventoryItem it) items.Add(it); if (items.Count > 0) client.Appearance.RemoveFromOutfit(items); } catch { }
            await client.Inventory.RemoveItemsAsync(stray.Select(s => s.Link), ct);
        }
        string bake; try { await client.Appearance.RequestSetAppearance(true); bake = "rebake requested"; } catch (Exception ex) { bake = "rebake failed: " + ex.GetBaseException().Message; }
        var msg = $"cof purge vs '{folder.Name}': {infos.Count(i => i.IsAlpha)} alpha link(s) in Current Outfit, removed {stray.Count}{(stray.Count > 0 ? ": " + string.Join(", ", stray.Select(s => "'" + s.Name + "'")) : "")}; {bake}";
        Log("outfit", msg); return msg;
    }
}
