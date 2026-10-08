using GalatayText;
using LibreMetaverse;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-07 21:18 "stuck under the house" (straight-line approach) and 21:19 "bikini inside the house".</summary>
public class HomeSafetyTests
{
    static Program.Graph Graph()
    {
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "routes", "_graph-Peronaut.json"));
        return Program.LoadGraphFile(File.Exists(repo) ? repo : "/workspace/galatea-sl-repo/textclient/routes/_graph-Peronaut.json");
    }
    static readonly Vector3 StartLower = new(241.8f, 67.2f, 25.5f);   // 21:18 where David's chat paused her
    static readonly Vector3 DavidUpstairs = new(229.9f, 69.9f, 29.0f);

    [Fact]
    public void Under_house_and_indoors_classification()
    {
        Assert.True(Program.UnderHouse(new(229.4f, 70.0f, 26.0f)));        // 21:18 stuck spot
        Assert.True(Program.IndoorsAtHome(new(228.9f, 69.0f, 29.0f)));    // home / arrival point
        Assert.True(Program.IndoorsAtHome(new(228.0f, 65.0f, 29.0f)));    // living
        Assert.False(Program.IndoorsAtHome(new(230.6f, 57.0f, 29.0f)));   // patio chairs (outside)
        Assert.False(Program.IndoorsAtHome(new(242.0f, 75.0f, 29.0f)));   // east deck (outside)
        Assert.False(Program.UnderHouse(StartLower));
        Assert.False(Program.UnderHouse(new(217.1f, 82.8f, 25.5f)));      // porch rockers
        Assert.False(Program.UnderHouse(new(219.3f, 60.3f, 21.1f)));      // rowboat
        Assert.All(Graph().N, n => Assert.False(Program.UnderHouse(n), $"graph node {n} is under the house"));
    }

    [Fact]
    public void Approach_to_David_upstairs_goes_over_the_graph_never_under_the_house()
    {
        var g = Graph();
        var (pts, err) = Program.PlanHomeApproach(g, StartLower, DavidUpstairs, 2.5f);
        Assert.Null(err);
        Assert.True(pts.Count > 2, "not a straight 2-point line");
        Assert.DoesNotContain(pts, Program.UnderHouse);
        var end = pts[^1];
        Assert.True(end.Z > 27.5f, $"ends on the house floor (z {end.Z:F1})");
        Assert.InRange(new Vector2(end.X - DavidUpstairs.X, end.Y - DavidUpstairs.Y).Length(), 0f, 4.5f);
        float len = 0; for (int i = 1; i < pts.Count; i++) len += new Vector2(pts[i].X - pts[i - 1].X, pts[i].Y - pts[i - 1].Y).Length();
        Assert.True(len > 15f, $"via the stairs, not the 12 m straight line ({len:F0} m)");
    }

    [Fact]
    public void Refuses_targets_below_the_floor_or_off_the_graph()
    {
        var g = Graph();
        Assert.Contains("below the house floor", Program.PlanHomeApproach(g, StartLower, new(229.9f, 69.9f, 25.5f), 2.5f).err);
        Assert.Contains("off the path graph", Program.PlanHomeApproach(g, StartLower, new(150f, 150f, 22f), 2.5f).err);
    }

    [Fact]
    public void Exit_node_from_under_the_house_is_outside_and_below_the_floor()
    {
        var g = Graph(); var me = new Vector3(229.4f, 70.0f, 26.0f);
        int k = Program.UnderHouseExitNode(g, me);
        Assert.True(k >= 0);
        Assert.False(Program.UnderHouse(g.N[k]));
        Assert.True(g.N[k].Z < Program.HouseFloorZ - 1.5f);
    }

    [Fact]
    public void Trim_stops_short_of_the_target()
    {
        var pts = new List<Vector3> { new(0, 0, 20), new(10, 0, 20) };
        var t = Program.TrimRouteEnd(pts, new Vector3(10, 0, 20), 2.5f);
        Assert.InRange(t[^1].X, 7.4f, 7.6f);
        Assert.Equal(10f, Program.TrimRouteEnd(pts, new Vector3(10, 0, 26), 2.5f)[^1].X); // other level: no early stop
    }

    [Fact]
    public void Bikini_indoors_before_the_beach_and_back_only_inside()
    {
        Assert.True(Program.BeachBound(new(214.8f, 30.5f, 23.9f), "lower-pier"));
        Assert.True(Program.BeachBound(new(219.3f, 60.3f, 21.1f), "lower-beach"));
        Assert.True(Program.BeachBound(new(228.5f, 53.0f, 21.2f), null));          // beach place
        Assert.False(Program.BeachBound(new(217.1f, 82.8f, 25.5f), "lower-porch"));
        Assert.False(Program.BeachBound(new(228.0f, 65.0f, 29.0f), null));
        Assert.Equal("change-here", Program.IndoorBikiniPlan(true, false, true, false));
        Assert.Equal("none", Program.IndoorBikiniPlan(true, true, true, false));        // already in the bikini
        Assert.Equal("none", Program.IndoorBikiniPlan(false, false, true, false));
        Assert.Equal("none", Program.IndoorBikiniPlan(true, false, false, true));       // already on the beach: zone safety net
        Assert.Equal("ARTi tubetop", Program.BeachRememberChoice("ARTi tubetop", null));
        Assert.Equal("Jani tshirt", Program.BeachRememberChoice("Bikini", "Jani tshirt"));
        Assert.Null(Program.BeachRememberChoice("Bikini", "Spicy"));
        Assert.Equal("none", Program.ZoneAction("beach", true, true, false));
        Assert.Equal("mark-beach", Program.ZoneAction("beach", false, true, false));   // never re-wear a worn bikini
        Assert.Equal("bikini-on", Program.ZoneAction("beach", false, false, false));
        Assert.Equal("wait-indoors", Program.ZoneAction("house", true, true, false)); // porch / patio: wait
        Assert.Equal("restore", Program.ZoneAction("house", true, true, true));
        Assert.Equal("none", Program.ZoneAction("house", false, false, true));
    }

    [Fact]
    public void Outside_with_a_beach_target_detours_indoors_before_the_change()
    {
        // 2026-10-08 08:14: on patio-sw (outside) when 'Burgundy Pier . Bench' was picked
        var patioSw = new Vector3(221.5f, 57.2f, 29.0f);
        Assert.False(Program.IndoorsAtHome(patioSw));
        Assert.NotEqual("beach", Program.OutfitZoneFor("Peronaut", patioSw, false));
        bool beach = Program.BeachBound(new(214.8f, 30.5f, 23.9f), "lower-pier");
        Assert.Equal("detour-indoors", Program.IndoorBikiniPlan(beach, false, Program.IndoorsAtHome(patioSw), false));
        Assert.Equal("none", Program.IndoorBikiniPlan(beach, true, false, false));      // bikini already on: no detour
    }
}
