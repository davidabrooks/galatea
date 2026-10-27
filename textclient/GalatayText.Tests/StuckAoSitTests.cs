using System;
using System.IO;
using System.Linq;
using GalatayText;
using LibreMetaverse;
using Xunit;
// 2026-10-27 09:34: after a relog the AO came back with its force sit ON and 985d3be4 (a VAMOSITG ground sit) playing. The id was in the learned STAND list (the AO starts the held
// sit right after login, while she is "plainly standing"), so the status said "standing", the login hover check retried against a sitting render and the walk went on 10 s too early.
public class StuckAoSitTests
{
    static string Root() { var d = new DirectoryInfo(System.AppContext.BaseDirectory); while (d != null && !Directory.Exists(Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent; return d!.FullName; }
    static string Src(string f) => File.ReadAllText(Path.Combine(Root(), "textclient/galatay-text", f));
    static readonly UUID Held = new("985d3be4-2b64-1123-c503-8dfa2eb083c6"), AoStand = new("be2dba76-6004-28cb-29e4-68f73d54f13b"), Stand2 = new("2af3a656-1f26-02af-d7f2-38d9a0f49f68");

    [Fact] public void TheHeldSit985d3be4_IsASeededSit_SoItIsASitWhateverTheLearnedFileSays()
    {
        Assert.Contains(Held, Program.AoSitSeed);
        Assert.True(Program.AoSitPlaying(new[] { Held }, false, Program.AoSitSeed));
        var s = Program.AoRealState(true, new[] { "985d3be4" }, false, false);
        Assert.Contains("AO sit playing (985d3be4)", s); Assert.DoesNotContain("standing (", s);
        var ao = Src("AoSit.cs");
        Assert.Contains("aoStandIds.Remove(u); aoSitIds.Add(u);", ao);   // the seed beats a stand entry of the learned file
        Assert.Contains("|| AoSitSeed.Contains(id)) return;", ao);        // and it can never be learned as a stand again
    }
    [Fact] public void Release_OnlyWhenTheRenderSaysSitting_WhileTheClientSaysStanding_InTheLoginWindow_OnceOnly()
    {
        const string sit = "the render shows her pelvis only 0.32 m high: sitting, not standing";
        Assert.True(Program.StuckSitReleaseOk("stand", sit, "login", 40, 0));
        Assert.True(Program.StuckSitReleaseOk("stand", sit, "stand", 40, 0));
        Assert.False(Program.StuckSitReleaseOk("stand", sit, "login", 40, 1));         // never a second press (the toggle turns force sit ON when it is off)
        Assert.False(Program.StuckSitReleaseOk("stand", sit, "login", 400, 0));        // not long after login
        Assert.False(Program.StuckSitReleaseOk("stand", sit, "manual", 40, 0));
        Assert.False(Program.StuckSitReleaseOk("stand", sit, "sit", 40, 0));
        Assert.False(Program.StuckSitReleaseOk("sit", sit, "login", 40, 0));           // she is believed to sit: the render agrees
        Assert.False(Program.StuckSitReleaseOk("stand", null, "login", 40, 0));        // the render shows a stand
        Assert.False(Program.StuckSitReleaseOk("stand", "the export holds an AO sit animation but I think she stands", "login", 40, 0));   // the other mismatch is not this one
    }
    [Fact] public void ReleaseLearnsTheHeldSit_OnlyWhenItWasTheOneAoAnimation_AndAClassifiedStandPlaysNow()
    {
        Assert.Equal(new[] { Held }, Program.StuckSitLearn(new[] { Held }, new[] { Animations.STAND, Stand2 }, new[] { Stand2 }));
        Assert.Empty(Program.StuckSitLearn(new[] { Held }, new[] { Held, Stand2 }, new[] { Stand2 }));                 // still playing: nothing learned
        Assert.Empty(Program.StuckSitLearn(new[] { Held }, new[] { Animations.STAND }, new[] { Stand2 }));             // no classified stand after: not proof
        Assert.Empty(Program.StuckSitLearn(new[] { Held, AoStand }, new[] { Stand2 }, new[] { Stand2, AoStand }));     // two candidates: a rotating stand must never become a sit
        Assert.Empty(Program.StuckSitLearn(new UUID[0], new[] { Stand2 }, new[] { Stand2 }));
        Assert.Empty(Program.StuckSitLearn(null, new[] { Stand2 }, new[] { Stand2 }));
    }
    [Fact] public void ReleaseText_GivesUpAfterTheOnePress()
    {
        Assert.Contains("giving up", Program.StuckSitReleaseText(1, true)); Assert.Contains("never pressed again", Program.StuckSitReleaseText(1, true));
        Assert.Contains("released", Program.StuckSitReleaseText(1, false));
    }
    [Fact] public void Wiring_OnePressPerLogin_ThroughTheExistingToggle_NeverStopAnimations()
    {
        var ao = Src("AoSit.cs"); var rel = ao[ao.IndexOf("static async Task<string> StuckSitRelease(")..]; rel = rel[..rel.IndexOf("// pure (tested): the real sit/stand state")];
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(rel, @"AoGroundSitToggle\("));      // one press, no loop
        Assert.DoesNotContain("while", rel); Assert.DoesNotContain("for (", rel); Assert.DoesNotContain("StopAnim", rel); Assert.DoesNotContain("stop_animations", rel);
        Assert.Contains("Task.Delay((int)StuckSitWaitMs)", rel); Assert.Equal(11000, Program.StuckSitWaitMs);
        Assert.Contains("StuckSitNewLogin();", Src("LoginJoin.cs"));                                       // a new login re-arms the single press
        var ph = Src("PoseHover.cs"); var chk = ph[ph.IndexOf("static async Task<string> PoseHoverCheck(")..];
        Assert.Contains("StuckSitReleaseOk(kind, why, requestWhy,", chk);
        Assert.Contains("PoseHoverCheck(kind, pose, why)", ph);                                          // the request's reason reaches the check
        Assert.True(chk.IndexOf("StuckSitReleaseOk(") < chk.IndexOf("return \"pose not rendered: \""));  // the retry reason still goes back to the request loop (it re-runs the check)
    }
}
