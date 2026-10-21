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
    internal static double IdleThresholdSec(double baseSec, double r01) => baseSec * (0.75 + 0.5 * Math.Clamp(r01, 0, 1));
    internal readonly record struct IdleIn(bool enabled, bool leashed, bool walkerDriving, bool following, bool sittingOnObject, bool moving,
        bool forceSat, bool sitLocked, bool busy, double idleSec, double thresholdSec, bool collarVisible, bool leashVisible, bool groundSitMapped);
    // pure (tested): enter which pose now (null = not now)
    internal static string IdleEnter(IdleIn s)
    {
        if (!s.enabled || s.walkerDriving || s.following || s.sittingOnObject || s.moving || s.forceSat || s.sitLocked || s.busy) return null;
        if (s.idleSec < s.thresholdSec) return null;
        var k = IdlePoseKind(s.collarVisible, s.leashVisible);
        if (k == "groundsit" && (s.leashed || !s.groundSitMapped)) return null;   // never the AO ground sit on a leash
        return k;
    }
    // pure (tested): must the pose be cleared now?
    internal static string IdleMustExit(string kind, bool following, bool sittingOnObject, bool moving, bool forceSat, bool leashed, bool davidWalkingAway, bool walkerWants)
        => kind == null ? null
         : following ? "follow" : sittingOnObject || forceSat ? "sit" : moving ? "moving" : davidWalkingAway ? "David walks away"
         : walkerWants ? "walker" : kind == "groundsit" && leashed ? "leashed" : null;
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
    internal static (int face, string st)? AoGroundSitSpot(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text ?? "", @"^\s*(?<f>\d+)\s+(?<u>[01]?\.\d+|[01]),(?<v>[01]?\.\d+|[01])\s*$", System.Text.RegularExpressions.RegexOptions.Multiline);
        return m.Success ? (int.Parse(m.Groups["f"].Value), $"{m.Groups["u"].Value},{m.Groups["v"].Value}") : null;
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
    static (int face, string st)? AoGroundSitNow() { try { return File.Exists(AoGroundSitFile) ? AoGroundSitSpot(File.ReadAllText(AoGroundSitFile)) : null; } catch { return null; } }
    static void IdleNoteActivity() { idleActivityAt = DateTime.Now; }
    static void IdleSave() { try { var s = idlePosed; if (s == null) File.Delete(IdlePoseFile); else File.WriteAllText(IdlePoseFile, JsonSerializer.Serialize(s)); } catch { } }
    static IdlePoseState IdleLoad() { try { return File.Exists(IdlePoseFile) ? JsonSerializer.Deserialize<IdlePoseState>(File.ReadAllText(IdlePoseFile)) : null; } catch { return null; } }

    static bool CollarVisibleNow()
    {
        try
        {
            var mm = MatchAttachment("Premium Collar", WornPrims()); if (mm.Count != 1) return true;   // unknown: assume visible (human outfits)
            foreach (var p in LinkPrims(mm[0]))
            {
                var te = p.Textures; if (te == null) continue;
                if (te.DefaultTexture != null && te.DefaultTexture.RGBA.A > 0.05f) return true;
                for (int f = 0; f < 45; f++) { var fe = te.FaceTextures[f]; if (fe != null && fe.RGBA.A > 0.05f) return true; }
            }
            return false;
        }
        catch { return true; }
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
        var r = await Exec($"touch-attachment \"ao\" \"1\" {spot.face} st={spot.st}");
        Log("idlepose", $"AO force sitg toggled ({why}): {r}");
        return r;
    }

    static async Task<string> IdlePoseEnter(string kind, string why)
    {
        if (!await idleLock.WaitAsync(0)) return "idle pose busy";
        try
        {
            if (idlePosed != null) return $"already posed ({idlePosed.kind} {idlePosed.pose})";
            if (kind == "collar")
            {
                var pose = PickIdlePose(idleLastPose, Random.Shared.NextDouble());
                var (ok, info, anims) = await CollarPosePlay(pose);
                Log("idlepose", $"collar pose {pose} ({why}): {info}");
                if (!ok) return info;
                idleLastPose = pose; idlePosed = new("collar", pose, anims ?? new(), DateTime.Now); IdleSave();
                return $"collar pose '{pose}': {info}";
            }
            var before = SelfAnims().ToHashSet();
            var r = await AoGroundSitToggle("enter: " + why);
            if (r.StartsWith("AO 'force sitg' button not mapped")) return r;
            await Task.Delay(2500);
            var added = SelfAnims().Where(a => !before.Contains(a)).Select(a => a.ToString()).ToList();
            idlePosed = new("groundsit", "force sitg", added, DateTime.Now); IdleSave();
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
                info = await AoGroundSitToggle("exit: " + why);
                await Task.Delay(1500);
                var still = s.anims.Where(a => SelfAnims().Any(x => x.ToString() == a)).ToList();
                if (still.Count > 0) { try { client.Self.Stand(); } catch { } info += "; stood up"; }
            }
            idlePosed = null; IdleSave(); IdleNoteActivity();
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
        idleLoginAt = DateTime.Now; IdleNoteActivity();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(20000);
                if (!LoggedIn) return;
                var saved = IdleLoad(); idlePosed = null;
                if (saved?.kind == "groundsit") { Log("idlepose", "login: leftover AO ground sit -> off: " + await AoGroundSitToggle("login reset")); }
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
        if (moving || following || sitObj || leashDriving) IdleNoteActivity();
        var posed = idlePosed;
        if (posed != null)
        {
            var why = IdleMustExit(posed.kind, following, sitObj, moving, forceSat, l != null, davidAway, false);
            if (why != null) await IdlePoseExit(why);
            return;
        }
        if (davidAway) IdleNoteActivity();
        var kind = IdleEnter(new IdleIn(idlePoseOn, l != null, leashDriving, following, sitObj, moving, forceSat,
            rlv.Has("sit") || rlv.Has("unsit") && sitObj, leashForgetBusy != 0 || shuttingDown,
            (DateTime.Now - idleActivityAt).TotalSeconds, idleThreshold, CollarVisibleNow(), LeashParticleTarget() != null, AoGroundSitNow() != null));
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
            case "stop": case "stand": return await IdlePoseExit("manual");
            case "idle" when a.Length > 1 && double.TryParse(a[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sec) && sec >= 30:
                idleThreshold = sec; return $"idle threshold {sec:F0} s (this round; base GT_IDLE_POSE_SEC={IdleBaseSec():F0})";
            case "aomap" when a.Length == 3: File.WriteAllText(AoGroundSitFile, $"{a[1]} {a[2]}\n"); return $"AO 'force sitg' mapped: face {a[1]} st {a[2]}";
            default:
            {
                var p = idlePosed;
                return $"idle pose {(idlePoseOn ? "ON" : "OFF")}; {(p == null ? "standing" : $"posed: {p.kind} '{p.pose}' since {p.since:HH:mm:ss}")}; idle {(DateTime.Now - idleActivityAt).TotalSeconds:F0}/{idleThreshold:F0} s; " +
                       $"collar {(CollarVisibleNow() ? "visible" : "hidden")}, leash chain {(LeashParticleTarget() != null ? "visible" : "none")}; AO force sitg {(AoGroundSitNow() is { } s ? $"face {s.face} st {s.st}" : "NOT mapped")}; last pose {idleLastPose ?? "-"}";
            }
        }
    }
}
