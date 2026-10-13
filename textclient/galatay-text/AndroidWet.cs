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

    // after login the worn prims' names may not be fetched yet (13:07 first deploy: plan "keep", not "keep-android")
    static async Task<bool> AndroidWornSettled()
    {
        try { var w = WornPrims(); await EnsureProperties(Sim, w); return w.Any(p => IsAndroidFamily(BodyFamily(p.Properties?.Name))); }
        catch { return AndroidWorn(); }
    }
}
