using System.Collections.Generic;
using System.IO;
using System.Linq;
using GalatayText;
using Xunit;
// 2026-10-26 (David): the hover check's render must show the hover that is really in force, and its floor/avatar offset must be calibratable from
// what he sees in his own viewer ("don't trust numbers, rely on visible confirmation, you need a way to calibrate that").
public class HoverCalibrationTests
{
    static string Root() { var d = new DirectoryInfo(System.AppContext.BaseDirectory); while (d != null && !Directory.Exists(Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent; return d!.FullName; }
    static string Src(string f) => File.ReadAllText(Path.Combine(Root(), "textclient/galatay-text", f));

    // ---- A: the hover in force ----
    [Fact] public void HoverInForce_PrefersTheCapAnswer_NotOurOwnStaleAppearance()
    {
        // 2026-10-26: AgentPreferences held -0.186 (David's viewer: sunk) while our own appearance said 0: the old check drew 0
        Assert.Equal((-0.186, "AgentPreferences GET"), Program.EffectiveHoverPick(-0.186, 0.0, 0.0));
        Assert.Equal(-0.186, Program.EffectiveHoverPick(null, -0.186, 0.0).v);        // no GET: the value the cap answered to our POST
        Assert.Equal(0.0, Program.EffectiveHoverPick(null, null, 0.0).v);              // last resort only
        Assert.Null(Program.EffectiveHoverPick(null, null, null).v);
    }
    [Fact] public void EveryHoverReader_UsesTheHoverInForce_NotTheStaleOwnAppearance()
    {
        foreach (var f in new[] { "PoseHover.cs", "Look.cs", "AutoHover.cs", "IdlePose.cs", "RlvAdjustHeight.cs" }) Assert.DoesNotContain("SimHover()", Src(f));
        Assert.Contains("hoverInForce = backV ?? v", Src("HeightGuard.cs"));            // every successful POST records what the cap answered
        Assert.Contains("await EffectiveHoverFresh()", Src("Look.cs"));                  // `look self` after a fix shows the fix
    }
    [Fact] public void HoverGet_SaysWhichValueTheViewersApply_AndCallsTheOwnAppearanceStale()
    {
        var l = Program.HoverGetLine(0, -0.186, "AgentPreferences GET", 0);
        Assert.Contains("hover in force -0.186", l); Assert.Contains("own appearance says 0 (stale, ignored)", l); Assert.DoesNotContain("sim applies", l);
    }
    [Fact] public void Check_VerifiesTheFixAfterwards_AndSaysNotVerifiedWhenItCannot()
    {
        var chk = Src("PoseHover.cs"); chk = chk[chk.IndexOf("static async Task<string> PoseHoverCheck(")..];
        Assert.Contains("var (eff2, src2) = await EffectiveHoverFresh();", chk);
        Assert.Contains("Math.Abs(e2 - t) <= 0.002", chk);                                // the cap must now hold the target
        Assert.Contains("HoverCheckRender(dir, size, eff2 ?? t, bias, kind, parts: parts)", chk);             // the re-render is drawn at the hover really in force
        Assert.DoesNotContain("HoverCheckRender(dir, size, t, bias, kind", chk);
        Assert.Contains("NOT verified: the hover in force is", chk);
        Assert.Contains("NOT verified against David's viewer (default bias)", chk);       // a default (uncalibrated) bias is never called "confirmed"
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(chk, @"PostHover\(").Count);
    }

    // ---- B: calibration ----
    [Fact] public void CalibratedBias_MakesTheRenderGapEqualWhatDavidSees()
    {
        // render said +0.185 m at bias -0.055; David sees her sunk 6 cm (-6): bias' = -0.055 + (0.185 - -0.06) = 0.19
        var nb = Program.CalibratedBias(-0.055, 0.185, -6);
        Assert.Equal(0.19, nb, 4);
        // the render gap is (gap0 - bias): at the new bias it is 0.185 + (-0.055 - 0.19) = -0.06 = what he sees
        Assert.Equal(-0.06, 0.185 + (-0.055 - nb), 4);
        Assert.Equal(-0.055, Program.CalibratedBias(-0.055, 0.0, 0), 4);                  // he sees contact and so does the render: unchanged
        Assert.Equal(0.2315 + 0.04, Program.CalibratedBias(0.2315, 0.04, 0), 4);          // render +4 cm, he sees contact: bias absorbs the 4 cm
    }
    [Theory]
    [InlineData("stand 4", "set", "stand", 4.0)] [InlineData("sit -3.5", "set", "sit", -3.5)] [InlineData("here 0", "set", "here", 0.0)] [InlineData("SIT 2cm", "set", "sit", 2.0)]
    [InlineData("stand 2 cm", "set", "stand", 2.0)] [InlineData("show", "show", null, 0.0)] [InlineData("reset sit", "reset", "sit", 0.0)] [InlineData("reset here", "reset", "here", 0.0)]
    public void CalibrateParse_Accepts(string args, string action, string target, double cm)
    {
        var r = Program.HoverCalibrateParse(args.Split(' '));
        Assert.True(r.ok, r.err); Assert.Equal(action, r.action); Assert.Equal(target, r.target); Assert.Equal(cm, r.cm, 3);
    }
    [Theory] [InlineData("")] [InlineData("stand")] [InlineData("stand abc")] [InlineData("stand 31")] [InlineData("stand -100")] [InlineData("kneel 3")] [InlineData("reset")] [InlineData("reset kneel")] [InlineData("stand 1 2 3")] [InlineData("stand NaN")]
    public void CalibrateParse_Refuses(string args)
    {
        var r = Program.HoverCalibrateParse(args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries));
        Assert.False(r.ok); Assert.False(string.IsNullOrEmpty(r.err));
    }
    [Fact] public void CalibrationKey_FollowsThePostureSheIsReallyIn()
    {
        var none = new Dictionary<string, double>();
        Assert.Equal("bias|android|stand", Program.CalibrationKey("stand", true, "stand", "stand", none).key);
        Assert.Equal("bias|android|sit", Program.CalibrationKey("sit", true, "sit", "VAMOSITG21", none).key);
        Assert.Equal("bias|android|VAMOSITG21", Program.CalibrationKey("here", true, "sit", "VAMOSITG21", none).key);
        Assert.NotNull(Program.CalibrationKey("sit", true, "stand", "stand", none).err);          // standing: cannot calibrate the sit
        Assert.NotNull(Program.CalibrationKey("stand", true, "sit", "VAMOSITG21", none).err);
        Assert.NotNull(Program.CalibrationKey("stand", true, "unknown", "unknown", none).err);     // unclassified AO animation: nothing is calibrated on it
        Assert.NotNull(Program.CalibrationKey("here", true, "seat", "x", none).err);
        var shadow = new Dictionary<string, double> { ["bias|android|VAMOSITG22"] = 0.137 };
        Assert.NotNull(Program.CalibrationKey("sit", true, "sit", "VAMOSITG22", shadow).err);      // a per-pose bias would hide the new sit bias
        Assert.Equal("bias|android|VAMOSITG22", Program.CalibrationKey("here", true, "sit", "VAMOSITG22", shadow).key);
    }
    [Fact] public void CalibratedFlag_IsOnlyTrueForABiasDavidSet()
    {
        var nums = new Dictionary<string, double> { ["bias|android|sit"] = 0.1 };
        Assert.True(Program.BiasCalibrated(nums, true, "sit", "VAMOSITG21"));
        Assert.False(Program.BiasCalibrated(nums, true, "stand", "stand"));                        // the stand default is only a first guess
        Assert.False(Program.BiasCalibrated(nums, false, "sit", "VAMOSITG21"));                    // other form
        Assert.True(Program.BiasCalibrated(new Dictionary<string, double> { ["bias|android|VAMOSITG21"] = 0.0 }, true, "sit", "VAMOSITG21"));
        Assert.False(Program.BiasCalibrated(null, true, "stand", "stand"));
    }
    [Fact] public void BiasMerge_KeepsOtherKeys_AndRemovesOnReset()
    {
        var raw = new Dictionary<string, double> { ["bias|android|sit"] = -0.055, ["bias|android|stand"] = 0.2315 };
        var m = Program.PoseBiasMerge(raw, "bias|android|sit", 0.123456);
        Assert.Equal(0.1235, m["bias|android|sit"], 4); Assert.Equal(0.2315, m["bias|android|stand"], 4); Assert.Equal(-0.055, raw["bias|android|sit"], 4);   // input untouched
        var r = Program.PoseBiasMerge(m, "bias|android|sit", null);
        Assert.False(r.ContainsKey("bias|android|sit")); Assert.True(r.ContainsKey("bias|android|stand"));
        Assert.Equal(raw.Count, Program.PoseBiasMerge(null, "x", 1).Count + raw.Count - 1);
    }
    [Fact] public void Calibrate_ChangesOnlyTheBiasFile_NeverHoverNorPosture()
    {
        var s = Src("HoverCalibrate.cs");
        Assert.DoesNotContain("PostHover(", s); Assert.DoesNotContain("Self.Stand", s); Assert.DoesNotContain("RequestSit", s); Assert.DoesNotContain("AoGroundSit", s);
        Assert.Contains("EffectiveHoverFresh()", s);                       // measured at the hover in force
        Assert.Contains("HoverCheckReady(", s);                            // not while she moves / is leashed / mid pose change
    }
    [Fact] public void HoverApply_PostsWithoutSaving_AndSetStillPins()
    {
        var hg = Src("HeightGuard.cs"); var ap = hg[hg.IndexOf("a[0] == \"apply\"")..]; ap = ap[..ap.IndexOf("a[0] == \"calibrate\"")];
        Assert.Contains("PostHover(av2", ap); Assert.DoesNotContain("File.WriteAllText(HoverFile", ap); Assert.DoesNotContain("rlvHoverOverride", ap);
        Assert.Equal(0.5, Program.HoverApplyMax);
    }
    [Fact] public void RenderPrintsItsBiasAndTheViewerFormulaGap_ForCalibration()
    {
        var py = File.ReadAllText(Path.Combine(Root(), "vision/cloud-render/render_mesh.py"));
        Assert.Contains("def hover_check_detail(", py); Assert.Contains("GT: hover check detail:", py); Assert.Contains("GT_HOVER_CAL", py);
        Assert.Contains("GT_HOVER_CAL", Src("Look.cs"));
        // the gap line the client parses is unchanged and still comes first
        Assert.Contains("GT: hover check gap {HC[1]:+.3f} m (hover", py);
    }
}
