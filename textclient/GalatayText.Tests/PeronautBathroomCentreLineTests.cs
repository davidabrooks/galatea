using System.Text.Json;
using GalatayText;
using LibreMetaverse;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-08 08:57 STUCK at 238.9,75.0-75.6: the east-deck leg ran along y 75.35 under the bathroom's north wall and
/// walked into David's new BackBone sink. David: "You should stay in the center of this room". Bathroom (east wing, through the
/// side-room-2 doorway) wall faces x 231.94-243.45, y 71.46-76.16, so the centre line is y 73.8; routes through it keep to that
/// line, well clear of the sink (north wall) and the clawfoot tub (south wall).</summary>
public class PeronautBathroomCentreLineTests
{
    const float CentreY = 73.8f, CentreX = 237.7f, MinClear = 0.8f;
    static readonly (float x0, float y0, float x1, float y1) Bathroom = (231.94f, 71.46f, 243.45f, 76.16f);

    static string GraphFile()
    {
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "routes", "_graph-Peronaut.json"));
        return File.Exists(repo) ? repo : "/workspace/galatea-sl-repo/textclient/routes/_graph-Peronaut.json";
    }
    static (float x0, float y0, float x1, float y1) Footprint(string uuid)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(GraphFile()));
        var f = doc.RootElement.GetProperty("furniture_footprints").EnumerateArray().Single(e => e.GetProperty("uuid").GetString() == uuid);
        var mn = f.GetProperty("min"); var mx = f.GetProperty("max");
        return ((float)mn[0].GetDouble(), (float)mn[1].GetDouble(), (float)mx[0].GetDouble(), (float)mx[1].GetDouble());
    }
    static readonly string Sink = "406eeb80-c05f-2f82-0857-145b725da0f8", Tub = "53f8929b-0abc-6e12-fac9-f9d6f9bfe6bc";

    static float BoxDist(float x, float y, (float x0, float y0, float x1, float y1) b)
    {
        float dx = Math.Max(0, Math.Max(b.x0 - x, x - b.x1)), dy = Math.Max(0, Math.Max(b.y0 - y, y - b.y1));
        return MathF.Sqrt(dx * dx + dy * dy);
    }
    static bool InBathroom(Vector3 p) => p.X > Bathroom.x0 && p.X < Bathroom.x1 && p.Y > Bathroom.y0 && p.Y < Bathroom.y1 && p.Z > 28f;
    // route resampled every 0.1 m
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

    [Fact]
    public void Sink_and_tub_footprints_are_real_sized()
    {
        var s = Footprint(Sink); var t = Footprint(Tub);
        Assert.InRange(s.x1 - s.x0, 3.5f, 4.5f); Assert.InRange(s.y1 - s.y0, 0.8f, 1.6f);   // wall vanity, 4.2 m wide
        Assert.InRange(t.x1 - t.x0, 2.5f, 3.0f); Assert.InRange(t.y1 - t.y0, 1.1f, 1.6f);   // 2.7 x 1.2 tub along x
        Assert.True(s.y1 >= Bathroom.y1 - 0.1f && t.y0 <= Bathroom.y0);                     // against the north / south walls
        Assert.True(MathF.Abs(CentreY - (Bathroom.y0 + Bathroom.y1) / 2) < 0.05f);
    }

    [Theory]
    [InlineData("front")]
    [InlineData("living")]
    [InlineData("bed")]
    [InlineData("patio-east")]
    public void East_deck_route_keeps_to_the_bathroom_centre_line_clear_of_sink_and_tub(string from)
    {
        var g = Program.LoadGraphFile(GraphFile());
        var (r, err) = Program.GraphRoute(g, g.N[g.Places[from].node], g.Places["east-deck"].node);
        Assert.Null(err);
        var inRoom = Dense(r).Where(InBathroom).ToList();
        Assert.NotEmpty(inRoom);
        var sink = Footprint(Sink); var tub = Footprint(Tub);
        foreach (var p in inRoom)
        {
            Assert.True(BoxDist(p.X, p.Y, sink) >= MinClear, $"{from}: {p} only {BoxDist(p.X, p.Y, sink):F2} m from the sink");
            Assert.True(BoxDist(p.X, p.Y, tub) >= MinClear, $"{from}: {p} only {BoxDist(p.X, p.Y, tub):F2} m from the tub");
        }
        Assert.Contains(inRoom, p => MathF.Abs(p.X - CentreX) < 0.2f && MathF.Abs(p.Y - CentreY) < 0.2f);   // through the room centre
        Assert.All(inRoom.Where(p => p.X > 235.5f), p => Assert.InRange(p.Y, CentreY - 0.3f, CentreY + 0.3f));  // on the centre line past the doorway turn
        var end = g.N[g.Places["east-deck"].node];
        Assert.True(BoxDist(end.X, end.Y, sink) >= 1f && Bathroom.x1 - end.X >= 1.5f, "east-deck end point by the sink or the east wall");
    }

    [Fact]
    public void Tub_route_enters_on_the_centre_line_and_stays_clear_of_the_sink()
    {
        var g = Program.LoadGraphFile(GraphFile());
        var (r, err) = Program.GraphRoute(g, g.N[g.Places["front"].node], g.Places["tub"].node);
        Assert.Null(err);
        var sink = Footprint(Sink);
        Assert.All(Dense(r).Where(InBathroom), p => Assert.True(BoxDist(p.X, p.Y, sink) >= MinClear, $"{p} near the sink"));
        Assert.Contains(r, p => MathF.Abs(p.Y - CentreY) < 0.1f && p.X > 234.5f && p.X < 236f);   // bath-centre node
    }
}
