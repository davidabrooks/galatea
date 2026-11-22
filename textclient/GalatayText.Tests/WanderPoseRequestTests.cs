using System;
using System.IO;
using GalatayText;
using Xunit;

// 2026-11-22 (fixes list, Nov 20 11:21-11:26 AM PT): "change pose babe" paused the wander and cut the tub sit short; the resume then walked a
// whole leg undressed before dressing. A pose request keeps the wander and the sit; an undressed resume dresses at the seat first.
public class WanderPoseRequestTests
{
    static string Src(string f)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, "textclient/galatay-text", f));
    }

    [Theory]
    [InlineData("change pose babe", true)]
    [InlineData("Change your pose, babe", true)]
    [InlineData("try another position?", true)]
    [InlineData("switch poses", true)]
    [InlineData("can you do a different pose", true)]
    [InlineData("new pose please", true)]
    [InlineData("pose change", true)]
    [InlineData("don't change pose", false)]
    [InlineData("no, don't switch positions", false)]
    [InlineData("I love this pose", false)]
    [InlineData("keep that position babe", false)]
    [InlineData("how was your day?", false)]
    [InlineData("come here", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void PoseChangeRequest_IsRecognised(string text, bool yes) => Assert.Equal(yes, Program.IsPoseChangeRequest(text));

    [Fact] public void OnlyDavid_OnlySeated_OnlyInARunningUnheldWander_KeepsTheWanderGoing()
    {
        Assert.True(Program.PoseRequestKeepsWander(true, true, null, true, "change pose babe"));
        Assert.True(Program.PoseRequestKeepsWander(true, true, "chat", true, "change pose babe"));      // already chatting: still no new pause
        Assert.False(Program.PoseRequestKeepsWander(true, false, null, true, "change pose babe"));     // walking: the normal chat pause
        Assert.False(Program.PoseRequestKeepsWander(false, true, null, true, "change pose babe"));     // wander off
        Assert.False(Program.PoseRequestKeepsWander(true, true, null, false, "change pose babe"));     // someone else
        Assert.False(Program.PoseRequestKeepsWander(true, true, "hold", true, "change pose babe"));    // a hold owns her
        Assert.False(Program.PoseRequestKeepsWander(true, true, null, true, "how was your day?"));     // ordinary chat still pauses
    }

    [Fact] public void TheSitChangesPose_OnlyWhenFreeToDoSo()
    {
        Assert.True(Program.SitChangesPoseOnRequest(true, true, false, false));
        Assert.False(Program.SitChangesPoseOnRequest(false, true, false, false));
        Assert.False(Program.SitChangesPoseOnRequest(true, false, false, false));
        Assert.False(Program.SitChangesPoseOnRequest(true, true, true, false));
        Assert.False(Program.SitChangesPoseOnRequest(true, true, false, true));   // a seat adjustment holds the pose: wait for it
    }

    [Fact] public void AnUndressedResume_DressesAtTheSeatFirst()
    {
        Assert.True(Program.RedressBeforeLeg(true, true, null));
        Assert.False(Program.RedressBeforeLeg(true, true, "chat"));    // still paused: stays seated
        Assert.False(Program.RedressBeforeLeg(true, false, null));     // already standing: the loop's own standing re-dress handles it
        Assert.False(Program.RedressBeforeLeg(false, true, null));
    }

    [Fact] public void TheWanderWiresIt_NoPauseForARequest_BothSitLoops_RedressBeforeTheLeg()
    {
        var w = Src("Wander.cs");
        var hook = w.IndexOf("if (PoseRequestKeepsWander(WanderOn, client.Self.SittingOn != 0, wanderPause, id == DavidId, text))", StringComparison.Ordinal);
        var pause = w.IndexOf("SetPause(\"chat\",", StringComparison.Ordinal);
        Assert.True(hook > 0 && hook < pause);                                   // decided before the chat pause is set
        Assert.Equal(2, CountOf(w, "bool asked = TakePoseChangeRequest(client.Self.SittingOn != 0, wanderPause != null, SeatAdjustBusy);"));
        Assert.Equal(2, CountOf(w, "if (!asked) changed = true;"));            // the timed rotation and the stay are untouched by a request
        Assert.Equal(2, CountOf(w, "wPoseChangeAsked = false;   // a request from before this sit is not for it"));
        var loop = w.IndexOf("static async Task HomeWanderLoop(", StringComparison.Ordinal);
        var redress = w.IndexOf("if (RedressBeforeLeg(pendingDressOutfit != null || pendingToilet != null, client.Self.SittingOn != 0, wanderPause))", loop, StringComparison.Ordinal);
        var leg = w.IndexOf("var r = await HomeRandomSit(g, ct);", loop, StringComparison.Ordinal);
        var walk = w.IndexOf("(ok, msg) = await FollowPoly(poly, o, legCts.Token);", loop, StringComparison.Ordinal);
        Assert.True(redress > loop && redress < leg && redress < walk);
        Assert.Contains("await EnsureStandingForWalk(ct);\n                await DressAfterSeatIfPending();", w);
    }
    static int CountOf(string s, string x) { int n = 0, i = 0; while ((i = s.IndexOf(x, i, StringComparison.Ordinal)) >= 0) { n++; i += x.Length; } return n; }
}
