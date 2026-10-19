"""Layered `photo hd` for crowds (2026-10-18). One Blender process cannot hold a ~3 M vertex club plus 90 avatars in the box's ~7.5 GB,
so the photo is rendered in passes, each in its own Blender process (memory freed between them), then composited:

  base   world + her, the other avatars only as clay stand-ins that cast shadows (invisible to the camera); opaque, sky and all.
         It also measures which avatars the camera can see (render_mesh.layer_plan -> layer-plan.json).
  batch  a few visible avatars at a time, with the world and her as HOLDOUTS on a transparent film: same camera, resolution, sun,
         sky lighting and DOF focus as the base pass (all fixed before any avatar is added), shadows of the world / her / the
         other avatars (stand-ins) still fall on them, and the world still hides whatever is behind it.
  composite  batches back to front (farthest from the camera first) alpha-over the base in scene-linear light, then glow bloom,
         Firestorm tonemap and sharpening ONCE on the finished frame.

Chosen over the alternatives:
  * front-to-back with holdouts of the nearer avatars: every later pass must load the earlier avatars again (as holdouts), so memory
    grows with each pass and the point is lost.
  * depth (Z) merge of full renders: exact for opaque surfaces but needs the world in every pass as real, lit geometry and
    anti-aliased / blended / alpha-cut edges (hair, lashes, DOF) get a depth that belongs to no surface.
  * back-to-front alpha-over of holdout passes (this): edges and DOF blend correctly through the alpha channel; the world and her
    occlude exactly (holdouts); occlusion BETWEEN batches is by batch depth order, so two avatars from different batches that
    interleave in depth (an arm reaching across) show the farther batch's whole avatar behind the nearer one's. Avatars are
    grouped by distance from the camera so a batch spans a contiguous depth slice, which keeps that rare.
Also: avatars outside the camera's view (+ a margin) are not rendered at all; their stand-ins still cast shadows into the frame.
"""
import json, os
import numpy as np

BYTES_PER_VERTEX = 1000   # Blender + Cycles RAM per mesh vertex: ~4.4 M vertices peaked ~6.6 GB over a ~2.4 GB base (2026-10-18)
BASE_MB = 3000            # film, tiles, denoise, textures: the part that does not scale with the mesh
MIN_BATCH_VERTS = 250_000

def world_verts(batches):
    """vertices of everything every pass carries: the scene, the backdrop and her own avatar group"""
    return sum(b["nv"] for b in batches if b["group"] in ("scene", "far", "avatar"))

def others_verts(batches):
    return sum(b["nv"] for b in batches if b["group"].startswith("avatar:"))

def mb_needed(verts, bpv=BYTES_PER_VERTEX, base_mb=BASE_MB):
    return base_mb + verts * bpv / 2 ** 20

def layers_mode(env=None):
    """GT_HD_LAYERS: on / off / auto (default)"""
    v = ((os.environ if env is None else env).get("GT_HD_LAYERS", "auto") or "auto").strip().lower()
    return v if v in ("on", "off", "auto") else "auto"

def needs_layers(batches, avail_mb, floor_mb=1500, mode="auto"):
    """True when one process would not fit: world + every other avatar's vertices vs what the box has free now"""
    if mode == "off": return False
    if mode == "on": return True
    if avail_mb is None: return False
    return mb_needed(world_verts(batches) + others_verts(batches)) > avail_mb - floor_mb

def batch_budget_verts(batches, avail_mb, floor_mb=1500, bpv=BYTES_PER_VERTEX, base_mb=BASE_MB):
    """vertices of avatars one batch pass may add on top of the world (never under MIN_BATCH_VERTS)"""
    if avail_mb is None: return 1_500_000
    room_mb = avail_mb - floor_mb - base_mb - world_verts(batches) * bpv / 2 ** 20
    return int(max(MIN_BATCH_VERTS, room_mb * 2 ** 20 / bpv))

def plan_batches(plan, budget_verts, max_per_batch=6):
    """plan: [{group, cam_dist, visible, verts}] -> [[group, ...], ...] ordered farthest batch first (paint order). Only visible
    avatars. Walks the avatars far -> near and starts a new batch when the vertex budget or max_per_batch is reached, so each batch is
    a contiguous slice of depth; an avatar over the budget on its own still gets a batch."""
    vis = sorted((p for p in plan if p.get("visible")), key=lambda p: -p["cam_dist"])
    batches, cur, used = [], [], 0
    for p in vis:
        v = int(p.get("verts") or 0)
        if cur and (used + v > budget_verts or len(cur) >= max_per_batch):
            batches.append(cur); cur, used = [], 0
        cur.append(p["group"]); used += v
    if cur: batches.append(cur)
    return batches

def split_batch(groups):
    """a batch that was memory-killed: two halves (same depth order), the farther half first"""
    h = (len(groups) + 1) // 2
    return [groups[:h], groups[h:]] if len(groups) > 1 else [groups]

def alpha_over(rgb, layer, fog=0.0, fog_col=None):
    """rgb (h, w, 3) float32 scene-linear, modified in place; layer (h, w, 4) premultiplied RGBA from a holdout pass.
    A batch layer is avatar-only: transparent film, no sky / world background and no haze in any pixel (edge pixels included).
    The distance haze in front of the layer's avatars is added here, once per layer, as a scalar `fog` toward `fog_col` and only
    where the layer has coverage (x alpha): an anti-aliased or depth-of-field-blurred edge pixel therefore carries the avatar's own colour
    at its alpha, never a background tint."""
    a = layer[..., 3:4]
    rgb *= (1.0 - a)
    if fog and fog_col is not None:
        rgb += layer[..., :3] * np.float32(1.0 - fog) + np.asarray(fog_col, np.float32) * (a * np.float32(fog))
    else:
        rgb += layer[..., :3]
    return rgb

def composite(base_rgba, layers):
    """layers: iterable of (h, w, 4) arrays, or loaders returning one, or (array, fog, fog_col) -- farthest first -> (h, w, 3) float32"""
    rgb = np.array(base_rgba[..., :3], np.float32)
    for L in layers:
        L = L() if callable(L) else L
        if isinstance(L, tuple): alpha_over(rgb, L[0], L[1], L[2])
        else: alpha_over(rgb, L)
        del L
    return rgb

def haze_path(npy):
    return npy[:-4] + "-haze.json"

def read_haze(npy):
    """(fog, colour) a batch pass left next to its layer, or (0.0, None)"""
    try:
        with open(haze_path(npy)) as f: d = json.load(f)
        return float(d["fog"]), d["col"]
    except (OSError, ValueError, KeyError): return 0.0, None

def layer_fog(alpha, mist, k, D, solid=0.9):
    """One fog fraction for a whole batch layer: 1 - exp(-k D m) at the median mist m (0..1 of D metres) of its SOLID pixels (alpha >= solid).
    Edge pixels' mist is a blend with whatever was behind (sky, holdout) and is never used. A batch is a contiguous slice of depth, so one
    value is within a fraction of a percent for every avatar in it."""
    sel = alpha >= solid
    if not sel.any(): return 0.0
    m = float(np.median(mist[sel]))
    return 0.0 if m >= 0.9999 else float(1.0 - np.exp(-k * D * m))

def read_plan(path):
    with open(path) as f: return json.load(f)
