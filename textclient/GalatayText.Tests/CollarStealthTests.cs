using Xunit; using GalatayText;

/// 2026-10-19 (David): android outfits make the N° 9 collar invisible (About -> Stealth), only when not already on.
public class CollarStealthTests
{
    [Theory]
    [InlineData("Android", true)] [InlineData("Android Base", true)] [InlineData("android wet", true)]
    [InlineData("Naked", false)] [InlineData("Bikini", false)] [InlineData("My Android", false)]
    public void AndroidOutfitNames(string n, bool want) => Assert.Equal(want, Program.IsAndroidOutfit(n));

    [Fact]
    public void StealthButtonState()
    {
        var about = new[] { "Config", "Update", "▴", "☑ Stealth", "Look" };
        Assert.Equal(("☑ Stealth", true), Program.StealthButton(about));
        Assert.Equal(("☐ Stealth", false), Program.StealthButton(new[] { "☐ Stealth", "Look" }));
        Assert.Null(Program.StealthButton(new[] { "Look" }).label);
    }
}
