using System.IO;
using GalatayText;
using Xunit;
// 2026-11-04 (David): "find a way to not get up or change poses when you're in the middle of an adjustment."
// The makeup table's Slump check pressed Z+ for 17 cm, wander's sit timer expired, and it stood her up mid-adjust before the undo could land.
// These are the two pure rules the seated wander loops now ask: an adjustment in progress holds the sit and its pose change.
public class SeatAdjustHoldTests
{
    const double Stay = 200;
    // seated, not paused, an adjust run in progress
    static bool Holding(double elapsed, bool adjusting, bool seated = true, bool paused = false)
        => SeatAdjust.SitKeepsSitting(elapsed, Stay, seated, paused, adjusting);
    static bool Changing(double elapsed, double changeAt, bool changed, bool adjusting, bool seated = true, bool paused = false)
        => SeatAdjust.SitChangesPose(elapsed, changeAt, changed, seated, paused, adjusting);

    [Fact] public void AnAdjustment_HoldsTheSitPastTheStay()
    {
        Assert.True(Holding(Stay - 1, adjusting: false));      // normally still sitting
        Assert.True(Holding(Stay + 1, adjusting: true));       // past the stay, mid-adjust: do not stand up
        Assert.True(Holding(Stay * 10, adjusting: true));      // a hung adjust still never stands her up on the timer
    }
    [Fact] public void WithoutAnAdjustment_TheSitEndsAtTheStay()
    {
        Assert.True(Holding(Stay - 0.5, adjusting: false));
        Assert.False(Holding(Stay, adjusting: false));         // the stay is over: the loop ends and she stands up
        Assert.False(Holding(Stay + 0.5, adjusting: false));
    }
    [Theory] [InlineData(0.0, false)] [InlineData(150.0, false)] [InlineData(199.9, false)]
    public void ThePoseChange_WaitsForTheAdjustmentToFinish(double elapsed, bool expect)
        => Assert.Equal(expect, Changing(elapsed, changeAt: 100, changed: false, adjusting: true));
    [Fact] public void ThePoseChange_IsAllowedOnceTheAdjustmentHasEnded()
    {
        Assert.False(Changing(120, changeAt: 100, changed: false, adjusting: true));    // mid-adjust: refused
        Assert.True(Changing(121, changeAt: 100, changed: false, adjusting: false));    // the very next tick after it ends: allowed
        Assert.False(Changing(99, changeAt: 100, changed: false, adjusting: false));    // not yet due
        Assert.False(Changing(120, changeAt: 100, changed: true, adjusting: false));    // only ever one change per sit
    }
    [Fact] public void APause_StillInterrupts_EvenWhileAdjusting()
    {
        Assert.False(Holding(10, adjusting: true, paused: true));                 // the pause ends the sit loop
        Assert.False(Changing(150, changeAt: 100, changed: false, adjusting: true, paused: true));   // and no pose change on the way out
    }
    [Fact] public void AlreadyStoodUp_EndsTheSit_AdjustingOrNot()
    {
        Assert.False(Holding(10, adjusting: true, seated: false));
        Assert.False(Holding(Stay + 1, adjusting: true, seated: false));
        Assert.False(Changing(150, changeAt: 100, changed: false, adjusting: true, seated: false));   // no pose change from off the seat
    }
    [Fact] public void BothWanderSeatedLoops_UseTheHoldRules()
    {
        var d = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent;
        var w = File.ReadAllText(Path.Combine(d!.FullName, "textclient/galatay-text/Wander.cs"));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(w, @"while \(SeatAdjust\.SitKeepsSitting\(").Count);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(w, @"if \(SeatAdjust\.SitChangesPose\(").Count);
        Assert.DoesNotContain("if (!changed && (DateTime.Now - t0).TotalSeconds >= changeAt", w);   // the inline shape is gone
        var run = File.ReadAllText(Path.Combine(d!.FullName, "textclient/galatay-text/SeatAdjustRun.cs"));
        Assert.Contains("SeatAdjustBusy => Volatile.Read(ref seatAdjustBusyCount) > 0", run);
        Assert.Contains("Interlocked.Increment(ref seatAdjustBusyCount)", run);
        Assert.Contains("Interlocked.Decrement(ref seatAdjustBusyCount)", run);
    }
}
