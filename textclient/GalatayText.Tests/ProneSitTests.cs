using System;
using System.IO;
using System.Linq;
using GalatayText;
using Xunit;
// 2026-10-27: VAMOSITG27 is belly down (chest, forearms and thighs on the floor). The hip-contact check set +0.138 and she floated in David's viewer; 0.0 looked slightly too low,
// +0.02 looked good. Prone poses are their own class: a fixed hover, not driven by the hip contact, never saved.
public class ProneSitTests
{
    static string Root() { var d = new DirectoryInfo(System.AppContext.BaseDirectory); while (d != null && !Directory.Exists(Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent; return d!.FullName; }
    static string Src(string f) => File.ReadAllText(Path.Combine(Root(), "textclient/galatay-text", f));

    [Fact] public void OnlyTheListedProneSits_HaveAFixedHover()
    {
        Assert.Equal(0.02, Program.ProneHoverTarget("sit", "VAMOSITG27"));
        Assert.Equal(0.02, Program.ProneHoverTarget("sit", " vamositg27 "));
        Assert.Null(Program.ProneHoverTarget("sit", "VAMOSITG13")); Assert.Null(Program.ProneHoverTarget("sit", "VAMOSITG12"));
        Assert.Null(Program.ProneHoverTarget("sit", "sit")); Assert.Null(Program.ProneHoverTarget("sit", null));
        Assert.Null(Program.ProneHoverTarget("stand", "VAMOSITG27")); Assert.Null(Program.ProneHoverTarget("seat", "VAMOSITG27"));
        Assert.DoesNotContain("VAMOSITG27", Program.HandSupportedSits.Keys);   // a separate class: no hip-contact lift
    }
    [Fact] public void TheHipContactNumber_NeverDrivesAPronePose()
    {
        // the bug: hips floating ~0.138 above the floor in a prone pose read as a gap of +0.138 -> hover set +0.138. Now a fix only compares the hover in force to the fixed value
        Assert.True(Program.ProneHoverNeedsFix(0.138, 0.02));   // the floating value David saw is corrected to +0.02
        Assert.True(Program.ProneHoverNeedsFix(0.0, 0.02));     // 0.0 looked slightly too low
        Assert.True(Program.ProneHoverNeedsFix(0.06, 0.02));    // a hover left over from another pose
        Assert.True(Program.ProneHoverNeedsFix(-0.01, 0.02));
        Assert.False(Program.ProneHoverNeedsFix(0.02, 0.02)); Assert.False(Program.ProneHoverNeedsFix(0.025, 0.02)); Assert.False(Program.ProneHoverNeedsFix(0.012, 0.02));   // within 1 cm: left alone
    }
    [Fact] public void Wiring_FixedValue_OneRender_NothingSaved_AndAnImplausibleHipGapIsNotAProblem()
    {
        var ph = Src("PoseHover.cs"); var chk = ph[ph.IndexOf("static async Task<string> PoseHoverCheck(")..];
        Assert.Contains("ProneHoverTarget(kind, pose)", chk);
        Assert.Contains("if (proneT == null && (g1 is not double || !PoseGapSane(gap)))", chk);      // the implausible-gap / furniture path does not run for a prone pose
        int guard = chk.IndexOf("PoseRenderMismatch("), prone = chk.IndexOf("if (proneT is double pt) return await ProneHoverCheck("), conf = chk.IndexOf("HoverFixNeedsConfirm(eg)");
        Assert.True(guard > 0 && guard < prone && prone < conf);                                     // the render still proves the pose first; the prone branch precedes every gap-driven fix
        var h = ph[ph.IndexOf("static async Task<string> ProneHoverCheck(")..]; h = h[..h.IndexOf("// seated on an object")];
        Assert.Equal(0, System.Text.RegularExpressions.Regex.Matches(h, @"await HoverCheckRender\(").Count);   // render 1 only: no verification render
        Assert.Contains("renders {renders}", h); Assert.Contains("int renders = 1;", h);
        Assert.Contains("PostHover(pt,", h); Assert.DoesNotContain("PoseHoverFromGap", h); Assert.DoesNotContain("File.Write", h);
        Assert.Equal(1, Program.HoverCheckRenderCount(false));
    }
}
