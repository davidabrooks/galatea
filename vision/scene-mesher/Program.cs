// scene-mesher: `scene export` dump -> geometry batches for the Blender worker (no SL login; assets from the public CDN).
//   dotnet run -- <export dir> [near_m=20] [avatar|scene|all] [cx,cy,cz r]   (scene prims only within r of c)
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

    sealed class Batch { public string Group, Tex; public float[] Rgba; public bool Fullbright; public List<float> P = new(), N = new(), T = new(); public List<uint> I = new(); }

    static async Task<byte[]> Get(string kind, UUID id)
    {
        try { return await Http.GetByteArrayAsync($"{Cdn}?{kind}_id={id}"); } catch { return null; }
    }

    static async Task<int> Main(string[] args)
    {
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
        var rigged = new ConcurrentBag<(Primitive, FacetedMesh)>();
        void Count(string k) => stats.AddOrUpdate(k, 1, (_, v) => v + 1);

        var prims = ((OSDArray)doc["prims"]).Cast<OSDMap>().Where(o => o.ContainsKey("attached_to_me")
            ? only != "scene" : only != "avatar" && Vector3.Distance(o["world_pos"].AsVector3(), focus) <= focusR).ToList();
        // fetch each distinct mesh / sculpt asset once
        var assets = new ConcurrentDictionary<(string, UUID), byte[]>();
        await Parallel.ForEachAsync(prims.Select(Primitive.FromOSD).Where(p => p.Sculpt != null && p.Sculpt.SculptTexture != UUID.Zero)
                .Select(p => (p.Sculpt.Type == SculptType.Mesh ? "mesh" : "texture", p.Sculpt.SculptTexture)).Distinct(),
            new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (k, _) => assets[k] = await Get(k.Item1, k.Item2));
        Console.WriteLine($"assets: {assets.Count(a => a.Value != null)}/{assets.Count} fetched");

        Parallel.ForEach(prims, new ParallelOptions { MaxDegreeOfParallelism = 8 }, o =>
        {
            var p = Primitive.FromOSD(o);
            bool mine = o.ContainsKey("attached_to_me");
            var pos = mine ? Vector3.Zero : o["world_pos"].AsVector3();
            var rot = mine ? Quaternion.Identity : o["world_rot"].AsQuaternion();
            bool close = mine || Vector3.Distance(pos, me) < near;
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
            // ponytail: non-rigged attachments need attach-point joint positions (skeleton); ceiling = rigged-only avatar
            if (mine && bind == null) { Count("attachment_unrigged_skipped"); return; }
            if (!mine && bind != null) { Count("rigged_in_world_skipped"); return; }
            if (bind != null) { rigged.Add((p, fm)); return; }  // posed after all joint overrides are known

            Emit(p, fm, mine, null, pos, rot);
        });


        void Emit(Primitive p, FacetedMesh fm, bool mine, Func<Vertex, VertexWeight?, (Vector3, Vector3)> pose, Vector3 pos, Quaternion rot)
        {
            foreach (var f in fm.Faces)
            {
                var te = f.TextureFace ?? p.Textures?.DefaultTexture;
                if (te == null || te.RGBA.A < 0.01f || te.TextureID == Transparent || f.Indices.Count == 0) continue;
                var verts = f.Vertices.ToList(); var wts = f.Weights;
                mf.TransformTexCoords(verts, Vector3.Zero, te, p.Scale);
                string tex = BakeOf.TryGetValue(te.TextureID, out var bake) ? "bake:" + bake : te.TextureID.ToString();
                string group = mine ? "avatar" : "scene";
                string key = $"{group}|{tex}|{te.RGBA.R:F2},{te.RGBA.G:F2},{te.RGBA.B:F2},{te.RGBA.A:F2}|{te.Fullbright}";
                var b = batches.GetOrAdd(key, _ => new Batch { Group = group, Tex = tex, Rgba = new[] { te.RGBA.R, te.RGBA.G, te.RGBA.B, te.RGBA.A }, Fullbright = te.Fullbright });
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
        var world = Skeleton.World(overrides);
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
            }, Vector3.Zero, Quaternion.Identity);
        }
        var outMeta = new List<object>(); long off = 0;
        using (var bin = new BinaryWriter(File.Create(Path.Combine(dir, "mesh.bin"))))
            foreach (var b in batches.Values.OrderBy(b => b.Group))
            {
                foreach (var x in b.P) bin.Write(x); foreach (var x in b.N) bin.Write(x); foreach (var x in b.T) bin.Write(x); foreach (var x in b.I) bin.Write(x);
                outMeta.Add(new { group = b.Group, tex = b.Tex, rgba = b.Rgba, fullbright = b.Fullbright, nv = b.P.Count / 3, ni = b.I.Count, offset = off });
                off += (b.P.Count + b.N.Count + b.T.Count + b.I.Count) * 4L;
            }
        File.WriteAllText(Path.Combine(dir, "mesh.json"), JsonSerializer.Serialize(new { batches = outMeta, stats, bakes = doc["bakes"].ToString(), me = new[] { me.X, me.Y, me.Z } }));
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
        static float[] Local(float[] pos, float[] rotDeg, float[] scale, Vector3? posOverride)
        {
            var q = Quaternion.CreateFromEulers(rotDeg[0] * MathF.PI / 180, rotDeg[1] * MathF.PI / 180, rotDeg[2] * MathF.PI / 180);
            var m = new float[16]; var sc = scale ?? new float[] { 1, 1, 1 };
            for (int i = 0; i < 3; i++)
            {
                var e = new Vector3(i == 0 ? 1 : 0, i == 1 ? 1 : 0, i == 2 ? 1 : 0) * q * sc[i];   // row i = image of basis i
                m[i * 4] = e.X; m[i * 4 + 1] = e.Y; m[i * 4 + 2] = e.Z;
            }
            var t = posOverride ?? new Vector3(pos[0], pos[1], pos[2]);
            m[12] = t.X; m[13] = t.Y; m[14] = t.Z; m[15] = 1;
            return m;
        }
        // world matrix of every bone and collision volume (fitted mesh) in the default skeleton
        public static Dictionary<string, float[]> World(Dictionary<string, Vector3> overrides)
        {
            var d = new Dictionary<string, float[]>();
            void Walk(JointBase j, float[] parent, bool isBone)
            {
                Vector3? ov = overrides.TryGetValue(j.name, out var o) ? o : null;
                // ponytail: a bone's own scale isn't propagated to children (SL bone scales are 1); ceiling = shape sliders
                var w = Mul(Local(j.pos, j.rot ?? new float[3], isBone ? null : j.scale, ov), parent);
                d[j.name] = w;
                if (j is Joint b)
                {
                    foreach (var cv in b.collision_volume ?? Array.Empty<CollisionVolume>()) Walk(cv, w, false);
                    foreach (var c in b.bone ?? Array.Empty<Joint>()) Walk(c, w, true);
                }
            }
            var id = new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
            Walk(LindenSkeleton.Load().bone, id, true);
            return d;
        }
    }

    // row-vector convention (as LibreMetaverse's MeshSkinData): [x y z w] * M
    static Vector3 Xform(float[] m, Vector3 v, float w) =>
        new(v.X * m[0] + v.Y * m[4] + v.Z * m[8] + w * m[12], v.X * m[1] + v.Y * m[5] + v.Z * m[9] + w * m[13], v.X * m[2] + v.Y * m[6] + v.Z * m[10] + w * m[14]);
}
