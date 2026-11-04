using System;
using System.IO;
using System.Text.RegularExpressions;
using GalatayText;
using Xunit;

// 2026-11-04 (David): "I think it looks better if you don't get dressed after an adult pose until you change poses or get up."
// The end-of-stay step used to dress her WHILE SEATED (NakedRedressBeforeStand) just before standing up. Now she stands as she is and the
// redress tick dresses her as soon as she is off the seat; the mid-sit switch to a non-adult pose still dresses her at the switch.
public class DressAfterAdultTests
{
    const string Pending = "TETRA Chill T-Shirt shorts";
    static string Src(string f)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, "textclient/galatay-text", f));
    }
    static (bool due, string why) Due(bool seated, bool sexualPoseNow = false) => Program.NakedRedressDue(Pending, false, seated, sexualPoseNow, 999, false);

    [Theory] [InlineData(true, "the adult pose is still running")] [InlineData(false, "still on the seat")]
    public void StillSeated_IsNeverDue_SoTheEndOfAStayStaysUndressed(bool adultPoseStillShowing, string why0)
    {
        var (due, why) = Due(seated: true, sexualPoseNow: adultPoseStillShowing);
        Assert.False(due);                                       // the end of a stay is just another seated second to this rule
        Assert.Contains(why0, why);
    }
    [Fact] public void Seated_WithANonAdultPose_StaysAsSheIs_UntilSheGetsUp()
    {
        var (due, why) = Due(seated: true, sexualPoseNow: false);
        Assert.False(due);
        Assert.Contains("until the pose changes or she gets up", why);   // David's rule, in the log line
    }
    [Fact] public void OnceSheIsOffTheSeat_ItIsDue_ThatIsTheGetUpHalf()
    {
        Assert.Equal((true, "stood up"), Due(seated: false));    // and the tick runs every second (ParcelWatchTick), so it is prompt
        Assert.True(Due(seated: false, sexualPoseNow: true).due); // she is off the seat, so no pose on it can be "the adult pose still running"
    }
    [Fact] public void TheStayNoLongerDressesHerBeforeSheStands_AndThePostStandDressIsStillThere()
    {
        var w = Src("Wander.cs");
        Assert.DoesNotContain("NakedRedressBeforeStand", w);       // the pre-stand seated redress is gone from the stay
        Assert.DoesNotContain("static async Task NakedRedressBeforeStand", Src("AdultRedress.cs"));   // and the helper is gone, not merely uncalled
        var stand = w.IndexOf("await EnsureStandingForWalk(ct);\n        await StandOutAfterSeat", StringComparison.Ordinal);
        Assert.True(stand > 0);                                    // she stands as she is ...
        Assert.True(w.IndexOf("await DressAfterSeatIfPending();", stand, StringComparison.Ordinal) > stand);   // ... and is dressed after
    }
    [Fact] public void EveryWayOutOfASit_StillLandsOnADressCheck()
    {
        var w = Src("Wander.cs");
        Assert.Contains("await DressAfterSeatIfPending(); return true; }", w);                                  // stood up by something else
        Assert.Contains("else await StandOutAfterSeat(info, ct); await DressAfterSeatIfPending(); return true; }", w);
        Assert.Contains("if (client.Self.SittingOn == 0) { MarkSeatFailed(c, \"sit failed: \" + r); await DressAfterSeatIfPending(); return false; }", w);   // the sit itself failed
        Assert.Contains("if ((pendingDressOutfit != null || pendingToilet != null) && client.Self.SittingOn == 0) await DressAfterSeatIfPending();", w);     // the loop, every pass
        var ws = Src("WanderSit.cs");
        Assert.Contains("if ((pendingDressOutfit != null || pendingToilet != null) && client.Self.SittingOn == 0) await DressAfterSeatIfPending();", ws);    // the sit-visit path
        // the adult-pose outfit is not pendingDressOutfit: its tick runs every second whether wander runs or not, so a chat pause, David's hold,
        // a seat stood up by something else or a boat leave all end with her off the seat and dressed on the next tick
        Assert.Contains("NakedRedressTick();", Src("ParcelWatch.cs"));
        Assert.Contains("try { ParcelWatchTick(); }", Src("Program.cs"));
    }
    [Fact] public void AMidSitSwitchToANonAdultPose_StillDressesAtTheSwitch()
    {
        Assert.Equal("redress", Program.AdultExitStep(nextSexual: false, redressPending: true, faceOn: true));
        Assert.Contains("await NakedRedressRun(\"switching to a non-adult pose\")", Src("AdultRedress.cs"));   // "change poses"
        Assert.Equal(2, Regex.Matches(Src("SeatPose.cs"), @"await LeaveAdultPoseBeforeSwitch\(").Count);       // both seat-menu paths
    }
    [Fact] public void TheHeaderSaysTheNewRuleAndItsDate_AndNamesWhatItMustNotTouch()
    {
        var ar = Src("AdultRedress.cs");
        Assert.Contains("2026-11-04", ar);
        Assert.Contains("until you change poses or get up", ar);
        foreach (var keep in new[] { "quick-relog", "Naked-first", "toilet", "bikini", "RedressTarget" }) Assert.Contains(keep, ar);
    }
    [Fact] public void QuickRelogNothingPendingAndABusyNakedFlow_AreUnaffected()
    {
        Assert.False(Program.NakedRedressDue(Pending, false, false, false, 999, false, quickRelog: true).due);   // still held back
        Assert.False(Program.NakedRedressDue(null, false, false, false, 999, false).due);                       // nothing to put back
        Assert.False(Program.NakedRedressDue(Pending, true, false, false, 999, false).due);                     // Naked flow busy
        Assert.False(Program.NakedRedressDue(Pending, false, false, false, 999, true).due);                     // another outfit change
    }
}
