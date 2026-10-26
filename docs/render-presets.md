# Render presets: which one for which check

Galatay's own renders (`look self`, the hover check) go through `vision/look.py`; the options and presets live in
`vision/look_scope.py`. Pick the **fastest preset that still shows what the check needs**. Defaults (no option given) are
the old render, unchanged: `look self` = the whole near scene on the real floor, quick exposure −1.5 stops.

How to run one:

- MCP tool `look` (`target=self`, `views=side`, `preset=hover-seat`, any option as an argument), or
- `client_cmd` with `look self side preset=hover-seat`, or `look self left scope=none floor=on small`.
- Output: the saved JPEG paths (`/workspace/secondlife/vision/look/look-<stamp>-self-body-<view>.jpg`). Open them with the image viewer.
- `seat_info` (MCP) gives the numbers for the same seat (seat object, my offset from it) before and after an adjust.

## Which preset for which check

| Check | Preset / settings | Why | Measured* |
|---|---|---|---|
| **Hover on the ground** (feet on the floor?) | `preset=hover-ground views=side` (or `left`) | Her + the floor under her, flat colours, 8 samples. The floor is what the check measures; the rest of the room does not matter. Her own worn items stay on: her lowest vertex is what touches the floor. | 3.5–3.9 s vs 6.5–7.1 s full |
| **Seated height on furniture** (sinking into the chair / floating?) | `preset=hover-seat views=side` | Her + only the object she sits on (its whole linkset), no floor, flat, 8 samples. The seat surface is what she must sit on. Add `floor=on` only when you also want the floor line in the picture. Then adjust with the furniture's own HUD (Adjust, Z+/Z−, step button .05 / .25 / .01, Save) and compare with `seat_info` (`sit_offset.z`). | 3.0–3.6 s |
| **Seated, need to see the room too** (is she clipping the table / arm of the sofa?) | `scope=nearby` (+ `views=side,front`) | Everything within 6 m, nothing farther; textures stay on. | ~4.7 s |
| **Outfit / what she looks like** | `preset=outfit` (front + back; add `left` with `views=front,back,left`) | Her alone with textures and materials on, no floor or furniture, so colours and fit are readable. | 3.7–4.2 s for two views |
| **Quick look at her in the room** | `preset=photo-fast` (= `look self fast`, today's quick look) | Whole near scene, floor, quick exposure −1.5 stops. Use when the surroundings matter. | 6.0–6.1 s |
| **Photo for David** | `preset=photo-hd` (= `photo hd self`) | 4K, Cycles + denoise, full textures, soft shadows, the shared environment. Minutes; returns when it starts and logs the paths as `[look]`. Never use it for a check. | minutes |
| **Geometry only** (shape / contact, no looks) | `scope=none floor=off textures=off materials=off small` | Smallest and fastest: half-size frame, 4 samples, plain colours. | 2.5–2.6 s |

\* Offline on the box against a saved 8 m export (83 scene prims, warm texture cache, other renders running on the same CPUs; load ≈ 4.7), wall-clock of
`look.py` including meshing, bakes and Blender. A busy room (hundreds of prims, cold textures) saves more than this simple one, because the
minimal presets skip meshing and fetching everything but her. The automatic hover check: 5.9–6.2 s → 2.4–2.6 s.

## Automatic hover / sit checks

After a sit, a stand or a new region the client's own check (`PoseHover.cs` → `look.py <export> hovercheck`) now uses the minimal preset by
itself: **`hover-seat` when she is seated on an object, `hover-ground` otherwise** (chosen from the export's `me.sitting_on`). The measured gap is geometry-only,
so the flat/few-samples render gives the same number as the full scene (checked: +0.052 m both ways). Set `GT_HC_FULL=1` in the client's
environment to get the old full-scene check back.

The floor a check measures against is every scene prim a ray straight down from her could hit (the chair, the rug, the slab). The kept set is a superset of that,
so removing everything else cannot change the floor height.

## Hover check: what the render draws, and how to calibrate it (2026-10-26)

**The hover in force.** The render draws her at the hover that the AgentPreferences capability holds (a fresh GET; falls back to the value the cap
answered to our last POST). That is what David's viewer applies. Our own `AvatarAppearance` value (`hover get`: "own appearance") stayed 0 after a
POST (-0.186 in the cap, 0 in our appearance, her sunk in his viewer), so until now every re-render drew hover 0 and "contact confirmed" after a fix
was arithmetic. `hover get` now prints `hover in force X (source)`. Every look's `hover check gap` line shows the hover it drew.

**Verification after a fix.** The check POSTs, GETs the preferences again (must equal the target), then re-renders at the value actually in force.
If the cap does not hold the target the log says `NOT verified: the hover in force is ...`. A gap of 0 from a bias that was never set from David's
viewer is reported as `contact in the render; NOT verified against David's viewer (default bias)`, never as "confirmed".

**Floor bias (calibration).** The placement `agent z - size/2 + hover - bias` is a model; the bias is its error per posture (default stand +0.2315, sit
-0.055, per pose possible). The error is not derivable: on 2026-10-26 the sim's agent z stood 0.975 m above the floor under her (body size 1.566 -> 0.783 m),
the viewer's own formula (scene-mesher `agent_off`) puts the mesh 0.107 m lower than `size/2`, and ground-sit animations move the hips by the pose.
The render prints both: `hover check detail: bias ..., viewer-formula gap ...` (diagnostic only). So the bias is **set from what David sees**:

| Command (also via `client_cmd`) | Effect |
|---|---|
| `hover calibrate stand <cm>` / `sit <cm>` / `here <cm>` | `<cm>` = what he sees in his viewer **now**, at the hover in force: `+4` she floats 4 cm, `-6` she is sunk 6 cm, `0` touching. Renders her now, sets the bias so the render's gap equals his, re-renders to confirm. `stand`/`sit` = the posture's bias; `here` = this exact pose (a VAMOSITG sit). Refused while moving, leashed, on an object, in an unclassified AO state, or if the posture does not match. |
| `hover calibrate show` / `reset stand|sit|here` | List the biases / remove one (back to the default). |
| `hover apply <m>` | Apply a hover now (+-0.5 m), **not saved** (no hover.txt). |
| `hover repin` | Back to the normal pinned hover. |

Recipe: `hover apply -0.05`, `look self side` (the picture shows -0.05), ask David "feet floating or sunk, how many cm?", `hover calibrate stand <cm>`.
After that the automatic check's `contact confirmed` means "contact in the render **and** the bias was set from his view". `hover set` is not in the
`client_cmd` list: it pins the value in hover.txt for every later login.

## Every option (`name=value` after `look self`, or an argument of the `look` tool)

| Option | Values | Effect |
|---|---|---|
| `preset` | `hover-ground`, `hover-seat`, `photo-fast`, `outfit`, `photo-hd` | A bundle of the options below; anything you add overrides it. `photo-hd` runs `photo hd self`. |
| `scope` | `full` (default), `nearby`, `seat`, `none` (aliases `self-only`, `seat-only`) | What else is drawn. `nearby` = linksets within 6 m; `seat` = the object she sits on; `none` = only her. |
| `floor` | `auto` (default = on), `on`, `off` | Terrain / floor prims under her and the red floor-height line. `off` removes all three. For `scope=seat/none`, `on` also keeps the prims a ray down from her would hit. |
| `avatars` | `auto`, `off` | `off` drops other avatars and their worn items (a self look never draws them in the picture anyway; it saves meshing). |
| `hide-attach` | attachment points: names (`skull`, `chin`, `left-ear`, `pelvis`, …) or numbers | Skips her worn items on those points, e.g. `skull` to leave out hair. Not offered for "all": her body is a worn mesh, with none she would not be drawn. |
| `textures` | `on`, `off` | `off` = no scene / attachment textures (plain face colours). Her skin bakes still load. |
| `materials` | `on`, `off` | `off` = diffuse only, no normal / specular maps. |
| `small` | flag | Half-size frame, 4 samples, 256 px bakes. |
| `scale` | 0.1–2 | Frame scale (wins over `small`). |
| `size` | `WxH` (64–4096) | Exact frame size (wins over scale). |
| `samples` | 1–256 | Cycles samples (quick looks use 8). |
| `jpeg` | 1–100 | JPEG quality (default 90). |
| `exposure` | −6…6 stops | View exposure. Default for quick looks: −1.5. |
| `cam-margin` | 0.5–4 | Camera distance factor (default 1.08; bigger = farther). |
| `cam-lens` / `cam-fov` | 14–200 mm / 5–120 deg | Lens or field of view across the long side (default 50 mm). `cam-lens` wins. |
| `cam-height` | −1…3 m | Camera height above the floor (default: half her height). |
| `views` / `views=` | `front,back,left,right,side` (= left), `all`, degrees | Which sides (self only). Use `side`/`left` to judge height; `front`/`back` for fit. |
| `fast` / `far` | flags | Existing: quick render / add the backdrop to 96 m. |

Not exposed (the renderer has no switch for them): lighting and shadows (one fixed neutral sun, no per-light controls),
the anti-aliasing filter, the sky, depth of field (photo hd only). Options apply to `look self` and the hover check only; `view`, `around`, `at`
and `photo hd` keep their own settings and refuse these.

The side view always draws the floor unless you say `floor=off` (David's rule): `look self side` is the level, low camera with the red line at floor height.
