using GalatayText;
using LibreMetaverse;
using Xunit;

// 2026-11-11 David: "follow me babe" must start the real follow (not come-here teleport);
// same-region come-here should walk when he is close; a landing-point miss is not success;
// following across a region border must not treat "within standoff" as arrived.
public class FollowComeHereTests
{
    [Theory]
    [InlineData("follow me babe")]
    [InlineData("follow me")]
    [InlineData("Follow")]
    [InlineData("come with me")]
    [InlineData("walk with me babe")]
    [InlineData("Galatea, come with me")]
    public void Follow_phrases_are_follow_requests_not_come_here(string text)
    {
        Assert.True(Program.IsFollowRequest(text), text);
        Assert.False(Program.IsComeRequest(text), text);
    }

    [Theory]
    [InlineData("come here babe")]
    [InlineData("Come here")]
    [InlineData("come over here")]
    [InlineData("come to me")]
    [InlineData("get over here")]
    [InlineData("join me")]
    public void Come_here_phrases_stay_come_here_not_follow(string text)
    {
        Assert.True(Program.IsComeRequest(text), text);
        Assert.False(Program.IsFollowRequest(text), text);
    }

    [Theory]
    [InlineData("can you hear me babe?")]
    [InlineData("stop following me")]
    [InlineData("following you")]
    [InlineData("hi babe")]
    public void Hearing_and_stop_following_are_neither(string text)
    {
        Assert.False(Program.IsFollowRequest(text), text);
        Assert.False(Program.IsComeRequest(text), text);
    }

    [Fact]
    public void Same_region_come_here_walks_when_close_and_path_ok()
    {
        Assert.True(Program.ComeHerePreferWalk(inHome: false, horizDist: 40f, pathOk: true));
        Assert.True(Program.ComeHerePreferWalk(inHome: false, horizDist: 64f, pathOk: true));
        Assert.False(Program.ComeHerePreferWalk(inHome: false, horizDist: 65f, pathOk: true));
        Assert.False(Program.ComeHerePreferWalk(inHome: false, horizDist: 20f, pathOk: false));
        Assert.False(Program.ComeHerePreferWalk(inHome: true, horizDist: 20f, pathOk: true));
    }

    [Fact]
    public void Landing_point_miss_is_not_success_past_five_metres()
    {
        Assert.False(Program.TeleportMissedSpot(0f));
        Assert.False(Program.TeleportMissedSpot(5f));
        Assert.True(Program.TeleportMissedSpot(5.01f));
        // Ahern 15:48:24: requested ~<9,9,41>, landed at entry <7.69,10.09,40.24> (~1.7 m) would be inside slack;
        // a telehub bounce farther than 5 m must walk the rest.
        Assert.True(Program.TeleportMissedSpot(H(new Vector3(7.69f, 10.09f, 40.24f), new Vector3(20f, 20f, 40f))));
        Assert.False(Program.TeleportMissedSpot(H(new Vector3(7.69f, 10.09f, 40.24f), new Vector3(9f, 9f, 41f))));
    }

    [Fact]
    public void Within_standoff_across_a_border_is_not_arrived()
    {
        // 15:51:05: 1.2 m from David, standoff 1.0 m, but she was still in Ahern and he was in Dore.
        Assert.Equal(Program.FollowAct.Pursue, Program.FollowDecide(1.2f, true, 0.2f, 0, 1.0f, false, sameRegion: false));
        Assert.Equal(Program.FollowAct.Pursue, Program.FollowDecide(1.2f, false, 0.2f, 10, 1.0f, false, sameRegion: false));
        Assert.Equal(Program.FollowAct.Arrive, Program.FollowDecide(1.2f, true, 0.2f, 0, 1.0f, false, sameRegion: true));
    }

    [Fact]
    public void Border_push_aims_well_past_the_border_not_at_a_1m_standoff()
    {
        // 15:50:59 Ahern -> Dore: she was at <2.2,24.5>, standoff at x≈-0.01; aim must go 8 m into Dore.
        var aim = SimGeo.CrossAim(new Vector3(2.2f, 24.5f, 40.2f), new Vector3(-0.01f, 24.12f, 40.2f));
        Assert.True(aim.X <= -8f + 1e-3f);
        Assert.InRange(aim.Y, 24.11f, 24.13f);
        Assert.True(SimGeo.CrossPastBorderNoHandoff(new Vector3(-0.1f, 24.1f, 40.2f), "W"));
        Assert.False(SimGeo.CrossPastBorderNoHandoff(new Vector3(2.2f, 24.5f, 40.2f), "W"));
        var back = SimGeo.CrossBackoff(new Vector3(-0.1f, 24.1f, 40.2f), "W", 0);
        Assert.InRange(back.X, 2.9f, 3.1f);
    }

    [Fact]
    public void Default_follow_standoff_is_one_and_a_half_metres()
    {
        Assert.Equal(1.5f, Program.FollowDistStd);
        var (stopAt, resumeAt, tooClose) = Program.FollowBands(Program.FollowDistStd, indoor: false);
        Assert.InRange(stopAt, 1.89f, 1.91f);
        Assert.InRange(resumeAt, 2.49f, 2.51f);
        Assert.InRange(tooClose, 0.99f, 1.01f);
    }

    [Fact]
    public void After_a_teleport_stale_leader_positions_are_not_used()
    {
        var t0 = new DateTime(2026, 11, 11, 16, 5, 4);
        ulong ahern = SimGeo.Handle(1001u * 256, 1001u * 256);
        ulong dore = SimGeo.Handle(1000u * 256, 1001u * 256);
        // 16:05:04: stale Dore cache converted into the Ahern frame (~231.9 m west) must not be followed.
        Assert.False(Program.FollowPosIsUsable(ahern, dore, t0.AddSeconds(-2), t0, t0.AddSeconds(0.1), 4f, true, 6f));
        Assert.False(Program.FollowPosIsUsable(ahern, dore, t0.AddSeconds(0.2), t0, t0.AddSeconds(1), 4f, true, 6f));
        Assert.True(Program.FollowPosIsUsable(ahern, ahern, t0.AddSeconds(0.2), t0, t0.AddSeconds(0.5), 4f, true, 6f));
        Assert.False(Program.FollowPosIsUsable(ahern, ahern, t0.AddSeconds(0.2), t0, t0.AddSeconds(5), 4f, false, 6f));
        Assert.True(Program.FollowPosIsUsable(ahern, dore, t0.AddSeconds(1), DateTime.MinValue, t0.AddSeconds(2), 4f, false, 6f));
        Assert.True(Program.FollowShouldHoldForFreshPos(true, false));
        Assert.False(Program.FollowShouldHoldForFreshPos(true, true));
    }

    [Fact]
    public void Per_call_follow_distance_sticks_for_the_session()
    {
        Assert.Equal(1.5f, Program.NextFollowSessionDist(1.5f, null, sameTarget: true, stopping: false));
        Assert.Equal(1.0f, Program.NextFollowSessionDist(1.5f, 1.0f, sameTarget: true, stopping: false));
        Assert.Null(Program.NextFollowSessionDist(1.5f, null, sameTarget: false, stopping: false));
        Assert.Null(Program.NextFollowSessionDist(1.5f, 1.5f, sameTarget: true, stopping: true));
    }

    static float H(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
