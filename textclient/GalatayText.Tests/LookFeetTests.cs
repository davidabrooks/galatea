using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using GalatayText;
using Xunit;
// 2026-10-26: `look` feet / contact close-up (David: "zooming in closer helps judging the hover"), one render per hover check, and the connector
// error on `look {cam_margin: 0.15, cam_height: 0.3}` (JSON numbers for string parameters: "An error occurred invoking look").
public class LookFeetTests
{
    static string Root() { var d = new DirectoryInfo(System.AppContext.BaseDirectory); while (d != null && !Directory.Exists(Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent; return d!.FullName; }
    static string Src(string f) => File.ReadAllText(Path.Combine(Root(), "textclient/galatay-text", f));
    static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Theory]
    [InlineData("feet", "feet")] [InlineData("contact", "feet")] [InlineData("side,feet", "left,feet")] [InlineData("FEET", "feet")] [InlineData("all", "front,back,left,right")] [InlineData("feet,feet", "feet")]
    public void LookSelfViews_AcceptsFeet(string arg, string expect) => Assert.Equal(expect, Program.LookSelfViews(arg));
    [Theory] [InlineData("toes")] [InlineData("feetx")] [InlineData("feet,bogus")] public void LookSelfViews_RefusesOtherWords(string arg) => Assert.Null(Program.LookSelfViews(arg));

    [Fact] public void ZoomOptionIsPassedToLookPy()
    {
        var (rest, opts, pre) = Program.LookOptions(new[] { "self", "side", "zoom=feet", "cam-margin=0.15", "preset=hover-ground" });
        Assert.Equal(new[] { "self", "side" }, rest); Assert.Contains("--zoom=feet", opts); Assert.Contains("--cam-margin=0.15", opts); Assert.Equal("hover-ground", pre);
    }

    // ---- the connector error ----
    [Fact] public void JsonNumbersAndBooleans_AreAcceptedForTheLookOptions()
    {
        Assert.Equal("0.15", Program.McpOpt(J("0.15"))); Assert.Equal("0.3", Program.McpOpt(J("0.3"))); Assert.Equal("0.15", Program.McpOpt(J("\"0.15\"")));
        Assert.Equal("35", Program.McpOpt(J("35"))); Assert.Equal("", Program.McpOpt(J("null"))); Assert.Equal("", Program.McpOpt(null));
        Assert.True(Program.McpFlag(J("true"), false)); Assert.True(Program.McpFlag(J("\"yes\""), false)); Assert.False(Program.McpFlag(J("false"), true)); Assert.False(Program.McpFlag(J("0"), true));
        Assert.True(Program.McpFlag(null, true)); Assert.False(Program.McpFlag(null, false)); Assert.True(Program.McpFlag(J("1"), false));
    }
    [Fact] public void TheFailedCall_BuildsTheRightCommandLine()
    {
        // {target: self, views: side, preset: hover-ground, cam_margin: 0.15, cam_height: 0.3}
        var line = Program.McpLookLine("self", "side", true, false, new[] { ("preset", "hover-ground"), ("cam-margin", Program.McpOpt(J("0.15"))), ("cam-height", Program.McpOpt(J("0.3"))) });
        Assert.Equal("look self side fast preset=hover-ground cam-margin=0.15 cam-height=0.3", line);
        Assert.Equal("look self feet fast zoom=feet", Program.McpLookLine("self", "feet", true, false, new[] { ("zoom", "feet") }));
    }
    [Fact] public void TheLookTool_TakesNumbersAsNumbers_AndNeverThrowsToTheHost()
    {
        var m = File.ReadAllText(Path.Combine(Root(), "textclient/galatay-mcp/McpProgram.cs")); var look = m[m.IndexOf("public static async Task<string> Look(")..]; look = look[..look.IndexOf("[McpServerTool(Name = \"seat_info\"")];
        foreach (var n in new[] { "cam_margin", "cam_lens", "cam_fov", "cam_height", "scale", "samples", "jpeg", "exposure", "small", "fast", "far" })
            Assert.Contains($"JsonElement? {n} = null", look);            // a number or a bool binds; a `string` parameter refused it before the method ran
        Assert.DoesNotContain("string cam_margin", look); Assert.Contains("string zoom", look);
        Assert.Contains("catch (Exception e) { return Json(new { ok = false, result = \"look failed: \"", look);   // an error is a message, not "An error occurred invoking look"
    }

    // ---- one render per hover check ----
    [Fact] public void HoverCheck_CostsOneRender_TwoOnlyToVerifyAFix()
    {
        Assert.Equal(1, Hover.HoverCheckRenderCount(false)); Assert.Equal(2, Hover.HoverCheckRenderCount(true));
        var ph = Src("PoseHover.cs");
        // exactly one blender launch per HoverCheckRender, one view (look_scope.hovercheck_view: the close-up INSTEAD of the wide side view)
        var rend = ph[ph.IndexOf("async Task<(double? gap, string img)> HoverCheckRender(")..]; rend = rend[..rend.IndexOf("internal static bool FormCertain")];
        Assert.Equal(1, Regex.Matches(rend, @"Process\.Start\(").Count); Assert.DoesNotContain("GT_VIEW", rend);
        // the check: one render always, a second only inside the fix branch, after the POST, to verify; the close-up is never a third
        var chk = ph[ph.IndexOf("async Task<string> PoseHoverCheck(")..];
        Assert.Equal(3, Regex.Matches(chk, @"await HoverCheckRender\(").Count);      // render 1, the confirm render of a LARGE fix only (> 0.12 m), the verification after the fix
        int fixAt = chk.IndexOf("await h.PostHover(t,"), second = chk.IndexOf("await HoverCheckRender(", fixAt);
        Assert.True(fixAt > 0 && second > fixAt); Assert.Equal(2, Regex.Matches(chk[..fixAt], @"await HoverCheckRender\(").Count);
        var big = chk[chk.IndexOf("if (HoverFixNeedsConfirm(eg))")..fixAt]; Assert.Equal(1, Regex.Matches(big, @"await HoverCheckRender\(").Count);   // the extra render sits only inside the large-fix branch
        Assert.Contains("HoverCheckRenderCount(fixedHover, confirmRender)", chk);
        Assert.DoesNotContain("HoverCheckRequest(", chk);                       // nothing re-requests a check (no render loop)
        var cal = Src("HoverCalibrate.cs"); Assert.Equal(2, Regex.Matches(cal, @"await HoverCheckRender\(").Count);   // calibrate: measure + confirm, only when David asks
    }
    [Fact] public void ManualHoverCheck_WaitsAndReturnsTheCloseUpPathAndTheRenderCount()
    {
        var hg = Src("HeightGuard.cs"); var chk = hg[hg.IndexOf("if (a[0] == \"check\")")..];
        Assert.Contains("var t = hoverSvc.HoverCheckRequest(\"manual\", 500);", chk); Assert.Contains("await t", chk);
        var ph = Src("PoseHover.cs");
        Assert.Contains("renders {renders}; close-up of {HoverContactLabel(kind)}: {finalImg}", ph);    // the result line names the render count and the image path
        Assert.Contains("hoverCheckResult = ", ph);
    }
    [Fact] public void ContactLabel_NamesWhatTheCloseUpFrames()
    {
        Assert.Contains("feet", Hover.HoverContactLabel("stand")); Assert.Contains("hips", Hover.HoverContactLabel("sit")); Assert.Equal("lowest contact", Hover.HoverContactLabel("unknown"));
        Assert.Contains("ContactEnv(kind)", Src("Look.cs"));
    }
}
