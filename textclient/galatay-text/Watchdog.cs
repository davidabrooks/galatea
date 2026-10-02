// Watchdog (added 2026-09-26 09:30 after the 06:32-09:12 freeze: the whole box VM was paused, then the stale
// session walked into STUCK and a NetworkTimeout ended the process with nothing to restart it).
// - HeartbeatLoop (thread pool, every 5 s): stamps hbTicks, writes run/heartbeat (the supervisor in text-galatay.sh
//   kills + relaunches the process if that file is > 120 s old twice in a row), refreshes the wander flag every 60 s.
// - Watchdog thread (dedicated, so a starved/deadlocked thread pool can't stop it), every 2 s:
//     * freeze / clock jump: > 30 s between its own 2 s ticks (wall UTC or monotonic) -> the SL circuit is dead
//     * heartbeat stalled > 60 s (thread pool / main async loop blocked)
//     * connection stale: logged in but no SimStats/ping reply packet for 45 s
//   -> log, keep the wander flag, clean logout (10 s cap), exit code 75 = "relaunch me" (text-galatay.sh supervisor).
// - Unexpected disconnects (NetworkTimeout etc.) that are not region restarts also exit 75 (Program.cs); region
//   restart disconnects keep the in-process reconnect (RegionRestart.cs).
using System.Diagnostics;
using LibreMetaverse;
using LibreMetaverse.Packets;

namespace GalatayText;

public static partial class Program
{
    public const int ExitRelaunch = 75;
    public static int ExitCode = 0;
    static long hbTicks = Environment.TickCount64, lastRecvTicks = Environment.TickCount64;
    static readonly string HeartbeatFile = Path.Combine(Path.GetDirectoryName(SockPath)!, "heartbeat");
    static volatile string wdLastEvent = "-";
    static int wdFired;
    static Thread wdThread;
    const double WdFreezeS = 30, WdHeartbeatS = 60, WdStaleS = 45;

    static void HookWatchdog()
    {
        EventHandler<PacketReceivedEventArgs> h = (s, e) => lastRecvTicks = Environment.TickCount64;
        client.Network.RegisterCallback(PacketType.SimStats, h);
        client.Network.RegisterCallback(PacketType.CompletePingCheck, h);
        client.Network.RegisterCallback(PacketType.ImprovedTerseObjectUpdate, h);
        client.Network.RegisterCallback(PacketType.CoarseLocationUpdate, h);
        lastRecvTicks = Environment.TickCount64;
    }

    static async Task HeartbeatLoop()
    {
        int n = 0;
        while (!cts.IsCancellationRequested)
        {
            hbTicks = Environment.TickCount64;
            try { File.WriteAllText(HeartbeatFile, $"{DateTime.Now:o} pid {Environment.ProcessId} logged_in {LoggedIn} wander {(WanderOn ? "on" : "off")}\n"); } catch { }
            if (++n % 12 == 0 && WanderOn) SaveWanderFlag(true, "heartbeat (wander on)");
            if (n % 720 == 0) Log("watchdog", $"alive: logged_in {LoggedIn}, wander {(WanderOn ? "on" : "off")}, last packet {(Environment.TickCount64 - lastRecvTicks) / 1000.0:F0} s ago");
            try { await Task.Delay(5000, cts.Token); } catch { }
        }
    }

    // pure decision (unit-tested): null = fine, else the reason to relogin
    static string WdDecide(double tickGapWallS, double tickGapMonoS, double hbAgeS, double recvAgeS, bool loggedIn, bool shutting, bool restartActive)
    {
        if (shutting) return null;
        var gap = Math.Max(tickGapWallS, tickGapMonoS);
        if (gap > WdFreezeS) return $"freeze/clock jump: {gap:F0} s between watchdog ticks (box or process was suspended; the SL session is dead)";
        if (hbAgeS > WdHeartbeatS) return $"heartbeat stalled {hbAgeS:F0} s (main async loop / thread pool blocked)";
        if (loggedIn && !restartActive && recvAgeS > WdStaleS) return $"connection stale: no packets from the sim for {recvAgeS:F0} s";
        return null;
    }

    static void StartWatchdog()
    {
        if (wdThread != null) return;
        wdThread = new Thread(() =>
        {
            var lastWall = DateTime.UtcNow; var lastMono = Environment.TickCount64;
            while (true)
            {
                Thread.Sleep(2000);
                var nowWall = DateTime.UtcNow; var nowMono = Environment.TickCount64;
                string why = null;
                try
                {
                    why = WdDecide((nowWall - lastWall).TotalSeconds, (nowMono - lastMono) / 1000.0, (nowMono - hbTicks) / 1000.0,
                        (nowMono - lastRecvTicks) / 1000.0, LoggedIn, shuttingDown || cts.IsCancellationRequested, RestartActive);
                }
                catch { }
                lastWall = nowWall; lastMono = nowMono;
                if (why != null) WdRelaunch(why);
            }
        }) { IsBackground = true, Name = "watchdog" };
        wdThread.Start();
    }

    static void WdRelaunch(string why)
    {
        if (Interlocked.Exchange(ref wdFired, 1) == 1) return;
        wdLastEvent = $"{DateTime.Now:HH:mm:ss} {why}";
        Log("watchdog", $"{why} -> clean logout, exit {ExitRelaunch}; the supervisor logs back in");
        var done = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            try { client.Self.AutoPilotCancel(); } catch { }
            try { if (WanderOn) StopWander("watchdog relogin (flag kept, resumes after the relaunch)", clearFlag: false); } catch { }
            shuttingDown = true; ExitCode = ExitRelaunch;
            try { if (client?.Network?.Connected == true) { var l = Task.Run(() => client.Network.Logout()); l.Wait(10000); } } catch { }
            try { if (File.Exists(LockPath) && File.ReadAllText(LockPath).Trim() == Environment.ProcessId.ToString()) File.Delete(LockPath); } catch { }
            done.Set();
        }) { IsBackground = true };
        t.Start();
        done.Wait(15000); // even if the logout or the thread pool hangs
        Log("exit", $"process exiting (watchdog, code {ExitRelaunch})");
        try { File.Delete(SockPath); } catch { }
        Environment.Exit(ExitRelaunch);
    }

    static string WatchdogStatus() =>
        $"watchdog: heartbeat {(Environment.TickCount64 - hbTicks) / 1000.0:F0} s ago, last sim packet {(Environment.TickCount64 - lastRecvTicks) / 1000.0:F0} s ago, thresholds freeze {WdFreezeS} s / heartbeat {WdHeartbeatS} s / stale {WdStaleS} s; last event {wdLastEvent}";

    static string WatchdogSelfTest()
    {
        var lines = new List<string>(); int pass = 0, fail = 0;
        void T(string name, string got, bool expectFire) { bool ok = (got != null) == expectFire; if (ok) pass++; else fail++; lines.Add($"{(ok ? "PASS" : "FAIL")} {name} -> {got ?? "ok (no action)"}"); }
        T("normal 2 s tick", WdDecide(2, 2, 3, 1, true, false, false), false);
        T("VM paused 2h40m (wall + mono jump 9600 s)", WdDecide(9600, 9600, 9600, 9600, true, false, false), true);
        T("wall clock jump only (mono paused)", WdDecide(9600, 2, 3, 1, true, false, false), true);
        T("heartbeat stalled 70 s", WdDecide(2, 2, 70, 1, true, false, false), true);
        T("heartbeat 50 s (below 60)", WdDecide(2, 2, 50, 1, true, false, false), false);
        T("no sim packets 50 s", WdDecide(2, 2, 3, 50, true, false, false), true);
        T("no packets but logged out (reconnect pending)", WdDecide(2, 2, 3, 500, false, false, false), false);
        T("no packets during region restart handling", WdDecide(2, 2, 3, 500, true, false, true), false);
        T("shutting down", WdDecide(9600, 9600, 9600, 9600, true, true, false), false);
        var lt = new[] { (12f, 63.0), (110f, 210.0), (400f, 645.0) };
        foreach (var (len, want) in lt) { var got = LegTimeoutS(len); bool ok = Math.Abs(got - want) < 0.5; if (ok) pass++; else fail++; lines.Add($"{(ok ? "PASS" : "FAIL")} leg timeout {len} m -> {got:F0} s (want {want:F0})"); }
        void E(string name, string why, bool del, int cur, int want) { int got = ShutdownExitCode(why, del, cur); bool ok = got == want; if (ok) pass++; else fail++; lines.Add($"{(ok ? "PASS" : "FAIL")} exit code: {name} -> {got} (want {want})"); }
        E("SIGTERM, no stop request (hang monitor)", "SIGTERM", false, 0, ExitRelaunch);
        E("SIGINT, no stop request", "SIGINT", false, 0, ExitRelaunch);
        E("SIGTERM after text-galatay.sh stop", "SIGTERM", true, 0, 0);
        E("logout command", "logout command", true, 0, 0);
        E("watchdog already set 75, then logout path", "logout command", true, ExitRelaunch, ExitRelaunch);
        return $"watchdog selftest: {pass} pass, {fail} fail (offline)\n" + string.Join("\n", lines);
    }
    static double LegTimeoutS(float len) => 45 + len * 1.5;
    // exit code for Shutdown(why): SIGTERM/SIGINT without a stop request -> 75 (supervisor relaunches); otherwise unchanged
    static int ShutdownExitCode(string why, bool deliberate, int current) => (why is "SIGTERM" or "SIGINT") && !deliberate ? ExitRelaunch : current;
}
