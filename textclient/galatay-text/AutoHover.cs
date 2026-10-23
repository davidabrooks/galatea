// AutoHover.cs (2026-10-21 11:27-11:29 David): detect floating/sinking myself and correct my server hover (AgentPreferences
// hover_height, the same path as @adjustheight), independent of the collar/RLV. Scope: standing, AO ground sit, collar kneel.
// Never while sat on an object; when seated, restore my normal (pinned) hover. A hover David set via the collar in the last
// 10 min wins. Logged to rlv-log.md.
// HONEST LIMIT: the client sees my capsule (root position + agent size) and the floor (terrain height map + bounding boxes of
// prims under me), not my feet/knees: the visible float/sink caused by an animation or shape is not measurable here. What it
// can catch: the capsule bottom clearly above/below the floor it should stand on (e.g. a stale hover, an old offset kept
// after a pose, sinking into a floor prim). Bounding boxes are exact only for unrotated box prims (mesh/rotated = estimate).
using System;
using System.Linq;
using System.Threading.Tasks;
using LibreMetaverse;
namespace GalatayText;
public static partial class Program
{
    internal const double AutoHoverTol = 0.03, AutoHoverMax = 0.30, AutoHoverManualSec = 600;
    internal static DateTime? hoverManualAt;   // set by @adjustheight from the collar
    // pure (tested): the hover to set now, or null = leave it. gap = capsule bottom - floor (+ = floating), cur = current hover.
    // offset = a deliberate per-pose/form offset (David 14:45: android ground sit -0.04 m): it is THE target, never undone.
    internal static double? AutoHoverDecide(string posture, bool seatedOnObject, double? gap, double cur, double pinned, double manualAgeSec, double? absHover = null)
    {
        if (seatedOnObject) return Math.Abs(cur - pinned) > 0.005 ? pinned : null;   // seats come later: back to normal
        if (absHover is double ah && posture == "groundsit") { var t0 = Math.Round(ah, 3); return Math.Abs(t0 - cur) > 0.005 ? t0 : null; }   // per-pose ABSOLUTE hover
        if (manualAgeSec < AutoHoverManualSec) return null;                            // David's recent collar hover wins
        if (posture is not ("stand" or "groundsit" or "kneel")) return null;
        if (gap is not double g || Math.Abs(g) <= AutoHoverTol || Math.Abs(g) > 1.0) return null;   // >1 m: not a floor problem
        var target = Math.Round(Math.Clamp(cur - g, pinned - AutoHoverMax, pinned + AutoHoverMax), 3);
        return Math.Abs(target - cur) > 0.005 ? target : null;
    }
    static DateTime autoHoverLast = DateTime.MinValue, autoHoverLogged = DateTime.MinValue;
    static void AutoHoverKick(string why) { autoHoverLast = DateTime.MinValue; _ = AutoHoverCheck(why); }
    static double? FloorBelow(Vector3 me)
    {
        var sim = Sim; if (sim == null) return null;
        double? best = sim.TerrainHeightAtPoint((int)Math.Clamp(me.X, 0, 255), (int)Math.Clamp(me.Y, 0, 255), out var h) ? h : null;
        foreach (var p in sim.ObjectsPrimitives.Values)
        {
            if (p == null || p.ParentID != 0 && p.ParentID == client.Self.LocalID) continue;
            var half = p.Scale * 0.5f; var c = p.Position; if (p.ParentID != 0) continue;   // root prims only (world pos known)
            if (Math.Abs(me.X - c.X) > half.X || Math.Abs(me.Y - c.Y) > half.Y) continue;
            double top = c.Z + half.Z; if (top > me.Z + 0.2 || top < me.Z - 3) continue;
            if (best == null || top > best) best = top;
        }
        return best;
    }
    static async Task AutoHoverCheck(string why)
    {
        if ((DateTime.Now - autoHoverLast).TotalSeconds < 10) return; autoHoverLast = DateTime.Now;
        try
        {
            var av = SelfAv(); if (av == null || av.Scale.Z < 1f) return;
            bool seated = client.Self.SittingOn != 0;
            var posed = idlePosed;
            string posture = seated ? "seat" : posed?.kind == "groundsit" ? "groundsit" : posed?.kind == "collar" ? "kneel"
                           : client.Self.Velocity.Length() < 0.1f ? "stand" : "moving";
            double? gap = null;
            // capsule bottom only means "feet" when standing; for kneels/ground sits the server keeps the same capsule
            if (!seated && FloorBelow(client.Self.SimPosition) is double f) gap = client.Self.SimPosition.Z - av.Scale.Z / 2 - f;
            double cur = SimHover() ?? PinnedHover(), pin = PinnedHover();
            double manualAge = hoverManualAt is DateTime m ? (DateTime.Now - m).TotalSeconds : 1e9;
            double? off = posture == "groundsit" ? GroundSitHoverFor(AndroidFormNow()) ?? pin : null;   // no value for this sit: normal pinned hover, never another sit's
            if (AutoHoverDecide(posture, seated, gap, cur, pin, manualAge, off) is not double t) return;
            if (off is double) { var (ok0, i0) = await PostHover(t, $"auto-hover: deliberate {posture} offset {off:+0.00;-0.00}"); RlvLog($"auto-hover: {posture} offset {off:+0.00;-0.00} -> {t:F3} ({i0})"); return; }
            // default OBSERVE: the capsule-vs-floor estimate is unproven (possible fixed bias -> a wrong hover), so only the
            // seated restore is applied; GT_AUTO_HOVER=on applies the corrections too.
            if (!seated && Env("GT_AUTO_HOVER", "observe") != "on")
            { if ((DateTime.Now - autoHoverLogged).TotalSeconds > 120) { autoHoverLogged = DateTime.Now; RlvLog($"auto-hover (observe): {posture}, capsule gap {gap:+0.000;-0.000} m, would set hover {cur:0.000} -> {t:0.000}"); } return; }
            var (ok, info) = await PostHover(t, $"auto-hover {posture} ({why})");
            RlvLog($"auto-hover: {posture}, capsule gap {(gap is double g ? g.ToString("+0.000;-0.000") : "?")} m, hover {cur:0.000} -> {t:0.000} ({why}): {info}");
        }
        catch (Exception ex) { Log("height", "auto-hover error: " + ex.GetBaseException().Message); }
    }
}
