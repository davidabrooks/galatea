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
