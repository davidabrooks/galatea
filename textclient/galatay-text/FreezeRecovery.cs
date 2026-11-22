// FreezeRecovery.cs (2026-11-22, fixes-after-sunday items 4c + 5). Nov 20 PT:
//  - 10:58:42 a host VM snapshot froze the box for 76 s while she walked the Burgundy pier. The sim kept her moving and she
//    ended up underwater at 206.5,26.1,12.7 (water 20). The watchdog dropped the circuit even though sim packets were
//    arriving again (rx 2 s ago). The fast relogin (5 s) logged her back in underwater, and the wander stayed off
//    ("flag kept; resumes after the restart"): the auto-resume only ran from the daemon Main, never after the in-process relogin.
//  - 11:12:57 a 63 s watchdog freeze with no VM-fork line: the relogin waited the usual 30 s (fast relogin was fork-only),
//    and the wander had to be started by hand at 11:14:09.
// Now:
//  - any freeze (watchdog tick gap or VM fork) stops the walk at once (leg + manual walk cancelled, autopilot stopped) and
//    puts a running wander in a "freeze" pause until the session is healthy again;
//  - before dropping the circuit for a watchdog freeze, the session is probed for FreezeProbeSec: a sim packet handled
//    after the freeze was seen = alive, keep it;
//  - a disconnect within 5 min of any freeze (or while the watchdog thread itself is late) uses the fast 5 s relogin;
//  - after a successful in-process auto-relogin the kept wander flag resumes it (same WanderAutoResume as the daemon);
//  - the home wander loop checks her spot before walking: underwater or far off the path graph -> local teleport to the
//    graph place 'home' (SL home / arrival point on the upper level), never a walk from the water.
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    internal const int FreezeProbeSec = 6, FreezeHoldMinSec = 3, FreezeHealthyRecvSec = 3, SafeSpotCheckSec = 20;
    internal const float FreezeOffPathM = 8f;
    static long lastFreezeTicks = long.MinValue / 2;      // Environment.TickCount64 when a freeze (watchdog or VM fork) was last seen
    static long wdTickTicks = Environment.TickCount64;     // last watchdog loop tick (Watchdog.cs)
    internal static volatile string LastFreezeEvent;
    static DateTime safeSpotCheckedAt = DateTime.MinValue;
    static int safeSpotFails;

    /// <summary>Pure (tested): a watchdog finding that means the box or process was suspended.</summary>
    internal static bool FreezeFinding(string why) => why != null && why.StartsWith("freeze/", StringComparison.Ordinal);

    /// <summary>Pure (tested): is a disconnect now blamed on a freeze? A freeze seen in the last 5 min, or the watchdog thread
    /// itself is late (the disconnect beat the watchdog to it after a resume).</summary>
    internal static bool FreezeRecent(long freezeTicks, long wdTick, bool watchdogRunning, long now)
        => VmForkRecent(freezeTicks, now) || (watchdogRunning && now - wdTick > (long)(WdFreezeS * 1000));

    /// <summary>Pure (tested): after a freeze seen at seenTicks, is the session alive? (connected and a sim packet handled since)</summary>
    internal static bool FreezeSessionAlive(bool connected, long lastRecv, long seenTicks) => !VmForkSessionDead(connected, lastRecv, seenTicks);

    /// <summary>Pure (tested): may a "freeze" wander pause end? Logged in, paused a few seconds, and sim packets flowing.</summary>
    internal static bool FreezeHoldOver(bool loggedIn, double pausedS, double recvAgeS)
        => loggedIn && pausedS >= FreezeHoldMinSec && recvAgeS <= FreezeHealthyRecvSec;

    /// <summary>Pure (tested): distance from p to the nearest path-graph edge (3D: node z is her standing height).</summary>
    internal static float GraphOffPath(Graph g, Vector3 p)
    {
        if (g == null || g.N.Count == 0) return 0f;
        float best = float.MaxValue;
        foreach (var n in g.N) best = Math.Min(best, Vector3.Distance(p, n));
        foreach (var (a, b, _) in g.E)
        {
            if (a < 0 || b < 0 || a >= g.N.Count || b >= g.N.Count) continue;
            var A = g.N[a]; var B = g.N[b]; var ab = B - A; float L2 = ab.LengthSquared();
            if (L2 < 1e-6f) continue;
            float t = Math.Clamp(Vector3.Dot(p - A, ab) / L2, 0f, 1f);
            best = Math.Min(best, Vector3.Distance(p, A + ab * t));
        }
        return best;
    }

    /// <summary>Pure (tested): why her spot is unsafe to walk from (null = fine). Seated = never (seats sit off the path).</summary>
    internal static string UnsafeSpotWhy(Vector3 me, float waterHeight, float offPathM, bool seated)
    {
        if (seated) return null;
        if (waterHeight > 0 && me.Z < waterHeight) return $"underwater (z {me.Z:F1}, water {waterHeight:F1})";
        if (offPathM > FreezeOffPathM) return $"{offPathM:F1} m off the path graph";
        return null;
    }

    /// <summary>Pure (tested): run the spot check now? Only while walking is due (no pause, or the freeze pause just ended),
    /// and at most every SafeSpotCheckSec unless a freeze was seen since the last check.</summary>
    internal static bool SafeSpotCheckDue(string pause, double sinceLastS, bool freezeSinceLast)
        => pause == null && (freezeSinceLast || sinceLastS >= SafeSpotCheckSec);

    static long safeSpotFreezeSeen = long.MinValue / 2;
    static bool FreezePauseOver() => FreezeHoldOver(LoggedIn, (DateTime.Now - wPausedAt).TotalSeconds, (Environment.TickCount64 - Interlocked.Read(ref lastRecvTicks)) / 1000.0);
    static bool SafeSpotCheckDueNow(string pause)
    {
        long fz = Interlocked.Read(ref lastFreezeTicks);
        if (!SafeSpotCheckDue(pause, (DateTime.Now - safeSpotCheckedAt).TotalSeconds, fz != safeSpotFreezeSeen)) return false;
        safeSpotCheckedAt = DateTime.Now; safeSpotFreezeSeen = fz;
        return true;
    }

    /// <summary>Any freeze: stop walking now. The walk and leg are cancelled and the autopilot stopped; a running, standing
    /// wander gets the "freeze" pause (HomeWanderLoop ends it once sim packets flow again).</summary>
    static void FreezeStopWalking(string why)
    {
        try
        {
            Interlocked.Exchange(ref lastFreezeTicks, Environment.TickCount64);
            LastFreezeEvent = $"{DateTime.Now:HH:mm:ss} {why}";
            if (!LoggedIn || client?.Self == null) return;
            if (client.Self.SittingOn != 0) { Log("freeze", $"{why}: seated, nothing to stop"); return; }
            if (WanderOn && wanderPause is null or "chat" or "greet") { SetPauseNow("freeze", $"{why}: stopping the walk until the session is healthy"); return; }
            legCts?.Cancel();
            if (walkTask != null && !walkTask.IsCompleted) walkCts?.Cancel();
            client.Self.AutoPilotCancel();
            Log("freeze", $"{why}: walk stopped");
        }
        catch (Exception ex) { Log("freeze", "stop walking error: " + ex.GetBaseException().Message); }
    }

    /// <summary>Watchdog freeze in the MCP connector: probe the session before dropping it.</summary>
    static async Task FreezeProbeThenDrop(string why, long seen)
    {
        try
        {
            for (int i = 0; i < FreezeProbeSec; i++)
            {
                await Task.Delay(1000);
                if (!LoggedIn || shuttingDown) { Log("watchdog", "freeze probe: session already gone; the auto-relogin handles it"); return; }
                if (FreezeSessionAlive(client?.Network?.Connected == true, Interlocked.Read(ref lastRecvTicks), seen))
                { Log("watchdog", $"freeze probe: session alive after {i + 1} s (sim packets handled again): not dropping it; {PacketAgesSummary()}"); return; }
            }
            if (DeliberateStopRequested()) { Log("watchdog", "freeze probe: session looks dead but deliberate-stop is set: no relogin"); return; }
            Log("watchdog", $"freeze probe: no sim packet handled for {FreezeProbeSec} s -> dropping the dead circuit; the in-process auto-relogin logs back in (fast); {PacketAgesSummary()}");
            await client.Network.ShutdownAsync(NetworkManager.DisconnectType.NetworkTimeout, "watchdog: " + why);
        }
        catch (Exception ex) { Log("watchdog", "freeze probe / circuit shutdown error: " + ex.GetBaseException().Message); }
    }

    /// <summary>Home wander: before a leg, get out of the water / back to the path graph by a local teleport to 'home'.
    /// Returns true when it acted (the loop re-checks).</summary>
    static async Task<bool> SafeSpotRecover(Graph g, string ctx, CancellationToken ct)
    {
        var sim = client.Network.CurrentSim;
        if (sim == null || !InPeronaut) return false;
        var me = client.Self.SimPosition;
        var why = UnsafeSpotWhy(me, sim.WaterHeight, GraphOffPath(g, me), client.Self.SittingOn != 0);
        if (why == null) { safeSpotFails = 0; return false; }
        if (!g.Places.TryGetValue("home", out var home) || home.node < 0 || home.node >= g.N.Count) { WLog($"UNSAFE SPOT at {P3(me)} ({why}, {ctx}) but the graph has no 'home' place: pausing (user)"); wanderPause = "user"; wPausedAt = DateTime.Now; return true; }
        var dest = g.N[home.node];
        WLog($"UNSAFE SPOT at {P3(me)} ({why}, {ctx}): teleporting to 'home' {P3(dest)} before walking anywhere");
        try { client.Self.AutoPilotCancel(); } catch { }
        bool ok = false;
        try { ok = await client.Self.TeleportAsync(sim.Name, new Vector3(dest.X, dest.Y, dest.Z + 0.3f)); }
        catch (Exception ex) when (ex is not OperationCanceledException) { WLog("safe-spot teleport exception: " + ex.GetBaseException().Message); }
        if (ok) { safeSpotFails = 0; WLog($"safe-spot teleport OK: now {P3(client.Self.SimPosition)}"); await Task.Delay(3000, ct); return true; }
        if (++safeSpotFails >= 3) { WLog($"safe-spot teleport failed {safeSpotFails}x ({client.Self.TeleportMessage}): pausing (user); 'wander resume' once she is safe"); wanderPause = "user"; wPausedAt = DateTime.Now; safeSpotFails = 0; return true; }
        WLog($"safe-spot teleport failed ({client.Self.TeleportMessage}); retrying in 10 s");
        await Task.Delay(10000, ct);
        return true;
    }
}
