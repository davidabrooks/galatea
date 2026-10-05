using GalatayText;
using Xunit;

namespace GalatayText.Tests;

/// <summary>Pure helpers for door open-trust / touch-next and follow dist reband (2026-10-05).</summary>
public class DoorAndFollowPureTests
{
    [Fact]
    public void Door_stale_pose_open_does_not_skip_touch()
    {
        var now = new DateTime(2026, 10, 5, 15, 22, 0);
        Assert.False(Program.DoorSkipTouchAlreadyOpen(true, false, DateTime.MinValue, now));
        Assert.False(Program.DoorSkipTouchAlreadyOpen(true, false, now.AddHours(-4), now));
        Assert.True(Program.DoorSkipTouchAlreadyOpen(true, false, now.AddSeconds(-5), now));
        Assert.True(Program.DoorSkipTouchAlreadyOpen(false, true, DateTime.MinValue, now));
    }

    [Fact]
    public void Door_failed_open_walk_next_is_touch_not_escape()
    {
        Assert.Equal("touch", Program.DoorNextAfterFailedOpenWalk("got closer, blocked"));
        Assert.Equal("touch", Program.DoorNextAfterFailedOpenWalk("bounced back"));
        Assert.Equal("touch", Program.DoorNextAfterFailedOpenWalk("nothing (no progress)"));
        Assert.Equal("none", Program.DoorNextAfterFailedOpenWalk("passed through"));
    }

    [Fact]
    public void Follow_reband_closes_in_when_dist_lowered_while_holding()
    {
        // Holding at 1.6 m after dist 1.5→1.0: stop 1.4 / resume 2.0 — without reband stays Hold
        Assert.Equal(Program.FollowAct.Hold, Program.FollowDecide(1.6f, false, 0.6f, 0, 1.0f, false));
        Assert.Equal(Program.FollowAct.Pursue, Program.FollowDecide(1.6f, false, 0.6f, 0, 1.0f, false, reband: true));
        Assert.Equal(Program.FollowAct.Hold, Program.FollowDecide(1.2f, false, 0.2f, 0, 1.0f, false, reband: true));
    }
}
