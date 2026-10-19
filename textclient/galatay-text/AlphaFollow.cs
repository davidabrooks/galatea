// AlphaFollow.cs (2026-10-19, David): when a clothing item comes off (RLV remoutfit/detach from his HUD, undress, toilet,
// detach command), an alpha layer / alpha attachment that belongs to it comes off too. Tie = same inventory folder as the
// removed item, or the alpha's name minus "alpha" is the removed item's name (e.g. 'X' + 'X Alpha'). Body, head and skin
// alphas without such a tie, protected items and the collar never come off. Decisions go to the RLV log.
// Also the tattoo-layer classifier (clothing tattoos such as panties are removable; skin/makeup/body tattoos are not).
using System.Text.RegularExpressions;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    internal sealed record AlphaCand(UUID Item, string Name, UUID Folder);
    static readonly Regex AlphaWordRx = new(@"\balpha\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex BodyAlphaRx = new(@"\b(body|head|skin|neck\s*fix|evo\s*x|lel|lelutka|maitreya|lara\s*x?|kay|calista|misaki|bom|bakes|hands?|feet|eyes?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex TattooProtectRx = new(@"\b(skin|make\s*up|makeup|body|brows?|eyebrows?|lips?|lipstick|freckles?|blush|liner|lash(es)?|shadow|moles?|scars?|tan|bom|bakes|base|essentials?|head|face|nails?|neck\s*fix|hairbase|eyes?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex ClothingWordRx = new(@"\b(underwear|panties|panty|pantie|bra|bras|thong|briefs?|knickers|lingerie|bikini|g-?string|stockings?|socks?|tights|garter|corset|shirt|top|dress|skirt|shorts|pants|jeans|bottoms?|swimsuit|outfit|clothing|clothes)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static string AlphaStem(string n) => Regex.Replace(AlphaWordRx.Replace(n ?? "", " "), @"[^\p{L}\p{N}]+", " ").Trim().ToLowerInvariant();

    // pure (tested): alphas that belong to the removed items
    internal static List<(AlphaCand alpha, string why)> AlphasTiedTo(IEnumerable<AlphaCand> removed, IEnumerable<AlphaCand> wornAlphas, ISet<UUID> protectedIds)
    {
        var rem = removed.ToList(); var res = new List<(AlphaCand, string)>();
        foreach (var a in wornAlphas)
        {
            if (protectedIds.Contains(a.Item) || rem.Any(r => r.Item == a.Item)) continue;
            var sameFolder = rem.FirstOrDefault(r => r.Folder != UUID.Zero && r.Folder == a.Folder);
            if (sameFolder != null) { res.Add((a, $"same folder as '{sameFolder.Name}'")); continue; }
            if (BodyAlphaRx.IsMatch(a.Name)) continue;   // body/head/skin alpha with no folder tie stays
            var st = AlphaStem(a.Name); if (st.Length < 3) continue;
            var byName = rem.FirstOrDefault(r => { var rs = AlphaStem(r.Name); return rs == st || (rs.Length >= 3 && (rs.StartsWith(st + " ") || st.StartsWith(rs + " "))); });
            if (byName != null) res.Add((a, $"name matches '{byName.Name}'"));
        }
        return res;
    }

    // pure (tested): a tattoo/alpha layer that is a garment (removable), not skin/makeup/body
    internal static bool IsClothingLayer(string name, string folderName) =>
        !TattooProtectRx.IsMatch(name ?? "") && (ClothingWordRx.IsMatch(name ?? "") || (!TattooProtectRx.IsMatch(folderName ?? "") && ClothingWordRx.IsMatch(folderName ?? "")));

    static readonly AsyncLocal<bool> alphaFollowing = new();
    static void AlphaFollowAfterRemove(IEnumerable<UUID> removedItems, string why)
    {
        if (alphaFollowing.Value) return;
        var ids = removedItems.Where(i => i != UUID.Zero).Distinct().ToList(); if (ids.Count == 0) return;
        _ = Task.Run(async () =>
        {
            alphaFollowing.Value = true;
            try
            {
                using var cts = new CancellationTokenSource(60000); var ct = cts.Token;
                await Task.Delay(1500, ct);
                var removed = new List<AlphaCand>();
                foreach (var id in ids) { var it = await FetchItemRO(id, ct); if (it != null) removed.Add(new(it.UUID, it.Name, it.ParentUUID)); }
                if (removed.Count == 0 || removed.All(r => AlphaWordRx.IsMatch(r.Name))) return;
                var cands = new List<AlphaCand>(); var layerIds = new HashSet<UUID>();
                foreach (var w in SafeWearables().Where(w => w.WearableType == WearableType.Alpha).GroupBy(w => w.ItemID).Select(g => g.First()))
                { var it = await FetchItemRO(w.ItemID, ct); if (it != null) { cands.Add(new(it.UUID, it.Name, it.ParentUUID)); layerIds.Add(it.UUID); } }
                foreach (var p in WornPrims())
                {
                    var n = p.Properties?.Name ?? ""; var id = AttachItemId(p);
                    if (id == UUID.Zero || !AlphaWordRx.IsMatch(n)) continue;
                    var it = await FetchItemRO(id, ct); cands.Add(new(id, n, it?.ParentUUID ?? UUID.Zero));
                }
                var hits = AlphasTiedTo(removed, cands, OutfitProtectedIds());
                RlvLog($"alpha follow ({why}): removed {string.Join(", ", removed.Select(r => "'" + r.Name + "'"))}; tied alphas: " +
                       (hits.Count == 0 ? "none" : string.Join(", ", hits.Select(h => $"'{h.alpha.Name}' ({h.why})"))) +
                       $"; kept: {string.Join(", ", cands.Where(c => hits.All(h => h.alpha.Item != c.Item)).Select(c => "'" + c.Name + "'"))}");
                if (hits.Count == 0) return;
                var layers = new List<InventoryItem>();
                foreach (var (a, _) in hits)
                    if (layerIds.Contains(a.Item)) { var it = await FetchItemRO(a.Item, ct); if (it != null) layers.Add(it); }
                    else DetachItem(a.Item, "alpha follow");
                if (layers.Count > 0) { client.Appearance.RemoveFromOutfit(layers); await RebakeNow("alpha follow"); }
                await CofLinksOffBatch(hits.Select(h => h.alpha.Item), "alpha follow", ct);
            }
            catch (Exception ex) { RlvLog("alpha follow failed: " + ex.GetBaseException().Message); }
        });
    }
}
