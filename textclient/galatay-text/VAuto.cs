// VAuto.cs (2026-10-19, David): when nothing covers her groin any more (RLV/strip, undress, outfit change), The V goes on;
// when a cover goes back on, The V comes off again unless the outfit is Naked or itself includes The V. Never in android
// outfits. 'redress' (MCP) puts back the outfit she wore before the strip (last named outfit), which also takes The V off
// and brings that outfit's own alphas back. Decisions go to the RLV log.
using System.Text.RegularExpressions;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    static readonly Regex GroinCoverRx = new(@"\b(shorts|skirt|dress|gown|pants|jeans|trousers|leggings|panties|panty|pantie|thong|briefs?|knickers|underwear|bottoms?|bikini|swimsuit|bodysuit|onesie|romper|jumpsuit|lingerie|g-?string|robe)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    internal static volatile bool lastOutfitHasV; static volatile string currentOutfit;

    // pure (tested): does anything worn cover the groin? attachments by name (HUDs excluded by the caller), layers by type,
    // tattoo layers only when they are clothing (panties)
    internal static bool GroinCovered(IEnumerable<string> attachmentNames, IEnumerable<(WearableType t, string name)> layers) =>
        attachmentNames.Any(n => GroinCoverRx.IsMatch(n ?? "") && !(n ?? "").Contains("HUD", StringComparison.OrdinalIgnoreCase))
        || layers.Any(l => l.t is WearableType.Underpants or WearableType.Pants or WearableType.Skirt
                           || (l.t == WearableType.Tattoo && IsClothingLayer(l.name, "") && GroinCoverRx.IsMatch(l.name ?? "")));

    // pure (tested): "wear" / "detach" / null
    internal static string VDecision(bool covered, bool vWorn, string outfit, bool outfitHasV)
    {
        if (IsAndroidOutfit(outfit)) return null;
        if (!covered && !vWorn) return "wear";
        if (covered && vWorn && !outfitHasV && !Regex.IsMatch(outfit ?? "", @"^\s*naked\b", RegexOptions.IgnoreCase)) return "detach";
        return null;
    }

    static int vGen;
    static void VAutoCheckSoon(string why)
    {
        int g = Interlocked.Increment(ref vGen);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(4000); if (g != vGen || !LoggedIn) return;   // debounce a burst of removals
                using var cts = new CancellationTokenSource(40000);
                var roots = WornPrims(); await EnsureProperties(Sim, roots);
                var atts = roots.Where(r => !IsHudAttachPoint(r.PrimData.AttachmentPoint)).Select(r => r.Properties?.Name ?? "").ToList();
                var layers = new List<(WearableType, string)>();
                foreach (var w in SafeWearables().Where(w => !IsBodyPartType(w.WearableType)).GroupBy(w => w.ItemID).Select(x => x.First()))
                { var it = await FetchItemRO(w.ItemID, cts.Token); layers.Add((w.WearableType, it?.Name ?? "")); }
                bool covered = GroinCovered(atts, layers);
                bool vWorn = roots.Any(r => AttachItemId(r) == TheVItem);
                var outfit = currentOutfit ?? lastNamedOutfit ?? "";
                var d = VDecision(covered, vWorn, outfit, lastOutfitHasV);
                RlvLog($"The V ({why}): groin {(covered ? "covered" : "uncovered")}, The V {(vWorn ? "worn" : "off")}, outfit '{outfit}'{(lastOutfitHasV ? " (includes The V)" : "")} -> {d ?? "no change"}");
                if (d == "wear") RlvLog("The V: " + await Exec("wear add " + TheVItem));
                else if (d == "detach") RlvLog("The V: " + await DetachItemAsync(TheVItem, "the v auto"));
            }
            catch (Exception ex) { RlvLog("The V check failed: " + ex.GetBaseException().Message); }
        });
    }

    static async Task<string> RedressCmd()
    {
        var name = lastNamedOutfit; if (IsAndroidOutfit(currentOutfit) is false && !string.IsNullOrWhiteSpace(currentOutfit) && !Regex.IsMatch(currentOutfit, @"^\s*naked\b", RegexOptions.IgnoreCase)) name = currentOutfit;
        if (string.IsNullOrWhiteSpace(name)) return "no outfit to go back to (none worn by name yet)";
        RlvLog($"redress: back into '{name}'");
        return await WearOutfitWithHuds(name, keepColor: true);
    }
}
