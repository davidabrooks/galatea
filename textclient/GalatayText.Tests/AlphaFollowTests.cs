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

public class TpeStandTests
{
    [Fact]
    public void TpeRules()
    {
        Assert.NotNull(Program.SelfStandBlock(true, false, false));   // @unsit=n always
        Assert.NotNull(Program.SelfStandBlock(false, true, true));    // placed + TPE on
        Assert.Null(Program.SelfStandBlock(false, true, false));      // placed, TPE off: deliberate stand ok
        Assert.Null(Program.SelfStandBlock(false, false, true));
        Assert.Equal(false, Program.TpeFromButtons(new[] { "☐ Group", "☐ TPE" }));
        Assert.Equal(true, Program.TpeFromButtons(new[] { "☑ TPE" }));
        Assert.Null(Program.TpeFromButtons(new[] { "About" }));
    }
}
