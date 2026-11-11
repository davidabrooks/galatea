using GalatayText;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-11-11 David: do not play the typing animation or send the typing start/stop indicator
/// when sending nearby chat or IMs. The human-like delay is unchanged.</summary>
public class TypingIndicatorTests
{
    [Fact]
    public void Typing_indicator_and_animation_are_disabled()
    {
        Assert.False(Program.TypingIndicatorEnabled);
        Assert.Empty(Program.TypingEmitActions(Program.TypingIndicatorEnabled, im: false, on: true));
        Assert.Empty(Program.TypingEmitActions(Program.TypingIndicatorEnabled, im: false, on: false));
        Assert.Empty(Program.TypingEmitActions(Program.TypingIndicatorEnabled, im: true, on: true));
        Assert.Empty(Program.TypingEmitActions(Program.TypingIndicatorEnabled, im: true, on: false));
    }

    [Fact]
    public void If_reenabled_nearby_chat_would_send_typing_state_and_animation()
    {
        Assert.Equal(new[] { "chat-start-typing", "anim-type-start" }, Program.TypingEmitActions(true, im: false, on: true));
        Assert.Equal(new[] { "chat-stop-typing", "anim-type-stop" }, Program.TypingEmitActions(true, im: false, on: false));
    }

    [Fact]
    public void If_reenabled_IMs_would_send_the_typing_dialog_only()
    {
        Assert.Equal(new[] { "im-start-typing" }, Program.TypingEmitActions(true, im: true, on: true));
        Assert.Equal(new[] { "im-stop-typing" }, Program.TypingEmitActions(true, im: true, on: false));
    }
}
