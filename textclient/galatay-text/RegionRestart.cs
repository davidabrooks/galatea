// RegionRestart (added 2026-09-25, David's rule: when the region restarts she must go somewhere else or log out).
// Signals: AlertMessage with AlertInfo RegionRestartMinutes / RegionRestartSeconds (ExtraParams MINUTES|SECONDS, NAME),
//          legacy RESTART_X_MINUTES / RESTART_X_SECONDS, the "region you are in now is about to restart" text,
//          plus the same text from SYSTEM sources only (system chat, IM from the null key / "Second Life").
//          Avatar/object chat or IMs never trigger anything (standing rule: never relocate because of other people).
// Flow: save region/pos/seat (restart-return.json) -> stand (seat-off AO rule runs) -> GoHome -> fallback regions -> logout.
//       Then poll the region's MapBlock every ~60 s; >= 3 min after the restart time and region up -> teleport back and
//       re-sit (Naberrie: sit_home rule; elsewhere: the saved seat only). Give up after 45 min (stay where she is).
// Disconnect caused by a restart/shutdown -> wait 2 min, log in to 'home', then the same return flow.
// Commands: restart status | restart test [fast] | restart cancel | sit_home | invitem <item uuid>
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LibreMetaverse;
using LibreMetaverse.StructuredData;

namespace GalatayText;

public static partial class Program
{
    static readonly string RestartStateFile = Env("GT_RESTART_STATE", "/home/box/viewers/textclient/restart-return.json");
    static readonly UUID HomePillow = new("10d8a656-56bf-a9c6-0ff1-59ef1a6a47fd");   // her pillow on the pool rock (Naberrie)
    static readonly UUID DavidPillow = new("d942bdeb-368d-29f5-6df6-43ab5f72b496");  // David's pillow next to it
    const string HomeSeatRegion = "Naberrie";
    const string OwnerName = "David Nightingale";
    // zendo "zendo-porch-top": 23.4 m square rotated 45 deg, center (80.5,142.0) -> diamond |dx|+|dy| <= 16.55 (+2 m margin)
    static readonly UUID BuddhaCenterParcel = new("09ebd50e-ef40-8652-fc8d-42a65ab1aaa1"); // parcel 'The Buddha Center' (Naberrie)
    // 2026-09-26 (David): the small parcel also named 'The Buddha Center' at the Deer Park teleporter arrival (~144,91.6)
    // is an experimental landing zone and a full part of the Buddha Center (routes, end points, seats). Matched by parcel ID.
    static readonly UUID BuddhaCenterLandingParcel = new("49645b6c-4d68-b346-5272-2a2693415788");
    static bool IsBuddhaCenter(UUID id) => id != UUID.Zero && (id == BuddhaCenterParcel || id == BuddhaCenterLandingParcel);
    // bounds: 'allowed' is BuddhaCenterParcel in Naberrie (then both BC parcels count), else the parcel she started in
    static bool ParcelOk(UUID id, UUID allowed) => id != UUID.Zero && (id == allowed || (allowed == BuddhaCenterParcel && IsBuddhaCenter(id)));
    static readonly Dictionary<(int, int), UUID> parcelCache = new();
    // parcel id at a region-local point (RemoteParcelRequest cap), cached per 4 m cell
    static async Task<UUID> ParcelAt(Simulator sim, Vector3 pos)
    {
        var key = ((int)(pos.X / 4), (int)(pos.Y / 4));
        lock (parcelCache) if (parcelCache.TryGetValue(key, out var c)) return c;
        UUID id = UUID.Zero;
        try { id = await client.Parcels.RequestRemoteParcelIDAsync(pos, sim.Handle, sim.ID); } catch (Exception ex) { Log("sithome", "parcel lookup failed: " + ex.GetBaseException().Message); return UUID.Zero; }
        if (id != UUID.Zero) lock (parcelCache) parcelCache[key] = id;
        return id;
    }
    static bool InZendo(Vector3 p) => Math.Abs(p.X - 80.5f) + Math.Abs(p.Y - 142.0f) <= 18.6f;
    // evacuation fallbacks after GoHome (home = Firestorm Orientation)
    static readonly (string region, Vector3 pos)[] SafeRegions =
    {
        ("Firestorm Orientation", new Vector3(128, 128, 30)),
        ("Hippotropolis", new Vector3(128, 128, 30)),
        ("Ahern", new Vector3(128, 128, 30)),
    };
    static readonly Regex RestartText = new(@"(region|sim|simulator)[^.]{0,80}\brestart|restart[^.]{0,40}(region|sim)\b|RESTART_X_(MINUTES|SECONDS)|RegionRestart(Minutes|Seconds)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex InTime = new(@"(\d+)\s*(minute|min|second|sec)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public class RestartState
    {
        public string phase { get; set; } = "idle";   // idle | evacuating | waiting | returning | done | gave-up | logged-out | cancelled
        public string region { get; set; }
        public ulong handle { get; set; }
        public float x { get; set; } public float y { get; set; } public float z { get; set; }
        public string seat { get; set; }
        public string seat_name { get; set; }
        public DateTime warned { get; set; }
        public DateTime restart_at { get; set; }
        public DateTime earliest_return { get; set; }
        public DateTime give_up_at { get; set; }
        public string source { get; set; }
        public bool test { get; set; }
        public bool fast { get; set; }
        public string evacuated_to { get; set; }
        public int polls { get; set; }
        public bool saw_down { get; set; }
        public string last_status { get; set; }
        public List<string> history { get; set; } = new();
    }
    static RestartState rs = LoadRestartState();
    static readonly object rsLock = new();
    static DateTime lastRestartWarning = DateTime.MinValue;
    static readonly Dictionary<string, DateTime> restartLogSeen = new();
    static Task restartFlow;
    static CancellationTokenSource restartCts;
    static (string region, ulong handle, Vector3 pos, UUID seat, string seatName, DateTime at) lastKnown;
    static Task lastKnownTask;
    static int reconnectTries;
    static string startOverride;

    static RestartState LoadRestartState()
    {
        try { if (File.Exists(RestartStateFile)) return JsonSerializer.Deserialize<RestartState>(File.ReadAllText(RestartStateFile)) ?? new(); } catch { }
        return new();
    }
    static void SaveRestartState()
    {
        try { var tmp = RestartStateFile + ".tmp"; File.WriteAllText(tmp, JsonSerializer.Serialize(rs, new JsonSerializerOptions { WriteIndented = true })); File.Move(tmp, RestartStateFile, true); } catch { }
    }
    static void RLog(string msg)
    {
        Log("restart", msg);
        lock (rsLock) { rs.history.Add($"{DateTime.Now:HH:mm:ss} {msg}"); while (rs.history.Count > 60) rs.history.RemoveAt(0); }
        SaveRestartState();
    }
    static bool RestartActive => rs.phase is "evacuating" or "waiting" or "returning";

    static void HookRestart()
    {
        client.Self.AlertMessage += (s, e) =>
        {
            try
            {
                var nid = e.NotificationId ?? ""; var msg = e.Message ?? "";
                bool hit = nid.StartsWith("RegionRestart", StringComparison.OrdinalIgnoreCase) || msg.StartsWith("RESTART_X_", StringComparison.OrdinalIgnoreCase) || RestartText.IsMatch(msg);
                if (!hit) return;
                int? secs = null; string name = null;
                var ep = e.ExtraParams;
                if (ep != null)
                {
                    if (ep.ContainsKey("SECONDS")) secs = ep["SECONDS"].AsInteger();
                    else if (ep.ContainsKey("MINUTES")) secs = ep["MINUTES"].AsInteger() * 60;
                    if (ep.ContainsKey("NAME")) name = ep["NAME"].AsString();
                }
                secs ??= ParseSecs(msg);
                OnRestartWarning($"alert{(nid.Length > 0 ? " " + nid : "")}", msg + (ep != null ? " " + OSDParser.SerializeJsonString(ep) : ""), secs, name, false, false);
            }
            catch (Exception ex) { Log("restart", "alert parse error: " + ex.GetBaseException().Message); }
        };
        client.Self.ChatFromSimulator += (s, e) =>
        {
            if (e.SourceType != ChatSourceType.System || string.IsNullOrEmpty(e.Message) || !RestartText.IsMatch(e.Message)) return;
            OnRestartWarning("system chat", e.Message, ParseSecs(e.Message), null, false, false);
        };
        client.Self.IM += (s, e) =>
        {
            var im = e.IM;
            if (string.IsNullOrEmpty(im.Message) || !RestartText.IsMatch(im.Message)) return;
            bool system = im.FromAgentID == UUID.Zero || string.Equals(im.FromAgentName, "Second Life", StringComparison.OrdinalIgnoreCase);
            if (!system) return; // never act on avatar/object messages
            OnRestartWarning($"system IM ({im.Dialog})", im.Message, ParseSecs(im.Message), null, false, false);
        };
        if (lastKnownTask == null) lastKnownTask = Task.Run(LastKnownLoop);
    }
    static int? ParseSecs(string t)
    {
        var m = InTime.Match(t ?? ""); if (!m.Success || !int.TryParse(m.Groups[1].Value, out var n)) return null;
        return m.Groups[2].Value.StartsWith("m", StringComparison.OrdinalIgnoreCase) ? n * 60 : n;
    }

    static async Task LastKnownLoop()
    {
        while (!cts.IsCancellationRequested)
        {
            try
            {
                var sim = client?.Network?.CurrentSim;
                if (LoggedIn && sim != null && !string.IsNullOrEmpty(sim.Name))
                {
                    var (seat, sname) = CurrentSeat();
                    lastKnown = (sim.Name, sim.Handle, client.Self.SimPosition, seat, sname, DateTime.Now);
                }
            }
            catch { }
            try { await Task.Delay(10000, cts.Token); } catch { }
        }
    }
    static (UUID id, string name) CurrentSeat()
    {
        var sim = client.Network.CurrentSim; var lid = client.Self.SittingOn;
        if (sim == null || lid == 0 || !sim.ObjectsPrimitives.TryGetValue(lid, out var p) || p == null) return (UUID.Zero, null);
        var root = p;
        if (p.ParentID != 0 && sim.ObjectsPrimitives.TryGetValue(p.ParentID, out var r) && r != null) root = r;
        return (root.ID, root.Properties?.Name);
    }

    // ---- warning -> evacuation -----------------------------------------------------------------
    static void OnRestartWarning(string source, string text, int? secs, string regionName, bool test, bool fast)
    {
        var cur = client?.Network?.CurrentSim?.Name;
        var key = source + "|" + text;
        lock (restartLogSeen)
        {
            if (!test && restartLogSeen.TryGetValue(key, out var t0) && (DateTime.Now - t0).TotalSeconds < 20) return; // same packet twice
            restartLogSeen[key] = DateTime.Now;
        }
        Log("restart", $"warning via {source}: '{text}' secs={(secs?.ToString() ?? "?")} region_param={regionName ?? "-"} current={cur ?? "-"}{(test ? " (TEST)" : "")}");
        lastRestartWarning = DateTime.Now;
        if (!test) Notify("region_restart", "Second Life", UUID.Zero, $"region restart warning via {source}: '{text}' secs={(secs?.ToString() ?? "?")} region={regionName ?? cur ?? "-"}", null); // urgent webhook (immediate)
        if (!string.IsNullOrEmpty(regionName) && cur != null && !string.Equals(regionName, cur, StringComparison.OrdinalIgnoreCase))
        { Log("restart", $"warning is for '{regionName}', not for my region '{cur}': no action"); return; }
        if (!LoggedIn || cur == null) { Log("restart", "not in a region: no action"); return; }
        lock (rsLock)
        {
            if (RestartActive) { Log("restart", $"already handling a restart of {rs.region} (phase {rs.phase}): no second evacuation"); return; }
            var (seat, sname) = CurrentSeat();
            var now = DateTime.Now;
            var restartAt = now.AddSeconds(secs ?? 300);
            rs = new RestartState
            {
                phase = "evacuating", region = cur, handle = client.Network.CurrentSim.Handle,
                x = client.Self.SimPosition.X, y = client.Self.SimPosition.Y, z = client.Self.SimPosition.Z,
                seat = seat == UUID.Zero ? null : seat.ToString(), seat_name = sname,
                warned = now, restart_at = restartAt,
                earliest_return = fast ? now.AddSeconds(20) : restartAt.AddMinutes(3),
                give_up_at = now.AddMinutes(45), source = source, test = test, fast = fast,
            };
        }
        RLog($"saved return point {rs.region} <{rs.x:F1},{rs.y:F1},{rs.z:F1}> seat={(rs.seat ?? "-")} ({rs.seat_name ?? "-"}); restart expected {rs.restart_at:HH:mm:ss}, earliest return {rs.earliest_return:HH:mm:ss}, give up {rs.give_up_at:HH:mm:ss}");
        try { WanderOnRestart(); } catch { } // restart handling overrides the wander (Wander.cs)
        restartCts?.Cancel(); restartCts = new CancellationTokenSource();
        var ct = restartCts.Token;
        restartFlow = Task.Run(async () =>
        {
            try { if (await Evacuate(ct)) await ReturnLoop(ct); }
            catch (OperationCanceledException) { RLog("flow cancelled"); }
            catch (Exception ex) { RLog("flow error: " + ex.GetBaseException().Message); }
        });
    }

    static async Task<bool> Evacuate(CancellationToken ct)
    {
        followId = UUID.Zero; walkCts?.Cancel(); client.Self.AutoPilotCancel();
        if (client.Self.SittingOn != 0)
        {
            Interlocked.Increment(ref sitGen);
            client.Self.Stand();
            for (int i = 0; i < 12 && client.Self.SittingOn != 0; i++) await Task.Delay(250, ct);
            RLog($"stood up ({(client.Self.SittingOn == 0 ? "standing" : "still reported seated")}); seat-off rule re-wears the AO");
            await Task.Delay(3000, ct);
        }
        var origin = rs.handle;
        // 2026-09-27 (David: robe only at the Buddha Center): safety first - the evacuation still happens, but it is logged
        if (RobeMaybeWorn(out var robeHowE)) RLog($"WARNING evacuating while the VIOLETTE robe is worn ({robeHowE}); robe rule overridden for safety (region restart), she returns to the BC afterwards");
        bool ok = false;
        try { ok = await client.Self.GoHomeAsync(ct); } catch (Exception ex) when (ex is not OperationCanceledException) { RLog("go home exception: " + ex.GetBaseException().Message); }
        if (ok) await Task.Delay(1500, ct); // fresh position for the log
        if (ok && client.Network.CurrentSim?.Handle != origin) { rs.evacuated_to = $"home: {client.Network.CurrentSim?.Name} {Fmt(client.Self.SimPosition)}"; }
        else
        {
            RLog($"go home {(ok ? "landed in the same region" : "failed: " + client.Self.TeleportMessage)}; trying fallback regions");
            foreach (var (reg, pos) in SafeRegions)
            {
                ct.ThrowIfCancellationRequested();
                if (string.Equals(reg, rs.region, StringComparison.OrdinalIgnoreCase)) continue;
                await Task.Delay(3000, ct);
                bool t = false;
                try { t = await client.Self.TeleportAsync(reg, pos, ct); } catch (Exception ex) when (ex is not OperationCanceledException) { RLog($"teleport {reg} exception: {ex.GetBaseException().Message}"); }
                if (t && client.Network.CurrentSim?.Handle != origin) { rs.evacuated_to = $"fallback: {client.Network.CurrentSim?.Name} {Fmt(client.Self.SimPosition)}"; ok = true; break; }
                RLog($"teleport to {reg} failed: {client.Self.TeleportMessage}");
            }
            if (rs.evacuated_to == null)
            {
                rs.phase = "logged-out"; RLog("all evacuation teleports failed: logging out cleanly");
                await Shutdown("region restart: could not leave the region");
                return false;
            }
        }
        rs.phase = "waiting"; RLog($"evacuated to {rs.evacuated_to}");
        return true;
    }

    // ---- wait for the region to come back, then return ------------------------------------------
    static async Task<string> RegionMapStatus(ulong handle)
    {
        Utils.LongToUInts(handle, out var gx, out var gy);
        ushort x = (ushort)(gx / 256), y = (ushort)(gy / 256);
        var tcs = new TaskCompletionSource<GridRegion>(TaskCreationOptions.RunContinuationsAsynchronously);
        void H(object s, GridRegionEventArgs e) { if (e.Region.RegionHandle == handle) tcs.TrySetResult(e.Region); }
        var c = client; c.Grid.GridRegion += H;
        try
        {
            c.Grid.RequestMapBlocks(GridLayerType.Objects, x, y, x, y, true);
            if (await Task.WhenAny(tcs.Task, Task.Delay(10000)) != tcs.Task) return "no-reply";
            var r = tcs.Task.Result;
            return r.Access switch { SimAccess.Down => "down", SimAccess.NonExistent => "nonexistent", SimAccess.Unknown => "unknown", _ => "up:" + r.Access };
        }
        finally { c.Grid.GridRegion -= H; }
    }

    static async Task ReturnLoop(CancellationToken ct)
    {
        int tpFails = 0;
        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.Now;
            if (now > rs.give_up_at) { rs.phase = "gave-up"; RLog($"gave up: {rs.region} not reachable after 45 min; staying at {client?.Network?.CurrentSim?.Name ?? "(offline)"}"); return; }
            if (LoggedIn && client.Network.CurrentSim != null)
            {
                if (client.Network.CurrentSim.Handle == rs.handle && now >= rs.earliest_return)
                { rs.phase = "returning"; RLog($"already back in {rs.region} (e.g. login location)"); await ReSit(ct); return; }
                if (now >= rs.earliest_return)
                {
                    string st; try { st = await RegionMapStatus(rs.handle); } catch (Exception ex) { st = "error " + ex.GetBaseException().GetType().Name; }
                    rs.polls++; rs.last_status = $"{now:HH:mm:ss} {st}"; if (st is "down" or "nonexistent") rs.saw_down = true;
                    RLog($"poll {rs.polls}: {rs.region} map status {st}{(rs.saw_down ? " (was down earlier)" : "")}");
                    if (st.StartsWith("up"))
                    {
                        rs.phase = "returning"; SaveRestartState();
                        var pos = new Vector3(rs.x, rs.y, rs.z);
                        bool ok = false;
                        try { ok = await client.Self.TeleportAsync(rs.handle, pos, ct); } catch (Exception ex) when (ex is not OperationCanceledException) { RLog("return teleport exception: " + ex.GetBaseException().Message); }
                        if (ok && client.Network.CurrentSim?.Handle == rs.handle)
                        {
                            await Task.Delay(2000, ct); // SimPosition is stale right after the teleport
                            RLog($"teleported back to {client.Network.CurrentSim.Name} {Fmt(client.Self.SimPosition)}");
                            await ReSit(ct); return;
                        }
                        tpFails++; rs.phase = "waiting"; RLog($"return teleport failed ({tpFails}): {client.Self.TeleportMessage}");
                    }
                }
                else rs.last_status = $"{now:HH:mm:ss} waiting until {rs.earliest_return:HH:mm:ss}";
                SaveRestartState();
            }
            else rs.last_status = $"{now:HH:mm:ss} offline (reconnect pending)";
            var wait = rs.fast ? 15000 : 60000;
            if (now < rs.earliest_return) wait = (int)Math.Clamp((rs.earliest_return - now).TotalMilliseconds + 500, 1000, wait);
            await Task.Delay(wait, ct);
        }
    }

    static async Task ReSit(CancellationToken ct)
    {
        await Task.Delay(5000, ct); // let objects and avatars arrive
        string r;
        if (rs.seat == null) r = "was standing before the restart: staying standing";
        else if (string.Equals(client.Network.CurrentSim?.Name, HomeSeatRegion, StringComparison.OrdinalIgnoreCase)) r = await SitHome(ct);
        else
        {
            var id = new UUID(rs.seat);
            var sim = client.Network.CurrentSim;
            Primitive p = null;
            for (int i = 0; i < 20 && (p = sim.ObjectsPrimitives.Values.FirstOrDefault(x => x != null && x.ID == id)) == null; i++) await Task.Delay(1000, ct);
            if (p == null) r = $"saved seat {id} not found: standing";
            else if (Sitters(sim).TryGetValue(p.LocalID, out var l) && l.Any(n => n != "ME")) r = $"saved seat {id} occupied by {string.Join(",", l)}: standing";
            else r = "saved seat: " + await Exec("sit " + id);
        }
        rs.phase = "done"; RLog("return complete: " + r);
        WanderAfterRestartReturn();
    }

    // ---- sit_home (Naberrie seat rule) ----------------------------------------------------------
    // Her pillow 10d8a656 if BOTH rock pillows are free of other people; if either is taken by someone else, sit on
    // neither: pick another free seat INSIDE parcel 'The Buddha Center' away from avatars (>= 10 m if possible), never inside the zendo, preferring seats
    // walkable from here; no seat -> stand and log.
    static async Task<string> SitHome(CancellationToken ct = default)
    {
        var sim = client.Network.CurrentSim;
        if (sim == null || !string.Equals(sim.Name, HomeSeatRegion, StringComparison.OrdinalIgnoreCase)) return $"sit_home only applies in {HomeSeatRegion} (now in {sim?.Name ?? "-"})";
        Primitive home = null, dav = null;
        for (int i = 0; i < 20; i++)
        {
            home = sim.ObjectsPrimitives.Values.FirstOrDefault(x => x != null && x.ID == HomePillow);
            dav = sim.ObjectsPrimitives.Values.FirstOrDefault(x => x != null && x.ID == DavidPillow);
            if (home != null && dav != null) break;
            await Task.Delay(1000, ct);
        }
        await Task.Delay(2000, ct); // seated avatars arrive with their seats
        var sit = Sitters(sim);
        // "someone else" = anyone except me and David (the owner may sit on his own pillow next to her)
        static bool Other(string n) => n != "ME" && !string.Equals(n, OwnerName, StringComparison.OrdinalIgnoreCase);
        string Occ(Primitive p) => p != null && sit.TryGetValue(p.LocalID, out var l) && l.Any(Other) ? string.Join(",", l.Where(Other)) : null;
        var occHome = Occ(home); var occDav = Occ(dav);
        if (home != null && client.Self.SittingOn == home.LocalID && occDav == null) { Log("sithome", "already on her pillow"); return "already seated on her pillow"; }
        if (home != null && occHome == null && occDav == null)
        {
            var r0 = await Exec("sit " + HomePillow);
            Log("sithome", $"rock pillows free -> her pillow: {r0}");
            if (client.Self.SittingOn != 0) return "her pillow: " + r0;
        }
        var why = home == null ? "her pillow not in view" : occHome != null || occDav != null ? $"rock pillows taken (hers: {occHome ?? "free"}, David's: {occDav ?? "free"})" : "sit on her pillow failed";
        Log("sithome", why + " -> looking for another quiet seat (not the zendo)");
        if (client.Self.SittingOn != 0) { Interlocked.Increment(ref sitGen); client.Self.Stand(); await Task.Delay(1500, ct); }

        var me = client.Self.SimPosition;
        var avs = sim.ObjectsAvatars.Values.Where(a => a != null && a.ID != client.Self.AgentID).Select(a => PositionHelper.GetAvatarPosition(sim, a)).Where(p => p != Vector3.Zero).ToList();
        var cands = sim.ObjectsPrimitives.Values.Where(p => p != null && p.ParentID == 0 && p.PrimData.PCode == PCode.Prim && p.ID != HomePillow && p.ID != DavidPillow
                                                          && HDist(p.Position, me) <= 60 && !InZendo(p.Position)).ToList();
        await EnsureProperties(sim, cands);
        sit = Sitters(sim);
        var scored = cands.Where(p => !sit.ContainsKey(p.LocalID) && SeatWords.Any(w => (p.Properties?.Name ?? "").Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Select(p => { var md = avs.Count == 0 ? 999f : avs.Min(a => Vector3.Distance(a, p.Position)); return (p, md, d: HDist(p.Position, me), dz: Math.Abs(p.Position.Z - me.Z)); })
            .OrderByDescending(t => t.md >= 10f).ThenByDescending(t => t.dz <= 4f).ThenByDescending(t => Math.Min(t.md, 30f) - 0.3f * t.d).ToList();
        Log("sithome", $"{scored.Count} candidate seat(s): " + string.Join("; ", scored.Take(6).Select(t => $"'{t.p.Properties?.Name}' {t.p.ID} {V(t.p.Position)} {t.d:F0}m away, nearest avatar {t.md:F0}m")));
        int attempts = 0, checkedParcels = 0;
        bool anyQuiet = false;
        foreach (var t in scored)
        {
            ct.ThrowIfCancellationRequested();
            if (attempts >= 3 || checkedParcels >= 20) break;
            checkedParcels++;
            var pid = await ParcelAt(sim, t.p.Position);
            if (!IsBuddhaCenter(pid)) { Log("sithome", $"skip '{t.p.Properties?.Name}' {t.p.ID} {V(t.p.Position)}: parcel {pid} is not The Buddha Center"); continue; }
            if (t.md >= 10f) anyQuiet = true;
            else if (anyQuiet) continue; // a quieter in-parcel seat was already tried
            attempts++;
            if (t.d > 3f)
            {
                var dest = t.p.Position + Vector3.Normalize(new Vector3(me.X - t.p.Position.X, me.Y - t.p.Position.Y, 0) + new Vector3(0.001f, 0, 0)) * 1.2f;
                bool walked;
                try { using var wc = CancellationTokenSource.CreateLinkedTokenSource(ct); wc.CancelAfter(TimeSpan.FromMinutes(3)); walked = await WalkPath(new List<Vector3> { dest }, wc.Token, 1.5f); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { walked = false; client.Self.AutoPilotCancel(); }
                Log("sithome", $"walk to '{t.p.Properties?.Name}' {(walked ? "arrived" : "did not arrive")} at {V(client.Self.SimPosition)}");
                if (!walked) continue;
            }
            var r = await Exec("sit " + t.p.ID);
            Log("sithome", $"sit '{t.p.Properties?.Name}' {t.p.ID}: {r}");
            if (client.Self.SittingOn != 0) return $"{why}; seated on '{t.p.Properties?.Name}' {t.p.ID} (nearest avatar {t.md:F0} m)";
        }
        var near = avs.Count == 0 ? -1 : avs.Min(a => Vector3.Distance(a, client.Self.SimPosition));
        Log("sithome", $"no suitable seat: standing at {V(client.Self.SimPosition)} (nearest avatar {(near < 0 ? "none" : near.ToString("F0") + " m")})");
        return $"{why}; no suitable seat -> standing at {V(client.Self.SimPosition)}";
    }

    // ---- disconnect by restart -> reconnect -----------------------------------------------------
    static bool RestartDisconnect(NetworkManager.DisconnectType reason, string message)
    {
        if (reason == NetworkManager.DisconnectType.ClientInitiated) return false;
        if (reason == NetworkManager.DisconnectType.SimShutdown) return true;
        if (!string.IsNullOrEmpty(message) && Regex.IsMatch(message, @"restart|shut ?down|shutting down", RegexOptions.IgnoreCase)) return true;
        return (DateTime.Now - lastRestartWarning).TotalMinutes < 30 || RestartActive;
    }
    // called from the Disconnected handler; true = reconnect scheduled (process keeps running)
    static bool MaybeScheduleReconnect(NetworkManager.DisconnectType reason, string message)
    {
        if (!RestartDisconnect(reason, message)) return false;
        if (++reconnectTries > 3) { Log("restart", "disconnected again after 3 reconnects: giving up"); return false; }
        var lk = lastKnown;
        lock (rsLock)
        {
            if (!RestartActive && lk.region != null)
            {
                var now = DateTime.Now;
                rs = new RestartState
                {
                    phase = "waiting", region = lk.region, handle = lk.handle, x = lk.pos.X, y = lk.pos.Y, z = lk.pos.Z,
                    seat = lk.seat == UUID.Zero ? null : lk.seat.ToString(), seat_name = lk.seatName, warned = now, restart_at = now,
                    earliest_return = now.AddMinutes(3), give_up_at = now.AddMinutes(45), source = $"disconnect {reason}", evacuated_to = "(disconnected)",
                };
            }
        }
        var delay = TimeSpan.FromMinutes(2 * reconnectTries);
        RLog($"disconnected by region restart/shutdown ({reason}: {message}); logging in to 'home' in {delay.TotalMinutes:F0} min (try {reconnectTries}/3)");
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delay, cts.Token); } catch { return; }
            startOverride = "home";
            var r = await LoginAsync();
            RLog($"reconnect: {r}");
            if (r.StartsWith("OK"))
            {
                reconnectTries = 0;
                if (RestartActive && (restartFlow == null || restartFlow.IsCompleted))
                {
                    restartCts?.Cancel(); restartCts = new CancellationTokenSource(); var ct = restartCts.Token;
                    restartFlow = Task.Run(async () => { try { await ReturnLoop(ct); } catch (OperationCanceledException) { } catch (Exception ex) { RLog("flow error: " + ex.GetBaseException().Message); } });
                }
            }
            else if (!MaybeScheduleReconnect(NetworkManager.DisconnectType.NetworkTimeout, "reconnect failed: " + r))
            { shuttingDown = true; if (ExitOnLogout) cts.Cancel(); }
        });
        return true;
    }

    // ---- commands -------------------------------------------------------------------------------
    static async Task<string> RestartCmds(string cmd, string[] a)
    {
        if (cmd == "sit_home") return LoggedIn ? await SitHome() : "not logged in";
        if (cmd == "invitem")
        {
            if (!LoggedIn) return "not logged in";
            if (a.Length != 1 || !UUID.TryParse(a[0], out var id)) return "usage: invitem <inventory item uuid>";
            var inStore = client.Inventory.Store != null && client.Inventory.Store.Contains(id);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            InventoryItem it = null; string err = null;
            try { using var t = new CancellationTokenSource(30000); it = await client.Inventory.FetchItemAsync(id, client.Self.AgentID, t.Token); } catch (Exception ex) { err = ex.GetBaseException().GetType().Name; }
            return it == null ? $"item {id}: NOT returned by FetchItem after {sw.Elapsed.TotalSeconds:F1}s{(err != null ? " (" + err + ")" : "")}; in local store: {inStore}"
                              : $"item {id}: '{it.Name}' type {it.InventoryType}/{it.AssetType} folder {it.ParentUUID} (fetched in {sw.Elapsed.TotalSeconds:F1}s; in local store before fetch: {inStore}); not attached";
        }
        var sub = a.Length > 0 ? a[0].ToLowerInvariant() : "status";
        switch (sub)
        {
            case "status":
            {
                var sb = new StringBuilder();
                sb.AppendLine($"phase={rs.phase} region={rs.region ?? "-"} pos=<{rs.x:F1},{rs.y:F1},{rs.z:F1}> seat={rs.seat ?? "-"} ({rs.seat_name ?? "-"}) test={rs.test} fast={rs.fast}");
                if (rs.warned != default) sb.AppendLine($"warned={rs.warned:HH:mm:ss} restart_at={rs.restart_at:HH:mm:ss} earliest_return={rs.earliest_return:HH:mm:ss} give_up={rs.give_up_at:HH:mm:ss} source={rs.source}");
                sb.AppendLine($"evacuated_to={rs.evacuated_to ?? "-"} polls={rs.polls} saw_down={rs.saw_down} last={rs.last_status ?? "-"} flow_running={(restartFlow != null && !restartFlow.IsCompleted)}");
                sb.AppendLine($"now: logged_in={LoggedIn} region={client?.Network?.CurrentSim?.Name ?? "-"} last_warning={(lastRestartWarning == DateTime.MinValue ? "-" : lastRestartWarning.ToString("HH:mm:ss"))} reconnect_tries={reconnectTries}");
                foreach (var h in rs.history.TakeLast(15)) sb.AppendLine("  " + h);
                return sb.ToString().TrimEnd();
            }
            case "test":
            {
                if (!LoggedIn) return "not logged in";
                bool fast = a.Length > 1 && a[1].Equals("fast", StringComparison.OrdinalIgnoreCase);
                OnRestartWarning("test", "SIMULATED: The region you are in now is about to restart. If you stay in this region you will be logged out.", fast ? 0 : 300, null, true, fast);
                return $"simulated restart warning ({(fast ? "fast: return after ~20 s once the region answers up" : "normal: return >= 3 min after the 5 min restart time")}); watch 'restart status' / [restart] log lines";
            }
            case "cancel":
                restartCts?.Cancel();
                if (RestartActive) { rs.phase = "cancelled"; RLog("cancelled by command (staying where she is)"); }
                return "restart flow cancelled; phase=" + rs.phase;
        }
        return "usage: restart status | restart test [fast] | restart cancel";
    }
}
