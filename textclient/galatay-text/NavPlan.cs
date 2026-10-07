// NavPlan.cs (2026-10-04, David: learn to walk around the Peronaut patio and through the two patio doors, without flying)
// Walkable grids built offline by scripts/nav_map.py from a `scene export` (prims sliced at avatar height, deck edges,
// door panels) live in routes/_nav-<name>.json. With a grid covering her and the goal she plans an 8-connected A* path
// (door cells cost x3), smooths it by line of sight, and walks it leg by leg (NavWalk.cs WalkLeg); a door crossing
// door first opens it (touch) if the door is still in its exported, closed pose. goto_avatar / sit_near and follow
// (manual and auto-follow of David) use the plan too whenever the straight line is blocked on the grid.
//   nav [status] | nav reload | nav doors | nav door <name> [touch] | nav plan <target> | nav to <target>[;<target>...] [--fly]
//   target: "x,y" | "x y" | a place or door name from the grid | an avatar in view.     nav selftest
//   nofly [on|off]   (persisted in run/nofly.txt, default on): walking never sets the fly flag (WalkLeg's last-resort fly hop
//                    is skipped); 'walk_path/goto_avatar/nav ... --fly' allows the hop for that one walk.
using System.Globalization;
using System.Text;
using System.Text.Json;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    internal sealed class NavDoor { public string Name; public UUID Id; public bool AlongX; public Vector2 OpenCenter, Leaf; public Vector3 Pos, Center; public Quaternion Rot; public float X0, Y0, X1, Y1; public DateTime LastTouch = DateTime.MinValue; }
    sealed class NavGrid
    {
        public string File, Name, Region; public float X0, Y0, Cell, FloorZ, Radius = 0.4f; public int Nx, Ny; public byte[] C; public DateTime Mtime;
        public List<NavDoor> Doors = new(); public Dictionary<string, Vector2> Places = new(StringComparer.OrdinalIgnoreCase);
        public bool Contains(float x, float y) => x >= X0 + Cell && y >= Y0 + Cell && x < X0 + (Nx - 1) * Cell && y < Y0 + (Ny - 1) * Cell;
        public (int i, int j) IJ(float x, float y) => ((int)MathF.Floor((x - X0) / Cell), (int)MathF.Floor((y - Y0) / Cell));
        public Vector2 XY(int i, int j) => new(X0 + (i + 0.5f) * Cell, Y0 + (j + 0.5f) * Cell);
        public byte At(int i, int j) => i < 0 || j < 0 || i >= Nx || j >= Ny ? (byte)1 : C[j * Nx + i];   // 0 free, 1 blocked, 2 door
    }

    static readonly string NoFlyFile = Env("GT_NOFLY_FILE", "/home/box/viewers/textclient/run/nofly.txt");
    static bool? noFlyCache;
    static bool? walkFlyOverride;   // per-walk '--fly' (true) / '--nofly' (false); null = the nofly setting. Cleared when that walk ends
    static bool NoFly
    {
        get { if (noFlyCache is bool b) return b; try { noFlyCache = !File.Exists(NoFlyFile) || File.ReadAllText(NoFlyFile).Trim() != "off"; } catch { noFlyCache = true; } return noFlyCache.Value; }
        set { noFlyCache = value; try { Directory.CreateDirectory(Path.GetDirectoryName(NoFlyFile)!); File.WriteAllText(NoFlyFile, value ? "on\n" : "off\n"); } catch (Exception ex) { Log("nav", "nofly save failed: " + ex.Message); } }
    }
    static bool FlyAllowed => walkFlyOverride ?? !NoFly;
    // strips --fly / --nofly from a command's args; returns the override (null = setting)
    static string TakeFlyFlag(string rest, out bool? fly)
    {
        fly = null; var w = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (w.Remove("--fly")) fly = true; if (w.Remove("--nofly")) fly = false;
        return string.Join(' ', w);
    }
    static void KeepGrounded() { if (!FlyAllowed && client.Self.Movement.Fly) { client.Self.Movement.Fly = false; client.Self.Movement.SendUpdate(true); Log("nav", "fly flag found on while nofly: cleared"); } }

    // ---- grids ----
    static readonly object navLock = new();
    static List<NavGrid> navGrids = new();
    static DateTime navScanned = DateTime.MinValue;
    static List<NavGrid> NavGrids()
    {
        lock (navLock)
        {
            if ((DateTime.Now - navScanned).TotalSeconds < 20) return navGrids;
            navScanned = DateTime.Now; var list = new List<NavGrid>();
            try
            {
                foreach (var f in Directory.GetFiles(RouteDir, "_nav-*.json").OrderBy(f => f))
                {
                    var mt = File.GetLastWriteTimeUtc(f); var old = navGrids.FirstOrDefault(g => g.File == f && g.Mtime == mt);
                    try { list.Add(old ?? LoadNavGrid(f, mt)); } catch (Exception ex) { Log("nav", $"grid {Path.GetFileName(f)} unreadable: {ex.Message}"); }
                }
            }
            catch (Exception ex) { Log("nav", "grid scan failed: " + ex.Message); }
            return navGrids = list;
        }
    }
    static NavGrid LoadNavGrid(string f, DateTime mt)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(f)); var r = doc.RootElement;
        float Fl(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetSingle() : 0f;
        var g = new NavGrid { File = f, Mtime = mt, Name = r.GetProperty("name").GetString(), Region = r.TryGetProperty("region", out var rg) ? rg.GetString() : null,
            X0 = Fl(r.GetProperty("x0")), Y0 = Fl(r.GetProperty("y0")), Cell = Fl(r.GetProperty("cell")), FloorZ = Fl(r.GetProperty("floor_z")),
            Nx = r.GetProperty("nx").GetInt32(), Ny = r.GetProperty("ny").GetInt32() };
        if (g.Cell < 0.02f || g.Nx < 2 || g.Ny < 2 || g.Nx * g.Ny > 4_000_000) throw new InvalidDataException("bad grid size");
        g.C = new byte[g.Nx * g.Ny]; int j = 0;
        foreach (var row in r.GetProperty("rows").EnumerateArray())
        {
            var s = row.GetString() ?? ""; if (s.Length != g.Nx || j >= g.Ny) throw new InvalidDataException($"row {j} has {s.Length} cells, want {g.Nx}");
            for (int i = 0; i < g.Nx; i++) g.C[j * g.Nx + i] = s[i] == '.' ? (byte)0 : s[i] == 'D' ? (byte)2 : (byte)1;
            j++;
        }
        if (j != g.Ny) throw new InvalidDataException($"{j} rows, want {g.Ny}");
        foreach (var d in r.GetProperty("doors").EnumerateArray())
        {
            var mn = d.GetProperty("min"); var mx = d.GetProperty("max"); var p = d.GetProperty("pos"); var q = d.GetProperty("local_rot"); var c = d.GetProperty("center");
            g.Doors.Add(new NavDoor { Name = d.GetProperty("name").GetString(), Id = UUID.Parse(d.GetProperty("uuid").GetString()), AlongX = d.GetProperty("axis").GetString() == "x",
                Pos = new Vector3(Fl(p[0]), Fl(p[1]), Fl(p[2])), Center = new Vector3(Fl(c[0]), Fl(c[1]), Fl(c[2])),
                Rot = new Quaternion(Fl(q[0]), Fl(q[1]), Fl(q[2]), Fl(q[3])), X0 = Fl(mn[0]), Y0 = Fl(mn[1]), X1 = Fl(mx[0]), Y1 = Fl(mx[1]) });
        }
        if (r.TryGetProperty("radius", out var rad)) g.Radius = Fl(rad);
        foreach (var d in g.Doors)
        {   // the opening = its door cells; the hinge = the prim origin (these doors turn about it: 'moved 0.00 m, turned 85 deg'),
            // the leaf runs from the hinge to the far end of the panel on the opening's side
            float sx = 0, sy = 0; int n = 0;
            for (int jj = 0; jj < g.Ny; jj++) for (int ii = 0; ii < g.Nx; ii++)
                if (g.C[jj * g.Nx + ii] == 2) { var c = g.XY(ii, jj); if (c.X >= d.X0 - 0.3f && c.X <= d.X1 + 0.3f && c.Y >= d.Y0 - 0.3f && c.Y <= d.Y1 + 0.3f) { sx += c.X; sy += c.Y; n++; } }
            d.OpenCenter = n > 0 ? new Vector2(sx / n, sy / n) : new Vector2(d.Center.X, d.Center.Y);
            if (d.AlongX) { float s = MathF.Sign(d.OpenCenter.X - d.Pos.X); d.Leaf = new Vector2(s < 0 ? d.X0 - d.Pos.X : d.X1 - d.Pos.X, 0); }
            else { float s = MathF.Sign(d.OpenCenter.Y - d.Pos.Y); d.Leaf = new Vector2(0, s < 0 ? d.Y0 - d.Pos.Y : d.Y1 - d.Pos.Y); }
        }
        foreach (var pl in r.GetProperty("places").EnumerateObject()) g.Places[pl.Name] = new Vector2(Fl(pl.Value[0]), Fl(pl.Value[1]));
        Log("nav", $"loaded grid '{g.Name}' ({g.Region}) {g.Nx}x{g.Ny} @ {g.Cell} m, {g.Doors.Count} doors, {g.Places.Count} places from {Path.GetFileName(f)}");
        return g;
    }
    static NavGrid NavGridFor(string region, Vector3 a, Vector3 b) => NavGrids().FirstOrDefault(g => (g.Region == null || string.Equals(g.Region, region, StringComparison.OrdinalIgnoreCase))
        && g.Contains(a.X, a.Y) && g.Contains(b.X, b.Y) && MathF.Abs(a.Z - g.FloorZ) < 3f);
    internal static HashSet<UUID> NavDoorIds() => NavGrids().SelectMany(g => g.Doors).Select(d => d.Id).ToHashSet();

    // ---- planner (same rules as scripts/nav_map.py) ----
    static bool Free(NavGrid g, int i, int j) => g.At(i, j) != 1;
    static (int, int)? NearestFree(NavGrid g, int i, int j, int rmax = 15)
    {
        if (Free(g, i, j)) return (i, j);
        for (int r = 1; r <= rmax; r++)
        {
            (int c, int i, int j)? best = null;
            for (int di = -r; di <= r; di++) for (int dj = -r; dj <= r; dj++)
                if (Math.Max(Math.Abs(di), Math.Abs(dj)) == r && Free(g, i + di, j + dj) && (best == null || di * di + dj * dj < best.Value.c)) best = (di * di + dj * dj, i + di, j + dj);
            if (best != null) return (best.Value.i, best.Value.j);
        }
        return null;
    }
    static bool Los(NavGrid g, (int i, int j) a, (int i, int j) b, bool allowDoor = false)
    {
        int n = Math.Max(Math.Abs(b.i - a.i), Math.Abs(b.j - a.j)) * 2 + 1;
        for (int k = 0; k <= n; k++)
        {
            int i = (int)Math.Round(a.i + (b.i - a.i) * k / (double)n, MidpointRounding.ToEven), j = (int)Math.Round(a.j + (b.j - a.j) * k / (double)n, MidpointRounding.ToEven);
            var c = g.At(i, j); if (c == 1 || (c == 2 && !allowDoor)) return false;
        }
        return true;
    }
    static List<Vector2> NavPlanPath(NavGrid g, Vector2 from, Vector2 to)
    {
        var s0 = NearestFree(g, g.IJ(from.X, from.Y).i, g.IJ(from.X, from.Y).j); var t0 = NearestFree(g, g.IJ(to.X, to.Y).i, g.IJ(to.X, to.Y).j);
        if (s0 == null || t0 == null) return null;
        var (s, t) = (s0.Value, t0.Value);
        int N = g.Nx * g.Ny, S = s.Item2 * g.Nx + s.Item1, T = t.Item2 * g.Nx + t.Item1;
        var cost = new float[N]; Array.Fill(cost, float.MaxValue); var came = new int[N]; Array.Fill(came, -1);
        var q = new PriorityQueue<int, float>(); cost[S] = 0; came[S] = S; q.Enqueue(S, 0);
        var dirs = new[] { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1) };
        while (q.TryDequeue(out var cur, out var pri))
        {
            if (cur == T) break;
            int ci = cur % g.Nx, cj = cur / g.Nx; float cc = cost[cur];
            if (pri - MathF.Sqrt((t.Item1 - ci) * (t.Item1 - ci) + (t.Item2 - cj) * (t.Item2 - cj)) > cc + 1e-3f) continue;   // stale entry
            foreach (var (di, dj) in dirs)
            {
                int ni = ci + di, nj = cj + dj;
                if (!Free(g, ni, nj) || (di != 0 && dj != 0 && !(Free(g, ci + di, cj) && Free(g, ci, cj + dj)))) continue;
                float nc = cc + (di != 0 && dj != 0 ? 1.4142f : 1f) * (g.At(ni, nj) == 2 ? 3f : 1f);
                int n = nj * g.Nx + ni;
                if (nc < cost[n]) { cost[n] = nc; came[n] = cur; q.Enqueue(n, nc + MathF.Sqrt((t.Item1 - ni) * (t.Item1 - ni) + (t.Item2 - nj) * (t.Item2 - nj))); }
            }
        }
        if (came[T] < 0) return null;
        var cells = new List<(int i, int j)>(); for (int c = T; ; c = came[c]) { cells.Add((c % g.Nx, c / g.Nx)); if (c == S) break; }
        cells.Reverse();
        // line-of-sight smoothing; never across door cells, so the cells just before / after a door stay (she crosses it square)
        var outp = new List<(int i, int j)> { cells[0] }; int k0 = 0;
        while (k0 < cells.Count - 1)
        {
            int m = cells.Count - 1;
            while (m > k0 + 1 && !(Los(g, cells[k0], cells[m]) && !cells.Skip(k0 + 1).Take(m - k0 - 1).Any(c => g.At(c.i, c.j) == 2))) m--;
            outp.Add(cells[m]); k0 = m;
        }
        // drop near-collinear middle points (the cell-by-cell steps through a door panel)
        var pts = outp.Select(c => g.XY(c.i, c.j)).ToList();
        for (int k = pts.Count - 2; k >= 1; k--)
        {
            var a = pts[k - 1]; var b = pts[k]; var c = pts[k + 1];
            float cross = MathF.Abs((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X)); float len = Vector2.Distance(a, c);
            if (len < 1e-4f || cross / len < 0.05f) pts.RemoveAt(k);
        }
        return pts;
    }
    static bool NavStraightClear(NavGrid g, Vector3 a, Vector3 b)
    { var ia = g.IJ(a.X, a.Y); var ib = g.IJ(b.X, b.Y); return Free(g, ia.i, ia.j) && Free(g, ib.i, ib.j) && Los(g, ia, ib); }
    // the grid door a segment crosses (a 'D' cell on it), if any
    static NavDoor DoorOnSegment(NavGrid g, Vector2 a, Vector2 b) => DoorsOnSegment(g, a, b).FirstOrDefault();
    // all doors a segment crosses (double doors share an opening line)
    static List<NavDoor> DoorsOnSegment(NavGrid g, Vector2 a, Vector2 b)
    {
        var hit = new List<NavDoor>();
        if (g == null) return hit;
        int n = Math.Max(1, (int)(Vector2.Distance(a, b) / (g.Cell * 0.5f)));
        for (int k = 0; k <= n; k++)
        {
            var p = a + (b - a) * (k / (float)n); var (i, j) = g.IJ(p.X, p.Y);
            if (g.At(i, j) != 2) continue;
            foreach (var d in g.Doors.OrderBy(d => Vector2.Distance(new Vector2(d.Center.X, d.Center.Y), p)))
            {
                if (p.X < d.X0 - 0.5f || p.X > d.X1 + 0.5f || p.Y < d.Y0 - 0.5f || p.Y > d.Y1 + 0.5f) continue;
                if (hit.All(h => h.Id != d.Id)) hit.Add(d);
            }
        }
        return hit;
    }
    // pure: a door + same-axis siblings within pairRadius (front-double-east + front-double-west)
    internal static List<NavDoor> DoorPairGroup(IReadOnlyList<NavDoor> all, NavDoor primary, float pairRadius = 3.5f)
    {
        if (primary == null || all == null) return new List<NavDoor>();
        return all.Where(d => d != null && (d.Id == primary.Id
            || (d.AlongX == primary.AlongX && Vector3.Distance(d.Center, primary.Center) <= pairRadius)))
            .GroupBy(d => d.Id).Select(g => g.First()).ToList();
    }
    // doors crossed by the segment, each expanded to its pair group
    static List<NavDoor> DoorsForCrossing(NavGrid g, Vector2 a, Vector2 b)
    {
        var primary = DoorsOnSegment(g, a, b);
        if (primary.Count == 0) return primary;
        var seen = new HashSet<UUID>(); var outL = new List<NavDoor>();
        foreach (var d in primary)
            foreach (var s in DoorPairGroup(g.Doors, d))
                if (seen.Add(s.Id)) outL.Add(s);
        return outL;
    }
    // Door trigger (2026-10-07 David 09:23 'doors open too late, she bumps them'): touch when the door is <= 3 m ahead along
    // the path, scanning DoorScanM ahead in DoorScanStepM steps; then wait up to DoorSwingWaitMs (total) for a leaf to swing.
    internal const float DoorTriggerM = 3.0f, DoorScanM = 6.0f, DoorScanStepM = 0.5f;
    internal const int DoorSwingWaitMs = 900;
    internal static bool DoorTouchDue(float distAhead) => distAhead >= 0 && distAhead <= DoorTriggerM;
    // path distance from s to the first step [u, u+step] that crosses a door, or null within the scan window
    internal static float? FirstCrossingAhead(Func<float, Vector2> at, float s, float len, Func<Vector2, Vector2, bool> crosses, float scan = DoorScanM, float step = DoorScanStepM)
    {
        float end = Math.Min(len, s + scan);
        for (float u = s; u < end; u += step) { float v = Math.Min(end, u + step); if (crosses(at(u), at(v))) return u - s; }
        return null;
    }
    // How long we may pause after touches before walking through (auto-close; 2026-10-05 David: within ~0.3 s).
    internal const int DoorThroughDelayMs = 300;
    // Touch closed doors only (never re-touch an already-open leaf — these toggle shut). Touch the pair in one burst,
    // wait at most DoorThroughDelayMs, then return so the walker goes through at full speed. One open leaf is enough;
    // a slow/failed sibling must not delay the pass (2026-10-05).
    static async Task EnsureDoorsOpen(IReadOnlyList<NavDoor> doors, CancellationToken ct)
    {
        if (doors == null || doors.Count == 0) return;
        var sim = client.Network.CurrentSim; if (sim == null) return;
        var need = new List<(NavDoor d, Primitive p, string how)>();
        int alreadyOpen = 0;
        foreach (var d in doors)
        {
            var st = DoorState(d);
            var p = sim.ObjectsPrimitives.Values.FirstOrDefault(x => x != null && x.ID == d.Id);
            bool ph = p != null && (p.Flags & PrimFlags.Phantom) != 0;
            if (!st.known || p == null) { Log("nav", $"door {d.Name}: {st.how}; walking on"); continue; }
            // Pose/phantom already open: do NOT touch (toggle would shut it). Count as passable.
            if (st.open || ph)
            { alreadyOpen++; Log("nav", $"door {d.Name}: already {st.how} — not touching (toggle risk)"); continue; }
            need.Add((d, p, st.how));
        }
        if (need.Count == 0) { if (alreadyOpen > 0) Log("nav", $"doors: {alreadyOpen} already open, going through"); return; }
        foreach (var (d, p, how) in need)
        {
            d.LastTouch = DateTime.Now; client.Self.Touch(p.LocalID);
            Log("door", $"touched nav door {d.Name} {d.Id} to open it (was {how})");
        }
        // Fixed short delay then GO — do not wait for every leaf; one opening is enough to pass
        await Task.Delay(DoorThroughDelayMs, ct);
        foreach (var (d, _, _) in need) Log("nav", $"door {d.Name}: {DoorState(d).how} (through after {DoorThroughDelayMs} ms)");
    }

    // ---- doors ----
    static float Yaw(Quaternion q) => MathF.Atan2(2 * (q.W * q.Z + q.X * q.Y), 1 - 2 * (q.Y * q.Y + q.Z * q.Z));
    // the grid with every open (turned) door leaf blocked where it now stands (hinge -> leaf end, inflated by the grid radius)
    static NavGrid WithOpenLeaves(NavGrid g)
    {
        NavGrid o = null; var sim = client.Network.CurrentSim;
        foreach (var d in g.Doors)
        {
            var p = sim?.ObjectsPrimitives.Values.FirstOrDefault(x => x != null && x.ID == d.Id); if (p == null) continue;
            float da = Yaw(p.Rotation) - Yaw(d.Rot); if (MathF.Abs(MathF.IEEERemainder(da, 2 * MathF.PI)) < 10 * MathF.PI / 180) continue;
            o ??= new NavGrid { File = g.File, Name = g.Name, Region = g.Region, X0 = g.X0, Y0 = g.Y0, Cell = g.Cell, FloorZ = g.FloorZ, Radius = g.Radius, Nx = g.Nx, Ny = g.Ny, C = (byte[])g.C.Clone(), Doors = g.Doors, Places = g.Places, Mtime = g.Mtime };
            var h = new Vector2(d.Pos.X, d.Pos.Y); float c = MathF.Cos(da), sn = MathF.Sin(da);
            var e = h + new Vector2(d.Leaf.X * c - d.Leaf.Y * sn, d.Leaf.X * sn + d.Leaf.Y * c);
            MarkSegment(o, h, e, o.Radius);
        }
        return o ?? g;
    }
    static void MarkSegment(NavGrid g, Vector2 a, Vector2 b, float r)
    {
        var (i0, j0) = g.IJ(MathF.Min(a.X, b.X) - r, MathF.Min(a.Y, b.Y) - r); var (i1, j1) = g.IJ(MathF.Max(a.X, b.X) + r, MathF.Max(a.Y, b.Y) + r);
        var ab = b - a; float L2 = MathF.Max(1e-6f, ab.X * ab.X + ab.Y * ab.Y);
        for (int j = Math.Max(0, j0); j <= Math.Min(g.Ny - 1, j1); j++)
            for (int i = Math.Max(0, i0); i <= Math.Min(g.Nx - 1, i1); i++)
            {
                var p = g.XY(i, j); float t = Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / L2, 0, 1);
                if (Vector2.Distance(p, a + ab * t) <= r) g.C[j * g.Nx + i] = 1;
            }
    }
    // open = the panel has left its exported (closed) pose: moved > 0.25 m or turned > 10 degrees
    static (bool known, bool open, string how) DoorState(NavDoor d)
    {
        var sim = client.Network.CurrentSim; var p = sim?.ObjectsPrimitives.Values.FirstOrDefault(x => x != null && x.ID == d.Id);
        if (p == null) return (false, false, "not in view");
        var wp = WorldPos(sim, p, out var root); if (root == null) return (false, false, "parent unknown");
        float mv = Vector3.Distance(wp, d.Pos); double ang = Math.Acos(Math.Min(1.0, Math.Abs(Quaternion.Dot(p.Rotation, d.Rot)))) * 2 * 180 / Math.PI;
        bool open = mv > 0.25f || ang > 10; bool ph = (p.Flags & PrimFlags.Phantom) != 0;
        return (true, open || ph, string.Format(CultureInfo.InvariantCulture, "{0} (moved {1:F2} m, turned {2:F0} deg{3})", open ? "open" : ph ? "phantom" : "closed", mv, ang, ph ? ", phantom" : ""));
    }
    static async Task<string> EnsureDoorOpen(NavDoor d, CancellationToken ct)
    {
        var st = DoorState(d);
        if (!st.known) { Log("nav", $"door {d.Name}: {st.how}; walking on"); return st.how; }
        // Open this leaf and any same-axis sibling (double doors) together, then go through quickly
        var g = NavGrids().FirstOrDefault(x => x.Doors.Any(dd => dd.Id == d.Id));
        var group = g != null ? DoorPairGroup(g.Doors, d) : new List<NavDoor> { d };
        await EnsureDoorsOpen(group, ct);
        st = DoorState(d);
        return st.how;
    }

    // ---- walking a plan ----
    static Vector2 V2(Vector3 v) => new(v.X, v.Y);
    static string P2(Vector2 v) => string.Format(CultureInfo.InvariantCulture, "{0:F1},{1:F1}", v.X, v.Y);
    static async Task<bool> NavWalkTo(NavGrid g, Vector2 goal, float lastTol, CancellationToken ct)
    {
        await EnsureStandingForWalk(ct);
        var aoRefuse = await AoGuardBeforeWalk("nav", ct);
        if (aoRefuse != null) { Log("walk", "REFUSED - " + aoRefuse); walkState = "refused: " + aoRefuse; return false; }
        int crossings = 0;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var me = client.Self.SimPosition;
            if (Vector2.Distance(V2(me), goal) <= lastTol) return true;
            var gl = WithOpenLeaves(g);   // an open door leaf is an obstacle where it stands now
            var path = NavPlanPath(gl, V2(me), goal);
            if (path == null) { Log("nav", $"no path on grid '{g.Name}' from {P2(V2(me))} to {P2(goal)}{(gl != g ? " (open door leaves blocked)" : "")}"); return false; }
            if (Free(g, g.IJ(goal.X, goal.Y).i, g.IJ(goal.X, goal.Y).j)) path[^1] = goal;
            Log("nav", $"plan{(attempt > 0 ? $" (re-plan {attempt})" : "")} on '{g.Name}': {string.Join(" -> ", path.Select(P2))} ({path.Count - 1} legs, nofly={(FlyAllowed ? "off" : "on")})");
            // walk up to the first door crossing only; past a door she re-plans from the far side
            int kd = -1; NavDoor door = null;
            for (int k = 1; k < path.Count && kd < 0; k++) { door = DoorOnSegment(g, path[k - 1], path[k]); if (door != null) kd = k; }
            bool ok = true;
            if (kd < 0) { ok = await NavLegs(g, path, 1, path.Count - 1, lastTol, ct); if (ok) return true; }
            else
            {
                // Cross through the opening at full speed (2026-10-05): NO wait-spot detour (that walked her backwards
                // while doors auto-closed). Walk along the plan up to the door segment, touch closed leaves (~0.3 s),
                // then WalkLeg straight past OpenCenter to the far side. Try both of a double door; one open is enough.
                var n = door.AlongX ? new Vector2(0, 1) : new Vector2(1, 0); var oc = door.OpenCenter; var near = path[kd - 1];
                if ((near.X - oc.X) * n.X + (near.Y - oc.Y) * n.Y > 0) n = -n;   // n = near -> far
                Vector2 FarSpot()
                {
                    foreach (var dist in new[] { 1.6f, 1.3f, 1.0f })
                    { var q = oc + n * dist; var c = g.IJ(q.X, q.Y); if (g.At(c.i, c.j) == 0) return q; }
                    return path[kd];
                }
                var through = FarSpot();
                // Approach along the planned path (never a side wait spot). Stop short of the panel so touch can fire.
                ok = kd > 1 ? await NavLegs(g, path, 1, kd - 1, 0.7f, ct, $" toward {door.Name}") : true;
                if (ok)
                {
                    var pair = DoorsForCrossing(g, path[kd - 1], path[kd]);
                    await EnsureDoorsOpen(pair, ct);   // <= DoorThroughDelayMs, then GO
                    navTrack = true;
                    try { ok = await WalkLeg(new Vector3(through.X, through.Y, client.Self.SimPosition.Z), 0.6f, $"nav through {door.Name}", ct, navMode: true); }
                    finally { navTrack = false; }
                    if (ok && ++crossings <= 6) { Log("nav", $"through {door.Name}: at {V(client.Self.SimPosition)}"); attempt--; continue; }
                }
            }
            Log("nav", $"leg failed at {V(client.Self.SimPosition)}: re-planning");
        }
        return false;
    }
    static bool navTrack;
    // walk path[from..to] (indices of target points); legs under 0.9 m are skipped (the autopilot often won't start on them)
    static async Task<bool> NavLegs(NavGrid g, List<Vector2> path, int from, int to, float lastTol, CancellationToken ct, string tag = "")
    {
        for (int k = from; k <= to; k++)
        {
            bool last = k == to;
            if (!last && Vector2.Distance(V2(client.Self.SimPosition), path[k]) < 0.9f) continue;
            var tgt = new Vector3(path[k].X, path[k].Y, client.Self.SimPosition.Z);
            navTrack = true;
            try { if (!await WalkLeg(tgt, last ? lastTol : 0.6f, $"nav {k}/{path.Count - 1}{tag}", ct, navMode: true)) return false; }
            finally { navTrack = false; }
        }
        return true;
    }

    // follow: when the straight line to the leader is blocked on a grid, steer to the next planned waypoint instead
    static DateTime navFollowLogAt = DateTime.MinValue; static List<Vector2> navFollowPath; static DateTime navFollowPlannedAt = DateTime.MinValue; static Vector2 navFollowGoal;
    static Vector3? NavFollowStep(Vector3 me, Vector3 leader)
    {
        var g = NavGridFor(client.Network.CurrentSim?.Name, me, leader); if (g == null) return null;
        KeepGrounded();
        if (NavStraightClear(g, me, leader)) return null;   // clear straight line: plain follow
        if (navFollowPath == null || (DateTime.Now - navFollowPlannedAt).TotalSeconds > 2 || Vector2.Distance(navFollowGoal, V2(leader)) > 1f)
        { navFollowPath = NavPlanPath(WithOpenLeaves(g), V2(me), V2(leader)); navFollowPlannedAt = DateTime.Now; navFollowGoal = V2(leader); }
        var path = navFollowPath; if (path == null || path.Count < 2) return null;
        var next = path.Skip(1).FirstOrDefault(p => Vector2.Distance(p, V2(me)) > 0.8f); if (next == default) return null;
        var door = DoorOnSegment(g, V2(me), next) ?? (path.Count > 2 ? DoorOnSegment(g, path[1], path[2]) : null);
        if (door != null && Vector2.Distance(V2(me), new Vector2(door.Center.X, door.Center.Y)) < 2.5f && (DateTime.Now - door.LastTouch).TotalSeconds > 15 && !DoorState(door).open)
            _ = Task.Run(async () => { try { await EnsureDoorOpen(door, CancellationToken.None); } catch { } });
        if ((DateTime.Now - navFollowLogAt).TotalSeconds > 10) { navFollowLogAt = DateTime.Now; Log("nav", $"follow: straight line blocked on '{g.Name}', routing {string.Join(" -> ", path.Select(P2))}"); }
        return new Vector3(next.X, next.Y, me.Z);
    }

    // ---- commands ----
    static Vector2? NavTarget(NavGrid g, string t, out string what)
    {
        what = t; var f = t.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (f.Length == 2 && F(f[0], out var x) && F(f[1], out var y)) return new Vector2(x, y);
        if (g != null)
        {
            var pl = g.Places.FirstOrDefault(kv => kv.Key.Equals(t, StringComparison.OrdinalIgnoreCase)); if (pl.Key == null) pl = g.Places.FirstOrDefault(kv => kv.Key.Contains(t, StringComparison.OrdinalIgnoreCase));
            if (pl.Key != null) { what = "place " + pl.Key; return pl.Value; }
            var d = g.Doors.FirstOrDefault(d => d.Name.Equals(t, StringComparison.OrdinalIgnoreCase)); if (d != null) { what = "door " + d.Name; return new Vector2(d.Center.X, d.Center.Y); }
        }
        var av = FindAvatar(t); if (av != null) { what = av.Name; var p = PositionHelper.GetAvatarPosition(Sim, av); return new Vector2(p.X, p.Y); }
        return null;
    }
    static NavGrid NavGridHere() { var me = client.Self.SimPosition; return NavGridFor(client.Network.CurrentSim?.Name, me, me); }

    static async Task<string> NavCmd(string[] a, string rest)
    {
        string sub = a.Length > 0 ? a[0].ToLowerInvariant() : "status";
        if (sub == "selftest") return NavSelfTest();
        if (sub == "reload") { lock (navLock) navScanned = DateTime.MinValue; var gs = NavGrids(); return $"{gs.Count} grid(s): {string.Join(", ", gs.Select(g => $"{g.Name} ({g.Region})"))}"; }
        if (!LoggedIn) return "not logged in";
        var g = NavGridHere();
        if (sub == "status")
            return $"nofly={(NoFly ? "on" : "off")}; grids: {string.Join(", ", NavGrids().Select(x => $"{x.Name} ({x.Region}, {x.Doors.Count} doors)"))}; here: {(g == null ? "none" : g.Name)}";
        if (g == null) return $"no nav grid covers {V(client.Self.SimPosition)} in {client.Network.CurrentSim?.Name} (scripts/nav_map.py build)";
        if (sub == "doors")
            return string.Join("\n", g.Doors.Select(d => $"{d.Name} {d.Id} opening {P2(d.OpenCenter)} hinge {d.Pos.X:F1},{d.Pos.Y:F1}: {DoorState(d).how}, {Vector2.Distance(V2(client.Self.SimPosition), new Vector2(d.Center.X, d.Center.Y)):F1} m"));
        if (sub == "places") return string.Join("\n", g.Places.Select(kv => $"{kv.Key}: {P2(kv.Value)}"));
        if (sub == "door" && a.Length >= 2)
        {
            var d = g.Doors.FirstOrDefault(d => d.Name.Equals(a[1], StringComparison.OrdinalIgnoreCase)); if (d == null) return $"no door '{a[1]}' (nav doors)";
            if (a.Length >= 3 && a[2] == "touch") { var sim = Sim; var p = sim.ObjectsPrimitives.Values.FirstOrDefault(x => x != null && x.ID == d.Id); if (p == null) return "door not in view"; client.Self.Touch(p.LocalID); d.LastTouch = DateTime.Now; Log("door", $"touched nav door {d.Name} {d.Id} (explicit)"); await Task.Delay(2500); }
            return $"{d.Name}: {DoorState(d).how}";
        }
        if (sub is "plan" or "to")
        {
            var body = TakeFlyFlag(rest.Substring(rest.IndexOf(a[0], StringComparison.Ordinal) + a[0].Length).Trim(), out var fly);
            var goals = new List<(Vector2 p, string what)>();
            foreach (var t in body.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            { var p = NavTarget(g, t, out var what); if (p == null) return $"unknown target '{t}' (x,y | place | door | avatar in view; 'nav places')"; if (!g.Contains(p.Value.X, p.Value.Y)) return $"{what} {P2(p.Value)} is outside grid '{g.Name}'"; goals.Add((p.Value, what)); }
            if (goals.Count == 0) return "usage: nav plan|to <x,y | place | door | avatar>[;...] [--fly]";
            if (sub == "plan")
            {
                var sb = new StringBuilder(); var from = V2(client.Self.SimPosition);
                var gl = WithOpenLeaves(g);
                foreach (var (p, what) in goals) { var path = NavPlanPath(gl, from, p); sb.AppendLine(path == null ? $"to {what}: NO PATH" : $"to {what}: {string.Join(" -> ", path.Select(P2))}{string.Concat(path.Zip(path.Skip(1)).Select(s => DoorOnSegment(g, s.First, s.Second)).Where(d => d != null).Select(d => $" [door {d.Name}: {DoorState(d).how}]"))}"); from = p; }
                return sb.ToString().TrimEnd();
            }
            if (WanderBlocksManualWalk) return "wander is running: 'wander pause' (or 'wander stop') first";
            followId = UUID.Zero;
            return StartWalk($"nav to {string.Join(" ; ", goals.Select(x => x.what))}", async ct =>
            {
                foreach (var (p, what) in goals)
                {
                    Log("nav", $"-> {what} {P2(p)}");
                    if (!await NavWalkTo(g, p, 0.8f, ct)) return $"stopped before {what} at {V(client.Self.SimPosition)}";
                    Log("nav", $"reached {what}: at {V(client.Self.SimPosition)}");
                }
                return $"arrived at {V(client.Self.SimPosition)}";
            }, fly);
        }
        return "usage: nav [status|reload|doors|places|selftest] | nav door <name> [touch] | nav plan|to <x,y | place | door | avatar>[;...] [--fly]";
    }

    static string NoFlyCmd(string[] a)
    {
        if (a.Length == 1 && a[0] is "on" or "off") { NoFly = a[0] == "on"; Log("nav", $"nofly {a[0]}"); if (NoFly) KeepGrounded(); }
        else if (a.Length > 0) return "usage: nofly [on|off]";
        return $"nofly {(NoFly ? "on: walking never flies (stuck recovery skips the fly hop; '--fly' allows it for one walk)" : "off: stuck recovery may do one short fly hop")}";
    }

    internal static string NavSelfTest()
    {
        // 4 x 4 m, wall across y=1.9..2.1 with a 0.8 m door (cells 16..23): south -> north must pass the door, and a closed wall has no path
        int n = 40; var g = new NavGrid { Name = "selftest", X0 = 0, Y0 = 0, Cell = 0.1f, Nx = n, Ny = n, C = new byte[n * n] };
        for (int j = 19; j <= 20; j++) for (int i = 0; i < n; i++) g.C[j * n + i] = (byte)(i >= 16 && i < 24 ? 2 : 1);
        g.Doors.Add(new NavDoor { Name = "d", Center = new Vector3(2, 2, 0), X0 = 1.6f, Y0 = 1.9f, X1 = 2.4f, Y1 = 2.1f });
        var p = NavPlanPath(g, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 3.5f));
        bool ok1 = p != null && p.Zip(p.Skip(1)).Any(s => DoorOnSegment(g, s.First, s.Second)?.Name == "d") && p.All(q => Free(g, g.IJ(q.X, q.Y).i, g.IJ(q.X, q.Y).j));
        bool okSeg = p != null && p.Zip(p.Skip(1)).All(s => Los(g, g.IJ(s.First.X, s.First.Y), g.IJ(s.Second.X, s.Second.Y), allowDoor: true));
        for (int i = 16; i < 24; i++) { g.C[19 * n + i] = 1; g.C[20 * n + i] = 1; }
        bool ok2 = NavPlanPath(g, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 3.5f)) == null;
        var p3 = NavPlanPath(g, new Vector2(0.5f, 0.5f), new Vector2(3.5f, 0.5f));
        bool ok3 = p3 != null && p3.Count == 2;   // open floor: one straight leg
        bool okFly = TakeFlyFlag("David Nightingale --fly", out var fl) == "David Nightingale" && fl == true && TakeFlyFlag("x --nofly", out var fl2) == "x" && fl2 == false;
        MarkSegment(g, new Vector2(2f, 0f), new Vector2(2f, 1.3f), 0.3f);   // an open leaf standing across the south room, gap y 1.6..1.9
        var p4 = NavPlanPath(g, new Vector2(0.5f, 0.5f), new Vector2(3.5f, 0.5f));
        bool ok4 = p4 != null && p4.Count > 2 && p4.All(q => Free(g, g.IJ(q.X, q.Y).i, g.IJ(q.X, q.Y).j)) && p4.Any(q => q.Y > 1.55f);
        bool all = ok1 && okSeg && ok2 && ok3 && ok4 && okFly;
        return all ? $"nav selftest ok (door path {string.Join(" -> ", p.Select(P2))})" : $"nav selftest FAILED door={ok1} segments={okSeg} closed={ok2} straight={ok3} leaf={ok4} flyflag={okFly} path={(p == null ? "null" : string.Join(" -> ", p.Select(P2)))}";
    }
}
