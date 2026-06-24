# SULFUR Together — Development Plan

**Author:** ryuka  
**GUID:** com.ryuka.sulfur.together

## Goal

Add 2–4 player Host-authoritative co-op to SULFUR as a BepInEx mod.

---

## Phases

> **Status note (kept current):** the project is well past the original skeleton. Player/enemy/combat sync, boss authority, and the scene/transition/join system are all implemented. The detailed sub-phase sections below this table are *historical* (they were written through Phase 2.3); the living references for the implemented systems are the dedicated docs:
> - **[SceneTransitionAndLinkState.md](SceneTransitionAndLinkState.md)** — level loading, host-seed authority, the client load gate, auto-follow, the client-led transition relay, the explicit 联机状态 link state, death-respawn guard, loading fade (Phases 5.3-I → 5.6-LK).
> - **[BossAuthority.md](BossAuthority.md)** + **[BossSourceAudit.md](BossSourceAudit.md)** — host-authoritative boss fights (Phases 5.4-E → 5.4-G7) and runtime spawn (5.5-RT).
> - **[HostDrivenProxyPlan.md](HostDrivenProxyPlan.md)** + **[NetworkingArchitecture.md](NetworkingArchitecture.md)** — enemy proxy architecture, message table, channels.

| Phase | Name | Status | Notes |
|-------|------|--------|-------|
| 0–1.4.1 | **Reverse Probe / Base Plugin** | ✅ Done | BepInEx scaffold, config, logger, probe bootstrap + cleanup/hardening |
| 2–2.3 | **Network Skeleton / Session / Run-state Negotiation** | ✅ Done | LiteNetLib, handshake, peers, run/scene metadata exchange |
| 3 | **Player Sync** | ✅ Done | Transform visual proxy (`PlayerTransformVisual=14`); weapon fire + held weapon (5.6-WS, msgs 44/45, visual only) |
| 4 | **Host-authoritative Enemy Sync** | ✅ Done | World roster, state snapshot, attack-phase, death/damage mirror, puppet pipeline (msgs 15/16/19/20/22/23) |
| 5 | **Combat Sync** | ✅ Done | `ClientHitRequest=24` → host-authoritative `Unit.ReceiveDamage`; hit-visual feedback; client damage suppression |
| 5.0 | **Host-Driven Proxy Architecture** | ✅ Done | See HostDrivenProxyPlan.md / NetworkingArchitecture.md |
| 5.3 | **Level Manifest + Used-Sets + Load Gate** | ✅ Done | Deterministic generation-input sync; client load gate; level manifest (msgs 25/26/27) |
| 5.4-E…G7 | **Boss Authority** | ✅ Done | Encounter start, dialog commit, damage/phase/death, dynamic spawn (msgs 28–42) — see BossAuthority.md |
| 5.4-A…D | **Scene Authority / Join Flow / Hub Return** | ✅ Done | Join policy, hub follow, transition-descriptor target authority |
| 5.5 | **Runtime Spawn + Enemy Damage/Projectile** | ✅ Done | `HostRuntimeSpawn=43`; enemy damage-type passthrough; projectile aim |
| 5.6-WS | **Player Weapon Sync** | ✅ Done | Bullet barrage + held-weapon model (visual; damage host-authoritative) |
| 5.6-DL/CL/LK | **Death-Respawn Guard / Client Level Load / Link State** | ✅ Done | Client-led transitions; explicit 联机状态; see SceneTransitionAndLinkState.md |
| 6 | **Loot / Inventory Sync** | 🟡 Partial | Loot via death loot-seed; full inventory/chest/trade diffs not started |
| 7 | **Scene / Run / Save Sync** | ✅ Done (core) | Covered by the scene/transition/link-state system; save coordination is per-player |
| — | **Ping/Connect UI** | 🔴 Planned | Replace config toggles; "don't run plugin before finding host"; host permission options |

---

## Phase 2.3 — Run / Scene State Negotiation (Done)

This phase only answers: "are peers currently in the same run/scene state?"
It does not load scenes, does not move players, and does not synchronize gameplay.

Implemented metadata:

- `ChapterName` from `GameManager.GoToLevel`
- `LevelIndex` from `GameManager.GoToLevel`
- `LoadingMode` and `SpawnIdentifier` from `GameManager.GoToLevel`
- `GameState` from `GameManager.SetState`
- local run-state `Revision` for change ordering/debugging

New files:

- `NetRunState.cs`
- `NetRunStateCodec.cs`
- `NetRunStateManager.cs`
- `NetRunStateBridge.cs`

New message:

- `RunStateUpdate = 10`

Behavior:

- Host and Client both collect local run-state metadata from existing reverse-probe GameManager hooks.
- After handshake, each side sends its local run state to the other side if known.
- Every `RunStateBroadcastIntervalSeconds` seconds, connected peers resend their latest local run state.
- When local and remote chapter / levelIndex / GameState differ, the mod logs a warning only.
- No automatic correction is performed. Client is not teleported and Host does not force-load the Client.

Still prohibited in this phase:

- automatic scene transition
- remote player spawning
- player transform sync
- enemy sync
- damage sync
- loot sync
- save sync

---

## Phase 2.2 — Session / Peer Management (Done)

- Added session metadata layer only; no gameplay synchronization.
- New files:
  - `NetPeerRole.cs`
  - `NetConnectionState.cs`
  - `NetPeerSession.cs`
  - `NetSessionManager.cs`
- Host creates local host session: `peerId=host`, `slot=0`.
- Host assigns connected clients stable session metadata after handshake:
  - `PeerId` such as `client-1`
  - `PlayerName` from handshake
  - `Slot` in range 1..MaxPlayers-1
  - `JoinedAt` / `LastSeen`
  - endpoint string
- `HandshakeAccepted` now carries assigned peer id / slot and host metadata.
- Client registers local session after accepted handshake and records the host session.
- `NetService` logs session join/leave and includes compact session list in the 30s status line.
- Reserved message IDs for future lobby/session UI messages: `SessionSnapshot`, `PeerJoined`, `PeerLeft`. They are not used for gameplay.

Still prohibited in this phase:
- remote player spawning
- player transform sync
- enemy sync
- damage sync
- loot sync
- scene sync

---

## Phase 2.1 — Network Handshake Test Prep (Done)

- Log message format standardized for test verification:
  - Host: `[Net] Host started on port 9050`
  - Client: `[Net] Client connecting to 127.0.0.1:9050`
  - HandshakeRejected demoted from Warn → Info (consistent with Accepted)
- Ping/Pong log lines now gated by `EnableDebugLog` (both directions); default off eliminates heartbeat noise
- Plugin.Awake() networking section wrapped in `try/catch` — prints clear error if LiteNetLib.dll is missing or fails to load

---

## Phase 1.4.1 — Pickup ItemName Fix (Done)

- `InteractionManager.SpawnPickup` now uses Harmony `__state` to carry the item name from Prefix to Postfix.
- `ReverseProbeKnownObjects.PickupInfo` stores `pickupInstanceID -> itemName` as strings only; no Unity object references are retained.
- `ExecutePickup` and `RemovePickup` resolve item names before removing pickup entries.
- `PickupSummary.topItems` now uses real item names instead of `Pickup(name=Pickup(Clone), id=...)`.
- `ReverseProbeFormatter.FormatItemName()` provides best-effort readable names with fallback for common currency asset names.
- `NetService` emits a 30s network status line while networking is enabled.

---

## Phase 1.4 — Loot/Pickup Log Compression (Done)

- **10 new config keys:**
  - `EnablePickupSpawnProbe` / `EnablePickupExecuteProbe` / `EnableLootRegisterProbe` / `EnableLootSpawnProbe` (all true by default — gate counting + burst)
  - `EnableVerbosePickupProbe` / `EnableVerboseLootProbe` (both false — gate per-item log lines)
  - `CompactPickupLogs` / `CompactLootLogs` (both true — use burst summary instead of per-item)
  - `PickupBurstSummaryIntervalSeconds` (5f) / `LootBurstSummaryIntervalSeconds` (5f)
- **Burst summary windows:** 5-second rolling windows flush to:
  - `[PickupSummary] spawned=N executed=N removed=N topItems=Cash Money Coin x10, ...`
  - `[LootSummary] registered=N spawned=N topItems=Cash Money Coin x10, ...`
  - Top-5 items by count. Skipped when no events in window.
- **Patch changes (default: compact, no per-item lines):**
  - `IM_SpawnPickup_Pre/Post`: count + KnownObjects + burst; verbose-only per-item log
  - `IM_ExecutePickup_Pre/Post`: count + KnownObjects remove + burst; verbose-only per-item log
  - `IM_RemovePickup_Pre`: KnownObjects remove + burst; verbose-only per-item log
  - `LM_RegisterLootDropped_Pre`: count + burst with item name; verbose-only per-item log
  - `LM_SpawnGlobalLoot_Pre` / `LM_SpawnLootFrom_Pre`: count + burst; verbose-only log
- **Startup config summary** extended to include EnableVerbosePickupProbe, EnableVerboseLootProbe, CompactPickupLogs, CompactLootLogs

---

## Phase 1.3 — Probe Lifecycle Fix (Done)

- **Inventory log gating:** 4 new config flags (all default false) gate verbose output per method:
  - `EnableInventorySerializationProbe` → `II_GetSerialized_Post`
  - `EnableInventoryDestroyProbe` → `II_DestroyFromInventory_Pre`
  - `EnableInventoryTransferProbe` → `II_TransferOwnership_Pre` + `II_TryMoveToPlayer_Pre`
  - `EnableInventoryDropProbe` → `II_DropFromPlayer_Pre`
  - Counters always increment; verbose log only when flag enabled. Not gated by `EnableDebugLog`.
- **5 new ProbeSummary inventory counters:** serialized / destroy / transfer / drop / moveToPlayer
  - Included in `allZero` check, output line, and reset block
  - `Known live` line now includes `units=` field
- **KnownObjects lifecycle fixed:**
  - `RegisterDeath` now removes from all live dicts (was incrementing DeathCount — stale entries kept players/npcs alive in counts)
  - `RegisterPickupRemoved` / `RegisterPickupExecuted` now remove from `_pickups` (was setting flags)
  - Added `ClearLevelScopedObjects()` — clears players/npcs/units/pickups; called from `GM_ClearLevel_Pre`
  - Added `ClearPickups()` — clears only pickups; called from `LM_ClearOnNewLevel_Pre`
- **Startup config summary:** `Plugin.Awake()` logs EnableDebugLog, EnableReverseProbe, EnableNetworking, NetworkMode, EnableInventorySerializationProbe, EnableAiUpdateTargetProbe, EnableAiSetDestinationProbe

---

## Phase 1.2 — Probe Hardening (Done)

- Fixed `ReverseProbeState.ShouldLog` throttle bug: `maxPerWindow=0` now correctly suppresses all calls within the window (previously returned true on every call)
- Fixed AI probe flooding: per-agent throttle keys `AI_UpdateTarget_{instanceId}` and `AI_SetDestination_{instanceId}` replace the shared global keys
- New granular AI probe config flags (all default off):
  - `EnableAiUpdateTargetProbe` / `EnableAiSetDestinationProbe` / `EnableAiNavMeshStateProbe` / `EnableAiCanMoveProbe`
  - `EnableNpcUpdateProbe` — Npc.Update gated by this flag (no longer gated by EnableDebugLog)
- `EnableDebugLog` default changed from `true` → `false` (clean dev mode)
- Expanded `ReverseProbeSummary`:
  - New counters: unitDamage/npcDamage/breakableDamage, unitDeaths/npcDeaths/breakableDeaths, lootSpawns, pickupSpawns, pickupExecutes
  - New configs: `EnableProbeSummary` (true), `ProbeSummaryIntervalSeconds` (30f), `SuppressEmptyProbeSummary` (true)
  - Summary is suppressed when all delta counters are zero and SuppressEmptyProbeSummary=true
  - Shows live Known counts: players / npcs / pickups
- New `ReverseProbeKnownObjects` — string/int-only dict tracking players, npcs, units, pickups without Unity strong refs
  - Registered from: AddPlayer, AddNpc, Unit.Spawn, Npc.Spawn, ReceiveDamage, Die, SpawnPickup, RemovePickup, ExecutePickup

---

## Phase 2 — Network Skeleton (Done)

- Added LiteNetLib via NuGet PackageReference (`Version="1.*"`)
- Deploy target updated: both `SULFUR Together.dll` and `LiteNetLib.dll` copy to `$(BepInExPluginDir)\SULFURTogether\`
- New `src/Networking/` with: NetMode, NetMessageType, NetMessage, NetHandshake, NetLogger, NetConfig, NetService
- `NetService` starts only when `EnableNetworking=true` AND `NetworkMode ∈ {Host, Client}`
- Supported message types: HandshakeRequest, HandshakeAccepted, HandshakeRejected, Ping, Pong, Disconnect
- Handshake validates: protocol magic, protocol version, connection key, mod version (if RequireSameModVersion=true)
- Ping/Pong exchange on `SendPingIntervalSeconds` timer
- No gameplay types referenced in network layer

---

## Phase 1.1 — Probe Cleanup (Done)

- Renamed author identity to `ryuka`; GUID: `com.ryuka.sulfur.together`
- Extracted all hardcoded paths to `LocalPaths.props` (gitignored) with `LocalPaths.props.example`
- Added `.gitignore`
- Fixed `SpawnPlayerNode.SpawnPlayer` Postfix: method returns void; removed `__result` parameter
- Log noise reduction: Unit.Spawn filtered; InventoryItem.Setup count-only by default; Npc.HandleMeleeHit and AiAgent.GetTarget disabled by default
- Added `ReverseProbeSummary`: 30-second periodic counter summary

---

## Phase Gate Rule

No phase begins until `ReverseMapping.md` entries for that phase have `Verified In Game = Yes`.

---

## Phase 0 Completion Criteria

- [x] All probe patches load without exceptions
- [x] `[GM] GoToLevel` fires on level transition
- [x] `[Unit] Spawn` / `[Npc] Spawn` fire on level enter
- [ ] `[Unit] ReceiveDamage` fires on combat — at least 1 overload confirmed
- [x] `[LootManager] SpawnLootFrom` fires on enemy death
- [x] `[InteractionManager] ExecutePickup` fires on item pickup
- [ ] Update `ReverseMapping.md` Verified column for all confirmed entries

---

## Phase 2.2.1 — Client Auto-Reconnect (Done)

- Client mode now retries connection every 5 seconds when the host is not available yet.
- This specifically supports Sandboxie/local testing where the client instance may start before the host instance.
- First attempt still logs:
  - `[Net] Client connecting to 127.0.0.1:9050`
- Retry attempts log:
  - `[Net] Client reconnecting to 127.0.0.1:9050 attempt=N`
- On connection failure, the client logs:
  - `[Net] Client will retry connection in 5 seconds`
- Scope remains network/session only. No gameplay sync is added.

---

## Phase 2.3.1 — RunState Mismatch Filter (Done)

- Scene mismatch warnings now only trigger when `chapterName` or `levelIndex` differ.
- Same-scene `Loading` vs `Running` is no longer treated as a warning.
- `Uninitialized`, `<unknown>`, `Loading`, and `Cinematic` are treated as transient/non-stable states for mismatch noise control.
- Stable same-scene `GameState` differences are debug-only when `EnableDebugLog=true`.
- `Revision` differences never trigger warnings by themselves.

## Phase 2.4 — Host Scene Authority Skeleton (Done)

- Added warning-only Host scene authority layer.
- New configs:
  - `EnableHostSceneAuthority` default `true`
  - `WarnOnClientSceneDrift` default `true`
- Host local RunState is treated as the authoritative scene metadata.
- Client compares local scene against Host scene and warns only when chapter/level differ:
  - `[SceneAuthority] Client is not in host scene: ... action=manual-only`
- Host compares connected Client scene states against Host scene and warns only when chapter/level differ:
  - `[SceneAuthority] Client client-1 is not in host scene: ... action=observe-only`
- Status logs now include authority status:
  - Host: `authority=HostScene localReady=True clientDrift=N`
  - Client: `authority=HostScene hostKnown=True inHostScene=True/False`
- No automatic scene loading, no teleporting, no remote player spawning, and no gameplay sync are implemented.

---

## Phase 2.5 — HostSceneRequest / ClientSceneAck Protocol Skeleton (Done)

- Added Phase 2.5 scene-follow negotiation messages:
  - `HostSceneRequest = 11`
  - `ClientSceneAck = 12`
  - `ClientSceneRefused = 13`
- Added new files:
  - `NetSceneRequest.cs`
  - `NetSceneRequestCodec.cs`
  - `NetSceneRequestManager.cs`
- New configs:
  - `EnableHostSceneRequestProtocol` default `true`
  - `AutoSendHostSceneRequestOnDrift` default `true`
  - `HostSceneRequestIntervalSeconds` default `10`
- Host sends `HostSceneRequest` when Host Scene Authority detects that a Client is in a different chapter/level.
- Client never auto-loads scenes. It only replies:
  - `ClientSceneAck` if already in the requested Host scene
  - `ClientSceneRefused` if not in target scene, with message `Automatic scene follow is not implemented yet; manual-only.`
- Status logs include `sceneReq:` summary.
- Scope remains metadata/protocol only. No scene loading, teleport, RemotePlayer, transform sync, enemy sync, damage sync, pickup sync, or save sync is implemented.

## Phase 2.5.1 / 2.6 - Scene request cleanup and manual client follow

Implemented after Phase 2.5 HostSceneRequest protocol.

Scope:
- `ClientSceneAck` and `ClientSceneRefused` now both clear the matching pending HostSceneRequest round on the Host.
- Status text now uses `sceneReq:pending=...` instead of the older ambiguous `outstanding=...` wording.
- Added Client-only manual scene follow skeleton:
  - Default key: `F6`.
  - Runs only when the user presses the key.
  - Uses the latest `HostSceneRequest` target.
  - Attempts to call the game's `GameManager.GoToLevel(...)` by reflection.
  - Fails safely with `ClientSceneRefused` if the target `WorldEnvironment` or `GoToLevel` method cannot be resolved.
- Manual follow sends no gameplay state and does not create remote players.

Still not implemented:
- Automatic scene following.
- Player position sync.
- Remote player visual proxy.
- Enemy / damage / pickup / inventory sync.
- Save or seed synchronization.


## Phase 2.6.1 Manual Scene Follow lookup fixes

- Manual scene follow now handles `WorldEnvironmentIds` enum parameters directly instead of calling Unity `Resources.FindObjectsOfTypeAll` on non-Unity types.
- Added scene alias normalization for network metadata and manual follow lookup.
- `Act_01_HedgemazeFromChurch` is treated as the loadable scene id `Act_01_Hedgemaze` for comparison and manual follow attempts.
- This remains warning/manual only. No remote player, gameplay, enemy, loot, damage, save, or forced scene synchronization was added.

## Phase 2.6.2 / 3.0 - Ack de-duplication and Remote Player Visual Proxy

Implemented after Phase 2.6.1 manual follow lookup fixes.

Phase 2.6.2:
- Client scene responses now carry a `FollowPhase` value:
  - `Refused`
  - `FollowInvoked`
  - `Arrived`
- Client suppresses duplicate response phases for the same `HostSceneRequest`.
- The same request should no longer spam repeated `ClientSceneAck` on every `GoToLevel` / `SetState` callback.
- Host status now reports `lastFollow` phase in addition to pending/response counts.

Phase 3.0:
- Added visual-only remote player proxy skeleton.
- New message type:
  - `PlayerTransformVisual = 14`
- Added local player transform capture from `GameManager.AddPlayer`.
- Local transform is sent at `RemotePlayerTransformSendRateHz` only when local run state has a known scene.
- Remote transform packets create/update local-only primitive proxy GameObjects.
- Proxies are visible only when the remote transform scene matches the local scene.
- Proxies are hidden after `RemotePlayerVisualTimeoutSeconds` without updates.

Strict scope limits:
- No real remote `Player` objects are spawned.
- No `GameManager.AddPlayer` calls are made for remote peers.
- No control, damage, inventory, pickup, enemy, level generation, save, or authority sync is implemented.
- The proxy is only a visual marker for early networking validation.

## Phase 3.1 / 3.2 / 3.3 - Level Seed Authority / Seed-aware Visual Proxy

Status: implemented for testing.

Purpose:
- The first remote visual proxy test proved that transform packets work, but SULFUR levels are procedurally generated.
- `chapterName + levelIndex` is not enough to guarantee that both peers are in the same actual map layout.
- The next synchronization boundary is therefore level seed metadata, not combat or loot.

Implemented:
- Captures local generated level seed through the stable observed `GameManager` instance and targeted `GameManager.currentSeed` reads from LevelGeneration callbacks / low-frequency network polling. No Unity Debug.Log text parsing is used.
- Adds `HasLevelSeed`, `LevelSeed`, and `LevelGenerator` to `NetRunState`.
- Adds level seed fields to `HostSceneRequest` and `ClientSceneAck/Refused`.
- Adds level seed fields to visual-only `PlayerTransformVisual` packets.
- Host/Client scene equality can now require proven matching `chapterName + levelIndex + levelSeed`; when seed authority is required, unknown seed is not treated as a full scene match.
- Manual Client scene follow attempts to set the game's `ForceLevelSeed` to the HostSceneRequest seed before invoking `GameManager.GoToLevel`.
- Remote visual proxies are hidden unless the remote transform and local run state have a proven matching seed when seed hiding is enabled.

Still not implemented:
- Direct deterministic level-generation patching.
- Room layout serialization.
- Enemy, loot, pickup, save, damage, inventory, or real player synchronization.

Important safety behavior:
- If level seed capture fails while seed authority is required, the mod does not treat the generated level instance as proven-matching; visual proxies stay hidden and scene requests remain manual/metadata-only.
- If `ForceLevelSeed` cannot be found or applied, manual follow still attempts GoToLevel and logs the failure detail.
- Remote visual proxies are still local-only markers, never real gameplay Player/Unit instances.


## Phase 3.4 - Formal LevelSeed Probe

- Replaced temporary Unity Debug.Log seed parsing with targeted GameManager hooks.
- Capture now uses GameManager.set_currentSeed and GameManager.StartLevelRoutineGraph.
- Manual follow now applies the host seed through GlobalSettings.ForceLevelSeed directly.
- No gameplay authority, enemy sync, pickup sync, or save sync is introduced in this phase.


## Phase 3.4.1 - Stable LevelSeed Probe Fix

- Removed fragile separate Harmony patches for `GameManager.set_currentSeed` and `GameManager.StartLevelRoutineGraph`.
- Level seed capture now uses existing stable GameManager hooks to remember the active GameManager instance.
- Seed is read from `GameManager.currentSeed` by targeted reflection during LevelGeneration node callbacks and a low-frequency network tick poll.
- This avoids parsing Unity log text and avoids patching tiny auto-property setters / compiler-generated coroutine methods.
- `GlobalSettings.ForceLevelSeed` remains the direct manual-follow seed application target.
- `UpdateLocalGoToLevel` now clears stale local seed metadata until the new level seed is captured.


## Phase 3.4.2 — Manual Follow Hotkey Safety

- Root cause fixed: the previous default manual follow key `F6` conflicts with SULFUR's DevTools/F-key bindings. In testing, pressing F6 toggled `Player invulnerable state: DevTools`, causing invulnerability and non-consuming ammo symptoms after manual scene follow.
- The default `ManualClientSceneFollowKey` is now `PageDown`. Existing configs using `F6` are migrated to `PageDown` on load.
- Runtime guard rejects all F-key manual follow shortcuts and logs a warning instead of executing follow input, preventing accidental DevTools/GodMode toggles.
- This does not change seed authority, scene request, remote visual proxy, damage, inventory, enemy, or loot behavior.


## Phase 3.5 - Remote Visual Proxy Polish / Status Hygiene

Status: source-level implementation added after Phase 3.4.2.1.

Implemented:
- Remote visual proxy first update / re-show now snaps directly to the latest received transform.
- Large transform corrections snap instead of smoothing across the map.
- Added config entries `RemotePlayerVisualInterpolationSpeed` and `RemotePlayerVisualSnapDistance`.
- Scene transitions clear local visual tracking and existing proxies so stale markers do not survive into the next level.
- Capturing a new local level seed hides existing proxies until fresh remote transform packets prove a matching scene + seed.
- Seed-required scene equality no longer treats unknown seed as a proven match.
- Visual proxies with `HideRemoteVisualWhenLevelSeedMismatch=true` require both sides to have known equal `levelSeed`.

Still not implemented:
- Real remote `Player` spawn or `GameManager.AddPlayer` registration.
- Gameplay authority, enemy sync, damage sync, loot/pickup sync, inventory sync, event sync, or save sync.

## Phase 3.5.1 - Scene Authority Log Hygiene

Status: source-level implementation added after Phase 3.5 test passed.

Purpose:
- Test logs after Phase 3.5 showed no SULFUR Together exceptions or patch failures.
- Remaining mod-side noise was mostly transient SceneAuthority warnings while one side was still `Loading` or had `seed=?` during normal scene transitions.

Implemented:
- SceneAuthority / RunState mismatch warnings now wait until both sides are comparable and stable.
- When seed authority is required, unknown seed remains not-proven, but no warning is emitted until both seeds are known and a real mismatch remains.
- HostSceneRequest creation now avoids sending a request for the same chapter/level while the client has not captured its seed yet; it waits for the next RunState update instead.
- If the client is in a different chapter/level, HostSceneRequest can still be sent immediately once the host has a known target seed.

Still not implemented:
- No gameplay synchronization was added.
- No real remote player, enemy, damage, loot, pickup, inventory, event, or save synchronization was added.

## Phase 4.0.0-A - Gameplay Entity Probe / NetworkEntityId Investigation Skeleton

Status: source-level implementation added after Phase 3.5.1 test passed.

Purpose:
- Begin the gameplay synchronization investigation without synchronizing gameplay.
- Collect structured Host/Client logs for entity spawn, damage counter, and death events under the same `chapterName + levelIndex + levelSeed`.
- Determine whether NPC/unit generation order and identity candidates are stable enough for a future `NetworkEntityId` design.

Implemented:
- Added local-only `NetGameplayEntityId`, `NetGameplayEntitySnapshot`, and `NetGameplayProbeManager`.
- Existing targeted ReverseProbe hooks now forward Unit/Npc/UnitManager/LevelGeneration events into the structured probe manager.
- The probe stores strings and value types only; it does not retain strong Unity/gameplay object references.
- Entity logs include spawn index, local instance id, Unity instance id, reflected `UnitIdentifier`/`AsGlobalId` candidate when available, actor name, position, scene, seed, game state, and source hook.
- Per-entity spawn/death logging is delayed until local scene and seed metadata are known when `RequireStableSceneAndSeedForGameplayProbe=true`.
- Damage events are counted but individual damage logs default to off to avoid spam.
- Level transitions and `GameManager.ClearLevel` clear the local probe entity table and reset spawn index.

New config section:

```ini
[NetworkGameplayProbe]
EnableGameplayEntityProbe = true
GameplayEntityProbeSummaryIntervalSeconds = 10
LogGameplayEntitySpawn = true
LogGameplayEntityDamage = false
LogGameplayEntityDeath = true
RequireStableSceneAndSeedForGameplayProbe = true
```

Still not implemented:
- No enemy position synchronization.
- No health, damage, death, loot, pickup, inventory, event, save, or real remote-player synchronization.
- No `NetworkEntityId` is considered authoritative yet; all ids are investigation candidates only.

## Phase 4.0.0-B - Host Enemy Death Event Mirror / Match-Only Experiment

Status: source-level implementation added after Phase 4.0.0-A test logs proved same-seed NPC spawn order is mostly stable.

Purpose:
- Start validating a Host-authoritative event flow without changing Client gameplay.
- Send Host NPC death events to Clients as reliable metadata packets.
- Let Clients match the received Host death event against their local structured probe table and log the result.
- Keep all gameplay mutation disabled in this phase.

Implemented:
- Added `HostEnemyDeathEvent` network message.
- Added `NetGameplayDeathEvent`, `NetGameplayDeathEventCodec`, and `NetGameplaySyncBridge`.
- `NetGameplayProbeManager` now emits one Host-side enemy death event after local death de-duplication.
- Only `Npc` category deaths are mirrored in this phase.
- Client receive path logs the Host event and attempts local matching by `spawnIndex + position` first, then unique `UnitGlobalId`, then unique candidate key.
- Received event IDs are de-duplicated per level.
- Scene and level seed must match before a Client reports a safe local match.
- `ApplyReceivedEnemyDeathEvents` is intentionally reserved. Even if enabled, Phase 4.0.0-B logs a warning and does not kill or modify Client enemies.

New config section:

```ini
[NetworkGameplaySyncExperimental]
EnableHostEnemyDeathEventMirror = false
LogReceivedEnemyDeathEvents = true
ApplyReceivedEnemyDeathEvents = false
EnemyDeathMirrorPositionTolerance = 2.5
```

Still not implemented:
- No Client enemy is killed by a received Host event.
- No enemy health, damage, AI, movement, loot, pickup, inventory, event, save, or real remote-player synchronization is implemented.
- No reflected or probe-derived id is considered authoritative yet.

## Phase 4.1.0-A — Host Enemy State Snapshot Mirror / Match-Only

Status: implemented as an experimental, default-off probe.

Goal: evaluate whether Host-authoritative enemy AI/state can be introduced without immediately mutating Client gameplay. Host can periodically send NPC state snapshots to Clients. Clients only match local same-seed NPCs and log positional drift; they do not move enemies, override AI, or change attack behavior in this phase.

Key constraints:

- Do not register remote players as real Player/Unit/Npc objects.
- Do not manually pull Client enemies yet.
- Do not patch AI target selection until a safe minimal entry point is confirmed.
- Keep the system scene/seed gated.
- Keep ApplyReceivedEnemyStateSnapshots reserved and non-mutating for this phase.

New config section:

```ini
[NetworkEnemyStateExperimental]
EnableHostEnemyStateSnapshotMirror = false
EnemyStateSnapshotSendRateHz = 2
EnemyStateSnapshotMaxEnemiesPerPacket = 64
OnlySendAliveEnemyStateSnapshots = true
LogReceivedEnemyStateSnapshots = true
ApplyReceivedEnemyStateSnapshots = false
EnemyStateSnapshotPositionTolerance = 5
```

Expected Client log:

```text
[EnemyStateMirror] Batch seq=... count=... matched=... unmatched=... deadMismatch=... drift>5.0m=... avgDelta=... maxDelta=... apply=False
```

## Phase 4.1.0-B — Apply Host Enemy State Snapshot / Transform Mirror

Status: implemented as an experimental, default-off apply path after Phase 4.1.0-A logs proved Host/Client enemy positions can drift far enough to block safe death-event application.

Goal: reduce enemy position drift before attempting deeper AI target or attack synchronization. This phase still does not adapt or replace enemy AI. Clients only mirror matched enemy transforms toward Host snapshots when explicitly enabled.

Implemented:
- `ApplyReceivedEnemyStateSnapshots=true` now queues matched Host NPC snapshots as local transform targets.
- Targets are applied from `NetGameplayProbeManager.Tick()` instead of directly inside the packet handler, keeping network receive and transform mutation separated.
- Client smoothly interpolates toward Host position using `EnemyStateSnapshotInterpolationSpeed`.
- Client snaps when the drift exceeds `EnemyStateSnapshotSnapDistance`.
- Optional Host Y rotation mirroring is controlled by `EnemyStateSnapshotApplyRotationY`.
- Scene and level seed still must match before a snapshot can be queued.
- Dead host/local entities are not moved.

New/updated config:

```ini
[NetworkEnemyStateExperimental]
ApplyReceivedEnemyStateSnapshots = false
EnemyStateSnapshotInterpolationSpeed = 18
EnemyStateSnapshotSnapDistance = 10
EnemyStateSnapshotApplyRotationY = true
```

Still not implemented:
- No Client AI suppression.
- No enemy target selection synchronization.
- No enemy attack event synchronization.
- No enemy damage-to-player synchronization.
- No remote player registration into gameplay systems.
