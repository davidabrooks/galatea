using System;
using System.Collections.Generic;
using System.IO;
using GalatayText;
using Xunit;
// 2026-11-22 dark-widen (David 11-03: "when a close-up is too dark, ZOOM OUT - step the camera back to a wider view and judge from that"):
// the wide retake (level 1) is unchanged; while it is still too dark the client steps back to GT_WIDEN 2 and 3 (render_mesh.widen_width).
public class SeatAdjustWidenTests
{
    static string Root() { var d = new DirectoryInfo(AppContext.BaseDirectory); while (d != null && !Directory.Exists(Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent; return d!.FullName; }
    static SeatAdjust.Quality Q(double dark, double over = 0.05) => new(20.0, 21.0, true, new() { ["hips"] = 10 }, new() { ["hips"] = 20.5 }, over, dark);
    static readonly SeatAdjust.SupportReading Sit = new("sitting", new[] { "hips" }, 0.02, new());

    [Fact] public void A_too_dark_gate_steps_back_one_level_at_a_time_up_to_the_cap()
    {
        var dark = SeatAdjust.GateFail(Sit, Q(0.9));
        Assert.StartsWith(SeatAdjust.DarkGatePrefix, dark);                 // the prefix is the gate's own wording
        Assert.Equal(2, SeatAdjust.NextWidenLevel(1, dark));
        Assert.Equal(3, SeatAdjust.NextWidenLevel(2, dark));
        Assert.Equal(0, SeatAdjust.NextWidenLevel(3, dark));                // the cap: stop, nothing pressed
        Assert.Equal(3, SeatAdjust.MaxWidenLevel);
    }

    [Fact] public void Other_gate_failures_stop_at_the_wide_retake_as_before()
    {
        var over = SeatAdjust.GateFail(Sit, Q(0.0, over: 0.5));
        Assert.Contains("overexposed", over);
        Assert.Equal(0, SeatAdjust.NextWidenLevel(1, over));
        Assert.Equal(0, SeatAdjust.NextWidenLevel(1, "no furniture surface under her hips in the render"));
        Assert.Equal(0, SeatAdjust.NextWidenLevel(1, null));                // passed: no retake
        Assert.Equal(0, SeatAdjust.NextWidenLevel(0, SeatAdjust.GateFail(Sit, Q(0.9))));   // the first close-up goes to the wide retake, not here
        Assert.Null(SeatAdjust.GateFail(Sit, Q(0.1)));
    }

    [Fact] public void Render_env_per_level()
    {
        Assert.Null(SeatAdjust.WidenEnv(0));
        Assert.Equal(new Dictionary<string, string> { ["GT_SUPPORT_WIDE"] = "1", ["GT_WIDEN"] = "1" }, SeatAdjust.WidenEnv(1));   // the old wide retake + level 1 = 1.6-3.0 m
        Assert.Equal("2", SeatAdjust.WidenEnv(2)["GT_WIDEN"]);
        Assert.Equal("3", SeatAdjust.WidenEnv(9)["GT_WIDEN"]);
        Assert.Equal("wide retake", SeatAdjust.WidenLabel(1));
        Assert.Equal("wider retake (level 3)", SeatAdjust.WidenLabel(3));
    }

    [Fact] public void The_gated_render_and_SupportRender_are_wired_to_the_levels()
    {
        var run = File.ReadAllText(Path.Combine(Root(), "textclient/galatay-text/SeatAdjustRun.cs"));
        var g = run[run.IndexOf("SeatAdjustGatedRender(bool waterSeat")..]; g = g[..g.IndexOf("internal static async Task<string> SeatAdjustRun(")];
        Assert.Contains("hoverSvc.SupportRender(widen: level)", g);
        Assert.Contains("SeatAdjust.NextWidenLevel(level, g2)", g);
        Assert.Contains("for (int level = 1; level > 0; )", g);             // starts at the unchanged wide retake
        var ph = File.ReadAllText(Path.Combine(Root(), "textclient/galatay-text/PoseHover.cs"));
        Assert.Contains("SupportRender(int widen = 0)", ph);
        Assert.Contains("extraEnv: SeatAdjust.WidenEnv(widen)", ph);
        // the renderer reads the same variable the client sets, through the validating parser
        var rm = File.ReadAllText(Path.Combine(Root(), "vision/cloud-render/render_mesh.py"));
        Assert.Contains("widen_level(os.environ.get(\"GT_WIDEN\"), wide_)", rm);
        Assert.Contains("WIDEN_MAX_LEVEL = " + SeatAdjust.MaxWidenLevel, rm);
    }
}
