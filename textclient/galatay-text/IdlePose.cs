// IdlePose.cs (2026-10-21 10:55-11:07, David): after standing idle a while she settles into a pose.
// - Collar visible (Stealth off) OR a visible leash chain: a random N° 9 collar pose from cutie/coy/kneel/nadu/pretty/shy/tower
//   (never the same twice in a row), via touch collar -> Pose -> page with ▸ until the label shows (pages in notes.md).
//   Get up = the same Pose menu's STOP, verified by the pose animation disappearing.
// - Collar hidden (Stealth on, Android*) and no visible leash: the Martha AO's 'force sitg' button (ground sit), mapped in
//   run/ao-groundsit.txt as "<face> <u>,<v>" (a toggle: pressed again to stand). Never while leashed.
// - Before ANY movement (walk/nav/autopilot, a walker start, follow, teleport, leash attach/unleash, a force-sit or RLV
//   sit, an outfit change, David walking away, logout) the pose is cleared first. On login a leftover pose is cleared.
// - The wander-off ("forgot I'm leashed") never fires while posed. State persists in run/idle-pose.json.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LibreMetaverse;
namespace GalatayText;
public static partial class Program
{
    internal static readonly string[] IdleCollarPoses = { "cutie", "coy", "kneel", "nadu", "pretty", "shy", "tower" };
    // pure (tested): always from the list, never the last one again
    internal static string PickIdlePose(string last, double r01)
    {
        var pool = IdleCollarPoses.Where(p => !string.Equals(p, last, StringComparison.OrdinalIgnoreCase)).ToArray();
        return pool[(int)(Math.Clamp(r01, 0, 0.999999) * pool.Length)];
    }
    // pure (tested): which idle pose: collar pose when the collar or the leash chain can be seen, else the AO ground sit
    internal static string IdlePoseKind(bool collarVisible, bool leashVisible) => collarVisible || leashVisible ? "collar" : "groundsit";
    // pure (tested): idle threshold with jitter: base * 0.75..1.25 (default base 240 s -> 3-5 min)
    internal static double IdleThresholdSec(double baseSec, double r01) => IdleWaitMin + (IdleWaitMax - IdleWaitMin) * Math.Clamp(r01, 0, 1);   // David 11:26: real variance, fresh each time (base kept for API)
    internal const double IdleWaitMin = 90, IdleWaitMax = 300;
    internal readonly record struct IdleIn(bool enabled, bool leashed, bool walkerDriving, bool following, bool sittingOnObject, bool moving,
        bool forceSat, bool sitLocked, bool busy, bool dancing, double idleSec, double thresholdSec, bool collarVisible, bool leashVisible, bool groundSitMapped);
    // pure (tested): enter which pose now (null = not now)
    internal static string IdleEnter(IdleIn s)
    {
        if (!s.enabled || s.walkerDriving || s.following || s.sittingOnObject || s.moving || s.forceSat || s.sitLocked || s.busy || s.dancing) return null;   // dancing is never idle
        if (s.idleSec < s.thresholdSec) return null;
        var k = IdlePoseKind(s.collarVisible, s.leashVisible);
        if (k == "groundsit" && (s.leashed || !s.groundSitMapped)) return null;   // never the AO ground sit on a leash
        return k;
    }
    // pure (tested): must the pose be cleared now?
    internal static string IdleMustExit(string kind, bool following, bool sittingOnObject, bool moving, bool forceSat, bool leashed, bool davidWalkingAway, bool walkerWants)
        => kind == null ? null
         : following ? "follow" : sittingOnObject || forceSat ? "sit" : moving ? "moving"
         : walkerWants ? "walker" : null;   // leash on/off alone never ends it (David 11:31)
    // pure (tested): the collar Pose menu's "Playing: <pose>" header -> pose, or null for (none)/unknown
    internal static string CollarPlaying(string menuText)
    {
        var m = System.Text.RegularExpressions.Regex.Match(menuText ?? "", @"Playing:\s*(?<p>[^\r\n\[]+)");
        if (!m.Success) return null; var p = m.Groups["p"].Value.Trim();
        return p.Length == 0 || p.StartsWith("(none", StringComparison.OrdinalIgnoreCase) ? null : p;
    }
    // pure (tested): the button for a pose on this page (case-insensitive), or null
    internal static string CollarPoseButton(IEnumerable<string> buttons, string pose) =>
        (buttons ?? Enumerable.Empty<string>()).FirstOrDefault(b => string.Equals(b?.Trim(), pose, StringComparison.OrdinalIgnoreCase));
    // pure (tested): "<face> <u>,<v>" for the AO's 'force sitg' button
    // pure (tested): "[<link>] <face> <u>,<v>" for the AO's 'force sitg' button (link defaults to 1).
    // Found 2026-10-21: link 2 'HUD2016 V03' face 4 uv 0.88,0.50 (the side list runs along u, reversed).
    internal static (int link, int face, string st)? AoGroundSitSpot(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text ?? "", @"^\s*(?:(?<l>\d+)\s+)?(?<f>\d+)\s+(?<u>[01]?\.\d+|[01]),(?<v>[01]?\.\d+|[01])\s*$", System.Text.RegularExpressions.RegexOptions.Multiline);
        return m.Success ? (m.Groups["l"].Success ? int.Parse(m.Groups["l"].Value) : 1, int.Parse(m.Groups["f"].Value), $"{m.Groups["u"].Value},{m.Groups["v"].Value}") : null;
    }
    // pure (tested): is a dance playing? (dance ball / dance HUD / AO DANCES mode / any anim named like a dance)
    internal static bool IdleDancing(bool aoDanceMode, IEnumerable<string> animNamesOrSources) =>
        aoDanceMode || (animNamesOrSources ?? Enumerable.Empty<string>()).Any(n => n != null &&
            System.Text.RegularExpressions.Regex.IsMatch(n, @"danc|disco|dj\b|club|salsa|tango|waltz|twerk|ballroom", System.Text.RegularExpressions.RegexOptions.IgnoreCase));

    // live: any playing anim whose name or source object/attachment looks like a dance (dance ball, dance HUD, AO DANCES)
    static bool IdleDancingNow()
    {
        try { Dictionary<UUID, (int seq, UUID src)> cur; lock (animLock) cur = new(ownAnims);
              return IdleDancing(false, cur.SelectMany(kv => new[] { AnimName(kv.Key), SrcDesc(kv.Value.src) })); }
        catch { return false; }
    }
    // pure (tested): how long to hold one pose before changing to another (David 11:29: random 3-10 min)
    internal static double IdleRotateSec(double r01) => 180 + 420 * Math.Clamp(r01, 0, 1);
    // pure (tested): change pose now? collar poses and (2026-10-23) AO ground sits; never while dancing
    internal static bool IdleShouldRotate(string kind, double heldSec, double rotateSec, bool dancing) => (kind == "collar" || kind == "groundsit") && !dancing && heldSec >= rotateSec;
    // ---- Martha AO ground-sit chooser (found 2026-10-23): link 5 'HUD2016 V05' face 4, the hidden pop-out by SITS GROUNDS.
    // Its top half 'SELECT' (st 0.40,0.75) opens "Select the Sitting on Ground animation to use:" with numbered entries
    // (VAMOSITG11,12,13,21..27 = 10 sits), buttons 1-8 + NEXT, then 9,10 + PREV. Picking one switches the playing sit at once
    // (owner-say "Current Sitting on Ground Animation::<name>"). The bottom half 'HIGH' is not used. Override: run/ao-groundsit-select.txt.
    internal const string AoSitSelectDefault = "5 4 0.40,0.75";
    // pure (tested): the numbered entries of the chooser text: (number, name)
    internal static List<(int n, string name)> AoSitEntries(string msg) =>
        System.Text.RegularExpressions.Regex.Matches(msg ?? "", @"(?m)^\s*(\d+)\.\s*(\S+)").Select(m => (int.Parse(m.Groups[1].Value), m.Groups[2].Value)).ToList();
    // pure (tested): pick a different sit number (1..count), never the current one when there is a choice
    internal static int AoSitPick(int count, int current, double r01)
    {
        if (count <= 1) return 1;
        int k = 1 + (int)Math.Floor(Math.Clamp(r01, 0, 0.999999) * (count - 1));   // 1..count-1
        return k >= current && current >= 1 ? k + 1 : k;
    }
    static int aoSitCurrent;   // the number picked last (0 = unknown)
    static volatile string aoSitCurrentName;   // from the AO's own owner-say "Current Sitting on Ground Animation::<name>"
    static int aoSitHooked;
    // pure (tested): the AO's confirmation line -> sit name (null = not that line)
    internal static string AoSitConfirmName(string msg)
    {
        var m = System.Text.RegularExpressions.Regex.Match(msg ?? "", @"Current Sitting on Ground Animation::\s*(\S+)");
        return m.Success ? m.Groups[1].Value : null;
    }
    // pure (tested): the current sit's number: the AO's last confirmed name wins over our own memory (a relog resets ours)
    internal static int AoSitCurrentNumber(List<(int n, string name)> all, string confirmedName, int remembered) =>
        confirmedName != null && all.FirstOrDefault(a => a.name == confirmedName) is { n: > 0 } hit ? hit.n : remembered;
    static void AoSitHook()
    {
        if (Interlocked.Exchange(ref aoSitHooked, 1) == 1) return;
        client.Self.ChatFromSimulator += (s, e) =>
        {
            try { if (e.Type == ChatType.OwnerSay && AoSitConfirmName(e.Message) is string n) aoSitCurrentName = n; } catch { }
        };
    }
    static async Task<ScriptDialogEventArgs> AoDialogAfter(ScriptDialogEventArgs prev, int ms)
    {
        for (int i = 0; i < ms / 250; i++)
        {
            var d = lastDialog;
            if (d != null && !ReferenceEquals(d, prev) && (d.ObjectName ?? "").Contains("VISTA", StringComparison.OrdinalIgnoreCase) && d.OwnerID == client.Self.AgentID) return d;
            await Task.Delay(250);
        }
        return null;
    }
    static async Task<string> AoGroundSitSwitch()
    {
        AoSitHook();
        var spot = (File.Exists(Env("GT_AO_SITSELECT_FILE", "/home/box/viewers/textclient/run/ao-groundsit-select.txt")) ? File.ReadAllText(Env("GT_AO_SITSELECT_FILE", "/home/box/viewers/textclient/run/ao-groundsit-select.txt")).Trim() : AoSitSelectDefault).Split(' ');
        var prev = lastDialog;
        await Exec($"touch-attachment \"ao\" \"{spot[0]}\" {spot[1]} st={spot[2]}");
        var d = await AoDialogAfter(prev, 6000);
        if (d == null || !(d.Message ?? "").Contains("Sitting on Ground", StringComparison.OrdinalIgnoreCase)) return "AO ground-sit chooser did not open";
        var all = new List<(int n, string name)>(AoSitEntries(d.Message));
        // count the later pages: NEXT shows the rest (page 1 holds 1-8)
        var page = d;
        while ((page.ButtonLabels ?? new()).Contains("NEXT") && all.Count < 60)
        {
            var p0 = page; await Exec("dialog NEXT"); page = await AoDialogAfter(p0, 5000); if (page == null) break;
            var more = AoSitEntries(page.Message); if (more.Count == 0 || more.All(m => all.Any(a => a.n == m.n))) break;
            all.AddRange(more.Where(m => !all.Any(a => a.n == m.n)));
        }
        int count = all.Count == 0 ? 0 : all.Max(a => a.n); if (count < 2) return $"only {count} ground sit(s): nothing to switch";
        int current = AoSitCurrentNumber(all, aoSitCurrentName, aoSitCurrent);
        int pick = AoSitPick(count, current, Random.Shared.NextDouble());
        // walk to the page whose buttons include the pick
        for (int i = 0; i < 10 && page != null && !(page.ButtonLabels ?? new()).Contains(pick.ToString()); i++)
        {
            var btn = (page.ButtonLabels ?? new()).Contains("PREV") ? "PREV" : "NEXT";
            var p0 = page; await Exec("dialog " + btn); page = await AoDialogAfter(p0, 5000);
        }
        if (page == null || !(page.ButtonLabels ?? new()).Contains(pick.ToString())) return $"could not reach ground sit {pick}";
        var name = all.FirstOrDefault(a => a.n == pick).name ?? "?";
        await Exec("dialog " + pick);
        // confirmed only by the AO's own line; a failed pick is reported, never retried or re-sat (no stand/resit)
        for (int i = 0; i < 16 && aoSitCurrentName != name; i++) await Task.Delay(250);
        if (aoSitCurrentName != name) return $"ground sit {pick} {name} pressed but the AO did not confirm (current {aoSitCurrentName ?? "unknown"}); left as is";
        aoSitCurrent = pick;
        return $"ground sit {(current > 0 ? current + " -> " : "")}{pick} {name} (of {count})";
    }
    static double idleRotate = IdleRotateSec(Random.Shared.NextDouble());
    static async Task<string> IdlePoseRotate()
    {
        if (!await idleLock.WaitAsync(0)) return "busy";
        try
        {
            var cur = idlePosed; if (cur == null) return "not posed";
            if (cur.kind == "groundsit")
            {
                var r = await AoGroundSitSwitch();
                idleRotate = IdleRotateSec(Random.Shared.NextDouble());
                idlePosed = cur with { pose = r.StartsWith("ground sit ") ? r : cur.pose, since = DateTime.Now }; IdleSave();
                return r;
            }
            if (cur.kind != "collar") return "not in a collar pose";
            var pose = PickIdlePose(cur.pose, Random.Shared.NextDouble());   // never the current one
            var (ok, info, anims) = await CollarPosePlay(pose);              // picked straight from the Pose menu, no STOP
            idleRotate = IdleRotateSec(Random.Shared.NextDouble());
            if (!ok) return info;
            idleLastPose = pose; idlePosed = new("collar", pose, anims ?? new(), DateTime.Now); IdleSave();
            try { AutoHoverKick("pose change"); } catch { }
            return $"{cur.pose} -> {pose}: {info}";
        }
        finally { idleLock.Release(); }
    }
    // pure (tested): switch pose family when visibility changes: collar hidden + no chain -> AO ground sit (if allowed),
    // collar or chain visible while ground sitting -> collar pose. null = keep.
    internal static string IdleSwitch(string kind, bool collarVisible, bool leashVisible, bool leashed, bool groundSitMapped)
    {
        var want = IdlePoseKind(collarVisible, leashVisible);
        if (kind == null || want == kind) return null;
        if (want == "groundsit" && (leashed || !groundSitMapped)) return null;   // keep kneeling rather than stand
        return want;
    }
    // 2026-10-23 (David): an explicit stand (stand tool, idle_pose stop, logout) blocks the idle sit/kneel for a fixed 30 min
    internal const double IdleStandCooldownSec = 1800;
    internal static bool IdleCooldownActive(DateTime now, DateTime noSitUntil) => now < noSitUntil;
    static DateTime idleNoSitUntil = DateTime.MinValue;
    // pure (tested): is the AO force sit really on? Judged from the playing anims (ground sit playing while not on an object),
    // never from an internal flag: the AO toggle persists across logins (2026-10-23 sat-right-back-down bug).
    internal static bool AoForceSitOn(IEnumerable<UUID> playing, bool sittingOnObject) =>
        !sittingOnObject && (playing ?? Enumerable.Empty<UUID>()).Any(a => a == Animations.SIT_GROUND || a == Animations.SIT_GROUND_staticRAINED);
    // pure (tested): touch the AO toggle only when its real state is on (touching it while off turns force sit ON)
    internal static bool AoShouldToggleOff(bool forceSitOn) => forceSitOn;
    // turn the AO ground sit off if (and only if) it is really on; verified
    static async Task<string> AoGroundSitEnsureOff(string why)
    {
        if (!AoForceSitOn(SelfAnims(), client.Self.SittingOn != 0)) return "AO force sit already off";
        try { client.Self.Stand(); } catch { }
        await Task.Delay(800);
        if (!AoShouldToggleOff(AoForceSitOn(SelfAnims(), client.Self.SittingOn != 0))) return "stood (AO force sit off)";
        var r = await AoGroundSitToggle("off: " + why);
        await Task.Delay(1500);
        if (AoForceSitOn(SelfAnims(), client.Self.SittingOn != 0)) { try { client.Self.Stand(); } catch { } await Task.Delay(800); }
        return $"AO force sit toggled off ({r}); {(AoForceSitOn(SelfAnims(), client.Self.SittingOn != 0) ? "STILL ground sitting" : "standing")}";
    }
    // 2026-10-23 (David): only HIS stand (stand tool, idle_pose stop) starts the 30 min cooldown; an internal stand
    // (logout, shutdown, deploy) never does, and a new login clears any cooldown.
    internal static bool IdleStandStartsCooldown(string why) => why is "stand command" or "manual stop";
    internal static DateTime IdleCooldownAfterLogin() => DateTime.MinValue;
    // stand (stand tool, idle_pose stop, logout): clear any pose, make sure the AO force sit is off; cooldown only for his stand
    static async Task<string> IdleExplicitStand(string why)
    {
        bool cool = IdleStandStartsCooldown(why);
        if (cool) idleNoSitUntil = DateTime.Now.AddSeconds(IdleStandCooldownSec); IdleNoteActivity();
        string a = idlePosed != null ? await IdlePoseExit(why) : "not posed";
        string b = await AoGroundSitEnsureOff(why);
        if (cool) idleNoSitUntil = DateTime.Now.AddSeconds(IdleStandCooldownSec);
        Log("idlepose", $"stand ({why}): {a}; {b}; {(cool ? $"no idle sit until {idleNoSitUntil:HH:mm}" : "no cooldown (internal stand)")}");
        return cool ? $"{a}; {b}; no idle sit/kneel for 30 min" : $"{a}; {b}";
    }
    internal sealed record IdlePoseState(string kind, string pose, List<string> anims, DateTime since);
    static volatile IdlePoseState idlePosed;
    static string idleLastPose;
    static DateTime idleActivityAt = DateTime.Now;
    static double idleThreshold = IdleThresholdSec(IdleBaseSec(), Random.Shared.NextDouble());
    static volatile bool idlePoseOn = Env("GT_IDLE_POSE", "on") != "off";
    static readonly SemaphoreSlim idleLock = new(1, 1);
    static readonly string IdlePoseFile = Env("GT_IDLE_POSE_FILE", "/home/box/viewers/textclient/run/idle-pose.json");
    static readonly string AoGroundSitFile = Env("GT_AO_GROUNDSIT_FILE", "/home/box/viewers/textclient/run/ao-groundsit.txt");
    static double IdleBaseSec() => double.TryParse(Env("GT_IDLE_POSE_SEC", "240"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 30 ? v : 240;
    static (int link, int face, string st)? AoGroundSitNow() { try { return File.Exists(AoGroundSitFile) ? AoGroundSitSpot(File.ReadAllText(AoGroundSitFile)) : null; } catch { return null; } }
    static void IdleNoteActivity() { idleActivityAt = DateTime.Now; }
    static void IdleSave() { try { var s = idlePosed; if (s == null) File.Delete(IdlePoseFile); else File.WriteAllText(IdlePoseFile, JsonSerializer.Serialize(s)); } catch { } }
    static IdlePoseState IdleLoad() { try { return File.Exists(IdlePoseFile) ? JsonSerializer.Deserialize<IdlePoseState>(File.ReadAllText(IdlePoseFile)) : null; } catch { return null; } }

    // pure (tested): collar visible from its prims' alphas. 2026-10-23 bug: right after a relog the collar's prims (or their
    // TextureEntry) have not arrived yet and that was read as "visible", so a hidden (Stealth) collar got collar poses.
    // Unknown (no single collar match, or no texture data yet) now counts as hidden: ground sit, and IdleSwitch moves to a
    // collar pose once real alphas show it.
    internal static bool CollarVisibleFrom(int matches, IEnumerable<IEnumerable<float>> primAlphas)
    {
        if (matches != 1 || primAlphas == null) return false;
        foreach (var prim in primAlphas) { if (prim == null) continue; foreach (var a in prim) if (a > 0.05f) return true; }
        return false;
    }
    static bool CollarVisibleNow()
    {
        try
        {
            var mm = MatchAttachment("Premium Collar", WornPrims());
            var alphas = mm.Count != 1 ? null : LinkPrims(mm[0]).Select(p =>
            {
                var te = p.Textures; if (te == null) return (IEnumerable<float>)null;
                var l = new List<float>(); if (te.DefaultTexture != null) l.Add(te.DefaultTexture.RGBA.A);
                for (int f = 0; f < 45; f++) { var fe = te.FaceTextures[f]; if (fe != null) l.Add(fe.RGBA.A); }
                return l;
            }).ToList();
            return CollarVisibleFrom(mm.Count, alphas);
        }
        catch { return false; }   // unknown = hidden
    }

    // ---- collar Pose menu ----
    static async Task<ScriptDialogEventArgs> CollarPoseMenu()
    {
        var prev = lastDialog;
        await Exec("touch-attachment \"Premium Collar\" 1");
        var main = await CollarDialogAfter(prev, 10000);
        if (main == null || !(main.ButtonLabels ?? new()).Contains("Pose")) return null;
        await Exec("dialog Pose");
        return await CollarDialogAfter(main, 10000);
    }
    static async Task<(bool ok, string info, List<string> anims)> CollarPosePlay(string pose)
    {
        var d = await CollarPoseMenu(); if (d == null) return (false, "collar Pose menu did not open", null);
        for (int page = 1; page <= 6; page++)
        {
            var b = CollarPoseButton(d.ButtonLabels, pose);
            if (b != null)
            {
                var before = SelfAnims().ToHashSet();
                await Exec("dialog " + b);
                await Task.Delay(2500);
                var added = SelfAnims().Where(a => !before.Contains(a)).Select(a => a.ToString()).ToList();
                return (true, $"'{b}' on page {page}" + (added.Count > 0 ? $", anim {string.Join(",", added.Select(x => x[..8]))}" : ", no new anim seen"), added);
            }
            var next = (d.ButtonLabels ?? new()).FirstOrDefault(x => x == "▸" || x.Equals("Next", StringComparison.OrdinalIgnoreCase));
            if (next == null) break;
            var prev = d; await Exec("dialog " + next); d = await CollarDialogAfter(prev, 8000); if (d == null) return (false, "collar menu stopped answering", null);
        }
        return (false, $"pose '{pose}' not found in the collar's Pose menu", null);
    }
    // STOP on the Pose menu; verified when the pose anims are gone (or the menu reads Playing: (none))
    static async Task<(bool ok, string info)> CollarPoseStop(IReadOnlyCollection<string> anims)
    {
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            var d = await CollarPoseMenu(); if (d == null) { if (attempt == 2) return (false, "collar Pose menu did not open"); continue; }
            var stop = (d.ButtonLabels ?? new()).FirstOrDefault(x => x.Equals("STOP", StringComparison.OrdinalIgnoreCase));
            if (stop == null) return (false, "no STOP on the Pose menu");
            var prev = d; await Exec("dialog " + stop);
            var back = await CollarDialogAfter(prev, 4000);
            await Task.Delay(1000);
            var now = SelfAnims().Select(a => a.ToString()).ToHashSet();
            bool gone = anims == null || anims.Count == 0 ? CollarPlaying(back?.Message) == null : !anims.Any(now.Contains);
            if (gone) return (true, $"STOP pressed, pose animation gone (attempt {attempt})");
        }
        return (false, "STOP pressed twice but the pose animation is still playing");
    }
    // ---- AO ground sit ----
    static async Task<string> AoGroundSitToggle(string why)
    {
        if (AoGroundSitNow() is not { } spot) return "AO 'force sitg' button not mapped (run/ao-groundsit.txt)";
        var r = await Exec($"touch-attachment \"ao\" \"{spot.link}\" {spot.face} st={spot.st}");
        Log("idlepose", $"AO force sitg toggled ({why}): {r}");
        return r;
    }

    // 2026-10-23 (David): before a kneel or ground sit, turn to face the leash holder (else David) when he is nearby.
    internal const float IdleFaceRadius = 10f;
    // pure (tested): whom to face: the leash holder if within the radius, else David if within it, else nobody
    internal static Vector3? IdleFaceTarget(Vector3 me, Vector3? holder, Vector3? david, float radius = IdleFaceRadius)
    {
        bool near(Vector3? p) { if (p is not Vector3 v || v == Vector3.Zero) return false; var d = Vector3.Distance(new Vector3(v.X, v.Y, 0), new Vector3(me.X, me.Y, 0)); return d > 0.05f && d <= radius; }
        return near(holder) ? holder : near(david) ? david : null;
    }
    static async Task IdleFaceHim()
    {
        try
        {
            var l = leash; var me = client.Self.SimPosition;
            Vector3? hp = l != null && l.holder != UUID.Zero ? Avatars().Where(a => a.av.ID == l.holder && a.dist >= 0).Select(a => (Vector3?)a.pos).FirstOrDefault() : null;
            if (IdleFaceTarget(me, hp, DavidPos()) is not Vector3 t) return;
            client.Self.Movement.TurnToward(new Vector3(t.X, t.Y, me.Z)); client.Self.Movement.SendUpdate(true);
            Log("idlepose", $"facing {(hp == t ? l.holderName : "David")} before posing ({Vector3.Distance(me, t):F1} m)");
            await Task.Delay(600);   // let the turn land before the pose locks the body
        }
        catch (Exception ex) { Log("idlepose", "face-him error " + ex.GetBaseException().Message); }
    }
    static async Task<string> IdlePoseEnter(string kind, string why)
    {
        if (!await idleLock.WaitAsync(0)) return "idle pose busy";
        try
        {
            if (idlePosed != null) return $"already posed ({idlePosed.kind} {idlePosed.pose})";
            await IdleFaceHim();
            if (kind == "collar")
            {
                var pose = PickIdlePose(idleLastPose, Random.Shared.NextDouble());
                var (ok, info, anims) = await CollarPosePlay(pose);
                Log("idlepose", $"collar pose {pose} ({why}): {info}");
                if (!ok) return info;
                idleLastPose = pose; idlePosed = new("collar", pose, anims ?? new(), DateTime.Now); IdleSave(); AutoHoverKick("kneel");
                return $"collar pose '{pose}': {info}";
            }
            var before = SelfAnims().ToHashSet();
            var r = await AoGroundSitToggle("enter: " + why);
            if (r.StartsWith("AO 'force sitg' button not mapped")) return r;
            await Task.Delay(800);
            try { client.Self.SitOnGround(); } catch { }   // force sitg only changes which sit plays: actually sit down
            await Task.Delay(2500);
            var added = SelfAnims().Where(a => !before.Contains(a)).Select(a => a.ToString()).ToList();
            idlePosed = new("groundsit", "force sitg", added, DateTime.Now); IdleSave(); AutoHoverKick("ground sit");
            return $"AO ground sit on ({(added.Count > 0 ? "anim " + string.Join(",", added.Select(x => x[..8])) : "no new anim seen")})";
        }
        finally { idleLock.Release(); }
    }
    static async Task<string> IdlePoseExit(string why)
    {
        var s = idlePosed; if (s == null) return "not posed";
        await idleLock.WaitAsync();
        try
        {
            s = idlePosed; if (s == null) return "not posed";
            string info;
            if (s.kind == "collar") { var (ok, i) = await CollarPoseStop(s.anims); info = i; if (!ok) foreach (var a in s.anims) try { client.Self.AnimationStop(new UUID(a), true); } catch { } }
            else
            {
                info = await AoGroundSitEnsureOff("exit: " + why);
            }
            idlePosed = null; IdleSave(); IdleNoteActivity(); AutoHoverKick("pose cleared");
            idleThreshold = IdleThresholdSec(IdleBaseSec(), Random.Shared.NextDouble());
            Log("idlepose", $"cleared {s.kind} {s.pose} before {why}: {info}");
            return $"cleared {s.kind} {s.pose}: {info}";
        }
        finally { idleLock.Release(); }
    }
    // sync callers (AutoPilotTo): clear first, bounded wait
    static void IdlePoseExitBeforeMove(string why)
    {
        if (idlePosed == null) return;
        try { Task.Run(() => IdlePoseExit(why)).Wait(8000); } catch { }
    }
    static Task IdlePoseExitAsync(string why) => idlePosed == null ? Task.CompletedTask : IdlePoseExit(why);

    static DateTime idleLoginAt;
    static void IdlePoseLoginStart()
    {
        idleLoginAt = DateTime.Now; IdleNoteActivity(); try { AoSitHook(); } catch { } idleNoSitUntil = IdleCooldownAfterLogin();   // a cooldown never survives a relog
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(20000);
                if (!LoggedIn) return;
                var saved = IdleLoad(); idlePosed = null;
                Log("idlepose", "login: " + await AoGroundSitEnsureOff("login reset"));
                var d = await CollarPoseMenu();
                var playing = CollarPlaying(d?.Message);
                if (playing != null || saved?.kind == "collar")
                {
                    var stop = (d?.ButtonLabels ?? new()).FirstOrDefault(x => x.Equals("STOP", StringComparison.OrdinalIgnoreCase));
                    if (stop != null) await Exec("dialog " + stop);
                    Log("idlepose", $"login: leftover collar pose '{playing ?? saved?.pose}' -> STOP");
                }
                else Log("idlepose", "login: no leftover collar pose");
                IdleSave();
            }
            catch (Exception ex) { Log("idlepose", "login reset: " + ex.GetBaseException().Message); }
        });
    }

    static void IdlePoseHooks()
    {
        client.Self.ChatFromSimulator += (s, e) => { if (e.SourceType == ChatSourceType.Agent && e.Type is ChatType.Normal or ChatType.Shout or ChatType.Whisper) IdleNoteActivity(); };
        client.Self.IM += (s, e) => IdleNoteActivity();
        client.Self.TeleportProgress += (s, e) => { if (e.Status == TeleportStatus.Start) { IdleNoteActivity(); _ = IdlePoseExitAsync("teleport"); } };
    }
    static bool idleHooked;
    static void IdlePoseLoopRun()
    {
        if (!idleHooked) { idleHooked = true; IdlePoseHooks(); }
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(1000);
                try { if (LoggedIn && (DateTime.Now - idleLoginAt).TotalSeconds > 40) await IdlePoseTick(); } catch { }
            }
        });
    }
    static async Task IdlePoseTick()
    {
        var l = leash; var v0 = client.Self.Velocity; float speed = new Vector3(v0.X, v0.Y, 0).Length();
        bool following = followId != UUID.Zero, sitObj = client.Self.SittingOn != 0, forceSat = rlvHoldSeat != null;
        bool moving = speed > 0.3f;
        var dav = Avatars().FirstOrDefault(a => a.av != null && a.av.ID == DavidId);
        bool davidAway = dav.av != null && dav.dist < 15 && LeashMovingAway(dav.pos, dav.av.Velocity, client.Self.SimPosition);
        bool dancing = IdleDancingNow();
        if (Env("GT_AUTO_HOVER", "observe") != "off") _ = AutoHoverCheck("idle tick");
        if (moving || following || sitObj || leashDriving || dancing) IdleNoteActivity();   // the timer restarts after a dance
        var posed = idlePosed;
        if (posed != null)
        {
            var why = IdleMustExit(posed.kind, following, sitObj, moving, forceSat, l != null, davidAway, false);
            if (why != null) await IdlePoseExit(why);
            else if (IdleSwitch(posed.kind, CollarVisibleNow(), LeashParticleTarget() != null, l != null, AoGroundSitNow() != null) is string sw)
            { await IdlePoseExit("switch to " + sw); Log("idlepose", "switch: " + await IdlePoseEnter(sw, "visibility changed")); }
            else if (IdleShouldRotate(posed.kind, (DateTime.Now - posed.since).TotalSeconds, idleRotate, dancing)) Log("idlepose", "rotate: " + await IdlePoseRotate());
            return;
        }
        if (davidAway) IdleNoteActivity();
        var kind = IdleEnter(new IdleIn(idlePoseOn, l != null, leashDriving, following, sitObj, moving, forceSat,
            rlv.Has("sit") || rlv.Has("unsit") && sitObj, leashForgetBusy != 0 || shuttingDown, dancing,
            (DateTime.Now - idleActivityAt).TotalSeconds, idleThreshold, CollarVisibleNow(), LeashParticleTarget() != null, AoGroundSitNow() != null));
        if (kind != null && IdleCooldownActive(DateTime.Now, idleNoSitUntil)) return;   // explicit stand cooldown
        if (kind != null) Log("idlepose", "enter: " + await IdlePoseEnter(kind, $"idle {(DateTime.Now - idleActivityAt).TotalSeconds:F0} s"));
    }

    static async Task<string> IdlePoseCmd(string[] a)
    {
        var sub = a.Length > 0 ? a[0].ToLowerInvariant() : "status";
        switch (sub)
        {
            case "on" when a.Length == 1: idlePoseOn = true; IdleNoteActivity(); return $"idle pose ON (after {idleThreshold:F0} s idle)";
            case "off": idlePoseOn = false; return "idle pose OFF; " + await IdlePoseExit("idle pose off");
            case "now": { var k = a.Length > 1 ? (a[1] == "collar" || a[1] == "kneel" ? "collar" : "groundsit") : IdlePoseKind(CollarVisibleNow(), LeashParticleTarget() != null); return await IdlePoseEnter(k, "manual"); }
            case "stop": case "stand": return await IdleExplicitStand("manual stop");
            case "idle" when a.Length > 1 && double.TryParse(a[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sec) && sec >= 30:
                idleThreshold = sec; return $"idle threshold {sec:F0} s (this round; base GT_IDLE_POSE_SEC={IdleBaseSec():F0})";
            case "aomap" when a.Length is 3 or 4: { var t = string.Join(" ", a.Skip(1)); if (AoGroundSitSpot(t) is not { } sp) return "usage: idlepose aomap [link] <face> <u,v>"; File.WriteAllText(AoGroundSitFile, t + "\n"); return $"AO 'force sitg' mapped: link {sp.link} face {sp.face} st {sp.st}"; }
            default:
            {
                var p = idlePosed;
                return $"idle pose {(idlePoseOn ? "ON" : "OFF")}; {(p == null ? "standing" : $"posed: {p.kind} '{p.pose}' since {p.since:HH:mm:ss}")}; idle {(DateTime.Now - idleActivityAt).TotalSeconds:F0}/{idleThreshold:F0} s; {(IdleCooldownActive(DateTime.Now, idleNoSitUntil) ? $"stand cooldown until {idleNoSitUntil:HH:mm}; " : "")}AO force sit {(AoForceSitOn(SelfAnims(), client.Self.SittingOn != 0) ? "ON" : "off")}; " +
                       $"collar {(CollarVisibleNow() ? "visible" : "hidden")}, leash chain {(LeashParticleTarget() != null ? "visible" : "none")}; AO force sitg {(AoGroundSitNow() is { } s ? $"link {s.link} face {s.face} st {s.st}" : "NOT mapped")}; last pose {idleLastPose ?? "-"}";
            }
        }
    }
}
