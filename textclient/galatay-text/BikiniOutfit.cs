// BikiniOutfit.cs (2026-10-05): outfit rename; bikini on/off with Spicy Bikini HUD random texture pick.
// David: rename My Outfits 'Spicy'/'Spicy Bikini' -> 'Bikini'; on wear, attach HUD, pick random [TEXTURE]
// (color D*/W* or pattern T*), detach HUD. Off restores strapless top + jeans. Never touch Martha AO HUD.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    internal static readonly UUID BikiniTopItem = UUID.Parse("d75d9bc6-7954-34c6-a9f4-2d41bb0b034f");
    internal static readonly UUID BikiniPantiesItem = UUID.Parse("d0e00864-8b56-3836-891d-ce7b4e1696e2");
    internal static readonly UUID BikiniHudItem = UUID.Parse("67c881aa-5adf-3f68-9c5c-0adb67e8e484");
    internal static readonly UUID StraplessTopItem = UUID.Parse("aa265665-7a17-34f6-b423-66d336262ed2");
    internal static readonly UUID JeansItem = UUID.Parse("e14414f9-6fff-38a6-abc6-70dce3f249b9");

    // The Spicy Bikini HUD's own spec: [TEXTURE] buttons D*/W* (colors) and T* (patterns); the shipped
    // routes/_clothing-huds.json entry wins, this is the fallback when the map is missing.
    internal static ClothingHudSpec DefaultBikiniHudSpec => new(BikiniHudItem, "<HUD> Spicy Bikini", new() { BikiniTopItem, BikiniPantiesItem }, @"^[DWT]\d+$", null, new());
    internal static ClothingHudSpec BikiniHudSpec(IEnumerable<ClothingHudSpec> specs) => specs?.FirstOrDefault(s => s.Hud == BikiniHudItem) ?? DefaultBikiniHudSpec;

    // Pure: pick among HUD [TEXTURE] buttons (desc D*/W*/T* = color or pattern). Never DETACH / store / social.
    // Same generic path as every other clothing HUD (ClothingHuds.cs).
    internal static (int link, uint local, string label)? BikiniPickTexture(IReadOnlyList<(int link, uint local, string name, string desc)> prims, Random rng, string last = null)
    {
        var p = PickHudOption(HudOptionsFor(prims, DefaultBikiniHudSpec), last, rng);
        return p == null ? null : (p.Link, p.Local, p.Label);
    }

    static async Task<InventoryFolder> FindMyOutfits(CancellationToken ct)
    {
        var root = client.Inventory.Store?.RootFolder; if (root == null) return null;
        var kids = await ReadFolderRO(root.UUID, ct);
        return kids.OfType<InventoryFolder>().FirstOrDefault(f => f.PreferredType == FolderType.MyOutfits && f.ParentUUID == root.UUID);
    }

    static async Task<InventoryFolder> FindOutfitFolder(string name, CancellationToken ct)
    {
        var mo = await FindMyOutfits(ct); if (mo == null) return null;
        var kids = (await ReadFolderRO(mo.UUID, ct)).OfType<InventoryFolder>().Where(f => f.ParentUUID == mo.UUID).ToList();
        return kids.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? kids.FirstOrDefault(f => f.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    // outfit rename <old> <new> — UpdateFolderProperties (reversible: rename back)
    static async Task<string> OutfitRename(string oldName, string newName)
    {
        if (!LoggedIn) return "not logged in";
        oldName = (oldName ?? "").Trim().Trim('"'); newName = (newName ?? "").Trim().Trim('"');
        if (oldName.Length == 0 || newName.Length == 0) return "usage: outfit rename <old> <new>";
        if (!Regex.IsMatch(newName, @"^[A-Za-z0-9][A-Za-z0-9 _'.-]{0,62}$")) return $"refused: new name '{newName}' looks unsafe";
        using var cts = new CancellationTokenSource(30000); var ct = cts.Token;
        var mo = await FindMyOutfits(ct); if (mo == null) return "My Outfits folder not found";
        var kids = (await ReadFolderRO(mo.UUID, ct)).OfType<InventoryFolder>().Where(f => f.ParentUUID == mo.UUID).ToList();
        var src = kids.FirstOrDefault(f => f.Name.Equals(oldName, StringComparison.OrdinalIgnoreCase));
        // David said 'Spicy Bikini' but the folder may be saved as 'Spicy'
        if (src == null && oldName.Equals("Spicy Bikini", StringComparison.OrdinalIgnoreCase))
            src = kids.FirstOrDefault(f => f.Name.Equals("Spicy", StringComparison.OrdinalIgnoreCase));
        if (src == null) return $"no outfit folder '{oldName}' under My Outfits (have: {string.Join(", ", kids.Select(k => $"'{k.Name}'"))})";
        if (kids.Any(k => k.UUID != src.UUID && k.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
            return $"ABORT: My Outfits already has '{newName}'";
        var prev = src.Name;
        client.Inventory.UpdateFolderProperties(src.UUID, src.ParentUUID, newName, src.PreferredType);
        Log("outfit", $"renamed My Outfits '{prev}' -> '{newName}' ({src.UUID})");
        await Task.Delay(800, ct);
        var again = (await ReadFolderRO(mo.UUID, ct)).OfType<InventoryFolder>().FirstOrDefault(f => f.UUID == src.UUID);
        return again != null && again.Name == newName
            ? $"renamed outfit '{prev}' -> '{newName}' ({src.UUID}); undo: outfit rename \"{newName}\" \"{prev}\""
            : $"rename sent for '{prev}' -> '{newName}' ({src.UUID}); folder now shows '{again?.Name ?? "?"}'; undo: outfit rename \"{newName}\" \"{prev}\"";
    }

    static async Task<string> OutfitWearNamed(string name, bool replace)
    {
        if (!LoggedIn) return "not logged in";
        using var cts = new CancellationTokenSource(150000); var ct = cts.Token;
        var folder = await FindOutfitFolder(name, ct);
        if (folder == null) return $"no outfit '{name}' under My Outfits";
        if (!replace)
        {
            var items = await ResolveOutfitItems(folder, ct);
            client.Appearance.AddToOutfit(items.Where(i => OutfitGroup(i.Name) == null && i.UUID != RetiredAwpAo).ToList(), false);
            return $"added outfit '{folder.Name}' pieces (no hair/head/body; add mode)";
        }
        return await OutfitWearSafe(folder, ct); // never stacks hair/head/body (OutfitSafe.cs)
    }

    static async Task<string> BikiniHudRandomize(CancellationToken ct)
    {
        var hud = await FetchItemRO(BikiniHudItem, ct);
        if (hud == null) return "Spicy Bikini HUD item not found";
        var spec = BikiniHudSpec(LoadClothingHudSpecs());
        return await HudRandomize(hud, spec.Clothing, ct, spec); // OutfitSafe.cs: random among all colors/patterns (not the last one), verify, detach
    }

    static async Task<string> BikiniOn()
    {
        if (!LoggedIn) return "not logged in";
        using var cts = new CancellationTokenSource(120000); var ct = cts.Token;
        var sb = new StringBuilder();
        // Prefer full My Outfits Bikini (or Spicy pre-rename); else clothing swap. Then HUD random among all D/W/T.
        var folder = await FindOutfitFolder("Bikini", ct) ?? await FindOutfitFolder("Spicy", ct);
        if (folder != null)
        {
            sb.AppendLine(await OutfitWearNamed(folder.Name, replace: true));
            await Task.Delay(3000, ct);
        }
        else
        {
            sb.AppendLine(await WearOpsCmd("wear", new[] { "remove", StraplessTopItem.ToString() }));
            sb.AppendLine(await WearOpsCmd("wear", new[] { "remove", JeansItem.ToString() }));
            sb.AppendLine(await WearOpsCmd("wear", new[] { "add", BikiniTopItem.ToString() }));
            sb.AppendLine(await WearOpsCmd("wear", new[] { "add", BikiniPantiesItem.ToString() }));
            await Task.Delay(2000, ct);
        }
        sb.AppendLine(await BikiniHudRandomize(ct));
        RememberNamedOutfit("Bikini");
        return sb.ToString().TrimEnd();
    }

    static async Task<string> BikiniOff()
    {
        if (!LoggedIn) return "not logged in";
        var sb = new StringBuilder();
        // Remove HUD if somehow still on
        if (WornPrims().Any(p => AttachItemId(p) == BikiniHudItem))
            sb.AppendLine(await WearOpsCmd("wear", new[] { "remove", BikiniHudItem.ToString() }));
        sb.AppendLine(await WearOpsCmd("wear", new[] { "remove", BikiniTopItem.ToString() }));
        sb.AppendLine(await WearOpsCmd("wear", new[] { "remove", BikiniPantiesItem.ToString() }));
        sb.AppendLine(await WearOpsCmd("wear", new[] { "add", StraplessTopItem.ToString() }));
        sb.AppendLine(await WearOpsCmd("wear", new[] { "add", JeansItem.ToString() }));
        Log("bikini", "bikini off -> strapless + jeans");
        return sb.ToString().TrimEnd();
    }

    internal static string BikiniSelfTest()
    {
        var sb = new StringBuilder(); int pass = 0, fail = 0;
        void C(bool ok, string w) { if (ok) pass++; else fail++; sb.AppendLine($"{(ok ? "PASS" : "FAIL")} {w}"); }
        var prims = new List<(int, uint, string, string)>
        {
            (1, 1, "<HUD> Spicy Bikini", ""),
            (2, 2, "[DETACH]", ""),
            (3, 3, "[TEXTURE]", "D6"),
            (4, 4, "[TEXTURE]", "W1"),
            (5, 5, "[TEXTURE]", "T9"),
            (6, 6, "Instagram", ""),
            (7, 7, "[TEXTURE]", "D38"),
        };
        var seen = new HashSet<string>();
        for (int i = 0; i < 40; i++)
        {
            var p = BikiniPickTexture(prims, new Random(i + 7));
            C(p != null, "pick non-null");
            if (p != null) seen.Add(p.Value.label);
        }
        C(seen.SetEquals(new[] { "D6", "W1", "T9", "D38" }), $"random among all D/W/T labels (got {string.Join(",", seen.OrderBy(x => x))})");
        C(BikiniPickTexture(prims.Where(p => p.Item3 != "[TEXTURE]").ToList(), Random.Shared) == null, "no textures -> null");
        var notLast = new HashSet<string>();
        for (int i = 0; i < 40; i++) { var p = BikiniPickTexture(prims, new Random(i), "D6"); if (p != null) notLast.Add(p.Value.label); }
        C(!notLast.Contains("D6") && notLast.Count == 3, "never the previous pick (D6) again");
        var withC = prims.Append((8, 8u, "[TEXTURE]", "C3")).ToList();
        C(Enumerable.Range(0, 40).All(i => BikiniPickTexture(withC, new Random(i))?.label != "C3"), "Bikini HUD ignores non-D/W/T codes");
        C(BikiniTopItem != UUID.Zero && BikiniHudItem != UUID.Zero && StraplessTopItem != UUID.Zero, "item UUIDs set");
        return $"bikini selftest: {pass} PASS, {fail} FAIL\n" + sb.ToString().TrimEnd();
    }
}
