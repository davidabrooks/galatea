using System;
using System.Collections.Generic;
using GalatayText;
using Xunit;

// 2026-11-11 16:36: auto-relogin could not re-read the secrets file (UnauthorizedAccessException)
// after the first login had already taken SECONDLIFE_PASSWORD from the environment and cleared it.
// A later MCP login in a *new* process worked because the launcher injected the env var again.
public class LoginSecretsTests
{
    const string Key = "card.SECONDLIFE_PASSWORD";
    const string FixtureJson = """{"card":{"SECONDLIFE_PASSWORD":"fixture-pw-not-live"}}""";

    [Fact]
    public void Parse_dotted_key_and_leaf_fallback()
    {
        Assert.Equal("fixture-pw-not-live", Program.ParsePasswordFromSecretsJson(FixtureJson, Key));
        Assert.Equal("nested", Program.ParsePasswordFromSecretsJson("""{"other":{"SECONDLIFE_PASSWORD":"nested"}}""", Key));
        Assert.Equal("", Program.ParsePasswordFromSecretsJson("""{"card":{}}""", Key));
    }

    [Fact]
    public void Env_is_used_then_cleared_and_cached_for_relogin()
    {
        var cache = new Program.LoginPasswordCache();
        bool cleared = false;
        int fileReads = 0;
        var first = Program.LoadLoginPassword(
            () => "from-env",
            () => cleared = true,
            () => { fileReads++; throw new UnauthorizedAccessException(); },
            Key, cache, 5, Program.LoginSecretsFileBackoffMs, _ => { });
        Assert.True(first.Ok);
        Assert.Equal("env", first.Source);
        Assert.Equal("from-env", first.Value);
        Assert.True(cleared);
        Assert.Equal(0, fileReads);
        Assert.True(cache.HasValue);

        // 16:36 path: env already cleared, file unreadable, relogin must use the cache.
        var relog = Program.LoadLoginPassword(
            () => null,
            () => { },
            () => { fileReads++; throw new UnauthorizedAccessException(); },
            Key, cache, 5, Program.LoginSecretsFileBackoffMs, _ => { });
        Assert.True(relog.Ok);
        Assert.Equal("cache", relog.Source);
        Assert.Equal("from-env", relog.Value);
        Assert.Equal(nameof(UnauthorizedAccessException), relog.ErrorType);
        Assert.Equal(5, relog.FileAttempts);
        Assert.Equal(5, fileReads);
    }

    [Fact]
    public void File_read_is_retried_with_backoff_then_succeeds()
    {
        var cache = new Program.LoginPasswordCache();
        var sleeps = new List<int>();
        int n = 0;
        var r = Program.LoadLoginPassword(
            () => null,
            () => { },
            () =>
            {
                n++;
                if (n < 3) throw new UnauthorizedAccessException();
                return FixtureJson;
            },
            Key, cache, 5, Program.LoginSecretsFileBackoffMs, sleeps.Add);
        Assert.True(r.Ok);
        Assert.Equal("file", r.Source);
        Assert.Equal("fixture-pw-not-live", r.Value);
        Assert.Equal(3, r.FileAttempts);
        Assert.Equal(new[] { 200, 500 }, sleeps);
        Assert.True(cache.HasValue);
    }

    [Fact]
    public void File_failure_with_no_cache_is_not_ok()
    {
        var cache = new Program.LoginPasswordCache();
        var sleeps = new List<int>();
        var r = Program.LoadLoginPassword(
            () => null,
            () => { },
            () => throw new UnauthorizedAccessException(),
            Key, cache, 5, Program.LoginSecretsFileBackoffMs, sleeps.Add);
        Assert.False(r.Ok);
        Assert.Null(r.Value);
        Assert.Equal(nameof(UnauthorizedAccessException), r.ErrorType);
        Assert.Equal(5, r.FileAttempts);
        Assert.Equal(new[] { 200, 500, 1000, 2000 }, sleeps);
        Assert.False(cache.HasValue);
    }

    [Fact]
    public void Cache_and_logs_never_contain_the_password()
    {
        var cache = new Program.LoginPasswordCache();
        var r = Program.LoadLoginPassword(() => "from-env", () => { }, () => null, Key, cache, 1, Array.Empty<int>(), _ => { });
        Assert.Equal("(hidden)", cache.ToString());
        Assert.DoesNotContain("from-env", cache.ToString());
        Assert.DoesNotContain("from-env", r.ToString());
        Assert.Contains("Source=env", r.ToString());
    }

    [Fact]
    public void Auto_relogin_uses_a_short_backoff_after_a_secrets_read_failure()
    {
        Assert.True(Program.IsSecretsReadFailure("FAILED: cannot read secrets file"));
        Assert.False(Program.IsSecretsReadFailure("OK in 3.8s: region Peronaut"));
        Assert.Equal(2, Program.EmbeddedReloginSecretsDelay(1).TotalSeconds);
        Assert.Equal(4, Program.EmbeddedReloginSecretsDelay(2).TotalSeconds);
        Assert.Equal(30, Program.EmbeddedReloginSecretsDelay(5).TotalSeconds);
        // After a secrets failure, try 2 is 4 s, not the normal 60 s.
        Assert.Equal(TimeSpan.FromSeconds(4), Program.EmbeddedReloginDelayAfterFailure(2, false, false, "FAILED: cannot read secrets file"));
        Assert.Equal(Program.EmbeddedReloginDelayAfter(2, false, false), Program.EmbeddedReloginDelayAfterFailure(2, false, false, "FAILED: something else"));
        Assert.Equal(Program.EmbeddedReloginDelayAfter(1, false, true), Program.EmbeddedReloginDelayAfterFailure(1, false, true, null));
        // A kick still waits the long kick delay.
        Assert.Equal(Program.EmbeddedReloginDelayAfter(1, true, false), Program.EmbeddedReloginDelayAfterFailure(1, true, false, "FAILED: cannot read secrets file"));
    }
}
