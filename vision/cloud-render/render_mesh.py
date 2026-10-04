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
ALPHA_CACHE = {}
def has_alpha(image):
    """any texel below 0.98 alpha (the image is loaded anyway; one numpy pass per distinct texture)"""
    if image.name not in ALPHA_CACHE:
        a = np.empty(len(image.pixels), np.float32); image.pixels.foreach_get(a)
        ALPHA_CACHE[image.name] = image.channels == 4 and a.size > 0 and float(a[3::4].min()) < 0.98
    return ALPHA_CACHE[image.name]

def material(b):
    """SL face -> Principled BSDF. alpha: none | blend | mask (cutoff) | emissive (alpha = glow mask) | auto (texture alpha)."""
    key = b["tex"]; mat = b.get("mat") or {"alpha": "auto"}; m = bpy.data.materials.new(key[:50]); m.use_nodes = True
    nt = m.node_tree; p = nt.nodes["Principled BSDF"]; r, g, bl, a = b["rgba"]; L = nt.links.new
    def img(uuid, data=False):
        path = f"{jobdir}/tex/{uuid.replace('bake:', 'bake-')}.png" if uuid else None
        if not path or not os.path.exists(path): return None
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
    m.blend_method = "HASHED"
    return m

def build(group):
    lo, hi = np.full(3, 1e9), np.full(3, -1e9); n = 0; obs = []
    for b in M["batches"]:
        if b["group"] != group or b["ni"] == 0: continue
        nv, ni, o = b["nv"], b["ni"], b["offset"]
        P = np.frombuffer(BIN, np.float32, nv * 3, o); o += nv * 12
        N = np.frombuffer(BIN, np.float32, nv * 3, o); o += nv * 12
        T = np.frombuffer(BIN, np.float32, nv * 2, o); o += nv * 8
        I = np.frombuffer(BIN, np.uint32, ni, o)
        me = bpy.data.meshes.new(b["tex"][:40]); nt = ni // 3
        me.vertices.add(nv); me.vertices.foreach_set("co", P)
        me.loops.add(ni); me.loops.foreach_set("vertex_index", I)
        me.polygons.add(nt); me.polygons.foreach_set("loop_start", np.arange(0, ni, 3, dtype=np.int32)); me.polygons.foreach_set("loop_total", np.full(nt, 3, np.int32))
        me.polygons.foreach_set("use_smooth", np.ones(nt, bool))
        uv = me.uv_layers.new(); uv.data.foreach_set("uv", T.reshape(-1, 2)[I].ravel())
        me.update(); me.normals_split_custom_set_from_vertices(N.reshape(-1, 3))
        ob = bpy.data.objects.new(me.name, me); sc.collection.objects.link(ob); me.materials.append(material(b)); obs.append(ob)
        # see-through avatar parts (lashes, hair strands) cast no shadow: under the 0.6 m portrait area lights a lash
        # shadow drew a grey "text" mark beside her nose. The SL viewer's sun shadow map is far too coarse to resolve them.
        # ponytail: decided per texture alpha; ceiling = no hair shadow on her neck
        tn = next((n for n in me.materials[0].node_tree.nodes if n.type == "TEX_IMAGE" and n.image), None)
        if group.startswith("avatar") and (b.get("mat") or {}).get("alpha", "auto") in ("auto", "blend") and not b["tex"].startswith("bake:") and tn and has_alpha(tn.image):
            ob.visible_shadow = False
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
        sc.cycles.samples = int(os.environ.get("GT_SAMPLES", "64")); sc.cycles.use_denoising = True
        sc.cycles.transparent_max_bounces = 16
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
    cam.data.lens = lens; cam.data.clip_start = 0.02; sc.render.resolution_x, sc.render.resolution_y = res

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

def terrain():
    t = json.load(open(f"{jobdir}/terrain.json")); nx, ny, st = t["nx"], t["ny"], t["step"]
    if nx < 2: return
    verts = [(t["x0"] + i * st, t["y0"] + j * st, t["heights"][j][i] if t["heights"][j][i] is not None else 20.0) for j in range(ny) for i in range(nx)]
    faces = [(j * nx + i, j * nx + i + 1, (j + 1) * nx + i + 1, (j + 1) * nx + i) for j in range(ny - 1) for i in range(nx - 1)]
    me = bpy.data.meshes.new("terrain"); me.from_pydata(verts, [], faces); ob = bpy.data.objects.new("terrain", me); sc.collection.objects.link(ob)
    m = bpy.data.materials.new("ground"); m.use_nodes = True; m.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.24, 0.30, 0.14, 1); me.materials.append(m)

gpu_setup(); views = []
if kind == "scene":
    build("scene"); terrain()
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
    lo, hi, obs = build("avatar"); z0 = z0_of(fl, ll, lo[2])
    for ob in obs: ob.location = (x, y, z0); ob.rotation_euler = (0, 0, math.radians(yaw))
    log("avatar at", round(x, 2), round(y, 2), "mesh z0", round(z0, 2), "floor", round(fl, 2), "LL", ll and round(ll, 2))
    def to_world(local, at, yw):
        c, s_ = math.cos(math.radians(yw)), math.sin(math.radians(yw))
        return Vector((at[0] + c * local[0] - s_ * local[1], at[1] + s_ * local[0] + c * local[1], at[2] + local[2]))
    heads = {"me": to_world(M["head"], (x, y, z0), yaw) + Vector((0, 0, 0.07))}
    for o, fp in placed:
        olo, _, oobs = build(o["group"]); at = (o["pos"][0], o["pos"][1], z0_of(*fp, olo[2]))
        for ob in oobs: ob.location = at; ob.rotation_euler = (0, 0, math.radians(o["yaw"]))
        heads[o["name"]] = to_world(o["head"], at, o["yaw"]) + Vector((0, 0, 0.07)); log("other avatar", o["name"], "at", [round(v, 2) for v in at])
    if M.get("env"): eep(M["env"])
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
    lo, hi, _ = build("avatar")
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
for i, v in enumerate(views):
    if i: set_view(v)
    sc.render.filepath = out if len(views) == 1 else out.replace(".jpg", f"-{i}.jpg")
    t = time.time(); bpy.ops.render.render(write_still=True); log("render seconds", round(time.time() - t, 1), sc.render.filepath)
