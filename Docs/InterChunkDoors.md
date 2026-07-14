# Inter-Chunk Door Sync (Phase DB-1)

Mirrors the opening of the hold-to-open doors SULFUR 0.18 places between chunks, so a door **any** player opens is
open — and passable — on **every** player's screen.

## Problem

0.18 ("Qiosk's Plenty") reworked level pacing: `FinalizeLevelNode` now places `DoorBlocker` doors on connectors
between chunks. The door physically blocks the doorway until a player holds the interact key to unlock it, and the
hold takes longer while enemies can reach you.

`DoorBlocker : HoldingInteractable` is driven entirely by the **local** player's interaction: `Interactable` →
`OnHoldInteract` → the base `Update` accumulates `holdingTimer` → `OnFinishedHolding`. Nothing about it is
networked, and each peer runs its own copy of the level. So a door one player opened stayed shut for everyone
else — and because it blocks the doorway, the other player was physically walled out of a chunk the first player
had already walked through.

This is **not** the boss-arena door. Boss rooms seal with `MetalGate` (Phase LD-1) or a `SetActive` door
(LD-1b); the PF-0 `[ArenaDoor]` probe recorded zero `DoorBlocker` events in a real Cousin fight. 0.18 is the first
build where `DoorBlocker` carries ordinary level pacing, and it is a separate channel from LD-1/LD-1b.

## Model — peer-authoritative EFFECT mirror

Same shape as Phase 5.7-BR (destructibles) and LD-1 (gates), and for the same reason: the door is environment,
the transition is **one-way**, and a mirror is cheaper and safer than routing the interaction through the host.

Opening is irreversible — `OnFinishedHolding` latches `isHoldingFinished`, `CanInteract` returns false forever
after, and nothing re-locks the door. A one-way transition needs no arbitration: two players opening the same door
at once converge on "open", so the mirror is naturally idempotent and no host authority is required.

## Identity — deterministic spawn position

Doors are placed by `FinalizeLevelNode` from `new Unity.Mathematics.Random((uint)Context.Seed)` — the prefab
choice (`TryGetValidDoorBlocker(…, ref random, …)`) and the trap/openable alternation (`random.NextBool()`) draw
from that one seeded stream. The seed is host-authoritative and synced, so both ends own the same doors at the
same positions, and **placement needs no sync**. The cross-peer key is the door's world position, captured at
`ActivateDoorBlocker` (the root never moves; `FitToDoor` only adjusts child colliders). Receivers match the
nearest still-closed local door within `MatchEpsilon = 1.0 m`.

## Scope — only doors that start closed

Level gen produces two shapes, alternating:

| Spawned as | `isAnClosingDoor` | Behaviour |
|---|---|---|
| `(Openable)` | `false` | Starts closed. Hold to open. |
| `(Closing Start)` | `true` | Starts **open**; slams shut when a player crosses its `DoorBlockerTrigger` — the trap. |
| `(Closing End)` | `false` | The trap pair's far door: starts closed, hold to open while the fight is on you. |

**Only `isAnClosingDoor == false` doors are registered.** That is the technical boundary that makes the capture
hook exact: those doors wire no `DoorBlockerTrigger`, so they never `CloseDoor`, so they never run
`EnemyOpeningDoor` — their `OnFinishedHolding` can *only* be a player finishing a hold. Registering trap doors
would also capture an enemy shouldering the door open, which is a different event with a different owner.

The trap doors' **slam-shut is deliberately not synced** (out of scope for DB-1): it stays per-end, so a trap
that closed on one player has not closed on the other.

## Flow

```
Opening peer:  hold → base Update → DoorBlocker.OnFinishedHolding (real open)
                 └ postfix → CaptureLocalOpen → broadcast NetDoorBlockerOpen{ position }
                 └ real open proceeds: lock sound, "OpenDoor" animation, LOS collider + navmesh cut off

Receiver:      HandleDoorBlockerOpen → MatchesScene? → ApplyRemoteOpen
                 └ FindMatch(nearest registered within 1.0 m)
                 └ already isHoldingFinished? → skip (idempotent)
                 └ _applyingMirror=true; OnFinishedHolding.Invoke(target); finally false
```

- **Topology** mirrors `NetGateState`: Client → Host → relay to other Clients; the opening peer never mirrors its
  own open (Host doesn't replay its own; Client skips its own `PeerId`).
- **`_applyingMirror`** guard: the mirrored `OnFinishedHolding` must not re-broadcast — no network echo.
- **The mirror calls the game's real method**, so it reproduces whatever the door does, including the animation
  that swings the physical door out of the way. Camera shake and rumble self-gate on the local player's distance
  to the door (`DoorBlockerShaker.maxDistance`, ≤15 m), so a peer across the level gets no shake from someone
  else's door.
- **`MatchesScene`** (chapter | level | seed) gates cross-level events.
- Registry is cleared on level transition (`GM_ClearLevel_Pre` + `GoToLevel`), and each door drops out of the
  registry the moment it opens (it can never match again).

## Files

| File | Role |
|------|------|
| `NetMessageType.DoorBlockerOpen = 79` | message id (ProtocolVersion → 5) |
| `Gameplay/NetDoorBlockerOpen.cs` + `…Codec.cs` | DTO (PeerId, scene, door position) + wire codec |
| `Gameplay/DoorBlockerSyncManager.cs` | position registry, capture, mirror match/apply, `Clear()` |
| `Patches/DoorBlockerPatches.cs` | `ActivateDoorBlocker` postfix (register + scope) / `OnFinishedHolding` postfix (capture) |
| `NetService.cs` | `BroadcastLocalDoorBlockerOpen` / `Send` / `Handle` / `Relay` + dispatch case |
| `NetGameplaySyncBridge.ReportLocalDoorBlockerOpen` | manager → NetService bridge |
| `CoopConfig` `[Destructibles]` | `LogDoorBlockerSync` (`EnableDoorBlockerSync` is `Fixed<bool>` — functional, always on) |

## Patch-binding notes

`DoorBlocker` lives in the **global namespace** (its base `HoldingInteractable` and `DoorBlockerShaker` are in
`PerfectRandom.Sulfur.Core`), and the Core assembly is referenced, so both hooks bind against the real type — a
future rename surfaces as a **compile error** rather than a silently dead hook.

0.18 changed `ActivateDoorBlocker(string, bool)` → `ActivateDoorBlocker(Connector, string, bool)`. We bind by
name and declare only `isAnClosingDoor`, so the added parameter is irrelevant to us (per `Docs/PatchRules.md`:
never pin a parameter table you don't read).

## Known boundaries

- **Trap doors' slam-shut is per-end** (out of scope). A `(Closing Start)` that trapped one player has not
  trapped the other.
- **No late-join catch-up**: a client joining mid-level gets no manifest of already-opened doors, so doors the
  host opened before the join are still closed for it. Same limitation as gates (LD-1) and destructibles (5.7-BR).
- **`ReReportEnemies` stays local**: the door's "opening this aggros enemies who can reach you / makes the hold
  slower in combat" logic reads the *local* `PlayerUnit` and mutates enemy AI. Enemy AI is host-authoritative, so
  a **client** opening a door does not pull enemies in the way a host opening one does. DB-1 mirrors the door
  state, not the aggro; fixing the asymmetry means routing the aggro through the host and is a separate change.
- The mirror end does not replay the *lock-turning* animation blend (`HoldingBlend`), only the open — the remote
  door opens without the wind-up its opener saw.
