using GalatayText;
using LibreMetaverse;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-14 15:07: undress dropped only the COF links of the Bimbette alphas; the wearables stayed worn.</summary>
public class UndressVerifyTests
{
    [Fact]
    public void Leftover_layers_are_the_removed_ones_still_worn()
    {
        UUID legs = UUID.Random(), knees = UUID.Random(), butt = UUID.Random(), skin = UUID.Random();
        var left = Program.UndressLeftovers(new[] { legs, knees, butt }, new[] { skin, legs, butt });
        Assert.Equal(new[] { legs, butt }, left);
        Assert.Empty(Program.UndressLeftovers(new[] { legs }, new[] { skin }));
    }
}
