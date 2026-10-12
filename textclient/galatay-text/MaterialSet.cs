// MaterialSet.cs (2026-10-12, David: CALISTA hair's white head-cap -> Kay style 6 mirror chrome; the hair has no scripts, so no HUD reaches it)
// material set <prim local id> <faces: 0,2,4 | all> <pbr material asset uuid | none>
//   apply (or clear, 'none') a GLTF/PBR material asset on chosen faces of ONE prim of a worn attachment (owner must have modify),
//   via the region's ModifyMaterialParams capability (what the viewer's Build tool sends). Clears any GLTF override on those faces.
//   Refuses prims that are not part of her own worn attachments and HUDs. Logs the previous per-face material ids (undo).
// material undo <prim local id>   restore the per-face ids saved before the last 'material set' on that object (run/material-undo.json; none = cleared, i.e. the original)
// material get <prim local id>   per-face PBR material ids the sim reports for that prim (RenderMaterial extra param)
using System.Text;
using LibreMetaverse;
using LibreMetaverse.StructuredData;

namespace GalatayText;

public static partial class Program
{
    internal record MaterialSetArgs(uint LocalId, List<int>? Faces, UUID Asset);   // Faces null = all faces

    internal static (MaterialSetArgs? a, string? err) ParseMaterialSetArgs(string rest)
    {
        const string usage = "usage: material set <prim local id|local:<id>> <faces: 0,2,4|all> <material uuid|none>";
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
        UUID asset;
        if (t[2].Equals("none", StringComparison.OrdinalIgnoreCase)) asset = UUID.Zero;
        else if (!UUID.TryParse(t[2], out asset) || asset == UUID.Zero) return (null, $"bad material uuid '{t[2]}'");
        return (new MaterialSetArgs(lid, faces, asset), null);
    }

    // the LLSD array the ModifyMaterialParams cap takes (llgltfmateriallist.cpp flushUpdatesOnce): one entry per face
    internal static OSDArray MaterialSetPayload(UUID objectId, IEnumerable<int> faces, UUID asset)
    {
        var arr = new OSDArray();
        foreach (var f in faces)
            arr.Add(new OSDMap { ["object_id"] = OSD.FromUUID(objectId), ["side"] = OSD.FromInteger(f), ["asset_id"] = OSD.FromUUID(asset), ["gltf_json"] = OSD.FromString("") });
        return arr;
    }

    internal static string FormatRenderMaterials(IReadOnlyDictionary<int, UUID>? rm, int faceCount)
    {
        var sb = new StringBuilder();
        for (int f = 0; f < faceCount; f++)
        {
            UUID id = UUID.Zero; rm?.TryGetValue(f, out id);
            sb.Append(f == 0 ? "" : " ").Append($"{f}:{(id == UUID.Zero ? "none" : id.ToString())}");
        }
        return sb.ToString();
    }

    // null = her own worn attachment prim (root or child); otherwise the refusal
    static Primitive? OwnAttachmentPrim(uint lid, out string why)
    {
        why = ""; var sim = Sim; var me = client.Self.LocalID;
        var p = sim.ObjectsPrimitives.Values.FirstOrDefault(x => x != null && x.LocalID == lid);
        if (p == null) { why = $"no prim with local id {lid} in view (see 'worn links')"; return null; }
        var root = p.ParentID == me ? p : sim.ObjectsPrimitives.Values.FirstOrDefault(x => x != null && x.LocalID == p.ParentID);
        if (root == null || root.ParentID != me) { why = $"refused: local {lid} is not part of one of her worn attachments"; return null; }
        return p;
    }

    static async Task<string> MaterialCmd(string rest)
    {
        var s = (rest ?? "").Trim();
        if (s.StartsWith("get ")) return await MaterialGet(s[4..]);
        if (s.StartsWith("set ")) return await MaterialSet(s[4..]);
        if (s.StartsWith("undo ")) return await MaterialUndo(s[5..]);
        return "usage: material set <prim local id> <faces|all> <material uuid|none> | material get <prim local id>";
    }

    static async Task<string> MaterialGet(string rest)
    {
        var a = rest.Trim(); if (a.StartsWith("local:")) a = a[6..];
        if (!uint.TryParse(a, out var lid)) return "usage: material get <prim local id>";
        var p = OwnAttachmentPrim(lid, out var why); if (p == null) return why;
        await EnsureProperties(Sim, new List<Primitive> { p });
        return $"local {lid} '{p.Properties?.Name ?? "?"}' faces {FaceCount(p)}: {FormatRenderMaterials(p.RenderMaterials, FaceCount(p))}";
    }

    static string MaterialUndoFile => Path.Combine(Env("GT_RUN_DIR", "/home/box/viewers/textclient/run"), "material-undo.json");

    internal static OSDArray MaterialRestorePayload(UUID objectId, IReadOnlyDictionary<int, UUID> before)
    {
        var arr = new OSDArray();
        foreach (var (f, id) in before.OrderBy(k => k.Key)) arr.Add(MaterialSetPayload(objectId, new[] { f }, id)[0]);
        return arr;
    }

    static OSDMap LoadUndo() { try { return File.Exists(MaterialUndoFile) && OSDParser.DeserializeJson(File.ReadAllText(MaterialUndoFile)) is OSDMap m ? m : new OSDMap(); } catch { return new OSDMap(); } }

    static async Task<string> MaterialUndo(string rest)
    {
        var a = rest.Trim(); if (a.StartsWith("local:")) a = a[6..];
        if (!uint.TryParse(a, out var lid)) return "usage: material undo <prim local id>";
        var p = OwnAttachmentPrim(lid, out var why); if (p == null) return why;
        var u = LoadUndo(); if (!u.ContainsKey(p.ID.ToString())) return $"no saved 'before' state for object {p.ID}";
        var before = new Dictionary<int, UUID>(); var um = (OSDMap)u[p.ID.ToString()]; foreach (var k in um.Keys) before[int.Parse(k)] = um[k].AsUUID();
        var cap = client.Network.CurrentSim?.Caps?.CapabilityURI("ModifyMaterialParams"); if (cap == null) return "ModifyMaterialParams capability not available";
        using var t = new CancellationTokenSource(20000);
        var (resp, _) = await client.HttpCapsClient.PostAsync(cap, OSDFormat.Xml, MaterialRestorePayload(p.ID, before), t.Token);
        Log("material", $"undo local {p.LocalID} obj {p.ID}: HTTP {(int)resp.StatusCode} -> {FormatRenderMaterials(before, FaceCount(p))}");
        await Task.Delay(2500);
        return $"undo HTTP {(int)resp.StatusCode}; now: {FormatRenderMaterials(p.RenderMaterials, FaceCount(p))}";
    }

    static async Task<string> MaterialSet(string rest)
    {
        var (a, err) = ParseMaterialSetArgs(rest); if (err != null) return err;
        var p = OwnAttachmentPrim(a!.LocalId, out var why); if (p == null) return why;
        await EnsureProperties(Sim, new List<Primitive> { p });
        if (p.Properties != null && (p.Properties.Permissions.OwnerMask & PermissionMask.Modify) == 0) return $"refused: '{p.Properties.Name}' is no-modify";
        int n = FaceCount(p);
        var faces = a.Faces ?? Enumerable.Range(0, n).ToList();
        if (faces.Any(f => f >= n)) return $"refused: prim has {n} faces (0..{n - 1})";
        var cap = client.Network.CurrentSim?.Caps?.CapabilityURI("ModifyMaterialParams");
        if (cap == null) return "ModifyMaterialParams capability not available in this region";
        var before = FormatRenderMaterials(p.RenderMaterials, n);
        try
        {
            var u = LoadUndo(); var key = p.ID.ToString();
            if (!u.ContainsKey(key)) // keep the FIRST before-state: undo always goes back to the original
            { var m = new OSDMap(); foreach (var f in faces) { UUID id = UUID.Zero; p.RenderMaterials?.TryGetValue(f, out id); m[f.ToString()] = OSD.FromUUID(id); } u[key] = m; File.WriteAllText(MaterialUndoFile, OSDParser.SerializeJsonString(u, true)); }
        }
        catch (Exception ex) { return "refused: could not save undo state: " + ex.Message; }
        int http; string body;
        try
        {
            using var t = new CancellationTokenSource(20000);
            var (resp, data) = await client.HttpCapsClient.PostAsync(cap, OSDFormat.Xml, MaterialSetPayload(p.ID, faces, a.Asset), t.Token);
            http = (int)resp.StatusCode; body = data == null ? "" : Encoding.UTF8.GetString(data);
        }
        catch (Exception ex) { return "ModifyMaterialParams failed: " + ex.GetBaseException().Message; }
        bool ok = http == 200 && !body.Contains("<key>success</key><boolean>false", StringComparison.OrdinalIgnoreCase) && !body.Contains("<key>success</key><false", StringComparison.OrdinalIgnoreCase);
        string msg = "";
        try { if (OSDParser.Deserialize(body) is OSDMap m) { if (m.ContainsKey("success")) ok = ok && m["success"].AsBoolean(); if (m.ContainsKey("message")) msg = m["message"].AsString(); } } catch { }
        Log("material", $"set local {p.LocalID} obj {p.ID} '{p.Properties?.Name}' faces {string.Join(',', faces)} -> {(a.Asset == UUID.Zero ? "none" : a.Asset)}: HTTP {http} ok={ok} {msg}; BEFORE {before}");
        await Task.Delay(2500);
        return $"{(ok ? "applied" : "FAILED")} {(a.Asset == UUID.Zero ? "none (cleared)" : a.Asset.ToString())} on local {p.LocalID} '{p.Properties?.Name}' faces {string.Join(',', faces)} (HTTP {http}{(msg.Length > 0 ? ", " + msg : "")})\n" +
               $"  before: {before}\n  now:    {FormatRenderMaterials(p.RenderMaterials, n)}\n  undo:   material undo {p.LocalID}";
    }
}
