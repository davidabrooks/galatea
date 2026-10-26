// LeashPull.cs (2026-10-21, David): the leash can be INVISIBLE (no particle chain), so the collar's pull
// (llMoveToTarget on me: I move although I send no movement) is a primary signal; the particle target is secondary.
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    // pure (tested): the avatar I'm being pulled toward (velocity points at it), or null
    internal static UUID? LeashPullToward(Vector3 me, Vector3 vel, IEnumerable<(UUID id, Vector3 pos)> avs, bool ownMove)
    {
        if (ownMove) return null;
        var v = new Vector3(vel.X, vel.Y, 0); float sp = v.Length(); if (sp < 0.4f) return null;
        (UUID id, float cos)? best = null;
        foreach (var a in avs)
        {
            var d = new Vector3(a.pos.X - me.X, a.pos.Y - me.Y, 0); float dl = d.Length(); if (dl < 0.5f || dl > 40f) continue;
            float cos = Vector3.Dot(v, d) / (sp * dl);
            if (cos > 0.85f && (best == null || cos > best.Value.cos)) best = (a.id, cos);
        }
        return best?.id;
    }

    static int pullTicks; static UUID pullId; internal static DateTime lastPullAt = DateTime.MinValue;
    // pure (tested): a lost particle chain ends the leash only when the collar has not pulled me lately (invisible leash)
    internal static bool LeashChainLossEnds(DateTime now, DateTime lastPull) => (now - lastPull).TotalSeconds > 30;
    static void LeashPullLoopRun() => _ = Task.Run(async () =>
    {
        while (true)
        {
            await Task.Delay(1000);
            try
            {
                if (!LoggedIn || client.Self.SittingOn != 0) { pullTicks = 0; continue; }
                var ctl = client.Self.Movement.AgentControls;
                bool own = (ctl & (uint) (AgentManager.ControlFlags.AGENT_CONTROL_AT_POS | AgentManager.ControlFlags.AGENT_CONTROL_AT_NEG | AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_AT_POS
                    | AgentManager.ControlFlags.AGENT_CONTROL_LEFT_POS | AgentManager.ControlFlags.AGENT_CONTROL_LEFT_NEG | AgentManager.ControlFlags.AGENT_CONTROL_UP_POS)) != 0 || followId != UUID.Zero;
                var avs = Avatars().Where(a => a.av != null).Select(a => (a.av.ID, a.av.Position)).ToList();
                var t = LeashPullToward(client.Self.SimPosition, client.Self.Velocity, avs, own);
                if (t is UUID id)
                {
                    pullTicks = id == pullId ? pullTicks + 1 : 1; pullId = id; lastPullAt = DateTime.Now;
                    if (pullTicks == 1) RlvLog($"leash-diag: PULL toward {id} (speed {client.Self.Velocity.Length():F1} m/s, no own movement)");
                    if (pullTicks == 3 && (leash == null || leash.mode != "leash") && LeashPullStartAllowed(DateTime.Now, leashNoPullStartUntil))
                    {
                        var av = Avatars().FirstOrDefault(a => a.av?.ID == id);
                        LeashStart("leash", id, av.av?.Name ?? id.ToString(), false, "collar pull (invisible leash?)");
                    }
                }
                else pullTicks = 0;
            }
            catch { }
        }
    });
}
