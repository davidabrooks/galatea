// AdultRedress.cs (2026-10-16, David: "you just chose an adult pose without getting undressed first").
// A sexual pose (SeatPose.cs: PosePathIsSexual, which also reads the parent menus and, on an adult-looking seat, unknown
// submenu names) is only taken after wearing the Naked outfit. 2026-10-16 (David: standing up is too slow): she changes
// WHILE SEATED, on the same seat, then picks the pose from the menu: no stand-up, no step to clear floor, no sit again. The outfit
// worn BEFORE is remembered here, and put back (same outfit, HUD colours kept) as soon as the adult pose is over: the seat shows
// a pose that is not sexual any more (a switch because someone entered our parcel, David left, wander changing the pose), else
// when she gets up off the seat.
// 2026-11-04 (David: "I think it looks better if you don't get dressed after an adult pose until you change poses or get up"):
// wander no longer re-dresses her WHILE SEATED at the end of a stay (the old NakedRedressBeforeStand, called before she stood). She
// stands as she is, and the tick dresses her as soon as she is off the seat (NakedRedressDue: `seated` = false). The mid-sit switch to
// a non-adult pose keeps its re-dress at the switch (that is "change poses"). Nothing else changed: not the quick-relog rule, not the
// Naked-first undressing, not the birthday-suit / toilet / bikini flows, not RedressTarget.
// Never while the Naked flow itself is running, and not while an adult pose is still running.
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    static string nakedRedressOutfit;            // the outfit to put back (null = nothing pending)
    static volatile bool nakedFlowBusy;          // Naked / pose in progress: no re-dress in between
    static DateTime nakedAt = DateTime.MinValue, nakedRetryAt = DateTime.MinValue;
    static int nakedRedressBusy;

    internal static bool NakedWornNow() => NakedNameRx.IsMatch((lastNamedOutfit ?? "").Trim());

    // pure: the outfit to put back after an adult pose. The one worn before (never Naked itself), else the beach Bikini on the
    // beach level, else the first daily outfit.
    internal static string RedressTarget(string worn, bool beach, string dailyFirst)
    {
        if (!string.IsNullOrWhiteSpace(worn) && !NakedNameRx.IsMatch(worn.Trim())) return worn.Trim();
        if (beach) return "Bikini";
        return string.IsNullOrWhiteSpace(dailyFirst) ? null : dailyFirst.Trim();
    }

    // pure: is it time to put the outfit back? (due, why)
    internal static (bool due, string why) NakedRedressDue(string pending, bool flowBusy, bool seated, bool sexualPoseNow, double sinceNakedS, bool outfitBusy, bool quickRelog = false)
    {
        if (string.IsNullOrWhiteSpace(pending)) return (false, "nothing pending");
        // 2026-11-03 16:25 / 18:03 David: a quick naked relog stands her up on the way out; that stand is not the end of the adult pose
        if (quickRelog) return (false, "quick relog: she stays as she is");
        if (flowBusy) return (false, "the Naked flow is still running");
        if (outfitBusy) return (false, "another outfit change is running");
        // 2026-11-03 14:48 David: adult -> adult on the same seat never re-dresses. 16:33 David: adult -> NON-adult re-dresses at the
        // switch itself (LeaveAdultPoseBeforeSwitch, SeatPose.cs); this tick only covers leaving the seat.
        // 2026-11-04 David: and that is the ONLY moment she dresses before getting up -- she stays as she is for the rest of the stay,
        // including its end, and stands undressed.
        if (seated) return (false, sexualPoseNow ? "the adult pose is still running" : "still on the seat: she stays as she is until the pose changes or she gets up");
        return (true, "stood up");
    }

    static Primitive SeatedSeatRoot()
    {
        try
        {
            var sim = client.Network.CurrentSim; var id = client.Self.SittingOn; if (sim == null || id == 0) return null;
            if (!sim.ObjectsPrimitives.TryGetValue(id, out var p) || p == null) return null;
            if (p.ParentID != 0 && sim.ObjectsPrimitives.TryGetValue(p.ParentID, out var root) && root != null) p = root;
            return p;
        }
        catch { return null; }
    }
    static string SeatedSeatName() => SeatedSeatRoot()?.Properties?.Name;
    static string SeatedSeatKey() => SeatedSeatRoot()?.ID.ToString();

    // is the pose she is in now a sexual one (parent menus included)?
    static bool SexualPoseRunningNow() => client.Self.SittingOn != 0 && PosePathIsSexual(wLastPosePath, PoseSeatIsAdult(SeatedSeatKey(), SeatedSeatName()));

    static void NakedRedressTick()
    {
        if (nakedRedressOutfit == null || !LoggedIn || DateTime.Now < nakedRetryAt) return;
        var (due, why) = NakedRedressDue(nakedRedressOutfit, nakedFlowBusy, client.Self.SittingOn != 0, SexualPoseRunningNow(), (DateTime.Now - nakedAt).TotalSeconds, beachOutfitBusy, QuickRelogSet(QuickRelogPath));
        if (due) _ = Task.Run(() => NakedRedressRun(why));
    }

    static async Task NakedRedressRun(string why)
    {
        if (Interlocked.Exchange(ref nakedRedressBusy, 1) == 1) return;
        var name = nakedRedressOutfit;
        try
        {
            if (name == null) return;
            await TheVFaceStop("re-dress (takes The V HUD off)");
            WLog($"REDRESS: adult pose over ({why}); putting '{name}' back on{(client.Self.SittingOn != 0 ? " (seated)" : "")}");
            var r = BikiniNameRx.IsMatch(name) ? await BikiniOn() : await WearOutfitWithHuds(name, keepColor: true);
            nakedRedressOutfit = null;
            WLog($"REDRESSED in '{name}': " + r.Replace("\n", " | ")[..Math.Min(300, r.Length)]);
            try { WLog("topless extras off: " + await ToplessExtrasOff()); } catch (Exception ex) { WLog("topless extras off failed: " + ex.GetBaseException().Message); }
        }
        catch (Exception ex) { nakedRetryAt = DateTime.Now.AddSeconds(30); WLog($"REDRESS in '{name}' failed (retry in 30 s): " + ex.GetBaseException().Message); }
        finally { nakedRedressBusy = 0; }
    }

    // quick relog (2026-11-03 David: "do you really need to get dressed when you're doing a quick naked relog?"): run/quick-relog is written by
    // 'logout quick' (MCP logout quick_relog=true, text-galatay.sh stop quick) before the logout stand and removed once the next login is in.
    // It only holds back the automatic re-dress after a stand; wearing an outfit by command is never blocked.
    static readonly string QuickRelogPath = Env("GT_QUICK_RELOG", "/home/box/viewers/textclient/run/quick-relog");
    internal static bool QuickRelogSet(string path) { try { return File.Exists(path); } catch { return false; } }
    internal static void QuickRelogMark(string path, string why) { try { File.WriteAllText(path, $"quick relog requested at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ({why})\n"); } catch (Exception ex) { Log("warn", "quick-relog flag write failed: " + ex.Message); } }
    internal static bool QuickRelogClear(string path) { try { if (!File.Exists(path)) return false; File.Delete(path); return true; } catch (Exception ex) { Log("warn", "quick-relog flag clear failed: " + ex.Message); return false; } }

    // pure (tested): what to do before a seat-menu pose switch. 2026-11-03 16:33 David: the mid-sit change went Vibrator -> Curled up,
    // stopped the face but left her naked. Next pose not adult -> The V STOP then re-dress (NakedRedressRun stops the face first), before the switch.
    internal static string AdultExitStep(bool nextSexual, bool redressPending, bool faceOn)
        => nextSexual ? null : redressPending ? "redress" : faceOn ? "face-stop" : null;

    static async Task LeaveAdultPoseBeforeSwitch(bool nextSexual)
    {
        switch (AdultExitStep(nextSexual, nakedRedressOutfit != null && !nakedFlowBusy, theVFaceOn))
        {
            case "redress": await NakedRedressRun("switching to a non-adult pose"); for (int k = 0; k < 240 && Volatile.Read(ref nakedRedressBusy) == 1; k++) await Task.Delay(250); break;
            case "face-stop": await TheVFaceStop("switching to a non-adult pose"); break;
        }
    }

    // 2026-11-04 David: no re-dress while she is still seated at the end of a stay. The old NakedRedressBeforeStand ("put it back while
    // still seated, then she stands") is gone; the seated re-dress at a mid-sit switch to a non-adult pose stays (LeaveAdultPoseBeforeSwitch,
    // "change poses"), and the tick dresses her as soon as she is off the seat (NakedRedressDue: `seated` false, "get up").
    // So there is exactly ONE place that puts the outfit back during a stay: AdultExitStep at a pose switch. Every stand -- the wander stay
    // ending, a chat pause that then walks off, David's hold, a seat stood up by something else, a boat leave -- leaves her as she is and the
    // tick (ParcelWatchTick, every second) dresses her once client.Self.SittingOn is 0.

    // the Naked-first step of SeatPose: remember what she wears, wear Naked right where she sits (no stand-up, no step to clear
    // floor, no sitting again), then take the pose through the seat menu. Returns an abort message, or null when the pose was
    // taken (path result in `result`). A failed change aborts the adult pose; the pending re-dress puts the old outfit back.
    static async Task<(string abort, string result)> NakedFirstThenPose(Primitive seat, string seatName, List<string> full, CancellationToken ct)
    {
        nakedFlowBusy = true;
        try
        {
            if (client.Self.SittingOn == 0) return ($"'{seatName}': not seated any more; not taking the sexual pose", null);
            if (!NakedWornNow() && nakedRedressOutfit == null)
            {
                nakedRedressOutfit = RedressTarget(lastNamedOutfit, beachMode, DailyOutfitAllow().FirstOrDefault());
                WLog($"POSE naked-first: will put '{nakedRedressOutfit ?? "?"}' back on after the adult pose");
            }
            nakedAt = DateTime.Now;
            string w = "already on";
            if (NakedWornNow()) WLog("POSE naked-first: the Naked outfit is already on; seated, no change needed");
            else
            {
                try { w = await OutfitWearNamed("Naked", true); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { w = "outfit change failed: " + ex.GetBaseException().Message; }
                WLog($"POSE naked-first (seated): {w}");
                await Task.Delay(3000, ct);
            }
            if (!NakedWornNow()) return ($"'{seatName}': the Naked outfit is not on ({w.Split('\n')[0]}); not taking the sexual pose", null);
            if (client.Self.SittingOn == 0) return ($"'{seatName}': no longer seated after undressing; not taking the sexual pose", null);
            if (!SexualPosesAllowedNow()) return ($"'{seatName}': someone else arrived while undressing; not taking the sexual pose", null);
            var r = await SeatPosePath(seat, seatName, full, ct);
            return (null, r);
        }
        finally { nakedAt = DateTime.Now; nakedFlowBusy = false; }
    }
}
