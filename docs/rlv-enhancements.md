# Galatea's RLV extras (beyond standard RLVa)

Plain-language list for David. Technical details: [rlv.md](rlv.md). "Tested" = tried in-world on 2026-10-19.

| Feature | What it does | What triggers it | Limits | Tested |
|---|---|---|---|---|
| Only my own worn things can command me | RLV commands are accepted only from objects I'm wearing (the collar). World objects and relays are ignored | Any @ command | No relay at all yet, so furniture/traps can't grab me directly | Yes (collar talks, others ignored) |
| Command log | Every @ command, which object sent it, and what I did | Always on | `/workspace/secondlife/rlv/rlv-log.md` | Yes |
| MAYDAY safeword + `rlv_clear` | Saying MAYDAY always reaches chat, even when chat is restricted, and clears all my local restrictions; `rlv_clear` does the same from my side | I say MAYDAY / call rlv_clear | Clears my side; the collar runs its own safeword logic | Unit tests only |
| Collar always worn | The N° 9 collar goes back on with every outfit, Naked too; nothing can detach it except the collar itself | Every outfit change, every detach | — | Yes |
| Collar Stealth in android outfits | Changing into an outfit named Android… turns the collar's Stealth on (only if it's off) | Android outfit change | Uses the collar's own About → Stealth button | Yes (it was already on, nothing pressed) |
| Body parts hidden from Strip | Body, head, eyes, teeth/gums, hair, hands/feet, skin add-ons, neckfix, AO, HUDs, collar and The V report a `> Stash/nostrip` path, so strip menus hide them | Collar asks @getpathnew while building the Strip menu | Name based; an oddly named body part may still show; detaching them is refused anyway | Unit tests; menu check pending David |
| Detach guards | Strip/@detach never removes body, head, hair, HUDs, the AO, the collar or anything the collar locked on | Any RLV detach | — | Unit tests + in-world shorts test |
| Clothing tattoos | Tattoo/alpha layers that are garments (panties, bra, underwear… by name or folder) can be stripped; skin, makeup, brows, lips, freckles, neckfix and BoM layers are protected | @remoutfit:tattoo, removal by name | Name/folder based | Classifier checked on my 3 worn tattoos (all protected); no garment tattoo worn to test |
| Alphas follow garments | When a garment comes off, its own alpha (same folder, or named "X Alpha") comes off too; body/head/skin alphas stay | Any strip, undress or detach | Needs a folder or naming tie | Ran on the shorts: no alphas worn, none removed |
| The V auto-wear | When nothing covers my groin any more, The V goes on; when something covering goes back on, it comes off (except Naked or outfits that include it). Never in android outfits | Strip, undress, outfit change | Cover = shorts/skirt/dress/pants/panties… by name, underpants/pants/skirt layers, panties tattoos | Yes (shorts off → The V on; redress → off) |
| `redress` ("get dressed") | Puts back the outfit I wore before being stripped, with its alphas; The V comes off | David says get dressed | — | Yes |
| Placement awareness | When RLV sits, stands or teleports me, my chat side gets an event with the object, where I am and whether @unsit is on | @sit/@unsit/@tpto force | Leash pulls aren't detected | Not yet with a real collar sit |
| Soft hold after a placement | After an RLV sit: no wander, no autofollow, and if something automatic stands me up I sit right back down. I can still stand deliberately | RLV sit | Ends when I stand deliberately, the collar unsits me, MAYDAY or rlv_clear | Unit tests only |
| Strict / @unsit | Only the collar's Strict force-sit (@unsit=n) and tp/leash restrictions stop me standing. TPE has nothing to do with it (N° 9 README) | Collar Strict | — | Unit tests only |
| Survives a relog | Restrictions and the hold are saved per item and restored after login; I'm sat back on the same seat if the collar doesn't do it | Relog | Same region only | Not yet |
