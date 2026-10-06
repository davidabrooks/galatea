// scene-mesher: `scene export` dump -> geometry batches for the Blender worker (no SL login; assets from the public CDN).
//   dotnet run -- --selftest | <export dir> [near_m=20] [avatar|scene|all] [cx,cy,cz r] [--anim=<uuid>] [--nav]
//   (scene prims only within r of c; --anim=a,b,.. poses her with the held frame of SL animations (priority-blended), e.g. the default sit)
//   Without --anim she is posed from the export's me.anims (the client's animation clock). Other avatars (export "avatars"
//   with agent_id, 2026-10-04+) get their own shape, bakes and animations, one group "avatar:<id8>" each (mesh.json "others").
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
    // a scene prim's linkset root: its parent when that parent is in the export, else itself
    static uint RootOf(OSDMap o, Dictionary<uint, OSDMap> byLocal) => byLocal.ContainsKey(o["parentid"].AsUInteger()) ? o["parentid"].AsUInteger() : o["localid"].AsUInteger();
    // per linkset root: bounding sphere (centre, radius) of its members' boxes, each taken as world_pos +- half its largest scale
    // ponytail: rotation ignored (a cube's extent bounds any rotated prim of that size only up to sqrt 3); ceiling = the
    // sphere can be a little loose, so an object can count as in range a few cm early
    static Dictionary<uint, (Vector3 c, float r)> LinksetSpheres(IEnumerable<OSDMap> scene, Dictionary<uint, OSDMap> byLocal)
    {
        var box = new Dictionary<uint, (Vector3 lo, Vector3 hi)>();
        foreach (var o in scene)
        {
            var p = o["world_pos"].AsVector3(); var sc = o["scale"].AsVector3(); var h = new Vector3(MathF.Max(sc.X, MathF.Max(sc.Y, sc.Z)) / 2);
            var k = RootOf(o, byLocal);
            box[k] = box.TryGetValue(k, out var b) ? (Vector3.Min(b.lo, p - h), Vector3.Max(b.hi, p + h)) : (p - h, p + h);
        }
        return box.ToDictionary(kv => kv.Key, kv => ((kv.Value.lo + kv.Value.hi) / 2, Vector3.Distance(kv.Value.lo, kv.Value.hi) / 2));
    }

    // ponytail: thresholds tuned by eye (0.24 / 0.06 / 0.03 of radius*2/distance), one LOD per linkset where SL picks per prim
    // other avatars' attachments by the wearer's distance from the focus (2026-10-05 Warehouse 21: ~50 avatars all at Highest
    // LOD = 20 M vertices, 5.4 GB peak, the mesher ran out of memory). Hers always Highest.
    // ponytail: by distance, not on-screen size; ceiling = a far avatar under a close-up `look at` gets a coarse outfit
    internal static DetailLevel AvatarLod(float d) => d switch { < 4f => DetailLevel.Highest, < 10f => DetailLevel.High, < 20f => DetailLevel.Medium, _ => DetailLevel.Low };
    // an avatar the export marks as still loading ("partial": some attachments missing, "bare": none yet, "unknown": no
    // appearance yet) is a stand-in; "complete" or no field (exports before 2026-10-05) renders as before
    // a big flat floor slab (two sides >= 8 m, thickness <= 1.5 m, lying flat) reaching within `reach` horizontally of the
    // focus and within 6 m of its height: kept even when its root is beyond --roots, so the floor doesn't end in sky at
    // ~30 m (Warehouse 21 courtyard, a skybox: the floor beyond read as pale sky). Only the slab itself, not its linkset.
    internal static bool GroundSlab(Vector3 pos, Quaternion rot, Vector3 scale, Vector3 focus, float reach)
    {
        var ax = new[] { (scale.X, new Vector3(1, 0, 0)), (scale.Y, new Vector3(0, 1, 0)), (scale.Z, new Vector3(0, 0, 1)) }.OrderBy(a => a.Item1).ToArray();
        if (ax[0].Item1 > 1.5f || ax[1].Item1 < 8f) return false;
        if (MathF.Abs((ax[0].Item2 * rot).Z) < 0.9f) return false;   // thin axis must point up: a floor, not a wall
        if (MathF.Abs(pos.Z - focus.Z) > 6f) return false;
        float half = MathF.Sqrt(ax[1].Item1 * ax[1].Item1 + ax[2].Item1 * ax[2].Item1) / 2;
        return MathF.Max(0, Vector2.Distance(new Vector2(pos.X, pos.Y), new Vector2(focus.X, focus.Y)) - half) <= reach;
    }

    internal static bool StandIn(string dressed) => dressed is "partial" or "bare" or "unknown";
    static DetailLevel FarLod(float r, float d) => (r * 2f / MathF.Max(d, 1f)) switch { >= 0.24f => DetailLevel.High, >= 0.06f => DetailLevel.Medium, _ => DetailLevel.Low };

    // drop the rigged-mesh position overrides of joints whose position an animation drives (the SL pose blend wins)
    static void PoseOverrides(Dictionary<string, Vector3> overrides, IEnumerable<string> positionAnimated)
    {
        foreach (var j in positionAnimated) overrides.Remove(j);
    }

    // Some mesh bodies ship inverse_bind_matrix / bind_shape_matrix factored for a non-1x bind skeleton
    // (IB diag ~0.1 with BS~25, or IB diag ~100 with BS~0.025 — same BS*IB product). LLSkinningUtil still does
    // IB * jointWorld on the viewer's 1x joints, so those meshes pancake (Scentual90 2026-10-04). Retarget only those
    // two factorizations: IB' = inv(default 1x World), BS' = BS * s. Do NOT retarget s≈1 (Galatea) or s≈0 (degenerate
    // IB on some Bento heads — BS T_y~175, IB diag all zero; multiplying BS by 0 collapsed them to the feet and blew
    // the avatar height to ~2.7 m). Study-only vs Firestorm LLSkinningUtil.
    static float InvBindScale(MeshSkinData sk)
    {
        int i = Array.IndexOf(sk.JointNames, "mPelvis");
        if (i < 0 || sk.InverseBindMatrices.Length < (i + 1) * 16) return 1f;
        var m = sk.InverseBindMatrices; int o = i * 16;
        return (MathF.Abs(m[o]) + MathF.Abs(m[o + 5]) + MathF.Abs(m[o + 10])) / 3f;
    }
    static bool NeedsSkinRetarget(float s) => (s > 0.05f && s < 0.3f) || (s > 3f && s < 300f);  // ~0.1 or ~100 only
    static void SkinRetarget(MeshSkinData sk, Dictionary<string, float[]> bindWorld, out float[] bs, out float[][] ib)
    {
        bs = sk.BindShapeMatrix; ib = null;
        float s = InvBindScale(sk);
        if (!NeedsSkinRetarget(s)) return;
        bs = (float[])sk.BindShapeMatrix.Clone();
        for (int k = 0; k < 12; k++) bs[k] *= s;
        bs[12] *= s; bs[13] *= s; bs[14] *= s;
        ib = new float[sk.JointNames.Length][];
        for (int j = 0; j < sk.JointNames.Length; j++)
            ib[j] = bindWorld.TryGetValue(sk.JointNames[j], out var w) ? Skeleton.Invert(w) : sk.InverseBindMatrices[(j * 16)..(j * 16 + 16)];
    }

    // collision volumes take their bone's shape-slider scale: in the SL viewer a skeletal param's bone scale delta also goes
    // to that bone's collision-volume children, times each volume's default scale (LLAvatarJointCollisionVolume inherits
    // scale; reimplemented from the documented LLPolySkeletalDistortion behaviour). LibreMetaverse's ComputeBoneTransforms
    // scales the bones only, so fitted mesh weighted to the volumes (L_CLAVICLE, L_UPPER_ARM, CHEST, NECK...) missed the
    // shape's broadening: David's shoulders came out narrow and sloped (2026-10-04). ponytail: param sex not checked (as
    // before); ceiling = a female-only skeletal param on a male shape would count
    static void InheritVolumeScale(Dictionary<string, BoneTransform> shape, IReadOnlyDictionary<int, float> vp)
    {
        var cvs = new Dictionary<string, CollisionVolume[]>();
        void Walk(Joint j) { if (j.collision_volume?.Length > 0) cvs[j.name] = j.collision_volume; foreach (var c in j.bone ?? Array.Empty<Joint>()) Walk(c); }
        Walk(LindenSkeleton.Load().bone);
        foreach (var p in VisualParams.Params.Values.Where(p => p.SkeletalDistortions != null))
        {
            float w = Math.Max(p.MinValue, Math.Min(p.MaxValue, vp.TryGetValue(p.ParamID, out var v) ? v : p.DefaultValue));
            foreach (var b in p.SkeletalDistortions)
                foreach (var cv in cvs.GetValueOrDefault(b.BoneName) ?? Array.Empty<CollisionVolume>())
                    if (shape.TryGetValue(cv.name, out var t) && cv.scale is { Length: >= 3 } s)
                    { t.Scale += new Vector3(s[0] * b.ScaleDeformation.X, s[1] * b.ScaleDeformation.Y, s[2] * b.ScaleDeformation.Z) * w; shape[cv.name] = t; }
        }
    }

    static readonly UUID Transparent = new("8dcd4a48-2d37-4909-9f78-f7a9eb4ef903");

    sealed class Batch { public string Group, Tex; public float[] Rgba; public bool Fullbright; public Dictionary<string, object> Mat; public List<float> P = new(), N = new(), T = new(); public List<uint> I = new(); }
    static readonly string[] AlphaModes = { "none", "blend", "mask", "emissive" };   // LegacyMaterial DiffuseAlphaMode

    // CDN assets are immutable per UUID: keep them on disk (SCENE_MESHER_CACHE, default ~/.cache/scene-mesher; "off" = none).
    // Warehouse 21 (2026-10-05): ~6000 mesh/sculpt/material assets took 15 s to fetch on every look.
    static readonly string CacheDir = Environment.GetEnvironmentVariable("SCENE_MESHER_CACHE") is { Length: > 0 } c
        ? (c == "off" ? null : c) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "scene-mesher");
    internal static string CachePath(string root, string kind, UUID id) =>
        root == null || id == UUID.Zero || kind.IndexOfAny(new[] { '/', '\\', '.' }) >= 0 ? null : Path.Combine(root, kind, id.ToString()[..2], id.ToString());
    static int cacheHits, cacheMisses;
    static async Task<byte[]> Get(string kind, UUID id)
    {
        var path = CachePath(CacheDir, kind, id);
        if (path != null && File.Exists(path)) { try { var b = await File.ReadAllBytesAsync(path); if (b.Length > 0) { Interlocked.Increment(ref cacheHits); return b; } } catch { } }
        byte[] data;
        try { data = await Http.GetByteArrayAsync($"{Cdn}?{kind}_id={id}"); } catch { return null; }
        Interlocked.Increment(ref cacheMisses);
        if (path != null && data is { Length: > 0 })
            try { Directory.CreateDirectory(Path.GetDirectoryName(path)); var tmp = path + "." + Guid.NewGuid().ToString("N")[..8]; await File.WriteAllBytesAsync(tmp, data); File.Move(tmp, path, true); } catch { }
        return data;
    }

    static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--selftest")  // default skeleton sanity: pelvis ~1.07 m, head ~1.75 m, arms out (T-pose)
        {
            var w = Skeleton.World(new());
            float Z(string j) => w[j][14];
            bool ok = Math.Abs(Z("mPelvis") - 1.067f) < 0.01f && Z("mHead") > 1.6f && Z("mHead") < 1.9f && Math.Abs(w["mWristLeft"][13]) > 0.5f
                      && Xform(Skeleton.Mul(new float[] { 1,0,0,0, 0,1,0,0, 0,0,1,0, 1,2,3,1 }, new float[] { 2,0,0,0, 0,2,0,0, 0,0,2,0, 0,0,0,1 }), new Vector3(1, 1, 1), 1) == new Vector3(4, 6, 8);
            var lookD = Vector3.Normalize(new Vector3(1, 0.5f, 0.3f)); var lookGot = new Vector3(1, 0, 0) * LookRot(lookD).Value;
            ok &= Vector3.Distance(lookGot, lookD) < 0.01f;   // look-at: forward turned onto the target direction
            // linkset sphere: a root at x=10 and a child at x=14 (1 m prims) -> one sphere covering both, centre x=12
            OSDMap P(uint id, uint par, float x) => new OSDMap { ["localid"] = OSD.FromUInteger(id), ["parentid"] = OSD.FromUInteger(par), ["world_pos"] = OSD.FromVector3(new Vector3(x, 0, 0)), ["scale"] = OSD.FromVector3(Vector3.One) };
            var ls = new Dictionary<uint, OSDMap> { [1] = P(1, 0, 10), [2] = P(2, 1, 14) }; var sp = LinksetSpheres(ls.Values, ls);
            ok &= sp.Count == 1 && MathF.Abs(sp[1].c.X - 12) < 1e-3f && sp[1].r > 2.5f && RootOf(ls[2], ls) == 1;
            // boots regression (2026-10-04): an mPelvis override at 10x scale must not move a pelvis an animation drives,
            // while an un-animated joint keeps its override
            var ovr = new Dictionary<string, Vector3> { ["mPelvis"] = new(0, 0, 10.67f), ["mKneeLeft"] = new(-0.001f, -0.046f, -0.491f) };
            PoseOverrides(ovr, new[] { "mPelvis" });
            var wb = Skeleton.World(ovr, null, null, new Vector3(0, 0.1f, 0.01f));
            ok &= !ovr.ContainsKey("mPelvis") && ovr.ContainsKey("mKneeLeft") && MathF.Abs(wb["mPelvis"][14] - 1.077f) < 0.01f && wb["mHead"][14] < 2f;
            // skin retarget: IB scale 0.1 -> BS scaled by 0.1, IB' = inv(World); rest pose v*BS'*IB'*W = v*(BS*0.1)
            {
                var bw = Skeleton.World(new());
                var sk = new MeshSkinData {
                    JointNames = new[] { "mPelvis" },
                    BindShapeMatrix = new float[] { 25,0,0,0, 0,25,0,0, 0,0,25,0, 0,0,10,1 },
                    InverseBindMatrices = new float[] { 0.1f,0,0,0, 0,0.1f,0,0, 0,0,0.1f,0, 0,0,-1.067f,1 }
                };
                SkinRetarget(sk, bw, out var bsR, out var ibR);
                ok &= ibR != null && MathF.Abs(bsR[0] - 2.5f) < 1e-4f && MathF.Abs(bsR[14] - 1f) < 1e-4f;
                var rest = Xform(Skeleton.Mul(Skeleton.Mul(bsR, ibR[0]), bw["mPelvis"]), Vector3.Zero, 1);
                ok &= MathF.Abs(rest.Z - 1f) < 0.02f;   // BS translation 10*0.1, not floating at z=10
                // degenerate IB (diag 0) must NOT retarget — BS*=0 would wipe the mesh (Scentual Bento heads)
                var bad = new MeshSkinData {
                    JointNames = new[] { "mPelvis" },
                    BindShapeMatrix = new float[] { 20,0,0,0, 0,25,0,0, 0,0,24,0, 0,175,1,1 },
                    InverseBindMatrices = new float[] { 0,0,0,0, 0,0,0,0, 0,0,0,0, 0,0,-1.067f,1 }
                };
                SkinRetarget(bad, bw, out var bsB, out var ibB);
                ok &= ibB == null && bsB[0] == 20f && bsB[13] == 175f;
            }
            var fz = new Vector3(100, 100, 2004);
            ok &= GroundSlab(new Vector3(150, 100, 2003), Quaternion.Identity, new Vector3(64, 64, 0.5f), fz, 30)          // 50 m away, 45 m half-diagonal
                  && !GroundSlab(new Vector3(150, 100, 2003), Quaternion.Identity, new Vector3(64, 64, 4f), fz, 30)          // too thick
                  && !GroundSlab(new Vector3(150, 100, 2003), Quaternion.CreateFromAxisAngle(1, 0, 0, MathF.PI / 2), new Vector3(64, 64, 0.5f), fz, 30)  // a wall
                  && !GroundSlab(new Vector3(200, 100, 2003), Quaternion.Identity, new Vector3(64, 64, 0.5f), fz, 30)        // too far
                  && !GroundSlab(new Vector3(150, 100, 1990), Quaternion.Identity, new Vector3(64, 64, 0.5f), fz, 30)        // other level
                  && GroundSlab(new Vector3(150, 100, 2003), Quaternion.CreateFromAxisAngle(0, 1, 0, MathF.PI / 2), new Vector3(0.5f, 64, 64), fz, 30);  // thin X turned up
            ok &= StandIn("partial") && StandIn("bare") && StandIn("unknown") && !StandIn("complete") && !StandIn(null);
            ok &= CachePath("/c", "mesh", new UUID("0123abcd-0000-0000-0000-000000000001")) == Path.Combine("/c", "mesh", "01", "0123abcd-0000-0000-0000-000000000001")
                  && CachePath(null, "mesh", UUID.Random()) == null && CachePath("/c", "../x", UUID.Random()) == null && CachePath("/c", "mesh", UUID.Zero) == null;
            ok &= AvatarLod(2) == DetailLevel.Highest && AvatarLod(6) == DetailLevel.High && AvatarLod(15) == DetailLevel.Medium && AvatarLod(35) == DetailLevel.Low;
            ok &= FarLod(5, 50) == DetailLevel.Medium && FarLod(0.5f, 90) == DetailLevel.Low && FarLod(20, 40) == DetailLevel.High;
            // shape: Thickness (34) scales mCollarLeft's Y by 0.2 per unit, so its volume L_CLAVICLE (default Y 0.14) widens by 0.028
            var lad0 = LindenAvatarDefinition.Load(Path.Combine(AppContext.BaseDirectory, "linden", "character", "avatar_lad.xml"));
            float Clav(float th) { var vp0 = new Dictionary<int, float> { [34] = th }; var sh = lad0.ComputeBoneTransforms(vp0); InheritVolumeScale(sh, vp0); return sh["L_CLAVICLE"].Scale.Y; }
            float dClav = Clav(1) - Clav(0); ok &= MathF.Abs(dClav - 0.028f) < 1e-4f;
            Console.WriteLine(ok ? "selftest ok" : $"selftest FAILED pelvis={Z("mPelvis")} head={Z("mHead")} wristY={w["mWristLeft"][13]} look={lookGot} clavicle={dClav}");
            return ok ? 0 : 1;
        }
        string animId = args.FirstOrDefault(a => a.StartsWith("--anim="))?[7..]; args = args.Where(a => !a.StartsWith("--anim=")).ToArray();
        // --look=x,y,z (region coordinates): turn her head toward that point (look at)
        var lookArg = args.FirstOrDefault(a => a.StartsWith("--look="))?[7..].Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        args = args.Where(a => !a.StartsWith("--look=")).ToArray();
        // --far=M: scene prims beyond M m of the focus are backdrop (group "far"): lowest LOD, no normal/spec maps, and
        // prims under 1 m skipped (David 2026-10-04: more background without slowing the render much) ponytail: far prims < 1 m vanish; ceiling = no small far detail (railings, signs)
        float farR = float.Parse(args.FirstOrDefault(a => a.StartsWith("--far="))?[6..] ?? "1e9", System.Globalization.CultureInfo.InvariantCulture);
        args = args.Where(a => !a.StartsWith("--far=")).ToArray();
        // --roots=M (2026-10-04): only linksets whose root is within M m of the focus, the `scene export` radius rule, so a
        // near-only look on a 96 m export meshes what a 32 m export holds (big linksets rooted far away pulled 800+ textures)
        float rootsR = float.Parse(args.FirstOrDefault(a => a.StartsWith("--roots="))?[8..] ?? "1e9", System.Globalization.CultureInfo.InvariantCulture);
        args = args.Where(a => !a.StartsWith("--roots=")).ToArray();
        // --nav (2026-10-04, walking planner scripts/nav_map.py): one group "nav:<localid>" per scene prim and invisible faces
        // kept (an invisible face still collides). ponytail: visual mesh as the collision shape; ceiling = mesh whose physics
        // shape differs from what it shows (a hull over a doorway, a phantom-looking curtain that is solid)
        bool nav = args.Contains("--nav"); args = args.Where(a => a != "--nav").ToArray();
        if (args.Length < 1 || !File.Exists(Path.Combine(args[0], "scene.json"))) { Console.Error.WriteLine("usage: scene-mesher <export dir> [near_m]"); return 2; }
        string dir = args[0]; float near = args.Length > 1 ? float.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 20;
        var doc = (OSDMap)OSDParser.DeserializeJson(File.ReadAllText(Path.Combine(dir, "scene.json")));
        var me = ((OSDMap)doc["me"])["pos"].AsVector3();
        // avatar-local: undo her rotation around her agent position (z moves into the mesh frame with agentOff in PoseAvatar)
        Vector3? lookAt = null; var agentOff = new ConcurrentDictionary<string, float>();  // per avatar owner: mesh z 0 relative to agent z
        if (lookArg is { Length: 3 })
            lookAt = (new Vector3(lookArg[0], lookArg[1], lookArg[2]) - me) * Quaternion.Conjugate(((OSDMap)doc["me"])["rot"].AsQuaternion());
        string only = args.Length > 2 ? args[2] : "all";
        Vector3 focus = me; float focusR = 1e9f;
        if (args.Length > 4) { var c = args[3].Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray(); focus = new Vector3(c[0], c[1], c[2]); focusR = float.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture); }
        var clock = System.Diagnostics.Stopwatch.StartNew(); var phases = new List<string>();
        void Phase(string name) { phases.Add($"{name} {clock.ElapsedMilliseconds} ms"); clock.Restart(); }
        var mf = new MeshFoundry();
        var batches = new ConcurrentDictionary<string, Batch>();
        var stats = new ConcurrentDictionary<string, int>();
        // avatar attachments by owner: "me" (hers) or the wearer's agent id (other avatars, exports from 2026-10-04)
        var rigged = new ConcurrentBag<(string, Primitive, FacetedMesh)>(); var unrigged = new ConcurrentBag<(string, Primitive, FacetedMesh)>();
        static string Owner(OSDMap o) => o.ContainsKey("attached_to_me") ? "me" : o.ContainsKey("attached_to") ? o["attached_to"].AsString() : null;
        void Count(string k) => stats.AddOrUpdate(k, 1, (_, v) => v + 1);

        var byLocal = ((OSDArray)doc["prims"]).Cast<OSDMap>().ToDictionary(o => o["localid"].AsUInteger());
        var wearerAt = ((OSDArray)doc["avatars"]).Cast<OSDMap>().Where(av => av.ContainsKey("agent_id") && av.ContainsKey("pos"))
            .GroupBy(av => av["agent_id"].AsString()).ToDictionary(g => g.Key, g => g.First()["pos"].AsVector3());
        // avatars the client saw still loading (export "dressed" != complete, 2026-10-05+): drawn as a neutral stand-in, so
        // their attachments that did arrive aren't meshed (never a half-dressed body); older exports have no "dressed"
        var loading = ((OSDArray)doc["avatars"]).Cast<OSDMap>().Where(av => av.ContainsKey("agent_id") && StandIn(av.ContainsKey("dressed") ? av["dressed"].AsString() : null))
            .Select(av => av["agent_id"].AsString()).ToHashSet();
        // range culling per whole linkset (David 2026-10-04: per-prim culling left floating tree fragments): a linkset's
        // bounding sphere (member positions +- half their largest scale) is in if its nearest point is within focusR, and
        // "far" (screen-size LOD, see FarLod; skipped when the whole object is under ~1 m across) when its centre is beyond farR (by the
        // nearest point, big objects reaching inside 30 m pulled 800+ full-LOD textures)
        var sphere = LinksetSpheres(byLocal.Values.Where(o => Owner(o) == null), byLocal);
        float Nearest(OSDMap o) { var (c, r) = sphere[RootOf(o, byLocal)]; return MathF.Max(0, Vector3.Distance(c, focus) - r); }
        bool Far(OSDMap o) => Vector3.Distance(sphere[RootOf(o, byLocal)].c, focus) > farR;
        var prims = ((OSDArray)doc["prims"]).Cast<OSDMap>().Where(o => Owner(o) != null
            ? only != "scene" && !loading.Contains(Owner(o)) : only != "avatar" && Nearest(o) <= focusR && (!Far(o) || sphere[RootOf(o, byLocal)].r >= 0.85f)
              && (Vector3.Distance(byLocal[RootOf(o, byLocal)]["world_pos"].AsVector3(), focus) <= rootsR
                  || RootOf(o, byLocal) == o["localid"].AsUInteger() && GroundSlab(o["world_pos"].AsVector3(), o["world_rot"].AsQuaternion(), o["scale"].AsVector3(), focus, focusR))).ToList();
        var lights = new List<object>();
        // the export's TextureEntry JSON drops "face_number" for face 0, so LibreMetaverse would read face 0 as the default
        // face: restore it (the only per-face entry that can lack the key)
        foreach (var o in prims)
            if (o["textures"] is OSDArray tes)
                foreach (var f in tes.Skip(1).OfType<OSDMap>().Where(f => !f.ContainsKey("face_number"))) f["face_number"] = 0;
        // fetch each distinct mesh / sculpt asset once
        var assets = new ConcurrentDictionary<(string, UUID), byte[]>();
        await Parallel.ForEachAsync(prims.Select(Primitive.FromOSD).Where(p => p.Sculpt != null && p.Sculpt.SculptTexture != UUID.Zero)
                .Select(p => (p.Sculpt.Type == SculptType.Mesh ? "mesh" : "texture", p.Sculpt.SculptTexture)).Distinct(),
            new ParallelOptions { MaxDegreeOfParallelism = 24 }, async (k, _) => assets[k] = await Get(k.Item1, k.Item2));
        // PBR (GLTF) material assets, per face, from the RenderMaterial extra param the export records
        var pbr = new ConcurrentDictionary<UUID, AssetMaterial>();
        await Parallel.ForEachAsync(prims.Where(o => o.ContainsKey("render_materials")).SelectMany(o => ((OSDMap)o["render_materials"]).Values.Select(v => v.AsUUID())).Distinct(),
            async (id, _) => { var d = await Get("material", id); var m = d == null ? null : new AssetMaterial(id, d); if (m != null && m.Decode()) pbr[id] = m; });
        var legacy = doc.ContainsKey("materials") ? (OSDMap)doc["materials"] : new OSDMap();
        Console.WriteLine($"assets: {assets.Count(a => a.Value != null)}/{assets.Count} fetched ({cacheHits} from the disk cache), {pbr.Count} pbr, {legacy.Count} legacy materials");
        Phase("fetch");

        // one bad prim (odd mesh asset, malformed TE) must not kill a whole crowd render (2026-10-05 Warehouse 21: the look
        // failed with only the Parallel.ForEach frame visible): count it, keep the first error for the log, skip that prim
        int primErrors = 0; string firstPrimError = null;
        Parallel.ForEach(prims, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, o =>
        {
            try { PrimBody(o); }
            catch (Exception ex)
            {
                if (Interlocked.Increment(ref primErrors) == 1)
                    firstPrimError = $"prim {(o.ContainsKey("id") ? o["id"].AsUUID().ToString() : "?")} '{(o.ContainsKey("name") ? o["name"].AsString() : "")}': {ex.GetType().Name}: {ex.Message} @ {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}";
            }
        });
        Phase("prims");
        if (primErrors > 0) { stats["prim_error_skipped"] = primErrors; Console.Error.WriteLine($"warning: {primErrors} prim(s) skipped after errors; first: {firstPrimError}"); }
        void PrimBody(OSDMap o)
        {
            var p = Primitive.FromOSD(o);
            var owner = Owner(o); bool mine = owner != null;
            var pos = mine ? Vector3.Zero : o["world_pos"].AsVector3();
            var rot = mine ? Quaternion.Identity : o["world_rot"].AsQuaternion();
            // child prims: world rotation = root * local, recomputed (exports before 2026-10-03 22:30 PT stored local * root)
            if (!mine && byLocal.TryGetValue(p.ParentID, out var par) && !par.ContainsKey("attached_to_me")) rot = par["rotation"].AsQuaternion() * p.Rotation;
            bool close = mine || Vector3.Distance(pos, focusR < 1e9f ? focus : me) < near;   // full LOD near the focus (or her)
            bool far = !mine && Far(o);
            // backdrop LOD from the whole object's on-screen size (radius x LOD factor 2 / distance), like an SL viewer at its
            // default LOD factor; the lowest LOD of many mesh trees is a handful of loose leaf triangles (floating fragments)
            var farLod = far ? FarLod(sphere[RootOf(o, byLocal)].r, Vector3.Distance(sphere[RootOf(o, byLocal)].c, focus)) : DetailLevel.Low;
            // someone else's attachment: LOD by the wearer's distance (hers stay Highest)
            DetailLevel? avLod = owner != null && owner != "me" && wearerAt.TryGetValue(owner, out var wp0)
                ? AvatarLod(Vector3.Distance(wp0, focusR < 1e9f ? focus : me)) : null;
            FacetedMesh fm = null; float[] bind = null;
            try
            {
                if (p.Sculpt?.Type == SculptType.Mesh)
                {
                    if (assets.TryGetValue(("mesh", p.Sculpt.SculptTexture), out var data) && data != null)
                        fm = mf.GenerateFacetedMeshMesh(p, data, avLod ?? (far ? farLod : close ? DetailLevel.Highest : DetailLevel.High));
                    if (fm?.SkinData != null) bind = fm.SkinData.BindShapeMatrix;
                    Count(fm == null ? "mesh_failed" : bind != null ? "mesh_rigged" : "mesh");
                }
                else if (p.Sculpt != null && p.Sculpt.SculptTexture != UUID.Zero)
                {
                    if (assets.TryGetValue(("texture", p.Sculpt.SculptTexture), out var data) && data != null)
                    {
                        var tex = new AssetTexture(UUID.Zero, data);
                        if (tex.Decode()) fm = mf.GenerateFacetedSculptMesh(p, tex.Image, avLod ?? (far ? farLod : DetailLevel.High));
                    }
                    Count(fm == null ? "sculpt_failed" : "sculpt");
                }
                else { fm = mf.GenerateFacetedMesh(p, avLod ?? (far ? farLod : close ? DetailLevel.Highest : DetailLevel.High)); Count(fm == null ? "prim_failed" : "prim"); }
            }
            catch { Count("exception"); }
            if (fm == null) return;
            // SL point lights (projector textures ignored). A backdrop light whose radius can't reach within farR of the focus
            // is dropped: it only lights far scenery, and 48 such lamps cost ~5 s a view (David 2026-10-04: keep the render
            // near its old time). ponytail: far lamps don't light the backdrop; ceiling = distant buildings unlit at night
            if (!mine && p.Light is { Intensity: > 0 } li && (!far || Vector3.Distance(pos, focus) - li.Radius <= farR))
                lock (lights) lights.Add(new { pos = new[] { pos.X, pos.Y, pos.Z }, color = new[] { li.Color.R, li.Color.G, li.Color.B }, intensity = li.Intensity, radius = li.Radius, falloff = li.Falloff });
            if (!mine && bind != null) { Count("rigged_in_world_skipped"); return; }
            if (mine) { (bind != null ? rigged : unrigged).Add((owner, p, fm)); return; }  // posed after all joint overrides are known
            Emit(p, fm, nav ? $"nav:{p.LocalID}" : far ? "far" : "scene", null, null, pos, rot, o);
        }


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

        void Emit(Primitive p, FacetedMesh fm, string group, string bakePrefix, Func<Vertex, VertexWeight?, (Vector3, Vector3)> pose, Vector3 pos, Quaternion rot, OSDMap o)
        {
            foreach (var f in fm.Faces)
            {
                var te = f.TextureFace ?? p.Textures?.DefaultTexture;
                // Alpha HUD / mesh-body hide: face colour A=0 (OSD JSON writes 0 as null → AsColor4 A=0). Transparent TE too.
                if (te == null || f.Indices.Count == 0) continue;
                if (!nav && te.RGBA.A < 0.01f) { Count("face_alpha0_skipped"); continue; }
                if (!nav && te.TextureID == Transparent) { Count("face_transparent_tex_skipped"); continue; }
                var verts = f.Vertices.ToList(); var wts = f.Weights;
                mf.TransformTexCoords(verts, Vector3.Zero, te, p.Scale);
                string tex = BakeOf.TryGetValue(te.TextureID, out var bake) ? "bake:" + bakePrefix + bake : te.TextureID.ToString();
                var rgba = new[] { te.RGBA.R, te.RGBA.G, te.RGBA.B, te.RGBA.A };
                var mat = Mat(te, f.ID, o, ref tex, ref rgba);
                if (group == "far") foreach (var k in new[] { "normal", "spec", "mr", "emissive_tex" }) mat?.Remove(k);
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
                        // mesh-asset UVs are GL-style (v up from the image's bottom row; the SL viewer decodes J2C bottom row
                        // first) and Blender samples the same way, so they pass through. Flipping them turned every mesh texture upside
                        // down (head bake rows on the wrong features, the jeans' pocket art on her hip). Generated prim/sculpt UVs keep it.
                        b.T.Add(v.TexCoord.X); b.T.Add(p.Sculpt?.Type == SculptType.Mesh ? v.TexCoord.Y : 1f - v.TexCoord.Y);
                    }
                    foreach (var i in f.Indices) b.I.Add(baseIdx + i);
                }
            }
        }

        // Rigged attachments: bind pose = SL's default (T-)pose. Per vertex: sum_k w_k * v * BindShape * InvBind_jk * JointWorld_jk
        // (row vectors), JointWorld from LibreMetaverse's copy of avatar_skeleton.xml, with joint position overrides
        // from alt_inverse_bind_matrix translations, as the SL viewer does. Reimplemented from the documented behaviour.
        // A mesh with lock_scale_if_joint_position also pins the scale of each joint it moves to the skeleton default,
        // undoing the shape sliders' bone scales there.
        // one avatar: shape (visual params), joint overrides, animation pose, then its attachments in avatar-local space
        // (feet near z=0, facing +x; render_mesh.py stands each group on the floor under the avatar's position)
        var lad = LindenAvatarDefinition.Load(Path.Combine(AppContext.BaseDirectory, "linden", "character", "avatar_lad.xml"));
        async Task<Dictionary<string, (Vector3 Pos, Quaternion Rot)>> PoseAvatar(string owner, string group, string bakePrefix, OSDMap vpMap, string animId)
        {
            Dictionary<string, (Vector3 Pos, Quaternion Rot)> frames;
            var overrides = new Dictionary<string, Vector3>(); var lockScale = new HashSet<string>();
            // joint-position overrides: highest mesh-asset UUID wins per joint (SL viewer conflict rule)
            var ovrFrom = new Dictionary<string, UUID>();
            foreach (var (_, p0, fm) in rigged.Where(x => x.Item1 == owner))
            {
                var sk = fm.SkinData;
                if (sk.AltInverseBindMatrices.Length != sk.JointNames.Length * 16) continue;   // SL ignores a mismatched list
                var meshId = p0.Sculpt?.SculptTexture ?? UUID.Zero;
                for (int j = 0; j < sk.JointNames.Length; j++)
                {
                    var jn = sk.JointNames[j];
                    var pos = new Vector3(sk.AltInverseBindMatrices[j * 16 + 12], sk.AltInverseBindMatrices[j * 16 + 13], sk.AltInverseBindMatrices[j * 16 + 14]);
                    // 10x-upload artifact: mPelvis as (0,0,10.67) = 10 * default pelvis height. On a 1x skeleton it lifts
                    // the avatar ~9.6 m (David's boots; Scentual90). Real pelvis locals stay near 0 — skip the giant ones.
                    if (jn == "mPelvis" && pos.Z > 5f) continue;
                    if (!ovrFrom.TryGetValue(jn, out var prev) || meshId.CompareTo(prev) > 0)
                    { overrides[jn] = pos; ovrFrom[jn] = meshId; }
                    if (sk.LockScaleIfJointPosition) lockScale.Add(jn);
                }
            }
            // her shape: visual params -> bone/collision-volume position+scale (LibreMetaverse's port of LLPolySkeletalDistortion)
            var vp = vpMap == null ? new Dictionary<int, float>() : vpMap.ToDictionary(kv => int.Parse(kv.Key), kv => (float)kv.Value.AsReal());
            var shape = vp.Count > 0 ? lad.ComputeBoneTransforms(vp) : null;
            if (shape != null) InheritVolumeScale(shape, vp);
            // animations (her playing set, e.g. AO stand + head expression): per joint the highest priority wins, ties -> later
            Dictionary<string, Quaternion> anim = null; Dictionary<string, Vector3> animPos = null; Vector3 pelvisOff = Vector3.Zero;
            var prio = new Dictionary<string, (int r, int p)>();
            if (!string.IsNullOrEmpty(animId))
            {
                anim = new(); animPos = new();
                foreach (var spec in animId.Split(','))
                {
                    // "uuid@seconds" = that far into playback (anims_at.py reads it off the client log); bare uuid = old hold frame
                    var id = spec.Split('@')[0]; float? played = spec.Contains('@') ? float.Parse(spec.Split('@')[1], System.Globalization.CultureInfo.InvariantCulture) : null;
                    var data = await Get("animatn", new UUID(id));
                    if (data == null) { Console.Error.WriteLine($"animation {id}: fetch failed, skipped"); continue; }
                    var a = new BinBVHAnimationReader(data);
                    // the frame SL shows `played` s in: a loop wraps between its in and out points after the first pass, else it
                    // holds the last frame; keys interpolate (positions lerp, rotations slerp). Without a time: mid-loop for loops
                    // (sits: in = out = end, so the end pose), else the last frame. ponytail: no ease-in/out weights
                    float tHold = played is float pt
                        ? (a.Loop && a.OutPoint > a.InPoint && pt > a.OutPoint ? a.InPoint + (pt - a.InPoint) % (a.OutPoint - a.InPoint) : Math.Min(pt, a.Length))
                        : a.Loop ? (a.InPoint + a.OutPoint) / 2 : a.Length;
                    Vector3 At(binBVHJointKey[] k, bool rot)
                    {
                        int i = Array.FindIndex(k, x => x.time >= tHold);
                        if (i <= 0) return k[i < 0 ? k.Length - 1 : 0].key_element;
                        var (k0, k1) = (k[i - 1], k[i]); float u = k1.time > k0.time ? (tHold - k0.time) / (k1.time - k0.time) : 1;
                        if (!rot) return Vector3.Lerp(k0.key_element, k1.key_element, u);
                        var q = Quaternion.Slerp(Q(k0.key_element), Q(k1.key_element), u); if (q.W < 0) q = -q;
                        return new Vector3(q.X, q.Y, q.Z);
                    }
                    static Quaternion Q(Vector3 e) => new(e.X, e.Y, e.Z, MathF.Sqrt(Math.Max(0, 1 - e.LengthSquared())));
                    // LibreMetaverse decodes positions over -0.5..1.5; the SL format range is -5..5 (LL_MAX_PELVIS_OFFSET)
                    Vector3 Pos(binBVHJointKey[] k) => (At(k, false) + new Vector3(0.5f, 0.5f, 0.5f)) * 5f - new Vector3(5, 5, 5);
                    foreach (var j in a.joints)
                    {
                        int jp = j.Priority >= 0 ? j.Priority : a.Priority;
                        var cur = prio.TryGetValue(j.Name, out var c) ? c : (r: -1, p: -1);
                        if (j.rotationkeys.Length > 0 && jp >= cur.r)
                        { anim[j.Name] = Q(At(j.rotationkeys, true)); cur.r = jp; }
                        if (j.positionkeys.Length > 0 && jp >= cur.p)
                        { if (j.Name == "mPelvis") pelvisOff = Pos(j.positionkeys); else animPos[j.Name] = Pos(j.positionkeys); cur.p = jp; }
                        prio[j.Name] = cur;
                    }
                    Console.WriteLine($"animation {id}: priority {a.Priority}, {a.joints.Length} joints, frame {tHold:F2}/{a.Length:F2} s");
                }
                Console.WriteLine($"pose: {anim.Count} rotated joints, {animPos.Count} moved, pelvis offset {pelvisOff}");
            }
            // a position override on a joint the animations move is overwritten every frame by the SL viewer's pose blend
            // (LLPose: the blended animation position replaces the joint's position), so it never shows. For the pelvis that
            // matters: David's boots carry an mPelvis override of (0,0,10.67), the 1.067 m default written at the boots' 10x
            // authoring scale; applied, it lifted his whole skeleton 9.6 m in mesh space (agent_off -10.48 hid it)
            PoseOverrides(overrides, prio.Where(kv => kv.Value.p >= 0).Select(kv => kv.Key));
            // LL places the pelvis from the agent position: pelvis = agent z - (bodysize.z / 2 - pelvis-to-foot) (+ hover), with
            // bodysize from the unanimated skeleton (reimplemented from LLVOAvatar::computeBodySize / updateCharacter). In this
            // mesh frame that is: mesh z 0 at agent z - bodysize/2 - rest foot z. ponytail: hover (AppearanceHover) not exported = 0
            var rest = new Dictionary<string, (Vector3 Pos, Quaternion Rot)>(); Skeleton.World(overrides, shape, null, default, rest, null, lockScale);
            float Rz(string j) => rest[j].Pos.Z;
            agentOff[owner] = -0.5f * (Rz("mHead") - Rz("mFootLeft") + MathF.Sqrt(2) * (Rz("mSkull") - Rz("mHead"))) - Rz("mFootLeft");
            frames = new Dictionary<string, (Vector3 Pos, Quaternion Rot)>();
            var world = Skeleton.World(overrides, shape, anim, pelvisOff, frames, animPos, lockScale);
            if (owner == "me" && lookAt is Vector3 lt0 && lt0 - new Vector3(0, 0, agentOff[owner]) is var lt && LookRot(lt - frames["mHead"].Pos) is Quaternion q)
            {
                // head look-at, reimplemented from the LL viewer's LLHeadRotMotion: the turn toward the target relative to the
                // body, limited to 72 deg; the torso takes 35 % of it and neck and head share the rest. LL runs it at
                // MEDIUM priority (1), under most AOs (hers animate torso/neck/head at 3-4), which would leave her staring
                // ahead; so above priority 1 the turn is added on top of the animated rotation instead of replacing it.
                // ponytail: shares split as powers of one rotation (exact for a pure turn); ceiling = slight error when
                // she also looks up/down a lot
                float Off() => MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(new Vector3(1, 0, 0) * frames["mHead"].Rot), Vector3.Normalize(lt - frames["mHead"].Pos)), -1, 1)) * 180 / MathF.PI;
                float before = Off(); anim ??= new();
                foreach (var (j, share) in new[] { ("mTorso", 0.35f), ("mNeck", 0.325f), ("mHead", 0.325f) })
                {
                    var t = Quaternion.Slerp(Quaternion.Identity, q, share);
                    anim[j] = prio.TryGetValue(j, out var pr) && pr.r > 1 && anim.TryGetValue(j, out var ar) ? ar * t : t;
                }
                frames = new(); world = Skeleton.World(overrides, shape, anim, pelvisOff, frames, animPos, lockScale);
                Console.WriteLine($"look-at: turn {2 * MathF.Acos(Math.Min(1, Math.Abs(q.W))) * 180 / MathF.PI:F0} deg; head off target {before:F0} -> {Off():F0} deg");
            }
            // non-rigged attachments: root prim sits at its attach point (avatar_lad.xml offset/rotation) on that point's joint
            var points = lad.AttachmentPoints.ToDictionary(ap => ap.Id);
            foreach (var (_, p, fm) in unrigged.Where(x => x.Item1 == owner))
            {
                var o = byLocal[p.LocalID]; var root = o; Vector3 lp = p.Position; var lr = p.Rotation;
                if (byLocal.TryGetValue(p.ParentID, out var r0)) { root = r0; var rr = r0["rotation"].AsQuaternion(); lp = r0["position"].AsVector3() + p.Position * rr; lr = rr * p.Rotation; }
                int apId = root["attach_point"].AsInteger();
                if (apId >= 31 && apId <= 38 || !points.TryGetValue(apId, out var ap) || !frames.TryGetValue(ap.Joint, out var jf)) { Count("attachment_hud_or_unknown_skipped"); continue; }
                var apRot = Quaternion.CreateFromEulers(ap.Rotation.X * MathF.PI / 180, ap.Rotation.Y * MathF.PI / 180, ap.Rotation.Z * MathF.PI / 180);
                var wr = jf.Rot * apRot * lr; var wp = jf.Pos + (ap.Position + lp * apRot) * jf.Rot;
                Count("attachment_unrigged"); Emit(p, fm, group, bakePrefix, null, wp, wr, o);
            }
            var bindWorld = Skeleton.World(new());   // default 1x bind for SkinRetarget (upload-time skeleton)
            foreach (var (_, p, fm) in rigged.Where(x => x.Item1 == owner))
            {
                var sk = fm.SkinData; SkinRetarget(sk, bindWorld, out var bs, out var ib);
                var jm = new float[sk.JointNames.Length][];
                for (int j = 0; j < jm.Length; j++)
                {
                    if (!world.TryGetValue(sk.JointNames[j], out var w)) { jm[j] = null; continue; }
                    var inv = ib != null ? ib[j] : sk.InverseBindMatrices[(j * 16)..(j * 16 + 16)];
                    jm[j] = Skeleton.Mul(Skeleton.Mul(bs, inv), w);
                }
                if (jm.Any(m => m == null)) Count("rigged_unknown_joint");
                if (ib != null) Count("skin_retarget_1x");
                Emit(p, fm, group, bakePrefix, (v, w) =>
                {
                    if (w == null) return (Xform(bs, v.Position, 1), Vector3.Normalize(Xform(bs, v.Normal, 0)));
                    var x = w.Value; Vector3 ps = Vector3.Zero, ns = Vector3.Zero; float tw = 0;
                    foreach (var (j, wt) in new[] { (x.Joint0, x.Weight0), (x.Joint1, x.Weight1), (x.Joint2, x.Weight2), (x.Joint3, x.Weight3) })
                    {
                        if (wt <= 0 || j < 0 || j >= jm.Length || jm[j] == null) continue;
                        ps += Xform(jm[j], v.Position, 1) * wt; ns += Xform(jm[j], v.Normal, 0) * wt; tw += wt;
                    }
                    return tw > 0 ? (ps / tw, Vector3.Normalize(ns)) : (Xform(bs, v.Position, 1), v.Normal);
                }, Vector3.Zero, Quaternion.Identity, byLocal[p.LocalID]);
            }
            return frames;
        }
        // rotation taking the avatar's forward (+X) to `d` (yaw, then pitch), capped at LL's HEAD_ROTATION_CONSTRAINT (72 deg);
        // null when the target is within LL's MIN_HEAD_LOOKAT_DISTANCE (0.3 m)
        static Quaternion? LookRot(Vector3 d)
        {
            if (d.Length() < 0.3f) return null;
            d = Vector3.Normalize(d);
            var q = Quaternion.CreateFromAxisAngle(0, 0, 1, MathF.Atan2(d.Y, d.X)) * Quaternion.CreateFromAxisAngle(0, 1, 0, -MathF.Asin(Math.Clamp(d.Z, -1, 1)));
            float ang = 2 * MathF.Acos(Math.Min(1, Math.Abs(q.W))), max = MathF.PI / 2 * 0.8f;
            return ang > max ? Quaternion.Slerp(Quaternion.Identity, q, max / ang) : q;
        }
        // the main bone chain as segments (avatar space, posed): render_mesh.py tells a garment's inside from its outside by
        // the nearest bone (bones are inside the body), to drop double-sided clothing's linings (see inner_layers there)
        string[][] Chains = {
            new[] { "mPelvis", "mTorso", "mChest", "mNeck", "mHead", "mSkull" },
            new[] { "mChest", "mCollarLeft", "mShoulderLeft", "mElbowLeft", "mWristLeft", "mHandMiddle1Left" },
            new[] { "mChest", "mCollarRight", "mShoulderRight", "mElbowRight", "mWristRight", "mHandMiddle1Right" },
            new[] { "mPelvis", "mHipLeft", "mKneeLeft", "mAnkleLeft", "mFootLeft", "mToeLeft" },
            new[] { "mPelvis", "mHipRight", "mKneeRight", "mAnkleRight", "mFootRight", "mToeRight" } };
        float[][] Bones(Dictionary<string, (Vector3 Pos, Quaternion Rot)> f) =>
            Chains.SelectMany(c => c.Zip(c.Skip(1)).Where(p => f.ContainsKey(p.First) && f.ContainsKey(p.Second))
                .Select(p => new[] { f[p.First].Pos.X, f[p.First].Pos.Y, f[p.First].Pos.Z, f[p.Second].Pos.X, f[p.Second].Pos.Y, f[p.Second].Pos.Z })).ToArray();
        static float Yaw(Quaternion q) => MathF.Atan2(2 * (q.W * q.Z + q.X * q.Y), 1 - 2 * (q.Y * q.Y + q.Z * q.Z)) * 180 / MathF.PI;
        var meMap = (OSDMap)doc["me"];
        animId ??= meMap.ContainsKey("anims") ? meMap["anims"].AsString() : null;   // the client's own animation clock (2026-10-04+)
        var frames = only == "scene" ? null : await PoseAvatar("me", "avatar", "", doc.ContainsKey("visual_params") ? (OSDMap)doc["visual_params"] : null, animId);
        var others = new List<object>();
        var avList = only == "scene" || !doc.ContainsKey("avatars") ? new List<OSDMap>() : ((OSDArray)doc["avatars"]).Cast<OSDMap>().Where(av => av.ContainsKey("agent_id")).ToList();
        // each avatar poses independently (its own animation fetches, skinning); Emit locks per batch. Was serial: ~8 s for 50.
        var otherAt = new object[avList.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, avList.Count), new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, async (ai, _) =>
            {
                var av = avList[ai];
                var id = av["agent_id"].AsString();
                bool worn = rigged.Any(x => x.Item1 == id) || unrigged.Any(x => x.Item1 == id);
                string g = "avatar:" + id[..8];
                var f = await PoseAvatar(id, g, id[..8] + "-", av.ContainsKey("visual_params") ? (OSDMap)av["visual_params"] : null, av.ContainsKey("anims") ? av["anims"].AsString() : null);
                var ap = av["pos"].AsVector3();
                // placeholder when export held no attachment prims: render_mesh draws a bake-textured stand-in so every
                // nearby avatar still shows (Warehouse 21: 34 in the export, only 1 had attachments on the wire)
                otherAt[ai] = new { group = g, agent_id = id, name = av.ContainsKey("name") ? av["name"].AsString() : "", pos = new[] { ap.X, ap.Y, ap.Z }, yaw = Yaw(av["rot"].AsQuaternion()), agent_off = agentOff[id], head = new[] { f["mHead"].Pos.X, f["mHead"].Pos.Y, f["mHead"].Pos.Z }, bones = Bones(f), placeholder = !worn, bake_prefix = id[..8], dressed = av.ContainsKey("dressed") ? av["dressed"].AsString() : null };
                Console.WriteLine($"avatar {av["name"].AsString()} ({id[..8]}): posed as {g}{(worn ? "" : " (no attachments: placeholder)")}");
            });
        others.AddRange(otherAt);
        Phase("avatars");
        var outMeta = new List<object>(); long off = 0;
        using (var bin = new BinaryWriter(File.Create(Path.Combine(dir, "mesh.bin"))))
            foreach (var b in batches.Values.OrderBy(b => b.Group))
            {
                foreach (var x in b.P) bin.Write(x); foreach (var x in b.N) bin.Write(x); foreach (var x in b.T) bin.Write(x); foreach (var x in b.I) bin.Write(x);
                outMeta.Add(new { group = b.Group, tex = b.Tex, rgba = b.Rgba, fullbright = b.Fullbright, mat = b.Mat, nv = b.P.Count / 3, ni = b.I.Count, offset = off });
                off += (b.P.Count + b.N.Count + b.T.Count + b.I.Count) * 4L;
            }
        File.WriteAllText(Path.Combine(dir, "mesh.json"), JsonSerializer.Serialize(new { batches = outMeta, stats, bakes = doc["bakes"].ToString(), sun = doc.ContainsKey("sun_dir") ? new[] { doc["sun_dir"].AsVector3().X, doc["sun_dir"].AsVector3().Y, doc["sun_dir"].AsVector3().Z } : null, anim = animId, lights, pelvis = frames == null ? null : new[] { frames["mPelvis"].Pos.X, frames["mPelvis"].Pos.Y, frames["mPelvis"].Pos.Z }, head = frames == null ? null : new[] { frames["mHead"].Pos.X, frames["mHead"].Pos.Y, frames["mHead"].Pos.Z }, me = new[] { me.X, me.Y, me.Z }, me_yaw = Yaw(meMap["rot"].AsQuaternion()), agent_off = agentOff.TryGetValue("me", out var mo) ? mo : (float?)null, bones = frames == null ? null : Bones(frames), others }));
        Phase("write");
        Console.Error.WriteLine("timing: " + string.Join(", ", phases));
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
        public static float[] Invert(float[] m)
        {
            var a = new System.Numerics.Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
            if (!System.Numerics.Matrix4x4.Invert(a, out var inv)) return (float[])m.Clone();
            return new[] { inv.M11, inv.M12, inv.M13, inv.M14, inv.M21, inv.M22, inv.M23, inv.M24, inv.M31, inv.M32, inv.M33, inv.M34, inv.M41, inv.M42, inv.M43, inv.M44 };
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
            Dictionary<string, Vector3> animPos = null, HashSet<string> lockScale = null)
        {
            var d = new Dictionary<string, float[]>();
            void Walk(JointBase j, Vector3 pPos, Quaternion pRot, Vector3 pScale)
            {
                BoneTransform? bt = shape != null && shape.TryGetValue(j.name, out var t) ? t : null;
                // as the SL viewer: an override within 0.1 mm of the skeleton default is ignored, so the shape's own
                // (slider-moved) position stays; a real override replaces it and, with lock_scale, the shape's scale too
                bool ov = overrides.TryGetValue(j.name, out var o) && (o - V(j.pos, 0)).LengthSquared() > 1e-8f;
                var pos = ov ? o : bt?.Position ?? V(j.pos, 0);
                if (j.name == "mPelvis") pos += pelvisOffset;
                else if (animPos != null && animPos.TryGetValue(j.name, out var ap)) pos = ap;   // animated joint translation (Bento face)
                var scale = ov && lockScale?.Contains(j.name) == true ? V(j.scale, 1) : bt?.Scale ?? V(j.scale, 1);
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
