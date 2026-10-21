
### chan_say / body_reattach (2026-10-21)
- `chan_say(channel, text)`: speak on a non-zero channel (e.g. `/95 jeans menu` for a Maitreya auto-hide item). Channel 0 is refused.
- `body_reattach`: detaches the Maitreya body (not its HUD or add-ons) and re-attaches it immediately at the same point, which resets stuck alpha segments (e.g. an auto-hide item that never sent "show" on detach). COF is untouched; logged as `BODY REATTACH`.
- `cof_unlink(name_or_uuid)`: removes one Current Outfit link (exact name or link uuid; protected items refused) and rebakes.
- `v_pause(on|off)`: pauses The V auto re-attach (file `run/v-auto-paused`, survives relogs).
- `force_rebake`: Firestorm "Rebake Textures" equivalent (bumps COF version, POSTs cof_version to UpdateAvatarAppearance, retries with the server's expected version, reports bake ids).
- `collar_pause(on|off|detach)`: David's diagnostic switch for the N° 9 collar guard (file `run/collar-guard-paused`); `detach` only works while paused and not RLV-locked; `off` re-attaches.
- `attachment_faces(attachment, filter)`: read-only per-face TextureEntry dump (alpha/rgb/texture) for a worn attachment.
- `outfits_collar(check)`: every saved outfit in My Outfits links the N° 9 collar (adds missing links). `outfit_save(name)` (= `outfit create`) always includes the collar link. The #265 hard-coded collar guard and `collar_pause` were removed (the collar link in every outfit replaces them; `_always-wear.txt` unchanged; NX Remote refusal kept).
- `redress` never goes back into Naked/Android; it falls back to the last clothed outfit (`run/last-clothed-outfit.txt`).
- RLV `@detach/@remattach` match the inventory item name as well as the in-world name, and a COF link id resolves to its target item.
