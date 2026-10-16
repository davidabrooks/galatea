using GalatayText;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-16 14:17: with the android body worn every beach-bound wander leg first walked ~100 m up to the bedroom,
/// then logged "bikini skipped: android body worn" and walked back down. The detour (and the change-back detour on the
/// way up) is now planned only when the swap will really happen; one predicate (BikiniSwapSkipped) serves BikiniOn and
/// the planners.</summary>
public class BikiniSkipNoDetourTests
{
    [Fact]
    public void Swap_predicate_skips_only_automatic_callers_with_an_android_body()
    {
        Assert.True(Program.BikiniSwapSkipped(true, true));
        Assert.False(Program.BikiniSwapSkipped(true, false));
        Assert.False(Program.BikiniSwapSkipped(false, true));    // 'bikini on' typed by hand still wears it
        Assert.False(Program.BikiniSwapSkipped(false, false));
    }

    [Theory]
    [InlineData(false, false)]   // outside the house, on the beach level
    [InlineData(false, true)]
    [InlineData(true, false)]    // inside
    [InlineData(true, true)]
    public void Android_body_never_plans_the_bedroom_detour(bool indoors, bool inBeachZone)
    {
        bool skipped = Program.BikiniSwapSkipped(true, true);
        Assert.Equal("none", Program.IndoorBikiniPlan(true, false, indoors, inBeachZone, skipped));
    }

    [Fact]
    public void Human_body_keeps_the_existing_plans()
    {
        bool skipped = Program.BikiniSwapSkipped(true, false);
        Assert.Equal("detour-indoors", Program.IndoorBikiniPlan(true, false, false, true, skipped));
        Assert.Equal("detour-indoors", Program.IndoorBikiniPlan(true, false, false, false, skipped));
        Assert.Equal("change-here", Program.IndoorBikiniPlan(true, false, true, false, skipped));
        Assert.Equal("none", Program.IndoorBikiniPlan(false, false, false, false, skipped));   // not beach-bound
        Assert.Equal("none", Program.IndoorBikiniPlan(true, true, false, false, skipped));     // already in the bikini
        Assert.Equal("detour-indoors", Program.IndoorBikiniPlan(true, false, false, true));    // default arg = old behaviour
    }

    [Fact]
    public void No_change_back_detour_when_nothing_was_changed()
    {
        // beach mode left over from a skipped swap (android body): going up does not walk to the bedroom
        Assert.Equal("none", Program.ReturnChangePlan(false, true, Program.BikiniSwapSkipped(true, true)));
        // a real bikini swap: the change-back detour stays
        Assert.Equal("bedroom-restore", Program.ReturnChangePlan(false, true, Program.BikiniSwapSkipped(true, false)));
        Assert.Equal("bedroom-restore", Program.ReturnChangePlan(false, true));
        Assert.Equal("none", Program.ReturnChangePlan(false, false));
        Assert.Equal("none", Program.ReturnChangePlan(true, true));
    }

    [Fact]
    public void Level_dwell_pick_is_untouched()
    {
        var t0 = new DateTime(2026, 10, 16, 14, 0, 0);
        Assert.Equal("beach", Program.LevelToPick("beach", t0, t0.AddMinutes(29), 30, true, true));
        Assert.Equal("upper", Program.LevelToPick("beach", t0, t0.AddMinutes(30), 30, true, true));
    }
}
