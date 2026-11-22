// net.core.rmem_max re-apply at every start (2026-11-22).
// PR 473 raised net.core.rmem_max to 16 MB by hand on 2026-11-20 so the 8 MB UDP SO_RCVBUF (UdpDrops.cs) is granted, but the
// 2026-11-22 2:34 PM PT box restart put it back to 212992: this box has no systemd, and /etc/sysctl.d is re-created from the
// image at boot, so scripts/setup-host.sh's sysctl.d file never survives. Linux caps SO_RCVBUF at rmem_max without an error.
// Fix: the client itself raises it when it starts (daemon Main via CheckUdpRcvbufLimit, the MCP connector's Main, and again
// before every login), before the UDP socket is opened. Order: plain `sysctl -w` (works only as root / CAP_NET_ADMIN), then
// `sudo -n sysctl -w` (non-interactive: fails at once if sudo wants a password, never prompts). Nothing is lowered, a failure
// is only a [net] WARNING and the client carries on (the 212 KB default still works, just with more risk of lost chat).
// stdout/stderr of the helper are captured, never inherited: the MCP connector's stdout is the JSON-RPC channel.
using System.Diagnostics;

namespace GalatayText;

public static partial class Program
{
    internal const long RmemMaxWanted = 16L * 1024 * 1024;   // scripts/sysctl-galatay-udp.conf
    const string RmemMaxProc = "/proc/sys/net/core/rmem_max";

    /// <summary>Pure: the sysctl binary to run (first existing of the usual paths, else "sysctl" from PATH).</summary>
    internal static string SysctlPath(Func<string, bool> exists)
    {
        foreach (var p in new[] { "/usr/sbin/sysctl", "/sbin/sysctl", "/usr/bin/sysctl", "/bin/sysctl" }) if (exists(p)) return p;
        return "sysctl";
    }

    /// <summary>Pure: the commands tried in order to set rmem_max to <paramref name="want"/>: plain sysctl, then sudo -n sysctl.</summary>
    internal static List<(string File, string[] Args)> RmemRaiseCommands(string sysctl, long want)
    {
        var kv = $"net.core.rmem_max={want}";
        return new() { (sysctl, new[] { "-q", "-w", kv }), ("sudo", new[] { "-n", sysctl, "-q", "-w", kv }) };
    }

    /// <summary>Raise net.core.rmem_max to at least <paramref name="want"/>. Never throws, never lowers it.
    /// read: current value (-1 = unreadable); run: (file, args) -> (exit code, error text). Returns the log line (null when already fine).</summary>
    internal static string EnsureRmemMax(Func<long> read, Func<string, string[], (int Rc, string Err)> run, string sysctl, long want = RmemMaxWanted)
    {
        long cur;
        try { cur = read(); } catch { cur = -1; }
        if (cur < 0) return "WARNING: net.core.rmem_max unreadable; not changed (client continues)";
        if (cur >= want) return null;
        var errs = new List<string>();
        foreach (var (file, args) in RmemRaiseCommands(sysctl, want))
        {
            (int Rc, string Err) r;
            try { r = run(file, args); } catch (Exception ex) { r = (-1, ex.GetType().Name + ": " + ex.Message); }
            long now; try { now = read(); } catch { now = -1; }
            var how = file == "sudo" ? "sudo -n sysctl -w" : "sysctl -w";
            if (r.Rc == 0 && now >= want) return $"net.core.rmem_max raised {cur} -> {now} ({how})";
            errs.Add($"{how}: rc {r.Rc}{(string.IsNullOrWhiteSpace(r.Err) ? "" : " " + r.Err.Trim().Split('\n')[0])}");
        }
        return $"WARNING: could not raise net.core.rmem_max from {cur} to {want} ({string.Join("; ", errs)}); client continues, "
             + "UDP receive buffer stays capped. Fix by hand: sudo scripts/setup-host.sh";
    }

    static long ReadRmemMax() => long.TryParse(File.ReadAllText(RmemMaxProc).Trim(), out var v) ? v : -1;

    static (int Rc, string Err) RunQuiet(string file, string[] args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p == null) return (-1, "could not start " + file);
        p.StandardInput.Close();                               // sudo -n never prompts; a closed stdin makes sure
        var err = p.StandardError.ReadToEndAsync(); _ = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(5000)) { try { p.Kill(true); } catch { } return (-1, "timed out after 5 s"); }
        return (p.ExitCode, err.Wait(1000) ? err.Result : "");
    }

    /// <summary>Process start + before each login: re-apply rmem_max (lost at every box reboot). Logs only when it acted or failed.</summary>
    internal static void EnsureRmemMaxAtStart()
    {
        try
        {
            if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("GT_SKIP_RMEM") == "1") return;
            var line = EnsureRmemMax(ReadRmemMax, RunQuiet, SysctlPath(File.Exists));
            if (line == null) return;
            Log("net", line);
            if (line.StartsWith("WARNING", StringComparison.Ordinal)) Console.Error.WriteLine("[net] " + line);
        }
        catch (Exception ex) { try { Log("net", "WARNING: rmem_max re-apply failed: " + ex.Message + " (client continues)"); } catch { } }
    }
}
