using System;
using System.Collections.Generic;
using System.Linq;
using GalatayText;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-16 13:17: android Galatea + David on BENTO PHYSICS MIRAGE BED 1.0, David typed 'pose couples' and she chose
/// Fist* &gt; fist &amp; balls. Explicit couples mode skipped the android / alone rule, and 'fist' / 'balls' / 'full job' were not
/// in the sexual word list. Real menu strings from the log.</summary>
public class PoseCouplesSexualRuleTests
{
    static readonly List<string> MirageTop = new() { "[SWAP]", "[<<]", "[>>]", "Fist*", "MF*", "[ADJUST]", "CUDDLES*", "ZERO G*", "WARM UP*", "F waits", "SOLO F*", "SLEEP*" };
    static readonly List<string> MirageFist = new() { "fist & balls", "full job", "[ADJUST]", "fist 2", "fist 3", "fist him", "[BACK]", "fist 1" };

    [Theory]
    [InlineData("Fist*")] [InlineData("fist & balls")] [InlineData("full job")] [InlineData("fist 2")] [InlineData("fist 3")]
    [InlineData("fist him")] [InlineData("fist 1")] [InlineData("MF*")] [InlineData("Handjob")] [InlineData("Gangbang*")] [InlineData("BJ")]
    public void Mirage_sexual_names_are_classified_sexual(string name)
        => Assert.True(Program.PosePathIsSexual(new[] { name }), name);

    [Theory]
    [InlineData("CUDDLES*")] [InlineData("ZERO G*")] [InlineData("WARM UP*")] [InlineData("F waits")] [InlineData("SOLO F*")] [InlineData("SLEEP*")]
    [InlineData("selfie")] [InlineData("champagne")] [InlineData("texting")] [InlineData("reading")] [InlineData("laptop")]
    public void Mirage_harmless_names_stay_harmless(string name)
        => Assert.False(Program.PosePathIsSexual(new[] { name }), name);

    [Fact]
    public void Explicit_couples_top_menu_never_picks_Fist_when_sexual_not_allowed()
    {
        for (int i = 0; i < 200; i++)
        {
            var (pick, _, _) = Program.PickPoseButton(MirageTop, "F waits", new Random(i), couplesMode: true, allowSexual: false);
            Assert.Equal("CUDDLES*", pick);   // the only couples-named, non-sexual category
        }
    }

    [Fact]
    public void Explicit_couples_inside_Fist_menu_picks_nothing_when_sexual_not_allowed()
    {
        for (int i = 0; i < 100; i++)
        {
            var r = Program.PickPoseButton(MirageFist, "F waits", new Random(i), couplesMode: true, inCouplesMenu: true, inSubmenu: true, allowSexual: false, inSexualMenu: true);
            Assert.Null(r.pick);
            // even without the parent-menu flag no leaf of that menu may be chosen
            var r2 = Program.PickPoseButton(MirageFist, "F waits", new Random(i), couplesMode: true, inCouplesMenu: true, inSubmenu: true, allowSexual: false);
            Assert.Null(r2.pick);
        }
    }

    [Fact]
    public void Unknown_harmless_looking_entry_inside_sexual_menu_is_skipped()
    {
        var menu = new List<string> { "Pose 7", "Smile", "[BACK]" };
        Assert.Null(Program.PickPoseButton(menu, null, new Random(1), couplesMode: true, inCouplesMenu: true, inSubmenu: true, allowSexual: false, inSexualMenu: true).pick);
        Assert.Null(Program.PickPoseButton(menu, null, new Random(1), inSubmenu: true, allowSexual: false, inSexualMenu: true).pick);
        Assert.NotNull(Program.PickPoseButton(menu, null, new Random(1), inSubmenu: true, allowSexual: false, inSexualMenu: false).pick);
    }

    [Fact]
    public void Explicit_couples_may_enter_sexual_menu_when_rule_allows()
    {
        var (pick, _, _) = Program.PickPoseButton(MirageTop, "F waits", new Random(1), couplesMode: true, allowSexual: true);
        Assert.NotNull(pick);
    }

    [Fact]
    public void Refusal_backstop_blocks_the_real_chosen_path_for_android_and_allows_when_rule_allows()
    {
        var logged = new[] { "Fist*", "fist & balls" };
        Assert.True(Program.PoseSexualRefused(logged, allowSexual: false));
        Assert.False(Program.PoseSexualRefused(logged, allowSexual: true));
        Assert.False(Program.PoseSexualRefused(new[] { "CUDDLES*", "hug" }, allowSexual: false));
    }

    [Fact]
    public void Android_and_not_alone_rules_still_decide_allowSexual()
    {
        var david = new LibreMetaverse.UUID("44ce5a36-c1c7-4a68-ac9a-635ddfff6233");
        Assert.False(Program.SexualPosesAllowed(true, "Peronaut", new[] { david }, david));
        Assert.False(Program.SexualPosesAllowed(false, "Peronaut", new[] { david, LibreMetaverse.UUID.Random() }, david));
        Assert.True(Program.SexualPosesAllowed(false, "Peronaut", new[] { david }, david));
    }
}
