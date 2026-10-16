using System.Text.Json;
using GalatayText;
using LibreMetaverse;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-16 15:50 David: "you got stuck on that chair". Standing from a Reiley net chair and walking north (home, front, ...)
/// ended in STUCK x4 at 233.2-233.9,58.1. 40 STUCK events since 10-08 sit at 232-233,58: the patio route ran north through the 0.75 m
/// gap between the east chair's right side (x 233.5) and the patio rail (x ~235), tighter than her walking accuracy. The chairs
/// had no furniture footprint, so CI could not see it. Now both chairs are footprints, the south patio no longer links north past
/// them (it goes west, round through patio-1), and node 5 (patio-se) is reached along the south edge, y 56.4.</summary>
public class PeronautReileyChairExitTests
{
    const string WestChair = "7beb04ac-b4cc-8e7d-98b2-d5a4683afcbc", EastChair = "cab3241e-97e4-4ad6-f795-008180ea2d74";
    // scene export-20261014-153726: chair rotZ; she stands 0.7 m in front of the seat (log 15:50:52: seat 230.28,59.11 -> stood at 230.2,58.4)
    static readonly Dictionary<string, double> RotZDeg = new() { [WestChair] = -12, [EastChair] = -40 };

    static string RoutesDir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "routes"));
    static string GraphFile() => Path.Combine(RoutesDir, "_graph-Peronaut.json");
    static (float x0, float y0, float x1, float y1) Footprint(string uuid)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(GraphFile()));
        var f = doc.RootElement.GetProperty("furniture_footprints").EnumerateArray().Single(e => e.GetProperty("uuid").GetString() == uuid);
        var mn = f.GetProperty("min"); var mx = f.GetProperty("max");
        return ((float)mn[0].GetDouble(), (float)mn[1].GetDouble(), (float)mx[0].GetDouble(), (float)mx[1].GetDouble());
    }
    static Vector3 SeatPos(string uuid)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RoutesDir, "_seats-peronaut-home.json")));
        var s = doc.RootElement.GetProperty("seats").EnumerateArray().Single(e => e.GetProperty("uuid").GetString() == uuid).GetProperty("pos");
        return new Vector3((float)s[0].GetDouble(), (float)s[1].GetDouble(), 29.0f);
    }
    static Vector3 StandPos(string uuid)
    {
        var p = SeatPos(uuid); var t = RotZDeg[uuid] * Math.PI / 180;
        return new Vector3(p.X + (float)(0.7 * Math.Sin(t)), p.Y - (float)(0.7 * Math.Cos(t)), p.Z);
    }
    static bool Hits(Vector3 a, Vector3 b, (float x0, float y0, float x1, float y1) f, float m) =>
        PeronautFurnitureFootprintTests.SegmentHitsBox(a.X, a.Y, b.X, b.Y, f.x0 - m, f.y0 - m, f.x1 + m, f.y1 + m);

    public static IEnumerable<object[]> ChairsAndTargets() =>
        new[] { WestChair, EastChair }.SelectMany(c => new[] { "home", "front", "living", "patio-east", "patio-west", "patio-sw", "patio-se", "chairs", "east-deck", "bed", "porch", "desk" }.Select(t => new object[] { c, t }));

    [Fact]
    public void Both_chairs_are_footprints_and_the_stand_point_matches_the_log()
    {
        Footprint(WestChair); Footprint(EastChair);
        var w = StandPos(WestChair);
        Assert.InRange(w.X, 230.0f, 230.3f); Assert.InRange(w.Y, 58.3f, 58.5f);   // logged: 230.2,58.4
    }

    [Theory]
    [MemberData(nameof(ChairsAndTargets))]
    public void Route_from_standing_up_never_walks_into_either_chair(string chair, string target)
    {
        var g = Program.LoadGraphFile(GraphFile());
        var from = StandPos(chair); var other = chair == WestChair ? EastChair : WestChair;
        var (r, err) = Program.GraphRoute(g, from, g.Places[target].node);
        Assert.Null(err);
        // the join step (stand point -> first path point) starts inside her own chair's bowl; it must not cross the neighbour
        Assert.False(Hits(r[0], r[1], Footprint(other), 0f), $"{chair[..4]} -> {target}: the join step crosses the other chair");
        for (int i = 1; i + 1 < r.Count; i++)
            foreach (var c in new[] { WestChair, EastChair })
                Assert.False(Hits(r[i], r[i + 1], Footprint(c), 0.3f), $"{chair[..4]} -> {target}: path {i} ({r[i]} -> {r[i + 1]}) hits chair {c[..4]}");
    }

    [Theory]
    [InlineData("chairs", "patio-east")]
    [InlineData("patio-se", "home")]
    public void Patio_east_and_south_east_corner_are_reached_without_the_gap_east_of_the_chairs(string from, string to)
    {
        var g = Program.LoadGraphFile(GraphFile());
        var (r, err) = Program.GraphRoute(g, g.N[g.Places[from].node], g.Places[to].node);
        Assert.Null(err);
        for (int i = 0; i + 1 < r.Count; i++)   // nothing runs north past x 233.5-235 between y 57.6 and 60.4 (the 0.75 m gap)
            Assert.False(PeronautFurnitureFootprintTests.SegmentHitsBox(r[i].X, r[i].Y, r[i + 1].X, r[i + 1].Y, 233.4, 57.6, 235.2, 60.4), $"{from}->{to}: {r[i]} -> {r[i + 1]} goes through the gap");
    }

    [Fact]
    public void Patio_se_node_stays_reachable_along_the_south_edge()
    {
        var g = Program.LoadGraphFile(GraphFile());
        var (r, err) = Program.GraphRoute(g, g.N[g.Places["chairs"].node], g.Places["patio-se"].node);
        Assert.Null(err);
        Assert.Contains(r, p => MathF.Abs(p.Y - 56.4f) < 0.01f);
        Assert.All(r, p => Assert.True(p.Y < 57.3f || p.X < 229f, $"{p} climbs toward the chairs"));
    }
}
