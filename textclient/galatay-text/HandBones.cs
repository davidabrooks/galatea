using LibreMetaverse;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace GalatayText;

// 2026-10-13 David: detect flat / splayed hands without a render. Every playing animation is an asset; the client
// downloads it (ViewerAsset cap, animatn_id, cached on disk), parses the binary BVH joint list and checks which
// animations key the Bento finger bones (mHand*) and at what priority.
//   - no playing animation keys any finger bone  -> fingers are undriven (the default flat/splayed Bento hand): set the
//     relaxed hand pose from the body HUD (once per sit)
//   - a seat animation keys the fingers above every other finger animation -> the pose itself drives the hands; a HUD
//     hand pose cannot override it (reported only)
//   - any non-built-in animation could not be read -> unknown, nothing done
// 'hands check [avatar]' runs the same check for me or another avatar (e.g. David) from the animations the sim reports.
public static partial class Program
{
    internal sealed record AnimFingers(UUID id, bool known, bool fingers, int prio, bool fromSeat);

    // pure: finger keys + highest finger priority of a parsed animation (joint priority, else the animation's)
    internal static (bool fingers, int prio) FingerInfo(BinBVHAnimationReader a)
    {
        bool f = false; int p = -1;
        foreach (var j in a.joints ?? Array.Empty<binBVHJoint>())
        {
            if (j.Name == null || !j.Name.StartsWith("mHand", StringComparison.Ordinal)) continue;
            if ((j.rotationkeys?.Length ?? 0) == 0 && (j.positionkeys?.Length ?? 0) == 0) continue;
            f = true; p = Math.Max(p, j.Priority >= 0 ? j.Priority : a.Priority);
        }
        return (f, p);
    }

    // pure: "undriven" | "ok" | "seat-overrides" | "unknown"
    internal static string HandsVerdict(IReadOnlyList<AnimFingers> anims)
    {
        if (anims.Any(a => !a.known)) return anims.Any(a => a.known && a.fingers) ? Verdict2(anims.Where(a => a.known).ToList()) : "unknown";
        return Verdict2(anims);
        static string Verdict2(IReadOnlyList<AnimFingers> k)
        {
            var f = k.Where(a => a.fingers).ToList();
            if (f.Count == 0) return "undriven";
            int top = f.Max(a => a.prio);
            bool seatTop = f.Where(a => a.prio == top).All(a => a.fromSeat) && f.Any(a => !a.fromSeat);
            return seatTop ? "seat-overrides" : "ok";
        }
    }

    static readonly HashSet<UUID> SystemAnimIds = typeof(Animations).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(UUID)).Select(f => (UUID)f.GetValue(null)).ToHashSet();
    static readonly ConcurrentDictionary<UUID, (bool ok, bool fingers, int prio)> fingerCache = new();
    static readonly string AnimCacheDir = Env("GT_ANIM_CACHE", "/home/box/viewers/textclient/objcache/anims");

    static async Task<(bool ok, bool fingers, int prio)> AnimFingerInfo(UUID id)
    {
        if (SystemAnimIds.Contains(id)) return (true, false, -1);   // system anims never key Bento fingers
        if (fingerCache.TryGetValue(id, out var c)) return c;
        byte[] data = null; var file = Path.Combine(AnimCacheDir, id + ".anim");
        try { if (File.Exists(file)) data = await File.ReadAllBytesAsync(file); } catch { }
        if (data is not { Length: > 0 })
        {
            try
            {
                var cap = client.Network.CurrentSim?.Caps?.CapabilityURI("ViewerAsset");
                if (cap == null) return (false, false, -1);
                var (resp, body) = await client.Assets.HttpDownloadManager.QueueDownloadAsync(new Uri($"{cap}?animatn_id={id}")).WaitAsync(TimeSpan.FromSeconds(20));
                if (resp != null && resp.IsSuccessStatusCode && body is { Length: > 0 })
                {
                    data = body;
                    try { Directory.CreateDirectory(AnimCacheDir); await File.WriteAllBytesAsync(file, data); } catch { }
                }
            }
            catch (Exception ex) { Log("hands", $"anim {id}: download failed ({ex.GetBaseException().Message})"); }
        }
        if (data is not { Length: > 0 }) return (false, false, -1);
        try { var (f, p) = FingerInfo(new BinBVHAnimationReader(data)); var r = (true, f, p); fingerCache[id] = r; return r; }
        catch (Exception ex) { Log("hands", $"anim {id}: parse failed ({ex.GetBaseException().Message})"); return (false, false, -1); }
    }

    static readonly ConcurrentDictionary<UUID, List<(UUID anim, UUID src)>> avatarAnims = new();
    static void HookHandBones() => client.Avatars.AvatarAnimation += (s, e) =>
    {
        try { avatarAnims[e.AvatarID] = e.Animations.Select(a => (a.AnimationID, a.AnimationSourceObjectID)).ToList(); } catch { }
    };

    static async Task<(string verdict, string detail)> CheckHands(UUID avatar, uint seatLocal)
    {
        List<(UUID anim, UUID src)> anims;
        if (avatar == client.Self.AgentID && !avatarAnims.ContainsKey(avatar)) anims = client.Self.SignaledAnimations.Keys.Select(k => (k, UUID.Zero)).ToList();
        else if (!avatarAnims.TryGetValue(avatar, out anims)) return ("unknown", "no animation list seen for that avatar yet");
        var sim = Sim; UUID seatId = UUID.Zero;
        if (seatLocal != 0 && sim.ObjectsPrimitives.TryGetValue(seatLocal, out var sp) && sp != null)
        {
            if (sp.ParentID != 0 && sim.ObjectsPrimitives.TryGetValue(sp.ParentID, out var root) && root != null) sp = root;
            seatId = sp.ID;
        }
        bool FromSeat(UUID src)
        {
            if (src == UUID.Zero || seatId == UUID.Zero) return false;
            if (src == seatId) return true;
            var o = sim.ObjectsPrimitives.Values.FirstOrDefault(p => p != null && p.ID == src);
            if (o == null) return false;
            return o.ParentID != 0 && sim.ObjectsPrimitives.TryGetValue(o.ParentID, out var r) && r?.ID == seatId;
        }
        var list = new List<AnimFingers>();
        foreach (var (anim, src) in anims)
        {
            var (ok, f, p) = await AnimFingerInfo(anim);
            list.Add(new AnimFingers(anim, ok, f, p, FromSeat(src)));
        }
        var v = HandsVerdict(list);
        var detail = string.Join(", ", list.Select(a => $"{a.id.ToString()[..8]}{(a.fromSeat ? "(seat)" : "")}:{(!a.known ? "?" : a.fingers ? $"fingers p{a.prio}" : "-")}"));
        return (v, detail);
    }

    // after a sit pose + its hand pose: verify; undriven -> relaxed once
    static void HandsVerifyAfterPose(string why)
    {
        var seat = client.Self.SittingOn;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(12000);
                if (client.Self.SittingOn != seat || seat == 0) return;
                var (v, d) = await CheckHands(client.Self.AgentID, seat);
                Log("hands", $"check after pose ({why}): {v} [{d}]");
                if (v == "undriven") await SetHandPose(HandRelaxedCol, "relaxed", "fingers undriven after " + why);
            }
            catch (Exception ex) { Log("hands", "check error: " + ex.GetBaseException().Message); }
        });
    }

    static async Task<string> HandsCheckCmd(string rest)
    {
        if (!LoggedIn) return "not logged in";
        if (string.IsNullOrWhiteSpace(rest)) { var (v, d) = await CheckHands(client.Self.AgentID, client.Self.SittingOn); return $"my hands: {v} [{d}]"; }
        var hit = FindAvatarPos(rest.Trim()); if (hit == null) return $"avatar '{rest}' not in view";
        var (v2, d2) = await CheckHands(hit.Value.av.ID, hit.Value.av.ParentID);
        return $"{hit.Value.av.Name}'s hands: {v2} [{d2}]";
    }
}
