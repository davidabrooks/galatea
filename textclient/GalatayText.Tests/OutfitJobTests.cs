using GalatayText;
using Xunit;

// 2026-11-23 David: outfit_wear for 'Android' blocked ~55 s and the MCP call timed out (-32001) although the swap worked.
// It now starts a job (OutfitJob.cs) and reports progress in status outfit_change.
public class OutfitJobTests
{
    static Program.OutfitJobState J(string state) => new(3, "Android", state, new DateTime(2026, 11, 23, 17, 3, 20), state is "done" or "failed" ? new DateTime(2026, 11, 23, 17, 4, 24) : null, null);

    [Fact] public void Running_states_block_a_second_start_and_name_the_running_job()
    {
        foreach (var s in new[] { "starting", "walking", "swapping", "post-steps" })
        {
            var r = Program.OutfitJobStartBlock(J(s), false);
            Assert.NotNull(r);
            Assert.StartsWith("refused", r);
            Assert.Contains("'Android' (job 3)", r);
            Assert.Contains(s, r);
        }
    }

    [Fact] public void Finished_jobs_do_not_block_but_another_swap_does()
    {
        Assert.Null(Program.OutfitJobStartBlock(J("done"), false));
        Assert.Null(Program.OutfitJobStartBlock(J("failed"), false));
        Assert.Null(Program.OutfitJobStartBlock(null, false));
        Assert.NotNull(Program.OutfitJobStartBlock(null, true));   // a daily pick / bikini swap is running
    }

    [Fact] public void Failure_detection_matches_the_reports()
    {
        Assert.True(Program.OutfitJobFailed("no outfit 'Androd' under My Outfits"));
        Assert.True(Program.OutfitJobFailed("not logged in"));
        Assert.True(Program.OutfitJobFailed(""));
        Assert.True(Program.OutfitJobFailed("wore 'Android': attach failed for 2 items"));
        Assert.False(Program.OutfitJobFailed("wore outfit 'Android' safely: 12 off, 30 on, 4 layers off, 5 layers on, 4 body parts\n  'HUD': HUD step skipped (same items)"));
    }

    [Fact] public void Summary_is_short_and_keeps_the_key_line()
    {
        var s = Program.OutfitJobSummary("bedroom first (outfit Android): walked to the bedroom\nwore outfit 'Android' safely: 12 off, 30 on\n  'X HUD': picked Pink");
        Assert.Contains("wore outfit 'Android' safely", s);
        Assert.Contains("bedroom first", s);
        Assert.DoesNotContain("Pink", s);
        Assert.True(Program.OutfitJobSummary(new string('x', 1000)).Length <= 301);
    }

    [Fact] public void Status_object_has_the_fields()
    {
        var o = J("done") with { Summary = "wore outfit 'Android' safely" };
        var json = System.Text.Json.JsonSerializer.Serialize(o.ToStatus());
        foreach (var k in new[] { "\"name\":\"Android\"", "\"state\":\"done\"", "\"started\":\"2026-11-23 17:03:20\"", "\"finished\":\"2026-11-23 17:04:24\"", "\"summary\":", "\"seconds\":64" })
            Assert.Contains(k, json);
        Assert.True(J("swapping").Running);
        Assert.False(J("done").Running);
    }
}
