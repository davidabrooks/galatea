using Xunit;

namespace GalatayText.Tests;

/// <summary>Wraps galatay-text offline --*-selftest flags (no SL login).</summary>
public class SelftestCliTests
{
    [Theory]
    [InlineData("--pose-selftest")]           // PR #39/#51/#52/#53 seat pose rules
    [InlineData("--pose-keeper-selftest")]    // PR #43 seated AO / pose recovery
    [InlineData("--chat-guard-selftest")]     // PR #54 say --re
    [InlineData("--imguard-selftest")]        // PR #32 im --re + ImGuard
    [InlineData("--attach-move-selftest")]    // PR #40/#42 attach move
    [InlineData("--detach-cof-selftest")]     // PR #41 detach COF
    [InlineData("--follow-door-selftest")]    // PR #44/#48 follow + doors + nav + linger
    [InlineData("--front-selftest")]         // front arc + avatar heading helpers
    [InlineData("--bikini-selftest")]        // bikini HUD random among D/W/T textures
    [InlineData("--outfit-zones-selftest")]   // Peronaut beach/house + daily candidates
    [InlineData("--exp-selftest")]            // PR #45/#46 experiences + TEMP
    [InlineData("--worn-selftest")]           // attachment tracking
    [InlineData("--voice-wake-selftest")]     // PR #49/#50 voice wake
    public void Offline_selftest_passes(string flag) => SelftestRunner.AssertPass(flag);

    [Fact]
    public void Bugfix_bundle_passes() => SelftestRunner.AssertPass("--bugfix-selftest", 180_000);

    // PR #47 full voice WebRTC+whisper selftest: skipped in CI (downloads speech model, slow/flaky).
    [Fact(Skip = "Needs faster-whisper model download and ~150s; run locally with --voice-selftest")]
    public void Voice_full_selftest_skipped_in_ci() => SelftestRunner.AssertPass("--voice-selftest", 200_000);
}
