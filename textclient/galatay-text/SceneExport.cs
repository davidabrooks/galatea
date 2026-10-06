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
    // complete, after GT_LOOK_ATTACH_STALL_S (10) without a single new attachment arriving, or at GT_LOOK_ATTACH_WAIT_S (45).
    // (W21 20:37, just after arriving: stopping when no avatar had *finished* for 8 s gave up with 3 of 57 dressed while
    // attachments were still streaming in; progress now counts attachments.)
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
        int budget = (int)(1000 * (double.TryParse(Env("GT_LOOK_ATTACH_WAIT_S", "45"), NumberStyles.Float, CultureInfo.InvariantCulture, out var bs) ? bs : 45));
        int stall = (int)(1000 * (double.TryParse(Env("GT_LOOK_ATTACH_STALL_S", "10"), NumberStyles.Float, CultureInfo.InvariantCulture, out var ss) ? ss : 10));
        var t0 = DateTime.UtcNow; int spins = 0; var samples = new List<int>(); string why; const int tick = 500;
        Dictionary<uint, int> rc;
        while (true)
        {
            rc = AttachRootsByAvatar(sim);
            samples.Add(near.Sum(a => Math.Min(rc.GetValueOrDefault(a.LocalID), ExpectedOf(a))));
            why = LookAttachWaitDone(samples, near.Sum(a => ExpectedOf(a)), (int)(DateTime.UtcNow - t0).TotalMilliseconds, tick, budget,
                LookStallMs(near.Count(a => State(a, rc) != "complete"), near.Count, stall));
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

    // stall window for the look's attachment wait: the full window while many avatars are unfinished, 3 s when only a few
    // stragglers remain (W21 22:12: 59/61 complete, and the 10 s window was the whole wait - some never finish: the sim
    // lists an attachment it never sends)
    internal static int LookStallMs(int incomplete, int total, int baseMs) =>
        incomplete <= Math.Max(2, total / 20) ? Math.Min(baseMs, 3000) : baseMs;

    // BOM: an attachment face showing one of these ids shows the wearer's server bake of that channel (as scene-mesher maps them)
    static readonly Dictionary<UUID, string> BakeRefs = new()
    {
        [new UUID("5a9f4a74-30f2-821c-b88d-70499d3e7183")] = "head", [new UUID("ae2de45c-d252-50b8-5c6e-19f39ce79317")] = "upper",
        [new UUID("24daea5f-0539-cfcf-047f-fbc40b2786ba")] = "lower", [new UUID("52cc6bb6-2ee5-e632-d3ad-50197b1dcb8a")] = "eyes",
        [new UUID("43529ce8-7faa-ad92-165a-bc4078371687")] = "skirt", [new UUID("09aac1fb-6bce-0bee-7d44-caac6dbb6c63")] = "hair",
        [new UUID("ff62763f-d60a-9855-890b-0c96f8f8cd98")] = "leftarm", [new UUID("8e915e25-31d1-cc95-ae08-d58a47488251")] = "leftleg",
        [new UUID("9742065b-19b5-297c-858a-29711d539043")] = "aux1", [new UUID("03642e83-2bd1-4eb9-34b4-4c47ed586d2d")] = "aux2",
        [new UUID("edd51b77-fc10-ce7a-4b3d-011dfc349e4f")] = "aux3",
    };
    // another avatar's bake channels worth fetching: the system body's (head/upper/lower/eyes) plus any its attachments show.
    // W21: 60 avatars x 11 channels were fetched, but skirt/hair/aux2 were never drawn and arms/legs/aux3 once each.
    internal static HashSet<string> BakesNeeded(IEnumerable<UUID> attachmentTextures)
    {
        var need = new HashSet<string> { "head", "upper", "lower", "eyes" };
        foreach (var t in attachmentTextures) if (BakeRefs.TryGetValue(t, out var n)) need.Add(n);
        return need;
    }
    static IEnumerable<UUID> FaceTextureIds(Primitive p)
    {
        var te = p.Textures; if (te == null) yield break;
        if (te.DefaultTexture != null) yield return te.DefaultTexture.TextureID;
        foreach (var f in te.FaceTextures) if (f != null) yield return f.TextureID;
    }

    // legacy materials by id (an id names fixed content), kept on disk: W21 exports asked the RenderMaterials cap for ~3,900
    // ids in 50-id requests one after another on every look
    static readonly string MaterialCacheFile = Env("GT_MATERIAL_CACHE", "/workspace/secondlife/vision/material-cache.json");
    static ConcurrentDictionary<string, OSD> matCache;
    static readonly object matCacheSave = new();
    static ConcurrentDictionary<string, OSD> MatCache()
    {
        if (matCache != null) return matCache;
        var d = new ConcurrentDictionary<string, OSD>();
        try { if (File.Exists(MaterialCacheFile) && OSDParser.DeserializeJson(File.ReadAllText(MaterialCacheFile)) is OSDMap m) foreach (KeyValuePair<string, OSD> kv in m) d[kv.Key] = kv.Value; } catch { }
        return matCache = d;
    }
    static void SaveMatCache()
    {
        lock (matCacheSave)
        {
            var m = new OSDMap(); foreach (var kv in MatCache()) m[kv.Key] = kv.Value;
            try { File.WriteAllText(MaterialCacheFile + ".tmp", OSDParser.SerializeJsonString(m)); File.Move(MaterialCacheFile + ".tmp", MaterialCacheFile, true); } catch { }
        }
    }
    static OSDMap MaterialOsd(LibreMetaverse.Materials.LegacyMaterial m) => new()
    {
        ["alpha_mode"] = (int)m.DiffuseAlphaMode, ["alpha_cutoff"] = (int)m.AlphaMaskCutoff,
        ["normal"] = OSD.FromUUID(m.NormalMap), ["normal_rep"] = OSD.FromVector3(new Vector3((float)m.NormalMapRepeatX, (float)m.NormalMapRepeatY, (float)m.NormalMapRotation)),
        ["normal_off"] = OSD.FromVector2(new Vector2((float)m.NormalMapOffsetX, (float)m.NormalMapOffsetY)),
        ["spec"] = OSD.FromUUID(m.SpecularMap), ["spec_rep"] = OSD.FromVector3(new Vector3((float)m.SpecularMapRepeatX, (float)m.SpecularMapRepeatY, (float)m.SpecularMapRotation)),
        ["spec_off"] = OSD.FromVector2(new Vector2((float)m.SpecularMapOffsetX, (float)m.SpecularMapOffsetY)),
        ["spec_color"] = OSD.FromColor4(m.SpecularColor), ["spec_exp"] = (int)m.SpecularExponent, ["env"] = (int)m.EnvironmentIntensity,
    };

    // the environment changes rarely; reused for 10 min per (region, parcel). Fetched for her parcel: its own EEP
    // setting is what the SL viewer shows there (LLEnvironment::requestParcel), the region's when it has none
    // (the reply's parcel_id is then -1). Before 2026-10-06 only the region's was fetched.
    static (ulong handle, int parcel, DateTime t, OSD env) envCache;

    // pure (neighbor selftest)
    internal static bool EnvCacheHit((ulong handle, int parcel, DateTime t, OSD env) c, ulong handle, int parcel, DateTime now)
        => c.env != null && c.handle == handle && c.parcel == parcel && (now - c.t).TotalMinutes < 10;

    // her parcel's local id: the parcel map (filled by every ParcelProperties, incl. the one sent on arrival), else
    // one ParcelProperties request; 0 if unknown
    static async Task<int> ParcelIdAt(Simulator sim, Vector3 pos)
    {
        int ix = Math.Clamp((int)(pos.X / 4), 0, 63), iy = Math.Clamp((int)(pos.Y / 4), 0, 63);
        try { var id = sim.ParcelMap[iy, ix]; if (id > 0) return id; } catch { }
        var p = await ParcelAt(sim, pos.X, pos.Y, 3000);
        return p?.LocalID ?? 0;
    }

    // scene.json written as it is built: {"prims":[ one prim at a time ], <rest of the document>} - the old single
    // SerializeJsonString held the whole ~30 MB document as OSD + UTF-8 buffer + UTF-16 string at once (W21: 20k prims,
    // client at 688 of 768 MB). Same JSON as SerializeJsonString(doc with the prims array), keys in another order.
    internal static void WriteJsonPrimsThenRest(Stream s, Action<Stream> writePrims, OSDMap restDoc)
    {
        s.Write(System.Text.Encoding.UTF8.GetBytes("{\"prims\":["));
        writePrims(s);                                        // comma-separated prim objects
        var rest = OSDParser.SerializeJsonString(restDoc);    // "{...}"
        s.Write(System.Text.Encoding.UTF8.GetBytes(rest.Length > 2 ? "]," + rest[1..] : "]}"));
    }

    static async Task<string> SceneExport(string[] a)
    {
        float r = 48;
        if (a.Length > 1 && (!float.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out r) || r <= 0 || r > 128))
            return "usage: scene export [radius 1-128]";
        var sw = System.Diagnostics.Stopwatch.StartNew(); var stages = new List<string>();
        void Stage(string name, string detail = "") { stages.Add($"{name}={sw.Elapsed.TotalSeconds:F1}{detail}"); sw.Restart(); }
        var sim = Sim; var me = client.Self;
        var myPos = me.SimPosition;
        // 2026-10-06 neighbor regions (Neighbors.cs): connected neighbors' prims and avatars within the radius, in this region's
        // frame (+ the neighbor's offset). Local ids are per region, so a neighbor's localid/parentid are remapped to unique
        // ids (scene-mesher indexes prims by localid). GT_LOOK_NEIGHBORS=off = this region only.
        var views = ViewSims(Env("GT_LOOK_NEIGHBORS", "on") != "off");
        var hereIds = sim.ObjectsAvatars.Values.Where(x => x != null).Select(x => x.ID).ToHashSet();
        var remap = new Dictionary<(int, uint), uint>(); uint nextId = 0xC0000000;
        uint Id(int si, uint lid) { if (si == 0 || lid == 0) return lid; if (!remap.TryGetValue((si, lid), out var v)) remap[(si, lid)] = v = nextId++; return v; }
        var nearAvs = new Dictionary<(int si, uint lid), (Avatar av, Vector3 pos)>();
        for (int si = 0; si < views.Count; si++)
        {
            var (vs, off) = views[si];
            foreach (var x in vs.ObjectsAvatars.Values)
            {
                if (x == null || (si == 0 && x.LocalID == me.LocalID) || (si > 0 && (x.ID == me.AgentID || hereIds.Contains(x.ID)))) continue;
                if (si > 0 && x.ParentID != 0 && !vs.ObjectsPrimitives.ContainsKey(x.ParentID)) continue;   // seat unknown: position unknown
                var pos = PositionHelper.GetAvatarPosition(vs, x) + off;
                if (Vector3.Distance(pos, myPos) <= r && !nearAvs.Values.Any(v => v.av.ID == x.ID)) nearAvs[(si, x.LocalID)] = (x, pos);
            }
        }
        int nbPrims = 0;
        var dir = $"/workspace/secondlife/vision/export-{DateTime.Now:yyyyMMdd-HHmmss}";
        Directory.CreateDirectory(dir);
        var scenePath = Path.Combine(dir, "scene.json");
        var primPart = Path.Combine(dir, "prims.part");
        int nPrims = 0, mine = 0, theirs = 0;
        var matIds = new HashSet<UUID>(); var gltf = new OSDMap();
        var wornTex = new Dictionary<(int, uint), HashSet<UUID>>();   // (region index, wearer LocalID) -> texture ids on its attachments
        using (var part = new FileStream(primPart, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        for (int si = 0; si < views.Count; si++)
        {
            var (vs, off) = views[si];
            var prims = vs.ObjectsPrimitives.Values.Where(p => p != null).ToList();
            var byLocal = new Dictionary<uint, Primitive>(); foreach (var p in prims) byLocal[p.LocalID] = p;
            Primitive Root(Primitive p) { var q = p; for (int i = 0; i < 8 && q.ParentID != 0 && byLocal.TryGetValue(q.ParentID, out var up); i++) q = up; return q; }
            foreach (var p in prims)
            {
                var root = Root(p); bool attachedToMe = si == 0 && root.ParentID == me.LocalID;
                nearAvs.TryGetValue((si, root.ParentID), out var wearer);
                if (root.ParentID != 0 && !attachedToMe && wearer.av == null) continue;   // seated-on / far avatars' attachments
                if (root.ParentID == 0 && Vector3.Distance(root.Position + off, myPos) > r && !(p == root && GroundSlab(p.Position + off, p.Rotation, p.Scale, myPos, r))) continue;
                if (p.PrimData.PCode != PCode.Prim) continue;                 // trees/grass (PCode Tree/Grass) have no volume data
                var o = (OSDMap)p.GetOSD();
                if (p.RenderMaterials is { Count: > 0 } rms) { var m = new OSDMap(); foreach (var (f, id) in rms) m[f.ToString()] = OSD.FromUUID(id); o["render_materials"] = m; }
                if (attachedToMe) { o["attached_to_me"] = true; o["attach_point"] = (int)root.PrimData.AttachmentPoint; mine++; }
                else if (wearer.av != null)
                {
                    o["attached_to"] = OSD.FromUUID(wearer.av.ID); o["attach_point"] = (int)root.PrimData.AttachmentPoint; theirs++;
                    if (!wornTex.TryGetValue((si, wearer.av.LocalID), out var ts)) wornTex[(si, wearer.av.LocalID)] = ts = new HashSet<UUID>();
                    foreach (var t in FaceTextureIds(p)) ts.Add(t);
                }
                else
                {
                    var wp = (p == root ? p.Position : root.Position + p.Position * root.Rotation) + off;
                    var wr = p == root ? p.Rotation : root.Rotation * p.Rotation;   // Hamilton order: local, then root
                    o["world_pos"] = OSD.FromVector3(wp); o["world_rot"] = OSD.FromQuaternion(wr);
                    if (p == root && !string.IsNullOrEmpty(p.Properties?.Name)) o["name"] = p.Properties.Name;
                }
                // legacy materials and GLTF overrides of exported prims only (was: every prim in the region)
                var te = p.Textures;
                if (te != null) foreach (var f in te.FaceTextures.Append(te.DefaultTexture)) if (f != null && f.MaterialID != UUID.Zero) matIds.Add(f.MaterialID);
                if (vs.GLTFMaterialOverrides.TryGetValue(p.LocalID, out var ov) && ov.FaceOverrides.Count > 0)
                {
                    var faces = new OSDMap();
                    foreach (var (face, m) in ov.FaceOverrides) faces[face.ToString()] = GltfOsd(m);
                    gltf[Id(si, p.LocalID).ToString()] = faces;
                }
                if (si > 0) { o["localid"] = OSD.FromUInteger(Id(si, p.LocalID)); o["parentid"] = OSD.FromUInteger(Id(si, p.ParentID)); o["region"] = vs.Name; nbPrims++; }
                if (nPrims++ > 0) part.WriteByte((byte)',');
                var bytes = System.Text.Encoding.UTF8.GetBytes(OSDParser.SerializeJsonString(o)); part.Write(bytes, 0, bytes.Length);
            }
        }
        Stage("prims", $"({nPrims}{(nbPrims > 0 ? $", {nbPrims} from {views.Count - 1} neighbor regions" : "")})");

        var bakeErr = new ConcurrentBag<string>(); int bakeCached = 0, bakeFetched = 0;
        // one bake (server-side, appearance service) as raw .j2c: bake-<prefix><name>.j2c; prefix "" for her, "<agent id[:8]>-" for others
        async Task<(string name, UUID id)?> FetchBake(Avatar av, int te, string name, string prefix)
        {
            var id = av?.Textures?.GetFace((uint)te)?.TextureID ?? UUID.Zero;
            if (id == UUID.Zero || id == Primitive.TextureEntry.WHITE_TEXTURE || id.ToString() == "3a367d1c-bef1-6d43-7595-e88c1e3aadb3") return null; // unset / default
            // a bake's texture id changes whenever the avatar re-bakes, so the id is a safe cache key (BakeCacheDir)
            var cached = BakeCachePath(BakeCacheDir, id);
            byte[] data = cached != null && File.Exists(cached) ? File.ReadAllBytes(cached) : null;
            if (data is { Length: > 0 }) Interlocked.Increment(ref bakeCached);
            else
            {
                var tex = await client.Assets.RequestServerBakedImageAsync(av.ID, id, name);
                data = tex?.AssetData;
                if (data == null) { bakeErr.Add(prefix + name); return null; }
                Interlocked.Increment(ref bakeFetched);
                if (cached != null) try { Directory.CreateDirectory(Path.GetDirectoryName(cached)!); File.WriteAllBytes(cached + ".tmp", data); File.Move(cached + ".tmp", cached, true); } catch { }
            }
            await File.WriteAllBytesAsync(Path.Combine(dir, $"bake-{prefix}{name}.j2c"), data);
            return (name, id);
        }
        static OSDMap Params(Avatar av) { var m = new OSDMap(); try { foreach (var (id, v) in av.DecodeVisualParams()) m[id.ToString()] = v; } catch { } return m; }
        sim.ObjectsAvatars.TryGetValue(me.LocalID, out var self);
        // "dressed" (complete / partial / bare / unknown): scene-mesher draws anyone not complete as a neutral stand-in, so
        // their bakes aren't needed. Every needed bake of every avatar (hers: all channels) fetched 16 at a time (was 8 avatars
        // at a time, each avatar's 11 channels one after another: ~55 s cold for 51 avatars).
        var rootsBy = views.Select(v => AttachRootsByAvatar(v.sim)).ToList();
        var avKeys = nearAvs.Keys.ToList();
        var avList = avKeys.Select(k => nearAvs[k]).ToList();
        var dressed = avKeys.Select((k, i) => (exp: ExpectedOf(avList[i].av), have: rootsBy[k.si].GetValueOrDefault(k.lid))).ToList();
        var jobs = new List<(int who, int te, string name)>();   // who: -1 = her
        if (self != null) foreach (var (te, name) in Bakes) jobs.Add((-1, te, name));
        for (int i = 0; i < avList.Count; i++)
        {
            if (AttachState(dressed[i].exp, dressed[i].have) != "complete") continue;
            var need = BakesNeeded(wornTex.TryGetValue(avKeys[i], out var ts) ? ts : Enumerable.Empty<UUID>());
            foreach (var (te, name) in Bakes) if (need.Contains(name)) jobs.Add((i, te, name));
        }
        var got = new ConcurrentDictionary<int, ConcurrentDictionary<string, UUID>>();
        await Parallel.ForEachAsync(jobs, new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (j, _) =>
        {
            var av = j.who < 0 ? self : avList[j.who].av;
            var res = await FetchBake(av, j.te, j.name, j.who < 0 ? "" : av.ID.ToString()[..8] + "-");
            if (res is { } x) got.GetOrAdd(j.who, _ => new())[x.name] = x.id;
        });
        OSDMap BakeMap(int who) { var m = new OSDMap(); if (got.TryGetValue(who, out var d)) foreach (var (te, name) in Bakes) if (d.TryGetValue(name, out var id)) m[name] = OSD.FromUUID(id); return m; }
        var bakes = BakeMap(-1);
        var avatars = new OSDArray();
        for (int i = 0; i < avList.Count; i++)
        {
            var (av, ap) = avList[i];
            avatars.Add(new OSDMap
            {
                ["pos"] = OSD.FromVector3(ap), ["rot"] = OSD.FromQuaternion(av.Rotation), ["agent_id"] = OSD.FromUUID(av.ID), ["name"] = av.Name ?? "",
                ["local_id"] = unchecked((int)Id(avKeys[i].si, av.LocalID)), ["visual_params"] = Params(av), ["bakes"] = BakeMap(i), ["anims"] = AnimsOf(av.ID),
                ["dressed"] = AttachState(dressed[i].exp, dressed[i].have), ["attach_expected"] = dressed[i].exp, ["attach_have"] = dressed[i].have,
            });
        }
        Stage("bakes", $"({jobs.Count} wanted, {bakeCached} cached, {bakeFetched} fetched, {bakeErr.Count} failed)");

        // legacy materials for every face MaterialID we export (alpha mode/cutoff, normal+spec maps): disk cache, misses
        // asked 4 requests at a time (the LL viewer posts <=50 ids per request)
        var mc = MatCache(); var mats = new OSDMap(); var missing = new List<UUID>();
        foreach (var id in matIds) if (mc.TryGetValue(id.ToString(), out var m)) mats[id.ToString()] = m; else missing.Add(id);
        int matFetched = 0;
        if (missing.Count > 0)
        {
            await Parallel.ForEachAsync(missing.Chunk(50), new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (chunk, _) =>
            {
                try { foreach (var m in await client.Objects.RequestMaterialsAsync(sim, chunk)) { mc[m.ID.ToString()] = MaterialOsd(m); Interlocked.Increment(ref matFetched); } }
                catch (Exception ex) { Log("look", "materials request failed: " + ex.GetBaseException().Message); }
            });
            foreach (var id in missing) if (mc.TryGetValue(id.ToString(), out var m)) mats[id.ToString()] = m;
            if (matFetched > 0) SaveMatCache();
        }
        Stage("materials", $"({matIds.Count} ids, {matIds.Count - missing.Count} cached, {matFetched} fetched)");

        var vparams = self == null ? new OSDMap() : Params(self);
        OSD env; var parcelId = await ParcelIdAt(sim, myPos);
        if (EnvCacheHit(envCache, sim.Handle, parcelId, DateTime.UtcNow)) env = envCache.env;
        else
        {
            env = new OSDMap();
            try
            {
                var e = parcelId > 0 ? await client.Environment.GetParcelEnvironmentAsync(parcelId) : null;
                e ??= await client.Environment.GetRegionEnvironmentAsync();
                if (e != null) { env = e.Serialize(); envCache = (sim.Handle, parcelId, DateTime.UtcNow, env); }
            }
            catch (Exception ex) { env = $"error: {ex.Message}"; }
        }
        Stage("env", $"(parcel {parcelId})");
        var doc = new OSDMap
        {
            ["materials"] = mats, ["gltf_overrides"] = gltf, ["visual_params"] = vparams, ["environment"] = env,
            ["sun_dir"] = OSD.FromVector3(client.Grid.SunDirection),
            ["region"] = sim.Name, ["agent_id"] = OSD.FromUUID(me.AgentID), ["me"] = new OSDMap { ["pos"] = OSD.FromVector3(myPos), ["rot"] = OSD.FromQuaternion(me.SimRotation), ["sitting_on"] = (int)me.SittingOn, ["anims"] = AnimsOf(me.AgentID) },
            ["water_height"] = sim.WaterHeight, ["terrain"] = views.Count > 1 ? TerrainGridMerged(views) : TerrainGrid(sim),
            ["neighbor_regions"] = new OSDArray(views.Skip(1).Select(v => (OSD)new OSDMap { ["name"] = v.sim.Name, ["offset"] = OSD.FromVector3(v.off) }).ToList()),
            ["radius"] = r, ["bakes"] = bakes, ["avatars"] = avatars, ["exported_at"] = DateTime.Now.ToString("o"),
        };
        using (var fs = new FileStream(scenePath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            WriteJsonPrimsThenRest(fs, st => { using var pp = File.OpenRead(primPart); pp.CopyTo(st); }, doc);
        }
        File.Move(scenePath + ".tmp", scenePath, true); File.Delete(primPart);
        Stage("write");
        var stageLine = string.Join(",", stages);
        Log("look", "export stages (s): " + stageLine);
        lastExportStages = stageLine;
        return $"exported {nPrims} prims, {mats.Count}/{matIds.Count} materials, {gltf.Count} gltf-override prims, {vparams.Count} visual params ({mine} on my attachments), {avatars.Count} avatar(s) ({theirs} prims on their attachments), bakes {string.Join(",", bakes.Keys)}" +
               (bakeErr.Count > 0 ? $" (failed: {string.Join(",", bakeErr)})" : "") + $" [stages {stageLine}]\n{scenePath}";
    }
    static string lastExportStages = "";

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

    // her region's heightmap plus every connected neighbor's, on one grid in her region's frame (a look across a border
    // has ground on both sides). Cells no region covers are filled from the nearest known cell (the renderer would put 20 m).
    static OSDMap TerrainGridMerged(List<(Simulator sim, Vector3 off)> views)
    {
        const int step = 4, n = 256 / step + 1;
        var grids = new List<(int gx0, int gy0, float?[,] h)>();
        foreach (var (s, off) in views)
        {
            var h = new float?[n, n]; int known = 0;
            for (int j = 0; j < n; j++) for (int i = 0; i < n; i++)
                if (s.TerrainHeightAtPoint(Math.Min(i * step, 255), Math.Min(j * step, 255), out var v)) { h[j, i] = v; known++; }
            if (known > 0) grids.Add(((int)MathF.Round(off.X / step), (int)MathF.Round(off.Y / step), h));
        }
        var (x0, y0, m) = MergeTerrain(grids, n);
        var rows = new OSDArray();
        for (int j = 0; j < m.GetLength(0); j++) { var row = new OSDArray(); for (int i = 0; i < m.GetLength(1); i++) row.Add(m[j, i] is float f ? OSD.FromReal(f) : new OSD()); rows.Add(row); }
        return new OSDMap { ["x0"] = x0 * step, ["y0"] = y0 * step, ["step"] = step, ["nx"] = m.GetLength(1), ["ny"] = m.GetLength(0), ["heights"] = rows };
    }

    // pure (neighbor selftest): grids of n x n cells at cell offsets (gx0, gy0); the first grid wins where they overlap;
    // returns the union's origin (cells) and heights [row j, column i], gaps filled from the nearest known cell (BFS)
    internal static (int x0, int y0, float?[,] h) MergeTerrain(List<(int gx0, int gy0, float?[,] h)> grids, int n)
    {
        if (grids.Count == 0) return (0, 0, new float?[0, 0]);
        int minX = grids.Min(g => g.gx0), minY = grids.Min(g => g.gy0), maxX = grids.Max(g => g.gx0) + n, maxY = grids.Max(g => g.gy0) + n;
        int w = maxX - minX, hgt = maxY - minY; var m = new float?[hgt, w];
        foreach (var (gx0, gy0, h) in grids)
            for (int j = 0; j < n; j++) for (int i = 0; i < n; i++)
            {
                int J = gy0 - minY + j, I = gx0 - minX + i;
                if (m[J, I] == null && h[j, i] != null) m[J, I] = h[j, i];
            }
        var q = new Queue<(int, int)>();
        for (int j = 0; j < hgt; j++) for (int i = 0; i < w; i++) if (m[j, i] != null) q.Enqueue((j, i));
        while (q.Count > 0)
        {
            var (j, i) = q.Dequeue();
            foreach (var (dj, di) in new[] { (0, 1), (1, 0), (0, -1), (-1, 0) })
            {
                int a = j + dj, b = i + di;
                if (a < 0 || b < 0 || a >= hgt || b >= w || m[a, b] != null) continue;
                m[a, b] = m[j, i]; q.Enqueue((a, b));
            }
        }
        return (minX, minY, m);
    }

    static OSDMap GltfOsd(AssetMaterial m) => new()
    {
        ["alpha_mode"] = (int)m.AlphaMode, ["alpha_cutoff"] = m.AlphaCutoff, ["base_color"] = OSD.FromColor4(m.BaseColorFactor),
        ["metallic"] = m.MetallicFactor, ["roughness"] = m.RoughnessFactor, ["emissive"] = OSD.FromVector3(m.EmissiveFactor),
        ["double_sided"] = m.DoubleSided, ["textures"] = new OSDArray((m.TextureIds ?? Array.Empty<UUID>()).Select(t => OSD.FromUUID(t)).ToList()),
    };
}
