using Xunit;
using static GalatayText.Program;

namespace GalatayText.Tests;

/// <summary>2026-10-30: the hover.txt pin (-0.04, approved flat-footed) is footwear-blind; with f3 block-heel boots the calibrated check found -0.009.</summary>
public class HoverFootwearTests
{
    [Fact]
    public void Repin_uses_the_last_verified_stand_hover_for_the_worn_footwear()
    {
        var saved = FootwearHoverMerge(null, "f3", -0.009);
        Assert.Equal(-0.009, PinnedHoverFrom(null, null, PinTextFor("f3", saved, "-0.04")), 4);
        Assert.Equal(-0.04, PinnedHoverFrom(null, null, PinTextFor("f1", saved, "-0.04")), 4);   // never verified: hover.txt
        Assert.Equal(-0.04, PinnedHoverFrom(null, null, PinTextFor(null, saved, "-0.04")), 4);   // footwear unknown
        Assert.Equal(0.02, PinnedHoverFrom(0.02, null, PinTextFor("f3", saved, "-0.04")), 4);    // RLV override still wins
    }

    [Fact]
    public void Saving_one_footwear_keeps_the_others()
    {
        var d = FootwearHoverMerge(FootwearHoverMerge(null, "f1", -0.04), "f3", -0.00912);
        Assert.Equal(-0.04, d["f1"]); Assert.Equal(-0.0091, d["f3"]);
    }

    [Theory]
    [InlineData("show", null)]
    [InlineData("show stand", "stand")]
    [InlineData("show sit", "sit")]
    [InlineData("show here", "here")]
    public void Calibrate_show_accepts_an_optional_target(string words, string target)
    {
        var r = GalatayText.Hover.HoverCalibrateParse(words.Split(' '));
        Assert.True(r.ok, r.err); Assert.Equal("show", r.action); Assert.Equal(target, r.target);
    }
}
