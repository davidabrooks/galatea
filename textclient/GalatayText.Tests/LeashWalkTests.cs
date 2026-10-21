using GalatayText;
using Xunit;
public class LeashWalkTests
{
    [Fact] public void DelayThreshold_StartsAt08xLeash()
    {
        Assert.Equal(4f, Program.LeashWalkStart(5f), 3);
        Assert.Equal("walk", Program.LeashWalkDecide(false, 4.1f, 5f, true));
        Assert.Equal("", Program.LeashWalkDecide(false, 3.9f, 5f, true));
        // shoestring (1 m): starts well before the 1 m pull, never inside the stop distance
        Assert.True(Program.LeashWalkStart(1f) <= 1f && Program.LeashWalkStart(1f) > Program.LeashWalkStop(1f));
        Assert.Equal("walk", Program.LeashWalkDecide(false, 0.85f, 1f, true));
    }
    [Fact] public void StopDistance_AtLeashNeverBumping()
    {
        Assert.Equal(3f, Program.LeashWalkStop(5f), 3);
        Assert.Equal(0.6f, Program.LeashWalkStop(0.5f), 3);
        Assert.Equal("stop", Program.LeashWalkDecide(true, 2.9f, 5f, true));
        Assert.Equal("", Program.LeashWalkDecide(true, 3.5f, 5f, true));   // keep walking until the stop distance
        Assert.Equal("stop", Program.LeashWalkDecide(true, 9f, 5f, false)); // seated/held/wander-off: stop
    }
    [Fact] public void Run_WhenHeRunsOrFar_UnlessRlvForbids()
    {
        Assert.True(Program.LeashShouldRun(4f, 1f, 1f, false));
        Assert.True(Program.LeashShouldRun(0f, 6f, 1f, false));
        Assert.False(Program.LeashShouldRun(1.5f, 1.5f, 1f, false));
        Assert.False(Program.LeashShouldRun(4f, 6f, 1f, true));
    }
    [Fact] public void WalkAnimDuringPull()
    {
        Assert.True(Program.PullNeedsWalkAnim(0.5f, false, false));
        Assert.False(Program.PullNeedsWalkAnim(0.1f, false, false));
        Assert.False(Program.PullNeedsWalkAnim(2f, true, false));
        Assert.False(Program.PullNeedsWalkAnim(2f, false, true));
    }
}
