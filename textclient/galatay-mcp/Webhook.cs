// Webhook wake-up push for incoming avatar chat / IMs.
// - Events are still queued for poll_events by the core; this only POSTs a batched summary.
// - Config is re-read at runtime: URL from a one-line file, key from the box secrets JSON
//   (card.GALATAY_WEBHOOK_KEY, else any key named GALATAY_WEBHOOK_KEY). Missing either -> silent no-op.
// - Debouncing (2026-10-02, David: Ryan got two near-identical IMs): events are held PER CONVERSATION (an IM sender's id, or
//   local chat as one conversation) until that conversation has been quiet for GT_WEBHOOK_QUIET_S (default 20 s), capped at
//   GT_WEBHOOK_MAX_S (default 60 s) after its first held line; then ONE POST carries all its lines (other conversations that are
//   due at the same moment ride along). Min GT_WEBHOOK_MIN_INTERVAL_S (default 15 s) between POSTs; max 120 POSTs/day.
//   Urgent kinds (teleport_offer, friendship_offer, region_restart) are POSTed at once, bypassing debounce and min interval.
//   Runtime: 'webhook debounce [<quiet s> [<max s>]]'.
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
    static double EnvS(string k, double d) => double.TryParse(Environment.GetEnvironmentVariable(k), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : d;
    public static TimeSpan Quiet = TimeSpan.FromSeconds(EnvS("GT_WEBHOOK_QUIET_S", 20));
    public static TimeSpan MaxHold = TimeSpan.FromSeconds(EnvS("GT_WEBHOOK_MAX_S", 60));
    public static TimeSpan MinInterval = TimeSpan.FromSeconds(EnvS("GT_WEBHOOK_MIN_INTERVAL_S", 15));
    public static readonly HashSet<string> UrgentKinds = new() { "teleport_offer", "friendship_offer", "region_restart" };
    public const int DailyCap = 120;
    const int MaxEventsPerBatch = 50;
    const int MaxTextChars = 1000;
    static readonly TimeSpan ConfigRecheck = TimeSpan.FromSeconds(60);

    static readonly object gate = new();
    sealed class Conv { public List<Ev> Evs = new(); public DateTime First, Last; }
    static readonly Dictionary<string, Conv> convs = new();
    static bool loopRunning;
    static int PendingCount() { lock (gate) return convs.Values.Sum(c => c.Evs.Count); }
    // test hook: replaces the HTTP POST (SelfTest)
    public static Func<string, Task<(int? status, string err)>> PostOverride;
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
            return JsonSerializer.Serialize(new { url_configured = u != null, url_host = host, key_configured = k != null, posts_today = postsToday, daily_cap = DailyCap, pending = convs.Values.Sum(c => c.Evs.Count), conversations_held = convs.Count, quiet_s = Quiet.TotalSeconds, max_hold_s = MaxHold.TotalSeconds, min_interval_s = MinInterval.TotalSeconds }, J);
        }
    }

    // ---- debounced batching ------------------------------------------------------
    static string ConvKey(string type, string fromId) => type == "local_chat" ? "local_chat" : $"{type}:{fromId}";

    public static void Enqueue(string type, string from, string fromId, string text, string time, double? distance)
    {
        if (PostOverride == null && !ConfiguredCached()) return; // silent no-op until URL and key exist
        if (text != null && text.Length > MaxTextChars) text = text[..MaxTextChars] + "…";
        var ev = new Ev(type, from, fromId, text, time, distance);
        if (UrgentKinds.Contains(type)) { _ = Task.Run(() => PostBatch(new List<Ev> { ev }, 0, urgent: true)); return; }
        var now = DateTime.UtcNow;
        lock (gate)
        {
            var k = ConvKey(type, fromId);
            if (!convs.TryGetValue(k, out var c)) convs[k] = c = new Conv { First = now };
            if (c.Evs.Count >= MaxEventsPerBatch) { droppedOverflow++; }
            else c.Evs.Add(ev);
            c.Last = now;
            if (loopRunning) return;
            loopRunning = true;
        }
        _ = Task.Run(FlushLoop);
    }

    // pure (unit-tested): is a held conversation due?
    public static bool Due(DateTime first, DateTime last, DateTime now, TimeSpan quiet, TimeSpan maxHold) => now - last >= quiet || now - first >= maxHold;

    static async Task FlushLoop()
    {
        try
        {
            while (true)
            {
                await Task.Delay(500);
                List<Ev> batch = null; int overflow = 0, nconv = 0;
                lock (gate)
                {
                    if (convs.Count == 0) { loopRunning = false; return; }
                    var now = DateTime.UtcNow;
                    var due = convs.Where(kv => Due(kv.Value.First, kv.Value.Last, now, Quiet, MaxHold)).Select(kv => kv.Key).ToList();
                    if (due.Count == 0 || now - lastPost < MinInterval) continue;
                    batch = new List<Ev>();
                    foreach (var k in due) { batch.AddRange(convs[k].Evs); convs.Remove(k); }
                    nconv = due.Count; overflow = droppedOverflow; droppedOverflow = 0;
                }
                await PostBatch(batch, overflow, urgent: false, nconv);
            }
        }
        catch (Exception ex)
        {
            lock (gate) loopRunning = false;
            LogLocal("flush error: " + ex.GetType().Name);
        }
    }

    static async Task PostBatch(List<Ev> batch, int overflow, bool urgent, int nconv = 1)
    {
        if (batch.Count == 0) return;
        if (PostOverride == null) lock (gate)
        {
            if (DateTime.Today != day) { day = DateTime.Today; postsToday = 0; }
            if (postsToday >= DailyCap) { LogLocal($"daily cap ({DailyCap} POSTs) reached; dropped batch of {batch.Count} event(s) (still in poll_events)"); return; }
            postsToday++; lastPost = DateTime.UtcNow;
        }
        var kinds = batch.Select(e => e.type).Distinct().ToList();
        var body = JsonSerializer.Serialize(new { kind = kinds.Count == 1 ? kinds[0] : "mixed", urgent, region = Core.RegionName, conversations = nconv, batched = batch.Count, events = batch }, J);
        if (overflow > 0) LogLocal($"batch overflow: {overflow} extra event(s) not included in webhook body (still in poll_events)");
        var (status, err) = PostOverride != null ? await PostOverride(body) : await Post(body, reReadConfig: true);
        LogLocal(status is >= 200 and < 300 ? $"POST ok ({status}) with {batch.Count} event(s) from {nconv} conversation(s){(urgent ? " [urgent]" : "")}" : $"POST failed ({(status?.ToString() ?? err)}) with {batch.Count} event(s)");
    }

    public static string DebounceCmd(string[] a)
    {
        if (a.Length >= 1 && double.TryParse(a[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var q) && q >= 0 && q <= 600)
        {
            Quiet = TimeSpan.FromSeconds(q);
            if (a.Length >= 2 && double.TryParse(a[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m) && m >= q && m <= 1800) MaxHold = TimeSpan.FromSeconds(m);
            if (MaxHold < Quiet) MaxHold = Quiet;
            LogLocal($"debounce set: quiet {Quiet.TotalSeconds} s, max hold {MaxHold.TotalSeconds} s (runtime only; env GT_WEBHOOK_QUIET_S / GT_WEBHOOK_MAX_S for restarts)");
        }
        lock (gate) return $"webhook debounce: quiet {Quiet.TotalSeconds} s, max hold {MaxHold.TotalSeconds} s, min interval {MinInterval.TotalSeconds} s; urgent (immediate): {string.Join(", ", UrgentKinds)}; held now: {convs.Count} conversation(s), {convs.Values.Sum(c => c.Evs.Count)} line(s)";
    }

    // in-process self-test with a fake POST (no network, no SL); short windows so it runs in ~15 s
    public static async Task<string> DebounceSelfTest()
    {
        var posts = new List<string>(); var t0 = DateTime.UtcNow;
        var (q0, m0, i0, p0) = (Quiet, MaxHold, MinInterval, PostOverride);
        var sb = new System.Text.StringBuilder();
        bool busy; lock (gate) busy = convs.Count > 0 || loopRunning;
        if (busy) return "debounce selftest: real events are held right now; try again in a minute";
        Quiet = TimeSpan.FromSeconds(2); MaxHold = TimeSpan.FromSeconds(5); MinInterval = TimeSpan.Zero;
        PostOverride = b => { lock (posts) posts.Add($"+{(DateTime.UtcNow - t0).TotalSeconds:F1}s {b}"); return Task.FromResult<(int?, string)>((200, null)); };
        int pass = 0, fail = 0; void Check(bool ok, string what) { if (ok) pass++; else fail++; sb.AppendLine($"{(ok ? "PASS" : "FAIL")} {what}"); }
        try
        {
            string T() => DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");
            // 1) burst of 4 IM lines from one sender, 0.5 s apart -> ONE post with 4 events, ~2 s after the last
            for (int i = 0; i < 4; i++) { Enqueue("im", "Test Sender", "11111111-1111-1111-1111-111111111111", $"line {i + 1}", T(), null); await Task.Delay(500); }
            await Task.Delay(3500);
            lock (posts) Check(posts.Count == 1 && posts[0].Contains("\"batched\":4"), $"burst of 4 IM lines -> {posts.Count} POST(s) (want 1 with 4 events)");
            // 2) urgent teleport offer -> immediate
            int before; lock (posts) before = posts.Count;
            Enqueue("teleport_offer", "Test Sender", "11111111-1111-1111-1111-111111111111", "join me", T(), null);
            await Task.Delay(300);
            lock (posts) Check(posts.Count == before + 1 && posts[^1].Contains("\"urgent\":true"), "urgent teleport_offer POSTed within 0.3 s");
            // 3) steady trickle every 1.5 s (never quiet 2 s) -> forced out by the 5 s cap
            lock (posts) before = posts.Count; var s0 = DateTime.UtcNow;
            for (int i = 0; i < 5; i++) { Enqueue("local_chat", "Chatty Resident", "22222222-2222-2222-2222-222222222222", $"chat {i}", T(), 3); await Task.Delay(1500); }
            await Task.Delay(2600);
            lock (posts) Check(posts.Count - before >= 2 && posts.Count - before <= 3, $"trickle never quiet -> capped at max hold: {posts.Count - before} POSTs for 5 lines over 7.5 s (want 2)");
            // 4) two senders at once -> separate conversations, both due together -> one POST with 2 conversations
            lock (posts) before = posts.Count;
            Enqueue("im", "A Resident", "33333333-3333-3333-3333-333333333333", "hi", T(), null);
            Enqueue("im", "B Resident", "44444444-4444-4444-4444-444444444444", "hello", T(), null);
            await Task.Delay(3200);
            lock (posts) Check(posts.Count == before + 1 && posts[^1].Contains("\"conversations\":2"), "two senders due together -> 1 POST, conversations=2");
        }
        finally { Quiet = q0; MaxHold = m0; MinInterval = i0; PostOverride = p0; }
        lock (posts) foreach (var p in posts) sb.AppendLine("  " + (p.Length > 220 ? p[..220] + "…" : p));
        return $"debounce selftest: {pass} PASS, {fail} FAIL\n" + sb.ToString().TrimEnd();
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
        await Task.Delay(Quiet + TimeSpan.FromSeconds(3));
        Say("fire local_chat D (window 2)"); Fire("local_chat", "Dave Resident", "later", 1.0);
        await Task.Delay(Quiet + TimeSpan.FromSeconds(4));
        Say("webhook_test (expected rate limited or ok): " + await Test());
        await Task.Delay(MinInterval + TimeSpan.FromSeconds(1));
        Say("webhook_test: " + await Test());
        Console.WriteLine("config: " + ConfigSummary());
        return 0;
    }
}
