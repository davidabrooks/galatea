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
internal sealed partial class Hover
{
    internal const double AutoHoverTol = 0.03, AutoHoverMax = 0.30, AutoHoverManualSec = 600;
    internal DateTime? hoverManualAt;   // set by @adjustheight from the collar
    // pure (tested): the hover to set now, or null = leave it. gap = capsule bottom - floor (+ = floating), cur = current hover.
    // (2026-10-26: no per-pose/per-stand hover offsets any more; PoseHover.cs checks the hover visually after a sit, stand or new region.)
    internal static double? AutoHoverDecide(string posture, bool seatedOnObject, double? gap, double cur, double pinned, double manualAgeSec)
    {
        if (seatedOnObject) return Math.Abs(cur - pinned) > 0.005 ? pinned : null;   // seats come later: back to normal
        if (manualAgeSec < AutoHoverManualSec) return null;                            // David's recent collar hover wins
        if (posture is not ("stand" or "groundsit" or "kneel")) return null;
        if (gap is not double g || Math.Abs(g) <= AutoHoverTol || Math.Abs(g) > 1.0) return null;   // >1 m: not a floor problem
        var target = Math.Round(Math.Clamp(cur - g, pinned - AutoHoverMax, pinned + AutoHoverMax), 3);
        return Math.Abs(target - cur) > 0.005 ? target : null;
    }
    DateTime autoHoverLast = DateTime.MinValue, autoHoverLogged = DateTime.MinValue;
    internal void AutoHoverKick(string why) { autoHoverLast = DateTime.MinValue; _ = AutoHoverCheck(why); }
    double? FloorBelow(Vector3 me)
    {
        var sim = h.Sim; if (sim == null) return null;
        double? best = sim.TerrainHeightAtPoint((int)Math.Clamp(me.X, 0, 255), (int)Math.Clamp(me.Y, 0, 255), out var th) ? th : null;
        foreach (var p in sim.ObjectsPrimitives.Values)
        {
            if (p == null || p.ParentID != 0 && p.ParentID == h.Client.Self.LocalID) continue;
            var half = p.Scale * 0.5f; var c = p.Position; if (p.ParentID != 0) continue;   // root prims only (world pos known)
            if (Math.Abs(me.X - c.X) > half.X || Math.Abs(me.Y - c.Y) > half.Y) continue;
            double top = c.Z + half.Z; if (top > me.Z + 0.2 || top < me.Z - 3) continue;
            if (best == null || top > best) best = top;
        }
        return best;
    }
    internal async Task AutoHoverCheck(string why)
    {
        if ((DateTime.Now - autoHoverLast).TotalSeconds < 10) return; autoHoverLast = DateTime.Now;
        try
        {
            var av = h.SelfAv(); if (av == null || av.Scale.Z < 1f) return;
            bool seated = h.Client.Self.SittingOn != 0;
            var posed = h.IdlePosed;
            string posture = seated ? "seat" : posed?.kind == "groundsit" || h.AoSitReallyOn() ? "groundsit" : posed?.kind == "collar" ? "kneel"
                           : h.Client.Self.Velocity.Length() < 0.1f ? (h.AoUnclassifiedNow().Count > 0 ? "unknown" : "stand") : "moving";   // unknown (unclassified AO animation) is never judged as a stand
            double? gap = null;
            // capsule bottom only means "feet" when standing; for kneels/ground sits the server keeps the same capsule
            if (!seated && FloorBelow(h.Client.Self.SimPosition) is double f) gap = h.Client.Self.SimPosition.Z - av.Scale.Z / 2 - f;
            double cur = h.EffectiveHoverNow() ?? h.PinnedHover(), pin = h.PinnedHover();
            double manualAge = hoverManualAt is DateTime m ? (DateTime.Now - m).TotalSeconds : 1e9;
            if (AutoHoverDecide(posture, seated, gap, cur, pin, manualAge) is not double t) return;
            // default OBSERVE: the capsule-vs-floor estimate is unproven (possible fixed bias -> a wrong hover), so only the
            // seated restore is applied; GT_AUTO_HOVER=on applies the corrections too.
            if (!seated && Program.Env("GT_AUTO_HOVER", "observe") != "on")
            { if ((DateTime.Now - autoHoverLogged).TotalSeconds > 120) { autoHoverLogged = DateTime.Now; h.RlvLog($"auto-hover (observe): {posture}, capsule gap {gap:+0.000;-0.000} m, would set hover {cur:0.000} -> {t:0.000}"); } return; }
            var (ok, info) = await h.PostHover(t, $"auto-hover {posture} ({why})");
            h.RlvLog($"auto-hover: {posture}, capsule gap {(gap is double g ? g.ToString("+0.000;-0.000") : "?")} m, hover {cur:0.000} -> {t:0.000} ({why}): {info}");
        }
        catch (Exception ex) { h.Log("height", "auto-hover error: " + ex.GetBaseException().Message); }
    }
}
