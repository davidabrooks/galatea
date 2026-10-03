# Second Life client options for Galatay (tested 2026-09-25 PT)

Box: Debian 13, 8 vCPU, no GPU (llvmpipe), ~16 GB RAM shared, no root. Every login below was a real login as
Galatay Resident to 'last' (Naberrie / The Buddha Center), with one client at a time, after Firestorm was closed (14:04:56 PT).
Test logs: /workspace/secondlife/clienttests/ and /workspace/secondlife/textclient.log.
Leak check: /workspace/secondlife/scripts/leak-count.py found the password 0 times in every log, script, and source file
(no `$1$` hash either).

## Summary table (measured)

| | Firestorm 7.2.4 | LibreMetaverse: my galatay-text daemon | LibreMetaverse: galatay-mcp (MCP server) | node-metaverse 0.8.21 | Radegast Veles 3.0.0 | LM TestClient | Corrade |
|---|---|---|---|---|---|---|---|
| Real login tested | yes (earlier) | yes, 2x | yes, 3x | yes, 2x | yes, 2x (2026-10-02) | no (see below) | not installed |
| Login time | ~17 s to STATE_STARTED | 4.6 s / 3.2 s | 3.5 / 2.7 / 3.5 s | 2.7-2.8 s (sim 3.3 s) | ~2 s after clicking Login | - | - |
| RAM | ~2.1 GB viewer + ~3.6 GB plugins/CEF | 100-170 MB RSS | 115-160 MB RSS | 100-150 MB RSS | 400-460 MB logged in (5-6 GB with Scene Viewer) | - | - |
| CPU | 260-280 % (~6-7 fps) | ~1 % idle (0.12 s CPU per 10 s) | 3.3-3.6 s CPU per ~55 s session incl. login | 3.7 s CPU per 50 s session | 14-22 % logged in | - | - |
| Walk | GUI | yes (autopilot; `walk 3` and moveto OK; stopped once at a cliff) | yes (walk 2 m OK) | only raw control flags: 2.2 m once, 0 m once; no autopilot API | GUI | moveto/follow cmds | yes |
| Sit by UUID | failing silently via GUI | **yes** (from 23.6 m away) | **yes** | **yes** | GUI | `siton` | yes |
| IM send | yes | yes | yes | yes | yes | yes | yes |
| Receive chat/IM | yes | chat, script dialogs, and alerts logged; incoming IM code path **not yet exercised** (David did not reply during the tests) | same core (events queue) | chat logged | yes | yes | yes |
| Teleport | yes | local TP OK | same core | API present (not run) | yes | `goto` | yes |
| Clean logout | yes | yes (~1 s) | yes | yes in run 2; **run 1 crashed without logout** | - | - | - |

## 1. Firestorm 7.2.4.80712 (current)
- What it is: full 3D viewer and the one David uses. Released 2026-06-01. Installed at /home/box/viewers/Phoenix-Firestorm-...; see notes.md.
- Pros: everything is available (build tools, LSL editor, inventory, map, media), and it is what David sees and knows.
- Cons: ~6 fps on llvmpipe and ~2-5 GB RAM. It can only be driven by screenshots and clicks, which is slow and brittle (the GUI sit has been silently failing). The password is visible in its argv.
- Best use: visual building and editing sessions only.

## 2. LibreMetaverse (C#, github.com/cinderblocks/libremetaverse)
- Maintenance: active. Last commit on master is 2026-09-14 (PR #187). NuGet `LibreMetaverse` 3.1.6. Targets net8/9/10. Uses namespace `LibreMetaverse` (renamed from OpenMetaverse).
- Install (no root): .NET SDK 8.0.425 and 10.0.401 via dotnet-install.sh into ~/.dotnet. The source is at /home/box/viewers/textclient/src-libremetaverse (it needs SDK 10 because restore covers net9/10). Build time is about 1 min.
- TestClient: builds fine (net10). I did **not** log in with it because it only accepts the password via `--pass` (argv) or a plain-text `--file`, and both break the password rules. It has the same library and login code as my clients. Its commands include say/im/siton/stand/follow/goto/moveto/findobjects/uploadscript/createnotecard/import/export/derez.
- **galatay-text** (my daemon): a background process, with commands sent over a Unix socket and events written to a log.
- **galatay-mcp** (Sophie-style MCP server): the same core with the MCP stdio transport (ModelContextProtocol 2.2.0 from 2026-08-13, plus Microsoft.Extensions.Hosting 10). It has 20 tools and a `poll_events` queue. The JSON-RPC test drives it end to end.
- Coverage in the library (APIs verified in source; not all are exposed as tools yet):
  - chat/IM, autopilot, teleport and lures, sit/stand, friends, groups, inventory
  - rez and edit prims: ObjectManager.AddPrim, SetPosition, SetScale, SetRotation, SetTextures, SetName, LinkPrims
  - LSL upload with compile results: InventoryManager.RequestUpdateScriptAgentInventoryAsync / RequestUpdateScriptTaskAsync, CopyScriptToTask
  - "Seeing" is data only (positions, names, owners); there is no rendering.
- Gotcha found and fixed: SL's default interest list is frustum-based. Objects behind the camera (David's pillow and the free pillow) were missing until I switched to `InterestListMode.Panoramic360` after login. After that, all 7 pillows were listed with their occupants.
- Other fixes: globalization-invariant mode crashed `Utils` (the library creates the en-US culture), so I set PredefinedCulturesOnly=false. The Firestorm LSL Bridge `bridgeAuth` token is masked in logs.
- Policy/login: it sends channel "GalatayText" and version 0.1.0.0, and SL accepted that (no update prompt, no error). MFA is supported (MfaEnabled=true); none was requested.

## 3. node-metaverse (npm @caspertech/node-metaverse)
- Maintenance: 0.8.21, last published 2026-07-07. Installed under /home/box/viewers/textclient/nm. npm needed `--legacy-peer-deps` (npm 9 crashed with "edgesOut").
- Pros: fastest login (2.7 s) and small footprint. It has rezPrims/buildObjectNew, inventory, a group API, and `setInterestList('360')`.
- Cons (measured):
  - It throws uncaught `TypeError: Cannot read properties of null (reading 'get')` in ObjectStoreLite.js (GLTF material-override textureTransforms) at Naberrie. Its winston logger has `handleExceptions` and exits the process, so **run 1 died without logging out**. Run 2 needed `winston.exitOnError=false` plus my own handler.
  - It has no walk/autopilot API (raw control flags only; unreliable in the test).
  - It sends the fixed channel "libnmv".
- Verdict: usable, but less robust and less complete than LibreMetaverse.

## 4. Radegast
- Legacy 2.x (tags up to v2.53) is WinForms and **Windows only**.
- **Radegast Veles 3.0.0** (Avalonia, net10) is the cross-platform rewrite. Source only. Rebuilt 2026-10-02 PT at upstream d1398eb (PR #201: login and crash fixes) in ~27 s at /home/box/viewers/textclient/radegast-src.
- SkiaSharp fix: the stock linux-x64 libSkiaSharp.so crashes ("Default font family name can't be null"). The fontconfig-enabled 4.151.1 build in radegast-src/local-fixes/ replaces it. A rebuild overwrites it; `textclient/run-radegast.sh` re-applies it.
- **Real login tested 2026-10-02 17:36 and 17:48 PT** (GUI, password typed into the masked field, Start Location = Last Location, "Remember credentials" off). Login took ~2 s to region.
- Resources: ~250 MB at the login screen; 400-460 MB RSS and 14-22 % CPU logged in (2D UI only). Opening the Scene Viewer pushed it to 5-6 GB RSS and ~300 % CPU while still rendering black (llvmpipe). Firestorm was 2.5 GB and ~440 % at the same spot.
- Works:
  - chat/IM receive with logs (`~/.config/RadegastVeles/Galatay Resident/*.txt`), toasts, profile view
  - group list, group chat session with participants, group notice archive
  - friends list with rights, offer/request teleport buttons (not used)
  - inventory tree, search, Received Items, rename (context menu > Rename, F2)
  - Create > New Landmark (made "BC Sky Platform", Naberrie 118,137,253)
  - landmark Teleport (works; inside the BC parcel SL reroutes to the landing point, 107,150,53)
  - Objects list with radius and search, Touch / Sit On / Walk To (sit-teleporters work)
  - Appearance tab: worn list, per-item detach (X); wear by double-click in inventory; hover height; rebake
  - minimap and status bar (region/pos/parcel/L$)
- Doesn't work / missing:
  - HUD Viewer and Scene Viewer need Vulkan GPU interop ("Compositor doesn't support GPU interop"). No HUD button clicks (Objects > Touch only hits a root prim) and no in-world snapshots.
  - no shape/appearance slider editor (the wearable panel shows only textures)
  - no sound (fmod not shipped)
  - inventory search-result right-click menu acts on the previously selected node (use double-click or the tree)
- Gotchas:
  - Closing the main window (title X / Alt+F4 / File > Hide Window) hides it to a tray icon. There's no tray host here, so the window can't be restored and the only way out is killing the process (no logout). Always use File > Logout, then Exit.
  - It logs the Firestorm LSL bridge owner-say, including bridgeAuth, unmasked in chat.txt.
  - Not scriptable from a shell: everything is clicks on an X display.
- Launcher: `textclient/run-radegast.sh` (deployed as /home/box/viewers/run-radegast.sh; needs DISPLAY).

## 5. Corrade (grimore.org, Wizardry and Steamworks)
- A mature, closed-source freeware bot. It is controlled from LSL/HTTP/MQTT with a group+password scheme and has a Docker image. Its releases are "infrequent" per its page (API pages last modified 2025-10).
- **Not installed:** the login password lives in its plain-text configuration (against our rules), and it is heavyweight for our needs. Everything it offers is available through LibreMetaverse directly.

## Deviations during testing
- node-metaverse sent David **two** IMs, one per run, because run 1 crashed and had to be repeated. All other clients sent exactly one.
- The gap before node-metaverse run 1 was ~46 s instead of ~60 s. The other gaps were 56-80 s.
- Nothing was said in local chat. No offers came in, and none were accepted.

## Recommendation
Use a hybrid:
- **galatay-mcp** (LibreMetaverse + MCP) for everyday presence: chat, IM, moving, sitting, following, teleports, event polling. It is about 150 MB and ~1 % CPU, versus Firestorm's 2-5 GB and ~270 %.
- **galatay-text** is the same core as a shell-driven daemon. Use it when MCP isn't mounted.
- **Firestorm** when building or editing visually, or when something must be seen. Only one of them can be logged in at a time: every launcher refuses if Firestorm or another Galatay client is running.
- Next steps for the MCP server: build/script tools (rez prim, set pos/scale/texture, upload and compile LSL into a prim) on top of the library APIs listed above.
