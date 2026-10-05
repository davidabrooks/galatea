// SeatLinger.cs (2026-10-05 10:30, David saw her "standing in a sitting pose" after 'stand' from 'Lalou - Ultra Sofa v1.1':
// the sofa's pose anim d61ed35e (source = the sofa) kept playing alongside STAND, and the stand check still logged OK).
// - While seated, every anim sourced by the seat link set (IsSeatSource) and every pose-keeper copy is remembered for
//   this sit, together with the seat object ids (seat prim, its root, every seat prim that sourced an anim).
// - When the sit ends by ANY means ('stand', a walk/follow standing her up, the seat unsitting her, a seat swap), every
//   playing anim whose source is one of those seat objects, or that is one of those seat/kept anims, is stopped at once,
//   then re-checked ~2 s and ~5 s later and stopped again (logged as FAIL: lingered) if still there.
// - StandAnimCheck (AttachWatch.cs) also treats anims from the last seat as stuck and logs FAIL instead of OK; an anim from
//   any other in-world object while standing is logged as a WARN (not stopped: dance balls/HUD-less animators are legit).
// Default stand/walk anims are never stopped here.
using System.Collections.Concurrent;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    static readonly ConcurrentDictionary<UUID, byte> sitSrcIds = new();   // seat objects of the current sit
    static readonly ConcurrentDictionary<UUID, byte> sitAnimIds = new();  // anims the seat (or our pose keeper) played in this sit
    static readonly object sitSessLock = new();
    static uint sitSessSeat;                                              // seat LocalID of the open sit session (0 = none)
    static HashSet<UUID> lastSitSrc = new(), lastSitAnims = new();
    static DateTime lastSitEnded = DateTime.MinValue;
    static int sitEndGen;
    static string seatLingerLast = "-";

    // pure (selftest): which playing anims must stop once the sit has ended?
    internal static List<UUID> SeatLingerersToStop(IEnumerable<(UUID id, UUID src)> playing, ISet<UUID> seatSrcs, ISet<UUID> seatAnims, ISet<UUID> kept, ISet<UUID> neverStop, bool bySourceOnly = false)
        => playing.Where(x => !neverStop.Contains(x.id) && x.id != Animations.SIT_TO_STAND
                              && ((x.src != UUID.Zero && seatSrcs.Contains(x.src)) || (!bySourceOnly && (seatAnims.Contains(x.id) || kept.Contains(x.id)))))
                  .Select(x => x.id).Distinct().ToList();

    static void SeatSessionAddSeat(Simulator sim, uint seat)
    {
        if (sim == null || seat == 0 || !sim.ObjectsPrimitives.TryGetValue(seat, out var sp) || sp == null) return;
        sitSrcIds[sp.ID] = 1;
        if (sp.ParentID != 0 && sim.ObjectsPrimitives.TryGetValue(sp.ParentID, out var root) && root != null && root.ID != client.Self.AgentID) sitSrcIds[root.ID] = 1;
    }

    // from OnAvatarAnimation (every anim packet for us) and SeatLingerTick (every second)
    static void SeatLingerNote(IEnumerable<KeyValuePair<UUID, (int seq, UUID src)>> now)
    {
        var seat = client.Self.SittingOn; var sim = client.Network.CurrentSim;
        lock (sitSessLock)
        {
            if (seat == 0) { if (sitSessSeat != 0) SeatSessionEnded("unseated", 0); return; }
            if (sitSessSeat != 0 && sitSessSeat != seat) SeatSessionEnded($"moved to another seat ({SeatName(seat)})", seat);
            if (sitSessSeat == 0) { sitSessSeat = seat; sitSrcIds.Clear(); sitAnimIds.Clear(); }
            SeatSessionAddSeat(sim, seat);
            foreach (var kv in now)
                if (IsSeatSource(kv.Value.src)) { sitSrcIds[kv.Value.src] = 1; if (!IsDefaultStandOrWalk(kv.Key)) sitAnimIds[kv.Key] = 1; }
            foreach (var k in keptPose.Keys) sitAnimIds[k] = 1;
        }
    }

    static void SeatLingerTick()
    {
        if (!LoggedIn) return;
        Dictionary<UUID, (int seq, UUID src)> cur; lock (animLock) cur = ownAnims;
        SeatLingerNote(cur);
    }

    // newSeat != 0: a seat swap (stop only by the old seat's sources; the new seat may share pose ids)
    static void SeatSessionEnded(string why, uint newSeat)
    {
        HashSet<UUID> srcs, anims;
        lock (sitSessLock)
        {
            if (sitSessSeat == 0) return;
            srcs = sitSrcIds.Keys.ToHashSet(); anims = sitAnimIds.Keys.ToHashSet();
            foreach (var k in keptPose.Keys) anims.Add(k);
            sitSessSeat = 0; sitSrcIds.Clear(); sitAnimIds.Clear();
            lastSitSrc = srcs; lastSitAnims = anims; lastSitEnded = DateTime.Now;
        }
        int gen = Interlocked.Increment(ref sitEndGen);
        bool swap = newSeat != 0;
        int n = StopSeatLingerers($"sit ended: {why}", srcs, anims, swap, recheck: false);
        if (swap) return;
        _ = Task.Run(async () =>
        {
            foreach (var (wait, at) in new[] { (2000, "2 s"), (3000, "5 s") })
            {
                await Task.Delay(wait);
                if (gen != Volatile.Read(ref sitEndGen) || client.Self.SittingOn != 0) return;
                StopSeatLingerers($"re-check {at} after standing", srcs, anims, false, recheck: true);
            }
        });
    }

    static int StopSeatLingerers(string why, HashSet<UUID> srcs, HashSet<UUID> anims, bool bySourceOnly, bool recheck)
    {
        Dictionary<UUID, (int seq, UUID src)> cur; lock (animLock) cur = ownAnims;
        if (bySourceOnly) srcs = srcs.Where(s => !IsSeatSource(s)).ToHashSet();   // swap within the same link set: keep its sources
        var stop = SeatLingerersToStop(cur.Select(kv => (kv.Key, kv.Value.src)), srcs, anims, keptPose.Keys.ToHashSet(), AoDefaultLoco, bySourceOnly);
        foreach (var id in stop) { try { client.Self.AnimationStop(id, true); } catch { } keptPose.TryRemove(id, out _); }
        if (stop.Count > 0)
        {
            var what = string.Join(", ", stop.Select(id => $"{AnimName(id)} from {SrcDesc(cur[id].src)}"));
            seatLingerLast = $"{DateTime.Now:HH:mm:ss} {(recheck ? "FAIL lingered" : "stopped")}: {what} ({why})";
            Log("height", recheck ? $"seat linger FAIL: seat anim(s) still playing ({why}): {what} -> stopped again"
                                  : $"seat linger: {why}: stopped seat anim(s) {what}");
        }
        else if (!recheck) { seatLingerLast = $"{DateTime.Now:HH:mm:ss} none left playing ({why})"; } if (stop.Count == 0 && !recheck) Log("height", $"seat linger: {why}: no seat anims left playing ({srcs.Count} seat object(s), {anims.Count} seat anim(s) watched)");
        return stop.Count;
    }

    // for StandAnimCheck: anims of the seat she just left (within 10 min)
    static List<UUID> LastSeatLingerers(Dictionary<UUID, (int seq, UUID src)> cur)
    {
        if ((DateTime.Now - lastSitEnded).TotalMinutes > 10) return new();
        return SeatLingerersToStop(cur.Select(kv => (kv.Key, kv.Value.src)), lastSitSrc, lastSitAnims, keptPose.Keys.ToHashSet(), AoDefaultLoco);
    }

    internal static string SeatLingerSelfTest()
    {
        var sb = new System.Text.StringBuilder(); int pass = 0, fail = 0; void C(bool ok, string w) { if (ok) pass++; else fail++; sb.AppendLine($"{(ok ? "PASS" : "FAIL")} {w}"); }
        var sofa = new UUID("f6844138-499f-0f3e-ba6a-ce601a16eafe"); var pose = new UUID("d61ed35e-3f36-5f06-4440-938983ddbdf0");
        var hud = UUID.Random(); var ao = UUID.Random(); var face = UUID.Random(); var kept = UUID.Random(); var me = UUID.Random();
        var none = new HashSet<UUID>();
        var playing = new List<(UUID, UUID)> { (Animations.STAND, UUID.Zero), (pose, sofa), (ao, hud), (face, hud) };
        var r = SeatLingerersToStop(playing, new HashSet<UUID> { sofa }, new HashSet<UUID> { pose }, none, AoDefaultLoco);
        C(r.Count == 1 && r[0] == pose, "10:30 case: sofa pose d61ed35e (source = sofa) next to STAND -> stopped; STAND / AO / face anims kept");
        r = SeatLingerersToStop(new List<(UUID, UUID)> { (pose, me) }, new HashSet<UUID> { sofa }, new HashSet<UUID> { pose }, none, AoDefaultLoco);
        C(r.Count == 1, "seat pose re-sourced to self (our copy) -> stopped by anim id");
        r = SeatLingerersToStop(new List<(UUID, UUID)> { (kept, me) }, none, none, new HashSet<UUID> { kept }, AoDefaultLoco);
        C(r.Count == 1, "pose-keeper copy -> stopped");
        r = SeatLingerersToStop(new List<(UUID, UUID)> { (Animations.STAND, sofa), (Animations.SIT_TO_STAND, sofa) }, new HashSet<UUID> { sofa }, none, none, AoDefaultLoco);
        C(r.Count == 0, "default STAND / SIT_TO_STAND never stopped, even if seat-sourced");
        var other = UUID.Random(); var newSeat = UUID.Random();
        r = SeatLingerersToStop(new List<(UUID, UUID)> { (pose, newSeat), (other, sofa) }, new HashSet<UUID> { sofa }, new HashSet<UUID> { pose }, none, AoDefaultLoco, bySourceOnly: true);
        C(r.Count == 1 && r[0] == other, "seat swap: only the old seat's sources stop (same pose id from the new seat kept)");
        r = SeatLingerersToStop(new List<(UUID, UUID)> { (Animations.SIT, UUID.Zero) }, none, new HashSet<UUID> { Animations.SIT }, none, AoDefaultLoco);
        C(r.Count == 1, "built-in SIT learned during the sit -> stopped");
        return $"seat linger selftest: {pass} PASS, {fail} FAIL\n" + sb.ToString().TrimEnd();
    }
}
