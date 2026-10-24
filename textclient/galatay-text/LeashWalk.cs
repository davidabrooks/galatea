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
    internal const float LeashMinGap = 0.7f;   // never closer than this to him
    // pure (tested): stop distance: at the leash, never bumping into him
    internal static float LeashWalkStop(float length) => Math.Max(0.9f, 0.6f * length);
    // pure (tested): run when he runs or I am far behind, unless RLV forbids running
    internal static bool LeashShouldRun(float holderSpeed, float dist, float length, bool runBlocked) =>
        !runBlocked && (holderSpeed > 3.2f || dist > 2f * length + 2f);
    // pure (tested): is he moving? speed, or position change between consecutive updates
    internal static bool LeashHolderMoving(float speed, float posDelta, double dtSec) =>
        speed > 0.3f || (dtSec > 0 && dtSec < 1.0 && posDelta / dtSec > 0.3);
    // pure (tested): his motion counts only when it takes him AWAY from me (my push or the pull toward him never counts)
    internal static bool LeashMovingAway(Vector3 holder, Vector3 hvel, Vector3 me)
    {
        var away = new Vector3(holder.X - me.X, holder.Y - me.Y, 0); var v = new Vector3(hvel.X, hvel.Y, 0);
        if (away.Length() < 0.01f) return false;
        return Vector3.Dot(v, Vector3.Normalize(away)) > 0.3f;
    }
    // pure (tested): natural reaction delay so he visibly starts first: 400-600 ms with jitter (total reaction stays < ~0.8 s)
    internal static int LeashReactionDelayMs(double r01) => 250 + (int)(150 * Math.Clamp(r01, 0, 1));   // David 11:26: sooner, 0.25-0.4 s
    // pure (tested): leashed idle pose: rise only when he walks away AND the gap passes the start gap (a real pull)
    internal const float LeashRiseSpeed = 0.5f;
    // pure (tested): seated/posed while leashed and he starts moving (walking speed, or already past the start gap) -> stand now
    internal static bool LeashMustStand(bool leashed, bool seatedOrPosed, bool hisMotion, float hisSpeed, float gap, float length)
        => leashed && seatedOrPosed && (hisSpeed > LeashRiseSpeed || (hisMotion && gap > LeashStartGap(length)));
    internal static bool LeashIdleMustRise(bool leashed, bool hisMotionAway, float gap, float length) => leashed && hisMotionAway && gap > LeashStartGap(length);
    // pure (tested): start gap: just over the collar-pull hold distance (stop + 0.1 m), at most the old "gap > leash"
    internal static float LeashStartGap(float length) => Math.Max(LeashWalkStop(length) + 0.1f, Math.Min(length, LeashWalkStop(length) + 0.3f));
    // pure (tested): may I start walking on his motion yet?
    internal static bool LeashReactionReady(DateTime? motionAt, int delayMs, DateTime now) => motionAt is DateTime m && (now - m).TotalMilliseconds >= delayMs;
    // pure (tested): grace after login/teleport/region change: ignore everything
    internal static bool LeashInGrace(DateTime arrivedAt, DateTime now) => (now - arrivedAt).TotalSeconds < 5;
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
    // ---- controller (pure, tested offline against a simulated collar pull: LeashWalkSimTests) ----
    // Rules (David 10:46): never walk while he stands; start only on HIS motion (after the 0.25-0.4 s delay) AND gap > LeashStartGap;
    // stop when he stops; forward controls only while gap >= max(1.0 m, stop + 0.5 m) (early deceleration, never forward under 1 m);
    // collision (my velocity toward him reverses, or a sudden speed drop near him) -> stop at once.
    internal const float LeashNoForwardUnder = 1.0f;
    internal static float LeashForwardCutoff(float length) => Math.Max(LeashNoForwardUnder, LeashWalkStop(length) + 0.5f);
    // speed-aware: plus my braking distance (assume a conservative 6 m/s^2 avatar deceleration)
    internal static float LeashForwardCutoff(float length, float vToward) => LeashForwardCutoff(length) + Math.Max(0, vToward) * Math.Max(0, vToward) / 12f;
    internal static bool LeashCollision(float gap, float vToward, float prevVToward, bool forwardWas) =>
        gap < 1.5f && ((prevVToward > 0.3f && vToward < -0.1f) || (forwardWas && prevVToward > 1.0f && vToward < 0.3f));
    internal readonly record struct LeashCtlOut(string act, bool forward, string why);
    internal static LeashCtlOut LeashControl(bool walking, bool hisMotion, bool hisStopped, float gap, float length, bool canMove,
                                             float vToward, float prevVToward, bool forwardWas)
    {
        if (!canMove) return new(walking ? "stop" : "", false, "can't move");
        if (!walking)
            return hisMotion && gap > LeashStartGap(length) ? new("walk", gap >= LeashForwardCutoff(length, vToward), "he walks away") : new("", false, "");
        if (LeashCollision(gap, vToward, prevVToward, forwardWas)) return new("stop", false, "collision");
        if (hisStopped) return new("stop", false, "he stopped");
        return new("", gap >= LeashForwardCutoff(length, vToward), "");
    }
    // pure (tested): the collar pull is moving me without my legs: show the walk. 2026-10-23 live test (back and forth after he
    // stopped): the nudge used to fire on ANY speed, facing my velocity, so an overshoot or bounce away from him got thrust in
    // that direction too, and the collar pulled back, and so on (positive feedback). Now: only while the pull is carrying me
    // TOWARD him, and only past the speed-aware forward cutoff (braking distance included), so it can never add overshoot.
    internal static bool PullNeedsWalkAnim(float vToward, bool sitting, bool driving, float gap, float length) =>
        !sitting && !driving && vToward > 0.2f && gap >= LeashForwardCutoff(length, vToward);
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
    static UUID holderSeen; static DateTime? holderMotionAt; static int holderDelayMs = 500;
    internal static DateTime leashArrivedAt = DateTime.Now; static Simulator leashArrivedSim;
    // kill switch: the file exists (or GT_LEASH_WALKER=off) -> no walker, no pull legs; checked live every tick
    internal static readonly string LeashWalkerOffFile = Env("GT_LEASH_WALKER_OFF", "/home/box/viewers/textclient/run/leash-walker-off");
    static bool LeashWalkerKilled() => Env("GT_LEASH_WALKER", "on") == "off" || System.IO.File.Exists(LeashWalkerOffFile);

    static void LeashWalkStopNow(Vector3? face)
    {
        var m = client.Self.Movement;
        m.AtPos = false; m.NudgeAtPos = false; m.AlwaysRun = false;
        if (face is Vector3 f) try { m.TurnToward(f); } catch { }
        m.SendUpdate(true);
    }
    static bool LeashCanMove(LeashState l) =>
        l != null && !l.anchor && l.holder != UUID.Zero && client.Self.SittingOn == 0 && rlvHoldSeat == null && leashForgetBusy == 0 && followId != l.holder;
    static bool leashFwdWas; static float leashPrevVToward;
    static void LeashDrive(bool forward)
    {
        var m = client.Self.Movement; var me = client.Self.SimPosition;
        float stop = LeashWalkStop(leashLength), dist = Vector3.Distance(me, holderPos);
        var tgt = LeashFollowTarget(holderPos, holderVel, me, stop);
        var to = new Vector3(tgt.X - me.X, tgt.Y - me.Y, 0);
        bool run = forward && LeashShouldRun(new Vector3(holderVel.X, holderVel.Y, 0).Length(), dist, leashLength, rlv.Has("alwaysrun") || rlv.Has("temprun"));
        try { m.TurnToward(to.Length() > 0.05f ? me + to : new Vector3(holderPos.X, holderPos.Y, me.Z)); } catch { }
        m.AlwaysRun = run; m.AtPos = forward; m.NudgeAtPos = false; m.SendUpdate(true);
    }
    static void LeashWalkStep(string why)
    {
        lock (leashWalkLock)
        {
            var l = leash;
            bool can = LeashCanMove(l) && holderSeen == l?.holder && !LeashWalkerKilled() && !LeashInGrace(leashArrivedAt, DateTime.Now) && idlePosed == null;
            var me = client.Self.SimPosition;
            float dist = Vector3.Distance(me, holderPos);
            var toHim = new Vector3(holderPos.X - me.X, holderPos.Y - me.Y, 0);
            var v = client.Self.Velocity;
            float vToward = toHim.Length() > 0.01f ? Vector3.Dot(new Vector3(v.X, v.Y, 0), Vector3.Normalize(toHim)) : 0;
            bool moveCounts = holderMoving && (leashDriving || LeashReactionReady(holderMotionAt, holderDelayMs, DateTime.Now));
            // David 11:31: get up only when he is actually about to pull me (walking away past the start gap), not when he moves nearby
            // 2026-10-23 16:57 (David: "always stand when I start walking and you're leashed"): seated on the ground (idle pose or
            // not) and he moves away -> stand at once (no stand cooldown: internal) and follow
            bool groundSat = AoForceSitOn(SelfAnims(), client.Self.SittingOn != 0);
            if (LeashMustStand(l != null, idlePosed != null || groundSat, holderMoving, new Vector3(holderVel.X, holderVel.Y, 0).Length(), dist, leashLength))
            {
                if (idlePosed != null) _ = IdlePoseExitAsync("leash: he walks away");
                else { try { client.Self.Stand(); } catch { } RlvLog("leash-walk: stood up from the ground: he walks away"); }
            }
            var o = LeashControl(leashDriving, moveCounts, LeashHolderStopped(holderSlowSince, DateTime.Now), dist, leashLength, can, vToward, leashPrevVToward, leashFwdWas);
            leashPrevVToward = vToward;
            if (o.act == "walk")
            {
                leashDriving = true; leashPullAnim = false; LeashDrive(o.forward); leashFwdWas = o.forward; leashLat.Commanded(DateTime.Now);
                RlvLog($"leash-walk: start ({why}): {l.holderName} moving {new Vector3(holderVel.X, holderVel.Y, 0).Length():F2} m/s at {dist:F1} m");
            }
            else if (o.act == "stop") { IdleNoteActivity(); idleThreshold = IdleThresholdSec(IdleBaseSec(), Random.Shared.NextDouble()); leashDriving = false; leashFwdWas = false; LeashWalkStopNow(holderPos); RlvLog($"leash-walk: stopped ({o.why}) at {dist:F1} m facing {l?.holderName}"); }
            else if (leashDriving) { if (o.forward != leashFwdWas || o.forward) LeashDrive(o.forward); leashFwdWas = o.forward; }
        }
    }
    static void LeashWalkHooks()
    {
        client.Network.SimChanged += (s, e) => leashArrivedAt = DateTime.Now;   // login / teleport / crossing: grace
        client.Network.LoginProgress += (s, e) => leashArrivedAt = DateTime.Now;
        void Holder(Simulator sim, Primitive p, Vector3 pos, Vector3 vel)
        {
            var l = leash; if (l == null || p == null || p.ID != l.holder || sim != client.Network.CurrentSim) return;
            var now = DateTime.Now;
            if (p.ParentID != 0) { holderSeen = UUID.Zero; return; }
            float delta = holderSeen == l.holder ? Vector3.Distance(pos, holderPos) : 0f;
            double dt = holderSeen == l.holder ? (now - holderAt).TotalSeconds : 0;
            bool was = holderMoving;
            var me = client.Self.SimPosition;
            holderMoving = LeashHolderMoving(new Vector3(vel.X, vel.Y, 0).Length(), delta, dt) && LeashMovingAway(pos, vel, me)
                           && Vector3.Distance(pos, me) > LeashWalkStop(leashLength) && !LeashInGrace(leashArrivedAt, now);
            holderPos = pos; holderVel = vel; holderAt = now; holderSeen = l.holder;
            if (holderMoving) { holderSlowSince = null; if (!was && !leashDriving)
                {
                    leashLat.HolderStopped(); leashLat.HolderMoved(now);
                    holderMotionAt = now; holderDelayMs = LeashReactionDelayMs(Random.Shared.NextDouble());
                    var d = holderDelayMs;
                    _ = Task.Run(async () => { await Task.Delay(d); try { LeashWalkStep($"he moved, after {d} ms"); } catch { } if (!leashDriving) leashLat.HolderStopped(); });
                } }
            else { holderSlowSince ??= now; holderMotionAt = null; if (was) leashLat.HolderStopped(); if (leashDriving) LeashWalkStep("he slowed"); }
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
                if (leashLat.IMoved(DateTime.Now) is { } r && r.moveMs < 5000) RlvLog($"leash-diag: reaction latency {r.moveMs} ms (his motion -> my movement; controls sent after {r.cmdMs} ms){(r.moveMs > 500 ? " SLOW" : "")}");
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
        var simNow = client.Network.CurrentSim;
        if (simNow != leashArrivedSim) { leashArrivedSim = simNow; leashArrivedAt = DateTime.Now; }   // login / teleport / crossing: grace
        if (LeashWalkerKilled() && (leashDriving || leashPullAnim)) lock (leashWalkLock) { leashDriving = leashPullAnim = false; LeashWalkStopNow(null); RlvLog("leash-walk: walker OFF (kill switch)"); }
        if (l == null || l.anchor || l.holder == UUID.Zero || LeashWalkerKilled())
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
        LeashWalkStep("tick");   // never starts on its own: only his motion starts the walker
        if (leashDriving) return;
        // the collar is dragging me: put the legs under it
        var v0 = client.Self.Velocity; var vel = new Vector3(v0.X, v0.Y, 0); var me0 = client.Self.SimPosition;
        var toHim = new Vector3(holderPos.X - me0.X, holderPos.Y - me0.Y, 0);
        float vTow = holderSeen == l.holder && toHim.Length() > 0.01f ? Vector3.Dot(vel, Vector3.Normalize(toHim)) : 0f;
        bool need = holderSeen == l.holder && PullNeedsWalkAnim(vTow, sitting, false, toHim.Length(), leashLength) && leashForgetBusy == 0 && !LeashInGrace(leashArrivedAt, DateTime.Now);
        if (need) { try { m.TurnToward(new Vector3(holderPos.X, holderPos.Y, me0.Z)); } catch { } m.NudgeAtPos = true; m.SendUpdate(true); if (!leashPullAnim) RlvLog($"leash-walk: pull legs on at {toHim.Length():F1} m ({vTow:F2} m/s toward {l.holderName})"); leashPullAnim = true; }
        else if (leashPullAnim) { leashPullAnim = false; m.NudgeAtPos = false; m.SendUpdate(true); RlvLog("leash-walk: pull legs off"); }
    }
}
