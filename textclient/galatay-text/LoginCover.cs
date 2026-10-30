// LoginCover.cs (2026-10-28, David: "do not show the underlying face before the real face renders; never flash the body unless purposely naked").
// Evidence (textclient.log, 16:11:53 login): the sim restores ONE object per shared attach point itself; the Lelutka head AVALON shares point #12 with
// the Real Kisser, so it was left out ("13 already attached skipped, 1 to add"), and the library's add-missing pass only ran at +20 s (waits for the
// server bake, then for the attachment count to be unchanged for 4 s): the base face showed for ~19 s. The sim's own restore had finished by +6 s.
// This adds a fast path: once the restore has settled (count > 0 and unchanged for 1.5 s, at the latest after 12 s), attach every COVER item of the
// Current Outfit that is not worn yet (real head, body, hands, feet, skeleton; rank <= 1 of OutfitSendRank, plus the body add-on), in rank order, at
// once. The library pass later finds them worn and skips them. The sim restore order itself (boots / shorts / body / T-shirt) is not under our control:
// what the client can still do for the body is reported by LoginOrder ([login-order] lines).
// 2026-10-29 (21:34 login, David: "I saw the base face too long"): the fast path still WAITED for the sim restore to settle (+6.0 s) before
// attaching the head, although the sim never restores it (point #12 goes to the Real Kisser): head seen at +6.7 s, base face ~6 s plus the
// viewer's mesh load. Now the real head (EarlyPlan) is attached right at login, before the settle wait; the rest of the cover set keeps the wait.
// 2026-10-30 (10:19 login, David: "I briefly saw your base torso, then you removed it for a few seconds even though you were wearing a bikini top"):
// the same one-object-per-point rule hit the BODY: the Petite Add-on (the LaraX torso/chest piece) shares point #1 (Chest) with the bikini top, so
// the sim restored the bikini top (+0 s) and never the add-on; the cover pass only sent it after the settle (+9.8 s) and it rezzed at +27 s, while
// the body itself came last in the sim's own restore (+7 s): base torso first, then the body with its chest hidden and no add-on under the bikini.
// EarlyPlan now attaches the whole body set (real head, body, body add-on) at login; the rest of the cover set keeps the wait.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibreMetaverse;
namespace GalatayText;

internal static class LoginCover
{
    internal sealed record Item(UUID Id, string Name);
    internal static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(1500), MaxWait = TimeSpan.FromSeconds(12);

    /// <summary>Pure: what covers the system avatar. rank 0/1 of OutfitSendRank (body, head, skeleton, hands, feet), the body add-on and the Lelutka head set (head, eyes, teeth); never HUDs / AO / collar / bridge.</summary>
    internal static bool IsCover(string name, Func<string, int> rank)
        => !string.IsNullOrWhiteSpace(name) && (rank(name) <= 1 || (name.Contains("Add-on", StringComparison.OrdinalIgnoreCase) && name.Contains("Maitreya", StringComparison.OrdinalIgnoreCase))
                                                  || name.Contains("lel evox", StringComparison.OrdinalIgnoreCase))   // the Lelutka head set: head, eyes, teeth
           && rank(name) < 8;

    /// <summary>Pure (tested): the cover items still to attach, real head first. Same rank keeps the Current Outfit order; items kept off are skipped.</summary>
    internal static List<Item> Plan(IEnumerable<Item> cof, ISet<UUID> worn, Func<string, int> rank, Func<UUID, string, bool> exclude = null)
        => (cof ?? Enumerable.Empty<Item>())
            .Where(i => i.Id != UUID.Zero && !worn.Contains(i.Id) && IsCover(i.Name, rank) && !(exclude?.Invoke(i.Id, i.Name) ?? false))
            .GroupBy(i => i.Id).Select(g => g.First())
            .Select((i, idx) => (i, idx, r: PlanRank(i.Name, rank)))
            .OrderBy(x => x.r).ThenBy(x => x.idx).Select(x => x.i).ToList();

    /// <summary>Pure (tested): the cover items attached AT ONCE at login, without waiting for the sim restore: the real head, the mesh body and the
    /// body add-on (the sim restores one object per point: the head's point goes to the Real Kisser, the add-on's Chest point to the top worn, and
    /// the body itself comes late in the sim's order; ATTACHMENT_ADD keeps the Kisser and the top on). Eyes, teeth, hands, feet keep the wait.</summary>
    internal static List<Item> EarlyPlan(IEnumerable<Item> cof, ISet<UUID> worn, Func<string, int> rank, Func<UUID, string, bool> exclude = null)
        => Plan(cof, worn, rank, exclude).Where(i => PlanRank(i.Name, rank) < 0 || IsBodyPart(i.Name)).ToList();

    /// <summary>Pure: the Maitreya mesh body or its add-on (not the body HUD).</summary>
    internal static bool IsBodyPart(string name)
        => (name ?? "").StartsWith("Maitreya Mesh Body", StringComparison.OrdinalIgnoreCase) && !(name ?? "").Contains("HUD", StringComparison.OrdinalIgnoreCase);

    /// <summary>The real head before everything else (the base face is the most visible gap), then body, then the rest of the cover set.</summary>
    internal static int PlanRank(string name, Func<string, int> rank)
        => System.Text.RegularExpressions.Regex.IsMatch(name ?? "", @"\bHEAD\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase) && !(name ?? "").Contains("Kisser", StringComparison.OrdinalIgnoreCase) ? -1 : rank(name);

    /// <summary>Pure (tested): the sim's own attachment restore is done: something is worn and the count has not changed for <paramref name="quiet"/>, or <paramref name="max"/> passed.</summary>
    internal static bool RestoreSettled(int count, TimeSpan unchangedFor, TimeSpan elapsed, TimeSpan quiet, TimeSpan max)
        => elapsed >= max || (count > 0 && unchangedFor >= quiet);
}

public static partial class Program
{
    static int loginCoverGen;
    static volatile bool loginCoverBusy;   // the fast cover pass is running: the walk to David waits for it (LoginJoin.cs LoginWaitLoaded)

    static void LoginCoverStart()
    {
        int gen = Interlocked.Increment(ref loginCoverGen);
        if (Env("GT_LOGIN_COVER", "1") == "0") return;
        loginCoverBusy = true;
        _ = Task.Run(async () =>
        {
            try { await LoginCoverRun(gen); }
            catch (Exception ex) { Log("login-cover", "failed: " + ex.GetBaseException().Message); }
            finally { if (gen == loginCoverGen) loginCoverBusy = false; }
        });
    }

    static async Task LoginCoverRun(int gen)
    {
        var t0 = DateTime.UtcNow; int last = -1; var changed = t0;
        var early = new HashSet<UUID>();
        try { early = await LoginCoverEarlyHead(gen, t0); } catch (Exception ex) { Log("login-cover", "early head attach failed: " + ex.GetBaseException().Message); }
        while (gen == loginCoverGen && LoggedIn)
        {
            int n = WornPrims().Count(p => AttachItemId(p) != UUID.Zero); var now = DateTime.UtcNow;
            if (n != last) { last = n; changed = now; }
            if (LoginCover.RestoreSettled(n, now - changed, now - t0, LoginCover.Quiet, LoginCover.MaxWait)) break;
            await Task.Delay(250);
        }
        if (gen != loginCoverGen || !LoggedIn) return;
        using var cts = new CancellationTokenSource(45000); var ct = cts.Token;
        var cof = await CofFolder(ct); if (cof == null) { Log("login-cover", "Current Outfit folder not found"); return; }
        var links = (await ReadFolderRO(cof.UUID, ct)).OfType<InventoryItem>()
            .Where(l => l.ParentUUID == cof.UUID && l.IsLink() && (l.InventoryType == InventoryType.Object || l.InventoryType == InventoryType.Attachment)).ToList();
        var worn = WornPrims().Select(AttachItemId).Where(u => u != UUID.Zero).ToHashSet();
        worn.UnionWith(early);   // already requested at login: do not send it twice
        var plan = LoginCover.Plan(links.Select(l => new LoginCover.Item(l.AssetUUID, l.Name)), worn, OutfitSendRank, LoginVeil.HoldExclude(client.Settings.Agent.OutfitSendExclude, LoginVeilHeld));
        double since() => (DateTime.UtcNow - t0).TotalSeconds;
        if (plan.Count == 0) { Log("login-cover", $"restore settled at +{since():F1} s with {worn.Count} worn: every cover item (body, head, hands, feet) is already on"); return; }
        Log("login-cover", $"restore settled at +{since():F1} s with {worn.Count} worn: attaching {plan.Count} missing cover item(s) now, head first: {string.Join("; ", plan.Select(p => p.Name))}");
        var invs = new List<InventoryItem>();
        foreach (var p in plan) { var it = await FetchItemRO(p.Id, ct); if (it != null && !it.IsLink()) invs.Add(it); else Log("login-cover", $"'{p.Name}' {p.Id}: not returned by the inventory fetch, left to the library pass"); }
        foreach (var it in invs) client.Appearance.Attach(it, AttachmentPoint.Default, false);   // ATTACHMENT_ADD at the item's own saved point, in plan order
        var want = invs.Select(i => i.UUID).ToList();
        while (!ct.IsCancellationRequested && LoggedIn && since() < 30)
        {
            var have = WornPrims().Select(AttachItemId).ToHashSet();
            if (want.All(have.Contains)) { Log("login-cover", $"cover items on at +{since():F1} s: {string.Join("; ", invs.Select(i => i.Name))}"); return; }
            await Task.Delay(300);
        }
        var have2 = WornPrims().Select(AttachItemId).ToHashSet();
        Log("login-cover", $"after +{since():F1} s not seen: {string.Join("; ", invs.Where(i => !have2.Contains(i.UUID)).Select(i => i.Name))} (the library add-missing pass still covers them)");
    }

    /// <summary>Attach the real head and the body set right at login (before the restore-settle wait). Returns the item ids requested.</summary>
    static async Task<HashSet<UUID>> LoginCoverEarlyHead(int gen, DateTime t0)
    {
        var sent = new HashSet<UUID>();
        if (gen != loginCoverGen || !LoggedIn) return sent;
        using var cts = new CancellationTokenSource(15000); var ct = cts.Token;
        // 2026-10-30: at the 10:35 and 10:36 logins this pass logged nothing: the inventory root was not readable yet at +0 s and it returned
        // silently. Retry for a few seconds, and say so when it gives up.
        InventoryFolder cof = null;
        for (int i = 0; i < 20 && cof == null && gen == loginCoverGen && LoggedIn; i++) { try { cof = await CofFolder(ct); } catch (OperationCanceledException) { throw; } catch { } if (cof == null) await Task.Delay(250, ct); }
        if (cof == null) { Log("login-cover", $"early: Current Outfit folder not readable by +{(DateTime.UtcNow - t0).TotalSeconds:F1} s; the body set waits for the restore"); return sent; }
        var links = (await ReadFolderRO(cof.UUID, ct)).OfType<InventoryItem>()
            .Where(l => l.ParentUUID == cof.UUID && l.IsLink() && (l.InventoryType == InventoryType.Object || l.InventoryType == InventoryType.Attachment)).ToList();
        var worn = WornPrims().Select(AttachItemId).Where(u => u != UUID.Zero).ToHashSet();
        var plan = LoginCover.EarlyPlan(links.Select(l => new LoginCover.Item(l.AssetUUID, l.Name)), worn, OutfitSendRank, LoginVeil.HoldExclude(client.Settings.Agent.OutfitSendExclude, LoginVeilHeld));
        double since() => (DateTime.UtcNow - t0).TotalSeconds;
        if (plan.Count == 0) { Log("login-cover", $"early: real head and body already on at +{since():F1} s"); return sent; }
        foreach (var p in plan)
        {
            if (gen != loginCoverGen || !LoggedIn) break;
            var it = await FetchItemRO(p.Id, ct); if (it == null || it.IsLink()) continue;
            client.Appearance.Attach(it, AttachmentPoint.Default, false); sent.Add(it.UUID);
            Log("login-cover", $"early: attaching at +{since():F1} s, before the restore settles: {it.Name}");
        }
        return sent;
    }
}
