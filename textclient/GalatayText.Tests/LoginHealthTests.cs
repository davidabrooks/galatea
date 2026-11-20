using System;
using GalatayText;
using Xunit;

public class LoginHealthTests
{
    [Fact] public void AutoRelogin_HonoursDeliberateStop_AndNeverOverlapsAManualLogin()   // 2026-10-31 13:52 PT tangle
    {
        Assert.Equal("go", Program.EmbeddedReloginDecision(false, false, 3, 3, false));
        Assert.StartsWith("skip: deliberate-stop", Program.EmbeddedReloginDecision(true, false, 3, 3, false));
        Assert.StartsWith("skip: a manual login", Program.EmbeddedReloginDecision(false, false, 3, 4, false));
        Assert.StartsWith("skip: already logged in", Program.EmbeddedReloginDecision(false, true, 3, 3, false));
        // 2026-11-20 10:18:34: "another login is running" used to end the retries for good; now it waits and checks again
        Assert.StartsWith("wait: another login", Program.EmbeddedReloginDecision(false, false, 3, 3, true));
        Assert.StartsWith("skip: deliberate-stop", Program.EmbeddedReloginDecision(true, false, 3, 3, true));   // deliberate-stop still wins
        Assert.StartsWith("skip: a manual login", Program.EmbeddedReloginDecision(false, false, 3, 4, true));
    }
    [Fact] public void ManualLoginOrLogout_CancelsThePendingAutoRelogin()
    {
        long scheduled = Program.CancelPendingRelogin("test: disconnect seen");
        long after = Program.CancelPendingRelogin("test: manual login");
        Assert.NotEqual("go", Program.EmbeddedReloginDecision(false, false, scheduled, after, false));
    }
    [Fact] public void LoginAsync_IsSerialised_AndWiredToTheHealthCheck()
    {
        var d = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory); while (d != null && !System.IO.Directory.Exists(System.IO.Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent; var root = d!.FullName;
        var prog = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "textclient/galatay-text/Program.cs"));
        var rr = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "textclient/galatay-text/RegionRestart.cs"));
        Assert.Contains("loginGate.WaitAsync(0)", prog);
        Assert.Contains("LoginHealthCheck(healthGen)", prog);
        Assert.Contains("CancelPendingRelogin(\"manual login\")", prog);
        Assert.Contains("CancelPendingRelogin(\"logout: \"", prog);
        Assert.Contains("EmbeddedReloginDecision(DeliberateStopRequested()", rr);
    }
    [Fact] public void FakedEmptyRegionSession_IsAFailedLogin()   // 13:52 (region '', 0 objects, no caps) and 13:57 (seed 404)
    {
        Assert.NotNull(Program.LoginHealthProblem("", 0, false, false));
        Assert.NotNull(Program.LoginHealthProblem("Peronaut", 2074, false, false));   // region but no caps
        Assert.Null(Program.LoginHealthProblem("Peronaut", 2074, true, true));
        Assert.Null(Program.LoginHealthProblem("Peronaut", 0, true, true));            // empty sky region with caps is fine
        // 14:12 regression: no UploadBakedTexture (legacy, unused by server-side baking) must not fail a healthy login
        Assert.Null(Program.LoginHealthProblem("Peronaut", 823, true, false));
        Assert.NotNull(Program.LoginHealthProblem("Peronaut", 823, false, true));
        Program.LoginFailedReason = Program.LoginHealthProblem("", 0, false, false);
        try
        {
            Assert.Contains("login FAILED", Program.LoginBrokenStop("collar"));
            Assert.Contains("region name unknown", Program.LoginBrokenStop("evox"));
        }
        finally { Program.LoginFailedReason = null; }
        Assert.Null(Program.LoginBrokenStop("collar"));
    }
    [Fact] public void FailedLogin_RecoversOnce_OnlyWithoutDeliberateStop()
    {
        Assert.True(Program.LoginRecoveryAllowed(false, false, 5, 5));
        Assert.False(Program.LoginRecoveryAllowed(true, false, 5, 5));
        Assert.False(Program.LoginRecoveryAllowed(false, true, 5, 5));
        Assert.False(Program.LoginRecoveryAllowed(false, false, 5, 6));
    }
    [Fact] public void AllWornNamesUnknown_StopsTheCollarStep()
    {
        Assert.True(Program.WornNamesUnresolved("no single worn attachment matches; worn: "));
        Assert.True(Program.WornNamesUnresolved("no single worn attachment matches; worn: ?, ?, ?"));
        Assert.False(Program.WornNamesUnresolved("no single worn attachment matches; worn: ?, N° 9 Premium Collar"));
        Assert.False(Program.WornNamesUnresolved("touched 'N° 9 Premium Collar'"));
    }

    // ---- 2026-11-20 10:17:14-10:18:34: a BUSY manual login cancelled the pending auto-relogin and nothing retried ----
    static long ReloginGen() => (long)typeof(Program).GetField("reloginGen", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
    static System.Threading.SemaphoreSlim LoginGate() => (System.Threading.SemaphoreSlim)typeof(Program).GetField("loginGate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
    [Fact] public async System.Threading.Tasks.Task BusyManualLogin_CancelsNothing()
    {
        var gate = LoginGate();
        Assert.True(await gate.WaitAsync(0));   // an auto-relogin holds the gate
        try
        {
            long before = ReloginGen();
            var r = await Program.LoginAsync(manual: true);
            Assert.StartsWith("BUSY", r);
            Assert.Equal(before, ReloginGen());   // the running auto-relogin's loop keeps its generation
            Assert.Equal("wait: another login is running", Program.EmbeddedReloginDecision(false, false, before, ReloginGen(), true));
        }
        finally { gate.Release(); }
    }
    [Fact] public void ManualLogin_CancelsPendingReloginOnlyPastTheGate()
    {
        var root = RepoRoot();
        var prog = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "textclient/galatay-text/Program.cs"));
        int gate = prog.IndexOf("loginGate.WaitAsync(0)", StringComparison.Ordinal);
        int cancel = prog.IndexOf("if (manual) CancelPendingRelogin(\"manual login\")", StringComparison.Ordinal);
        Assert.True(gate > 0 && cancel > gate, "the manual-login cancel must come after the BUSY gate");
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(prog, @"CancelPendingRelogin\(""manual login""\)").Count);
        Assert.Contains("await LoginAsync(manual: true)", prog);
        Assert.Contains("ManualLoginRearm(lr, LoggedIn, DeliberateStopRequested(), embedded: !ExitOnLogout)", prog);
    }
    [Fact] public void FailedManualLogin_ReArmsAutoRelogin_OnlyWhenThatCanHelp()
    {
        Assert.True(Program.ManualLoginRearm("FAILED: Login canceled", false, false, true));
        Assert.True(Program.ManualLoginRearm("FAILED: Unable to connect to simulator", false, false, true));
        Assert.False(Program.ManualLoginRearm("FAILED: Login canceled", false, true, true));     // deliberate-stop
        Assert.False(Program.ManualLoginRearm("FAILED: Login canceled", true, false, true));     // logged in after all
        Assert.False(Program.ManualLoginRearm("FAILED: Login canceled", false, false, false));   // daemon: the supervisor relaunches
        Assert.False(Program.ManualLoginRearm("BUSY: another login is running", false, false, true));   // the running login carries on
        Assert.False(Program.ManualLoginRearm("REFUSED: Firestorm is logged in", false, false, true));
        Assert.False(Program.ManualLoginRearm("OK in 3.1s: region Peronaut", false, false, true));
        Assert.False(Program.ManualLoginRearm("FAILED: cannot read secrets file", false, false, true));
        Assert.False(Program.ManualLoginRearm("FAILED: password empty", false, false, true));
        Assert.False(Program.ManualLoginRearm(null, false, false, true));
    }
    [Fact] public void ReArmedLoop_ReplacesAnOlderGenerationsLoop_NotASameGenerationOne()
    {
        Assert.True(Program.EmbeddedReloginScheduleNeeded(false, -1, 7));
        Assert.True(Program.EmbeddedReloginScheduleNeeded(true, 6, 7));    // the old loop's generation was bumped by the manual login
        Assert.False(Program.EmbeddedReloginScheduleNeeded(true, 7, 7));   // duplicate disconnect event
        var rr = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "textclient/galatay-text/RegionRestart.cs"));
        Assert.Contains("EmbeddedReloginScheduleNeeded(embeddedReloginLoopId != 0, embeddedReloginLoopGen, gen)", rr);
        Assert.Contains("if (embeddedReloginLoopId == loopId) embeddedReloginLoopId = 0;", rr);
        Assert.Contains("go.StartsWith(\"wait\", StringComparison.Ordinal)", rr);
    }
    static string RepoRoot()
    {
        var d = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory); while (d != null && !System.IO.Directory.Exists(System.IO.Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent;
        return d!.FullName;
    }
}
