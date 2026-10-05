// Wander (added 2026-09-25): autonomous walk loop for Galatay in Naberrie (Buddha Center).
// Commands: wander start | stop | pause | hold | resume | status | selftest
// Home (Peronaut): same commands; uses routes/_graph-Peronaut.json + upper-level seats from _seats-peronaut-home.json
// Loop: deerpark <-> landing over the path graph (RouteNav GraphRoute + CheckBounds + FollowPoly), 25% zendo-edge spur at the landing.
// Sits: every 1-3 legs a free seat near the path (BC parcel, outside the zendo + margin, unoccupied, quiet if possible),
//       120-240 s (doubled 2026-09-26), then back to the path. Never David's pillow; her pillow only if no one but David is on the rock pillows.
// Greetings: avatars within 10 m (|dz| < 10), each avatar at most once per 24 h (2026-09-27 09:29, David; GT_GREET_REPEAT_HOURS, default 24; 0 = once ever), max one per 20 s, everyone incl. David and Sophie, not muted, not near the zendo.
//            History is keyed ONLY by avatar UUID (never display name). Chat/IM with someone also marks them greeted (2026-10-05), so a resume after auto-follow does not stranger-greet a friend.
//            After a greeting she stops, faces them and waits 15 s; a reply switches to the chat pause, otherwise the loop continues.
// Chat: nearby agent chat (<= 20 m) or an IM pauses the wander; she approaches to ~2.5 m (if allowed) and waits. No auto-reply.
//       Auto-resume after 3 min without chat from anyone nearby and without her own chat. 'wander hold' keeps it paused.
// Short pauses: every 15-45 m of a leg she stops 6-20 s (doubled 2026-09-26) (not near the zendo, on steps or next to people), often facing a feature or the view.
// Events use kind "wander" (poll_events only; never the webhook).
// Quiet mode (Quiet.cs, 2026-09-25): while a session is detected (>= 3 seated in the zendo or Deer Park) no greetings, deerpark legs
//   turn around >= 15 m short of an in-session Deer Park, the zendo spur is skipped, no seats in keep-outs, no chat approach into a session.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    static readonly string WanderStateFile = Env("GT_WANDER_STATE", "/home/box/viewers/textclient/wander-state.json");
    const string HomeWanderRegion = "Peronaut";
    static bool InPeronaut => string.Equals(client.Network.CurrentSim?.Name, HomeWanderRegion, StringComparison.OrdinalIgnoreCase);
    static bool InWanderRegion => InNaberrie || InPeronaut;
    static readonly string HomeSeatsFile = Path.Combine(RouteDir, "_seats-peronaut-home.json");
    static readonly Regex HomeSeatRx = new(@"\b(pillow|cushion|zafu|zabuton|seat|mat|bench|chair|stool|pouf|sofa|couch|desk|bed|tub|bath|lounger|hammock|rocking)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly UUID AlexNova = new("84a4e062-0511-4a1c-9983-866907907199");
    static readonly object wLock = new();
    static CancellationTokenSource wanderCts, legCts;
    static Task wanderTask;
    static volatile string wanderPause;          // null | "greet" (15 s reply window) | "chat" | "hold" | "user"
    static DateTime wGreetWaitUntil; static UUID wGreetWho; static string wGreetWhoName;
    static volatile string wanderPhase = "off";
    static volatile bool wanderResumeAfterRestart;
    static bool WanderOn => wanderTask != null && !wanderTask.IsCompleted;
    static bool WanderBlocksManualWalk => WanderOn && wanderPause is null or "chat" or "greet";
    static DateTime wStarted, wLastIncoming = DateTime.MinValue, wLastOwnChat = DateTime.MinValue, wLastGreet = DateTime.MinValue, wLastSnap = DateTime.MinValue, wPausedAt;
    static UUID wSpeaker; static string wSpeakerName; static Vector3 wSpeakerPos; static volatile bool wApproachPending;
    static int wLegs, wLoops, wSits, wGreets, wFails, wLegsUntilSit;
    static string wTarget, wLastLeg = "-", wLastSit = "-", wLastGreetTxt = "-";
    static readonly Dictionary<UUID, DateTime> wGreeted = new();
    // last-greeted time per avatar UUID survives daemon restarts (2026-09-25 22:15; restarts had wiped it -> repeat greetings)
    static readonly string GreetHistoryFile = Env("GT_GREET_HISTORY", "/home/box/viewers/textclient/run/greet-history.json");
    static readonly Dictionary<UUID, DateTime> wSeatFailed = new();
    static UUID wLastSeat = UUID.Zero;
    static readonly Random wRnd = new();
    static int wGreetIdx = -1;
    static void WLog(string m) => Log("wander", m);

    // ---- persistence ---------------------------------------------------------------------------------
    // Greet rule (2026-09-27 09:29, David): each avatar at most once per 24 h, everyone (David and Sophie included).
    // GT_GREET_REPEAT_HOURS (default 24, also exported by text-galatay.sh); 0 = once ever. The greet history is permanent
    // (never pruned) in every mode. (Before 09:25: 60 min window + 24 h pruning -> EricBroadbent greeted at 05:09 and 07:08.)
    static readonly double GreetRepeatHours = double.TryParse(Env("GT_GREET_REPEAT_HOURS", "24"), NumberStyles.Float, CultureInfo.InvariantCulture, out var grh) && grh > 0 ? grh : 0;
    static double GreetKeepHours => double.PositiveInfinity;
    static bool GreetedRecently(Dictionary<UUID, DateTime> greeted, UUID id, DateTime now, double repeatHours) =>
        greeted.TryGetValue(id, out var t) && (repeatHours <= 0 || (now - t).TotalHours < repeatHours);
    // greet history: {"<uuid>": "<ISO time with offset>", ...}; keys MUST be avatar UUIDs (display names are ignored on load).
    // entries older than keepHours are dropped (none in "once" mode)
    static string SerializeGreetHistory(Dictionary<UUID, DateTime> h, DateTime now, double keepHours = double.NaN)
    {
        if (double.IsNaN(keepHours)) keepHours = GreetKeepHours;
        var o = new JsonObject();
        foreach (var kv in h.Where(kv => (now - kv.Value).TotalHours < keepHours).OrderBy(kv => kv.Value))
            o[kv.Key.ToString()] = kv.Value.ToString("o", CultureInfo.InvariantCulture);
        return o.ToJsonString();
    }
    static Dictionary<UUID, DateTime> ParseGreetHistory(string json, DateTime now, double keepHours = double.NaN)
    {
        if (double.IsNaN(keepHours)) keepHours = GreetKeepHours;
        var r = new Dictionary<UUID, DateTime>();
        try
        {
            foreach (var kv in JsonNode.Parse(json)!.AsObject())
            {
                if (!UUID.TryParse(kv.Key, out var id) || kv.Value is null) continue;
                if (!DateTime.TryParse((string)kv.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)) continue;
                t = t.Kind == DateTimeKind.Utc ? t.ToLocalTime() : t;
                if ((now - t).TotalHours < keepHours) r[id] = t;
            }
        }
        catch { }
        return r;
    }
    static void SaveGreetHistory(string path = null)
    {
        try
        {
            string js; lock (wGreeted) js = SerializeGreetHistory(wGreeted, DateTime.Now);
            path ??= GreetHistoryFile; var tmp = path + ".tmp"; File.WriteAllText(tmp, js); File.Move(tmp, path, true);
        }
        catch (Exception ex) { WLog("greet history write failed: " + ex.Message); }
    }
    // merge the file into memory (keeps the later time per avatar)
    static int LoadGreetHistory(string path = null)
    {
        path ??= GreetHistoryFile;
        if (!File.Exists(path)) return 0;
        Dictionary<UUID, DateTime> h;
        try { h = ParseGreetHistory(File.ReadAllText(path), DateTime.Now); } catch (Exception ex) { WLog("greet history read failed: " + ex.Message); return 0; }
        lock (wGreeted) foreach (var kv in h) if (!wGreeted.TryGetValue(kv.Key, out var t) || kv.Value > t) wGreeted[kv.Key] = kv.Value;
        return h.Count;
    }
    // mark an avatar as already-greeted / known (by UUID only). Used after a greeting AND after chat/IM so a wander
    // resume never stranger-greets someone she was just talking to. Display names are never keys.
    static void NoteGreeted(UUID id, DateTime? when = null)
    {
        if (id == UUID.Zero) return;
        try { if (client != null && id == client.Self.AgentID) return; } catch { }
        var t = when ?? DateTime.Now;
        lock (wGreeted)
        {
            if (wGreeted.TryGetValue(id, out var old) && old >= t) return;
            wGreeted[id] = t;
        }
        SaveGreetHistory();
    }
    // stop = "deliberate" is written only by a deliberate shutdown (text-galatay.sh stop / logout command); every other
    // write (start, heartbeat, crash-safe keeps) omits it, so a flag last written by a heartbeat means "went down unexpectedly".
    static void SaveWanderFlag(bool enabled, string reason, string stop = null)
    {
        try
        {
            var o = new JsonObject { ["enabled"] = enabled, ["saved_at"] = DateTime.Now.ToString("o", CultureInfo.InvariantCulture), ["reason"] = reason };
            if (stop != null) o["stop"] = stop;
            var tmp = WanderStateFile + ".tmp"; File.WriteAllText(tmp, o.ToJsonString()); File.Move(tmp, WanderStateFile, true);
        }
        catch (Exception ex) { WLog("state file write failed: " + ex.Message); }
    }
    static (bool enabled, DateTime at, string stop, string reason) LoadWanderFlag()
    {
        try
        {
            var j = JsonNode.Parse(File.ReadAllText(WanderStateFile))!;
            var at = DateTime.Parse((string)j["saved_at"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (at.Kind == DateTimeKind.Utc) at = at.ToLocalTime();
            return ((bool?)j["enabled"] ?? false, at, (string)j["stop"], (string)j["reason"] ?? "");
        }
        catch { return (false, DateTime.MinValue, null, ""); }
    }
    // Resume rule (2026-09-26 10:51, after the 10:22 reboot left the wander off): the flag is refreshed every 60 s while
    // the wander is on, so after a reboot / kill / crash / long outage it is naturally stale. Stale alone no longer blocks:
    // resume if the flag is on, unless the last shutdown was deliberate (text-galatay.sh stop or a logout command) and it
    // is stale (a deliberate stop + start within 15 min, i.e. a deploy restart, still resumes as before). 'wander stop'
    // clears the flag, so it never resumes. Pure (unit-tested in 'wander selftest' / 'wander resumetest').
    static (bool resume, string why) WanderResumeDecision(bool enabled, DateTime savedAt, string stop, string reason, DateTime now)
    {
        if (!enabled) return (false, "flag off ('wander stop', left Naberrie, gave up, or never started)");
        var age = now - savedAt; bool stale = age.TotalMinutes > 15; bool deliberate = stop == "deliberate";
        string ago = age.TotalHours >= 1 ? $"{(int)age.TotalHours} h {age.Minutes} min" : $"{Math.Max(0, (int)age.TotalMinutes)} min";
        if (!stale) return (true, $"flag on and fresh (saved {savedAt:HH:mm:ss}, {ago} ago; '{reason}')");
        if (deliberate) return (false, $"flag on but stale (saved {savedAt:HH:mm:ss}, {ago} ago) and the last shutdown was a deliberate stop ('{reason}')");
        return (true, $"flag on, stale (saved {savedAt:HH:mm:ss}, {ago} ago) but the last shutdown was not deliberate (last write '{reason}': reboot, crash, kill or outage)");
    }
    // called after a successful login (Main): resume per WanderResumeDecision (daemon restart / deploy / reboot / crash)
    static async Task WanderAutoResume()
    {
        try
        {
            var (en, at, stop, reason) = LoadWanderFlag();
            if (!en) return;
            var (resume, why) = WanderResumeDecision(en, at, stop, reason, DateTime.Now);
            if (!resume) { WLog($"state file: {why}: staying off"); SaveWanderFlag(false, "stale flag after a deliberate stop"); return; }
            WLog($"state file: {why}: auto-resume in 45 s");
            await Task.Delay(45000, cts.Token); // let sit_home / objects settle
            // the AO guard in StartWander may refuse while the AO comes back after login; re-check (never touches the HUD)
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                if (WanderOn || !LoggedIn || RestartActive || !InWanderRegion) { WLog("auto-resume skipped (already on, restart handling, or left wander region)"); return; }
                var (e2, _, _, _) = LoadWanderFlag();
                if (!e2) { WLog("auto-resume cancelled: the flag was cleared meanwhile ('wander stop')"); return; }
                WLog($"auto-resume after daemon restart (state flag on){(attempt > 1 ? $", attempt {attempt}" : "")}");
                var r = StartWander("auto-resume after restart");
                if (!r.StartsWith("refused: AO")) return;
                if (attempt < 5) { WLog("auto-resume: AO not active yet; re-checking in 30 s"); await Task.Delay(30000, cts.Token); }
                else { WLog("auto-resume gave up: AO still not active after 5 checks (flag kept; 'wander start' when the AO is on)"); SaveWanderFlag(true, "auto-resume waiting for the AO"); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { WLog("auto-resume error: " + ex.GetBaseException().Message); }
    }
    static string ResumeSelfTest()
    {
        var lines = new List<string>(); int pass = 0, fail = 0;
        var now = new DateTime(2026, 9, 26, 10, 40, 45);
        void T(string name, bool en, double minAgo, string stop, bool expect)
        {
            var (r, why) = WanderResumeDecision(en, now.AddMinutes(-minAgo), stop, "test", now); bool ok = r == expect; if (ok) pass++; else fail++;
            lines.Add($"{(ok ? "PASS" : "FAIL")} {name} -> {(r ? "resume" : "stay off")} ({why})");
        }
        T("reboot 10:22 -> manual start 10:40 (heartbeat 10:21:57, 19 min)", true, 18.8, null, true);
        T("box paused/down 3 h, heartbeat flag", true, 180, null, true);
        T("crash + relaunch within 1 min", true, 1, null, true);
        T("deploy: stop + start within 1 min (deliberate, fresh)", true, 0.2, "deliberate", true);
        T("deliberate stop, started again 2 h later", true, 120, "deliberate", false);
        T("deliberate stop, 16 min later", true, 16, "deliberate", false);
        T("'wander stop' then restart (flag off)", false, 0.5, null, false);
        T("'wander stop' then reboot (flag off, stale)", false, 60, null, false);
        T("watchdog relogin keep (flag on, no stop field)", true, 0.3, null, true);
        T("unknown stop kind, stale -> not deliberate", true, 30, "other", true);
        return $"wander resume selftest: {pass} pass, {fail} fail (offline)\n" + string.Join("\n", lines);
    }

    // ---- start / stop --------------------------------------------------------------------------------
    static string StartWander(string why)
    {
        lock (wLock)
        {
            if (WanderOn) return "wander already running (" + WanderStatus() + ")";
            if (!LoggedIn) return "not logged in";
            if (!InWanderRegion) return "wander only works in Naberrie (Buddha Center) or Peronaut (home)";
            if (InPeronaut && LoadGraph(HomeWanderRegion) == null) return "wander at home needs routes/_graph-Peronaut.json";
            if (RestartActive) return "region restart handling is active; not starting";
            if (client.Self.SittingOn == 0 && !AoStateNow().active) { AoLog($"wander start REFUSED ({why}): {AoStateNow().why}"); return "refused: AO not active (" + AoStateNow().why + "); not starting the wander"; }
            if (walkTask != null && !walkTask.IsCompleted) { walkCts?.Cancel(); client.Self.AutoPilotCancel(); }
            wanderCts = new CancellationTokenSource();
            var ct = wanderCts.Token;
            wanderPause = null; wApproachPending = false; wFails = 0; wLegs = 0; wLoops = 0; wSits = 0; wGreets = 0;
            wLegsUntilSit = wRnd.Next(1, 4); endsReachedHome = 0; wStarted = DateTime.Now; wLastSnap = DateTime.Now.AddMinutes(-9.5); // first picture ~30 s in
            wanderResumeAfterRestart = false;
            SaveWanderFlag(true, why);
            var gh = LoadGreetHistory();
            if (gh > 0) WLog($"greet history: {gh} avatar(s) loaded from {GreetHistoryFile}; rule: {(GreetRepeatHours <= 0 ? "each avatar greeted once, never again" : $"again after {GreetRepeatHours:0.#} h")}");
            wanderPhase = "starting";
            wanderTask = Task.Run(async () =>
            {
                try { await WanderLoop(ct); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { WLog("loop error: " + ex.GetBaseException().Message); }
                finally { try { client.Self.AutoPilotCancel(); } catch { } wanderPhase = "off"; wanderPause = null; blockIgnore = UUID.Zero; }
            });
            _ = Task.Run(() => GreetLoop(ct));
            WLog($"started ({why}); first sit after {wLegsUntilSit} leg(s)");
            return $"wander started ({why}); first sit after {wLegsUntilSit} leg(s)";
        }
    }
    // clearFlag=false keeps the persisted flag (logout / restart evacuation) so it can resume later
    static string StopWander(string why, bool clearFlag = true, string stopKind = null)
    {
        bool was;
        lock (wLock)
        {
            was = WanderOn;
            wanderCts?.Cancel(); legCts?.Cancel();
            if (clearFlag) SaveWanderFlag(false, why);
            else if (was) SaveWanderFlag(true, why, stopKind);
        }
        if (was) { try { client.Self.AutoPilotCancel(); } catch { } WLog($"stopped ({why}); legs {wLegs}, loops {wLoops}, sits {wSits}, greetings {wGreets}"); }
        return was ? $"wander stopped ({why})" : "wander was not running";
    }
    // deliberate = text-galatay.sh stop (run/stop-requested or run/deliberate-stop present) or a logout command: the kept flag
    // is marked stop=deliberate, so it resumes only on a start within 15 min (deploy restart). Anything else (SIGTERM
    // without a stop request, e.g. a reboot) keeps it unmarked -> resumes even when stale.
    static void WanderOnShutdown(bool deliberate = false)
    {
        if (WanderOn) { StopWander(deliberate ? "deliberate stop (flag kept; resumes only on a start within 15 min)" : "logout/shutdown (flag kept; resumes after the restart)", clearFlag: false, stopKind: deliberate ? "deliberate" : null); return; }
        if (!deliberate) return;
        var (en, _, _, reason) = LoadWanderFlag(); // e.g. paused for a region restart: still mark the stop as deliberate
        if (en) SaveWanderFlag(true, "deliberate stop (was: " + reason + ")", "deliberate");
    }
    static void WanderOnRestart()
    {
        if (!WanderOn) return;
        wanderResumeAfterRestart = true;
        StopWander("region restart evacuation (will resume after the return)", clearFlag: false);
    }
    static void WanderAfterRestartReturn()
    {
        if (!wanderResumeAfterRestart) return;
        wanderResumeAfterRestart = false;
        _ = Task.Run(async () =>
        {
            await Task.Delay(10000);
            if (InWanderRegion && !WanderOn && !RestartActive) { WLog("resuming after the region restart return"); StartWander("resume after region restart"); }
            else WLog("not resuming after the restart return (not in Naberrie or already running)");
        });
    }

    static void SetPause(string reason, string why)
    {
        wanderPause = reason; wPausedAt = DateTime.Now;
        legCts?.Cancel(); try { client.Self.AutoPilotCancel(); } catch { }
        WLog($"PAUSE ({reason}): {why}");
    }

    // ---- chat hooks (called from Program.Hook) -------------------------------------------------------
    static void WanderOwnChat() { wLastOwnChat = DateTime.Now; }
    static void WanderChatIn(UUID id, string name, Vector3 pos, bool im)
    {
        try
        {
            if (id == UUID.Zero || id == client.Self.AgentID) return;
            NoteGreeted(id); // by UUID: a chat/IM partner is never a stranger-greet candidate (even while wander is off / on hold)
            if (!WanderOn) return;
            var me = client.Self.SimPosition;
            var av = client.Network.CurrentSim?.ObjectsAvatars.Values.FirstOrDefault(x => x != null && x.ID == id);
            var sim = client.Network.CurrentSim;
            if (av != null && sim != null && (av.ParentID == 0 || sim.ObjectsPrimitives.ContainsKey(av.ParentID))) pos = PositionHelper.GetAvatarPosition(sim, av);
            float dist = pos == Vector3.Zero ? -1 : Vector3.Distance(pos, me);
            if (!im && (dist < 0 || dist > 20f)) return; // local chat from farther away doesn't pause
            wLastIncoming = DateTime.Now;
            var p = wanderPause;
            if (p is "hold" or "user" or "ao") { WLog($"{(im ? "IM" : "chat")} from {name} while paused ({p}): no action"); return; }
            if (p == "greet") { WLog($"{name} spoke during the greeting reply window ({(id == wGreetWho ? "the greeted avatar" : "someone else nearby")})"); p = null; }
            bool newSpeaker = p == null || wSpeaker != id;
            wSpeaker = id; wSpeakerName = name; wSpeakerPos = pos;
            if (dist >= 0 && dist <= 40f) wApproachPending = true;
            if (p == null) SetPause("chat", $"{(im ? "IM" : "nearby chat")} from {name}{(dist >= 0 ? $" ({dist:F0} m)" : " (not nearby)")}; waiting for the conversation (auto-resume after 3 min quiet)");
            else if (newSpeaker) WLog($"chat pause: now following {name}{(dist >= 0 ? $" ({dist:F0} m)" : "")}");
        }
        catch (Exception ex) { WLog("chat hook error: " + ex.Message); }
    }

    // ---- short greeting replies (2026-09-27 10:10, David) --------------------------------------------------
    // A nearby-chat reply to her greeting that is only a short acknowledgement ("Thank you!", "hi", "_/\_", ":)", "/me bows")
    // is NOT a conversation: no pause, no approach, no webhook. Conservative: every word must be an acknowledgement/greeting/
    // bow/emoji word (or harmless filler), at least one real acknowledgement token, <= 5 words and <= 40 chars, no '?',
    // speaker greeted by her within the last 2 minutes, and no chat conversation currently going on. IMs are never filtered.
    const double ShortReplyWindowSec = 120;
    static readonly HashSet<string> AckCore = new(StringComparer.Ordinal)
    {
        "thanks", "thank", "thankyou", "thanx", "thanks!", "thnx", "thx", "tx", "ty", "tyvm", "tysm", "cheers", "merci", "danke", "gracias", "grazie",
        "hi", "hii", "hiii", "hello", "helo", "hallo", "hey", "heya", "hiya", "hya", "hai", "howdy", "hola", "greetings", "yo",
        "namaste", "namaskar", "gassho", "bows", "bow", "bowing", "smiles", "smile", "waves", "wave", "nods", "nod", "grins", "hugs",
        "welcome", "yw", "np", "peace", "blessings", "likewise", "gm", "morning", "evening", "afternoon",
    };
    static readonly HashSet<string> AckFiller = new(StringComparer.Ordinal)
    {
        "you", "u", "ya", "yu", "too", "2", "so", "much", "very", "lots", "a", "lot", "and", "to", "same", "as", "well", "also",
        "dear", "my", "friend", "sister", "galatea", "galatay", "gala", "good", "day", "all", "back", "again", "oh", "aw", "aww",
        "your", "yours", "youre", "you're", "ur", "kindly", "deeply", "warmly", "politely", "softly", "gently", "back", "in", "return",
    };
    static readonly string[] AckPhrases = { "you too", "same to you", "and you", "and to you", "you as well", "same here", "right back at you" };
    static bool IsEmojiToken(string t) => t.Length > 0 && !t.Any(char.IsLetterOrDigit);
    // pure (unit-tested)
    static (bool ack, string why) ShortGreetReply(string text, DateTime? greetedAt, DateTime now, bool inConversation)
    {
        if (inConversation) return (false, "a chat conversation is going on");
        if (greetedAt == null) return (false, "not greeted by her recently");
        var ago = (now - greetedAt.Value).TotalSeconds;
        if (ago < 0 || ago > ShortReplyWindowSec) return (false, $"greeted {ago:F0} s ago (> {ShortReplyWindowSec:F0} s)");
        var t = (text ?? "").Trim();
        if (t.StartsWith("/me ", StringComparison.OrdinalIgnoreCase)) t = t[4..].Trim();
        if (t.Length == 0) return (false, "empty");
        if (t.Contains('?') || t.Contains('\uFF1F')) return (false, "contains a question");
        if (t.Length > 40) return (false, $"too long ({t.Length} chars)");
        var toks = t.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        if (toks.Length > 5) return (false, $"too many words ({toks.Length})");
        int core = 0;
        // reply phrases whose words are only filler on their own ("you too", "same to you", "and you")
        var norm = " " + System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(t.ToLowerInvariant(), @"[^a-z']+", " "), @"\s+", " ").Trim() + " ";
        foreach (var ph in AckPhrases) if (norm.Contains(" " + ph + " ", StringComparison.Ordinal)) { core++; break; }
        foreach (var raw in toks)
        {
            if (IsEmojiToken(raw)) { core++; continue; }            // _/\_  :)  🙏  <3  ^^
            foreach (var part in raw.ToLowerInvariant().Replace('\u2019', '\'').Split('-', ',', '.', '!', '~', '*', ';'))
            {
                var w = part.Trim('(', ')', '"', ':', '<', '>', '^', '_', '/', '\\', '=');
                if (w.Length == 0) continue;
                if (AckCore.Contains(w)) { core++; continue; }
                if (AckFiller.Contains(w)) continue;
                if (IsEmojiToken(w)) { core++; continue; }
                if (w.All(ch => char.IsLetter(ch) && ch < 128) && System.Text.RegularExpressions.Regex.IsMatch(w, "^(t+h+a+n+k+s+|h+i+|h+e+y+|h+e+l+l+o+|a+w+)$")) { core++; continue; } // thaaanks, hiii, heyyy
                if (w.Any(ch => char.GetUnicodeCategory(ch) is System.Globalization.UnicodeCategory.OtherSymbol or System.Globalization.UnicodeCategory.Surrogate)
                    && !w.Any(char.IsLetterOrDigit)) { core++; continue; }
                return (false, $"real content ('{w}')");
            }
        }
        if (core == 0) return (false, "no acknowledgement word");
        return (true, $"greeted {ago:F0} s ago; short acknowledgement");
    }
    static DateTime? GreetedAt(UUID id) { lock (wGreeted) return wGreeted.TryGetValue(id, out var t) ? t : null; }
    static bool InChatConversation => wanderPause == "chat";
    // called by Program.Hook for an ignored short reply: if we are still in the 15 s greet window for this avatar, end it now
    static void WanderShortReply(UUID id, string name)
    {
        try
        {
            if (WanderOn && wanderPause == "greet" && id == wGreetWho) { wGreetWaitUntil = DateTime.Now; WLog($"short greeting reply from {name}: no conversation, continuing the loop"); }
        }
        catch { }
    }

    // ---- graph helpers -------------------------------------------------------------------------------
    static (Vector3 p, float d) NearestOnGraph(Graph g, Vector3 x)
    {
        Vector3 best = Vector3.Zero; float bd = float.MaxValue;
        foreach (var e in g.E)
        {
            var a = g.N[e.a]; var b = g.N[e.b]; var d = new Vector3(b.X - a.X, b.Y - a.Y, 0); float l2 = d.X * d.X + d.Y * d.Y;
            float t = l2 < 1e-6f ? 0 : Math.Clamp(((x.X - a.X) * d.X + (x.Y - a.Y) * d.Y) / l2, 0, 1);
            var q = Vector3.Lerp(a, b, t); float dist = HDist(q, x);
            if (dist < bd) { bd = dist; best = q; }
        }
        return (best, bd);
    }
    // route over the graph from 'from' to an arbitrary point on the network ('to' is projected onto the nearest edge)
    static (List<Vector3> pts, string err) GraphRouteTo(Graph g, Vector3 from, Vector3 to)
    {
        int bestE = -1; float bd = float.MaxValue, bt = 0;
        for (int k = 0; k < g.E.Count; k++)
        {
            var a = g.N[g.E[k].a]; var b = g.N[g.E[k].b]; var d = new Vector3(b.X - a.X, b.Y - a.Y, 0); float l2 = d.X * d.X + d.Y * d.Y;
            float t = l2 < 1e-6f ? 0 : Math.Clamp(((to.X - a.X) * d.X + (to.Y - a.Y) * d.Y) / l2, 0, 1);
            float dist = HDist(Vector3.Lerp(a, b, t), to);
            if (dist < bd) { bd = dist; bestE = k; bt = t; }
        }
        if (bestE < 0) return (null, "graph has no edges");
        // temporary graph with a goal node splitting that edge
        var g2 = new Graph(); g2.N.AddRange(g.N); g2.E.AddRange(g.E);
        var e0 = g.E[bestE]; var goalP = Vector3.Lerp(g.N[e0.a], g.N[e0.b], bt);
        int gi = g2.N.Count; g2.N.Add(goalP); g2.E.Add((e0.a, gi, "virt")); g2.E.Add((gi, e0.b, "virt"));
        var r = GraphRoute(g2, from, gi);
        return r;
    }


    // ---- Peronaut home wander (upper level only; lower beach catalogued but not on the path graph yet) ------
    static async Task HomeWanderLoop(CancellationToken ct)
    {
        var g = LoadGraph(HomeWanderRegion) ?? throw new InvalidOperationException("no path graph for Peronaut");
        string[] ends = { "front", "chairs", "patio-sw", "living", "home", "patio-east", "east-deck" };
        ends = ends.Where(n => g.Places.ContainsKey(n)).ToArray();
        if (ends.Length == 0) throw new InvalidOperationException("Peronaut graph has no wander places");
        var me0 = client.Self.SimPosition;
        wTarget = ends.OrderBy(n => HDist(me0, g.N[g.Places[n].node])).First();
        while (!ct.IsCancellationRequested)
        {
            if (!LoggedIn) { await Task.Delay(2000, ct); continue; }
            if (!InPeronaut) { WLog($"left Peronaut (now {client.Network.CurrentSim?.Name ?? "-"}): stopping"); SaveWanderFlag(false, "left Peronaut"); return; }
            if (RestartActive) { wanderResumeAfterRestart = true; WLog("region restart handling active: stopping (resume after the return)"); SaveWanderFlag(true, "restart"); return; }
            MaybeSnapshot();
            var pause = wanderPause;
            if (pause != null)
            {
                wanderPhase = "paused (" + pause + ")";
                if (pause == "greet")
                {
                    var gav = Avatars().FirstOrDefault(t => t.av.ID == wGreetWho && t.dist >= 0);
                    if (gav.av != null && client.Self.SittingOn == 0) client.Self.Movement.TurnToward(gav.pos);
                    if (DateTime.Now >= wGreetWaitUntil && wanderPause == "greet")
                    { wanderPause = null; WLog($"no reply from {wGreetWhoName} within 15 s: continuing the loop"); continue; }
                    await Task.Delay(500, ct); continue;
                }
                if (pause == "chat")
                {
                    if (wApproachPending) { wApproachPending = false; await ApproachSpeaker(ct); }
                    var quiet = DateTime.Now - (wLastIncoming > wLastOwnChat ? wLastIncoming : wLastOwnChat);
                    if (quiet.TotalSeconds >= 180 && wanderPause == "chat")
                    { wanderPause = null; WLog($"RESUME: no chat for {quiet.TotalSeconds:F0} s (paused {(DateTime.Now - wPausedAt).TotalSeconds:F0} s for {wSpeakerName})"); continue; }
                }
                await Task.Delay(1000, ct); continue;
            }
            if (wLegsUntilSit <= 0)
            {
                wanderPhase = "choosing a seat";
                var r = await HomeRandomSit(g, ct);
                wLegsUntilSit = r ? wRnd.Next(1, 4) : 1;
                continue;
            }
            var target = wTarget;
            wanderPhase = $"walking to {target}";
            var (pts, err) = GraphRoute(g, client.Self.SimPosition, g.Places[target].node);
            bool ok; string msg;
            var t0 = DateTime.Now;
            if (err != null) { ok = false; msg = "no route: " + err; }
            else
            {
                var poly = new Poly(pts);
                var o = new RouteOpts { Label = $"home wander leg to {target}", Place = target, Idle = true };
                var b = await CheckBounds(poly, o);
                if (b != null) { ok = false; msg = "refused: " + b; }
                else
                {
                    legCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    wLegGoal = poly.P[^1]; wLegHasGoal = true;
                    try { (ok, msg) = await FollowPoly(poly, o, legCts.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    { client.Self.AutoPilotCancel(); WLog($"leg to {target} interrupted ({wanderPause ?? "cancel"}) after {(DateTime.Now - t0).TotalSeconds:F0} s"); continue; }
                    catch (OperationCanceledException) { client.Self.AutoPilotCancel(); throw; }
                    finally { wLegHasGoal = false; }
                }
            }
            if (ct.IsCancellationRequested) return;
            if (!InPeronaut) continue;
            if (wanderPause != null) continue;
            if (ok)
            {
                wFails = 0; wLegs++;
                wLastLeg = $"{DateTime.Now:HH:mm:ss} {target}: {msg}";
                WLog($"LEG {wLegs} done -> {target} ({(DateTime.Now - t0).TotalSeconds:F0} s): {msg}");
                endsReachedHome++;
                if (endsReachedHome % 2 == 0) wLoops++;
                wLegsUntilSit--;
                // pick a different end
                var others = ends.Where(n => n != target).ToArray();
                wTarget = others.Length == 0 ? target : others[wRnd.Next(others.Length)];
            }
            else
            {
                if (msg.Contains("AO not active")) { WLog($"PAUSE (ao): {msg}; staying put until the AO is confirmed and 'wander resume'"); wanderPause = "ao"; wPausedAt = DateTime.Now; continue; }
                if (msg.Contains("she is seated")) { WLog("leg ended because she is seated: pausing (user)"); wanderPause = "user"; wPausedAt = DateTime.Now; continue; }
                wFails++;
                wLastLeg = $"{DateTime.Now:HH:mm:ss} {target} FAILED: {msg}";
                WLog($"LEG to {target} FAILED ({wFails}/3 in a row): {msg}");
                if (wFails >= 3) { await HomeWanderRecover(g, "3 failed legs in a row"); return; }
                var others = ends.Where(n => n != target).ToArray();
                if (others.Length > 0) { wTarget = others[wRnd.Next(others.Length)]; WLog($"turning around: next target {wTarget}"); }
                await Task.Delay(5000, ct);
            }
        }
    }
    static int endsReachedHome;

    static async Task HomeWanderRecover(Graph g, string why)
    {
        wanderPhase = "recovering to sofa";
        WLog($"GIVING UP ({why}): walking to living/sofa and sitting");
        SaveWanderFlag(false, "gave up: " + why);
        using var rc = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        string res;
        try
        {
            var place = g.Places.ContainsKey("living") ? "living" : "home";
            var (pts, err) = GraphRoute(g, client.Self.SimPosition, g.Places[place].node);
            if (err != null) res = "no route to " + place + ": " + err;
            else res = await RunRoute(pts, new RouteOpts { Label = "home wander recovery to " + place, Place = place, Idle = true }, rc.Token);
        }
        catch (Exception ex) { client.Self.AutoPilotCancel(); res = "recovery walk failed: " + ex.GetBaseException().Message; }
        if (client.Self.SittingOn == 0)
        {
            try
            {
                var sofa = UUID.Parse("f6844138-499f-0f3e-ba6a-ce601a16eafe");
                res += "; sit sofa: " + await Exec("sit " + sofa);
            }
            catch (Exception ex) { res += "; sit sofa error " + ex.Message; }
        }
        WLog("stopped after home recovery: " + res);
    }

    static HashSet<UUID> LoadHomeSeatIds()
    {
        var ids = new HashSet<UUID>();
        try
        {
            if (!File.Exists(HomeSeatsFile)) return ids;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(HomeSeatsFile));
            if (!doc.RootElement.TryGetProperty("seats", out var seats)) return ids;
            foreach (var s in seats.EnumerateArray())
            {
                var level = s.TryGetProperty("level", out var lv) ? lv.GetString() ?? "" : "";
                if (!level.StartsWith("upper", StringComparison.OrdinalIgnoreCase)) continue; // home wander stays on upper floor
                if (s.TryGetProperty("uuid", out var u) && UUID.TryParse(u.GetString(), out var id)) ids.Add(id);
            }
        }
        catch (Exception ex) { WLog("home seats file: " + ex.Message); }
        return ids;
    }

    static async Task<List<SeatCand>> HomeWanderSeats(Graph g, List<string> dbg = null)
    {
        var sim = Sim; var me = client.Self.SimPosition;
        var prefer = LoadHomeSeatIds();
        var allowed = await AllowedParcel(sim);
        var raw = new List<(Primitive p, Vector3 q, float d)>();
        foreach (var p in sim.ObjectsPrimitives.Values)
        {
            if (p == null || p.ParentID != 0 || p.PrimData.PCode != PCode.Prim) continue;
            if (HDist(p.Position, me) > 40f) continue;
            if (p.Position.Z < 27f || p.Position.Z > 32f) continue; // upper level only
            var (q, d) = NearestOnGraph(g, p.Position);
            if (d > 14f) continue;
            raw.Add((p, q, d));
        }
        await EnsureProperties(sim, raw.Select(t => t.p).ToList());
        dbg?.Add($"  ({raw.Count} upper-level root prims near path; prefer {prefer.Count} catalogued seats)");
        var sit = Sitters(sim);
        var avs = Avatars().Where(t => t.dist >= 0).ToList();
        var now = DateTime.Now;
        var res = new List<SeatCand>();
        foreach (var (p, q, d) in raw)
        {
            var name = p.Properties?.Name ?? "";
            bool listed = prefer.Contains(p.ID);
            if (!listed && (!HomeSeatRx.IsMatch(name) || WSeatBad.IsMatch(name))) continue;
            void R(string why) => dbg?.Add($"  - '{name}' {p.ID} {P3(p.Position)}: {why}");
            if (Math.Max(p.Scale.X, Math.Max(p.Scale.Y, p.Scale.Z)) > 10f) { R("too big"); continue; }
            if (sit.ContainsKey(p.LocalID)) { R("occupied"); continue; }
            if (p.ID == wLastSeat) { R("sat there last time"); continue; }
            bool cool; lock (wSeatFailed) cool = wSeatFailed.TryGetValue(p.ID, out var ft) && (now - ft).TotalMinutes < 30;
            if (cool) { R("failed recently (30 min cooldown)"); continue; }
            var level = avs.Where(t => Math.Abs(t.pos.Z - p.Position.Z) < 10f).ToList();
            float quiet = level.Count == 0 ? 999f : level.Min(t => HDist(t.pos, p.Position));
            if (quiet < 3f) { R($"avatar {quiet:F1} m away"); continue; }
            res.Add(new SeatCand(p, name, HDist(p.Position, me), d, quiet, q));
        }
        var outp = new List<SeatCand>();
        foreach (var c in res.OrderBy(c => c.fromMe).Take(24))
        {
            var pid = await ParcelAt(sim, c.p.Position);
            if (ParcelOk(pid, allowed)) outp.Add(c);
            else dbg?.Add($"  - '{c.name}' {c.p.ID} {P3(c.p.Position)}: outside home parcel");
        }
        return outp;
    }

    static async Task<bool> HomeRandomSit(Graph g, CancellationToken ct)
    {
        var cands = await HomeWanderSeats(g);
        var quiet = cands.Where(c => c.quiet >= 10f).OrderBy(c => c.fromMe).Take(8).ToList();
        var pool = quiet.Count > 0 ? quiet : cands.OrderBy(c => c.fromMe).Take(5).ToList();
        if (pool.Count == 0) { WLog("sit: no free upper-level seat near here (skipping this time)"); return false; }
        var c = pool[wRnd.Next(pool.Count)];
        var seatPos = c.p.Position;
        WLog($"SIT target: '{c.name}' {c.p.ID} at {P3(seatPos)} ({c.fromMe:F0} m from her, {c.fromPath:F1} m from the path; {cands.Count} candidates)");
        var (pts, err) = GraphRouteTo(g, client.Self.SimPosition, c.pathPt);
        if (err != null) { MarkSeatFailed(c, "no route: " + err); return false; }
        if (HDist(c.pathPt, seatPos) > 2.2f)
        {
            var dir = new Vector3(c.pathPt.X - seatPos.X, c.pathPt.Y - seatPos.Y, 0); dir = Vector3.Normalize(dir);
            pts.Add(new Vector3(seatPos.X + dir.X * 1.2f, seatPos.Y + dir.Y * 1.2f, Math.Min(c.pathPt.Z, seatPos.Z)));
        }
        var poly = new Poly(pts);
        var o = new RouteOpts { Label = $"home wander to seat '{c.name}'", Idle = true };
        var bad = await CheckBounds(poly, o);
        if (bad != null) { MarkSeatFailed(c, bad); return false; }
        wanderPhase = $"walking to seat '{c.name}'";
        legCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bool ok; string msg;
        wLegGoal = seatPos; wLegHasGoal = true;
        try { (ok, msg) = await FollowPoly(poly, o, legCts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { client.Self.AutoPilotCancel(); WLog("walk to seat interrupted (" + (wanderPause ?? "cancel") + ")"); return false; }
        finally { wLegHasGoal = false; }
        if (!ok && msg.Contains("AO not active")) { WLog($"PAUSE (ao): {msg}; not sitting"); wanderPause = "ao"; wPausedAt = DateTime.Now; return false; }
        if (!ok && HDist(client.Self.SimPosition, seatPos) > 10f) { MarkSeatFailed(c, msg); return false; }
        if (!ok) WLog($"walk to the seat ended {HDist(client.Self.SimPosition, seatPos):F1} m short ({msg}); trying to sit from here");
        var sim = Sim;
        if (Sitters(sim).ContainsKey(c.p.LocalID)) { MarkSeatFailed(c, "someone sat there first"); return false; }
        if (wanderPause != null) return false;
        var sitReq = DateTime.Now;
        var r = await Exec("sit " + c.p.ID);
        await Task.Delay(1000, ct);
        if (client.Self.SittingOn == 0) { MarkSeatFailed(c, "sit failed: " + r); return false; }
        wLastSeat = c.p.ID; wSits++;
        var stay = wRnd.Next(120, 241);
        var t0 = DateTime.Now;
        wanderPhase = $"sitting on '{c.name}' ({stay} s)";
        WLog($"SAT on '{c.name}' {c.p.ID} ({r}); staying {stay} s");
        bool menu = false;
        try { var pr = await SeatPose(c.p, c.name, sitReq, false, ct); menu = pr.Contains(" chose "); } catch (OperationCanceledException) { throw; } catch (Exception ex) { WLog("POSE error: " + ex.GetBaseException().Message); }
        double changeAt = menu && stay >= 180 ? stay * (0.4 + wRnd.NextDouble() * 0.2) : double.MaxValue; bool changed = false;
        while ((DateTime.Now - t0).TotalSeconds < stay && wanderPause == null && client.Self.SittingOn != 0)
        {
            await Task.Delay(1000, ct);
            if (!changed && (DateTime.Now - t0).TotalSeconds >= changeAt && wanderPause == null && client.Self.SittingOn != 0)
            {
                changed = true;
                try { await SeatPose(c.p, c.name, DateTime.Now, true, ct); } catch (OperationCanceledException) { throw; } catch (Exception ex) { WLog("POSE change error: " + ex.GetBaseException().Message); }
            }
        }
        var sat = (DateTime.Now - t0).TotalSeconds;
        if (wanderPause != null) { wLastSit = $"{t0:HH:mm:ss} '{c.name}' {c.p.ID} {sat:F0} s (interrupted: {wanderPause})"; WLog($"sit on '{c.name}' interrupted after {sat:F0} s ({wanderPause})"); return true; }
        if (client.Self.SittingOn == 0) { wLastSit = $"{t0:HH:mm:ss} '{c.name}' {c.p.ID} {sat:F0} s (stood up by something else)"; WLog($"no longer seated after {sat:F0} s"); return true; }
        await EnsureStandingForWalk(ct);
        wLastSit = $"{t0:HH:mm:ss} '{c.name}' {c.p.ID} {sat:F0} s";
        WLog($"STOOD UP from '{c.name}' {c.p.ID} after {sat:F0} s; rejoining the path");
        return true;
    }

    // ---- main loop -----------------------------------------------------------------------------------
    static async Task WanderLoop(CancellationToken ct)
    {
        if (InPeronaut) { await HomeWanderLoop(ct); return; }
        var g = LoadGraph(HomeSeatRegion) ?? throw new InvalidOperationException("no path graph for Naberrie");
        string[] ends = { "deerpark", "landing" };
        var me0 = client.Self.SimPosition;
        wTarget = HDist(me0, g.N[g.Places["deerpark"].node]) > HDist(me0, g.N[g.Places["landing"].node]) ? "deerpark" : "landing";
        string afterSpur = null; int endsReached = 0;
        while (!ct.IsCancellationRequested)
        {
            if (!LoggedIn) { await Task.Delay(2000, ct); continue; }
            if (!InNaberrie) { WLog($"left Naberrie (now {client.Network.CurrentSim?.Name ?? "-"}): stopping"); SaveWanderFlag(false, "left Naberrie"); return; }
            if (RestartActive) { wanderResumeAfterRestart = true; WLog("region restart handling active: stopping (resume after the return)"); SaveWanderFlag(true, "restart"); return; }
            MaybeSnapshot();
            var pause = wanderPause;
            if (pause != null)
            {
                wanderPhase = "paused (" + pause + ")";
                if (pause == "greet")
                {
                    var gav = Avatars().FirstOrDefault(t => t.av.ID == wGreetWho && t.dist >= 0);
                    if (gav.av != null && client.Self.SittingOn == 0) client.Self.Movement.TurnToward(gav.pos);
                    if (DateTime.Now >= wGreetWaitUntil && wanderPause == "greet")
                    {
                        wanderPause = null;
                        WLog($"no reply from {wGreetWhoName} within 15 s: continuing the loop");
                        continue;
                    }
                    await Task.Delay(500, ct); continue;
                }
                if (pause == "chat")
                {
                    if (wApproachPending) { wApproachPending = false; await ApproachSpeaker(ct); }
                    var quiet = DateTime.Now - (wLastIncoming > wLastOwnChat ? wLastIncoming : wLastOwnChat);
                    if (quiet.TotalSeconds >= 180 && wanderPause == "chat")
                    {
                        wanderPause = null;
                        WLog($"RESUME: no chat for {quiet.TotalSeconds:F0} s (paused {(DateTime.Now - wPausedAt).TotalSeconds:F0} s for {wSpeakerName})");
                        continue;
                    }
                }
                await Task.Delay(1000, ct); continue;
            }
            // random sit
            if (wLegsUntilSit <= 0)
            {
                wanderPhase = "choosing a seat";
                var r = await RandomSit(g, ct);
                wLegsUntilSit = r ? wRnd.Next(1, 4) : 1;
                continue;
            }
            // next leg
            if (wTarget == "zendo" && QIn("zendo")) { WLog("QUIET: skipping the zendo-edge spur (zendo in session)"); wTarget = afterSpur ?? "deerpark"; afterSpur = null; continue; }
            var target = wTarget;
            wanderPhase = $"walking to {target}";
            var (pts, err) = GraphRoute(g, client.Self.SimPosition, g.Places[target].node);
            bool ok; string msg;
            var t0 = DateTime.Now;
            if (err != null) { ok = false; msg = "no route: " + err; }
            else
            {
                var poly = new Poly(pts);
                if (target == "zendo" && poly.Len > 5f)
                {   // stop 2 m short of the zendo edge node: the follower aims ~0.8 m past the end and must not step into the margin
                    var cut = poly.Len - 2f; var tp = new List<Vector3>();
                    for (int i = 0; i < poly.P.Count && poly.C[i] < cut; i++) tp.Add(poly.P[i]);
                    tp.Add(poly.At(cut)); poly = new Poly(tp);
                }
                var o = new RouteOpts { Label = $"wander leg to {target}", Place = target, Idle = true };
                bool shortTurn = false;
                if (target == "deerpark" && QIn("deerpark"))
                {   // Deer Park in session: stop >= 15 m short of it and turn around
                    var (cutp, cut, at) = QTruncate(poly, p => QBlocked(p, QIn, QSeatedIn, true) != null);
                    if (cut)
                    {
                        shortTurn = true;
                        if (cutp == null) WLog($"QUIET: Deer Park in session and she is already at/inside the 15 m keep-out ({HDist(client.Self.SimPosition, DeerParkCentre):F0} m from its centre): turning around");
                        else { WLog($"QUIET: Deer Park in session: leg cut from {poly.Len:F0} m to {at:F0} m, turning around at {P3(cutp.P[^1])} ({HDist(cutp.P[^1], DeerParkCentre):F0} m from its centre)"); poly = cutp; o.Label = "wander leg toward deerpark (short: Deer Park in session)"; o.Place = null; }
                    }
                    if (shortTurn && cutp == null) { poly = null; }
                }
                var b = poly == null ? null : await CheckBounds(poly, o);
                if (poly == null) { ok = true; msg = "turned around short of Deer Park (in session)"; }
                else if (b != null) { ok = false; msg = "refused: " + b; }
                else
                {
                    legCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    wLegGoal = poly.P[^1]; wLegHasGoal = true;
                    try { (ok, msg) = await FollowPoly(poly, o, legCts.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    { client.Self.AutoPilotCancel(); WLog($"leg to {target} interrupted ({wanderPause ?? "cancel"}) after {(DateTime.Now - t0).TotalSeconds:F0} s"); continue; }
                    catch (OperationCanceledException) { client.Self.AutoPilotCancel(); throw; }
                    finally { wLegHasGoal = false; }
                    if (ok && shortTurn) msg = "turned around short of Deer Park (in session): " + msg;
                }
            }
            if (ct.IsCancellationRequested) return;
            if (!InNaberrie) continue;
            if (wanderPause != null) continue; // a pause arrived as the leg ended
            if (ok)
            {
                wFails = 0; wLegs++;
                wLastLeg = $"{DateTime.Now:HH:mm:ss} {target}: {msg}";
                WLog($"LEG {wLegs} done -> {target} ({(DateTime.Now - t0).TotalSeconds:F0} s): {msg}");
                if (target == "zendo") { wTarget = afterSpur ?? "deerpark"; afterSpur = null; continue; } // spur doesn't count toward sits/loops
                endsReached++; if (endsReached % 2 == 1 && endsReached > 1) { wLoops++; WLog($"LOOP {wLoops} complete (end-to-end and back)"); }
                wLegsUntilSit--;
                var next = target == "deerpark" ? "landing" : "deerpark";
                if (target == "landing" && wRnd.NextDouble() < 0.25 && g.Places.ContainsKey("zendo"))
                {
                    if (QIn("zendo")) WLog("QUIET: zendo in session: not taking the zendo-edge spur");
                    else { afterSpur = next; next = "zendo"; WLog("taking the zendo-edge spur (stays outside the zendo footprint)"); }
                }
                wTarget = next;
            }
            else
            {
                if (msg.Contains("AO not active")) { WLog($"PAUSE (ao): {msg}; staying put until the AO is confirmed and 'wander resume'"); wanderPause = "ao"; wPausedAt = DateTime.Now; continue; }
                if (msg.Contains("she is seated")) { WLog("leg ended because she is seated (someone/something sat her): pausing (user)"); wanderPause = "user"; wPausedAt = DateTime.Now; continue; }
                wFails++;
                wLastLeg = $"{DateTime.Now:HH:mm:ss} {target} FAILED: {msg}";
                WLog($"LEG to {target} FAILED ({wFails}/3 in a row): {msg}");
                if (wFails >= 3) { await WanderRecover(g, "3 failed legs in a row"); return; }
                if (wFails == 2) { wTarget = target == "deerpark" ? "landing" : "deerpark"; WLog($"turning around: next target {wTarget}"); }
                await Task.Delay(5000, ct);
            }
        }
    }

    static async Task WanderRecover(Graph g, string why)
    {
        wanderPhase = "recovering to poolrock";
        WLog($"GIVING UP ({why}): walking to poolrock and sitting");
        SaveWanderFlag(false, "gave up: " + why);
        using var rc = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        string res;
        try
        {
            var (pts, err) = GraphRoute(g, client.Self.SimPosition, g.Places["poolrock"].node);
            if (err != null) res = "no route to poolrock: " + err;
            else res = await RunRoute(pts, new RouteOpts { Label = "wander recovery to poolrock", Place = "poolrock", SitAtEnd = true }, rc.Token);
        }
        catch (Exception ex) { client.Self.AutoPilotCancel(); res = "recovery walk failed: " + ex.GetBaseException().Message; }
        if (client.Self.SittingOn == 0) { try { res += "; sit_home: " + await SitHome(); } catch (Exception ex) { res += "; sit_home error " + ex.Message; } }
        WLog("stopped after recovery: " + res);
    }

    // 2026-09-27 (David): no map pictures. The periodic wander overhead snapshot is OFF unless GT_WANDER_SNAPSHOT=1
    // (the manual `overhead` / `snapshot` command still works). Off = no image, no python call, no log line.
    static readonly bool WanderSnapshot = Env("GT_WANDER_SNAPSHOT", "0") == "1";
    static void MaybeSnapshot()
    {
        if (!WanderSnapshot) return;
        if ((DateTime.Now - wLastSnap).TotalMinutes < 10) return;
        wLastSnap = DateTime.Now;
        _ = Task.Run(async () =>
        {
            try
            {
                var r = await Overhead("wander");
                WLog("snapshot: " + r.Split(' ')[0]);
                var dir = "/workspace/secondlife/images";
                var old = Directory.GetFiles(dir, "overhead-*-wander*.png").OrderByDescending(f => f, StringComparer.Ordinal).Skip(30).ToList();
                foreach (var f in old) { try { File.Delete(f); } catch { } }
            }
            catch (Exception ex) { WLog("snapshot failed: " + ex.Message); }
        });
    }

    // ---- short pauses: something to look at ------------------------------------------------------------
    static readonly Regex WFeatureRx = new(@"water|pool|pond|lotus|lily|flower|garden|tree|statue|buddha|waterfall|fountain|bamboo|lantern|bell|deer|koi|bonsai|stupa|pagoda|blossom|sakura|maple", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex WFeatureBad = new(@"sound|script|sign|invisible|phantom|walk this way|path|texture|particle|emitter", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static (Vector3? look, string what) IdleLook(Vector3 me, Vector3 dir)
    {
        var roll = wRnd.NextDouble();
        if (roll < 0.6)
        {
            var sim = client.Network.CurrentSim;
            var feats = sim?.ObjectsPrimitives.Values.Where(p => p != null && p.ParentID == 0 && p.Properties?.Name is string n && WFeatureRx.IsMatch(n) && !WFeatureBad.IsMatch(n)
                                 && HDist(p.Position, me) is >= 3f and <= 15f && Math.Abs(p.Position.Z - me.Z) < 6f).ToList();
            if (feats != null && feats.Count > 0) { var f = feats[wRnd.Next(feats.Count)]; return (f.Position, $"'{f.Properties.Name}' ({HDist(f.Position, me):F0} m)"); }
        }
        if (roll < 0.9)
        {
            float side = wRnd.Next(2) == 0 ? 1f : -1f;
            var v = new Vector3(me.X - dir.Y * side * 10f + dir.X * 3f, me.Y + dir.X * side * 10f + dir.Y * 3f, me.Z);
            return (v, $"the view to the {(side > 0 ? "left" : "right")}");
        }
        return (null, null);
    }

    // ---- approach a speaker --------------------------------------------------------------------------
    static async Task ApproachSpeaker(CancellationToken ct)
    {
        var sim = Sim; var id = wSpeaker;
        var av = sim.ObjectsAvatars.Values.FirstOrDefault(x => x != null && x.ID == id);
        if (av == null || (av.ParentID != 0 && !sim.ObjectsPrimitives.ContainsKey(av.ParentID))) { WLog($"approach: {wSpeakerName} not visible nearby: waiting here"); return; }
        var sp = PositionHelper.GetAvatarPosition(sim, av); var me = client.Self.SimPosition;
        float d = HDist(sp, me);
        var qa = QAreaOf(sp); // speaker inside an in-session area?
        if (qa != null && QIn(qa)) { WLog($"approach SUPPRESSED (quiet mode): {wSpeakerName} is inside the in-session {qa} area; waiting here, not walking over"); return; }
        if (Math.Abs(sp.Z - me.Z) > 4f) { WLog($"approach: {wSpeakerName} is on another level (dz {sp.Z - me.Z:F0} m): waiting here"); return; }
        if (client.Self.SittingOn != 0 && d <= 8f) { WLog($"approach: seated and {wSpeakerName} is {d:F1} m away: staying seated"); return; }
        if (d <= 3.5f) { if (client.Self.SittingOn == 0) client.Self.Movement.TurnToward(sp); WLog($"approach: {wSpeakerName} already {d:F1} m away: facing and waiting"); return; }
        var dir = new Vector3(me.X - sp.X, me.Y - sp.Y, 0); dir = dir.Length() < 0.01f ? Vector3.UnitX : Vector3.Normalize(dir);
        var o = new RouteOpts { Label = $"wander approach {wSpeakerName}" };
        Vector3 goal = Vector3.Zero;
        foreach (var rot in new[] { 0f, 0.6f, -0.6f, 1.2f, -1.2f })
        {
            var rd = new Vector3(dir.X * MathF.Cos(rot) - dir.Y * MathF.Sin(rot), dir.X * MathF.Sin(rot) + dir.Y * MathF.Cos(rot), 0);
            var cand = new Vector3(sp.X + rd.X * 2.5f, sp.Y + rd.Y * 2.5f, Math.Min(sp.Z, me.Z));
            if (await PointAllowed(cand, o)) { goal = cand; break; }
        }
        if (goal == Vector3.Zero) { WLog($"approach: no allowed spot near {wSpeakerName} (parcel/zendo): waiting here"); return; }
        var qb = QApproachBlock(sp, goal, QIn);
        if (qb != null) { WLog($"approach SUPPRESSED (quiet mode): {wSpeakerName}: {qb}; waiting here, not walking over"); return; }
        var poly = new Poly(new List<Vector3> { me, goal });
        var bad = await CheckBounds(poly, o);
        if (bad != null) { WLog($"approach: straight line not allowed ({bad}): waiting here"); return; }
        wanderPhase = "approaching " + wSpeakerName;
        WLog($"APPROACH {wSpeakerName}: {d:F1} m away, walking to {P3(goal)}");
        blockIgnore = id;
        legCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var (ok, msg) = await FollowPoly(poly, o, legCts.Token);
            WLog($"approach {(ok ? "done" : "ended")}: {msg}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { client.Self.AutoPilotCancel(); WLog("approach interrupted (new chat)"); }
        finally { blockIgnore = UUID.Zero; }
        av = sim.ObjectsAvatars.Values.FirstOrDefault(x => x != null && x.ID == id);
        if (av != null && client.Self.SittingOn == 0) { client.Self.Movement.TurnToward(PositionHelper.GetAvatarPosition(sim, av)); }
    }

    // ---- random sits ---------------------------------------------------------------------------------
    static readonly Regex WSeatRx = new(@"\b(pillow|cushion|zafu|zabuton|seat|mat|bench|chair|stool|pouf|lily ?pad|lilypad)s?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex WSeatBad = new(@"sign|vendor|giver|texture|door|rezz|board|notecard|donat|tip jar|kiosk|frog|with water", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static float ZendoDiamond(Vector3 p) => Math.Abs(p.X - 80.5f) + Math.Abs(p.Y - 142.0f);

    sealed record SeatCand(Primitive p, string name, float fromMe, float fromPath, float quiet, Vector3 pathPt);
    static async Task<List<SeatCand>> WanderSeats(Graph g, List<string> dbg = null)
    {
        var sim = Sim; var me = client.Self.SimPosition;
        var raw = new List<(Primitive p, Vector3 q, float d)>();
        foreach (var p in sim.ObjectsPrimitives.Values)
        {
            if (p == null || p.ParentID != 0 || p.PrimData.PCode != PCode.Prim) continue;
            if (HDist(p.Position, me) > 45f) continue;
            if (p.ID == DavidPillow) continue;
            var (q, d) = NearestOnGraph(g, p.Position);
            if (d > 12f || p.Position.Z > q.Z + 5f || p.Position.Z < q.Z - 4f) continue;
            raw.Add((p, q, d));
        }
        await EnsureProperties(sim, raw.Select(t => t.p).ToList());
        dbg?.Add($"  ({raw.Count} root prims within 45 m of her and 12 m of the path; {raw.Count(t => t.p.Properties == null)} without properties)");
        var sit = Sitters(sim);
        var avs = Avatars().Where(t => t.dist >= 0).ToList();
        bool rockHasStranger = sit.Any(kv =>
        {
            if (!sim.ObjectsPrimitives.TryGetValue(kv.Key, out var rp)) return false;
            return (rp.ID == HomePillow || rp.ID == DavidPillow) && kv.Value.Any(n => n != "ME" && !string.Equals(n, OwnerName, StringComparison.OrdinalIgnoreCase));
        });
        var now = DateTime.Now;
        var res = new List<SeatCand>();
        foreach (var (p, q, d) in raw)
        {
            var name = p.Properties?.Name ?? "";
            bool home = p.ID == HomePillow;
            if (!home && (!WSeatRx.IsMatch(name) || WSeatBad.IsMatch(name))) continue;
            void R(string why) => dbg?.Add($"  - '{name}' {p.ID} {P3(p.Position)}: {why}");
            if (home && rockHasStranger) { R("her pillow, but someone other than David is on the rock pillows"); continue; }
            if (Math.Max(p.Scale.X, Math.Max(p.Scale.Y, p.Scale.Z)) > 8f) { R("too big"); continue; }
            if (sit.ContainsKey(p.LocalID)) { R("occupied"); continue; }
            if (ZendoDiamond(p.Position) <= 20f) { R("zendo footprint + margin"); continue; } // zendo footprint + margin
            var qblk = QBlocked(p.Position, QIn, QSeatedIn, false);
            if (qblk != null) { R($"within 15 m of the in-session {qblk} (quiet mode)"); continue; }
            if (p.ID == wLastSeat) { R("sat there last time"); continue; }
            bool cool; lock (wSeatFailed) cool = wSeatFailed.TryGetValue(p.ID, out var ft) && (now - ft).TotalMinutes < 30;
            if (cool) { R("failed recently (30 min cooldown)"); continue; }
            var level = avs.Where(t => Math.Abs(t.pos.Z - p.Position.Z) < 10f).ToList();
            float quiet = level.Count == 0 ? 999f : level.Min(t => HDist(t.pos, p.Position));
            if (quiet < 3f) { R($"avatar {quiet:F1} m away"); continue; }
            res.Add(new SeatCand(p, name, HDist(p.Position, me), d, quiet, q));
        }
        // exact parcel check for the nearest few
        var outp = new List<SeatCand>();
        foreach (var c in res.OrderBy(c => c.fromMe).Take(24))
            if (IsBuddhaCenter(await ParcelAt(sim, c.p.Position))) outp.Add(c); else dbg?.Add($"  - '{c.name}' {c.p.ID} {P3(c.p.Position)}: outside The Buddha Center parcel");
        return outp;
    }

    static async Task<bool> RandomSit(Graph g, CancellationToken ct)
    {
        var cands = await WanderSeats(g);
        var quiet = cands.Where(c => c.quiet >= 10f).OrderBy(c => c.fromMe).Take(8).ToList();
        var pool = quiet.Count > 0 ? quiet : cands.OrderBy(c => c.fromMe).Take(5).ToList();
        if (pool.Count == 0) { WLog("sit: no free seat near here (skipping this time)"); return false; }
        var c = pool[wRnd.Next(pool.Count)];
        var seatPos = c.p.Position;
        WLog($"SIT target: '{c.name}' {c.p.ID} at {P3(seatPos)} ({c.fromMe:F0} m from her, {c.fromPath:F1} m from the path, nearest avatar {(c.quiet > 900 ? "none" : c.quiet.ToString("F0", IC) + " m")}; {cands.Count} candidates, {quiet.Count} quiet)");
        // route: graph to the path point nearest the seat, then a short approach
        var (pts, err) = GraphRouteTo(g, client.Self.SimPosition, c.pathPt);
        if (err != null) { MarkSeatFailed(c, "no route: " + err); return false; }
        if (HDist(c.pathPt, seatPos) > 2.2f)
        {
            var dir = new Vector3(c.pathPt.X - seatPos.X, c.pathPt.Y - seatPos.Y, 0); dir = Vector3.Normalize(dir);
            pts.Add(new Vector3(seatPos.X + dir.X * 1.2f, seatPos.Y + dir.Y * 1.2f, Math.Min(c.pathPt.Z, seatPos.Z)));
        }
        var poly = new Poly(pts);
        var o = new RouteOpts { Label = $"wander to seat '{c.name}'", Idle = true };
        var bad = await CheckBounds(poly, o);
        if (bad != null) { MarkSeatFailed(c, bad); return false; }
        wanderPhase = $"walking to seat '{c.name}'";
        legCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bool ok; string msg;
        wLegGoal = seatPos; wLegHasGoal = true;
        try { (ok, msg) = await FollowPoly(poly, o, legCts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { client.Self.AutoPilotCancel(); WLog("walk to seat interrupted (" + (wanderPause ?? "cancel") + ")"); return false; }
        finally { wLegHasGoal = false; }
        if (QBlocked(seatPos, QIn, QSeatedIn, false) is string qs) { WLog($"QUIET: not sitting on '{c.name}': a session started nearby ({qs})"); return false; }
        if (!ok && msg.Contains("AO not active")) { WLog($"PAUSE (ao): {msg}; not sitting, staying put until the AO is confirmed and 'wander resume'"); wanderPause = "ao"; wPausedAt = DateTime.Now; return false; }
        if (!ok && HDist(client.Self.SimPosition, seatPos) > 10f) { MarkSeatFailed(c, msg); return false; }
        if (!ok) WLog($"walk to the seat ended {HDist(client.Self.SimPosition, seatPos):F1} m short ({msg}); trying to sit from here");
        // still free?
        var sim = Sim;
        if (Sitters(sim).ContainsKey(c.p.LocalID)) { MarkSeatFailed(c, "someone sat there first"); return false; }
        if (wanderPause != null) return false;
        var sitReq = DateTime.Now;
        var r = await Exec("sit " + c.p.ID);
        await Task.Delay(1000, ct);
        if (client.Self.SittingOn == 0) { MarkSeatFailed(c, "sit failed: " + r); return false; }
        wLastSeat = c.p.ID; wSits++;
        var stay = wRnd.Next(120, 241); // 120-240 s (was 60-120 s; doubled 2026-09-26 on David's request)
        var t0 = DateTime.Now;
        wanderPhase = $"sitting on '{c.name}' ({stay} s)";
        WLog($"SAT on '{c.name}' {c.p.ID} ({r}); staying {stay} s");
        bool menu = false;
        try { var pr = await SeatPose(c.p, c.name, sitReq, false, ct); menu = pr.Contains(" chose "); } catch (OperationCanceledException) { throw; } catch (Exception ex) { WLog("POSE error: " + ex.GetBaseException().Message); }
        double changeAt = menu && stay >= 180 ? stay * (0.4 + wRnd.NextDouble() * 0.2) : double.MaxValue; bool changed = false;
        while ((DateTime.Now - t0).TotalSeconds < stay && wanderPause == null && client.Self.SittingOn != 0)
        {
            await Task.Delay(1000, ct);
            if (!changed && (DateTime.Now - t0).TotalSeconds >= changeAt && wanderPause == null && client.Self.SittingOn != 0)
            {
                changed = true;
                try { await SeatPose(c.p, c.name, DateTime.Now, true, ct); } catch (OperationCanceledException) { throw; } catch (Exception ex) { WLog("POSE change error: " + ex.GetBaseException().Message); }
            }
        }
        var sat = (DateTime.Now - t0).TotalSeconds;
        if (wanderPause != null) { wLastSit = $"{t0:HH:mm:ss} '{c.name}' {c.p.ID} {sat:F0} s (interrupted: {wanderPause})"; WLog($"sit on '{c.name}' interrupted after {sat:F0} s ({wanderPause})"); return true; }
        if (client.Self.SittingOn == 0) { wLastSit = $"{t0:HH:mm:ss} '{c.name}' {c.p.ID} {sat:F0} s (stood up by something else)"; WLog($"no longer seated after {sat:F0} s"); return true; }
        await EnsureStandingForWalk(ct);
        wLastSit = $"{t0:HH:mm:ss} '{c.name}' {c.p.ID} {sat:F0} s";
        WLog($"STOOD UP from '{c.name}' {c.p.ID} after {sat:F0} s; rejoining the path");
        return true;
    }
    static void MarkSeatFailed(SeatCand c, string why)
    {
        lock (wSeatFailed) wSeatFailed[c.p.ID] = DateTime.Now;
        WLog($"sit: skipping '{c.name}' {c.p.ID} for 30 min: {why}");
    }

    // ---- greetings -----------------------------------------------------------------------------------
    sealed record GreetCand(UUID id, string name, Vector3 pos, bool seated);
    // 2026-09-27 09:31 (David): short, gentle Buddhist greetings, always with the display name (GreetName/ShortName).
    // Picked at random, never the same line twice in a row. Lines mentioning the Deer Park are used only near the Deer Park.
    // 2026-09-27 09:50 (David): the Buddhist pool/personality is used ONLY while the VIOLETTE robe is actually worn
    // (RobeWorn(): her attached objects, not her location); without the robe the old ordinary pool below is used.
    static readonly string[] OrdinaryGreetTemplates =
    {
        "Hi {name}, welcome to the Buddha Center!",
        "Hello {name}! Lovely {tod} for a walk.",
        "Hi {name}! Peaceful {tod} here, isn't it?",
        "Hello there, {name}, welcome! Enjoy the gardens.",
        "Hi {name}, nice to see you here :)",
        "Hello {name}! Welcome to the Buddha Center, enjoy your visit.",
        "Hi {name}! Wishing you a calm and happy {tod}.",
        "Hey {name}, hello! Beautiful place for a stroll, isn't it?",
    };
    static readonly string[] GreetTemplates =
    {
        @"_/\_ Namaste, {name}",
        "Namaste, {name} \U0001F64F",
        @"_/\_ Peace be with you, {name}",
        @"May you be well and happy, {name} _/\_",
        "Hello, {name}, may your day be peaceful \U0001F64F",
        @"Welcome to the Buddha Center, {name} _/\_",
        "May you be at peace, {name} \U0001F64F",
        @"Be at ease here, {name} _/\_",
        @"Welcome to the Deer Park, {name} _/\_",
    };
    // natural short form of a display name: NFKC (fancy unicode letters -> plain), symbols stripped;
    // whole name if short and clean (<= 2 words, <= 16 chars), else the first real word; fallback: username first part
    static string ShortName(string display, string legacy)
    {
        static string Clean(string x)
        {
            if (string.IsNullOrWhiteSpace(x)) return "";
            try { x = x.Normalize(System.Text.NormalizationForm.FormKC); } catch { }
            var sb = new System.Text.StringBuilder();
            foreach (var r in x.EnumerateRunes())
            {
                int cp = r.Value; char? m = null;
                if (cp >= 0x1D400 && cp <= 0x1D6A3) { int i = (cp - 0x1D400) % 52; m = (char)(i < 26 ? 'A' + i : 'a' + i - 26); } // math bold/italic/script/fraktur...
                else if (cp >= 0x1D7CE && cp <= 0x1D7FF) m = (char)('0' + (cp - 0x1D7CE) % 10);
                else if (cp >= 0xFF21 && cp <= 0xFF3A) m = (char)('A' + cp - 0xFF21);
                else if (cp >= 0xFF41 && cp <= 0xFF5A) m = (char)('a' + cp - 0xFF41);
                else if (cp >= 0x24B6 && cp <= 0x24CF) m = (char)('A' + cp - 0x24B6);
                else if (cp >= 0x24D0 && cp <= 0x24E9) m = (char)('a' + cp - 0x24D0);
                else if (cp >= 0x1F130 && cp <= 0x1F189) m = (char)('A' + (cp - 0x1F130) % 32 % 26);
                if (m != null) { sb.Append(m.Value); continue; }
                sb.Append(System.Text.Rune.IsLetterOrDigit(r) || cp == '\'' || cp == '-' ? r.ToString() : " ");
            }
            return Regex.Replace(sb.ToString(), @"\s+", " ").Trim(' ', '-', '\'');
        }
        string Fallback()
        {
            var l = (legacy ?? "").Trim(); var first = l.Split(' ', '.')[0];
            first = Clean(first);
            return first.Length == 0 ? "friend" : char.ToUpperInvariant(first[0]) + first[1..];
        }
        var raw = (display ?? "").Trim();
        if (raw.Length == 0) return Fallback();
        var c = Clean(raw);
        if (c.EndsWith(" Resident", StringComparison.OrdinalIgnoreCase)) c = c[..^9].Trim();
        var words = c.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Count(char.IsLetter) >= 2 && !Regex.IsMatch(w, "^[xX]{2,}$")).ToList();
        if (words.Count == 0) return Fallback();
        bool decorated = c != Regex.Replace(raw, @"\s+", " ");
        var whole = string.Join(' ', words);
        var pick = (!decorated && words.Count <= 2 && whole.Length <= 16) ? whole : words[0];
        if (pick.Length > 16) pick = pick[..16];
        return pick;
    }
    static readonly Dictionary<UUID, (string name, DateTime at)> dispCache = new();
    static async Task<string> GreetName(UUID id, string legacy)
    {
        lock (dispCache) if (dispCache.TryGetValue(id, out var c) && (DateTime.Now - c.at).TotalMinutes < 60) return c.name;
        string disp = null;
        try
        {
            var t = client.Avatars.GetDisplayNamesAsync(new List<UUID> { id });
            if (await Task.WhenAny(t, Task.Delay(3000)) == t) { var (ok, names, _) = await t; if (ok) disp = names?.FirstOrDefault(n => n.ID == id)?.DisplayName; }
        }
        catch { }
        var sn = ShortName(disp, legacy);
        if (disp != null) lock (dispCache) dispCache[id] = (sn, DateTime.Now);
        return sn;
    }
    static string TimeOfDay(DateTime t) => t.Hour switch { >= 5 and < 12 => "morning", >= 12 and < 17 => "afternoon", >= 17 and < 22 => "evening", _ => "night" };
    static bool GreetDeerParkOnly(string t) => t.Contains("Deer Park", StringComparison.Ordinal);
    // idx = last line used (encoded: Buddhist i, ordinary 100+i) so a pool switch never blocks a line wrongly
    static string NextGreeting(DateTime now, string name, ref int idx, bool atDeerPark = false, bool robe = true)
    {
        var t = robe ? GreetTemplates : OrdinaryGreetTemplates; int off = robe ? 0 : 100;
        int last = idx - off;
        var pool = Enumerable.Range(0, t.Length).Where(i => i != last && (atDeerPark || !GreetDeerParkOnly(t[i]))).ToList();
        int pick = pool[wRnd.Next(pool.Count)]; idx = pick + off;
        return t[pick].Replace("{tod}", TimeOfDay(now)).Replace("{name}", name);
    }
    // ---- robe (2026-09-27 09:50, David) -----------------------------------------------------------------
    static readonly UUID RobeItem = new("ecdfcb6b-6064-39ba-a57a-225cf131470d"); // 'VIOLETTE - Bhikkhuni Monk - Legacy' (RightHand)
    // actual worn state: the robe's attached object (AttachItemID) among her attachments, from the object library or the raw attachment table
    static bool RobeWorn(out string how)
    {
        try
        {
            if (WornPrims().Any(p => AttachItemId(p) == RobeItem)) { how = "robe object attached (object library)"; return true; }
            if (TrackedAttachments().Any(r => r.Item == RobeItem)) { how = "robe object attached (raw attachment table)"; return true; }
            how = "robe not among her attached objects"; return false;
        }
        catch (Exception ex) { how = "robe check failed: " + ex.GetBaseException().Message; return false; }
    }
    static bool RobeWorn() => RobeWorn(out _);
    // for the teleport guard: fail safe - if no attached objects are known at all yet (just after login/teleport), assume the robe is on
    static bool RobeMaybeWorn(out string how)
    {
        if (RobeWorn(out how)) return true;
        try { if (WornPrims().Count == 0 && TrackedAttachments().Count == 0) { how = "attachments not received yet: assuming the robe is worn"; return true; } } catch { }
        return false;
    }
    // pure rule (unit-tested): the robe is worn only at the Buddha Center (all BC parcels, landing zone, sky platform are in Naberrie).
    // Returns null = allowed, else the refusal reason.
    static string RobeTpBlock(string destRegion, bool robeWorn, bool force)
    {
        if (!robeWorn || force) return null;
        if (string.Equals(destRegion?.Trim(), HomeSeatRegion, StringComparison.OrdinalIgnoreCase)) return null;
        return $"refused: the VIOLETTE robe is worn and the robe stays at the Buddha Center; '{destRegion ?? "unknown destination"}' is outside it. " +
               "Change to the 'Original' outfit first (no automatic outfit switching yet), or add 'force'.";
    }
    // pure decision (unit-testable): who to greet now, or null with the reason
    static (GreetCand who, string why) PickGreet(List<GreetCand> avs, Vector3 me, DateTime now, Dictionary<UUID, DateTime> greeted, DateTime lastAny, Func<UUID, bool> muted, UUID self, string quietWhy = null, double repeatHours = double.NaN)
    {
        if (double.IsNaN(repeatHours)) repeatHours = GreetRepeatHours;
        if (quietWhy != null) return (null, "quiet mode: " + quietWhy);
        if ((now - lastAny).TotalSeconds < 20) return (null, "global 20 s gap");
        if (ZendoDiamond(me) <= 23f) return (null, "she is near the zendo (quiet zone)");
        foreach (var a in avs.OrderBy(a => HDist(a.pos, me)))
        {
            if (a.id == self || a.id == UUID.Zero) continue;
            if (a.id == AlexNova || muted(a.id)) continue;
            float hd = HDist(a.pos, me), dz = Math.Abs(a.pos.Z - me.Z);
            if (hd > 10f || dz >= 10f) continue;
            if (a.seated && hd > 6f) continue;
            if (InZendo(a.pos)) continue;
            if (GreetedRecently(greeted, a.id, now, repeatHours)) continue;
            return (a, "ok");
        }
        return (null, "nobody to greet");
    }
    static async Task GreetLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
                try
                {
                    if (!LoggedIn || !InWanderRegion || wanderPause != null || !(wanderPhase.StartsWith("walking"))) continue;
                    if (client.Self.SittingOn != 0) continue;
                    var sim = Sim;
                    var avs = Avatars().Where(t => t.dist >= 0).Select(t => new GreetCand(t.av.ID, t.av.Name, t.pos, t.av.ParentID != 0)).ToList();
                    GreetCand who; string why;
                    lock (wGreeted) (who, why) = PickGreet(avs, client.Self.SimPosition, DateTime.Now, wGreeted, wLastGreet, IsMuted, client.Self.AgentID);
                    if (who == null) continue;
                    if (QuietOn) { QLogSuppressedGreet(who); continue; } // Buddha Center rule: no nearby chat during sessions

                    var rg = RateGuard();
                    if (rg != null) { WLog("greeting skipped: " + rg); wLastGreet = DateTime.Now; continue; }
                    var gname = await GreetName(who.id, who.name);
                    if (wanderPause != null || !wanderPhase.StartsWith("walking") || QuietOn) continue; // changed while looking up the name
                    bool robeOn = RobeWorn(out var robeHow);
                    var txt = NextGreeting(DateTime.Now, gname, ref wGreetIdx, HDist(client.Self.SimPosition, DeerParkCentre) <= 25f, robeOn);
                    WLog($"greeting pool: {(robeOn ? "Buddhist" : "ordinary")} ({robeHow})");
                    HeadTurnTo(who.id, "wander greeting");   // a short head turn to whom she greets (LookAt.cs)
                    client.Self.Chat(txt, 0, ChatType.Normal);
                    var now = DateTime.Now;
                    NoteGreeted(who.id, now);
                    wLastGreet = now; wGreets++; wLastGreetTxt = $"{now:HH:mm:ss} {who.name}: \"{txt}\"";
                    Log("me-chat", txt + " (wander greeting)");
                    WLog($"GREETED {who.name} ({who.id}) at {HDist(who.pos, client.Self.SimPosition):F1} m: \"{txt}\"");
                    if (wanderPause == null)
                    {
                        wGreetWho = who.id; wGreetWhoName = who.name; wGreetWaitUntil = DateTime.Now.AddSeconds(15);
                        SetPause("greet", $"stopped and facing {who.name}; waiting 15 s for a reply");
                        client.Self.Movement.TurnToward(who.pos);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { WLog("greet error: " + ex.Message); await Task.Delay(5000, ct); }
            }
        }
        catch (OperationCanceledException) { }
    }

    static string GreetSelfTest()
    {
        var me = new Vector3(120, 120, 22); var now = new DateTime(2026, 9, 25, 19, 30, 0);
        var self = UUID.Random(); var a = UUID.Random(); var b = UUID.Random(); var m = UUID.Random();
        var greeted = new Dictionary<UUID, DateTime>();
        var lines = new List<string>(); int pass = 0, fail = 0;
        void T(string name, List<GreetCand> avs, DateTime lastAny, UUID? expect, Vector3? meAt = null, Func<UUID, bool> mut = null, double rep = 0)
        {
            var (w, why) = PickGreet(avs, meAt ?? me, now, greeted, lastAny, mut ?? (x => x == m), self, null, rep);
            bool ok = expect == null ? w == null : w?.id == expect;
            if (ok) pass++; else fail++;
            lines.Add($"{(ok ? "PASS" : "FAIL")} {name}: {(w == null ? "none (" + why + ")" : w.name)}");
        }
        var old = now.AddMinutes(-5);
        T("walker 6 m away", new() { new(a, "Anna Walker", new Vector3(126, 120, 22), false) }, old, a);
        T("11 m away (too far)", new() { new(a, "Anna Walker", new Vector3(131, 120, 22), false) }, old, null);
        T("skybox 300 m up", new() { new(a, "Anna Walker", new Vector3(122, 120, 322), false) }, old, null);
        T("dz 9 m (same ground level-ish)", new() { new(a, "Anna Walker", new Vector3(122, 120, 31), false) }, old, a);
        T("David greeted like everyone (09:29 rule)", new() { new(b, "David Nightingale", new Vector3(122, 120, 22), false) }, old, b);
        T("Alex Nova (muted id) never", new() { new(AlexNova, "Alex Nova", new Vector3(122, 120, 22), false) }, old, null);
        T("mute-list avatar never", new() { new(m, "Muted Person", new Vector3(122, 120, 22), false) }, old, null);
        T("global 20 s gap", new() { new(a, "Anna Walker", new Vector3(122, 120, 22), false) }, now.AddSeconds(-10), null);
        T("seated 8 m away skipped", new() { new(a, "Anna Walker", new Vector3(128, 120, 22), true) }, old, null);
        T("seated 5 m away greeted", new() { new(a, "Anna Walker", new Vector3(125, 120, 22), true) }, old, a);
        T("avatar inside the zendo skipped", new() { new(a, "Anna Walker", new Vector3(96, 142, 22), false) }, old, null, new Vector3(104, 146, 22));
        T("she is near the zendo: quiet", new() { new(a, "Anna Walker", new Vector3(100, 150, 22), false) }, old, null, new Vector3(99, 146, 22));
        greeted[a] = now.AddMinutes(-30);
        T("greeted 30 min ago: skip", new() { new(a, "Anna Walker", new Vector3(122, 120, 22), false) }, old, null);
        T("... but the other one is greeted", new() { new(a, "Anna Walker", new Vector3(122, 120, 22), false), new(b, "Ben Stroll", new Vector3(125, 121, 22), false) }, old, b);
        greeted[a] = now.AddMinutes(-61);
        T("once rule: greeted 61 min ago: NOT again", new() { new(a, "Anna Walker", new Vector3(122, 120, 22), false) }, old, null);
        greeted[a] = now.AddHours(-30);
        T("once rule: greeted 30 h ago: NOT again", new() { new(a, "Anna Walker", new Vector3(122, 120, 22), false) }, old, null);
        greeted[a] = now.AddMinutes(-61);
        T("repeat window 1 h (GT_GREET_REPEAT_HOURS=1): greeted 61 min ago: again", new() { new(a, "Anna Walker", new Vector3(122, 120, 22), false) }, old, a, null, null, 1);
        greeted[a] = now.AddHours(-23);
        T("24 h rule: greeted 23 h ago: NOT again", new() { new(a, "Anna Walker", new Vector3(122, 120, 22), false) }, old, null, null, null, 24);
        greeted[a] = now.AddHours(-25);
        T("24 h rule: greeted 25 h ago: again", new() { new(a, "Anna Walker", new Vector3(122, 120, 22), false) }, old, a, null, null, 24);
        bool keepAll = double.IsPositiveInfinity(GreetKeepHours); if (keepAll) pass++; else fail++;
        lines.Add($"{(keepAll ? "PASS" : "FAIL")} greet history is permanent (keep hours = {GreetKeepHours}); live rule: {(GreetRepeatHours <= 0 ? "once ever" : $"once per {GreetRepeatHours:0.#} h")}");
        // persistence across a daemon restart: save, wipe memory, reload -> still inside the 60 min window
        {
            var h = new Dictionary<UUID, DateTime> { [a] = now.AddMinutes(-5), [b] = now.AddMinutes(-61), [m] = now.AddHours(-30) };
            var js24 = SerializeGreetHistory(h, now, 24);
            var back24 = ParseGreetHistory(js24, now, 24);
            bool rt = back24.Count == 2 && back24.TryGetValue(a, out var ta) && Math.Abs((ta - now.AddMinutes(-5)).TotalSeconds) < 1 && back24.ContainsKey(b) && !back24.ContainsKey(m);
            if (rt) pass++; else fail++;
            lines.Add($"{(rt ? "PASS" : "FAIL")} greet history round trip with a 24 h keep (repeat mode): {back24.Count} entries, >24 h dropped");
            var js = SerializeGreetHistory(h, now, double.PositiveInfinity);
            var back = ParseGreetHistory(js, now, double.PositiveInfinity);
            bool rtAll = back.Count == 3 && back.ContainsKey(m); if (rtAll) pass++; else fail++;
            lines.Add($"{(rtAll ? "PASS" : "FAIL")} once mode keeps every entry (incl. 30 h old): {back.Count} entries");
            var (w1, why1) = PickGreet(new() { new(a, "Anna Walker", new Vector3(122, 120, 22), false) }, me, now, back, old, x => false, self, null, 0);
            bool r1 = w1 == null; if (r1) pass++; else fail++;
            lines.Add($"{(r1 ? "PASS" : "FAIL")} after a restart (history reloaded) Anna greeted 5 min ago is NOT greeted again: {(w1 == null ? why1 : w1.name)}");
            var (w2, _) = PickGreet(new() { new(b, "Ben Stroll", new Vector3(122, 120, 22), false), new(m, "Mo Old", new Vector3(123, 120, 22), false) }, me, now, back, old, x => false, self, null, 0);
            bool r2 = w2 == null; if (r2) pass++; else fail++;
            lines.Add($"{(r2 ? "PASS" : "FAIL")} after a restart Ben (61 min) and Mo (30 h) are NOT greeted again: {(w2 == null ? "none" : w2.name)}");
            // display-name keys must be ignored (history is UUID-only)
            var mixedJson = "{\"" + a + "\":\"" + now.AddMinutes(-2).ToString("o") + "\",\"RyanSinclair65\":\"" + now.AddMinutes(-1).ToString("o") + "\"}";
            var onlyUuid = ParseGreetHistory(mixedJson, now, double.PositiveInfinity);
            bool nameIgnored = onlyUuid.ContainsKey(a) && onlyUuid.Count == 1 && !onlyUuid.Keys.Any(k => k == UUID.Zero);
            lines.Add($"{(nameIgnored ? "PASS" : "FAIL")} greet history ignores display-name keys (UUID-only): got {onlyUuid.Count} entr{(onlyUuid.Count == 1 ? "y" : "ies")}");
            if (nameIgnored) pass++; else fail++;

            var tmpf = Path.Combine(Path.GetTempPath(), $"greet-history-selftest-{Guid.NewGuid():N}.json");
            try
            {
                File.WriteAllText(tmpf, SerializeGreetHistory(new() { [a] = DateTime.Now.AddMinutes(-3) }, DateTime.Now));
                var fromFile = ParseGreetHistory(File.ReadAllText(tmpf), DateTime.Now);
                File.WriteAllText(tmpf, "{not json");
                var bad = ParseGreetHistory(File.ReadAllText(tmpf), DateTime.Now);
                bool r3 = fromFile.ContainsKey(a) && bad.Count == 0; if (r3) pass++; else fail++;
                lines.Add($"{(r3 ? "PASS" : "FAIL")} greet history file write/read OK; corrupt file -> empty history, no crash");
            }
            finally { try { File.Delete(tmpf); } catch { } }
        }
        int idx = -1; var seen = new List<string>(); var seenDp = new List<string>();
        for (int i = 0; i < 200; i++) seen.Add(NextGreeting(now, "Maya", ref idx));
        int idx2 = -1; for (int i = 0; i < 200; i++) seenDp.Add(NextGreeting(now, "Maya", ref idx2, true));
        bool named = seen.All(x => x.Contains("Maya") && !x.Contains("{"));
        if (named) pass++; else fail++;
        lines.Add($"{(named ? "PASS" : "FAIL")} every variant contains the name");
        foreach (var (d, l, want) in new (string, string, string)[] {
            ("Maya", "maya.brightstar", "Maya"), ("Maya Brightstar", "mayab Resident", "Maya Brightstar"),
            ("\u2728 \U0001D4DC\U0001D4EA\U0001D502\U0001D4EA \u2728", "x Resident", "Maya"), ("~*~ Luna ~*~ Moonchild of the Stars", "luna Resident", "Luna"),
            ("Bartholomew-Maximilian Featherstonehaugh", "bart Resident", "Bartholomew-Maxi"), (null, "BodhiCheetah Resident", "BodhiCheetah"),
            ("\u2605\u2605\u2605", "sunny.day", "Sunny"), ("xX Kira Xx", "kira Resident", "Kira"), ("Anna Resident", "anna Resident", "Anna") })
        {
            var got = ShortName(d, l); bool ok = got == want;
            if (ok) pass++; else fail++;
            lines.Add($"{(ok ? "PASS" : "FAIL")} name '{d ?? "(not loaded)"}' / '{l}' -> '{got}' (want '{want}')");
        }
        bool noRepeat = seen.Zip(seen.Skip(1)).All(p => p.First != p.Second) && seenDp.Zip(seenDp.Skip(1)).All(p => p.First != p.Second);
        int general = GreetTemplates.Count(t => !GreetDeerParkOnly(t));
        bool allUsed = seen.Distinct().Count() == general && seenDp.Distinct().Count() == GreetTemplates.Length;
        bool dpOnly = !seen.Any(GreetDeerParkOnly) && seenDp.Any(GreetDeerParkOnly);
        bool shortOk = seenDp.All(x => x.Length <= 60) && seenDp.All(x => x.Contains("Maya") && !x.Contains("{"));
        if (noRepeat && allUsed) pass++; else fail++;
        lines.Add($"{(noRepeat && allUsed ? "PASS" : "FAIL")} random pool: {seen.Distinct().Count()}/{general} general lines used away from the Deer Park, {seenDp.Distinct().Count()}/{GreetTemplates.Length} at the Deer Park (200 draws each), never the same line twice in a row={noRepeat}");
        if (dpOnly) pass++; else fail++;
        lines.Add($"{(dpOnly ? "PASS" : "FAIL")} 'Deer Park' line only near the Deer Park");
        if (shortOk) pass++; else fail++;
        // short greeting reply classifier
        {
            var g = now.AddSeconds(-19);
            foreach (var (msg, at, conv, want) in new (string, DateTime?, bool, bool)[] {
                ("Thank you!", g, false, true), ("thanks", g, false, true), ("ty", g, false, true), ("thx :)", g, false, true),
                ("Hi!", g, false, true), ("hello Galatea", g, false, true), ("hey :)", g, false, true), ("hiya", g, false, true),
                ("namaste _/\\_", g, false, true), ("_/\\_", g, false, true), ("\U0001F64F", g, false, true), ("\U0001F64F\U0001F64F", g, false, true),
                (":)", g, false, true), ("/me bows", g, false, true), ("smiles", g, false, true), ("You too!", g, false, true),
                ("same to you", g, false, true), ("and you \U0001F64F", g, false, true), ("thank you so much", g, false, true),
                ("thaaanks!!", g, false, true), ("and you", g, false, true), ("you as well :)", g, false, true), ("you", g, false, false), ("too", g, false, false), ("Thank you, you too _/\\_", g, false, true),
                ("How are you?", g, false, false), ("thanks, how are you", g, false, false), ("Thank you! Is there a meditation today?", g, false, false),
                ("hi, where is the temple", g, false, false), ("Galatea can you help me", g, false, false),
                ("Thank you, I really love this place and its gardens", g, false, false), ("thanks thanks thanks thanks thanks thanks", g, false, false),
                ("Thank you!", null, false, false), ("Thank you!", now.AddMinutes(-5), false, false), ("Thank you!", g, true, false),
                ("go away", g, false, false), ("thanks idiot", g, false, false), ("hi 123", g, false, false), ("", g, false, false), ("so much", g, false, false) })
            {
                var (ack, why) = ShortGreetReply(msg, at, now, conv);
                bool ok = ack == want; if (ok) pass++; else fail++;
                lines.Add($"{(ok ? "PASS" : "FAIL")} short-reply: '{msg}' greeted={(at == null ? "never" : $"{(now - at.Value).TotalSeconds:F0}s ago")}{(conv ? " in-conversation" : "")} -> {(ack ? "IGNORED (short greeting reply)" : "conversation")} ({why})");
            }
        }
        lines.Add($"{(shortOk ? "PASS" : "FAIL")} every line short (<= 60 chars) and has the name");
        lines.Add("pool: " + string.Join(" | ", GreetTemplates.Select(t => t.Replace("{name}", "Maya"))));
        // robe-gated pools
        int oi = -1; var seenO = new List<string>(); for (int i = 0; i < 200; i++) seenO.Add(NextGreeting(now, "Maya", ref oi, true, robe: false));
        var ordSet = OrdinaryGreetTemplates.Select(t => t.Replace("{tod}", TimeOfDay(now)).Replace("{name}", "Maya")).ToHashSet();
        bool ordOk = seenO.All(ordSet.Contains) && seenO.Distinct().Count() == OrdinaryGreetTemplates.Length && seenO.Zip(seenO.Skip(1)).All(p => p.First != p.Second);
        if (ordOk) pass++; else fail++;
        lines.Add($"{(ordOk ? "PASS" : "FAIL")} no robe -> ordinary pool only ({seenO.Distinct().Count()}/{OrdinaryGreetTemplates.Length} used, no Buddhist line, never twice in a row)");
        int mi = -1; var mixed = new List<string>(); for (int i = 0; i < 100; i++) mixed.Add(NextGreeting(now, "Maya", ref mi, false, robe: i % 7 < 4));
        bool mixOk = mixed.Zip(mixed.Skip(1)).All(p => p.First != p.Second);
        if (mixOk) pass++; else fail++;
        lines.Add($"{(mixOk ? "PASS" : "FAIL")} robe on/off switching: never the same line twice in a row");
        foreach (var (reg, robe, force, allow) in new (string, bool, bool, bool)[] { ("Naberrie", true, false, true), ("naberrie", true, false, true), ("Firestorm Orientation", true, false, false), ("Firestorm Orientation", true, true, true), ("Firestorm Orientation", false, false, true), (null, true, false, false) })
        {
            bool ok = (RobeTpBlock(reg, robe, force) == null) == allow; if (ok) pass++; else fail++;
            lines.Add($"{(ok ? "PASS" : "FAIL")} robe teleport guard: to '{reg ?? "(unknown, e.g. lure)"}' robe={robe} force={force} -> {(allow ? "allowed" : "refused")}");
        }
        lines.Add("ordinary pool: " + string.Join(" | ", OrdinaryGreetTemplates.Select(t => t.Replace("{name}", "Maya"))));
        return $"greeting selftest: {pass} pass, {fail} fail\n" + string.Join("\n", lines);
    }

    static string WanderStatus()
    {
        if (!WanderOn) return $"wander off; last leg {wLastLeg}; last sit {wLastSit}; {QuietShort()} [quiet={QuietFlag()}]";
        var quiet = DateTime.Now - (wLastIncoming > wLastOwnChat ? wLastIncoming : wLastOwnChat);
        return $"wander ON since {wStarted:HH:mm:ss}; phase: {wanderPhase}{(wanderPause != null ? $"; PAUSED ({wanderPause}{(wanderPause == "chat" ? $", speaker {wSpeakerName}, quiet {quiet.TotalSeconds:F0}/180 s" : "")})" : "")}; " +
               $"target {wTarget}; legs {wLegs}, loops {wLoops}, sits {wSits}, greetings {wGreets}, fails in a row {wFails}; next sit in {Math.Max(0, wLegsUntilSit)} leg(s); " +
               $"last leg {wLastLeg}; last sit {wLastSit}; last pose {wLastPose}; last greeting {wLastGreetTxt}; route: {routeState}; {QuietShort()} [quiet={QuietFlag()}]; ao={AoFlag()} (last guard event {aoLastEvent})";
    }

    static async Task<string> WanderCmds(string[] a)
    {
        var sub = a.Length > 0 ? a[0].ToLowerInvariant() : "status";
        switch (sub)
        {
            case "start": return StartWander("command");
            case "stop": return StopWander("command");
            case "pause": case "hold":
                if (!WanderOn) return "wander is not running";
                SetPause(sub == "hold" ? "hold" : "user", $"'wander {sub}' command (stays paused until 'wander resume')");
                return $"wander paused ({sub}); 'wander resume' to continue";
            case "resume":
                if (!WanderOn) return "wander is not running ('wander start')";
                if (wanderPause == null) return "wander is not paused";
                if (client.Self.SittingOn == 0 && !AoStateNow().active) { AoLog("wander resume REFUSED: " + AoStateNow().why); return "refused: AO not active (" + AoStateNow().why + "); staying paused"; }
                WLog($"RESUME by command (was {wanderPause})"); wanderPause = null;
                return "wander resumed";
            case "status": return WanderStatus();
            case "selftest": return GreetSelfTest() + "\n" + QuietSelfTest() + "\n" + PoseSelfTest() + "\n" + AoSelfTest() + "\n" + WatchdogSelfTest() + "\n" + ResumeSelfTest();
            case "resumetest": return ResumeSelfTest();
            case "seats":
            {
                if (InPeronaut)
                {
                    var g = LoadGraph(HomeWanderRegion); if (g == null) return "no Peronaut path graph";
                    var dbg = new List<string>();
                    var c = await HomeWanderSeats(g, dbg);
                    return $"{dbg.Count} rejected seat-named objects:\n{string.Join("\n", dbg)}\n{c.Count} candidate upper-level home seats:\n" + string.Join("\n", c.OrderBy(x => x.fromMe).Select(x => $"  '{x.name}' {x.p.ID} {P3(x.p.Position)} me {x.fromMe:F0} m, path {x.fromPath:F1} m, nearest avatar {(x.quiet > 900 ? "-" : x.quiet.ToString("F0", IC))} m"));
                }
                var g2 = LoadGraph(HomeSeatRegion); if (g2 == null || !InNaberrie) return "not in Naberrie or Peronaut";
                var dbg2 = new List<string>();
                var c2 = await WanderSeats(g2, dbg2);
                return $"{dbg2.Count} rejected seat-named objects:\n{string.Join("\n", dbg2)}\n{c2.Count} candidate seats within 45 m:\n" + string.Join("\n", c2.OrderBy(x => x.fromMe).Select(x => $"  '{x.name}' {x.p.ID} {P3(x.p.Position)} me {x.fromMe:F0} m, path {x.fromPath:F1} m, nearest avatar {(x.quiet > 900 ? "-" : x.quiet.ToString("F0", IC))} m"));
            }
            default: return "usage: wander start|stop|pause|hold|resume|status|seats|selftest|resumetest";
        }
    }
}
