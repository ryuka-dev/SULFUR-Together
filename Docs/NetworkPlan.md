# SULFUR Together — Network Plan

> **Historical planning doc (Phases 2 – 2.5).** It captures the early host-authoritative plan and the skeleton message protocol; it is *not* kept in sync with later work. For the current state see:
> - **[NetworkingArchitecture.md](NetworkingArchitecture.md)** — full message table (IDs 1–46), channels, enemy proxy.
> - **[SceneTransitionAndLinkState.md](SceneTransitionAndLinkState.md)** — scene/transition/join/link-state.
> - **[BossAuthority.md](BossAuthority.md)** — boss pipeline.
> - **[DevelopmentPlan.md](DevelopmentPlan.md)** — phase status table.

## Architecture: Host-Authoritative

```
Host
 ├─ Runs full AI tick
 ├─ Validates all damage
 ├─ Controls loot and item drops
 ├─ Owns scene transitions
 └─ Owns save state

Client
 ├─ Sends: input requests (move, attack, interact, use item)
 ├─ Receives: world snapshot diffs from Host
 └─ Displays: interpolated state only — never writes authoritative state
```

---

## Transport: LiteNetLib Direct UDP

- Library: LiteNetLib (MIT, pure C#, Unity-compatible), `Version="1.*"` via NuGet
- Both `SULFUR Together.dll` and `LiteNetLib.dll` are deployed to `BepInEx\plugins\SULFURTogether\`
- Host listens on `CoopConfig.HostPort` (default 9050)
- Client connects to `CoopConfig.HostAddress:HostPort`
- `EnableNetworking = false` → no socket ever opens (hard off)
- `NetworkMode` enum: `Off` / `Host` / `Client`

---

## Phase 2 — Network Skeleton (Implemented)

> Phase 2 scope: connection plumbing only. No gameplay data sent.

### Implemented

| Feature | Status |
|---------|--------|
| Host UDP listener | ✅ |
| Client connect by IP:port | ✅ |
| Connection key check (LiteNetLib layer) | ✅ |
| HandshakeRequest / HandshakeAccepted / HandshakeRejected | ✅ |
| Assigned peer id + player slot in HandshakeAccepted | ✅ |
| Protocol magic + version validation | ✅ |
| Mod version match enforcement (configurable) | ✅ |
| Ping / Pong heartbeat | ✅ |
| Disconnect handling | ✅ |
| Host session list and client slot assignment | ✅ |
| Client local session registration after accepted handshake | ✅ |
| 30s compact session status log | ✅ |
| Run/scene state metadata exchange | ✅ |
| Scene mismatch warning only, no correction | ✅ |
| `EnableNetworking=false` hard off | ✅ |

### Not In Phase 2 (Blocked Until ReverseMapping Verified)

- Player position / rotation sync
- Enemy state snapshots
- Damage validation
- Loot / item sync
- Scene transitions

---

## Message Protocol

| ID | Type | Direction | Payload |
|----|------|-----------|---------|
| 1 | HandshakeRequest | Client → Host | magic, protocolVersion, modVersion, playerName, connectionKey |
| 2 | HandshakeAccepted | Host → Client | assignedPeerId, assignedSlot, hostPeerId, hostPlayerName, hostModVersion |
| 3 | HandshakeRejected | Host → Client | reason string |
| 4 | Ping | Both | (empty) |
| 5 | Pong | Both | (empty) |
| 6 | Disconnect | Both | reason string |
| 10 | RunStateUpdate | Both | peerId, playerName, chapterName, levelIndex, loadingMode, spawnIdentifier, gameState, revision |

Protocol constants: `ProtocolMagic = "SULFUR_TOGETHER"`, `ProtocolVersion = 1`

---

---

## Phase 2.3 — Run / Scene State Metadata (Implemented)

This phase only exchanges metadata about where each peer currently is. It is a diagnostic and negotiation layer, not scene synchronization.

### Local state source

Existing reverse-probe hooks report to `NetRunStateBridge`:

| Hook | Data captured |
|------|---------------|
| `GameManager.GoToLevel` | chapter, levelIndex, loadingMode, spawnIdentifier |
| `GameManager.SetState` | GameState string |
| `GameManager.ClearLevel` | revision bump only |

### Network behavior

- Both Host and Client send `RunStateUpdate` after handshake if local state is known.
- Both sides periodically resend the latest run state every `RunStateBroadcastIntervalSeconds` seconds.
- Host records Client state under the assigned `PeerId`, e.g. `client-1`.
- Client records Host state under `host`.
- Status logs include a compact run-state summary.

Example:

```
[RunState] Local GoToLevel: Player2(id=host,scene=ChurchHub:0,state=Running,rev=3) mode=Menu spawn=
[Net] Status: mode=Host peers=1 sessions=2 [...] run:local=Player2(id=host,scene=ChurchHub:0,state=Running,rev=3) remotes=1 [...]
```

### Mismatch handling

If local and remote chapter / levelIndex / GameState differ, the mod logs one warning per mismatch key:

```
[RunState] Mismatch with client-1: local=Player2(id=host,scene=ChurchHub:0,state=Running,rev=3) remote=Player1(id=client-1,scene=Onboarding:0,state=Running,rev=2)
```

The warning is intentionally passive. No peer is moved, no scene is loaded, and no gameplay state is corrected.

---

## Phase 2.2 — Session Metadata (Implemented)

This phase only answers: "who is connected to this session?"
It does not spawn remote players and does not synchronize the world.

### Session fields

| Field | Meaning |
|-------|---------|
| PeerId | Stable id assigned by Host, e.g. `host`, `client-1` |
| PlayerName | Name from config / handshake |
| Slot | Host is slot 0, clients get the lowest free slot from 1 upward. Display/ordering identity only — there is no player cap. |
| Role | Host or Client |
| State | Connecting / Handshaking / Connected / Disconnected / Rejected |
| JoinedAt | Local realtime when session was accepted |
| LastSeen | Last network packet / pong touch time |
| EndPoint | Remote endpoint string, not a gameplay object |

### Expected log examples

Host after client handshake:

```
[Session] Peer joined: id=client-1 slot=1 name='Player2' endpoint=127.0.0.1:xxxxx
[Net] Status: mode=Host peers=1 sessions=2 [Host(...); Player2(...)]
```

Client after accepted handshake:

```
[Session] Local session assigned: id=client-1 slot=1 name='Player2'
[Session] Host session known: id=host name='Player'
[Net] Status: mode=Client connected=True sessions=2 [...]
```

No gameplay object types may be referenced from this layer.

## Future: Steam P2P

- Requires confirming Steamworks is present in SULFUR (check via reverse mapping before planning)
- Would replace or supplement LiteNetLib for NAT traversal

---

## Phase 3+ Planned Message Types

| Direction | Message | Description |
|-----------|---------|-------------|
| Host → Clients | WorldSnapshot | Position, HP, enemy states (delta compressed) |
| Host → Clients | EventBroadcast | Death, loot drop, door open |
| Client → Host | InputRequest | Move vector, action |
| Client → Host | InteractRequest | Object interaction at position |
| Host → Clients | SceneTransition | Level load trigger |
| Host → Clients | SessionEnd | Run over |

---

## Phase 2.3.1 / 2.4 — Host Scene Authority Skeleton

This layer is deliberately warning-only. It establishes terminology and diagnostics for later scene synchronization work, but it does not synchronize anything yet.

### Authority rule

- Host is the authoritative source for scene metadata.
- Client may observe Host scene metadata.
- Client may warn when it is not in the Host scene.
- Host may warn when a Client reports a different scene.
- No side calls `GoToLevel` in response to network data.

### Mismatch filtering

Warnings are now based on scene identity only:

| Local / Remote difference | Log level |
|---------------------------|-----------|
| Different chapterName | Warning |
| Different levelIndex | Warning |
| Same scene, Loading vs Running | Suppressed |
| Same scene, `<unknown>` / Uninitialized / Loading / Cinematic | Suppressed |
| Same scene, stable GameState difference | Debug only when EnableDebugLog=true |
| Revision difference only | Suppressed |

### Expected authority status

Host:

```
[Net] Status: mode=Host peers=1 sessions=2 [...] run:local=... remotes=1 [...] authority=HostScene localReady=True clientDrift=0
```

Client:

```
[Net] Status: mode=Client connected=True sessions=2 [...] run:local=... remotes=1 [...] authority=HostScene hostKnown=True inHostScene=True
```

When Client is in a different scene:

```
[SceneAuthority] Client is not in host scene: local=... host=... action=manual-only
```

When Host sees a Client in a different scene:

```
[SceneAuthority] Client client-1 is not in host scene: host=... client=... action=observe-only
```

---

## Phase 2.5 — HostSceneRequest / ClientSceneAck Protocol Skeleton

This phase adds scene-follow request messages, but it still does not perform scene synchronization.
The Host can tell Clients what scene is authoritative, and Clients can report whether they are already there.

### New message types

| ID | Message | Direction | Payload |
|----|---------|-----------|---------|
| 11 | HostSceneRequest | Host → Client | requestId, hostPeerId, hostPlayerName, chapterName, levelIndex, loadingMode, spawnIdentifier, hostGameState, hostRevision, reason, autoLoadAllowed |
| 12 | ClientSceneAck | Client → Host | requestId, clientPeerId, clientPlayerName, chapterName, levelIndex, gameState, localRevision, isInTargetScene, message |
| 13 | ClientSceneRefused | Client → Host | same payload as ClientSceneAck |

### Rules

- `HostSceneRequest.AutoLoadAllowed` is currently always `false`.
- Client must not call `GameManager.GoToLevel` from a received request.
- Client replies `ClientSceneAck` only when its local `chapterName` and `levelIndex` already match the request target.
- Client replies `ClientSceneRefused` when not in target scene, because automatic scene follow is not implemented yet.
- Host records responses for diagnostics only.

### Expected logs

Host sends request:

```text
[SceneRequest] HostSceneRequest sent to client-1: request=... target=Act_01_Caves:1 ... action=request-only
```

Client receives and refuses because it is not already in target:

```text
[SceneRequest] HostSceneRequest received: request=... target=Act_01_Caves:1 ... action=no-auto-load
[SceneRequest] ClientSceneRefused sent: request=... inTarget=False msg='Automatic scene follow is not implemented yet; manual-only.'
```

Host receives response:

```text
[SceneRequest] ClientSceneRefused from client-1: request=... scene=Act_01_Caves:0 ... inTarget=False
```

If Client is already in target:

```text
[SceneRequest] ClientSceneAck sent: request=... inTarget=True
```

## Phase 2.5.1 / 2.6 - Manual scene follow protocol notes

HostSceneRequest lifecycle:
1. Host detects a Client scene drift.
2. Host sends `HostSceneRequest`.
3. Client immediately replies:
   - `ClientSceneAck` if already in target scene.
   - `ClientSceneRefused` if not in target scene and no manual action has happened yet.
4. Host treats both Ack and Refused as the end of that pending request round.
5. If the Client user presses `ManualClientSceneFollowKey`, the Client attempts local `GoToLevel` by reflection.
6. If the local run state later matches the Host request target, Client sends `ClientSceneAck`.

Manual follow is intentionally user-triggered only. Host never forces a Client scene load.


## Phase 2.6.1 Manual Scene Follow lookup fixes

- Manual scene follow now handles `WorldEnvironmentIds` enum parameters directly instead of calling Unity `Resources.FindObjectsOfTypeAll` on non-Unity types.
- Added scene alias normalization for network metadata and manual follow lookup.
- `Act_01_HedgemazeFromChurch` is treated as the loadable scene id `Act_01_Hedgemaze` for comparison and manual follow attempts.
- This remains warning/manual only. No remote player, gameplay, enemy, loot, damage, save, or forced scene synchronization was added.

## Phase 2.6.2 / 3.0 - Ack de-duplication and visual-only remote proxy

### Scene response phases

Client scene responses now include `FollowPhase`:

| Phase | Meaning |
|-------|---------|
| Refused | Client is not in target and no manual follow has completed. |
| FollowInvoked | User pressed the manual follow key and the local GoToLevel call was invoked. |
| Arrived | Client local run state now matches the HostSceneRequest target. |

A Client sends each phase at most once per request id. This prevents repeated `ClientSceneAck` spam while GameManager reports multiple state transitions during level loading.

### Visual-only player transform message

| ID | Message | Direction | Payload |
|----|---------|-----------|---------|
| 14 | PlayerTransformVisual | Host ↔ Client | peerId, playerName, chapterName, levelIndex, hasLevelSeed, levelSeed, sequence, sentAt, position(x/y/z), rotationY |

Rules:
- Client sends its local visual transform to Host.
- Host sends its local visual transform to all Clients.
- Host relays Client visual transforms to other Clients.
- Receiver only applies the packet to a local visual proxy if the packet scene matches the receiver's local scene.
- The visual proxy is a primitive local GameObject only. It is not a SULFUR `Player`, `Unit`, `Npc`, inventory owner, or gameplay authority.

Status adds:

```text
localPlayer=<captured-or-none> remoteVisuals=<visible>/<total>
```

Expected proxy logs:

```text
[RemotePlayer] Local player transform captured: Unit_Player(Clone) 0#123456
[RemotePlayer] Visual proxy created for host
[RemotePlayer] Visual proxy created for client-1
```

## Phase 3.1 - Level Seed Metadata

New metadata:
- `NetRunState.HasLevelSeed`
- `NetRunState.LevelSeed`
- `NetRunState.LevelGenerator`
- `NetHostSceneRequest.HasLevelSeed`
- `NetHostSceneRequest.LevelSeed`
- `NetHostSceneRequest.LevelGenerator`
- `NetClientSceneResponse.HasLevelSeed`
- `NetClientSceneResponse.LevelSeed`
- `NetClientSceneResponse.LevelGenerator`
- `NetPlayerTransformState.HasLevelSeed`
- `NetPlayerTransformState.LevelSeed`

Scene equality rule:
- Without seed authority: same `chapterName + levelIndex` is treated as the same scene metadata.
- With `RequireSameLevelSeedForSceneMatch=true`: same `chapterName + levelIndex` is only a proven level-instance match when both sides know the seed and the `levelSeed` values match.
- Unknown seed is treated as not-yet-proven instead of assumed compatible. Host waits for its own seed before sending seed-authority scene requests; clients can Ack only after their local matching seed is known.

Manual follow seed behavior:
- Client receives `HostSceneRequest` with Host seed.
- When the user presses the manual follow key, the client tries to set `ForceLevelSeed` before calling GoToLevel.
- If the game accepts the seed, the next generated map should match the Host seed.
- If the game ignores/overwrites `ForceLevelSeed`, logs will show a seed mismatch and visual proxies will be hidden when configured.


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


## Phase 3.5 - Visual Proxy Polish / Seed Strictness

- Remote visual proxies now snap on first packet, after being hidden, or after a large correction, so they no longer lerp in from world origin or stale scene positions.
- `RemotePlayerVisualInterpolationSpeed` controls visual smoothing; `RemotePlayerVisualSnapDistance` controls large-distance snap correction.
- When `HideRemoteVisualWhenLevelSeedMismatch=true`, unknown seed is hidden the same as mismatched seed. The visual marker appears only after both sides prove the same `chapterName + levelIndex + levelSeed`.
- Local scene transitions clear captured visual transforms and existing proxies, keeping old-scene markers out of the next scene.
- Still no real RemotePlayer, Player/Unit registration, damage, enemy, loot, pickup, inventory, or save synchronization.

## Phase 3.5.1 - Scene Authority Log Hygiene

SceneAuthority diagnostics are now less noisy during ordinary loading windows:

- `seed=?` still means the level instance is not proven compatible.
- A warning is not emitted merely because one side is loading or has not captured seed yet.
- A warning is emitted only after both sides are stable/comparable and still disagree.
- HostSceneRequest waits when both peers report the same chapter/level but the client seed is still unknown.
- HostSceneRequest is still allowed when the client is clearly in a different chapter/level and the host target seed is known.

This keeps the strict seed safety rule without flooding logs during normal seed capture timing.

## Phase 4.0.0-A - Gameplay Entity Probe Transport Policy

This phase intentionally adds no new network message type.

The new gameplay probe is local-only and log-only:
- No entity probe packet is sent over LiteNetLib.
- No Host authority is applied to combat, AI, loot, pickup, or inventory.
- No Client object is created, destroyed, moved, damaged, or healed from probe data.
- No remote visual proxy is converted into a gameplay Player/Unit.

The probe exists to compare Host/Client logs after both peers are in a proven matching level instance:

```text
chapterName + levelIndex + levelSeed
```

The fields logged by `NetGameplayEntityId` are only candidates for a later `NetworkEntityId`. They must be validated by test logs before any gameplay synchronization protocol is designed.

## Phase 4.0.0-B - Host Enemy Death Event Mirror

New experimental message:

| ID | Message | Direction | Payload |
|----|---------|-----------|---------|
| 15 | HostEnemyDeathEvent | Host -> Client | eventId, sourcePeerId, chapterName, levelIndex, hasLevelSeed, levelSeed, sourceRevision, sequence, spawnIndex, candidateKey, localInstanceId, unityInstanceId, typeName, unitIdentifier, unitGlobalId, category, actorName, hasPosition, position(x/y/z), damageCount, source, sentAt |

Rules:
- Disabled by default with `NetworkGameplaySyncExperimental.EnableHostEnemyDeathEventMirror=false`.
- Host sends only after the local `NetGameplayProbeManager` has de-duplicated death hooks.
- Only `Npc` category deaths are mirrored.
- Delivery uses `ReliableOrdered` because death event order matters for later authority experiments.
- Client only logs and attempts a local match in Phase 4.0.0-B.
- Client match requires same chapter, level index, and known equal seed when seed authority is enabled.
- Client primary match is `spawnIndex + position tolerance`; fallback matching uses unique `UnitGlobalId` or unique candidate key.
- `ApplyReceivedEnemyDeathEvents` is reserved and intentionally does not mutate gameplay in this phase.

Expected logs when enabled:

```text
[EnemyDeathMirror] Received host death: event=host:... idx=21 ... scene=DebugChapter:0 seed=...
[EnemyDeathMirror] Matched local entity: idx=21 candidate=... match=spawnIndex+position distance=0.12m apply=False
```

## Phase 4.1.0-A — HostEnemyStateSnapshot

Added message:

```text
HostEnemyStateSnapshot = 16
```

Host sends a batch of NPC state snapshots at a configurable low rate. The packet is `ReliableSequenced`: snapshots are periodic state telemetry, and newer batches can supersede older ones while still avoiding malformed oversized unreliable packets during early tests. Client handling is match-only: it compares Host snapshots against local same-seed entities and logs drift. It intentionally does not move local enemies or suppress Client-side AI.

Snapshot identity still uses the current probe candidate set:

```text
scene + levelIndex + seed + spawnIndex + candidateKey/unitId + position
```

This is preparation for a later Host-authoritative enemy AI/state design. It is not enemy AI synchronization yet.

## Phase 4.1.0-B — HostEnemyStateSnapshot apply path

`HostEnemyStateSnapshot` remains the same network message introduced in Phase 4.1.0-A. The protocol did not add a new packet type.

Client behavior changed only when this experimental flag is enabled:

```ini
[NetworkEnemyStateExperimental]
ApplyReceivedEnemyStateSnapshots = true
```

When enabled:
- The Client validates scene + level index + level seed.
- The Client matches a local NPC by the existing probe identity path, preferring `spawnIndex`.
- The Client stores the Host position/rotation as a transform target.
- `NetGameplayProbeManager.Tick()` applies the target every frame with interpolation or snap correction.

This is transform mirroring, not AI synchronization. It is a bridge toward Host-authoritative enemy state. Attack windows, damage, target selection, pathing decisions, and local AI execution are still not network-authoritative.
