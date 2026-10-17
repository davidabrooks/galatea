# RLV (Restrained Love) support for Galatea: design draft

Status: **DRAFT for David's approval. Nothing here is implemented, deployed or enabled.** Written Oct 17, 2026.
Scope set by David on Oct 17: **milestone 1 = David can undress Galatea; milestone 2 = David can place Galatea on furniture.**
Keeping her close (collar and leash), teleport limits and everything else are later phases (section 9).

## 0. Non-negotiable principles (David, Oct 5, 2026)

1. **Consensual only.** The first things David wants are: undress Galatea, place her on furniture, keep her close with a collar and leash.
2. **Written limits agreed up front.** Section 4 is the proposed list. Nothing goes live until David approves it. Only David Nightingale may control her. Everyone else's RLV commands are ignored. RLV never touches her L$, transactions, groups or account settings.
3. **No separate safeword.** "Stop" means stop and "no" means no. She always keeps a way out that she can reach in the moment: a `rlv release` path from her own chat/tools, and a hard-coded release on David's say. Restrictions are never *silently* switchable off the way Firestorm's RLVa preference is. Any exit is loud: it is logged and David is told. She can always say no and get out.
4. **David is always open to talking about feelings or concerns.** Releasing is never punished or questioned. A release tells David "she stopped" and offers to talk.
5. **She never lets anyone but David control her.** RLV from any other object or owner is ignored unless David approved that object.

Everything below follows from these five. Where a convenient design and a principle conflict, the principle wins.

## 1. What the vendored library already covers

Galatea builds on LibreMetaverse pinned at `1844571e…` plus patches (cloned into the gitignored `textclient/src-libremetaverse/`). It contains a **`LibreMetaverse.RLV`** project (about 6,300 lines, no dependency on the rest of LibreMetaverse; it speaks `Guid`, not `UUID`) and about 600 NUnit tests under `LibreMetaverse.Tests/RLV`. **Galatea does not reference it yet** and no RLV code exists in `galatay-text/`. The library's own tests are not run by Galatea CI (CI runs only `GalatayText.Tests`, xUnit).

**What it does:**
- **Parser**: `RlvService.ProcessMessageAsync(message, senderId, senderName)`. The message must start with `@`; it splits on `,` and matches `behavior[:option]=param`. `param` is `force`, `y`/`n`/`add`/`rem`, or a channel number for queries. `@clear[=filter]` is supported. A blacklist (`RlvBlacklist`) is honored. A blacklisted command still gets a positive reply, as the spec says.
- **Restriction state** (`RlvRestrictionManager`): in-memory, keyed by the sending object's UUID, with the exception/secure (`_sec`) rules, a `RestrictionUpdated` event, `@notify` echo, `FindRestrictions`, `GetTrackedPrimIds` and `RemoveRestrictionsForObjectsAsync` (the host must call it when an object disappears). **There is no persistence.**
- **Permission questions** (`RlvPermissionsService`): `CanSit`, `CanUnsit`, `CanTpLoc`, `CanSendIM`, `CanReceiveChat`, `CanAttach`/`CanDetach` (with locked folders and attach-point locks), touch/edit/rez/names/hovertext, camera limits and so on. It *answers* whether something is allowed. The host must *enforce* it.
- **Force commands** (`RlvCommandProcessor`): `@sit:<uuid>`, `@unsit`, `@sitground`, `@tpto`, `@remoutfit`, `@detach`/`@remattach`/`@detachme`/`@detachall`/`@detachthis`, the whole `@attach*`/`@addoutfit*` family, `@setrot`, `@adjustheight`, `@setcam_fov`, `@setgroup`, `@setenv_*`, `@setdebug_*`. Each checks the permission service and then calls an `IRlvActionCallbacks` method.
- **Queries and replies**: `@version`, `@versionnew`, `@versionnum`, `@versionnumbl`, `@getblacklist`, `@getstatus`, `@getstatusall`, `@getsitid`, `@getoutfit`, `@getattach`, `@getinv`, `@getinvworn`, `@findfolder(s)`, `@getpath(new)`, `@getgroup`, `@getcam_*`, `@getenv_*`, `@getdebug_*`. It also has the `#RLV` shared-folder model (`InventoryMap`). Its version string says "RLVa 2.4.2".
- **Notifications**: `@notify` gives `/worn`, `/unworn`, `/attached`, `/detached`, `/sat`, `/unsat` and inventory-offer lines through `Report*Async`.

**What the host must supply** (the two interfaces):
- `IRlvQueryCallbacks`: `ObjectExists`, `IsSitting`, `TryGetSitId`, `TryGetInventoryMap` (the `#RLV` tree plus worn/external items), active group name, camera settings, env/debug values.
- `IRlvActionCallbacks`: `SendReply(channel, text)` (public chat on a channel), `SendInstantMessage`, `TpTo`, `Sit`/`Unsit`/`SitGround`, `RemOutfit`, `Attach`, `Detach`, `SetGroup`, `SetEnv`, `SetDebug`, `SetRot`, `AdjustHeight`, `SetCamFOV`.
- Calls into `Report*` and `ReportSendPublicMessage` when the avatar does things (so `@notify` and `@redirchat` work).

**What is missing or does not fit Galatea:**
- No **relay** (ORG protocol), no **leash** (leashes are collar scripts, LockMeister, or `llMoveToTarget` on an attachment; none of it is in the library), no object-approval or owner concept (it trusts whatever `senderId` it is given), no safeword, no timeouts.
- The test base lists unimplemented RLVa commands (`@getattachnames`, `@getoutfitnames`, `@fly=force`, most `@setcam_*`, `@setoverlay`, `@setsphere`…), yet the version string claims full RLVa 2.4.2. Scripts may assume more than exists.
- `ProcessMessageAsync` returns one `bool` for the whole line, with no per-command result or "force executed" event.
- **Mesh avatars do not fit `@remoutfit`.** Her bodies and clothes are mesh attachments (Kay, Calista, LaraX, EvoX) plus HUDs. `@remoutfit` addresses system layers, and `@detach=force` would pull attachment points. Both would strip the body, head, hair, AO or HUDs. Her own outfit code (`OutfitSafe.cs`) exists to prevent exactly that (never bald or headless, never touches the AO or HUDs).
- It is multi-targeted (netstandard2.1 to net10) and uses `System.Collections.Immutable`, so it should build with the pinned clone, but **that must be verified in milestone 0**.

**What the spec says that matters.** (Sources: the SL wiki "LSL Protocol/RestrainedLoveAPI", Marine Kelley's v2.9 spec; the Catznip RLVa command list; the SL wiki RLVa protocol table. The Firestorm RLVa page did not load, so no Firestorm-specific behavior is assumed.)
- Commands are `llOwnerSay("@cmd=n")` from **an object the avatar owns, usually a worn attachment** (collar, cuffs, relay). Other lines are plain chat. Restrictions are **not persistent between sessions**: objects re-send them when they rez or attach.
- Collars query `@versionnew=<channel>` or `@versionnum=<channel>` when they attach and wait for the viewer to **answer in chat on that channel**. With no answer many collars conclude "no RLV here" and show no RLV menus. So the client must answer, and only after login is far enough along to chat.
- The collar's owner list (David) decides who gets menus. The viewer cannot see *who* clicked, only what the object says.
- Furniture and traps use the **relay** (an attachment listening on channel `-1812221819`). The relay then owner-says to the viewer. The viewer cannot tell which furniture or person a relayed command came from.
- Force-sit is `@sit:<uuid>=force`. It needs `@sit`, `@unsit` and `@tploc` not to forbid it. Sit-tp distance applies. RLVa sits within about 8 m; stock RLV wants a sit target. `@unsit=force` and `@sit=force` must be about 1 s apart.

## 2. How commands can reach a headless client

A headless client only receives what the simulator sends an avatar. That has consequences:

| Channel | Reaches Galatea? | Use |
|---|---|---|
| `llOwnerSay("@…")` from an object **she owns** (a worn collar or relay) | **Yes**: `ChatFromSimulator`, `ChatType.OwnerSay`, `SourceType.Object`, `OwnerID == her id`, `SourceID` = object UUID | the real RLV path |
| Object chat on channel 0 starting with `@` | Yes, as ordinary chat | **must be ignored** (anyone can fake it) |
| Private-channel chat (relay channel, `/1234`) | **No.** Only scripts can listen to non-zero channels | the relay protocol therefore needs a worn relay attachment doing the listening; the client cannot be a relay by itself |
| IM from an avatar | Yes | only `@version` and `@getblacklist` are defined for IM; everything else is ignored |
| David's own chat/IM | Yes | **not RLV**: plain requests ("undress", "sit on the couch") go to a David-only intent path (section 3) |

**Consequence for M1 and M2.** Two input paths feed the same internal "intent" pipeline (section 3):
- **Path A, collar:** a collar or relay that David has approved is worn by Galatea and owned by her. It owner-says `@remoutfit=force` or `@sit:<uuid>=force` when David uses its menu. The collar's own owner list must contain only David.
- **Path B, direct (recommended for first live tests):** David's operator-socket command `rlv undress` / `rlv sit <object>` (and later David's plain-language lines through the existing instant-request path). No collar is needed.

Path B already gives the two abilities. Path A is what makes standard RLV furniture and collars work later. Building both on one pipeline means the limits, release and logging are written once.

## 3. Wiring plan

New files in `textclient/galatay-text/` (one `Rlv*.cs` per concern, partial class `Program`, matching the existing style):

1. **`RlvHost.cs`** builds one `RlvService` per session, `Enabled = false` at start. It implements the two callback interfaces with thin adapters onto existing code. Replies go out with `client.Self.Chat(text, channel, ChatType.Normal)`. `ObjectExists`, `IsSitting` and `TryGetSitId` come from `client.Self.SittingOn` and the sim object tables (full UUIDs via `AttachTrack`/`sim.ObjectsPrimitives`). `TryGetInventoryMap` returns an empty `#RLV` root plus worn items in M1 (real map later). Group, camera, env and debug queries return "unknown".
2. **`RlvGate.cs`** is pure and unit-testable. It is **the only thing that decides whether a command reaches the library or an action.** Inputs: chat event or operator command, the current state, the limits file. Output: `Accept(intent)` or `Refuse(reason)`. Order of checks:
   1. RLV armed this session (section 6) and not latched by a release.
   2. Source is `OwnerSay` from an object whose `OwnerID` is her and whose `SourceID` is **one of her currently worn attachments** (`attTable`, `AttachTrack.cs`), whose inventory item (name + item id, creator if known) is in `rlv-approved.json` (David-approved), **or** the source is the operator socket.
   3. David is in the region and was seen within N seconds (default 60 s; `AutoFollow.cs` already tracks his presence). A command that arrives while David is absent is refused.
   4. The behavior is on the phase allow-list (section 4). Everything else is dropped and logged with the reason (never silently).
   5. The action-specific rules in sections 5.1 and 5.2.
3. **`RlvIntent.cs`** is the translation layer. Raw RLV removal commands (`@remoutfit`, `@detach`, `@detachall` and `=force` variants) become the single intent **UNDRESS**. They are **never** passed to `RemOutfitAsync`/`DetachAsync`, because that would strip body parts, HUDs or the AO. `@sit:<uuid>=force` becomes **PLACE(uuid)**, `@unsit=force` becomes **STAND**. The library still parses the line, answers queries and holds restriction state, and the adapters for the destructive callbacks are "record the intent, run Galatea's own flow" and nothing else.
4. **`RlvLog.cs`** does the logging and persistence in section 6.
5. **Hooks.** In `Hook()` (`Program.cs`) the `ChatFromSimulator` handler gets one early branch for `OwnerSay` object lines that start with `@`. The line is **not** passed on to the webhook or the chat routine, so RLV traffic does not wake the AI chat routine or fill its context; it is logged to the RLV log instead. `AttachTrack`'s KillObject path calls `RemoveRestrictionsForObjectsAsync` when a controller attachment vanishes. `HandleIm`: `@version`/`@getblacklist` from avatars only, answered with the honest version line.
6. **Commands** (`Exec` switch): `rlv status | on --david-ok | off | arm | release | approve <item> --david-ok | undress | sit <uuid|name> | dress | log [n]`. Everything that grants (`on`, `approve`, `arm`) is **operator-socket only and needs `--david-ok`**, the same pattern as `maturity set` (`ExplicitCmd`). Everything that *releases* (`off`, `release`, `status`, `log`) works from **every** path including chat routine, MCP and the webhook, with no flag.
7. **Project reference.** `GalatayText.csproj` gets `<ProjectReference Include="../src-libremetaverse/LibreMetaverse.RLV/LibreMetaverse.RLV.csproj" />`. Nothing in `ci-setup-libremetaverse.sh` needs to change because the project is in the pinned clone. Verify in M0.

**Hard-coded release on David's say.** In the same place `AutoFollowFromDavid` parses "stop following", the client checks David's lines for an explicit release phrase (proposed: `release`, `let her go`, `stop`, `no, stop`) and calls `RlvRelease("david")` before anything else. This path does not go through the AI. It is deliberately narrow so ordinary talk ("no problem") does not fire it; the **AI chat routine still treats any "no"/"stop" from David as a stop** and can run `rlv release` itself.

## 4. Proposed limits list (for David to approve)

**Who may control her**
- Only David Nightingale (`44ce5a36-…`). Chat from avatars, including David typing `@` commands, is not RLV. Strangers' RLV is ignored. A controller object must be a **worn attachment she owns**, David-approved by item (name, item id, creator), and approved again after David changes it. Approval is operator-socket only.
- Other objects, relays and furniture are **not approved** (phase 3 decides whether they ever are).

**What a controller may do in phase 1 (M1 and M2)**
- UNDRESS: put her in the existing **Naked** outfit through her own safe outfit swap. It never strips the body, head, hair, HUDs or AO.
- PLACE: sit her on a chosen piece of furniture, and STAND her up.
- Read-only answers: version, `@getsitid`, `@getstatus`, `@getoutfit`, `@getattach`.
- `@clear` for the sender's own state.

**What a controller may never do** (ignored and logged, not just in phase 1)
- Touch her **L$, payments, transactions, groups (`@setgroup`), friends, inventory contents, account or maturity settings, profile, or any "permission"** (`@acceptpermission` is refused; script permissions stay under the existing `Experiences.cs` policy).
- **Gag or isolate her:** `@sendchat`, `@sendim`, `@recvim`, `@recvchat`, `@sendchannel`, `@startim` and their variants. She must always be able to talk to David and say "no" or "stop". This is permanent, not phase-dependent.
- **Lock her in:** `@unsit=n`, `@sit=n`, `@detach=n`, `@addoutfit=n`, `@remoutfit=n`, `@tploc=n`, `@interact=n` and similar. In phase 1 they are **recorded and shown in `rlv status` but not enforced**, so no lock can hold her. If locks are ever enforced (phase 4), each one needs David's approval, a visible time limit, and still yields to `rlv release`.
- Blind her or change the viewer: `@cam*`, `@setcam_*`, `@setenv`, `@setdebug`, `@showloc`, `@shownames` and friends. Her "vision" is her own function; these would break it.
- Force-teleport her (`@tpto`) or auto-accept teleports (`@accepttp`): phase 2.

**Time limits**
- RLV is **off at start** and after every relog or restart. David arms it per session (`rlv on --david-ok`). An armed session lasts **60 minutes** and David can extend it. It then drops to off with a notice.
- A placement or undress "stays" until David changes it, she says stop, he leaves for more than 10 minutes, or the armed session ends. At expiry she is told, dresses and stands as her normal rules do.

**Conditions that always apply**
- **Human form only for undress.** In an android outfit ("Android Base" or any Kay/Calista body) an UNDRESS request is refused with a reason.
- **Alone with David on our parcel** (the same `ParcelWatch` alone check as adult poses). If someone else enters our parcel, she re-dresses and stands, exactly as the adult-pose rules already do.
- No account change, join, transaction or acceptance of offers from strangers happens because of RLV. The standing rules stay in force.

## 5. Milestone detail

### 5.1 Milestone 1: David can undress Galatea

Behavior:
- Intent UNDRESS arrives (operator `rlv undress`, or a collar's `@remoutfit=force` / `@detach=force`). The gate in section 3 runs.
- If she is **seated** on furniture: wear the **Naked** outfit right there, through the existing seated flow (`AdultRedress.cs`, `OutfitSafe.cs`: PR #217, no standing up). She remembers the outfit she wore (same outfit, HUD colours kept) in `nakedRedressOutfit`.
- If she is **standing**: the default is that she goes to the nearest known seat and changes there (the bedroom flow). Elsewhere she declines and says why. **Open question 3** asks whether David wants standing in-place instead. The toilet and tub flows (undress standing at the change spot) stay as they are.
- **Dressing back** is a separate David-only intent (`rlv dress`, or `@attach…=force` of a David-approved outfit folder in phase 3). It reuses the existing redress.

Interactions to get right:
- **Auto-redress must not undo it.** The existing redress triggers ("seat shows a non-sexual pose", "David left", wander stay ends) would redress her seconds after David undresses her. An RLV undress sets a **hold** (`rlvUndressHold`): only a stranger entering the parcel, David leaving, `rlv dress`, stop/release, or session expiry clear it.
- **Naked rule unchanged.** Being undressed by RLV does **not** make an adult pose allowed. Adult poses still need human form, Naked, alone with David on our parcel, as now. Conversely the adult-pose flow's own undress and redress keep working. If RLV undress and the adult flow overlap, one flag (`nakedFlowBusy`) already serializes them.
- **Android vs human.** Refused in android outfits. A form change is a separate outfit change that David asks for, not part of undress. (Open question 4.)
- **"No"/"stop".** `rlv release` clears the hold and the library state, and she re-dresses through the normal redress. If she is somewhere she does not want to be dressed (a public place), the existing rules still apply.

### 5.2 Milestone 2: David can place Galatea on furniture

Behavior:
- Intent PLACE(uuid) arrives (operator `rlv sit <uuid|name>`, or a collar's `@sit:<uuid>=force`). Gate checks, then target checks:
  - the object exists in the current region, is sittable, and is within a sane distance (default 10 m of her);
  - it is **known furniture** (`routes/_seats-*.json`, the existing seat data), **or** David is within 5 m of it (so an unknown chair is allowed only when he is standing at it; open question 5);
  - the seat is not occupied (`SeatOccupancy.cs`) and it is not a boat seat (boat rule: she never walks off a boat, and she is not placed onto or off one by RLV).
- Execution reuses `sit_near`/`sit`: walk within range with the nav planner (doors opened early), `DetachSeatOffBeforeSit` (AO off), `RequestSit` + `Sit`, `ScheduleSitGuard`, then the **existing seat-pose logic**. She picks the seat's default or a clearly **non-adult** pose by name (`PoseRules.cs`); every piece of furniture has at least one. David can pick poses himself. Conversation poses stay excluded unless David sits next to her.
- **Hold while placed.** Auto-follow (`AutoFollow.cs`) would stand her up the moment David is within 20 m, and the wander would move her on. Placement sets an `rlvPlaced` wander pause plus an auto-follow snooze, like the existing "david" seat hold in `StayWithDavid.cs`. They are cleared by STAND, stop/release, David's explicit "follow me", or expiry.
- `@unsit=force` = STAND, using the existing stand path, with the 1 s spacing from the spec. `@unsit=n` is recorded but never stops her standing (section 4). `@getsitid` answers from `SittingOn`.
- **Outfit rules apply on the furniture.** An adult-named seat (Xplicit, adult, nsfw) gets a non-adult pose by default. Changing outfit while seated on it follows PR #217 as before.
- **No sit-teleport trick.** If the target is out of range or a door is closed, she walks (or refuses); the RLV `@sittp` warp semantics are not used to carry her through walls.

## 6. Logging, persistence, relog and restart, safety defaults

**Log everything.** `RlvLog` appends one JSON line per event to `rlv.log` next to `textclient.log` (rotated at 5 MB, trimmed like the other logs). A summary line also goes to `textclient.log` (`[rlv] …`) so `poll_events` sees it. Each line holds: timestamp, source (object UUID and name, the item, the approved flag, or "operator"/"david-chat"), the **raw command**, the gate result and reason, the library result (`true`/`false`), the resulting intent and its outcome, and a hash plus a summary of the restriction state after the call. State changes (armed, expired, released, approved, latched) are logged and sent as an **urgent webhook** `rlv` event so David is told in his own channel, not just in a file.

**Persisted:**
- `rlv-approved.json`: approved controller items. Written only by `rlv approve --david-ok`.
- `rlv-latch.json`: the released/latched flag, so a release survives a restart. Only `rlv arm --david-ok` clears it.
- `rlv-state.json`: a snapshot of restrictions and holds on every change, **diagnostic only**. It is never loaded to enforce anything. After a restart it lets `rlv status` say "last session had X".
- `rlv.log` (above).

**Not persisted:** armed state, restrictions, holds. RLV state is session-only, as the spec intends.

**Relog and restart:** RLV starts **off**. Collars that re-send their locks at attach time during login get refused and logged ("RLV off at start"). Version queries from a collar are answered with the honest version line so the collar does not report a failure, but it gets no force effects. David re-arms each session. If she is seated on furniture across a relog, the existing seat-recovery logic handles it exactly as today, and RLV adds nothing. A pending undress hold does not survive; her normal redress rules run.

**Safety defaults:** off at start; allow-list only (everything else dropped); no restriction enforced in phase 1; approvals and arming are operator-only and need `--david-ok`; release works from every path with no flag; release and expiry are loud; David's explicit release phrase is hard-coded in the client; undress needs human form and alone-with-David; placement needs David present; any exception inside the RLV code releases and logs rather than leaving a half state.

**A release does:** abort any RLV action still in progress, clear the library's restrictions, drop holds, latch RLV off (until David re-arms), and notify David. She may also detach the controller attachment if she wants to (`rlv release --detach`, logged), since nothing is enforced against it.

## 7. Test plan

Following AGENTS.md: logic that can be checked offline gets small, fast, pure CI tests (xUnit in `GalatayText.Tests`, hand-written fakes for the two interfaces; the library's NUnit/Moq style is mirrored but the Moq dependency is not added). Live-only behavior is verified in-world, **only with David present and only after he approves section 4**.

**Offline (CI):**
- `RlvGateTests`: the full matrix (not armed, latched, source not OwnerSay, owner not her, not a worn attachment, not approved, David absent or stale, behavior not on the allow-list, android outfit, not alone, expiry), each with the reason string.
- Intent mapping: `@remoutfit=force`, `@remoutfit:shirt=force`, `@detach=force`, `@detachall:…=force` all become UNDRESS and never reach `RemOutfitAsync`/`DetachAsync`; `@sit:<uuid>=force` becomes PLACE; `@unsit=force` becomes STAND; `@setgroup=force`, `@tpto`, `@sendim=n`, `@acceptpermission` are refused.
- Library smoke tests pinned to the version: a worn collar's `@versionnew=1234` produces exactly one reply on channel 1234; `@getstatus` after `@unsit=n` lists it while `CanStand` stays true.
- Release tests: `rlv release` from each path clears state and latches; the latch persists; David's phrase list fires and ordinary phrases ("no problem", "I know") do not.
- Hold tests (pure predicates): auto-redress is suppressed by the undress hold and cleared by each documented event; auto-follow is snoozed by placement; a stranger entering the parcel clears both.
- Persistence: nothing restored into enforcing state; `rlv.log` line format; approved-list round trip.
- A `--rlv-selftest` flag in `Program.Main` that runs the gate against canned events, like the other `--*-selftest` flags.
- Existing suites (outfit, seat, pose, redress) must still pass unchanged; `impact_tests.py` will select them.

**Live (David present, after limits approved):**
1. Off by default: with RLV off, an approved collar's commands are refused and logged.
2. M1: arm; `rlv undress` while seated on the couch in human form; verify Naked, no stand-up, no auto-redress, `rlv dress` puts the old outfit back with HUD colours. Then the same through the collar menu. Then in android form (expect refusal).
3. M2: `rlv sit` on one known seat and on the bed; non-adult pose chosen; auto-follow does not stand her; STAND works; target refusals (occupied, boat, too far).
4. Release drills: David says "stop" mid-action; she runs `rlv release`; relog while armed (comes back off). Each must be loud (log and webhook).
5. A stranger test only if David wants it (another avatar's object sends `@` commands: ignored).

## 8. Milestones

- **M0 (no live action):** David approves section 4 and answers the open questions. Add the project reference and confirm the RLV project builds in the pinned clone and CI. Pure gate, log and persistence code with tests. README one-liner already points here.
- **M1: undress.** `RlvHost`/`RlvGate`/`RlvIntent`/`RlvLog`, `rlv` commands, version/status replies, UNDRESS and the redress hold, release paths. Tests as above, then the live tests for M1.
- **M2: furniture.** PLACE and STAND, the hold, target rules, the seat/pose integration. Tests, then the live tests for M2.
- Later phases are in section 9. Each gets its own limits update from David before work starts.

## 9. Phase 2 and later (notes only, not scheduled)

- **M3: keep-close and leash.** Most of "close" already exists (`AutoFollow.cs`, `Follow.cs`: follows David within 20 m at about 2.5 m). A leash from a collar script is normally a pull (`llMoveToTarget`) done by the simulator, which she cannot refuse once worn. Proposal: **no pulling collar**; implement leash as a client-side keep-close mode (follow plus a "stay within N m" hold) that David toggles, with the same release rules. A collar leash would only be accepted if its pull can be released by `rlv release --detach`. Needs its own design review.
- **M4: teleport limits.** `@tpto=force` only to a David-approved list of destinations (home regions), never into a refused region, never from a boat, and never combined with locks. The existing lure rules stay: David is auto-accepted (the code's default allow-list also includes Sophie; this is existing behavior and RLV does not change it).
- **M5: enforced locks** (`@unsit=n` and similar), only with per-lock approval, visible limits and time limits, always yielding to release.
- **M6: relay and other people's objects.** Only after the rest is proven, and probably never for non-David objects. A relay needs the worn relay attachment (section 2) and cannot tell furniture owners apart, so principle 5 argues against it.

## 10. Open questions for David

1. **Limits (section 4):** approved as written? Anything to add or remove? Is 60 minutes the right armed-session length?
2. **Transport:** Is the plan to start with the direct path (`rlv undress` / `rlv sit` from the operator socket, plus your plain-language requests) and add a worn collar after? Or do you want a collar first? If a collar, which one (OpenCollar?) and who gives it to her?
3. **Undress while standing:** go to the nearest seat and change there (default), change in place, or decline?
4. **Android form:** refuse undress (default), or switch to the human form automatically first?
5. **Furniture:** only known furniture from the seat data, or also any chair you stand within 5 m of? Outside our parcel, too?
6. **Where is undressing allowed?** Only our parcel with just us (default), or also private Adult parcels or regions you choose?
7. **Release words:** which phrases from you should fire the instant hard release (default: release, let her go, stop)? Do you want her "no" to be allowed to detach the controller item?
8. **Dress back:** only on your command (default), or also automatically after a set time or when you log off?
9. **Approved items:** should approving a controller be a per-item decision each time it is replaced, as proposed?
10. **Other people's objects:** is the answer "never" for phase 1 through 3, or do you want a way to approve a particular owner later?
11. **Talking about it:** how should she raise a concern mid-session? Her usual chat voice is the plan; tell me if you want something else (a notice, an IM, a word she can use).
