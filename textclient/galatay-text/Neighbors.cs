// Neighbors.cs (2026-10-06, David: sim border / neighbor-region support)
// LibreMetaverse already speaks the multi-region protocol behind Settings.Agent.MultipleSims: EnableSimulator (event
// queue) opens a child-agent UDP circuit to each neighbor, EstablishAgentCommunication gives it a seed cap / event queue,
// and CrossedRegion moves the root agent into an already-connected neighbor (CompleteAgentMovement there). This file:
//   - turns that on (GT_MULTI_SIMS, default on) and tracks the neighbors (`regions`)
//   - SimGeo: pure region-grid math (handles, offsets into the current region's frame, which region a point is in)
//   - neighbor avatars/objects/coarse map for `nearby`, `avatars`, `crowd`, the MCP nearby tool and look exports, in the
//     current region's local frame (a neighbor 256 m east adds <256,0,0>; positions may read < 0 or > 256)
//   - CrossingWatch: a small state machine over one border crossing (CrossedRegion -> region change -> settled), so the AO
//     guard / walks / follow don't misread the few seconds after a crossing (attachments and her own avatar object are
//     re-sent by the new region) and a crossing that never completes is logged and recovered.
// Memory: neighbors stream what is inside her draw distance (Far, sent to the root region and passed on to the child
// agents); `regions` shows objects per region and the process heap. GT_NEIGHBOR_MAX_ROOTS caps the linksets kept per
// neighbor (farthest from her dropped first, never an avatar's attachments or a seat in use; halved while the managed
// heap is over 448 MB of the 768 MB cap). Dropped objects are re-requested when she enters that region.
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using LibreMetaverse;
using LibreMetaverse.Packets;

namespace GalatayText;

// ---- pure region-grid math (selftested) --------------------------------------------------------------------------
internal static class SimGeo
{
    public const uint Grid = 256;
    public static (uint x, uint y) Origin(ulong handle) { Utils.LongToUInts(handle, out var x, out var y); return (x, y); }
    public static ulong Handle(uint gx, uint gy) => Utils.UIntsToLong(gx, gy);
    // add to a position in region `other` to express it in region `cur`'s local frame
    public static Vector3 Offset(ulong cur, ulong other)
    {
        var (cx, cy) = Origin(cur); var (ox, oy) = Origin(other);
        return new Vector3((float)((double)ox - cx), (float)((double)oy - cy), 0f);
    }
    public static Vector3 ToCur(ulong cur, ulong other, Vector3 p) => p + Offset(cur, other);
    // region on the 256 m grid that contains a point given in cur's local frame (0 = off the grid)
    public static ulong HandleAt(ulong cur, Vector3 local)
    {
        var (cx, cy) = Origin(cur);
        double gx = cx + (double)local.X, gy = cy + (double)local.Y;
        if (gx < 0 || gy < 0 || gx >= uint.MaxValue || gy >= uint.MaxValue) return 0;
        return Handle((uint)(Math.Floor(gx / Grid) * Grid), (uint)(Math.Floor(gy / Grid) * Grid));
    }
    // compass direction of region `other` seen from `cur` ("" = same region)
    public static string Dir(ulong cur, ulong other)
    {
        var o = Offset(cur, other);
        return (o.Y > 0 ? "N" : o.Y < 0 ? "S" : "") + (o.X > 0 ? "E" : o.X < 0 ? "W" : "");
    }
    // the two region squares touch (edge or corner) and are not the same region
    public static bool Adjacent(ulong a, ulong b, uint sa = Grid, uint sb = Grid)
    {
        if (a == b) return false;
        var (ax, ay) = Origin(a); var (bx, by) = Origin(b);
        bool ox = (long)bx <= (long)ax + sa && (long)ax <= (long)bx + sb;
        bool oy = (long)by <= (long)ay + sa && (long)ay <= (long)by + sb;
        return ox && oy;
    }
    public static bool InRegion(Vector3 p, uint sx = Grid, uint sy = Grid) => p.X >= 0 && p.X < sx && p.Y >= 0 && p.Y < sy;
    // distance to the nearest edge of her own region (horizontal)
    public static float EdgeDist(Vector3 p, uint sx = Grid, uint sy = Grid) => MathF.Min(MathF.Min(p.X, sx - p.X), MathF.Min(p.Y, sy - p.Y));
    // horizontal distance from a cur-frame point to region `other`'s square (0 = inside it)
    public static float DistToRegion(ulong cur, ulong other, Vector3 p, uint sx = Grid, uint sy = Grid)
    {
        var o = Offset(cur, other);
        float dx = MathF.Max(0, MathF.Max(o.X - p.X, p.X - (o.X + sx)));
        float dy = MathF.Max(0, MathF.Max(o.Y - p.Y, p.Y - (o.Y + sy)));
        return MathF.Sqrt(dx * dx + dy * dy);
    }
    // distance from a to the border a straight walk to b leaves her region through (+inf = b is inside the region)
    public static float ExitDist(Vector3 a, Vector3 b, uint sx = Grid, uint sy = Grid) => ExitBorder(a, b, sx, sy) switch
    {
        "W" => a.X, "E" => sx - a.X, "S" => a.Y, "N" => sy - a.Y, _ => float.PositiveInfinity
    };
    // the sim-side autopilot (LibreMetaverse's AutoPilot = the "autopilot" GenericMessage) clamps its goal to the region,
    // so it stops at the border (live 09:28: STUCK at x 0.9 walking west). Within this distance of the exit border she
    // walks forward with her own controls until the region hands her over.
    public const float PushDist = 3f;
    public static bool NeedsPush(Vector3 me, Vector3 target, uint sx = Grid, uint sy = Grid) => ExitDist(me, target, sx, sy) < PushDist;
    // which border a straight walk from a to b (cur frame) leaves her region through: null = stays inside
    public static string ExitBorder(Vector3 a, Vector3 b, uint sx = Grid, uint sy = Grid)
    {
        if (InRegion(b, sx, sy)) return null;
        float t = 1f; string side = null;
        void Try(float num, float den, string s) { if (MathF.Abs(den) < 1e-6f) return; var tt = num / den; if (tt >= 0 && tt < t) { t = tt; side = s; } }
        var d = b - a;
        if (b.X < 0) Try(-a.X, d.X, "W"); if (b.X >= sx) Try(sx - a.X, d.X, "E");
        if (b.Y < 0) Try(-a.Y, d.Y, "S"); if (b.Y >= sy) Try(sy - a.Y, d.Y, "N");
        return side;
    }
}

// ---- one border crossing at a time (selftested) ----------------------------------------------------------------------
internal sealed class CrossingWatch
{
    public enum Phase { Idle, Crossing, Settling, Failed }
    public Phase State { get; private set; } = Phase.Idle;
    public DateTime Since { get; private set; }
    public ulong From { get; private set; }
    public ulong To { get; private set; }
    public bool AoBefore { get; private set; }
    public bool SeatedBefore { get; private set; }
    public UUID FollowBefore { get; private set; }
    public int Crossings { get; private set; }
    public int Failures { get; private set; }
    public string Last { get; private set; } = "-";
    public double CrossTimeoutS = 30, SettleCapS = 20, FailedHoldS = 10;
    DateTime crossBegan;

    string Set(Phase p, DateTime t, string why) { State = p; Since = t; Last = $"{t:HH:mm:ss} {why}"; return why; }

    // CrossedRegion arrived (UDP packet or event queue; both can arrive for one crossing)
    public string Begin(DateTime t, ulong from, ulong to, bool ao, bool seated, UUID following)
    {
        if (State == Phase.Crossing && To == to) return null;               // duplicate notice
        if (State == Phase.Settling && To == to) return null;               // late duplicate after the region change
        From = from; To = to; AoBefore = ao; SeatedBefore = seated; FollowBefore = following; crossBegan = t;
        return Set(Phase.Crossing, t, $"crossing {SimGeo.Dir(from, to)} into region {to:X}");
    }

    // CurrentSim changed. teleport = a teleport was in progress (not a crossing).
    public string SimChanged(DateTime t, ulong from, ulong to, bool teleport, bool ao, bool seated, UUID following)
    {
        if (teleport) { if (State == Phase.Idle) return null; return Set(Phase.Idle, t, "teleport: crossing watch reset"); }
        if (State != Phase.Crossing)
        {   // CrossedRegion not seen (or a stale state): an adjacent region change without a teleport is a crossing too
            if (from == 0 || !SimGeo.Adjacent(from, to)) { if (State == Phase.Idle) return null; return Set(Phase.Idle, t, "non-adjacent region change: reset"); }
            From = from; AoBefore = ao; SeatedBefore = seated; FollowBefore = following; crossBegan = t;
        }
        To = to; Crossings++;
        return Set(Phase.Settling, t, $"in the new region {SimGeo.Dir(From, to)} after {(t - crossBegan).TotalSeconds:F1} s: settling");
    }

    // the library gave up (RegionCrossed with no new region): she stays in the old one
    public string Failed(DateTime t, string why) { Failures++; return Set(Phase.Failed, t, "crossing FAILED: " + why); }

    // every tick: selfSeen = her avatar object exists in the new region; hudSeen = the AO HUD (or nothing to wait for)
    public string Observe(DateTime t, bool selfSeen, bool hudSeen)
    {
        var age = (t - Since).TotalSeconds;
        switch (State)
        {
            case Phase.Crossing when age > CrossTimeoutS:
                Failures++; return Set(Phase.Failed, t, $"crossing STUCK: no region change {age:F0} s after CrossedRegion");
            case Phase.Settling when selfSeen && (hudSeen || !AoBefore):
                return Set(Phase.Idle, t, $"crossing settled in {(t - crossBegan).TotalSeconds:F1} s");
            case Phase.Settling when age > SettleCapS:
                return Set(Phase.Idle, t, $"crossing settle cap {SettleCapS:F0} s reached (avatar seen {selfSeen}, AO HUD seen {hudSeen})");
            case Phase.Failed when age > FailedHoldS:
                return Set(Phase.Idle, t, "after a failed crossing: idle");
        }
        return null;
    }

    // the few seconds when her own object/attachments are not yet in the new region: don't trust worn/AO checks
    public bool Grace => State is Phase.Crossing or Phase.Settling;
    public bool AssumeAo => Grace && AoBefore && !SeatedBefore;
}

public static partial class Program
{
    static readonly CrossingWatch crossing = new();
    static bool MultiSims => client?.Settings?.Agent?.MultipleSims == true;
    static int NeighborMaxRoots => int.TryParse(Env("GT_NEIGHBOR_MAX_ROOTS", "4000"), out var n) ? n : 4000;
    // per region handle: local ids dropped by the neighbor cap, re-requested when she enters that region
    static readonly ConcurrentDictionary<ulong, HashSet<uint>> droppedBy = new();
    static volatile bool teleportActive; static DateTime teleportActiveAt = DateTime.MinValue;
    // her state as of the last quiet tick (no crossing): the library switches regions inside its own CrossedRegion handler,
    // before ours runs, so "AO on / seated / following" must come from before the switch (live 09:23: the AO read as off
    // in the new region -> the walk re-attached the HUD)
    static (bool ao, bool seated, UUID follow, DateTime t) preCross = (false, false, UUID.Zero, DateTime.MinValue);
    static (bool ao, bool seated, UUID follow) PreCrossState()
    {
        var p = preCross;
        if ((DateTime.Now - p.t).TotalSeconds < 5) return (p.ao, p.seated, p.follow);
        return (false, client.Self.SittingOn != 0, followId);   // no recent snapshot: AO unknown (never assume it on)
    }
    // per region handle: the latest coarse (map) avatar positions, region-local
    static readonly ConcurrentDictionary<ulong, IReadOnlyDictionary<UUID, Vector3>> coarseBy = new();

    static Simulator[] SimsSnapshot()
    {
        for (int i = 0; i < 3; i++) { try { return client.Network.Simulators.ToArray(); } catch (ArgumentException) { } catch (InvalidOperationException) { } }
        return Array.Empty<Simulator>();
    }

    // the current region first (offset 0), then every connected neighbor with its offset into the current frame
    static List<(Simulator sim, Vector3 off)> ViewSims(bool neighbors = true)
    {
        var res = new List<(Simulator, Vector3)>();
        var cur = client?.Network?.CurrentSim; if (cur == null) return res;
        res.Add((cur, Vector3.Zero));
        if (!neighbors || !MultiSims) return res;
        foreach (var s in SimsSnapshot())
            if (s != null && s != cur && s.Connected && s.Handle != 0 && s.Handle != cur.Handle) res.Add((s, SimGeo.Offset(cur.Handle, s.Handle)));
        return res;
    }

    // avatars streamed by neighbor regions (not in the current one), position in the current frame, nearest first
    static List<(Avatar av, Simulator sim, Vector3 pos, float dist)> NeighborAvatars(float maxDist = float.MaxValue)
    {
        var res = new List<(Avatar, Simulator, Vector3, float)>();
        var cur = client?.Network?.CurrentSim; if (cur == null) return res;
        var here = cur.ObjectsAvatars.Values.Where(a => a != null).Select(a => a.ID).ToHashSet();
        var me = client.Self.SimPosition;
        foreach (var (sim, off) in ViewSims().Skip(1))
            foreach (var a in sim.ObjectsAvatars.Values)
            {
                if (a == null || a.ID == client.Self.AgentID || here.Contains(a.ID)) continue;
                if (a.ParentID != 0 && !sim.ObjectsPrimitives.ContainsKey(a.ParentID)) continue;   // seat unknown: position unknown
                var p = PositionHelper.GetAvatarPosition(sim, a) + off; var d = Vector3.Distance(p, me);
                if (d <= maxDist && !res.Any(r => r.Item1.ID == a.ID)) res.Add((a, sim, p, d));
            }
        return res.OrderBy(r => r.Item4).ToList();
    }

    // an avatar by id in any region we hold (current first): its region and position in the current frame
    static (Avatar av, Simulator sim, Vector3 pos)? FindAvatarAnySim(UUID id)
    {
        foreach (var (sim, off) in ViewSims())
        {
            var av = sim.ObjectsAvatars.Values.FirstOrDefault(a => a != null && a.ID == id);
            if (av == null || (av.ParentID != 0 && !sim.ObjectsPrimitives.ContainsKey(av.ParentID))) continue;
            return (av, sim, PositionHelper.GetAvatarPosition(sim, av) + off);
        }
        return null;
    }

    // walk forward with her own controls (AtPos, facing the target) until the current region changes, max 6 s
    static async Task<bool> PushAcrossBorder(Vector3 target, string label, CancellationToken ct)
    {
        var cur = client.Network.CurrentSim; if (cur == null) return false;
        var h0 = cur.Handle; var mv = client.Self.Movement; var me = client.Self.SimPosition;
        client.Self.AutoPilotCancel();
        Log("walk", $"{label}: {SimGeo.ExitDist(me, target):F1} m from the {SimGeo.ExitBorder(me, target)} border: walking across with her own controls");
        var until = DateTime.Now.AddSeconds(6);
        try
        {
            while (DateTime.Now < until && client.Network.CurrentSim?.Handle == h0 && crossing.State != CrossingWatch.Phase.Crossing)
            {
                var p = client.Self.SimPosition;
                mv.TurnToward(new Vector3(target.X, target.Y, p.Z));
                mv.AtPos = true; mv.SendUpdate(true);
                await Task.Delay(200, ct);
            }
        }
        finally { mv.AtPos = false; try { mv.SendUpdate(true); } catch { } }
        for (int i = 0; i < 20 && crossing.State == CrossingWatch.Phase.Crossing; i++) await Task.Delay(250, ct);
        bool ok = client.Network.CurrentSim?.Handle != h0;
        Log("walk", $"{label}: {(ok ? $"crossed into {client.Network.CurrentSim?.Name}" : $"still in {cur.Name} at {V(client.Self.SimPosition)} after 6 s")}");
        return ok;
    }

    static string RegionTag(Simulator sim)
    {
        var cur = client.Network.CurrentSim;
        return sim == null || sim == cur ? "" : $"  [{sim.Name} {SimGeo.Dir(cur.Handle, sim.Handle)}]";
    }

    // terrain height at a point in the current frame, from whichever region holds it
    static float? GroundAny(float x, float y)
    {
        var cur = client.Network.CurrentSim; if (cur == null) return null;
        var p = new Vector3(x, y, 0);
        if (SimGeo.InRegion(p)) return Ground(x, y);
        foreach (var (sim, off) in ViewSims().Skip(1))
        {
            var q = p - off;
            if (SimGeo.InRegion(q) && sim.TerrainHeightAtPoint((int)q.X, (int)q.Y, out var h)) return h;
        }
        return null;
    }

    static void HookNeighbors()
    {
        client.Settings.Agent.MultipleSims = Env("GT_MULTI_SIMS", "on") != "off";
        Log("regions", $"neighbor regions (child agents) {(client.Settings.Agent.MultipleSims ? "ON" : "OFF")} (GT_MULTI_SIMS); neighbor cap {NeighborMaxRoots} linksets per region");
        client.Network.SimConnected += (s, e) =>
        {
            var cur = client.Network.CurrentSim;
            if (cur != null && e.Simulator != cur && e.Simulator.Handle != 0)
                Log("regions", $"neighbor connected: {e.Simulator.Name} ({SimGeo.Dir(cur.Handle, e.Simulator.Handle)}) {e.Simulator.IPEndPoint}");
        };
        client.Network.SimDisconnected += (s, e) => { if (e.Simulator != null) { coarseBy.TryRemove(e.Simulator.Handle, out _); droppedBy.TryRemove(e.Simulator.Handle, out _); Log("regions", $"region disconnected: {e.Simulator.Name}"); } };
        client.Self.TeleportProgress += (s, e) =>
        {
            if (e.Status is TeleportStatus.Start or TeleportStatus.Progress) { teleportActive = true; teleportActiveAt = DateTime.Now; }
            else if (e.Status is TeleportStatus.Finished or TeleportStatus.Failed or TeleportStatus.Cancelled) { _ = Task.Run(async () => { await Task.Delay(3000); teleportActive = false; }); }
        };
        void Began(ulong to)
        {
            var cur = client.Network.CurrentSim; if (cur == null) return;
            var (ao0, seated0, follow0) = PreCrossState();
            var r = crossing.Begin(DateTime.Now, cur.Handle, to, ao0, seated0, follow0);
            if (r != null) Log("crossing", $"{r} from {cur.Name} at {V(client.Self.SimPosition)}{(followId != UUID.Zero ? $", following {followName}" : "")}");
        }
        client.Network.RegisterCallback(PacketType.CrossedRegion, (s, e) => { try { Began(((CrossedRegionPacket)e.Packet).RegionData.RegionHandle); } catch { } });
        client.Network.RegisterEventCallback("CrossedRegion", (key, msg, sim) => { try { Began(((LibreMetaverse.Messages.Linden.CrossedRegionMessage)msg).RegionHandle); } catch { } });
        client.Self.RegionCrossed += (s, e) =>
        {
            if (e.NewSimulator == null) { Log("crossing", crossing.Failed(DateTime.Now, $"the library could not enter the new region; staying in {e.OldSimulator?.Name}")); client.Self.AutoPilotCancel(); }
        };
        client.Network.SimChanged += (s, e) =>
        {
            var cur = client.Network.CurrentSim; if (cur == null) return;
            bool tp = teleportActive && (DateTime.Now - teleportActiveAt).TotalSeconds < 90;
            var (ao0, seated0, follow0) = PreCrossState();
            var r = crossing.SimChanged(DateTime.Now, e.PreviousSimulator?.Handle ?? 0, cur.Handle, tp, ao0, seated0, follow0);
            if (tp || (e.PreviousSimulator != null && e.PreviousSimulator.Handle != 0 && !SimGeo.Adjacent(e.PreviousSimulator.Handle, cur.Handle)))
                _ = Task.Run(async () => { await Task.Delay(20000); PruneFarRegions("after a teleport"); });
            if (r != null) Log("crossing", $"{r}: now in {cur.Name}{(e.PreviousSimulator != null ? $" (from {e.PreviousSimulator.Name})" : "")}");
            if (droppedBy.TryRemove(cur.Handle, out var dropped) && dropped.Count > 0)
            {   // objects the neighbor cap dropped while she was a child agent here: the region thinks she has them
                List<uint> ids; lock (dropped) ids = dropped.ToList();
                foreach (var chunk in ids.Chunk(200)) try { client.Objects.RequestObjects(cur, chunk.ToList()); } catch { }
                Log("regions", $"entered {cur.Name}: re-requested {ids.Count} objects dropped by the neighbor cap");
            }
        };
        client.Grid.CoarseLocationUpdate += (s, e) => { if (e.Simulator != null && e.Simulator.Handle != 0) coarseBy[e.Simulator.Handle] = e.Positions; };
    }

    // called every second from Ticker(): crossing state + neighbor object cap
    static int nbTick;
    static void NeighborTick()
    {
        if (!LoggedIn) return;
        var cur = client.Network.CurrentSim; if (cur == null) return;
        if (crossing.State == CrossingWatch.Phase.Idle && cur.AgentMovementComplete)
            preCross = (AoStateNow().active, client.Self.SittingOn != 0, followId, DateTime.Now);
        if (crossing.State != CrossingWatch.Phase.Idle)
        {
            bool self = cur.ObjectsAvatars.Values.Any(a => a != null && a.ID == client.Self.AgentID);
            bool hud = false; try { hud = AoHudWorn(); } catch { }
            var r = crossing.Observe(DateTime.Now, self, hud);
            if (r != null)
            {
                Log("crossing", $"{r}; in {cur.Name} at {V(client.Self.SimPosition)}, AO {AoFlag()}, follow {(followId == UUID.Zero ? "-" : followName)}");
                if (r.Contains("STUCK")) _ = Task.Run(CrossingRecover);
            }
        }
        if (++nbTick % 10 == 0 && MultiSims) TrimNeighborObjects();
        if (nbTick % 60 == 0 && MultiSims && crossing.State == CrossingWatch.Phase.Idle) PruneFarRegions("periodic");
    }

    // regions that are neither hers nor adjacent (left behind by a teleport; the grid closes them after a minute or so):
    // close them now, their objects are dead weight under the 768 MB cap
    static void PruneFarRegions(string why)
    {
        var cur = client.Network.CurrentSim; if (cur == null || cur.Handle == 0 || !LoggedIn) return;
        if (crossing.State != CrossingWatch.Phase.Idle) return;
        foreach (var s in SimsSnapshot())
        {
            if (s == null || s == cur || s.Handle == 0 || SimGeo.Adjacent(cur.Handle, s.Handle)) continue;
            int n = s.ObjectsPrimitives.Count;
            try { client.Network.DisconnectSim(s, true); Log("regions", $"closed {s.Name} ({why}): not adjacent to {cur.Name}, {n} objects released"); }
            catch (Exception ex) { Log("regions", $"close {s.Name} failed: {ex.GetBaseException().Message}"); }
        }
    }

    // a crossing that never finished: stop the autopilot, re-send her state to the region she is in (AgentUpdate), and
    // re-request her own object; the library has already restored the old region as current when it gave up
    static async Task CrossingRecover()
    {
        try
        {
            client.Self.AutoPilotCancel();
            client.Self.Movement.SendUpdate(true);
            await Task.Delay(2000);
            var cur = client.Network.CurrentSim;
            Log("crossing", $"recovery: in {cur?.Name} at {V(client.Self.SimPosition)}, movement complete {cur?.AgentMovementComplete}");
            if (cur != null && !cur.AgentMovementComplete) { client.Self.CompleteAgentMovement(cur); Log("crossing", "recovery: CompleteAgentMovement re-sent"); }
        }
        catch (Exception ex) { Log("crossing", "recovery error: " + ex.GetBaseException().Message); }
    }

    // pure: which of a neighbor's root objects to drop to stay under the cap (farthest from her first; never avatar
    // attachments or anything an avatar sits on). roots: (localId, distance, protected)
    internal static List<uint> TrimPlan(IEnumerable<(uint id, float dist, bool keep)> roots, int total, int cap)
    {
        var drop = new List<uint>();
        if (total <= cap) return drop;
        int over = total - cap;
        foreach (var r in roots.Where(r => !r.keep).OrderByDescending(r => r.dist))
        {
            if (over <= 0) break;
            drop.Add(r.id); over--;
        }
        return drop;
    }

    // keeps each neighbor's object table under GT_NEIGHBOR_MAX_ROOTS linksets: drops the farthest unattached linksets
    // locally (halved cap under heap pressure); their ids are kept and re-requested when she enters that region.
    static void TrimNeighborObjects()
    {
        int cap = NeighborMaxRoots; if (cap <= 0) return;
        long heapMb = GC.GetTotalMemory(false) / 1048576;
        if (heapMb > 448) cap /= 2;
        var me = client.Self.SimPosition;
        foreach (var (sim, off) in ViewSims().Skip(1))
        {
            var all = sim.ObjectsPrimitives.Values.Where(p => p != null).ToList();
            var rootList = all.Where(p => p.ParentID == 0).ToList();
            if (rootList.Count <= cap) continue;
            var seats = sim.ObjectsAvatars.Values.Where(a => a != null && a.ParentID != 0).Select(a => a.ParentID).ToHashSet();
            var children = all.Where(p => p.ParentID != 0).GroupBy(p => p.ParentID).ToDictionary(g => g.Key, g => g.Select(p => p.LocalID).ToList());
            var roots = rootList.Select(p => (p.LocalID, Vector3.Distance(p.Position + off, me),
                seats.Contains(p.LocalID) || (children.TryGetValue(p.LocalID, out var ch) && ch.Any(seats.Contains)))).ToList();
            var drop = TrimPlan(roots, roots.Count, cap);
            var rec = droppedBy.GetOrAdd(sim.Handle, _ => new HashSet<uint>());
            int n = 0;
            foreach (var id in drop)
            {
                if (children.TryGetValue(id, out var ch)) foreach (var c in ch) if (sim.ObjectsPrimitives.TryRemove(c, out _)) { n++; lock (rec) rec.Add(c); }
                if (sim.ObjectsPrimitives.TryRemove(id, out _)) { n++; lock (rec) rec.Add(id); }
            }
            if (n > 0) Log("regions", $"neighbor {sim.Name}: dropped {n} far objects ({drop.Count} linksets) to keep {cap} linksets (heap {heapMb} MB)");
        }
    }

    // walk_to [<region>] <x> <y> [z] -> target in the current region's frame (region = current or a connected neighbor)
    static (Vector3? t, string err) ParseWalkTo(string[] a)
    {
        const string usage = "usage: walk_to [<region>] <x> <y> [z]  (region-local; a neighbor region's name walks across the border; x/y beyond 0-256 = across the border)";
        int nNum = 0;
        for (int i = a.Length - 1; i >= 0 && nNum < 3 && F(a[i], out _); i--) nNum++;
        if (nNum < 2) return (null, usage);
        var nums = a.Skip(a.Length - nNum).Select(x => { F(x, out var f); return f; }).ToArray();
        var region = string.Join(' ', a.Take(a.Length - nNum)).Trim().Trim('"');
        var cur = client.Network.CurrentSim; if (cur == null) return (null, "not connected");
        var off = Vector3.Zero;
        if (region.Length > 0 && !region.Equals(cur.Name, StringComparison.OrdinalIgnoreCase))
        {
            var nb = ViewSims().Skip(1).FirstOrDefault(v => v.sim.Name.Equals(region, StringComparison.OrdinalIgnoreCase));
            if (nb.sim == null) return (null, $"'{region}' is not a connected neighbor region (neighbors: {string.Join(", ", ViewSims().Skip(1).Select(v => v.sim.Name))}); use teleport");
            off = nb.off;
        }
        var xy = new Vector3(nums[0], nums[1], 0) + off;
        float z = nNum == 3 ? nums[2] : (GroundAny(xy.X, xy.Y) ?? client.Self.SimPosition.Z);
        var t = new Vector3(xy.X, xy.Y, z);
        var h = SimGeo.HandleAt(cur.Handle, t);
        if (h != cur.Handle && !ViewSims().Skip(1).Any(v => v.sim.Handle == h)) return (null, $"{V(t)} is in region {h:X}, which is not a connected neighbor; use teleport");
        if (HDist(client.Self.SimPosition, t) > 400) return (null, $"{HDist(client.Self.SimPosition, t):F0} m is too far for a walk; use teleport");
        return (t, null);
    }

    static string RegionsCmd(string[] a)
    {
        if (a.Length > 0 && a[0] == "selftest") return NeighborSelfTest();
        var cur = client.Network.CurrentSim; if (cur == null) return "not connected";
        var me = client.Self.SimPosition;
        var sb = new StringBuilder();
        using var proc = System.Diagnostics.Process.GetCurrentProcess();
        sb.AppendLine($"regions: {(MultiSims ? "neighbors ON" : "neighbors OFF (GT_MULTI_SIMS=off)")}; she is in {cur.Name} at {V(me)}, {SimGeo.EdgeDist(me, cur.SizeX == 0 ? 256 : cur.SizeX, cur.SizeY == 0 ? 256 : cur.SizeY):F0} m from the nearest border; " +
                      $"draw distance {client.Self.Movement.Camera.Far:F0} m; heap {GC.GetTotalMemory(false) / 1048576} MB, rss {proc.WorkingSet64 / 1048576} MB");
        foreach (var s in SimsSnapshot().OrderBy(s => s == cur ? 0 : 1).ThenBy(s => s.Name))
        {
            var dir = s == cur ? "HERE" : s.Handle == 0 ? "?" : SimGeo.Dir(cur.Handle, s.Handle);
            var dist = s == cur ? 0 : s.Handle == 0 ? -1 : SimGeo.DistToRegion(cur.Handle, s.Handle, me);
            int coarse = coarseBy.TryGetValue(s.Handle, out var c) ? c.Count : 0;
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  {0,-4} {1,-24} {2}  {3}  objects {4,6}  avatars {5,3} (map {6,3})  {7}{8}",
                dir, string.IsNullOrEmpty(s.Name) ? "(no handshake yet)" : s.Name, s.Handle == 0 ? "handle ?" : $"{SimGeo.Origin(s.Handle).x / 256},{SimGeo.Origin(s.Handle).y / 256}",
                dist < 0 ? "    ?" : $"{dist,5:F0} m", s.ObjectsPrimitives.Count, s.ObjectsAvatars.Count, coarse,
                s.Connected ? "connected" : "NOT connected", s.Caps == null ? ", no caps" : ""));
        }
        sb.AppendLine($"crossing: {crossing.State} (crossings {crossing.Crossings}, failures {crossing.Failures}); last: {crossing.Last}");
        return sb.ToString().TrimEnd();
    }

    internal static string NeighborSelfTest()
    {
        var sb = new StringBuilder(); int pass = 0, fail = 0;
        void C(bool ok, string w) { if (ok) pass++; else fail++; sb.AppendLine($"{(ok ? "PASS" : "FAIL")} {w}"); }
        bool Near(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-3f;
        // a mainland-like block: region (1000, 1000) on the grid and its 8 neighbors
        ulong H(uint gx, uint gy) => SimGeo.Handle(gx * 256, gy * 256);
        ulong cur = H(1000, 1000), east = H(1001, 1000), west = H(999, 1000), north = H(1000, 1001), ne = H(1001, 1001), sw = H(999, 999), far = H(1002, 1000);
        C(SimGeo.Origin(cur) == (256000u, 256000u), "handle -> global origin (256 m grid)");
        C(Near(SimGeo.Offset(cur, east), new Vector3(256, 0, 0)) && Near(SimGeo.Offset(cur, west), new Vector3(-256, 0, 0)) && Near(SimGeo.Offset(cur, sw), new Vector3(-256, -256, 0)),
          "offset of a neighbor into the current frame: E +256 x, W -256 x, SW -256,-256");
        C(Near(SimGeo.ToCur(cur, east, new Vector3(3, 100, 25)), new Vector3(259, 100, 25)) && Near(SimGeo.ToCur(east, cur, new Vector3(250, 100, 25)), new Vector3(-6, 100, 25)),
          "a point 3 m into the east neighbor reads x 259 here; 6 m before the border reads x -6 from the east side");
        C(Near(SimGeo.ToCur(cur, east, SimGeo.ToCur(east, cur, new Vector3(17, 42, 30))), new Vector3(17, 42, 30)), "frame round trip is exact");
        C(SimGeo.HandleAt(cur, new Vector3(10, 10, 0)) == cur && SimGeo.HandleAt(cur, new Vector3(259, 10, 0)) == east && SimGeo.HandleAt(cur, new Vector3(-0.5f, 10, 0)) == west
          && SimGeo.HandleAt(cur, new Vector3(300, 300, 0)) == ne && SimGeo.HandleAt(cur, new Vector3(256, 0, 0)) == east, "which region holds a point (borders belong to the higher region)");
        C(SimGeo.HandleAt(SimGeo.Handle(0, 0), new Vector3(-1, 5, 0)) == 0, "a point west of the grid's edge has no region");
        C(SimGeo.Dir(cur, east) == "E" && SimGeo.Dir(cur, ne) == "NE" && SimGeo.Dir(cur, sw) == "SW" && SimGeo.Dir(cur, north) == "N" && SimGeo.Dir(cur, cur) == "", "compass direction of a neighbor");
        C(SimGeo.Adjacent(cur, east) && SimGeo.Adjacent(cur, ne) && SimGeo.Adjacent(cur, sw) && !SimGeo.Adjacent(cur, far) && !SimGeo.Adjacent(cur, cur), "adjacent: edges and corners, not two away, not itself");
        C(MathF.Abs(SimGeo.EdgeDist(new Vector3(250, 128, 0)) - 6) < 1e-3f && MathF.Abs(SimGeo.DistToRegion(cur, east, new Vector3(250, 128, 0)) - 6) < 1e-3f
          && SimGeo.DistToRegion(cur, east, new Vector3(260, 5, 0)) == 0 && MathF.Abs(SimGeo.DistToRegion(cur, ne, new Vector3(253, 252, 0)) - 5) < 1e-3f, "distances to the border / to a neighbor square (corner = diagonal)");
        C(SimGeo.ExitBorder(new Vector3(250, 100, 0), new Vector3(262, 100, 0)) == "E" && SimGeo.ExitBorder(new Vector3(100, 5, 0), new Vector3(100, -3, 0)) == "S"
          && SimGeo.ExitBorder(new Vector3(10, 10, 0), new Vector3(20, 20, 0)) == null && SimGeo.ExitBorder(new Vector3(250, 250, 0), new Vector3(270, 258, 0)) == "E", "exit border of a straight walk");
        C(SimGeo.NeedsPush(new Vector3(0.9f, 241, 0), new Vector3(-9.6f, 237.9f, 0)) && !SimGeo.NeedsPush(new Vector3(8, 241, 0), new Vector3(-9.6f, 237.9f, 0))
          && !SimGeo.NeedsPush(new Vector3(1, 100, 0), new Vector3(5, 100, 0)) && SimGeo.NeedsPush(new Vector3(254, 254, 0), new Vector3(262, 255, 0))
          && !SimGeo.NeedsPush(new Vector3(1, 128, 0), new Vector3(100, 300, 0)), "border push: only within 3 m of the border the walk leaves through (not another nearby edge)");
        // walking frame shift: a target given before the crossing, re-expressed after entering the east region
        var target = new Vector3(270, 100, 25); var inEast = target + SimGeo.Offset(east, cur);
        C(Near(inEast, new Vector3(14, 100, 25)), "walk target 270,100 becomes 14,100 after crossing east");

        // crossing state machine
        var t0 = new DateTime(2026, 10, 6, 10, 0, 0); var w = new CrossingWatch(); var follow = UUID.Random();
        C(w.Begin(t0, cur, east, true, false, follow) != null && w.State == CrossingWatch.Phase.Crossing && w.Grace && w.AssumeAo, "CrossedRegion -> Crossing, grace, AO assumed");
        C(w.Begin(t0.AddSeconds(0.2), cur, east, true, false, follow) == null, "the duplicate notice (UDP + event queue) is ignored");
        C(w.SimChanged(t0.AddSeconds(1.5), cur, east, false, false, false, UUID.Zero) != null && w.State == CrossingWatch.Phase.Settling && w.AoBefore && w.FollowBefore == follow,
          "region change -> Settling; keeps the state from before the crossing (AO on, following)");
        C(w.Observe(t0.AddSeconds(2), false, false) == null && w.Grace, "still settling while her avatar object is not in the new region");
        C(w.Observe(t0.AddSeconds(3), true, false) == null && w.Grace, "AO was on: waits for the AO HUD too");
        var s1 = w.Observe(t0.AddSeconds(4), true, true);
        C(s1 != null && s1.Contains("settled") && w.State == CrossingWatch.Phase.Idle && !w.Grace && w.Crossings == 1, "avatar + HUD seen -> settled, idle");
        w.Begin(t0.AddSeconds(10), east, cur, false, false, UUID.Zero);
        C(w.Observe(t0.AddSeconds(25), false, false) == null && w.State == CrossingWatch.Phase.Crossing, "no region change for 15 s: still crossing");
        var st = w.Observe(t0.AddSeconds(41), false, false);
        C(st != null && st.Contains("STUCK") && w.State == CrossingWatch.Phase.Failed && w.Failures == 1 && !w.Grace, "no region change 30 s after CrossedRegion -> STUCK (recover)");
        C(w.Observe(t0.AddSeconds(52), false, false) != null && w.State == CrossingWatch.Phase.Idle, "failed -> idle after the hold");
        C(w.SimChanged(t0.AddSeconds(60), cur, east, false, true, false, UUID.Zero) != null && w.State == CrossingWatch.Phase.Settling && w.Crossings == 2, "adjacent region change without CrossedRegion still counts as a crossing");
        C(w.Observe(t0.AddSeconds(81), false, false)?.Contains("settle cap") == true && w.State == CrossingWatch.Phase.Idle, "settle cap: never stuck in grace");
        C(w.SimChanged(t0.AddSeconds(90), cur, far, false, true, false, UUID.Zero) == null && w.State == CrossingWatch.Phase.Idle, "non-adjacent change (teleport) is not a crossing");
        w.Begin(t0.AddSeconds(100), cur, east, true, false, UUID.Zero);
        C(w.SimChanged(t0.AddSeconds(101), cur, far, true, true, false, UUID.Zero) != null && w.State == CrossingWatch.Phase.Idle, "a teleport during a crossing resets the watch");
        w.Begin(t0.AddSeconds(110), cur, east, true, true, UUID.Zero);
        C(w.Grace && !w.AssumeAo, "seated (vehicle) crossing: grace, but no AO assumption");
        C(w.SimChanged(t0.AddSeconds(111), cur, east, false, false, true, UUID.Zero) != null && w.Observe(t0.AddSeconds(112), true, false) == null && w.Grace,
          "seated: AO was on -> waits for the HUD");
        C(w.Failed(t0.AddSeconds(113), "x") != null && w.State == CrossingWatch.Phase.Failed, "library gave up -> Failed");

        // neighbor object cap
        var roots = new List<(uint, float, bool)> { (1, 10, false), (2, 200, false), (3, 150, true), (4, 90, false), (5, 120, false) };
        C(TrimPlan(roots, 5, 10).Count == 0, "under the cap: nothing dropped");
        var tp = TrimPlan(roots, 5, 3);
        C(tp.SequenceEqual(new uint[] { 2, 5 }), "over the cap by 2: the two farthest unprotected (seats/attachments kept)");
        C(TrimPlan(roots, 5, 0).Count == 4, "cap 0: every unprotected root, never a protected one");

        // chat position from a neighbor region, and the chat dedupe
        C(Near(ChatPosInCur(cur, east, new Vector3(2, 50, 22)), new Vector3(258, 50, 22)) && ChatPosInCur(cur, cur, new Vector3(2, 50, 22)) == new Vector3(2, 50, 22)
          && ChatPosInCur(0, east, new Vector3(2, 50, 22)) == new Vector3(2, 50, 22), "chat position from a neighbor region in the current frame");
        var dd = new Dictionary<string, DateTime>(); var who = UUID.Random();
        C(!ChatDup(dd, who, "hi", t0) && ChatDup(dd, who, "hi", t0.AddSeconds(1)) && !ChatDup(dd, who, "hi", t0.AddSeconds(5)) && !ChatDup(dd, who, "hello", t0.AddSeconds(5.5)),
          "the same line from two regions within 2 s is shown once");

        // terrain of her region + its east neighbor on one grid (look renders across a border)
        float?[,] G(float v, bool holes = false) { var g = new float?[3, 3]; for (int j = 0; j < 3; j++) for (int i = 0; i < 3; i++) g[j, i] = holes && i == 1 ? null : v; return g; }
        var (mx0, my0, mh) = MergeTerrain(new() { (0, 0, G(20)), (2, 0, G(30)) }, 3);
        C(mx0 == 0 && my0 == 0 && mh.GetLength(1) == 5 && mh.GetLength(0) == 3 && mh[1, 2] == 20 && mh[1, 3] == 30 && mh[1, 0] == 20,
          "terrain merge: shared border column keeps her region's value, neighbor continues east");
        var (wx0, _, wh) = MergeTerrain(new() { (0, 0, G(20, holes: true)), (-2, 0, G(10)) }, 3);
        C(wx0 == -2 && wh[0, 0] == 10 && wh[1, 3] != null && wh.Cast<float?>().All(v => v != null), "terrain merge: west neighbor (negative origin), holes filled from neighbors");

        sb.Insert(0, $"neighbor selftest: {pass} pass, {fail} FAIL\n");
        return sb.ToString().TrimEnd();
    }

    // pure: chat position (region-local to the region that sent it) in the current region's frame
    internal static Vector3 ChatPosInCur(ulong cur, ulong from, Vector3 p) => cur == 0 || from == 0 || cur == from || p == Vector3.Zero ? p : SimGeo.ToCur(cur, from, p);
    // pure: the same speaker + text within 2 s (a line relayed by two regions) = duplicate
    internal static bool ChatDup(Dictionary<string, DateTime> seen, UUID who, string text, DateTime now)
    {
        lock (seen)
        {
            foreach (var k in seen.Where(kv => (now - kv.Value).TotalSeconds > 10).Select(kv => kv.Key).ToList()) seen.Remove(k);
            var key = who + "\n" + text;
            if (seen.TryGetValue(key, out var t) && (now - t).TotalSeconds < 2) return true;
            seen[key] = now; return false;
        }
    }
    static readonly Dictionary<string, DateTime> chatSeen = new();
}
