// OutfitZones.cs (2026-10-05): Peronaut beach ↔ house outfit swap; once-per-PT-day random non-Bikini outfit on login;
// trash saved outfit folders (MoveFolder → Trash). David: remember current outfit before beach Bikini+HUD;
// restore on return upstairs; daily pick among saved outfits excluding Bikini.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    static readonly string DailyOutfitDateFile = Env("GT_DAILY_OUTFIT_DATE", "/home/box/viewers/textclient/run/daily-outfit-date.txt");
    static readonly string LastNamedOutfitFile = Env("GT_LAST_OUTFIT", "/home/box/viewers/textclient/run/last-named-outfit.txt");
    static readonly string BeachOutfitStateFile = Env("GT_BEACH_OUTFIT_STATE", "/home/box/viewers/textclient/run/beach-outfit-state.txt");

    // Peronaut home: beach ~z20–23, mid porch ~25.5, upper ~27–30. Hysteresis avoids thrash on stairs.
    const float BeachEnterZ = 23.5f;
    const float BeachLeaveZ = 26.5f;
    static readonly Regex BikiniNameRx = new(@"^(Bikini|Spicy)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly string[] TrashOutfitNames = { "Original", "Avatar Welcome Pack", "monk", "new monk", "new monk legacy" };

    static volatile bool beachOutfitBusy;
    static volatile bool beachMode; // true while we consider her on-beach (bikini rule active)
    static string beachRememberedOutfit; // saved folder name before bikini
    static string lastNamedOutfit; // last worn named My Outfits folder (non-transient)
    static DateTime beachTickAt = DateTime.MinValue;
    static int dailyOutfitKick; // 0=pending after login, 1=running/done this process

    static void OutfitZonesLoad()
    {
        try { if (File.Exists(LastNamedOutfitFile)) lastNamedOutfit = File.ReadAllText(LastNamedOutfitFile).Trim(); } catch { }
        try
        {
            if (File.Exists(BeachOutfitStateFile))
            {
                var lines = File.ReadAllLines(BeachOutfitStateFile);
                beachMode = lines.Length > 0 && lines[0].Trim().Equals("beach", StringComparison.OrdinalIgnoreCase);
                beachRememberedOutfit = lines.Length > 1 ? lines[1].Trim() : null;
            }
        }
        catch { }
    }

    static void RememberNamedOutfit(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        lastNamedOutfit = name.Trim();
        try { Directory.CreateDirectory(Path.GetDirectoryName(LastNamedOutfitFile)!); File.WriteAllText(LastNamedOutfitFile, lastNamedOutfit + "\n"); } catch { }
    }

    static void PersistBeachState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BeachOutfitStateFile)!);
            File.WriteAllText(BeachOutfitStateFile, (beachMode ? "beach" : "house") + "\n" + (beachRememberedOutfit ?? "") + "\n");
        }
        catch { }
    }

    // Pure: beach / house / mid for Peronaut Z. Other regions → null (no auto swap).
    internal static string OutfitZoneFor(string region, float z, bool currentlyBeach)
    {
        if (!string.Equals(region, "Peronaut", StringComparison.OrdinalIgnoreCase)) return null;
        if (currentlyBeach) return z >= BeachLeaveZ ? "house" : "beach";
        return z <= BeachEnterZ ? "beach" : (z >= BeachLeaveZ ? "house" : "mid");
    }

    internal static List<string> DailyOutfitCandidates(IEnumerable<string> folderNames) =>
        folderNames.Where(n => !string.IsNullOrWhiteSpace(n) && !BikiniNameRx.IsMatch(n.Trim())).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    static async Task<List<InventoryFolder>> ListOutfitFolders(CancellationToken ct)
    {
        var mo = await FindMyOutfits(ct); if (mo == null) return new();
        return (await ReadFolderRO(mo.UUID, ct)).OfType<InventoryFolder>().Where(f => f.ParentUUID == mo.UUID).OrderBy(f => f.Name).ToList();
    }

    static async Task OutfitZoneTick()
    {
        if (!LoggedIn || beachOutfitBusy) return;
        var now = DateTime.UtcNow;
        if ((now - beachTickAt).TotalSeconds < 2.5) return;
        beachTickAt = now;
        var sim = client.Network.CurrentSim; if (sim == null) return;
        var z = client.Self.SimPosition.Z;
        var zone = OutfitZoneFor(sim.Name, z, beachMode);
        if (zone == null || zone == "mid") return;
        if (zone == "beach" && beachMode) return;
        if (zone == "house" && !beachMode) return;
        beachOutfitBusy = true;
        try
        {
            if (zone == "beach")
            {
                var remember = lastNamedOutfit;
                if (string.IsNullOrWhiteSpace(remember) || BikiniNameRx.IsMatch(remember))
                    remember = beachRememberedOutfit;
                if (string.IsNullOrWhiteSpace(remember) || BikiniNameRx.IsMatch(remember))
                {
                    var folders = await ListOutfitFolders(CancellationToken.None);
                    remember = DailyOutfitCandidates(folders.Select(f => f.Name)).FirstOrDefault() ?? "tubetop";
                }
                beachRememberedOutfit = remember;
                beachMode = true; PersistBeachState();
                Log("outfit-zone", $"entering beach (z={z:F1}): remember '{remember}', wearing Bikini + HUD random");
                var r = await BikiniOn();
                RememberNamedOutfit("Bikini");
                Log("outfit-zone", "beach wear done: " + r.Replace("\n", " | ")[..Math.Min(300, r.Length)]);
            }
            else // house
            {
                var restore = beachRememberedOutfit;
                beachMode = false; PersistBeachState();
                if (string.IsNullOrWhiteSpace(restore) || BikiniNameRx.IsMatch(restore))
                {
                    Log("outfit-zone", $"leaving beach (z={z:F1}): no remembered outfit → bikini off (strapless+jeans)");
                    var r = await BikiniOff();
                    Log("outfit-zone", "house restore: " + r.Replace("\n", " | ")[..Math.Min(300, r.Length)]);
                }
                else
                {
                    Log("outfit-zone", $"leaving beach (z={z:F1}): restoring outfit '{restore}'");
                    var r = await OutfitWearNamed(restore, replace: true);
                    RememberNamedOutfit(restore);
                    // Detach bikini HUD if still on
                    if (WornPrims().Any(p => AttachItemId(p) == BikiniHudItem))
                        await WearOpsCmd("wear", new[] { "remove", BikiniHudItem.ToString() });
                    Log("outfit-zone", "house restore: " + r);
                }
                beachRememberedOutfit = null; PersistBeachState();
            }
        }
        catch (Exception ex) { Log("outfit-zone", "tick error: " + ex.GetBaseException().Message); }
        finally { beachOutfitBusy = false; }
    }

    static async Task DailyOutfitAfterLogin()
    {
        if (Interlocked.Exchange(ref dailyOutfitKick, 1) != 0) return;
        try
        {
            await Task.Delay(25000); // let appearance / COF settle
            if (!LoggedIn) return;
            OutfitZonesLoad();
            var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
                TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles")).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string prev = null;
            try { if (File.Exists(DailyOutfitDateFile)) prev = File.ReadAllText(DailyOutfitDateFile).Trim(); } catch { }
            if (prev == today) { Log("outfit-daily", $"already picked for {today} PT; skip"); return; }
            using var cts = new CancellationTokenSource(90000);
            var folders = await ListOutfitFolders(cts.Token);
            var cands = DailyOutfitCandidates(folders.Select(f => f.Name));
            if (cands.Count == 0) { Log("outfit-daily", "no non-Bikini outfits to pick"); return; }
            var pick = cands[Random.Shared.Next(cands.Count)];
            Log("outfit-daily", $"first login of {today} PT: wearing '{pick}' among {cands.Count}: {string.Join(", ", cands)}");
            var r = await OutfitWearNamed(pick, replace: true);
            RememberNamedOutfit(pick);
            try { Directory.CreateDirectory(Path.GetDirectoryName(DailyOutfitDateFile)!); File.WriteAllText(DailyOutfitDateFile, today + "\n" + pick + "\n"); } catch { }
            Log("outfit-daily", r);
        }
        catch (Exception ex) { Log("outfit-daily", "error: " + ex.GetBaseException().Message); }
    }

    // Move My Outfits folders to Trash (reversible from inventory Trash). Does not purge items.
    static async Task<string> OutfitTrashNamed(IEnumerable<string> names)
    {
        if (!LoggedIn) return "not logged in";
        using var cts = new CancellationTokenSource(60000); var ct = cts.Token;
        var mo = await FindMyOutfits(ct); if (mo == null) return "My Outfits folder not found";
        var trash = client.Inventory.FindFolderForType(FolderType.Trash);
        if (trash == UUID.Zero) return "Trash folder not found";
        var kids = (await ReadFolderRO(mo.UUID, ct)).OfType<InventoryFolder>().Where(f => f.ParentUUID == mo.UUID).ToList();
        var want = names.Select(n => n.Trim()).Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        var moved = new List<string>();
        foreach (var f in kids.Where(f => want.Contains(f.Name)).ToList())
        {
            client.Inventory.MoveFolder(f.UUID, trash);
            moved.Add($"'{f.Name}' {f.UUID}");
            Log("outfit", $"moved outfit folder '{f.Name}' {f.UUID} → Trash");
            sb.AppendLine($"trashed outfit folder '{f.Name}' ({f.UUID})");
        }
        foreach (var n in want.Where(n => !moved.Any(m => m.StartsWith($"'{n}'", StringComparison.OrdinalIgnoreCase))))
            sb.AppendLine($"not found under My Outfits: '{n}'");
        await Task.Delay(1000, ct);
        var left = (await ReadFolderRO(mo.UUID, ct)).OfType<InventoryFolder>().Where(f => f.ParentUUID == mo.UUID).OrderBy(f => f.Name).Select(f => f.Name).ToList();
        sb.AppendLine($"remaining My Outfits ({left.Count}): {string.Join(", ", left.Select(n => $"'{n}'"))}");
        return sb.ToString().TrimEnd();
    }

    static async Task<string> OutfitZonesCmd(string[] a)
    {
        if (a.Length == 0) return "usage: outfit zone status|selftest | outfit trash monk|original|… | outfit daily status";
        if (a[0] == "zone" && a.Length > 1 && a[1] == "status")
        {
            var sim = client?.Network?.CurrentSim?.Name ?? "?";
            var z = LoggedIn ? client.Self.SimPosition.Z : 0;
            return $"region={sim} z={z:F1} zone={OutfitZoneFor(sim, z, beachMode) ?? "n/a"} beachMode={beachMode} remembered='{beachRememberedOutfit ?? ""}' lastNamed='{lastNamedOutfit ?? ""}'";
        }
        if (a[0] == "zone" && a.Length > 1 && a[1] == "selftest") return OutfitZonesSelfTest();
        if (a[0] == "daily" && a.Length > 1 && a[1] == "status")
        {
            string prev = "?"; try { if (File.Exists(DailyOutfitDateFile)) prev = File.ReadAllText(DailyOutfitDateFile).Trim().Replace("\n", " / "); } catch { }
            return $"daily-outfit file: {prev}; lastNamed='{lastNamedOutfit ?? ""}'";
        }
        if (a[0] == "trash")
        {
            var names = a.Length > 1 ? a[1..] : TrashOutfitNames;
            if (names.Length == 1 && names[0].Equals("defaults", StringComparison.OrdinalIgnoreCase)) names = TrashOutfitNames;
            return await OutfitTrashNamed(names);
        }
        return "usage: outfit zone status|selftest | outfit trash <name…>|defaults | outfit daily status";
    }

    internal static string OutfitZonesSelfTest()
    {
        var sb = new StringBuilder(); int pass = 0, fail = 0;
        void C(bool ok, string w) { if (ok) pass++; else fail++; sb.AppendLine($"{(ok ? "PASS" : "FAIL")} {w}"); }
        C(OutfitZoneFor("Peronaut", 21f, false) == "beach", "enter beach at z21");
        C(OutfitZoneFor("Peronaut", 25f, false) == "mid", "mid at z25 from house");
        C(OutfitZoneFor("Peronaut", 25f, true) == "beach", "still beach at z25 with hysteresis");
        C(OutfitZoneFor("Peronaut", 28f, true) == "house", "leave beach at z28");
        C(OutfitZoneFor("Naberrie", 21f, false) == null, "other region no zone");
        var c = DailyOutfitCandidates(new[] { "Bikini", "Spicy", "tubetop", "tshirt", "monk", "Bikini" });
        C(c.SequenceEqual(new[] { "monk", "tshirt", "tubetop" }), $"daily candidates ({string.Join(",", c)})");
        C(!c.Any(BikiniNameRx.IsMatch), "Bikini/Spicy excluded");
        return $"outfit-zones selftest: {pass} PASS, {fail} FAIL\n" + sb.ToString().TrimEnd();
    }
}
