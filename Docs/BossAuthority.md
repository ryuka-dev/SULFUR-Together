# Boss Multiplayer Authority

Implementation of host-authoritative boss fights (Phases 5.4-E → 5.4-G7, plus 5.5-RT runtime spawn). The reverse-engineered references this is built on live in **`BossSourceAudit.md`** — read that for exact method/field names and per-boss mechanics. This doc is the *architecture + per-subsystem map*.

> Messages: `ClientBossStartRequest=28`, `HostBossEncounterStart=29`, `ClientBossDialogCommitRequest=30`, `HostBossDialogCommit=31`, `HostBossState=32`, `HostBossDynamicSpawn=33`, `ClientBossHitRequest=34`, `HostBossHitVisual=35`, `HostBossDiscreteEvent=36`, `ClientLuciaEyeReport=37`, `HostLuciaEyeState=38`, `HostLuciaDeath=39`, `HostWitchPhase=40`, `HostWitchP2Manifest=41`, `HostWitchP2Result=42`, `HostRuntimeSpawn=43`. Config: `EnableBossEncounterSync` (+ per-feature flags below).

---

## Core principle

The **host runs the real boss fight**; the client mirrors it. Every mechanic-advancing action goes through the host's real game code:
- Real damage entry is **`Unit.ReceiveDamage`** (fires `onDamageRecieved`). `SetStatus` only sets HP and does **not** advance phase/death mechanics (5.4-E5 audit finding).
- The client never computes boss damage, phase changes, spawns, or death locally — it reports intent and applies host-authoritative results.

### Adapter pattern — `IBossEncounterAdapter`
Each boss family has an adapter (`WitchBossControllerAdapter`, `CousinHelperAdapter`, `LuciaBossFightHelperAdapter`, `EmperorBossAdapter`, generic `BossFightHelperAdapter`). `NetBossEncounterManager` is the message hub; `BossReflect` is the reflection helper (`DeclaredOnly` to handle `new`-hidden members). Adapters resolve roles and host targets; the manager owns the wire protocol.

---

## 1. Encounter start (5.4-E / E2 / E3)

Bosses start via a host-authoritative handshake so both ends begin the fight on the same frame:
- Client triggers a boss → `ClientBossStartRequest(28)` → host runs the real start chain → `HostBossEncounterStart(29)`.
- An **authorized continuation window** keeps a cross-frame start chain from being blocked (5.4-E2). Real start chains per boss; Emperor's real type is `PerfectRandom.EmperorBossFightHelper`.
- **Dialog-gated bosses** (Cousin / Lucia / Desert): the real dialog is closed with `DialogueTree.currentDialogue.Stop(true)`; commit is `ClientBossDialogCommitRequest(30)` → `HostBossDialogCommit(31)`, with duplicate dialog-entry suppression. Lucia uses `new TriggerFight`.
- Presentation (`EnableBossClientPresentation`, **default false**): the client's boss-intro animation chain does not fully run, and `DoneAppearing` (an animation-event that clears invulnerability) never fires → client-side boss owner stays permanently invulnerable. So client presentation is suppressed by default; only safe visuals (e.g. Lucia hit flash) are kept (5.4-F3).

---

## 2. Damage authority — `BossDamageAuthority` (5.4-F)

```
client hits boss main body
  → Npc.ReceiveDamage prefix → TryClientBossHit (boss-claim takes priority over the generic puppet ClientHitRequest)
  → ClientBossHitRequest(34){role}
  → host BossDamageReflect → real Unit.ReceiveDamage(IDamager overload, source = GameManager.Instance.PlayerUnit, NOT Player)
  → boss mechanic advances + ~0.4s later HostBossState(32) syncs health
```
- Adapter `ResolveHitTargetRole` (client) / `ResolveHostTargetForRole` (host) map a hit to the right host Unit. `main` ↔ `GetHealthUnit`.
- Feedback (5.4-F2): `HostBossHitVisual(35)` → client plays the native white flash (visual only, no re-damage).
- **Witch routing (5.4-G):** the player actually shoots each phase controller's `witchUnit` (not `witchMainUnit`); its `onDamageRecieved` routes to `OnDamageMainWitch` (+ Phase4 `RegisterInstance`). The adapter routes a client hit on the phase1/3/4/5/6 witch to the host's corresponding `controller.witchUnit` so the main HP and the phase mechanic advance together, reusing the same `BossDamageAuthority` pipeline.

---

## 3. Phase authority

### Witch phases (5.4-G2) — `HostWitchPhase=40`
Witch phases **cycle** (Phase6 → Phase1) and the client would self-advance and desync. Fix: patch `ChangePhase` — prefix **blocks** the client's self-advance; postfix (host) broadcasts `phase + monotonic revision`. The client applies by revision (`EndCurrentWitchPhase` outside the reentry, `ChangePhase` inside). `HostBossState` then only carries health.

### Witch Phase 2 — true/illusion domes (5.4-G4 / G5 / G6)
The real witch among the domes is the host's two random rolls; the client must learn the **real dome index**:
- Host captures `realDome = IndexOf(realWitchUnit)` at the `ShowWitches` postfix → `HostWitchP2Manifest(41)`.
- Client blocks its local `ShowWitches` and mirrors: real witch into the host's real dome, illusions elsewhere.
- Hits route as `p2dome:N` role (boss-first); host calls `spawnedWitches[N].ReceiveDamage` → real `Real/IllusionTakeDamage` → `HostWitchP2Result(42)` → client hides.
- Lifecycle hardening (5.4-G6): a dedicated `_witchP2Cycle` (per `ShowWitches`) + `_witchP2Active` gate the result broadcast so it fires once per real transition; `HidePhase2Witches` on leaving Phase 2.

---

## 4. Dynamic / runtime spawns

### Boss-owned adds (5.4-E4) — `HostBossDynamicSpawn=33`
Boss sub-units (Terrorbaum/Lucia henchmen/maidens, CousinArm, LuciaEye, Witch illusions) spawn at runtime and the two ends choose differently. Chokepoint: hook **`UnitSO.SpawnUnit`** (its `mono` = the owning boss). The host records `(encounter, addType, seq)` and broadcasts; the client binds `local[seq] ↔ host[seq]` (same-position adds can only be bound by sequence — proven by LuciaEye). Health sync via `SetStatus` (92) + fire `onHealthChange` + attach bar once (E4.2).

### General runtime spawn sync (5.5-RT) — `HostRuntimeSpawn=43`
Architecture for **all** post-stabilization spawns (boss adds + F3 player spawns): host spawns → broadcasts `unitId.value + pos + hostSpawnIndex` → client `unitDatabase[UnitId(value)]` → `UnitSO.SpawnUnitAsync` mirror + `RegisterMirroredRuntimeSpawn` binds the host SpawnIndex into the existing puppet pipeline. Unified hook = `UnitSO.SpawnUnit`; F3 = `DevToolsManager.Spawn`.
- RT3: boss-mono adds reuse the E4 path (`NetBossDynamicSpawn` += `UnitIdValue + HostSpawnIndex`); client `FinishBind` → `RegisterMirroredRuntimeSpawn` reuses the local add as the host puppet (no double-spawn / NRE). Special-exclude set `{LuciaEye, CousinArm}`.
- Config `EnableRuntimeSpawnSync`.

---

## 5. Discrete events & terminal death

### Cousin (5.4-F4) — `HostBossDiscreteEvent=36`
Cousin is a **fixed-pool** event boss; both ends independently pick a pool (Submerge/MoveToNewPool/Reappear) and surface at different pools. Host is authoritative: broadcasts `NetBossDiscreteEvent`; the client prefix **blocks** its own choice and mirrors the host's pool (`MoveToNewPool` → `SetNewPool` + `TeleportTo`, matched by `cousinPosition` world pos since pool indices are unstable). **CousinDeath** reuses msg36 (`EventName=CousinDeath`): client `owner.Die()` runs the real death (elevator/health bar/music/hand cleanup) + marks `_terminalDead` to suppress later hit/state.

### Lucia eyes (5.4-F5 / F6) — `ClientLuciaEyeReport=37`, `HostLuciaEyeState=38`, `HostLuciaDeath=39`
Eye phase (`currentPhase==5`) count/cycle loop: client `EyeDied` prefix intercepts → reports (37), does NOT locally `RestartPhases`; host consumes its first living `LuciaEye` (`owner.Die()`) to trigger the real `EyeDied/RestartPhases`; postfix broadcasts remaining (38). Cycle-complete: client calls the real `RestartPhases` (recenter / leave phase5 / multi-wave re-entry). Terminal death (39): patch-overridden `OnBossDead`, prefix isolates the client's loot/save (`PlaceLoot/SaveCheckpoints/SaveBackup`), client `bossUnit.Die()` real death + presentation subset, reuse `_terminalDead`.

### Witch death (5.4-G7) — `EnableWitchDeathFix`
Client `WitchDeath` crashed because `witchMainUnit` is roster-bound → generic `EnemyDeathMirror` `Die()` → `WitchDeath` → `EquipmentManager.AmuletHoldable` getter → `equippedItems[InventorySlot.Amulet]` `KeyNotFoundException` (the client has no Amulet slot key). Fix (G7b): **prefix blocks the client's `WitchDeath`** + `adapter.TryApplyWitchDeath` runs a reflection-based safe replica (teleport back / health bar off / fire / music / environment / clear adds; **skips amulet + PlayerProgress**; `ChangePhase Dead` handled by G2) + `MarkWitchTerminal`; postfix (host) marks terminal.

---

## 6. Per-boss quick map

| Boss | Real helper type | Damage target role | Phase mechanic | Special spawns | Death |
|------|------------------|--------------------|----------------|----------------|-------|
| Witch | `WitchBossController` (+ per-phase `witchUnit`) | per-phase `controller.witchUnit` → `OnDamageMainWitch` | phases CYCLE; revision-driven (`HostWitchPhase`); Phase2 dome manifest | Phase2 illusions, Phase6 egg (TODO), adds | G7 safe replica (`TryApplyWitchDeath`) |
| Cousin | `CousinHelperAdapter` | main | fixed-pool discrete events | CousinArm | `CousinDeath` via msg36 |
| Lucia | `LuciaBossFightHelperAdapter` | main + eyes | eye count/cycle (`currentPhase==5`) | LuciaEye, henchmen | `HostLuciaDeath` (msg39) |
| Emperor | `PerfectRandom.EmperorBossFightHelper` | main | (diagnostic) two-worm | worms | (extension point) |
| Desert | `DesertClauseBossFightHelper` via `BossFightHelperAdapter` | main (`bossUnit`) | `BossPhase` (health-%), **host-authoritative** (client suppressed) | pikes / mount / diggas / missiles (TODO) | (extension point) |

### Desert (host-authoritative composite)

The Desert boss (`DesertClauseBossFightHelper`) is a composite: an old-man body (`bossUnit`/`bossNPC`) on a pike **mount**, a moving **sandstorm arena** (`DesertClausePerimeter` — a `SphereCollider` danger zone), spawned pikes/diggas, and pre-placed sniper/terminator missile bases. Its fight is **host-authoritative** and the client boss is a **passive puppet**: the client's per-frame phase logic is suppressed so it can't run a divergent local fight, and everything visible is driven from the host. Full breakdown in **§9**.

---

## 7. Files

`src/Networking/Gameplay/Boss/`: `NetBossEncounterManager.cs` (hub), `NetBossEncounterMessages.cs`, `IBossEncounterAdapter.cs` / `BossAdapterBase.cs`, the per-boss adapters, `BossReflect.cs` / `BossDamageReflect.cs` / `BossDialogReflect.cs`, `BossDynamicSpawnManifest.cs`, `BossLifecycleProbe.cs`, `WitchPhase2Probe.cs`, `EmperorWormDiagnostics.cs`, `BossTypeDiscovery.cs`. Patches: `BossEncounterPatches.cs`, `BossSpawnPatches.cs`. Runtime spawn: `RuntimeSpawnManager.cs`, `NetRuntimeSpawn.cs`, `BossSpawnPatches.cs`.

---

## 8. Known gaps

- Witch Phase 6 egg; precise Phase 2 `EightFlying` timing; black-ripple VFX; phase-witch positions (mostly OK, occasionally off).
- Emperor phase/death is diagnostic-only (extension points present).
- Boss adds: RT3 covers boss-mono adds; non-boss-mono adds (e.g. Witch egg whose `mono` is GameManager/phase) go through the general per-unitId-seq hook (RT3b).
- Host hitting a boss while the client watches: a minor "no feedback on client" cosmetic gap was deferred.

---

## 9. Desert boss (LD-Sandstorm / F4)

The Desert boss combines the sandstorm arena keep-in (LD-Sandstorm) with the client-presentation problem (F4). Reverse-engineering lives in `BossSourceAudit.md`; the sandstorm arena in `BossPreFightFlow.md`.

**Arena (LD-Sandstorm).** Gate-less: the battlefield is ringed by a moving `SphereCollider` "danger zone", not a door, so LD-2's seal/barrier/popup do not apply. In/out is the game's own test — `Distance(unit, sphereCollider.transform.position) > SphereRadius` (`SphereRadius = radius * lossyScale.x`, read live so it tracks the moving/resizing sphere) via `BossFightHelperAdapter.TryGetSandstormArenaSphere`. At fight-start (the boss dialog trigger) the host arms an `ArenaLockdownManager` sandstorm lockdown; after `SandstormPullDelaySeconds` every end whose local player is outside the sphere teleports to a spot **beside the first in-arena player** (not the centre, where the boss sits) + a toast. One-shot pull (no continuous keep-in).

**Client presentation (F4) — the boss must be visible + not fight locally.**
- **Puppet (host-driven):** the Desert boss is classified as a **`CombatEnemy`** so it enters the client puppet system — host-driven position, animation mirrored, local AI/weapon suppressed. It carries a dialog component (its mid-fight calls), which made `ClassifyEntitySyncCategory` tag it `InteractiveNpc` and exclude it from the puppet system → the client boss ran its own AI (shot the client), showed a local pose (gun instead of the walkie-talkie), and didn't follow the host. Fix: honour the game's own `UnitType.Boss` flag (`unitSO.unitType`) before the `DialogSpeaker`/`Speakable` check, so bosses always classify as `CombatEnemy`.
- **Visible:** the composite body is assembled by the boss's own intro chain (`OnStartInteractWithBoss` → intro anim → `TriggerFight`, which hides `sandSantaAnimationSprite` + sets `BossStarted`). Both the host-first start path (`TryApplyHostStart` → `OnStartInteractWithBoss`) and the client-first commit path (`TryApplyDialogCommit`, see Entry below) run the **full** `OnStartInteractWithBoss` — a bare `TriggerFight` flips `fightStarted` (health bar) but never assembles the body (Log294: health bar, no boss, no dialog).
- **Passive (no divergence):** the boss's per-frame combat AND phase transition are suppressed — `Desert_UpdatePhases_Pre` blocks `UpdatePhasesDeltaTime` / `UpdatePhasesFixedTime` / `TransitionTo` once started (`ShouldSuppressClientBossCombat`). `TransitionTo` is critical because `BossPhase` is a *separate* MonoBehaviour whose own `Update`→`TransitionTo`→`PhaseTransition()` sets the boss invulnerable, cleared only by the (suppressed) `UpdatePhases` — without blocking it the client half-transitions and freezes.
- **Position:** the intro's `RepositionBossFromCamera` places the rig 12 m in front of the camera. Suppressed on **both** ends (`ShouldSuppressClientBossReposition`) so the boss stays at its seed-placed arena position; the client boss (puppet) follows the host. Both-ends is required because the host can now run the intro for a client-triggered start while it is **out of the arena** — repositioning to the far host camera would drag the boss (and the following client puppet) out of the arena.
- **Entry (FF14 flow, incl. client-first):** the boss starts via the dialog-commit path (Desert is a dialog boss). `ApplyIntroCutsceneGated` is Cousin-only (`GatesFightOnDialogClose`); a non-gated boss (Desert) bypasses it and applies its real commit directly — otherwise the out-of-room branch force-calls Cousin's `Introduction` (absent on Desert) and the fight never starts (Log293). `TryApplyDialogCommit` runs `OnStartInteractWithBoss` on both ends, so each **runs its own intro chain** — assembling the body and opening the intro cutscene `Dialog_DesertClauseIntro` **locally** (so the intro is NOT network-synced; broadcasting it would double-open). The sandstorm pull-in is armed in the dialog-commit path (`BroadcastDialogCommitOnce` → `TryBeginSandstormArena`) so out-of-arena players (e.g. a host far from a client-first trigger) are teleported in.
- **Combat-entry (host-authoritative — the intro→combat handoff).** Desert enters real combat through `TriggerFight`, an **animation-event on the intro clip** (`base.TriggerFight` sets `fightStarted` + `StartBossPhases`); the sandstorm presentation + the release of the intro's `Cinematic` lock are two later anim-events (`Anim_OnTriggerSandstorm` → `StartSandstorm`). These fire only if the clip actually plays through locally. On a client whose local intro animation never reaches them — **host-first, where the client is out-of-arena and the boss renderer is culled so its Animator is frozen (Log298)** — `fightStarted` stays false forever, so `IsLocalIntroPresentationActive` (`_appliedStart && !fightStarted`) stays true, the boss's snapshot is skipped, and the boss is frozen out of the puppet system (no position follow, no combat, no sandstorm). Fix: make combat-entry a **second host-authoritative event**, distinct from the intro start. The host broadcasts its own `TriggerFight` once (`IsCombatEntrySource` → `BroadcastCombatEntryOnce`, on a **separate** `_hostCombatEntryBroadcast` gate — the intro's `OnStartInteractWithBoss` already consumed `BroadcastStartOnce`'s gate in the host-first case, which is why the host used to never re-broadcast). The client applies it (`HandleHostBossEncounterStart` detects a `TriggerFight` StartSource and runs it **even after** the intro start already applied) via `TryApplyCombatEntry` → invokes `TriggerFight` (idempotent) **and releases the `Cinematic` controller-lock + invulnerability** (mirroring the `StartSandstorm` tail we skip, so the local player isn't left frozen). `fightStarted` flips → the intro guard clears → the puppet resumes (host-driven position + host-authoritative combat). Client-first is a no-op (the client's own intro anim already fired `TriggerFight`, so `IsStarted` short-circuits). Trade-off: a host-first client does not replay its own 8 s sandstorm cutscene — it snaps to the host's current combat state (the in-room/catch-up model, consistent with the dialog-model divergence below).
- **Mid-fight dialog sync:** the boss opens mid-fight dialogs (airstrike / sniper / terminator) by setting `bossNPC.dialog.graph` then `bossNPC.Interact(null)`; the passive client never opens them itself. Host detects the open (`Npc.Interact` postfix → `OnHostBossDialogInteract`, gated on `SafeStarted`), identifies the graph **by name** (`TryGetActiveMidFightDialogId` — the NodeCanvas `dialog.graph` getter returns a bound runtime instance, so reference equality against the template field fails), and broadcasts `NetBossDiscreteEvent{EventName="Dialog:<id>"}`; the client sets the same graph + `Interact`. `DialogClose` is synced host→client via `TryFinalizeCurrentDialog`.
- **Intro-finish sync (client read → host commits).** The intro dialog `Dialog_DesertClauseIntro` is read locally on each end; its CLOSE was previously synced host→client only, so a **client** reading its intro to the end never told the host. Now `DialogController.SetCurrentSpeakable` is tracked on both ends (`OnLocalDialogSpeakableChanged`): a non-null speaker matched to a registered, not-yet-started `RunsLocalIntroPresentation` boss (root-compare speaker.unit vs `GetHealthUnit`) records `_localIntroDialogKey`; a null speaker = the local player finished. The **host** reading its own intro is a no-op (its native chain runs dialog → sandstorm → `TriggerFight` and drives clients via the combat-entry broadcast, preserving its presentation). A **client** finishing sends a dialog-commit request with `CommitSource="intro-finish"`; the host's `HandleClientBossDialogCommitRequest` runs `CommitHostIntroDialog`. **Key fact (Log301):** the sandstorm + `TriggerFight` are animation-events gated on each end's OWN intro dialog reaching **natural** completion — `Graph.Stop(true)` merely *aborts* the dialog and does NOT execute the tree's end action that resumes the intro, so simply closing the host's dialog leaves its native intro stuck (host frozen forever, no sandstorm). So instead of hoping the native chain resumes, the host drives the two presentation beats **host-authoritatively**: (1) close its dialog (`TryFinalizeCurrentDialog` → `DialogClose` broadcast); (2) invoke `Anim_OnTriggerSandstorm` → the real `StartSandstorm` runs the sandstorm and releases the `Cinematic` lock at its tail (the host watches the ~8 s cutscene then unfreezes — native), and its prefix broadcasts the sandstorm to clients; (3) invoke `TriggerFight` → `StartBossPhases`, flowing through the combat-entry gate → broadcast so every client enters combat. (`StartBossPhases` only arms the phase timer and neither it nor the first `DivingAround`/`SetBossPike` calls `StopAllCoroutines`, so the `StartSandstorm` coroutine survives to release the lock.) Guard: the client sets `_applyingHostDialogClose` while finalizing a host-driven `DialogClose` so its own `SetCurrentSpeakable(null)` isn't mistaken for a local finish and echoed back.

- **Sandstorm presentation sync (host-authored, mirrored — Log301 Direction B).** The arena-edge sandstorm (`Anim_OnTriggerSandstorm` → `StartSandstorm`) natively fires only on the end whose local player reads the intro to the end, so the host in a client-first start (player elsewhere) and any client preempted by combat-entry never played it. It is now a host-authored discrete event, exactly like the dismount: a **prefix** on `Anim_OnTriggerSandstorm` (both ends) — `OnLocalBossSandstorm` — dedups per key (`_sandstormSeen`: `StartSandstorm` runs at most once per end, whether from the native anim-event or a mirror invoke) and, on the **host**, broadcasts `NetBossDiscreteEvent{EventName="Sandstorm"}` once; a **client** applies it via `TryApplyDiscreteEvent` → invokes `Anim_OnTriggerSandstorm` (its prefix no-ops if this end already played it). So every end plays the sandstorm + releases its own `Cinematic` lock. `CommitHostIntroDialog` schedules `TriggerFight` ~`sandstormLoopLength` (≈8 s) after the sandstorm (via a coroutine on the boss MonoBehaviour), matching the native beat — firing it immediately (Log302) preempted the sandstorm with combat-entry before it spread (client saw no arena-edge storm) and ran `SetBossPike`'s `AttachToPike` concurrently with the sandstorm's bossAnimator triggers (boss not visibly on the pike). The phase-1 pike-riding position drift below still applies once combat starts.
- **Pike-riding body visibility (cosmetic clone, both ends).** In co-op the boss body's "Sprite" renderer (a **MeshRenderer** quad, not a SpriteRenderer) flakily gets stuck `enabled=false` while riding the pike: `DesertPikeCarrier.AttachUnit` disables it on mount and `JumpTowards` re-enables it on pop-out, but that re-enable is racy in co-op and **either** end can lose it (Log309 host, Log311 client — an earlier good client run was luck). Forcing the real renderer back on fights the native burrow cycle and loses (Log310: re-enabled 127×, still off). Fix (verified Log313): never touch the real renderer — `EnsureBossPikeVisual` mirrors it onto an **owned** clone (`CoopBossPikeVisual`) parented under the real Sprite's transform, copying mesh + materials + `MaterialPropertyBlock` (or sprite/flip/color for a SpriteRenderer source) each frame; the animator keeps driving the disabled renderer's data, so the clone animates. It burrows with the body (the parent GameObject toggles active) and is only enabled while the real renderer is off, so an end where native works is untouched. Driven once per frame from a `DesertPikeCarrier.Update` postfix (`UpdateBossPikeVisual`), both ends.
- **Phase actions — dismount (F4 Stage 3):** the native dismount is `pikeCarrier.onJump`→`OnBossJump` / `onLand`→`OnBossLand`(`Terminator`) — these fire only on the host. **Root cause of "stuck on the mount" (Log284):** `DesertPikeCarrier.AttachUnit` parents the boss body to a mount point, sets `animator.enabled=false`, and its `Update()` zeroes every attached unit's `localPosition` each frame. Host postfixes the three callbacks → `OnHostBossPikeDismount` broadcasts `NetBossDiscreteEvent{EventName="BossJump"|"BossLand"}`. Since the boss is now a host-position-driven puppet, the client's `TryApplyDiscreteEvent` only **severs the boss from the pike's `attachedUnits`** (else the pike keeps zeroing it — Log286: snapped to world origin) + reparents off the mount + re-enables the animator + toggles `JumpingOffPike`; the **puppet drive owns the descent** (following the host down the real jump arc). An earlier hand-rolled descent fought the puppet drive and flung the boss ~130 m away (Log290).

**Open / known divergences (registered per `BossSyncReuseMap.md` §5):**

- **Dialog model is a deliberate variant, not the Cousin RM-2b path.** `BossSyncReuseMap.md` §5.1's default is "the trigger opens the dialog; in-room players catch up; opt-outs respected." Desert instead has **each end run its own full intro chain** (the composite body must assemble via the intro on every end). It does not do RM-2b in-room *cutscene scoping* (an in-arena player who doesn't want the cutscene still gets it).
- **Intro-cutscene "native finish" sync — foundation cleaned, redo pending.** Goal (user spec): players read the intro at their own pace; when any player reads to the end, every other end still reading runs the **last step natively** (`OnDialogueFinished` → boss end presentation), instead of the current crude host→client `Graph.Stop` close. A first attempt (finish-event relay + per-frame `AcceptDialogOption` fast-forward) broke (Log297: host dialog re-opened, looped) because the **foundation was unstable**, not the sync: the client applied the boss start via TWO paths — `HostBossEncounterStart` → `TryApplyHostStart` AND the dialog commit → `TryApplyDialogCommit` (the host sends BOTH for a dialog boss: `BroadcastStartOnce` + `BroadcastDialogCommitOnce`), and `TryApplyDialogCommit` called `TryFinalizeCurrentDialog` (`Graph.Stop`) which **force-closed the intro dialog** before re-running `OnStartInteractWithBoss`; that force-close misfired the "dialog finished" detection and fast-forwarding fought the still-running intro chain. **Foundation fix (done):** (1) `_appliedStart` is now the shared start-applied gate on **both** client paths — `HandleHostBossDialogCommit` acquires it atomically and skips its intro/start apply if `HandleHostBossEncounterStart` already applied (and vice-versa), so a both-message dialog boss no longer double-applies; (2) `TryApplyDialogCommit` no longer finalizes the current dialog when `EnableFaithfulBossIntro` is on — the native intro chain plays its cutscene undisturbed. **Native-finish sync can now be redone on this stable base.** Interim: intro close stays `Graph.Stop` (works, not native, one-directional).
- **Phase-1 pike-riding position drift after combat-entry — DEFERRED.** Once the client enters combat (`TriggerFight` → `StartBossPhases` → `StartPhase(DivingAround)` → `InitDivingAround` → `SetBossPike`), the client attaches its boss to a **locally-spawned** `DesertPikeCarrier`, whose `Update()` zeroes the attached unit's `localPosition` + drives it along the perimeter each frame — fighting the host-driven puppet position (Log299: `maxErr≈132 m`, hundreds of snaps; client-first `avgErr≈13 m`). This is the same puppet-vs-pike conflict as the dismount (Log290), but now caused by the **native** phase machine rather than mod code. Not yet fixed. Two candidate directions: **(A, matches the committed puppet model)** suppress/sever the client's local pike-attach + perimeter movement so the puppet drive owns the position (note `SetBossPike` also does `animator.enabled=true` + `SetTrigger("AttachToPike")`, so it can't be skipped wholesale — sever after, à la the dismount `SeverBossFromPike`); **(B)** run the full native pike-riding on every end, keeping positions close via the shared seed (larger; risks gradual divergence).
- **Adds not synced (RT3):** runtime-spawned pikes / diggas are not mirrored; the client sees none. Should ride the RT3 `UnitSO.SpawnUnit` chokepoint (`HostBossDynamicSpawn`).
- **Phase index not synced:** `FillBossState` carries only health → `BossState` never sets the phase index → the client boss stays at phase 0 (cosmetic; combat is host-driven and suppressed anyway).
