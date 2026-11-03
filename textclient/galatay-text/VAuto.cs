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
    internal static readonly UUID NippleRingsItem = new("64f6948c-acb3-3a32-9509-3ab1923f2146"); // '[BB] Nipple Rings - LaraX Petite (Puffy)', the one the Naked outfit links
    static readonly Regex ChestCoverRx = new(@"\b(tops?|bra|bras|bralette|shirt|t-?shirt|tee|tube|tank|crop|blouse|camisole|cami|dress|gown|bikini|swimsuit|bodysuit|sweater|hoodie|jacket|cardigan|corset|bustier|halter|robe|lingerie|onesie|jumpsuit|romper|coat)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    internal static volatile bool lastOutfitHasRings, lastOutfitHasV;
    static volatile string preStripOutfit;   // the outfit worn when a strip started; 'redress' and 'get dressed' go back to it

    // 2026-11-02 (David, toilet undress): 'TETRA - Spicy Bikini Top' matched "bikini" and counted as a groin cover, so The V
    // came off on the toilet. A piece named as a top only covers the chest, a piece named as a bottom only the groin.
    static readonly Regex TopOnlyRx = new(@"\b(tops?|bra|bras|bralette|tube|crop|halter|bustier|pasties)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex BottomOnlyRx = new(@"\b(bottoms?|panties|panty|pantie|thong|briefs?|knickers|g-?string|shorts|skirt|pants|jeans|trousers|leggings)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    internal static bool PieceCoversGroin(string n) => GroinCoverRx.IsMatch(n ?? "") && !(TopOnlyRx.IsMatch(n ?? "") && !BottomOnlyRx.IsMatch(n ?? ""));
    internal static bool PieceCoversChest(string n) => ChestCoverRx.IsMatch(n ?? "") && !(BottomOnlyRx.IsMatch(n ?? "") && !TopOnlyRx.IsMatch(n ?? ""));
    // pure (tested): does anything worn cover the chest?
    internal static bool ChestCovered(IEnumerable<string> attachmentNames, IEnumerable<(WearableType t, string name)> layers) =>
        attachmentNames.Any(n => PieceCoversChest(n) && !(n ?? "").Contains("HUD", StringComparison.OrdinalIgnoreCase))
        || layers.Any(l => l.t is WearableType.Shirt or WearableType.Undershirt or WearableType.Jacket
                           || (l.t == WearableType.Tattoo && IsClothingLayer(l.name, "") && PieceCoversChest(l.name)));
    // pure (tested): the outfit a strip started from (null = keep the one already noted, or not a real outfit)
    // 2026-10-21 (David): redress must NEVER go back into Naked (or an Android outfit); fall back to the last clothed outfit
    internal static bool IsClothedOutfitName(string n) => !string.IsNullOrWhiteSpace(n) && !IsAndroidOutfit(n) && !Regex.IsMatch(n, @"^\s*naked\b", RegexOptions.IgnoreCase);
    internal static string RedressTarget(string preStrip, string current, string lastNamed, string lastClothed) =>
        new[] { preStrip, current, lastNamed, lastClothed }.FirstOrDefault(IsClothedOutfitName)?.Trim();
    static string LastClothedFile => "/home/box/viewers/textclient/run/last-clothed-outfit.txt";
    static string LastClothedOutfit() { try { return File.Exists(LastClothedFile) ? File.ReadAllText(LastClothedFile).Trim() : null; } catch { return null; } }
    static void NoteClothedOutfit(string n) { if (IsClothedOutfitName(n)) try { File.WriteAllText(LastClothedFile, n.Trim()); } catch { } }
    internal static string PreStripCandidate(string already, string current) =>
        already ?? (string.IsNullOrWhiteSpace(current) || IsAndroidOutfit(current) || Regex.IsMatch(current, @"^\s*naked\b", RegexOptions.IgnoreCase) ? null : current.Trim());
    // pure (tested): a fast-chat outfit request while stripped: a random/"get dressed" pick becomes the pre-strip outfit
    internal static (string kind, string name) FastRedressOverride(string kind, string name, bool stripped, string preStrip) =>
        stripped && preStrip != null && kind == "random" ? ("redress", preStrip) : (kind, name);
    internal static bool IsStripWhy(string why) => why != null && (why.StartsWith("rlv") || why.StartsWith("undress") || why.StartsWith("toilet") || why == "command");
    static void NoteStripStart(string why)
    {
        if (!IsStripWhy(why)) return;   // HUD clean-ups (feet hud, wear remove) after an outfit change are not a strip
        var p = PreStripCandidate(preStripOutfit, currentOutfit ?? lastNamedOutfit);
        if (p != null && preStripOutfit == null) { preStripOutfit = p; RlvLog($"strip started ({why}): 'redress' / 'get dressed' will go back to '{p}'"); }
    } static volatile string currentOutfit;

    // pure (tested): does anything worn cover the groin? attachments by name (HUDs excluded by the caller), layers by type,
    // tattoo layers only when they are clothing (panties)
    internal static bool GroinCovered(IEnumerable<string> attachmentNames, IEnumerable<(WearableType t, string name)> layers) =>
        attachmentNames.Any(n => PieceCoversGroin(n) && !(n ?? "").Contains("HUD", StringComparison.OrdinalIgnoreCase))
        || layers.Any(l => l.t is WearableType.Underpants or WearableType.Pants or WearableType.Skirt
                           || (l.t == WearableType.Tattoo && IsClothingLayer(l.name, "") && PieceCoversGroin(l.name)));

    // pure (tested): "wear" / "detach" / null
    // stripping: a strip / toilet undress is in progress (pre-strip outfit noted, not yet redressed): never take it off then;
    // the redress / toilet restore takes it off itself
    internal static string VDecision(bool covered, bool vWorn, string outfit, bool outfitHasV, bool stripping = false)
    {
        if (IsAndroidOutfit(outfit)) return null;
        if (!covered && !vWorn) return "wear";
        if (stripping) return null;
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
                var outfit = currentOutfit ?? lastNamedOutfit ?? ""; bool stripping = preStripOutfit != null;
                bool chest = ChestCovered(atts, layers); bool rWorn = roots.Any(r => AttachItemId(r) == NippleRingsItem);
                var dr = VDecision(chest, rWorn, outfit, lastOutfitHasRings, stripping);
                RlvLog($"Nipple rings ({why}): chest {(chest ? "covered" : "uncovered")}, rings {(rWorn ? "worn" : "off")}, outfit '{outfit}'{(lastOutfitHasRings ? " (includes them)" : "")} -> {dr ?? "no change"}");
                if (dr == "wear") RlvLog("Nipple rings: " + await Exec("wear add " + NippleRingsItem));
                else if (dr == "detach") RlvLog("Nipple rings: " + await DetachItemAsync(NippleRingsItem, "rings auto"));
                var d = VDecision(covered, vWorn, outfit, lastOutfitHasV, stripping);
                RlvLog($"The V ({why}): groin {(covered ? "covered" : "uncovered")}, The V {(vWorn ? "worn" : "off")}, outfit '{outfit}'{(lastOutfitHasV ? " (includes The V)" : "")} {(stripping ? " (strip in progress)" : "")} -> {d ?? "no change"}");
                if (d == "wear" && VAutoPaused()) RlvLog("The V: auto-wear paused (v pause), not re-attaching");
                else if (d == "wear") RlvLog("The V: " + await Exec("wear add " + TheVItem));
                else if (d == "detach") RlvLog("The V: " + await DetachItemAsync(TheVItem, "the v auto"));
            }
            catch (Exception ex) { RlvLog("The V check failed: " + ex.GetBaseException().Message); }
        });
    }

    static async Task<string> RedressCmd()
    {
        var name = RedressTarget(preStripOutfit, currentOutfit, lastNamedOutfit, LastClothedOutfit());
        if (string.IsNullOrWhiteSpace(name)) return "no outfit to go back to (none worn by name yet)";
        RlvLog($"redress: back into '{name}'");
        return await WearOutfitWithHuds(name, keepColor: true);
    }
}
