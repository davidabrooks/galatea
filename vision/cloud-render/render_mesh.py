"""Blender script: render real SL geometry from scene-mesher output (mesh.json + mesh.bin).

blender -b --factory-startup --python render_mesh.py -- <jobdir> {scene|face|body} {CYCLES|EEVEE} out.jpg
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

def material(b):
    key = b["tex"]; m = bpy.data.materials.new(key[:50]); m.use_nodes = True
    nt = m.node_tree; p = nt.nodes["Principled BSDF"]; r, g, bl, a = b["rgba"]
    p.inputs["Base Color"].default_value = (r, g, bl, 1); p.inputs["Roughness"].default_value = 0.6
    path = f"{jobdir}/tex/{key.replace('bake:', 'bake-')}.png"
    if os.path.exists(path):
        it = nt.nodes.new("ShaderNodeTexImage"); it.image = bpy.data.images.load(path, check_existing=True)
        mix = nt.nodes.new("ShaderNodeMix"); mix.data_type = "RGBA"; mix.blend_type = "MULTIPLY"; mix.inputs["Factor"].default_value = 1
        nt.links.new(it.outputs["Color"], mix.inputs["A"]); mix.inputs["B"].default_value = (r, g, bl, 1)
        nt.links.new(mix.outputs["Result"], p.inputs["Base Color"])
        if a < 0.999:
            mul = nt.nodes.new("ShaderNodeMath"); mul.operation = "MULTIPLY"; mul.inputs[1].default_value = a
            nt.links.new(it.outputs["Alpha"], mul.inputs[0]); nt.links.new(mul.outputs[0], p.inputs["Alpha"])
        else:
            nt.links.new(it.outputs["Alpha"], p.inputs["Alpha"])
        # ponytail: every texture alpha is treated as blend/hashed; SL alpha modes (mask/emissive) from materials aren't exported
    elif a < 0.999:
        p.inputs["Alpha"].default_value = a
    if b["fullbright"]:
        if p.inputs["Base Color"].links: nt.links.new(p.inputs["Base Color"].links[0].from_socket, p.inputs["Emission Color"])
        else: p.inputs["Emission Color"].default_value = (r, g, bl, 1)
        p.inputs["Emission Strength"].default_value = 0.8
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
    else:
        sc.render.engine = "BLENDER_EEVEE_NEXT"; sc.eevee.taa_render_samples = 16

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

def terrain():
    t = json.load(open(f"{jobdir}/terrain.json")); nx, ny, st = t["nx"], t["ny"], t["step"]
    if nx < 2: return
    verts = [(t["x0"] + i * st, t["y0"] + j * st, t["heights"][j][i] if t["heights"][j][i] is not None else 20.0) for j in range(ny) for i in range(nx)]
    faces = [(j * nx + i, j * nx + i + 1, (j + 1) * nx + i + 1, (j + 1) * nx + i) for j in range(ny - 1) for i in range(nx - 1)]
    me = bpy.data.meshes.new("terrain"); me.from_pydata(verts, [], faces); ob = bpy.data.objects.new("terrain", me); sc.collection.objects.link(ob)
    m = bpy.data.materials.new("ground"); m.use_nodes = True; m.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.24, 0.30, 0.14, 1); me.materials.append(m)

gpu_setup()
if kind == "scene":
    build("scene"); terrain()
    g = M["me"]
    lo, hi, obs = build("avatar")
    # ponytail: she stands in T-pose at her spot facing the camera, feet ~1.15 m below her agent position (she may be
    # seated; posing needs the skeleton + animations); ceiling = posture and exact placement
    for ob in obs: ob.location = (g[0], g[1], g[2] - 1.15 - lo[2]); ob.rotation_euler = (0, 0, math.radians(-90))
    bpy.ops.object.light_add(type="SUN", rotation=(math.radians(50), 0, math.radians(200))); bpy.context.object.data.energy = 3.5
    world(1.0)
    cam = os.environ.get("GT_CAM")  # "x,y,z,tx,ty,tz[,lens]" in region coordinates
    if cam:
        c = [float(v) for v in cam.split(",")]; camera(c[0:3], c[3:6], c[6] if len(c) > 6 else 24, (960, 540))
    else:
        camera((g[0] - 1.5, g[1] - 7.5, g[2] + 2.2), (g[0] + 0.5, g[1] + 3.5, g[2] + 0.3), 24, (960, 540))
else:
    lo, hi, _ = build("avatar")
    head = np.array([0.0, 0.0, hi[2] - 0.13])  # ponytail: head centre from the avatar's top; ceiling = hats/tall hair shift it
    if kind == "face":
        camera(head + (0.85, 0.0, 0.0), head + (0, 0, -0.01), 85, (768, 768))
        area(head + (0.6, 0.5, 0.25), head, 12, 0.6); area(head + (0.6, -0.6, 0.0), head, 5, 0.8); area(head + (-0.5, 0.0, 0.4), head, 8, 0.5)
    else:
        mid = np.array([0.0, 0.0, (lo[2] + hi[2]) / 2]); h = hi[2] - lo[2]
        camera(mid + (h * 1.9, 0.0, 0.0), mid, 50, (640, 1024))
        area(mid + (2.0, 1.5, 1.0), mid, 120, 2.0); area(mid + (2.0, -2.0, 0.0), mid, 50, 2.5); area(mid + (-1.5, 0.0, 1.5), mid, 60, 1.5)
    world(0.5)
sc.view_settings.view_transform = "Standard"  # SL shows textures as plain sRGB
sc.render.image_settings.file_format = "JPEG"; sc.render.image_settings.quality = 90; sc.render.filepath = out
log("build seconds", round(time.time() - T0, 1))
t = time.time(); bpy.ops.render.render(write_still=True); log("render seconds", round(time.time() - t, 1))
