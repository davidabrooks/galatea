using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GalatayText;
using Xunit;
// 2026-10-28 (David: "You should only sit once, not three times. ... You should do that [choose the pose] first."): the AO ground-sit entry used to be
// SitOnGround (generic SIT_GROUND) -> force sitg (the AO's LAST pose) -> pick a new pose (a third pose + a re-measured hover). It is now: pick the pose while
// standing -> ONE sit -> ONE force-sitg press -> ONE hover check. These tests fail on the old order.
public class GroundSitEntryTests
{
    static string Root() { var d = new DirectoryInfo(System.AppContext.BaseDirectory); while (d != null && !Directory.Exists(Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent; return d!.FullName; }
    static string Src(string f) => File.ReadAllText(Path.Combine(Root(), "textclient/galatay-text", f));

    sealed class Fake : IGroundSitSteps
    {
        public readonly List<string> Calls = new(); readonly bool pickOk;
        public Fake(bool pickOk = true) { this.pickOk = pickOk; }
        public Task<(bool ok, string info)> PreSelectPose() { Calls.Add("select"); return Task.FromResult((pickOk, pickOk ? "ground sit 7 -> 5 VAMOSITG22 (of 10)" : "ground sit 5 VAMOSITG22 pressed but the AO did not confirm")); }
        public Task<string> SitDown() { Calls.Add("sit"); return Task.FromResult("sim ground sit playing"); }
        public Task<string> PressForceSit() { Calls.Add("press"); return Task.FromResult("AO force sit now ON"); }
    }

    [Fact] public async Task EntryChoosesThePoseFirst_ThenSitsOnce_ThenPressesOnce()
    {
        var f = new Fake(); var r = await GroundSitEntry.Run(f);
        Assert.Equal(new[] { "select", "sit", "press" }, f.Calls);   // the old order was sit, press, select
        Assert.True(r.entered); Assert.Contains("VAMOSITG22", r.pick);
    }
    [Fact] public async Task AFailedPick_EntersNothing_ShestaysStanding()
    {
        var f = new Fake(pickOk: false); var r = await GroundSitEntry.Run(f);
        Assert.Equal(new[] { "select" }, f.Calls);   // no sit, no force-sit press
        Assert.False(r.entered); Assert.Null(r.sit); Assert.Null(r.press);
    }
    [Fact] public void PickConfirmed_OnlyOnTheAosOwnLine()
    {
        Assert.True(Program.GroundSitPickConfirmed("ground sit 7 -> 5 VAMOSITG22 (of 10)"));
        Assert.True(Program.GroundSitPickConfirmed("ground sit 5 VAMOSITG22 (of 10)"));      // unknown previous pick
        Assert.True(Program.GroundSitPickConfirmed("only 1 ground sit(s): nothing to switch"));
        Assert.False(Program.GroundSitPickConfirmed("ground sit 5 VAMOSITG22 pressed but the AO did not confirm (current unknown); left as is"));   // starts with "ground sit" too
        Assert.False(Program.GroundSitPickConfirmed("could not reach ground sit 9"));
        Assert.False(Program.GroundSitPickConfirmed("AO ground-sit chooser did not open"));
        Assert.False(Program.GroundSitPickConfirmed("only 0 ground sit(s): nothing to switch"));
        Assert.False(Program.GroundSitPickConfirmed(null));
    }
    [Fact] public void ForceSitIsPressedAsSoonAsTheSimsGroundSitPlays_NotAfterAFixedWait()
    {
        Assert.False(Program.GroundSitPressReady(false, 100));
        Assert.False(Program.GroundSitPressReady(true, 100));    // seen, but give the sit 0.5 s to settle
        Assert.True(Program.GroundSitPressReady(true, 500));
        Assert.False(Program.GroundSitPressReady(false, 2000));
        Assert.True(Program.GroundSitPressReady(false, 2500));   // never waits longer than 2.5 s (the old fixed wait was 1.5 s + 2 s + the pick)
    }
    [Fact] public void ThePrePickWhileStandingAsksForNoHoverCheck_ARotationDoes()
    {
        Assert.False(Program.AoSitPickWantsHoverCheck(true, false));    // pre-pick, standing: nothing on the floor yet
        Assert.False(Program.AoSitPickWantsHoverCheck(true, true));
        Assert.False(Program.AoSitPickWantsHoverCheck(false, false));   // a late AO line while standing
        Assert.True(Program.AoSitPickWantsHoverCheck(false, true));     // rotation: the new pose is remeasured
        Assert.Contains("AoSitPickWantsHoverCheck(aoSitPrePick, AoSitReallyOn())", Src("IdlePose.cs"));
    }
    [Fact] public void TheLiveEntryUsesTheOrder_NoPickAfterTheSit_OneSitRequest_OneHoverRequestAfterTheLock()
    {
        var src = Src("IdlePose.cs");
        var enter = src[src.IndexOf("static async Task<string> IdlePoseEnter(")..]; enter = enter[..enter.IndexOf("finally { idleLock.Release();")];
        Assert.Contains("GroundSitEntry.Run(new GroundSitSteps(why))", enter);
        Assert.DoesNotContain("AoGroundSitSwitch", enter);      // the old third pose
        Assert.DoesNotContain("SitOnGround()", string.Join("\n", enter.Split('\n').Where(l => !l.TrimStart().StartsWith("//"))));   // the sit lives in GroundSitSteps.SitDown only
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(src, @"client\.Self\.SitOnGround\(\)").Count);
        var steps = src[src.IndexOf("sealed class GroundSitSteps")..]; steps = steps[..steps.IndexOf("static async Task<string> IdlePoseEnter(")];
        Assert.True(steps.IndexOf("AoGroundSitSwitch()") is var pick and > 0 && pick < steps.IndexOf("SitOnGround()") && steps.IndexOf("SitOnGround()") < steps.IndexOf("AoGroundSitToggle("));
        Assert.Contains("aoSitPrePick = true", steps);
        Assert.Contains("finally { idleLock.Release(); if (hoverAfter) hoverSvc.HoverCheckRequest(\"ground sit\", 1000); }", src);   // ONE request, made once the entry is over (poseBusy would hold it for the 8 s retry)
        var hov = Src("AutoHover.cs");
        Assert.DoesNotContain("Stand()", hov); Assert.DoesNotContain("SitOnGround", hov); Assert.DoesNotContain("AoGroundSitToggle", hov);
    }
    [Fact] public void AfterAnyStand_TheHoverGoesBackToThePin_ExceptAtLogoutOrWhileStillSitting()
    {
        Assert.True(Program.IdleRepinNeeded("manual stop", false, -0.068, 0.0));
        Assert.False(Program.IdleRepinNeeded("manual stop", false, 0.002, 0.0));     // already there
        Assert.False(Program.IdleRepinNeeded("manual stop", true, -0.068, 0.0));     // still sitting: never repin a sit
        Assert.False(Program.IdleRepinNeeded("logout", false, -0.068, 0.0));
        Assert.False(Program.IdleRepinNeeded("manual stop", false, null, 0.0));      // hover in force unknown
        Assert.Contains("IdleRepinAfterStand(why)", Src("IdlePose.cs"));
    }
}
