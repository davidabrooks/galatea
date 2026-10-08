using System.Text.Json.Nodes;
using GalatayText;
using LibreMetaverse;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-07 19:46 Burgundy pier: she turned onto the pier before its centre line and got STUCK at 218.9,37.7.</summary>
public class NarrowPierTests
{
    static JsonNode Graph()
    {
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "routes", "_graph-Peronaut.json"));
        var p = File.Exists(repo) ? repo : "/workspace/galatea-sl-repo/textclient/routes/_graph-Peronaut.json";
        return JsonNode.Parse(File.ReadAllText(p))!;
    }
    static readonly Program.NarrowCorridor Pier = new("Burgundy pier", new(216.3f, 49.4f), new(216.3f, 17.0f), 2.3f);
    static float[] Cum(List<Vector3> P)
    {
        var c = new float[P.Count];
        for (int i = 1; i < P.Count; i++) c[i] = c[i - 1] + new Vector2(P[i].X - P[i - 1].X, P[i].Y - P[i - 1].Y).Length();
        return c;
    }

    [Fact]
    public void Corridor_contains_the_deck_not_the_stuck_spot_outside_the_rail()
    {
        Assert.True(Program.InCorridor(new(216.3f, 37.7f), Pier));
        Assert.True(Program.InCorridor(new(215.4f, 30.5f), Pier));
        Assert.False(Program.InCorridor(new(218.9f, 37.7f), Pier));   // 19:46 STUCK spot, past the east rail
        Assert.False(Program.InCorridor(new(220.0f, 50.5f), Pier));   // beach
    }

    [Fact]
    public void Graph_pier_nodes_sit_on_the_long_axis_and_inside_the_deck()
    {
        var g = Graph(); var cs = Program.ParseNarrow(g);
        Assert.Contains(cs, c => c.Name == "Burgundy pier");
        var n = g["nodes"]!.AsArray();
        float X(int k) => (float)n[k]![0]!.GetValue<double>(); float Y(int k) => (float)n[k]![1]!.GetValue<double>();
        var places = g["places"]!.AsObject();
        foreach (var name in new[] { "pier-start", "pier", "pier-end" })
            Assert.InRange(X(places[name]!["node"]!.GetValue<int>()), 216.2f, 216.4f);
        // every pier edge stays inside the deck (both ends and the midpoint)
        foreach (var e in g["edges"]!.AsArray())
        {
            var kind = (string)e![2]!;
            if (kind is not ("burgundy-pier" or "pier-bench")) continue;
            int a = e[0]!.GetValue<int>(), b = e[1]!.GetValue<int>();
            if (Y(a) > 49.4f || Y(b) > 49.4f) continue;   // the open root module at the north end
            foreach (var t in new[] { 0f, 0.5f, 1f })
                Assert.True(Program.InCorridor(new(X(a) + (X(b) - X(a)) * t, Y(a) + (Y(b) - Y(a)) * t), Pier), $"edge {a}-{b} leaves the deck");
        }
    }

    [Fact]
    public void Steering_never_looks_past_the_pier_corner()
    {
        // beach-sw -> pier-start (turn) -> straight south along the axis -> bench jog
        var P = new List<Vector3> { new(220.0f, 50.5f, 21f), new(216.3f, 51.9f, 21.2f), new(216.3f, 44.6f, 22.3f), new(216.3f, 30.5f, 24.7f), new(215.4f, 30.5f, 24.7f) };
        var C = Cum(P); var cs = new List<Program.NarrowCorridor> { Pier };
        Assert.Equal(C[1] - 0.5f, Program.NarrowLook(P, C, 0.5f, 8f, cs), 3);        // approach: aim at the pier-start corner, not past it
        Assert.Equal(Program.NarrowMinLook, Program.NarrowLook(P, C, C[1] - 0.3f, 8f, cs), 3);
        Assert.Equal(8f, Program.NarrowLook(P, C, C[2] + 1f, 8f, cs), 3);              // long straight on the axis: full look
        Assert.Equal(3f, Program.NarrowLook(P, C, C[3] - 3f, 8f, cs), 3);              // turn only at the bench
        Assert.Equal(8f, Program.NarrowLook(P, C, 0.5f, 8f, new List<Program.NarrowCorridor>())); // elsewhere unchanged
    }

    [Fact]
    public void No_lane_offsets_on_the_pier()
    {
        var P = new List<Vector3> { new(216.3f, 51.9f, 21f), new(216.3f, 30.5f, 24.7f) };
        var C = Cum(P); var cs = new List<Program.NarrowCorridor> { Pier };
        Assert.True(Program.NoLaneHere(P, C, 14f, new(216.3f, 37.7f), cs));
        var beach = new List<Vector3> { new(228.5f, 53f, 21f), new(220f, 50.5f, 21f) };
        Assert.False(Program.NoLaneHere(beach, Cum(beach), 2f, new(226f, 52f), cs));
    }

    [Fact]
    public void Pier_kind_edges_become_corridors()
    {
        var j = JsonNode.Parse("{\"nodes\":[[0,0,0],[10,0,0],[10,10,0]],\"edges\":[[0,1,\"beach\"],[1,2,\"old-dock\"]]}");
        var cs = Program.ParseNarrow(j);
        Assert.Single(cs);
        Assert.True(Program.InCorridor(new(10.5f, 5f), cs[0]));
        Assert.False(Program.InCorridor(new(5f, 0f), cs[0]));
    }
}
