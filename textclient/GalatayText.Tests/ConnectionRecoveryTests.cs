using System;
using System.IO;
using System.Linq;
using GalatayText;
using LibreMetaverse;
using Xunit;

// 2026-11-20 10:15:29 PT NetworkTimeout at Peronaut, the 10:16:03 relogin that hung 60 s in the login POST, and the
// MCP connector that ran without its watchdog (ConnectionDiag.cs, LoginRequest.cs, Watchdog.cs, VmForkWatch.cs)
public class ConnectionRecoveryTests
{
    static string Src(string file)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory); while (d != null && !Directory.Exists(Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, "textclient/galatay-text", file));
    }
    static string Between(string s, string from, string to) { int a = s.IndexOf(from, StringComparison.Ordinal); Assert.True(a >= 0, from); int b = s.IndexOf(to, a, StringComparison.Ordinal); Assert.True(b > a, to); return s[a..b]; }

    // ---- watchdog in the MCP connector ----
    [Fact] public void Watchdog_InTheMcpConnector_NeverExits_AndDropsOnlyADeadSession()
    {
        const string stale = "connection stale: no packets from the sim for 50 s", freeze = "freeze/clock jump: 40 s between watchdog ticks", hb = "heartbeat stalled 70 s (main async loop / thread pool blocked)";
        // MCP connector (exitOnLogout false): no process exit, ever
        Assert.Equal("drop-circuit", Program.WdAction(stale, false, true, false));
        Assert.Equal("drop-circuit", Program.WdAction(freeze, false, true, false));
        Assert.Equal("log", Program.WdAction(hb, false, true, false));        // the session may be fine
        Assert.Equal("log", Program.WdAction(stale, false, true, true));      // deliberate-stop: log only
        Assert.Equal("log", Program.WdAction(freeze, false, false, false));   // no session (a login may be running)
        // daemon: unchanged while logged in (clean logout, exit 75, the supervisor relaunches)
        Assert.Equal("exit-relaunch", Program.WdAction(stale, true, true, false));
        Assert.Equal("exit-relaunch", Program.WdAction(hb, true, true, false));
        Assert.Equal("log", Program.WdAction(freeze, true, false, false));
        Assert.Equal("log", Program.WdAction(null, false, true, false));
    }
    [Fact] public void Watchdog_StartsFromEveryLogin_Idempotently()
    {
        var prog = Src("Program.cs"); var wd = Src("Watchdog.cs");
        var core = Between(prog, "static async Task<string> LoginCoreAsync()", "var lp = client.Network.DefaultLoginParams");
        Assert.Contains("EnsureWatchdog();", core);
        Assert.Contains("StartLastOnlineTimer();", core);
        Assert.Contains("HookPacketAges();", core);
        Assert.DoesNotContain("_ = Task.Run(HeartbeatLoop); StartWatchdog();", prog);   // only via EnsureWatchdog
        Assert.Contains("if (Interlocked.Exchange(ref watchdogStarted, 1) == 1) return;", wd);
        var act = Between(wd, "static void WdAct(string why)", "static void WdRelaunch(string why)");
        Assert.Contains("if (action == \"exit-relaunch\") { WdRelaunch(why); return; }", act);   // the only way to the exit
        Assert.Contains("ShutdownAsync(NetworkManager.DisconnectType.NetworkTimeout", act);        // -> Disconnected -> auto-relogin
        Assert.DoesNotContain("Environment.Exit", act);
        Assert.Contains("if (why != null) WdAct(why);", wd);
    }

    // ---- diagnostics ----
    [Fact] public void PacketAges_CurrentRegionFirst_NeverForUnseen()
    {
        var s = Program.FormatPacketAges(new[]
        {
            new Program.SimPacketAge("Terric", false, 1.25, 1.3, 400),
            new Program.SimPacketAge("Peronaut", true, 61.04, 75.5, 12345),
            new Program.SimPacketAge("Castiliano", false, null, null, 0),
        });
        Assert.Equal("Peronaut* rx 61.0 s / processed 75.5 s ago (12345 pkts); Castiliano rx never / processed never ago (0 pkts); Terric rx 1.2 s / processed 1.3 s ago (400 pkts)", s);
        Assert.Equal("no regions seen", Program.FormatPacketAges(Array.Empty<Program.SimPacketAge>()));
    }
    [Fact] public void NetworkTimeoutLine_CarriesPacketAges_AndStatsLogEveryMinute()
    {
        var prog = Src("Program.cs"); var wd = Src("Watchdog.cs"); var diag = Src("ConnectionDiag.cs");
        Assert.Contains("e.Reason == NetworkManager.DisconnectType.NetworkTimeout ? $\"; last packets: {PacketAgesSummary()}; {TickerStatus()}\"", prog);
        Assert.Contains("SamplePacketAges(nowMono); MaybeLogPacketStats(nowMono); CheckTickerStall(nowMono);", wd);
        Assert.Equal(60, Program.PacketStatsEverySec);
        Assert.Contains("RegisterCallback(PacketType.Default", diag);
        Assert.Contains("\"net-stats\"", prog);   // a NonEvents kind: one line a minute must not flood poll_events
    }
    [Fact] public void TickerSteps_OverFiveSeconds_AreLogged()
    {
        Assert.Null(Program.TickerSlowStepNote("OutfitZoneTick", 4.9, true));
        Assert.Null(Program.TickerSlowStepNote("OutfitZoneTick", 5.0, true));
        Assert.Null(Program.TickerSlowStepNote(null, 30, false));
        Assert.Equal("slow step OutfitZoneTick: 7.2 s (the 1 s ticker waited for it)", Program.TickerSlowStepNote("OutfitZoneTick", 7.21, true));
        Assert.Equal("step NeighborTick still running after 12 s (the 1 s ticker is blocked)", Program.TickerSlowStepNote("NeighborTick", 12.3, false));
        var ticker = Between(Src("Program.cs"), "static async Task Ticker()", "static readonly TimeSpan CmdReplyCap");
        foreach (var step in new[] { "AutoFollowTick", "CameraAnchorTick", "ParcelWatchTick", "ScriptsWatchTick", "NeighborTick", "OutfitZoneTick" })
            Assert.Contains($"TickerStepBegin(\"{step}\")", ticker);
        Assert.Equal(6, System.Text.RegularExpressions.Regex.Matches(ticker, @"TickerStepEnd\(\)").Count);
        Assert.DoesNotContain("LastOnlineHeartbeat()", ticker);   // its own timer now: a blocked step no longer stops last-online.txt
        Assert.Contains("LastOnlineHeartbeat()", Between(Src("ConnectionDiag.cs"), "static void StartLastOnlineTimer()", "\n    }\n"));
    }

    // ---- login request ----
    [Fact] public void LoginReply_IsAnyStatusPastConnectingToLogin()
    {
        Assert.False(Program.LoginReplied(LoginStatus.ConnectingToLogin));
        Assert.False(Program.LoginReplied(LoginStatus.None));
        Assert.False(Program.LoginReplied(LoginStatus.Failed));
        Assert.True(Program.LoginReplied(LoginStatus.ReadingResponse));
        Assert.True(Program.LoginReplied(LoginStatus.ConnectingToSim));
        Assert.True(Program.LoginReplied(LoginStatus.Redirecting));
        Assert.True(Program.LoginReplied(LoginStatus.Success));
        Assert.InRange(Program.LoginPostTimeoutS, 20, 25);
    }
    [Fact] public void CancelledLoginRequest_RetriesOncePerCause_NeverWithDeliberateStop()
    {
        Assert.Equal("timeout", Program.LoginRequestRetry("timeout: no reply from the login server in 22 s", 0, 0, false));
        Assert.Null(Program.LoginRequestRetry("timeout: no reply from the login server in 22 s", 1, 0, false));
        Assert.Equal("fork", Program.LoginRequestRetry("fork: host VM snapshot (fork 127->128) landed while the login request was in flight", 1, 0, false));
        Assert.Null(Program.LoginRequestRetry("fork: x", 0, 1, false));
        Assert.Null(Program.LoginRequestRetry("timeout: x", 0, 0, true));   // deliberate-stop
        Assert.Null(Program.LoginRequestRetry(null, 0, 0, false));          // a failure we did not cause: not retried here
        Assert.Null(Program.LoginRequestRetry("Login canceled", 0, 0, false));
    }
    [Fact] public void OnlyAnUnansweredRequestIsCancelled_Once()   // the double-login guard
    {
        var t = typeof(Program); const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var ctsField = t.GetField("loginAttemptCts", F)!; var repliedField = t.GetField("loginAttemptReplied", F)!; var whyField = t.GetField("loginAttemptCancel", F)!;
        try
        {
            ctsField.SetValue(null, null);
            Assert.False(Program.CancelLoginRequest("fork: no login running"));
            var cts = new System.Threading.CancellationTokenSource();
            ctsField.SetValue(null, cts); repliedField.SetValue(null, true); whyField.SetValue(null, null);
            Assert.False(Program.CancelLoginRequest("fork: after the reply"));   // the sim connect runs on
            Assert.False(cts.IsCancellationRequested);
            repliedField.SetValue(null, false);
            Assert.True(Program.CancelLoginRequest("timeout: no reply"));
            Assert.True(cts.IsCancellationRequested);
            Assert.Equal("timeout: no reply", whyField.GetValue(null));
            Assert.False(Program.CancelLoginRequest("fork: second cause"));      // first reason wins, one cancel
            Assert.Equal("timeout: no reply", whyField.GetValue(null));
        }
        finally { ctsField.SetValue(null, null); repliedField.SetValue(null, false); whyField.SetValue(null, null); }
    }
    [Fact] public void LoginLoop_UsesTheGuardedAttempt_AndVmForkCancelsAnInFlightRequest()
    {
        var core = Between(Src("Program.cs"), "static async Task<string> LoginCoreAsync()", "lp.Password = \"\";");
        Assert.Contains("await LoginAttemptAsync(lp)", core);
        Assert.DoesNotContain("client.Network.LoginAsync(lp)", core);
        Assert.Contains("LoginRequestRetry(cancelled, timeoutRetries, forkRetries, DeliberateStopRequested())", core);
        Assert.Contains("ghostPending = true;", core);   // a "presence" refusal after a cancelled request is waited out
        Assert.Contains("ShouldRetryGhostLogin(client.Network.LoginMessage, ghostPending, ++ghostTries)", core);
        var fork = Src("VmForkWatch.cs");
        Assert.Contains("!DeliberateStopRequested() && CancelLoginRequest($\"fork: {label} landed while the login request was in flight\")", fork);
        var req = Src("LoginRequest.cs");
        Assert.Contains("net.LoginAsync(lp, cts.Token)", req);
        Assert.Contains("if (cts == null || loginAttemptReplied || loginAttemptCancel != null) return false;", req);
    }
}
