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
        var due = DateTime.Now; poseDue[kind + "|" + pose] = due;
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

    static async Task PoseHoverCheck(string kind, string pose)
    {
        if (!LoggedIn || !PoseStill(kind, pose)) return;
        if (!FormCertainNow()) { Log("posehover", $"{pose}: form not known yet (attachments still loading); retry in 10 s, nothing saved"); PoseHoverSchedule(kind, pose, 10000); return; }
        bool android = AndroidFormNow(); var key = PoseHoverKey(android, pose); var refKey = PoseRefKey(android, kind);
        var nums = PoseNums();
        double pin = PinnedHover();
        double? saved = nums.TryGetValue(key, out var sv) ? sv : PoseHoverLookup(nums, android, pose);
        double cur = saved ?? pin;
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
            var psi = new ProcessStartInfo(LookPython) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add(PoseContactPy); psi.ArgumentList.Add(dir); psi.ArgumentList.Add(kind);
            using var pr = Process.Start(psi)!; var outT = pr.StandardOutput.ReadToEndAsync(); var errT = pr.StandardError.ReadToEndAsync();
            if (!pr.WaitForExit(90000)) { try { pr.Kill(); } catch { } Log("posehover", $"{key}: mesher timed out"); return; }
            var line = (await outT).Trim().Split('\n').LastOrDefault() ?? "";
            if (pr.ExitCode != 0 || !line.StartsWith("{")) { Log("posehover", $"{key}: contact failed: {(await errT).Trim()}"); return; }
            using var j = JsonDocument.Parse(line); var r = j.RootElement;
            if (r.GetProperty("lo").ValueKind != JsonValueKind.Number || r.GetProperty("agent_off").ValueKind != JsonValueKind.Number) { Log("posehover", $"{key}: no contact"); return; }
            if (!PoseStill(kind, pose)) { Log("posehover", $"{key}: pose changed while measuring; skipped"); return; }
            double? floorN = FloorBelow(client.Self.SimPosition); double floor = floorN ?? 0, hov = SimHover() ?? cur;
            double e = r.GetProperty("lo").GetDouble() + r.GetProperty("agent_off").GetDouble() + r.GetProperty("me_z").GetDouble() + hov - floor;
            if (!PoseMeasureSane(floorN, e, nums.TryGetValue(refKey, out var rr) ? rr : null)) { Log("posehover", $"{key}: implausible contact e {e:0.000} (floor {(floorN is double fz ? fz.ToString("0.00") : "?")}): nothing changed or saved"); return; }
            string how;
            if (!nums.TryGetValue(refKey, out var eRef))
            {
                if (!PoseSeedsRef(android, kind, pose)) { Log("posehover", $"{key}: no reference yet for {refKey} (needs {HoverRefSit} at {AndroidGroundSitHover:0.00}); kept {hov:0.000}"); return; }
                // 16:17: G26 seeded the reference at hover 0.000 instead of David's approved -0.12, skewing every android sit
                double appr = kind == "sit" && android ? AndroidGroundSitHover : hov;
                var er = PoseRefFromApproved(e, hov, appr);
                PoseNumSet(refKey, er); PoseNumSet(key, appr); if (Math.Abs(appr - hov) > 0.005) await PostHover(appr, $"pose hover: {key} approved {appr:0.000}");
                how = $"reference {refKey} = this pose at {appr:0.000} (contact e {er:0.000})";
            }
            else if (PoseHoverConfirmed(e, eRef)) { PoseNumSet(key, hov); how = $"confirmed {hov:0.000} (e {e:0.000} vs ref {eRef:0.000})"; }
            else
            {
                var t = PoseHoverTarget(hov, e, eRef);
                var (ok, info) = await PostHover(t, $"pose hover: {key} nudge");
                if (ok) PoseNumSet(key, t);
                how = $"nudged {hov:0.000} -> {t:0.000} (e {e:0.000} vs ref {eRef:0.000}): {info}";
            }
            poseChecked[key] = true;
            Log("posehover", $"{key}: {how} in {sw.Elapsed.TotalSeconds:F0} s");
            // one quick low-res side view for her own checking (internal: never sent to David)
            _ = Task.Run(async () => { try { var l = await LookCmd(new[] { "self", "left", "fast" }); Log("posehover", $"{key}: side render {l.Split('\n').FirstOrDefault()}"); } catch { } });
        }
        finally { poseGate.Release(); }
    }
}
