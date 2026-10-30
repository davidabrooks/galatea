// LoginVeil.cs (2026-10-30, David at the 10:36 relog: "I didn't see your base textures, but I did see your good body for a few seconds before your clothes covered you").
// Evidence (textclient.log, login 10:36:17): boots #7 and shorts #10 seen +3 s, real head +4 s, shorts "Ready" +5 s, BODY #40 and T-shirt #55 seen
// together at +7 s, T-shirt and boots "Ready" +10 s. The sim restores body and T-shirt in the same instant: the client has no order left to fix. What
// David saw is his viewer drawing the (cached) body before the T-shirt's mesh and the shorts' body cut arrived (the shorts were Ready at +5 s, before
// the body existed, so their cut had nobody to apply to). The only lever is to keep the body INVISIBLE until the garments are there and Ready:
// a body-only system alpha layer (the "veil": upper + lower body alpha, NOT the head) put on at logout, so the login bake already hides the
// system body AND the BOM mesh body from the first frame, then taken off (and rebaked) once every worn LaraX garment is seen and Ready.
// The veil item is named by its inventory item id in routes/_login-veil.txt (first non-comment line). No file / item: nothing changes.
// Safety: the veil is always taken off by MaxHold, and a veil found worn outside the login window is taken off too.
// 2026-10-30 12:03 / 12:04 relogs (David: "the veil came off, but many seconds after all your clothes appeared, and your hair and teeth were floating too"):
// (1) 'veil off at +21.5 s: every garment attached and Ready by +9.2 s'. The release loop did a SERVER read of the Current Outfit every tick (up to 20 s
//     timeout); at login that read queues behind the login-cover / outfit-send inventory traffic, so the tick that saw "all Ready" only ran at ~+16 s
//     (COF links removed 12:03:08), then the off path did two more COF reads and an item fetch before the bake (12:03:08 -> 12:03:13).
//     Now: the COF is read once (re-read at most every 5 s, short timeout), the veil item is fetched once up front, each tick only checks
//     in-memory state (worn prims, Ready chat), and the grace after the last Ready is 0.5 s (the server bake itself adds ~4-5 s of margin).
// (2) Hair, teeth, eyes and mesh clothes are separate meshes an alpha cannot hide: they floated around the invisible body. Now, at logout, AFTER the
//     veil is verified on, those attachments are detached with their COF links KEPT and recorded in run/login-veil-stash.txt (written before the
//     detach). At login the clothes are re-attached at once from that list; hair / teeth / eyes / jewellery go on together with the veil-off bake.
//     Never nude: with a stash the veil does not come off until the clothes are attached (MaxHold 90 s instead of 25 s; past it the veil STAYS on and
//     the log says so). Fallbacks: a second attach of the stash list at +8 s, the last clothed outfit at +25 s, and the library's own COF add-missing
//     pass (the COF links were never removed). The daily outfit waits for this restore.
using LibreMetaverse;

namespace GalatayText;

internal static class LoginVeil
{
    internal sealed record Garment(string Name, double? SeenSec, double? ReadySec);
    internal static readonly double MaxHoldSec = 25, GraceSec = 0.5, MaxHoldStashedSec = 90, RetryAttachSec = 8, OutfitFallbackSec = 25;
    internal sealed record Stashed(UUID Item, int Point, string Name);

    /// <summary>Pure (tested): take this attachment off at logout while the veil hides the body? Not HUDs (points 31-38), not the mesh body / add-on,
    /// not the real head, hands, feet or skeleton (rank &lt;= 1, under the alpha), not HUD / AO / collar / bridge (rank 8), not the Kisser, not unnamed.</summary>
    internal static bool ShouldStash(string name, int point)
    {
        if (string.IsNullOrWhiteSpace(name) || (point >= 31 && point <= 38)) return false;
        if (LoginOrder.IsBody(name) || LoginCover.IsBodyPart(name) || name.Contains("Kisser", StringComparison.OrdinalIgnoreCase)) return false;
        int r = Program.OutfitSendRank(name);
        return r > 1 && r < 8 && LoginCover.PlanRank(name, Program.OutfitSendRank) >= 0;
    }

    /// <summary>Pure (tested): put this stashed item back only together with the veil-off bake (hair, teeth, eyes, jewellery), not at login.</summary>
    internal static bool RevealWithVeil(string name)
        => !Waits(name) && (Program.OutfitSendRank(name) == 2 || System.Text.RegularExpressions.Regex.IsMatch(name ?? "", @"teeth|eyes?\b|lel evox|earring|necklace|piercing|nails|lashes", System.Text.RegularExpressions.RegexOptions.IgnoreCase));

    internal static string FormatStash(IEnumerable<Stashed> s, DateTime at)
        => $"# login veil stash {at:yyyy-MM-dd HH:mm:ss}: attachments taken off at logout (COF links kept); re-attached at login\n"
           + string.Concat(s.Select(x => $"{x.Item}\t{x.Point}\t{(x.Name ?? "").Replace('\t', ' ').Replace('\n', ' ')}\n"));
    internal static List<Stashed> ParseStash(string text)
    {
        var r = new List<Stashed>();
        foreach (var l in (text ?? "").Split('\n'))
        {
            if (l.StartsWith("#")) continue; var f = l.TrimEnd('\r').Split('\t');
            if (f.Length >= 3 && UUID.TryParse(f[0], out var id) && id != UUID.Zero && int.TryParse(f[1], out var pt) && r.All(x => x.Item != id)) r.Add(new(id, pt, f[2]));
        }
        return r;
    }

    /// <summary>Pure: the garments the veil waits for: LaraX-fitted clothing (it cuts the body and announces Ready).</summary>
    internal static bool Waits(string name) => LoginOrder.IsBodyCuttingGarment(name);

    /// <summary>Pure (tested): take the veil off now? Every waited garment of the current outfit is seen and Ready, plus a short grace for the
    /// viewer to draw it; nothing to wait for (purposely naked) releases at once; MaxHold always releases.</summary>
    internal static (bool release, string why) Release(IEnumerable<Garment> garments, double nowSec, bool stashed = false)
    {
        var g = (garments ?? Array.Empty<Garment>()).Where(x => Waits(x.Name)).ToList();
        var missing = g.Where(x => x.SeenSec == null || x.ReadySec == null).ToList();
        double hold = stashed ? MaxHoldStashedSec : MaxHoldSec;
        if (nowSec >= hold)
        {   // with a stash the body under the veil is bare until the clothes are back: stay invisible rather than nude
            if (stashed && g.Any(x => x.SeenSec == null)) return (false, $"KEEP: clothes taken off at logout not back after {hold:F0} s ({string.Join("; ", g.Where(x => x.SeenSec == null).Select(x => x.Name))}); the veil stays on");
            return (true, $"max hold {hold:F0} s reached; still waiting for: {string.Join("; ", missing.Select(x => x.Name))}");
        }
        if (g.Count == 0) return (true, "no body-cutting garment in the outfit");
        if (missing.Count > 0) return (false, "waiting for: " + string.Join("; ", missing.Select(x => x.Name + (x.SeenSec == null ? " (not attached)" : " (not Ready)"))));
        double last = g.Max(x => Math.Max(x.SeenSec!.Value, x.ReadySec!.Value));
        return nowSec >= last + GraceSec ? (true, $"every garment attached and Ready by +{last:F1} s") : (false, "grace after the last Ready");
    }

    /// <summary>Pure (tested): did the logout veil really get into the outfit? It needs its COF link and a COF version newer than before
    /// (the sim bakes by COF version; an unchanged version means the bake request is skipped as a duplicate).</summary>
    internal static (bool ok, string why) LogoutVerdict(bool linkInCof, int cofBefore, int cofAfter)
    {
        if (!linkInCof) return (false, "no veil link in Current Outfit");
        if (cofAfter <= cofBefore) return (false, $"COF version did not move (v{cofBefore} -> v{cofAfter}), the bake would skip it");
        return (true, $"COF v{cofBefore} -> v{cofAfter}, veil linked");
    }

    /// <summary>Pure (tested): did the login veil really come off? Its COF link must be gone and the COF version must have moved (else the
    /// rebake is skipped as a duplicate). 11:48 relog: "veil off" was logged while the link stayed in the COF.</summary>
    internal static (bool ok, string why) OffVerdict(bool linkStillInCof, int cofBefore, int cofAfter)
    {
        if (linkStillInCof) return (false, "veil link still in Current Outfit");
        if (cofAfter <= cofBefore) return (false, $"COF version did not move (v{cofBefore} -> v{cofAfter}), the bake would skip it");
        return (true, $"COF v{cofBefore} -> v{cofAfter}, veil link gone");
    }

    /// <summary>Pure: the veil item id from routes/_login-veil.txt (first non-comment UUID).</summary>
    internal static UUID ParseVeilFile(string text)
    {
        foreach (var l in (text ?? "").Split('\n').Select(x => x.Trim()))
            if (l.Length > 0 && !l.StartsWith("#") && UUID.TryParse(l.Split(' ', '\t')[0], out var id) && id != UUID.Zero) return id;
        return UUID.Zero;
    }
}

public static partial class Program
{
    static int loginVeilGen;
    static UUID LoginVeilItem()
    {
        if (Env("GT_LOGIN_VEIL", "1") == "0") return UUID.Zero;
        try { var f = Path.Combine(RouteDir, "_login-veil.txt"); return File.Exists(f) ? LoginVeil.ParseVeilFile(File.ReadAllText(f)) : UUID.Zero; } catch { return UUID.Zero; }
    }
    static bool LoginVeilWorn(UUID id) => id != UUID.Zero && SafeWearables().Any(w => w.ItemID == id);

    /// <summary>Logout: put the veil on and get it into the bake, so the next login starts with the body hidden.</summary>
    static async Task LoginVeilOnForLogout()
    {
        var id = LoginVeilItem(); if (id == UUID.Zero || !LoggedIn) return;
        try
        {
            using var cts = new CancellationTokenSource(20000);
            var it = await FetchItemRO(id, cts.Token); if (it == null) { Log("login-veil", $"veil item {id} not found in inventory"); return; }
            // 11:34 relog: AddToOutfit alone left the COF at v2918 (no link yet), so the bake request was skipped as a duplicate and the veil
            // never reached the sim; the log still said "on". Now: the COF link is made and checked, the COF version must move, then the bake.
            var cof = await CofFolder(cts.Token); int before = cof?.Version ?? -1;
            if (!LoginVeilWorn(id)) client.Appearance.AddToOutfit(new List<InventoryItem> { it }, false);
            var add = await CofLinksOnBatch(new List<InventoryItem> { it }, null, "login veil on for logout", cts.Token);
            bool linked = false; int after = -1;
            if (cof != null)
            {
                var items = await ReadFolderRO(cof.UUID, cts.Token, 8000);
                linked = items.OfType<InventoryItem>().Any(l => l.IsLink() && l.AssetUUID == id);
                after = (client.Inventory.Store[cof.UUID] as InventoryFolder)?.Version ?? cof.Version;
            }
            var (ok, why) = LoginVeil.LogoutVerdict(linked, before, after);
            if (ok) { await RebakeNow("login veil on for logout"); await Task.Delay(4000); }
            if (ok) await LoginVeilStashForLogout();   // only once the veil is verified: never take clothes off over a visible body
            Log("login-veil", ok ? $"veil '{it.Name}' on for logout ({why}; {add}): the next login starts with the body hidden"
                                 : $"veil NOT on for logout: {why} ({add}); this login will show the body as before");
        }
        catch (Exception ex) { Log("login-veil", "veil on failed: " + ex.GetBaseException().Message); }
    }

    static string LoginVeilStashFile => Env("GT_VEIL_STASH_FILE", "/home/box/viewers/textclient/run/login-veil-stash.txt");
    static volatile Task loginVeilRestoreTask = Task.CompletedTask;   // the daily outfit waits for it (OutfitZones.cs)

    /// <summary>Logout, veil verified on: take off what the alpha cannot hide (hair, teeth, eyes, mesh clothes), COF links kept, list saved first.</summary>
    static async Task LoginVeilStashForLogout()
    {
        if (Env("GT_VEIL_STASH", "1") == "0" || !LoggedIn) return;
        try
        {
            var prims = WornPrims().Where(p => AttachItemId(p) != UUID.Zero).ToList();
            var noProps = prims.Where(p => p.Properties == null).ToList();
            if (noProps.Count > 0) { try { await Task.WhenAny(EnsureProperties(client.Network.CurrentSim, noProps), Task.Delay(3000)); } catch { } }
            var always = AlwaysWearIds();
            var stash = prims.Select(p => new LoginVeil.Stashed(AttachItemId(p), (int)p.PrimData.AttachmentPoint, p.Properties?.Name ?? ""))
                             .Where(x => LoginVeil.ShouldStash(x.Name, x.Point) && !always.Contains(x.Item)).GroupBy(x => x.Item).Select(g => g.First()).ToList();
            if (stash.Count == 0) { Log("login-veil", "stash: nothing the veil cannot hide is worn"); return; }
            var tmp = LoginVeilStashFile + ".tmp";
            File.WriteAllText(tmp, LoginVeil.FormatStash(stash, DateTime.Now)); File.Move(tmp, LoginVeilStashFile, true);   // the list exists BEFORE anything comes off
            foreach (var x in stash) DetachItem(x.Item, "login veil stash");
            for (int i = 0; i < 16; i++) { var w = WornPrims().Select(AttachItemId).ToHashSet(); if (!stash.Any(x => w.Contains(x.Item))) break; await Task.Delay(250); }
            var left = WornPrims().Select(AttachItemId).ToHashSet();
            Log("login-veil", $"stash: {stash.Count} attachment(s) taken off for logout (COF links kept, list in {Path.GetFileName(LoginVeilStashFile)}): {string.Join("; ", stash.Select(x => x.Name))}"
                + (stash.Any(x => left.Contains(x.Item)) ? $"; still attached: {string.Join("; ", stash.Where(x => left.Contains(x.Item)).Select(x => x.Name))}" : ""));
        }
        catch (Exception ex) { Log("login-veil", "stash failed (the outfit stays as it is): " + ex.GetBaseException().Message); }
    }

    static List<LoginVeil.Stashed> LoginVeilReadStash()
    {
        try
        {
            var f = LoginVeilStashFile; if (!File.Exists(f)) return new();
            if ((DateTime.Now - File.GetLastWriteTime(f)).TotalDays > 14) { Log("login-veil", "stash file older than 14 days: ignored (the COF restore covers the outfit)"); File.Delete(f); return new(); }
            return LoginVeil.ParseStash(File.ReadAllText(f));
        }
        catch { return new(); }
    }

    static async Task<int> LoginVeilAttach(IEnumerable<LoginVeil.Stashed> items, string why, CancellationToken ct)
    {
        int n = 0; var worn = WornPrims().Select(AttachItemId).ToHashSet();
        foreach (var x in items.Where(x => !worn.Contains(x.Item)))
        {
            if (OutfitChangeActive) { Log("login-veil", $"stash attach ({why}) stopped: an outfit change is running"); break; }
            var it = await FetchItemRO(x.Item, ct);
            if (it == null || it.IsLink()) { Log("login-veil", $"stash: '{x.Name}' {x.Item} not returned by the inventory fetch ({why})"); continue; }
            client.Appearance.Attach(it, x.Point > 0 ? (AttachmentPoint)x.Point : AttachmentPoint.Default, false); n++;
        }
        if (n > 0) Log("login-veil", $"stash: {n} attachment(s) sent ({why})");
        return n;
    }

    /// <summary>Login: take the veil off as soon as the outfit's garments are attached and Ready (LoginVeil.Release); put the stash back.</summary>
    static void LoginVeilStart()
    {
        int gen = Interlocked.Increment(ref loginVeilGen); var t0 = DateTime.Now;
        var id = LoginVeilItem(); var stash = LoginVeilReadStash();
        if (id == UUID.Zero && stash.Count == 0) return;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); loginVeilRestoreTask = done.Task;
        _ = Task.Run(async () =>
        {
            string why = "?"; bool off = true;
            using var cts = new CancellationTokenSource(150000); var ct = cts.Token;
            var early = stash.Where(x => !LoginVeil.RevealWithVeil(x.Name)).ToList(); var late = stash.Where(x => LoginVeil.RevealWithVeil(x.Name)).ToList();
            try
            {
                if (stash.Count > 0) Log("login-veil", $"stash from logout: {stash.Count} item(s); clothes now ({early.Count}), with the veil off ({late.Count})");
                var itemTask = id == UUID.Zero ? Task.FromResult<InventoryItem>(null) : FetchItemRO(id, ct);   // fetched once, used by the off path
                await Task.Delay(1000, ct);
                if (early.Count > 0) await LoginVeilAttach(early, "login", ct);
                if (id == UUID.Zero) { why = "no veil configured"; return; }
                bool retried = false, fellBack = false; List<InventoryItem> links = null; DateTime linksAt = DateTime.MinValue; UUID cofId = UUID.Zero;
                while (gen == loginVeilGen && LoggedIn)
                {
                    double now = (DateTime.Now - t0).TotalSeconds;
                    if (links == null || (DateTime.Now - linksAt).TotalSeconds > 5)
                    {   // one COF read, refreshed at most every 5 s with a short timeout: the tick itself never waits on the server
                        var cof = cofId == UUID.Zero ? await CofFolder(ct) : null; if (cof != null) cofId = cof.UUID;
                        if (cofId != UUID.Zero)
                        {
                            var (kids, okRead) = await ReadFolderTimed(cofId, ct, 4000);
                            if (okRead) { links = kids.OfType<InventoryItem>().Where(l => l.ParentUUID == cofId && l.IsLink() && (l.InventoryType == InventoryType.Object || l.InventoryType == InventoryType.Attachment)).GroupBy(l => l.AssetUUID).Select(g => g.First()).ToList(); }
                        }
                        linksAt = DateTime.Now;
                    }
                    var worn = WornPrims().Select(AttachItemId).ToHashSet();
                    if (stash.Count > 0 && !retried && now >= LoginVeil.RetryAttachSec) { retried = true; await LoginVeilAttach(early, "retry", ct); }
                    if (stash.Count > 0 && !fellBack && now >= LoginVeil.OutfitFallbackSec && early.Any(x => LoginVeil.Waits(x.Name) && !worn.Contains(x.Item)))
                    {
                        fellBack = true; var last = LastClothedOutfit();
                        Log("login-veil", $"stash clothes still not back at +{now:F0} s: wearing the last clothed outfit '{last ?? "-"}'");
                        if (!string.IsNullOrWhiteSpace(last)) _ = Task.Run(async () => { try { Log("login-veil", "fallback outfit: " + (await WearOutfitWithHuds(last)).Replace("\n", " | ")); } catch (Exception ex) { Log("login-veil", "fallback outfit failed: " + ex.GetBaseException().Message); } });
                    }
                    var garments = (links ?? new()).Select(l => new LoginVeil.Garment(l.Name,
                        worn.Contains(l.AssetUUID) ? (loginSeenAt.TryGetValue(l.AssetUUID, out var s) ? (s - t0).TotalSeconds : 0) : null,
                        loginReadyAt.TryGetValue(l.Name, out var r) ? (r - t0).TotalSeconds : null)).ToList();
                    var (rel, w) = links == null && now < LoginVeil.MaxHoldSec ? (false, "Current Outfit not readable yet") : LoginVeil.Release(garments, now, stash.Count > 0);
                    why = w; if (rel) break;
                    if (w.StartsWith("KEEP")) { off = false; break; }
                    await Task.Delay(200, ct);
                }
                if (late.Count > 0 && LoggedIn) await LoginVeilAttach(late, "with the veil off", ct);   // hair, teeth, eyes: drawn with the body's bake
                if (off && gen == loginVeilGen) await LoginVeilOff(id, t0, why, await itemTask);
                else if (!off) Log("login-veil", $"veil KEPT ON at +{(DateTime.Now - t0).TotalSeconds:F1} s: {why}");
            }
            catch (Exception ex)
            {   // never nude: with a stash, the veil only comes off once its clothes are attached
                why = "error: " + ex.GetBaseException().Message; var w = WornPrims().Select(AttachItemId).ToHashSet();
                if (gen == loginVeilGen && id != UUID.Zero && early.Where(x => LoginVeil.Waits(x.Name)).All(x => w.Contains(x.Item))) await LoginVeilOff(id, t0, why, null);
                else Log("login-veil", $"veil KEPT ON ({why}): stashed clothes not back");
            }
            finally
            {
                try { if (stash.Count > 0 && LoggedIn) await LoginVeilStashDone(stash, t0); } catch { }
                done.TrySetResult();
            }
        });
    }

    static async Task LoginVeilStashDone(List<LoginVeil.Stashed> stash, DateTime t0)
    {
        for (int i = 0; i < 40; i++) { var w = WornPrims().Select(AttachItemId).ToHashSet(); if (stash.All(x => w.Contains(x.Item))) break; await Task.Delay(250); }
        var worn = WornPrims().Select(AttachItemId).ToHashSet(); var miss = stash.Where(x => !worn.Contains(x.Item)).ToList();
        Log("login-veil", miss.Count == 0 ? $"stash: all {stash.Count} back on by +{(DateTime.Now - t0).TotalSeconds:F1} s"
                                          : $"stash: not back on: {string.Join("; ", miss.Select(x => x.Name))} (COF links kept: the library's add-missing pass still wears them)");
        try { File.Delete(LoginVeilStashFile); } catch { }
    }

    static async Task LoginVeilOff(UUID id, DateTime t0, string why, InventoryItem it = null)
    {
        string At() => $"+{(DateTime.Now - t0).TotalSeconds:F1} s";
        try
        {
            if (!LoggedIn) { Log("login-veil", $"veil NOT off at {At()}: logged out ({why})"); return; }
            using var cts = new CancellationTokenSource(30000); var ct = cts.Token;
            var cof = await CofFolder(ct); if (cof == null) { Log("login-veil", $"veil NOT off at {At()}: Current Outfit not found ({why})"); return; }
            async Task<bool> Linked() => (await ReadFolderRO(cof.UUID, ct, 5000)).OfType<InventoryItem>().Any(l => l.ParentUUID == cof.UUID && l.IsLink() && l.AssetUUID == id);
            int CofVer() => (client.Inventory.Store[cof.UUID] as InventoryFolder)?.Version ?? cof.Version;
            // 11:48 relog: trusted RemoveFromOutfit, logged "veil off", but the COF link stayed. The COF link is the truth: remove it ourselves,
            // read back, then rebake. 12:03 relog: no pre-read and no item fetch here any more (both were done before, at the server's pace).
            int before = CofVer();
            it ??= await FetchItemRO(id, ct);
            if (it != null) { try { client.Appearance.RemoveFromOutfit(new List<InventoryItem> { it }); } catch { } }
            string rm = ""; bool still = true;
            for (int k = 0; k < 2 && still; k++)
            {
                rm += (k > 0 ? "; retry: " : "") + await CofLinksOffBatch(new[] { id }, "login veil off", ct);
                still = await Linked();
            }
            if (rm.Contains("no COF links to remove") && !LoginVeilWorn(id) && !still && CofVer() <= before) { Log("login-veil", $"veil not worn at {At()} ({why})"); return; }
            var (ok, v) = LoginVeil.OffVerdict(still, before, CofVer());
            if (ok) await RebakeNow("login veil off");
            Log("login-veil", ok ? $"veil off at {At()}: {why} ({v}; {rm})" : $"veil NOT off at {At()}: {v} ({rm}); trigger: {why}");
        }
        catch (Exception ex) { Log("login-veil", $"veil NOT off at {At()}: " + ex.GetBaseException().Message); }
    }
}
