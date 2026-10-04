// scene-mesher: `scene export` dump -> geometry batches for the Blender worker (no SL login; assets from the public CDN).
//   dotnet run -- --selftest | <export dir> [near_m=20] [avatar|scene|all] [cx,cy,cz r] [--anim=<uuid>]
//   (scene prims only within r of c; --anim=a,b,.. poses her with the held frame of SL animations (priority-blended), e.g. the default sit)
// Writes <dir>/mesh.json (batches: material + counts) and <dir>/mesh.bin (float32 pos/normal/uv, uint32 indices),
// one batch per material, world space for the scene and bind-pose avatar space for Galatea's rigged attachments.
// Prims: LibreMetaverse PrimMesher (via MeshFoundry). Sculpts: sculpt map from the CDN. Mesh: LOD from the CDN.
using System.Collections.Concurrent;
using System.Text.Json;
using LibreMetaverse;
using LibreMetaverse.Assets;
using LibreMetaverse.Rendering;
using LibreMetaverse.StructuredData;
using Path = System.IO.Path;
using Vector3 = LibreMetaverse.Vector3;

static class Mesher
{
    const string Cdn = "http://asset-cdn.glb.agni.lindenlab.com/";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    // the "use baked texture" placeholder UUIDs (constants from the SL viewer) -> appearance-service bake names
    static readonly Dictionary<UUID, string> BakeOf = new()
    {
        [new UUID("5a9f4a74-30f2-821c-b88d-70499d3e7183")] = "head", [new UUID("ae2de45c-d252-50b8-5c6e-19f39ce79317")] = "upper",
        [new UUID("24daea5f-0539-cfcf-047f-fbc40b2786ba")] = "lower", [new UUID("52cc6bb6-2ee5-e632-d3ad-50197b1dcb8a")] = "eyes",
        [new UUID("43529ce8-7faa-ad92-165a-bc4078371687")] = "skirt", [new UUID("09aac1fb-6bce-0bee-7d44-caac6dbb6c63")] = "hair",
        [new UUID("ff62763f-d60a-9855-890b-0c96f8f8cd98")] = "leftarm", [new UUID("8e915e25-31d1-cc95-ae08-d58a47488251")] = "leftleg",
        [new UUID("9742065b-19b5-297c-858a-29711d539043")] = "aux1", [new UUID("03642e83-2bd1-4eb9-34b4-4c47ed586d2d")] = "aux2",
        [new UUID("edd51b77-fc10-ce7a-4b3d-011dfc349e4f")] = "aux3",
    };
    static readonly UUID Transparent = new("8dcd4a48-2d37-4909-9f78-f7a9eb4ef903");

    sealed class Batch { public string Group, Tex; public float[] Rgba; public bool Fullbright; public Dictionary<string, object> Mat; public List<float> P = new(), N = new(), T = new(); public List<uint> I = new(); }
    static readonly string[] AlphaModes = { "none", "blend", "mask", "emissive" };   // LegacyMaterial DiffuseAlphaMode

    static async Task<byte[]> Get(string kind, UUID id)
    {
        try { return await Http.GetByteArrayAsync($"{Cdn}?{kind}_id={id}"); } catch { return null; }
    }

    static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--selftest")  // default skeleton sanity: pelvis ~1.07 m, head ~1.75 m, arms out (T-pose)
        {
            var w = Skeleton.World(new());
            float Z(string j) => w[j][14];
            bool ok = Math.Abs(Z("mPelvis") - 1.067f) < 0.01f && Z("mHead") > 1.6f && Z("mHead") < 1.9f && Math.Abs(w["mWristLeft"][13]) > 0.5f
                      && Xform(Skeleton.Mul(new float[] { 1,0,0,0, 0,1,0,0, 0,0,1,0, 1,2,3,1 }, new float[] { 2,0,0,0, 0,2,0,0, 0,0,2,0, 0,0,0,1 }), new Vector3(1, 1, 1), 1) == new Vector3(4, 6, 8);
            Console.WriteLine(ok ? "selftest ok" : $"selftest FAILED pelvis={Z("mPelvis")} head={Z("mHead")} wristY={w["mWristLeft"][13]}");
            return ok ? 0 : 1;
        }
        string animId = args.FirstOrDefault(a => a.StartsWith("--anim="))?[7..]; args = args.Where(a => !a.StartsWith("--anim=")).ToArray();
        if (args.Length < 1 || !File.Exists(Path.Combine(args[0], "scene.json"))) { Console.Error.WriteLine("usage: scene-mesher <export dir> [near_m]"); return 2; }
        string dir = args[0]; float near = args.Length > 1 ? float.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 20;
        var doc = (OSDMap)OSDParser.DeserializeJson(File.ReadAllText(Path.Combine(dir, "scene.json")));
        var me = ((OSDMap)doc["me"])["pos"].AsVector3();
        string only = args.Length > 2 ? args[2] : "all";
        Vector3 focus = me; float focusR = 1e9f;
        if (args.Length > 4) { var c = args[3].Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray(); focus = new Vector3(c[0], c[1], c[2]); focusR = float.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture); }
        var mf = new MeshFoundry();
        var batches = new ConcurrentDictionary<string, Batch>();
        var stats = new ConcurrentDictionary<string, int>();
        var rigged = new ConcurrentBag<(Primitive, FacetedMesh)>(); var unrigged = new ConcurrentBag<(Primitive, FacetedMesh)>();
        void Count(string k) => stats.AddOrUpdate(k, 1, (_, v) => v + 1);

        var prims = ((OSDArray)doc["prims"]).Cast<OSDMap>().Where(o => o.ContainsKey("attached_to_me")
            ? only != "scene" : only != "avatar" && Vector3.Distance(o["world_pos"].AsVector3(), focus) <= focusR).ToList();
        var lights = new List<object>();
        var byLocal = ((OSDArray)doc["prims"]).Cast<OSDMap>().ToDictionary(o => o["localid"].AsUInteger());
        // the export's TextureEntry JSON drops "face_number" for face 0, so LibreMetaverse would read face 0 as the default
        // face: restore it (the only per-face entry that can lack the key)
        foreach (var o in prims)
            if (o["textures"] is OSDArray tes)
                foreach (var f in tes.Skip(1).OfType<OSDMap>().Where(f => !f.ContainsKey("face_number"))) f["face_number"] = 0;
        // fetch each distinct mesh / sculpt asset once
        var assets = new ConcurrentDictionary<(string, UUID), byte[]>();
        await Parallel.ForEachAsync(prims.Select(Primitive.FromOSD).Where(p => p.Sculpt != null && p.Sculpt.SculptTexture != UUID.Zero)
                .Select(p => (p.Sculpt.Type == SculptType.Mesh ? "mesh" : "texture", p.Sculpt.SculptTexture)).Distinct(),
            new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (k, _) => assets[k] = await Get(k.Item1, k.Item2));
        // PBR (GLTF) material assets, per face, from the RenderMaterial extra param the export records
        var pbr = new ConcurrentDictionary<UUID, AssetMaterial>();
        await Parallel.ForEachAsync(prims.Where(o => o.ContainsKey("render_materials")).SelectMany(o => ((OSDMap)o["render_materials"]).Values.Select(v => v.AsUUID())).Distinct(),
            async (id, _) => { var d = await Get("material", id); var m = d == null ? null : new AssetMaterial(id, d); if (m != null && m.Decode()) pbr[id] = m; });
        var legacy = doc.ContainsKey("materials") ? (OSDMap)doc["materials"] : new OSDMap();
        Console.WriteLine($"assets: {assets.Count(a => a.Value != null)}/{assets.Count} fetched, {pbr.Count} pbr, {legacy.Count} legacy materials");

        Parallel.ForEach(prims, new ParallelOptions { MaxDegreeOfParallelism = 8 }, o =>
        {
            var p = Primitive.FromOSD(o);
            bool mine = o.ContainsKey("attached_to_me");
            var pos = mine ? Vector3.Zero : o["world_pos"].AsVector3();
            var rot = mine ? Quaternion.Identity : o["world_rot"].AsQuaternion();
            // child prims: world rotation = root * local, recomputed (exports before 2026-10-03 22:30 PT stored local * root)
            if (!mine && byLocal.TryGetValue(p.ParentID, out var par) && !par.ContainsKey("attached_to_me")) rot = par["rotation"].AsQuaternion() * p.Rotation;
            bool close = mine || Vector3.Distance(pos, focusR < 1e9f ? focus : me) < near;   // full LOD near the focus (or her)
            FacetedMesh fm = null; float[] bind = null;
            try
            {
                if (p.Sculpt?.Type == SculptType.Mesh)
                {
                    if (assets.TryGetValue(("mesh", p.Sculpt.SculptTexture), out var data) && data != null)
                        fm = mf.GenerateFacetedMeshMesh(p, data, close ? DetailLevel.Highest : DetailLevel.High);
                    if (fm?.SkinData != null) bind = fm.SkinData.BindShapeMatrix;
                    Count(fm == null ? "mesh_failed" : bind != null ? "mesh_rigged" : "mesh");
                }
                else if (p.Sculpt != null && p.Sculpt.SculptTexture != UUID.Zero)
                {
                    if (assets.TryGetValue(("texture", p.Sculpt.SculptTexture), out var data) && data != null)
                    {
                        var tex = new AssetTexture(UUID.Zero, data);
                        if (tex.Decode()) fm = mf.GenerateFacetedSculptMesh(p, tex.Image, DetailLevel.High);
                    }
                    Count(fm == null ? "sculpt_failed" : "sculpt");
                }
                else { fm = mf.GenerateFacetedMesh(p, close ? DetailLevel.Highest : DetailLevel.High); Count(fm == null ? "prim_failed" : "prim"); }
            }
            catch { Count("exception"); }
            if (fm == null) return;
            if (!mine && p.Light is { Intensity: > 0 } li)   // SL point lights (projector textures ignored)
                lock (lights) lights.Add(new { pos = new[] { pos.X, pos.Y, pos.Z }, color = new[] { li.Color.R, li.Color.G, li.Color.B }, intensity = li.Intensity, radius = li.Radius, falloff = li.Falloff });
            if (!mine && bind != null) { Count("rigged_in_world_skipped"); return; }
            if (mine) { (bind != null ? rigged : unrigged).Add((p, fm)); return; }  // posed after all joint overrides are known
            Emit(p, fm, false, null, pos, rot, o);
        });


        // per-face material: PBR asset > legacy material (alpha mode/cutoff, normal + specular maps) > SL's default
        // ("auto": the texture's own alpha blends). Normal/spec/PBR maps reuse the diffuse UVs.
        // ponytail: per-map repeats/offsets and GLTF overrides (none on exported prims yet) aren't applied; ceiling = tiled maps
        Dictionary<string, object> Mat(Primitive.TextureEntryFace te, int face, OSDMap o, ref string tex, ref float[] rgba)
        {
            var m = new Dictionary<string, object> { ["alpha"] = "auto" };
            static string U(UUID u) => u == UUID.Zero ? null : u.ToString();
            if (o.ContainsKey("render_materials") && ((OSDMap)o["render_materials"]).TryGetValue(face.ToString(), out var rid) && pbr.TryGetValue(rid.AsUUID(), out var g))
            {
                m["alpha"] = g.AlphaMode switch { GltfAlphaMode.Blend => "blend", GltfAlphaMode.Mask => "mask", _ => "none" }; m["cutoff"] = g.AlphaCutoff;
                var t = g.TextureIds ?? Array.Empty<UUID>(); UUID T(int i) => i < t.Length ? t[i] : UUID.Zero;
                tex = U(T(AssetMaterial.TEXTURE_BASE_COLOR)) ?? "none"; var c = g.BaseColorFactor; rgba = new[] { c.R, c.G, c.B, c.A };
                m["normal"] = U(T(AssetMaterial.TEXTURE_NORMAL)); m["mr"] = U(T(AssetMaterial.TEXTURE_METALLIC_ROUGHNESS)); m["emissive_tex"] = U(T(AssetMaterial.TEXTURE_EMISSIVE));
                m["metallic"] = g.MetallicFactor; m["roughness"] = g.RoughnessFactor; m["emissive"] = new[] { g.EmissiveFactor.X, g.EmissiveFactor.Y, g.EmissiveFactor.Z }; m["pbr"] = true;
            }
            else if (te.MaterialID != UUID.Zero && legacy.TryGetValue(te.MaterialID.ToString(), out var lo) && lo is OSDMap l)
            {
                int mode = l.ContainsKey("alpha_mode") ? l["alpha_mode"].AsInteger() : 0;
                m["alpha"] = AlphaModes[Math.Clamp(mode, 0, 3)]; m["cutoff"] = (l.ContainsKey("alpha_cutoff") ? l["alpha_cutoff"].AsInteger() : 0) / 255f;
                if (l.ContainsKey("normal")) m["normal"] = U(l["normal"].AsUUID());
                if (l.ContainsKey("spec")) m["spec"] = U(l["spec"].AsUUID());
                var sc = l.ContainsKey("spec_color") ? l["spec_color"].AsColor4() : Color4.White;
                m["spec_color"] = new[] { sc.R, sc.G, sc.B, sc.A }; m["gloss"] = (l.ContainsKey("spec_exp") ? l["spec_exp"].AsInteger() : 0) / 255f;
                m["env"] = (l.ContainsKey("env") ? l["env"].AsInteger() : 0) / 255f;
            }
            if (rgba[3] < 0.999f && (string)m["alpha"] != "blend") m["alpha"] = "blend";  // SL: a translucent face colour always blends
            return m;
        }

        void Emit(Primitive p, FacetedMesh fm, bool mine, Func<Vertex, VertexWeight?, (Vector3, Vector3)> pose, Vector3 pos, Quaternion rot, OSDMap o)
        {
            foreach (var f in fm.Faces)
            {
                var te = f.TextureFace ?? p.Textures?.DefaultTexture;
                if (te == null || te.RGBA.A < 0.01f || te.TextureID == Transparent || f.Indices.Count == 0) continue;
                var verts = f.Vertices.ToList(); var wts = f.Weights;
                mf.TransformTexCoords(verts, Vector3.Zero, te, p.Scale);
                string tex = BakeOf.TryGetValue(te.TextureID, out var bake) ? "bake:" + bake : te.TextureID.ToString();
                var rgba = new[] { te.RGBA.R, te.RGBA.G, te.RGBA.B, te.RGBA.A };
                var mat = Mat(te, f.ID, o, ref tex, ref rgba);
                string group = mine ? "avatar" : "scene";
                string key = $"{group}|{tex}|{string.Join(",", rgba.Select(x => x.ToString("F2")))}|{te.Fullbright}|{JsonSerializer.Serialize(mat)}";
                var b = batches.GetOrAdd(key, _ => new Batch { Group = group, Tex = tex, Rgba = rgba, Fullbright = te.Fullbright, Mat = mat });
                lock (b)
                {
                    uint baseIdx = (uint)(b.P.Count / 3);
                    for (int vi = 0; vi < verts.Count; vi++)
                    {
                        var v = verts[vi];
                        var (wp, wn) = pose != null ? pose(v, wts != null && vi < wts.Count ? wts[vi] : null) : (v.Position * p.Scale * rot + pos, Vector3.Normalize(v.Normal * rot));
                        b.P.Add(wp.X); b.P.Add(wp.Y); b.P.Add(wp.Z); b.N.Add(wn.X); b.N.Add(wn.Y); b.N.Add(wn.Z);
                        b.T.Add(v.TexCoord.X); b.T.Add(1f - v.TexCoord.Y);
                    }
                    foreach (var i in f.Indices) b.I.Add(baseIdx + i);
                }
            }
        }

        // Rigged attachments: bind pose = SL's default (T-)pose. Per vertex: sum_k w_k * v * BindShape * InvBind_jk * JointWorld_jk
        // (row vectors), JointWorld from LibreMetaverse's copy of avatar_skeleton.xml, with joint position overrides
        // from alt_inverse_bind_matrix translations, as the SL viewer does. Reimplemented from the documented behaviour.
        var overrides = new Dictionary<string, Vector3>();
        foreach (var (_, fm) in rigged)
        {
            var sk = fm.SkinData;
            for (int j = 0; j < sk.JointNames.Length && sk.AltInverseBindMatrices.Length >= (j + 1) * 16; j++)
                overrides[sk.JointNames[j]] = new Vector3(sk.AltInverseBindMatrices[j * 16 + 12], sk.AltInverseBindMatrices[j * 16 + 13], sk.AltInverseBindMatrices[j * 16 + 14]);
        }
        // her shape: visual params -> bone/collision-volume position+scale (LibreMetaverse's port of LLPolySkeletalDistortion)
        var lad = LindenAvatarDefinition.Load(Path.Combine(AppContext.BaseDirectory, "linden", "character", "avatar_lad.xml"));
        var vp = doc.ContainsKey("visual_params") ? ((OSDMap)doc["visual_params"]).ToDictionary(kv => int.Parse(kv.Key), kv => (float)kv.Value.AsReal()) : new Dictionary<int, float>();
        var shape = vp.Count > 0 ? lad.ComputeBoneTransforms(vp) : null;
        // animations (her playing set, e.g. AO stand + head expression): per joint the highest priority wins, ties -> later
        Dictionary<string, Quaternion> anim = null; Dictionary<string, Vector3> animPos = null; Vector3 pelvisOff = Vector3.Zero;
        if (animId != null)
        {
            anim = new(); animPos = new(); var prio = new Dictionary<string, (int r, int p)>();
            foreach (var id in animId.Split(','))
            {
                var data = await Get("animatn", new UUID(id));
                if (data == null) { Console.Error.WriteLine($"animation {id}: fetch failed, skipped"); continue; }
                var a = new BinBVHAnimationReader(data);
                // one held frame: mid-loop for looping animations (sits: in = out = end, so the end pose; blinks and AO stands
                // loop from 0, mid-loop dodges a blink at t=0), else the last frame. ponytail: no blending over time
                float tHold = a.Loop ? (a.InPoint + a.OutPoint) / 2 : a.Length;
                Vector3 At(binBVHJointKey[] k) => k.OrderBy(x => Math.Abs(x.time - tHold)).First().key_element;
                // LibreMetaverse decodes positions over -0.5..1.5; the SL format range is -5..5 (LL_MAX_PELVIS_OFFSET)
                Vector3 Pos(binBVHJointKey[] k) => (At(k) + new Vector3(0.5f, 0.5f, 0.5f)) * 5f - new Vector3(5, 5, 5);
                foreach (var j in a.joints)
                {
                    int jp = j.Priority >= 0 ? j.Priority : a.Priority;
                    var cur = prio.TryGetValue(j.Name, out var c) ? c : (r: -1, p: -1);
                    if (j.rotationkeys.Length > 0 && jp >= cur.r)
                    { var e = At(j.rotationkeys); anim[j.Name] = new Quaternion(e.X, e.Y, e.Z, MathF.Sqrt(Math.Max(0, 1 - e.LengthSquared()))); cur.r = jp; }
                    if (j.positionkeys.Length > 0 && jp >= cur.p)
                    { if (j.Name == "mPelvis") pelvisOff = Pos(j.positionkeys); else animPos[j.Name] = Pos(j.positionkeys); cur.p = jp; }
                    prio[j.Name] = cur;
                }
                Console.WriteLine($"animation {id}: priority {a.Priority}, {a.joints.Length} joints");
            }
            Console.WriteLine($"pose: {anim.Count} rotated joints, {animPos.Count} moved, pelvis offset {pelvisOff}");
        }
        var frames = new Dictionary<string, (Vector3 Pos, Quaternion Rot)>();
        var world = Skeleton.World(overrides, shape, anim, pelvisOff, frames, animPos);
        // non-rigged attachments: root prim sits at its attach point (avatar_lad.xml offset/rotation) on that point's joint
        var points = lad.AttachmentPoints.ToDictionary(ap => ap.Id);
        foreach (var (p, fm) in unrigged)
        {
            var o = byLocal[p.LocalID]; var root = o; Vector3 lp = p.Position; var lr = p.Rotation;
            if (byLocal.TryGetValue(p.ParentID, out var r0)) { root = r0; var rr = r0["rotation"].AsQuaternion(); lp = r0["position"].AsVector3() + p.Position * rr; lr = rr * p.Rotation; }
            int apId = root["attach_point"].AsInteger();
            if (apId >= 31 && apId <= 38 || !points.TryGetValue(apId, out var ap) || !frames.TryGetValue(ap.Joint, out var jf)) { Count("attachment_hud_or_unknown_skipped"); continue; }
            var apRot = Quaternion.CreateFromEulers(ap.Rotation.X * MathF.PI / 180, ap.Rotation.Y * MathF.PI / 180, ap.Rotation.Z * MathF.PI / 180);
            var wr = jf.Rot * apRot * lr; var wp = jf.Pos + (ap.Position + lp * apRot) * jf.Rot;
            Count("attachment_unrigged"); Emit(p, fm, true, null, wp, wr, o);
        }
        foreach (var (p, fm) in rigged)
        {
            var sk = fm.SkinData; var jm = new float[sk.JointNames.Length][];
            for (int j = 0; j < jm.Length; j++)
                jm[j] = world.TryGetValue(sk.JointNames[j], out var w) ? Skeleton.Mul(Skeleton.Mul(sk.BindShapeMatrix, sk.InverseBindMatrices[(j * 16)..(j * 16 + 16)]), w) : null;
            if (jm.Any(m => m == null)) Count("rigged_unknown_joint");
            Emit(p, fm, true, (v, w) =>
            {
                if (w == null) return (Xform(sk.BindShapeMatrix, v.Position, 1), Vector3.Normalize(Xform(sk.BindShapeMatrix, v.Normal, 0)));
                var x = w.Value; Vector3 ps = Vector3.Zero, ns = Vector3.Zero; float tw = 0;
                foreach (var (j, wt) in new[] { (x.Joint0, x.Weight0), (x.Joint1, x.Weight1), (x.Joint2, x.Weight2), (x.Joint3, x.Weight3) })
                {
                    if (wt <= 0 || j < 0 || j >= jm.Length || jm[j] == null) continue;
                    ps += Xform(jm[j], v.Position, 1) * wt; ns += Xform(jm[j], v.Normal, 0) * wt; tw += wt;
                }
                return tw > 0 ? (ps / tw, Vector3.Normalize(ns)) : (Xform(sk.BindShapeMatrix, v.Position, 1), v.Normal);
            }, Vector3.Zero, Quaternion.Identity, byLocal[p.LocalID]);
        }
        var outMeta = new List<object>(); long off = 0;
        using (var bin = new BinaryWriter(File.Create(Path.Combine(dir, "mesh.bin"))))
            foreach (var b in batches.Values.OrderBy(b => b.Group))
            {
                foreach (var x in b.P) bin.Write(x); foreach (var x in b.N) bin.Write(x); foreach (var x in b.T) bin.Write(x); foreach (var x in b.I) bin.Write(x);
                outMeta.Add(new { group = b.Group, tex = b.Tex, rgba = b.Rgba, fullbright = b.Fullbright, mat = b.Mat, nv = b.P.Count / 3, ni = b.I.Count, offset = off });
                off += (b.P.Count + b.N.Count + b.T.Count + b.I.Count) * 4L;
            }
        File.WriteAllText(Path.Combine(dir, "mesh.json"), JsonSerializer.Serialize(new { batches = outMeta, stats, bakes = doc["bakes"].ToString(), sun = doc.ContainsKey("sun_dir") ? new[] { doc["sun_dir"].AsVector3().X, doc["sun_dir"].AsVector3().Y, doc["sun_dir"].AsVector3().Z } : null, anim = animId, lights, pelvis = new[] { frames["mPelvis"].Pos.X, frames["mPelvis"].Pos.Y, frames["mPelvis"].Pos.Z }, head = new[] { frames["mHead"].Pos.X, frames["mHead"].Pos.Y, frames["mHead"].Pos.Z }, me = new[] { me.X, me.Y, me.Z } }));
        Console.WriteLine($"{batches.Count} batches, {outMeta.Sum(m => ((dynamic)m).nv)} vertices, {off / 1048576.0:F1} MB; " + string.Join(", ", stats.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")));
        return 0;
    }

    static class Skeleton
    {
        // 4x4 row-major, row-vector convention (v * M), like LibreMetaverse's MeshSkinData
        public static float[] Mul(float[] a, float[] b)
        {
            var r = new float[16];
            for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) { float t = 0; for (int k = 0; k < 4; k++) t += a[i * 4 + k] * b[k * 4 + j]; r[i * 4 + j] = t; }
            return r;
        }
        static float[] Matrix(Vector3 scale, Quaternion rot, Vector3 pos)
        {
            var m = new float[16]; var sc = new[] { scale.X, scale.Y, scale.Z };
            for (int i = 0; i < 3; i++)
            {
                var e = new Vector3(i == 0 ? 1 : 0, i == 1 ? 1 : 0, i == 2 ? 1 : 0) * sc[i] * rot;   // row i = image of basis i
                m[i * 4] = e.X; m[i * 4 + 1] = e.Y; m[i * 4 + 2] = e.Z;
            }
            m[12] = pos.X; m[13] = pos.Y; m[14] = pos.Z; m[15] = 1;
            return m;
        }
        static Vector3 V(float[] a, float d) => a != null && a.Length >= 3 ? new Vector3(a[0], a[1], a[2]) : new Vector3(d, d, d);
        // world matrix of every bone and collision volume (fitted mesh). As the SL viewer's joints: a child's offset is
        // scaled by its parent's own scale and rotated into the parent's world frame; a joint's scale is not inherited.
        // shape = visual-param bone transforms, anim = local joint rotations, overrides = rigged-mesh joint positions.
        public static Dictionary<string, float[]> World(Dictionary<string, Vector3> overrides, Dictionary<string, BoneTransform> shape = null,
            Dictionary<string, Quaternion> anim = null, Vector3 pelvisOffset = default, Dictionary<string, (Vector3, Quaternion)> frames = null,
            Dictionary<string, Vector3> animPos = null)
        {
            var d = new Dictionary<string, float[]>();
            void Walk(JointBase j, Vector3 pPos, Quaternion pRot, Vector3 pScale)
            {
                BoneTransform? bt = shape != null && shape.TryGetValue(j.name, out var t) ? t : null;
                var pos = overrides.TryGetValue(j.name, out var o) ? o : bt?.Position ?? V(j.pos, 0);
                if (j.name == "mPelvis") pos += pelvisOffset;
                else if (animPos != null && animPos.TryGetValue(j.name, out var ap)) pos = ap;   // animated joint translation (Bento face)
                var scale = bt?.Scale ?? V(j.scale, 1);
                var r = j.rot ?? new float[3];
                var local = Quaternion.CreateFromEulers(r[0] * MathF.PI / 180, r[1] * MathF.PI / 180, r[2] * MathF.PI / 180);
                if (anim != null && anim.TryGetValue(j.name, out var ar)) local = ar * local;
                // LibreMetaverse quaternions multiply Hamilton-style: pRot * local = apply local, then parent
                var wRot = pRot * local; var wPos = pPos + new Vector3(pos.X * pScale.X, pos.Y * pScale.Y, pos.Z * pScale.Z) * pRot;
                d[j.name] = Matrix(scale, wRot, wPos); if (frames != null) frames[j.name] = (wPos, wRot);
                if (j is Joint b)
                {
                    foreach (var cv in b.collision_volume ?? Array.Empty<CollisionVolume>()) Walk(cv, wPos, wRot, scale);
                    foreach (var c in b.bone ?? Array.Empty<Joint>()) Walk(c, wPos, wRot, scale);
                }
            }
            Walk(LindenSkeleton.Load().bone, Vector3.Zero, Quaternion.Identity, Vector3.One);
            return d;
        }
    }

    // row-vector convention (as LibreMetaverse's MeshSkinData): [x y z w] * M
    static Vector3 Xform(float[] m, Vector3 v, float w) =>
        new(v.X * m[0] + v.Y * m[4] + v.Z * m[8] + w * m[12], v.X * m[1] + v.Y * m[5] + v.Z * m[9] + w * m[13], v.X * m[2] + v.Y * m[6] + v.Z * m[10] + w * m[14]);
}
