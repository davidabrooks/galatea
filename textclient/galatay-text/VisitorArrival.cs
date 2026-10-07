// VisitorArrival.cs (2026-10-07, David): "greet and engage visitors in chat when they first arrive at our home".
// - While logged in on the home region (Peronaut), every 5 s look at the avatars in the sim. An avatar that is not David
//   and not me, seen for the first time this session, and not woken for in the last 24 h (run/visitor-arrivals.json,
//   UUID keys), fires ONE webhook event kind 'visitor_arrival' (from = their name, from_id = their UUID, distance,
//   context = already-greeted note) so the chat routine greets them in nearby chat and keeps a friendly conversation going.
// - No duplicate hello: if wander already greeted them (greet history, 24 h) the event says so (routine: no second hi,
//   just engage). Once the event fires they are marked greeted, so a wander loop will not stranger-greet them after the routine.
// - Avatars already there in the first 20 s after my login count too (first sighting this session), cooldown permitting.
// - 'visitors [status|selftest]'.
using System.Globalization;
using System.Text.Json;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    const string VisitorHomeRegion = "Peronaut";
    static readonly string VisitorStateFile = Env("GT_VISITOR_STATE", "/home/box/viewers/textclient/run/visitor-arrivals.json");
    static readonly TimeSpan VisitorCooldown = TimeSpan.FromHours(24);
    static readonly Dictionary<UUID, DateTime> visitorWoken = new();   // UUID -> last wake (local)
    static readonly HashSet<UUID> visitorSeenSession = new();
    static int visitorLoopRunning, visitorLoaded; static int visitorGen;

    // pure: should this sighting wake the routine?
    public static bool ShouldWakeVisitor(UUID id, UUID self, UUID david, string region, bool seenThisSession,
        DateTime? lastWoken, DateTime now) =>
        id != UUID.Zero && id != self && id != david
        && string.Equals(region, VisitorHomeRegion, StringComparison.OrdinalIgnoreCase)
        && !seenThisSession
        && (lastWoken is null || now - lastWoken.Value >= VisitorCooldown);

    // pure: event text for the routine
    public static string VisitorArrivalText(string name, double? dist, bool alreadyGreeted) =>
        $"Visitor arrival at home ({VisitorHomeRegion}): {name} just arrived" + (dist is double d ? $", {d:F0} m from me" : "") + ". " +
        (alreadyGreeted ? "My wander already said hi to them recently: do NOT say hi again, just engage warmly in nearby chat."
                        : "Greet them warmly in nearby chat (say), introduce myself, and chat with them in a friendly way.");

    static void VisitorWatchStart()
    {
        lock (visitorSeenSession) visitorSeenSession.Clear();
        Interlocked.Increment(ref visitorGen);
        if (Interlocked.Exchange(ref visitorLoaded, 1) == 0)
            try { var st = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(VisitorStateFile));
                  lock (visitorWoken) foreach (var kv in st) if (UUID.TryParse(kv.Key, out var u)) visitorWoken[u] = kv.Value; } catch { }
        if (Interlocked.Exchange(ref visitorLoopRunning, 1) == 0) _ = Task.Run(VisitorLoop);
    }

    static async Task VisitorLoop()
    {
        try
        {
            while (!shuttingDown)
            {
                await Task.Delay(5000);
                try { VisitorTick(); } catch (Exception ex) { Log("visitors", "tick error: " + ex.GetType().Name); }
            }
        }
        finally { visitorLoopRunning = 0; }
    }

    static void VisitorTick()
    {
        if (!LoggedIn) return;
        var sim = client.Network.CurrentSim; if (sim == null) return;
        if (!string.Equals(sim.Name, VisitorHomeRegion, StringComparison.OrdinalIgnoreCase)) return;
        var me = client.Self.AgentID; var myPos = client.Self.SimPosition;
        var ids = new List<UUID>();
        var avs = sim.ObjectsAvatars.Values.Where(x => x != null && x.ID != UUID.Zero).ToList();
        foreach (var x in avs) if (!ids.Contains(x.ID)) ids.Add(x.ID);
        foreach (var id in ids)
        {
            bool seen; lock (visitorSeenSession) seen = visitorSeenSession.Contains(id);
            DateTime? last; lock (visitorWoken) last = visitorWoken.TryGetValue(id, out var t) ? t : null;
            var now = DateTime.Now;
            if (!ShouldWakeVisitor(id, me, DavidId, sim.Name, seen, last, now))
            { if (id != me) lock (visitorSeenSession) visitorSeenSession.Add(id); continue; }
            var av = avs.FirstOrDefault(x => x.ID == id);
            var name = av?.Name; if (string.IsNullOrWhiteSpace(name)) { var n = NameOf(id); name = n == id.ToString() ? null : n; }
            if (name == null) { try { client.Avatars.RequestAvatarName(id); } catch { } continue; } // retry next tick once the name is known
            double? dist = null; if (av != null && av.ParentID == 0) dist = Math.Round(HDist(av.Position, myPos), 1);
            bool greeted; lock (wGreeted) greeted = GreetedRecently(wGreeted, id, now, GreetRepeatHours > 0 ? GreetRepeatHours : 24);
            lock (visitorSeenSession) visitorSeenSession.Add(id);
            lock (visitorWoken) { visitorWoken[id] = now; try { File.WriteAllText(VisitorStateFile, JsonSerializer.Serialize(visitorWoken.ToDictionary(k => k.Key.ToString(), k => k.Value))); } catch { } }
            NoteGreeted(id, now); // wander won't stranger-greet them on top of the routine
            Log("visitors", $"visitor_arrival: {name} ({id}) at {dist?.ToString(CultureInfo.InvariantCulture) ?? "?"} m; already greeted={greeted}; waking the chat routine");
            Notify("visitor_arrival", name, id, VisitorArrivalText(name, dist, greeted), dist, context: greeted ? "already_greeted_by_wander" : "new_visitor");
        }
    }

    static string VisitorsCmd(string[] a)
    {
        if (a.Length > 0 && a[0] == "selftest") return VisitorSelftest();
        int n; lock (visitorWoken) n = visitorWoken.Count; int s; lock (visitorSeenSession) s = visitorSeenSession.Count;
        return $"visitors: home {VisitorHomeRegion}, cooldown {VisitorCooldown.TotalHours:F0} h per visitor; seen this session {s}; woken history {n}; loop {(visitorLoopRunning == 1 ? "on" : "off")}";
    }

    static string VisitorSelftest()
    {
        var self = new UUID("11111111-1111-1111-1111-111111111111"); var v = new UUID("22222222-2222-2222-2222-222222222222");
        var now = new DateTime(2026, 10, 7, 12, 0, 0); int pass = 0, fail = 0;
        void C(bool ok) { if (ok) pass++; else fail++; }
        C(ShouldWakeVisitor(v, self, DavidId, "Peronaut", false, null, now));
        C(!ShouldWakeVisitor(DavidId, self, DavidId, "Peronaut", false, null, now));
        C(!ShouldWakeVisitor(self, self, DavidId, "Peronaut", false, null, now));
        C(!ShouldWakeVisitor(v, self, DavidId, "naberrie", false, null, now));
        C(!ShouldWakeVisitor(v, self, DavidId, "Peronaut", true, null, now));
        C(!ShouldWakeVisitor(v, self, DavidId, "Peronaut", false, now.AddHours(-23), now));
        C(ShouldWakeVisitor(v, self, DavidId, "Peronaut", false, now.AddHours(-25), now));
        return $"visitors selftest: {pass} PASS, {fail} FAIL";
    }
}
