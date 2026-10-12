// GlowSet.cs (2026-10-12, David: mech Cyber Eyes glow off/on; the eyes' HUD only toggles a ~0.01 glow and cannot restore exact amounts)
// glow set <prim local id> <faces: 0,2,4 | all> <value 0..1>   set the legacy glow on chosen faces of ONE prim of her own worn attachment
//   (owner must have modify) by sending the prim's TextureEntry back with only Glow changed (what the viewer's Build tool does).
//   Refuses prims that are not part of her own worn attachments and HUDs. Logs the previous per-face glow (undo = set them back).
// glow get <prim local id>   per-face glow values
using System.Globalization;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    internal record GlowSetArgs(uint LocalId, List<int>? Faces, float Value);   // Faces null = all faces

    internal static (GlowSetArgs? a, string? err) ParseGlowSetArgs(string rest)
    {
        const string usage = "usage: glow set <prim local id|local:<id>> <faces: 0,2,4|all> <value 0..1>";
        var t = (rest ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (t.Length != 3) return (null, usage);
        var ls = t[0].StartsWith("local:", StringComparison.OrdinalIgnoreCase) ? t[0][6..] : t[0];
        if (!uint.TryParse(ls, out var lid) || lid == 0) return (null, $"bad prim local id '{t[0]}' (see 'worn links')");
        List<int>? faces = null;
        if (!t[1].Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            faces = new();
            foreach (var f in t[1].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(f, out var n) || n < 0 || n > 7) return (null, $"bad face '{f}' (0..7, comma separated, or all)");
                if (!faces.Contains(n)) faces.Add(n);
            }
            if (faces.Count == 0) return (null, usage);
            faces.Sort();
        }
        if (!float.TryParse(t[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || float.IsNaN(v) || v < 0 || v > 1) return (null, $"bad glow '{t[2]}' (0..1)");
        return (new GlowSetArgs(lid, faces, v), null);
    }

    // copy of the entry with glow changed on the chosen faces only (all: default face and every face 0..n-1)
    internal static Primitive.TextureEntry ApplyGlow(Primitive.TextureEntry src, int faceCount, IEnumerable<int>? faces, float value)
    {
        var bytes = src.GetBytes();
        var te = new Primitive.TextureEntry(bytes, 0, bytes.Length);
        if (faces == null) te.DefaultTexture.Glow = value;
        foreach (var f in faces ?? Enumerable.Range(0, faceCount)) te.CreateFace((uint)f).Glow = value;
        return te;
    }

    internal static string FormatGlow(Primitive.TextureEntry? te, int faceCount)
    {
        if (te == null) return "?";
        return string.Join(" ", Enumerable.Range(0, faceCount).Select(f => $"{f}:{te.GetFace((uint)f).Glow.ToString("0.00", CultureInfo.InvariantCulture)}"));
    }

    static async Task<string> GlowCmd(string rest)
    {
        var s = (rest ?? "").Trim();
        if (s.StartsWith("get "))
        {
            var a = s[4..].Trim(); if (a.StartsWith("local:")) a = a[6..];
            if (!uint.TryParse(a, out var lid)) return "usage: glow get <prim local id>";
            var p = OwnAttachmentPrim(lid, out var why); if (p == null) return why;
            return $"local {lid} faces {FaceCount(p)} glow {FormatGlow(p.Textures, FaceCount(p))}";
        }
        if (s.StartsWith("set ")) return await GlowSet(s[4..]);
        return "usage: glow set <prim local id> <faces|all> <value 0..1> | glow get <prim local id>";
    }

    static async Task<string> GlowSet(string rest)
    {
        var (a, err) = ParseGlowSetArgs(rest); if (err != null) return err;
        var p = OwnAttachmentPrim(a!.LocalId, out var why); if (p == null) return why;
        if (p.Textures == null) return "refused: no texture entry known for that prim yet";
        await EnsureProperties(Sim, new List<Primitive> { p });
        if (p.Properties != null && (p.Properties.Permissions.OwnerMask & PermissionMask.Modify) == 0) return $"refused: '{p.Properties.Name}' is no-modify";
        int n = FaceCount(p);
        if (a.Faces != null && a.Faces.Any(f => f >= n)) return $"refused: prim has {n} faces (0..{n - 1})";
        var before = FormatGlow(p.Textures, n);
        var te = ApplyGlow(p.Textures, n, a.Faces, a.Value);
        client.Objects.SetTextures(Sim, p.LocalID, te);
        Log("glow", $"set local {p.LocalID} obj {p.ID} faces {(a.Faces == null ? "all" : string.Join(',', a.Faces))} -> {a.Value:0.00}; BEFORE {before}");
        await Task.Delay(1200);
        return $"glow {a.Value:0.00} sent for local {p.LocalID} faces {(a.Faces == null ? "all" : string.Join(',', a.Faces))}\n  before: {before}\n  now:    {FormatGlow(p.Textures, n)}";
    }
}
