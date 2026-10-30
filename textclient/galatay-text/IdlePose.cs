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
    // 2026-10-26 (David): in android form she NEVER uses the collar kneel poses, only AO ground sits (and standing)
    internal static string IdlePoseKind(bool collarVisible, bool leashVisible, bool android = false) => !android && (collarVisible || leashVisible) ? "collar" : "groundsit";
    // pure (tested): the kind an idle pose may really be entered as, or null = no idle pose now. android: never a collar kneel (ground sit
    // instead); leashed: never any new sit/kneel via the AO ground sit; leashHold: she was stood up because of this leash (2026-10-26
    // 09:54 bug: groundsit -> leash -> stood -> straight into a collar kneel) so nothing starts until the leash is released.
    internal static string IdleEnterKindFor(string kind, bool android, bool leashed, bool leashHold)
    {
        if (kind == null) return null;
        if (android) kind = "groundsit";
        if (leashed && (kind == "groundsit" || leashHold)) return null;
        return kind;
    }
    // pure (tested): idle threshold with jitter: base * 0.75..1.25 (default base 240 s -> 3-5 min)
    internal static double IdleThresholdSec(double baseSec, double r01) => IdleWaitMin + (IdleWaitMax - IdleWaitMin) * Math.Clamp(r01, 0, 1);   // David 11:26: real variance, fresh each time (base kept for API)
    internal const double IdleWaitMin = 90, IdleWaitMax = 300;
    internal readonly record struct IdleIn(bool enabled, bool leashed, bool walkerDriving, bool following, bool sittingOnObject, bool moving,
        bool forceSat, bool sitLocked, bool busy, bool dancing, double idleSec, double thresholdSec, bool collarVisible, bool leashVisible, bool groundSitMapped, bool android = false, bool leashHold = false);
    // pure (tested): enter which pose now (null = not now)
    internal static string IdleEnter(IdleIn s)
    {
        if (!s.enabled || s.walkerDriving || s.following || s.sittingOnObject || s.moving || s.forceSat || s.sitLocked || s.busy || s.dancing) return null;   // dancing is never idle
        if (s.idleSec < s.thresholdSec) return null;
        var k = IdleEnterKindFor(IdlePoseKind(s.collarVisible, s.leashVisible, s.android), s.android, s.leashed, s.leashHold);
        if (k == "groundsit" && !s.groundSitMapped) return null;
        return k;
    }
    // pure (tested): must the pose be cleared now?
    internal static string IdleMustExit(string kind, bool following, bool sittingOnObject, bool moving, bool forceSat, bool leashed, bool davidWalkingAway, bool walkerWants, bool android = false)
        => kind == null ? null
         : following ? "follow" : sittingOnObject || forceSat ? "sit" : moving ? "moving"
         : walkerWants ? "walker"
         : leashed && (kind == "groundsit" || kind == "collar" && android) ? "leashed"   // 2026-10-26: a leash ends an AO ground sit (and any android collar kneel): stand, never switch into another pose
         : null;   // a leash alone never ends a human collar kneel (David 11:31)
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
    internal static string IdleStartSitPick(string switchResult) => switchResult ?? "no result";
    // pure (tested): hover offset for the AO ground sit by form (David 14:45: android sits ~4 cm too high)
    // 2026-10-26 (David): hover heights are NOT saved or applied per pose or per AO stand (the ground changes with the location):
    // after a sit, a stand or a new region PoseHover.cs checks the hover visually and fixes it then.
    static volatile string aoSitCurrentName;   // from the AO's own owner-say "Current Sitting on Ground Animation::<name>"
    static GridClient aoSitHookedClient;   // 2026-10-26: per client (a relog makes a new GridClient; a static flag left the new one unhooked: stale sit name, no hover check after a switch)
    // pure (tested): does this client still need the AO-sit chat hook?
    internal static bool AoSitHookNeeded(object hooked, object current) => !ReferenceEquals(hooked, current);
    // pure (tested): the AO's confirmation line -> sit name (null = not that line)
    internal static string AoSitConfirmName(string msg)
    {
        var m = System.Text.RegularExpressions.Regex.Match(msg ?? "", @"Current Sitting on Ground Animation::\s*(\S+)");
        return m.Success ? m.Groups[1].Value : null;
    }
    // pure (tested): the current sit's number: the AO's last confirmed name wins over our own memory (a relog resets ours)
    internal static int AoSitCurrentNumber(List<(int n, string name)> all, string confirmedName, int remembered) =>
        confirmedName != null && all.FirstOrDefault(a => a.name == confirmedName) is { n: > 0 } hit ? hit.n : remembered;
    static readonly string AoSitLastFile = Env("GT_AO_SITLAST_FILE", "/home/box/viewers/textclient/run/ao-groundsit-last.txt");
    // 2026-10-28 (David: choose the pose FIRST, sit once): the pick made while she still STANDS (GroundSitPreSelect) must not ask for a hover check (nothing is on the floor yet);
    // a pick while a sit plays (a rotation) still does: the new pose is remeasured. pure (tested)
    static volatile bool aoSitPrePick;
    internal static bool AoSitPickWantsHoverCheck(bool prePick, bool sitPlaying) => !prePick && sitPlaying;
    static void AoSitHook()
    {
        try { if (aoSitCurrentName == null && File.Exists(AoSitLastFile)) aoSitCurrentName = File.ReadAllText(AoSitLastFile).Trim(); } catch { }   // the AO keeps its pick across relogs
        if (!AoSitHookNeeded(aoSitHookedClient, client)) return;
        aoSitHookedClient = client;
        client.Self.ChatFromSimulator += (s, e) =>
        {
            try { if (e.Type == ChatType.OwnerSay && AoSitConfirmName(e.Message) is string n) { aoSitCurrentName = n; try { File.WriteAllText(AoSitLastFile, n); } catch { } if (AoSitPickWantsHoverCheck(aoSitPrePick, AoSitReallyOn())) hoverSvc.HoverCheckRequest("sit"); } } catch { }
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
    static volatile bool idleRotatePaused;   // `idlepose rotate off` (for tests): keep the current ground sit / collar pose, never switch automatically; resets on restart
    static async Task<string> IdlePoseRotate()
    {
        if (!await idleLock.WaitAsync(0)) return "busy";
        try
        {
            var cur = idlePosed; if (cur == null) return "not posed";
            if (cur.kind == "collar" && IdleAndroidNow()) return "android form: no collar poses";
            if (cur.kind == "groundsit")
            {
                var beforeAnims = SelfAnims().ToHashSet(); aoSitTransitionUntil = DateTime.Now.AddSeconds(30);
                var r = await AoGroundSitSwitch();
                try { AoLearnSits(SelfAnims().Where(a => !beforeAnims.Contains(a)).Select(a => a.ToString()), beforeAnims); } catch { }
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
            try { hoverSvc.AutoHoverKick("pose change"); } catch { }
            return $"{cur.pose} -> {pose}: {info}";
        }
        finally { idleLock.Release(); }
    }
    // pure (tested): switch pose family when visibility changes: collar hidden + no chain -> AO ground sit (if allowed),
    // collar or chain visible while ground sitting -> collar pose. null = keep.
    internal static string IdleSwitch(string kind, bool collarVisible, bool leashVisible, bool leashed, bool groundSitMapped, bool android = false)
    {
        var want = IdlePoseKind(collarVisible, leashVisible, android);
        if (kind == null || want == kind) return null;
        if (leashed) return null;   // 2026-10-26: leashed => a switch may only EXIT (IdleMustExit), never enter another pose
        if (want == "groundsit" && !groundSitMapped) return null;   // keep kneeling rather than stand
        return want;
    }
    // 2026-10-23 (David): an explicit stand (stand tool, idle_pose stop, logout) blocks the idle sit/kneel for a fixed 30 min
    internal const double IdleStandCooldownSec = 1800;
    internal static bool IdleCooldownActive(DateTime now, DateTime noSitUntil) => now < noSitUntil;
    static DateTime idleNoSitUntil = DateTime.MinValue;
    // pure (tested): is the AO force sit really on? Judged from the playing anims (ground sit playing while not on an object),
    // never from an internal flag: the AO toggle persists across logins (2026-10-23 sat-right-back-down bug).
    // 2026-10-26: built-in ground sits AND the AO's own learned sit animations (AoSit.cs): the sim can say STAND while the AO holds a sit
    internal static bool AoForceSitOn(IEnumerable<UUID> playing, bool sittingOnObject) => AoSitPlaying(playing, sittingOnObject, AoSitIdsNow());
    // pure (tested): touch the AO toggle only when its real state is on (touching it while off turns force sit ON)
    internal static bool GroundSitStandNeeded(IEnumerable<UUID> playing, bool sittingOnObject) => AoForceSitOn(playing, sittingOnObject);
    internal static bool AoShouldToggleOff(bool forceSitOn) => forceSitOn;
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
        string c = ""; try { c = await IdleRepinAfterStand(why); } catch { }
        if (cool) idleNoSitUntil = DateTime.Now.AddSeconds(IdleStandCooldownSec);
        Log("idlepose", $"stand ({why}): {a}; {b}{c}; {(cool ? $"no idle sit until {idleNoSitUntil:HH:mm}" : "no cooldown (internal stand)")}");
        return cool ? $"{a}; {b}{c}; no idle sit/kneel for 30 min" : $"{a}; {b}{c}";
    }
    // 2026-10-28: after ANY stand (also one that found no idle pose to clear, e.g. an AO sit the client did not start) the hover goes back to the pinned stand value, so a
    // sit hover never lingers on a standing avatar; the stand check that follows measures the floor. Never at logout, never while still sitting. pure (tested)
    internal static bool IdleRepinNeeded(string why, bool stillSitting, double? inForce, double pinned) => why != "logout" && !stillSitting && inForce is double hv && Math.Abs(hv - pinned) > 0.005;
    static async Task<string> IdleRepinAfterStand(string why)
    {
        bool still = client.Self.SittingOn != 0 || AoSitReallyOn();
        if (!IdleRepinNeeded(why, still, EffectiveHoverNow(), PinnedHover())) return "";
        var (ok, info) = await PostHover(PinnedHover(), "stood up: back to normal hover");
        return $"; hover repinned to {PinnedHover():0.000} ({(ok ? "ok" : info)})";
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
        return await CollarDialogWhere(main, d => CollarLooksLikePoseMenu(d.ButtonLabels), 10000);
    }
    static async Task<(bool ok, string info, List<string> anims)> CollarPosePlay(string pose)
    {
        if (!await CollarMenuEnter()) return (false, "collar menu busy for 90 s", null);
        try { return await CollarPosePlayLocked(pose); } finally { stealthLock.Release(); }
    }
    static async Task<(bool ok, string info, List<string> anims)> CollarPosePlayLocked(string pose)
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
        if (!await CollarMenuEnter()) return (false, "collar menu busy for 90 s");
        try { return await CollarPoseStopLocked(anims); } finally { stealthLock.Release(); }
    }
    static async Task<(bool ok, string info)> CollarPoseStopLocked(IReadOnlyCollection<string> anims)
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
    // 2026-10-28: ONE press, then wait ~11 s, re-read the animation list and say whether force sit is now on or off from the AO animations that started (never from a
    // stored flag: the button is a toggle and its state survives a relog). A second press is refused until this read-back is done (the 11:30 press turned force sit ON
    // while the status said off; only the second press turned it off).
    static int aoPressBusy;
    static ForceSitState aoForceSitInferred = ForceSitState.Unknown; static string aoLastPressReport = null; static DateTime aoLastPressAt = DateTime.MinValue;
    static async Task<string> AoGroundSitToggle(string why)
    {
        var (r, readback) = await AoGroundSitPress(why);
        return readback == null ? r : $"{r}; {await readback}";
    }
    // 2026-10-29 (David: "it's reasonable to wait a few seconds but 11 is too long"): the press itself returns at once; the ~11 s read-back of the force-sit
    // state runs on in the background (still holding the press lock, so no second press before it) and never delays the ground sit's hover measurement.
    // readback == null = nothing was pressed (r says why).
    static async Task<(string r, Task<string> readback)> AoGroundSitPress(string why)
    {
        if (AoGroundSitNow() is not { } spot) return ("AO 'force sitg' button not mapped (run/ao-groundsit.txt)", null);
        if (!PoseEvidence.ForcePressAllowed(Interlocked.CompareExchange(ref aoPressBusy, 1, 0) == 0))
        { Log("idlepose", $"AO force sitg press REFUSED ({why}): the previous press has not been read back yet"); return ("force sit press REFUSED: the previous press is still being read back (never pressed twice without re-reading the animations)", null); }
        string r; List<UUID> before;
        try
        {
            before = SelfAnims().ToList();
            r = await Exec($"touch-attachment \"ao\" \"{spot.link}\" {spot.face} st={spot.st}");
            Log("idlepose", $"AO force sitg toggled ({why}): {r}");
            aoLastPressAt = DateTime.Now; aoSitTransitionUntil = DateTime.Now.AddSeconds(StuckSitWaitMs / 1000.0 + 20);
        }
        catch { Interlocked.Exchange(ref aoPressBusy, 0); throw; }
        var readback = Task.Run(async () =>
        {
            try
            {
                await Task.Delay((int)StuckSitWaitMs);
                var after = SelfAnims().ToList();
                aoForceSitInferred = PoseEvidence.ForceSitAfterPress(after, client.Self.SittingOn != 0, AoSitIdsNow(), AoStandIdsNow(), AoUnclassifiedNow(after).Count);
                aoLastPressReport = PoseEvidence.ForceSitReport(aoForceSitInferred, before, after);
                Log("idlepose", $"AO force sitg press read back ({why}): {aoLastPressReport}");
                return aoLastPressReport;
            }
            catch (Exception ex) { return "read-back error " + ex.GetBaseException().Message; }
            finally { Interlocked.Exchange(ref aoPressBusy, 0); }
        });
        return (r, readback);
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
    // ---- ground sit entry (2026-10-28): pose first, then sit once ----
    // pure (tested): did the pre-pick land? Only the AO's own confirmation line counts ("ground sit 7 -> 5 NAME (of 10)"); the failure texts of AoGroundSitSwitch
    // ("... pressed but the AO did not confirm", "could not reach ...", "chooser did not open") also start with "ground sit", so the text is checked whole.
    // A single available sit is nothing to choose: accepted.
    internal static bool GroundSitPickConfirmed(string switchResult) => switchResult != null
        && (System.Text.RegularExpressions.Regex.IsMatch(switchResult, @"^ground sit (\d+ -> )?\d+ \S+ \(of \d+\)$") || switchResult.StartsWith("only 1 ground sit", StringComparison.Ordinal));
    // pure (tested): press force sitg once the sim's own ground sit plays (and 0.5 s after the sit request, so the AO sees a settled sit), else after 2.5 s regardless
    internal static bool GroundSitPressReady(bool simGroundSitSeen, double msSinceSit) => msSinceSit >= 2500 || simGroundSitSeen && msSinceSit >= 500;
    sealed class GroundSitSteps : IGroundSitSteps
    {
        readonly string why; internal GroundSitSteps(string why) { this.why = why; }
        public async Task<(bool ok, string info)> PreSelectPose()
        {
            aoSitPrePick = true;
            try { var r = await AoGroundSitSwitch(); return (GroundSitPickConfirmed(r), r); }
            catch (Exception ex) { return (false, "pre-pick error " + ex.GetBaseException().Message); }
            finally { aoSitPrePick = false; }
        }
        public async Task<string> SitDown()
        {
            aoSitTransitionUntil = DateTime.Now.AddSeconds(60);
            try { client.Self.SitOnGround(); } catch { }
            var sw = System.Diagnostics.Stopwatch.StartNew(); bool seen = false;
            while (!GroundSitPressReady(seen, sw.Elapsed.TotalMilliseconds)) { await Task.Delay(100); seen = AoForceSitOn(SelfAnims(), client.Self.SittingOn != 0); }
            return seen ? $"sim ground sit playing after {sw.ElapsedMilliseconds} ms" : "sim ground sit not seen after 2.5 s";
        }
        List<UUID> beforePress = new(); bool simSitSeen;
        public async Task<(string press, Task<string> readback)> PressForceSit()
        {
            beforePress = SelfAnims().ToList(); simSitSeen = AoForceSitOn(beforePress, client.Self.SittingOn != 0);
            return await AoGroundSitPress("enter: " + why);
        }
        public bool ChosenPosePlaying()
        {
            var now = SelfAnims().ToList(); var b = new HashSet<UUID>(beforePress);
            var started = now.Where(a => !b.Contains(a)).ToList();
            return PoseEvidence.ChosenGroundSitPlaying(started, AoSitIdsNow(), AoUnclassifiedNow(started).Count, simSitSeen || AoForceSitOn(now, false), client.Self.SittingOn != 0);
        }
    }
    static async Task<string> IdlePoseEnter(string kind, string why)
    {
        if (!await idleLock.WaitAsync(0)) return "idle pose busy";
        bool hoverAfter = false; int hoverDelayMs = 1000;
        try
        {
            if (idlePosed != null) return $"already posed ({idlePosed.kind} {idlePosed.pose})";
            var allowed = IdleEnterKindFor(kind, IdleAndroidNow(), IdleLeashedNow(), idleLeashHold);
            if (allowed == null) return $"no idle pose ({kind}): {(IdleLeashedNow() ? "leashed: stand only" : "not allowed")}";
            if (allowed != kind) { Log("idlepose", $"enter {kind} -> {allowed} (android form never uses collar kneel poses)"); kind = allowed; }
            await IdleFaceHim();
            if (kind == "collar")
            {
                var pose = PickIdlePose(idleLastPose, Random.Shared.NextDouble());
                var (ok, info, anims) = await CollarPosePlay(pose);
                Log("idlepose", $"collar pose {pose} ({why}): {info}");
                if (!ok) return info;
                idleLastPose = pose; idlePosed = new("collar", pose, anims ?? new(), DateTime.Now); IdleSave(); hoverSvc.AutoHoverKick("kneel");
                return $"collar pose '{pose}': {info}";
            }
            var before = SelfAnims().ToHashSet();
            if (AoGroundSitNow() == null) return "AO 'force sitg' button not mapped (run/ao-groundsit.txt)";
            // 2026-10-28 (David: "You should only sit once, not three times. ... choose the pose first"): the old order was SitOnGround (generic cross-legged sit) ->
            // force sitg (the AO's LAST pose) -> pick a new pose (a third pose, then a re-measured hover). Now (GroundSitEntry): 1. the AO chooser picks the pose while she
            // still STANDS (nothing plays), 2. ONE SitOnGround, 3. ONE force-sitg press the moment the sim's ground sit plays (the AO then shows the chosen pose; the
            // generic sit is only on screen for that moment), 4. ONE hover check as soon as the chosen pose's animation is seen playing (polled every 250 ms, cap 8 s)
            // + a ~1.75 s settle (2026-10-29: was after the ~11 s read-back, which still runs in the background); cap hit = after the read-back, as before.
            // A failed pick = she stays standing.
            var entry = await GroundSitEntry.Run(new GroundSitSteps(why));
            Log("idlepose", $"ground sit pose chosen BEFORE sitting: {entry.pick}");
            if (!entry.entered) { IdleNoteActivity(); return $"ground sit NOT entered, she stays standing: {entry.pick}"; }
            Log("idlepose", $"ground sit: sat once ({entry.sit}); {entry.press}; {entry.poseWait}");
            hoverDelayMs = GroundSitEntry.HoverDelayMs(entry.poseSeen, (DateTime.Now - hoverSvc.PostureChangedAt).TotalMilliseconds);
            var added = SelfAnims().Where(a => !before.Contains(a)).Select(a => a.ToString()).ToList();
            AoLearnSits(added, before);
            idlePosed = new("groundsit", "force sitg", added, DateTime.Now); IdleSave(); hoverSvc.AutoHoverKick("ground sit");
            hoverAfter = true;   // requested after the lock is released (a request made inside it waits for the 8 s retry)
            return $"AO ground sit on ({(added.Count > 0 ? "anim " + string.Join(",", added.Select(x => x[..8])) : "no new anim seen")}; pose {aoSitCurrentName ?? "?"})";
        }
        finally { idleLock.Release(); if (hoverAfter) hoverSvc.HoverCheckRequest("ground sit", hoverDelayMs); }   // the ONE hover check of this sit
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
                // 2026-10-23 16:57: since entry sits with SitOnGround() first (#297), AO force sit off alone left her sitting on the
                // ground (idle pose cleared, body still seated); stand for real. Internal stand: no 30-min cooldown.
                if (GroundSitStandNeeded(SelfAnims(), client.Self.SittingOn != 0)) { try { client.Self.Stand(); } catch { } info += "; stood up (SitOnGround cleared)"; }
                try { if (EffectiveHoverNow() is double hv && Math.Abs(hv - PinnedHover()) > 0.005) await PostHover(PinnedHover(), "ground sit ended: back to normal hover"); } catch { }
            }
            idlePosed = null; IdleSave(); IdleNoteActivity(); hoverSvc.AutoHoverKick("pose cleared");
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
                ScriptDialogEventArgs d; await CollarMenuEnter();
                try { d = await CollarPoseMenu(); } finally { stealthLock.Release(); }
                var playing = CollarPlaying(d?.Message);
                if (playing != null || saved?.kind == "collar")
                {
                    var stop = (d?.ButtonLabels ?? new()).FirstOrDefault(x => x.Equals("STOP", StringComparison.OrdinalIgnoreCase));
                    if (stop != null) await Exec("dialog " + stop);
                    Log("idlepose", $"login: leftover collar pose '{playing ?? saved?.pose}' -> STOP");
                }
                else Log("idlepose", "login: no leftover collar pose");
                IdleSave();
                // the AO may start its held force sit after the reset: re-check, and stand (toggle off) if a learned AO sit plays
                foreach (var ms in LoginAoRecheckDelaysMs)
                {
                    await Task.Delay(ms);
                    if (!LoginAoRecheckNeeded(LoggedIn, AoSitReallyOn(), client.Self.SittingOn != 0, idlePosed != null)) continue;
                    Log("idlepose", "login recheck: AO sit playing after the login reset: " + await AoGroundSitEnsureOff("login recheck"));
                }
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
    // 2026-10-26: android form (outfit name or worn body); an unsettled form counts as android (never a collar kneel by mistake)
    static bool IdleAndroidNow() { try { return AndroidFormNow() || !hoverSvc.FormCertainNow(); } catch { return true; } }
    // leashed = the leash object, or the leash chain seen on the collar (it shows up a tick before the leash object), or a walker driving
    static bool IdleLeashedNow() => leash != null || leashDriving || LeashParticleTarget() != null;
    static volatile bool idleLeashHold;   // a leash stood her up: no idle pose until it is released
    static async Task IdlePoseTick()
    {
        var l = leash; bool leashedNow = IdleLeashedNow(), androidNow = IdleAndroidNow();
        if (!leashedNow) idleLeashHold = false; var v0 = client.Self.Velocity; float speed = new Vector3(v0.X, v0.Y, 0).Length();
        bool following = followId != UUID.Zero, sitObj = client.Self.SittingOn != 0, forceSat = rlvHoldSeat != null;
        bool moving = speed > 0.3f;
        var dav = Avatars().FirstOrDefault(a => a.av != null && a.av.ID == DavidId);
        bool davidAway = dav.av != null && dav.dist < 15 && LeashMovingAway(dav.pos, dav.av.Velocity, client.Self.SimPosition);
        bool dancing = IdleDancingNow();
        if (Env("GT_AUTO_HOVER", "observe") != "off") _ = hoverSvc.AutoHoverCheck("idle tick");
        try { hoverSvc.HoverPostureNote(SelfAnims(), client.Self.SittingOn != 0); } catch { }   // PoseHover.cs: a sit/stand the anim packets missed
        if (moving || following || sitObj || leashDriving || dancing) IdleNoteActivity();   // the timer restarts after a dance
        var posed = idlePosed;
        if (posed != null)
        {
            var why = IdleMustExit(posed.kind, following, sitObj, moving, forceSat, leashedNow, davidAway, false, androidNow);
            if (why != null) { if (why == "leashed") idleLeashHold = true; await IdlePoseExit(why); }
            else if (IdleSwitch(posed.kind, CollarVisibleNow(), LeashParticleTarget() != null, leashedNow, AoGroundSitNow() != null, androidNow) is string sw)
            { await IdlePoseExit("switch to " + sw); Log("idlepose", "switch: " + await IdlePoseEnter(sw, "visibility changed")); }
            else if (!idleRotatePaused && IdleShouldRotate(posed.kind, (DateTime.Now - posed.since).TotalSeconds, idleRotate, dancing)) Log("idlepose", "rotate: " + await IdlePoseRotate());
            return;
        }
        if (davidAway) IdleNoteActivity();
        var kind = IdleEnter(new IdleIn(idlePoseOn, leashedNow, leashDriving, following, sitObj, moving, forceSat,
            rlv.Has("sit") || rlv.Has("unsit") && sitObj, leashForgetBusy != 0 || shuttingDown, dancing,
            (DateTime.Now - idleActivityAt).TotalSeconds, idleThreshold, CollarVisibleNow(), LeashParticleTarget() != null, AoGroundSitNow() != null, androidNow, idleLeashHold));
        if (kind != null && IdleCooldownActive(DateTime.Now, idleNoSitUntil)) return;   // explicit stand cooldown
        if (kind != null) Log("idlepose", "enter: " + await IdlePoseEnter(kind, $"idle {(DateTime.Now - idleActivityAt).TotalSeconds:F0} s"));
    }

    // "standing" only when no unclassified AO animation plays (2026-10-26: it may be a sit)
    static string IdleStandingWord(PoseCall? real = null) => real == PoseCall.Sitting ? "NOT posed by me, but she is SITTING (see the real state)"
        : AoUnclassifiedNow().Count > 0 && !AoSitReallyOn() ? "not posed by me, but an AO animation is unclassified (may be a sit)" : "standing";
    // 2026-10-28: the real sit/stand state from the animations AND the fresh render's pelvis (PoseEvidence.cs); the render wins
    static PoseEvidence.RealPose IdleRealPose()
    {
        var playing = SelfAnims(); bool onObj = client.Self.SittingOn != 0;
        var unk = AoUnclassifiedNow(playing); bool sit = AoForceSitOn(playing, onObj);
        var sitIds = AoSitIdsNow(); var ids = playing.Where(sitIds.Contains).Select(x => x.ToString()[..8]).ToList();
        string detail = onObj ? "sitting on an object" : sit ? "AO sit " + (ids.Count == 0 ? "built-in ground sit" : string.Join(",", ids)) : unk.Count > 0 ? "unclassified AO " + string.Join(",", unk.Select(x => x.ToString()[..8])) : "only classified AO stands / none";
        var (rc, rd) = hoverSvc.RenderEvidenceNow();
        return PoseEvidence.Resolve(PoseEvidence.FromAnims(onObj, sit, unk.Count), detail, rc, rd);
    }
    static string IdleRealStateText()
    {
        var sit = AoSitIdsNow(); var playing = SelfAnims();
        var ids = playing.Where(sit.Contains).Select(x => x.ToString()[..8]).ToList();
        bool simGround = playing.Any(a => a == Animations.SIT_GROUND || a == Animations.SIT_GROUND_staticRAINED);
        var unk = AoUnclassifiedNow(playing).Select(x => x.ToString()[..8]).ToList();
        return AoRealState(AoForceSitOn(playing, client.Self.SittingOn != 0), ids, client.Self.SittingOn != 0, simGround, unk);
    }

    static async Task<string> IdlePoseCmd(string[] a)
    {
        var sub = a.Length > 0 ? a[0].ToLowerInvariant() : "status";
        switch (sub)
        {
            case "on" when a.Length == 1: idlePoseOn = true; IdleNoteActivity(); return $"idle pose ON (after {idleThreshold:F0} s idle)";
            case "off": idlePoseOn = false; return "idle pose OFF; " + await IdlePoseExit("idle pose off");
            case "now": { var k = a.Length > 1 ? (a[1] == "collar" || a[1] == "kneel" ? "collar" : "groundsit") : IdlePoseKind(CollarVisibleNow(), LeashParticleTarget() != null, IdleAndroidNow()); return await IdlePoseEnter(k, "manual"); }   // IdlePoseEnter refuses collar kneels in android form
            case "rotate" when a.Length == 2 && (a[1] == "on" || a[1] == "off"): idleRotatePaused = a[1] == "off"; return idleRotatePaused ? "automatic pose switching PAUSED (keeps the current pose until stand or restart)" : "automatic pose switching ON";
            case "stop": case "stand": return await IdleExplicitStand("manual stop");
            case "idle" when a.Length > 1 && double.TryParse(a[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sec) && sec >= 30:
                idleThreshold = sec; return $"idle threshold {sec:F0} s (this round; base GT_IDLE_POSE_SEC={IdleBaseSec():F0})";
            case "aomap" when a.Length is 3 or 4: { var t = string.Join(" ", a.Skip(1)); if (AoGroundSitSpot(t) is not { } sp) return "usage: idlepose aomap [link] <face> <u,v>"; File.WriteAllText(AoGroundSitFile, t + "\n"); return $"AO 'force sitg' mapped: link {sp.link} face {sp.face} st {sp.st}"; }
            case "verify": { var pr = await hoverSvc.PoseProbe(); Log("idlepose", "verify: " + pr); goto default; }   // one scene export for a fresh render pelvis, then the status
            default:
            {
                var p = idlePosed;
                if (hoverSvc.RenderEvidenceNow().call == null && sub == "status") try { await Task.WhenAny(hoverSvc.PoseProbe(), Task.Delay(20000)); } catch { }   // status wants a render, not only the animation list
                var real = IdleRealPose();
                string gu = hoverSvc.HoverGaveUp is string g && (DateTime.Now - hoverSvc.HoverGaveUpAt).TotalMinutes < 30 ? $"; {g}" : "";
                return $"idle pose {(idlePoseOn ? "ON" : "OFF")}; REAL STATE: {real.Text}; {(p == null ? IdleStandingWord(real.State) : $"posed: {p.kind} '{p.pose}' since {p.since:HH:mm:ss}")}; idle {(DateTime.Now - idleActivityAt).TotalSeconds:F0}/{idleThreshold:F0} s; {(IdleCooldownActive(DateTime.Now, idleNoSitUntil) ? $"stand cooldown until {idleNoSitUntil:HH:mm}; " : "")}AO force sit {(AoForceSitOn(SelfAnims(), client.Self.SittingOn != 0) ? "ON" : AoUnclassifiedNow().Count > 0 ? "UNKNOWN (unclassified AO animation)" : "off")} (from the animations, not a stored flag){(aoLastPressReport != null ? $"; last press {aoLastPressAt:HH:mm:ss}: {aoLastPressReport}" : "")}; {IdleRealStateText()}{gu}; " +
                       $"collar {(CollarVisibleNow() ? "visible" : "hidden")}, leash chain {(LeashParticleTarget() != null ? "visible" : "none")}; {feetSvc.LastFitText}; AO force sitg {(AoGroundSitNow() is { } s ? $"link {s.link} face {s.face} st {s.st}" : "NOT mapped")}; last pose {idleLastPose ?? "-"}";
            }
        }
    }
}

// GroundSitEntry (2026-10-28, David: "choose the pose first, sit once"): the order of an AO ground-sit entry, separate from the live calls so a test can run it
// against a fake. Pose chosen while standing -> ONE sit -> ONE force-sit press (read back inside it). A failed pick enters nothing: she stays standing.
internal interface IGroundSitSteps
{
    Task<(bool ok, string info)> PreSelectPose();   // standing: the AO chooser picks the sit (nothing plays yet)
    Task<string> SitDown();                         // ONE SitOnGround; returns when the sim's ground sit plays
    Task<(string press, Task<string> readback)> PressForceSit();   // ONE toggle press; returns at once, the ~11 s read-back runs on (readback null = not pressed)
    bool ChosenPosePlaying();                       // evidence: the chosen AO sit animation started after the press, with the sim sit
}
internal static class GroundSitEntry
{
    // 2026-10-29 (David: "There is an animation when you sit, so it's reasonable to wait a few seconds but 11 is too long")
    internal const int PosePollMs = 250, PoseWaitCapMs = 8000, HoverSettleMs = 1750, FallbackHoverDelayMs = 1000;
    internal sealed record Result(bool entered, string pick, string sit, string press, bool poseSeen, int poseWaitMs, string poseWait);
    /// <summary>Polls the evidence every pollMs until the chosen pose plays or capMs passes. delay is injected (a fake clock in tests).</summary>
    internal static async Task<(bool seen, int waitedMs)> WaitChosenPose(Func<bool> playing, Func<int, Task> delay, int pollMs = PosePollMs, int capMs = PoseWaitCapMs)
    {
        int waited = 0;
        while (true)
        {
            if (playing()) return (true, waited);
            if (waited >= capMs) return (false, waited);
            await delay(pollMs); waited += pollMs;
        }
    }
    /// <summary>Pure: the hover check's delay after the entry returns: pose seen = the settle, stretched so the hover check's own posture settle
    /// (Hover.HoverCheckSettleSec since the sit) is already met when it runs (else it would wait a whole 8 s retry); not seen = the old short delay after the read-back.</summary>
    internal static int HoverDelayMs(bool poseSeen, double msSincePostureChange) =>
        !poseSeen ? FallbackHoverDelayMs : Math.Max(HoverSettleMs, (int)Math.Ceiling(Hover.HoverCheckSettleSec * 1000 + 200 - msSincePostureChange));
    internal static async Task<Result> Run(IGroundSitSteps s, Func<int, Task> delay = null)
    {
        delay ??= ms => Task.Delay(ms);
        var (ok, pick) = await s.PreSelectPose();
        if (!ok) return new(false, pick, null, null, false, 0, null);
        var sit = await s.SitDown();
        var (press, readback) = await s.PressForceSit();
        if (readback == null) return new(true, pick, sit, press, false, 0, "force sit not pressed");
        var (seen, waited) = await WaitChosenPose(s.ChosenPosePlaying, delay);
        if (seen) return new(true, pick, sit, press, true, waited, $"chosen pose playing {waited} ms after the press (hover check after a {HoverSettleMs} ms settle; force-sit read-back continues in the background)");
        var rb = await readback;   // cap hit: the old path, measure only after the read-back
        return new(true, pick, sit, $"{press}; {rb}", false, waited, $"chosen pose NOT seen within {PoseWaitCapMs} ms: hover check after the force-sit read-back");
    }
}
