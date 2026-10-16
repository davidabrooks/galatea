using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GalatayText;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-16 14:00 on BENTO PHYSICS MIRAGE BED 1.0: I stood and sat alone again; AVsitter sent '[Female seat] [F waits]' 1 s after the
/// sit response, but nothing was recorded and nothing was logged (the hook returned silently while Self.SittingOn was still 0).
/// The decision is now one pure function with a reason for every decline; real strings from the log.</summary>
public class PoseSitDefaultRecordTests
{
    // exact dialog text + root menu from textclient.log 14:00:12
    const string Msg = "AVsitter™2.1\n\nPress Ctrl S to resync Animations\n[Female seat] [F waits]";
    static readonly List<string> Root = new() { "[SWAP]", "[<<]", "[>>]", "Fist*", "MF*", "[ADJUST]", "CUDDLES*", "ZERO G*", "WARM UP*", "F waits", "SOLO F*", "SLEEP*" };
    static readonly List<string> SoloSub = new() { "selfie", "champagne", "[ADJUST]", "texting", "reading", "laptop", "[BACK]" };

    static (bool record, bool conclusive, string cur, string reason) Check(
        bool anchored = true, double since = 1.0, string pathAt = "", string pathNow = "", bool sitting = true, bool fromSeat = true,
        string msg = Msg, IReadOnlyList<string> labels = null, bool alone = true, bool changed = false) =>
        Program.PoseSitDefaultCheck(anchored, since, pathAt == pathNow, pathAt, pathNow, sitting, fromSeat, msg, labels ?? Root, alone, changed);

    [Fact]
    public void Real_log_case_records_F_waits_one_second_after_the_sit()
    {
        var r = Check();
        Assert.True(r.record); Assert.Equal("F waits", r.cur);
    }

    [Fact]
    public void Menu_before_the_sit_state_is_known_is_not_dropped_silently_it_is_kept_for_a_retry_and_says_why()
    {
        var r = Check(sitting: false);       // the race: dialog first, avatar object update (SittingOn) after
        Assert.False(r.record); Assert.False(r.conclusive);
        Assert.Contains("not sitting yet", r.reason);
        Assert.True(Check(sitting: true).record);   // the retry once SittingOn is set
    }

    [Fact]
    public void Dialog_slightly_before_the_sit_changed_anchor_still_counts_but_a_late_one_does_not()
    {
        Assert.True(Check(since: -2).record);
        var early = Check(since: -9); Assert.False(early.record); Assert.Contains("window", early.reason);
        var late = Check(since: 13); Assert.False(late.record); Assert.True(late.conclusive); Assert.Contains("window", late.reason);
        Assert.True(Check(since: 11.9).record);
    }

    [Fact]
    public void Pressed_pose_or_a_changed_header_blocks_recording_with_a_reason()
    {
        var a = Check(pathAt: "", pathNow: "SOLO F*>selfie"); Assert.False(a.record); Assert.Contains("pressed", a.reason);
        var b = Check(changed: true); Assert.False(b.record); Assert.Contains("pressed", b.reason);
    }

    [Fact]
    public void Not_alone_never_records_and_says_alone()
    {
        var r = Check(alone: false); Assert.False(r.record); Assert.True(r.conclusive); Assert.Contains("alone", r.reason);
    }

    [Fact]
    public void Not_anchored_other_object_and_seat_select_are_not_conclusive_and_never_record()
    {
        Assert.False(Check(anchored: false).record);
        var o = Check(fromSeat: false); Assert.False(o.record); Assert.False(o.conclusive);
        var ss = Check(labels: new List<string> { "F", "M", "[ADJUST]" }, msg: "[Pick seat]"); Assert.False(ss.record);
    }

    [Theory]
    [InlineData("AVsitter™2.1\n\n[Female seat] [CUDDLES*]")]                  // a submenu name is not a pose
    [InlineData("AVsitter™2.1\n\n[Male seat] [M stand]")]                      // male pose
    [InlineData("AVsitter™2.1\n\n[Female seat] [Together]")]                   // couples pose
    [InlineData("AVsitter™2.1\n\n[Female seat] [Sexy sit]")]                   // sexual name
    [InlineData("AVsitter™2.1\n\n[Female seat] [[ADJUST]]")]                   // control
    public void Never_records_couples_male_sexual_or_submenu_poses(string msg)
    {
        var r = Check(msg: msg); Assert.False(r.record);
    }

    [Fact]
    public void A_submenu_or_a_couples_menu_is_not_the_first_menu()
    {
        var sub = Check(labels: SoloSub, msg: "AVsitter™2.1\n[Female seat] [selfie]");
        Assert.False(sub.record); Assert.Contains("BACK", sub.reason);
        var cu = Check(labels: new List<string> { "[ADJUST]", "hug you", "in my eyes", "Together" }, msg: "[Female seat] [in my eyes]");
        Assert.False(cu.record);
    }

    [Fact]
    public void Header_without_a_pose_name_declines_with_a_parse_reason_and_no_other_seat_names_are_matched()
    {
        var r = Check(msg: "no brackets here"); Assert.False(r.record); Assert.Contains("header parse", r.reason);
        // a seat whose default is not called anything 'wait'-like is recorded the same way
        var odd = Check(msg: "AVsitter™2.1\n[Female seat] [lay 3]", labels: new List<string> { "[SWAP]", "lay 3", "Meh", "SOLO*" });
        Assert.True(odd.record); Assert.Equal("lay 3", odd.cur);
    }

    [Fact]
    public void Persisted_defaults_round_trip_and_ignore_unusable_entries()
    {
        var poses = new Dictionary<string, string> { ["cdedb159-2e61-e54d-6045-283a58d25015"] = "F waits", ["x"] = "lay 3" };
        var names = new Dictionary<string, string> { ["cdedb159-2e61-e54d-6045-283a58d25015"] = "BENTO PHYSICS MIRAGE BED 1.0" };
        var json = Program.PoseSitDefaultsToJson(poses, names);
        var back = Program.PoseSitDefaultsFromJson(json);
        Assert.Equal("F waits", back["cdedb159-2e61-e54d-6045-283a58d25015"].pose);
        Assert.Equal("BENTO PHYSICS MIRAGE BED 1.0", back["cdedb159-2e61-e54d-6045-283a58d25015"].seat);
        Assert.Equal("lay 3", back["x"].pose);
        var bad = Program.PoseSitDefaultsFromJson("{\"a\":{\"pose\":\"CUDDLES*\"},\"b\":{\"pose\":\"Sexy sit\"},\"c\":\"Meh\",\"d\":{}}");
        Assert.Equal(new[] { "c" }, bad.Keys.ToArray());
        Assert.Empty(Program.PoseSitDefaultsFromJson("not json"));
    }

    [Fact]
    public void Save_and_load_survive_a_restart_through_the_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "seatdef-" + Guid.NewGuid()); var f = Path.Combine(dir, "seat-default-poses.json");
        var old = Environment.GetEnvironmentVariable("GT_SEAT_DEFAULTS");
        Environment.SetEnvironmentVariable("GT_SEAT_DEFAULTS", f);
        try
        {
            var key = "seat-" + Guid.NewGuid();
            Assert.True(Program.PoseRecordSitDefault(key, "F waits", true, false));
            Program.PoseSitDefaultSaveForTest(key, "Test bed");
            Assert.True(File.Exists(f));
            Assert.Contains("F waits", File.ReadAllText(f)); Assert.Contains("Test bed", File.ReadAllText(f));
            Assert.Equal("F waits", Program.PoseSitDefaultsFromJson(File.ReadAllText(f))[key].pose);
        }
        finally { Environment.SetEnvironmentVariable("GT_SEAT_DEFAULTS", old); try { Directory.Delete(dir, true); } catch { } }
    }
}
