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
The prims are named by panel; buttons are areas on the panel faces (touch by face + ST, not by prim name).

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

## Current settings (2026-10-07 11:15 PT)
Factory defaults, untouched: nothing on any HUD has been pressed. The HUD state (which shape, pubes, piercing, option
radios) lives in the V's scripts and is not readable from the text client; the in-world look is the reference.

## Main options David may want to adjust
1. Shape / state: one of the 5 shapes or an extra state (56 = puffy), then REST POSITION Save so it stays.
2. Pubic hair: shaved or a colour (black/brown/blond/ginger) and a style (trimmed/strip/bush).
3. Piercing: none / ball / bars / hoop.
4. Options: Sounds and Face Animations on/off (both play when animations run), Switch to Rest State on TP, V light.
5. Shine HUD (body + V spec) and Texture HUD (tone match / redness) if the V's colour does not match her skin.
6. Remote control: give David a copy of the Play HUD and allow him in Options > Remote Control.

## Galatea's rules
- Worn only while undressed: The V + Play HUD are in routes/_topless-extras.txt with the nipple rings (on after an
  undress, off with COF links removed when she dresses; outfit wear drops the HUD too).
- Outfit "Naked" under My Outfits = body, head, skin, shape, hair, rings, The V, Play HUD (+ AO as in every outfit).
- Do not press HUD buttons beyond defaults without David's OK. No animations/sounds/fluids in public.
- HUD only shows on her own screen (nobody else sees HUDs), so it is never "in the way" for others.
