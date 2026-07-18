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

### 5.1 As-built (shipped & verified — Independent mode)

The shipped implementation refines the §5 plan in three ways: XP is credited **deterministically to the
killer** (not by each end's pickup radius), the card panel locks movement only, and Endless enemy targeting
is fully distance-based and card-select-aware. All of the following is **host-authoritative** and gated on a
live Endless run; single-player and the host in non-Endless levels are untouched.

**XP — deterministic kill attribution (EM-5c).** The per-end orb idea in §5 was dropped: duplicating orbs
made total XP depend on differing pickup radii, and orbs visibly flying toward players who did not earn them
was confusing. Instead the host tracks, per Endless enemy, **who first-damaged it and who last-hit it**:

- Host hits are recorded from the `Npc.ReceiveDamage` host hook; client hits are recorded in
  `ProcessClientHitRequest` (the forwarded damage is attributed to the sending peer via a
  `HostApplyingHitPeer` flag so the same `ReceiveDamage` hook credits the client, not the host).
- On death (`OnEnemyDied`), the host resolves the attributed peer per the **`EndlessXpFirstDamage`** session
  setting (host-authoritative, connect-page toggle, **default = last-hit**) and broadcasts an award
  (`NetEndlessXpDrop` with `AwardPeerId`). The host's own vanilla orb spawn inside `OnEnemyDied` is
  suppressed so this is the single XP source.
- **Only the awardee's screen** spawns the real-value orbs; every other screen shows nothing (no
  "orbs-fly-to-me-then-vanish" illusion). The orbs are **force-collected**: once each orb finishes its
  vanilla spawn-burst (reaches the Idle state) it is flipped to the vanilla `Collecting` state regardless of
  distance, so it bursts at the death point then flies to the killer even on a long-range snipe. XP is
  credited by vanilla **as each orb is absorbed** (not instantly). Force-collect stamps `collectStartTime`
  with **`Time.time`** — the clock vanilla's attract-speed ramp (`Time.time - collectStartTime`) measures
  against; a `realtimeSinceStartup` stamp makes that term large-negative and the orb accelerates *backwards*.
- **bug1 (downed soft-lock):** a downed player's XP no longer opens the card panel — a `CheckXPThreshold`
  prefix skips while the local player is downed; the held XP fires normally on revive.

**Card selection — non-freezing, movement-only lock (EM-5 / EM-5c-fix).** The vanilla global
`SetTimeScale(0)` is undone immediately (the shared world keeps running for the other player). The selecting
player instead gets a brief local **invulnerability** bubble plus a **movement-only** lock
(`AddLock(PlayerLocks.PlayerMovement)`, which zeroes locomotion in `CalculateMovementDirection`). It must
**not** use `ModifyControllerLock`/any padlock — those route through `LockPlayerController`, which also locks
Camera + Weapon + Interaction and `InputReader.LockInput`, and card selection needs look (to hover a card) +
Fire/Interact (to confirm), so a padlock soft-locks the pick. Cleared on `CardSpinComplete` (plus safety
clears on leaving CardSelection / level change).

**Enemy targeting — distance-based + card-select-aware (EM-Target).** Endless enemies acquire targets from
`AiAgent.overridetargets` (host-only list rebuilt every `RefreshTargets`); a host postfix re-adds every
client's ghost unit so all players are candidates. Two refinements make the split fair:

- Vanilla `GetTarget` has a hardcoded **10 m host bias** (any override-target enemy within 10 m of the
  host's local `PlayerUnit` is forced onto the host). A host-only `GetTarget` **prefix**, scoped to a live
  Endless run, bypasses the bias and returns the game's own distance pick `OverrideTarget.GrabHostileUnit`
  (`Closest`) directly — nearest player, host & client symmetric. The generic `BalanceCoopEnemyTargeting`
  hostilesInLOS re-pick is skipped for Endless (it does not exclude invulnerable players).
- A **card-selecting player leaves every enemy's aggro.** `GrabHostileUnit` already excludes invulnerable
  units, and the selector is invulnerable during the pick, so the **host** selector drops off for free (its
  sticky Closest cache is invalidated when the cached target is now-invuln so enemies redirect to another
  player instead of freezing). For a **client** selector — whose ghost lives on the host — the client sends
  `EndlessCardSelect` (client→host) on entering/leaving the pick, and the host marks that client's ghost
  invulnerable, so the same exclusion applies.

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

### 7.1 As-built (EM-6b-3a — shipped, pending real-machine test)

The shipped card vote is a **dedicated N-option protocol** (`EndlessCardVoteManager` + `NetEndlessCardVote`),
kept separate from the binary `CoopVoteManager` rather than generalizing its codec — the tally is per-card, the
inputs are aim-based, and the feedback lives on the 3D cards, so a bespoke channel was cleaner than perturbing
the binary path. Both ends cast through the existing card-pick prefix (aim at an ordinary card + Fire/Interact →
one vote, re-castable); the host owns the tally and broadcasts a snapshot (`EndlessCardVoteState`, msg 93) of who
voted for which index; clients forward casts (`EndlessCardVoteCast`, msg 94). Resolution is **majority wins, tie →
host roll, nobody-voted → host uniform roll**, resolving early once everyone has voted. The resolved index is
applied on **both ends** via the vanilla `SpinAndDismissCard`, so personal card effects land on each `PlayerUnit`.

Refinements vs. the §7 sketch:

- **The countdown starts only after the first cast** (user decision): early on, players may deliberate
  indefinitely, so an un-cast vote never expires. (The binary `CoopVoteManager` is unaffected — its initiator
  always casts immediately, so its clock already started at t0.)
- **Feedback is on the cards, not the center square.** Each voter's name is stamped on the card they chose
  (cloned from the card's own subtitle TMP so it inherits the font/orientation/motion), rubber-stamp style with a
  per-voter random tilt; the local player's stamp is gold ink, teammates' red. The stamp size is the subtitle's
  auto-size floor (`fontSizeMin`) — the subtitle auto-sizes so `fontSize` returns its large max, which ballooned a
  short name ~10×. A small bottom-centre status line carries the prompt / waiting (`voted/total — secs`) / resolved.
- **A tie is not instant.** A tie / no-vote host roll would snap the winner in immediately, which reads as "my vote
  was ignored"; instead both ends play a short decelerating **raffle sweep** across the cards (the overlay pulses
  the cursor card's scale, landing on the winner) before the pick applies. A clear majority applies at once (the
  winner is obvious from the stamps). Carried by `ResolvedByRoll` on the snapshot; the sweep is a deterministic
  easeOut over the votable cards, so no RNG needs to travel.
- **Scope:** in 6b-3a only the ordinary reward cards are votable; the static Skip/Reroll cards and the per-card
  banish are deferred. **EM-6b-3b (below) makes Skip + Reroll votable**; the banish is still deferred (EM-6b-3c).
- **Known gap (EM-7):** a resolved **world card** (companion / shop NPC) runs `ExecuteReward` on both ends and so
  spawns twice — world-card rewards still need routing to a single authority. Personal cards are correct per-player.

`ProtocolVersion 18→19`. Localization row 31 (`endless.cardvote.*`).

### 7.2 As-built (EM-6b-3b — Skip + Reroll votable, shipped)

The vote is extended from the ordinary reward cards to the **whole panel**. The votable set is every *interactable*
card (`EndlessCardManager.GetVotableIndices` reads `FloatingCard.Interactable`), so an exhausted **0-reroll card is
excluded** — `EndlessModeManager.ConsumeReroll` has no floor, and a dead reroll must never be a tie/no-vote roll
target. `HostOpenVote` now receives the full card count plus that votable-index array; casts and the nobody-voted
uniform roll are both restricted to it. `TryReadAimedVotableCard` (renamed from `…OrdinaryCard`) drops the
static-card rejection and instead requires `Interactable`; the DismissButton banish is still ignored (EM-6b-3c).

Resolution branches by the resolved card:

- **Skip** — both ends run vanilla `SpinAndDismissCard(skipIndex)`. The skip reward is `PassForStamps` (personal),
  so each player gets their own stamps and the panel closes; symmetric, same as an ordinary personal card.
- **Reroll** — **host-authoritative**. The host runs the vanilla spin, whose terminal `SpawnCards` re-rolls the
  panel and, through the existing 6b-1/6b-2 chain (`SpawnCards` prefix → `HostCaptureRoll` broadcast, then the
  manifest + a fresh `HostOpenVote`), issues a new roll/manifest/vote under a new `CardEventId`. The **client does
  not** run that spin — its terminal `SpawnCards` would roll a divergent local panel and could collide with the
  incoming host roll mid-coroutine; instead `ApplyResolvedPick` spins the reroll card for feedback and keeps
  `ClientRollActive` set so the host's new roll tears the panel down and swaps in the authoritative one via
  `ApplyCardRoll`. A `FloatingCardManager.SpawnCards` **client prefix** additionally suppresses *any* non-replay
  client card spawn in Shared mode (only the host-driven replay, which sets `ClientRollActive` first, is allowed).

The status HUD names the Skip/Reroll outcome (`DescribeResolvedCard` → `endless.cardvote.resolved_skip` /
`…_reroll`) rather than a card number. `ProtocolVersion 19→20` (vote semantics changed; the wire is unchanged).

### 7.3 As-built (EM-6b-3c — per-card banish vote, shipped)

Banishing a card is the third vote action, implemented **inside the Endless card-vote channel** rather than via
`CoopVoteManager`. That reuse was the original sketch (§6), but a review found `CoopVoteManager` is payload-less
(votes a `VoteKind` enum only — a banish needs a target card index), casts on **Y/N keyboard** through a separate
top-center panel (clashing with the aim + Fire card input), is single-vote-at-a-time, and its Majority rule has
never run in-game; the Endless channel already has a host tally, participants, a realtime clock that survives the
frozen card-select world, and the on-card overlay. So the banish rides the existing channel.

Per the user's revision the vote is **unified**: every player casts exactly **one** vote — pick a card
(ordinary/Skip/Reroll) OR banish a card — and any option is a **toggle** (re-cast to retract).

- **Input:** aiming at a card body + firing is a pick; aiming at a card's dismiss button + firing is a banish. The
  game only shows that button on a non-static, non-`TravelBackToChurch` card while shared banishes remain, so the
  gate is natural; the host re-validates. One vote per player — a pick clears any banish and vice versa.
- **Resolution:** count every cast option (a pick of card *i* and a banish of card *i* are distinct options), take
  the most-voted, break a tie by a single host roll across **all** tied options (so a 4-way pick/Skip/Reroll/banish
  tie is one uniform draw).
- **Draw:** the tie-break animation is **host-authoritative and reused for every rolled outcome and every banish** (a
  clear pick applies at once). The host plays a short draw with the winner hidden — a multi-card sweep, or a
  single-card blink for a clear banish / a same-card pick-vs-banish tie — broadcasts it via the snapshot's
  `RaffleActive` flag + `TiedIndices`, and both ends render it. The on-card stamps stay visible through the draw,
  which is what lets the *last* banish vote's ✕ stamp show before its card leaves. When the host timer elapses it
  applies the winner.
- **Apply:** a **pick/Skip/Reroll** winner resolves the panel (both ends run `SpinAndDismissCard`). A **banish**
  winner runs the vanilla `DismissCard` (consumes one shared banish, updates the pools that feed future rolls via the
  roll-state replay, animates the card out; each client mirrors via `ClientMirrorBanish` = StartDismissal +
  `RemoveCardAfterDismissal` + local banish decrement + text refresh, retried if the panel isn't up yet) and
  **re-opens the same vote on the remaining cards** — all votes cleared (Skip/Reroll are never banishable, so the
  panel can't empty). The winning card's ✕ stamp is released (not destroyed) so it rides the dismissing card out.
- **Countdown:** runs only while at least one vote is cast — retracting to zero votes stops it, and the next cast
  restarts it from full.
- **Protocol:** `NetEndlessCardVoteCast` gains a `Kind` byte (0 pick / 1 banish); `NetEndlessCardVoteState` gains a
  per-participant `BanishIndex`, a cumulative `BanishedIndices[]`, and a `RaffleActive` flag (codecs → v3; no new
  message type).
- **Feedback:** a violet **✕ NAME** stamp on the card each player is voting to banish (distinct from the gold/red
  pick stamps). `ProtocolVersion 20→22`.

---

## 7.5 Card-effect co-op audit (EM-7 basis)

`FloatingCardManager.ExecuteReward(reward, preselectedItem)` is the single apply chokepoint. In **Shared**
mode the vote applies it on **both ends**; in **Independent** mode each player applies its own pick. That
"both ends run it" is why personal cards already work and world cards duplicate. Categories:

- **Personal (already correct — each end applies to its own `PlayerUnit`):** `ApplyBuff`
  (`ExistingBuff` / `PermanentModifier`), `GiveResource`, `TriggerEvent.PassForStamps`,
  `TriggerEvent.RepairAll`.
- **Manager-flag (writes `EndlessModeManager` fields; the client's manager is a slave):**
  `InfiniteAmmo` / `Indestructible` are set on both ends (read per-player locally), **but their expiry
  runs inside `StartEnemySpawning`, which the client suppresses — so they never expire on the client**
  (`currentWaveIncludingLoops = currentWave + loopCount*maxWaveInData` is synced, only the expiry branch
  is missing). `MeleeXPBonus` is read in the host `OnEnemyDied` → does not cross ends (known gap).
  `ammoGainMultiplier` is set on both (correct). `IncreaseXPRange`/`IncreaseXPAmount` write
  `xpOrbManager.pickupRadius`/`xpMultiplier`; under host-authoritative force-collect XP the client copy
  is likely inert — audit pending.
- **World (duplicate/diverge — the EM-7 target):** `SpawnFromLootTable`, `SpawnInteractable`
  (HiddenChest / ServiceStation / Container), `SpawnRandomAllies` (companion —
  `ApplyForcedCharmed(local PlayerUnit)`, so under host-only spawn it follows the host, not the picker),
  `SpawnNPC` (shop), `TriggerEvent.ExplosiveBarrels` / `Bonanza` / `TravelBackToChurch` (level transition).

Observed symptom root cause: **loot shows two visible copies** because `SpawnFromLootTable` → `SpawnPickup`
is the WID chokepoint, and both ends spawn+broadcast their own; **companion/shop/chest are per-end local**
because `SpawnUnitAsync(FloatingCardManager,…)` / `Instantiate` owners are not recognized by
`RuntimeSpawnManager.ClassifyOwner` (only `EndlessModeManager` is), so they are never broadcast/mirrored.

### 7.6 As-built (EM-7a — card loot single authority, shipped)

First EM-7 slice, Shared mode. The loot-table reward's **plain-Pickup path** (`containerPrefab == null`) is
deduped: the client suppresses its own `ExecuteReward` copy (the host's arrives via the WID mirror) and the
host tags its spawn (`WorldPickupManager.EndlessSharedLootContext`) so the pickup mirrors **regardless of the
SharedLoot toggle** — card loot has no `inventoryData` and would otherwise fail the Independent-mode WID
filter, leaving the client with nothing. `ExecuteReward` is fire-and-forget (not awaited), so the client
prefix returns a completed `Task`; the loot branch has no await, so the host postfix clears the tag after the
pickups spawn. No protocol change. Container-path loot, companions, shop NPCs, interactables, and the
`lootLightEffect` spawn-locator beam are later slices. `ProtocolVersion` unchanged.

### 7.7 As-built (EM-7b — loot-locator beam, shipped)

`FloatingCardManager.lootLightEffect` is a single shared light pillar that marks where a card spawned loot /
chests / NPCs (moved + activated per spawn, turned off at the next arena transition). It is host-authoritative
in Shared mode: the client suppresses its own `SpawnLootLightEffect` and the beam's active state + position ride
the existing `NetEndlessWaveState` snapshot (codec v2, reliable) — so it appears, moves, and disappears in sync
with the host. Because it is a single object, this is decoupled from spawn-mirroring (it works even for spawns
that aren't mirrored yet). Independent mode keeps each player's local beam. Note the vanilla beam sits at the
loot **spawn** position while the item settles ~1 m away and the client mirror glides to the settled spot
(WID-3) — a small, host-consistent offset. `ProtocolVersion 22→23`.

### 7.8 As-built (EM-7c — companion mirror + charmed presentation, shipped)

A `SpawnRandomAllies` card spawns one host-authoritative ally, mirrored to the client as a puppet (via the
existing RuntimeSpawn pipeline) instead of one per screen. `FloatingCardManager` is classified in
`RuntimeSpawnManager.ClassifyOwner`, but **only companion spawns** are mirrored — the host brackets
`SpawnCompanion` with a depth counter so shop NPCs (which share the owner via `SpawnNPC`) are left per-end for
EM-7d. The client suppresses its own companion spawn (the EM-7a world-reward `ExecuteReward` prefix, extended).
A companion is a **charmed** ally on the host (`ApplyForcedCharmed` = heart symbol + allied faction); the
position/animation puppet mirror doesn't carry that, so — since `NetRuntimeSpawn.Source` already travels on the
wire (`"EndlessCompanion"`) — the client re-applies the charmed presentation on the mirrored puppet (its AI is
puppet-suppressed, so this is visual only). No protocol change for companions. **Open:** the companion charms to
the host's `PlayerUnit`; "follows the picker" only has a well-defined picker in Independent mode (deferred).

### 7.9 As-built (EM-7d-1 — shop NPC mirror + client setup, shipped)

A `SpawnNPC` shop card spawns one host-authoritative vendor, mirrored to the client (RuntimeSpawn puppet). Since
`SpawnNPC` is inline in `ExecuteReward` (no method to bracket like `SpawnCompanion`), the host brackets
`ExecuteReward` with a depth counter — its first `await` is the shop's `SpawnUnitAsync`, observed synchronously by
`NotePendingSpawn` — so only shop spawns classify as `"EndlessShop"` (a multi-NPC card mirrors only its first). The
client suppresses its own shop spawn, and because that also suppresses the host's post-spawn wiring, on mirror the
client registers the `UnitInteractable` and runs each `ServiceStation.DoSetup` so the shop is openable + usable.
Per the user's decision this ships as **one host-authoritative NPC with local (independent) purchases**. **Stock:**
`DoSetup` rolls the vendor table via `UnityEngine.Random`, so a *random* `ShopKeeper` would diverge between ends (a
host-authoritative stock broadcast, EM-7d-2, would be added then); a *deterministic* vendor (e.g. a named trader)
matches for free. No protocol change (reuses RuntimeSpawn + `Source`).

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
- ~~**XP fairness in Independent mode:** duplicating orbs per-end means total XP earned differs from
  single-player pacing only if pickup radii differ — acceptable? Or credit XP directly on kill?~~
  **Decided & shipped:** deterministic kill attribution — XP goes to the first-damager or last-hitter (host
  toggle, default last-hit); the orb flies to the killer and credits on absorption. See §5.1 (EM-5c).
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
