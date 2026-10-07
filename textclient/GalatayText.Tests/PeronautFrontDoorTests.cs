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

    // door groups: (name, alongX, line, lo, hi) - double leaves merged by name prefix
    static (Grid g, List<(string name, bool ax, double line, double lo, double hi)> doors) Doors()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Routes("_nav-peronaut-home.json"))); var r = doc.RootElement;
        var g = new Grid { X0 = r.GetProperty("x0").GetDouble(), Y0 = r.GetProperty("y0").GetDouble(), Cell = r.GetProperty("cell").GetDouble(),
            Nx = r.GetProperty("nx").GetInt32(), Ny = r.GetProperty("ny").GetInt32(), Rows = r.GetProperty("rows").EnumerateArray().Select(x => x.GetString()!).ToArray() };
        var list = r.GetProperty("doors").EnumerateArray().Select(d => (name: d.GetProperty("name").GetString()!, ax: d.GetProperty("axis").GetString() == "x",
            mn: d.GetProperty("min").EnumerateArray().Select(v => v.GetDouble()).ToArray(), mx: d.GetProperty("max").EnumerateArray().Select(v => v.GetDouble()).ToArray()))
            .GroupBy(d => d.name.StartsWith("front-double") ? "front-double" : d.name)
            .Select(k => (k.Key, k.First().ax,
                k.Average(d => d.ax ? (d.mn[1] + d.mx[1]) / 2 : (d.mn[0] + d.mx[0]) / 2),
                k.Min(d => d.ax ? d.mn[0] : d.mn[1]), k.Max(d => d.ax ? d.mx[0] : d.mx[1]))).ToList();
        Assert.Equal(5, list.Count);
        return (g, list);
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

    // u = along the door line, v = across it
    static double U(double[] p, bool ax) => ax ? p[0] : p[1];
    static double V(double[] p, bool ax) => ax ? p[1] : p[0];

    [Fact]
    public void Every_door_edge_crosses_its_opening_centre_in_both_directions()
    {
        var (g, doors) = Doors(); var bad = new List<string>();
        foreach (var d in doors)
        {
            var du = Enumerable.Range(0, (int)((d.hi - d.lo + 1) / 0.05)).Select(i => d.lo - 0.5 + i * 0.05)
                .Where(u => Enumerable.Range(-3, 7).Any(k => (d.ax ? g.At(u, d.line + k * 0.1) : g.At(d.line + k * 0.1, u)) == 'D')).ToArray();
            Assert.True(du.Length > 0, d.name + ": no door cells");
            double centre = (du.Min() + du.Max()) / 2, half = (du.Max() - du.Min()) / 2;
            int crossing = 0;
            foreach (var e in UpperEdges())
            {
                double v0 = V(e.A, d.ax), v1 = V(e.B, d.ax);
                if ((v0 - d.line) * (v1 - d.line) > 0 || v0 == v1) continue;
                double t = (d.line - v0) / (v1 - v0), u = U(e.A, d.ax) + (U(e.B, d.ax) - U(e.A, d.ax)) * t;
                if (u < d.lo - 1 || u > d.hi + 1) continue;
                crossing++;
                var tag = $"{d.name} {e.a}-{e.b} {e.name}";
                if (Math.Abs(u - centre) > Math.Max(0.1, half - 0.3)) bad.Add($"{tag} crosses at {u:F2}, opening {centre:F2}+-{half:F2}");
                if (Math.Abs(U(e.A, d.ax) - centre) > 0.15 || Math.Abs(U(e.B, d.ax) - centre) > 0.15) bad.Add($"{tag}: approach/exit nodes not on the opening axis {centre:F2}");
                foreach (var (P, Q, dir) in new[] { (e.A, e.B, "fwd"), (e.B, e.A, "rev") })
                {
                    var s = Sample(g, P, Q);
                    if (!s.Contains('D')) bad.Add($"{tag} {dir}: no door cells (door would not be pre-touched)");
                    if (s.Contains('#')) bad.Add($"{tag} {dir}: hits blocked cells (door frame / wall)");
                }
            }
            if (crossing == 0) bad.Add(d.name + ": no graph edge goes through it");
        }
        Assert.True(bad.Count == 0, string.Join("; ", bad));
    }

    [Fact]
    public void Front_hall_edges_near_the_door_stay_clear_of_walls()
    {
        var (g, doors) = Doors(); var d = doors.Single(x => x.name == "front-double");
        var bad = UpperEdges().Where(e => new[] { e.A, e.B }.Any(p => Math.Abs(p[1] - d.line) < 6 && p[0] > d.lo - 2 && p[0] < d.hi + 2)
                && e.name.StartsWith("front") && Sample(g, e.A, e.B).Contains('#'))
            .Select(e => $"{e.a}-{e.b} {e.name}").ToList();
        Assert.True(bad.Count == 0, "front edges through walls: " + string.Join("; ", bad));
    }
}
