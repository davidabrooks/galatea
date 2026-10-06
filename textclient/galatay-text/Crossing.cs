// Crossing.cs (2026-10-06, David: seamless region crossings)
// One border crossing, step by step (CrossTimeline, logged as "[crossing] timeline ..."): CrossedRegion received (the
// library's stamp, before its own handler runs), CompleteAgentMovement sent, region switched (SimChanged), caps kept or a
// new event queue up, AgentMovementComplete from the new region, her first object update there, the first update that
// shows her moving again, the autopilot re-issued (walk/follow), settled (avatar + AO HUD seen). "movement gap" = last
// update showing her moving in the old region -> first one in the new region.
// Fast hand-over (GT_FAST_CROSSING, default on; `crossing fast on|off` switches it live for before/after runs):
//   library (libremetaverse-multisim.patch): CompleteAgentMovement right away (no blocking UseCircuitCode ack), the child
//   agent's caps + event queue kept when the seed is unchanged, no appearance rebake / outfit re-send on a crossing;
//   here: AgentUpdate every 100 ms through the hand-over (her controls reach the new region at once, not on the 500 ms
//   timer), a border push keeps walking until the new region has her (instead of letting go at the border), the walk
//   re-issues its autopilot the moment AgentMovementComplete arrives (was: next 500 ms poll + a fixed 1 s wait), and the
//   settle check runs every 50 ms (was: the 1 s tick).
using System.Collections.Concurrent;
using LibreMetaverse;
using LibreMetaverse.Packets;

namespace GalatayText;

// ---- pure: one crossing's timeline (selftested) ---------------------------------------------------------------------
public sealed class CrossTimeline
{
    public DateTime T0 { get; private set; }
    public string From { get; private set; } = "";
    public string To { get; private set; } = "";
    public bool Active { get; private set; }
    public bool Fast { get; private set; }
    public DateTime? LastMoveOld { get; private set; }
    readonly List<(string what, DateTime t)> marks = new();
    readonly object gate = new();

    // t0 = CrossedRegion receipt; lastMoveOld = her last moving update in the old region (null: she was standing)
    public void Start(DateTime t0, string from, string to, bool fast, DateTime? lastMoveOld)
    {
        lock (gate) { marks.Clear(); T0 = t0; From = from; To = to; Fast = fast; LastMoveOld = lastMoveOld; StillS = null; Active = true; }
    }
    // first occurrence only; false if inactive or already marked
    public bool Mark(string what, DateTime t)
    {
        lock (gate)
        {
            if (!Active || marks.Exists(m => m.what == what)) return false;
            marks.Add((what, t)); return true;
        }
    }
    public bool Has(string what) { lock (gate) return marks.Exists(m => m.what == what); }
    public double? Ms(string what) { lock (gate) { var i = marks.FindIndex(m => m.what == what); return i < 0 ? null : (marks[i].t - T0).TotalMilliseconds; } }
    // movement gap: her last moving update in the old region -> first moving update in the new one
    public double? MoveGapMs { get { lock (gate) { var i = marks.FindIndex(m => m.what == "moving"); return i < 0 || LastMoveOld == null ? null : (marks[i].t - LastMoveOld.Value).TotalMilliseconds; } } }
    public string Summary()
    {
        lock (gate)
        {
            var steps = string.Join(", ", marks.OrderBy(m => m.t).Select(m => $"{m.what} {((m.t - T0).TotalMilliseconds >= 0 ? "+" : "")}{(m.t - T0).TotalMilliseconds:F0}"));
            var gap = MoveGapMs is double g ? $"; movement gap {g / 1000:F2} s" : LastMoveOld == null ? "; standing (no movement gap)" : "; not moving again yet";
            var still = StillS is double st ? $"; stood still {st:F2} s of the first 3 s" : "";
            return $"timeline {From} -> {To} ({(Fast ? "fast" : "classic")} hand-over), ms after CrossedRegion: {steps}{gap}{still}";
        }
    }
    public void End() { lock (gate) Active = false; }
    // a border push keeps her own controls on into the new region until she has been moving there for 400 ms (at most
    // 1.5 s): the walk's autopilot takes over from a walking avatar, never during the region's arrival (live: autopilot
    // sent ~100 ms after the hand-over left her frozen at Ahern <12.2,2.2> three times in ~15 Morris -> Ahern crossings)
    public bool PushThroughDone(DateTime now)
    {
        var mv = Ms("moving");
        double age = (now - T0).TotalMilliseconds;
        return age >= 1500 || (mv != null && age >= mv.Value + 400);
    }
    // seconds she stood still: her velocity from the region (what the viewer dead-reckons with; a steady walk sends few
    // position updates, so positions alone read as standing) below 0.3 m/s, over samples taken through the hand-over
    public static double StillSeconds(IReadOnlyList<(DateTime t, Vector3 v)> s)
    {
        double still = 0;
        for (int i = 1; i < s.Count; i++)
        {
            double dt = (s[i].t - s[i - 1].t).TotalSeconds; if (dt <= 0) continue;
            var v = s[i - 1].v; if (Math.Sqrt(v.X * v.X + v.Y * v.Y) < 0.3) still += dt;
        }
        return still;
    }
    public double? StillS { get; set; }
    // the AgentUpdate pump through the hand-over: until AgentMovementComplete + 300 ms, at most 2.5 s
    public bool HandoffDone(DateTime now)
    {
        var mc = Ms("movement_complete");
        double age = (now - T0).TotalMilliseconds;
        return age > 2500 || (mc != null && age > mc.Value + 300);
    }
}

public static partial class Program
{
    static readonly CrossTimeline xline = new();
    static readonly ConcurrentQueue<string> xlineHistory = new();
    static DateTime selfLastMoveAt = DateTime.MinValue; static Simulator selfLastMoveSim;
    static TaskCompletionSource<bool> regionChangedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    static bool FastCrossing { get => AgentManager.FastCrossing; set => AgentManager.FastCrossing = value; }
    static DateTime LocalOf(DateTime utc) => utc == default ? DateTime.MinValue : utc.ToLocalTime();

    // pure (selftest): a point in the old region's frame -> the new region's frame
    internal static Vector3 CameraIntoNewFrame(Vector3 p, ulong fromHandle, ulong toHandle) => p + SimGeo.Offset(toHandle, fromHandle);

    static void XMark(string what) { if (xline.Active) xline.Mark(what, DateTime.Now); }

    // completes when the current region changes (walks wake on it instead of their 500 ms poll)
    static Task RegionChangedSignal => regionChangedTcs.Task;

    static void HookCrossingTimeline()
    {
        FastCrossing = Env("GT_FAST_CROSSING", "on") != "off";
        client.Objects.TerseObjectUpdate += (s, e) =>
        {
            if (!e.Update.Avatar || e.Prim == null || e.Prim.ID != client.Self.AgentID) return;
            SelfUpdate(e.Simulator, e.Update.Velocity);
        };
        client.Objects.AvatarUpdate += (s, e) =>
        {
            if (e.Avatar == null || e.Avatar.ID != client.Self.AgentID) return;
            SelfUpdate(e.Simulator, e.Avatar.Velocity);
        };
        client.Network.RegisterCallback(PacketType.AgentMovementComplete, (s, e) =>
        {
            if (xline.Active && e.Simulator == client.Network.CurrentSim && xline.Mark("movement_complete", DateTime.Now)) { }
        });
        client.Network.EventQueueRunning += (s, e) => { if (xline.Active && e.Simulator == client.Network.CurrentSim) XMark("event_queue_up"); };
        client.Self.RegionCrossed += (s, e) => { if (e.NewSimulator != null) XMark("library_completed"); };
    }

    static void SelfUpdate(Simulator sim, Vector3 vel)
    {
        bool moving = new Vector2(vel.X, vel.Y).Length() > 0.5f;
        var cur = client.Network.CurrentSim;
        if (xline.Active && sim == cur && cur?.Name == xline.To)
        {
            xline.Mark("first_update", DateTime.Now);
            if (moving) xline.Mark("moving", DateTime.Now);
        }
        else if (!xline.Active && moving && sim == cur) { selfLastMoveAt = DateTime.Now; selfLastMoveSim = sim; }
    }

    // from the SimChanged handler (Neighbors.cs) on a crossing (not a teleport)
    static void CrossingEntered(Simulator from, Simulator to)
    {
        var now = DateTime.Now;
        var t0 = LocalOf(client.Self.CrossedRegionAtUtc);
        if ((now - t0).TotalSeconds > 10) t0 = now;   // no CrossedRegion stamp (a crossing the library did not start)
        DateTime? lastMove = selfLastMoveSim == from && (t0 - selfLastMoveAt).TotalSeconds < 1.5 ? selfLastMoveAt : null;
        xline.Start(t0, from?.Name ?? "?", to.Name, FastCrossing, lastMove);
        var cam = LocalOf(client.Self.CrossingCamSentAtUtc);
        if ((now - cam).TotalSeconds < 10) xline.Mark("cam_sent", cam);
        xline.Mark("sim_changed", now); lastCrossingAt = now;
        if (client.Self.CrossingCapsReused) xline.Mark("caps_kept", now);
        if (client.Self.CrossingCircuitReused) xline.Mark("circuit_kept", now);
        if (FastCrossing && from != null && from.Handle != 0)
        {   // her camera center is still in the old region's frame (255 m off in the new one until the 1 s camera tick:
            // live 10:07:20, she only moved again right after that re-anchor): move it into the new frame now
            try
            {
                var camr = client.Self.Movement.Camera; var p = CameraIntoNewFrame(camr.Position, from.Handle, to.Handle);
                camr.LookAt(p, p + camr.AtAxis);
                xline.Mark("camera_reframed", DateTime.Now);
            }
            catch { }
        }
        var old = regionChangedTcs; regionChangedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously); old.TrySetResult(true);
        _ = Task.Run(() => HandoffAsync(to));
    }

    // through the hand-over: AgentUpdate every 100 ms (fast), settle check every 50 ms, then the timeline line
    static async Task HandoffAsync(Simulator to)
    {
        var samples = new List<(DateTime t, Vector3 v)>(); var x0 = xline.T0; bool walking = xline.LastMoveOld != null;
        try
        {
            var t0 = DateTime.Now; var lastPump = DateTime.MinValue;
            while ((DateTime.Now - t0).TotalSeconds < 20)
            {
                var now = DateTime.Now;
                if (walking && (now - x0).TotalSeconds <= 3 && client.Network.CurrentSim == to) samples.Add((now, client.Self.Velocity));
                if (FastCrossing && HandoffPump && !xline.HandoffDone(now) && (now - lastPump).TotalMilliseconds >= 100 && client.Network.CurrentSim == to)
                { lastPump = now; try { client.Self.Movement.SendUpdate(true, to); } catch { } }
                if (crossing.State == CrossingWatch.Phase.Settling)
                {
                    bool self = xline.Has("first_update") || to.ObjectsAvatars.Values.Any(a => a != null && a.ID == client.Self.AgentID);
                    bool hud = false; try { hud = AoHudWorn(); } catch { }
                    var r = crossing.Observe(now, self, hud);
                    if (r != null) { Log("crossing", $"{r}; in {to.Name} at {V(client.Self.SimPosition)}, AO {AoFlag()}, follow {(followId == UUID.Zero ? "-" : followName)}"); if (r.Contains("settled")) XMark("settled"); }
                }
                bool done = crossing.State != CrossingWatch.Phase.Settling && (xline.Has("moving") || xline.LastMoveOld == null || (now - t0).TotalSeconds > 4)
                            && (!walking || (now - x0).TotalSeconds > 3);
                if (done && (now - t0).TotalSeconds > 0.5) break;
                await Task.Delay(FastCrossing ? 50 : 250);
            }
        }
        catch (Exception ex) { Log("crossing", "hand-over watch error: " + ex.GetBaseException().Message); }
        finally
        {
            if (xline.Active)
            {
                if (samples.Count > 10) xline.StillS = CrossTimeline.StillSeconds(samples) + Math.Max(0, (samples[0].t - x0).TotalSeconds);
                var line = xline.Summary(); xline.End(); Log("crossing", line);
                xlineHistory.Enqueue($"{DateTime.Now:HH:mm:ss} {line}"); while (xlineHistory.Count > 12) xlineHistory.TryDequeue(out _);
            }
        }
    }

    static DateTime lastCrossingAt = DateTime.MinValue;
    // live 2026-10-06 (~90 crossings at the Ahern/Morris/Dore four-corner): driving her (autopilot or her own controls)
    // within ~1.5 s of a crossing froze her about one Morris -> Ahern crossing in three (arrival at <12.4,2.1,39.8>, below
    // the plaza edge, while the region was still re-placing her; only a teleport or relog freed her). Resuming at
    // >= 1.0 s: 1 of 6 froze; at >= 1.6 s (and classic, ~1.7 s): 0 of 22. So walks/follow hold until 1.6 s after
    // CrossedRegion; the border push lets go at the border (push-through stays as a diagnostic switch, off).
    static int ResumeMinMs = int.TryParse(Env("GT_CROSS_RESUME_MS", "1600"), out var rm) ? rm : 1600;
    internal static bool CrossingSettling(DateTime now, DateTime crossedAt, int minMs) => minMs > 0 && now >= crossedAt && (now - crossedAt).TotalMilliseconds < minMs;
    static bool PushThrough = Env("GT_CROSS_PUSH", "off") == "on", HandoffPump = Env("GT_CROSS_PUMP", "on") != "off";

    // pure: she has not moved at all (< 5 cm) through a full stuck recovery, within 30 s of a crossing
    internal static bool LooksFrozenAfterCrossing(double secsSinceCrossing, float movedSinceFirstStuck, int recoveries)
        => recoveries >= 1 && secsSinceCrossing >= 0 && secsSinceCrossing < 30 && movedSinceFirstStuck < 0.05f;

    // a teleport to where she stands (same region, 0.3 m up): the region re-places her and lets her move again
    static async Task<string> UnfreezeHop(Vector3 p)
    {
        var sim = client.Network.CurrentSim; if (sim == null) return "no region";
        if (RobeMaybeWorn(out var how) && RobeTpBlock(sim.Name, true, false) is string block) return $"skipped ({block}; {how})";
        try { bool ok = await client.Self.TeleportAsync(sim.Name, new Vector3(p.X, p.Y, p.Z + 0.3f)); return ok ? "ok" : "teleport failed"; }
        catch (Exception ex) { return "error " + ex.GetBaseException().Message; }
    }

    // a walk after a frame change: wait (fast: at most 1.5 s) for the new region to take her, then re-aim at once
    static async Task WaitHandedOver(CancellationToken ct)
    {
        if (!FastCrossing) { await Task.Delay(1000, ct); return; }
        // wait for the new region's first update of her (it has placed her): driving her before that froze her (see PlacedBy)
        for (int i = 0; i < 75 && xline.Active && !xline.Has("first_update"); i++) await Task.Delay(20, ct);
        if (ResumeMinMs > 0 && xline.Active) { var wait = ResumeMinMs - (DateTime.Now - xline.T0).TotalMilliseconds; if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait), ct); }
    }

    // `crossing` / `crossing fast on|off` / `crossing log`
    static string CrossingCmd(string[] a)
    {
        if (a.Length > 0 && a[0] is not ("fast" or "push" or "pump" or "lmv" or "resume")) a = a[1..];   // tolerate a leading word ("crossing set fast off")
        var sub = a.Length > 0 ? a[0].ToLowerInvariant() : "";
        if (sub == "lmv" && a.Length > 2)
        {
            bool on = a[2] is "on" or "1" or "true";
            switch (a[1]) { case "circuit": AgentManager.FastCrossingCircuit = on; break; case "caps": AgentManager.FastCrossingCaps = on; break; case "appearance": AgentManager.FastCrossingAppearance = on; break; }
            Log("crossing", $"library fast hand-over part {a[1]} {(on ? "ON" : "OFF")}");
        }
        if (sub == "resume" && a.Length > 1 && int.TryParse(a[1], out var rms)) { ResumeMinMs = rms; Log("crossing", $"walk resumes no sooner than {rms} ms after CrossedRegion"); }
        if (sub == "push" && a.Length > 1) { PushThrough = a[1] is "on" or "1" or "true"; Log("crossing", $"push-through {(PushThrough ? "ON" : "OFF")}"); }
        if (sub == "pump" && a.Length > 1) { HandoffPump = a[1] is "on" or "1" or "true"; Log("crossing", $"hand-over AgentUpdate pump {(HandoffPump ? "ON" : "OFF")}"); }
        if (sub == "fast" && a.Length > 1) { FastCrossing = a[1] is "on" or "1" or "true"; Log("crossing", $"fast hand-over {(FastCrossing ? "ON" : "OFF")}"); }
        var sb = new System.Text.StringBuilder($"crossing: fast hand-over {(FastCrossing ? "ON" : "OFF")} (GT_FAST_CROSSING), push-through {(PushThrough ? "ON" : "OFF")}, pump {(HandoffPump ? "ON" : "OFF")}, resume>={ResumeMinMs} ms, library circuit/caps/appearance {(AgentManager.FastCrossingCircuit ? 1 : 0)}{(AgentManager.FastCrossingCaps ? 1 : 0)}{(AgentManager.FastCrossingAppearance ? 1 : 0)}; state {crossing.State}; {crossing.Crossings} crossings, {crossing.Failures} failures\n");
        var cs = client.Network.CurrentSim;
        if (cs != null)
        {   // which avatar entries in this region are her (stale copies from earlier visits made her read frozen once)
            var mine = cs.ObjectsAvatars.Values.Where(av => av != null && av.ID == client.Self.AgentID).Select(av => $"{av.LocalID}@{V(av.Position)}");
            sb.AppendLine($"self: local id {client.Self.LocalID} at {V(client.Self.SimPosition)}; her avatar entries in {cs.Name}: {string.Join(", ", mine)}");
        }
        foreach (var l in xlineHistory) sb.AppendLine(l);
        return sb.ToString().TrimEnd();
    }
}
