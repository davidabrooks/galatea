// 2026-10-07 The V (Session Skins) pubic hair from the Play HUD, without a viewer.
// The HUD's "Vagina look" panel puts the 8 pubic hair buttons on invisible faces of the prim 'vagina_look_menu_2'.
// Face order (learned live by touching each face and reading The V's pubes face texture back) is NOT the panel order:
//   0 SHAVED, 1 BLACK, 2 BROWN, 3 BLOND, 4 STRIP, 5 GINGER, 6 TRIMMED, 7 BUSH.
// A colour keeps the current style and a style keeps the current colour, so "blond strip" = touch face 3, then face 4.
// The V shows the result as the texture on faces 0+7 of its mesh child whose face 1 is the skin patch 8dc72b73
// (alpha 0 = shaved). The state lives in the attachment's prims, so it persists across detach/re-attach, relog and TP.
// Piercing: faces of the big panel prim 'vagina_look_menu_1': 4 BARS, 5 NONE, 6 BALL, 7 HOOP (faces 0-3 did nothing
// visible). The V shows it as one of three hidden mesh children (texture 506f3252, silver): the 1-face one = ball,
// 2 faces = hoop (ring + bead; seen in a render 2026-10-07), 3 faces = bars.
// Doc: routes/the-v-hud.md.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    internal const string TheVHudName = "The V - Bento Play HUD";
    internal const string TheVPubesPrim = "vagina_look_menu_2";
    internal static readonly UUID TheVItem = new("3034de7b-4f25-3d3a-980c-8a00134f240d");

    internal static readonly IReadOnlyDictionary<string, int> TheVPubesFace = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["shaved"] = 0, ["black"] = 1, ["brown"] = 2, ["blond"] = 3, ["blonde"] = 3, ["strip"] = 4, ["landing"] = 4,
        ["ginger"] = 5, ["red"] = 5, ["trimmed"] = 6, ["bush"] = 7,
    };
    static readonly HashSet<int> TheVColourFaces = new() { 1, 2, 3, 5 };
    static readonly HashSet<int> TheVStyleFaces = new() { 4, 6, 7 };

    // textures seen live 2026-10-07 (faces 0+7 of the V mesh child); others are reported as 'unknown texture'
    internal static readonly IReadOnlyDictionary<string, string> TheVPubesTex = new Dictionary<string, string>
    {
        ["5c4db955-e569-2caf-251d-0392af150fab"] = "blond strip",
        ["f4bb3440-3724-71c7-8175-0202a6d483ff"] = "blond trimmed",
        ["97313e78-56ff-c45b-c080-fda2373b7b02"] = "ginger strip",
        ["d439f8f3-5f7c-8927-ff3e-64f200f3291b"] = "ginger trimmed",
        ["d0fd5d7b-3092-a678-c42b-5dd48cfd4e13"] = "ginger bush",
        ["12d20473-8f72-05e8-028f-e41c430dda13"] = "black bush",
        ["918a4e8a-5910-c473-5c79-54827ea470f9"] = "brown bush",
        ["0b81f6ab-dbbd-797f-2d91-fc1ad935983c"] = "brown strip",
    };

    /// <summary>Pure: words ("blond strip", "shaved", "landing strip", "bush") -> HUD faces to touch in order
    /// (colour first, then style), or an error starting with "usage". Shaved takes no style.</summary>
    internal static (List<int> faces, string err) TheVPubesPlan(IEnumerable<string> words)
    {
        var w = words.Select(x => x.Trim().Trim(',')).Where(x => x.Length > 0).ToList();
        if (w.Count >= 2 && w[^2].Equals("landing", StringComparison.OrdinalIgnoreCase) && w[^1].Equals("strip", StringComparison.OrdinalIgnoreCase)) w.RemoveAt(w.Count - 1);
        int colour = -1, style = -1; bool shaved = false;
        foreach (var x in w)
        {
            if (!TheVPubesFace.TryGetValue(x, out var f)) return (null, $"usage: thev pubes <shaved | black|brown|blond|ginger [trimmed|strip|bush] | trimmed|strip|bush>  ('{x}' unknown)");
            if (f == 0) { shaved = true; continue; }
            if (TheVColourFaces.Contains(f)) { if (colour >= 0 && colour != f) return (null, "usage: one colour only"); colour = f; }
            if (TheVStyleFaces.Contains(f)) { if (style >= 0 && style != f) return (null, "usage: one style only"); style = f; }
        }
        if (shaved) return colour >= 0 || style >= 0 ? (null, "usage: shaved takes no colour or style") : (new List<int> { 0 }, null);
        var r = new List<int>(); if (colour >= 0) r.Add(colour); if (style >= 0) r.Add(style);
        return r.Count == 0 ? (null, "usage: thev pubes <shaved | black|brown|blond|ginger [trimmed|strip|bush] | trimmed|strip|bush>") : (r, null);
    }

    internal const string TheVPiercePrim = "vagina_look_menu_1";
    internal static readonly IReadOnlyDictionary<string, int> TheVPierceFace = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    { ["bars"] = 4, ["none"] = 5, ["ball"] = 6, ["hoop"] = 7, ["ring"] = 7 };

    /// <summary>Pure: visible piercing prim(s) of The V, as face counts (1 ball, 2 hoop, 3 bars) -> label.</summary>
    internal static string TheVPierceLabel(IEnumerable<int> visibleFaceCounts)
    {
        var v = visibleFaceCounts.ToList();
        if (v.Count == 0) return "none";
        if (v.Count > 1) return "several visible (" + string.Join(",", v) + " faces)";
        return v[0] switch { 1 => "ball", 2 => "hoop (ring)", 3 => "bars", _ => $"unknown ({v[0]} faces)" };
    }

    /// <summary>Pure: The V pubes face texture + alpha -> label.</summary>
    internal static string TheVPubesLabel(UUID tex, float alpha) =>
        alpha < 0.01f ? "shaved (pubes face hidden)" : TheVPubesTex.TryGetValue(tex.ToString(), out var l) ? l : $"unknown texture {tex}";

    static async Task<string> TheVPubesState()
    {
        var roots = WornPrims(); await EnsureProperties(Sim, roots);
        var v = roots.FirstOrDefault(p => AttachItemId(p) == TheVItem) ?? roots.FirstOrDefault(p => (p.Properties?.Name ?? "").StartsWith("The V - Bento by", StringComparison.OrdinalIgnoreCase));
        if (v == null) return "The V is not worn";
        string pubes = null; var pierce = new List<int>();
        foreach (var p in LinkPrims(v))
        {
            var te = p.Textures; if (te == null) continue;
            int n = FaceCount(p);
            try
            {
                if (te.GetFace(1).TextureID.ToString().StartsWith("8dc72b73"))
                {
                    var f0 = te.GetFace(0);
                    pubes = $"pubes: {TheVPubesLabel(f0.TextureID, f0.RGBA.A)} (texture {f0.TextureID}, alpha {f0.RGBA.A:F2})";
                }
                else if (te.GetFace(0).TextureID.ToString().StartsWith("506f3252") && te.GetFace(0).RGBA.A > 0.01f) pierce.Add(n);
            }
            catch { }
        }
        return (pubes ?? "pubes face not found (prims not loaded yet?)") + $"; piercing: {TheVPierceLabel(pierce)}";
    }

    static async Task<string> TheVCmd(string rest)
    {
        if (!LoggedIn) return "not logged in";
        var a = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (a.Length == 0 || a[0].Equals("status", StringComparison.OrdinalIgnoreCase)) return await TheVPubesState();
        if (a[0].Equals("pierce", StringComparison.OrdinalIgnoreCase) || a[0].Equals("piercing", StringComparison.OrdinalIgnoreCase))
        {
            if (a.Length < 2) return await TheVPubesState();
            if (!TheVPierceFace.TryGetValue(a[1], out var pf)) return "usage: thev pierce none|ball|bars|hoop";
            var t = await TouchAttachment($"\"{TheVHudName}\" {TheVPiercePrim} {pf}");
            if (!t.StartsWith("touched")) return t;
            await Task.Delay(4000);
            var r = $"piercing face {pf} ({a[1]}) touched; " + await TheVPubesState(); Log("thev", r); return r;
        }
        if (!a[0].Equals("pubes", StringComparison.OrdinalIgnoreCase)) return "usage: thev [status] | thev pubes <shaved | colour [style] | style> | thev pierce none|ball|bars|hoop";
        if (a.Length == 1) return await TheVPubesState();
        var (faces, err) = TheVPubesPlan(a.Skip(1)); if (err != null) return err;
        var sb = new StringBuilder();
        foreach (var f in faces)
        {
            var t = await TouchAttachment($"\"{TheVHudName}\" {TheVPubesPrim} {f}");
            if (!t.StartsWith("touched")) return t;
            sb.Append($"face {f} ({TheVPubesFace.First(kv => kv.Value == f).Key}) touched; ");
            await Task.Delay(4000);
        }
        var s = await TheVPubesState(); Log("thev", sb + s);
        return sb + s;
    }
}
