// InstantReplies.cs (2026-10-08 19:08, David: "There was a long delay before you answered me in nearby chat. Please make
// that faster"). His "start wandering!" waited 5 s in the webhook batch and ~60 s for the chat routine to start up.
// 1. Instant replies: a few clear requests from David (nearby chat or IM) are answered by the client itself in ~1 s:
//    wander_start / wander_stop / follow / follow_stop. Patterns + reply variants live in routes/_instant-replies.json
//    (read fresh on every line). The client acts first; only if the action worked does it reply and claim the line exactly
//    like 'say --re <id>' / 'im --re <id>' (Explicit answered), so the webhook does not POST it on its own and a later
//    '--re' on it is refused. Anything else (questions, chatty lines, failed actions, quiet mode) goes to the routine.
//    If he has just logged in and I have not said anything since, the greeting is folded into the reply ("Hi babe! On it!").
// 2. Typing indicator: any other line from David starts the SL typing state + typing animation right away (nearby chat:
//    ChatType.StartTyping + typing animation; IM: the IM typing dialog only) until my next say / IM to him, or 90 s.
// 'instant [status|test <text>]'.
using System.Text.Json;
using System.Text.RegularExpressions;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    internal sealed class InstantCmd { public string Action; public List<string> Phrases = new(); public List<string> Replies = new(); }
    internal sealed class InstantCfg
    {
        public bool Enabled = true; public int MaxChars = 60;
        public List<string> Fillers = new(); public List<string> Greetings = new(); public List<InstantCmd> Commands = new();
    }
    static string InstantFile => Path.Combine(RouteDir, "_instant-replies.json");
    static readonly Random instRnd = new();
    // msg id -> my instant reply (the webhook keeps these as answered context next to other unanswered lines)
    static readonly System.Collections.Concurrent.ConcurrentDictionary<long, string> instantAnswered = new();
    public static string InstantReplyFor(long id) => instantAnswered.TryGetValue(id, out var r) ? r : null;
    static DateTimeOffset? lastOwnNearbyChatAt;   // any own nearby chat (say, chan 0, wander greeting): NoteSpokeToNearby
    static DateTime instantGreetAtUtc = DateTime.MinValue;
    static string instLast = "-";

    // pure: parse the JSON text (null on error)
    internal static InstantCfg ParseInstantCfg(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var r = d.RootElement; var c = new InstantCfg();
            if (r.TryGetProperty("enabled", out var e)) c.Enabled = e.ValueKind != JsonValueKind.False;
            if (r.TryGetProperty("max_chars", out var m) && m.TryGetInt32(out var mc)) c.MaxChars = Math.Clamp(mc, 10, 200);
            List<string> L(JsonElement o, string k) => o.TryGetProperty(k, out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList() : new();
            c.Fillers = L(r, "fillers").Select(InstantNorm).Where(s => s.Length > 0).OrderByDescending(s => s.Length).ToList();
            c.Greetings = L(r, "greetings");
            if (r.TryGetProperty("commands", out var cs) && cs.ValueKind == JsonValueKind.Array)
                foreach (var x in cs.EnumerateArray())
                {
                    var act = x.TryGetProperty("action", out var a) ? a.GetString() : null;
                    if (string.IsNullOrEmpty(act)) continue;
                    c.Commands.Add(new InstantCmd { Action = act, Phrases = L(x, "phrases").Select(InstantNorm).Where(s => s.Length > 0).ToList(), Replies = L(x, "replies") });
                }
            return c;
        }
        catch { return null; }
    }
    static InstantCfg LoadInstantCfg() { try { return File.Exists(InstantFile) ? ParseInstantCfg(File.ReadAllText(InstantFile)) : null; } catch { return null; } }

    // pure: lower-case, curly quotes -> ', anything but letters/digits/'/space -> space, collapsed
    internal static string InstantNorm(string s)
    {
        s = (s ?? "").ToLowerInvariant().Replace('\u2019', '\'').Replace('\u2018', '\'');
        s = Regex.Replace(s, @"[^\p{L}\p{N}' ]+", " ");
        return Regex.Replace(s, @"\s+", " ").Trim().Trim('\'').Trim();
    }

    // pure: which action does this line ask for? null = not a clear instant command (goes to the routine)
    internal static InstantCmd MatchInstant(InstantCfg cfg, string text)
    {
        if (cfg == null || !cfg.Enabled || string.IsNullOrWhiteSpace(text)) return null;
        var raw = text.Trim();
        if (raw.Length > cfg.MaxChars || raw.Contains('?') || raw.Contains('\uFF1F')) return null;
        if (raw.StartsWith("/me ", StringComparison.OrdinalIgnoreCase)) return null;   // an emote is not a request
        var t = InstantNorm(raw);
        for (bool changed = true; changed && t.Length > 0;)
        {
            changed = false;
            foreach (var f in cfg.Fillers)
            {
                if (t == f) { t = ""; changed = true; break; }
                if (t.StartsWith(f + " ", StringComparison.Ordinal)) { t = t[(f.Length + 1)..]; changed = true; break; }
                if (t.EndsWith(" " + f, StringComparison.Ordinal)) { t = t[..^(f.Length + 1)]; changed = true; break; }
            }
        }
        if (t.Length == 0) return null;
        return cfg.Commands.FirstOrDefault(c => c.Phrases.Contains(t, StringComparer.Ordinal));
    }

    // pure: should the reply carry the login greeting? He came online / I logged in less than `window` ago and I have not
    // said anything nearby or IMed him since.
    internal static bool NeedsLoginGreet(DateTime nowUtc, DateTime myLoginUtc, DateTime davidOnlineUtc, DateTimeOffset? lastOwnNearby, DateTimeOffset? lastImToDavid, TimeSpan window)
    {
        if (myLoginUtc == DateTime.MinValue) return false;
        var start = davidOnlineUtc > myLoginUtc ? davidOnlineUtc : myLoginUtc;
        if (nowUtc - start > window || nowUtc < start) return false;
        if (lastOwnNearby != null && lastOwnNearby.Value.UtcDateTime >= start) return false;
        if (lastImToDavid != null && lastImToDavid.Value.UtcDateTime >= start) return false;
        return true;
    }

    // pure: the reply text (greeting folded in front when needed)
    internal static string InstantReplyText(InstantCmd c, IReadOnlyList<string> greetings, bool greet, Random rnd)
    {
        var r = c.Replies.Count > 0 ? c.Replies[rnd.Next(c.Replies.Count)] : "Okay!";
        if (!greet || greetings == null || greetings.Count == 0) return r;
        var g = greetings[rnd.Next(greetings.Count)];
        return g.TrimEnd() + " " + r;
    }

    // ---- live path (Program.Hook / HandleIm) ------------------------------------------------------------
    // true = answered + claimed here (no typing indicator, the routine sees it as answered)
    static bool TryInstantReply(UUID from, string name, string text, long msgId, bool im)
    {
        try
        {
            if (from != DavidId || msgId <= 0) return false;
            var cfg = LoadInstantCfg();
            var c = MatchInstant(cfg, text);
            if (c == null) return false;
            if (!im && QuietChatGuard() != null) { instLast = $"{DateTime.Now:HH:mm:ss} '{Short(text, 40)}': quiet mode, left to the routine"; return false; }
            var (ok, what) = InstantAct(c.Action);
            if (!ok) { instLast = $"{DateTime.Now:HH:mm:ss} '{Short(text, 40)}' -> {c.Action} not done ({what}); left to the routine"; Log("instant", instLast); return false; }
            bool greet = NeedsLoginGreet(DateTime.UtcNow, myLoginAt, davidOnlineAt, lastOwnNearbyChatAt, LastMyImTo(DavidId.ToString()), TimeSpan.FromMinutes(15));
            var reply = InstantReplyText(c, cfg.Greetings, greet, instRnd);
            if (RateGuard() is string rg) { Log("instant", $"{c.Action} done ({what}) but no reply: {rg}"); return false; }
            WanderOwnChat();
            bool sent; string skip;
            if (im) (sent, skip) = ImGuardedSend(from.ToString(), name, true, false, () => client.Self.InstantMessage(from, reply), null, false, new[] { msgId }, reply);
            else { HeadTurnForSay(); (sent, skip) = ChatGuardedSay(ChatType.Normal, reply, false, new[] { msgId }); }
            if (!sent) { Log("instant", $"{c.Action} done ({what}); reply not sent: {skip}"); return false; }
            instantAnswered[msgId] = reply;
            if (greet) instantGreetAtUtc = DateTime.UtcNow;
            if (im) { Log("me-im", $"to {name} ({from}): {reply} (instant --re {msgId})"); NoteGreeted(from); }
            else { NoteSpokeToNearby(ChatType.Normal); Log("me-chat", $"(say --re {msgId}, instant) {reply}"); }
            instLast = $"{DateTime.Now:HH:mm:ss} '{Short(text, 40)}' -> {c.Action} ({what}); replied '{reply}'{(greet ? " (with the login greeting)" : "")}";
            Log("instant", instLast);
            return true;
        }
        catch (Exception ex) { Log("instant", "error: " + ex.GetBaseException().Message); return false; }
    }

    static (bool ok, string what) InstantAct(string action)
    {
        bool followingDavid = followId == DavidId;
        switch (action)
        {
            case "wander_start":
            {
                if (followingDavid) { FollowCmd("off"); }
                Interlocked.Increment(ref wDeferredPauseGen);   // drop a chat pause deferred past a doorway (DoorwayNoPause.cs)
                if (WanderOn)
                {
                    var p = wanderPause;
                    if (p == null) return (true, "already wandering");
                    if (client.Self.SittingOn == 0 && !AoStateNow().active) return (false, "AO not active");
                    WLog($"RESUME by David's instant request (was {p})"); if (wRestUntil != null) wRestUntil = DateTime.Now; wanderPause = null;
                    return (true, $"resumed (was {p})");
                }
                var r = StartWander("David asked in chat (instant reply)");
                return (r.StartsWith("wander started"), r);
            }
            case "wander_stop":
                return (true, StopWander("David asked in chat (instant reply)"));
            case "follow":
            {
                if (client.Self.SittingOn != 0) return (false, "seated (standing up is left to the routine: outfit/redress rules)");
                if (followingDavid) return (true, "already following him");
                var dav = Avatars().FirstOrDefault(t => t.av.ID == DavidId);
                if (dav.av == null || dav.dist < 0) return (false, "David not in view");
                if (WanderOn) StopWander("David asked me to come / follow (instant reply)");
                var r = FollowCmd(dav.av.Name);
                return (r.StartsWith("following"), r);
            }
            case "follow_stop":
                if (!followingDavid) return (false, "not following him");
                return (true, FollowCmd("off"));
        }
        return (false, "unknown action '" + action + "'");
    }

    // ---- typing indicator -----------------------------------------------------------------------------------
    static readonly TimeSpan TypingMax = TimeSpan.FromSeconds(90);
    static int typingGen; static volatile bool typingOn; static bool typingIm;
    static void TypingStart(bool im)
    {
        try
        {
            if (!LoggedIn) return;
            var gen = Interlocked.Increment(ref typingGen);
            if (!typingOn || typingIm != im)
            {
                if (typingOn) TypingSend(false);
                typingIm = im; typingOn = true; TypingSend(true);
                Log("typing", $"started ({(im ? "IM" : "nearby chat")} from David; until my reply or {TypingMax.TotalSeconds:F0} s)");
            }
            _ = Task.Run(async () => { await Task.Delay(TypingMax); if (gen == typingGen && typingOn) TypingStop("90 s without a reply"); });
        }
        catch (Exception ex) { Log("typing", "start error: " + ex.Message); }
    }
    static void TypingStop(string why)
    {
        if (!typingOn) return;
        typingOn = false; Interlocked.Increment(ref typingGen);
        try { if (LoggedIn) TypingSend(false); } catch { }
        Log("typing", "stopped: " + why);
    }
    static void TypingSend(bool on)
    {
        if (typingIm)
            client.Self.InstantMessage(client.Self.Name, DavidId, "typing", DavidId ^ client.Self.AgentID,
                on ? InstantMessageDialog.StartTyping : InstantMessageDialog.StopTyping, InstantMessageOnline.Online,
                client.Self.SimPosition, UUID.Zero, Utils.EmptyBytes);
        else client.Self.Chat(string.Empty, 0, on ? ChatType.StartTyping : ChatType.StopTyping);
        if (typingIm) return;   // like a viewer: the typing animation only for nearby chat
        if (on) client.Self.AnimationStart(Animations.TYPE, true); else client.Self.AnimationStop(Animations.TYPE, true);
    }

    static string InstantCmdText(string[] a)
    {
        var cfg = LoadInstantCfg();
        if (a.Length > 0 && a[0] == "test")
        {
            var c = MatchInstant(cfg, string.Join(' ', a.Skip(1)));
            return c == null ? "no instant match (goes to the chat routine)" : $"instant match: {c.Action} (reply e.g. '{InstantReplyText(c, cfg.Greetings, false, instRnd)}')";
        }
        return cfg == null ? $"instant replies: {InstantFile} missing or invalid (all lines go to the routine)"
            : $"instant replies {(cfg.Enabled ? "ON" : "OFF")}: {string.Join(", ", cfg.Commands.Select(c => $"{c.Action} ({c.Phrases.Count} phrases)"))}; typing {(typingOn ? "on" : "off")}; last: {instLast}";
    }
}
