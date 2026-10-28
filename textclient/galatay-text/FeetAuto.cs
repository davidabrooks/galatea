// 2026-10-28: `FeetAuto` is its own class; everything it uses from the rest of the client goes through IFeetHost (HoverHost.cs).
// FeetAuto (2026-10-11, David): Maitreya foot pose follows the worn footwear. Every 4 s she compares her worn
// attachments with routes/_feet-rules.json: a listed heeled shoe worn -> its pose (f3 for the Moca boots), none -> the
// default (f1, flat). When the wanted pose has been the same for two polls and differs from the last pose pressed
// (run/feet-state.txt), she attaches the LaraX body HUD, touches that button, and detaches it again (the HUD stays on if
// it was already worn). Because it watches what is worn rather than who changed it, outfit wear, undress/redress, the
// bikini/beach switch and the in-SL chat helper are all covered. 'feet' reports; 'feet apply' forces a press now.
// ponytail: 4 s poll + one HUD round-trip (~15 s); ceiling = feet lag a few seconds behind a shoe change.
using System.Text.Json;
using LibreMetaverse;

namespace GalatayText;

internal sealed class FeetAuto
{
    readonly IFeetHost h;
    internal FeetAuto(IFeetHost host) { h = host; }
    internal sealed record FeetRules(UUID Hud, string Default, Dictionary<UUID, (string name, string feet)> Footwear);

    string FeetRulesPath => Path.Combine(h.RouteDir, "_feet-rules.json");
    internal string StatePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "..", "run", "feet-state.txt");
    static readonly UUID MaitreyaHudItem = new("961d2d4b-4948-3bac-ab88-0a7ba3b99302");
    readonly SemaphoreSlim feetGate = new(1, 1);

    /// <summary>Pure: parse _feet-rules.json text (bad/missing parts -> defaults: f1, the LaraX HUD, no footwear).</summary>
    internal static FeetRules ParseFeetRules(string json)
    {
        var fw = new Dictionary<UUID, (string, string)>(); string def = "f1"; var hud = MaitreyaHudItem;
        try
        {
            using var d = JsonDocument.Parse(json ?? "{}"); var r = d.RootElement;
            if (r.TryGetProperty("default", out var de) && IsFeetButton(de.GetString())) def = de.GetString();
            if (r.TryGetProperty("hud", out var he) && UUID.TryParse(he.GetString(), out var hu) && hu != UUID.Zero) hud = hu;
            if (r.TryGetProperty("footwear", out var fa) && fa.ValueKind == JsonValueKind.Array)
                foreach (var f in fa.EnumerateArray())
                    if (f.TryGetProperty("item", out var ie) && UUID.TryParse(ie.GetString(), out var iu) && f.TryGetProperty("feet", out var fe) && IsFeetButton(fe.GetString()))
                        fw[iu] = (f.TryGetProperty("name", out var ne) ? ne.GetString() : "?", fe.GetString());
        }
        catch { }
        return new FeetRules(hud, def, fw);
    }

    /// <summary>Pure: the tab bar of the page holding a button = the lowest 'tabs' prim above the button (pages stack in z), or -1.</summary>
    internal static int FeetPageTabIndex(IReadOnlyList<(string name, float z)> prims, float buttonZ)
    {
        int best = -1; float bz = float.MaxValue;
        for (int i = 0; i < prims.Count; i++) if (prims[i].name == "tabs" && prims[i].z > buttonZ && prims[i].z < bz && prims[i].z - buttonZ < 1f) { bz = prims[i].z; best = i; }
        return best;
    }
    internal static bool IsFeetButton(string s) => s is "f1" or "f2" or "f3" or "f4" or "f5";

    /// <summary>Pure: the pose the worn items call for: the first listed shoe that is worn, else the default.</summary>
    internal static (string feet, string why) FeetWanted(IEnumerable<UUID> worn, FeetRules rules)
    {
        foreach (var u in worn ?? Array.Empty<UUID>())
            if (rules.Footwear.TryGetValue(u, out var s)) return (s.feet, s.name);
        return (rules.Default, "no listed heeled footwear");
    }

    /// <summary>Pure: press when two polls agree and the pose differs from the last one pressed (unknown -> press).</summary>
    internal static bool FeetShouldPress(string wanted, string previousPoll, string lastPressed) =>
        wanted != null && wanted == previousPoll && wanted != lastPressed;

    /// <summary>Pure: signature of the worn Maitreya/LaraX body = sorted "itemId/objectId" of its attachment roots (name-matched via BodyFamily; the HUD and add-ons are not the body).
    /// A new or re-attached body has a new attachment object id. The child prim count is NOT part of it: prims streaming in and out made the signature flap every poll
    /// (2026-10-28 13:30 live loop: "worn body changed" every ~2 s, f3 re-pressed every 12 s).</summary>
    internal static string FeetBodySignature(IEnumerable<(UUID item, string name, UUID objectId)> worn) =>
        string.Join(",", (worn ?? Array.Empty<(UUID, string, UUID)>()).Where(w => IsFeetBody(w.name)).Select(w => $"{w.item}/{w.objectId}").OrderBy(x => x, StringComparer.Ordinal));

    /// <summary>Pure: is this worn attachment the LaraX body itself (not its HUD or an add-on)?</summary>
    internal static bool IsFeetBody(string name) => Program.BodyFamily(name) == "larax" && !(name ?? "").Contains("HUD", StringComparison.OrdinalIgnoreCase) && !(name ?? "").Contains("Add-on", StringComparison.OrdinalIgnoreCase);

    /// <summary>Pure: the body changed when a previous signature exists (not the first poll) and differs.</summary>
    internal static bool FeetBodyChanged(string previousSig, string sig) => previousSig != null && previousSig != sig;

    /// <summary>Pure decision per poll: a body change makes the last-pressed pose unknown (the body reset its feet to flat), then the normal rule applies.
    /// Returns whether to press and the last-pressed value to use (null = cleared).</summary>
    internal static (bool press, string lastPressed) FeetDecide(string wanted, string previousPoll, string lastPressed, bool bodyChanged)
    {
        if (bodyChanged) lastPressed = null;
        return (FeetShouldPress(wanted, previousPoll, lastPressed), lastPressed);
    }

    string FeetBodySigNow()
    {
        var worn = h.WornPrims();
        return FeetBodySignature(worn.Select(p => (h.AttachItemId(p), p.Properties?.Name, p.ID)));
    }

    string ReadFeetState() { try { return File.Exists(StatePath) ? File.ReadAllText(StatePath).Trim() : null; } catch { return null; } }
    void ClearFeetState() { try { File.Delete(StatePath); } catch { } }

    (string feet, string why, FeetRules rules) FeetNow()
    {
        var rules = ParseFeetRules(File.Exists(FeetRulesPath) ? File.ReadAllText(FeetRulesPath) : "{}");
        var (f, why) = FeetWanted(h.WornPrims().Select(h.AttachItemId).Where(u => u != UUID.Zero), rules);
        return (f, why, rules);
    }

    // ---- 2026-10-28 (David: the feet fit is judged on the FOOT, not the boot; never finished while a bare foot shows) ----
    // Live failure: feet-state.txt kept "f3" across logout/login, the login press landed mid outfit swap and did not take, a later press landed as she sat and was never
    // checked: toes poked out of the boots. Now: the state is cleared at every login; a press waits for a stable outfit, a settled posture and standing; and EVERY press is
    // verified from evidence (which foot mesh pair of the worn body is visible, read from the body's own face alpha), never from feet-state.txt or the press log.
// 2026-10-28 (root cause): the page-open touch hit the ALPHA tab (u 0.1) so the foot press did nothing; it now touches the HANDS/FEET tab (u 0.70). The two
// 'heel-foot animations' once used as proof are hand-finger animations, and a skin-colour check on the close-up counted wood heels and suede as skin: both are gone.
// The close-up is kept only as an informational picture; it never decides a fit.
    // 2026-10-28 11:42 relog (David: the body showed under the shirt and shorts before the clothes rendered): the feet code attached / detached the Maitreya HUD (an item the sim had
    // already restored, worn and in the COF) four times within 30 s of login. No HUD round trip until the login has settled.
    internal const double FeetOutfitStableSec = 8, FeetPoseSettleSec = 5, FeetRetryCooldownSec = 90, FeetLoginGraceSec = 40;
    internal const int FeetPressAttempts = 3, FeetRetakes = 3, FeetGiveUpRounds = 3;
    internal const double FeetMinLuma = 0.10, FeetMinP95Luma = 0.25;
    internal static readonly double[] FeetRetakeExposure = { 0.5, 2.5, 4.5 };   // each retake of an unusable (near-black) close-up is brighter
    // 2026-10-28: the LaraX HUD page with the f1-f5 buttons is the HANDS/FEET tab; on the 'tabs' prim (face 4) it sits at u ~0.70 (u 0.1 = the ALPHA tab: the press did nothing)
    internal const float FeetTabU = 0.70f, FeetTabV = 0.5f; internal const int FeetTabFace = 4;
    internal static Vector3 FeetTabSt => new(FeetTabU, FeetTabV, 0);
    // foot mesh prims on 'Maitreya Mesh Body - LaraX V1.1': f1 = b46/b47 (flat), f2 = b48/b49, f3 = b50/b51, f4 = b52/b53, f5 = b54/b55; exactly one pair is visible (alpha > 0)
    internal static readonly (string feet, string a, string b)[] FootMeshPairs = { ("f1", "b46", "b47"), ("f2", "b48", "b49"), ("f3", "b50", "b51"), ("f4", "b52", "b53"), ("f5", "b54", "b55") };

    /// <summary>Pure: signature of everything worn (sorted item ids): a change means an outfit change is (or just was) in progress.</summary>
    internal static string FeetWornSignature(IEnumerable<UUID> worn) => string.Join(",", (worn ?? Array.Empty<UUID>()).Where(u => u != UUID.Zero).Select(u => u.ToString()).OrderBy(x => x, StringComparer.Ordinal));

    /// <summary>Pure: may a feet press happen now? Not while the outfit is changing, a pose / sit transition runs, the posture just changed, or she sits (a press must be verifiable standing).</summary>
    internal static (bool ok, string why) FeetPressReady(double outfitStableSec, bool poseBusy, double poseSettledSec, bool sitting, double sinceLoginSec = double.MaxValue)
    {
        if (sinceLoginSec < FeetLoginGraceSec) return (false, $"login settling ({sinceLoginSec:F0} s since login, no HUD round trip before {FeetLoginGraceSec:F0} s)");
        if (outfitStableSec < FeetOutfitStableSec) return (false, $"outfit change in progress (worn attachments changed {outfitStableSec:F0} s ago, need {FeetOutfitStableSec:F0} s stable)");
        if (poseBusy) return (false, "a pose / sit transition is in progress");
        if (poseSettledSec < FeetPoseSettleSec) return (false, "the posture changed a moment ago");
        if (sitting) return (false, "sitting: the feet are pressed and verified only while standing");
        return (true, null);
    }

    /// <summary>Pure: did AttachItem report the item as already worn (the sim restored it at login and it is part of the outfit)? Then it must never be detached afterwards.</summary>
    internal static bool AttachResultAlreadyWorn(string r) => r != null && r.Contains("already worn", StringComparison.OrdinalIgnoreCase);

    /// <summary>Pure: automatic attempts are bounded: after FeetGiveUpRounds failed rounds stop (until the wanted pose, the body or the login changes); between rounds cool down.</summary>
    internal static bool FeetRetryAllowed(int failedRounds, double sinceFailSec) => failedRounds < FeetGiveUpRounds && (failedRounds == 0 || sinceFailSec >= FeetRetryCooldownSec);

    /// <summary>What a `look self feet` close-up showed (vision/feet_fit.py): Readable=false or near-black = unusable.</summary>
    internal sealed record FeetImage(bool Readable, double MeanLuma, double P95Luma, string Path, string Why);

    /// <summary>Pure: parse feet_fit.py's json line; null / garbage = an unreadable image (never a pass).</summary>
    internal static FeetImage ParseFeetImage(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json ?? ""); var r = d.RootElement;
            double N(string k) => r.TryGetProperty(k, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : 0;
            return new FeetImage(r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True, N("mean_luma"), N("p95_luma"),
                r.TryGetProperty("image", out var im) ? im.GetString() : null, r.TryGetProperty("why", out var w) ? w.GetString() : null);
        }
        catch { return new FeetImage(false, 0, 0, null, "no / unreadable analysis"); }
    }
    /// <summary>Pure: a close-up is unusable (retaken, never described as a picture of the feet) when it is unreadable or near-black.</summary>
    internal static bool FeetImageInvalid(FeetImage i) => i == null || !i.Readable || i.MeanLuma < FeetMinLuma || i.P95Luma < FeetMinP95Luma;

    internal enum FeetKind { Verified, Failed, Invalid }
    internal sealed record FeetVerdict(FeetKind Kind, string Why);

    /// <summary>Pure: which foot pose the body shows, from the alpha of its foot mesh prims (name, alpha): exactly one of the pairs b46/47 (f1) .. b54/55 (f5) is visible (both prims alpha > 0.5)
    /// and all ten prims were read. Anything else (body not found, a prim missing, none or several pairs visible) = null with the reason: the proof is missing.</summary>
    internal static (string feet, string why) FeetShownFromMesh(IEnumerable<(string name, float alpha)> prims)
    {
        if (prims == null) return (null, "the worn body's foot mesh prims could not be read");
        var al = new Dictionary<string, float>(); foreach (var (n, a) in prims) if (n != null) al[n] = a;
        var missing = FootMeshPairs.SelectMany(p => new[] { p.a, p.b }).Where(n => !al.ContainsKey(n)).ToList();
        if (missing.Count > 0) return (null, $"foot mesh prims missing on the body: {string.Join(", ", missing)}");
        var shown = FootMeshPairs.Where(p => al[p.a] > 0.5f && al[p.b] > 0.5f).Select(p => p.feet).ToList();
        if (shown.Count == 0) return (null, "no foot mesh pair is visible");
        if (shown.Count > 1) return (null, $"several foot mesh pairs are visible ({string.Join(", ", shown)})");
        return (shown[0], "ok");
    }

    /// <summary>Pure: is the foot fit proven? The ONLY proof is the body's own foot mesh: the pair showing must be the wanted one. A missing / unreadable proof is a failure, never a pass.
    /// The close-up is informational (its path goes in the text); it neither passes nor fails a fit. Neither feet-state.txt nor the press log is an input.</summary>
    internal static FeetVerdict FeetFit(string wanted, string shown, string meshWhy, FeetImage img)
    {
        if (shown == null) return new(FeetKind.Failed, $"no proof of the foot pose: {meshWhy}");
        if (shown != wanted) return new(FeetKind.Failed, $"the body shows foot {shown} but {wanted} is wanted for the worn footwear");
        var pic = img == null || FeetImageInvalid(img) ? "no usable close-up (informational only)" : $"close-up (informational only, not a proof): {img.Path}";
        return new(FeetKind.Verified, $"the body's foot mesh pair for {wanted} is the one visible; {pic}");
    }

    // ---- state of the loop ----
    internal Func<DateTime> Now = () => DateTime.Now;
    internal Func<int, Task> Delay = ms => Task.Delay(ms);
    string prevPoll, prevSig, prevWornSig, lastWaitReason, failedWanted; DateTime wornChangedAt = DateTime.MinValue, lastFailAt = DateTime.MinValue;
    int failedRounds; bool needsVerify = true; DateTime loginAt = DateTime.MinValue;
    internal string LastFitText = "feet fit: not checked yet since login";

    /// <summary>A new login: the last pressed pose is not valid across a relog, so it is cleared and everything restarts (the first stable poll verifies the fit from evidence).</summary>
    internal void NewLogin()
    {
        ClearFeetState(); prevPoll = prevSig = prevWornSig = lastWaitReason = failedWanted = null; failedRounds = 0; needsVerify = true; wornChangedAt = loginAt = Now();
        LastFitText = "feet fit: not checked yet since login";
        h.Log("feet", "new login: last pressed feet pose cleared (it is not valid across a relog); the fit will be verified from evidence");
    }

    (bool ok, string why) FeetReadyNow()
    {
        var wornSig = FeetWornSignature(h.WornPrims().Select(h.AttachItemId));
        if (wornSig != prevWornSig) { prevWornSig = wornSig; wornChangedAt = Now(); }
        return FeetPressReady((Now() - wornChangedAt).TotalSeconds, h.PoseBusy, h.PoseSettledSec, h.Sitting, (Now() - loginAt).TotalSeconds);
    }

    int loopClaimed;
    /// <summary>The loop is started from LoginAsync, i.e. once per login of the same process: only the first start may run (2026-10-28: one loop per login multiplied the polls and the presses).</summary>
    internal bool ClaimLoop() => Interlocked.Exchange(ref loopClaimed, 1) == 0;

    /// <summary>Pure: the same wanted pose is not pressed again within FeetMinPressIntervalSec (a failed verification has its own bounded rounds).</summary>
    internal const double FeetMinPressIntervalSec = 60;
    internal static bool FeetPressRateOk(string lastPressedWanted, string wanted, double sinceLastPressSec) => lastPressedWanted != wanted || sinceLastPressSec >= FeetMinPressIntervalSec;
    string lastPressWanted; DateTime lastPressAt = DateTime.MinValue;

    internal async Task FeetLoop()
    {
        if (!ClaimLoop()) { h.Log("feet", "feet loop already running: this start is ignored"); return; }
        await Task.Delay(45000); // let login attachments arrive
        ClearFeetState();        // a fresh process is a fresh login: the stored pose is not trusted
        while (true)
        {
            try { if (h.Client?.Network?.Connected == true) await FeetTick(); }
            catch (Exception ex) { h.Log("feet", "loop error: " + ex.GetBaseException().Message); }
            await Task.Delay(4000);
        }
    }

    /// <summary>One poll. Returns what it did (for tests / logs).</summary>
    internal async Task<string> FeetTick()
    {
        if (h.WornPrims().Count <= 3) return "waiting for the attachments";
        var (f, why, rules) = FeetNow();
        var sig = FeetBodySigNow(); bool bodyChanged = FeetBodyChanged(prevSig, sig); prevSig = sig;
        var (press, _) = FeetDecide(f, prevPoll, ReadFeetState(), bodyChanged);
        if (bodyChanged) { ClearFeetState(); failedRounds = 0; needsVerify = true; h.Log("feet", $"worn body changed (item/attachment id): last pressed pose cleared, the fit will be verified; signature {sig}"); }
        if (prevPoll != null && f != prevPoll) { failedRounds = 0; needsVerify = true; }
        prevPoll = f;
        if (failedWanted != f) { failedWanted = f; failedRounds = 0; }
        if (!press && !needsVerify) return "nothing to do";
        if (press && failedRounds == 0 && !FeetPressRateOk(lastPressWanted, f, (Now() - lastPressAt).TotalSeconds)) return $"press rate-limited: {f} was pressed {(Now() - lastPressAt).TotalSeconds:F0} s ago";
        if (!FeetRetryAllowed(failedRounds, (Now() - lastFailAt).TotalSeconds)) return $"feet NOT verified: {(failedRounds >= FeetGiveUpRounds ? "gave up (use `feet apply`)" : "cooling down before the next round")}";
        var (ok, wait) = FeetReadyNow();
        if (!ok) { if (wait != lastWaitReason) { lastWaitReason = wait; h.Log("feet", "feet check / press waits: " + wait); } return "waiting: " + wait; }
        lastWaitReason = null;
        if (!press)   // no press is due by the stored pose: do not trust it, look at the foot
        {
            var v = await VerifyFit(f, rules); needsVerify = false;
            if (v.Kind == FeetKind.Verified) return LogFit(true, f, v.Why);
            h.Log("feet", "feet NOT verified before any press: " + v.Why + "; pressing"); 
        }
        lastPressWanted = f; lastPressAt = Now();
        return await PressFeet(f, why, rules);
    }

    string LogFit(bool verified, string feet, string why)
    {
        LastFitText = verified ? $"feet fit verified at {Now():HH:mm:ss} ({feet}): {why}" : $"FEET NOT VERIFIED at {Now():HH:mm:ss} ({feet}): {why}";
        h.Log("feet", verified ? "feet fit verified: " + why : "feet NOT verified: " + why);
        return LastFitText;
    }

    /// <summary>Evidence of the fit now: the visible foot mesh pair (the proof), plus one informational close-up (retaken brighter when near-black, bounded).</summary>
    async Task<FeetVerdict> VerifyFit(string feet, FeetRules rules)
    {
        List<(string name, float alpha)> mesh = null;
        try { mesh = await h.FootMeshAlphas(); } catch (Exception ex) { h.Log("feet", "foot mesh read failed: " + ex.GetBaseException().Message); }
        var (shown, meshWhy) = FeetShownFromMesh(mesh);
        FeetImage img = null;
        for (int take = 0; take < FeetRetakes; take++)
        {
            img = ParseFeetImage(await SafeCloseup(FeetRetakeExposure[Math.Min(take, FeetRetakeExposure.Length - 1)]));
            if (!FeetImageInvalid(img)) break;
            if (take + 1 < FeetRetakes) await Delay(2000);
        }
        return FeetFit(feet, shown, meshWhy, img);
    }
    async Task<string> SafeCloseup(double exposure) { try { return await h.FeetCloseupJson(exposure); } catch (Exception ex) { h.Log("feet", "close-up failed: " + ex.GetBaseException().Message); return null; } }

    /// <summary>Press, then verify from evidence; retry up to FeetPressAttempts. The stored pose is written only after the fit is verified.</summary>
    async Task<string> PressFeet(string feet, string why, FeetRules rules)
    {
        if (!await feetGate.WaitAsync(0)) return "feet: a press is already running";
        try
        {
            string last = "";
            for (int attempt = 1; attempt <= FeetPressAttempts; attempt++)
            {
                var (touched, r) = await PressFeetOnce(feet, why, rules);
                if (!touched) return r;   // not ready / HUD missing: nothing pressed, not a failed round
                await Delay(4000);        // the body restarts its feet animations
                var v = await VerifyFit(feet, rules);
                last = v.Why;
                if (v.Kind == FeetKind.Verified)
                {
                    try { Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!); File.WriteAllText(StatePath, feet); } catch { }
                    failedRounds = 0; needsVerify = false;
                    return LogFit(true, feet, $"{why}, press {attempt}/{FeetPressAttempts}: {v.Why}");
                }
                h.Log("feet", $"press {attempt}/{FeetPressAttempts} ({feet}) did not fit: {v.Why}");
                ClearFeetState();
                if (attempt < FeetPressAttempts) { var (ok, wait) = FeetReadyNow(); if (!ok) { last += $" (stopped: {wait})"; break; } }
            }
            failedRounds++; lastFailAt = Now(); needsVerify = false;
            return LogFit(false, feet, $"{FeetPressAttempts} bounded attempts, still not fitting: {last}. Her feet do not fit the shoes: fix needed, never call this finished");
        }
        finally { feetGate.Release(); }
    }

    /// <summary>One HUD round trip: (touched, text). touched=false when it was not ready or the HUD would not attach (nothing pressed).</summary>
    async Task<(bool touched, string text)> PressFeetOnce(string feet, string why, FeetRules rules)
    {
        bool wasWorn = h.WornPrims().Any(p => h.AttachItemId(p) == rules.Hud), hudRoundTrip = !wasWorn;
        if (!wasWorn)
        {
            var ar = await h.AttachItem(rules.Hud, "");
            if (AttachResultAlreadyWorn(ar)) wasWorn = true;   // worn all along (restored by the sim, in the COF): ours to use, never to detach
            for (int i = 0; i < 30 && !h.WornPrims().Any(p => h.AttachItemId(p) == rules.Hud); i++) await Delay(1000);
            if (!h.WornPrims().Any(p => h.AttachItemId(p) == rules.Hud)) { h.Log("feet", "body HUD did not attach in 30 s"); return (false, "feet: body HUD did not attach"); }
            await Delay(5000); // HUD script + link names
        }
        // our own HUD attach / detach is not an outfit change: re-baseline the worn signature around it
        if (hudRoundTrip) prevWornSig = FeetWornSignature(h.WornPrims().Select(h.AttachItemId));
        var rd = FeetReadyNow();
        if (!rd.ok)
        {
            if (!wasWorn) { await h.DetachItemAsync(rules.Hud, "feet hud"); prevWornSig = FeetWornSignature(h.WornPrims().Select(h.AttachItemId)); }
            h.Log("feet", "press aborted, not ready: " + rd.why); return (false, "feet: press aborted: " + rd.why);
        }
        // the LaraX HUD only reacts on the page it SHOWS (2026-10-21 11:38 bug: f3 pressed on a hidden page, feet stayed flat):
        // open the feet page by touching that page's own tab bar first (the 'tabs' prim just above the f-buttons)
        try
        {
            var hudRoot = h.WornPrims().FirstOrDefault(p => h.AttachItemId(p) == rules.Hud);
            if (hudRoot != null)
            {
                var prims = h.LinkPrims(hudRoot); await h.EnsureProperties(h.Sim, prims);
                var btn = prims.FirstOrDefault(p => p.Properties?.Name == feet);
                int ti = btn == null ? -1 : FeetPageTabIndex(prims.Select(p => (p.Properties?.Name ?? "", p.Position.Z)).ToList(), btn.Position.Z);
                if (ti < 0) h.Log("feet", $"no 'tabs' prim found above button {feet}: the feet page could not be opened");
                if (ti >= 0)
                {
                    await h.TouchPrim(prims[ti], FeetTabSt, FeetTabFace); await Delay(1500);   // the HANDS/FEET tab (u 0.70); u 0.1 is the ALPHA tab
                    h.Log("feet", $"opened the HANDS/FEET tab on '{prims[ti].Properties?.Name}' local {prims[ti].LocalID} at st {FeetTabU:0.00},{FeetTabV:0.00} face {FeetTabFace}");
                }
            }
        }
        catch (Exception ex) { h.Log("feet", "feet page open failed: " + ex.GetBaseException().Message); }
        var r = await h.TouchAttachment($"{rules.Hud} {feet} 4 st=0.5,0.5");
        await Delay(2500);
        if (!wasWorn) { await h.DetachItemAsync(rules.Hud, "feet hud"); prevWornSig = FeetWornSignature(h.WornPrims().Select(h.AttachItemId)); }
        bool ok = r != null && !r.StartsWith("no ", StringComparison.Ordinal) && !r.Contains(" matches ");
        h.Log("feet", $"{feet} ({why}) pressed: {r}");
        return (ok, ok ? $"feet {feet} ({why}): {r}" : $"feet press did not register: {r}");
    }

    internal async Task<string> FeetCmd(string[] a)
    {
        var (f, why, rules) = FeetNow();
        if (a.Length > 1 && a[1] == "apply") { failedRounds = 0; return await PressFeet(f, why, rules); }
        if (a.Length > 1 && a[1] == "verify") { var v = await VerifyFit(f, rules); return LogFit(v.Kind == FeetKind.Verified, f, v.Why); }
        return $"feet: wanted {f} ({why}); last pressed {ReadFeetState() ?? "unknown"} (not trusted as proof); {rules.Footwear.Count} heeled item(s) listed; {LastFitText}";
    }
}
