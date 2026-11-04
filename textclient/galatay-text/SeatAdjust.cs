// SeatAdjust.cs (2026-11-02, David: "you should never use hardcoded adjustments, but I do want you to click the save button when you adjust your pose.
// Hopefully, everything will eventually get saved with a good adjustment that is based purely on your visual renders.")
// After a furniture pose settles (wander sit, manual pose pick, mid-sit change: SeatPose.cs "chose"), ONE seat render (look.py hovercheck, seated
// placement) reads what bears her weight (render_mesh.py classify_support: the pose kind - sitting, kneeling, standing, lying, hand-supported - comes from
// which BODY parts rest lowest on the surface under them, never from the pose name) and the mean gap of those parts against that surface.
// Outside the acceptance band she is moved with the furniture's OWN adjust (AVsitter 2: [ADJUST] > [POSE] > Z+/Z- in 0.25/0.05/0.01 m steps), by exactly
// the measured gap (rounded to AVsitter's 1 cm step), re-rendered, and presses [SAVE] for it (AVsitter: a personal per-avatar, per-pose offset; any
// sitter may save it, it is not the owner's notecard). 2026-11-04 David: every nudge that was applied is saved, because she always re-checks her
// seat; the re-render's verdict is logged, not used to leave a nudge unsaved. A nudge the re-render shows to be WORSE is still undone, and nothing
// is saved when the run lost the furniture (stood up, menu lost). Hover is never touched. Only on Peronaut furniture or her own. nPose / other: log and skip.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
namespace GalatayText;

internal static partial class SeatAdjust
{
    internal sealed record SupportReading(string Kind, string[] Bearing, double? Gap, Dictionary<string, double> Parts)
    {
        public string BaseKind => Kind?.Split('+')[0];
        public bool Conclusive => Kind != null && Gap.HasValue && Bearing.Length > 0;
    }
    static readonly string[] SoftParts = { "hips", "thighs", "chest" };
    // a hard contact (soles, knees, palms) does not sink into a cushion: the band is the render's own repeatability (Hover.HoverConfirmTol x 2)
    internal const double HardContactTol = 2 * Hover.HoverConfirmTol;
    internal const double ImproveMin = 0.005;   // half AVsitter's smallest step: less is not a measurable improvement
    internal const double Step = 0.01;          // AVsitter's smallest position step (m)
    internal const int MaxIterations = 3;

    // pure (tested): "GT: furniture support: sitting; bearing hips,thighs; gap +0.035 m; parts hips +0.030, thighs +0.040"
    internal static SupportReading Parse(string stdout)
    {
        var line = (stdout ?? "").Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("GT: furniture support:", StringComparison.Ordinal));
        if (line == null) return null;
        var parts = new Dictionary<string, double>();
        var pm = Regex.Match(line, @"parts (.+)$");
        if (pm.Success)
            foreach (Match m in Regex.Matches(pm.Groups[1].Value, @"([a-z]+) ([+-]\d+\.\d+)")) parts[m.Groups[1].Value] = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var k = Regex.Match(line, @"^GT: furniture support: ([a-z+\-]+); bearing ([a-z,]+); gap ([+-]?\d+\.\d+) m");
        if (!k.Success) return new SupportReading(null, Array.Empty<string>(), null, parts);
        return new SupportReading(k.Groups[1].Value, k.Groups[2].Value.Split(',', StringSplitOptions.RemoveEmptyEntries), double.Parse(k.Groups[3].Value, CultureInfo.InvariantCulture), parts);
    }
    // pure (tested): the acceptance band of a reading: soft bearing parts sink into slats / cushions (Hover.SeatSinkTol, Hover.SeatFloatTol); hard ones do not
    internal static (double lo, double hi) Band(SupportReading r) => r.Bearing.All(b => SoftParts.Contains(b)) ? (-Hover.SeatSinkTol, Hover.SeatFloatTol) : (-HardContactTol, HardContactTol);
    internal static bool InBand(SupportReading r) => r is { Conclusive: true } && r.Gap >= Band(r).lo && r.Gap <= Band(r).hi;
    // pure (tested): the Z move that puts the weight-bearing contact on its surface: minus the measured gap, in AVsitter 1 cm steps (0 = nothing to do)
    internal static double CorrectionZ(SupportReading r) => r is not { Conclusive: true } || InBand(r) ? 0 : Math.Round(-r.Gap!.Value / Step) * Step;

    internal enum Verdict { Save, Continue, Revert, Inconclusive }
    // pure (tested): after a nudge. Never Save on an inconclusive render, a changed pose kind, or a result no better than before.
    internal static Verdict Decide(SupportReading before, SupportReading after)
    {
        if (before is not { Conclusive: true } || after is not { Conclusive: true }) return Verdict.Inconclusive;
        if (before.BaseKind != after.BaseKind) return Verdict.Inconclusive;
        if (Math.Abs(after.Gap!.Value) > Math.Abs(before.Gap!.Value) - ImproveMin) return Verdict.Revert;
        return InBand(after) ? Verdict.Save : Verdict.Continue;
    }
    // pure (tested): which of the sampled seat readings is the one to act on (its index), and why when none is. The reading nearest the median of the
    // readings that agree within Hover.HoverSampleSpreadMax; the ones outside it are dropped as outliers (Hover.HoverSampleMedian's rule, the stand
    // check's). 2026-11-03 Lalou sofa: at ONE fixed position three readings ran +0.043 .. +0.019 m with nothing moving, and a single one drove the nudge.
    // Fewer than `need` readings, fewer than two that agree, or agreeing ones still spread more than that: -1 and nothing is pressed.
    internal static (int index, double? median, string why) SampleMedian(IReadOnlyList<SupportReading> samples, int need)
    {
        var l = samples ?? Array.Empty<SupportReading>();
        var withGap = l.Select((r, i) => (r, i)).Where(x => x.r is { Conclusive: true }).ToList();
        var v = withGap.OrderBy(x => x.r.Gap!.Value).ToList();
        if (v.Count < need) return (-1, null, $"{v.Count} of {need} conclusive readings");
        var (med, why) = Hover.HoverSampleMedian(v.Select(x => (double?)x.r.Gap!.Value).ToList(), need);
        if (med is not double m) return (-1, null, why);
        // the representative: the sample nearest the median; on a tie the freshest (its parts and its render's quality are the ones used), so the
        // reading acted on and the frame its gate judges always come from the same sample and the nudge is the median, never one reading
        var rep = v.OrderBy(x => Math.Abs(x.r.Gap!.Value - m)).ThenByDescending(x => x.i).First();
        return (rep.i, m, $"{why} (median {Fmt(m)})");
    }
    // pure (tested): should the sit watcher start a check? a new seat that no pose choice already covered (David 2026-11-02: every sit, no path skipped)
    internal static bool SitNeedsCheck(uint prevSeat, uint nowSeat, uint lastCheckedSeat) => nowSeat != 0 && nowSeat != prevSeat && nowSeat != lastCheckedSeat;
    // pure (tested): does a seated wander stay keep sitting? An adjustment in progress holds the sit (she must not stand up mid-adjust,
    // David 2026-11-04), so the stay no longer ends it; a pause still interrupts even while adjusting.
    internal static bool SitKeepsSitting(double elapsed, double stay, bool seated, bool paused, bool adjusting)
        => seated && !paused && (adjusting || elapsed < stay);
    // pure (tested): may the stay change her pose? The pose change waits for the adjustment to finish, then happens on a later tick.
    internal static bool SitChangesPose(double elapsed, double changeAt, bool changed, bool seated, bool paused, bool adjusting)
        => !changed && seated && !paused && !adjusting && elapsed >= changeAt;
    // pure (tested): the line surfaced when an adjustment stays on but could not be saved
    internal static string UnsavedLine(string seatName, string pose, double applied, string why) => $"UNSAVED '{seatName}' {pose}: kept Z {applied:+0.00;-0.00} m from the render but {why}: David should check it";

    // ---- AVsitter 2 dialogs ([AV]sitA.lsl: options_menu "Adjust:", adjust_pose_menu "Personal adjustment:") ----
    internal enum Menu { None, PoseMenu, Options, Personal, Generic, Entry }
    // ---- other systems (nPose and the like): found by button NAMES, never per-object offsets ----
    static readonly System.Text.RegularExpressions.Regex UpRx = new(@"^\W*(z\s*\+|up|raise|higher)\W*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    static readonly System.Text.RegularExpressions.Regex DownRx = new(@"^\W*(z\s*-|down|lower)\W*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    static readonly System.Text.RegularExpressions.Regex SaveRx = new(@"^\W*(save|remember|store|save\s*pos(ition)?|save\s*pose)\W*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    static readonly System.Text.RegularExpressions.Regex EntryRx = new(@"^\W*(adjust|adjustment|offset|offsets|position|adjust\s*pos(ition)?|tweak)\W*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    // pure (tested): the up / down / save buttons of a non-AVsitter adjust menu (null = absent)
    internal static (string up, string down, string save) GenericButtons(IReadOnlyList<string> labels)
    {
        var l = (labels ?? Array.Empty<string>()).Select(x => x.Trim()).ToList();
        return (l.FirstOrDefault(x => UpRx.IsMatch(x)), l.FirstOrDefault(x => DownRx.IsMatch(x)), l.FirstOrDefault(x => SaveRx.IsMatch(x)));
    }
    // pure (tested): the button that opens an adjust menu on a non-AVsitter menu (null = none)
    internal static string EntryButton(IReadOnlyList<string> labels) => (labels ?? Array.Empty<string>()).Select(x => x.Trim()).FirstOrDefault(x => EntryRx.IsMatch(x));
    // pure (tested): presses of a measured step (metres of gap change per press, from the renders) to cover dz; 0 when it would overshoot by more than half a step
    internal static int GenericPresses(double dz, double stepPerPress) => Math.Abs(stepPerPress) < 0.003 ? 0 : (int)Math.Round(dz / stepPerPress);
    static readonly string[] PersonalButtons = { "X+", "Y+", "Z+", "X-", "Y-", "Z-", "[SAVE]" };
    // pure (tested)
    internal static Menu Classify(string message, IReadOnlyList<string> labels)
    {
        var l = (labels ?? Array.Empty<string>()).Select(x => x.Trim()).ToList();
        if (PersonalButtons.All(b => l.Contains(b))) return Menu.Personal;
        if (l.Contains("[POSE]") && (message ?? "").Split('\n').Any(x => x.Trim().StartsWith("Adjust", StringComparison.OrdinalIgnoreCase))) return Menu.Options;   // AVsitter puts its "AVsitter™ 2.1" header line first
        if (l.Contains("[ADJUST]")) return Menu.PoseMenu;
        var g = GenericButtons(l); if (g.up != null && g.down != null) return Menu.Generic;
        if (EntryButton(l) != null) return Menu.Entry;
        return Menu.None;
    }
    // pure (tested): the personal menu's state: position (vs rotation) mode and the step in force (the step button names the current increment)
    internal static (bool position, double? step) PersonalState(IReadOnlyList<string> labels)
    {
        var l = labels.Select(x => x.Trim()).ToList();
        bool pos = l.Contains("Position") && !l.Contains("Rotation");
        double? st = l.Contains("0.05m") ? 0.05 : l.Contains("0.25m") ? 0.25 : l.Contains("0.01m") ? 0.01 : null;
        return (pos, st);
    }
    internal static readonly double[] StepCycle = { 0.05, 0.25, 0.01 };   // pressing the step button: 0.05 -> 0.25 -> 0.01 -> 0.05
    static string StepLabel(double s) => s switch { 0.05 => "0.05m", 0.25 => "0.25m", _ => "0.01m" };
    // pure (tested): the button presses that move her dz metres up (+) or down (-) from step `cur`: whole 0.25 / 0.05 / 0.01 steps, cycling the step button
    // in its own order. Returns the labels and the step in force afterwards.
    internal static (List<string> presses, double stepAfter) PlanZ(double dz, double cur)
    {
        var presses = new List<string>(); int units = (int)Math.Round(Math.Abs(dz) / Step);
        if (units == 0) return (presses, cur);
        var need = new Dictionary<double, int> { [0.25] = units / 25, [0.05] = units % 25 / 5, [0.01] = units % 5 };
        string dir = dz > 0 ? "Z+" : "Z-"; int ci = Array.IndexOf(StepCycle, cur); if (ci < 0) ci = 0; int c0 = ci;
        for (int k = 0; k < 3; k++)
        {
            var s = StepCycle[(c0 + k) % 3];
            if (need[s] == 0) continue;
            while (StepCycle[ci % 3] != s) { presses.Add(StepLabel(StepCycle[ci % 3])); ci++; }
            for (int i = 0; i < need[s]; i++) presses.Add(dir);
        }
        return (presses, StepCycle[ci % 3]);
    }
    internal static string Fmt(double? g) => g is double d ? d.ToString("+0.000;-0.000", CultureInfo.InvariantCulture) : "?";
    internal static string Describe(SupportReading r) => r is not { Conclusive: true } ? "inconclusive" : $"{r.Kind} on {string.Join("+", r.Bearing)} gap {Fmt(r.Gap)} m";
}
