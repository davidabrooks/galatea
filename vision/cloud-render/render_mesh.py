"""Blender script: render real SL geometry from scene-mesher output (mesh.json + mesh.bin).

blender -b --factory-startup --python render_mesh.py -- <jobdir> {scene|face|body} {CYCLES|EEVEE|WORKBENCH} out.jpg
jobdir holds mesh.json, mesh.bin, terrain.json and tex/<uuid>.png, tex/bake-<name>.png.
Galatea's rigged attachments are in bind pose (= SL's default T-pose), in avatar space (+X forward, feet near z=0).
"""
import bpy, json, math, os, sys, time
import numpy as np
from mathutils import Vector

jobdir, kind, engine, out = sys.argv[sys.argv.index("--") + 1:][:4]
M = json.load(open(f"{jobdir}/mesh.json")); BIN = open(f"{jobdir}/mesh.bin", "rb").read()
T0 = time.time()
bpy.ops.wm.read_factory_settings(use_empty=True)
sc = bpy.context.scene
def log(*a): print("GT:", *a, flush=True)
def lin(c): return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4   # sRGB -> linear

def amblit(env):
    """The SL viewer's sky ambient on every surface (reimplemented from the LL viewer's calcAtmosphericVars):
    ambient raised by cloud cover, ^0.9 x 0.57, to linear, as grey luminance. It is NOT occluded the way Cycles' world light
    is, which is why her interiors at night rendered near-black while the SL viewer shows them dim but readable.
    ponytail: LL's normal-dependent ambientLighting() factor is taken at its mean (0.9); reflection probes (the PBR
    viewer's indoor irradiance) aren't modelled; ceiling = flat-looking ambient in corners"""
    if not env or not env.get("ambient"): return 0.0
    cs = env.get("cloud_shadow", 0.0)
    t = [lin((x + (1 - x) * cs * 0.5) ** 0.9 * 0.57 * 0.9) for x in env["ambient"]]
    return 0.2126 * t[0] + 0.7152 * t[1] + 0.0722 * t[2]
AMB = amblit(M.get("env")) if kind == "scene" else 0.0  # face/body portraits have their own studio lights
ALPHA_CACHE = {}  # name -> (has_cutout, amax)
def _alpha_stats(image):
    if image.name not in ALPHA_CACHE:
        if image.channels != 4 or not image.pixels:
            ALPHA_CACHE[image.name] = (False, 1.0)
        else:
            a = np.empty(len(image.pixels), np.float32); image.pixels.foreach_get(a)
            aa = a[3::4]; ALPHA_CACHE[image.name] = (float(aa.min()) < 0.98, float(aa.max()) if aa.size else 1.0)
    return ALPHA_CACHE[image.name]
def has_alpha(image):
    """any texel below 0.98 alpha (the image is loaded anyway; one numpy pass per distinct texture)"""
    return _alpha_stats(image)[0]

def material(b):
    """SL face -> Principled BSDF. alpha: none | blend | mask (cutoff) | emissive (alpha = glow mask) | auto (texture alpha)."""
    key = b["tex"]; mat = b.get("mat") or {"alpha": "auto"}; m = bpy.data.materials.new(key[:50]); m.use_nodes = True
    nt = m.node_tree; p = nt.nodes["Principled BSDF"]; r, g, bl, a = b["rgba"]; L = nt.links.new
    def img(uuid, data=False):
        if not uuid: return None
        base = f"{jobdir}/tex/{uuid.replace('bake:', 'bake-')}"
        path = next((p for p in (base + ".png", *(f"{base}.{c}.png" for c in (1024, 512, 256, 128))) if os.path.exists(p)), None)
        if not path: return None
        n = nt.nodes.new("ShaderNodeTexImage"); n.image = bpy.data.images.load(path, check_existing=True)
        if data: n.image.colorspace_settings.name = "Non-Color"
        return n
    def mul(x, y):
        n = nt.nodes.new("ShaderNodeMath"); n.operation = "MULTIPLY"; L(x, n.inputs[0])
        if isinstance(y, float): n.inputs[1].default_value = y
        else: L(y, n.inputs[1])
        return n.outputs[0]
    p.inputs["Base Color"].default_value = (r, g, bl, 1); p.inputs["Roughness"].default_value = 0.6
    alpha = None; it = img(key)
    if it:
        mix = nt.nodes.new("ShaderNodeMix"); mix.data_type = "RGBA"; mix.blend_type = "MULTIPLY"; mix.inputs["Factor"].default_value = 1
        L(it.outputs["Color"], mix.inputs["A"]); mix.inputs["B"].default_value = (r, g, bl, 1); L(mix.outputs["Result"], p.inputs["Base Color"])
        alpha = mul(it.outputs["Alpha"], a) if a < 0.999 else it.outputs["Alpha"]
    mode = mat["alpha"]
    if mode in ("auto", "blend"):
        if alpha is not None: L(alpha, p.inputs["Alpha"])
        elif a < 0.999: p.inputs["Alpha"].default_value = a
    elif mode == "mask" and alpha is not None:
        gt = nt.nodes.new("ShaderNodeMath"); gt.operation = "GREATER_THAN"; L(alpha, gt.inputs[0]); gt.inputs[1].default_value = mat.get("cutoff", 0.5) - 1e-4
        L(gt.outputs[0], p.inputs["Alpha"])
    elif mode == "emissive" and alpha is not None:
        L(p.inputs["Base Color"].links[0].from_socket, p.inputs["Emission Color"]); L(alpha, p.inputs["Emission Strength"])
    nm = img(mat.get("normal"), True)
    if nm:
        n = nt.nodes.new("ShaderNodeNormalMap"); L(nm.outputs["Color"], n.inputs["Color"]); L(n.outputs["Normal"], p.inputs["Normal"])
    if mat.get("pbr"):
        p.inputs["Metallic"].default_value = mat["metallic"]; p.inputs["Roughness"].default_value = mat["roughness"]
        mr = img(mat.get("mr"), True)
        if mr:  # glTF: G = roughness, B = metallic
            sep = nt.nodes.new("ShaderNodeSeparateColor"); L(mr.outputs["Color"], sep.inputs["Color"])
            L(mul(sep.outputs["Green"], float(mat["roughness"])), p.inputs["Roughness"]); L(mul(sep.outputs["Blue"], float(mat["metallic"])), p.inputs["Metallic"])
        e = mat.get("emissive") or [0, 0, 0]; et = img(mat.get("emissive_tex"))
        if max(e) > 0:
            p.inputs["Emission Color"].default_value = (*e, 1); p.inputs["Emission Strength"].default_value = 1
            if et: L(et.outputs["Color"], p.inputs["Emission Color"])
    elif "gloss" in mat:  # legacy: specular colour x spec map; glossiness (exponent/255) -> roughness
        sc = mat.get("spec_color", [1, 1, 1, 1]); sp = img(mat.get("spec"))
        p.inputs["Roughness"].default_value = 1 - 0.85 * mat["gloss"]
        p.inputs["Specular Tint"].default_value = (sc[0], sc[1], sc[2], 1)
        if sp:
            mx = nt.nodes.new("ShaderNodeMix"); mx.data_type = "RGBA"; mx.blend_type = "MULTIPLY"; mx.inputs["Factor"].default_value = 1
            L(sp.outputs["Color"], mx.inputs["A"]); mx.inputs["B"].default_value = (sc[0], sc[1], sc[2], 1); L(mx.outputs["Result"], p.inputs["Specular Tint"])
        p.inputs["Specular IOR Level"].default_value = 0.5 + 0.5 * mat.get("env", 0)
        # ponytail: env intensity only raises specular; no reflection probes; ceiling = shiny SL surfaces look matte
    if b["fullbright"]:
        if p.inputs["Base Color"].links: L(p.inputs["Base Color"].links[0].from_socket, p.inputs["Emission Color"])
        else: p.inputs["Emission Color"].default_value = (r, g, bl, 1)
        p.inputs["Emission Strength"].default_value = 0.8
    elif AMB > 0 and mode != "emissive" and not p.inputs["Emission Strength"].links and p.inputs["Emission Strength"].default_value == 0:
        # SL sky ambient: albedo x amblit, as emission so walls and roofs don't shade it (see amblit())
        if p.inputs["Base Color"].links: L(p.inputs["Base Color"].links[0].from_socket, p.inputs["Emission Color"])
        else: p.inputs["Emission Color"].default_value = (r, g, bl, 1)
        p.inputs["Emission Strength"].default_value = AMB
    # Opaque clothes must depth-test solid: always-HASHED let body BOM show through jeans/tops (Scentual90).
    if mode in ("none", "emissive"):
        m.blend_method = "OPAQUE"
    elif mode == "mask":
        m.blend_method = "CLIP"; m.alpha_threshold = float(mat.get("cutoff", 0.5))
    elif it and not has_alpha(it.image):
        m.blend_method = "OPAQUE"
    else:
        m.blend_method = "HASHED"
    return m

def arrays(b):
    nv, ni, o = b["nv"], b["ni"], b["offset"]
    P = np.frombuffer(BIN, np.float32, nv * 3, o); o += nv * 12
    N = np.frombuffer(BIN, np.float32, nv * 3, o); o += nv * 12
    T = np.frombuffer(BIN, np.float32, nv * 2, o); o += nv * 8
    return P, N, T, np.frombuffer(BIN, np.uint32, ni, o)

def inner_layers(bs, bones, sample=300):
    """Double-sided clothing (2026-10-04, David's shirt): the shirt's lining is a second face of the same mesh, the same
    surface with the winding reversed (normals inward, tinted grey). The SL viewer culls back faces, so from outside only the
    cloth shows and from inside only the lining. Cycles draws both, and the coplanar layers z-fight into grey blotches. A
    Backfacing -> Transparent shader can't fix it (the ray skips the coplanar front face and sees the lining across the body).
    So a batch (one face/material) is a lining, and is dropped, when most of its triangles face into the body (normal
    towards the nearest bone segment; scene-mesher exports the posed bone chain) AND most of a random sample has a coplanar
    opposite-facing twin within 0.5 mm. Hair cards (half each way), mouth/eye interiors (no twin) and the cloth stay.
    -> set of batch indices to drop. ponytail: whole batches only; ceiling = an open collar's inside shows the cloth colour"""
    if not bones: return set()
    from mathutils.bvhtree import BVHTree
    S = np.array(bones, np.float64).reshape(-1, 2, 3); A, D = S[:, 0], S[:, 1] - S[:, 0]; DD = np.maximum((D * D).sum(1), 1e-12)
    arr = [(k, *arrays(b)) for k, b in bs]
    V = np.vstack([P.reshape(-1, 3) for _, P, _, _, _ in arr]) if arr else np.zeros((0, 3))
    offs = np.cumsum([0] + [len(P) // 3 for _, P, _, _, _ in arr])
    F = [I.reshape(-1, 3) + o for (_, _, _, _, I), o in zip(arr, offs)]
    tree = None; drop = set(); rng = np.random.default_rng(1)
    for (k, *_), f in zip(arr, F):
        if len(f) < 8: continue
        tp = V[f]; g = np.cross(tp[:, 1] - tp[:, 0], tp[:, 2] - tp[:, 0]); c = tp.mean(1)
        t = np.clip(((c[:, None, :] - A[None]) * D[None]).sum(2) / DD[None], 0, 1)          # (tris, segments)
        near = A[None] + t[..., None] * D[None]; d2 = ((c[:, None, :] - near) ** 2).sum(2); q = near[np.arange(len(c)), d2.argmin(1)]
        inward = float(((g * (q - c)).sum(1) > 0).mean())
        if inward < 0.7: continue
        if tree is None: tree = BVHTree.FromPolygons(V.tolist(), np.vstack(F).tolist(), all_triangles=True)
        pick = rng.choice(len(f), min(sample, len(f)), replace=False); twin = 0
        for nn, cc in zip(g[pick], c[pick]):
            nn = Vector(nn).normalized(); twin += any(nn.dot(h[1]) < -0.5 for h in tree.find_nearest_range(Vector(cc), 5e-4))
        if twin / len(pick) >= 0.5: drop.add(k)
        if os.environ.get("GT_DBG"): log("  batch", M["batches"][k]["tex"][:8], round(M["batches"][k]["rgba"][0], 2), "inward", round(inward, 2), "twin", round(twin / len(pick), 2), "DROP" if k in drop else "")
    return drop

def build(group, bones=None):
    lo, hi = np.full(3, 1e9), np.full(3, -1e9); n = 0; obs = []
    mine = [(i, b) for i, b in enumerate(M["batches"]) if b["group"] == group and b["ni"] > 0]
    drop = set()
    if group.startswith("avatar"):
        t = time.time(); drop = inner_layers(mine, bones); log(group, "lining batches dropped", len(drop), "in", round(time.time() - t, 2), "s")
    min_tris = int(os.environ.get("GT_MIN_TRIS", "0"))  # look around: drop dust (nav still sees big obstacles)
    for bi, b in mine:
        if bi in drop: continue
        nv, ni = b["nv"], b["ni"]
        if min_tris and ni // 3 < min_tris and not b["group"].startswith("avatar"): continue
        P, N, T, I = arrays(b)
        me = bpy.data.meshes.new(b["tex"][:40]); nt = ni // 3
        me.vertices.add(nv); me.vertices.foreach_set("co", P)
        me.loops.add(ni); me.loops.foreach_set("vertex_index", I)
        me.polygons.add(nt); me.polygons.foreach_set("loop_start", np.arange(0, ni, 3, dtype=np.int32)); me.polygons.foreach_set("loop_total", np.full(nt, 3, np.int32))
        me.polygons.foreach_set("use_smooth", np.ones(nt, bool))
        uv = me.uv_layers.new(); uv.data.foreach_set("uv", T.reshape(-1, 2)[I].ravel())
        me.update(); me.normals_split_custom_set_from_vertices(N.reshape(-1, 3))
        ob = bpy.data.objects.new(me.name, me); sc.collection.objects.link(ob); me.materials.append(material(b))
        # see-through avatar parts (lashes, hair strands) cast no shadow: under the 0.6 m portrait area lights a lash
        # shadow drew a grey "text" mark beside her nose. The SL viewer's sun shadow map is far too coarse to resolve them.
        # ponytail: decided per texture alpha; ceiling = no hair shadow on her neck
        tn = next((n for n in me.materials[0].node_tree.nodes if n.type == "TEX_IMAGE" and n.image), None)
        # blank SL textures (32x32 all-alpha-0, e.g. Scentual f54a0c32 "clothing" layer): skip so they don't
        # sit as hashed ghosts over BOM skin
        if tn and _alpha_stats(tn.image)[1] < 1e-3:
            bpy.data.objects.remove(ob, do_unlink=True); bpy.data.meshes.remove(me); continue
        obs.append(ob)
        if group.startswith("avatar") and (b.get("mat") or {}).get("alpha", "auto") in ("auto", "blend") and not b["tex"].startswith("bake:") and tn and has_alpha(tn.image):
            ob.visible_shadow = False
        # the backdrop is seen and casts sun shadow, but takes no part in the near scene's bounce light or reflections:
        # the near scene lights exactly as before the backdrop (ponytail: no bounce light off far walls; ceiling = a sunlit
        # building across the water doesn't brighten her side of the deck)
        if group == "far": ob.visible_diffuse = False; ob.visible_glossy = False
        p3 = P.reshape(-1, 3); lo = np.minimum(lo, p3.min(0)); hi = np.maximum(hi, p3.max(0)); n += nt
    log(group, "triangles", n, "bbox", lo.round(2).tolist(), hi.round(2).tolist())
    return lo, hi, obs

def gpu_setup():
    if engine == "CYCLES":
        sc.render.engine = "CYCLES"; prefs = bpy.context.preferences.addons["cycles"].preferences
        try:
            prefs.compute_device_type = "OPTIX"; prefs.get_devices()
            for d in prefs.devices: d.use = d.type == "OPTIX"
            sc.cycles.device = "GPU" if any(d.type == "OPTIX" for d in prefs.devices) else "CPU"
        except Exception as e: log("optix unavailable", e); sc.cycles.device = "CPU"
        sc.cycles.samples = int(os.environ.get("GT_SAMPLES", "64"))
        sc.cycles.use_denoising = os.environ.get("GT_DENOISE", "1") != "0" and sc.cycles.samples >= 24
        sc.cycles.transparent_max_bounces = 8 if sc.cycles.samples < 24 else 16
        log("cycles device", sc.cycles.device)
    elif engine == "WORKBENCH":  # flat preview: textures, no lighting model (cheapest; CPU/llvmpipe friendly)
        sc.render.engine = "BLENDER_WORKBENCH"; sc.display.shading.color_type = "TEXTURE"; sc.display.shading.light = "STUDIO"
    else:
        sc.render.engine = "BLENDER_EEVEE_NEXT"; sc.eevee.taa_render_samples = int(os.environ.get("GT_SAMPLES", "16"))

def world(strength):
    w = bpy.data.worlds.new("w"); sc.world = w; w.use_nodes = True
    sky = w.node_tree.nodes.new("ShaderNodeTexSky"); sky.sky_type = "NISHITA"; sky.sun_elevation = math.radians(35); sky.sun_rotation = math.radians(200)
    w.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.25 * strength
    w.node_tree.links.new(sky.outputs["Color"], w.node_tree.nodes["Background"].inputs["Color"])

def camera(loc, target, lens, res):
    cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam")); sc.collection.objects.link(cam); sc.camera = cam
    cam.location = loc; cam.rotation_euler = (Vector(target) - Vector(loc)).to_track_quat("-Z", "Y").to_euler()
    cam.data.lens = lens; cam.data.clip_start = 0.02
    if os.environ.get("GT_RES"):
        res = tuple(int(x) for x in os.environ["GT_RES"].split("x"))
    sc.render.resolution_x, sc.render.resolution_y = res

def area(loc, target, energy, size):
    bpy.ops.object.light_add(type="AREA", location=loc); L = bpy.context.object; L.data.energy = energy; L.data.size = size
    L.rotation_euler = (Vector(target) - Vector(loc)).to_track_quat("-Z", "Y").to_euler()

def eep(env):
    """Region EEP sky at export time (make_job.py): sun or, below the horizon, moon; sky colour as ambient; SL point lights."""
    sun = Vector(env["sun_dir"]).normalized(); night = sun.z < 0
    travel = sun if night else -sun   # ponytail: moon taken as opposite the sun; ceiling = real moon_rotation
    c = env["sunlight"]; peak = max(max(c), 1e-3)
    bpy.ops.object.light_add(type="SUN"); L = bpy.context.object; L.rotation_euler = travel.to_track_quat("-Z", "Y").to_euler()
    L.data.color = [x / peak for x in c]; L.data.energy = (0.25 * env["moon"] if night else 1.2 * peak)
    w = bpy.data.worlds.new("w"); sc.world = w; w.use_nodes = True; bg = w.node_tree.nodes["Background"]
    bg.inputs["Color"].default_value = (*env["horizon"], 1); bg.inputs["Strength"].default_value = 0.35 if night else 1.0
    sky_backdrop(w, env, night)
    for l in M.get("lights", []):
        # SL point light, reimplemented from the LL viewer's deferred light shader: radiance = albedo x linear colour x
        # intensity x atten x N.L x 3.25/pi, atten = 2 (1 - clamp((d/radius + falloff) / (1 + falloff)))^2, zero past the
        # radius. Cycles: constant falloff (no inverse square), the curve on Light Path ray length (= distance);
        # 4 pi^2 cancels Cycles' point-light normalisation (calibrated: 1 W constant -> 1/(4 pi^2) on a white plane).
        d = bpy.data.lights.new("sl", "POINT"); d.color = [lin(c) for c in l["color"]]; d.shadow_soft_size = 0.05
        d.energy = 4 * math.pi ** 2 * 3.25 / math.pi; d.use_nodes = True; nt = d.node_tree; f = l.get("falloff", 0.75)
        def op(kind, a, b, clamp=False):
            n = nt.nodes.new("ShaderNodeMath"); n.operation = kind; n.use_clamp = clamp
            for i, v in enumerate((a, b)):
                if isinstance(v, (int, float)): n.inputs[i].default_value = v
                else: nt.links.new(v, n.inputs[i])
            return n.outputs[0]
        x = op("DIVIDE", nt.nodes.new("ShaderNodeLightPath").outputs["Ray Length"], max(l["radius"], 0.01))
        k = op("SUBTRACT", 1.0, op("DIVIDE", op("ADD", x, f), 1 + f, clamp=True))
        fo = nt.nodes.new("ShaderNodeLightFalloff"); nt.links.new(op("MULTIPLY", op("POWER", k, 2.0), 2 * l["intensity"]), fo.inputs["Strength"])
        nt.links.new(fo.outputs["Constant"], nt.nodes["Emission"].inputs["Strength"])
        o = bpy.data.objects.new("sl", d); o.location = l["pos"]; sc.collection.objects.link(o)
    log("eep", "night" if night else "day", "ambient", round(AMB, 4), "sun", [round(x, 2) for x in sun], "lights", len(M.get("lights", [])))

def sky_backdrop(w, env, night):
    """What the camera (and water reflections) see of the sky: the EEP horizon colour at the horizon fading to the sky's
    blue_density colour overhead, scaled to the horizon's full brightness (SL's legacy sky model, much simplified; at
    0.85 of it the upper sky read as a darker picture). Diffuse
    lighting still comes from the flat horizon colour it always used, so nothing in the scene changes brightness.
    ponytail: no clouds, sun disc, haze glow or neighbouring regions; ceiling = a clean but plain gradient"""
    if "zenith" not in env or not M.get("backdrop", True): return   # look.py without --far: the flat horizon colour
    nt = w.node_tree; L = nt.links.new; bg = nt.nodes["Background"]
    h = env["horizon"]; z = env["zenith"]; k = max(sum(h), 1e-3) / max(sum(z), 1e-3)
    co = nt.nodes.new("ShaderNodeTexCoord"); sp = nt.nodes.new("ShaderNodeSeparateXYZ"); L(co.outputs["Generated"], sp.inputs[0])
    mr = nt.nodes.new("ShaderNodeMapRange"); mr.interpolation_type = "SMOOTHSTEP"; mr.inputs["From Min"].default_value = 0.0; mr.inputs["From Max"].default_value = 0.45
    L(sp.outputs["Z"], mr.inputs["Value"])
    mx = nt.nodes.new("ShaderNodeMix"); mx.data_type = "RGBA"; L(mr.outputs["Result"], mx.inputs["Factor"])
    mx.inputs["A"].default_value = (*h, 1); mx.inputs["B"].default_value = (*[min(1.0, c * k) for c in z], 1)
    seen = nt.nodes.new("ShaderNodeBackground"); L(mx.outputs["Result"], seen.inputs["Color"]); seen.inputs["Strength"].default_value = bg.inputs["Strength"].default_value
    lp = nt.nodes.new("ShaderNodeLightPath"); mm = nt.nodes.new("ShaderNodeMath"); mm.operation = "MAXIMUM"
    L(lp.outputs["Is Camera Ray"], mm.inputs[0]); L(lp.outputs["Is Glossy Ray"], mm.inputs[1])
    ms = nt.nodes.new("ShaderNodeMixShader"); L(mm.outputs[0], ms.inputs["Fac"]); L(bg.outputs[0], ms.inputs[1]); L(seen.outputs[0], ms.inputs[2])
    L(ms.outputs[0], nt.nodes["World Output"].inputs["Surface"])

def water():
    """The region's water plane (export: water_height), 4 km across so it meets the sky at the horizon. Shaded without
    tracing: the sky gradient looked up along the reflected view ray, weighted by a Fresnel-like facing term, over a dark
    sea colour, as emission (a traced glossy plane cost ~3 s of the ~20 s budget). ponytail: no waves, no reflections of
    objects or of the sun; ceiling = a calm, matte-mirror sea"""
    wh = M.get("water_height"); env = M.get("env") or {}
    if wh is None: return
    bpy.ops.mesh.primitive_plane_add(size=4096, location=(128, 128, wh)); ob = bpy.context.object; ob.name = "water"
    m = bpy.data.materials.new("water"); m.use_nodes = True; nt = m.node_tree; L = nt.links.new
    for n in list(nt.nodes):
        if n.type != "OUTPUT_MATERIAL": nt.nodes.remove(n)
    h = env.get("horizon", [0.5, 0.5, 0.6]); z = env.get("zenith", h); k = max(sum(h), 1e-3) / max(sum(z), 1e-3)
    night = (env.get("sun_dir") or [0, 0, 1])[2] < 0; s = 0.35 if night else 1.0
    co = nt.nodes.new("ShaderNodeTexCoord"); sp = nt.nodes.new("ShaderNodeSeparateXYZ"); L(co.outputs["Reflection"], sp.inputs[0])
    mr = nt.nodes.new("ShaderNodeMapRange"); mr.interpolation_type = "SMOOTHSTEP"; mr.inputs["From Max"].default_value = 0.45; L(sp.outputs["Z"], mr.inputs["Value"])
    sky = nt.nodes.new("ShaderNodeMix"); sky.data_type = "RGBA"; L(mr.outputs["Result"], sky.inputs["Factor"])
    sky.inputs["A"].default_value = (*h, 1); sky.inputs["B"].default_value = (*[min(1.0, c * k) for c in z], 1)
    lw = nt.nodes.new("ShaderNodeLayerWeight"); lw.inputs["Blend"].default_value = 0.35
    mx = nt.nodes.new("ShaderNodeMix"); mx.data_type = "RGBA"; L(lw.outputs["Fresnel"], mx.inputs["Factor"])
    # sea body = the horizon colour at ~80%, a little bluer (was a near-black (0.02, 0.05, 0.07): the sea fills a third of
    # most frames where the pre-backdrop render showed the flat horizon colour, so the picture read as darker, David 2026-10-04)
    mx.inputs["A"].default_value = (h[0] * 0.72, h[1] * 0.8, h[2] * 0.9, 1); L(sky.outputs["Result"], mx.inputs["B"])
    em = nt.nodes.new("ShaderNodeEmission"); L(mx.outputs["Result"], em.inputs["Color"]); em.inputs["Strength"].default_value = s
    L(em.outputs[0], nt.nodes["Material Output"].inputs["Surface"])
    ob.data.materials.append(m); ob.visible_shadow = False; ob.visible_diffuse = False; ob.visible_glossy = False

def terrain():
    # ponytail: one flat green material, a 4 m grid, this region only; a point with no land data sits at 20 m;
    # ceiling = no terrain textures, no neighbouring regions
    t = json.load(open(f"{jobdir}/terrain.json")); nx, ny, st = t["nx"], t["ny"], t["step"]
    if nx < 2: return
    verts = [(t.get("x0", 0) + i * st, t.get("y0", 0) + j * st, t["heights"][j][i] if t["heights"][j][i] is not None else 20.0) for j in range(ny) for i in range(nx)]
    faces = [(j * nx + i, j * nx + i + 1, (j + 1) * nx + i + 1, (j + 1) * nx + i) for j in range(ny - 1) for i in range(nx - 1)]
    me = bpy.data.meshes.new("terrain"); me.from_pydata(verts, [], faces); ob = bpy.data.objects.new("terrain", me); sc.collection.objects.link(ob)
    ob.visible_diffuse = False; ob.visible_glossy = False   # backdrop only, like the far prims (see build)
    m = bpy.data.materials.new("ground"); m.use_nodes = True; m.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.24, 0.30, 0.14, 1); me.materials.append(m)

gpu_setup(); views = []

def stand_in(o):
    """Bake-textured capsule+head for an avatar whose attachments never reached the export (awareness, not polish)."""
    import os
    pref = o.get("bake_prefix") or (o.get("agent_id") or "")[:8]
    def tex(name):
        path = f"{jobdir}/tex/bake-{pref}-{name}.png" if pref else None
        if path and os.path.exists(path): return path
        path = f"{jobdir}/tex/bake-{name}.png"
        return path if os.path.exists(path) else None
    obs = []
    # body capsule from pelvis to neck using bone chain if present
    bones = o.get("bones") or []
    z0, z1 = 0.05, 1.4
    if bones:
        zs = [b[2] for b in bones] + [b[5] for b in bones]
        z0, z1 = min(zs), max(zs) - 0.25
    bpy.ops.mesh.primitive_cylinder_add(radius=0.18, depth=max(0.4, z1 - z0), location=(0, 0, (z0 + z1) / 2))
    body = bpy.context.object; body.name = f"standin-body-{pref}"
    # solid clothing colour (not the skin bake): a skin-textured capsule reads as nude
    mat = bpy.data.materials.new(f"standin-upper-{pref}"); mat.use_nodes = True
    nt = mat.node_tree; nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial"); bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
    nt.links.new(bsdf.outputs[0], out.inputs[0])
    bsdf.inputs["Base Color"].default_value = (0.22, 0.28, 0.45, 1)  # muted shirt blue
    body.data.materials.append(mat); obs.append(body)
    # head sphere at mHead
    hx, hy, hz = o.get("head") or [0, 0, 1.7]
    bpy.ops.mesh.primitive_uv_sphere_add(radius=0.12, location=(hx, hy, hz))
    head = bpy.context.object; head.name = f"standin-head-{pref}"
    mat2 = bpy.data.materials.new(f"standin-head-{pref}"); mat2.use_nodes = True
    nt2 = mat2.node_tree; nt2.nodes.clear()
    out2 = nt2.nodes.new("ShaderNodeOutputMaterial"); bsdf2 = nt2.nodes.new("ShaderNodeBsdfPrincipled")
    nt2.links.new(bsdf2.outputs[0], out2.inputs[0])
    tp2 = tex("head")
    if tp2:
        img2 = bpy.data.images.load(tp2); texn2 = nt2.nodes.new("ShaderNodeTexImage"); texn2.image = img2
        nt2.links.new(texn2.outputs[0], bsdf2.inputs["Base Color"])
    else:
        bsdf2.inputs["Base Color"].default_value = (0.55, 0.45, 0.38, 1)
    head.data.materials.append(mat2); obs.append(head)
    lo = np.array([-0.2, -0.2, z0]); hi = np.array([0.2, 0.2, hz + 0.12])
    log("stand-in", o.get("name"), "bake", pref)
    return lo, hi, obs

if kind == "scene":
    build("scene"); build("far"); terrain(); water()
    g = M["me"]; av = os.environ.get("GT_AV")  # "x,y,yaw_deg": stand/sit her there, on the first surface below
    x, y, yaw = [float(v) for v in av.split(",")] if av else (g[0], g[1], M.get("me_yaw", -90.0))
    bpy.context.view_layer.update(); dg = bpy.context.evaluated_depsgraph_get()
    def floor_at(px, py, pz):  # first surface below an agent position (cast before any avatar is built)
        hit, loc, *_ = sc.ray_cast(dg, Vector((px, py, pz + 0.5)), Vector((0, 0, -1)))
        return loc.z if hit else pz - 1.15
    # avatar height: the LL viewer puts mesh z 0 at agent z + agent_off (scene-mesher, from bodysize, 2026-10-04+). Our bodysize
    # runs ~0.2 m short of the sim's (standing agents come out ~0.2 m above the floor; unverified why), so a standing avatar
    # (LL estimate within 0.35 m of the floor under her) keeps its lowest vertex on that floor; otherwise (sitting, hovering,
    # a seat or table under her) the LL placement wins instead of standing her on whatever the cast hit. GT_AV (moved
    # elsewhere) or an old mesh.json -> floor cast. ponytail: hover isn't exported (0); ceiling = sitting heights ~0.2 m high
    def place(o, px, py, pz):  # -> (floor under it, LL mesh z0 or None); cast before any avatar is built
        return floor_at(px, py, pz), (pz + o["agent_off"] if o.get("agent_off") is not None else None)
    def z0_of(fl, ll, lo):
        return fl - lo if ll is None or abs(ll + lo - fl) < 0.35 else ll
    placed = [(o, place(o, *o["pos"])) for o in M.get("others", [])]
    fl, ll = place(M, x, y, g[2]); ll = None if av else ll
    lo, hi, obs = build("avatar", M.get("bones")); z0 = z0_of(fl, ll, lo[2])
    for ob in obs: ob.location = (x, y, z0); ob.rotation_euler = (0, 0, math.radians(yaw))
    log("avatar at", round(x, 2), round(y, 2), "mesh z0", round(z0, 2), "floor", round(fl, 2), "LL", ll and round(ll, 2))
    def to_world(local, at, yw):
        c, s_ = math.cos(math.radians(yw)), math.sin(math.radians(yw))
        return Vector((at[0] + c * local[0] - s_ * local[1], at[1] + s_ * local[0] + c * local[1], at[2] + local[2]))
    heads = {"me": to_world(M["head"], (x, y, z0), yaw) + Vector((0, 0, 0.07))}
    for o, fp in placed:
        # attachments missing from the export -> bake-textured stand-in (not a nude mesh body)
        if o.get("placeholder") or not any(b["group"] == o["group"] and b["ni"] > 0 for b in M["batches"]):
            olo, _, oobs = stand_in(o)
        else:
            olo, _, oobs = build(o["group"], o.get("bones"))
        at = (o["pos"][0], o["pos"][1], z0_of(*fp, olo[2]))
        for ob in oobs: ob.location = at; ob.rotation_euler = (0, 0, math.radians(o["yaw"]))
        heads[o["name"]] = to_world(o["head"], at, o["yaw"]) + Vector((0, 0, 0.07)); log("other avatar", o["name"], "at", [round(v, 2) for v in at])
    if os.environ.get("GT_NOSKY") == "1":
        # lean look-around: flat horizon, one sun; no sky gradient / water reflections work
        bpy.ops.object.light_add(type="SUN", rotation=(math.radians(50), 0, math.radians(200))); bpy.context.object.data.energy = 2.5
        w = bpy.data.worlds.new("w"); sc.world = w; w.use_nodes = True
        bg = w.node_tree.nodes["Background"]; bg.inputs["Color"].default_value = (0.45, 0.55, 0.7, 1); bg.inputs["Strength"].default_value = 0.8
    elif M.get("env"): eep(M["env"])
    else:
        bpy.ops.object.light_add(type="SUN", rotation=(math.radians(50), 0, math.radians(200))); bpy.context.object.data.energy = 3.5
        world(1.0)
    cam = os.environ.get("GT_CAM")  # "x,y,z,tx,ty,tz[,lens]" in region coordinates
    # look: GT_VIEW = ";"-separated views, one image each: "eye[:yaw offset deg]" over her shoulder, "at:<avatar name>|x,y,z"
    views = [v for v in os.environ.get("GT_VIEW", "").split(";") if v]
    def set_view(view):
        h = heads["me"]
        def shoulder(f, back, side, up, target, lens):
            """over her right shoulder; a wall or chimney behind her would fill the frame, so stop in front of the first
            surface between her head and the camera (as the SL camera does)"""
            want = h - f * back + f.cross(Vector((0, 0, 1))) * side + Vector((0, 0, up)); d = want - h; u = d.normalized()
            bpy.context.view_layer.update()
            hit, loc, *_ = sc.ray_cast(bpy.context.evaluated_depsgraph_get(), h + u * 0.35, u, distance=d.length - 0.35)
            camera(loc - u * 0.15 if hit else want, target, lens, (960, 540))
        if view.startswith("at:"):
            t = view[3:]; tgt = heads.get(t) or Vector([float(v) for v in t.split(",")])
            f = (tgt - h); f.z = 0; f = f.normalized() if f.length > 1e-3 else Vector((1, 0, 0))
            shoulder(f, 1.5, 0.45, 0.3, tgt, 35)
        else:
            a = math.radians(yaw + (float(view[4:]) if view.startswith("eye:") else 0)); f = Vector((math.cos(a), math.sin(a), 0))
            shoulder(f, 2.2, 0.5, 0.35, h + f * 6 - Vector((0, 0, 0.5)), 24)
        log("view", view, "camera from", [round(v, 2) for v in sc.camera.location])
    if views: set_view(views[0])
    elif cam:
        c = [float(v) for v in cam.split(",")]; camera(c[0:3], c[3:6], c[6] if len(c) > 6 else 24, (960, 540))
    else:
        camera((g[0] - 1.5, g[1] - 7.5, g[2] + 2.2), (g[0] + 0.5, g[1] + 3.5, g[2] + 0.3), 24, (960, 540))
else:
    lo, hi, _ = build("avatar", M.get("bones"))
    # face: the mHead joint (base of the skull) + ~7 cm up to the eyes; older mesh.json: from the avatar's top
    head = np.array(M["head"]) + (0, 0, 0.07) if "head" in M else np.array([0.0, 0.0, hi[2] - 0.13])
    if kind == "face":
        camera(head + (0.85, 0.0, 0.0), head + (0, 0, -0.01), 85, (768, 768))
        area(head + (0.6, 0.5, 0.25), head, 12, 0.6); area(head + (0.6, -0.6, 0.0), head, 5, 0.8); area(head + (-0.5, 0.0, 0.4), head, 8, 0.5)
    else:
        mid = (lo + hi) / 2; h = hi[2] - lo[2]
        camera(mid + (h * 1.9, 0.0, 0.0), mid, 50, (640, 1024))
        area(mid + (2.0, 1.5, 1.0), mid, 120, 2.0); area(mid + (2.0, -2.0, 0.0), mid, 50, 2.5); area(mid + (-1.5, 0.0, 1.5), mid, 60, 1.5)
    world(0.5)
sc.view_settings.view_transform = "Standard"  # SL shows textures as plain sRGB
sc.view_settings.exposure = float(os.environ.get("GT_EXPOSURE", "0"))
sc.render.image_settings.file_format = "JPEG"; sc.render.image_settings.quality = 90
log("build seconds", round(time.time() - T0, 1))
views = views or [None]
# keep the synced scene (BVH, ~1 GB of images with the backdrop) between the views of one job: each view re-synced it,
# 2-3 s a view with the backdrop's textures (David 2026-10-04: keep `look around` near its pre-backdrop time)
sc.render.use_persistent_data = len(views) > 1
for i, v in enumerate(views):
    if i: set_view(v)
    sc.render.filepath = out if len(views) == 1 else out.replace(".jpg", f"-{i}.jpg")
    t = time.time(); bpy.ops.render.render(write_still=True); log("render seconds", round(time.time() - t, 1), sc.render.filepath)
