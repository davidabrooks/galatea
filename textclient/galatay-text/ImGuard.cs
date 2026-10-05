// ImGuard.cs (2026-10-03, David): per-recipient duplicate-IM guard for the 'im' command.
// Two chat-routine runs raced and sent near-identical IMs to the same person ~1 s apart (12:14 and 12:42 PT today).
// Under a per-recipient lock, an 'im' is REFUSED when
//   (a) I already sent that avatar an IM within the last GT_IM_GUARD_WINDOW_S (default 5 s), or
//   (b) my last IM to them is newer than their latest incoming IM (already answered) and was sent within the last
//       GT_IM_GUARD_FRESH_H hours (default 6; older = a fresh conversation, which may start again).
// David Nightingale is only subject to (a). 'im --force <to> <text>' skips both (manual / David-directed sends only).
// 'im --headsup <to> <text>' (2026-10-03): a short "give me a bit, I need to check with David" note that skips (b), but
// only ONCE per recipient per their latest incoming IM (a second heads-up is refused until they write again); it still
// obeys (a) and the lock. Logged '[im-guard] headsup: sent to <name> (<uuid>)'; seeded from those log lines after a restart.
// A refusal is a normal skip, not an error: the reply starts with "skipped: " (text-galatay.sh cmd exits 10, the MCP
// tool answers ok=true, skipped=true). Times: in-memory, millisecond precision; after a restart seeded from the
// [me-im] / [im] log lines (Program.cs SeedMyIms, second precision).
// 'imguard selftest' (synthetic ids, nothing sent) | 'imguard check <name|uuid>' (dry run: would 'im' send now?)
// (2026-10-04 21:15, David: Ryan got two 'yummy' replies from two overlapping runs; earlier the time-only "already answered"
// rule also refused a real answer, so runs fell back to --force.) Every incoming IM now gets a message id (msg_id in the
// webhook event and in 'imlog'). 'im --re <id[,id..]> <to> <text>' claims exactly those messages: under the recipient's lock
// it is refused if ANY of them was already answered (the reply names the answer and the still-open ids), else it sends and
// marks them answered; it skips the time-only rules, so answering a message that came in while another run was replying
// works. --force never overrides an --re conflict. A plain 'im' keeps the old rules and marks everything received so far.
// 'imlog <name> [n]' (also 'im history|log <name> [n]') = read-only IM history with ids. 'im' refuses a bare one-word
// recipient that is an im subcommand word or not already known locally (no directory lookup for single words): at 21:08 PT
// 'im history ThomasNejutto' IMed 'ThomasNejutto' to a stranger, History Resident.
using System.Collections.Concurrent;
using System.Globalization;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    static readonly TimeSpan ImGuardWindow = TimeSpan.FromSeconds(double.TryParse(Env("GT_IM_GUARD_WINDOW_S", "5"), NumberStyles.Float, CultureInfo.InvariantCulture, out var igw) && igw >= 0 ? igw : 5);
    static readonly TimeSpan ImGuardFresh = TimeSpan.FromHours(double.TryParse(Env("GT_IM_GUARD_FRESH_H", "6"), NumberStyles.Float, CultureInfo.InvariantCulture, out var igf) && igf > 0 ? igf : 6);
    static readonly ConcurrentDictionary<string, object> imGuardLocks = new(StringComparer.OrdinalIgnoreCase);
    static int imGuardSkips;
    static readonly ConcurrentDictionary<string, DateTimeOffset> headsupTo = new(StringComparer.OrdinalIgnoreCase); // my last heads-up per avatar
    static readonly System.Text.RegularExpressions.Regex HeadsupRx = new(@"^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d) \[im-guard\] headsup: sent to .*?\(([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\)", System.Text.RegularExpressions.RegexOptions.Compiled);
    static void NoteHeadsup(string id, DateTimeOffset t) => headsupTo.AddOrUpdate(id, t, (_, old) => t > old ? t : old);
    static DateTimeOffset? LastHeadsup(string id) => headsupTo.TryGetValue(id, out var t) ? t : null;
    // called by Program.SeedMyIms for each log line
    static void SeedHeadsupLine(string line, Func<DateTime, DateTimeOffset> ord = null)
    {
        if (!line.Contains("[im-guard] headsup: sent to ")) return;
        var m = HeadsupRx.Match(line);
        if (m.Success && DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t)) NoteHeadsup(m.Groups[2].Value, ord != null ? ord(t) : new DateTimeOffset(t));
    }

    // ---- incoming message ids (in memory; ids keep increasing across restarts: unix seconds * 100 at start) ----
    internal sealed class InMsg { public long Id; public DateTimeOffset T; public string Text = ""; public DateTimeOffset? AnsweredAt; public string AnsweredBy; public bool Explicit; }
    static long imMsgSeq = DateTimeOffset.UtcNow.ToUnixTimeSeconds() * 100;
    static readonly ConcurrentDictionary<string, List<InMsg>> inMsgs = new(StringComparer.OrdinalIgnoreCase);
    public static long NoteInbound(string from, string text, DateTimeOffset? t = null)
    {
        var m = new InMsg { Id = Interlocked.Increment(ref imMsgSeq), T = t ?? DateTimeOffset.Now, Text = text ?? "" };
        var l = inMsgs.GetOrAdd(from, _ => new List<InMsg>());
        lock (l) { l.Add(m); if (l.Count > 200) l.RemoveRange(0, l.Count - 200); }
        return m.Id;
    }
    static List<InMsg> InMsgsOf(string from) { if (!inMsgs.TryGetValue(from, out var l)) return new(); lock (l) return l.ToList(); }
    // webhook: was this message answered with an explicit 'im --re'? (those lines are not re-POSTed)
    public static bool AnsweredExplicitly(string from, long id) => InMsgsOf(from).Any(m => m.Id == id && m.AnsweredAt != null && m.Explicit);
    static string Short(string t, int n = 60) => t.Length <= n ? t : t[..n] + "…";

    // pure (selftest-covered): may a reply claiming `re` go out? null = yes, else the "skipped: ..." text
    // kind/logCmd/reCmd customize IM vs nearby-chat wording ('im' / 'say').
    internal static string ReClaimCheck(IReadOnlyList<InMsg> msgs, IReadOnlyCollection<long> re, string name,
                                        string kind = "IM", string logCmd = "imlog", string reCmd = "im --re")
    {
        var unknown = re.Where(id => !msgs.Any(m => m.Id == id)).ToList();
        if (unknown.Count > 0) return $"skipped: message id(s) {string.Join(",", unknown)} are not {kind}s from {name} on record (see '{logCmd}'); nothing sent.";
        var done = msgs.Where(m => re.Contains(m.Id) && m.AnsweredAt != null).ToList();
        if (done.Count == 0) return null;
        var open = msgs.Where(m => m.AnsweredAt == null).ToList();
        return $"skipped: already answered {name}'s message(s) " + string.Join("; ", done.Select(m => $"{m.Id} '{Short(m.Text, 40)}' at {PT(m.AnsweredAt!.Value)} by: '{Short(m.AnsweredBy ?? "", 80)}'")) +
               (open.Count == 0 ? ". Nothing from them is unanswered; nothing sent." : $". Still unanswered: {string.Join("; ", open.Select(m => $"{m.Id} '{Short(m.Text, 60)}'"))} - reply to only those with '{reCmd} <ids>'; nothing sent.");
    }
    // pure: mark answered (explicit ids, or every message received up to `upTo` for a plain 'im')
    internal static void MarkAnswered(IEnumerable<InMsg> msgs, IReadOnlyCollection<long> re, DateTimeOffset now, string text, DateTimeOffset? upTo)
    {
        foreach (var m in msgs)
        {
            if (m.AnsweredAt != null) continue;
            bool hit = re != null && re.Count > 0 ? re.Contains(m.Id) : upTo != null && m.T <= upTo.Value;
            if (hit) { m.AnsweredAt = now; m.AnsweredBy = text; m.Explicit = re != null && re.Count > 0; }
        }
    }

    // ---- 'im' recipient sanity: words that are commands, not names ----
    internal static readonly HashSet<string> ImSubcommandWords = new(StringComparer.OrdinalIgnoreCase) { "history", "log", "imlog", "logs", "unanswered", "check", "help", "status", "list", "show", "last", "read", "search", "find", "get", "send", "to", "selftest" };
    // pure (selftest-covered): 'im history|log|logs|imlog <name> [n]' is the read-only lookup, never a send
    internal static bool IsImHistoryCmd(string[] a) => a != null && a.Length > 0 && a[0].ToLowerInvariant() is "history" or "log" or "logs" or "imlog";
    // pure (selftest-covered): null = OK to send to this target, else the refusal
    internal static string ImTargetCheck(string target, bool quoted, bool knownLocally)
    {
        var t = target.Trim();
        if (UUID.TryParse(t, out _)) return null;
        if (ImSubcommandWords.Contains(t)) return $"refused: '{t}' is a command word, not an avatar name; nothing sent. IM history is 'imlog <name> [n]' (or 'im history <name> [n]'); to IM someone use their full name, \"quoted name\" or uuid.";
        bool bare = !quoted && !t.Contains(' ') && !t.Contains('.');
        if (bare && !knownLocally) return $"refused: '{t}' is a single bare word and no avatar by that name is known here (friend, nearby, or someone who chatted); nothing sent. Use the full name ('First Last'), \"quoted name\", or uuid.";
        return null;
    }

    // 'imlog <name|uuid> [n]' (read-only): last n IM lines both ways from the log, plus message ids / answered state
    static async Task<string> ImLog(string[] a)
    {
        if (a.Length == 0) return "usage: imlog <First Last|username|uuid|\"Name\"> [n=20]   (read-only; also 'im history <name> [n]')";
        int n = 20; var parts = a.ToList();
        if (parts.Count > 1 && int.TryParse(parts[^1], out var nn) && nn > 0) { n = Math.Min(nn, 200); parts.RemoveAt(parts.Count - 1); }
        var who = string.Join(' ', parts).Trim('"');
        var id = await ResolveAvatar(who);
        if (id == UUID.Zero) return $"could not resolve avatar '{who}' (nothing sent)";
        var ids = id.ToString(); var lines = new List<string>();
        try
        {
            using var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length > 8_000_000) fs.Seek(-8_000_000, SeekOrigin.End);
            using var sr = new StreamReader(fs); string line;
            while ((line = sr.ReadLine()) != null)
                if ((line.Contains(" [im] ") || line.Contains(" [me-im] to ")) && line.Contains("(" + ids + ")")) { lines.Add(line); if (lines.Count > n * 2) lines.RemoveRange(0, lines.Count - n); }
        }
        catch (Exception ex) { return "imlog: could not read the log: " + ex.GetType().Name; }
        var last = lines.Skip(Math.Max(0, lines.Count - n)).ToList();
        var msgs = InMsgsOf(ids).TakeLast(n).ToList();
        var sb = new System.Text.StringBuilder($"IM history with {NameOf(id)} ({id}), last {last.Count} line(s), times PT (read-only, nothing sent):\n");
        foreach (var l in last) sb.AppendLine("  " + l);
        sb.AppendLine(msgs.Count == 0 ? "message ids: none this session" : "message ids this session (for 'im --re <ids>'):");
        foreach (var m in msgs) sb.AppendLine($"  {m.Id} {PT(m.T)} '{Short(m.Text, 80)}' -> {(m.AnsweredAt == null ? "UNANSWERED" : $"answered {PT(m.AnsweredAt.Value)}{(m.Explicit ? "" : " (plain im)")}")}");
        return sb.ToString().TrimEnd();
    }

    internal enum ImGuardResult { Send, Window, Answered, HeadsupUsed }

    // pure (selftest-covered)
    internal static ImGuardResult ImGuardDecide(DateTimeOffset now, DateTimeOffset? lastMe, DateTimeOffset? lastIn, bool isDavid, bool force, TimeSpan window, TimeSpan fresh,
                                                bool headsup = false, DateTimeOffset? lastHeadsup = null)
    {
        if (force || lastMe == null) return ImGuardResult.Send;
        if (now - lastMe.Value < window) return ImGuardResult.Window;
        if (isDavid) return ImGuardResult.Send;
        if (now - lastMe.Value >= fresh) return ImGuardResult.Send;               // old exchange: fresh conversation
        if (lastIn == null || lastMe.Value > lastIn.Value)
        {
            if (!headsup) return ImGuardResult.Answered;
            // one heads-up per their latest incoming IM: refused if I already sent one after it (and recently)
            bool used = lastHeadsup != null && (lastIn == null || lastHeadsup.Value > lastIn.Value) && now - lastHeadsup.Value < fresh;
            return used ? ImGuardResult.HeadsupUsed : ImGuardResult.Send;
        }
        return ImGuardResult.Send;                                                 // they wrote after my last IM
    }

    static string PT(DateTimeOffset t) => t.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " PT";

    static string ImGuardSkipText(ImGuardResult r, string name, DateTimeOffset now, DateTimeOffset lastMe, DateTimeOffset? lastIn, DateTimeOffset? lastHeadsup = null) => r switch
    {
        ImGuardResult.Window => $"skipped: already sent {name} an IM at {PT(lastMe)} ({(now - lastMe).TotalSeconds:F1} s ago, {ImGuardWindow.TotalSeconds:0} s duplicate window); not sent again. Use 'im --force' only for a manual/David-directed extra message.",
        ImGuardResult.HeadsupUsed => $"skipped: heads-up already sent to {name} at {PT(lastHeadsup ?? lastMe)} (their latest IM: {(lastIn == null ? "none on record" : PT(lastIn.Value))}); only one heads-up until they write again. Use 'im --force' only for a manual/David-directed extra message.",
        _ => $"skipped: already answered {name} at {PT(lastMe)} (their latest IM: {(lastIn == null ? "none on record" : PT(lastIn.Value))}); not sent again. If you must wait (e.g. to check with David), send one short note with 'im --headsup'; 'im --force' only for a manual/David-directed extra message.",
    };

    // decide + send + record under the recipient's lock, so two racing runs cannot both pass the check
    static (bool sent, string reply) ImGuardedSend(string id, string name, bool isDavid, bool force, Action send, Func<DateTimeOffset> clock = null, bool headsup = false,
                                                   IReadOnlyCollection<long> re = null, string text = null)
    {
        lock (imGuardLocks.GetOrAdd(id, _ => new object()))
        {
            var now = (clock ?? (() => DateTimeOffset.Now))();
            var list = inMsgs.GetOrAdd(id, _ => new List<InMsg>());
            if (re != null && re.Count > 0)
            {
                // claim-and-send: check, send and mark under the same lock, so two runs can never answer the same message
                string why; lock (list) why = ReClaimCheck(list, re, name);
                if (why != null) { Interlocked.Increment(ref imGuardSkips); Log("im-guard", why); return (false, why); }
                send();
                NoteMyIm(id, now);
                lock (list) MarkAnswered(list, re, now, text ?? "", null);
                Log("im-guard", $"--re {string.Join(",", re)}: sent to {name} ({id}){(force ? " (--force ignored with --re)" : "")}");
                return (true, null);
            }
            var lastMe = LastMyImTo(id); var lastIn = LastImFrom(id);
            var lastHu = LastHeadsup(id);
            var r = ImGuardDecide(now, lastMe, lastIn, isDavid, force, ImGuardWindow, ImGuardFresh, headsup, lastHu);
            if (r != ImGuardResult.Send)
            {
                Interlocked.Increment(ref imGuardSkips);
                var txt = ImGuardSkipText(r, name, now, lastMe!.Value, lastIn, lastHu);
                Log("im-guard", txt);
                return (false, txt);
            }
            send();
            NoteMyIm(id, now);
            if (!headsup) lock (list) MarkAnswered(list, null, now, text ?? "", now); // plain im: everything received so far counts as answered
            if (headsup && !force) { NoteHeadsup(id, now); Log("im-guard", $"headsup: sent to {name} ({id}) - one heads-up until they write again"); }
            if (force) Log("im-guard", $"--force: sent to {name} without the duplicate check");
            return (true, null);
        }
    }

    static async Task<string> ImGuardCmd(string[] a)
    {
        if (a.Length > 0 && a[0] == "selftest") return await ImGuardSelfTest();
        if (a.Length > 1 && a[0] == "check")
        {
            var who = string.Join(' ', a.Skip(1)).Trim('"');
            var id = await ResolveAvatar(who);
            if (id == UUID.Zero) return $"could not resolve avatar '{who}'";
            var now = DateTimeOffset.Now; var lastMe = LastMyImTo(id.ToString()); var lastIn = LastImFrom(id.ToString());
            var hu = LastHeadsup(id.ToString());
            var r = ImGuardDecide(now, lastMe, lastIn, id == DavidId, false, ImGuardWindow, ImGuardFresh);
            var rh = ImGuardDecide(now, lastMe, lastIn, id == DavidId, false, ImGuardWindow, ImGuardFresh, true, hu);
            return $"dry run for {NameOf(id)} ({id}): my last IM {(lastMe == null ? "none" : PT(lastMe.Value))}, their latest IM {(lastIn == null ? "none" : PT(lastIn.Value))}, my last heads-up {(hu == null ? "none" : PT(hu.Value))}\n" +
                   "  'im' now: " + (r == ImGuardResult.Send ? "WOULD be sent" : "would be " + ImGuardSkipText(r, NameOf(id), now, lastMe!.Value, lastIn, hu)) + "\n" +
                   "  'im --headsup' now: " + (rh == ImGuardResult.Send ? "WOULD be sent" : "would be " + ImGuardSkipText(rh, NameOf(id), now, lastMe!.Value, lastIn, hu));
        }
        return $"im guard: {ImGuardWindow.TotalSeconds:0} s duplicate window, already-answered check (fresh after {ImGuardFresh.TotalHours:0.#} h), David only the window; one 'im --headsup' per their latest IM; skips this process: {imGuardSkips}. 'imguard check <name>' | 'imguard selftest'";
    }

    static async Task<string> ImGuardSelfTest()
    {
        var sb = new System.Text.StringBuilder(); int pass = 0, fail = 0; void C(bool ok, string w) { if (ok) pass++; else fail++; sb.AppendLine($"{(ok ? "PASS" : "FAIL")} {w}"); }
        int skips0 = imGuardSkips;
        var t = DateTimeOffset.Now; TimeSpan S(double s) => TimeSpan.FromSeconds(s); var W = S(5); var F = TimeSpan.FromHours(6);
        // pure rules
        C(ImGuardDecide(t, null, null, false, false, W, F) == ImGuardResult.Send, "fresh conversation (never IMed them) -> send");
        C(ImGuardDecide(t, null, t - S(10), false, false, W, F) == ImGuardResult.Send, "first reply to their IM -> send");
        C(ImGuardDecide(t, t - S(30), t - S(10), false, false, W, F) == ImGuardResult.Send, "they wrote after my last IM -> send");
        C(ImGuardDecide(t, t - S(1), t - S(10), false, false, W, F) == ImGuardResult.Window, "1 s after my IM -> refused (5 s window)");
        C(ImGuardDecide(t, t - S(20), t - S(30), false, false, W, F) == ImGuardResult.Answered, "my IM newer than their latest -> refused (already answered)");
        C(ImGuardDecide(t, t - S(20), null, false, false, W, F) == ImGuardResult.Answered, "I IMed them 20 s ago, nothing back -> refused (already answered)");
        C(ImGuardDecide(t, t - TimeSpan.FromHours(7), t - TimeSpan.FromHours(8), false, false, W, F) == ImGuardResult.Send, "last exchange 7 h ago -> fresh conversation, send");
        C(ImGuardDecide(t, t - S(20), t - S(30), true, false, W, F) == ImGuardResult.Send, "David: already-answered rule does not apply -> send");
        C(ImGuardDecide(t, t - S(2), t - S(30), true, false, W, F) == ImGuardResult.Window, "David: still the 5 s window");
        C(ImGuardDecide(t, t - S(1), t - S(30), false, true, W, F) == ImGuardResult.Send && ImGuardDecide(t, t - S(20), null, false, true, W, F) == ImGuardResult.Send, "--force skips both checks");
        // race: two concurrent sends to the same synthetic recipient -> exactly one goes out
        var id = "99999999-9999-4999-9999-" + Random.Shared.Next(100000000, 999999999).ToString("D12");
        int sends = 0;
        NoteImFrom(id, DateTimeOffset.Now.AddSeconds(-30)); // their IM 30 s ago
        var go = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(() => { go.Wait(); return ImGuardedSend(id, "Selftest Person", false, false, () => { Interlocked.Increment(ref sends); Thread.Sleep(50); }); })).ToArray();
        go.Set(); var res = await Task.WhenAll(tasks);
        C(sends == 1 && res.Count(x => x.sent) == 1, $"race: 2 concurrent 'im' to one person -> {sends} sent");
        var skip = res.FirstOrDefault(x => !x.sent).reply ?? "";
        C(skip.StartsWith("skipped: already sent Selftest Person an IM at "), "racing duplicate gets the 5 s window skip text");
        var later = ImGuardedSend(id, "Selftest Person", false, false, () => Interlocked.Increment(ref sends), () => DateTimeOffset.Now.AddSeconds(10));
        C(!later.sent && later.reply.StartsWith("skipped: already answered Selftest Person at "), $"10 s later, no new IM from them -> '{later.reply[..Math.Min(60, later.reply.Length)]}…'");
        NoteImFrom(id, DateTimeOffset.Now.AddSeconds(11));
        var reply2 = ImGuardedSend(id, "Selftest Person", false, false, () => Interlocked.Increment(ref sends), () => DateTimeOffset.Now.AddSeconds(15));
        C(reply2.sent, "they IM again -> the next reply goes through");
        var forced = ImGuardedSend(id, "Selftest Person", false, true, () => Interlocked.Increment(ref sends), () => DateTimeOffset.Now.AddSeconds(16));
        C(forced.sent && sends == 3, "--force after that -> sent");
        ForgetMyIm(id); ForgetImFrom(id); imGuardLocks.TryRemove(id, out _);
        // heads-up: pure rules
        C(ImGuardDecide(t, t - S(30), t - S(60), false, false, W, F, true, null) == ImGuardResult.Send, "heads-up after an answered exchange (none sent yet) -> send");
        C(ImGuardDecide(t, t - S(20), t - S(60), false, false, W, F, true, t - S(20)) == ImGuardResult.HeadsupUsed, "second heads-up, they have not written since -> refused");
        C(ImGuardDecide(t, t - S(20), t - S(10), false, false, W, F, true, t - S(20)) == ImGuardResult.Send, "they wrote after my heads-up -> a normal reply / new heads-up is allowed");
        C(ImGuardDecide(t, t - S(2), t - S(60), false, false, W, F, true, null) == ImGuardResult.Window, "heads-up still obeys the 5 s window");
        C(ImGuardDecide(t, t - S(20), null, false, false, W, F, true, null) == ImGuardResult.Send && ImGuardDecide(t, t - S(20), null, false, false, W, F, true, t - S(20)) == ImGuardResult.HeadsupUsed, "no IM from them on record: one heads-up, then refused");
        // heads-up: live path + race (synthetic recipient)
        var id2 = "99999999-9999-4999-8888-" + Random.Shared.Next(100000000, 999999999).ToString("D12");
        NoteImFrom(id2, DateTimeOffset.Now.AddSeconds(-60)); NoteMyIm(id2, DateTimeOffset.Now.AddSeconds(-30)); // answered 30 s ago
        int hs = 0;
        var go2 = new ManualResetEventSlim(false);
        var ht = Enumerable.Range(0, 3).Select(_ => Task.Run(() => { go2.Wait(); return ImGuardedSend(id2, "Selftest Person", false, false, () => { Interlocked.Increment(ref hs); Thread.Sleep(50); }, null, true); })).ToArray();
        go2.Set(); var hr = await Task.WhenAll(ht);
        C(hs == 1 && hr.Count(x => x.sent) == 1, $"race: 3 concurrent 'im --headsup' -> {hs} sent");
        var plain = ImGuardedSend(id2, "Selftest Person", false, false, () => Interlocked.Increment(ref hs), () => DateTimeOffset.Now.AddSeconds(10));
        C(!plain.sent && plain.reply.Contains("'im --headsup'"), "plain 'im' after it: refused, and the text points to --headsup");
        var hu2 = ImGuardedSend(id2, "Selftest Person", false, false, () => Interlocked.Increment(ref hs), () => DateTimeOffset.Now.AddSeconds(10), true);
        C(!hu2.sent && hu2.reply.StartsWith("skipped: heads-up already sent to Selftest Person at "), "second heads-up 10 s later -> 'skipped: heads-up already sent ...'");
        NoteImFrom(id2, DateTimeOffset.Now.AddSeconds(20));
        var hu3 = ImGuardedSend(id2, "Selftest Person", false, false, () => Interlocked.Increment(ref hs), () => DateTimeOffset.Now.AddSeconds(30), true);
        C(hu3.sent && hs == 2, "they write again -> one more heads-up allowed");
        var sl = "2026-10-03 12:00:00 [im-guard] headsup: sent to Seed Person (" + id2 + ") - one heads-up until they write again";
        headsupTo.TryRemove(id2, out _); SeedHeadsupLine(sl);
        C(LastHeadsup(id2)?.ToString("HH:mm:ss") == "12:00:00", "heads-up time is re-read from the [im-guard] headsup log line after a restart");
        ForgetMyIm(id2); ForgetImFrom(id2); headsupTo.TryRemove(id2, out _); imGuardLocks.TryRemove(id2, out _);
        // message ids + 'im --re' claim (the 20:46 PT 'yummy' double reply, replayed with synthetic ids)
        var id3 = "99999999-9999-4999-7777-" + Random.Shared.Next(100000000, 999999999).ToString("D12");
        var q = NoteInbound(id3, "David is not on much is he ?"); var y = NoteInbound(id3, "it was yummy"); var sm = NoteInbound(id3, "😃😃😃");
        C(y == q + 1 && sm == y + 1, "incoming IMs get increasing message ids");
        int s3 = 0;
        var a1 = ImGuardedSend(id3, "Selftest Person", false, false, () => s3++, null, false, new long[] { y }, "Yay, I am so glad");
        var b1 = ImGuardedSend(id3, "Selftest Person", false, true, () => s3++, null, false, new long[] { q, y, sm }, "Glad it was yummy, and no, David...");
        C(a1.sent && !b1.sent && s3 == 1 && b1.reply.Contains($"{y} 'it was yummy'") && b1.reply.Contains($"Still unanswered: {q} ") && b1.reply.Contains($"{sm} "),
            "second run claiming an answered message is refused even with --force, and is told which ids are still open");
        var b2 = ImGuardedSend(id3, "Selftest Person", false, false, () => s3++, null, false, new long[] { q, sm }, "No, David isn't on as much as I'd like");
        C(b2.sent && s3 == 2, "...then answering only the open ids goes through right away (no 5 s window / time-only rule with --re)");
        C(!ImGuardedSend(id3, "Selftest Person", false, false, () => s3++, null, false, new long[] { 12345 }, "x").sent, "unknown message id -> refused");
        var w = NoteInbound(id3, "late line"); int s4 = 0;
        var go3 = new ManualResetEventSlim(false);
        var rt = Enumerable.Range(0, 3).Select(_ => Task.Run(() => { go3.Wait(); return ImGuardedSend(id3, "Selftest Person", false, false, () => { Interlocked.Increment(ref s4); Thread.Sleep(50); }, null, false, new long[] { w }, "reply"); })).ToArray();
        go3.Set(); var rr = await Task.WhenAll(rt);
        C(s4 == 1 && rr.Count(x => x.sent) == 1, $"race: 3 concurrent 'im --re {w}' -> {s4} sent");
        var p1 = NoteInbound(id3, "plain one"); NoteImFrom(id3, DateTimeOffset.Now.AddSeconds(1)); var pr = ImGuardedSend(id3, "Selftest Person", false, false, () => { }, () => DateTimeOffset.Now.AddSeconds(30), false, null, "plain reply");
        C(pr.sent && InMsgsOf(id3).First(m => m.Id == p1).AnsweredAt != null && !AnsweredExplicitly(id3, p1) && AnsweredExplicitly(id3, w), "plain 'im' marks earlier messages answered (implicit); --re marks explicitly");
        ForgetMyIm(id3); ForgetImFrom(id3); inMsgs.TryRemove(id3, out _); imGuardLocks.TryRemove(id3, out _);
        // recipient sanity (the 21:08 PT 'im history ThomasNejutto' -> History Resident)
        C(ImTargetCheck("history", false, true) != null && ImTargetCheck("History", true, true) != null && ImTargetCheck("log", false, false) != null, "'history' / 'log' are command words -> refused even if quoted or known");
        C(ImTargetCheck("somestranger", false, false) != null, "bare single word not known locally -> refused (no directory lookup)");
        C(ImTargetCheck("thomasnejutto", false, true) == null && ImTargetCheck("ThomasNejutto Resident", false, false) == null && ImTargetCheck("Some Body", true, false) == null
          && ImTargetCheck("aebe8a43-0170-487f-aad8-3df2dc0137b3", false, false) == null && ImTargetCheck("first.last", false, false) == null, "known username, 'First Last', quoted name, uuid, first.last -> allowed");
        var (tg, tx) = SplitTarget("history ThomasNejutto");
        C(tg == "history" && ImTargetCheck(tg, false, true) != null, "'im history ThomasNejutto' parses target 'history' -> refused (never sent)");
        string[] Args(string r) => r.Split(' ', StringSplitOptions.RemoveEmptyEntries); // as RunCmd splits 'im' args
        C(IsImHistoryCmd(Args("history ThomasNejutto")) && IsImHistoryCmd(Args("History ThomasNejutto Resident 30")) && IsImHistoryCmd(Args("log Ryan")),
            "'im history|log <name> [n]' is routed to the read-only lookup (rate guard and send path skipped)");
        C(!IsImHistoryCmd(Args("\"History Resident\" hi")) && !IsImHistoryCmd(Args("ThomasNejutto hi")) && !IsImHistoryCmd(Args("--re 1 Ryan hi")) && !IsImHistoryCmd(Array.Empty<string>()),
            "normal 'im' (incl. a quoted \"History ...\" name, --re) is not taken for a history lookup");
        var (tq, _) = SplitTarget("\"history\" ThomasNejutto");
        C(ImTargetCheck(tq, true, false) != null, "MCP-style quoted 'im \"history\" ThomasNejutto' -> refused by the target guard (never sent)");
        Interlocked.Exchange(ref imGuardSkips, skips0);
        return $"im guard selftest: {pass} PASS, {fail} FAIL (synthetic recipient; nothing sent to SL)\n" + sb.ToString().TrimEnd();
    }
}
