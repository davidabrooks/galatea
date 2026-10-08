using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LibreMetaverse;

namespace GalatayText;

// 2026-10-07 19:46 David: "You didn't enter that pier correctly" / "You turned before you reached the center of it".
// The walk to the second Burgundy pier bench cut the corner onto the pier from the side and got STUCK at 218.9,37.7
// (outside the east rail at x 218.6). Narrow corridors (piers, docks) come from the graph's "narrow" list plus any
// edge whose kind names a pier/dock/jetty/boardwalk/bridge. On or right before such a segment she:
//  - never looks past the next vertex (no corner cutting): she walks to the corner, then turns;
//  - never takes lane offsets: no stuck-recovery lane +-1 m and no sidestep around people.
public static partial class Program
{
    internal sealed record NarrowCorridor(string Name, Vector2 A, Vector2 B, float HalfWidth);
    internal const float NarrowEdgeHalfWidth = 1.2f, NarrowMinLook = 1.6f;   // min look > the ~1 m autopilot stop distance
    static readonly Regex NarrowEdgeKind = new(@"pier|dock|jetty|boardwalk|bridge|gangway", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // pure: inside the corridor rectangle (segment A-B, +-HalfWidth, not past the ends)
    internal static bool InCorridor(Vector2 p, NarrowCorridor c)
    {
        var d = c.B - c.A; float l2 = d.LengthSquared();
        if (l2 < 1e-6f) return Vector2.Distance(p, c.A) <= c.HalfWidth;
        float t = ((p.X - c.A.X) * d.X + (p.Y - c.A.Y) * d.Y) / l2;
        if (t < 0f || t > 1f) return false;
        var q = c.A + d * t;
        return Vector2.Distance(p, q) <= c.HalfWidth;
    }
    internal static bool InAnyNarrow(Vector2 p, IReadOnlyList<NarrowCorridor> cs) => cs != null && cs.Any(c => InCorridor(p, c));

    // pure: polyline segment i (P[i] -> P[i+1]) runs in a narrow corridor (midpoint, or both ends, inside one)
    internal static bool SegNarrow(IReadOnlyList<Vector3> P, int i, IReadOnlyList<NarrowCorridor> cs)
    {
        if (cs == null || cs.Count == 0 || i < 0 || i >= P.Count - 1) return false;
        Vector2 a = new(P[i].X, P[i].Y), b = new(P[i + 1].X, P[i + 1].Y);
        return InAnyNarrow((a + b) / 2f, cs) || (InAnyNarrow(a, cs) && InAnyNarrow(b, cs));
    }

    // pure: segment index for path distance s over cumulative lengths C
    internal static int SegAt(IReadOnlyList<float> C, float s)
    {
        for (int i = 0; i < C.Count - 2; i++) if (s < C[i + 1]) return i;
        return Math.Max(0, C.Count - 2);
    }

    // pure: steering look-ahead on/next to a narrow segment: never past the next corner (min NarrowMinLook)
    internal static float NarrowLook(IReadOnlyList<Vector3> P, IReadOnlyList<float> C, float s, float lookD, IReadOnlyList<NarrowCorridor> cs)
    {
        int i = SegAt(C, s);
        if (i + 1 >= P.Count - 1) return lookD;                 // last segment: no corner ahead
        if (!SegNarrow(P, i, cs) && !SegNarrow(P, i + 1, cs)) return lookD;
        return Math.Min(lookD, Math.Max(NarrowMinLook, C[i + 1] - s));
    }

    // pure: no lane offsets here (on a narrow segment or standing in a corridor)
    internal static bool NoLaneHere(IReadOnlyList<Vector3> P, IReadOnlyList<float> C, float s, Vector2 me, IReadOnlyList<NarrowCorridor> cs) =>
        SegNarrow(P, SegAt(C, s), cs) || InAnyNarrow(me, cs);

    // pure: corridors from a graph JSON ("narrow" list + pier/dock-kind edges)
    internal static List<NarrowCorridor> ParseNarrow(JsonNode j)
    {
        var res = new List<NarrowCorridor>();
        if (j == null) return res;
        if (j["narrow"] is JsonArray na)
            foreach (var n in na)
            {
                if (n?["a"] is not JsonArray a || n["b"] is not JsonArray b) continue;
                float hw = n["half_width"] is JsonNode h ? (float)h.GetValue<double>() : NarrowEdgeHalfWidth;
                res.Add(new((string)n["name"] ?? "narrow", new((float)a[0]!.GetValue<double>(), (float)a[1]!.GetValue<double>()),
                    new((float)b[0]!.GetValue<double>(), (float)b[1]!.GetValue<double>()), hw));
            }
        if (j["nodes"] is JsonArray nodes && j["edges"] is JsonArray edges)
            foreach (var e in edges)
            {
                var kind = (string)e?[2] ?? "";
                if (!NarrowEdgeKind.IsMatch(kind)) continue;
                int ia = e![0]!.GetValue<int>(), ib = e[1]!.GetValue<int>();
                if (ia < 0 || ib < 0 || ia >= nodes.Count || ib >= nodes.Count) continue;
                Vector2 V(int k) => new((float)nodes[k]![0]!.GetValue<double>(), (float)nodes[k]![1]!.GetValue<double>());
                res.Add(new($"edge {ia}-{ib} {kind}", V(ia), V(ib), NarrowEdgeHalfWidth));
            }
        return res;
    }
    static List<NarrowCorridor> NarrowFor(string region)
    {
        try
        {
            var f = Path.Combine(RouteDir, $"_graph-{region}.json");
            return File.Exists(f) ? ParseNarrow(JsonNode.Parse(File.ReadAllText(f))) : new();
        }
        catch (Exception ex) { RLogR($"narrow corridors for {region}: {ex.Message}"); return new(); }
    }
}
