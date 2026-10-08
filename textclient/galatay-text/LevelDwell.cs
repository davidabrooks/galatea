using LibreMetaverse;

namespace GalatayText;

// 2026-10-08 12:22 David: "Because it takes a while to change and walk down to the beach, let's change the wandering so
// when you go down to the beach you stay down there for a while before you go back up, and then you stay on the upper
// level for a while before you go back down. 30min? Also, go in the bedroom to change in/out of your bikini."
//
// Level dwell: the home wander picks walk targets and seats from one of two levels.
//  - beach: everything that needs the bikini (BeachBound: lower-beach / lower-pier seats, the beach zone z <= 23.5 and the
//    Burgundy pier). Beach, hammock, Nerenzo chairs, rowboat, outdoor shower, mooring deck + hot tub, pier benches.
//  - upper: everything else (the house, bathroom, bedroom, patios, front hall, the porch rockers).
// The clock starts when she is first seen on a level (at wander start / login, and after each arrival). Once it has run
// for routes/_wander-rules.json "levelDwellMin" (default 30) she picks from the other level. A level with nothing to
// pick (no walk targets and no free seats) falls back to the other one. If only the seats are busy, she takes a walk
// on the same level instead of sitting.
// Bikini changes: on the way down she walks to the bedroom change spot (graph place "bedroom", west wing, clear of the
// MIRAGE bed and the side-room-1 doorway), changes there, then heads down. On the way back up she walks straight to
// the same spot, changes back into the remembered outfit (colors kept), then goes on. While the home wander runs, the
// outfit-zone tick leaves that restore to the wander. When the wander is off or paused, she still changes back anywhere indoors.
public static partial class Program
{
    internal const double DefaultLevelDwellMin = 30;
    internal const string LevelBeach = "beach", LevelUpper = "upper";
    // beach-level walk targets (graph places); upper ones are the existing home ends
    internal static readonly string[] BeachLevelEnds = { "beach", "beach-east", "beach-chairs", "rowboat", "mooring-deck", "pier", "pier-end" };
    internal static readonly string[] UpperLevelEnds = { "front", "chairs", "patio-sw", "living", "home", "patio-east", "east-deck", "porch" };

    // west wing (MIRAGE bed) = the bedroom; same rect as the house outline's west wing
    internal static bool InBedroom(Vector3 p) => IndoorsAtHome(p) && p.X >= 213.0f && p.X <= 224.0f && p.Y >= 71.3f && p.Y <= 75.7f;

    // pure: which level a destination / position belongs to
    internal static string HomeLevelOf(Vector3 pos, string seatLevel = null) => BeachBound(pos, seatLevel) ? LevelBeach : LevelUpper;
    internal static string OtherLevel(string level) => level == LevelBeach ? LevelUpper : LevelBeach;

    // pure: arrival bookkeeping. A new level restarts the clock, the same level keeps it.
    internal static (string level, DateTime since) LevelArrive(string tracked, DateTime since, string levelNow, DateTime now) =>
        tracked == levelNow ? (tracked, since) : (levelNow, now);

    // pure: the level to pick from. Stay until dwellMin has passed, then the other level; fall back when one is empty.
    internal static string LevelToPick(string levelNow, DateTime since, DateTime now, double dwellMin, bool beachHasAny, bool upperHasAny)
    {
        var want = (now - since).TotalMinutes >= dwellMin ? OtherLevel(levelNow) : levelNow;
        bool Has(string l) => l == LevelBeach ? beachHasAny : upperHasAny;
        return !Has(want) && Has(OtherLevel(want)) ? OtherLevel(want) : want;
    }

    // pure: the walk targets on a level that exist in the graph (and really are on that level)
    internal static List<string> LevelEnds(Graph g, string level) =>
        (level == LevelBeach ? BeachLevelEnds : UpperLevelEnds)
            .Where(n => g.Places.ContainsKey(n) && HomeLevelOf(g.N[g.Places[n].node]) == level).ToList();

    // pure: the bikini change spot: graph place "bedroom", else the old living-room spot
    internal static string BikiniChangePlace(Graph g) =>
        g.Places.ContainsKey("bedroom") ? "bedroom" : g.Places.ContainsKey("living") ? "living" : "home";

    // pure: on the way back up (a non-beach target while in beach mode) she changes back in the bedroom
    internal static string ReturnChangePlan(bool targetBeachBound, bool beachMode) =>
        !targetBeachBound && beachMode ? "bedroom-restore" : "none";

    // pure: already standing at the change spot (no walk needed)
    internal static bool AtBikiniSpot(Vector3 me, Vector3 spot) => HDist(me, spot) <= 1.0f && Math.Abs(me.Z - spot.Z) <= 1.5f;

    // pure: the home wander owns the change back only while it is actually moving (on, at home, not paused; the 15 s
    // greeting stop counts as moving). On but paused indoors (chat, hold, user, ao, rest on the sofa), the outfit-zone
    // tick changes her back itself; the 8-min guard after the bedroom bikini change still applies. 2026-10-08 14:51 David.
    internal static bool HomeWanderOwnsRestore(bool wanderOn, bool inPeronaut, string pause) => wanderOn && inPeronaut && pause is null or "greet";

    // pure: the outfit-zone tick defers the house restore to the home wander (which changes back in the bedroom)
    internal static string ZoneActionWithWander(string zone, bool beachMode, bool bikiniWorn, bool indoors, bool homeWanderOwnsRestore)
    {
        var a = ZoneAction(zone, beachMode, bikiniWorn, indoors);
        return a == "restore" && homeWanderOwnsRestore ? "wait-wander" : a;
    }
    // pure: the zone tick's final action, with the 8-min guard after the bedroom bikini change ("wait-guard")
    internal static string ZoneTickAction(string zone, bool beachMode, bool bikiniWorn, bool indoors, bool homeWanderOwnsRestore, DateTime nowUtc, DateTime deferRestoreUntilUtc)
    {
        var a = ZoneActionWithWander(zone, beachMode, bikiniWorn, indoors, homeWanderOwnsRestore);
        return a == "restore" && nowUtc < deferRestoreUntilUtc ? "wait-guard" : a;
    }

    static string wLevel; static DateTime wLevelSince;
    static double LevelDwellMin => CurrentWanderRule().LevelDwellMin;

    static void WanderLevelReset()
    {
        wLevel = HomeLevelOf(client.Self.SimPosition); wLevelSince = DateTime.Now;
        WLog($"level dwell: starting on the {wLevel} level ({LevelDwellMin:0.#} min before switching)");
    }
    static void WanderNoteLevel()
    {
        var now = HomeLevelOf(client.Self.SimPosition);
        var prev = wLevel;
        (wLevel, wLevelSince) = LevelArrive(wLevel, wLevelSince, now, DateTime.Now);
        if (prev != wLevel) WLog($"level dwell: arrived on the {wLevel} level (from {prev ?? "-"}); staying {LevelDwellMin:0.#} min");
    }
    static string WanderLevelStatus() => wLevel == null ? "level -" :
        $"level {wLevel} for {(DateTime.Now - wLevelSince).TotalMinutes:F0}/{LevelDwellMin:0.#} min";

    // the level for the next pick (seats count only when the caller passes them)
    static string WanderPickLevel(Graph g, int beachSeats = 1, int upperSeats = 1)
    {
        WanderNoteLevel();
        bool beachAny = LevelEnds(g, LevelBeach).Count + beachSeats > 0, upperAny = LevelEnds(g, LevelUpper).Count + upperSeats > 0;
        var pick = LevelToPick(wLevel, wLevelSince, DateTime.Now, LevelDwellMin, beachAny, upperAny);
        if (pick != wLevel) WLog($"level dwell: {(DateTime.Now - wLevelSince).TotalMinutes:F0} min on the {wLevel} level: picking from the {pick} level now");
        return pick;
    }

    // next walk target on the chosen level (uniform, no repeat of the last 2, never the place she is at)
    static string PickHomeLegTarget(Graph g, string exclude)
    {
        var level = WanderPickLevel(g);
        var ends = LevelEnds(g, level);
        if (ends.Count == 0) ends = LevelEnds(g, OtherLevel(level));
        var cands = ends.Where(n => n != exclude).ToList();
        if (cands.Count == 0) cands = ends;
        var t = PickFresh(cands, wRecentPlaces, wRnd);
        WLog($"next target {t} ({level} level: uniform of [{string.Join(", ", cands)}], no repeat of last 2: recent [{string.Join(", ", wRecentPlaces)}])");
        return t;
    }

    // before any wander move: bikini in the bedroom when beach-bound, back in the bedroom when coming up
    static async Task BeachOutfitForTarget(Graph g, Vector3 target, string seatLevel, string what, CancellationToken ct)
    {
        if (BeachBound(target, seatLevel)) { await BikiniIndoorsIfBeachBound(g, target, seatLevel, what, ct); return; }
        if (ReturnChangePlan(false, beachMode) == "none") return;
        WLog($"back from the beach ({what}): walking to the bedroom to change back first");
        if (!await WalkToChangeSpot(g, "to change back", ct)) return;
        if (!IndoorsAtHome(client.Self.SimPosition)) { WLog("change back: not inside the house after the walk; trying again before the next move"); return; }
        await RestoreFromBeach($"bedroom ({what})");
    }

    // walk to the bedroom change spot; false when interrupted (pause / cancel)
    static async Task<bool> WalkToChangeSpot(Graph g, string why, CancellationToken ct)
    {
        var spotName = BikiniChangePlace(g);
        var spot = g.N[g.Places[spotName].node];
        if (AtBikiniSpot(client.Self.SimPosition, spot)) return true;
        await StepToNarrowCentre(spot, $"to the {spotName} {why}", ct);
        var (pts, err) = GraphRoute(g, client.Self.SimPosition, g.Places[spotName].node);
        if (err != null) { WLog($"change spot ({spotName}): no route ({err})"); return true; }
        wanderPhase = $"walking to the {spotName} {why}";
        legCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wLegGoal = spot; wLegHasGoal = true;
        try
        {
            var (ok, msg) = await FollowPoly(new Poly(pts), new RouteOpts { Label = $"to the {spotName} {why}", Place = spotName }, legCts.Token);
            WLog($"change spot ({spotName}): {(ok ? "reached" : "not reached")} ({msg})");
            if (!ok && msg.Contains("AO not active")) { wanderPause = "ao"; wPausedAt = DateTime.Now; return false; }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { client.Self.AutoPilotCancel(); WLog($"walk to the change spot interrupted ({wanderPause ?? "cancel"})"); return false; }
        finally { wLegHasGoal = false; }
        if (UnderHouse(client.Self.SimPosition)) await UnderHouseRecover(g, "walk to the change spot", ct);
        return wanderPause == null;
    }

    // back into the remembered outfit (colors kept), bikini HUD off. The wander calls this in the bedroom.
    static async Task RestoreFromBeach(string where)
    {
        for (int i = 0; i < 120 && beachOutfitBusy; i++) await Task.Delay(500);
        if (!beachMode) return;
        beachOutfitBusy = true;
        try { wanderPhase = "changing back from the bikini"; await RestoreFromBeachCore(where); }
        catch (Exception ex) { Log("outfit-zone", "restore error: " + ex.GetBaseException().Message); }
        finally { beachOutfitBusy = false; }
    }

    // the restore itself; the caller holds beachOutfitBusy (the zone tick and RestoreFromBeach)
    static async Task RestoreFromBeachCore(string where)
    {
        var restore = beachRememberedOutfit;
        beachMode = false; PersistBeachState();
        if (string.IsNullOrWhiteSpace(restore) || BikiniNameRx.IsMatch(restore))
        {
            Log("outfit-zone", $"back from the beach, {where}: no remembered outfit → bikini off (strapless+jeans)");
            var r = await BikiniOff();
            Log("outfit-zone", "house restore: " + r.Replace("\n", " | ")[..Math.Min(300, r.Length)]);
        }
        else
        {
            Log("outfit-zone", $"back from the beach, {where}: restoring outfit '{restore}'");
            var r = await WearOutfitWithHuds(restore, keepColor: true);
            RememberNamedOutfit(restore);
            if (WornPrims().Any(p => AttachItemId(p) == BikiniHudItem))
                await WearOpsCmd("wear", new[] { "remove", BikiniHudItem.ToString() });
            Log("outfit-zone", "house restore: " + r);
        }
        beachRememberedOutfit = null; PersistBeachState();
    }
}
