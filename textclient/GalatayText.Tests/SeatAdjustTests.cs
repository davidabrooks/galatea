using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GalatayText;
using Xunit;
// 2026-11-02 (David): after a furniture pose settles, render, nudge with the furniture's own adjust by the MEASURED gap, re-render, SAVE only when better and in band.
public class SeatAdjustTests
{
    static SeatAdjust.SupportReading R(string kind, double gap, params string[] bear) => new(kind, bear, gap, new());
    static readonly List<string> Personal = new() { "[BACK]", "Position", "0.05m", "[DEFAULT]", "[SAVE]", "[SAVE ALL]", "X+", "Y+", "Z+", "X-", "Y-", "Z-" };

    [Fact] public void Parse_ReadsKindBearingGapAndParts()
    {
        var r = SeatAdjust.Parse("x.jpg\nGT: furniture support: kneeling+hands; bearing shins,feet,hands; gap -0.041 m; parts hips +0.300, shins -0.050, hands -0.020\n");
        Assert.Equal("kneeling+hands", r.Kind); Assert.Equal("kneeling", r.BaseKind); Assert.Equal(new[] { "shins", "feet", "hands" }, r.Bearing);
        Assert.Equal(-0.041, r.Gap); Assert.Equal(-0.05, r.Parts["shins"]); Assert.True(r.Conclusive);
    }
    [Fact] public void Parse_UnknownAndMissingAreInconclusive()
    {
        Assert.False(SeatAdjust.Parse("GT: furniture support: unknown (no surface under any part)").Conclusive);
        Assert.Null(SeatAdjust.Parse("GT: seat contact gap +0.010 m"));
    }
    [Fact] public void Band_SoftPartsMaySink_HardPartsMayNot()
    {
        Assert.True(SeatAdjust.InBand(R("sitting", -0.04, "hips", "thighs")));
        Assert.False(SeatAdjust.InBand(R("standing", -0.04, "feet")));
        Assert.False(SeatAdjust.InBand(R("sitting", +0.05, "hips")));
    }
    [Theory] [InlineData(0.083, -0.08)] [InlineData(-0.127, 0.13)] [InlineData(0.004, 0)]
    public void Correction_IsMinusTheMeasuredGap(double gap, double expect) => Assert.Equal(expect, SeatAdjust.CorrectionZ(R("standing", gap, "feet")), 6);
    [Fact] public void Correction_ZeroWhenInBandOrInconclusive()
    {
        Assert.Equal(0, SeatAdjust.CorrectionZ(R("sitting", -0.03, "hips")));
        Assert.Equal(0, SeatAdjust.CorrectionZ(new SeatAdjust.SupportReading(null, Array.Empty<string>(), null, new())));
    }
    [Fact] public void Correction_ScalesWithTheMeasurement_NoFixedValue()
    {   // two different measurements never give the same correction: nothing is a constant offset
        var a = SeatAdjust.CorrectionZ(R("sitting", 0.10, "hips")); var b = SeatAdjust.CorrectionZ(R("sitting", 0.20, "hips"));
        Assert.Equal(2 * a, b, 6);
    }
    [Fact] public void Decide_NeverSavesWhenInconclusiveWorseOrPoseKindChanged()
    {
        var before = R("sitting", 0.08, "hips");
        Assert.Equal(SeatAdjust.Verdict.Inconclusive, SeatAdjust.Decide(before, null));
        Assert.Equal(SeatAdjust.Verdict.Inconclusive, SeatAdjust.Decide(before, new SeatAdjust.SupportReading(null, Array.Empty<string>(), null, new())));
        Assert.Equal(SeatAdjust.Verdict.Inconclusive, SeatAdjust.Decide(before, R("standing", 0.0, "feet")));
        Assert.Equal(SeatAdjust.Verdict.Revert, SeatAdjust.Decide(before, R("sitting", 0.09, "hips")));
        Assert.Equal(SeatAdjust.Verdict.Revert, SeatAdjust.Decide(before, R("sitting", -0.078, "hips")));   // no measurable improvement
        Assert.Equal(SeatAdjust.Verdict.Continue, SeatAdjust.Decide(before, R("sitting", 0.04, "hips")));
        Assert.Equal(SeatAdjust.Verdict.Save, SeatAdjust.Decide(before, R("sitting", 0.005, "hips")));
        Assert.Equal(SeatAdjust.Verdict.Save, SeatAdjust.Decide(R("kneeling", 0.08, "shins"), R("kneeling+hands", 0.0, "shins", "hands")));
    }
    [Fact] public void MayAdjust_HomeOrOwnOnly()
    {
        Assert.True(SeatAdjust.MayAdjust(true, false)); Assert.True(SeatAdjust.MayAdjust(false, true)); Assert.False(SeatAdjust.MayAdjust(false, false));
    }
    [Fact] public void Classify_AvsitterMenus()
    {
        Assert.Equal(SeatAdjust.Menu.Personal, SeatAdjust.Classify("Personal adjustment:", Personal));
        Assert.Equal(SeatAdjust.Menu.Options, SeatAdjust.Classify("Adjust:", new List<string> { "[BACK]", "[SECURITY]", "[POSE]" }));
        Assert.Equal(SeatAdjust.Menu.PoseMenu, SeatAdjust.Classify("Pose: [Relax]", new List<string> { "[ADJUST]", "[<<]", "[>>]", "Relax", "[BACK]" }));
        Assert.Equal(SeatAdjust.Menu.None, SeatAdjust.Classify("nPose", new List<string> { "Sit", "Options", "Adjust" }));
    }
    [Fact] public void PersonalState_ReadsModeAndStep()
    {
        Assert.Equal((true, (double?)0.05), SeatAdjust.PersonalState(Personal));
        var rot = Personal.Select(x => x == "Position" ? "Rotation" : x == "0.05m" ? "5°" : x).ToList();
        Assert.Equal((false, (double?)null), SeatAdjust.PersonalState(rot));
    }
    [Fact] public void PlanZ_DecomposesIntoAvsitterSteps_CyclingTheStepButton()
    {
        var (p, after) = SeatAdjust.PlanZ(-0.08, 0.05);   // 1 x 0.05 then (0.05 -> 0.25 -> 0.01) 3 x 0.01
        Assert.Equal(new[] { "Z-", "0.05m", "0.25m", "Z-", "Z-", "Z-" }, p); Assert.Equal(0.01, after);
        var (q, _) = SeatAdjust.PlanZ(0.31, 0.01); Assert.Equal("Z+ 0.01m Z+ 0.05m Z+", string.Join(" ", q));
        double moved = 0; double st = 0.01;
        foreach (var b in q) { if (b == "Z+") moved += st; else st = SeatAdjust.StepCycle[(Array.IndexOf(SeatAdjust.StepCycle, st) + 1) % 3]; }
        Assert.Equal(0.31, moved, 6);
        Assert.Empty(SeatAdjust.PlanZ(0.004, 0.05).presses);
    }
    [Fact] public void LogRow_HasObjectPoseOffsetAndBeforeAfter()
    {
        var row = SeatAdjust.LogRow(new DateTime(2026, 11, 2, 16, 30, 0), "Rocking chair (abc)", "Solo* > Relax", -0.08, R("sitting", 0.08, "hips", "thighs"), R("sitting", 0.004, "hips", "thighs"), "SAVED");
        Assert.Contains("Rocking chair (abc)", row); Assert.Contains("Z -0.08 m", row); Assert.Contains("gap +0.080", row); Assert.Contains("gap +0.004", row); Assert.Contains("SAVED", row);
    }
    [Fact] public void Wiring_SeatPoseStartsTheAdjust_AndItNeverTouchesTheHover()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory); while (d != null && !Directory.Exists(Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent;
        string Src(string f) => File.ReadAllText(Path.Combine(d!.FullName, "textclient/galatay-text", f));
        Assert.Contains("SeatAdjustAfterPoseAsync(seat, seatName", Src("SeatPose.cs"));
        var run = Src("SeatAdjustRun.cs");
        Assert.DoesNotContain("SetHover", run); Assert.DoesNotContain("PostHover", run);
        Assert.Contains("SeatAdjust.Decide(before, cur) == SeatAdjust.Verdict.Save", run);   // SAVE needs a better-than-the-first-render result too
    }
}
