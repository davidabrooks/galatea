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

## Contact close-up (`feet` view, `zoom=feet`) and the hover check's render count (2026-10-26)

David: zooming in close on the contact makes the hover easy to judge. `views=feet` (alias `contact`), or `zoom=feet` on a side view, draws a tight side view
(about 0.6 m wide, camera level and low, the frame starting 6 cm under the floor) of **her lowest body contact for the pose she is in**, not always the feet:

| Pose | What the close-up frames |
|---|---|
| Standing | the feet (soles and the floor) |
| Ground sit (the AO's, `GT_HC_KIND=sit`) | **the hips-to-floor contact around her pelvis** (see below): the butt, the lower back and the backs of the thighs, about 0.6-0.9 m wide; heels, hands and the head are not in it |
| Kneel | the shins and knees (and toes) on the floor |
| **Sitting on furniture** (an object: chair, bench, AVsitter seat) | **the hips against the seat**, never the feet (see below) |

How: the lowest 0.2 % of her vertices are the "lowest point"; every vertex within 10 cm above it is the contact, and the frame is that area's extent plus 0.3 m,
at least 0.6 m. The render logs `contact close-up: <what>, N vertices within 0.1 m of her lowest point, frame W m wide`.

**The hover check draws this close-up *instead of* the wide side view: one image, one render.** A check costs:

| Case | Renders |
|---|---|
| Contact confirmed / nothing to fix | **1** (the close-up) |
| A fix was applied | **2**: the close-up, then one more close-up at the hover really in force to verify (#312). Nothing else renders. |
| Sitting on furniture (manual `hover check` only) | **1**: the hips close-up; no fix exists to verify (hover is never changed), so never 2 |

The close-up is never an extra render on top of the wide one. `GT_HC_ZOOM=off` in the client's environment goes back to the wide side view (still one render).
The contact decision is still the gap measured in that same render; the close-up is the picture of it. The path of the close-up is logged
(`posehover`: `renders N; close-up of <what>: <path>`) and **`hover check` returns it**: it now waits for the check (up to 120 s) and answers with the verdict, the
render count and the close-up image path, so it can be shown to David. Tests: `LookFeetTests` (render count per check) and `test_look_scope.py` (stand / sit / kneel framing).

### Ground sit: tight on the butt, not head to heels (2026-10-26, live test after #315)

In an AO ground sit leaning back with the legs forward and the hands propped behind, the "lowest 10 cm of her whole body" is the heels, both hands **and** the butt, about 1.4 m
apart along the view, so the frame hit its 1.4 m cap and showed head to heels like a normal side view. The window is now searched only among the vertices within 0.30 m (horizontally)
of her pelvis bone (`ground_sit_contact` in `render_mesh.py`); the contact is their lowest 10 cm, and the frame is that extent + 0.3 m, 0.6 m at least and **0.9 m at most** for a ground sit.
Offline on the live export (hips 3 cm over a floor the heels touched): 1.40 m before, **0.70 m** after. The client sends the posture as `GT_HC_KIND` (`stand`, `sit` = AO ground sit, `seat` = on furniture,
else empty); only `sit` uses the pelvis window, so the standing (soles), kneeling / unknown (whole-body lowest 10 cm) and furniture (hips against the seat) framings are unchanged. No pelvis bone
in the export, or fewer than 20 vertices near it: the old whole-body contact is used.
The gap and the hover fix of a ground-sit check still come from her whole-body lowest vertex (the bias was calibrated that way, VAMOSITG26), so the close-up can show the butt a few cm above the grass
while the heels or hands touch it: that is the pose, not a hover error. Tests: `test_look_seat_contact.py` (synthetic leaning-back sit with heels and hands on the floor), `SeatContactTests` (`ContactEnv`).

### Sitting on furniture: hips against the seat (2026-10-26, David: "when you're sitting, you should be checking your hips, not your feet")

Found live after #313: seated on an AVsitter chair, `look self feet` framed her dangling foot over the grass, because the close-up assumed the terrain is what she touches.
When she sits **on an object** (`seat_info` says seated; the export's `sitting_on` is set), the close-up now frames the **hips against the seat surface** instead:

- **Hips:** the vertices within 0.30 m (horizontally) of her pelvis bone and between 0.30 m under and 0.15 m over it; the legs that hang in front are farther out. The contact
  is the lowest 10 cm of those, like the other postures (a stray vertex is ignored). No pelvis bone in the export: the agent position stands in for it.
- **Seat plane (#315, after the rocking-chair live test):** the median of up to 48 downward rays from the lowest patch of her butt (the contact points within 3 cm of her lowest vertex),
  each started 10 cm over her lowest hip vertex and skipping her own body, taking the first scene surface. Rays that fall between slats (no hit) or reach a rail far below are dropped; the median of
  the hits within 12 cm under her lowest vertex (else within 35 cm) is the seat top, and fewer than 3 hits means **"seat surface not found": the frame falls back to the hips' own
  bounding area** (still the hips, never the feet). It is used only when a prim is really under her (never the terrain). #314 used ONE ray at the agent position, which hit a slat edge. The frame is at least 0.8 m wide and starts 6 cm under the seat, so the seat line and the gap are in the picture.
- **Unobstructed (#315):** the chair's near frame, arm and rocker runners hid the hips from her left. Before the close-up is rendered, every face of the seat object (not her body, not the
  floor line) inside the corridor between the camera and her hips is cut away: from 5 cm past the near edge of her butt's contact patch to 0.3 m behind the camera, as wide as the frame
  plus 12 cm, from the frame's bottom to its top plus 12 cm. The seat under and beside her, the far frame and the slats she sits on stay. It is a cutaway of the render only; nothing in
  the world changes. Still one render.
- **Gap:** the render logs `contact close-up: hips against the seat, ...` and `seat contact gap +0.012 m` (hips above the seat +, sunk into it -). `look.py` prints both.
- **No hover model:** she is drawn at the sim's own seated placement (agent position + the seated offset, no hover pin, no bias, no floor snap), so the gap is her real sit height.
  Hover is not the fix on furniture: the furniture's own adjust is (AVsitter HUD, Z+ / Z-, then SAVE).

`hover check` while she sits on an object (manual only; the automatic sit / stand checks still skip furniture) renders **one** hips close-up and answers
`seat: hips on the seat (gap +0.010 m) | hips N cm ABOVE / sunk INTO the seat ...; hover left alone; renders 1; close-up of hips against the seat: <path>`. It never posts a hover,
touches the AO, stands her up or moves the furniture. **Verdict thresholds (#315):** a gap from -0.06 m to +0.02 m reads "hips on the seat" (her butt sinks a few cm into slats and cushions in every sit; the live rocking chair measured -0.038 m and David's eye said it looked good); more than 2 cm above reads ABOVE, more than 6 cm under reads sunk INTO. The gap is the render's measurement, about +-2 cm on a slatted seat (slat tops are uneven and the seat slopes), and the seated placement model is not calibrated against his viewer: his eye decides. `look self ...` while seated gets the same placement (the hover env is replaced by `GT_SEATED=1 GT_SEAT_CHECK=1`).
Ground sits, stands and kneels are unchanged (the table above). Tests: `vision/tests/test_look_seat_contact.py` (synthetic chair, floating / sunk / no-seat-surface / stray vertex) and `SeatContactTests`.

Use it by hand: MCP `look {target: "self", views: "feet"}`, or `client_cmd` `look self feet` / `look self side zoom=feet`.

**Connector error fixed.** `look {views: side, preset: hover-ground, cam_margin: 0.15, cam_height: 0.3}` failed with "An error occurred invoking look": the numbers were
JSON numbers but the tool declared those parameters as strings, so the call was refused before the tool ran. The numeric and flag parameters now accept a number, a string or
a boolean, and a failure inside `look` comes back as `look failed: <reason>`. `cam-margin` now allows 0.1–4.

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
| `cam-margin` | 0.1–4 | Camera distance factor (default 1.08; bigger = farther). |
| `cam-lens` / `cam-fov` | 14–200 mm / 5–120 deg | Lens or field of view across the long side (default 50 mm). `cam-lens` wins. |
| `cam-height` | −1…3 m | Camera height above the floor (default: half her height). |
| `views` / `views=` | `front,back,left,right,side` (= left), `feet` (= `contact`), `all`, degrees | Which sides (self only). Use `side`/`left` to judge height; `front`/`back` for fit. |
| `zoom` | `feet` (= `contact`, `close`), `off` | Turns a side view into the contact close-up (same as `views=feet`). See below. |
| `fast` / `far` | flags | Existing: quick render / add the backdrop to 96 m. |

Not exposed (the renderer has no switch for them): lighting and shadows (one fixed neutral sun, no per-light controls),
the anti-aliasing filter, the sky, depth of field (photo hd only). Options apply to `look self` and the hover check only; `view`, `around`, `at`
and `photo hd` keep their own settings and refuse these.

The side view always draws the floor unless you say `floor=off` (David's rule): `look self side` is the level, low camera with the red line at floor height.

## Hover check: pose-render guard (2026-10-26)

The hover check never POSTs a hover fix from a render that does not show the pose the client believes.
- On render 1 it reads the export's `scene.json` (`me.anims`) and `mesh.json` (`pelvis` local z). `sit` needs an AO sit animation in the export and a pelvis below 0.45 x her height; `stand` needs no AO sit animation and a pelvis above 0.55 x her height. Otherwise it logs `not verified: pose not rendered (...)`, changes nothing and returns `pose not rendered: ...` so the request retries.
- A fix larger than 0.12 m is POSTed only after a second render confirms the same gap (within 2 cm); otherwise `large fix not confirmed by a second render`. Render count: 1 (no fix), 2 (small fix, verify), 3 (large fix: confirm + verify).
- Cause of the 13:20 case: the collar's animations (947dd903, dccb493a, c9574fd9, 201d84aa) were learned as AO sits during a pose entry, so a stand counted as "AO sit playing". Animations started by own non-AO attachments are now recorded in the `other` class of `ao-anim-classes.json`, never learned as sits, and purged from the sit list on load.
