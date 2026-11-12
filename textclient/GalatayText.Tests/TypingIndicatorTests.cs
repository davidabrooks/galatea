using System;
using System.Linq;
using GalatayText;
using LibreMetaverse;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-11-11 David: the typing indicator is ok to use; the typing animation must not play.
/// The human-like reply delay is unchanged. Packet path mirrors Firestorm with PlayTypingAnim off.</summary>
public class TypingIndicatorTests
{
    static readonly UUID KissChin = new("7431bb98-7b70-1502-ceaf-61f9b3ac6544"); // 17:11:35, 10 s into typing
    static readonly UUID UnknownAttTypist = new("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

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
        // Firestorm llagent.cpp startTyping with PlayTypingAnim off: CHAT_TYPE_START, no TYPE start.
        // stopTyping always sends ANIM_AGENT_TYPE STOP and CHAT_TYPE_STOP. We also stop TYPE right after start.
        Assert.Equal(new[] { "chat-start-typing", "anim-type-stop" }, Program.TypingEmitActions(true, im: false, on: true));
        Assert.Equal(new[] { "chat-stop-typing", "anim-type-stop" }, Program.TypingEmitActions(true, im: false, on: false));
    }

    [Fact]
    public void IMs_send_the_typing_dialog_only()
    {
        // Firestorm llimview.cpp pack_instant_message IM_TYPING_START/STOP does not start TYPE.
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

    [Fact]
    public void Built_in_TYPE_and_known_typist_overrides_are_always_stopped()
    {
        var known = Program.TypingKnownOverrides();
        Assert.Contains(Program.TypingAnimVistaAo, known);
        Assert.Contains(Program.TypingAnimEvoxHead, known);
        Assert.True(Program.TypingIsTypistAnim(Animations.TYPE, fromAttachment: false, secondsSinceTypingStart: 0, known));
        Assert.True(Program.TypingIsTypistAnim(Program.TypingAnimVistaAo, fromAttachment: true, 0.8, known));
        Assert.True(Program.TypingIsTypistAnim(Program.TypingAnimEvoxHead, fromAttachment: true, 1.0, known));
        // Known typist overrides stay suppressed even after the start window (AO may restart them).
        Assert.True(Program.TypingIsTypistAnim(Program.TypingAnimVistaAo, fromAttachment: true, 10, known));
    }

    [Fact]
    public void Attachment_typist_in_the_AGENT_TYPING_window_is_stopped_but_later_anims_are_not()
    {
        var known = Program.TypingKnownOverrides();
        // First sight of a typist AO, ~1 s after StartTyping (17:10:31 pattern).
        Assert.True(Program.TypingIsTypistAnim(UnknownAttTypist, fromAttachment: true, 0.5, known));
        Assert.True(Program.TypingIsTypistAnim(UnknownAttTypist, fromAttachment: true, Program.TypingOverrideWindowSec, known));
        // Kiss from the EvoX head at 17:11:35, 10 s into typing, must not be stopped.
        Assert.False(Program.TypingIsTypistAnim(KissChin, fromAttachment: true, 10, known));
        Assert.False(Program.TypingIsTypistAnim(KissChin, fromAttachment: true, 10, known: Array.Empty<UUID>()));
        // Seat / built-in / self-sourced anims in the window are not typist overrides.
        Assert.False(Program.TypingIsTypistAnim(UnknownAttTypist, fromAttachment: false, 0.5, known));
        Assert.False(Program.TypingIsTypistAnim(UUID.Zero, fromAttachment: true, 0.5, known));
    }
}
