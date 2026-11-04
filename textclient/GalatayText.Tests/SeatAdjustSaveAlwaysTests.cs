using System;
using System.IO;
using GalatayText;
using Xunit;

// 2026-11-04 (David): "just save your adjustments anyway" / "You don't need to ask for permission to save your adjustments because you will
// always be checking them." The check used to keep a nudge UNSAVED whenever the re-render "read differently" or was not good enough to
// confirm, and pinged him to look. Now every nudge that was APPLIED is saved; only losing the furniture (or pressing nothing) stops it.
public class SeatAdjustSaveAlwaysTests
{
    static string Src(string f)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, "textclient/galatay-text", f));
    }
    static SeatAdjust.SupportReading R(string kind, double gap, params string[] bear) => new(kind, bear, gap, new());
    const string GoodQ = "GT: support quality: frame z 20.000..20.600; seat found; hits shins 12, feet 9, hips 10, thighs 8, hands 11; surface shins 20.150, feet 20.140, hips 20.300, thighs 20.310, hands 20.320\nGT: render quality: overexposed 0.090; dark 0.100; contrast 0.300";
    const string UnconfirmedQ = "GT: support quality: frame z 21.050..21.550; seat not found; hits shins 3, feet 3; surface shins 20.900, feet 20.900\nGT: render quality: overexposed 0.050; dark 0.000; contrast 0.208";

    [Fact] public void AnAppliedNudge_IsSaved_EvenWhenTheBearingPartsChanged()
    {
        // the makeup table 2026-11-04: hips+thighs before the nudge -> hips+thighs+hands after, which the old rule kept UNSAVED
        var (call, why) = SeatAdjust.SaveConfidence(R("sitting", 0.15, "hips", "thighs"), SeatAdjust.ParseQuality(GoodQ),
                                                   R("sitting", 0.00, "hips", "thighs", "hands"), SeatAdjust.ParseQuality(GoodQ));
        Assert.Equal(SeatAdjust.SaveCall.Save, call);
        Assert.Contains("reads differently after the nudge", why);       // the log still tells the story
        Assert.Contains("hips+thighs", why); Assert.Contains("hips+thighs+hands", why);
    }
    [Fact] public void AnAppliedNudge_IsSaved_EvenWhenTheReRenderCannotConfirmIt()
    {
        var (call, why) = SeatAdjust.SaveConfidence(R("sitting", 0.12, "hips"), SeatAdjust.ParseQuality(GoodQ),
                                                   R("sitting", 0.03, "hips"), SeatAdjust.ParseQuality(UnconfirmedQ));
        Assert.Equal(SeatAdjust.SaveCall.Save, call);
        Assert.Contains("not good enough to confirm", why);
    }
    [Fact] public void AnAppliedNudge_ThatReadsBetterButOutOfBand_IsSaved()
    {
        var (call, why) = SeatAdjust.SaveConfidence(R("sitting", 0.20, "hips"), SeatAdjust.ParseQuality(GoodQ),
                                                   R("sitting", 0.06, "hips"), SeatAdjust.ParseQuality(GoodQ));
        Assert.Equal(SeatAdjust.SaveCall.Save, call);
        Assert.Contains("outside the contact band", why);
    }
    [Fact] public void NoPresses_MeansNothingIsSaved()
    {
        var q = SeatAdjust.ParseQuality(GoodQ);
        var (call, why) = SeatAdjust.SaveDecision(R("sitting", 0.02, "hips"), q, R("sitting", 0.02, "hips"), q, 0, true, true);
        Assert.Equal(SeatAdjust.SaveCall.CannotSave, call);
        Assert.Contains("no presses were applied", why);
    }
    [Fact] public void SeatLostMidRun_IsSavedFalse_WithTheHonestReason()
    {
        var q = SeatAdjust.ParseQuality(GoodQ);
        var (call, why) = SeatAdjust.SaveDecision(R("sitting", 0.12, "hips"), q, R("sitting", 0.00, "hips"), q, 12, onSameSeat: false, menuAlive: true);
        Assert.Equal(SeatAdjust.SaveCall.CannotSave, call);
        Assert.Contains("stood up", why);
        Assert.DoesNotContain("confident", why);                          // it never claims a save it could not make
        // the menu lost (the presses got no answer) reads differently from being stood up
        Assert.Contains("menu was lost", SeatAdjust.CannotSaveWhy(12, onSameSeat: true, menuAlive: false));
    }
    [Fact] public void AWorseReRender_IsStillUndone_NotSaved_EvenWhenTheRunCouldNotSave()
    {
        var q = SeatAdjust.ParseQuality(GoodQ);
        Assert.Equal(SeatAdjust.SaveCall.Revert, SeatAdjust.SaveDecision(R("sitting", 0.05, "hips"), q, R("sitting", 0.09, "hips"), q, 5, true, true).call);
        // she was stood up before the undo: the run must still say "COULD NOT undo", not "could not save" (PR #427's honesty)
        Assert.Equal(SeatAdjust.SaveCall.Revert, SeatAdjust.SaveDecision(R("sitting", 0.05, "hips"), q, R("sitting", 0.09, "hips"), q, 5, false, true).call);
    }
    [Fact] public void TheAVsitterPath_SavesOnEveryAppliedNudge_AndOnlyReportsWhenItCannot()
    {
        var run = Src("SeatAdjustRun.cs");
        Assert.DoesNotContain("KeepUnsaved", run);                                      // the outcome that left a nudge on but unsaved is gone
        Assert.Contains("SeatAdjust.SaveDecision(before, q0, cur, qcur,", run);                                  // the tested rule, not a second one
        Assert.Contains("(int)Math.Round(Math.Abs(applied) / SeatAdjust.Step), SeatAdjustOnSeat(seat, gen), d != null);", run);
        Assert.Contains("SeatAdjust.SaveDecision(before, q0, cur, qcur, net, SeatAdjustOnSeat(seat, gen), d != null, save != null);", run);   // the generic path
        Assert.Contains("saved ([SAVE]): {why}", run);                                  // the log says what the old confidence rule thought
        Assert.Contains("saved ('{save}'): {why}", run);
        // the [SAVE] press is still the furniture's own button on the open menu, and it is still waited on
        Assert.Contains("client.Self.ReplyToScriptDialog(d.Channel, si, d.ButtonLabels[si], d.ObjectID)", run);
        Assert.Contains("var after = await WaitSeatDialog(seat, t, 5000, ct);", run);
        // the measurement and the presses are untouched: the same CorrectionZ, the same re-render, the same undo of a worse nudge
        Assert.Contains("double dz = SeatAdjust.CorrectionZ(cur);", run);
        Assert.Contains("SeatAdjustPressZ(seat, d, -applied, ct)", run);
        Assert.Contains("COULD NOT undo", run);                                          // PR #427's honesty is intact
    }
    [Fact] public void TheGate_StillExists_AndStillRefusesToPressOnAnUntrustedRender()
    {
        var gate = Src("SeatAdjustGate.cs");
        Assert.Contains("internal enum SaveCall { Save, CannotSave, Revert }", gate);
        Assert.DoesNotContain("KeepUnsaved", gate);
        Assert.Contains("if (pressesApplied == 0) return (SaveCall.CannotSave, \"no presses were applied\");", gate);
        var run = Src("SeatAdjustRun.cs");
        Assert.Contains("if (g0 != null) { SALog($\"'{seatName}' {pose}: {SeatAdjust.Describe(r0)} but the render cannot be trusted ({g0}): nothing pressed", run);
    }
    [Fact] public void SAUnsaved_FiresOnlyForTheGenuineCannotSaveCases()
    {
        var run = Src("SeatAdjustRun.cs");
        var calls = System.Text.RegularExpressions.Regex.Matches(run, @"SAUnsaved\(seatName, pose, [^;]*;");
        Assert.NotEmpty(calls);
        foreach (System.Text.RegularExpressions.Match c in calls)
        {
            var line = c.Value;
            Assert.DoesNotContain("not confident", line);                  // never for "not confident" any more
            Assert.DoesNotContain("I am not confident enough", line);
        }
        Assert.Contains("SAUnsaved(seatName, pose, applied, why + \", so the nudge is still unsaved\");", run);   // cannot save
        Assert.Contains("SAUnsaved(seatName, pose, applied, \"the [SAVE] press got no answer from the furniture (save not confirmed)\");", run);
        Assert.Contains("SAUnsaved(seatName, pose, applied, noUndo + \", so an unproven nudge may still be on\");", run);   // the undo that could not land
    }
}
