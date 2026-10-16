using GalatayText;
using Xunit;
using Xunit.Abstractions;
using Vector2 = LibreMetaverse.Vector2;
using Vector3 = LibreMetaverse.Vector3;

namespace GalatayText.Tests;

/// <summary>2026-10-16 10:20 David: "you ran into both doors before they opened on your walk in (beach -> bedroom)". The
/// inbound walk touched the front doors 3.5 m ahead and side-room-1 4.5 m ahead: she climbs the porch stairs (z 26.4 -> 29, off
/// the home grid that ends at y 84) and turns a U-turn at their foot, so her measured speed (1.7 m/s) shrank the speed-based
/// lead. Real beach -> bedroom graph route and nav grid, ticks sampled like FollowPoly.</summary>
public class InboundDoorTests
{
    readonly ITestOutputHelper o; public InboundDoorTests(ITestOutputHelper o) { this.o = o; }
    static string Routes(string f) => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "routes", f));
    static Program.NavGrid Grid() => Program.LoadNavGrid(Routes("_nav-peronaut-home.json"), DateTime.Now);
    static Program.Poly Inbound()
    {
        var g = Program.LoadGraphFile(Routes("_graph-Peronaut.json"));
        var (pts, err) = Program.GraphRoute(g, new Vector3(228.2f, 53.1f, 21.8f), g.Places["bedroom"].node);
        Assert.Null(err);
        return new Program.Poly(pts);
    }
    // speed profile: walking 3.2 m/s, but 1.7 m/s from the U-turn at s~60 through the stairs (s<=70), and 1.7 m/s at the s~80 corner
    static float Speed(float s, bool dips) => dips && ((s > 59f && s < 70f) || (s > 78f && s < 82f)) ? 1.7f : 3.2f;

    static List<(string door, float dAhead, float s)> Walk(Program.Poly poly, Program.NavGrid grid, bool dips, float phase, bool old)
    {
        var touches = new List<(string, float, float)>(); var opened = new HashSet<LibreMetaverse.UUID>();
        float s = phase;
        while (s < poly.Len)
        {
            float v = Speed(s, dips);
            // the pre-change trigger: measured speed >= 1.5 used as is
            float trig = old ? Math.Max(3f, (v < 1.5f ? 3.2f : v) * (Program.DoorSwingWaitMs + Program.DoorTouchLatencyMs + Program.DoorTickMs) / 1000f) : Program.DoorLeadTriggerM(v);
            if (Program.DoorsDueAhead(new[] { grid }, poly.At, s, poly.Len, opened, trig) is var (da, ds))
                foreach (var nd in ds) { touches.Add((nd.Name, da, s)); opened.Add(nd.Id); }
            s += v * 0.4f;
        }
        return touches;
    }

    [Fact]
    public void Old_lead_touched_the_inbound_doors_late_when_she_slows_on_the_stairs()
    {
        var poly = Inbound(); var grid = Grid();
        var t = Walk(poly, grid, dips: true, 0, old: true);
        foreach (var x in t) o.WriteLine($"OLD {x.door} {x.dAhead:F1} m at s={x.s:F1}");
        Assert.Contains(t, x => x.door.StartsWith("front-double") && x.dAhead < 5.0f);   // the reported 3.5 m (old trigger = DoorTriggerFor with the 900 ms swing)
    }

    [Fact]
    public void Every_inbound_door_is_first_touched_at_least_9_m_ahead_at_any_tick_phase_and_speed_dip()
    {
        var poly = Inbound(); var grid = Grid();
        foreach (var dips in new[] { false, true })
            for (float phase = 0; phase < 1.3f; phase += 0.1f)
            {
                var t = Walk(poly, grid, dips, phase, old: false);
                var names = t.Select(x => x.door).ToList();
                foreach (var leaf in new[] { "front-double-east", "front-double-west", "side-room-1" })
                    Assert.True(names.Count(n => n == leaf) == 1, $"{leaf}: touched {names.Count(n => n == leaf)}x (dips {dips}, phase {phase:F1})");
                foreach (var x in t) Assert.True(x.dAhead >= 9.0f, $"{x.door} first touched {x.dAhead:F1} m ahead at s={x.s:F1} (dips {dips}, phase {phase:F1})");
            }
    }

    // 2026-10-16 10:31: the route folds back on itself near the bedroom (a hairpin out to 230.0,72.4 and back); she cuts the
    // corner, so her projected path position jumps ahead ~5 m in one tick and a door lead that was still > 7.5 m was skipped
    // (side-room-1 first touched 4.5 m ahead). The lead must cover the jump: the door is touched BEFORE the jump starts.
    [Fact]
    public void A_corner_cutting_jump_of_6_m_at_the_hairpin_never_makes_a_door_first_touched_late()
    {
        var poly = Inbound(); var grid = Grid();
        for (float jumpAt = 76.5f; jumpAt <= 80f; jumpAt += 0.5f)   // the cut starts at the hairpin apex (s~77-80)
            for (float phase = 0; phase < 1.3f; phase += 0.1f)
            {
                var opened = new HashSet<LibreMetaverse.UUID>(); var touches = new List<(string, float)>();
                float s = phase; bool jumped = false;
                while (s < poly.Len)
                {
                    if (Program.DoorsDueAhead(new[] { grid }, poly.At, s, poly.Len, opened, Program.DoorLeadTriggerM(3.2f)) is var (da, ds))
                        foreach (var nd in ds) { touches.Add((nd.Name, da)); opened.Add(nd.Id); }
                    s += 3.2f * 0.4f;
                    if (!jumped && s >= jumpAt) { s += 6f; jumped = true; }
                }
                foreach (var x in touches) Assert.True(x.Item2 >= 9.0f, $"{x.Item1} first touched {x.Item2:F1} m ahead (jump at {jumpAt}, phase {phase:F1})");
            }
    }

    [Fact]
    public void Both_front_leaves_are_detected_from_the_porch_stairs_off_the_grid_and_below_its_floor()
    {
        var poly = Inbound(); var grid = Grid();
        // walker at s=66 (z 26.8, y 86: off the grid, 1.2 m under its floor): the doors ahead are still found
        var due = Program.DoorsDueAhead(new[] { grid }, poly.At, 66.0f, poly.Len, new HashSet<LibreMetaverse.UUID>(), Program.DoorLeadTriggerM(1.7f));
        Assert.NotNull(due);
        Assert.Contains(due!.Value.doors, d => d.Name == "front-double-east");
        Assert.Contains(due.Value.doors, d => d.Name == "front-double-west");
        Assert.InRange(due.Value.dist, 4f, Program.DoorLeadTriggerM(1.7f));
        Assert.True(Program.DoorLeadTriggerM(1.7f) >= 10f);
    }

    [Fact]
    public void Real_inbound_doors_are_14_m_apart_so_side_room_1_gets_its_own_early_touch()
    {
        var poly = Inbound(); var grid = Grid();
        var first = Program.DoorsDueAhead(new[] { grid }, poly.At, 64.0f, poly.Len, new HashSet<LibreMetaverse.UUID>(), 9.0f);
        Assert.NotNull(first);
        Assert.DoesNotContain(first!.Value.doors, d => d.Name == "side-room-1");
        var done = new HashSet<LibreMetaverse.UUID>(grid.Doors.Where(d => d.Name.StartsWith("front-double")).Select(d => d.Id));
        var after = Program.DoorsDueAhead(new[] { grid }, poly.At, 76.0f, poly.Len, done, Program.DoorLeadTriggerM(3.2f));
        Assert.NotNull(after);
        Assert.Contains(after!.Value.doors, d => d.Name == "side-room-1");
        Assert.True(after.Value.dist >= 9.0f, "side-room-1 first touched " + after.Value.dist);
    }

    // front doors (y 79.6) then side-room-1 (x 224.2, y 76) on one short path: 7-8 m apart
    static Program.Poly TwoDoors() => new(new List<Vector3> { new(228f, 92f, 29f), new(228f, 78f, 29f), new(224.2f, 78f, 29f), new(224.2f, 70f, 29f) });

    [Fact]
    public void A_second_door_within_8_m_of_the_first_is_touched_with_it_and_nothing_twice()
    {
        var grid = Grid(); var poly = TwoDoors();
        var done = new HashSet<LibreMetaverse.UUID>();
        // the front doors come due 7.5 m ahead; side-room-1 lies ~8 m further on, inside the scan window: one batch
        var b = Program.DoorsDueAhead(new[] { grid }, poly.At, 10.0f, poly.Len, done, Program.DoorLeadTriggerM(3.2f));
        Assert.NotNull(b);
        o.WriteLine($"batch at {b!.Value.dist:F1}: " + string.Join(", ", b.Value.doors.Select(d => d.Name)));
        Assert.Contains(b.Value.doors, d => d.Name == "front-double-east");
        Assert.Contains(b.Value.doors, d => d.Name == "side-room-1");
        foreach (var d in b.Value.doors) done.Add(d.Id);
        Assert.Null(Program.DoorsDueAhead(new[] { grid }, poly.At, 10.0f, poly.Len, done, 12f));
        Assert.Null(Program.DoorsDueAhead(new[] { grid }, poly.At, 11.0f, poly.Len, done, 12f));
    }

    [Fact]
    public void Doors_beyond_the_together_window_are_left_for_their_own_trigger()
    {
        var grid = Grid(); var poly = TwoDoors();
        var b = Program.DoorsDueAhead(new[] { grid }, poly.At, 10.0f, poly.Len, new HashSet<LibreMetaverse.UUID>(), 7.5f);
        Assert.NotNull(b);
        // nothing from a different grid / no grids
        Assert.Null(Program.DoorsDueAhead(new Program.NavGrid[0], poly.At, 10.0f, poly.Len, new HashSet<LibreMetaverse.UUID>(), 12f));
    }
}
