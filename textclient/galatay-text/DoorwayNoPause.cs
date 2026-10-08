using System.Globalization;
using LibreMetaverse;

namespace GalatayText;

// 2026-10-07 19:45 David: "Don't pause your walk while going through a doorway".
// During a wander leg she never stops, pauses, idles, greets or sidesteps inside a doorway zone. The zone runs from
// DoorZoneBeforeM before each mapped door crossing on the path to DoorZoneAfterM past it, and also covers anything
// within DoorNearM of a mapped door centre. Pauses that would start there wait until she is clear. The 19:44:37 log
// had a "short pause 17 s at s=22" right in the front double doors; the door then shut on her.
public static partial class Program
{
    internal const float DoorZoneBeforeM = 1.5f, DoorZoneAfterM = 2.0f, DoorNearM = 1.5f;
    internal const int DeferredPauseMaxMs = 8000;   // a pause requested in a doorway waits at most this long
    static volatile bool routeInDoorZone;            // set by FollowPoly every tick while a leg runs
    static volatile string routeDoorZoneWhy = "";
    static int wDeferredPauseGen;

    // pure: every path distance u where the step [u, u+step] crosses a door (whole path, sorted, merged within 1 m)
    internal static List<float> AllDoorCrossings(Func<float, Vector2> at, float len, Func<Vector2, Vector2, bool> crosses, float step = DoorScanStepM)
    {
        var res = new List<float>();
        for (float u = 0; u < len; u += step)
        {
            float v = Math.Min(len, u + step);
            if (!crosses(at(u), at(v))) continue;
            float mid = (u + v) / 2f;
            if (res.Count > 0 && mid - res[^1] < 1.0f) continue;
            res.Add(mid);
        }
        return res;
    }

    // pure: inside a doorway zone at path distance s? Returns the s at which she is clear (crossing + after), else null.
    internal static float? DoorZoneClearAt(float s, IReadOnlyList<float> crossings, float before = DoorZoneBeforeM, float after = DoorZoneAfterM)
    {
        if (crossings == null) return null;
        float? clear = null;
        foreach (var c in crossings)
            if (s >= c - before && s <= c + after) clear = Math.Max(clear ?? float.MinValue, c + after);
        // back-to-back doors: chain zones that overlap
        if (clear != null)
            foreach (var c in crossings.OrderBy(x => x))
                if (c - before <= clear.Value && c + after > clear.Value) clear = c + after;
        return clear;
    }

    // pure: within r metres (horizontal) of any mapped door centre
    internal static bool NearDoorCentre(Vector2 me, IEnumerable<Vector2> centres, float r = DoorNearM) =>
        centres != null && centres.Any(c => Vector2.Distance(me, c) <= r);

    // pure: the short look-around pause may start here? (not in a doorway zone, not next to a door)
    internal static bool IdlePauseAllowedHere(float s, IReadOnlyList<float> crossings, bool nearDoor) =>
        !nearDoor && DoorZoneClearAt(s, crossings) == null;

    // pure: someone ahead on the path while she is in a doorway zone. Keep walking (pause later) when the person is
    // beyond the zone exit, so she stops outside the doorway. A person standing inside the doorway still stops her,
    // because she never walks through anyone.
    internal static bool DeferBlockerPause(float s, float? zoneClearAt, float blockerS) =>
        zoneClearAt is float clear && blockerS >= clear + 0.8f && blockerS - s >= 1.2f;

    // pure: hold before a door she just touched. She keeps walking when, at her speed, the leaf has DoorSwingWaitMs to
    // swing before she reaches it; otherwise she waits only the missing time, outside the doorway (09:23 rule: no bumping).
    internal static int DoorApproachHoldMs(float distAhead, float speedMps, int elapsedMs, bool anyTouched)
    {
        if (!anyTouched) return 0;                       // already-open doors: never stop for them
        float v = Math.Max(0.3f, speedMps);
        int reachMs = (int)(distAhead / v * 1000f);
        return Math.Max(0, DoorSwingWaitMs - elapsedMs - reachMs);
    }

    // a pause requested while she is in a doorway: apply it once she is clear (or after DeferredPauseMaxMs)
    static bool DeferPauseIfInDoorway(string reason, string why, Action<string, string> apply)
    {
        if (!routeInDoorZone || !WanderOn) return false;
        var gen = Interlocked.Increment(ref wDeferredPauseGen);
        WLog($"pause ({reason}) deferred: she is in a doorway ({routeDoorZoneWhy}); pausing once clear");
        _ = Task.Run(async () =>
        {
            var t0 = DateTime.Now;
            while (routeInDoorZone && (DateTime.Now - t0).TotalMilliseconds < DeferredPauseMaxMs) await Task.Delay(150);
            if (gen != Volatile.Read(ref wDeferredPauseGen) || !WanderOn || wanderPause != null) return;
            apply(reason, why + string.Format(CultureInfo.InvariantCulture, " (deferred {0:F1} s past the doorway)", (DateTime.Now - t0).TotalSeconds));
        });
        return true;
    }
}
