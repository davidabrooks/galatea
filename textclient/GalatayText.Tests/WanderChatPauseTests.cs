using GalatayText;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-11-11 David: wandering should not stop unless someone chats within a few metres (5 m).</summary>
public class WanderChatPauseTests
{
    [Fact]
    public void Pause_range_is_five_metres() =>
        Assert.Equal(5f, Program.WanderChatPauseRangeM);

    [Theory]
    [InlineData(0f)]
    [InlineData(2.5f)]
    [InlineData(5f)]
    public void Local_chat_within_five_metres_pauses(float dist) =>
        Assert.True(Program.WanderChatShouldPause(im: false, dist));

    [Theory]
    [InlineData(5.001f)]
    [InlineData(5.1f)]
    [InlineData(19f)]
    [InlineData(20f)]
    [InlineData(40f)]
    public void Local_chat_farther_than_five_metres_does_not_pause(float dist) =>
        Assert.False(Program.WanderChatShouldPause(im: false, dist));

    [Theory]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    public void Local_chat_from_unknown_or_not_in_view_position_does_not_pause(float dist) =>
        Assert.False(Program.WanderChatShouldPause(im: false, dist));

    [Theory]
    [InlineData(-1f)]
    [InlineData(0f)]
    [InlineData(5f)]
    [InlineData(19f)]
    [InlineData(100f)]
    public void IMs_always_pause_regardless_of_distance(float dist) =>
        Assert.True(Program.WanderChatShouldPause(im: true, dist));

    [Theory]
    [InlineData(5.001f)]
    [InlineData(19f)]
    [InlineData(20f)]
    public void Far_local_chat_does_not_pause_wander_but_still_takes_the_reply_path(float dist)
    {
        var plan = Program.PlanChatInbound(im: false, dist);
        Assert.False(plan.pauseWander);
        Assert.True(plan.replyPath);
    }

    [Fact]
    public void Unknown_or_not_in_view_local_chat_does_not_pause_but_still_takes_the_reply_path()
    {
        var plan = Program.PlanChatInbound(im: false, -1f);
        Assert.False(plan.pauseWander);
        Assert.True(plan.replyPath);
    }

    [Theory]
    [InlineData(false, 3f, true)]
    [InlineData(true, 19f, true)]
    [InlineData(true, -1f, true)]
    public void Near_local_chat_and_IMs_still_pause_and_still_take_the_reply_path(bool im, float dist, bool expectPause)
    {
        var plan = Program.PlanChatInbound(im, dist);
        Assert.Equal(expectPause, plan.pauseWander);
        Assert.True(plan.replyPath);
    }
}
