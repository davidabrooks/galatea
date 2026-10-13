using System;
using System.Collections.Generic;
using GalatayText;
using LibreMetaverse;
using Xunit;

namespace GalatayText.Tests;

public class SeatPoseSkipTests
{
    static readonly UUID Nutmeg = new("c78d42b6-c75c-df84-aa8c-8f78cbd2cb6c");
    static readonly List<string> Menu = new() { "m_sit3", "m_talkphone", "[ADJUST]", "f_sit3", "m_sit", "m_sit2", "f_reading", "f_talkphone", "f_phonetyping", "f_sit", "f_sit2" };

    [Fact]
    public void Nutmeg_chair_never_picks_reading_but_keeps_phone_poses()
    {
        var f = Program.SeatPoseFilter(Nutmeg, Menu);
        Assert.DoesNotContain("f_reading", f);
        Assert.Contains("f_talkphone", f); Assert.Contains("f_phonetyping", f);
        var rnd = new Random(1);
        for (int i = 0; i < 200; i++) Assert.NotEqual("f_reading", Program.PickPoseButton(f, "f_sit", rnd).pick);
    }

    [Fact]
    public void Other_seats_unchanged() => Assert.Contains("f_reading", Program.SeatPoseFilter(UUID.Random(), Menu));
}
