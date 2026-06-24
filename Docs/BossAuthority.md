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
| Desert | `BossFightHelperAdapter` (generic) | main | — | — | — |

---

## 7. Files

`src/Networking/Gameplay/Boss/`: `NetBossEncounterManager.cs` (hub), `NetBossEncounterMessages.cs`, `IBossEncounterAdapter.cs` / `BossAdapterBase.cs`, the per-boss adapters, `BossReflect.cs` / `BossDamageReflect.cs` / `BossDialogReflect.cs`, `BossDynamicSpawnManifest.cs`, `BossLifecycleProbe.cs`, `WitchPhase2Probe.cs`, `EmperorWormDiagnostics.cs`, `BossTypeDiscovery.cs`. Patches: `BossEncounterPatches.cs`, `BossSpawnPatches.cs`. Runtime spawn: `RuntimeSpawnManager.cs`, `NetRuntimeSpawn.cs`, `BossSpawnPatches.cs`.

---

## 8. Known gaps

- Witch Phase 6 egg; precise Phase 2 `EightFlying` timing; black-ripple VFX; phase-witch positions (mostly OK, occasionally off).
- Emperor phase/death is diagnostic-only (extension points present).
- Boss adds: RT3 covers boss-mono adds; non-boss-mono adds (e.g. Witch egg whose `mono` is GameManager/phase) go through the general per-unitId-seq hook (RT3b).
- Host hitting a boss while the client watches: a minor "no feedback on client" cosmetic gap was deferred.
