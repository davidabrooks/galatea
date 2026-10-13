using GalatayText;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-12: android body (Kay/CALISTA) never wanders to the toilet, bathtub, showers or hot tub.</summary>
public class AndroidWetTests
{
    [Theory]
    [InlineData("BackBone Playtime Toilet", "toilet")]
    [InlineData("Dutchie clawfoot bath tub adult", "undress")]
    [InlineData("Nutmeg. Outdoor Shower Adult", "shower")]
    [InlineData("Dutchie hot tub Adult", null)]
    [InlineData("Beach Shower", null)]
    public void Wet_spots_are_skipped_with_an_android_body(string name, string special)
    {
        Assert.True(Program.AndroidWetSpot(name, special));
        Assert.False(Program.AndroidSeatAllowed(true, name, special));
        Assert.True(Program.AndroidSeatAllowed(false, name, special));   // human body: unchanged
    }

    [Theory]
    [InlineData("Lalou - Ultra Sofa v1.1")]
    [InlineData("BackBone Playtime Bathroom Sink - Adult")]
    [InlineData("Nerenzo beach chair - adult (right)")]
    [InlineData("Atelier Burgundy . Burgundy Pier . Bench")]
    [InlineData("Nutmeg. Marsh Rowboat White w/Blankets (Swaying)")]
    public void Other_seats_stay(string name) => Assert.True(Program.AndroidSeatAllowed(true, name, null));

    [Fact]
    public void Body_family_detection_is_the_outfit_one()
    {
        Assert.True(Program.IsAndroidFamily(Program.BodyFamily("MISAKI -Kay Body V-Tech")));
        Assert.True(Program.IsAndroidFamily(Program.BodyFamily("CALISTA body")));
        Assert.False(Program.IsAndroidFamily(Program.BodyFamily("Maitreya Mesh Body - LaraX V1.1")));
    }
}

public class AndroidLoginOutfitTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, false, false)]
    public void Android_login_keeps_the_outfit(bool beach, bool bikini, bool daily) =>
        Assert.Equal("keep-android", Program.LoginOutfitPlan(beach, bikini, daily, "tshirt", androidWorn: true));

    [Fact]
    public void Human_login_unchanged()
    {
        Assert.Equal("daily-wear", Program.LoginOutfitPlan(false, false, true, null, androidWorn: false));
        Assert.Equal("bikini-on", Program.LoginOutfitPlan(true, false, false, null));
    }
}
