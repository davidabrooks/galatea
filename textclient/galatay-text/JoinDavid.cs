using LibreMetaverse;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GalatayText;

// 2026-10-12 David: setting off to join David (goto_avatar David, follow David, his lure) while he is in the Peronaut
// beach zone: human form only, she first walks to the bedroom and puts the bikini on, then goes to him. beachMode is set
// as on a wander beach trip, so the way back up changes back in the bedroom (wander / outfit-zone tick). Androids stay.
// 2026-11-11 David: "come here" (nearby or IM) must work at any distance or sim. Same region: walk, or teleport if a
// path is not practical. Other region: teleport to a friend-map location when we have map rights; otherwise IM him
// asking for a teleport offer (never send a lure request). Only David Nightingale.
public static partial class Program
{
    // pure
    internal static bool JoinDavidNeedsBikini(bool androidWorn, bool bikiniWorn, string region, Vector3? davidPos)
        => !androidWorn && !bikiniWorn && davidPos != null && string.Equals(region, "Peronaut", StringComparison.OrdinalIgnoreCase)
           && OutfitZoneFor("Peronaut", davidPos.Value, false) == "beach";

    internal static bool NamesDavid(string s) => !string.IsNullOrWhiteSpace(s) &&
        (s.Trim().Equals("david", StringComparison.OrdinalIgnoreCase) || s.Trim().Equals("me", StringComparison.OrdinalIgnoreCase)
         || s.Trim().StartsWith("david nightingale", StringComparison.OrdinalIgnoreCase));

    static Vector3? DavidPos()
    {
        var a = Avatars().FirstOrDefault(t => t.av.ID == DavidId);
        if (a.av != null) return a.pos;
        try { foreach (var kv in coarseBy.ToArray()) if (kv.Key == client.Network.CurrentSim?.Handle && kv.Value.TryGetValue(DavidId, out var p)) return p; } catch { }
        return null;
    }

    // null = nothing to do
    static async Task<string> JoinDavidBeachPrep(string what)
    {
        try
        {
            if (!LoggedIn) return null;
            var dp = DavidPos();
            if (!JoinDavidNeedsBikini(AndroidWorn(), BikiniWorn(), client.Network.CurrentSim?.Name, dp)) return null;
            if (client.Self.SittingOn != 0) { StandFaceFirst(); await Task.Delay(2500); }
            var g = LoadGraph("Peronaut");
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            Log("outfit", $"join David on the beach ({what}): bedroom + bikini first (David at {dp.Value.X:F0},{dp.Value.Y:F0})");
            await BikiniIndoorsIfBeachBound(g, dp.Value, null, $"join David ({what})", cts.Token, manual: true);
            return BikiniWorn() ? "joined-David prep: bikini on in the bedroom" : "joined-David prep: bikini change did not happen (see log)";
        }
        catch (Exception ex) { var m = "join David prep: " + ex.GetBaseException().Message; Log("outfit", m); return m; }
    }
    // "come here" and similar stay come-here. "follow me" / "come with me" / "walk with me" / bare "follow" are the real follow (not "following").
    static readonly System.Text.RegularExpressions.Regex ComeRx = new(@"(?<![a-z])(come\s+(here|over|down|up|back|to\s+me|join|sit|see|outside|inside|to\s+the\s+\w+)|get\s+over\s+here|join\s+me|over\s+here\s+(babe|baby|honey|love|please)?|come\s*!|come$)(?![a-z])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    static readonly System.Text.RegularExpressions.Regex FollowRx = new(@"(?<![a-z])(follow(\s+me)?|come\s+with\s+me|walk\s+with\s+me)(?![a-z])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    // pure: an explicit request to come to the speaker (a "can you hear me?" check is not). "follow me" is not come-here.
    internal static bool IsComeRequest(string text) => !string.IsNullOrWhiteSpace(text) && ComeRx.IsMatch(text.Trim());
    internal static bool IsFollowRequest(string text) => !string.IsNullOrWhiteSpace(text) && FollowRx.IsMatch(text.Trim());

    // 2026-11-11: same-region come-here (not home) walks when he is within this range and the path is walkable.
    internal const float ComeWalkMaxM = 64f;
    // After a same-region teleport, a landing-point / telehub miss farther than this is not success: walk the rest.
    internal const float TeleportLandSlackM = 5f;

    internal static bool ComeHerePreferWalk(bool inHome, float horizDist, bool pathOk) =>
        !inHome && horizDist <= ComeWalkMaxM && pathOk;

    internal static bool TeleportMissedSpot(float horizDist, float slackM = TeleportLandSlackM) => horizDist > slackM;

    // 2026-11-11 David: come-here from him at any distance or sim. Never a lure request.
    internal const float OtherComeLocalMaxM = 20f;   // pre-457 local-chat cap for everyone else
    internal const float OtherComeImMaxM = 40f;
    internal const string DavidComeHereAskTpIm = "I can't get to you from here, could you send me a TP?";
    internal enum ComeHereAction { None, Approach, TeleportToLocation, AskForTeleportOffer }
    internal readonly record struct ComeHerePlan(ComeHereAction action, string why);

    /// <summary>
    /// How to answer a come request. Only David Nightingale gets unlimited range and other-sim travel.
    /// Other speakers keep the old nearby caps. <paramref name="sameRegion"/> is true, false, or null when unknown.
    /// Other-sim with no map location: ask him (IM) for a teleport offer — never send a lure request.
    /// </summary>
    internal static ComeHerePlan PlanComeHere(bool isDavid, bool isComeRequest, bool im,
        bool? sameRegion, bool positionKnown, float dist, bool mapLocationKnown)
    {
        if (!isComeRequest) return new(ComeHereAction.None, "not a come request");
        if (!isDavid)
        {
            if (sameRegion == false) return new(ComeHereAction.None, "other region (only David)");
            if (!positionKnown || dist < 0) return new(ComeHereAction.None, "speaker position unknown");
            var cap = im ? OtherComeImMaxM : OtherComeLocalMaxM;
            if (dist > cap) return new(ComeHereAction.None, "too far for a non-David come request");
            return new(ComeHereAction.Approach, "nearby speaker");
        }
        if (sameRegion == true) return new(ComeHereAction.Approach, "David is in this region");
        if (sameRegion != false && !im) return new(ComeHereAction.Approach, "David in local chat");
        if (mapLocationKnown) return new(ComeHereAction.TeleportToLocation, "David mapped in another region");
        return new(ComeHereAction.AskForTeleportOffer, "David not reachable; ask him for a teleport offer");
    }

    static DateTime lastDavidComeAt = DateTime.MinValue;
    static readonly object davidComeLock = new();

    // Hook / instant-reply entry: David + a come request only. Deduped for a couple of seconds.
    static void NoteDavidComeHere(string source)
    {
        try
        {
            if (!LoggedIn) return;
            lock (davidComeLock)
            {
                if (DateTime.UtcNow - lastDavidComeAt < TimeSpan.FromSeconds(2)) { Log("come", $"come-here from David ({source}): already starting"); return; }
                lastDavidComeAt = DateTime.UtcNow;
            }
            _ = Task.Run(() => StartDavidComeHere(source));
        }
        catch (Exception ex) { Log("come", "come-here note error: " + ex.Message); }
    }

    static async Task StartDavidComeHere(string source)
    {
        try
        {
            var pos = DavidPos();
            bool same = pos != null;
            float dist = pos != null ? Vector3.Distance(pos.Value, client.Self.SimPosition) : -1f;
            bool canMap = false;
            try { canMap = client.Friends.FriendList.TryGetValue(DavidId, out var f) && f.CanSeeThemOnMap; } catch { }
            var im = source.StartsWith("IM", StringComparison.OrdinalIgnoreCase);
            var plan = PlanComeHere(true, true, im, same ? true : im ? false : null, pos != null, dist, canMap);
            Log("come", $"come-here from David ({source}): {plan.action} ({plan.why})" +
                (pos != null ? $"; he is {dist:F0} m away in this region" : canMap ? "; map rights yes" : "; not in this region, no map"));

            if (plan.action == ComeHereAction.AskForTeleportOffer)
            {
                AskDavidForTeleportOffer(source);
                return;
            }

            if (WanderOn) StopWander("David asked me to come");
            StopRoamIfOn("David asked me to come");

            if (plan.action == ComeHereAction.Approach)
            {
                await GoToDavidSameRegion(pos, source);
                MaybeStartFollowAfterCome(source);
                return;
            }

            if (plan.action == ComeHereAction.TeleportToLocation)
            {
                var mapped = await MapDavidLocation();
                if (mapped == null)
                {
                    Log("come", "friend map did not return David's location; asking him for a teleport offer");
                    AskDavidForTeleportOffer(source + ", map failed");
                    return;
                }
                var (handle, loc) = mapped.Value;
                if (handle == (client.Network.CurrentSim?.Handle ?? 0))
                {
                    await GoToDavidSameRegion(loc, source + ", mapped in this region");
                    MaybeStartFollowAfterCome(source);
                    return;
                }
                await TeleportToDavid(handle, loc, source);
                MaybeStartFollowAfterCome(source);
            }
        }
        catch (Exception ex) { Log("come", "come-here error: " + ex.GetBaseException().Message); }
    }

    static void AskDavidForTeleportOffer(string source)
    {
        // David 2026-11-11: do not send a lure request. Ask him in IM to offer a TP (already auto-accepted).
        client.Self.InstantMessage(DavidId, DavidComeHereAskTpIm);
        try { TypingStop("asked David for a teleport offer"); } catch { }
        Log("come", $"IM to David ({source}): '{DavidComeHereAskTpIm}' (no lure request)");
    }

    static async Task<(ulong handle, Vector3 loc)?> MapDavidLocation()
    {
        var tcs = new TaskCompletionSource<(ulong, Vector3)>();
        EventHandler<FriendFoundReplyEventArgs> h = (s, e) =>
        {
            if (e.AgentID == DavidId) tcs.TrySetResult((e.RegionHandle, e.Location));
        };
        client.Friends.FriendFoundReply += h;
        try
        {
            client.Friends.MapFriend(DavidId);
            var winner = await Task.WhenAny(tcs.Task, Task.Delay(8000));
            if (winner != tcs.Task) return null;
            return await tcs.Task;
        }
        finally { client.Friends.FriendFoundReply -= h; }
    }

    static async Task GoToDavidSameRegion(Vector3? pos, string source)
    {
        var prep = await JoinDavidBeachPrep("come here");
        if (prep != null) Log("come", prep);
        if (pos == null) pos = DavidPos();
        if (pos == null) { Log("come", $"come-here ({source}): David is not visible in this region yet; waiting"); return; }
        var me = client.Self.SimPosition;
        float d = Vector3.Distance(pos.Value, me);
        if (client.Self.SittingOn != 0)
        {
            StandFaceFirst();
            await Task.Delay(2500);
            me = client.Self.SimPosition;
        }
        if (d <= 3.5f && Math.Abs(pos.Value.Z - me.Z) < 1.5f)
        {
            if (client.Self.SittingOn == 0) client.Self.Movement.TurnToward(pos.Value);
            Log("come", $"come-here ({source}): already {d:F1} m from David; facing him");
            return;
        }
        if (InPeronaut)
        {
            var g = LoadGraph(HomeWanderRegion);
            me = client.Self.SimPosition;
            var (pts, err) = PlanHomeApproach(g, me, pos.Value, 2.5f);
            if (err == null && pts != null && pts.Count >= 2)
            {
                var poly = new Poly(pts);
                var o = new RouteOpts { Label = "come to David" };
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
                Log("come", $"come-here ({source}): walking the home path {poly.Len:F0} m to David at {pos.Value.X:F0},{pos.Value.Y:F0},{pos.Value.Z:F0}");
                var (ok, msg) = await FollowPoly(poly, o, cts.Token);
                Log("come", $"come-here walk {(ok ? "done" : "ended")}: {msg}");
                if (ok) return;
            }
            else Log("come", $"come-here ({source}): no home path ({err ?? "none"}); teleporting in-region");
        }
        else
        {
            me = client.Self.SimPosition;
            float hd = HDist(me, pos.Value);
            bool pathOk = ComePathWalkable(me, pos.Value);
            if (ComeHerePreferWalk(false, hd, pathOk))
            {
                var dest = new Vector3(pos.Value.X, pos.Value.Y, ComeSupportZ(pos.Value, me.Z));
                Log("come", $"come-here ({source}): walking {hd:F0} m to David at {pos.Value.X:F0},{pos.Value.Y:F0},{dest.Z:F0} (same region, within {ComeWalkMaxM:F0} m)");
                using var wcts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                bool walked = await WalkPath(new System.Collections.Generic.List<Vector3> { dest }, wcts.Token, 1.5f);
                Log("come", walked
                    ? $"come-here walk done at {V(client.Self.SimPosition)}"
                    : $"come-here walk did not arrive (at {V(client.Self.SimPosition)}); teleporting");
                if (walked) return;
            }
            else Log("come", $"come-here ({source}): not walking ({hd:F0} m, path {(pathOk ? "ok" : "blocked")}); teleporting in-region");
        }
        var sim = client.Network.CurrentSim?.Name;
        if (string.IsNullOrEmpty(sim)) { Log("come", "come-here: no current region; cannot teleport"); return; }
        var block = RobeTpBlock(sim, RobeMaybeWorn(out _), false);
        if (block != null) { Log("come", "come-here same-region teleport " + block); return; }
        Log("come", $"come-here ({source}): same-region teleport to David at {sim} {pos.Value.X:F0},{pos.Value.Y:F0},{pos.Value.Z:F0}");
        var requested = pos.Value;
        var okTp = await client.Self.TeleportAsync(sim, requested);
        Log("come", okTp
            ? $"come-here: teleported to {client.Network.CurrentSim?.Name} {client.Self.SimPosition}"
            : "come-here: same-region teleport failed: " + client.Self.TeleportMessage);
        if (okTp) await WalkRemainderIfLandingMissed(requested, "come-here");
    }

    static float ComeSupportZ(Vector3 at, float avatarZ)
    {
        try
        {
            var sim = client.Network.CurrentSim;
            IEnumerable<RoamPrim> prims = sim != null ? RoamPrimsFromSim(sim, avatarZ) : System.Array.Empty<RoamPrim>();
            return RoamSupportZ(at.X, at.Y, GroundAny(at.X, at.Y), avatarZ, prims);
        }
        catch { return at.Z; }
    }

    static bool ComePathWalkable(Vector3 from, Vector3 to)
    {
        try
        {
            var sim = client.Network.CurrentSim; if (sim == null) return false;
            float len = HDist(from, to); if (len < 0.5f) return true;
            var prims = RoamPrimsFromSim(sim, from.Z);
            float water = sim.WaterHeight;
            uint sx = sim.SizeX == 0 ? 256u : (uint)sim.SizeX;
            uint sy = sim.SizeY == 0 ? 256u : (uint)sim.SizeY;
            bool hasW = false, hasE = false, hasS = false, hasN = false;
            foreach (var (nsim, _) in ViewSims())
            {
                if (nsim == null || nsim == sim || !nsim.Connected) continue;
                if (!RoamNeighborAllowed(sim.SimOwner, nsim.SimOwner, true, SimGeo.Adjacent(sim.Handle, nsim.Handle))) continue;
                var dir = SimGeo.Dir(sim.Handle, nsim.Handle);
                if (dir.Contains('W')) hasW = true;
                if (dir.Contains('E')) hasE = true;
                if (dir.Contains('S')) hasS = true;
                if (dir.Contains('N')) hasN = true;
            }
            float dx = to.X - from.X, dy = to.Y - from.Y;
            float zAt(float d)
            {
                float t = d / len;
                return RoamSupportZ(from.X + dx * t, from.Y + dy * t, GroundAny(from.X + dx * t, from.Y + dy * t), from.Z, prims);
            }
            bool waterAt(float d) => zAt(d) < water;
            bool edgeAt(float d)
            {
                float t = d / len;
                return RoamTooCloseToVoidEdge(from.X + dx * t, from.Y + dy * t, sx, sy, 8f, hasW, hasE, hasS, hasN);
            }
            return RoamPathCheckAlong(len, zAt, waterAt, edgeAt) == RoamPathReject.Ok;
        }
        catch { return false; }
    }

    // After any same-region teleport: if the parcel landing point / telehub dropped her more than TeleportLandSlackM
    // from the requested spot, walk the rest of the way. Shared by come-here and the teleport command.
    static async Task WalkRemainderIfLandingMissed(Vector3 requested, string why)
    {
        try
        {
            var me = client.Self.SimPosition;
            float hd = HDist(me, requested);
            if (!TeleportMissedSpot(hd)) return;
            var dest = new Vector3(requested.X, requested.Y, ComeSupportZ(requested, me.Z));
            Log("come", $"{why}: landing point left me {hd:F1} m from the requested spot {V(requested)} (at {V(me)}); walking the rest");
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            bool ok = await WalkPath(new System.Collections.Generic.List<Vector3> { dest }, cts.Token, 1.5f);
            Log("come", ok
                ? $"{why}: walked the rest, now at {V(client.Self.SimPosition)}"
                : $"{why}: walk after landing miss stopped at {V(client.Self.SimPosition)}");
        }
        catch (Exception ex) { Log("come", why + " landing-miss walk error: " + ex.GetBaseException().Message); }
    }

    static volatile bool followDavidAfterCome;

    static void MaybeStartFollowAfterCome(string source)
    {
        if (!followDavidAfterCome) return;
        FollowDavid(source + ", after come-here");
    }

    // Same as the `follow` tool, targeting David at the default standoff. Works by UUID so he does not have to resolve by name.
    static void FollowDavid(string source)
    {
        followDavidAfterCome = false;
        try
        {
            if (WanderOn) StopWander("follow David");
            StopRoamIfOn("follow David");
            if (client.Self.SittingOn != 0) StandFaceFirst();
            // Keep a per-call session standoff if one is already set; otherwise the default (1.5 m) applies.
            followId = DavidId;
            followName = DavidName;
            Log("follow", $"following David ({source}) at {FollowDist:F1} m standoff");
        }
        catch (Exception ex) { Log("follow", "follow David error: " + ex.Message); }
    }

    // Hook / instant-reply entry: "follow me" and variants. If he is in view, start follow now; otherwise come-here first, then follow.
    static void NoteDavidFollow(string source)
    {
        try
        {
            if (!LoggedIn) return;
            if (FindAvatarAnySim(DavidId) != null)
            {
                FollowDavid(source);
                return;
            }
            followDavidAfterCome = true;
            Log("follow", $"David is not in view ({source}); coming to him first, then following");
            NoteDavidComeHere(source + ", then follow");
        }
        catch (Exception ex) { Log("follow", "follow-David note error: " + ex.Message); }
    }

    static async Task TeleportToDavid(ulong handle, Vector3 loc, string source)
    {
        // Destination region name is unknown from the handle alone; robe rule uses the name when we have it.
        // A teleport to another region while the robe is worn is blocked (same as 'teleport').
        var destName = handle == (client.Network.CurrentSim?.Handle ?? 0) ? client.Network.CurrentSim?.Name : "another region";
        var block = RobeTpBlock(destName, RobeMaybeWorn(out var how), false);
        if (block != null)
        {
            Log("come", $"come-here teleport blocked ({how}): {block}; asking David for a teleport offer instead");
            AskDavidForTeleportOffer(source + ", robe block");
            return;
        }
        followId = UUID.Zero;
        Log("come", $"come-here ({source}): teleport to David's mapped position (handle {handle}, {loc.X:F0},{loc.Y:F0},{loc.Z:F0})");
        var ok = await client.Self.TeleportAsync(handle, loc);
        Log("come", ok
            ? $"come-here: teleported to {client.Network.CurrentSim?.Name} {client.Self.SimPosition}"
            : "come-here: teleport failed: " + client.Self.TeleportMessage + "; asking him for a teleport offer");
        if (!ok) AskDavidForTeleportOffer(source + ", teleport failed");
    }
}
