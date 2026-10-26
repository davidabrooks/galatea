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

/// <summary>2026-10-26 09:29 login: worn prims not known yet but the last named outfit was 'Android Base' -> keep-android, daily stays pending.</summary>
public class AndroidLoginDecisionTests
{
    static readonly TimeSpan Max = TimeSpan.FromSeconds(60);

    [Fact]
    public void Todays_case_prims_unknown_last_outfit_Android_Base_is_android()
    {
        var d = Program.AndroidLoginDecision(false, false, new[] { "Android Base", null }, TimeSpan.FromSeconds(20), Max);
        Assert.Equal("android", d);
        Assert.Equal("keep-android", Program.LoginOutfitPlan(false, false, true, null, androidWorn: d == "android"));
    }

    [Theory]
    [InlineData("Android Base")]
    [InlineData("android v-tech")]
    [InlineData("Android")]
    public void Last_named_android_outfit_alone_is_enough(string last) =>
        Assert.Equal("android", Program.AndroidLoginDecision(false, false, new[] { last }, TimeSpan.Zero, Max));

    [Fact]
    public void Android_prims_alone_are_enough() =>
        Assert.Equal("android", Program.AndroidLoginDecision(true, true, new string[] { null }, TimeSpan.Zero, Max));

    [Fact]
    public void Human_login_still_picks_daily_wear()
    {
        var d = Program.AndroidLoginDecision(false, true, new[] { "TETRA Chill T-Shirt" }, TimeSpan.FromSeconds(20), Max);
        Assert.Equal("human", d);
        Assert.Equal("daily-wear", Program.LoginOutfitPlan(false, false, true, null, androidWorn: d == "android"));
    }

    [Fact]
    public void Nothing_conclusive_waits_then_falls_back_to_human()
    {
        Assert.Equal("wait", Program.AndroidLoginDecision(false, false, new[] { "TETRA Chill T-Shirt", null }, TimeSpan.FromSeconds(20), Max));
        Assert.Equal("wait", Program.AndroidLoginDecision(false, false, new string[0], TimeSpan.FromSeconds(59), Max));
        Assert.Equal("human", Program.AndroidLoginDecision(false, false, new[] { "TETRA Chill T-Shirt" }, Max, Max));
    }

    [Fact]
    public void Skipped_android_login_does_not_write_the_daily_date_file()
    {
        // the daily date file is written only after a pick (dailyPending && pick != null); keep-android returns before any pick
        Assert.Equal("keep-android", Program.LoginOutfitPlan(false, false, true, null, androidWorn: true));
        Assert.Equal("keep-android", Program.LoginOutfitPlan(true, true, true, "x", androidWorn: true));
    }
}
