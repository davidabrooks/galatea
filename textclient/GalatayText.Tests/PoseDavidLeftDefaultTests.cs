using System;
using System.Collections.Generic;
using System.Linq;
using GalatayText;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-16 13:28:31 on BENTO PHYSICS MIRAGE BED 1.0: couples pose 'CUDDLES* > in my eyes', David stood up and stayed close
/// ("just adjusting -> solo"). The bed's menu had no clearly solo option, so she kept the couples pose running alone.
/// David's rule: switch to the seat's DEFAULT pose ('F waits' here). Real menu strings from the log.</summary>
public class PoseDavidLeftDefaultTests
{
    static readonly List<string> MirageTop = new() { "[SWAP]", "[<<]", "[>>]", "Fist*", "MF*", "[ADJUST]", "CUDDLES*", "ZERO G*", "WARM UP*", "F waits", "SOLO F*", "SLEEP*" };
    // the CUDDLES* submenu she was standing in (log: skipped [ADJUST], [<<], [>>], hug you, [BACK]; current 'in my eyes')
    static readonly List<string> MirageCuddles = new() { "[ADJUST]", "[<<]", "[>>]", "hug you", "in my eyes", "[BACK]" };

    [Fact]
    public void Real_log_case_the_submenu_has_no_solo_pick_and_the_pose_is_couples()
    {
        var (pick, _, why) = Program.PickPoseButton(MirageCuddles, "in my eyes", new Random(1), preferPgSolo: true, allowSexual: false);
        Assert.Null(pick);
        Assert.Contains("no clearly solo option", why);
        Assert.True(Program.PoseLeafIsCouples("in my eyes", new[] { "CUDDLES*", "in my eyes" }));
    }

    [Fact]
    public void Recorded_sit_alone_pose_is_restored_from_the_root_menu_by_what_the_seat_gave_not_by_name()
    {
        // the seat's own first pose, whatever it is called
        var (pick, how) = Program.PickDefaultPoseButton(MirageTop, "F waits");
        Assert.Equal("F waits", pick);
        Assert.Contains("first sat down alone", how);
        var other = new List<string> { "[BACK]", "CUDDLES*", "Meh", "lay 3", "SOLO F*", "Fist*" };
        Assert.Equal("lay 3", Program.PickDefaultPoseButton(other, "lay 3").pick);       // not a 'wait'-style name at all
        Assert.Equal("Meh", Program.PickDefaultPoseButton(other, "Meh").pick);
    }

    [Fact]
    public void Recorded_pose_beats_menu_order_and_a_wait_like_name_gets_no_special_treatment()
    {
        var menu = new List<string> { "F waits", "Meh", "lay 3" };
        Assert.Equal("lay 3", Program.PickDefaultPoseButton(menu, "lay 3").pick);
        Assert.Equal("Meh", Program.PickDefaultPoseButton(menu, "Meh").pick);
    }

    [Fact]
    public void Nothing_recorded_or_not_on_the_menu_falls_back_to_the_first_non_sexual_single_pose_and_says_so()
    {
        var (p1, why1) = Program.PickDefaultPoseButton(MirageTop, null);
        Assert.Equal("F waits", p1);                       // first usable leaf of the real Mirage root menu
        Assert.Contains("no sit-alone pose recorded", why1);
        var (p2, why2) = Program.PickDefaultPoseButton(MirageTop, "Gone pose");
        Assert.Equal("F waits", p2);
        Assert.Contains("not on the root menu", why2);
        var menu = new List<string> { "[ADJUST]", "fist 2", "hug you", "M stand", "Smile", "lay 3" };
        Assert.Equal("Smile", Program.PickDefaultPoseButton(menu, null).pick);   // skips sexual, couples, male, control
    }

    [Fact]
    public void No_usable_leaf_on_the_root_menu_gives_null()
        => Assert.Null(Program.PickDefaultPoseButton(new List<string> { "[SWAP]", "Fist*", "CUDDLES*", "SOLO F*", "[BACK]" }, "Smile").pick);

    [Fact]
    public void Sexual_couples_or_male_recorded_poses_are_never_restored()
    {
        var menu = new List<string> { "fist 2", "hug you", "full job", "M waits" };
        foreach (var rec in menu) Assert.Null(Program.PickDefaultPoseButton(menu, rec, allowSexual: false).pick);
    }

    [Fact]
    public void Sit_alone_pose_is_recorded_only_on_the_initial_unchanged_sit_when_alone_and_non_sexual()
    {
        var key = "seat-" + Guid.NewGuid();
        Assert.False(Program.PoseRecordSitDefault(key, "F waits", alone: false, changeOrRecovery: false));   // David / someone on the seat
        Assert.False(Program.PoseRecordSitDefault(key, "F waits", alone: true, changeOrRecovery: true));     // after a change
        Assert.False(Program.PoseRecordSitDefault(key, "in my eyes", alone: true, changeOrRecovery: false, currentIsCouples: true)); // couples pose (known by menu path, not name)
        Assert.False(Program.PoseRecordSitDefault(key, "fist & balls", alone: true, changeOrRecovery: false)); // sexual
        Assert.False(Program.PoseRecordSitDefault(key, null, alone: true, changeOrRecovery: false));
        Assert.True(Program.PoseRecordSitDefault(key, "F waits", alone: true, changeOrRecovery: false));
    }

    [Fact]
    public void Android_sexual_rule_still_holds_for_the_normal_solo_pick()
    {
        // the default fallback does not loosen the regular picker: Fist* / couples entries stay skipped
        for (int i = 0; i < 30; i++)
        {
            var (pick, _, _) = Program.PickPoseButton(MirageTop, "in my eyes", new Random(i), preferPgSolo: true, allowSexual: false);
            Assert.NotNull(pick);
            Assert.False(Program.PosePathIsSexual(new[] { pick }), pick);
            Assert.NotEqual("Fist*", pick); Assert.NotEqual("MF*", pick);
        }
    }

    [Fact]
    public void David_left_close_by_still_means_solo_and_the_fallback_is_the_recorded_pose()
    {
        Assert.Equal("solo", Program.DavidLeftReaction(0.8, 0.8, false, 0.9).action);
        Assert.Equal("F waits", Program.PickDefaultPoseButton(MirageTop, "F waits").pick);
    }

    // the SOLO F* submenu as David described it (selfie, champagne, texting, reading, laptop)
    static readonly List<string> MirageSoloF = new() { "[ADJUST]", "selfie", "champagne", "texting", "reading", "laptop", "[BACK]" };

    [Fact]
    public void Regression_13_28_32_the_open_cuddles_submenu_must_trigger_a_root_search()
    {
        Assert.True(Program.PoseNeedsRootSearch(MirageCuddles, currentIsCouples: true));
        Assert.True(Program.PoseNeedsRootSearch(MirageCuddles, currentIsCouples: false));   // 'hug you' is couples-named by itself
        Assert.False(Program.PoseNeedsRootSearch(MirageTop, currentIsCouples: true));        // already the root
        Assert.False(Program.PoseNeedsRootSearch(MirageSoloF, currentIsCouples: false));     // a solo submenu stays as it is
        Assert.False(Program.PoseNeedsRootSearch(new List<string> { "F", "M", "David Nightingale" }, true));   // seat-select chrome
        Assert.False(Program.PoseNeedsRootSearch(null, true));
    }

    [Fact]
    public void Regression_root_search_finds_the_solo_submenu_and_then_a_non_sexual_solo_pose()
    {
        // 1) root menu: only SOLO F* qualifies (Fist* / MF* / CUDDLES* are risky; ZERO G / WARM UP / SLEEP are not 'clearly solo')
        for (int i = 0; i < 30; i++)
        {
            var (pick, sub, _) = Program.PickPoseButton(MirageTop, "in my eyes", new Random(i), preferPgSolo: true, allowSexual: false);
            Assert.Equal("SOLO F*", pick);
            Assert.True(sub);
        }
        // 2) inside SOLO F*: a real solo pose, never a control
        var seen = new HashSet<string>();
        for (int i = 0; i < 60; i++)
        {
            var (pick, sub, _) = Program.PickPoseButton(MirageSoloF, "in my eyes", new Random(i), preferPgSolo: true, inSubmenu: true, allowSexual: false);
            Assert.NotNull(pick); Assert.False(sub); seen.Add(pick);
        }
        Assert.Subset(new HashSet<string> { "selfie", "champagne", "texting", "reading", "laptop" }, seen);
        Assert.True(seen.Count > 1);
    }
}
