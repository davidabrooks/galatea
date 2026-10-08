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
        t is WearableType.Pants or WearableType.Skirt or WearableType.Underpants || (t == WearableType.Alpha && LegAlphaNameRx.IsMatch(name ?? ""));

    // pure: the female pose buttons of the toilet menu
    internal static List<string> ToiletFemaleButtons(IEnumerable<string> buttons) =>
        (buttons ?? Enumerable.Empty<string>()).Where(b => !string.IsNullOrWhiteSpace(b) && !ToiletNotFemaleRx.IsMatch(b)).ToList();

    sealed record ToiletUndressState(List<(UUID id, AttachmentPoint pt, string name)> Atts, List<(UUID id, string name)> Layers, List<UUID> Added);
    static ToiletUndressState pendingToilet;

    static async Task<string> ToiletUndress(CancellationToken ct)
    {
        var atts = new List<(UUID, AttachmentPoint, string)>(); var layers = new List<(UUID, string)>(); var added = new List<UUID>();
        var sb = new StringBuilder();
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
                if (it != null && ToiletLowerLayer(w.WearableType, it.Name)) layers.Add((it.UUID, it.Name));
            }
            pendingToilet = new ToiletUndressState(atts, layers, added); // set first: a failure below still restores
            foreach (var (id, _, nm) in atts) { await WearOpsCmd("wear", new[] { "remove", id.ToString() }); sb.Append($"off '{nm}'; "); }
            foreach (var (id, nm) in layers) { await WearOpsCmd("wear", new[] { "remove", id.ToString() }); sb.Append($"layer off '{nm}'; "); }
            // an outfit-orphan alpha dropped by 'wear remove' also comes back afterwards
            try
            {
                var alphasAfter = client.Appearance.GetWearables().Where(w => w.WearableType == WearableType.Alpha).Select(w => w.ItemID).ToHashSet();
                foreach (var gone in alphasBefore.Where(a => !alphasAfter.Contains(a) && !layers.Any(l => l.Item1 == a)))
                { var it = await FetchItemRO(gone, ct); layers.Add((gone, it?.Name ?? gone.ToString())); sb.Append($"orphan alpha off '{it?.Name}'; "); }
            }
            catch { }
            var worn = WornPrims().Select(AttachItemId).ToHashSet();
            foreach (var v in new[] { TheVItem, TheVHudItem })
            {
                if (worn.Contains(v)) continue;
                var r = await WearOpsCmd("wear", new[] { "add", v.ToString() });
                added.Add(v); sb.Append($"on {v.ToString()[..8]} ({r.Split('\n')[0]}); ");
            }
            await Task.Delay(1500, ct);
        }
        finally { outfitChangeUntil = DateTime.Now.AddSeconds(15); }
        var res = sb.Length == 0 ? "nothing on the legs" : sb.ToString().TrimEnd(' ', ';');
        WLog("TOILET undress: " + res);
        return res;
    }

    static async Task ToiletRestoreIfPending()
    {
        var st = pendingToilet; if (st == null || client.Self.SittingOn != 0) return;
        pendingToilet = null;
        var sb = new StringBuilder();
        outfitChangeUntil = DateTime.Now.AddSeconds(60);
        try
        {
            foreach (var (id, pt, nm) in st.Atts) sb.Append($"on '{nm}': {(await WearOpsCmd("wear", new[] { "add", id.ToString(), ((int)pt).ToString() })).Split('\n')[0]}; ");
            foreach (var (id, nm) in st.Layers) { await WearOpsCmd("wear", new[] { "add", id.ToString() }); sb.Append($"layer on '{nm}'; "); }
            foreach (var v in st.Added) sb.Append($"off {v.ToString()[..8]}: {(await WearOpsCmd("wear", new[] { "remove", v.ToString() })).Split('\n')[0]}; ");
        }
        catch (Exception ex) { sb.Append("FAILED: " + ex.GetBaseException().Message); }
        finally { outfitChangeUntil = DateTime.Now.AddSeconds(15); }
        WLog("TOILET restore after standing: " + sb.ToString().TrimEnd(' ', ';'));
    }
}
