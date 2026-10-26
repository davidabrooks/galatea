using System;
using System.Collections.Generic;
using System.Linq;
using GalatayText;
using LibreMetaverse;
using Xunit;
// 2026-10-26: the AO is the authority on sit vs stand; stand verifies against what really plays; hover pin; shared stand default
public class AoSitTests
{
    static readonly UUID VamoG13 = new("7c8e8863-ab9a-efea-4d64-47a0fa85cdc1");    // the AO ground sit that kept playing at 09:03 while the sim said STAND
    static readonly UUID AoStand = new("be2dba76-6004-28cb-29e4-68f73d54f13b");    // an AO stand animation (restarts all the time while standing)
    [Fact] public void AoSitAnimationPlayingWhileSimSaysStand_IsGroundSitting()
    {
        var playing = new[] { Animations.STAND, VamoG13 };   // 09:03:22: sim STAND + the AO's own sit
        Assert.False(Program.AoSitPlaying(playing, false, new UUID[0]));                       // unknown to us: what failed before
        Assert.True(Program.AoSitPlaying(playing, false, new[] { VamoG13 }));                    // learned: the AO plays a sit
        Assert.False(Program.AoSitPlaying(playing, true, new[] { VamoG13 }));                    // on an object: not the AO's ground sit
        Assert.True(Program.AoSitPlaying(new[] { Animations.SIT_GROUND_staticRAINED }, false, null));
        Assert.False(Program.AoSitPlaying(new[] { Animations.STAND, AoStand }, false, new[] { VamoG13 }));
        Assert.False(Program.AoSitPlaying(null, false, null));
    }
    [Fact] public void SitIdsAreLearnedFromWhatTheAoAdded_NeverStandOrBuiltinOrAlreadyPlaying()
    {
        var added = new[] { VamoG13.ToString(), AoStand.ToString(), Animations.SIT_GROUND_staticRAINED.ToString(), "not-a-uuid" };
        var got = Program.AoSitLearnable(added, new UUID[0], new[] { AoStand });
        Assert.Equal(new[] { VamoG13 }, got);
        Assert.Empty(Program.AoSitLearnable(added, new[] { VamoG13 }, new[] { AoStand }));      // was already playing before the pose: not new
        Assert.Equal(2, Program.AoSitLearnable(added, new UUID[0], new UUID[0]).Count);        // an unknown stand animation can slip in (it is unlearned when seen standing)
    }
    [Fact] public void AoStandAnimationIsLearnedOnlyInPlainStanding()
    {
        var t = new DateTime(2026, 10, 26, 9, 3, 0); var later = t.AddMinutes(2);
        Assert.True(Program.AoStandLearnOk(false, false, false, later, t));
        Assert.False(Program.AoStandLearnOk(true, false, false, later, t));                       // posed
        Assert.False(Program.AoStandLearnOk(false, true, false, later, t));                       // built-in ground sit playing
        Assert.False(Program.AoStandLearnOk(false, false, true, later, t));                       // on an object
        Assert.False(Program.AoStandLearnOk(false, false, false, t.AddSeconds(5), t.AddSeconds(60)));   // inside a pose transition window
    }
    [Fact] public void VerifiedStand_StandsThenTogglesOnlyWhileAnAoSitPlays_Bounded()
    {
        Assert.Equal(Program.AoStandStep.Done, Program.AoStandNext(false, 0, true));              // nothing plays: never touch the toggle
        Assert.Equal(Program.AoStandStep.Stand, Program.AoStandNext(true, 0, true));
        Assert.Equal(Program.AoStandStep.Stand, Program.AoStandNext(true, 1, true));
        Assert.Equal(Program.AoStandStep.Toggle, Program.AoStandNext(true, 2, true));             // stood twice and the AO still sits
        Assert.Equal(Program.AoStandStep.Stand, Program.AoStandNext(true, 3, true));              // never two toggles in a row
        Assert.Equal(Program.AoStandStep.Toggle, Program.AoStandNext(true, 4, true));
        Assert.Equal(Program.AoStandStep.Stand, Program.AoStandNext(true, 5, true));
        Assert.Equal(Program.AoStandStep.GiveUp, Program.AoStandNext(true, 6, true));             // bounded
        Assert.Equal(Program.AoStandStep.Stand, Program.AoStandNext(true, 2, false));             // not believed to be ground sitting: stand only, no toggle
        var toggles = Enumerable.Range(0, 7).Count(m => Program.AoStandNext(true, m, true) == Program.AoStandStep.Toggle);
        Assert.Equal(2, toggles);
    }
    [Fact] public void IdleposeStatusReportsTheRealStateAndTheMismatch()
    {
        var s = Program.AoRealState(true, new[] { "7c8e8863" }, false, false);
        Assert.Contains("AO sit playing (7c8e8863)", s); Assert.Contains("sim says stand", s); Assert.Contains("MISMATCH", s);
        var ok = Program.AoRealState(false, new string[0], false, false);
        Assert.Contains("standing (no AO sit playing)", ok); Assert.DoesNotContain("MISMATCH", ok);
        Assert.DoesNotContain("MISMATCH", Program.AoRealState(true, new string[0], false, true));
        Assert.Contains("sitting on an object", Program.AoRealState(false, new string[0], true, false));
    }
    [Fact] public void StandPathUsesTheVerifiedAoCheck_AndTheAoIsTheAuthorityEverywhere()
    {
        var dir = System.IO.Path.Combine(Root(), "textclient/galatay-text");
        var ao = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "AoSit.cs"));
        Assert.Contains("AoStandNext(", ao); Assert.Contains("verified stand", ao);
        var ip = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "IdlePose.cs"));
        Assert.Contains("string b = await AoGroundSitEnsureOff(why);", ip);
        Assert.Contains("AoSitPlaying(playing, sittingOnObject, AoSitIdsNow())", ip);            // AoForceSitOn judges by the AO's own animations
        Assert.Contains("IdleRealStateText()", ip);                                              // `idlepose status` reports the real state
        Assert.Contains("AoSitReallyOn()", System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "PoseHover.cs")));   // the render/hover pose is the AO's
        Assert.Contains("AoSitReallyOn()", System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "AutoHover.cs")));
    }
    // ---- hover pin and stand default ----
    [Fact] public void HoverSetChangesTheRealPin_NotJustTheFile()
    {
        Assert.Equal(0.02, Program.PinnedHoverFrom(0.02, null, "0"), 3);                          // 09:01: the collar's override kept winning over hover.txt
        var after = Program.HoverSetOverride(0.02, 0.0);
        Assert.Equal(0.0, after);
        Assert.Equal(0.0, Program.PinnedHoverFrom(after, null, "0"), 3);                          // `hover get` and "back to normal hover" now say 0
        Assert.Null(Program.HoverSetOverride(null, 0.0));                                       // no override: the file is the pin
        Assert.Equal(0.0, Program.PinnedHoverFrom(null, null, "0\n"), 3);
        Assert.Equal(0.05, Program.PinnedHoverFrom(null, "0.05", "0"), 3);                        // GT_HOVER still beats the file
        Assert.Equal(2.0, Program.PinnedHoverFrom(null, "9", null), 3);                           // clamped
        Assert.Equal(0.0, Program.PinnedHoverFrom(null, null, null), 3);
        var hg = System.IO.File.ReadAllText(System.IO.Path.Combine(Root(), "textclient/galatay-text/HeightGuard.cs"));
        Assert.Contains("rlvHoverOverride = HoverSetOverride(rlvHoverOverride, v)", hg);
    }
    [Fact] public void NewStandIdsInheritOneSharedDefault_ZeroUntilMeasured()
    {
        var nums = new Dictionary<string, double> { ["android|stand:fba08491"] = 0.015, ["ref|android|stand"] = 0.6324, ["bias|android|stand"] = 0.2315 };
        Assert.Equal(0.0, Program.PoseStandDefault(nums, true));                                  // never the 0.02 pin
        Assert.Equal("android|stand", Program.PoseStandDefaultKey(true)); Assert.Equal("human|stand", Program.PoseStandDefaultKey(false));
        nums["android|stand"] = -0.013;
        Assert.Equal(-0.013, Program.PoseStandDefault(nums, true));
        Assert.Equal(0.0, Program.PoseStandDefault(nums, false));
        Assert.Equal(0.015, Program.PoseStartHover("stand", 0.015, -0.013, 0.02));                // a measured id keeps its own value
        Assert.Equal(-0.013, Program.PoseStartHover("stand", null, -0.013, 0.02));                // a new id inherits the default
        Assert.Equal(0.02, Program.PoseStartHover("sit", null, -0.013, 0.02));                    // unknown sit: the pin, as before
    }
    static string Root() { var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory); while (d != null && !System.IO.Directory.Exists(System.IO.Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent; return d!.FullName; }
}
