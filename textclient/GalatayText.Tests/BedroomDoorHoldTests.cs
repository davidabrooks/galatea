using GalatayText;
using Xunit;
using Vector2 = LibreMetaverse.Vector2;
using Vector3 = LibreMetaverse.Vector3;

namespace GalatayText.Tests;

/// <summary>2026-10-16 David: walking out of the bedroom she almost crossed the living room (39 -> 45 -> 44 -> 48, east and north to the
/// desk side and back) before turning to the front door. Now: a direct 39-48 edge, and a deliberate door hold at node 39 (just past
/// the side-room-1 doorway): she stops, faces the front doors, opens them from there, then goes outside. Real graph and nav grid.</summary>
public class BedroomDoorHoldTests
{
    static string Routes(string f) => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "routes", f));
    static Program.Graph G() => Program.LoadGraphFile(Routes("_graph-Peronaut.json"));
    static Program.NavGrid Grid() => Program.LoadNavGrid(Routes("_nav-peronaut-home.json"), DateTime.Now);
    static Func<float, Vector2> At(Program.Poly p) => u => { var v = p.At(u); return new Vector2(v.X, v.Y); };
    static Program.Poly Route(Vector3 from, string place)
    {
        var g = G(); var (pts, err) = Program.GraphRoute(g, from, g.Places[place].node);
        Assert.Null(err); return new Program.Poly(pts);
    }
    static readonly Vector3 Bed = new(218.32f, 75.2f, 29.0f);

    [Fact]
    public void Graph_has_the_bedroom_door_hold_and_a_direct_edge_past_the_door()
    {
        var g = G();
        var edge = Assert.Single(g.E, e => (e.a, e.b) == (39, 48));
        Assert.True(Program.IsOneWay(edge.kind));
        var h = Assert.Single(g.Holds);
        Assert.Equal((40, 39, "front-double"), (h.From, h.At, h.Opens));
        Assert.InRange(h.Ms, 1000, 1500);
        Assert.NotNull(h.Look);
    }

    [Theory]
    [InlineData("front")] [InlineData("front-steps")] [InlineData("beach")] [InlineData("pier")]
    public void Bedroom_to_outside_goes_straight_from_the_bedroom_door_to_the_front_door(string place)
    {
        var g = G(); var poly = Route(Bed, place);
        // no detour east/north to the desk side: nodes 45 (228.2,74.0) and 44 (230,72.4) are not on the way
        Assert.DoesNotContain(poly.P, p => Vector3.Distance(p, g.N[45]) < 0.3f || Vector3.Distance(p, g.N[44]) < 0.3f);
        int i39 = poly.P.FindIndex(p => Vector3.Distance(p, g.N[39]) < 0.15f);
        Assert.True(i39 > 0 && Vector3.Distance(poly.P[i39 + 1], g.N[48]) < 0.15f, "39 is followed by 48");
        // everything between the bedroom and the front door stays south of the bedroom door line (y >= 75.2) and west of x 228.1
        foreach (var p in poly.P.Take(i39 + 2)) { Assert.True(p.Y >= 75.19f, $"y {p.Y} at {p.X}"); Assert.True(p.X <= 228.1f, $"x {p.X}"); }
    }

    [Fact]
    public void New_edge_is_clear_of_walls_and_furniture()
    {
        var g = G(); var ng = Grid(); var a = g.N[39]; var b = g.N[48];
        int n = (int)(Vector3.Distance(a, b) / (ng.Cell * 0.5f));
        for (int k = 0; k <= n; k++)
        {
            var p = Vector3.Lerp(a, b, k / (float)n); var (i, j) = ng.IJ(p.X, p.Y);
            Assert.Equal(0, ng.At(i, j));   // free floor: no wall, frame or door cell
        }
        Assert.True(b.Y < 77.7f && a.Y > 75f, "stays well north of the front door line (y 79.6) and south of the desk (y 72.2)");
    }

    [Fact]
    public void Hold_sits_at_node_39_when_leaving_the_bedroom_for_the_front_doors()
    {
        var g = G(); var ng = Grid(); var poly = Route(Bed, "beach");
        var holds = Program.HoldsOnPoly(g, poly).Where(h => Program.HoldDoors(new[] { ng }, poly, h, null) != null).ToList();
        var h = Assert.Single(holds);
        Assert.True(Vector3.Distance(h.At, g.N[39]) < 0.01f);
        Assert.InRange(h.S, 8.0f, 9.5f);                                      // bed -> 40 -> 39 = 3.7 + 4.5 m
        Assert.True(Vector3.Distance(poly.At(h.S), g.N[39]) < 0.05f);
        // the hold is after the bedroom door panel (x 224.25) and before the front doors, with the doors ahead
        Assert.True(h.At.X > 224.25f + 1.5f);
        var (dist, doors) = Program.HoldDoors(new[] { ng }, poly, h, null)!.Value;
        Assert.Contains(doors, d => d.Name == "front-double-east"); Assert.Contains(doors, d => d.Name == "front-double-west");
        Assert.InRange(dist, 3f, 7f);
    }

    [Fact]
    public void Hold_wait_lets_the_leaf_swing_for_the_walk_to_the_doors_and_is_about_a_beat()
    {
        var g = G(); var ng = Grid(); var poly = Route(Bed, "beach");
        var h = Program.HoldsOnPoly(g, poly).Single(); var (dist, _) = Program.HoldDoors(new[] { ng }, poly, h, null)!.Value;
        int wait = Program.DoorPreOpenWaitFor(dist);
        Assert.InRange(wait, 500, 1800);   // door open lag + margin less the ~1.5 s walk to the doors
        Assert.InRange(Math.Max(h.Ms, wait), 1000, 2000);   // she stands from the touch until a leaf can have swung: one beat of ~1.2-2 s
    }

    [Fact]
    public void Front_doors_are_not_touched_before_the_hold_but_the_bedroom_door_is()
    {
        var g = G(); var ng = Grid(); var poly = Route(Bed, "beach");
        var h = Program.HoldsOnPoly(g, poly).Single();
        // unrestricted: the early scan reaches the front doors from the bed (that is the old behaviour the hold replaces)
        var all = Program.DoorsDueAhead(new[] { ng }, u => poly.At(u), 0, poly.Len, new HashSet<LibreMetaverse.UUID>(), Program.DoorLeadTriggerM(0));
        Assert.NotNull(all); Assert.Contains(all!.Value.doors, d => d.Name == "side-room-1");
        Assert.Contains(all.Value.doors, d => d.Name.StartsWith("front-double"));
        // limited to the hold: only the bedroom door
        var lim = Program.DoorsDueAhead(new[] { ng }, u => poly.At(u), 0, h.S, new HashSet<LibreMetaverse.UUID>(), Program.DoorLeadTriggerM(0));
        Assert.NotNull(lim); Assert.Equal(new[] { "side-room-1" }, lim!.Value.doors.Select(d => d.Name).ToArray());
        var pre = Program.DoorsToPreOpen(ng, At(poly), h.S);
        Assert.NotNull(pre); Assert.DoesNotContain(pre!.Value.doors, d => d.Name.StartsWith("front-double"));
        Assert.InRange(pre.Value.dist, 0f, Program.DoorPreOpenM);               // start pre-open of the bedroom door still happens
    }

    [Theory]
    [InlineData("patio-west")] [InlineData("living")] [InlineData("east-deck")]
    public void Routes_that_do_not_go_to_the_front_doors_do_not_hold(string place)
    {
        var g = G(); var ng = Grid(); var poly = Route(Bed, place);
        Assert.Empty(Program.HoldsOnPoly(g, poly).Where(h => Program.HoldDoors(new[] { ng }, poly, h, null) != null));
    }

    [Theory]
    [InlineData("beach")] [InlineData("pier")] [InlineData("front-steps")]
    public void Inbound_routes_never_hold(string from)
    {
        var g = G(); var ng = Grid(); var poly = Route(g.N[g.Places[from].node], "bed");
        Assert.Empty(Program.HoldsOnPoly(g, poly));
        poly = Route(g.N[g.Places[from].node], "bedroom");
        Assert.Empty(Program.HoldsOnPoly(g, poly));
    }

    [Fact]
    public void Inbound_route_is_unchanged_because_the_shortcut_is_one_way()
    {
        var g = G(); var ng = Grid(); var poly = Route(g.N[g.Places["front"].node], "bed");
        Assert.Equal(new[] { 9, 48, 44, 45, 39, 40, 12 }.Select(i => g.N[i]).ToArray(), poly.P.ToArray());
        // 9 -> 48 still runs over the front door cells on the opening axis (both leaves pre-open through the pair group)
        var pre = Program.DoorsToPreOpen(ng, At(poly), poly.Len);
        Assert.NotNull(pre); Assert.Contains(pre!.Value.doors, d => d.Name == "front-double-east"); Assert.Contains(pre.Value.doors, d => d.Name == "front-double-west");
    }

    [Fact]
    public void One_way_edge_is_used_outbound_only_by_graph_routes()
    {
        var g = G(); bool Uses(string from, string to) { var r = Program.GraphRoute(g, g.N[g.Places[from].node], g.Places[to].node).pts!; return r.Zip(r.Skip(1)).Any(x => (Vector3.Distance(x.First, g.N[39]) < .1f && Vector3.Distance(x.Second, g.N[48]) < .1f) || (Vector3.Distance(x.First, g.N[48]) < .1f && Vector3.Distance(x.Second, g.N[39]) < .1f)); }
        Assert.True(Uses("bed", "front"));
        Assert.False(Uses("front", "bed"));
        Assert.False(Uses("beach", "bedroom"));
        Assert.False(Uses("front", "west-door"));
    }

    [Fact]
    public void Hold_needs_the_walk_to_pass_node_40_to_39()
    {
        var g = G(); var ng = Grid();
        bool Holds(Vector3 from) { var poly = Route(from, "front"); return Program.HoldsOnPoly(g, poly).Any(h => Program.HoldDoors(new[] { ng }, poly, h, null) != null); }
        Assert.True(Holds(Bed));                                      // from the bed
        Assert.True(Holds(g.N[57]));                                  // from the bedroom node
        Assert.True(Holds(new Vector3(223.6f, 75.3f, 29.0f)));        // already standing in the doorway, between 40 and 39
        Assert.False(Holds(g.N[39]));                                 // standing at the hold point itself
        Assert.False(Holds(new Vector3(227.5f, 76.0f, 29.0f)));       // already in the room
        Assert.False(Holds(g.N[45]));                                 // home point
    }

    [Fact]
    public void Poly_keeps_the_hold_vertex_when_a_leg_is_cut_further_along()
    {
        var g = G(); var poly = Route(Bed, "beach");
        var h = Program.HoldsOnPoly(g, poly).Single();
        // a wander leg that is trimmed (Wander.cs adds poly.At(cut)) keeps node 39 as a vertex, so the hold survives
        var cut = new List<Vector3>(); float c = 3f; cut.Add(poly.At(c)); foreach (var p in poly.P) if (poly.C[poly.P.IndexOf(p)] > c + 0.05f) cut.Add(p);
        Assert.Single(Program.HoldsOnPoly(g, new Program.Poly(cut)));
        Assert.Equal(h.S - c, Program.HoldsOnPoly(g, new Program.Poly(cut)).Single().S, 0.1f);
    }
}
