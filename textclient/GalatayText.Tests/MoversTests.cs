using GalatayText;
using Xunit;
using K = GalatayText.Program.MoverKind;

// 2026-11-23 David (one mover at a time): a follow kept running under the wander's seat walk (16:55) and the walk got STUCK
// 7.7 m short of the sofa. Every new self-directed mover now stops the running ones (Movers.cs MoverPlanFor).
public class MoversTests
{
    static Program.MoverPlan P(K next, bool own = false, bool forced = false, bool leashed = false, bool targetHolder = false,
                               bool follow = false, bool followHolder = false, bool followKeep = false, bool roam = false, bool walk = false, bool wsit = false)
        => Program.MoverPlanFor(next, own, forced, leashed, targetHolder, follow, followHolder, followKeep, roam, walk, wsit);

    [Fact] public void Wander_sit_stops_a_running_follow_the_1655_fight()
    {
        var p = P(K.WanderSit, follow: true);
        Assert.Null(p.Refuse);
        Assert.True(p.StopFollow);
        Assert.True(p.CancelAutoPilot);
    }

    [Theory]
    [InlineData((int)K.Walk)] [InlineData((int)K.WanderSit)] [InlineData((int)K.Sit)] [InlineData((int)K.Roam)] [InlineData((int)K.ComeHere)] [InlineData((int)K.Teleport)]
    public void Every_new_mover_stops_follow_walk_and_the_others(int kind)
    {
        var next = (K)kind;
        var p = P(next, follow: true, roam: true, walk: true, wsit: true);
        Assert.Null(p.Refuse);
        Assert.True(p.StopFollow);
        Assert.True(p.StopWalk);                                  // a new walk replaces a running one too
        Assert.Equal(next != K.Roam, p.StopRoam);
        Assert.Equal(next != K.WanderSit, p.StopWanderSit);
        Assert.True(p.CancelAutoPilot);
    }

    [Fact] public void A_new_follow_retargets_and_stops_the_rest()
    {
        var p = P(K.Follow, follow: true, roam: true, walk: true, wsit: true);
        Assert.False(p.StopFollow);
        Assert.True(p.StopRoam && p.StopWalk && p.StopWanderSit);
        Assert.False(p.CancelAutoPilot);                         // the (re-targeted) follow owns the autopilot
    }

    [Fact] public void A_movers_own_steps_are_not_new_movers()
    {
        var p = P(K.Sit, own: true, follow: true, walk: true, wsit: true);   // the wander-sit visit's own 'sit'
        Assert.False(p.StopsAnything);
        Assert.Null(p.Refuse);
        Assert.False(p.CancelAutoPilot);
    }

    [Fact] public void Leash_comes_first()
    {
        Assert.Null(P(K.Walk, leashed: true).Refuse);                      // short walks (sit_lap, doors) stay allowed; the pull wins
        Assert.NotNull(P(K.WanderSit, leashed: true).Refuse);
        Assert.NotNull(P(K.Roam, leashed: true).Refuse);
        Assert.NotNull(P(K.Follow, leashed: true).Refuse);                 // someone else
        Assert.Null(P(K.Follow, leashed: true, targetHolder: true).Refuse); // the holder
        // a sit / come-here / teleport are not refused, and never stop a follow of the leash holder
        foreach (var k in new[] { K.Sit, K.ComeHere, K.Teleport })
        {
            var p = P(k, leashed: true, follow: true, followHolder: true, walk: true);
            Assert.Null(p.Refuse);
            Assert.False(p.StopFollow);
            Assert.True(p.StopWalk);
            Assert.False(p.CancelAutoPilot);
        }
        // the holder flag means nothing without a leash
        Assert.True(P(K.Sit, follow: true, followHolder: true).StopFollow);
    }

    [Fact] public void Rlv_forced_actions_are_never_refused()
    {
        var p = P(K.WanderSit, forced: true, leashed: true, walk: true);
        Assert.Null(p.Refuse);
        Assert.True(p.StopWalk);
    }

    [Fact] public void Accepting_a_lure_keeps_a_follow_of_the_sender_only()
    {
        Assert.False(P(K.Teleport, follow: true, followKeep: true).StopFollow);
        Assert.True(P(K.Teleport, follow: true, followKeep: false).StopFollow);
    }

    [Fact] public void Nothing_running_nothing_stopped()
    {
        var p = P(K.Walk);
        Assert.False(p.StopsAnything);
        Assert.Null(p.Refuse);
    }

    [Fact] public void Follow_ends_after_a_teleport_the_leader_did_not_lure_me_on()
    {
        Assert.False(Program.FollowGiveUpAfterTeleport(true, false, Program.FollowTeleportGiveUpS - 1));
        Assert.True(Program.FollowGiveUpAfterTeleport(true, false, Program.FollowTeleportGiveUpS));
        Assert.False(Program.FollowGiveUpAfterTeleport(true, true, 600));    // his lure: he is at the arrival, keep holding
        Assert.False(Program.FollowGiveUpAfterTeleport(false, false, 600));  // he was seen here: keep following
    }
}
