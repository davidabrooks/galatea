# David's Firestorm "High+" photo settings vs the photo hd pipeline (2026-10-13)

Profiles: `photo hd ... [highplus|ultra]` (look.py `--profile=`; highplus is the default). Ultra = Firestorm featuretable "list Ultra".

| Setting (High+) | Ultra | Our HD pipeline | Status |
|---|---|---|---|
| Transparent water on | on | water plane with EEP fog colour, fresnel-mixed transparency (max 0.88), depth fog from the night fog colour + ambient, noise-perturbed moon/sun glint | supported (approximation, no refraction distortion) |
| Ambient occlusion on | on | Cycles path tracing + SSAO on the sky ambient (ambient x mix(1, ao, 0.8), 0.8 m) | supported |
| SSR off / Mirrors off | off / off | GT_SSR=0: faces without a specular/PBR material get no reflection; shiny faces keep traced glossy (= probe reflections) | supported |
| Shadows sun/moon + projectors | same | sun/moon + point-light shadows; projectors = textured spot lights down the prim's -Z (fov from LightImage) | supported (focus blur / ambiance ignored; synthetic test only) |
| Reflection detail / coverage / max probes | same | path traced, no probes | n/a |
| Point lighting full, max nearby lights 4096 | 8192 | every exported prim light | supported |
| Attached (face) lights OFF | (default on) | attachment lights never rendered | supported (always off) |
| Draw distance 128 | 256 | export radius per profile: high 96, highplus 128, ultra 256 m, stepped down by MemAvailable (>=14 GB 256, >=10 GB 128, else 96) | supported (memory guarded) |
| Max particles 4096 / attached particles | 8192 | PSys data exported from ObjectUpdate; emissive/lit soft billboards, max 400 per source | supported (needs in-world check; 20:05 export predates it) |
| Avatar complexity 350k / 16 non-impostors | 16 | avatars past the 16 nearest drop to the lowest LOD (impostor stand-in); complexity not in the export | partly (no ARC limit) |
| Post process High | - | n/a | n/a |
| Exposure 1.0 | 1.0 | hdr_post exposure (GT_FS_EXPOSURE) | supported |
| Tone mapper ACES, mix 0.0 | ACES 0.7 | hdr_post ACES Hill, mix per profile (classic sky = 0 anyway) | supported |
| Sharpening (CAS) 0.4 | 0.4 | hdr_post.cas_sharpen | supported (approximation) |
| LOD factors objects 2 / flexi 1 / trees 0.5 / avatars 1 / terrain 2 | 3 / 1 / 1 / 1 / 2 | object LOD factor per profile (--lod-factor 2 / 3) on the backdrop screen-size LOD; near objects always highest | supported (objects; flexi/tree/terrain factors n/a) |
| Sky detail 96 | 128 | procedural sky shader | n/a |
| Anisotropic, SMAA High | same | Cycles pixel filter 1.5 + texture filtering | supported |
| HDR + emissive, glow on strength 2 | same | face glow AOV + Firestorm bloom (iterations 2, strength 0.325, width 1.3) | supported |
| Alpha masks on | on | mask / blend / emissive alpha modes | supported |
| Texture max 2048 | 2048 | GT_TEX_CAP_* = 2048 | supported |
| Shadow quality 1.0 | - | soft path-traced shadows | n/a |
| Terrain texture scale 12 | 12 | one tile per 12 m | supported |
| DOF off, FOV 60 | off | no DOF; HD self framing lens 50, scene views lens ~18 (60 deg vertical) | supported |

| Moon texture | same | procedural moon face (maria noise + limb darkening), 2.6 deg | partly (not the SL moon texture) |

## 2026-10-14: David's Ultra = the photo hd default

`photo hd` now defaults to `ultra` = David's own Firestorm Ultra screenshots (`highplus` stays available). Opt-outs:
`nossr nomirrors nodof nosmaa nosharpen noaces noimpostors` (look.py `--no=ssr,mirrors,...`).

| Setting (Ultra) | Our HD pipeline | Status |
|---|---|---|
| SSR on | every face keeps Principled specular; Cycles traces reflections in world space, so water and shiny/metal surfaces reflect off-screen objects too and every 4K tile is consistent | supported (better than screen space: no edge fade) |
| Mirrors on, 2048, update Ultra | smooth faces reflect sharply at the full render resolution; `nomirrors` floors roughness at 0.12 (probe blur). Mirror-flagged reflection probes aren't exported (LibreMetaverse has no reflection probe param), so a mirror prim reflects only through its own material | partly |
| DOF on, f/9, 50 mm, FOV 60, quality 0.7 | Firestorm's CoF (pipeline.cpp renderDoF + cofF.glsl) mapped to a Blender thin-lens f-stop (render_mesh.dof_fstop), CameraMaxCoF 10 px clamp, focus = first surface towards the subject. **Only with an avatar subject** (self portrait, David, a named avatar); view / scenery / object targets stay sharp (David 2026-10-14). Quality 0.7 (blur buffer scale) n/a: path traced | supported |
| ACES, mix 0.0 | hdr_post: mix 0 = Firestorm's toneMap() returns the exposed colour clamped (no ACES curve); classic skies are 0 anyway | supported |
| Sharpening 0.4 | CAS approximation | supported |
| SMAA Ultra | Cycles pixel filter 1.5 over 24+ samples (`nosmaa`: 0.01 px, aliased) | supported (equivalent) |
| LOD objects 3 / terrain 2 / sky 128, draw 256 | --lod-factor=3, radius 256 (memory guarded) | supported |
| 16 non-impostor avatars | --avatar-max=16 (`noimpostors` = all full) | supported |
| Avatar max complexity 350,000 | not in the export (ARC needs per-attachment cost data) | not matched |
| Max particles 8192, attached particles on, face lights off, HDR/emissive, glow 2, alpha masks, textures 2048, AO, shadows sun/moon+projectors, point lights full | as High+ above | supported |

## Texture streaming, mesh LOD and memory (2026-10-14, PR "HD photo memory = viewer texture streaming")

The Ultra HD photo (256 m, 4591 prims, 3 neighbour regions) loaded every scene face at 2048 px and Blender hit
~7 GB; the memory guard killed it twice. The viewer never holds that: it streams each texture at the discard level
its on-screen size needs. Ported from secondlife/viewer `develop` (Oct 2026; Firestorm 7.x carries the same code):

| Viewer (file / function) | Logic | Our HD pipeline |
|---|---|---|
| `LLPipeline::calcPixelArea`, `LLFace::calcPixelArea` | `app_angle = atan(radius/dist)`, pixel radius = `app_angle * sCurPixelAngle` (view height px / vertical FOV), area = pi r^2; dist < 16 m ramps to `(d/16)^2*16` | `handler.pixel_area`: the 4K output is the screen (2160 px landscape / 3840 portrait, 60 deg), her position the camera, radius/centre from each face batch's bounds in mesh.bin |
| `LLFace::getTextureVirtualSize` | `texel_area = |repeats*0.5|^2 * pi`; `vsize = pixel_area / clamp(texel_area, 1/64, 128)` | `handler.virtual_size`, repeats = the batch's UV extent |
| `LLViewerFetchedTexture::processTextureStats` | `discard = floor(log4(texels / vsize))`, min discard 1 above `RenderMaxTextureResolution` (2048) | `handler.discard_cap`: smallest power-of-two edge >= sqrt(vsize), 64..2048; fetched/decoded at that size (JPEG 2000 resolution levels skipped) |
| `LLViewerTextureList::updateImageDecodePriority` | `sDesiredDiscardBias` b >= 1 divides vsize by `round(4^(b-1))` (each +1 halves the edge) | `GT_TEX_BIAS` |
| `LLViewerTexture::updateClass` (low memory) | low system memory slams bias to 1.5, then raises it until usage fits the budget | `GT_TEX_BUDGET_MB` = MemAvailable - 1500 MB guard floor - 3000 MB Blender/BVH/film (600..6000); bias steps 1.5, 2.0, ... until the estimate (RGBA8, Blender image + Cycles copy) fits. Her own avatar and other avatars keep their caps (`crowd_cap`), like the viewer's boosted avatar textures |
| `LLVOVolume::calcLOD` + `computeLODDetail`, `LLVolumeLODGroup` | ramp `d < 2*LODFactor`: `(d/ramp)^2*ramp`; `d *= pi/3`; `tan = LODFactor*radius/d` (0.01 rounding); thresholds 0.03 / 0.06 / 0.24 -> LOD 0..3 | scene-mesher `FarLod` for the backdrop (> 30 m), RenderVolumeLODFactor 3 (Ultra) / 2 (High+), **per prim**: radius = 0.5 x |prim scale| (mLODScaleBias), distance = that prim's own (was the whole linkset's sphere, which put every child of a big build at the top LOD). Backdrop at Peronaut: 982k -> 565k triangles |
| Draw distance / impostors | far clip 256 m; avatars beyond the 16 nearest are impostors | export radius 256; `--avatar-max=16` (lowest LOD attachments) |

After a memory-guard kill the retry re-streams the textures with bias +1 (>= 2.0, i.e. every scene texture half its
edge) and half the budget, Cycles `texture_limit_render` 1024 and 512 px tiles; a third attempt goes one bias step
further with 256 px tiles. Cancelling a photo (SIGTERM/SIGINT to look.py, `vision/photo_cancel.sh`) SIGKILLs every
render child's process group; children also get `PR_SET_PDEATHSIG`, so a killed look.py never leaves Blender running.
