// RouteNav (added 2026-09-25): named routes, path following, route recording and overhead pictures for Galatay.
// Routes: textclient/routes/<name>.json {name, region, source, created, note, points:[[x,y,z],...]}
// Graph:  textclient/routes/_graph-<Region>.json {nodes:[[x,y,z]], edges:[[a,b,kind]], places:{name:{node,note}}}
// Commands: route list | route show <name> | route walk <name> [reverse] [allow_zendo] [allow_outside] | route status
//           route record <avatar> <name> | route record stop | route stop | goto_place <place> [nosit] [allow_zendo] [allow_outside]
//           overhead [tag] (alias snapshot) -> /workspace/secondlife/images/overhead-<time>[-tag].png
// Walking: server autopilot re-aimed every 0.4 s at a carrot 2.5 m ahead on the polyline (no stop at waypoints).
// Doors (2026-10-05): before a known nav-grid door on the path, touch it (and same-axis siblings / double doors) and
// walk straight through without a long pause — these auto-close quickly. Stuck near a door: DoorUnstick first, then
// the usual sidestep recoveries. Same FollowPoly path is used by route walk and goto_place.
// Bounds: every point checked (2 m steps) against the zendo footprint (+2 m) and the parcel (Naberrie: The Buddha Center;
// elsewhere: the parcel she starts in) unless allow_zendo / allow_outside. Stuck: sidestep max 1 m / back off 1.2 m, no flying,
// after 4 failed recoveries she stops and logs. People: avatar within the next 3 m of her lane -> pause up to 10 s, then
// sidestep 1 m if that lane is clear, else wait (30 s total) then stop and log. Never walks through anyone.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    static readonly string RouteDir = Env("GT_ROUTE_DIR", "/home/box/viewers/textclient/routes");
    static readonly string ImgDir = Env("GT_IMG_DIR", "/workspace/secondlife/images");
    static readonly string OverheadPy = Env("GT_OVERHEAD_PY", "/workspace/secondlife/scripts/overhead.py");
    static readonly string PyBin = Env("GT_PY", "/workspace/secondlife/.venv/bin/python");
    static readonly CultureInfo IC = CultureInfo.InvariantCulture;

    // ---- polyline ---------------------------------------------------------------------------------
    sealed class Poly
    {
        public readonly List<Vector3> P; public readonly float[] C; public readonly float Len;
        public Poly(List<Vector3> pts)
        {
            P = new List<Vector3>();
            foreach (var p in pts) if (P.Count == 0 || HDist(P[^1], p) > 0.05f) P.Add(p);
            if (P.Count == 1) P.Add(P[0] + new Vector3(0.01f, 0, 0));
            C = new float[P.Count];
            for (int i = 1; i < P.Count; i++) C[i] = C[i - 1] + HDist(P[i - 1], P[i]);
            Len = C[^1];
        }
        int Seg(float s) { for (int i = 0; i < P.Count - 2; i++) if (s < C[i + 1]) return i; return P.Count - 2; }
        public Vector3 At(float s)
        {
            s = Math.Clamp(s, 0, Len); int i = Seg(s); float l = C[i + 1] - C[i];
            return Vector3.Lerp(P[i], P[i + 1], l < 1e-4f ? 0 : (s - C[i]) / l);
        }
        public Vector3 Dir(float s)
        {
            int i = Seg(Math.Clamp(s, 0, Len)); var d = new Vector3(P[i + 1].X - P[i].X, P[i + 1].Y - P[i].Y, 0);
            return d.Length() < 1e-4f ? Vector3.UnitX : Vector3.Normalize(d);
        }
        // nearest point with s in [s0,s1]; lat > 0 = left of travel direction
        public (float s, float dist, float lat) Project(Vector3 p, float s0, float s1)
        {
            s0 = Math.Clamp(s0, 0, Len); s1 = Math.Clamp(s1, s0, Len);
            (float s, float dist, float lat) best = (s0, float.MaxValue, 0);
            for (int i = 0; i < P.Count - 1; i++)
            {
                if (C[i + 1] < s0 || C[i] > s1) continue;
                var a = P[i]; var d = new Vector3(P[i + 1].X - a.X, P[i + 1].Y - a.Y, 0); float l = C[i + 1] - C[i];
                float t = l < 1e-4f ? 0 : ((p.X - a.X) * d.X + (p.Y - a.Y) * d.Y) / (l * l);
                float sLo = (Math.Max(s0, C[i]) - C[i]) / Math.Max(l, 1e-4f), sHi = (Math.Min(s1, C[i + 1]) - C[i]) / Math.Max(l, 1e-4f);
                t = Math.Clamp(t, sLo, sHi);
                var q = new Vector3(a.X + d.X * t, a.Y + d.Y * t, 0);
                float dist = MathF.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y));
                if (dist < best.dist - 1e-3f)
                {
                    var n = l < 1e-4f ? Vector3.Zero : new Vector3(-d.Y / l, d.X / l, 0);
                    best = (C[i] + t * l, dist, (p.X - q.X) * n.X + (p.Y - q.Y) * n.Y);
                }
            }
            return best;
        }
        // sub-polyline from s to the end, starting at the point At(s)
        public List<Vector3> From(float s) { var l = new List<Vector3> { At(s) }; for (int i = 0; i < P.Count; i++) if (C[i] > s + 0.05f) l.Add(P[i]); return l; }
    }

    sealed class RouteOpts { public bool AllowZendo, AllowOutside, SitAtEnd, Idle; public string Label = ""; public string Place; }

    // live state (read by route status / overhead)
    static volatile Poly curRoute; static volatile string curRouteName; static float curS; static volatile string routeState = "idle";
    static readonly List<string> snapshotsThisWalk = new();

    static bool InNaberrie => string.Equals(client.Network.CurrentSim?.Name, HomeSeatRegion, StringComparison.OrdinalIgnoreCase);
    static bool ZendoHit(Vector3 p) => InNaberrie && InZendo(p);
    static string P3(Vector3 v) => string.Format(IC, "{0:F1},{1:F1},{2:F1}", v.X, v.Y, v.Z);
    static void RLogR(string m) => Log("route", m);

    // ---- files ------------------------------------------------------------------------------------
    static string RoutePath(string name) => Path.Combine(RouteDir, name + ".json");
    static readonly Regex RouteName = new("^[a-z0-9][a-z0-9_-]{0,40}$", RegexOptions.Compiled);
    static (List<Vector3> pts, string region, string note, string source) LoadRoute(string name)
    {
        var j = JsonNode.Parse(File.ReadAllText(RoutePath(name)))!;
        var pts = j["points"]!.AsArray().Select(p => new Vector3((float)p![0]!.GetValue<double>(), (float)p[1]!.GetValue<double>(), (float)p[2]!.GetValue<double>())).ToList();
        return (pts, (string)j["region"], (string)j["note"] ?? "", (string)j["source"] ?? "");
    }
    static void SaveRoute(string name, List<Vector3> pts, string source, string note)
    {
        Directory.CreateDirectory(RouteDir);
        var o = new JsonObject
        {
            ["name"] = name, ["region"] = client.Network.CurrentSim?.Name, ["source"] = source,
            ["created"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", IC), ["note"] = note,
            ["points"] = new JsonArray(pts.Select(p => (JsonNode)new JsonArray(Math.Round(p.X, 2), Math.Round(p.Y, 2), Math.Round(p.Z, 2))).ToArray()),
        };
        var tmp = RoutePath(name) + ".tmp"; File.WriteAllText(tmp, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); File.Move(tmp, RoutePath(name), true);
    }
    sealed class Graph { public List<Vector3> N = new(); public List<(int a, int b, string kind)> E = new(); public Dictionary<string, (int node, string note)> Places = new(StringComparer.OrdinalIgnoreCase); }
    static Graph LoadGraph(string region) => LoadGraphFile(Path.Combine(RouteDir, $"_graph-{region}.json"));
    static Graph LoadGraphFile(string f)
    {
        if (!File.Exists(f)) return null;
        var j = JsonNode.Parse(File.ReadAllText(f))!; var g = new Graph();
        foreach (var n in j["nodes"]!.AsArray()) g.N.Add(new Vector3((float)n![0]!.GetValue<double>(), (float)n[1]!.GetValue<double>(), (float)n[2]!.GetValue<double>()));
        foreach (var e in j["edges"]!.AsArray()) g.E.Add((e![0]!.GetValue<int>(), e[1]!.GetValue<int>(), (string)e[2] ?? ""));
        foreach (var kv in j["places"]!.AsObject()) g.Places[kv.Key] = (kv.Value!["node"]!.GetValue<int>(), (string)kv.Value["note"] ?? "");
        return g;
    }

    // pure: BFS node path over undirected edges (offline doorway checks).
    public static List<int> GraphNodePath(IReadOnlyList<(int a, int b, string kind)> edges, int from, int to)
    {
        var adj = new Dictionary<int, List<(int v, string k)>>();
        void add(int a, int b, string k) { if (!adj.TryGetValue(a, out var L)) adj[a] = L = new(); L.Add((b, k)); }
        foreach (var e in edges) { add(e.a, e.b, e.kind); add(e.b, e.a, e.kind); }
        if (!adj.ContainsKey(from)) return null;
        var prev = new Dictionary<int, int> { [from] = -1 };
        var q = new Queue<int>(); q.Enqueue(from);
        while (q.Count > 0)
        {
            var u = q.Dequeue();
            if (u == to) break;
            if (!adj.TryGetValue(u, out var nbrs)) continue;
            foreach (var (v, _) in nbrs) if (!prev.ContainsKey(v)) { prev[v] = u; q.Enqueue(v); }
        }
        if (!prev.ContainsKey(to)) return null;
        var path = new List<int>();
        for (int v = to; v != -1; v = prev[v]) path.Add(v);
        path.Reverse();
        return path;
    }

    // pure: Peronaut west/east wing seats must route through doorway-kind edges (2026-10-06 wall-cut fix).
    public static (bool ok, string detail) PeronautDoorwayGraphOk(
        IReadOnlyDictionary<string, int> places,
        IReadOnlyList<(int a, int b, string kind)> edges)
    {
        if (!places.TryGetValue("front", out var front) || !places.TryGetValue("bed", out var bed)
            || !places.TryGetValue("east-deck", out var eastDeck) || !places.TryGetValue("inside-west", out var insideWest)
            || !places.TryGetValue("inside-east", out var insideEast))
            return (false, "missing places front/bed/east-deck/inside-west/inside-east");
        bool hasDirectWest = edges.Any(e => (e.a == insideWest && e.b == bed) || (e.b == insideWest && e.a == bed));
        bool hasDirectEast = edges.Any(e => (e.a == insideEast && e.b == eastDeck) || (e.b == insideEast && e.a == eastDeck));
        if (hasDirectWest || hasDirectEast)
            return (false, $"wall-cutting direct edge still present (west={hasDirectWest}, east={hasDirectEast})");
        bool hasWestDoor = edges.Any(e => e.kind.Contains("west-doorway", StringComparison.OrdinalIgnoreCase));
        bool hasEastDoor = edges.Any(e => e.kind.Contains("east-doorway", StringComparison.OrdinalIgnoreCase));
        if (!hasWestDoor || !hasEastDoor)
            return (false, $"missing doorway edges (west-doorway={hasWestDoor}, east-doorway={hasEastDoor})");
        var toBed = GraphNodePath(edges, front, bed);
        if (toBed == null) return (false, "no path front -> bed");
        var kindsBed = new List<string>();
        for (int i = 0; i + 1 < toBed.Count; i++)
        {
            var a = toBed[i]; var b = toBed[i + 1];
            var e = edges.FirstOrDefault(x => (x.a == a && x.b == b) || (x.b == a && x.a == b));
            kindsBed.Add(e.kind ?? "");
        }
        if (!kindsBed.Any(k => k.Contains("west-doorway", StringComparison.OrdinalIgnoreCase)))
            return (false, "front->bed path misses west-doorway: " + string.Join(" > ", kindsBed));
        var toEast = GraphNodePath(edges, bed, eastDeck);
        if (toEast == null) return (false, "no path bed -> east-deck");
        var kindsEast = new List<string>();
        for (int i = 0; i + 1 < toEast.Count; i++)
        {
            var a = toEast[i]; var b = toEast[i + 1];
            var e = edges.FirstOrDefault(x => (x.a == a && x.b == b) || (x.b == a && x.a == b));
            kindsEast.Add(e.kind ?? "");
        }
        if (!kindsEast.Any(k => k.Contains("west-doorway", StringComparison.OrdinalIgnoreCase))
            || !kindsEast.Any(k => k.Contains("east-doorway", StringComparison.OrdinalIgnoreCase)))
            return (false, "bed->east-deck must use both doorways: " + string.Join(" > ", kindsEast));
        return (true, $"front->bed: {string.Join(" > ", kindsBed)}; bed->east-deck: {string.Join(" > ", kindsEast)}");
    }

    public static (bool ok, string detail) PeronautDoorwaySelfTest(string graphPath = null)
    {
        graphPath ??= Path.Combine(RouteDir, "_graph-Peronaut.json");
        if (!File.Exists(graphPath))
        {
            // repo / CI: prefer routes next to the built binary's sibling routes, then repo textclient/routes
            foreach (var cand in new[]
            {
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "routes", "_graph-Peronaut.json")),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "routes", "_graph-Peronaut.json")),
                "/workspace/galatea-sl-repo/textclient/routes/_graph-Peronaut.json",
            })
                if (File.Exists(cand)) { graphPath = cand; break; }
        }
        if (!File.Exists(graphPath)) return (false, "no _graph-Peronaut.json at " + graphPath);
        var g = LoadGraphFile(graphPath);
        if (g == null) return (false, "failed to load " + graphPath);
        var places = g.Places.ToDictionary(kv => kv.Key, kv => kv.Value.node, StringComparer.OrdinalIgnoreCase);
        return PeronautDoorwayGraphOk(places, g.E);
    }

    // shortest path over the graph from the nearest point on any edge to a place node
    static (List<Vector3> pts, string err) GraphRoute(Graph g, Vector3 from, int goal)
    {
        int bestE = -1; float bestD = float.MaxValue, bestT = 0;
        for (int k = 0; k < g.E.Count; k++)
        {
            var a = g.N[g.E[k].a]; var b = g.N[g.E[k].b]; var d = new Vector3(b.X - a.X, b.Y - a.Y, 0); float l2 = d.X * d.X + d.Y * d.Y;
            float t = l2 < 1e-6f ? 0 : Math.Clamp(((from.X - a.X) * d.X + (from.Y - a.Y) * d.Y) / l2, 0, 1);
            var q = Vector3.Lerp(a, b, t); float dist = GDist(q, from); // level-aware (stacked patio/beach)
            if (dist < bestD) { bestD = dist; bestE = k; bestT = t; }
        }
        if (bestE < 0) return (null, "graph has no edges");
        if (bestD > 25f) return (null, $"too far from the path network ({bestD:F0} m); walk closer first");
        int n = g.N.Count; var ea = g.E[bestE];
        var start = Vector3.Lerp(g.N[ea.a], g.N[ea.b], bestT);
        var adj = new List<(int v, float w)>[n + 1];
        for (int i = 0; i <= n; i++) adj[i] = new();
        foreach (var e in g.E) { float w = HDist(g.N[e.a], g.N[e.b]); adj[e.a].Add((e.b, w)); adj[e.b].Add((e.a, w)); }
        adj[n].Add((ea.a, HDist(start, g.N[ea.a]))); adj[n].Add((ea.b, HDist(start, g.N[ea.b])));
        var dist2 = Enumerable.Repeat(float.MaxValue, n + 1).ToArray(); var prev = Enumerable.Repeat(-1, n + 1).ToArray();
        var pq = new PriorityQueue<int, float>(); dist2[n] = 0; pq.Enqueue(n, 0);
        while (pq.TryDequeue(out var u, out var du))
        {
            if (du > dist2[u]) continue; if (u == goal) break;
            foreach (var (v, w) in adj[u]) if (du + w < dist2[v]) { dist2[v] = du + w; prev[v] = u; pq.Enqueue(v, dist2[v]); }
        }
        if (dist2[goal] == float.MaxValue) return (null, "no route over the path network");
        var seq = new List<int>(); for (int v = goal; v != -1; v = prev[v]) seq.Add(v); seq.Reverse();
        var pts = new List<Vector3>();
        if (bestD > 0.7f) pts.Add(from);
        foreach (var v in seq) pts.Add(v == n ? start : g.N[v]);
        return (pts, null);
    }

    // ---- bounds -------------------------------------------------------------------------------------
    static async Task<UUID> AllowedParcel(Simulator sim)
    {
        if (InNaberrie) return BuddhaCenterParcel;
        return await ParcelAt(sim, client.Self.SimPosition);
    }
    static async Task<string> CheckBounds(Poly poly, RouteOpts o)
    {
        var sim = Sim;
        var samples = new List<Vector3>();
        for (float s = 0; s < poly.Len; s += 2f) samples.Add(poly.At(s));
        samples.Add(poly.At(poly.Len));
        // the first 3 m may be inside the margin (e.g. she stopped at the zendo edge node): walking OUT is fine
        if (!o.AllowZendo) { var z = samples.Where((p, i) => i * 2f >= 3f || i == samples.Count - 1).FirstOrDefault(ZendoHit); if (z != Vector3.Zero) return $"route enters the zendo footprint near {P3(z)} (use allow_zendo only if asked)"; }
        if (o.AllowOutside) return null;
        var allowed = await AllowedParcel(sim);
        if (allowed == UUID.Zero) return "could not look up the allowed parcel";
        var cells = samples.GroupBy(p => ((int)(p.X / 4), (int)(p.Y / 4))).Select(gr => gr.First()).ToList();
        using var gate = new SemaphoreSlim(6);
        var res = await Task.WhenAll(cells.Select(async p => { await gate.WaitAsync(); try { return (p, id: await ParcelAt(sim, p)); } finally { gate.Release(); } }));
        var bad = res.Where(r => !ParcelOk(r.id, allowed)).ToList();
        if (bad.Count > 0)
            return $"route leaves the allowed parcel near {P3(bad[0].p)} (parcel {(bad[0].id == UUID.Zero ? "lookup failed" : bad[0].id.ToString())}; {bad.Count} of {res.Length} cells; use allow_outside only if asked)";
        return null;
    }
    static async Task<bool> PointAllowed(Vector3 p, RouteOpts o)
    {
        if (!o.AllowZendo && ZendoHit(p)) return false;
        if (o.AllowOutside) return true;
        var sim = Sim; var allowed = await AllowedParcel(sim);
        return ParcelOk(await ParcelAt(sim, p), allowed);
    }

    // ---- people -------------------------------------------------------------------------------------
    sealed record Block(string name, float s, float lat, float dist);
    static UUID blockIgnore; // avatar the follower may walk up to (wander approach)
    static Block Blocker(Poly poly, float s, float lane, Vector3 me)
    {
        var dir = poly.Dir(s);
        foreach (var (av, pos, dist) in Avatars())
        {
            if (dist < 0 || dist > 9f || Math.Abs(pos.Z - me.Z) > 2.5f || av.ID == blockIgnore) continue;
            var pj = poly.Project(pos, s - 0.5f, s + 3.2f);
            if (pj.s >= s - 0.3f && pj.s <= s + 3.0f && pj.dist <= 2.5f && Math.Abs(pj.lat - lane) < 1.0f) return new Block(av.Name, pj.s, pj.lat, dist);
            var rel = new Vector3(pos.X - me.X, pos.Y - me.Y, 0);
            if (rel.Length() < 1.0f && rel.X * dir.X + rel.Y * dir.Y > 0) return new Block(av.Name, s + rel.Length(), pj.lat, dist);
        }
        return null;
    }
    static async Task<bool> LaneClear(Poly poly, float s, float lane, RouteOpts o)
    {
        for (float k = 0.5f; k <= 4f; k += 0.7f)
        {
            float ss = Math.Min(poly.Len, s + k); var d = poly.Dir(ss); var p = poly.At(ss) + new Vector3(-d.Y, d.X, 0) * lane;
            if (!await PointAllowed(p, o)) return false;
            foreach (var (av, pos, dist) in Avatars()) if (dist >= 0 && av.ID != blockIgnore && HDist(pos, p) < 0.9f && Math.Abs(pos.Z - p.Z) < 2.5f) return false;
        }
        return true;
    }

    // ---- steering (2026-09-25 22:xx smooth mode) -----------------------------------------------------
    // legacy: autopilot re-aimed every 0.4 s at a carrot 2.5 m ahead; client AgentUpdates (every 0.5 s, LibreMetaverse timer,
    //         duplicate check disabled) kept sending a STALE BodyRotation (last TurnToward) -> the sim snapped her heading back
    //         and forth while the autopilot turned her toward the carrot = visible zigzag.
    // smooth: pure pursuit, lookahead 3.5 m (extended up to 8 m while the path ahead is straight within 0.5 m); the autopilot target
    //         is re-sent only when the bearing error > 9 deg (min 0.6 s apart), off-path > 1.2 m with error > 4 deg, the old target
    //         is < 2.5 m away, or the mode changes (lane/drift/resume). Every re-aim also sets BodyRotation toward the same target,
    //         so the periodic AgentUpdates agree with the autopilot.
    static volatile bool steerLegacy = Env("GT_STEER", "smooth") == "legacy";
    const float SteerDeadbandDeg = 9f, SteerMinInterval = 0.6f, SteerLookBase = 3.5f, SteerLookMax = 8f;
    static string lastSteerStats = "-";
    static float YawDeg(Quaternion q) => (float)(Math.Atan2(2.0 * (q.W * q.Z + q.X * q.Y), 1.0 - 2.0 * (q.Y * q.Y + q.Z * q.Z)) * 180.0 / Math.PI);
    static float BearingDeg(Vector3 from, Vector3 to) => (float)(Math.Atan2(to.Y - from.Y, to.X - from.X) * 180.0 / Math.PI);
    static float AngDiff(float a, float b) { var d = Math.Abs(a - b) % 360f; return d > 180f ? 360f - d : d; }
    static float SegDist(Vector3 p, Vector3 a, Vector3 b)
    {
        var d = new Vector2(b.X - a.X, b.Y - a.Y); float l2 = d.LengthSquared();
        float t = l2 < 1e-6f ? 0 : Math.Clamp(((p.X - a.X) * d.X + (p.Y - a.Y) * d.Y) / l2, 0, 1);
        return new Vector2(a.X + d.X * t - p.X, a.Y + d.Y * t - p.Y).Length();
    }
    static float SteerLook(Poly poly, float s)
    {
        float L = SteerLookBase; var p0 = poly.At(s);
        for (float LL = SteerLookBase + 1f; LL <= SteerLookMax && s + LL <= poly.Len; LL += 1f)
        {
            var q = poly.At(s + LL); bool straight = true;
            for (float k = 0.5f; k < LL; k += 0.5f) if (SegDist(poly.At(s + k), p0, q) > 0.5f) { straight = false; break; }
            if (!straight) break; L = LL;
        }
        return L;
    }

    // ---- the follower -------------------------------------------------------------------------------
    static async Task<(bool ok, string msg)> FollowPoly(Poly poly, RouteOpts o, CancellationToken ct)
    {
        await EnsureStandingForWalk(ct);
        var aoRefuse = await AoGuardBeforeWalk(o.Label, ct);
        if (aoRefuse != null) { RLogR($"{o.Label}: REFUSED - {aoRefuse}"); return (false, "stopped: " + aoRefuse); }
        DateTime? aoBadSince = null; bool aoRestoreTried = false;
        var region = client.Network.CurrentSim?.Name;
        curRoute = poly; curRouteName = o.Label; curS = 0;
        const float Look = 2.5f, EndTol = 1.0f;
        var t0 = DateTime.Now; var timeout = TimeSpan.FromSeconds(LegTimeoutS(poly.Len)); // 45 s + 1.5 s/m (was 60 + 2.5 s/m)
        float s = 0, lastProgS = 0, recovAtS = -99, lane = 0, laneUntil = -1, maxDev = 0; Vector3 maxDevAt = Vector3.Zero;
        DateTime lastProgT = DateTime.Now, lastDriftLog = DateTime.MinValue; DateTime? blockedSince = null, belowSince = null;
        int recov = 0, pauses = 0, sidesteps = 0, stuckEvents = 0; Block lastBlock = null; bool triedSide = false;
        var doorsOpened = new HashSet<UUID>(); bool doorSeqTried = false;
        double idleSecs = 0; float nextIdle = o.Idle ? 15f + (float)wRnd.NextDouble() * 30f : float.MaxValue; // wander: short natural pauses
        var start = client.Self.SimPosition;
        bool legacy = steerLegacy;
        RLogR($"{o.Label}: start at {P3(start)}, {poly.Len:F0} m, {poly.P.Count} points, end {P3(poly.At(poly.Len))}; steer {(legacy ? "legacy" : "smooth")}");
        // steering state + heading instrumentation (sampled every 100 ms from the sim's own-avatar rotation while moving > 0.8 m/s)
        Vector3? aim = null; DateTime aimAt = DateTime.MinValue; bool needAim = true, driftMode = false; float aimLane = 0;
        int aimSends = 0, yawSnaps = 0, yawSamples = 0; float maxYawStep = 0, yawWalked = 0;
        using var sampCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sampler = Task.Run(async () =>
        {
            float? prev = null; Vector3 prevPos = client.Self.SimPosition;
            try
            {
                while (!sampCts.IsCancellationRequested)
                {
                    await Task.Delay(100, sampCts.Token);
                    var v = client.Self.Velocity; var pos = client.Self.SimPosition;
                    if (new Vector2(v.X, v.Y).Length() < 0.8f) { prev = null; prevPos = pos; continue; }
                    var yaw = YawDeg(client.Self.RelativeRotation);
                    if (prev != null) { var dy = AngDiff(yaw, prev.Value); if (dy > 8f) yawSnaps++; if (dy > maxYawStep) maxYawStep = dy; yawSamples++; yawWalked += HDist(pos, prevPos); }
                    prev = yaw; prevPos = pos;
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        });
        string Steer() { float per = Math.Max(1f, s) / 10f, perW = Math.Max(1f, yawWalked) / 10f; return $"steer {(legacy ? "legacy" : "smooth")}: aim/heading updates {aimSends} ({aimSends / per:F1}/10 m), heading jumps >8deg/0.1s {yawSnaps} ({yawSnaps / perW:F1}/10 m walked, max {maxYawStep:F0} deg, {yawSamples} samples)"; }
        string Summary(string how) { lastSteerStats = $"{DateTime.Now:HH:mm:ss} {o.Label}: " + Steer() + $"; max off-path {maxDev:F1} m"; return $"{how} in {(DateTime.Now - t0).TotalSeconds:F0} s; {poly.Len:F0} m; max off-path {maxDev:F1} m (at {P3(maxDevAt)}); pauses {pauses}; sidesteps {sidesteps}; stuck recoveries {stuckEvents}; {Steer()}"; }
        try
        {
            while (true)
            {
                await Task.Delay(400, ct);
                var now = DateTime.Now; var me = client.Self.SimPosition;
                if (!string.Equals(client.Network.CurrentSim?.Name, region, StringComparison.OrdinalIgnoreCase)) { client.Self.AutoPilotCancel(); return (false, Summary($"stopped: left region {region}")); }
                if (client.Self.SittingOn != 0) { client.Self.AutoPilotCancel(); return (false, Summary("stopped: she is seated")); }
                // AO guard: never keep walking with the default animations
                if (!AoStateNow().active) { aoBadSince ??= now; } else aoBadSince = null;
                if (aoBadSince != null && (now - aoBadSince.Value).TotalSeconds >= 1.5)
                {
                    client.Self.AutoPilotCancel();
                    AoLog($"{o.Label}: AO DROPPED mid-walk at {P3(me)} ({AoStateNow().why}): stopped");
                    if (aoRestoreTried || !await AoRestore("dropped mid-walk", ct)) { var m = Summary($"stopped: AO not active mid-walk ({AoStateNow().why}); staying put"); RLogR($"{o.Label}: {m}"); return (false, m); }
                    aoRestoreTried = true; aoBadSince = null; needAim = true; lastProgT = DateTime.Now; lastProgS = s; idleSecs += 30; continue;
                }
                var pj = poly.Project(me, s - 2f, s + 6f);
                if (pj.dist > 4f) { var pa = poly.Project(me, 0, poly.Len); if (pa.dist < pj.dist - 1f && pa.s > s - 10f) pj = pa; }
                if (pj.s > s) s = pj.s;
                curS = s; if (pj.dist > maxDev) { maxDev = pj.dist; maxDevAt = me; }
                var end = poly.At(poly.Len);
                routeState = $"{o.Label}: {s:F0}/{poly.Len:F0} m at {P3(me)}, off-path {pj.dist:F1} m{(lane != 0 ? $", lane {lane:+0;-0}" : "")}{(blockedSince != null ? ", PAUSED for " + lastBlock?.name : "")}";
                if (poly.Len - s < 1.6f && HDist(me, end) <= EndTol)
                {
                    client.Self.AutoPilotCancel(); await Task.Delay(1200, ct);
                    var fin = client.Self.SimPosition;
                    var msg = Summary("arrived") + $"; final {P3(fin)}, {HDist(fin, end):F1} m from the end point (settled)";
                    RLogR($"{o.Label}: {msg}"); return (true, msg);
                }
                if (wdFired == 1) { client.Self.AutoPilotCancel(); var mf = Summary($"stopped: watchdog relogin ({wdLastEvent}) at {P3(me)}"); RLogR($"{o.Label}: {mf}"); return (false, mf); }
                if (now - t0 > timeout + TimeSpan.FromSeconds(idleSecs)) { client.Self.AutoPilotCancel(); var m = Summary($"stopped: timeout at {P3(me)}"); RLogR($"{o.Label}: {m}"); return (false, m); }
                // fell off a raised section?
                var pz = poly.At(s).Z;
                if (me.Z < pz - 1.8f && pj.dist < 4f) { belowSince ??= now; if ((now - belowSince.Value).TotalSeconds > 2.5) { client.Self.AutoPilotCancel(); var m = Summary($"stopped: FELL below the path at {P3(me)} (path z {pz:F1})"); RLogR($"{o.Label}: {m}"); return (false, m); } }
                else belowSince = null;
                // people
                var blk = Blocker(poly, s, lane, me);
                if (blk != null)
                {
                    if (blockedSince == null) { blockedSince = now; pauses++; triedSide = false; client.Self.AutoPilotCancel(); needAim = true; RLogR($"{o.Label}: PAUSE - {blk.name} on the path {blk.s - s:F1} m ahead (lateral {blk.lat:+0.0;-0.0} m, {blk.dist:F1} m away) at s={s:F0}"); }
                    lastBlock = blk;
                    var waited = (now - blockedSince.Value).TotalSeconds;
                    if (waited >= 10 && !triedSide)
                    {
                        triedSide = true;
                        foreach (var side in new[] { blk.lat > 0 ? -1f : 1f, blk.lat > 0 ? 1f : -1f })
                        {
                            if (Math.Abs(blk.lat - side) < 1.0f) continue;
                            if (!await LaneClear(poly, s, side, o)) continue;
                            lane = side; laneUntil = blk.s + 1.5f; sidesteps++; blockedSince = null;
                            RLogR($"{o.Label}: SIDESTEP {side:+0;-0} m to pass {blk.name} (until s={laneUntil:F0})"); break;
                        }
                        if (blockedSince != null) RLogR($"{o.Label}: no clear 1 m sidestep around {blk.name}; waiting");
                    }
                    if (blockedSince != null)
                    {
                        if (waited >= 30) { var m = Summary($"stopped: path blocked by {blk.name} for 30 s at {P3(me)}"); RLogR($"{o.Label}: {m}"); return (false, m); }
                        lastProgT = now; lastProgS = s; continue;
                    }
                }
                else if (blockedSince != null) { RLogR($"{o.Label}: path clear after {(now - blockedSince.Value).TotalSeconds:F0} s, resuming"); blockedSince = null; lastProgT = now; lastProgS = s; }
                if (lane != 0 && laneUntil >= 0 && s > laneUntil && Blocker(poly, s, 0, me) == null) { lane = 0; laneUntil = -1; RLogR($"{o.Label}: back to the path centre at s={s:F0}"); }
                if (lane != 0 && poly.Len - s < 2.5f && Blocker(poly, s, 0, me) == null) { lane = 0; laneUntil = -1; }
                // natural short pause (wander only): every 15-45 m, 6-20 s (was 3-10 s; David 2026-09-26 10:15 'wait a little longer'), not near the end, zendo margin, steps, or people
                if (s >= nextIdle && lane == 0 && blockedSince == null && poly.Len - s > 5f)
                {
                    float slope = Math.Abs(poly.At(Math.Min(poly.Len, s + 2f)).Z - poly.At(Math.Max(0, s - 2f)).Z);
                    bool steps = slope > 0.5f || Math.Abs(me.Z - poly.At(s).Z) > 1.6f;
                    if (steps || ZendoDiamond(me) <= 24f || Avatars().Any(t => t.dist >= 0 && t.dist < 3f)) nextIdle = s + 4f; // try again a bit further on
                    else
                    {
                        var dur = 6 + wRnd.Next(0, 15); // 6-20 s
                        client.Self.AutoPilotCancel();
                        await Task.Delay(700, ct);
                        var (look, what) = IdleLook(client.Self.SimPosition, poly.Dir(s));
                        if (look != null) client.Self.Movement.TurnToward(look.Value);
                        Log("wander", $"short pause {dur} s at s={s:F0}/{poly.Len:F0} m{(what != null ? ", looking at " + what : "")}");
                        await Task.Delay(dur * 1000 - 700, ct);
                        idleSecs += dur; nextIdle = s + 15f + (float)wRnd.NextDouble() * 30f; needAim = true;
                        lastProgT = DateTime.Now; lastProgS = s;
                    }
                }
                // doors ahead on a nav grid: scan the path (not a chord, so bends can't miss a door) and touch the pair
                // once it is DoorTriggerM ahead, then give the leaf time to swing before walking into it (David 2026-10-07 09:23)
                {
                    var look = Math.Min(poly.Len, s + DoorScanM);
                    var ng = NavGridFor(region, poly.At(s), poly.At(look)) ?? NavGridFor(region, poly.At(look), poly.At(look));
                    if (ng != null)
                    {
                        List<NavDoor> ahead = null;
                        var dAhead = FirstCrossingAhead(u => V2(poly.At(u)), s, poly.Len, (p, q) =>
                        {
                            if (!ng.Contains(p.X, p.Y) && !ng.Contains(q.X, q.Y)) return false;
                            var l = DoorsForCrossing(ng, p, q).Where(nd => !doorsOpened.Contains(nd.Id)).ToList();
                            if (l.Count == 0) return false; ahead = l; return true;
                        });
                        if (dAhead is float da && DoorTouchDue(da))
                        {
                            client.Self.AutoPilotCancel();
                            RLogR($"{o.Label}: door(s) {da:F1} m ahead ({string.Join(", ", ahead.Select(nd => nd.Name))}): touching then through");
                            await EnsureDoorsOpen(ahead, ct);
                            foreach (var nd in ahead) doorsOpened.Add(nd.Id);
                            var t0 = DateTime.Now;
                            while ((DateTime.Now - t0).TotalMilliseconds < DoorSwingWaitMs - DoorThroughDelayMs && !ahead.Any(nd => DoorState(nd).open)) await Task.Delay(100, ct);
                            doorSeqTried = false; // allow unstick again if still blocked
                            now = DateTime.Now; lastProgT = now; lastProgS = s; needAim = true;
                        }
                    }
                }
                // stuck?
                if (s - lastProgS >= 0.4f) { lastProgS = s; lastProgT = now; if (s - recovAtS > 2.5f) recov = 0; }
                else if ((now - lastProgT).TotalSeconds > 3.0)
                {
                    recov++; stuckEvents++; recovAtS = s;
                    if (!doorSeqTried)
                    {
                        doorSeqTried = true; client.Self.AutoPilotCancel();
                        var dr = await DoorUnstick(null, poly.At(Math.Min(poly.Len, s + 3f)), 4f, o.Label, ct);
                        if (dr != null)
                        {
                            RLogR($"{o.Label}: stuck near a door -> {dr}");
                            if (!dr.Contains("FAILED")) { lastProgT = now; lastProgS = s; needAim = true; recov = Math.Max(0, recov - 1); continue; }
                        }
                    }
                    if (recov > 4) { client.Self.AutoPilotCancel(); var m = Summary($"stopped: STUCK at {P3(me)} (s={s:F0}) after 4 recoveries"); RLogR($"{o.Label}: {m}"); return (false, m); }
                    float side = recov switch { 1 => 1f, 2 => -1f, 3 => 1f, _ => -1f };
                    if (recov >= 3)
                    {
                        var back = poly.At(Math.Max(0, s - 1.2f)); client.Self.AutoPilotCancel(); AutoPilotTo(back);
                        for (int i = 0; i < 5 && HDist(client.Self.SimPosition, back) > 0.5f; i++) await Task.Delay(400, ct);
                    }
                    bool ok = await LaneClear(poly, s, side, o) || await LaneClear(poly, s, side = -side, o);
                    lane = ok ? side : 0; laneUntil = s + 3f;
                    RLogR($"{o.Label}: STUCK at {P3(me)} s={s:F0} (no progress 3 s) -> recovery {recov}/4: {(recov >= 3 ? "backed off 1.2 m, " : "")}{(ok ? $"lane {side:+0;-0} m" : "no free side lane, retry centre")}");
                    lastProgT = now; lastProgS = s; needAim = true;
                }
                // steer
                float lookD = legacy ? Look : SteerLook(poly, s);
                float cs = Math.Min(poly.Len, s + lookD);
                var d = poly.Dir(cs); var c = poly.At(cs) + new Vector3(-d.Y, d.X, 0) * lane;
                // the server autopilot stops ~1 m short of its target: near the end aim 0.8 m past the end point (arrival check cancels at 1 m)
                if (s + lookD >= poly.Len) c += d * (legacy ? Math.Min(0.8f, s + lookD - poly.Len + 0.3f) : 0.8f); // smooth: fixed 0.8 m so the target is not re-sent every tick
                bool drift = pj.dist > 2.5f;
                if (drift)
                {
                    c = poly.At(Math.Min(poly.Len, pj.s + 1.0f));
                    if ((now - lastDriftLog).TotalSeconds > 5) { lastDriftLog = now; RLogR($"{o.Label}: DRIFT {pj.dist:F1} m off the path at {P3(me)} -> steering back"); }
                }
                if (legacy) { AutoPilotTo(c); aimSends++; continue; }
                bool send;
                if (aim == null || needAim || lane != aimLane || drift != driftMode) send = true;
                else
                {
                    float err = AngDiff(BearingDeg(me, c), BearingDeg(me, aim.Value));
                    float since = (float)(now - aimAt).TotalSeconds;
                    send = (since >= SteerMinInterval && err > SteerDeadbandDeg)
                        || (since >= SteerMinInterval && pj.dist > 1.2f && err > 4f)
                        || (HDist(me, aim.Value) < 2.5f && HDist(c, aim.Value) > 1.0f)
                        || (since >= SteerMinInterval && HDist(me, aim.Value) < 1.3f && HDist(c, aim.Value) > 0.2f)
                        || (drift && since >= 1.0f);
                }
                if (send)
                {
                    AutoPilotTo(c); aim = c; aimAt = now; needAim = false; aimLane = lane; driftMode = drift; aimSends++;
                    if (HDist(me, c) > 0.5f) client.Self.Movement.TurnToward(new Vector3(c.X, c.Y, me.Z)); // keep the client's BodyRotation in step with the autopilot
                }
            }
        }
        finally { curRoute = null; routeState = "idle"; try { sampCts.Cancel(); } catch { } }
    }

    static async Task<string> RunRoute(List<Vector3> pts, RouteOpts o, CancellationToken ct)
    {
        var poly = new Poly(pts);
        var err = await CheckBounds(poly, o);
        if (err != null) { RLogR($"{o.Label}: REFUSED - {err}"); return "refused: " + err; }
        var (ok, msg) = await FollowPoly(poly, o, ct);
        if (ok && o.SitAtEnd)
        {
            var r = await SitHome(ct);
            RLogR($"{o.Label}: sit_home -> {r}");
            msg += "; sit_home: " + r;
        }
        return (ok ? "OK " : "NOT OK ") + msg;
    }

    // ---- recording ----------------------------------------------------------------------------------
    static CancellationTokenSource recCts; static Task recTask; static volatile string recState = "idle";
    static List<Vector3> Simplify(List<Vector3> pts, float eps)
    {
        if (pts.Count < 3) return pts.ToList();
        var keep = new bool[pts.Count]; keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>(); stack.Push((0, pts.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop(); float best = 0; int bi = -1;
            for (int i = a + 1; i < b; i++)
            {
                var seg = new Poly(new List<Vector3> { pts[a], pts[b] }); var pj = seg.Project(pts[i], 0, seg.Len);
                var t = seg.Len < 1e-3f ? 0 : pj.s / seg.Len; float dz = Math.Abs(pts[i].Z - (pts[a].Z + (pts[b].Z - pts[a].Z) * t)) * 0.5f;
                float dd = Math.Max(pj.dist, dz); if (dd > best) { best = dd; bi = i; }
            }
            if (bi >= 0 && best > eps) { keep[bi] = true; stack.Push((a, bi)); stack.Push((bi, b)); }
        }
        return pts.Where((p, i) => keep[i]).ToList();
    }
    static string StartRecord(string who, string name)
    {
        if (recTask != null && !recTask.IsCompleted) return "already recording (route record stop first)";
        if (!RouteName.IsMatch(name)) return "route name: lowercase letters, digits, - and _ only";
        var av0 = FindAvatar(who); if (av0 == null) return $"avatar '{who}' not in view";
        recCts = new CancellationTokenSource(TimeSpan.FromMinutes(20)); var ct = recCts.Token; var avName = av0.Name; var avId = av0.ID;
        recTask = Task.Run(async () =>
        {
            var raw = new List<Vector3>(); DateTime lastMove = DateTime.Now, lastSeen = DateTime.Now; bool moved = false; string why = "stopped";
            RLogR($"record '{name}': sampling {avName} every 0.5 s (stops on 'route record stop', 90 s standing still after moving, 30 s out of view, or 20 min)");
            try
            {
                while (true)
                {
                    await Task.Delay(500, ct);
                    var sim = client.Network.CurrentSim; var av = sim?.ObjectsAvatars.Values.FirstOrDefault(x => x != null && x.ID == avId);
                    if (av == null || (av.ParentID != 0 && !sim.ObjectsPrimitives.ContainsKey(av.ParentID))) { if ((DateTime.Now - lastSeen).TotalSeconds > 30) { why = "avatar out of view for 30 s"; break; } continue; }
                    lastSeen = DateTime.Now;
                    var p = PositionHelper.GetAvatarPosition(sim, av);
                    if (raw.Count == 0 || HDist(raw[^1], p) >= 0.5f || Math.Abs(raw[^1].Z - p.Z) >= 0.5f)
                    { if (raw.Count > 0) moved = true; raw.Add(p); lastMove = DateTime.Now; }
                    recState = $"recording '{name}' from {avName}: {raw.Count} samples, last {P3(p)}";
                    if (moved && (DateTime.Now - lastMove).TotalSeconds > 90) { why = "avatar stood still for 90 s"; break; }
                }
            }
            catch (OperationCanceledException) { why = "stopped (route record stop, route stop, or the 20 min cap)"; }
            if (raw.Count < 2) { recState = $"record '{name}': nothing saved ({raw.Count} sample(s); {why})"; RLogR(recState); return; }
            var simp = Simplify(raw, 0.35f); var len = new Poly(simp).Len;
            SaveRoute(name, simp, $"recorded from {avName}", $"recorded {DateTime.Now:yyyy-MM-dd HH:mm} PT, {raw.Count} samples -> {simp.Count} points");
            recState = $"record '{name}': saved {simp.Count} points ({raw.Count} samples, {len:F0} m) to {RoutePath(name)} ({why})";
            RLogR(recState);
        });
        return $"recording '{name}' from {avName} (route record stop to finish)";
    }

    // ---- overhead picture ---------------------------------------------------------------------------
    static readonly SemaphoreSlim overheadLock = new(1, 1);
    static async Task<string> Overhead(string tag)
    {
        if (!await overheadLock.WaitAsync(0)) return "an overhead picture is already being made";
        try
        {
            var sim = Sim; var me = client.Self.SimPosition;
            Utils.LongToUInts(sim.Handle, out var gx, out var gy);
            float x0, x1, y0, y1;
            if (InNaberrie) { x0 = 56; x1 = 186; y0 = 84; y1 = 194; } else { x0 = me.X - 64; x1 = me.X + 64; y0 = me.Y - 55; y1 = me.Y + 55; }
            var rt = curRoute;
            var inc = new List<Vector3> { me }; if (rt != null) inc.AddRange(rt.P);
            foreach (var p in inc) { x0 = Math.Min(x0, p.X - 10); x1 = Math.Max(x1, p.X + 10); y0 = Math.Min(y0, p.Y - 10); y1 = Math.Max(y1, p.Y + 10); }
            x0 = Math.Max(0, x0); y0 = Math.Max(0, y0); x1 = Math.Min(256, x1); y1 = Math.Min(256, y1);
            bool In(Vector3 p) => p.X >= x0 && p.X <= x1 && p.Y >= y0 && p.Y <= y1;
            var roots = sim.ObjectsPrimitives.Values.Where(p => p != null && p.ParentID == 0 && p.PrimData.PCode == PCode.Prim && In(p.Position) && p.Position.Z < 200)
                .OrderBy(p => HDist(p.Position, me)).Take(4000).ToList();
            await EnsureProperties(sim, roots.Where(p => p.Properties == null).Take(3000).ToList());
            var sit = Sitters(sim);
            var pieces = new JsonArray(); var seats = new JsonArray();
            foreach (var p in roots)
            {
                var n = p.Properties?.Name ?? ""; var nl = n.ToLowerInvariant();
                p.Rotation.GetEulerAngles(out _, out _, out var rz);
                if ((nl.Contains("path") || nl.Contains("step") || nl.Contains("stair")) && !nl.Contains("walk this way") && p.Scale.X < 15 && p.Scale.Y < 15)
                    pieces.Add(new JsonObject { ["x"] = p.Position.X, ["y"] = p.Position.Y, ["sx"] = p.Scale.X, ["sy"] = p.Scale.Y, ["rotz"] = rz * 180 / MathF.PI, ["kind"] = nl.Contains("path") ? "path" : "step" });
                else if (SeatWords.Any(w => nl.Contains(w)))
                    seats.Add(new JsonObject { ["x"] = p.Position.X, ["y"] = p.Position.Y, ["z"] = p.Position.Z, ["id"] = p.ID.ToString(), ["occupied"] = sit.TryGetValue(p.LocalID, out var l) ? string.Join(",", l) : null });
            }
            var avs = new JsonArray();
            foreach (var (av, pos, dist) in Avatars()) if (dist >= 0) avs.Add(new JsonObject { ["name"] = av.Name, ["x"] = pos.X, ["y"] = pos.Y, ["z"] = pos.Z, ["seated"] = av.ParentID != 0, ["friend_owner"] = string.Equals(av.Name, OwnerName, StringComparison.OrdinalIgnoreCase) });
            var st = new JsonObject
            {
                ["region"] = sim.Name, ["grid_x"] = gx / 256, ["grid_y"] = gy / 256, ["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", IC), ["tag"] = tag,
                ["crop"] = new JsonArray(x0, x1, y0, y1),
                ["me"] = new JsonObject { ["x"] = me.X, ["y"] = me.Y, ["z"] = me.Z, ["seated"] = client.Self.SittingOn != 0, ["seat"] = client.Self.SittingOn == 0 ? null : SeatName(client.Self.SittingOn) },
                ["avatars"] = avs, ["seats"] = seats, ["pieces"] = pieces,
                ["graph"] = File.Exists(Path.Combine(RouteDir, $"_graph-{sim.Name}.json")) ? Path.Combine(RouteDir, $"_graph-{sim.Name}.json") : null,
                ["home_pillow"] = HomePillow.ToString(), ["david_pillow"] = DavidPillow.ToString(),
                ["zendo"] = InNaberrie ? new JsonArray(80.5, 142.0, 16.55) : null,
                ["parcel_grid"] = InNaberrie ? "/workspace/secondlife/research/parcel-grid8.txt" : null,
                ["state"] = routeState,
            };
            if (rt != null) { st["route"] = new JsonObject { ["name"] = curRouteName, ["points"] = new JsonArray(rt.P.Select(p => (JsonNode)new JsonArray(p.X, p.Y, p.Z)).ToArray()), ["progress_m"] = curS, ["len_m"] = rt.Len }; }
            Directory.CreateDirectory(ImgDir);
            var safeTag = Regex.Replace(tag ?? "", "[^a-zA-Z0-9_-]", ""); if (safeTag.Length > 30) safeTag = safeTag[..30];
            var outp = Path.Combine(ImgDir, $"overhead-{DateTime.Now:yyyyMMdd-HHmmss}{(safeTag.Length > 0 ? "-" + safeTag : "")}.png");
            var stf = Path.Combine(Path.GetDirectoryName(SockPath)!, "overhead-state.json");
            await File.WriteAllTextAsync(stf, st.ToJsonString());
            var psi = new ProcessStartInfo(PyBin) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add(OverheadPy); psi.ArgumentList.Add(stf); psi.ArgumentList.Add(outp);
            using var pr = Process.Start(psi)!;
            var so = pr.StandardOutput.ReadToEndAsync(); var se = pr.StandardError.ReadToEndAsync();
            using var tcs = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await pr.WaitForExitAsync(tcs.Token); } catch (OperationCanceledException) { try { pr.Kill(); } catch { } return "overhead: drawing timed out"; }
            var err = (await se).Trim();
            if (pr.ExitCode != 0 || !File.Exists(outp)) { Log("overhead", "failed: " + err[..Math.Min(300, err.Length)]); return "overhead failed: " + err[..Math.Min(300, err.Length)]; }
            Log("overhead", $"saved {outp} (me {P3(me)}, {avs.Count} avatars, {seats.Count} seats, {pieces.Count} path pieces{(rt != null ? $", route {curRouteName} {curS:F0}/{rt.Len:F0} m" : "")})");
            lock (snapshotsThisWalk) snapshotsThisWalk.Add(outp);
            return outp + " " + (await so).Trim();
        }
        finally { overheadLock.Release(); }
    }

    // ---- commands -----------------------------------------------------------------------------------
    static RouteOpts Opts(string[] a, string label) => new RouteOpts
    {
        AllowZendo = a.Contains("allow_zendo"), AllowOutside = a.Contains("allow_outside"), Label = label,
    };
    static async Task<string> RouteCmds(string cmd, string rest, string[] a)
    {
        if (cmd is "overhead" or "snapshot") return await Overhead(rest);
        if (cmd == "goto_place")
        {
            if (WanderBlocksManualWalk) return "wander is running: 'wander pause' (or 'wander stop') first";
            if (a.Length == 0) return "usage: goto_place <place> [nosit] [allow_zendo] [allow_outside]  (route list shows places)";
            var g = LoadGraph(Sim.Name); if (g == null) return $"no path graph for {Sim.Name}";
            if (!g.Places.TryGetValue(a[0], out var pl)) return $"unknown place '{a[0]}'; places: {string.Join(", ", g.Places.Keys)}";
            var (pts, err) = GraphRoute(g, client.Self.SimPosition, pl.node);
            if (err != null) return err;
            var o = Opts(a, $"goto_place {a[0]}"); o.Place = a[0];
            o.SitAtEnd = a[0].Equals("poolrock", StringComparison.OrdinalIgnoreCase) && !a.Contains("nosit") && InNaberrie;
            var len = new Poly(pts).Len;
            return StartWalk(o.Label, ct => RunRoute(pts, o, ct)) + $"; {pts.Count} points, {len:F0} m{(o.SitAtEnd ? "; will sit_home at the end" : "")}";
        }
        var sub = a.Length > 0 ? a[0].ToLowerInvariant() : "list";
        switch (sub)
        {
            case "list":
            {
                var sb = new StringBuilder("routes:\n");
                if (Directory.Exists(RouteDir))
                    foreach (var f in Directory.GetFiles(RouteDir, "*.json").Where(f => !Path.GetFileName(f).StartsWith("_")).OrderBy(f => f))
                    {
                        var nm = Path.GetFileNameWithoutExtension(f);
                        try { var r = LoadRoute(nm); sb.AppendLine($"  {nm}: {r.region}, {r.pts.Count} points, {new Poly(r.pts).Len:F0} m - {r.note}"); } catch (Exception ex) { sb.AppendLine($"  {nm}: unreadable ({ex.GetBaseException().Message})"); }
                    }
                var g = client.Network.CurrentSim != null ? LoadGraph(Sim.Name) : null;
                if (g != null) { sb.AppendLine($"places in {Sim.Name} (goto_place):"); foreach (var kv in g.Places) sb.AppendLine($"  {kv.Key}: {P3(g.N[kv.Value.node])} - {kv.Value.note}"); }
                return sb.ToString().TrimEnd();
            }
            case "show":
            {
                if (a.Length < 2 || !RouteName.IsMatch(a[1]) || !File.Exists(RoutePath(a[1]))) return "usage: route show <name> (route list)";
                var r = LoadRoute(a[1]); var poly = new Poly(r.pts);
                var sb = new StringBuilder($"{a[1]}: region {r.region}, {r.pts.Count} points, {poly.Len:F0} m, source {r.source}\n  {r.note}\n");
                sb.AppendLine("  " + string.Join(" ; ", r.pts.Select(P3)));
                if (string.Equals(r.region, Sim.Name, StringComparison.OrdinalIgnoreCase)) { var e = await CheckBounds(poly, Opts(a, "show")); sb.Append("  bounds: " + (e ?? "OK (inside the allowed parcel, outside the zendo)")); }
                var me = client.Self.SimPosition; var pj = poly.Project(me, 0, poly.Len); sb.Append($"\n  she is {pj.dist:F1} m from it (nearest at {pj.s:F0} m along)");
                return sb.ToString();
            }
            case "walk":
                if (WanderBlocksManualWalk) return "wander is running: 'wander pause' (or 'wander stop') first";
            {
                if (a.Length < 2 || !RouteName.IsMatch(a[1]) || !File.Exists(RoutePath(a[1]))) return "usage: route walk <name> [reverse] [allow_zendo] [allow_outside]";
                var r = LoadRoute(a[1]);
                if (!string.Equals(r.region, Sim.Name, StringComparison.OrdinalIgnoreCase)) return $"route {a[1]} is in {r.region}, she is in {Sim.Name}";
                var pts = r.pts.ToList(); bool rev = a.Contains("reverse"); if (rev) pts.Reverse();
                var poly = new Poly(pts); var me = client.Self.SimPosition; var pj = poly.Project(me, 0, poly.Len);
                if (pj.dist > 25f) return $"she is {pj.dist:F0} m from route {a[1]}; walk closer first";
                var walkPts = poly.From(pj.s); if (pj.dist > 0.7f) walkPts.Insert(0, me);
                var o = Opts(a, $"route {a[1]}{(rev ? " (reverse)" : "")}");
                return StartWalk(o.Label, ct => RunRoute(walkPts, o, ct)) + $"; joins at {pj.s:F0} m ({pj.dist:F1} m away), {new Poly(walkPts).Len:F0} m to go";
            }
            case "record":
                if (a.Length >= 2 && a[1] == "stop") { if (recTask == null || recTask.IsCompleted) return "not recording; " + recState; recCts.Cancel(); await Task.WhenAny(recTask, Task.Delay(3000)); return recState; }
                if (a.Length < 3) return "usage: route record <avatar name|uuid> <name> | route record stop";
                return StartRecord(string.Join(' ', a[1..^1]), a[^1]);
            case "stop":
            {
                var r = new List<string>();
                if (WanderOn) r.Add(StopWander("route stop"));
                if (walkTask != null && !walkTask.IsCompleted) { walkCts?.Cancel(); client.Self.AutoPilotCancel(); r.Add("walk stopped"); }
                if (recTask != null && !recTask.IsCompleted) { recCts?.Cancel(); await Task.WhenAny(recTask, Task.Delay(3000)); r.Add(recState); }
                return r.Count == 0 ? "nothing running" : string.Join("; ", r);
            }
            case "status":
                return $"walk: {walkState}; route: {routeState}; record: {recState}; steer {(steerLegacy ? "legacy" : "smooth")}; last leg steering: {lastSteerStats}";
            case "steer":
                if (a.Length > 1 && (a[1] == "legacy" || a[1] == "smooth")) { steerLegacy = a[1] == "legacy"; RLogR($"steering mode set to {a[1]} (from the next leg)"); }
                return $"steering: {(steerLegacy ? "legacy (carrot 2.5 m, re-aim every 0.4 s)" : $"smooth (pure pursuit {SteerLookBase}-{SteerLookMax} m, deadband {SteerDeadbandDeg} deg, min {SteerMinInterval} s, BodyRotation synced)")}; last leg: {lastSteerStats}  (route steer smooth|legacy)";
        }
        return "usage: route list | show <name> | walk <name> [reverse] | record <avatar> <name> | record stop | stop | status | steer [smooth|legacy]";
    }
}
