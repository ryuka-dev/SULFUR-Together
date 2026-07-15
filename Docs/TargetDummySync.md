# Shared Target-Dummy Damage Numbers (TD-1)

Shares the 0.18 target dummy's damage **numbers** across peers, so several players shooting the same practice dummy
accumulate one combined total and every player sees the same flying head number and foot total.

## What the game does

The dummy's display is `PerfectRandom.Sulfur.Gameplay.DamageTracker` (a child of the dummy's `Unit`):

- `Start` subscribes to `trackingUnit.onDamageRecieved` (invoked from inside `Unit.ReceiveDamage`, right after the
  health deduction).
- `ShowDamage(unit, damage, sourceData, collisionPoint)` — the whole display, per hit:
  - `AddToTotal((int)damage)` → the **foot total** (`textMeshProTotal`, "Bump" anim), reset to 0 after **5 s** with no
    hit (`ResetTotalRoutine`);
  - a pooled `DamageTextInstance.Show(...)` → the **flying head number**, coloured by `sourceData.damageType`, crit
    variant on `sourceData.isCritical`;
  - `PlayHayEffect(collisionPoint, crit)`.
- The `unit` parameter is unused; `damage`, `sourceData.damageType`, `sourceData.isCritical` and `collisionPoint` are
  the entire input.

**Unlock**: the dummy is wrapped by `UnlockableStorage`, whose `DoActivationCheck()` keys on the per-player save flag
`PlayerProgress.GetCheckpointReached("ItemBroughtToChurch_<item>")` and **`SetActive(false)`s the whole subtree** when
the player hasn't unlocked it. A locked peer therefore never runs `DamageTracker.Start` and never registers a tracker.

**Damage is local to each shooter**: the dummy is not a host-roster puppet, so a client's hit is not routed to the host
(`TrySendClientHitRequest` skips non-puppets) — the vanilla `ReceiveDamage` runs locally on every shooter and only that
shooter sees the numbers. That is the gap this closes (the white hit flash already worked for the same reason).

## Model — peer-broadcast, each peer accumulates locally

No host authority is imposed on a non-puppet object, and the shooter keeps zero-latency feedback:

- **Capture**: a `ShowDamage` postfix records each real local hit (mirror replays are filtered by a guard).
- **Broadcast**: the hitting peer sends a batch; topology is the `BreakableBreak` one — `Client→Host→relay to other
  Clients`, host stamps `PeerId` + scene context, `ReliableOrdered`. The source peer never processes its own echo (it
  already displayed the hit).
- **Apply**: every other peer replays the **effect only** through the same private `ShowDamage` (reflection, under the
  mirror guard) — flying number + `AddToTotal` — and **never touches the dummy's health**.
- **Result**: each peer's total = its own local hits + everyone else's relayed hits = the same exact shared sum on
  every screen. Each hit is counted exactly once per peer; `ReliableOrdered` means none are dropped.

The 5 s reset runs per-peer off the same event stream, so all peers reset together (within the batch/latency skew).

## Coalescing (high fire-rate)

A shooter accumulates its hits per dummy over a **75 ms** window and ships **one** message whose `Amount` is the summed
damage. That amount is both added to the total and shown as one flying number, so:

- the shared total stays **exact** (integer sum);
- the message rate is bounded (≤ ~13 msg/s per dummy per shooter regardless of RPM);
- the **local shooter is untouched** — full vanilla per-hit numbers. Only remote peers see the coalesced number (e.g.
  one `180` instead of nine `20`s), which is also what keeps their flying-text pool from thrashing.

## Unlock gating (falls out of the lifecycle)

- The sender only broadcasts when its own dummy was hit — so it is unlocked there by definition.
- The receiver only drives a **locally registered** tracker, and registration happens in `Start`, which only runs for
  an **active** (= unlocked) dummy. A locked peer has no registered tracker → the relayed hit finds no match and is
  silently ignored.
- Nothing is ever spawned or revealed by the sync: it only drives an existing local tracker.

## Identity

The tracker's deterministic authored world position (`Start`-time `transform.position`; its `Update` only
`LookAt`-rotates, so it never moves), matched with a 1.0 m epsilon — the same keying as `NetChestOpen` /
`NetDoorBlockerOpen` / `NetBreakableBreak`. The hit point travels separately as `HitPoint` (where to spawn the flying
number + hay effect). Scene context (`MatchesScene`: chapter | level | seed) gates cross-level events.

## Files

| File | Role |
|------|------|
| `NetMessageType` `85` | `TargetDummyDamage` (peer → Host → relay) |
| `Gameplay/NetTargetDummyDamage.cs` | DTO (dummy key + summed amount + type/crit + hit point + scene) |
| `Gameplay/NetTargetDummyDamageCodec.cs` | wire codec (manual) |
| `Gameplay/TargetDummySyncManager.cs` | registry, capture + coalescing, `Tick` flush, relay apply, `Clear` |
| `Patches/TargetDummyPatches.cs` | `DamageTracker.Start` postfix (register) / `ShowDamage` postfix (capture) |
| `NetService.cs` | broadcast/send/handle/relay + dispatch + `Tick` flush |
| `NetGameplaySyncBridge` | `ReportLocalTargetDummyDamage` |
| `NetHandshake` | `ProtocolVersion 10` (TD-1 added `TargetDummyDamage`) |
| `CoopConfig` `[TargetDummy]` | `EnableTargetDummySync` (Fixed on), `LogTargetDummySync` (off — high volume) |

Reflection-only against `DamageTracker` (Gameplay assembly, not referenced); `DamageSourceData`
(`Core.Units`) / `DamageTypes` (`Core.Stats`) are Core types the mod does reference.

## Known boundaries (v1)

- **Remote flying numbers are coalesced**, not per-hit (deliberate — see above). The total is exact either way.
- **Remote totals lag** by the batch window + latency and land in chunks; they converge to the same value.
- **Reset skew**: if the last event reaches peers at slightly different times, their 5 s resets fire slightly apart.
  Cosmetic and self-healing (the value is ephemeral display state, not authoritative).
- **Registry is cleared per level transition** (alongside the chest / lootable / breakable registries) and rebuilt from
  `Start`. If the dummy ever lives in an area that is *never* reloaded, its `Start` would not re-run after a clear —
  not observed, but the thing to check first if numbers stop sharing after changing level.
- The dummy's **health/flash remain per-peer local** (unchanged, by design) — only the numbers are shared.

## Client-dealt hits (TD-1b)

The dummy is an `Npc` in the host roster, so a client's hit IS routed to the host — but the host applies it (like every
enemy) through the health-only `EntityStats.SetStatus` write, which **bypasses `onDamageRecieved`**, so `ShowDamage`
never fires for a client's hit and no number appeared on any screen (the dummy's ~1,000,000 HP just ticked down
invisibly; the white flash still showed because it rides a separate `HostHitVisual` channel). The client's own
`ReceiveDamage` is suppressed when the hit is routed to the host, so it produced no local number either.

Fix: right after the host applies a client hit (`TryApplyHostHitDamage`), it drives the number on its **own** dummy via
`TargetDummySyncManager.ShowHostNumberForClientHit(unit, actualApplied, …)` — a direct `ShowDamage` call, **not**
mirror-guarded, so the same TD-1 postfix captures it and broadcasts to every peer (the shooter included), exactly as the
host's own hits already sync. It is a no-op for any unit without a `DamageTracker` (every ordinary enemy) — gated by a
cached `GetComponentInChildren` lookup — and only fires for client-originated hits (the host's own hits never go through
`TryApplyHostHitDamage`), so each hit yields exactly one host-side `ShowDamage` and there is no double-count. The host is
the single origin of the number for a client's hit; the shooter sees it after one round-trip via the verified
host→client path.

**Known limitation**: `NetClientHitRequest` carries no damage type, so a client-dealt number uses `Normal`(7) colour on
every screen (the total and value are exact; only the colour of a client's numbers is generic). Adding the type to that
message is a wider change to the shared hit path (codec + protocol) — deferred.

## Base-game issue: two overlapping dummies per station (NOT this mod)

The ChurchHub spawns **two target dummies at each station**, at identical world positions. This is base-game
behaviour, **not** caused by this mod or by the damage-number sync. Evidence:

- It reproduces in **vanilla single-player with the mod fully uninstalled** (two foot-totals under the dummy).
- With the mod loaded but no client connected (solo host), the level generates **six** dummy `Npc` instances for the
  three stations (distinct instance ids), before any remote player / ghost stand-in is registered — so it is not the
  per-player logic and not the roster sync. The mod patches nothing on the spawn/pool path.
- The redundant copy is the same variant as its twin, so on the host they overlap into what looks like one dummy per
  station (three visible). A joining client can see them separated because the roster position-binding of two
  identical-position units is ambiguous, which is why the doubling is more obvious client-side.

Consequences visible through this feature, all downstream of the base-game double-spawn:

- **Two foot-totals** can appear at a station — each overlapping dummy carries its own `DamageTracker`, so each keeps
  its own total. Which tracker a shot feeds depends on which collider the ray hits; the position-keyed relay
  (epsilon 1 m) may resolve to the other twin, so a station can show two running totals.
- **The client can see up to six dummies** rather than three.

No workaround is applied — this is left to the base game to fix. A mod-side mitigation (collapsing the redundant twin
via the `DamageTracker.Start` hook) was considered and **declined**: the dummy is a host-roster `Npc` whose client-hit
routing binds by roster spawn index, and disabling a different twin on each peer (spawn order differs) risks breaking
that binding — not worth it for a cosmetic base-game artifact.
