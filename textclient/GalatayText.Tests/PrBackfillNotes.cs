using Xunit;

namespace GalatayText.Tests;

/// <summary>
/// Named anchors for PR backfill coverage. Heavy behavior lives in CLI selftests;
/// this file documents which PR each area maps to (and keeps a smoke check that the
/// test runner itself is wired).
/// </summary>
public class PrBackfillNotes
{
    // look-render / vision: see vision/tests (PR #33–#37)
    // wander/greet + webhook retry: --bugfix-selftest (PR #38)
    // outfit detach COF: --detach-cof-selftest (PR #41)
    // TEMP false positive: --exp-selftest (PR #46)
    // AVsitter experience: --exp-selftest (PR #45)
    // seat pose / couples: --pose-selftest + --pose-keeper-selftest (PR #43, #51–#53)
    // follow + doors: --follow-door-selftest (PR #44)
    // Peronaut nav: --follow-door-selftest includes nav (PR #48)
    // voice wake: --voice-wake-selftest (PR #49, #50)
    // say --re: --chat-guard-selftest (PR #54)

    [Fact]
    public void Pr_backfill_map_is_documented()
    {
        var map = new (int pr, string area)[]
        {
            (33, "vision crowd / attachments"),
            (34, "scene-mesher InvBind"),
            (35, "scene-mesher degenerate InvBind"),
            (36, "vision alpha blend"),
            (37, "look around faster offline"),
            (38, "wander hold + webhook retry"),
            (41, "detach COF persistence"),
            (43, "pose keeper / AO stand"),
            (44, "follow distance + doors"),
            (45, "AVsitter experience auto-grant"),
            (46, "TEMP false positive"),
            (48, "Peronaut nav + seat catalog"),
            (49, "voice wake name/invitation"),
            (50, "voice invitation questions, comments"),
            (51, "no auto-couples on shared seat"),
            (52, "solo when David leaves"),
            (53, "pose path back to root"),
            (54, "say --re nearby chat dedupe"),
        };
        Assert.True(map.Length >= 15);
        Assert.Contains(map, x => x.pr == 54);
    }
}
