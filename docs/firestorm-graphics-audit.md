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
