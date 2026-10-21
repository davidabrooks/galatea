using System;
using GalatayText;
using LibreMetaverse;
using Xunit;
public class LeashWalkTests
{
    [Fact] public void MotionStartTrigger_NoDistanceWait()
    {
        Assert.True(Program.LeashHolderMoving(0.4f, 0, 0));
        Assert.True(Program.LeashHolderMoving(0f, 0.1f, 0.25));      // position change between 4 Hz samples
        Assert.False(Program.LeashHolderMoving(0.1f, 0.01f, 0.25));
        Assert.Equal("walk", Program.LeashWalkDecide(false, true, false, 1.05f, 1f, true));   // he moves off: walk at once, no 0.8x wait
        Assert.Equal("", Program.LeashWalkDecide(false, false, true, 1.05f, 1f, true));
        Assert.Equal("walk", Program.LeashWalkDecide(false, false, true, 1.4f, 1f, true));    // distance fallback
        Assert.Equal("stop", Program.LeashWalkDecide(true, true, false, 5f, 1f, false));      // seated/held/wander-off/kill switch
    }
    [Fact] public void NoMovementInsideStopDistance()
    {
        Assert.True(Program.LeashWalkStop(1f) >= Program.LeashMinGap);
        foreach (var d in new[] { 0.3f, 0.6f, 0.7f, 0.9f })
        {
            Assert.Equal("", Program.LeashWalkDecide(false, true, false, d, 1f, true));        // never start inside the stop distance
            Assert.Equal("stop", Program.LeashWalkDecide(true, true, false, d, 1f, true));     // and stop at once, even while he moves
        }
    }
    [Fact] public void NoFeedbackLoop_OnlyMotionAwayFromMeCounts()
    {
        var me = new Vector3(100, 100, 20); var him = new Vector3(101, 100, 20);
        Assert.True(Program.LeashMovingAway(him, new Vector3(1, 0, 0), me));      // he walks away
        Assert.False(Program.LeashMovingAway(him, new Vector3(-1, 0, 0), me));    // toward me
        Assert.False(Program.LeashMovingAway(him, new Vector3(0, 1, 0), me));     // sideways, nothing away
        Assert.False(Program.LeashMovingAway(him, new Vector3(0.2f, 0, 0), me));  // a nudge from my bump
    }
    [Fact] public void LoginGrace()
    {
        var t = DateTime.Now;
        Assert.True(Program.LeashInGrace(t, t.AddSeconds(1)));
        Assert.True(Program.LeashInGrace(t, t.AddSeconds(4.9)));
        Assert.False(Program.LeashInGrace(t, t.AddSeconds(5.1)));
    }
    [Fact] public void StopOnHisStop()
    {
        var t = DateTime.Now;
        Assert.False(Program.LeashHolderStopped(null, t));
        Assert.False(Program.LeashHolderStopped(t.AddSeconds(-0.2), t));
        Assert.True(Program.LeashHolderStopped(t.AddSeconds(-0.3), t));
        Assert.Equal("", Program.LeashWalkDecide(true, true, false, 1.5f, 1f, true));        // he still moves: keep walking
        Assert.Equal("stop", Program.LeashWalkDecide(true, false, true, 1.05f, 1f, true));   // he stopped, I'm at the gap
        Assert.Equal("", Program.LeashWalkDecide(true, false, true, 2f, 1f, true));          // close in to the gap first
        Assert.Equal(0.9f, Program.LeashWalkStop(1f), 3);
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
        Assert.True(Program.PullNeedsWalkAnim(0.5f, false, false));
        Assert.False(Program.PullNeedsWalkAnim(0.1f, false, false));
        Assert.False(Program.PullNeedsWalkAnim(2f, true, false));
        Assert.False(Program.PullNeedsWalkAnim(2f, false, true));
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
        Assert.Equal(400, Program.LeashReactionDelayMs(0));
        Assert.Equal(600, Program.LeashReactionDelayMs(1));
        for (int i = 0; i < 200; i++) { var d = Program.LeashReactionDelayMs(System.Random.Shared.NextDouble()); Assert.InRange(d, 400, 600); }
        Assert.Equal(600, Program.LeashReactionDelayMs(5));   // clamped: never grows into the old lag
    }
    [Fact] public void NoMoveBeforeTheDelay()
    {
        var t = System.DateTime.Now;
        Assert.False(Program.LeashReactionReady(null, 500, t));
        Assert.False(Program.LeashReactionReady(t, 500, t.AddMilliseconds(499)));
        Assert.True(Program.LeashReactionReady(t, 500, t.AddMilliseconds(500)));
    }
}
