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
    static readonly string DailyOutfitAllowFile = Env("GT_DAILY_OUTFITS", "/home/box/viewers/textclient/run/daily-outfits.txt");
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

    static volatile bool zoneArmed; // false until the login sequence has decided beach/house (no race with the daily pick)
    static bool BikiniWorn() { var have = WornPrims().Select(AttachItemId).ToHashSet(); return have.Contains(BikiniTopItem) && have.Contains(BikiniPantiesItem); }

    static void OutfitZonesLoad()
    {
        zoneArmed = false; Interlocked.Exchange(ref dailyOutfitKick, 0);
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
    // Burgundy pier south of the beach (benches z21.4-23.9, deck up to ~z24.7): always beach (Bikini), David 17:00.
    internal static bool OnPeronautPier(float x, float y, float z) => x >= 210f && x <= 224f && y >= 18f && y <= 49f && z < 26f;
    internal static string OutfitZoneFor(string region, Vector3 pos, bool currentlyBeach)
    {
        if (string.Equals(region, "Peronaut", StringComparison.OrdinalIgnoreCase) && OnPeronautPier(pos.X, pos.Y, pos.Z)) return "beach";
        return OutfitZoneFor(region, pos.Z, currentlyBeach);
    }
    internal static string OutfitZoneFor(string region, float z, bool currentlyBeach)
    {
        if (!string.Equals(region, "Peronaut", StringComparison.OrdinalIgnoreCase)) return null;
        if (currentlyBeach) return z >= BeachLeaveZ ? "house" : "beach";
        return z <= BeachEnterZ ? "beach" : (z >= BeachLeaveZ ? "house" : "mid");
    }

    // allow == null: every saved outfit except Bikini; else only the names in the allow-list (run/daily-outfits.txt, David 16:28: tubetop + tshirt)
    internal static List<string> DailyOutfitCandidates(IEnumerable<string> folderNames, ICollection<string> allow = null) =>
        folderNames.Where(n => !string.IsNullOrWhiteSpace(n) && !BikiniNameRx.IsMatch(n.Trim()))
                   .Where(n => allow == null || allow.Count == 0 || allow.Contains(n.Trim(), StringComparer.OrdinalIgnoreCase))
                   .Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    // Pure: the daily pick never repeats the outfit she is wearing / wore last when any other candidate exists (2026-10-07)
    internal static List<string> DailyOutfitPool(IReadOnlyList<string> cands, string current)
    {
        if (string.IsNullOrWhiteSpace(current)) return cands.ToList();
        var pool = cands.Where(c => !c.Equals(current.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        return pool.Count > 0 ? pool : cands.ToList();
    }

    static List<string> DailyOutfitAllow()
    {
        try { if (File.Exists(DailyOutfitAllowFile)) return File.ReadAllLines(DailyOutfitAllowFile).Select(l => l.Split('#')[0].Trim()).Where(l => l.Length > 0).ToList(); } catch { }
        return new();
    }

    static async Task<List<InventoryFolder>> ListOutfitFolders(CancellationToken ct)
    {
        var mo = await FindMyOutfits(ct); if (mo == null) return new();
        return (await ReadFolderRO(mo.UUID, ct)).OfType<InventoryFolder>().Where(f => f.ParentUUID == mo.UUID).OrderBy(f => f.Name).ToList();
    }

    static async Task OutfitZoneTick()
    {
        if (!LoggedIn || beachOutfitBusy || !zoneArmed) return;
        var now = DateTime.UtcNow;
        if ((now - beachTickAt).TotalSeconds < 2.5) return;
        beachTickAt = now;
        var sim = client.Network.CurrentSim; if (sim == null) return;
        var z = client.Self.SimPosition.Z;
        var zone = OutfitZoneFor(sim.Name, client.Self.SimPosition, beachMode);
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
                    remember = DailyOutfitCandidates(folders.Select(f => f.Name), DailyOutfitAllow()).FirstOrDefault();
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
                    var r = await WearOutfitWithHuds(restore, keepColor: true);
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

    // Login order (David 16:16): settle, decide beach/house FIRST, then the daily pick. On the beach: keep / put on the
    // Bikini and save the daily pick for the walk up to the house. Not on the beach: wear the daily pick (once per PT day).
    internal static string LoginOutfitPlan(bool onBeach, bool bikiniWorn, bool dailyPending, string rememberedForHouse)
    {
        if (onBeach) return (bikiniWorn ? "keep-bikini" : "bikini-on") + (dailyPending ? "+daily-deferred" : "");
        if (dailyPending) return "daily-wear";
        return string.IsNullOrWhiteSpace(rememberedForHouse) ? "keep" : "restore-remembered";
    }

    static async Task DailyOutfitAfterLogin()
    {
        if (Interlocked.Exchange(ref dailyOutfitKick, 1) != 0) return;
        try
        {
            await Task.Delay(20000); // attachments + COF settle after login
            if (!LoggedIn) return;
            var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
                TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles")).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            string prev = null;
            try { if (File.Exists(DailyOutfitDateFile)) prev = File.ReadLines(DailyOutfitDateFile).FirstOrDefault()?.Trim(); } catch { }
            bool dailyPending = prev != today;
            var region = client.Network.CurrentSim?.Name ?? "";
            var z = client.Self.SimPosition.Z;
            var lpos = client.Self.SimPosition;
            bool onBeach = OutfitZoneFor(region, lpos, false) == "beach" || (beachMode && OutfitZoneFor(region, lpos, true) == "beach");
            var plan = LoginOutfitPlan(onBeach, BikiniWorn(), dailyPending, beachMode ? beachRememberedOutfit : null);
            Log("outfit-daily", $"login outfit plan: {plan} (region {region} z {z:F1}, daily {(dailyPending ? "pending" : "done")} for {today} PT)");
            string pick = null; bool freshPick = false; var wornBefore = lastNamedOutfit;
            if (dailyPending)
            {
                using var cts = new CancellationTokenSource(60000);
                var cands = DailyOutfitCandidates((await ListOutfitFolders(cts.Token)).Select(f => f.Name), DailyOutfitAllow());
                var pool = DailyOutfitPool(cands, lastNamedOutfit);
                Log("outfit-daily", $"candidates [{string.Join(", ", cands)}], current '{lastNamedOutfit ?? "-"}' -> pool [{string.Join(", ", pool)}]");
                if (pool.Count > 0) { pick = pool[Random.Shared.Next(pool.Count)]; freshPick = true; }
            }
            beachOutfitBusy = true;
            try
            {
                if (onBeach)
                {
                    if (pick != null) beachRememberedOutfit = pick;           // worn when she goes up to the house
                    else if (string.IsNullOrWhiteSpace(beachRememberedOutfit) && !string.IsNullOrWhiteSpace(lastNamedOutfit) && !BikiniNameRx.IsMatch(lastNamedOutfit))
                        beachRememberedOutfit = lastNamedOutfit;
                    beachMode = true; PersistBeachState();
                    if (!BikiniWorn()) Log("outfit-daily", "on the beach without the bikini: " + (await BikiniOn()).Replace("\n", " | "));
                    else Log("outfit-daily", "on the beach in the bikini: keeping it");
                    if (pick != null) Log("outfit-daily", $"daily pick '{pick}' saved for the house");
                }
                else
                {
                    if (beachMode && !string.IsNullOrWhiteSpace(beachRememberedOutfit) && pick == null) pick = beachRememberedOutfit;
                    beachMode = false; beachRememberedOutfit = null; PersistBeachState();
                    if (pick != null) Log("outfit-daily", $"wearing '{pick}': " + (await WearOutfitWithHuds(pick, KeepHudColorFor(pick, wornBefore, freshPick))).Replace("\n", " | "));
                }
                if (dailyPending && pick != null)
                    try { Directory.CreateDirectory(Path.GetDirectoryName(DailyOutfitDateFile)!); File.WriteAllText(DailyOutfitDateFile, today + "\n" + pick + "\n"); } catch { }
            }
            finally { beachOutfitBusy = false; zoneArmed = true; }
        }
        catch (Exception ex) { Log("outfit-daily", "error: " + ex.GetBaseException().Message); zoneArmed = true; }
    }

    // Move a folder with the classic MoveInventoryFolder UDP message. The AIS PATCH parent_id route the library uses
    // answers 400 Bad Request on SL (16:13 + 16:28 PT): the local cache moved, the server did not.
    static void MoveFolderUdp(InventoryFolder f, UUID newParent)
    {
        var move = new LibreMetaverse.Packets.MoveInventoryFolderPacket
        {
            AgentData = { AgentID = client.Self.AgentID, SessionID = client.Self.SessionID, Stamp = false },
            InventoryData = new[] { new LibreMetaverse.Packets.MoveInventoryFolderPacket.InventoryDataBlock { FolderID = f.UUID, ParentID = newParent } }
        };
        client.Network.SendPacket(move);
        try { f.ParentUUID = newParent; client.Inventory.Store.UpdateNodeFor(f); } catch { }
    }

    internal static List<string> ParseOutfitNames(string rest) =>
        (rest ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(n => n.Trim('"', '\'')).Where(n => n.Length > 0).ToList();

    // pure (selftest): per requested name -> "trash" / "refused" (kept outfit: Bikini + daily list, unless force) / "not found"
    // (exact, case-insensitive folder name under My Outfits; never a substring). Blank and repeated names are dropped.
    internal static List<(string name, string verdict)> PlanOutfitTrash(IEnumerable<string> names, IEnumerable<string> existing, ISet<string> keep, bool force)
    {
        var have = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return names.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n => (n, keep.Contains(n) && !force ? "refused" : have.Contains(n) ? "trash" : "not found")).ToList();
    }

    // pure (selftest): a term that is the uuid of a My Outfits folder -> that folder's exact name (the command line strips
    // quotes, so a folder saved as '"Valentine' can only be named by its uuid); other terms unchanged
    internal static List<string> ResolveOutfitTrashUuids(IEnumerable<string> names, IEnumerable<(UUID id, string name)> kids) =>
        names.Select(n => UUID.TryParse(n.Trim(), out var id) && kids.Any(k => k.id == id) ? kids.First(k => k.id == id).name : n).ToList();

    // Move My Outfits folders to Trash (exact names or folder uuids; Bikini + the daily outfits refused unless 'force'). Never purges.
    static async Task<string> OutfitTrashNamed(IEnumerable<string> names, bool force = false)
    {
        if (!LoggedIn) return "not logged in";
        using var cts = new CancellationTokenSource(60000); var ct = cts.Token;
        var mo = await FindMyOutfits(ct); if (mo == null) return "My Outfits folder not found";
        var trash = client.Inventory.FindFolderForType(FolderType.Trash);
        if (trash == UUID.Zero) return "Trash folder not found";
        var kids = (await ReadFolderRO(mo.UUID, ct)).OfType<InventoryFolder>().Where(f => f.ParentUUID == mo.UUID).ToList();
        var keep = DailyOutfitAllow().Append("Bikini").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        names = ResolveOutfitTrashUuids(names, kids.Select(k => (k.UUID, k.Name)));
        foreach (var (n, verdict) in PlanOutfitTrash(names, kids.Select(k => k.Name), keep, force))
        {
            if (verdict == "refused") { sb.AppendLine($"refused: '{n}' is a kept outfit (Bikini / daily list)"); continue; }
            if (verdict == "not found") { sb.AppendLine($"not found under My Outfits (exact name): '{n}'"); continue; }
            var f = kids.First(k => k.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
            MoveFolderUdp(f, trash);
            Log("outfit", $"moved outfit folder '{f.Name}' {f.UUID} -> Trash (UDP)");
            sb.AppendLine($"trashed outfit folder '{f.Name}' ({f.UUID})");
        }
        await Task.Delay(1500, ct);
        var left = (await ReadFolderRO(mo.UUID, ct)).OfType<InventoryFolder>().Where(f => f.ParentUUID == mo.UUID).OrderBy(f => f.Name).Select(f => f.Name).ToList();
        sb.AppendLine($"remaining My Outfits ({left.Count}): {string.Join(", ", left.Select(n => $"'{n}'"))}");
        return sb.ToString().TrimEnd();
    }

    // Put a folder that sits in Trash back under My Outfits (also repairs a cache that thinks it was moved).
    static async Task<string> OutfitUntrash(string name)
    {
        using var cts = new CancellationTokenSource(60000); var ct = cts.Token;
        var mo = await FindMyOutfits(ct); if (mo == null) return "My Outfits folder not found";
        var trash = client.Inventory.FindFolderForType(FolderType.Trash);
        var f = (await ReadFolderRO(trash, ct)).OfType<InventoryFolder>().FirstOrDefault(k => k.ParentUUID == trash && k.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (f == null) return $"no folder '{name}' in Trash";
        MoveFolderUdp(f, mo.UUID);
        Log("outfit", $"moved folder '{f.Name}' {f.UUID} Trash -> My Outfits (UDP)");
        return $"'{f.Name}' ({f.UUID}) back under My Outfits";
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
            bool force = a.Length > 1 && a[^1].Equals("force", StringComparison.OrdinalIgnoreCase);
            var rest = string.Join(' ', a[1..(force ? a.Length - 1 : a.Length)]);
            var names = rest.Trim().Equals("defaults", StringComparison.OrdinalIgnoreCase) || rest.Trim().Length == 0 ? TrashOutfitNames.ToList() : ParseOutfitNames(rest);
            return await OutfitTrashNamed(names, force);
        }
        if (a[0] == "untrash" && a.Length > 1) return await OutfitUntrash(string.Join(' ', a[1..]));
        return "usage: outfit zone status|selftest | outfit trash <name…>|defaults | outfit daily status";
    }

    internal static string OutfitZonesSelfTest()
    {
        var sb = new StringBuilder(); int pass = 0, fail = 0;
        void C(bool ok, string w) { if (ok) pass++; else fail++; sb.AppendLine($"{(ok ? "PASS" : "FAIL")} {w}"); }
        C(OutfitZoneFor("Peronaut", 21f, false) == "beach", "enter beach at z21");
        var dc = new List<string> { "ARTi'S Strapless Top", "PCP Beth Tube Top", "TETRA Chill T-Shirt" };
        C(DailyOutfitPool(dc, "arti's strapless top ").SequenceEqual(new[] { "PCP Beth Tube Top", "TETRA Chill T-Shirt" }), "daily pool excludes the current outfit (case/space-insensitive)");
        C(DailyOutfitPool(dc.Take(1).ToList(), "ARTi'S Strapless Top").Count == 1 && DailyOutfitPool(dc, null).Count == 3 && DailyOutfitPool(dc, "Bikini").Count == 3, "daily pool: only candidate kept; no/other current -> all");
        C(OutfitZoneFor("Peronaut", 25f, false) == "mid", "mid at z25 from house");
        C(OutfitZoneFor("Peronaut", 25f, true) == "beach", "still beach at z25 with hysteresis");
        C(OutfitZoneFor("Peronaut", 28f, true) == "house", "leave beach at z28");
        C(OutfitZoneFor("Naberrie", 21f, false) == null, "other region no zone");
        C(OutfitZoneFor("Peronaut", new Vector3(215.4f, 30.5f, 24.7f), false) == "beach", "pier deck z24.7 counts as beach (not mid)");
        C(OutfitZoneFor("Peronaut", new Vector3(218f, 44.6f, 21.4f), false) == "beach", "first pier bench is beach");
        C(OutfitZoneFor("Peronaut", new Vector3(217.2f, 84.1f, 25.5f), false) == "mid", "front porch z25.5 is not beach");
        C(OutfitZoneFor("Peronaut", new Vector3(228f, 75f, 29f), true) == "house", "house from beach");
        var c = DailyOutfitCandidates(new[] { "Bikini", "Spicy", "tubetop", "tshirt", "monk", "Bikini" });
        C(c.SequenceEqual(new[] { "monk", "tshirt", "tubetop" }), $"daily candidates ({string.Join(",", c)})");
        C(!c.Any(BikiniNameRx.IsMatch), "Bikini/Spicy excluded");
        C(LoginOutfitPlan(true, true, true, null) == "keep-bikini+daily-deferred", "login on beach in bikini: keep, defer daily");
        C(LoginOutfitPlan(true, false, false, null) == "bikini-on", "login on beach not in bikini: bikini on");
        C(LoginOutfitPlan(false, true, true, null) == "daily-wear", "login upstairs: daily wear");
        C(LoginOutfitPlan(false, false, false, "tshirt") == "restore-remembered", "login upstairs after beach: restore");
        C(LoginOutfitPlan(false, false, false, null) == "keep", "login upstairs, daily done: keep");
        var al = DailyOutfitCandidates(new[] { "Bikini", "tubetop", "tshirt", "Jiyoo tubetop", "jiyoo Tshirt", "Jani tshirt" }, new[] { "tubetop", "tshirt" });
        C(al.SequenceEqual(new[] { "tshirt", "tubetop" }), $"daily allow-list tubetop+tshirt ({string.Join(",", al)})");
        var pn = ParseOutfitNames("Jiyoo tubetop, jiyoo Tshirt,'Jani tshirt'");
        C(pn.SequenceEqual(new[] { "Jiyoo tubetop", "jiyoo Tshirt", "Jani tshirt" }), "trash names: comma separated, spaces kept");
        // PR #61 follow-up tests: exact comma names, kept-outfit refusal, daily allow-list
        C(ParseOutfitNames("Jiyoo tubetop").SequenceEqual(new[] { "Jiyoo tubetop" }), "a space-separated name stays ONE name (never split on spaces)");
        C(ParseOutfitNames(" , \"PCP Beth Tube Top\" ,, ").SequenceEqual(new[] { "PCP Beth Tube Top" }), "blank entries dropped, quotes stripped");
        var keep = new HashSet<string>(new[] { "Bikini", "PCP Beth Tube Top" }, StringComparer.OrdinalIgnoreCase);
        var have = new[] { "Bikini", "PCP Beth Tube Top", "Jiyoo tubetop", "tubetop" };
        var plan = PlanOutfitTrash(new[] { "bikini", "Jiyoo tubetop", "tube", "Jiyoo Tubetop", "  " }, have, keep, false);
        C(plan.Count == 3 && plan[0] == ("bikini", "refused"), "kept outfit (Bikini) refused without force, case-insensitive");
        C(plan[1] == ("Jiyoo tubetop", "trash"), "exact name trashed");
        C(plan[2] == ("tube", "not found"), "a substring ('tube') never matches 'tubetop'");
        C(PlanOutfitTrash(new[] { "PCP Beth Tube Top" }, have, keep, true).Single().verdict == "trash", "force trashes a kept outfit");
        C(PlanOutfitTrash(new[] { "tubetop" }, have, keep, false).Single().verdict == "trash" && !plan.Any(p => p.name == "tubetop"), "'Jiyoo tubetop' never hits 'tubetop'");
        var vk = new List<(UUID, string)> { (new UUID("d2a13474-008d-443b-9b93-7732bf681990"), "\"Valentine"), (new UUID("665fc9bb-1ce7-453c-b522-297e259aed82"), "Bikini") };
        var rv = ResolveOutfitTrashUuids(ParseOutfitNames("d2a13474-008d-443b-9b93-7732bf681990, Monk, 11111111-2222-3333-4444-555555555555"), vk);
        C(rv.SequenceEqual(new[] { "\"Valentine", "Monk", "11111111-2222-3333-4444-555555555555" }), "outfit trash: a My Outfits folder uuid -> its exact (quoted) name; names + unknown uuids unchanged");
        C(PlanOutfitTrash(ResolveOutfitTrashUuids(new[] { "665fc9bb-1ce7-453c-b522-297e259aed82" }, vk), new[] { "Bikini" }, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Bikini" }, false).Single().verdict == "refused", "outfit trash: Bikini by uuid still refused without force");
        var al2 = DailyOutfitCandidates(new[] { "Bikini", "PCP BETH TUBE TOP", "TETRA Chill T-Shirt", "ARTi'S Strapless Top" }, new[] { "PCP Beth Tube Top", "TETRA Chill T-Shirt", "Missing Outfit" });
        C(al2.Count == 2 && !al2.Contains("ARTi'S Strapless Top") && !al2.Any(n => n == "Missing Outfit"), $"daily allow-list: case-insensitive, unlisted excluded, listed-but-unsaved ignored ({string.Join(",", al2)})");
        C(DailyOutfitCandidates(new[] { "Bikini" }, new[] { "Bikini" }).Count == 0, "Bikini never a daily outfit, even if listed");
        return $"outfit-zones selftest: {pass} PASS, {fail} FAIL\n" + sb.ToString().TrimEnd();
    }
}
