using GalatayText;
using LibreMetaverse;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-07 19:39 "You chose a couples pose": KraftWork Beach Hammock ADULT, Adults* > 5Adults picked as solo.</summary>
public class SoloPosePickerTests
{
    static readonly List<string> Hammock = new() { "Adults*", "[ADJUST]", "[SWAP]", "SinglesSet*", "Mirror*", "Cuddles*" };

    [Fact]
    public void Hammock_top_menu_always_picks_SinglesSet()
    {
        var rnd = new Random(1);
        for (int i = 0; i < 200; i++)
        {
            Assert.Equal("SinglesSet*", Program.PickPoseButton(Hammock, null, rnd).pick);
            Assert.Equal("SinglesSet*", Program.PickPoseButton(Hammock, "5Adults", rnd, preferPgSolo: true).pick);
        }
    }

    [Theory]
    [InlineData("Adults*")] [InlineData("5Adults")] [InlineData("Mirror*")] [InlineData("Cuddles*")] [InlineData("Couple2")]
    [InlineData("Love*")] [InlineData("Kiss 1")] [InlineData("Spoon")] [InlineData("Duo*")] [InlineData("2P*")]
    [InlineData("His & Hers*")] [InlineData("M/F*")] [InlineData("F&M 3")] [InlineData("Sex*")]
    public void Intimate_buttons_never_picked_in_solo_mode(string bad)
    {
        var rnd = new Random(2);
        for (int i = 0; i < 50; i++)
            Assert.NotEqual(bad, Program.PickPoseButton(new() { bad, "SinglesSet*", "[ADJUST]" }, null, rnd).pick);
        Assert.Null(Program.PickPoseButton(new() { bad, "[ADJUST]", "[BACK]" }, null, rnd).pick);
    }

    [Fact]
    public void No_clearly_solo_option_stays_in_default_pose()
    {
        var (p, _, why) = Program.PickPoseButton(new() { "Adults*", "[ADJUST]", "Mirror*", "Cuddles*", "Clean*" }, null, new Random(3), preferPgSolo: true);
        Assert.Null(p);
        Assert.Contains("default pose", why);
    }

    [Fact]
    public void Inside_SinglesSet_leaves_are_fine_and_plain_menus_unchanged()
    {
        var rnd = new Random(4);
        var leaf = Program.PickPoseButton(new() { "1Set", "2Set", "3Set", "[BACK]" }, "3Set", rnd, preferPgSolo: true, inSubmenu: true).pick;
        Assert.True(leaf is "1Set" or "2Set");
        Assert.True(Program.PickPoseButton(new() { "Full Lotus*", "Misc*", "[ADJUST]", "Seiza*" }, null, rnd, preferPgSolo: true).pick is "Full Lotus*" or "Misc*" or "Seiza*");
        Assert.True(Program.PoseMenuHasSoloSubmenu(Hammock));
        Assert.False(Program.PoseMenuHasSoloSubmenu(new[] { "Adults*", "Mirror*", "Cuddles*" }));
    }

    [Fact]
    public void Adult_named_seat_needs_confirmed_singles()
    {
        const string n = "KraftWork Summer Shack . Beach Hammock ADULT";
        Assert.False(Program.SoloSeatNameOk(n, singlesConfirmed: false));
        Assert.True(Program.SoloSeatNameOk(n, singlesConfirmed: true));
        Assert.True(Program.SoloSeatNameOk("KraftWork Summer Shack . Beach Hammock", false));
    }
}
