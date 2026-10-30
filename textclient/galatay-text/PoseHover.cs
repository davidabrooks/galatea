// 2026-10-28: `Hover` (this file, HoverCalibrate.cs, AutoHover.cs) is its own class; everything it uses from the rest of the client goes through IHoverHost (HoverHost.cs).
// PoseHover.cs (2026-10-26 David: "I don't think you should try to save the hover heights for each pose, because the ground can
// change based on your location. When you sit, stand, or enter a new region you should visually check your hover height and fix
// it at that time.")
// NOTHING is saved or applied per pose or per AO stand any more. After a sit, a stand (judged from the AO's own anim_list, never the
// sim) and an arrival in a new region, ONE visual hover check is requested (HoverCheckRequest): a few seconds after the pose has
// settled it exports the scene, renders her side view over the real floor (look.py hovercheck) and, if her lowest vertex is not on
// the floor, POSTs a corrected hover (the problem fix) and re-renders to confirm. The value is only ever applied right then, never
// remembered. Pose first, hover after: a request waits while she is moving, leashed/following, mid pose-entry/exit or just changed
// posture. Only ever POSTs the hover: never sits or re-sits, and never presses the AO. The one exception: an implausible render gap while on / next to a furniture
// footprint (HoverGapOnFurniture) stands her up (idle stop path, ground sit only), steps to clear floor and re-checks; the hover is never changed from an implausible render.
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
using FurnitureBox = GalatayText.Program.FurnitureBox;
using HomeSeatInfo = GalatayText.Program.HomeSeatInfo;
using NavGrid = GalatayText.Program.NavGrid;
namespace GalatayText;
internal sealed partial class Hover
{
    readonly IHoverHost h;
    internal Hover(IHoverHost host) { h = host; }
    internal const double PoseHoverTol = 0.01;
    // pure (tested): per-pose bias first (2026-10-23 17:33 VAMOSITG22: the sit bias tuned in VAMOSITG26 left her ~7 cm above the
    // floor while the render said contact: the model's body-root error differs per animation), then the (form, kind) bias, then the default
    internal static string PoseBiasPoseKey(bool android, string pose) => $"bias|{(android ? "android" : "human")}|{pose}";
    internal static double PoseBiasFor(IReadOnlyDictionary<string, double> nums, bool android, string kind, string pose)
        => nums.TryGetValue(PoseBiasPoseKey(android, pose), out var a) ? a : nums.TryGetValue(PoseBiasKey(android, kind), out var b) ? b : PoseBiasDefault(kind, android);
    // pure (tested): David's photo is truth: the bias that makes the render's gap equal the real gap he sees
    internal static double PoseBiasFromPhoto(double biasUsed, double renderGap, double realGap) => Math.Round(biasUsed + (renderGap - realGap), 4);
    // pure (tested): has this pose / posture a bias that was set from David's own view (hover calibrate)? else the default is only a first guess
    internal static bool BiasCalibrated(IReadOnlyDictionary<string, double> nums, bool android, string kind, string pose)
        => nums != null && (nums.ContainsKey(PoseBiasPoseKey(android, pose ?? "")) || nums.ContainsKey(PoseBiasKey(android, kind)));
    internal static string PoseBiasKey(bool android, string kind) => $"bias|{(android ? "android" : "human")}|{kind}";
    // measured 2026-10-23 17:19 (android): stands render 0.2315 m high at the pin (the sim keeps her feet on the floor);
    // ground sits -0.055 m at David's approved VAMOSITG26 -0.12 m
    // 2026-10-29 21:36-21:40 (David's viewer is truth): the HUMAN form used these ANDROID numbers, so the render and his viewer disagreed:
    // stand: render contact at +0.009, David sees contact at -0.04 (render floor ~4.9 cm high) -> 0.2315 - 0.049 = 0.1825;
    // VAMOSITG12 ground sit: render contact at -0.30, David sees ~13 cm sunk there (contact ~ -0.17) -> -0.055 + 0.13 = 0.075.
    // First guesses from David's eyes; `hover calibrate` still overrides per kind / pose.
    internal const double HumanStandBiasDefault = 0.1825, HumanSitBiasDefault = 0.075;
    internal static double PoseBiasDefault(string kind, bool android = true) =>
        android ? (kind == "stand" ? 0.2315 : -0.055) : (kind == "stand" ? HumanStandBiasDefault : HumanSitBiasDefault);
    internal static bool PoseGapSane(double gap) => Math.Abs(gap) <= 0.25;
    // 2026-10-29 21:38 (David's own screenshot): VAMOSITG12 on clear Peronaut floor really floated +0.28 m; the 0.25 m cap rejected a correct render as
    // "implausible" and nothing was fixed. A ground sit on clear floor (not on / within HoverFurnitureNearM of a footprint) may apply up to GroundSitGapMax;
    // fixes above HoverFixMax still need the second consistent render (HoverFixConfirmed). Stands and sits on furniture keep the 0.25 m cap.
    internal const double GroundSitGapMax = 0.45;
    internal static bool PoseGapSane(double gap, string kind, bool nearFurniture) =>
        PoseGapSane(gap) || (kind == "sit" && !nearFurniture && Math.Abs(gap) <= GroundSitGapMax);
    internal static bool PoseGapConfirmed(double gap) => Math.Abs(gap) <= PoseHoverTol;
    // ---- ground sits whose floor contact is not (only) the hips (2026-10-27 David: VAMOSITG13 / 11 rest on their hands, VAMOSITG27 on chest / forearms / thighs) ----
    // David's rule: look at the ground the way he does and adjust to it; the ground types differ, so NO per-pose hover value and NO per-pose offset. The ONE close-up render measures, against the floor
    // surface drawn in that render, the visible gap under every body part (render_mesh.py `body_part_gaps`: hips, chest, hands, forearms, thighs, shins, feet; `GT: hover check parts: ...`).
    // The AO pose name only CHOOSES WHICH PARTS SUPPORT HER (data below, never a number). The hover then goes where the LOWEST supporting part rests on the floor or is just above it:
    // every supporting part is on or above the floor (hands never below it: they aim HandsAimM above), and nothing is lifted more than that. A pose not listed keeps the
    // whole-body lowest-vertex gap as before. Nothing absolute is saved or hardcoded.
    internal const double HandsAimM = 0.008;
    internal static readonly Dictionary<string, string[]> SupportPartsOfPose = new(StringComparer.OrdinalIgnoreCase)
    {
        ["VAMOSITG13"] = new[] { "hips", "hands" },    // hips on the floor, both hands propped behind her
        ["VAMOSITG12"] = new[] { "hips", "hands" },    // hand-supported too
        ["VAMOSITG11"] = new[] { "hips", "hands" },    // leaning back on both hands
        ["VAMOSITG27"] = new[] { "chest", "forearms", "thighs" },   // belly down: the hips do not count
    };
    internal static readonly string[] BodyParts = { "hips", "chest", "hands", "forearms", "thighs", "shins", "feet" };
    // pure (tested): the body parts that support her in this AO ground sit; null = unlisted pose (the whole-body lowest gap, as before)
    internal static string[] SupportParts(string kind, string pose) => kind == "sit" && pose != null && SupportPartsOfPose.TryGetValue(pose.Trim(), out var p) ? p : null;
    // pure (tested): "GT: hover check parts: hips +0.012, hands -0.040, chest +0.150" -> part -> gap above the floor (m); parts the render could not see are absent
    internal static Dictionary<string, double> HoverCheckPartGaps(string stdout)
    {
        var d = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var line = (stdout ?? "").Split('\n').LastOrDefault(l => l.Contains("hover check parts:"));
        if (line == null) return d;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(line[(line.IndexOf("hover check parts:") + 18)..], @"([a-z]+) ([+-]\d+\.\d+)"))
            d[m.Groups[1].Value] = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        return d;
    }
    // pure (tested): the gap that drives the fix: the LOWEST gap among the supporting parts the render measured (every part on or above the floor once it is zero); null = none measured
    internal static double? SupportGap(IEnumerable<string> parts, IReadOnlyDictionary<string, double> gaps)
    {
        var g = (parts ?? Enumerable.Empty<string>()).Where(p => gaps != null && gaps.ContainsKey(p)).Select(p => gaps[p]).ToList();
        return g.Count == 0 ? null : g.Min();
    }
    // pure (tested): where the lowest supporting part is aimed above the floor: HandsAimM when hands support her (never below the floor), else on it
    internal static double SupportAim(IEnumerable<string> parts) => parts != null && parts.Contains("hands") ? HandsAimM : 0.0;
    // pure (tested): is the lowest supporting part where it is aimed? (effective gap = support gap - aim)
    internal static bool SupportConfirmed(double effGap, double aim) => Math.Abs(effGap) <= (aim > 0 ? aim : PoseHoverTol);
    // pure (tested): "hips +0.012, hands -0.040" for the log, in the order of the supporting parts
    internal static string SupportGapsText(IEnumerable<string> parts, IReadOnlyDictionary<string, double> gaps) =>
        string.Join(", ", (parts ?? Enumerable.Empty<string>()).Select(p => gaps != null && gaps.TryGetValue(p, out var v) ? $"{p} {v:+0.000;-0.000;+0.000}" : $"{p} not seen"));
    // pure (tested): the pose changed since the last finished check (an automatic rotation): the hover in force may be one set for the OTHER pose, by hand or by its check.
    // It is never trusted: this check measures the new pose against its own target (the render at the hover in force) and corrects, it does not inherit "contact" from the old value
    internal static bool PoseChangedSinceCheck(string lastPose, string pose) => lastPose != null && !string.Equals(lastPose.Trim(), (pose ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
    string hoverLastCheckedPose;
    volatile Dictionary<string, double> hoverLastPartGaps = new();   // the part gaps of the last HoverCheckRender (every body part the render saw)
    internal static double PoseHoverFromGap(double hov, double gap) => Math.Round(Math.Clamp(hov - gap, -0.4, 0.4), 3);
    // pure (tested): the env that makes the quick render a hover check (hover offset applied, real placement, floor drawn)
    // parts = the body parts that support her in this pose (SupportParts): the renderer frames them in the close-up; it reports every part's gap either way
    internal static Dictionary<string, string> HoverCheckEnv(double size, double hover, double bias, string[] parts = null)
    {
        var env = new Dictionary<string, string>
        {
            ["GT_HOVER_CHECK"] = "1", ["GT_AGENT_SIZE"] = size.ToString("0.000", CultureInfo.InvariantCulture),
            ["GT_HOVER"] = hover.ToString("0.000", CultureInfo.InvariantCulture), ["GT_HOVER_BIAS"] = bias.ToString("0.0000", CultureInfo.InvariantCulture),
        };
        if (parts is { Length: > 0 }) env["GT_HC_PARTS"] = string.Join(",", parts);
        return env;
    }
    internal static double? HoverCheckGap(string stdout)
    {
        var m = System.Text.RegularExpressions.Regex.Match(stdout ?? "", @"hover check gap ([+-]?\d+\.\d+) m");
        return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }
    // pure (tested): what the close-up is framed on, for the log: the lowest body contact of the posture
    internal static string HoverContactLabel(string kind) => kind switch { "stand" => "feet (soles)", "sit" => "hips and any thighs / heels in contact", "seat" => "hips against the seat", _ => "lowest contact" };
    // pure (tested): on furniture (sitting on an object) the contact close-up frames her HIPS against the SEAT, never her feet over the terrain, and the render uses the
    // sim's own seated placement (no hover model: hover is not the fix on furniture, the furniture's adjust is). Env for look.py / render_mesh.py
    // pure (tested): what tells the renderer which close-up to draw: the label for the log and the posture kind (stand | sit = AO ground sit | seat | other).
    // 2026-10-26: a ground sit is framed on the hips-to-floor contact around her pelvis (render_mesh ground_sit_contact), not on the lowest 10 cm of her whole body
    internal static Dictionary<string, string> ContactEnv(string kind) => new() { ["GT_HC_POSTURE"] = HoverContactLabel(kind), ["GT_HC_KIND"] = kind ?? "" };
    internal static Dictionary<string, string> SeatLookEnv() => new(ContactEnv("seat")) { ["GT_SEATED"] = "1", ["GT_SEAT_CHECK"] = "1" };
    // 2026-10-26 live test on a slatted rocking chair: the render said -0.038 m and David's eye said "looks good": her butt sinks a few cm into slats and cushions in every
    // sit pose, so down to -0.06 m is ON the seat; floating more than 2 cm is not. The gap itself is the render's measurement (about +-2 cm: slat tops are uneven), David's eye decides
    internal const double SeatSinkTol = 0.06, SeatFloatTol = 0.02;
    internal static double? SeatContactGap(string stdout)
    {
        var m = System.Text.RegularExpressions.Regex.Match(stdout ?? "", @"seat contact gap ([+-]?\d+\.\d+) m");
        return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }
    // pure (tested): the one-line verdict of a seated check; it never changes the hover
    internal static string SeatCheckVerdict(double? gap) => gap is not double g ? "seat surface not found in the render: framed the hips only, gap unknown"
        : g >= -SeatSinkTol && g <= SeatFloatTol ? $"hips on the seat (gap {g:+0.000;-0.000} m; a few cm of sink into slats or cushions is normal, down to {-SeatSinkTol:0.00} m)"
        : g > 0 ? $"hips {g * 100:0.#} cm ABOVE the seat (gap {g:+0.000} m): lower her with the furniture's own adjust (AVsitter Z-, then SAVE)"
        : $"hips {-g * 100:0.#} cm sunk INTO the seat (gap {g:+0.000;-0.000} m): lift her with the furniture's own adjust (AVsitter Z+, then SAVE)";
    // ---- the render must show the pose the client believes (2026-10-26 13:20 live test: the client said `sit VAMOSITG12`, the export held a STAND, the check posted -0.229) ----
    internal const double HoverFixMax = 0.12, HoverConfirmTol = 0.01, SitPelvisMaxFrac = 0.45, StandPelvisMinFrac = 0.55;
    // pure (tested): the animation ids of the export's me.anims ("id@seconds,id@seconds")
    internal static List<UUID> ExportAnimIds(string anims) => (anims ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(a => UUID.TryParse(a.Split('@')[0], out var u) ? u : UUID.Zero).Where(u => u != UUID.Zero).ToList();
    // pure (tested): null when the export / render shows the posture the check is about, else why not. kind = sit (AO ground sit) | stand. The export must hold (sit) or lack (stand) an AO sit
    // animation, and the posed pelvis (mesh.json, metres above her feet level) must be low (sit: <= 0.45 x her height) or high (stand: >= 0.55 x her height). Unknown pelvis: only the animations count
    internal static string PoseRenderMismatch(string kind, double size, double? pelvisZ, IEnumerable<UUID> exportAnims, IEnumerable<UUID> sitIds)
    {
        bool sitInExport = Program.AoSitPlaying(exportAnims, false, sitIds);
        if (kind == "sit")
        {
            if (!sitInExport) return "the export holds no AO sit animation (the sit has not started playing, or it is not the one I think)";
            if (pelvisZ is double pz && size > 0 && pz > SitPelvisMaxFrac * size) return $"the render shows her pelvis {pz:0.00} m high: standing, not sitting";
        }
        else if (kind == "stand")
        {
            if (sitInExport) return "the export holds an AO sit animation but I think she stands";
            if (pelvisZ is double pz && size > 0 && pz < StandPelvisMinFrac * size) return $"the render shows her pelvis only {pz:0.00} m high: sitting, not standing";
        }
        return null;
    }
    // pure (tested): the pelvis height of the export's mesh.json (metres), null when unreadable
    internal static double? MeshPelvisZ(string meshJson)
    {
        try { var p = JsonDocument.Parse(meshJson).RootElement.GetProperty("pelvis"); return p.ValueKind == JsonValueKind.Array && p.GetArrayLength() == 3 ? p[2].GetDouble() : null; } catch { return null; }
    }
    // pure (tested): a hover fix this large (metres) needs a second, consistent render before it is posted
    internal static bool HoverFixNeedsConfirm(double gap) => Math.Abs(gap) > HoverFixMax;
    internal static bool HoverFixConfirmed(double gap1, double gap2) => Math.Abs(gap1 - gap2) <= HoverConfirmTol;
    // pure (tested): renders one hover check costs. Each HoverCheckRender call is ONE blender run of ONE image (the contact close-up, which REPLACES the wide
    // side view: look_scope.hovercheck_view). 1 when the contact is confirmed; 2 when a fix was applied (the re-render at the hover really in force is the verification)
    internal static int HoverCheckRenderCount(bool fixApplied, bool confirmRender = false) => (fixApplied ? 2 : 1) + (confirmRender ? 1 : 0);
    // ---- 2026-10-27 09:12 / 09:15 (David): she stood / sat at 217.8,74.5 INSIDE the Mirage bed's footprint (the bed's collision hull held her ~0.6 m above the floor);
    // the check saw gaps of +0.712 / +0.479 ("implausible") and did nothing. An implausible gap with her XY inside (or within HoverFurnitureNearM of) a known footprint
    // now means "get off the furniture": stand up if ground sitting, step to the nearest clear floor spot, re-run the check there. The hover is NEVER changed from an implausible render.
    internal enum HoverGapVerdict { Plausible, MoveClear, Unresolved }
    internal const float HoverFurnitureNearM = 0.5f;
    internal const double HoverClearAgainSec = 180;
    // pure (tested): what an implausible render gap means. movedRecently = she already stepped clear for this reason a moment ago (never loop)
    internal static HoverGapVerdict HoverGapDecision(double gap, Vector3 me, IReadOnlyList<FurnitureBox> boxes, bool movedRecently) =>
        PoseGapSane(gap) ? HoverGapVerdict.Plausible
        : !movedRecently && Program.InFurnitureMargin(me, boxes, HoverFurnitureNearM) != null ? HoverGapVerdict.MoveClear : HoverGapVerdict.Unresolved;
    // pure (tested): the clear floor spot to step to: the footprint helpers of the outfit-change flow (ClearStepPlan: inside a footprint, the seat's own stand_spot else a
    // computed spot, never across other furniture), else, within 0.5 m of one, the login walk's LoginJoinGoal ring (outside every footprint + body margin, straight line crosses no furniture)
    internal static (Vector3? to, string why) HoverClearTarget(Vector3 me, IReadOnlyList<FurnitureBox> boxes, IEnumerable<HomeSeatInfo> seats, Func<float, float, bool> blocked)
    {
        var plan = Program.ClearStepPlan(me, Program.NearestHomeSeat(me, seats), boxes, blocked);
        if (plan.Needed) return (plan.To, plan.Why);
        var g = Program.LoginJoinGoal(me, me, true, UUID.Zero, seats, boxes, blocked);
        return g.Kind == "spot" ? (g.To, "within 0.5 m of furniture; " + g.Why) : (null, g.Why);
    }
    DateTime hoverClearAt = DateTime.MinValue;
    // runs with poseGate held. Returns the retry reason when she moved (the request loop re-checks), null when unresolved (logged, hover unchanged)
    async Task<string> HoverGapOnFurniture(string kind, string key, double gap, string img)
    {
        var boxes = h.InPeronaut ? h.HomeFootprints() : new List<FurnitureBox>(); var me = h.Client.Self.SimPosition;
        bool recent = (DateTime.Now - hoverClearAt).TotalSeconds < HoverClearAgainSec;
        var v = HoverGapDecision(gap, me, boxes, recent);
        string Unres(string why) { var m = $"{key}: render gap {gap:+0.000;-0.000} is implausible (the calibration may be off for this pose, see PoseBiasFromPhoto): left UNRESOLVED, hover unchanged: {why} ({img})"; h.Log("posehover", m); hoverCheckResult = m; return null; }
        if (v == HoverGapVerdict.Plausible) return null;
        if (v == HoverGapVerdict.Unresolved) return Unres(recent ? "I already stepped to clear floor a moment ago and the gap is still implausible" : h.InPeronaut ? $"not on or within {HoverFurnitureNearM:0.0} m of a known furniture footprint (at {Program.P3(me)})" : "not in the mapped home region");
        var g = h.NavGridHere();
        Func<float, float, bool> blocked = g == null ? null : (x, y) => { if (!g.Contains(x, y)) return false; var (i, j) = g.IJ(x, y); return g.At(i, j) != 0; };
        var (to, why) = HoverClearTarget(me, boxes, h.LoadHomeSeats(), blocked);
        if (to is not Vector3 dest) return Unres($"at {Program.P3(me)} on / next to furniture but no clear floor spot found ({why})");
        h.Log("posehover", $"{key}: render gap {gap:+0.000;-0.000} is implausible and I am at {Program.P3(me)} on / next to '{Program.InFurnitureMargin(me, boxes, HoverFurnitureNearM)?.Name}': stepping to clear floor {Program.P3(dest)} ({why}) and re-checking there; hover unchanged");
        if (kind == "sit")
        {
            await h.IdleExplicitStand("hover clear floor");
            h.SuppressAutoSit(Program.IdleStandCooldownSec);   // she stays standing: no automatic re-sit
            if (h.AoSitReallyOn()) return Unres("could not stand up from the ground sit to step off the furniture");
        }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await h.StandOutLock.WaitAsync(cts.Token);
        try { await h.StandOutMoveTo("hover clear floor", h.Client.Self.SimPosition, dest, false, Program.InFurniture(me, boxes), cts.Token); }
        catch (OperationCanceledException) { h.Log("posehover", "hover clear floor: walk timed out"); }
        finally { h.StandOutLock.Release(); }
        hoverClearAt = DateTime.Now;
        h.Log("posehover", $"{key}: now at {Program.P3(h.Client.Self.SimPosition)} after stepping off the furniture; re-checking");
        return "stepped to clear floor after an implausible gap: re-checking";
    }

    // ONE render, ONE image. kind (stand|sit) only labels what the close-up frames
    async Task<(double? gap, string img)> HoverCheckRender(string exportDir, double size, double hover, double bias, string kind = null, bool seat = false, string[] parts = null)
    {
        var psi = new ProcessStartInfo(h.LookPython) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(h.LookPy); psi.ArgumentList.Add(exportDir); psi.ArgumentList.Add("hovercheck");
        foreach (var kv in seat ? SeatLookEnv() : HoverCheckEnv(size, hover, bias, parts)) psi.Environment[kv.Key] = kv.Value;   // seat: her real seated placement, no hover model
        foreach (var kv in ContactEnv(seat ? "seat" : kind)) psi.Environment[kv.Key] = kv.Value;
        using var pr = Process.Start(psi)!; var o = pr.StandardOutput.ReadToEndAsync(); var e = pr.StandardError.ReadToEndAsync();
        if (!await Task.Run(() => pr.WaitForExit(180000))) { try { pr.Kill(); } catch { } return (null, "timed out"); }
        var text = await o; await e;
        hoverLastPartGaps = seat ? new() : HoverCheckPartGaps(text);   // read by the check that made this render (under poseGate)
        return (seat ? SeatContactGap(text) : HoverCheckGap(text), text.Split('\n').FirstOrDefault(l => l.EndsWith(".jpg"))?.Trim() ?? "no image");
    }
    // pure (tested): is the form (android/human) known for certain? 2026-10-23 15:19: right after login the worn prims had no
    // names yet, AndroidWorn() read false and an android stand was saved as human|stand (and seeded ref|human|stand).
    // Certain = the outfit name says android, or every worn attachment's name has arrived (then AndroidWorn is real).
    internal static bool FormCertain(bool outfitSaysAndroid, int wornCount, int wornNamed) => outfitSaysAndroid || (wornCount > 0 && wornNamed == wornCount);
    internal bool FormCertainNow()
    {
        if (Program.IsAndroidOutfit(h.CurrentOutfit)) return true;
        try { var w = h.WornPrims().Where(p => p.ParentID == h.Client.Self.LocalID).ToList(); return FormCertain(false, w.Count, w.Count(p => !string.IsNullOrEmpty(p.Properties?.Name))); } catch { return false; }
    }
    readonly SemaphoreSlim poseGate = new(1, 1);

    // ---- 2026-10-28: the render's pelvis height is evidence of sit vs stand (PoseEvidence.cs). Every scene export a check makes records it; `idle_pose verify` / status make one on purpose ----
    internal sealed record RenderPose(DateTime At, double? PelvisZ, double Size);
    volatile RenderPose lastRender;
    internal DateTime PostureChangedAt => hoverPostureAt;
    internal void NoteRender(double? pelvisZ, double size) => lastRender = new RenderPose(DateTime.Now, pelvisZ, size);
    // the fresh, decisive render call (sitting / standing) with a one-line description, else (null, why not)
    internal (PoseCall? call, string detail) RenderEvidenceNow()
    {
        var r = lastRender;
        if (r == null) return (null, "no render yet");
        if (!PoseEvidence.RenderFresh(r.At, DateTime.Now, hoverPostureAt)) return (null, $"last render {(DateTime.Now - r.At).TotalSeconds:F0} s ago is stale");
        var c = PoseEvidence.FromRender(r.PelvisZ, r.Size);
        return (c, r.PelvisZ is double pz ? $"render: pelvis {pz:0.00} m of {r.Size:0.00} m, {(DateTime.Now - r.At).TotalSeconds:F0} s ago" : "render: pelvis unreadable");
    }
    // one scene export (the mesher; no Blender run) just to read the posed pelvis; skipped when a hover check holds the gate (its export records the pelvis too)
    internal async Task<string> PoseProbe()
    {
        if (!h.LoggedIn) return "not logged in";
        if (!await poseGate.WaitAsync(0)) return "a hover check is running (it records the pelvis too)";
        try
        {
            var ex = await h.SceneExport(new[] { "export", "4", "noenv" });
            var scene = ex.Split('\n').Last();
            if (!scene.EndsWith("scene.json")) { h.Log("posehover", "pose probe: export failed: " + ex); return "export failed"; }
            double size = h.SelfAv()?.Scale.Z ?? 0; double? pz = null;
            try { pz = MeshPelvisZ(File.ReadAllText(Path.Combine(Path.GetDirectoryName(scene)!, "mesh.json"))); } catch { }
            NoteRender(pz, size);
            return $"pelvis {(pz is double v ? v.ToString("0.00") : "?")} m";
        }
        catch (Exception ex) { return "probe error: " + ex.GetBaseException().Message; }
        finally { poseGate.Release(); }
    }
    // pure (tested): the loud line when a hover check gives up; a pose that is not rendered as expected must never end as a silent stop
    internal static string HoverGiveUpText(string why, int attempts, string lastRetry) =>
        lastRetry != null && lastRetry.StartsWith("pose not rendered", StringComparison.Ordinal)
            ? $"HOVER CHECK GAVE UP, hover NOT verified: {why}: after {attempts} tries the render does not show the pose I think she is in ({lastRetry}); nothing was changed; look at her and say what you see"
            : $"{why}: gave up after {attempts} tries ({lastRetry})";
    internal volatile string HoverGaveUp = null; internal DateTime HoverGaveUpAt = DateTime.MinValue;

    // pure (tested): only the renderer's calibration is read from pose-hover.json; a saved hover height under any other key
    // ("android|VAMOSITG24", "android|stand", ...) is ignored, never applied
    internal static Dictionary<string, double> PoseBiasNums(IReadOnlyDictionary<string, double> all)
        => (all ?? new Dictionary<string, double>()).Where(kv => kv.Key.StartsWith("bias|", StringComparison.OrdinalIgnoreCase)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, double> PoseNums()
    {
        try { return PoseBiasNums(JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(PoseHoverFile))); }
        catch { return new(); }
    }
    string PoseHoverFile => Program.Env("GT_POSE_HOVER", Path.Combine(Path.GetDirectoryName(h.LogPath) ?? ".", "pose-hover.json"));
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
    internal static (bool go, string why) HoverCheckReady(string nowKind, float speed, bool leashed, bool following, bool poseBusy, double settledSec, double sinceLastRunSec, bool manual, bool poseChanged = false)
    {
        if (nowKind == "seat") return (false, "skip: sitting on an object (the normal hover is restored there)");
        if (nowKind == "unknown") return (false, "unclassified AO animation playing (may be a sit): not a stand, no check");
        if (leashed) return (false, "leashed");
        if (following || speed >= 0.1f) return (false, "moving");
        if (poseBusy) return (false, "a pose is being entered or left");
        if (settledSec < HoverCheckSettleSec) return (false, "posture changed a moment ago");
        if (!manual && !poseChanged && sinceLastRunSec < HoverCheckCooldownSec) return (false, "another check ran a moment ago");   // an automatic pose change is always remeasured, never held back by the cooldown
        return (true, null);
    }
    long hoverReqSeq; string hoverPosture; DateTime hoverPostureAt = DateTime.MinValue, hoverCheckLastRun = DateTime.MinValue;
    // from every own-animation packet and the idle tick: note the posture; a sit or stand after another posture requests ONE check
    internal void HoverPostureNote(IEnumerable<UUID> playing, bool seatedOnObject)
    {
        playing = playing.ToList();
        var now = HoverPostureKind(h.AoForceSitOn(playing, seatedOnObject), seatedOnObject, !seatedOnObject && h.AoUnclassifiedNow(playing).Count > 0);
        var prev = Interlocked.Exchange(ref hoverPosture, now);
        if (prev == now) return;
        hoverPostureAt = DateTime.Now;
        if (HoverCheckWantedOnPosture(prev, now)) _ = HoverCheckRequest(now, 4000);
    }
    // schedule ONE check (why: sit, stand, region or manual). A newer request replaces an older pending one; blocked states retry a few times, then give up.
    // returns the task that ends with the check's one-line result (what a manual `hover check` waits for and returns)
    internal volatile string hoverCheckResult = "";
    // when a check last FINISHED (measured and fixed / confirmed, or ended with its own final "nothing to do" result); the login walk waits for one after login (LoginJoin.cs)
    internal DateTime hoverCheckDoneAt = DateTime.MinValue;
    internal Task<string> HoverCheckRequest(string why, int delayMs = 4000)
    {
        if (Program.Env("GT_POSE_HOVER_CHECK", "on") == "off") return Task.FromResult("hover check is off (GT_POSE_HOVER_CHECK=off)");
        long id = Interlocked.Increment(ref hoverReqSeq);
        h.Log("posehover", $"visual hover check requested ({why}), in {delayMs / 1000.0:0.#} s");
        return Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs);
                if (why == "manual" && h.Client.Self.SittingOn != 0) return await SeatContactCheck();   // on furniture: report the hips-to-seat gap, leave the hover alone
                for (int attempt = 1; ; attempt++)
                {
                    if (Interlocked.Read(ref hoverReqSeq) != id) return "replaced by a newer hover check request";   // a newer request replaced this one
                    if (!h.LoggedIn) return "not logged in";
                    var (kind, pose) = PoseNow(); var sitting = h.Client.Self.SittingOn != 0;
                    var (go, block) = HoverCheckReady(sitting ? "seat" : kind, h.Client.Self.Velocity.Length(), h.IdleLeashedNow(), h.Following, h.IdleBusy,
                                                      (DateTime.Now - hoverPostureAt).TotalSeconds, (DateTime.Now - hoverCheckLastRun).TotalSeconds, why == "manual", PoseChangedSinceCheck(hoverLastCheckedPose, pose));
                    string retry = block;
                    if (go)
                    {
                        hoverCheckLastRun = DateTime.Now;
                        retry = await PoseHoverCheck(kind, pose, why);
                        if (retry == null) { hoverCheckDoneAt = DateTime.Now; HoverGaveUp = null; return hoverCheckResult; }
                    }
                    else if (block.StartsWith("skip:")) { h.Log("posehover", $"{why}: {block}"); return $"{why}: {block}"; }
                    if (attempt >= HoverCheckMaxTries)
                    {
                        var gu = HoverGiveUpText(why, attempt, retry); h.Log("posehover", gu);
                        if (retry != null && retry.StartsWith("pose not rendered", StringComparison.Ordinal)) { hoverCheckResult = gu; HoverGaveUp = gu; HoverGaveUpAt = DateTime.Now; }
                        return gu;
                    }
                    h.Log("posehover", $"{why}: waiting ({retry}); try {attempt + 1}/{HoverCheckMaxTries} in {HoverCheckRetryMs / 1000} s");
                    await Task.Delay(HoverCheckRetryMs);
                }
            }
            catch (Exception ex) { h.Log("posehover", $"{why}: {ex.GetBaseException().Message}"); return $"{why}: error {ex.GetBaseException().Message}"; }
        });
    }
    // is she still in that pose? (a check must never act on a pose she has left). The AO's anim_list decides sit vs stand, never the sim.
    bool PoseStill(string kind, string pose) => h.Client.Self.SittingOn == 0 && (kind == "sit"
        ? h.AoSitReallyOn() && (pose == "sit" || string.Equals(h.AoSitCurrentName, pose, StringComparison.OrdinalIgnoreCase))
        : kind == "stand" && h.IdlePosed == null && h.Client.Self.Velocity.Length() < 0.1f && !h.LeashActive && !h.AoForceSitOn(h.SelfAnims(), false) && h.AoUnclassifiedNow().Count == 0);
    // the pose she is really in (the AO decides sit vs stand, not the sim): (kind, pose name)
    internal (string kind, string pose) PoseNow() => h.AoSitReallyOn() ? ("sit", h.AoSitCurrentName ?? "sit") : h.AoUnclassifiedNow().Count > 0 ? ("unknown", "unknown") : ("stand", "stand");

    // seated on an object (2026-10-26, David: "when you're sitting, you should be checking your hips, not your feet"): ONE render of the hips against the seat;
    // reports the gap and the close-up path; changes nothing (the AO, the hover and the furniture stay as they are)
    async Task<string> SeatContactCheck()
    {
        if (!h.LoggedIn || h.Client.Self.SittingOn == 0) return "seat check: not sitting on an object";
        if (!await poseGate.WaitAsync(0)) return "seat check: a check is already running";
        try
        {
            var ex = await h.SceneExport(new[] { "export", "4", "noenv" });
            var scene = ex.Split('\n').Last();
            if (!scene.EndsWith("scene.json")) { h.Log("posehover", $"seat check: export failed: {ex}"); return "seat check: export failed"; }
            if (h.Client.Self.SittingOn == 0) return "seat check: she is no longer sitting on an object";
            var (gap, img) = await HoverCheckRender(Path.GetDirectoryName(scene)!, 0, 0, 0, "seat", seat: true);
            var r = $"seat: {SeatCheckVerdict(gap)}; hover left alone; renders 1; close-up of {HoverContactLabel("seat")}: {img}";
            h.Log("posehover", r); hoverCheckResult = r; return r;
        }
        finally { poseGate.Release(); }
    }

    // the visual check: null = finished (checked, or nothing to do); a string = not now, retry (the reason)
    async Task<string> PoseHoverCheck(string kind, string pose, string requestWhy = null)
    {
        if (!h.LoggedIn || !PoseStill(kind, pose)) return "not in that pose";
        if (!FormCertainNow()) return "form not known yet (attachments still loading)";
        bool android = h.AndroidFormNow(); var key = PoseHoverKey(android, pose);
        var nums = PoseNums();
        double pin = h.PinnedHover();
        if (!await poseGate.WaitAsync(0)) return "a check is already running";
        try
        {
            var sw = Stopwatch.StartNew();
            var ex = await h.SceneExport(new[] { "export", "4", "noenv" });
            var scene = ex.Split('\n').Last();
            if (!scene.EndsWith("scene.json")) { h.Log("posehover", $"{key}: export failed: {ex}"); return null; }
            var dir = Path.GetDirectoryName(scene)!;
            // 2026-10-23 17:15 (David "Fix it"): the check is the hover-check RENDER: look.py hovercheck = her real placement
            // (agent z - size/2 + hover - posture bias, never snapped to the floor) over the scene's own floor, seen level from
            // her side at the contact; it prints the gap of her lowest vertex above that floor. Nudge by the gap, then render
            // again to confirm. Private: the images are never sent to David. The result is applied now and NOT remembered.
            if (!PoseStill(kind, pose)) { h.Log("posehover", $"{key}: pose changed while exporting; skipped"); return null; }
            // 2026-10-26: the hover IN FORCE (AgentPreferences, what David's viewer applies), read fresh. Our own appearance value stayed 0 after a
            // POST, so every re-render drew her at 0 and "contact confirmed" was arithmetic (David: sunk below the ground at the "confirmed" -0.186)
            var (effHov, effSrc) = await h.EffectiveHoverFresh();
            double hov = effHov ?? pin, size = h.SelfAv()?.Scale.Z ?? 0;
            bool calibrated = BiasCalibrated(nums, android, kind, pose);
            if (size < 1) { h.Log("posehover", $"{key}: agent size unknown; skipped"); return null; }
            double bias = PoseBiasFor(nums, android, kind, pose);
            var parts = SupportParts(kind, pose);   // which body parts support her in this pose (data: names only, never numbers); null = unlisted: the whole-body lowest gap
            var (g1, img1) = await HoverCheckRender(dir, size, hov, bias, kind, parts: parts);   // render 1: the contact close-up (replaces the wide side view)
            string finalImg = img1;
            bool nearFurn = h.InPeronaut && Program.InFurnitureMargin(h.Client.Self.SimPosition, h.HomeFootprints(), HoverFurnitureNearM) != null;
            if (g1 is not double gap || !PoseGapSane(gap, kind, nearFurn))
            {
                if (g1 is double bg) return await HoverGapOnFurniture(kind, key, bg, img1);   // moved off the furniture (retry reason) or left unresolved (logged, null = finished)
                var bad = $"{key}: render gap {g1?.ToString("+0.000;-0.000") ?? "none"} is implausible: the calibration is off for this pose (needs a per-pose bias, see PoseBiasFromPhoto); nothing changed ({img1})"; h.Log("posehover", bad); hoverCheckResult = bad; return null;
            }
            // the guard: a fix is only ever computed from a render that shows the pose the client believes (never post on a standing render for a "sit")
            {
                double? pz = null; try { pz = MeshPelvisZ(File.ReadAllText(Path.Combine(dir, "mesh.json"))); } catch { }
                List<UUID> exAnims = new(); try { using var sj = JsonDocument.Parse(File.ReadAllText(scene)); if (sj.RootElement.GetProperty("me").TryGetProperty("anims", out var an)) exAnims = ExportAnimIds(an.GetString()); } catch { }
                NoteRender(pz, size);   // the render's pelvis is evidence of sit vs stand for the status too (PoseEvidence.cs)
                var why = PoseRenderMismatch(kind, size, pz, exAnims, h.AoSitIdsNow());
                if (why != null)
                {
                    var msg = $"{key}: not verified: pose not rendered ({why}); render gap {gap:+0.000;-0.000} ignored, nothing changed, wait and retry ({img1})";
                    h.Log("posehover", msg); hoverCheckResult = msg;
                    // 2026-10-27 09:34: the AO came back from a relog holding its force sit while the client believed she stood: the check retried against a sitting render for
                    // minutes. Press the AO force sit ONCE (never repeatedly), wait, and let the request loop re-run the check; after that one press: log and give up.
                    if (Program.StuckSitReleaseOk(kind, why, requestWhy, (DateTime.Now - h.StuckSitLoginAt).TotalSeconds, h.StuckSitPresses))
                    { try { await h.StuckSitRelease(why); } catch (Exception rex) { h.Log("idlepose", "stuck AO sit release error " + rex.GetBaseException().Message); } }
                    else if (kind == "stand" && why.Contains("sitting, not standing") && h.StuckSitPresses > 0) h.Log("posehover", Program.StuckSitReleaseText(h.StuckSitPresses, true));
                    return "pose not rendered: " + why;
                }
            }
            string how; bool fixedHover = false, confirmRender = false;
            // the gap that drives the fix: the lowest SUPPORTING part's gap to the floor drawn in this render (hips, hands, chest, thighs ... as the pose lists them), else the whole-body lowest gap
            var partGaps1 = hoverLastPartGaps; double? sg1 = parts == null ? null : SupportGap(parts, partGaps1);
            double basis = sg1 ?? gap, aim = sg1 != null ? SupportAim(parts) : 0.0, eg = basis - aim;
            string liftTxt = parts == null ? "" : sg1 == null ? $"; supporting parts {string.Join("+", parts)} not seen in the render: whole-body lowest gap used"
                : $"; supporting parts {SupportGapsText(parts, partGaps1)}: hover aimed so the lowest is {aim:0.000} m above the floor{(aim > 0 ? " (hands never below it)" : "")}; no per-pose hover value, measured on this floor";
            if (PoseChangedSinceCheck(hoverLastCheckedPose, pose)) { liftTxt += $"; pose changed from {hoverLastCheckedPose}: the hover in force ({hov:0.000}) was set for that pose, remeasured here"; h.Log("posehover", $"{key}: pose changed since the last check ({hoverLastCheckedPose} -> {pose}): hover in force {hov:0.000} is not trusted, remeasuring on this pose"); }
            hoverLastCheckedPose = pose;
            string calTxt = calibrated ? "bias calibrated against David's viewer" : "DEFAULT bias, not yet calibrated against David's viewer (hover calibrate)";
            if (SupportConfirmed(eg, aim)) how = $"render confirms contact at {hov:0.000} ({effSrc}; gap {basis:+0.000}; {calTxt}{liftTxt}); hover left alone";
            else
            {
                var t = PoseHoverFromGap(hov, eg);
                if (HoverFixNeedsConfirm(eg))   // a large fix (> 0.12 m): only after a second render at the same hover agrees
                {
                    var (gc, imgc) = await HoverCheckRender(dir, size, hov, bias, kind, parts: parts); confirmRender = true;
                    double? gcBasis = gc is double gcr ? (parts == null ? gcr : SupportGap(parts, hoverLastPartGaps) ?? gcr) : null;
                    if (gcBasis is not double gc2 || !HoverFixConfirmed(basis, gc2))
                    {
                        var msg = $"{key}: not verified: the render gap {gap:+0.000;-0.000} (a fix of more than {HoverFixMax:0.00} m) was not confirmed by a second render ({(gc is double gq ? gq.ToString("+0.000;-0.000") : "none")}); nothing changed ({img1}, {imgc})";
                        h.Log("posehover", msg); hoverCheckResult = msg; return "large fix not confirmed by a second render";
                    }
                }
                if (!PoseStill(kind, pose)) { h.Log("posehover", $"{key}: pose changed; skipped"); return null; }
                var (ok, info) = await h.PostHover(t, $"visual hover fix: {key} (applied now, not saved)");
                // verify: the cap must now hold t (fresh GET), and the re-render is drawn at THAT value, not at the value we meant to set
                var (eff2, src2) = await h.EffectiveHoverFresh();
                bool applied = ok && eff2 is double e2 && Math.Abs(e2 - t) <= 0.002;
                var (g2, img2) = await HoverCheckRender(dir, size, eff2 ?? t, bias, kind, parts: parts);   // render 2 (only after a fix): the verification at the hover really in force
                fixedHover = true; finalImg = img2;
                double? basis2 = g2 is double g2r ? (parts == null || sg1 == null ? g2r : SupportGap(parts, hoverLastPartGaps) ?? g2r) : null;
                bool good = applied && basis2 is double gg && SupportConfirmed(gg - aim, aim);
                string verdict = good ? (calibrated ? "contact confirmed" : "contact in the render; NOT verified against David's viewer (default bias)") : !applied ? $"NOT verified: the hover in force is {(eff2 is double q ? q.ToString("0.000") : "unknown")} ({src2}), not {t:0.000}" : "NOT confirmed";
                how = $"render gap {basis:+0.000} at {hov:0.000} -> {t:0.000}, re-render at {(eff2 ?? t):0.000} ({src2}) gap {(basis2 is double x ? x.ToString("+0.000;-0.000") : "?")} ({verdict}; {calTxt}{liftTxt}): {info}; {img2}";
            }
            int renders = HoverCheckRenderCount(fixedHover, confirmRender);
            h.Log("posehover", $"{key}: {how} in {sw.Elapsed.TotalSeconds:F0} s ({img1}); renders {renders}; close-up of {HoverContactLabel(kind)}: {finalImg}");
            hoverCheckResult = $"{key}: {how}; renders {renders}; close-up of {HoverContactLabel(kind)}: {finalImg}" + (fixedHover ? $" (before the fix: {img1})" : "");
            return null;
        }
        finally { poseGate.Release(); }
    }
}
