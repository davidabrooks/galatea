// AdultRedress.cs (2026-10-16, David: "you just chose an adult pose without getting undressed first").
// A sexual pose (SeatPose.cs: PosePathIsSexual, which also reads the parent menus and, on an adult-looking seat, unknown
// submenu names) is only taken after standing, wearing the Naked outfit and sitting again. The outfit worn BEFORE is remembered
// here, and put back (same outfit, HUD colours kept) as soon as the adult pose is over: she stands up, or the seat shows a
// pose that is not sexual any more (a switch because someone entered our parcel, David left, wander changing the pose).
// Never while the Naked flow itself is running, and not while an adult pose is still running.
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    static string nakedRedressOutfit;            // the outfit to put back (null = nothing pending)
    static volatile bool nakedFlowBusy;          // stand / Naked / sit / pose in progress: no re-dress in between
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
            WLog($"REDRESS: adult pose over ({why}); putting '{name}' back on");
            var r = BikiniNameRx.IsMatch(name) ? await BikiniOn() : await WearOutfitWithHuds(name, keepColor: true);
            nakedRedressOutfit = null;
            WLog($"REDRESSED in '{name}': " + r.Replace("\n", " | ")[..Math.Min(300, r.Length)]);
            try { WLog("topless extras off: " + await ToplessExtrasOff()); } catch (Exception ex) { WLog("topless extras off failed: " + ex.GetBaseException().Message); }
        }
        catch (Exception ex) { nakedRetryAt = DateTime.Now.AddSeconds(30); WLog($"REDRESS in '{name}' failed (retry in 30 s): " + ex.GetBaseException().Message); }
        finally { nakedRedressBusy = 0; }
    }

    // the Naked-first step of SeatPose: remember what she wears, then stand, wear Naked, sit again and take the pose.
    // Returns an abort message, or null when the pose was taken (path result in `result`).
    static async Task<(string abort, string result)> NakedFirstThenPose(Primitive seat, string seatName, List<string> full, CancellationToken ct)
    {
        nakedFlowBusy = true;
        try
        {
            if (!NakedWornNow() && nakedRedressOutfit == null)
            {
                nakedRedressOutfit = RedressTarget(lastNamedOutfit, beachMode, DailyOutfitAllow().FirstOrDefault());
                WLog($"POSE naked-first: will put '{nakedRedressOutfit ?? "?"}' back on after the adult pose");
            }
            nakedAt = DateTime.Now;
            client.Self.Stand(); await Task.Delay(2500, ct);
            var w = await OutfitWearNamed("Naked", true); WLog($"POSE naked-first: {w}");
            await Task.Delay(3000, ct);
            if (!NakedWornNow()) return ($"'{seatName}': the Naked outfit is not on ({w.Split('\n')[0]}); not taking the sexual pose", null);
            if (!SexualPosesAllowedNow()) return ($"'{seatName}': someone else arrived while undressing; not taking the sexual pose", null);
            client.Self.RequestSit(seat.ID, Vector3.Zero); await Task.Delay(1200, ct); client.Self.Sit(); await Task.Delay(3000, ct);
            var r = await SeatPosePath(seat, seatName, full, ct);
            return (null, r);
        }
        finally { nakedAt = DateTime.Now; nakedFlowBusy = false; }
    }
}
