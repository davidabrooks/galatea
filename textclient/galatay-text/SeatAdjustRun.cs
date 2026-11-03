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
    internal const string PoseAdjustLog = "/workspace/secondlife/pose-adjust-log.md";
    internal const int SeatAdjustSettleMs = 8000;
    static int seatAdjustGen;
    internal static volatile string seatAdjustLast = "none yet";
    static void SALog(string m) { seatAdjustLast = $"{DateTime.Now:HH:mm:ss} {m}"; Log("seatadjust", m); }

    // called (fire and forget) when SeatPose chose a leaf pose; a newer pose cancels an older pending adjust
    static void SeatAdjustAfterPoseAsync(Primitive seat, string seatName, string pose)
    {
        if (Env("GT_SEAT_ADJUST", "on") == "off") return;
        int gen = Interlocked.Increment(ref seatAdjustGen);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SeatAdjustSettleMs);
                if (gen != Volatile.Read(ref seatAdjustGen)) return;
                await SeatAdjustRun(seat, seatName, pose, gen);
            }
            catch (Exception ex) { SALog($"'{seatName}' {pose}: error {ex.GetBaseException().Message}"); }
        });
    }
    static bool SeatAdjustStill(Primitive seat, int gen) => gen == Volatile.Read(ref seatAdjustGen) && client.Self.SittingOn == seat.LocalID && !(WanderOn && wanderPause != null);

    internal static async Task<string> SeatAdjustRun(Primitive seat, string seatName, string pose, int gen)
    {
        bool mine = seat.OwnerID == client.Self.AgentID || seat.Properties?.OwnerID == client.Self.AgentID;
        if (!SeatAdjust.MayAdjust(InPeronaut, mine)) { SALog($"'{seatName}' {pose}: not home furniture and not mine: no adjust"); return seatAdjustLast; }
        if (!SeatAdjustStill(seat, gen)) return "moved on";
        var (r0, img0, w0) = await hoverSvc.SupportRender();
        if (r0 is not { Conclusive: true }) { SALog($"'{seatName}' {pose}: render inconclusive ({w0 ?? SeatAdjust.Describe(r0)}): nothing pressed ({img0})"); return seatAdjustLast; }
        if (SeatAdjust.InBand(r0)) { SALog($"'{seatName}' {pose}: {SeatAdjust.Describe(r0)}: contact good, no adjust ({img0})"); return seatAdjustLast; }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(150)); var ct = cts.Token;
        var d = await SeatAdjustOpenPersonal(seat, ct);
        if (d == null) { SALog($"'{seatName}' {pose}: {SeatAdjust.Describe(r0)} but no AVsitter personal adjust menu (nPose / other system): logged, skipped ({img0})"); return seatAdjustLast; }
        double applied = 0; var before = r0; var cur = r0; SeatAdjust.Verdict v = SeatAdjust.Verdict.Continue;
        for (int it = 0; it < SeatAdjust.MaxIterations && v == SeatAdjust.Verdict.Continue; it++)
        {
            double dz = SeatAdjust.CorrectionZ(cur);
            if (dz == 0) break;
            d = await SeatAdjustPressZ(seat, d, dz, ct); if (d == null) { v = SeatAdjust.Verdict.Inconclusive; break; }
            applied += dz;
            await Task.Delay(2500, ct);
            if (!SeatAdjustStill(seat, gen)) { v = SeatAdjust.Verdict.Inconclusive; break; }
            var (r, img, _) = await hoverSvc.SupportRender();
            SALog($"'{seatName}' {pose}: nudged Z {dz:+0.00;-0.00} (total {applied:+0.00;-0.00}): {SeatAdjust.Describe(cur)} -> {SeatAdjust.Describe(r)} ({img})");
            v = SeatAdjust.Decide(cur, r);
            if (v is SeatAdjust.Verdict.Save or SeatAdjust.Verdict.Continue) cur = r;
        }
        string outcome;
        if (v == SeatAdjust.Verdict.Save && d != null && SeatAdjust.Decide(before, cur) == SeatAdjust.Verdict.Save && SeatAdjustStill(seat, gen))
        {
            int si = d.ButtonLabels.FindIndex(b => b.Trim() == "[SAVE]");
            client.Self.ReplyToScriptDialog(d.Channel, si, d.ButtonLabels[si], d.ObjectID);
            outcome = "SAVED ([SAVE]: AVsitter personal offset for this pose)";
        }
        else
        {
            if (applied != 0 && d != null) d = await SeatAdjustPressZ(seat, d, -applied, ct);   // undo: nothing unproven stays
            outcome = $"not saved ({v}); {(applied != 0 ? $"undid Z {-applied:+0.00;-0.00}" : "nothing moved")}";
            applied = 0;
        }
        SALog($"'{seatName}' {pose}: {outcome}: before {SeatAdjust.Describe(before)}, after {SeatAdjust.Describe(cur)}");
        try
        {
            if (!File.Exists(PoseAdjustLog)) File.WriteAllText(PoseAdjustLog, "# Pose adjust log (SeatAdjust.cs)\n\n| when | object | pose | offset | before | after | outcome |\n|---|---|---|---|---|---|---|\n");
            File.AppendAllText(PoseAdjustLog, SeatAdjust.LogRow(DateTime.Now, $"{seatName} ({seat.ID})", pose, applied, before, cur, outcome) + "\n");
        }
        catch (Exception ex) { SALog("log write failed: " + ex.Message); }
        return seatAdjustLast;
    }

    // `seatadjust` command: run the check now on the seat she is on (status: the last result)
    static async Task<string> SeatAdjustCmd(string[] a)
    {
        if (a.Length > 0 && a[0] == "status") return "seatadjust: " + seatAdjustLast;
        var seat = Sim?.ObjectsPrimitives.Values.FirstOrDefault(p => p != null && p.LocalID == client.Self.SittingOn);
        if (seat == null) return "seatadjust: not sitting on an object";
        int gen = Interlocked.Increment(ref seatAdjustGen);
        return "seatadjust: " + await SeatAdjustRun(seat, seat.Properties?.Name ?? seat.ID.ToString(), wLastPose ?? "?", gen);
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
        for (int hop = 0; d != null && hop < 4; hop++)
        {
            switch (SeatAdjust.Classify(d.Message, d.ButtonLabels))
            {
                case SeatAdjust.Menu.Personal:
                    if (!SeatAdjust.PersonalState(d.ButtonLabels).position) d = await SeatAdjustPress(seat, d, "Rotation", ct);
                    return d != null && SeatAdjust.PersonalState(d.ButtonLabels).position && SeatAdjust.PersonalState(d.ButtonLabels).step != null ? d : null;
                case SeatAdjust.Menu.Options: d = await SeatAdjustPress(seat, d, "[POSE]", ct); break;
                case SeatAdjust.Menu.PoseMenu: d = await SeatAdjustPress(seat, d, "[ADJUST]", ct); break;
                default: return null;
            }
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
