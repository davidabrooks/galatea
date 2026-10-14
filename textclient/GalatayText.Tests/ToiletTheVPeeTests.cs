using GalatayText;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-14 David: a pee pose on the toilet triggers The V's pee (its touch menu "spray"); stopped before standing.</summary>
public class ToiletTheVPeeTests
{
    [Fact]
    public void Pee_poses_are_sit_and_hover_not_poop()
    {
        Assert.True(Program.ToiletPeePose(new[] { "Hover" }));
        Assert.True(Program.ToiletPeePose(new[] { "Sit" }));
        Assert.True(Program.ToiletPeePose(new[] { "F", "Pee 2" }));
        Assert.False(Program.ToiletPeePose(new[] { "Poop" }));
        Assert.False(Program.ToiletPeePose(null));
    }

    [Fact]
    public void Menu_buttons_and_the_v_body_not_its_hud()
    {
        var menu = new List<string> { "State 1", "State 2", "Cum", "Drip", "Spray", "Stop", "Cum Layer" };
        Assert.Equal("Spray", Program.TheVMenuButton(menu, true));
        Assert.Equal("Stop", Program.TheVMenuButton(menu, false));
        Assert.Null(Program.TheVMenuButton(new List<string> { "Cum", "Drip" }, true));
        Assert.True(Program.TheVBodyName("The V - Bento by Session Skins & ASA Studios"));
        Assert.False(Program.TheVBodyName("The V - Bento Play HUD by Session & ASA V1.61 COPY TRANSFER"));
    }
}
