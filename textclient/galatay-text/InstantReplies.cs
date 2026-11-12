// InstantReplies.cs (2026-10-08 19:08, David: "There was a long delay before you answered me in nearby chat. Please make
// that faster"). His "start wandering!" waited 5 s in the webhook batch and ~60 s for the chat routine to start up.
// 1. Instant replies: a few clear requests from David (nearby chat or IM) are answered by the client itself in ~1 s:
//    wander_start / wander_stop / follow / follow_stop. Patterns + reply variants live in routes/_instant-replies.json
//    (read fresh on every line). The client acts first; only if the action worked does it reply and claim the line exactly
//    like 'say --re <id>' / 'im --re <id>' (Explicit answered), so the webhook does not POST it on its own and a later
//    '--re' on it is refused. Anything else (questions, chatty lines, failed actions, quiet mode) goes to the routine.
//    If he has just logged in and I have not said anything since, the greeting is folded into the reply ("Hi babe! On it!").
// 2. Typing: the human-like pause before a reply is unchanged (FastTypingDelayMs). The typing indicator is
//    sent; the typing animation must never play (2026-11-11 David). We mirror Firestorm with PlayTypingAnim
//    off (llagent.cpp LLAgent::startTyping / stopTyping): StartTyping chat / IM typing dialog, never
//    AnimationStart(TYPE), always AnimationStop(TYPE) on nearby stop. Worn typist AOs that fire on
//    AGENT_TYPING are stopped while the nearby indicator is on (see TypingIsTypistAnim).
// 'instant [status|test <text>]'.
using System.Collections.Concurrent;
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
                // 2026-11-11 David: "follow me" starts the real follow (same as the follow tool). Come-here first only if he is not in view.
                NoteDavidFollow("instant reply");
                return (true, followId == DavidId ? "following David" : "coming to David, then following");
            }
            case "come":
            {
                NoteDavidComeHere("instant reply");
                return (true, "coming to David");
            }
            case "kiss":
            {
                var r = KissCmd("David");
                return (r.StartsWith("kissed"), r);
            }
            case "follow_stop":
                if (!followingDavid) return (false, "not following him");
                return (true, FollowCmd("off"));
        }
        return (false, "unknown action '" + action + "'");
    }

    // ---- typing indicator -----------------------------------------------------------------------------------
    // 2026-11-11 David: send the typing indicator, but never play the typing animation.
    // Mirror Firestorm with PlayTypingAnim off (indra/newview/llagent.cpp):
    //   LLAgent::startTyping: if (PlayTypingAnim) sendAnimationRequest(ANIM_AGENT_TYPE, START);
    //     always sendChatFromViewer("", CHAT_TYPE_START) — the indicator.
    //   LLAgent::stopTyping: always sendAnimationRequest(ANIM_AGENT_TYPE, STOP) and CHAT_TYPE_STOP.
    //   IM typing (llimview.cpp pack_instant_message IM_TYPING_START/STOP) does not start TYPE.
    // Nearby: ChatType.Start/StopTyping, and AnimationStop(TYPE) (never AnimationStart).
    // IM: the typing dialog only.
    // PR 458 already did that. At 17:10 PT ANIM_AGENT_TYPE was not started (anim-events.log).
    // What David still saw: StartTyping sets AGENT_TYPING, and worn typist scripts started
    // 7513cd69 (Vista AO HUD @HUDCenter) and e352f82b (EvoX head @Chin) until the reply.
    // We AnimationStop those, and any attachment typist that starts in the AGENT_TYPING window,
    // while the nearby indicator is on. The delay before a reply stays. Pure helpers are unit-tested.
    internal const bool TypingIndicatorEnabled = true;
    static readonly TimeSpan TypingMax = TimeSpan.FromSeconds(90);
    internal const double TypingOverrideWindowSec = 1.5;
    // Observed 2026-11-11 17:10–17:11 in anim-events.log, in lockstep with [typing] started/stopped.
    internal static readonly UUID TypingAnimVistaAo = new("7513cd69-a25b-1ae0-5546-5aa94b4660b8");
    internal static readonly UUID TypingAnimEvoxHead = new("e352f82b-6253-c6a4-192b-bf5dafd48ca5");
    static readonly ConcurrentDictionary<UUID, byte> typistOverrideAnims = TypingSeedOverrides();
    static ConcurrentDictionary<UUID, byte> TypingSeedOverrides()
    {
        var d = new ConcurrentDictionary<UUID, byte>();
        d.TryAdd(TypingAnimVistaAo, 0);
        d.TryAdd(TypingAnimEvoxHead, 0);
        return d;
    }
    static int typingGen; static volatile bool typingOn; static bool typingIm;
    static DateTime typingStartedUtc;
    // What would be sent to the sim for a typing start/stop. Empty when disabled.
    // Nearby chat: ChatType.Start/StopTyping, and AnimationStop(TYPE) (never AnimationStart).
    // IM: the typing dialog only (no animation, like a viewer).
    internal static IReadOnlyList<string> TypingEmitActions(bool enabled, bool im, bool on)
    {
        if (!enabled) return Array.Empty<string>();
        if (im) return new[] { on ? "im-start-typing" : "im-stop-typing" };
        // Firestorm with PlayTypingAnim off still sends the typing chat; stopTyping always stops TYPE.
        // We also stop TYPE right after StartTyping, in case anything starts it from that chat.
        return on
            ? new[] { "chat-start-typing", "anim-type-stop" }
            : new[] { "chat-stop-typing", "anim-type-stop" };
    }
    // Pure: stop this animation so a typist pose never plays. known is TYPE-overrides already identified
    // (Vista AO / EvoX head, plus ones learned this session). Callers exclude gestures and seat poses.
    internal static bool TypingIsTypistAnim(UUID id, bool fromAttachment, double secondsSinceTypingStart, IReadOnlyCollection<UUID> known)
    {
        if (id == UUID.Zero) return false;
        if (id == Animations.TYPE) return true;
        if (known != null && known.Contains(id)) return true;
        return fromAttachment && secondsSinceTypingStart >= 0 && secondsSinceTypingStart <= TypingOverrideWindowSec;
    }
    internal static IReadOnlyCollection<UUID> TypingKnownOverrides() => typistOverrideAnims.Keys.ToArray();
    // Called from OnAvatarAnimation when one of our animations starts or restarts.
    static void TypingNoteAnimStarted(UUID id, UUID src, string srcDesc)
    {
        try
        {
            if (!TypingIndicatorEnabled || !typingOn || typingIm || !LoggedIn) return;
            if (GestureAnimOwnedNow(id)) return;
            if (src != UUID.Zero && IsSeatSource(src)) return;
            var fromAtt = src != UUID.Zero && src != client.Self.AgentID;
            var sec = (DateTime.UtcNow - typingStartedUtc).TotalSeconds;
            if (!TypingIsTypistAnim(id, fromAtt, sec, typistOverrideAnims.Keys.ToArray())) return;
            typistOverrideAnims.TryAdd(id, 0);
            client.Self.AnimationStop(id, true);
            Log("typing", $"stopped typist anim {AnimName(id)} from {srcDesc}");
        }
        catch (Exception ex) { Log("typing", "suppress error: " + ex.Message); }
    }
    static void TypingStopPlayingTypistAnims()
    {
        if (!typingOn || typingIm || !LoggedIn) return;
        try
        {
            List<UUID> playing;
            try { playing = client.Self.SignaledAnimations.Keys.ToList(); }
            catch { return; }
            var known = typistOverrideAnims.Keys.ToArray();
            foreach (var id in playing)
            {
                if (!TypingIsTypistAnim(id, fromAttachment: false, secondsSinceTypingStart: double.MaxValue, known)) continue;
                client.Self.AnimationStop(id, true);
            }
        }
        catch (Exception ex) { Log("typing", "sweep error: " + ex.Message); }
    }
    static void TypingStart(bool im)
    {
        try
        {
            if (!TypingIndicatorEnabled || !LoggedIn) return;
            var gen = Interlocked.Increment(ref typingGen);
            if (!typingOn || typingIm != im)
            {
                if (typingOn) TypingSend(false);
                typingIm = im; typingOn = true; typingStartedUtc = DateTime.UtcNow; TypingSend(true);
                Log("typing", $"started ({(im ? "IM" : "nearby chat")} from David; until my reply or {TypingMax.TotalSeconds:F0} s)");
                if (!im)
                {
                    _ = Task.Run(async () =>
                    {
                        for (int i = 0; i < 10 && gen == typingGen && typingOn && !typingIm; i++)
                        {
                            await Task.Delay(200);
                            TypingStopPlayingTypistAnims();
                        }
                    });
                }
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
        foreach (var a in TypingEmitActions(TypingIndicatorEnabled, typingIm, on))
        {
            switch (a)
            {
                case "im-start-typing":
                    client.Self.InstantMessage(client.Self.Name, DavidId, "typing", DavidId ^ client.Self.AgentID,
                        InstantMessageDialog.StartTyping, InstantMessageOnline.Online,
                        client.Self.SimPosition, UUID.Zero, Utils.EmptyBytes);
                    break;
                case "im-stop-typing":
                    client.Self.InstantMessage(client.Self.Name, DavidId, "typing", DavidId ^ client.Self.AgentID,
                        InstantMessageDialog.StopTyping, InstantMessageOnline.Online,
                        client.Self.SimPosition, UUID.Zero, Utils.EmptyBytes);
                    break;
                case "chat-start-typing":
                    client.Self.Chat(string.Empty, 0, ChatType.StartTyping);
                    break;
                case "chat-stop-typing":
                    client.Self.Chat(string.Empty, 0, ChatType.StopTyping);
                    break;
                case "anim-type-start":
                    client.Self.AnimationStart(Animations.TYPE, true);
                    break;
                case "anim-type-stop":
                    client.Self.AnimationStop(Animations.TYPE, true);
                    break;
            }
        }
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
