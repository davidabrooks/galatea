// VmForkWatch.cs (2026-11-02): today's drops at 13:02:32 and 14:43:02 PT were the host pausing/snapshotting the box VM
// (kernel: "random: crng reseeded due to virtual machine fork"). SL formally timed the session out ~50 s after the fork,
// then the embedded auto-relogin waited another 30 s. Now:
//  - a dedicated thread polls the kernel's VM-fork count every 5 s (dmesg, readable as user box);
//  - when it rises while logged in it checks the session: a fresh sim packet within 8 s = healthy, else the dead
//    circuit is shut down at once (Disconnected -> NetworkTimeout -> embedded auto-relogin);
//  - a NetworkTimeout within 5 min of a fork uses a 5 s first relogin delay instead of 30 s.
// The relogin is the same LoginAsync as always (full normal login: veil, persisted stand cooldown, LoginFaceHim), and all
// existing cancellations (manual login/logout generation, deliberate-stop marker, loginGate) still apply.
using System.Diagnostics;
using System.Text.RegularExpressions;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    internal const string VmForkMarker = "virtual machine fork";
    internal const int VmForkPollSec = 5, VmForkHealthWaitSec = 8, VmForkRecentSec = 300, VmForkReloginDelaySec = 5;
    static Thread vmForkThread;
    static long lastVmForkTicks = long.MinValue / 2;   // Environment.TickCount64 when a fork increase was last seen
    internal static volatile string LastVmForkEvent;     // "fork N->M" of the last increase

    /// <summary>Pure (tested): number of VM-fork lines in a dmesg dump.</summary>
    internal static int CountVmForks(string dmesg)
        => string.IsNullOrEmpty(dmesg) ? 0 : Regex.Matches(dmesg, Regex.Escape(VmForkMarker)).Count;

    /// <summary>Pure (tested): did the count rise? (a drop = ring buffer rolled over: rebaseline, no event)</summary>
    internal static bool VmForkRose(int prev, int now) => prev >= 0 && now > prev;

    /// <summary>Pure (tested): was a fork seen recently enough that a NetworkTimeout is blamed on it?</summary>
    internal static bool VmForkRecent(long forkTicks, long nowTicks) => nowTicks - forkTicks >= 0 && nowTicks - forkTicks <= VmForkRecentSec * 1000L;

    /// <summary>Pure (tested): first embedded relogin delay. After a host VM snapshot: 5 s; otherwise the usual backoff.</summary>
    internal static TimeSpan EmbeddedReloginDelayAfter(int tryN, bool kicked, bool afterVmFork)
        => !kicked && afterVmFork && tryN == 1 ? TimeSpan.FromSeconds(VmForkReloginDelaySec) : EmbeddedReloginDelay(tryN, kicked);

    /// <summary>Pure (tested): after a fork, is the session dead? (no sim packet since the fork was seen, or not connected)</summary>
    internal static bool VmForkSessionDead(bool connected, long lastRecv, long forkSeenTicks) => !connected || lastRecv < forkSeenTicks;

    internal static string VmForkLabel(int prev, int now) => $"host VM snapshot (fork {prev}->{now})";

    static int ReadVmForkCount()
    {
        try
        {
            var psi = new ProcessStartInfo("dmesg") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi);
            var o = p!.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(3000)) { try { p.Kill(); } catch { } return -1; }
            return p.ExitCode == 0 ? CountVmForks(o) : -1;
        }
        catch { return -1; }
    }

    static void StartVmForkWatch()
    {
        if (Environment.GetEnvironmentVariable("GT_VMFORK_WATCH") == "0") return;
        lock (typeof(VmForkLock)) { if (vmForkThread != null) return; vmForkThread = new Thread(VmForkLoop) { IsBackground = true, Name = "vmfork" }; }
        vmForkThread.Start();
    }
    sealed class VmForkLock { }

    static void VmForkLoop()
    {
        int prev = ReadVmForkCount();
        Log("keepalive", prev < 0 ? "VM-fork watch: dmesg not readable, watch off" : $"VM-fork watch on (count {prev}, every {VmForkPollSec} s)");
        if (prev < 0) return;
        while (true)
        {
            Thread.Sleep(VmForkPollSec * 1000);
            int now = ReadVmForkCount();
            if (now < 0) continue;
            if (VmForkRose(prev, now))
            {
                var label = VmForkLabel(prev, now);
                var seen = Environment.TickCount64;
                Interlocked.Exchange(ref lastVmForkTicks, seen);
                LastVmForkEvent = $"{DateTime.Now:HH:mm:ss} {label}";
                if (LoggedIn && !shuttingDown) { Log("keepalive", $"{label} while logged in: checking the session"); _ = Task.Run(() => VmForkHealthCheck(label, seen)); }
                else Log("keepalive", $"{label} (not logged in)");
            }
            prev = now;
        }
    }

    static async Task VmForkHealthCheck(string label, long seen)
    {
        try
        {
            for (int i = 0; i < VmForkHealthWaitSec; i++)
            {
                await Task.Delay(1000);
                if (!LoggedIn || shuttingDown) { Log("keepalive", $"{label}: session already gone; auto-relogin handles it"); return; }
                if (!VmForkSessionDead(client?.Network?.Connected == true, Interlocked.Read(ref lastRecvTicks), seen)) { Log("keepalive", $"{label}: session alive (sim packets flowing)"); return; }
            }
            if (DeliberateStopRequested()) { Log("keepalive", $"{label}: session looks dead but deliberate-stop is set: no relogin"); return; }
            Log("keepalive", $"{label}: no sim packets for {VmForkHealthWaitSec} s -> dropping the dead circuit and relogging now");
            try { await client.Network.ShutdownAsync(NetworkManager.DisconnectType.NetworkTimeout, $"{label}: session dead"); } catch (Exception ex) { Log("keepalive", $"{label}: shutdown error {ex.GetBaseException().Message}"); }
        }
        catch (Exception ex) { Log("keepalive", $"{label}: health check error {ex.GetBaseException().Message}"); }
    }
}
