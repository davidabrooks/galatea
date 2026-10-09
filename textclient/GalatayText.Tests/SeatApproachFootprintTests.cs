using LibreMetaverse;
using GalatayText;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-09 08:27: walking to the MIRAGE bed she ended up standing on it: the final approach step (1.2 m short
/// of the seat) was inside the bed. Every home seat's last walk point must be on clear floor outside its footprint.</summary>
public class SeatApproachFootprintTests
{
    static string Routes()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            foreach (var p in new[] { Path.Combine(d.FullName, "textclient", "routes"), Path.Combine(d.FullName, "routes") })
                if (File.Exists(Path.Combine(p, "_seats-peronaut-home.json"))) return p;
        throw new DirectoryNotFoundException("routes");
    }

    [Fact]
    public void Every_listed_seat_approach_is_outside_its_footprint_at_floor_height()
    {
        var r = Routes();
        var g = Program.LoadGraphFile(Path.Combine(r, "_graph-Peronaut.json"));
        var boxes = Program.ParseFurnitureBoxes(File.ReadAllText(Path.Combine(r, "_graph-Peronaut.json")));
        var seats = Program.ParseHomeSeats(File.ReadAllText(Path.Combine(r, "_seats-peronaut-home.json"))).Where(s => s.Wander).ToList();
        int checkedSeats = 0;
        foreach (var s in seats)
        {
            var box = boxes.FirstOrDefault(b => b.Id == s.Id.ToString());
            var (q, _) = Program.NearestOnGraph(g, s.Pos);
            var end = Program.SeatFinalApproach(q, s.Pos, s.Approach, boxes) ?? q;
            foreach (var b in boxes)
                Assert.True(Program.InFurniture(end, new[] { b }) == null, $"'{s.Name}': last walk point {end} is inside '{b.Name}'");
            if (s.Approach is Vector3 ap) Assert.Null(Program.InFurniture(ap, boxes));
            if (s.StandSpot is Vector3 ss) Assert.Null(Program.InFurniture(ss, boxes));
            if (s.Level == "upper") Assert.InRange(end.Z, Program.HouseFloorZ + 0.3f, Program.HouseFloorZ + 1.6f);
            if (box != null) checkedSeats++;
        }
        Assert.True(checkedSeats >= 5, $"only {checkedSeats} seats have footprints");
    }

    [Fact]
    public void The_one_point_two_metre_step_is_pushed_out_of_a_footprint_or_dropped()
    {
        var bed = new Program.FurnitureBox("bed", "x", 214.46f, 72.71f, 217.92f, 76.16f);
        var seat = new Vector3(216.15f, 75.28f, 27.87f);
        var path = new Vector3(219.5f, 74.0f, 29.0f);                       // 3.6 m away: the old step landed at x 217.3 (on the bed)
        var p = Program.SeatFinalApproach(path, seat, null, new[] { bed });
        Assert.NotNull(p); Assert.Null(Program.InFurnitureMargin(p.Value, new[] { bed }, Program.SeatApproachMargin));
        Assert.Null(Program.SeatFinalApproach(new Vector3(218.4f, 75.0f, 29f), seat, null, new[] { bed }));   // nothing clear in between: no step
        var ap = new Vector3(218.32f, 75.2f, 29f);
        Assert.Equal(ap, Program.SeatFinalApproach(path, seat, ap, new[] { bed }));                           // listed approach wins
        Assert.Null(Program.SeatFinalApproach(ap, seat, ap, new[] { bed }));                                  // already there
    }
}
