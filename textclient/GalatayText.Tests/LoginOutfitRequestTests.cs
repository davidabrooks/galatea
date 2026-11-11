using GalatayText;
using LibreMetaverse;
using Xunit;

// 2026-11-11 (David t3258u): log in as Android Base; mid-login outfit_wear must beat the daily pick;
// android outfits hide EvoX ears and a later human outfit shows them again.
public class LoginOutfitRequestTests
{
    [Fact]
    public void ParseLoginOutfitArg_ReadsOutfitAndQuotes()
    {
        Assert.Null(UsualOutfit.ParseLoginOutfitArg(null));
        Assert.Null(UsualOutfit.ParseLoginOutfitArg(""));
        Assert.Null(UsualOutfit.ParseLoginOutfitArg("something else"));
        Assert.Equal("Android Base", UsualOutfit.ParseLoginOutfitArg("outfit Android Base"));
        Assert.Equal("Android Base", UsualOutfit.ParseLoginOutfitArg("outfit \"Android Base\""));
        Assert.Equal("Android Base", UsualOutfit.ParseLoginOutfitArg("outfit 'Android Base'"));
        Assert.Equal("Android Base", UsualOutfit.ParseLoginOutfitArg("--outfit Android Base"));
        Assert.Equal("Android Perky", UsualOutfit.ParseLoginOutfitArg("--outfit \"Android Perky\""));
        Assert.Null(UsualOutfit.ParseLoginOutfitArg("outfit"));
        Assert.Null(UsualOutfit.ParseLoginOutfitArg("outfit   "));
    }

    [Fact]
    public void ParseLoginOutfitText_FirstNonEmptyLine()
    {
        Assert.Null(UsualOutfit.ParseLoginOutfitText(null));
        Assert.Null(UsualOutfit.ParseLoginOutfitText("# comment\n\n"));
        Assert.Equal("Android Base", UsualOutfit.ParseLoginOutfitText("Android Base\n"));
        Assert.Equal("Android Base", UsualOutfit.ParseLoginOutfitText("# request\n\"Android Base\"\n"));
    }

    [Fact]
    public void RequestedOutfit_BeatsDailyAndBeach_AndDoesNotConsumeDaily()
    {
        Assert.Equal("wear-requested", Program.LoginOutfitPlan(false, false, true, null, false, "Android Base"));
        Assert.Equal("wear-requested", Program.LoginOutfitPlan(true, true, true, "tshirt", true, "Android Base"));
        Assert.Equal("keep-android", Program.LoginOutfitPlan(false, false, true, null, true, null));
        Assert.Equal("daily-wear", Program.LoginOutfitPlan(false, false, true, null, false, null));
    }

    [Fact]
    public void DailyPass_YieldsToAManualWearDuringLogin()
    {
        Assert.True(Program.DailyPassYieldsToManual("Android Base"));
        Assert.True(Program.DailyPassYieldsToManual("PCP Beth Tube Top"));
        Assert.False(Program.DailyPassYieldsToManual(null));
        Assert.False(Program.DailyPassYieldsToManual(""));
        Assert.False(Program.DailyPassYieldsToManual("   "));
    }

    [Fact]
    public void LoginTarget_RequestedOutfitHoldsAsWorn_SkipsDaily()
    {
        var since = TimeSpan.FromMinutes(120);
        var usual = new List<(UUID, string)> { (UUID.Random(), "TETRA - Chill T-Shirt - LaraX Petite") };
        var daily = new List<(UUID, string)> { (UUID.Random(), "Beth Top :: PetiteX - White :: Pink Cream Pie") };
        var t = UsualOutfit.LoginTarget(since, true, "TETRA Chill T-Shirt shorts", usual, "PCP Beth Tube Top", daily, requestedName: "Android Base");
        Assert.False(t.Daily);
        Assert.True(t.HoldAsWorn);
        Assert.Equal("Android Base", t.Name);
        Assert.Empty(t.Links);
        var worn = new List<LoginVeil.Stashed> { new(UUID.Random(), 10, "TETRA - Chill T-Shirt - LaraX Petite") };
        var p = UsualOutfit.AtLogin(worn, t.Links, t.Name, TimeSpan.Zero);
        Assert.False(p.Changed);
        Assert.Equal(worn[0].Item, p.Stash[0].Item);
    }
}

public class AndroidEarPolicyTests
{
    static EvoxHide.Parts Visible => new(false, false);
    static EvoxHide.Parts EarsHidden => new(true, false);
    static EvoxHide.Parts BothHidden => new(true, true);

    [Fact]
    public void ShowTarget_AndroidKeepsEarsHidden_StillShowsLashes()
    {
        Assert.Equal(new EvoxHide.Parts(true, false), EvoxHide.ShowTarget("ears,lashes", BothHidden, android: true));
        Assert.Equal(new EvoxHide.Parts(true, false), EvoxHide.ShowTarget("ears", EarsHidden, android: true));
        Assert.Equal(new EvoxHide.Parts(false, false), EvoxHide.ShowTarget("ears,lashes", BothHidden, android: false));
        Assert.Empty(EvoxHide.Presses(EarsHidden, EvoxHide.ShowTarget("ears,lashes", EarsHidden, android: true)));
        Assert.Equal(new[] { "lashes" }, EvoxHide.Presses(BothHidden, EvoxHide.ShowTarget("ears,lashes", BothHidden, android: true)).Select(x => x.what));
    }

    [Fact]
    public void EarTarget_HidesOrShowsEars_LeavesLashes()
    {
        Assert.Equal(new EvoxHide.Parts(true, false), EvoxHide.EarTarget(Visible, true));
        Assert.Equal(new EvoxHide.Parts(false, true), EvoxHide.EarTarget(BothHidden, false));
        Assert.Equal(new EvoxHide.Parts(true, true), EvoxHide.EarTarget(BothHidden, true));
        Assert.Null(EvoxHide.EarTarget(null, true));
        Assert.Empty(EvoxHide.Presses(EarsHidden, EvoxHide.EarTarget(EarsHidden, true)));
        Assert.Equal(new[] { "ears" }, EvoxHide.Presses(Visible, EvoxHide.EarTarget(Visible, true)).Select(x => x.what));
        Assert.Equal(new[] { "ears" }, EvoxHide.Presses(EarsHidden, EvoxHide.EarTarget(EarsHidden, false)).Select(x => x.what));
    }

    [Fact]
    public void HideEarsAtLogin_RequestedOrNamedOrWorn()
    {
        Assert.True(EvoxHide.HideEarsAtLogin(true, false, false));
        Assert.True(EvoxHide.HideEarsAtLogin(false, true, false));
        Assert.True(EvoxHide.HideEarsAtLogin(false, false, true));
        Assert.False(EvoxHide.HideEarsAtLogin(false, false, false));
        Assert.True(Program.IsAndroidOutfit("Android Base"));
        Assert.True(Program.IsAndroidOutfit("android perky"));
        Assert.False(Program.IsAndroidOutfit("PCP Beth Tube Top"));
        Assert.False(Program.IsAndroidOutfit(null));
    }
}
