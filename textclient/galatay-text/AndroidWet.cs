// AndroidWet.cs (2026-10-12, David): while she wears an android body (Kay or CALISTA, BodyFamily from OutfitSafe.cs),
// the home wander never picks the toilet, the bathtub, the (beach/outdoor) shower or the hot tub.
using System.Text.RegularExpressions;

namespace GalatayText;

public static partial class Program
{
    static readonly Regex AndroidWetNameRx = new(@"toilet|bath\s*tub|\btub\b|shower|hot\s*tub|jacuzzi", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // pure: a wet / bathroom spot an android body must skip (seat-list special or name)
    internal static bool AndroidWetSpot(string name, string special) =>
        special is "toilet" or "shower" or "undress" || AndroidWetNameRx.IsMatch(name ?? "");

    // pure: keep a seat unless an android body is worn and the seat is a wet spot
    internal static bool AndroidSeatAllowed(bool android, string name, string special) => !android || !AndroidWetSpot(name, special);

    // 2026-10-26 David: the 09:29 login wore 'Android Base' (Kay) but the worn prims' names were not known 20 s after login,
    // so the login plan was daily-wear and the body was swapped Kay -> LaraX. Decision from three signals:
    //   android = the worn prims say android (BodyFamily) OR the last named outfit (memory / last-named-outfit.txt) starts with 'Android'.
    //   human   = a body prim is known and it is not android (a human body is worn).
    //   wait    = nothing conclusive yet (attachments / properties still loading): retry until the deadline, then human.
    internal static string AndroidLoginDecision(bool primsAndroid, bool anyBodyPrimKnown, IEnumerable<string> lastNamedOutfits, TimeSpan waited, TimeSpan maxWait)
    {
        if (primsAndroid || (lastNamedOutfits ?? Array.Empty<string>()).Any(IsAndroidOutfit)) return "android";
        if (anyBodyPrimKnown) return "human";
        return waited < maxWait ? "wait" : "human";
    }

    static IEnumerable<string> LastNamedOutfitSignals()
    {
        var l = new List<string> { lastNamedOutfit, currentOutfit };
        try { if (File.Exists(LastNamedOutfitFile)) l.Add(File.ReadLines(LastNamedOutfitFile).FirstOrDefault()?.Trim()); } catch { }
        return l;
    }

    // one look: worn prims (names fetched) + the last named outfit
    static async Task<(bool primsAndroid, bool bodyKnown)> WornBodySignals()
    {
        try
        {
            var w = WornPrims(); await EnsureProperties(Sim, w);
            var fams = w.Select(p => BodyFamily(p.Properties?.Name)).Where(f => f != null).ToList();
            return (fams.Any(IsAndroidFamily), fams.Count > 0);
        }
        catch { return (AndroidWorn(), false); }
    }

    // after login the worn prims' names may not be fetched yet (13:07 first deploy: plan "keep", not "keep-android")
    // no waiting (callers away from login, e.g. the beach restore)
    static async Task<bool> AndroidWornSettled()
    {
        var (pa, known) = await WornBodySignals();
        return AndroidLoginDecision(pa, known, LastNamedOutfitSignals(), TimeSpan.Zero, TimeSpan.Zero) == "android";
    }

    // the login plan: retry up to maxWait (60 s) while nothing is conclusive, before a human login's daily-wear can run
    static async Task<bool> AndroidWornAtLogin(TimeSpan? maxWait = null)
    {
        var max = maxWait ?? TimeSpan.FromSeconds(60); var t0 = DateTime.UtcNow; string d;
        while (true)
        {
            var (pa, known) = await WornBodySignals();
            d = AndroidLoginDecision(pa, known, LastNamedOutfitSignals(), DateTime.UtcNow - t0, max);
            if (d != "wait" || !LoggedIn) break;
            await Task.Delay(5000);
        }
        Log("outfit-daily", $"android check at login: {d} (last named '{lastNamedOutfit ?? "-"}', current '{currentOutfit ?? "-"}', waited {(DateTime.UtcNow - t0).TotalSeconds:F0} s)");
        return d == "android";
    }
}
