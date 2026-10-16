using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GalatayText;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-16 13:43 (BENTO PHYSICS MIRAGE BED 1.0): 'go to root' pressed "[<<]" (previous PAGE) instead of "[BACK]", landed on
/// CUDDLES* page 2 and took it for the root, so SOLO F* was never searched and the fallback picked 'head massage' (couples).
/// Real menu strings from the log. Works for any depth (already at root, 1, 2, 3, 4 levels) and for roots without a solo folder.</summary>
public class PoseBackToRootTests
{
    static readonly List<string> CuddlesP1 = new() { "[ADJUST]", "[<<]", "[>>]", "backpack", "hold hand", "hug you", "hold her", "in my eyes", "time", "[BACK]", "comfy", "chilling" };
    static readonly List<string> CuddlesP2 = new() { "[ADJUST]", "[<<]", "[>>]", "mmm", "you mine", "kiss neck", "yummy kiss", "killing time", "love you", "[BACK]", "sweet surprise", "kiss me :)" };
    static readonly List<string> Root = new() { "[SWAP]", "[<<]", "[>>]", "Fist*", "MF*", "[ADJUST]", "CUDDLES*", "ZERO G*", "WARM UP*", "F waits", "SOLO F*", "SLEEP*" };
    static readonly List<string> SoloF = new() { "[ADJUST]", "selfie", "champagne", "texting", "reading", "laptop", "[BACK]" };

    /// <summary>A menu tree: id -> labels; pressing a label moves per the map. Records every pressed label.</summary>
    class Sim
    {
        public Dictionary<string, List<string>> Menus = new();
        public Dictionary<(string, string), string> Go = new();
        public string At; public List<string> Pressed = new();
        public Task<List<string>> Press(int idx, string label)
        {
            Pressed.Add(label);
            Assert.Equal(Menus[At][idx], label);
            if (Go.TryGetValue((At, label), out var n)) At = n;
            return Task.FromResult(Menus[At].ToList());
        }
    }

    static Sim MirageSim(string start)
    {
        var s = new Sim { At = start };
        s.Menus["root"] = Root; s.Menus["c1"] = CuddlesP1; s.Menus["c2"] = CuddlesP2; s.Menus["solo"] = SoloF;
        s.Go[("root", "CUDDLES*")] = "c1"; s.Go[("root", "SOLO F*")] = "solo";
        s.Go[("c1", "[BACK]")] = "root"; s.Go[("c2", "[BACK]")] = "root"; s.Go[("solo", "[BACK]")] = "root";
        s.Go[("c1", "[>>]")] = "c2"; s.Go[("c1", "[<<]")] = "c2"; s.Go[("c2", "[>>]")] = "c1"; s.Go[("c2", "[<<]")] = "c1";   // paging wraps
        return s;
    }

    [Fact]
    public void Real_strings_back_button_vs_page_buttons()
    {
        Assert.Equal(9, Program.PoseBackIndex(CuddlesP1));    // [BACK], not [<<] at index 1
        Assert.Equal(9, Program.PoseBackIndex(CuddlesP2));
        Assert.Equal(-1, Program.PoseBackIndex(Root));        // [<<] [>>] are paging
        Assert.Equal(6, Program.PoseBackIndex(SoloF));
        Assert.False(Program.PoseIsBackButton("[<<]"));
        Assert.Equal(1, Program.PoseBackIndex(new[] { "x", "<<" }));   // a lone "<<" (no ">>") still goes back
    }

    [Fact]
    public void Real_strings_root_detection_and_not_root_detection()
    {
        Assert.True(Program.PoseMenuIsTopLevel(Root));
        Assert.False(Program.PoseMenuIsTopLevel(CuddlesP1));
        Assert.False(Program.PoseMenuIsTopLevel(CuddlesP2));
        Assert.False(Program.PoseMenuIsTopLevel(SoloF));
        Assert.False(Program.PoseMenuIsTopLevel(null));
    }

    [Fact]
    public async Task Regression_13_43_cuddles_page1_goes_straight_to_the_real_root_pressing_only_BACK()
    {
        var s = MirageSim("c1");
        var r = await Program.PoseBackToRootCore(s.Menus["c1"].ToList(), s.Press);
        Assert.True(r.atRoot); Assert.Equal(Root, r.labels); Assert.Equal(1, r.presses);
        Assert.All(s.Pressed, p => Assert.Equal("[BACK]", p));
    }

    [Fact]
    public async Task Cuddles_page2_also_reaches_the_root()
    {
        var s = MirageSim("c2");
        var r = await Program.PoseBackToRootCore(s.Menus["c2"].ToList(), s.Press);
        Assert.True(r.atRoot); Assert.Equal(Root, r.labels);
        Assert.DoesNotContain("[<<]", s.Pressed); Assert.DoesNotContain("[>>]", s.Pressed);
    }

    [Fact]
    public async Task Already_at_root_presses_nothing()
    {
        var s = MirageSim("root");
        var r = await Program.PoseBackToRootCore(Root.ToList(), s.Press);
        Assert.True(r.atRoot); Assert.Equal(0, r.presses); Assert.Empty(s.Pressed);
    }

    // a seat with `levels` levels above the root: root(no BACK) <- L1 <- L2 ... <- Ln; each level has a different set of pose buttons
    static Sim DepthSim(int levels, List<string> rootLabels)
    {
        var s = new Sim { At = "L" + levels };
        s.Menus["L0"] = rootLabels;
        for (int i = 1; i <= levels; i++)
        {
            s.Menus["L" + i] = new List<string> { "[ADJUST]", "Pose " + i + "a", "Pose " + i + "b", "Sub" + i + "*", "[BACK]" };
            s.Go[("L" + i, "[BACK]")] = "L" + (i - 1);
        }
        return s;
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(7)]
    public async Task Any_depth_reaches_the_top_level_without_a_hardcoded_depth(int levels)
    {
        var s = DepthSim(levels, new List<string> { "Sit*", "Couples*", "Solo*", "Relax 1", "[SWAP]" });
        var r = await Program.PoseBackToRootCore(s.Menus["L" + levels].ToList(), s.Press);
        Assert.True(r.atRoot, r.note); Assert.Equal(levels, r.presses);
        Assert.Equal(s.Menus["L0"], r.labels);
        Assert.All(s.Pressed, p => Assert.Equal("[BACK]", p));
    }

    [Fact]
    public async Task Root_without_any_solo_folder_is_still_recognised_and_nothing_wrong_is_picked()
    {
        var rootLabels = new List<string> { "[SWAP]", "Dance 1", "Lounge*", "Stand tall", "Cuddle*" };
        var s = DepthSim(3, rootLabels);
        var r = await Program.PoseBackToRootCore(s.Menus["L3"].ToList(), s.Press);
        Assert.True(r.atRoot); Assert.Equal(rootLabels, r.labels);
        // the solo picker finds nothing clearly solo among folders it cannot judge, but the default fallback is a root-level single pose, never a folder / couples
        var (pick, why) = Program.PickDefaultPoseButton(r.labels, null);
        Assert.Equal("Dance 1", pick);
        Assert.Null(Program.PickDefaultPoseButton(new List<string> { "[SWAP]", "Lounge*", "Cuddle*" }, null).pick);
    }

    [Fact]
    public async Task Gives_up_with_a_clear_note_at_the_cap_and_when_stuck_or_no_dialog()
    {
        var s = DepthSim(12, new List<string> { "Sit*", "Couples*", "Solo*" });
        var r = await Program.PoseBackToRootCore(s.Menus["L12"].ToList(), s.Press, cap: 8);
        Assert.False(r.atRoot); Assert.Equal(8, r.presses); Assert.Contains("gave up after 8", r.note);
        var stuck = await Program.PoseBackToRootCore(CuddlesP1.ToList(), (i, l) => Task.FromResult(CuddlesP1.ToList()));
        Assert.False(stuck.atRoot); Assert.Contains("stuck", stuck.note);
        var none = await Program.PoseBackToRootCore(CuddlesP1.ToList(), (i, l) => Task.FromResult<List<string>>(null));
        Assert.False(none.atRoot); Assert.Contains("no dialog", none.note);
    }

    [Fact]
    public void Default_pose_is_never_picked_from_a_middle_page_head_massage_regression()
    {
        // 13:43: page 2 of CUDDLES* (and page 1) used to be fed to the default picker
        var pageWithMassage = new List<string> { "[ADJUST]", "[<<]", "[>>]", "head massage", "chilling", "comfy", "[BACK]" };
        Assert.Null(Program.PickDefaultPoseButton(CuddlesP1, null).pick);
        Assert.Null(Program.PickDefaultPoseButton(CuddlesP2, "kiss me :)").pick);
        Assert.Null(Program.PickDefaultPoseButton(pageWithMassage, "head massage").pick);
        Assert.Null(Program.PickDefaultPoseButton(SoloF, "selfie").pick);
        Assert.Equal("F waits", Program.PickDefaultPoseButton(Root, null).pick);        // first usable root-level single pose
        Assert.Equal("F waits", Program.PickDefaultPoseButton(Root, "F waits").pick);
    }

    [Fact]
    public void Root_pick_chooses_SOLO_F_never_male_sexual_or_couples_and_inside_it_a_solo_pose()
    {
        var rootWithMale = Root.Concat(new[] { "SOLO M*", "M waits" }).ToList();
        for (int i = 0; i < 40; i++)
        {
            var (pick, sub, _) = Program.PickPoseButton(rootWithMale, "chilling", new Random(i), preferPgSolo: true, allowSexual: false);
            Assert.Equal("SOLO F*", pick); Assert.True(sub);
            var (leaf, sub2, _) = Program.PickPoseButton(SoloF, "chilling", new Random(i), preferPgSolo: true, inSubmenu: true, allowSexual: false);
            Assert.NotNull(leaf); Assert.False(sub2);
            Assert.Contains(leaf, new[] { "selfie", "champagne", "texting", "reading", "laptop" });
            Assert.NotEqual("head massage", leaf);
        }
    }

    [Fact]
    public void Sit_alone_default_is_recorded_from_the_dialog_header_with_no_name_matching()
    {
        // header '[Female seat] [F waits]' -> PoseCurrent takes the active pose
        var key = "seat-" + Guid.NewGuid();
        Assert.True(Program.PoseRecordSitDefault(key, "F waits", alone: true, changeOrRecovery: false));
        Assert.Equal("F waits", Program.PickDefaultPoseButton(Root, "F waits").pick);
        var t = DateTime.Now;
        Assert.True(Program.PoseSitDefaultWindowOk(t, t.AddSeconds(5), "", ""));
        Assert.False(Program.PoseSitDefaultWindowOk(t, t.AddSeconds(30), "", ""));          // not the first menu after sitting
        Assert.False(Program.PoseSitDefaultWindowOk(t, t.AddSeconds(5), "", "SOLO F*>selfie")); // a pose was pressed since the sit
        Assert.False(Program.PoseSitDefaultWindowOk(DateTime.MinValue, t, "", ""));
    }
}
