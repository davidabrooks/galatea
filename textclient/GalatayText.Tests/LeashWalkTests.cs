using System;
using GalatayText;
using LibreMetaverse;
using Xunit;
public class LeashWalkTests
{
    [Fact] public void NeverWalksWhileHeStands()
    {
        foreach (var gap in new[] { 0.5f, 1f, 1.4f, 3f, 15f })
            Assert.Equal("", Program.LeashControl(false, false, true, gap, 1f, true, 0, 0, false).act);
    }
    [Fact] public void StartHysteresis_HisMotionAndGapOverLeash()
    {
        Assert.Equal("", Program.LeashControl(false, true, false, 0.95f, 1f, true, 0, 0, false).act);
        var o = Program.LeashControl(false, true, false, 1.2f, 1f, true, 0, 0, false);
        Assert.Equal("walk", o.act); Assert.False(o.forward);              // started, but no forward under the 1.4 m cutoff
        Assert.True(Program.LeashControl(false, true, false, 2f, 1f, true, 0, 0, false).forward);
        Assert.Equal("stop", Program.LeashControl(true, false, true, 2f, 1f, true, 0.5f, 0.5f, false).act);   // he stopped
        Assert.Equal("stop", Program.LeashControl(true, true, false, 2f, 1f, false, 0, 0, false).act);        // seated/held/kill switch
    }
    [Fact] public void EarlyDecel_NoForwardUnder1m()
    {
        Assert.True(Program.LeashForwardCutoff(1f) >= 1.4f - 1e-4);
        foreach (var gap in new[] { 0.5f, 0.9f, 0.99f, 1.3f })
            Assert.False(Program.LeashControl(true, true, false, gap, 1f, true, 0.5f, 0.5f, true).forward);
        Assert.False(Program.PullNeedsWalkAnim(1f, false, false, 0.9f, 1f));
    }
    [Fact] public void CollisionStopsAtOnce()
    {
        Assert.Equal("stop", Program.LeashControl(true, true, false, 1.2f, 1f, true, -0.4f, 1.5f, true).act);  // velocity reversed (bounce)
        Assert.Equal("stop", Program.LeashControl(true, true, false, 1.2f, 1f, true, 0.1f, 2.5f, true).act);   // sudden drop while forward
        Assert.Equal("", Program.LeashControl(true, true, false, 3f, 1f, true, -0.4f, 1.5f, true).act);       // far away: not a collision
    }
    [Fact] public void TargetLeadsAlongHisHeading()
    {
        var tgt = Program.LeashFollowTarget(new Vector3(100, 100, 20), new Vector3(1, 0, 0), new Vector3(99, 100, 20), 0.6f);
        Assert.Equal(99.9f, tgt.X, 2); Assert.Equal(100f, tgt.Y, 2);   // 0.5 s lead minus the stop distance behind him (never his own position)
    }
    [Fact] public void MatchHisSpeed()
    {
        Assert.False(Program.LeashShouldRun(1.5f, 1f, 1f, false));   // he walks: I walk
        Assert.True(Program.LeashShouldRun(4f, 1f, 1f, false));      // he runs: I run
        Assert.True(Program.LeashShouldRun(0f, 6f, 1f, false));      // far behind: run
        Assert.False(Program.LeashShouldRun(4f, 6f, 1f, true));      // RLV forbids running
    }
    [Fact] public void LatencyPath()
    {
        var l = new Program.LeashLatency(); var t = DateTime.Now;
        Assert.Null(l.IMoved(t));                     // no motion yet
        l.HolderMoved(t); l.Commanded(t.AddMilliseconds(20));
        var r = l.IMoved(t.AddMilliseconds(320));
        Assert.Equal((20, 320), r);
        Assert.Null(l.IMoved(t.AddMilliseconds(400)));   // logged once per motion start
    }
    [Fact] public void WalkAnimDuringPull()
    {
        Assert.True(Program.PullNeedsWalkAnim(0.5f, false, false, 1.5f, 1f));
        Assert.False(Program.PullNeedsWalkAnim(0.1f, false, false, 9f, 1f));
        Assert.False(Program.PullNeedsWalkAnim(2f, true, false, 9f, 1f));
        Assert.False(Program.PullNeedsWalkAnim(2f, false, true, 9f, 1f));
        Assert.False(Program.PullNeedsWalkAnim(-1f, false, false, 2f, 1f));   // moving AWAY from him (overshoot/bounce): never thrust
        Assert.False(Program.PullNeedsWalkAnim(3f, false, false, 1.6f, 1f));  // fast toward him: inside braking distance
    }
}
public class LeashHolderResolveTests
{
    [Fact] public void HandleInHisHand_ResolvesToHim()
    {
        UUID david = UUID.Random(), handle = UUID.Random(), post = UUID.Random();
        (uint, uint)? Prim(UUID id) => id == handle ? (50u, 40u) : id == post ? (60u, 0u) : null;   // handle child of a worn root 40, root parent = avatar 7
        UUID? Av(uint lid) => lid == 7 ? david : null;
        uint Parent(uint lid) => lid == 40 ? 7u : 0u;
        Assert.Equal(david, Program.LeashResolveHolder(handle, Prim, Av, Parent));
        Assert.Equal(post, Program.LeashResolveHolder(post, Prim, Av, Parent));          // a real post stays an anchor
        Assert.Equal(david, Program.LeashResolveHolder(david, Prim, Av, Parent));        // avatar itself
    }
}
public class LeashReactionDelayTests
{
    [Fact] public void DelayInRange()
    {
        Assert.Equal(250, Program.LeashReactionDelayMs(0));
        Assert.Equal(400, Program.LeashReactionDelayMs(1));
        for (int i = 0; i < 200; i++) { var d = Program.LeashReactionDelayMs(System.Random.Shared.NextDouble()); Assert.InRange(d, 250, 400); }
        Assert.Equal(400, Program.LeashReactionDelayMs(5));   // clamped: never grows into the old lag
    }
    [Fact] public void NoMoveBeforeTheDelay()
    {
        var t = System.DateTime.Now;
        Assert.False(Program.LeashReactionReady(null, 500, t));
        Assert.False(Program.LeashReactionReady(t, 500, t.AddMilliseconds(499)));
        Assert.True(Program.LeashReactionReady(t, 500, t.AddMilliseconds(500)));
    }
}

public class LeashStartGapTests
{
    [Fact] public void JustOverHoldDistance_NeverAboveLeash()
    {
        foreach (var L in new[] { 1f, 2f, 3f, 5f })
        {
            var g = Program.LeashStartGap(L);
            Assert.True(g > Program.LeashWalkStop(L), $"L {L}: start {g} must be above the hold/stop distance");
            Assert.True(g <= Math.Max(L, Program.LeashWalkStop(L) + 0.1f) + 1e-4, $"L {L}: start {g} sooner than before");
        }
        Assert.True(Program.LeashStartGap(3f) < 3f);   // sooner than the old gap > leash
    }
}
