using GalatayText;
using LibreMetaverse;
using Xunit;
public class LoginJoinTests
{
    [Fact] public void SpawnDistance_OnlyWhenBothArrived()
    {
        Assert.Null(Program.LoginSpawnDistance(null, new Vector3(1, 1, 1)));
        Assert.Null(Program.LoginSpawnDistance(new Vector3(1, 1, 1), null));
        Assert.Null(Program.LoginSpawnDistance(Vector3.Zero, new Vector3(1, 1, 1)));   // stale/unset position
        Assert.Equal(15.6f, Program.LoginSpawnDistance(new Vector3(230.2f, 60.7f, 29.1f), new Vector3(228.2f, 76.2f, 29.1f)).Value, 1);
    }
    [Fact] public void AcrossTheRoom_WakeNeverSaysBySide()
    {
        var t = Program.LoginPlacementText(new Program.LoginPlacement(15.6f, "walked", 1.4f));
        Assert.Contains("spawned 15.6 m", t); Assert.Contains("walked over", t); Assert.Contains("did NOT appear by his side", t);
        Assert.Contains("UNKNOWN", Program.LoginPlacementText(null));
        Assert.Contains("UNKNOWN", Program.LoginPlacementText(new Program.LoginPlacement(null, "unknown", null)));
        Assert.DoesNotContain("NOT appear", Program.LoginPlacementText(new Program.LoginPlacement(1.2f, "spawned-near", 1.2f)));
    }
    [Fact] public void RelogAwayFromHim_WalksOver()
    {
        Assert.True(Program.LoginShouldJoin(15.6f, true, false));
        Assert.False(Program.LoginShouldJoin(1.5f, true, false));
        Assert.False(Program.LoginShouldJoin(15.6f, false, false));    // he's not in my region
        Assert.False(Program.LoginShouldJoin(15.6f, true, true));      // seated
        Assert.False(Program.LoginShouldJoin(null, true, false));      // unknown: don't guess
    }
}
