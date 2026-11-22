using System;
using System.IO;
using System.Text.RegularExpressions;
using GalatayText;
using LibreMetaverse;
using Xunit;

// 2026-11-22 (fixes list items 4c + 5, Nov 20 PT): a 76 s freeze at 10:58:42 left her underwater off the Burgundy pier, the watchdog
// dropped a session whose packets were arriving again, and the wander never resumed after the in-process relogin; a 63 s freeze at
// 11:12:57 (no VM-fork line) waited the slow 30 s relogin.
public class FreezeRecoveryTests
{
    static string Src(string f)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, "textclient/galatay-text", f));
    }
    static Program.Graph PeronautGraph()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "textclient/routes"))) d = d.Parent;
        return Program.LoadGraphFile(Path.Combine(d!.FullName, "textclient/routes/_graph-Peronaut.json"));
    }

    [Fact] public void OnlyTickGapFindings_AreFreezes()
    {
        Assert.True(Program.FreezeFinding("freeze/clock jump: 76 s between watchdog ticks (box or process was suspended; the SL session is dead)"));
        Assert.False(Program.FreezeFinding("heartbeat stalled 70 s (main async loop / thread pool blocked)"));
        Assert.False(Program.FreezeFinding("connection stale: no packets from the sim for 50 s"));
        Assert.False(Program.FreezeFinding(null));
    }

    [Fact] public void AnyRecentFreeze_GetsTheFastRelogin_NotOnlyAVmFork()
    {
        long now = 10_000_000;
        Assert.True(Program.FreezeRecent(now - 40_000, now - 1_000, true, now));          // 11:12:57: watchdog freeze 40 s ago
        Assert.False(Program.FreezeRecent(now - 400_000, now - 1_000, true, now));        // old freeze, watchdog ticking
        Assert.True(Program.FreezeRecent(long.MinValue / 2, now - 63_000, true, now));    // disconnect beat the late watchdog thread
        Assert.False(Program.FreezeRecent(long.MinValue / 2, now - 63_000, false, now));  // no watchdog thread (tests, early start)
        Assert.False(Program.FreezeRecent(long.MinValue / 2, now - 2_000, true, now));    // ordinary network drop: usual backoff
        Assert.Equal(TimeSpan.FromSeconds(5), Program.EmbeddedReloginDelayAfter(1, false, true));
    }

    [Fact] public void Probe_KeepsASessionWhosePacketsFlowAgain()
    {
        Assert.True(Program.FreezeSessionAlive(true, lastRecv: 2_000, seenTicks: 1_000));
        Assert.False(Program.FreezeSessionAlive(true, lastRecv: 900, seenTicks: 1_000));
        Assert.False(Program.FreezeSessionAlive(false, lastRecv: 2_000, seenTicks: 1_000));
    }

    [Fact] public void FreezePause_EndsOnlyWhenTheSessionIsHealthy()
    {
        Assert.True(Program.FreezeHoldOver(true, 5, 0.5));
        Assert.False(Program.FreezeHoldOver(true, 1, 0.5));    // just paused
        Assert.False(Program.FreezeHoldOver(true, 5, 10));     // no packets
        Assert.False(Program.FreezeHoldOver(false, 60, 0.1));  // logged out: the relogin path resumes it
    }

    [Fact] public void Nov20Spot_UnderwaterOffThePier_IsUnsafe()
    {
        var g = PeronautGraph();
        var me = new Vector3(206.5f, 26.1f, 12.7f);
        var why = Program.UnsafeSpotWhy(me, 20f, Program.GraphOffPath(g, me), seated: false);
        Assert.NotNull(why);
        Assert.Contains("underwater", why);
        Assert.Null(Program.UnsafeSpotWhy(me, 20f, 9f, seated: true));   // seated: never moved by this check
    }

    [Fact] public void OnThePathAndOnTheBeach_AreSafe()
    {
        var g = PeronautGraph();
        foreach (var p in new[] { g.N[g.Places["home"].node], g.N[g.Places["beach"].node], g.N[g.Places["pier"].node], new Vector3(218.9f, 57.4f, 21.5f) })
        {
            float off = Program.GraphOffPath(g, p);
            Assert.True(off < 8f, $"{p} off {off}");
            Assert.Null(Program.UnsafeSpotWhy(p, 20f, off, seated: false));
        }
    }

    [Fact] public void FarOffTheGraph_IsUnsafe()
    {
        Assert.Contains("off the path graph", Program.UnsafeSpotWhy(new Vector3(100, 100, 25), 20f, 30f, false));
        var g = new Program.Graph(); g.N.Add(new Vector3(0, 0, 21)); g.N.Add(new Vector3(10, 0, 21)); g.E.Add((0, 1, ""));
        Assert.Equal(3f, Program.GraphOffPath(g, new Vector3(5, 3, 21)), 3);
        Assert.Equal(0f, Program.GraphOffPath(null, new Vector3(5, 3, 21)));
    }

    [Fact] public void SpotCheck_RunsAfterAFreezeAndOtherwiseRateLimited()
    {
        Assert.True(Program.SafeSpotCheckDue(null, 1, freezeSinceLast: true));
        Assert.False(Program.SafeSpotCheckDue(null, 1, freezeSinceLast: false));
        Assert.True(Program.SafeSpotCheckDue(null, 25, freezeSinceLast: false));
        Assert.False(Program.SafeSpotCheckDue("chat", 99, freezeSinceLast: true));
    }

    [Fact] public void Wiring()
    {
        var wd = Src("Watchdog.cs"); var rr = Src("RegionRestart.cs"); var wa = Src("Wander.cs"); var vf = Src("VmForkWatch.cs"); var fr = Src("FreezeRecovery.cs");
        // a watchdog freeze stops the walk before anything else and probes before dropping
        Assert.Matches(new Regex(@"if \(FreezeFinding\(why\)\) FreezeStopWalking\([^\n]*\n\s*if \(action == ""exit-relaunch""\)"), wd);
        Assert.Contains("FreezeProbeThenDrop(why, now)", wd);
        Assert.Contains("Interlocked.Exchange(ref wdTickTicks, nowMono)", wd);
        // a VM fork stops the walk too
        Assert.Contains("FreezeStopWalking(label);", vf);
        // fast relogin for any freeze; wander resumes after the in-process relogin
        Assert.Contains("afterFork |= afterFreeze;", rr);
        Assert.Matches(new Regex(@"if \(r\.StartsWith\(""OK""\)\)\s*\{[^}]*WanderAutoResume\(""in-process auto-relogin""\)"), rr);
        // both wander loops end the freeze pause; the home loop checks her spot before the next leg
        Assert.Equal(2, Regex.Matches(wa, @"if \(pause == ""freeze""\)").Count);
        Assert.Contains("SafeSpotCheckDueNow(wanderPause) && await SafeSpotRecover(g, \"home wander loop\", ct)", wa);
        // recovery is a local teleport to the graph place 'home', never a walk from the water
        Assert.Contains("TryGetValue(\"home\"", fr);
        Assert.Contains("TeleportAsync(sim.Name", fr);
    }
}
