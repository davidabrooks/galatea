// WanderSit.cs (2026-11-03, David: "Add a command that sends you to a chosen seat the full wander way, then test the tub").
// 'wander sit <place|uuid|name>' (alias 'wander go'): ONE home wander visit to that catalogued seat, run by the picker's own
// visit function HomeRandomSit(force: ...). So the walk / approach, the seat's special (undress at the change spot before
// sitting and dressed again after; the toilet flow; shower; boats), the menu / pose pick, the post-sit body check and adjust,
// bugged-pose detection, stay_s, standing and redressing are exactly the wander's. Nothing here re-implements any of it.
// Rules: only seats with "wander": true in the seat list; "adult": true seats only when David is near and nobody else is.
// Works with wander stopped; a running wander is stopped for the visit and started again after it (a pause comes back too).
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    internal const float WanderSitDavidNearM = 30f, WanderSitOthersM = 30f;
    static CancellationTokenSource wSitCts;
    static Task wSitTask;

    // pure: seat-list matches for a query. uuid exact > place exact > name substring (case-insensitive). Never null.
    internal static List<HomeSeatInfo> MatchHomeSeatQuery(IEnumerable<HomeSeatInfo> seats, string q)
    {
        q = (q ?? "").Trim(); var all = (seats ?? Enumerable.Empty<HomeSeatInfo>()).ToList();
        if (q.Length == 0) return new();
        if (UUID.TryParse(q, out var id) && id != UUID.Zero) return all.Where(s => s.Id == id).Take(1).ToList();
        var byPlace = all.Where(s => string.Equals(s.Place, q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byPlace.Count > 0) return byPlace;
        return all.Where(s => (s.Name ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    // pure: null = allowed, else the refusal. Adult seats only when David is near and no one else is.
    internal static string WanderSitAdultRefusal(HomeSeatInfo seat, bool davidNear, int othersNear)
    {
        if (seat == null || !seat.Adult) return null;
        if (!davidNear) return $"refused: '{seat.Name}' is an adult seat and David is not near me";
        if (othersNear > 0) return $"refused: '{seat.Name}' is an adult seat and {othersNear} other avatar(s) are near";
        return null;
    }

    // pure: which match to use. Several matches in one group / place (e.g. 'chairs') -> a random free one is fine;
    // matches that are different furniture -> ambiguous.
    internal static (HomeSeatInfo pick, string err) WanderSitChoose(List<HomeSeatInfo> m, string q, Random r)
    {
        var w = m.Where(s => s.Wander).ToList();
        if (m.Count == 0) return (null, $"no seat in the home seat list matches '{q}' (try a place like tub, sofa, bed, toilet, or a uuid)");
        if (w.Count == 0) return (null, $"'{m[0].Name}' is excluded from wander sits (\"wander\": false in the seat list)");
        var names = w.Select(s => s.Name).Distinct().ToList();
        bool samePlace = w.All(s => s.Place != null && s.Place == w[0].Place) || names.Count == 1;
        if (!samePlace) return (null, $"'{q}' matches {w.Count} different seats: " + string.Join(" | ", w.Select(s => $"'{s.Name}' {s.Id}" + (s.Place != null ? $" (place {s.Place})" : ""))));
        return (w[r.Next(w.Count)], null);
    }

    static async Task<string> WanderSitCmd(string q)
    {
        if (string.IsNullOrWhiteSpace(q)) return "usage: wander sit <place|uuid|name>  (alias: wander go)";
        if (!LoggedIn) return "not logged in";
        if (!InPeronaut) return "wander sit only works at home (Peronaut)";
        if (wSitTask != null && !wSitTask.IsCompleted) return "a wander sit visit is already running";
        var g = LoadGraph(HomeWanderRegion); if (g == null) return "no Peronaut path graph";
        var (info, err) = WanderSitChoose(MatchHomeSeatQuery(LoadHomeSeats(), q), q, wRnd);
        if (err != null) return err;
        var avs = Avatars().Where(t => t.dist >= 0).ToList();
        bool davidNear = avs.Any(t => t.av.ID == DavidId && t.dist <= WanderSitDavidNearM);
        int others = avs.Count(t => t.av.ID != DavidId && t.dist <= WanderSitOthersM);
        var refuse = WanderSitAdultRefusal(info, davidNear, others);
        if (refuse != null) { WLog("wander sit " + refuse); return refuse; }
        var sim = Sim;
        var p = sim.ObjectsPrimitives.Values.FirstOrDefault(x => x != null && x.ID == info.Id);
        if (p == null) return $"'{info.Name}' is not in view right now";
        if (Sitters(sim).ContainsKey(p.LocalID)) return $"'{info.Name}' is occupied";
        var (qpt, d) = NearestOnGraph(g, p.Position);
        if (d > 6f && !IsBoat(info)) return $"'{info.Name}' is {d:F1} m from the path graph";
        var level = avs.Where(t => Math.Abs(t.pos.Z - p.Position.Z) < 4f).ToList();
        float quiet = level.Count == 0 ? 999f : level.Min(t => HDist(t.pos, p.Position));
        var cand = new SeatCand(p, p.Properties?.Name ?? info.Name, Vector3.Distance(p.Position, client.Self.SimPosition), d, quiet, qpt);

        bool wasOn = WanderOn; var prevPause = wanderPause;
        if (wasOn) { StopWander("wander sit visit", clearFlag: false); try { await Task.WhenAny(wanderTask, Task.Delay(5000)); } catch { } }
        wanderPause = null;
        wSitCts = new CancellationTokenSource(); var ct = wSitCts.Token;
        WLog($"WANDER SIT: one visit to '{info.Name}' {info.Id}{(info.Special != null ? ", special " + info.Special : "")} (wander was {(wasOn ? "on" : "off")})");
        wSitTask = Task.Run(async () =>
        {
            bool ok = false;
            try { if (client.Self.SittingOn != 0) { await EnsureStandingForWalk(ct); } ok = await HomeRandomSit(g, ct, force: cand); }
            catch (OperationCanceledException) { WLog("wander sit visit cancelled"); }
            catch (Exception ex) { WLog("wander sit error: " + ex.GetBaseException().Message); }
            finally
            {
                try { if ((pendingDressOutfit != null || pendingToilet != null) && client.Self.SittingOn == 0) await DressAfterSeatIfPending(); } catch { }
                if (!ok && client.Self.SittingOn == 0) WLog($"wander sit visit to '{info.Name}' did not complete");
                var dv = Avatars().FirstOrDefault(t => t.av.ID == DavidId && t.dist >= 0);
                if (dv.av != null && client.Self.SittingOn == 0) client.Self.Movement.TurnToward(dv.pos);
                wanderPhase = "off";
                if (wasOn && !ct.IsCancellationRequested && LoggedIn)
                {
                    WLog("wander sit visit done: " + StartWander("after wander sit"));
                    if (prevPause != null) SetPause(prevPause, "restored after wander sit");
                }
                else if (!wasOn) WLog("wander sit visit done; wander stays off");
            }
        });
        return $"going to '{info.Name}' {info.Id} the full wander way{(info.Special != null ? $" (special {info.Special})" : "")}; wander {(wasOn ? "pauses for the visit and resumes after" : "stays off after")}";
    }
}
