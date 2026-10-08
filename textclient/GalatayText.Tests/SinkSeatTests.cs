using GalatayText;
using LibreMetaverse;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-08 David: bathroom sink = singles poses only (Solo*, never Shave); the clawfoot tub moved to 240.6,72.0.</summary>
public class SinkSeatTests
{
    static readonly List<string> SinkTop = new() { "Sex Behind*", "[ADJUST]", "[SWAP]", "Blowjobs*", "Mutual FP*", "Sex Front*", "Playing*", "Going Down*", "Handjobs*", "Texture", "Solo*", "Cuddles*" };
    static string RouteFile(string f)
    {
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "routes", f));
        return File.Exists(repo) ? repo : "/workspace/galatea-sl-repo/textclient/routes/" + f;
    }
    static Dictionary<UUID, Program.HomeSeatInfo> Seats() =>
        Program.ParseHomeSeats(File.ReadAllText(RouteFile("_seats-peronaut-home.json"))).ToDictionary(s => s.Id);

    [Fact]
    public void Solo_picker_only_ever_enters_Solo_on_the_sink_menu()
    {
        for (int k = 0; k < 50; k++)
        {
            Assert.Equal("Solo*", Program.PickPoseButton(SinkTop, null, new Random(k), preferPgSolo: true).pick);
            Assert.Equal("Solo*", Program.PickPoseButton(SinkTop, null, new Random(k)).pick);
            // without the obviously couples-named entries the adult ones still never win
            var noSex = SinkTop.Where(l => !l.StartsWith("Sex") && !l.StartsWith("Cuddles")).ToList();
            Assert.Equal("Solo*", Program.PickPoseButton(noSex, null, new Random(k), preferPgSolo: true).pick);
            Assert.Equal("Solo*", Program.PickPoseButton(noSex, null, new Random(k)).pick);
        }
        Assert.True(Program.PoseMenuHasSoloSubmenu(SinkTop));
    }

    [Fact]
    public void Sink_seat_entry_Solo_path_random_wash_pose_never_Shave_and_adult_name_allowed()
    {
        var sink = Seats()[new UUID("406eeb80-c05f-2f82-0857-145b725da0f8")];
        Assert.True(sink.Wander);
        Assert.Null(sink.Special);                                      // stays dressed
        Assert.True(Program.HomeSeatSoloMenuPath(sink));
        Assert.False(Program.SoloSeatNameOk(sink.Name, false));          // the name alone would be skipped
        var picks = Enumerable.Range(0, 120).Select(k => Program.HomeSeatMenu(sink, new Random(k))).ToList();
        Assert.All(picks, m => { Assert.Equal(2, m.Count); Assert.Equal("Solo*", m[0]); });
        Assert.Equal(new HashSet<string> { "Wash Face", "Wash Hands", "Soap Face", "Eyebrows", "Wait", "Break" }, picks.Select(m => m[1]).ToHashSet());
        Assert.False(Program.HomeSeatSoloMenuPath(sink with { MenuFixed = new() { "Sex Front*" } }));
        Assert.False(Program.HomeSeatSoloMenuPath(sink with { MenuFixed = new() }));
    }

    [Fact]
    public void Moved_tub_change_spot_is_on_the_floor_beside_it_and_clear_of_the_sink()
    {
        var s = Seats();
        var tub = s[new UUID("53f8929b-0abc-6e12-fac9-f9d6f9bfe6bc")]; var sink = s[new UUID("406eeb80-c05f-2f82-0857-145b725da0f8")];
        Assert.Equal(240.6f, tub.Pos.X, 2); Assert.Equal(72.0f, tub.Pos.Y, 2);
        var cs = tub.ChangeSpot!.Value;
        // tub footprint 2.7 x 1.2 m along x (rotZ 180): x 238.55..241.25, y 71.4..72.6; stand >= 0.6 m off it, within 2.5 m of its centre
        bool inside = cs.X > 238.55f - 0.6f && cs.X < 241.25f + 0.6f && cs.Y > 71.4f - 0.6f && cs.Y < 72.6f + 0.6f;
        Assert.False(inside);
        Assert.InRange(new Vector2(cs.X - tub.Pos.X, cs.Y - tub.Pos.Y).Length(), 1f, 2.5f);
        Assert.True(new Vector2(cs.X - sink.Pos.X, cs.Y - sink.Pos.Y).Length() > 2f);   // not where she would stand at the sink
        var g = Program.LoadGraphFile(RouteFile("_graph-Peronaut.json"));
        Assert.True(new Vector2(cs.X - g.N[g.Places["tub"].node].X, cs.Y - g.N[g.Places["tub"].node].Y).Length() < 0.2f);
        var (r, err) = Program.GraphRoute(g, g.N[g.Places["front"].node], g.Places["tub"].node);
        Assert.Null(err);
        Assert.Contains(r, p => new Vector2(p.X - g.N[g.Places["east-door-room"].node].X, p.Y - g.N[g.Places["east-door-room"].node].Y).Length() < 0.3f);
    }
}
