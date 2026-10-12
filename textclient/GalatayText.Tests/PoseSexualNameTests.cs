using System;
using System.Collections.Generic;
using GalatayText;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-12 15:24: solo wander picked Lalou Ultra Sofa Solo > Solo Couch > "Sexy sit".</summary>
public class PoseSexualNameTests
{
    [Fact]
    public void Solo_couch_never_picks_sexy_sit()
    {
        var menu = new List<string> { "Sexy sit", "Meh", "Cross legs", "Feet down", "[BACK]" };
        var rnd = new Random(3);
        for (int i = 0; i < 300; i++)
        {
            var p = Program.PickPoseButton(menu, "Meh", rnd, preferPgSolo: true, inSubmenu: true).pick;
            Assert.NotEqual("Sexy sit", p);
        }
    }

    [Theory]
    [InlineData("Sexy sit")] [InlineData("Seduce")] [InlineData("FoldFuck")] [InlineData("CreamyCunt")] [InlineData("Analwork")]
    [InlineData("DeepThroat")] [InlineData("Cowgirl")] [InlineData("Tit job")] [InlineData("Boob nRub")] [InlineData("Ride me")]
    [InlineData("Vibrator")] [InlineData("Dildo")] [InlineData("Diddle")] [InlineData("Grind")] [InlineData("69")] [InlineData("Teasing*")]
    public void Sexual_names_are_never_auto_picked(string name)
    {
        var p = Program.PickPoseButton(new List<string> { name, "Cross legs" }, null, new Random(1), inSubmenu: true);
        for (int i = 0; i < 50; i++) Assert.NotEqual(name, Program.PickPoseButton(new List<string> { name, "Cross legs" }, null, new Random(i), inSubmenu: true).pick);
    }

    [Theory]
    [InlineData("Cross legs")] [InlineData("Meh")] [InlineData("Laptop")] [InlineData("Lay read")] [InlineData("Contemplate")] [InlineData("Curled up")]
    [InlineData("Dreamy")] [InlineData("Watching")] [InlineData("Vogue")] [InlineData("Backlean")] [InlineData("Belly lay")] [InlineData("f_talkphone")]
    [InlineData("f_sit2")] [InlineData("Hang out")] [InlineData("Listening")] [InlineData("Relaxing")] [InlineData("Seat")] [InlineData("Pretty")]
    public void Neutral_names_stay(string name) => Assert.Equal(name, Program.PickPoseButton(new List<string> { name }, null, new Random(1), inSubmenu: true).pick);
}

public class SexualPoseRuleTests
{
    static readonly LibreMetaverse.UUID David = new("44ce5a36-c1c7-4a68-ac9a-635ddfff6233");

    [Fact]
    public void Android_outfit_blocks_sexual_poses_even_with_only_David()
        => Assert.False(Program.SexualPosesAllowed(true, "Peronaut", new[] { David }, David));

    [Fact]
    public void Human_outfit_with_only_David_allows_and_needs_naked_first()
    {
        Assert.True(Program.SexualPosesAllowed(false, "Peronaut", new[] { David, David }, David));
        Assert.True(Program.PosePathIsSexual(new[] { "Solo*", "Solo Couch*", "Sexy sit" }));
        Assert.True(Program.PosePathIsSexual(new[] { "Solo*", "Solo Adult*", "Feeling" }));
        Assert.False(Program.PosePathIsSexual(new[] { "Solo*", "Solo Couch*", "Meh" }));
        // with permission the picker may choose it; couples names stay out
        var menu = new List<string> { "Sexy sit", "Together", "Cuddles*" };
        bool sawSexy = false;
        for (int i = 0; i < 100; i++) { var p = Program.PickPoseButton(menu, null, new Random(i), inSubmenu: true, allowSexual: true).pick; Assert.NotEqual("Together", p); Assert.NotEqual("Cuddles*", p); sawSexy |= p == "Sexy sit"; }
        Assert.True(sawSexy);
    }

    [Fact]
    public void Human_outfit_with_others_or_alone_or_elsewhere_blocks()
    {
        var other = LibreMetaverse.UUID.Random();
        Assert.False(Program.SexualPosesAllowed(false, "Peronaut", new[] { David, other }, David));
        Assert.False(Program.SexualPosesAllowed(false, "Peronaut", new LibreMetaverse.UUID[0], David));
        Assert.False(Program.SexualPosesAllowed(false, "Naberrie", new[] { David }, David));
        for (int i = 0; i < 50; i++) Assert.NotEqual("Sexy sit", Program.PickPoseButton(new List<string> { "Sexy sit", "Meh" }, null, new Random(i), inSubmenu: true, allowSexual: false).pick);
    }
}
