// PoseHover.cs (2026-10-26 David: "I don't think you should try to save the hover heights for each pose, because the ground can
// change based on your location. When you sit, stand, or enter a new region you should visually check your hover height and fix
// it at that time.")
// NOTHING is saved or applied per pose or per AO stand any more. After a sit, a stand (judged from the AO's own anim_list, never the
// sim) and an arrival in a new region, ONE visual hover check is requested (HoverCheckRequest): a few seconds after the pose has
// settled it exports the scene, renders her side view over the real floor (look.py hovercheck) and, if her lowest vertex is not on
// the floor, POSTs a corrected hover (the problem fix) and re-renders to confirm. The value is only ever applied right then, never
// remembered. Pose first, hover after: a request waits while she is moving, leashed/following, mid pose-entry/exit or just changed
// posture. Only ever POSTs the hover: never stands, sits, re-sits or touches the AO (tested).
// pose-hover.json now only holds the renderer's calibration (bias|... keys); any other key in it is ignored.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LibreMetaverse;
namespace GalatayText;
public static partial class Program
{
    internal const double PoseHoverTol = 0.01;
    // pure (tested): per-pose bias first (2026-10-23 17:33 VAMOSITG22: the sit bias tuned in VAMOSITG26 left her ~7 cm above the
    // floor while the render said contact: the model's body-root error differs per animation), then the (form, kind) bias, then the default
    internal static string PoseBiasPoseKey(bool android, string pose) => $"bias|{(android ? "android" : "human")}|{pose}";
    internal static double PoseBiasFor(IReadOnlyDictionary<string, double> nums, bool android, string kind, string pose)
        => nums.TryGetValue(PoseBiasPoseKey(android, pose), out var a) ? a : nums.TryGetValue(PoseBiasKey(android, kind), out var b) ? b : PoseBiasDefault(kind);
    // pure (tested): David's photo is truth: the bias that makes the render's gap equal the real gap he sees
    internal static double PoseBiasFromPhoto(double biasUsed, double renderGap, double realGap) => Math.Round(biasUsed + (renderGap - realGap), 4);
    // pure (tested): has this pose / posture a bias that was set from David's own view (hover calibrate)? else the default is only a first guess
    internal static bool BiasCalibrated(IReadOnlyDictionary<string, double> nums, bool android, string kind, string pose)
        => nums != null && (nums.ContainsKey(PoseBiasPoseKey(android, pose ?? "")) || nums.ContainsKey(PoseBiasKey(android, kind)));
    internal static string PoseBiasKey(bool android, string kind) => $"bias|{(android ? "android" : "human")}|{kind}";
    // measured 2026-10-23 17:19 (android): stands render 0.2315 m high at the pin (the sim keeps her feet on the floor);
    // ground sits -0.055 m at David's approved VAMOSITG26 -0.12 m
    internal static double PoseBiasDefault(string kind) => kind == "stand" ? 0.2315 : -0.055;
    internal static bool PoseGapSane(double gap) => Math.Abs(gap) <= 0.25;
    internal static bool PoseGapConfirmed(double gap) => Math.Abs(gap) <= PoseHoverTol;
    internal static double PoseHoverFromGap(double hov, double gap) => Math.Round(Math.Clamp(hov - gap, -0.4, 0.4), 3);
    // pure (tested): the env that makes the quick render a hover check (hover offset applied, real placement, floor drawn)
    internal static Dictionary<string, string> HoverCheckEnv(double size, double hover, double bias) => new()
    {
        ["GT_HOVER_CHECK"] = "1", ["GT_AGENT_SIZE"] = size.ToString("0.000", CultureInfo.InvariantCulture),
        ["GT_HOVER"] = hover.ToString("0.000", CultureInfo.InvariantCulture), ["GT_HOVER_BIAS"] = bias.ToString("0.0000", CultureInfo.InvariantCulture),
    };
    internal static double? HoverCheckGap(string stdout)
    {
        var m = System.Text.RegularExpressions.Regex.Match(stdout ?? "", @"hover check gap ([+-]?\d+\.\d+) m");
        return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }
    // pure (tested): what the close-up is framed on, for the log: the lowest body contact of the posture
    internal static string HoverContactLabel(string kind) => kind switch { "stand" => "feet (soles)", "sit" => "hips and any thighs / heels in contact", "seat" => "hips against the seat", _ => "lowest contact" };
    // pure (tested): on furniture (sitting on an object) the contact close-up frames her HIPS against the SEAT, never her feet over the terrain, and the render uses the
    // sim's own seated placement (no hover model: hover is not the fix on furniture, the furniture's adjust is). Env for look.py / render_mesh.py
    internal static Dictionary<string, string> SeatLookEnv() => new() { ["GT_SEATED"] = "1", ["GT_SEAT_CHECK"] = "1", ["GT_HC_POSTURE"] = HoverContactLabel("seat") };
    internal const double SeatGapTol = 0.03;
    internal static double? SeatContactGap(string stdout)
    {
        var m = System.Text.RegularExpressions.Regex.Match(stdout ?? "", @"seat contact gap ([+-]?\d+\.\d+) m");
        return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }
    // pure (tested): the one-line verdict of a seated check; it never changes the hover
    internal static string SeatCheckVerdict(double? gap) => gap is not double g ? "seat surface not found in the render: framed the hips only, gap unknown"
        : Math.Abs(g) <= SeatGapTol ? $"hips on the seat (gap {g:+0.000;-0.000} m)"
        : g > 0 ? $"hips {g * 100:0.#} cm ABOVE the seat (gap {g:+0.000} m): lower her with the furniture's own adjust (AVsitter Z-, then SAVE)"
        : $"hips {-g * 100:0.#} cm sunk INTO the seat (gap {g:+0.000;-0.000} m): lift her with the furniture's own adjust (AVsitter Z+, then SAVE)";
    // pure (tested): renders one hover check costs. Each HoverCheckRender call is ONE blender run of ONE image (the contact close-up, which REPLACES the wide
    // side view: look_scope.hovercheck_view). 1 when the contact is confirmed; 2 when a fix was applied (the re-render at the hover really in force is the verification)
    internal static int HoverCheckRenderCount(bool fixApplied) => fixApplied ? 2 : 1;
    // ONE render, ONE image. kind (stand|sit) only labels what the close-up frames
    static async Task<(double? gap, string img)> HoverCheckRender(string exportDir, double size, double hover, double bias, string kind = null, bool seat = false)
    {
        var psi = new ProcessStartInfo(LookPython) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(LookPy); psi.ArgumentList.Add(exportDir); psi.ArgumentList.Add("hovercheck");
        foreach (var kv in seat ? SeatLookEnv() : HoverCheckEnv(size, hover, bias)) psi.Environment[kv.Key] = kv.Value;   // seat: her real seated placement, no hover model
        psi.Environment["GT_HC_POSTURE"] = HoverContactLabel(seat ? "seat" : kind);
        using var pr = Process.Start(psi)!; var o = pr.StandardOutput.ReadToEndAsync(); var e = pr.StandardError.ReadToEndAsync();
        if (!await Task.Run(() => pr.WaitForExit(180000))) { try { pr.Kill(); } catch { } return (null, "timed out"); }
        var text = await o; await e;
        return (seat ? SeatContactGap(text) : HoverCheckGap(text), text.Split('\n').FirstOrDefault(l => l.EndsWith(".jpg"))?.Trim() ?? "no image");
    }
    // pure (tested): is the form (android/human) known for certain? 2026-10-23 15:19: right after login the worn prims had no
    // names yet, AndroidWorn() read false and an android stand was saved as human|stand (and seeded ref|human|stand).
    // Certain = the outfit name says android, or every worn attachment's name has arrived (then AndroidWorn is real).
    internal static bool FormCertain(bool outfitSaysAndroid, int wornCount, int wornNamed) => outfitSaysAndroid || (wornCount > 0 && wornNamed == wornCount);
    static bool FormCertainNow()
    {
        if (IsAndroidOutfit(currentOutfit)) return true;
        try { var w = WornPrims().Where(p => p.ParentID == client.Self.LocalID).ToList(); return FormCertain(false, w.Count, w.Count(p => !string.IsNullOrEmpty(p.Properties?.Name))); } catch { return false; }
    }
    static readonly SemaphoreSlim poseGate = new(1, 1);

    // pure (tested): only the renderer's calibration is read from pose-hover.json; a saved hover height under any other key
    // ("android|VAMOSITG24", "android|stand", ...) is ignored, never applied
    internal static Dictionary<string, double> PoseBiasNums(IReadOnlyDictionary<string, double> all)
        => (all ?? new Dictionary<string, double>()).Where(kv => kv.Key.StartsWith("bias|", StringComparison.OrdinalIgnoreCase)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    static Dictionary<string, double> PoseNums()
    {
        try { return PoseBiasNums(JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(PoseHoverFile))); }
        catch { return new(); }
    }
    static string PoseHoverFile => Env("GT_POSE_HOVER", Path.Combine(Path.GetDirectoryName(LogPath) ?? ".", "pose-hover.json"));
    internal static string PoseHoverKey(bool android, string anim) => (android ? "android|" : "human|") + (anim ?? "").Trim();

    // ---- when a visual hover check is requested ----
    // pure (tested): the posture from the AO's own anim_list: "seat" (on an object), "sit" (an AO/built-in ground sit playing), else "stand"
    // 2026-10-26: "unknown" = an AO animation that is in neither the sit nor the stand list plays (2941ee1d kept her kneeling while the status said standing):
    // never treated as a stand, so no hover check runs on it
    internal static string HoverPostureKind(bool aoSitPlaying, bool seatedOnObject, bool unclassifiedAoAnim = false) => seatedOnObject ? "seat" : aoSitPlaying ? "sit" : unclassifiedAoAnim ? "unknown" : "stand";
    // pure (tested): a sit or a stand that follows a different posture (login = unknown) wants a check; a stand->stand AO rotation, or sitting on an object, does not
    internal static bool HoverCheckWantedOnPosture(string prev, string now) => now is "sit" or "stand" && prev != now;
    internal const int HoverCheckMaxTries = 6, HoverCheckRetryMs = 8000, HoverCheckSettleSec = 3, HoverCheckCooldownSec = 30;
    // pure (tested): may the check run now? (true, null) = go; (false, reason) = wait and retry; (false, "skip: ...") = never for this request
    internal static (bool go, string why) HoverCheckReady(string nowKind, float speed, bool leashed, bool following, bool poseBusy, double settledSec, double sinceLastRunSec, bool manual)
    {
        if (nowKind == "seat") return (false, "skip: sitting on an object (the normal hover is restored there)");
        if (nowKind == "unknown") return (false, "unclassified AO animation playing (may be a sit): not a stand, no check");
        if (leashed) return (false, "leashed");
        if (following || speed >= 0.1f) return (false, "moving");
        if (poseBusy) return (false, "a pose is being entered or left");
        if (settledSec < HoverCheckSettleSec) return (false, "posture changed a moment ago");
        if (!manual && sinceLastRunSec < HoverCheckCooldownSec) return (false, "another check ran a moment ago");
        return (true, null);
    }
    static long hoverReqSeq; static string hoverPosture; static DateTime hoverPostureAt = DateTime.MinValue, hoverCheckLastRun = DateTime.MinValue;
    // from every own-animation packet and the idle tick: note the posture; a sit or stand after another posture requests ONE check
    static void HoverPostureNote(IEnumerable<UUID> playing, bool seatedOnObject)
    {
        playing = playing.ToList();
        var now = HoverPostureKind(AoForceSitOn(playing, seatedOnObject), seatedOnObject, !seatedOnObject && AoUnclassifiedNow(playing).Count > 0);
        var prev = Interlocked.Exchange(ref hoverPosture, now);
        if (prev == now) return;
        hoverPostureAt = DateTime.Now;
        if (HoverCheckWantedOnPosture(prev, now)) _ = HoverCheckRequest(now, 4000);
    }
    // schedule ONE check (why: sit, stand, region or manual). A newer request replaces an older pending one; blocked states retry a few times, then give up.
    // returns the task that ends with the check's one-line result (what a manual `hover check` waits for and returns)
    static volatile string hoverCheckResult = "";
    internal static Task<string> HoverCheckRequest(string why, int delayMs = 4000)
    {
        if (Env("GT_POSE_HOVER_CHECK", "on") == "off") return Task.FromResult("hover check is off (GT_POSE_HOVER_CHECK=off)");
        long id = Interlocked.Increment(ref hoverReqSeq);
        Log("posehover", $"visual hover check requested ({why}), in {delayMs / 1000.0:0.#} s");
        return Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs);
                if (why == "manual" && client.Self.SittingOn != 0) return await SeatContactCheck();   // on furniture: report the hips-to-seat gap, leave the hover alone
                for (int attempt = 1; ; attempt++)
                {
                    if (Interlocked.Read(ref hoverReqSeq) != id) return "replaced by a newer hover check request";   // a newer request replaced this one
                    if (!LoggedIn) return "not logged in";
                    var (kind, pose) = PoseNow(); var sitting = client.Self.SittingOn != 0;
                    var (go, block) = HoverCheckReady(sitting ? "seat" : kind, client.Self.Velocity.Length(), IdleLeashedNow(), followId != UUID.Zero, idleLock.CurrentCount == 0,
                                                      (DateTime.Now - hoverPostureAt).TotalSeconds, (DateTime.Now - hoverCheckLastRun).TotalSeconds, why == "manual");
                    string retry = block;
                    if (go)
                    {
                        hoverCheckLastRun = DateTime.Now;
                        retry = await PoseHoverCheck(kind, pose);
                        if (retry == null) return hoverCheckResult;
                    }
                    else if (block.StartsWith("skip:")) { Log("posehover", $"{why}: {block}"); return $"{why}: {block}"; }
                    if (attempt >= HoverCheckMaxTries) { Log("posehover", $"{why}: gave up after {attempt} tries ({retry})"); return $"{why}: gave up after {attempt} tries ({retry})"; }
                    Log("posehover", $"{why}: waiting ({retry}); try {attempt + 1}/{HoverCheckMaxTries} in {HoverCheckRetryMs / 1000} s");
                    await Task.Delay(HoverCheckRetryMs);
                }
            }
            catch (Exception ex) { Log("posehover", $"{why}: {ex.GetBaseException().Message}"); return $"{why}: error {ex.GetBaseException().Message}"; }
        });
    }
    // is she still in that pose? (a check must never act on a pose she has left). The AO's anim_list decides sit vs stand, never the sim.
    static bool PoseStill(string kind, string pose) => client.Self.SittingOn == 0 && (kind == "sit"
        ? AoSitReallyOn() && (pose == "sit" || string.Equals(aoSitCurrentName, pose, StringComparison.OrdinalIgnoreCase))
        : kind == "stand" && idlePosed == null && client.Self.Velocity.Length() < 0.1f && leash == null && !AoForceSitOn(SelfAnims(), false) && AoUnclassifiedNow().Count == 0);
    // the pose she is really in (the AO decides sit vs stand, not the sim): (kind, pose name)
    static (string kind, string pose) PoseNow() => AoSitReallyOn() ? ("sit", aoSitCurrentName ?? "sit") : AoUnclassifiedNow().Count > 0 ? ("unknown", "unknown") : ("stand", "stand");

    // seated on an object (2026-10-26, David: "when you're sitting, you should be checking your hips, not your feet"): ONE render of the hips against the seat;
    // reports the gap and the close-up path; changes nothing (the AO, the hover and the furniture stay as they are)
    static async Task<string> SeatContactCheck()
    {
        if (!LoggedIn || client.Self.SittingOn == 0) return "seat check: not sitting on an object";
        if (!await poseGate.WaitAsync(0)) return "seat check: a check is already running";
        try
        {
            var ex = await SceneExport(new[] { "export", "4", "noenv" });
            var scene = ex.Split('\n').Last();
            if (!scene.EndsWith("scene.json")) { Log("posehover", $"seat check: export failed: {ex}"); return "seat check: export failed"; }
            if (client.Self.SittingOn == 0) return "seat check: she is no longer sitting on an object";
            var (gap, img) = await HoverCheckRender(Path.GetDirectoryName(scene)!, 0, 0, 0, "seat", seat: true);
            var r = $"seat: {SeatCheckVerdict(gap)}; hover left alone; renders 1; close-up of {HoverContactLabel("seat")}: {img}";
            Log("posehover", r); hoverCheckResult = r; return r;
        }
        finally { poseGate.Release(); }
    }

    // the visual check: null = finished (checked, or nothing to do); a string = not now, retry (the reason)
    static async Task<string> PoseHoverCheck(string kind, string pose)
    {
        if (!LoggedIn || !PoseStill(kind, pose)) return "not in that pose";
        if (!FormCertainNow()) return "form not known yet (attachments still loading)";
        bool android = AndroidFormNow(); var key = PoseHoverKey(android, pose);
        var nums = PoseNums();
        double pin = PinnedHover();
        if (!await poseGate.WaitAsync(0)) return "a check is already running";
        try
        {
            var sw = Stopwatch.StartNew();
            var ex = await SceneExport(new[] { "export", "4", "noenv" });
            var scene = ex.Split('\n').Last();
            if (!scene.EndsWith("scene.json")) { Log("posehover", $"{key}: export failed: {ex}"); return null; }
            var dir = Path.GetDirectoryName(scene)!;
            // 2026-10-23 17:15 (David "Fix it"): the check is the hover-check RENDER: look.py hovercheck = her real placement
            // (agent z - size/2 + hover - posture bias, never snapped to the floor) over the scene's own floor, seen level from
            // her side at the contact; it prints the gap of her lowest vertex above that floor. Nudge by the gap, then render
            // again to confirm. Private: the images are never sent to David. The result is applied now and NOT remembered.
            if (!PoseStill(kind, pose)) { Log("posehover", $"{key}: pose changed while exporting; skipped"); return null; }
            // 2026-10-26: the hover IN FORCE (AgentPreferences, what David's viewer applies), read fresh. Our own appearance value stayed 0 after a
            // POST, so every re-render drew her at 0 and "contact confirmed" was arithmetic (David: sunk below the ground at the "confirmed" -0.186)
            var (effHov, effSrc) = await EffectiveHoverFresh();
            double hov = effHov ?? pin, size = SelfAv()?.Scale.Z ?? 0;
            bool calibrated = BiasCalibrated(nums, android, kind, pose);
            if (size < 1) { Log("posehover", $"{key}: agent size unknown; skipped"); return null; }
            double bias = PoseBiasFor(nums, android, kind, pose);
            var (g1, img1) = await HoverCheckRender(dir, size, hov, bias, kind);   // render 1: the contact close-up (replaces the wide side view)
            string finalImg = img1;
            if (g1 is not double gap || !PoseGapSane(gap)) { var bad = $"{key}: render gap {g1?.ToString("+0.000;-0.000") ?? "none"} is implausible: the calibration is off for this pose (needs a per-pose bias, see PoseBiasFromPhoto); nothing changed ({img1})"; Log("posehover", bad); hoverCheckResult = bad; return null; }
            string how; bool fixedHover = false;
            string calTxt = calibrated ? "bias calibrated against David's viewer" : "DEFAULT bias, not yet calibrated against David's viewer (hover calibrate)";
            if (PoseGapConfirmed(gap)) how = $"render confirms contact at {hov:0.000} ({effSrc}; gap {gap:+0.000}; {calTxt}); hover left alone";
            else
            {
                var t = PoseHoverFromGap(hov, gap);
                if (!PoseStill(kind, pose)) { Log("posehover", $"{key}: pose changed; skipped"); return null; }
                var (ok, info) = await PostHover(t, $"visual hover fix: {key} (applied now, not saved)");
                // verify: the cap must now hold t (fresh GET), and the re-render is drawn at THAT value, not at the value we meant to set
                var (eff2, src2) = await EffectiveHoverFresh();
                bool applied = ok && eff2 is double e2 && Math.Abs(e2 - t) <= 0.002;
                var (g2, img2) = await HoverCheckRender(dir, size, eff2 ?? t, bias, kind);   // render 2 (only after a fix): the verification at the hover really in force
                fixedHover = true; finalImg = img2;
                bool good = applied && g2 is double gg && PoseGapConfirmed(gg);
                string verdict = good ? (calibrated ? "contact confirmed" : "contact in the render; NOT verified against David's viewer (default bias)") : !applied ? $"NOT verified: the hover in force is {(eff2 is double q ? q.ToString("0.000") : "unknown")} ({src2}), not {t:0.000}" : "NOT confirmed";
                how = $"render gap {gap:+0.000} at {hov:0.000} -> {t:0.000}, re-render at {(eff2 ?? t):0.000} ({src2}) gap {(g2 is double x ? x.ToString("+0.000;-0.000") : "?")} ({verdict}; {calTxt}): {info}; {img2}";
            }
            int renders = HoverCheckRenderCount(fixedHover);
            Log("posehover", $"{key}: {how} in {sw.Elapsed.TotalSeconds:F0} s ({img1}); renders {renders}; close-up of {HoverContactLabel(kind)}: {finalImg}");
            hoverCheckResult = $"{key}: {how}; renders {renders}; close-up of {HoverContactLabel(kind)}: {finalImg}" + (fixedHover ? $" (before the fix: {img1})" : "");
            return null;
        }
        finally { poseGate.Release(); }
    }
}
