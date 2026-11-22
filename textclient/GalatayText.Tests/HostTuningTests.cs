using GalatayText;
using Xunit;

// HostTuning.cs (2026-11-22): net.core.rmem_max is re-applied at every start because a box reboot resets it to 212992.
public class HostTuningTests
{
    const long Want = 16L * 1024 * 1024;
    static string Src(string rel) => File.ReadAllText(Path.Combine(FindTextclient(), rel));
    static string FindTextclient()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "galatay-text", "HostTuning.cs"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("textclient");
    }

    [Fact] public void Wants_sixteen_megabytes_matching_the_sysctl_conf()
    {
        Assert.Equal(Want, Program.RmemMaxWanted);
        Assert.True(Program.RmemMaxWanted >= Program.UdpSockBufWanted);
        Assert.Contains("net.core.rmem_max = " + Want, File.ReadAllText(Path.Combine(FindTextclient(), "..", "scripts", "sysctl-galatay-udp.conf")));
    }

    [Fact] public void Already_high_enough_runs_nothing_and_logs_nothing()
    {
        int runs = 0;
        Assert.Null(Program.EnsureRmemMax(() => Want, (f, a) => { runs++; return (0, ""); }, "/usr/sbin/sysctl"));
        Assert.Null(Program.EnsureRmemMax(() => 64L << 20, (f, a) => { runs++; return (0, ""); }, "/usr/sbin/sysctl"));   // never lowered
        Assert.Equal(0, runs);
    }

    [Fact] public void Plain_sysctl_first_then_sudo_n()
    {
        long v = 212992; var calls = new List<string>();
        var line = Program.EnsureRmemMax(() => v, (f, a) =>
        {
            calls.Add(f + " " + string.Join(' ', a));
            if (f == "sudo") { v = Want; return (0, ""); }
            return (255, "sysctl: permission denied on key \"net.core.rmem_max\"");
        }, "/usr/sbin/sysctl");
        Assert.Equal(new[] { "/usr/sbin/sysctl -q -w net.core.rmem_max=16777216", "sudo -n /usr/sbin/sysctl -q -w net.core.rmem_max=16777216" }, calls);
        Assert.Equal("net.core.rmem_max raised 212992 -> 16777216 (sudo -n sysctl -w)", line);
    }

    [Fact] public void Plain_sysctl_success_skips_sudo()
    {
        long v = 212992; var calls = new List<string>();
        var line = Program.EnsureRmemMax(() => v, (f, a) => { calls.Add(f); v = Want; return (0, ""); }, "/sbin/sysctl");
        Assert.Single(calls);
        Assert.Contains("(sysctl -w)", line);
    }

    [Fact] public void Failure_is_a_warning_and_never_throws()
    {
        var line = Program.EnsureRmemMax(() => 212992, (f, a) => f == "sudo" ? throw new System.ComponentModel.Win32Exception("no sudo") : (1, "permission denied\nmore"), "sysctl");
        Assert.StartsWith("WARNING: could not raise net.core.rmem_max from 212992 to 16777216", line);
        Assert.Contains("sysctl -w: rc 1 permission denied;", line);
        Assert.Contains("sudo -n sysctl -w: rc -1 Win32Exception", line);
        Assert.Contains("client continues", line);
        Assert.DoesNotContain("more", line);
    }

    [Fact] public void Rc_zero_but_value_unchanged_counts_as_failure()
    {
        var line = Program.EnsureRmemMax(() => 212992, (f, a) => (0, ""), "sysctl");   // e.g. a read-only /proc/sys in a container
        Assert.StartsWith("WARNING", line);
    }

    [Fact] public void Unreadable_value_changes_nothing()
    {
        int runs = 0;
        var line = Program.EnsureRmemMax(() => throw new IOException("no /proc"), (f, a) => { runs++; return (0, ""); }, "sysctl");
        Assert.StartsWith("WARNING: net.core.rmem_max unreadable", line);
        Assert.Equal(0, runs);
    }

    [Fact] public void Sysctl_path_prefers_sbin_then_falls_back_to_PATH()
    {
        Assert.Equal("/usr/sbin/sysctl", Program.SysctlPath(p => p is "/usr/sbin/sysctl" or "/sbin/sysctl"));
        Assert.Equal("/sbin/sysctl", Program.SysctlPath(p => p == "/sbin/sysctl"));
        Assert.Equal("sysctl", Program.SysctlPath(_ => false));
    }

    [Fact] public void Wired_into_daemon_connector_and_login_with_captured_stdout()
    {
        var ht = Src("galatay-text/HostTuning.cs");
        Assert.Contains("RedirectStandardOutput = true", ht);   // the connector's stdout is MCP JSON-RPC only
        Assert.Contains("RedirectStandardError = true", ht);
        Assert.Contains("\"-n\"", ht);                           // sudo never prompts
        Assert.Contains("EnsureRmemMaxAtStart();", Src("galatay-text/Program.cs"));
        Assert.Contains("Core.EnsureRmemMaxAtStart();", Src("galatay-mcp/McpProgram.cs"));
        Assert.Contains("EnsureRmemMaxAtStart();", Src("galatay-text/UdpDrops.cs"));   // CheckUdpRcvbufLimit runs at every login
        Assert.Contains("galatay-text/HostTuning.cs", Src("galatay-mcp/GalatayMcp.csproj"));
    }
}
