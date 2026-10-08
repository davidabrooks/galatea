using System.Text.Json.Nodes;
using GalatayText;
using LibreMetaverse;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-08 08:27 replay: stood up from the second pier bench at 215.5,31.0 (0.8 m off the axis x 216.3);
/// every leg and the recovery were STUCK at s=0 because the first aim was inside the autopilot stop distance.</summary>
public class PierBenchStandTests
{
    static string GraphPath()
    {
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "routes", "_graph-Peronaut.json"));
        return File.Exists(repo) ? repo : "/workspace/galatea-sl-repo/textclient/routes/_graph-Peronaut.json";
    }
    static readonly Vector3 StoodUp = new(215.5f, 31.0f, 24.5f);

    // straight-line distance from the start to the steering aim at s=0 (the narrow look-ahead along the route)
    static float FirstAimDist(Program.Graph g, List<Program.NarrowCorridor> cs, Vector3 from, string place)
    {
        var (P, err) = Program.GraphRoute(g, from, g.Places[place].node);
        Assert.Null(err);
        var C = new List<float> { 0f };
        for (int i = 1; i < P.Count; i++) C.Add(C[^1] + new Vector2(P[i].X - P[i - 1].X, P[i].Y - P[i - 1].Y).Length());
        float look = Program.NarrowLook(P, C, 0f, 4f, cs);
        int k = Program.SegAt(C, look);
        float f = C[k + 1] > C[k] ? (look - C[k]) / (C[k + 1] - C[k]) : 0f;
        var aim = P[k] + (P[k + 1] - P[k]) * Math.Clamp(f, 0f, 1f);
        return new Vector2(aim.X - from.X, aim.Y - from.Y).Length();
    }

    [Fact]
    public void Root_cause_the_first_aim_from_the_stand_up_spot_is_inside_the_autopilot_stop_distance()
    {
        var g = Program.LoadGraphFile(GraphPath()); var cs = Program.ParseNarrow(JsonNode.Parse(File.ReadAllText(GraphPath())));
        Assert.True(FirstAimDist(g, cs, StoodUp, "living") < 1.0f);
    }

    [Fact]
    public void She_steps_to_the_centre_line_toward_the_house_then_the_route_aims_far_enough()
    {
        var g = Program.LoadGraphFile(GraphPath()); var cs = Program.ParseNarrow(JsonNode.Parse(File.ReadAllText(GraphPath())));
        var step = Program.NarrowCentreStep(StoodUp, cs, g.N[g.Places["living"].node]);
        Assert.NotNull(step);
        Assert.InRange(step!.Value.X, 216.2f, 216.4f);                 // pier centre line
        Assert.InRange(step.Value.Y, 32.5f, 33.5f);                    // ~2 m north, toward the beach / house
        Assert.True(new Vector2(step.Value.X - StoodUp.X, step.Value.Y - StoodUp.Y).Length() > 1.5f); // a real walk, past the stop distance
        Assert.True(new Vector2(step.Value.X - 214.8f, step.Value.Y - 30.5f).Length() > 2f);          // away from the bench
        foreach (var place in new[] { "living", "east-deck", "patio-east" })
            Assert.True(FirstAimDist(g, cs, step.Value, place) >= 1.5f, place);
    }

    [Fact]
    public void Direction_follows_the_goal_and_no_step_when_already_centred_or_off_the_pier()
    {
        var cs = Program.ParseNarrow(JsonNode.Parse(File.ReadAllText(GraphPath())));
        var south = Program.NarrowCentreStep(StoodUp, cs, new Vector3(216.3f, 24.4f, 24.7f));   // toward the third bench
        Assert.NotNull(south); Assert.InRange(south!.Value.Y, 28.5f, 29.5f);
        Assert.Null(Program.NarrowCentreStep(new Vector3(216.5f, 35f, 24.6f), cs, new Vector3(228f, 68f, 29f)));  // 0.2 m off: fine
        Assert.Null(Program.NarrowCentreStep(new Vector3(222.6f, 53.7f, 20.5f), cs, new Vector3(228f, 68f, 29f))); // beach
        var end = Program.NarrowCentreStep(new Vector3(215.0f, 17.5f, 24.7f), cs, new Vector3(216.3f, 0f, 24f)); // clamps at the pier end
        Assert.NotNull(end); Assert.True(end!.Value.Y >= 17.0f - 0.01f);
    }
}
