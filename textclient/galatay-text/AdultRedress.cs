// AdultRedress.cs (2026-10-16, David: "you just chose an adult pose without getting undressed first").
// A sexual pose (SeatPose.cs: PosePathIsSexual, which also reads the parent menus and, on an adult-looking seat, unknown
// submenu names) is only taken after wearing the Naked outfit. 2026-10-16 (David: standing up is too slow): she changes
// WHILE SEATED, on the same seat, then picks the pose from the menu: no stand-up, no step to clear floor, no sit again. The outfit
// worn BEFORE is remembered here, and put back (same outfit, HUD colours kept) as soon as the adult pose is over: the seat shows
// a pose that is not sexual any more (a switch because someone entered our parcel, David left, wander changing the pose), or
// wander's stay ends (put back before she stands, NakedRedressBeforeStand), else when she stands up for any other reason.
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
    internal static (bool due, string why) NakedRedressDue(string pending, bool flowBusy, bool seated, bool sexualPoseNow, double sinceNakedS, bool outfitBusy)
    {
        if (string.IsNullOrWhiteSpace(pending)) return (false, "nothing pending");
        if (flowBusy) return (false, "the Naked flow is still running");
        if (outfitBusy) return (false, "another outfit change is running");
        if (seated && sexualPoseNow) return (false, "the adult pose is still running");
        if (seated && sinceNakedS < 20) return (false, "pose choice still settling");
        return (true, seated ? "the pose is no longer sexual" : "stood up");
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
        var (due, why) = NakedRedressDue(nakedRedressOutfit, nakedFlowBusy, client.Self.SittingOn != 0, SexualPoseRunningNow(), (DateTime.Now - nakedAt).TotalSeconds, beachOutfitBusy);
        if (due) _ = Task.Run(() => NakedRedressRun(why));
    }

    static async Task NakedRedressRun(string why)
    {
        if (Interlocked.Exchange(ref nakedRedressBusy, 1) == 1) return;
        var name = nakedRedressOutfit;
        try
        {
            if (name == null) return;
            WLog($"REDRESS: adult pose over ({why}); putting '{name}' back on{(client.Self.SittingOn != 0 ? " (seated)" : "")}");
            var r = BikiniNameRx.IsMatch(name) ? await BikiniOn() : await WearOutfitWithHuds(name, keepColor: true);
            nakedRedressOutfit = null;
            WLog($"REDRESSED in '{name}': " + r.Replace("\n", " | ")[..Math.Min(300, r.Length)]);
            try { WLog("topless extras off: " + await ToplessExtrasOff()); } catch (Exception ex) { WLog("topless extras off failed: " + ex.GetBaseException().Message); }
        }
        catch (Exception ex) { nakedRetryAt = DateTime.Now.AddSeconds(30); WLog($"REDRESS in '{name}' failed (retry in 30 s): " + ex.GetBaseException().Message); }
        finally { nakedRedressBusy = 0; }
    }

    // wander's stay on the seat is over and the outfit is still pending: put it back while still seated, then she stands
    // (the retry timer after a failure is respected: she stands anyway and the tick retries)
    static async Task NakedRedressBeforeStand(string why)
    {
        if (nakedRedressOutfit == null || !LoggedIn || nakedFlowBusy || beachOutfitBusy || client.Self.SittingOn == 0 || DateTime.Now < nakedRetryAt) return;
        await NakedRedressRun(why);   // returns at once when the tick is already running it: wait for that one below
        for (int k = 0; k < 240 && Volatile.Read(ref nakedRedressBusy) == 1; k++) await Task.Delay(250);
    }

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
