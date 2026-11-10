using System.Text.Json;
using GalatayMcp;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-11-09: chat relay went silent when box-secrets.json was unreadable by the box user
/// while GALATAY_WEBHOOK_KEY was already in the process env. Env-first key + non-silent warnings.</summary>
public class WebhookKeyConfigTests
{
    const string SampleKey = "test-webhook-key-value-not-real";
    static string CardJson(string key) => JsonSerializer.Serialize(new { card = new Dictionary<string, string> { ["GALATAY_WEBHOOK_KEY"] = key } });

    [Fact]
    public void ResolveKey_env_wins_over_file()
    {
        var k = Webhook.ResolveKey("  env-key  ", true, CardJson("file-key"), null, out var source, out var issue);
        Assert.Equal("env-key", k);
        Assert.Equal("env", source);
        Assert.Null(issue);
    }

    [Fact]
    public void ResolveKey_file_fallback_when_env_empty()
    {
        var k = Webhook.ResolveKey(null, true, CardJson(SampleKey), null, out var source, out var issue);
        Assert.Equal(SampleKey, k);
        Assert.Equal("file", source);
        Assert.Null(issue);

        k = Webhook.ResolveKey("   ", true, CardJson(SampleKey), null, out source, out issue);
        Assert.Equal(SampleKey, k);
        Assert.Equal("file", source);
    }

    [Fact]
    public void ResolveKey_unreadable_file_ok_when_env_set()
    {
        var k = Webhook.ResolveKey(SampleKey, true, null, "UnauthorizedAccessException", out var source, out var issue);
        Assert.Equal(SampleKey, k);
        Assert.Equal("env", source);
        Assert.Null(issue);
    }

    [Fact]
    public void ResolveKey_unreadable_file_without_env_reports_issue()
    {
        var k = Webhook.ResolveKey(null, true, null, "UnauthorizedAccessException", out var source, out var issue);
        Assert.Null(k);
        Assert.Null(source);
        Assert.Contains("unreadable", issue);
        Assert.Contains("UnauthorizedAccessException", issue);
        Assert.Contains("GALATAY_WEBHOOK_KEY", issue);
        Assert.DoesNotContain(SampleKey, issue);
    }

    [Fact]
    public void ResolveKey_missing_file_without_env_reports_issue()
    {
        var k = Webhook.ResolveKey("", false, null, null, out var source, out var issue);
        Assert.Null(k);
        Assert.Null(source);
        Assert.Contains("missing", issue);
    }

    [Fact]
    public void KeyFromSecretsJson_reads_card_and_nested()
    {
        Assert.Equal(SampleKey, Webhook.KeyFromSecretsJson(CardJson(SampleKey)));
        var nested = """{"other":{"GALATAY_WEBHOOK_KEY":"nested-key"}}""";
        Assert.Equal("nested-key", Webhook.KeyFromSecretsJson(nested));
        Assert.Null(Webhook.KeyFromSecretsJson("""{"card":{}}"""));
        Assert.Null(Webhook.KeyFromSecretsJson("not-json"));
    }

    [Fact]
    public void WarnNotConfiguredOnce_sets_warning_once_and_status_exposes_it()
    {
        Webhook.ResetWarningsForTests();
        Assert.Null(Webhook.LastConfigWarning);
        Webhook.WarnNotConfiguredOnce();
        var first = Webhook.LastConfigWarning;
        Assert.False(string.IsNullOrEmpty(first));
        Assert.Contains("not configured", first);
        Assert.DoesNotContain("Bearer", first);
        Webhook.WarnNotConfiguredOnce();
        Assert.Equal(first, Webhook.LastConfigWarning); // still the same one-time warning
        var summary = Webhook.ConfigSummary();
        Assert.Contains("config_warning", summary);
        Assert.Contains("not configured", summary);
    }

    [Fact]
    public void NotePostFailureOnce_sets_last_post_error_once()
    {
        Webhook.ResetWarningsForTests();
        Webhook.NotePostFailureOnce("400");
        Assert.Contains("400", Webhook.LastPostError);
        Assert.Contains("webhook-failed.log", Webhook.LastPostError);
        Webhook.NotePostFailureOnce("500");
        Assert.Contains("500", Webhook.LastPostError); // reason updates
        var summary = Webhook.ConfigSummary();
        Assert.Contains("last_post_error", summary);
    }

    [Fact]
    public void ReadKeyDetailed_uses_env_without_touching_unreadable_default_file()
    {
        Webhook.ResetWarningsForTests();
        var old = Environment.GetEnvironmentVariable("GALATAY_WEBHOOK_KEY");
        try
        {
            Environment.SetEnvironmentVariable("GALATAY_WEBHOOK_KEY", SampleKey);
            var k = Webhook.ReadKeyDetailed(out var source, out var issue);
            Assert.Equal(SampleKey, k);
            Assert.Equal("env", source);
            Assert.Null(issue);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GALATAY_WEBHOOK_KEY", old);
        }
    }
}
