# Host-Driven Proxy Architecture — Implementation Plan

## Problem Statement

The previous "patch-based mirror" approach caused AI/animator/projectile/damage/death divergence between host and client game states. Clients were running autonomous enemy behavior (BehaviorTree, NavMesh, damage, death resolution) locally and then trying to sync results back, causing irreconcilable state splits.

## Architecture Direction

```
Host authoritative world
  → Stable HostNetId (HostSpawnIndex from HostWorldRoster)
  → Client visual proxy (puppet record, no autonomy)
  → Semantic events over raw field mirroring
  → Snapshot interpolation (unreliable, position/rotation/animator)
  → Host-owned combat result (damage, death, drops)
```

Every new feature must serve this direction. See `NetworkingArchitecture.md` for channel semantics.

---

## Phase Breakdown

### P0 — CombatEnemy Binding Stability + Damage Suppression

**Goal:** Client enemies never cause real damage/death divergence.

**Changes:**

| File | Change |
|------|--------|
| `ReverseProbePatches.cs` | `Npc_MeleeHit_Pre` — Phase 5.0 path: allow HandleMeleeHit for visual animation, ALWAYS enter damage suppression for all puppet NPCs when `EnableHostDrivenEnemyProxy=true` |
| `NetGameplayProbeManager.cs` | `ShouldSuppressAllClientPuppetDamage()` — broad gate for all puppet NPC damage on client |
| `CoopConfig.cs` | `EnableHostDrivenEnemyProxy`, `SuppressAllClientPuppetDamage`, `LogClientPuppetDamageSuppression` |

**Behavior:**
- Host: all damage, death, drops run normally via existing `TryApplyHostAuthoritativeEnemyDamage` path.
- Client: `Npc.HandleMeleeHit` runs (for visual animation) but `Unit.ReceiveDamage` is suppressed by `EnterClientEnemyNativeDamageSuppression` depth counter.
- Client: puppet NPCs cannot kill the local player; damage events are discarded silently.

---

### P1 — Host Attack Phase Events + Death Sync

**Goal:** Host reliably drives enemy attack animation on client. Melee animation visible without authorized window.

**New Message:** `NetMessageType.HostAttackPhaseEvent = 20` (ReliableUnordered)

**New File:** `NetHostAttackPhaseEvent.cs`

Phase constants:
- `PhaseNone = 0`
- `PhaseWindup = 1` — attack telegraphed, animation starts
- `PhaseActive = 2` — damage window open (StartMeleeDamage)
- `PhaseRecovery = 3` — damage window closed (EndMeleeDamage)
- `PhaseCancelled = 4`

Kind constants:
- `KindNone = 0`, `KindMelee = 1`, `KindRanged = 2`, `KindWeaponAction = 3`

**CombatActionKind → AttackPhase mapping:**

| CombatActionKind | Phase | Kind |
|---|---|---|
| TriggerAttackAnimation (1) | Windup | Melee |
| Shoot (2) | Windup | Ranged |
| SetShooting (3) | Windup | Ranged |
| TriggerWeapon (4) | Windup | WeaponAction |
| TriggerShootFromAnimation (5) | Windup | Ranged |
| StartMeleeDamage (6) | Active | Melee |
| EndMeleeDamage (7) | Recovery | Melee |
| SetRangedAttacking (8) | Windup | Ranged |
| SetAttacking (9) | Windup | Melee |
| DoneAttacking (10) | None | Melee |
| DoneShooting (11) | None | Ranged |

**Client receive handler:** `ProcessHostAttackPhaseEvent` → `TryApplyAttackPhaseToClientPuppet` — applies animator state via `Animator.CrossFade` (NO native method replay).

**Config:**
- `EnableHostAttackPhaseEvents` (default ON)
- `LogHostAttackPhaseEvents` (default ON, dev)
- `EnableClientAttackPhaseAnimatorDrive` (default ON)
- `ClientAttackPhaseCrossFadeSeconds` (default 0.05f)

---

### P2 — Projectile Visual Proxy (Disabled by Default)

**Goal:** Client sees projectile flight path as a cosmetic proxy. No real physics/damage on client.

**New Message:** `NetMessageType.HostProjectileVisualSpawn = 21` (ReliableUnordered)

**New File:** `NetHostProjectileVisualSpawn.cs`

Fields: scene context, HostSpawnIndex, UnitIdentifier, Sequence, Origin, Velocity (direction×speed), Lifetime, ProjectileKind.

**Status:** P2 — client stub only logs receipt. Actual cosmetic spawn not yet implemented.

**Config:**
- `EnableHostProjectileVisualSpawnEvent` (default **OFF** — P2)
- `LogHostProjectileVisualSpawn` (default OFF)

---

## Interest Management

**Goal:** Reduce snapshot bandwidth for far/non-combat enemies.

**Thresholds:**
- `EnemyNearCombatDistance` (default 20f) — full snapshot rate
- `EnemyFarDistance` (default 40f) — reduced to `EnemyFarSnapshotHz` (default 2 Hz)

**Position source:** host's own local player position via `SetLocalPlayerPositionHint()`, updated from `HandleRemotePlayerVisualProxyTimer` in `NetService`.

**Config:**
- `EnableEnemyInterestManagement` (default ON)
- `EnemyNearCombatDistance`, `EnemyFarDistance`, `EnemyFarSnapshotHz`

---

## Config Entries Added (Section: "HostDrivenProxy")

| Key | Type | Dev Default | Description |
|-----|------|-------------|-------------|
| `EnableHostDrivenEnemyProxy` | bool | true | Master toggle for Phase 5.0 architecture |
| `SuppressAllClientPuppetDamage` | bool | true | All puppet NPC damage suppressed on client |
| `LogClientPuppetDamageSuppression` | bool | true | Log each suppressed damage event |
| `EnableHostAttackPhaseEvents` | bool | true | Host broadcasts HostAttackPhaseEvent |
| `LogHostAttackPhaseEvents` | bool | true | Log sent/received attack phase events |
| `EnableClientAttackPhaseAnimatorDrive` | bool | true | Client applies Animator.CrossFade on receipt |
| `ClientAttackPhaseCrossFadeSeconds` | float | 0.05f | CrossFade blend duration |
| `EnableHostProjectileVisualSpawnEvent` | bool | **false** | P2: host broadcasts projectile visual spawn |
| `LogHostProjectileVisualSpawn` | bool | false | P2: log projectile events |
| `EnableEnemyInterestManagement` | bool | true | Distance-based snapshot rate reduction |
| `EnemyNearCombatDistance` | float | 20f | Full-rate snapshot distance |
| `EnemyFarDistance` | float | 40f | Reduced-rate snapshot distance |
| `EnemyFarSnapshotHz` | float | 2f | Snapshot Hz for far enemies |

All dev defaults applied in `ApplyUnpublishedDevelopmentDefaults()`.

---

## What Must NOT Break

- Connection / Host / Client flow and peer lifecycle
- Player position sync and F6/PageDown scene follow
- Random seed logic and HostWorldRoster existing registration
- Existing config and logging systems
- `TestPlan.md` (frozen — no edits)

---

## Known Blockers / Game API Uncertainties

1. **Animator state layer 0 accessibility** — `TryPopulateAttackPhaseAnimatorHint` uses reflection to read the Animator component's current state hash. If the game strips or obfuscates Animator methods, the hint will be absent (HasAnimatorHint=false) and CrossFade will operate without a state name.

2. **HostNetId binding stability** — relies on HostSpawnIndex from HostWorldRoster. If enemies despawn/respawn mid-level, indices may shift. Mitigation: `UnitIdentifier` field as secondary match hint.

3. **Projectile cosmetic spawn (P2)** — requires identifying the projectile prefab or effect from `ProjectileKind`. Not yet implemented; stub logs receipt only.

4. **Per-client interest management** — current implementation uses host's own position as proxy for all clients. Multi-client scenarios where a second client is near a far enemy will under-deliver snapshots for that client. Full per-client culling is a future P3 concern.

---

## Test Steps

1. **P0 damage suppression:** Launch host + client. Walk client player into puppet enemy melee range. Confirm client player HP does not decrease. Check logs for `[Phase5.0]` suppression counters in `MaybeLogSummary`.

2. **P1 attack phase:** Enable `LogHostAttackPhaseEvents`. Trigger enemy attack on host side. Confirm client log shows `[HostAttackPhaseEvent] recv` with correct Phase/Kind. Observe client enemy plays attack animation without HP drain.

3. **Interest management:** Enable `LogInterestManagement` (if added) or check `_interestManagementFarSkipped` counter in summary. Place enemy >40u from host player. Confirm snapshot rate drops to ~2 Hz.

4. **P2 disabled:** Confirm `EnableHostProjectileVisualSpawnEvent=false` means no projectile visual events are sent or received.
