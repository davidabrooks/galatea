// AutoFollow.cs (2026-10-03, David): follow David automatically when he is near.
// - David Nightingale (44ce5a36-...) in the same region within GT_AUTOFOLLOW_RANGE_M (default 20 m) -> stand up if sitting,
//   stop the wander (flag kept, remembered) and follow him (the normal follow, Follow.cs: ~2.5 m standoff behind him,
//   'follow dist' default; if he sits she just stops 2-3 m away).
// - He leaves the region / logs off (absent for 10 s) -> stop following; restore the earlier wander state (2026-10-05):
//   if wander was on hold/user/ao, put it back on that pause; never start an active wander while she is seated;
//   otherwise restart the wander as before.
// - An explicit 'follow off' (or David saying "stop following" / "stay" in chat or IM) while auto-following: no auto-follow
//   for 10 min (GT_AUTOFOLLOW_SNOOZE_MIN). "follow me" from David lifts that. A manual 'follow <someone else>' is never overridden.
// - 'autofollow on|off|status'; default on; persisted in run/autofollow.txt.
// - Stalled while following (2026-10-05): the robust door sequence / stuck-escape in Follow.cs + Doors.cs (was: one touch per 30 s).
using System.Text.RegularExpressions;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    static readonly UUID DavidId = new("44ce5a36-c1c7-4a68-ac9a-635ddfff6233");
    static readonly string AutoFollowFile = Env("GT_AUTOFOLLOW_FILE", "/home/box/viewers/textclient/run/autofollow.txt");
    static readonly float AutoFollowRange = float.TryParse(Env("GT_AUTOFOLLOW_RANGE_M", "20"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var afr) && afr > 0 ? afr : 20f;
    static readonly TimeSpan AutoFollowSnooze = TimeSpan.FromMinutes(double.TryParse(Env("GT_AUTOFOLLOW_SNOOZE_MIN", "10"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var afs) && afs >= 0 ? afs : 10);
    static bool? autoFollowOnCache;
    static volatile bool afEngaged;            // we started the current follow of David
    static bool afWanderWasOn;                 // wander was running when we engaged (restore it afterwards)
    static string afWanderPause;               // wanderPause at engage (null | hold | user | ao | chat | greet); restore hold/user/ao
    static DateTime afSnoozeUntil = DateTime.MinValue, afDavidLastSeen = DateTime.MinValue, afLastMoveCheck = DateTime.MinValue, afLastDoorTouch = DateTime.MinValue;
    static Vector3 afLastPos; static string afLastRegion; static DateTime afRegionSince = DateTime.MinValue;
    static string afLast = "-";

    static bool AutoFollowOn
    {
        get
        {
            if (autoFollowOnCache is bool b) return b;
            try { autoFollowOnCache = !File.Exists(AutoFollowFile) || File.ReadAllText(AutoFollowFile).Trim() != "off"; } catch { autoFollowOnCache = true; }
            return autoFollowOnCache.Value;
        }
        set
        {
            autoFollowOnCache = value;
            try { Directory.CreateDirectory(Path.GetDirectoryName(AutoFollowFile)!); File.WriteAllText(AutoFollowFile, value ? "on\n" : "off\n"); } catch (Exception ex) { Log("autofollow", "save failed: " + ex.Message); }
        }
    }

    static void AfLog(string m) { afLast = $"{DateTime.Now:HH:mm:ss} {m}"; Log("autofollow", m); }

    // pure (selftest-covered): what should the auto-follow do this tick?
    internal enum AfAction { None, Engage, Release }
    internal static AfAction AutoFollowDecide(bool on, bool engaged, bool davidPresent, float dist, double absentS, bool snoozed, bool followingSomeoneElse, bool busy)
    {
        if (engaged)
        {
            if (!on || followingSomeoneElse) return AfAction.Release;
            if (!davidPresent && absentS >= 10) return AfAction.Release;
            return AfAction.None;
        }
        if (!on || snoozed || followingSomeoneElse || busy || !davidPresent || dist < 0 || dist > AutoFollowRange) return AfAction.None;
        return AfAction.Engage;
    }

    // called every second from Ticker()
    static void AutoFollowTick()
    {
        if (!LoggedIn) return;
        var sim = client.Network.CurrentSim; if (sim == null) return;
        var av = sim.ObjectsAvatars.Values.FirstOrDefault(a => a != null && a.ID == DavidId);
        float dist = -1;
        if (av != null && (av.ParentID == 0 || sim.ObjectsPrimitives.ContainsKey(av.ParentID)))
            dist = Vector3.Distance(PositionHelper.GetAvatarPosition(sim, av), client.Self.SimPosition);
        if (av != null) afDavidLastSeen = DateTime.Now;
        bool followingDavid = followId == DavidId;
        bool someoneElse = followId != UUID.Zero && !followingDavid;
        if (afEngaged && !followingDavid && followId == UUID.Zero) { afEngaged = false; AfLog("follow ended elsewhere (follow cleared)"); ResumeWanderAfterFollow(); return; }
        if (!afEngaged && followingDavid) return; // manual 'follow David': leave it alone
        if (sim.Name != afLastRegion) { afLastRegion = sim.Name; afRegionSince = DateTime.Now; }
        bool busy = RestartActive || (DateTime.Now - afRegionSince).TotalSeconds < 5; // just arrived: let the avatar list fill
        var act = AutoFollowDecide(AutoFollowOn, afEngaged, av != null, dist, (DateTime.Now - afDavidLastSeen).TotalSeconds, DateTime.Now < afSnoozeUntil, someoneElse, busy);
        if (act == AfAction.Engage)
        {
            afWanderWasOn = WanderOn;
            afWanderPause = WanderOn ? wanderPause : null; // capture before StopWander clears it
            if (afWanderWasOn) StopWander("auto-follow David (restores wander state when he leaves)", clearFlag: false);
            if (client.Self.SittingOn != 0) client.Self.Stand();
            followDistThis = null; followId = DavidId; followName = av.Name; afEngaged = true; afLastPos = client.Self.SimPosition; afLastMoveCheck = DateTime.Now;
            var wnote = !afWanderWasOn ? "" : afWanderPause is "hold" or "user" or "ao" ? $" (wander was on {afWanderPause}; will restore)" : " (wander stopped; resumes when he leaves)";
            AfLog($"David is {dist:F1} m away in {sim.Name}: following him{wnote}");
        }
        else if (act == AfAction.Release)
        {
            if (someoneElse) { afWanderWasOn = false; afWanderPause = null; } // she follows someone else now: do not restart the wander under them
            if (followingDavid) { followId = UUID.Zero; try { client.Self.AutoPilotCancel(); } catch { } }
            afEngaged = false;
            AfLog(av == null ? "David left the region / logged off: stopped following" : !AutoFollowOn ? "autofollow turned off: stopped following" : "follow changed: auto-follow released");
            ResumeWanderAfterFollow();
        }
        // stuck at a door while following: Follow.cs FollowStalled -> Doors.cs DoorUnstick (manual and auto-follow alike)
    }

    // pure (selftest-covered): after auto-follow, what wander restore action?
    // hold/user/ao -> restore that pause (even if seated); active wander -> only if standing; else none.
    internal enum AfWanderRestore { None, Start, StartHeld }
    internal static AfWanderRestore AutoFollowWanderRestore(bool wasOn, string pause, bool seated) =>
        !wasOn ? AfWanderRestore.None
        : pause is "hold" or "user" or "ao" ? AfWanderRestore.StartHeld
        : seated ? AfWanderRestore.None
        : AfWanderRestore.Start;

    static void ResumeWanderAfterFollow()
    {
        if (!afWanderWasOn) { afWanderPause = null; return; }
        var wasOn = afWanderWasOn; var pause = afWanderPause;
        afWanderWasOn = false; afWanderPause = null;
        _ = Task.Run(async () =>
        {
            await Task.Delay(3000);
            if (WanderOn || afEngaged) return;
            if (!InNaberrie) { AfLog("not restoring the wander (not in Naberrie)"); return; }
            bool seated = client.Self.SittingOn != 0;
            var act = AutoFollowWanderRestore(wasOn, pause, seated);
            if (act == AfWanderRestore.None)
            {
                AfLog(seated ? "not restarting the wander (she is seated; was not on hold)" : "not restoring the wander");
                return;
            }
            var r = StartWander("after auto-follow");
            if (act == AfWanderRestore.StartHeld && WanderOn)
            {
                SetPause(pause, $"restored after auto-follow (was on {pause} before David)");
                AfLog($"restored wander on {pause}: {r}");
            }
            else AfLog("restarting the wander: " + r);
        });
    }

    // explicit 'follow off' (command) while auto-following: snooze
    static void AutoFollowOnFollowOff(string why)
    {
        if (!afEngaged && followId != DavidId) return;
        afEngaged = false; afSnoozeUntil = DateTime.Now + AutoFollowSnooze;
        AfLog($"{why}: not auto-following David until {afSnoozeUntil:HH:mm} PT");
    }

    static readonly Regex AfStopRx = new(@"\b(stop following|don'?t follow|do not follow|stay (here|there|put)|wait here|unfollow)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex AfFollowRx = new(@"\b(follow me|come with me|come along)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // chat / IM from David (Program.Hook / HandleIm)
    static void AutoFollowFromDavid(UUID from, string text)
    {
        if (from != DavidId || string.IsNullOrEmpty(text) || !AutoFollowOn) return;
        if (AfStopRx.IsMatch(text))
        {
            bool wasFollowing = followId == DavidId;
            if (wasFollowing) { followId = UUID.Zero; try { client.Self.AutoPilotCancel(); } catch { } }
            afEngaged = false; afSnoozeUntil = DateTime.Now + AutoFollowSnooze;
            AfLog($"David said '{(text.Length > 60 ? text[..60] + "…" : text)}': {(wasFollowing ? "stopped following; " : "")}no auto-follow until {afSnoozeUntil:HH:mm} PT");
        }
        else if (AfFollowRx.IsMatch(text) && DateTime.Now < afSnoozeUntil)
        {
            afSnoozeUntil = DateTime.MinValue;
            AfLog("David said 'follow me': auto-follow snooze lifted");
        }
    }

    static string AutoFollowCmd(string[] a)
    {
        var sub = a.Length > 0 ? a[0].ToLowerInvariant() : "status";
        if (sub == "on") { AutoFollowOn = true; afSnoozeUntil = DateTime.MinValue; AfLog("turned on"); }
        else if (sub == "off") { AutoFollowOn = false; AfLog("turned off"); if (afEngaged) { if (followId == DavidId) { followId = UUID.Zero; try { client.Self.AutoPilotCancel(); } catch { } } afEngaged = false; ResumeWanderAfterFollow(); } }
        else if (sub == "selftest") return AutoFollowSelfTest();
        else if (sub != "status") return "usage: autofollow on|off|status|selftest";
        var snooze = DateTime.Now < afSnoozeUntil ? $"; snoozed until {afSnoozeUntil:HH:mm} PT (explicit follow off / 'stop following')" : "";
        return $"autofollow {(AutoFollowOn ? "ON" : "OFF")} (David within {AutoFollowRange:F0} m in the same region){snooze}; " +
               $"{(afEngaged ? "FOLLOWING David now" : followId == DavidId ? "following David (manual)" : "not following David")}" +
               $"{(afWanderWasOn ? "; wander resumes when he leaves" : "")}; last: {afLast}";
    }

    static string AutoFollowSelfTest()
    {
        var sb = new System.Text.StringBuilder(); int pass = 0, fail = 0; void C(bool ok, string w) { if (ok) pass++; else fail++; sb.AppendLine($"{(ok ? "PASS" : "FAIL")} {w}"); }
        C(AutoFollowDecide(true, false, true, 12, 0, false, false, false) == AfAction.Engage, "David 12 m away -> follow");
        C(AutoFollowDecide(true, false, true, 35, 0, false, false, false) == AfAction.None, "David 35 m away -> no");
        C(AutoFollowDecide(true, false, true, -1, 0, false, false, false) == AfAction.None, "David position unknown -> no");
        C(AutoFollowDecide(false, false, true, 5, 0, false, false, false) == AfAction.None, "autofollow off -> no");
        C(AutoFollowDecide(true, false, true, 5, 0, true, false, false) == AfAction.None, "snoozed after 'follow off' -> no");
        C(AutoFollowDecide(true, false, true, 5, 0, false, true, false) == AfAction.None, "following someone else -> never overridden");
        C(AutoFollowDecide(true, false, true, 5, 0, false, false, true) == AfAction.None, "teleport / region restart in progress -> no");
        C(AutoFollowDecide(true, true, true, 40, 0, false, false, false) == AfAction.None, "engaged, David walks 40 m away (same region) -> keep following");
        C(AutoFollowDecide(true, true, false, -1, 4, false, false, false) == AfAction.None, "engaged, David missing 4 s -> wait");
        C(AutoFollowDecide(true, true, false, -1, 11, false, false, false) == AfAction.Release, "engaged, David gone 11 s -> release");
        C(AutoFollowDecide(false, true, true, 3, 0, false, false, false) == AfAction.Release, "engaged, turned off -> release");
        C(AfStopRx.IsMatch("ok stop following me") && AfStopRx.IsMatch("Stay here babe") && AfStopRx.IsMatch("don't follow me") && !AfStopRx.IsMatch("I'll stay online a bit"), "stop phrases ('stop following', 'stay here', 'don't follow'; not 'stay online')");
        C(AfFollowRx.IsMatch("follow me") && !AfFollowRx.IsMatch("I follow the news"), "'follow me' detection");
        C(AutoFollowWanderRestore(false, null, false) == AfWanderRestore.None, "wander was off -> no restore");
        C(AutoFollowWanderRestore(true, "hold", false) == AfWanderRestore.StartHeld && AutoFollowWanderRestore(true, "hold", true) == AfWanderRestore.StartHeld, "was on hold -> restore hold (even if seated)");
        C(AutoFollowWanderRestore(true, "user", true) == AfWanderRestore.StartHeld && AutoFollowWanderRestore(true, "ao", false) == AfWanderRestore.StartHeld, "was on user/ao -> restore that pause");
        C(AutoFollowWanderRestore(true, null, true) == AfWanderRestore.None, "active wander + seated -> do NOT resume");
        C(AutoFollowWanderRestore(true, null, false) == AfWanderRestore.Start && AutoFollowWanderRestore(true, "chat", false) == AfWanderRestore.Start, "active / chat pause + standing -> restart");
        return $"autofollow selftest: {pass} PASS, {fail} FAIL\n" + sb.ToString().TrimEnd();
    }
}
