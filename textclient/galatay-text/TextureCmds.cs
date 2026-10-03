// TextureCmds.cs (2026-10-02, David: let me "see" vendor boards / product photos without Firestorm)
// Thin wrapper over the separate, proprietary SL Texture Vision library (private repo davidabrooks/sl-texture-vision,
// git submodule at textclient/sl-texture-vision). Without the submodule the client still builds; these commands then
// just report that texture vision is not installed.
//   texture save <uuid>                         download one texture -> /workspace/secondlife/textures/<uuid>.png
//   faces <object name|uuid> [face=<n>] [r=<m>] list the linkset's faces + texture UUIDs and save the non-blank ones as PNG
//   vendor look <name filter> [radius]          scan nearby objects whose name/hover text matches (default 20 m), save their
//                                               face textures + index.json under /workspace/secondlife/textures/scan-*/
// Read-only: never touches, pays or edits anything.
using System.Globalization;
using System.Text;
using LibreMetaverse;
#if TEXTURE_VISION
using SlTextureVision;
#endif

namespace GalatayText;

public static partial class Program
{
    const string TextureDir = "/workspace/secondlife/textures";

#if TEXTURE_VISION
    static TextureVision textureVision;
    static TextureVision TV => textureVision ??= new TextureVision(client, new TextureVisionOptions { OutputDirectory = TextureDir });

    static async Task<string> TextureCmds(string cmd, string[] a, string rest)
    {
        try
        {
            switch (cmd)
            {
                case "texture":
                {
                    if (a.Length < 2 || a[0] != "save" || !UUID.TryParse(a[1], out var id)) return "usage: texture save <uuid>";
                    var r = await TV.SaveTextureAsync(id);
                    return r.Ok ? $"saved {r.PngPath}  {r.Width}x{r.Height}  ({r.Components} channels)" : $"texture {id}: {r.Error}";
                }
                case "faces":
                {
                    string face = null; float radius = 96; var words = new List<string>();
                    foreach (var w in a)
                    {
                        if (w.StartsWith("face=")) face = w[5..];
                        else if (w.StartsWith("r=") && float.TryParse(w[2..], NumberStyles.Float, CultureInfo.InvariantCulture, out var rr)) radius = rr;
                        else words.Add(w);
                    }
                    var target = string.Join(" ", words).Trim().Trim('"');
                    if (target.Length == 0) return "usage: faces <object name|uuid> [face=<n>] [r=<meters>]";
                    var prim = await TV.FindObjectAsync(target, radius);
                    if (prim == null) return $"no object matching '{target}' within {radius:F0} m";
                    var look = await TV.LookAtAsync(prim, face, "objects/" + TextureVision.SafeName((prim.Properties?.Name ?? "object")) + "-" + prim.ID.ToString()[..8]);
                    return FormatLook(look, face == null ? null : $"face {face} only");
                }
                case "vendor":
                {
                    if (a.Length < 2 || a[0] != "look") return "usage: vendor look <name filter> [radius]";
                    var parts = a[1..].ToList(); float radius = 20;
                    if (parts.Count > 1 && float.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var rr)) { radius = rr; parts.RemoveAt(parts.Count - 1); }
                    var filter = string.Join(" ", parts).Trim().Trim('"');
                    var (folder, looks) = await TV.ScanAsync(filter, radius, 12);
                    var sb = new StringBuilder($"scan '{filter}' within {radius:F0} m: {looks.Count} object(s), {looks.Sum(l => l.Saved.Count(s => s.Ok))} PNG(s)\nfolder {folder}\nindex  {Path.Combine(folder, "index.json")}\n");
                    foreach (var l in looks) sb.Append(FormatLook(l, null));
                    return sb.ToString();
                }
            }
            return "unknown texture command";
        }
        catch (Exception ex) { return $"{cmd} failed: {ex.GetBaseException().Message}"; }
    }

    static string FormatLook(ObjectLook l, string note)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0}  {1}  {2:F1}m  prims={3}{4}", l.Id, l.Name.Length > 0 ? l.Name : "(no name)", l.Distance, l.PrimCount, note == null ? "" : "  " + note));
        if (l.HoverText.Length > 0) sb.AppendLine("  hover: " + l.HoverText.Replace("\n", " | "));
        if (l.Description.Length > 0) sb.AppendLine("  desc:  " + l.Description);
        foreach (var g in l.Faces.GroupBy(f => f.TextureId))
        {
            var f0 = g.First();
            var where = string.Join(",", g.Select(f => $"L{f.LinkIndex}:{f.Face}").Take(8)) + (g.Count() > 8 ? $",+{g.Count() - 8}" : "");
            var saved = l.Saved.FirstOrDefault(s => s.TextureId == g.Key);
            var what = f0.Skipped ? $"skipped ({f0.SkipReason})" : saved == null ? "not saved" : saved.Ok ? $"{saved.PngPath}  {saved.Width}x{saved.Height}" : saved.Error;
            sb.AppendLine($"  {g.Key}  [{where}]  {what}");
        }
        return sb.ToString();
    }
#else
    static Task<string> TextureCmds(string cmd, string[] a, string rest) =>
        Task.FromResult("texture vision not installed: this build has no sl-texture-vision submodule (private, proprietary add-on)");
#endif
}
