// PoseHover.cs (2026-10-23 David: "you should always check it when you sit or change poses"; "not just for sits but for stands too")
// On every AO ground-sit pick/switch and every AO stand change: apply the pose's saved hover (pose-hover.json) first, then once
// per pose per session measure the floor contact of her posed mesh (scene export -> scene-mesher -> vision/pose_contact.py),
// nudge the hover toward the approved reference contact and save the per-pose value; one quick low-res side render
// (look self left fast) is kept for her own checking (never sent to David). Only ever POSTs the hover: never stands, sits,
// re-sits or touches the AO (tested).
// Contact e = lowest body vertex (hips/thighs sitting, soles standing) + agent_off + agent z + hover - floor. The renderer's
// body size is ~0.2 m off (render_mesh.z0_of), so e is compared to a REFERENCE e per (form, kind), never to 0: android sits =
// VAMOSITG26 at David's approved -0.12 m (14:50 PT); stands and human sits = the first pose measured at the normal pinned hover.
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
    internal const double PoseHoverTol = 0.01, PoseHoverMaxDelta = 0.30;
    // pure (tested): the hover that puts this pose's contact where the reference pose's is
    internal static double PoseHoverTarget(double curHover, double eCur, double eRef)
        => Math.Round(curHover - Math.Clamp(eCur - eRef, -PoseHoverMaxDelta, PoseHoverMaxDelta), 3);
    // pure (tested): does the check confirm the saved value (no nudge)?
    internal const double PoseHoverMaxSane = 0.15;
    // pure (tested): is a measurement believable? 16:57 a stand measured while being led read e 3.953 (ref 0.632) and set -0.30
    internal static bool PoseMeasureSane(double? floor, double e, double? eRef) => floor != null && (eRef is not double r || Math.Abs(e - r) <= PoseHoverMaxSane);
    // pure (tested): the reference contact when seeding from David's approved sit measured at hover hov: the contact it has at the approved value
    internal static double PoseRefFromApproved(double e, double hov, double approved) => e - hov + approved;
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
    internal static bool PoseHoverConfirmed(double eCur, double eRef) => Math.Abs(eCur - eRef) <= PoseHoverTol;
    internal static string PoseRefKey(bool android, string kind) => $"ref|{(android ? "android" : "human")}|{kind}";
    // pure (tested): may this pose seed the reference? android sit: only David's approved sit; otherwise the first pose measured at the pin
    internal static bool PoseSeedsRef(bool android, string kind, string pose) => kind == "sit" && android ? string.Equals(pose, HoverRefSit, StringComparison.OrdinalIgnoreCase) : true;

    // pure (tested): is the form (android/human) known for certain? 2026-10-23 15:19: right after login the worn prims had no
    // names yet, AndroidWorn() read false and an android stand was saved as human|stand (and seeded ref|human|stand).
    // Certain = the outfit name says android, or every worn attachment's name has arrived (then AndroidWorn is real).
    internal static bool FormCertain(bool outfitSaysAndroid, int wornCount, int wornNamed) => outfitSaysAndroid || (wornCount > 0 && wornNamed == wornCount);
    static bool FormCertainNow()
    {
        if (IsAndroidOutfit(currentOutfit)) return true;
        try { var w = WornPrims().Where(p => p.ParentID == client.Self.LocalID).ToList(); return FormCertain(false, w.Count, w.Count(p => !string.IsNullOrEmpty(p.Properties?.Name))); } catch { return false; }
    }
    static readonly ConcurrentDictionary<string, bool> poseChecked = new();
    static readonly SemaphoreSlim poseGate = new(1, 1);
    static readonly ConcurrentDictionary<string, DateTime> poseDue = new();

    static Dictionary<string, double> PoseNums() { try { return JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(PoseHoverFile)) ?? new(); } catch { return new(); } }
    static void PoseNumSet(string k, double v) { var d = PoseNums(); d[k] = Math.Round(v, 4); File.WriteAllText(PoseHoverFile, JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true })); }
    static string PoseContactPy => Path.Combine(Path.GetDirectoryName(LookPy) ?? ".", "pose_contact.py");

    // schedule a check a few seconds after the pose settles (debounced per pose)
    internal static void PoseHoverSchedule(string kind, string pose, int delayMs = 3000)
    {
        if (string.IsNullOrWhiteSpace(pose) || Env("GT_POSE_HOVER_CHECK", "on") == "off") return;
        var due = DateTime.Now; poseDue[kind + "|" + pose] = due; lastPoseKind = kind; lastPoseName = pose;
        _ = Task.Run(async () =>
        {
            await Task.Delay(delayMs);
            if (poseDue.TryGetValue(kind + "|" + pose, out var d0) && d0 != due) return;   // a newer change replaced it
            try { await PoseHoverCheck(kind, pose); } catch (Exception ex) { Log("posehover", $"{pose}: {ex.GetBaseException().Message}"); }
        });
    }
    // is she still in that pose? (a check must never act on a pose she has left)
    static bool PoseStill(string kind, string pose) => kind == "sit"
        ? idlePosed?.kind == "groundsit" && string.Equals(aoSitCurrentName, pose, StringComparison.OrdinalIgnoreCase) && client.Self.SittingOn == 0
        : idlePosed == null && client.Self.SittingOn == 0 && client.Self.Velocity.Length() < 0.1f && leash == null && !AoForceSitOn(SelfAnims(), false) && pose.StartsWith("stand:") && SelfAnims().Any(a => a.ToString().StartsWith(pose[6..]));

    // 2026-10-26 (David: 0.02 is too high, and the corrected stand hover did not stick): stand hovers are keyed per AO stand animation
    // id and the ids rotate, so a new id fell back to the pin (0.02). A new stand id now inherits ONE shared default, "android|stand"
    // (human|stand), 0.0 until the floor render or `hover set` while standing measures otherwise. Per-id values stay where measured.
    internal const double PoseStandDefaultBuiltin = 0.0;
    internal static string PoseStandDefaultKey(bool android) => PoseHoverKey(android, "stand");
    internal static double PoseStandDefault(IReadOnlyDictionary<string, double> nums, bool android)
        => nums != null && nums.TryGetValue(PoseStandDefaultKey(android), out var v) ? v : PoseStandDefaultBuiltin;
    // pure (tested): the hover a pose starts from: its own saved value, else (stands) the shared stand default, else (sits) the pin
    internal static double PoseStartHover(string kind, double? saved, double standDefault, double pin) => saved ?? (kind == "stand" ? standDefault : pin);
    static volatile string lastPoseKind, lastPoseName;
    // `hover set` while standing: that value is the stand default and this stand pose's value
    static void HoverSetRememberStand(double v)
    {
        if (idlePosed != null || client.Self.SittingOn != 0 || AoSitReallyOn()) return;
        bool android = AndroidFormNow(); PoseNumSet(PoseStandDefaultKey(android), v);
        if (lastPoseKind == "stand" && lastPoseName != null && PoseStill("stand", lastPoseName)) PoseNumSet(PoseHoverKey(android, lastPoseName), v);
    }
    // the pose she is really in (the AO decides sit vs stand, not the sim): (kind, pose or null)
    static (string kind, string pose) PoseNow()
    {
        if (AoSitReallyOn()) return ("sit", aoSitCurrentName);
        return ("stand", lastPoseKind == "stand" && lastPoseName != null && PoseStill("stand", lastPoseName) ? lastPoseName : null);
    }

    static async Task PoseHoverCheck(string kind, string pose)
    {
        if (!LoggedIn || !PoseStill(kind, pose)) return;
        if (!FormCertainNow()) { Log("posehover", $"{pose}: form not known yet (attachments still loading); retry in 10 s, nothing saved"); PoseHoverSchedule(kind, pose, 10000); return; }
        bool android = AndroidFormNow(); var key = PoseHoverKey(android, pose); var refKey = PoseRefKey(android, kind);
        var nums = PoseNums();
        double pin = PinnedHover();
        double? saved = nums.TryGetValue(key, out var sv) ? sv : PoseHoverLookup(nums, android, pose);
        double cur = PoseStartHover(kind, saved, PoseStandDefault(nums, android), pin);
        if (Math.Abs((SimHover() ?? pin) - cur) > 0.005) await PostHover(cur, $"pose hover: {key} saved {cur:0.000}");   // 1. apply the saved value first
        if (poseChecked.ContainsKey(key)) return;                                                                         // checked this session: reuse
        if (!await poseGate.WaitAsync(0)) { PoseHoverSchedule(kind, pose, 15000); return; }
        try
        {
            var sw = Stopwatch.StartNew();
            var ex = await SceneExport(new[] { "export", "4", "noenv" });
            var scene = ex.Split('\n').Last();
            if (!scene.EndsWith("scene.json")) { Log("posehover", $"{key}: export failed: {ex}"); return; }
            var dir = Path.GetDirectoryName(scene)!;
            // 2026-10-23 17:15 (David "Fix it"): the check is the hover-check RENDER: look.py hovercheck = her real placement
            // (agent z - size/2 + hover - posture bias, never snapped to the floor) over the scene's own floor, seen level from
            // her side at the contact; it prints the gap of her lowest vertex above that floor. Nudge by the gap, then render
            // again to confirm. Private: the images are never sent to David.
            if (!PoseStill(kind, pose)) { Log("posehover", $"{key}: pose changed while exporting; skipped"); return; }
            double hov = SimHover() ?? cur, size = SelfAv()?.Scale.Z ?? 0;
            if (size < 1) { Log("posehover", $"{key}: agent size unknown; skipped"); return; }
            double bias = PoseBiasFor(nums, android, kind, pose);
            var (g1, img1) = await HoverCheckRender(dir, size, hov, bias);
            if (g1 is not double gap || !PoseGapSane(gap)) { Log("posehover", $"{key}: render gap {g1?.ToString("+0.000;-0.000") ?? "none"} is implausible: the calibration is off for this pose (needs a per-pose bias, see PoseBiasFromPhoto); nothing changed or saved ({img1})"); return; }
            string how;
            if (PoseGapConfirmed(gap)) { PoseNumSet(key, hov); how = $"render confirms contact at {hov:0.000} (gap {gap:+0.000})"; }
            else
            {
                var t = PoseHoverFromGap(hov, gap);
                if (!PoseStill(kind, pose)) { Log("posehover", $"{key}: pose changed; skipped"); return; }
                var (ok, info) = await PostHover(t, $"pose hover: {key} render nudge");
                var (g2, img2) = await HoverCheckRender(dir, size, t, bias);   // same pose, re-rendered at the new hover
                bool good = ok && g2 is double gg && PoseGapConfirmed(gg);
                if (ok) PoseNumSet(key, t);
                how = $"render gap {gap:+0.000} at {hov:0.000} -> {t:0.000}, re-render gap {(g2 is double x ? x.ToString("+0.000;-0.000") : "?")} ({(good ? "contact confirmed" : "NOT confirmed")}): {info}; {img2}";
            }
            poseChecked[key] = true;
            if (kind == "stand" && PoseStill(kind, pose)) { try { PoseNumSet(PoseStandDefaultKey(android), PoseNums().TryGetValue(key, out var mv) ? mv : hov); } catch { } }   // the measured stand value seeds new stand ids
            Log("posehover", $"{key}: {how} in {sw.Elapsed.TotalSeconds:F0} s ({img1})");
        }
        finally { poseGate.Release(); }
    }
}
