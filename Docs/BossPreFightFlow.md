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
- **Dialog entry:** cousin NPC dialog → `Trigger()` → `TriggerIntro()` (sets `triggeredByPlayer=true`).
- **Start chain:** `Introduction()` (once, `introPlayed`): `ModifyControllerLock(Cinematic,true)` +
  `ModifyPlayerInvulnerability(Cinematic,true)`, `SetNewPool(GetClosestPool())`,
  **`owner.TeleportTo(currentPool.cousinPosition.position)`** (moves the *boss*, not the player),
  `RotateCameraTowardsPosition(...)`, `LootManager.SetBossFightLoot(true)`, animator "Intro".
  → `StartFight()` (animation event): `FightStarted=true`, unlock Cinematic, `EnablePoolDamagers(true)`.
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

### G-B. Room sealing diverges (presentation suppressed on client)
The blockade SetActive calls fire from **animation events** in the intro/death animators. With
`EnableBossClientPresentation=false` (5.4-F3, default) the client's intro animation chain does not fully
run, so `churchEntranceBlockade` / `blockadesToActivateAfterFight` may be left in the wrong state on the
client (room visually open while host has it sealed, or vice-versa).

**Fix direction:** treat room-seal as a **host-authoritative discrete event** (like Cousin pools): host
broadcasts seal/unseal of a named blockade; client mirrors `SetActive` directly (bypassing the animation
event it never receives). Reuse the `HostBossDiscreteEvent(36)` channel or add a dedicated one.

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
| **PF-2 Room-seal mirror** | Host broadcasts blockade seal/unseal as a discrete event; client mirrors `SetActive` directly, independent of the suppressed animation chain (G-B). Per-boss blockade registry (Witch first). | Medium |
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

## 3b. Arena Lockdown (FF14-style boss gate) — feature design

User request (the actual next feature; supersedes the PF-1/PF-2 ordering above as the priority):
re-create an FF14-style boss-arena entry experience, whose *purpose is to let some players AFK
safely* (an AFK player must not be the boss's target).

**Desired flow (per the user):**
1. Any player starts the fight.
2. Players **inside** the room: their boss room is already sealed (they crossed the entry trigger that
   closes the door on the way in).
3. Players **outside** the room (never crossed the close trigger): their boss room seals **after 5 s**.
4. **5 s after sealing**: a confirmation popup appears — **only a "Yes" option**.
5. Teleport to the *close-trigger position* (position only; the door is already shut) happens on **Yes**
   **or** when the **boss fight ends**.

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

## 4. Open questions to resolve before coding PF-1
- Where is the **boss-room entry trigger** in the scene graph? (Dialog trigger vs a separate volume.)
  Needs an in-game probe: log the first pre-fight entrypoint per boss and whether the client reaches it
  before/after the host.
- Does the host already know "client is in my boss scene+seed"? (SceneTransition/LinkState has scene+seed
  authority — PF-1 should *reuse* `NetRunState` seed equality, not invent a new check.)
- Is blocking at `EventStarted`/`Trigger` enough, or does the dialog need to be held earlier (at the
  dialog-open) to avoid the player reading a desynced dialog?
