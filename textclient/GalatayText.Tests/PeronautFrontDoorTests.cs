using System.Text.Json;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-07 09:09: inbound porch -> house leg cut diagonally from node 9 through the wall west of the
/// front double door, never crossed its door cells (so neither leaf was pre-touched) and got stuck. Every upper-floor
/// graph edge that crosses the front door line must go straight through the opening centre and over its 'D' cells,
/// in both directions, with no blocked cells.</summary>
public class PeronautFrontDoorTests
{
    static string Routes(string f)
    {
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "routes", f));
        return File.Exists(repo) ? repo : "/workspace/galatea-sl-repo/textclient/routes/" + f;
    }

    sealed class Grid
    {
        public double X0, Y0, Cell; public int Nx, Ny; public string[] Rows = Array.Empty<string>();
        public char At(double x, double y)
        {
            int i = (int)Math.Floor((x - X0) / Cell), j = (int)Math.Floor((y - Y0) / Cell);
            return i < 0 || j < 0 || i >= Nx || j >= Ny ? '#' : Rows[j][i];
        }
    }

    static (Grid g, double line, double lo, double hi) FrontDoor()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Routes("_nav-peronaut-home.json"))); var r = doc.RootElement;
        var g = new Grid { X0 = r.GetProperty("x0").GetDouble(), Y0 = r.GetProperty("y0").GetDouble(), Cell = r.GetProperty("cell").GetDouble(),
            Nx = r.GetProperty("nx").GetInt32(), Ny = r.GetProperty("ny").GetInt32(), Rows = r.GetProperty("rows").EnumerateArray().Select(x => x.GetString()!).ToArray() };
        var leaves = r.GetProperty("doors").EnumerateArray().Where(d => d.GetProperty("name").GetString()!.StartsWith("front-double")).ToList();
        Assert.Equal(2, leaves.Count);
        double lo = leaves.Min(d => d.GetProperty("min")[0].GetDouble()), hi = leaves.Max(d => d.GetProperty("max")[0].GetDouble());
        double line = leaves.Average(d => (d.GetProperty("min")[1].GetDouble() + d.GetProperty("max")[1].GetDouble()) / 2);
        return (g, line, lo, hi);
    }

    static List<(int a, int b, string name, double[] A, double[] B)> UpperEdges()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Routes("_graph-Peronaut.json"))); var g = doc.RootElement;
        var n = g.GetProperty("nodes").EnumerateArray().Select(x => x.EnumerateArray().Select(v => v.GetDouble()).ToArray()).ToList();
        return g.GetProperty("edges").EnumerateArray().Select(e => (a: e[0].GetInt32(), b: e[1].GetInt32(), name: e[2].GetString()!))
            .Where(e => Math.Min(n[e.a][2], n[e.b][2]) >= 28).Select(e => (e.a, e.b, e.name, n[e.a], n[e.b])).ToList();
    }

    static List<char> Sample(Grid g, double[] A, double[] B)
    {
        int k = Math.Max(1, (int)(Math.Sqrt(Math.Pow(B[0] - A[0], 2) + Math.Pow(B[1] - A[1], 2)) / (g.Cell * 0.5)));
        return Enumerable.Range(0, k + 1).Select(i => g.At(A[0] + (B[0] - A[0]) * i / k, A[1] + (B[1] - A[1]) * i / k)).ToList();
    }

    [Fact]
    public void Front_door_edges_cross_the_opening_centre_in_both_directions()
    {
        var (g, line, lo, hi) = FrontDoor();
        double[] dx = Enumerable.Range(0, (int)((hi - lo) / 0.05)).Select(i => lo + i * 0.05).Where(x => g.At(x, line) == 'D').ToArray();
        Assert.NotEmpty(dx);
        double centre = (dx.Min() + dx.Max()) / 2, half = (dx.Max() - dx.Min()) / 2;
        int crossing = 0; var bad = new List<string>();
        foreach (var e in UpperEdges())
        {
            if ((e.A[1] - line) * (e.B[1] - line) > 0 || e.A[1] == e.B[1]) continue;
            double t = (line - e.A[1]) / (e.B[1] - e.A[1]), x = e.A[0] + (e.B[0] - e.A[0]) * t;
            if (x < lo - 1 || x > hi + 1) continue;
            crossing++;
            if (Math.Abs(x - centre) > Math.Max(0.1, half - 0.3)) bad.Add($"{e.a}-{e.b} {e.name} crosses at x {x:F2}, opening {centre:F2}+-{half:F2}");
            if (Math.Abs(e.A[0] - centre) > 0.15 || Math.Abs(e.B[0] - centre) > 0.15) bad.Add($"{e.a}-{e.b} {e.name} approach/exit nodes not on the opening axis x {centre:F2}");
            foreach (var (P, Q, dir) in new[] { (e.A, e.B, "fwd"), (e.B, e.A, "rev") })
            {
                var s = Sample(g, P, Q);
                if (!s.Contains('D')) bad.Add($"{e.a}-{e.b} {e.name} {dir}: no door cells (leaves would not be pre-touched)");
                if (s.Contains('#')) bad.Add($"{e.a}-{e.b} {e.name} {dir}: hits blocked cells (door frame / wall)");
            }
        }
        Assert.True(crossing > 0, "no graph edge goes through the front door");
        Assert.True(bad.Count == 0, string.Join("; ", bad));
    }

    [Fact]
    public void Front_hall_edges_near_the_door_stay_clear_of_walls()
    {
        var (g, line, lo, hi) = FrontDoor();
        var bad = UpperEdges().Where(e => new[] { e.A, e.B }.Any(p => Math.Abs(p[1] - line) < 6 && p[0] > lo - 2 && p[0] < hi + 2)
                && (e.name.StartsWith("front")) && Sample(g, e.A, e.B).Contains('#'))
            .Select(e => $"{e.a}-{e.b} {e.name}").ToList();
        Assert.True(bad.Count == 0, "front edges through walls: " + string.Join("; ", bad));
    }
}
