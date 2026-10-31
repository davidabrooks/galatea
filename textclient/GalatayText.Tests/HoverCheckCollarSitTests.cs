using GalatayText;
using Xunit;

// 2026-10-31 (David: "your automatic hover height check when you sit or change sits is not working").
// 1. Collar kneel / sit poses (12:57:41 'tower', 13:17:37 'nadu') never requested a visual hover check: they are not AO animations, so PoseNow said "stand".
// 2. A check attempt that stopped on "form not known yet" still started the 30 s cooldown; the next tries waited on "another check ran a moment ago" and the
//    request gave up after 6 tries (12:52 login, and 17:14 / 17:16 on 10-30).
public class HoverCheckCollarSitTests
{
    static Program.IdlePoseState Collar(string pose) => new("collar", pose, new(), DateTime.Now);

    [Fact] public void CollarPose_IsAKneel_NotAStand()
    {
        Assert.Equal(("kneel", "nadu"), Hover.PoseKindNow(Collar("nadu"), false, false, null, false));
        Assert.Equal(("stand", "stand"), Hover.PoseKindNow(null, false, false, null, false));
        Assert.Equal(("sit", "VAMOSITG13"), Hover.PoseKindNow(new("groundsit", "force sitg", new(), DateTime.Now), false, true, "VAMOSITG13", false));
        Assert.NotEqual("kneel", Hover.PoseKindNow(Collar("nadu"), true, false, null, false).kind);   // on furniture: never a kneel check
    }

    [Fact] public void KneelCheck_RunsOnlyWhileThatCollarPoseHolds()
    {
        Assert.True(Hover.KneelStill(Collar("nadu"), "Nadu", 0f));
        Assert.False(Hover.KneelStill(Collar("tower"), "nadu", 0f));    // changed pose: that check is stale
        Assert.False(Hover.KneelStill(null, "nadu", 0f));
        Assert.False(Hover.KneelStill(Collar("nadu"), "nadu", 1f));
    }

    [Fact] public void KneelKind_PassesTheReadyGate_AndAPoseChangeIsNeverHeldByTheCooldown()
    {
        Assert.Equal((true, (string)null), Hover.HoverCheckReady("kneel", 0f, false, false, false, 10, 100, false));
        Assert.True(Hover.HoverCheckReady("kneel", 0f, false, false, false, 10, 5, false, poseChanged: true).go);   // tower -> nadu 5 s after a check
        Assert.Equal("knees / shins / feet (lowest contact)", Hover.HoverContactLabel("kneel"));
    }

    [Fact] public void SamePoseJustChecked_FinishesAsCovered_InsteadOfRetryingUntilItGivesUp()
    {
        Assert.True(Hover.HoverCovered(false, "stand", "stand", 8));       // 13:13: region check finished on the stand; the stand request is covered
        Assert.False(Hover.HoverCovered(true, "stand", "stand", 8));       // manual always runs
        Assert.False(Hover.HoverCovered(false, "nadu", "tower", 8));       // a changed pose is always measured
        Assert.False(Hover.HoverCovered(false, "stand", "stand", 40));     // past the cooldown
        Assert.False(Hover.HoverCovered(false, "stand", null, 8));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("form not known yet (attachments still loading)", false)]
    [InlineData("not in that pose", false)]
    [InlineData("a check is already running", false)]
    [InlineData("moved off the furniture", true)]
    public void OnlyARealMeasurementStartsTheCooldown(string retry, bool counts) => Assert.Equal(counts, Hover.HoverRunCounts(retry));

    [Fact] public void Login1252_Replay_FormUnknownFirst_ThenTheCheckRunsOnTheNextTry()
    {
        // try 1 at +4 s: form unknown -> no cooldown; try 2 at +12 s: not blocked by "another check ran a moment ago"
        DateTime lastRun = DateTime.MinValue, t = new(2026, 10, 31, 12, 52, 37);
        if (Hover.HoverRunCounts("form not known yet (attachments still loading)")) lastRun = t;
        var r = Hover.HoverCheckReady("stand", 0f, false, false, false, 10, (t.AddSeconds(8) - lastRun).TotalSeconds, false);
        Assert.True(r.go);
        Assert.True(IdleConst.CollarDelay > 0);
    }
    static class IdleConst { internal static int CollarDelay => Program.CollarHoverDelayMs; }
}
