using GalatayText;
using Xunit;
public class RlvAdjustHeightTests
{
    [Theory]
    [InlineData("3", 0.03)]                  // RLVa doc examples
    [InlineData("0.03;1", 0.03)]
    [InlineData("0.01;2;0.01;1", 0.03)]
    [InlineData("-2", -0.02)]                // the collar's Animator buttons: -2, 0, 2, 4 = centimetres
    [InlineData("0", 0.0)]
    [InlineData("4", 0.04)]
    [InlineData("500", 2.0)]                 // clamped to the hover range
    [InlineData("-500", -2.0)]
    [InlineData("10;1;0.5", 2.0)]
    public void Units(string opt, double want) => Assert.Equal(want, Program.RlvAdjustHeightParse(opt)!.Value, 4);
    [Theory]
    [InlineData("")] [InlineData("abc")] [InlineData(";1")]
    public void Malformed(string opt) => Assert.Null(Program.RlvAdjustHeightParse(opt));
    [Fact] public void BadFactorFallsBackToCentimetres() => Assert.Equal(0.02, Program.RlvAdjustHeightParse("2;x")!.Value, 4);
}
