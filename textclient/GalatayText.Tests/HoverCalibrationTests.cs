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
        var chk = Src("PoseHover.cs"); chk = chk[chk.IndexOf("async Task<string> PoseHoverCheck(")..];
        Assert.Contains("var (eff2, src2) = await h.EffectiveHoverFresh();", chk);
        Assert.Contains("Math.Abs(e2 - t) <= 0.002", chk);                                // the cap must now hold the target
        Assert.Contains("HoverCheckRender(dir, size, eff2 ?? t, kind, parts: parts)", chk);             // the re-render is drawn at the hover really in force
        Assert.DoesNotContain("HoverCheckRender(dir, size, t, bias, kind", chk);
        Assert.Contains("NOT verified: the hover in force is", chk);
        Assert.DoesNotContain("(default bias)", chk);                                      // 2026-10-31 #392: no per-pose bias in the render, so none is claimed
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(chk, @"PostHover\(").Count);
    }

    [Fact] public void HoverApply_PostsWithoutSaving_AndSetStillPins()
    {
        var hg = Src("HeightGuard.cs"); var ap = hg[hg.IndexOf("a[0] == \"apply\"")..]; ap = ap[..ap.IndexOf("return \"usage: hover")];
        Assert.Contains("PostHover(av2", ap); Assert.DoesNotContain("File.WriteAllText(HoverFile", ap); Assert.DoesNotContain("rlvHoverOverride", ap);
        Assert.Equal(0.5, Program.HoverApplyMax);
    }
}
