using System;
using System.Collections.Generic;
using System.IO;
using GalatayText;
using Xunit;
// 2026-11-22 (David: "Cap every retake at 1 m, then rotate 180"; "tell me in world when you are unable to measure so we can troubleshoot")
public class SeatAdjustRetakeTests
{
    static string Root() { var d = new DirectoryInfo(AppContext.BaseDirectory); while (d != null && !Directory.Exists(Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent; return d!.FullName; }
    static SeatAdjust.Quality Q(double dark, double over = 0.05) => new(20.0, 21.0, true, new() { ["hips"] = 10 }, new() { ["hips"] = 20.5 }, over, dark);
    static readonly SeatAdjust.SupportReading Sit = new("sitting", new[] { "hips" }, 0.02, new());

    [Fact] public void A_too_dark_wide_retake_gets_one_opposite_side_retake_then_stops()
    {
        var dark = SeatAdjust.GateFail(Sit, Q(0.9));
        Assert.StartsWith(SeatAdjust.DarkGatePrefix, dark);                 // the prefix is the gate's own wording
        Assert.Equal(2, SeatAdjust.NextRetake(1, dark));                    // wide retake too dark -> the opposite side
        Assert.Equal(0, SeatAdjust.NextRetake(2, dark));                    // still too dark -> stop: unmeasurable
        Assert.Equal(0, SeatAdjust.NextRetake(0, dark));                    // the first close-up goes to the wide retake, not here
        Assert.Equal(2, SeatAdjust.MaxRetake);
    }

    [Fact] public void Other_gate_failures_stop_at_the_wide_retake()
    {
        var over = SeatAdjust.GateFail(Sit, Q(0.0, over: 0.5));
        Assert.Contains("overexposed", over);
        Assert.Equal(0, SeatAdjust.NextRetake(1, over));
        Assert.Equal(0, SeatAdjust.NextRetake(1, "no furniture surface under her hips in the render"));
        Assert.Equal(0, SeatAdjust.NextRetake(1, null));
    }

    [Fact] public void Render_env_per_retake()
    {
        Assert.Null(SeatAdjust.RetakeEnv(0));
        Assert.Equal(new Dictionary<string, string> { ["GT_SUPPORT_WIDE"] = "1" }, SeatAdjust.RetakeEnv(1));
        Assert.Equal(new Dictionary<string, string> { ["GT_SUPPORT_WIDE"] = "1", ["GT_RETAKE_TURN"] = "180" }, SeatAdjust.RetakeEnv(2));
        Assert.Equal("wide retake", SeatAdjust.RetakeLabel(1));
        Assert.Equal("opposite-side retake (180)", SeatAdjust.RetakeLabel(2));
    }

    [Fact] public void Unmeasurable_im_is_short_and_in_her_voice()
    {
        var trail = "the close-up is too dark (72 %); wide retake: the close-up is too dark (70 %); opposite-side retake (180): the close-up is too dark (81 %)";
        Assert.Equal("the close-up is too dark (81 %)", SeatAdjust.ShortReason(trail));
        Assert.Equal("I couldn't measure my seat on Lalou sofa / chill 2: the close-up is too dark (81 %). Want to troubleshoot?",
            SeatAdjust.UnmeasurableIm("Lalou sofa", "Solo* > chill 2", trail));
        Assert.Equal("I couldn't measure my seat on this seat: the render was not usable. Want to troubleshoot?", SeatAdjust.UnmeasurableIm("", "(default)", null));
        Assert.True(SeatAdjust.UnmeasurableIm("x", "y", new string('a', 400)).Length < 260);
    }

    [Fact] public void Only_failed_render_reverts_count_as_unmeasurable()
    {
        Assert.NotNull(SeatAdjust.UnmeasurableFromSave(SeatAdjust.SaveCall.Revert, "the re-render is not good enough to confirm it, so the nudge is undone, never saved: x"));
        Assert.NotNull(SeatAdjust.UnmeasurableFromSave(SeatAdjust.SaveCall.Revert, "the first render was not good enough: x"));
        Assert.Null(SeatAdjust.UnmeasurableFromSave(SeatAdjust.SaveCall.Revert, "the re-render is worse than before"));
        Assert.Null(SeatAdjust.UnmeasurableFromSave(SeatAdjust.SaveCall.Save, "confident"));
    }

    [Fact] public void One_im_per_sit_and_pose_and_only_while_David_is_online()
    {
        var set = new HashSet<string>();
        Assert.Equal((false, "David is offline"), SeatAdjust.UnmeasurableNotifyDecision(set, "12|a", false));
        Assert.True(SeatAdjust.UnmeasurableNotifyDecision(set, "12|a", true).send);
        set.Add("12|a");
        Assert.False(SeatAdjust.UnmeasurableNotifyDecision(set, "12|a", true).send);
        Assert.True(SeatAdjust.UnmeasurableNotifyDecision(set, "12|b", true).send);   // another pose on the same sit
    }

    [Fact] public void Wired_into_the_run()
    {
        var run = File.ReadAllText(Path.Combine(Root(), "textclient/galatay-text/SeatAdjustRun.cs"));
        var g = run[run.IndexOf("SeatAdjustGatedRender(bool waterSeat")..]; g = g[..g.IndexOf("internal static async Task<string> SeatAdjustRun(")];
        Assert.Contains("hoverSvc.SupportRender(retake: retake)", g);
        Assert.Contains("SeatAdjust.NextRetake(retake, g2)", g);
        Assert.Contains("SeatUnmeasurable(seat, seatName, pose, suspect)", run);                // water seat, misread pose kind
        Assert.Contains("SeatUnmeasurable(seat, seatName, pose, g0)", run);                     // the gate (too dark after 180, no surface ...)
        Assert.Equal(2, run.Split("SeatAdjust.UnmeasurableFromSave(call, why)").Length - 1);    // both the AVsitter and the generic path
        Assert.Contains("seatUnmeasNotified.Clear()", run);                                     // a new sit may IM again
        Assert.Contains("client.Self.InstantMessage(DavidAgent, line)", run);
        var ph = File.ReadAllText(Path.Combine(Root(), "textclient/galatay-text/PoseHover.cs"));
        Assert.Contains("extraEnv: SeatAdjust.RetakeEnv(retake)", ph);
        var rm = File.ReadAllText(Path.Combine(Root(), "vision/cloud-render/render_mesh.py"));
        Assert.Contains("GT_RETAKE_TURN", rm); Assert.DoesNotContain("GT_WIDEN", rm);
    }
}
