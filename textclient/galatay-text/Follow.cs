// Follow.cs (2026-10-05, David on a house tour at Peronaut: "keep about 2-3 m behind me, especially indoors, and walk around
// the furniture instead of crowding me"; she used to close to ~0.6 m because the autopilot was aimed AT him and only cancelled
// on the next 1 s tick).
// - The autopilot is aimed at a STANDOFF POINT ~2.5 m from the leader on her side of him (never at him). On a nav grid the
//   point is moved (±20..120 deg around him, radius s/s+0.4/s-0.3) to a free cell with line of sight to him, and the walk
//   there uses the grid planner (NavFollowStep) whenever the straight line is blocked: around furniture, through doors.
// - Bands (s = follow distance, default 2.5 m): stop when <= s+0.4 m (indoors s+0.5) or at the point; resume when > s+1.0 m
//   (indoors s+1.2); if he stands still >= 1.5 s and she ended her own walk closer than s-0.5 m (2.0 m) she backs off to
//   the standoff point (max 3 back-offs until he moves again, >= 4 s apart; if HE walks up to her she stays). 'Indoors' = on a nav grid with walls/furniture within 1.5 m.
// - 250 ms tick (was 1 s), autopilot re-aimed at most every 0.5 s (BodyRotation kept in step), she turns to face him on stop.
// - Stalled (< 0.3 m in ~4 s while pursuing): robust door sequence (Doors.cs DoorUnstick) if a door/gate is within 4 m,
//   otherwise a short stuck-escape (back 1 m, sidestep 1.2 m). Never flies, never teleports.
// - 'follow <name> [m]' (m = this follow only), 'follow dist [m]' (persisted default, run/follow-dist.txt, 1-8 m),
//   'follow status', 'follow selftest'. Auto-follow of David uses the default.
// - After 'follow dist' (or this-follow metres) changes while she is holding, re-evaluate at once and close in if
//   she is beyond the new stop band (otherwise the old resume hysteresis keeps her put until he moves).
using System.Globalization;
using System.Text;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    static readonly string FollowDistFile = Env("GT_FOLLOW_DIST_FILE", "/home/box/viewers/textclient/run/follow-dist.txt");
    internal const float FollowDistMin = 1.0f, FollowDistMax = 8f, FollowDistStd = 2.5f;
    static float? followDistCache;
    static float? followDistThis;   // 'follow <name> <m>': this follow only
    static float FollowDistDefault
    {
        get
        {
            if (followDistCache is float f) return f;
            try { followDistCache = File.Exists(FollowDistFile) && F(File.ReadAllText(FollowDistFile).Trim(), out var v) ? ClampFollowDist(v) : FollowDistStd; } catch { followDistCache = FollowDistStd; }
            return followDistCache.Value;
        }
        set
        {
            followDistCache = ClampFollowDist(value);
            try { Directory.CreateDirectory(Path.GetDirectoryName(FollowDistFile)!); File.WriteAllText(FollowDistFile, followDistCache.Value.ToString("0.0#", CultureInfo.InvariantCulture) + "\n"); } catch (Exception ex) { Log("follow", "save failed: " + ex.Message); }
        }
    }
    static float FollowDist => followDistThis ?? FollowDistDefault;
    internal static float ClampFollowDist(float d) => float.IsFinite(d) ? Math.Clamp(d, FollowDistMin, FollowDistMax) : FollowDistStd;

    // ---- pure helpers (selftest) ----
    internal static (float stopAt, float resumeAt, float tooClose) FollowBands(float s, bool indoor)
        => (s + (indoor ? 0.5f : 0.4f), s + (indoor ? 1.2f : 1.0f), MathF.Max(0.8f, s - 0.5f));
    internal enum FollowAct { Hold, Pursue, Arrive, BackOff }
    internal static FollowAct FollowDecide(float d, bool pursuing, float toTarget, double leaderStillS, float s, bool indoor, bool sheClosedIn = true, bool reband = false)
    {
        var (stopAt, resumeAt, tooClose) = FollowBands(s, indoor);
        if (pursuing) return d <= stopAt || toTarget <= 0.5f ? FollowAct.Arrive : FollowAct.Pursue;
        if (d > resumeAt) return FollowAct.Pursue;
        // Dist just lowered while holding: close in past the new stop (do not wait for the old resume band)
        if (reband && d > stopAt) return FollowAct.Pursue;
        if (d < tooClose && leaderStillS >= 1.5 && sheClosedIn) return FollowAct.BackOff;
        return FollowAct.Hold;
    }
    static readonly int[] StandoffAngles = { 0, 20, -20, 40, -40, 60, -60, 90, -90, 120, -120 };
    // a point s m from the leader on her side of him; 'ok' (nav grid: free cell with line of sight to him) may move it around him
    internal static Vector2 FollowStandoffPoint(Vector2 me, Vector2 leader, float s, Func<Vector2, bool> ok)
    {
        var v = me - leader; float ang = v.Length() < 0.05f ? 0f : MathF.Atan2(v.Y, v.X);
        Vector2 At(float a, float r) => leader + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r;
        if (ok != null)
            foreach (var deg in StandoffAngles)
                foreach (var r in new[] { s, s + 0.4f, s - 0.3f })
                {
                    if (r < FollowDistMin) continue;
                    var p = At(ang + deg * MathF.PI / 180f, r);
                    if (ok(p)) return p;
                }
        return At(ang, s);
    }

    // ---- state ----
    enum FMode { Hold, Pursue, BackOff }
    static FMode fMode = FMode.Hold; static DateTime fModeSince = DateTime.MinValue;
    static UUID fLastId = UUID.Zero;
    static Vector3? fAim; static DateTime fAimAt = DateTime.MinValue;
    static Vector3? fLeaderAnchor; static DateTime fLeaderStillSince = DateTime.Now;
    static int fBackoffs; static DateTime fLastBackoff = DateTime.MinValue, fLastArriveAt = DateTime.MinValue; static Vector3 fBackT; static bool fTightLogged;
    static readonly Queue<(DateTime t, Vector3 p)> fHist = new();
    static DateTime fLastDoorRun = DateTime.MinValue, fLastEscape = DateTime.MinValue, fErrAt = DateTime.MinValue;
    static volatile bool followBusy;   // a door sequence / stuck-escape owns the autopilot
    static CancellationTokenSource fBusyCts = new();
    static int fEscapeSide = 1;
    static string followLast = "-"; static float fLastD = -1; static bool fIndoor; static Vector2 fLastT;
    static bool fDistReband;   // set when follow dist changes: next Hold tick may close in past new stopAt
    static void FLog(string m) { followLast = $"{DateTime.Now:HH:mm:ss} {m}"; Log("follow", m); }
    static void FollowNoteDistChange() { fDistReband = true; }

    static async Task FollowLoop()
    {
        while (!cts.IsCancellationRequested)
        {
            try { FollowTick(); }
            catch (Exception ex) { if ((DateTime.Now - fErrAt).TotalSeconds > 30) { fErrAt = DateTime.Now; Log("follow", "tick error: " + ex.GetBaseException().Message); } }
            try { await Task.Delay(250, cts.Token); } catch { }
        }
    }

    static void FollowReset()
    {
        fMode = FMode.Hold; fAim = null; fHist.Clear(); fBackoffs = 0; fTightLogged = false; fLeaderAnchor = null; fLastD = -1;
        try { fBusyCts.Cancel(); } catch { }
        fBusyCts = new CancellationTokenSource();
    }

    static bool NearBlocked(NavGrid g, Vector2 p, float r)
    {
        var (i0, j0) = g.IJ(p.X - r, p.Y - r); var (i1, j1) = g.IJ(p.X + r, p.Y + r);
        for (int j = j0; j <= j1; j++) for (int i = i0; i <= i1; i++)
            if (g.At(i, j) == 1 && Vector2.Distance(g.XY(i, j), p) <= r) return true;
        return false;
    }

    static void FollowTick()
    {
        var id = followId;
        if (id != fLastId) { fLastId = id; FollowReset(); if (id != UUID.Zero) fLastArriveAt = DateTime.Now; }   // a new follow may settle her once to the standoff
        if (id == UUID.Zero || !LoggedIn || followBusy || client.Self.SittingOn != 0) return;
        var sim = client.Network.CurrentSim; if (sim == null) return;
        // mid-crossing (CrossedRegion seen, region not switched yet) positions mix two frames: wait (Neighbors.cs)
        if (crossing.State == CrossingWatch.Phase.Crossing) return;
        // the leader may be across a border: found in a neighbor region, position in this region's frame (Neighbors.cs);
        // the autopilot takes global coordinates, so she walks over the border after him
        var found = FindAvatarAnySim(id);
        if (found == null) return;
        var lp = found.Value.pos; var me = client.Self.SimPosition; var now = DateTime.Now;
        float d = HDist(lp, me); if (MathF.Abs(lp.Z - me.Z) > 3f) d = Vector3.Distance(lp, me);
        fLastD = d;
        if (fLeaderAnchor == null || HDist(lp, fLeaderAnchor.Value) > 0.4f) { fLeaderAnchor = lp; fLeaderStillSince = now; fBackoffs = 0; fTightLogged = false; }
        double still = (now - fLeaderStillSince).TotalSeconds;
        var g = NavGridFor(sim.Name, me, lp);
        bool indoor = g != null && NearBlocked(g, V2(me), 1.5f); fIndoor = indoor;
        float s = FollowDist; var bands = FollowBands(s, indoor);
        Func<Vector2, bool> ok = null;
        if (g != null)
        {
            var l2 = V2(lp);
            ok = p =>
            {
                if (!g.Contains(p.X, p.Y)) return false;
                var c = g.IJ(p.X, p.Y); if (g.At(c.i, c.j) != 0) return false;
                var dir = p - l2; if (dir.Length() < 0.7f) return false;
                var lq = l2 + Vector2.Normalize(dir) * 0.6f;   // his own cell is often 'blocked' (furniture next to him)
                return Los(g, c, g.IJ(lq.X, lq.Y), allowDoor: true);
            };
        }
        var T = FollowStandoffPoint(V2(me), V2(lp), s, ok); fLastT = T;
        var T3 = new Vector3(T.X, T.Y, me.Z);

        if (fMode == FMode.BackOff)
        {
            if (HDist(me, fBackT) <= 0.5f || (now - fModeSince).TotalSeconds > 4 || d > bands.resumeAt)
            {
                client.Self.AutoPilotCancel(); fMode = FMode.Hold; fAim = null; FaceLeader(lp, me);
                FLog($"backed off: {d:F1} m from {followName}");
            }
            return;
        }
        // back-offs only correct HER overshoot (stopped inside 2 m after her own walk); if he walks up to her she stays
        bool sheClosedIn = (now - fLastArriveAt).TotalSeconds < 8 || fBackoffs > 0;
        bool reband = fDistReband; fDistReband = false;
        var act = FollowDecide(d, fMode == FMode.Pursue, HDist(me, T3), still, s, indoor, sheClosedIn, reband);
        if (act == FollowAct.Pursue)
        {
            if (fMode != FMode.Pursue) { fMode = FMode.Pursue; fModeSince = now; fHist.Clear(); fAim = null; FLog($"{followName} is {d:F1} m away: following to {s:F1} m behind{(indoor ? " (indoors)" : "")}"); }
            var step = NavFollowStep(me, T3) ?? T3;   // NavPlan.cs: around furniture / walls / via doors on a nav grid
            if (SimGeo.NeedsPush(me, step) && !followBusy && (now - fLastPush).TotalSeconds > 8)
            {   // he is across a border and she is at it: the sim's autopilot stops there; walk over with her own controls (Neighbors.cs)
                fLastPush = now; followBusy = true; var tok = fBusyCts.Token;
                _ = Task.Run(async () => { try { await PushAcrossBorder(step, "follow", tok); } catch { } finally { fAim = null; fHist.Clear(); fModeSince = DateTime.Now; followBusy = false; } });
                return;
            }
            FollowAim(step, me, now);
            fHist.Enqueue((now, me));
            while (fHist.Count > 0 && (now - fHist.Peek().t).TotalSeconds > 4.2) fHist.Dequeue();
            if ((now - fModeSince).TotalSeconds >= 4 && fHist.Count >= 12 && (now - fHist.Peek().t).TotalSeconds >= 3.8 && HDist(fHist.Peek().p, me) < 0.3f && d > bands.stopAt)
            { fHist.Clear(); FollowStalled(HDist(me, step) > 1f ? step : lp, me); }
        }
        else if (act == FollowAct.Arrive)
        {
            client.Self.AutoPilotCancel(); fMode = FMode.Hold; fAim = null; FaceLeader(lp, me); fLastArriveAt = now;
            FLog($"stopped {d:F1} m from {followName} (standoff {s:F1} m{(indoor ? ", indoors" : "")})");
        }
        else if (act == FollowAct.BackOff)
        {
            if (fBackoffs >= 3) { if (!fTightLogged) { fTightLogged = true; FLog($"{followName} is {d:F1} m away; 3 back-offs did not hold {s:F1} m here (tight spot): staying put until he moves"); } return; }
            if ((now - fLastBackoff).TotalSeconds < 4) return;
            if (HDist(T3, me) < 0.4f) return;
            fBackoffs++; fLastBackoff = now; fBackT = T3; fMode = FMode.BackOff; fModeSince = now;
            AutoPilotTo(T3);
            FLog($"{followName} is standing still only {d:F1} m away: backing off to {s:F1} m ({P2(T)})");
        }
    }

    static DateTime fLastPush = DateTime.MinValue;
    static void FaceLeader(Vector3 lp, Vector3 me) { try { if (HDist(lp, me) > 0.3f) client.Self.Movement.TurnToward(new Vector3(lp.X, lp.Y, me.Z)); } catch { } }

    static void FollowAim(Vector3 step, Vector3 me, DateTime now)
    {
        double since = (now - fAimAt).TotalSeconds;
        bool send = fAim == null || since >= 1.5 || (since >= 0.5 && HDist(step, fAim.Value) > 0.4f);
        if (!send) return;
        AutoPilotTo(step); fAim = step; fAimAt = now;
        if (HDist(me, step) > 0.5f) client.Self.Movement.TurnToward(new Vector3(step.X, step.Y, me.Z));   // keep BodyRotation in step with the autopilot
    }

    // pursuing but not moving: door sequence if a door/gate is near, else a short stuck-escape
    static void FollowStalled(Vector3 goal, Vector3 me)
    {
        var now = DateTime.Now; var ct = fBusyCts.Token;
        bool tryDoor = (now - fLastDoorRun).TotalSeconds >= 20;
        if (!tryDoor && (now - fLastEscape).TotalSeconds < 6) return;
        followBusy = true; client.Self.AutoPilotCancel();
        _ = Task.Run(async () =>
        {
            try
            {
                string r = null;
                if (tryDoor)
                {
                    fLastDoorRun = DateTime.Now;
                    using var to = CancellationTokenSource.CreateLinkedTokenSource(ct); to.CancelAfter(TimeSpan.FromSeconds(75));
                    r = await DoorUnstick(null, goal, 4f, "follow", to.Token);
                    if (r != null) FLog("stuck while following: " + r);
                }
                if (r == null || r.Contains("FAILED"))
                {
                    fLastEscape = DateTime.Now;
                    await StuckEscape(goal, "follow", ct);
                }
            }
            catch (OperationCanceledException) { client.Self.AutoPilotCancel(); }
            catch (Exception ex) { Log("follow", "stuck handling error: " + ex.GetBaseException().Message); }
            finally { fAim = null; fHist.Clear(); fModeSince = DateTime.Now; followBusy = false; }
        });
    }

    // back off 1 m from the goal direction, then sidestep 1.2 m (alternating sides); walking only
    static async Task StuckEscape(Vector3 goal, string why, CancellationToken ct)
    {
        var me = client.Self.SimPosition;
        var dir = new Vector3(goal.X - me.X, goal.Y - me.Y, 0); dir = dir.Length() < 0.01f ? Vector3.UnitX : Vector3.Normalize(dir);
        var side = new Vector3(-dir.Y, dir.X, 0) * fEscapeSide; fEscapeSide = -fEscapeSide;
        var back = me - dir * 1.0f; var sidep = back + side * 1.2f + dir * 0.5f;
        Log("follow", $"[{why}] stuck-escape at {V(me)}: back 1 m to {V(back)}, sidestep to {V(sidep)}");
        foreach (var p in new[] { back, sidep })
        {
            AutoPilotTo(new Vector3(p.X, p.Y, me.Z));
            for (int i = 0; i < 8 && HDist(client.Self.SimPosition, p) > 0.4f; i++) await Task.Delay(300, ct);
        }
        client.Self.AutoPilotCancel();
        Log("follow", $"[{why}] stuck-escape done at {V(client.Self.SimPosition)} (moved {HDist(client.Self.SimPosition, me):F1} m)");
    }

    // 'follow' command: follow <name> [m] | follow off | follow dist [m] | follow status | follow selftest
    static string FollowCmd(string rest)
    {
        var a = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (rest.Length == 0 || rest.Equals("off", StringComparison.OrdinalIgnoreCase))
        { AutoFollowOnFollowOff("explicit 'follow off'"); followId = UUID.Zero; followDistThis = null; client.Self.AutoPilotCancel(); return "follow off"; }
        var sub = a[0].ToLowerInvariant();
        if (sub == "selftest" && a.Length == 1) return FollowSelfTest();
        if (sub is "dist" or "distance" && a.Length <= 2)
        {
            if (a.Length == 2) { if (!F(a[1], out var v) || v <= 0) return "usage: follow dist <metres 1-8>"; FollowDistDefault = v; FollowNoteDistChange(); FLog($"default follow distance set to {FollowDistDefault:F1} m"); }
            var (st, rs, tc) = FollowBands(FollowDistDefault, false);
            return $"follow distance {FollowDistDefault:F1} m (default, persisted): stops at <= {st:F1} m, resumes at > {rs:F1} m, backs off when he stands still and she is < {tc:F1} m";
        }
        if (sub == "status" && a.Length == 1)
            return $"follow {(followId == UUID.Zero ? "off" : $"{followName}{(afEngaged ? " (auto)" : "")}")}; distance {FollowDist:F1} m{(followDistThis != null ? " (this follow)" : " (default)")}; " +
                   $"mode {fMode}{(followBusy ? " (door/stuck handling)" : "")}{(fLastD >= 0 ? $", {fLastD:F1} m from him" : "")}{(fIndoor ? ", indoors" : "")}; last: {followLast}; doors: {doorLast}";
        float? dist = null; var name = rest;
        if (a.Length >= 2 && F(a[^1], out var dv)) { dist = ClampFollowDist(dv); name = string.Join(' ', a[..^1]); }
        var av = Avatars().FirstOrDefault(t => t.av.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || t.av.Name.Equals(name + " Resident", StringComparison.OrdinalIgnoreCase));
        if (av.av == null) return $"'{name}' is not in view (must be in the same region and within draw distance)";
        if (client.Self.SittingOn != 0) client.Self.Stand();
        followDistThis = dist;
        if (dist != null) FollowNoteDistChange();
        followId = av.av.ID; followName = av.av.Name;
        return $"following {followName} ({av.dist:F1} m away), keeping {FollowDist:F1} m behind{(dist != null ? " (this follow; 'follow dist <m>' sets the default)" : "")}";
    }

    internal static string FollowSelfTest()
    {
        var sb = new StringBuilder(); int pass = 0, fail = 0; void C(bool ok, string w) { if (ok) pass++; else fail++; sb.AppendLine($"{(ok ? "PASS" : "FAIL")} {w}"); }
        const float S = 2.5f;
        C(FollowDecide(6f, false, 3.5f, 0, S, false) == FollowAct.Pursue, "6 m away, holding -> pursue");
        C(FollowDecide(3.3f, false, 0.8f, 0, S, false) == FollowAct.Hold, "3.3 m away, holding -> hold (resume only > 3.5 m)");
        C(FollowDecide(3.6f, false, 1.1f, 0, S, false) == FollowAct.Pursue, "3.6 m -> resume");
        // Dist lowered while holding (e.g. 1.6 m with new s=1.0: stop 1.4, resume 2.0): close in immediately
        C(FollowDecide(1.6f, false, 0.6f, 0, 1.0f, false, true, reband: true) == FollowAct.Pursue, "reband: 1.6 m with s=1.0 while holding -> pursue");
        C(FollowDecide(1.6f, false, 0.6f, 0, 1.0f, false, true, reband: false) == FollowAct.Hold, "without reband: 1.6 m with s=1.0 stays hold (< resume 2.0)");
        C(FollowDecide(1.2f, false, 0.2f, 0, 1.0f, false, true, reband: true) == FollowAct.Hold, "reband: already inside new stop band -> hold");
        C(FollowDecide(3.2f, true, 0.7f, 0, S, false) == FollowAct.Pursue, "pursuing at 3.2 m -> keep walking");
        C(FollowDecide(2.85f, true, 0.4f, 0, S, false) == FollowAct.Arrive, "pursuing reaches 2.85 m -> stop (never aims at him)");
        C(FollowDecide(4f, true, 0.4f, 0, S, false) == FollowAct.Arrive, "at the standoff point -> stop");
        C(FollowDecide(2.95f, true, 0.6f, 0, S, true) == FollowAct.Arrive, "indoors stops earlier (<= 3.0 m)");
        C(FollowDecide(3.6f, false, 1.1f, 0, S, true) == FollowAct.Hold && FollowDecide(3.8f, false, 1.3f, 0, S, true) == FollowAct.Pursue, "indoors resumes only > 3.7 m");
        C(FollowDecide(1.4f, false, 1.1f, 3, S, false) == FollowAct.BackOff, "he stands still 3 s, she is 1.4 m away -> back off");
        C(FollowDecide(1.4f, false, 1.1f, 0.5, S, false) == FollowAct.Hold, "too close but he is moving -> hold (no back-off dance)");
        C(FollowDecide(2.2f, false, 0.3f, 10, S, false) == FollowAct.Hold, "2.2 m, he is still -> hold (2-3 m band)");
        C(FollowDecide(1.4f, false, 1.1f, 3, S, false, sheClosedIn: false) == FollowAct.Hold, "he walked up to her (she did not close in) -> she stays, no back-off");
        var tp = FollowStandoffPoint(new Vector2(10, 0), Vector2.Zero, S, null);
        C(MathF.Abs(tp.X - 2.5f) < 0.01f && MathF.Abs(tp.Y) < 0.01f, $"standoff point 2.5 m from him on her side ({P2(tp)})");
        var tp2 = FollowStandoffPoint(new Vector2(10, 0), Vector2.Zero, S, p => p.Y > 0.5f);   // her side blocked (sofa): moved around him
        C(tp2.Y > 0.5f && MathF.Abs(tp2.Length() - S) < 0.45f, $"blocked side -> point moved around him at ~2.5 m ({P2(tp2)})");
        var tp3 = FollowStandoffPoint(new Vector2(10, 0), Vector2.Zero, S, p => p.Length() > 2.7f);
        C(MathF.Abs(tp3.Length() - 2.9f) < 0.01f, $"tight radius -> tries 2.9 m ({P2(tp3)})");
        C(ClampFollowDist(0.2f) == FollowDistMin && ClampFollowDist(20f) == FollowDistMax && ClampFollowDist(float.NaN) == FollowDistStd, "follow dist clamped 1..8 m");
        var (st, rs, tc) = FollowBands(S, false);
        C(st <= 3.0f && st >= 2.5f && rs > 3.4f && rs < 3.6f && tc >= 1.9f && tc <= 2.1f, $"bands for 2.5 m: stop {st:F1} / resume {rs:F1} / too close {tc:F1}");
        // simulated approach: she walks straight at 3.2 m/s toward a still leader, 250 ms ticks, 0.4 m slide after cancel
        float pos = 12f, v = 3.2f; bool pursuing = false; float settled = -1;
        for (int i = 0; i < 80; i++)
        {
            var act = FollowDecide(pos, pursuing, MathF.Abs(pos - S), 10, S, false);
            if (act == FollowAct.Pursue) { pursuing = true; pos -= MathF.Min(v * 0.25f, MathF.Max(0, pos - S)); }
            else if (act == FollowAct.Arrive) { pursuing = false; pos -= MathF.Min(0.4f, MathF.Max(0, pos - S)); settled = pos; break; }
        }
        C(settled >= 2.0f && settled <= 3.0f, $"simulated approach settles at {settled:F2} m (2-3 m, autopilot aimed at the standoff point)");
        return $"follow selftest: {pass} PASS, {fail} FAIL\n" + sb.ToString().TrimEnd();
    }
}
