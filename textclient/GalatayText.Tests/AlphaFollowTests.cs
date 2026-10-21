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

public class LeashTests
{
    [Fact]
    public void WalkDistanceStaysInsideTheLeash()
    {
        Assert.Equal(2.1f, Program.LeashWalkDist(3f), 2);
        Assert.Equal(1f, Program.LeashWalkDist(0.5f));
        Assert.Equal(8f, Program.LeashWalkDist(20f));
        Assert.False(Program.LeashShouldWalk(2.5f, 3f));   // comfortable: the collar's pull does the rest
        Assert.True(Program.LeashShouldWalk(4f, 3f));
    }

    [Fact]
    public void StrictComesFromRestrictionsOrAnchorAndNeverFromFollow()
    {
        Assert.True(Program.LeashIsStrict("leash", false, b => b == "tploc"));
        Assert.True(Program.LeashIsStrict("leash", true, b => false));
        Assert.False(Program.LeashIsStrict("leash", false, b => b == "sendchat"));
        Assert.False(Program.LeashIsStrict("follow", true, b => true));   // Peanut docs: follow "cannot be strict"
    }

    [Fact]
    public void StrictBlocksTeleportsSelfUnleashAndOtherLuresButNotChat()
    {
        Assert.NotNull(Program.LeashBlocks("tp", "Peronaut 1 2 3", true, "David Nightingale", null));
        Assert.NotNull(Program.LeashBlocks("home", "", true, "David Nightingale", null));
        Assert.NotNull(Program.LeashBlocks("leash", "release", true, "David Nightingale", null));
        Assert.NotNull(Program.LeashBlocks("accept", "", true, "David Nightingale", "Someone Else"));
        Assert.Null(Program.LeashBlocks("accept", "", true, "David Nightingale", "David Nightingale"));
        Assert.Null(Program.LeashBlocks("say", "MAYDAY", true, "David Nightingale", null));
        Assert.Null(Program.LeashBlocks("tp", "Peronaut 1 2 3", false, "David Nightingale", null));
        Assert.Null(Program.LeashBlocks("leash", "release", false, "David Nightingale", null));
    }

    [Fact]
    public void CollarTextHeuristics()
    {
        Assert.Equal(("leash", "David Nightingale"), Program.LeashFromText("David Nightingale grabbed your leash."));
        Assert.Equal("unleash", Program.LeashFromText("You have been unleashed.")?.kind);
        Assert.Equal(("follow", "David Nightingale"), Program.LeashFromText("David Nightingale is now following you."));
        Assert.Equal("unfollow", Program.LeashFromText("You stopped following David.")?.kind);
        Assert.Equal(("follow", "David Nightingale"), Program.LeashFromText("David Nightingale follows you"));
        Assert.Null(Program.LeashFromText("@tploc=n"));
        Assert.Null(Program.LeashFromText("Collar: RLV ready!"));
    }

    [Fact]
    public void SavedStateRoundTripsWithAndWithoutLeash()
    {
        var old = System.Text.Json.JsonSerializer.Deserialize<Program.RlvStateDoc>("{\"sources\":[],\"holdSeat\":null,\"holdName\":null,\"holdRegion\":null}");
        Assert.Null(old!.leash);
        var doc = new Program.RlvStateDoc(new(), null, null, "Peronaut", new Program.LeashSaved("leash", "44ce5a36-c1c7-4a68-ac9a-635ddfff6233", "David Nightingale", false, 3f, "particle chain", "Peronaut"));
        var back = System.Text.Json.JsonSerializer.Deserialize<Program.RlvStateDoc>(System.Text.Json.JsonSerializer.Serialize(doc));
        Assert.Equal("David Nightingale", back!.leash!.holderName);
        Assert.False(old.leashWalk);
        Assert.True(old.leashWanderOff);   // the playful wander-off is on by default
    }

    [Fact]
    public void ForgetAndWanderOffRules()
    {
        var t0 = new DateTime(2026, 10, 19, 19, 0, 0);
        Assert.Equal(t0.AddMinutes(10), Program.LeashNextForget(t0, 0));
        Assert.Equal(t0.AddMinutes(20), Program.LeashNextForget(t0, 1));
        Assert.True(Program.LeashMayForget(true, false, false, false, true, t0, t0));
        Assert.False(Program.LeashMayForget(false, false, false, false, true, t0, t0));   // option off
        Assert.False(Program.LeashMayForget(true, true, false, false, true, t0, t0));    // sitting
        Assert.False(Program.LeashMayForget(true, false, true, false, true, t0, t0));    // placed / held
        Assert.False(Program.LeashMayForget(true, false, false, true, true, t0, t0));    // @unsit
        Assert.False(Program.LeashMayForget(true, false, false, false, true, t0, t0.AddMinutes(1)));   // not due
        var tgt = Program.LeashForgetTarget(new LibreMetaverse.Vector3(100, 100, 20), new LibreMetaverse.Vector3(101, 100, 21), null, 2.5f);
        Assert.Equal(104f, tgt.X, 2); Assert.Equal(100f, tgt.Y, 2); Assert.Equal(21f, tgt.Z, 2);   // just 1.5 m past the leash
        Assert.Equal(4f, Program.LeashLengthFromText("Leash length: 4 m"));
        Assert.Null(Program.LeashLengthFromText("Leash grabbed"));
        Assert.Equal(Program.ShoelaceM, Program.LeashLengthFromText("Length set to Shoelace"));   // walking after the holder is off by default
    }
}

public class LongGapLoginRegressionTests
{
    // 2026-10-21 regression: after ~38 h offline she logged in back on the bed. The MCP connector launcher never set
    // GT_START, and the built-in default was "last", so the long-gap rule (LoginStart.cs) chose "last" anyway.
    [Fact]
    public void LongGapLogsInAtTheNormalEntryPointByDefault()
    {
        var now = new DateTime(2026, 10, 21, 15, 9, 0, DateTimeKind.Utc);
        if (Environment.GetEnvironmentVariable("GT_START") == null) Assert.Equal("home", Program.Start);
        Assert.Equal("home", Program.ChooseLoginStart("home", now.AddHours(-38), now));
        Assert.Equal("last", Program.ChooseLoginStart("home", now.AddMinutes(-5), now));
        Assert.Equal("home", Program.ChooseLoginStart("home", null, now));
    }

    [Fact]
    public void PlacementIsRestoredOnlyAfterAQuickRelog()
    {
        Assert.True(Program.RestorePlacementAfterRelog(TimeSpan.FromMinutes(5)));
        Assert.False(Program.RestorePlacementAfterRelog(TimeSpan.FromHours(38)));
        Assert.False(Program.RestorePlacementAfterRelog(TimeSpan.FromMinutes(30)));
        Assert.False(Program.RestorePlacementAfterRelog(null));
    }
}

public class CofPurgeTests
{
    // 2026-10-21: leg alphas left linked in Current Outfit after a swap kept her thighs/knees invisible
    [Fact]
    public void StrayAlphaLinksNotInTheSavedOutfitArePurged()
    {
        var legs = LibreMetaverse.UUID.Random(); var butt = LibreMetaverse.UUID.Random(); var skin = LibreMetaverse.UUID.Random(); var keep = LibreMetaverse.UUID.Random();
        var cof = new[] {
            new Program.CofLinkInfo(LibreMetaverse.UUID.Random(), legs, "Bimbette /// Alpha Layer /// Upper Legs & Knees", true),
            new Program.CofLinkInfo(LibreMetaverse.UUID.Random(), butt, "Bimbette /// Alpha Layer /// Butt 1", true),
            new Program.CofLinkInfo(LibreMetaverse.UUID.Random(), keep, "outfit's own alpha", true),
            new Program.CofLinkInfo(LibreMetaverse.UUID.Random(), skin, "VELOUR skin", false) };
        var stray = Program.CofStrayAlphaLinks(cof, new HashSet<LibreMetaverse.UUID> { keep });
        Assert.Equal(2, stray.Count);
        Assert.DoesNotContain(stray, s => s.Target == keep || s.Target == skin);   // outfit alphas and non-alpha layers stay
    }
}
