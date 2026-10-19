# RLV in Galatay's text client (phase 1, 2026-10-19)

David controls the N° 9 Premium collar (worn, always-wear) with his NX Remote HUD. The collar talks to the viewer with
`llOwnerSay("@...")`; `textclient/galatay-text/Rlv.cs` is our own implementation of that public RLV protocol.

**Trust:** only owner-say lines from objects attached to me are accepted (child prims are mapped to their worn root). Owner-say
from my rezzed objects, and anything from world objects, is ignored and logged. There is no relay in phase 1.

**Advertised version:** `@version` → `RestrainedLife viewer v3.4.3 (RLVa 2.4.2)`, `@versionnew` → `RestrainedLove viewer v3.4.3 (RLVa 2.4.2)`,
`@versionnum` / `@versionnumbl` → `3040300`. Replies go to the requested channel (positive channels only).

**Forces:** `@sit:<uuid>=force`, `@unsit=force`, `@remoutfit[:layer]=force` (body parts never), `@detach[:point|:object name|:#RLV folder]=force`,
`@detachall:<#RLV folder>=force`. Detach never takes off: always-wear / keep-attached items (the collar), the AO, body/head/hair, HUDs,
or a source that holds `@detach=n` on itself. Undress forces go through the wardrobe lock and mark me undressed, so no automatic
outfit puts clothes back on right away.

**Restrictions:** any `behaviour[:option]=n|add` is recorded per source and lifted with `=y|rem` or `@clear[=filter]`. A source that is not
worn for 60 s loses its restrictions. Enforced today on my own commands: `unsit` (stand), `sit`, `sendchat` (say/shout/whisper) and `detach=n`
(that item stays on through detach and outfit changes). Others (tplm, tploc, tplure, addoutfit, ...) are recorded and reported but not yet enforced.

**Queries:** `@getstatus`, `@getstatusall`, `@getoutfit[:layer]`, `@getattach[:point]`, `@getinv[:path]` (subfolders of `#RLV`), `@getinvworn[:path]` (`|xy,name|xy` worn flags 0-3), `@getpath`/`@getpathnew:<uuid|point|layer>` (#RLV-relative folder paths, empty when not under #RLV), `@findfolder`/`@findfolders`, `@getsitid`. Any other `@get...` gets an empty reply so a menu waiting on it never hangs (2026-10-19: the collar's Strip menu waited forever on an unanswered `@getpathnew`). Replies use normal say on the requested channel.

**Safety:** saying **MAYDAY** always reaches chat (even with sendchat restricted) so the collar's safeword works, and it also clears every
local restriction. `rlv_clear` (MCP) / `rlv clear` is the local emergency override. Nothing in RLV moves L$ or accepts transactions.
The NX Remote is never worn.

**Log:** every @ line, its source name/uuid/item, and what I did → `/workspace/secondlife/rlv/rlv-log.md` (`rlv_log`).

**Clothing tattoos and alphas (2026-10-19):** `@remoutfit:tattoo` / `:alpha` and removal by item name (`@remoutfit:<name>`, or `@detach:<name>` when the name is a worn layer) only take garments: a name or folder with underwear/panties/bra/... words and no skin/makeup/body/brow/lip/freckle/BoM words (`IsClothingLayer`). Everything else is protected (`rlv layers` lists the classification). When a garment comes off (RLV, undress, detach), an alpha tied to it (same folder, or "X Alpha" naming) follows; body/head/skin alphas without a tie and the collar stay (`AlphaFollow.cs`, logged here). `rlv force <cmd>` / MCP `rlv_force` tests a force locally.
