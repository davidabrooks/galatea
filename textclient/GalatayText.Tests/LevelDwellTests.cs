using GalatayText;
using LibreMetaverse;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-08 12:22 David: stay ~30 min on the beach / upper level before switching; change in/out of the bikini
/// in the bedroom. Also the house outline now covers the east-wing bathroom.</summary>
public class LevelDwellTests
{
    static Program.Graph Graph()
    {
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "routes", "_graph-Peronaut.json"));
        return Program.LoadGraphFile(File.Exists(repo) ? repo : "/workspace/galatea-sl-repo/textclient/routes/_graph-Peronaut.json");
    }
    static readonly DateTime T0 = new(2026, 10, 8, 12, 0, 0);

    [Fact]
    public void Dwell_switches_after_the_configured_time_and_not_before()
    {
        const double dwell = 30;
        Assert.Equal("upper", Program.LevelToPick("upper", T0, T0.AddMinutes(0), dwell, true, true));
        Assert.Equal("upper", Program.LevelToPick("upper", T0, T0.AddMinutes(29.9), dwell, true, true));
        Assert.Equal("beach", Program.LevelToPick("upper", T0, T0.AddMinutes(30), dwell, true, true));
        Assert.Equal("beach", Program.LevelToPick("beach", T0, T0.AddMinutes(12), dwell, true, true));
        Assert.Equal("upper", Program.LevelToPick("beach", T0, T0.AddMinutes(31), dwell, true, true));
        // the setting is honoured (a 10 min dwell switches at 10)
        Assert.Equal("upper", Program.LevelToPick("upper", T0, T0.AddMinutes(9), 10, true, true));
        Assert.Equal("beach", Program.LevelToPick("upper", T0, T0.AddMinutes(10), 10, true, true));
    }

    [Fact]
    public void Arrival_restarts_the_clock_and_staying_keeps_it()
    {
        var (l1, s1) = Program.LevelArrive(null, DateTime.MinValue, "upper", T0);              // login
        Assert.Equal(("upper", T0), (l1, s1));
        var (l2, s2) = Program.LevelArrive(l1, s1, "upper", T0.AddMinutes(20));                // still upstairs
        Assert.Equal(T0, s2);
        var (l3, s3) = Program.LevelArrive(l2, s2, "beach", T0.AddMinutes(35));                // arrived on the beach
        Assert.Equal(("beach", T0.AddMinutes(35)), (l3, s3));
        Assert.Equal("beach", Program.LevelToPick(l3, s3, T0.AddMinutes(60), 30, true, true)); // 25 min down there: stay
        Assert.Equal("upper", Program.LevelToPick(l3, s3, T0.AddMinutes(65), 30, true, true));
    }

    [Fact]
    public void Restart_on_the_same_level_keeps_the_clock()
    {
        // 2026-11-04: the beach never came up because every wander restart began the count again
        var (l, since, why) = Program.LevelRestore("upper", T0, "upper", T0.AddMinutes(12));
        Assert.Equal(("upper", T0), (l, since));
        Assert.Contains("kept", why);
        Assert.Equal("beach", Program.LevelToPick(l, since, T0.AddMinutes(20), 20, true, true));   // 20 min of 20: switch
        // a saved clock made on the beach survives a relog on the beach
        var (lb, sb, _) = Program.LevelRestore("beach", T0, "beach", T0.AddMinutes(9));
        Assert.Equal(("beach", T0), (lb, sb));
        Assert.Equal("beach", Program.LevelToPick(lb, sb, T0.AddMinutes(8), 20, true, true));
    }

    [Fact]
    public void Restart_on_the_other_level_starts_fresh()
    {
        // saved as upper, but she is on the beach now (walked down, or a teleport): a new clock from now
        var (l, since, why) = Program.LevelRestore("upper", T0, "beach", T0.AddMinutes(15));
        Assert.Equal(("beach", T0.AddMinutes(15)), (l, since));
        Assert.Contains("now on the beach", why);
        Assert.Equal("beach", Program.LevelToPick(l, since, T0.AddMinutes(16), 20, true, true));
        // ... and the other way round
        var (u, us, _) = Program.LevelRestore("beach", T0, "upper", T0.AddMinutes(15));
        Assert.Equal(("upper", T0.AddMinutes(15)), (u, us));
    }

    [Fact]
    public void Missing_or_corrupt_state_starts_fresh()
    {
        foreach (var bad in new[] { null, "", "   ", "not json", "{}", "{\"level\":\"attic\",\"since\":\"nope\"}",
                                    "{\"level\":\"upper\"}", "{\"level\":null,\"since\":null}" })
        {
            Assert.False(Program.ParseLevelDwell(bad, out _, out _), $"'{bad}' is not usable");
            var (l, since, why) = Program.LevelRestore(null, DateTime.MinValue, "upper", T0);
            Assert.Equal(("upper", T0, "no saved level"), (l, since, why));
            // and the same fallback through the file parser
            var (l2, s2, _) = Program.LevelRestore(null, DateTime.MinValue, "beach", T0.AddMinutes(3));
            Assert.Equal(("beach", T0.AddMinutes(3)), (l2, s2));
        }
    }

    [Fact]
    public void A_stale_or_future_clock_starts_fresh()
    {
        // older than the 6 h ceiling: not trusted (a long outage, yesterday's file)
        var (l, since, why) = Program.LevelRestore("upper", T0, "upper", T0.AddHours(6) + TimeSpan.FromMinutes(1));
        Assert.Equal(("upper", T0.AddHours(6) + TimeSpan.FromMinutes(1)), (l, since));
        Assert.Contains("too old", why);
        // exactly at the ceiling is still kept
        Assert.Equal(T0, Program.LevelRestore("upper", T0, "upper", T0.AddHours(6)).since);
        // a clock in the future (a clock change, a bad file) is not trusted
        var (lf, sf, whyf) = Program.LevelRestore("upper", T0.AddMinutes(5), "upper", T0);
        Assert.Equal(("upper", T0), (lf, sf));
        Assert.Contains("future", whyf);
    }

    [Fact]
    public void Saved_state_round_trips_through_the_json_shape()
    {
        var json = Program.LevelDwellJson("beach", new DateTime(2026, 11, 4, 9, 49, 12));
        Assert.Contains("\"level\":\"beach\"", json);
        Assert.Contains("\"since\":\"2026-11-04T09:49:12", json);
        Assert.True(Program.ParseLevelDwell(json, out var level, out var since));
        Assert.Equal("beach", level);
        Assert.Equal(new DateTime(2026, 11, 4, 9, 49, 12), since);
        // a UTC stamp comes back as local time, so the comparison against DateTime.Now is honest
        Assert.True(Program.ParseLevelDwell("{\"level\":\"Upper\",\"since\":\"2026-11-04T17:49:12Z\"}", out var lu, out var su));
        Assert.Equal("upper", lu);
        Assert.Equal(new DateTime(2026, 11, 4, 9, 49, 12), su);
        Assert.Equal(DateTimeKind.Local, su.Kind);
        // the restored pair then drives the pick as usual
        Assert.Equal("upper", Program.LevelToPick(lu, su, new DateTime(2026, 11, 4, 10, 0, 0), 20, true, true));
        Assert.Equal("beach", Program.LevelToPick(lu, su, new DateTime(2026, 11, 4, 10, 9, 12), 20, true, true));
    }

    [Fact]
    public void Empty_level_falls_back_to_the_other()
    {
        Assert.Equal("upper", Program.LevelToPick("upper", T0, T0.AddMinutes(40), 30, false, true));  // nothing on the beach
        Assert.Equal("beach", Program.LevelToPick("upper", T0, T0.AddMinutes(5), 30, true, false));   // nothing upstairs
        Assert.Equal("beach", Program.LevelToPick("beach", T0, T0, 30, false, false));                 // nothing anywhere: unchanged
    }

    [Fact]
    public void Levels_split_beach_destinations_from_the_upper_ones()
    {
        var g = Graph();
        var beach = Program.LevelEnds(g, "beach");
        var upper = Program.LevelEnds(g, "upper");
        Assert.Contains("beach", beach); Assert.Contains("mooring-deck", beach); Assert.Contains("pier-end", beach); Assert.Contains("rowboat", beach);
        Assert.Contains("living", upper); Assert.Contains("porch", upper); Assert.Contains("east-deck", upper); Assert.Contains("patio-sw", upper);
        Assert.Empty(beach.Intersect(upper));
        // seats: by their level tag / position, same rule as the bikini
        Assert.Equal("beach", Program.HomeLevelOf(new(210.5f, 59.3f, 21.2f), "lower-beach"));   // hot tub
        Assert.Equal("beach", Program.HomeLevelOf(new(214.8f, 30.5f, 23.9f), "lower-pier"));    // pier bench
        Assert.Equal("upper", Program.HomeLevelOf(new(217.1f, 82.8f, 25.5f), "lower-porch"));   // porch rocker
        Assert.Equal("upper", Program.HomeLevelOf(new(240.6f, 72.0f, 28.8f), "upper"));         // tub
        Assert.Equal("upper", Program.HomeLevelOf(new(228.9f, 69.0f, 29.0f)));                  // her login spot
    }

    [Fact]
    public void Dwell_setting_comes_from_wander_rules_with_a_30_minute_default()
    {
        Assert.Equal(30, Program.ParseWanderRule("{\"Peronaut\":{\"place\":\"home\",\"seatAvatarM\":3,\"greet\":true}}", "Peronaut").LevelDwellMin);
        Assert.Equal(45, Program.ParseWanderRule("{\"Peronaut\":{\"place\":\"home\",\"levelDwellMin\":45}}", "Peronaut").LevelDwellMin);
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "routes", "_wander-rules.json"));
        if (File.Exists(repo)) Assert.Equal(30, Program.ParseWanderRule(File.ReadAllText(repo), "Peronaut").LevelDwellMin);
    }

    [Fact]
    public void Bikini_changes_happen_at_the_bedroom_spot()
    {
        var g = Graph();
        Assert.Equal("bedroom", Program.BikiniChangePlace(g));
        var spot = g.N[g.Places["bedroom"].node];
        Assert.True(Program.InBedroom(spot), $"spot {spot} is in the bedroom");
        Assert.True(Program.IndoorsAtHome(spot));
        // clear of the MIRAGE bed (footprint x 214.2-217.3, y 72.7-75.8) by >= 1 m, and well out of the side-room-1 doorway
        Assert.True(spot.X >= 217.3f + 1.0f, "away from the bed");
        var door = new Vector2(224.25f, 76.03f);
        Assert.True(Vector2.Distance(new Vector2(spot.X, spot.Y), door) > Program.DoorNearM + 1.0f, "not in the doorway");
        Assert.False(Program.NearDoorCentre(new Vector2(spot.X, spot.Y), new[] { door, new Vector2(231.85f, 76.03f), new Vector2(223.92f, 68.08f) }));
        // reachable from the living room and the beach, through the side-room-1 doorway
        var (p1, e1) = Program.GraphRoute(g, g.N[g.Places["living"].node], g.Places["bedroom"].node);
        Assert.Null(e1); Assert.True(Program.InBedroom(p1[^1]));
        var (p2, e2) = Program.GraphRoute(g, g.N[g.Places["beach"].node], g.Places["bedroom"].node);
        Assert.Null(e2); Assert.DoesNotContain(p2, Program.UnderHouse);
        // plans: the way down changes into the bikini; the way back up changes back (in the bedroom)
        Assert.Equal("bedroom-restore", Program.ReturnChangePlan(false, true));
        Assert.Equal("none", Program.ReturnChangePlan(true, true));     // still beach-bound: keep the bikini
        Assert.Equal("none", Program.ReturnChangePlan(false, false));   // not in beach mode
        Assert.True(Program.AtBikiniSpot(new(220.9f, 73.6f, 29.0f), spot));
        Assert.False(Program.AtBikiniSpot(new(228.0f, 68.0f, 29.0f), spot));   // living room: walk first
    }

    [Fact]
    public void Zone_tick_leaves_the_restore_to_the_wander()
    {
        Assert.Equal("wait-wander", Program.ZoneActionWithWander("house", true, true, true, true));
        Assert.Equal("restore", Program.ZoneActionWithWander("house", true, true, true, false));   // wander off: anywhere indoors
        Assert.Equal("bikini-on", Program.ZoneActionWithWander("beach", false, false, false, true)); // beach safety net unchanged
        Assert.Equal("wait-indoors", Program.ZoneActionWithWander("house", true, true, false, true));
    }

    [Fact]
    public void Bathroom_counts_as_inside()
    {
        Assert.True(Program.IndoorsAtHome(new(233.6f, 73.0f, 29.0f)));   // toilet spot
        Assert.True(Program.IndoorsAtHome(new(239.9f, 73.6f, 29.0f)));   // tub spot
        Assert.True(Program.IndoorsAtHome(new(240.4f, 75.5f, 29.0f)));   // at the sink
        Assert.True(Program.IndoorsAtHome(new(235.2f, 73.8f, 29.0f)));   // bath centre line
        Assert.True(Program.IndoorsAtHome(new(243.3f, 72.0f, 29.0f)));   // east end
        Assert.False(Program.IndoorsAtHome(new(246.5f, 72.0f, 29.0f)));  // east stairs walk outside
        Assert.False(Program.IndoorsAtHome(new(234.4f, 57.2f, 29.0f)));  // patio SE
        Assert.False(Program.UnderHouse(new(246.52f, 71.98f, 25.48f)));  // east walk below stays outside the footprint
        Assert.All(Graph().N, n => Assert.False(Program.UnderHouse(n), $"graph node {n} is under the house"));
    }
}
