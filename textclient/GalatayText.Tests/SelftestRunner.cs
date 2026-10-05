using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace GalatayText.Tests;

/// <summary>Runs galatay-text --*-selftest offline (no SL login, no secrets).</summary>
public static class SelftestRunner
{
    static readonly Regex FailLine = new(@"(?m)^FAIL\b|[1-9]\d*\s+fail\b|[1-9]\d*\s+FAIL\b", RegexOptions.Compiled);

    public static string FindExe()
    {
        var env = Environment.GetEnvironmentVariable("GALATAY_TEXT_EXE");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        var here = AppContext.BaseDirectory;
        foreach (var rel in new[]
                 {
                     Path.Combine(here, "galatay-text"),
                     Path.Combine(here, "..", "..", "..", "..", "app-staging", "galatay-text"),
                     Path.Combine(here, "..", "..", "..", "..", "app-staging", "galatay-text.dll"),
                     Path.GetFullPath(Path.Combine(here, "..", "..", "..", "..", "..", "app-staging", "galatay-text")),
                 })
        {
            var p = Path.GetFullPath(rel);
            if (File.Exists(p)) return p;
        }
        var root = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE")
                   ?? Path.GetFullPath(Path.Combine(here, "..", "..", "..", "..", ".."));
        var staging = Path.Combine(root, "textclient", "app-staging", "galatay-text");
        if (File.Exists(staging)) return staging;
        throw new FileNotFoundException(
            "galatay-text executable not found. Build with `bash textclient/build-all.sh` or set GALATAY_TEXT_EXE.");
    }

    public static (int exit, string output) Run(string flag, int timeoutMs = 120_000)
    {
        var exe = FindExe();
        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (exe.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = "dotnet";
            psi.ArgumentList.Add(exe);
            psi.ArgumentList.Add(flag);
        }
        else
        {
            psi.FileName = exe;
            psi.ArgumentList.Add(flag);
        }
        // No secrets: empty/placeholder env so Program does not look for box-secrets
        psi.Environment["GT_SOCK"] = Path.Combine(Path.GetTempPath(), "galatay-ci-test.sock");
        psi.Environment["GT_LOG"] = Path.Combine(Path.GetTempPath(), "galatay-ci-test.log");
        using var p = Process.Start(psi)!;
        var sb = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"selftest {flag} exceeded {timeoutMs} ms");
        }
        return (p.ExitCode, sb.ToString());
    }

    public static void AssertPass(string flag, int timeoutMs = 120_000)
    {
        var (exit, output) = Run(flag, timeoutMs);
        Assert.True(exit == 0, $"exit {exit} for {flag}\n{output}");
        Assert.False(FailLine.IsMatch(output), $"FAIL lines in {flag}\n{output}");
    }
}
