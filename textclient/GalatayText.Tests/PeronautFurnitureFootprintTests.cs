using System.Text.Json;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-07: no Peronaut graph edge may cross the couch / desk footprints (she walked through the Lalou sofa).</summary>
public class PeronautFurnitureFootprintTests
{
    static JsonDocument Graph()
    {
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "routes", "_graph-Peronaut.json"));
        var p = File.Exists(repo) ? repo : "/workspace/galatea-sl-repo/textclient/routes/_graph-Peronaut.json";
        return JsonDocument.Parse(File.ReadAllText(p));
    }

    // Liang-Barsky segment vs axis-aligned box
    public static bool SegmentHitsBox(double ax, double ay, double bx, double by, double x0, double y0, double x1, double y1)
    {
        double t0 = 0, t1 = 1, dx = bx - ax, dy = by - ay;
        double[] p = { -dx, dx, -dy, dy }, q = { ax - x0, x1 - ax, ay - y0, y1 - ay };
        for (int i = 0; i < 4; i++)
        {
            if (p[i] == 0) { if (q[i] < 0) return false; continue; }
            var r = q[i] / p[i];
            if (p[i] < 0) { if (r > t1) return false; if (r > t0) t0 = r; }
            else { if (r < t0) return false; if (r < t1) t1 = r; }
        }
        return true;
    }

    [Fact]
    public void Segment_box_helper_basic_cases()
    {
        Assert.True(SegmentHitsBox(0, 0, 10, 0, 4, -1, 6, 1));
        Assert.False(SegmentHitsBox(0, 2, 10, 2, 4, -1, 6, 1));
        Assert.True(SegmentHitsBox(5, 0, 5, 0.5, 4, -1, 6, 1));
        Assert.False(SegmentHitsBox(0, 0, 3, 3, 4, -1, 6, 1));
    }

    [Fact]
    public void No_Peronaut_edge_crosses_living_room_furniture()
    {
        using var doc = Graph(); var g = doc.RootElement;
        var nodes = g.GetProperty("nodes").EnumerateArray().Select(n => (x: n[0].GetDouble(), y: n[1].GetDouble())).ToList();
        var feet = g.GetProperty("furniture_footprints").EnumerateArray().ToList();
        double m = g.TryGetProperty("footprint_margin", out var mm) ? mm.GetDouble() : 0.3;
        Assert.Contains(feet, f => f.GetProperty("name").GetString()!.Contains("Sofa"));
        Assert.Contains(feet, f => f.GetProperty("name").GetString()!.Contains("desk"));
        var bad = new List<string>();
        foreach (var e in g.GetProperty("edges").EnumerateArray())
        {
            int a = e[0].GetInt32(), b = e[1].GetInt32();
            foreach (var f in feet)
            {
                var mn = f.GetProperty("min"); var mx = f.GetProperty("max");
                if (SegmentHitsBox(nodes[a].x, nodes[a].y, nodes[b].x, nodes[b].y, mn[0].GetDouble() - m, mn[1].GetDouble() - m, mx[0].GetDouble() + m, mx[1].GetDouble() + m))
                    bad.Add($"{a}-{b} {e[2].GetString()} x {f.GetProperty("name").GetString()}");
            }
        }
        Assert.True(bad.Count == 0, "edges crossing furniture: " + string.Join("; ", bad));
    }

    [Fact]
    public void Home_place_is_the_arrival_point_off_the_couch()
    {
        using var doc = Graph(); var g = doc.RootElement;
        var n = g.GetProperty("places").GetProperty("home").GetProperty("node").GetInt32();
        var p = g.GetProperty("nodes")[n];
        Assert.InRange(p[0].GetDouble(), 227.7, 228.7);
        Assert.InRange(p[1].GetDouble(), 73.5, 74.5);
    }
}
