// LoginVeilUsual.cs (2026-10-30, David: "you should dress as usual when logging in", after taking my clothes off in-world).
// The login veil stash only put back what I wore at logout, so a logout undressed (or half dressed) meant a nude login.
// Now, at logout (veil verified on), the clothes in the stash are compared with my usual outfit: the last clothed outfit
// (run/last-clothed-outfit.txt, the same one the redress and the +25 s login fallback use), else the last named outfit.
// When they differ, the stash's clothes are replaced by the usual outfit's clothes (its My Outfits folder links), and the
// Current Outfit links follow (the dropped clothes' links removed, the usual ones added), so the login sends the usual clothes
// at +1 s in the same veil flow: the veil waits for them Ready and for the real head. Head, hair, eyes, teeth, skin: unchanged.
using LibreMetaverse;

namespace GalatayText;

// 2026-10-30 16:37 (David's rule, replaces #357's always-swap): logging back in within 10 minutes of the logout, I come back in exactly
// what I logged out in, even naked; only after more than 10 minutes do I log in in my usual outfit. The decision is made at LOGIN:
// the logout keeps the as-worn stash and writes the usual outfit's clothes list beside it (run/login-veil-usual.txt); the COF is not
// rewritten at logout. GT_VEIL_USUAL_AFTER_MIN (default 10) is the threshold.
internal static class UsualOutfit
{
    internal const double DefaultAfterMin = 10;
    // 2026-10-31 (David, t2850u): the next outfit pick goes on any time I've been logged out more than an hour (was: once per PT day).
    internal const double DefaultChangeAfterMin = 60;

    /// <summary>Pure (tested): does this login change to the next outfit pick? Yes past the hour, or when the one-shot force marker was set.</summary>
    internal static bool OutfitChangeDue(TimeSpan sinceLogout, bool forced, double changeAfterMin = DefaultChangeAfterMin)
        => forced || sinceLogout > TimeSpan.FromMinutes(changeAfterMin);

    /// <summary>Pure (tested): the stash to put on at login. Within the threshold: as worn (no swap). Past it: the usual outfit's plan.</summary>
    internal static Plan AtLogin(List<LoginVeil.Stashed> asWorn, IEnumerable<(UUID Item, string Name)> usualLinks, string usualName, TimeSpan sinceLogout, double afterMin = DefaultAfterMin, bool forced = false)
    {
        var s = asWorn ?? new();
        if (!forced && sinceLogout < TimeSpan.FromMinutes(afterMin))
            return new(s, new(), new(), false, $"back within {afterMin:F0} min of the logout ({sinceLogout.TotalMinutes:F1} min): exactly as I logged out");
        var p = For(s, usualLinks, usualName);
        return p with { Why = $"{sinceLogout.TotalMinutes:F0} min since the logout (> {afterMin:F0}): " + p.Why };
    }

    // 2026-10-31 12:52 login (David: wrong outfit then a swap ~30 s later, feet wrong for ~90 s): past the threshold, when today's daily outfit
    // is still pending, the login wears the daily pick (pre-chosen at logout, its links saved) instead of the usual outfit, so there is no swap.
    /// <summary>Pure (tested): which outfit the login veil should put on past the threshold: the daily pre-pick when the daily is pending and it
    /// has clothes; else the usual outfit. Within the threshold: the usual (AtLogin then keeps the stash as worn anyway).</summary>
    internal static (string Name, List<(UUID Item, string Name)> Links, bool Daily) LoginTarget(TimeSpan sinceLogout, bool dailyPending,
        string usualName, List<(UUID Item, string Name)> usualLinks, string dailyName, List<(UUID Item, string Name)> dailyLinks, double afterMin = DefaultAfterMin, bool forced = false)
    {
        usualLinks ??= new();
        if ((!forced && sinceLogout < TimeSpan.FromMinutes(afterMin)) || !dailyPending || string.IsNullOrWhiteSpace(dailyName)
            || dailyLinks == null || !dailyLinks.Any(x => x.Item != UUID.Zero && IsClothes(x.Name)))
            return (usualName, usualLinks, false);
        return (dailyName, dailyLinks, true);
    }

    internal static string FormatUsual(string name, IEnumerable<(UUID Item, string Name)> links)
        => $"# usual {(name ?? "").Replace('\n', ' ')}\n" + string.Concat((links ?? Enumerable.Empty<(UUID, string)>()).Select(x => $"{x.Item}\t{(x.Name ?? "").Replace('\t', ' ').Replace('\n', ' ')}\n"));
    internal static (string name, List<(UUID Item, string Name)> links) ParseUsual(string text)
    {
        string name = null; var l = new List<(UUID, string)>();
        foreach (var line in (text ?? "").Split('\n').Select(x => x.TrimEnd('\r')))
        {
            if (line.StartsWith("# usual ")) { name = line.Substring(8).Trim(); continue; }
            var f = line.Split('\t'); if (f.Length >= 2 && UUID.TryParse(f[0], out var id) && id != UUID.Zero) l.Add((id, f[1]));
        }
        return (string.IsNullOrWhiteSpace(name) ? null : name, l);
    }

    internal sealed record Plan(List<LoginVeil.Stashed> Stash, List<LoginVeil.Stashed> Add, List<UUID> Drop, bool Changed, string Why);

    /// <summary>Pure (tested): my usual outfit's name: the last clothed outfit, else the last named one if it is a clothed outfit.</summary>
    internal static string Name(string lastClothed, string lastNamed)
        => new[] { lastClothed, lastNamed }.FirstOrDefault(Program.IsClothedOutfitName)?.Trim();

    /// <summary>Pure (tested): a stashed clothing attachment (put back at login with the clothes), not the head or a head part.</summary>
    internal static bool IsClothes(string name)
        => LoginVeil.ShouldStash(name, 1) && !LoginVeil.RevealWithVeil(name) && !LoginOrder.IsRealHead(name) && !LoginVeil.IsHeadPart(name);

    /// <summary>Pure (tested): the stash to write at logout. Same clothes as the usual outfit (or usual unknown): unchanged. Else the stash's
    /// clothes become the usual outfit's clothes; everything else in the stash (hair, teeth, eyes, earrings...) stays as it was.</summary>
    internal static Plan For(IEnumerable<LoginVeil.Stashed> stash, IEnumerable<(UUID Item, string Name)> usualLinks, string usualName)
    {
        var s = (stash ?? Enumerable.Empty<LoginVeil.Stashed>()).ToList();
        var usual = (usualLinks ?? Enumerable.Empty<(UUID Item, string Name)>()).Where(x => x.Item != UUID.Zero && IsClothes(x.Name)).GroupBy(x => x.Item).Select(g => g.First()).ToList();
        if (string.IsNullOrWhiteSpace(usualName) || usual.Count == 0) return new(s, new(), new(), false, $"usual outfit '{usualName ?? "-"}' unknown or without clothes: stash as worn");
        var worn = s.Where(x => IsClothes(x.Name)).ToList();
        var u = usual.Select(x => x.Item).ToHashSet();
        if (worn.Select(x => x.Item).ToHashSet().SetEquals(u)) return new(s, new(), new(), false, $"wearing my usual outfit '{usualName}'");
        var keepPoint = worn.ToDictionary(x => x.Item, x => x.Point);
        var clothes = usual.Select(x => new LoginVeil.Stashed(x.Item, keepPoint.TryGetValue(x.Item, out var p) ? p : 0, x.Name)).ToList();
        var add = clothes.Where(x => !keepPoint.ContainsKey(x.Item)).ToList();
        var drop = worn.Where(x => !u.Contains(x.Item)).Select(x => x.Item).ToList();
        var outStash = s.Where(x => !IsClothes(x.Name)).Concat(clothes).ToList();
        return new(outStash, add, drop, true, $"clothes differ from my usual outfit '{usualName}': login wears its {clothes.Count} clothes"
            + (add.Count > 0 ? $" (adds {string.Join("; ", add.Select(x => x.Name))})" : "") + (drop.Count > 0 ? $", {drop.Count} not put back" : ""));
    }
}

public static partial class Program
{
    static string LoginVeilUsualFile => Env("GT_VEIL_USUAL_FILE", "/home/box/viewers/textclient/run/login-veil-usual.txt");
    static double LoginVeilUsualAfterMin => double.TryParse(Env("GT_VEIL_USUAL_AFTER_MIN", ""), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m) && m >= 0 ? m : UsualOutfit.DefaultAfterMin;

    /// <summary>Logout (veil on): write my usual outfit's name and links beside the stash (bounded reads); nothing is changed now.</summary>
    static string LoginVeilDailyFile => Env("GT_VEIL_DAILY_FILE", "/home/box/viewers/textclient/run/login-veil-daily.txt");
    internal static string loginDailyPick;   // the daily outfit the login veil put on (DailyOutfitAfterLogin uses it as the day's pick: no second, random swap)

    /// <summary>Logout: pre-pick the next daily outfit (same pool rule as the login picker) and save its links, so a login after more than an hour away
    /// dresses in it from the start. Not on the beach (the bikini rule decides there) and not while undressed.</summary>
    static async Task LoginVeilDailySave(InventoryFolder mo, string current, CancellationToken ct)
    {
        try
        {
            if (Env("GT_VEIL_DAILY", "1") == "0" || beachMode || !AutoOutfitAllowed(wardrobeUndressed, wardrobeUndressedAt, DateTime.Now)) return;
            var folders = (await ReadFolderRO(mo.UUID, ct, 5000)).OfType<InventoryFolder>().Where(f => f.ParentUUID == mo.UUID).ToList();
            var pool = DailyOutfitPool(DailyOutfitCandidates(folders.Select(f => f.Name), DailyOutfitAllow()), lastNamedOutfit ?? current);
            if (pool.Count == 0) return;
            var pick = pool[Random.Shared.Next(pool.Count)];
            var folder = folders.First(f => f.Name.Trim().Equals(pick, StringComparison.OrdinalIgnoreCase));
            var links = (await ReadFolderRO(folder.UUID, ct, 5000)).OfType<InventoryItem>()
                .Where(l => l.ParentUUID == folder.UUID && (l.InventoryType == InventoryType.Object || l.InventoryType == InventoryType.Attachment))
                .Select(l => (l.IsLink() ? l.AssetUUID : l.UUID, l.Name)).ToList();
            File.WriteAllText(LoginVeilDailyFile, UsualOutfit.FormatUsual(pick, links));
            Log("login-veil", $"next daily outfit '{pick}' pre-picked for login ({links.Count} links; worn at login when I'm away more than {LoginVeilOutfitChangeAfterMin:F0} min or the force marker is set)");
        }
        catch (Exception ex) { Log("login-veil", "daily pre-pick failed (login wears the usual outfit): " + ex.GetBaseException().Message); }
    }

    static double LoginVeilOutfitChangeAfterMin => double.TryParse(Env("GT_OUTFIT_CHANGE_AFTER_MIN", ""), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m) && m >= 0 ? m : UsualOutfit.DefaultChangeAfterMin;
    // One-shot (David t2850u test): `touch` this file and the next login changes to the next pick however short the time away. Consumed at login.
    internal static string ForceOutfitChangeFile => Env("GT_FORCE_OUTFIT_CHANGE", "/home/box/viewers/textclient/run/force-outfit-change");
    static int forceOutfitConsumed;   // per process: the marker is read once
    static bool forceOutfitThisLogin;
    /// <summary>Set by the login veil: null = not decided yet (no veil stash), else whether this login changes to the next pick.</summary>
    internal static bool? loginOutfitChangeDue;

    static bool ConsumeForceOutfitMarker()
    {
        if (Interlocked.Exchange(ref forceOutfitConsumed, 1) != 0) return forceOutfitThisLogin;
        try
        {
            if (File.Exists(ForceOutfitChangeFile))
            {
                File.Delete(ForceOutfitChangeFile); forceOutfitThisLogin = true;
                Log("login-veil", $"force-outfit-change marker found and consumed ({ForceOutfitChangeFile}): this login changes to the next pick");
            }
        }
        catch (Exception ex) { Log("login-veil", "force-outfit-change marker read failed: " + ex.GetBaseException().Message); }
        return forceOutfitThisLogin;
    }

    static async Task LoginVeilUsualSave()
    {
        try { File.Delete(LoginVeilUsualFile); } catch { }
        try { File.Delete(LoginVeilDailyFile); } catch { }
        var name = UsualOutfit.Name(LastClothedOutfit(), lastNamedOutfit);
        if (Env("GT_VEIL_USUAL", "1") == "0" || name == null) { Log("login-veil", $"usual outfit '{name ?? "-"}' unknown: login wears the stash as worn"); return; }
        try
        {
            using var cts = new CancellationTokenSource(12000);
            var mo = await FindMyOutfits(cts.Token);
            var folder = mo == null ? null : (await ReadFolderRO(mo.UUID, cts.Token, 5000)).OfType<InventoryFolder>()
                .FirstOrDefault(f => f.ParentUUID == mo.UUID && f.Name.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
            if (folder == null) { Log("login-veil", $"usual outfit '{name}' not found in My Outfits"); return; }
            var links = (await ReadFolderRO(folder.UUID, cts.Token, 5000)).OfType<InventoryItem>()
                .Where(l => l.ParentUUID == folder.UUID && (l.InventoryType == InventoryType.Object || l.InventoryType == InventoryType.Attachment))
                .Select(l => (l.IsLink() ? l.AssetUUID : l.UUID, l.Name)).ToList();
            File.WriteAllText(LoginVeilUsualFile, UsualOutfit.FormatUsual(name, links));
            Log("login-veil", $"usual outfit '{name}' saved for login ({links.Count} links; used only if I'm away more than {LoginVeilUsualAfterMin:F0} min)");
            await LoginVeilDailySave(mo, name, cts.Token);
        }
        catch (Exception ex) { Log("login-veil", "usual outfit read failed (login wears the stash as worn): " + ex.GetBaseException().Message); }
    }

    /// <summary>Login: as worn, or (away past the threshold) my usual outfit. Never throws; any failure = as worn.</summary>
    static UsualOutfit.Plan LoginVeilUsualAtLogin(List<LoginVeil.Stashed> stash, DateTime loggedOutAt)
    {
        try
        {
            var (name, links) = File.Exists(LoginVeilUsualFile) ? UsualOutfit.ParseUsual(File.ReadAllText(LoginVeilUsualFile)) : (null, new());
            var (dName, dLinks) = File.Exists(LoginVeilDailyFile) ? UsualOutfit.ParseUsual(File.ReadAllText(LoginVeilDailyFile)) : (null, new());
            try { File.Delete(LoginVeilUsualFile); } catch { }
            try { File.Delete(LoginVeilDailyFile); } catch { }
            var since = DateTime.Now - loggedOutAt;
            var forced = ConsumeForceOutfitMarker();
            var due = UsualOutfit.OutfitChangeDue(since, forced, LoginVeilOutfitChangeAfterMin);
            loginOutfitChangeDue = due;
            Log("login-veil", $"outfit change {(due ? "DUE" : "not due")}: {since.TotalMinutes:F1} min away (change after {LoginVeilOutfitChangeAfterMin:F0}){(forced ? ", forced by marker" : "")}");
            var t = UsualOutfit.LoginTarget(since, due, name, links, dName, dLinks, LoginVeilUsualAfterMin, forced);
            var plan = UsualOutfit.AtLogin(stash, t.Links, t.Name, since, LoginVeilUsualAfterMin, forced && t.Daily);
            if (t.Daily) { loginDailyPick = t.Name; plan = plan with { Why = plan.Why.Replace("my usual outfit", "the next outfit pick") }; }
            return plan;
        }
        catch (Exception ex) { return new(stash, new(), new(), false, "usual outfit check failed (as worn): " + ex.GetBaseException().Message); }
    }

    /// <summary>Login, usual outfit chosen: the Current Outfit follows the plan (dropped clothes unlinked, usual clothes linked), so the
    /// login's add-missing pass and the veil's COF wait both see my usual outfit.</summary>
    static async Task LoginVeilUsualCof(UsualOutfit.Plan plan)
    {
        if (!plan.Changed) return;
        try
        {
            using var cts = new CancellationTokenSource(15000);
            if (plan.Drop.Count > 0) Log("login-veil", "usual outfit: " + await CofLinksOffBatch(plan.Drop, "usual outfit at login", cts.Token));
            if (plan.Add.Count > 0)
            {
                var items = new List<InventoryItem>();
                foreach (var x in plan.Add) items.Add(await FetchItemRO(x.Item, cts.Token) is InventoryItem it && !it.IsLink() ? it
                    : new InventoryObject(x.Item) { Name = x.Name ?? "", OwnerID = client.Self.AgentID, InventoryType = InventoryType.Object });
                Log("login-veil", "usual outfit: " + await CofLinksOnBatch(items, null, "usual outfit at login", cts.Token));
            }
        }
        catch (Exception ex) { Log("login-veil", "usual outfit COF update failed (the stash still sends the clothes): " + ex.GetBaseException().Message); }
    }
}
