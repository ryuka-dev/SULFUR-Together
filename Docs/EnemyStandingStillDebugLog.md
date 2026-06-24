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

---

## OPEN — Cause 4: same-seed spawn divergence → retro-bind can't bind (rare)

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
