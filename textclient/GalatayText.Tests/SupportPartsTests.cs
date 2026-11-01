using System.IO;
using GalatayText;
using Xunit;
// 2026-10-31 (David: "don't use hardcoded adjustments"; "an average height across all possible touchpoints"): the client has no pose-name contact table,
// no per-pose part list and no aim; it moves the hover by the renderer's gap, which is the touchpoint average (render_mesh.py touch_contact).
public class SupportPartsTests
{
    static string Src(string f) { var d = new DirectoryInfo(System.AppContext.BaseDirectory); while (!Directory.Exists(Path.Combine(d!.FullName, "textclient/galatay-text"))) d = d.Parent; return File.ReadAllText(Path.Combine(d.FullName, "textclient/galatay-text", f)); }
    [Fact] public void NoNamedPoseContactTables()
    {
        var s = Src("PoseHover.cs");
        Assert.DoesNotContain("SupportPartsOfPose", s); Assert.DoesNotContain("GroundSitParts", s); Assert.DoesNotContain("HandsAimM", s); Assert.DoesNotContain("SupportParts(", Src("Look.cs"));
    }
    // David's 20:37 photo (hover -0.030): the touchpoint average rendered +0.046 above the floor (hips floating, heel down) -> -0.076
    [Fact] public void Photo20261031_2037_TheTouchpointGapDrivesTheHover() => Assert.Equal(-0.076, Hover.PoseHoverFromGap(-0.030, 0.046), 3);
}
public class NoPoseBiasTests
{
    static string Src(string f) { var d = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory); while (!System.IO.Directory.Exists(System.IO.Path.Combine(d!.FullName, "textclient/galatay-text"))) d = d.Parent; return System.IO.File.ReadAllText(System.IO.Path.Combine(d.FullName, "textclient/galatay-text", f)); }
    // 2026-10-31 David: no hardcoded adjustments. The per-posture render bias (named-pose defaults, pose-hover.json bias keys, `hover calibrate`) is gone.
    [Xunit.Fact] public void NoRenderBiasAnywhere()
    {
        var s = Src("PoseHover.cs") + Src("Look.cs") + Src("McpSurface.cs") + Src("HeightGuard.cs");
        foreach (var w in new[] { "PoseBiasFor", "PoseBiasDefault", "BiasDefault", "PoseBiasNums", "GT_HOVER_BIAS =", "\"GT_HOVER_BIAS\"]", "HoverCalibrate(" }) Xunit.Assert.DoesNotContain(w, s);
        Xunit.Assert.DoesNotContain("GT_HOVER_BIAS", GalatayText.Hover.HoverCheckEnv(1.57, 0.0).Keys);
    }
}
