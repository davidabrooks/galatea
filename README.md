# Galatea – Second Life AI avatar

Handoff kit so another AI agent can help run or improve an AI-run avatar in Second Life. Home base: The Buddha Center, region **Naberrie**.

## What's here
- `textclient/galatay-text/` – headless C# text client (LibreMetaverse, .NET 10). It handles chat/IM, wandering, routes, AO guard, sit guard, mute list, profile, inventory read-only, webhook wake-up.
  - Added Oct 2, 2026 (`NewCmds.cs`), so Firestorm is rarely needed: `offers` (pending inventory/friendship offers, `offers accept|decline <n>`, never auto-accepted; anyone off the allow-list needs `confirm` = owner's OK), `friend list|accept|decline|add`, `landmark create|list|tp` (`list`/`tp` wait up to 15 s for the new region's inventory capability after a teleport instead of reporting an empty folder), `sethome` (set home to the current spot; prints the server's reply, e.g. a refusal off your own or group land), `worn links <attachment>` + `touch-attachment <attachment> <link|prim name> [face]` (press HUD buttons, e.g. the Martha AO power: `touch-attachment ao 1`), `shape get|set <slider> <0-100>` (only on the worn shape 'Galatea Petite shape - Jani short neck'; backup of the old asset in `shape-backups/`, upload, COF link refresh, server rebake).
  - Webhook debouncing (`Webhook.cs`), per conversation (IM sender, or local chat): it waits only when it sees a burst. A single line waits a 4 s detect window and then goes out at once. If a second line arrives inside that window, the conversation is held until 20 s of quiet (cap 60 s from its first line), then sent as ONE POST. Teleport offers, friendship offers and region-restart warnings go out immediately. Config `GT_WEBHOOK_BURST_DETECT_S` / `GT_WEBHOOK_QUIET_S` / `GT_WEBHOOK_MAX_S` or `webhook debounce <quiet> [max] [detect]`, `webhook debounce detect <s>`; `webhook debounce selftest`.
  - Known limits: the BC parcel routes all teleports to its landing point (107,150,53), so `landmark tp` to the sky platform lands on the ground; sit on 'BC: Build PlatformTeleporter' by the door to go up. Snapshots still need Firestorm.
- `textclient/galatay-mcp/` – MCP connector sharing the same core (`McpProgram.cs`, `Webhook.cs`).
- `textclient/text-galatay.sh` – start/stop/status/`cmd "<command>"` wrapper with a relaunch supervisor. Run `cmd help` for every command.
- `textclient/build-all.sh` – builds both; deploy by copying `galatay-text.dll/.pdb` from `app-staging/` into `app/`, then restart.
- `textclient/routes/` – walking routes for Naberrie. `*-block.txt`, `ao-item.txt`, `hover.txt` are config.
- `scripts/` – helpers (map/overhead drawing, path graph, password leak check).
- `client-options.md` – comparison of viewer/client options tried.
- `lelutka-evox-hud-SKILL.md` – notes on driving the LeLutka EvoX head HUD.

## Not included (on purpose)
No passwords, webhook URL/key, login state, chat logs, people log or notes about other residents. The client reads the password at runtime from a local secret store (`box-secrets.json`, key `card.SECONDLIFE_PASSWORD`); you need your own copy of that.

## Standing rules (from David)
- Never share David's real-life details. Spend L$ only with his explicit OK.
- Accept friends/teleports only from the owner's approved list; ask the owner about anyone else.
- Never reply to hostile messages; report the exact words to the owner. Known bad actors are muted.
- Greet each avatar at most once per 24 h; ignore short acknowledgements. Always be honest about being an AI.
- The Vista Martha AO HUD always stays attached (HUD Center, minimized, Force Sit off, slow walk). Other HUDs only temporarily.
- Clothing: nothing too sexy at the Buddha Center, no monastic robes, no face lights, never nude (add the new item before removing the old).
- Save outfits only with David's OK; edit shapes on copies. Current look: outfit "Jani tshirt" (Avalon head, Jani face, ~1.77 m). Avoid very short shapes.
- Face expressions: pleasant or neutral only.

## Status (Oct 2, 2026)
Logged out (deliberate stop) until the token reset, about Oct 5. When restarting: `text-galatay.sh start`.
