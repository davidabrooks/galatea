# RLV (Restrained Love) support for Galatea: design draft

Status: **DRAFT for David's approval. Nothing here is implemented, deployed or enabled.** Written Oct 17, 2026.
Scope set by David on Oct 17: **milestone 1 = David can undress Galatea; milestone 2 = David can place Galatea on furniture.** He controls both with **his own HUD, with no typed commands and nothing for Galatea to wear** (no collar). Section 2 checks what the protocol allows. Keeping her close (collar and leash), teleport limits and everything else are later phases (section 9); the Peanut (Wendy Starfall) collar is researched there as background only.

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

## 2. How David's HUD commands can reach a headless client (the transport problem)

**Goal David set:** the listener lives in the client's own architecture, and Galatea wears nothing. This section checks honestly whether the protocol allows that. **Partly: yes for some delivery paths, no for others.**

### 2.1 What the grid actually sends to a viewer

Checked against the vendored LibreMetaverse packet handlers (`AgentManager.PacketHandlers.cs`) and the RLV spec:

- **`ChatFromSimulator`**: chat the avatar can *hear*. Types: whisper, normal, shout, start/stop typing, debug, **owner-say (8)** and **region-say-to (9)**. Each carries `SourceType` (agent, object, system), the source id and, for objects, the **owner id**. `llRegionSayTo(avatar, 0, text)` arrives as type 9 (the library enum has `ChatType.RegionSayTo = 9`). Galatea's `Hook()` already receives every type except typing and debug.
- **Instant messages**: from avatars (sender's agent id) and from objects (`MessageFromObject`, today only logged as `objim`).
- **Script dialogs**: `llDialog` / `llTextBox` from an object arrive in the `ScriptDialog` event (message text, channel, buttons, object id, **owner id**). Galatea already answers dialogs with `ReplyToScriptDialog`, which speaks the chosen button on the dialog's private channel, so the sender's script *can* hear a reply.
- Also: script permission questions, load-URL, alerts, offers.

**What a viewer never receives:** chat on any non-zero channel (`llSay`, `llShout`, `llRegionSay`, `llRegionSayTo` on a private channel). Only scripts can listen there. A client cannot "listen to a channel" because the simulator never sends it.

**What Firestorm/RLVa does:** nothing special. It acts on `@…` lines that arrive as **owner-say from an object the user owns** (usually one they wear). The RLV *relay* and every RLV collar are scripts on the avatar whose only job is to turn private-channel traffic into `llOwnerSay("@…")`. So even Firestorm cannot receive a HUD's private-channel RLV commands without a script of the avatar's own.

**Who is the sender?** Two fields are set by the simulator and cannot be forged by the sender: the **object owner key** in chat and dialogs, and the **agent id** of an avatar IM. Authentication therefore rests on "owned by David Nightingale (`44ce5a36-…`)", never on text in the message.

### 2.2 What David's HUD is (depends on David)

David has not named the HUD yet. The Wendy Starfall "Virtual Disgrace Remote" (Peanut line), which he mentioned, is a **collar controller**. From its published source: it registers a partner by sending `<partner uuid>:ping` on a private channel derived from the partner's UUID and waits for `…:pong` from a collar, and every button then sends `<partner uuid>:<collar command>` with `llRegionSayTo` on that private channel. It has no mode for an avatar without a collar, and nothing in a client can hear that channel. **The stock Remote therefore cannot control Galatea without a collar or another script of hers.**

### 2.3 Options compared

| # | Transport | Worn/owned by her | Enters her client as | Authenticated by | Notes |
|---|---|---|---|---|---|
| T1 | **HUD speaks to her on channel 0**: `llRegionSayTo(<her key>, 0, "…")` (private to her, region-wide) | **nothing** | `ChatFromSimulator`, type 9, `SourceType.Object`, `OwnerID == David` | owner key | Simplest. Needs a HUD script that can emit a channel-0 line: David's own script, or a small replacement HUD. A closed product that only uses private channels cannot be re-pointed. `llSay(0, …)` also works (20 m, but everyone nearby reads it), so prefer `RegionSayTo`. **Confirm live** that type 9 arrives. |
| T8 | **HUD pops an `llDialog` at her**, command text in the dialog message and a button | **nothing** | `ScriptDialog` (message, channel, owner id) | owner id in the dialog | Also built-in. Adds a two-way channel: her reply goes back on the dialog channel, so the HUD can get an "ok"/"refused" without any chat. Costs: the HUD must `llDialog` (not a normal chat line), dialogs queue and expire, and there is a 511-character limit. Reasonable alternative to T1 if David wants acknowledgements in the HUD. |
| T2 | **HUD sends an IM** (`llInstantMessage`) | nothing | `HandleIm`, `MessageFromObject` | agent id of the IM | Works across regions. **Uncertain whether the IM's from-id is the owner or the object**; test before relying on it. No advantage over T1. |
| T4a | **A small receiver worn by her** on a HUD slot (invisible; listens on a private channel for messages from David-owned objects and forwards a fixed line with `llOwnerSay`) | **one worn attachment** | owner-say from the receiver | owner key checked in the receiver, plus the client checks it is her approved worn item | **The least-intrusive option if the HUD only speaks a private channel.** The receiver must understand that HUD's messages, so it depends on the HUD's protocol. |
| T4b | The same receiver **rezzed at home, not worn** (owned by her) | an owned object on the parcel | owner-say from the receiver | same as T4a | Nothing worn, but it hears only in range (or region-wide if the HUD uses `llRegionSay`) and only where it is rezzed. Fine for the home parcel, useless when she travels. Rez rights and giving her the item need David's OK. |
| T3 | **HUD speaks the RLV relay protocol** (channel `-1812221819`, lines like `<name>,<her uuid>,@sit:<uuid>=force`) and **she wears a relay** | a relay attachment | owner-say `@…` from the relay | the relay's own trust list | The client cannot hear the relay channel, so a worn relay is mandatory. The viewer then sees plain `@…` lines and **cannot tell which HUD or person sent them**; safety rests on the relay being set to David only. Uses the library. |
| T5 | **Peanut Remote + Peanut collar** | the collar | owner-say `@…` from the collar | the collar's owner list | Works with the stock Remote. A collar: later phase (section 9). |
| T6 | HUD calls an HTTPS endpoint (`llHTTPRequest`; the grid adds `X-SecondLife-Owner-Key`) | nothing | a new public web endpoint on the box | owner header plus shared secret | Needs an inbound public endpoint (new attack surface, secrets). Not recommended. |
| T7 | David types a chat line or IM, or uses the operator socket | nothing | agent chat / `HandleIm` / socket | agent id | Already possible. David does not want to type, so it is only the fallback and the test path (`rlv undress`, `rlv sit …`). |

### 2.4 Recommendation

1. **Milestones 1 and 2: a built-in listener for T1 (and T8 if David wants acknowledgements), with T7 always available.** The client accepts a line only when it is **object chat (or a dialog) whose owner id is David** and it matches an exact grammar (for example `gt undress`, `gt dress`, `gt sit <object uuid>`, `gt stand`, `gt release`). **Nothing is worn or owned by her**, no collar, no relay, no new trust anchor beyond David's own account. This is the closest design to "nothing worn" that the protocol allows. It works only if his HUD can emit one of those delivery types.
2. **If the HUD cannot** (it speaks only private channels, as the Peanut Remote and most RLV controllers do): the protocol does not allow a client-only listener. The least-intrusive fix is **T4a**, a tiny invisible receiver on a HUD slot. **T4b** (rezzed at home, not worn) is possible for the home parcel only. **T3** (a standard relay) is the RLV-standard route when the HUD already speaks relay protocol. The gate, intents, holds, release, logging and tests are identical in every case; only the entry file changes.
3. **Not T6, T2 or T5 for the first milestones.**
4. **Do not promise more than this.** Whether David's HUD can use T1/T8 is unknown until we know its name, version and whether its script can be edited. It is an open question (section 10), and the transport is settled only after one live test with that HUD.

### 2.5 What depends on the HUD (David to provide)
- Name, version, creator, and whether David can edit its scripts (T1 is then a one-line change).
- What it speaks: channel-0 chat, an IM, a dialog, the relay protocol, or a private-channel protocol (then the exact channel formula and message strings, so a receiver can be matched).
- Which buttons exist (undress? strip by layer? outfit? force-sit with an object list?) so the grammar covers them.
- Whether its undress is a full strip (`@remoutfit=force` / `@detach=force`) or per layer. The client must still translate any of them into the single safe UNDRESS intent (section 3).

## 3. Wiring plan

New files in `textclient/galatay-text/` (one `Rlv*.cs` per concern, partial class `Program`, matching the existing style). Milestones 1 and 2 need **no library and no `@` parsing** when the transport is T1 or T7. The library arrives with T3/T5 (and with T4 if the receiver forwards `@` lines).

1. **`RlvEntry.cs`** is the only place that knows about transports. It turns an incoming event into a plain `RlvRequest(source, verb, args)` or drops it:
   - T1: in `Hook()`'s `ChatFromSimulator` handler, one early branch for `SourceType.Object` lines (types normal, owner-say and region-say-to) with `OwnerID == DavidId` that match the grammar (`gt <verb> …`). Other object chat is untouched. The matched line is **not** passed to the webhook or the chat routine, so HUD traffic does not wake the AI chat routine.
   - T8 (if chosen): a branch in the existing `ScriptDialog` hook for dialogs whose owner id is David and whose message matches the grammar. The client answers with `ReplyToScriptDialog` (an "ok" or "refused" button label), which the HUD script can hear.
   - T7: operator-socket `rlv …` commands (and later David's own IM/chat through the existing instant-request path).
   - T3/T4a/T4b/T5 (only if the HUD cannot use T1/T8): owner-say lines from an approved receiver, relay or collar she owns, through the library or a fixed grammar.
2. **`RlvGate.cs`** is pure and unit-testable and is **the only thing that decides whether a request becomes an action.** Checks, in order:
   1. RLV armed this session (section 6) and not latched by a release.
   2. Source authenticated: object chat or dialog with owner id David (T1/T8), operator socket (T7), or an approved receiver, relay or collar she owns (T3/T4/T5).
   3. David is in the region and was seen within N seconds (default 60 s; `AutoFollow.cs` already tracks his presence). A command that arrives while David is absent is refused.
   4. The verb is on the phase allow-list (section 4). Everything else is dropped and logged with the reason.
   5. The action-specific rules in sections 5.1 and 5.2.
3. **`RlvIntent.cs`** maps requests to the three intents: **UNDRESS**, **PLACE(uuid)**, **STAND**, plus `dress` and `release`. When a library transport is added, raw `@remoutfit`/`@detach`/`@detachall` `=force` become UNDRESS and are **never** passed to `RemOutfitAsync`/`DetachAsync`, because that would strip her mesh body, head, HUDs or AO. `@sit:<uuid>=force` becomes PLACE and `@unsit=force` becomes STAND.
4. **`RlvLog.cs`** does the logging and persistence in section 6.
5. **Commands** (`Exec` switch): `rlv status | on --david-ok | off | arm | release | undress | sit <uuid|name> | stand | dress | log [n]`, plus `rlv approve <item> --david-ok` once a worn object is involved. Everything that grants (`on`, `arm`, `approve`) is **operator-socket only and needs `--david-ok`** (the `ExplicitCmd` pattern of `maturity set`). Everything that *releases* (`off`, `release`, `status`, `log`) works from **every** path (chat routine, MCP, webhook) with no flag. The HUD's own `gt release` also works.
6. **Hard-coded release on David's say.** Where `AutoFollowFromDavid` parses "stop following", the client checks David's own lines for an explicit release phrase (proposed: `release`, `let her go`, `stop`, `no, stop`) and calls `RlvRelease("david")` before the AI is involved. The phrase list is narrow so ordinary talk ("no problem") does not fire it; the **AI chat routine still treats any "no"/"stop" from David as a stop** and can run `rlv release` itself.
7. **Later (library transports only):** `RlvHost.cs` builds one `RlvService` per session (`Enabled = false` at start), implements `IRlvQueryCallbacks`/`IRlvActionCallbacks` as thin adapters onto existing code (`client.Self.Chat(text, channel, ChatType.Normal)` for replies; sit and object state from `SittingOn` and the sim tables; an empty or virtual `#RLV` inventory map), and `GalatayText.csproj` gets `<ProjectReference Include="../src-libremetaverse/LibreMetaverse.RLV/LibreMetaverse.RLV.csproj" />`. `AttachTrack`'s KillObject path then calls `RemoveRestrictionsForObjectsAsync` when a controller attachment vanishes.

## 4. Proposed limits list (for David to approve)

**Who may control her**
- Only David Nightingale (`44ce5a36-…`). A command counts only if it arrives as **object chat or a dialog whose owner id is David** (his HUD), as an operator-socket command, or (if a receiver is ever needed) from a David-approved receiver she owns. Any other source is ignored and logged, including text that merely *says* it is from David.
- Chat from other avatars, other people's objects, relays and furniture are **not approved** (a later phase decides whether any ever are). If a receiver, relay or collar is added later, it must be approved by item (name, item id, creator), and approved again after David changes it. Approval is operator-socket only.
- David's HUD is identified by name and creator once he provides them; until then the gate trusts the owner id only, and `rlv status` shows which objects have been sending.

**What a controller may do in phase 1 (M1 and M2)**
- UNDRESS: put her in the existing **Naked** outfit through her own safe outfit swap. It never strips the body, head, hair, HUDs or AO.
- PLACE: sit her on a chosen piece of furniture, and STAND her up.
- Read-only answers to the HUD (an "ok" or "refused: reason" dialog reply, or a private line to David).
- Only when an RLV-speaking transport is used later: `@version*`, `@getsitid`, `@getstatus`, `@getoutfit`, `@getattach` answers and `@clear` for the sender's own state.

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
- Intent UNDRESS arrives (David's HUD line `gt undress` or dialog; operator `rlv undress`; later, a relay's or collar's `@remoutfit=force` / `@detach=force`). The gate in section 3 runs.
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
- Intent PLACE(uuid) arrives (HUD `gt sit <uuid>`; operator `rlv sit <uuid|name>`; later, `@sit:<uuid>=force`). Gate checks, then target checks:
  - the object exists in the current region, is sittable, and is within a sane distance (default 10 m of her);
  - it is **known furniture** (`routes/_seats-*.json`, the existing seat data), **or** David is within 5 m of it (so an unknown chair is allowed only when he is standing at it; open question 5);
  - the seat is not occupied (`SeatOccupancy.cs`) and it is not a boat seat (boat rule: she never walks off a boat, and she is not placed onto or off one by RLV).
- Execution reuses `sit_near`/`sit`: walk within range with the nav planner (doors opened early), `DetachSeatOffBeforeSit` (AO off), `RequestSit` + `Sit`, `ScheduleSitGuard`, then the **existing seat-pose logic**. She picks the seat's default or a clearly **non-adult** pose by name (`PoseRules.cs`); every piece of furniture has at least one. David can pick poses himself. Conversation poses stay excluded unless David sits next to her.
- **Hold while placed.** Auto-follow (`AutoFollow.cs`) would stand her up the moment David is within 20 m, and the wander would move her on. Placement sets an `rlvPlaced` wander pause plus an auto-follow snooze, like the existing "david" seat hold in `StayWithDavid.cs`. They are cleared by STAND, stop/release, David's explicit "follow me", or expiry.
- `@unsit=force` = STAND, using the existing stand path, with the 1 s spacing from the spec. `@unsit=n` is recorded but never stops her standing (section 4). `@getsitid` answers from `SittingOn`.
- **Outfit rules apply on the furniture.** An adult-named seat (Xplicit, adult, nsfw) gets a non-adult pose by default. Changing outfit while seated on it follows PR #217 as before.
- **No sit-teleport trick.** If the target is out of range or a door is closed, she walks (or refuses); the RLV `@sittp` warp semantics are not used to carry her through walls.

## 6. Logging, persistence, relog and restart, safety defaults

**Log everything.** `RlvLog` appends one JSON line per event to `rlv.log` next to `textclient.log` (rotated at 5 MB, trimmed like the other logs). A summary line also goes to `textclient.log` (`[rlv] …`) so `poll_events` sees it. Each line holds: timestamp, source (object UUID, name and owner id, or dialog; the approved flag; or "operator"/"david-chat"), the **raw command**, the gate result and reason, the library result (`true`/`false`, when the library is in use), the resulting intent and its outcome, and a hash plus a summary of the restriction state after the call. State changes (armed, expired, released, approved, latched) are logged and sent as an **urgent webhook** `rlv` event so David is told in his own channel, not just in a file.

**Persisted:**
- `rlv-approved.json`: approved controller items (HUD name/creator, and any later receiver). Written only by `rlv approve --david-ok`.
- `rlv-latch.json`: the released/latched flag, so a release survives a restart. Only `rlv arm --david-ok` clears it.
- `rlv-state.json`: a snapshot of restrictions and holds on every change, **diagnostic only**. It is never loaded to enforce anything. After a restart it lets `rlv status` say "last session had X".
- `rlv.log` (above).

**Not persisted:** armed state, restrictions, holds. RLV state is session-only, as the spec intends.

**Relog and restart:** RLV starts **off**: HUD lines that arrive while it is off are refused with a short "RLV is off" reply and logged. (If a receiver, relay or collar is added later, its login-time commands are refused and logged the same way, and its version query is still answered honestly so it does not report a failure.) David re-arms each session. If she is seated on furniture across a relog, the existing seat-recovery logic handles it exactly as today, and RLV adds nothing. A pending undress hold does not survive; her normal redress rules run.

**Safety defaults:** off at start; allow-list only (everything else dropped); no restriction enforced in phase 1; approvals and arming are operator-only and need `--david-ok`; release works from every path with no flag; release and expiry are loud; David's explicit release phrase is hard-coded in the client; undress needs human form and alone-with-David; placement needs David present; any exception inside the RLV code releases and logs rather than leaving a half state.

**A release does:** abort any RLV action still in progress, clear any recorded restrictions, drop holds, latch RLV off (until David re-arms), and notify David. If a receiver, relay or collar is worn, she may also detach it (`rlv release --detach`, logged), since nothing is enforced against it. With David's HUD alone there is nothing to detach.

## 7. Test plan

Following AGENTS.md: logic that can be checked offline gets small, fast, pure CI tests (xUnit in `GalatayText.Tests`, hand-written fakes; if the library is adopted later, its NUnit/Moq style is mirrored with hand-written fakes for its two interfaces and no Moq dependency). Live-only behavior is verified in-world, **only with David present and only after he approves section 4**.

**Offline (CI):**
- `RlvGateTests`: the full matrix (not armed, latched, source is an avatar or a stranger's object, object owner id not David, line is a forged "I am David" text, David absent or stale, verb not on the allow-list, android outfit, not alone, expiry), each with the reason string. Same matrix for the dialog path (T8).
- `RlvGrammarTests`: exact `gt …` verbs parse; near-misses, extra text, oversized lines and malformed uuids are dropped.
- Intent mapping (only once an `@`-speaking transport exists): `@remoutfit=force`, `@remoutfit:shirt=force`, `@detach=force`, `@detachall:…=force` all become UNDRESS and never reach `RemOutfitAsync`/`DetachAsync`; `@sit:<uuid>=force` becomes PLACE; `@unsit=force` becomes STAND; `@setgroup=force`, `@tpto`, `@sendim=n`, `@acceptpermission` are refused.
- Library smoke tests pinned to the version (only once the library is adopted): a worn collar's `@versionnew=1234` produces exactly one reply on channel 1234; `@getstatus` after `@unsit=n` lists it while `CanStand` stays true.
- Release tests: `rlv release` from each path clears state and latches; the latch persists; David's phrase list fires and ordinary phrases ("no problem", "I know") do not.
- Hold tests (pure predicates): auto-redress is suppressed by the undress hold and cleared by each documented event; auto-follow is snoozed by placement; a stranger entering the parcel clears both.
- Persistence: nothing restored into enforcing state; `rlv.log` line format; approved-list round trip.
- A `--rlv-selftest` flag in `Program.Main` that runs the gate against canned events, like the other `--*-selftest` flags.
- Existing suites (outfit, seat, pose, redress) must still pass unchanged; `impact_tests.py` will select them.

**Live (David present, after limits approved):**
1. **Probe:** with the client in log-only mode, David presses each HUD button; confirm which delivery type arrives (chat type 9, owner-say, dialog, IM, or nothing) and what `OwnerID` it carries. This settles the transport.
2. Off by default: with RLV off, a HUD button is refused and logged.
3. M1: arm; HUD undress while seated on the couch in human form; verify Naked, no stand-up, no auto-redress, `rlv dress` puts the old outfit back with HUD colours. Then in android form (expect refusal).
4. M2: HUD place on one known seat and on the bed; non-adult pose chosen; auto-follow does not stand her; STAND works; target refusals (occupied, boat, too far).
5. Release drills: David says "stop" mid-action; she runs `rlv release`; relog while armed (comes back off). Each must be loud (log and webhook).
6. A stranger test only if David wants it (another avatar's object says `gt undress`: ignored).

## 8. Milestones

- **M0 (no live action):** David approves section 4, names the HUD, and answers the open questions. Pure gate, grammar, log and persistence code with tests. Decide the transport (section 2.4) after one **read-only live probe** with his HUD: log what the client receives when he presses a button (no action taken).
- **M1: undress.** `RlvEntry` (T1, plus T8 if wanted) and T7, `RlvGate`, `RlvIntent`, `RlvLog`, `rlv` commands, UNDRESS and the redress hold, release paths. **Collar-free and nothing worn.** Tests as above, then the live tests for M1.
- **M2: furniture.** PLACE and STAND, the hold, target rules, the seat/pose integration. Tests, then the live tests for M2.
- **M3+: leash and keep-close, then the others** (section 9). The Peanut collar viewer side comes in with the leash work, if David still wants a collar then. Each later phase gets its own limits update from David before work starts.
- **Only if the HUD cannot use T1/T8:** a receiver (T4a, or T3/T5) is added **as part of M1**, because without it the HUD cannot reach her at all. The rest of M1 is unchanged.

## 9. Later phases (notes only, not scheduled)

- **M3: keep-close and leash.** Most of "close" already exists (`AutoFollow.cs`, `Follow.cs`: follows David within 20 m at about 2.5 m). A collar leash is normally a pull (`llTarget` plus `llMoveToTarget`) done by the simulator, which she cannot refuse once worn (Peanut's leash script does exactly this, plus `fly=n,tplm=n,tplure=n,tploc=n` restrictions). Proposal: implement leash as a client-side keep-close mode (follow plus a "stay within N m" hold) that David toggles from his HUD, with the same release rules, rather than a pulling collar. Needs its own design review.
- **M4: teleport limits.** `@tpto=force` or an equivalent HUD verb only to a David-approved list of destinations (home regions), never into a refused region, never from a boat, never combined with locks. The existing teleport-offer rules stay as they are; RLV does not change them.
- **M5: enforced locks** (`@unsit=n` and similar), only with per-lock approval, visible limits and time limits, always yielding to release.
- **M6: relay and other people's objects.** Only after the rest is proven, and probably never for non-David objects: a relay cannot tell furniture owners apart, which principle 5 argues against.

### 9.1 Background: the Peanut collar (Wendy Starfall's OpenCollar fork), for a later phase

Read from the public source (`VirtualDisgrace/opencollar`, "OpenCollar Six", Wendy's Peanut line; the repo's last commit is Apr 2022). **It is not certain that this repository is the exact "v6.7+ Peanut" build David means**; the source's help text links to `opencollar.at`. David should confirm the version from his collar's menu. Everything below is what the source shows; behavior in David's build must be checked live. Source is GPL-2.0: it was read to understand behavior only and nothing is copied.

**Authentication and who can act.** Access levels: owner, trusted, group, public ("open access"), wearer, blocked. The collar decides by the **owner key of whatever sent the command**, so a HUD or object owned by an owner counts as that owner. Two hazards for Galatea: (1) **with no owner set, the wearer is treated as owner**; (2) the wearer can add **Vanilla** (self-owned) and owners can enable **public** or **group** access or capture ("temp owner"); each of these would let someone other than David act. Owner/trust/block lists hold up to 60 entries; `<prefix> list` prints them to the requester.

**How a controller reaches it.** The collar listens on public channel 0 and a private channel (default 1) for chat from the wearer and from owners (prefix: the first two letters of the wearer's name, lower case, e.g. `/1ga menu`). The Remote sends `<wearer uuid>:<command text>` with `llRegionSayTo` on the private channel `-abs(hex(last 7 digits of the wearer's UUID))`, region-wide. The collar strips the 37-character prefix and treats the rest exactly like a typed command, authorised by the sender's owner key. It answers a `ping` with `<wearer uuid>:pong`, which is how the Remote lists collars it may use.

**How menu actions become RLV.** The collar's RLV script sends commands to the viewer with `llOwnerSay("@…")`. From the source:
- On attach it sends `@versionnew=293847` and listens on channel 293847 **for a reply from the wearer's avatar** (so the client must *speak* the version line on that channel). It retries, with a 30 s timeout, **three times**; if there is no answer RLV is off until the next login. The reply is parsed as `RestrainedLove viewer vX.Y.Z (RLVa A.B.C)`; the library's string has that shape.
- The collar re-sends its restrictions after teleports and region changes, and when locked it sends `@detach=n` (the collar cannot be removed).
- **Outfits menu:** `@remoutfit=force,detach=force`, a 1.5 s pause, then `@attachallover:<folder>=force,attachallover:<prefix>/.core/=force`. It first strips everything and then wears a `#RLV` folder plus a `.core` folder (body parts). **Taken literally this would strip her mesh body, head, HUDs and AO.** The client must treat the strip as "outfit change pending", not execute it, and map the following `attach…` to one of her saved outfits (a virtual `#RLV` tree whose folders are her outfit names).
- **Folders menu:** `@attach/attachover/attachall…:<path>=force`, `@detach:<path>=force`, `@detachall:<path>=force`, `@detachthis…`, plus `@getinv`, `@findfolder(s)`, `@getoutfit`, `@getattach` queries answered on private channels, and lock/unlock of folders.
- **Force sit:** `@sit:<uuid>=force`; stand is `@unsit=y,unsit=force` (the two are sent as one line, which the spec warns can race).
- **Restrictions menu** (owner-controlled; each is a lock David can set): `sendim`, `recvim`, `sendchat`, `recvchat`, `touchall`, `tplm/tploc/tplure/sittp`, `unsit`, `showinv…`, `addoutfit/remoutfit/addattach/remattach`, blur, `shownames…`. These are the gag-and-lock family section 4 forbids.
- A **Terminal** button (a text box) lets an authorised user send arbitrary RLV lines, one per line. I did not find a separate per-layer undress script in the collar folder; the relay in `spares/` has a "smart strip" option that rewrites `@remoutfit` into `@detachallthis`. Whether David's build has either is unknown.

**The collar's own exits** (relevant to principle 3): a **safeword** (the built-in `RED`, and a word the wearer can change; said in chat; it clears RLV restrictions, sends `@unsit=force` and notifies the owners) and **Runaway** (the wearer can drop all owners, and the source says this works even if she is blocked). The relay has a **"helpless"** mode (`relay helpless on`) that removes the wearer's relay safeword; a Galatea collar must keep it off (open question 8). The collar also listens on LockMeister and the relay channel.

**Relay.** The collar includes an ORG relay on `-1812221819` with modes, trusted lists and its own safeword. Other objects' relayed commands arrive at the viewer as the same owner-say lines, so the client cannot distinguish them from David's.

**What her client must do as the "viewer side" (when a collar is adopted):**
1. Treat only the collar item David approved (name, item id, creator, as a worn attachment she owns) as an RLV source; everything else's `@…` is ignored.
2. **Speak the version reply** on the requested channel quickly (inside the 30 s window, only after login can chat).
3. **Audit the owner list** before accepting force commands: speak `/1<prefix> list` on the private channel (not public chat), parse the reply for avatar ids, and refuse everything while any avatar other than David is owner, trusted or blocked-changed, or public/group access is on. Re-run on a schedule and whenever the collar announces an owner change. *This depends on the build answering `list` to the wearer; confirm live.*
4. Accept force commands only while David is in the region and only through the gate (section 3). `@detach`/`@remoutfit` strips become the safe UNDRESS or outfit-swap intent; `@sit:<uuid>=force` becomes PLACE; locks are recorded but not enforced; `@setgroup`, `@sendim`, `@tpto` and similar stay refused.
5. Honor the collar's safeword and Runaway as additional releases (they clear collar state; the client then latches RLV off and tells David).
6. Never accept the collar into her outfit, or give it permissions, without David's OK (offers and script-permission questions stay under their existing rules).

**What David would provide for the collar phase:** the collar item (or which collar), its exact version/updater ("OpenCollar Update (Peanut)"), whether the per-layer Un/Dress and the Terminal are present, the Remote's name and version, and a plan for setting David as the only owner (the access menu flows from the wearer's side: Galatea would add him, and he would confirm in his own viewer).

## 10. Open questions for David

1. **Limits (section 4):** approved as written? Anything to add or remove? Is 60 minutes the right armed-session length?
2. **Your HUD:** what is its exact name, version and creator, and can you edit its script? Which buttons does it have? **This decides the transport.** If it can say a line on channel 0 or open a dialog (T1/T8), nothing needs to be worn. If it only uses a private channel (as the Peanut Remote does), I cannot receive it with a client-only listener, and the least-intrusive receiver is a tiny invisible HUD attachment (T4a), or a standard relay if it speaks relay protocol.
3. **A live probe:** may I log (read-only) what her client receives when you press each HUD button, before any action is wired?
4. **Acknowledgements:** do you want the HUD to show "ok/refused" (needs a dialog reply, T8) or is a line to you in chat enough?
5. **Undress while standing:** go to the nearest seat and change there (default), change in place, or decline?
6. **Android form:** refuse undress (default), or switch to the human form automatically first?
7. **Furniture:** only known furniture from the seat data, or also any chair you stand within 5 m of? Outside our parcel, too? Does your HUD pass an object id, or must she find "the furniture next to you"?
8. **Collar phase (later):** Is the Peanut build the one in the `VirtualDisgrace/opencollar` source, and which version is yours? May the collar's safeword and Runaway stay enabled, and relay "helpless" and public/group access stay off, as the design requires?
9. **Where is undressing allowed?** Only our parcel with just us (default), or also private Adult parcels or regions you choose?
10. **Release words:** which phrases from you should fire the instant hard release (default: release, let her go, stop)? May her own "no" detach any worn receiver or collar?
11. **Dress back:** only on your command (default), or also after a set time or when you log off?
12. **Other people's objects:** is the answer "never" for the first phases, or do you want a way to approve a particular owner later?
13. **Talking about it:** how should she raise a concern mid-session? Her usual chat voice is the plan; tell me if you want something else (a notice, an IM, a word she can use).
