// Webhook wake-up push for incoming avatar chat / IMs.
// - Events are still queued for poll_events by the core; this only POSTs a batched summary.
// - Config is re-read at runtime: URL from a one-line file, key from the box secrets JSON
//   (card.GALATAY_WEBHOOK_KEY, else any key named GALATAY_WEBHOOK_KEY). Missing either -> silent no-op.
// - Batching: first event opens a ~15 s window; one POST per window; min 15 s between POSTs; max 120 POSTs/day.
// - One try, 8 s timeout, no retry. Failures append the JSON body to the failed log. The key is never logged.
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Core = GalatayText.Program;

namespace GalatayMcp;

public static class Webhook
{
    static string Env(string k, string d) => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(k)) ? d : Environment.GetEnvironmentVariable(k)!;
    static readonly string UrlFile = Env("GT_WEBHOOK_URL_FILE", "/home/box/viewers/textclient/webhook-url.txt");
    static readonly string SecretsFile = Env("GT_WEBHOOK_SECRETS", "/home/box/agent-data/box-secrets.json");
    static readonly string FailedLog = Env("GT_WEBHOOK_FAILED_LOG", "/workspace/secondlife/webhook-failed.log");
    const string KeyName = "GALATAY_WEBHOOK_KEY";
    public static TimeSpan Window = TimeSpan.FromSeconds(double.Parse(Env("GT_WEBHOOK_WINDOW_S", "15"), System.Globalization.CultureInfo.InvariantCulture));
    public static TimeSpan MinInterval = TimeSpan.FromSeconds(15);
    public const int DailyCap = 120;
    const int MaxEventsPerBatch = 50;
    const int MaxTextChars = 1000;
    static readonly TimeSpan ConfigRecheck = TimeSpan.FromSeconds(60);

    static readonly object gate = new();
    static readonly List<Ev> pending = new();
    static bool flushScheduled;
    static DateTime lastPost = DateTime.MinValue;
    static DateTime day = DateTime.Today;
    static int postsToday;
    static int droppedOverflow;
    static DateTime lastConfigCheck = DateTime.MinValue;
    static bool configuredCached;
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(8) };
    static readonly JsonSerializerOptions J = new() { WriteIndented = false };

    public sealed record Ev(string type, string from, string from_id, string text, string time, double? distance);

    public static void Init() => Core.OnIncoming = c => Enqueue(c.type, c.from, c.from_id, c.text, c.time, c.distance);

    static void LogLocal(string msg) => Core.Log("webhook", msg);

    // ---- config ------------------------------------------------------------
    static string ReadUrl()
    {
        try
        {
            if (!File.Exists(UrlFile)) return null;
            var line = File.ReadLines(UrlFile).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));
            if (line == null || !Uri.TryCreate(line, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http")) return null;
            return line;
        }
        catch { return null; }
    }
    static string ReadKey()
    {
        try
        {
            if (!File.Exists(SecretsFile)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(SecretsFile));
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("card", out var card) && card.ValueKind == JsonValueKind.Object
                && card.TryGetProperty(KeyName, out var k) && k.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(k.GetString()))
                return k.GetString()!.Trim();
            string found = null;
            void Walk(JsonElement e)
            {
                if (found != null) return;
                if (e.ValueKind == JsonValueKind.Object)
                    foreach (var p in e.EnumerateObject())
                    {
                        if (p.Name == KeyName && p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString())) { found = p.Value.GetString()!.Trim(); return; }
                        Walk(p.Value);
                    }
                else if (e.ValueKind == JsonValueKind.Array) foreach (var x in e.EnumerateArray()) Walk(x);
            }
            Walk(root);
            return found;
        }
        catch { return null; }
    }
    static (string url, string key) ReadConfig() { var u = ReadUrl(); var k = u == null ? null : ReadKey(); return (u, k); }

    static bool ConfiguredCached()
    {
        lock (gate)
        {
            if (DateTime.UtcNow - lastConfigCheck < ConfigRecheck) return configuredCached;
            lastConfigCheck = DateTime.UtcNow;
        }
        var (u, k) = ReadConfig();
        var ok = u != null && k != null;
        lock (gate) configuredCached = ok;
        return ok;
    }

    public static string ConfigSummary()
    {
        var u = ReadUrl(); var k = ReadKey();
        string host = null; if (u != null) try { host = new Uri(u).Host; } catch { }
        lock (gate)
        {
            if (DateTime.Today != day) { day = DateTime.Today; postsToday = 0; }
            return JsonSerializer.Serialize(new { url_configured = u != null, url_host = host, key_configured = k != null, posts_today = postsToday, daily_cap = DailyCap, pending = pending.Count }, J);
        }
    }

    // ---- batching ------------------------------------------------------------
    public static void Enqueue(string type, string from, string fromId, string text, string time, double? distance)
    {
        if (!ConfiguredCached()) return; // silent no-op until URL and key exist
        if (text != null && text.Length > MaxTextChars) text = text[..MaxTextChars] + "…";
        lock (gate)
        {
            if (pending.Count >= MaxEventsPerBatch) { droppedOverflow++; return; }
            pending.Add(new Ev(type, from, fromId, text, time, distance));
            if (flushScheduled) return;
            flushScheduled = true;
        }
        _ = Task.Run(FlushLoop);
    }

    static async Task FlushLoop()
    {
        try
        {
            // collect for the window after the first event, and respect the min interval between POSTs
            DateTime due;
            lock (gate) { var a = DateTime.UtcNow + Window; var b = lastPost + MinInterval; due = a > b ? a : b; }
            var wait = due - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
            List<Ev> batch; int overflow;
            lock (gate)
            {
                batch = new List<Ev>(pending); pending.Clear(); overflow = droppedOverflow; droppedOverflow = 0;
                flushScheduled = false;
                if (DateTime.Today != day) { day = DateTime.Today; postsToday = 0; }
                if (batch.Count == 0) return;
                if (postsToday >= DailyCap)
                {
                    LogLocal($"daily cap ({DailyCap} POSTs) reached; dropped batch of {batch.Count} event(s) (still in poll_events)");
                    return;
                }
                postsToday++; lastPost = DateTime.UtcNow;
            }
            var kinds = batch.Select(e => e.type).Distinct().ToList();
            var body = JsonSerializer.Serialize(new { kind = kinds.Count == 1 ? kinds[0] : "mixed", region = Core.RegionName, events = batch }, J);
            if (overflow > 0) LogLocal($"batch overflow: {overflow} extra event(s) not included in webhook body (still in poll_events)");
            var (status, err) = await Post(body, reReadConfig: true);
            LogLocal(status is >= 200 and < 300 ? $"POST ok ({status}) with {batch.Count} event(s)" : $"POST failed ({(status?.ToString() ?? err)}) with {batch.Count} event(s)");
        }
        catch (Exception ex)
        {
            lock (gate) flushScheduled = false;
            LogLocal("flush error: " + ex.GetType().Name);
        }
    }

    // returns (http status or null, error text). Appends body to failed log on any failure.
    static async Task<(int? status, string err)> Post(string body, bool reReadConfig)
    {
        var (url, key) = ReadConfig();
        lock (gate) { configuredCached = url != null && key != null; lastConfigCheck = DateTime.UtcNow; }
        if (url == null || key == null) return (null, "not configured");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8) };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            req.Headers.TryAddWithoutValidation("X-Automation-Key", key);
            using var resp = await http.SendAsync(req);
            var code = (int)resp.StatusCode;
            if (code < 200 || code >= 300) AppendFailed(body);
            return (code, null);
        }
        catch (Exception ex)
        {
            AppendFailed(body);
            return (null, ex is TaskCanceledException ? "timeout" : ex.GetType().Name);
        }
    }

    static void AppendFailed(string body)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(FailedLog)!); File.AppendAllText(FailedLog, body + "\n"); } catch { }
    }

    // ---- webhook_test tool ----------------------------------------------------
    public static async Task<string> Test()
    {
        var (url, key) = ReadConfig();
        if (url == null || key == null) return JsonSerializer.Serialize(new { ok = false, status = "not configured", url_configured = ReadUrl() != null, key_configured = ReadKey() != null }, J);
        lock (gate)
        {
            if (DateTime.Today != day) { day = DateTime.Today; postsToday = 0; }
            var since = DateTime.UtcNow - lastPost;
            if (since < MinInterval) return JsonSerializer.Serialize(new { ok = false, status = $"rate limited: retry in {(MinInterval - since).TotalSeconds:F0} s" }, J);
            if (postsToday >= DailyCap) return JsonSerializer.Serialize(new { ok = false, status = $"daily cap ({DailyCap}) reached" }, J);
            postsToday++; lastPost = DateTime.UtcNow;
        }
        var body = JsonSerializer.Serialize(new { kind = "test", region = Core.RegionName, events = Array.Empty<object>() }, J);
        var (status, err) = await Post(body, true);
        LogLocal($"test POST -> {(status?.ToString() ?? err)}");
        return JsonSerializer.Serialize(new { ok = status is >= 200 and < 300, status = (object)status ?? err, url_host = new Uri(url).Host }, J);
    }

    // ---- offline self-test (no SL login): galatay-mcp --webhook-selftest ------
    public static async Task<int> SelfTest()
    {
        Console.WriteLine("config: " + ConfigSummary());
        Init();
        void Fire(string type, string from, string text, double? d) =>
            Core.OnIncoming?.Invoke(new Core.IncomingChat(type, from, "00000000-0000-0000-0000-00000000000" + (from.Length % 10), text,
                DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"), d, "SelfTest"));
        var t0 = DateTime.Now;
        void Say(string s) => Console.WriteLine($"+{(DateTime.Now - t0).TotalSeconds,5:F1}s {s}");
        Say("fire local_chat A"); Fire("local_chat", "Alice Resident", "hello", 3.2);
        await Task.Delay(3000); Say("fire im B"); Fire("im", "Bob Resident", "hi via IM", null);
        await Task.Delay(3000); Say("fire local_chat C"); Fire("local_chat", "Carol Resident", "third", 7.5);
        await Task.Delay(Window + TimeSpan.FromSeconds(3));  // first POST due ~15 s after A
        Say("fire local_chat D (window 2)"); Fire("local_chat", "Dave Resident", "later", 1.0);
        await Task.Delay(Window + TimeSpan.FromSeconds(4));
        Say("webhook_test (expected rate limited or ok): " + await Test());
        await Task.Delay(MinInterval + TimeSpan.FromSeconds(1));
        Say("webhook_test: " + await Test());
        Console.WriteLine("config: " + ConfigSummary());
        return 0;
    }
}
