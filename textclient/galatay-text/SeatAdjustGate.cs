// SeatAdjustGate.cs (2026-11-02 17:12 incident: 'Dutchie clawfoot bath tub adult' Solo* > shave. She floated high over the tub; the close-up was an
// overexposed shot of her thighs and torso with no tub and no floor in it, yet the reading "kneeling on shins+feet gap +0.077" was nudged Z -0.08 and SAVED.
// David: "never Save an adjustment unless confident it's correct", "we will need a way to detect bugged poses".)
//  1) Quality gate: a reading is only acted on when the render shows the surface under every bearing part (rays hit it, its z is inside the frame),
//     the seat top under her hips was found, and the image is not blown out. Else one wide retake (GT_SUPPORT_WIDE), else nothing is pressed.
//  2) Save: David 2026-11-04 ("just save your adjustments anyway ... because you will always be checking them") reversed the 11-02 rule for an
//     APPLIED nudge: whatever the re-render reads (a changed bearing pattern, a render not good enough to confirm it, better but out of band) it is
//     SAVED, and the old confidence verdict is only told in the log line. What is still undone: a nudge the re-render shows to be WORSE than before.
//  3) Bugged pose: evidence from OBJECT DATA (her body vs the seat linkset's box: floating high over it / buried under it), the render (a huge gap),
//     the anims (no seat pose anim playing). A bugged pose goes on the per-furniture denylist (_pose-rules.json "bugged_poses"), another pose on the
//     SAME furniture is picked; she only leaves when no solo pose is left. No per-object numbers anywhere: the thresholds are body-scale constants.
//  4) 2026-11-22 (fixes list, Nov 11 11:17 + Nov 20 incidents): a nudge is NOT saved when the re-render that should confirm it fails the gate (Nov 11:
//     Mirage 'texting' misread as kneeling +0.243, nudged -0.24 and SAVED on a 91 % dark re-render, which left her sunk in the bed). Such a nudge is
//     undone instead. A re-render that passes the gate still saves as David asked on 11-04, whatever it reads. And the render alone no longer
//     denylists a pose: a huge render gap was a false positive 3 times of 4 (the tub's water surface read as a surface over her, 'read book' and
//     'shave'; the Mirage bed's bad saved offset read as 'lying on chest -0.263'). Only object data (floating / buried) or no pose animation denylist;
//     a huge render gap is unmeasurable and nothing is pressed. In a water seat (tub, pool, hot tub) a sunk reading is unmeasurable too.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
namespace GalatayText;

internal static partial class SeatAdjust
{
    internal sealed record Quality(double? FrameLo, double? FrameHi, bool SeatFound, Dictionary<string, int> Hits, Dictionary<string, double> SurfaceZ, double? Overexposed, double? Dark);
    internal const int MinHits = 3;                 // render_mesh seat_top_hits: fewer rays than this on a surface = no surface
    internal const double MaxOverexposed = 0.30;    // blown-out share of the close-up (tub 17:12: 0.46; good seat renders today 0.09-0.10)
    internal const double MaxDark = 0.60;
    internal const double FrameSlack = 0.02;
    internal const double HugeGap = 0.25;           // more than this between her bearing parts and the surface: not a fit problem, a broken pose
    internal const double FloatOverTop = 0.30;      // her lowest point this far above the top of the furniture's box: floating
    internal const double BuriedUnder = 0.10;       // her highest point this far below the bottom of the box: buried

    // pure (tested): "GT: support quality: frame z a..b; seat found|not found; hits shins 12, feet 9; surface shins 20.150" + "GT: render quality: overexposed 0.46; dark 0.00; ..."
    internal static Quality ParseQuality(string text)
    {
        text ??= "";
        var q = Regex.Match(text, @"GT: support quality: frame z ([+-]?\d+\.\d+)\.\.([+-]?\d+\.\d+); seat (found|not found); hits ([^;]+); surface ([^\n]+)");
        var rq = Regex.Match(text, @"GT: render quality: overexposed (\d+\.\d+); dark (\d+\.\d+)");
        double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
        var hits = new Dictionary<string, int>(); var surf = new Dictionary<string, double>();
        if (q.Success)
        {
            foreach (Match m in Regex.Matches(q.Groups[4].Value, @"([a-z]+) (\d+)")) hits[m.Groups[1].Value] = int.Parse(m.Groups[2].Value);
            foreach (Match m in Regex.Matches(q.Groups[5].Value, @"([a-z]+) ([+-]?\d+\.\d+)")) surf[m.Groups[1].Value] = D(m.Groups[2].Value);
        }
        return new Quality(q.Success ? D(q.Groups[1].Value) : null, q.Success ? D(q.Groups[2].Value) : null, q.Success && q.Groups[3].Value == "found",
            hits, surf, rq.Success ? D(rq.Groups[1].Value) : null, rq.Success ? D(rq.Groups[2].Value) : null);
    }

    // pure (tested): a seat she sits IN water in (bath tub, hot tub, pool...): the linkset's water surface lies over her body, so the render reads
    // a sunk gap that is not one (Nov 20: Dutchie tub 'soak' -0.189 nudged +0.19 read worse; 'read book' -0.319 and 'shave' -0.489 denylisted)
    static readonly Regex WaterSeatName = new(@"\b(bath|bathtub|tub|hot ?tub|hottub|jacuzzi|pool|spa|onsen|pond)s?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    internal static bool IsWaterSeat(string seatName) => !string.IsNullOrWhiteSpace(seatName) && WaterSeatName.IsMatch(seatName);
    // pure (tested): may this reading be acted on? (null = yes, else why not). waterSeat: a sunk reading (below the contact band) is unmeasurable there.
    internal static string GateFail(SupportReading r, Quality q, bool waterSeat = false)
    {
        if (r is not { Conclusive: true }) return "no conclusive support reading";
        if (q == null || q.FrameLo == null) return "the render gave no quality evidence (frame / surface hits)";
        if (q.Overexposed is not double o) return "the image exposure could not be read";
        if (o > MaxOverexposed) return $"the close-up is overexposed ({o:P0} blown out)";
        if (q.Dark is double dk && dk > MaxDark) return $"the close-up is too dark ({dk:P0})";
        if (!q.SeatFound) return "no furniture surface under her hips in the render";
        foreach (var b in r.Bearing)
        {
            if (!q.Hits.TryGetValue(b, out var n) || n < MinHits) return $"no furniture surface under her {b} ({(q.Hits.TryGetValue(b, out var n2) ? n2 : 0)} ray hits)";
            if (!q.SurfaceZ.TryGetValue(b, out var z) || z < q.FrameLo - FrameSlack || z > q.FrameHi + FrameSlack) return $"the surface under her {b} is not in the frame";
        }
        if (Math.Abs(r.Gap!.Value) > HugeGap) return $"gap {r.Gap:+0.000;-0.000} m is beyond any fit ({HugeGap} m): unmeasurable (a misread pose kind, a surface over her, or a bad saved offset), not an adjustment";
        if (waterSeat && r.Gap!.Value < Band(r).lo) return $"a sit in water: the sunk reading {r.Gap:+0.000;-0.000} m is the water surface over her, unmeasurable";
        return null;
    }
    // ---- retakes (David 2026-11-22: "Cap every retake at 1 m, then rotate 180"): retake 1 = the wide retake (GT_SUPPORT_WIDE=1), its camera never
    // more than 1 m from her (render_mesh RETAKE_MAX_CAM_M; the lens widens instead); retake 2, only while retake 1 is still too dark = the same retake
    // from the opposite side (GT_RETAKE_TURN=180, same height and distance). Still too dark after that = unmeasurable, nothing pressed. ----
    internal const int MaxRetake = 2;
    internal const string DarkGatePrefix = "the close-up is too dark";
    // pure (tested): the next retake after a failed gate, 0 = stop. Only a too-dark retake 1 gets the opposite side; any other failure stops there.
    internal static int NextRetake(int retake, string gateFail)
        => retake == 1 && gateFail != null && gateFail.StartsWith(DarkGatePrefix, StringComparison.Ordinal) ? 2 : 0;
    // pure (tested): the render env for a retake (null = the plain close-up)
    internal static Dictionary<string, string> RetakeEnv(int retake)
        => retake <= 0 ? null : retake == 1 ? new Dictionary<string, string> { ["GT_SUPPORT_WIDE"] = "1" }
         : new Dictionary<string, string> { ["GT_SUPPORT_WIDE"] = "1", ["GT_RETAKE_TURN"] = "180" };
    internal static string RetakeLabel(int retake) => retake <= 1 ? "wide retake" : "opposite-side retake (180)";

    // ---- unmeasurable -> tell David in world (David 2026-11-22: "tell me in world when you are unable to measure so we can troubleshoot") ----
    // pure (tested): the run's outcome is "unmeasurable" when the decision undid (or never made) a nudge because a render was not good enough
    internal static string UnmeasurableFromSave(SaveCall call, string why)
        => call == SaveCall.Revert && why != null && why.Contains("not good enough", StringComparison.Ordinal) ? why : null;
    // pure (tested): the reason in a few words: the last step of a gate trail ("...; opposite-side retake (180): the close-up is too dark (81 %)")
    internal static string ShortReason(string why)
    {
        if (string.IsNullOrWhiteSpace(why)) return "the render was not usable";
        var r = why.Split("; ").Last().Trim();
        foreach (var lbl in new[] { "wide retake: ", "opposite-side retake (180): " }) if (r.StartsWith(lbl, StringComparison.Ordinal)) r = r[lbl.Length..];
        r = Regex.Replace(r, @"\s*\(/[^)]*\.(png|jpg)\)", "");
        return r.Length > 160 ? r[..157] + "..." : r;
    }
    // pure (tested): the IM, short and in her voice
    internal static string UnmeasurableIm(string seatName, string pose, string why)
    {
        var leaf = (pose ?? "").Split('>').Last().Trim();
        var where = string.IsNullOrWhiteSpace(seatName) ? "this seat" : seatName.Trim();
        return $"I couldn't measure my seat on {where}{(leaf.Length > 0 && !leaf.StartsWith("(") ? " / " + leaf : "")}: {ShortReason(why)}. Want to troubleshoot?";
    }
    // pure (tested): send? once per sit and pose (key = seat + pose; the set is cleared when she stands), and only while David is online
    internal static (bool send, string why) UnmeasurableNotifyDecision(ISet<string> notified, string key, bool davidOnline)
        => !davidOnline ? (false, "David is offline") : notified.Contains(key) ? (false, "already told David for this sit and pose") : (true, null);
    internal static bool SameClass(SupportReading a, SupportReading b) => a?.Kind == b?.Kind && a != null && b != null && a.Bearing.OrderBy(x => x).SequenceEqual(b.Bearing.OrderBy(x => x));

    // Save = press the furniture's Save button for this posed position. Revert = the presses are undone (nothing unproven stays). CannotSave =
    // the run can no longer reach its Save button (she was stood up / the seat changed mid-run, the menu was lost, no press was applied): the
    // caller reports it honestly instead of pretending to save.
    internal enum SaveCall { Save, CannotSave, Revert }
    // pure (tested): the ONLY things that may stop a Save (David 2026-11-04: an applied nudge is always saved), and never how the re-render reads.
    // null = the run can still save. A pause in the wander is not on this list: the menu is still open, so the save still lands.
    internal static string CannotSaveWhy(int pressesApplied, bool onSameSeat, bool menuAlive, bool saveButton = true)
        => !onSameSeat ? "she was stood up (or the seat changed) mid-run, so the menu is gone"
         : !menuAlive ? "the menu was lost (the presses got no answer)"
         : pressesApplied == 0 ? "no presses were applied"
         : !saveButton ? "this furniture's adjust menu has no Save button"
         : null;
    // pure (tested): David 2026-11-04: an APPLIED nudge is Saved once a re-render that passes the gate confirms it; the confidence reading is
    // still made, but it is only the `why` the log tells ("the pose reads differently after the nudge ...", "better but still outside the contact
    // band"). Undone: a nudge that is worse than before, one resting on a first render that never passed the gate, and (2026-11-22, the Nov 11
    // Mirage save) one whose confirming re-render fails the gate: nothing may be saved on an untrusted measurement.
    internal static (SaveCall call, string why) SaveConfidence(SupportReading before, Quality qb, SupportReading after, Quality qa, bool waterSeat = false)
    {
        if (after is { Conclusive: true } && before is { Conclusive: true } && Math.Abs(after.Gap!.Value) > Math.Abs(before.Gap!.Value) + ImproveMin) return (SaveCall.Revert, "the re-render is worse than before");
        if (GateFail(before, qb, waterSeat) is string fb) return (SaveCall.Revert, "the first render was not good enough: " + fb);
        if (GateFail(after, qa, waterSeat) is string fa) return (SaveCall.Revert, "the re-render is not good enough to confirm it, so the nudge is undone, never saved: " + fa);
        if (!SameClass(before, after)) return (SaveCall.Save, $"the pose reads differently after the nudge ({Describe(before)} -> {Describe(after)})");
        if (Math.Abs(after.Gap!.Value) > Math.Abs(before.Gap!.Value) - ImproveMin) return (SaveCall.Revert, "no measurable improvement");
        if (!InBand(after)) return (SaveCall.Save, "better but still outside the contact band");
        return (SaveCall.Save, "confident: both renders pass the gate, same pose kind, in band");
    }
    // pure (tested): the whole decision a run makes after its presses, from the render verdict plus whether it can still reach the Save button.
    // Nothing pressed = there is nothing to save and nothing to undo (CannotSave, never a pretend Revert). Losing the furniture only turns a Save
    // into CannotSave: a nudge the re-render shows to be WORSE is still undone, whatever state the menu is in, so a run that could not land its
    // undo still says so (PR #427).
    internal static (SaveCall call, string why) SaveDecision(SupportReading before, Quality qb, SupportReading after, Quality qa,
        int pressesApplied, bool onSameSeat, bool menuAlive, bool saveButton = true, bool waterSeat = false)
    {
        if (pressesApplied == 0) return (SaveCall.CannotSave, "no presses were applied");
        var r = SaveConfidence(before, qb, after, qa, waterSeat);
        return r.call == SaveCall.Save && CannotSaveWhy(pressesApplied, onSameSeat, menuAlive, saveButton) is string why ? (SaveCall.CannotSave, why) : r;
    }

    // ---- bugged pose detection ----
    internal sealed record BoxZ(double Bottom, double Top);
    // pure (tested): the vertical extent of a linkset from object data: each prim's centre z +- its own vertical half-extent (from its scale and rotation)
    internal static BoxZ LinksetZ(IEnumerable<(double z, double halfZ)> prims)
    {
        var l = prims?.ToList() ?? new();
        return l.Count == 0 ? null : new BoxZ(l.Min(p => p.z - p.halfZ), l.Max(p => p.z + p.halfZ));
    }
    // pure (tested): a box prim's vertical half-extent: half its scale projected on world up through its rotation (x, y, z, w quaternion)
    internal static double HalfZ(double sx, double sy, double sz, double qx, double qy, double qz, double qw)
    {
        double r20 = 2 * (qx * qz - qw * qy), r21 = 2 * (qy * qz + qw * qx), r22 = 1 - 2 * (qx * qx + qy * qy);
        return (Math.Abs(r20) * sx + Math.Abs(r21) * sy + Math.Abs(r22) * sz) / 2;
    }
    // pure (tested): a reason when the pose is broken, else null. avatarZ = her centre (SimPosition), height = her size z.
    // 2026-11-22: only OBJECT DATA (floating / buried) or the anims denylist a pose. A huge render gap alone is SuspectReason (logged, never
    // denylisted): David confirmed three such poses fine (tub 'read book' / 'shave' in water, the Mirage 'texting' sunk by a bad saved offset).
    internal static string BuggedReason(double? avatarZ, double? height, BoxZ seat, SupportReading r, bool? poseAnimPlaying)
    {
        if (poseAnimPlaying == false) return "the seat's pose animation is not playing (only built-in anims)";
        if (avatarZ is double z && height is double hgt && hgt > 0.5 && seat != null)
        {
            double lo = z - hgt / 2, hi = z + hgt / 2;
            if (lo > seat.Top + FloatOverTop) return $"floating: her lowest point is {lo - seat.Top:0.00} m above the top of the furniture";
            if (hi < seat.Bottom - BuriedUnder) return $"buried: her highest point is {seat.Bottom - hi:0.00} m below the bottom of the furniture";
        }
        return null;
    }
    // pure (tested): a render-only reading too far off to be a fit problem: told in the log, nothing pressed, NOT denylisted
    internal static string SuspectReason(SupportReading r, bool waterSeat)
        => r is { Conclusive: true } && Math.Abs(r.Gap!.Value) > HugeGap
            ? $"the render shows {Describe(r)}{(waterSeat && r.Gap < 0 ? " in a water seat (the water surface over her)" : "")}: beyond any fit from the render alone, so unmeasurable, not denylisted"
            : null;
    // pure (tested): the pose label a check is for: the last pose choice only when it was made on THIS seat (17:14 makeup table run was labelled the tub's 'shave')
    internal static string PoseLabelFor(string lastPose, string seatId)
    {
        if (string.IsNullOrEmpty(lastPose) || string.IsNullOrEmpty(seatId) || !lastPose.Contains(seatId)) return "(default pose)";
        var i = lastPose.IndexOf(seatId, StringComparison.Ordinal) + seatId.Length;
        var rest = lastPose[i..].TrimStart(':', ' ');
        var b = rest.IndexOf(" [", StringComparison.Ordinal); var t = rest.IndexOf(" (", StringComparison.Ordinal);
        int cut = new[] { b, t }.Where(x => x >= 0).DefaultIfEmpty(rest.Length).Min();
        return rest[..cut].Trim();
    }
    // pure (tested): menu navigation toward the adjust controls. A touch re-shows the submenu she is in (17:14: Masturbate* page, no [ADJUST]): go [BACK] up.
    internal static string NavPress(string message, IReadOnlyList<string> labels) => Classify(message, labels) switch
    {
        Menu.Options => "[POSE]",
        Menu.PoseMenu => "[ADJUST]",
        Menu.Entry => EntryButton(labels),
        Menu.None => (labels ?? Array.Empty<string>()).Select(x => x.Trim()).FirstOrDefault(x => Regex.IsMatch(x, @"^\W*(back|<<|main|top|home)\W*$", RegexOptions.IgnoreCase)) is { } bk ? bk : null,
        _ => null,
    };
    // pure (tested): `seatadjust z <dz metres>`: ONE manual height nudge through the furniture's own adjust (PlanZ lays the presses out and
    // cycles AVsitter's step button). 2026-11-03: a manual nudge had to be driven button by button (the Dildo pose took 4 presses where 3 would do).
    // Never saves: a manual move is not a measurement, so the seat check's own Save rule still decides.
    internal static (bool ok, double dz, string err) ParseZ(string[] a)
    {
        if (a is { Length: 2 } && string.Equals(a[0]?.Trim(), "z", StringComparison.OrdinalIgnoreCase)
            && double.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var dz) && Math.Abs(dz) >= Step / 2 && Math.Abs(dz) <= 1.0)
            return (true, dz, null);
        return (false, 0, "usage: seatadjust z <dz metres, e.g. -0.08 (+ is up, - is down; one AVsitter 1 cm step or more, up to 1 m)>");
    }
    // pure (tested): `seatadjust restore <dz>` / `seatadjust default`
    internal static (bool ok, string err) ParseRestore(string[] a)
    {
        if (a.Length >= 1 && a[0] == "default") return (true, null);
        if (a.Length >= 2 && a[0] == "restore" && double.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var dz) && Math.Abs(dz) >= Step / 2 && Math.Abs(dz) <= 1.0) return (true, null);
        return (false, "usage: seatadjust restore <dz metres, e.g. +0.08> | seatadjust default");
    }
}
