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
        // he starts moving while I'm right next to him: walk at once
        Assert.Equal("walk", Program.LeashWalkDecide(false, true, false, 0.5f, 1f, true));
        Assert.Equal("", Program.LeashWalkDecide(false, false, true, 0.5f, 1f, true));
        Assert.Equal("walk", Program.LeashWalkDecide(false, false, true, 0.9f, 1f, true));   // distance fallback
        Assert.Equal("stop", Program.LeashWalkDecide(true, true, false, 5f, 1f, false));     // seated/held/wander-off
    }
    [Fact] public void StopOnHisStop()
    {
        var t = DateTime.Now;
        Assert.False(Program.LeashHolderStopped(null, t));
        Assert.False(Program.LeashHolderStopped(t.AddSeconds(-0.2), t));
        Assert.True(Program.LeashHolderStopped(t.AddSeconds(-0.3), t));
        Assert.Equal("", Program.LeashWalkDecide(true, true, false, 0.5f, 1f, true));        // he still moves: keep walking
        Assert.Equal("stop", Program.LeashWalkDecide(true, false, true, 0.6f, 1f, true));
        Assert.Equal("", Program.LeashWalkDecide(true, false, true, 2f, 1f, true));          // close in to the stop distance first
        Assert.Equal(0.6f, Program.LeashWalkStop(1f), 3);
    }
    [Fact] public void TargetLeadsAlongHisHeading()
    {
        var tgt = Program.LeashFollowTarget(new Vector3(100, 100, 20), new Vector3(1, 0, 0), new Vector3(99, 100, 20), 0.6f);
        Assert.Equal(99.9f, tgt.X, 2); Assert.Equal(100f, tgt.Y, 2);   // 0.5 s lead minus the stop distance behind him
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
