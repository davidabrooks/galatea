// StandStepOff.cs (2026-10-30 PT, David: "When you stood from this furniture you ended up standing on the chair").
// The plain `stand` tool only sent Stand(): the sim drops her at the seat prim's top (Xplicit Makeup Table chair, z ~29.5 vs
// floor ~28.95) and nothing moved her off. The stand-out / clear-floor steps (SeatStandOut.cs, ClearFloor.cs) ran only for
// wander and outfit flows. Now an explicit stand from an object also runs the clear-floor step (home footprints + nav grid,
// the seat's stand_spot first). The -4 cm stand hover is untouched: the posture-change hover check restores it after the walk.
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    // pure: step off after the stand tool only when she was seated on an object (a ground sit leaves her on the floor already)
    internal static bool StandCommandStepsOff(bool wasSeatedOnObject) => wasSeatedOnObject;

    // pure: what runs after the stand tool unseats her, in order. The stand hover repin is ALWAYS last (2026-10-30: she floated after
    // `stand` until a manual `hover repin`), so it also follows any step-off walk.
    internal static string[] StandCommandAfterSteps(bool wasSeatedOnObject) =>
        StandCommandStepsOff(wasSeatedOnObject) ? new[] { "step off", "repin hover" } : new[] { "repin hover" };

    static async Task StandCommandAfter(bool wasSeatedOnObject)
    {
        foreach (var step in StandCommandAfterSteps(wasSeatedOnObject))
            try
            {
                if (step == "step off")
                {
                    await Task.Delay(1200);   // let the sim settle her stand position first
                    using var cc = new CancellationTokenSource(30000);
                    await ClearFloorForOutfitChange("stand command", null, cc.Token);
                }
                else if (client.Self.SittingOn == 0)
                {
                    await Task.Delay(500);
                    var r = await PostHover(PinnedHover(), "stand command repin");
                    WLog($"stand command: hover repin {PinnedHover():F3} -> {(r.ok ? "ok" : "failed")} {r.info}");
                }
            }
            catch (Exception ex) { WLog($"stand command ({step}): " + ex.GetBaseException().Message); }
    }
}
