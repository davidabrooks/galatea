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
}
