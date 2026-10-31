// LoginHealth.cs (2026-10-31 13:52-13:58 PT): two failures in one afternoon.
// 1. The embedded auto-relogin (RegionRestart.cs) fired 30 s after a NetworkTimeout while a deliberate logout + manual login
//    was in progress: it ignored run/deliberate-stop and overlapped the manual login, leaving a tangled session
//    (region '', pos 0,0,0, no caps, every worn name '?'). Now every pending auto-relogin carries a generation number;
//    a manual login or any logout bumps it (cancelling the pending one), deliberate-stop is honoured before each try,
//    and only one login runs at a time (loginGate).
// 2. A login can come back "OK" with no seed caps (seed request 404): empty region, 0 objects, no FetchInventory2 /
//    UploadBakedTexture. The veil/collar/EvoX steps then ran blind for minutes. Now a health check at +15 s marks such a
//    login FAILED (LoginFailedReason), the collar and EvoX steps stop with a clear message, and (only when deliberate-stop
//    is absent and no manual login/logout happened meanwhile) one automatic full disconnect + fresh login is tried.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GalatayText;

public static partial class Program
{
    static long reloginGen;                                   // bumped by a manual login and by every logout
    static readonly SemaphoreSlim loginGate = new(1, 1);      // one login at a time (manual or automatic)
    internal static string LoginFailedReason;                 // set by the +15 s health check; null = healthy or unknown
    static int loginRecoveryUsed;                             // one automatic recovery per process
    internal const int LoginHealthDelaySec = 15;

    /// <summary>A manual login or any logout: cancel every pending auto-relogin.</summary>
    internal static long CancelPendingRelogin(string why)
    {
        var g = Interlocked.Increment(ref reloginGen);
        Log("disconnect", $"pending auto-relogin cancelled ({why})");
        return g;
    }

    /// <summary>Pure (tested): may a scheduled auto-relogin try go now? "go" or "skip: reason" (skip = give up for good).</summary>
    internal static string EmbeddedReloginDecision(bool deliberateStop, bool loggedIn, long scheduledGen, long currentGen, bool loginRunning)
    {
        if (deliberateStop) return "skip: deliberate-stop marker present";
        if (scheduledGen != currentGen) return "skip: a manual login or logout happened since the disconnect";
        if (loggedIn) return "skip: already logged in again";
        if (loginRunning) return "skip: another login is running";
        return "go";
    }

    /// <summary>Pure (tested): null when the login looks healthy at +15 s, else why it failed.</summary>
    internal static string LoginHealthProblem(string region, int objectsKnown, bool hasFetchInventory2, bool hasUploadBakedTexture)
    {
        var p = new List<string>();
        if (string.IsNullOrWhiteSpace(region)) p.Add("region name unknown");
        if (objectsKnown <= 0) p.Add("0 objects");
        if (!hasFetchInventory2) p.Add("no FetchInventory2 cap");
        if (!hasUploadBakedTexture) p.Add("no UploadBakedTexture cap");
        // the region name alone can arrive without caps (13:57 seed 404): caps missing = failed even with a region
        bool failed = p.Contains("region name unknown") || !hasFetchInventory2 || !hasUploadBakedTexture;
        return failed ? string.Join(", ", p) : null;
    }

    /// <summary>Pure (tested): after a failed login, try the one automatic full disconnect + fresh login?</summary>
    internal static bool LoginRecoveryAllowed(bool deliberateStop, bool alreadyUsed, long genAtLogin, long currentGen)
        => !deliberateStop && !alreadyUsed && genAtLogin == currentGen;

    /// <summary>Pure (tested): a touch/list result in which no worn attachment name has resolved ("worn: ?, ?" or "worn: ").</summary>
    internal static bool WornNamesUnresolved(string touchResult)
    {
        var s = touchResult ?? "";
        int i = s.IndexOf("worn:", StringComparison.Ordinal);
        if (i < 0) return false;
        var names = s[(i + 5)..].TrimEnd(')', ' ').Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return names.Length == 0 || names.All(n => n == "?");
    }

    /// <summary>The steps that need a working session (collar, EvoX) call this first: non-null = stop with this message.</summary>
    internal static string LoginBrokenStop(string step)
        => LoginFailedReason == null ? null : $"{step} skipped: the login FAILED ({LoginFailedReason}); a fresh login is needed";

    static async Task LoginHealthCheck(long genAtLogin)
    {
        try
        {
            await Task.Delay(LoginHealthDelaySec * 1000);
            if (!LoggedIn || Interlocked.Read(ref reloginGen) != genAtLogin) return;
            var sim = client.Network.CurrentSim;
            var problem = LoginHealthProblem(sim?.Name, sim?.ObjectsPrimitives.Count ?? 0,
                sim?.Caps?.CapabilityURI("FetchInventory2") != null, sim?.Caps?.CapabilityURI("UploadBakedTexture") != null);
            if (problem == null) { LoginFailedReason = null; Log("login", $"health at +{LoginHealthDelaySec} s: ok"); return; }
            LoginFailedReason = problem;
            Log("login", $"FAILED (health at +{LoginHealthDelaySec} s): {problem}; veil/collar/EvoX steps stop");
            bool used = Interlocked.CompareExchange(ref loginRecoveryUsed, 1, 0) == 1;
            if (!LoginRecoveryAllowed(DeliberateStopRequested(), used, genAtLogin, Interlocked.Read(ref reloginGen)))
            { Log("login", "no automatic recovery (deliberate-stop, already tried, or a manual login/logout happened): a fresh login is needed"); return; }
            Log("login", "recovery: one full disconnect, 60 s wait, one fresh login");
            shuttingDown = true;   // our own logout: the Disconnected handler must not schedule an auto-relogin
            try { client.Network.Logout(); } catch (Exception ex) { Log("login", "recovery logout: " + ex.GetBaseException().Message); }
            await Task.Delay(60000);
            if (!LoginRecoveryAllowed(DeliberateStopRequested(), false, genAtLogin, Interlocked.Read(ref reloginGen)) || LoggedIn)
            { Log("login", "recovery: cancelled before the fresh login"); return; }
            Log("login", "recovery login: " + await LoginAsync());
        }
        catch (Exception ex) { Log("login", "health check error: " + ex.GetBaseException().Message); }
    }
}
