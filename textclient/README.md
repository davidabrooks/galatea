
### chan_say / body_reattach (2026-10-21)
- `chan_say(channel, text)`: speak on a non-zero channel (e.g. `/95 jeans menu` for a Maitreya auto-hide item). Channel 0 is refused.
- `body_reattach`: detaches the Maitreya body (not its HUD or add-ons) and re-attaches it immediately at the same point, which resets stuck alpha segments (e.g. an auto-hide item that never sent "show" on detach). COF is untouched; logged as `BODY REATTACH`.
- `cof_unlink(name_or_uuid)`: removes one Current Outfit link (exact name or link uuid; protected items refused) and rebakes.
- `v_pause(on|off)`: pauses The V auto re-attach (file `run/v-auto-paused`, survives relogs).
