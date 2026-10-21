using System;
using System.Collections.Generic;
using GalatayText;
using Xunit;
// Offline 1-D simulation: holder ahead on +x, collar llMoveToTarget pull, avatar walk physics, body collision.
public class LeashWalkSimTests
{
    sealed record SimResult(int starts, int stops, float minGap, int collisions, float finalSpeed, List<string> log);
    static SimResult Run(Func<double, float> hisSpeed, double seconds, float startGap, float L = 1f)
    {
        const double dt = 0.05; float him = 0, me = -startGap, own = 0, prevToward = 0; bool walking = false, fwd = false;
        double? motionAt = null, slowSince = 0; int starts = 0, stops = 0, coll = 0; float minGap = startGap; var log = new List<string>();
        for (double t = 0; t < seconds; t += dt)
        {
            float hv = hisSpeed(t); him += hv * (float)dt;
            float gap = him - me;
            // collar pull (llMoveToTarget): toward him when past the leash
            float pull = gap > L ? Math.Min(3f, 2f * (gap - L)) : 0f;
            // his motion as my client sees it: away from me, past stop distance, after the 0.4-0.6 s reaction delay
            bool moving = hv > 0.3f && gap > Program.LeashWalkStop(L);
            if (moving) { motionAt ??= t; slowSince = null; } else { motionAt = null; slowSince ??= t; }
            bool hisMotion = moving && (walking || t - motionAt >= 0.5);
            bool hisStopped = slowSince is double s && t - s >= 0.3;
            float vToward = own + pull;
            var o = Program.LeashControl(walking, hisMotion, hisStopped, gap, L, true, vToward, prevToward, fwd);
            prevToward = vToward;
            if (o.act == "walk") { walking = true; starts++; log.Add($"{t:F2} start gap {gap:F2}"); }
            else if (o.act == "stop") { walking = false; stops++; log.Add($"{t:F2} stop ({o.why}) gap {gap:F2}"); }
            fwd = walking && o.forward;
            float target = fwd ? (hv > 3.2f ? 5f : 3.2f) : 0f;
            own += Math.Clamp(target - own, -8f * (float)dt, 10f * (float)dt);
            me += (own + pull) * (float)dt;
            gap = him - me;
            if (gap < 0.5f) { me = him - 0.5f; own = -0.5f * own; coll++; log.Add($"{t:F2} COLLISION"); }
            minGap = Math.Min(minGap, him - me);
        }
        return new(starts, stops, minGap, coll, own, log);
    }
    [Fact] public void HeStandsStill_CollarHolds_NoOscillation()
    {
        var r = Run(_ => 0f, 15, 1.4f);
        Assert.Equal(0, r.starts); Assert.Equal(0, r.collisions);
    }
    [Fact] public void LoginAcrossTheRoom_HeStands_WalkerDoesNothing()
    {
        var r = Run(_ => 0f, 15, 15f);
        Assert.Equal(0, r.starts); Assert.Equal(0, r.collisions);   // (the login walk-to-him is a separate, bounded path)
    }
    [Theory]
    [InlineData(1.3f, 6.0)]   // walk
    [InlineData(3.2f, 4.0)]   // fast walk
    [InlineData(5.0f, 4.0)]   // run
    public void HeWalksThenStops_NoOvershootNoBounce(float speed, double walkFor)
    {
        var r = Run(t => t < 1 ? 0f : t < 1 + walkFor ? speed : 0f, 1 + walkFor + 6, 1.0f);
        Assert.Equal(0, r.collisions);
        Assert.True(r.minGap >= Program.LeashMinGap, $"min gap {r.minGap:F2}: " + string.Join(" | ", r.log));
        Assert.InRange(r.starts, 1, 2);
        Assert.True(r.stops >= r.starts - 0 && r.stops <= 2, string.Join(" | ", r.log));
        Assert.True(Math.Abs(r.finalSpeed) < 0.05f);
    }
    [Fact] public void StopGo_NoJitter()
    {
        // he walks 2 s, stands 2 s, five times
        var r = Run(t => (t % 4) < 2 ? 1.3f : 0f, 20, 1.0f);
        Assert.Equal(0, r.collisions);
        Assert.True(r.minGap >= Program.LeashMinGap, string.Join(" | ", r.log));
        Assert.True(r.starts <= 5, string.Join(" | ", r.log));   // at most one start per walk
    }
}
