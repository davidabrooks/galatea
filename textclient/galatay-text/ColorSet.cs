// ColorSet.cs (2026-10-12, David: mech Cyber Eyes ring tint; same path as GlowSet.cs)
// color set <prim local id> <faces: 0,2,4 | all> <r> <g> <b>   (0..255; alpha kept) | color get <prim local id>
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    internal static (GlowSetArgs? a, byte[]? rgb, string? err) ParseColorArgs(string rest)
    {
        const string usage = "usage: color set <prim local id|local:<id>> <faces: 0,2,4|all> <r> <g> <b>  (0..255)";
        var t = (rest ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (t.Length != 5) return (null, null, usage);
        var rgb = new byte[3];
        for (int i = 0; i < 3; i++) if (!byte.TryParse(t[2 + i], out rgb[i])) return (null, null, $"bad colour value '{t[2 + i]}' (0..255)");
        var (a, err) = ParseGlowSetArgs($"{t[0]} {t[1]} 0");
        return (a, err == null ? rgb : null, err);
    }

    internal static Primitive.TextureEntry ApplyColor(Primitive.TextureEntry src, int faceCount, IEnumerable<int>? faces, byte[] rgb)
    {
        var bytes = src.GetBytes();
        var te = new Primitive.TextureEntry(bytes, 0, bytes.Length);
        void Set(Primitive.TextureEntryFace f) { var a = f.RGBA.A; f.RGBA = new Color4(rgb[0] / 255f, rgb[1] / 255f, rgb[2] / 255f, a); }
        if (faces == null) Set(te.DefaultTexture);
        foreach (var f in faces ?? Enumerable.Range(0, faceCount)) Set(te.CreateFace((uint)f));
        return te;
    }

    internal static string FormatColor(Primitive.TextureEntry? te, int faceCount) =>
        te == null ? "?" : string.Join(" ", Enumerable.Range(0, faceCount).Select(f => { var c = te.GetFace((uint)f).RGBA; return $"{f}:{(int)Math.Round(c.R * 255)},{(int)Math.Round(c.G * 255)},{(int)Math.Round(c.B * 255)},a{c.A:0.00}"; }));

    static async Task<string> ColorCmd(string rest)
    {
        var s = (rest ?? "").Trim();
        if (s.StartsWith("get "))
        {
            var a = s[4..].Trim(); if (a.StartsWith("local:")) a = a[6..];
            if (!uint.TryParse(a, out var lid)) return "usage: color get <prim local id>";
            var p = OwnAttachmentPrim(lid, out var why); if (p == null) return why;
            return $"local {lid} faces {FaceCount(p)} color {FormatColor(p.Textures, FaceCount(p))}";
        }
        if (!s.StartsWith("set ")) return "usage: color set <prim local id> <faces|all> <r> <g> <b> | color get <prim local id>";
        var (g, rgb, err) = ParseColorArgs(s[4..]); if (err != null) return err;
        var pr = OwnAttachmentPrim(g!.LocalId, out var why2); if (pr == null) return why2;
        if (pr.Textures == null) return "refused: no texture entry known for that prim yet";
        await EnsureProperties(Sim, new List<Primitive> { pr });
        if (pr.Properties != null && (pr.Properties.Permissions.OwnerMask & PermissionMask.Modify) == 0) return $"refused: '{pr.Properties.Name}' is no-modify";
        int n = FaceCount(pr);
        if (g.Faces != null && g.Faces.Any(f => f >= n)) return $"refused: prim has {n} faces (0..{n - 1})";
        var before = FormatColor(pr.Textures, n);
        client.Objects.SetTextures(Sim, pr.LocalID, ApplyColor(pr.Textures, n, g.Faces, rgb!));
        Log("color", $"set local {pr.LocalID} obj {pr.ID} faces {(g.Faces == null ? "all" : string.Join(',', g.Faces))} -> {rgb![0]},{rgb[1]},{rgb[2]}; BEFORE {before}");
        await Task.Delay(1200);
        return $"color {rgb[0]},{rgb[1]},{rgb[2]} sent for local {pr.LocalID}\n  before: {before}\n  now:    {FormatColor(pr.Textures, n)}";
    }
}
