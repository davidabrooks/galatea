using Xunit;
using P = GalatayText.Program;
namespace GalatayText.Tests;

// 2026-11-02 15:48: her own wander leg toward the patio read as a collar pull toward David ("I did not leash you")
public class LeashFalsePullTests
{
    static readonly DateTime Now = new(2026, 11, 2, 15, 48, 9);
    [Fact] public void WanderLegCoastIsOwnMovement()
    {
        Assert.True(P.OwnMoveOrCoast(false, Now, Now.AddSeconds(-2)));    // a leg/stand just ended: still momentum
        Assert.False(P.OwnMoveOrCoast(false, Now, Now.AddSeconds(-10)));
        Assert.True(P.OwnMoveOrCoast(true, Now, DateTime.MinValue));
    }
    [Fact] public void PullWithoutCollarEvidenceIsNoLeash()
    {
        Assert.False(P.PullLeashHasEvidence(Now, DateTime.MinValue));      // the 15:48 case: no chain, no collar text
        Assert.True(P.PullLeashHasEvidence(Now, Now.AddSeconds(-30)));     // collar chain/leash text a moment ago
        Assert.False(P.PullLeashHasEvidence(Now, Now.AddMinutes(-10)));
    }
    [Fact] public void PullOnlyLeashGoesStale()
    {
        const string how = "collar pull (invisible leash?)";
        Assert.True(P.PullLeashStale(how, Now, Now.AddMinutes(-2), DateTime.MinValue));
        Assert.False(P.PullLeashStale(how, Now, Now.AddSeconds(-20), DateTime.MinValue));   // still being pulled
        Assert.False(P.PullLeashStale(how, Now, Now.AddMinutes(-2), Now.AddSeconds(-10)));  // collar evidence lately
        Assert.False(P.PullLeashStale("particle chain", Now, Now.AddMinutes(-2), DateTime.MinValue));
        Assert.True(P.PullLeashStale(how, Now, DateTime.MinValue, DateTime.MinValue));      // restored after a relog, no pull since
    }
}
