// LoginVeil.cs (2026-10-30, David at the 10:36 relog: "I didn't see your base textures, but I did see your good body for a few seconds before your clothes covered you").
// Evidence (textclient.log, login 10:36:17): boots #7 and shorts #10 seen +3 s, real head +4 s, shorts "Ready" +5 s, BODY #40 and T-shirt #55 seen
// together at +7 s, T-shirt and boots "Ready" +10 s. The sim restores body and T-shirt in the same instant: the client has no order left to fix. What
// David saw is his viewer drawing the (cached) body before the T-shirt's mesh and the shorts' body cut arrived (the shorts were Ready at +5 s, before
// the body existed, so their cut had nobody to apply to). The only lever is to keep the body INVISIBLE until the garments are there and Ready:
// a body-only system alpha layer (the "veil": upper + lower body alpha, NOT the head) put on at logout, so the login bake already hides the
// system body AND the BOM mesh body from the first frame, then taken off (and rebaked) once every worn LaraX garment is seen and Ready.
// The veil item is named by its inventory item id in routes/_login-veil.txt (first non-comment line). No file / item: nothing changes.
// Safety: the veil is always taken off by MaxHold, and a veil found worn outside the login window is taken off too.
using LibreMetaverse;

namespace GalatayText;

internal static class LoginVeil
{
    internal sealed record Garment(string Name, double? SeenSec, double? ReadySec);
    internal static readonly double MaxHoldSec = 25, GraceSec = 1.5;

    /// <summary>Pure: the garments the veil waits for: LaraX-fitted clothing (it cuts the body and announces Ready).</summary>
    internal static bool Waits(string name) => LoginOrder.IsBodyCuttingGarment(name);

    /// <summary>Pure (tested): take the veil off now? Every waited garment of the current outfit is seen and Ready, plus a short grace for the
    /// viewer to draw it; nothing to wait for (purposely naked) releases at once; MaxHold always releases.</summary>
    internal static (bool release, string why) Release(IEnumerable<Garment> garments, double nowSec)
    {
        var g = (garments ?? Array.Empty<Garment>()).Where(x => Waits(x.Name)).ToList();
        if (nowSec >= MaxHoldSec) return (true, $"max hold {MaxHoldSec:F0} s reached; still waiting for: {string.Join("; ", g.Where(x => x.SeenSec == null || x.ReadySec == null).Select(x => x.Name))}");
        if (g.Count == 0) return (true, "no body-cutting garment in the outfit");
        var missing = g.Where(x => x.SeenSec == null || x.ReadySec == null).ToList();
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
            Log("login-veil", ok ? $"veil '{it.Name}' on for logout ({why}; {add}): the next login starts with the body hidden"
                                 : $"veil NOT on for logout: {why} ({add}); this login will show the body as before");
        }
        catch (Exception ex) { Log("login-veil", "veil on failed: " + ex.GetBaseException().Message); }
    }

    /// <summary>Login: take the veil off once the outfit's garments are attached and Ready (LoginVeil.Release).</summary>
    static void LoginVeilStart()
    {
        int gen = Interlocked.Increment(ref loginVeilGen); var t0 = DateTime.Now;
        var id = LoginVeilItem(); if (id == UUID.Zero) return;
        _ = Task.Run(async () =>
        {
            string why = "?";
            try
            {
                await Task.Delay(1000);
                using var cts = new CancellationTokenSource(60000); var ct = cts.Token;
                while (gen == loginVeilGen && LoggedIn)
                {
                    double now = (DateTime.Now - t0).TotalSeconds;
                    var garments = new List<LoginVeil.Garment>();
                    var cof = await CofFolder(ct);
                    if (cof != null)
                    {
                        var worn = WornPrims().Select(AttachItemId).ToHashSet();
                        foreach (var l in (await ReadFolderRO(cof.UUID, ct)).OfType<InventoryItem>().Where(l => l.ParentUUID == cof.UUID && l.IsLink() && (l.InventoryType == InventoryType.Object || l.InventoryType == InventoryType.Attachment)).GroupBy(l => l.AssetUUID).Select(g => g.First()))
                            garments.Add(new(l.Name, worn.Contains(l.AssetUUID) && loginSeenAt.TryGetValue(l.AssetUUID, out var s) ? (s - t0).TotalSeconds : worn.Contains(l.AssetUUID) ? 0 : null,
                                             loginReadyAt.TryGetValue(l.Name, out var r) ? (r - t0).TotalSeconds : null));
                    }
                    var (rel, w) = cof == null && now < LoginVeil.MaxHoldSec ? (false, "Current Outfit not readable yet") : LoginVeil.Release(garments, now);
                    why = w; if (rel) break;
                    await Task.Delay(300, ct);
                }
            }
            catch (Exception ex) { why = "error: " + ex.GetBaseException().Message; }
            finally { if (gen == loginVeilGen) await LoginVeilOff(id, t0, why); }
        });
    }

    static async Task LoginVeilOff(UUID id, DateTime t0, string why)
    {
        string At() => $"+{(DateTime.Now - t0).TotalSeconds:F1} s";
        try
        {
            if (!LoggedIn) { Log("login-veil", $"veil NOT off at {At()}: logged out ({why})"); return; }
            using var cts = new CancellationTokenSource(30000); var ct = cts.Token;
            var cof = await CofFolder(ct); if (cof == null) { Log("login-veil", $"veil NOT off at {At()}: Current Outfit not found ({why})"); return; }
            async Task<bool> Linked() => (await ReadFolderRO(cof.UUID, ct, 8000)).OfType<InventoryItem>().Any(l => l.ParentUUID == cof.UUID && l.IsLink() && l.AssetUUID == id);
            int CofVer() => (client.Inventory.Store[cof.UUID] as InventoryFolder)?.Version ?? cof.Version;
            // 11:48 relog: trusted RemoveFromOutfit, logged "veil off", but the COF link stayed (outfit_wear at 11:49 still removed it).
            // The COF link is the truth: check it (not only the wearables list), remove it ourselves, read back, then rebake.
            if (!LoginVeilWorn(id) && !await Linked()) { Log("login-veil", $"veil not worn at {At()} ({why})"); return; }
            int before = CofVer();
            var it = await FetchItemRO(id, ct);
            if (it != null) { try { client.Appearance.RemoveFromOutfit(new List<InventoryItem> { it }); } catch { } }
            string rm = ""; bool still = true;
            for (int k = 0; k < 2 && still; k++)
            {
                rm += (k > 0 ? "; retry: " : "") + await CofLinksOffBatch(new[] { id }, "login veil off", ct);
                still = await Linked();
            }
            var (ok, v) = LoginVeil.OffVerdict(still, before, CofVer());
            if (ok) await RebakeNow("login veil off");
            Log("login-veil", ok ? $"veil off at {At()}: {why} ({v}; {rm})" : $"veil NOT off at {At()}: {v} ({rm}); trigger: {why}");
        }
        catch (Exception ex) { Log("login-veil", $"veil NOT off at {At()}: " + ex.GetBaseException().Message); }
    }
}
