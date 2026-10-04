// SceneExport.cs (2026-10-03, David: real shapes + her real face/body for the agent-vision test renders)
//   scene export [radius=48]   READ-ONLY: writes /workspace/secondlife/vision/export-<time>/scene.json with every prim
//                              (roots + children) whose root is within radius, her own attachments, nearby avatar
//                              positions, and her server-side bakes as raw .j2c (fetched from the appearance service).
//                              Also: legacy materials (RenderMaterials cap), per-face GLTF overrides, her decoded visual
//                              params, the region EEP environment and sun direction (all reads).
// Prims are LibreMetaverse's Primitive.GetOSD() plus world_pos/world_rot. Geometry is built offline by
// vision/scene-mesher (keeps meshing out of this process). Never touches, sends or edits anything.
using System.Globalization;
using LibreMetaverse;
using LibreMetaverse.Assets;
using LibreMetaverse.StructuredData;

namespace GalatayText;

public static partial class Program
{
    // bake name in the appearance-service URL, by AvatarTextureIndex (names as in the LL viewer's appearance dictionary)
    static readonly (int te, string name)[] Bakes = { (8, "head"), (9, "upper"), (10, "lower"), (11, "eyes"), (19, "skirt"), (20, "hair"),
                                                       (40, "leftarm"), (41, "leftleg"), (42, "aux1"), (43, "aux2"), (44, "aux3") };

    static async Task<string> SceneExport(string[] a)
    {
        float r = 48;
        if (a.Length > 1 && (!float.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out r) || r <= 0 || r > 128))
            return "usage: scene export [radius 1-128]";
        var sim = Sim; var me = client.Self;
        var myPos = me.SimPosition;
        var prims = sim.ObjectsPrimitives.Values.Where(p => p != null).ToList();
        var byLocal = prims.ToDictionary(p => p.LocalID);
        Primitive Root(Primitive p) { var q = p; for (int i = 0; i < 8 && q.ParentID != 0 && byLocal.TryGetValue(q.ParentID, out var up); i++) q = up; return q; }

        var outPrims = new OSDArray(); int mine = 0;
        foreach (var p in prims)
        {
            var root = Root(p); bool attachedToMe = root.ParentID == me.LocalID;
            if (root.ParentID != 0 && !attachedToMe) continue;            // other avatars' attachments: skipped (privacy, no skeleton yet)
            if (!attachedToMe && Vector3.Distance(root.Position, myPos) > r) continue;
            if (p.PrimData.PCode != PCode.Prim) continue;                 // trees/grass (PCode Tree/Grass) have no volume data
            var o = (OSDMap)p.GetOSD();
            if (p.RenderMaterials is { Count: > 0 } rms) { var m = new OSDMap(); foreach (var (f, id) in rms) m[f.ToString()] = OSD.FromUUID(id); o["render_materials"] = m; }
            if (attachedToMe) { o["attached_to_me"] = true; o["attach_point"] = (int)root.PrimData.AttachmentPoint; mine++; }
            else
            {
                var wp = p == root ? p.Position : root.Position + p.Position * root.Rotation;
                var wr = p == root ? p.Rotation : root.Rotation * p.Rotation;   // Hamilton order: local, then root
                o["world_pos"] = OSD.FromVector3(wp); o["world_rot"] = OSD.FromQuaternion(wr);
            }
            outPrims.Add(o);
        }

        var dir = $"/workspace/secondlife/vision/export-{DateTime.Now:yyyyMMdd-HHmmss}";
        Directory.CreateDirectory(dir);
        var bakes = new OSDMap(); var bakeErr = new List<string>();
        sim.ObjectsAvatars.TryGetValue(me.LocalID, out var self);
        foreach (var (te, name) in Bakes)
        {
            var id = self?.Textures?.GetFace((uint)te)?.TextureID ?? UUID.Zero;
            if (id == UUID.Zero || id == Primitive.TextureEntry.WHITE_TEXTURE || id.ToString() == "3a367d1c-bef1-6d43-7595-e88c1e3aadb3") continue; // unset / default
            var tex = await client.Assets.RequestServerBakedImageAsync(me.AgentID, id, name);
            if (tex?.AssetData == null) { bakeErr.Add(name); continue; }
            File.WriteAllBytes(Path.Combine(dir, $"bake-{name}.j2c"), tex.AssetData);
            bakes[name] = OSD.FromUUID(id);
        }
        var avatars = new OSDArray();
        foreach (var av in sim.ObjectsAvatars.Values.Where(x => x != null && x.LocalID != me.LocalID))
        {
            var ap = PositionHelper.GetAvatarPosition(sim, av);
            if (Vector3.Distance(ap, myPos) <= r) avatars.Add(new OSDMap { ["pos"] = OSD.FromVector3(ap), ["rot"] = OSD.FromQuaternion(av.Rotation) });
        }
        // legacy materials for every face MaterialID we export (alpha mode/cutoff, normal+spec maps)
        var matIds = new HashSet<UUID>(); var gltf = new OSDMap();
        foreach (var p in prims)
        {
            var te = p.Textures; if (te == null) continue;
            foreach (var f in te.FaceTextures.Append(te.DefaultTexture)) if (f != null && f.MaterialID != UUID.Zero) matIds.Add(f.MaterialID);
            if (sim.GLTFMaterialOverrides.TryGetValue(p.LocalID, out var ov) && ov.FaceOverrides.Count > 0)
            {
                var faces = new OSDMap();
                foreach (var (face, m) in ov.FaceOverrides) faces[face.ToString()] = GltfOsd(m);
                gltf[p.LocalID.ToString()] = faces;
            }
        }
        var mats = new OSDMap();
        foreach (var chunk in matIds.Chunk(50))                             // ponytail: the LL viewer posts <=50 ids per request
            foreach (var m in await client.Objects.RequestMaterialsAsync(sim, chunk))
                mats[m.ID.ToString()] = new OSDMap
                {
                    ["alpha_mode"] = (int)m.DiffuseAlphaMode, ["alpha_cutoff"] = (int)m.AlphaMaskCutoff,
                    ["normal"] = OSD.FromUUID(m.NormalMap), ["normal_rep"] = OSD.FromVector3(new Vector3((float)m.NormalMapRepeatX, (float)m.NormalMapRepeatY, (float)m.NormalMapRotation)),
                    ["normal_off"] = OSD.FromVector2(new Vector2((float)m.NormalMapOffsetX, (float)m.NormalMapOffsetY)),
                    ["spec"] = OSD.FromUUID(m.SpecularMap), ["spec_rep"] = OSD.FromVector3(new Vector3((float)m.SpecularMapRepeatX, (float)m.SpecularMapRepeatY, (float)m.SpecularMapRotation)),
                    ["spec_off"] = OSD.FromVector2(new Vector2((float)m.SpecularMapOffsetX, (float)m.SpecularMapOffsetY)),
                    ["spec_color"] = OSD.FromColor4(m.SpecularColor), ["spec_exp"] = (int)m.SpecularExponent, ["env"] = (int)m.EnvironmentIntensity,
                };
        var vparams = new OSDMap();
        if (self != null) foreach (var (id, v) in self.DecodeVisualParams()) vparams[id.ToString()] = v;
        OSD env = new OSDMap();
        try { var e = await client.Environment.GetRegionEnvironmentAsync(); if (e != null) env = e.Serialize(); } catch (Exception ex) { env = $"error: {ex.Message}"; }
        var doc = new OSDMap
        {
            ["materials"] = mats, ["gltf_overrides"] = gltf, ["visual_params"] = vparams, ["environment"] = env,
            ["sun_dir"] = OSD.FromVector3(client.Grid.SunDirection),
            ["region"] = sim.Name, ["agent_id"] = OSD.FromUUID(me.AgentID), ["me"] = new OSDMap { ["pos"] = OSD.FromVector3(myPos), ["rot"] = OSD.FromQuaternion(me.SimRotation), ["sitting_on"] = (int)me.SittingOn },
            ["radius"] = r, ["prims"] = outPrims, ["bakes"] = bakes, ["avatars"] = avatars, ["exported_at"] = DateTime.Now.ToString("o"),
        };
        File.WriteAllText(Path.Combine(dir, "scene.json"), OSDParser.SerializeJsonString(doc));
        return $"exported {outPrims.Count} prims, {mats.Count}/{matIds.Count} materials, {gltf.Count} gltf-override prims, {vparams.Count} visual params ({mine} on my attachments), {avatars.Count} avatar(s), bakes {string.Join(",", bakes.Keys)}" +
               (bakeErr.Count > 0 ? $" (failed: {string.Join(",", bakeErr)})" : "") + $"\n{dir}/scene.json";
    }

    static OSDMap GltfOsd(AssetMaterial m) => new()
    {
        ["alpha_mode"] = (int)m.AlphaMode, ["alpha_cutoff"] = m.AlphaCutoff, ["base_color"] = OSD.FromColor4(m.BaseColorFactor),
        ["metallic"] = m.MetallicFactor, ["roughness"] = m.RoughnessFactor, ["emissive"] = OSD.FromVector3(m.EmissiveFactor),
        ["double_sided"] = m.DoubleSided, ["textures"] = new OSDArray((m.TextureIds ?? Array.Empty<UUID>()).Select(t => OSD.FromUUID(t)).ToList()),
    };
}
