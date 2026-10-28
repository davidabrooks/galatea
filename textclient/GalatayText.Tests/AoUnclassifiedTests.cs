using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GalatayText;
using LibreMetaverse;
using Xunit;
// 2026-10-26 11:28: after `idle_pose stop` from a ground sit the AO kept playing 2941ee1d (kneel / sit back on heels) while the status said
// "standing; AO force sit off; real state: standing (no AO sit playing)": the id was in neither the sit nor the stand list.
public class AoUnclassifiedTests
{
    static string Root() { var d = new DirectoryInfo(System.AppContext.BaseDirectory); while (d != null && !Directory.Exists(Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent; return d!.FullName; }
    static string Src(string f) => File.ReadAllText(Path.Combine(Root(), "textclient/galatay-text", f));
    static readonly UUID Kneel = new("2941ee1d-94c1-a56f-0d90-fe0e2e0918dc"), AoStand = new("be2dba76-6004-28cb-29e4-68f73d54f13b"), Stand2 = new("3c28cc63-f92b-27a2-f9da-8282a6c9357b"), Sit1 = new("7c8e8863-ab9a-efea-4d64-47a0fa85cdc1");

    [Fact] public void AnAoAnimationInNeitherList_IsUnclassified_NeverAStand()
    {
        var u = Program.AoUnclassified(new[] { Animations.STAND, Kneel, AoStand }, _ => true, new[] { AoStand }, new[] { Sit1 });
        Assert.Equal(new[] { Kneel }, u);
        Assert.Empty(Program.AoUnclassified(new[] { Kneel }, _ => false, new[] { AoStand }, new[] { Sit1 }));      // not the AO's (a dance, a collar pose): not our business
        Assert.Empty(Program.AoUnclassified(new[] { Animations.STAND, Animations.SIT_GROUND }, _ => true, null, null));  // built-ins are never "unclassified"
        Assert.Empty(Program.AoUnclassified(new[] { AoStand, Stand2, Sit1 }, _ => true, new[] { AoStand, Stand2 }, new[] { Sit1 }));
        Assert.Empty(Program.AoUnclassified(null, _ => true, null, null));
    }
    [Fact] public void TheKneelAnimation_IsASeededSit_AndAnUnclassifiedOneIsNotAStandInStatus()
    {
        Assert.Contains(Kneel, Program.AoSitSeed);
        Assert.False(Program.AoSitPlaying(new[] { Kneel }, false, new UUID[0]));                  // unseeded it was invisible to the sit check: the bug
        Assert.True(Program.AoSitPlaying(new[] { Kneel }, false, Program.AoSitSeed));
        var s = Program.AoRealState(false, new string[0], false, false, new[] { "2941ee1d" });
        Assert.Contains("UNKNOWN: AO animation unclassified (2941ee1d), may be a sit", s); Assert.DoesNotContain("standing (", s);
        Assert.Contains("standing (no AO sit playing", Program.AoRealState(false, new string[0], false, false, new string[0]));
        Assert.Contains("standing (no AO sit playing", Program.AoRealState(false, new string[0], false, false));
        Assert.DoesNotContain("UNKNOWN", Program.AoRealState(true, new[] { "7c8e8863" }, false, false, new[] { "2941ee1d" }));   // a known sit wins
    }
    [Fact] public void SeededSits_CanNeverBeLearnedAsStands()
    {
        var ao = Src("AoSit.cs");
        Assert.Contains("|| AoSitSeed.Contains(id)) return;", ao);                               // AoNoteStarted
        Assert.Contains("aoStandIds.Remove(u); aoSitIds.Add(u);", ao);                          // the load re-asserts the seed over the file
    }
    [Fact] public void UnknownToggle_OnceOnly_AndOnlyWhenSheWasSitting()
    {
        Assert.True(Program.AoUnknownToggleOk(true, true, 0));
        Assert.False(Program.AoUnknownToggleOk(true, true, 1));                                  // never repeatedly
        Assert.False(Program.AoUnknownToggleOk(true, false, 0));                                 // the toggle turns force sit ON when it is off: not without a reason
        Assert.False(Program.AoUnknownToggleOk(false, true, 0));
        var now = new DateTime(2026, 10, 26, 11, 30, 0);
        Assert.True(Program.AoBelievedSitting("groundsit", DateTime.MinValue, now));
        Assert.True(Program.AoBelievedSitting(null, now.AddMinutes(-9), now));
        Assert.False(Program.AoBelievedSitting(null, now.AddMinutes(-11), now));
        Assert.False(Program.AoBelievedSitting("collar", DateTime.MinValue, now));
    }
    [Fact] public void AfterTheToggle_WhatStoppedWhileAClassifiedStandPlays_IsLearnedAsASit()
    {
        Assert.Equal(new[] { Kneel }, Program.AoLearnAfterToggle(new[] { Kneel }, new[] { Animations.STAND, Stand2 }, new[] { Stand2 }));   // 3c28cc63 started, 2941ee1d stopped
        Assert.Empty(Program.AoLearnAfterToggle(new[] { Kneel }, new[] { Animations.STAND, Kneel, Stand2 }, new[] { Stand2 }));                // still playing: nothing learned
        Assert.Empty(Program.AoLearnAfterToggle(new[] { Kneel }, new[] { Animations.STAND }, new[] { Stand2 }));                             // no classified stand took over: unknown stays unknown
    }
    [Fact] public void StandVerifier_StandsTwiceThenTogglesOnce_AndNeverClaimsStandingWithAnUnclassifiedAnim()
    {
        var ao = Src("AoSit.cs"); var f = ao[ao.IndexOf("static async Task<string> AoGroundSitEnsureOff(")..];
        Assert.Contains("var unk0 = AoUnclassifiedNow();", f);
        Assert.Contains("AoUnknownToggleOk(true, believed, toggles)", f);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(f, @"AoGroundSitToggle\(\$""off: \{why\} \(unclassified").Count);   // the single unknown-toggle call site
        Assert.Contains("NOT VERIFIED standing: AO animation unclassified", f);
        Assert.Contains("only classified AO stands play", f);
        Assert.Contains("AoLearnAfterToggle(", f);
    }
    [Fact] public void StatusAndAutoHover_NeverCallAnUnclassifiedStateStanding()
    {
        var ip = Src("IdlePose.cs");
        Assert.Contains("IdleStandingWord(real.State)", ip); Assert.Contains("UNKNOWN (unclassified AO animation)", ip);
        Assert.Contains("\"unknown\" : \"stand\"", Src("AutoHover.cs"));
    }
    // ---- the hover check does not run on it ----
    [Fact] public void HoverCheck_NeverRunsOnAnUnclassifiedState()
    {
        Assert.Equal("unknown", Hover.HoverPostureKind(false, false, true));
        Assert.Equal("sit", Hover.HoverPostureKind(true, false, true));                         // a known sit wins
        Assert.Equal("seat", Hover.HoverPostureKind(false, true, true));
        Assert.Equal("stand", Hover.HoverPostureKind(false, false, false));
        Assert.False(Hover.HoverCheckWantedOnPosture("sit", "unknown"));                        // no request when it turns unknown
        Assert.True(Hover.HoverCheckWantedOnPosture("unknown", "stand"));                       // a verified stand afterwards asks for one
        var r = Hover.HoverCheckReady("unknown", 0f, false, false, false, 10, 100, true);
        Assert.False(r.go); Assert.StartsWith("skip:", r.why.Length > 0 && r.why.StartsWith("skip") ? r.why : "skip:" + r.why);
        var ph = Src("PoseHover.cs");
        Assert.Contains("\"unknown\", \"unknown\"", ph);                                          // PoseNow
        var still = ph[ph.IndexOf("bool PoseStill(")..]; still = still[..still.IndexOf("internal (string kind, string pose) PoseNow")];
        Assert.Contains("kind == \"stand\" &&", still); Assert.Contains("AoUnclassifiedNow().Count == 0", still);
    }
}
