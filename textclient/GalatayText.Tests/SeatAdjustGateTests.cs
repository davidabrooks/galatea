using System;
using System.Collections.Generic;
using System.Linq;
using GalatayText;
using Xunit;
// 2026-11-02 17:12 tub 'shave' (floating high, overexposed close-up with no tub in it, nudged and SAVED) and 17:14 makeup table (stale pose label, wrong menu page)
// 2026-11-04 (David: "just save your adjustments anyway ... you will always be checking them"): an APPLIED nudge is saved whatever the re-render reads.
public class SeatAdjustGateTests
{
    static SeatAdjust.SupportReading R(string kind, double gap, params string[] bear) => new(kind, bear, gap, new());
    const string GoodQ = "GT: support quality: frame z 20.000..20.600; seat found; hits shins 12, feet 9, hips 10, thighs 8; surface shins 20.150, feet 20.140, hips 20.300, thighs 20.310\nGT: render quality: overexposed 0.090; dark 0.100; contrast 0.300";
    // the tub render: the seat top under her hips not found, nothing under the shins in frame, 46 % blown out
    const string TubQ = "GT: support quality: frame z 21.050..21.550; seat not found; hits shins 3, feet 3; surface shins 20.900, feet 20.900\nGT: render quality: overexposed 0.460; dark 0.000; contrast 0.208";

    [Fact] public void ParseQuality_ReadsFrameHitsSurfaceExposure()
    {
        var q = SeatAdjust.ParseQuality(GoodQ);
        Assert.Equal(20.0, q.FrameLo); Assert.Equal(20.6, q.FrameHi); Assert.True(q.SeatFound);
        Assert.Equal(12, q.Hits["shins"]); Assert.Equal(20.15, q.SurfaceZ["shins"]); Assert.Equal(0.09, q.Overexposed);
        Assert.Null(SeatAdjust.ParseQuality("nothing").FrameLo);
    }
    [Fact] public void Gate_TubRenderIsRejected()
    {
        var why = SeatAdjust.GateFail(R("kneeling", 0.077, "shins", "feet"), SeatAdjust.ParseQuality(TubQ));
        Assert.NotNull(why); Assert.Contains("overexposed", why);
        var noExposure = SeatAdjust.GateFail(R("kneeling", 0.077, "shins", "feet"), SeatAdjust.ParseQuality(TubQ.Replace("0.460", "0.050")));
        Assert.Contains("hips", noExposure);   // seat not found
        var q = SeatAdjust.ParseQuality(TubQ.Replace("0.460", "0.050").Replace("seat not found", "seat found"));
        Assert.Contains("not in the frame", SeatAdjust.GateFail(R("kneeling", 0.077, "shins", "feet"), q));
    }
    [Fact] public void Gate_GoodRenderPasses_HugeGapAndFewHitsDoNot()
    {
        var q = SeatAdjust.ParseQuality(GoodQ);
        Assert.Null(SeatAdjust.GateFail(R("kneeling", 0.04, "shins", "feet"), q));
        Assert.Contains("beyond any fit", SeatAdjust.GateFail(R("kneeling", 0.40, "shins", "feet"), q));
        Assert.Contains("ray hits", SeatAdjust.GateFail(R("kneeling", 0.04, "shins", "knees"), q));
        Assert.NotNull(SeatAdjust.GateFail(R("kneeling", 0.04, "shins"), null));
    }
    [Fact] public void SaveConfidence_SavesEveryAppliedNudge_AndSaysWhatTheOldRuleThought()
    {
        var q = SeatAdjust.ParseQuality(GoodQ); var bad = SeatAdjust.ParseQuality(TubQ);
        Assert.Equal(SeatAdjust.SaveCall.Save, SeatAdjust.SaveConfidence(R("kneeling", 0.077, "shins", "feet"), q, R("kneeling", -0.004, "shins", "feet"), q).call);
        // the tub: an untrusted first render never leads to a Save (nothing else may stand on it)
        Assert.NotEqual(SeatAdjust.SaveCall.Save, SeatAdjust.SaveConfidence(R("kneeling", 0.077, "shins", "feet"), bad, R("kneeling", -0.014, "shins", "feet"), bad).call);
        // 2026-11-04 David ("just save your adjustments anyway"): these three used to stay UNSAVED; now they save and the reason is only the log line
        var reRenderUnconfirmed = SeatAdjust.SaveConfidence(R("kneeling", 0.077, "shins", "feet"), q, R("kneeling", -0.004, "shins", "feet"), bad);
        Assert.Equal(SeatAdjust.SaveCall.Save, reRenderUnconfirmed.call);
        Assert.Contains("not good enough to confirm", reRenderUnconfirmed.why);
        var bearingChanged = SeatAdjust.SaveConfidence(R("kneeling", 0.077, "shins", "feet"), q, R("sitting", -0.004, "hips", "thighs"), q);
        Assert.Equal(SeatAdjust.SaveCall.Save, bearingChanged.call);
        Assert.Contains("reads differently after the nudge", bearingChanged.why);       // hips+thighs -> hips+thighs+hands, in the log
        Assert.Contains("kneeling on shins+feet", bearingChanged.why); Assert.Contains("sitting on hips+thighs", bearingChanged.why);
        var outOfBand = SeatAdjust.SaveConfidence(R("kneeling", 0.12, "shins", "feet"), q, R("kneeling", 0.05, "shins", "feet"), q);
        Assert.Equal(SeatAdjust.SaveCall.Save, outOfBand.call);
        Assert.Contains("outside the contact band", outOfBand.why);
        // a nudge the re-render shows to be WORSE is still undone
        Assert.Equal(SeatAdjust.SaveCall.Revert, SeatAdjust.SaveConfidence(R("kneeling", 0.05, "shins", "feet"), q, R("kneeling", 0.09, "shins", "feet"), q).call);
        Assert.Contains("worse than before", SeatAdjust.SaveConfidence(R("kneeling", 0.05, "shins", "feet"), q, R("kneeling", 0.09, "shins", "feet"), q).why);
    }
    [Fact] public void CannotSave_IsTheOnlyThingThatStopsASave_AndNeverHowTheRenderReads()
    {
        var q = SeatAdjust.ParseQuality(GoodQ);
        // a render that reads differently is NOT a reason to skip the save any more
        Assert.Null(SeatAdjust.CannotSaveWhy(17, onSameSeat: true, menuAlive: true));
        // she was stood up / the seat changed mid-run: the menu went with it
        var stoodUp = SeatAdjust.CannotSaveWhy(17, onSameSeat: false, menuAlive: true);
        Assert.Contains("stood up", stoodUp);
        Assert.Equal(SeatAdjust.SaveCall.CannotSave, SeatAdjust.SaveDecision(R("kneeling", 0.077, "shins", "feet"), q, R("kneeling", -0.004, "shins", "feet"), q, 17, false, true).call);
        // the menu was lost / the presses got no answer
        var menuLost = SeatAdjust.CannotSaveWhy(17, onSameSeat: true, menuAlive: false);
        Assert.Contains("menu was lost", menuLost);
        Assert.Equal(SeatAdjust.SaveCall.CannotSave, SeatAdjust.SaveDecision(R("kneeling", 0.077, "shins", "feet"), q, R("sitting", -0.004, "hips", "thighs"), q, 17, true, false).call);
        // no press was applied: nothing to save
        var nothing = SeatAdjust.CannotSaveWhy(0, onSameSeat: true, menuAlive: true);
        Assert.Contains("no presses", nothing);
        var none = SeatAdjust.SaveDecision(R("kneeling", 0.077, "shins", "feet"), q, R("kneeling", 0.077, "shins", "feet"), q, 0, true, true);
        Assert.Equal(SeatAdjust.SaveCall.CannotSave, none.call); Assert.Contains("no presses", none.why);
        // a furniture with no Save button of its own (the generic path)
        Assert.Contains("no Save button", SeatAdjust.CannotSaveWhy(3, onSameSeat: true, menuAlive: true, saveButton: false));
        // the standing-up case is reported first: it is the one that explains the missing menu
        Assert.Contains("stood up", SeatAdjust.CannotSaveWhy(3, onSameSeat: false, menuAlive: false, saveButton: false));
    }
    [Fact] public void Bugged_FloatingHighOverTheTubFromObjectData()
    {
        var tub = SeatAdjust.LinksetZ(new[] { (20.35, SeatAdjust.HalfZ(1.8, 0.9, 0.7, 0, 0, 0, 1)), (20.6, SeatAdjust.HalfZ(1.6, 0.7, 0.05, 0, 0, 0, 1)) });   // tub body + water plane
        Assert.Equal(20.0, tub.Bottom, 6); Assert.Equal(20.7, tub.Top, 6);
        Assert.Contains("floating", SeatAdjust.BuggedReason(22.3, 1.75, tub, R("kneeling", 0.077, "shins", "feet"), true));
        Assert.Null(SeatAdjust.BuggedReason(21.0, 1.75, tub, R("sitting", 0.01, "hips"), true));        // sitting in it: fine
        Assert.Contains("buried", SeatAdjust.BuggedReason(17.0, 1.75, tub, null, true));
        Assert.Contains("animation", SeatAdjust.BuggedReason(21.0, 1.75, tub, null, false));
        Assert.Contains("beyond any fit", SeatAdjust.BuggedReason(21.0, 1.75, tub, R("lying", -0.40, "chest"), true));
        Assert.Null(SeatAdjust.LinksetZ(Array.Empty<(double, double)>()));
        Assert.Equal(0.9, SeatAdjust.HalfZ(1.8, 0.9, 0.7, 0, Math.Sin(Math.PI / 4), 0, Math.Cos(Math.PI / 4)), 6);   // turned 90 deg about y: its x length stands up
    }
    [Fact] public void PoseLabel_OnlyForThisSeat()
    {
        var last = "17:12:07 'Dutchie clawfoot bath tub adult' 53f8929b-0abc-6e12-fac9-f9d6f9bfe6bc: Solo* > shave [solo]";
        Assert.Equal("Solo* > shave", SeatAdjust.PoseLabelFor(last, "53f8929b-0abc-6e12-fac9-f9d6f9bfe6bc"));
        Assert.Equal("(default pose)", SeatAdjust.PoseLabelFor(last, "71321f3c-1316-f031-a701-5b655dadd27c"));
        Assert.Equal("Solo* > Masturbate* > Jilling", SeatAdjust.PoseLabelFor("17:14:46 'Xplicit Makeup Table (c,m)' 71321f3c-1316-f031-a701-5b655dadd27c: Solo* > Masturbate* > Jilling (mid-sit change) [solo]", "71321f3c-1316-f031-a701-5b655dadd27c"));
    }
    [Fact] public void Nav_BacksOutOfASubmenuToReachAdjust()
    {
        // 17:14:36: the touch re-showed the Masturbate* page (no [ADJUST]) and the check gave up
        Assert.Equal("[BACK]", SeatAdjust.NavPress("AVsitter™2.2\n[1.Female] [Legs Up]", new List<string> { "Quietly", "Read", "Clit Play", "[BACK]", "Jiller", "Jilling" }));
        Assert.Equal("[ADJUST]", SeatAdjust.NavPress("AVsitter™2.2", new List<string> { "Features*", "[ADJUST]", "[SWAP]", "Solo*" }));
        Assert.Equal("[POSE]", SeatAdjust.NavPress("AVsitter™ 2.1\nAdjust:", new List<string> { "[WATER]", "[POSE]", "[BACK]" }));
        Assert.Null(SeatAdjust.NavPress("hi", new List<string> { "A", "B" }));
    }
    [Fact] public void BugList_AddsPerFurnitureKeepsOtherKeys_AndRulesSkipIt()
    {
        var j = PoseBugList.Add("{\"not_solo_names\":[\"Peeking\"]}", "Dutchie clawfoot bath tub adult", "shave", "Solo* > shave", "floating", new DateTime(2026, 11, 2, 17, 12, 0));
        j = PoseBugList.Add(j, "Dutchie clawfoot bath tub adult", "shave", "Solo* > shave", "again", DateTime.Now);
        var r = PoseRuleSet.FromJson(j);
        Assert.True(r.IsBugged("Dutchie clawfoot bath tub adult", "shave")); Assert.False(r.IsBugged("Other tub", "shave"));
        Assert.True(r.IsNotSoloName("Peeking")); Assert.Equal(1, r.BuggedCount);
    }
    [Fact] public void Restore_ParsesTheUndoOfTheTubSave()
    {
        Assert.True(SeatAdjust.ParseRestore(new[] { "restore", "+0.08" }).ok);
        Assert.True(SeatAdjust.ParseRestore(new[] { "default" }).ok);
        Assert.False(SeatAdjust.ParseRestore(new[] { "restore" }).ok);
        Assert.False(SeatAdjust.ParseRestore(new[] { "restore", "5" }).ok);
    }
}
