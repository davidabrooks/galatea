using LibreMetaverse;

namespace GalatayText;

// 2026-10-07 21:18 David: "Now you're stuck under the house".
// The wander approach to him used a straight 2-point line from the lower level (241.8,67.2,25.5) to 229.9,69.9,25.5.
// Its z was min(his z, hers), and it ended under the living room floor (z ~28). At Peronaut home, approaches
// (wander approach, goto_avatar, sit_near, front) now route over the path graph to the graph point nearest the avatar
// at the avatar's real level (stairs included). They refuse targets below the house floor or off the graph, and an
// under-house guard stops a walk that ends up below the floor and walks her back out (or 'home' as a last resort).
// 2026-10-07 21:19 David: "When you decide you walk to the beach you should put on your bikini inside the house".
// Before a beach-bound wander walk she changes in the living room. Back from the beach she changes back only once
// she is inside the house again (not on the porch or patios).
// 2026-10-08 08:14 David: no bikini change at home before the beach. The beach seat was picked while she was outside on
// patio-sw, so the indoor change was skipped and the beach zone changed her on the way down. Now, outside the house (and
// not already in the beach zone), she detours back into the living room, changes there, then walks on to the beach.
// The beach-zone change stays only as a safety net.
public static partial class Program
{
    // house body from the nav grid (_nav-peronaut-home.json, floor z 28.03): living + hall, the patio-door band, the west (bed) wing.
    // Patios, the east deck and the porch are outside.
    internal static readonly (float x0, float y0, float x1, float y1)[] PeronautHouseRects =
    {
        (224.6f, 60.5f, 231.4f, 79.4f),   // living room + front hall (front doors at y 79.6)
        (222.0f, 68.3f, 234.0f, 70.6f),   // band between the patio-1 / patio-2 doors (inside-west .. inside-east)
        (213.0f, 71.3f, 224.6f, 75.5f),   // west wing (MIRAGE bed)
    };
    internal const float HouseFloorZ = 28.03f, HomeApproachMaxOffGraph = 4f;

    internal static bool InHouseFootprint(float x, float y) => PeronautHouseRects.Any(r => x >= r.x0 && x <= r.x1 && y >= r.y0 && y <= r.y1);
    // pure: on the house floor (indoors)
    internal static bool IndoorsAtHome(Vector3 p) => InHouseFootprint(p.X, p.Y) && p.Z >= HouseFloorZ - 1.0f && p.Z <= HouseFloorZ + 3.0f;
    // pure: under the house (inside the footprint, well below the floor)
    internal static bool UnderHouse(Vector3 p) => InHouseFootprint(p.X, p.Y) && p.Z < HouseFloorZ - 1.5f;

    // pure: cut the route where it first comes within stopShort (horizontal, same level) of the target
    internal static List<Vector3> TrimRouteEnd(List<Vector3> pts, Vector3 target, float stopShort)
    {
        if (pts == null || pts.Count < 2) return pts;
        bool Close(Vector3 p) => HDist(p, target) <= stopShort && Math.Abs(p.Z - target.Z) < 2f;
        var outp = new List<Vector3> { pts[0] };
        for (int i = 1; i < pts.Count; i++)
        {
            var a = pts[i - 1]; var b = pts[i]; float L = HDist(a, b);
            int n = Math.Max(1, (int)(L / 0.25f));
            for (int k = 1; k <= n; k++)
            {
                var p = Vector3.Lerp(a, b, k / (float)n);
                if (Close(p)) { outp.Add(p); return outp; }
            }
            outp.Add(b);
        }
        return outp;
    }

    // pure: graph route from me to the graph point nearest the target (its real level), stopping stopShort before it
    internal static (List<Vector3> pts, string err) PlanHomeApproach(Graph g, Vector3 me, Vector3 target, float stopShort)
    {
        if (g == null) return (null, "no Peronaut path graph");
        if (UnderHouse(target)) return (null, $"target {P3(target)} is below the house floor");
        var (q, d) = NearestOnGraph(g, target);
        if (d > HomeApproachMaxOffGraph) return (null, $"target {P3(target)} is {d:F1} m off the path graph");
        if (UnderHouse(q)) return (null, $"nearest path point {P3(q)} is below the house floor");
        var (pts, err) = GraphRouteTo(g, me, q);
        if (err != null) return (null, err);
        pts = TrimRouteEnd(pts, target, stopShort);
        if (!UnderHouse(me) && pts.Any(UnderHouse)) return (null, "the route would pass under the house");
        return (pts, null);
    }

    // pure: lower-level graph node to walk out to from under the house (nearest, below the floor, outside the footprint)
    internal static int UnderHouseExitNode(Graph g, Vector3 me) =>
        g.N.Select((p, i) => (p, i)).Where(t => t.p.Z < HouseFloorZ - 1.5f && !UnderHouse(t.p))
           .OrderBy(t => HDist(t.p, me)).Select(t => t.i).DefaultIfEmpty(-1).First();

    // ---- bikini indoors before the beach -----------------------------------------------------------------
    // pure: a walk target in the beach zone (beach, pier, rowboat, hammock, mooring deck)
    internal static bool BeachBound(Vector3 target, string seatLevel) =>
        (seatLevel != null && (seatLevel.StartsWith("lower-beach", StringComparison.OrdinalIgnoreCase) || seatLevel.StartsWith("lower-pier", StringComparison.OrdinalIgnoreCase)))
        || OutfitZoneFor("Peronaut", target, false) == "beach";
    // pure: "none" | "change-here" (inside: change in the living room) | "detour-indoors" (outside: walk back into the living
    // room first, change there, then on to the beach). Already in the beach zone: "none" (the zone safety net changes her).
    internal static string IndoorBikiniPlan(bool beachBound, bool bikiniWorn, bool indoorsNow, bool inBeachZoneNow) =>
        !beachBound || bikiniWorn ? "none" : indoorsNow ? "change-here" : inBeachZoneNow ? "none" : "detour-indoors";
    // pure: the clothed outfit to come back to (never the bikini itself)
    internal static string BeachRememberChoice(string lastNamed, string beachRemembered)
    {
        if (!string.IsNullOrWhiteSpace(lastNamed) && !BikiniNameRx.IsMatch(lastNamed.Trim())) return lastNamed.Trim();
        if (!string.IsNullOrWhiteSpace(beachRemembered) && !BikiniNameRx.IsMatch(beachRemembered.Trim())) return beachRemembered.Trim();
        return null;
    }
    // pure: the outfit-zone tick's action. Restore only once she is inside the house; never re-wear a worn bikini.
    internal static string ZoneAction(string zone, bool beachMode, bool bikiniWorn, bool indoors) => zone switch
    {
        "beach" when beachMode => "none",
        "beach" when bikiniWorn => "mark-beach",
        "beach" => "bikini-on",
        "house" when !beachMode => "none",
        "house" when !indoors => "wait-indoors",
        "house" => "restore",
        _ => "none",
    };

    static async Task<string> RememberForBeach()
    {
        var r = BeachRememberChoice(lastNamedOutfit, beachRememberedOutfit);
        if (r != null) return r;
        var folders = await ListOutfitFolders(CancellationToken.None);
        return DailyOutfitCandidates(folders.Select(f => f.Name), DailyOutfitAllow()).FirstOrDefault();
    }

    // before a beach-bound wander walk: walk to the living room (not a doorway) and put the Bikini on there
    static async Task BikiniIndoorsIfBeachBound(Graph g, Vector3 target, string seatLevel, string what, CancellationToken ct)
    {
        var me = client.Self.SimPosition;
        var plan = IndoorBikiniPlan(BeachBound(target, seatLevel), BikiniWorn(), IndoorsAtHome(me), OutfitZoneFor("Peronaut", me, false) == "beach");
        if (plan == "none")
        {
            if (BeachBound(target, seatLevel) && !BikiniWorn()) WLog($"beach-bound ({what}) but already in the beach zone (at {P3(me)}): the beach zone will change her");
            return;
        }
        if (plan == "detour-indoors") WLog($"beach-bound ({what}) from outside the house (at {P3(me)}): detour into the living room to change first");
        var spotName = g.Places.ContainsKey("living") ? "living" : "home";
        var spot = g.N[g.Places[spotName].node];
        if (HDist(me, spot) > 3f || Math.Abs(me.Z - spot.Z) > 1.5f)
        {
            var (pts, err) = GraphRoute(g, me, g.Places[spotName].node);
            if (err == null)
            {
                wanderPhase = "walking to the living room to change";
                legCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                try
                {
                    var (ok, msg) = await FollowPoly(new Poly(pts), new RouteOpts { Label = $"to the {spotName} to put the bikini on" }, legCts.Token);
                    WLog($"bikini change spot ({spotName}): {(ok ? "reached" : "not reached")} ({msg})");
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                { client.Self.AutoPilotCancel(); WLog($"walk to the change spot interrupted ({wanderPause ?? "cancel"})"); return; }
            }
        }
        if (!IndoorsAtHome(client.Self.SimPosition)) { WLog("bikini: not inside the house after the walk; the beach zone will change her on arrival"); return; }
        beachOutfitBusy = true;
        try
        {
            var remember = await RememberForBeach();
            wanderPhase = "putting the bikini on";
            WLog($"BIKINI indoors before the beach ({what}): remember '{remember ?? "-"}' for the way back");
            var r = await BikiniOn();
            RememberNamedOutfit("Bikini");
            beachRememberedOutfit = remember; beachMode = true; PersistBeachState();
            WLog("bikini on: " + r.Replace("\n", " | ")[..Math.Min(300, r.Length)]);
        }
        catch (Exception ex) { WLog("bikini indoors failed: " + ex.GetBaseException().Message); }
        finally { beachOutfitBusy = false; }
    }

    // ---- under-house guard -------------------------------------------------------------------------------
    static async Task<bool> UnderHouseRecover(Graph g, string why, CancellationToken ct)
    {
        var me = client.Self.SimPosition;
        if (!InPeronaut || !UnderHouse(me)) return false;
        WLog($"UNDER THE HOUSE at {P3(me)} ({why}): walking back out over the path graph");
        int k = g == null ? -1 : UnderHouseExitNode(g, me);
        if (k >= 0)
        {
            try
            {
                var (ok, msg) = await FollowPoly(new Poly(new List<Vector3> { me, g.N[k] }), new RouteOpts { Label = "out from under the house", EscapeUnderHouse = true }, ct);
                WLog($"under-house exit to node {k} {P3(g.N[k])}: {(ok ? "out" : "failed")} ({msg})");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { WLog("under-house exit walk: " + ex.GetBaseException().Message); }
        }
        if (UnderHouse(client.Self.SimPosition))
        {
            WLog("still under the house: 'home' as the last resort");
            try { WLog("home: " + await Exec("home")); } catch (Exception ex) { WLog("home failed: " + ex.GetBaseException().Message); }
        }
        return true;
    }

    // goto_avatar / sit_near / front at home: walk over the path graph (never a straight line, never the upper grid from below)
    static async Task<string> HomeGraphWalkTo(Vector3 target, float stopShort, string label, CancellationToken ct)
    {
        var g = LoadGraph(HomeWanderRegion);
        var (pts, err) = PlanHomeApproach(g, client.Self.SimPosition, target, stopShort);
        if (err != null) { Log("walk", $"{label}: refused at home ({err})"); return "refused: " + err; }
        if (pts.Count < 2 || HDist(pts[0], pts[^1]) < 0.3f) return null;   // already there
        var (ok, msg) = await FollowPoly(new Poly(pts), new RouteOpts { Label = label + " (path graph)" }, ct);
        if (UnderHouse(client.Self.SimPosition)) await UnderHouseRecover(g, label, ct);
        return ok ? null : "stopped: " + msg;
    }
}
