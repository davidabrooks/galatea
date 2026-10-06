// NavWalk (added 2026-09-25): walking navigation for Galatay.
// - walk_path x,y,z;x,y,z;...  walk waypoint legs (autopilot, walking, never teleport)
// - goto_avatar <name>          walk to ~1.5 m from an avatar (straight legs of <= 8 m with stuck recovery)
// - sit_near <avatar>           goto_avatar if needed, then sit on the nearest UNOCCUPIED pillow/cushion/seat within 3 m of them
// - walk_status | walk_stop     progress / cancel;  map [radius] [x y] | terrain <x> <y>   planning data
// Position is checked every 0.5 s; < 0.25 m progress in 3 s = stuck -> (door/gate within 3 m: the door sequence, Doors.cs,
// once per leg) back off 1.5 m, then side offsets +-2 m / +-4 m;
// a short logged fly hop only as a last resort, and only with 'nofly off' or '--fly' for that walk (2026-10-04, David: no
// flying on the walking test; NavPlan.cs). Every leg is logged as [walk] with local (PT) timestamps.
using System.Globalization;
using System.Text;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    static CancellationTokenSource walkCts;
    static int trackTick;
    static Task walkTask;
    static string walkState = "idle";

    static string V(Vector3 v) => string.Format(CultureInfo.InvariantCulture, "<{0:F1},{1:F1},{2:F1}>", v.X, v.Y, v.Z);
    static float HDist(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Y - b.Y).Length();

    static void AutoPilotTo(Vector3 p)
    {
        Utils.LongToUInts(client.Network.CurrentSim.Handle, out var rx, out var ry);
        client.Self.AutoPilot(p.X + (double)rx, p.Y + (double)ry, p.Z);
        if (xline.Active && client.Network.CurrentSim?.Name == xline.To) XMark("autopilot_reissued");   // Crossing.cs timeline
    }

    static float? Ground(float x, float y)
    {
        var sim = client.Network.CurrentSim;
        if (sim != null && x >= 0 && y >= 0 && x < 256 && y < 256 && sim.TerrainHeightAtPoint((int)x, (int)y, out var h)) return h;
        return null;
    }

    static async Task EnsureStandingForWalk(CancellationToken ct)
    {
        if (client.Self.SittingOn != 0)
        {
            Interlocked.Increment(ref sitGen);
            client.Self.Stand();
            Log("walk", "standing up to walk (AO is re-worn by the seat-off rule)");
            for (int i = 0; i < 12 && client.Self.SittingOn != 0; i++) await Task.Delay(250, ct);
            await Task.Delay(1500, ct);
        }
        if (client.Self.Movement.AlwaysRun) client.Self.Movement.AlwaysRun = false;
        if (client.Self.Movement.Fly) { client.Self.Movement.Fly = false; client.Self.Movement.SendUpdate(true); }
    }

    // one leg with stuck recovery; true = arrived within tol (horizontal)
    // navMode (NavPlan.cs): give up after 2 recoveries so the planner re-plans instead of side-stepping into walls
    static async Task<bool> WalkLeg(Vector3 target, float tol, string label, CancellationToken ct, bool navMode = false)
    {
        var start = client.Self.SimPosition;
        Log("walk", $"leg {label}: {V(start)} -> {V(target)} ({HDist(start, target):F1} m)");
        int recoveries = 0; bool flew = false, doorTried = false, unfrozeTried = false; Vector3? firstStuckAt = null;
        var legStart = DateTime.Now; var legT0 = DateTime.Now; var lastPush = DateTime.MinValue;
        Vector3 goal = target;
        AutoPilotTo(goal);
        var hist = new Queue<(DateTime t, Vector3 p)>();
        ulong frame = client.Network.CurrentSim?.Handle ?? 0;   // target/goal are local to this region (Neighbors.cs)
        if (!SimGeo.InRegion(target)) Log("walk", $"leg {label}: target is across the {SimGeo.ExitBorder(start, target) ?? SimGeo.Dir(frame, SimGeo.HandleAt(frame, target))} border ({V(target)} in {client.Network.CurrentSim?.Name}'s frame)");
        while (true)
        {
            // Crossing.cs (fast): wake on a region change, and don't wait at all when it already happened (a border push
            // returns right after the new region took her: live 10:04:43, the autopilot came 0.5 s late)
            if (!FastCrossing) await Task.Delay(500, ct);
            else if ((client.Network.CurrentSim?.Handle ?? 0) == frame) await Task.WhenAny(Task.Delay(500, ct), RegionChangedSignal);
            ct.ThrowIfCancellationRequested();
            // border crossing (Neighbors.cs): wait while the region hands her over, then re-express target/goal in the new
            // region's frame and re-issue the autopilot (the sim-side autopilot does not survive the hand-over)
            if (crossing.State == CrossingWatch.Phase.Crossing) { hist.Clear(); legStart = legStart.AddSeconds(0.5); walkState = $"leg {label}: crossing a region border"; continue; }
            var curH = client.Network.CurrentSim?.Handle ?? 0;
            if (curH != 0 && frame != 0 && curH != frame)
            {
                if (!SimGeo.Adjacent(frame, curH)) { client.Self.AutoPilotCancel(); Log("walk", $"leg {label}: region changed to non-adjacent {client.Network.CurrentSim?.Name} (teleport?): stopped"); return false; }
                var shift = SimGeo.Offset(curH, frame);
                target += shift; goal += shift; frame = curH; hist.Clear(); legStart = DateTime.Now.AddSeconds(-10);
                Log("walk", $"leg {label}: crossed into {client.Network.CurrentSim?.Name} at {V(client.Self.SimPosition)}; target now {V(target)}");
                await WaitHandedOver(ct);   // Crossing.cs: fast = until the new region has her (was a fixed 1 s)
                AutoPilotTo(goal);
                continue;
            }
            var p = client.Self.SimPosition;
            if (SimGeo.NeedsPush(p, target) && crossing.State == CrossingWatch.Phase.Idle && (DateTime.Now - lastPush).TotalSeconds > 8)
            {   // the sim's autopilot stops at the border: cross it with her own controls (Neighbors.cs)
                lastPush = DateTime.Now; hist.Clear();
                await PushAcrossBorder(target, $"leg {label}", ct);
                continue;   // the frame shift above re-aims the autopilot in the new region
            }
            KeepGrounded();
            if (navTrack && (++trackTick % 3) == 0) Log("navpos", V(p));
            walkState = $"leg {label}: at {V(p)}, {HDist(p, target):F1} m to go";
            if (!AoStateNow().active) { await Task.Delay(1000, ct); if (!AoStateNow().active) { client.Self.AutoPilotCancel(); AoLog($"walk leg {label}: AO DROPPED mid-walk at {V(p)} ({AoStateNow().why}): stopped"); if (!await AoRestore("dropped mid-walk (walk_path)", ct)) { Log("walk", $"leg {label}: stopped, AO not active; staying put"); return false; } AutoPilotTo(goal); hist.Clear(); legStart = legStart.AddSeconds(30); continue; } }
            if (HDist(p, target) <= tol)
            {
                client.Self.AutoPilotCancel();
                if (flew) { client.Self.Movement.Fly = false; client.Self.Movement.SendUpdate(true); Log("walk", "fly off (landed)"); }
                Log("walk", $"leg {label}: arrived at {V(p)} in {(DateTime.Now - legT0).TotalSeconds:F0} s{(recoveries > 0 ? $" after {recoveries} recoveries" : "")}");
                return true;
            }
            if (goal != target && HDist(p, goal) <= 0.8f) { goal = target; AutoPilotTo(goal); hist.Clear(); continue; } // detour point reached -> resume
            hist.Enqueue((DateTime.Now, p));
            while (hist.Count > 0 && (DateTime.Now - hist.Peek().t).TotalSeconds > 3.0) hist.Dequeue();
            bool stuck = hist.Count >= 5 && (DateTime.Now - hist.Peek().t).TotalSeconds >= 2.4 && Vector3.Distance(hist.Peek().p, p) < 0.25f;
            if ((DateTime.Now - legStart).TotalSeconds > 90) { client.Self.AutoPilotCancel(); Log("walk", $"leg {label}: timeout at {V(p)}"); return false; }
            if (!stuck) continue;
            hist.Clear();
            if (!doorTried && !flew)
            {   // 2026-10-05: stuck near a door/gate -> the door sequence first (Doors.cs DoorUnstick), once per leg
                doorTried = true; client.Self.AutoPilotCancel();
                var dr = await DoorUnstick(null, target, 3f, $"walk {label}", ct);
                if (dr != null)
                {
                    Log("walk", $"leg {label}: stuck at a door -> {dr}");
                    legStart = DateTime.Now; goal = target; AutoPilotTo(goal); hist.Clear();
                    if (!dr.Contains("FAILED")) continue;
                }
            }
            // frozen by the region right after a crossing (live 10:11:25 and 10:25:13 at Ahern <12.2,2.2>: no move at all,
            // in any direction, until a teleport or relog): an in-place hop gets the region to take her properly
            if (!unfrozeTried && LooksFrozenAfterCrossing((DateTime.Now - lastCrossingAt).TotalSeconds, firstStuckAt is Vector3 f0 ? Vector3.Distance(f0, p) : float.MaxValue, recoveries))
            {
                unfrozeTried = true; client.Self.AutoPilotCancel();
                Log("walk", $"leg {label}: frozen at {V(p)} since the crossing {(DateTime.Now - lastCrossingAt).TotalSeconds:F0} s ago -> in-place teleport to unfreeze");
                var un = await UnfreezeHop(p);
                Log("walk", $"leg {label}: unfreeze hop: {un}; now at {V(client.Self.SimPosition)}");
                AutoPilotTo(goal); hist.Clear(); legStart = DateTime.Now; continue;
            }
            firstStuckAt ??= p;
            recoveries++;
            var dir = new Vector3(target.X - p.X, target.Y - p.Y, 0); if (dir.Length() < 0.01f) dir = Vector3.UnitX; dir = Vector3.Normalize(dir);
            var side = new Vector3(-dir.Y, dir.X, 0);
            if (navMode && recoveries > 2) { client.Self.AutoPilotCancel(); Log("walk", $"leg {label}: STUCK at {V(p)} after 2 recoveries (nav: re-plan)"); return false; }
            if (recoveries <= 4)
            {
                float off = (recoveries <= 2 ? 2f : 4f) * (recoveries % 2 == 1 ? 1 : -1);
                var back = p - dir * 1.5f;
                Log("walk", $"leg {label}: STUCK at {V(p)} (no progress 3 s) -> back off to {V(back)}, then side offset {off:+0;-0} m");
                client.Self.AutoPilotCancel(); AutoPilotTo(back);
                for (int i = 0; i < 6 && Vector3.Distance(client.Self.SimPosition, back) > 0.6f; i++) await Task.Delay(500, ct);
                var gp = client.Self.SimPosition + side * off + dir * 2f; goal = new Vector3(gp.X, gp.Y, target.Z);
                AutoPilotTo(goal);
                continue;
            }
            if (recoveries == 5 && !flew && !FlyAllowed) { client.Self.AutoPilotCancel(); Log("walk", $"leg {label}: giving up at {V(p)} after {recoveries - 1} recoveries (nofly on: no fly hop)"); return false; }
            if (recoveries == 5 && !flew)
            {
                flew = true;
                Log("walk", $"leg {label}: still stuck at {V(p)} -> brief FLY hop over the obstacle (logged exception to walk-only)");
                client.Self.Movement.Fly = true; client.Self.Movement.SendUpdate(true);
                goal = new Vector3(p.X + dir.X * 3f, p.Y + dir.Y * 3f, Math.Max(p.Z, target.Z) + 2.5f);
                AutoPilotTo(goal);
                await Task.Delay(3000, ct);
                client.Self.Movement.Fly = false; client.Self.Movement.SendUpdate(true);
                Log("walk", $"fly off at {V(client.Self.SimPosition)}");
                goal = target; AutoPilotTo(goal);
                continue;
            }
            client.Self.AutoPilotCancel();
            Log("walk", $"leg {label}: giving up at {V(p)} after {recoveries} recoveries");
            return false;
        }
    }

    static async Task<bool> WalkPath(List<Vector3> pts, CancellationToken ct, float lastTol = 1.0f)
    {
        await EnsureStandingForWalk(ct);
        var aoRefuse = await AoGuardBeforeWalk("walk_path", ct);
        if (aoRefuse != null) { Log("walk", "REFUSED - " + aoRefuse); walkState = "refused: " + aoRefuse; return false; }
        pts = pts.ToList();   // waypoints are local to the region she starts in; re-expressed after a border crossing
        ulong pf = client.Network.CurrentSim?.Handle ?? 0;
        for (int i = 0; i < pts.Count; i++)
        {
            // split long legs into <= 8 m pieces so the autopilot can't wander far off a bad line
            var from = client.Self.SimPosition; var to = pts[i];
            int n = Math.Max(1, (int)Math.Ceiling(HDist(from, to) / 8f));
            for (int k = 1; k <= n; k++)
            {
                var ch = client.Network.CurrentSim?.Handle ?? 0;
                if (ch != 0 && pf != 0 && ch != pf && SimGeo.Adjacent(pf, ch))
                {   // Neighbors.cs: a leg crossed a border: move every remaining point into the new region's frame
                    var sh = SimGeo.Offset(ch, pf);
                    for (int j = 0; j < pts.Count; j++) pts[j] += sh;
                    from = client.Self.SimPosition; to += sh; pf = ch;
                }
                var sub = from + (to - from) * (k / (float)n);
                bool last = i == pts.Count - 1 && k == n;
                // Proactive door open on nav grids (same as route walk / nav): touch closed leaves, go through fast
                var ng = NavGridFor(client.Network.CurrentSim?.Name, from, sub);
                if (ng != null)
                {
                    var doors = DoorsForCrossing(ng, V2(from), V2(sub));
                    if (doors.Count > 0) await EnsureDoorsOpen(doors, ct);
                }
                if (!await WalkLeg(sub, last ? lastTol : 1.0f, $"{i + 1}/{pts.Count}{(n > 1 ? $".{k}" : "")}", ct)) return false;
                from = client.Self.SimPosition;
            }
        }
        return true;
    }

    static Avatar FindAvatar(string who)
    {
        var sim = client.Network.CurrentSim; who = who.Trim().Trim('"');
        UUID.TryParse(who, out var id);
        return sim?.ObjectsAvatars.Values.FirstOrDefault(a => a != null && (a.ID == id || string.Equals(a.Name, who, StringComparison.OrdinalIgnoreCase)
            || (a.Name ?? "").StartsWith(who + " ", StringComparison.OrdinalIgnoreCase)));
    }

    // an avatar by name/uuid in her region or (Neighbors.cs) a connected neighbor: position in the current region's frame
    static (Avatar av, Vector3 pos)? FindAvatarPos(string who)
    {
        var sim = client.Network.CurrentSim; var a = FindAvatar(who);
        if (a != null && sim != null && (a.ParentID == 0 || sim.ObjectsPrimitives.ContainsKey(a.ParentID))) return (a, PositionHelper.GetAvatarPosition(sim, a));
        who = who.Trim().Trim('"'); UUID.TryParse(who, out var id);
        foreach (var (s, off) in ViewSims().Skip(1))
        {
            var b = s.ObjectsAvatars.Values.FirstOrDefault(x => x != null && (x.ID == id || string.Equals(x.Name, who, StringComparison.OrdinalIgnoreCase) || (x.Name ?? "").StartsWith(who + " ", StringComparison.OrdinalIgnoreCase)));
            if (b != null && (b.ParentID == 0 || s.ObjectsPrimitives.ContainsKey(b.ParentID))) return (b, PositionHelper.GetAvatarPosition(s, b) + off);
        }
        return a != null && sim != null ? (a, PositionHelper.GetAvatarPosition(sim, a)) : null;
    }

    static string StartWalk(string what, Func<CancellationToken, Task<string>> job, bool? fly = null)
    {
        if (walkTask != null && !walkTask.IsCompleted) return "a walk is already running (walk_stop first)";
        walkCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = walkCts.Token;
        walkState = what + ": starting";
        walkTask = Task.Run(async () =>
        {
            walkFlyOverride = fly;
            try { var r = await job(ct); walkState = $"done: {r}"; Log("walk", $"{what} finished: {r}"); }
            catch (OperationCanceledException) { client.Self.AutoPilotCancel(); walkState = "cancelled"; Log("walk", $"{what} cancelled"); }
            catch (Exception ex) { client.Self.AutoPilotCancel(); walkState = "error: " + ex.GetBaseException().Message; Log("walk", $"{what} error: {ex.GetBaseException().Message}"); }
            finally { walkFlyOverride = null; }
        });
        return $"{what} started (watch [walk] lines; 'walk_status' / 'walk_stop')";
    }

    static readonly string[] SeatWords = { "pillow", "cushion", "zafu", "zabuton", "seat", "mat", "bench", "chair", "stool", "pouf" };

    static async Task<(Primitive p, float d)?> FindFreeSeatNear(Vector3 at, float radius)
    {
        var sim = Sim; var sit = Sitters(sim);
        var cands = sim.ObjectsPrimitives.Values.Where(p => p != null && p.ParentID == 0 && p.PrimData.PCode == PCode.Prim && HDist(p.Position, at) <= radius && Math.Abs(p.Position.Z - at.Z) < 2.5f).ToList();
        await EnsureProperties(sim, cands);
        var free = cands.Where(p => !sit.ContainsKey(p.LocalID) && SeatWords.Any(w => (p.Properties?.Name ?? "").Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Select(p => (p, d: HDist(p.Position, at))).OrderBy(t => t.d).ToList();
        return free.Count > 0 ? free[0] : null;
    }

    static async Task<string> NavCmds(string cmd, string rest, string[] a)
    {
        bool? flyFlag = null;
        if (cmd is "walk_path" or "goto_avatar" or "sit_near" or "walk_to") { rest = TakeFlyFlag(rest, out flyFlag); a = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries); }
        switch (cmd)
        {
            case "walk_status": return walkState;
            case "walk_stop": if (WanderOn) StopWander("walk_stop"); walkCts?.Cancel(); client.Self.AutoPilotCancel(); return "stop sent";
            case "terrain":
                if (a.Length == 2 && F(a[0], out var tx) && F(a[1], out var ty)) { var g = Ground(tx, ty); return g.HasValue ? $"ground at {tx},{ty}: {g.Value:F2}" : "no terrain data for that point"; }
                return "usage: terrain <x> <y>";
            case "map":
            {
                float r = 12; var me = client.Self.SimPosition; var c = me;
                if (a.Length >= 1 && F(a[0], out var rr)) r = rr;
                if (a.Length >= 3 && F(a[1], out var cx) && F(a[2], out var cy)) c = new Vector3(cx, cy, me.Z);
                var sim = Sim;
                var ps = sim.ObjectsPrimitives.Values.Where(p => p != null && p.ParentID == 0 && p.PrimData.PCode == PCode.Prim && HDist(p.Position, c) <= r).OrderBy(p => HDist(p.Position, c)).Take(250).ToList();
                await EnsureProperties(sim, ps);
                var sb = new StringBuilder($"root objects within {r} m of {V(c)} (ground there {Ground(c.X, c.Y)?.ToString("F1") ?? "?"}):\n");
                foreach (var p in ps)
                {
                    p.Rotation.GetEulerAngles(out var rx, out var ry, out var rz);
                    sb.AppendLine($"  {V(p.Position)} size {p.Scale.X:F1}x{p.Scale.Y:F1}x{p.Scale.Z:F1} rotZ {rz * 180 / MathF.PI:F0} {(((p.Flags & PrimFlags.Phantom) != 0) ? "PHANTOM " : "")}'{p.Properties?.Name}' {p.ID}");
                }
                return sb.ToString();
            }
            case "walk_to":
            {   // walk_to [<region>] <x> <y> [z]: teleport-free walk, also into a neighbor region (Neighbors.cs)
                var (tgt, err) = ParseWalkTo(a);
                if (err != null) return err;
                var t = tgt.Value; var me0 = client.Self.SimPosition;
                return StartWalk($"walk_to {V(t)}{(SimGeo.InRegion(t) ? "" : $" (across the {SimGeo.ExitBorder(me0, t)} border)")} {HDist(me0, t):F0} m",
                    async ct => await WalkPath(new() { t }, ct) ? $"arrived at {V(client.Self.SimPosition)} in {client.Network.CurrentSim?.Name}" : $"stopped at {V(client.Self.SimPosition)} in {client.Network.CurrentSim?.Name}", flyFlag);
            }
            case "walk_path":
            {
                var pts = new List<Vector3>();
                foreach (var seg in rest.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var f = seg.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length < 2 || !F(f[0], out var x) || !F(f[1], out var y)) return $"bad waypoint '{seg}' (use x,y,z;x,y,z)";
                    float z = f.Length > 2 && F(f[2], out var zz) ? zz : (GroundAny(x, y) ?? client.Self.SimPosition.Z);   // x/y may be across a border (Neighbors.cs)
                    pts.Add(new Vector3(x, y, z));
                }
                if (pts.Count == 0) return "usage: walk_path x,y,z;x,y,z;...";
                return StartWalk($"walk_path ({pts.Count} waypoints)", async ct => await WalkPath(pts, ct) ? $"arrived at {V(client.Self.SimPosition)}" : $"stopped at {V(client.Self.SimPosition)}", flyFlag);
            }
            case "goto_avatar":
            case "sit_near":
            {
                if (rest.Length == 0) return $"usage: {cmd} <avatar name|uuid>";
                var av0 = FindAvatarPos(rest); if (av0 == null) return $"avatar '{rest}' not in view";
                return StartWalk($"{cmd} {av0.Value.av.Name}", async ct =>
                {
                    // the avatar may be across a border (Neighbors.cs): position in this region's frame; the walk crosses over
                    var hit = FindAvatarPos(rest); if (hit == null) return $"avatar '{rest}' no longer in view";
                    var (av, apos) = hit.Value; var sim = Sim;
                    Vector3 dest = apos;
                    (Primitive p, float d)? seat = null;
                    if (cmd == "sit_near")
                    {
                        seat = await FindFreeSeatNear(apos, 3f);
                        if (seat == null) return $"no unoccupied pillow/cushion/seat within 3 m of {av.Name}";
                        dest = seat.Value.p.Position;
                        Log("walk", $"sit_near: target seat '{seat.Value.p.Properties?.Name}' {seat.Value.p.ID} at {V(dest)}, {seat.Value.d:F1} m from {av.Name}");
                    }
                    if (HDist(client.Self.SimPosition, dest) > 2.0f)
                    {
                        var me = client.Self.SimPosition;
                        var dir = new Vector3(dest.X - me.X, dest.Y - me.Y, 0); dir = dir.Length() > 0.01f ? Vector3.Normalize(dir) : Vector3.UnitX;
                        var sp0 = dest - dir * 1.2f; var stop = new Vector3(sp0.X, sp0.Y, dest.Z);
                        var ng = NavGridFor(sim.Name, me, dest);   // NavPlan.cs: around walls / through doors when a grid covers both
                        if (ng != null && !await NavWalkTo(ng, V2(dest), 1.4f, ct)) return $"could not walk to {av.Name} on nav grid '{ng.Name}' (stopped at {V(client.Self.SimPosition)})";
                        if (ng == null && !await WalkPath(new() { stop }, ct, 1.2f)) return $"could not walk to {av.Name} (stopped at {V(client.Self.SimPosition)})";
                    }
                    if (cmd == "goto_avatar") return $"next to {av.Name}: {HDist(client.Self.SimPosition, FindAvatarPos(rest)?.pos ?? apos):F1} m{(client.Network.CurrentSim != sim ? $" (now in {client.Network.CurrentSim?.Name})" : "")}";
                    var sit = Sitters(sim);
                    if (sit.ContainsKey(seat.Value.p.LocalID)) { seat = await FindFreeSeatNear(apos, 3f); if (seat == null) return "seat got taken and no other free seat within 3 m"; }
                    var r = await Exec($"sit {seat.Value.p.ID}");
                    await Task.Delay(1500);
                    var aoWorn = SeatOffItems().Keys.Any(k => WornByItem().ContainsKey(k));
                    return $"{r}; seat {seat.Value.p.ID}; distance to {av.Name} {Vector3.Distance(client.Self.SimPosition, FindAvatarPos(rest)?.pos ?? apos):F1} m; seat-off AO worn now: {(aoWorn ? "yes (loop will detach)" : "no")}";
                }, flyFlag);
            }
        }
        return "?";
    }
}
