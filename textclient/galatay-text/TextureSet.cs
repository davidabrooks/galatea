// TextureSet.cs (2026-10-12, David: clear a Cyber Eyes style the HUD leaves layered; same path as ColorSet.cs)
// texture set <prim local id> <faces: 0,2,4 | all> <texture uuid>   (only the texture id changes) | texture get <prim local id>
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    internal static (GlowSetArgs? a, UUID tex, string? err) ParseTextureSetArgs(string rest)
    {
        var t = (rest ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (t.Length != 3) return (null, UUID.Zero, "usage: texture set <prim local id|local:<id>> <faces: 0,2,4|all> <texture uuid>");
        if (!UUID.TryParse(t[2], out var tex) || tex == UUID.Zero) return (null, UUID.Zero, $"bad texture uuid '{t[2]}'");
        var (a, err) = ParseGlowSetArgs($"{t[0]} {t[1]} 0");
        return (a, tex, err);
    }

    internal static Primitive.TextureEntry ApplyTexture(Primitive.TextureEntry src, int faceCount, IEnumerable<int>? faces, UUID tex)
    {
        var bytes = src.GetBytes();
        var te = new Primitive.TextureEntry(bytes, 0, bytes.Length);
        if (faces == null) te.DefaultTexture.TextureID = tex;
        foreach (var f in faces ?? Enumerable.Range(0, faceCount)) te.CreateFace((uint)f).TextureID = tex;
        return te;
    }

    internal static string FormatTextures(Primitive.TextureEntry? te, int faceCount) =>
        te == null ? "?" : string.Join(" ", Enumerable.Range(0, faceCount).Select(f => $"{f}:{te.GetFace((uint)f).TextureID}"));

    static async Task<string> TextureCmd(string rest)
    {
        var s = (rest ?? "").Trim();
        if (s.StartsWith("get "))
        {
            var a = s[4..].Trim(); if (a.StartsWith("local:")) a = a[6..];
            if (!uint.TryParse(a, out var lid)) return "usage: texture get <prim local id>";
            var p = OwnAttachmentPrim(lid, out var why); if (p == null) return why;
            return $"local {lid} faces {FaceCount(p)} textures {FormatTextures(p.Textures, FaceCount(p))}";
        }
        if (!s.StartsWith("set ")) return "usage: texture set <prim local id> <faces|all> <texture uuid> | texture get <prim local id>";
        var (g, tex, err) = ParseTextureSetArgs(s[4..]); if (err != null) return err;
        var pr = OwnAttachmentPrim(g!.LocalId, out var why2); if (pr == null) return why2;
        if (pr.Textures == null) return "refused: no texture entry known for that prim yet";
        await EnsureProperties(Sim, new List<Primitive> { pr });
        if (pr.Properties != null && (pr.Properties.Permissions.OwnerMask & PermissionMask.Modify) == 0) return $"refused: '{pr.Properties.Name}' is no-modify";
        int n = FaceCount(pr);
        if (g.Faces != null && g.Faces.Any(f => f >= n)) return $"refused: prim has {n} faces (0..{n - 1})";
        var before = FormatTextures(pr.Textures, n);
        client.Objects.SetTextures(Sim, pr.LocalID, ApplyTexture(pr.Textures, n, g.Faces, tex));
        Log("texture", $"set local {pr.LocalID} obj {pr.ID} faces {(g.Faces == null ? "all" : string.Join(',', g.Faces))} -> {tex}; BEFORE {before}");
        await Task.Delay(1200);
        return $"texture {tex} sent for local {pr.LocalID}\n  before: {before}\n  now:    {FormatTextures(pr.Textures, n)}";
    }
}
