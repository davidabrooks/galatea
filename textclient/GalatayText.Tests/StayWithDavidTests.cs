using GalatayText;
using LibreMetaverse;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-09 09:36 PT Lalou sofa: David sat with her and picked couples poses; a chat pause ended and the wander stood her up.</summary>
public class StayWithDavidTests
{
    [Theory]
    [InlineData(null, true)] [InlineData("chat", true)] [InlineData("greet", true)] [InlineData("rest", true)]
    [InlineData("david", false)] [InlineData("user", false)] [InlineData("hold", false)] [InlineData("ao", false)]
    public void Hold_starts_over_automatic_pauses_only(string pause, bool starts) =>
        Assert.Equal(starts, Program.DavidSeatHoldStarts(wanderOn: true, seated: true, davidOnSeat: true, pause));

    [Fact]
    public void No_hold_without_wander_seat_or_David()
    {
        Assert.False(Program.DavidSeatHoldStarts(false, true, true, null));
        Assert.False(Program.DavidSeatHoldStarts(true, false, true, null));
        Assert.False(Program.DavidSeatHoldStarts(true, true, false, null));
    }

    [Fact]
    public void Resume_only_30_s_after_David_left()
    {
        var t = new DateTime(2026, 10, 9, 9, 38, 0);
        Assert.False(Program.DavidSeatHoldCanResume(true, t, t.AddMinutes(10), 30));     // still on the seat
        Assert.False(Program.DavidSeatHoldCanResume(false, null, t, 30));                // never seen leaving
        Assert.False(Program.DavidSeatHoldCanResume(false, t, t.AddSeconds(29), 30));
        Assert.True(Program.DavidSeatHoldCanResume(false, t, t.AddSeconds(30), 30));
    }

    [Fact]
    public void Chat_pause_never_replaces_the_David_hold()
    {
        Assert.Equal("david", Program.PauseAfterRequest("david", "chat"));
        Assert.Equal("david", Program.PauseAfterRequest("david", "greet"));
        Assert.Equal("user", Program.PauseAfterRequest("david", "user"));
        Assert.Equal("chat", Program.PauseAfterRequest(null, "chat"));
        Assert.Equal("david", Program.PauseAfterRequest("chat", "david"));
    }

    [Fact]
    public void Keeper_never_falls_back_to_her_old_solo_pose_with_David()
    {
        var solo = new UUID("c8ae03fe-b8c6-6c55-99fb-4abb665d9292");   // Backlean (her pick)
        var his = new UUID("6c3cb302-8e33-2a82-8521-0b65c03ae7cb");    // his couples pick
        Assert.Empty(Program.KeeperReassertSet(new UUID[0], new[] { solo }, davidOnSeat: true));
        Assert.Equal(new[] { his }, Program.KeeperReassertSet(new[] { his }, new[] { solo }, davidOnSeat: true));
        Assert.Equal(new[] { solo }, Program.KeeperReassertSet(new UUID[0], new[] { solo }, davidOnSeat: false));
    }

    [Fact]
    public void Seat_pose_start_is_his_pick_only_when_she_pressed_nothing()
    {
        Assert.True(Program.PoseChangeIsDavids(true, true, false, 11));   // 09:36:13 6c3cb302, 11 s after he sat
        Assert.False(Program.PoseChangeIsDavids(true, true, false, 1));   // her own menu press just now
        Assert.False(Program.PoseChangeIsDavids(true, true, true, 60));   // AVsitter restart of the same anim
        Assert.False(Program.PoseChangeIsDavids(true, false, false, 60)); // AO / head anim
        Assert.False(Program.PoseChangeIsDavids(false, true, false, 60));
    }

    [Fact]
    public void Solo_picker_is_never_reached_while_David_is_on_the_seat()
    {
        Assert.True(Program.DavidChoosesPoses(davidOnSeat: true, explicitCouplesRequest: false));
    }
}
