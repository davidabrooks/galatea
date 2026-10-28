using System.Collections.Generic;
using System.IO;
using GalatayText;
using Xunit;
// 2026-10-26 (David): no saved/applied per-pose or per-AO-stand hover; after a sit, a stand or a new region she checks her hover
// visually (side render over the floor) and fixes it then.
public class HoverVisualCheckTests
{
    static string Root() { var d = new DirectoryInfo(System.AppContext.BaseDirectory); while (d != null && !Directory.Exists(Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent; return d!.FullName; }
    static string Src(string f) => File.ReadAllText(Path.Combine(Root(), "textclient/galatay-text", f));

    [Fact] public void SavedPerPoseHoverValues_AreIgnored_OnlyBiasCalibrationIsRead()
    {
        var file = new Dictionary<string, double> { ["android|VAMOSITG24"] = -0.02, ["android|stand"] = -0.013, ["android|stand:fba08491"] = 0.015, ["ref|android|stand"] = 0.63, ["bias|android|sit"] = -0.055 };
        var nums = Hover.PoseBiasNums(file);
        Assert.Single(nums); Assert.Equal(-0.055, nums["bias|android|sit"], 3);
        Assert.Empty(Hover.PoseBiasNums(null));
        Assert.Equal(-0.055, Hover.PoseBiasFor(nums, true, "sit", "VAMOSITG24"), 3);   // the stale per-pose hover never leaks in as a bias
    }
    [Fact] public void NothingIsSavedOrAppliedPerPose_InSource()
    {
        var all = Src("PoseHover.cs") + Src("IdlePose.cs") + Src("AutoHover.cs") + Src("HeightGuard.cs") + Src("AttachWatch.cs");
        foreach (var gone in new[] { "PoseNumSet", "PoseHoverSave", "PoseHoverSaved", "GroundSitHoverFor", "PoseHoverLookup", "PoseHoverBuiltin", "PoseStandDefault", "PoseStartHover", "HoverSetRememberStand", "poseChecked", "PoseHoverSchedule", "absHover" })
            Assert.DoesNotContain(gone, all);
        // the check posts a fix only from the render's measured gap; no value is read back from disk
        var body = Src("PoseHover.cs"); body = body[body.IndexOf("async Task<string> PoseHoverCheck(")..];
        Assert.Contains("PostHover(t,", body); Assert.DoesNotContain("File.Write", body); Assert.DoesNotContain("PoseNum", body.Replace("PoseNums()", ""));
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(body, @"PostHover\(").Count);
    }
    [Fact] public void AoStandRotation_NoLongerSchedulesAnything()
    {
        var aw = Src("AttachWatch.cs");
        Assert.DoesNotContain("PoseHoverSchedule", aw); Assert.DoesNotContain("HoverCheckRequest", aw);   // only the posture tracker may request
        Assert.Contains("hoverSvc.HoverPostureNote(now.Keys, client.Self.SittingOn != 0)", aw);
    }
    [Fact] public void Posture_IsJudgedFromTheAoAnimList_NotTheSim()
    {
        Assert.Equal("sit", Hover.HoverPostureKind(true, false));      // AO sit playing while the sim says standing
        Assert.Equal("stand", Hover.HoverPostureKind(false, false));
        Assert.Equal("seat", Hover.HoverPostureKind(false, true));
        Assert.Equal("seat", Hover.HoverPostureKind(true, true));
        var ph = Src("PoseHover.cs");
        Assert.Contains("AoForceSitOn(playing, seatedOnObject)", ph);    // the AO's anim_list authority
        Assert.Contains("AoSitReallyOn()", ph);
        // the still-in-pose gate for a sit is the AO's, no longer the stored idle flag
        var still = ph[ph.IndexOf("bool PoseStill(")..]; still = still[..still.IndexOf("internal (string kind, string pose) PoseNow")];
        Assert.Contains("AoSitReallyOn()", still); Assert.DoesNotContain("idlePosed?.kind", still);
    }
    [Fact] public void ACheckIsWantedAfterASitOrStand_NotOnStandToStandRotation()
    {
        Assert.True(Hover.HoverCheckWantedOnPosture(null, "stand"));     // login
        Assert.True(Hover.HoverCheckWantedOnPosture("stand", "sit"));
        Assert.True(Hover.HoverCheckWantedOnPosture("sit", "stand"));
        Assert.True(Hover.HoverCheckWantedOnPosture("seat", "stand"));   // got up from a chair
        Assert.False(Hover.HoverCheckWantedOnPosture("stand", "stand")); // AO rotates stands: not a change
        Assert.False(Hover.HoverCheckWantedOnPosture("sit", "sit"));
        Assert.False(Hover.HoverCheckWantedOnPosture("stand", "seat"));  // on an object the normal hover is restored elsewhere
        Assert.False(Hover.HoverCheckWantedOnPosture(null, "seat"));
    }
    [Fact] public void ARegionArrivalRequestsACheck_AfterThePinnedHoverPost()
    {
        var pr = Src("Program.cs");
        int sc = pr.IndexOf("client.Network.SimChanged += (s, e) => { Log(\"region\"");
        var line = pr[sc..pr.IndexOf('\n', sc)];
        Assert.Contains("HoverCheckRequest(\"region\", 12000)", line);   // 12 s: after the 3 s "region change" pin post
        Assert.Contains("Task.Delay(3000)", line);
    }
    [Fact] public void Check_WaitsWhileMovingLeashedBusyOrJustChanged_ThenRunsOnce()
    {
        Assert.Equal((true, (string)null), Hover.HoverCheckReady("stand", 0f, false, false, false, 10, 100, false));
        Assert.Equal((true, (string)null), Hover.HoverCheckReady("sit", 0f, false, false, false, 10, 100, false));
        Assert.False(Hover.HoverCheckReady("stand", 1.5f, false, false, false, 10, 100, false).go);      // walking
        Assert.False(Hover.HoverCheckReady("stand", 0f, true, false, false, 10, 100, false).go);         // leashed
        Assert.False(Hover.HoverCheckReady("stand", 0f, false, true, false, 10, 100, false).go);         // following
        Assert.False(Hover.HoverCheckReady("sit", 0f, false, false, true, 10, 100, false).go);           // pose entry/exit running
        Assert.False(Hover.HoverCheckReady("stand", 0f, false, false, false, 1, 100, false).go);         // posture changed 1 s ago
        var cool = Hover.HoverCheckReady("stand", 0f, false, false, false, 10, 5, false);
        Assert.False(cool.go); Assert.DoesNotContain("skip", cool.why);                                   // cooldown: retried, not dropped
        Assert.True(Hover.HoverCheckReady("stand", 0f, false, false, false, 10, 5, true).go);            // a manual request ignores the cooldown only
        Assert.False(Hover.HoverCheckReady("stand", 1.5f, false, false, false, 10, 5, true).go);         // ...never the movement guard
        var seat = Hover.HoverCheckReady("seat", 0f, false, false, false, 10, 100, false);
        Assert.False(seat.go); Assert.StartsWith("skip:", seat.why);                                      // on an object: dropped, not retried
        Assert.True(Hover.HoverCheckMaxTries <= 10 && Hover.HoverCheckCooldownSec >= 20);             // cannot loop forever or storm
    }
    [Fact] public void RequestIsDebounced_NewestWins_AndRunsPoseFirst()
    {
        var ph = Src("PoseHover.cs");
        var req = ph[ph.IndexOf("internal Task<string> HoverCheckRequest(")..]; req = req[..req.IndexOf("// is she still in that pose?")];
        Assert.Contains("Interlocked.Increment(ref hoverReqSeq)", req);
        Assert.Contains("!= id) return \"replaced", req);                                  // a newer request replaced this one
        Assert.True(req.IndexOf("await Task.Delay(delayMs)") < req.IndexOf("await PoseHoverCheck("));   // pose first, hover after
        Assert.Contains("attempt >= HoverCheckMaxTries", req);                   // bounded retries
        Assert.Contains("hoverCheckLastRun = DateTime.Now", req);                // cooldown stamp before the run: a failed check cannot loop
        // after the fix nothing re-requests a check (no loop through PostHover)
        var chk = ph[ph.IndexOf("async Task<string> PoseHoverCheck(")..];
        Assert.DoesNotContain("HoverCheckRequest(", chk);
        Assert.DoesNotContain("HoverPostureNote(", Src("HeightGuard.cs"));
    }
    [Fact] public void IdleTickAlsoNotesPosture_SoAMissedPacketStillTriggersOnce()
    {
        var ip = Src("IdlePose.cs");
        Assert.Contains("hoverSvc.HoverPostureNote(SelfAnims(), client.Self.SittingOn != 0)", ip);
        // the AO's own sit-confirm (a sit-to-sit switch) also requests, and it is the same debounced request
        Assert.Contains("HoverCheckRequest(\"sit\")", ip);
    }
    [Fact] public void ManualHoverCheckCommand_Exists_AndCollarAdjustHeightStaysIgnored()
    {
        var hg = Src("HeightGuard.cs");
        Assert.Contains("a[0] == \"check\"", hg); Assert.Contains("HoverCheckRequest(\"manual\"", hg); Assert.Contains("hover check", hg);
        Assert.False(Program.RlvAdjustHeightApplies(null));
        var rlv = Src("RlvAdjustHeight.cs");
        Assert.True(rlv.IndexOf("RlvAdjustHeightApplies(") < rlv.IndexOf("PostHover(v"));
    }
}
