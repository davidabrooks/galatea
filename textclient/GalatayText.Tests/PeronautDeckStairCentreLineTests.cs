using System.Text.Json;
using GalatayText;
using LibreMetaverse;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-09 09:00 David: on the west 3-step GOOSE deck stair (5c560cf2, up to the Mooring deck) she walked too close
/// to the water side. Stair from scene export-20261009-090203: root 214.20,57.64, 1.76 m run along x, 2.5 m wide, rotZ 180 ->
/// x 213.32-215.08, y 56.39-58.89, centre line y 57.64. Routes up it keep to that line, with a straight run-up before the foot,
/// inside a 'narrow' corridor (no corner cutting), and end at the hot-tub stop clear of the tub.</summary>
public class PeronautDeckStairCentreLineTests
{
    const float CentreY = 57.64f, StairX0 = 213.32f, StairX1 = 215.08f, Tol = 0.15f;

    static string GraphFile()
    {
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "routes", "_graph-Peronaut.json"));
        return File.Exists(repo) ? repo : "/workspace/galatea-sl-repo/textclient/routes/_graph-Peronaut.json";
    }
    static List<Vector3> Dense(List<Vector3> pts)
    {
        var o = new List<Vector3>();
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            int n = Math.Max(1, (int)(Vector3.Distance(pts[i], pts[i + 1]) / 0.1f));
            for (int k = 0; k < n; k++) o.Add(Vector3.Lerp(pts[i], pts[i + 1], k / (float)n));
        }
        o.Add(pts[^1]); return o;
    }

    [Theory]
    [InlineData("rowboat")]
    [InlineData("pier")]
    [InlineData("beach")]
    [InlineData("home")]
    public void Mooring_deck_route_climbs_the_stair_on_its_centre_line_after_a_straight_run_up(string from)
    {
        var g = Program.LoadGraphFile(GraphFile());
        var (r, err) = Program.GraphRoute(g, g.N[g.Places[from].node], g.Places["mooring-deck"].node);
        Assert.Null(err);
        // on the stair and 1.2 m of run-up east of its foot: on the centre line
        var onLine = Dense(r).Where(p => p.X >= StairX0 - 0.3f && p.X <= StairX1 + 1.2f && MathF.Abs(p.Y - CentreY) < 2f).ToList();
        Assert.NotEmpty(onLine);
        Assert.All(onLine, p => Assert.True(MathF.Abs(p.Y - CentreY) <= Tol, $"{from}: {p} is {p.Y - CentreY:F2} m off the stair centre line"));
        var end = r[^1];
        Assert.True(MathF.Abs(end.Y - CentreY) <= Tol && end.X < StairX0 && end.X > StairX0 - 0.8f, $"hot-tub stop {end} not at the stair top");
    }

    [Fact]
    public void Stair_is_a_narrow_corridor_and_the_stop_is_clear_of_the_hot_tub()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(GraphFile()));
        var root = doc.RootElement;
        var c = root.GetProperty("narrow").EnumerateArray().Single(n => n.GetProperty("name").GetString()!.Contains("deck stair 3"));
        var a = c.GetProperty("a"); var b = c.GetProperty("b");
        Assert.Equal(CentreY, (float)a[1].GetDouble(), 2); Assert.Equal(CentreY, (float)b[1].GetDouble(), 2);
        Assert.True(Math.Max(a[0].GetDouble(), b[0].GetDouble()) >= StairX1 + 1.2 && Math.Min(a[0].GetDouble(), b[0].GetDouble()) <= StairX0);
        Assert.InRange(c.GetProperty("half_width").GetDouble(), 1.0, 1.25);   // stair is 2.5 m wide
        var tub = root.GetProperty("furniture_footprints").EnumerateArray().Single(f => f.GetProperty("uuid").GetString()!.StartsWith("a631d7bb"));
        double m = tub.GetProperty("margin").GetDouble();
        var g = Program.LoadGraphFile(GraphFile());
        var end = g.N[g.Places["mooring-deck"].node];
        bool inBox = end.X >= tub.GetProperty("min")[0].GetDouble() - m && end.X <= tub.GetProperty("max")[0].GetDouble() + m
                  && end.Y >= tub.GetProperty("min")[1].GetDouble() - m && end.Y <= tub.GetProperty("max")[1].GetDouble() + m;
        Assert.False(inBox, $"mooring-deck stop {end} inside the hot tub footprint + margin");
    }
}
