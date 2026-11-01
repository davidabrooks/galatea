using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-31 17:16 regression: the login stand check measured before the Moca boots' f3 foot pose was pressed, fixed to -0.25 and saved it
/// for f3 (she stood 0.25 m sunk); 17:19 a manual stand value replaced her ground-sit hover.</summary>
public class StandFeetBlockTests
{
    [Fact]
    public void Stand_check_waits_until_the_worn_footwear_feet_pose_is_verified()
    {
        Assert.NotNull(Hover.StandFeetBlock("f3", "f3", null));      // login: boots worn, f3 not pressed/verified yet (the 17:16 case)
        Assert.NotNull(Hover.StandFeetBlock("f3", "f3", "f1"));      // still the old flat-foot verification
        Assert.NotNull(Hover.StandFeetBlock("f1", "f3", "f3"));      // footwear changed during the check: never saved under the new key
        Assert.NotNull(Hover.StandFeetBlock(null, null, "f3"));      // footwear unknown
        Assert.Null(Hover.StandFeetBlock("f3", "f3", "f3"));         // settled: measure, fix, save for f3
    }

    [Fact]
    public void Manual_hover_apply_never_replaces_a_sit_hover_unless_forced()
    {
        Assert.NotNull(Hover.HoverApplyBlock("sit", false, false));   // AO ground sit (the 17:19 case)
        Assert.NotNull(Hover.HoverApplyBlock("kneel", false, false));
        Assert.NotNull(Hover.HoverApplyBlock("stand", true, false));  // seated on furniture
        Assert.Null(Hover.HoverApplyBlock("stand", false, false));
        Assert.Null(Hover.HoverApplyBlock("sit", false, true));
    }
}
