// RoamLife.cs (2026-11-23, David, first live roam in Ahern 17:09-17:20 PT): roam that looks alive.
// - Next stop: a weighted random pick among all walkable spots, not the same top-16 list in a fixed order. Never a spot
//   within 5 m of the last 4 stops, never within 3 m of someone standing there now, and spots near a point of interest
//   (a sign, statue, fountain, vendor, building feature...) are preferred so a stop has a reason.
// - At a stop: look at something there (body turn + head LookAt for part of the pause, sometimes a second target),
//   or now and then glance at a nearby avatar briefly (one 4 s head turn, never tracking). Logged as [roam] look.
// - Sit detours: when a leg passes a free, non-adult seat away from other people with a safe path to it, sometimes
//   (35 %, at most once per 5 min) walk over and sit for 1-5 min, then stand (never in the middle of a seat adjust) and
//   go on roaming. The sit is the plain 'sit' command (AO off, sit guard, pose/hover checks as for any sit) run inside
//   the roam task, so Movers.cs treats it as roam's own step, and roam does not stop on its own sit. Adult seats are
//   skipped; if the seat starts a sexual pose anyway I stand at once.
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    internal const int RoamRecentStops = 4;
    internal const float RoamRecentAvoidM = 5f, RoamAvatarClearM = 3f;
    internal const float RoamPoiNearMinM = 2.5f, RoamPoiNearMaxM = 8f, RoamPoiBonus = 6f;
    internal const double RoamSitChance = 0.35, RoamSitCooldownS = 300;
    internal const int RoamSitMinS = 60, RoamSitMaxS = 300;
    internal const float RoamSeatLegM = 8f, RoamSeatOthersM = 4f, RoamLookAvatarChance = 0.15f;

    static volatile bool roamOwnSit;          // roam itself is sitting her (not an outside stop)
    static DateTime roamLastSitEnd = DateTime.MinValue;
    static readonly HashSet<UUID> roamSeatBad = new();
    static readonly Random roamRnd = new();
    static DateTime roamEjectAt = DateTime.MinValue; static volatile string roamEjectMsg = "";
    static readonly HashSet<(ulong handle, int parcel)> roamNoGo = new();

    // pure (tested): an alert that means this land threw me out / keeps me out
    internal static bool RoamIsEjectAlert(string msg) => !string.IsNullOrEmpty(msg) &&
        (msg.Contains("ejected", StringComparison.OrdinalIgnoreCase) || msg.Contains("banned", StringComparison.OrdinalIgnoreCase)
         || msg.Contains("not allowed to enter", StringComparison.OrdinalIgnoreCase) || msg.Contains("cannot enter", StringComparison.OrdinalIgnoreCase)
         || msg.Contains("can't enter", StringComparison.OrdinalIgnoreCase) || msg.Contains("no entry", StringComparison.OrdinalIgnoreCase));

    internal static void RoamNoteAlert(string msg)
    {
        if (!RoamOn || !RoamIsEjectAlert(msg)) return;
        roamEjectMsg = msg; roamEjectAt = DateTime.Now;
        RoamLog($"land alert while roaming: '{msg}' (re-planning, that parcel becomes no-go)");
    }

    static bool RoamParcelIsNoGo(Simulator sim, float x, float y)
    {
        lock (roamNoGo) if (roamNoGo.Count == 0) return false;
        try
        {
            int ix = Math.Clamp((int)(x / 4), 0, 63), iy = Math.Clamp((int)(y / 4), 0, 63);
            var id = sim.ParcelMap[iy, ix];
            lock (roamNoGo) return id > 0 && roamNoGo.Contains((sim.Handle, id));
        }
        catch { return false; }
    }

    // ---- next stop -----------------------------------------------------------------------------
    internal static string RoamKey(RoamCandidate c) => $"{c.Handle:X}:{c.X:F0},{c.Y:F0}";

    static float D2((double x, double y) a, (double x, double y) b) => (float)Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y));

    /// <summary>Pure (tested): index of the next stop, or -1. Skips bad spots, spots under 6 m away, spots near the last
    /// stops (relaxed if nothing else is left) and spots within 3 m of an avatar now. Weighted random by score.</summary>
    internal static int RoamChooseNext(IReadOnlyList<RoamCandidate> cands, (double x, double y) me,
        IReadOnlyList<(double x, double y)> recent, IReadOnlyList<(double x, double y)> avatarsNow, ICollection<string> bad, Random r)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            var idx = new List<int>(); var w = new List<double>();
            for (int i = 0; i < cands.Count; i++)
            {
                var c = cands[i]; var p = (c.X, c.Y);
                if (bad != null && bad.Contains(RoamKey(c))) continue;
                float d = D2(p, me);
                if (d < 6f) continue;
                if (pass == 0 && recent != null && recent.Any(s => D2(p, s) < RoamRecentAvoidM)) continue;
                if (avatarsNow != null && avatarsNow.Any(a => D2(p, a) < RoamAvatarClearM)) continue;
                idx.Add(i);
                w.Add(Math.Exp(Math.Clamp(c.Score, -20f, 40f) / 10.0) * (1.0 + Math.Min(d, 25f) / 25.0));
            }
            if (idx.Count == 0) continue;
            double roll = r.NextDouble() * w.Sum();
            for (int k = 0; k < idx.Count; k++) { roll -= w[k]; if (roll <= 0) return idx[k]; }
            return idx[^1];
        }
        return -1;
    }

    // ---- points of interest / look-at ----------------------------------------------------------
    internal readonly record struct RoamPoi(UUID Id, string Name, Vector3 Pos, float Interest, bool IsAvatar);

    static readonly string[] RoamPoiWords = { "sign", "board", "info", "map", "statue", "sculpture", "fountain", "vendor", "kiosk",
        "poster", "art", "painting", "tree", "plant", "flower", "lamp", "gate", "arch", "tower", "portal", "teleport", "welcome",
        "notice", "display", "screen", "pond", "boat", "column", "pillar", "monument", "bell", "clock", "garden", "bench", "shop",
        "store", "gallery", "landmark", "guide", "help", "tutorial", "light", "rock", "waterfall", "bridge", "house", "building" };

    /// <summary>Pure (tested): how interesting an object is to look at (0 = not). Named features score by keyword,
    /// unnamed / default-named objects only when they are big structures.</summary>
    internal static float RoamPoiInterest(string name, Vector3 scale)
    {
        var n = (name ?? "").Trim();
        float size = MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z));
        if (size < 0.3f) return 0;
        bool generic = n.Length == 0 || n.Equals("Object", StringComparison.OrdinalIgnoreCase) || n.Equals("Primitive", StringComparison.OrdinalIgnoreCase);
        if (!generic && RoamPoiWords.Any(k => n.Contains(k, StringComparison.OrdinalIgnoreCase))) return 2f + MathF.Min(size, 10f) / 10f;
        if (size >= 4f) return generic ? 0.6f : 1f;
        return generic ? 0 : 0.3f;
    }

    /// <summary>Pure (tested): what to look at from <paramref name="me"/>. Objects 2-15 m away, weighted by interest and
    /// nearness; with <paramref name="avatarChance"/> a nearby avatar (3-12 m) instead. Skips the last target.</summary>
    internal static RoamPoi? RoamPickLookTarget(IReadOnlyList<RoamPoi> pois, IReadOnlyList<RoamPoi> avatars, Vector3 me, Random r,
        float avatarChance = RoamLookAvatarChance, UUID skip = default)
    {
        var avs = (avatars ?? Array.Empty<RoamPoi>()).Where(a => a.Id != skip && HDist(a.Pos, me) is >= 3f and <= 12f).ToList();
        if (avs.Count > 0 && r.NextDouble() < avatarChance) return avs[r.Next(avs.Count)];
        var obj = (pois ?? Array.Empty<RoamPoi>()).Where(p => p.Id != skip && p.Interest > 0 && HDist(p.Pos, me) is >= 2f and <= 15f).ToList();
        if (obj.Count == 0) return avs.Count > 0 && r.NextDouble() < 0.5 ? avs[r.Next(avs.Count)] : null;
        var w = obj.Select(p => p.Interest / (1f + HDist(p.Pos, me) / 5f)).ToList();
        double roll = r.NextDouble() * w.Sum();
        for (int i = 0; i < obj.Count; i++) { roll -= w[i]; if (roll <= 0) return obj[i]; }
        return obj[^1];
    }

    /// <summary>Pure (tested): the looks during a pause: (start s, length s). One look for ~35-60 % of the pause, and on
    /// pauses of 30 s or more sometimes a second one later. Never past the end.</summary>
    internal static List<(int at, int len)> RoamLookPlan(int pauseS, Random r)
    {
        var plan = new List<(int, int)>();
        if (pauseS < 6) return plan;
        int a = r.Next(1, 4), len = Math.Max(3, (int)(pauseS * (0.35 + r.NextDouble() * 0.25)));
        len = Math.Min(len, pauseS - a - 1); if (len < 2) return plan;
        plan.Add((a, len));
        int next = a + len + r.Next(3, 8);
        if (pauseS >= 30 && r.NextDouble() < 0.5 && pauseS - next >= 6)
            plan.Add((next, Math.Min(pauseS - next - 1, Math.Max(4, (int)(pauseS * 0.25)))));
        return plan;
    }

    static List<RoamPoi> RoamPoisNear(Simulator sim, Vector3 me, float radius)
    {
        var res = new List<RoamPoi>();
        if (sim == null) return res;
        try
        {
            foreach (var p in sim.ObjectsPrimitives.Values)
            {
                if (p == null || p.ParentID != 0 || p.PrimData.PCode != PCode.Prim) continue;
                if (HDist(p.Position, me) > radius) continue;
                float it = RoamPoiInterest(p.Properties?.Name, p.Scale);
                if (it > 0) res.Add(new RoamPoi(p.ID, p.Properties?.Name ?? "", p.Position, it, false));
            }
        }
        catch { }
        return res;
    }

    static List<RoamPoi> RoamAvatarPois()
    {
        var res = new List<RoamPoi>();
        try { foreach (var a in Avatars()) if (a.av != null && a.av.ID != client.Self.AgentID && a.dist >= 0) res.Add(new RoamPoi(a.av.ID, a.av.Name, a.pos, 1f, true)); }
        catch { }
        return res;
    }

    // the pause at a stop, with looks (ends early on chat / sit / follow / logout like the old pause)
    static async Task RoamPauseWithLooks(int pause, CancellationToken ct)
    {
        var t0 = DateTime.Now; var plan = RoamLookPlan(pause, roamRnd); int step = 0; UUID last = UUID.Zero;
        (RoamPoi poi, DateTime until)? looking = null; DateTime lastSend = DateTime.MinValue;
        while ((DateTime.Now - t0).TotalSeconds < pause && !ct.IsCancellationRequested)
        {
            if (roamPause == "chat") break;
            if (client.Self.SittingOn != 0 || followId != UUID.Zero || !LoggedIn) break;
            double el = (DateTime.Now - t0).TotalSeconds;
            if (looking == null && step < plan.Count && el >= plan[step].at)
            {
                var (at, len) = plan[step++];
                var me = client.Self.SimPosition;
                var pois = RoamPoisNear(client.Network.CurrentSim, me, 15f);
                try { await EnsureProperties(client.Network.CurrentSim, client.Network.CurrentSim.ObjectsPrimitives.Values.Where(p => p != null && p.ParentID == 0 && HDist(p.Position, me) <= 15f && p.Properties == null).Take(40).ToList()); } catch { }
                pois = RoamPoisNear(client.Network.CurrentSim, me, 15f);
                var pick = RoamPickLookTarget(pois, RoamAvatarPois(), me, roamRnd, skip: last);
                if (pick is { } t)
                {
                    last = t.Id;
                    int hold = t.IsAvatar ? Math.Min(len, 5) : len;
                    looking = (t, DateTime.Now.AddSeconds(hold));
                    if (!t.IsAvatar) { try { client.Self.Movement.TurnToward(new Vector3(t.Pos.X, t.Pos.Y, me.Z)); } catch { } }
                    RoamLog($"look: {(t.IsAvatar ? "glance at avatar" : "object")} '{t.Name}' {HDist(t.Pos, me):F0} m away for {hold} s " +
                            $"({(t.IsAvatar ? "brief head turn, no body turn, not tracked" : $"interest {t.Interest:F1}, body turn + head look")}; look {step}/{plan.Count} of a {pause} s pause)");
                    if (t.IsAvatar) HeadTurnTo(t.Id, "roam glance");
                    lastSend = DateTime.Now;
                    if (!t.IsAvatar) HeadTurnToPoint(t.Pos, "roam look");
                }
                else RoamLog($"look: nothing of interest within 15 m (look {step}/{plan.Count})");
            }
            if (looking is { } lk)
            {
                if (DateTime.Now >= lk.until) looking = null;
                else if (!lk.poi.IsAvatar && (DateTime.Now - lastSend).TotalSeconds >= 2) { HeadTurnToPoint(lk.poi.Pos, "roam look"); lastSend = DateTime.Now; }
            }
            await Task.Delay(500, ct);
        }
    }

    // ---- sit detours ---------------------------------------------------------------------------
    internal readonly record struct RoamSeatSeen(UUID Id, string Name, Vector3 Pos, bool Occupied, bool Adult,
        float NearestOtherAvM, float DistToLegM, bool PathOk, bool InArea);

    internal static float RoamDistToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = new Vector2(b.X - a.X, b.Y - a.Y); var ap = new Vector2(p.X - a.X, p.Y - a.Y);
        float l2 = ab.LengthSquared();
        float t = l2 < 1e-6f ? 0 : Math.Clamp(Vector2.Dot(ap, ab) / l2, 0f, 1f);
        return (ap - ab * t).Length();
    }

    /// <summary>Pure (tested): why a seen seat is not a roam sit (null = fine).</summary>
    internal static string RoamSeatSkipReason(RoamSeatSeen s, bool knownBad = false)
    {
        if (!IsRealSeatName(s.Name)) return "not a seat";
        if (knownBad) return "refused me before";
        if (s.Adult) return "adult seat";
        if (s.Occupied) return "occupied";
        if (!s.InArea) return "outside the roam area";
        if (s.DistToLegM > RoamSeatLegM) return $"{s.DistToLegM:F0} m off the leg";
        if (s.NearestOtherAvM < RoamSeatOthersM) return $"someone {s.NearestOtherAvM:F1} m from it";
        if (!s.PathOk) return "no safe path";
        return null;
    }

    /// <summary>Pure (tested): sit this time? Only after the cooldown, and then with the given chance.</summary>
    internal static bool RoamSitRoll(double roll, double sinceLastSitS, double chance = RoamSitChance, double cooldownS = RoamSitCooldownS) =>
        sinceLastSitS >= cooldownS && roll < chance;

    internal static int RoamSitDurationS(Random r) => r.Next(RoamSitMinS, RoamSitMaxS + 1);

    /// <summary>Before a leg: maybe detour to a seat near it. True when she sat (and stood again) or the detour walk
    /// moved her, so the caller re-plans from where she is now.</summary>
    static async Task<bool> RoamMaybeSitDetour(Vector3 legTo, IReadOnlyDictionary<ulong, RoamRegionView> views, CancellationToken ct)
    {
        var sim = client.Network.CurrentSim; if (sim == null) return false;
        var me = client.Self.SimPosition;
        List<Primitive> near;
        try { near = sim.ObjectsPrimitives.Values.Where(p => p != null && p.ParentID == 0 && p.PrimData.PCode == PCode.Prim && RoamDistToSegment(p.Position, me, legTo) <= RoamSeatLegM + 4f).Take(200).ToList(); }
        catch { return false; }
        if (near.Count == 0) return false;
        try { await EnsureProperties(sim, near.Where(p => p.Properties == null).Take(60).ToList()); } catch { }
        var sitters = Sitters(sim);
        var others = Avatars().Where(a => a.av != null && a.av.ID != client.Self.AgentID && a.dist >= 0).Select(a => a.pos).ToList();
        var seen = new List<(RoamSeatSeen s, string why, Primitive p)>();
        foreach (var p in near)
        {
            var name = p.Properties?.Name ?? "";
            if (!IsRealSeatName(name)) continue;
            float nearest = others.Count == 0 ? 999f : others.Min(o => HDist(o, p.Position));
            var g = RoamToGlobal(sim.Handle, p.Position.X, p.Position.Y);
            var reg = RoamRegionAt(views, g.x, g.y, out var loc);
            bool pathOk = reg != null && RoamSeatPathOk(views, me, p.Position, sim.Handle);
            var s = new RoamSeatSeen(p.ID, name, p.Position, sitters.ContainsKey(p.LocalID), PoseSeatIsAdult(p.ID.ToString(), name) || PoseAdultSeatName(name),
                nearest, RoamDistToSegment(p.Position, me, legTo), pathOk, reg != null && Math.Abs(p.Position.Z - me.Z) < 2.5f);
            bool bad; lock (roamSeatBad) bad = roamSeatBad.Contains(p.ID);
            seen.Add((s, RoamSeatSkipReason(s, bad), p));
        }
        if (seen.Count == 0) return false;
        var ok = seen.Where(x => x.why == null).OrderBy(x => x.s.DistToLegM).ToList();
        double since = (DateTime.Now - roamLastSitEnd).TotalSeconds, roll = roamRnd.NextDouble();
        bool go = ok.Count > 0 && RoamSitRoll(roll, since);
        RoamLog($"sit check: {seen.Count} seat(s) near the leg: " + string.Join("; ", seen.Select(x => $"'{x.s.Name}' {HDist(x.s.Pos, me):F0} m {(x.why ?? "ok")}")) +
                (ok.Count == 0 ? " -> no sit" : go ? $" -> sitting (roll {roll:F2} < {RoamSitChance:F2})"
                    : since < RoamSitCooldownS ? $" -> not now (last roam sit {since:F0} s ago, cooldown {RoamSitCooldownS:F0} s)" : $" -> not this time (roll {roll:F2})"));
        if (!go) return false;

        var (seat, _, prim) = ok[0];
        int stay = RoamSitDurationS(roamRnd);
        var dir = new Vector3(seat.Pos.X - me.X, seat.Pos.Y - me.Y, 0); dir = dir.Length() > 0.01f ? Vector3.Normalize(dir) : Vector3.UnitX;
        var stop = seat.Pos - dir * 1.2f; stop = new Vector3(stop.X, stop.Y, seat.Pos.Z);
        RoamLog($"sit: walking to '{seat.Name}' {seat.Id} at {V(seat.Pos)} to sit for {stay} s");
        if (HDist(me, stop) > 1.5f && !await WalkPath(new List<Vector3> { stop }, ct, lastTol: 1.5f))
        { lock (roamSeatBad) roamSeatBad.Add(seat.Id); RoamLog($"sit: could not reach '{seat.Name}'; skipping it from now on"); return true; }
        if (Sitters(sim).ContainsKey(prim.LocalID)) { RoamLog($"sit: '{seat.Name}' got taken; going on"); return true; }
        roamOwnSit = true;
        bool sat = false;
        try
        {
            var (sok, r) = await TrySitOn(prim);   // the 'sit' command: inside the roam task, so Movers.cs counts it as roam's own step
            if (!sok) { lock (roamSeatBad) roamSeatBad.Add(seat.Id); RoamLog($"sit: '{seat.Name}' did not take me ({r}); going on"); return true; }
            sat = true;
            RoamLog($"sit: seated on '{seat.Name}' for {stay} s");
            var t0 = DateTime.Now;
            while ((DateTime.Now - t0).TotalSeconds < stay)
            {
                await Task.Delay(1000, ct);
                if (!LoggedIn) return true;
                if (client.Self.SittingOn == 0) { RoamLog("sit: I'm standing already (someone or something stood me); going on"); return true; }
                if ((DateTime.Now - t0).TotalSeconds > 4 && SexualPoseRunningNow()) { RoamLog("sit: the seat started an adult pose; standing now (never in roam)"); break; }
            }
            for (int i = 0; i < 60 && SeatAdjustBusy; i++) await Task.Delay(1000, ct);   // never stand in the middle of a seat adjust
            var sr = await Exec("stand");
            for (int i = 0; i < 10 && client.Self.SittingOn != 0; i++) await Task.Delay(500, ct);
            RoamLog($"sit: stood up after {(DateTime.Now - t0).TotalSeconds:F0} s ({sr}); roaming on");
            var dv = Avatars().FirstOrDefault(t => t.av?.ID == DavidId && t.dist >= 0 && t.dist <= 20f);
            if (dv.av != null) try { client.Self.Movement.TurnToward(new Vector3(dv.pos.X, dv.pos.Y, client.Self.SimPosition.Z)); } catch { }
            return true;
        }
        catch (OperationCanceledException) { if (sat) RoamLog("sit: roam stopped while I sit; staying seated"); throw; }
        finally { roamOwnSit = false; if (sat) roamLastSitEnd = DateTime.Now; }
    }


    static bool RoamSeatPathOk(IReadOnlyDictionary<ulong, RoamRegionView> views, Vector3 me, Vector3 seat, ulong frame)
    {
        try
        {
            var a = RoamToGlobal(frame, me.X, me.Y); var b = RoamToGlobal(frame, seat.X, seat.Y);
            float len = MathF.Max(0, D2(a, b) - 1.2f);   // up to the stop point before the seat
            (double x, double y) At(float d) { float t = D2(a, b) < 1e-3f ? 1 : d / D2(a, b); return (a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t); }
            var res = RoamPathCheckAlong(len,
                d => { var p = At(d); var r = RoamRegionAt(views, p.x, p.y, out var l); return r == null ? me.Z : RoamSupportAt(r, l, me.Z); },
                d => { var p = At(d); var r = RoamRegionAt(views, p.x, p.y, out var l); return r != null && RoamWaterAt(r, l, me.Z); },
                d => { var p = At(d); return RoamRegionAt(views, p.x, p.y, out _) == null; });
            if (res != RoamPathReject.Ok) return false;
            var stopG = At(len);
            // the seat itself (and the furniture around it) is where she is going: ignore boxes that hold the seat point
            return !RoamWalkHitsObstacle(views, a, stopG, me.Z, o =>
            {
                var r = RoamRegionAt(views, b.x, b.y, out var loc);
                return loc.X >= o.X0 - 0.5f && loc.X <= o.X1 + 0.5f && loc.Y >= o.Y0 - 0.5f && loc.Y <= o.Y1 + 0.5f;
            });
        }
        catch { return false; }
    }
}
