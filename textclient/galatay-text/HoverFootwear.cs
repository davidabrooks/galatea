// HoverFootwear.cs (2026-10-30 PT). The single pin in hover.txt is footwear-blind: -0.04 was approved with flat feet, but with
// block-heel boots (f3) the calibrated visual check finds -0.009. The stand hover that a visual check VERIFIED is now saved per
// footwear foot pose (f1..f5 from _feet-rules.json) in hover-footwear.json, and every repin (PinnedHover) uses the last verified
// value for the footwear worn now; hover.txt is the fallback for footwear never verified. An RLV @adjustheight override still wins.
using System.Text.Json;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    static readonly string HoverFootwearFile = Env("GT_HOVER_FOOTWEAR_FILE", Path.Combine(Path.GetDirectoryName(HoverFile) ?? ".", "hover-footwear.json"));

    // pure (tested): the value a repin uses: the verified one for this footwear, else the hover.txt pin
    internal static string PinTextFor(string footwear, IReadOnlyDictionary<string, double> saved, string fileText)
        => footwear != null && saved != null && saved.TryGetValue(footwear, out var v) ? I(v) : fileText;

    // pure (tested): file content after saving one footwear's verified stand hover
    internal static Dictionary<string, double> FootwearHoverMerge(IReadOnlyDictionary<string, double> raw, string footwear, double v)
    {
        var d = new Dictionary<string, double>(raw ?? new Dictionary<string, double>(), StringComparer.OrdinalIgnoreCase);
        d[footwear] = Math.Round(v, 4); return d;
    }

    internal static Dictionary<string, double> LoadFootwearHovers()
    {
        try { return File.Exists(HoverFootwearFile) ? JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(HoverFootwearFile)) ?? new() : new(); }
        catch { return new(); }
    }

    // the foot pose the worn footwear calls for (f1 = flat / none listed), or null when unknown (not logged in)
    internal static string FootwearKeyNow()
    {
        try
        {
            if (client?.Network?.CurrentSim == null) return null;
            var f = Path.Combine(RouteDir, "_feet-rules.json");
            var rules = FeetAuto.ParseFeetRules(File.Exists(f) ? File.ReadAllText(f) : "{}");
            return FeetAuto.FeetWanted(WornPrims().Select(AttachItemId).Where(u => u != UUID.Zero), rules).feet;
        }
        catch { return null; }
    }

    // a visual check verified this stand hover for the footwear worn now: remember it
    internal static void SaveVerifiedStandHover(double v, string why)
    {
        var fw = FootwearKeyNow(); if (fw == null) return;
        try
        {
            File.WriteAllText(HoverFootwearFile, JsonSerializer.Serialize(FootwearHoverMerge(LoadFootwearHovers(), fw, v), new JsonSerializerOptions { WriteIndented = true }));
            Log("height", $"verified stand hover {I(v)} saved for footwear {fw} ({why})");
        }
        catch (Exception ex) { Log("height", "footwear hover save: " + ex.Message); }
    }
}
