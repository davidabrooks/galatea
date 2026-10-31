using GalatayText;
using LibreMetaverse;
using Xunit;

// 2026-10-31 David (t2850u): change to the next outfit pick any time I've been logged out more than an hour (not once per day),
// plus a one-shot force marker so a short relog can test the switch. Under 10 min stays exactly as worn.
public class OutfitChangeAfterHourTests
{
    const string Shirt = "TETRA - Chill T-Shirt - LaraX Petite", Top = "Beth Top :: PetiteX - White :: Pink Cream Pie",
        Jeans = "Maitreya LaraX Vintage Jeans Low-Waist #3 (Flat)", Hair = "DOUX - Oasis Hairstyle [M/Long]";
    static readonly UUID ShirtId = UUID.Random(), TopId = UUID.Random(), JeansId = UUID.Random(), HairId = UUID.Random();
    static List<(UUID, string)> Usual() => new() { (ShirtId, Shirt) };
    static List<(UUID, string)> Next() => new() { (TopId, Top), (JeansId, Jeans) };
    static List<LoginVeil.Stashed> Worn() => new() { new(HairId, 2, Hair), new(ShirtId, 10, Shirt) };

    [Fact] public void ChangeDue_OnlyPastTheHour()
    {
        Assert.False(UsualOutfit.OutfitChangeDue(TimeSpan.FromMinutes(5), false));
        Assert.False(UsualOutfit.OutfitChangeDue(TimeSpan.FromMinutes(30), false));
        Assert.False(UsualOutfit.OutfitChangeDue(TimeSpan.FromMinutes(60), false));
        Assert.True(UsualOutfit.OutfitChangeDue(TimeSpan.FromMinutes(61), false));
        Assert.True(UsualOutfit.OutfitChangeDue(TimeSpan.FromHours(30), false));   // a second change on the same day is fine now
    }

    [Fact] public void ForceMarker_MakesTheChangeDue_EvenOnAQuickRelog()
    {
        Assert.True(UsualOutfit.OutfitChangeDue(TimeSpan.FromMinutes(3), true));
        var since = TimeSpan.FromMinutes(3);
        var t = UsualOutfit.LoginTarget(since, true, "U", Usual(), "Bikini top jeans", Next(), forced: true);
        Assert.True(t.Daily);
        var p = UsualOutfit.AtLogin(Worn(), t.Links, t.Name, since, forced: true);
        Assert.True(p.Changed);
        Assert.Contains(p.Add, x => x.Item == TopId);
    }

    [Fact] public void QuickRelog_NotForced_StaysExactlyAsWorn()
    {
        var since = TimeSpan.FromMinutes(3);
        var t = UsualOutfit.LoginTarget(since, UsualOutfit.OutfitChangeDue(since, false), "U", Usual(), "D", Next());
        Assert.False(t.Daily);
        Assert.False(UsualOutfit.AtLogin(Worn(), t.Links, t.Name, since).Changed);
    }

    [Fact] public void BetweenTenMinutesAndAnHour_UsualOutfit_NotTheNextPick()
    {
        var since = TimeSpan.FromMinutes(40);
        var t = UsualOutfit.LoginTarget(since, UsualOutfit.OutfitChangeDue(since, false), "U", Usual(), "D", Next());
        Assert.False(t.Daily);
        Assert.Equal("U", t.Name);
    }

    [Fact] public void PastTheHour_NextPick()
    {
        var since = TimeSpan.FromMinutes(75);
        var t = UsualOutfit.LoginTarget(since, UsualOutfit.OutfitChangeDue(since, false), "U", Usual(), "D", Next());
        Assert.True(t.Daily);
        Assert.Equal("D", t.Name);
    }
}
