# Boss Pre-Fight Flow Sync (design + audit)

Goal of this phase (user request, 进行ルーティン): synchronize the **pre-fight** portion of a boss
encounter — the part *before* `BossDamageAuthority` takes over:

1. **Room sync start** (boss房间的同步开始) — both players are converged in the boss room before the
   intro sequence begins (no client racing ahead into a divergent boss instance).
2. **Room sealing** (同步关闭房间) — the arena blockades / doors close in sync on both ends.
3. **Dialog sync** (对话同步) — the intro dialog plays and commits ("I chose to fight") in sync.
4. **Player teleport** (玩家传送) — the intro/phase teleports place both local players into the same
   arena room together.

This is the missing front half of [BossAuthority.md](BossAuthority.md). That doc covers from
`TriggerFight`/`EventStarted` onward (damage/phase/death). This doc covers the entry sequence that
*leads into* it.

---

## 0. Reverse-engineered ground truth

Decompiled from `PerfectRandom.Sulfur.Gameplay.dll` (`obj/decomp_gp/…`). Method/field names exact.

### Witch (`WitchBossController` + `WitchAnimationControl`)
- **Dialog entry:** `WitchFightDialogTrigger.StartFight()` → `bossController.EventStarted()`. Called from
  a NodeCanvas ExecuteFunction node inside the dialog graph.
- **Start chain:** `EventStarted()` → `FightStartRoutine()` (coroutine, spans frames):
  - transition effect → `WaitForSeconds(1)` → `WitchPhase2.SpawnWitches()`
  - **`TeleportPlayerTo(WitchFightRooms.Normal)`** → `playerUnit.TeleportTo(playerPointNormal.position)` +
    camera rotate. **Moves the local `playerUnit` only.**
  - `StartFight()` (private): `fightStarted=true`, `witchMainUnit.AttachToBossUI(true)`,
    `ModifyControllerLock(Cinematic,true)`, `ModifyPlayerInvulnerability(Cinematic,true)`.
  - music, `witchMainUnit.onDeath += WitchDeath`, `WaitForSeconds(1)`, `ChangePhase(Phase1_EightFlying)`.
- **Per-phase teleport:** `TeleportPlayerTo` is also called inside phase transitions:
  `Phase5Transition → Laser`, `Phase6Transition → Normal`, death `TeleportBackAfterFight → Realworld`.
  Rooms are 3 distinct physical locations (`playerPointNormal/Laser/Realworld`).
- **Room sealing:** `WitchAnimationControl`:
  - `churchEntranceBlockade.SetActive(...)` — disabled by `LookAtWitch(clip)` (intro look, also
    `LockPlayerController()`), re-enabled by `EnableChurchBlockade()` (post-fight).
  - `blockadesToActivateAfterFight.SetActive(true)` in `WitchDeath`.
  - These fire from **animation events** in the witch intro/death animators.
- **Locks:** `GameManager.ModifyControllerLock(LockStatePadlock.Cinematic, …)` /
  `ModifyPlayerInvulnerability(Cinematic, …)` / `LockPlayerController(bool)`.

### Cousin (`CousinHelper`)
> **Corrected by full decompile (was "StartFight is an animation event" — wrong).** `CousinHelper` itself has
> **no dialog / attack-option / sequencing logic**. The whole flow is driven by an external **behavior tree**
> (`U_GoblinCousin_Sequencer_1`); the class only exposes the step methods it calls.
- **Entry trigger:** `PlayerTrigger "CousinTrigger"` → `CousinHelper.Trigger()` → `TriggerIntro()` (sets
  `triggeredByPlayer=true` — that is *all* `Trigger` does).
- **Behavior-tree sequence (reads `triggeredByPlayer`):**
  `Introduction() → 2s → Npc.Interact() [REAL intro dialog] → 1s → SpawnArm(pos) → 0.1s → AttachToBossUI + StartFight()`.
- `Introduction()` (once, `introPlayed`): `ModifyControllerLock(Cinematic,true)` +
  `ModifyPlayerInvulnerability(Cinematic,true)`, `SetNewPool(GetClosestPool())`,
  **`owner.TeleportTo(currentPool.cousinPosition.position)`** (moves the *boss*, not the player),
  `RotateCameraTowardsPosition(...)`, `LootManager.SetBossFightLoot(true)`, animator "Intro", then
  `triggeredByPlayer=false`.
- `StartFight()`: `FightStarted=true`, unlock Cinematic, `EnablePoolDamagers(true)`.
- **The dialog is non-interactive presentation; the "双剑/attack" option does NOT call StartFight.** What gates
  the fight in single-player is the **pause**: opening the dialog sets `timeScale=0` (Dialog padlock), which
  **freezes the behavior tree's `WaitForSeconds`** before SpawnArm/StartFight. Dismissing the dialog unpauses,
  the timer resumes, and ~1.1s later the fight starts. So in vanilla SP the *dialog dismissal* is the de-facto
  fight gate. → This is the root of the co-op overlap bug; see **§3d (Plan B)**.
- **No player teleport** — Cousin teleports itself to the closest pool to the player. Camera locks onto it.

### Lucia (`LuciaBossFightHelper` + `LuciaBossFightTrigger`)
- **Dialog entry:** dialog "attack" option → `LuciaBossFightTrigger.TriggerFight()` →
  `luciaHelper.TriggerFight()` (`new`, hides base). No room teleport in the start (see BossAuthority.md).

### Desert (`DesertClauseBossFightHelper`)
- **Start entry:** `OnStartInteractWithBoss()` (player interact) → camera + `DelayIntro` coroutine →
  animator "IntroStarted" → anim event → override `TriggerFight()`. Cinematic camera lock (5.4-F3:
  the client skips the cinematic and calls `TriggerFight` directly).

### Generic doors — `MetalGate` / `MetalGateTrigger`
- `MetalGate.Open()/Close()` toggle `boxCollider.enabled` + animator "Closed" (+ optional A* nav
  walkability). `MetalGateTrigger.OnTriggerEnter` (Player(Clone)) **opens** the gate then `Destroy`s the
  trigger. These are *ordinary* corridor doors, not boss-arena seals — but they are the generic
  "door state" primitive a room-seal sync may reuse.

---

## 1. What already works (do not rebuild)

- **Encounter-start handshake** (5.4-E…E3): client first start entrypoint is blocked → requests host →
  host runs real start → `HostBossEncounterStart(29)` → client `TryApplyHostStart` **invokes the same
  host start method under a reentry guard**. For the Witch this means the client **replays the real
  `EventStarted`**, so `FightStartRoutine` runs locally and **`TeleportPlayerTo(Normal)` already
  teleports the client's own local player into the arena.** (BossAdapterBase.TryApplyHostStart →
  BossReflect.TryInvoke(EventStarted).)
- **Dialog commit** (5.4-E3): `ClientBossDialogCommitRequest(30)` / `HostBossDialogCommit(31)` already
  syncs the "I chose to fight" decision for dialog-gated bosses + suppresses duplicate dialog entry.
- **Phase teleports**: per-phase `TeleportPlayerTo` is driven by `ChangePhase`, which is
  host-authoritative (5.4-G2, `HostWitchPhase=40`). When the client applies the host phase under the
  reentry guard, the real `ChangePhase` runs its transition coroutine → the client's local player
  teleports to the new room. (Needs an in-game re-confirm that the G2 apply path runs the coroutine.)

---

## 2. The real gaps (what this phase must fix)

### G-A. Convergence: client races ahead into a divergent boss level
**Root cause (Log99, known-issue memory):** the client can load/enter the boss level *ahead of* the
host. Procedural seeds then diverge → the client's boss is an **orphan** (host can't damage it; host
broadcasts `fightStarted=false`) → **infinite dialog**. The pre-fight handshake assumes both ends share
the same boss instance; convergence is currently *not enforced at the boss-room boundary.*

**Fix direction:** a host-authoritative **pre-fight convergence gate**. When any player reaches the
boss-room entry trigger, hold the intro (block `EventStarted`/`Trigger`/`OnStartInteractWithBoss` from
*committing*) until the host confirms all live players are present in the same boss scene+seed. Only
then does the host broadcast "begin intro" and both ends run the start chain together.

### G-B. Room sealing diverges (presentation suppressed on client) — ❌ REJECTED, not a real gap (Log151/152)
**Original hypothesis:** the blockade SetActive calls fire from **animation events** in the intro/death
animators. With `EnableBossClientPresentation=false` (5.4-F3, default) the client's intro animation chain
does not fully run, so `churchEntranceBlockade` / `blockadesToActivateAfterFight` may be left in the wrong
state on the client.

**Outcome — this gap does not exist.** PF-2 (room-seal mirror) was built and tested, then reverted. Two
findings disproved the hypothesis:
- A real Witch fight (Log152, `Act_03_EndChurch`) showed the seal-driver methods
  (`WitchAnimationControl.LookAtWitch / EnableChurchBlockade / EnableOutsideTrigger`) **never fire at
  runtime** — not on the host, not even caught by the read-only lifecycle probe. There is nothing to mirror.
- The Witch arena is **inherently enclosed and entered by teleport** (`TeleportPlayerTo`, element ④ — which
  already works). There is no door that *locks during the fight*; `churchEntranceBlockade` is handled by
  scenery/cutscene timing outside the live fight flow, not as a per-fight seal.
- Cousin already has no vanilla blockade (Log121 `[ArenaDoor]`=0).

So no SULFUR boss uses a live, divergent in-fight room seal. The PF-2 work (host-authoritative seal
broadcast on `HostBossDiscreteEvent(36)` with `EventName="Seal:<id>:<0|1>"` + client `SetActive` mirror)
was `git restore`-reverted. **Do not rebuild it.** A genuine "seal the room" feature is the FF14 arena
lockdown (§3b), which spawns its *own* barrier rather than mirroring a vanilla one.

### G-C. Multi-player teleport target (single point → overlap)
`TeleportPlayerTo` moves to a single `playerPoint*`. With 2–4 players all teleporting to the same point
they stack. Acceptable for a first pass (physics separates them), but a per-slot offset may be wanted.
Low priority — list as a polish item, not a blocker.

### G-D. Lock/invuln state ownership during cinematic
`ModifyControllerLock(Cinematic)` / `ModifyPlayerInvulnerability(Cinematic)` are set by the start chain.
Because the client replays `EventStarted`, these run locally too — but if the convergence gate (G-A)
holds the client's start, the client must not be left permanently locked. The gate must guarantee a
matching unlock on every exit path (including failure/timeout).

---

## 3. Proposed phases

| Phase | Scope | Risk |
|-------|-------|------|
| **PF-0 Convergence probe** ✅ | Read-only. Logs local-vs-remote scene+seed convergence at every boss pre-fight start entrypoint, plus Witch room-seal timing. Answers section-4 open questions before PF-1/PF-2 are built. | None (diagnostic) |
| **PF-1 Convergence gate** | Host-authoritative "all players present in boss scene+seed" barrier at the boss-room entry; hold intro until converged or timeout; guaranteed unlock on every exit. Fixes the infinite-dialog root cause (G-A/G-D). | High (touches start path) |
| ~~**PF-2 Room-seal mirror**~~ ❌ REJECTED | Built + tested + reverted. The seal-driver animation events never fire in a real Witch fight and the arena is teleport-in/enclosed — G-B is not a real gap. Do not rebuild. See G-B above. | — |
| **PF-3 Teleport co-op polish** | Verify per-phase `TeleportPlayerTo` follows on the client; add optional per-slot offset so players don't stack (G-C). | Low |

Each phase is config-gated and falls back to current behavior when off. Validate one boss end-to-end
(recommend **Witch** — it has all four elements: dialog trigger, room teleport, blockade seal, phases)
before generalizing.

---

## 3a. PF-0 probe — implemented (read-only)

Config: `[NetworkBoss] LogBossPreFight = true` (default on). No gameplay change.

**Wire-up:** `NetBossEncounterManager.LogPreFight` is called at the top of `OnLocalStartEntrypoint` (the single
choke for every boss start entrypoint). Convergence is read via
`NetGameplaySyncBridge.FormatBossConvergence` → `NetService.FormatBossConvergence` →
`NetRunStateManager.FormatBossConvergence` (compares `LocalState` to every known remote with the same seed
authority the scene system uses). Room-seal timing reuses the existing lifecycle probe, now also patched onto
`WitchAnimationControl.{EnableChurchBlockade, LookAtWitch, EnableOutsideTrigger}`.

**What to read in the logs (run host + sandbox client, take a boss to the door):**
- `[BossPreFight] entry source=… mode=Host/Client joined=… terminal=… key=… allConverged=… | local=…(scene,seed) requireSeed=… converged=N/M | <peer>=>OK|SEED-SPLIT|DIFF-SCENE|NO-LEVEL`
  - **`allConverged=False` with `SEED-SPLIT`** when the boss start fires ⇒ confirms G-A (client raced ahead into a
    divergent seed). This is the smoking gun for the infinite-dialog bug.
  - Compare the host and client `[BossPreFight]` timestamps for the *same* `key` to see who reached the boss first.
- `[BossLifecycle] WitchAnimationControl.EnableChurchBlockade | …` / `LookAtWitch` / `EnableOutsideTrigger` — seal
  timing on each end. If the client never logs `LookAtWitch`/seal lines that the host logs ⇒ confirms G-B (the
  client's suppressed intro animation never fires the blockade events).
- The existing `[BossLifecycle] WitchBossController.TeleportPlayerTo` lines (already probed) show whether the client
  replays the room teleport.

> Decision gate: only build PF-1/PF-2 after the probe shows whether the client actually races ahead (SEED-SPLIT) and
> whether the seal events diverge.

## 3b-LD1. Generic combat-room gate sync (MetalGate) — implemented + deployed (awaiting verify)

**Corrected understanding (Inspector evidence from the user, supersedes the "Cousin needs a mod barrier" note below).**
The `[ArenaDoor]` probe (3b) only hooked `DoorBlocker.CloseDoor`, so its zero hits meant "not a DoorBlocker", **not**
"no door". The real combat-room seal is a **`MetalGate`** (`PerfectRandom.Sulfur.Gameplay.Mechanisms.MetalGate.MetalGate`)
closed by a **`PlayerTrigger`** the entering player crosses: `PlayerTrigger(On Enter, Only Once).OnTriggerEvents →
MetalGate.Close()` (+ `MusicTrigger.StopMusic`). This is a **generic SULFUR mechanic** — boss arenas *and* ordinary
elite rooms use it (per the user). So no mod-spawned barrier is needed; we drive the **native** gate.

Inspector facts: the example gate has `startClosed=false` (opens by default, the trigger closes it on entry), its script
`boxCollider` field is **unassigned** and `disableNavOnClose=false`, so `MetalGate.Close()` here reduces to
`animator.SetBool("Closed", true)` — the physical block is the animated door mesh's own collider. `Close()`/`Open()`/
`ToggleDoor()` are all **public**. The user confirmed gates are **per-end independent** (each end's local `PlayerTrigger`
only closes its own gate), so an out-of-room / AFK player's gate is left open → the desync to fix.

**LD-1 (this build) — peer-authoritative gate mirror** (modeled on Phase 5.7-BR Breakable sync):
- Chokepoint: `MetalGate.Close()` / `Open()` postfix (every state change — PlayerTrigger seal, `MetalGateTrigger`,
  `AllDeadTrigger` open, `startClosed` init, witch car-chase — routes through these). `Awake` postfix registers the gate
  + its static world position. `MetalGate` is in the unreferenced Gameplay assembly → resolved + invoked by **reflection**.
- Networking: `NetGateState { pos, closed }` on `NetMessageType.GateState(53)`, same Client→Host→relay topology as
  `BreakableBreak`; the firing peer never mirrors its own. Receivers `FindMatch` the nearest local gate (≤1 m) and call
  the same `Close()`/`Open()` under a reentry guard (`GateSyncManager.IsApplyingMirror`) so the mirrored call doesn't
  re-broadcast. Per-gate `_lastState` skips redundant same-state sends (e.g. `OnEnable` re-close).
- Files: `NetGateState(.cs/Codec)`, `GateSyncManager`, `MetalGatePatches`, wired through `NetGameplaySyncBridge` /
  `NetService` (Broadcast/Send/Handle/Relay + dispatch) / `PatchBootstrap`; registry cleared on level change next to the
  breakable clear. Config `EnableGateSync` (default on), log tag `[GateSync]`.
- **Music NOT synced yet** (`MusicTrigger.StopMusic` is a separate subsystem; a client that never crosses won't hear the
  boss music — real bug, handled separately). FF14 popup/teleport = LD-2, layered on this.
- **Status:** built + deployed + **verified (Log153/154, partial multi-boss)** — bidirectional sync confirmed for
  GraveyardGate / MetalGate / TreeGate / Big Door (all MetalGate under the hood); Emperor (same-trigger dialog+lock)
  syncs fine. Known items:
  - **Edge:** the client missed one host "Big Door" close (`no gate near` — its matching gate wasn't registered / >1 m
    at that instant; same late-spawn race as BreakableBreak). Hardening TODO: queue unmatched gate events and apply on
    the gate's `Awake` register (a door left open on one end matters more than a missed barrel).
  - **Lucia — covered by LD-1b** (below): its door is a `GameObject "Doors"` toggled via
    `PlayerTrigger_StartEvent.OnTriggerEvents → GameObject.SetActive(Doors, true)` (alongside `Npc.Interact` for the
    dialog + `MusicTrigger.StartMusic`), not a `MetalGate.Close()`.
  - **Desert has no door:** the "sandstorm wall" is a *damage zone* encircling the arena (leaving the arena into the
    storm deals continuous damage), not a gate — nothing for LD-1 to sync. The arena presentation is handled by the
    existing desert-boss start sync (5.4-F).

## 3b-LD1b. SetActive-door sync (Lucia) — implemented + deployed (awaiting verify)

Some arenas seal not with a `MetalGate` but with a `PlayerTrigger` firing `GameObject.SetActive("…door…", true)` from
its `onTriggerEvents` (Lucia: `PlayerTrigger_StartEvent` → `SetActive(Doors, true)` + `Npc.Interact` dialog + music).
LD-1's `MetalGate.Close/Open` hook can't see a `SetActive`, so this is a sibling channel.

- Capture: `PlayerTrigger.Trigger(GameObject)` postfix (Core type, reflection; coexists with the dialog-flow probe on
  the same method) → `TriggerDoorSyncManager` reads the trigger's `onTriggerEvents` via the public `UnityEventBase`
  persistent-call API (`GetPersistentEventCount/Target/MethodName`), collects every `SetActive` target whose **name
  contains "door"** (only doors — `Npc.Interact`/music/other `SetActive` are ignored), and broadcasts
  `NetTriggerDoors { triggerPos, [(name, active)] }` (`TriggerDoors(54)`, same Client→Host→relay as `GateState`).
- Mirror: the receiver finds the matching local `PlayerTrigger` by position (≤1 m) and reads **its own** event to get
  **its** door GameObject reference (a serialized persistent target — returned even while the door is inactive), then
  `SetActive`s it to the broadcast state. No echo (the mirror calls `SetActive` directly, never `Trigger`).
- Config `EnableTriggerDoorSync` (default on), log `[DoorSync]`. Verify: enter Lucia's room on one end →
  `[DoorSync] capture trigger=PlayerTrigger_StartEvent doors=[Doors=on]` + the other end `[DoorSync] mirror … applied=1/1`
  and its `Doors` object activates.
  - **Unrelated (not ours):** `Act_03_Canyon:1` ("Canyon2") crashes with an Addressables `InvalidKeyException` (missing
    GUID 8e87dcbc…) — reproduced identically WITHOUT the coop mod (Log155). Vanilla/modpack content bug.

## 3b. Arena Lockdown (FF14-style boss gate) — feature design

User request (the actual next feature; supersedes the PF-1/PF-2 ordering above as the priority):
re-create an FF14-style boss-arena entry experience, whose *purpose is to let some players AFK
safely* (an AFK player must not be the boss's target).

**Desired flow — precise timeline (per the user, 2026-06-27 — pin this for LD-2):**
1. **Any** player enters the arena → crosses the entry trigger → door closes (LD-1 / LD-1b already sync this).
2. Every player who has crossed that close trigger = **in-room**.
3. The **first** door-close is the time anchor (**t = 0**).
4. **t + 5 s:** force-seal the local door of **every non-in-room** player.
5. **t + 10 s** (another 5 s): send a **teleport event** to the non-in-room players (teleport them in).
6. **In-room is updated in real time but event-driven** (a player who walks in later becomes in-room and is no
   longer sealed/teleported; the host does NOT poll — it reacts to entry events). Built on the
   room-membership substrate (§3e). Confirm-popup on the teleport (user: spike `DialogController` first, else
   `Dialog`+keypress); teleport also fires on boss death.

**⚠️ The seal must be a TWO-WAY barrier, not the vanilla one-way door (user finding, LD-1b test).** Lucia's vanilla
door is **single-sided**: from outside it is invisible (back-face culled) AND **passable** (one-sided collider). LD-1b
correctly reproduces that state on the out-of-room end — but a one-way door is **cheesable**: a player kept outside who
declines the teleport can still shoot into the arena through the passable/invisible door. (Gating the teleport popup to
a boss-dialog state so the player can't fire is *not* enough.) So when LD-2 force-seals a non-in-room player (step 4),
the barrier must be **two-way impassable + invisible** (an invisible solid wall blocking movement *and* projectiles/LOS
from both sides), not merely `SetActive` of the one-sided vanilla door. RECORDED — do not change LD-1/LD-1b for this;
it is an LD-2 requirement.

**Vanilla primitives (reverse-engineered, Core DLL):**
- `DoorBlocker : HoldingInteractable` — the sealable door. `ActivateDoorBlocker(env, isAnClosingDoor)`
  spawns 3 `DoorBlockerTrigger`s, each `onTriggerEvents.AddListener(CloseDoor)`. **`CloseDoor()` is the
  seal chokepoint.** `doorIsClosed` field; players hold it to open (`OnFinishedHolding`); `CanInteract`
  requires `doorIsClosed`.
- `DoorBlockerTrigger.OnTriggerEnter(Player)` → `onTriggerEvents.Invoke()` → `CloseDoor`. This is the
  "entered the room → shut the door" trigger.
- `AllDeadTrigger` (list of `Npc`, `onTriggerEvents`) — fires when all listed enemies die → opens the door
  (the room-cleared/fight-ended signal).

**Co-op problem:** every instance runs its own copy of the room + its own `DoorBlocker`. Player A
crossing the trigger only shuts A's local door. So a host-authoritative lockdown must drive the seal +
teleport on every instance.

**Targeting (answers the user's AFK concern):** `AiAgent.GetClosestPlayer()` picks the **nearest** player
from `GameManager.Players` (which includes the remote ghost players, phase 5.7-B). It is **not** all
players. So an AFK player kept **outside the sealed door (far)** is naturally not the boss's chosen target
— the in-room fighter is closer. Only AoE / special attacks (Cousin's summoned hands, etc.) hit everyone
in range, which the user accepts. (A harder guarantee would exclude the AFK ghost from the eligible set,
but distance is likely enough.)

**Proposed networking — `NetBossArenaLockdown` (host-authoritative):**
1. Host detects fight start (reuse the existing encounter-start handshake) → broadcast `ArenaLockdown{key}`.
2. Each instance: if the local door for `key` is already closed (player was inside) → nothing; else start a
   5 s timer → `DoorBlocker.CloseDoor()` directly (bypasses the trigger the out-of-room player never hit).
3. After seal + 5 s → show the Yes-only popup locally.
4. On Yes **or** on the encounter's terminal-death event (already synced per boss) → teleport the local
   player to the recorded close-trigger position.
- Config-gated; falls back to current behavior when off. Validate on **Cousin** first (Act_01_Caves).

**UI decision (user chose "reuse game UI"):** the game has **no clean generic Yes/No modal**. Candidates:
- `DialogController` + `DialogOption` (`UIManager.dialogControllerPrefab`, `DialogOption.SetDialogOption(string)`,
  `AcceptDialogOption`) — the in-world NPC option box; most native, but NodeCanvas/`DialogSpeaker`-bound and
  non-trivial to drive standalone.
- `Dialog` (text + background + `SetDialogText` / `SetState(UIState)`) + a confirm keypress — simplest,
  semi-native ("press [key] to enter"). Since the popup is Yes-only, press-to-continue UX fits.
- **Unresolved:** whether `DialogController` can be driven without a `DialogueTree`. Needs a probe / spike.

**Open unknowns to verify in-game before coding (probe-first, per the project's "stop guess-patching" rule):**
- Does the **Cousin room actually use `DoorBlocker`/`DoorBlockerTrigger`/`AllDeadTrigger`?** (Boss rooms may
  seal differently — e.g. Witch uses blockades/teleport-rooms, not `DoorBlocker`.) The PF-0 probe is being
  extended to log `DoorBlocker.CloseDoor` / `DoorBlockerTrigger` / `AllDeadTrigger` so a Cousin test reveals
  the real wiring + per-end timing.
- Can `DialogController` be invoked standalone for the Yes-only popup, or do we use `Dialog`+keypress?

## 3c. Fix A (root) — boss dialog interactable removal (PRIMARY dialog handling) ✅ implemented

**Evidence (LogOutput121, 5× Cousin/Caves):** convergence was healthy in all fights (`allConverged=True`,
0 SEED-SPLIT) — the desyncs were *not* seed bugs. Two real findings:
- **Host stale-dialog loop**: when the fight starts *remotely* (client triggered), the host opened the
  Cousin dialog **9 times** (`SetCurrentSpeakable: Cousin` ×9). The host's `mode==Host` start path always
  `return true` (runs the original) and had **no** equivalent of the client's duplicate-dialog suppression,
  so a host player arriving late kept re-opening a stale dialog.
- **Cousin room uses no vanilla door**: `[ArenaDoor]` runtime events = 0 (DoorBlocker/AllDeadTrigger never
  fired). The arena-lockdown seal for Cousin must be a mod-spawned barrier, not a reused vanilla door.

**Root cause of the loop:** `DialogueTree.currentDialogue.Stop(true)` only closes a *running* dialog; it
does nothing about the **interactable that can start a new dialog later**. When the fight starts while a
player is absent, there's no running dialog to stop, and the interactable survives → re-opens. Vanilla
already solves this for the Witch: `WitchBossController.FightStartRoutine` calls
`InteractionManager.RemoveInteractable(witchUnitInteractable)` (decompile :28977 — **pure vanilla**, not our
code). Witch doesn't loop in MP because its `EventStarted` replay runs that removal.

**Fix (now the PRIMARY boss-dialog handling, per user):** at fight-start, on **every end**, remove the
boss's dialog interactable — the same vanilla pattern, applied to Cousin/Lucia/Desert and to the MP
remote-start case. Suppression (`ShouldSuppressDuplicateDialogEntry`) + the client deferred-finalize stay
only as safety nets.

- `IBossEncounterAdapter.TryRemoveDialogInteractable` + generic `BossAdapterBase` impl: resolve the boss's
  dialog Npc (`ResolveDialogNpc` → `GetHealthUnit`), find every `UnitInteractable` whose `npc` is that boss
  (`UnitInteractable`/`InteractionManager` are **Core** types, referenced directly — no reflection), then
  `RemoveInteractable` + deactivate.
- Manager `RemoveDialogInteractableOnce` (once per encounter key) called from: client commit
  (`HandleHostBossDialogCommit`), host applying a client-initiated commit
  (`HandleClientBossDialogCommitRequest`), and host-initiated start (`OnLocalStartEntrypoint` host branch).
- Config `RemoveBossDialogInteractableOnStart` (default on). Log tag `[BossDialogFix]`.
- **Unifies with the FF14 lockdown:** *any player completing the boss dialog = fight start*; at that instant
  every player's boss dialog interactable is removed (this fix), so no one can re-trigger.
- **Status:** built + deployed; awaiting an in-game Cousin co-op re-test (commit only after verification).

## 3d. Plan B — dialog-close-gated fight start ✅ implemented + verified (Log133, commit `d3130d1`)

**Problem.** Phase 5.7-NP disables the multiplayer pause. For Cousin (and any boss whose fight is sequenced by a
behavior tree off scaled-time `WaitForSeconds`), that pause was the *de-facto* fight gate (see §0 Cousin): the
intro dialog used to freeze the timer until the player dismissed it. With pause gone, the behavior tree's
`StartFight` fires ~1.1s after the dialog **opens** and overlaps it — the fight and the cutscene run together.

**Faithful intro (prerequisite, config `EnableFaithfulBossIntro`).** Rather than fake-start via direct
`Introduction()/StartFight()` reflection, the client sets the boss's own `triggeredByPlayer` (via `TriggerIntro()`
inside `TryApplyDialogCommit`) so its **native behavior-tree intro + real dialog + camera + boss bar** play
locally; the mechanic stays host-authoritative. This is what makes a *real* dialog appear on every end to gate on.

**Plan B design (host-authoritative, two-phase; commit signal = boss dialog close).**
- **INTRO commit** (existing `NetBossDialogCommit`, `IsFightCommit=false`): walking into the trigger drives the
  intro handshake → both ends `TriggerIntro` → behavior tree plays intro + dialog. **StartFight is now gated.**
- **StartFight gate** (`OnLocalStartEntrypoint`, before the mode branches): for a dialog-gated boss
  (`CousinHelperAdapter.GatesFightOnDialogClose => true`) every behavior-tree `StartFight` is **blocked**. The
  real `StartFight` is invoked *only* by the dialog-close commit, under the reentry guard (which bypasses the gate
  via the `InReentry` early-out). While blocked, the player stays Cinematic-locked + invulnerable (Introduction's
  locks are never released) — i.e. SP-faithful "you can't act until the fight starts".
- **FIGHT commit** (`NetBossDialogCommit`, `IsFightCommit=true` — codec bumped to v2):
  - Arm: `Npc.Interact` postfix (boss) → `NotifyBossDialogOpened(npc)` records the open encounter (matched by
    `GetHealthUnit == npc`).
  - Trigger: `DialogController.SetCurrentSpeakable(null)` → `NotifyDialogClosed()` → the in-room player's commit.
  - Host: own close, or a client's fight-commit request → `CommitFightStart` = invoke real `StartFight` + close
    any lingering dialog + broadcast the FIGHT commit.
  - Client: own close → send a fight-commit **request**; on receiving the host's FIGHT commit → `CommitFightStartLocal`
    (invoke `StartFight` + finalize dialog). FF14 spec: once committed, *every* player's boss dialog is closed.
- Patches: `PatchDialogFlowProbe` now also applies when `GateBossFightOnDialogClose` is on (it reads the same
  `Npc.Interact` / `SetCurrentSpeakable` chokepoints the read-only probe used). Config `GateBossFightOnDialogClose`
  (default on). Log tag `[BossFightGate]`.

**Verification (Log133, 4× Cousin @ Act_01_Caves:6).** All 4 passed, both dismissal paths:
- client-first: `client dialog dismissed → request fight commit` → host `received client fight-commit` →
  `committed fight start [invoked StartFight]` → broadcast → client `received host fight-commit → committed`.
- host-first: `host dialog dismissed → committing` → broadcast → client `received host fight-commit → committed`.
- Both ends: `blocked behavior-tree StartFight … committed=False` during the dialog, then `invoked StartFight`
  only after the close. **0 Error / 0 NRE / 0 Exception. No deadlock, no overlap.**

**Intro-arm timing (phase PF-ArmDefer, issue 1 — implemented).** Originally only `StartFight` was gated, not the
intro `SpawnArm`, so the behavior tree's single intro arm (a **self-despawning presentation arm**: Log133
`damageCount=0`, `lifetime=13.4s`) poked out **during** the dialog instead of at fight start. In single-player the
dialog pauses the game (timeScale=0), freezing the behavior tree's `WaitForSeconds` so the arm only appears once the
dialog closes; co-op's no-pause mode (Phase 5.7-NP) removed that freeze. The fix restores the vanilla timing:

- **Block:** `CousinHelper.SpawnArm` is prefix-patched (`BossEncounterPatches.PatchIntroArm` →
  `NetBossEncounterManager.OnLocalIntroArmSpawn`). On both ends, while the encounter's fight loop has not begun
  (no Submerge/Reappear/MoveToNewPool fired yet, `_cousinFightLoopStarted`) and the boss is not terminal, the
  behavior-tree intro arm is **blocked** (`return false`). Our own commit replay runs under the reentry guard
  (`InReentry`), so it bypasses the block; mid-fight **Reappear arms** flow normally (the loop has started by then).
- **Replay:** `CommitFightStartLocal` (the dialog-close fight commit, host + client) invokes
  `adapter.TryReplayIntroArm` once per key (`_introArmReplayed`) under the reentry guard → the real `SpawnArm` runs
  at fight start (vanilla timing). With `EnableCousinArmSync` the host's replayed arm flows through the RT3-A
  boss-add pipeline + broadcasts; the client's replayed arm binds to it → **one** puppet, no double-spawn.
- **Late-client safety:** if the FIGHT commit reaches a client before its intro plays (Log134 race), the commit
  replays the arm, then the client's late behavior-tree intro arm fires — but the fight loop still hasn't started,
  so it is blocked as a duplicate (the `_cousinFightLoopStarted` gate, not `FightStarted`, is what distinguishes it
  from a real Reappear arm).
- Config `DeferBossIntroArm` (default on, requires `GateBossFightOnDialogClose`). Log tag `[BossArmDefer]`.

> **Prior status (phase RT3-Cousin-arms).** The arm *sync* half was already done: `GoblinCousinArm` flows through
> the RT3-A boss-add pipeline (see [Versioning.md](Versioning.md) §4 / agent memory `phase-5-5-rt-runtime-spawn-sync`),
> so the client mirrors **one** host-authoritative puppet arm, and the client mud-ball visual is the arm's own
> `CousinArm.ThrowProjectile` de-fanged + target-fixed via `CousinArmPatches` (animation-event aligned). PF-ArmDefer
> above closes the remaining timing half (issue 1).

**Pre-existing behavior, out of scope.** When a client triggers the boss remotely, the host's faithful intro runs
too, so the host player is pulled into the synced cutscene (Cinematic-locked) even if across the map. Plan B holds
that lock until *someone* dismisses the dialog. This is the room-membership problem (§3b #2) — addressed by the
room-membership substrate (§3e), whose first consumer (RM-2) will scope the synced cutscene to in-room players.

## 3e. Room-membership substrate (RM) — RM-1 implemented (observe-only)

The shared foundation behind both the dialog cutscene scoping and the arena lockdown (§3b): a host-authoritative
"who is in the boss room" set, per the user's **report → host accepts → in-room broadcast** model.

**Feed, decompile-confirmed.** `PlayerTrigger.onlyOnce` is a per-instance `SerializeField`, and host/client each
have their own local copy of every trigger; `OnTriggerEnter` requires `unit.isPlayer`. So **each end fires its own
room triggers for its own local player, independently** — `onlyOnce` does not stop the *other* end's player. Log133
shows each end crossing `Trigger` (room doorway, generic name, fires via an `onTriggerEvents` UnityEvent the probe
can't read) then `CousinTrigger` (→ `CousinHelper.Trigger`, deeper, at the boss). RM-1 uses **crossing CousinTrigger**
(i.e. the local `CousinHelper.Trigger` firing) as the in-room signal — precisely attributable to the encounter key,
and it captures every player who reaches the boss (the wider doorway `Trigger` is an RM-2+ refinement for players who
enter the room but never reach the boss).

**RM-1 (this build, observe-only — changes no behavior).**
- Host holds `_roomMembers: key → {playerId}` (host = `"host"`, clients = peer id); clients cache the last host
  broadcast in `_roomMembersClientView`.
- `OnLocalStartEntrypoint` (before any gating/dedup, so it fires even when the start is blocked) calls
  `ReportLocalRoomEntry` when `adapter.IsRoomEntrySource(source)` (Cousin = `"Trigger"`). Host marks itself +
  broadcasts; a client sends `ClientRoomEnter` (msg 51). Host `HandleClientRoomEnter` marks the peer and broadcasts
  `HostRoomMembership` (msg 52); clients cache it.
- API for future consumers: `IsPlayerInRoom(key, id)`, `GetRoomMembers(key)`. Config `EnableBossRoomMembership`
  (default on). Log tag `[RoomMembership]`.
- **Verify next test:** both players walk up to Cousin → host logs `host in-room += host` and `+= client-1`,
  `members=[host,client-1]`; client logs `client received … members=[…]`. Once the set is correct, RM-2 wires the
  first consumer (scope the synced intro to in-room players; the host runs the mechanic authoritatively but skips its
  own intro/Cinematic-lock when not in-room) and then the arena lockdown.

## 4. Open questions to resolve before coding PF-1
- Where is the **boss-room entry trigger** in the scene graph? (Dialog trigger vs a separate volume.)
  Needs an in-game probe: log the first pre-fight entrypoint per boss and whether the client reaches it
  before/after the host.
- Does the host already know "client is in my boss scene+seed"? (SceneTransition/LinkState has scene+seed
  authority — PF-1 should *reuse* `NetRunState` seed equality, not invent a new check.)
- Is blocking at `EventStarted`/`Trigger` enough, or does the dialog need to be held earlier (at the
  dialog-open) to avoid the player reading a desynced dialog?

## 5. Investigation — "dialog can't advance, and it persists" (multiple-choice not clickable)

**Root cause CONFIRMED (user, later real-match observation): a missing overlay layer.** Clicking the blank area of a
stuck dialog reports hit type `none`; in a healthy dialog clicking blank space should **advance** the dialog. So the
defect is that the full-screen overlay that is supposed to sit under the dialog, catch clicks on empty space, and turn
them into "advance" is absent — blank clicks fall through to `none` and nothing happens. **Fix direction:** restore /
re-enable that full-screen click-catching overlay when a dialog / multiple-choice is shown (or make a blank-space
click advance the dialog). This supersedes the "shelved" state below; the probe findings are kept for context.

A recurring co-op bug: a boss/NPC dialog reaches a **multiple-choice** and the player cannot pick any option
(clicking does nothing); the state persists across a level switch. Reverse-engineered + probed (`DialogInputProbe`,
now reverted) across Log269–271. Recording the findings so the next attempt starts from fact.

**Ruled out (all measured, not guessed):**
- **NOT melee / holding the attack key.** `Fire` (weapon) and `AcceptDialogOption` (dialog advance) *do* both bind
  Mouse leftButton, and our no-pause mod (`PauseControlPatches`) *does* block the vanilla `Dialog` pause padlock
  (`ModifyGamePauseState`) so the game keeps running during dialog — but the probe showed `LMB held-at-open=False`
  every time. The held-LMB "no fresh press edge" theory is wrong.
- **NOT cursor lock.** `cursor = None/vis=True` at every dialog open (free + visible).
- **NOT a null `selectedButton`.** It is `NULL` in BOTH the working and stuck dialogs, so it isn't the differentiator
  (mouse selection goes through the button's own `onClick`/EventSystem, not `selectedButton`).
- **NOT a fullscreen raycast-blocking overlay.** An EventSystem `RaycastAll` at each stuck click returned
  `rayTop=NONE` — the click hit *nothing*, not a blocker.

**What it actually is (pinned to the mechanism, not yet the trigger):** during the stuck dialog the **player-options
panel does not intercept clicks**. The NPC text box (`CharacterDialog`/`DialogBodyText`) *is* raycastable (probe hit
it), but clicks aimed at the options land on empty space (`rayTop=NONE`), and the option `onClick` → `Finalize`
**never fires** (vs the working dialog, where `Finalize` fired). Relevant code fact: `DialogController.
SetOptionsInteractable(state)` only sets `playerDialogCanvasGroup.interactable`, **not `blocksRaycasts`** — so if the
options `CanvasGroup` isn't blocking raycasts (or the panel isn't truly active/visible), the buttons are shown but
don't catch the mouse. It correlates with a messy reload/teleport session (the probe also saw F3 level-select menu
hits, `LevelMenuButton`/`ChapterPanel`), so a clean repro (no F3) is needed to isolate the trigger.

**Not yet pinned:** *why* the options panel stops blocking raycasts on a subsequent dialog (CanvasGroup
`blocksRaycasts`/`alpha` vs `activeSelf` vs an EventSystem/GraphicRaycaster left disabled after a teleport/reload).
**Next probe if revisited:** log the `PlayerDialog` panel's `activeSelf` + `CanvasGroup.alpha`/`blocksRaycasts`
+ EventSystem presence at the moment a stuck multiple-choice is shown, with a clean (no-F3) repro. **Likely fix**
once confirmed: set `blocksRaycasts=true` alongside `interactable` (or re-activate the options panel) when a
multiple-choice is shown. **SHELVED per decision; probe reverted, no code shipped.**
