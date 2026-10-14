// Occupied poses on shared furniture (David, 2026-10-14 13:17 PT): on the Beach House boat she picked AVsitter
// SINGLE* > 'tan 1' while David was already in that pose, and sat on top of him. Now, for every automatic or solo pick
// (pose / pose change / pose path, wander + chat-routine sits, boat Singles picks, David-left solo switch):
//  1. skip leaves known to put her on another sitter: per seat we remember where each leaf put her (offset from the
//     seat root) and veto a leaf whose remembered spot is within PoseOverlapM of an avatar sitting on that seat now;
//     leaves that overlapped earlier stay vetoed while that seat is still shared;
//  2. after the press, verify: if she is within PoseOverlapM of any other avatar, the leaf is marked occupied and the
//     next free pose is picked automatically (up to PoseOverlapRetries).
// Couples poses (explicit 'pose couples', lap poses in SitFixes.cs) are exempt: sharing a spot is the point there.
using System.Collections.Concurrent;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    public static float PoseOverlapM = 0.5f;
    public static int PoseOverlapRetries = 3;
    public static int PoseOverlapSettleMs = 2500;
    // seat id -> leaf (bare, lower) -> her offset from the seat root when she sat in it
    static readonly ConcurrentDictionary<UUID, ConcurrentDictionary<string, Vector3>> poseLeafSpots = new();
    // seat id -> leaves that overlapped another avatar (cleared when nobody else sits on the seat)
    static readonly ConcurrentDictionary<UUID, ConcurrentDictionary<string, DateTime>> poseLeafOccupied = new();

    internal static string PoseLeafKey(string raw) => PoseBare(raw).ToLowerInvariant();

    /// <summary>Pure: distance to the nearest other avatar, or +inf.</summary>
    internal static float NearestAvatarDist(Vector3 me, IEnumerable<Vector3> others)
    {
        float best = float.PositiveInfinity;
        foreach (var o in others ?? Enumerable.Empty<Vector3>()) { var d = Vector3.Distance(me, o); if (d < best) best = d; }
        return best;
    }
    /// <summary>Pure: is she overlapping another avatar?</summary>
    internal static bool PoseOverlaps(Vector3 me, IEnumerable<Vector3> others, float thr) => NearestAvatarDist(me, others) < thr;

    /// <summary>Pure: leaves to avoid now = leaves that overlapped before + leaves whose remembered spot is taken by
    /// a current sitter (offsets relative to the seat root).</summary>
    internal static HashSet<string> PoseLeavesTaken(IReadOnlyDictionary<string, Vector3> spots, IEnumerable<string> overlappedBefore,
        IEnumerable<Vector3> otherSitterOffsets, float thr)
    {
        var s = new HashSet<string>(overlappedBefore ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var others = (otherSitterOffsets ?? Enumerable.Empty<Vector3>()).ToList();
        if (spots != null && others.Count > 0)
            foreach (var kv in spots) if (PoseOverlaps(kv.Value, others, thr)) s.Add(kv.Key);
        return s;
    }

    /// <summary>Pure: labels minus taken LEAVES (submenus '*' and controls stay).</summary>
    internal static List<string> PoseDropTaken(IReadOnlyList<string> labels, ISet<string> taken)
    {
        if (labels == null) return null;
        if (taken == null || taken.Count == 0) return labels.ToList();
        return labels.Where(l => (l ?? "").TrimEnd().EndsWith("*") || !taken.Contains(PoseLeafKey(l))).ToList();
    }

    // ---- live ----
    static uint SeatRootLocal(Primitive seat)
    {
        var sim = Sim; if (seat == null || sim == null) return 0;
        return seat.ParentID != 0 && sim.ObjectsPrimitives.ContainsKey(seat.ParentID) ? seat.ParentID : seat.LocalID;
    }
    static Vector3? SeatRootPos(Primitive seat)
    {
        var sim = Sim; var r = SeatRootLocal(seat);
        return sim != null && r != 0 && sim.ObjectsPrimitives.TryGetValue(r, out var p) && p != null ? p.Position : null;
    }
    // other avatars' region positions (seated ones resolved through their seat)
    static List<(Avatar av, Vector3 pos)> OtherAvatarPositions()
    {
        var res = new List<(Avatar, Vector3)>(); var sim = Sim; if (sim == null) return res;
        var me = client.Self.AgentID;
        foreach (var a in sim.ObjectsAvatars.Values.ToArray())
        {
            if (a == null || a.ID == me) continue;
            if (a.ParentID != 0 && !sim.ObjectsPrimitives.ContainsKey(a.ParentID)) continue;
            try { res.Add((a, PositionHelper.GetAvatarPosition(sim, a))); } catch { }
        }
        return res;
    }
    static List<Vector3> OtherSitterOffsets(Primitive seat)
    {
        var root = SeatRootPos(seat); if (root == null) return new();
        var sim = Sim; var rl = SeatRootLocal(seat);
        return OtherAvatarPositions().Where(x => x.av.ParentID == rl || (sim.ObjectsPrimitives.TryGetValue(x.av.ParentID, out var p) && p != null && p.ParentID == rl))
            .Select(x => x.pos - root.Value).ToList();
    }
    static HashSet<string> PoseTakenNow(Primitive seat)
    {
        if (seat == null) return new();
        var others = OtherSitterOffsets(seat);
        if (others.Count == 0) { poseLeafOccupied.TryRemove(seat.ID, out _); return new(); }
        poseLeafSpots.TryGetValue(seat.ID, out var spots);
        poseLeafOccupied.TryGetValue(seat.ID, out var occ);
        return PoseLeavesTaken(spots, occ?.Keys, others, PoseOverlapM);
    }
    static List<string> PoseLabelsFree(Primitive seat, IReadOnlyList<string> labels, string seatName)
    {
        var taken = PoseTakenNow(seat);
        var r = PoseDropTaken(labels, taken);
        if (labels != null && r.Count < labels.Count) WLog($"POSE '{seatName}': skipping occupied pose(s) {string.Join(", ", labels.Except(r))} (another avatar is there)");
        return r;
    }

    /// <summary>After a solo leaf press: wait, remember where it put her, return null if free or a reason if she
    /// overlaps another avatar (the leaf is then marked occupied on this seat).</summary>
    static async Task<string> PoseCheckOverlap(Primitive seat, string leaf, CancellationToken ct)
    {
        await Task.Delay(PoseOverlapSettleMs, ct);
        if (client.Self.SittingOn == 0) return null;
        var me = client.Self.SimPosition;
        var root = SeatRootPos(seat);
        var others = OtherAvatarPositions();
        var near = others.OrderBy(o => Vector3.Distance(me, o.pos)).FirstOrDefault();
        float d = near.av == null ? float.PositiveInfinity : Vector3.Distance(me, near.pos);
        var key = PoseLeafKey(leaf);
        if (d < PoseOverlapM)
        {
            poseLeafOccupied.GetOrAdd(seat.ID, _ => new())[key] = DateTime.Now;
            return $"'{leaf}' put her {d:F2} m from {near.av.Name}";
        }
        if (root != null) poseLeafSpots.GetOrAdd(seat.ID, _ => new())[key] = me - root.Value;
        return null;
    }
}
