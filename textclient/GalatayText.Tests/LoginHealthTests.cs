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
        Assert.StartsWith("skip: another login", Program.EmbeddedReloginDecision(false, false, 3, 3, true));
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
}
