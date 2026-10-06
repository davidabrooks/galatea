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
        lock (gate) { marks.Clear(); T0 = t0; From = from; To = to; Fast = fast; LastMoveOld = lastMoveOld; Active = true; }
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
            return $"timeline {From} -> {To} ({(Fast ? "fast" : "classic")} hand-over), ms after CrossedRegion: {steps}{gap}";
        }
    }
    public void End() { lock (gate) Active = false; }
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
        xline.Mark("sim_changed", now);
        if (client.Self.CrossingCapsReused) xline.Mark("caps_kept", now);
        var old = regionChangedTcs; regionChangedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously); old.TrySetResult(true);
        _ = Task.Run(() => HandoffAsync(to));
    }

    // through the hand-over: AgentUpdate every 100 ms (fast), settle check every 50 ms, then the timeline line
    static async Task HandoffAsync(Simulator to)
    {
        try
        {
            var t0 = DateTime.Now; var lastPump = DateTime.MinValue;
            while ((DateTime.Now - t0).TotalSeconds < 20)
            {
                var now = DateTime.Now;
                if (FastCrossing && !xline.HandoffDone(now) && (now - lastPump).TotalMilliseconds >= 100 && client.Network.CurrentSim == to)
                { lastPump = now; try { client.Self.Movement.SendUpdate(true, to); } catch { } }
                if (crossing.State == CrossingWatch.Phase.Settling)
                {
                    bool self = xline.Has("first_update") || to.ObjectsAvatars.Values.Any(a => a != null && a.ID == client.Self.AgentID);
                    bool hud = false; try { hud = AoHudWorn(); } catch { }
                    var r = crossing.Observe(now, self, hud);
                    if (r != null) { Log("crossing", $"{r}; in {to.Name} at {V(client.Self.SimPosition)}, AO {AoFlag()}, follow {(followId == UUID.Zero ? "-" : followName)}"); if (r.Contains("settled")) XMark("settled"); }
                }
                bool done = crossing.State != CrossingWatch.Phase.Settling && (xline.Has("moving") || xline.LastMoveOld == null || (now - t0).TotalSeconds > 4);
                if (done && (now - t0).TotalSeconds > 0.5) break;
                await Task.Delay(FastCrossing ? 50 : 250);
            }
        }
        catch (Exception ex) { Log("crossing", "hand-over watch error: " + ex.GetBaseException().Message); }
        finally
        {
            if (xline.Active)
            {
                var line = xline.Summary(); xline.End(); Log("crossing", line);
                xlineHistory.Enqueue($"{DateTime.Now:HH:mm:ss} {line}"); while (xlineHistory.Count > 12) xlineHistory.TryDequeue(out _);
            }
        }
    }

    // a walk after a frame change: wait (fast: at most 1.5 s) for the new region to take her, then re-aim at once
    static async Task WaitHandedOver(CancellationToken ct)
    {
        if (!FastCrossing) { await Task.Delay(1000, ct); return; }
        for (int i = 0; i < 75 && xline.Active && !xline.Has("movement_complete") && !xline.Has("first_update"); i++) await Task.Delay(20, ct);
    }

    // `crossing` / `crossing fast on|off` / `crossing log`
    static string CrossingCmd(string[] a)
    {
        var sub = a.Length > 1 ? a[1].ToLowerInvariant() : "";
        if (sub == "fast" && a.Length > 2) { FastCrossing = a[2] is "on" or "1" or "true"; Log("crossing", $"fast hand-over {(FastCrossing ? "ON" : "OFF")}"); }
        var sb = new System.Text.StringBuilder($"crossing: fast hand-over {(FastCrossing ? "ON" : "OFF")} (GT_FAST_CROSSING); state {crossing.State}; {crossing.Crossings} crossings, {crossing.Failures} failures\n");
        foreach (var l in xlineHistory) sb.AppendLine(l);
        return sb.ToString().TrimEnd();
    }
}
