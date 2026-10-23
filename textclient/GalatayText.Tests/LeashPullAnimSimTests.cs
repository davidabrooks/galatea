using System;
using GalatayText;
using Xunit;
// Regression (2026-10-23 live test, Peronaut): David led me into the next room and stopped; I went back and forth many
// times before settling. The walker never started (no leash-walk lines): the collar dragged me and the "pull legs" nudge
// thrust me along my own velocity, including away from him after an overshoot, so the pull and the nudge fed each other.
// 1-D sim: he stands at x=0, I start far behind; collar = damped spring toward the leash length, body collision at 0.5 m.
public class LeashPullAnimSimTests
{
    // the fixed nudge always faces him; returns the number of direction reversals of my motion after the first arrival, and the final speed
    static (int reversals, float finalSpeed, float minGap) Run(Func<float, float, float, bool> nudge, float L = 1f)
    {
        const float dt = 0.05f; float me = -6f, own = 0f; int rev = 0; float lastSign = 0, minGap = 99;
        for (float t = 0; t < 20; t += dt)
        {
            float gap = -me;                                   // he is at 0, I am at me < 0
            float v = own + (gap > L ? Math.Min(6f, 4f * (gap - L)) : 0f);
            float vToward = v;                                 // +x is toward him
            float speed = Math.Abs(v);
            bool on = nudge(speed, vToward, gap);
            float target = on ? 3.2f : 0f;   // faces him (LeashWalkTick)
            own += Math.Clamp(target - own, -8f * dt, 10f * dt);
            v = own + (gap > L ? Math.Min(6f, 4f * (gap - L)) : 0f);
            me += v * dt;
            if (-me < 0.5f) { me = -0.5f; own = -0.8f * Math.Abs(v); }  // bump into him: bounce back
            minGap = Math.Min(minGap, -me);
            float sgn = Math.Abs(v) > 0.2f ? Math.Sign(v) : 0f;
            if (sgn != 0 && lastSign != 0 && sgn != lastSign) rev++;
            if (sgn != 0) lastSign = sgn;
        }
        float fin = own + (-me > L ? Math.Min(6f, 4f * (-me - L)) : 0f);
        return (rev, fin, minGap);
    }
    [Fact] public void NudgeNeverThrustsAwayFromHim()   // the bug: the old nudge faced my velocity, bounce or overshoot included
    {
        foreach (var gap in new[] { 1.0f, 1.2f, 1.5f, 2f, 4f })
            foreach (var v in new[] { -0.3f, -1f, -3f })
                Assert.False(Program.PullNeedsWalkAnim(v, false, false, gap, 1f), $"gap {gap} v {v}");
    }
    [Fact] public void NewNudge_SettlesOnceNoBackAndForth()
    {
        var r = Run((speed, vt, gap) => Program.PullNeedsWalkAnim(vt, false, false, gap, 1f));
        Assert.True(r.reversals <= 1, $"reversals {r.reversals}");
        Assert.True(Math.Abs(r.finalSpeed) < 0.05f, $"final speed {r.finalSpeed}");
        Assert.True(r.minGap >= 0.5f);
    }
}
