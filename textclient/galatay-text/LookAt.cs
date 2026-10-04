// LookAt.cs (2026-10-04, David): what Galatea's head-turn tells others.
// SL viewers send ViewerEffect LookAt (where the camera is focused, what is selected, idle gaze) and PointAt / beam effects
// (the selection beam). Others' viewers turn her head toward the target, and anyone with a "show look at" option sees a
// crosshair labelled with her name on it, so camera focus and zoom leak. Modes ('lookat mode', persisted in run/):
//   head (default): never camera-focus / select / mouselook types, never PointAt or beams. Only when she deliberately looks
//     at an avatar (`look at <avatar>`, a wander greeting, `say` while someone nearby is talking with her) one short
//     Respond LookAt (4 s) with target = that avatar's id and a zero offset, so others see her head turn to his/her head
//     whatever her (non-existent) camera does. A position target (`look at <object>`) is pulled to within 1.5 m of her
//     head first, like Firestorm's "Limit distance from head" (FSLookAtTargetMaxDistance); with nothing to look at nothing
//     is sent and viewers show their own idle gaze.
//   private: send no LookAt at all (like Firestorm's private look-at with local display off).
// LibreMetaverse itself only sends effects when asked (Self.LookAtEffect/PointAtEffect/BeamEffect; GiveItem's beam, which
// this client never calls). An audit counts outgoing ViewerEffect packets ([effects] log) and flags any not sent
// through here as UNEXPECTED. 'lookat audit on|off' also logs them to the console (test).
// Reimplemented from the documented behaviour of LL/Firestorm's LLHUDEffectLookAt; no viewer code copied.
using LibreMetaverse;
using LibreMetaverse.Packets;

namespace GalatayText;

public static partial class Program
{
    static readonly string LookAtModeFile = Env("GT_LOOKAT_MODE_FILE", "/home/box/viewers/textclient/run/lookat-mode");
    static string lookAtMode = LoadLookAtMode();
    static readonly UUID LookAtEffectId = UUID.Random();   // one effect id per session, as viewers reuse their lookat effect
    static int effectsAuditHooked, ourEffectSends; static bool effectsAuditVerbose;
    static readonly Dictionary<UUID, DateTime> lastHeadTurn = new();
    static (UUID id, DateTime at) chatPartner;
    public const float LookAtMaxFromHead = 1.5f, HeadTurnMaxRange = 20f;

    static string LoadLookAtMode()
    {
        try { var m = File.ReadAllText(LookAtModeFile).Trim(); if (m is "head" or "private") return m; } catch { }
        return Env("GT_LOOKAT_MODE", "head") == "private" ? "private" : "head";
    }

    // pure (selftest): a position target pulled to within max of her head (Firestorm's limiter, reimplemented)
    public static Vector3 ClampToHead(Vector3 head, Vector3 target, float max)
    {
        var d = target - head; var len = d.Length();
        return len <= max || len < 1e-4f ? target : head + d * (max / len);
    }

    // LibreMetaverse never raises Network.PacketSent (UDPBase doesn't call it), so the audit counts outgoing ViewerEffect
    // packets in its per-type send stats once a second and compares with the sends made here (each logged at the call).
    // ponytail: a count, not the packet contents; an unexpected send shows up as UNEXPECTED with no type/target details.
    static void EffectsAuditStart()
    {
        if (Interlocked.Exchange(ref effectsAuditHooked, 1) != 0) return;
        client.Settings.Packets.TrackUtilization = true;
        _ = Task.Run(async () =>
        {
            long seen = 0;
            while (true)
            {
                await Task.Delay(1000);
                try
                {
                    long tx = client.Stats.GetStatistics().TryGetValue("ViewerEffect", out var st) ? st.TxCount : 0;
                    if (tx < seen) seen = 0;   // stats reset at relog
                    long n = tx - seen; seen = tx; if (n <= 0) continue;
                    long ours = Math.Min(n, Interlocked.Exchange(ref ourEffectSends, 0)), other = n - ours;
                    var line = $"ViewerEffect packets sent in the last second: {n} ({ours} from LookAt.cs{(other > 0 ? $", {other} UNEXPECTED (not from LookAt.cs)" : "")})";
                    Log("effects", line); if (effectsAuditVerbose) Console.WriteLine("[effects] " + line);
                }
                catch (Exception ex) { Log("effects", "audit error: " + ex.Message); }
            }
        });
    }

    static void NoteChatPartner(UUID id) => chatPartner = (id, DateTime.Now);

    // she deliberately looks at an avatar: one short Respond LookAt aimed at its head (target id, zero offset)
    static string HeadTurnTo(UUID avatar, string why)
    {
        if (lookAtMode == "private") { Log("lookat", $"{why}: private mode, nothing sent"); return "private: nothing sent"; }
        if (!LoggedIn || avatar == UUID.Zero || avatar == client.Self.AgentID) return "no target";
        var av = Sim?.ObjectsAvatars.Values.FirstOrDefault(x => x?.ID == avatar);
        if (av == null) return "not in view: nothing sent";
        var dist = Vector3.Distance(PositionHelper.GetAvatarPosition(Sim, av), client.Self.SimPosition);
        if (dist > HeadTurnMaxRange) return $"{dist:F0} m away (> {HeadTurnMaxRange:F0} m): nothing sent";
        lock (lastHeadTurn) { if (lastHeadTurn.TryGetValue(avatar, out var t) && (DateTime.Now - t).TotalSeconds < 2) return "just sent"; lastHeadTurn[avatar] = DateTime.Now; }
        Interlocked.Increment(ref ourEffectSends);
        client.Self.LookAtEffect(client.Self.AgentID, avatar, Vector3d.Zero, LookAtType.Respond, LookAtEffectId);
        Log("lookat", $"{why}: head turn to {av.Name} ({avatar}), Respond 4 s");
        return $"head turn to {av.Name} (Respond, 4 s)";
    }

    // a position target (an object): clamped to 1.5 m from her head, FreeLook (2 s), no object id
    static string HeadTurnToPoint(Vector3 regionPos, string why)
    {
        if (lookAtMode == "private") { Log("lookat", $"{why}: private mode, nothing sent"); return "private: nothing sent"; }
        if (!LoggedIn) return "not logged in";
        var head = client.Self.SimPosition + new Vector3(0, 0, 0.7f);
        var p = ClampToHead(head, regionPos, LookAtMaxFromHead);
        Interlocked.Increment(ref ourEffectSends);
        var me = client.Self.SimPosition; var g = client.Self.GlobalPosition;   // region -> global: her global position + the offset
        client.Self.LookAtEffect(client.Self.AgentID, UUID.Zero, new Vector3d(g.X + p.X - me.X, g.Y + p.Y - me.Y, g.Z + p.Z - me.Z), LookAtType.FreeLook, LookAtEffectId);
        Log("lookat", $"{why}: head turn toward {Fmt(regionPos)} (sent clamped {Fmt(p)}), FreeLook 2 s");
        return $"head turn toward the point (clamped to {LookAtMaxFromHead} m from my head)";
    }

    // `say` while someone nearby has been talking with her in the last minute: look at them
    static void HeadTurnForSay()
    {
        var (id, at) = chatPartner;
        if (id != UUID.Zero && (DateTime.Now - at).TotalSeconds <= 60) HeadTurnTo(id, "say");
    }

    static string LookAtCmd(string[] a)
    {
        var sub = a.Length > 0 ? a[0].ToLowerInvariant() : "status";
        if (sub == "mode" && a.Length > 1 && a[1] is "head" or "private")
        {
            lookAtMode = a[1]; try { File.WriteAllText(LookAtModeFile, lookAtMode + "\n"); } catch { }
            Log("lookat", "mode " + lookAtMode); return "lookat mode " + lookAtMode;
        }
        if (sub == "audit" && a.Length > 1) { effectsAuditVerbose = a[1] == "on"; return "effects audit console " + (effectsAuditVerbose ? "on" : "off"); }
        if (sub == "selftest")
        {
            var h = new Vector3(100, 100, 30); int pass = 0, fail = 0; var sb = new System.Text.StringBuilder();
            void C(bool ok, string w) { if (ok) pass++; else fail++; sb.AppendLine($"{(ok ? "PASS" : "FAIL")} {w}"); }
            C(Vector3.Distance(ClampToHead(h, h + new Vector3(30, 40, 0), 1.5f), h) is > 1.49f and < 1.51f, "a target 50 m away is pulled to 1.5 m from the head");
            C(ClampToHead(h, h + new Vector3(1, 0, 0), 1.5f) == h + new Vector3(1, 0, 0), "a target 1 m away is left alone");
            var c = ClampToHead(h, h + new Vector3(0, 30, 0), 1.5f); C(c.X == 100 && c.Y > 101.4f, "direction is kept");
            return $"lookat selftest: {pass} PASS, {fail} FAIL (pure; nothing sent)\n" + sb.ToString().TrimEnd();
        }
        return $"lookat mode {lookAtMode} (head = short Respond head turns at avatars she deliberately looks at; private = none); " +
               $"never sent: Focus/Select/Mouselook/Idle LookAt, PointAt, beams; audit log [effects]{(effectsAuditVerbose ? ", console on" : "")}";
    }
}
