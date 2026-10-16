using System;
using System.Collections.Generic;
using System.Linq;
using GalatayText;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-16 15:51:47 Xplicit Makeup Table (c,m), human body, dressed: "chose Solo* > Masturbate* > Jiller (... pg-solo)". The
/// 'Masturbate*' menu and 'Jiller' were not in the sexual word list, so the pose was taken as PG: no Naked outfit first.</summary>
public class AdultPoseClassifyTests
{
    static readonly string[] Jiller = { "Solo*", "Masturbate*", "Jiller" };
    const string Table = "Xplicit Makeup Table (c,m)";

    [Fact]
    public void The_exact_live_path_is_sexual_and_refused_without_permission()
    {
        Assert.True(Program.PosePathIsSexual(Jiller));
        Assert.True(Program.PosePathIsSexual(Jiller, Program.PoseAdultSeatName(Table)));
        Assert.True(Program.PoseSexualRefused(Jiller, allowSexual: false));
        Assert.False(Program.PoseSexualRefused(Jiller, allowSexual: true));   // allowed (human, alone): then the Naked step runs first
        Assert.True(Program.PoseAdultSeatName(Table));
        Assert.False(Program.PoseAdultSeatName("BENTO PHYSICS MIRAGE BED 1.0"));
    }

    [Theory]
    [InlineData("Masturbate*")] [InlineData("Jiller")] [InlineData("Jilling")] [InlineData("Clit Play")] [InlineData("Boobies")]
    [InlineData("Grope")] [InlineData("PussyRub")] [InlineData("Diddle")] [InlineData("Vibrator")] [InlineData("Dildo")] [InlineData("Foreplay*")]
    [InlineData("Fist*")] [InlineData("fist & balls")] [InlineData("Blowjobs*")] [InlineData("Mutual FP*")] [InlineData("Adult M+F*")] [InlineData("MF*")]
    [InlineData("Sex Behind*")] [InlineData("Handjobs*")] [InlineData("Going Down*")] [InlineData("Jerk off")] [InlineData("Wank")]
    public void Sexual_names_are_classified_sexual(string name) => Assert.True(Program.PosePathIsSexual(new[] { name }), name);

    [Theory]
    [InlineData("Lady")] [InlineData("So Pretty")] [InlineData("Phone Talk")] [InlineData("Legs Up")] [InlineData("Check Sit")] [InlineData("Coy")]
    [InlineData("Stand Preen*")] [InlineData("Pictures*")] [InlineData("Solo Sits*")] [InlineData("selfie")] [InlineData("champagne")] [InlineData("texting")]
    [InlineData("reading")] [InlineData("laptop")] [InlineData("SOLO F*")] [InlineData("Cross legs")] [InlineData("Hang out")] [InlineData("Vogue")] [InlineData("Quietly")]
    public void Plain_names_stay_plain(string name) => Assert.False(Program.PosePathIsSexual(new[] { name }), name);

    [Fact]
    public void A_pose_inherits_sexual_from_its_parent_menu()
    {
        // 'Quietly' and 'Read' sit inside Masturbate*: harmless names, sexual path
        Assert.True(Program.PosePathIsSexual(new[] { "Solo*", "Masturbate*", "Quietly" }));
        Assert.True(Program.PosePathIsSexual(new[] { "Solo*", "Masturbate*", "Read" }));
        Assert.False(Program.PosePathIsSexual(new[] { "Solo*", "Solo Sits*", "Lady" }));
    }

    [Fact]
    public void The_picker_never_enters_or_picks_inside_Masturbate_without_permission()
    {
        var menu = new List<string> { "Stand Preen*", "Masturbate*", "Pictures*", "[BACK]", "Solo Sits*" };
        for (int i = 0; i < 300; i++)
        {
            var r = Program.PickPoseButton(menu, "Legs Up", new Random(i), preferPgSolo: true, inSubmenu: true, adultSeat: true);
            Assert.NotEqual("Masturbate*", r.pick);
            var r2 = Program.PickPoseButton(menu, "Legs Up", new Random(i), preferPgSolo: true, inSubmenu: true);   // even without the adult-seat rule (word list)
            Assert.NotEqual("Masturbate*", r2.pick);
        }
        var inside = new List<string> { "Quietly", "Read", "Clit Play", "[BACK]", "Jiller", "Jilling" };
        Assert.Null(Program.PickPoseButton(inside, null, new Random(1), preferPgSolo: true, inSubmenu: true, inSexualMenu: Program.PosePathIsSexual(new[] { "Solo*", "Masturbate*" }, true), adultSeat: true).pick);
    }

    [Fact]
    public void With_permission_the_adult_menu_can_be_entered_and_the_leaf_needs_the_naked_step()
    {
        var menu = new List<string> { "Masturbate*" };
        var r = Program.PickPoseButton(menu, null, new Random(1), preferPgSolo: true, inSubmenu: true, allowSexual: true, adultSeat: true);
        Assert.Equal("Masturbate*", r.pick);
        Assert.True(Program.PosePathIsSexual(new[] { "Solo*", "Masturbate*", "Jiller" }, true));   // SeatPose runs the stand / Naked / sit step on this
    }

    [Fact]
    public void An_unknown_submenu_on_an_adult_looking_seat_counts_as_sexual()
    {
        Assert.True(Program.PosePathIsSexual(new[] { "Solo*", "Zorble*", "Lady" }, adultSeat: true));
        Assert.False(Program.PosePathIsSexual(new[] { "Solo*", "Zorble*", "Lady" }, adultSeat: false));   // an ordinary seat is not affected
        var menu = new List<string> { "Zorble*", "Solo Sits*" };
        for (int i = 0; i < 100; i++) Assert.Equal("Solo Sits*", Program.PickPoseButton(menu, null, new Random(i), preferPgSolo: true, inSubmenu: true, adultSeat: true).pick);
        Assert.True(Program.PoseSexualRefused(new[] { "Solo*", "Zorble*", "Lady" }, false, adultSeat: true));
    }

    [Theory]
    [InlineData("Solo*")] [InlineData("Solo Sits*")] [InlineData("Pictures*")] [InlineData("Stand Preen*")] [InlineData("SOLO F*")] [InlineData("Solo Couch*")]
    [InlineData("Singles*")] [InlineData("SINGLE*")] [InlineData("ZERO G*")] [InlineData("SLEEP*")]
    public void Known_plain_submenus_are_trusted(string name) => Assert.True(Program.PoseSubmenuTrustedPg(name), name);

    [Theory]
    [InlineData("Masturbate*")] [InlineData("Features*")] [InlineData("Un/Dress*")] [InlineData("Pamper Me*")] [InlineData("Playing*")] [InlineData("Zorble*")]
    public void Unknown_or_adult_submenus_are_not_trusted(string name) => Assert.False(Program.PoseSubmenuTrustedPg(name), name);

    [Fact]
    public void Re_dress_target_and_timing()
    {
        Assert.Equal("TETRA Chill T-Shirt shorts", Program.RedressTarget("TETRA Chill T-Shirt shorts", false, "Daily"));
        Assert.Equal("Bikini", Program.RedressTarget("Naked", true, "Daily"));
        Assert.Equal("Daily", Program.RedressTarget(null, false, "Daily"));
        Assert.Equal("Daily", Program.RedressTarget("naked", false, "Daily"));
        Assert.Null(Program.RedressTarget(null, false, null));
        Assert.False(Program.NakedRedressDue(null, false, false, false, 99, false).due);                // nothing to put back
        Assert.False(Program.NakedRedressDue("X", true, false, false, 99, false).due);                  // the Naked flow is running (stand / Naked / sit)
        Assert.False(Program.NakedRedressDue("X", false, true, true, 99, false).due);                   // adult pose still running
        Assert.False(Program.NakedRedressDue("X", false, true, false, 5, false).due);                   // pose choice settling
        Assert.False(Program.NakedRedressDue("X", false, false, false, 99, true).due);                  // another outfit change
        Assert.True(Program.NakedRedressDue("X", false, false, true, 0, false).due);                    // stood up
        var sw = Program.NakedRedressDue("X", false, true, false, 30, false);
        Assert.True(sw.due); Assert.Contains("no longer sexual", sw.why);                                // switched to a PG pose (stranger on the parcel / David left)
    }

    [Fact]
    public void A_seat_with_an_adult_menu_is_adult_whatever_its_name()
    {
        var key = "seat-" + Guid.NewGuid();
        Assert.False(Program.PoseSeatIsAdult(key, "Rocking chair"));
        Assert.False(Program.PoseNoteSeatMenu(key, new[] { "Solo Sits*", "Pictures*", "[BACK]" }));     // plain menu: unaffected
        Assert.False(Program.PoseSeatIsAdult(key, "Rocking chair"));
        Assert.True(Program.PoseNoteSeatMenu(key, new[] { "Stand Preen*", "Masturbate*", "Pictures*", "[BACK]" }));
        Assert.True(Program.PoseSeatIsAdult(key, "Rocking chair"));
        Assert.True(Program.PosePathIsSexual(new[] { "Solo*", "Zorble*", "Lady" }, Program.PoseSeatIsAdult(key, "Rocking chair")));
        var plain = "seat-" + Guid.NewGuid();   // a plain PG seat (net chair, rocking chair): unclear names stay as they are
        Assert.False(Program.PosePathIsSexual(new[] { "Zorble*", "Lady" }, Program.PoseSeatIsAdult(plain, "Net chair")));
    }

    [Fact]
    public void The_rules_are_data_a_catalog_file_can_replace()
    {
        var def = PoseRuleSet.FromJson(null);
        Assert.True(def.IsSexualName("Jiller")); Assert.True(def.IsAdultMenuName("Adult M+F*")); Assert.True(def.SeatNameAdult("Xplicit Makeup Table (c,m)"));
        Assert.False(def.IsSexualName("Lady")); Assert.True(def.SubmenuTrusted("Solo Sits"));
        var cat = PoseRuleSet.FromJson("{\"adult_names\":[\"Lady\"],\"pg_names\":[\"Wet Hair\",\"Jilling\"],\"extra_plain_menu_words\":[\"zorble\"],\"adult_seat\":\"playroom\"}", "test");
        Assert.True(cat.IsSexualName("Lady")); Assert.True(cat.IsSexualName("Lady*"));                 // catalog says adult
        Assert.False(cat.IsSexualName("Wet Hair")); Assert.False(cat.IsSexualName("Jilling"));          // catalog says plain: beats the word list
        Assert.True(cat.SubmenuTrusted("Zorble")); Assert.False(def.SubmenuTrusted("Zorble"));
        Assert.True(cat.SeatNameAdult("The Playroom")); Assert.False(cat.SeatNameAdult("Xplicit Makeup Table"));
        Assert.Equal("test", cat.Source);
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => PoseRuleSet.FromJson("{nope"));
    }
}
