// FullbrightSet.cs (2026-10-12, David: mech Cyber Eyes fullbright off; same path as GlowSet.cs)
// fullbright set <prim local id> <faces: 0,2,4 | all> <on|off>   | fullbright get <prim local id>
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    internal static (GlowSetArgs? a, bool on, string? err) ParseFullbrightArgs(string rest)
    {
        var t = (rest ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (t.Length != 3 || !(t[2] is "on" or "off" or "1" or "0")) return (null, false, "usage: fullbright set <prim local id|local:<id>> <faces: 0,2,4|all> <on|off>");
        var (a, err) = ParseGlowSetArgs($"{t[0]} {t[1]} 0");
        return (a, t[2] is "on" or "1", err);
    }

    internal static Primitive.TextureEntry ApplyFullbright(Primitive.TextureEntry src, int faceCount, IEnumerable<int>? faces, bool on)
    {
        var bytes = src.GetBytes();
        var te = new Primitive.TextureEntry(bytes, 0, bytes.Length);
        if (faces == null) te.DefaultTexture.Fullbright = on;
        foreach (var f in faces ?? Enumerable.Range(0, faceCount)) te.CreateFace((uint)f).Fullbright = on;
        return te;
    }

    internal static string FormatFullbright(Primitive.TextureEntry? te, int faceCount) =>
        te == null ? "?" : string.Join(" ", Enumerable.Range(0, faceCount).Select(f => $"{f}:{(te.GetFace((uint)f).Fullbright ? "on" : "off")}"));

    static async Task<string> FullbrightCmd(string rest)
    {
        var s = (rest ?? "").Trim();
        if (s.StartsWith("get "))
        {
            var a = s[4..].Trim(); if (a.StartsWith("local:")) a = a[6..];
            if (!uint.TryParse(a, out var lid)) return "usage: fullbright get <prim local id>";
            var p = OwnAttachmentPrim(lid, out var why); if (p == null) return why;
            return $"local {lid} faces {FaceCount(p)} fullbright {FormatFullbright(p.Textures, FaceCount(p))}";
        }
        if (!s.StartsWith("set ")) return "usage: fullbright set <prim local id> <faces|all> <on|off> | fullbright get <prim local id>";
        var (g, on, err) = ParseFullbrightArgs(s[4..]); if (err != null) return err;
        var pr = OwnAttachmentPrim(g!.LocalId, out var why2); if (pr == null) return why2;
        if (pr.Textures == null) return "refused: no texture entry known for that prim yet";
        await EnsureProperties(Sim, new List<Primitive> { pr });
        if (pr.Properties != null && (pr.Properties.Permissions.OwnerMask & PermissionMask.Modify) == 0) return $"refused: '{pr.Properties.Name}' is no-modify";
        int n = FaceCount(pr);
        if (g.Faces != null && g.Faces.Any(f => f >= n)) return $"refused: prim has {n} faces (0..{n - 1})";
        var before = FormatFullbright(pr.Textures, n);
        client.Objects.SetTextures(Sim, pr.LocalID, ApplyFullbright(pr.Textures, n, g.Faces, on));
        Log("fullbright", $"set local {pr.LocalID} obj {pr.ID} faces {(g.Faces == null ? "all" : string.Join(',', g.Faces))} -> {(on ? "on" : "off")}; BEFORE {before}");
        await Task.Delay(1200);
        return $"fullbright {(on ? "on" : "off")} sent for local {pr.LocalID}\n  before: {before}\n  now:    {FormatFullbright(pr.Textures, n)}";
    }
}
