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
    static (bool sent, string reply) ImGuardedSend(string id, string name, bool isDavid, bool force, Action send, Func<DateTimeOffset> clock = null, bool headsup = false)
    {
        lock (imGuardLocks.GetOrAdd(id, _ => new object()))
        {
            var now = (clock ?? (() => DateTimeOffset.Now))();
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
        Interlocked.Exchange(ref imGuardSkips, skips0);
        return $"im guard selftest: {pass} PASS, {fail} FAIL (synthetic recipient; nothing sent to SL)\n" + sb.ToString().TrimEnd();
    }
}
