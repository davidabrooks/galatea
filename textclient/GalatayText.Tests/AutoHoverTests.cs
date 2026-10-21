using GalatayText;
using Xunit;
public class AutoHoverTests
{
    [Fact] public void SeatedRestoresNormalHover() { Assert.Equal(0.02, Program.AutoHoverDecide("seat", true, 0.2, 0.15, 0.02, 1e9)); Assert.Null(Program.AutoHoverDecide("seat", true, null, 0.02, 0.02, 1e9)); }
    [Fact] public void RecentCollarHoverWins() => Assert.Null(Program.AutoHoverDecide("stand", false, 0.1, 0, 0, 120));
    [Fact] public void WithinToleranceLeavesIt() => Assert.Null(Program.AutoHoverDecide("stand", false, 0.02, 0, 0, 1e9));
    [Fact] public void FloatingLowersSinkingRaises()
    {
        Assert.Equal(-0.08, Program.AutoHoverDecide("stand", false, 0.08, 0, 0, 1e9));
        Assert.Equal(0.06, Program.AutoHoverDecide("kneel", false, -0.06, 0, 0, 1e9));
        Assert.Equal(0.05, Program.AutoHoverDecide("groundsit", false, -0.05, 0, 0, 1e9));
    }
    [Fact] public void ClampedAndOnlyKnownPostures()
    {
        Assert.Equal(-0.30, Program.AutoHoverDecide("stand", false, 0.9, 0, 0, 1e9));
        Assert.Null(Program.AutoHoverDecide("stand", false, 1.5, 0, 0, 1e9));
        Assert.Null(Program.AutoHoverDecide("moving", false, 0.1, 0, 0, 1e9));
    }
}
