# In-Scene Destructible Sync (Phase 5.7-BR)

Mirrors destruction of in-scene destructibles (barrels, crates, glass, planks, vases, …) so that an object broken
by **any** player shatters on **every** player's screen. Validated 2026-06-25 (LogOutput113, both ends).

## Problem

Destructibles are `PerfectRandom.Sulfur.Core.Units.Breakable : Unit` — **not** `Npc`. The existing client→host hit
router (`Npc_ReceiveDamage_Pre` → `TrySendClientHitRequest` / `TryClientBossHit`) only intercepts `Npc`, so a
`Breakable` only ever reaches `Unit_ReceiveDamage_Pre` (suppression / probe — no host routing). Consequently each peer
could only break its **own** local destructibles with its **own** real bullets, and the remote players' replayed
visual bullets (Phase 5.6-WS, damage stripped) break nothing. A barrel A shot stayed standing on B's screen.

## Model — peer-authoritative EFFECT mirror

User-chosen over host-authoritative. Each peer breaks its own destructibles for real (its real bullet / physics /
explosion → `Unit.ReceiveDamage` → `Breakable.Die`); when that happens we broadcast a break event and receivers mirror
the **effect** (not the bullet). Rationale: loot is not networked anyway (each peer spawns its own, same as enemy
deaths), destructibles are environment, and an effect mirror is immune to bullet-spread divergence and low-risk.

## Identity — deterministic spawn position

Destructibles are placed deterministically by level generation; the seed is host-authoritative and synced, so both
ends own the same set at the same positions. The cross-peer key is each breakable's **spawn position**, captured at
`Breakable.Start` (before any physics movement — stable even for barrels that roll before breaking). Receivers match
the nearest still-alive local `Breakable` within `MatchEpsilon = 0.75 m`.

## Flow

```
Firing peer:   bullet/physics → Breakable.ReceiveDamage → Breakable.Die
                 └ Die prefix → CaptureLocalBreak → broadcast NetBreakableBreak{ spawnPos }
                 └ Die proceeds → real shatter + onBreakEvents + child cascade + loot + destroy (local)

Receiver:      HandleBreakableBreak → MatchesScene? → ApplyRemoteBreak
                 └ FindMatch(nearest live within 0.75 m) → _applyingMirror=true; target.Break(); finally false
```

- **Topology** mirrors `PlayerWeaponFire`: Client → Host → relay to other Clients; the firing peer never mirrors its
  own break (Host doesn't replay its own; Client skips its own `PeerId`).
- **`_applyingMirror`** guard: a mirrored `Break()` (and the child / `breakOtherOnSelfBreak` cascade it triggers) is
  not re-broadcast — no network echo.
- **Idempotent:** a second event for the same destructible (cascade child already broken locally, or already broken by
  this peer) finds no live match → logged as `no alive match` and skipped. This is expected, not an error.
- **`MatchesScene`** (chapter | level | seed) gates cross-level events.
- Registry is cleared on level transition (`GM_ClearLevel_Pre` + `GoToLevel`).

## Files

| File | Role |
|------|------|
| `NetMessageType.BreakableBreak = 47` | message id |
| `Gameplay/NetBreakableBreak.cs` + `…Codec.cs` | DTO (PeerId, scene, spawn position) + wire codec |
| `Gameplay/BreakableBreakManager.cs` | spawn-position registry, capture, mirror match/apply, `Clear()` |
| `Patches/BreakablePatches.cs` | `Breakable.Start` postfix (register) / `Breakable.Die` prefix (capture + unregister) |
| `NetService.cs` | `BroadcastLocalBreakableBreak` / `Send` / `Handle` / `Relay` + dispatch case |
| `NetGameplaySyncBridge.ReportLocalBreakableBreak` | manager → NetService bridge |
| `CoopConfig` `[Destructibles]` | `EnableBreakableSync`, `LogBreakableSync` (default on) |

## Validation (LogOutput113, 2026-06-25)

Clean symmetric mirror, zero errors / malformed / failures:

- host capture 47 ↔ client mirror 47; client capture 70 ↔ host mirror 70.
- `no alive match` 24–25 per end — the expected idempotent skips (cascade children / self-already-broken).

## Known boundaries

- **Loot** still spawns per-peer (loot is not networked — pre-existing; same as enemy deaths).
- **Exploding barrels (BarrelBoy):** AoE damage to enemies is already host-authoritative (Npc routing); damage to the
  local player is per-peer (same as any environmental hazard).
- **`onBreakEvents`** that drive gameplay (open a gate, spawn enemies) fire on the mirror end too — correct; any spawns
  go through the separate runtime-spawn sync.
- **`DestroyBreakables`** area clears break many at once → a burst of reliable packets (ReliableOrdered absorbs it; add
  a per-frame cap if it ever matters). `FindMatch` is O(n) over the live registry per event.
- Reversible: `EnableBreakableSync = false`.

## Optional / not done — consistent bullet spread

Spread comes from the global `UnityEngine.Random.Range` (`Helpers.GetRandomDirectionInCode`, 2 calls/bullet). All peers
*could* see identical spread by syncing a per-shot seed (`Random.InitState(seed)` on the firing peer's barrage + send
seed + receiver `InitState` before replay), but it perturbs the global RNG stream and the gain is purely cosmetic. The
BR mirror does **not** depend on it — destruction propagates as an effect, not as "the remote bullet really hit."
