using LibreMetaverse;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GalatayText;

// 2026-11-09 (David t3223u-t3225u): animation asset headers (priority, length, loop, per-joint priority).
//   - Gestures: a non-looping animation (HELLO, CLAP...) stays in the sim's list for us until someone stops it; the viewer's motion
//     ends at the asset's length, so the gesture player now treats it as finished at its length (+ a small margin). The 30 s cap stays
//     as the fallback for unknown or looping animations.
//   - Visibility: the stock Linden gesture animations are low priority; her AO stands are higher on the arm joints, so observers see
//     the stand, not the wave (David saw the same with his own /wave). `anim check` / the gesture result name the joints a playing
//     animation outranks.
// Format: LibreMetaverse BinBVHAnimationReader (priority, length, loop, per-joint priority; joint priority -1 = the animation's).
public static partial class Program
{
    internal sealed record AnimHead(int Priority, double Length, bool Loop, double EaseOut, IReadOnlyDictionary<string, int> Joints);

    // pure: parse the binary animation header; only joints with keys count (an unkeyed joint does not drive the bone)
    internal static AnimHead AnimHeadParse(byte[] data)
    {
        var a = new BinBVHAnimationReader(data);
        var j = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var x in a.joints ?? Array.Empty<binBVHJoint>())
        {
            if (string.IsNullOrEmpty(x.Name) || ((x.rotationkeys?.Length ?? 0) == 0 && (x.positionkeys?.Length ?? 0) == 0)) continue;
            var p = x.Priority >= 0 ? x.Priority : a.Priority;
            j[x.Name] = j.TryGetValue(x.Name, out var q) ? Math.Max(p, q) : p;
        }
        return new(a.Priority, a.Length, a.Loop, a.EaseOutTime, j);
    }

    // pure: seconds after its start when a non-looping animation is over for the viewer (its length + margin); null = loops or unknown
    internal static double? AnimNaturalEnd(AnimHead h, double margin = 0.5)
        => h == null || h.Loop || !(h.Length > 0) || double.IsNaN(h.Length) ? null : Math.Min(h.Length, 60) + margin;

    // pure: the joints of `mine` that another playing animation outranks (strictly higher priority: the higher one wins that joint for
    // every viewer; on equal priority the later start wins, and the gesture starts last). Unknown headers are reported separately.
    internal sealed record AnimHidden(string Joint, int Mine, int Other, UUID By);
    internal static List<AnimHidden> AnimHiddenJoints(AnimHead mine, IEnumerable<(UUID id, AnimHead head)> others)
    {
        var r = new List<AnimHidden>();
        if (mine == null) return r;
        foreach (var (joint, p) in mine.Joints)
        {
            AnimHidden top = null;
            foreach (var (id, h) in others)
                if (h != null && h.Joints.TryGetValue(joint, out var q) && q > p && (top == null || q > top.Other)) top = new(joint, p, q, id);
            if (top != null) r.Add(top);
        }
        return r.OrderBy(x => x.Joint, StringComparer.Ordinal).ToList();
    }

    // pure: one line for the gesture result / log
    internal static string AnimVisibilityText(string name, AnimHead mine, List<AnimHidden> hidden, int unknownOthers, Func<UUID, string> label)
    {
        if (mine == null) return $"'{name}': priority unknown (asset not readable)";
        if (mine.Joints.Count == 0) return $"'{name}' (P{mine.Priority}): keys no joints";
        string head = $"'{name}' (P{mine.Priority}, {mine.Joints.Count} joint(s))";
        string unk = unknownOthers > 0 ? $"; {unknownOthers} playing animation(s) unreadable" : "";
        if (hidden.Count == 0) return $"{head}: visible over everything playing{unk}";
        var by = hidden.GroupBy(h => h.By).Select(g => $"{label(g.Key)} P{g.Max(x => x.Other)} on {g.Count()}").ToList();
        return $"{head}: HIDDEN on {hidden.Count}/{mine.Joints.Count} joint(s) by {string.Join(", ", by)}{unk}";
    }

    // ---- live ----
    static readonly ConcurrentDictionary<UUID, AnimHead> animHeads = new();
    static async Task<AnimHead> AnimHeadGet(UUID id)
    {
        if (animHeads.TryGetValue(id, out var h)) return h;
        var data = await AnimBytes(id);
        if (data == null) return null;
        try { h = AnimHeadParse(data); animHeads[id] = h; return h; }
        catch (Exception ex) { Log("anim", $"anim {id}: header parse failed ({ex.GetBaseException().Message})"); return null; }
    }
    static AnimHead AnimHeadCached(UUID id) => animHeads.TryGetValue(id, out var h) ? h : null;

    static async Task<Dictionary<UUID, AnimHead>> AnimHeadsGet(IEnumerable<UUID> ids, TimeSpan budget)
    {
        var list = ids.Distinct().ToList(); var res = new ConcurrentDictionary<UUID, AnimHead>();
        var all = Task.WhenAll(list.Select(async id => { var h = await AnimHeadGet(id); if (h != null) res[id] = h; }));
        await Task.WhenAny(all, Task.Delay(budget));
        return new(res);
    }

    static string AnimLabel(UUID id)
    {
        var n = AnimName(id); if (n != id.ToString()) return n;
        (int seq, UUID src) v = default; bool has; lock (animLock) has = ownAnims.TryGetValue(id, out v);
        return id.ToString()[..8] + (has ? $" from {SrcDesc(v.src)}" : "");
    }

    static string AnimHeadText(UUID id, AnimHead h)
    {
        if (h == null) return $"{AnimLabel(id)}: not readable";
        var arms = h.Joints.Where(j => j.Key.Contains("Shoulder") || j.Key.Contains("Elbow") || j.Key.Contains("Wrist") || j.Key.Contains("Collar")).Select(j => $"{j.Key}={j.Value}");
        return $"{AnimLabel(id)}: P{h.Priority}, {(h.Loop ? "loops" : "once")}, length {h.Length.ToString("0.##", CultureInfo.InvariantCulture)} s, {h.Joints.Count} joint(s)" +
               $"{(arms.Any() ? ", arms " + string.Join(" ", arms) : ", no arm joints")}";
    }

    // `anim info <uuid>` (one asset) | `anim check` (everything playing on me)  - read-only
    static async Task<string> AnimInfoCmd(string[] a)
    {
        if (a.Length >= 2 && a[0] == "info" && UUID.TryParse(a[1], out var id)) return AnimHeadText(id, await AnimHeadGet(id));
        if (a.Length >= 1 && a[0] == "check")
        {
            var ids = client.Self.SignaledAnimations.Keys.ToList();
            var hs = await AnimHeadsGet(ids, TimeSpan.FromSeconds(20));
            return $"{ids.Count} animation(s) playing:\n" + string.Join("\n", ids.Select(i => "  " + AnimHeadText(i, hs.TryGetValue(i, out var h) ? h : null)));
        }
        return "usage: anim info <uuid> | anim check";
    }
}
