using GalatayText;
using LibreMetaverse;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-08 David: wash your hands in the new bathroom sink after you use the toilet.</summary>
public class WashHandsTests
{
    static string RouteFile(string f)
    {
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "routes", f));
        return File.Exists(repo) ? repo : "/workspace/galatea-sl-repo/textclient/routes/" + f;
    }
    static Dictionary<UUID, Program.HomeSeatInfo> Seats() =>
        Program.ParseHomeSeats(File.ReadAllText(RouteFile("_seats-peronaut-home.json"))).ToDictionary(s => s.Id);

    [Fact]
    public void After_the_toilet_the_next_pick_is_the_sink_with_Wash_Hands()
    {
        var s = Seats();
        Assert.Equal("toilet", s[Program.ToiletSeatId].Special);
        var now = new DateTime(2026, 10, 8, 14, 0, 0);
        Assert.True(Program.WashHandsDue(now.AddMinutes(-4), now));
        var free = new HashSet<UUID> { Program.BathroomSinkId, new UUID("53f8929b-0abc-6e12-fac9-f9d6f9bfe6bc") };
        var (sink, menu, skip) = Program.WashHandsPick(s, free);
        Assert.Null(skip);
        Assert.Equal(Program.BathroomSinkId, sink);
        Assert.Equal(new[] { "Solo*", "Wash Hands" }, menu);
        Assert.Equal("upper", s[sink].Level);   // level dwell counts it as the upper level
    }

    [Fact]
    public void Busy_or_missing_sink_falls_back_to_the_normal_wander()
    {
        var s = Seats();
        var (sink, menu, skip) = Program.WashHandsPick(s, new HashSet<UUID> { Program.ToiletSeatId });
        Assert.Equal(UUID.Zero, sink); Assert.Null(menu); Assert.Contains("busy", skip);
        Assert.Equal(UUID.Zero, Program.WashHandsPick(new Dictionary<UUID, Program.HomeSeatInfo>(), new HashSet<UUID> { Program.BathroomSinkId }).sink);
        // no hand-wash button (only Shave) -> skip, never Shave
        var shaveOnly = s[Program.BathroomSinkId] with { MenuChoice = new() { "Shave", "Shave Hands" } };
        Assert.Null(Program.WashHandsMenu(shaveOnly));
        Assert.Equal(UUID.Zero, Program.WashHandsPick(new Dictionary<UUID, Program.HomeSeatInfo> { [Program.BathroomSinkId] = shaveOnly }, new HashSet<UUID> { Program.BathroomSinkId }).sink);
    }

    [Fact]
    public void Wash_is_due_only_after_a_recent_toilet_sit_and_is_short()
    {
        var now = new DateTime(2026, 10, 8, 14, 0, 0);
        Assert.False(Program.WashHandsDue(null, now));
        Assert.False(Program.WashHandsDue(now.AddMinutes(-31), now));
        var stays = Enumerable.Range(0, 200).Select(k => Program.WashStaySeconds(new Random(k))).ToList();
        Assert.All(stays, x => Assert.InRange(x, 20, 40));
        Assert.True(stays.Max() < 120);   // shorter than a normal home sit (120-240 s)
    }
}
