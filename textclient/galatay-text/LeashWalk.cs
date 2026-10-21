// LeashWalk.cs: while leashed (or on the collar's follow), walk WITH the holder the moment he moves.
// Motion-triggered: his terse/full object updates are handled as they arrive (no polling). The instant his speed
// passes 0.3 m/s (or his position jumps between updates), I start walking (agent controls: real walk/run + AO),
// steer to a point at the stop distance behind him, led by his velocity, and match walk/run to his speed.
// When he has been under 0.1 m/s for 0.3 s I stop at the stop distance facing him. A 10 Hz loop steers and keeps
// the distance fallback (he is past 0.8x the leash) and the walk control during the collar's pull (no gliding).
// Reaction latency (his motion start -> my first movement) is logged on leash-diag lines.
using System;
using System.Linq;
using System.Threading.Tasks;
using LibreMetaverse;
namespace GalatayText;
partial class Program
{
    // pure (tested): start walking past this distance (fallback when he is standing still)
    internal static float LeashWalkStart(float length) => Math.Max(0.8f * length, LeashWalkStop(length) + 0.2f);
    // pure (tested): stop distance: at the leash, never bumping into him
    internal static float LeashWalkStop(float length) => Math.Max(0.6f, 0.6f * length);
    // pure (tested): run when he runs or I am far behind, unless RLV forbids running
    internal static bool LeashShouldRun(float holderSpeed, float dist, float length, bool runBlocked) =>
        !runBlocked && (holderSpeed > 3.2f || dist > 2f * length + 2f);
    // pure (tested): is he moving? speed, or position change between consecutive updates
    internal static bool LeashHolderMoving(float speed, float posDelta, double dtSec) =>
        speed > 0.3f || (dtSec > 0 && dtSec < 1.0 && posDelta / dtSec > 0.3);
    // pure (tested): stopped = under 0.1 m/s for 0.3 s
    internal static bool LeashHolderStopped(DateTime? slowSince, DateTime now) => slowSince is DateTime s && (now - s).TotalSeconds >= 0.3;
    // pure (tested): where to head: the stop distance behind him along his heading, led 0.5 s ahead of him
    internal static Vector3 LeashFollowTarget(Vector3 holder, Vector3 hvel, Vector3 me, float stopDist)
    {
        var v = new Vector3(hvel.X, hvel.Y, 0);
        var dir = v.Length() > 0.05f ? Vector3.Normalize(v) : (me - holder) is var d && d.Length() > 0.01f ? -Vector3.Normalize(new Vector3(d.X, d.Y, 0)) : new Vector3(1, 0, 0);
        var t = holder + v * 0.5f - dir * stopDist;
        return new Vector3(t.X, t.Y, me.Z);
    }
    // pure (tested): what to do this tick: "walk", "stop" or "" (keep)
    internal static string LeashWalkDecide(bool walking, bool holderMoving, bool holderStopped, float dist, float length, bool canMove) =>
        !canMove ? (walking ? "stop" : "") :
        !walking && (holderMoving || dist > LeashWalkStart(length)) ? "walk" :
        walking && holderStopped && dist <= LeashWalkStop(length) ? "stop" : "";
    // pure (tested): the collar pull is moving me without my legs: show the walk
    internal static bool PullNeedsWalkAnim(float speed, bool sitting, bool driving) => !sitting && !driving && speed > 0.2f;
    // pure (tested): reaction latency tracker (his motion start -> my first movement)
    internal sealed class LeashLatency
    {
        public DateTime? MotionAt, CommandAt;
        public void HolderMoved(DateTime t) { MotionAt ??= t; }
        public void Commanded(DateTime t) { if (MotionAt != null) CommandAt ??= t; }
        public void HolderStopped() { MotionAt = CommandAt = null; }
        // first movement of mine after his motion started: returns (command ms, movement ms) once
        public (int cmdMs, int moveMs)? IMoved(DateTime t)
        {
            if (MotionAt is not DateTime m || CommandAt is not DateTime c) return null;
            var r = ((int)(c - m).TotalMilliseconds, (int)(t - m).TotalMilliseconds); MotionAt = CommandAt = null; return r;
        }
    }

    static volatile bool leashDriving; static bool leashPullAnim;
    static readonly object leashWalkLock = new();
    static readonly LeashLatency leashLat = new();
    static Vector3 holderPos, holderVel; static DateTime holderAt; static bool holderMoving; static DateTime? holderSlowSince;
    static UUID holderSeen;

    static void LeashWalkStopNow(Vector3? face)
    {
        var m = client.Self.Movement;
        m.AtPos = false; m.NudgeAtPos = false; m.AlwaysRun = false;
        if (face is Vector3 f) try { m.TurnToward(f); } catch { }
        m.SendUpdate(true);
    }
    static bool LeashCanMove(LeashState l) =>
        l != null && !l.anchor && l.holder != UUID.Zero && client.Self.SittingOn == 0 && rlvHoldSeat == null && leashForgetBusy == 0 && followId != l.holder;
    // steer toward the follow target right now and send the controls immediately
    static void LeashDrive(LeashState l)
    {
        var m = client.Self.Movement; var me = client.Self.SimPosition;
        float stop = LeashWalkStop(leashLength), dist = Vector3.Distance(me, holderPos);
        var tgt = LeashFollowTarget(holderPos, holderVel, me, stop);
        var to = new Vector3(tgt.X - me.X, tgt.Y - me.Y, 0);
        bool run = LeashShouldRun(new Vector3(holderVel.X, holderVel.Y, 0).Length(), dist, leashLength, rlv.Has("alwaysrun") || rlv.Has("temprun"));
        bool ahead = holderMoving && to.Length() < 0.25f;   // on the spot: let him lead a step
        try { m.TurnToward(holderMoving ? me + (to.Length() > 0.05f ? to : new Vector3(holderVel.X, holderVel.Y, 0)) : new Vector3(holderPos.X, holderPos.Y, me.Z)); } catch { }
        m.AlwaysRun = run; m.AtPos = !ahead; m.NudgeAtPos = false; m.SendUpdate(true);
    }
    static void LeashWalkStep(string why)
    {
        lock (leashWalkLock)
        {
            var l = leash;
            bool can = LeashCanMove(l) && holderSeen == l?.holder;
            float dist = Vector3.Distance(client.Self.SimPosition, holderPos);
            var act = LeashWalkDecide(leashDriving, holderMoving, LeashHolderStopped(holderSlowSince, DateTime.Now), can ? dist : 0, leashLength, can);
            if (act == "walk")
            {
                leashDriving = true; leashPullAnim = false; LeashDrive(l); leashLat.Commanded(DateTime.Now);
                RlvLog($"leash-walk: start ({why}): {l.holderName} {(holderMoving ? $"moving {new Vector3(holderVel.X, holderVel.Y, 0).Length():F2} m/s" : "standing")} at {dist:F1} m");
            }
            else if (act == "stop") { leashDriving = false; LeashWalkStopNow(holderPos); RlvLog($"leash-walk: stopped at {dist:F1} m facing {l?.holderName}"); }
            else if (leashDriving) LeashDrive(l);
        }
    }
    static void LeashWalkHooks()
    {
        void Holder(Simulator sim, Primitive p, Vector3 pos, Vector3 vel)
        {
            var l = leash; if (l == null || p == null || p.ID != l.holder || sim != client.Network.CurrentSim) return;
            var now = DateTime.Now;
            if (p.ParentID != 0) { holderSeen = UUID.Zero; return; }
            float delta = holderSeen == l.holder ? Vector3.Distance(pos, holderPos) : 0f;
            double dt = holderSeen == l.holder ? (now - holderAt).TotalSeconds : 0;
            bool was = holderMoving;
            holderMoving = LeashHolderMoving(new Vector3(vel.X, vel.Y, 0).Length(), delta, dt);
            holderPos = pos; holderVel = vel; holderAt = now; holderSeen = l.holder;
            if (holderMoving) { holderSlowSince = null; if (!was) { leashLat.HolderMoved(now); LeashWalkStep("he moved"); } }
            else { holderSlowSince ??= now; if (was) leashLat.HolderStopped(); if (leashDriving) LeashWalkStep("he slowed"); }
        }
        client.Objects.TerseObjectUpdate += (s, e) => { try { if (e.Update.Avatar) Holder(e.Simulator, e.Prim, e.Update.Position, e.Update.Velocity); } catch { } };
        client.Objects.AvatarUpdate += (s, e) => { try { if (e.Avatar != null) Holder(e.Simulator, e.Avatar, e.Avatar.Position, e.Avatar.Velocity); } catch { } };
        // my own first movement after his motion start: reaction latency
        client.Objects.TerseObjectUpdate += (s, e) =>
        {
            try
            {
                if (!e.Update.Avatar || e.Prim?.ID != client.Self.AgentID) return;
                var v = e.Update.Velocity; if (new Vector3(v.X, v.Y, 0).Length() < 0.2f) return;
                if (leashLat.IMoved(DateTime.Now) is { } r) RlvLog($"leash-diag: reaction latency {r.moveMs} ms (his motion -> my movement; controls sent after {r.cmdMs} ms){(r.moveMs > 500 ? " SLOW" : "")}");
            }
            catch { }
        };
    }
    static void LeashWalkLoopRun()
    {
        LeashWalkHooks();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(100);
                try { if (LoggedIn) LeashWalkTick(); } catch { }
            }
        });
    }
    static void LeashWalkTick()
    {
        var l = leash; var m = client.Self.Movement;
        bool sitting = client.Self.SittingOn != 0;
        if (l == null || l.anchor || l.holder == UUID.Zero)
        {
            if (leashDriving || leashPullAnim) lock (leashWalkLock) { leashDriving = leashPullAnim = false; LeashWalkStopNow(null); }
            holderSeen = UUID.Zero; return;
        }
        if (holderSeen != l.holder)   // no update yet for him: seed from the object list
        {
            var h = Avatars().FirstOrDefault(a => a.av != null && a.av.ID == l.holder);
            if (h.av == null || h.av.ParentID != 0) { if (leashDriving) LeashWalkStep("lost him"); return; }
            holderPos = h.pos; holderVel = h.av.Velocity; holderAt = DateTime.Now; holderSeen = l.holder; holderMoving = false; holderSlowSince = DateTime.Now;
        }
        if ((DateTime.Now - holderAt).TotalSeconds > 0.5 && holderMoving) { holderMoving = false; holderSlowSince ??= DateTime.Now; }   // updates stop when he stops
        LeashWalkStep("tick");
        if (leashDriving) return;
        // the collar is dragging me: put the legs under it
        var v0 = client.Self.Velocity; var vel = new Vector3(v0.X, v0.Y, 0);
        bool need = PullNeedsWalkAnim(vel.Length(), sitting, false) && leashForgetBusy == 0;
        if (need) { try { m.TurnToward(client.Self.SimPosition + vel); } catch { } m.NudgeAtPos = true; m.SendUpdate(true); leashPullAnim = true; }
        else if (leashPullAnim) { leashPullAnim = false; m.NudgeAtPos = false; m.SendUpdate(true); }
    }
}
