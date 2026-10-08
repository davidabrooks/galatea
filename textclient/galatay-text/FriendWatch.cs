// FriendWatch.cs (2026-10-04, David): notice David Nightingale coming online and wake the chat webhook routine.
// - Source: the friend online notification (FriendsManager.FriendOnline); fallback: every 60 s while logged in, ask the
//   server for his status again (requestonlinenotification), so a missed notification still turns into an online event.
// - Online notifications in the first 15 s after Galatea's own login are the server's "who is already online" list,
//   not a login: they only set the baseline. At 15 s she checks his status once: online -> david_login, reason
//   galatea_login (2026-10-04).
// - ~10 s after he comes online, ONE urgent webhook event kind 'david_login' (his name + uuid + pending reminders) if Galatea is
//   still logged in and he is still online. Debounce: at most one per 10 min (persisted in run/david-login.json), so a relog
//   or a flapping status doesn't greet twice.
// - 'friendwatch [status|selftest|simulate]': simulate fires the same path as kind 'david_login_test' (own debounce slot;
//   the routine must NOT IM anyone for it).
// - Reminders: /workspace/secondlife/inworld-reminders.md, one '- [pending] ...' / '- [delivered <time>] ...' line each;
//   'remind add <text> | remind list | remind done <n>' (works while logged out).
using System.Globalization;
using System.Text;
using System.Text.Json;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    static readonly UUID DavidAgent = new("44ce5a36-c1c7-4a68-ac9a-635ddfff6233");
    const string DavidName = "David Nightingale";
    static readonly string RemindersPath = Env("GT_REMINDERS", "/workspace/secondlife/inworld-reminders.md");
    static readonly string DavidLoginState = Env("GT_DAVID_LOGIN_STATE", "/home/box/viewers/textclient/run/david-login.json");
    static readonly TimeSpan GreetDelay = TimeSpan.FromSeconds(10), GreetDebounce = TimeSpan.FromMinutes(10), LoginBaseline = TimeSpan.FromSeconds(15);
    static DateTime myLoginAt = DateTime.MinValue, davidOnlineAt = DateTime.MinValue;
    static bool davidOnline; static int friendWatchHooked, friendPollRunning;
    static readonly Dictionary<string, DateTime> lastWake = new(); // kind -> last fired (UTC)
    static readonly object fwGate = new();

    // pure (selftest): should an online event fire a wake? (baseline window after my login, debounce)
    public static bool ShouldWake(DateTime now, DateTime myLogin, DateTime lastFired, bool simulated, bool atMyLogin = false) =>
        (simulated || atMyLogin || now - myLogin >= LoginBaseline) && now - lastFired >= GreetDebounce;
    static int loginGen;

    // called from LoginAsync after a good login
    static void FriendWatchStart()
    {
        myLoginAt = DateTime.UtcNow;
        if (Interlocked.Exchange(ref friendWatchHooked, 1) == 0)
        {
            try { var st = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(DavidLoginState)); lock (fwGate) foreach (var kv in st) lastWake[kv.Key] = kv.Value; } catch { }
            client.Friends.FriendOnline += (s, e) => { if (e.Friend.UUID == DavidAgent) DavidCameOnline("online notification", false); };
            client.Friends.FriendOffline += (s, e) => { if (e.Friend.UUID == DavidAgent) { if (davidOnline) Log("friendwatch", $"{DavidName} went offline"); davidOnline = false; NotePoseDavidOnSeat(false); } };
        }
        davidOnline = client.Friends.FriendList.TryGetValue(DavidAgent, out var f) && f.IsOnline;
        if (Interlocked.Exchange(ref friendPollRunning, 1) == 0) _ = Task.Run(FriendPoll);
        var gen = Interlocked.Increment(ref loginGen); _ = Task.Run(() => GalateaLoginCheck(gen));
    }

    // 2026-10-04 (David): she logs in while he is already online -> the same david_login wake, reason galatea_login. Online
    // notices in the first 15 s are the server's "already online" list (baseline); at 15 s, once the login has settled,
    // check his status (friend list + those notices, or ask the server) and wake if he is on. Same 10-min debounce.
    static async Task GalateaLoginCheck(int gen)
    {
        await Task.Delay(LoginBaseline);
        if (gen != loginGen || !LoggedIn) return;
        bool On() => davidOnline || client.Friends.FriendList.TryGetValue(DavidAgent, out var f) && f.IsOnline;
        if (!On()) { try { client.Friends.RequestOnlineNotification(DavidAgent); } catch { } await Task.Delay(3000); }
        if (gen != loginGen || !LoggedIn) return;
        if (!On()) { Log("friendwatch", $"galatea_login: {DavidName} is offline, no wake"); return; }
        davidOnline = true; if (davidOnlineAt == DateTime.MinValue) davidOnlineAt = DateTime.UtcNow;
        Log("friendwatch", $"galatea_login: {DavidName} is already online; wake now");
        Wake("galatea_login (I just logged in and he was already online)", false, true, TimeSpan.Zero);
    }

    static async Task FriendPoll()
    {
        try
        {
            while (!shuttingDown)
            {
                await Task.Delay(60000);
                if (!LoggedIn) continue;
                bool listed = client.Friends.FriendList.TryGetValue(DavidAgent, out var f) && f.IsOnline;
                if (listed && !davidOnline) DavidCameOnline("status poll", false); // the notification handler missed it
                try { client.Friends.RequestOnlineNotification(DavidAgent); } catch { } // the server answers with his current status
            }
        }
        finally { friendPollRunning = 0; }
    }

    static void DavidCameOnline(string source, bool simulated)
    {
        var now = DateTime.UtcNow;
        if (!simulated)
        {
            if (davidOnline) return;
            davidOnline = true; davidOnlineAt = now;
            if (now - myLoginAt < LoginBaseline) { Log("friendwatch", $"{DavidName} already online at my login ({source}): baseline, no wake"); return; }
            Log("friendwatch", $"{DavidName} came online ({source}); wake in {GreetDelay.TotalSeconds:F0} s");
        }
        Wake(source, simulated, false, GreetDelay);
    }

    static void Wake(string source, bool simulated, bool atMyLogin, TimeSpan delay)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(delay);
            var kind = simulated ? "david_login_test" : "david_login";
            if (!LoggedIn) { Log("friendwatch", $"{kind}: I'm not logged in any more, no wake"); return; }
            if (!simulated && !davidOnline) { Log("friendwatch", $"{kind}: he went offline again within {delay.TotalSeconds:F0} s, no wake"); return; }
            DateTime last; lock (fwGate) last = lastWake.GetValueOrDefault(kind, DateTime.MinValue);
            if (!ShouldWake(DateTime.UtcNow, myLoginAt, last, simulated, atMyLogin)) { Log("friendwatch", $"{kind}: debounced (last wake {last.ToLocalTime():HH:mm:ss} PT, < {GreetDebounce.TotalMinutes:F0} min)"); return; }
            lock (fwGate) { lastWake[kind] = DateTime.UtcNow; try { File.WriteAllText(DavidLoginState, JsonSerializer.Serialize(lastWake)); } catch { } }
            var pending = Reminders().Select((r, i) => (n: i + 1, r)).Where(x => x.r.pending).ToList();
            var text = DavidLoginText(simulated, atMyLogin, source, DateTime.Now, pending.Select(x => (x.n, x.r.text)).ToList());
            Log("friendwatch", $"{kind}: waking the chat routine ({pending.Count} pending reminder(s))");
            if (!simulated) NoteSpokeTo(DavidAgent, "david_login wake; the chat routine greets him"); // 19:38 double hi race
            Notify(kind, DavidName, DavidAgent, text, null);
        });
    }

    // ---- reminders file -------------------------------------------------------------------
    static List<(bool pending, string text, string line)> Reminders()
    {
        try
        {
            return File.Exists(RemindersPath) ? File.ReadAllLines(RemindersPath).Where(l => l.StartsWith("- [")).Select(l =>
            {
                var close = l.IndexOf(']'); var status = close > 3 ? l[3..close] : "";
                return (status == "pending", l[(close + 1)..].Trim(), l);
            }).ToList() : new();
        }
        catch { return new(); }
    }

    static string RemindCmd(string[] a)
    {
        string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " PT";
        var sub = a.Length > 0 ? a[0].ToLowerInvariant() : "list";
        lock (fwGate)
        {
            if (sub == "add" && a.Length > 1)
            {
                var text = string.Join(' ', a.Skip(1)).Replace('\n', ' ').Trim();
                if (!File.Exists(RemindersPath))
                    File.WriteAllText(RemindersPath, "# Galatea's in-world reminders for David (text client: remind add <text> | remind list | remind done <n>)\n" +
                        "# One per line: '- [pending] (added <time>) text' -> '- [delivered <time>] (added <time>) text'. Delivered on his next login.\n\n");
                File.AppendAllText(RemindersPath, $"- [pending] (added {Now()}) {text}\n");
                Log("remind", "added: " + text);
                return $"reminder #{Reminders().Count} added (pending)";
            }
            if (sub == "done" && a.Length > 1 && int.TryParse(a[1], out var n))
            {
                var lines = File.Exists(RemindersPath) ? File.ReadAllLines(RemindersPath).ToList() : new();
                int k = 0;
                for (int i = 0; i < lines.Count; i++)
                {
                    if (!lines[i].StartsWith("- [") || ++k != n) continue;
                    if (!lines[i].StartsWith("- [pending]")) return $"reminder #{n} is not pending: {lines[i]}";
                    lines[i] = $"- [delivered {Now()}]" + lines[i]["- [pending]".Length..];
                    File.WriteAllLines(RemindersPath, lines); Log("remind", $"#{n} marked delivered");
                    return $"reminder #{n} marked delivered";
                }
                return $"no reminder #{n}";
            }
            if (sub != "list") return "usage: remind add <text> | remind list | remind done <n>";
            var all = Reminders();
            if (all.Count == 0) return "no reminders";
            var sb = new StringBuilder($"{all.Count(r => r.pending)} pending of {all.Count} ({RemindersPath})\n");
            for (int i = 0; i < all.Count; i++) sb.AppendLine($"  #{i + 1} {all[i].line[2..]}");
            return sb.ToString().TrimEnd();
        }
    }

    // pure (selftest): the david_login event text. With no pending reminders the reminder clause is left out entirely
    // (David 17:45: a "No pending reminders." line made her tell him "no reminders"; he asked her not to).
    internal static string DavidLoginText(bool simulated, bool atMyLogin, string source, DateTime nowPt, IReadOnlyList<(int n, string text)> pending) =>
        (simulated ? "SIMULATED TEST (no real login; do NOT IM anyone): " : "") +
        $"{DavidName} ({DavidAgent}) {(atMyLogin ? "is online" : "came online")} ({source}) at {nowPt:HH:mm} PT." +
        (pending == null || pending.Count == 0 ? "" : $" Pending reminders ({pending.Count}): " + string.Join(" | ", pending.Select(x => $"#{x.n}: {x.text}")) + " (after delivering: 'remind done <n>')");

    static string FriendWatchCmd(string[] a)
    {
        var sub = a.Length > 0 ? a[0].ToLowerInvariant() : "status";
        if (sub == "selftest")
        {
            var sb = new StringBuilder(); int pass = 0, fail = 0; void C(bool ok, string w) { if (ok) pass++; else fail++; sb.AppendLine($"{(ok ? "PASS" : "FAIL")} {w}"); }
            var t = new DateTime(2026, 10, 4, 18, 0, 0, DateTimeKind.Utc); var never = DateTime.MinValue;
            C(!ShouldWake(t.AddSeconds(5), t, never, false), "online notice 5 s after my login = baseline, no wake");
            C(ShouldWake(t.AddMinutes(5), t, never, false), "online 5 min after my login -> wake");
            C(!ShouldWake(t.AddMinutes(15), t, t.AddMinutes(9), false), "relog 6 min after the last wake -> debounced");
            C(ShouldWake(t.AddMinutes(20), t, t.AddMinutes(9), false), "11 min after the last wake -> wake again");
            C(ShouldWake(t.AddSeconds(5), t, never, true), "simulate ignores the login baseline");
            C(ShouldWake(t.AddSeconds(15), t, never, false, true), "galatea_login: he was already online at my login -> wake");
            C(!ShouldWake(t.AddSeconds(15), t, t.AddMinutes(-4), false, true), "galatea_login 4 min after the last wake (relog) -> debounced");
            C(GalatayMcp.Webhook.UrgentKinds.Contains("david_login") && GalatayMcp.Webhook.UrgentKinds.Contains("david_login_test"), "david_login(_test) are urgent (immediate, cap-exempt)");
            var t0 = new DateTime(2026, 10, 5, 17, 45, 0);
            var none = DavidLoginText(false, false, "friend online", t0, new List<(int, string)>());
            C(!none.Contains("eminder", StringComparison.OrdinalIgnoreCase) && none.EndsWith("17:45 PT."), "no pending reminders -> no reminder clause at all ('" + none + "')");
            var some = DavidLoginText(false, true, "galatea_login", t0, new List<(int, string)> { (2, "buy milk") });
            C(some.Contains("Pending reminders (1): #2: buy milk") && some.Contains("remind done"), "pending reminders are still listed");
            return $"friendwatch selftest: {pass} PASS, {fail} FAIL (pure; nothing sent)\n" + sb.ToString().TrimEnd();
        }
        if (sub == "simulate")
        {
            if (!LoggedIn) return "not logged in (the wake only fires while I'm logged in)";
            DavidCameOnline("simulated", true);
            return $"simulated {DavidName} login: david_login_test wake in {GreetDelay.TotalSeconds:F0} s (see the log: [friendwatch] / [webhook])";
        }
        DateTime last; lock (fwGate) last = lastWake.GetValueOrDefault("david_login", DateTime.MinValue);
        return $"friendwatch: {DavidName} {(davidOnline ? "online" : "offline")}{(davidOnlineAt > DateTime.MinValue ? $" (online since {davidOnlineAt.ToLocalTime():HH:mm:ss} PT)" : "")}; " +
               $"last david_login wake {(last == DateTime.MinValue ? "never" : last.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + " PT")}; poll {(friendPollRunning == 1 ? "on" : "off")}; " +
               $"{Reminders().Count(r => r.pending)} pending reminder(s)";
    }
}
