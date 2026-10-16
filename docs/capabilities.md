# What Galatea can do, and how to drive it

A guide to the Second Life text client (`textclient/galatay-text/`, LibreMetaverse) and its MCP connector (`textclient/galatay-mcp/`). It says what each area does, the command syntax, and where the settings live. It is not a full command reference: **`text-galatay.sh cmd help`** prints every command, and the code is the authority. The README has the architecture, standing rules and per-feature history; [TESTING.md](../TESTING.md) covers tests.

Conventions:
- **CLI**: `text-galatay.sh cmd "<command>"` (the wrapper sends one line over a Unix socket to the running client). Exit codes: 0 ok, 1 client not running, 3 socket unreachable, 10 a normal "skipped:" reply (the IM duplicate guard), 2 usage. The wrapper gives up after 100 s (`GT_CMD_TIMEOUT_S`); the client itself answers after at most 85 s (`GT_CMD_REPLY_CAP_S`), and a late result is logged as `[cmd-late]`.
- **MCP tools**: the same core, exposed as tools (`login`, `say`, `walk_to`, ...). Each tool returns JSON `{"ok": bool, "result": "..."}`. Only the tools listed under [MCP connector](#mcp-connector-tools) exist as MCP tools; everything else is CLI only.
- Paths: the client is deployed under `/home/box/viewers/` (`text-galatay.sh`, `textclient/app/`, `textclient/run/`, `textclient/routes/`); logs and images go under `/workspace/secondlife/`. Nothing here needs a password on the command line: the client reads the account password itself from the box secret store.

## Contents
[Starting, stopping, supervisor](#starting-stopping-supervisor-autostart) · [MCP connector](#mcp-connector-tools) · [Chat and IMs](#chat-and-ims) · [Seeing the surroundings](#seeing-the-surroundings) · [Moving](#moving) · [Sitting, poses](#sitting-and-poses) · [Following](#following) · [Outfits and attachments](#outfits-and-attachments) · [Touch and dialogs](#touch-and-dialogs) · [Inventory](#inventory-and-profile) · [Photos and renders](#photos-and-renders) · [Textures](#texture-vision) · [Voice](#voice) · [Teleports](#teleports-and-offers) · [Webhook and events](#webhook-and-events) · [Keeping awake](#keeping-the-computer-awake) · [Logs](#logs-and-log-pruning) · [Impacted tests](#running-only-the-impacted-tests-development)

## Starting, stopping, supervisor, autostart
```
text-galatay.sh start | stop | status | cmd "<command>" | log [n] | check
```
- `start` runs the client under a **relaunch supervisor**. If the client exits with a non-zero code (for example 75 from the in-client watchdog or a network timeout) or is killed, it is relaunched after a backoff of 15, 30, 60, 120, then 300 s; it gives up after more than 12 relaunches in an hour (`GT_MAX_RELAUNCH_HOUR`). A hang monitor kills the client if `run/heartbeat` is stale for more than 120 s on two checks. A clean exit (code 0, from `logout`) ends the loop. If another login of the same account kicks her, the supervisor waits 5 minutes before relaunching.
- `start` refuses if a `galatay-text` process already exists outside its supervisor/pid file, and is idempotent otherwise (one start at a time, under a lock).
- **Deliberate stop**: `text-galatay.sh stop`, and the client's own `logout` command, write the marker `textclient/run/deliberate-stop`. While it exists, nothing restarts her. Only a manual `text-galatay.sh start` clears it. It survives a box reboot because it lives under `/home/box`.
- **Autostart at boot**: `galatay-autostart.sh` (launched detached by `galatay-mcp.sh` when the box brings the MCP connector up) runs `start` once per boot unless the marker exists, the client is already up, or Firestorm is running; it waits for the network first. Log: `/workspace/secondlife/textclient.autostart.log`.
- `status` shows the supervisor, the marker, and the client's `status` line.
- Login location defaults to home (`GT_START=home`, set in `text-galatay.sh`).
- Deploying a new C# build means copying the built dlls from `textclient/app-staging/` into `app/` and restarting (see README, `build-all.sh`); Python-only changes (`vision/`) take effect on the next render with no restart.

## MCP connector tools
The connector (`galatay-mcp.sh`) is the same client core as a stdio MCP server. Tools:

| Area | Tools |
|---|---|
| Session | `login`, `logout`, `status` (state, region, position, seat, follow target, pending teleport, memory), `poll_events`, `webhook_test`, `webhook_status` |
| Talking | `say`, `im` (`to`, `text`, optional `headsup`, `re`) |
| Looking | `nearby` (`radius`, `filter`, `max`), `object_info` (`uuid`) |
| Moving | `walk_to` (x, y, z), `walk` (metres), `turn` (degrees), `stop`, `teleport` (region, x, y, z), `follow` (`name` or `off`, optional `distance_m`) |
| Sitting | `sit` (object uuid), `stand` |
| Teleport offers | `accept_teleport`, `decline_teleport`, `auto_accept_teleports` (`on`) |
| Profile | `get_profile`, `set_profile` (About text only, backs up the old profile first) |
| Voice | `voice` (`action` = on, off, status, tail, wake) |
| Dialogs | `dialog_reply` (`button`; refuses anything that looks like payment) |

Everything else (outfits, photos, `touch`, `goto_place`, `wander`, `inv`, ...) is a CLI command. Adding a connector build needs `build-all.sh --stage-mcp`; the MCP server namespace has to be restarted by its owner to pick it up.

## Chat and IMs
```
say [--re <msg id>[,<id>]] [--force] <text>      shout <text> | whisper <text> | chan <n> <text>
im [--headsup|--force] <First Last|username|uuid> <text>
im --re <msg id>[,<id>] <to> <text>
chatlog [name] [n=20]        imlog <name> [n=20]        (read-only history with message ids and answered state)
```
- `say` is 20 m local chat, rate-limited (8 per minute). `say --re` / `im --re` are **claim-and-send**: they answer exactly those incoming message ids and are refused if one was already answered, so two routine runs cannot double-reply.
- **Duplicate-IM guard**: `im` is refused (reply `skipped: ...`, CLI exit 10, MCP `ok: true, skipped: true`) if she IMed that avatar under 5 s ago, or already answered their latest IM within 6 h (the owner: only the 5 s rule). `--headsup` allows one short "give me a bit" note per recipient; `--force` bypasses the guard and is for manual sends. Details in the README (ImGuard).
- `chatlog` and `imlog` read the tail of `textclient.log`; they never say anything. A muted avatar's chat/IM/offers are dropped (`mute <uuid|name>`, `unmute`, `mutelist`).
- Greetings are throttled in code (once per avatar per 24 h, `GT_GREET_REPEAT_HOURS`).
- Settings: `GT_IM_GUARD_WINDOW_S`, `GT_IM_GUARD_FRESH_H`; fast replies `routes/_fast-chat.json` and `routes/_instant-replies.json` (`fastchat status`, `instant status`).

## Seeing the surroundings
```
nearby [radius=20]            avatars + objects (uuid, distance, owner, who is sitting)
objects [radius=20] [name filter]      find <name filter>   (64 m)      objinfo <uuid>
avatars                       avatars in view with heading
map [radius] [x y]            objects with size and rotation (planning data)       terrain <x> <y>   ground height
scene export [radius]         read-only dump of prims, attachments, bakes, terrain and environment -> /workspace/secondlife/vision/export-<time>/
crowd [radius]                who is complete / partial / bare among nearby avatars
```
`nearby`/`objects` read what the simulator has streamed to her (`far [metres]` and `interest [status|360|default]` change how far and how complete that is). Pictures of the scene: see [Photos and renders](#photos-and-renders).

## Moving
```
moveto <x> <y> <z>        (MCP walk_to)      walk <metres>      turn <degrees>      face <x y z | avatar>      stop
walk_path x,y,z;x,y,z;...        walk_status        walk_stop        goto_avatar <name>        front <avatar> [m=1.5]
goto_place <place> [nosit]       nav [status|places|doors|plan|to ...]       route list|show|walk <name>
wander start|stop|pause|hold|resume|status|seats
```
- Walking is the server autopilot and **never teleports**. A stuck walk recovers with sidesteps; the last-resort fly hop is off by default (`nofly on`); `--fly` on `walk_path`, `goto_avatar`, `sit_near` or `nav to` allows it for that one walk.
- Where a **nav grid** (`routes/_nav-<name>.json`) covers both ends and the straight line is blocked, `moveto`, `goto_avatar` and `follow` plan a route around walls and through doors (touching them). `goto_place` uses the path graph of the region (`routes/_graph-<region>.json`); `nav places` lists named places for a grid.
- `wander` is the idle loop (route legs, seats, greetings); `wander status` reports what she is doing, `wander hold|pause|resume` interrupt it, and it resumes after a restart or reboot (see its help line for the staleness rule after a deliberate stop). Tunable rules live in `routes/_wander-rules.json`.
- `door [name filter|uuid]` touches the nearest door within 10 m; `door try` runs the full sequence.
- Route walks (`goto_place`, `route walk`, wander legs) touch each mapped door pair early (about 5 m ahead at walking speed), also on the last stretch before the grid's edge (front double doors). The touch leads by swing + latency + tick + 0.5 s margin the measured 2.4 s read-open lag and never by less than 10 m of path at her walking speed (about 11.8 m at 3.2 m/s; also from a standing start and on stairs and turns, where the measured speed dips), so a door is first touched at least about 9 m ahead; at the start of a walk she waits (up to 3 s, less her walking time to the door) until a leaf reads open for any door within 10 m; a second door within 8 m is touched in the same batch; each touch logs how long the door took to read open. A door within 6 m of where a walk starts is opened first, while she is still standing, and she waits up to 1.2 s for a leaf to swing before the first step (Oct 16, 2026). A door guard stops her 1.5-2 m before a mapped door that is still closed; it never re-touches a door touched less than 3 s ago (touch toggles; the door state lags 2.1-2.4 s), waits for a leaf that is moving, and touches again at most once, only if the leaf has not moved at all.
- Settings: `run/nofly.txt` (nofly), `routes/` (everything above), see `route status`.

## Sitting and poses
```
sit <object uuid>        sit_near <avatar>        stand        pose [change|couples|path A>B>C]        hover [get|set <m>|repin]
```
- `sit` sits on an object by UUID (find one with `nearby`). A sit guard re-checks pose and hover height a few seconds after sitting (`sitguard on|off`).
- `pose` is a solo random pose by default and never starts a couples pose by itself (`pose couples` only when the owner asks; `pose path A>B>C` restores an exact menu path). The AO stays attached; `textclient/attach-block.txt` lists items taken off while seated and re-worn on standing.
- The sexual-pose rule holds for every way a pose button is pressed, including `pose couples` and `pose path` (Oct 16, 2026): never in an android outfit; in a human outfit only in Naked and only when David is the only other avatar in Peronaut. A menu or leaf is treated as sexual by name (`fist`, `balls`, `job`, `MF*`, `Adult*` and the rest of the word list), and nothing is picked inside a sexual menu, even an entry that looks harmless. If every couples option is sexual, `pose couples` keeps the current pose and says so in the log (`refused sexual pick`).
- When David stands up from a couples pose and stays close (Oct 16, 2026), she switches to a solo pose searched from the seat's root menu (all solo submenus, not only the couples submenu the seat reopens in). If none qualifies she uses the seat's default pose, the one it gave when she first sat down alone (recorded per seat; else the first non-sexual single pose on the menu, logged), and never leaves the couples pose running alone. The android / alone sexual rule still applies.
- Her menu back-navigation works on any furniture, at any number of menu levels (already at the top, 1, 2, 3, 4+; cap 8, logged): she presses only the real `[BACK]` (never the `[<<]`/`[>>]` page buttons) and re-reads the menu until it has no `[BACK]` (the top-level menu). The solo search and default-pose fallback only run there; if the top level cannot be reached she keeps her current pose and logs why. The seat's sit-alone default pose is recorded from the header of the first menu after sitting (no name matching). This works for every way of sitting (wander sit, `sit`, relog beside the seat) even when the menu arrives before the sit is confirmed (kept and retried); it records only when she sits alone and has pressed nothing yet, never a couples / male / sexual pose, logs the reason whenever it declines, and keeps the per-seat default in `seat-default-poses.json` so it survives relogs and restarts.
- Hover height is pinned in `textclient/hover.txt` (`hover set` writes it).

## Following
```
follow <First Last> [metres]      follow off      follow dist [m]      follow status       autofollow on|off|status
```
She keeps about 2.5 m behind (stops at 2-3 m, resumes beyond 3.5 m, routes around furniture, opens doors). `autofollow` follows the owner automatically when they are in the region within 20 m; `follow off` snoozes it for 10 minutes. State in `run/autofollow.txt`.

## Outfits and attachments
```
worn [scripts]        worn all        worn links <attachment|ao>
outfit wear <name> [replace|add]       outfit plan|create <name>       outfit check       outfit daily status
wear add|remove <item> [attach point]      attach <item> [point]      detach <item>
touch-attachment <attachment|ao> <link no.|prim name> [face]       bikini on|off
```
- `outfit wear` swaps in a My Outfits folder without stacking (one hair, one head, body replaced, AO untouched) and randomises the outfit's colour HUDs. The client also picks a random outfit once per day (`outfit daily status`) and swaps outfits on a beach/house zone boundary (`outfit zone status`); undressing and outfit changes share one wardrobe lock so they cannot overlap (`Wardrobe.cs`).
- `wear add|remove` and `attach|detach` are reversible and logged; body parts need `wear bodypart`/`shape wear`. `outfit coffix` repairs missing COF links so a relog keeps the look.
- An android body is never swapped to human automatically: not at login (the daily change waits for a human login) and not when walking back from the beach to the house. Only David's command or an explicit request changes it.
- With an android body worn she does not plan any bedroom detour for the bikini (Oct 16, 2026): a beach-bound wander leg, a seat on the beach level or the pier goes straight to its target, and going back up does not walk to the bedroom to "change back" (nothing was changed). The same single check (`BikiniSwapSkipped`) decides the `bikini skipped: android body worn` log line and the planning. Human bodies behave as before (bikini in the bedroom first); `bikini on` typed by hand still wears it; the 30 min level dwell is unchanged.
- The README's "Standing rules" apply (never nude, save outfits only with the owner's OK).

## Touch and dialogs
```
touch <object uuid>          dialog <button label>
```
`touch` presses an object that is in the region's object list; a menu it opens arrives as a `[dialog]` log line and `poll_events` item with its button labels. `dialog <label>` presses a button on the **most recent** dialog (labels are matched case-insensitively and trimmed; a wrong one lists the real buttons). The MCP `dialog_reply` refuses buttons containing L$, pay or buy: she never spends L$ except with the owner's explicit OK (`pay object <uuid> <L$> confirm`).

### `[loadurl]`: scripts that open a web page
Objects call `llLoadURL` to send a link (for example a control panel of a rental box or security orb). The client logs every one to `textclient.log` as
```
2026-10-15 12:00:00 [loadurl] object '<name>' <object uuid> owner <owner uuid> message '<text>' url <url>
```
and writes only the **latest** to `last-loadurl.txt` (mode 600; next to `textclient.log` unless `GT_LAST_LOADURL` sets another path). The URL may carry a login token, so it deliberately bypasses `Log()`: no console echo, no `poll_events`, no webhook, and `last-loadurl.txt` is excluded from the box's Drive backup. The client never opens or fetches the link; reading it is a manual step (treat the URL as a secret, do not paste it into chats or tickets).

## Inventory and profile
```
inv find <text>[|text2]        inv ls <folder uuid>        inv read <notecard item>        invitem <item uuid>
inv mkdir|move ...             rez <item> | take <object>   (her own objects only)
profile get      (MCP get_profile)       set_profile "<About text>"      balance | prices | caps
```
`inv find/ls/read` are read-only (`inv find` stops after 30 s and resumes on the next call; partial results say PARTIAL). `inv mkdir|move` are logged and reversible. Spending needs the owner's OK (`pay ... confirm`); `balance`, `prices`, `caps` are read-only.

## Photos and renders
Two modes; setup and memory needs are in the [README](../README.md#photos-and-renders).
```
look [self [front|back|left|right|<degrees>|all]|around|at <avatar or object>] [fast] [far]
photo hd [self|view|<avatar or object>] [landscape|portrait] [<sky preset>] [ultra|highplus] [fov <deg>] [nossr nomirrors nodof nosmaa nosharpen noaces noimpostors]
```
- **Quick `look`** (her own vision, not a photo for the owner): a small JPG of the near scene in a fixed neutral light, ignoring the in-world time of day. `look self` gives face + full body (`look self back,left,45` only the body from those angles, degrees counter-clockwise from her front); `around` = four views; `at X` turns her head toward X first; `fast` uses fewer samples; `far` adds the 96 m backdrop. JPGs: `/workspace/secondlife/vision/look/` (`GT_LOOK_OUT`). It waits up to 75 s for the result, then keeps going and logs the path as `[look]`.
- **`photo hd`** (for the owner): 3840x2160 (`landscape`, default) or 2160x3840 (`portrait`, default for `self`) in the real surroundings. Output in `/workspace/secondlife/photos/` (`GT_PHOTO_OUT`): `photo-<stamp>-<mode>.png` (the photo; 16-bit RGB, about 30 MB at 4K), `.jpg` (quality 95) and the **HDR masters** `.exr` and `.hdr`. Takes minutes on the CPU; the command returns after a few seconds and logs the paths as `[look]`.
- **Environment**: HD photos follow the shared environment (parcel, else region) like Firestorm's "Use shared environment". Name a preset to override the sky: `shared` (default), `default`, `midday`, `midday-legacy`, `sunrise`, `sunset`, `midnight`, `night`, `foggy` (as a bare last word or `preset=<name>`), e.g. `photo hd self landscape sunset`. See [docs/photo-hd-environment.md](photo-hd-environment.md).
- **Look of the render**: the default profile `ultra` mirrors the owner's Firestorm Ultra settings; `highplus` is the other. The opt-outs turn individual effects off (`nodof`, ...). Depth of field is only applied when there is an **avatar subject** (`self`, or a named avatar); scenery, `view` and object targets stay sharp. Comparison with the viewer: [docs/firestorm-graphics-audit.md](firestorm-graphics-audit.md).
- **Framing**: `fov` is the vertical field of view, 10 to 120 degrees (default 60, Firestorm's default camera angle). `self` is framed on her whole body from the front in the real scene (no studio backdrop); `view` is over her shoulder; `photo hd <avatar>` looks from beside her head toward that avatar or object (name match is a substring). Lenses (audit doc): `self` framing 50 mm, scene views about 18 mm (60 degrees vertical).
- Caveats: one render at a time (`look: another look is still rendering`); a 4K render needs several GB free (the memory guard may kill and retry it); rendering never logs in anywhere and only reads asset CDNs (textures are cached: raw JPEG2000 per UUID in `tex-raw/`, decoded sizes in `tex/`, 2 GB cap each, oldest-used evicted; a texture the CDN never answers is skipped for 6 h; see the README's [Texture cache](../README.md#texture-cache), which also lists the caps on the bake and scene-mesher caches); in-world avatars that are still loading appear as grey stand-ins (the output says so).

## Texture vision
```
texture save <uuid>        faces <object name|uuid> [face=<n>] [r=<m>]        vendor look <name filter> [radius]
```
Downloads textures and decodes them to PNG under `/workspace/secondlife/textures/` (a vendor scan adds an `index.json` with names, hover text, faces). Read-only: it never touches or pays. Library and CLI: `textclient/SlTextureVision/` (README there).

## Voice
```
voice on | off | status | tail [n] | wake off|name|all|test
```
**Listen-only** WebRTC voice with local speech-to-text: transcripts go to `/workspace/secondlife/voice/transcript-<date>.md` with speaker names; her microphone is never sent. Off by default and after every login; joined only when the owner asks. `wake` decides whether finished lines wake the chat routine through the webhook (default `name`: only lines that mention her). Setup and tests: [textclient/galatay-voice/README.md](../textclient/galatay-voice/README.md) (needs the voice venv from its `install.sh`).

## Teleports and offers
```
accept | decline          autolure on|off          teleport [force] <region> <x> <y> <z>        home [force]
offers [all]      offers accept|decline <n> [confirm]      friend list|accept|decline|add <name>      group invites | group accept|decline <n>
restart status
```
- Teleport offers are only ever held (or auto-accepted, `autolure`) from the allow-list `GT_LURE_ALLOW` (comma-separated names or UUIDs; set in `text-galatay.sh`). An offer from anyone else is logged as `[lure] ... IGNORED (not allow-listed)`; the standing rule is to ask the owner.
- Inventory offers, friend requests and group invites are never auto-accepted from strangers: `offers` lists them and anything off the allow-list needs `confirm` (the owner's OK). Pending ones survive a restart (`run/pending-offers.json`).
- A region-restart warning makes her stand, go home (or log out if there is no safe fallback) and come back at least 3 minutes after the restart (`restart status`).
- `teleport` and `landmark tp` go through the parcel's landing-point routing; `parcel [x y]` shows how a parcel will treat a teleport.

## Webhook and events
- **Events** (`poll_events` in MCP): a queue of local chat, IMs, teleport and friendship offers, dialogs, alerts, sit and teleport results, `{time, kind, text}` per item, oldest first. Noisy internal kinds (`cmd`, `anim`, `mute`, ...) are not queued, and neither is `[loadurl]`.
- **Chat webhook**: the client wakes an external chat routine by POSTing batches of IMs and chat. The URL is read from `textclient/webhook-url.txt` (`GT_WEBHOOK_URL_FILE`) and the key `GALATAY_WEBHOOK_KEY` from the box secret store; neither is in this repo. Per conversation, a single line is sent after a 4 s window and a burst after 20 s of quiet (max 60 s); there is a daily cap of 600 POSTs (`GT_WEBHOOK_DAILY_CAP`); urgent kinds (teleport/friend offers, group invites, region restarts) and the owner's messages are exempt. Check with `webhook status` / MCP `webhook_status`; probe with `webhook_test`; tune with `webhook debounce`, `webhook cap`. Full behaviour is described in the README.
- **Fast chat** (`fastchat`, `FastChat.cs`) answers conversational nearby chat and IMs (David's and visitors') in a few seconds, without waiting for the chat routine to start (which took about 60 s). It uses an outside AI service: the **xAI chat completions API** (`https://api.x.ai/v1/chat/completions`, model `grok-4.20-0309-non-reasoning` by default, set in `routes/_fast-chat.json`).
  - **What is sent**: the persona and rules from `routes/_fast-chat.json` plus current state, then only the last N lines (default 20) of that one conversation (nearby chat or a single IM session, never mixed) and the new line. So the text of recent chat with that person goes to xAI.
  - **What comes back**: JSON `{reply, action, pace}`. `pace` (quick, normal, slow, thoughtful) sets a human-like typing delay. If `action` is true (a request to do something), the client never acts on the model's word: for David it sends a short acknowledgement and passes the line on to the chat routine, which does the work; for a visitor nothing is sent and the routine handles it. David saying "look into" always goes to the routine. An empty reply, an API error or a timeout (default 8 s) also passes the line to the routine unchanged.
  - **Key**: `XAI_API_KEY` from the environment, else the box secret store card; never logged. Without a key fast chat does nothing and every line goes to the routine.
  - **Cost and checks**: token use per call is appended to `fastchat-usage.jsonl` next to the client log. `fastchat status` shows settings; `fastchat test [--im|--voice] [--from <name>] <line>` makes a real call; the offline `--fastchat-test [--im] <line>` is a dry run that sends nothing. `fastchat` settings include enabled, visitors on/off, max tokens, timeout and history lines.

## Keeping the computer awake
The box (the computer the client runs on) sleeps when idle and is paused by its host, and in-machine activity alone does not stop that. Two silent Grok Bot routines wake it every few minutes so chat, IMs and kisses are not missed. They are not part of this repo; they are saved in the assistant's routine list.
- **Routines**: `Keep-awake ping A` (`*/5 * * * *`, folder `pause-test-ping`) and `Keep-awake ping B` (`2-57/5 * * * *`, folder `pause-test-ping-b`, offset 2 minutes because routines cannot run more often than every 5 minutes). Times are Pacific.
- **What a ping does**: it reads `text-galatay.sh cmd status`. Only when it shows `connected=True` does it log `pingA`/`pingB <time> forks=<n>` to `/workspace/rtest/pause-ping.log` (truncated to its last 500 lines past 200 KB). When logged out it stops at once.
- **Rule (David, Oct 15-16, 2026)**: they run around the clock (David removed the 8 AM to midnight limit on Oct 16). the pings may run whenever Galatea needs them so her work is never interrupted, including while she is logged out (long renders, builds, background jobs). Resume both routines before a long job and pause them when idle; keep them running while she is logged in. A ping still logs only when `connected=True`.
- **`SL keep-awake check`** (:24 and :54): an hourly check that logs into `/workspace/secondlife/keepalive-log.md` and relogs her only if she is logged out by accident. A deliberate `text-galatay.sh stop` (marker set) is respected.

## Kiss detection (Pretty Liars Real Kisser)
The Real Kisser announces each kiss in nearby chat in math-bold letters ("David Nightingale 𝐤𝐢𝐬𝐬𝐞𝐬 Galatea Nightingale's 𝐥𝐢𝐩𝐬."). A kiss from David on her raises the `kissed` event and the automatic response (kiss back and/or a sweet IM, at most one per 30 seconds; the kiss-back itself still has its own 10 min cooldown, within it she answers with the sweet IM/say instead. The line is folded to plain text by hand (`FoldFancyText`): the live client runs with InvariantGlobalization, where `string.Normalize(FormKC)` does nothing, which silently disabled detection until 2026-10-16. Every ignored chat line that mentions "kisses" now logs one `[kiss] kiss line ignored: <reason>; line '...'` line (wrong source type, OwnerSay, not parsable, not about her, not David, within 20 s of the last event).

## Running only the impacted tests (development)

`python3 scripts/impact_tests.py` (repo root) runs just the tests a change can affect, instead of all of them. Default input is the diff against `origin/main` plus uncommitted and untracked files. `--dry-run` prints the selection and the reason chain (`test uses X <- changed member`), `--full` runs everything CI runs, `--commit SHA` / `--range A..B` (with `--dry-run`) analyse past commits. C# is mapped member by member (changed lines -> changed members -> everything that uses them -> `[Fact]`/`[Theory]` methods, run with `dotnet test --filter`); selftest flags narrow `SelftestCliTests`; data/script files map to the code and tests that name them; Python changes run the pytest files that mention them; docs-only runs nothing. A changed client source is always compiled (`dotnet test`, or `build-all.sh` when a selected test uses the built binary or no test covers it). It runs the **full suite** when it is unsure (project/build/CI files, unparsable C#, a changed file nothing references, selection above 60%). `IMPACT_PYTHON` names the interpreter that has pytest. Limits: it cannot see reflection or names built at runtime, so CI keeps running everything on every PR and `main`. Mapper tests: `textclient/tests/test_impact_tests.py`.

## Logs and log pruning
| File (under `/workspace/secondlife/`) | What |
|---|---|
| `textclient.log` | the client's main log, one line per event, `YYYY-MM-DD HH:MM:SS [kind] text` in Pacific time; also the chat and IM history that `chatlog` / `imlog` read |
| `textclient.console.log` (+ `.prev` from the last relaunch) | the client's stdout/stderr |
| `textclient.supervisor.log`, `textclient.autostart.log` | supervisor and boot-hook decisions |
| `anim-events.log` | compact `[anim]` start/stop lines for her avatar (they replay the pose in renders); `GT_ANIM_LOG=1` puts them back into `textclient.log` |
| `last-loadurl.txt` | the latest `llLoadURL` (secret, mode 600) |
| `vision/`, `photos/`, `textures/`, `voice/` | renders and exports, HD photos, texture PNGs, voice transcripts |

`text-galatay.sh log [n]` tails `textclient.log`. **Pruning**: `/home/box/tools/bin/log-prune.sh [--dry-run] [-v]` is a box tool (not in this repo) meant to run hourly or daily while the client is live. It caps live logs by keeping their tails (`textclient.log` is cut back to its last 10 MB once over 16 MB, the cut-off head gzipped into `/home/box/log-archive/`; the console log is copytruncated at 10 MB; others at 5 MB), deletes old Firestorm logs and old build backups (`app-backup-*`, `mcp-app-backups`), never touches `app/`, photos, vision exports or the repo, and records every action in `cleanup-log.md`. Run it with `--dry-run` first.
