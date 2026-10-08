// WashHands.cs (2026-10-08 14:41, David: "wash your hands in the new bathroom sink after you use the toilet").
// Any sit on the toilet (wander or a manual 'sit') marks a hand wash as due. The home wander's next step, once she is
// standing and dressed again, is the bathroom sink: Solo* > "Wash Hands" for 20-40 s, then stand and wander as usual.
// A busy / out-of-view / recently failed sink (or a seat list without the button) skips the wash; she never waits for it.
using System.Text.RegularExpressions;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    internal static readonly UUID ToiletSeatId = new("48b303e0-8dcc-58e2-39bf-23e9f9f895b9");
    internal static readonly UUID BathroomSinkId = new("406eeb80-c05f-2f82-0857-145b725da0f8");
    internal const int WashStayMinS = 20, WashStayMaxS = 40;
    // ponytail: a toilet sit older than 30 min no longer triggers the wash; ceiling = a very long chat pause on the toilet
    internal static readonly TimeSpan WashDueWindow = TimeSpan.FromMinutes(30);
    static readonly Regex WashHandsRx = new(@"^\s*wash\s*hands?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex ShaveRx = new(@"shave", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static DateTime? toiletSatAt;   // set by the sit response for the toilet; consumed by the home wander

    static void NoteSitForWash(UUID seat)
    {
        if (seat != ToiletSeatId) return;
        toiletSatAt = DateTime.Now;
        Log("wander", "toilet sit: hands get washed at the bathroom sink next");
    }

    internal static bool WashHandsDue(DateTime? toiletAt, DateTime now) => toiletAt is DateTime t && now - t <= WashDueWindow && now >= t;

    // pure: the sink menu path for washing hands (fixed steps from the seat list + the hand-wash button; never Shave)
    internal static List<string> WashHandsMenu(HomeSeatInfo sink)
    {
        if (sink == null || sink.MenuFixed.Count == 0 || !HomeSeatSoloMenuPath(sink)) return null;
        var b = sink.MenuChoice.FirstOrDefault(x => WashHandsRx.IsMatch(x ?? "") && !ShaveRx.IsMatch(x));
        return b == null ? null : sink.MenuFixed.Append(b).ToList();
    }

    // pure: the next wander pick after the toilet. freeSeats = the wander's current seat candidates (occupied, recently
    // failed, out of view or too close to an avatar are already left out). null sink = skip the wash (why says so).
    internal static (UUID sink, List<string> menu, string skip) WashHandsPick(IReadOnlyDictionary<UUID, HomeSeatInfo> infos, ICollection<UUID> freeSeats)
    {
        if (infos == null || !infos.TryGetValue(BathroomSinkId, out var s)) return (UUID.Zero, null, "the sink is not in the seat list");
        var m = WashHandsMenu(s);
        if (m == null) return (UUID.Zero, null, "no Wash Hands button on the sink's solo menu");
        if (freeSeats == null || !freeSeats.Contains(BathroomSinkId)) return (UUID.Zero, null, "the sink is busy or unavailable");
        return (BathroomSinkId, m, null);
    }

    internal static int WashStaySeconds(Random r) => r.Next(WashStayMinS, WashStayMaxS + 1);

    // home wander step, once she stands (dressed again) after the toilet: wash, or log why not and carry on
    static async Task WashHandsIfDue(Graph g, CancellationToken ct)
    {
        var at = toiletSatAt;
        if (at == null || client.Self.SittingOn != 0 || pendingToilet != null) return;
        toiletSatAt = null;
        if (!WashHandsDue(at, DateTime.Now)) { WLog($"wash hands: toilet sit at {at:HH:mm:ss} is too long ago, skipping"); return; }
        var cands = await HomeWanderSeats(g);
        var infos = LoadHomeSeats().GroupBy(i => i.Id).ToDictionary(x => x.Key, x => x.First());
        var (sink, menu, skip) = WashHandsPick(infos, cands.Select(c => c.p.ID).ToHashSet());
        if (sink == UUID.Zero) { WLog($"wash hands: skipped ({skip}); carrying on"); return; }
        WLog($"wash hands after the toilet: sink, menu {string.Join(" > ", menu)}");
        var ok = await HomeRandomSit(g, ct, cands.First(c => c.p.ID == sink), menu, WashStaySeconds(wRnd));
        if (!ok && wanderPause != null) { toiletSatAt = at; WLog("wash hands: interrupted (" + wanderPause + "), will try again after the pause"); }
        else if (ok) wLegsUntilSit = wRnd.Next(1, 4);
    }
}
