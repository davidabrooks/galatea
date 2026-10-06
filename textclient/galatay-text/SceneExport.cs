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


    // Before a look/export: wait briefly for nearby avatars' attachment prims (ParentID == avatar LocalID).
    // AvatarAppearance lists expected attachment UUIDs; the interest list may not have delivered them yet in a crowd
    // (Warehouse 21: 34 avatars in range, only 1 had any attachment prims). Select the avatars and re-anchor the
    // camera to nudge the sim; read-only. ponytail: adaptive wait up to 25 s (stops when no progress for 4 s); ceiling = still-missing attachments stay missing
    static async Task EnsureNearbyAttachments(float radius)
    {
        var sim = Sim; var me = client.Self; var myPos = me.SimPosition;
        var near = sim.ObjectsAvatars.Values.Where(a => a != null && a.LocalID != me.LocalID
            && Vector3.Distance(PositionHelper.GetAvatarPosition(sim, a), myPos) <= radius).ToList();
        if (near.Count == 0) return;
        int Have(Avatar a) => sim.ObjectsPrimitives.Values.Count(p => p != null && p.ParentID == a.LocalID);
        int Expect(Avatar a) => a.Attachments?.Count ?? 0;
        var short_ = near.Where(a => Expect(a) > 0 && Have(a) < Expect(a)).ToList();
        if (short_.Count == 0 && near.All(a => Have(a) > 0)) return;
        // select avatar objects so the sim prioritizes their children
        foreach (var chunk in near.Select(a => a.LocalID).Chunk(50))
            client.Objects.SelectObjects(sim, chunk.ToArray(), true);
        var mv = client.Self.Movement; var home = mv.Camera.Position; var homeAt = mv.Camera.AtAxis;
        // the region should be in 360 interest mode (Crowd.cs); re-assert it once if it isn't, then wait while attachments
        // keep arriving (adaptive: stop when all have some, no progress for 4 s, or 25 s)
        if (!(interestState.TryGetValue(sim.Handle, out var ist) && ist.ok)) _ = Ensure360ForCurrentRegion("look");
        var t0 = DateTime.UtcNow; int spins = 0; var samples = new List<int>(); string why;
        const int tick = 500, budget = 25000;
        while (true)
        {
            var rc = AttachRootsByAvatar(sim);
            int withAny = near.Count(a => rc.ContainsKey(a.LocalID));
            samples.Add(withAny);
            why = LookAttachWaitDone(samples, near.Count, (int)(DateTime.UtcNow - t0).TotalMilliseconds, tick, budget);
            if (why != null) break;
            // nudge camera toward the nearest bare/short avatar (interest list), then back
            short_ = near.Where(a => Expect(a) > 0 && Have(a) < Math.Max(1, Expect(a) / 2)).ToList();
            var focus = (short_.Count > 0 ? short_ : near.Where(a => Have(a) == 0).DefaultIfEmpty(near[0])).OrderBy(a => Vector3.Distance(PositionHelper.GetAvatarPosition(sim, a), myPos)).First();
            var fp = PositionHelper.GetAvatarPosition(sim, focus);
            mv.Camera.LookAt(myPos, fp); mv.SendUpdate(true);
            await Task.Delay(tick); spins++;
        }
        mv.Camera.LookAt(home, home + homeAt); mv.SendUpdate(true);
        int with = near.Count(a => Have(a) > 0);
        Log("look", $"attachments: {with}/{near.Count} nearby avatars have prims after {spins} nudge(s) in {(DateTime.UtcNow - t0).TotalSeconds:F1} s ({why})");
    }

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
            if (root.ParentID == 0 && Vector3.Distance(root.Position, myPos) > r) continue;
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
                var tex = await client.Assets.RequestServerBakedImageAsync(av.ID, id, name);
                if (tex?.AssetData == null) { bakeErr.Add(prefix + name); continue; }
                File.WriteAllBytes(Path.Combine(dir, $"bake-{prefix}{name}.j2c"), tex.AssetData);
                got[name] = OSD.FromUUID(id);
            }
            return got;
        }
        static OSDMap Params(Avatar av) { var m = new OSDMap(); try { foreach (var (id, v) in av.DecodeVisualParams()) m[id.ToString()] = v; } catch { } return m; }
        sim.ObjectsAvatars.TryGetValue(me.LocalID, out var self);
        var bakes = self == null ? new OSDMap() : await FetchBakes(self, "");
        var avatars = new OSDArray();
        foreach (var (av, ap) in nearAvs.Values)
            avatars.Add(new OSDMap
            {
                ["pos"] = OSD.FromVector3(ap), ["rot"] = OSD.FromQuaternion(av.Rotation), ["agent_id"] = OSD.FromUUID(av.ID), ["name"] = av.Name ?? "",
                ["local_id"] = (int)av.LocalID, ["visual_params"] = Params(av), ["bakes"] = await FetchBakes(av, av.ID.ToString()[..8] + "-"), ["anims"] = AnimsOf(av.ID),
            });
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
