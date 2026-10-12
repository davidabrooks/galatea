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
