// Movers.cs (2026-11-23, David via dr eggbot, 5:00 PM PT): ONE MOVER AT A TIME.
// At 16:55 a follow of David that I started in Ahern (16:48) survived his lure home (by design: the lure sender is followed),
// then 'wander sit sofa' started the wander's seat walk while the follow kept backing off / closing in on him: the two fought
// for the autopilot and the seat walk ended STUCK 7.7 m short of the sofa.
// Rule: every self-directed mover asks MoverClaim before it starts. The claim stops the movers already running (follow, roam,
// a walk task, a wander-sit visit, the autopilot) and logs one [mover] line saying what it stopped. The one central decision is
// the pure MoverPlanFor (tested); the entry points (follow, walk_to/moveto/walk/walk_path/goto_avatar/sit_near/route walk via
// StartWalk, wander sit, sit, roam, come-here, teleport, accepting a teleport offer) only call MoverClaim.
// Priorities:
//   - Leash / RLV first: while LEASHED (collar leash mode, not the collar's follow mode) a wander-sit visit, a roam or a follow
//     of anyone but the holder is refused (the leash leads; the wander is already held under a leash; a strict leash's teleport
//     refusals stay in Leash.cs), and a follow of the leash holder is never stopped by a claim. Short walks (walk_to, sit_lap,
//     doors...) are allowed: the collar's pull still wins. An RLV-forced action (@sit, @tpto) is never refused.
//   - A step a running mover takes itself (the wander-sit visit's own 'sit', a walk's own sit_near sit, come-here's follow after
//     arriving) is not a new mover: moverSelf (AsyncLocal) marks the mover's own task and its claims are no-ops.
//   - Accepting a lure keeps a follow of the lure sender (he is at the arrival: Follow.cs), stops every other mover.
// The background home wander (Wander.cs) keeps its own rules (WanderBlocksManualWalk, auto-follow) and is not a claim target.
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    internal enum MoverKind { Follow, Walk, WanderSit, Sit, Roam, ComeHere, Teleport }

    /// <summary>What a new mover does to the running ones. Refuse != null: do not start it.</summary>
    internal sealed record MoverPlan(string Refuse, bool StopFollow, bool StopRoam, bool StopWalk, bool StopWanderSit, bool CancelAutoPilot)
    {
        public static readonly MoverPlan Nothing = new(null, false, false, false, false, false);
        public bool StopsAnything => StopFollow || StopRoam || StopWalk || StopWanderSit;
    }

    /// <summary>Pure (tested): the one-mover decision.</summary>
    /// <param name="ownStep">the claim comes from inside a running mover's own task</param>
    /// <param name="forced">an RLV-forced action (never refused)</param>
    /// <param name="leashed">collar leash mode (not the collar's follow mode)</param>
    /// <param name="followTargetIsHolder">the NEW follow's target is the leash holder (only for Follow)</param>
    /// <param name="followOn">a follow is running</param>
    /// <param name="followIsHolder">the running follow's target is the leash holder</param>
    /// <param name="followIsKeepTarget">the running follow's target is the one to keep (a lure sender)</param>
    internal static MoverPlan MoverPlanFor(MoverKind next, bool ownStep, bool forced, bool leashed, bool followTargetIsHolder,
                                           bool followOn, bool followIsHolder, bool followIsKeepTarget, bool roamOn, bool walkOn, bool wanderSitOn)
    {
        if (ownStep) return MoverPlan.Nothing;
        if (leashed && !forced)
        {
            string r = next switch
            {
                MoverKind.WanderSit => "refused: I'm leashed, the leash leads (no wander sit)",
                MoverKind.Roam => "refused: leashed; not starting a roam",
                MoverKind.Follow when !followTargetIsHolder => "refused: I'm leashed; I only follow the one holding my leash",
                _ => null
            };
            if (r != null) return MoverPlan.Nothing with { Refuse = r };
        }
        bool keepFollow = (leashed && followIsHolder) || followIsKeepTarget || next == MoverKind.Follow;   // a new follow retargets the old one
        bool stopFollow = followOn && !keepFollow;
        bool stopRoam = roamOn && next != MoverKind.Roam;
        bool stopWanderSit = wanderSitOn && next != MoverKind.WanderSit;
        // the autopilot belongs to the follow when a follow is kept running
        bool cancelAp = !(followOn && !stopFollow);
        return new MoverPlan(null, stopFollow, stopRoam, walkOn, stopWanderSit, cancelAp);
    }

    /// <summary>Set inside a mover's own task: its own steps are not new movers.</summary>
    static readonly AsyncLocal<MoverKind?> moverSelf = new();

    /// <summary>Before a self-directed mover starts: stop the others (one mover at a time). Returns the refusal text, or null
    /// to go ahead. <paramref name="keepFollowOf"/>: a follow of this avatar keeps running (a lure sender).</summary>
    static string MoverClaim(MoverKind kind, string why, UUID followTarget = default, UUID keepFollowOf = default, bool forced = false)
    {
        try
        {
            var l = leash; bool leashed = l is { mode: "leash" };
            var fid = followId;
            bool walkOn = walkTask != null && !walkTask.IsCompleted, wSitOn = wSitTask != null && !wSitTask.IsCompleted;
            var plan = MoverPlanFor(kind, moverSelf.Value != null, forced || rlvForcing.Value, leashed,
                leashed && followTarget != UUID.Zero && followTarget == l.holder,
                fid != UUID.Zero, leashed && fid != UUID.Zero && fid == l.holder, keepFollowOf != UUID.Zero && fid == keepFollowOf,
                RoamOn, walkOn, wSitOn);
            if (plan.Refuse != null) { Log("mover", $"{kind} ({why}) {plan.Refuse}"); return plan.Refuse; }
            var stopped = new List<string>();
            if (plan.StopFollow) { var n = followName; AutoFollowOnFollowOff("new mover: " + why); followId = UUID.Zero; followDistThis = null; stopped.Add("follow of " + n); }
            if (plan.StopRoam) { StopRoamIfOn("new mover: " + why); stopped.Add("roam"); }
            if (plan.StopWanderSit) { try { wSitCts?.Cancel(); } catch { } stopped.Add("wander-sit visit"); }
            if (plan.StopWalk) { try { walkCts?.Cancel(); } catch { } stopped.Add("walk (" + walkState + ")"); }
            if (plan.CancelAutoPilot) try { client.Self.AutoPilotCancel(); } catch { }
            // let the stopped tasks finish their cleanup before the new mover takes the legs (bounded)
            if (plan.StopWalk) try { walkTask?.Wait(3000); } catch { }
            if (plan.StopWanderSit) try { wSitTask?.Wait(3000); } catch { }
            if (stopped.Count > 0) Log("mover", $"{kind} ({why}): one mover at a time, stopped {string.Join(", ", stopped)}");
            return null;
        }
        catch (Exception ex) { Log("mover", $"claim error ({kind}, {why}): {ex.GetBaseException().Message}"); return null; }
    }
}
