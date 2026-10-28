// LoginJoin.cs (2026-10-21 10:48 David): after every login while David is in my region, walk over to him myself
// (follow engine: routes around furniture; on a leash this replaces being dragged by the collar), and tell the chat
// routine the REAL placement: my spawn distance to him, measured only once my own avatar update and his avatar have
// both arrived (else UNKNOWN), plus how I got next to him. 10:46 bug: the wake fired 15 s after login, after the
// walker/collar had already dragged me 15 m across the room, so "you're right here" looked true to the routine.
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibreMetaverse;
namespace GalatayText;
public static partial class Program
{
    internal sealed record LoginPlacement(float? spawnDist, string how, float? finalDist);
    static volatile LoginPlacement loginPlacement;
    static TaskCompletionSource<bool> loginJoinDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal const float LoginJoinWithin = 2.5f;

    // pure (tested): spawn distance only when BOTH positions really arrived after login (no stale/zero positions)
    internal static float? LoginSpawnDistance(Vector3? me, Vector3? him) =>
        me is Vector3 a && him is Vector3 b && a != Vector3.Zero && b != Vector3.Zero ? Vector3.Distance(a, b) : null;
    // pure (tested): walk to him after login?
    internal static bool LoginShouldJoin(float? spawnDist, bool davidHere, bool sitting) => davidHere && !sitting && spawnDist is float d && d > LoginJoinWithin;
    // pure (tested): the placement line for the david_login wake (the chat routine must not invent "right by your side")
    internal static string LoginPlacementText(LoginPlacement p) =>
        p == null || p.spawnDist == null
            ? " PLACEMENT: my distance to him at login is UNKNOWN (positions not received yet); check 'nearby' and do NOT claim I appeared by his side."
            : $" PLACEMENT: I spawned {p.spawnDist:F1} m from him" +
              (p.how == "walked" ? $" and walked over to him myself (now {p.finalDist?.ToString("F1") ?? "?"} m)" :
               p.how == "spot-walked" ? $" and, once fully loaded in, walked over to him myself and stopped on the floor beside the furniture he is on or next to (now {p.finalDist?.ToString("F1") ?? "?"} m), never onto it" :
               p.how == "no-route" ? " and could not find a clear floor route to him around the furniture, so I stayed where I was (I did NOT walk over)" :
               p.how == "met" ? $" and he was already beside me (now {p.finalDist?.ToString("F1") ?? "?"} m) by the time I was fully loaded in, so I did not walk" :
               p.how == "walk-failed" ? $" and tried to walk over but am still {p.finalDist?.ToString("F1") ?? "?"} m away" : "") +
              (p.spawnDist > LoginJoinWithin ? ". I did NOT appear by his side: say I came over to him, never that I logged in right next to him." : ".");

    internal const double LoginStallS = 3.0;
    // pure (tested): samples (age s, dist m), oldest first: stalled = we have >= LoginStallS of samples and the distance fell
    // by < 0.3 m over the last LoginStallS (2026-10-23 14:57: she ground into the corner for ~35 s instead of giving up)
    internal static bool LoginJoinStalled(System.Collections.Generic.IReadOnlyList<(double ageS, float d)> s)
    {
        if (s.Count == 0 || s[0].ageS < LoginStallS) return false;
        var then = s.Where(x => x.ageS >= LoginStallS).Last().d;
        return then - s[^1].d < 0.3f;
    }

    // ---- 2026-10-27 08:43 (David on the bed, 4.7 m away): the walk started 1 s after login, before my outfit / attachments / the room had
    // loaded, planned on a static nav grid that has no bed (moved Oct 9, grid built Oct 4), and aimed 1.2 m from him = onto the mattress.
    // Now: (a) wait until I am loaded in, (b) never route to or across furniture: use the curated footprints (_graph-Peronaut.json) and the
    // seat's own clear floor spot (stand_spot), else a computed floor spot beside the furniture he is on or next to.
    internal enum LoginLoadVerdict { Wait, Loaded, GaveUp }
    internal const double LoginLoadMinS = 20, LoginLoadStableS = 8, LoginLoadMaxS = 75;
    // pure (tested): loaded = my own appearance arrived, his position resolved (his seat prim is in), I wear something, and the worn-attachment
    // count and the root prims near me have not changed for LoginLoadStableS, no sooner than LoginLoadMinS after login (the library re-sends
    // the outfit ~8-15 s in); after LoginLoadMaxS go anyway (the furniture rule below still protects the route)
    internal static LoginLoadVerdict LoginLoadStep(double sinceLoginS, double stableS, bool appearance, int worn, bool davidPlaced)
    {
        if (sinceLoginS >= LoginLoadMaxS) return LoginLoadVerdict.GaveUp;
        return appearance && davidPlaced && worn > 0 && sinceLoginS >= LoginLoadMinS && stableS >= LoginLoadStableS ? LoginLoadVerdict.Loaded : LoginLoadVerdict.Wait;
    }

    // ---- 2026-10-27 09:18 (David: "you walked to a good location but you should have fixed your hover first"): the walk started when "loaded in" was
    // reached, but the first visual hover check (PoseHover.cs; waits for the attachments / form) only finished ~2 min after login, so she walked floating.
    // Now the walk also waits for ONE hover check to have finished since login (the one a region / stand / sit request already runs; if none is in sight a
    // "login" request is made), at most LoginHoverMaxS after login, then goes anyway.
    internal enum LoginHoverVerdict { Wait, Done, GaveUp }
    internal const double LoginHoverMaxS = 150;
    // pure (tested): done = a hover check finished since login; never wait past LoginHoverMaxS after login
    internal static LoginHoverVerdict LoginHoverStep(double sinceLoginS, bool checkDoneSinceLogin) =>
        checkDoneSinceLogin ? LoginHoverVerdict.Done : sinceLoginS >= LoginHoverMaxS ? LoginHoverVerdict.GaveUp : LoginHoverVerdict.Wait;
    // pure (tested): a "login" check request that ended without measuring must be asked again (it gave up its tries or errored); "replaced by a newer ..."
    // means another request is already doing the check, so only wait
    internal static bool LoginHoverRerequest(string requestResult) =>
        requestResult != null && !requestResult.StartsWith("replaced") && (requestResult.Contains("gave up after") || requestResult.Contains(": error "));
    // pure (tested): the request ended with its own "never for this posture" answer (sitting on an object, unclassified animation): nothing more to wait for
    internal static bool LoginHoverSkipped(string requestResult) => requestResult != null && requestResult.Contains(": skip:");

    internal sealed record LoginGoal(string Kind, Vector3? To, string Why);   // Kind: follow (open floor: the follow engine) | spot (walk to To) | none (no clear route)
    internal const float LoginNearFurnitureM = 1.5f;
    // pure (tested): where to walk. On open floor (he is not on / near furniture and the straight line crosses none) = the follow engine as before.
    // Otherwise a floor spot: the seat's own stand_spot / approach when he is on that seat, else the nearest ring point around him (1.2-3.5 m)
    // that is outside every footprint (+0.35 m body margin) and reachable from me in a straight line that crosses no furniture or wall.
    internal static LoginGoal LoginJoinGoal(Vector3 me, Vector3 him, bool davidSeated, UUID seatRoot, IEnumerable<HomeSeatInfo> seats,
                                            IReadOnlyList<FurnitureBox> boxes, Func<float, float, bool> blocked = null)
    {
        boxes ??= new List<FurnitureBox>(); blocked ??= (_, _) => false;
        bool onFurniture = davidSeated || InFurnitureMargin(him, boxes, ClearBodyMargin) != null;
        bool nearFurniture = InFurnitureMargin(him, boxes, LoginNearFurnitureM) != null || !ClearLine(me, him, boxes, (_, _) => false);
        if (!onFurniture && !nearFurniture) return new LoginGoal("follow", null, "open floor");
        var cands = new List<Vector3>();
        var seat = seats?.FirstOrDefault(x => seatRoot != UUID.Zero && x.Id == seatRoot) ?? (onFurniture ? NearestHomeSeat(him, seats) : null);
        var spot = seat?.StandSpot ?? seat?.Approach;
        if (onFurniture && spot != null) cands.Add(new Vector3(spot.Value.X, spot.Value.Y, me.Z));
        var ring = new List<(Vector3 c, float score)>();
        foreach (var r in new[] { 1.2f, 1.6f, 2.0f, 2.5f, 3.0f, 3.5f })
            for (int k = 0; k < 24; k++)
            {
                float a = k * MathF.PI / 12; var c = new Vector3(him.X + MathF.Cos(a) * r, him.Y + MathF.Sin(a) * r, me.Z);
                ring.Add((c, r + 0.05f * HDist(me, c)));
            }
        cands.AddRange(ring.OrderBy(x => x.score).Select(x => x.c));
        foreach (var c in cands)
            if (InFurnitureMargin(c, boxes, ClearBodyMargin) == null && !blocked(c.X, c.Y) && ClearLine(me, c, boxes, blocked))
                return new LoginGoal("spot", c, onFurniture ? "he is on furniture" : "furniture beside him or on the way");
        return new LoginGoal("none", null, "no clear floor spot reachable without crossing furniture or a wall");
    }

    static (int worn, int near) LoginLoadCounts(Vector3 me)
    {
        var sim = client.Network.CurrentSim; if (sim == null) return (0, 0);
        return (WornPrims().Count, sim.ObjectsPrimitives.Values.Count(p => p != null && p.ParentID == 0 && HDist(p.Position, me) < 25f));
    }
    static async Task<string> LoginWaitLoaded(DateTime t0)
    {
        var last = (worn: -1, near: -1); var stableSince = DateTime.Now;
        while (LoggedIn)
        {
            var cnt = LoginLoadCounts(client.Self.SimPosition);
            if (cnt != last) { last = cnt; stableSince = DateTime.Now; }
            bool app = SelfAv()?.VisualParameters?.Length > 0;
            var dav = Avatars().FirstOrDefault(a => a.av != null && a.av.ID == DavidId);
            var v = LoginLoadStep((DateTime.Now - t0).TotalSeconds, (DateTime.Now - stableSince).TotalSeconds, app, cnt.worn, dav.av != null && dav.pos != Vector3.Zero);
            if (v != LoginLoadVerdict.Wait)
                return $"{(v == LoginLoadVerdict.Loaded ? "loaded in" : $"gave up waiting after {LoginLoadMaxS:F0} s (going anyway)")}: appearance {(app ? "yes" : "no")}, {cnt.worn} worn prims, {cnt.near} root prims within 25 m, {(DateTime.Now - t0).TotalSeconds:F0} s after login";
            await Task.Delay(500);
        }
        return "logged out";
    }

    static async Task<string> LoginWaitHover(DateTime t0)
    {
        Task<string> req = null;
        while (LoggedIn)
        {
            var since = (DateTime.Now - t0).TotalSeconds;
            var v = LoginHoverStep(since, hoverSvc.hoverCheckDoneAt >= t0);
            if (v == LoginHoverVerdict.Done) return $"hover check finished {(hoverSvc.hoverCheckDoneAt - t0).TotalSeconds:F0} s after login: {hoverSvc.hoverCheckResult}";
            if (v == LoginHoverVerdict.GaveUp) return $"no hover check finished within {LoginHoverMaxS:F0} s of login (going anyway, hover may be off): last request {(req == null ? "none" : req.IsCompleted ? req.Result : "still running")}";
            if (Env("GT_POSE_HOVER_CHECK", "on") == "off") return "hover check is off";
            // none requested by me yet (the region / stand request may already be running: it replaces nothing until I ask) or my last one gave up: ask once the form is known
            if (req != null && req.IsCompleted && LoginHoverSkipped(req.Result)) return $"hover check not applicable: {req.Result}";
            if ((req == null || req.IsCompleted && LoginHoverRerequest(req.Result)) && hoverSvc.FormCertainNow()) req = hoverSvc.HoverCheckRequest("login", 1000);
            await Task.Delay(500);
        }
        return "logged out";
    }

    static async Task<string> LoginWalkToSpot(Vector3 goal)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var t1 = DateTime.Now; var best = new System.Collections.Generic.List<(DateTime t, float d)>();
        var walk = WalkLeg(goal, 0.6f, "login join", cts.Token, navMode: true);
        string why = null;
        while (!walk.IsCompleted && LoggedIn)
        {
            await Task.Delay(500);
            best.Add((DateTime.Now, HDist(client.Self.SimPosition, goal)));
            if (LoginJoinStalled(best.Select(b => ((DateTime.Now - b.t).TotalSeconds, b.d)).ToList()))
            { why = $"approach blocked (no progress in {LoginStallS:F0} s): stopping, staying put"; cts.Cancel(); break; }
        }
        bool ok = false;
        try { ok = await walk; } catch (OperationCanceledException) { why ??= "walk cancelled or timed out"; } catch (Exception ex) { why = ex.GetBaseException().Message; }
        try { client.Self.AutoPilotCancel(); } catch { }
        return ok ? null : why ?? "did not arrive";
    }
    static void LoginJoinStart() { StuckSitNewLogin(); feetSvc.NewLogin(); loginJoinDone = new(TaskCreationOptions.RunContinuationsAsynchronously); loginPlacement = null; _ = Task.Run(LoginJoinDavid); }
    static async Task LoginJoinDavid()
    {
        try
        {
            Vector3? me = null, him = null; var t0 = DateTime.Now;
            while ((DateTime.Now - t0).TotalSeconds < 12)
            {
                await Task.Delay(250);
                if (!LoggedIn) return;
                var sim = client.Network.CurrentSim;
                bool selfArrived = sim != null && client.Self.LocalID != 0 && sim.ObjectsAvatars.ContainsKey(client.Self.LocalID);
                var dav = Avatars().FirstOrDefault(a => a.av != null && a.av.ID == DavidId);
                if (selfArrived) me = client.Self.SimPosition;
                if (dav.av != null && dav.pos != Vector3.Zero) him = dav.pos;
                if (me != null && him != null) break;
            }
            var spawn = LoginSpawnDistance(me, him);
            Log("login", $"placement: spawn {(me is Vector3 m ? Fmt(m) : "?")}, David {(him is Vector3 h ? Fmt(h) : "not in view")}: {(spawn is float s ? $"{s:F1} m" : "UNKNOWN")}");
            if (!LoginShouldJoin(spawn, him != null, client.Self.SittingOn != 0)) { loginPlacement = new(spawn, spawn == null ? "unknown" : "spawned-near", spawn); return; }
            Log("login", $"placement: waiting until I am fully loaded in before walking over ({spawn:F1} m): {await LoginWaitLoaded(t0)}");
            if (!LoggedIn) return;
            Log("login", $"placement: waiting for the first hover check before walking: {await LoginWaitHover(t0)}");
            if (!LoggedIn) return;
            // fresh positions after the wait (he may have moved; I may have been moved by a pose routine)
            var dav2 = Avatars().FirstOrDefault(a => a.av != null && a.av.ID == DavidId);
            if (dav2.av == null || dav2.pos == Vector3.Zero) { loginPlacement = new(spawn, "walk-failed", null); Log("login", "placement: David is no longer in view after loading in; not walking"); return; }
            var me2 = client.Self.SimPosition; float d0 = HDist(me2, dav2.pos);
            if (!LoginShouldJoin(d0, true, client.Self.SittingOn != 0)) { loginPlacement = new(spawn, "met", d0); Log("login", $"placement: David is already {d0:F1} m from me: no walk needed"); return; }
            var sim2 = client.Network.CurrentSim;
            var boxes = InPeronaut ? HomeFootprints() : new List<FurnitureBox>();
            var g = NavGridHere();
            Func<float, float, bool> blocked = g == null ? null : (x, y) => { if (!g.Contains(x, y)) return false; var (i, j) = g.IJ(x, y); return g.At(i, j) != 0; };
            uint par = dav2.av.ParentID; var rootId = UUID.Zero;
            if (par != 0 && sim2 != null && sim2.ObjectsPrimitives.TryGetValue(par, out var pp) && pp != null)
            { if (pp.ParentID != 0 && sim2.ObjectsPrimitives.TryGetValue(pp.ParentID, out var rp) && rp != null) pp = rp; rootId = pp.ID; }
            var goal = LoginJoinGoal(me2, dav2.pos, par != 0, rootId, LoadHomeSeats(), boxes, blocked);
            Log("login", $"placement: {goal.Kind} ({goal.Why}){(goal.To is Vector3 gt ? $" -> {P3(gt)}" : "")}; David {P3(dav2.pos)}{(par != 0 ? $" on seat {(rootId == UUID.Zero ? "(prim unknown)" : rootId.ToString())}" : "")}, me {P3(me2)}, {d0:F1} m");
            float d = d0;
            if (goal.Kind == "none") { loginPlacement = new(spawn, "no-route", d0); return; }
            if (goal.Kind == "spot")
            {
                var fail = await LoginWalkToSpot(goal.To.Value);
                var dv = Avatars().FirstOrDefault(a => a.av != null && a.av.ID == DavidId); d = dv.av != null ? dv.dist : d0;
                if (fail != null) Log("login", $"placement: {fail} ({HDist(client.Self.SimPosition, goal.To.Value):F1} m from the spot)");
                loginPlacement = new(spawn, fail == null ? "spot-walked" : "walk-failed", d);
                Log("login", $"placement: {(fail == null ? "beside the furniture, on the floor" : "could not reach the spot")}, {d:F1} m from David");
                return;
            }
            Log("login", $"placement: walking over to David ({d0:F1} m){(leash != null ? " (leashed: walking myself, not dragged)" : "")}");
            followDistThis = 1.2f; followName = DavidName; followId = DavidId;
            var t1 = DateTime.Now; var best = new System.Collections.Generic.List<(DateTime t, float d)>();
            while ((DateTime.Now - t1).TotalSeconds < 40 && LoggedIn)
            {
                await Task.Delay(500);
                var dav = Avatars().FirstOrDefault(a => a.av != null && a.av.ID == DavidId);
                if (dav.av == null) break;
                d = dav.dist; if (d <= 2f) break;
                best.Add((DateTime.Now, d));
                if (LoginJoinStalled(best.Select(b => ((DateTime.Now - b.t).TotalSeconds, b.d)).ToList()))
                { Log("login", $"placement: approach blocked (no progress in {LoginStallS:F0} s, {d:F1} m): stopping, staying put"); break; }
            }
            if (followId == DavidId) { followId = UUID.Zero; followDistThis = null; try { client.Self.AutoPilotCancel(); } catch { } }
            loginPlacement = new(spawn, d <= LoginJoinWithin ? "walked" : "walk-failed", d);
            Log("login", $"placement: {(d <= LoginJoinWithin ? "beside" : "could not reach")} David, {d:F1} m");
        }
        catch (Exception ex) { Log("login", "placement: " + ex.GetBaseException().Message); }
        finally { loginJoinDone.TrySetResult(true); }
    }
}
