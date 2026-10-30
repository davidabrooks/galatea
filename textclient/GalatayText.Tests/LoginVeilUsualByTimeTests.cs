using GalatayText;
using LibreMetaverse;
using Xunit;

// 2026-10-30 16:37, David's rule (replaces #357's always-swap): back within 10 minutes of the logout = exactly what I logged out in, even naked;
// more than 10 minutes = my usual outfit. Decided at login from the as-worn stash and the usual outfit list saved at logout.
public class LoginVeilUsualByTimeTests
{
    const string Hair = "DOUX - Oasis Hairstyle [M/Long]", Boots = "TETRA - Moca Ankle Boots (Suede) - Maitreya Mid", V = "The V - Bento by Session Skins & ASA Studios",
        Shirt = "TETRA - Chill T-Shirt - LaraX Petite", Shorts = "TETRA - Geena ripped shorts - LaraX (low waist)", Bracelet = ".Heol Star Bracelet Gold";
    static readonly UUID HairId = UUID.Random(), VId = UUID.Random(), ShirtId = UUID.Random(), ShortsId = UUID.Random(), BootsId = UUID.Random();
    static List<LoginVeil.Stashed> Naked() => new() { new(HairId, 2, Hair), new(VId, 28, V) };
    static List<(UUID, string)> Usual() => new() { (ShirtId, Shirt), (ShortsId, Shorts), (BootsId, Boots) };

    [Fact] public void QuickRelog_Naked_ComesBackExactlyAsWorn_AllHeld()
    {
        var p = UsualOutfit.AtLogin(Naked(), Usual(), "TETRA Chill T-Shirt shorts", TimeSpan.FromMinutes(4));
        Assert.False(p.Changed); Assert.Empty(p.Add); Assert.Empty(p.Drop);
        Assert.Equal(new[] { HairId, VId }, p.Stash.Select(x => x.Item));
        var (now, held) = LoginVeil.SplitStash(p.Stash);
        Assert.Empty(now); Assert.Equal(2, held.Count);   // all held to veil-off
        Assert.True(LoginVeil.Release(Array.Empty<LoginVeil.Garment>(), 0.5, stashed: true).release);   // nothing to wait for: the veil goes at once
    }

    [Fact] public void LongAbsence_WearsTheUsualOutfit()
    {
        var p = UsualOutfit.AtLogin(Naked(), Usual(), "TETRA Chill T-Shirt shorts", TimeSpan.FromMinutes(25));
        Assert.True(p.Changed);
        Assert.Contains(p.Stash, x => x.Item == ShirtId); Assert.Contains(p.Stash, x => x.Item == HairId);
        var (now, _) = LoginVeil.SplitStash(p.Stash);
        Assert.Contains(now, x => x.Item == ShirtId); Assert.Contains(now, x => x.Item == BootsId);
    }

    [Fact] public void Threshold_IsConfigurable_DefaultTen()
    {
        Assert.Equal(10, UsualOutfit.DefaultAfterMin);
        Assert.False(UsualOutfit.AtLogin(Naked(), Usual(), "x", TimeSpan.FromMinutes(9.9)).Changed);
        Assert.True(UsualOutfit.AtLogin(Naked(), Usual(), "x", TimeSpan.FromMinutes(10)).Changed);
        Assert.False(UsualOutfit.AtLogin(Naked(), Usual(), "x", TimeSpan.FromMinutes(25), afterMin: 30).Changed);
    }

    [Fact] public void UsualList_RoundTrips()
    {
        var (n, l) = UsualOutfit.ParseUsual(UsualOutfit.FormatUsual("TETRA Chill T-Shirt shorts", Usual()));
        Assert.Equal("TETRA Chill T-Shirt shorts", n); Assert.Equal(Usual(), l);
    }

    [Fact] public void QuickRelogWindow_StaysThirtyMinutes()
        => Assert.Equal(TimeSpan.FromMinutes(30), Program.ShortAbsence);
}
