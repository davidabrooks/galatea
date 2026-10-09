// 2026-10-09 David: "When I sit down on the couch with you and pick a couples pose, stop wandering and stay in the pose
// with me." At 09:36 PT he sat on the Lalou sofa (log: 'pose: seat occupancy -> shared', then a seat-sourced pose change
// 6c3cb302 and 23d3770e that she did not pick), but a chat pause ended at 09:38:11 and the wander stood her up.
// Rule: while wandering and seated, David Nightingale on the same linkset => wander pause "david": no stand, no pose
// change, no next leg. Resume only when he leaves the seat (then ~30 s) or tells her to (wander resume / instant request).
// Signals: Sitters() root of her SittingOn has David (by UUID or name), refreshed ~1 s by the seat maintenance tick and on
// every own-anim change; a seat-sourced pose starting while he is there and she did not just press a menu = his pick.
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    public static double DavidSeatResumeS = 30;
    static DateTime? wDavidLeftSeatAt;

    // pure: should a "david" hold begin now? Replaces automatic pauses (chat/greet/rest) only; an explicit
    // 'wander pause/hold' or the AO pause keeps needing an explicit resume.
    public static bool DavidSeatHoldStarts(bool wanderOn, bool seated, bool davidOnSeat, string pause) =>
        wanderOn && seated && davidOnSeat && (pause is null or "chat" or "greet" or "rest");

    // pure: a "david" hold may end once David has been off her seat for resumeS (she may be seated or not).
    public static bool DavidSeatHoldCanResume(bool davidOnSeat, DateTime? davidLeftAt, DateTime now, double resumeS) =>
        !davidOnSeat && davidLeftAt != null && (now - davidLeftAt.Value).TotalSeconds >= resumeS;

    // pure: automatic pauses (chat/greet/rest/ao) never replace a "david" hold; explicit user/hold commands do.
    public static string PauseAfterRequest(string current, string requested) =>
        current == "david" && requested is "chat" or "greet" or "rest" ? "david" : requested;

    // pure: which anims the pose keeper may re-assert. With David on the seat only what the seat itself plays now
    // (his pick); never a kept copy of her earlier solo pose.
    public static List<UUID> KeeperReassertSet(IReadOnlyCollection<UUID> seatSourced, IReadOnlyCollection<UUID> keptPlaying, bool davidOnSeat) =>
        seatSourced.Count > 0 ? seatSourced.ToList() : davidOnSeat ? new List<UUID>() : keptPlaying.ToList();

    // pure: a seat-sourced pose that starts while David shares the seat and she has not pressed a pose button lately is his pick.
    public static bool PoseChangeIsDavids(bool davidOnSeat, bool seatSourced, bool restart, double sinceOwnMenuS) =>
        davidOnSeat && seatSourced && !restart && sinceOwnMenuS > 5;

    // David on the same linkset as my seat (UUID first, then the display name Sitters() records).
    static bool DavidOnMySeatNow()
    {
        try
        {
            var sim = client?.Network?.CurrentSim; uint me = client?.Self?.SittingOn ?? 0;
            if (sim == null || me == 0) return false;
            uint Root(uint lid) => sim.ObjectsPrimitives.TryGetValue(lid, out var p) && p != null && p.ParentID != 0 && sim.ObjectsPrimitives.ContainsKey(p.ParentID) ? p.ParentID : lid;
            uint myRoot = Root(me);
            foreach (var a in sim.ObjectsAvatars.Values)
                if (a != null && a.ID == DavidId && a.ParentID != 0 && Root(a.ParentID) == myRoot) return true;
        }
        catch { }
        return false;
    }

    // Called when David is seen on my seat (occupancy tick or his pose pick). Idempotent.
    static void DavidSeatHoldCheck(bool davidHere, string signal)
    {
        if (davidHere) wDavidLeftSeatAt = null;
        if (!DavidSeatHoldStarts(WanderOn, client?.Self?.SittingOn != 0, davidHere, wanderPause)) return;
        wDavidLeftSeatAt = null;
        SetPauseNow("david", $"David is on my seat ({signal}); staying in the pose with him until he gets up (+{DavidSeatResumeS:g} s) or says to go on");
    }

    // Wander loop, pause == "david": true when the hold is over (he left the seat ~30 s ago).
    static bool DavidSeatHoldTick()
    {
        bool here = client.Self.SittingOn != 0 && (poseDavidOnSeat || DavidOnMySeatNow());
        if (here) { wDavidLeftSeatAt = null; return false; }
        if (wDavidLeftSeatAt == null) { wDavidLeftSeatAt = DateTime.Now; WLog($"David left my seat: resuming the wander in {DavidSeatResumeS:g} s unless he comes back"); }
        if (!DavidSeatHoldCanResume(false, wDavidLeftSeatAt, DateTime.Now, DavidSeatResumeS)) return false;
        WLog($"RESUME: David off my seat for {(DateTime.Now - wDavidLeftSeatAt.Value).TotalSeconds:F0} s (held {(DateTime.Now - wPausedAt).TotalSeconds:F0} s with him)");
        wDavidLeftSeatAt = null;
        return true;
    }
}
