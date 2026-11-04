// SeatAdjustRun.cs: the live side of SeatAdjust.cs (render -> AVsitter Z nudge -> re-render -> [SAVE] only when measurably better and in band).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibreMetaverse;
namespace GalatayText;

public static partial class Program
{
    internal const int SeatAdjustSettleMs = 8000;
    static int seatAdjustGen;
    internal static volatile string seatAdjustLast = "none yet";
    static void SALog(string m) { seatAdjustLast = $"{DateTime.Now:HH:mm:ss} {m}"; Log("seatadjust", m); }

    // called (fire and forget) when SeatPose chose a leaf pose; a newer pose cancels an older pending adjust
    static void SeatAdjustAfterPoseAsync(Primitive seat, string seatName, string pose)
    {
        if (Env("GT_SEAT_ADJUST", "on") == "off") return;
        int gen = Interlocked.Increment(ref seatAdjustGen); seatAdjustCheckedSeat = seat.LocalID;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SeatAdjustSettleMs);
                for (int w = 0; w < 60 && Volatile.Read(ref seatPosePicking) > 0; w++) await Task.Delay(1000);   // a pose pick in progress: its own choice re-starts the check
                if (gen != Volatile.Read(ref seatAdjustGen) || Volatile.Read(ref seatPosePicking) > 0) return;
                await SeatAdjustRun(seat, seatName, pose, gen);
            }
            catch (Exception ex) { SALog($"'{seatName}' {pose}: error {ex.GetBaseException().Message}"); }
        });
    }
    static bool SeatAdjustStill(Primitive seat, int gen) => gen == Volatile.Read(ref seatAdjustGen) && client.Self.SittingOn == seat.LocalID && !(WanderOn && wanderPause != null);

    internal static volatile string seatAdjustUnsaved = null;   // the last UNSAVED line, shown in wander status until a later sit saves or needs nothing
    static uint seatAdjustCheckedSeat, seatWatchPrev;
    static Thread seatWatchThread;
    // every sit on anything gets a check, also sits with no pose choice (manual sit, default pose, no menu). A pose choice resets it (newest wins).
    static void StartSeatAdjustWatch()
    {
        if (Env("GT_SEAT_ADJUST", "on") == "off") return;
        lock (typeof(SeatAdjust)) { if (seatWatchThread != null) return; seatWatchThread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    Thread.Sleep(2000);
                    uint now = LoggedIn ? client.Self.SittingOn : 0, prev = seatWatchPrev; seatWatchPrev = now;
                    if (now == 0) { seatAdjustCheckedSeat = 0; continue; }
                    if (Volatile.Read(ref seatPosePicking) > 0) { seatWatchPrev = prev; continue; }   // mid-pick: wait, the pick starts its own check
                    if (!SeatAdjust.SitNeedsCheck(prev, now, seatAdjustCheckedSeat)) continue;
                    var seat = Sim?.ObjectsPrimitives.Values.FirstOrDefault(p => p != null && p.LocalID == now);
                    if (seat == null) { seatWatchPrev = 0; continue; }   // not in view yet: retry next tick
                    // a root's child prim: the sit is on the linkset; menus come from the root
                    SeatAdjustAfterPoseAsync(seat, seat.Properties?.Name ?? seat.ID.ToString(), SeatAdjust.PoseLabelFor(wLastPose, SeatRootOf(seat).ID.ToString()));
                }
                catch { }
            }
        }) { IsBackground = true, Name = "seatadjust-watch" }; }
        seatWatchThread.Start();
    }

    static void SAUnsaved(string seatName, string pose, double applied, string why)
    {
        var line = SeatAdjust.UnsavedLine(seatName, pose, applied, why);
        seatAdjustUnsaved = $"{DateTime.Now:HH:mm} {line}"; SALog(line);
        try { Notify("seatadjust_unsaved", "Galatay (seatadjust)", client.Self.AgentID, line, null); } catch { }
    }

    static Primitive SeatRootOf(Primitive p) => p != null && p.ParentID != 0 && Sim?.ObjectsPrimitives.TryGetValue(p.ParentID, out var r) == true && r != null ? r : p;
    // the seat linkset's vertical box from object data (root + children), in region coordinates
    static SeatAdjust.BoxZ SeatLinksetZ(Primitive seat)
    {
        var root = SeatRootOf(seat); if (root == null || Sim == null) return null;
        double H(Vector3 sc, Quaternion q) => SeatAdjust.HalfZ(sc.X, sc.Y, sc.Z, q.X, q.Y, q.Z, q.W);
        var l = new List<(double, double)> { (root.Position.Z, H(root.Scale, root.Rotation)) };
        foreach (var c in Sim.ObjectsPrimitives.Values.Where(q => q != null && q.ParentID == root.LocalID))
            l.Add(((root.Position + c.Position * root.Rotation).Z, H(c.Scale, c.Rotation * root.Rotation)));
        return SeatAdjust.LinksetZ(l);
    }
    static string SeatAdjustBugged(Primitive seat, SeatAdjust.SupportReading r)
    {
        var av = SelfAv(); bool? anim = null; try { anim = HasPoseAnim(SelfAnims()); } catch { }
        return SeatAdjust.BuggedReason(client.Self.SimPosition.Z, av != null ? av.Scale.Z : null, SeatLinksetZ(seat), r, anim);
    }
    static int seatBuggedSwitches;
    // a broken pose: denylist it for THIS furniture, tell David, pick another pose on the same furniture (stay seated); stand only when none is left
    static async Task<string> SeatAdjustOnBugged(Primitive seat, string seatName, string pose, string reason, string img)
    {
        var leaf = (pose ?? "").Split('>').Last().Trim();
        try { if (leaf.Length > 0 && !leaf.StartsWith("(")) PoseBugList.AddToFile(seatName, leaf, pose, reason); } catch (Exception ex) { SALog($"'{seatName}' {pose}: could not write the bugged-pose list: {ex.Message}"); }
        var line = $"BUGGED POSE '{seatName}' {pose}: {reason}; denylisted for this furniture ({img})";
        SALog(line); try { Notify("bugged_pose", "Galatay (seatadjust)", client.Self.AgentID, line, null); } catch { }
        if (++seatBuggedSwitches > 6) { seatBuggedSwitches = 0; StandFaceFirst(); SALog($"'{seatName}': too many bugged poses in a row; stood up"); return seatAdjustLast; }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var res = await SeatPose(seat, seatName, DateTime.Now, true, cts.Token);
        if (res == null || !res.Contains(" chose "))
        {
            SALog($"'{seatName}': no other solo pose left after denylisting {pose} ({res}); standing up");
            seatBuggedSwitches = 0; if (client.Self.SittingOn != 0) StandFaceFirst();
        }
        else SALog($"'{seatName}': switched away from bugged {pose}: {res}");
        return seatAdjustLast;
    }
    // one render, gated; a failed gate gets ONE wide retake (the bearing parts and the surface under them in frame)
    static async Task<(SeatAdjust.SupportReading r, string img, string why, SeatAdjust.Quality q, string gate)> SeatAdjustGatedRender()
    {
        var (r, img, why, q) = await hoverSvc.SupportRender();
        var g = SeatAdjust.GateFail(r, q);
        if (g == null || r is not { Conclusive: true } && why != null && r == null) return (r, img, why, q, g);
        var (r2, img2, why2, q2) = await hoverSvc.SupportRender(wide: true);
        var g2 = SeatAdjust.GateFail(r2, q2);
        return g2 == null || r2 != null ? (r2, img2, why2, q2, g2 == null ? null : $"{g}; wide retake: {g2}") : (r, img, why, q, g);
    }

    internal static async Task<string> SeatAdjustRun(Primitive seat, string seatName, string pose, int gen)
    {
        if (!SeatAdjustStill(seat, gen)) return "moved on";
        var (r0, img0, w0, q0, g0) = await SeatAdjustGatedRender();
        if (!SeatAdjustStill(seat, gen)) return "moved on";
        var bug = SeatAdjustBugged(seat, r0);
        if (bug != null) return await SeatAdjustOnBugged(seat, seatName, pose, bug, img0);
        seatBuggedSwitches = 0;
        if (r0 is not { Conclusive: true }) { SALog($"'{seatName}' {pose}: render inconclusive ({w0 ?? SeatAdjust.Describe(r0)}): nothing pressed ({img0})"); return seatAdjustLast; }
        if (SeatAdjust.InBand(r0) && g0 == null) { SALog($"'{seatName}' {pose}: {SeatAdjust.Describe(r0)}: contact good, no adjust ({img0})"); return seatAdjustLast; }
        if (g0 != null) { SALog($"'{seatName}' {pose}: {SeatAdjust.Describe(r0)} but the render cannot be trusted ({g0}): nothing pressed ({img0})"); return seatAdjustLast; }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(150)); var ct = cts.Token;
        var d = await SeatAdjustOpenPersonal(seat, ct);
        if (d == null) { SALog($"'{seatName}' {pose}: {SeatAdjust.Describe(r0)} but this furniture's menus have no adjust controls I can find (no AVsitter personal adjust, no up/down or Z+/Z- buttons): nothing pressed ({img0})"); return seatAdjustLast; }
        if (SeatAdjust.Classify(d.Message, d.ButtonLabels) == SeatAdjust.Menu.Generic) return await SeatAdjustGeneric(seat, seatName, pose, gen, d, r0, q0, ct);
        double applied = 0; var before = r0; var cur = r0; var qcur = q0; SeatAdjust.Verdict v = SeatAdjust.Verdict.Continue;
        for (int it = 0; it < SeatAdjust.MaxIterations && v == SeatAdjust.Verdict.Continue; it++)
        {
            double dz = SeatAdjust.CorrectionZ(cur);
            if (dz == 0) break;
            d = await SeatAdjustPressZ(seat, d, dz, ct); if (d == null) { v = SeatAdjust.Verdict.Inconclusive; break; }
            applied += dz;
            await Task.Delay(2500, ct);
            if (!SeatAdjustStill(seat, gen)) { v = SeatAdjust.Verdict.Inconclusive; break; }
            var (r, img, _, q, g) = await SeatAdjustGatedRender();
            SALog($"'{seatName}' {pose}: nudged Z {dz:+0.00;-0.00} (total {applied:+0.00;-0.00}): {SeatAdjust.Describe(cur)} -> {SeatAdjust.Describe(r)}{(g != null ? $" [gate: {g}]" : "")} ({img})");
            v = g != null ? SeatAdjust.Verdict.Inconclusive : SeatAdjust.Decide(cur, r);
            cur = r; qcur = q;
            if (v is not (SeatAdjust.Verdict.Save or SeatAdjust.Verdict.Continue)) break;
        }
        var (call, why) = SeatAdjust.SaveConfidence(before, q0, cur, qcur);
        if (applied == 0) call = SeatAdjust.SaveCall.Revert;
        string outcome;
        if (call == SeatAdjust.SaveCall.Save && d != null && SeatAdjustStill(seat, gen))
        {
            int si = d.ButtonLabels.FindIndex(b => b.Trim() == "[SAVE]");
            var t = DateTime.Now; client.Self.ReplyToScriptDialog(d.Channel, si, d.ButtonLabels[si], d.ObjectID);
            var after = await WaitSeatDialog(seat, t, 5000, ct);   // AVsitter answers a save by re-showing its menu
            if (after == null) SAUnsaved(seatName, pose, applied, "the [SAVE] press got no answer from the furniture (save not confirmed)");
            else seatAdjustUnsaved = null;
            outcome = after != null ? $"SAVED ([SAVE]: AVsitter personal offset for this pose; {why})" : "pressed [SAVE], not confirmed";
        }
        else if (call == SeatAdjust.SaveCall.KeepUnsaved && d != null)
        {
            SAUnsaved(seatName, pose, applied, "I am not confident enough to Save it: " + why);
            outcome = $"kept Z {applied:+0.00;-0.00} UNSAVED ({why})";
        }
        else
        {
            if (applied != 0 && d != null) d = await SeatAdjustPressZ(seat, d, -applied, ct);   // undo: nothing unproven stays
            if (applied != 0 && d == null) SAUnsaved(seatName, pose, applied, "the undo presses failed, so an unproven nudge may still be on");
            outcome = $"not saved ({why}); {(applied != 0 ? $"undid Z {-applied:+0.00;-0.00}" : "nothing moved")}";
        }
        SALog($"'{seatName}' {pose}: {outcome}: before {SeatAdjust.Describe(before)}, after {SeatAdjust.Describe(cur)}");
        return seatAdjustLast;
    }

    // non-AVsitter adjust (nPose and the like): the step per press is unknown, so it is MEASURED: one press, re-render, gap change = step
    static async Task<string> SeatAdjustGeneric(Primitive seat, string seatName, string pose, int gen, ScriptDialogEventArgs d, SeatAdjust.SupportReading r0, SeatAdjust.Quality q0, CancellationToken ct)
    {
        var (up, down, save) = SeatAdjust.GenericButtons(d.ButtonLabels);
        int net = 0; var before = r0; var cur = r0; var qcur = q0; SeatAdjust.Verdict v = SeatAdjust.Verdict.Continue; double? step = null;
        async Task<bool> Press(int n)
        {
            for (int i = 0; i < Math.Abs(n); i++)
            {
                d = await SeatAdjustPress(seat, d, n > 0 ? up : down, ct); if (d == null) return false;
                net += Math.Sign(n);
            }
            return true;
        }
        for (int it = 0; it < SeatAdjust.MaxIterations + 1 && v == SeatAdjust.Verdict.Continue; it++)
        {
            double dz = SeatAdjust.CorrectionZ(cur); if (dz == 0) break;
            int n = step is double s ? SeatAdjust.GenericPresses(dz, s) : Math.Sign(dz);
            if (n == 0) break;
            if (!await Press(n)) { v = SeatAdjust.Verdict.Inconclusive; break; }
            await Task.Delay(2500, ct);
            if (!SeatAdjustStill(seat, gen)) { v = SeatAdjust.Verdict.Inconclusive; break; }
            var (r, img, _, q, g) = await SeatAdjustGatedRender();
            if (g != null) { v = SeatAdjust.Verdict.Inconclusive; cur = r; qcur = q; SALog($"'{seatName}' {pose}: pressed {(n > 0 ? up : down)} x{Math.Abs(n)}: re-render untrusted ({g}) ({img})"); break; }
            SALog($"'{seatName}' {pose}: pressed {(n > 0 ? up : down)} x{Math.Abs(n)} (net {net:+0;-0}): {SeatAdjust.Describe(cur)} -> {SeatAdjust.Describe(r)} ({img})");
            if (step == null && r is { Conclusive: true } && cur.Gap is double g0 && r.Gap is double g1) step = (g0 - g1) / n;   // gap shrinks as she moves toward the surface
            v = SeatAdjust.Decide(cur, r);
            if (step is double st && Math.Abs(st) < 0.003) v = SeatAdjust.Verdict.Inconclusive;   // presses do not move her measurably
            if (v is SeatAdjust.Verdict.Save or SeatAdjust.Verdict.Continue) { cur = r; qcur = q; }
        }
        var (call, why) = SeatAdjust.SaveConfidence(before, q0, cur, qcur); if (net == 0) call = SeatAdjust.SaveCall.Revert;
        double appliedM = before.Gap is double b && cur.Gap is double c ? b - c : 0;
        string outcome;
        if (call == SeatAdjust.SaveCall.KeepUnsaved && d != null)
        {
            SAUnsaved(seatName, pose, appliedM, "I am not confident enough to Save it: " + why); outcome = $"kept UNSAVED ({why})";
        }
        else if (call == SeatAdjust.SaveCall.Save && SeatAdjustStill(seat, gen))
        {
            if (save != null)
            {
                d = await SeatAdjustPress(seat, d, save, ct);
                if (d == null) SAUnsaved(seatName, pose, appliedM, $"the '{save}' press got no answer (save not confirmed)"); else seatAdjustUnsaved = null;
                outcome = d != null ? $"SAVED ('{save}')" : $"pressed '{save}', not confirmed";
            }
            else { SAUnsaved(seatName, pose, appliedM, "this furniture's adjust menu has no Save button"); outcome = "kept, no Save button"; }
        }
        else
        {
            if (net != 0 && d != null && !await Press(-net)) d = null;
            if (net != 0 && d == null) SAUnsaved(seatName, pose, appliedM, "the undo presses failed, so an unproven nudge may still be on");
            outcome = $"not kept ({why}); {(net != 0 ? "undid the presses" : "nothing moved")}";
        }
        SALog($"'{seatName}' {pose}: {outcome}: before {SeatAdjust.Describe(before)}, after {SeatAdjust.Describe(cur)}");
        return seatAdjustLast;
    }

    // `seatadjust` command: run the check now on the seat she is on (status: the last result)
    static async Task<string> SeatAdjustCmd(string[] a)
    {
        if (a.Length > 0 && a[0] == "status") return "seatadjust: " + seatAdjustLast + (seatAdjustUnsaved != null ? "; " + seatAdjustUnsaved : "");
        var seat = Sim?.ObjectsPrimitives.Values.FirstOrDefault(p => p != null && p.LocalID == client.Self.SittingOn);
        if (seat == null) return "seatadjust: not sitting on an object";
        // `seatadjust restore <dz>` (e.g. +0.08: undo a bad saved nudge) / `seatadjust default`: on the pose she is in NOW, through AVsitter's personal
        // adjust: move by dz (or press [DEFAULT]) and press [SAVE]. Only run when David / the parent asks for it; nothing calls it automatically.
        if (a.Length > 0 && (a[0] == "restore" || a[0] == "default"))
        {
            var (ok, err) = SeatAdjust.ParseRestore(a);
            if (!ok) return "seatadjust: " + err;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var d = await SeatAdjustOpenPersonal(seat, cts.Token);
            if (d == null) return "seatadjust: no AVsitter personal adjust menu on this furniture";
            if (a[0] == "default") d = await SeatAdjustPress(seat, d, "[DEFAULT]", cts.Token);
            else d = await SeatAdjustPressZ(seat, d, double.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture), cts.Token);
            if (d == null) return "seatadjust: the presses got no answer; nothing saved";
            d = await SeatAdjustPress(seat, d, "[SAVE]", cts.Token);
            var m = $"'{seat.Properties?.Name}' {SeatAdjust.PoseLabelFor(wLastPose, SeatRootOf(seat).ID.ToString())}: {a[0]} {(a.Length > 1 ? a[1] : "")} then [SAVE]: {(d != null ? "saved" : "save not confirmed")}";
            SALog(m); return "seatadjust: " + m;
        }
        int gen = Interlocked.Increment(ref seatAdjustGen);
        return "seatadjust: " + await SeatAdjustRun(seat, seat.Properties?.Name ?? seat.ID.ToString(), SeatAdjust.PoseLabelFor(wLastPose, SeatRootOf(seat).ID.ToString()), gen);
    }
    static async Task<ScriptDialogEventArgs> SeatAdjustPress(Primitive seat, ScriptDialogEventArgs d, string label, CancellationToken ct)
    {
        int i = d.ButtonLabels.FindIndex(b => b.Trim() == label); if (i < 0) return null;
        var t = DateTime.Now; client.Self.ReplyToScriptDialog(d.Channel, i, d.ButtonLabels[i], d.ObjectID);
        return await WaitSeatDialog(seat, t, 5000, ct);
    }
    // touch -> pose menu -> [ADJUST] -> (Adjust: [POSE]) -> Personal adjustment, in Position mode. null = not AVsitter's personal adjust
    static async Task<ScriptDialogEventArgs> SeatAdjustOpenPersonal(Primitive seat, CancellationToken ct)
    {
        var t0 = DateTime.Now; client.Self.Touch(seat.LocalID);
        var d = await WaitSeatDialog(seat, t0, 5000, ct);
        for (int hop = 0; d != null && hop < 7; hop++)
        {
            var kind = SeatAdjust.Classify(d.Message, d.ButtonLabels);
            if (kind == SeatAdjust.Menu.Personal)
            {
                if (!SeatAdjust.PersonalState(d.ButtonLabels).position) d = await SeatAdjustPress(seat, d, "Rotation", ct);
                return d != null && SeatAdjust.PersonalState(d.ButtonLabels).position && SeatAdjust.PersonalState(d.ButtonLabels).step != null ? d : null;
            }
            if (kind == SeatAdjust.Menu.Generic) return d;
            var press = SeatAdjust.NavPress(d.Message, d.ButtonLabels);   // a touch re-shows the submenu she is in: [BACK] up to the page with [ADJUST]
            if (press == null) return null;
            d = await SeatAdjustPress(seat, d, press, ct);
        }
        return null;
    }
    static async Task<ScriptDialogEventArgs> SeatAdjustPressZ(Primitive seat, ScriptDialogEventArgs d, double dz, CancellationToken ct)
    {
        var st = SeatAdjust.PersonalState(d.ButtonLabels).step ?? 0.05;
        var (presses, _) = SeatAdjust.PlanZ(dz, st);
        foreach (var p in presses)
        {
            d = await SeatAdjustPress(seat, d, p, ct);
            if (d == null || SeatAdjust.Classify(d.Message, d.ButtonLabels) != SeatAdjust.Menu.Personal) return null;
        }
        return d;
    }
}
