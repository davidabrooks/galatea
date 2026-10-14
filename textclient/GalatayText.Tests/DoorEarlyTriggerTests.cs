using GalatayText;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-14 David: she still walks into doors at home; the touch must lead by swing + touch + tick time.</summary>
public class DoorEarlyTriggerTests
{
    [Fact]
    public void Walking_speed_touches_early_enough_for_the_swing_with_no_hold()
    {
        float d = Program.DoorTriggerFor(3.2f);                               // her logged walk speed at Peronaut
        Assert.InRange(d, 5.0f, Program.DoorScanM);
        Assert.True(Program.DoorTouchDue(5.0f, 3.2f));
        Assert.False(Program.DoorTouchDue(3.6f));                             // the old 3 m rule unchanged when slow
        Assert.Equal(Program.DoorTriggerM, Program.DoorTriggerFor(0f));
        // touched at the trigger distance -> she reaches the door after the leaf has swung: no hold
        Assert.Equal(0, Program.DoorApproachHoldMs(d, 3.2f, Program.DoorTouchLatencyMs, anyTouched: true));
    }
}
