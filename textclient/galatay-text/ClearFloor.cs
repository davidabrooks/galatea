// ClearFloor.cs (2026-10-16 16:30 PT, David: "you were standing on that makeup desk before you changed").
// The 'naked first' adult-pose flow stood up from the Xplicit Makeup Table at 222.6,72.7,29.7 (inside its footprint, 0.7 m above
// the floor) and changed outfit right there; the re-dress afterwards and the toilet / tub undress had the same hole.
// 2026-10-16 (later, David: standing up to change is too slow): the Naked swap and the re-dress of an adult pose now happen WHILE
// SEATED (AdultRedress.cs), so they no longer use this. It stays for the toilet / tub flows, which by David's earlier rule (09:06)
// undress and dress standing on the floor at the seat's change spot: before the undress and after standing up from them she first steps to a clear
// floor spot beside the furniture: the seat's own spot from routes data (stand_spot, else approach, else change_spot) when it has one,
// otherwise a computed spot within 2 m that is outside every furniture footprint (+0.35 m body margin), not a wall on the nav grid
// and reachable in a straight line, preferring the side away from the furniture (the room). Log lines start with "STEP CLEAR".
// No spot found (or no footprint data, not the home region) -> the change happens where she stands, as before, and the log says why.
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    internal const float ClearBodyMargin = 0.35f, ClearMaxRadiusM = 2.0f, ClearAboveFloorM = 0.4f, ClearSeatNearM = 2.5f;
    internal sealed record ClearStep(bool Needed, Vector3? To, string Why, bool FromData);

    // pure: should she step away before changing outfit, and to where? `blocked(x, y)` = wall / blocked cell on the nav grid (null = none known).
    internal static ClearStep ClearStepPlan(Vector3 me, HomeSeatInfo seat, IReadOnlyList<FurnitureBox> boxes, Func<float, float, bool> blocked, float maxR = ClearMaxRadiusM)
    {
        boxes ??= new List<FurnitureBox>(); blocked ??= (_, _) => false;
        if (seat != null && HDist(me, seat.Pos) > ClearSeatNearM) seat = null;   // a seat she is not next to says nothing about this spot
        var spot = seat?.StandSpot ?? seat?.Approach ?? seat?.ChangeSpot;
        var inBox = InFurniture(me, boxes);
        bool above = spot != null && seat != null && me.Z - spot.Value.Z > ClearAboveFloorM;
        if (inBox == null && !above) return new ClearStep(false, null, "already on clear floor", false);
        var on = inBox != null ? $"inside '{inBox.Name}'" : $"{me.Z - spot.Value.Z:F1} m above the floor beside '{seat.Name}'";
        if (spot != null && HDist(me, spot.Value) > StandOutNearTol) return new ClearStep(true, spot, $"standing {on}; the seat's clear spot", true);
        var origin = inBox != null ? new Vector3((inBox.X0 + inBox.X1) / 2, (inBox.Y0 + inBox.Y1) / 2, me.Z) : seat?.Pos ?? me;
        float ox = me.X - origin.X, oy = me.Y - origin.Y, ol = MathF.Sqrt(ox * ox + oy * oy);
        float outA = ol > 0.05f ? MathF.Atan2(oy, ox) : 0f;
        var others = boxes.Where(b => b != inBox).ToList();
        Vector3? best = null; float bestScore = float.MaxValue;
        foreach (var r in new[] { 0.8f, 1.2f, 1.6f, 2.0f })
        {
            if (r > maxR + 0.001f) break;
            for (int k = 0; k < 16; k++)
            {
                float a = k * MathF.PI / 8, cx = me.X + MathF.Cos(a) * r, cy = me.Y + MathF.Sin(a) * r;
                var c = new Vector3(cx, cy, above ? spot.Value.Z : me.Z);   // up on a seat: aim at the floor, not seat height
                if (InFurnitureMargin(c, boxes, ClearBodyMargin) != null || blocked(cx, cy) || !ClearLine(me, c, others, blocked)) continue;
                float d = MathF.Abs(a - outA) % (2 * MathF.PI); if (d > MathF.PI) d = 2 * MathF.PI - d;
                float score = r + (ol > 0.05f ? 0.8f * d / MathF.PI : 0f);
                if (score < bestScore) { bestScore = score; best = c; }
            }
        }
        return best == null ? new ClearStep(true, null, $"standing {on}, but no clear floor spot within {maxR:F1} m (footprints / walls)", false)
                            : new ClearStep(true, best, $"standing {on}; computed clear spot", false);
    }

    // pure: straight walk from a to b (first 0.5 m not checked: she may stand in a blocked cell) crosses no wall and no other furniture
    static bool ClearLine(Vector3 a, Vector3 b, IReadOnlyList<FurnitureBox> others, Func<float, float, bool> blocked)
    {
        float len = HDist(a, b);
        for (float s = 0.5f; s <= len; s += 0.1f)
        {
            float x = a.X + (b.X - a.X) * s / len, y = a.Y + (b.Y - a.Y) * s / len;
            if (blocked(x, y) || InFurnitureMargin(new Vector3(x, y, a.Z), others, 0.15f) != null) return false;
        }
        return true;
    }

    // pure: the listed home seat she is next to (within 2.5 m), used when the seat is not known (re-dress after the tub / toilet)
    internal static HomeSeatInfo NearestHomeSeat(Vector3 me, IEnumerable<HomeSeatInfo> seats, float within = ClearSeatNearM)
        => (seats ?? Enumerable.Empty<HomeSeatInfo>()).Where(x => HDist(me, x.Pos) <= within).OrderBy(x => HDist(me, x.Pos)).FirstOrDefault();

    static readonly SemaphoreSlim standOutLock = new(1, 1);   // one step-away at a time (wander's stand-out and the re-dress tick can meet)

    static List<FurnitureBox> HomeFootprints()
    {
        try { var f = Path.Combine(RouteDir, "_graph-Peronaut.json"); return File.Exists(f) ? ParseFurnitureBoxes(File.ReadAllText(f)) : new(); }
        catch (Exception ex) { WLog("STEP CLEAR: footprints: " + ex.Message); return new(); }
    }

    // Stand clear of the furniture before an outfit change. true = on clear floor (or nothing to do), false = could not (the change goes ahead where she is).
    static async Task<bool> ClearFloorForOutfitChange(string why, HomeSeatInfo seat, CancellationToken ct)
    {
        try
        {
            if (!LoggedIn || !InPeronaut) return true;   // footprints are mapped for the home only
            if (client.Self.SittingOn != 0) { WLog($"STEP CLEAR ({why}): still seated; not stepping"); return false; }
            await standOutLock.WaitAsync(ct);
            try
            {
                if (client.Self.SittingOn != 0) return false;
                var boxes = HomeFootprints(); var me = client.Self.SimPosition;
                seat ??= NearestHomeSeat(me, LoadHomeSeats());
                var g = NavGridHere();
                Func<float, float, bool> blocked = g == null ? null : (x, y) => { if (!g.Contains(x, y)) return false; var (i, j) = g.IJ(x, y); return g.At(i, j) != 0; };
                var plan = ClearStepPlan(me, seat, boxes, blocked);
                if (!plan.Needed) { WLog($"STEP CLEAR ({why}): {plan.Why} at {P3(me)}"); return true; }
                if (plan.To == null) { WLog($"STEP CLEAR ({why}): {plan.Why}; changing where I stand ({P3(me)})"); return false; }
                var to = plan.To.Value;
                WLog($"STEP CLEAR ({why}): at {P3(me)} {plan.Why} {P3(to)}; stepping there before changing");
                await StandOutMoveTo($"clear floor ({why})", me, to, false, InFurniture(me, boxes), ct);
                var now = client.Self.SimPosition;
                bool clear = !ClearStepPlan(now, seat, boxes, blocked).Needed || HDist(now, to) <= StandOutNearTol + 0.2f;
                WLog($"STEP CLEAR ({why}): now at {P3(now)} ({(clear ? "clear" : "still on the furniture; changing here anyway")})");
                return clear;
            }
            finally { standOutLock.Release(); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { WLog($"STEP CLEAR ({why}): {ex.GetBaseException().Message}; changing where I stand"); return false; }
    }
}
