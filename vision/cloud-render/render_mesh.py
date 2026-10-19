"""Blender script: render real SL geometry from scene-mesher output (mesh.json + mesh.bin).

blender -b --factory-startup --python render_mesh.py -- <jobdir> {scene|face|body} {CYCLES|EEVEE|WORKBENCH} out.jpg
jobdir holds mesh.json, mesh.bin, terrain.json and tex/<uuid>.png, tex/bake-<name>.png.
Galatea's rigged attachments are in bind pose (= SL's default T-pose), in avatar space (+X forward, feet near z=0).
"""
import bpy, json, math, os, re, sys, time, warnings
import numpy as np
from mathutils import Vector

jobdir, kind, engine, out = sys.argv[sys.argv.index("--") + 1:][:4]
import mmap
M = json.load(open(f"{jobdir}/mesh.json"))
_binf = open(f"{jobdir}/mesh.bin", "rb"); BIN = mmap.mmap(_binf.fileno(), 0, access=mmap.ACCESS_READ)   # paged in per batch, not read whole (~0.7 GB for a crowd)
T0 = time.time()
bpy.ops.wm.read_factory_settings(use_empty=True)
sc = bpy.context.scene
def log(*a): print("GT:", *a, flush=True)
def memlog(tag):   # GT_MEM_TRACE=1: this process's resident/anonymous memory at a milestone
    if os.environ.get("GT_MEM_TRACE"):
        d = dict(l.split(":", 1) for l in open("/proc/self/status") if l.startswith(("VmRSS", "RssAnon", "RssFile")))
        log("mem", tag, {k: int(v.split()[0]) // 1024 for k, v in d.items()}, "MB", "images", len(bpy.data.images), "Mpx", round(sum(i.size[0] * i.size[1] for i in bpy.data.images) / 1e6, 1))
# photo hd layered crowd (vision/layers.py): "base" = world + her + shadow-only proxies of everyone else; "batch" = a few avatars
# (GT_LAYER_AVATARS = their mesh.json groups) with the world and her as holdouts on a transparent film, same camera/lighting.
# Both save scene-linear RGBA as GT_LAYER_NPY (.npy) and skip the tonemap: look.py composites back-to-front, then tonemaps once.
LAYER = os.environ.get("GT_LAYER", "") if os.environ.get("GT_HD") == "1" else ""
LAYER_AVS = {g for g in os.environ.get("GT_LAYER_AVATARS", "").split(",") if g}
HD = os.environ.get("GT_HD") == "1"   # `photo hd`: David's 4K stills (the only renders that use the region environment fully)
def lin(c): return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4   # sRGB -> linear

SSR = os.environ.get("GT_SSR", "1") != "0"
# Firestorm's sun/moon shadow is a shadow map (4-tap PCF at +-1.5 texels, shadowUtil.glsl pcfShadow) blurred in screen
# space (blurLightF.glsl, RenderShadowBlurSize 1.4, RenderShadowGaussian 3/2): a soft edge a few cm wide at a chair's
# height, and thin alpha-masked weave (rope chairs) washes out to a partial shadow. Cycles' default 0.5 deg disc gave
# razor edges and a solid weave shadow; a wider disc reproduces the blurred penumbra (fitted on the 2026-10-13 porch).
SHADOW_ANGLE_NIGHT = float(os.environ.get("GT_SHADOW_ANGLE", "0.53")); SHADOW_ANGLE_DAY = float(os.environ.get("GT_SHADOW_ANGLE_DAY", "0.53"))
FULLBRIGHT = float(os.environ.get("GT_FULLBRIGHT", "0.8"))   # HD profiles 1.0: SL fullbright = the texture at full display value
INDOOR_AMB = float(os.environ.get("GT_INDOOR_AMB", "0.2")); INDOOR_DIST = float(os.environ.get("GT_INDOOR_DIST", "15"))   # sky ambient left under a closed roof; AO reach (m)
SSAO = float(os.environ.get("GT_SSAO", "0")); SSAO_DIST = float(os.environ.get("GT_SSAO_DIST", "0.8"))   # profiles: 0.8 (RenderSSAOEffect)
# 2026-10-14 David's Ultra (photo hd default): SSR + mirrors on. Cycles traces reflections in world space, so they are the
# same in every 4K tile (no screen-edge fade, nothing off-screen missing): SSR on = every face keeps Principled's specular
# (water, wet/shiny surfaces); mirrors on = smooth faces reflect sharply at full render resolution (>= his 2048 mirror
# probe). Mirrors off (`nomirrors`): smooth faces get the radiance-probe blur instead (roughness floor MIRROR_OFF_ROUGH)
MIRRORS = os.environ.get("GT_MIRRORS", "1") != "0"; MIRROR_OFF_ROUGH = 0.12

def dof_subject(view, heads):
    """David 2026-10-14: DOF only when the photo has an avatar subject (`self` portrait, David, a named avatar);
    scenery / `view` / an object target stay sharp everywhere"""
    v = view or ""
    if v.startswith("portrait"): return True
    return v.startswith("at:") and v[3:] in (heads or {})

def roughness_floor(r):
    """reflection roughness Cycles uses: unchanged with mirrors on; probe-like floor with mirrors off"""
    return r if MIRRORS else max(r, MIRROR_OFF_ROUGH)

def dof_fstop(n, focal_mm, fov_deg, fovy, lens_mm):
    """Firestorm's depth of field (pipeline.cpp renderDoF + cofF.glsl calc_cof) -> the Blender f-stop that blurs the same.
    Viewer: focal_length = CameraFocalLength x tan(CameraFieldOfView/2) / tan(fov/2) (fov = the camera's vertical view),
    CoF px = 1.414 x |d-S|/d x (f^2 / (N (S-f))) / magnification / (S x tan_pixel_angle), tan_pixel_angle ~ fovy / height
    -> CoF / height = 1.414 f |d-S| / (d N S fovy) (FIRE-13989: the same at every resolution). Blender's thin lens with
    sensor height 2 F tan(fovy/2): CoF / height = F |d-S| / (d N_b (S-F) 2 tan(fovy/2)). Equal when
    N_b = N fovy F / (1.414 f 2 tan(fovy/2)) (S >> F; f, F in metres; F = Blender's lens)"""
    f = focal_mm / 1000.0 * math.tan(math.radians(fov_deg) / 2) / math.tan(fovy / 2)
    # 2026-10-14: the viewer's sc is the bokeh RADIUS (postDeferredF.glsl samples a circle of radius sc px), Blender's CoF
    # a diameter: half the f-stop (we blurred half as much as Firestorm)
    return n * fovy * (lens_mm / 1000.0) / (1.414 * f * 2 * math.tan(fovy / 2)) / 2.0

def max_cof_ok(n_b, lens_mm, fovy, subject_m, max_cof_px=10.0, viewer_h=1080.0):
    """Firestorm clamps the CoF at CameraMaxCoF (10 px of a ~1080 p window); Blender has no clamp: raise the f-stop so the
    far-background blur (d -> inf) stays within it"""
    F = lens_mm / 1000.0
    far = F / (n_b * max(subject_m - F, 1e-3) * 2 * math.tan(fovy / 2))   # CoF / height at infinity
    lim = 2.0 * max_cof_px / viewer_h   # CameraMaxCoF clamps the radius
    return n_b if far <= lim else n_b * far / lim

def light_linear(c):
    """an SL point light's colour as the shader uses it. The wire value is already linear (llprimitive.h LLLightParams:
    mColor "linear color (not gamma corrected)"; the viewer's getLightLinearColor() passes it straight on). Until
    2026-10-13 we ran it through sRGB -> linear again: warm lamps (1, 0.8, 0.6) lit as (1, 0.6, 0.32), dim and orange"""
    return [min(1.0, max(0.0, float(x or 0.0))) for x in c[:3]]

def moon_strength(env):
    """Cycles sun strength for the moon under a classic sky, from Firestorm's legacy shading (softenLightF.glsl, classic):
    colour = srgb_to_linear(amb x 0.9 + srgb(da^1.2) x sunlit x 0.7) x 1.1, sunlit = moon diffuse (sunlight colour x
    moon_brightness, llsettingssky.cpp) x RenderSkySunlightScale 1.5 x 1.35 (classic). Matched at da = 0.5 (moon ~30 deg
    up on a floor): the moonlight part of that over the ambient. The old 0.25 x moon_brightness was ~10x too dim: a
    moonlit porch floor rendered like a shaded one (Firestorm reference 2026-10-13)"""
    a_s = AMB_SRGB(env)
    S = max(env.get("sunlight") or [1.0]) * float(env.get("moon", 0.5)) * 1.5 * 1.35
    da = 0.5 ** 1.2
    srgb = lambda v: 12.92 * v if v <= 0.0031308 else 1.055 * v ** (1 / 2.4) - 0.055
    e = lin(min(1.0, a_s * 0.9 + srgb(da) * S * 0.7)) * 1.1 - lin(min(1.0, a_s * 0.9)) * 1.1
    # x 0.36: calibrated on the two 2026-10-13 Peronaut references (porch view +9 %, moonlit deck view -9 % overall); the
    # viewer adds the moon in sRGB space, which one linear sun can't follow everywhere
    return max(0.0, e / 0.5 * MOON_CAL)

MOON_CAL = float(os.environ.get("GT_MOON_CAL", "0.75"))   # 2026-10-13 23:10: re-fit with full SSAO (ambient no longer lifts shadows): view 2 percentiles 5-99 within 2 levels

# RenderSkyAmbientScale: the code fallback is 1.5 (llsettingsvo.cpp), Firestorm's shipped settings.xml default 1.0
# the classic sky as Firestorm displays it (2026-10-14 15:37 Beach House shot, 6 rows 2-31 % down the frame): his sky is
# ~(0.84, 0.85, 0.88) x skyF.glsl's display value at the same elevation (linear ~(0.66, 0.70, 0.76)) from the horizon to 27 deg
SKY_SCALE = [float(x) for x in os.environ.get("GT_SKY_SCALE", "0.66,0.70,0.76").split(",")]   # per channel (linear): R, G, B
WATER_REFL = float(os.environ.get("GT_WATER_REFL", "0.7"))
GT_WORLD_GREY = float(os.environ.get("GT_WORLD_GREY", "0.5"))   # classic amblit is grey: half the horizon colour's saturation in the fill   # experiment knobs (2026-10-14 saturation pass)
GT_SUN_MUL = float(os.environ.get("GT_SUN_MUL", "1.2"))   # classic softenLightF: sunlit x 1.35 (we kept 1.2 x peak; with the world fill cut to 0.1 the lit side needs it)
GT_PBR_SPEC = float(os.environ.get("GT_PBR_SPEC", "0.15"))   # x Principled's 0.5: the sky sheen on rough non-metal PBR (teak deck at grazing view)
GT_PBR_AMB = os.environ.get("GT_PBR_AMB", "1") == "1"
SKY_AMB_SCALE = float(os.environ.get("GT_SKY_AMBIENT_SCALE", "1.5"))

def AMB_SRGB(env):
    """the classic sky's ambient in the viewer's sRGB space, x RenderSkyAmbientScale 1.5 (mean of the channels)"""
    if not env or not env.get("ambient"): return 0.0
    cs = env.get("cloud_shadow", 0.0)
    return sum((x + (1 - x) * cs * 0.5) ** 0.9 * 0.57 * 0.9 * SKY_AMB_SCALE for x in env["ambient"]) / 3

def amblit(env):
    """The SL viewer's sky ambient on every surface (reimplemented from the LL viewer's calcAtmosphericVars):
    ambient raised by cloud cover, ^0.9 x 0.57, to linear, as grey luminance. It is NOT occluded the way Cycles' world light
    is, which is why her interiors at night rendered near-black while the SL viewer shows them dim but readable.
    ponytail: LL's normal-dependent ambientLighting() factor is taken at its mean (0.9); reflection probes (the PBR
    viewer's indoor irradiance) aren't modelled; ceiling = flat-looking ambient in corners"""
    if not env or not env.get("ambient"): return 0.0
    cs = env.get("cloud_shadow", 0.0)
    # 2026-10-13 "why are your photos so much darker?": Firestorm's classic sky (legacy EEP, RenderSkyAutoAdjustLegacy off)
    # scales the sRGB ambient by RenderSkyAmbientScale 1.5 (llsettingsvo.cpp, sky_ambient_scale in atmosphericsFuncs.glsl),
    # then softenLightF.glsl: srgb_to_linear(amblit x 0.9 + sun) x 1.1. We stopped at lin(amblit): ~half the viewer's ambient
    k_s, k_l = (SKY_AMB_SCALE * 0.9, 1.1) if env.get("classic", True) else (1.0, 1.0)
    t = [lin(min(1.0, (x + (1 - x) * cs * 0.5) ** 0.9 * 0.57 * 0.9 * k_s)) * k_l for x in env["ambient"]]
    return 0.2126 * t[0] + 0.7152 * t[1] + 0.0722 * t[2]
AMB = amblit(M.get("env")) if kind == "scene" else 0.0  # face/body portraits have their own studio lights
# look.py writes tex-manifest.json: the exact file each texture was fetched to for this job (no 5-way exists() search,
# no bigger cached copy picked over the job's cap) and each file's alpha stats (PIL, cached across looks), so the build
# never reads image pixels in Python (Warehouse 21: ~11 s for ~3500 images)
_mf = f"{jobdir}/tex-manifest.json"
MANIFEST = json.load(open(_mf)) if os.path.exists(_mf) else {}
TEX_PATHS = MANIFEST.get("paths", {}); ALPHA_SIDECAR = MANIFEST.get("alpha", {})
HIDE_BATCH = set(os.environ.get("GT_DEBUG_HIDE_BATCH", "").split(","))
SELF_REFLECT = os.environ.get("GT_SELF_REFLECT") == "1"
NOGLOSSY = [k for k in os.environ.get("GT_DEBUG_NOGLOSSY", "").split(",") if k]
ALPHA_CACHE = {}  # name -> (has_cutout, amax)
def _alpha_stats(image):
    if image.name not in ALPHA_CACHE:
        side = ALPHA_SIDECAR.get(os.path.basename(image.filepath))
        if side is not None:
            ALPHA_CACHE[image.name] = (bool(side[0]), float(side[1]))
        elif image.channels != 4 or not image.pixels:
            ALPHA_CACHE[image.name] = (False, 1.0)
        else:
            a = np.empty(len(image.pixels), np.float32); image.pixels.foreach_get(a)
            aa = a[3::4]; ALPHA_CACHE[image.name] = (float(aa.min()) < 0.98, float(aa.max()) if aa.size else 1.0)
    return ALPHA_CACHE[image.name]
def has_alpha(image):
    """any texel below 0.98 alpha (from the manifest; else one numpy pass per distinct texture)"""
    return _alpha_stats(image)[0]

_PATHS = {}
def tex_path(uuid):
    if not uuid: return None
    if uuid in TEX_PATHS and os.path.exists(TEX_PATHS[uuid]): return TEX_PATHS[uuid]
    if uuid not in _PATHS:
        base = f"{jobdir}/tex/{uuid.replace('bake:', 'bake-')}"
        _PATHS[uuid] = next((p for p in (base + ".png", *(f"{base}.{c}.png" for c in (1024, 512, 256, 128))) if os.path.exists(p)), None)
    return _PATHS[uuid]
_IMAGES = {}
HOLD_PX = int(os.environ.get("GT_LAYER_HOLD_PX", "64"))   # batch passes: the holdout world's textures are shrunk to this
def load_image(path, data=False, small=False):
    # images.load(check_existing=True) compares against every loaded image: O(n^2) over a crowd's ~3500 textures
    k = (path, small)
    if k not in _IMAGES:
        im = _IMAGES[k] = bpy.data.images.load(path, check_existing=False)
        if small:   # a holdout is never seen: it only needs its silhouette (cut-out alpha) and its average colour for bounce light
            _alpha_stats(im); w, h = im.size
            if max(w, h) > HOLD_PX: s = HOLD_PX / max(w, h); im.scale(max(1, round(w * s)), max(1, round(h * s)))
    if data: _IMAGES[k].colorspace_settings.name = "Non-Color"
    return _IMAGES[k]

def mat_images(b, small=False):
    """slot -> Blender image for one SL face (diffuse, normal, mr, emissive_tex, spec), as _material reads them.
    small (a batch pass's holdout world): the diffuse (and emissive) maps only, shrunk to HOLD_PX"""
    mat = b.get("mat") or {"alpha": "auto"}; out = {}
    def put(slot, uuid, data=False):
        p_ = tex_path(uuid)
        if p_: out[slot] = load_image(p_, data, small)
    put("diffuse", b["tex"])
    if not small: put("normal", mat.get("normal"), True)
    if mat.get("pbr"):
        if not small: put("mr", mat.get("mr"), True)
        put("emissive_tex", mat.get("emissive_tex"))
    elif "gloss" in mat and not small: put("spec", mat.get("spec"))
    return out

# Materials: one template per structure (alpha mode, which maps exist, numeric material params, fullbright, cut-out),
# copied per texture set; the face colour comes from the mesh attribute "sl_tint". Every node/link/socket edit through
# bpy re-propagates over all node trees in the file (~0.3 ms each at 4000 materials: O(n^2)); Material.copy() doesn't.
# Warehouse 21: ~17 s of node building -> ~1 s. Also faces that differed only in colour now share a material.
TEMPLATES = {}; MAT_CACHE = {}
# glow/bloom (Firestorm "Glow", High+ strength 2 = RenderGlowIterations): only photo hd scenes with a glowing face
GLOW_ON = HD and LAYER != "batch" and os.environ.get("GT_GLOW", "1") != "0" and any((b.get("glow") or 0) > 0 for b in M.get("batches", []))
# Batch passes: a holdout's see-through (blended / hashed) alpha made it a PARTIAL holdout, so an avatar behind her hair showed through every
# hashed gap in the batch layer, and alpha-over onto the base (which already has her hair over the world) darkened the hair there: fine
# dark dots over her hair (Oct 19 2026). A holdout is never seen, only its silhouette: its blended alpha is cut at GT_HOLD_ALPHA_CUT
# (default 0.5, the usual mask cutoff; 0 = the old partial holdout), so the layer is either fully held out or not, never dithered.
HOLD_ALPHA_CUT = float(os.environ.get("GT_HOLD_ALPHA_CUT", "0.5"))

def material(b, imgs=None, hold=False):
    imgs = mat_images(b) if imgs is None else imgs
    mat = b.get("mat") or {"alpha": "auto"}
    if hold and HOLD_ALPHA_CUT > 0 and mat.get("alpha", "auto") in ("auto", "blend"):
        mat = {**mat, "alpha": "mask", "cutoff": HOLD_ALPHA_CUT}; b = {**b, "mat": mat}
    cut = has_alpha(imgs["diffuse"]) if "diffuse" in imgs else None
    sig = json.dumps([{k: v for k, v in mat.items() if k not in ("normal", "spec", "mr", "emissive_tex")}, sorted(imgs),
                      b["rgba"][3] < 0.999, bool(b["fullbright"]), cut], sort_keys=True)
    k = (sig, tuple(sorted((s_, im.name) for s_, im in imgs.items())))
    if k not in MAT_CACHE:
        if sig not in TEMPLATES: TEMPLATES[sig] = _material(b, set(imgs), cut)
        m = TEMPLATES[sig].copy(); m.name = b["tex"][:50]
        for s_, im in imgs.items(): m.node_tree.nodes[s_].image = im
        MAT_CACHE[k] = m
    return MAT_CACHE[k]

def glow_aov(nt, p, tint):
    """photo hd glow: AOV "glow" = the face's colour x its SL glow (LL glowExtract: colour x glow), bloomed in hdr_post"""
    src = p.inputs["Base Color"].links[0].from_socket if p.inputs["Base Color"].links else tint.outputs["Color"]
    ga = nt.nodes.new("ShaderNodeAttribute"); ga.attribute_name = "sl_glow"
    sv = nt.nodes.new("ShaderNodeVectorMath"); sv.operation = "SCALE"; nt.links.new(src, sv.inputs[0]); nt.links.new(ga.outputs["Fac"], sv.inputs["Scale"])
    ao = nt.nodes.new("ShaderNodeOutputAOV"); ao.aov_name = "glow"; nt.links.new(sv.outputs[0], ao.inputs["Color"])

def _material(b, slots, cut):
    """SL face -> Principled BSDF template (image nodes named by slot, left empty; colour from the "sl_tint" attribute).
    alpha: none | blend | mask (cutoff) | emissive (alpha = glow mask) | auto (texture alpha)."""
    mat = b.get("mat") or {"alpha": "auto"}; m = bpy.data.materials.new("tmpl"); m.use_nodes = True
    nt = m.node_tree; p = nt.nodes["Principled BSDF"]; a = b["rgba"][3]; L = nt.links.new
    tint = nt.nodes.new("ShaderNodeAttribute"); tint.attribute_name = "sl_tint"   # per-batch face colour (linear RGBA)
    def img(slot):
        if slot not in slots: return None
        n = nt.nodes.new("ShaderNodeTexImage"); n.name = slot
        return n
    def mul(x, y):
        n = nt.nodes.new("ShaderNodeMath"); n.operation = "MULTIPLY"; L(x, n.inputs[0])
        if isinstance(y, float): n.inputs[1].default_value = y
        else: L(y, n.inputs[1])
        return n.outputs[0]
    L(tint.outputs["Color"], p.inputs["Base Color"]); p.inputs["Roughness"].default_value = 0.6
    alpha = None; it = img("diffuse")
    if it:
        mix = nt.nodes.new("ShaderNodeMix"); mix.data_type = "RGBA"; mix.blend_type = "MULTIPLY"; mix.inputs["Factor"].default_value = 1
        L(it.outputs["Color"], mix.inputs["A"]); L(tint.outputs["Color"], mix.inputs["B"]); L(mix.outputs["Result"], p.inputs["Base Color"])
        alpha = mul(it.outputs["Alpha"], tint.outputs["Alpha"]) if a < 0.999 else it.outputs["Alpha"]
    mode = mat["alpha"]
    if mode in ("auto", "blend"):
        if alpha is not None: L(alpha, p.inputs["Alpha"])
        elif a < 0.999: L(tint.outputs["Alpha"], p.inputs["Alpha"])
    elif mode == "mask" and alpha is not None:
        gt = nt.nodes.new("ShaderNodeMath"); gt.operation = "GREATER_THAN"; L(alpha, gt.inputs[0]); gt.inputs[1].default_value = mat.get("cutoff", 0.5) - 1e-4
        L(gt.outputs[0], p.inputs["Alpha"])
    elif mode == "emissive" and alpha is not None:
        L(p.inputs["Base Color"].links[0].from_socket, p.inputs["Emission Color"]); L(alpha, p.inputs["Emission Strength"])
    nm = img("normal")
    if nm:
        n = nt.nodes.new("ShaderNodeNormalMap"); L(nm.outputs["Color"], n.inputs["Color"]); L(n.outputs["Normal"], p.inputs["Normal"])
    if mat.get("pbr"):
        p.inputs["Metallic"].default_value = mat["metallic"]; p.inputs["Roughness"].default_value = roughness_floor(mat["roughness"])
        p.inputs["Specular IOR Level"].default_value = 0.5 * GT_PBR_SPEC
        mr = img("mr")
        if mr:  # glTF: G = roughness, B = metallic
            sep = nt.nodes.new("ShaderNodeSeparateColor"); L(mr.outputs["Color"], sep.inputs["Color"])
            rg = mul(sep.outputs["Green"], float(mat["roughness"]))
            if not MIRRORS:
                mx_ = nt.nodes.new("ShaderNodeMath"); mx_.operation = "MAXIMUM"; L(rg, mx_.inputs[0]); mx_.inputs[1].default_value = MIRROR_OFF_ROUGH; rg = mx_.outputs[0]
            L(rg, p.inputs["Roughness"]); L(mul(sep.outputs["Blue"], float(mat["metallic"])), p.inputs["Metallic"])
        e = mat.get("emissive") or [0, 0, 0]; et = img("emissive_tex")
        if max(e) > 0:
            p.inputs["Emission Color"].default_value = (*e, 1); p.inputs["Emission Strength"].default_value = 1
            if et: L(et.outputs["Color"], p.inputs["Emission Color"])
            if GT_PBR_AMB and AMB > 0 and not b["fullbright"] and mode != "emissive":
                # 2026-10-14: a PBR face with an emissive factor (the Beach House boat's whole deck: factor 1, black emissive
                # map) skipped the sky ambient below (emission strength already 1): no albedo-tinted fill, only the world's
                # blue-ish light. Add albedo x amblit to the emissive map instead
                em = nt.nodes.new("ShaderNodeMix"); em.data_type = "RGBA"; em.blend_type = "ADD"; em.inputs["Factor"].default_value = 1.0
                src = p.inputs["Emission Color"].links[0].from_socket if p.inputs["Emission Color"].links else None
                if src is not None: L(src, em.inputs["A"])
                else: em.inputs["A"].default_value = (*e, 1)
                bc = nt.nodes.new("ShaderNodeMix"); bc.data_type = "RGBA"; bc.blend_type = "MULTIPLY"; bc.inputs["Factor"].default_value = 1.0
                L(p.inputs["Base Color"].links[0].from_socket if p.inputs["Base Color"].links else tint.outputs["Color"], bc.inputs["A"]); bc.inputs["B"].default_value = (AMB, AMB, AMB, 1)
                L(bc.outputs["Result"], em.inputs["B"]); L(em.outputs["Result"], p.inputs["Emission Color"])
    elif "gloss" in mat:  # legacy: specular colour x spec map; glossiness (exponent/255) -> roughness
        sc = mat.get("spec_color", [1, 1, 1, 1]); sp = img("spec")
        p.inputs["Roughness"].default_value = roughness_floor(1 - 0.85 * mat["gloss"])
        p.inputs["Specular Tint"].default_value = (sc[0], sc[1], sc[2], 1)
        if sp:
            mx = nt.nodes.new("ShaderNodeMix"); mx.data_type = "RGBA"; mx.blend_type = "MULTIPLY"; mx.inputs["Factor"].default_value = 1
            L(sp.outputs["Color"], mx.inputs["A"]); mx.inputs["B"].default_value = (sc[0], sc[1], sc[2], 1); L(mx.outputs["Result"], p.inputs["Specular Tint"])
        p.inputs["Specular IOR Level"].default_value = 0.5 + 0.5 * mat.get("env", 0)
        # ponytail: env intensity only raises specular; no reflection probes; ceiling = shiny SL surfaces look matte
    # 2026-10-14 bikini compare: with SSR on too: the viewer's SSR / probes only reflect on faces with a glossiness
    # (specular.a); a plain face stays matte. The 0.5 sheen greyed the teak deck ((97, 85, 76) vs his (108, 84, 59))
    if not ("gloss" in mat or "roughness" in mat):
        # SSR + mirrors off (David's High+, Firestorm 2026-10-13): a face without a specular/PBR material gets no reflection
        # at all in the viewer (no screen-space pass, no hero probe); Principled's default 0.5 specular put a sky/lamp sheen on
        # leaves, bark, skin bakes and plaster (foliage 37 vs 24, wall/skin +10-17%). Shiny faces keep Cycles' glossy = probes.
        p.inputs["Specular IOR Level"].default_value = 0.0
    if b["fullbright"]:
        if p.inputs["Base Color"].links: L(p.inputs["Base Color"].links[0].from_socket, p.inputs["Emission Color"])
        else: L(tint.outputs["Color"], p.inputs["Emission Color"])
        p.inputs["Emission Strength"].default_value = FULLBRIGHT
    elif AMB > 0 and mode != "emissive" and not p.inputs["Emission Strength"].links and p.inputs["Emission Strength"].default_value == 0:
        # SL sky ambient: albedo x amblit, as emission so walls and roofs don't shade it (see amblit())
        if p.inputs["Base Color"].links: L(p.inputs["Base Color"].links[0].from_socket, p.inputs["Emission Color"])
        else: L(tint.outputs["Color"], p.inputs["Emission Color"])
        # PBR metal reflects instead of scattering: sky ambient as albedo emission washed chrome to flat paint (2026-10-11)
        p.inputs["Emission Strength"].default_value = AMB * (1 - 0.8 * float(mat.get("metallic", 0))) if mat.get("pbr") else AMB
        if HD and SSAO > 0:
            # Firestorm SSAO (High+ "Ambient occlusion on"): the deferred ambient term x mix(1, ao, RenderSSAOEffect 0.8).
            # Ambient as emission skipped occlusion, so leaf clusters and a hugging couple got the open-sky ambient
            # (palm 30 vs 24, skin 69 vs 59 on the 2026-10-13 porch reference).
            ao = nt.nodes.new("ShaderNodeAmbientOcclusion"); ao.inputs["Distance"].default_value = SSAO_DIST; ao.samples = 8
            k = nt.nodes.new("ShaderNodeMapRange"); L(ao.outputs["AO"], k.inputs["Value"]); k.inputs[3].default_value = 1 - SSAO
            L(mul(k.outputs["Result"], float(p.inputs["Emission Strength"].default_value)), p.inputs["Emission Strength"])
        if HD and INDOOR_AMB < 1:
            # indoors (Oct 19 2026, David at Warehouse 21: "darker in-world, the light comes from the spotlights"): the sky ambient was emission
            # on every face, so a roofed club got the full open-sky fill. Its sky share now follows how much open sky the point sees (AO
            # over GT_INDOOR_DIST m: roof and walls block it), down to GT_INDOOR_AMB of it under a closed roof (1 = the old unoccluded fill).
            # The sun / moon and the world light are real Cycles lights and already blocked by the roof; the SL point / spot lights are untouched.
            ia = nt.nodes.new("ShaderNodeAmbientOcclusion"); ia.inputs["Distance"].default_value = INDOOR_DIST; ia.samples = 4; ia.only_local = False
            kk = nt.nodes.new("ShaderNodeMapRange"); L(ia.outputs["AO"], kk.inputs["Value"]); kk.inputs[3].default_value = INDOOR_AMB
            src = p.inputs["Emission Strength"].links[0].from_socket if p.inputs["Emission Strength"].links else float(p.inputs["Emission Strength"].default_value)
            L(mul(kk.outputs["Result"], src), p.inputs["Emission Strength"])
    # Opaque clothes must depth-test solid: always-HASHED let body BOM show through jeans/tops (Scentual90).
    if mode in ("none", "emissive"):
        m.blend_method = "OPAQUE"
    elif mode == "mask":
        m.blend_method = "CLIP"; m.alpha_threshold = float(mat.get("cutoff", 0.5))
    elif it and not cut:
        m.blend_method = "OPAQUE"
    else:
        m.blend_method = "HASHED"
    if GLOW_ON: glow_aov(nt, p, tint)
    return m

# Hair bases: some hair meshes carry a scalp-cap piece whose texture region is a flat fill with a blob-shaped alpha (hair 8d5ad85f: UV
# strip u > 0.83, flat brown). Drawn with its alpha-MASK material (cutoff 0.94) it is an opaque flat patch with a hard scalloped edge lying
# on the forehead, between the skin and the strands. Her own avatar's mask-mode triangles whose UVs all lie inside that strip are dropped
# (keyed by texture id, never by prim id): the strands and the blended soft layer are untouched. GT_HAIR_SCALP_HIDE=0 keeps the cap.
SCALP_HIDE = os.environ.get("GT_HAIR_SCALP_HIDE", "1") != "0"
SCALP_CAPS = {"8d5ad85f-453b-ed86-31b3-c7356150164f": 0.83}   # texture id -> u0 of the cap strip

def scalp_cap_filter(b, T, I):
    """triangle index array without the scalp-cap triangles of a mask-mode batch of a known hair texture (else I unchanged)"""
    u0 = SCALP_CAPS.get(b.get("tex"))
    if not SCALP_HIDE or u0 is None or (b.get("mat") or {}).get("alpha") != "mask": return I
    tri = np.asarray(I).reshape(-1, 3); cap = T.reshape(-1, 2)[tri][:, :, 0].min(1) > u0
    return tri[~cap].ravel() if cap.any() else I

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

# lining decisions per outfit, kept across looks (jobdir/lining-cache.json): whether a batch is a garment's inside is a
# property of the mesh, not of the pose, so an outfit seen before skips the twin search (~1 s per near avatar)
_lc = f"{jobdir}/lining-cache.json"
LINING_CACHE = json.load(open(_lc)) if os.path.exists(_lc) else {}
def lining_key(group, mine):
    import hashlib
    sig = "|".join(sorted(f"{b['tex']}:{b['nv']}:{b['ni']}" for _, b in mine))
    return group + ":" + hashlib.sha1(sig.encode()).hexdigest()[:16]
def cached_linings(group, mine, bones):
    k = lining_key(group, mine)
    if k in LINING_CACHE:
        want = set(LINING_CACHE[k]); return {i for i, b in mine if f"{b['tex']}:{b['nv']}:{b['ni']}" in want}
    drop = inner_layers(mine, bones, sample=120)
    LINING_CACHE[k] = sorted(f"{M['batches'][i]['tex']}:{M['batches'][i]['nv']}:{M['batches'][i]['ni']}" for i in drop)
    try:
        json.dump(LINING_CACHE, open(_lc + ".tmp", "w")); os.replace(_lc + ".tmp", _lc)
    except OSError: pass
    return drop

# Blended overlays lying ON the skin (2026-10-07, The V's soft-edged outer patch over the BoM body): a transparent Cycles
# ray continuing from the overlay skips the coplanar skin under it and shows the body's inside as a dark box. The SL viewer
# rasterizes the overlay over the skin. Lift see-through avatar faces 0.5 mm along their normals so the skin is hit next.
# ponytail: a fixed lift; ceiling = overlays modelled less than 0.5 mm in front of other see-through layers swap order
DECAL_LIFT = float(os.environ.get("GT_DECAL_LIFT", "0.0005"))
def lift_along_normals(P, N, d):
    n = N.reshape(-1, 3).astype(np.float64); n /= np.maximum(np.linalg.norm(n, axis=1, keepdims=True), 1e-9)
    return (P.reshape(-1, 3) + d * n).astype(np.float32).ravel()

def build(group, bones=None, lining=True):
    lo, hi = np.full(3, 1e9), np.full(3, -1e9); n = 0; obs = []
    mine = [(i, b) for i, b in enumerate(M["batches"]) if b["group"] == group and b["ni"] > 0]
    drop = set()
    if group.startswith("avatar") and lining:  # a lining's z-fight is invisible at crowd distance (~0.5 s per avatar)
        t = time.time(); drop = cached_linings(group, mine, bones); log(group, "lining batches dropped", len(drop), "in", round(time.time() - t, 2), "s")
    min_tris = int(os.environ.get("GT_MIN_TRIS", "0"))  # look around: drop dust (nav still sees big obstacles)
    for bi, b in mine:
        if bi in drop: continue
        if str(bi) in HIDE_BATCH: continue   # diagnosis: GT_DEBUG_HIDE_BATCH=45,60
        nv, ni = b["nv"], b["ni"]
        P, N, T, I = arrays(b)
        if b["group"] == "avatar" and SCALP_HIDE:   # her own avatar only (others in a crowd keep their hair as authored)
            I = scalp_cap_filter(b, T, I); ni = len(I)
            if not ni: continue
        if min_tris and ni // 3 < min_tris and not b["group"].startswith("avatar"):
            # dust = few triangles AND small: a 2-triangle floor face (Warehouse 21 courtyard, a skybox) is not dust
            ext = P.reshape(-1, 3).max(0) - P.reshape(-1, 3).min(0) if nv else np.zeros(3)
            if float(ext.max()) < 1.0: continue
        hold = LAYER == "batch" and group not in LAYER_AVS   # a batch pass's holdout world: seen as a silhouette only
        imgs = mat_images(b, small=hold)
        tn = imgs.get("diffuse") or next(iter(imgs.values()), None)
        # blank SL textures (32x32 all-alpha-0, e.g. Scentual f54a0c32 "clothing" layer): skip so they don't
        # sit as hashed ghosts over BOM skin (decided before any mesh is made: removing objects is slow in a big scene)
        if tn and _alpha_stats(tn)[1] < 1e-3: continue
        see_through = group.startswith("avatar") and (b.get("mat") or {}).get("alpha", "auto") in ("auto", "blend") and not b["tex"].startswith("bake:") and tn and has_alpha(tn)
        if see_through and DECAL_LIFT > 0: P = lift_along_normals(P, N, DECAL_LIFT)
        me = bpy.data.meshes.new(b["tex"][:40]); nt = ni // 3
        me.vertices.add(nv); me.vertices.foreach_set("co", P)
        me.loops.add(ni); me.loops.foreach_set("vertex_index", I)
        me.polygons.add(nt); me.polygons.foreach_set("loop_start", np.arange(0, ni, 3, dtype=np.int32)); me.polygons.foreach_set("loop_total", np.full(nt, 3, np.int32))
        me.polygons.foreach_set("use_smooth", np.ones(nt, bool))
        uv = me.uv_layers.new(); uv.data.foreach_set("uv", T.reshape(-1, 2)[I].ravel())
        tint = me.color_attributes.new("sl_tint", "FLOAT_COLOR", "POINT")   # the face colour (materials are per texture)
        tint.data.foreach_set("color", np.tile(np.asarray(b["rgba"], np.float32), nv))
        if GLOW_ON:   # SL face glow (photo hd): per-point attribute so the templates stay shared
            gl = me.attributes.new("sl_glow", "FLOAT", "POINT"); gl.data.foreach_set("value", np.full(nv, float(b.get("glow") or 0.0), np.float32))
        me.update()
        if not (hold and os.environ.get("GT_HOLD_FLAT", "1") == "1"): me.normals_split_custom_set_from_vertices(N.reshape(-1, 3))
        ob = bpy.data.objects.new(me.name, me); sc.collection.objects.link(ob); me.materials.append(material(b, imgs, hold))
        obs.append(ob)
        # see-through avatar parts (lashes, hair strands) cast no shadow: under the 0.6 m portrait area lights a lash
        # shadow drew a grey "text" mark beside her nose. The SL viewer's sun shadow map is far too coarse to resolve them.
        # ponytail: decided per texture alpha; ceiling = no hair shadow on her neck
        if see_through:
            ob.visible_shadow = False
        # the backdrop is seen and casts sun shadow, but takes no part in the near scene's bounce light or reflections:
        # the near scene lights exactly as before the backdrop (ponytail: no bounce light off far walls; ceiling = a sunlit
        # building across the water doesn't brighten her side of the deck)
        if group == "far": ob.visible_diffuse = False; ob.visible_glossy = False
        # SL reflections come from reflection probes / the sky environment, which never capture avatars: chrome on her chest
        # mirrored her own chin and neck skin as a brown blur (2026-10-13, 4K photo; quick looks too). Avatars (hers and
        # others') are therefore invisible to glossy rays; the scene and sky still reflect. GT_SELF_REFLECT=1 = old behaviour.
        if group.startswith("avatar") and not SELF_REFLECT: ob.visible_glossy = False
        if NOGLOSSY and any(k in b["tex"] for k in NOGLOSSY): ob.visible_glossy = False   # diagnosis: GT_DEBUG_NOGLOSSY=bake:,<uuid>
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
        if os.environ.get("GT_HD") == "1":   # `photo hd` (2026-10-13): stills for David, quality over speed
            sc.cycles.use_adaptive_sampling = True; sc.cycles.adaptive_threshold = float(os.environ.get("GT_HD_THRESHOLD", "0.02"))
            sc.cycles.denoiser = "OPENIMAGEDENOISE"; sc.cycles.denoising_input_passes = "RGB_ALBEDO_NORMAL"
            sc.cycles.denoising_prefilter = os.environ.get("GT_DENOISE_PREFILTER", "ACCURATE")
            if hasattr(sc.cycles, "denoising_quality"): sc.cycles.denoising_quality = os.environ.get("GT_DENOISE_QUALITY", "HIGH")
            sc.cycles.max_bounces = 6; sc.cycles.diffuse_bounces = 3; sc.cycles.glossy_bounces = 3
            sc.cycles.transparent_max_bounces = int(os.environ.get("GT_TRANSPARENT_BOUNCES", "24")); sc.cycles.filter_width = 1.5 if os.environ.get("GT_SMAA", "1") != "0" else 0.01   # pixel filter = anti-aliasing (SMAA Ultra; `nosmaa` = aliased)
            # 2026-10-13 20:05 photo hd self landscape: Blender OOM-killed at 7.2 GB RSS (15 GB box, client running) after
            # 8 min. Tiles bound the film/denoise buffers; look.py's retry after a kill also sets GT_TEX_LIMIT
            sc.cycles.use_auto_tile = True; sc.cycles.tile_size = int(os.environ.get("GT_TILE", "1024"))
        if os.environ.get("GT_TEX_LIMIT"): sc.cycles.texture_limit_render = os.environ["GT_TEX_LIMIT"]
        log("cycles device", sc.cycles.device)
        if os.environ.get("GT_BVH_COMPACT") == "1":   # Embree compact BVH: less memory for the ~3 M-triangle holdout world
            for k_, v_ in (("debug_use_compact_bvh", True), ("debug_bvh_type", "STATIC_BVH")):
                try: setattr(sc.cycles, k_, v_)
                except Exception as e: log("bvh option", k_, e)
    elif engine == "WORKBENCH":  # flat preview: textures, no lighting model (cheapest; CPU/llvmpipe friendly)
        sc.render.engine = "BLENDER_WORKBENCH"; sc.display.shading.color_type = "TEXTURE"; sc.display.shading.light = "STUDIO"
    else:
        sc.render.engine = "BLENDER_EEVEE_NEXT"; sc.eevee.taa_render_samples = int(os.environ.get("GT_SAMPLES", "16"))

def world(strength):
    w = bpy.data.worlds.new("w"); sc.world = w; w.use_nodes = True
    sky = w.node_tree.nodes.new("ShaderNodeTexSky"); sky.sky_type = "NISHITA"; sky.sun_elevation = math.radians(35); sky.sun_rotation = math.radians(200)
    w.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.25 * strength
    w.node_tree.links.new(sky.outputs["Color"], w.node_tree.nodes["Background"].inputs["Color"])

BODY_VIEW_DEG = {"front": 0.0, "back": 180.0, "left": 90.0, "right": -90.0}  # avatar faces +X; her left is +Y
def body_view_deg(v):
    """a side name or degrees counter-clockwise (from above) from her front -> rig rotation in degrees"""
    v = v.strip().lower()
    return BODY_VIEW_DEG[v] if v in BODY_VIEW_DEG else float(v)
def body_view_label(v):
    """file-name suffix: the side name, else e.g. 135deg / -45deg"""
    v = v.strip().lower()
    return v if v in BODY_VIEW_DEG else f"{float(v):g}deg"

QUICK_EXPOSURE = -1.5   # stops

def view_exposure(env, hd, kind):
    """View-transform exposure (stops). Quick scene looks (not photo hd, not the studio-lit face/body portraits) default to
    -1.5: the neutral sun (3.5) under the Standard transform clipped a third of a sunny deck frame (2026-10-14, David: "way
    too bright"). An explicit GT_EXPOSURE always wins (the Runpod handler always sets one). photo hd keeps 0: its own
    tonemap / exposure (hdr_post) is applied to the scene-linear master."""
    v = env.get("GT_EXPOSURE")
    if v not in (None, ""): return float(v)
    return QUICK_EXPOSURE if (not hd and kind == "scene") else 0.0

def lens_for_vfov(vfov_deg, rx, ry):
    """Blender lens (mm) for a vertical field of view; sensor_fit AUTO puts the 36 mm sensor on the long side"""
    sh = 36.0 * (ry / rx if rx >= ry else 1.0)
    return sh / (2 * math.tan(math.radians(vfov_deg) / 2))

def hd_fov():
    """photo hd vertical FOV in degrees (GT_HD_FOV, default 60 = Firestorm's camera), None outside photo hd"""
    return float(os.environ.get("GT_HD_FOV", "60")) if os.environ.get("GT_HD") == "1" else None

def camera(loc, target, lens, res):
    cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam")); sc.collection.objects.link(cam); sc.camera = cam
    cam.location = loc; cam.rotation_euler = (Vector(target) - Vector(loc)).to_track_quat("-Z", "Y").to_euler()
    cam.data.lens = lens; cam.data.clip_start = 0.02
    if os.environ.get("GT_RES"):
        res = tuple(int(x) for x in os.environ["GT_RES"].split("x"))
    sc.render.resolution_x, sc.render.resolution_y = res
    cam["focus_target"] = list(target)

DOF_SUBJECT = [False]
def setup_dof():
    """photo hd DOF (David's Ultra: f/9, 50 mm, FOV 60): focus on the photo subject = the first surface along the ray to the
    framing target (her / the named avatar or object; `view`: what's at the centre), else the target's distance"""
    cam = sc.camera
    if not (HD and kind == "scene" and cam and os.environ.get("GT_DOF") == "1" and DOF_SUBJECT[0]):
        if cam: cam.data.dof.use_dof = False
        return
    bpy.context.view_layer.update()
    o = cam.matrix_world.translation; t = Vector(cam.get("focus_target", (0, 0, 0))); d = t - o
    hit, loc, *_ = sc.ray_cast(bpy.context.evaluated_depsgraph_get(), o + d.normalized() * 0.05, d.normalized(), distance=d.length + 2)
    S = max(0.1, (loc - o).length if hit else d.length)
    rx, ry = sc.render.resolution_x, sc.render.resolution_y
    cam.data.sensor_fit = "AUTO"; sh = 36.0 * (min(rx, ry) / max(rx, ry) if rx >= ry else 1.0)
    fovy = 2 * math.atan(sh / (2 * cam.data.lens))
    nb = dof_fstop(float(os.environ.get("GT_DOF_FNUMBER", "9")), float(os.environ.get("GT_DOF_FOCAL", "50")), float(os.environ.get("GT_DOF_FOV", "60")), fovy, cam.data.lens)
    nb = max_cof_ok(nb, cam.data.lens, fovy, S, float(os.environ.get("GT_DOF_MAX_COF", "10")))
    cam.data.dof.use_dof = True; cam.data.dof.focus_distance = S; cam.data.dof.aperture_fstop = nb
    log("dof focus", round(S, 2), "m, blender f-stop", round(nb, 1), "fovy", round(math.degrees(fovy), 1))

def area(loc, target, energy, size):
    bpy.ops.object.light_add(type="AREA", location=loc); L = bpy.context.object; L.data.energy = energy; L.data.size = size
    # studio lights cast no shadow (2026-10-13): the SL viewer's local lights don't, and Cycles' contact shadows turned
    # open cut-outs into black panels (the Kay ttchL/ttchR chest windows looked closed: the light into the body's cavity
    # was blocked by the shell around the window, while in-world the mechanics behind it show). Sun shadow is unchanged.
    L.data.use_shadow = os.environ.get("GT_STUDIO_SHADOW", "0") == "1"
    L.rotation_euler = (Vector(target) - Vector(loc)).to_track_quat("-Z", "Y").to_euler()

SUN = {}
SUN_ATTEN = os.environ.get("GT_SUN_ATTEN", "1") == "1"
def atmos_sunlit(env, vy):
    """the sun colour a surface sees in Firestorm's classic shading (atmosphericsFuncs.glsl calcAtmosphericVars): the EEP
    sunlight x exp(-(blue_density + haze_density / 4) x density_multiplier x max_y x off_axis), off_axis = 1 / (sun up +
    view-ray up), the ray from the camera to the surface (vy, a little negative looking down at a deck). At a 36 deg sun
    that is ~(0.9, 0.8, 0.7) of (1.17, 1.21, 1.30): a warm sun. Cycles' sun used the raw sunlight, a cool white-blue one
    (2026-10-14 saturation pass: the orange-brown teak deck read grey)"""
    hz = env.get("haze")
    if not hz or not env.get("sun_dir"): return None
    try:
        att = [(b + hz["haze_density"] * 0.25) * (hz["density_multiplier"] * env.get("max_y", 1605)) for b in hz["blue_density"]]
        off = 1.0 / max(1e-6, max(0.0, env["sun_dir"][2]) + vy)
        return [c * math.exp(-a * off) for c, a in zip(env["sunlight"], att)]
    except (KeyError, TypeError, ValueError): return None

def retint_sun(vy):
    """after the camera is placed: tint the day sun by the atmosphere along the view (classic sky, photo hd only)"""
    L = SUN.get("light"); env = SUN.get("env")
    if not (HD and SUN_ATTEN and L and env and env["sun_dir"][2] > 0 and env.get("classic", True)): return
    ca = atmos_sunlit(env, vy)
    if not ca: return
    c = SUN["c"]; lum = lambda v: 0.2126 * v[0] + 0.7152 * v[1] + 0.0722 * v[2]
    pk = max(max(ca), 1e-3); L.data.color = [x / pk for x in ca]
    L.data.energy = (1.2 * lum(c) * pk / max(lum(ca), 1e-3) if os.environ.get("GT_SUN_LUMKEEP", "1") == "1" else 1.2 * pk) * GT_SUN_MUL   # keep the sun's luminance (hue only) unless LUMKEEP=0
    log("sun tint", [round(x, 3) for x in ca], "energy", round(L.data.energy, 3), "vy", round(vy, 3))

# the viewer (pipeline.cpp renderDeferredLighting) draws every in-world light with size = prim light radius x 1.5 and falloff =
# prim falloff x DEFERRED_LIGHT_FALLOFF (0.5); mesh.json carries the raw prim values. Without these a radius-10 stage spot reached 10 m, not 15 m,
# i.e. not the floor 10-12 m below it, and every light's falloff was twice too steep (Oct 18 2026: the club's 33 spotlights lit nothing).
LIGHT_RADIUS_SCALE = float(os.environ.get("GT_LIGHT_RADIUS_SCALE", "1.5")); LIGHT_FALLOFF_FUDGE = float(os.environ.get("GT_LIGHT_FALLOFF_FUDGE", "0.5"))

SPOT_SHADOWS = 2   # LL: RenderShadowDetail 2 gives shadows to at most two projectors (mShadowSpotLight[2]); point lights never cast any

def light_shadow_ids(lights, me, n=None, mode=None):
    """ids of the lights that cast shadows, the way the viewer does: none of the point lights and only the n nearest projectors
    (the rest shine through their own fixture housings, walls and trusses; Cycles shadowing them blacked out ~every stage spot).
    GT_LIGHT_SHADOW=all restores the old every-light-shadows behaviour"""
    mode = mode or os.environ.get("GT_LIGHT_SHADOW", "sl")
    if mode == "all": return {id(l) for l in lights}
    n = SPOT_SHADOWS if n is None else n
    near = sorted((l for l in lights if l.get("proj")), key=lambda l: sum((a - b) ** 2 for a, b in zip(l["pos"], me)))
    return {id(l) for l in near[:n]}

# Light effects (Oct 18 2026, David: "as good as possible"): SL draws no beam for a spot light and only the fixture's own tiny glow for a
# lamp, so stage lights were small dots. Camera-only emissive geometry (invisible to every light / shadow / reflection ray, so it lights
# nothing): a soft cone for each projector / spot (apex at the light, half angle = fov/2 capped, fading along the throw and toward the cone's
# silhouette) and a soft halo ball at each lamp. Cycles surface shaders only (no volume: no memory, ~free render time); base / single pass
# only, never an avatar batch layer. GT_LIGHT_FX=0 off, GT_LIGHT_BEAM / GT_LIGHT_HALO strengths (0 = that part off).
LIGHT_FX = os.environ.get("GT_LIGHT_FX", "1") != "0" and LAYER != "batch"
LIGHT_BEAM = float(os.environ.get("GT_LIGHT_BEAM", "0.05")); LIGHT_HALO = float(os.environ.get("GT_LIGHT_HALO", "3.0"))
BEAM_MAX_HALF = math.radians(float(os.environ.get("GT_LIGHT_BEAM_MAX_DEG", "28")))

def beam_params(l):
    """(half angle rad, length m, radiance) of a projector's visible cone, or None: wide fill projectors (fov > 120 deg, the ambiance ones) and dim ones get none"""
    pj = l.get("proj")
    if not pj or LIGHT_BEAM <= 0: return None
    fov = float(pj.get("fov") or 0)
    if fov > 2.1 or l.get("intensity", 0) < 0.15: return None
    half = min(max(fov / 2, 0.05), BEAM_MAX_HALF)
    length = min(max(l.get("radius", 10) * 1.5, 1.0), 18.0)
    return half, length, LIGHT_BEAM * min(l["intensity"], 1.5) * (0.35 / max(math.tan(half), 0.2))

def halo_params(l):
    """(radius m, radiance) of a lamp's glow ball, or None"""
    if LIGHT_HALO <= 0 or l.get("intensity", 0) < 0.15 or max(l["color"]) < 0.05: return None
    return 0.18 + 0.02 * min(l.get("radius", 2), 10), LIGHT_HALO * min(l["intensity"], 1.5)

def fx_material(name, col, k, beam_len=None, power=2.0):
    """Add(Transparent, Emission(col, k x profile)): profile = |N.V|^2 (thick middle, soft silhouette) x, for a beam, apex fade-in x throw fade-out"""
    m = bpy.data.materials.new(name); m.use_nodes = True; nt = m.node_tree; nt.nodes.clear()
    def n(t): return nt.nodes.new(t)
    def mm(kind, a, b, clamp=False):
        x = n("ShaderNodeMath"); x.operation = kind; x.use_clamp = clamp
        for i, v in enumerate((a, b)):
            if isinstance(v, (int, float)): x.inputs[i].default_value = v
            else: nt.links.new(v, x.inputs[i])
        return x.outputs[0]
    g = n("ShaderNodeNewGeometry"); dt = n("ShaderNodeVectorMath"); dt.operation = "DOT_PRODUCT"
    nt.links.new(g.outputs["Normal"], dt.inputs[0]); nt.links.new(g.outputs["Incoming"], dt.inputs[1])
    prof = mm("POWER", mm("ABSOLUTE", dt.outputs["Value"], 0.0), power)
    if beam_len:
        tc = n("ShaderNodeTexCoord"); sp = n("ShaderNodeSeparateXYZ"); nt.links.new(tc.outputs["Object"], sp.inputs[0])
        s = mm("MULTIPLY", sp.outputs["Z"], -1.0 / beam_len, True)
        fin = mm("MINIMUM", mm("MULTIPLY", s, 12.5), 1.0)                  # apex fade-in over the first 8% (no bright point at the lamp)
        fout = mm("POWER", mm("SUBTRACT", 1.0, s, True), 1.5)
        prof = mm("MULTIPLY", prof, mm("MULTIPLY", fin, fout))
    cd = n("ShaderNodeCameraData")   # nothing glows in the camera's face: fade in over 1.5 .. 5 m (a cone whose apex is beside the lens would tint the whole frame)
    prof = mm("MULTIPLY", prof, mm("DIVIDE", mm("SUBTRACT", cd.outputs["View Distance"], 1.5, False), 3.5, True))
    em = n("ShaderNodeEmission"); em.inputs["Color"].default_value = (*col, 1.0); nt.links.new(mm("MULTIPLY", prof, k), em.inputs["Strength"])
    tr = n("ShaderNodeBsdfTransparent"); ad = n("ShaderNodeAddShader"); out = n("ShaderNodeOutputMaterial")
    nt.links.new(tr.outputs[0], ad.inputs[0]); nt.links.new(em.outputs[0], ad.inputs[1]); nt.links.new(ad.outputs[0], out.inputs["Surface"])
    return m

def fx_object(name, me, mat, loc, rot=None):
    me.materials.append(mat); o = bpy.data.objects.new(name, me); o.location = loc
    if rot is not None: o.rotation_euler = rot
    for a in ("visible_diffuse", "visible_glossy", "visible_transmission", "visible_volume_scatter", "visible_shadow"): setattr(o, a, False)
    bpy.context.scene.collection.objects.link(o); return o

def light_fx(lights):
    from mathutils import Vector
    import bmesh; nb = nh = 0; seg = 24
    for l in lights:
        col = light_linear(l["color"]); bp = beam_params(l)
        if bp:
            half, length, k = bp; r = length * math.tan(half)
            me = bpy.data.meshes.new("beam")
            vs = [(0, 0, 0)] + [(r * math.cos(2 * math.pi * i / seg), r * math.sin(2 * math.pi * i / seg), -length) for i in range(seg)]
            me.from_pydata(vs, [], [(0, 1 + i, 1 + (i + 1) % seg) for i in range(seg)]); me.update()
            fx_object("beam", me, fx_material("beam", col, k, length), l["pos"], Vector(l["proj"]["dir"]).to_track_quat("-Z", "Y").to_euler()); nb += 1
        hp = halo_params(l)
        if hp:
            rad, k = hp; me = bpy.data.meshes.new("halo"); bm = bmesh.new(); bmesh.ops.create_uvsphere(bm, u_segments=16, v_segments=8, radius=rad); bm.to_mesh(me); bm.free()
            fx_object("halo", me, fx_material("halo", col, k, None, 3.5), l["pos"]); nh += 1
    log("light fx", "beams", nb, "halos", nh, "beam", LIGHT_BEAM, "halo", LIGHT_HALO)

def eep(env):
    """Region EEP sky at export time (make_job.py): sun or, below the horizon, moon; sky colour as ambient; SL point lights."""
    sun = Vector(env["sun_dir"]).normalized(); night = sun.z < 0
    moon = Vector(env.get("moon_dir") or -sun).normalized()   # EEP moon_rotation (older envs: opposite the sun)
    if os.environ.get("GT_DEBUG_MOON_DIR"): moon = Vector([float(x) for x in os.environ["GT_DEBUG_MOON_DIR"].split(",")]).normalized()   # diagnosis
    if moon.z < 0: moon = -sun   # moon down too: keep the old faint light from above
    travel = -moon if night else -sun
    c = env["sunlight"]
    if HD and env.get("classic") is False: c = [lin(min(x, 8.0)) if x <= 1 else ((x + 0.055) / 1.055) ** 2.4 for x in c]   # PBR sky: srgb_to_linear(sunlit) (atmosphericsFuncs.glsl)
    peak = max(max(c), 1e-3)
    bpy.ops.object.light_add(type="SUN"); L = bpy.context.object; L.rotation_euler = travel.to_track_quat("-Z", "Y").to_euler()
    SUN["light"] = L; SUN["env"] = env; SUN["peak"] = peak; SUN["c"] = c
    L.data.color = [x / peak for x in c]; L.data.energy = (moon_strength(env) if night else 1.2 * peak * GT_SUN_MUL)
    if HD: L.data.angle = math.radians(SHADOW_ANGLE_NIGHT if night else SHADOW_ANGLE_DAY)
    w = bpy.data.worlds.new("w"); sc.world = w; w.use_nodes = True; bg = w.node_tree.nodes["Background"]
    _hz = list(env["horizon"]); _y = 0.2126 * _hz[0] + 0.7152 * _hz[1] + 0.0722 * _hz[2]; _hz = [_y + (c - _y) * (1 - GT_WORLD_GREY) for c in _hz]
    bg.inputs["Color"].default_value = (*_hz, 1); bg.inputs["Strength"].default_value = (0.35 if night else 1.0) * (env.get("probe_ambiance", 1.0) if HD and env.get("classic") is False else 1.0)   # PBR sky: probe ambiance
    if HD and not night and env.get("classic", True) and AMB > 0:
        # 2026-10-14 bikini compare (deck shadows (117, 103, 92) vs Firestorm's (106, 80, 63), rail shadows barely there):
        # the classic sky's ambient is already on every surface as amblit emission; the world's full-strength horizon
        # light on top filled the shadows twice. Keep a quarter for bounce / reflections
        bg.inputs["Strength"].default_value *= float(os.environ.get("GT_CLASSIC_WORLD", "0.1"))
    sky_backdrop(w, env, night)
    shadow_ids = light_shadow_ids(M.get("lights", []), M.get("me") or [0, 0, 0])
    for l in M.get("lights", []):
        # SL point light, reimplemented from the LL viewer's deferred light shader: radiance = albedo x linear colour x
        # intensity x atten x N.L x 3.25/pi, atten = 2 (1 - clamp((d/radius + falloff) / (1 + falloff)))^2, zero past the
        # radius. Cycles: constant falloff (no inverse square), the curve on Light Path ray length (= distance);
        # 4 pi^2 cancels Cycles' point-light normalisation (calibrated: 1 W constant -> 1/(4 pi^2) on a white plane).
        pj = l.get("proj")
        d = bpy.data.lights.new("sl", "SPOT" if pj else "POINT"); d.color = light_linear(l["color"]); d.shadow_soft_size = 0.05; d.use_shadow = id(l) in shadow_ids
        d.energy = 4 * math.pi ** 2 * 3.25 / math.pi * float(os.environ.get("GT_PROJ_SCALE", "1") if pj else os.environ.get("GT_POINT_SCALE", "1")); d.use_nodes = True; nt = d.node_tree; f = l.get("falloff", 0.75) * LIGHT_FALLOFF_FUDGE; radius = l["radius"] * LIGHT_RADIUS_SCALE
        def op(kind, a, b, clamp=False):
            n = nt.nodes.new("ShaderNodeMath"); n.operation = kind; n.use_clamp = clamp
            for i, v in enumerate((a, b)):
                if isinstance(v, (int, float)): n.inputs[i].default_value = v
                else: nt.links.new(v, n.inputs[i])
            return n.outputs[0]
        x = op("DIVIDE", nt.nodes.new("ShaderNodeLightPath").outputs["Ray Length"], max(radius, 0.01))
        k = op("SUBTRACT", 1.0, op("DIVIDE", op("ADD", x, f), 1 + f, clamp=True))
        fo = nt.nodes.new("ShaderNodeLightFalloff"); nt.links.new(op("MULTIPLY", op("POWER", k, 2.0), 2 * l["intensity"]), fo.inputs["Strength"])
        nt.links.new(fo.outputs["Constant"], nt.nodes["Emission"].inputs["Strength"])
        o = bpy.data.objects.new("sl", d); o.location = l["pos"]; sc.collection.objects.link(o)
        if pj: projector(d, o, pj, op)
    if LIGHT_FX: light_fx(M.get("lights", []))
    log("eep", "night" if night else "day", "ambient", round(AMB, 4), "sun", [round(x, 2) for x in sun], "lights", len(M.get("lights", [])))

def proj_spot(fov):
    """SL projector field of view (LightImage params.x, radians, full cone) -> (Blender spot_size, tan(half angle)) """
    f = min(max(float(fov or 0), 0.02), math.pi - 0.02); return f, math.tan(f / 2)

def projector(d, o, pj, op):
    """SL projector (Firestorm "Shadows: sun/moon + projectors"): a spot light down the prim's -Z with the light texture
    projected across the cone (gobo: the light's own Normal coordinate is the ray direction in light space, u,v =
    x,y / -z / tan(fov/2) / 2 + 1/2). ponytail: focus (blur) and ambiance (the fill term) ignored; ceiling = a
    projector looks sharper and its unlit side darker than in the viewer"""
    from mathutils import Vector
    size, t = proj_spot(pj.get("fov")); d.spot_size = size; d.spot_blend = 0.1
    o.rotation_euler = Vector(pj["dir"]).to_track_quat("-Z", "Y").to_euler()
    tp = tex_path(pj.get("tex")) if os.environ.get("GT_PROJ_NOTEX") != "1" else None   # GT_PROJ_NOTEX=1: diagnosis, plain coloured spot
    if not tp: return
    nt = d.node_tree; tc = nt.nodes.new("ShaderNodeTexCoord"); sp = nt.nodes.new("ShaderNodeSeparateXYZ"); nt.links.new(tc.outputs["Normal"], sp.inputs[0])
    z = op("MAXIMUM", op("MULTIPLY", sp.outputs["Z"], -1.0), 1e-4)
    uv = nt.nodes.new("ShaderNodeCombineXYZ")
    for ax, i in (("X", 0), ("Y", 1)): nt.links.new(op("ADD", op("MULTIPLY", op("DIVIDE", sp.outputs[ax], z), 0.5 / t), 0.5), uv.inputs[i])
    im = nt.nodes.new("ShaderNodeTexImage"); im.image = load_image(tp); im.extension = "CLIP"; nt.links.new(uv.outputs[0], im.inputs["Vector"])
    mx = nt.nodes.new("ShaderNodeMix"); mx.data_type = "RGBA"; mx.blend_type = "MULTIPLY"; mx.inputs["Factor"].default_value = 1
    mx.inputs["A"].default_value = (*d.color, 1); nt.links.new(im.outputs["Color"], mx.inputs["B"])
    nt.links.new(mx.outputs["Result"], nt.nodes["Emission"].inputs["Color"])

def sky_seen(env, night):
    """(horizon, overhead, strength) of the sky the camera sees. Night: the LL viewer's own sky colours (llsky.py
    night_sky: ~0.03-0.05, faintly blue, for the Linden legacy midnight), at full strength. Day: the EEP horizon
    colour fading to the blue_density colour scaled to the horizon's brightness (at 0.85 of it the upper sky read as
    a darker picture); before 2026-10-06 night was this day gradient at 0.35, a flat mid-grey ~3x the viewer's."""
    ns = env.get("night_sky") if night else None
    # 2026-10-13 Firestorm reference (Peronaut night): the viewer's night sky colours are what it puts on screen (sRGB:
    # horizon 0.059 = 15/255, the reference's sky exactly); drawn as linear they came out 53/255, a grey sky 3.5x too bright
    if ns: return [srgb_to_linear(c) for c in ns["horizon"]], [srgb_to_linear(c) for c in ns["zenith"]], 1.0
    sk = env.get("sky") if HD and not night else None
    if sk:   # photo hd (2026-10-13): the viewer's sky shader by day too (blue horizon/density, haze, density multiplier, max_y, glow)
        # 2026-10-14 Beach House boat vs David's Firestorm shot: the classic sky shader's output goes to the screen as is
        # (no tone mapping, already display values: horizon 0.41/0.48/0.70 -> his 107/127/184 sRGB). Drawn as linear it
        # came out ~2.5x too bright and grey-lavender, and the water mirrored it pale. Same fix as the night sky above.
        return [srgb_to_linear(min(5.0, c)) * k for c, k in zip(sk["horizon"], SKY_SCALE)], [srgb_to_linear(min(5.0, c)) * k for c, k in zip(sk["zenith"], SKY_SCALE)], 1.0   # HDR (skyF.glsl clamps at 5)
    h = env["horizon"]; z = env.get("zenith", h); k = max(sum(h), 1e-3) / max(sum(z), 1e-3)
    return h, [min(1.0, c * k) for c in z], 0.35 if night else 1.0

def sky_stops(env):
    """photo hd day: the classic sky shader (llsky.sky_rgb = skyF.glsl) at several elevations -> [(sin elev, linear rgb)]
    for a colour ramp; None without the haze params"""
    hz = env.get("haze"); sk = env.get("sky")
    if not hz or not sk or not env.get("sun_dir") or env["sun_dir"][2] < 0 or not env.get("classic", True): return None
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__))); import llsky
    p = {"max_y": env.get("max_y", 1605), "blue_density": hz["blue_density"], "blue_horizon": env["horizon"], "haze_density": hz["haze_density"],
         "haze_horizon": hz["haze_horizon"], "density_multiplier": hz["density_multiplier"], "sunlight": env["sunlight"],
         "glow": env.get("glow", [5, 0, -2.5]), "ambient": env["ambient"], "cloud_shadow": env.get("cloud_shadow", 0)}
    try: return [(math.sin(math.radians(d)), [srgb_to_linear(min(5.0, c)) * k for c, k in zip(llsky.sky_rgb(p, env["sun_dir"][2], True, 1.0, d), SKY_SCALE)])
                 for d in (0.5, 2, 5, 10, 15, 20, 30, 45, 60, 90)]
    except (KeyError, TypeError, ValueError) as e: log("sky stops", e); return None

def sky_backdrop(w, env, night):
    """What the camera (and water reflections) see of the sky: sky_seen's horizon-to-overhead gradient, and at night
    the region's stars (star_brightness). Diffuse lighting still comes from the flat horizon colour it always used, so
    nothing in the scene changes brightness. Without --far the day sky stays the flat horizon colour (#29); the night
    sky is always drawn (the flat day colour at night was the grey sky in the Ahern 2026-10-06 render).
    ponytail: no clouds, sun/moon disc, haze glow; stars are a random field, not the viewer's star map"""
    ns = env.get("night_sky") if night else None
    if not ns and ("zenith" not in env or not M.get("backdrop", True)): return
    nt = w.node_tree; L = nt.links.new; bg = nt.nodes["Background"]
    h, zc, s = sky_seen(env, night)
    co = nt.nodes.new("ShaderNodeTexCoord"); sp = nt.nodes.new("ShaderNodeSeparateXYZ"); L(co.outputs["Generated"], sp.inputs[0])
    mr = nt.nodes.new("ShaderNodeMapRange"); mr.interpolation_type = "SMOOTHSTEP"; mr.inputs["From Min"].default_value = 0.0; mr.inputs["From Max"].default_value = 0.45
    L(sp.outputs["Z"], mr.inputs["Value"])
    mx = nt.nodes.new("ShaderNodeMix"); mx.data_type = "RGBA"; L(mr.outputs["Result"], mx.inputs["Factor"])
    mx.inputs["A"].default_value = (*h, 1); mx.inputs["B"].default_value = (*zc, 1)
    col = mx.outputs["Result"]
    stops = sky_stops(env) if HD and not night else None
    if stops:
        # 2026-10-14 bikini compare: the viewer's sky shader is most saturated 10-25 deg up ((111, 136, 188) at 10 deg);
        # a 2-key horizon -> zenith blend went through grey there ((98, 107, 133) vs his (94, 118, 172))
        cr = nt.nodes.new("ShaderNodeValToRGB"); L(sp.outputs["Z"], cr.inputs["Fac"]); el = cr.color_ramp.elements
        el[0].position = stops[0][0]; el[0].color = (*stops[0][1], 1); el[1].position = stops[-1][0]; el[1].color = (*stops[-1][1], 1)
        for z_, c_ in stops[1:-1]: e_ = el.new(z_); e_.color = (*c_, 1)
        col = cr.outputs["Color"]
    if ns and ns.get("stars", 0) > 0:
        # stars: Voronoi cells on the view direction, a point at each cell's centre, random brightness, above the horizon
        vo = nt.nodes.new("ShaderNodeTexVoronoi"); vo.inputs["Scale"].default_value = 60.0; L(co.outputs["Generated"], vo.inputs["Vector"])
        pt = nt.nodes.new("ShaderNodeMath"); pt.operation = "LESS_THAN"; pt.inputs[1].default_value = 0.06; L(vo.outputs["Distance"], pt.inputs[0])
        rb = nt.nodes.new("ShaderNodeSeparateColor"); L(vo.outputs["Color"], rb.inputs[0])
        pw = nt.nodes.new("ShaderNodeMath"); pw.operation = "POWER"; pw.inputs[1].default_value = 3.0; L(rb.outputs["Red"], pw.inputs[0])
        st = nt.nodes.new("ShaderNodeMath"); st.operation = "MULTIPLY"; L(pt.outputs[0], st.inputs[0]); L(pw.outputs[0], st.inputs[1])
        hz = nt.nodes.new("ShaderNodeMapRange"); hz.inputs["From Min"].default_value = 0.02; hz.inputs["From Max"].default_value = 0.15; L(sp.outputs["Z"], hz.inputs["Value"])
        st2 = nt.nodes.new("ShaderNodeMath"); st2.operation = "MULTIPLY"; L(st.outputs[0], st2.inputs[0]); L(hz.outputs["Result"], st2.inputs[1])
        st3 = nt.nodes.new("ShaderNodeMath"); st3.operation = "MULTIPLY"; st3.inputs[1].default_value = 0.9 * ns["stars"]; L(st2.outputs[0], st3.inputs[0])
        # camera rays only (2026-10-13 Firestorm reference): glossy glass / water picked the star points up as white specks
        # all over the porch windows; the viewer's reflection probes blur them away
        stc = nt.nodes.new("ShaderNodeMath"); stc.operation = "MULTIPLY"; L(st3.outputs[0], stc.inputs[0])
        L(nt.nodes.new("ShaderNodeLightPath").outputs["Is Camera Ray"], stc.inputs[1]); st3 = stc
        ad = nt.nodes.new("ShaderNodeMix"); ad.data_type = "RGBA"; ad.blend_type = "ADD"; ad.inputs["Factor"].default_value = 1.0
        L(col, ad.inputs["A"]); L(st3.outputs[0], ad.inputs["B"]); col = ad.outputs["Result"]
    if os.environ.get("GT_HD") == "1": col = sky_hd(nt, co, sp, col, env, night)
    seen = nt.nodes.new("ShaderNodeBackground"); L(col, seen.inputs["Color"]); seen.inputs["Strength"].default_value = s
    lp = nt.nodes.new("ShaderNodeLightPath"); mm = nt.nodes.new("ShaderNodeMath"); mm.operation = "MAXIMUM"
    L(lp.outputs["Is Camera Ray"], mm.inputs[0]); L(lp.outputs["Is Glossy Ray"], mm.inputs[1])
    ms = nt.nodes.new("ShaderNodeMixShader"); L(mm.outputs[0], ms.inputs["Fac"]); L(bg.outputs[0], ms.inputs[1]); L(seen.outputs[0], ms.inputs[2])
    L(ms.outputs[0], nt.nodes["World Output"].inputs["Surface"])
    # the lighting part is still the flat colour: no world importance map (that map of the gradient/stars doubled the
    # render, 2.9 -> 6.3 s on the Ahern look); BSDF rays that escape still see the flat colour
    try: w.cycles.sampling_method = "NONE"
    except (AttributeError, TypeError): pass

def sky_hd(nt, co, sp, col, env, night):
    """photo hd (2026-10-13): what the SL sky adds on top of the gradient: the region's cloud layer (EEP cloud colour,
    coverage, density, scale; flat dome projection, fading at the horizon), the sun's haze glow and disc by day, the
    moon disc by night. ponytail: procedural noise, not the region's cloud texture; no cloud lighting or scrolling"""
    L = nt.links.new
    def node(kind, **kw):
        n = nt.nodes.new(kind)
        for k, v in kw.items(): setattr(n, k, v)
        return n
    def math_(op, a, b=None, clamp=False):
        n = node("ShaderNodeMath", operation=op, use_clamp=clamp)
        for i, v in enumerate((a, b)):
            if v is None: continue
            if isinstance(v, (int, float)): n.inputs[i].default_value = v
            else: L(v, n.inputs[i])
        return n.outputs[0]
    def mixc(f, a, b, blend="MIX"):
        m = node("ShaderNodeMix", data_type="RGBA", blend_type=blend)
        if isinstance(f, (int, float)): m.inputs["Factor"].default_value = f
        else: L(f, m.inputs["Factor"])
        for k, v in (("A", a), ("B", b)):
            if isinstance(v, (list, tuple)): m.inputs[k].default_value = (*v[:3], 1)
            else: L(v, m.inputs[k])
        return m.outputs["Result"]
    cl = env.get("clouds") or {}
    cov = float(cl.get("coverage", 0)); dens = float(cl.get("density", 1))
    if cov > 0.01 and dens > 0.01:
        # dome projection: xy / (z + 0.08); EEP cloud_scale ~0.42 -> features a few hundred metres across
        dz = math_("ADD", sp.outputs["Z"], 0.08); vx = math_("DIVIDE", sp.outputs["X"], dz); vy = math_("DIVIDE", sp.outputs["Y"], dz)
        cv = node("ShaderNodeCombineXYZ"); L(vx, cv.inputs[0]); L(vy, cv.inputs[1])
        nz = node("ShaderNodeTexNoise"); nz.inputs["Scale"].default_value = 1.2 / max(0.05, float(cl.get("scale", 0.42))); nz.inputs["Detail"].default_value = 6
        off = cl.get("offset") or [0, 0]   # EEP cloud position + scroll rate x export time (llsky.build_env)
        sh = node("ShaderNodeVectorMath", operation="ADD"); L(cv.outputs[0], sh.inputs[0]); sh.inputs[1].default_value = (off[0] * 8, off[1] * 8, 0)
        L(sh.outputs[0], nz.inputs["Vector"])
        nz.inputs["Roughness"].default_value = min(0.85, 0.35 + 0.5 * float(cl.get("detail", 0.125)))   # cloud_pos_density2 z = detail layer
        mr = node("ShaderNodeMapRange", interpolation_type="SMOOTHSTEP"); L(nz.outputs["Fac"], mr.inputs["Value"])
        # 2026-10-14 bikini compare: classic cloudsF.glsl takes cloudDensity = 2 x (cloud_shadow - 0.25), so a coverage of
        # 0.27 is a near-clear sky in Firestorm; the old 0.56 - 0.4 x cov threshold drew a half-clouded sky at any coverage
        cd = max(0.0, min(1.0, 2.0 * (cov - 0.25))) if env.get("classic", True) else cov
        mr.inputs["From Min"].default_value = 0.64 - 0.45 * cd; mr.inputs["From Max"].default_value = 0.80 - 0.45 * cd
        hz = node("ShaderNodeMapRange"); L(sp.outputs["Z"], hz.inputs["Value"]); hz.inputs["From Min"].default_value = 0.02; hz.inputs["From Max"].default_value = 0.25
        k = math_("MULTIPLY", math_("MULTIPLY", mr.outputs["Result"], hz.outputs["Result"]), min(1.0, dens))
        h, zc, _ = sky_seen(env, night)
        lit = [min(1.0, c * (sum(h) / 3 * 1.5 + 0.2)) for c in cl.get("color", [0.41] * 3)]   # was x2.2+0.25: near-white
        # night (2026-10-13 Firestorm reference): moonlit clouds barely part from the sky; the old 0.25 x day lighting drew
        # grey clouds ~3x the (linear) night sky. Now the sky's own colour, lifted by the cloud colour
        if night: lit = [z_ * (1.0 + c) for z_, c in zip(zc, cl.get("color", [0.41] * 3))]
        col = mixc(k, col, lit)
    up = [nt.nodes.new("ShaderNodeCombineXYZ")]
    def disc(dirv, cos_r, colr, strength):
        cx = up[0] if not hasattr(disc, "used") else nt.nodes.new("ShaderNodeCombineXYZ"); disc.used = True
        for i in range(3): cx.inputs[i].default_value = dirv[i]
        dp = node("ShaderNodeVectorMath", operation="DOT_PRODUCT"); L(co.outputs["Generated"], dp.inputs[0]); L(cx.outputs[0], dp.inputs[1])
        return dp.outputs["Value"]
    cam = node("ShaderNodeLightPath").outputs["Is Camera Ray"]   # discs/glow for the camera only: in glossy rays they were fireflies
    if not night:
        sun = env["sun_dir"]; sl = env.get("sunlight", [1, 1, 1]); pk = max(max(sl), 1e-3)
        d = disc(sun, 0, sl, 1)
        # EEP glow (size x, focus z) as skyF.glsl: ((1 - view.sun) * x) ^ z, scaled so the default (5, -0.48) keeps the
        # previous 0.35 at the sun; haze_horizon (default 0.19) scales it like the viewer's haze glow term
        gx, _, gz = (list(env.get("glow") or [5, 0.001, -0.48]) + [0, 0, 0])[:3]; gz = min(-0.05, gz)
        hh = (env.get("haze") or {}).get("haze_horizon", 0.19)
        g0 = (0.001 * 5) ** -0.48
        gv = math_("POWER", math_("MULTIPLY", math_("MAXIMUM", math_("SUBTRACT", 1.0, d), 0.001), max(0.01, gx)), gz)
        glow = math_("MULTIPLY", math_("MULTIPLY", math_("MINIMUM", gv, 4 * g0), 0.35 / g0 * min(3.0, hh / 0.19)), cam)
        col = mixc(glow, col, [c / pk for c in sl], "ADD")
        sd = math_("MULTIPLY", math_("GREATER_THAN", d, math.cos(math.radians(0.6 * max(0.05, env.get("sun_scale", 1.0))))), cam)
        col = mixc(sd, col, [c / pk * 8 for c in sl])
    elif env.get("moon_dir") and env["moon_dir"][2] > 0:
        d = disc(env["moon_dir"], 0, None, 1)
        md = math_("MULTIPLY", math_("GREATER_THAN", d, math.cos(math.radians(MOON_RADIUS_DEG * max(0.05, env.get("moon_scale", 1.0))))), cam)
        # the moon's face (2026-10-13): Firestorm draws the moon texture (grey disc with dark maria); procedural stand-in:
        # two noise octaves on the view direction -> darker maria patches, edges a touch darker (limb)
        mn = node("ShaderNodeTexNoise"); mn.inputs["Scale"].default_value = 55.0; mn.inputs["Detail"].default_value = 3
        L(co.outputs["Generated"], mn.inputs["Vector"])
        maria = node("ShaderNodeMapRange"); L(mn.outputs["Fac"], maria.inputs["Value"])
        maria.inputs["From Min"].default_value = 0.45; maria.inputs["From Max"].default_value = 0.6
        maria.inputs["To Min"].default_value = 0.62; maria.inputs["To Max"].default_value = 1.0
        limb = node("ShaderNodeMapRange"); L(d, limb.inputs["Value"])
        limb.inputs["From Min"].default_value = math.cos(math.radians(MOON_RADIUS_DEG * max(0.05, env.get("moon_scale", 1.0))))
        limb.inputs["From Max"].default_value = 1.0; limb.inputs["To Min"].default_value = 0.8; limb.inputs["To Max"].default_value = 1.0
        face = math_("MULTIPLY", maria.outputs["Result"], limb.outputs["Result"])
        mc = node("ShaderNodeCombineColor"); b_ = 0.55 * max(0.3, env.get("moon", 0.5))
        for i in range(3): L(math_("MULTIPLY", face, b_ * (1.0, 0.98, 0.94)[i]), mc.inputs[i])
        col = mixc(md, col, mc.outputs["Color"])
    return col

MIST_ERODE = int(os.environ.get("GT_MIST_ERODE", "-1"))   # px; -1 = auto (1 px per 1080 of the frame's short side, at least 1), 0 = off
HAZE = None   # (k per metre, mist depth m, fog colour) of a batch pass: its ONE per-layer fog fraction is written next to the layer

INDOOR_HAZE = os.environ.get("GT_INDOOR_HAZE", "")   # fog colour scale with the camera under a roof; "" = GT_INDOOR_AMB, 1 = old

def indoor_haze():
    """the haze in-scatter colour is sky light; under a roof (Warehouse 21) the far walls fogged to a pale grey-blue glow
    (Oct 19 2026). Camera under geometry within 80 m straight up -> scale the fog colour (base and batch layers alike)"""
    f = float(INDOOR_HAZE) if INDOOR_HAZE else INDOOR_AMB
    if f >= 1: return 1.0
    cam = sc.camera
    if cam is None: return 1.0
    hit = sc.ray_cast(bpy.context.evaluated_depsgraph_get(), cam.matrix_world.translation, Vector((0.0, 0.0, 1.0)), distance=80.0)[0]
    log("indoor haze", "roof" if hit else "open sky", round(f, 3) if hit else 1.0)
    return f if hit else 1.0

def env_post(env):
    """photo hd: EEP scene gamma and distance haze (the viewer's atmospheric fog on far geometry).
    gamma: LL scaleSoftClip 1 - (1 - c)^gamma ~ Blender view gamma (1 = unchanged). Haze: transmittance
    exp(-(blue_density + haze_density) x density_multiplier x distance_multiplier x metres) toward the sky's horizon colour,
    from Cycles' mist pass (linear 0..4 km) in the compositor; the sky itself (mist = 1) is left as drawn.
    ponytail: one grey extinction (mean of the channels), not per-channel; no in-scatter toward the sun"""
    g = float(env.get("gamma", 1.0) or 1.0)   # applied in hdr_post (legacyGamma / exposure range), not here
    hz = env.get("haze") or {}
    k = (sum(hz.get("blue_density", [0, 0, 0])) / 3 + hz.get("haze_density", 0)) * hz.get("density_multiplier", 0) * hz.get("distance_multiplier", 0)
    log("env post", env.get("source"), "gamma", round(g, 3), "haze k/m", round(k, 6))
    if k < 1e-6: return
    D = 4000.0
    sc.view_layers[0].use_pass_mist = True; sc.world.mist_settings.start = 0.0; sc.world.mist_settings.depth = D; sc.world.mist_settings.falloff = "LINEAR"
    sc.use_nodes = True; nt = sc.node_tree; L = nt.links.new
    rl = nt.nodes.get("Render Layers") or nt.nodes.new("CompositorNodeRLayers")
    cp = nt.nodes.get("Composite") or nt.nodes.new("CompositorNodeComposite")
    def m(op, a, b):
        n = nt.nodes.new("CompositorNodeMath"); n.operation = op
        for i, v in enumerate((a, b)):
            if isinstance(v, (int, float)): n.inputs[i].default_value = v
            else: L(v, n.inputs[i])
        return n.outputs[0]
    mist = rl.outputs["Mist"]; er_px = MIST_ERODE if MIST_ERODE >= 0 else max(1, round(min(sc.render.resolution_x, sc.render.resolution_y) / 1080))
    if LAYER != "batch" and er_px > 0:
        # the per-pixel mist of a pixel that is half hashed-alpha hair / anti-aliased edge and half far sky (or backdrop) is the MEAN of 0.0008 and 1.0,
        # i.e. ~0.5 = 2000 m of haze on a surface 3 m away, and it is not denoised like the colour: every such pixel was fogged to the sky's pale
        # lavender (the speckled hair, the fringe round the hair and the stage truss, Oct 18 2026). The nearest surface in a few pixels decides the
        # fog instead (a minimum filter = erode): the blend of near + far is then fogged like the near one, the far one's own 1-2 px edge a hair less
        er = nt.nodes.new("CompositorNodeDilateErode"); er.mode = "DISTANCE"; er.distance = -er_px; L(mist, er.inputs[0]); mist = er.outputs[0]
    ex = m("EXPONENT", m("MULTIPLY", mist, -k * D), 0)
    fog = m("MULTIPLY", m("SUBTRACT", 1.0, ex), m("LESS_THAN", mist, 0.9999))
    h, _, s = sky_seen(env, (env.get("sun_dir") or [0, 0, 1])[2] < 0)
    s *= indoor_haze()
    if LAYER == "batch":
        # premultiplied RGBA of a holdout pass = avatars only, NO haze or sky colour in any pixel: the per-pixel mist at an anti-aliased or
        # depth-of-field-blurred silhouette is a blend with the sky / the holdout in front (20-40x the avatar's own), and fogging with it painted a
        # pale sky-blue fringe round every background avatar (Oct 18 2026). The Mist pass goes to a file; hd_render_hdr turns the SOLID
        # pixels' mist into one fog fraction per layer (-haze.json) and look.py adds that haze when it composites (layers.alpha_over).
        global HAZE
        HAZE = (k, D, [c * s for c in h])
        mdir = os.environ["GT_LAYER_NPY"][:-4] + "-mist"
        fo = nt.nodes.new("CompositorNodeOutputFile"); fo.base_path = mdir; fo.format.file_format = "OPEN_EXR"; fo.format.color_depth = "32"
        fo.file_slots[0].path = "mist"; L(rl.outputs["Mist"], fo.inputs[0])
        L(rl.outputs["Image"], cp.inputs["Image"])
        return
    mx = nt.nodes.new("CompositorNodeMixRGB"); L(fog, mx.inputs[0]); L(rl.outputs["Image"], mx.inputs[1]); mx.inputs[2].default_value = (*[c * s for c in h], 1)
    L(mx.outputs[0], cp.inputs["Image"])

def particle_systems():
    """photo hd: SL particles (particles.py snapshot) as small camera-facing soft discs: emissive ones glow in their
    colour (the purple orb), the rest are lit, alpha = the particle's alpha. ponytail: round sprites, not the particle
    texture; spheres face the camera from every side"""
    srcs = M.get("particles") or []
    if not (HD and srcs): return
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__))); import particles
    m = bpy.data.materials.new("sl_particle"); m.use_nodes = True; nt = m.node_tree; L = nt.links.new
    for n in list(nt.nodes):
        if n.type != "OUTPUT_MATERIAL": nt.nodes.remove(n)
    at = nt.nodes.new("ShaderNodeAttribute"); at.attribute_name = "sl_pcol"; at.attribute_type = "OBJECT"
    em = nt.nodes.new("ShaderNodeEmission"); L(at.outputs["Color"], em.inputs["Color"])
    ae = nt.nodes.new("ShaderNodeAttribute"); ae.attribute_name = "sl_pem"; ae.attribute_type = "OBJECT"
    df = nt.nodes.new("ShaderNodeBsdfDiffuse"); L(at.outputs["Color"], df.inputs["Color"])
    sh = nt.nodes.new("ShaderNodeMixShader"); L(ae.outputs["Fac"], sh.inputs["Fac"]); L(df.outputs[0], sh.inputs[1]); L(em.outputs[0], sh.inputs[2])
    lw = nt.nodes.new("ShaderNodeLayerWeight"); lw.inputs["Blend"].default_value = 0.5
    soft = nt.nodes.new("ShaderNodeMath"); soft.operation = "SUBTRACT"; soft.inputs[0].default_value = 1.0; L(lw.outputs["Facing"], soft.inputs[1])
    aa = nt.nodes.new("ShaderNodeAttribute"); aa.attribute_name = "sl_palpha"; aa.attribute_type = "OBJECT"
    al = nt.nodes.new("ShaderNodeMath"); al.operation = "MULTIPLY"; L(soft.outputs[0], al.inputs[0]); L(aa.outputs["Fac"], al.inputs[1])
    tr = nt.nodes.new("ShaderNodeBsdfTransparent"); mx = nt.nodes.new("ShaderNodeMixShader")
    L(al.outputs[0], mx.inputs["Fac"]); L(tr.outputs[0], mx.inputs[1]); L(sh.outputs[0], mx.inputs[2])
    L(mx.outputs[0], nt.nodes["Material Output"].inputs["Surface"])
    bpy.ops.mesh.primitive_uv_sphere_add(segments=12, ring_count=8, radius=0.5); proto = bpy.context.object.data; bpy.data.objects.remove(bpy.context.object)
    proto.materials.append(m); n = 0
    for i, s_ in enumerate(srcs):
        for q in particles.snapshot(s_["ps"], s_["pos"], seed=i):
            ob = bpy.data.objects.new("sl_particle", proto); sc.collection.objects.link(ob)
            ob.location = q["pos"]; ob.scale = (q["size"][0], q["size"][0], q["size"][1])
            c = q["color"]; k = 1.0 + 2.0 * q["glow"]
            ob["sl_pcol"] = [c[0] * k, c[1] * k, c[2] * k]; ob["sl_pem"] = 1.0 if q["emissive"] else 0.0; ob["sl_palpha"] = c[3]
            ob.visible_shadow = False; n += 1
    log("particles", len(srcs), "sources", n, "particles")

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
    night = (env.get("sun_dir") or [0, 0, 1])[2] < 0
    h, zc, s = sky_seen({"horizon": [0.5, 0.5, 0.6], **env}, night)
    co = nt.nodes.new("ShaderNodeTexCoord"); sp = nt.nodes.new("ShaderNodeSeparateXYZ"); L(co.outputs["Reflection"], sp.inputs[0])
    mr = nt.nodes.new("ShaderNodeMapRange"); mr.interpolation_type = "SMOOTHSTEP"; mr.inputs["From Max"].default_value = 0.45; L(sp.outputs["Z"], mr.inputs["Value"])
    sky = nt.nodes.new("ShaderNodeMix"); sky.data_type = "RGBA"; L(mr.outputs["Result"], sky.inputs["Factor"])
    sky.inputs["A"].default_value = (*h, 1); sky.inputs["B"].default_value = (*zc, 1)
    lw = nt.nodes.new("ShaderNodeLayerWeight"); lw.inputs["Blend"].default_value = 0.35
    mx = nt.nodes.new("ShaderNodeMix"); mx.data_type = "RGBA"; L(lw.outputs["Fresnel"], mx.inputs["Factor"])
    # sea body = the horizon colour at ~80%, a little bluer (was a near-black (0.02, 0.05, 0.07): the sea fills a third of
    # most frames where the pre-backdrop render showed the flat horizon colour, so the picture read as darker, David 2026-10-04)
    mx.inputs["A"].default_value = (h[0] * 0.72, h[1] * 0.8, h[2] * 0.9, 1); L(sky.outputs["Result"], mx.inputs["B"])
    wt = env.get("water") if HD else None
    if wt and not night and os.environ.get("GT_FS_WATER", "1") == "1":
        water_fs(ob, m, nt, wt); return
    if wt:   # photo hd: the EEP water: fog colour as the sea body (lit like the viewer: x the horizon brightness), fresnel scale
        hb = max(0.15, sum(h) / 3 * 1.6); fd = min(1.0, float(wt.get("fog_density", 10)) / 10.0)
        # 2026-10-14: the EEP fog colour is sRGB (Firestorm: waterFogColorLinear = srgb_to_linear); used raw it read pale
        fc = [min(1.0, lin(c) * hb * (2.4 - fd)) for c in wt["fog_color"]]
        # night (2026-10-13 Firestorm reference: the bay reads (45, 52, 68)/255, ours was (15, 19, 23)): the EEP fog colour
        # is a display colour, lifted by the classic sky's ambient as the viewer's water shader does
        if night: fc = [lin(min(1.0, c + 0.35 * AMB_SRGB(env))) for c in wt["fog_color"]]
        mx.inputs["A"].default_value = (*fc, 1)
        lw.inputs["Blend"].default_value = max(0.05, min(0.95, float(wt.get("fresnel_scale", 0.4)) * 0.875))
        # 2026-10-14 bikini compare: the far sea read sky-pale (108, 125, 164) vs Firestorm's (55, 70, 99): the grazing
        # fresnel went to a full sky mirror. Firestorm's water reflects at fresnel_scale x fresnel + offset into a darker
        # probe; cap the sky share at the scale
        fm = nt.nodes.new("ShaderNodeMath"); fm.operation = "MULTIPLY"; L(lw.outputs["Fresnel"], fm.inputs[0])
        fm.inputs[1].default_value = min(1.0, float(wt.get("fresnel_scale", 0.4))); L(fm.outputs[0], mx.inputs["Factor"])
    col = mx.outputs["Result"]
    lt = (env.get("moon_dir") if night else env.get("sun_dir")) if HD else None
    if lt and lt[2] > 0:
        # photo hd (2026-10-13 audit): the sun / moon glitter path on the waves (Firestorm water: specular off the wave
        # normal maps). A noise-perturbed reflection vector against the light: a broken streak toward the light
        nz = nt.nodes.new("ShaderNodeTexNoise"); nz.inputs["Scale"].default_value = 3.0; nz.inputs["Detail"].default_value = 6
        L(co.outputs["Object"], nz.inputs["Vector"])
        pv = nt.nodes.new("ShaderNodeVectorMath"); pv.operation = "SUBTRACT"; L(nz.outputs["Color"], pv.inputs[0]); pv.inputs[1].default_value = (0.5, 0.5, 0.5)
        sv = nt.nodes.new("ShaderNodeVectorMath"); sv.operation = "SCALE"; L(pv.outputs[0], sv.inputs[0]); sv.inputs["Scale"].default_value = 0.25
        rv = nt.nodes.new("ShaderNodeVectorMath"); rv.operation = "ADD"; L(co.outputs["Reflection"], rv.inputs[0]); L(sv.outputs[0], rv.inputs[1])
        nv = nt.nodes.new("ShaderNodeVectorMath"); nv.operation = "NORMALIZE"; L(rv.outputs[0], nv.inputs[0])
        dp = nt.nodes.new("ShaderNodeVectorMath"); dp.operation = "DOT_PRODUCT"; L(nv.outputs[0], dp.inputs[0]); dp.inputs[1].default_value = tuple(lt)
        pw = nt.nodes.new("ShaderNodeMath"); pw.operation = "POWER"; L(dp.outputs["Value"], pw.inputs[0]); pw.inputs[1].default_value = 600.0
        gk = nt.nodes.new("ShaderNodeMath"); gk.operation = "MULTIPLY"; L(pw.outputs[0], gk.inputs[0])
        gk.inputs[1].default_value = (0.6 * max(0.3, env.get("moon", 0.5))) if night else 4.0
        ad = nt.nodes.new("ShaderNodeMix"); ad.data_type = "RGBA"; ad.blend_type = "ADD"; L(gk.outputs[0], ad.inputs["Factor"])
        L(col, ad.inputs["A"]); ad.inputs["B"].default_value = (1, 1, 0.95, 1); col = ad.outputs["Result"]
    em = nt.nodes.new("ShaderNodeEmission"); L(col, em.inputs["Color"]); em.inputs["Strength"].default_value = s
    out = em.outputs[0]
    if HD and os.environ.get("GT_TRANSPARENT_WATER", "1") == "1":
        # Transparent Water (High+ on): the shallows show through, the deep / grazing view is the sea body (fresnel)
        tr = nt.nodes.new("ShaderNodeBsdfTransparent"); ms = nt.nodes.new("ShaderNodeMixShader")
        op_ = nt.nodes.new("ShaderNodeMath"); op_.operation = "MAXIMUM"; L(lw.outputs["Fresnel"], op_.inputs[0]); op_.inputs[1].default_value = 0.88
        L(op_.outputs[0], ms.inputs["Fac"]); L(tr.outputs[0], ms.inputs[1]); L(out, ms.inputs[2]); out = ms.outputs[0]
    L(out, nt.nodes["Material Output"].inputs["Surface"])
    ob.data.materials.append(m); ob.visible_shadow = False; ob.visible_diffuse = False; ob.visible_glossy = False

def water_fs(ob, m, nt, wt):
    """photo hd day water as Firestorm's PBR viewer draws it (class3/environment/waterF.glsl, 2026-10-14 bikini compare:
    the emission-only sea had no waves, no sparkle and mirrored the sky pale where his is deep blue):
    - wave normals: two layers of ripples (the viewer scrolls its normal map at wave1 / wave2, x normal_scale); a noise
      height field through a bump here (ponytail: not the 822ded49 normal map texture itself)
    - fresnel per wave: df = dot(view, n) x fresnel_scale + fresnel_offset; mix factor min(1, 3 df^2) (df3 summed),
      reflection weight df (df2.y). The viewer's view vector points from the eye to the water (dot < 0), Blender's
      Incoming the other way: -dot. So the grazing sea mirrors most (0.5) and looking down shows the deep fog colour
    - reflection = probe + SSR: a traced GGX mirror (roughness = blur_multiplier) of the sky and the scene, x df; the
      sun's punctual specular on the waves comes out of the same lobe (the sparkle path)
    - refraction / fog: deep water is getWaterFogViewNoClip -> srgb_to_linear(fog colour) x ~1 (no lighting);
      the shallows show through (Transparent Water) as before"""
    L = nt.links.new; N = lambda t: nt.nodes.new(t)
    geo = N("ShaderNodeNewGeometry")
    ns = max(0.5, sum(wt.get("normal_scale") or [2, 2, 2]) / 3)
    h = None
    for sc_, amp, w_ in ((0.9, 1.0, wt.get("wave1") or [1, 0]), (3.5, 0.4, wt.get("wave2") or [1, -1])):   # ~1 m swell, ~25 cm ripples
        mp = N("ShaderNodeMapping"); L(geo.outputs["Position"], mp.inputs["Vector"])
        mp.inputs["Scale"].default_value = (sc_, sc_ * (0.55 + 0.1 * abs(w_[1])), sc_ * 0.5)   # ripples stretched across the wind
        mp.inputs["Rotation"].default_value = (0, 0, math.atan2(w_[1], w_[0]))
        nz = N("ShaderNodeTexNoise"); nz.inputs["Scale"].default_value = 1.0; nz.inputs["Detail"].default_value = 4; nz.inputs["Roughness"].default_value = 0.55
        L(mp.outputs["Vector"], nz.inputs["Vector"])
        k = N("ShaderNodeMath"); k.operation = "MULTIPLY"; L(nz.outputs["Fac"], k.inputs[0]); k.inputs[1].default_value = amp
        if h is None: h = k.outputs[0]
        else:
            ad = N("ShaderNodeMath"); ad.operation = "ADD"; L(h, ad.inputs[0]); L(k.outputs[0], ad.inputs[1]); h = ad.outputs[0]
    bm = N("ShaderNodeBump"); bm.inputs["Strength"].default_value = min(1.0, 0.3 * ns); bm.inputs["Distance"].default_value = 0.3
    L(h, bm.inputs["Height"]); nrm = bm.outputs["Normal"]
    dp = N("ShaderNodeVectorMath"); dp.operation = "DOT_PRODUCT"; L(geo.outputs["Incoming"], dp.inputs[0]); L(nrm, dp.inputs[1])
    df = N("ShaderNodeMath"); df.operation = "MULTIPLY_ADD"; df.use_clamp = False; L(dp.outputs["Value"], df.inputs[0])
    df.inputs[1].default_value = -float(wt.get("fresnel_scale", 0.4)); df.inputs[2].default_value = float(wt.get("fresnel_offset", 0.5))
    d2 = N("ShaderNodeMath"); d2.operation = "MULTIPLY"; L(df.outputs[0], d2.inputs[0]); L(df.outputs[0], d2.inputs[1])
    kx = N("ShaderNodeMath"); kx.operation = "MULTIPLY"; kx.use_clamp = True; L(d2.outputs[0], kx.inputs[0]); kx.inputs[1].default_value = 3.0
    gl = N("ShaderNodeBsdfGlossy"); gl.distribution = "GGX"; gl.inputs["Roughness"].default_value = max(0.02, float(wt.get("blur_multiplier", 0.04)))
    L(nrm, gl.inputs["Normal"])
    rf = N("ShaderNodeMath"); rf.operation = "MULTIPLY"; L(df.outputs[0], rf.inputs[0]); rf.inputs[1].default_value = WATER_REFL
    cw = N("ShaderNodeCombineColor"); [L(rf.outputs[0], cw.inputs[i]) for i in range(3)]; L(cw.outputs[0], gl.inputs["Color"])
    fb = N("ShaderNodeEmission"); fb.inputs["Color"].default_value = (*[lin(c) for c in wt["fog_color"]], 1); fb.inputs["Strength"].default_value = 1.0
    ms = N("ShaderNodeMixShader"); L(kx.outputs[0], ms.inputs["Fac"]); L(fb.outputs[0], ms.inputs[1]); L(gl.outputs[0], ms.inputs[2])
    out = ms.outputs[0]
    if os.environ.get("GT_TRANSPARENT_WATER", "1") == "1":
        lw = N("ShaderNodeLayerWeight"); lw.inputs["Blend"].default_value = 0.35
        tr = N("ShaderNodeBsdfTransparent"); mt = N("ShaderNodeMixShader")
        op_ = N("ShaderNodeMath"); op_.operation = "MAXIMUM"; L(lw.outputs["Fresnel"], op_.inputs[0]); op_.inputs[1].default_value = 0.97   # the seabed shows unfogged here (Firestorm fogs it by depth): a hint only
        L(op_.outputs[0], mt.inputs["Fac"]); L(tr.outputs[0], mt.inputs[1]); L(out, mt.inputs[2]); out = mt.outputs[0]
    L(out, nt.nodes["Material Output"].inputs["Surface"])
    ob.data.materials.append(m); ob.visible_shadow = False; ob.visible_diffuse = False; ob.visible_glossy = False
    log("water: firestorm waterF (waves, fresnel", wt.get("fresnel_scale"), "+", wt.get("fresnel_offset"), ", traced reflection, fog)")

TERRAIN_FALLBACK = [(0.33, 0.27, 0.18), (0.22, 0.30, 0.12), (0.30, 0.30, 0.24), (0.38, 0.37, 0.35)]  # dirt, grass, mountain, rock

# the moon's apparent radius: Firestorm draws it ~5.3 deg across at moon_scale 1 (2026-10-13 reference, 60 deg FOV);
# 0.9 deg was a dot a sixth that size
MOON_RADIUS_DEG = 2.6

def srgb_to_linear(c):
    c = max(0.0, float(c or 0.0))
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4

def z0_of(fl, ll, lo, seated=False):
    """mesh z 0 for an avatar: seated -> the sim's placement (ll); standing -> lowest vertex on the floor unless ll is
    more than 0.35 m off it (a seat / table / hover under her)"""
    if seated and ll is not None: return ll
    return fl - lo if ll is None or abs(ll + lo - fl) < 0.35 else ll

def omit_stand_in(env):
    """HD photos leave out an avatar that would be a stand-in (GT_STANDIN=1 forces it back)"""
    return env.get("GT_HD") == "1" and env.get("GT_STANDIN") != "1"

def terrain_layers(verts, comps):
    """LLVLComposition: per vertex, which of the region's 4 detail textures (0..3, fractional = blend): (height - start) * 4 /
    range, start and range bilinear across the region's corners (00 SW, 01 NW, 10 SE, 11 NE). The viewer adds noise; the
    shader does (Noise texture). A vertex outside every region uses the nearest region's values."""
    out = []
    for x, y, z in verts:
        c = min(comps, key=lambda c: max(0, abs(x - c["offset"][0] - 128) - 128) + max(0, abs(y - c["offset"][1] - 128) - 128))
        u = min(1, max(0, (x - c["offset"][0]) / 256)); v = min(1, max(0, (y - c["offset"][1]) / 256))
        bil = lambda a: sum((w or 0.0) * f for w, f in zip(a or (0, 0, 0, 0), ((1 - u) * (1 - v), (1 - u) * v, u * (1 - v), u * v)))   # null (= 0 in OSD JSON) safe
        out.append(min(3.0, max(0.0, (z - bil(c["start"])) * 4 / max(bil(c["range"]), 1e-3))))
    return out

def terrain():
    # quick looks: one flat green material, a 4 m grid; a point with no land data sits at 20 m
    # photo hd (2026-10-13): the region's own detail textures blended by height (LL's composition), no faces where there is
    # no land data (old exports: no green slab), none wholly under water (the sea covers them; the sky stays visible)
    t = json.load(open(f"{jobdir}/terrain.json")); nx, ny, st = t["nx"], t["ny"], t["step"]
    if nx < 2: return
    hd = os.environ.get("GT_HD") == "1"; H = t["heights"]; wh = M.get("water_height")
    verts = [(t.get("x0", 0) + i * st, t.get("y0", 0) + j * st, H[j][i] if H[j][i] is not None else 20.0) for j in range(ny) for i in range(nx)]
    faces = []
    for j in range(ny - 1):
        for i in range(nx - 1):
            q = (j * nx + i, j * nx + i + 1, (j + 1) * nx + i + 1, (j + 1) * nx + i)
            if hd:
                hs = [H[j][i], H[j][i + 1], H[j + 1][i + 1], H[j + 1][i]]
                if any(h is None for h in hs) or (wh is not None and max(hs) < wh - 1.0): continue
            faces.append(q)
    me = bpy.data.meshes.new("terrain"); me.from_pydata(verts, [], faces); ob = bpy.data.objects.new("terrain", me); sc.collection.objects.link(ob)
    ob.visible_diffuse = False; ob.visible_glossy = False   # backdrop only, like the far prims (see build)
    m = bpy.data.materials.new("ground"); m.use_nodes = True; nt = m.node_tree; p = nt.nodes["Principled BSDF"]; L = nt.links.new
    comps = t.get("composition") if hd else None
    if not comps:
        p.inputs["Base Color"].default_value = (0.24, 0.30, 0.14, 1); me.materials.append(m); return
    for poly in me.polygons: poly.use_smooth = True
    lay = me.attributes.new("tlayer", "FLOAT", "POINT"); lay.data.foreach_set("value", terrain_layers(verts, comps))
    p.inputs["Roughness"].default_value = 0.9
    geo = nt.nodes.new("ShaderNodeNewGeometry")
    sc_ = nt.nodes.new("ShaderNodeVectorMath"); sc_.operation = "SCALE"; sc_.inputs["Scale"].default_value = 1 / 12.0   # one tile per 12 m (RenderTerrainScale)
    L(geo.outputs["Position"], sc_.inputs[0])
    cols = []
    for k, path in enumerate(comps[0].get("paths") or [None] * 4):
        if path and os.path.exists(path):
            im = nt.nodes.new("ShaderNodeTexImage"); im.image = bpy.data.images.load(path); L(sc_.outputs[0], im.inputs["Vector"]); cols.append(im.outputs["Color"])
        else:
            rgb = nt.nodes.new("ShaderNodeRGB"); rgb.outputs[0].default_value = (*TERRAIN_FALLBACK[k], 1); cols.append(rgb.outputs[0])
    at = nt.nodes.new("ShaderNodeAttribute"); at.attribute_name = "tlayer"
    nz = nt.nodes.new("ShaderNodeTexNoise"); nz.inputs["Scale"].default_value = 0.15; L(geo.outputs["Position"], nz.inputs["Vector"])
    nm = nt.nodes.new("ShaderNodeMath"); nm.operation = "MULTIPLY_ADD"; L(nz.outputs["Fac"], nm.inputs[0]); nm.inputs[1].default_value = 1.2; nm.inputs[2].default_value = -0.6
    lv = nt.nodes.new("ShaderNodeMath"); lv.operation = "ADD"; L(at.outputs["Fac"], lv.inputs[0]); L(nm.outputs[0], lv.inputs[1])
    c = cols[0]
    for k in (1, 2, 3):
        f = nt.nodes.new("ShaderNodeMath"); f.operation = "SUBTRACT"; f.use_clamp = True; L(lv.outputs[0], f.inputs[0]); f.inputs[1].default_value = k - 1
        mx = nt.nodes.new("ShaderNodeMix"); mx.data_type = "RGBA"; L(f.outputs[0], mx.inputs["Factor"]); L(c, mx.inputs["A"]); L(cols[k], mx.inputs["B"]); c = mx.outputs["Result"]
    L(c, p.inputs["Base Color"])
    if AMB > 0: L(c, p.inputs["Emission Color"]); p.inputs["Emission Strength"].default_value = AMB   # SL sky ambient, as for prims
    me.materials.append(m)
    log("terrain hd", len(faces), "faces", "regions", len(comps), "textures", sum(1 for x in comps[0].get("paths") or [] if x))

gpu_setup(); views = []

def prim_object(name, shape, r, depth, centre):
    """Cylinder/sphere mesh with its centre baked into the vertices (the caller sets ob.location to the avatar's feet).
    bmesh, not bpy.ops.mesh.primitive_*: each op re-evaluates the view layer, ~1 s per stand-in in a 55-avatar scene."""
    import bmesh
    bm = bmesh.new()
    if shape == "cylinder": bmesh.ops.create_cone(bm, cap_ends=True, segments=24, radius1=r, radius2=r, depth=depth)
    else: bmesh.ops.create_uvsphere(bm, u_segments=24, v_segments=12, radius=r)
    bmesh.ops.translate(bm, vec=Vector(centre), verts=bm.verts)
    me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free()
    ob = bpy.data.objects.new(name, me); sc.collection.objects.link(ob)
    return ob

STANDIN_MAT = []
def stand_in(o):
    """Neutral grey capsule + head for an avatar still loading (attachments not all arrived; David 2026-10-05: like a
    normal viewer's orange cloud, never a half-dressed or nude body). One shared clay material, no skin bake."""
    pref = o.get("bake_prefix") or (o.get("agent_id") or "")[:8]
    if not STANDIN_MAT:
        m = bpy.data.materials.new("standin"); m.use_nodes = True
        m.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.42, 0.42, 0.45, 1)
        m.node_tree.nodes["Principled BSDF"].inputs["Roughness"].default_value = 0.8
        STANDIN_MAT.append(m)
    bones = o.get("bones") or []
    z0, z1 = 0.05, 1.4
    if bones:
        zs = [b[2] for b in bones] + [b[5] for b in bones]
        z0, z1 = min(zs), max(zs) - 0.25
    body = prim_object(f"standin-body-{pref}", "cylinder", 0.18, max(0.4, z1 - z0), (0, 0, (z0 + z1) / 2))
    hx, hy, hz = o.get("head") or [0, 0, 1.7]
    head = prim_object(f"standin-head-{pref}", "sphere", 0.12, 0, (hx, hy, hz))
    for ob in (body, head): ob.data.materials.append(STANDIN_MAT[0])
    lo = np.array([-0.2, -0.2, z0]); hi = np.array([0.2, 0.2, hz + 0.12])
    log("stand-in", o.get("name"), o.get("dressed") or "no attachments")
    return lo, hi, [body, head]

def layer_plan():
    """base pass: which avatars the camera can see (their boxes projected with a margin) and how far from it; look.py batches them.
    Written to <jobdir>/layer-plan.json"""
    from bpy_extras.object_utils import world_to_camera_view
    cam = sc.camera; margin = float(os.environ.get("GT_LAYER_MARGIN", "0.2")); plan = []
    verts = {}
    for b in M["batches"]: verts[b["group"]] = verts.get(b["group"], 0) + b["nv"]
    for o, fp in placed:
        if not any(b["group"] == o["group"] and b["ni"] > 0 for b in M["batches"]) or o.get("placeholder"): continue
        fl, ll, st = fp; zs = [fl] + ([ll] if ll is not None else [])
        lo_z, hi_z = min(zs) - 0.6, max(zs) + 2.6
        cs = [Vector((o["pos"][0] + dx, o["pos"][1] + dy, z)) for dx in (-0.7, 0.7) for dy in (-0.7, 0.7) for z in (lo_z, hi_z)]
        pr = [world_to_camera_view(sc, cam, c) for c in cs]
        front = [p for p in pr if p.z > 0.05]
        vis = bool(front) and (len(front) < len(pr) or (min(p.x for p in front) <= 1 + margin and max(p.x for p in front) >= -margin
                                                         and min(p.y for p in front) <= 1 + margin and max(p.y for p in front) >= -margin))
        d = (Vector(o["pos"]) - cam.matrix_world.translation).length
        plan.append({"group": o["group"], "name": o.get("name"), "cam_dist": round(d, 2), "visible": vis, "verts": verts.get(o["group"], 0)})
    json.dump(plan, open(f"{jobdir}/layer-plan.json", "w"))
    log("layer plan", len(plan), "avatars,", sum(1 for p in plan if p["visible"]), "in view")

if kind == "scene":
    build("scene"); memlog("scene built")
    if LAYER != "batch": build("far")   # a batch pass only needs what can occlude / light its avatars: the backdrop is > 30 m out
    terrain()
    if LAYER != "batch": water(); particle_systems()
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
    seated_keep = os.environ.get("GT_SEATED") == "1"   # 2026-10-11: seated avatars: pelvis at agent position, no floor snap
    # 2026-10-13 Reiley Net Chair: seated avatars were snapped onto the floor cast (which hits the seat cushion) by their
    # lowest vertex, so who sits higher depended on where each pose puts its feet: Galatea on David's lap (agent z 0.11 m
    # above his) rendered 0.13 m BELOW him, i.e. him on her lap. Seated (mesh.json "seated", scene-mesher 2026-10-13+):
    # always the sim's placement, agent z + agent_off_seated, for every avatar alike
    def place(o, px, py, pz):  # -> (floor under it, LL mesh z0 or None, seated); cast before any avatar is built
        st = seated_keep or bool(o.get("seated"))
        off = o.get("agent_off_seated") if st and o.get("agent_off_seated") is not None else o.get("agent_off")
        return floor_at(px, py, pz), (pz + off if off is not None else None), st
    placed = [(o, place(o, *o["pos"])) for o in M.get("others", [])]
    fl, ll, mst = place(M, x, y, g[2]); ll = None if av else ll
    lo, hi, obs = build("avatar", M.get("bones")); z0 = z0_of(fl, ll, lo[2], mst)
    for ob in obs: ob.location = (x, y, z0); ob.rotation_euler = (0, 0, math.radians(yaw))
    log("avatar at", round(x, 2), round(y, 2), "mesh z0", round(z0, 2), "floor", round(fl, 2), "LL", ll and round(ll, 2))
    def to_world(local, at, yw):
        c, s_ = math.cos(math.radians(yw)), math.sin(math.radians(yw))
        return Vector((at[0] + c * local[0] - s_ * local[1], at[1] + s_ * local[0] + c * local[1], at[2] + local[2]))
    heads = {"me": to_world(M["head"], (x, y, z0), yaw) + Vector((0, 0, 0.07))}
    layer_real = []   # a batch pass: the objects of the avatars it draws (everything else becomes a holdout)
    def add_others():
        for o, fp in placed:
            # attachments missing from the export -> bake-textured stand-in (not a nude mesh body)
            if o.get("placeholder") or not any(b["group"] == o["group"] and b["ni"] > 0 for b in M["batches"]):
                # 2026-10-13: never a silent grey block. HD photos omit an avatar whose attachments didn't arrive; looks keep the stand-in
                if omit_stand_in(os.environ):
                    print(f"GT: avatar omitted {o.get('name', '?')} (attachments not loaded, dressed={o.get('dressed')})", flush=True); continue
                print(f"GT: avatar stand-in {o.get('name', '?')} (attachments not loaded, dressed={o.get('dressed')})", flush=True)
                olo, _, oobs = stand_in(o)
            elif LAYER and o["group"] not in LAYER_AVS:
                # layered photo: this avatar is drawn in another pass; here a clay stand-in only casts its shadow (invisible to camera rays)
                olo, _, oobs = stand_in(o)
                for ob in oobs: ob.visible_camera = False
            else:
                near = math.dist(o["pos"][:2], (x, y)) < float(os.environ.get("GT_LINING_M", "8"))
                olo, _, oobs = build(o["group"], o.get("bones"), lining=near)
                if LAYER: layer_real.extend(oobs)
            at = (o["pos"][0], o["pos"][1], z0_of(fp[0], fp[1], olo[2], fp[2]))
            for ob in oobs: ob.location = at; ob.rotation_euler = (0, 0, math.radians(o["yaw"]))
            heads[o["name"]] = to_world(o["head"], at, o["yaw"]) + Vector((0, 0, 0.07)); log("other avatar", o["name"], "at", [round(v, 2) for v in at])
    if not LAYER: add_others()   # layered: after the camera is placed (the wall / DOF ray casts must not see avatars, same in every pass)
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
        h = heads["me"]; DOF_SUBJECT[0] = dof_subject(view, heads)
        def shoulder(f, back, side, up, target, lens):
            """over her right shoulder; a wall or chimney behind her would fill the frame, so stop in front of the first
            surface between her head and the camera (as the SL camera does)"""
            want = h - f * back + f.cross(Vector((0, 0, 1))) * side + Vector((0, 0, up)); d = want - h; u = d.normalized()
            bpy.context.view_layer.update()
            hit, loc, *_ = sc.ray_cast(bpy.context.evaluated_depsgraph_get(), h + u * 0.35, u, distance=d.length - 0.35)
            if hd_fov():
                rx, ry = (int(v) for v in os.environ.get("GT_RES", "3840x2160").split("x")); lens = lens_for_vfov(hd_fov(), rx, ry)
            camera(loc - u * 0.15 if hit else want, target, lens, (960, 540))
        if view.startswith("portrait"):
            # `photo hd self` (2026-10-13): her whole body from in front, in the real scene; "portrait[:yaw offset]".
            # Framed on her world bounding box (standing, sitting or lying), lens 50; Blender's sensor fit = the long side
            a = math.radians(yaw + (float(view[9:]) if view.startswith("portrait:") else 0)); f = Vector((math.cos(a), math.sin(a), 0))
            rx, ry = (int(v) for v in os.environ.get("GT_RES", "2160x3840").split("x"))
            bpy.context.view_layer.update()
            pts = [ob.matrix_world @ Vector(c) for ob in obs for c in ob.bound_box]
            blo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts))); bhi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
            mid = (blo + bhi) / 2; ext = bhi - blo; side = abs(ext.x * f.y) + abs(ext.y * f.x)   # width across the view
            # photo hd (2026-10-14 David): Firestorm's 60 deg vertical view angle (GT_HD_FOV), subject framing kept by
            # moving the camera to keep her well framed, with Firestorm's wider surroundings around her
            lens = lens_for_vfov(hd_fov(), rx, ry) if hd_fov() else 50.0
            hf = 2 * math.atan(18 / lens * (rx / ry if ry >= rx else 1)); vf = 2 * math.atan(18 / lens * (1 if ry >= rx else ry / rx))
            # margin: half-extent / tan(half fov); 0.6 = body fills ~83% of the frame (old 50 mm close-up). At 60 deg
            # that would put the lens ~1.5 m from her (wide-angle distortion), so at 60 deg she fills ~70% of the frame, matched to David's Firestorm boat shot 2026-10-14
            m = 0.45 if hd_fov() else 0.6
            dist = max(ext.z * m / math.tan(vf / 2), side * m / math.tan(hf / 2), 0.4 if hd_fov() else 1.2) + max(ext.x, ext.y) / 2
            want = mid + f * dist + Vector((0, 0, 0.15 * dist)); e = want - h; u = e.normalized()
            # a wall in front of her: stop just before it (cast from her head: a seat or table isn't a wall)
            hit, loc, *_ = sc.ray_cast(bpy.context.evaluated_depsgraph_get(), h + u * 0.5, u, distance=max(0.01, e.length - 0.5))
            camera(loc - u * 0.15 if hit else want, mid, lens, (rx, ry))
        elif view.startswith("at:"):
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
    if LAYER:
        # same sun tint + DOF focus in every pass, both measured before any avatar exists in the scene
        if sc.camera and sc.camera.get("focus_target") is not None:
            _d = Vector(sc.camera["focus_target"]) - sc.camera.matrix_world.translation; retint_sun((_d.normalized().z if _d.length > 1e-6 else 0.0) - 0.25)
        memlog("before others"); setup_dof(); add_others(); memlog("others added")
        if LAYER == "base":
            layer_plan()
            # same light-tree memory cost as the batch passes (5.6 GB at the 3 M-triangle world): emissive faces farther than
            # GT_BASE_MIS_RADIUS metres from the camera (default 25) are not importance-sampled; nearer ones, and any material
            # shared with a near object, keep full sampling so her own lighting stays as clean as before
            R = float(os.environ.get("GT_BASE_MIS_RADIUS", "25")); cam = sc.camera.matrix_world.translation if sc.camera else None
            if cam is not None and R > 0:
                near_m = set(); far_m = set()
                for ob in sc.objects:
                    if ob.type != "MESH": continue
                    bb = np.array([tuple(c) for c in ob.bound_box], np.float64); c_ = (bb.min(0) + bb.max(0)) / 2
                    (near_m if float(np.linalg.norm(c_ - np.array(cam))) - float(np.linalg.norm(bb.max(0) - bb.min(0))) / 2 < R else far_m).update(ob.data.materials)
                n_ = 0
                for m_ in far_m - near_m:
                    if hasattr(m_.cycles, "emission_sampling"): m_.cycles.emission_sampling = "NONE"; n_ += 1
                log("layer base: emission sampling off on", n_, "far materials of", len(far_m | near_m))
        else:
            sc.render.film_transparent = True
            keep = set(layer_real)
            # Cycles "Updating Lights" builds a light-tree entry per emissive triangle (fullbright faces): +3.5 GB of the ~5 GB peak with
            # the 3 M-triangle world. A holdout's emission still lights the avatars (BSDF sampling), it just isn't importance-sampled.
            if os.environ.get("GT_HOLD_EMIT_MIS", "0") != "1":
                real_mats = {m for ob in keep for m in ob.data.materials}
                for m_ in bpy.data.materials:
                    if m_ not in real_mats and hasattr(m_.cycles, "emission_sampling"): m_.cycles.emission_sampling = "NONE"
            for ob in sc.objects:
                if ob.type == "MESH" and ob not in keep and ob.visible_get() and getattr(ob, "visible_camera", True): ob.is_holdout = True
            log("layer batch", sorted(LAYER_AVS), "holdout world")
    if os.environ.get("GT_DEBUG_PROJ"):   # diagnosis: where the SL lights and glowing / fullbright faces land in the frame (pixels, y down)
        bpy.context.view_layer.update()
        from bpy_extras.object_utils import world_to_camera_view
        W_, H_ = sc.render.resolution_x, sc.render.resolution_y; out = {"lights": [], "glow": []}
        def px(p):
            v = world_to_camera_view(sc, sc.camera, Vector(p)); return [round(v.x * W_), round((1 - v.y) * H_), round(v.z, 1)]
        for o_ in sc.objects:
            if o_.type == "LIGHT": out["lights"].append({"type": o_.data.type, "px": px(o_.location), "color": [round(c, 2) for c in o_.data.color], "spot": round(math.degrees(o_.data.spot_size), 1) if o_.data.type == "SPOT" else None})
        for b_ in M["batches"]:
            if b_["group"] in ("scene", "far") and ((b_.get("glow") or 0) > 0 or b_.get("fullbright")) and b_["nv"]:
                P_ = arrays(b_)[0].reshape(-1, 3); c_ = (P_.min(0) + P_.max(0)) / 2
                out["glow"].append({"px": px(c_), "nv": b_["nv"], "glow": b_.get("glow"), "fb": bool(b_.get("fullbright")), "ext": [round(float(e), 2) for e in P_.max(0) - P_.min(0)], "rgba": [round(c, 2) for c in b_["rgba"]], "tex": b_["tex"][:8]})
        json.dump(out, open(os.environ["GT_DEBUG_PROJ"], "w"))
else:
    lo, hi, _ = build("avatar", M.get("bones"))
    # face: the mHead joint (base of the skull) + ~7 cm up to the eyes; older mesh.json: from the avatar's top
    head = np.array(M["head"]) + (0, 0, 0.07) if "head" in M else np.array([0.0, 0.0, hi[2] - 0.13])
    if kind == "face":
        camera(head + (0.85, 0.0, 0.0), head + (0, 0, -0.01), 85, (768, 768))
        area(head + (0.6, 0.5, 0.25), head, 12, 0.6); area(head + (0.6, -0.6, 0.0), head, 5, 0.8); area(head + (-0.5, 0.0, 0.4), head, 8, 0.5)
    else:
        mid = (lo + hi) / 2; h = hi[2] - lo[2]
        # camera + studio lights on one rig around her vertical axis: GT_BODY_VIEWS="front;back;left;right" (or degrees)
        # renders each side with the same lighting relative to the camera (alpha-layer checks need the back and sides)
        rig = bpy.data.objects.new("body-rig", None); sc.collection.objects.link(rig); rig.location = Vector(mid)
        camera(mid + (h * 1.9, 0.0, 0.0), mid, 50, (640, 1024)); rigged = [sc.camera]
        for loc, e_, s_ in (((2.0, 1.5, 1.0), 120, 2.0), ((2.0, -2.0, 0.0), 50, 2.5), ((-1.5, 0.0, 1.5), 60, 1.5)):
            area(mid + loc, mid, e_, s_); rigged.append(bpy.context.object)
        for ob in rigged: ob.parent = rig; ob.location = ob.location - Vector(mid)
        views = [("body", body_view_deg(v), body_view_label(v)) for v in os.environ.get("GT_BODY_VIEWS", "").split(";") if v.strip()]
        # close-up (alpha cut-out / attachment checks): GT_BODY_FOCUS="<m below her top>[,dist m[,lens]]", camera in front
        fo = os.environ.get("GT_BODY_FOCUS", "")
        if fo:
            fp = [float(x) for x in fo.split(",")]; dist = fp[1] if len(fp) > 1 else 0.6; lens = fp[2] if len(fp) > 2 else 70
            rig.location = Vector((mid[0], mid[1], hi[2] - fp[0])); sc.camera.location = Vector((dist, 0, 0)); sc.camera.data.lens = lens
            sc.render.resolution_x = sc.render.resolution_y = 768
            log("focus", [round(v, 2) for v in rig.location], "dist", dist)
    world(0.5)
sc.view_settings.view_transform = "Standard"  # SL shows textures as plain sRGB
sc.view_settings.exposure = view_exposure(os.environ, HD, kind)
if HD and kind == "scene" and M.get("env"): env_post(M["env"])
if out.lower().endswith(".png"):   # `photo hd`: lossless master (look.py writes the high-quality JPG from it)
    sc.render.image_settings.file_format = "PNG"; sc.render.image_settings.color_mode = "RGB"; sc.render.image_settings.compression = 15
else:
    sc.render.image_settings.file_format = "JPEG"; sc.render.image_settings.quality = 90
log("build seconds", round(time.time() - T0, 1))

BAND_OVERLAP = 48
def band_rows(h, n):
    """[(y0, y1)] row ranges of n near-equal horizontal bands covering 0..h"""
    n = max(1, min(int(n), h)); e = [round(h * i / n) for i in range(n + 1)]
    return [(e[i], e[i + 1]) for i in range(n)]

def hd_render_hdr(png):
    """photo hd (2026-10-13, David: HDR): render once scene-linear to a 32-bit OpenEXR master (+ Radiance .hdr), then the
    PNG = Firestorm's tonemap/exposure/sRGB pass on it (hdr_post.firestorm_ldr)"""
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__))); import hdr_post
    exr = png[:-4] + ".exr"; im = sc.render.image_settings
    im.file_format = "OPEN_EXR"; im.color_mode = "RGBA" if LAYER == "batch" else "RGB"; im.color_depth = "32"; im.exr_codec = "ZIP"
    sc.render.filepath = exr
    gexr = None
    if GLOW_ON:
        vl = sc.view_layers[0]
        if "glow" not in [x.name for x in vl.aovs]: av = vl.aovs.add(); av.name = "glow"; av.type = "COLOR"
        sc.use_nodes = True; cnt = sc.node_tree
        rl = cnt.nodes.get("Render Layers") or cnt.nodes.new("CompositorNodeRLayers")
        if not any(n.bl_idname == "CompositorNodeComposite" for n in cnt.nodes):
            cp = cnt.nodes.new("CompositorNodeComposite"); cnt.links.new(rl.outputs["Image"], cp.inputs["Image"])
        fo = cnt.nodes.new("CompositorNodeOutputFile"); gdir = png[:-4] + "-glow"; fo.base_path = gdir
        fo.format.file_format = "OPEN_EXR"; fo.format.color_depth = "32"; fo.file_slots[0].path = "glow"
        cnt.links.new(rl.outputs["glow"], fo.inputs[0]); gexr = gdir
    # 2026-10-14 memory: the post runs in float32 (8-bit PNG / RGBE out; float64 copies of a 4K frame were ~1 GB of the
    # peak) and each EXR's Blender image is freed as soon as its pixels are read. Cycles also merges the 4K tiles and
    # runs OIDN on the WHOLE frame at the end (+3.4 GB at 3840x2160, measured), whatever the tile size. GT_HD_BANDS=n
    # renders n horizontal border bands (each denoised alone, BAND_OVERLAP rows of margin each side so OIDN's edges fall
    # outside the kept rows) and stitches them: that end-of-render spike drops to ~1/n. look.py picks n from free memory.
    bands = max(1, int(os.environ.get("GT_HD_BANDS", "1") or 1))
    W_, H_ = sc.render.resolution_x, sc.render.resolution_y
    rgb = glow = mist = None; t = time.time()
    for y0, y1 in band_rows(H_, bands):
        if bands > 1:
            lo, hi = max(0, y0 - BAND_OVERLAP), min(H_, y1 + BAND_OVERLAP)
            sc.render.use_border = True; sc.render.use_crop_to_border = False
            sc.render.border_min_x, sc.render.border_max_x = 0.0, 1.0
            sc.render.border_min_y, sc.render.border_max_y = lo / H_, hi / H_
        memlog("render start"); bpy.ops.render.render(write_still=True); memlog("render done")
        img = bpy.data.images.load(exr); w, h = img.size; px = np.empty(w * h * 4, np.float32); img.pixels.foreach_get(px)
        bpy.data.images.remove(img)
        if rgb is None: rgb = np.zeros((h, w, 4 if LAYER else 3), np.float32)
        rgb[y0:y1] = px.reshape(h, w, 4)[y0:y1, :, :(4 if LAYER else 3)]; del px     # Blender rows bottom-up, like border_min_y
        if gexr:
            import glob, shutil
            gf = sorted(glob.glob(gexr + "/glow*.exr"))
            if gf:
                gi = bpy.data.images.load(gf[0]); gp = np.empty(w * h * 4, np.float32); gi.pixels.foreach_get(gp)
                bpy.data.images.remove(gi)
                if glow is None: glow = np.zeros((h, w, 3), np.float32)
                glow[y0:y1] = gp.reshape(h, w, 4)[y0:y1, :, :3]; del gp
            shutil.rmtree(gexr, ignore_errors=True)
        if LAYER == "batch" and HAZE:
            import glob
            mf = sorted(glob.glob(os.environ["GT_LAYER_NPY"][:-4] + "-mist/mist*.exr"))
            if mf:
                mi = bpy.data.images.load(mf[0]); mp = np.empty(w * h * 4, np.float32); mi.pixels.foreach_get(mp); bpy.data.images.remove(mi)
                if mist is None: mist = np.zeros((h, w), np.float32)
                mist[y0:y1] = mp.reshape(h, w, 4)[y0:y1, :, 0]; del mp
    secs = round(time.time() - t, 1)
    if LAYER:   # layered photo: scene-linear RGBA (premultiplied; base alpha 1) + glow for look.py to composite, tonemap there
        npy = os.environ["GT_LAYER_NPY"]; rgba = rgb.reshape(h, w, 4)
        if LAYER == "base": rgba[..., 3] = 1.0
        elif HAZE and mist is not None:
            if os.environ.get("GT_LAYER_DEBUG_RAW"): np.save(npy[:-4] + "-pre.npy", rgba); np.save(npy[:-4] + "-mist.npy", mist)   # diagnosis
            sel = rgba[..., 3] >= 0.9; k_, D_, col_ = HAZE; m_ = float(np.median(mist[sel])) if sel.any() else 1.0   # == layers.layer_fog
            fog_ = 0.0 if m_ >= 0.9999 else float(1.0 - np.exp(-k_ * D_ * m_))
            json.dump({"fog": fog_, "col": [float(c) for c in col_]}, open(npy[:-4] + "-haze.json", "w")); log("layer haze fog", round(fog_, 4), "solid mist", round(m_, 4))
        import shutil; shutil.rmtree(npy[:-4] + "-mist", ignore_errors=True)
        np.save(npy, rgba)
        if glow is not None: np.save(npy[:-4] + "-glow.npy", glow)
        log("layer raw", LAYER, npy, "alpha mean", round(float(rgba[..., 3].mean()), 4), "render seconds", secs)
        log("render seconds", secs, png); return
    if bands > 1:   # the stitched scene-linear master replaces the last band's EXR
        sc.render.use_border = False; log("hd bands", bands)
        ex = bpy.data.images.new("exr-master", W_, H_, alpha=True, float_buffer=True); ex.colorspace_settings.name = "Linear Rec.709"
        o = np.ones((H_, W_, 4), np.float32); o[..., :3] = rgb; ex.pixels.foreach_set(o.ravel()); del o
        ex.save_render(exr, scene=sc); bpy.data.images.remove(ex)
    if glow is not None:
        st = hdr_post.settings_from_env(os.environ)
        rgb += hdr_post.bloom(glow, st["glow_iterations"], st["glow_strength"]).astype(np.float32); del glow
        log("glow bloom", "iterations", st["glow_iterations"], "strength", st["glow_strength"])
    ldr, info = hdr_post.firestorm_ldr(rgb, M.get("env"), 2.0 ** float(os.environ.get("GT_EXPOSURE", "0")), hdr_post.settings_from_env(os.environ))
    hdr_post.write_rgbe(png[:-4] + ".hdr", rgb[::-1])   # Blender pixels are bottom row first
    out = bpy.data.images.new("ldr", w, h, alpha=False, float_buffer=True); out.colorspace_settings.name = "Non-Color"
    del rgb
    o = np.ones((h, w, 4), np.float32); o[..., :3] = ldr; del ldr; out.pixels.foreach_set(o.ravel()); del o
    out.filepath_raw = png; out.file_format = "PNG"; out.save()
    log("hdr master", exr, png[:-4] + ".hdr", "post", json.dumps(info))
    log("render seconds", secs, png)
views = views or [None]
# keep the synced scene (BVH, ~1 GB of images with the backdrop) between the views of one job: each view re-synced it,
# 2-3 s a view with the backdrop's textures (David 2026-10-04: keep `look around` near its pre-backdrop time)
# persistent data saves ~1.5 s a view but segfaulted embree (rtcSetSharedGeometryBuffer) on the 2nd/3rd view in every
# multi-view crowd run on 2026-10-05, costing a ~40 s retry; off unless GT_PERSIST=1.
sc.render.use_persistent_data = len(views) > 1 and os.environ.get("GT_PERSIST", "0") == "1"
if os.environ.get("GT_DEBUG_PICK"):   # diagnosis: "u,v[;u,v]" (0..1 from top-left): what shadows that pixel from each sun
    from mathutils import Vector
    from bpy_extras.object_utils import world_to_camera_view  # noqa: F401
    dg = bpy.context.evaluated_depsgraph_get(); camo = sc.camera
    for uv in os.environ["GT_DEBUG_PICK"].split(";"):
        u, v = (float(x) for x in uv.split(","))
        fr = [camo.matrix_world @ c for c in camo.data.view_frame(scene=sc)]   # tr, br, bl, tl
        tl, tr, bl = fr[3], fr[0], fr[2]; px = tl + (tr - tl) * u + (bl - tl) * v
        d = (px - camo.matrix_world.translation).normalized()
        hit, loc, nrm, _, ob, _ = sc.ray_cast(dg, camo.matrix_world.translation, d)
        log("pick", uv, "hit", ob.name if hit else None, [round(x, 2) for x in loc] if hit else None)
        if not hit: continue
        for lo in [o for o in sc.objects if o.type == "LIGHT" and o.data.type == "SUN"]:
            ld = lo.matrix_world.to_quaternion() @ Vector((0, 0, 1))
            h2, l2, _, _, o2, _ = sc.ray_cast(dg, loc + nrm * 0.01, ld)
            log("pick", uv, "sun", lo.name, "blocked by", (o2.name, [round(x, 2) for x in l2], o2.get("sl_tex"), o2.data.materials[0].name if o2.data.materials else None) if h2 else None)
for i, v in enumerate(views):
    if isinstance(v, tuple) and v[0] == "body":  # body side views: rotate the camera/light rig, name the file by side
        rig.rotation_euler = (0, 0, math.radians(v[1]))
        sc.render.filepath = re.sub(r"\.(jpg|png)$", rf"-{v[2]}.\1", out)
    else:
        if i: set_view(v)
        sc.render.filepath = out if len(views) == 1 else re.sub(r"\.(jpg|png)$", rf"-{i}.\1", out)
    if sc.camera and sc.camera.get("focus_target") is not None and not LAYER:
        _d = Vector(sc.camera["focus_target"]) - sc.camera.matrix_world.translation; retint_sun((_d.normalized().z if _d.length > 1e-6 else 0.0) - 0.25)   # the centre of the frame's lower half (the deck / ground she is on), not the subject itself
    if not LAYER: setup_dof()
    if HD and sc.render.filepath.lower().endswith(".png"):
        hd_render_hdr(sc.render.filepath); continue
    t = time.time(); bpy.ops.render.render(write_still=True); log("render seconds", round(time.time() - t, 1), sc.render.filepath)
