using GalatayText;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-07 'look self [front|back|left|right|deg|all]' side parsing (pure).</summary>
public class LookSelfViewsTests
{
    [Theory]
    [InlineData("back", "back")]
    [InlineData("all", "front,back,left,right")]
    [InlineData("Back,left,back", "back,left")]
    [InlineData("135", "135")]
    [InlineData("-45deg", "-45")]
    [InlineData("90.0", "90")]
    public void Valid_sides_and_degrees(string arg, string want) => Assert.Equal(want, Program.LookSelfViews(arg));

    [Theory]
    [InlineData("")]
    [InlineData("top")]
    [InlineData("400")]
    [InlineData("NaN")]
    [InlineData("all,45")]
    [InlineData("1,2,3,4,5,6,7,8,9")]
    public void Invalid_is_null(string arg) => Assert.Null(Program.LookSelfViews(arg));
}
