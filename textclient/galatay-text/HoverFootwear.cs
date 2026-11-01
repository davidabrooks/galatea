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

    internal const double SavedHoverDeviationMax = 0.03;
    /// <summary>Pure (tested): why a stand render fix to `target` must NOT be applied or saved, or null. 2026-10-31 18:26: after a relog the export's
    /// agent z sat 0.080 m lower (28.873 vs 28.953 at the same spot and hover; it flips between the two across logins), all 3 samples shared it and read
    /// -0.06 at the approved 0.012, the fix 0.074 was "verified" on the same stale z and saved; David saw her floating. A fix that moves more than
    /// SavedHoverDeviationMax away from the verified value for this footwear is not trusted: keep the saved value, save nothing, ask for a visual check.</summary>
    internal static string SavedHoverDeviationBlock(string footwear, IReadOnlyDictionary<string, double> saved, double target)
        => footwear != null && saved != null && saved.TryGetValue(footwear, out var sv) && Math.Abs(target - sv) > SavedHoverDeviationMax
            ? $"the render asks for {I(target)} but {footwear}'s verified hover is {I(sv)} ({Math.Abs(target - sv) * 100:0.0} cm apart, over {SavedHoverDeviationMax * 100:0} cm): the render is not trusted (the export's agent z can be stale by ~8 cm after a login); kept {I(sv)}, nothing saved; needs a visual check from David before a new value is saved"
            : null;

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

    // 2026-10-31 17:30:51: a stand check ran DURING the logout (veil on, clothes being stashed) and saved -0.001 for f3. No save while logging out.
    internal static volatile bool logoutHoverFreeze;
    internal static string FootwearHoverSaveBlock(bool loggingOut) => loggingOut ? "logout in progress: the render shows a half-stashed outfit, not saved" : null;

    internal const int FootwearFitTimeoutMs = 90000;
    /// <summary>Pure (tested): the saved hover to apply right after the foot pose for `feet` is pressed, or null (not standing / never saved).</summary>
    internal static double? FootwearPressHover(string kind, bool seated, string feet, IReadOnlyDictionary<string, double> saved)
        => !seated && kind == "stand" && feet != null && saved != null && saved.TryGetValue(feet, out var v) ? v : null;
    /// <summary>Pure (tested): after the timeout, re-apply the saved value when the fit is still unconfirmed for the same footwear and the hover drifted.</summary>
    internal static bool FootwearTimeoutReapply(string feet, string fwNow, string verified, double? inForce, double savedV)
        => string.Equals(feet, fwNow, StringComparison.OrdinalIgnoreCase) && !string.Equals(verified, feet, StringComparison.OrdinalIgnoreCase)
           && (inForce is not double h || Math.Abs(h - savedV) > 0.005);

    // the shoes' foot pose was just pressed: apply the saved hover for that footwear now (standing only); the measuring check follows the verified fit.
    // If the fit is never confirmed, the saved value stays in force after FootwearFitTimeoutMs (never the barefoot hover).
    internal static async Task FootwearPressedHover(string feet)
    {
        try
        {
            var (kind, _) = hoverSvc.PoseNow();
            var v = FootwearPressHover(kind, client.Self.SittingOn != 0, feet, LoadFootwearHovers());
            if (v is not double sv) { Log("height", $"footwear {feet} pressed: no saved hover applied ({(kind != "stand" || client.Self.SittingOn != 0 ? "not standing" : "none saved")})"); return; }
            var r = await PostHover(sv, $"footwear {feet} pressed: saved hover for it");
            Log("height", $"footwear {feet} pressed: saved hover {I(sv)} applied ({r.info}); the measuring check runs once the fit is verified");
            await Task.Delay(FootwearFitTimeoutMs);
            if (!LoggedIn || client.Self.SittingOn != 0 || hoverSvc.PoseNow().kind != "stand") return;
            var saved = LoadFootwearHovers(); if (!saved.TryGetValue(feet, out var sv2)) return;
            if (FootwearTimeoutReapply(feet, FootwearKeyNow(), feetSvc.VerifiedFeet, EffectiveHoverNow(), sv2))
            { var r2 = await PostHover(sv2, $"footwear {feet}: fit not confirmed in {FootwearFitTimeoutMs / 1000} s, saved hover kept"); Log("height", $"footwear {feet}: fit not confirmed in {FootwearFitTimeoutMs / 1000} s: saved hover {I(sv2)} re-applied ({r2.info})"); }
        }
        catch (Exception ex) { Log("height", $"footwear {feet} pressed hover: {ex.GetBaseException().Message}"); }
    }

    // a visual check verified this stand hover for the footwear worn now: remember it
    internal static void SaveVerifiedStandHover(double v, string why)
    {
        var fw = FootwearKeyNow(); if (fw == null) return;
        if (FootwearHoverSaveBlock(logoutHoverFreeze) is string blk) { Log("height", $"stand hover {I(v)} for footwear {fw} NOT saved: {blk} ({why})"); return; }
        try
        {
            File.WriteAllText(HoverFootwearFile, JsonSerializer.Serialize(FootwearHoverMerge(LoadFootwearHovers(), fw, v), new JsonSerializerOptions { WriteIndented = true }));
            Log("height", $"verified stand hover {I(v)} saved for footwear {fw} ({why})");
        }
        catch (Exception ex) { Log("height", "footwear hover save: " + ex.Message); }
    }
}
