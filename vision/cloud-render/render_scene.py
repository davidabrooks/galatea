"""Blender script: build an approximate SL scene (or an approximate portrait) and render it.

blender -b --factory-startup --python render_scene.py -- scene.json texdir {scene|face} {CYCLES|EEVEE} out.jpg
Approximations (first test): every object is an oriented box at its bounding size with one representative
texture (box-projected), trees/grass/water get simple stand-ins, terrain is a height grid. The face render
is a procedural stand-in head (the real EvoX mesh and BoM bakes are not available headlessly yet).
"""
import bpy, json, math, os, sys, time

argv = sys.argv[sys.argv.index("--") + 1:]
scene_path, texdir, kind, engine, out = argv[:5]
S = json.load(open(scene_path))
T0 = time.time()
bpy.ops.wm.read_factory_settings(use_empty=True)
sc = bpy.context.scene

def log(*a): print("GT:", *a, flush=True)

def mat(name, color=(0.6, 0.6, 0.6, 1), tex=None, rough=0.7, alpha_from_tex=False, emit=0.0, sss=0.0, metal=0.0):
    m = bpy.data.materials.new(name); m.use_nodes = True
    nt = m.node_tree; b = nt.nodes["Principled BSDF"]
    b.inputs["Base Color"].default_value = color; b.inputs["Roughness"].default_value = rough
    b.inputs["Metallic"].default_value = metal
    if sss: b.inputs["Subsurface Weight"].default_value = sss; b.inputs["Subsurface Radius"].default_value = (1.0, 0.35, 0.2)
    if tex and os.path.exists(tex):
        it = nt.nodes.new("ShaderNodeTexImage"); it.image = bpy.data.images.load(tex); it.projection = "BOX"; it.projection_blend = 0.2
        tc = nt.nodes.new("ShaderNodeTexCoord"); nt.links.new(tc.outputs["Generated"], it.inputs["Vector"])
        nt.links.new(it.outputs["Color"], b.inputs["Base Color"])
        if alpha_from_tex:
            nt.links.new(it.outputs["Alpha"], b.inputs["Alpha"]); m.blend_method = "HASHED"
    if emit: b.inputs["Emission Color"].default_value = color; b.inputs["Emission Strength"].default_value = emit
    return m

def add(obj, m):
    obj.data.materials.append(m); return obj

def gpu_setup():
    if engine == "CYCLES":
        sc.render.engine = "CYCLES"
        prefs = bpy.context.preferences.addons["cycles"].preferences
        chosen = None
        for dev_type in ("OPTIX", "CUDA"):
            try:
                prefs.compute_device_type = dev_type; prefs.get_devices()
                devs = [d for d in prefs.devices if d.type == dev_type]
                if devs:
                    for d in prefs.devices: d.use = d.type == dev_type
                    chosen = dev_type; log("cycles devices", dev_type, [d.name for d in devs]); break
            except Exception as e:
                log("cycles", dev_type, "unavailable:", e)
        sc.cycles.device = "GPU" if chosen else "CPU"
        log("cycles device:", sc.cycles.device, chosen)
        sc.cycles.samples = int(os.environ.get("GT_SAMPLES", "48")); sc.cycles.use_denoising = True
        try: sc.cycles.denoiser = "OPTIX" if chosen == "OPTIX" else "OPENIMAGEDENOISE"
        except Exception: pass
    else:
        sc.render.engine = "BLENDER_EEVEE_NEXT"
        sc.eevee.taa_render_samples = 16

def world(strength=1.0):
    w = bpy.data.worlds.new("w"); sc.world = w; w.use_nodes = True
    sky = w.node_tree.nodes.new("ShaderNodeTexSky"); sky.sky_type = "NISHITA"; sky.sun_elevation = math.radians(35); sky.sun_rotation = math.radians(200)
    bg = w.node_tree.nodes["Background"]; bg.inputs["Strength"].default_value = 0.25 * strength
    w.node_tree.links.new(sky.outputs["Color"], bg.inputs["Color"])

def look_at(cam, target):
    from mathutils import Vector
    d = Vector(target) - cam.location; cam.rotation_euler = d.to_track_quat("-Z", "Y").to_euler()

def build_scene():
    tr = S["terrain"]; nx, ny, st = tr["nx"], tr["ny"], tr["step"]
    verts, faces = [], []
    for j in range(ny):
        for i in range(nx):
            h = tr["heights"][j][i]; verts.append((tr["x0"] + i * st, tr["y0"] + j * st, h if h is not None else 20.0))
    for j in range(ny - 1):
        for i in range(nx - 1):
            a = j * nx + i; faces.append((a, a + 1, a + nx + 1, a + nx))
    me = bpy.data.meshes.new("terrain"); me.from_pydata(verts, [], faces)
    t = bpy.data.objects.new("terrain", me); sc.collection.objects.link(t)
    add(t, mat("ground", (0.24, 0.30, 0.14, 1), rough=0.95)); [p.__setattr__("use_smooth", True) for p in me.polygons]
    n = 0
    for o in S["objects"]:
        name = o["name"].lower(); (x, y, z), (sx, sy, sz) = o["pos"], o["size"]
        if max(sx, sy, sz) < 0.15: continue
        tex = os.path.join(texdir, o["texture"] + ".png") if o.get("texture") else None
        if "tree" in name:
            bpy.ops.mesh.primitive_cylinder_add(radius=0.25, depth=sz * 0.5, location=(x, y, z - sz * 0.25)); add(bpy.context.object, mat("trunk", (0.25, 0.16, 0.09, 1)))
            bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=2, radius=1, location=(x, y, z + sz * 0.15))
            c = bpy.context.object; c.scale = (sx / 2, sy / 2, sz * 0.4); add(c, mat("leaf", (0.13, 0.30, 0.10, 1), rough=0.9)); n += 2; continue
        bpy.ops.mesh.primitive_cube_add(size=1, location=(x, y, z), rotation=(0, 0, math.radians(o["yaw_deg"])))
        c = bpy.context.object; c.scale = (max(sx, 0.02), max(sy, 0.02), max(sz, 0.02))
        if "water" in name:
            c.scale = (sx, sy, 0.05); add(c, mat("water", (0.05, 0.18, 0.25, 1), rough=0.05))
        elif "grass" in name or "flower" in name:
            c.scale = (sx, sy, min(sz, 0.3)); add(c, mat("grass", (0.20, 0.38, 0.12, 1), tex=tex, rough=0.9))
        else:
            add(c, mat("m", tex=tex, alpha_from_tex=sx * sy * sz < 0.05 or min(sx, sy) < 0.05))
        n += 1
    g = S["galatea"]["pos"]
    bpy.ops.mesh.primitive_cylinder_add(radius=0.18, depth=1.25, location=(g[0], g[1], g[2] - 0.35)); add(bpy.context.object, mat("body", (0.35, 0.45, 0.65, 1)))
    bpy.ops.mesh.primitive_uv_sphere_add(radius=0.12, location=(g[0], g[1], g[2] + 0.42)); add(bpy.context.object, mat("skin", (0.85, 0.66, 0.55, 1), sss=0.2))
    log("objects built", n)
    bpy.ops.object.light_add(type="SUN", rotation=(math.radians(50), 0, math.radians(200))); bpy.context.object.data.energy = 3.5
    world(1.0)
    cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam")); sc.collection.objects.link(cam); sc.camera = cam
    cam.data.lens = 24; cam.location = (g[0] - 1.5, g[1] - 7.5, g[2] + 2.2); look_at(cam, (g[0] + 0.5, g[1] + 3.5, g[2] + 0.3))
    sc.render.resolution_x, sc.render.resolution_y = (960, 540) if engine == "CYCLES" else (640, 360)

def build_face():
    hair_tex = os.path.join(texdir, "8d5ad85f-453b-ed86-31b3-c7356150164f.png")
    skin = mat("skin", (0.80, 0.58, 0.47, 1), rough=0.5, sss=0.12)
    bpy.ops.mesh.primitive_uv_sphere_add(segments=64, ring_count=32, radius=1, location=(0, 0, 0)); h = bpy.context.object
    for v in h.data.vertices:  # taper the lower half into a jaw/chin, flatten the face front a little
        z = v.co.z
        if z < 0: v.co.x *= 1 + 0.42 * z; v.co.y *= 1 + 0.25 * z
        if v.co.y < -0.6: v.co.y = -0.6 - (abs(v.co.y) - 0.6) * 0.5
    h.scale = (0.075, 0.095, 0.115); add(h, skin); bpy.ops.object.shade_smooth()
    bpy.ops.mesh.primitive_cylinder_add(radius=0.045, depth=0.12, location=(0, 0.01, -0.15)); add(bpy.context.object, skin)
    for sx in (-1, 1):
        bpy.ops.mesh.primitive_uv_sphere_add(radius=0.0125, location=(sx * 0.03, -0.066, 0.012)); add(bpy.context.object, mat("eyewhite", (0.92, 0.9, 0.88, 1), rough=0.1)); bpy.ops.object.shade_smooth()
        bpy.ops.mesh.primitive_uv_sphere_add(radius=0.0062, location=(sx * 0.03, -0.0772, 0.012)); add(bpy.context.object, mat("iris", (0.23, 0.12, 0.05, 1), rough=0.1)); bpy.ops.object.shade_smooth()
        bpy.ops.mesh.primitive_uv_sphere_add(radius=0.0028, location=(sx * 0.03, -0.0815, 0.012)); add(bpy.context.object, mat("pupil", (0.01, 0.01, 0.01, 1), rough=0.05))
        bpy.ops.mesh.primitive_cube_add(size=1, location=(sx * 0.031, -0.072, 0.033), rotation=(0, math.radians(sx * -8), 0)); b = bpy.context.object; b.scale = (0.026, 0.004, 0.0035); add(b, mat("brow", (0.18, 0.12, 0.08, 1)))
    bpy.ops.mesh.primitive_uv_sphere_add(radius=1, location=(0, -0.066, -0.045)); l = bpy.context.object; l.scale = (0.017, 0.006, 0.0055); add(l, mat("lips", (0.70, 0.35, 0.36, 1), rough=0.35)); bpy.ops.object.shade_smooth()
    bpy.ops.mesh.primitive_uv_sphere_add(radius=1, location=(0, -0.074, -0.012)); n = bpy.context.object; n.scale = (0.011, 0.012, 0.016); add(n, skin); bpy.ops.object.shade_smooth()
    hair = mat("hair", (0.55, 0.42, 0.25, 1), tex=hair_tex, rough=0.65)
    bpy.ops.mesh.primitive_uv_sphere_add(segments=48, ring_count=24, radius=1, location=(0, 0.008, 0.012)); hc = bpy.context.object; hc.scale = (0.082, 0.097, 0.112); add(hc, hair); bpy.ops.object.shade_smooth()
    bpy.ops.object.mode_set(mode="EDIT"); import bmesh
    bm = bmesh.from_edit_mesh(hc.data)
    for v in bm.verts:
        if v.co.z < 0.15 and v.co.y < 0.2: v.select = True
        else: v.select = False
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if v.select], context="VERTS"); bmesh.update_edit_mesh(hc.data); bpy.ops.object.mode_set(mode="OBJECT")
    for sx in (-1, 1):
        bpy.ops.mesh.primitive_cube_add(size=1, location=(sx * 0.072, 0.02, -0.09)); p = bpy.context.object; p.scale = (0.02, 0.07, 0.24); add(p, hair)
    bpy.ops.mesh.primitive_cube_add(size=1, location=(0, 0.06, -0.12)); add(bpy.context.object, hair); bpy.context.object.scale = (0.15, 0.03, 0.3)
    for loc, en, size in (((-0.6, -0.8, 0.35), 14, 0.6), ((0.7, -0.5, 0.1), 6, 0.8), ((0.0, 0.7, 0.5), 10, 0.5)):
        bpy.ops.object.light_add(type="AREA", location=loc); L = bpy.context.object; L.data.energy = en; L.data.size = size; look_at(L, (0, 0, 0))
    world(0.6)
    cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam")); sc.collection.objects.link(cam); sc.camera = cam
    cam.data.lens = 85; cam.location = (0, -0.75, 0.0); look_at(cam, (0, 0, -0.01))
    sc.render.resolution_x, sc.render.resolution_y = 768, 768

gpu_setup()
build_scene() if kind == "scene" else build_face()
sc.view_settings.view_transform = "AgX"
sc.render.image_settings.file_format = "JPEG"; sc.render.image_settings.quality = 90; sc.render.filepath = out
log("build seconds", round(time.time() - T0, 1))
t = time.time(); bpy.ops.render.render(write_still=True); log("render seconds", round(time.time() - t, 1))
try:
    import gpu; log("gpu platform:", gpu.platform.vendor_get(), "|", gpu.platform.renderer_get(), "|", gpu.platform.version_get())
except Exception as e:
    log("gpu platform unavailable:", e)
