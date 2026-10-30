using GalatayText;
using Xunit;

// 2026-10-28 11:42 relog (the real order from the log): shorts +4 s (Ready +6), boots +4, body +8, T-shirt +8 (Ready +11), seconds since 11:42:43. The shorts were Ready before the body existed.
public class LoginOrderTests
{
    const string Body = "Maitreya Mesh Body - LaraX V1.1", Shorts = "TETRA - Geena ripped shorts - LaraX (low waist)", Shirt = "TETRA - Chill T-Shirt - LaraX Petite";
    [Fact] public void TheLiveLogin_NamesTheShortsThatWereReadyBeforeTheBody()
    {
        var f = LoginOrder.Findings(new[] { new LoginOrder.Worn(Body, 8, null), new LoginOrder.Worn(Shorts, 4, 6), new LoginOrder.Worn(Shirt, 8, 11), new LoginOrder.Worn("Maitreya Mesh Body - LaraX HUD V1.1", 6, null) });
        Assert.Contains(f, l => l.Contains(Shorts) && l.Contains("BEFORE the body arrived") && l.Contains("skin showing"));
        Assert.DoesNotContain(f, l => l.Contains(Shirt));
        Assert.Contains("not confirmed", f[0]);
    }
    [Fact] public void EveryGarmentReadyAfterTheBody_GivesTheUncutWindow()
    {
        var f = LoginOrder.Findings(new[] { new LoginOrder.Worn(Body, 4, null), new LoginOrder.Worn(Shorts, 5, 8), new LoginOrder.Worn(Shirt, 5, 11) });
        Assert.Single(f); Assert.Contains("every garment was Ready by +11 s", f[0]); Assert.Contains("uncut for up to 7 s", f[0]);
    }
    [Fact] public void AGarmentThatNeverSaidReady_IsNamed()
        => Assert.Contains(LoginOrder.Findings(new[] { new LoginOrder.Worn(Body, 4, null), new LoginOrder.Worn(Shirt, 5, null) }), l => l.Contains("never announced Ready"));
    [Fact] public void NoBody_IsSaid() => Assert.Contains("body was not seen", LoginOrder.Findings(new[] { new LoginOrder.Worn(Shirt, 5, 9) })[0]);
    [Theory] [InlineData("Maitreya Mesh Body - LaraX V1.1", true)] [InlineData("Maitreya Mesh Body - LaraX HUD V1.1", false)] [InlineData("Maitreya Mesh Body - LaraX Petite Add-on V1.1", false)] [InlineData("TETRA - Chill T-Shirt - LaraX Petite", false)]
    public void IsBody(string n, bool b) => Assert.Equal(b, LoginOrder.IsBody(n));
    [Theory] [InlineData("TETRA - Chill T-Shirt - LaraX Petite", true)] [InlineData("TETRA - Moca Ankle Boots (Suede) - Maitreya Mid", false)] [InlineData("Maitreya Mesh Body - LaraX HUD V1.1", false)] [InlineData("DOUX - Oasis Hairstyle [M/Long]", false)]
    public void Garments(string n, bool g) => Assert.Equal(g, LoginOrder.IsBodyCuttingGarment(n));
    // 2026-10-30 10:19 login: bikini top Ready +4 s, body +11 s; the daily swap put the T-shirt on at +62 s (Ready +62) and took the bikini top off before the
    // +60 s check, which then said "every garment was Ready". The bikini top (seen at login, gone since) must be judged; the T-shirt is a later change.
    [Fact] public void DailySwapAfterLogin_DoesNotHideTheLoginGarment()
    {
        const string Top = "TETRA - Spicy Bikini Top - LaraX Petite";
        var f = LoginOrder.Findings(new[] { new LoginOrder.Worn(Body, 11, null), new LoginOrder.Worn(Top, 2, 4), new LoginOrder.Worn(Shirt, 62, 62), new LoginOrder.Worn(Shorts, 62, 62) });
        Assert.Contains("not confirmed", f[0]);
        Assert.Contains(f, l => l.Contains(Top) && l.Contains("BEFORE the body"));
        Assert.Contains(f, l => l.Contains(Shirt) && l.Contains("after login"));
        Assert.DoesNotContain(f, l => l.Contains("every garment was Ready"));
    }
}
