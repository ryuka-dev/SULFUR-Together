# Black Guild Cardinal — audit + sync (Phase BGC)

The cardinal fight had never been synced. This document is both halves: the reverse-engineered walk of the vanilla
fight (§1) and the slice built on it (§2). Method and field names are exact, read from the `v0.18.5` decompile of
`PerfectRandom.Sulfur.Gameplay.dll` (the build the project compiles against).

Related: [BossSyncReuseMap.md](BossSyncReuseMap.md) §3 (the new-boss recipe) and §6 (the "remote start" audit rule
this follows), [BossAuthority.md](BossAuthority.md) (the boss framework — which this fight deliberately does *not*
join, see §1.5).

---

## 1. Vanilla

Three classes, all `MonoBehaviour`, all in namespace `PerfectRandom.Sulfur.Gameplay`. The assembly is not referenced
by this project, so every hook is reflection-resolved.

- **`CardinalFightHelper`** — the room. Serialized: `difficulty` (1–5), `spawnPoints` (`CardinalSpawn[]` — the
  alcoves), `cardinalObjects` (`List<GameObject>` — the cardinals), `music` (`MusicTrigger`).
- **`CardinalHelper`** — one cardinal. Serialized: `spawnHelper` (its starting alcove), `sequence` (the room),
  `richAI`, `npc`, two teleport `AudioClip`s.
- **`CardinalSpawn`** — one alcove. A single public `bool isOccupied`.

The cardinals are ordinary `Npc` units (`UnitIds.BlackGuildCardinal`, value 7) parked in alcoves with their `RichAI`
disabled at `Start`. Their movement model is **teleport between alcoves**, not pathfinding — the same fixed-pool
shape as the Cousin (BossAuthority.md §5), which is why the same recipe applies.

### 1.1 `CardinalFightHelper.Start()` — the cull

```csharp
int num = 5 - difficulty;
for (int i = 0; i < num; i++) {
    int index = UnityEngine.Random.Range(0, cardinalObjects.Count - 1);
    cardinalObjects[index].GetComponent<CardinalHelper>().spawnHelper.isOccupied = false;
    Destroy(cardinalObjects[index]);
    cardinalObjects.RemoveAt(index);
}
// survivors: SetInvulnerable(true) + SetHitboxesInvulnerable(true)
```

**How many** cardinals survive is pure `difficulty` — deterministic. **Which** ones survive is the global
`UnityEngine.Random`, whose stream position differs between two peers regardless of the level seed.

### 1.2 `StartFight()` — no code caller

`StartFight`, `EndFight`, `StartTeleport` and `ExecuteTeleport` are all `public` with **zero callers anywhere in
either game assembly**: they are invoked from scene data (a `PlayerTrigger`'s `onTriggerEvents`, an animation event,
or a behaviour-tree `ExecuteFunction` node — the same pattern as the Emperor's `StartMovement`, which Log254 pinned
to a NodeCanvas dialog node). So each runs on whichever end's own local flow fired it.

```csharp
public void StartFight() {
    playerTransform = StaticInstance<GameManager>.Instance.PlayerObject.transform;   // LOCAL player only
    StartCoroutine(StartFightRoutine());                 // 6 s, then CardinalHelper.StartFight() on each survivor
    StartCoroutine(SetClosestSpawnsToOccupiedRoutine()); // 1 Hz
    music.StartMusic();
}
```

`CardinalHelper.StartFight()` is the step that clears `SetInvulnerable` / `SetHitboxesInvulnerable` and opens the
alcove door (`currentSpawn.GetComponentInChildren<Animator>().SetTrigger("Open")`).

### 1.3 `SetClosestSpawnsToOccupiedRoutine()` — reserves one alcove, for one player

Every second: clear the previously reserved alcove, find the alcove nearest `playerTransform`, mark it
`isOccupied = true`. That is the game's "don't teleport on top of the player" rule, and it knows about exactly one
player.

### 1.4 `getUnoccupiedSpawn()` / `ExecuteTeleport()` — the destination roll

```csharp
public CardinalSpawn getUnoccupiedSpawn() {
    int num = UnityEngine.Random.Range(0, spawnPoints.Length);   // global RNG again
    for (int i = 0; i < spawnPoints.Length; i++) {
        int num2 = (i + num) % spawnPoints.Length;
        if (!spawnPoints[num2].isOccupied) return spawnPoints[num2];
    }
    Debug.LogError("No un occupied spawnPoint in CardinalSpawn list");
    return null;                                                  // vanilla NREs one line later if this happens
}
```

`StartTeleport()` is the wind-up (`isTeleporting`, animator bool, source-alcove clip). `ExecuteTeleport()` rolls the
destination, swaps the `isOccupied` flags, calls `npc.TeleportTo(dest)` — a hard `Rigidbody.position` +
`transform.position` write with velocities zeroed — then opens the destination alcove and plays the second clip.

### 1.5 What breaks in co-op — the "remote start" walk

Walking the chain from the entrypoint to "a remote player can see it, be attacked by it, and damage it", per the
BossSyncReuseMap §6 rule:

| # | Local-only decision | Consequence in co-op |
|---|---------------------|----------------------|
| 1 | `Start()` cull uses the global RNG | The two ends keep **different cardinals**. The roster binds by `chapter\|level\|seed\|unitId\|idx\|position`, so each end's unmatched cardinal is unbindable: on the client it is quarantined into a motionless statue (`TryQuarantineClientOnlyEnemy`), and the host's extra cardinal is **invisible on the client but still shooting it**. Same defect shape as GH-1 (issue #6) and the crypt challenge (CS). |
| 2 | `StartFight()` fires per-end from scene data | Whichever end's player crossed the trigger arms its cardinals; the other end keeps a room of **invulnerable, inert** cardinals — and since client damage is applied by the host's real `ReceiveDamage`, a host that never started the fight rejects every client hit. |
| 3 | `playerTransform` = local player | The alcove reservation protects one player per end. |
| 4 | `getUnoccupiedSpawn()` global RNG | Each end picks a different destination. |
| 5 | `ExecuteTeleport` reachable from the teleport clip's animation event | The client's animator still runs (its cardinal is a host-driven puppet with AI off, but animations are mirrored), so the client can fire its own `ExecuteTeleport` and roll a divergent destination. |

**Not a registered boss encounter.** `CardinalFightHelper` does not derive from `BossFightHelper`: there is no boss
health bar, no `BossPhases`, no dialog, no adds, no single health `Unit`. The `IBossEncounterAdapter` framework
(dialog-commit, encounter key, `HostBossState`) has nothing to attach to. This is a **room encounter** of ordinary
roster enemies, so it is synced the way the crypt challenge and the gates are: a small dedicated manager on its own
message. Position, health, damage routing and death already come free from the existing enemy roster/puppet
pipeline — only the three decisions above needed authority.

---

## 2. The slice

Message **`CardinalFightEvent = 102`** (`ProtocolVersion` 32 → 33). One channel, four kinds, routed by kind in
`NetService.BroadcastLocalCardinalFightEvent`: only `FightStartRequest` travels client→host; the rest are host→all,
rejected at the sender otherwise so a bug cannot make a client a second authority.

Files: `src/Networking/Gameplay/NetCardinalFightEvent.cs` + `…Codec.cs`,
`src/Networking/Gameplay/CardinalFightSyncManager.cs`, `src/Patches/CardinalPatches.cs`.
Config: `EnableCardinalSync` (`Fixed<bool>`, always on) + `LogCardinalSync`. Log tag `[Cardinal]`.

### BGC-1 — the cull becomes a function of the level seed

`CardinalFightHelper.Start` prefix/postfix seeds `UnityEngine.Random` from `GameManager.currentSeed`, salted and
mixed with the room's rounded world position (so two cardinal rooms in one level do not cull identically), and
restores `Random.state` afterwards. Scoping the reseed to that one call is the design point, exactly as in
`CryptChallengePatches` (CS) and `GhostSpawnPatches` (GH-1): it does **not** depend on the two ends having issued
the same number of global-RNG draws beforehand. No level seed available ⇒ vanilla behaviour is left alone and a
warning is logged rather than inventing a seed.

This is what makes everything below cheap: once both ends keep the same cardinals, `cardinalObjects` is the same
list in the same order on both ends, and `spawnPoints` is a serialized array — so a **plain index** is a valid
cross-end identity and no position matching is needed for the entities themselves.

> **Intentional behaviour change, single player included** (same call as CS): which cardinals a room keeps becomes a
> function of the level seed rather than re-rolled per playthrough. The distribution is unchanged and the count was
> always fixed by `difficulty`. Not gated on multiplayer — that would make one seed mean different things in the
> same install.

### BGC-2 — host-authoritative fight start

`CardinalFightHelper.StartFight` prefix/postfix. A linked client **blocks** its own start and sends
`FightStartRequest` (keyed by the room's world position); the host runs the **real** `StartFight` and its postfix
broadcasts `FightStartCommit`; every end then runs the same native chain, so the 6 s arming coroutine, the alcove
doors, the invulnerability clear and the music all happen together. The host's own trigger commits inline. Dedup is
by helper instance id, so a re-firing trigger and a host re-entry from a client request each commit once.

**Fail open.** A client that blocked its start and hears no commit within 5 s (host in another level, packet lost)
starts the fight locally and logs a warning — a desynced start is bad, a room of permanently invulnerable cardinals
is worse.

### BGC-3 — host-authoritative teleport

`CardinalHelper.StartTeleport` / `ExecuteTeleport` prefix/postfix. A linked client blocks both (defence against the
animation-event path, #5 above); the host broadcasts `TeleportBegin` (cardinal index) and, after the native method
has written it, `TeleportExecute` (cardinal index + alcove index + the destination world position).

The client replays the **real** native methods under a mirror guard, with the host's alcove forced into
`getUnoccupiedSpawn` by a prefix — so the alcove door animator, both teleport clips, the physics/navmesh nudge and
the `isOccupied` bookkeeping all happen exactly as on the host, instead of the cardinal silently sliding to a new
position on the puppet snapshot. The alcove index falls back to nearest-position matching; if neither resolves the
replay is **skipped entirely** (the puppet snapshot still carries the position — a missed animation, not a
divergence) rather than allowed to roll its own.

The mirror guard (`IsApplyingMirror`) is read by every capture hook: replaying a native method replays our own hooks
on it too, and a replay is not a local decision (BossSyncReuseMap §6).

### BGC-4 — a destination must not sit on any player

`getUnoccupiedSpawn` postfix. Vanilla reserves only the alcove nearest the **local** player, so in co-op every
remote player is a valid landing spot for a native `Unit.TeleportTo` — and a kinematic collider materialising inside
a player is the PK-3 fling (234 m, out of bounds). The postfix re-picks when the chosen alcove is within 4 m of any
player unit (host player + ghost proxies, via `CousinArmPatches.GatherPlayerUnits`), skipping occupied alcoves.
Fails open: if no free alcove is clear of everyone, vanilla's pick stands.

### Deliberately not done

- **Arena lockdown / room membership (LD-2, RM).** The cardinal room is not a sealed boss arena and has no
  `MetalGate` seal trigger of its own; adding a lockdown would be a gameplay change, not a sync fix.
- **`EndFight()`** is left per-end. It only calls `StopAllCoroutines`; on both ends it is driven by the same
  all-cardinals-dead condition, and deaths are already host-authoritative.
- **Joining the boss-encounter framework.** See §1.5 — there is nothing for an adapter to bind to.

---

## 3. Verification status

Built clean (0 errors, no new warnings). **Not yet live-tested** — the test procedure is in the phase registry
(`Versioning.md` §4). Nothing here can be confirmed by compilation alone: BGC-1's parity, BGC-2's request/commit
round trip and BGC-3's replay all need a two-end run through the room.
