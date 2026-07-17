# Endless Mode — Co-op Sync Audit & Plan

Status: **design / not yet implemented.** This document records the vanilla implementation of Endless
Mode, why it desyncs in co-op today, and the planned host-authoritative synchronization split.

---

## 1. Vanilla implementation (facts from decompilation)

Assembly: `PerfectRandom.Sulfur.Core`. Entry: `WorldEnvironmentIds.EndlessMode`.

### 1.1 `EndlessModeManager` — the brain

A **scene-local `MonoBehaviour` singleton** (`Instance`) that lives inside the Endless arena. It `Awake`s
when the arena loads and drives the whole mode. Key state:

- `seed` (`uint`) = `(uint)GameManager.Instance.currentSeed`, captured in `Awake`.
- `gameplayRandom` (`Unity.Mathematics.Random`), re-seeded per burst by
  `SeedForBurst(i)` → `seed + i * -1640531527`.
- `waveData` (`EndlessModeWaveDataSO`): a static table of `BurstData` (stage, wave, burstNumber,
  conditionType, timer/percentage, count, `enemyId`, `spawnType`, `mutation`). Identical on both ends.
- `currentStage`, `currentWave`, `currentBurstIndex`, `loopCount`, `transitionState`.
- `spawnedUnits` / `allSpawnedUnits` / `spawnedBossUnits` / `spawnedBosses`.
- Progression: `currentXP`, `currentCardLevel`, `nextCardThresholdXP`, `BanishesRemaining`,
  `RerollsRemaining`, plus per-run flags (`infiniteAmmoActive`, `indestructibleActive`,
  `meleeXPBonusActive`, `explosiveBarrelsActive`, `ammoGainMultiplier`).

### 1.2 Arena selection (deterministic on seed)

`Awake`: `arenaPrefabs[gameplayRandom.NextInt(0, arenaPrefabs.Count)]` → `InstantiateArena` (adds a `Room`
to `graphContext.orderedRooms`, builds a catch cube). Player spawn = `GetRandomPlayerSpawnPoint()` (also
`gameplayRandom`). Stage transitions pick a new arena the same way (`lastUsedArenaIndex` avoids repeats).
**All arena choices are pure functions of `seed` → identical on both ends iff `currentSeed` matches.**

### 1.3 Wave state machine (`Update`)

```
WaveActive → (XP threshold) CardSelection → RewardCollection → WaveActive
WaveActive → (stage complete) ArenaTransition → WaveActive
```

- Wave advance judged by `GetKillPercentage()` over the **local** `spawnedUnits` list + a
  `allBurstsSpawnedForCurrentWave` flag.
- `StartEnemySpawning` walks `waveData.allBursts` from `currentBurstIndex`, honoring each burst's
  `WaitForBurstCondition` (Timer / PercentageKilled / PercentageKilledOrTimer), then `SpawnBurst`.

### 1.4 Enemy spawning (`SetEnemy`)

`SetEnemy(enemyId, spawnType, mutation)`:
1. `GetSpawnPosition(spawnType)` — **NOT purely seed-deterministic**. It filters spawn points by distance
   from **the live player's position + velocity** (`GetSpawnPosition`, `GetSpawnPositionRelativeToPlayer`
   read `GameManager.PlayerUnit.transform.position`, `Rigidbody.linearVelocity`, camera forward) and by
   `usedSpawnPointsThisBurst`. Random index draws use `gameplayRandom`.
2. `unit = await unitSO.SpawnUnitAsync(this, spawnPosition)` — **the owner MonoBehaviour is the
   `EndlessModeManager`**. This is the same `UnitSO.SpawnUnitAsync` → static `UnitSO.SpawnUnit` chokepoint
   we already hook (`BossSpawnPatches` → `RuntimeSpawnManager` / `BossDynamicSpawnManifest`).
3. Post-spawn: forces AI (`onlyTargetPlayer`, `useLineOfSight=false`, `GiveUnitPlayerPosition`), overrides
   faction to `Corrupted`, applies mutation, `ApplyLoopScaling` (per-loop HP/size/speed/damage), registers
   an `onDeath` → `OnEnemyDied`.

**Consequence:** even with an identical seed, spawn *positions* diverge because they depend on live,
per-client player motion. Spawn *selection order and unit types* are seed-deterministic; positions are not.

### 1.5 Progression (single-player assumptions everywhere)

- **XP:** `OnEnemyDied` spawns XP orbs at the corpse via `xpOrbManager.SpawnOrb`; the player absorbs them by
  pickup radius. `CheckXPThreshold` → `currentCardLevel++` → `StartTransition(CardSelection)`.
- **Card selection:** `StartTransition(CardSelection)` calls `cardManager.SpawnCards()` **and
  `GameManager.SetTimeScale(0f)`** (freezes the entire game), prevents shooting, clears fullscreen effects.
  This global freeze is fundamentally incompatible with a shared physics world (see Phase 5.7-NP).
- **Card effects (`FloatingCardManager`)** split into two categories:
  - **Personal** — `ApplyBuff` (endless buffs on `PlayerUnit`), `PermanentModifier` (stat mods on
    `PlayerUnit`), item spawns for the player, infinite-ammo / indestructible / XP-multiplier / melee-XP.
    Per invariant §16.2 these are **client-owned personal state**.
  - **World** — `SpawnRandomAllies` (companions), `SpawnNPC` (shop NPCs → `ServiceStation`, interactables),
    `SpawnInteractables` (loot/supply, `HiddenChest`), explosive barrels. These are **shared world objects**
    → host-authoritative.
- Records (`EndlessStage/Wave/Level`) and resume (`EndlessRunSaveData`) go through `PlayerProgress` /
  `SulfurSaveState`. **We do not touch saves** (established boundary, Phase 5.4-A).

---

## 2. Current co-op breakage

`EndlessModeManager` runs **independently on both ends** (it is a plain scene MonoBehaviour that `Awake`s
wherever the arena loads). `RuntimeSpawnManager.ClassifyOwner` does **not** recognize `EndlessModeManager`,
so its spawns are never broadcast/suppressed. Result: two unrelated Endless runs overlaid on one scene —
divergent arenas (unless seed already matches), divergent enemies, divergent wave/stage, divergent cards.
Nothing is shared.

---

## 3. Design overview — two modes via a host session toggle

Per the user decision, mirror the existing **independent-loot vs shared-loot** pattern: a
host-authoritative session setting selects the progression model. **Default = Shared.**

- New `NetSessionSettings` flag `SharedEndlessProgress` (host-owned; same plumbing as `SharedLoot` /
  `DeveloperMode`; broadcast on change with the standard two-end toast). Bump `ProtocolVersion` (10 → 11).
- The **world layer (Part 4) is common to both modes** and is always host-authoritative.
- The **progression layer differs**: Independent (Part 5) vs Shared (Part 6).

```
                    ┌─────────────────────────────────────────────┐
   COMMON WORLD ──▶ │ arena (seed) · wave state machine · enemy    │  host-authoritative
   (both modes)     │ spawns · loop scaling · world-card rewards   │  client = slave/mirror
                    └─────────────────────────────────────────────┘
                    ┌──────────────────────┬──────────────────────┐
   PROGRESSION ──▶  │ INDEPENDENT          │ SHARED (default)      │
                    │ per-player XP/level/ │ shared XP pool +      │
                    │ cards; NO freeze;    │ level; GLOBAL pause;  │
                    │ local card panel     │ 1-of-N card vote      │
                    └──────────────────────┴──────────────────────┘
```

---

## 4. World layer — host-authoritative (common to both modes)

Goal: both players fight **the same arena, the same waves, the same enemies**. The client's
`EndlessModeManager` becomes a **slave**: it must not select its own arena, must not spawn its own enemies,
must not advance its own wave state.

### 4.1 Arena parity (seed)

Verify at runtime that `GlobalSettings.ForceLevelSeed` (already applied by `NetLevelSeed` on the client for
followed levels) is set **before** the Endless arena loads so `GameManager.currentSeed` matches on both
ends. If it matches, `Awake` arena selection and `SeedForBurst` streams are identical for free. **First
implementation step is to confirm this parity with a log probe** — everything else assumes it.

### 4.2 Slave manager

On the client (when `BossMode == Client` and the level is EndlessMode):
- Block the local wave driver: prefix `EndlessModeManager.Update`'s state-machine section (or the burst
  loop entry) so the client neither advances bursts/stages nor decides transitions locally. The client's
  `transitionState` is driven by a host broadcast (see 4.4).
- Suppress local enemy spawns: `SetEnemy` → `SpawnUnitAsync(EndlessModeManager, …)` must not create a
  local unit on the client. Two options (decide at build time):
  - **(A) Suppress + mirror** — client's `SetEnemy` is prefixed to no-op; enemies arrive only via the
    host runtime-spawn broadcast (below). Cleanest; reuses the puppet pipeline end-to-end.
  - **(B) Deterministic co-spawn** — both ends spawn from the shared seed and the client suppresses only
    duplicated side-effects. **Rejected**: positions are player-motion-dependent (§1.4), so co-spawn
    diverges. Use (A).

### 4.3 Host enemy spawn broadcast (reuse RuntimeSpawn)

Add `"Endless"` to `RuntimeSpawnManager.ClassifyOwner` (owner type name `EndlessModeManager`). Then the
existing pipeline already:
- HOST: `NotePendingSpawn` (async prefix) → `OnUnitSpawned` (static postfix) → `BroadcastHostRuntimeSpawn`
  (unitId + world pos + rotation + host `SpawnIndex`).
- CLIENT: `HandleHostRuntimeSpawn` → `MirrorSpawnAsync` → binds host `SpawnIndex` ↔ local puppet so host
  state / attacks / death drive it (the same EnemyPuppet path bosses and DevTools spawns use).

Loop scaling (`ApplyLoopScaling`) runs on the host's real unit; the puppet mirrors host HP/state, so the
client does not need to re-apply it. Verify the puppet reflects scaled HP (it drives from host health
state already).

### 4.4 Wave/stage/transition sync

The host owns `currentStage / currentWave / currentBurstIndex / loopCount / transitionState`. Add a small
host→client broadcast (**new msg `EndlessWaveState`**) sent on every transition change (and a low-rate
keepalive), carrying those fields + a revision. The client applies it to its slave manager so its UI
(stage/wave/XP-bar labels) and its gating match the host. Wave-advance is judged **only on the host** from
the host's `spawnedUnits` kill percentage.

### 4.5 World-card rewards (companions / shop / interactables / barrels)

These are created via `SpawnUnitAsync(FloatingCardManager, …)` and `Instantiate` of interactables. They are
shared world objects:
- Enemy/ally/NPC spawns already flow through the `UnitSO.SpawnUnit` chokepoint → extend the owner
  classification to `FloatingCardManager` so they broadcast + mirror like §4.3.
- Non-unit interactables (loot stations, `HiddenChest`) need their own host-authoritative spawn mirror OR
  are deferred to a later phase (they are lower-stakes; see Open Questions). In **Shared** mode these are
  triggered once (host) and mirrored. In **Independent** mode, world-card rewards still come from a single
  authority — see Part 5.4.

---

## 5. Progression layer — Independent mode

Each player has their own `currentXP`, `currentCardLevel`, card picks, and personal buffs/mods
(consistent with §16.2). No global freeze.

- **XP:** the *world* enemy set is host-authoritative (puppets on the client). Enemy death fires on the
  host; the client's puppet death does not run `EndlessModeManager.OnEnemyDied` (its manager is a slave and
  never registered `onDeath`). So each client must be given XP-orb credit for kills locally. Plan: on each
  end, spawn that end's **own** XP orbs at the (mirrored) death position so each player collects their own
  XP by their own pickup radius. This keeps XP fully personal and needs no XP wire messages.
- **Card selection:** replace the global `SetTimeScale(0f)` with a **local, non-freezing** card panel for
  the selecting player only — the world keeps running for both. To avoid the selecting player being killed
  mid-pick, grant that player a brief local slow / invulnerability bubble (local presentation only, never
  authoritative). Only the *personal* card effects apply (buffs/mods on the local `PlayerUnit`).
- **World-card rewards in Independent mode:** a personal card that spawns a *world* object (companion, shop)
  must still be created by a single authority to avoid divergence. Route the request to the host (host
  spawns + broadcasts via §4.5); the reward is "owned" by the requesting player for targeting but exists
  once in the shared world.

---

## 6. Progression layer — Shared mode (default)

One shared XP pool and one shared `currentCardLevel`. Card selection uses a **global pause for everyone**
and a **1-of-N card vote**.

- **XP:** host owns the single `currentXP` pool; enemy kills (host-authoritative) add to it. Host owns the
  threshold check → when reached, host initiates the shared card event. Broadcast the XP/level in the
  `EndlessWaveState` snapshot (§4.4) so both bars agree.
- **Global pause:** unlike normal co-op (Phase 5.7-NP forbids pause), the shared card event **intentionally
  pauses both ends** — host drives `SetTimeScale(0)` locally and tells the client to do the same for the
  duration of the vote. This is a bounded, host-authoritative pause window, distinct from the accidental
  single-player pauses NP blocks.
- **Card vote (new mechanism):** the current `CoopVoteManager` is binary (Agree/Decline). Card pick is
  1-of-N, so add a **new shared-choice mechanism** (Part 7). Outcome = one card index, applied on both
  ends: personal effects apply to each `PlayerUnit`; world effects spawn once on the host (§4.5).

---

## 7. New shared-card-pick mechanism (Shared mode only)

A dedicated host-authoritative "shared selection" protocol, modelled on `CoopVoteManager` but with an
integer option set instead of Agree/Decline:

- Host opens a selection: `N` card options (the drawn cards, seed-deterministic so both ends can render the
  same faces from the shared RNG), a countdown, and a tally of who has picked which index.
- Each player casts an **index** (`0..N-1`); host records it. **Resolution rule (decided): majority wins;
  on a tie the host randomly picks one of the tied indices.** The host owns the tally and the tiebreak roll
  (host-authoritative RNG), then broadcasts the single resolved index so both ends apply the same card — the
  client never rolls. AFK / no-cast at the deadline: count as no vote (does not create a fake tie); if
  nobody voted, host rolls uniformly over all N.
- Reuse the vote overlay visuals; per the user, the **center square shows the card number ("第 K 张卡")**
  rather than an Agree/Decline glyph. This is a new overlay variant, not a reskin of the existing one.
- New messages (`EndlessCardSelectStart` / `EndlessCardCast` / `EndlessCardState` / `EndlessCardResolved`)
  or a generalized "multi-choice vote" that both this and any future N-way vote can share. Prefer a small
  generalization of the vote codec (add an `OptionIndex` + `OptionCount`) over a bespoke protocol, but keep
  it a **separate `VoteKind`** so it does not perturb the binary path.

---

## 8. Phasing

Each phase builds + is independently verifiable. Tag commits with an internal phase label (see
Docs/Versioning.md) and register in the ledger.

1. **EM-0 Seed parity probe** — confirm `currentSeed` matches on both ends at Endless entry (arena parity).
   No behavior change; log only.
2. **EM-1 Slave manager + spawn suppression** — client `EndlessModeManager` stops selecting arena / driving
   waves / spawning enemies locally.
3. **EM-2 Host enemy spawn mirror** — classify `EndlessModeManager` in `RuntimeSpawnManager`; client
   mirrors host enemies as puppets. (Depends EM-1.)
4. **EM-3 Wave/stage state sync** — `EndlessWaveState` broadcast; client UI + gating follow host.
5. **EM-4 Session toggle** — `SharedEndlessProgress` setting (+ toast, +ProtocolVersion bump). Default
   Shared. Wire the two branches.
6. **EM-5 Independent progression** — per-player XP orbs, non-freezing local card panel, world-card
   requests routed to host.
7. **EM-6 Shared progression** — shared XP pool, host-driven global pause, and the new 1-of-N card vote
   (Part 7) + overlay variant.
8. **EM-7 World-card rewards** — companions / shop NPCs / interactables spawn once (host) + mirror; loop
   scaling parity check.

Minimum shippable slice = EM-0..EM-4 with **one** progression branch. Given default = Shared, EM-6 is on
the critical path; EM-5 can trail.

---

## 9. Open questions / risks / decisions still needed

- ~~Shared card resolution rule~~ **Decided:** Independent mode = each player picks their own card. Shared
  mode = majority wins; tie → host randomly picks one of the tied indices (host-authoritative roll,
  broadcasts the resolved index). See Part 7.
- **XP fairness in Independent mode:** duplicating orbs per-end means total XP earned differs from
  single-player pacing only if pickup radii differ — acceptable? Or credit XP directly on kill?
- **Non-unit interactables** (loot stations, hidden chests from cards) — mirror now or defer? They do not
  go through `UnitSO.SpawnUnit`, so they need a separate spawn-mirror or a deferral decision.
- **Bosses in Endless** (`spawnedBossUnits`, `AttachBossToUI`) reuse the existing boss puppet/health path;
  verify the Endless boss progress bar binds to host health state.
- **Player-motion-dependent spawn positions** are resolved by host authority (host's motion decides), but
  the client sees enemies appear relative to the *host's* position, not its own. Accept as a consequence of
  host authority (same tradeoff as all host-spawned enemies), or add a "spawn near nearest player" tweak on
  the host later.
- **Time-freeze vs Phase 5.7-NP:** the shared-mode pause must be a whitelisted, host-authoritative window
  so the NP pause-prevention patches don't fight it. Confirm the interaction before EM-6.
- **Save/records:** unchanged; we do not touch `EndlessRunSaveData` / `PlayerProgress`. Resume of a
  co-op Endless run is out of scope.
