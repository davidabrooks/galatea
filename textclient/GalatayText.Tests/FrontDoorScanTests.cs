using GalatayText;
using Xunit;
using Vector2 = LibreMetaverse.Vector2;
using Vector3 = LibreMetaverse.Vector3;

namespace GalatayText.Tests;

/// <summary>2026-10-16 09:41 David: "you're still running into doors before you open them". goto_place beach from the
/// bedroom logged the front doors in its doorway zones (s=18.2) but never pre-touched them: the look-ahead scan needed the
/// look point (9 m ahead) inside the nav grid, and the home grid ends at y 84, so from s=14 on (just as the touch came
/// due at 5.1 m) the scan was skipped and only the stuck recovery opened the door. Uses the real graph, route and grid.</summary>
public class FrontDoorScanTests
{
    static string Routes(string f) => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "routes", f));
    static Program.NavGrid Grid() => Program.LoadNavGrid(Routes("_nav-peronaut-home.json"), DateTime.Now);
    static Program.Poly HomeToBeach(string place = "beach")
    {
        var g = Program.LoadGraphFile(Routes("_graph-Peronaut.json"));
        var (pts, err) = Program.GraphRoute(g, new Vector3(220.6f, 74.3f, 29.0f), g.Places[place].node);
        Assert.Null(err);
        return new Program.Poly(pts);
    }
    static Func<float, Vector2> At(Program.Poly p) => u => { var v = p.At(u); return new Vector2(v.X, v.Y); };

    // the FollowPoly look-ahead scan, one tick at a time at a constant walking speed
    static List<(string door, float dAhead, float s)> Simulate(Program.Poly poly, Program.NavGrid grid, float speed, bool oldGridPick, float phase = 0)
    {
        var touches = new List<(string, float, float)>(); var opened = new HashSet<LibreMetaverse.UUID>();
        for (float s = phase; s < poly.Len; s += speed * 0.4f)
        {
            var look = Math.Min(poly.Len, s + Program.DoorScanM);
            var ng = oldGridPick
                ? (Program.NavGridFor("Peronaut", poly.At(s), poly.At(look)) ?? Program.NavGridFor("Peronaut", poly.At(look), poly.At(look)))
                : Program.PickScanGrid(new[] { grid }, "Peronaut", poly.At(s), poly.At(look));
            if (ng == null) continue;
            List<Program.NavDoor> ahead = null;
            var d = Program.FirstCrossingAhead(At(poly), s, poly.Len, (p, q) =>
            {
                if (!ng.Contains(p.X, p.Y) && !ng.Contains(q.X, q.Y)) return false;
                var l = Program.DoorsForCrossing(ng, p, q).Where(nd => !opened.Contains(nd.Id)).ToList();
                if (l.Count == 0) return false; ahead = l; return true;
            });
            if (d is float da && Program.DoorTouchDue(da, Program.DoorLeadSpeedMps(speed)))
            {
                foreach (var nd in ahead) { touches.Add((nd.Name, da, s)); opened.Add(nd.Id); }
            }
        }
        return touches;
    }

    [Fact]
    public void Old_grid_pick_lost_the_window_before_the_front_doors()
    {
        // at 2.8 m/s the touch comes due 4.5 m before the doors (s>=13.7), but from s=14 the look point is off the grid
        var poly = HomeToBeach(); var grid = Grid();
        var old = Simulate(poly, grid, 2.8f, oldGridPick: true);
        Assert.DoesNotContain(old, t => t.door.StartsWith("front-double"));   // the reported bug: never pre-touched
    }

    [Fact]
    public void Both_front_double_leaves_are_touched_at_the_speed_trigger_at_any_speed_and_tick_phase()
    {
        var poly = HomeToBeach(); var grid = Grid();
        for (float speed = 1.0f; speed <= 3.6f; speed += 0.2f)
            for (float phase = 0f; phase < speed * 0.4f; phase += 0.1f)
            {
                var t = Simulate(poly, grid, speed, oldGridPick: false, phase);
                foreach (var leaf in new[] { "front-double-east", "front-double-west" })
                {
                    var hit = t.Where(x => x.door == leaf).ToList();
                    var tag = $"{leaf} at {speed:F1} m/s phase {phase:F1}";
                    Assert.True(hit.Count == 1, tag + ": touched " + hit.Count + " times");
                    // touched once the door is within the trigger (never later than it), and at most one tick (plus one scan step) after it came due
                    var trig = Program.DoorTriggerFor(Program.DoorLeadSpeedMps(speed));
                    Assert.True(hit[0].dAhead <= trig && hit[0].dAhead >= trig - speed * 0.4f - Program.DoorScanStepM - 0.01f, tag + ": da " + hit[0].dAhead);
                }
            }
    }

    [Fact]
    public void Scan_grid_is_found_from_the_walker_even_when_the_look_point_is_off_the_grid()
    {
        var grid = Grid();
        var here = new Vector3(228.05f, 78.4f, 29f); var off = new Vector3(228.4f, 88.3f, 26.5f);   // off: y > 84, below the floor
        Assert.Null(Program.NavGridFor("Peronaut", here, off));
        Assert.Same(grid, Program.PickScanGrid(new[] { grid }, "Peronaut", here, off));
        Assert.Same(grid, Program.PickScanGrid(new[] { grid }, "Peronaut", off, here));   // leaving the porch toward the door
        Assert.Null(Program.PickScanGrid(new[] { grid }, "Peronaut", off, off));
        Assert.Null(Program.PickScanGrid(new[] { grid }, "Elsewhere", here, here));
    }

    [Fact]
    public void Door_guard_sees_the_front_doors_only_when_they_are_about_to_be_entered()
    {
        var poly = HomeToBeach(); var grid = Grid();
        Assert.Equal(3.2f * 0.5f + 0.5f, Program.DoorGuardFor(3.2f), 3);
        Assert.Equal(Program.DoorGuardM, Program.DoorGuardFor(0f));
        // the doorway zone starts at s=18.2; the 'D' cells begin a little before that
        Assert.Null(Program.DoorGuardAhead(grid, At(poly), 12f, poly.Len, Program.DoorGuardFor(3.2f)));
        var near = Program.DoorGuardAhead(grid, At(poly), 17f, poly.Len, Program.DoorGuardFor(3.2f));
        Assert.NotNull(near);
        Assert.InRange(near!.Value.dist, 0f, Program.DoorGuardFor(3.2f));
        Assert.Contains(near.Value.doors, d => d.Name == "front-double-east");
        Assert.Contains(near.Value.doors, d => d.Name == "front-double-west");   // the pair together
        // already inside the opening (door still shut, e.g. it swung back): still guarded
        Assert.NotNull(Program.DoorGuardAhead(grid, At(poly), 18.2f, poly.Len, Program.DoorGuardM));
        // past it: nothing
        Assert.Null(Program.DoorGuardAhead(grid, At(poly), 22f, poly.Len, Program.DoorGuardFor(3.2f)));
        Assert.Null(Program.DoorGuardAhead(null, At(poly), 17f, poly.Len, 2f));
    }
}
