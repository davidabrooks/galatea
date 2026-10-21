// AttFaces.cs (2026-10-21, David): 'att faces <attachment> [name filter]' dumps each prim's per-face TextureEntry
// (color RGBA, texture, glow) for a worn attachment, e.g. the Maitreya HUD 'hide:N' buttons (hidden segments are drawn
// differently) or the body itself (alpha 0 = hidden face).
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    // pure (tested): faces with a different look than the prim's most common one
    internal static string FaceSummary(IReadOnlyList<(int face, float r, float g, float b, float a, UUID tex)> faces)
    {
        if (faces.Count == 0) return "(no faces)";
        return string.Join(" ", faces.Select(f => $"f{f.face}:a{f.a:0.##}{(f.r < 0.99f || f.g < 0.99f || f.b < 0.99f ? $"/rgb{f.r:0.##},{f.g:0.##},{f.b:0.##}" : "")}/t{f.tex}"));
    }

    static async Task<string> AttFacesCmd(string attName, string filter)
    {
        var roots = WornPrims(); await EnsureProperties(Sim, roots);
        var mm = MatchAttachment(attName, roots);
        if (mm.Count != 1) return $"'{attName}' matches {mm.Count} worn attachments";
        var prims = LinkPrims(mm[0]); await EnsureProperties(Sim, prims);
        var sb = new System.Text.StringBuilder($"'{mm[0].Properties?.Name}': {prims.Count} prims\n");
        int i = 0;
        foreach (var p in prims)
        {
            i++;
            var n = p.Properties?.Name ?? "?";
            if (!string.IsNullOrEmpty(filter) && !n.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            var te = p.Textures; var faces = new List<(int, float, float, float, float, UUID)>();
            if (te != null)
                for (int f = 0; f < 45; f++)
                {
                    var fe = te.FaceTextures[f]; if (fe == null) continue;
                    faces.Add((f, fe.RGBA.R, fe.RGBA.G, fe.RGBA.B, fe.RGBA.A, fe.TextureID));
                }
            var d = te?.DefaultTexture;
            sb.AppendLine($"  link {i} local {p.LocalID} '{n}' default a{d?.RGBA.A:0.##} rgb{d?.RGBA.R:0.##},{d?.RGBA.G:0.##},{d?.RGBA.B:0.##} t{d?.TextureID} | {FaceSummary(faces)}");
        }
        return sb.ToString().TrimEnd();
    }
}
