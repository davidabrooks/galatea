---
name: LeLutka EvoX head HUD
description: >-
  Use this when reading, changing, or comparing settings on a LeLutka EvoX head
  HUD in Second Life (eyes mode, sliders, size/offset, eye sockets, matching
  alphas), or when fixing eye problems on an EvoX head.
---
# LeLutka EvoX head HUD (Eyes tab focus)

Sources: the official LeLutka Evolution manual (https://lelutkasl.com/evolution/manual/), community threads on EvoX eyes and alphas, and hands-on comparison of the 4.0 and 3.1 HUD layouts.

Key rule: before reporting a HUD state, walk the checklist in section 10 and report EVERY difference, including sliders and toggles, not just the one you were asked about.

## 1. Which HUD is it?
- Inventory name: `/ HUD / lel evox (f) 4.0` or `(m) 4.0` (current "Evolution 4.0" layout), `/ HUD / lel EvoX (m) 3.1` (older 3.1 layout). The head and HUD must be the same generation.
- Attach with right-click > **Add** (never "Wear", which can knock other HUDs off the Center point). It attaches to HUD Center. Detach via Appearance > Wearing > Detach, or the HUD's own detach icon.
- Settings (eye mode, sliders, socket, etc.) are stored **in the worn head/eye objects**, not in the outfit. Changing them doesn't need an outfit re-save. Only worn wearables/attachments (alphas, etc.) live in outfits.

## 2. Sidebar (left column), top to bottom
| 4.0 layout | 3.1 layout | Tab |
|---|---|---|
| lel logo (top) | lel logo | main |
| head outline | face/head | Head & Skin (UV mode SL/EvoX, skin material, hide scalp, ears) |
| lashes | (other icon) | Lashes (4.0) |
| eye | eye | **Eyes** |
| teeth | teeth/mouth | Mouth & Teeth |
| tall pill with stacked layers | "HD" | HD Brow / Eyelids / Lips (Beard on male heads) |
| droplet | droplet | Tint panel for the current tab |
| play triangle | play triangle | Animations (moods, eye control, vocal) |
| gear | gear | Settings (reset scripts, repair materials, HUD size/position) |
| small round icons outside the HUD | icons at bottom | Utility poses: closed eyes, open mouth, pose |
The active tab icon is filled pink.

## 3. Eyes tab sections (top to bottom)
1. **Preview image + "BAKES ON MESH" button**. Pink = mesh eyes show the **system eyes** texture (BoM). Dark = Applier mode. Clicking any iris swatch switches back to Applier mode. The preview picture is only an illustration, not the current eye colour.
2. **Apply to: RIGHT | BOTH | LEFT** (3.1 "APPLIER / APPLY TO", 4.0 "AFFECT EYE (APPLIER)"). Normally BOTH.
3. **Eye colour grid**: 18 irises. Clicking one = Applier mode + that iris. None highlighted in BoM mode.
4. **MATERIAL** row: "BLANK" and 2 material thumbnails.
5. **Sliders: GLOSSINESS, INTENSITY, ENVIRONMENT**. The pink filled part of the track shows the value.
6. **EYE OPTIONS**
   - **SIZE**: 5 concentric rings = 5 iris sizes; the **middle (3rd) ring is default**. Selected ring is pink.
   - **OFFSET**: 4 arrows around a centre circle. **Centre lit = default position**; an arrow lit = iris offset that way. Clicking the centre restores default.
   - **RESET**: size + offset back to default.
7. **HIDE EYES: RIGHT | BOTH | LEFT** (4.0 only): hides the mesh eyeballs. All dark = visible.
8. **EYE SOCKET(S)**: **HIDE EYE SOCKET(S)** toggle (pink = hidden) + OPTIONS row of socket shades + **MATERIAL ON | BLANK**.

## 4. ON vs OFF
- Pink/salmon filled button = ON/selected. Dark with white text = OFF.
- In a pair like "MATERIAL ON | BLANK", the word with the pink pill is the current choice.
- Zoom a crop (2x-4x) before deciding; phone photos have glare, so compare with a button you know is on (e.g. BOTH).

## 5. Reading a partly visible size target (4.0)
In 4.0 the SIZE target is a quarter circle in the bottom-left corner of its box. Count bands from the corner outward: centre, ring 2, **ring 3 (middle)**, ring 4, ring 5 (outer). A pixel scan along one row is the most reliable count. If still unsure, say so and ask the other person to verify. In 3.1 the full circle is visible.

## 6. Setting sliders
- Click-to-position on the track; allow 1-2 s to redraw.
- Measure track start x0 and end x1 in a screenshot; target x = x0 + p × (x1 − x0). Verify where the pink fill ends afterward.
- Estimating someone else's values from a photo: crop the slider area (Python/PIL), value ≈ (knob centre − track start) / (track end − track start).
- If a crop is displayed down-scaled, convert coordinates using the real crop size before clicking.

## 7. Eye modes and the matching LeLutka alpha
| Setup | Alpha to wear |
|---|---|
| BoM head + BoM body + **applier** eyes | `+ ALPHA ( BOM Head + Body - APPLIER Eyes )` (hides system eyeballs) |
| BoM head + applier body + BoM eyes | `+ ALPHA ( BOM Head + Eyes - APPLIER Body )` |
| BoM head + applier body + applier eyes | `+ ALPHA ( BOM Head - APPLIER Body + Eyes )` |
| **BoM head + BoM body + BoM eyes** | **no LeLutka alpha** |
- "APPLIER Body" alphas make a BoM mesh body disappear.
- BoM eyes still need the mesh eye attachment (`/ EYES / lel evox`) worn; the colour comes from the system eyes layer.

## 8. Common problems
- **Empty/invisible eye sockets**: eye mode and alpha don't match. Remove the eye-hiding alpha (full BoM), or go back to Applier mode and keep the APPLIER-Eyes alpha.
- **Dark marks above/around the eyes, double eyes**: system eyes poking through or socket showing. Fix in order: correct alpha for the mode; turn on HIDE EYE SOCKET(S); SIZE ring and OFFSET; shape Eye Depth/Size last.
- **Wrong iris colour**: applier texture still set, or wrong system eyes in BoM mode.
- **HUD not responding**: Settings > Reset Scripts; Repair Materials if shine broke.

## 9. Other tabs (look, don't change unless asked)
- Mouth & Teeth: teeth options, hide teeth/tongue, mouth shades, material, shine sliders, braces/gems add-ons.
- Lashes (4.0): styles, lengths, alpha mode.
- Head & Skin: UV mode SL vs EvoX (skins must match), skin material, hide scalp, ears.

## 10. Checklist: compare another HUD against your own and report EVERY difference
1. Get their image of the full tab. Note their HUD version; list layout-only differences (labels, ring drawing, swatch shapes, extra sections) separately from setting differences.
2. Attach your HUD (Add), open the same tab, screenshot, crop.
3. Walk top to bottom, one line per control, "theirs / yours / same?":
   - eye mode (BAKES ON MESH on/off)
   - Apply to
   - selected iris swatch
   - MATERIAL: BLANK vs thumbnail
   - GLOSSINESS %, INTENSITY %, ENVIRONMENT %
   - SIZE ring
   - OFFSET: centre or which arrow
   - HIDE EYES
   - HIDE EYE SOCKET(S)
   - socket OPTIONS shade
   - socket MATERIAL ON/BLANK
   - anything else lit pink
4. Also compare worn alphas and the system eyes layer.
5. Change only what was asked; list every remaining difference and why it was left.
6. Re-screenshot and re-check every line after changes.
7. Close-up eye check: dark marks, double eyes, empty sockets, iris colour.
8. Detach the HUD. Re-save the outfit only if worn items changed.
