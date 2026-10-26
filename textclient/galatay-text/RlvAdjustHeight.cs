// RlvAdjustHeight.cs (2026-10-21 11:01 David): @adjustheight:<distance>[;factor[;delta]]=force, as RLVa (Firestorm/Catznip):
// hover offset (m) = distance * factor + delta, factor defaults to 0.01 (so @adjustheight:4=force = +0.04 m), delta to 0.
// It is ABSOLUTE (it sets the hover, it doesn't add to it), clamped to the viewer's hover range -2..+2 m, and applied like
// Firestorm's hover slider: AgentPreferences hover_height (server-side, seen by everyone). Not reset on a pose STOP
// (RLVa doesn't; the collar sends its own @adjustheight:0). Kept until logout; the login/sit hover pin uses it.
using System;
using System.Globalization;
using System.Threading.Tasks;
namespace GalatayText;
public static partial class Program
{
    internal static double? rlvHoverOverride;
    // pure (tested): null = malformed
    internal static double? RlvAdjustHeightParse(string opt)
    {
        var a = (opt ?? "").Split(';');
        static bool F(string x, out double v) => double.TryParse(x.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        if (a.Length < 1 || !F(a[0], out var dist)) return null;
        double factor = 0.01, delta = 0;
        if (a.Length > 1 && a[1].Trim().Length > 0 && !F(a[1], out factor)) factor = 0.01;
        if (a.Length > 2 && a[2].Trim().Length > 0 && !F(a[2], out delta)) delta = 0;
        var v = dist * factor + delta;
        if (double.IsNaN(v) || double.IsInfinity(v)) return null;
        return Math.Round(Math.Clamp(v, -2.0, 2.0), 4);
    }
    // 2026-10-26 (David: "you should never change your hover height unless you are fixing a problem"): the collar's pose script sends
    // @adjustheight:8=force when a kneel starts (and :2 on STOP) and that raised her 8 cm off the floor. Hover is owned by the
    // visual hover check (PoseHover.cs: nothing saved per pose; after a sit, stand or new region the render-based check fixes it), so an
    // object's @adjustheight is recorded and NOT applied. GT_RLV_ADJUSTHEIGHT=apply restores the old behaviour.
    internal static bool RlvAdjustHeightApplies(string env) => string.Equals((env ?? "").Trim(), "apply", StringComparison.OrdinalIgnoreCase);
    static async Task<string> RlvAdjustHeight(string opt)
    {
        if (RlvAdjustHeightParse(opt) is not double v) return $"adjustheight: bad option '{opt}'";
        if (!RlvAdjustHeightApplies(Environment.GetEnvironmentVariable("GT_RLV_ADJUSTHEIGHT")))
        {
            RlvLog($"adjustheight:{opt} -> hover {v:+0.000;-0.000;0} m ignored (hover is only changed to fix a problem; the visual hover check owns it); hover stays {(SimHover() is double h0 ? h0.ToString("0.000", CultureInfo.InvariantCulture) : "?")}");
            return $"adjustheight {v:0.000} m ignored (hover unchanged)";
        }
        var before = SimHover();
        rlvHoverOverride = v; hoverManualAt = DateTime.Now;
        var (ok, info) = await PostHover(v, $"rlv adjustheight:{opt}");
        RlvLog($"adjustheight:{opt} -> hover {v:+0.000;-0.000;0} m (was {(before is double b ? b.ToString("0.000", CultureInfo.InvariantCulture) : "?")}): {info}");
        return ok ? $"hover set to {v:0.000} m ({info})" : $"hover POST failed: {info}";
    }
}
