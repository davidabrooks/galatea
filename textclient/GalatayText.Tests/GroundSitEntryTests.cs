using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GalatayText;
using Xunit;
using LibreMetaverse;
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
        public Task<(string press, Task<string> readback)> PressForceSit() { Calls.Add("press"); return Task.FromResult(("pressed", (Task<string>)Task.FromResult("AO force sit now ON"))); }
        public bool ChosenPosePlaying() { Calls.Add("poll"); return true; }
    }
    // fake clock + fake animation feed: the chosen pose starts playing at poseAtMs after the press (null = never); the read-back finishes at 11000 ms
    sealed class Clocked : IGroundSitSteps
    {
        public int Now; readonly int? poseAtMs; public int? ReadbackAwaitedAt; public int Presses;
        public Clocked(int? poseAtMs) { this.poseAtMs = poseAtMs; }
        public Task<(bool ok, string info)> PreSelectPose() => Task.FromResult((true, "ground sit 7 -> 5 VAMOSITG22 (of 10)"));
        public Task<string> SitDown() => Task.FromResult("sim ground sit playing");
        public Task<(string press, Task<string> readback)> PressForceSit()
        {
            Presses++;
            async Task<string> Rb() { await rbGate.Task; return "AO force sit now ON"; }
            return Task.FromResult(("pressed", Rb()));
        }
        public bool ChosenPosePlaying() => poseAtMs is int t && Now >= t;
        readonly TaskCompletionSource rbGate = new();
        public Task Delay(int ms) { Now += ms; return Task.CompletedTask; }
        // the read-back ends at 11 s: the cap-hit path awaits it, the clock jumps there
        public void FinishReadback() { Now = System.Math.Max(Now, 11000); ReadbackAwaitedAt = Now; rbGate.TrySetResult(); }
    }

    [Fact] public async Task HoverCheckIsScheduledWhenTheChosenPosePlays_NotAfterThe11sReadback()
    {
        var f = new Clocked(poseAtMs: 1200); var run = GroundSitEntry.Run(f, f.Delay);
        Assert.True(run.IsCompleted);                        // did not wait for the read-back (never finished here)
        var r = await run;
        Assert.True(r.poseSeen); Assert.Equal(1, f.Presses);
        Assert.InRange(r.poseWaitMs, 1200, 1500);            // polled every 250 ms
        Assert.True(f.Now < 2000, $"entry returned at {f.Now} ms");   // the 11 s read-back did not hold it
        var hoverAt = f.Now + GroundSitEntry.HoverDelayMs(r.poseSeen, 5000);
        Assert.True(hoverAt < 5000, $"hover check at {hoverAt} ms after the press");   // the old order: >= 11000 + 1000
        Assert.InRange(GroundSitEntry.HoverSettleMs, 1500, 2000);
    }
    [Fact] public async Task CapHit_FallsBackToTheReadback_ThenTheShortDelay()
    {
        var f = new Clocked(poseAtMs: null); var run = GroundSitEntry.Run(f, f.Delay);
        Assert.False(run.IsCompleted);                       // still waiting on the read-back after the cap
        f.FinishReadback(); var r = await run;
        Assert.False(r.poseSeen); Assert.Equal(1, f.Presses);
        Assert.Equal(GroundSitEntry.PoseWaitCapMs, r.poseWaitMs);
        Assert.NotNull(f.ReadbackAwaitedAt);                 // waited for the read-back
        Assert.Contains("AO force sit now ON", r.press);
        Assert.Equal(GroundSitEntry.FallbackHoverDelayMs, GroundSitEntry.HoverDelayMs(false, 99999));
    }
    [Fact] public void HoverDelay_MeetsTheHoverChecksOwnPostureSettle()
    {
        Assert.Equal(GroundSitEntry.HoverSettleMs, GroundSitEntry.HoverDelayMs(true, 5000));
        Assert.Equal(Hover.HoverCheckSettleSec * 1000 + 200 - 500, GroundSitEntry.HoverDelayMs(true, 500));
    }
    [Fact] public void ChosenPose_NeedsANewAoSitAndTheSimSit_NotTheBuiltinGroundSit()
    {
        var vamo = UUID.Random(); var sits = new[] { vamo };
        Assert.True(PoseEvidence.ChosenGroundSitPlaying(new[] { vamo }, sits, 0, true, false));
        Assert.False(PoseEvidence.ChosenGroundSitPlaying(new[] { vamo }, sits, 0, false, false));     // no sim sit
        Assert.False(PoseEvidence.ChosenGroundSitPlaying(new[] { vamo }, sits, 0, true, true));       // on an object
        Assert.False(PoseEvidence.ChosenGroundSitPlaying(new[] { Animations.SIT_GROUND_staticRAINED }, sits, 0, true, false));   // generic sit only
        Assert.True(PoseEvidence.ChosenGroundSitPlaying(new[] { UUID.Random() }, sits, 1, true, false));   // new AO anim not yet learned
        Assert.False(PoseEvidence.ChosenGroundSitPlaying(new[] { UUID.Random() }, sits, 0, true, false));
    }

    [Fact] public async Task EntryChoosesThePoseFirst_ThenSitsOnce_ThenPressesOnce()
    {
        var f = new Fake(); var r = await GroundSitEntry.Run(f);
        Assert.Equal(new[] { "select", "sit", "press", "poll" }, f.Calls);   // the old order was sit, press, select
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
        Assert.True(steps.IndexOf("AoGroundSitSwitch()") is var pick and > 0 && pick < steps.IndexOf("SitOnGround()") && steps.IndexOf("SitOnGround()") < steps.IndexOf("AoGroundSitPress("));
        Assert.Contains("aoSitPrePick = true", steps);
        Assert.Contains("finally { idleLock.Release(); if (hoverAfter) hoverSvc.HoverCheckRequest(\"ground sit\", hoverDelayMs); }", src);   // ONE request, made once the entry is over (poseBusy would hold it for the 8 s retry)
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
