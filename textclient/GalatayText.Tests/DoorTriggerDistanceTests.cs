using GalatayText;
using LibreMetaverse;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-07 09:23 David: doors opened too late (she bumped them). Touch at ~3 m ahead along the path, and give it time to swing.</summary>
public class DoorTriggerDistanceTests
{
    static bool CrossesX10(Vector2 p, Vector2 q) => (p.X - 10f) * (q.X - 10f) <= 0 && p.X != q.X;

    [Fact]
    public void Trigger_is_three_metres_with_a_swing_wait()
    {
        Assert.Equal(3.0f, Program.DoorTriggerM);
        Assert.True(Program.DoorScanM > Program.DoorTriggerM);
        Assert.InRange(Program.DoorSwingWaitMs, Program.DoorThroughDelayMs + 300, 1500);
        Assert.True(Program.DoorTouchDue(2.9f));
        Assert.True(Program.DoorTouchDue(0.2f));
        Assert.False(Program.DoorTouchDue(3.6f));
    }

    [Fact]
    public void Door_found_ahead_on_a_straight_path_and_due_at_three_metres()
    {
        Func<float, Vector2> at = u => new Vector2(u, 0);
        var far = Program.FirstCrossingAhead(at, 0f, 20f, CrossesX10);
        Assert.Null(far); // 10 m away: beyond the scan window
        var d = Program.FirstCrossingAhead(at, 5.5f, 20f, CrossesX10);
        Assert.NotNull(d); Assert.False(Program.DoorTouchDue(d!.Value));
        var near = Program.FirstCrossingAhead(at, 7.2f, 20f, CrossesX10);
        Assert.NotNull(near); Assert.InRange(near!.Value, 2.0f, 3.0f); Assert.True(Program.DoorTouchDue(near.Value));
    }

    [Fact]
    public void Door_after_a_bend_is_still_found()
    {
        // L-shaped path: 0..4 along +y, then +x; the door line x=10 lies past the bend; a chord would be fine here,
        // but a 4 m chord from s=4 to s=8 on a U-turn path would not — scan follows the path
        Func<float, Vector2> at = u => u <= 4 ? new Vector2(8f, u) : new Vector2(8f + (u - 4), 4);
        var d = Program.FirstCrossingAhead(at, 2f, 20f, CrossesX10);
        Assert.NotNull(d); Assert.InRange(d!.Value, 3.5f, 4.0f);
        Assert.Null(Program.FirstCrossingAhead(at, 0f, 3f, CrossesX10)); // path ends before the door
    }
}
