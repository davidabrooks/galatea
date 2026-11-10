# The V - Bento (Session Skins & ASA), Maitreya LaraX: package, HUDs, settings

Gift from David, 2026-10-07. Inventory folder `/Received Items/The V - Bento by Session & ASA Maitreya Lara X`
(`7ffe9e33-894f-39c3-b370-2aa7e3d8fe42`), version V1.61 LaraX. Tutorials: http://sessionskins.com/tutorials/

## Package
| item | uuid | notes |
|---|---|---|
| The V - Bento for Maitreya LaraX - SCULPTED - V1.61 | 3034de7b-4f25-3d3a-980c-8a00134f240d | **worn** (Stomach); in-world name "The V - Bento by Session Skins & ASA Studios". The full nude shape. |
| The V - Bento for Maitreya LaraX - CLOTHES FRIENDLY - V1.61 | e070c5ce-813d-334d-ab74-43456bf51158 | flatter shape that sits behind most clothes (V1.61). Not used: clothed outfits wear no V. |
| The V - Bento Play HUD by Session & ASA V1.61 COPY TRANSFER | 8b537b4b-009a-395d-95d7-70945f1b4a6b | **worn** (HUD top left). Main HUD; copy/transfer, so a copy can be given to a partner as a remote. |
| The V - Bento Texture HUD LaraX V1.61 | c15ae4d9-84b7-31f7-9aaf-94f658f6f68b | skin tone / colourize / redness texture (labia). Not attached. |
| The V - Bento Layer HUD LaraX V1.61 | 93c890a4-3712-3bad-a88e-e90ae48756e5 | layers: cum, redness, stubble. Not attached. |
| The V - Bento Shine HUD LaraX V1.61 | 7e738148-a6b0-3850-aca0-fe27730192af | specular shine for body and V separately (10 options x 4 intensities). Not attached. |
| The V - Bento Instructions V1.56 (FAQ) | 05cf5ef1-6bd4-39dc-9987-7572d7352b11 | 35-question FAQ (11.9k chars; `inv read` saves the full text to /workspace/secondlife/notecards/ after the relog with PR #102) |
| The V - Bento Maitreya LaraX RELEASE NOTES V1.61 | 82feb047-a975-34f4-a6a7-af428b6e51b9 | release notes |
| 24/7 V SUPPORT notecard, Session Skins landmark | adeaa5b1-..., ac23fe14-... | support group / store (kunst 177,184,23, Adult) |

Skin: the V takes her BoM skin automatically (Skin Applier Support = BoM; her VELOUR skin is BoM). Omega needs a L$99
relay and is not used. The V hides the Maitreya groin itself while worn.

## Play HUD (41 prims, root 'The V - Bento Play HUD by Session & ASA V1.61 COPY TRANSFER')
Read with `worn links "The V - Bento Play HUD"`; menu art = HUD textures saved under /workspace/secondlife/the-v/hud-*.jpg.
The prims are named by panel; the buttons are faces of the small overlay prims (`vagina_look_menu_2` = pubic hair, see below). Touch by prim + face.

| prim(s) | panel | what it does |
|---|---|---|
| sidebar, sidebar_1, sidebar_2 | left bar "The V Bento" | open panels: Vagina look, Animations (play), Sounds, Face, Deformers (tools), "..." Options; STOP; power (on/off), eye (hide V / show original BoM groin), minimize |
| shortcuts_side_large_1/2, shortcuts_side_small | Shortcuts side bar + mini HUD | push in, the 5 vagina shapes, fluids (drip, drops, spray, cum), favourite face, favourite sound, STOP |
| vagina_look_menu_1..4 | Vagina look | **Vagina Shapes**: PUSH IN + 5 shapes (closed .. open), TOGGLE EXTRA SHAPES (more of the 56 states; 39 = hidden/pushed in, 56 = puffy), REST POSITION Save/Load, 5 save slots. **Pubic hair**: SHAVED / BLACK / BROWN / BLOND / GINGER and TRIMMED / STRIP / BUSH. **Piercing**: NONE / ball / bars / hoop |
| animation_menu_1/2, animation_names, animation_play_buttons | Animations | 5 groups (vagina, hands-on-body, breasts, full-body stand, booty shaker) x 6 numbered animations with play buttons (e.g. Open Close Wide, Light Pulse, Clit Gyration, Fingering Self, Breast Fondler, Booty Bounce); ANIMATION SPEED slider; FLUIDS (drip, drops, spray, cum) |
| sound_menu, sound_names, sound_play_buttons | Sounds | 5 sets (F, MF, FF, 3S, ORAL) x 8 sounds, SAVE TO SHORTCUTS |
| face_menu, face_menu_1_1..3_1 | Facial expressions | ~45 face animations in pages (shy smile, moan, climax, ...), SAVE TO SHORTCUTS |
| deformer_menu, deformer_buttons_1..3, deformer_boobs_left/right, deformer_breast_rotate | Deformers / Shoulder positioning | breast and booty move/rotate gizmos, mirrored/separate, 3 save slots each; shoulder positioning for AO arms (play, adjust, save). Hold STOP 3 s to reset the deformers |
| options_menu_1..4, only_bom, only_omega | Extra options | Skin Applier Support BOM/OMEGA; Toggle V light ON/OFF; Sounds ON/OFF; Face Animations ON/OFF; Switch to Rest State on TP ON/OFF; Eye Icon Shows Original BoM Groin ON/OFF; Moaning Sound With Cum ON/OFF; Sound Volume HIGH/MED; RESET V COLOR; TOGGLE CUM LAYER; HUD RESIZE; REMOTE CONTROL (group/public/private/guest list); group/wiki/landmark/support |
| data, empty | internal | script data / blank backing |

## Current settings (2026-10-07 11:50 PT)
- **Pubic hair: BROWN + STRIP (landing strip).** David's choices on 2026-10-07: first "I would like you to have a landing
  strip of pubic hair", then "match my dark eyebrows". Her brows are dark brown: about (105,72,64) lit in the render.
  The HUD's hair colours average black (3,3,3), brown (58,36,25), ginger (124,72,49) and blond (167,117,86), so BROWN is the
  closest dark one (black is jet black). It shows as texture 0b81f6ab on The V. (Blond strip 5c4db955 was set for a few
  minutes before the eyebrow note.)
- **Piercing: HOOP (silver ring with a bead).** David asked for "a clit ring that matches your nipple rings". The
  nipple rings are [BB] Belzebubble Nipple Rings LaraX Petite (Puffy): silver captive-bead rings (chrome texture
  ed4c7f1f, white tint). The V's hoop is the same silver ring with a bead (texture 506f3252, white tint). The BB set has no
  clit or genital ring, so the built-in hoop is used. It persists with The V like the pubes, so there's no extra item in
  routes/_topless-extras.txt or in the Naked outfit (The V is already in both).
- Verified with `thev status` ("brown strip; piercing: hoop (ring)") and a 2560x4096 front render cropped to the groin.
- Everything else is factory default (shape and option radios).
- Persistence: the pubes and piercing are on The V's own prims, so the server keeps them in the attachment. Tested twice
  on 2026-10-07: The V was detached and re-attached, and its new prims came back with the same pubes and piercing. The
  HUD still drives the re-attached V. Relog and TP keep them the same way. There's no save button for these (REST
  POSITION Save is for the vagina shape only).
- Re-attach gotcha: a `wear add` about 8 s after detaching The V was silently dropped by the sim (it was still saving the
  changed V back to inventory). The second `wear add` worked. Since PR #103, `wear add` and the undress extras wait for
  the attachment and re-send once.

## Piercing: how to change it (text client)
`thev pierce none|ball|bars|hoop` touches a face of the big panel prim `vagina_look_menu_1`: 4 BARS, 5 NONE, 6 BALL,
7 HOOP. Faces 0-3 changed nothing visible. The V shows it as one of three hidden silver mesh children (texture 506f3252):
1 face = ball, 2 faces = hoop (seen in a render), 3 faces = bars (ball and bars are inferred from the face counts and
icons, not rendered). `thev status` reports it.

## Pubic hair: how to change it (text client)
`thev status` reads the current pubes and piercing; `thev pubes <words>` presses the HUD buttons, e.g. `thev pubes brown strip` (current),
`thev pubes shaved`, `thev pubes brown bush`, `thev pubes trimmed` (keeps the colour). Code: galatay-text/TheV.cs.

The 8 pubic hair buttons are invisible faces of the HUD prim `vagina_look_menu_2`; the face order is NOT the panel order
(learned by touching each face and reading The V back):

| face | button | | face | button |
|---|---|---|---|---|
| 0 | SHAVED (hides the pubes face) | | 4 | STRIP (landing strip) |
| 1 | BLACK | | 5 | GINGER |
| 2 | BROWN | | 6 | TRIMMED |
| 3 | BLOND | | 7 | BUSH |

A colour keeps the current style and a style keeps the current colour (from shaved, a colour comes back with the last
style). By hand: `touch-attachment "The V - Bento Play HUD" vagina_look_menu_2 <face>`, wait ~4 s, then `thev status`.
The V shows the result as the texture on face 7 (+ default face) of its mesh child whose faces 0+1 are the skin patch 8dc72b73 (alpha 0 =
shaved). Textures seen: blond strip 5c4db955, blond trimmed f4bb3440, ginger strip 97313e78, ginger trimmed d439f8f3,
ginger bush d0fd5d7b, black bush 12d20473, brown bush 918a4e8a, brown strip 0b81f6ab. The HUD says nothing in chat.
Blond on her fair skin is subtle in the 640 px `look self front`. Use a 1280x2048 render (GT_RES) and crop the groin.

## Main options David may want to adjust
1. Shape / state: one of the 5 shapes or an extra state (56 = puffy), then REST POSITION Save so it stays.
2. Pubic hair: shaved or a colour (black/brown/blond/ginger) and a style (trimmed/strip/bush). Now brown strip (David).
3. Piercing: none / ball / bars / hoop. Now hoop, matching the nipple rings (David).
4. Options: Sounds and Face Animations on/off (both play when animations run), Switch to Rest State on TP, V light.
5. Shine HUD (body + V spec) and Texture HUD (tone match / redness) if the V's colour does not match her skin.
6. Remote control: give David a copy of the Play HUD and allow him in Options > Remote Control.

## Galatea's rules
- Worn only while undressed: The V + Play HUD are in routes/_topless-extras.txt with the nipple rings (on after an
  undress, off with COF links removed when she dresses; outfit wear drops the HUD too).
- Outfit "Naked" under My Outfits = body, head, skin, shape, hair, rings, The V, Play HUD (+ AO as in every outfit).
- Do not press HUD buttons beyond the settings above without David's OK. No animations/sounds/fluids in public.
- HUD only shows on her own screen (nobody else sees HUDs), so it is never "in the way" for others.

## Face page probe (2026-11-03 15:00-15:15 PT, partial; the-v-face.json NOT written)
- Face buttons work without opening the Face panel first: each face of face_menu_1_1 / 1_2 / 1_3 (8/8/5 faces) plays its own
  face animation (The V @Stomach starts a unique anim plus the shared a129e25b + e29bf17f). Touch ST does not matter.
  Touching the face that is playing again stops it (a toggle), e.g. face_menu_1_1 face 1 = anim 1144d137 on, again = off.
- anim_list gives only UUIDs, not names, so a face's NAME can't be read back. Face names come from the atlas
  hud-2ad8d281.jpg (5 x 9, row-major; Pure Pleasure = row 4 col 2 = cell 17). The mesh face order is NOT proven to follow it.
  Seen in renders: face_menu_1_2 face 0 = eyes closed (fits cell 9 "Lick it all"); 1_2 face 7 = tongue out, eyes rolled.
  1_3 face 0 = half-lidded eyes, soft pout (candidate for Pure Pleasure, unconfirmed).
- anim_ids seen: 1_1 f0 b9356338, f1 1144d137, f2 c6b96ba9, f7 edf09ab5; 1_2 f0 f44eb6f7.
- sidebar_1 faces 0-7: none stops a face; face 5 swapped a V body anim (fd5b838b -> e88303a3), a vagina state button.
- sidebar_2 face 0 DETACHES the HUD (close button). Avoid. sidebar face 0/1 at st 0.5,0.46 did nothing.
- STOP is not found yet (candidates left: shortcuts_side_large_1/2, shortcuts_side_small, which also hold fluids/sounds).
- The anim-events.log and textclient.log lag behind under load (queued events); use anim_list.

## Face page and STOP mapping (2026-11-03 15:25 PT, read from a render of the HUD itself; supersedes the probe above)
Method: the HUD prims are meshes with the UVs baked in (repeats 1, offset 0), so each face's atlas cell comes from the mesh
UVs. The mesh assets are cached in ~/.cache/scene-mesher/mesh, the TEs are in the look scene export (attach_point 34), and
the atlas images are /workspace/secondlife/the-v/hud-*.jpg. Tools: /workspace/the-v-hud/mesh.py (LL mesh decoder) and
render.py (composes the HUD). Render: /workspace/the-v-hud/the-v-hud-render.png.
Page 1 shows atlas columns 1-4 of rows 1-5 (atlas hud-2ad8d281, 5 x 9 grid).
| prim | face | atlas row,col | label |
|---|---|---|---|
| face_menu_1_1 | 0 | 2,3 | Lick the Nip |
| face_menu_1_1 | 1 | 2,4 | Lick It All |
| face_menu_1_1 | 2 | 2,2 | Tongue Fuck |
| face_menu_1_1 | 3 | 2,1 | Supergasm |
| face_menu_1_1 | 4 | 1,4 | Definitely Coming |
| face_menu_1_1 | 5 | 1,2 | It's Coming |
| face_menu_1_1 | 6 | 1,3 | Coming Again |
| face_menu_1_1 | 7 | 1,1 | Shy Smile |
| face_menu_1_2 | 0 | 4,4 | Clit Suck Face |
| face_menu_1_2 | 1 | 4,3 | Ecstasy |
| face_menu_1_2 | 2 | 4,1 | Roar |
| face_menu_1_2 | 3 | 4,2 | Pure Pleasure |
| face_menu_1_2 | 4 | 3,4 | Moan |
| face_menu_1_2 | 5 | 3,3 | Talk Dirty |
| face_menu_1_2 | 6 | 3,2 | Lick It |
| face_menu_1_2 | 7 | 3,1 | 3some Tongue |
| face_menu_1_3 | 0 | 5,1 | Climax |
| face_menu_1_3 | 1 | 5,2 | Trying to Cum |
| face_menu_1_3 | 2 | 5,3 | Angry Sex |
| face_menu_1_3 | 3 | 5,4 | Lightly Stimulated |
| face_menu_1_3 | 4 | 9,-3 | (not a face: "More Expressions" strip) |

Mini HUD and shortcut overlays (texture e38b9792):
- sidebar_1 faces 5,4,3,2,1,0 = PUSH IN, shape 1 (closed), 2, 3, 4, 5 (open). Face 6 = the mini top bar, face 7 = the mini panel.
  Shape 1 = The V anim fd5b838b, the normal state (restored 15:2x). PUSH IN = e88303a3.
- sidebar_2: face 7 = drip, 2 = drops, 3 = spray, 4 = favourite face, 5 = favourite sound, **6 = STOP**, 1 = panel; touching face 0
  detached the HUD.
- shortcuts_side_large_1 faces 0-5 = PUSH IN and shapes 1-5; shortcuts_side_large_2 faces 2-6 = drip, drops, spray, face, sound (no STOP);
  shortcuts_side_small = the "Shortcuts" tab. sidebar face 0 = the whole main bar (icons need touch ST; not used).
Verified 2026-11-03: face_menu_1_2 face 3 started febe4578 (+ shared a129e25b, e29bf17f); sidebar_2 face 6 stopped all three
and kept the shape anim fd5b838b. Face buttons work without opening the Face panel, so the-v-face.json has no "open" touches.

## Incident 2026-11-03: The V hidden ("show original BoM groin"), and the fix
- From ~15:09 PT The V's main mesh (prim name "VHD", pubes 0b81f6ab) and the hoop were transparent, and prim "BOM_ORIGINAL"
  (24daea5f, the original BoM groin) showed instead. The shape animation (fd5b838b) kept running. Most likely cause: probing
  touched sidebar_1 face 6 (the mini HUD's top bar: power / eye / ▶) at its centre, and the centre is the eye.
- **Fix: touch `sidebar` face 1 (the eye overlay, texture px x134-153 y101-118) once.** At 15:40:4x VHD and the hoop showed
  again and BOM_ORIGINAL hid. The pubes (brown strip) and hoop were never lost.
- How to read it: in a look scene export (attach_point 28), the prim "VHD" face alpha should be 1 and "BOM_ORIGINAL" 0 (null).
  The hoop prim (2 faces) should be alpha 1.
- This HUD is in its minimized (mini) layout: `sidebar` face 0 (the full main bar) is hidden, so touch-ST presses on it do nothing.
- RISKY / do not touch: `sidebar` face 0 at st 0.066,0.98 and `sidebar_2` face 0 DETACH the HUD. `sidebar_1` face 6 = mini top
  bar (power/eye/▶); `sidebar_1` faces 0-5 = shapes / PUSH IN; `sidebar_2` faces 7,2,3 = fluids (drip/drops/spray), 5 = sound.
  The V's own touch menu (touch The V) offers <<<CLOSE | STOP | OPEN>>> | DRIP | SPRAY | CUM | FAV SOUND | CUM LAYER | FAV FACE | GIVE HUD.
  On 15:38 OPEN>>> was pressed once (no visible change; the shape anim stayed fd5b838b).
- vagina_look_menu_3 faces (texture hud-3b049989): 0 PUSH IN, 1 shape 1, 3 shape 2, 4 shape 3, 2 shape 4, 5 shape 5.
