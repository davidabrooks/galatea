// LeashWalk.cs: while leashed (or on the collar's follow), keep up with the holder on my own legs.
// The collar's pull (llMoveToTarget) drags me with no walk animation and kicks in late; this 4 Hz loop
// walks me (agent controls: real walk/run + AO) as soon as he is past ~0.8x the leash, stops at the leash facing him,
// and sets the walk control whenever the pull is moving me so the walk animation always shows.
using System;
using System.Linq;
using System.Threading.Tasks;
using LibreMetaverse;
namespace GalatayText;
partial class Program
{
    // pure (tested): start walking past this distance (0.8x leash, never inside the stop distance)
    internal static float LeashWalkStart(float length) => Math.Max(0.8f * length, LeashWalkStop(length) + 0.2f);
    // pure (tested): stop distance: at the leash, never bumping into him
    internal static float LeashWalkStop(float length) => Math.Max(0.6f, 0.6f * length);
    // pure (tested): run when he runs or I am far behind, unless RLV forbids running
    internal static bool LeashShouldRun(float holderSpeed, float dist, float length, bool runBlocked) =>
        !runBlocked && (holderSpeed > 3.2f || dist > 2f * length + 2f);
    // pure (tested): what to do this tick: "walk", "stop" or "" (keep)
    internal static string LeashWalkDecide(bool walking, float dist, float length, bool canMove) =>
        !canMove ? (walking ? "stop" : "") :
        !walking && dist > LeashWalkStart(length) ? "walk" :
        walking && dist <= LeashWalkStop(length) ? "stop" : "";
    // pure (tested): the collar pull is moving me without my legs: show the walk
    internal static bool PullNeedsWalkAnim(float speed, bool sitting, bool driving) => !sitting && !driving && speed > 0.2f;

    static volatile bool leashDriving; static bool leashPullAnim;
    static void LeashWalkStopNow(Vector3? face)
    {
        var m = client.Self.Movement;
        m.AtPos = false; m.AlwaysRun = false;
        if (face is Vector3 f) try { m.TurnToward(f); } catch { }
        m.SendUpdate(true);
    }
    static void LeashWalkLoopRun() => _ = Task.Run(async () =>
    {
        while (true)
        {
            await Task.Delay(250);
            try { if (LoggedIn) LeashWalkTick(); } catch { }
        }
    });
    static void LeashWalkTick()
    {
        var l = leash; var m = client.Self.Movement;
        bool sitting = client.Self.SittingOn != 0;
        if (l == null || l.anchor || l.holder == UUID.Zero)
        {
            if (leashDriving || leashPullAnim) { leashDriving = leashPullAnim = false; LeashWalkStopNow(null); }
            return;
        }
        var h = Avatars().FirstOrDefault(a => a.av != null && a.av.ID == l.holder);
        var me = client.Self.SimPosition;
        bool canMove = h.av != null && h.av.ParentID == 0 && !sitting && rlvHoldSeat == null && leashForgetBusy == 0 && followId != l.holder;
        var act = LeashWalkDecide(leashDriving, h.av != null ? h.dist : 0, leashLength, canMove);
        if (act == "walk") { leashDriving = true; RlvLog($"leash-walk: {l.holderName} at {h.dist:F1} m (> {LeashWalkStart(leashLength):F1}): walking after him"); }
        else if (act == "stop") { leashDriving = false; LeashWalkStopNow(h.av != null ? h.pos : null); RlvLog($"leash-walk: stopped at {(h.av != null ? h.dist : 0):F1} m facing {l.holderName}"); }
        if (leashDriving)
        {
            bool run = LeashShouldRun(h.av.Velocity.Length(), h.dist, leashLength, rlv.Has("alwaysrun") || rlv.Has("temprun"));
            try { m.TurnToward(new Vector3(h.pos.X, h.pos.Y, me.Z)); } catch { }
            m.AlwaysRun = run; m.AtPos = true; m.SendUpdate(true);
            leashPullAnim = false; return;
        }
        // the collar is dragging me: put the legs under it
        var v0 = client.Self.Velocity; var vel = new Vector3(v0.X, v0.Y, 0);
        bool need = PullNeedsWalkAnim(vel.Length(), sitting, false) && leashForgetBusy == 0;
        if (need) { try { m.TurnToward(me + vel); } catch { } m.NudgeAtPos = true; m.SendUpdate(true); leashPullAnim = true; }
        else if (leashPullAnim) { leashPullAnim = false; m.NudgeAtPos = false; m.SendUpdate(true); }
    }
}
