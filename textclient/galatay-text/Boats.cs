// Boats.cs (2026-10-09 08:46 PT, David): "add the different seats on our boats to your wandering, but it works a little
// differently with them. You can't really walk to them or walk away. Walk to the middle of the pier and create a landmark
// to use with them. To sit on a boat, go to that LM and then sit on a boat, and then choose a random seat. Exclude the
// submarine. Each pose on a boat will move you to another seat on the boat. When you want to exit the boat, teleport to
// the pier LM."
// Follow-ups the same morning:
//  - NEVER press boat driving / control buttons (Drive, Start, Engine, Pilot, Captain, Anchor, Lights, Rez, Options,
//    SYSTEM* ...). Poses come only from a Singles/Solo set: the root step is an ALLOWLIST of Singles/Solo submenus, and
//    inside it only plain pose labels ("sitting 5", "chill 3"). No Singles set -> skip that boat (teleport off).
//  - Do NOT stand before leaving a boat (standing on a moving boat can drop her in the water): teleport straight to
//    the pier spot while still seated; the teleport unseats her.
// Boats sit in the seat list's "boats" array (mode "boat"); the pier spot is "boat_spot" {landmark, pos} (landmark
// 'Peronaut Pier' created at the pier centre line 216.6,33.6,24.5). Boats count as the beach level for the level dwell.
using System.Text.Json;
using System.Text.RegularExpressions;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    internal const string BoatMode = "boat";
    internal sealed record BoatSpot(string Landmark, Vector3 Pos);
    internal static bool IsBoat(HomeSeatInfo i) => i?.Mode == BoatMode;

    static readonly Regex BoatSubmarineRx = new(@"submarin\w*|submers\w*|(?<![a-z])sub(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // pure: never the submarine
    internal static bool BoatExcluded(string name) => BoatSubmarineRx.IsMatch(name ?? "");

    // pure: top-level "boat_spot": {"landmark": "...", "pos": [x, y, z]}
    internal static BoatSpot ParseBoatSpot(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("boat_spot", out var b) || b.ValueKind != JsonValueKind.Object) return null;
        if (!b.TryGetProperty("pos", out var pv) || pv.ValueKind != JsonValueKind.Array || pv.GetArrayLength() < 3) return null;
        var lm = b.TryGetProperty("landmark", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
        return new BoatSpot(lm, new Vector3((float)pv[0].GetDouble(), (float)pv[1].GetDouble(), (float)pv[2].GetDouble()));
    }
    static BoatSpot LoadBoatSpot()
    {
        try { return File.Exists(HomeSeatsFile) ? ParseBoatSpot(File.ReadAllText(HomeSeatsFile)) : null; }
        catch (Exception ex) { WLog("boat spot: " + ex.Message); return null; }
    }

    // pure: how she gets to the pier spot. Already on the beach level with a path-graph route: walk; otherwise teleport.
    internal static string BoatApproach(string myLevel, bool routeOk) => myLevel == LevelBeach && routeOk ? "walk" : "teleport";

    // pure: leaving a boat is a teleport while still seated (never stand first)
    internal static bool BoatStandBeforeLeave => false;

    // ---- menu: allowlist only -----------------------------------------------------------------------------
    // Root: only submenus named exactly like a Singles / Solo set (SINGLE*, Singles*, Solo*, SinglesSet*, Single Poses*).
    static readonly Regex BoatSoloMenuRx = new(@"^(?:singles?|solo)(?:\s*(?:set|sets|poses?))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Inside the Singles/Solo set: only plain pose labels: a word or two plus an optional number ("sitting 5", "chill 3").
    static readonly Regex BoatPoseLabelRx = new(@"^[a-z]+(?: [a-z]+)?(?: ?\d{1,2})?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Safety veto on top of the allowlist (a pose label never needs these words); also no standing poses on a boat.
    static readonly Regex BoatControlWordRx = new(@"(?<![a-z])(driv\w*|start\w*|engine\w*|motor\w*|ignit\w*|pilot\w*|captain\w*|helm\w*|steer\w*|throttle|anchor\w*|lights?|lamps?|rez\w*|options?|settings?|system|menu|stop|park|lock\w*|sail\w*|speed|gears?|horn|music|radio|camera|hud|reset|dock\w*|moor\w*|go|run|stand\w*|unsit|access|owner|eject|kick|texture\w*|colou?rs?|home\w*|skip)(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static bool BoatIsSoloSubmenu(string raw)
    {
        var t = (raw ?? "").Trim();
        return t.EndsWith("*") && BoatSoloMenuRx.IsMatch(t.TrimEnd('*').Trim());
    }
    internal static bool BoatIsPoseButton(string raw)
    {
        var t = (raw ?? "").Trim();
        if (t.Length == 0 || t.EndsWith("*") || t.Contains('[') || t.Contains(']')) return false;
        if (!BoatPoseLabelRx.IsMatch(t)) return false;
        if (BoatControlWordRx.IsMatch(t)) return false;
        return !PoseIsCouplesNamed(t) && !PoseIsAdultMenu(t) && !PoseIsMale(t);
    }
    // pure: the AVsitter menu text says which submenu is open ("Sitter 0>SINGLE"); true = inside a Singles/Solo set
    internal static bool BoatMenuInSolo(string menuText)
    {
        if (string.IsNullOrEmpty(menuText)) return false;
        var m = Regex.Match(menuText, @">\s*([^>\r\n\[]+?)\s*(?:\r?\n|$|\[)");
        return m.Success && BoatSoloMenuRx.IsMatch(m.Groups[1].Value.Trim().TrimEnd('*'));
    }
    // pure: root step = a random Singles/Solo submenu; null = this boat has no Singles set (skip it)
    internal static string BoatPickRoot(IReadOnlyList<string> labels, Random r)
    {
        var c = (labels ?? new List<string>()).Where(BoatIsSoloSubmenu).ToList();
        return c.Count == 0 ? null : c[r.Next(c.Count)];
    }
    // pure: pose step inside the Singles/Solo set = a random allowlisted pose (not the current one when there is a choice)
    internal static string BoatPickPose(IReadOnlyList<string> labels, string current, Random r)
    {
        var c = (labels ?? new List<string>()).Where(BoatIsPoseButton).ToList();
        if (c.Count > 1 && !string.IsNullOrEmpty(current)) c = c.Where(x => !x.Trim().Equals(current.Trim(), StringComparison.OrdinalIgnoreCase)).DefaultIfEmpty(c[0]).ToList();
        return c.Count == 0 ? null : c[r.Next(c.Count)];
    }

    // ---- live ---------------------------------------------------------------------------------------------
    static uint wOnBoatLocal;   // LocalID of the boat she sat on through the wander (0 = none)

    static bool NearBoatSpot(Vector3 me, BoatSpot s) => HDist(me, s.Pos) <= 2.0f && Math.Abs(me.Z - s.Pos.Z) <= 2.0f;

    // teleport to the exact pier spot (same region). Seated or not: never stands first (the teleport unseats her).
    static async Task<bool> BoatTeleportToSpot(BoatSpot s, string why)
    {
        var r = await Exec(FormattableString.Invariant($"teleport {HomeWanderRegion} {s.Pos.X:F2} {s.Pos.Y:F2} {s.Pos.Z:F2}"));
        // a same-region teleport can report "Teleport started" as a failure while it lands fine: judge by position (up to 6 s)
        bool ok = false;
        for (int i = 0; i < 12 && !ok; i++) { await Task.Delay(500); ok = InPeronaut && NearBoatSpot(client.Self.SimPosition, s); }
        WLog($"BOAT teleport to the pier spot{(s.Landmark != null ? $" ('{s.Landmark}')" : "")} ({why}): {r}; {(ok ? "there" : "NOT there")} at {P3(client.Self.SimPosition)}");
        return ok;
    }

    // to the pier spot: walk if she is already down on the beach level, else teleport; bikini on the spot if needed
    static async Task<bool> BoatGoToSpot(Graph g, BoatSpot s, CancellationToken ct)
    {
        var me = client.Self.SimPosition;
        if (!NearBoatSpot(me, s))
        {
            var (pts, err) = GraphRouteTo(g, me, s.Pos);
            var how = BoatApproach(HomeLevelOf(me), err == null && client.Self.SittingOn == 0);
            WLog($"BOAT: to the pier spot {P3(s.Pos)} from {P3(me)} by {how}{(err != null ? $" (no route: {err})" : "")}");
            bool there = false;
            if (how == "walk")
            {
                wanderPhase = "walking to the pier spot (boats)";
                legCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wLegGoal = s.Pos; wLegHasGoal = true;
                try
                {
                    var (ok, msg) = await FollowPoly(new Poly(pts), new RouteOpts { Label = "home wander to the pier spot (boats)", Idle = true }, legCts.Token);
                    if (!ok && msg.Contains("AO not active")) { wanderPause = "ao"; wPausedAt = DateTime.Now; return false; }
                    there = NearBoatSpot(client.Self.SimPosition, s);
                    WLog($"BOAT: walk to the pier spot {(there ? "arrived" : "ended short")} ({msg})");
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                { client.Self.AutoPilotCancel(); WLog("BOAT: walk to the pier spot interrupted (" + (wanderPause ?? "cancel") + ")"); return false; }
                finally { wLegHasGoal = false; }
            }
            if (wanderPause != null) return false;
            if (!there && !await BoatTeleportToSpot(s, "before a boat sit")) return false;
        }
        // 2026-10-08 approved: at the beach without passing through the bedroom -> bikini on the spot
        if (!BikiniWorn())
        {
            beachOutfitBusy = true;
            try
            {
                var remember = await RememberForBeach();
                wanderPhase = "putting the bikini on (pier)";
                WLog($"BIKINI on the spot at the pier (did not pass through the bedroom): remember '{remember ?? "-"}'");
                var r = await BikiniOn();
                RememberNamedOutfit("Bikini");
                beachRememberedOutfit = remember; beachMode = true; PersistBeachState();
                WLog("bikini on: " + r.Replace("\n", " | ")[..Math.Min(300, r.Length)]);
            }
            catch (Exception ex) { WLog("pier bikini failed: " + ex.GetBaseException().Message); }
            finally { beachOutfitBusy = false; }
        }
        return true;
    }

    // a random seat on the boat = a random pose from its Singles/Solo set (each pose moves her to another seat)
    // (ok, message); ok=false means: no Singles set / no menu / no pose -> leave the boat
    static async Task<(bool ok, string msg)> BoatPose(Primitive boat, string name, CancellationToken ct)
    {
        lastSeatPoseMenuAt = DateTime.Now; seatPoseMissingSince = null;
        var t0 = DateTime.Now; client.Self.Touch(boat.LocalID);
        var d = await WaitSeatDialog(boat, t0, 6000, ct);
        if (d == null) return (false, "no menu after touch");
        var path = new List<string>();
        if (!BoatMenuInSolo(d.Message))
        {
            var root = BoatPickRoot(d.ButtonLabels, wRnd);
            if (root == null) return (false, $"no Singles/Solo set in [{string.Join(" | ", d.ButtonLabels ?? new())}]");
            int ri = (d.ButtonLabels ?? new()).IndexOf(root);
            var t1 = DateTime.Now;
            client.Self.ReplyToScriptDialog(d.Channel, ri, root, d.ObjectID);
            path.Add(root.Trim());
            d = await WaitSeatDialog(boat, t1, 6000, ct);
            if (d == null) return (false, $"'{root}' did not open");
            if (!BoatMenuInSolo(d.Message) && !(d.ButtonLabels ?? new()).Any(BoatIsPoseButton)) return (false, $"'{root}' opened no pose list");
        }
        var pose = BoatPickPose(d.ButtonLabels, PoseCurrent(d.Message), wRnd);
        if (pose == null) return (false, $"no allowlisted pose in [{string.Join(" | ", d.ButtonLabels ?? new())}]");
        ClearKeptPoseCopies();
        client.Self.ReplyToScriptDialog(d.Channel, d.ButtonLabels.IndexOf(pose), pose, d.ObjectID);
        path.Add(pose.Trim());
        wLastPosePath = path.ToList(); wLastSoloPosePath = path.ToList();
        wLastPose = $"{DateTime.Now:HH:mm:ss} '{name}' {boat.ID}: {string.Join(" > ", path)} [boat, solo]";
        return (true, string.Join(" > ", path));
    }

    // leave the boat: teleport to the pier spot while still seated (never stand first)
    static async Task BoatLeave(string why)
    {
        var s = LoadBoatSpot();
        wOnBoatLocal = 0;
        if (s == null) { WLog($"BOAT leave ({why}): no boat_spot in the seat list; standing instead"); client.Self.Stand(); return; }
        wanderPhase = "teleporting off the boat to the pier";
        if (!await BoatTeleportToSpot(s, "leaving the boat: " + why))
        {
            await Task.Delay(3000);
            if (!await BoatTeleportToSpot(s, "leaving the boat (retry): " + why)) WLog("BOAT leave: teleport failed twice; she is still at " + P3(client.Self.SimPosition));
        }
        // the seat-off rule re-wears the AO after the teleport unseats her; give it a moment before the next walk
        try { if (!await AoWaitActive(15000, CancellationToken.None)) WLog("BOAT leave: AO not active yet (the walk guard waits/restores)"); } catch { }
    }

    // still on a boat when the loop wants to move on (after a pause, a restart of the loop ...)
    static async Task<bool> LeaveBoatIfOnOne(string why)
    {
        if (wOnBoatLocal == 0) return false;
        if (client.Self.SittingOn != wOnBoatLocal) { if (client.Self.SittingOn == 0 && OnBoatWater(client.Self.SimPosition)) await BoatLeave(why + " (unseated in the water)"); wOnBoatLocal = 0; return false; }
        await BoatLeave(why);
        return true;
    }
    static bool OnBoatWater(Vector3 p) => p.Z < 22.5f && p.Y < 50f && (p.X < 214.0f || p.X > 218.6f);
}
