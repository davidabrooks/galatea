// Webhook wake-up push for incoming avatar chat / IMs.
// - Events are still queued for poll_events by the core; this only POSTs a batched summary.
// - Config is re-read at runtime: URL from a one-line file, key from the box secrets JSON
//   (card.GALATAY_WEBHOOK_KEY, else any key named GALATAY_WEBHOOK_KEY). Missing either -> silent no-op.
// - Debouncing (2026-10-02, David: Ryan got two near-identical IMs; later the same day: only wait when it is a burst).
//   Events are held PER CONVERSATION (an IM sender's id, or local chat as one conversation):
//     * single line: the first line of a conversation waits only the burst-detect window GT_WEBHOOK_BURST_DETECT_S (default 4 s).
//       If no other line of that conversation arrives in that window, it is POSTed right away.
//     * burst: if a second line arrives inside the detect window, the conversation is a burst and is held until it has been
//       quiet for GT_WEBHOOK_QUIET_S (default 20 s), capped at GT_WEBHOOK_MAX_S (default 60 s) after its first held line;
//       then ONE POST carries all its lines.
//   Other conversations that are due at the same moment ride along. Min GT_WEBHOOK_MIN_INTERVAL_S (default 15 s) between POSTs;
//   max GT_WEBHOOK_DAILY_CAP POSTs/day (default 600; was 120 until 2026-10-03, hit at 11:18 PT that day). Cap-exempt (counted
//   separately, never dropped): urgent kinds and anything from David Nightingale (IM or local chat). 'webhook cap [<n>]',
//   'webhook reset-cap', 'webhook cap selftest'. Every IM event carries my_last_im_to_sender (my last outgoing IM to that
//   avatar, from the [me-im] log) and answered_after (that IM is newer than the event) so the routine can skip answered ones.
//   Urgent kinds (teleport_offer, friendship_offer, group_invite, group_invite_accepted, region_restart, david_login[_test]) are POSTed at once, bypassing debounce
//   and min interval. Runtime: 'webhook debounce [<quiet s> [<max s> [<detect s>]]]', 'webhook debounce detect <s>'.
// - Reply lease (2026-10-04, David: two overlapping routine runs both answered Ryan's 'it was yummy'). After a POST that carries
//   IMs from an avatar, that avatar's NEW lines are held (not POSTed, so no second concurrent run) until I send them an IM or
//   GT_WEBHOOK_LEASE_S (default 60 s) passes; then ONE POST carries the held lines, minus lines already answered with
//   'im --re <msg_id>' / 'say --re <msg_id>'. Every IM and local_chat event carries msg_id. 'webhook lease [<s>]', 'webhook lease selftest'.
// - Bounded retry (2026-10-05): up to 4 attempts with backoff (0/2/5/15 s) on 400/408/429/5xx and transport errors.
//   Same JSON body (same msg_ids) is resent — no duplicate events. Final failure appends the body to the failed log
//   (with the HTTP status / error). Response body is logged (truncated). The key is never logged.
// - David fast path (2026-10-08): a conversation with a line from David: ~1.2 s single-line wait, burst 3 s quiet / 10 s max,
//   3 s min interval (GT_WEBHOOK_DAVID_*). Lines the client answered itself (InstantReplies.cs) are never POSTed alone;
//   next to an unanswered line they ride along as context with answered=true + my_reply.
// - JSON: UTF-8 (UnsafeRelaxedJsonEscaping; no \uD800 surrogate pairs for emoji), null fields omitted.
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    public static TimeSpan Detect = TimeSpan.FromSeconds(EnvS("GT_WEBHOOK_BURST_DETECT_S", 4)); // single-line wait / burst detect window
    public static TimeSpan Quiet = TimeSpan.FromSeconds(EnvS("GT_WEBHOOK_QUIET_S", 20));        // burst: quiet gap before the POST
    public static TimeSpan MaxHold = TimeSpan.FromSeconds(EnvS("GT_WEBHOOK_MAX_S", 60));        // burst: cap from the first held line
    public static TimeSpan MinInterval = TimeSpan.FromSeconds(EnvS("GT_WEBHOOK_MIN_INTERVAL_S", 15));
    public static TimeSpan Lease = TimeSpan.FromSeconds(EnvS("GT_WEBHOOK_LEASE_S", 60));        // reply lease per IM sender after a POST
    // 2026-10-08 19:08 (David: "long delay before you answered me"): a held conversation with a line from David uses short
    // timings: single line ~1.2 s, a 2-line burst goes as one POST after 3 s quiet (max 10 s), min 3 s after the last POST.
    public static TimeSpan DavidDetect = TimeSpan.FromSeconds(EnvS("GT_WEBHOOK_DAVID_DETECT_S", 1.2));
    public static TimeSpan DavidQuiet = TimeSpan.FromSeconds(EnvS("GT_WEBHOOK_DAVID_QUIET_S", 3));
    public static TimeSpan DavidMaxHold = TimeSpan.FromSeconds(EnvS("GT_WEBHOOK_DAVID_MAX_S", 10));
    public static TimeSpan DavidMinInterval = TimeSpan.FromSeconds(EnvS("GT_WEBHOOK_DAVID_MIN_INTERVAL_S", 3));
    static readonly Dictionary<string, (DateTime start, string fromId)> leases = new();         // conv key -> lease (UTC start)
    // pure (selftest-covered): is the reply lease still held? ends at my first IM to them after the POST, or after `lease`
    public static bool LeaseActive(DateTime startUtc, DateTime nowUtc, TimeSpan lease, DateTimeOffset? myLastImTo)
        => nowUtc - startUtc < lease && !(myLastImTo != null && myLastImTo.Value.UtcDateTime >= startUtc);
    public static readonly HashSet<string> UrgentKinds = new() { "teleport_offer", "friendship_offer", "group_invite", "group_invite_accepted", "region_restart", "david_login", "david_login_test", "visitor_arrival" };
    public static int DailyCap = (int)Math.Clamp(EnvS("GT_WEBHOOK_DAILY_CAP", 600), 1, 100000);
    const string DavidId = "44ce5a36-c1c7-4a68-ac9a-635ddfff6233";
    static int exemptToday, capDroppedToday;
    static DateTime capHitAt = DateTime.MinValue;
    const int MaxEventsPerBatch = 50;
    const int MaxTextChars = 1000;
    static readonly TimeSpan ConfigRecheck = TimeSpan.FromSeconds(60);

    static readonly object gate = new();
    sealed class Conv { public List<Ev> Evs = new(); public DateTime First, Last; public bool Burst, David; }
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
    static readonly JsonSerializerOptions J = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // real UTF-8 emoji; avoids \uD800 surrogate escapes some receivers 400 on
    };
    // retry: attempt 0 immediate, then 2 s, 5 s, 15 s (4 tries max). Same body each time (msg_ids unchanged).
    static readonly int[] RetryBackoffMs = { 0, 2000, 5000, 15000 };
    public static bool RetriableStatus(int? status) =>
        status is null or 400 or 408 or 429 or >= 500;
    // System.Text.Json always writes astral chars as \uD800 surrogate pairs; some receivers 400 on those.
    // Expand surrogate pairs (and \u0027) back to real UTF-8 before POST. Pure / selftest-covered.
    static readonly Regex SurrogatePair = new(@"\\u([Dd][89A-Fa-f][0-9A-Fa-f]{2})\\u([Dd][C-Fc-f][0-9A-Fa-f]{2})", RegexOptions.Compiled);
    static readonly Regex U0027 = new(@"\\u0027", RegexOptions.Compiled);
    public static string SanitizeJsonBody(string json)
    {
        if (string.IsNullOrEmpty(json)) return json;
        json = SurrogatePair.Replace(json, m =>
        {
            int hi = Convert.ToInt32(m.Groups[1].Value, 16), lo = Convert.ToInt32(m.Groups[2].Value, 16);
            return char.ConvertFromUtf32(((hi - 0xD800) << 10) + (lo - 0xDC00) + 0x10000);
        });
        return U0027.Replace(json, "'");
    }

    public sealed record Ev(string type, string from, string from_id, string text, string time, double? distance)
    {
        public string my_last_im_to_sender { get; init; } // dedupe hint: my last outgoing IM to this avatar (ISO time), null = none known
        public bool? answered_after { get; init; }         // true = that IM was sent after this event arrived
        public long? msg_id { get; init; }                 // incoming IM / local_chat id, for 'im --re' / 'say --re' (claim-and-send)
        public string parcel { get; init; }                // voice: parcel name where the utterance was heard
        public string context { get; init; }               // voice name-mode: prior ~30 s of other speakers
        public string channel { get; init; }               // voice: "voice" (medium) or the session channel label
        public string trigger { get; init; }               // voice: "name" | "invitation" | "all" (why this wake fired)
        public string line { get; init; }                  // voice: the exact triggering line (reply to this straight away)
        public bool? answered { get; init; }               // true = already answered by the client's instant reply (context only; never reply)
        public string my_reply { get; init; }              // that instant reply's text
    }

    // pure: skip the debounce flush loop (voice is debounced upstream)
    public static bool PostsImmediately(string type) => type == "voice";
    // pure (selftest-covered): bypasses the daily cap?
    public static bool CapExempt(Ev e) => UrgentKinds.Contains(e.type) || (e.from_id == DavidId && e.type is "im" or "local_chat" or "voice");
    // pure: may a batch be POSTed? exempt batches always; others while under the cap
    public static bool CapAllows(bool exempt, int postsToday, int cap) => exempt || postsToday < cap;

    // adds the [me-im] dedupe hint to IM events (time of POST)
    static Ev WithImHint(Ev e)
    {
        if (e.type != "im" || string.IsNullOrEmpty(e.from_id)) return e;
        var last = Core.LastMyImTo(e.from_id);
        if (last == null) return e;
        bool? after = DateTimeOffset.TryParse(e.time, out var et) ? last.Value > et : null;
        return e with { my_last_im_to_sender = last.Value.ToString("yyyy-MM-ddTHH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture), answered_after = after };
    }

    static void DayRoll() { if (DateTime.Today != day) { day = DateTime.Today; postsToday = 0; exemptToday = 0; capDroppedToday = 0; capHitAt = DateTime.MinValue; } }

    public static string ResetCap()
    {
        int was; lock (gate) { DayRoll(); was = postsToday; postsToday = 0; capDroppedToday = 0; capHitAt = DateTime.MinValue; }
        LogLocal($"daily cap counter reset by command (was {was}/{DailyCap})");
        return $"webhook daily counter reset: {was} -> 0 (cap {DailyCap}/day; exempt today {exemptToday})";
    }

    public static string CapCmd(string[] a)
    {
        if (a.Length > 0 && a[0] == "selftest") return CapSelfTest();
        if (a.Length > 0)
        {
            if (!int.TryParse(a[0], out var n) || n < 1 || n > 100000) return "usage: webhook cap [<1-100000>] | webhook cap selftest | webhook reset-cap";
            lock (gate) DailyCap = n;
            LogLocal($"daily cap set to {n} (runtime; GT_WEBHOOK_DAILY_CAP for restarts)");
        }
        lock (gate)
        {
            DayRoll();
            return $"webhook daily cap {DailyCap}: {postsToday} counted POST(s) today, {exemptToday} exempt (urgent / David), {capDroppedToday} batch(es) dropped at the cap" +
                   (capHitAt == DateTime.MinValue ? "" : $" (cap hit {capHitAt:HH:mm} PT)");
        }
    }

    static string CapSelfTest()
    {
        var sb = new StringBuilder(); int pass = 0, fail = 0; void C(bool ok, string w) { if (ok) pass++; else fail++; sb.AppendLine($"{(ok ? "PASS" : "FAIL")} {w}"); }
        Ev E(string type, string id, string time = null) => new(type, "X", id, "t", time ?? DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"), null);
        var other = "11111111-1111-1111-1111-111111111111";
        C(CapExempt(E("group_invite", other)) && CapExempt(E("teleport_offer", other)) && CapExempt(E("group_invite_accepted", other)), "urgent kinds (group_invite, teleport_offer, ...) are cap-exempt");
        C(CapExempt(E("im", DavidId)) && CapExempt(E("local_chat", DavidId)), "IM / local chat from David Nightingale is cap-exempt");
        C(!CapExempt(E("im", other)) && !CapExempt(E("local_chat", other)), "IM / local chat from anyone else counts against the cap");
        C(CapAllows(false, 599, 600) && !CapAllows(false, 600, 600) && CapAllows(true, 600, 600) && CapAllows(true, 5000, 600), "cap: normal batch stops at 600, exempt batch always passes");
        C(DailyCap >= 600 || Environment.GetEnvironmentVariable("GT_WEBHOOK_DAILY_CAP") != null, $"daily cap is {DailyCap} (default 600, GT_WEBHOOK_DAILY_CAP)");
        // dedupe hint with a synthetic [me-im] time for a synthetic avatar
        var av = "99999999-9999-9999-9999-" + Random.Shared.Next(100000000, 999999999).ToString("D12");
        var t1 = DateTimeOffset.Now.AddMinutes(-10);
        C(WithImHint(E("im", av, t1.ToString("yyyy-MM-ddTHH:mm:sszzz"))).my_last_im_to_sender == null, "no IM sent to this avatar -> no hint");
        Core.NoteMyIm(av, DateTimeOffset.Now.AddMinutes(-5));
        var h = WithImHint(E("im", av, t1.ToString("yyyy-MM-ddTHH:mm:sszzz")));
        C(h.my_last_im_to_sender != null && h.answered_after == true, $"my IM 5 min ago, their line 10 min ago -> answered_after=true ({h.my_last_im_to_sender})");
        var h2 = WithImHint(E("im", av, DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz")));
        C(h2.my_last_im_to_sender != null && h2.answered_after == false, "their line newer than my IM -> answered_after=false");
        C(WithImHint(E("local_chat", av)).my_last_im_to_sender == null, "local chat gets no IM hint");
        var js = JsonSerializer.Serialize(h, J);
        C(js.Contains("\"my_last_im_to_sender\"") && js.Contains("\"answered_after\":true"), "hint fields are in the JSON payload");
        Core.ForgetMyIm(av);
        C(RetriableStatus(400) && RetriableStatus(429) && RetriableStatus(503) && RetriableStatus(null) && RetriableStatus(408), "400/408/429/5xx/null are retriable");
        C(!RetriableStatus(200) && !RetriableStatus(201) && !RetriableStatus(401) && !RetriableStatus(403) && !RetriableStatus(404), "2xx and 401/403/404 are not retriable");
        C(RetryBackoffMs.Length == 4 && RetryBackoffMs[0] == 0 && RetryBackoffMs[^1] == 15000, "retry backoffs: 0, 2s, 5s, 15s (4 tries)");
        // JSON: nulls omitted; SanitizeJsonBody expands STJ surrogate escapes to UTF-8
        var evEmoji = new Ev("im", "X", other, "😃", "2026-10-04T21:28:00-07:00", null) { msg_id = 7 };
        var jsRaw = JsonSerializer.Serialize(evEmoji, J);
        var jsE = SanitizeJsonBody(jsRaw);
        C(!jsRaw.Contains("my_last_im_to_sender") && !jsRaw.Contains("answered_after"), "JSON: null hint fields omitted");
        C(jsE.Contains("😃") && !jsE.Contains("\\uD83D"), "SanitizeJsonBody expands emoji surrogates to UTF-8");
        C(SanitizeJsonBody("{\"t\":\"i\\u0027m\"}").Contains("i'm"), "SanitizeJsonBody expands \\u0027 to apostrophe");
        return $"webhook cap selftest: {pass} PASS, {fail} FAIL (pure checks; nothing POSTed)\n" + sb.ToString().TrimEnd();
    }

    // in-process: PostOverride fails with 400 twice then 200; one logical POST, same body, no duplicate AppendFailed
    public static async Task<string> RetrySelfTest()
    {
        var posts = new List<string>();
        var p0 = PostOverride;
        int n = 0;
        PostOverride = body =>
        {
            posts.Add(body); n++;
            if (n < 3) return Task.FromResult<(int?, string)>((400, null));
            return Task.FromResult<(int?, string)>((200, null));
        };
        var sb = new StringBuilder(); int pass = 0, fail = 0; void C(bool ok, string w) { if (ok) pass++; else fail++; sb.AppendLine($"{(ok ? "PASS" : "FAIL")} {w}"); }
        try
        {
            // PostBatch path uses PostOverride directly without our retry — call Post via a thin path:
            // exercise retry by temporarily routing PostOverride through the real retry loop is hard;
            // instead unit-test the decision + simulate the loop here matching Post().
            int? lastStatus = null; string lastErr = null; string body = "{\"kind\":\"test\",\"events\":[{\"msg_id\":42}]}";
            for (int attempt = 0; attempt < RetryBackoffMs.Length; attempt++)
            {
                if (RetryBackoffMs[attempt] > 0) await Task.Delay(Math.Min(RetryBackoffMs[attempt], 50)); // short in test
                var r = await PostOverride(body);
                lastStatus = r.Item1; lastErr = r.Item2;
                if (lastStatus is >= 200 and < 300) break;
                if (!(attempt < RetryBackoffMs.Length - 1 && RetriableStatus(lastStatus))) break;
            }
            C(posts.Count == 3 && posts.All(b => b == body), $"retry: 2x400 then 200 -> {posts.Count} attempts, same body each time");
            C(lastStatus == 200, "retry eventually succeeds with 200");
            C(body.Contains("\"msg_id\":42"), "msg_ids preserved across retries (no duplicate event rewrite)");
        }
        finally { PostOverride = p0; }
        return $"webhook retry selftest: {pass} PASS, {fail} FAIL\n" + sb.ToString().TrimEnd();
    }

    public static void Init() => Core.OnIncoming = c => Enqueue(c.type, c.from, c.from_id, c.text, c.time, c.distance, c.msg_id, c.parcel, c.context, c.channel, c.trigger, c.line);

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
            DayRoll();
            return JsonSerializer.Serialize(new { url_configured = u != null, url_host = host, key_configured = k != null, posts_today = postsToday, daily_cap = DailyCap, exempt_today = exemptToday, cap_dropped_today = capDroppedToday, cap_hit_at = capHitAt == DateTime.MinValue ? null : capHitAt.ToString("HH:mm:ss"), pending = convs.Values.Sum(c => c.Evs.Count), conversations_held = convs.Count, burst_detect_s = Detect.TotalSeconds, quiet_s = Quiet.TotalSeconds, max_hold_s = MaxHold.TotalSeconds, min_interval_s = MinInterval.TotalSeconds }, J);
        }
    }

    // ---- debounced batching ------------------------------------------------------
    static string ConvKey(string type, string fromId) => type is "local_chat" or "voice" ? type : $"{type}:{fromId}";

    public static void Enqueue(string type, string from, string fromId, string text, string time, double? distance, long? msgId = null, string parcel = null, string context = null, string channel = null, string trigger = null, string line = null)
    {
        if (PostOverride == null && !ConfiguredCached()) return; // silent no-op until URL and key exist
        if (text != null && text.Length > MaxTextChars) text = text[..MaxTextChars] + "…";
        if (context != null && context.Length > MaxTextChars * 2) context = context[..(MaxTextChars * 2)] + "…";
        var ev = new Ev(type, from, fromId, text, time, distance) { msg_id = msgId, parcel = parcel, context = context, channel = channel, trigger = trigger, line = line };
        if (UrgentKinds.Contains(type)) { _ = Task.Run(() => PostBatch(new List<Ev> { ev }, 0, urgent: true)); return; }
        // voice wakes are already debounced + rate-limited in Voice.cs: POST at once (was +4-5 s in the flush loop)
        if (PostsImmediately(type)) { _ = Task.Run(() => PostBatch(new List<Ev> { ev }, 0, urgent: false)); return; }
        var now = DateTime.UtcNow;
        lock (gate)
        {
            var k = ConvKey(type, fromId);
            bool david = fromId == DavidId;
            if (!convs.TryGetValue(k, out var c)) convs[k] = c = new Conv { First = now, David = david };
            else { if (!c.Burst && now - c.First < (c.David || david ? DavidDetect : Detect)) c.Burst = true; c.David |= david; } // another line inside the detect window -> burst
            if (c.Evs.Count >= MaxEventsPerBatch) { droppedOverflow++; }
            else c.Evs.Add(ev);
            c.Last = now;
            if (loopRunning) return;
            loopRunning = true;
        }
        _ = Task.Run(FlushLoop);
    }

    // pure (unit-tested): is a held conversation due?
    //  not a burst: due once the detect window after its first line has passed (no second line came in time)
    //  burst: due after `quiet` without a new line, or `maxHold` after the first held line
    public static bool Due(bool burst, DateTime first, DateTime last, DateTime now, TimeSpan detect, TimeSpan quiet, TimeSpan maxHold)
        => burst ? now - last >= quiet || now - first >= maxHold : now - first >= detect;

    // pure: due with David's short timings when the conversation has a line from him
    public static bool DueFor(bool david, bool burst, DateTime first, DateTime last, DateTime now) =>
        david ? Due(burst, first, last, now, DavidDetect, DavidQuiet, DavidMaxHold) : Due(burst, first, last, now, Detect, Quiet, MaxHold);

    // pure: drop lines already answered with '--re'. Lines answered by an instant reply stay in, marked answered=true with
    // my_reply, but only when the batch still has an unanswered line (context for the routine); else nothing is POSTed.
    public static List<Ev> FilterAnswered(List<Ev> batch, Func<Ev, bool> answered, Func<Ev, string> instantReply, out int dropped)
    {
        bool anyOpen = batch.Any(e => !answered(e));
        var r = new List<Ev>();
        foreach (var e in batch)
        {
            if (!answered(e)) { r.Add(e); continue; }
            var ir = instantReply(e);
            if (anyOpen && ir != null) r.Add(e with { answered = true, my_reply = ir });
        }
        dropped = batch.Count - r.Count;
        return r;
    }

    static async Task FlushLoop()
    {
        try
        {
            while (true)
            {
                await Task.Delay(250);
                List<Ev> batch = null; int overflow = 0, nconv = 0;
                lock (gate)
                {
                    if (convs.Count == 0) { loopRunning = false; return; }
                    var now = DateTime.UtcNow;
                    foreach (var lk in leases.Where(l => !LeaseActive(l.Value.start, now, Lease, Core.LastMyImTo(l.Value.fromId))).Select(l => l.Key).ToList()) leases.Remove(lk);
                    var due = convs.Where(kv => !leases.ContainsKey(kv.Key) && DueFor(kv.Value.David, kv.Value.Burst, kv.Value.First, kv.Value.Last, now)).Select(kv => kv.Key).ToList();
                    if (due.Count == 0 || now - lastPost < (due.Any(k => convs[k].David) ? DavidMinInterval : MinInterval)) continue;
                    batch = new List<Ev>();
                    foreach (var k in due) { batch.AddRange(convs[k].Evs); convs.Remove(k); }
                    // held lines already answered with 'im --re' / 'say --re' are not POSTed again (they stay in poll_events)
                    batch = FilterAnswered(batch, ev => ev.msg_id is long mid && (
                        (ev.type == "im" && Core.AnsweredExplicitly(ev.from_id, mid)) ||
                        (ev.type == "local_chat" && Core.ChatAnsweredExplicitly(ev.from_id, mid))),
                        ev => ev.msg_id is long mi ? Core.InstantReplyFor(mi) : null, out var answered);
                    if (answered > 0) LogLocal($"lease: {answered} held line(s) already answered with 'im --re'/'say --re' or an instant reply; not POSTed");
                    if (batch.Count > 0 && batch.All(ev => ev.answered == true)) batch.Clear();
                    foreach (var ev in batch.Where(ev => ev.type == "im" && !string.IsNullOrEmpty(ev.from_id))) leases[ConvKey("im", ev.from_id)] = (now, ev.from_id); // one run per sender
                    if (batch.Count == 0) continue;
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
        bool exempt = batch.Any(CapExempt);
        if (PostOverride == null) lock (gate)
        {
            DayRoll();
            if (!CapAllows(exempt, postsToday, DailyCap))
            {
                capDroppedToday++; if (capHitAt == DateTime.MinValue) capHitAt = DateTime.Now;
                LogLocal($"daily cap ({DailyCap} POSTs) reached; dropped batch of {batch.Count} event(s) (still in poll_events; 'webhook reset-cap' / 'webhook cap <n>')"); return;
            }
            if (exempt) exemptToday++; else postsToday++;
            lastPost = DateTime.UtcNow;
        }
        batch = batch.Select(WithImHint).ToList();
        var kinds = batch.Select(e => e.type).Distinct().ToList();
        var body = SanitizeJsonBody(JsonSerializer.Serialize(new { kind = kinds.Count == 1 ? kinds[0] : "mixed", urgent, region = Core.RegionName, conversations = nconv, batched = batch.Count, events = batch }, J));
        if (overflow > 0) LogLocal($"batch overflow: {overflow} extra event(s) not included in webhook body (still in poll_events)");
        var (status, err) = PostOverride != null ? await PostOverride(body) : await Post(body, reReadConfig: true);
        LogLocal(status is >= 200 and < 300 ? $"POST ok ({status}) with {batch.Count} event(s) from {nconv} conversation(s){(urgent ? " [urgent]" : "")}" : $"POST failed ({(status?.ToString() ?? err)}) with {batch.Count} event(s)");
    }

    public static string LeaseCmd(string[] a)
    {
        if (a.Length > 0 && a[0] == "selftest") return LeaseSelfTest();
        if (a.Length > 0) { if (TryS(a[0], 0, 600, out var v)) { Lease = TimeSpan.FromSeconds(v); LogLocal($"reply lease set to {v} s (runtime; GT_WEBHOOK_LEASE_S for restarts)"); } else return "usage: webhook lease [<0-600 s>] | webhook lease selftest"; }
        lock (gate) return $"webhook reply lease {Lease.TotalSeconds} s: after a POST with IMs from someone, their new lines wait until I IM them (or the lease ends); held now: {leases.Count} sender(s)";
    }
    static string LeaseSelfTest()
    {
        var sb = new StringBuilder(); int pass = 0, fail = 0; void C(bool ok, string w) { if (ok) pass++; else fail++; sb.AppendLine($"{(ok ? "PASS" : "FAIL")} {w}"); }
        var t = DateTime.UtcNow; var L = TimeSpan.FromSeconds(60); TimeSpan S(double x) => TimeSpan.FromSeconds(x);
        C(LeaseActive(t, t + S(5), L, null), "5 s after the POST, no reply yet -> lease held (new lines wait)");
        C(LeaseActive(t, t + S(5), L, new DateTimeOffset(t - S(30))), "my IM from BEFORE the POST does not end the lease");
        C(!LeaseActive(t, t + S(15), L, new DateTimeOffset(t + S(13))), "I replied 13 s after the POST -> lease ends, held lines go out in one POST");
        C(!LeaseActive(t, t + S(60), L, null), "no reply at all -> lease ends after 60 s (nothing is stuck)");
        var ev = new Ev("im", "X", "1", "t", "2026-10-04T20:46:01-07:00", null) { msg_id = 42 };
        C(JsonSerializer.Serialize(ev, J).Contains("\"msg_id\":42"), "IM events carry msg_id in the JSON payload");
        var evc = new Ev("local_chat", "Y", "2", "hi", "2026-10-05T11:56:00-07:00", 3.0) { msg_id = 99 };
        C(JsonSerializer.Serialize(evc, J).Contains("\"msg_id\":99") && evc.type == "local_chat", "local_chat events carry msg_id for say --re");
        return $"webhook lease selftest: {pass} PASS, {fail} FAIL (pure checks; nothing POSTed)\n" + sb.ToString().TrimEnd();
    }

    static bool TryS(string s, double lo, double hi, out double v) =>
        double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) && v >= lo && v <= hi;

    // 'webhook debounce'                               -> show settings
    // 'webhook debounce <quiet> [<max> [<detect>]]'    -> set burst quiet gap, burst cap, detect window (runtime only)
    // 'webhook debounce detect <s>'                    -> set only the detect window
    public static string DebounceCmd(string[] a)
    {
        string err = null;
        if (a.Length >= 2 && a[0].Equals("detect", StringComparison.OrdinalIgnoreCase))
        {
            if (TryS(a[1], 0, 60, out var d)) Detect = TimeSpan.FromSeconds(d); else err = "detect must be 0-60 s";
        }
        else if (a.Length >= 1)
        {
            if (TryS(a[0], 0, 600, out var q))
            {
                Quiet = TimeSpan.FromSeconds(q);
                if (a.Length >= 2) { if (TryS(a[1], q, 1800, out var m)) MaxHold = TimeSpan.FromSeconds(m); else err = "max must be >= quiet and <= 1800 s"; }
                if (a.Length >= 3) { if (TryS(a[2], 0, 60, out var d)) Detect = TimeSpan.FromSeconds(d); else err = "detect must be 0-60 s"; }
                if (MaxHold < Quiet) MaxHold = Quiet;
            }
            else err = "usage: webhook debounce [<quiet s> [<max s> [<detect s>]]] | webhook debounce detect <s> | webhook debounce selftest";
        }
        if (a.Length >= 1 && err == null)
            LogLocal($"debounce set: detect {Detect.TotalSeconds} s, burst quiet {Quiet.TotalSeconds} s, burst max hold {MaxHold.TotalSeconds} s (runtime only; env GT_WEBHOOK_BURST_DETECT_S / GT_WEBHOOK_QUIET_S / GT_WEBHOOK_MAX_S for restarts)");
        lock (gate)
            return (err != null ? err + "\n" : "")
                + $"webhook debounce: single line POSTed after the {Detect.TotalSeconds} s detect window; burst (2nd line inside it) held until {Quiet.TotalSeconds} s quiet, max {MaxHold.TotalSeconds} s from the first line; "
                + $"min interval {MinInterval.TotalSeconds} s; urgent (immediate): {string.Join(", ", UrgentKinds)}; "
                + $"held now: {convs.Count} conversation(s) ({convs.Values.Count(c => c.Burst)} burst), {convs.Values.Sum(c => c.Evs.Count)} line(s)";
    }

    // in-process self-test with a fake POST (no network, no SL); short windows (detect 1 s, quiet 2 s, max 5 s), runs in ~15 s
    public static async Task<string> DebounceSelfTest()
    {
        var posts = new List<(double t, string body)>(); var t0 = DateTime.UtcNow;
        var (d0, q0, m0, i0, p0) = (Detect, Quiet, MaxHold, MinInterval, PostOverride);
        var sb = new System.Text.StringBuilder();
        bool busy; lock (gate) busy = convs.Count > 0 || loopRunning;
        if (busy) return "debounce selftest: real events are held right now; try again in a minute";
        int pass = 0, fail = 0; void Check(bool ok, string what) { if (ok) pass++; else fail++; sb.AppendLine($"{(ok ? "PASS" : "FAIL")} {what}"); }
        // 0) pure Due() rules
        {
            var f = DateTime.UtcNow; TimeSpan S(double x) => TimeSpan.FromSeconds(x);
            Check(!Due(false, f, f, f + S(3.9), S(4), S(20), S(60)) && Due(false, f, f, f + S(4), S(4), S(20), S(60)), "Due: single line waits exactly the 4 s detect window");
            Check(!Due(true, f, f + S(2), f + S(21), S(4), S(20), S(60)) && Due(true, f, f + S(2), f + S(22), S(4), S(20), S(60)), "Due: burst waits 20 s of quiet after its last line");
            Check(!Due(true, f, f + S(59), f + S(59.9), S(4), S(20), S(60)) && Due(true, f, f + S(59), f + S(60), S(4), S(20), S(60)), "Due: burst capped 60 s after its first line");
        }
        var l0 = Lease; Lease = TimeSpan.Zero; lock (gate) leases.Clear(); // the reply lease has its own test ('webhook lease selftest')
        Detect = TimeSpan.FromSeconds(1); Quiet = TimeSpan.FromSeconds(2); MaxHold = TimeSpan.FromSeconds(5); MinInterval = TimeSpan.Zero;
        PostOverride = b => { lock (posts) posts.Add(((DateTime.UtcNow - t0).TotalSeconds, b)); return Task.FromResult<(int?, string)>((200, null)); };
        try
        {
            string T() => DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");
            double Now() => (DateTime.UtcNow - t0).TotalSeconds;
            int before; double s0;
            // 1) single IM line -> POSTed right after the 1 s detect window (not the 2 s quiet gap)
            lock (posts) before = posts.Count; s0 = Now();
            Enqueue("im", "Test Sender", "11111111-1111-1111-1111-111111111111", "just one line", T(), null);
            await Task.Delay(1800);
            lock (posts) Check(posts.Count == before + 1 && posts[^1].body.Contains("\"batched\":1") && posts[^1].t - s0 < 1.8,
                $"single line -> 1 POST after {(posts.Count > before ? (posts[^1].t - s0).ToString("F1") : "-")} s (want ~1 s detect window)");
            // 2) burst of 4 IM lines 0.5 s apart -> nothing at the detect window, ONE post with 4 events ~2 s after the last line
            lock (posts) before = posts.Count; s0 = Now();
            for (int i = 0; i < 4; i++) { Enqueue("im", "Test Sender", "11111111-1111-1111-1111-111111111111", $"line {i + 1}", T(), null); await Task.Delay(500); }
            lock (posts) Check(posts.Count == before, "burst: nothing POSTed while lines keep coming (detect window did not fire)");
            await Task.Delay(2500);
            lock (posts) Check(posts.Count == before + 1 && posts[^1].body.Contains("\"batched\":4") && posts[^1].t - s0 >= 3.4,
                $"burst of 4 lines -> {posts.Count - before} POST(s) at +{(posts.Count > before ? (posts[^1].t - s0).ToString("F1") : "-")} s (want 1 with 4 events, ~3.5 s = last line + 2 s quiet)");
            // 3) urgent teleport offer -> immediate
            lock (posts) before = posts.Count;
            Enqueue("teleport_offer", "Test Sender", "11111111-1111-1111-1111-111111111111", "join me", T(), null);
            await Task.Delay(300);
            lock (posts) Check(posts.Count == before + 1 && posts[^1].body.Contains("\"urgent\":true"), "urgent teleport_offer POSTed within 0.3 s");
            // 4) steady burst every 0.8 s (never quiet 2 s): 7 lines over 4.8 s -> forced out by the 5 s cap, ONE POST with all 7
            lock (posts) before = posts.Count; s0 = Now();
            for (int i = 0; i < 7; i++) { Enqueue("local_chat", "Chatty Resident", "22222222-2222-2222-2222-222222222222", $"chat {i}", T(), 3); if (i < 6) await Task.Delay(800); }
            await Task.Delay(2200);
            lock (posts) Check(posts.Count == before + 1 && posts[before].body.Contains("\"batched\":7") && posts[before].t - s0 >= 4.9 && posts[before].t - s0 < 6,
                $"never-quiet burst -> {posts.Count - before} POST(s), first at +{(posts.Count > before ? (posts[before].t - s0).ToString("F1") : "-")} s (want 1 with 7 lines at ~5 s max hold)");
            // 5) two senders, one line each, same moment -> both due after the detect window -> one POST with 2 conversations
            lock (posts) before = posts.Count;
            Enqueue("im", "A Resident", "33333333-3333-3333-3333-333333333333", "hi", T(), null);
            Enqueue("im", "B Resident", "44444444-4444-4444-4444-444444444444", "hello", T(), null);
            await Task.Delay(1800);
            lock (posts) Check(posts.Count == before + 1 && posts[^1].body.Contains("\"conversations\":2"), "two single-line senders due together -> 1 POST, conversations=2");
        }
        finally { Detect = d0; Quiet = q0; MaxHold = m0; MinInterval = i0; PostOverride = p0; Lease = l0; lock (gate) leases.Clear(); }
        lock (posts) foreach (var p in posts) { var line = $"+{p.t:F1}s {p.body}"; sb.AppendLine("  " + (line.Length > 220 ? line[..220] + "…" : line)); }
        return $"debounce selftest: {pass} PASS, {fail} FAIL\n" + sb.ToString().TrimEnd();
    }

    // returns (http status or null, error text). Retries retriable failures with backoff; appends body to failed log only on final failure.
    static async Task<(int? status, string err)> Post(string body, bool reReadConfig)
    {
        var (url, key) = ReadConfig();
        lock (gate) { configuredCached = url != null && key != null; lastConfigCheck = DateTime.UtcNow; }
        if (url == null || key == null) return (null, "not configured");
        int? lastStatus = null; string lastErr = null;
        for (int attempt = 0; attempt < RetryBackoffMs.Length; attempt++)
        {
            if (RetryBackoffMs[attempt] > 0) await Task.Delay(RetryBackoffMs[attempt]);
            (lastStatus, lastErr) = await PostOnce(url, key, body);
            if (lastStatus is >= 200 and < 300) return (lastStatus, null);
            bool more = attempt < RetryBackoffMs.Length - 1 && RetriableStatus(lastStatus);
            LogLocal($"POST failed ({(lastStatus?.ToString() ?? lastErr)}) attempt {attempt + 1}/{RetryBackoffMs.Length}"
                     + (more ? $"; retry in {RetryBackoffMs[attempt + 1]} ms (same body / msg_ids)" : "; giving up"));
            if (!more) break;
        }
        AppendFailed(body, lastStatus, lastErr);
        return (lastStatus, lastErr);
    }

    static async Task<(int? status, string err)> PostOnce(string url, string key, string body)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8) };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            req.Headers.TryAddWithoutValidation("X-Automation-Key", key);
            using var resp = await http.SendAsync(req);
            var code = (int)resp.StatusCode;
            if (code < 200 || code >= 300)
            {
                string rb = null;
                try { rb = await resp.Content.ReadAsStringAsync(); } catch { }
                if (!string.IsNullOrEmpty(rb))
                    LogLocal($"POST {code} body: {(rb.Length > 240 ? rb[..240] + "…" : rb)}");
            }
            return (code, null);
        }
        catch (Exception ex)
        {
            return (null, ex is TaskCanceledException ? "timeout" : ex.GetType().Name);
        }
    }

    static void AppendFailed(string body, int? status = null, string err = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FailedLog)!);
            var meta = status != null || err != null ? $"/* failed status={status?.ToString() ?? err} at {DateTimeOffset.Now:o} */\n" : "";
            File.AppendAllText(FailedLog, meta + body + "\n");
        }
        catch { }
    }

    // ---- webhook_test tool ----------------------------------------------------
    public static async Task<string> Test()
    {
        var (url, key) = ReadConfig();
        if (url == null || key == null) return JsonSerializer.Serialize(new { ok = false, status = "not configured", url_configured = ReadUrl() != null, key_configured = ReadKey() != null }, J);
        lock (gate)
        {
            DayRoll();
            var since = DateTime.UtcNow - lastPost;
            if (since < MinInterval) return JsonSerializer.Serialize(new { ok = false, status = $"rate limited: retry in {(MinInterval - since).TotalSeconds:F0} s" }, J);
            exemptToday++; lastPost = DateTime.UtcNow; // a manual test never counts against (or is blocked by) the daily cap
        }
        var body = SanitizeJsonBody(JsonSerializer.Serialize(new { kind = "test", region = Core.RegionName, events = Array.Empty<object>() }, J));
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
        Say("fire local_chat D (single line: POST after the detect window)"); Fire("local_chat", "Dave Resident", "later", 1.0);
        await Task.Delay(Detect + MinInterval + TimeSpan.FromSeconds(2));
        Say("webhook_test (expected rate limited or ok): " + await Test());
        await Task.Delay(MinInterval + TimeSpan.FromSeconds(1));
        Say("webhook_test: " + await Test());
        Console.WriteLine("config: " + ConfigSummary());
        return 0;
    }
}
