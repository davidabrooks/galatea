// SceneExport.cs (2026-10-03, David: real shapes + her real face/body for the agent-vision test renders)
//   scene export [radius=48]   READ-ONLY: writes /workspace/secondlife/vision/export-<time>/scene.json with every prim
//                              (roots + children) whose root is within radius, her own attachments, nearby avatar
//                              positions, and her server-side bakes as raw .j2c (fetched from the appearance service).
//                              Also: legacy materials (RenderMaterials cap), per-face GLTF overrides, her decoded visual
//                              params, the region EEP environment and sun direction (all reads).
//   2026-10-04 (David: render other avatars): also every nearby avatar's attachments ("attached_to" = wearer), shape
//   (visual params), server bakes (bake-<agent id[:8]>-<name>.j2c) and playing animations ("anims" = uuid@seconds since
//   each started, from AnimClock; hers in me.anims), and root prim names (for `look at <object>`). All local reads.
//   2026-10-04 (crowd): EnsureNearbyAttachments nudges the interest list before export so other avatars' prims arrive.
// Prims are LibreMetaverse's Primitive.GetOSD() plus world_pos/world_rot. Geometry is built offline by
// vision/scene-mesher (keeps meshing out of this process). Never touches, sends or edits anything.
using System.Collections.Concurrent;
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

    // per avatar: animation -> (sequence, first seen). A new sequence number = the animation restarted.
    // ponytail: an animation already playing when she arrived counts from first sight; ceiling = loop phase off
    static readonly ConcurrentDictionary<UUID, Dictionary<UUID, (int seq, DateTime since)>> AnimClock = new();
    static void HookAnimClock() => client.Avatars.AvatarAnimation += (s, e) =>
    {
        var prev = AnimClock.TryGetValue(e.AvatarID, out var p) ? p : new();
        AnimClock[e.AvatarID] = e.Animations.GroupBy(x => x.AnimationID).ToDictionary(g => g.Key, g =>
            prev.TryGetValue(g.Key, out var o) && o.seq == g.First().AnimationSequence ? o : (g.First().AnimationSequence, DateTime.Now));
    };
    static string AnimsOf(UUID av) => AnimClock.TryGetValue(av, out var m)
        ? string.Join(",", m.OrderBy(kv => kv.Value.since).Select(kv => $"{kv.Key}@{(DateTime.Now - kv.Value.since).TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}")) : "";


    // Before a look/export: wait for nearby avatars to finish loading their attachments (David 2026-10-05: like the orange
    // clouds in a normal viewer, avatars take a while; wait for everything, with a cap). Complete = every non-HUD
    // attachment the avatar's appearance lists has its root prim here (ParentID == avatar LocalID). Selects the avatars
    // and points the camera at the nearest unfinished one to nudge the sim's interest list; read-only. Stops when all are
    // complete, after GT_LOOK_ATTACH_STALL_S (8) without a newly completed avatar, or at GT_LOOK_ATTACH_WAIT_S (30).
    // Anyone still loading is drawn as a neutral stand-in (scene-mesher reads "dressed" from the export), never half-dressed.
    static async Task<string> EnsureNearbyAttachments(float radius)
    {
        var sim = Sim; var me = client.Self; var myPos = me.SimPosition;
        var near = sim.ObjectsAvatars.Values.Where(a => a != null && a.LocalID != me.LocalID
            && Vector3.Distance(PositionHelper.GetAvatarPosition(sim, a), myPos) <= radius).ToList();
        if (near.Count == 0) return "no avatars nearby";
        string State(Avatar a, Dictionary<uint, int> rc) => AttachState(ExpectedOf(a), rc.GetValueOrDefault(a.LocalID));
        var rc0 = AttachRootsByAvatar(sim);
        if (near.All(a => State(a, rc0) == "complete")) return $"all {near.Count} nearby avatars already complete";
        // select avatar objects so the sim prioritizes their children
        foreach (var chunk in near.Select(a => a.LocalID).Chunk(50))
            client.Objects.SelectObjects(sim, chunk.ToArray(), true);
        var mv = client.Self.Movement; var home = mv.Camera.Position; var homeAt = mv.Camera.AtAxis;
        if (!(interestState.TryGetValue(sim.Handle, out var ist) && ist.ok)) _ = Ensure360ForCurrentRegion("look");
        int budget = (int)(1000 * (double.TryParse(Env("GT_LOOK_ATTACH_WAIT_S", "30"), NumberStyles.Float, CultureInfo.InvariantCulture, out var bs) ? bs : 30));
        int stall = (int)(1000 * (double.TryParse(Env("GT_LOOK_ATTACH_STALL_S", "8"), NumberStyles.Float, CultureInfo.InvariantCulture, out var ss) ? ss : 8));
        var t0 = DateTime.UtcNow; int spins = 0; var samples = new List<int>(); string why; const int tick = 500;
        Dictionary<uint, int> rc;
        while (true)
        {
            rc = AttachRootsByAvatar(sim);
            samples.Add(near.Count(a => State(a, rc) == "complete"));
            why = LookAttachWaitDone(samples, near.Count, (int)(DateTime.UtcNow - t0).TotalMilliseconds, tick, budget, stall);
            if (why != null) break;
            // nudge the camera toward the nearest unfinished avatar (interest list), then back
            var focus = near.Where(a => State(a, rc) != "complete").DefaultIfEmpty(near[0])
                .OrderBy(a => Vector3.Distance(PositionHelper.GetAvatarPosition(sim, a), myPos)).First();
            mv.Camera.LookAt(myPos, PositionHelper.GetAvatarPosition(sim, focus)); mv.SendUpdate(true);
            await Task.Delay(tick); spins++;
        }
        mv.Camera.LookAt(home, home + homeAt); mv.SendUpdate(true);
        var by = near.GroupBy(a => State(a, rc)).ToDictionary(g => g.Key, g => g.Count());
        var msg = $"{by.GetValueOrDefault("complete")}/{near.Count} nearby avatars complete ({by.GetValueOrDefault("partial")} partial, {by.GetValueOrDefault("bare")} bare, {by.GetValueOrDefault("unknown")} unknown) after {(DateTime.UtcNow - t0).TotalSeconds:F1} s, {spins} nudge(s) ({why})";
        Log("look", "attachments: " + msg);
        return msg;
    }

    // a big flat floor slab (two sides >= 8 m, <= 1.5 m thick, lying flat, within 6 m of her height) whose edge comes within
    // r of her although its centre is farther: exported on its own so the floor doesn't end in sky (Warehouse 21, a
    // skybox). Same rule as scene-mesher's GroundSlab.
    internal static bool GroundSlab(Vector3 pos, Quaternion rot, Vector3 scale, Vector3 me, float r)
    {
        var ax = new[] { (scale.X, new Vector3(1, 0, 0)), (scale.Y, new Vector3(0, 1, 0)), (scale.Z, new Vector3(0, 0, 1)) }.OrderBy(a => a.Item1).ToArray();
        if (ax[0].Item1 > 1.5f || ax[1].Item1 < 8f) return false;
        if (MathF.Abs((ax[0].Item2 * rot).Z) < 0.9f) return false;
        if (MathF.Abs(pos.Z - me.Z) > 6f) return false;
        float half = MathF.Sqrt(ax[1].Item1 * ax[1].Item1 + ax[2].Item1 * ax[2].Item1) / 2;
        return MathF.Max(0, Vector2.Distance(new Vector2(pos.X, pos.Y), new Vector2(me.X, me.Y)) - half) <= r;
    }

    static readonly string BakeCacheDir = Env("GT_BAKE_CACHE", "/workspace/secondlife/vision/bake-cache");
    internal static string BakeCachePath(string root, UUID id) => string.IsNullOrEmpty(root) || id == UUID.Zero ? null : Path.Combine(root, id.ToString()[..2], id + ".j2c");

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

        var nearAvs = sim.ObjectsAvatars.Values.Where(x => x != null && x.LocalID != me.LocalID)
            .Select(x => (av: x, pos: PositionHelper.GetAvatarPosition(sim, x))).Where(x => Vector3.Distance(x.pos, myPos) <= r).ToDictionary(x => x.av.LocalID);
        var outPrims = new OSDArray(); int mine = 0, theirs = 0;
        foreach (var p in prims)
        {
            var root = Root(p); bool attachedToMe = root.ParentID == me.LocalID;
            nearAvs.TryGetValue(root.ParentID, out var wearer);
            if (root.ParentID != 0 && !attachedToMe && wearer.av == null) continue;   // seated-on / far avatars' attachments
            if (root.ParentID == 0 && Vector3.Distance(root.Position, myPos) > r && !(p == root && GroundSlab(p.Position, p.Rotation, p.Scale, myPos, r))) continue;
            if (p.PrimData.PCode != PCode.Prim) continue;                 // trees/grass (PCode Tree/Grass) have no volume data
            var o = (OSDMap)p.GetOSD();
            if (p.RenderMaterials is { Count: > 0 } rms) { var m = new OSDMap(); foreach (var (f, id) in rms) m[f.ToString()] = OSD.FromUUID(id); o["render_materials"] = m; }
            if (attachedToMe) { o["attached_to_me"] = true; o["attach_point"] = (int)root.PrimData.AttachmentPoint; mine++; }
            else if (wearer.av != null) { o["attached_to"] = OSD.FromUUID(wearer.av.ID); o["attach_point"] = (int)root.PrimData.AttachmentPoint; theirs++; }
            else
            {
                var wp = p == root ? p.Position : root.Position + p.Position * root.Rotation;
                var wr = p == root ? p.Rotation : root.Rotation * p.Rotation;   // Hamilton order: local, then root
                o["world_pos"] = OSD.FromVector3(wp); o["world_rot"] = OSD.FromQuaternion(wr);
                if (p == root && !string.IsNullOrEmpty(p.Properties?.Name)) o["name"] = p.Properties.Name;
            }
            outPrims.Add(o);
        }

        var dir = $"/workspace/secondlife/vision/export-{DateTime.Now:yyyyMMdd-HHmmss}";
        Directory.CreateDirectory(dir);
        var bakeErr = new List<string>();
        // an avatar's server-side bakes (appearance service) as raw .j2c; file prefix "" for her, "<agent id[:8]>-" for others
        async Task<OSDMap> FetchBakes(Avatar av, string prefix)
        {
            var got = new OSDMap();
            foreach (var (te, name) in Bakes)
            {
                var id = av?.Textures?.GetFace((uint)te)?.TextureID ?? UUID.Zero;
                if (id == UUID.Zero || id == Primitive.TextureEntry.WHITE_TEXTURE || id.ToString() == "3a367d1c-bef1-6d43-7595-e88c1e3aadb3") continue; // unset / default
                // a bake's texture id changes whenever the avatar re-bakes, so the id is a safe cache key (BakeCacheDir)
                var cached = BakeCachePath(BakeCacheDir, id);
                byte[] data = cached != null && File.Exists(cached) ? File.ReadAllBytes(cached) : null;
                if (data is not { Length: > 0 })
                {
                    var tex = await client.Assets.RequestServerBakedImageAsync(av.ID, id, name);
                    data = tex?.AssetData;
                    if (data == null) { lock (bakeErr) bakeErr.Add(prefix + name); continue; }
                    if (cached != null) try { Directory.CreateDirectory(Path.GetDirectoryName(cached)!); File.WriteAllBytes(cached + ".tmp", data); File.Move(cached + ".tmp", cached, true); } catch { }
                }
                File.WriteAllBytes(Path.Combine(dir, $"bake-{prefix}{name}.j2c"), data);
                got[name] = OSD.FromUUID(id);
            }
            return got;
        }
        static OSDMap Params(Avatar av) { var m = new OSDMap(); try { foreach (var (id, v) in av.DecodeVisualParams()) m[id.ToString()] = v; } catch { } return m; }
        sim.ObjectsAvatars.TryGetValue(me.LocalID, out var self);
        var bakes = self == null ? new OSDMap() : await FetchBakes(self, "");
        var avatars = new OSDArray();
        // "dressed" (complete / partial / bare / unknown): scene-mesher draws anyone not complete as a neutral stand-in, so
        // their bakes aren't needed. Bakes fetched in parallel (was one avatar after another: ~40 s for 50 avatars).
        var rootsBy = AttachRootsByAvatar(sim);
        var avList = nearAvs.Values.ToList();
        var dressed = avList.Select(x => (exp: ExpectedOf(x.av), have: rootsBy.GetValueOrDefault(x.av.LocalID))).ToList();
        var avBakes = new OSDMap[avList.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, avList.Count), new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (i, _) =>
            avBakes[i] = AttachState(dressed[i].exp, dressed[i].have) == "complete" ? await FetchBakes(avList[i].av, avList[i].av.ID.ToString()[..8] + "-") : new OSDMap());
        for (int i = 0; i < avList.Count; i++)
        {
            var (av, ap) = avList[i];
            avatars.Add(new OSDMap
            {
                ["pos"] = OSD.FromVector3(ap), ["rot"] = OSD.FromQuaternion(av.Rotation), ["agent_id"] = OSD.FromUUID(av.ID), ["name"] = av.Name ?? "",
                ["local_id"] = (int)av.LocalID, ["visual_params"] = Params(av), ["bakes"] = avBakes[i], ["anims"] = AnimsOf(av.ID),
                ["dressed"] = AttachState(dressed[i].exp, dressed[i].have), ["attach_expected"] = dressed[i].exp, ["attach_have"] = dressed[i].have,
            });
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
        var vparams = self == null ? new OSDMap() : Params(self);
        OSD env = new OSDMap();
        try { var e = await client.Environment.GetRegionEnvironmentAsync(); if (e != null) env = e.Serialize(); } catch (Exception ex) { env = $"error: {ex.Message}"; }
        var doc = new OSDMap
        {
            ["materials"] = mats, ["gltf_overrides"] = gltf, ["visual_params"] = vparams, ["environment"] = env,
            ["sun_dir"] = OSD.FromVector3(client.Grid.SunDirection),
            ["region"] = sim.Name, ["agent_id"] = OSD.FromUUID(me.AgentID), ["me"] = new OSDMap { ["pos"] = OSD.FromVector3(myPos), ["rot"] = OSD.FromQuaternion(me.SimRotation), ["sitting_on"] = (int)me.SittingOn, ["anims"] = AnimsOf(me.AgentID) },
            ["water_height"] = sim.WaterHeight, ["terrain"] = TerrainGrid(sim),
            ["radius"] = r, ["prims"] = outPrims, ["bakes"] = bakes, ["avatars"] = avatars, ["exported_at"] = DateTime.Now.ToString("o"),
        };
        File.WriteAllText(Path.Combine(dir, "scene.json"), OSDParser.SerializeJsonString(doc));
        return $"exported {outPrims.Count} prims, {mats.Count}/{matIds.Count} materials, {gltf.Count} gltf-override prims, {vparams.Count} visual params ({mine} on my attachments), {avatars.Count} avatar(s) ({theirs} prims on their attachments), bakes {string.Join(",", bakes.Keys)}" +
               (bakeErr.Count > 0 ? $" (failed: {string.Join(",", bakeErr)})" : "") + $"\n{dir}/scene.json";
    }

    // the region heightmap every 4 m (65 x 65, x/y 0..256; the last row/column reads 255) for the renderer's ground and
    // shoreline (2026-10-04); a point without land-patch data is null
    static OSDMap TerrainGrid(Simulator sim)
    {
        const int step = 4, n = 256 / step + 1; var rows = new OSDArray();
        for (int j = 0; j < n; j++)
        {
            var row = new OSDArray();
            for (int i = 0; i < n; i++)
                row.Add(sim.TerrainHeightAtPoint(Math.Min(i * step, 255), Math.Min(j * step, 255), out var h) ? OSD.FromReal(h) : new OSD());
            rows.Add(row);
        }
        return new OSDMap { ["x0"] = 0, ["y0"] = 0, ["step"] = step, ["nx"] = n, ["ny"] = n, ["heights"] = rows };
    }

    static OSDMap GltfOsd(AssetMaterial m) => new()
    {
        ["alpha_mode"] = (int)m.AlphaMode, ["alpha_cutoff"] = m.AlphaCutoff, ["base_color"] = OSD.FromColor4(m.BaseColorFactor),
        ["metallic"] = m.MetallicFactor, ["roughness"] = m.RoughnessFactor, ["emissive"] = OSD.FromVector3(m.EmissiveFactor),
        ["double_sided"] = m.DoubleSided, ["textures"] = new OSDArray((m.TextureIds ?? Array.Empty<UUID>()).Select(t => OSD.FromUUID(t)).ToList()),
    };
}
