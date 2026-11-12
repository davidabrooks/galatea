// Login credential loading (2026-11-11 16:36 PT).
// First login in a process (and a login after a connector restart) reads SECONDLIFE_PASSWORD
// from the environment (the launcher injects it) and then clears that variable. Auto-relogin
// in the same process then fell through to File.ReadAllText(SecretsPath). The secrets file is
// mode 0600, owned by a different uid than the client, so the in-process read threw
// UnauthorizedAccessException (5/5 tries at 16:36-16:49 after a VM snapshot). The MCP login
// tool uses the same LoginAsync; it only succeeded at 16:51 because the connector had been
// restarted and the env var was injected again. Fix: cache the password in memory after the
// first successful load (never logged) so auto-relogin uses the same credentials; if the file
// must be read, retry with backoff and fall back to the cache rather than giving up.
using System.Text.Json;

namespace GalatayText;

public static partial class Program
{
    // Never log, print, or ToString this. Process-lifetime cache so a later LoginAsync
    // (auto-relogin) does not have to re-open the secrets file.
    internal sealed class LoginPasswordCache
    {
        string _value;
        internal bool HasValue => !string.IsNullOrEmpty(_value);
        internal string Peek() => _value;
        internal void Store(string v) { if (!string.IsNullOrEmpty(v)) _value = v; }
        internal void Clear() { _value = null; }
        public override string ToString() => "(hidden)";
    }

    static readonly LoginPasswordCache loginPasswordCache = new();

    internal static readonly int[] LoginSecretsFileBackoffMs = { 200, 500, 1000, 2000, 4000 };

    internal readonly record struct LoginPasswordResult(bool Ok, string Source, string Value, string ErrorType, int FileAttempts)
    {
        // Default record ToString would include Value (the password). Never do that.
        public override string ToString() =>
            $"(Ok={Ok}, Source={Source}, ErrorType={ErrorType}, FileAttempts={FileAttempts})";
    }

    // Pure: pick a password from env (cleared after use), the secrets file (retried), or a
    // previous in-memory cache. Value is the password; tests must not assert the live secret,
    // only fixture values they themselves supplied.
    internal static LoginPasswordResult LoadLoginPassword(
        Func<string> getEnv,
        Action clearEnv,
        Func<string> readFileText,
        string secretKey,
        LoginPasswordCache cache,
        int fileRetries,
        IReadOnlyList<int> backoffMs,
        Action<int> sleepMs)
    {
        cache ??= new LoginPasswordCache();
        var env = getEnv?.Invoke();
        if (!string.IsNullOrEmpty(env))
        {
            cache.Store(env);
            try { clearEnv?.Invoke(); } catch { }
            return new LoginPasswordResult(true, "env", env, null, 0);
        }

        Exception last = null;
        int attempts = 0;
        int retries = Math.Max(1, fileRetries);
        for (int i = 0; i < retries; i++)
        {
            if (i > 0)
            {
                int ms = 0;
                if (backoffMs != null && backoffMs.Count > 0)
                    ms = backoffMs[Math.Min(i - 1, backoffMs.Count - 1)];
                if (ms > 0) sleepMs?.Invoke(ms);
            }
            attempts++;
            try
            {
                var text = readFileText?.Invoke();
                var parsed = ParsePasswordFromSecretsJson(text, secretKey);
                if (!string.IsNullOrEmpty(parsed))
                {
                    cache.Store(parsed);
                    return new LoginPasswordResult(true, "file", parsed, null, attempts);
                }
                // File was readable but had no password: do not keep retrying as an access failure.
                if (cache.HasValue)
                    return new LoginPasswordResult(true, "cache", cache.Peek(), "empty", attempts);
                return new LoginPasswordResult(false, "file", null, "empty", attempts);
            }
            catch (Exception ex) when (IsSecretsReadException(ex))
            {
                last = ex;
            }
        }

        if (cache.HasValue)
            return new LoginPasswordResult(true, "cache", cache.Peek(), last?.GetType().Name, attempts);

        return new LoginPasswordResult(false, null, null, last?.GetType().Name ?? "Exception", attempts);
    }

    internal static bool IsSecretsReadException(Exception ex) =>
        ex is UnauthorizedAccessException or IOException;

    // Pure: walk a secrets JSON document the same way ReadPassword does (dotted key, then leaf search).
    // Never logs the value.
    internal static string ParsePasswordFromSecretsJson(string json, string secretKey)
    {
        if (string.IsNullOrEmpty(json)) return "";
        using var doc = JsonDocument.Parse(json);
        JsonElement cur = doc.RootElement;
        bool ok = true;
        var key = string.IsNullOrEmpty(secretKey) ? "card.SECONDLIFE_PASSWORD" : secretKey;
        foreach (var part in key.Split('.'))
        {
            if (cur.ValueKind == JsonValueKind.Object && cur.TryGetProperty(part, out var nx)) cur = nx;
            else { ok = false; break; }
        }
        if (ok && cur.ValueKind == JsonValueKind.String) return cur.GetString() ?? "";
        var leaf = key.Split('.').Last();
        string found = null;
        void Walk(JsonElement e)
        {
            if (found != null) return;
            if (e.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Name == leaf && p.Value.ValueKind == JsonValueKind.String) { found = p.Value.GetString(); return; }
                    Walk(p.Value);
                }
            }
            else if (e.ValueKind == JsonValueKind.Array) foreach (var x in e.EnumerateArray()) Walk(x);
        }
        Walk(doc.RootElement);
        return found ?? "";
    }

    internal static bool IsSecretsReadFailure(string result) =>
        !string.IsNullOrEmpty(result) && result.Contains("cannot read secrets file", StringComparison.OrdinalIgnoreCase);

    // After a secrets-read failure, retry sooner than the normal 30-300 s login backoff
    // (each of those tries used to fail instantly on the file). Caps at 30 s.
    internal static TimeSpan EmbeddedReloginSecretsDelay(int tryN) =>
        TimeSpan.FromSeconds(Math.Min(30, Math.Max(2, 2 * Math.Pow(2, Math.Max(0, tryN - 1)))));

    internal static TimeSpan EmbeddedReloginDelayAfterFailure(int tryN, bool kicked, bool afterFork, string lastResult)
        => !kicked && IsSecretsReadFailure(lastResult)
            ? EmbeddedReloginSecretsDelay(tryN)
            : EmbeddedReloginDelayAfter(tryN, kicked, afterFork);
}
