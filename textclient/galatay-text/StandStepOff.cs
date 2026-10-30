// StandStepOff.cs (2026-10-30 PT, David: "When you stood from this furniture you ended up standing on the chair").
// The plain `stand` tool only sent Stand(): the sim drops her at the seat prim's top (Xplicit Makeup Table chair, z ~29.5 vs
// floor ~28.95) and nothing moved her off. The stand-out / clear-floor steps (SeatStandOut.cs, ClearFloor.cs) ran only for
// wander and outfit flows. Now an explicit stand from an object also runs the clear-floor step (home footprints + nav grid,
// the seat's stand_spot first). The -4 cm stand hover is untouched: the posture-change hover check restores it after the walk.
// 2026-10-30 10:38 PT follow-up (David: "I saw you walking but you didn't really go anywhere, and it was just luck you ended up on the floor").
// Log: after a fixed 1.2 s she was still at the seat position <222.4,72.5,29.5>; the target was the seat's stand_spot 221.6,72.9 only 0.8 m
// away (and only 0.5 m outside the table footprint x0 222.14). The sim autopilot stops ~1 m short of a close goal, so WalkLeg's stop-distance
// slack called it "arrived" after 0.3 m at <222.1,72.4>, and the clear check (plain footprint, no margin) passed because that is 4 cm outside
// the box. Now: wait for her real post-unseat position (settled), plan with a 0.6 m footprint margin (StandOffMarginM), aim the autopilot
// THROUGH the spot so its stop distance lands on it, verify arrival, nudge with her own walk controls (no teleport) if short, and only then repin
// the hover and run the visual check.
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    // pure: step off after the stand tool only when she was seated on an object (a ground sit leaves her on the floor already)
    internal static bool StandCommandStepsOff(bool wasSeatedOnObject) => wasSeatedOnObject;

    // pure: what runs after the stand tool unseats her, in order. The stand hover repin is ALWAYS last (2026-10-30: she floated after
    // `stand` until a manual `hover repin`), so it also follows any step-off walk.
    internal static string[] StandCommandAfterSteps(bool wasSeatedOnObject) =>
        StandCommandStepsOff(wasSeatedOnObject) ? new[] { "step off", "repin hover" } : new[] { "repin hover" };

    static async Task StandCommandAfter(bool wasSeatedOnObject)
    {
        foreach (var step in StandCommandAfterSteps(wasSeatedOnObject))
            try
            {
                if (step == "step off")
                {
                    using var cc = new CancellationTokenSource(30000);
                    await StandStepOff(cc.Token);
                }
                else if (client.Self.SittingOn == 0)
                {
                    await Task.Delay(500);
                    var r = await PostHover(PinnedHover(), "stand command repin (last verified for this footwear)");
                    WLog($"stand command: hover repin {PinnedHover():F3} -> {(r.ok ? "ok" : "failed")} {r.info}; visual check follows");
                    hoverSvc.HoverCheckRequest("stand", 3000);   // its verified result is the hover kept in force (and saved for this footwear)
                }
            }
            catch (Exception ex) { WLog($"stand command ({step}): " + ex.GetBaseException().Message); }
    }

    internal const float StandOffMarginM = 0.6f, StandOffArriveTol = 0.25f, StandOffAimThroughM = 1.2f;

    // pure: is this a settled post-unseat position? (the last samples within 3 cm of each other, horizontally and in z)
    internal static bool StandPositionSettled(IReadOnlyList<Vector3> samples) =>
        samples.Count >= 3 && samples.Skip(samples.Count - 3).All(p => Vector3.Distance(p, samples[^1]) < 0.03f);

    // pure: where to aim the autopilot so its ~1 m stop distance ends ON `to`: past it along the walk, unless the far point is not clear
    internal static Vector3 StandOffAim(Vector3 from, Vector3 to, Func<Vector3, bool> clearAt)
    {
        float dx = to.X - from.X, dy = to.Y - from.Y, l = MathF.Sqrt(dx * dx + dy * dy);
        if (l < 0.01f) return to;
        for (float k = StandOffAimThroughM; k > 0.05f; k -= 0.4f)
        {
            var a = new Vector3(to.X + dx / l * k, to.Y + dy / l * k, to.Z);
            if (clearAt == null || clearAt(a)) return a;
        }
        return to;
    }

    // pure: has the walk reached the spot? close to it, or already past it along the walk direction
    internal static bool StandOffReached(Vector3 from, Vector3 to, Vector3 now)
    {
        if (HDist(now, to) <= StandOffArriveTol) return true;
        float dx = to.X - from.X, dy = to.Y - from.Y, l2 = dx * dx + dy * dy;
        if (l2 < 1e-4f) return false;
        float t = ((now.X - from.X) * dx + (now.Y - from.Y) * dy) / l2;
        return t >= 1f && HDist(now, to) <= 0.6f;
    }

    // pure: the end state counts as clear floor only at least StandOffMarginM outside every footprint (not merely outside the box)
    internal static bool StandOffClear(Vector3 now, IReadOnlyList<FurnitureBox> boxes) => InFurnitureMargin(now, boxes, StandOffMarginM) == null;

    static async Task<Vector3> SettledStandPosition(CancellationToken ct)
    {
        var s = new List<Vector3>(); var t0 = DateTime.Now;
        await Task.Delay(400, ct);
        while ((DateTime.Now - t0).TotalSeconds < 4)
        {
            s.Add(client.Self.SimPosition);
            if (StandPositionSettled(s)) break;
            await Task.Delay(250, ct);
        }
        WLog($"stand step-off: post-unseat position {P3(client.Self.SimPosition)} ({(StandPositionSettled(s) ? "settled" : "not settled after 4 s")}, {s.Count} samples)");
        return client.Self.SimPosition;
    }

    // stand command: step down off the seat to floor at least 0.6 m outside furniture, verified (no teleport)
    static async Task StandStepOff(CancellationToken ct)
    {
        if (!LoggedIn || !InPeronaut || client.Self.SittingOn != 0) return;
        var me = await SettledStandPosition(ct);
        await standOutLock.WaitAsync(ct);
        try
        {
            if (client.Self.SittingOn != 0) return;
            var boxes = HomeFootprints(); var seat = NearestHomeSeat(me, LoadHomeSeats());
            var g = NavGridHere();
            Func<float, float, bool> blocked = g == null ? null : (x, y) => { if (!g.Contains(x, y)) return false; var (i, j) = g.IJ(x, y); return g.At(i, j) != 0; };
            var plan = ClearStepPlan(me, seat, boxes, blocked, standMargin: StandOffMarginM);
            if (!plan.Needed) { WLog($"stand step-off: {plan.Why} at {P3(me)}; not walking"); return; }
            if (plan.To == null) { WLog($"stand step-off: {plan.Why}; staying at {P3(me)}"); return; }
            var to = plan.To.Value;
            Func<Vector3, bool> clearAt = p => InFurnitureMargin(p, boxes, ClearBodyMargin) == null && !(blocked?.Invoke(p.X, p.Y) ?? false);
            WLog($"stand step-off: at {P3(me)} {plan.Why} {P3(to)} ({HDist(me, to):F1} m)");
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                var from = client.Self.SimPosition;
                var aim = StandOffAim(from, to, clearAt);
                AutoPilotTo(aim);
                var t0 = DateTime.Now; Vector3 now = from;
                while ((DateTime.Now - t0).TotalSeconds < 6)
                {
                    await Task.Delay(100, ct);
                    now = client.Self.SimPosition;
                    if (StandOffReached(from, to, now)) break;
                }
                client.Self.AutoPilotCancel();
                await Task.Delay(300, ct); now = client.Self.SimPosition;
                bool ok = StandOffClear(now, boxes);
                WLog($"stand step-off: walk {attempt} aimed {P3(aim)}: {P3(from)} -> {P3(now)} ({HDist(from, now):F1} m moved, {HDist(now, to):F1} m from the spot) {(ok ? "clear" : "NOT clear yet")}");
                if (ok) return;
            }
            // nudge: her own forward walk control toward the spot, short pulses (no teleport)
            var m = client.Self.Movement;
            for (int i = 0; i < 8 && !StandOffClear(client.Self.SimPosition, boxes); i++)
            {
                try { m.TurnToward(new Vector3(to.X, to.Y, client.Self.SimPosition.Z)); } catch { }
                m.AtPos = true; m.SendUpdate(true); await Task.Delay(350, ct);
                m.AtPos = false; m.SendUpdate(true); await Task.Delay(150, ct);
            }
            var end = client.Self.SimPosition;
            WLog($"stand step-off: after nudging at {P3(end)} ({(StandOffClear(end, boxes) ? "clear" : "STILL within 0.6 m of furniture")})");
        }
        finally { standOutLock.Release(); }
    }
}
