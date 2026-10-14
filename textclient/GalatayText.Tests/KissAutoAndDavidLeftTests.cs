using System;
using GalatayText;
using Xunit;

public class KissAutoAndDavidLeftTests
{
    [Fact]
    public void Kisser_line_with_display_name_matches_legacy_self_name()   // 2026-10-13 20:05:01: never matched
    {
        var k = Program.ParseKissLine("★ 𝐏𝐫𝐞𝐭𝐭𝐲 𝐋𝐢𝐚𝐫𝐬 ★ David Nightingale 𝐤𝐢𝐬𝐬𝐞𝐬 Galatea Nightingale's 𝐥𝐢𝐩𝐬. ");
        Assert.True(Program.IsKissOnMe(k, "Galatay Resident", "David Nightingale", DateTime.Now, DateTime.MinValue));
        Assert.True(Program.IsKissOnMe(k, "Galatay Resident", "davidabrooks Resident", DateTime.Now, DateTime.MinValue));
        Assert.False(Program.NameIsMe("Someone Else", "Galatay Resident"));
    }

    [Fact]
    public void Kiss_response_thirds_cooldown_and_fallback()
    {
        var now = DateTime.Now; var old = DateTime.MinValue;
        Assert.Equal("kiss", Program.KissResponse(0.1, now, old, true, 1.0));
        Assert.Equal("im", Program.KissResponse(0.5, now, old, true, 1.0));
        Assert.Equal("both", Program.KissResponse(0.9, now, old, true, 1.0));
        Assert.Equal("im", Program.KissResponse(0.1, now, old, true, 12.0));   // too far
        Assert.Equal("im", Program.KissResponse(0.9, now, old, false, 1.0));   // kissed him recently
        Assert.Equal("none:cooldown", Program.KissResponse(0.1, now, now.AddSeconds(-30), true, 1.0));
    }

    [Fact]
    public void David_left_reaction()
    {
        Assert.Equal("stand_follow", Program.DavidLeftReaction(1.0, 2.0, false, 0.9).action);   // walking away
        Assert.Equal("stand_follow", Program.DavidLeftReaction(4.0, 4.0, false, 0.9).action);   // far
        Assert.Equal("solo", Program.DavidLeftReaction(1.0, 1.1, false, 0.9).action);           // adjusting
        Assert.Equal("stand_follow", Program.DavidLeftReaction(1.0, 1.1, false, 0.1).action);   // random join
        Assert.Equal("solo", Program.DavidLeftReaction(1.0, 2.0, true, 0.1).action);            // sat nearby
        Assert.Equal("stand_follow", Program.DavidLeftReaction(1.0, 8.0, true, 0.9).action);
    }
}
