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

    /// <summary>Pure: signature of the worn Maitreya/LaraX body = sorted "itemId/primCount" of its attachment roots (name-matched via BodyFamily);
    /// a new, re-attached or changed body (new item/attachment id, or its prims arriving/changing) gives a different string.</summary>
    internal static string FeetBodySignature(IEnumerable<(UUID item, string name, int childPrims)> worn) =>
        string.Join(",", (worn ?? Array.Empty<(UUID, string, int)>()).Where(w => Program.BodyFamily(w.name) == "larax").Select(w => $"{w.item}/{w.childPrims}").OrderBy(x => x, StringComparer.Ordinal));

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
        return FeetBodySignature(worn.Select(p => (h.AttachItemId(p), p.Properties?.Name, h.Sim?.ObjectsPrimitives.Values.Count(c => c != null && c.ParentID == p.LocalID) ?? 0)));
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
    // verified from evidence (the heel-foot animations + a `look self feet` close-up analysed for bare skin), never from feet-state.txt or the press log.
    internal const double FeetOutfitStableSec = 8, FeetPoseSettleSec = 5, FeetRetryCooldownSec = 90;
    internal const int FeetPressAttempts = 3, FeetRetakes = 3, FeetGiveUpRounds = 3;
    internal const double FeetMinLuma = 0.10, FeetMinP95Luma = 0.25, FeetSkinMaxFrac = 0.004;
    internal static readonly double[] FeetRetakeExposure = { 0.5, 2.5, 4.5 };   // each retake of an unusable (near-black) close-up is brighter
    // the heel-foot animations the Maitreya body restarts (from its childprim object) when a heeled foot pose is on
    internal static readonly UUID[] HeelFeetAnims = { new("08630d35-8417-803b-d9f9-946af813af65"), new("08d66d19-29ca-f6b3-492d-932e5e19c5f5") };

    /// <summary>Pure: signature of everything worn (sorted item ids): a change means an outfit change is (or just was) in progress.</summary>
    internal static string FeetWornSignature(IEnumerable<UUID> worn) => string.Join(",", (worn ?? Array.Empty<UUID>()).Where(u => u != UUID.Zero).Select(u => u.ToString()).OrderBy(x => x, StringComparer.Ordinal));

    /// <summary>Pure: may a feet press happen now? Not while the outfit is changing, a pose / sit transition runs, the posture just changed, or she sits (a press must be verifiable standing).</summary>
    internal static (bool ok, string why) FeetPressReady(double outfitStableSec, bool poseBusy, double poseSettledSec, bool sitting)
    {
        if (outfitStableSec < FeetOutfitStableSec) return (false, $"outfit change in progress (worn attachments changed {outfitStableSec:F0} s ago, need {FeetOutfitStableSec:F0} s stable)");
        if (poseBusy) return (false, "a pose / sit transition is in progress");
        if (poseSettledSec < FeetPoseSettleSec) return (false, "the posture changed a moment ago");
        if (sitting) return (false, "sitting: the feet are pressed and verified only while standing");
        return (true, null);
    }

    /// <summary>Pure: automatic attempts are bounded: after FeetGiveUpRounds failed rounds stop (until the wanted pose, the body or the login changes); between rounds cool down.</summary>
    internal static bool FeetRetryAllowed(int failedRounds, double sinceFailSec) => failedRounds < FeetGiveUpRounds && (failedRounds == 0 || sinceFailSec >= FeetRetryCooldownSec);

    /// <summary>What a `look self feet` close-up showed (vision/feet_fit.py): Readable=false or near-black = unusable.</summary>
    internal sealed record FeetImage(bool Readable, double MeanLuma, double P95Luma, double SkinFootFrac, string Path, string Why);

    /// <summary>Pure: parse feet_fit.py's json line; null / garbage = an unreadable image (never a pass).</summary>
    internal static FeetImage ParseFeetImage(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json ?? ""); var r = d.RootElement;
            double N(string k) => r.TryGetProperty(k, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : 0;
            return new FeetImage(r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True, N("mean_luma"), N("p95_luma"), N("skin_foot_frac"),
                r.TryGetProperty("image", out var im) ? im.GetString() : null, r.TryGetProperty("why", out var w) ? w.GetString() : null);
        }
        catch { return new FeetImage(false, 0, 0, 0, null, "no / unreadable analysis"); }
    }
    /// <summary>Pure: a close-up is unusable (must be retaken, never judged) when it is unreadable or near-black.</summary>
    internal static bool FeetImageInvalid(FeetImage i) => i == null || !i.Readable || i.MeanLuma < FeetMinLuma || i.P95Luma < FeetMinP95Luma;

    internal enum FeetKind { Verified, Failed, Invalid }
    internal sealed record FeetVerdict(FeetKind Kind, string Why);

    /// <summary>Pure: is the foot fit proven? Evidence only: the heel-foot animations and the close-up. A heeled shoe wanted with no heel-foot animation = a flat foot beside heeled boots (FAIL);
    /// a flat foot wanted with the heel animations playing = FAIL; skin-coloured foot pixels in the close-up = a bare foot / toes outside the shoe (FAIL, never explained away as "inside the
    /// opening"); an unusable (near-black) or missing close-up = INVALID: retake, and without a valid one it is not verified. Neither feet-state.txt nor the press log is an input.</summary>
    internal static FeetVerdict FeetFit(bool heelWanted, bool heelAnimsPlaying, FeetImage img)
    {
        if (heelWanted && !heelAnimsPlaying) return new(FeetKind.Failed, "a heeled shoe is worn but the heel-foot animations are not playing: the foot is flat beside heeled boots");
        if (!heelWanted && heelAnimsPlaying) return new(FeetKind.Failed, "no heeled shoe is worn but the heel-foot animations are playing: heeled feet in a flat shoe");
        if (FeetImageInvalid(img)) return new(FeetKind.Invalid, $"the feet close-up is unusable ({img?.Why ?? "no image"}): retake");
        if (img.SkinFootFrac > FeetSkinMaxFrac) return new(FeetKind.Failed, $"bare foot / toes visible outside the shoe in the close-up ({img.SkinFootFrac * 100:0.00}% of the foot zone is skin-coloured, limit {FeetSkinMaxFrac * 100:0.0}%): {img.Path}");
        return new(FeetKind.Verified, $"no bare foot in the close-up ({img.SkinFootFrac * 100:0.00}% skin in the foot zone) and the heel-foot animations {(heelWanted ? "play" : "are off")}: {img.Path}");
    }

    // ---- state of the loop ----
    internal Func<DateTime> Now = () => DateTime.Now;
    internal Func<int, Task> Delay = ms => Task.Delay(ms);
    string prevPoll, prevSig, prevWornSig, lastWaitReason, failedWanted; DateTime wornChangedAt = DateTime.MinValue, lastFailAt = DateTime.MinValue;
    int failedRounds; bool needsVerify = true;
    internal string LastFitText = "feet fit: not checked yet since login";

    /// <summary>A new login: the last pressed pose is not valid across a relog, so it is cleared and everything restarts (the first stable poll verifies the fit from evidence).</summary>
    internal void NewLogin()
    {
        ClearFeetState(); prevPoll = prevSig = prevWornSig = lastWaitReason = failedWanted = null; failedRounds = 0; needsVerify = true; wornChangedAt = Now();
        LastFitText = "feet fit: not checked yet since login";
        h.Log("feet", "new login: last pressed feet pose cleared (it is not valid across a relog); the fit will be verified from evidence");
    }

    (bool ok, string why) FeetReadyNow()
    {
        var wornSig = FeetWornSignature(h.WornPrims().Select(h.AttachItemId));
        if (wornSig != prevWornSig) { prevWornSig = wornSig; wornChangedAt = Now(); }
        return FeetPressReady((Now() - wornChangedAt).TotalSeconds, h.PoseBusy, h.PoseSettledSec, h.Sitting);
    }

    internal async Task FeetLoop()
    {
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
        if (bodyChanged) { ClearFeetState(); failedRounds = 0; needsVerify = true; h.Log("feet", "worn body changed: last pressed pose cleared, the fit will be verified"); }
        if (prevPoll != null && f != prevPoll) { failedRounds = 0; needsVerify = true; }
        prevPoll = f;
        if (failedWanted != f) { failedWanted = f; failedRounds = 0; }
        if (!press && !needsVerify) return "nothing to do";
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
        return await PressFeet(f, why, rules);
    }

    string LogFit(bool verified, string feet, string why)
    {
        LastFitText = verified ? $"feet fit verified at {Now():HH:mm:ss} ({feet}): {why}" : $"FEET NOT VERIFIED at {Now():HH:mm:ss} ({feet}): {why}";
        h.Log("feet", verified ? "feet fit verified: " + why : "feet NOT verified: " + why);
        return LastFitText;
    }

    /// <summary>Evidence of the fit now: animations + a close-up, retaking an unusable (near-black) close-up brighter, bounded.</summary>
    async Task<FeetVerdict> VerifyFit(string feet, FeetRules rules)
    {
        bool heel = feet != rules.Default; FeetVerdict v = null;
        for (int take = 0; take < FeetRetakes; take++)
        {
            bool heelAnims = h.SelfAnims().Any(HeelFeetAnims.Contains);
            var img = ParseFeetImage(await SafeCloseup(FeetRetakeExposure[Math.Min(take, FeetRetakeExposure.Length - 1)]));
            v = FeetFit(heel, heelAnims, img);
            if (v.Kind != FeetKind.Invalid) return v;
            h.Log("feet", $"close-up {take + 1}/{FeetRetakes} invalid: {v.Why}");
            await Delay(2000);
        }
        return new(FeetKind.Failed, $"no usable close-up after {FeetRetakes} takes ({v?.Why}): the fit cannot be proven");
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
        bool wasWorn = h.WornPrims().Any(p => h.AttachItemId(p) == rules.Hud);
        if (!wasWorn)
        {
            await h.AttachItem(rules.Hud, "");
            for (int i = 0; i < 30 && !h.WornPrims().Any(p => h.AttachItemId(p) == rules.Hud); i++) await Delay(1000);
            if (!h.WornPrims().Any(p => h.AttachItemId(p) == rules.Hud)) { h.Log("feet", "body HUD did not attach in 30 s"); return (false, "feet: body HUD did not attach"); }
            await Delay(5000); // HUD script + link names
        }
        // our own HUD attach / detach is not an outfit change: re-baseline the worn signature around it
        if (!wasWorn) prevWornSig = FeetWornSignature(h.WornPrims().Select(h.AttachItemId));
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
                if (ti >= 0)
                {
                    var none = AgentManager.TOUCH_INVALID_VECTOR; var st = new Vector3(0.1f, 0.5f, 0);
                    h.Client.Self.Grab(prims[ti].LocalID, Vector3.Zero, st, st, 4, none, none, none); await Delay(150);
                    h.Client.Self.DeGrab(prims[ti].LocalID, st, st, 4, none, none, none); await Delay(1500);
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
