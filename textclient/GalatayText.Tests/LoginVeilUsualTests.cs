using GalatayText;
using LibreMetaverse;
using Xunit;

// 2026-10-30 David took my clothes off in-world: "you should dress as usual when logging in". The stash only restored what was worn at logout.
public class LoginVeilUsualTests
{
    static readonly UUID Shirt = UUID.Random(), Shorts = UUID.Random(), Boots = UUID.Random(), Bracelet = UUID.Random(), Hair = UUID.Random(), Teeth = UUID.Random(), Other = UUID.Random(), Head = UUID.Random(), Ao = UUID.Random();
    static readonly (UUID, string)[] Usual = {
        (Shirt, "TETRA - Chill T-Shirt - LaraX Petite"), (Shorts, "TETRA - Geena ripped shorts - LaraX (low waist)"), (Boots, "TETRA - Moca Ankle Boots (Suede) - Maitreya Mid"),
        (Bracelet, ".Heol Star Bracelet Gold"), (Hair, "DOUX - Oasis Hairstyle [M/Long]"), (Head, "/ HEAD / lel evox / AVALON 4.0") };
    static LoginVeil.Stashed S(UUID id, int p, string n) => new(id, p, n);
    static List<LoginVeil.Stashed> HeadPartsOnly() => new() { S(Hair, 2, "DOUX - Oasis Hairstyle [M/Long]"), S(Teeth, 47, "/ GUMS / TEETH / lel evox 4.0") };

    [Fact] public void Naked_AtLogout_LoginWearsTheUsualClothes_WaitedByTheVeil()
    {
        var p = UsualOutfit.For(HeadPartsOnly(), Usual, "TETRA Chill T-Shirt shorts");
        Assert.True(p.Changed);
        Assert.Equal(new[] { Shirt, Shorts, Boots, Bracelet }.ToHashSet(), p.Add.Select(x => x.Item).ToHashSet());
        Assert.Empty(p.Drop);
        // the login sends these with the clothes at +1 s (not with the veil-off burst) and the veil waits for the LaraX ones
        var early = p.Stash.Where(x => !LoginVeil.RevealWithVeil(x.Name)).ToList();
        Assert.Contains(early, x => x.Item == Shirt && LoginVeil.Waits(x.Name)); Assert.Contains(early, x => x.Item == Shorts && LoginVeil.Waits(x.Name));
        // hair and teeth unchanged, the head is never added (never stashed: it stays on through logout)
        Assert.Contains(p.Stash, x => x.Item == Teeth && x.Point == 47); Assert.DoesNotContain(p.Stash, x => x.Item == Head);
        Assert.Single(p.Stash, x => x.Item == Hair);
    }

    [Fact] public void PartlyDressed_TheMissingUsualClothesAreAdded_WornPointsKept()
    {
        var s = HeadPartsOnly(); s.Add(S(Shorts, 10, "TETRA - Geena ripped shorts - LaraX (low waist)"));
        var p = UsualOutfit.For(s, Usual, "TETRA Chill T-Shirt shorts");
        Assert.True(p.Changed); Assert.Equal(10, p.Stash.Single(x => x.Item == Shorts).Point);
        Assert.DoesNotContain(p.Add, x => x.Item == Shorts); Assert.Contains(p.Add, x => x.Item == Shirt);
    }

    [Fact] public void OtherClothes_AreReplacedByTheUsualOnes()
    {
        var s = HeadPartsOnly(); s.Add(S(Other, 5, "PCP - Beth Tube Top - LaraX"));
        var p = UsualOutfit.For(s, Usual, "TETRA Chill T-Shirt shorts");
        Assert.Equal(new[] { Other }, p.Drop); Assert.DoesNotContain(p.Stash, x => x.Item == Other);
    }

    [Fact] public void WearingTheUsualOutfit_StashUnchanged()
    {
        var s = HeadPartsOnly();
        s.AddRange(new[] { S(Shirt, 55, "TETRA - Chill T-Shirt - LaraX Petite"), S(Shorts, 10, "TETRA - Geena ripped shorts - LaraX (low waist)"),
                           S(Boots, 7, "TETRA - Moca Ankle Boots (Suede) - Maitreya Mid"), S(Bracelet, 6, ".Heol Star Bracelet Gold") });
        var p = UsualOutfit.For(s, Usual, "TETRA Chill T-Shirt shorts");
        Assert.False(p.Changed); Assert.Same(s, s); Assert.Equal(s, p.Stash);
    }

    [Fact] public void UsualUnknown_StashAsWorn()
    {
        var s = HeadPartsOnly();
        Assert.False(UsualOutfit.For(s, null, "x").Changed); Assert.False(UsualOutfit.For(s, Usual, null).Changed);
        Assert.Equal(s, UsualOutfit.For(s, Usual, null).Stash);
    }

    [Fact] public void Name_IsTheLastClothedOutfit_NeverNakedOrAndroid()
    {
        Assert.Equal("TETRA Chill T-Shirt shorts", UsualOutfit.Name("TETRA Chill T-Shirt shorts", "Naked"));
        Assert.Equal("PCP Beth Tube Top", UsualOutfit.Name(null, "PCP Beth Tube Top"));
        Assert.Null(UsualOutfit.Name(null, "Naked"));
    }

    // the real My Outfits/TETRA Chill T-Shirt shorts object links (inv_ls 2026-10-30 16:2x PT): only these four are the clothes
    [Fact] public void RealUsualFolder_ClothesAreShirtShortsBootsBracelet()
    {
        var names = new[] { "-Pretty Liars- Real Kisser V7.0 [FEMALE HEAD]", ".Heol Star Bracelet Gold", ".Heol Star Earrings Gold", "/ EYES / lel evox 4.0",
            "/ GUMS / TEETH / lel evox 4.0", "/ HEAD / lel evox / AVALON 4.0", ":: No 9 :: Premium Collar", "DOUX - Oasis Hairstyle [M/Long]",
            "Maitreya Mesh Body - LaraX Petite Add-on V1.1", "Maitreya Mesh Body - LaraX V1.1", "TETRA - Chill T-Shirt - LaraX Petite",
            "TETRA - Geena ripped shorts - LaraX (low waist)", "TETRA - Moca Ankle Boots (Suede) - Maitreya Mid", "VISTA ANIMATIONS *HUD 6.3*MARTHA STS BENTO AO-V1.7" };
        Assert.Equal(new[] { ".Heol Star Bracelet Gold", "TETRA - Chill T-Shirt - LaraX Petite", "TETRA - Geena ripped shorts - LaraX (low waist)", "TETRA - Moca Ankle Boots (Suede) - Maitreya Mid" },
            names.Where(UsualOutfit.IsClothes));
    }
}
