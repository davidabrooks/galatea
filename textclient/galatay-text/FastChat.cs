// FastChat.cs (2026-10-08 19:50, David: chat replies took ~60 s because every line waited for the "Second Life chat"
// routine to start up). A conversational nearby-chat line or IM (David's, or a visitor's) that InstantReplies.cs did not
// take is answered by the client itself through the xAI chat completions API in a few seconds:
//   system = persona from routes/_fast-chat.json (stable prefix, so it is prompt-cached) + current state,
//   user   = the last N lines of THAT conversation only (nearby chat, or this one IM session; never mixed) + the new line.
// The model answers JSON {"reply","action","pace"} (pace quick/normal/slow/thoughtful sets the typing delay, FastTypingDelayMs):
//   - action=false, reply non-empty: sent with say/IM and claimed exactly like 'say --re <id>' / 'im --re <id>', so the
//     webhook never POSTs that line (it rides along as answered context next to another open line).
//   - action=true (a request to DO something): the client never acts on the model's word. For David the short ack is sent
//     WITHOUT claiming the line, and the line goes on to the chat routine with fast_ack=<ack> (the routine acts, and only
//     reports the result / problems). For a visitor nothing is sent; the routine handles it as before.
//   - empty reply, API error, or timeout (timeout_s, default 8 s): the line goes to the routine unchanged.
// While a call is in flight the webhook holds that conversation (Webhook.FastHold), at most timeout_s + 2 s.
// Key: env XAI_API_KEY, else the box secret store card.XAI_API_KEY; never logged. Token usage per call is appended to
// fastchat-usage.jsonl next to the client log. 'fastchat [status|test [--im|--voice] [--from <name>] <line>]' and the offline CLI
// '--fastchat-test [--im] <line>' (no login; dry run, nothing is sent).
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    internal sealed class FastCfg
    {
        public bool Enabled = true, Visitors = true; public string Model = "grok-4.20-0309-non-reasoning";
        public int MaxTokens = 120, HistoryLines = 20; public double Temperature = 0.8, TimeoutS = 8;
        public double PriceIn = 1.25, PriceCachedIn = 0.2, PriceOut = 2.5; public string Persona = "";
        // human-like typing delay (2026-10-08 David: replies sometimes too fast; vary with the mood): total time since the
        // incoming line = (base + per_word * words) * pace multiplier + jitter, capped (thoughtful has its own, higher cap)
        public double TypingBaseS = 1.5, TypingPerWordS = 0.25, TypingMaxS = 6, TypingJitterS = 0.4;
        public Dictionary<string, double> PaceMult = new() { ["quick"] = 0.6, ["normal"] = 1.0, ["slow"] = 1.4, ["thoughtful"] = 1.8 };
        public Dictionary<string, double> PaceMaxS = new() { ["thoughtful"] = 10 };
        public double MaxDelayS => PaceMaxS.Values.Append(TypingMaxS).Max();
    }
    internal sealed record FastLine(string Scope, string Speaker, string Text, bool Mine);
    internal sealed record FastResult(string Reply, bool Action, int PromptTok, int CachedTok, int OutTok, string Error, string Pace = "normal", string Outfit = null, bool Helper = false);

    static string FastFile => Path.Combine(RouteDir, "_fast-chat.json");
    static string FastUsageFile => Path.Combine(Path.GetDirectoryName(LogPath) ?? ".", "fastchat-usage.jsonl");
    const string FastUrl = "https://api.x.ai/v1/chat/completions";
    static readonly HttpClient fastHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    // tests swap this for a mock (request JSON -> response JSON); null = the real API
    internal static Func<string, CancellationToken, Task<string>> FastPostOverride;
    static readonly ConcurrentDictionary<long, DateTime> fastPending = new();        // msg id -> hold until (UTC)
    static readonly ConcurrentDictionary<long, string> fastAcks = new();             // msg id -> ack sent, line left to the routine
    static readonly List<FastLine> fastHist = new();                                  // nearby + IM lines, newest last (max 400)
    static string fastLast = "-"; static int fastCalls, fastSent, fastAcked, fastFallbacks;

    // webhook (Webhook.cs): hold a conversation while its line is with the fast path; ack text for lines handed on
    public static bool FastHold(long id) => fastPending.TryGetValue(id, out var until) && DateTime.UtcNow < until;
    public static string FastAckFor(long id) => fastAcks.TryGetValue(id, out var a) ? a : null;

    internal static FastCfg ParseFastCfg(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var r = d.RootElement; var c = new FastCfg();
            if (r.TryGetProperty("enabled", out var e)) c.Enabled = e.ValueKind != JsonValueKind.False;
            if (r.TryGetProperty("visitors", out var v)) c.Visitors = v.ValueKind != JsonValueKind.False;
            if (r.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(m.GetString())) c.Model = m.GetString();
            int I(string k, int def, int lo, int hi) => r.TryGetProperty(k, out var x) && x.TryGetInt32(out var n) ? Math.Clamp(n, lo, hi) : def;
            double D(string k, double def, double lo, double hi) => r.TryGetProperty(k, out var x) && x.TryGetDouble(out var n) ? Math.Clamp(n, lo, hi) : def;
            c.MaxTokens = I("max_tokens", 120, 16, 400); c.HistoryLines = I("history_lines", 20, 0, 60);
            c.Temperature = D("temperature", 0.8, 0, 2); c.TimeoutS = D("timeout_s", 8, 1, 20);
            c.PriceIn = D("price_in_per_m", 1.25, 0, 100); c.PriceCachedIn = D("price_cached_in_per_m", 0.2, 0, 100); c.PriceOut = D("price_out_per_m", 2.5, 0, 100);
            c.TypingBaseS = D("typing_base_s", 1.5, 0, 10); c.TypingPerWordS = D("typing_per_word_s", 0.25, 0, 2);
            c.TypingMaxS = D("typing_max_s", 6, 0, 20); c.TypingJitterS = D("typing_jitter_s", 0.4, 0, 3);
            if (r.TryGetProperty("pace_multipliers", out var pm) && pm.ValueKind == JsonValueKind.Object)
                foreach (var kv in pm.EnumerateObject()) if (kv.Value.TryGetDouble(out var mv)) c.PaceMult[kv.Name.ToLowerInvariant()] = Math.Clamp(mv, 0, 5);
            if (r.TryGetProperty("pace_max_s", out var px) && px.ValueKind == JsonValueKind.Object)
                foreach (var kv in px.EnumerateObject()) if (kv.Value.TryGetDouble(out var xv)) c.PaceMaxS[kv.Name.ToLowerInvariant()] = Math.Clamp(xv, 0, 20);
            if (r.TryGetProperty("persona", out var p))
                c.Persona = p.ValueKind == JsonValueKind.Array ? string.Join("\n", p.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()))
                          : p.ValueKind == JsonValueKind.String ? p.GetString() : "";
            if (string.IsNullOrWhiteSpace(c.Persona)) return null;   // never call the model without the persona + rules
            return c;
        }
        catch { return null; }
    }
    static FastCfg LoadFastCfg() { try { return File.Exists(FastFile) ? ParseFastCfg(File.ReadAllText(FastFile)) : null; } catch { return null; } }

    static string FastKey()
    {
        var k = Environment.GetEnvironmentVariable("XAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(k)) return k.Trim();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(SecretsPath));
            if (doc.RootElement.TryGetProperty("card", out var c) && c.TryGetProperty("XAI_API_KEY", out var x) && x.ValueKind == JsonValueKind.String)
                return x.GetString()?.Trim();
        }
        catch { }
        return null;
    }

    // pure: one client log line -> a history line (null = not chat). Scopes: "nearby" or "im:<uuid>".
    static readonly System.Text.RegularExpressions.Regex FastImRx = new(@"^(?:to )?(.+?) \(([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\)(?: \[offline[^\]]*\])?: (.*?)(?: \((?:instant|fast)(?: --re [\d,]+| ack)\))?$");
    internal static FastLine FastParseLog(string kind, string msg)
    {
        if (string.IsNullOrEmpty(msg)) return null;
        switch (kind)
        {
            case "chat":
            {
                var i = msg.IndexOf(": ", StringComparison.Ordinal);
                if (i <= 0) return null;
                var who = msg[..i];
                if (who.EndsWith(" (object)") || who.EndsWith(" (system)") || who.Contains(" (object) ") || who.Contains(" (system) ")) return null;
                return new FastLine("nearby", who.Replace(" whisper", "").Replace(" shout", ""), msg[(i + 2)..], false);
            }
            case "me-chat":
            {
                if (msg.StartsWith("(channel ")) return null;
                var t = msg.StartsWith("(") && msg.IndexOf(") ", StringComparison.Ordinal) is int j and > 0 ? msg[(j + 2)..] : msg;
                if (t.EndsWith(" (wander greeting)")) t = t[..^" (wander greeting)".Length];
                return new FastLine("nearby", "Galatea", t, true);
            }
            case "im": case "me-im":
            {
                var m = FastImRx.Match(msg);
                if (!m.Success || (kind == "me-im") != msg.StartsWith("to ")) return null;
                return new FastLine("im:" + m.Groups[2].Value, kind == "me-im" ? "Galatea" : m.Groups[1].Value, m.Groups[3].Value, kind == "me-im");
            }
        }
        return null;
    }
    static void FastNote(string kind, string msg)
    {
        if (kind is not ("chat" or "me-chat" or "im" or "me-im")) return;
        try { var l = FastParseLog(kind, msg); if (l == null) return; lock (fastHist) { fastHist.Add(l); if (fastHist.Count > 400) fastHist.RemoveRange(0, 100); } }
        catch { }
    }

    // pure: the request body. History is only the given scope; the new line is the last history entry (already logged).
    internal static string BuildFastRequest(FastCfg cfg, string state, IReadOnlyList<FastLine> hist, string scope, string from, string text, bool im)
    {
        var mine = hist.Where(h => h.Scope == scope).ToList();
        if (mine.Count > 0 && !mine[^1].Mine && mine[^1].Text == text) mine.RemoveAt(mine.Count - 1);   // the new line goes last, once
        if (mine.Count > cfg.HistoryLines) mine = mine.Skip(mine.Count - cfg.HistoryLines).ToList();
        var sb = new StringBuilder();
        bool voice = scope == "voice";
        sb.AppendLine(im ? $"Private IM conversation with {from} (only you two see it). Earlier lines:"
            : voice ? "Spoken voice chat nearby, transcribed by speech-to-text (expect transcription errors). You cannot speak; your reply goes out in nearby text chat. Earlier lines:"
            : "Nearby (public) chat; everyone close by sees it. Earlier lines:");
        if (mine.Count == 0) sb.AppendLine("(none)");
        foreach (var h in mine) sb.AppendLine($"{(h.Mine ? "Galatea (you)" : h.Speaker)}: {h.Text}");
        sb.AppendLine().Append($"New {(im ? "IM" : voice ? "spoken line" : "nearby chat line")} from {from}: {text}");
        var body = new JsonObject
        {
            ["model"] = cfg.Model, ["max_tokens"] = cfg.MaxTokens, ["temperature"] = cfg.Temperature, ["stream"] = false,
            ["response_format"] = new JsonObject { ["type"] = "json_object" },
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = cfg.Persona },
                new JsonObject { ["role"] = "system", ["content"] = "Current state: " + (state ?? "unknown") },
                new JsonObject { ["role"] = "user", ["content"] = sb.ToString() },
            },
        };
        return body.ToJsonString();
    }

    // pure: API response -> result (reply trimmed to chat size; anything unparsable = error -> routine)
    internal static FastResult ParseFastResponse(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            var r = d.RootElement;
            int pt = 0, ct = 0, ot = 0;
            if (r.TryGetProperty("usage", out var u))
            {
                if (u.TryGetProperty("prompt_tokens", out var a)) a.TryGetInt32(out pt);
                if (u.TryGetProperty("completion_tokens", out var b)) b.TryGetInt32(out ot);
                if (u.TryGetProperty("prompt_tokens_details", out var pd) && pd.ValueKind == JsonValueKind.Object && pd.TryGetProperty("cached_tokens", out var c)) c.TryGetInt32(out ct);
            }
            var content = r.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            content = content.Trim();
            if (content.StartsWith("```")) content = content.Trim('`').Replace("json", "", StringComparison.OrdinalIgnoreCase).Trim();
            using var cd = JsonDocument.Parse(content);
            var reply = cd.RootElement.TryGetProperty("reply", out var rp) && rp.ValueKind == JsonValueKind.String ? rp.GetString() ?? "" : "";
            bool action = cd.RootElement.TryGetProperty("action", out var ac) && ac.ValueKind == JsonValueKind.True;
            // 2026-10-09: helper=true (a how/why question about her own behaviour / client / speed / bugs / setup) is never
            // answered from guesswork: it is handled like an action (short 'let me check' ack, the routine answers)
            bool helper = cd.RootElement.TryGetProperty("helper", out var hp) && hp.ValueKind == JsonValueKind.True;
            if (helper) action = true;
            // 2026-10-09: outfit = "random" | "top_color" | "<saved outfit name>" (FastOutfit.cs validates it; null = none)
            var outfit = cd.RootElement.TryGetProperty("outfit", out var of) && of.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(of.GetString()) ? of.GetString().Trim() : null;
            if (outfit != null && (outfit.Equals("null", StringComparison.OrdinalIgnoreCase) || outfit.Equals("none", StringComparison.OrdinalIgnoreCase))) outfit = null;
            var pace = cd.RootElement.TryGetProperty("pace", out var pc) && pc.ValueKind == JsonValueKind.String ? pc.GetString() : null;
            (reply, var tagPace) = FastStripPaceTag(reply);
            pace = FastNormPace(pace ?? tagPace);
            reply = System.Text.RegularExpressions.Regex.Replace(reply, @"\s*\n\s*", " ").Trim();
            if (reply.Length > 300) reply = reply[..300].TrimEnd() + "…";
            return new FastResult(reply, action, pt, ct, ot, null, pace, action && !helper ? outfit : null, helper);
        }
        catch (Exception ex) { return new FastResult("", false, 0, 0, 0, "bad response: " + ex.GetType().Name); }
    }

    static readonly string[] FastPaces = { "quick", "normal", "slow", "thoughtful" };
    // pure: unknown / missing pace -> "normal"
    internal static string FastNormPace(string p)
    {
        p = (p ?? "").Trim().Trim('[', ']', '(', ')').ToLowerInvariant();
        return FastPaces.Contains(p) ? p : "normal";
    }
    // pure: a pace tag the model put into the text itself ("[pace: slow]", "(pace=quick)", "<pace>thoughtful</pace>") is
    // removed from what gets sent; returns the tag's value (null if none)
    static readonly System.Text.RegularExpressions.Regex FastPaceTagRx = new(@"\s*(?:\[\s*pace\s*[:=]\s*(\w+)\s*\]|\(\s*pace\s*[:=]\s*(\w+)\s*\)|<pace>\s*(\w+)\s*</pace>|\{\s*pace\s*[:=]\s*(\w+)\s*\})\s*",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    internal static (string text, string pace) FastStripPaceTag(string reply)
    {
        if (string.IsNullOrEmpty(reply)) return (reply ?? "", null);
        string pace = null;
        var t = FastPaceTagRx.Replace(reply, m => { pace ??= m.Groups.Cast<System.Text.RegularExpressions.Group>().Skip(1).FirstOrDefault(g => g.Success)?.Value; return " "; });
        return (System.Text.RegularExpressions.Regex.Replace(t, @"\s{2,}", " ").Trim(), pace);
    }
    // pure: how long to keep "typing" before sending. Target total since the incoming line =
    // (base + per_word * words) * pace multiplier + jitter (jitterUnit in -1..1), capped by the pace's cap; minus the time
    // the API call already took. Never negative.
    internal static int FastTypingDelayMs(FastCfg c, string reply, string pace, long elapsedMs, double jitterUnit)
    {
        pace = FastNormPace(pace);
        int words = string.IsNullOrWhiteSpace(reply) ? 0 : reply.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Length;
        double mult = c.PaceMult.TryGetValue(pace, out var m) ? m : 1.0;
        double cap = c.PaceMaxS.TryGetValue(pace, out var x) ? x : c.TypingMaxS;
        double target = (c.TypingBaseS + c.TypingPerWordS * words) * mult + Math.Clamp(jitterUnit, -1, 1) * c.TypingJitterS;
        target = Math.Clamp(target, 0, cap);
        return (int)Math.Max(0, Math.Round(target * 1000 - elapsedMs));
    }

    internal enum FastOutcome { Send, Ack, Routine }
    // pure: what to do with a result. Visitors' action requests and every empty / failed answer go to the routine.
    internal static FastOutcome FastDecide(FastResult r, bool isDavid)
    {
        if (r == null || r.Error != null || string.IsNullOrWhiteSpace(r.Reply)) return FastOutcome.Routine;
        if (r.Action) return isDavid ? FastOutcome.Ack : FastOutcome.Routine;
        return FastOutcome.Send;
    }

    internal static double FastCost(FastCfg c, int pt, int ct, int ot) =>
        ((pt - ct) * c.PriceIn + ct * c.PriceCachedIn + ot * c.PriceOut) / 1_000_000.0;

    // the API call with a hard timeout; never throws
    internal static async Task<(FastResult r, long ms)> FastCall(FastCfg cfg, string body, string key)
    {
        var sw = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(cfg.TimeoutS));
        try
        {
            string resp;
            if (FastPostOverride != null) resp = await FastPostOverride(body, cts.Token).WaitAsync(cts.Token);
            else
            {
                if (string.IsNullOrEmpty(key)) return (new FastResult("", false, 0, 0, 0, "no API key"), sw.ElapsedMilliseconds);
                using var req = new HttpRequestMessage(HttpMethod.Post, FastUrl) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var res = await fastHttp.SendAsync(req, cts.Token);
                resp = await res.Content.ReadAsStringAsync(cts.Token);
                if (!res.IsSuccessStatusCode) return (new FastResult("", false, 0, 0, 0, $"HTTP {(int)res.StatusCode}"), sw.ElapsedMilliseconds);
            }
            return (ParseFastResponse(resp), sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) { return (new FastResult("", false, 0, 0, 0, $"timeout after {cfg.TimeoutS:0.#} s"), sw.ElapsedMilliseconds); }
        catch (Exception ex) { return (new FastResult("", false, 0, 0, 0, ex.GetType().Name), sw.ElapsedMilliseconds); }
    }

    static void FastUsage(FastCfg c, string who, bool im, FastResult r, long ms, string outcome)
    {
        try
        {
            var o = new JsonObject
            {
                ["t"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture), ["model"] = c.Model, ["who"] = who, ["im"] = im,
                ["ms"] = ms, ["prompt"] = r.PromptTok, ["cached"] = r.CachedTok, ["out"] = r.OutTok,
                ["usd"] = Math.Round(FastCost(c, r.PromptTok, r.CachedTok, r.OutTok), 6), ["outcome"] = outcome, ["error"] = r.Error,
            };
            lock (fastHist) File.AppendAllText(FastUsageFile, o.ToJsonString() + "\n");
        }
        catch { }
    }

    static string FastState()
    {
        try
        {
            var parts = new List<string>();
            var sim = client?.Network?.CurrentSim;
            if (sim != null)
            {
                var p = client.Self.SimPosition;
                parts.Add($"in region {sim.Name}{(sim.Name == "Peronaut" ? " (at home with David, the beach house)" : "")} at {p.X:0},{p.Y:0},{p.Z:0}");
            }
            if (!string.IsNullOrEmpty(lastNamedOutfit)) parts.Add($"wearing the outfit '{lastNamedOutfit}'");
            var outfits = FastOutfitNamesSnapshot();
            if (outfits.Count > 0) parts.Add($"your saved outfits: {string.Join(", ", outfits.Select(n => "'" + n + "'"))}");
            parts.Add(client?.Self?.SittingOn is uint s && s != 0 ? "sitting" : "standing");
            parts.Add(WanderOn ? $"wandering around the house ({wanderPhase}{(wanderPause != null ? ", paused" : "")})" : "not wandering");
            if (followId != UUID.Zero) parts.Add($"following {followName}");
            parts.Add($"local time {DateTime.Now:ddd h:mm tt} PT");
            return string.Join("; ", parts);
        }
        catch { return $"local time {DateTime.Now:ddd h:mm tt} PT"; }
    }

    static List<FastLine> FastHistSnapshot() { lock (fastHist) return fastHist.ToList(); }

    // ---- live path (Program.Hook / HandleIm, after TryInstantReply) --------------------------------------------
    static void TryFastChat(UUID from, string name, string text, long msgId, bool im, UUID imSession)
    {
        try
        {
            if (msgId <= 0 || string.IsNullOrWhiteSpace(text) || from == client.Self.AgentID) return;
            var cfg = LoadFastCfg();
            if (cfg == null || !cfg.Enabled) return;
            bool david = from == DavidId;
            if (!david && !cfg.Visitors) return;
            if (!im && QuietChatGuard() != null) return;
            // same filter as Notify: while nearby-quiet.txt exists, a visitor's nearby line only counts if it names Galatea
            if (!im && !david && File.Exists("/home/box/viewers/textclient/nearby-quiet.txt") && text.IndexOf("galat", StringComparison.OrdinalIgnoreCase) < 0) return;
            // 2026-10-09 David: "look into" / "look in to" from him ALWAYS goes to the full routine (short ack, never the model)
            if (david && FastLookIntoTrigger(text)) { FastLookIntoAck(cfg, from, name, text, msgId, im, imSession); return; }
            var key = FastKey();
            if (string.IsNullOrEmpty(key) && FastPostOverride == null) { fastLast = "no API key"; return; }
            fastPending[msgId] = DateTime.UtcNow.AddSeconds(cfg.TimeoutS + cfg.MaxDelayS + 2);   // API call + typing delay
            var scope = im ? "im:" + from : "nearby";
            var body = BuildFastRequest(cfg, FastState(), FastHistSnapshot(), scope, name, text, im);
            _ = Task.Run(async () =>
            {
                string outcome = "routine", typed = "";
                FastResult r = null; long ms = 0; var sw = Stopwatch.StartNew();
                Task<FastOutfitRun> outfitTask = null; bool keepHold = false;
                try
                {
                    Interlocked.Increment(ref fastCalls);
                    (r, ms) = await FastCall(cfg, body, key);
                    var dec = FastDecide(r, david);
                    if (dec != FastOutcome.Routine && RateGuard() is string rg) { outcome = "routine (" + rg + ")"; dec = FastOutcome.Routine; }
                    // 2026-10-09 David: an outfit change he asks for starts here, ~1 s after his line (not ~60 s later in the routine)
                    if (dec == FastOutcome.Ack && david && r.Outfit != null) outfitTask = FastOutfitStart(r.Outfit, text, msgId);
                    if (dec != FastOutcome.Routine)
                    {
                        // human-like pause, typing indicator on the whole time (David's line already started it)
                        int wait = FastTypingDelayMs(cfg, r.Reply, r.Pace, sw.ElapsedMilliseconds, Random.Shared.NextDouble() * 2 - 1);
                        if (wait > 0)
                        {
                            fastPending[msgId] = DateTime.UtcNow.AddMilliseconds(wait + 3000);
                            if (david) TypingStart(im); else if (!im && !typingOn) TypingStart(false);
                            await Task.Delay(wait);
                        }
                        typed = $", typed {r.Pace} {sw.ElapsedMilliseconds / 1000.0:0.0} s";
                    }
                    if (dec == FastOutcome.Ack && david && r.Outfit != null && FastOutfitBegin(cfg, r, from, name, text, msgId, im, imSession, outfitTask) is string ow)
                    {
                        // the change is already running (started right after the API answer, before the typing pause)
                        outcome = ow; keepHold = true;
                    }
                    else if (dec == FastOutcome.Send) outcome = FastSend(from, name, r.Reply, msgId, im, imSession, claim: true) ? "sent" : "routine (send refused)";
                    else if (dec == FastOutcome.Ack)
                    {
                        if (FastSend(from, name, r.Reply, msgId, im, imSession, claim: false)) { fastAcks[msgId] = r.Reply; outcome = "ack, action left to the routine"; }
                        else outcome = "routine (ack not sent)";
                    }
                    else if (r.Error != null) outcome = "routine (" + r.Error + ")";
                    else if (r.Action) outcome = "routine (visitor action request)";
                    else outcome = "routine (no reply needed)";
                }
                catch (Exception ex) { outcome = "routine (error " + ex.GetType().Name + ")"; }
                finally
                {
                    if (!keepHold) fastPending.TryRemove(msgId, out _);
                    if (outcome == "sent") Interlocked.Increment(ref fastSent); else if (outcome.StartsWith("ack")) Interlocked.Increment(ref fastAcked); else Interlocked.Increment(ref fastFallbacks);
                    fastLast = $"{DateTime.Now:HH:mm:ss} {name} {(im ? "IM" : "nearby")} '{Short(text, 40)}' -> {outcome} in {ms} ms{typed}{(r != null && !string.IsNullOrEmpty(r.Reply) ? $" ('{Short(r.Reply, 60)}')" : "")}";
                    Log("fastchat", fastLast);
                    if (r != null) FastUsage(cfg, name, im, r, ms, outcome);
                }
            });
        }
        catch (Exception ex) { fastPending.TryRemove(msgId, out _); Log("fastchat", "error: " + ex.GetBaseException().Message); }
    }

    // ---- voice (Voice.cs VoiceWakeFlush; voice listening stays off unless David turns it on) ------------------------
    // A voice wake first tries the fast path. Voice is listen-only (no speech output), so the reply goes out in nearby text
    // chat (unclaimed: a voice line has no msg id). Sent -> no webhook POST. Action (David) -> ack sent, then `post` runs
    // with the ack so the routine does the action. Anything else (visitor action, empty, error, timeout) -> `post(null)`.
    // History: the voice scope only (transcript lines passed in + my earlier voice replies), never nearby chat or IMs.
    static bool TryFastVoice(UUID from, string name, string text, IReadOnlyList<(string speaker, string text)> transcript, Action<string> post)
    {
        try
        {
            var cfg = LoadFastCfg();
            if (cfg == null || !cfg.Enabled || string.IsNullOrWhiteSpace(text) || from == UUID.Zero) return false;
            bool david = from == DavidId;
            if (!david && !cfg.Visitors) return false;
            if (QuietChatGuard() != null) return false;
            var key = FastKey();
            if (string.IsNullOrEmpty(key) && FastPostOverride == null) return false;
            var hist = FastHistSnapshot().Where(h => h.Scope == "voice" && h.Mine).ToList();
            hist.AddRange(transcript.Select(t => new FastLine("voice", t.speaker, t.text, false)));
            var body = BuildFastRequest(cfg, FastState(), hist, "voice", name, text, false);
            _ = Task.Run(async () =>
            {
                string outcome = "routine"; FastResult r = null; long ms = 0; string ack = null; bool posted = false;
                try
                {
                    Interlocked.Increment(ref fastCalls);
                    (r, ms) = await FastCall(cfg, body, key);
                    var dec = FastDecide(r, david);
                    if (dec != FastOutcome.Routine && RateGuard() is string rg) { outcome = "routine (" + rg + ")"; dec = FastOutcome.Routine; }
                    if (dec != FastOutcome.Routine && LoggedIn)
                    {
                        HeadTurnForSay(); WanderOwnChat();
                        client.Self.Chat(r.Reply, 0, ChatType.Normal);
                        NoteSpokeToNearby(ChatType.Normal);
                        Log("me-chat", $"(fast voice{(dec == FastOutcome.Ack ? " ack" : "")}) {r.Reply}");
                        lock (fastHist) fastHist.Add(new FastLine("voice", "Galatea", r.Reply, true));
                        if (dec == FastOutcome.Ack) { ack = r.Reply; outcome = "ack, action left to the routine"; } else outcome = "sent";
                    }
                    else if (r.Error != null) outcome = "routine (" + r.Error + ")";
                }
                catch (Exception ex) { outcome = "routine (error " + ex.GetType().Name + ")"; }
                finally
                {
                    if (outcome != "sent") { posted = true; try { post(ack); } catch { } }
                    if (outcome == "sent") Interlocked.Increment(ref fastSent); else if (ack != null) Interlocked.Increment(ref fastAcked); else Interlocked.Increment(ref fastFallbacks);
                    fastLast = $"{DateTime.Now:HH:mm:ss} {name} voice '{Short(text, 40)}' -> {outcome} in {ms} ms{(r != null && !string.IsNullOrEmpty(r.Reply) ? $" ('{Short(r.Reply, 60)}')" : "")}{(posted ? "; webhook POSTed" : "")}";
                    Log("fastchat", fastLast);
                    if (r != null) FastUsage(cfg, name, false, r, ms, "voice: " + outcome);
                }
            });
            return true;
        }
        catch (Exception ex) { Log("fastchat", "voice error: " + ex.GetBaseException().Message); return false; }
    }

    // pure: David's guaranteed hand-off keyword ("look into", "look in to", any case / spacing)
    static readonly System.Text.RegularExpressions.Regex FastLookIntoRx = new(@"\blook\s+in\s*to\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    internal static bool FastLookIntoTrigger(string text) => !string.IsNullOrEmpty(text) && FastLookIntoRx.IsMatch(text);
    internal static readonly string[] FastLookIntoAcks = { "Okay babe, I'll look into it.", "On it, let me look into that, babe.", "Sure, I'll look into it, love." };

    // short canned ack after a typing pause, unclaimed; the line goes to the routine with fast_ack (no API call)
    static void FastLookIntoAck(FastCfg cfg, UUID from, string name, string text, long msgId, bool im, UUID imSession)
    {
        var ack = FastLookIntoAcks[Random.Shared.Next(FastLookIntoAcks.Length)];
        int wait = FastTypingDelayMs(cfg, ack, "normal", 0, Random.Shared.NextDouble() * 2 - 1);
        fastPending[msgId] = DateTime.UtcNow.AddMilliseconds(wait + 3000);
        _ = Task.Run(async () =>
        {
            string outcome = "routine (look-into ack not sent)";
            try
            {
                TypingStart(im); await Task.Delay(wait);
                if (RateGuard() is string rg) outcome = "routine (" + rg + ")";
                else if (FastSend(from, name, ack, msgId, im, imSession, claim: false)) { fastAcks[msgId] = ack; outcome = "look-into: ack, left to the routine"; }
            }
            catch (Exception ex) { outcome = "routine (error " + ex.GetType().Name + ")"; }
            finally
            {
                fastPending.TryRemove(msgId, out _);
                if (outcome.StartsWith("look")) Interlocked.Increment(ref fastAcked); else Interlocked.Increment(ref fastFallbacks);
                fastLast = $"{DateTime.Now:HH:mm:ss} {name} {(im ? "IM" : "nearby")} '{Short(text, 40)}' -> {outcome}";
                Log("fastchat", fastLast);
            }
        });
    }

    // claim=true: like 'say --re <id>' / 'im --re <id>' (the routine skips the line); claim=false: an ack that leaves it open
    static bool FastSend(UUID to, string name, string reply, long msgId, bool im, UUID imSession, bool claim)
    {
        if (!LoggedIn) return false;
        void SendIm() { if (imSession != UUID.Zero) client.Self.InstantMessage(to, reply, imSession); else client.Self.InstantMessage(to, reply); }
        WanderOwnChat();
        if (im)
        {
            if (claim)
            {
                var (sent, skip) = ImGuardedSend(to.ToString(), name, to == DavidId, false, SendIm, null, false, new[] { msgId }, reply);
                if (!sent) { Log("fastchat", "IM not sent: " + skip); return false; }
                instantAnswered[msgId] = reply;
            }
            else SendIm();
            Log("me-im", $"to {name} ({to}): {reply} (fast{(claim ? " --re " + msgId : " ack")})");
            NoteGreeted(to);
            if (to == DavidId) TypingStop("fast chat IM to David");
        }
        else
        {
            HeadTurnForSay();
            if (claim)
            {
                var (sent, skip) = ChatGuardedSay(ChatType.Normal, reply, false, new[] { msgId });
                if (!sent) { Log("fastchat", "say not sent: " + skip); return false; }
                instantAnswered[msgId] = reply;
            }
            else client.Self.Chat(reply, 0, ChatType.Normal);
            NoteSpokeToNearby(ChatType.Normal);
            Log("me-chat", $"({(claim ? "say --re " + msgId + ", fast" : "fast ack")}) {reply}");
            TypingStop("fast chat reply");
        }
        return true;
    }

    // 'fastchat [status|test [--im|--voice] [--from <name>] <line>]' (test = dry run: real API call, nothing sent)
    static async Task<string> FastChatCmd(string[] a)
    {
        var cfg = LoadFastCfg();
        if (a.Length > 0 && a[0] == "test") return await FastTest(cfg, a.Skip(1).ToArray(), LoggedIn ? FastState() : null);
        if (cfg == null) return $"fast chat: {FastFile} missing or invalid (no persona?); all lines go to the routine";
        return $"fast chat {(cfg.Enabled ? "ON" : "OFF")} (model {cfg.Model}, max_tokens {cfg.MaxTokens}, timeout {cfg.TimeoutS:0.#} s, history {cfg.HistoryLines}, visitors {(cfg.Visitors ? "on" : "off")}, key {(string.IsNullOrEmpty(FastKey()) ? "MISSING" : "ok")}); " +
               $"this process: {fastCalls} call(s), {fastSent} sent, {fastAcked} ack(s), {fastFallbacks} to the routine; in flight {fastPending.Count}; {FastUsageSummary(cfg)}; last: {fastLast}; last outfit: {fastOutfitLast}";
    }

    static string FastUsageSummary(FastCfg cfg)
    {
        try
        {
            if (!File.Exists(FastUsageFile)) return "usage log empty";
            var today = DateTime.Now.ToString("yyyy-MM-dd"); int n = 0; long pt = 0, ct = 0, ot = 0; double usd = 0;
            foreach (var l in File.ReadLines(FastUsageFile))
            {
                if (!l.Contains("\"t\":\"" + today)) continue;
                using var d = JsonDocument.Parse(l); var r = d.RootElement;
                n++; pt += r.GetProperty("prompt").GetInt32(); ct += r.GetProperty("cached").GetInt32(); ot += r.GetProperty("out").GetInt32(); usd += r.GetProperty("usd").GetDouble();
            }
            return n == 0 ? "no calls today" : $"today {n} call(s), avg {pt / n} prompt ({ct / n} cached) + {ot / n} out tokens, ${usd:0.0000} total (${usd / n:0.00000}/call)";
        }
        catch { return "usage log unreadable"; }
    }

    static async Task<string> FastTest(FastCfg cfg, string[] a, string state)
    {
        if (cfg == null) return $"fast chat: {FastFile} missing or invalid";
        bool im = false, voice = false; string from = "David Nightingale"; var words = new List<string>();
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == "--im") im = true;
            else if (a[i] == "--voice") voice = true;
            else if (a[i] == "--from" && i + 1 < a.Length) from = a[++i].Replace('_', ' ');
            else words.Add(a[i]);
        }
        var text = string.Join(' ', words).Trim().Trim('"');
        if (text.Length == 0) return "usage: fastchat test [--im|--voice] [--from <First_Last>] <line>";
        var scope = im ? "im:test" : voice ? "voice" : "nearby";
        var hist = FastHistSnapshot().Where(h => h.Scope == scope).ToList();
        var body = BuildFastRequest(cfg, state ?? "in region Peronaut (at home with David, the beach house); wearing the outfit 'TETRA Chill T-Shirt shorts'; standing; not wandering; local time " + DateTime.Now.ToString("ddd h:mm tt") + " PT", hist, scope, from, text, im);
        var (r, ms) = await FastCall(cfg, body, FastKey());
        FastUsage(cfg, from + " (test)", im, r, ms, "test");
        var dec = FastDecide(r, from == "David Nightingale");
        return $"fastchat test ({(im ? "IM" : voice ? "voice" : "nearby")}, from {from}, {cfg.Model}): {ms} ms; reply '{r.Reply}', action={r.Action.ToString().ToLowerInvariant()}, pace={r.Pace}, outfit={r.Outfit ?? "-"}, helper={r.Helper.ToString().ToLowerInvariant()} (typing ~{FastTypingDelayMs(cfg, r.Reply, r.Pace, 0, 0) / 1000.0:0.0} s) -> would {dec switch { FastOutcome.Send => "send + claim", FastOutcome.Ack => "send the ack and hand the action to the routine", _ => "leave it to the routine" }}" +
               $"{(r.Error != null ? " (" + r.Error + ")" : "")}; tokens {r.PromptTok} prompt ({r.CachedTok} cached) + {r.OutTok} out = ${FastCost(cfg, r.PromptTok, r.CachedTok, r.OutTok):0.00000}";
    }
}
