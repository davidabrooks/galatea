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
    internal const float RoamArriveTolM = 2.0f;   // a leg ends within this horizontal distance of its spot (WalkPath lastTol)
    // Governor Linden (mainland / welcome-area owner). 2026-11-23: was ...-7227138161bb (wrong); the Ahern
    // welcome-area parcel owner in the log, and world.secondlife.com/resident/<id>, are ...-722652995cf1.
    internal static readonly UUID RoamGovernorLinden = new("3d6181b0-6a4b-97ef-18d8-722652995cf1");

    internal static CancellationTokenSource roamCts;   // internal: RoamLoopTests drive StopRoamIfOn
    internal static Task roamTask;
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

    internal static bool RoamOn => roamTask != null && !roamTask.IsCompleted;

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

    // pure (tested): world floor box of a (possibly rotated) prim: null when it is no floor (under 0.8 m across, or tilted
    // more than ~10 degrees so its top is a slope or a wall). Footprint = the rotated box's axis-aligned bounds.
    internal static RoamPrim? RoamPrimWorldBox(Vector3 pos, Quaternion rot, Vector3 scale)
    {
        var ax = Vector3.UnitX * rot * scale.X * 0.5f; var ay = Vector3.UnitY * rot * scale.Y * 0.5f; var az = Vector3.UnitZ * rot * scale.Z * 0.5f;
        float hx = MathF.Abs(ax.X) + MathF.Abs(ay.X) + MathF.Abs(az.X);
        float hy = MathF.Abs(ax.Y) + MathF.Abs(ay.Y) + MathF.Abs(az.Y);
        float hz = MathF.Abs(ax.Z) + MathF.Abs(ay.Z) + MathF.Abs(az.Z);
        // the face pointing up must be one of the box's faces, nearly level
        float best = 0; float thin = 0;
        foreach (var (v, half, other) in new[] { (ax, scale.X * 0.5f, (scale.Y, scale.Z)), (ay, scale.Y * 0.5f, (scale.X, scale.Z)), (az, scale.Z * 0.5f, (scale.X, scale.Y)) })
        {
            if (half < 1e-4f) continue;
            float up = MathF.Abs(v.Z) / half;
            if (up > best) { best = up; thin = MathF.Min(other.Item1, other.Item2); }
        }
        if (best < 0.985f || thin < 0.8f) return null;
        return new RoamPrim(pos.X - hx, pos.Y - hy, pos.X + hx, pos.Y + hy, pos.Z + hz);
    }

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
        // 0.5 m everywhere (was 2 m coarse): a 1 m wide wall or ledge between two coarse samples was never seen
        var ds = RoamSampleDistances(length, zAt, coarse: RoamSampleFineM);
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

    // ---- the main loop's decisions, pure (tested in RoamLoopTests) -----------------------------------------
    // 2026-11-23: pulled out of RoamCmd / RoamLoop unchanged so the loop's stop, re-plan and give-up rules are testable.
    internal enum RoamStop { None, LoggedOut, SatDown, Follow, Leashed, NoRegion, LeftArea }

    // checked at the top of every loop pass, in this order; a roam-owned sit (RoamLife.cs) is not a stop
    internal static RoamStop RoamStopCheck(bool loggedIn, bool sitting, bool roamOwnSit, bool following, bool leashed, bool hasRegion, bool inArea) =>
        !loggedIn ? RoamStop.LoggedOut
        : sitting && !roamOwnSit ? RoamStop.SatDown
        : following ? RoamStop.Follow
        : leashed ? RoamStop.Leashed
        : !hasRegion ? RoamStop.NoRegion
        : !inArea ? RoamStop.LeftArea
        : RoamStop.None;

    // why `roam on` is refused (null = start). ao/status are read only when that check is reached.
    internal static string RoamStartRefusal(bool loggedIn, bool roamOn, Func<string> status, bool wanderOn, bool inPeronaut,
        bool sitting, bool leashed, Func<(bool active, string why)> ao)
    {
        if (!loggedIn) return "not logged in";
        if (roamOn) return "roam already running (" + status() + ")";
        if (wanderOn) return "wander is running; 'wander stop' first (roam is for non-home sims)";
        if (inPeronaut) return "roam is for sims other than home (Peronaut); use wander there";
        if (sitting) return "refused: sitting; stand first";
        if (leashed) return "refused: leashed; not starting a roam";
        if (!sitting && !ao().active) return "refused: AO not active (" + ao().why + "); not starting a roam";
        return null;
    }

    // the loop's leg bookkeeping: an eject or two failed legs in a row re-plan from where she is (each eject once);
    // a good leg resets both counters; too many re-plans without a good leg, or too many failed legs, stop the roam.
    internal sealed class RoamLegBook
    {
        internal const int ReplanAfterFails = 2, MaxReplans = 4, MaxFails = 12;
        DateTime ejectSeen;
        internal int Fails { get; private set; }
        internal int Replans { get; private set; }
        internal RoamLegBook(DateTime ejectSeenAtStart) => ejectSeen = ejectSeenAtStart;
        internal bool Ejected(DateTime ejectAt) => ejectAt > ejectSeen;
        internal bool ReplanDue(DateTime ejectAt) => Ejected(ejectAt) || Fails == ReplanAfterFails;
        internal void EjectHandled(DateTime ejectAt) => ejectSeen = ejectAt;
        internal bool ReplanLimitHit() => ++Replans > MaxReplans;   // counts this re-plan; true = stop instead
        internal void Replanned() => Fails = 0;
        internal void LegFailed() => Fails++;
        internal bool TooManyFails => Fails > MaxFails;
        internal void LegOk() { Fails = 0; Replans = 0; }
    }

    internal static void StopRoamIfOn(string why)
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
        { var no = RoamStartRefusal(LoggedIn, RoamOn, RoamStatus, WanderOn, InPeronaut, LoggedIn && client.Self.SittingOn != 0,
              leash is { mode: "leash" }, () => { var s = AoStateNow(); return (s.active, s.why); }); if (no != null) return no; }
        { var mr = MoverClaim(MoverKind.Roam, "roam on"); if (mr != null) return mr; }   // Movers.cs: stops a follow / walk / wander sit
        float radius = RoamDefaultRadiusM;
        if (a.Length > 1 && F(a[1], out var r)) radius = Math.Clamp(r, 10f, 200f);
        roamRadius = radius;
        roamCts = new CancellationTokenSource();
        roamPause = null; roamBad.Clear(); lock (roamSeatBad) roamSeatBad.Clear(); lock (roamNoGo) roamNoGo.Clear(); roamOwnSit = false; roamCandidatesLog.Clear(); roamParcelsLogged.Clear();
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
            var all = sim.ObjectsPrimitives;
            foreach (var p in all.Values)
            {
                if (p == null || p.PrimData.PCode != PCode.Prim) continue;
                if ((p.Flags & PrimFlags.Phantom) != 0) continue;
                // 2026-11-23 Ahern: the plaza floor is a linkset child; roots only gave terrain (~39.4) under feet at ~40.
                Vector3 pos = p.Position; Quaternion rot = p.Rotation;
                if (p.ParentID != 0)
                {
                    if (!all.TryGetValue(p.ParentID, out var root) || root == null || root.ParentID != 0) continue;
                    pos = root.Position + p.Position * root.Rotation; rot = p.Rotation * root.Rotation;
                }
                var box = RoamPrimWorldBox(pos, rot, p.Scale);
                if (box is not { } b) continue; // walls / posts / tilted parts are not floors
                if (b.Top > avatarZ + RoamSupportAboveAvatarM) continue;
                boxes.Add(b);
            }
        }
        catch { }
        return boxes;
    }

    // pure (tested): world axis-aligned bounds of a (possibly rotated) prim
    internal static RoamObstacle RoamPrimBounds(Vector3 pos, Quaternion rot, Vector3 scale)
    {
        var ax = Vector3.UnitX * rot * scale.X * 0.5f; var ay = Vector3.UnitY * rot * scale.Y * 0.5f; var az = Vector3.UnitZ * rot * scale.Z * 0.5f;
        float hx = MathF.Abs(ax.X) + MathF.Abs(ay.X) + MathF.Abs(az.X);
        float hy = MathF.Abs(ax.Y) + MathF.Abs(ay.Y) + MathF.Abs(az.Y);
        float hz = MathF.Abs(ax.Z) + MathF.Abs(ay.Z) + MathF.Abs(az.Z);
        return new RoamObstacle(pos.X - hx, pos.Y - hy, pos.X + hx, pos.Y + hy, pos.Z - hz, pos.Z + hz);
    }

    // solid prims near her height that are not walkable floors (floors are in RoamPrimsFromSim): walls, posts, fences,
    // furniture, signs. Phantom prims and big things far above / below are left out. Bounds are axis-aligned, so a
    // diagonal wall blocks a little more than it is; that only costs a candidate, never a walk into it.
    static List<RoamObstacle> RoamObstaclesFromSim(Simulator sim, float avatarZ)
    {
        var res = new List<RoamObstacle>();
        if (sim == null) return res;
        try
        {
            var all = sim.ObjectsPrimitives;
            foreach (var p in all.Values)
            {
                if (p == null || p.PrimData.PCode != PCode.Prim || (p.Flags & PrimFlags.Phantom) != 0) continue;
                Vector3 pos = p.Position; Quaternion rot = p.Rotation;
                if (p.ParentID != 0)
                {
                    if (!all.TryGetValue(p.ParentID, out var root) || root == null || root.ParentID != 0) continue;
                    if ((root.Flags & PrimFlags.Phantom) != 0) continue;
                    pos = root.Position + p.Position * root.Rotation; rot = p.Rotation * root.Rotation;
                }
                if (RoamPrimWorldBox(pos, rot, p.Scale) is { } floor && floor.Top <= avatarZ + RoamSupportAboveAvatarM) continue;   // a floor
                var b = RoamPrimBounds(pos, rot, p.Scale);
                if (b.Top < avatarZ - 6f || b.Bottom > avatarZ + 4f) continue;
                if (b.X1 - b.X0 > 64f || b.Y1 - b.Y0 > 64f) continue;   // terrain-like megaprims: the support height covers them
                res.Add(b);
            }
        }
        catch { }
        return res;
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
        Func<float, float, float?> Terrain, IReadOnlyList<RoamPrim> Prims, Func<float, float, bool> ParcelOk,
        IReadOnlyList<RoamObstacle> Obstacles = null)
    {
        // support / water per 0.25 m cell (the path checks sample the same ground many times; prims are scanned once)
        internal readonly System.Collections.Concurrent.ConcurrentDictionary<(int x, int y, int az), (float z, bool water)> Memo = new();
    }
    // a solid object she would walk into (region-local axis-aligned bounds)
    internal readonly record struct RoamObstacle(float X0, float Y0, float X1, float Y1, float Bottom, float Top);
    internal const float RoamBodyRadiusM = 0.3f, RoamBodyHeightM = 1.8f;

    // pure (tested): where a straight walk a->b first enters the box grown by the body radius (t in 0..1), or null
    internal static float? RoamSegmentEntersBox(Vector2 a, Vector2 b, RoamObstacle o, float grow = RoamBodyRadiusM)
    {
        float x0 = o.X0 - grow, x1 = o.X1 + grow, y0 = o.Y0 - grow, y1 = o.Y1 + grow;
        float t0 = 0, t1 = 1; var d = b - a;
        bool Clip(float p, float q) { if (MathF.Abs(p) < 1e-9f) return q >= 0; float r = q / p; if (p < 0) { if (r > t1) return false; if (r > t0) t0 = r; } else { if (r < t0) return false; if (r < t1) t1 = r; } return true; }
        if (Clip(-d.X, a.X - x0) && Clip(d.X, x1 - a.X) && Clip(-d.Y, a.Y - y0) && Clip(d.Y, y1 - a.Y)) return t0;
        return null;
    }

    // pure (tested): an obstacle blocks a walk when it stands in the body's height band where she would meet it:
    // its top above a step (0.6 m over the ground there) and its bottom below her head. Starting inside one is ignored.
    internal static bool RoamObstacleBlocks(RoamObstacle o, float groundZ) =>
        o.Top > groundZ + RoamStepUpMaxM && o.Bottom < groundZ + RoamBodyHeightM;
    internal readonly record struct RoamCandidate(double X, double Y, float Z, ulong Handle, float Score, string Why);

    internal const string RoamRejOutside = "outside_area", RoamRejEdge = "edge_margin", RoamRejWater = "water",
        RoamRejMap = "map", RoamRejParcel = "parcel", RoamRejPathWall = "path_wall", RoamRejPathLedge = "path_ledge",
        RoamRejPathWater = "path_water", RoamRejPathEdge = "path_edge", RoamRejPathError = "path_error",
        RoamRejPathObstacle = "path_obstacle";

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

    // a straight walk between two global points runs into a solid object (walls, posts, fences, furniture...)
    internal static bool RoamWalkHitsObstacle(IReadOnlyDictionary<ulong, RoamRegionView> regions, (double x, double y) a, (double x, double y) b,
        float avatarZ, Func<RoamObstacle, bool> ignore = null)
    {
        foreach (var r in regions.Values)
        {
            if (r.Obstacles == null || r.Obstacles.Count == 0) continue;
            var la = RoamFromGlobal(r.Handle, a.x, a.y); var lb = RoamFromGlobal(r.Handle, b.x, b.y);
            var a2 = new Vector2(la.X, la.Y); var b2 = new Vector2(lb.X, lb.Y);
            foreach (var o in r.Obstacles)
            {
                if (ignore != null && ignore(o)) continue;
                var t = RoamSegmentEntersBox(a2, b2, o);
                if (t is not float tt || tt <= 1e-4f) continue;   // starting inside / against it: she is already there
                var hit = a2 + (b2 - a2) * tt;
                float g = RoamSupportAt(r, new Vector3(hit.X, hit.Y, 0), avatarZ);
                if (RoamObstacleBlocks(o, g)) return true;
            }
        }
        return false;
    }

    static (float z, bool water) RoamGroundAt(RoamRegionView r, Vector3 local, float avatarZ)
    {
        var key = ((int)MathF.Floor(local.X * 4), (int)MathF.Floor(local.Y * 4), (int)MathF.Floor(avatarZ * 2));
        if (r.Memo.TryGetValue(key, out var hit)) return hit;
        float? terrain = null;
        try { terrain = r.Terrain?.Invoke(local.X, local.Y); } catch { }
        float z = RoamSupportZ(local.X, local.Y, terrain, avatarZ, r.Prims);
        var v = (z, z <= r.Water + 0.15f && (terrain == null || terrain <= r.Water + 0.15f));
        r.Memo[key] = v;
        return v;
    }
    static float RoamSupportAt(RoamRegionView r, Vector3 local, float avatarZ) => RoamGroundAt(r, local, avatarZ).z;
    static bool RoamWaterAt(RoamRegionView r, Vector3 local, float avatarZ) => RoamGroundAt(r, local, avatarZ).water;

    internal static List<RoamCandidate> RoamSelectTargets(
        (double x, double y) start, float radius, float avatarZ,
        IReadOnlyDictionary<ulong, RoamRegionView> regions,
        IReadOnlyList<(double x, double y)> avatars,
        Func<(double x, double y, ulong handle), RoamMapCell> mapAt,
        RoamRejectCounts rejects, int keep = 16, IReadOnlyList<(double x, double y)> pois = null)
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
            if (path == RoamPathReject.Ok && RoamWalkHitsObstacle(regions, start, (gx, gy), avatarZ)) { rejects.Add(RoamRejPathObstacle); continue; }
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
            bool nearPoi = pois != null && pois.Any(q => { float pd = D2((gx, gy), q); return pd >= RoamPoiNearMinM && pd <= RoamPoiNearMaxM; });
            if (nearPoi) score += RoamPoiBonus;   // RoamLife.cs: a stop near something to look at
            scored.Add(new RoamCandidate(gx, gy, z, region.Handle, score, $"score={score:F1} z={z:F2}{(nearPoi ? " poi" : "")}"));
        }
        return scored.OrderByDescending(c => c.Score).ThenBy(c => c.X).ThenBy(c => c.Y).Take(keep).ToList();
    }

    static Dictionary<ulong, RoamRegionView> RoamViews(Dictionary<ulong, (Simulator sim, List<RoamPrim> prims, float water, uint sx, uint sy)> regions, float avatarZ)
    {
        var views = new Dictionary<ulong, RoamRegionView>();
        foreach (var (h, inf) in regions)
        {
            var sim = inf.sim;
            views[h] = new RoamRegionView(h, inf.sx, inf.sy, inf.water,
                (x, y) => sim != null && sim.TerrainHeightAtPoint((int)x, (int)y, out var th) ? th : null,
                inf.prims,
                (x, y) => sim == null || (RoamParcelOkAt(sim, new Vector3(x, y, 0)) && !RoamParcelIsNoGo(sim, x, y)),
                RoamObstaclesFromSim(sim, avatarZ));
        }
        return views;
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
        var views = RoamViews(regions, me0.Z);
        List<(double x, double y)> poiG = new();
        try
        {
            var near = startSim.ObjectsPrimitives.Values.Where(p => p != null && p.ParentID == 0 && p.PrimData.PCode == PCode.Prim && HDist(p.Position, me0) <= radius + 10f).ToList();
            await EnsureProperties(startSim, near.Where(p => p.Properties == null).Take(150).ToList());
            foreach (var q in RoamPoisNear(startSim, me0, radius + 10f)) if (q.Interest >= 1f) poiG.Add(RoamToGlobal(startSim.Handle, q.Pos.X, q.Pos.Y));
        }
        catch { }
        List<RoamCandidate> Plan((double x, double y) center, float az, string why)
        {
            var rj = new RoamRejectCounts();
            var cs = RoamSelectTargets(center, radius, az, views, avatars, null, rj, keep: 80, pois: poiG);
            lock (roamLock)
            {
                roamCandidatesLog.Clear();
                foreach (var c in cs)
                {
                    var loc = RoamFromGlobal(c.Handle, c.X, c.Y, c.Z);
                    var o = SimGeo.Origin(c.Handle);
                    roamCandidatesLog.Add($"({loc.X:F1},{loc.Y:F1},{c.Z:F2}) grid {o.x / 256},{o.y / 256} {c.Why}");
                }
            }
            var scan = $"scanned {rj.Considered} grid spots, kept {cs.Count}; rejected {rj}; area {string.Join(", ", collected.Select(c => c.sim.Name))}; " +
                       $"{poiG.Count} point(s) of interest, {cs.Count(c => c.Why.EndsWith(" poi"))} stop(s) near one";
            if (cs.Count == 0) { RoamLog($"no walkable candidates in radius ({why}); stopping ({scan})"); return cs; }
            RoamLog($"candidate scan ({why}): " + scan);
            RoamLog("candidates: " + string.Join(" | ", roamCandidatesLog.Take(16)) + (roamCandidatesLog.Count > 16 ? $" | ... {roamCandidatesLog.Count - 16} more" : ""));
            return cs;
        }
        var cands = Plan(roamStartGlobal, me0.Z, "start");
        if (cands.Count == 0) return;
        var book = new RoamLegBook(roamEjectAt);
        Vector3 lastStop = me0; ulong lastStopHandle = startSim.Handle;

        int legs = 0;
        var recent = new List<(double x, double y)> { roamStartGlobal };
        try
        {
            while (!ct.IsCancellationRequested)
            {
                bool loggedIn = LoggedIn;
                var cur = loggedIn ? client.Network.CurrentSim : null;
                bool inArea; lock (roamLock) inArea = cur != null && roamHandles.Contains(cur.Handle);
                switch (RoamStopCheck(loggedIn, loggedIn && client.Self.SittingOn != 0, roamOwnSit, followId != UUID.Zero,
                            leash is { mode: "leash" }, cur != null, inArea))
                {
                    case RoamStop.LoggedOut: RoamLog("logged out; stopping"); return;
                    case RoamStop.SatDown: RoamLog("sat down; stopping"); return;
                    case RoamStop.Follow: RoamLog("follow started; stopping"); return;
                    case RoamStop.Leashed: RoamLog("leashed; stopping"); return;
                    case RoamStop.NoRegion: RoamLog("no region; stopping"); return;
                    case RoamStop.LeftArea: RoamLog($"left the roam area ({cur.Name}); stopping (teleport or unexpected region)"); return;
                }

                if (roamPause == "chat")
                {
                    var quiet = (DateTime.Now - roamQuietSince).TotalSeconds;
                    if (quiet >= RoamChatQuietS) { roamPause = null; RoamLog($"resume after {quiet:F0} s quiet"); }
                    else { await Task.Delay(1000, ct); continue; }
                }

                var meNow = client.Self.SimPosition;
                var meG = RoamToGlobal(cur.Handle, meNow.X, meNow.Y);

                // 17:20:41 Peacock Park live: "You have been ejected from this land." moved her 25 m and 11 m down; roam kept
                // walking back toward the ejecting parcel (3 legs x 90 s timeouts). Now: that parcel is no-go for this roam
                // and the spots are planned again from where she is. Two failed legs in a row also re-plan from here.
                if (book.ReplanDue(roamEjectAt))
                {
                    string why;
                    if (book.Ejected(roamEjectAt))
                    {
                        book.EjectHandled(roamEjectAt);
                        var lsim = client.Network.Simulators.ToArray().FirstOrDefault(x => x.Handle == lastStopHandle);
                        var par = lsim != null ? ParcelUnderMe(lsim, lastStop) : null;
                        if (lsim != null && par != null) lock (roamNoGo) roamNoGo.Add((lsim.Handle, par.LocalID));
                        why = $"ejected ('{roamEjectMsg}') from parcel '{par?.Name ?? "?"}' (local {par?.LocalID.ToString() ?? "?"}), no-go for this roam";
                    }
                    else why = "two failed legs in a row";
                    if (book.ReplanLimitHit()) { RoamLog($"{why}; re-planned {RoamLegBook.MaxReplans} times without a good leg; stopping"); return; }
                    RoamLog($"{why}; re-planning from {V(meNow)}");
                    cands = Plan(meG, meNow.Z, "re-plan");
                    if (cands.Count == 0) return;
                    roamBad.Clear(); book.Replanned();
                }
                var avNow = new List<(double x, double y)>();
                try { foreach (var a in Avatars()) if (a.av != null && a.av.ID != client.Self.AgentID && a.dist >= 0) avNow.Add(RoamToGlobal(cur.Handle, a.pos.X, a.pos.Y)); } catch { }
                int pi = RoamChooseNext(cands, meG, recent.TakeLast(RoamRecentStops).ToList(), avNow, roamBad, roamRnd);
                if (pi < 0) { RoamLog("every candidate is marked bad or taken; stopping"); return; }
                var pick = cands[pi];
                var key = RoamKey(pick);
                float supportZ = pick.Z;
                var destLocal = RoamFromGlobal(cur.Handle, pick.X, pick.Y, supportZ);
                float avatarZ = meNow.Z;
                var reg = RoamRegionAt(views, pick.X, pick.Y, out var locOn);
                if (reg != null) { supportZ = RoamSupportAt(reg, locOn, avatarZ); destLocal = RoamFromGlobal(cur.Handle, pick.X, pick.Y, supportZ); }

                // RoamLife.cs: sometimes sit on a free seat near this leg first, then plan again from there
                try { if (await RoamMaybeSitDetour(destLocal, views, ct)) continue; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { RoamLog("sit check error: " + ex.GetBaseException().Message); }

                legs++;
                RoamLog($"walk to {V(destLocal)} (leg {legs}, support z {supportZ:F2}, {HDist(meNow, destLocal):F0} m{(pick.Why.EndsWith(" poi") ? ", near a point of interest" : "")}" +
                        $"{(pick.Handle != cur.Handle ? $", in {reg?.Handle.ToString("X") ?? "?"} (neighbor region)" : "")})");
                bool ok = false;
                try
                {
                    // WalkPath is crossing-aware (Neighbors.cs / WalkLeg). no flying (default). 2 m arrival: these are
                    // open spots, not seats (17:16 live: 1.8 m short of a blocked spot burned the 90 s leg timeout).
                    ok = await WalkPath(new List<Vector3> { destLocal }, ct, lastTol: RoamArriveTolM);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { RoamLog("walk error: " + ex.Message); ok = false; }
                if (ct.IsCancellationRequested) return;
                if (!ok)
                {
                    book.LegFailed();
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
                    if (book.TooManyFails) { RoamLog("too many failed legs; stopping"); return; }
                    continue;
                }
                book.LegOk();
                recent.Add((pick.X, pick.Y)); if (recent.Count > 12) recent.RemoveAt(0);
                int pause = roamRnd.Next(RoamPauseMinS, RoamPauseMaxS + 1);
                var arrived = client.Self.SimPosition;
                lastStop = arrived; lastStopHandle = client.Network.CurrentSim?.Handle ?? lastStopHandle;
                RoamLog($"arrived at {V(arrived)} ({HDist(arrived, destLocal):F1} m from the spot); pausing {pause} s");
                await RoamPauseWithLooks(pause, ct);
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
