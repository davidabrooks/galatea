using Xunit;

namespace GalatayText.Tests;

/// <summary>Wraps galatay-text offline --*-selftest flags (no SL login).</summary>
public class SelftestCliTests
{
    [Theory]
    [InlineData("--pose-selftest")]           // PR #39/#51/#52/#53 seat pose rules
    [InlineData("--pose-keeper-selftest")]    // PR #43 seated AO / pose recovery
    [InlineData("--chat-guard-selftest")]     // PR #54 say --re
    [InlineData("--im-target-selftest")]     // im recipient split: one-word usernames, quotes, uuid, known contacts
    [InlineData("--inv-trash-selftest")]     // inv trash: exact names, ambiguity by uuid, never worn/COF/Trash
    [InlineData("--neighbor-selftest")]   // neighbor regions: handle/offset math, crossing state machine, neighbor object cap, chat dedupe
    [InlineData("--crowd-selftest")]   // coarse-only avatars, attachment state, 360 retry, look attachment wait
    [InlineData("--friendwatch-selftest")]   // david_login wake rules + event text (no reminder clause when none)
    [InlineData("--imguard-selftest")]        // PR #32 im --re + ImGuard
    [InlineData("--attach-move-selftest")]    // PR #40/#42 attach move
    [InlineData("--detach-cof-selftest")]     // PR #41 detach COF
    [InlineData("--follow-door-selftest")]    // PR #44/#48 follow + doors + nav + linger
    [InlineData("--front-selftest")]         // front arc + avatar heading helpers
    [InlineData("--bikini-selftest")]        // bikini HUD random among D/W/T textures
    [InlineData("--outfit-zones-selftest")]   // Peronaut beach/house + daily candidates
    [InlineData("--home-seats-selftest")]     // Peronaut lower-level seats over the path graph (tour 2026-10-05)
    [InlineData("--clothing-huds-selftest")]  // clothing -> color HUD specs (Bikini + 3 tops), ARTi'S swatch grid, no-repeat random pick
    [InlineData("--outfit-safe-selftest")]    // no hair stacking, AO protected, clothing HUD buttons
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
