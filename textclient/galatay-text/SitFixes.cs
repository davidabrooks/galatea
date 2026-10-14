using LibreMetaverse;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GalatayText;

// 2026-10-13 David (fixes from 2026-10-12 16:28-16:34):
// 1. The outfit-zone tick never swaps into the bikini in place outside the bedroom: it walks to the bedroom over the
//    path graph, changes there, then walks back to where she was.
// 2. sit_near / goto_avatar David (and sit_lap) apply the join-David beach rule first (bedroom + bikini), also while the
//    wander is paused / on hold.
// 3. sit_near never targets non-seat parts (Nerenzo beach chair - parasol / - table); on 'No room to sit' or no sit
//    response it tries the next nearest free real seat near the avatar.
// 4. sit_lap <avatar>: sit on the avatar's own seat, then pick a lap pose (known table per furniture, else a
//    CUDDLES*/couples menu and a button whose name suggests lap / on you).
public static partial class Program
{
    // ---- 1. outfit-zone: bedroom only ---------------------------------------------------------------------
    // pure: what the zone tick does with a "bikini-on". Only in the bedroom (or outside Peronaut, no home) does it swap
    // in place; a running manual walk or a moving home wander is left alone (they apply the bedroom rule themselves).
    internal static string ZoneBikiniRoute(string region, bool inBedroom, bool manualWalkActive, bool homeWanderOwns, bool retryBlocked)
    {
        if (!string.Equals(region, "Peronaut", StringComparison.OrdinalIgnoreCase)) return "bikini-on";
        if (inBedroom) return "bikini-on";
        if (homeWanderOwns) return "wait-wander";
        if (manualWalkActive) return "wait-walk";
        if (retryBlocked) return "wait-retry";
        return "bedroom-first";
    }
    static DateTime zoneBedroomRetryAt = DateTime.MinValue;
    static string zoneHouseOutfitOverride;   // login on the beach: the daily pick to wear back in the house
    static bool ManualWalkActive => walkTask != null && !walkTask.IsCompleted;

    // the tick's "bedroom-first": stand, walk to the bedroom change spot over the graph, bikini on there, walk back
    static async Task ZoneBikiniViaBedroom(string why)
    {
        var sim = client.Network.CurrentSim; var back = client.Self.SimPosition;
        if (AndroidWorn()) { Log("outfit-zone", $"{why}: android body: no bikini change"); zoneBedroomRetryAt = DateTime.UtcNow.AddMinutes(10); return; }
        zoneBedroomRetryAt = DateTime.UtcNow.AddMinutes(3);
        Log("outfit-zone", $"{why}: not in the bedroom (at {back.X:F1},{back.Y:F1},{back.Z:F1}): walking to the bedroom to change, never in place");
        if (client.Self.SittingOn != 0) { client.Self.Stand(); await Task.Delay(2500); }
        var g = LoadGraph(sim.Name);
        if (g == null) { Log("outfit-zone", "no path graph: no change"); return; }
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        await WalkToChangeSpot(g, "to put the bikini on", cts.Token, manual: true);
        var p = client.Self.SimPosition;
        if (!InBedroom(p) && !IndoorsAtHome(p)) { Log("outfit-zone", $"did not reach the bedroom (at {p.X:F1},{p.Y:F1},{p.Z:F1}): no change (retry in 3 min)"); return; }
        var remember = zoneHouseOutfitOverride ?? await RememberForBeach(); zoneHouseOutfitOverride = null;
        var r = await BikiniOn();
        RememberNamedOutfit("Bikini");
        beachRememberedOutfit = remember; beachMode = true; beachModeDeferRestoreUntil = DateTime.UtcNow.AddMinutes(8); PersistBeachState();
        Log("outfit-zone", "bikini on in the bedroom: " + r.Replace("\n", " | ")[..Math.Min(300, r.Length)]);
        var hr = await HomeGraphWalkTo(back, 0.8f, "back after changing", cts.Token);
        Log("outfit-zone", hr == null ? $"back at {P3(client.Self.SimPosition)}" : "walk back: " + hr);
    }

    // ---- 3. real seats only + next seat on 'No room' ------------------------------------------------------
    static readonly Regex NonSeatPart = new(@"(?<![a-z])(parasols?|umbrellas?|tables?|lamps?|sunshades?|shades?|canopy|awning|cooler|tray)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // pure: a sit target by name (seat word present, and not a "<furniture> - parasol/table" part)
    internal static bool IsRealSeatName(string name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) return false;
        if (NonSeatPart.IsMatch(n)) return false;
        return SeatWords.Any(w => n.Contains(w, StringComparison.OrdinalIgnoreCase));
    }
    // pure: an alert that means the sit was refused
    internal static bool IsNoRoomAlert(string msg) => !string.IsNullOrEmpty(msg) &&
        (msg.Contains("No room to sit", StringComparison.OrdinalIgnoreCase) || msg.Contains("can't sit", StringComparison.OrdinalIgnoreCase) || msg.Contains("cannot sit", StringComparison.OrdinalIgnoreCase));
    static volatile string lastSitAlert; static DateTime lastSitAlertAt = DateTime.MinValue;
    internal static void NoteAlertForSit(string msg) { if (IsNoRoomAlert(msg)) { lastSitAlert = msg; lastSitAlertAt = DateTime.UtcNow; } }

    // pure: choose among candidates (name, dist, id), skipping excluded / non-seats, nearest first
    internal static int PickSeatIndex(IReadOnlyList<(string name, float d, UUID id)> cands, ICollection<UUID> exclude)
    {
        int best = -1; float bd = float.MaxValue;
        for (int i = 0; i < cands.Count; i++)
        {
            var c = cands[i];
            if (exclude != null && exclude.Contains(c.id)) continue;
            if (!IsRealSeatName(c.name)) continue;
            if (c.d < bd) { bd = c.d; best = i; }
        }
        return best;
    }

    // sit and wait for the sim to seat her: true seated; false on a 'No room' alert or no response within ~5 s
    static async Task<(bool ok, string r)> TrySitOn(Primitive seat)
    {
        var t0 = DateTime.UtcNow;
        var r = await Exec($"sit {seat.ID}");
        for (int i = 0; i < 25; i++)
        {
            await Task.Delay(200);
            if (client.Self.SittingOn != 0) return (true, r);
            if (lastSitAlertAt >= t0) return (false, r + $"; refused: {lastSitAlert}");
        }
        return (client.Self.SittingOn != 0, r + (client.Self.SittingOn != 0 ? "" : "; no sit response"));
    }

    // walk (path graph at home) next to the seat and sit; on refusal try the next nearest free real seat (max 4)
    static async Task<(bool ok, string msg, Primitive seat)> SitNearWithFallback(Vector3 apos, string avName, (Primitive p, float d)? first, CancellationToken ct)
    {
        var tried = new HashSet<UUID>(); var notes = new List<string>();
        var seat = first;
        for (int attempt = 0; attempt < 4 && seat != null; attempt++)
        {
            var sp = seat.Value.p; var nm = sp.Properties?.Name ?? sp.ID.ToString();
            tried.Add(sp.ID);
            var dest = sp.Position;
            if (attempt > 0)
            {
                Log("walk", $"sit_near: next seat '{nm}' {sp.ID} at {V(dest)}, {seat.Value.d:F1} m from {avName}");
                if (InPeronaut && HDist(client.Self.SimPosition, dest) > 2.0f)
                {
                    var hr = await HomeGraphWalkTo(dest, 1.2f, $"sit_near {avName} (next seat)", ct);
                    if (hr != null) { notes.Add($"'{nm}': {hr}"); seat = await FindFreeSeatNear(apos, 3f, tried); continue; }
                }
            }
            var (ok, r) = await TrySitOn(sp);
            if (ok) return (true, (notes.Count > 0 ? string.Join("; ", notes) + "; " : "") + r, sp);
            notes.Add($"'{nm}': {r}");
            Log("walk", $"sit_near: '{nm}' did not take her ({r}); trying the next nearest free seat");
            seat = await FindFreeSeatNear(apos, 3f, tried);
        }
        return (false, string.Join("; ", notes) + (seat == null ? "; no other free seat within 3 m" : ""), null);
    }

    // ---- 4. lap poses --------------------------------------------------------------------------------------
    // known lap poses per furniture (name prefix -> AVsitter path). David approved 'on you' 2026-10-12 16:34.
    internal static readonly List<(string seatPrefix, string[] path)> LapPoses = new()
    {
        ("Nerenzo beach chair - adult", new[] { "CUDDLES*", "on you" }),
    };
    internal static string[] LapPoseFor(string seatName) =>
        LapPoses.FirstOrDefault(t => (seatName ?? "").Trim().StartsWith(t.seatPrefix, StringComparison.OrdinalIgnoreCase)).path;

    static readonly Regex LapLeafRx = new(@"(?<![a-z])(lap|on\s*(you|him|me|top)|sit\s*on|straddl\w*|lap\s*sit)(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex LapMenuRx = new(@"(?<![a-z])(cuddl\w*|couples?|lap|together|pg\s*couples|romanc\w*|snuggl\w*)(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // pure: index of the submenu to enter for a lap pose (cuddles first, then couples), -1 none. Adult/sexual menus never.
    internal static int PickLapMenu(IReadOnlyList<string> labels)
    {
        if (labels == null) return -1;
        int best = -1, bestRank = 9;
        for (int i = 0; i < labels.Count; i++)
        {
            var b = labels[i] ?? ""; if (!b.TrimEnd().EndsWith("*") || PoseIsControl(b) || PoseIsAdultMenu(b) || PoseIsMale(b)) continue;
            var bare = PoseBare(b);
            int rank = Regex.IsMatch(bare, @"lap", RegexOptions.IgnoreCase) ? 0 : Regex.IsMatch(bare, @"cuddl", RegexOptions.IgnoreCase) ? 1 : LapMenuRx.IsMatch(bare) ? 2 : 9;
            if (rank < bestRank) { bestRank = rank; best = i; }
        }
        return best;
    }
    // pure: index of a leaf whose name suggests a lap pose, -1 none
    internal static int PickLapLeaf(IReadOnlyList<string> labels)
    {
        if (labels == null) return -1;
        for (int i = 0; i < labels.Count; i++)
        {
            var b = labels[i] ?? "";
            if (b.TrimEnd().EndsWith("*") || b.Trim().StartsWith("[") || PoseIsBackButton(b) || PoseIsSexualNamed(b) || PoseIsMale(b)) continue;   // not PoseIsControl: it drops "on you" (on/off words)
            if (LapLeafRx.IsMatch(PoseBare(b))) return i;
        }
        return -1;
    }

    // pure: the root seat object an avatar sits on (its ParentID, walked to the link root), 0 when standing
    internal static uint RootSeatOf(uint avatarParent, Func<uint, uint> parentOf)
    {
        if (avatarParent == 0) return 0;
        uint cur = avatarParent;
        for (int i = 0; i < 4; i++) { var up = parentOf(cur); if (up == 0) return cur; cur = up; }
        return cur;
    }

    static async Task<string> LapPose(Primitive seat, string seatName, CancellationToken ct)
    {
        var known = LapPoseFor(seatName);
        if (known != null)
        {
            var r = await SeatPosePath(seat, seatName, known.ToList(), ct);
            if (r.Contains("restored") || r.Contains("already")) return "lap pose (known): " + r;
            Log("lap", "known lap path failed: " + r + "; trying the menu");
        }
        var t0 = DateTime.Now; client.Self.Touch(seat.LocalID);
        var d = await WaitSeatDialog(seat, t0, 5000, ct);
        if (d == null) return $"'{seatName}': no pose menu";
        d = await SeatPoseBackToRoot(seat, d, ct);
        int leaf = PickLapLeaf(d.ButtonLabels);
        if (leaf < 0)
        {
            int m = PickLapMenu(d.ButtonLabels);
            if (m < 0) return $"'{seatName}': no cuddles/lap menu in [{string.Join(" | ", d.ButtonLabels)}]";
            var menu = d.ButtonLabels[m]; var t1 = DateTime.Now;
            client.Self.ReplyToScriptDialog(d.Channel, m, menu, d.ObjectID);
            d = await WaitSeatDialog(seat, t1, 5000, ct);
            if (d == null) return $"'{seatName}': '{menu}' did not open";
            leaf = PickLapLeaf(d.ButtonLabels);
            if (leaf < 0) return $"'{seatName}': no lap-like pose in {menu} [{string.Join(" | ", d.ButtonLabels)}]; tell me which one";
            var pick = d.ButtonLabels[leaf];
            client.Self.ReplyToScriptDialog(d.Channel, leaf, pick, d.ObjectID);
            wLastPosePath = new() { menu.Trim(), pick.Trim() };
            return $"lap pose: {menu} > {pick}";
        }
        var p2 = d.ButtonLabels[leaf];
        client.Self.ReplyToScriptDialog(d.Channel, leaf, p2, d.ObjectID);
        wLastPosePath = new() { p2.Trim() };
        return $"lap pose: {p2}";
    }

    static async Task<string> SitLapCmd(string rest)
    {
        if (rest.Length == 0) return "usage: sit_lap <avatar>";
        if (WanderBlocksManualWalk) return "wander is running: 'wander pause' (or 'wander stop') first";
        var hit0 = FindAvatarPos(rest); if (hit0 == null) return $"avatar '{rest}' not in view";
        string prep = NamesDavid(rest) || hit0.Value.av.ID == DavidId ? await JoinDavidBeachPrep("sit_lap") : null;
        var res = StartWalk($"sit_lap {hit0.Value.av.Name}", async ct =>
        {
            var hit = FindAvatarPos(rest); if (hit == null) return $"avatar '{rest}' no longer in view";
            var (av, apos) = hit.Value; var sim = Sim;
            uint rootId = RootSeatOf(av.ParentID, id => sim.ObjectsPrimitives.TryGetValue(id, out var q) && q != null ? q.ParentID : 0);
            if (rootId == 0 || !sim.ObjectsPrimitives.TryGetValue(rootId, out var seat) || seat == null) return $"{av.Name} is not sitting: no lap to sit on";
            if (seat.Properties == null) await EnsureProperties(sim, new() { seat });
            var name = seat.Properties?.Name ?? seat.ID.ToString();
            if (InPeronaut && HDist(client.Self.SimPosition, seat.Position) > 2.0f)
            {
                var hr = await HomeGraphWalkTo(seat.Position, 1.2f, $"sit_lap {av.Name}", ct);
                if (hr != null) return $"could not walk to {av.Name}: {hr}";
            }
            var (ok, r) = await TrySitOn(seat);
            if (!ok) return $"could not sit on {av.Name}'s seat '{name}': {r}";
            await Task.Delay(2500);
            // roles first (David 2026-10-13): me on his lap, never him on mine; then the lap pose; then verify again
            string pre = av.ID == DavidId ? await EnsureCoupleRoles(seat, name, "sit_lap (before pose)", true, ct) : null;
            var lp = await LapPose(seat, name, ct);
            Log("lap", lp);
            string post = av.ID == DavidId ? await EnsureCoupleRoles(seat, name, "sit_lap (after pose)", true, ct) : null;
            return $"on {av.Name}'s seat '{name}'; " + (pre != null ? pre + "; " : "") + lp + (post != null ? "; " + post : "");
        });
        return prep == null ? res : prep + "\n" + res;
    }
}
