using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using GalatayText;
using GalatayMcp;
using Xunit;

// 2026-11-22: the in-process xUnit run wrote into the LIVE /workspace/secondlife/textclient.log ("pending auto-relogin cancelled
// (test: disconnect seen)", "login threw InvalidOperationException: boom", webhook warnings), interleaved with her real session
// (Nov 11 11:12:51, Nov 20 10:41:31, Nov 22 13:22). The webhook code could also read the live URL file and POST to it.
// Before any Program static field initialises, point the log, the webhook config, and the secrets at per-run temp
// paths (an explicit env from the caller wins), the same way SelftestRunner already does for the child process.
static class HermeticTestEnv
{
    internal static readonly string Dir = Path.Combine(Path.GetTempPath(), $"galatay-xunit-{Environment.ProcessId}");

    [ModuleInitializer]
    internal static void Init()
    {
        Directory.CreateDirectory(Dir);
        void Set(string k, string v) { if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(k))) Environment.SetEnvironmentVariable(k, v); }
        Set("GT_LOG", Path.Combine(Dir, "textclient.log"));
        Set("GT_WEBHOOK_URL_FILE", Path.Combine(Dir, "missing-webhook-url.txt"));
        Set("GT_WEBHOOK_SECRETS", Path.Combine(Dir, "missing-webhook-secrets.json"));
        Set("GT_SECRETS", Path.Combine(Dir, "missing-secrets.json"));
        Environment.SetEnvironmentVariable("GALATAY_WEBHOOK_KEY", null);
        Environment.SetEnvironmentVariable("XAI_API_KEY", null);
    }
}

public class HermeticTestEnvTests
{
    static string StaticString(Type t, string name) => (string)t.GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    [Fact] public void TheTestRun_NeverWritesTheLiveLog()
    {
        var log = StaticString(typeof(Program), "LogPath");
        Assert.NotEqual("/workspace/secondlife/textclient.log", log);
        Assert.StartsWith(Path.GetTempPath(), log);
    }

    [Fact] public void TheTestRun_NeverReadsTheLiveWebhookOrSecrets()
    {
        Assert.StartsWith(Path.GetTempPath(), StaticString(typeof(Webhook), "UrlFile"));
        Assert.StartsWith(Path.GetTempPath(), StaticString(typeof(Webhook), "SecretsFile"));
        Assert.DoesNotContain("box-secrets.json", Environment.GetEnvironmentVariable("GT_SECRETS"));
    }
}
