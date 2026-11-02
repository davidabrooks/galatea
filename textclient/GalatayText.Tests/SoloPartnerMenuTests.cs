using GalatayText;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-11-02 15:46:58 David "not singular": wander on 'Xplicit Makeup Table (c,m)' chose Solo* > Pictures* > Peeking.
/// The Pictures* submenu also lists Couple / Couple2, so its plain-named entries are the halves of partner poses.</summary>
public class SoloPartnerMenuTests
{
    static readonly List<string> Pictures = new() { "Reach", "Couple", "Couple2", "Check Sit", "Coy", "Peeking", "[BACK]", "Check Heel", "Check Me" };
    static readonly List<string> MirageCuddles = new() { "[ADJUST]", "[<<]", "[>>]", "backpack", "hold hand", "hug you", "hold her", "in my eyes", "time", "[BACK]", "comfy", "chilling" };

    [Fact]
    public void Pictures_submenu_with_couples_entries_picks_nothing_solo()
    {
        var rnd = new Random(7);
        for (int i = 0; i < 100; i++)
        {
            var (p, _, why) = Program.PickPoseButton(Pictures, "Legs Up", rnd, preferPgSolo: true, inSubmenu: true, adultSeat: true);
            Assert.Null(p);
            Assert.Contains("partner", why);
        }
    }

    [Fact]
    public void Partner_menu_still_allows_a_clearly_solo_named_entry()
    {
        var rnd = new Random(8);
        for (int i = 0; i < 50; i++)
            Assert.Equal("Solo sit", Program.PickPoseButton(new() { "Couple", "Reach", "Solo sit", "[BACK]" }, null, rnd, inSubmenu: true).pick);
    }

    [Fact]
    public void Couples_submenu_with_neutral_names_picks_nothing_solo()
    {
        Assert.Null(Program.PickPoseButton(MirageCuddles, null, new Random(9), inSubmenu: true).pick);
    }

    [Fact]
    public void Peeking_is_never_a_solo_pick_even_without_couples_siblings()
    {
        var rnd = new Random(10);
        for (int i = 0; i < 100; i++)
            Assert.NotEqual("Peeking", Program.PickPoseButton(new() { "Peeking", "Coy", "Check Me", "[BACK]" }, null, rnd, inSubmenu: true).pick);
        Assert.Null(Program.PickPoseButton(new() { "Peeking", "[BACK]" }, null, rnd, inSubmenu: true).pick);
    }

    [Fact]
    public void Plain_submenu_without_partner_entries_still_picks()
    {
        var p = Program.PickPoseButton(new() { "Coy", "Check Me", "Check Heel", "[BACK]" }, null, new Random(11), inSubmenu: true).pick;
        Assert.Contains(p, new[] { "Coy", "Check Me", "Check Heel" });
    }

    [Fact]
    public void Not_solo_names_extend_from_rules_json()
    {
        var r = PoseRuleSet.FromJson("{\"not_solo_names\": [\"Reach\", \"Over shoulder*\"]}");
        Assert.True(r.IsNotSoloName("Reach")); Assert.True(r.IsNotSoloName("over shoulder*")); Assert.True(r.IsNotSoloName("Peeking"));
        Assert.False(r.IsNotSoloName("Coy"));
    }
}
