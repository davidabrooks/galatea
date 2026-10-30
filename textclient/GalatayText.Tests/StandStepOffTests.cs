using LibreMetaverse;
using Xunit;
using static GalatayText.Program;

namespace GalatayText.Tests;

/// <summary>2026-10-30 Peronaut: `stand` from the Xplicit Makeup Table chair left her standing ON the chair seat (z ~29.5, floor ~28.95).</summary>
public class StandStepOffTests
{
    static string RouteFile(string f)
    {
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "routes", f));
        return File.Exists(repo) ? repo : "/workspace/galatea-sl-repo/textclient/routes/" + f;
    }

    [Theory]
    [InlineData(222.6f, 72.7f, 29.5f)]   // inside the table / stool footprint
    [InlineData(221.9f, 72.6f, 29.5f)]   // just west of the footprint, but at chair-seat height
    public void Stand_tool_from_the_makeup_chair_steps_down_to_floor_beside_it(float x, float y, float z)
    {
        Assert.True(StandCommandStepsOff(true));
        Assert.False(StandCommandStepsOff(false));
        var boxes = ParseFurnitureBoxes(File.ReadAllText(RouteFile("_graph-Peronaut.json")));
        var seats = ParseHomeSeats(File.ReadAllText(RouteFile("_seats-peronaut-home.json")));
        var me = new Vector3(x, y, z);
        var seat = NearestHomeSeat(me, seats);
        Assert.NotNull(seat);
        var p = ClearStepPlan(me, seat, boxes, null);
        Assert.True(p.Needed, p.Why);
        Assert.NotNull(p.To);
        Assert.Null(InFurniture(p.To!.Value, boxes));
        Assert.True(p.To.Value.Z < 29.1f);   // on the floor, not seat height
    }

    [Fact]
    public void Stand_tool_always_repins_the_stand_hover_last_even_after_a_step_off()
    {
        Assert.Equal(new[] { "step off", "repin hover" }, StandCommandAfterSteps(true));
        Assert.Equal(new[] { "repin hover" }, StandCommandAfterSteps(false));
    }

    // 2026-10-30 10:38:34 PT live: post-unseat <222.4,72.5,29.5>, walk to the seat stand_spot 221.6,72.9 (0.8 m) "arrived" at <222.1,72.4,29.0>
    // (0.7 m short: the autopilot stop distance), called clear because it is 4 cm outside the table box. She only moved 0.3 m.
    [Fact]
    public void Live_10_38_makeup_chair_stand_targets_floor_0_6_m_clear_and_the_short_stop_is_not_clear()
    {
        var boxes = ParseFurnitureBoxes(File.ReadAllText(RouteFile("_graph-Peronaut.json")));
        var seats = ParseHomeSeats(File.ReadAllText(RouteFile("_seats-peronaut-home.json")));
        var me = new Vector3(222.4f, 72.5f, 29.5f);
        var seat = NearestHomeSeat(me, seats);
        var p = ClearStepPlan(me, seat, boxes, null, standMargin: StandOffMarginM);
        Assert.True(p.Needed, p.Why);
        Assert.NotNull(p.To);
        Assert.True(StandOffClear(p.To!.Value, boxes), $"target {p.To} not 0.6 m clear");
        Assert.True(p.To.Value.Z < 29.1f);
        Assert.NotEqual(221.64f, p.To.Value.X);                 // the 0.5 m-close stand_spot is not used
        var shortStop = new Vector3(222.1f, 72.4f, 29.0f);
        Assert.False(StandOffClear(shortStop, boxes));          // the old check passed this
        Assert.False(StandOffReached(me, p.To.Value, shortStop));
        Assert.True(ClearStepPlan(shortStop, seat, boxes, null, standMargin: StandOffMarginM).Needed);
        // aim through the spot so the ~1 m autopilot stop distance lands on it
        var aim = StandOffAim(me, p.To.Value, null);
        Assert.True(HDistT(me, aim) > HDistT(me, p.To.Value) + 1.0f);
        // the old outfit-change behaviour (no margin) is unchanged
        Assert.False(ClearStepPlan(shortStop, seat, boxes, null).Needed);
    }

    [Fact]
    public void Step_off_waits_for_a_settled_position_and_skips_when_already_clear()
    {
        Assert.False(StandPositionSettled(new[] { new Vector3(222.4f, 72.5f, 29.5f), new Vector3(222.4f, 72.5f, 29.2f), new Vector3(222.4f, 72.5f, 29.0f) }));
        Assert.True(StandPositionSettled(new[] { new Vector3(222.4f, 72.5f, 29.2f), new Vector3(221f, 72.5f, 29.0f), new Vector3(221f, 72.5f, 29.0f), new Vector3(221.01f, 72.5f, 29.0f) }));
        var boxes = ParseFurnitureBoxes(File.ReadAllText(RouteFile("_graph-Peronaut.json")));
        var seats = ParseHomeSeats(File.ReadAllText(RouteFile("_seats-peronaut-home.json")));
        var floor = new Vector3(221.2f, 72.9f, 29.0f);
        Assert.False(ClearStepPlan(floor, NearestHomeSeat(floor, seats), boxes, null, standMargin: StandOffMarginM).Needed);
        Assert.True(StandOffReached(new Vector3(222.4f, 72.5f, 29f), new Vector3(221.2f, 72.9f, 29f), new Vector3(221.0f, 72.95f, 29f)));   // past it
    }

    static float HDistT(Vector3 a, Vector3 b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
