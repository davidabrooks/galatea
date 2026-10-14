using GalatayText;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-14 David: mid-walk pauses at home look unnatural; home wander legs walk through (rests only at destinations).</summary>
public class HomeWanderContinuousWalkTests
{
    [Fact]
    public void No_mid_leg_look_around_on_home_wander_routes()
    {
        foreach (var l in new[] { "home wander leg to porch", "home wander to seat 'Lalou - Ultra Sofa v1.1'", "home wander recovery to beach", "home wander to the pier spot (boats)" })
            Assert.False(Program.MidLegIdlePauses(l), l);
        Assert.True(Program.MidLegIdlePauses("wander leg to zendo"));
        Assert.True(Program.MidLegIdlePauses("wander to seat 'x'"));
    }
}
