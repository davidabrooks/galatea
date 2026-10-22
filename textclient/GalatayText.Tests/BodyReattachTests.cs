using Xunit;
using GalatayText;

public class BodyReattachTests
{
    [Theory]
    [InlineData("Maitreya Mesh Body - LaraX V1.1", true)]
    [InlineData("Maitreya Mesh Body - LaraX HUD V1.1", false)]
    [InlineData("Maitreya Mesh Body - LaraX Petite Add-on V1.1", false)]
    [InlineData("Maitreya LaraX Vintage Jeans Low-Waist #3 (Flat)", false)]
    [InlineData(null, false)]
    public void OnlyTheBodyItselfMatches(string name, bool want) => Assert.Equal(want, Program.IsMaitreyaBodyName(name));
}
