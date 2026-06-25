# Standing-Still Enemy — Debug Log & Current State

**Symptom (recurring):** on the client, an enemy stands frozen in place (often "the last enemy of the
fight"). The host's copy may be moving/attacking; the client's puppet just stands there. This is a
*family* of bugs with several distinct root causes — fixing one reveals the next. This file is the
running record so we don't re-derive it each time.

Scene used for most repros: the multi-sublevel **Act_01_Shanty** Blackguild fight (lots of enemies,
client roams far from a stationary host). Two instances on one PC (host real profile + client in
Sandboxie), both on the `开发` Gale profile.

---

## Resolved causes (in the order we found them)

### 1. Snapshot starvation via the distance interest throttle — FIXED (RB2→RB4)
A *bound* enemy a far-from-host client was fighting froze because the host throttled its position
snapshots (`ShouldSendByInterestManagement`) by distance to the **host** player, and the remote-player
interest feed was broken (it reported the host's own position — `[InterestFeed]` showed
`distRemoteMin == distHost`). **Fix = `SendAllEnemySnapshotsToClients` (RB4, default ON):** while a
client is connected the host does not distance-throttle enemy snapshots at all; delta compression
(`ShouldSendHostEnemyStateSnapshot`, heartbeat 0.75 s) still bounds bandwidth. Verified: host
`interestManagementFarSkipped=0`.

### 2. Per-hit client hitch (separate symptom) — FIXED (HG/HG2)
"Each ranged hit stutters." Root = `LogEnemyHostDamageAuthority` logging once **per hit** = synchronous
disk I/O on the client. Now Bind-default OFF (and not dev-forced). The `[DamageHitch]` probe also
revealed the native `Unit.ReceiveDamage` feedback costs ~50 ms/hit on the sandboxed client (render
feedback under two-instance GPU/CPU contention; blood is skipped because we pass `Hitmesh.Data.Default`)
— acceptable for now, may matter under heavy fights. Probe stays behind `LogDamageApplyHitch`.

### 3. Host despawns an enemy the client is fighting → orphaned zombie puppet — FIXED (SC3) ✅ verified
**This was the main "last enemy stands still" cause.** Evidence chain (LogOutput111):
- Host: `Death idx=1 BlackGuildTracker damageCount=0 lifetime=22.9s Npc.Die` — the host's enemy
  **despawned** (`damageCount=0` = NOT killed by the player; compare `damageCount=10` = a real kill).
  It died on its own while far from the host player (leash / LOD / fell off map — root not yet
  determined).
- Client: received that death, matched the bound local entity, applied `Npc.Die()`…
- …**but the enemy-puppet binding was never released.** The puppet lingered in `ActiveEnemyPuppets`,
  and `ReleaseStaleEnemyPuppets` kept *suppressing* its release because it was still "host-bound"
  (`Stale-release suppressed (host-bound)`, `lastRecv` climbing to 21 s) → it stood there forever.

**Fix (`ReleasePuppetOnHostDeath`, default ON):** after `Die()` is applied on a host-death,
`ReleaseClientEnemyPuppetOnHostDeath` releases the puppet (`ReleaseEnemyPuppet`) and drops the binding
both ways (`ClientLocalKeyToHostSpawnIndex` / `ClientHostToLocalKeyByHostSpawnIndex` /
`_runtimeSpawnBindingsByHostIdx`) + the recv timestamp. **Verified LogOutput112:** `[EnemyPuppet]
Release … reason=host death (HostEnemyDeathEvent)` for idx 1/2/3/4/6; **zero `Stale-release
suppressed`** all session.

### 4. Duplicate / orphaned host-binding → frozen "host-bound" zombie — FIXED (DB) ✅
**This was the melee zombie that stuck after climbing onto the player's platform (LogOutput116).** A
`BlackGuildBruiser` ran in bound and fine, then froze the moment it jumped onto the player's platform; the
client lost its binding from that point. Evidence:
- Host `Death idx=1 BlackGuildBruiser` — the real `hostIdx=1` was bound (`Bound hostIdx=1 → localIdx=1`
  dist=0.0m), fought (`ClientHit` seq 1–7), and died normally. Not a same-seed divergence (dist=0.0m).
- Client `[EnemyPuppet] Stale-release suppressed (host-bound) idx=4 actor=[3]-Bruiser hostIdx=1 recv=never`
  **and** later `idx=17 actor=[16]-Bruiser hostIdx=1 recv=never`. **Three different local entities were all
  bound to `hostIdx=1`.** Only the live one (`[0]`) received snapshots; the surplus puppets never did
  (`recv=never`), went stale at 3 s, but `ReleaseStaleEnemyPuppets` kept *suppressing* their release because
  they were still "host-bound" → frozen forever.

**Root cause:** the additive binding-write sites (`ProcessHostManifest` reconcile ~7293, retro-bind ~6601,
`RegisterMirroredRuntimeSpawn` ~6475) wrote `ClientHostToLocalKeyByHostSpawnIndex[h]=localKey` /
`ClientLocalKeyToHostSpawnIndex[localKey]=h` **without evicting the stale reverse entry**. When `hostIdx=h`
re-bound to a *new* local entity (enemy moved / re-matched on climb), the **old** local key kept its
`localKey→h` reverse mark. It became an orphan: snapshots for `h` went to the new binding, the orphan got
`recv=never`, and "host-bound" suppression made it permanent. SC3's death-release only cleaned the single
*currently-matched* local key, leaving the orphans. (`ReleaseEnemyPuppet` also never removed the binding-map
entries — only SC3 and reconcile-`Clear()` did.)

**Fix (`EvictStaleHostBindings`, default ON):**
- New `SetClientHostBinding(hostIdx, localKey)` helper routes all additive writes; before writing it evicts
  (a) the old local key if `hostIdx` was bound elsewhere, and (b) the old `hostIdx` if `localKey` was bound
  elsewhere → the two maps stay strictly 1:1.
- Safety net in `ReleaseStaleEnemyPuppets`: if a stale puppet is "host-bound" but the forward map for its
  `hostIdx` now points at a *different* local key (orphan), **release it** instead of suppressing.
- Counter `dbEvicted=` in the orphan-release log line. **Verify next run:** the frozen-after-climb Bruiser is
  gone; client log shows `[EnemyPuppet] Release … reason=stale orphan binding hostIdx=… (forward map disowned…)`
  if any orphan is caught, and no lingering `Stale-release suppressed … recv=never` for a live fight.

Changed: `NetGameplayProbeManager.cs` (SetClientHostBinding + 3 call sites + orphan branch),
`CoopConfig.cs` (gate + dev-default), `Plugin.cs` (marker DB).

### 5. Dead host idx re-binds the surviving same-type sibling → frozen ranged zombie — FIXED (DB2) ✅
**The ranged BlackGuildTracker that stood frozen after the host killed it (LogOutput117).** Two Trackers in
the fight (`hostIdx=12` and `hostIdx=17`), both retro-bound at dist=0.0m. Sequence:
- Client kills `hostIdx=12` → death applied, `local [11]` puppet released, binding dropped (SC3 working).
- The **WorldRoster is the static level-gen roster and still lists `hostIdx=12`**, so the next reconcile's
  proximity pass re-binds the *dead* `hostIdx=12` to the only surviving Tracker, `local [16]`. That puppet
  now shows `Stale-release suppressed (host-bound) idx=17 hostIdx=12 recv=never` — it never gets another
  snapshot and stands frozen.
- Meanwhile the *alive* `hostIdx=17` is left unbound; when the host kills it the client logs
  `No safe local match for host death: hostIdx=17 never bound, late-bind failed` → **the death is never
  applied** → the survivor stays standing. Exactly the report: "host killed it, client didn't see it die."

**Root cause:** binding paths happily (re)bind a host idx the client has *already buried*. The client
already tracks these in `_clientTerminalDeadHostIdx` (set when a terminal death is applied; cleared on level
change) — it just wasn't consulted when binding.

**Fix (`SkipDeadHostIdxRebind`, default ON):** `IsClientKnownDeadHostIdx(idx)` gates every bind path —
`SetClientHostBinding`, the `ProcessHostWorldRoster` proximity loop, the `ProcessHostManifest` enemy-bind
loop, and the retro-bind parked-ledger scan all skip a buried idx. `ReleaseStaleEnemyPuppets` also releases a
puppet already stuck on a buried idx (instead of suppressing). Net effect: the dead Tracker can't steal the
survivor, so the survivor binds the alive host idx and its death applies normally.

Changed: `NetGameplayProbeManager.cs` (IsClientKnownDeadHostIdx + 4 skip sites + stale-release branch),
`CoopConfig.cs` (gate + dev-default), `Plugin.cs` (marker DB2). **Verify next run:** the ranged Tracker dies
on the client when the host kills it; no lingering `recv=never` suppression for the survivor.

> **Still open (separate, bigger):** this fight also showed heavy **same-seed level-gen divergence** —
> `[LevelManifestDiff]` listed ~15 `modifier mismatch` lines (host `Offensive` vs client `Defensive`, etc.),
> a `unit mismatch idx=27 host=BlackGuildDog client=BlackGuildAssassin`, and many host-only/client-only
> units. So enemies' **special attributes (modifiers) are rolled differently on each side**, and the
> **`Unit.SpawnOnDeath()` "spawn a random enemy on death" mechanism is not synced** (host spawned a
> `ShavwaLurk`; the client won't mirror it). DB2 stops the *frozen-zombie* symptom; the divergence + add-sync
> is the cause-4 architectural work below.

---

## OPEN — Cause 4(b): same-seed spawn divergence → retro-bind can't bind (rare)

**Status: NOT fixed. Diagnostic left in place. Set aside by owner (rare — "took a long time to
appear"); pick up opportunistically when it recurs.**

LogOutput112, **Shanty4** (`Act_01_Shanty:3`), a **melee** (Bruiser-class) enemy stood still. This is
NOT cause #3 (no `Stale-release` events; SC3 working). Findings:
- Host and client **same seed** (`1494913893`) — not a seed-authority divergence.
- Client reconcile: `No local match` for a **contiguous block** `hostIdx=30…36`
  (BlackGuildBruiser/Rifleman/Dog) and **`retroLedger=14` that never gets consumed** (`retroBound`
  stuck at 7). So the host has a wave of enemies the client's binding never matched.
- The standing enemy is a client-local puppet (clientOnly=0 → all client enemies bound), so it is
  *bound*, but likely bound to the wrong / a dormant host sibling because the **client's local spawn
  positions diverged from the host's** for that wave (same seed, but RNG/room-timing divergence) →
  `TryRetroactiveBindNewLocalEntity` defers (`ambiguous-pos-defer`) rather than mis-bind.

This is the original "level-gen enemies, same seed but divergent spawn → position-based bind fails"
problem (RT3-A46 era; boss adds got snap-on-bind, level enemies never did).

### Gen-divergence investigation (LogOutput117, the "special attribute" report) — partly explained
The user reported enemies whose **special attribute (Offensive/Defensive)** differs per side, and the
`[LevelManifestDiff]` showed ~15 `modifier mismatch` lines + a `unit mismatch` + many hostOnly/clientOnly.
Decompiling the gen pipeline (`PerfectRandom.Sulfur.LevelGeneration.dll`) settled where the randomness lives:
- **Level layout + unit selection + mutations are DETERMINISTIC.** Each graph node gets
  `_Random = new Unity.Mathematics.Random((uint)(Seed + nodeIndex))` (`MakerGraphContext`), and
  `SpawnEnemiesNode` / `FinalizeAndMutateUnitsNode` draw only from that `_Random`. Same level seed ⇒ same
  enemies, positions, and **mutations** (the `MutationDefinition`, incl. `unitsToSpawnOnDeath` — the
  "spawn a random enemy on death" special). So the *assignment* of the death-spawn mutation is in sync.
- **The Offensive/Defensive role is NOT.** `AiAgent.AssignStartRole()` picks it with
  **`UnityEngine.Random.Range(0, rolesAvailable.Count)`** — the global, non-seeded Unity RNG, per enemy, at
  AI init. Host and client roll independently → ~50 % differ. **This is the entire "modifier mismatch".**
- **Impact is mostly diagnostic, not binding.** Actual binding matches by `Category + UnitId + position` and
  never reads `ModifierFlags`. But `UnitSignature` *did* fold `ModifierFlags` into the generation hash, so
  `genHash` could never match and the SAME enemy was counted as both hostOnly (host role) and clientOnly
  (client role) — drowning out any real divergence. **Fixed (Phase 5.7-GD):** `UnitSignature` =
  `unitId#qx,qz` only; the role is still surfaced by the explicit `modifier mismatch` line. Now `genHash`
  is a real determinism check and the hostOnly/clientOnly counts reflect true divergence.

**Death-spawn networked — DONE (Phase 5.7-DS).** The "spawn a random enemy on death" mutation
(`MutationDefinition.unitsToSpawnOnDeath`) picks its unit in `AddSpawnUnit` with the **global
`UnityEngine.Random`**, so host and client baked *different* units into the dying enemy's `onDeath` delegate
→ on death each side spawned a different enemy (the user's "客机也会出一只怪，只是完全不同怪"). The actual
spawn runs through `MutationDefinition.OnDeathSpawnUnitsFunc` → static `UnitSO.SpawnUnit`, and in non-endless
play that method has **no `await` before the spawn** (only the endless branch awaits) so it is fully
synchronous. Fix (`EnableDeathSpawnSync`, default ON, host-authoritative):
- **Client** prefixes `OnDeathSpawnUnitsFunc` and returns false → **suppresses its local divergent spawn**.
- **Host** brackets the call with a flag; the existing `UnitSO.SpawnUnit` postfix
  (`RuntimeSpawnManager.OnUnitSpawned`) sees the flag and **broadcasts** the spawned unit via the RT1
  `NetRuntimeSpawn` pipeline (`src=DeathSpawn`); the client **mirrors + binds** it like any host runtime spawn.
- Verify: host `[RuntimeSpawn] host broadcasting … src=DeathSpawn`; client `[RuntimeSpawn] client suppressed
  local death-spawn …` + `client mirrored unit hostIdx=…`. Counters `DeathSpawnHostBroadcast` /
  `DeathSpawnClientSuppressed`.
**Minion-spawn networked — DONE (Phase 5.7-DS2).** `SpawnMinions` (the `spawnMinionsOnDeath.amountToSpawn`
mutation: N **same-type** minions spawned async via `SpawnUnitAsync(GameManager,…)`) was the source of
LogOutput118's `never bound, late-bind failed` block — a wave of `GoblinYoung` the host spawned but never
broadcast, so the client got their deaths with no local entity. The async path can't use the DS synchronous
flag bracket, so instead the host registers a short-lived **minion context** (parent UnitSO + remaining
count + 5 s TTL); `NotePendingSpawn` (already on the `SpawnUnitAsync` prefix, async-safe) claims it and tags
the spawn `DeathMinion` so the existing pipeline broadcasts it. The client suppresses its whole `SpawnMinions`
and mirrors the host's. Gate `EnableMinionSpawnSync`. Counters `MinionHostBroadcast`/`MinionClientSuppressed`;
host log `src=DeathMinion`. *Loot caveat:* the client skip also skips the parent's trailing `SpawnLoot()`/
`SpawnGibExplosion()`; harmless in practice because client puppet deaths don't re-fire the mutation `onDeath`
(LogOutput118 DS suppress count = 0), but if loot for minion-spawning enemies goes missing on the client,
switch the client suppression from whole-method skip to a targeted async `SpawnUnitAsync` suppress.

**BatchedNPCRaycasts.LateUpdate hardened (Phase 5.7-DS2).** The Burst LOS/ground job's results are indexed by
`unitMapping`/`npcMapping` count and decode player indices from LOD bytes (`players[255-b]`); when the roster
or `GameManager.Players` changes between job-schedule and `LateUpdate` (runtime spawns + our injected ghost
Players, Phase 5.7-B) an index can run past the arrays → `ArgumentOutOfRangeException` (LogOutput118, 1×). A
finalizer swallows it (recovers next frame) under the existing `EnableDestroyedUnitListSweep` gate.

**Still open (needs an owner decision — none done yet):**
1. **Deterministic / host-authoritative AgentRole** — so the two sides agree on Offensive vs Defensive.
   Low correctness impact (client enemies are host-driven puppets; host role is authoritative) but removes
   the cosmetic/behaviour split. Options: seed `AssignStartRole` from (levelSeed, gen index) deterministically,
   or have the host broadcast each enemy's role. (`UnityEngine.Random.Range` in `AiAgent.AssignStartRole`.)

### Diagnostic now in place (so the next repro self-documents)
Gate `LogEnemyInterestDiag` (dev-default ON). When a late-spawned client enemy can't retro-bind:
```
[RetroBindDiag] reason=ambiguous-pos-defer|no-unbound-slot localUnit=… localIdx=… localPos=… \
    sameUnitParked=N unboundMatches=M bestDist=Dm parked: [hostIdx=… pos=… dist=… bound=…] …
```
- `reason=ambiguous-pos-defer` + large `bestDist` → **position divergence** is the cause (client
  spawned the enemy >5 m from every host record of that type). Fix direction = relax retro-bind
  tolerance / bind by spawn order not nearest position / snap-on-bind to host pos.
- `reason=no-unbound-slot` → the client spawned a sibling but every host slot of that unitId is already
  bound → count/ordering mismatch.
- Also watch the host `[SnapColl] npcTracked/collected/sent/excluded*` and the client
  `[EnemyPuppet] Stale-release suppressed … hostIdx=… lastRecv=…` lines.

### How to resume
1. Repro the standing-still in a later Shanty sublevel; grab both logs.
2. Read the `[RetroBindDiag]` lines for the stuck enemy's unitId → confirm position-divergence vs
   ordering.
3. If position divergence: change `TryRetroactiveBindNewLocalEntity` to bind by parked-order /
   wider tolerance, and snap the puppet to the host record's position on bind (mirror the RT3-A
   snap-on-bind that boss adds use).

---

## Config flags (all reversible)
| Flag | Default | Purpose |
|---|---|---|
| `SendAllEnemySnapshotsToClients` | ON | RB4 — no distance throttle while a client is connected |
| `ReleasePuppetOnHostDeath` | ON | SC3 — release the client puppet+binding when a host death applies |
| `EvictStaleHostBindings` | ON | DB — keep host↔local maps 1:1; release orphaned host-bound puppets |
| `SkipDeadHostIdxRebind` | ON | DB2 — never (re)bind a buried host idx; release puppets stuck on one |
| `EnableDeathSpawnSync` | ON | DS — host-authoritative "spawn random enemy on death" mutation (client suppress + mirror host) |
| `EnableMinionSpawnSync` | ON | DS2 — host-authoritative `spawnMinionsOnDeath` minions (client suppress + mirror host) |
| `EnableRetroactiveEnemyBinding` | ON | RB — park unmatched host records, bind on later client spawn |
| `LogEnemyInterestDiag` | dev-ON | `[SnapColl]` + `[RetroBindDiag]` host/bind diagnostics |
| `LogClientEnemyPuppetMode` | dev-ON | `[EnemyPuppet]` stale/release lines (with `hostIdx`/`lastRecv`) |
| `LogDamageApplyHitch` | OFF | `[DamageHitch]` per-hit apply timing (threshold-gated) |
| `LogEnemyHostDamageAuthority` | OFF | per-hit damage log — leave OFF (per-hit disk I/O hitch) |

## Known follow-ups (not blocking)
- **Root-cause cause #3:** the host shouldn't *despawn* enemies a remote player is fighting
  (`Npc.Die damageCount=0` — leash/LOD/fell off map?). SC3 is the clean symptom fix; the root despawn
  needs reverse-engineering (owner approved, deferred).
- `ClassifyEntitySyncCategory` uses a dirty `actorName.Contains("shanty")` → `Ambient` heuristic
  (`NetGameplayProbeManager.cs` ~7498). Harmless in repros so far but can misclassify; clean up.
- Double-binding seen once (two local entities → same `hostIdx`) — watch for it; tied to cause #4.

See memory `phase-5-7-known-roster-bind-spawn-divergence` for the condensed running notes.
