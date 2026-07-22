# Desert Crypt Co-op Sync — Plan (Route A: host-authoritative)

Status: in progress. Feedback: "the basement key door does not open for the other player; the
challenge is random and desyncs (one end hunts chickens, the other kills enemies); enemies spawn
out of sync; entering the crypt activates the challenge and that activation must sync." Route chosen
by the maintainer: **A — treat the crypt challenge as a host-authoritative shared-world event**
(project invariant #1), the same shape as a boss arena.

The exit-door soft-lock (LD-Crypt, commit `3402c99`) is already fixed and is NOT part of this plan.

## Vanilla anatomy (verified against v0.18.5 decompile)

- **Entrance key door** — `PerfectRandom.Sulfur.Core.OpenableDoor` (Core, referenced). Opened by a
  `KeyStation` service station that consumes an `Item_CryptKey` (id 418). The single funnel that
  actually opens the door is the private `OpenableDoor.Open()` (`animator.SetTrigger("Open")` +
  `onTriggerEventsOpen`), reached however the door was triggered. There is exactly **one** key in the
  shared world, so without sync the second player can never enter.
- **Challenge activation** — a `PlayerTrigger` named `PlayerStartTrigger`
  (`persistent=[CryptChallengeManager.StartChallenge, FogChangeTrigger.Trigger]`) fires when a player
  crosses the entry. It is per-end: each player's own crossing calls `StartChallenge` on its own end
  (Log522, both ends).
- **Challenge selection** — `CryptChallengeManager.Awake` calls `SelectRandomChallenge()` →
  `challengeComponents[UnityEngine.Random.Range(0, count)]` on the **global** RNG, then `SetActive`s
  only the chosen child. Same defect shape as GH-1: identical seed, divergent pick (Log521:
  `firstDivergence host=CorruptedAmalgamation client=CryptTurtle`, `mismatch=71`). Types:
  `CryptDefeatEnemiesChallenge` (`isDefeatEnemiesChallenge` → Defeat, else Survive; `Hunt` variant =
  "find N units"), `CryptProtectTargetChallenge` (defend crystals).
- **Enemy spawning** — `CryptPeriodicEnemySpawner`. `OnStartChallenge → StartTargetEnemiesMode →
  spawner.StartSpawning()`, then `SpawnSingleEnemy` picks `unitSO = list[Random.Range(0, count)]` and
  `spawnIndex = Random.Range(0, n)` from the **global** RNG, **asynchronously and repeatedly over
  time** via `unitSO.SpawnUnitAsync(this, pos)`. This is NOT fixable by seed injection — the number of
  global-RNG calls diverges across ends (the exact case GH-1's ledger flagged as unenforceable). It is
  also NOT covered by the existing `RuntimeSpawnManager` owner whitelist
  (`DevTools/TriggerSpawner/EndlessModeManager/FloatingCardManager` only), so today both ends spawn
  independently.
- **Outcome** — `CryptChallengeManager.OnChallengeCompleted` (alert + `CryptUI.TurnOff` + achievements)
  and `OnChallengeFailed` (alert + **`GameManager.Instance.PlayerUnit.Die()`** — a failed challenge
  kills the player). A shared challenge's win/lose must be one decision for both players.

## Key conclusion (answers the "does spawn auto-sync?" question)

**No.** Syncing *which* challenge is selected does not make enemies match, because the spawner rolls
unit type + spawn point from the global RNG independently on each end, asynchronously, an unbounded
number of times. Enemy spawning needs host authority no matter what. Given that, and given the
failure-kills-the-player outcome, the whole challenge lifecycle is host-authoritative (Route A).

## Design

Crypt identity across ends: the crypt is generated from the shared level seed, so its
`CryptChallengeManager` sits at the same world position on both ends. Key by rounded world position
(the DB-1 / gate keying), tolerant to float drift.

### KD — Key door open sync (independent, prerequisite; do first)
Clone of DB-1 (`DoorBlocker`) for `OpenableDoor`:
- Patch `OpenableDoor.Open()` (postfix) → capture, broadcast the door's world position.
- Receiving peer resolves the matching local `OpenableDoor` by position and reflect-invokes its private
  `Open()` (no key consumed, no `KeyStation` needed on the mirror side). Guard with an
  `IsApplyingMirror` flag so the mirror's own `Open` postfix never re-broadcasts.
- Open-only, one-way (a door that opened stays opened) — closeable-door *close* sync is out of scope,
  exactly as DB-1 skips the trap-door slam. Mirroring an open for everyone is always safe.
- New wire message `OpenableDoorOpen = 80`; protocol version bump.

### CS — Challenge selection (host-effective via deterministic seed)
`CryptChallengeManager.Awake` runs during generation on both ends, before any per-crypt network
round-trip is reliable. Rather than race a broadcast against `Awake`, make the pick a pure function of
the shared seed — the GH-1 approach, which is host-authoritative *in effect* (both ends compute the
same answer with no message): seed the global RNG from `levelSeed ⊕ hash(cryptPosition)` for exactly
the `SelectRandomChallenge` call, then restore `UnityEngine.Random.state`. Single-player unaffected in
behaviour (distribution unchanged; pick becomes a function of the seed, like every other gen choice).

### SP — Enemy spawn (host-authoritative mirror + client suppression)
- Add `CryptPeriodicEnemySpawner` to `RuntimeSpawnManager.ClassifyOwner` (host broadcasts its
  `SpawnUnitAsync` units as `RuntimeSpawn` puppets).
- Client suppresses its own crypt spawns: block `CryptPeriodicEnemySpawner.SpawnSingleEnemy`
  (and `StartSpawning`) on a linked client, mirroring the host's instead (same pattern as the Endless
  wave / death-spawn suppression).

### AC — Activation + lifecycle (host-authoritative)
- On a client, the local `PlayerStartTrigger → StartChallenge` must not run the real challenge (no
  spawners, no timer-driven lose). Client suppresses its own `CryptChallengeManager` lifecycle and
  instead mirrors: host broadcasts *challenge started* (per crypt), client shows the `CryptUI` and
  relies on SP for enemies.
- Host broadcasts *challenge ended (success/fail)* per crypt; client applies the same outcome
  (UI + the failure consequence), so a shared loss is one decision. The exact client-side failure
  handling (does the client player also die?) is an open question — see below.

## Open questions / risks
- **Failure outcome on the client.** Vanilla kills the local player on fail. For a shared challenge,
  should both players die, or only down? Needs a maintainer call before AC lands.
- **`OpenableDoor.Open` scope.** Patching it syncs *every* openable door, not just crypt key doors.
  Open-only mirroring is safe, but confirm no closeable door relies on per-end open state.
- **Selection timing.** Confirm `CryptChallengeManager.Awake` runs while the level seed is available
  on the client (it should — same window as `SpawnGhost`), else CS falls back to a host broadcast.
- **Testing needs ≥2 machines**, a desert level containing a crypt, and both the entry and each
  challenge type (Defeat / Survive / Hunt / Protect).

## Phasing (each independently buildable + verifiable)
1. **KD** — key door open sync. Unblocks the second player entering; self-contained. ← current
2. **CS** — deterministic challenge selection. Both ends pick the same task.
3. **SP** — spawn host-authority + client suppression. Enemies match.
4. **AC** — activation + outcome host-authority. Timer/win/lose consistent.
