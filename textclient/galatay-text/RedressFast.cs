// RedressFast.cs (2026-10-08 ~18:58 PT, David "Yes" to skipping the ~20 s colour re-check on a same-outfit re-dress, and
// the #121 batch + immediate bake applied to the bikini change in/out).
// 1) Re-dressing the SAME outfit (after the tub / beach / toilet / relog) re-wears the very same inventory items, and an
//    item keeps its HUD-applied texture state, so the HUD attach + press + verify + detach is skipped when the remembered
//    colour was applied to exactly these items. Any doubt (no record yet, a piece with a different item id = a fresh copy,
//    a piece not actually worn) runs the HUD step as before. A real outfit switch (new random colour) and the shorts colour
//    match on a switch always run it.
// 2) Every outfit wear (the bikini on/off included): attachments detached together, layer COF links removed / added in one
//    batch each, and the bake requested right away instead of after LibreMetaverse's 5 s debounce per layer change.
using System.Text.Json;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    // Pure: why the HUD step can be skipped on a re-dress (null = run it).
    internal static string RedressHudSkipReason(bool keepColor, string lastPick, IReadOnlyCollection<UUID> recordedItems,
                                                IReadOnlyCollection<UUID> clothing, ISet<UUID> wornNow)
    {
        if (!keepColor) return null;                                       // real outfit switch: new colour
        if (string.IsNullOrWhiteSpace(lastPick)) return null;              // nothing remembered
        if (recordedItems == null || recordedItems.Count == 0) return null; // never recorded which items got it: learn once
        if (clothing == null || clothing.Count == 0) return null;
        var rec = recordedItems.ToHashSet();
        if (!clothing.All(rec.Contains)) return null;                      // fresh copy / different item: re-apply
        if (wornNow == null || !clothing.All(wornNow.Contains)) return null; // not (yet) worn: let the HUD step verify
        return $"same item(s) as when '{lastPick}' was applied, colour kept by the items";
    }

    // Pure: the items that now carry a HUD's colour. A new pick replaces the record; a re-applied remembered colour adds.
    internal static List<UUID> HudColouredItemsAfter(IEnumerable<UUID> before, IEnumerable<UUID> clothing, bool newPick) =>
        (newPick ? clothing : (before ?? Enumerable.Empty<UUID>()).Concat(clothing)).Where(u => u != UUID.Zero).Distinct().OrderBy(u => u.ToString()).ToList();

    internal static string FormatItemList(IEnumerable<UUID> ids) => string.Join(",", ids.Select(i => i.ToString()));
    internal static List<UUID> ParseItemList(string s) =>
        (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Select(x => UUID.TryParse(x, out var u) ? u : UUID.Zero).Where(u => u != UUID.Zero).Distinct().ToList();

    // ---- which items each HUD's remembered colour was applied to (run/hud-last-items.json) ---------------------------
    static string HudLastItemsFile => Env("GT_HUD_LAST_ITEMS", Path.Combine(Path.GetDirectoryName(LastNamedOutfitFile) ?? "/tmp", "hud-last-items.json"));
    static List<UUID> HudLastItems(UUID hud)
    {
        try { return File.Exists(HudLastItemsFile) && ParseHudLastPicks(File.ReadAllText(HudLastItemsFile)).TryGetValue(hud.ToString(), out var v) ? ParseItemList(v) : new(); }
        catch { return new(); }
    }
    static void SaveHudLastItems(UUID hud, IEnumerable<UUID> clothing, bool newPick)
    {
        try
        {
            var d = File.Exists(HudLastItemsFile) ? ParseHudLastPicks(File.ReadAllText(HudLastItemsFile)) : new(StringComparer.OrdinalIgnoreCase);
            d[hud.ToString()] = FormatItemList(HudColouredItemsAfter(newPick ? null : HudLastItems(hud), clothing, newPick));
            Directory.CreateDirectory(Path.GetDirectoryName(HudLastItemsFile)!);
            File.WriteAllText(HudLastItemsFile, JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log("hud", "last-items save: " + ex.Message); }
    }

    // ---- outfit wear order ----------------------------------------------------------------------------------------
    internal enum SwapStep { DetachAll, LayersOff, BodyOn, LayersOn, CofLinksOff, CofLinksOn, RebakeNow, AttachAll }

    // Pure: the order of an outfit wear (the bikini on/off included). Old attachments all detached at once, layer changes
    // sent together, COF links removed / added in one batch each, the bake right away; new attachments only after that
    // (and after the short pause that keeps a new hair from landing on the old one).
    internal static List<SwapStep> OutfitSwapOrder(int detach, int layersOff, int bodyParts, int layersOn, int attach)
    {
        var s = new List<SwapStep>();
        if (detach > 0) s.Add(SwapStep.DetachAll);
        if (layersOff > 0) s.Add(SwapStep.LayersOff);
        if (bodyParts > 0) s.Add(SwapStep.BodyOn);
        if (layersOn > 0) s.Add(SwapStep.LayersOn);
        if (detach > 0 || layersOff > 0) s.Add(SwapStep.CofLinksOff);
        if (layersOn > 0) s.Add(SwapStep.CofLinksOn);
        if (layersOff > 0 || layersOn > 0) s.Add(SwapStep.RebakeNow);
        if (attach > 0) s.Add(SwapStep.AttachAll);
        return s;
    }
}
