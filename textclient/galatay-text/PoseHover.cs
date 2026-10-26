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
    static async Task<(double? gap, string img)> HoverCheckRender(string exportDir, double size, double hover, double bias)
    {
        var psi = new ProcessStartInfo(LookPython) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(LookPy); psi.ArgumentList.Add(exportDir); psi.ArgumentList.Add("hovercheck");
        foreach (var kv in HoverCheckEnv(size, hover, bias)) psi.Environment[kv.Key] = kv.Value;
        using var pr = Process.Start(psi)!; var o = pr.StandardOutput.ReadToEndAsync(); var e = pr.StandardError.ReadToEndAsync();
        if (!await Task.Run(() => pr.WaitForExit(180000))) { try { pr.Kill(); } catch { } return (null, "timed out"); }
        var text = await o; await e;
        return (HoverCheckGap(text), text.Split('\n').FirstOrDefault(l => l.EndsWith(".jpg"))?.Trim() ?? "no image");
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
    internal static string HoverPostureKind(bool aoSitPlaying, bool seatedOnObject) => seatedOnObject ? "seat" : aoSitPlaying ? "sit" : "stand";
    // pure (tested): a sit or a stand that follows a different posture (login = unknown) wants a check; a stand->stand AO rotation, or sitting on an object, does not
    internal static bool HoverCheckWantedOnPosture(string prev, string now) => now is "sit" or "stand" && prev != now;
    internal const int HoverCheckMaxTries = 6, HoverCheckRetryMs = 8000, HoverCheckSettleSec = 3, HoverCheckCooldownSec = 30;
    // pure (tested): may the check run now? (true, null) = go; (false, reason) = wait and retry; (false, "skip: ...") = never for this request
    internal static (bool go, string why) HoverCheckReady(string nowKind, float speed, bool leashed, bool following, bool poseBusy, double settledSec, double sinceLastRunSec, bool manual)
    {
        if (nowKind == "seat") return (false, "skip: sitting on an object (the normal hover is restored there)");
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
        var now = HoverPostureKind(AoForceSitOn(playing, seatedOnObject), seatedOnObject);
        var prev = Interlocked.Exchange(ref hoverPosture, now);
        if (prev == now) return;
        hoverPostureAt = DateTime.Now;
        if (HoverCheckWantedOnPosture(prev, now)) HoverCheckRequest(now, 4000);
    }
    // schedule ONE check (why: sit, stand, region or manual). A newer request replaces an older pending one; blocked states retry a few times, then give up.
    internal static void HoverCheckRequest(string why, int delayMs = 4000)
    {
        if (Env("GT_POSE_HOVER_CHECK", "on") == "off") return;
        long id = Interlocked.Increment(ref hoverReqSeq);
        Log("posehover", $"visual hover check requested ({why}), in {delayMs / 1000.0:0.#} s");
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs);
                for (int attempt = 1; ; attempt++)
                {
                    if (Interlocked.Read(ref hoverReqSeq) != id) return;   // a newer request replaced this one
                    if (!LoggedIn) return;
                    var (kind, pose) = PoseNow(); var sitting = client.Self.SittingOn != 0;
                    var (go, block) = HoverCheckReady(sitting ? "seat" : kind, client.Self.Velocity.Length(), IdleLeashedNow(), followId != UUID.Zero, idleLock.CurrentCount == 0,
                                                      (DateTime.Now - hoverPostureAt).TotalSeconds, (DateTime.Now - hoverCheckLastRun).TotalSeconds, why == "manual");
                    string retry = block;
                    if (go)
                    {
                        hoverCheckLastRun = DateTime.Now;
                        retry = await PoseHoverCheck(kind, pose);
                        if (retry == null) return;
                    }
                    else if (block.StartsWith("skip:")) { Log("posehover", $"{why}: {block}"); return; }
                    if (attempt >= HoverCheckMaxTries) { Log("posehover", $"{why}: gave up after {attempt} tries ({retry})"); return; }
                    Log("posehover", $"{why}: waiting ({retry}); try {attempt + 1}/{HoverCheckMaxTries} in {HoverCheckRetryMs / 1000} s");
                    await Task.Delay(HoverCheckRetryMs);
                }
            }
            catch (Exception ex) { Log("posehover", $"{why}: {ex.GetBaseException().Message}"); }
        });
    }
    // is she still in that pose? (a check must never act on a pose she has left). The AO's anim_list decides sit vs stand, never the sim.
    static bool PoseStill(string kind, string pose) => client.Self.SittingOn == 0 && (kind == "sit"
        ? AoSitReallyOn() && (pose == "sit" || string.Equals(aoSitCurrentName, pose, StringComparison.OrdinalIgnoreCase))
        : idlePosed == null && client.Self.Velocity.Length() < 0.1f && leash == null && !AoForceSitOn(SelfAnims(), false));
    // the pose she is really in (the AO decides sit vs stand, not the sim): (kind, pose name)
    static (string kind, string pose) PoseNow() => AoSitReallyOn() ? ("sit", aoSitCurrentName ?? "sit") : ("stand", "stand");

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
            double hov = SimHover() ?? pin, size = SelfAv()?.Scale.Z ?? 0;
            if (size < 1) { Log("posehover", $"{key}: agent size unknown; skipped"); return null; }
            double bias = PoseBiasFor(nums, android, kind, pose);
            var (g1, img1) = await HoverCheckRender(dir, size, hov, bias);
            if (g1 is not double gap || !PoseGapSane(gap)) { Log("posehover", $"{key}: render gap {g1?.ToString("+0.000;-0.000") ?? "none"} is implausible: the calibration is off for this pose (needs a per-pose bias, see PoseBiasFromPhoto); nothing changed ({img1})"); return null; }
            string how;
            if (PoseGapConfirmed(gap)) how = $"render confirms contact at {hov:0.000} (gap {gap:+0.000}); hover left alone";
            else
            {
                var t = PoseHoverFromGap(hov, gap);
                if (!PoseStill(kind, pose)) { Log("posehover", $"{key}: pose changed; skipped"); return null; }
                var (ok, info) = await PostHover(t, $"visual hover fix: {key} (applied now, not saved)");
                var (g2, img2) = await HoverCheckRender(dir, size, t, bias);   // same pose, re-rendered at the new hover
                bool good = ok && g2 is double gg && PoseGapConfirmed(gg);
                how = $"render gap {gap:+0.000} at {hov:0.000} -> {t:0.000}, re-render gap {(g2 is double x ? x.ToString("+0.000;-0.000") : "?")} ({(good ? "contact confirmed" : "NOT confirmed")}): {info}; {img2}";
            }
            Log("posehover", $"{key}: {how} in {sw.Elapsed.TotalSeconds:F0} s ({img1})");
            return null;
        }
        finally { poseGate.Release(); }
    }
}
