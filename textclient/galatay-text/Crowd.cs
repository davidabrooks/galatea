// Crowd.cs (2026-10-05 18:20 PT, David: "see large groups" at Warehouse 21).
// - Interest list: SL's default interest list only streams what is in the camera frustum, so a headless client in a crowd
//   got the avatars but almost none of their attachment prims (Warehouse 21: 8 of 52 avatars after 6 min). The old
//   Set360() ran at SimChanged, before the new region's caps existed, so the 360 POST silently went nowhere after a
//   teleport. Now: per region, wait for the InterestList cap, POST 360, verify, log [interest], retry; 'interest' shows it.
// - Coarse locations: 'avatars' also counts avatars the sim reports on the map but has not streamed yet, so she never says
//   "no avatars" at a busy spot just after arriving (the Sunday-night public-spot reports).
// - 'crowd': per avatar, attachments the sim lists (AvatarAppearance) vs attachment roots actually received.
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using LibreMetaverse;
using LibreMetaverse.Messages.Linden;

namespace GalatayText;

public static partial class Program
{
    // ---- interest list ----------------------------------------------------------------------------------------------
    static readonly ConcurrentDictionary<ulong, (string region, bool ok, string how, DateTime t)> interestState = new();
    static int interestGen;

    // pure: retry delays for the per-region 360 POST (cap not there yet / POST failed). Total ~60 s, then give up.
    internal static IReadOnlyList<int> InterestRetryDelaysMs() => new[] { 500, 1000, 2000, 3000, 5000, 8000, 10000, 15000, 15000 };

    static async Task Ensure360ForCurrentRegion(string why)
    {
        int gen = Interlocked.Increment(ref interestGen);
        var sim = client.Network.CurrentSim; if (sim == null) return;
        string last = "no InterestList cap yet";
        foreach (var d in InterestRetryDelaysMs())
        {
            if (gen != interestGen || client.Network.CurrentSim != sim) return;   // a newer region change took over
            if (sim.Caps?.CapabilityURI("InterestList") != null)
            {
                bool ok = false;
                try { ok = await client.InterestList.SetModeOnSimAsync(sim, InterestListMode.Panoramic360); last = ok ? "POST 360 ok" : "POST 360 failed"; }
                catch (Exception ex) { last = "POST 360 error: " + ex.GetBaseException().Message; }
                if (ok)
                {
                    interestState[sim.Handle] = (sim.Name, true, last, DateTime.Now);
                    Log("interest", $"360 on {sim.Name} ({why}): ok");
                    return;
                }
            }
            await Task.Delay(d);
        }
        interestState[sim.Handle] = (sim.Name, false, last, DateTime.Now);
        Log("interest", $"360 on {sim.Name} ({why}): FAILED after retries ({last}); objects behind the camera may not stream");
    }

    static async Task<string> InterestCmd(string[] a)
    {
        var sub = a.Length > 0 ? a[0].ToLowerInvariant() : "status";
        var sim = client.Network.CurrentSim;
        if (sub == "360" || sub == "default")
        {
            if (sim == null) return "not connected";
            var mode = sub == "360" ? InterestListMode.Panoramic360 : InterestListMode.Default;
            bool ok = await client.InterestList.SetModeOnSimAsync(sim, mode);
            if (sub == "360") interestState[sim.Handle] = (sim.Name, ok, ok ? "POST 360 ok (manual)" : "POST 360 failed (manual)", DateTime.Now);
            return $"interest list {sub} on {sim.Name}: {(ok ? "ok" : "FAILED")}";
        }
        var sb = new StringBuilder();
        sb.AppendLine($"client mode: {client.InterestList.CurrentMode}; current region {sim?.Name ?? "-"} cap {(sim?.Caps?.CapabilityURI("InterestList") != null ? "present" : "missing")}");
        foreach (var kv in interestState.Values.OrderByDescending(v => v.t))
            sb.AppendLine($"  {kv.region}: {(kv.ok ? "360 ok" : "NOT 360")} ({kv.how}) at {kv.t:HH:mm:ss} PT");
        return sb.ToString().TrimEnd();
    }

    static string ThrottleCmd(string[] a)
    {
        var t = client.Throttle;
        if (a.Length >= 2 && a[0].Equals("task", StringComparison.OrdinalIgnoreCase) && F(a[1], out var kbps) && kbps >= 100 && kbps <= 1338)
        { t.Task = kbps * 1000f; t.Set(); Log("throttle", $"task -> {t.Task / 1000:F0} kbps (sent AgentThrottle)"); }
        else if (a.Length > 0) return "usage: throttle [task <100-1338 kbps>]";
        return $"throttle kbps: task {t.Task / 1000:F0}, land {t.Land / 1000:F0}, texture {t.Texture / 1000:F0}, asset {t.Asset / 1000:F0}, resend {t.Resend / 1000:F0}, total {t.Total / 1000:F0}";
    }

    // ---- coarse locations (the region map's avatar list) --------------------------------------------------------------
    static volatile IReadOnlyDictionary<UUID, Vector3> coarseNow = new Dictionary<UUID, Vector3>();
    static Simulator coarseSim;
    static void HookCoarse()
    {
        client.Grid.CoarseLocationUpdate += (s, e) =>
        {
            if (e.Simulator != client.Network.CurrentSim) return;
            coarseSim = e.Simulator; coarseNow = e.Positions;
        };
        client.Network.SimChanged += (s, e) => { rawChildren.Clear(); coarseNow = new Dictionary<UUID, Vector3>(); };
    }

    // pure: avatars on the coarse map that are not streamed as objects yet (and not me), nearest first.
    // Coarse Z is 4 m steps capped at 1020 (Z byte 255 = "above 1020 m"): compare horizontally when either Z is unknown.
    internal static List<(UUID id, float dist)> CoarseOnly(IReadOnlyDictionary<UUID, Vector3> coarse, ISet<UUID> streamed, UUID me, Vector3 myPos)
    {
        var list = new List<(UUID, float)>();
        foreach (var (id, p) in coarse)
        {
            if (id == me || streamed.Contains(id)) continue;
            bool zUnknown = p.Z >= 1020f || myPos.Z >= 1020f;
            var d = zUnknown ? Vector2.Distance(new Vector2(p.X, p.Y), new Vector2(myPos.X, myPos.Y)) : Vector3.Distance(p, myPos);
            list.Add((id, d));
        }
        return list.OrderBy(x => x.Item2).ToList();
    }

    static string CoarseOnlyLines(int max = 12)
    {
        var sim = client.Network.CurrentSim;
        if (sim == null || coarseSim != sim) return "";
        var streamed = sim.ObjectsAvatars.Values.Where(a => a != null).Select(a => a.ID).ToHashSet();
        var only = CoarseOnly(coarseNow, streamed, client.Self.AgentID, client.Self.SimPosition);
        if (only.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine($"+ {only.Count} more avatar(s) in the region on the map (coarse position only; not streamed to me yet, or beyond draw distance):");
        foreach (var (id, d) in only.Take(max)) sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  ~{0,5:F0}m  {1}  {2}", d, id, NameOf(id)));
        if (only.Count > max) sb.AppendLine($"  ... and {only.Count - max} more");
        return sb.ToString();
    }

    // ---- crowd completeness ----------------------------------------------------------------------------------------------
    // pure: an avatar's attachment state from the sim's own list (expected) and the roots we hold (have).
    internal static string AttachState(int expected, int have) =>
        have == 0 ? (expected == 0 ? "unknown" : "bare") : expected > 0 && have < expected ? "partial" : "complete";

    static Dictionary<uint, int> AttachRootsByAvatar(Simulator sim)
    {
        var avIds = sim.ObjectsAvatars.Values.Where(a => a != null).Select(a => a.LocalID).ToHashSet();
        var d = new Dictionary<uint, int>();
        foreach (var p in sim.ObjectsPrimitives.Values)
            if (p != null && avIds.Contains(p.ParentID)) d[p.ParentID] = d.GetValueOrDefault(p.ParentID) + 1;
        return d;
    }

    static string CrowdCmd(string[] a)
    {
        float r = 40; if (a.Length > 0 && !F(a[0], out r)) return "usage: crowd [radius]";
        var sim = Sim; var me = client.Self.SimPosition;
        var roots = AttachRootsByAvatar(sim);
        var near = Avatars().Where(t => t.dist >= 0 && t.dist <= r).ToList();
        var sb = new StringBuilder(); var counts = new Dictionary<string, int>();
        foreach (var (av, pos, d) in near)
        {
            int exp; lock (av) exp = av.Attachments?.Count ?? 0;
            int have = roots.GetValueOrDefault(av.LocalID);
            int raw = rawChildren.TryGetValue(av.LocalID, out var rk) ? rk.Count : 0;
            var st = AttachState(exp, have); counts[st] = counts.GetValueOrDefault(st) + 1;
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0,6:F1}m  {1,-28} attachments {2,3}/{3,-3} {4}{5}", d, av.Name, have, exp, st, raw != have ? $" (raw updates seen: {raw})" : ""));
        }
        var head = $"crowd within {r:F0} m: {near.Count} avatar(s) streamed ({sim.ObjectsAvatars.Count} in region objects, {coarseNow.Count} on the map); " +
                   string.Join(", ", counts.OrderBy(k => k.Key).Select(k => $"{k.Value} {k.Key}")) +
                   $"; objects={sim.ObjectsPrimitives.Count}; interest={(interestState.TryGetValue(sim.Handle, out var s) && s.ok ? "360" : "NOT 360")}";
        return head + "\n" + sb.ToString().TrimEnd();
    }

    internal static string CrowdSelfTest()
    {
        var sb = new StringBuilder(); int pass = 0, fail = 0;
        void C(bool ok, string w) { if (ok) pass++; else fail++; sb.AppendLine($"{(ok ? "PASS" : "FAIL")} {w}"); }
        var me = UUID.Random(); var a = UUID.Random(); var b = UUID.Random(); var c = UUID.Random();
        var coarse = new Dictionary<UUID, Vector3> { [me] = new(10, 10, 20), [a] = new(12, 10, 20), [b] = new(50, 10, 20), [c] = new(11, 10, 1020) };
        var only = CoarseOnly(coarse, new HashSet<UUID> { a }, me, new Vector3(10, 10, 2004));
        C(only.Count == 2 && !only.Any(x => x.id == me || x.id == a), "coarse-only skips me and streamed avatars");
        C(only[0].id == c && Math.Abs(only[0].dist - 1f) < 0.01f, "Z 1020 (skybox, unknown height) compares horizontally");
        var o2 = CoarseOnly(new Dictionary<UUID, Vector3> { [a] = new(40, 10, 20), [b] = new(12, 10, 20) }, new HashSet<UUID>(), me, new Vector3(10, 10, 20));
        C(o2[0].id == b && o2[1].id == a, "nearest first");
        C(AttachState(0, 0) == "unknown" && AttachState(12, 0) == "bare" && AttachState(12, 5) == "partial" && AttachState(12, 12) == "complete" && AttachState(0, 3) == "complete",
          "attachment state: unknown / bare / partial / complete");
        var dl = InterestRetryDelaysMs();
        C(dl.Count >= 5 && dl.Sum() >= 30000 && dl.Sum() <= 90000 && dl.Zip(dl.Skip(1)).All(p => p.Second >= p.First), "360 retry: growing delays, 30-90 s total");
        C(LookAttachWaitDone(Enumerable.Repeat(2, 10).ToArray(), 52, 5000, 500, 25000) == "stalled" && LookAttachWaitDone(new[] { 2, 8, 20 }, 52, 1500, 500, 25000) == null
          && LookAttachWaitDone(new[] { 52 }, 52, 500, 500, 25000) == "complete" && LookAttachWaitDone(new[] { 5, 9 }, 52, 25000, 500, 25000) == "timeout",
          "look attachment wait: complete / stalled (no progress 4 s) / timeout / keep waiting while it grows");
        return $"crowd selftest: {pass} PASS, {fail} FAIL\n" + sb.ToString().TrimEnd();
    }

    // pure: should the pre-look attachment wait stop? samples = avatars-with-attachments count per tick (oldest first).
    // Stop when everyone has some, when there was no progress for 4 s, or at the hard budget. null = keep waiting.
    internal static string LookAttachWaitDone(IReadOnlyList<int> samples, int total, int elapsedMs, int tickMs, int budgetMs)
    {
        if (samples.Count > 0 && samples[^1] >= total) return "complete";
        if (elapsedMs >= budgetMs) return "timeout";
        int window = Math.Max(1, 4000 / Math.Max(1, tickMs));
        if (samples.Count > window && samples[^1] <= samples[^(window + 1)]) return "stalled";
        return null;
    }
}
