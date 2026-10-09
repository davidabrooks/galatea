// Toilet.cs (2026-10-08, David: "When you sit on it, choose female poses and take off whatever you're wearing on your legs
// when you use it"). Home seat special "toilet" (BackBone Playtime Toilet): before sitting take off the lower-body clothing
// (pants / skirts / shorts / panties / bikini bottoms + the leg-hiding alpha layers) and wear The V + its HUD (top stays on,
// no nipple rings); pose = random female button (never "(m)", Vomit, [ADJUST], Texture); after standing re-add exactly what
// came off and take The V + HUD off again (only if the toilet put them on).
using System.Text;
using System.Text.RegularExpressions;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    internal static readonly UUID TheVHudItem = new("8b537b4b-009a-395d-95d7-70945f1b4a6b");

    static readonly Regex LowerClothingNameRx = new(@"(?<![a-z])(jeans|pants|trousers|leggings|shorts|cutoffs|capris?|skirt|panties|panty|thong|briefs|knickers|underwear|bottoms?)(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex NotLowerNameRx = new(@"(?<![a-z])(top|shirt|t-shirt|tee|bra|jacket|sweater|hoodie|dress|boots|shoes|heels|sandals|hud)(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex LegAlphaNameRx = new(@"(?<![a-z])(legs?|knees?|thighs?|calf|calves|butt|bum|booty|hips?|pelvis|crotch|groin)(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex ToiletNotFemaleRx = new(@"\(\s*m\s*\)|(?<![a-z])(male|vomit|adjust|texture|options?|back|stop|swap|sync)(?![a-z])|^\s*\[|<<|>>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // pure: is this worn attachment lower-body clothing that comes off on the toilet?
    internal static bool ToiletLowerAttachment(string name, AttachmentPoint pt)
    {
        name ??= "";
        if (OutfitGroup(name) != null || name.StartsWith("The V", StringComparison.OrdinalIgnoreCase)) return false;
        if (LowerClothingNameRx.IsMatch(name) && !Regex.IsMatch(name, @"(?<![a-z])(top|bra|hud)(?![a-z])", RegexOptions.IgnoreCase)) return true;
        bool lowerPt = pt is AttachmentPoint.Pelvis or AttachmentPoint.LeftHip or AttachmentPoint.RightHip
                          or AttachmentPoint.LeftUpperLeg or AttachmentPoint.RightUpperLeg or AttachmentPoint.LeftLowerLeg or AttachmentPoint.RightLowerLeg;
        return lowerPt && ClothingNameRx.IsMatch(name) && !NotLowerNameRx.IsMatch(name);
    }

    // pure: is this worn clothing layer lower-body (pants / skirt / underpants, or a leg / butt alpha)?
    internal static bool ToiletLowerLayer(WearableType t, string name) =>
        t is WearableType.Pants or WearableType.Skirt or WearableType.Underpants
        || (t == WearableType.Alpha && (LegAlphaNameRx.IsMatch(name ?? "") || LowerClothingNameRx.IsMatch(name ?? ""))); // '<Alpha mask> Chill Shorts - Maitreya' too

    // pure: the female pose buttons of the toilet menu
    internal static List<string> ToiletFemaleButtons(IEnumerable<string> buttons) =>
        (buttons ?? Enumerable.Empty<string>()).Where(b => !string.IsNullOrWhiteSpace(b) && !ToiletNotFemaleRx.IsMatch(b)).ToList();

    sealed record ToiletUndressState(List<(UUID id, AttachmentPoint pt, string name)> Atts, List<(UUID id, string name)> Layers, List<UUID> Added);
    static ToiletUndressState pendingToilet;

    // 2026-10-08 18:36 (David: long delay before the legs alpha comes off): layers + jeans in one batch, COF links in one
    // call, bake right away (FastLayers.UndressOrder); The V + HUD and the orphan-alpha check after that.
    static async Task<string> ToiletUndress(CancellationToken ct)
    {
        var atts = new List<(UUID, AttachmentPoint, string)>(); var layers = new List<(UUID, string)>(); var added = new List<UUID>();
        var layerItems = new List<InventoryItem>();
        var sb = new StringBuilder(); var t0 = DateTime.Now;
        outfitChangeUntil = DateTime.Now.AddSeconds(60);
        try
        {
            var prot = OutfitProtectedIds();
            var roots = WornPrims(); await EnsureProperties(Sim, roots);
            foreach (var r in roots)
            {
                if (IsHudAttachPoint(r.PrimData.AttachmentPoint)) continue;
                var id = AttachItemId(r); var nm = r.Properties?.Name ?? "";
                if (id != UUID.Zero && !prot.Contains(id) && ToiletLowerAttachment(nm, r.PrimData.AttachmentPoint) && !atts.Any(a => a.Item1 == id)) atts.Add((id, r.PrimData.AttachmentPoint, nm));
            }
            List<AppearanceManager.WearableData> cur; try { cur = client.Appearance.GetWearables().ToList(); } catch { cur = new(); }
            var alphasBefore = cur.Where(w => w.WearableType == WearableType.Alpha).Select(w => w.ItemID).ToHashSet();
            foreach (var w in cur.GroupBy(w => w.ItemID).Select(x => x.First()))
            {
                var it = await FetchItemRO(w.ItemID, ct);
                if (it != null && ToiletLowerLayer(w.WearableType, it.Name)) { layers.Add((it.UUID, it.Name)); layerItems.Add(it); }
            }
            pendingToilet = new ToiletUndressState(atts, layers, added); // set first: a failure below still restores
            var worn = WornPrims().Select(AttachItemId).ToHashSet();
            var extras = new[] { TheVItem, TheVHudItem }.Where(v => !worn.Contains(v)).ToList();
            foreach (var step in UndressOrder(atts.Count, layers.Count, extras.Count, orphanCheck: true))
                switch (step)
                {
                    case LegStep.LayersOff: client.Appearance.RemoveFromOutfit(layerItems); foreach (var (_, nm) in layers) sb.Append($"layer off '{nm}'; "); break;
                    case LegStep.DetachClothing: foreach (var (id, _, nm) in atts) { DetachItem(id, "toilet undress"); sb.Append($"off '{nm}'; "); } break;
                    case LegStep.CofLinksOff: await CofLinksOffBatch(atts.Select(a => a.Item1).Concat(layers.Select(l => l.Item1)), "toilet undress", ct); break;
                    case LegStep.RebakeNow: await RebakeNow("toilet undress"); sb.Append($"bake asked at +{(DateTime.Now - t0).TotalSeconds:F1} s; "); break;
                    case LegStep.ExtrasOn:
                        foreach (var v in extras)
                        {
                            var r = await WearOpsCmd("wear", new[] { "add", v.ToString() });
                            added.Add(v); sb.Append($"on {v.ToString()[..8]} ({r.Split('\n')[0]}); ");
                        }
                        break;
                    case LegStep.OrphanCheck:
                        // an outfit-orphan alpha that belonged to the clothing also comes off now (and back afterwards)
                        foreach (var (id, _, _) in atts)
                        {
                            try { var inv = await FetchItemRO(id, ct); if (inv != null) await DropOrphanAlphasAfterRemove(inv, ct); } catch { }
                        }
                        try
                        {
                            var alphasAfter = client.Appearance.GetWearables().Where(w => w.WearableType == WearableType.Alpha).Select(w => w.ItemID).ToHashSet();
                            foreach (var gone in alphasBefore.Where(a => !alphasAfter.Contains(a) && !layers.Any(l => l.Item1 == a)))
                            { var it = await FetchItemRO(gone, ct); layers.Add((gone, it?.Name ?? gone.ToString())); sb.Append($"orphan alpha off '{it?.Name}'; "); }
                        }
                        catch { }
                        break;
                }
            await Task.Delay(1000, ct);
        }
        finally { outfitChangeUntil = DateTime.Now.AddSeconds(15); }
        var res = sb.Length == 0 ? "nothing on the legs" : sb.ToString().TrimEnd(' ', ';');
        WLog($"TOILET undress ({(DateTime.Now - t0).TotalSeconds:F0} s): " + res);
        return res;
    }

    // 2026-10-08 18:37 (David: long delay after the toilet before washing hands): was 66 s, one 'wear add' per alpha each
    // reading every COF link. Now jeans + all layers in one batch, COF links in one call, The V off, bake right away.
    static async Task ToiletRestoreIfPending()
    {
        var st = pendingToilet; if (st == null || client.Self.SittingOn != 0) return;
        pendingToilet = null;
        var sb = new StringBuilder(); var t0 = DateTime.Now;
        outfitChangeUntil = DateTime.Now.AddSeconds(60);
        try
        {
            using var cts = new CancellationTokenSource(60000); var ct = cts.Token;
            var wornBefore = WornLayerTypes();
            var attInv = new List<(InventoryItem, AttachmentPoint)>(); var layerInv = new List<InventoryItem>();
            foreach (var (id, pt, _) in st.Atts) { var it = await FetchItemRO(id, ct); if (it != null) attInv.Add((it, pt)); }
            foreach (var (id, _) in st.Layers) { var it = await FetchItemRO(id, ct); if (it is InventoryWearable) layerInv.Add(it); }
            if (layerInv.Count > 0) { client.Appearance.AddToOutfit(layerInv, false); sb.Append($"layers on: {string.Join(", ", layerInv.Select(l => "'" + l.Name + "'"))}; "); }
            foreach (var (it, pt) in attInv) client.Appearance.Attach(it, pt, false);
            foreach (var v in st.Added) DetachItem(v, "toilet restore");
            var descs = LayerLinkDescs(layerInv.OfType<InventoryWearable>().Select(w => (w.UUID, w.WearableType)), wornBefore);
            sb.Append(await CofLinksOnBatch(layerInv.Concat(attInv.Select(a => a.Item1)).ToList(), descs, "toilet restore", ct) + "; ");
            if (st.Added.Count > 0) sb.Append(await CofLinksOffBatch(st.Added, "toilet restore", ct) + "; ");
            sb.Append(await RebakeNow("toilet restore") + $" at +{(DateTime.Now - t0).TotalSeconds:F1} s; ");
            // verify (re-send once): the jeans attach, and layers a stale wearables list may have dropped (COF only)
            if (attInv.Count > 0)
            {
                await WaitWorn(attInv.Select(a => a.Item1.UUID).ToList(), 8000, ct);
                var seen = WornPrims().Select(AttachItemId).ToHashSet();
                foreach (var (it, pt) in attInv)
                    if (seen.Contains(it.UUID)) sb.Append($"on '{it.Name}'; ");
                    else { client.Appearance.Attach(it, pt, false); sb.Append($"'{it.Name}' re-sent; "); }
            }
            HashSet<UUID> have; try { have = client.Appearance.GetWearables().Select(w => w.ItemID).ToHashSet(); } catch { have = new(); }
            var miss = layerInv.Where(l => !have.Contains(l.UUID)).ToList();
            if (miss.Count > 0) { client.Appearance.AddToOutfit(miss, false); sb.Append($"layer retry on: {string.Join(", ", miss.Select(m => "'" + m.Name + "'"))}; "); await RebakeNow("toilet restore retry"); }
        }
        catch (Exception ex) { sb.Append("FAILED: " + ex.GetBaseException().Message); }
        finally { outfitChangeUntil = DateTime.Now.AddSeconds(15); }
        WLog($"TOILET restore after standing ({(DateTime.Now - t0).TotalSeconds:F0} s): " + sb.ToString().TrimEnd(' ', ';'));
    }
}
