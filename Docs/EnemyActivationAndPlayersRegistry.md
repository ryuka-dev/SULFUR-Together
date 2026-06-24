# Enemy Activation & Multiplayer Players Registry

Reverse-engineered from `PerfectRandom.Sulfur.Core.dll` (ilspycmd). This pins down **why enemies do
not wake when a client walks ahead of a stationary host**, and the host-authoritative fix
("Plan B"): register each remote player as a headless entry in `GameManager.Players` so the game's
already-multiplayer-aware detection layer engages clients natively, plus a patch to the one place
that is *not* multiplayer-aware — the activation (wake) LOD.

---

## 1. Symptom

Host stands still, a client runs ahead. Past a certain distance the enemies around the client are
inert "statues": no aggro, no pathing, no attack. Walking the host over wakes them.

## 2. The three layers (reverse-engineered)

| Layer | Class / method | Multiplayer-aware? |
|---|---|---|
| **Activation / wake** | `NpcUpdateManager.LateUpdate` | ❌ **only the host singleton** |
| **Detection / LOS** | `BatchedNPCRaycasts.Update` + `LateUpdate` | ✅ iterates `GameManager.Players` |
| **Target select** | `AiAgent.GetTarget` | ⚠️ partial (`onlyTargetPlayer=false` reads `hostilesInLOS`; singleton-bound otherwise) |

### 2.1 Activation entry — `NpcUpdateManager.LateUpdate` (the root cause)

```csharp
int roomIndex = GameManager.Instance.PlayerUnit.currentRoom.roomIndex;   // host singleton
Vector3 position = GameManager.Instance.PlayerObject.transform.position;  // host singleton
float num2 = worldEnvironment.npcActiveDistanceToPlayer²;                 // default 200 (Switch 100)
foreach npc in GameManager.Instance.npcs:
    if (npc.excludeFromNpcLOD) continue;
    sqr = (position - npc.pos).sqrMagnitude;
    if (npc.gameObject.activeSelf) { npc.AiAgent.DistanceToPlayer = sqrt(sqr); }
    else {
        if (!(sqr < num2)) continue;                                      // TOO FAR from host → never wakes
        ... room gate: (npc.room.roomIndex - roomIndex) < npcActiveRoomMargin (default 4) ...
        _npcsToActivate.Enqueue(npc);
    }
// dequeued 16/frame: npc.gameObject.SetActive(true); npc.ActivateBehaviour();
```

Inactive NPC → its `GameObject` is disabled → `AiAgent.OnEnable` never runs → `PeriodicScan`
coroutine never runs → total statue. **Both gates use only the host singleton**
(`PlayerObject`/`PlayerUnit`). This is the single LOD that the local-splitscreen co-op heritage
never wired to the `Players` list (one shared screen → one activation anchor was enough).

### 2.2 Detection — `BatchedNPCRaycasts` (already multiplayer)

`SetupNpcList` sizes everything to `GameManager.Players.Count`; `Update` caches
`players[j].cameraRoot.position`, raycasts each NPC against **every** player, and `LateUpdate`
fills each NPC's `hostilesInLOS` with `players[index].playerUnit`. Hostility uses a **hard-coded
`playerFactionId = 16` (Player)** for all players — so a registered player is treated as a hostile
target regardless of its unit faction. `ReportLastSeen` group alerts are dispatched by
`players[playerIndex]`.

### 2.3 Target select — `AiAgent.GetTarget`

`onlyTargetPlayer == false` enemies return `hostilesInLOS.LastOrDefault(alive)` — which **can be a
remote player's `playerUnit`** once detection above populates it. The singleton-bound branches
(`onlyTargetPlayer`, the `flag` LOS check, the `overridetargets` + `<10f PlayerPosition` override)
only matter for player-only hunters and the force-target path.

## 3. Why the old faction proxy failed (retired by Plan B)

`RemotePlayerTargetProxyManager` (Phase 5.5-P3-A2) built a `faction=Player` `Unit` and registered it
in `GameManager.units`, expecting the AI to "scan and detect" it. But detection
(`BatchedNPCRaycasts`) **never scans `units` by faction** — it only raycasts entries of
`GameManager.Players` and only adds `players[i].playerUnit` to `hostilesInLOS`. A proxy that is not
in `Players` can never enter `hostilesInLOS` (log61: "proxy alive+Player but never in hostilesInLOS").
The only thing that worked was `ForceAggro` (`AiAgent.overridetargets.AddUnits`), which bypasses
detection but cannot wake a sleeping enemy. Default-OFF flags there were disabled for real reasons
(e.g. `RemotePlayerTargetProxyBodyBlocker` corrupts the projectile AutoPool — log72).

## 4. `GameManager.Players` consumer audit (blast radius of a ghost entry)

Only **9 references / 5 methods** read `Players`; only `AddPlayer` writes it.

| Consumer | Effect of a ghost player | Requirement |
|---|---|---|
| `BatchedNPCRaycasts` (×5) | ✅ the goal: client detected/targeted | ghost `cameraRoot` + `playerUnit` non-null |
| `*.GetClosestPlayer` (throw AI) | ✅ enemy engages client | ghost root `transform` at remote pos |
| `Flashbang.TriggerFlashbang` | ⚠️ blinds ghost unit | `playerUnit.Stats` valid (else guard) |
| `Player.OnBegin/OnEndCameraRendering` (×2) | 🔴 host's own camera callback derefs `players[k].activeHoldableRenderers.Length` | **ghost `activeHoldableRenderers = new Renderer[0]`** |

Confirmed **absent**: no `Players`-driven all-players-dead / game-over, no `Players.Count`-driven
splitscreen viewport split (that lives on the `AddPlayer`/`SpawnPlayer` path we bypass), no
per-player save loop over `Players`. Singletons (`PlayerScript/PlayerObject/PlayerUnit/PlayerPosition`)
are **not** touched as long as we do **not** call `AddPlayer` (it overwrites them) and insert into the
list directly.

## 5. `Player` MonoBehaviour lifecycle hazards

- `Start()` → `playerUnit = GetComponent<Unit>()` (would clobber our field)
- `OnEnable()` → registers `RenderPipelineManager.begin/endCameraRendering` callbacks
- `Update()` → derefs `inputReader` / `cameraRootAnimator` (NRE for a headless ghost)

**Mitigation: the ghost `Player` is kept `enabled = false`.** A disabled component runs no
`OnEnable`/`Start`/`Update`; `Players` consumers only read fields, which work on a disabled
component. All fields are set manually. (Ghost's own camera callbacks are never registered, and even
if they were they early-return on `camera != playerCamera` since `playerCamera == null`.)

## 6. Plan B design

**Host-only.** For each in-scene, alive (not downed/dead) remote player:

1. A follow `GameObject` tracking the remote player's networked position (reuses the same
   per-peer position source as `RemotePlayerTargetProxyManager`).
2. A minimal `Unit` (`playerUnit`): `SetStats(playerUnitSO)` → `Spawn()` (alive) → `OverrideFaction(Player)`.
   Optionally a `Hitmesh` so native attacks land (damage routing to the client is a later step; for
   now hits are cosmetic — enemies engage, the client is unharmed).
3. A child `cameraRoot` transform at eye height (LOS ray origin for `BatchedNPCRaycasts`).
4. A `Player` component, **`enabled = false`**, fields set manually:
   `playerUnit`, `cameraRoot`, `activeHoldableRenderers = new Renderer[0]`,
   `playerVisuals = new Renderer[0]`, unique `playerIndex`.
5. Inserted into `GameManager.Players` **directly** (reflected list `.Add`, never `AddPlayer`),
   so detection/targeting/LOS/group-alerts engage natively. Removed on peer gone/downed and on
   scene change.

Plus the mandatory **activation patch** (independent of the registry, needed regardless):

6. **`NpcUpdateManager.LateUpdate` Postfix** → after the native host-only pass, scan
   `GameManager.npcs` and `SetActive(true)` + `ActivateBehaviour()` any inactive, non-excluded NPC
   within `MultiPlayerNpcActivationDistance` of **any** remote player position, rate-limited to
   `MultiPlayerNpcActivationsPerFrame`. (Distance-only first cut; the native room-index gate is an
   optimization we deliberately skip.)

### Config (all default **OFF** — experimental until in-game tested)

| Key | Default | Purpose |
|---|---|---|
| `EnableMultiPlayerNpcActivation` | false | the activation Postfix (the direct fix for §1) |
| `MultiPlayerNpcActivationDistance` | 60 | wake radius around a remote player |
| `MultiPlayerNpcActivationsPerFrame` | 8 | per-frame activation budget |
| `EnableRemotePlayerInPlayersList` | false | register headless `Player` in `GameManager.Players` |
| `EnableGhostPlayerHitbox` | false | item ① — ghost `Hitmesh` so enemy hits route to the client (needs `EnableDamageProbe`) |
| `LogRemotePlayerRegistry` | true | verbose create/update/destroy/register logging |

### Relationship to existing features

- This **supersedes** the faction-detection premise of `RemotePlayerTargetProxyManager`. The
  ForceAggro path can remain as a fallback but is redundant once registry detection works.
- `EnableMultiPlayerNpcActivation` is the **only** piece that addresses the reported "won't wake"
  bug; the registry addresses "once awake, does it fight the client".

## 7. Status — VERIFIED WORKING

- [x] Entry located, data layer + audit + lifecycle reverse-engineered (this doc).
- [x] Config + `RemotePlayerRegistryManager` + `NpcUpdateManager.LateUpdate` postfix + NetService wiring.
- [x] **Activation verified (LogOutput93): host stationary, client ahead → `activation woke N npc(s)`, client loads/sees enemies. No activation-side errors.**
- [x] Two registry bugs found & fixed (build `5.7-B2`):
  - `Unit.SetupBreakableArmor` NRE on the ghost's `Unit.Start` → register the ghost unit via `RemotePlayerTargetProxyManager.RegisterExternalProxyUnit` so the existing `Unit_SetupBreakableArmor_Pre` guard skips it.
  - `BatchedNPCRaycasts.Update` IndexOutOfRange **every frame** — adding a player mid-level desyncs its native arrays (sized at `SetupNpcList` time). Fix: call `BatchedNPCRaycasts.SetupNpcList()` after every Players add/remove.
- [x] **Re-verified (LogOutput94): build `5.7-B2`, `setupNpcList=True`, 24 activations, ghost lifecycle balanced (4 reg / 3 removed). Whole host log has `NullReferenceException = 0` and `BatchedNPCRaycasts IndexOutOfRange = 0`. Registry clean.**
- [x] **Item ① ghost damage routing (build `5.7-B3`) — VERIFIED (LogOutput95)**: ghost `Hitmesh` on the enemy attack layer (hitboxMask = layer 6) so native attacks land on it. The ghost unit is already in `_proxyUnitPeers`, so the **existing A3 forward** (`Unit_ReceiveDamage_Pre` → `ReportHostAuthoritativeEnemyDamage(peer,…)`) routes each hit to the client's real player and suppresses the ghost's own health (stays alive = persistent aggro). Confirmed: `[EnemyDamageAuthority] Host sent damage target=client-1 … reason=enemy via target proxy`. Gated by `EnableGhostPlayerHitbox` (default OFF). **Dependency: the A3 forward lives inside the damage-probe prefix, so it also needs `EnableDamageProbe = true`.**
- [x] **Log-noise fix (build `5.7-B3b`)**: LogOutput95 stutter ≠ item ①. Plan B activated a large ranged group (BlackGuildCultist) → diagnostic-log flood (8750 lines: `[Npc]` per-shot 3192, `[AttackPhase] Host broadcast` 2591, `[PosDiag]` 593). Synchronous host file I/O + reflection (PosDiag `GetComponentInChildren`) hitches the host frame → client stutter (LogOutput96, fewer enemies, no stutter). Fix: gated the 4 unconditional `[Npc]` combat logs behind `LogEnemyCombatProbe`; dev-defaults now force `LogEnemyCombatProbe=false` + `LogHostAttackPhaseEvents=false`; `LogTeleportDiag` off in cfg. Functional events/network broadcasts untouched (`EnableHostAttackPhaseEvents` stays on). NOTE: the AttackPhase **network broadcast** volume for large ranged groups is a deeper pre-existing scaling concern — if stutter persists with logs quiet, it needs throttle/coalesce (separate task).
- [x] **Ghost-leak fixes (build `5.7-B4`, from LogOutput98)**:
  - *Roster leak* — the ghost `Unit` was tracked as a world entity and broadcast to clients as a phantom "Other" (62 lines of `No local match / special mismatch host=RemotePlayerGhost`). Fix: a single chokepoint guard at `NetGameplayProbeManager.ReportSpawn` skips any `IsProxyUnit` entity, so the ghost never enters entity tracking → never in `BuildHostWorldRoster` / `BuildLevelManifest`.
  - *NextLevelTrigger* — the ghost's collider (enemy-hit layer) overlapped a level-exit trigger on the host. Fix: `NLT_OnTriggerEnter_Pre` is now a bool prefix (installed unconditionally, not gated by `EnableLevelProbe`) that returns false for ghost colliders (`RemotePlayerRegistryManager.IsGhostCollider`, matched by root-GameObject instance id).
- [x] **Ghost Hitmesh hit-sound NRE (build `5.7-B5`)**: enemy melee on the ghost NRE'd in `Hitmesh.GetPhysicsMaterial` (`hitShapes.Length` on a null array). Fix: ghost Hitmesh gets an empty `hitShapes` array → `GetPhysicsMaterial` returns null → `PlayMeleeSound` no-ops (no hit sound, no NRE; damage still forwards).
- [ ] Later (out of scope): per-player hazard guard for the ghost (web/slow + `PunishPlayerTrigger`, same family — §10); optional room-gate parity; `onlyTargetPlayer=true` hunters still only chase the host singleton (item ②).

## 7b. Combat-event flood control (`HostDrivenProxy` config — caused by Plan B activating large groups)

Activating big groups near a client surfaced network-broadcast floods that stutter the client. Evolution:

- **B5 distance gate (`GateCombatEventsByInterest`) — REVERTED (B6)**: skipped attack events for enemies far from all players. Over-skipped because remote-player interest positions drop a far-ahead client (`IsVisible` filter) → enemies the client was fighting froze (LogOutput101: 1017→38 events). Removed.
- **B7 target-aware burst suppression — SUPERSEDED (B8)**: skip NPC-targeted attacks only during a high-rate burst. Never engaged in testing (rate never hit the threshold), and the dominant flood turned out to be *damage* events, not attack-phase. Removed.
- **B8 unified per-entity coalescing/throttling (`EnableCombatEventCoalescing`, default ON) — VERIFIED (LogOutput103)**: the keeper.
  1. **enemy→client damage** (`NetPlayerLifeManager`): ACCUMULATED per `(peer,damageType)`, flushed once per `EnemyToClientDamageCoalesceSeconds` (0.1s) via `FlushPendingEnemyToClientDamage` in Tick. Total damage preserved; feedback batches; per-type so status effects aren't merged.
  2. **enemy→NPC damage + health events**: THROTTLED per enemy (`EnemyDamageEventMinIntervalSeconds` 0.07) — intermediate events dropped (display only; health converges via the periodic enemy-state snapshot). **Death health state (`isDead`) is never throttled.**
  3. **attack-phase animation events**: THROTTLED per enemy (`AttackPhaseEventMinIntervalSeconds` 0.08), uniform (no target check) — replaces B7.
  - Verified: `attackPhaseThrottled=7097 enemyDamageEventThrottled=1043`; enemy→client damage 841→199; client not frozen.
  - Tunable via the three `*Seconds` keys; whole thing off with `EnableCombatEventCoalescing=false`.

## 8. Not caused by this phase (recorded, not fixed)

### 8.1 Enemy roster-binding ordering (RECURRING — escalating)

LogOutput94 showed an elite `GoblinBarrelBoy` (barrel that breaks into 3 goblins) whose death-adds
were **not synced** — host and client each spawned their own 3 (different ids, ~0.5–1 unit apart).

Root cause is a **pre-existing level-gen roster-binding ordering bug**, not Plan B:
- The `GoblinBarrelBoy` (idx=22) spawned deterministically on both sides via
  `FinalizeAndMutateUnitsNode` (same seed/pos), but the client's `LevelManifestDiff` ran **before**
  the client had spawned that unit → recorded as `hostOnly … no local candidate (record only, no
  spawn in v1)` → never bound. Throughout combat: `[AttackPhase] Client no roster binding for
  hostIdx=22`.
- Unbound ⇒ the client simulates that enemy autonomously ⇒ on death both sides independently run
  the barrel-break and each spawn 3.

Belongs to the existing Phase 5.3-E/H manifest ("record only, no retro-bind") and Phase 5.5-RT
(RT3b/RT4 runtime-spawn sync) gaps. Plan B does **not** touch roster binding / manifest / client
simulation — it only makes such divergences **more visible**, because the host now actually
simulates the enemies a client engages (previously those enemies were dormant on the host).

**Escalation (LogOutput103):** now clearly impactful, not a rare edge — `rosterReceived=27 rosterBound=8`
(only 30% bound), 567 `No local match`, plus a downstream client flood of 12×
`BatchedNPCRaycasts.Update → Transform.get_position` NRE (a puppet enemy left in the live lists with a
destroyed transform). Unbound enemies desync damage/death/state.

#### FIXED — Phase 5.7-RB (2026-06-24)

Both halves implemented; gates default ON, reversible.

1. **Retro-active binding (`EnableRetroactiveEnemyBinding`)** — the "v2" the `no spawn in v1` comment
   anticipated. A small ledger (`_pendingHostBindLedger`, keyed by stable hostIdx) parks every
   unmatched host record from **both** binding authorities:
   - `ProcessHostWorldRoster` — the `No local match` branch now calls `RecordPendingHostBind(...)`.
   - `ReconcileHostManifest` — the `hostOnly … no local candidate` branch now parks it (log text
     changed to "parked for retro-bind").

   When the client later spawns a unit, `ReportSpawn` (the single client-side spawn chokepoint) calls
   `TryRetroactiveBindNewLocalEntity`: it finds the parked host entry of the same `UnitIdentifier`
   whose hostIdx isn't already bound (closest by position; a >5 m tie-break among multiple same-type
   siblings is deferred rather than mis-assigned, since level-gen positions are deterministic), binds
   it both ways, un-quarantines it, drains any pending health state, and drops the ledger entry.
   Parked entries that the client never produces a match for expire after 30 s
   (`ExpireStalePendingHostBinds`, run from `Tick`). Retro-bindings survive the next roster reconcile
   via the existing RT3-A7 `preservedHostToLocal` path. Verify with `[RetroBind] bound late-spawned …`
   and the new `retroLedger=`/`retroBound=` fields on the WorldRoster / LevelManifest "complete" lines.

2. **Destroyed-unit list sweep (`EnableDestroyedUnitListSweep`)** — fixes the NRE flood directly. The
   vanilla `BatchedNPCRaycasts.Update` iterates `GameManager.units` with **no** null/destroyed guard
   (`units[i].transform.position`), so a destroyed puppet still referenced there dereferences a null
   `Transform`. A Harmony **prefix** (`BatchedNPCRaycasts_Update_Pre` in `ReverseProbePatches`) sweeps
   Unity-null entries out of `GameManager.units` and `aliveNpcs` before the native body runs — strictly
   corrective (a destroyed object should never be in the live lists). Log: `[NpcListSweep] removed N …`.

**RB verified (LogOutput104):** `[RetroBind] bound late-spawned … dist=0.0m` (exact), `retroBound=7`,
`No local match` collapsed. The "standing still + attack animation + client takes damage" symptom was
then shown **not** to be a binding failure (the enemy was `hostBound=True` throughout, death synced).

#### Snapshot-starvation freeze — Phase 5.7-RB2 → RB4 (2026-06-24)

A *bound* enemy a far-from-host client is fighting froze in place (`Stale-release suppressed
(host-bound) stale=3.00s` repeated; the corpse only snapped to the right place on death via the
position-carrying death event). Root cause = the host's **distance-based snapshot interest throttle**
(`ShouldSendByInterestManagement`) dropped position updates for enemies far from the (stationary) host
player, and the remote-player interest feed meant to exempt the client's enemies is **broken** —
`[InterestDiag]`/`[InterestFeed]` showed `distRemoteMin == distHost` exactly (the "remote" position
equals the host's) and `remoteCount=0` ~43 % of the time. Attempts RB2 (`HasHostTarget`/recent-hit
exempt) and RB3 (don't throttle when `remoteCount==0`) only partially helped.

**RB4 is the structural fix (`SendAllEnemySnapshotsToClients`, default ON):** enemy positions are
gameplay-critical, so while a client is connected the host does **not** drop them via the distance
heuristic at all — `ShouldSendByInterestManagement` returns true up front. Delta compression
(`ShouldSendHostEnemyStateSnapshot`, heartbeat 0.75 s) still bounds bandwidth, so stationary enemies
don't spam. The distance throttle + remote-interest feed remain behind flags but default off because
the feed cannot be trusted. `[InterestFeed]` coordinate logging is left in to fix the feed properly
later. LogOutput107 confirmed the remaining freezes after RB4 were a **separate** cause (game pause —
see `MultiplayerPauseAudit.md`).

See memory `phase-5-7-known-roster-bind-spawn-divergence` (updated).

## 9. Deployment note (workflow gotcha)

The Release post-build deploys to `$(BepInExPluginDir)` from `LocalPaths.props` = the **`Default`**
profile, but the game runs the **`开发`** profile. A build alone will **not** reach the running game.
After each Release build, copy `bin/Release/net472/SULFUR Together.dll` into the Gale-managed folder
of the `开发` profile (host real path **and** the client Sandboxie path), overwriting the DLL in the
existing `SULFUR Together` folder (which also holds `LiteNetLib.dll` — do not create a separate
`SULFURTogether` folder or both copies load and conflict). The `[Build] …` marker line in
`Plugin.cs` is bumped per change so the log proves which DLL actually loaded.

### Files
- `src/Config/CoopConfig.cs` — `PlayerRegistry` section (5 keys, all default OFF).
- `src/Networking/Gameplay/RemotePlayerRegistryManager.cs` — host registry + static activation pass.
- `src/Patches/ReverseProbePatches.cs` — `ApplyMultiPlayerActivationPatches` → `NpcUpdateManager.LateUpdate` postfix.
- `src/Networking/NetService.cs` — `PlayerRegistry` field + Tick + Clear wiring.

### How to test in-game
1. Set `EnableMultiPlayerNpcActivation = true` (the direct fix). Optionally `EnableRemotePlayerInPlayersList = true` (native detect/aggro).
2. Host stands still; client walks several rooms ahead to enemies that were previously inert.
3. Expect: enemies `SetActive`/`ActivateBehaviour` near the client (watch `[PlayerRegistry] activation woke N npc(s)`); with the registry on, they detect and path to the client without ForceAggro.

## 10. Trigger handling: ghost ∩ player-trigger volumes (future design)

The headless ghost is a real `Player` component with a collider, so world systems that resolve a
`Player` from a trigger overlap will pick it up. Observed (LogOutput97): the ghost followed the
client into a spider-web hazard → `PlayerHazards.OnTriggerEnter` → `Player.ChangeMovementPreset`
NRE (the headless ghost has no movement machinery). Currently benign (1 non-fatal NRE), but it
defines a real surface that needs **per-trigger-type** handling — *not* a blanket "ghost ignores all
triggers".

Design direction (per the project owner):

- **Per-player triggers (traps: web/slow/damage volumes)** — affect only the player who tripped them.
  A remote player's trap experience belongs on **that client's own machine**, where their real player
  is. The host-side ghost must therefore **NOT** fire these (and must not NRE). → ghost is guarded
  out of per-player hazards. *(Not yet implemented; the web NRE is the placeholder for this.)*

- **Global / arena triggers (e.g. boss-room door-close)** — the **opposite**: when **any** player
  activates the boss-room door trigger, it should fire and apply to **all** players (everyone's door
  closes), solving the door-sync divergence. Planned companion: a prompt "teleporting all players not
  in the room in 5s" that then teleports stragglers to the door trigger's position — solving the
  "door closed before I got in" lockout. *(Future feature.)*

So trigger handling is split by intent: **per-player traps = local to the triggering player**;
**arena/progression triggers = globalised across all players (+ pull stragglers in)**. The ghost
participates or is excluded per that classification, not uniformly.
