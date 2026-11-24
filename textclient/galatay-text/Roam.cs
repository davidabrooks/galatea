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
    // Governor Linden (mainland / welcome-area owner). 2026-11-23: was ...-7227138161bb (wrong); the Ahern
    // welcome-area parcel owner in the log, and world.secondlife.com/resident/<id>, are ...-722652995cf1.
    internal static readonly UUID RoamGovernorLinden = new("3d6181b0-6a4b-97ef-18d8-722652995cf1");

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

    // region type from the handshake (RegionInfo3.ProductName), e.g. "Mainland / Full Region"
    internal static bool RoamIsMainland(string productName) =>
        !string.IsNullOrEmpty(productName) && productName.Contains("Mainland", StringComparison.OrdinalIgnoreCase);

    internal static bool RoamNeighborAllowed(UUID startOwner, UUID neighborOwner, bool connected, bool adjacent,
        bool startMainland = false, bool neighborMainland = false) =>
        connected && adjacent && (
            (startOwner != UUID.Zero && startOwner == neighborOwner)
            || RoamIsLindenOwner(startOwner)
            || RoamIsLindenOwner(neighborOwner)
            || startMainland || neighborMainland);

    static string RoamOwnerTag(UUID owner, string product) =>
        $"owner {(RoamIsLindenOwner(owner) ? "Governor Linden" : owner == UUID.Zero ? "unknown(zero)" : owner.ToString())}, type '{product}'";

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
        if (client.Self.SittingOn == 0 && !AoStateNow().active) return "refused: AO not active (" + AoStateNow().why + "); not starting a roam";
        { var mr = MoverClaim(MoverKind.Roam, "roam on"); if (mr != null) return mr; }   // Movers.cs: stops a follow / walk / wander sit
        float radius = RoamDefaultRadiusM;
        if (a.Length > 1 && F(a[1], out var r)) radius = Math.Clamp(r, 10f, 200f);
        roamRadius = radius;
        roamCts = new CancellationTokenSource();
        roamPause = null; roamBad.Clear(); roamCandidatesLog.Clear(); roamParcelsLogged.Clear();
        roamStartedAt = DateTime.Now;
        var ct = roamCts.Token;
        roamTask = Task.Run(() => { moverSelf.Value = MoverKind.Roam; return RoamLoop(radius, ct); });
        return $"roam on, radius {radius:F0} m (watching [roam] lines; 'roam status' / 'roam off')";
    }

    static List<(Simulator sim, bool allowed)> RoamCollectRegions(Simulator start, UUID startOwner)
    {
        var list = new List<(Simulator sim, bool allowed)>();
        if (start == null) return list;
        list.Add((start, true));
        RoamLog($"start region {start.Name}: {RoamOwnerTag(startOwner, start.ProductName)}");
        try
        {
            foreach (var (sim, _) in ViewSims(true))
            {
                if (sim == null || sim.Handle == start.Handle) continue;
                bool adj = SimGeo.Adjacent(start.Handle, sim.Handle, start.SizeX == 0 ? 256u : (uint)start.SizeX, sim.SizeX == 0 ? 256u : (uint)sim.SizeX);
                bool ok = RoamNeighborAllowed(startOwner, sim.SimOwner, sim.Connected, adj,
                    RoamIsMainland(start.ProductName), RoamIsMainland(sim.ProductName));
                RoamLog($"neighbor {sim.Name} ({SimGeo.Dir(start.Handle, sim.Handle)}): {RoamOwnerTag(sim.SimOwner, sim.ProductName)}, " +
                        $"connected={sim.Connected} adjacent={adj} -> {(ok ? "in the roam area" : "left out")}");
                if (ok) list.Add((sim, true));
            }
        }
        catch (Exception ex) { RoamLog("neighbor list: " + ex.Message); }
        return list;
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

    // ---- target selection (pure, tested; 2026-11-23) ------------------------------------------
    // 2026-11-23 17:09 Ahern: roam from <7,8> (SW corner) stopped at once with "no walkable candidates".
    // (1) The path check sampled the start itself, which sat inside the 8 m void-edge margin, so every
    //     path was refused as "Edge". The margin now only refuses a path that goes deeper toward a void
    //     edge than she already stands; walking away from the edge is always fine. Targets still keep 8 m.
    // (2) No neighbor was accepted (area "Ahern" only, though Dore / Morris / Bonifacio were connected):
    //     the Governor Linden UUID was wrong, so mainland never counted as Linden. Fixed, and mainland
    //     regions (ProductName "Mainland ...") also count as public, connected Linden land.
    internal sealed record RoamRegionView(ulong Handle, uint Sx, uint Sy, float Water,
        Func<float, float, float?> Terrain, IReadOnlyList<RoamPrim> Prims, Func<float, float, bool> ParcelOk);
    internal readonly record struct RoamCandidate(double X, double Y, float Z, ulong Handle, float Score, string Why);

    internal const string RoamRejOutside = "outside_area", RoamRejEdge = "edge_margin", RoamRejWater = "water",
        RoamRejMap = "map", RoamRejParcel = "parcel", RoamRejPathWall = "path_wall", RoamRejPathLedge = "path_ledge",
        RoamRejPathWater = "path_water", RoamRejPathEdge = "path_edge", RoamRejPathError = "path_error";

    internal sealed class RoamRejectCounts
    {
        readonly SortedDictionary<string, int> n = new(StringComparer.Ordinal);
        public int Considered;
        public void Add(string why) => n[why] = Get(why) + 1;
        public int Get(string why) => n.TryGetValue(why, out var c) ? c : 0;
        public int Total => n.Values.Sum();
        public override string ToString() => n.Count == 0 ? "none" : string.Join(" ", n.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    // how far a point is inside the margin of each void (not allowed) edge; 0 = not inside that margin
    internal static (float w, float e, float s, float n) RoamVoidEdgeDepth(float x, float y, uint sx, uint sy, float margin,
        bool hasWest, bool hasEast, bool hasSouth, bool hasNorth) =>
        (hasWest ? 0 : MathF.Max(0, margin - x), hasEast ? 0 : MathF.Max(0, margin - (sx - x)),
         hasSouth ? 0 : MathF.Max(0, margin - y), hasNorth ? 0 : MathF.Max(0, margin - (sy - y)));

    // a path sample is too close to a void edge only when it is deeper in that edge's margin than the start
    internal static bool RoamPathDeeperIntoEdge((float w, float e, float s, float n) sample, (float w, float e, float s, float n) start, float slack = 0.05f) =>
        sample.w > start.w + slack || sample.e > start.e + slack || sample.s > start.s + slack || sample.n > start.n + slack;

    internal static (bool w, bool e, bool s, bool n) RoamAllowedEdges(ulong handle, ICollection<ulong> allowed)
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

    static RoamRegionView RoamRegionAt(IReadOnlyDictionary<ulong, RoamRegionView> regions, double gx, double gy, out Vector3 local)
    {
        foreach (var r in regions.Values)
        {
            var loc = RoamFromGlobal(r.Handle, gx, gy);
            if (SimGeo.InRegion(loc, r.Sx, r.Sy)) { local = loc; return r; }
        }
        local = default;
        return null;
    }

    static float RoamSupportAt(RoamRegionView r, Vector3 local, float avatarZ)
    {
        float? terrain = null;
        try { terrain = r.Terrain?.Invoke(local.X, local.Y); } catch { }
        return RoamSupportZ(local.X, local.Y, terrain, avatarZ, r.Prims);
    }

    static bool RoamWaterAt(RoamRegionView r, Vector3 local, float avatarZ)
    {
        float? terrain = null;
        try { terrain = r.Terrain?.Invoke(local.X, local.Y); } catch { }
        float z = RoamSupportZ(local.X, local.Y, terrain, avatarZ, r.Prims);
        return z <= r.Water + 0.15f && (terrain == null || terrain <= r.Water + 0.15f);
    }

    internal static List<RoamCandidate> RoamSelectTargets(
        (double x, double y) start, float radius, float avatarZ,
        IReadOnlyDictionary<ulong, RoamRegionView> regions,
        IReadOnlyList<(double x, double y)> avatars,
        Func<(double x, double y, ulong handle), RoamMapCell> mapAt,
        RoamRejectCounts rejects, int keep = 16)
    {
        rejects ??= new RoamRejectCounts();
        var scored = new List<RoamCandidate>();
        var allowed = regions.Keys.ToList();
        var startRegion = RoamRegionAt(regions, start.x, start.y, out var startLocal);
        var startDepth = (0f, 0f, 0f, 0f);
        if (startRegion != null)
        {
            var se = RoamAllowedEdges(startRegion.Handle, allowed);
            startDepth = RoamVoidEdgeDepth(startLocal.X, startLocal.Y, startRegion.Sx, startRegion.Sy, RoamEdgeMarginM, se.w, se.e, se.s, se.n);
        }
        const float step = 6f;
        for (float ox = -radius; ox <= radius; ox += step)
        for (float oy = -radius; oy <= radius; oy += step)
        {
            double gx = start.x + ox, gy = start.y + oy;
            float dist = MathF.Sqrt(ox * ox + oy * oy);
            if (dist > radius || dist < 6f) continue;
            rejects.Considered++;
            var region = RoamRegionAt(regions, gx, gy, out var local);
            if (region == null) { rejects.Add(RoamRejOutside); continue; }
            var edges = RoamAllowedEdges(region.Handle, allowed);
            if (RoamTooCloseToVoidEdge(local.X, local.Y, region.Sx, region.Sy, RoamEdgeMarginM, edges.w, edges.e, edges.s, edges.n))
            { rejects.Add(RoamRejEdge); continue; }
            float z = RoamSupportAt(region, local, avatarZ);
            if (RoamWaterAt(region, local, avatarZ)) { rejects.Add(RoamRejWater); continue; }
            if (mapAt != null && mapAt((gx, gy, region.Handle)) is RoamMapCell.Water or RoamMapCell.Build) { rejects.Add(RoamRejMap); continue; }
            bool parcelOk = true;
            try { parcelOk = region.ParcelOk?.Invoke(local.X, local.Y) ?? true; } catch { }
            if (!parcelOk) { rejects.Add(RoamRejParcel); continue; }

            float pathLen = dist;
            (double x, double y) At(float d)
            {
                float t = pathLen < 1e-3f ? 1 : d / pathLen;
                return (start.x + (gx - start.x) * t, start.y + (gy - start.y) * t);
            }
            RoamPathReject path;
            try
            {
                path = RoamPathCheckAlong(pathLen,
                    d => { var p = At(d); var r = RoamRegionAt(regions, p.x, p.y, out var l); return r == null ? z : RoamSupportAt(r, l, avatarZ); },
                    d => { var p = At(d); var r = RoamRegionAt(regions, p.x, p.y, out var l); return r != null && RoamWaterAt(r, l, avatarZ); },
                    d =>
                    {
                        var p = At(d);
                        var r = RoamRegionAt(regions, p.x, p.y, out var l);
                        if (r == null) return true; // off the allowed area
                        var e = RoamAllowedEdges(r.Handle, allowed);
                        var depth = RoamVoidEdgeDepth(l.X, l.Y, r.Sx, r.Sy, RoamEdgeMarginM, e.w, e.e, e.s, e.n);
                        var from = startRegion != null && r.Handle == startRegion.Handle ? startDepth : (0f, 0f, 0f, 0f);
                        return RoamPathDeeperIntoEdge(depth, from);
                    });
            }
            catch { path = RoamPathReject.Restricted; rejects.Add(RoamRejPathError); continue; }
            if (path != RoamPathReject.Ok)
            {
                rejects.Add(path switch
                {
                    RoamPathReject.Wall => RoamRejPathWall,
                    RoamPathReject.Ledge => RoamRejPathLedge,
                    RoamPathReject.Water => RoamRejPathWater,
                    RoamPathReject.Edge => RoamRejPathEdge,
                    _ => RoamRejPathError,
                });
                continue;
            }
            float score = RoamTargetScore(gx, gy, start, radius, avatars);
            scored.Add(new RoamCandidate(gx, gy, z, region.Handle, score, $"score={score:F1} z={z:F2}"));
        }
        return scored.OrderByDescending(c => c.Score).ThenBy(c => c.X).ThenBy(c => c.Y).Take(keep).ToList();
    }

    static List<RoamCandidate> RoamPickTargets((double x, double y) start, float radius, float avatarZ,
        Dictionary<ulong, (Simulator sim, List<RoamPrim> prims, float water, uint sx, uint sy)> regions,
        IReadOnlyList<(double x, double y)> avatars, RoamRejectCounts rejects)
    {
        var views = new Dictionary<ulong, RoamRegionView>();
        foreach (var (h, inf) in regions)
        {
            var sim = inf.sim;
            views[h] = new RoamRegionView(h, inf.sx, inf.sy, inf.water,
                (x, y) => sim != null && sim.TerrainHeightAtPoint((int)x, (int)y, out var th) ? th : null,
                inf.prims,
                (x, y) => sim == null || RoamParcelOkAt(sim, new Vector3(x, y, 0)));
        }
        return RoamSelectTargets(start, radius, avatarZ, views, avatars, null, rejects);
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

        var rejects = new RoamRejectCounts();
        var cands = RoamPickTargets(roamStartGlobal, radius, me0.Z, regions, avatars, rejects);
        lock (roamLock)
        {
            roamCandidatesLog.Clear();
            foreach (var c in cands)
            {
                var loc = RoamFromGlobal(c.Handle, c.X, c.Y, c.Z);
                var o = SimGeo.Origin(c.Handle);
                roamCandidatesLog.Add($"({loc.X:F1},{loc.Y:F1},{c.Z:F2}) grid {o.x / 256},{o.y / 256} {c.Why}");
            }
        }
        var scan = $"scanned {rejects.Considered} grid spots, kept {cands.Count}; rejected {rejects}; area {string.Join(", ", collected.Select(c => c.sim.Name))}";
        if (cands.Count == 0) { RoamLog($"no walkable candidates in radius; stopping ({scan})"); return; }
        RoamLog("candidate scan: " + scan);
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
                var key = $"{pick.Handle:X}:{pick.X:F0},{pick.Y:F0}";
                if (roamBad.Contains(key))
                {
                    if (cands.All(c => roamBad.Contains($"{c.Handle:X}:{c.X:F0},{c.Y:F0}")))
                    { RoamLog("every candidate is marked bad; stopping"); return; }
                    continue;
                }
                float supportZ = pick.Z;
                var destLocal = RoamFromGlobal(cur.Handle, pick.X, pick.Y, supportZ);
                float avatarZ = client.Self.SimPosition.Z;
                var destHandle = SimGeo.HandleAt(cur.Handle, destLocal);
                if (destHandle == 0) destHandle = pick.Handle;
                if (regions.TryGetValue(destHandle, out var inf))
                {
                    var locOn = RoamFromGlobal(inf.sim?.Handle ?? destHandle, pick.X, pick.Y);
                    float? ter = null;
                    try { if (inf.sim != null && inf.sim.TerrainHeightAtPoint((int)locOn.X, (int)locOn.Y, out var th)) ter = th; } catch { }
                    supportZ = RoamSupportZ(locOn.X, locOn.Y, ter, avatarZ, inf.prims);
                    destLocal = RoamFromGlobal(cur.Handle, pick.X, pick.Y, supportZ);
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
