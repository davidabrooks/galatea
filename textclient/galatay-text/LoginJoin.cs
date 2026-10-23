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
    static void LoginJoinStart() { loginJoinDone = new(TaskCreationOptions.RunContinuationsAsynchronously); loginPlacement = null; _ = Task.Run(LoginJoinDavid); }
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
            Log("login", $"placement: walking over to David ({spawn:F1} m){(leash != null ? " (leashed: walking myself, not dragged)" : "")}");
            followDistThis = 1.2f; followName = DavidName; followId = DavidId;
            var t1 = DateTime.Now; float d = spawn.Value; var best = new System.Collections.Generic.List<(DateTime t, float d)>();
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
