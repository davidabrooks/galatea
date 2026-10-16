// ParcelWatch.cs (2026-10-16, David): "alone" means nobody else on OUR OWN PARCEL.
// David: "there is no one else in our parcel.. if neighbors want to spy on us we will let them". Refines the Oct 14 rule
// (only avatars in Peronaut itself count): avatars elsewhere in the Peronaut region and in neighbour regions never count.
//   - The parcel is the one my avatar stands on: ParcelProperties (sent on arrival, and requested by position when no cached
//     parcel of this region covers my 4 m cell) carries the parcel's local id, name and a 64x64 bitmap of the region's 4 m
//     cells. An avatar is "on our parcel" when its cell is in the bitmap (streamed avatars: exact position, seated ones
//     through their seat; map-only avatars: the region map's whole-metre x,y, checked over the metre it can be in).
//   - Fail safe: no parcel data (not arrived / not covering my cell / older than 20 min) -> the old strict region-wide count,
//     logged with the reason. Missing data never means "alone". An avatar whose position is unknown counts as on the parcel.
//   - Parcel entry watcher: on every region-map update and once a second, the avatars on the parcel are compared with the
//     previous set. 'PARCEL ENTER: <name> on our parcel' / 'PARCEL LEAVE: <name> left our parcel' go to the log (so they are
//     poll_events of kind "parcel"; at home, Peronaut, only). A running sexual pose is dropped at once when someone enters (ParcelIntruderReact).
//   - 'alone' shows the verdict and every avatar of the region with its cell; 'nearby'/'avatars' show the parcel line.
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using LibreMetaverse;

namespace GalatayText;

internal readonly record struct ParcelAvatar(UUID Id, Vector3? Pos, float Slack);

internal sealed record AloneVerdict(bool Allowed, bool Fallback, string Line, List<UUID> OnParcel, List<UUID> Elsewhere);

// pure: who was on the parcel before / now. Entering is immediate; leaving only after the avatar has been off the parcel
// for GraceS (a map position flickering at the edge of the parcel is not a leave + enter).
internal sealed class ParcelPresence
{
    readonly Dictionary<UUID, DateTime> lastOn = new(); readonly HashSet<UUID> present = new(); bool baselined;
    public double GraceS = 4;
    public bool Baselined => baselined;
    public IReadOnlyCollection<UUID> Present => present;
    public void Reset() { lastOn.Clear(); present.Clear(); baselined = false; }
    // first call after a Reset only records who is there (nobody "enters" because I arrived or the parcel data arrived)
    public (List<UUID> entered, List<UUID> left, bool baseline) Update(DateTime now, ISet<UUID> onParcelNow)
    {
        var entered = new List<UUID>(); var left = new List<UUID>();
        if (!baselined)
        {
            foreach (var id in onParcelNow) { present.Add(id); lastOn[id] = now; }
            baselined = true; return (entered, left, true);
        }
        foreach (var id in onParcelNow) { lastOn[id] = now; if (present.Add(id)) entered.Add(id); }
        foreach (var id in present.ToList())
            if (!onParcelNow.Contains(id) && (now - lastOn[id]).TotalSeconds >= GraceS) { present.Remove(id); lastOn.Remove(id); left.Add(id); }
        return (entered, left, false);
    }
}

public static partial class Program
{
    internal const int ParcelCells = 64, ParcelCellM = 4, ParcelBitmapBytes = 512;

    // ---- pure -------------------------------------------------------------------------------------------------------
    internal static bool ParcelBitmapValid(byte[] bm) => bm != null && bm.Length >= ParcelBitmapBytes && bm.Take(ParcelBitmapBytes).Any(b => b != 0);

    // bitmap layout as LibreMetaverse's ParcelProperties handler reads it: index = y * 64 + x, bit = index % 8 of byte index / 8
    internal static bool ParcelCellOn(byte[] bm, int cx, int cy)
    {
        if (!ParcelBitmapValid(bm) || cx < 0 || cy < 0 || cx >= ParcelCells || cy >= ParcelCells) return false;
        int index = cy * ParcelCells + cx;
        return (bm[index >> 3] & (1 << (index % 8))) != 0;
    }

    // a point of the current region (x,y in metres) is on the parcel. slack > 0: the real position is somewhere in
    // [x, x+slack] (the region map sends whole metres), so the point counts when ANY of those corners is on the parcel
    // (the stricter reading). Outside 0..256 = another region = never on the parcel. Height is not used.
    internal static bool ParcelOn(byte[] bm, float x, float y, float slack = 0f)
    {
        foreach (var (dx, dy) in new[] { (0f, 0f), (slack, 0f), (0f, slack), (slack, slack) })
        {
            float px = x + dx, py = y + dy;
            if (px < 0 || py < 0 || px >= 256 || py >= 256) continue;
            if (ParcelCellOn(bm, (int)(px / ParcelCellM), (int)(py / ParcelCellM))) return true;
        }
        return false;
    }

    // the verdict. others = avatars of the CURRENT region only (not me); bitmap null/invalid = no parcel data (reason says why).
    internal static AloneVerdict AloneCheck(bool androidWorn, string region, IEnumerable<ParcelAvatar> others, UUID david,
        byte[] bitmap, string parcelName, int parcelLocalId, string noDataReason, Func<UUID, string> nameOf = null)
    {
        nameOf ??= id => id.ToString();
        var list = (others ?? Enumerable.Empty<ParcelAvatar>()).Where(a => a.Id != UUID.Zero).GroupBy(a => a.Id).Select(g => g.First()).ToList();
        string Names(IEnumerable<UUID> ids) { var l = ids.Select(i => i == david ? "David" : nameOf(i)).ToList(); return l.Count == 0 ? "none" : string.Join(", ", l); }
        if (androidWorn) return new(false, false, "alone check: android outfit worn -> no sexual poses", new(), new());
        if (!string.Equals(region, "Peronaut", StringComparison.OrdinalIgnoreCase)) return new(false, false, $"alone check: region '{region}' is not Peronaut -> no sexual poses", new(), new());
        if (!ParcelBitmapValid(bitmap))
        {   // strict old behaviour: everyone in the region counts
            var ids = list.Select(a => a.Id).ToList();
            bool ok = SexualPosesAllowed(false, region, ids, david);
            return new(ok, true, $"alone check: no parcel data ({noDataReason}); falling back to the strict region-wide count: others in region: {Names(ids)} -> {(ok ? "alone with David" : "not alone")}", ids, new());
        }
        var on = new List<UUID>(); var off = new List<UUID>();
        foreach (var a in list)
            (a.Pos == null || ParcelOn(bitmap, a.Pos.Value.X, a.Pos.Value.Y, a.Slack) ? on : off).Add(a.Id);
        bool davidOn = on.Contains(david); var others2 = on.Where(i => i != david).ToList();
        bool allowed = davidOn && others2.Count == 0;
        string why = allowed ? "alone with David" : !davidOn && others2.Count == 0 ? "David is not on our parcel" : "not alone";
        return new(allowed, false, $"alone check: parcel '{parcelName}' (local id {parcelLocalId}), others on parcel: {Names(others2)} ({off.Count} elsewhere in region ignored); David {(davidOn ? "on" : "not on")} our parcel -> {why}", on, off);
    }

    // what a parcel entry does to a running pose. (action, why): "switch" = leave the sexual pose now
    internal static (string action, string why) PoseIntruderDecision(bool seated, bool sexualPose, bool davidOnSeat, bool allowedNow, double sinceLastReactS)
    {
        if (!seated) return ("none", "not seated");
        if (!sexualPose) return ("none", "no sexual pose running");
        if (davidOnSeat) return ("none", "David is on the seat (he chooses poses)");
        if (allowedNow) return ("none", "still alone with David");
        if (sinceLastReactS < 10) return ("none", "just reacted");
        return ("switch", "a sexual pose is running and I am no longer alone");
    }

    // ---- parcel cache -----------------------------------------------------------------------------------------------
    sealed record ParcelEntry(int LocalId, string Name, byte[] Bitmap, DateTime At);
    static readonly ConcurrentDictionary<ulong, ConcurrentDictionary<int, ParcelEntry>> parcelDataByRegion = new();
    static DateTime parcelAskedAt = DateTime.MinValue; static int parcelAsking;
    static readonly TimeSpan ParcelMaxAge = TimeSpan.FromMinutes(20), ParcelRefreshAge = TimeSpan.FromMinutes(10);
    static readonly ParcelPresence parcelPresence = new();
    static int parcelPresenceLocalId = -1; static ulong parcelPresenceHandle;
    static DateTime parcelReactAt = DateTime.MinValue, parcelVerdictLogAt = DateTime.MinValue; static string parcelVerdictLast;
    static int parcelEvalBusy;

    static void HookParcelWatch()
    {
        client.Parcels.ParcelProperties += (s, e) =>
        {
            try
            {
                var p = e.Parcel; if (e.Simulator == null || p == null || e.Result == ParcelResult.NoData || !ParcelBitmapValid(p.Bitmap)) return;
                parcelDataByRegion.GetOrAdd(e.Simulator.Handle, _ => new())[p.LocalID] = new ParcelEntry(p.LocalID, p.Name ?? "", p.Bitmap.ToArray(), DateTime.Now);
            }
            catch { }
        };
        client.Network.SimChanged += (s, e) =>
        {   // refresh on region change / teleport: drop every other region's parcels, the new one is requested by the tick
            var h = client.Network.CurrentSim?.Handle ?? 0;
            foreach (var k in parcelDataByRegion.Keys.ToList()) if (k != h) parcelDataByRegion.TryRemove(k, out _);
            parcelPresence.Reset(); parcelPresenceLocalId = -1; parcelAskedAt = DateTime.MinValue;
        };
        client.Grid.CoarseLocationUpdate += (s, e) => { if (e.Simulator == client.Network.CurrentSim) { try { ParcelWatchEval("region map"); } catch (Exception ex) { Log("parcel", "watch error: " + ex.GetBaseException().Message); } } };
    }

    static Vector3 ParcelSelfPos()
    {
        var sim = client.Network.CurrentSim;
        try { if (sim != null && sim.ObjectsAvatars.TryGetValue(client.Self.LocalID, out var me) && me != null) return PositionHelper.GetAvatarPosition(sim, me); } catch { }
        return client.Self.SimPosition;
    }

    // the cached parcel under me (null + reason when there is none); kicks off a request when missing or due for a refresh
    static (ParcelEntry p, string why) ParcelHere()
    {
        var sim = client.Network.CurrentSim; if (sim == null) return (null, "not in a region");
        var me = ParcelSelfPos(); ParcelEntry hit = null; bool stale = false;
        if (parcelDataByRegion.TryGetValue(sim.Handle, out var d))
            foreach (var e in d.Values)
                if (ParcelOn(e.Bitmap, me.X, me.Y) && (hit == null || e.At > hit.At)) hit = e;
        if (hit != null && DateTime.Now - hit.At > ParcelMaxAge) { stale = true; hit = null; }
        if (hit == null || DateTime.Now - hit.At > ParcelRefreshAge) ParcelAsk(sim, me);
        if (hit != null) return (hit, null);
        return (null, stale ? "parcel data older than 20 min, refreshing" : $"no parcel data covers my position {me.X:0},{me.Y:0} yet (requested)");
    }

    static void ParcelAsk(Simulator sim, Vector3 at)
    {
        if (DateTime.Now - parcelAskedAt < TimeSpan.FromSeconds(10) || Interlocked.Exchange(ref parcelAsking, 1) == 1) return;
        parcelAskedAt = DateTime.Now;
        _ = Task.Run(async () =>
        {
            try { var p = await ParcelAt(sim, at.X, at.Y, 8000); if (p == null) Log("parcel", $"parcel request at {at.X:0},{at.Y:0}: no ParcelProperties reply within 8 s"); }
            catch (Exception ex) { Log("parcel", "parcel request failed: " + ex.GetBaseException().Message); }
            finally { parcelAsking = 0; }
        });
    }

    // other avatars of the CURRENT region: streamed ones with their real position (seated: through the seat), map-only ones
    // with the region map's whole-metre position; neighbour regions are not looked at. null pos = position unknown.
    static List<ParcelAvatar> ParcelOthers()
    {
        var sim = client.Network.CurrentSim; var res = new Dictionary<UUID, ParcelAvatar>(); if (sim == null) return new();
        var me = client.Self.AgentID;
        IReadOnlyDictionary<UUID, Vector3> coarse = null; try { if (coarseBy.TryGetValue(sim.Handle, out var c)) coarse = c; } catch { }
        foreach (var a in sim.ObjectsAvatars.Values.ToArray())
        {
            if (a == null || a.ID == me || a.ID == UUID.Zero) continue;
            Vector3? pos = null; float slack = 0;
            try { if (a.ParentID == 0 || sim.ObjectsPrimitives.ContainsKey(a.ParentID)) pos = PositionHelper.GetAvatarPosition(sim, a); } catch { }
            if (pos == null && coarse != null && coarse.TryGetValue(a.ID, out var cp)) { pos = cp; slack = 1; }
            res[a.ID] = new ParcelAvatar(a.ID, pos, slack);
        }
        if (coarse != null)
            foreach (var (id, p) in coarse) if (id != me && id != UUID.Zero && !res.ContainsKey(id)) res[id] = new ParcelAvatar(id, p, 1);
        return res.Values.ToList();
    }

    static string ParcelNameOf(UUID id) { var n = NameOf(id); return n == id.ToString() ? id.ToString()[..8] : n; }

    // the verdict now, from live data. logIt: log the line when it changed (or every 60 s while it is asked for).
    static AloneVerdict AloneCheckNow(bool logIt = true)
    {
        var sim = client.Network.CurrentSim;
        var (p, why) = ParcelHere();
        var v = AloneCheck(AndroidWorn(), sim?.Name, ParcelOthers(), DavidId, p?.Bitmap, p?.Name, p?.LocalId ?? 0, why, ParcelNameOf);
        if (logIt && (v.Line != parcelVerdictLast || (DateTime.Now - parcelVerdictLogAt).TotalSeconds > 60)) { parcelVerdictLast = v.Line; parcelVerdictLogAt = DateTime.Now; Log("parcel", v.Line); }
        return v;
    }

    // ---- entry / leave watcher -----------------------------------------------------------------------------------------
    static void ParcelWatchTick() { if (LoggedIn) ParcelWatchEval("tick"); try { NakedRedressTick(); } catch (Exception ex) { Log("outfit", "redress tick error: " + ex.GetBaseException().Message); } }

    static void ParcelWatchEval(string src)
    {
        if (!LoggedIn || Interlocked.Exchange(ref parcelEvalBusy, 1) == 1) return;
        try
        {
            var sim = client.Network.CurrentSim; if (sim == null || !sim.AgentMovementComplete || !InPeronaut) return;   // home only: no enter / leave events in public places
            var (p, _) = ParcelHere(); if (p == null) return;           // no parcel data: no entry events (never guess)
            if (parcelPresenceHandle != sim.Handle || parcelPresenceLocalId != p.LocalId)
            { parcelPresence.Reset(); parcelPresenceHandle = sim.Handle; parcelPresenceLocalId = p.LocalId; }
            var on = new HashSet<UUID>(ParcelOthers().Where(a => a.Id != DavidId && (a.Pos == null || ParcelOn(p.Bitmap, a.Pos.Value.X, a.Pos.Value.Y, a.Slack))).Select(a => a.Id));
            var (entered, left, baseline) = parcelPresence.Update(DateTime.Now, on);
            if (baseline) { Log("parcel", $"parcel '{p.Name}' (local id {p.LocalId}): on our parcel now: {(on.Count == 0 ? "nobody but David/me" : string.Join(", ", on.Select(ParcelNameOf)))}"); return; }
            foreach (var id in left) Log("parcel", $"PARCEL LEAVE: {ParcelNameOf(id)} ({id}) left our parcel '{p.Name}'");
            foreach (var id in entered) Log("parcel", $"PARCEL ENTER: {ParcelNameOf(id)} ({id}) on our parcel '{p.Name}' ({src})");
            if (entered.Count > 0) ParcelIntruderReact(string.Join(", ", entered.Select(ParcelNameOf)));
        }
        finally { parcelEvalBusy = 0; }
    }

    // someone entered our parcel: leave a running sexual pose now (the same switch as when David stands up: a non-sexual solo
    // pose of the seat, found from its root menu; the sexual rule no longer allows anything adult). Outfits are not touched.
    static void ParcelIntruderReact(string who)
    {
        bool seated = client.Self.SittingOn != 0;
        bool sexual = seated && SexualPoseRunningNow();
        var v = AloneCheckNow();
        var (act, why) = PoseIntruderDecision(seated, sexual, poseDavidOnSeat, v.Allowed, (DateTime.Now - parcelReactAt).TotalSeconds);
        if (act != "switch") { if (sexual || seated) Log("parcel", $"{who} entered our parcel: pose left as is ({why})"); return; }
        parcelReactAt = DateTime.Now;
        Log("parcel", $"{who} entered our parcel while I am in a sexual pose [{string.Join(" > ", wLastPosePath)}]: switching to a non-sexual pose now");
        var gen = Interlocked.Increment(ref poseDavidLeftSwitchGen);
        _ = Task.Run(async () =>
        {
            await PoseSwitchToSoloAfterDavidLeft(gen, 0);
            if (client.Self.SittingOn != 0 && PosePathIsSexual(wLastPosePath) && !SexualPosesAllowedNow())
            { Log("parcel", "still in a sexual pose after the switch: standing up"); client.Self.Stand(); }
        });
    }

    // ---- commands / nearby ------------------------------------------------------------------------------------------
    // 'alone': the verdict and every avatar of this region with its 4 m cell and whether it is on our parcel
    static string AloneCmd()
    {
        var sim = client.Network.CurrentSim; if (sim == null) return "not in a region";
        var v = AloneCheckNow(false); var (p, why) = ParcelHere();
        var sb = new StringBuilder(v.Line + "\n");
        sb.AppendLine(p == null ? $"parcel: unknown ({why})" : $"parcel: '{p.Name}' local id {p.LocalId}, data {(DateTime.Now - p.At).TotalSeconds:0} s old, {Enumerable.Range(0, 4096).Count(i => ParcelCellOn(p.Bitmap, i % 64, i / 64))} cells ({Enumerable.Range(0, 4096).Count(i => ParcelCellOn(p.Bitmap, i % 64, i / 64)) * 16} m2)");
        foreach (var a in ParcelOthers().OrderBy(a => a.Id == DavidId ? 0 : 1))
        {
            string pos = a.Pos == null ? "position unknown (counts as on parcel)" : $"{a.Pos.Value.X:0},{a.Pos.Value.Y:0} cell {(int)(a.Pos.Value.X / 4)},{(int)(a.Pos.Value.Y / 4)}{(a.Slack > 0 ? " (region map, +/-1 m)" : "")}";
            sb.AppendLine($"  {(a.Id == DavidId ? "David" : ParcelNameOf(a.Id))}: {OnOurParcelTag(p, a)}  {pos}");
        }
        return sb.ToString().TrimEnd();
    }

    static string OnOurParcelTag(ParcelEntry p, ParcelAvatar a) => p == null ? "parcel ?" : (a.Pos == null || ParcelOn(p.Bitmap, a.Pos.Value.X, a.Pos.Value.Y, a.Slack)) ? "ON OUR PARCEL" : "elsewhere in region";

    // one line for 'nearby' / 'avatars': parcel + who is on it
    static string ParcelSummaryLine()
    {
        try
        {
            var (p, why) = ParcelHere();
            if (p == null) return $"PARCEL: unknown ({why}); the alone check counts everyone in the region\n";
            var others = ParcelOthers();
            var on = others.Where(a => a.Pos == null || ParcelOn(p.Bitmap, a.Pos.Value.X, a.Pos.Value.Y, a.Slack)).Select(a => a.Id == DavidId ? "David" : ParcelNameOf(a.Id)).ToList();
            return $"PARCEL: '{p.Name}' (local id {p.LocalId}); on our parcel: {(on.Count == 0 ? "nobody" : string.Join(", ", on))}; {others.Count - on.Count} elsewhere in the region\n";
        }
        catch { return ""; }
    }

    // MCP nearby: is this avatar on our parcel (null = parcel data not available)
    static bool? AvatarOnOurParcel(UUID id)
    {
        try
        {
            var (p, _) = ParcelHere(); if (p == null) return null;
            var a = ParcelOthers().FirstOrDefault(x => x.Id == id);
            return a.Id == UUID.Zero ? false : a.Pos == null || ParcelOn(p.Bitmap, a.Pos.Value.X, a.Pos.Value.Y, a.Slack);
        }
        catch { return null; }
    }
}
