// LoginOrder.cs (2026-10-28, David: at the 11:42 relog the body showed under the shirt and shorts before the clothes rendered). The log of that login: the sim restores the worn
// attachments in its own order, not ours (boots, shorts, head +4 s, body and T-shirt +8 s); the shorts announced "Ready" at +6 s BEFORE the body had arrived, so the cut it
// asks of the body (the body alpha under it) had nobody to apply to, the T-shirt was Ready at +11 s (seconds since login 11:42:43). The client cannot reorder the sim's rez, so this records the order and names every
// clothing item that was Ready before the body existed (its body alpha may not have applied) and the length of the window in which the body showed uncut.
using System.Collections.Concurrent;
using LibreMetaverse;

namespace GalatayText;

internal static class LoginOrder
{
    internal sealed record Worn(string Name, double? SeenSec, double? ReadySec);

    /// <summary>Pure: the Maitreya LaraX body itself (not its HUD or the petite add-on).</summary>
    internal static bool IsBody(string name) => name != null && name.StartsWith("Maitreya Mesh Body - Lara", StringComparison.OrdinalIgnoreCase)
        && !name.Contains("HUD", StringComparison.OrdinalIgnoreCase) && !name.Contains("Add-on", StringComparison.OrdinalIgnoreCase);

    /// <summary>Pure: the real (Lelutka) head: a "HEAD" attachment that is not the Real Kisser add-on.</summary>
    internal static bool IsRealHead(string name) => name != null && System.Text.RegularExpressions.Regex.IsMatch(name, @"\bHEAD\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
        && !name.Contains("Kisser", StringComparison.OrdinalIgnoreCase);

    /// <summary>Pure: clothing that cuts the body (it announces "Ready" itself): a LaraX-fitted garment, not the body, head, hair, jewelry or HUDs.</summary>
    internal static bool IsBodyCuttingGarment(string name) => name != null && name.Contains("LaraX", StringComparison.OrdinalIgnoreCase) && !IsBody(name)
        && !name.Contains("HUD", StringComparison.OrdinalIgnoreCase) && !name.Contains("Add-on", StringComparison.OrdinalIgnoreCase);

    /// <summary>Pure: what the login order says. Seconds are since login. A garment Ready before the body arrived, a garment never Ready, and the uncut window.</summary>
    internal static List<string> Findings(IEnumerable<Worn> worn)
    {
        var w = (worn ?? Array.Empty<Worn>()).ToList(); var res = new List<string>();
        var head = w.FirstOrDefault(x => IsRealHead(x.Name));
        var headLine = head == null ? null : head.SeenSec is not double hs ? $"the real head '{head.Name}' was not seen: the base face stayed visible"
            : hs > 2 ? $"real head arrived at +{hs:F0} s: the base face showed for about {hs:F0} s" : $"real head arrived at +{hs:F0} s";
        var body = w.FirstOrDefault(x => IsBody(x.Name) && x.SeenSec != null);
        if (body == null) { res.Add("the body was not seen in this login's attachments"); if (headLine != null) res.Add(headLine); return res; }
        double bodyAt = body.SeenSec!.Value; double uncutUntil = bodyAt;
        foreach (var g in w.Where(x => IsBodyCuttingGarment(x.Name)).OrderBy(x => x.SeenSec ?? 1e9))
        {
            if (g.ReadySec is not double r) { res.Add($"'{g.Name}' never announced Ready: its body cut may never have applied"); uncutUntil = double.MaxValue; continue; }
            if (r < bodyAt) { res.Add($"'{g.Name}' was Ready at +{r:F0} s, BEFORE the body arrived at +{bodyAt:F0} s: its cut of the body may not have applied (skin showing under it)"); uncutUntil = double.MaxValue; }
            else uncutUntil = Math.Max(uncutUntil, r);
        }
        res.Insert(0, uncutUntil == double.MaxValue ? $"body arrived at +{bodyAt:F0} s; at least one garment's cut of it is not confirmed"
            : $"body arrived at +{bodyAt:F0} s; every garment was Ready by +{uncutUntil:F0} s (the body showed uncut for up to {uncutUntil - bodyAt:F0} s)");
        if (headLine != null) res.Add(headLine);
        return res;
    }
}

public static partial class Program
{
    static readonly ConcurrentDictionary<UUID, DateTime> loginSeenAt = new();
    static readonly ConcurrentDictionary<string, DateTime> loginReadyAt = new();
    static DateTime loginOrderT0 = DateTime.MinValue;
    static void LoginOrderStart()
    {
        loginSeenAt.Clear(); loginReadyAt.Clear(); loginOrderT0 = DateTime.Now; var t0 = loginOrderT0;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(60000); if (loginOrderT0 != t0) return;
                var roots = WornPrims(); await EnsureProperties(Sim, roots);
                var worn = roots.Select(p => { var n = p.Properties?.Name ?? ""; var it = AttachItemId(p);
                    return new LoginOrder.Worn(n, loginSeenAt.TryGetValue(it, out var s) ? (s - t0).TotalSeconds : null, loginReadyAt.TryGetValue(n, out var r) ? (r - t0).TotalSeconds : null); }).ToList();
                Log("login-order", string.Join("; ", LoginOrder.Findings(worn)));
            }
            catch (Exception ex) { Log("login-order", "analysis failed: " + ex.GetBaseException().Message); }
        });
    }
    static void LoginOrderNoteSeen(UUID item) { if (item != UUID.Zero && loginOrderT0 != DateTime.MinValue) loginSeenAt.TryAdd(item, DateTime.Now); }
    static void LoginOrderNoteChat(ChatSourceType src, string from, string msg)
    { if (src == ChatSourceType.Object && loginOrderT0 != DateTime.MinValue && (msg ?? "").TrimStart().StartsWith("Ready", StringComparison.Ordinal)) loginReadyAt.TryAdd(from ?? "", DateTime.Now); }
}
