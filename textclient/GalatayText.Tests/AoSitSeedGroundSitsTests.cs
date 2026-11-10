using GalatayText;
using LibreMetaverse;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-11-09 15:46 PT: after a relog the AO held VAMOSITG26 (03b3df38), which an earlier login had learned as a STAND, so idle_pose status said
/// "standing" while the render showed her sitting. 42303bb6 (VAMOSITG22) was filed the same way. Both are seeded sits; the seed beats the learned file.</summary>
public class AoSitSeedGroundSitsTests
{
    static readonly UUID G26 = UUID.Parse("03b3df38-4116-f8bc-055a-6302a9245cf1"), G22 = UUID.Parse("42303bb6-35d6-8419-2379-c9b01bfdd913"),
        Stand = UUID.Parse("be2dba76-6004-28cb-29e4-68f73d54f13b");

    [Fact]
    public void A_file_that_lists_the_ground_sits_as_stands_still_loads_them_as_sits()
    {
        // the live file on 2026-11-09 before the fix
        var d = new Dictionary<string, List<string>> { ["sit"] = new(), ["stand"] = new() { G26.ToString(), G22.ToString(), Stand.ToString() }, ["other"] = new() };
        HashSet<UUID> sit = new(), stand = new(), other = new();
        Program.AoClassMerge(d, sit, stand, other);
        Assert.Contains(G26, sit); Assert.Contains(G22, sit);
        Assert.DoesNotContain(G26, stand); Assert.DoesNotContain(G22, stand);
        Assert.Contains(Stand, stand); Assert.DoesNotContain(Stand, sit);
    }

    [Fact]
    public void The_held_G26_sit_reads_as_an_AO_sit_playing()
    {
        var playing = new[] { G26, Animations.STAND };
        HashSet<UUID> sit = new(), stand = new(), other = new();
        Program.AoClassMerge(new() { ["stand"] = new() { G26.ToString() } }, sit, stand, other);
        Assert.True(Program.AoSitPlaying(playing, false, sit));
        Assert.Contains(G26, Program.AoSitSeed); Assert.Contains(G22, Program.AoSitSeed);
        // and it can never be learned as a stand again (AoNoteStarted returns on a seeded sit)
        Assert.True(Program.AoStandMayToggle(null, false, Program.AoSitPlaying(playing, false, sit)));
    }
}
