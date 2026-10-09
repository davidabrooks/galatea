using GalatayText;
using LibreMetaverse;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-09 David: boats at the Burgundy pier: pier spot + teleport, Singles poses only (allowlist), no submarine,
/// leave by teleporting while still seated.</summary>
public class BoatSeatTests
{
    static readonly List<string> NammosRoot = new() { "SYSTEM*", "[SWAP]", "[ADJUST]", "home skip", "SINGLE*", "COUPLE*" };
    static readonly List<string> NammosSingle = new() { "[SWAP]", "[PAGE-]", "[PAGE+]", "sitting 6", "sitting 7", "sitting 8", "sitting 3", "sitting 4", "sitting 5", "[BACK]", "sitting 1", "sitting 2" };
    static readonly List<string> BeachHouseSingle = new() { "[SWAP]", "[PAGE-]", "[PAGE+]", "chill 6", "chill 7", "stand 1", "chill 3", "chill 4", "chill 5", "[BACK]", "chill 1", "chill 2" };
    static string RouteFile(string f)
    {
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "routes", f));
        return File.Exists(repo) ? repo : "/workspace/galatea-sl-repo/textclient/routes/" + f;
    }

    [Fact]
    public void Root_step_only_ever_enters_the_Singles_set()
    {
        for (int k = 0; k < 40; k++) Assert.Equal("SINGLE*", Program.BoatPickRoot(NammosRoot, new Random(k)));
        Assert.Null(Program.BoatPickRoot(new List<string> { "SYSTEM*", "COUPLE*", "Drive", "Start", "[ADJUST]" }, new Random(1)));
        Assert.Null(Program.BoatPickRoot(new List<string> { "Single" /* not a submenu */, "Options*" }, new Random(1)));
        Assert.Equal("Solo*", Program.BoatPickRoot(new List<string> { "Solo*", "Captain*" }, new Random(2)));
    }

    [Fact]
    public void Pose_step_is_an_allowlist_never_controls()
    {
        for (int k = 0; k < 40; k++)
        {
            var p = Program.BoatPickPose(NammosSingle, "sitting 5", new Random(k));
            Assert.StartsWith("sitting ", p); Assert.NotEqual("sitting 5", p);
            var b = Program.BoatPickPose(BeachHouseSingle, null, new Random(k));
            Assert.StartsWith("chill ", b);   // never 'stand 1' on a boat
        }
        foreach (var bad in new[] { "Drive", "Start", "Engine", "Pilot", "Captain", "Anchor", "Lights", "Rez", "Options", "SYSTEM*", "[BACK]", "Start Engine", "Anchor Up", "COUPLE*", "Couple 1", "Lights On", "Take Helm" })
            Assert.False(Program.BoatIsPoseButton(bad), bad);
        Assert.Null(Program.BoatPickPose(new List<string> { "Drive", "Start", "[BACK]", "Anchor" }, null, new Random(3)));
    }

    [Fact]
    public void Menu_text_tells_when_she_is_inside_the_Singles_set()
    {
        Assert.True(Program.BoatMenuInSolo("Sitter 0>SINGLE"));
        Assert.True(Program.BoatMenuInSolo("AVsitter™ 1.29.u2\nSitter 0>SINGLE"));
        Assert.False(Program.BoatMenuInSolo("Sitter 0"));
        Assert.False(Program.BoatMenuInSolo("Sitter 0>SYSTEM"));
        Assert.False(Program.BoatMenuInSolo("Sitter 0>COUPLE"));
    }

    [Fact]
    public void Approach_walks_only_on_the_beach_level_and_leaving_never_stands_first()
    {
        Assert.Equal("walk", Program.BoatApproach(Program.LevelBeach, true));
        Assert.Equal("teleport", Program.BoatApproach(Program.LevelBeach, false));
        Assert.Equal("teleport", Program.BoatApproach(Program.LevelUpper, true));
        Assert.False(Program.BoatStandBeforeLeave);
    }

    [Fact]
    public void Seat_list_has_the_two_boats_the_pier_spot_and_never_the_submarine()
    {
        var json = File.ReadAllText(RouteFile("_seats-peronaut-home.json"));
        var seats = Program.ParseHomeSeats(json);
        var boats = seats.Where(Program.IsBoat).ToList();
        var wander = boats.Where(b => b.Wander).Select(b => b.Id.ToString()).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "5301c6d5-1ee7-a790-dd4e-ec1295138232", "aebcf450-75bd-598e-e467-1071c2572a47" }, wander);
        Assert.All(boats.Where(b => b.Wander), b => Assert.Equal(Program.LevelBeach, Program.HomeLevelOf(b.Pos, b.Level)));
        var sub = boats.Single(b => b.Id == new UUID("670dfaff-9a94-3bdb-74a4-f9383524549b"));
        Assert.False(sub.Wander);
        Assert.True(Program.BoatExcluded(sub.Name));
        Assert.False(Program.BoatExcluded("Nammos Beach"));
        // the submarine stays out even if someone flips its wander flag
        var flipped = Program.ParseHomeSeats("{\"boats\":[{\"uuid\":\"670dfaff-9a94-3bdb-74a4-f9383524549b\",\"name\":\"STEELHEAD 1003 MKII SUBMERSSIBLE\",\"wander\":true}]}");
        Assert.False(flipped.Single().Wander);
        var spot = Program.ParseBoatSpot(json);
        Assert.NotNull(spot);
        Assert.Equal("Peronaut Pier", spot.Landmark);
        Assert.InRange(spot.Pos.X, 214.0f, 218.6f);   // on the pier deck (rails x 214.0 / 218.6)
        Assert.InRange(spot.Pos.Y, 17.0f, 49.4f);
        // ordinary seats keep no mode
        Assert.All(seats.Where(s => !Program.IsBoat(s)), s => Assert.Null(s.Mode));
    }
}
