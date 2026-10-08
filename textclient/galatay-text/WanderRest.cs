// WanderRest.cs (2026-10-08 14:51, David approved): after 3 failed walks in a row the home wander no longer turns itself
// off. She walks to the living-room sofa (the same recovery walk as before), sits, rests 5-10 min ("rest" pause), then
// stands and wanders again on her own. Cap: at most MaxRestsPerHour rests in any 60 min; the next give-up after that
// turns the wander off (flag cleared) and logs it, so a stuck spot can never loop forever.
// Chat during a rest switches to the normal chat pause; when the chat goes quiet she goes back to resting until the
// rest time is up (still seated), or simply wanders on (if she got up to talk). 'wander resume' ends a rest at once.
namespace GalatayText;

public static partial class Program
{
    internal const int MaxRestsPerHour = 2;
    internal const int RestMinS = 300, RestMaxS = 600;
    static readonly List<DateTime> wRests = new();
    static DateTime? wRestUntil;

    // pure: may she rest now (fewer than maxPerHour rests in the last 60 min)? Otherwise the wander turns off.
    internal static bool RestAllowed(IEnumerable<DateTime> restsAt, DateTime now, int maxPerHour = MaxRestsPerHour) =>
        restsAt.Count(t => t <= now && now - t < TimeSpan.FromHours(1)) < maxPerHour;

    internal static int RestSeconds(Random r) => r.Next(RestMinS, RestMaxS + 1);

    static bool wRestSeated;   // she sat on the sofa for this rest (a failed sit rests standing where she is)

    // pure: what the loop does with a rest once no other pause is active.
    //   "none"   no rest going on
    //   "rest"   keep resting (time left; still seated if the rest began seated)
    //   "end"    rest over (time up, or she got up from the sofa, e.g. to talk): stand if needed and wander again
    internal static string RestState(DateTime? until, DateTime now, bool restSeated, bool seatedNow) =>
        until is not DateTime u ? "none" : now >= u || (restSeated && !seatedNow) ? "end" : "rest";

    static string RestStateNow() => RestState(wRestUntil, DateTime.Now, wRestSeated, client.Self.SittingOn != 0);
    static void WanderRestReset() { wRestUntil = null; wRestSeated = false; lock (wRests) wRests.Clear(); }
}
