// Roam.cs (2026-11-11, David): roaming on non-home sims (Peronaut home wander is unchanged).
// MCP / command: roam on|off|status [radius_m=40]. Picks open walkable targets from terrain, prim
// floors, and optional map tiles of the start region plus directly adjacent connected neighbors
// (same owner or a Linden/public area) within radius_m of the start, in global coordinates.
// Walks with z = the support surface (terrain or the highest prim/floor under the point, at most
// avatar height + 2 m). Arrival is horizontal (walk_to / WalkLeg); z is never required.
// Stairs are walkable (fine ~0.5 m samples near height changes, steps up to ~0.5 m, overall
// rise/fall allowed). Reject only a wall jump > ~0.6 m up, a drop > ~1.0 m, water, or an 8 m
// margin on a void / non-connected / restricted edge. Pause 20-60 s between legs; pause for
// nearby chat within 5 m (not IMs or far chat). Stop on teleport, sit, leash/follow, come here,
// or logout. Walked border crossings continue (wait for the handoff); a failed crossing is stuck.
using System.Globalization;
using System.Net.Http;
using System.Text;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    internal const float RoamDefaultRadiusM = 40f;
    internal const float RoamEdgeMarginM = 8f;
    internal const float RoamStepUpMaxM = 0.6f;
    internal const float RoamDropMaxM = 1.0f;
    internal const float RoamStepTypicalM = 0.5f;
    internal const float RoamSampleCoarseM = 2f;
    internal const float RoamSampleFineM = 0.5f;
    internal const float RoamSupportAboveAvatarM = 2f;
    internal const float RoamChatQuietS = 60f;
    internal const int RoamPauseMinS = 20;
    internal const int RoamPauseMaxS = 60;
    // Governor Linden (mainland / welcome-area owner)
    internal static readonly UUID RoamGovernorLinden = new("3d6181b0-6a4b-97ef-18d8-7227138161bb");

    static CancellationTokenSource roamCts;
    static Task roamTask;
    static volatile string roamPause; // null | "chat"
    static DateTime roamQuietSince = DateTime.MinValue, roamStartedAt;
    static float roamRadius = RoamDefaultRadiusM;
    static ulong roamStartHandle;
    static (double x, double y) roamStartGlobal;
    static readonly HashSet<ulong> roamHandles = new();
    static readonly List<string> roamCandidatesLog = new();
    static readonly HashSet<string> roamBad = new(StringComparer.Ordinal);
    static readonly object roamLock = new();
    static readonly HashSet<int> roamParcelsLogged = new();

    static bool RoamOn => roamTask != null && !roamTask.IsCompleted;

    internal enum RoamMapCell { Open, Water, Build }
    internal enum RoamPathReject { Ok, Wall, Ledge, Water, Edge, Restricted }
    internal readonly record struct RoamPrim(float X0, float Y0, float X1, float Y1, float Top);
    internal readonly record struct RoamPathSample(float Dist, float Z, bool Water, bool NearVoidEdge);

    // ---- coordinates (global metres on the 256 m region grid) ---------------------------------
    internal static (double x, double y) RoamToGlobal(ulong handle, float lx, float ly)
    {
        var o = SimGeo.Origin(handle);
        return (o.x + (double)lx, o.y + (double)ly);
    }
    internal static Vector3 RoamFromGlobal(ulong handle, double gx, double gy, float z = 0)
    {
        var o = SimGeo.Origin(handle);
        return new Vector3((float)(gx - o.x), (float)(gy - o.y), z);
    }

    // ---- region / edge / parcel / owner -------------------------------------------------------
    internal static bool RoamIsLindenOwner(UUID owner) => owner != UUID.Zero && owner == RoamGovernorLinden;

    internal static bool RoamNeighborAllowed(UUID startOwner, UUID neighborOwner, bool connected, bool adjacent) =>
        connected && adjacent && (
            (startOwner != UUID.Zero && startOwner == neighborOwner)
            || RoamIsLindenOwner(startOwner)
            || RoamIsLindenOwner(neighborOwner));

    // 8 m margin applies only on an edge that is void, or a non-connected / restricted neighbor
    internal static bool RoamTooCloseToVoidEdge(float localX, float localY, uint sx, uint sy, float margin,
        bool hasWest, bool hasEast, bool hasSouth, bool hasNorth)
    {
        if (localX < margin && !hasWest) return true;
        if (sx - localX < margin && !hasEast) return true;
        if (localY < margin && !hasSouth) return true;
        if (sy - localY < margin && !hasNorth) return true;
        return false;
    }

    // unknown parcel = allowed (only refuse when we know it is restricted or we are banned)
    internal static bool RoamParcelAllowsEntry(bool known, bool accessGroup, bool accessList, bool banned) =>
        !known || (!banned && !accessGroup && !accessList);

    // ---- support surface (terrain vs prim/floor tops) -----------------------------------------
    internal static float RoamSupportZ(float x, float y, float? terrain, float avatarZ, IEnumerable<RoamPrim> prims, float aboveAv = RoamSupportAboveAvatarM)
    {
        float cap = avatarZ + aboveAv;
        float? best = null;
        if (prims != null)
        {
            foreach (var p in prims)
            {
                if (x < p.X0 || x > p.X1 || y < p.Y0 || y > p.Y1) continue;
                if (p.Top > cap) continue;
                if (best == null || p.Top > best.Value) best = p.Top;
            }
        }
        if (terrain is float t && t <= cap)
            best = best == null ? t : MathF.Max(best.Value, t);
        return best ?? terrain ?? avatarZ;
    }

    internal static RoamPrim RoamPrimBox(float cx, float cy, float sx, float sy, float top) =>
        new(cx - sx * 0.5f, cy - sy * 0.5f, cx + sx * 0.5f, cy + sy * 0.5f, top);

    // ---- path check (stairs ok; walls / long drops / water / void edge are not) --------------
    // Coarse 2 m samples; when the support height changes, refine to ~0.5 m so a staircase
    // reads as many small rises (~0.5 m) instead of one jump.
    internal static List<float> RoamSampleDistances(float length, Func<float, float> zAt,
        float coarse = RoamSampleCoarseM, float fine = RoamSampleFineM, float refineIf = 0.25f)
    {
        length = MathF.Max(0, length);
        var ds = new List<float>();
        for (float d = 0; d < length - 1e-3f; d += coarse)
        {
            ds.Add(d);
            float n = MathF.Min(d + coarse, length);
            if (MathF.Abs(zAt(n) - zAt(d)) >= refineIf)
            {
                for (float e = d + fine; e < n - 1e-3f; e += fine) ds.Add(e);
            }
        }
        ds.Add(length);
        ds.Sort();
        // unique
        var uniq = new List<float>(ds.Count);
        foreach (var d in ds)
            if (uniq.Count == 0 || MathF.Abs(d - uniq[^1]) > 1e-3f) uniq.Add(d);
        return uniq;
    }

    internal static RoamPathReject RoamPathCheck(IReadOnlyList<RoamPathSample> samples, float stepUp = RoamStepUpMaxM, float drop = RoamDropMaxM)
    {
        if (samples == null || samples.Count == 0) return RoamPathReject.Ok;
        for (int i = 0; i < samples.Count; i++)
        {
            if (samples[i].NearVoidEdge) return RoamPathReject.Edge;
            if (samples[i].Water) return RoamPathReject.Water;
            if (i == 0) continue;
            float dz = samples[i].Z - samples[i - 1].Z;
            if (dz > stepUp) return RoamPathReject.Wall;
            if (dz < -drop) return RoamPathReject.Ledge;
        }
        return RoamPathReject.Ok;
    }

    internal static RoamPathReject RoamPathCheckAlong(float length, Func<float, float> zAt, Func<float, bool> waterAt, Func<float, bool> edgeAt)
    {
        var ds = RoamSampleDistances(length, zAt);
        var samples = ds.Select(d => new RoamPathSample(d, zAt(d), waterAt?.Invoke(d) == true, edgeAt?.Invoke(d) == true)).ToList();
        return RoamPathCheck(samples);
    }

    // ---- map pixel heuristic (optional; live fetch is best-effort) ----------------------------
    internal static RoamMapCell RoamClassifyMapPixel(byte r, byte g, byte b)
    {
        if (b > 80 && b >= g + 15 && b >= r + 25 && r < 140) return RoamMapCell.Water;
        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        if (max - min < 28 && (max < 70 || max > 170) && !(g > r + 15 && g > b)) return RoamMapCell.Build;
        return RoamMapCell.Open;
    }

    // ---- chat pause (nearby local chat only; never IMs or far / unknown-position chat) --------
    internal static bool RoamChatShouldPause(bool im, float dist) =>
        !im && dist >= 0f && dist <= WanderChatPauseRangeM;

    // ---- target scoring: prefer spots a few metres from other avatars, inside the radius -----
    internal static float RoamTargetScore(double gx, double gy, (double x, double y) start, float radius, IReadOnlyList<(double x, double y)> avatars)
    {
        float dx = (float)(gx - start.x), dy = (float)(gy - start.y);
        float fromStart = MathF.Sqrt(dx * dx + dy * dy);
        if (fromStart > radius) return float.NegativeInfinity;
        float best = 0;
        if (avatars != null)
        {
            foreach (var a in avatars)
            {
                float adx = (float)(gx - a.x), ady = (float)(gy - a.y);
                float d = MathF.Sqrt(adx * adx + ady * ady);
                // prefer ~6-20 m from someone; standing on them is bad
                float s = d < 3f ? -20f : d <= 20f ? 20f - MathF.Abs(d - 10f) : MathF.Max(0, 30f - d);
                if (s > best) best = s;
            }
        }
        return best + (radius - fromStart) * 0.02f;
    }

    static void RoamLog(string m) => Log("roam", m);

    static void StopRoamIfOn(string why)
    {
        if (!RoamOn) return;
        try
        {
            roamCts?.Cancel();
            try { client.Self.AutoPilotCancel(); } catch { }
            RoamLog("stopped: " + why);
        }
        catch (Exception ex) { RoamLog("stop error: " + ex.Message); }
    }

    static void RoamNoteChat(bool im, float dist, string name)
    {
        if (!RoamOn) return;
        if (!RoamChatShouldPause(im, dist)) return;
        roamPause = "chat";
        roamQuietSince = DateTime.Now;
        RoamLog($"chat pause: nearby chat from {name} ({dist:F0} m); resume after {RoamChatQuietS:0} s quiet");
    }

    // ---- command / MCP -----------------------------------------------------------------------
    static string RoamStatus()
    {
        if (!RoamOn) return "roam off";
        var sim = client.Network.CurrentSim;
        var me = client.Self.SimPosition;
        var g = sim != null ? RoamToGlobal(sim.Handle, me.X, me.Y) : (0, 0);
        string regions;
        lock (roamLock) regions = roamHandles.Count == 0 ? "-" : string.Join(",", roamHandles.Select(h =>
        {
            var o = SimGeo.Origin(h);
            return $"{o.x / 256},{o.y / 256}";
        }));
        var cand = "";
        lock (roamLock) cand = roamCandidatesLog.Count == 0 ? "no candidates" : string.Join("; ", roamCandidatesLog.Take(12));
        return $"roam on; region={sim?.Name ?? "?"} pos={V(me)} global=({g.x:F1},{g.y:F1}) radius={roamRadius:F0} m pause={(roamPause ?? "none")} " +
               $"area=[{regions}] started {roamStartedAt:HH:mm:ss} PT; candidates: {cand}";
    }

    static async Task<string> RoamCmd(string[] a)
    {
        var act = a.Length > 0 ? a[0].ToLowerInvariant() : "status";
        if (act is "status" or "") return RoamStatus();
        if (act is "off" or "stop") { if (!RoamOn) return "roam was not running"; StopRoamIfOn("command"); return "roam off"; }
        if (act is not ("on" or "start")) return "usage: roam on|off|status [radius_m=40]";
        if (!LoggedIn) return "not logged in";
        if (RoamOn) return "roam already running (" + RoamStatus() + ")";
        if (WanderOn) return "wander is running; 'wander stop' first (roam is for non-home sims)";
        if (InPeronaut) return "roam is for sims other than home (Peronaut); use wander there";
        if (client.Self.SittingOn != 0) return "refused: sitting; stand first";
        if (leash is { mode: "leash" }) return "refused: leashed; not starting a roam";
        if (followId != UUID.Zero) return "refused: already following; 'follow off' first";
        if (client.Self.SittingOn == 0 && !AoStateNow().active) return "refused: AO not active (" + AoStateNow().why + "); not starting a roam";
        float radius = RoamDefaultRadiusM;
        if (a.Length > 1 && F(a[1], out var r)) radius = Math.Clamp(r, 10f, 200f);
        roamRadius = radius;
        roamCts = new CancellationTokenSource();
        roamPause = null; roamBad.Clear(); roamCandidatesLog.Clear(); roamParcelsLogged.Clear();
        roamStartedAt = DateTime.Now;
        var ct = roamCts.Token;
        roamTask = Task.Run(() => RoamLoop(radius, ct));
        return $"roam on, radius {radius:F0} m (watching [roam] lines; 'roam status' / 'roam off')";
    }

    static List<(Simulator sim, bool allowed)> RoamCollectRegions(Simulator start, UUID startOwner)
    {
        var list = new List<(Simulator sim, bool allowed)>();
        if (start == null) return list;
        list.Add((start, true));
        try
        {
            foreach (var (sim, _) in ViewSims(true))
            {
                if (sim == null || sim.Handle == start.Handle) continue;
                bool adj = SimGeo.Adjacent(start.Handle, sim.Handle, start.SizeX == 0 ? 256u : (uint)start.SizeX, sim.SizeX == 0 ? 256u : (uint)sim.SizeX);
                bool ok = RoamNeighborAllowed(startOwner, sim.SimOwner, sim.Connected, adj);
                if (ok) list.Add((sim, true));
            }
        }
        catch (Exception ex) { RoamLog("neighbor list: " + ex.Message); }
        return list;
    }

    static (bool w, bool e, bool s, bool n) RoamAllowedEdges(ulong handle, HashSet<ulong> allowed)
    {
        var o = SimGeo.Origin(handle);
        bool has(int dx, int dy)
        {
            long x = (long)o.x + dx * 256L, y = (long)o.y + dy * 256L;
            if (x < 0 || y < 0) return false;
            return allowed.Contains(SimGeo.Handle((uint)x, (uint)y));
        }
        return (has(-1, 0), has(1, 0), has(0, -1), has(0, 1));
    }

    static List<RoamPrim> RoamPrimsFromSim(Simulator sim, float avatarZ)
    {
        var boxes = new List<RoamPrim>();
        if (sim == null) return boxes;
        try
        {
            foreach (var p in sim.ObjectsPrimitives.Values)
            {
                if (p == null || p.ParentID != 0 || p.PrimData.PCode != PCode.Prim) continue;
                if ((p.Flags & PrimFlags.Phantom) != 0) continue;
                var s = p.Scale;
                if (s.X < 0.8f || s.Y < 0.8f) continue; // walls / posts are not floors
                float top = p.Position.Z + s.Z * 0.5f;
                if (top > avatarZ + RoamSupportAboveAvatarM) continue;
                boxes.Add(RoamPrimBox(p.Position.X, p.Position.Y, s.X, s.Y, top));
            }
        }
        catch { }
        return boxes;
    }

    static bool RoamParcelOkAt(Simulator sim, Vector3 local)
    {
        try
        {
            if (sim?.Parcels == null || sim.Parcels.IsEmpty) return true;
            foreach (var kv in sim.Parcels)
            {
                var p = kv.Value; if (p == null) continue;
                if (local.X >= p.AABBMin.X && local.X <= p.AABBMax.X && local.Y >= p.AABBMin.Y && local.Y <= p.AABBMax.Y)
                {
                    var f = DecodeParcelFlags(p.Flags, p.MusicURL, p.Media.MediaURL);
                    lock (roamLock)
                    {
                        if (roamParcelsLogged.Add(p.LocalID))
                            RoamLog(ParcelInfoLine(p.Name, p.OwnerID.ToString(), p.Area, p.LocalID, f)
                                 + (f.AccessRestricted ? " (skip: no public access)" : ""));
                    }
                    return RoamParcelAllowsEntry(true, f.AccessGroup, f.AccessList, false);
                }
            }
        }
        catch { }
        return true; // unknown = do not refuse
    }

    static List<(double x, double y, float z, ulong handle, string why)> RoamPickTargets(
        (double x, double y) start, float radius, float avatarZ,
        Dictionary<ulong, (Simulator sim, List<RoamPrim> prims, float water, uint sx, uint sy)> regions,
        HashSet<ulong> allowed,
        IReadOnlyList<(double x, double y)> avatars,
        Func<(double x, double y, ulong handle), RoamMapCell> mapAt)
    {
        var picked = new List<(double x, double y, float z, ulong handle, string why)>();
        var scored = new List<(float score, double x, double y, float z, ulong handle, string why)>();
        const float step = 6f;
        for (float ox = -radius; ox <= radius; ox += step)
        for (float oy = -radius; oy <= radius; oy += step)
        {
            double gx = start.x + ox, gy = start.y + oy;
            float dist = MathF.Sqrt(ox * ox + oy * oy);
            if (dist > radius || dist < 6f) continue;
            ulong handle = 0;
            foreach (var h in allowed)
            {
                var loc = RoamFromGlobal(h, gx, gy);
                if (!regions.TryGetValue(h, out var info)) continue;
                if (SimGeo.InRegion(loc, info.sx, info.sy)) { handle = h; break; }
            }
            if (handle == 0) continue;
            var info2 = regions[handle];
            var local = RoamFromGlobal(handle, gx, gy);
            var edges = RoamAllowedEdges(handle, allowed);
            if (RoamTooCloseToVoidEdge(local.X, local.Y, info2.sx, info2.sy, RoamEdgeMarginM, edges.w, edges.e, edges.s, edges.n))
                continue;
            var sim = info2.sim;
            float? terrain = null;
            try { if (sim != null && sim.TerrainHeightAtPoint((int)local.X, (int)local.Y, out var th)) terrain = th; } catch { }
            float z = RoamSupportZ(local.X, local.Y, terrain, avatarZ, info2.prims);
            bool water = z <= info2.water + 0.15f && (terrain == null || terrain <= info2.water + 0.15f);
            if (water) continue;
            if (mapAt != null)
            {
                var cell = mapAt((gx, gy, handle));
                if (cell is RoamMapCell.Water or RoamMapCell.Build) continue;
            }
            if (sim != null && !RoamParcelOkAt(sim, local)) continue;
            // path from start (in start-handle frame, but we only have z along global line)
            var pathLen = dist;
            RoamPathReject path = RoamPathReject.Ok;
            try
            {
                path = RoamPathCheckAlong(pathLen, d =>
                {
                    float t = pathLen < 1e-3f ? 1 : d / pathLen;
                    double px = start.x + (gx - start.x) * t, py = start.y + (gy - start.y) * t;
                    ulong ph = handle;
                    foreach (var h in allowed)
                    {
                        var loc = RoamFromGlobal(h, px, py);
                        if (regions.TryGetValue(h, out var inf) && SimGeo.InRegion(loc, inf.sx, inf.sy)) { ph = h; break; }
                    }
                    if (!regions.TryGetValue(ph, out var inf2)) return z;
                    var loc2 = RoamFromGlobal(ph, px, py);
                    float? ter = null;
                    try { if (inf2.sim != null && inf2.sim.TerrainHeightAtPoint((int)loc2.X, (int)loc2.Y, out var th)) ter = th; } catch { }
                    return RoamSupportZ(loc2.X, loc2.Y, ter, avatarZ, inf2.prims);
                },
                d =>
                {
                    float t = pathLen < 1e-3f ? 1 : d / pathLen;
                    double px = start.x + (gx - start.x) * t, py = start.y + (gy - start.y) * t;
                    foreach (var h in allowed)
                    {
                        if (!regions.TryGetValue(h, out var inf)) continue;
                        var loc = RoamFromGlobal(h, px, py);
                        if (!SimGeo.InRegion(loc, inf.sx, inf.sy)) continue;
                        float? ter = null;
                        try { if (inf.sim != null && inf.sim.TerrainHeightAtPoint((int)loc.X, (int)loc.Y, out var th)) ter = th; } catch { }
                        float zz = RoamSupportZ(loc.X, loc.Y, ter, avatarZ, inf.prims);
                        return zz <= inf.water + 0.15f && (ter == null || ter <= inf.water + 0.15f);
                    }
                    return false;
                },
                d =>
                {
                    float t = pathLen < 1e-3f ? 1 : d / pathLen;
                    double px = start.x + (gx - start.x) * t, py = start.y + (gy - start.y) * t;
                    foreach (var h in allowed)
                    {
                        if (!regions.TryGetValue(h, out var inf)) continue;
                        var loc = RoamFromGlobal(h, px, py);
                        if (!SimGeo.InRegion(loc, inf.sx, inf.sy)) continue;
                        var e = RoamAllowedEdges(h, allowed);
                        return RoamTooCloseToVoidEdge(loc.X, loc.Y, inf.sx, inf.sy, RoamEdgeMarginM, e.w, e.e, e.s, e.n);
                    }
                    return true; // off the allowed area
                });
            }
            catch { path = RoamPathReject.Ok; }
            if (path != RoamPathReject.Ok) continue;
            float score = RoamTargetScore(gx, gy, start, radius, avatars);
            scored.Add((score, gx, gy, z, handle, path.ToString()));
        }
        foreach (var s in scored.OrderByDescending(x => x.score).ThenBy(x => x.x).Take(16))
            picked.Add((s.x, s.y, s.z, s.handle, $"score={s.score:F1} z={s.z:F2}"));
        return picked;
    }

    static async Task RoamLoop(float radius, CancellationToken ct)
    {
        var startSim = client.Network.CurrentSim;
        if (startSim == null) { RoamLog("no current region; not starting"); return; }
        roamStartHandle = startSim.Handle;
        var me0 = client.Self.SimPosition;
        roamStartGlobal = RoamToGlobal(startSim.Handle, me0.X, me0.Y);
        var startOwner = startSim.SimOwner;
        var collected = RoamCollectRegions(startSim, startOwner);
        lock (roamLock)
        {
            roamHandles.Clear();
            foreach (var (sim, ok) in collected) if (ok) roamHandles.Add(sim.Handle);
        }
        RoamLog($"start in {startSim.Name} at {V(me0)} global ({roamStartGlobal.x:F1},{roamStartGlobal.y:F1}), radius {radius:F0} m; " +
             $"area {string.Join(", ", collected.Where(c => c.allowed).Select(c => c.sim.Name))}");

        // best-effort combined map tiles
        try { await RoamFetchMapTiles(collected.Where(c => c.allowed).Select(c => c.sim), ct); }
        catch (Exception ex) { RoamLog("map tiles: " + ex.Message + " (continuing with terrain + prims)"); }

        var regions = new Dictionary<ulong, (Simulator sim, List<RoamPrim> prims, float water, uint sx, uint sy)>();
        foreach (var (sim, ok) in collected)
        {
            if (!ok || sim == null) continue;
            uint sx = sim.SizeX == 0 ? 256u : (uint)sim.SizeX, sy = sim.SizeY == 0 ? 256u : (uint)sim.SizeY;
            regions[sim.Handle] = (sim, RoamPrimsFromSim(sim, me0.Z), sim.WaterHeight, sx, sy);
        }

        var avatars = new List<(double x, double y)>();
        try
        {
            foreach (var a in Avatars())
            {
                if (a.av == null || a.av.ID == client.Self.AgentID) continue;
                var sim = client.Network.CurrentSim;
                var g = RoamToGlobal(sim.Handle, a.pos.X, a.pos.Y); // Avatars() positions are in current-region frame (neighbors already offset)
                avatars.Add(g);
            }
        }
        catch { }

        HashSet<ulong> allowed;
        lock (roamLock) allowed = new HashSet<ulong>(roamHandles);
        var cands = RoamPickTargets(roamStartGlobal, radius, me0.Z, regions, allowed, avatars, null);
        lock (roamLock)
        {
            roamCandidatesLog.Clear();
            foreach (var c in cands)
            {
                var loc = RoamFromGlobal(c.handle, c.x, c.y, c.z);
                var o = SimGeo.Origin(c.handle);
                roamCandidatesLog.Add($"({loc.X:F1},{loc.Y:F1},{c.z:F2}) grid {o.x / 256},{o.y / 256} {c.why}");
            }
        }
        if (cands.Count == 0) { RoamLog("no walkable candidates in radius; stopping"); return; }
        RoamLog("candidates: " + string.Join(" | ", roamCandidatesLog));

        int idx = 0, fails = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!LoggedIn) { RoamLog("logged out; stopping"); return; }
                if (client.Self.SittingOn != 0) { RoamLog("sat down; stopping"); return; }
                if (followId != UUID.Zero) { RoamLog("follow started; stopping"); return; }
                if (leash is { mode: "leash" }) { RoamLog("leashed; stopping"); return; }
                var cur = client.Network.CurrentSim;
                if (cur == null) { RoamLog("no region; stopping"); return; }
                lock (roamLock)
                {
                    if (!roamHandles.Contains(cur.Handle))
                    { RoamLog($"left the roam area ({cur.Name}); stopping (teleport or unexpected region)"); return; }
                }

                if (roamPause == "chat")
                {
                    var quiet = (DateTime.Now - roamQuietSince).TotalSeconds;
                    if (quiet >= RoamChatQuietS) { roamPause = null; RoamLog($"resume after {quiet:F0} s quiet"); }
                    else { await Task.Delay(1000, ct); continue; }
                }

                if (idx >= cands.Count) idx = 0;
                var pick = cands[idx++];
                var key = $"{pick.handle:X}:{pick.x:F0},{pick.y:F0}";
                if (roamBad.Contains(key))
                {
                    if (cands.All(c => roamBad.Contains($"{c.handle:X}:{c.x:F0},{c.y:F0}")))
                    { RoamLog("every candidate is marked bad; stopping"); return; }
                    continue;
                }
                float supportZ = pick.z;
                var destLocal = RoamFromGlobal(cur.Handle, pick.x, pick.y, supportZ);
                float avatarZ = client.Self.SimPosition.Z;
                var destHandle = SimGeo.HandleAt(cur.Handle, destLocal);
                if (destHandle == 0) destHandle = pick.handle;
                if (regions.TryGetValue(destHandle, out var inf))
                {
                    var locOn = RoamFromGlobal(inf.sim?.Handle ?? destHandle, pick.x, pick.y);
                    float? ter = null;
                    try { if (inf.sim != null && inf.sim.TerrainHeightAtPoint((int)locOn.X, (int)locOn.Y, out var th)) ter = th; } catch { }
                    supportZ = RoamSupportZ(locOn.X, locOn.Y, ter, avatarZ, inf.prims);
                    destLocal = RoamFromGlobal(cur.Handle, pick.x, pick.y, supportZ);
                }
                RoamLog($"walk to {V(destLocal)} (support z {supportZ:F2}, {HDist(client.Self.SimPosition, destLocal):F0} m)");
                bool ok = false;
                try
                {
                    // WalkPath is crossing-aware (Neighbors.cs / WalkLeg). no flying (default).
                    ok = await WalkPath(new List<Vector3> { destLocal }, ct, lastTol: 1.0f);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { RoamLog("walk error: " + ex.Message); ok = false; }
                if (ct.IsCancellationRequested) return;
                if (!ok)
                {
                    fails++;
                    roamBad.Add(key);
                    RoamLog($"stuck or failed at {V(client.Self.SimPosition)}; stepping back, marking this target bad ({key})");
                    try
                    {
                        var me = client.Self.SimPosition;
                        var step = me - Vector3.Normalize(new Vector3(destLocal.X - me.X, destLocal.Y - me.Y, 0)) * 1.5f;
                        var back = new Vector3(step.X, step.Y, me.Z);
                        try { client.Self.AutoPilotCancel(); } catch { }
                        AutoPilotTo(back);
                        await Task.Delay(1500, ct);
                        try { client.Self.AutoPilotCancel(); } catch { }
                    }
                    catch { }
                    if (fails > 12) { RoamLog("too many failed legs; stopping"); return; }
                    continue;
                }
                fails = 0;
                int pause = Random.Shared.Next(RoamPauseMinS, RoamPauseMaxS + 1);
                RoamLog($"arrived at {V(client.Self.SimPosition)}; pausing {pause} s");
                var until = DateTime.Now.AddSeconds(pause);
                while (DateTime.Now < until && !ct.IsCancellationRequested)
                {
                    if (roamPause == "chat") break;
                    if (client.Self.SittingOn != 0 || followId != UUID.Zero || !LoggedIn) break;
                    await Task.Delay(500, ct);
                }
            }
        }
        catch (OperationCanceledException) { RoamLog("cancelled"); }
        catch (Exception ex) { RoamLog("loop error: " + ex.GetBaseException().Message); }
        finally
        {
            try { client.Self.AutoPilotCancel(); } catch { }
            RoamLog("off");
        }
    }

    static async Task RoamFetchMapTiles(IEnumerable<Simulator> sims, CancellationToken ct)
    {
        var dir = "/workspace/secondlife/research/tiles";
        try { Directory.CreateDirectory(dir); } catch { return; }
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(8);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("galatay-roam/1");
        foreach (var sim in sims)
        {
            if (sim == null) continue;
            ct.ThrowIfCancellationRequested();
            var o = SimGeo.Origin(sim.Handle);
            uint gx = o.x / 256, gy = o.y / 256;
            var url = $"https://secondlife-maps-cdn.akamaized.net/map-1-{gx}-{gy}-objects.jpg";
            var dest = Path.Combine(dir, $"map-1-{gx}-{gy}-objects.jpg");
            try
            {
                if (File.Exists(dest) && DateTime.UtcNow - File.GetLastWriteTimeUtc(dest) < TimeSpan.FromHours(12))
                { RoamLog($"map tile cached {sim.Name} ({gx},{gy})"); continue; }
                var data = await http.GetByteArrayAsync(url, ct);
                var tmp = dest + ".tmp";
                await File.WriteAllBytesAsync(tmp, data, ct);
                File.Move(tmp, dest, true);
                RoamLog($"map tile {sim.Name} ({gx},{gy}) {data.Length} bytes");
            }
            catch (Exception ex) { RoamLog($"map tile {sim.Name} ({gx},{gy}): {ex.GetType().Name} {ex.Message}"); }
        }
    }
}
