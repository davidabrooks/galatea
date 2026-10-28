using GalatayText;
using Xunit;
public class AutoHoverTests
{
    [Fact] public void SeatedRestoresNormalHover() { Assert.Equal(0.02, Hover.AutoHoverDecide("seat", true, 0.2, 0.15, 0.02, 1e9)); Assert.Null(Hover.AutoHoverDecide("seat", true, null, 0.02, 0.02, 1e9)); }
    [Fact] public void RecentCollarHoverWins() => Assert.Null(Hover.AutoHoverDecide("stand", false, 0.1, 0, 0, 120));
    [Fact] public void WithinToleranceLeavesIt() => Assert.Null(Hover.AutoHoverDecide("stand", false, 0.02, 0, 0, 1e9));
    [Fact] public void FloatingLowersSinkingRaises()
    {
        Assert.Equal(-0.08, Hover.AutoHoverDecide("stand", false, 0.08, 0, 0, 1e9));
        Assert.Equal(0.06, Hover.AutoHoverDecide("kneel", false, -0.06, 0, 0, 1e9));
        Assert.Equal(0.05, Hover.AutoHoverDecide("groundsit", false, -0.05, 0, 0, 1e9));
    }
    [Fact] public void ClampedAndOnlyKnownPostures()
    {
        Assert.Equal(-0.30, Hover.AutoHoverDecide("stand", false, 0.9, 0, 0, 1e9));
        Assert.Null(Hover.AutoHoverDecide("stand", false, 1.5, 0, 0, 1e9));
        Assert.Null(Hover.AutoHoverDecide("moving", false, 0.1, 0, 0, 1e9));
    }
    [Fact] public void NoPerPoseGroundSitOffset_GapPathAndSeatedRestoreOnly()   // 2026-10-26 David: no saved/applied per-pose hover
    {
        Assert.Null(Hover.AutoHoverDecide("groundsit", false, null, -0.10, 0.02, 1e9));     // nothing to apply without a measured gap
        Assert.Equal(0, Hover.AutoHoverDecide("seat", true, null, -0.04, 0, 1e9));          // seated: back to normal
    }
}
