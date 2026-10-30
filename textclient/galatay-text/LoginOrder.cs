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
    /// <summary>2026-10-30 (10:19 login): the check ran at +60 s, after the daily outfit swap had replaced the bikini top with the T-shirt (+62 s), and
    /// reported "every garment Ready". Garments first seen after <paramref name="loginWindowSec"/> are not part of the login order (named apart);
    /// the caller also passes garments seen at login and taken off since.</summary>
    internal const double LoginWindowSec = 30;
    /// <param name="veilOffSec">16:24 login: "the base face showed for about 5 s" was logged although the full-avatar login veil hid the base
    /// head until +90 s. With a veil (seconds it came off; double.MaxValue = still on), a head that arrived before then never showed the base face.</param>
    internal static List<string> Findings(IEnumerable<Worn> worn, double loginWindowSec = LoginWindowSec, double? veilOffSec = null)
    {
        var all = (worn ?? Array.Empty<Worn>()).ToList(); var res = new List<string>();
        var later = all.Where(x => IsBodyCuttingGarment(x.Name) && x.SeenSec is double s0 && s0 > loginWindowSec).ToList();
        var w = all.Except(later).ToList();
        var head = w.FirstOrDefault(x => IsRealHead(x.Name));
        var headLine = head == null ? null : head.SeenSec is not double hs ? $"the real head '{head.Name}' was not seen: the base face stayed visible"
            : veilOffSec is double vo && hs <= vo ? $"real head arrived at +{hs:F0} s, under the login veil ({(vo >= 1e9 ? "still on" : $"off at +{vo:F0} s")}): the base face was hidden"
            : veilOffSec is double vo2 && hs - vo2 > 0.5 ? $"real head arrived at +{hs:F0} s, {hs - vo2:F0} s AFTER the veil came off at +{vo2:F0} s: the base face showed for about {hs - vo2:F0} s"
            : veilOffSec != null ? $"real head arrived at +{hs:F0} s"
            : hs > 2 ? $"real head arrived at +{hs:F0} s: the base face showed for about {hs:F0} s" : $"real head arrived at +{hs:F0} s";
        var body = w.FirstOrDefault(x => IsBody(x.Name) && x.SeenSec != null);
        if (body == null) { res.Add("the body was not seen in this login's attachments"); if (headLine != null) res.Add(headLine); return res; }
        double bodyAt = body.SeenSec!.Value; double uncutUntil = bodyAt;
        foreach (var g in w.Where(x => IsBodyCuttingGarment(x.Name) && !LoginVeil.IsBodyAddOn(x.Name)).OrderBy(x => x.SeenSec ?? 1e9))
        {
            if (g.ReadySec is not double r) { res.Add($"'{g.Name}' never announced Ready: its body cut may never have applied"); uncutUntil = double.MaxValue; continue; }
            if (r < bodyAt) { res.Add($"'{g.Name}' was Ready at +{r:F0} s, BEFORE the body arrived at +{bodyAt:F0} s: its cut of the body may not have applied (skin showing under it)"); uncutUntil = double.MaxValue; }
            else uncutUntil = Math.Max(uncutUntil, r);
        }
        res.Insert(0, uncutUntil == double.MaxValue ? $"body arrived at +{bodyAt:F0} s; at least one garment's cut of it is not confirmed"
            : $"body arrived at +{bodyAt:F0} s; every garment was Ready by +{uncutUntil:F0} s (the body showed uncut for up to {uncutUntil - bodyAt:F0} s)");
        if (headLine != null) res.Add(headLine);
        foreach (var l in later) res.Add($"'{l.Name}' put on at +{l.SeenSec:F0} s (outfit change after login): not part of the login order");
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
                LoginOrder.Worn Mk(string n, UUID it) => new(n, loginSeenAt.TryGetValue(it, out var s) ? (s - t0).TotalSeconds : null, loginReadyAt.TryGetValue(n, out var r) ? (r - t0).TotalSeconds : null);
                var worn = roots.Select(p => Mk(p.Properties?.Name ?? "", AttachItemId(p))).ToList();
                // garments seen at login and taken off since (the daily outfit swap): they are what showed at login
                var wornIds = roots.Select(AttachItemId).ToHashSet();
                using var cts = new CancellationTokenSource(10000);
                foreach (var (id, at) in loginSeenAt.ToArray())
                    if (!wornIds.Contains(id) && (at - t0).TotalSeconds <= LoginOrder.LoginWindowSec && await FetchItemRO(id, cts.Token) is InventoryItem gone) worn.Add(Mk(gone.Name, id));
                var vo = loginVeilOffAt; double? veilSec = vo == DateTime.MinValue ? null : vo == DateTime.MaxValue ? double.MaxValue : Math.Max(0, (vo - t0).TotalSeconds);
                Log("login-order", string.Join("; ", LoginOrder.Findings(worn, veilOffSec: veilSec)));
            }
            catch (Exception ex) { Log("login-order", "analysis failed: " + ex.GetBaseException().Message); }
        });
    }
    static void LoginOrderNoteSeen(UUID item) { if (item != UUID.Zero && loginOrderT0 != DateTime.MinValue) loginSeenAt.TryAdd(item, DateTime.Now); }
    static void LoginOrderNoteChat(ChatSourceType src, string from, string msg)
    { if (src == ChatSourceType.Object && loginOrderT0 != DateTime.MinValue && (msg ?? "").TrimStart().StartsWith("Ready", StringComparison.Ordinal)) loginReadyAt.TryAdd(from ?? "", DateTime.Now); }
}
