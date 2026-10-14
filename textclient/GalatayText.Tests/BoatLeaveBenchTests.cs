using GalatayText;
using LibreMetaverse;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-14 14:44 replay: 'wander' while she sat in tan 2 on the Beach House boat (seated by a pose command,
/// so the wander's own boat flag was 0): the first leg stood her up and she fell into the water (z 9.4).
/// Rule (David): from a boat never stand/walk off; sit straight onto a free pier bench, then stand and walk.</summary>
public class BoatLeaveBenchTests
{
    static List<Program.HomeSeatInfo> Seats()
    {
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "routes", "_seats-peronaut-home.json"));
        return Program.ParseHomeSeats(File.ReadAllText(File.Exists(repo) ? repo : "/workspace/galatea-sl-repo/textclient/routes/_seats-peronaut-home.json"));
    }
    static readonly UUID BeachHouse = new("aebcf450-75bd-598e-e467-1071c2572a47");
    static readonly Vector3 Tan2 = new(223.4f, 23.7f, 22.7f);   // where she sat at 14:44 (not inside OnBoatWater's z < 22.5)

    [Fact]
    public void Seated_on_a_listed_boat_counts_whoever_seated_her()
    {
        Assert.True(Program.SeatedOnBoat(true, BeachHouse, Tan2, Seats()));
        Assert.False(Program.SeatedOnBoat(false, BeachHouse, Tan2, Seats()));
        Assert.False(Program.SeatedOnBoat(true, new UUID("753e6d7c-5cc0-5ab2-d015-f80b6ef5abf6"), new Vector3(214.8f, 30.5f, 23.9f), Seats()));
    }

    [Fact]
    public void Nearest_free_pier_bench_first_and_occupied_skipped()
    {
        var b = Program.PierBenchesFrom(Tan2, Seats(), _ => false);
        Assert.Equal(3, b.Count);
        Assert.Equal(new UUID("90a518f7-e954-b6f1-3d4f-eb0c7ed1449f"), b[0].Id);   // third bench (218.0,24.4) is nearest the boat
        var free = Program.PierBenchesFrom(Tan2, Seats(), id => id == b[0].Id);
        Assert.DoesNotContain(free, x => x.Id == b[0].Id);
        Assert.All(b, x => Assert.False(Program.IsBoat(x)));
    }
}
