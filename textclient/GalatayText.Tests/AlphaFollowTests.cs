using System.Collections.Generic; using System.Linq; using LibreMetaverse; using Xunit; using GalatayText;

/// 2026-10-19 (David): tied alphas follow their garment off; clothing tattoos are removable, skin/makeup/body ones are not.
public class AlphaFollowTests
{
    static readonly UUID F1 = UUID.Random(), F2 = UUID.Random();
    static Program.AlphaCand C(string n, UUID f) => new(UUID.Random(), n, f);

    [Fact]
    public void TiedBySameFolderOrName_BodyAlphaKept()
    {
        var shorts = C("TETRA - Geena ripped shorts", F1);
        var a1 = C("Geena alpha (legs)", F1);                 // same folder
        var a2 = C("TETRA - Geena ripped shorts Alpha", F2);  // name convention
        var body = C("LeL_EvoX(alpha)", F2);                  // head alpha, no tie
        var other = C("Valentine Dress Alpha", F2);
        var hits = Program.AlphasTiedTo(new[] { shorts }, new[] { a1, a2, body, other }, new HashSet<UUID>());
        Assert.Equal(new[] { a1.Item, a2.Item }, hits.Select(h => h.alpha.Item));
    }

    [Fact]
    public void ProtectedNeverRemoved()
    {
        var top = C("Silk top", F1); var a = C("Silk top alpha", F1);
        Assert.Empty(Program.AlphasTiedTo(new[] { top }, new[] { a }, new HashSet<UUID> { a.Item }));
    }

    [Theory]
    [InlineData("Lace panties tattoo", "", true)] [InlineData("Bra - pink", "", true)]
    [InlineData("Freckles tattoo", "", false)] [InlineData("VELOUR: BODY NECKFIX", "", false)]
    [InlineData("Lipstick red", "Outfit - Bikini", false)] [InlineData("Pink set", "Underwear", true)]
    [InlineData("Eyebrows dark", "", false)]
    public void ClothingLayerClassifier(string name, string folder, bool clothing) => Assert.Equal(clothing, Program.IsClothingLayer(name, folder));
}

public class VAutoTests
{
    [Fact]
    public void GroinCover_ByNameLayerOrPantiesTattoo()
    {
        Assert.True(Program.GroinCovered(new[] { "TETRA - Geena ripped shorts - LaraX" }, new (WearableType, string)[0]));
        Assert.False(Program.GroinCovered(new[] { "[ARTi'S] Strapless Top", "Shorts HUD" }, new (WearableType, string)[0]));
        Assert.True(Program.GroinCovered(new string[0], new[] { (WearableType.Underpants, "x") }));
        Assert.True(Program.GroinCovered(new string[0], new[] { (WearableType.Tattoo, "Lace panties tattoo") }));
        Assert.False(Program.GroinCovered(new string[0], new[] { (WearableType.Tattoo, "ANGEL Skin for LaraX") }));
    }

    [Fact]
    public void Decisions()
    {
        Assert.Equal("wear", Program.VDecision(false, false, "ARTi'S Strapless Top shorts", false));
        Assert.Equal("detach", Program.VDecision(true, true, "ARTi'S Strapless Top shorts", false));
        Assert.Null(Program.VDecision(true, true, "Naked", false));
        Assert.Null(Program.VDecision(true, true, "Bikini", true));     // outfit includes it
        Assert.Null(Program.VDecision(false, false, "Android Base", false));
        Assert.Null(Program.VDecision(false, true, "x", false));
    }
}

public class RlvPlaceTests
{
    [Fact]
    public void HoldResitsOnlyWhenHeldStandingAndNotReleasing()
    {
        Assert.True(Program.RlvHoldResit("seat", 0, false));
        Assert.False(Program.RlvHoldResit(null, 0, false));
        Assert.False(Program.RlvHoldResit("seat", 123, false));
        Assert.False(Program.RlvHoldResit("seat", 0, true));
    }
    [Fact]
    public void OnlyDeliberateStandsRelease()
    {
        Assert.True(Program.RlvReleasesHold("stand command")); Assert.True(Program.RlvReleasesHold("safeword"));
        Assert.False(Program.RlvReleasesHold("wander")); Assert.False(Program.RlvReleasesHold("height guard"));
    }
}

public class StandRuleTests
{
    // N° 9 README (Force Sit): Strict (@unsit=n) is what keeps her seated; TPE has nothing to do with standing
    [Fact]
    public void OnlyUnsitRestrictionBlocksDeliberateStand()
    {
        Assert.NotNull(Program.SelfStandBlock(true));
        Assert.Null(Program.SelfStandBlock(false));   // soft hold after a placement: a deliberate stand is fine
        Assert.NotNull(Program.RlvBlocks("stand", "", false, b => b == "unsit"));
        Assert.Null(Program.RlvBlocks("stand", "", true, b => b == "unsit"));   // the collar's own @unsit=force
    }

    [Fact]
    public void StoreExportRoundTrip()
    {
        var s = new Program.RlvStore(); var a = LibreMetaverse.UUID.Random();
        s.Add(a, "collar", "unsit", ""); s.Add(a, "collar", "tplure", "x");
        var e = s.Export().Single();
        Assert.Equal(new[] { "tplure:x", "unsit" }, e.keys.OrderBy(k => k));
    }
}

public class RlvDetachByUuidTests
{
    [Fact]
    public void RemattachByObjectUuid_TakesThatAttachmentOnly_CollarProtected()
    {
        var shortsPrim = LibreMetaverse.UUID.Random(); var shorts = LibreMetaverse.UUID.Random();
        var collar = LibreMetaverse.UUID.Random(); var collarPrim = LibreMetaverse.UUID.Random();
        var worn = new[] { new Program.RlvWorn(shorts, shortsPrim, "TETRA - Geena ripped shorts", 10),
                           new Program.RlvWorn(LibreMetaverse.UUID.Random(), LibreMetaverse.UUID.Random(), "Top", 1),
                           new Program.RlvWorn(collar, collarPrim, "No 9 Collar", 39) };
        var none = new HashSet<LibreMetaverse.UUID>();
        Assert.Equal(new[] { shorts }, Program.RlvDetachPlan(worn, shortsPrim.ToString(), none, none).Select(w => w.Item));
        Assert.Empty(Program.RlvDetachPlan(worn, collarPrim.ToString(), new HashSet<LibreMetaverse.UUID> { collar }, none));
    }
}

public class NoStripTests
{
    static readonly HashSet<LibreMetaverse.UUID> None = new();
    [Theory]
    [InlineData("Maitreya Mesh Body - LaraX V1.1")] [InlineData("/ EYES / lel evox 4.0")] [InlineData("Lelutka teeth & gums")]
    [InlineData("DOUX - Oasis Hairstyle [M/Long]")] [InlineData("MISAKI -Kay HandR")] [InlineData("VELOUR: BODY NECKFIX")]
    public void BodyPartsHidden(string n) => Assert.NotNull(Program.RlvNoStripWhy(LibreMetaverse.UUID.Random(), n, false, None));

    [Theory]
    [InlineData("TETRA - Geena ripped shorts - LaraX (low waist)")] [InlineData("[ARTi'S] Strapless Top - Lara PetiteX")]
    public void ClothingShown(string n)
    {
        // clothing fitted 'for LaraX' must stay strippable
        Assert.Null(Program.RlvNoStripWhy(LibreMetaverse.UUID.Random(), n, false, None));
    }
}

public class NippleRingsAndRedressTests
{
    static readonly (WearableType, string)[] NoLayers = new (WearableType, string)[0];
    [Fact]
    public void ChestCover()
    {
        Assert.True(Program.ChestCovered(new[] { "[ARTi'S] Strapless Top - Lara PetiteX" }, NoLayers));
        Assert.True(Program.ChestCovered(new[] { "PCP Beth Tube Top" }, NoLayers));
        Assert.False(Program.ChestCovered(new[] { "TETRA - Geena ripped shorts", "[ARTi'S] Strapless Top - HUD" }, NoLayers));
        Assert.True(Program.ChestCovered(new string[0], new[] { (WearableType.Tattoo, "Lace bra tattoo") }));
        Assert.False(Program.ChestCovered(new string[0], new[] { (WearableType.Tattoo, "Freckles body tattoo") }));
    }

    [Fact]
    public void RingsAreNoStrip() => Assert.Equal("body", Program.RlvNoStripWhy(Program.NippleRingsItem, "[BB] Nipple Rings", false, new HashSet<LibreMetaverse.UUID>()));

    [Fact]
    public void GetDressedAfterStrip_RedressesNotRandom()
    {
        // 2026-10-19 17:28: 'get dressed babe' picked a random outfit (PCP Beth Tube Top) instead of the pre-strip one
        Assert.Equal(("redress", "ARTi'S Strapless Top shorts"), Program.FastRedressOverride("random", "PCP Beth Tube Top", true, "ARTi'S Strapless Top shorts"));
        Assert.Equal(("random", "PCP Beth Tube Top"), Program.FastRedressOverride("random", "PCP Beth Tube Top", false, null));
        Assert.Equal(("named", "Bikini"), Program.FastRedressOverride("named", "Bikini", true, "ARTi'S Strapless Top shorts"));
        Assert.Equal("ARTi'S Strapless Top shorts", Program.PreStripCandidate(null, "ARTi'S Strapless Top shorts"));
        Assert.Equal("A", Program.PreStripCandidate("A", "B"));
        Assert.Null(Program.PreStripCandidate(null, "Naked")); Assert.Null(Program.PreStripCandidate(null, "Android Base"));
    }
}

public class StripWhyTests
{
    [Theory]
    [InlineData("rlv detach", true)] [InlineData("rlv remoutfit", true)] [InlineData("undress (tub)", true)]
    [InlineData("feet hud", false)] [InlineData("wear remove", false)] [InlineData("outfit swap", false)] [InlineData("alpha follow", false)]
    public void OnlyRealStripsStartAStrip(string why, bool strip) => Assert.Equal(strip, Program.IsStripWhy(why));
}
