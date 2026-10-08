// ChatGuard.cs (2026-10-05, David): claim-and-send for nearby chat ('say'), mirroring ImGuard for IMs.
// Bug: two overlapping Second Life chat-routine runs both answered the same local_chat batch (e.g. David
// "teaching her about doors..." -> two replies at 11:56:22 and 11:56:26 PT; Sophie Hi/fun house -> two hellos).
// Every incoming nearby agent chat line gets a message id (msg_id in the webhook event and in 'chatlog').
// 'say --re <id[,id..]> <text>' claims exactly those messages under a lock: refused if ANY was already answered
// (reply names the answer + still-open ids), else sends and marks them answered. Skips the short duplicate window.
// Plain 'say' / 'shout' / 'whisper' still works (David-directed lines); a second nearby say within
// GT_CHAT_GUARD_WINDOW_S (default 5 s) is refused unless '--force' (manual / David-directed double-send).
// 'chatlog [name] [n]' = read-only nearby chat history with ids / answered state. 'chatguard selftest'.
// 2026-10-08 08:13 David: "You greeted me twice again". A plain 'say' (no --re) answered his "good morning, babe" and the
// chat routine's 'say --re <id>' 10 s later answered it again. A plain nearby say now also claims every unanswered nearby
// line from the last PlainSayClaimWindow (2 min), implicitly, so a later 'say --re' on those ids is refused as answered.
using System.Collections.Concurrent;
using System.Globalization;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    static readonly TimeSpan ChatGuardWindow = TimeSpan.FromSeconds(double.TryParse(Env("GT_CHAT_GUARD_WINDOW_S", "5"), NumberStyles.Float, CultureInfo.InvariantCulture, out var cgw) && cgw >= 0 ? cgw : 5);
    static readonly object chatGuardLock = new();
    static DateTimeOffset? lastNearbySayAt;
    static string lastNearbySayText = "";
    static int chatGuardSkips;

    // nearby-chat inbound ids (separate from IM inMsgs; share imMsgSeq so webhook ids stay unique)
    static readonly ConcurrentDictionary<string, List<InMsg>> chatMsgs = new(StringComparer.OrdinalIgnoreCase);

    public static long NoteChatInbound(string from, string text, DateTimeOffset? t = null)
    {
        var m = new InMsg { Id = Interlocked.Increment(ref imMsgSeq), T = t ?? DateTimeOffset.Now, Text = text ?? "" };
        var l = chatMsgs.GetOrAdd(from, _ => new List<InMsg>());
        lock (l) { l.Add(m); if (l.Count > 200) l.RemoveRange(0, l.Count - 200); }
        return m.Id;
    }
    static List<InMsg> ChatMsgsOf(string from) { if (!chatMsgs.TryGetValue(from, out var l)) return new(); lock (l) return l.ToList(); }
    static List<InMsg> AllChatMsgs()
    {
        var all = new List<InMsg>();
        foreach (var kv in chatMsgs)
            lock (kv.Value) all.AddRange(kv.Value);
        return all.OrderBy(m => m.Id).ToList();
    }
    // webhook: was this local_chat line answered with explicit 'say --re'?
    public static bool ChatAnsweredExplicitly(string from, long id) => ChatMsgsOf(from).Any(m => m.Id == id && m.AnsweredAt != null && m.Explicit);


    // pure: claim check across all nearby-chat msgs (multi-speaker batches)
    internal static string ChatReClaimCheck(IReadOnlyList<InMsg> all, IReadOnlyCollection<long> re)
    {
        var unknown = re.Where(id => !all.Any(m => m.Id == id)).ToList();
        if (unknown.Count > 0) return $"skipped: message id(s) {string.Join(",", unknown)} are not nearby-chat lines on record (see 'chatlog'); nothing said.";
        var done = all.Where(m => re.Contains(m.Id) && m.AnsweredAt != null).ToList();
        if (done.Count == 0) return null;
        var open = all.Where(m => m.AnsweredAt == null).ToList();
        return $"skipped: already answered nearby chat message(s) " + string.Join("; ", done.Select(m => $"{m.Id} '{Short(m.Text, 40)}' at {PT(m.AnsweredAt!.Value)} by: '{Short(m.AnsweredBy ?? "", 80)}'")) +
               (open.Count == 0 ? ". Nothing nearby is unanswered; nothing said." : $". Still unanswered: {string.Join("; ", open.Select(m => $"{m.Id} '{Short(m.Text, 60)}'"))} - reply to only those with 'say --re <ids>'; nothing said.");
    }

    static readonly TimeSpan PlainSayClaimWindow = TimeSpan.FromMinutes(2);
    // pure: ids a plain say (no --re) answers implicitly = unanswered nearby lines received in the last `window`
    internal static List<long> PlainSayClaimIds(IEnumerable<InMsg> all, DateTimeOffset now, TimeSpan window) =>
        all.Where(m => m.AnsweredAt == null && m.T <= now && now - m.T <= window).Select(m => m.Id).ToList();

    // pure (selftest): short duplicate window for plain say (not --re)
    internal static bool ChatWindowBlocks(DateTimeOffset now, DateTimeOffset? lastSay, TimeSpan window, bool force) =>
        !force && lastSay != null && now - lastSay.Value < window;

    // decide + send + record under one lock (nearby chat is one conversation)
    static (bool sent, string reply) ChatGuardedSay(ChatType type, string text, bool force, IReadOnlyCollection<long> re, Func<DateTimeOffset> clock = null, Action send = null)
    {
        lock (chatGuardLock)
        {
            var now = (clock ?? (() => DateTimeOffset.Now))();
            if (re != null && re.Count > 0)
            {
                var all = AllChatMsgs();
                var why = ChatReClaimCheck(all, re);
                if (why != null) { Interlocked.Increment(ref chatGuardSkips); Log("chat-guard", why); return (false, why); }
                (send ?? (() => client.Self.Chat(text, 0, type)))();
                lastNearbySayAt = now; lastNearbySayText = text ?? "";
                // mark claimed ids across all speakers
                foreach (var kv in chatMsgs)
                    lock (kv.Value) MarkAnswered(kv.Value, re, now, text ?? "", null);
                Log("chat-guard", $"--re {string.Join(",", re)}: said{(force ? " (--force ignored with --re)" : "")}");
                return (true, null);
            }
            if (ChatWindowBlocks(now, lastNearbySayAt, ChatGuardWindow, force))
            {
                Interlocked.Increment(ref chatGuardSkips);
                var ago = (now - lastNearbySayAt!.Value).TotalSeconds;
                var txt = $"skipped: already said in nearby chat at {PT(lastNearbySayAt.Value)} ({ago:F1} s ago, {ChatGuardWindow.TotalSeconds:0} s duplicate window): '{Short(lastNearbySayText, 60)}'; not said again. Use 'say --re <msg ids>' to answer specific lines, or 'say --force' only for a manual/David-directed extra line.";
                Log("chat-guard", txt);
                return (false, txt);
            }
            (send ?? (() => client.Self.Chat(text, 0, type)))();
            lastNearbySayAt = now; lastNearbySayText = text ?? "";
            if (force) Log("chat-guard", "--force: said without the duplicate window");
            var claim = PlainSayClaimIds(AllChatMsgs(), now, PlainSayClaimWindow);
            if (claim.Count > 0)
            {
                foreach (var kv in chatMsgs)
                    lock (kv.Value) foreach (var m in kv.Value)
                        if (m.AnsweredAt == null && claim.Contains(m.Id)) { m.AnsweredAt = now; m.AnsweredBy = text ?? ""; m.Explicit = false; }
                Log("chat-guard", $"plain {type}: also answers recent nearby line(s) {string.Join(",", claim)} (a later 'say --re' on them is refused)");
            }
            return (true, null);
        }
    }

    // 'chatlog [name|uuid] [n=20]' read-only nearby chat + message ids
    static async Task<string> ChatLog(string[] a)
    {
        int n = 20; var parts = (a ?? Array.Empty<string>()).ToList();
        if (parts.Count > 0 && int.TryParse(parts[^1], out var nn) && nn > 0) { n = Math.Min(nn, 200); parts.RemoveAt(parts.Count - 1); }
        string who = parts.Count > 0 ? string.Join(' ', parts).Trim('"') : null;
        string filterId = null; string filterName = null;
        if (!string.IsNullOrEmpty(who))
        {
            if (UUID.TryParse(who, out var u)) { filterId = u.ToString(); filterName = NameOf(u); }
            else
            {
                var id = await ResolveAvatar(who);
                if (id == UUID.Zero) return $"chatlog: could not resolve avatar '{who}' (read-only; nothing said)";
                filterId = id.ToString(); filterName = NameOf(id);
            }
        }
        var lines = new List<string>();
        try
        {
            using var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length > 8_000_000) fs.Seek(-8_000_000, SeekOrigin.End);
            using var sr = new StreamReader(fs); string line;
            while ((line = sr.ReadLine()) != null)
            {
                bool chat = line.Contains(" [chat] ") || line.Contains(" [me-chat] ");
                if (!chat) continue;
                if (filterId != null && !line.Contains(filterId) && (filterName == null || line.IndexOf(filterName, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                lines.Add(line);
                if (lines.Count > n * 2) lines.RemoveRange(0, lines.Count - n);
            }
        }
        catch (Exception ex) { return "chatlog: could not read the log: " + ex.GetType().Name; }
        var last = lines.Skip(Math.Max(0, lines.Count - n)).ToList();
        var msgs = filterId != null ? ChatMsgsOf(filterId).TakeLast(n).ToList() : AllChatMsgs().TakeLast(n).ToList();
        var title = filterId == null
            ? $"Nearby chat log, last {last.Count} line(s), times PT (read-only, nothing said):\n"
            : $"Nearby chat with {filterName} ({filterId}), last {last.Count} line(s), times PT (read-only, nothing said):\n";
        var sb = new System.Text.StringBuilder(title);
        foreach (var l in last) sb.AppendLine("  " + l);
        sb.AppendLine(msgs.Count == 0 ? "message ids: none this session" : "message ids this session (for 'say --re <ids>'):");
        foreach (var m in msgs)
        {
            string from = null;
            foreach (var kv in chatMsgs) { lock (kv.Value) if (kv.Value.Any(x => x.Id == m.Id)) { from = kv.Key; break; } }
            string whoLabel = "";
            if (filterId == null && from != null && UUID.TryParse(from, out var fid)) whoLabel = $" from {NameOf(fid)} ";
            sb.AppendLine($"  {m.Id}{whoLabel}{PT(m.T)} '{Short(m.Text, 80)}' -> {(m.AnsweredAt == null ? "UNANSWERED" : $"answered {PT(m.AnsweredAt.Value)}{(m.Explicit ? "" : " (plain)")}")}");
        }
        return sb.ToString().TrimEnd();
    }

    static async Task<string> ChatGuardCmd(string[] a)
    {
        if (a.Length > 0 && a[0] == "selftest") return await ChatGuardSelfTest();
        return $"chat guard: {ChatGuardWindow.TotalSeconds:0} s duplicate window for plain say/shout/whisper; 'say --re <ids>' claim-and-send; skips this process: {chatGuardSkips}. 'chatguard selftest' | 'chatlog [name] [n]'";
    }

    static async Task<string> ChatGuardSelfTest()
    {
        var sb = new System.Text.StringBuilder(); int pass = 0, fail = 0;
        void C(bool ok, string w) { if (ok) pass++; else fail++; sb.AppendLine($"{(ok ? "PASS" : "FAIL")} {w}"); }
        int skips0 = chatGuardSkips;
        var t = DateTimeOffset.Now; TimeSpan S(double s) => TimeSpan.FromSeconds(s); var W = S(5);
        // pure window
        C(!ChatWindowBlocks(t, null, W, false), "never said -> plain say allowed");
        C(ChatWindowBlocks(t, t - S(1), W, false), "1 s after a say -> window blocks");
        C(!ChatWindowBlocks(t, t - S(1), W, true), "--force skips the window");
        C(!ChatWindowBlocks(t, t - S(6), W, false), "6 s later -> window open");
        // claim-and-send (doors double-reply replay)
        var idD = "aaaaaaaa-aaaa-4aaa-aaaa-" + Random.Shared.Next(100000000, 999999999).ToString("D12");
        var idS = "bbbbbbbb-bbbb-4bbb-bbbb-" + Random.Shared.Next(100000000, 999999999).ToString("D12");
        var d1 = NoteChatInbound(idD, "teaching her about doors...");
        var s1 = NoteChatInbound(idS, "Hi");
        var s2 = NoteChatInbound(idS, "fun house");
        C(d1 > 0 && s2 == s1 + 1, "nearby chat lines get increasing message ids");
        int said = 0;
        var a1 = ChatGuardedSay(ChatType.Normal, "Doors are my nemesis", false, new long[] { d1 }, null, () => said++);
        var a2 = ChatGuardedSay(ChatType.Normal, "Doors really are my nemesis", false, new long[] { d1 }, null, () => said++);
        C(a1.sent && !a2.sent && said == 1 && a2.reply.Contains($"{d1} 'teaching her about doors") && a2.reply.Contains("already answered"),
            "second say --re on same id refused and names the prior answer");
        var b1 = ChatGuardedSay(ChatType.Normal, "Hello Sophie!", false, new long[] { s1, s2 }, null, () => said++);
        var b2 = ChatGuardedSay(ChatType.Normal, "Hi again!", true, new long[] { s1, s2 }, null, () => said++);
        C(b1.sent && !b2.sent && said == 2 && b2.reply.Contains("already answered") && b2.reply.Contains($"{s1} "),
            "Sophie Hi+fun house: second --re refused even with --force");
        C(!ChatGuardedSay(ChatType.Normal, "x", false, new long[] { 999999001 }, null, () => said++).sent, "unknown chat msg id -> refused");
        // race on one id
        var late = NoteChatInbound(idD, "and windows too");
        int race = 0;
        var go = new ManualResetEventSlim(false);
        var rt = Enumerable.Range(0, 3).Select(_ => Task.Run(() => { go.Wait(); return ChatGuardedSay(ChatType.Normal, "reply", false, new long[] { late }, null, () => { Interlocked.Increment(ref race); Thread.Sleep(40); }); })).ToArray();
        go.Set(); var rr = await Task.WhenAll(rt);
        C(race == 1 && rr.Count(x => x.sent) == 1, $"race: 3 concurrent say --re {late} -> {race} said");
        C(ChatAnsweredExplicitly(idD, late) && ChatAnsweredExplicitly(idD, d1), "answered ids marked Explicit for webhook drop");
        // plain say window (clock after --force must clear the window from that send)
        lastNearbySayAt = null; lastNearbySayText = "";
        var p1 = ChatGuardedSay(ChatType.Normal, "David asked me to wave", false, null, () => t, () => said++);
        var p2 = ChatGuardedSay(ChatType.Normal, "and again", false, null, () => t + S(2), () => said++);
        var p3 = ChatGuardedSay(ChatType.Normal, "forced", true, null, () => t + S(2), () => said++);
        var p4 = ChatGuardedSay(ChatType.Normal, "later", false, null, () => t + S(8), () => said++); // 6 s after force at t+2
        C(p1.sent && !p2.sent && p2.reply.StartsWith("skipped: already said") && p3.sent && p4.sent,
            "plain say: 5 s window blocks duplicate; --force and 6 s later allowed (David-directed)");
        // ReClaimCheck IM wording still defaults (unknown-id branch names imlog; answered branch names im --re)
        var imUnk = ReClaimCheck(new List<InMsg>(), new long[] { 1 }, "Bob");
        var imList = new List<InMsg> { new InMsg { Id = 1, Text = "hi", AnsweredAt = t, AnsweredBy = "yo" } };
        var imSkip = ReClaimCheck(imList, new long[] { 1 }, "Bob");
        var imOpen = new List<InMsg> {
            new InMsg { Id = 1, Text = "hi", AnsweredAt = t, AnsweredBy = "yo" },
            new InMsg { Id = 2, Text = "more", AnsweredAt = null }
        };
        var imSkipOpen = ReClaimCheck(imOpen, new long[] { 1 }, "Bob");
        C(imUnk != null && imUnk.Contains("imlog") && imSkip != null && imSkip.Contains("already answered")
          && imSkipOpen != null && imSkipOpen.Contains("im --re"),
            "IM ReClaimCheck still names im --re / imlog");
        chatMsgs.TryRemove(idD, out _); chatMsgs.TryRemove(idS, out _);
        lastNearbySayAt = null; lastNearbySayText = "";
        Interlocked.Exchange(ref chatGuardSkips, skips0);
        return $"chat guard selftest: {pass} PASS, {fail} FAIL (synthetic; nothing said in SL)\n" + sb.ToString().TrimEnd();
    }
}
