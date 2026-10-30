// LoginVeilUsual.cs (2026-10-30, David: "you should dress as usual when logging in", after taking my clothes off in-world).
// The login veil stash only put back what I wore at logout, so a logout undressed (or half dressed) meant a nude login.
// Now, at logout (veil verified on), the clothes in the stash are compared with my usual outfit: the last clothed outfit
// (run/last-clothed-outfit.txt, the same one the redress and the +25 s login fallback use), else the last named outfit.
// When they differ, the stash's clothes are replaced by the usual outfit's clothes (its My Outfits folder links), and the
// Current Outfit links follow (the dropped clothes' links removed, the usual ones added), so the login sends the usual clothes
// at +1 s in the same veil flow: the veil waits for them Ready and for the real head. Head, hair, eyes, teeth, skin: unchanged.
using LibreMetaverse;

namespace GalatayText;

internal static class UsualOutfit
{
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
    /// <summary>Logout (veil on): the plan for the stash against my usual outfit. Bounded inventory reads; any failure keeps the stash as worn.</summary>
    static async Task<UsualOutfit.Plan> LoginVeilUsualPlan(List<LoginVeil.Stashed> stash)
    {
        var name = UsualOutfit.Name(LastClothedOutfit(), lastNamedOutfit);
        if (Env("GT_VEIL_USUAL", "1") == "0" || name == null) return UsualOutfit.For(stash, null, name);
        try
        {
            using var cts = new CancellationTokenSource(12000);
            var mo = await FindMyOutfits(cts.Token);
            var folder = mo == null ? null : (await ReadFolderRO(mo.UUID, cts.Token, 5000)).OfType<InventoryFolder>()
                .FirstOrDefault(f => f.ParentUUID == mo.UUID && f.Name.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
            if (folder == null) { Log("login-veil", $"usual outfit '{name}' not found in My Outfits: stash as worn"); return UsualOutfit.For(stash, null, null); }
            var links = (await ReadFolderRO(folder.UUID, cts.Token, 5000)).OfType<InventoryItem>()
                .Where(l => l.ParentUUID == folder.UUID && (l.InventoryType == InventoryType.Object || l.InventoryType == InventoryType.Attachment))
                .Select(l => (l.IsLink() ? l.AssetUUID : l.UUID, l.Name)).ToList();
            return UsualOutfit.For(stash, links, name);
        }
        catch (Exception ex) { Log("login-veil", "usual outfit read failed (stash as worn): " + ex.GetBaseException().Message); return UsualOutfit.For(stash, null, null); }
    }

    /// <summary>Logout, after the stash detach: the Current Outfit follows the plan (dropped clothes unlinked, usual clothes linked), so the
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
