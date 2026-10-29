// CrashGuard.cs (2026-10-28): the connector died ~1-2 s after every `login` (dmesg: "galatay-mcp: .NET TP Worker: potentially unexpected
// fatal signal 6", 4 times 17:28-17:32). Root cause: the deployed LibreMetaverse.dll was the stale unpatched build (sha 08b13a80...,
// copied from the old /home/box/viewers/textclient/*-staging), so the seed-capability retry was the upstream unbounded synchronous
// recursion again ("Seed capability returned no response. Trying again." x19 in one second, then "Stack overflow." = SIGABRT; the
// same crash as 2026-10-08, fixed by libremetaverse-seed-retry.patch, which the stale DLL does not contain).
// Guards here: (1) refuse to log in when the loaded LibreMetaverse lacks a galatea patch marker; (2) log every unhandled / unobserved
// exception to textclient.log before the runtime aborts (a stack overflow itself cannot be caught: only the runtime's stderr shows it);
// (3) a lock file that names a dead connector = a ghost avatar session: say so, clear the lock, let the next login replace it;
// (4) a tool call (login) finishes on its own even when the MCP caller cancels or drops it.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
namespace GalatayText;

public static partial class Program
{
    // (type, public/nonpublic member) that only the galatea-patched LibreMetaverse has. Pure check below.
    internal static readonly (string Type, string Member, string Patch)[] RequiredLmvMarkers =
    {
        ("LibreMetaverse.Caps", "SeedMaxAttempts", "libremetaverse-seed-retry.patch"),
    };

    /// <summary>pure (tested): the patch files missing from <paramref name="lmv"/>; empty = good.</summary>
    internal static List<string> LmvMissingPatches(Assembly lmv, IEnumerable<(string Type, string Member, string Patch)> markers)
    {
        var missing = new List<string>();
        foreach (var (type, member, patch) in markers)
        {
            var t = lmv?.GetType(type, false);
            bool has = t != null && t.GetMember(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance).Length > 0;
            if (!has && !missing.Contains(patch)) missing.Add(patch);
        }
        return missing;
    }

    /// <summary>null = the loaded LibreMetaverse carries every galatea patch; otherwise the refusal text.</summary>
    internal static string LmvPatchGuard()
    {
        try
        {
            var lmv = typeof(LibreMetaverse.GridClient).Assembly;
            var missing = LmvMissingPatches(lmv, RequiredLmvMarkers);
            if (missing.Count == 0) return null;
            return $"the loaded LibreMetaverse.dll ({lmv.Location}) is missing galatea patch(es) {string.Join(", ", missing)}: it is an unpatched/stale build and would crash the connector right after login (stack overflow in the seed-capability retry). Rebuild from the repo (build-all.sh) and deploy from ITS app-staging / mcp-app-staging";
        }
        catch (Exception ex) { return null; /* a guard failure must not block login */ }
    }

    // ---- unhandled / unobserved exceptions -> textclient.log -------------------------------------------------------
    static int crashHandlersInstalled;
    internal static string CrashRunDir => Path.GetDirectoryName(SockPath)!;

    internal static void InstallCrashHandlers()
    {
        if (System.Threading.Interlocked.Exchange(ref crashHandlersInstalled, 1) != 0) return;
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            CrashLine("crash", $"UNHANDLED {(e.IsTerminating ? "(terminating) " : "")}{e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            CrashLine("warn", "unobserved task exception (ignored, process kept): " + e.Exception.Flatten());
            e.SetObserved();
        };
    }

    /// <summary>log line written straight to the log file + stderr, without taking logLock (the crashing thread may hold it).</summary>
    internal static void CrashLine(string kind, string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{kind}] {msg}";
        try { File.AppendAllText(LogPath, line + "\n"); } catch { }
        try { Console.Error.WriteLine(line); } catch { }
        if (kind == "crash") { try { File.WriteAllText(Path.Combine(CrashRunDir, "crash-last.txt"), line + "\n"); } catch { } }
    }

    // ---- ghost session ---------------------------------------------------------------------------------------------
    /// <summary>pure (tested): pid named by the session lock when that connector is gone (= it died logged in), else null.</summary>
    internal static int? GhostPidFromLock(string lockText, int selfPid, Func<int, bool> alive)
    {
        if (!int.TryParse((lockText ?? "").Trim(), out var pid) || pid <= 0 || pid == selfPid) return null;
        return alive(pid) ? null : pid;
    }

    /// <summary>pure (tested): /proc/&lt;pid&gt;/stat says the process still runs (not a zombie / dead).</summary>
    internal static bool ProcStatAlive(string statLine)
    {
        if (string.IsNullOrWhiteSpace(statLine)) return false;
        int close = statLine.LastIndexOf(')');
        if (close < 0 || close + 2 >= statLine.Length) return false;
        char state = statLine[close + 2];
        return state != 'Z' && state != 'X' && state != 'x';
    }

    internal static string GhostMarkerPath => Path.Combine(CrashRunDir, "ghost-session.json");

    /// <summary>Startup (connector and daemon): the lock names a dead connector -> its avatar is still on the grid. Record it, clear the lock.</summary>
    internal static int? GhostSessionAtStart()
    {
        try
        {
            if (!File.Exists(LockPath)) return null;
            var pid = GhostPidFromLock(File.ReadAllText(LockPath), Environment.ProcessId, ProcAlive);
            if (pid == null) return null;
            CrashLine("ghost", $"connector pid {pid} died while logged in (see crash-last.txt / dmesg): its avatar session may still be on the grid; stale lock cleared; the next login replaces it (the login server logs the old session out)");
            File.WriteAllText(GhostMarkerPath, JsonSerializer.Serialize(new { pid, detected = DateTime.Now.ToString("s") }));
            File.Delete(LockPath);
            return pid;
        }
        catch (Exception ex) { CrashLine("warn", "ghost check failed: " + ex.Message); return null; }
    }

    /// <summary>After a good login: the ghost (if any) is replaced.</summary>
    internal static void GhostSessionReplaced()
    {
        try
        {
            if (!File.Exists(GhostMarkerPath)) return;
            Log("ghost", "login OK: the ghost session of the dead connector is replaced (marker cleared)");
            File.Delete(GhostMarkerPath);
        }
        catch { }
    }

    /// <summary>pure (tested): retry a failed login while a ghost is pending and the server says the account is still logged in (bounded).</summary>
    internal static bool ShouldRetryGhostLogin(string loginMessage, bool ghostPending, int failedAttempts, int maxAttempts = 3)
    {
        if (!ghostPending || failedAttempts >= maxAttempts) return false;
        var m = (loginMessage ?? "").ToLowerInvariant();
        return m.Contains("already logged in") || m.Contains("still logged in") || m.Contains("presence") || m.Contains("logging out") || m.Contains("in the process of logging");
    }

    // ---- a tool call finishes on its own ---------------------------------------------------------------------------
    /// <summary>Runs <paramref name="work"/> on its own task so a cancelled / dropped MCP call never stops it half way (login set-up);
    /// an exception becomes <paramref name="onError"/>'s text, never an unobserved task failure.</summary>
    public static async Task<string> RunDetached(Func<Task<string>> work, string what, Func<string, string> onError)
    {
        try { return await Task.Run(work).ConfigureAwait(false); }
        catch (Exception ex)
        {
            var b = ex.GetBaseException();
            CrashLine("error", $"{what} threw {b.GetType().Name}: {b.Message}");
            return onError($"{what} failed: {b.Message}");
        }
    }
}
