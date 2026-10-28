using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GalatayText;
using Xunit;
// 2026-10-27 (David): the hover check looks at the ground the way he does. The ONE close-up render reports the gap under every body part against the floor drawn in it;
// the AO pose name only chooses which parts support her (names, never numbers); the hover goes where the lowest supporting part touches (hands never below the floor).
public class SupportPartsTests
{
    static string Root() { var d = new DirectoryInfo(System.AppContext.BaseDirectory); while (d != null && !Directory.Exists(Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent; return d!.FullName; }
    static string Src(string f) => File.ReadAllText(Path.Combine(Root(), "textclient/galatay-text", f));
    static Dictionary<string, double> Gaps(double hov, double floorUnderHips, double hips, double hands, double chest = 0.3, double thighs = 0.2, double forearms = 0.15)
        // gaps the render would print at hover `hov` on a floor where the body parts rest `hips`/`hands`/... above it at hover 0: every gap moves 1:1 with the hover; floorUnderHips shifts the whole floor
        => new() { ["hips"] = hips + hov - floorUnderHips, ["hands"] = hands + hov - floorUnderHips, ["chest"] = chest + hov - floorUnderHips, ["thighs"] = thighs + hov - floorUnderHips, ["forearms"] = forearms + hov - floorUnderHips };
    // what the check does: one render at the hover in force, then the target
    static double Target(string pose, Dictionary<string, double> gaps, double hov)
    {
        var parts = Hover.SupportParts("sit", pose); double basis = Hover.SupportGap(parts, gaps)!.Value, aim = Hover.SupportAim(parts);
        return Hover.PoseHoverFromGap(hov, basis - aim);
    }

    [Fact] public void TheRenderLineIsParsed_AndTheLowestSupportingPartDrivesTheFix()
    {
        var g = Hover.HoverCheckPartGaps("noise\nGT: hover check parts: hips +0.012, chest +0.315, forearms +0.140, hands -0.040, thighs +0.233\n/x.jpg");
        Assert.Equal(5, g.Count); Assert.Equal(-0.040, g["hands"]); Assert.Equal(0.012, g["hips"]);
        Assert.Equal(-0.040, Hover.SupportGap(new[] { "hips", "hands" }, g));            // hands sunk 4 cm: they drive, the hips float 5 cm
        Assert.Equal(0.233, Hover.SupportGap(new[] { "thighs" }, g)); Assert.Null(Hover.SupportGap(new[] { "shins" }, g)); Assert.Null(Hover.SupportGap(null, g));
        Assert.Empty(Hover.HoverCheckPartGaps("GT: hover check gap +0.1 m")); Assert.Empty(Hover.HoverCheckPartGaps(null));
    }
    [Fact] public void PoseNames_OnlyChooseWhichPartsSupport_NeverNumbers()
    {
        Assert.Equal(new[] { "hips", "hands" }, Hover.SupportParts("sit", "VAMOSITG13")); Assert.Equal(new[] { "hips", "hands" }, Hover.SupportParts("sit", " vamositg11 "));
        Assert.Equal(new[] { "hips", "hands" }, Hover.SupportParts("sit", "VAMOSITG12")); Assert.Equal(new[] { "chest", "forearms", "thighs" }, Hover.SupportParts("sit", "VAMOSITG27"));
        Assert.Null(Hover.SupportParts("sit", "VAMOSITG23")); Assert.Null(Hover.SupportParts("sit", null)); Assert.Null(Hover.SupportParts("stand", "VAMOSITG13")); Assert.Null(Hover.SupportParts("seat", "VAMOSITG27"));
        Assert.All(Hover.SupportPartsOfPose.Values.SelectMany(v => v), p => Assert.Contains(p, Hover.BodyParts));   // part names only
        var ph = Src("PoseHover.cs"); var table = ph[ph.IndexOf("SupportPartsOfPose = new")..]; table = table[..table.IndexOf("};")];
        Assert.DoesNotMatch(@"[+-]?\d+\.\d+", table.Replace("VAMOSITG", ""));   // no hover number anywhere in the table
        foreach (var gone in new[] { "HandSupportLiftM", "ContactClass", "FixedHover", "ProneSits", "HandSupportLift(" }) Assert.DoesNotContain(gone, ph);
    }
    [Fact] public void HandsAreNeverLeftBelowTheFloor_AndHipsAreLiftedOnlyAsMuchAsNeeded()
    {
        // G13-like: the hips touch (0) while the hands are 5 cm sunk at the old hover: the hover rises 5 cm + the aim so the hands show, the hips float the same amount
        var g = Gaps(0, 0, 0.0, -0.05);
        double t = Target("VAMOSITG13", g, 0);
        Assert.Equal(0.05 + Hover.HandsAimM, t, 3);
        var after = Gaps(t, 0, 0.0, -0.05);
        Assert.True(after["hands"] >= 0); Assert.Equal(Hover.HandsAimM, after["hands"], 3); Assert.Equal(0.05 + Hover.HandsAimM, after["hips"], 3);
        Assert.True(Hover.SupportConfirmed(Hover.SupportGap(new[] { "hips", "hands" }, after)!.Value - Hover.HandsAimM, Hover.HandsAimM));
        // G11-like: the hands are LOWER than the hips' contact the other way round: hips float 0.119 above the floor, the hands touch: the hands drive (the lowest supporting part), the hips are never lifted to meet them
        var g11 = Gaps(0, 0, 0.119, 0.0); double t11 = Target("VAMOSITG11", g11, 0);
        Assert.Equal(Hover.HandsAimM, t11, 3);   // the hands touch (gap 0) and aim 8 mm above the floor; the hips (+0.119) are not lifted at all for them
    }
    [Fact] public void BellyDown_UsesChestForearmsThighs_NotTheHips()
    {
        // hips float 0.138 above the floor while chest / forearms / thighs rest on it: the hips do not count
        var g = new Dictionary<string, double> { ["hips"] = 0.138, ["hands"] = 0.2, ["chest"] = 0.0, ["thighs"] = 0.0, ["forearms"] = 0.0 };
        Assert.Equal(0.0, Hover.SupportGap(Hover.SupportParts("sit", "VAMOSITG27"), g)!.Value, 3);
        Assert.Equal(0.138, Target("VAMOSITG27", g, 0.138), 3);                                  // already touching: nothing to fix
        var floating = new Dictionary<string, double> { ["hips"] = 0.30, ["hands"] = 0.3, ["chest"] = 0.12, ["thighs"] = 0.118, ["forearms"] = 0.15 };   // the lowest supporting part is 11.8 cm up: lower by that
        Assert.Equal(0.138 - 0.118, Target("VAMOSITG27", floating, 0.138), 3);
        Assert.True(Hover.SupportConfirmed(0.004, 0.0)); Assert.False(Hover.SupportConfirmed(0.03, 0.0));
    }
    [Fact] public void TheSameRuleOnAnotherFloor_NoAbsoluteNumbers()
    {
        // the floor is 0.20 m higher here (relative to where the avatar is drawn): every measured gap is 0.20 smaller, so the target hover is 0.20 higher; nothing is remembered between floors
        foreach (var pose in new[] { "VAMOSITG13", "VAMOSITG11", "VAMOSITG27" })
        {
            var low = Gaps(0, 0, 0.02, 0.0, chest: 0.0, thighs: 0.01, forearms: 0.02); var high = Gaps(0, 0.20, 0.02, 0.0, chest: 0.0, thighs: 0.01, forearms: 0.02);
            Assert.Equal(Target(pose, low, 0) + 0.20, Target(pose, high, 0), 3);
        }
        Assert.Equal(0.0, Hover.SupportAim(new[] { "chest", "thighs" })); Assert.Equal(Hover.HandsAimM, Hover.SupportAim(new[] { "hips", "hands" })); Assert.Equal(0.0, Hover.SupportAim(null));
    }
    [Fact] public void AnAutomaticPoseChange_IsAlwaysRemeasured_NeverTrustsTheOtherPosesHover()
    {
        Assert.True(Hover.PoseChangedSinceCheck("VAMOSITG13", "VAMOSITG12")); Assert.False(Hover.PoseChangedSinceCheck("VAMOSITG12", " vamositg12 ")); Assert.False(Hover.PoseChangedSinceCheck(null, "VAMOSITG12"));
        Assert.False(Hover.HoverCheckReady("sit", 0f, false, false, false, 99, 5, false).go);                          // 5 s after the last check: the cooldown holds
        Assert.True(Hover.HoverCheckReady("sit", 0f, false, false, false, 99, 5, false, poseChanged: true).go);       // ... unless the pose changed: remeasured at once
        var chk = Src("PoseHover.cs"); chk = chk[chk.IndexOf("async Task<string> PoseHoverCheck(")..];
        Assert.True(chk.IndexOf("h.EffectiveHoverFresh()") < chk.IndexOf("SupportConfirmed(eg, aim)"));                   // the hover in force is read fresh, then judged by what the render measures
        Assert.Contains("PoseChangedSinceCheck(hoverLastCheckedPose, pose)", chk); Assert.Contains("PoseChangedSinceCheck(hoverLastCheckedPose, pose));", Src("PoseHover.cs"));
    }
    [Fact] public void Wiring_OneRender_PartsReachTheRenderer_NothingSaved_RenderCountReported()
    {
        var env = Hover.HoverCheckEnv(1.6, 0.0, -0.055, new[] { "hips", "hands" }); Assert.Equal("hips,hands", env["GT_HC_PARTS"]); Assert.DoesNotContain("GT_HC_PARTS", Hover.HoverCheckEnv(1.6, 0.0, -0.055).Keys);
        var chk = Src("PoseHover.cs"); chk = chk[chk.IndexOf("async Task<string> PoseHoverCheck(")..];
        Assert.Contains("HoverCheckRender(dir, size, hov, bias, kind, parts: parts)", chk); Assert.Contains("HoverCheckRender(dir, size, eff2 ?? t, bias, kind, parts: parts)", chk);
        Assert.Contains("renders {renders}", chk); Assert.Equal(1, Hover.HoverCheckRenderCount(false)); Assert.Equal(2, Hover.HoverCheckRenderCount(true));
        Assert.Contains("SupportParts(kind, pose)", Src("Look.cs"));
        Assert.DoesNotContain("File.Write", chk);
    }
}
