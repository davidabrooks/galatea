// SwapVeil.cs (2026-11-20, David t3443u/t3446u): "you changed from android to human without a veil, so the larger breasts of your android
// shape popped through the smaller shirt of your human outfit for a moment while you were changing shapes."
// Evidence (textclient.log, fast outfit 'random' -> 'TETRA Chill T-Shirt', 10:11:46-10:12:38 PT): the LaraX body and the T-shirt attach at
// 10:11:58 (shirt Ready 10:11:59) while the Kay pieces and the android shape are still on; the Kay pieces go at 10:12:04, the new shape and
// skin are baked for COF v3791 at 10:12:05 and the final bake (v3805) lands at 10:12:14. For those seconds the shirt is drawn over a body
// with the android shape.
// Now, on an android -> human change only (OutfitWearSafe: android body worn or an Android* outfit current, target outfit not Android* and
// bringing a human (non-Kay/CALISTA) mesh body), a BODY-ONLY system alpha (upper + lower body; head, eyes and hair stay visible) goes on
// and into the bake BEFORE any item comes off or goes on, and comes off only once the swap has settled:
//   every attachment the swap put on is worn, every one it took off is gone, every body-cutting garment (LaraX) is attached and said Ready
//   (or was silent ReadyTimeoutSec / KnownSilentTimeoutSec, the login veil's rules), the worn set has not changed for StableSec, the sim has
//   sent our appearance for the COF version the swap ended on (the bake), plus GraceSec. MaxSettleSec after the swap ends it comes off
//   regardless, with a WARNING naming what was still missing: the veil never sticks.
// The login veil item (routes/_login-veil.txt) is a FULL-avatar alpha since 2026-10-30 12:03 (head, eyes and hair masked too: hair, teeth, eyes
// and lashes are stashed at logout so nothing floats). Mid-session nothing is stashed, so that veil would leave eyes, teeth, hair and lashes
// floating; the swap veil is the body-only alpha: routes/_swap-veil.txt, else the original body-only login veil build of 2026-10-30 11:33
// (d87120d0, 'Galatea login veil (body alpha)'); 'veil create swap' makes a new one. Only an Alpha wearable with one of BodyVeilNames is used.
// Never saved into an outfit (OutfitSave excludes it), protected from alpha-follow / undress / RLV strips (OutfitProtectedIds), not removed
// by the swap's own layer pass, its COF link removed and read back at veil-off; a login finding it still linked/worn takes it off.
using LibreMetaverse;

namespace GalatayText;

internal static class SwapVeil
{
    internal const double OnConfirmMaxSec = 6, MaxSettleSec = 30, StableSec = 1.0, GraceSec = 0.5, TickSec = 0.2;
    internal static readonly UUID LegacyBodyVeil = new("d87120d0-e113-3763-83ee-0d682730f955");
    internal const string LegacyBodyVeilName = "Galatea login veil (body alpha)";

    /// <summary>Pure (tested): the swap veil item: routes/_swap-veil.txt's first id, else the original body-only veil build.</summary>
    internal static UUID ItemFrom(string fileText)
    {
        var id = fileText == null ? UUID.Zero : LoginVeil.ParseVeilFile(fileText);
        return id != UUID.Zero ? id : LegacyBodyVeil;
    }

    /// <summary>Pure (tested): only a body-only veil may be the swap veil (never the full-avatar login veil: eyes, teeth and hair would float).</summary>
    internal static bool IsBodyVeilName(string name) => name is LoginVeilAsset.SwapName or LegacyBodyVeilName;

    /// <summary>Pure (tested): does this outfit change get the swap veil? Only android -> human: she is in an android body (or an Android*
    /// outfit), the target is not an Android* outfit, and it brings a human mesh body (a body swap happens). Human -> human, human -> android,
    /// android -> android and outfits that keep the current body: no veil. Never while the login veil is on.</summary>
    internal static (bool veil, string why) Wanted(bool androidBodyWorn, string currentOutfit, string targetOutfit, IEnumerable<string> targetItemNames,
        bool veilConfigured, bool loginVeilActive)
    {
        bool androidNow = androidBodyWorn || Program.IsAndroidOutfit(currentOutfit);
        if (!androidNow) return (false, "not in an android body");
        if (Program.IsAndroidOutfit(targetOutfit)) return (false, $"'{targetOutfit}' is an android outfit");
        var fams = (targetItemNames ?? Enumerable.Empty<string>()).Select(Program.BodyFamily).Where(f => f != null).ToHashSet();
        if (fams.Any(Program.IsAndroidFamily)) return (false, $"'{targetOutfit}' brings an android body");
        if (fams.Count == 0) return (false, $"'{targetOutfit}' has no mesh body (the current body stays)");
        if (!veilConfigured) return (false, "no swap veil configured");
        if (loginVeilActive) return (false, "the login veil is on");
        return (true, $"android -> human ('{targetOutfit}', body {string.Join("/", fams.OrderBy(f => f))})");
    }

    /// <summary>One look at the swap while the veil is on. Seconds are since the veil went on.</summary>
    internal sealed record State(IReadOnlyList<LoginVeil.Garment> Garments, IReadOnlyList<string> NotYetOn, IReadOnlyList<string> NotYetOff,
        bool BakeDone, double SinceWornChangeSec, double? SwapEndSec, bool SwapFailed = false);

    /// <summary>Pure (tested): take the swap veil off now? See the header. A failed swap (exception) takes it off at once; MaxSettleSec after
    /// the swap ended it always comes off (timedOut = true, why says what was still missing).</summary>
    internal static (bool off, bool timedOut, string why) OffDecision(State s, double nowSec, ICollection<string> knownSilent = null)
    {
        if (s.SwapFailed) return (true, true, "the outfit change failed");
        if (s.SwapEndSec is not double end) return (false, false, "outfit change still running");
        var g = (s.Garments ?? Array.Empty<LoginVeil.Garment>()).Where(x => LoginVeil.Waits(x.Name)).ToList();
        double To(LoginVeil.Garment x) => knownSilent != null && knownSilent.Contains(x.Name) ? LoginVeil.KnownSilentTimeoutSec : LoginVeil.ReadyTimeoutSec;
        double? Done(LoginVeil.Garment x) => x.ReadySec ?? (x.SeenSec is double sn && nowSec >= sn + To(x) ? sn + To(x) : null);
        var waits = new List<string>();
        if (s.NotYetOn?.Count > 0) waits.Add("not on yet: " + string.Join("; ", s.NotYetOn));
        if (s.NotYetOff?.Count > 0) waits.Add("not off yet: " + string.Join("; ", s.NotYetOff));
        var missing = g.Where(x => x.SeenSec == null || Done(x) == null).ToList();
        if (missing.Count > 0) waits.Add("garments " + string.Join("; ", missing.Select(x => x.Name + (x.SeenSec == null ? " (not attached)" : " (not Ready)"))));
        if (!s.BakeDone) waits.Add("bake for the new outfit not confirmed by the sim");
        if (nowSec - end >= MaxSettleSec)
            return (true, waits.Count > 0, waits.Count > 0 ? $"max settle {MaxSettleSec:F0} s after the change reached; still waiting for: {string.Join(" | ", waits)}" : "settled at the max settle time");
        if (waits.Count > 0) return (false, false, string.Join(" | ", waits));
        if (s.SinceWornChangeSec < StableSec) return (false, false, "attachments still changing");
        double last = g.Count == 0 ? end : Math.Max(end, g.Max(x => Math.Max(x.SeenSec!.Value, Done(x)!.Value)));
        if (nowSec < last + GraceSec) return (false, false, "grace after the last Ready");
        return (true, false, $"settled: everything on and off, {(g.Count == 0 ? "no body-cutting garment" : $"{g.Count} garment(s) Ready by +{last:F1} s")}, bake confirmed");
    }

    /// <summary>Tested: poll the swap until OffDecision says off (it always does by MaxSettleSec after the swap ends, or at once on a failed swap).
    /// The clock and the tick are injected so the tests run without waiting.</summary>
    internal static async Task<(bool timedOut, string why, double atSec)> WaitSettled(Func<double> nowSec, Func<double, State> sample, Func<Task> tick,
        ICollection<string> knownSilent = null, Action<string> onWait = null)
    {
        string lastWait = null;
        while (true)
        {
            double now = nowSec();
            var (off, timedOut, why) = OffDecision(sample(now), now, knownSilent);
            if (off) return (timedOut, why, now);
            var key = System.Text.RegularExpressions.Regex.Replace(why, @"[\d.]+ s", "#");
            if (key != lastWait) { lastWait = key; onWait?.Invoke($"waiting at +{now:F1} s: {why}"); }
            await tick();
        }
    }

    /// <summary>Tested: the veil-off always runs, also when waiting for the settle throws.</summary>
    internal static async Task<string> OffAfter(Func<Task<(bool timedOut, string why, double atSec)>> wait, Func<string, bool, Task<string>> takeOff)
    {
        (bool timedOut, string why, double atSec) r;
        try { r = await wait(); }
        catch (Exception ex) { r = (true, "settle wait failed: " + ex.GetBaseException().Message, -1); }
        return await takeOff(r.atSec >= 0 ? $"{r.why} (+{r.atSec:F1} s)" : r.why, r.timedOut);
    }
}

public static partial class Program
{
    /// <summary>What OutfitWearSafeLocked has planned, handed to the swap veil before any item comes off or goes on.</summary>
    internal sealed record SwapPlan(IReadOnlyList<InventoryItem> Items, IReadOnlyList<(UUID Item, string Name)> Attach, IReadOnlyList<(UUID Item, string Name)> Detach);
    internal sealed class SwapVeilRun
    {
        public UUID Id, CofId; public InventoryItem Item; public DateTime T0; public string Outfit; public int SimAtOn; public bool Added;
        public List<(UUID Item, string Name)> Targets = new(), Attach = new(), Detach = new();
        public readonly System.Collections.Concurrent.ConcurrentDictionary<UUID, double> SeenSec = new();
        public volatile bool Done;
    }
    static volatile SwapVeilRun swapVeilRun;
    /// <summary>Every object's latest "Ready" owner-say (LoginOrderNoteChat); the login's loginReadyAt keeps only the first one since login.</summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> objectReadyAt = new();
    static UUID SwapVeilItem()
    {
        if (Env("GT_SWAP_VEIL", "1") == "0") return UUID.Zero;
        try { var f = Path.Combine(RouteDir, "_swap-veil.txt"); return SwapVeil.ItemFrom(File.Exists(f) ? File.ReadAllText(f) : null); } catch { return SwapVeil.LegacyBodyVeil; }
    }
    static int CofVersionNow(UUID cofId)
    {
        try { return cofId != UUID.Zero && client.Inventory.Store.Contains(cofId) && client.Inventory.Store[cofId] is InventoryFolder f ? f.Version : -1; } catch { return -1; }
    }

    /// <summary>OutfitWearSafeLocked's hook, after the plan and before any change: on an android -> human change, the body veil on and baked.
    /// Returns the run (the caller takes the veil off after the change, whatever happens), or null: no veil.</summary>
    static async Task<SwapVeilRun> SwapVeilOnIfWanted(string outfit, SwapPlan plan)
    {
        var id = SwapVeilItem(); bool androidBody = AndroidWorn();
        bool loginVeil = loginVeilOffAt == DateTime.MaxValue || LoginVeilWorn(LoginVeilItem());
        var (want, why) = SwapVeil.Wanted(androidBody, currentOutfit, outfit, plan.Items.Select(i => i.Name), id != UUID.Zero, loginVeil);
        if (!want) { if (androidBody || IsAndroidOutfit(currentOutfit)) Log("swap-veil", $"outfit '{outfit}': no swap veil ({why})"); return null; }
        var run = new SwapVeilRun { Id = id, T0 = DateTime.Now, Outfit = outfit, SimAtOn = simAttCof,
            Targets = plan.Items.Where(i => i is InventoryObject || i is InventoryAttachment).Select(i => (i.UUID, i.Name)).ToList(), Attach = plan.Attach.ToList(), Detach = plan.Detach.ToList() };
        try
        {
            using var cts = new CancellationTokenSource(15000);
            var it = await FetchItemRO(id, cts.Token);
            if (it is not InventoryWearable w || w.WearableType != WearableType.Alpha || !SwapVeil.IsBodyVeilName(it.Name))
            { Log("swap-veil", $"WARNING outfit '{outfit}': swap veil {id} not usable ({(it == null ? "not found" : $"'{it.Name}'")}; 'veil create swap' makes a body-only one): changing without a veil"); return null; }
            run.Item = it;
            var cof = await CofFolder(cts.Token); run.CofId = cof?.UUID ?? UUID.Zero;
            run.SimAtOn = simAttCof; swapVeilRun = run; SwapVeilWatchSeen(run);
            run.Added = true;
            if (!LoginVeilWorn(id)) client.Appearance.AddToOutfit(new List<InventoryItem> { it }, false);
            var add = await CofLinksOnBatch(new List<InventoryItem> { it }, null, "swap veil on", cts.Token);
            var bake = await RebakeNow("swap veil on");
            bool conf = false;
            for (int i = 0; i < SwapVeil.OnConfirmMaxSec * 10; i++) { if (LoginVeil.BakeConfirmed(run.SimAtOn, simAttCof)) { conf = true; break; } await Task.Delay(100); }
            Log("swap-veil", $"veil '{it.Name}' on before the change ({why}): {add}; {bake}; " + (conf ? $"sim appearance COF v{run.SimAtOn} -> v{simAttCof} at +{(DateTime.Now - run.T0).TotalSeconds:F1} s"
                : $"no sim appearance for a newer COF than v{run.SimAtOn} in {SwapVeil.OnConfirmMaxSec:F0} s (changing anyway)"));
        }
        catch (Exception ex)
        {
            Log("swap-veil", $"veil on failed for '{outfit}': {ex.GetBaseException().Message}" + (run.Added ? "; it comes off after the change anyway" : ""));
            if (!run.Added) { swapVeilRun = null; run.Done = true; return null; }
        }
        return run;
    }

    /// <summary>While the veil is on: when each outfit object is first seen worn (seconds since veil-on), for the silent-garment timeout.</summary>
    static void SwapVeilWatchSeen(SwapVeilRun run) => _ = Task.Run(async () =>
    {
        var deadline = DateTime.Now.AddSeconds(240);
        while (!run.Done && DateTime.Now < deadline)
        {
            try { var worn = WornPrims().Select(AttachItemId).ToHashSet(); double now = (DateTime.Now - run.T0).TotalSeconds; foreach (var t in run.Targets) if (worn.Contains(t.Item)) run.SeenSec.TryAdd(t.Item, now); } catch { }
            await Task.Delay(250);
        }
    });

    /// <summary>After the change (or its failure): wait for the settle (SwapVeil.OffDecision, capped), then take the veil off. Never throws.</summary>
    static async Task<string> SwapVeilOffAfterSettle(SwapVeilRun run, bool failed)
    {
        try
        {
            var silent = LoginSilentGarments(); var end = DateTime.Now; int cofEnd = CofVersionNow(run.CofId);
            HashSet<UUID> lastWorn = null; var changedAt = DateTime.Now; bool rebaked = false;
            double Now() => (DateTime.Now - run.T0).TotalSeconds;
            SwapVeil.State Sample(double now)
            {
                var worn = WornPrims().Select(AttachItemId).Where(x => x != UUID.Zero).ToHashSet();
                if (lastWorn == null || !worn.SetEquals(lastWorn)) { lastWorn = worn; changedAt = DateTime.Now; }
                foreach (var t in run.Targets) if (worn.Contains(t.Item)) run.SeenSec.TryAdd(t.Item, now);
                var garments = run.Targets.Where(t => LoginVeil.Waits(t.Name)).Select(t => new LoginVeil.Garment(t.Name,
                    worn.Contains(t.Item) && run.SeenSec.TryGetValue(t.Item, out var s) ? s : null,
                    objectReadyAt.TryGetValue(t.Name, out var r) && r >= run.T0 ? (r - run.T0).TotalSeconds : null)).ToList();
                bool bake = cofEnd > 0 ? simAttCof >= cofEnd : simAttCof > run.SimAtOn;
                // the change can end on a COF edit with no bake request after it (body-part links): ask once, so the bake signal comes
                if (!bake && !rebaked && !failed && (DateTime.Now - end).TotalSeconds >= 2) { rebaked = true; _ = RebakeNow("swap veil: bake for the final COF"); }
                return new SwapVeil.State(garments, run.Attach.Where(a => !worn.Contains(a.Item)).Select(a => a.Name).ToList(),
                    run.Detach.Where(d => worn.Contains(d.Item)).Select(d => d.Name).ToList(), bake, (DateTime.Now - changedAt).TotalSeconds, failed ? null : (end - run.T0).TotalSeconds, failed);
            }
            return await SwapVeil.OffAfter(
                () => SwapVeil.WaitSettled(Now, Sample, () => Task.Delay(TimeSpan.FromSeconds(SwapVeil.TickSec)), silent, w => Log("swap-veil", w)),
                async (why, timedOut) =>
                {
                    var (ok, msg) = await SwapVeilTakeOff(run.Id, run.Item, run.CofId);
                    if (!ok) { await Task.Delay(5000); (ok, msg) = await SwapVeilTakeOff(run.Id, run.Item, run.CofId); msg = "retry: " + msg; }
                    var line = $"veil {(ok ? "off" : "NOT off")} at +{Now():F1} s after '{run.Outfit}' ({(timedOut ? "WARNING timeout: " : "")}{why}): {msg}";
                    Log("swap-veil", (ok ? "" : "WARNING ") + line);
                    return "swap veil: " + line;
                });
        }
        catch (Exception ex) { Log("swap-veil", "WARNING veil off path failed: " + ex.GetBaseException().Message); return "swap veil: off path failed: " + ex.GetBaseException().Message; }
        finally { run.Done = true; if (swapVeilRun == run) swapVeilRun = null; }
    }

    /// <summary>The body veil off: the layer dropped, its COF link removed and read back (3 tries), then the bake. Returns ok + a report.</summary>
    static async Task<(bool ok, string msg)> SwapVeilTakeOff(UUID id, InventoryItem it, UUID cofId)
    {
        int simBefore = simAttCof;
        try
        {
            using var cts = new CancellationTokenSource(30000); var ct = cts.Token;
            if (cofId == UUID.Zero) cofId = (await CofFolder(ct))?.UUID ?? UUID.Zero;
            if (cofId == UUID.Zero) return (false, "Current Outfit not found");
            int before = CofVersionNow(cofId);
            try { client.Appearance.RemoveFromOutfit(new List<InventoryItem> { LoginVeil.LayerRecord(id, it, SafeWearables().FirstOrDefault(w => w.ItemID == id)?.WearableType) }); } catch { }
            string rm = ""; bool still = true;
            for (int k = 0; k < 3 && still; k++)
            {
                rm += (k > 0 ? "; retry: " : "") + await CofLinksOffBatch(new[] { id }, "swap veil off", ct);
                var (kids, okRead) = await ReadFolderTimed(cofId, ct, 5000);
                still = !okRead || kids.OfType<InventoryItem>().Any(l => l.ParentUUID == cofId && l.IsLink() && l.AssetUUID == id);
            }
            if (still) return (false, $"veil link still in Current Outfit (or not readable): {rm}");
            var (_, v) = LoginVeil.OffVerdict(false, before, CofVersionNow(cofId));
            var bake = await RebakeNow("swap veil off");
            bool conf = false;
            for (int i = 0; i < LoginVeil.ConfirmMaxSec * 10; i++) { if (LoginVeil.BakeConfirmed(simBefore, simAttCof)) { conf = true; break; } await Task.Delay(100); }
            return (true, $"{v}; {rm}; {bake}; " + (conf ? $"sim appearance COF v{simBefore} -> v{simAttCof}" : $"no sim appearance for a newer COF than v{simBefore} in {LoginVeil.ConfirmMaxSec:F0} s"));
        }
        catch (Exception ex) { return (false, "veil off failed: " + ex.GetBaseException().Message); }
    }

    /// <summary>Every login, once the login veil is done: a swap veil still worn or linked (a disconnect mid-change) comes off.</summary>
    static void SwapVeilLoginCleanup()
    {
        var id = SwapVeilItem(); if (id == UUID.Zero) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(5000);
                await Task.WhenAny(loginVeilRestoreTask ?? Task.CompletedTask, Task.Delay(180000));   // the login veil's own work first
                await Task.Delay(3000);
                if (!LoggedIn || swapVeilRun != null) return;
                using var cts = new CancellationTokenSource(20000);
                var cof = await CofFolder(cts.Token); if (cof == null) return;
                var (kids, ok) = await ReadFolderTimed(cof.UUID, cts.Token, 8000);
                bool linked = ok && kids.OfType<InventoryItem>().Any(l => l.ParentUUID == cof.UUID && l.IsLink() && l.AssetUUID == id);
                if (!linked && !LoginVeilWorn(id)) return;
                var (done, msg) = await SwapVeilTakeOff(id, await FetchItemRO(id, cts.Token), cof.UUID);
                Log("swap-veil", $"{(done ? "" : "WARNING ")}login: swap veil was still {(linked ? "linked in Current Outfit" : "worn")}: {(done ? "taken off" : "NOT taken off")}: {msg}");
            }
            catch (Exception ex) { Log("swap-veil", "login cleanup failed: " + ex.GetBaseException().Message); }
        });
    }
}
