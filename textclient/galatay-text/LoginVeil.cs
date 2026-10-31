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
// 2026-10-30 12:37 relog (David: "collar first, then clothes, hair, and teeth. Then a number of seconds later your skin and face"):
// log: release decided at ~+6.3 s (every garment Ready by +5.8 s), COF link removal logged only at 12:37:36 (+16 s), bake at 12:37:38 (+17.5 s).
// Root cause: LoginVeilOff did THREE more server inventory reads before the bake (CofFolder = a root folder read, then CofLinksOffBatch's own
// CofFolder + full COF read, then a COF read-back), each queued behind the login-cover / outfit-send / stash inventory traffic (~3 s each at
// login), and only THEN requested the bake. Now the release loop's own COF read keeps the veil's link ids, so the off path is: drop the layer
// from the library's wearables, delete the known link(s), request the bake: no read before the bake (OffCore, tested). The read-back runs
// after the bake request and only re-does the slow path if the link is still there.
// Also: hair / teeth / eyes were attached BEFORE the veil came off (and login-cover attached eyes + teeth at +5.8 s): they floated. Now they
// are held (login-cover and the library add-missing pass skip them) until the sim sends our AvatarAppearance for the new COF version (the
// veil-off bake applied), then attached. The collar is put in Stealth at logout (its own About menu) and shown again after that bake.
using LibreMetaverse;
using LibreMetaverse.StructuredData;

namespace GalatayText;

internal static class LoginVeil
{
    internal sealed record Garment(string Name, double? SeenSec, double? ReadySec);
    internal static readonly double MaxHoldSec = 25, GraceSec = 0.5, MaxHoldStashedSec = 90, RetryAttachSec = 8, OutfitFallbackSec = 25;
    internal sealed record Stashed(UUID Item, int Point, string Name);
    internal static readonly double ConfirmMaxSec = 12, ReadyTimeoutSec = 8;
    internal const int CollarStealthMaxMs = 20000, EvoxHideMaxMs = 25000;

    /// <summary>Pure-ish (tested with fakes): the veil-off order. Layer out of the library's wearables, known COF link(s) deleted, bake requested,
    /// and only then the (slow, at login) confirmation. Returns whether the links call succeeded and whether the bake was confirmed.
    /// The head-parts burst (attach requests, no waiting) goes between the link removal and the bake request, so face, hair and skin land together.</summary>
    internal static async Task<(bool removed, bool confirmed)> OffCore(Action dropLayer, Func<Task<bool>> removeKnownLinks, Func<Task> rebake, Func<Task<bool>> confirm, Func<Task> burst = null)
    {
        try { dropLayer?.Invoke(); } catch { }
        bool removed = await removeKnownLinks();
        if (!removed) return (false, false);
        if (burst != null) { try { await burst(); } catch { } }
        await rebake();
        return (true, await confirm());
    }

    /// <summary>Pure-ish (tested with fakes): the logout order. Collar Stealth first (to its end, capped), then the veil; the stash only when the
    /// veil is verified on. 14:01 relog (David): the collar disappeared last, because the stealth ran beside the veil and the stash.</summary>
    internal static async Task<bool> LogoutCore(Func<Task> collarStealth, int collarMaxMs, Func<Task<bool>> veilOn, Func<Task> stash, Func<Task> looseHeadPartsFirst = null,
                                                Func<Task> hideHeadFaces = null, int hideHeadFacesMaxMs = EvoxHideMaxMs)
    {
        // 15:31:45 logout (David): hair, teeth and earrings still floated alone at the end although our client got their KillObject 1.4 s before
        // the head's. Observers get those kills later than we do, so they now come off FIRST, while the head and body are still fully shown;
        // collar stealth and the veil bake (~6 s at 15:31:36-42) then run before the head and clothes go: a late kill shows a bald, whole avatar.
        // 16:48 relog (David): eyes and hair came off ~19 s before anything else (hair wave gone 16:48:36, EvoX ear/lash HUD step until
        // 16:48:49, collar stealth until 16:48:52, veil bake 16:48:53). The slow steps (head HUD, collar menu) now run first, with the head
        // still fully dressed; the hair wave goes right before the veil, so the bald moment is the veil bake only (~1 s).
        // Ears and lashes (EvoX head faces) are hidden through the head's HUD, capped so a stuck HUD never holds the logout.
        if (hideHeadFaces != null) { try { await Task.WhenAny(hideHeadFaces(), Task.Delay(hideHeadFacesMaxMs)); } catch { } }
        if (collarStealth != null) { try { await Task.WhenAny(collarStealth(), Task.Delay(collarMaxMs)); } catch { } }
        // 15:31:45 logout: hair, teeth and earrings floated alone at the end (observers get their kills late), so they still come off
        // before the veil and the head / clothes stash.
        if (looseHeadPartsFirst != null) { try { await looseHeadPartsFirst(); } catch { } }
        bool ok = await veilOn();
        if (ok) await stash();
        return ok;
    }

    /// <summary>Pure-ish (tested with fakes): start the stashed clothes' attach beside the release loop, never in front of it. 14:01 relog: the
    /// awaited attach waited ~14 s on inventory fetches, so the veil decision came at +15.5 s for clothes Ready at +12.3 s.</summary>
    internal static async Task<T> BesideLoop<T>(Func<Task> attach, Func<Task<T>> loop)
    {
        if (attach != null) _ = Task.Run(async () => { try { await attach(); } catch { } });
        return await loop();
    }

    /// <summary>Pure (tested): the veil-off bake is applied once the sim sends our AvatarAppearance for a COF version newer than before the removal.</summary>
    internal static bool BakeConfirmed(int simCofBefore, int simCofNow) => simCofNow > simCofBefore;

    /// <summary>Pure (tested): the cover / add-missing exclude while the veil is on: the base exclude, plus head parts held for after the veil-off bake.</summary>
    internal static Func<UUID, string, bool> HoldExclude(Func<UUID, string, bool> baseExclude, Func<UUID, bool> held)
        => (id, name) => (held?.Invoke(id) ?? false) || (baseExclude?.Invoke(id, name) ?? false);

    /// <summary>Pure (tested): show the collar again at login? Only when it was visible before the logout stealth (an Android outfit keeps it hidden).</summary>
    internal static bool CollarShowAfterLogin(string marker) => (marker ?? "").Trim() == "show";
    internal static string CollarMarker(bool? wasOnBeforeLogout, string result) => wasOnBeforeLogout == false && (result ?? "").Contains("now invisible") ? "show" : "keep";

    /// <summary>Pure (tested): take this attachment off at logout while the veil hides the body? Not HUDs (points 31-38), not the mesh body / add-on,
    /// not the real head, hands, feet or skeleton (rank &lt;= 1, under the alpha), not HUD / AO / collar / bridge (rank 8), not unnamed.
    /// 2026-10-30 14:01 relog: lashes floated at login. They are a non-BOM face of the Lelutka head (and the nails a non-BOM face of the
    /// Maitreya body), which no alpha layer hides, so the real head and the Kisser lips are stashed too (the system head under the full
    /// alpha is invisible) and come back in the veil-off moment. Generic: everything on a body point except the body / add-on (the clothes
    /// need it to cut and announce Ready), bento hands / feet / skeleton, and rank 8 (HUD / AO / collar / bridge / RLV relay).</summary>
    internal static bool ShouldStash(string name, int point)
    {
        if (string.IsNullOrWhiteSpace(name) || (point >= 31 && point <= 38)) return false;
        if (IsHeadEyes(name, point)) return false;   // 17:03 (David): the head handles the eyes itself
        if (LoginOrder.IsBody(name) || LoginCover.IsBodyPart(name)) return false;
        int r = Program.OutfitSendRank(name);
        if (r == 0 || r >= 8) return false;
        if (r == 1) return false;   // 15:16:48 relog (David): base face for ~4 s. The real head (and the lips on it) is NEVER stashed: see HeadGate
        return true;
    }

    /// <summary>Pure (tested): the base face must never show. 2026-10-30 15:16:48 relog: the stashed real head was sent with the veil-off burst at
    /// +9.0 s, the bake without the veil landed at +17.2 s, the head only rezzed at +21 s: the system head was visible ~4 s. The veil now never comes
    /// off while a real head of the outfit (COF links or stash) is not attached; past the stash hold it STAYS on (KEEP), never a bare base face.</summary>
    internal static (bool release, string why) ReleaseWithHead(IEnumerable<Garment> garments, double nowSec, bool stashed, IEnumerable<string> headsNotAttached)
    {
        var miss = (headsNotAttached ?? Array.Empty<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (miss.Count == 0) return Release(garments, nowSec, stashed);
        if (nowSec >= MaxHoldStashedSec) return (false, $"KEEP: real head not attached after {MaxHoldStashedSec:F0} s ({string.Join("; ", miss)}); the veil stays on (never the base face)");
        return (false, "waiting for the real head: " + string.Join("; ", miss) + " (not attached)");
    }
    /// <summary>Pure (tested): the real heads expected (by item) that are not worn yet.</summary>
    internal static List<string> HeadsNotAttached(IEnumerable<(UUID Item, string Name)> expected, ISet<UUID> worn)
        => (expected ?? Enumerable.Empty<(UUID, string)>()).Where(x => LoginOrder.IsRealHead(x.Name) && !(worn?.Contains(x.Item) ?? false)).Select(x => x.Name).Distinct().ToList();

    /// <summary>Pure (tested): the logout detach waves. 2026-10-30 14:56:49 and 14:59:14 logouts (David): hair and teeth floated by themselves after
    /// everything else vanished; all 9 detaches went out in one burst, in worn order (head before hair at 14:56:49), and the logout followed in the same
    /// second. Wave 1 = the small head parts (hair, teeth, eyes, earrings, lips: what floats without the head), wave 2 = the real head and the clothes.
    /// Each wave is confirmed gone (and settled) before the next, and the last one before the logout.</summary>
    internal static List<List<Stashed>> LogoutWaves(IEnumerable<Stashed> stash)
    {
        var all = (stash ?? Enumerable.Empty<Stashed>()).ToList();
        var w1 = all.Where(x => IsLooseHeadPart(x.Name)).ToList();
        var w2 = all.Where(x => !IsLooseHeadPart(x.Name)).ToList();
        return new[] { w1, w2 }.Where(w => w.Count > 0).ToList();
    }
    /// <summary>Pure (tested): a head part that would float on its own once the head is gone (not the real head itself).</summary>
    internal static bool IsLooseHeadPart(string name) => !LoginOrder.IsRealHead(name) && RevealWithVeil(name);
    internal const int WaveConfirmMs = 4000, WaveSettleMs = 1200, LogoutSettleMs = 2500, EarlySettleMs = 0;   // the veil follows the hair wave at once (16:48 relog)
    /// <summary>Pure (tested): the early stash (loose head parts, taken before the veil) merged with the late one, by item, early first.</summary>
    internal static List<Stashed> MergeStash(IEnumerable<Stashed> early, IEnumerable<Stashed> late)
        => (early ?? Enumerable.Empty<Stashed>()).Concat(late ?? Enumerable.Empty<Stashed>()).GroupBy(x => x.Item).Select(g => g.First()).ToList();
    /// <summary>Pure-ish (tested with fakes): detach wave by wave; wait until every item of a wave is no longer worn (bounded) plus a settle, so the
    /// observers' viewers get the kills in that order, then the next wave; the settle after the last wave keeps the logout from racing the kills.
    /// Returns one log line per wave with its ms offset.</summary>
    internal static async Task<List<string>> DetachInWaves(List<List<Stashed>> waves, Action<Stashed> detach, Func<HashSet<UUID>> worn,
        int confirmMs = WaveConfirmMs, int settleMs = WaveSettleMs, int finalSettleMs = LogoutSettleMs)
    {
        var log = new List<string>(); var t0 = DateTime.Now;
        for (int i = 0; i < waves.Count; i++)
        {
            var w = waves[i]; foreach (var x in w) detach(x);
            var sent = (DateTime.Now - t0).TotalMilliseconds; bool gone = false;
            for (int t = 0; t <= confirmMs; t += 100) { var on = worn(); if (!w.Any(x => on.Contains(x.Item))) { gone = true; break; } await Task.Delay(100); }
            var conf = (DateTime.Now - t0).TotalMilliseconds;
            log.Add($"wave {i + 1}/{waves.Count} sent +{sent:F0} ms, {(gone ? "confirmed gone" : "NOT all gone")} +{conf:F0} ms: {string.Join("; ", w.Select(x => x.Name))}");
            await Task.Delay(i == waves.Count - 1 ? finalSettleMs : settleMs);
        }
        return log;
    }

    /// <summary>Pure (tested): the real head or a head add-on (Kisser lips): faces the alpha cannot hide (lashes), shown with the veil-off bake.</summary>
    internal static bool IsHeadPart(string name)
        => LoginOrder.IsRealHead(name) || (name ?? "").Contains("Kisser", StringComparison.OrdinalIgnoreCase);

    /// <summary>Pure (tested): the order the veil-off burst attaches in: real head first (no headless frame), then the lips, hair, eyes, teeth, the rest.</summary>
    internal static int RevealOrder(string name)
        => LoginOrder.IsRealHead(name) ? 0 : IsHeadPart(name) ? 1 : Program.OutfitSendRank(name) == 2 ? 2 : 3;

    /// <summary>Pure (tested): put this stashed item back only together with the veil-off bake (hair, teeth, eyes, jewellery), not at login.
    /// Never the real head: since #353 the veil waits for the head, so a stashed head held for veil-off deadlocked the 15:32 login (veil KEPT at
    /// +90 s). A real head found in the stash goes with the clothes.</summary>
    internal static bool RevealWithVeil(string name)
        => !Waits(name) && !LoginOrder.IsRealHead(name) && (IsHeadPart(name) || Program.OutfitSendRank(name) == 2 || System.Text.RegularExpressions.Regex.IsMatch(name ?? "", @"teeth|eyes?\b|lel evox|earring|necklace|piercing|nails|lashes", System.Text.RegularExpressions.RegexOptions.IgnoreCase));

    /// <summary>Pure (tested): the record to attach a stashed item from, with NO inventory fetch. 14:38 relog: FetchInventory2 was not
    /// available yet at login, so every stash fetch hung (~10 s): the clothes only came on at +10.5 s (through the library's outfit send, not
    /// the stash), and the 4 head parts were "not prefetched yet" at veil-off. RezSingleAttachmentFromInv only needs the item id (the sim
    /// looks the item up in our own inventory); the name rides along for the log. A cached, non-link record is used when there is one.</summary>
    internal static InventoryItem AttachRecord(Stashed x, UUID owner, InventoryItem known)
        => known != null && !known.IsLink() && known.UUID == x.Item ? known : new InventoryObject(x.Item) { Name = x.Name ?? "", OwnerID = owner, Description = "" };

    /// <summary>Pure (tested): what the release loop waits on: the COF links when read; before that, the stash's clothes if any of them
    /// is a waited (body-cutting) garment; else null (not known yet).</summary>
    internal static List<(UUID Item, string Name)> WaitSource(List<(UUID Item, string Name)> cofLinks, List<(UUID Item, string Name)> stashedClothes)
        => cofLinks ?? (stashedClothes != null && stashedClothes.Any(x => Waits(x.Name)) ? stashedClothes : null);

    /// <summary>Pure (tested): the record that drops the veil from the library's worn layers. 14:38 relog: the veil item fetch had not
    /// finished (FetchInventory2 not up yet), so the layer drop was skipped: the COF link and the bake were right (body visible), but the
    /// library still listed the veil as worn (rlv_layers showed it). Now a fetched wearable is used, else one built from the worn layer.</summary>
    internal static InventoryItem LayerRecord(UUID id, InventoryItem fetched, WearableType? worn)
        => fetched is InventoryWearable ? fetched : new InventoryWearable(id) { AssetType = AssetType.Clothing, WearableType = worn ?? WearableType.Alpha, Name = "login veil" };

    internal static string FormatStash(IEnumerable<Stashed> s, DateTime at)
        => $"# login veil stash {at:yyyy-MM-dd HH:mm:ss}: attachments taken off at logout (COF links kept); re-attached at login\n"
           + string.Concat(s.Select(x => $"{x.Item}\t{x.Point}\t{(x.Name ?? "").Replace('\t', ' ').Replace('\n', ' ')}\n"));
    /// <summary>Pure (tested): mesh eyes (eyeball points or an 'eyes' item). 2026-10-30 17:03 (David): my head handles the eyes automatically, so
    /// the veil flow leaves them alone: never stashed or detached at logout, never held or re-attached at login (old stash lines are dropped).</summary>
    internal static bool IsHeadEyes(string name, int point)
        => point == 15 || point == 16 || System.Text.RegularExpressions.Regex.IsMatch(name ?? "", @"\beyes\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    internal static List<Stashed> ParseStash(string text)
    {
        var r = new List<Stashed>();
        foreach (var l in (text ?? "").Split('\n'))
        {
            if (l.StartsWith("#")) continue; var f = l.TrimEnd('\r').Split('\t');
            if (f.Length >= 3 && UUID.TryParse(f[0], out var id) && id != UUID.Zero && int.TryParse(f[1], out var pt) && !IsHeadEyes(f[2], pt) && r.All(x => x.Item != id)) r.Add(new(id, pt, f[2]));
        }
        return r;
    }

    /// <summary>Pure: the garments the veil waits for: LaraX-fitted clothing (it cuts the body and announces Ready).</summary>
    internal static bool Waits(string name) => LoginOrder.IsBodyCuttingGarment(name) && !IsBodyAddOn(name);

    // 2026-10-30 16:24 login (David: my head, hair and teeth floating, plus boots and 'The V', body veiled ~90 s): the V and the nipple rings
    // were sent with the clothes at +1 s (they float on the veiled skin), and the veil waited for their "Ready" that never comes.
    /// <summary>Pure (tested): a body add-on that sits on the skin (genitals, nipple rings, piercings): not clothing, held until veil-off, never gated on.</summary>
    internal static bool IsBodyAddOn(string name)
        => System.Text.RegularExpressions.Regex.IsMatch(name ?? "", @"^The V\b|\bThe V - |nipple|genital|vagina|pubic|piercing|belly ?ring|Session Skins|ASA Studios|\bAdd-on\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    /// <summary>Pure (tested): footwear (boots, shoes, heels...).</summary>
    internal static bool IsFootwear(string name)
        => System.Text.RegularExpressions.Regex.IsMatch(name ?? "", @"boots?\b|shoes?\b|heels?\b|sandals?\b|sneakers?\b|pumps?\b|flats\b|loafers?\b|slippers?\b|stilettos?\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    /// <summary>Pure (tested): jewellery and small accessories (bracelet, rings, anklets...): float on a veiled body, so held.</summary>
    internal static bool IsJewellery(string name)
        => System.Text.RegularExpressions.Regex.IsMatch(name ?? "", @"bracelet|bangle|anklet|\brings?\b|necklace|choker|earrings?|pendant|watch\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    /// <summary>Pure (tested): a real body-covering garment (top, bottom, dress...): what makes "dressed".</summary>
    internal static bool IsCoveringGarment(string name)
        => !string.IsNullOrWhiteSpace(name) && !IsBodyAddOn(name) && !IsFootwear(name) && !IsJewellery(name) && !LoginOrder.IsRealHead(name) && !IsHeadPart(name)
           && !RevealWithVeil(name) && (LoginOrder.IsBodyCuttingGarment(name) || Program.OutfitSendRank(name) is > 1 and < 8);

    /// <summary>Pure (tested): put this stashed item on only with the veil-off bake? Head parts (hair, eyes, teeth, earrings), body add-ons and
    /// jewellery always; and EVERYTHING (boots too) when the stash has no real body-covering garment: undressed, boots would float alone.
    /// Never the real head (the veil waits for it: it goes at login).</summary>
    internal static bool HeldForVeilOff(string name, bool stashHasCovering)
        => !LoginOrder.IsRealHead(name) && (RevealWithVeil(name) || IsBodyAddOn(name) || IsJewellery(name) || !stashHasCovering);
    internal static (List<Stashed> now, List<Stashed> held) SplitStash(IEnumerable<Stashed> stash)
    {
        var s = (stash ?? Enumerable.Empty<Stashed>()).ToList(); bool cov = s.Any(x => IsCoveringGarment(x.Name));
        return (s.Where(x => !HeldForVeilOff(x.Name, cov)).ToList(), s.Where(x => HeldForVeilOff(x.Name, cov)).ToList());
    }
    /// <summary>Pure (tested): held items found worn before veil-off (sim restore of a bad logout, login-cover, add-missing, the +25 s fallback, any
    /// path): to take off again at once, COF links kept.</summary>
    internal static List<UUID> HeldWornEarly(IEnumerable<UUID> held, ISet<UUID> worn)
        => (held ?? Enumerable.Empty<UUID>()).Where(x => worn?.Contains(x) ?? false).Distinct().ToList();

    /// <summary>Pure (tested): take the veil off now? Every waited garment of the current outfit is seen and Ready, plus a short grace for the
    /// viewer to draw it; nothing to wait for (purposely naked) releases at once; MaxHold always releases.</summary>
    internal static (bool release, string why) Release(IEnumerable<Garment> garments, double nowSec, bool stashed = false)
    {
        var g = (garments ?? Array.Empty<Garment>()).Where(x => Waits(x.Name)).ToList();
        // 16:24 login: '[BB] Nipple Rings' never said Ready and held the veil 90 s. A garment seen but silent for ReadyTimeoutSec counts as drawn.
        double? Done(Garment x) => x.ReadySec ?? (x.SeenSec is double sn && nowSec >= sn + ReadyTimeoutSec ? sn + ReadyTimeoutSec : null);
        var missing = g.Where(x => x.SeenSec == null || Done(x) == null).ToList();
        double hold = stashed ? MaxHoldStashedSec : MaxHoldSec;
        if (nowSec >= hold)
        {   // with a stash the body under the veil is bare until the clothes are back: stay invisible rather than nude
            if (stashed && g.Any(x => x.SeenSec == null)) return (false, $"KEEP: clothes taken off at logout not back after {hold:F0} s ({string.Join("; ", g.Where(x => x.SeenSec == null).Select(x => x.Name))}); the veil stays on");
            return (true, $"max hold {hold:F0} s reached; still waiting for: {string.Join("; ", missing.Select(x => x.Name))}");
        }
        if (g.Count == 0) return (true, "no body-cutting garment in the outfit");
        if (missing.Count > 0) return (false, "waiting for: " + string.Join("; ", missing.Select(x => x.Name + (x.SeenSec == null ? " (not attached)" : " (not Ready)"))));
        double last = g.Max(x => Math.Max(x.SeenSec!.Value, Done(x)!.Value));
        return nowSec >= last + GraceSec ? (true, $"every garment attached and Ready (or silent {ReadyTimeoutSec:F0} s) by +{last:F1} s") : (false, "grace after the last Ready");
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
    internal static DateTime loginVeilOffAt = DateTime.MinValue;   // MaxValue: veil on (login); MinValue: no veil this login
    // head parts (hair, teeth, eyes, jewellery) held off until the veil-off bake is confirmed: login-cover and the add-missing pass skip them
    static readonly System.Collections.Concurrent.ConcurrentDictionary<UUID, byte> loginVeilHeld = new();
    internal static bool LoginVeilHeld(UUID id) => !loginVeilHeld.IsEmpty && loginVeilHeld.ContainsKey(id);
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
        int gen0 = Volatile.Read(ref loginVeilGen);
        // 14:01 relog (David): the collar vanished LAST. Stealth now runs to the end (capped) BEFORE the veil and the stash; the collar's
        // alpha is saved with it on logout, so it also rezzes hidden at the next login.
        loginVeilEarlyStash = new();
        await LoginVeil.LogoutCore(LoginVeilCollarStealthForLogout, LoginVeil.CollarStealthMaxMs, LoginVeilOnVerified, LoginVeilStashForLogout, LoginVeilLooseHeadPartsFirst, LoginVeilEvoxHideForLogout);
        // the logout hold must never outlive an aborted logout: the outfit send would skip those items for the rest of the session
        _ = Task.Run(async () => { await Task.Delay(120000); if (LoggedIn && Volatile.Read(ref loginVeilGen) == gen0) loginVeilHeld.Clear(); });
    }

    static List<LoginVeil.Stashed> loginVeilEarlyStash = new();
    static List<LoginVeil.Stashed> LoginVeilStashCandidates(List<Primitive> prims)
    {
        var always = AlwaysWearIds();
        return prims.Select(p => new LoginVeil.Stashed(AttachItemId(p), (int)p.PrimData.AttachmentPoint, p.Properties?.Name ?? ""))
                    .Where(x => LoginVeil.ShouldStash(x.Name, x.Point) && !always.Contains(x.Item)).GroupBy(x => x.Item).Select(g => g.First()).ToList();
    }
    /// <summary>Logout step 0: hair, teeth, eyes, earrings off while the head and body are fully visible (stash file written first).</summary>
    static async Task LoginVeilLooseHeadPartsFirst()
    {
        if (Env("GT_VEIL_STASH", "1") == "0" || !LoggedIn || LoginVeilItem() == UUID.Zero) return;
        try
        {
            var prims = WornPrims().Where(p => AttachItemId(p) != UUID.Zero).ToList();
            var noProps = prims.Where(p => p.Properties == null).ToList();
            if (noProps.Count > 0) { try { await Task.WhenAny(EnsureProperties(client.Network.CurrentSim, noProps), Task.Delay(3000)); } catch { } }
            var loose = LoginVeilStashCandidates(prims).Where(x => LoginVeil.IsLooseHeadPart(x.Name)).ToList();
            if (loose.Count == 0) return;
            var tmp = LoginVeilStashFile + ".tmp";
            File.WriteAllText(tmp, LoginVeil.FormatStash(loose, DateTime.Now)); File.Move(tmp, LoginVeilStashFile, true);
            loginVeilEarlyStash = loose;
            // 16:23:52 logout: the early wave was confirmed gone, then the veil bake's after-bake outfit send (add-missing, COF links kept)
            // put hair, teeth, eyes and earrings straight back at 16:24:03. Held items are excluded from that send from here to the logout.
            loginVeilHoldTries.Clear(); foreach (var x in loose) loginVeilHeld[x.Item] = 1;
            var l = await LoginVeil.DetachInWaves(new List<List<LoginVeil.Stashed>> { loose }, x => DetachItem(x.Item, "login veil stash, early"),
                () => WornPrims().Select(AttachItemId).ToHashSet(), finalSettleMs: LoginVeil.EarlySettleMs);
            foreach (var x in l) Log("login-veil", "stash early (head still on, before collar/veil) " + x);
        }
        catch (Exception ex) { Log("login-veil", "early head-part stash failed: " + ex.GetBaseException().Message); }
    }

    static async Task<bool> LoginVeilOnVerified()
    {
        var id = LoginVeilItem(); if (id == UUID.Zero || !LoggedIn) return false;
        try
        {
            using var cts = new CancellationTokenSource(20000);
            var it = await FetchItemRO(id, cts.Token); if (it == null) { Log("login-veil", $"veil item {id} not found in inventory"); return false; }
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
            // the stash (LogoutCore) only once the veil is verified: never take clothes off over a visible body
            Log("login-veil", ok ? $"veil '{it.Name}' on for logout ({why}; {add}): the next login starts with the body hidden"
                                 : $"veil NOT on for logout: {why} ({add}); this login will show the body as before");
            return ok;
        }
        catch (Exception ex) { Log("login-veil", "veil on failed: " + ex.GetBaseException().Message); return false; }
    }

    static string LoginVeilCollarFile => Env("GT_VEIL_COLLAR_FILE", "/home/box/viewers/textclient/run/login-veil-collar.txt");
    /// <summary>Logout: the collar's own Stealth (About menu, read back), not a detach: it runs RLV. The marker says whether to show it at login.</summary>
    static async Task LoginVeilCollarStealthForLogout()
    {
        if (Env("GT_VEIL_COLLAR", "1") == "0" || !LoggedIn) return;
        try
        {
            if (!WornPrims().Any(p => AttachItemId(p) == N9CollarItem)) { Log("login-veil", "collar: not worn, nothing to hide"); return; }
            var (r, wasOn) = await CollarStealthSet(true, "logout veil");
            var m = LoginVeil.CollarMarker(wasOn, r);
            File.WriteAllText(LoginVeilCollarFile, m + "\n");
            Log("login-veil", $"collar for logout: {r} (was {(wasOn == null ? "unknown" : wasOn.Value ? "already hidden" : "visible")}; login: {(m == "show" ? "show it after the veil-off bake" : "leave as is")})");
        }
        catch (Exception ex) { Log("login-veil", "collar stealth for logout failed: " + ex.GetBaseException().Message); }
    }
    static async Task LoginVeilCollarShowAfterLogin()
    {
        try
        {
            var f = LoginVeilCollarFile; if (!File.Exists(f)) return;
            var m = File.ReadAllText(f); File.Delete(f);
            if (!LoginVeil.CollarShowAfterLogin(m) || !LoggedIn) return;
            var (r, _) = await CollarStealthSet(false, "login veil off");
            Log("login-veil", "collar after the veil-off bake: " + r);
        }
        catch (Exception ex) { Log("login-veil", "collar show after login failed: " + ex.GetBaseException().Message); }
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
            var late = LoginVeilStashCandidates(prims);
            var stash = LoginVeil.MergeStash(loginVeilEarlyStash, late);   // as worn; the usual outfit is chosen at login (LoginVeilUsual.cs)
            await LoginVeilUsualSave();
            if (stash.Count == 0) { Log("login-veil", "stash: nothing the veil cannot hide is worn"); return; }
            var tmp = LoginVeilStashFile + ".tmp";
            File.WriteAllText(tmp, LoginVeil.FormatStash(stash, DateTime.Now)); File.Move(tmp, LoginVeilStashFile, true);   // the list exists BEFORE anything comes off
            // hair / teeth / eyes / earrings first (confirmed gone), then the head and the clothes, then a settle before the logout (LoginVeil.LogoutWaves)
            foreach (var x in stash) loginVeilHeld[x.Item] = 1;   // no outfit send may put any of them back before the logout
            var waveLog = await LoginVeil.DetachInWaves(LoginVeil.LogoutWaves(late), x => DetachItem(x.Item, "login veil stash"), () => WornPrims().Select(AttachItemId).ToHashSet());
            foreach (var l in waveLog) Log("login-veil", "stash " + l);
            var left = WornPrims().Select(AttachItemId).ToHashSet();
            var back = LoginVeil.HeldWornEarly(stash.Select(x => x.Item), left);   // anything back on (or never off): one more confirmed wave
            if (back.Count > 0)
            {
                var again = stash.Where(x => back.Contains(x.Item)).ToList();
                foreach (var l in await LoginVeil.DetachInWaves(new List<List<LoginVeil.Stashed>> { again }, x => DetachItem(x.Item, "login veil stash, again"), () => WornPrims().Select(AttachItemId).ToHashSet()))
                    Log("login-veil", "stash re-detach (was back on) " + l);
                left = WornPrims().Select(AttachItemId).ToHashSet();
            }
            Log("login-veil", $"stash: {stash.Count} attachment(s) taken off for logout (COF links kept, list in {Path.GetFileName(LoginVeilStashFile)}): {string.Join("; ", stash.Select(x => x.Name))}"
                + (stash.Any(x => left.Contains(x.Item)) ? $"; still attached: {string.Join("; ", stash.Where(x => left.Contains(x.Item)).Select(x => x.Name))}" : ""));
        }
        catch (Exception ex) { Log("login-veil", "stash failed (the outfit stays as it is): " + ex.GetBaseException().Message); }
    }

    static DateTime loginVeilStashAt = DateTime.MinValue;   // the stash file's write time = the logout
    static List<LoginVeil.Stashed> LoginVeilReadStash()
    {
        try
        {
            var f = LoginVeilStashFile; if (!File.Exists(f)) return new();
            loginVeilStashAt = File.GetLastWriteTime(f);
            if ((DateTime.Now - File.GetLastWriteTime(f)).TotalDays > 14) { Log("login-veil", "stash file older than 14 days: ignored (the COF restore covers the outfit)"); File.Delete(f); return new(); }
            return LoginVeil.ParseStash(File.ReadAllText(f));
        }
        catch { return new(); }
    }

    /// <summary>Attach stashed items NOW from the stash file's own ids (LoginVeil.AttachRecord): no fetch, never waits on the inventory server.</summary>
    static int LoginVeilAttachDirect(IEnumerable<LoginVeil.Stashed> items, string why)
    {
        int n = 0; var worn = WornPrims().Select(AttachItemId).ToHashSet();
        foreach (var x in items.Where(x => !worn.Contains(x.Item)))
        {
            if (OutfitChangeActive) { Log("login-veil", $"stash attach ({why}) stopped: an outfit change is running"); break; }
            InventoryItem known = null; try { known = client.Inventory.Store.Contains(x.Item) ? client.Inventory.Store[x.Item] as InventoryItem : null; } catch { }
            client.Appearance.Attach(LoginVeil.AttachRecord(x, client.Self.AgentID, known), x.Point > 0 ? (AttachmentPoint)x.Point : AttachmentPoint.Default, false); n++;
        }
        if (n > 0) Log("login-veil", $"stash: {n} attachment(s) sent directly, no fetch ({why})");
        return n;
    }

    static async Task<int> LoginVeilAttach(IEnumerable<LoginVeil.Stashed> items, string why, CancellationToken ct, IReadOnlyDictionary<UUID, Task<InventoryItem>> pre = null)
    {
        int n = 0; var worn = WornPrims().Select(AttachItemId).ToHashSet();
        foreach (var x in items.Where(x => !worn.Contains(x.Item)))
        {
            if (OutfitChangeActive) { Log("login-veil", $"stash attach ({why}) stopped: an outfit change is running"); break; }
            var it = pre != null && pre.TryGetValue(x.Item, out var pt) ? await pt : null;
            it ??= await FetchItemRO(x.Item, ct);
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
        LoginVeilEvoxSafetyNet(t0);   // ears / lashes hidden at logout come back on every path (no veil, kept on, error)
        var id = LoginVeilItem(); var stash = LoginVeilReadStash();
        if (id == UUID.Zero && stash.Count == 0) return;
        var usual = stash.Count == 0 ? null : LoginVeilUsualAtLogin(stash, loginVeilStashAt);
        if (usual != null) { Log("login-veil", "login outfit: " + usual.Why); stash = usual.Stash; }
        var (early, late) = LoginVeil.SplitStash(stash); loginVeilHoldTries.Clear();
        loginVeilOffAt = id != UUID.Zero ? DateTime.MaxValue : DateTime.MinValue;   // the login-order check: the base head is covered until the veil is off
        loginVeilHeld.Clear(); loginVeilBurstSent.Clear(); foreach (var x in late) loginVeilHeld[x.Item] = 1;   // before login-cover plans (and the outfit send)
        if (usual?.Changed == true)
        {   // the clothes not put back keep their COF links until LoginVeilUsualCof drops them: hold them so no path wears them meanwhile
            foreach (var d in usual.Drop) loginVeilHeld[d] = 1;
            _ = Task.Run(() => LoginVeilUsualCof(usual));
        }
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); loginVeilRestoreTask = done.Task;
        _ = Task.Run(async () =>
        {
            string why = "?"; bool off = true;
            using var cts = new CancellationTokenSource(150000); var ct = cts.Token;
                        var pre = stash.GroupBy(x => x.Item).ToDictionary(g => g.Key, g => FetchItemRO(g.Key, ct));   // every stash item fetched once, in parallel, up front
            List<UUID> veilLinks = null;
            try
            {
                if (stash.Count > 0) Log("login-veil", $"stash from logout: {stash.Count} item(s); clothes now ({early.Count}), with the veil off ({late.Count})");
                var itemTask = id == UUID.Zero ? Task.FromResult<InventoryItem>(null) : FetchItemRO(id, ct);   // fetched once, used by the off path
                // 14:01 relog: this awaited the stash item fetches (~14 s at login, the server's pace) BEFORE the release loop started, so the
                // veil decision came at +15.5 s for clothes Ready at +12.3 s. Now the attach runs beside the loop; the loop never waits on it.
                if (id == UUID.Zero) { if (early.Count > 0) { await Task.Delay(1000, ct); LoginVeilAttachDirect(early, "login"); } why = "no veil configured"; return; }
                bool retried = false, fellBack = false; List<InventoryItem> links = null; DateTime linksAt = DateTime.MinValue; UUID cofId = UUID.Zero;
                await LoginVeil.BesideLoop(early.Count == 0 ? null : async () => { await Task.Delay(1000, ct); LoginVeilAttachDirect(early, $"login, +{(DateTime.Now - t0).TotalSeconds:F1} s"); }, async () => {
                while (gen == loginVeilGen && LoggedIn)
                {
                    double now = (DateTime.Now - t0).TotalSeconds;
                    if (links == null || (DateTime.Now - linksAt).TotalSeconds > 5)
                    {   // one COF read, refreshed at most every 5 s with a short timeout: the tick itself never waits on the server
                        var cof = cofId == UUID.Zero ? await CofFolder(ct) : null; if (cof != null) cofId = cof.UUID;
                        if (cofId != UUID.Zero)
                        {
                            var (kids, okRead) = await ReadFolderTimed(cofId, ct, 4000);
                            if (okRead) { veilLinks = kids.OfType<InventoryItem>().Where(l => l.ParentUUID == cofId && l.IsLink() && l.AssetUUID == id).Select(l => l.UUID).ToList(); links = kids.OfType<InventoryItem>().Where(l => l.ParentUUID == cofId && l.IsLink() && (l.InventoryType == InventoryType.Object || l.InventoryType == InventoryType.Attachment)).GroupBy(l => l.AssetUUID).Select(g => g.First()).ToList(); }
                        }
                        linksAt = DateTime.Now;
                    }
                    var worn = WornPrims().Select(AttachItemId).ToHashSet();
                    LoginVeilEnforceHold(late, worn, now);
                    if (stash.Count > 0 && !retried && now >= LoginVeil.RetryAttachSec) { retried = true; _ = Task.Run(async () => { try { await LoginVeilAttach(early, "retry", ct, pre); } catch { } }); }
                    if (stash.Count > 0 && !fellBack && now >= LoginVeil.OutfitFallbackSec && early.Any(x => LoginVeil.Waits(x.Name) && !worn.Contains(x.Item)))
                    {
                        fellBack = true; var last = LastClothedOutfit();
                        Log("login-veil", $"stash clothes still not back at +{now:F0} s: wearing the last clothed outfit '{last ?? "-"}'");
                        if (!string.IsNullOrWhiteSpace(last)) _ = Task.Run(async () => { try { Log("login-veil", "fallback outfit: " + (await WearOutfitWithHuds(last)).Replace("\n", " | ")); } catch (Exception ex) { Log("login-veil", "fallback outfit failed: " + ex.GetBaseException().Message); } });
                    }
                    // the waited list: the COF links, or (COF not read yet) the stash's own clothes, so a slow COF read never holds the veil
                    var src = LoginVeil.WaitSource(links?.Select(l => (l.AssetUUID, l.Name)).ToList(), early.Select(x => (x.Item, x.Name)).ToList());
                    var garments = (src ?? new()).Select(l => new LoginVeil.Garment(l.Name,
                        worn.Contains(l.Item) ? (loginSeenAt.TryGetValue(l.Item, out var s) ? (s - t0).TotalSeconds : 0) : null,
                        loginReadyAt.TryGetValue(l.Name, out var r) ? (r - t0).TotalSeconds : null)).ToList();
                    var heads = LoginVeil.HeadsNotAttached((links?.Select(l => (l.AssetUUID, l.Name)) ?? Enumerable.Empty<(UUID, string)>()).Concat(stash.Select(x => (x.Item, x.Name))), worn);
                    var (rel, w) = src == null && now < LoginVeil.MaxHoldSec ? (false, "Current Outfit not readable yet") : LoginVeil.ReleaseWithHead(garments, now, stash.Count > 0, heads);
                    why = w; if (rel) break;
                    if (w.StartsWith("KEEP")) { off = false; break; }
                    await Task.Delay(200, ct);
                }
                return 0; });
                bool confirmed = false;
                Func<Task> burst = late.Count == 0 ? null : () => LoginVeilBurst(late, t0, ct, pre);
                if (off && gen == loginVeilGen) confirmed = await LoginVeilOff(id, t0, why, itemTask.IsCompleted ? await itemTask : null, cofId, veilLinks, burst);
                else if (!off) Log("login-veil", $"veil KEPT ON at +{(DateTime.Now - t0).TotalSeconds:F1} s: {why}");
                if (off) await LoginVeilReveal(late, t0, confirmed, ct, pre);   // hair, teeth, eyes: only after the skin is drawn
            }
            catch (Exception ex)
            {   // never nude: with a stash, the veil only comes off once its clothes are attached
                why = "error: " + ex.GetBaseException().Message; var w = WornPrims().Select(AttachItemId).ToHashSet();
                if (gen == loginVeilGen && id != UUID.Zero && early.Where(x => LoginVeil.Waits(x.Name)).All(x => w.Contains(x.Item)) && LoginVeil.HeadsNotAttached(stash.Select(x => (x.Item, x.Name)), w).Count == 0) { var c = await LoginVeilOff(id, t0, why, null); await LoginVeilReveal(late, t0, c, CancellationToken.None, null); }
                else Log("login-veil", $"veil KEPT ON ({why}): stashed clothes not back");
            }
            finally
            {
                loginVeilHeld.Clear();
                try { if (stash.Count > 0 && LoggedIn) await LoginVeilStashDone(stash, t0); } catch { }
                done.TrySetResult();
            }
        });
    }

    /// <summary>16:24 login: hair, eyes, teeth and earrings were on before veil-off (the sim restored them: they were worn at logout). Whatever
    /// path put a held item on before veil-off, it comes off again at once (COF link kept), at most 3 tries per item.</summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<UUID, int> loginVeilHoldTries = new();
    static void LoginVeilEnforceHold(List<LoginVeil.Stashed> held, HashSet<UUID> worn, double now)
    {
        if (held.Count == 0 || loginVeilHeld.IsEmpty) return;
        foreach (var id in LoginVeil.HeldWornEarly(held.Select(x => x.Item).Where(LoginVeilHeld), worn))
        {
            int n = loginVeilHoldTries.AddOrUpdate(id, 1, (_, v) => v + 1); if (n > 3) continue;
            var nm = held.First(x => x.Item == id).Name;
            Log("login-veil", $"held for veil-off but worn at +{now:F1} s: '{nm}': taking it off again (try {n}; COF link kept)");
            try { DetachItem(id, "login veil hold"); } catch (Exception ex) { Log("login-veil", "hold detach failed: " + ex.GetBaseException().Message); }
        }
    }

    static async Task LoginVeilStashDone(List<LoginVeil.Stashed> stash, DateTime t0)
    {
        for (int i = 0; i < 40; i++) { var w = WornPrims().Select(AttachItemId).ToHashSet(); if (stash.All(x => w.Contains(x.Item))) break; await Task.Delay(250); }
        var worn = WornPrims().Select(AttachItemId).ToHashSet(); var miss = stash.Where(x => !worn.Contains(x.Item)).ToList();
        Log("login-veil", miss.Count == 0 ? $"stash: all {stash.Count} back on by +{(DateTime.Now - t0).TotalSeconds:F1} s"
                                          : $"stash: not back on: {string.Join("; ", miss.Select(x => x.Name))} (COF links kept: the library's add-missing pass still wears them)");
        try { File.Delete(LoginVeilStashFile); } catch { }
    }

    /// <summary>After the veil-off bake (or ConfirmMaxSec without a confirmation): release the held head parts, attach them, show the collar.</summary>
    static async Task LoginVeilReveal(List<LoginVeil.Stashed> late, DateTime t0, bool confirmed, CancellationToken ct, IReadOnlyDictionary<UUID, Task<InventoryItem>> pre)
    {
        loginVeilHeld.Clear();
        if (!LoggedIn) return;
        if (late.Count > 0 && late.Any(x => !WornPrims().Select(AttachItemId).Contains(x.Item)))
        {
            Log("login-veil", $"head parts on at +{(DateTime.Now - t0).TotalSeconds:F1} s ({(confirmed ? "veil-off bake confirmed by the sim" : $"no bake confirmation in {LoginVeil.ConfirmMaxSec:F0} s")})");
            try
            {
                LoginVeilAttachDirect(late.Where(x => !loginVeilBurstSent.ContainsKey(x.Item)), "after the veil-off bake");
                for (int i = 0; i < 12; i++) { var w = WornPrims().Select(AttachItemId).ToHashSet(); if (late.All(x => w.Contains(x.Item))) break; await Task.Delay(250, ct); }
                await LoginVeilAttach(late, "after the veil-off bake, fetched retry", ct, pre);   // only what the direct attach did not bring
            }
            catch (Exception ex) { Log("login-veil", "head parts attach failed: " + ex.GetBaseException().Message); }
        }
        // the collar only once the head parts are really on (David: it appeared first)
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 40; i++) { var w = WornPrims().Select(AttachItemId).ToHashSet(); if (late.All(x => w.Contains(x.Item))) break; await Task.Delay(250); }
            _ = Task.Run(() => LoginVeilEvoxShowAfterLogin("veil off"));   // ears and lashes right after the head is on (LoginVeilEvox.cs)
            await Task.Delay(500); await LoginVeilCollarShowAfterLogin();
        });
    }

    /// <summary>The veil-off burst: head parts sent right as the veil link goes (before the bake POST), head first, so face, hair and skin
    /// land in the same moment. Item records come from the up-front prefetch; one missing is fetched (never blocks the others).</summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<UUID, byte> loginVeilBurstSent = new();
    static async Task LoginVeilBurst(List<LoginVeil.Stashed> late, DateTime t0, CancellationToken ct, IReadOnlyDictionary<UUID, Task<InventoryItem>> pre)
    {
        // 14:38 relog: "4 not prefetched yet" (the fetch hung at login): the burst no longer needs a fetched record, every head part goes now
        var ordered = late.OrderBy(x => LoginVeil.RevealOrder(x.Name)).ToList();
        foreach (var x in ordered) loginVeilBurstSent[x.Item] = 1;
        int n = LoginVeilAttachDirect(ordered, "veil-off burst");
        Log("login-veil", $"head parts sent with the veil-off bake at +{(DateTime.Now - t0).TotalSeconds:F1} s: {n} ({string.Join("; ", ordered.Select(x => x.Name))})");
        await Task.CompletedTask;
    }

    /// <summary>Veil off. Fast path (the release loop's COF read gave the veil link ids): no server read before the bake. Returns: bake confirmed.</summary>
    static async Task<bool> LoginVeilOff(UUID id, DateTime t0, string why, InventoryItem it, UUID cofId, List<UUID> veilLinks, Func<Task> burst = null)
    {
        string At() => $"+{(DateTime.Now - t0).TotalSeconds:F1} s";
        Func<Task> once = null; int fired = 0;
        if (burst != null) once = async () => { if (Interlocked.Exchange(ref fired, 1) == 0) { try { await burst(); } catch (Exception ex) { Log("login-veil", "veil-off burst failed: " + ex.GetBaseException().Message); } } };
        if (!LoggedIn || cofId == UUID.Zero || veilLinks == null || veilLinks.Count == 0) return await LoginVeilOff(id, t0, why, it, once);
        try
        {
            using var cts = new CancellationTokenSource(30000); var ct = cts.Token;
            int simBefore = simAttCof; var tStart = DateTime.Now;
            double rmMs = 0, bakeMs = 0;
            var (removed, confirmed) = await LoginVeil.OffCore(
                () => { client.Appearance.RemoveFromOutfit(new List<InventoryItem> { LoginVeil.LayerRecord(id, it, SafeWearables().FirstOrDefault(w => w.ItemID == id)?.WearableType) }); },
                async () =>
                {
                    using var t = CancellationTokenSource.CreateLinkedTokenSource(ct); t.CancelAfter(5000);
                    try { await client.Inventory.RemoveItemsAsync(veilLinks, t.Token); rmMs = (DateTime.Now - tStart).TotalMilliseconds; Log("wear", $"COF link(s) removed (login veil off, known ids, no read): {veilLinks.Count}"); return true; }
                    catch (Exception ex) { Log("login-veil", "known-link removal failed: " + ex.GetBaseException().Message); return false; }
                },
                async () => { await LoginVeilBakePost(cofId, ct); bakeMs = (DateTime.Now - tStart).TotalMilliseconds; },
                async () =>
                {
                    for (int i = 0; i < LoginVeil.ConfirmMaxSec * 10; i++) { if (LoginVeil.BakeConfirmed(simBefore, simAttCof)) return true; await Task.Delay(100, ct); }
                    return false;
                }, once);
            if (!removed) return await LoginVeilOff(id, t0, why, it, once);
            double confMs = (DateTime.Now - tStart).TotalMilliseconds;
            loginVeilOffAt = DateTime.Now;
            Log("login-veil", $"veil off at {At()}: {why} (links removed +{rmMs:F0} ms, bake requested +{bakeMs:F0} ms, "
                + (confirmed ? $"sim appearance COF v{simBefore} -> v{simAttCof} at +{confMs:F0} ms)" : $"no sim appearance for a newer COF than v{simBefore} in {LoginVeil.ConfirmMaxSec:F0} s)"));
            // read-back AFTER the bake request: only if the link is somehow still there, redo the verified slow path
            var (kids, ok) = await ReadFolderTimed(cofId, ct, 6000);
            if (ok && kids.OfType<InventoryItem>().Any(l => l.ParentUUID == cofId && l.IsLink() && l.AssetUUID == id))
            { Log("login-veil", "veil link still in Current Outfit after the fast removal: verified slow path"); return await LoginVeilOff(id, t0, why, it); }
            return confirmed;
        }
        catch (Exception ex) { Log("login-veil", $"fast veil off failed at {At()}: {ex.GetBaseException().Message}; verified slow path"); return await LoginVeilOff(id, t0, why, it, once); }
    }

    /// <summary>14:01 relog: RebakeNow (LMV RequestSetAppearance) took 1.6 s after the link removal: it chains behind the appearance pipeline
    /// RemoveFromOutfit just started. The bake is the UpdateAvatarAppearance POST with the new COF version; send that directly (retry once on
    /// "expected"), and only fall back to RebakeNow when the POST is not possible.</summary>
    static async Task LoginVeilBakePost(UUID cofId, CancellationToken ct)
    {
        var cap = client.Network.CurrentSim?.Caps?.CapabilityURI("UpdateAvatarAppearance");
        int ver = (client.Inventory.Store.Contains(cofId) ? client.Inventory.Store[cofId] as InventoryFolder : null)?.Version ?? -1;
        if (cap == null || ver <= 0) { await RebakeNow("login veil off (no direct POST)"); return; }
        try
        {
            using var t = CancellationTokenSource.CreateLinkedTokenSource(ct); t.CancelAfter(5000);
            for (int i = 0; i < 2; i++)
            {
                var (_, data) = await client.HttpCapsClient.PostAsync(cap, OSDFormat.Xml, new OSDMap { ["cof_version"] = ver }, t.Token);
                var res = data == null ? null : OSDParser.Deserialize(data) as OSDMap;
                if (res != null && res.ContainsKey("success") && res["success"].AsBoolean())
                { try { client.Appearance.UpdateLastReceivedCOFVersion(res["cof_version"].AsInteger()); } catch { } Log("wear", $"bake POST cof_version {ver}: ok (login veil off)"); return; }
                var next = RebakeRetryVersion(res, ver); if (next == null) break; ver = next.Value;
            }
        }
        catch (Exception ex) { Log("wear", "bake POST failed: " + ex.GetBaseException().Message); }
        await RebakeNow("login veil off (POST fallback)");
    }

    static async Task<bool> LoginVeilOff(UUID id, DateTime t0, string why, InventoryItem it = null, Func<Task> burst = null)
    {
        int simBefore0 = simAttCof;
        string At() => $"+{(DateTime.Now - t0).TotalSeconds:F1} s";
        try
        {
            if (!LoggedIn) { Log("login-veil", $"veil NOT off at {At()}: logged out ({why})"); return false; }
            using var cts = new CancellationTokenSource(30000); var ct = cts.Token;
            var cof = await CofFolder(ct); if (cof == null) { Log("login-veil", $"veil NOT off at {At()}: Current Outfit not found ({why})"); return false; }
            async Task<bool> Linked() => (await ReadFolderRO(cof.UUID, ct, 5000)).OfType<InventoryItem>().Any(l => l.ParentUUID == cof.UUID && l.IsLink() && l.AssetUUID == id);
            int CofVer() => (client.Inventory.Store[cof.UUID] as InventoryFolder)?.Version ?? cof.Version;
            // 11:48 relog: trusted RemoveFromOutfit, logged "veil off", but the COF link stayed. The COF link is the truth: remove it ourselves,
            // read back, then rebake. 12:03 relog: no pre-read and no item fetch here any more (both were done before, at the server's pace).
            int before = CofVer();
            it ??= await FetchItemRO(id, ct);
            try { client.Appearance.RemoveFromOutfit(new List<InventoryItem> { LoginVeil.LayerRecord(id, it, SafeWearables().FirstOrDefault(w => w.ItemID == id)?.WearableType) }); } catch { }
            string rm = ""; bool still = true;
            for (int k = 0; k < 2 && still; k++)
            {
                rm += (k > 0 ? "; retry: " : "") + await CofLinksOffBatch(new[] { id }, "login veil off", ct);
                still = await Linked();
            }
            if (rm.Contains("no COF links to remove") && !LoginVeilWorn(id) && !still && CofVer() <= before) { Log("login-veil", $"veil not worn at {At()} ({why})"); return true; }
            var (ok, v) = LoginVeil.OffVerdict(still, before, CofVer());
            if (ok) { if (burst != null) await burst(); await RebakeNow("login veil off"); loginVeilOffAt = DateTime.Now; }
            Log("login-veil", ok ? $"veil off at {At()}: {why} ({v}; {rm})" : $"veil NOT off at {At()}: {v} ({rm}); trigger: {why}");
            if (!ok) return false;
            for (int i = 0; i < LoginVeil.ConfirmMaxSec * 10; i++) { if (LoginVeil.BakeConfirmed(simBefore0, simAttCof)) return true; await Task.Delay(100); }
            return false;
        }
        catch (Exception ex) { Log("login-veil", $"veil NOT off at {At()}: " + ex.GetBaseException().Message); return false; }
    }
}
