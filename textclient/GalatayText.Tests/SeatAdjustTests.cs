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
    static string Root() { var d = new DirectoryInfo(AppContext.BaseDirectory); while (d != null && !Directory.Exists(Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent; return d!.FullName; }
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
    [Fact] public void Classify_AvsitterMenus()
    {
        Assert.Equal(SeatAdjust.Menu.Personal, SeatAdjust.Classify("Personal adjustment:", Personal));
        Assert.Equal(SeatAdjust.Menu.Options, SeatAdjust.Classify("Adjust:", new List<string> { "[BACK]", "[SECURITY]", "[POSE]" }));
        Assert.Equal(SeatAdjust.Menu.PoseMenu, SeatAdjust.Classify("Pose: [Relax]", new List<string> { "[ADJUST]", "[<<]", "[>>]", "Relax", "[BACK]" }));
        Assert.Equal(SeatAdjust.Menu.Entry, SeatAdjust.Classify("nPose", new List<string> { "Sit", "Options", "Adjust" }));
        Assert.Equal(SeatAdjust.Menu.None, SeatAdjust.Classify("nPose", new List<string> { "Sit", "Options", "Stand" }));
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
    [Fact] public void Wiring_SeatPoseStartsTheAdjust_AndItNeverTouchesTheHover()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory); while (d != null && !Directory.Exists(Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent;
        string Src(string f) => File.ReadAllText(Path.Combine(d!.FullName, "textclient/galatay-text", f));
        Assert.Contains("SeatAdjustAfterPoseAsync(seat, seatName", Src("SeatPose.cs"));
        var run = Src("SeatAdjustRun.cs");
        Assert.DoesNotContain("SetHover", run); Assert.DoesNotContain("PostHover", run);
        Assert.Contains("SeatAdjust.SaveDecision(before, q0, cur, qcur,", run);   // 2026-11-04: SAVED unless the run cannot save (SeatAdjustGate.cs)
        Assert.Contains("seatPosePicking) > 0) { seatWatchPrev = prev; continue; }", run);   // 17:14: the watcher never runs mid-pick
        Assert.Contains("SeatAdjust.PoseLabelFor(wLastPose", run);                               // ... nor with another seat's pose label
        var sp = Src("SeatPose.cs");
        Assert.Contains("HandPoseForPathAsync(full, seatName); SeatAdjustAfterPoseAsync(seat, seatName", sp);   // the naked-first path is checked too
        Assert.Contains("PoseRules.Current.IsBugged(seatName, l)", sp);
    }
    [Fact] public void Classify_AdjustMenuAfterAvsitterHeaderLine()
    {
        // Mirage bed 2026-11-02: message is "AVsitter™ 2.1\n\nAdjust:" and was read as no AVsitter menu
        Assert.Equal(SeatAdjust.Menu.Options, SeatAdjust.Classify("AVsitter™ 2.1\n\nAdjust:", new List<string> { "[BACK]", "[HELPER]", "[POSE]" }));
        Assert.Equal(SeatAdjust.Menu.PoseMenu, SeatAdjust.Classify("AVsitter™2.1\n\nPress Ctrl S to resync Animations", new List<string> { "selfie", "[ADJUST]", "[BACK]" }));
    }
    [Fact] public void Generic_FindsUpDownSaveByName()
    {
        var l = new List<string> { "<<", "Up", "Down", "Fwd", "Back", "Save", "Menu" };
        Assert.Equal(SeatAdjust.Menu.Generic, SeatAdjust.Classify("nPose adjust", l));
        Assert.Equal(("Up", "Down", "Save"), SeatAdjust.GenericButtons(l));
        Assert.Equal(("Z+", "Z-", (string)null), SeatAdjust.GenericButtons(new List<string> { "X+", "Z+", "Z-" }));
        Assert.Equal(SeatAdjust.Menu.Personal, SeatAdjust.Classify("Personal adjustment:", new List<string> { "X-", "Y-", "Z-", "X+", "Y+", "Z+", "[DEFAULT]", "[SAVE]", "[SAVE ALL]", "[BACK]", "Position", "0.05m" }));
    }
    [Fact] public void GenericPresses_FromMeasuredStep()
    {
        Assert.Equal(-4, SeatAdjust.GenericPresses(-0.04, 0.01));
        Assert.Equal(2, SeatAdjust.GenericPresses(0.05, 0.025));
        Assert.Equal(0, SeatAdjust.GenericPresses(0.05, 0.001));   // presses do not move her measurably
    }
    [Fact] public void SitWatcher_EverySitOnce()
    {
        Assert.True(SeatAdjust.SitNeedsCheck(0, 42, 0));       // new sit, no pose choice yet
        Assert.False(SeatAdjust.SitNeedsCheck(0, 42, 42));     // a pose choice already started the check
        Assert.False(SeatAdjust.SitNeedsCheck(42, 42, 0));     // still on the same seat
        Assert.False(SeatAdjust.SitNeedsCheck(42, 0, 0));      // stood
        Assert.True(SeatAdjust.SitNeedsCheck(42, 43, 42));     // moved to another seat
    }
    // ---- FIX 1 (2026-11-03): a seat reading is the median of 3 samples, never one ----
    [Fact] public void SampleMedian_ThreeAgreeingReadings_UsesTheirMiddle()
    {
        // the Lalou sofa, 2026-11-03, ONE fixed position and pose: +0.043 then +0.021 then +0.019 m, nothing moving
        var rs = new List<SeatAdjust.SupportReading> { R("sitting", 0.043, "hips"), R("sitting", 0.021, "hips"), R("sitting", 0.019, "hips") };
        var (i, med, why) = SeatAdjust.SampleMedian(rs, 3);
        Assert.Equal(0.021, med!.Value, 6);
        Assert.Equal(1, i); Assert.Contains("median", why);
        Assert.Contains("all agree", why);                     // 2.2 cm is inside HoverSampleSpreadMax: kept, and the MIDDLE one is the reading
    }
    [Fact] public void SampleMedian_DropsOneOutlierAndNeverReturnsIt()
    {
        var rs = new List<SeatAdjust.SupportReading> { R("sitting", 0.010, "hips"), R("sitting", 0.012, "hips"), R("sitting", 0.090, "hips") };
        var (i, med, why) = SeatAdjust.SampleMedian(rs, 3);
        Assert.Equal(0.011, med!.Value, 6);                        // the middle of the two that agree
        Assert.NotEqual(0.090, med!.Value, 6);                     // the outlier never drives the nudge
        Assert.InRange(i, 0, 1); Assert.Contains("outlier", why);
    }
    [Fact] public void SampleMedian_ReadingsThatDisagree_GiveNoReading()
    {
        var wide = new List<SeatAdjust.SupportReading> { R("sitting", 0.10, "hips"), R("sitting", 0.00, "hips"), R("sitting", -0.10, "hips") };
        Assert.Equal(-1, SeatAdjust.SampleMedian(wide, 3).index);   // nothing is pressed on a spread this wide
        Assert.Null(SeatAdjust.SampleMedian(wide, 3).median);
        var two = new List<SeatAdjust.SupportReading> { R("sitting", 0.010, "hips"), null, R("sitting", 0.012, "hips") };
        Assert.Equal(-1, SeatAdjust.SampleMedian(two, 3).index);    // a failed sample is not a reading
        var inconclusive = new List<SeatAdjust.SupportReading> { new(null, Array.Empty<string>(), null, new()), R("sitting", 0.010, "hips"), R("sitting", 0.011, "hips") };
        Assert.Equal(-1, SeatAdjust.SampleMedian(inconclusive, 3).index);
        Assert.Equal(-1, SeatAdjust.SampleMedian(null, 3).index);
    }
    [Fact] public void SampleMedian_NeedsTheSamplesItWasAskedFor()
    {
        var one = new List<SeatAdjust.SupportReading> { R("sitting", 0.02, "hips") };
        Assert.Equal(-1, SeatAdjust.SampleMedian(one, 3).index);
        Assert.Equal((0, (double?)0.02), (SeatAdjust.SampleMedian(one, 1).index, SeatAdjust.SampleMedian(one, 1).median));
    }
    [Fact] public void TheSeatedCheck_SamplesEveryPosture_NotJustStanding()
    {
        var ph = File.ReadAllText(Path.Combine(Root(), "textclient/galatay-text/PoseHover.cs"));
        var loop = ph[ph.IndexOf("for (int si = 0; si < HoverSamples; si++)")..]; loop = loop[..loop.IndexOf("var (med, sampleWhy)")];
        Assert.DoesNotContain("break;", loop);                                   // no "sit / kneel: one render, unchanged" early exit any more
        Assert.Contains("HoverSampleMedian(samples, HoverSamples)", ph);         // every posture takes the median of 3
        Assert.DoesNotContain("kind == \"stand\" ? HoverSamples : 1", ph);
        // the reading a seat adjust acts on AND its quality gate come from the same sample
        var sr = ph[ph.IndexOf("internal async Task<(SeatAdjust.SupportReading r, string img, string why, SeatAdjust.Quality q)> SupportRender(")..];
        sr = sr[..sr.IndexOf("// vision/render_quality.py")];
        Assert.Contains("for (int si = 0; si < HoverSamples; si++)", sr);
        Assert.Contains("SeatAdjust.SampleMedian(readings, HoverSamples)", sr);
        Assert.Contains("hoverLastSupport = ri >= 0 ? readings[ri] with { Gap = med } : null;", sr);   // the nudge is the median ...
        Assert.Contains("return (hoverLastSupport, imgs[ri], null, qs[ri]);", sr);                    // ... and the gate judges that same sample's render
    }

    // ---- FIX 2 (2026-11-03): one narrow seatadjust command, reusing the tested PlanZ ----
    [Theory] [InlineData("z", "-0.08", -0.08)] [InlineData("z", "+0.05", 0.05)] [InlineData("Z", "0.13", 0.13)] [InlineData("z", "-1.0", -1.0)]
    public void ParseZ_TakesMetresUpOrDown(string verb, string dz, double expect)
    {
        var (ok, v, err) = SeatAdjust.ParseZ(new[] { verb, dz });
        Assert.True(ok, err); Assert.Equal(expect, v, 6); Assert.Null(err);
    }
    [Fact] public void ParseZ_RefusesWhatItCannotDo()
    {
        foreach (var a in new[] { new[] { "z" }, new[] { "z", "abc" }, new[] { "z", "0.001" }, new[] { "z", "2" }, new[] { "z", "-0.08", "force" }, new[] { "restore", "0.08" }, Array.Empty<string>() })
        {
            var (ok, _, err) = SeatAdjust.ParseZ(a);
            Assert.False(ok); Assert.Contains("usage: seatadjust z", err);
        }
    }
    [Fact] public void SeatAdjustZ_PlansItsPressesWithPlanZ_AndNeverSaves()
    {
        var run = File.ReadAllText(Path.Combine(Root(), "textclient/galatay-text/SeatAdjustRun.cs"));
        var z = run[run.IndexOf("if (a.Length > 0 && string.Equals(a[0], \"z\", StringComparison.OrdinalIgnoreCase))")..];
        z = z[..z.IndexOf("if (a.Length > 0 && (a[0] == \"restore\" || a[0] == \"default\"))")];
        Assert.Contains("SeatAdjust.ParseZ(a)", z); Assert.Contains("SeatAdjust.PlanZ(dz, st0)", z);   // the tested planner, not a second one
        Assert.Contains("await SeatAdjustPress(seat, zd, pr, zcts.Token)", z);                        // the presses it lays out, one by one
        Assert.Contains("NOT saved", z);
        foreach (var never in new[] { "[SAVE]", "CorrectionZ", "SeatAdjustRun(", "PostHover", "SetHover" }) Assert.DoesNotContain(never, z);   // no save, no measurement, no full check, no hover
    }
    [Fact] public void UnsavedLine_NamesFurnitureAndPose()
    {
        var l = SeatAdjust.UnsavedLine("Mirage bed", "texting", -0.04, "no Save button");
        Assert.StartsWith("UNSAVED 'Mirage bed' texting", l); Assert.Contains("-0.04", l); Assert.Contains("no Save button", l);
    }
}
