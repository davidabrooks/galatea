using System.Linq;
using GalatayText;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-11-11 David: the typing indicator is ok to use; the typing animation must not play.
/// The human-like reply delay is unchanged.</summary>
public class TypingIndicatorTests
{
    [Fact]
    public void Typing_indicator_is_on_but_the_animation_is_never_started()
    {
        Assert.True(Program.TypingIndicatorEnabled);
        foreach (var im in new[] { false, true })
        foreach (var on in new[] { false, true })
        {
            var actions = Program.TypingEmitActions(Program.TypingIndicatorEnabled, im, on);
            Assert.DoesNotContain("anim-type-start", actions);
        }
    }

    [Fact]
    public void Nearby_chat_sends_the_typing_indicator_and_stops_the_type_animation()
    {
        // StartTyping can make the sim start TYPE; we send AnimationStop right after, and again on stop
        // (Firestorm with "play typing animation" off still sends the typing chat and stops TYPE on stop).
        Assert.Equal(new[] { "chat-start-typing", "anim-type-stop" }, Program.TypingEmitActions(true, im: false, on: true));
        Assert.Equal(new[] { "chat-stop-typing", "anim-type-stop" }, Program.TypingEmitActions(true, im: false, on: false));
    }

    [Fact]
    public void IMs_send_the_typing_dialog_only()
    {
        Assert.Equal(new[] { "im-start-typing" }, Program.TypingEmitActions(true, im: true, on: true));
        Assert.Equal(new[] { "im-stop-typing" }, Program.TypingEmitActions(true, im: true, on: false));
        Assert.DoesNotContain("anim-type-stop", Program.TypingEmitActions(true, im: true, on: true));
        Assert.DoesNotContain("anim-type-start", Program.TypingEmitActions(true, im: true, on: true));
    }

    [Fact]
    public void When_disabled_nothing_is_sent()
    {
        Assert.Empty(Program.TypingEmitActions(false, im: false, on: true));
        Assert.Empty(Program.TypingEmitActions(false, im: true, on: true));
    }
}
