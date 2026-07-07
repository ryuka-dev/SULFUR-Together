# Networking Architecture

## Transport

**Library:** LiteNetLib (UDP)

**Delivery modes used:**

| Mode | LiteNetLib Enum | When to use |
|------|----------------|-------------|
| ReliableOrdered | `DeliveryMethod.ReliableOrdered` | Ordered state changes (spawn, death, roster) |
| ReliableUnordered | `DeliveryMethod.ReliableUnordered` | Reliable events where order doesn't matter (attack phase, projectile) |
| Unreliable | `DeliveryMethod.Unreliable` | High-frequency snapshots (position, rotation, animator) |

Every message, codec, and handler above is 100% LiteNetLib-typed (`NetPeer`/`NetDataWriter`/`NetDataReader`) and
transport-agnostic in intent only — see "Connection methods" below for how a second real-world transport (Steam
P2P) rides underneath this exact same layer without touching any of it.

---

## Connection methods (STEAM-1..4)

Two ways to *establish* the LiteNetLib connection above; once established, everything above this line behaves
identically regardless of which one was used.

- **Direct IP** (original): host binds `NetManager.Start(HostPort)`, client `NetManager.Connect(HostAddress,
  HostPort, ConnectionKey)`. Real UDP, LAN or port-forwarded WAN.
- **Steam P2P** (`src/Networking/Steam*.cs`): rather than introduce a `Send(peerId, byte[])` abstraction and
  touch every codec, Steam is added as a **loopback relay underneath** the exact same LiteNetLib socket calls:
  - `SteamNetworkingSupport.cs` — compile-time reference to the game's own already-initialized
    `com.rlabrecque.steamworks.net.dll` (real SULFUR AppID **2124120**; `Private=false` in the csproj — nothing
    bundled). Availability check, local `CSteamID`, and raw send/receive/accept wrappers over
    `SteamNetworkingMessages` (channel 0, **Reliable | NoNagle | AutoRestartBrokenSession** — see "Verified
    fixes" below for why not unreliable). Exposes both `SessionRequested` (inbound P2P open) and
    `SessionFailed` (negotiation/mid-session failure, with Steam's own reason code + debug string) as events.
  - `SteamRelayBridge.cs` — the actual byte pump. **Host**: one small loopback `UdpClient` per connected Steam
    peer, "connected" (UDP sense) to `127.0.0.1:HostPort` — to the real `NetManager` each looks like an ordinary
    new remote endpoint (a distinct loopback source port), so `ConnectionRequestEvent`/handshake/version/
    connection-key validation all run completely unmodified. **Client**: `NetService.SetConnectTarget` points
    `ConnectToHost()` at a local loopback port instead of the configured `HostAddress`/`HostPort`; the bridge
    shuttles bytes between that port and the host's `CSteamID` over Steam. A host can have Direct-IP and Steam
    peers connected simultaneously — they're just distinct remote endpoints either way. `Initialize()` (called
    once from `Plugin.Awake`) subscribes the inbound-session handler permanently — it no-ops (fast, logged)
    unless Steam hosting is currently enabled — and subscribes `SessionFailed` to drop a peer's bridge entry so
    a retry from the same SteamID is re-accepted instead of hitting a stale "already bridged" state.
  - `SteamRichPresenceJoin.cs` — the "Invite Friends" convenience layer. Steam's `"connect"` rich-presence key
    (not a Lobby — one less API surface) makes a "Join Game" entry appear in a friend's Steam friends list;
    `ActivateGameOverlayInviteDialogConnectString` also pops the invite picker. Either path fires
    `GameRichPresenceJoinRequested_t` on the invitee's side with our SteamID64 back — the exact same "address"
    the manual "Steam ID to join" field takes. Only our own `"connect"` key is ever touched (never
    `ClearRichPresence()`, which would also wipe the base game's own status text). The connect page's background
    tick (`CoopConnectPage.ApplySteamState`, runs from `Plugin.Update` regardless of whether the page is
    currently on-screen) auto-joins the instant an invite is seen while free to (not already hosting/connected,
    save loaded) — accepting the invite is the whole action, matching ordinary Steam multiplayer UX.
  - `CoopConnection.ApplySteamClient` / `EnableSteamHosting` / `DisableSteamHosting` — the entry points the
    connect page (`CoopConnectPage.cs`) drives; Steam hosting is a deliberate opt-in (never automatic on
    Create), so a host who never clicks "Invite Friends" exposes no Steam-facing surface at all.
    `EnableSteamHosting` is safe to call repeatedly — the bridge itself only starts once, but the overlay
    invite dialog re-opens on every call so a host can invite additional friends without recreating the room.
  - **Known limitation**: without Valve's Steam Datagram Relay enabled for this AppID (not something a
    third-party mod can turn on), there's no guaranteed relay fallback for restrictive/symmetric NATs — same
    class of risk Direct IP already has with port forwarding, not worse, but not a guarantee either.
  - **Verified fixes (real-machine, two-Steam-account testing — see Versioning.md STEAM-2/3):** an unreliable
    first send never triggers Steam's session negotiation at all (silently dropped pre-session) → switched to
    reliable; `CoopConnection.Apply()` unconditionally tore down a just-opened client bridge before
    `NetService.Start()` could use it → skipped when a pending Steam join target is about to be consumed by
    that very call; the host's per-peer bridge entry was never removed on session end → retries from the same
    peer were silently ignored forever → cleaned up via the new `SessionFailed` event; the inbound-session
    handler was only subscribed while hosting was enabled → an invite accepted before that click got no
    response for ~30s → subscription moved to permanent `Initialize()`; `JoinRequested` had no subscribers at
    all → invite-accept never actually joined → wired to auto-join; `EnableSteamHosting`'s early-return skipped
    re-advertising → "Invite Friends" only worked once per room → fixed to always re-advertise.

---

## Message Type Table

All messages are prefixed with a `byte` message type header. **Source of truth: `src/Networking/NetMessageType.cs`** (each entry there carries an inline phase + description comment). Keep this table in sync with that enum.

| ID | Name | Direction | Phase | Description |
|----|------|-----------|-------|-------------|
| 1 | `HandshakeRequest` | Client→Host | 2 | Connection handshake |
| 2 | `HandshakeAccepted` | Host→Client | 2 | Handshake accepted |
| 3 | `HandshakeRejected` | Host→Client | 2 | Handshake rejected (version/slot) |
| 4 | `Ping` | Any | 2 | Latency probe |
| 5 | `Pong` | Any | 2 | Latency reply |
| 6 | `Disconnect` | Any | 2 | Graceful disconnect |
| 7 | `SessionSnapshot` | Host→Client | 2.x | Reserved (lobby/session UI) |
| 8 | `PeerJoined` | Host→Client | 2.2 | Peer joined notice |
| 9 | `PeerLeft` | Host→Client | 2.2 | Peer left notice |
| 10 | `RunStateUpdate` | Any→Any | 2.3 | Run/scene metadata (chapter, level, seed, GameState, revision) |
| 11 | `HostSceneRequest` | Host→Client | 2.5 | Host's current scene + seed + used-sets + autoLoad flag |
| 12 | `ClientSceneAck` | Client→Host | 2.5 | Client confirms it reached / is in the host scene |
| 13 | `ClientSceneRefused` | Client→Host | 2.5 | Client cannot/will not follow (with phase + reason) |
| 14 | `PlayerTransformVisual` | Any→All | 3.0 | Remote player transform (visual proxy only) |
| 15 | `HostEnemyDeathEvent` | Host→Client | 4.0-B | Enemy death mirror (loot seed) |
| 16 | `HostEnemyStateSnapshot` | Host→Client | 4.1-A | Bulk enemy pos/rot/animator snapshot (Unreliable) |
| 17 | `ClientEnemyDeathClaim` | Client→Host | 4.2 | Client reports a local NPC death for host validation |
| 18 | `PlayerLifeState` | Any→Host / Host→All | 4.3 | Player HP/alive + downed/revive lifecycle |
| 19 | `HostWorldRoster` | Host→Client | 4.4 | Classified entity roster → HostSpawnIndex binding |
| 20 | `HostAttackPhaseEvent` | Host→Client | 5.0 | Semantic enemy attack phase (Windup/Active/Recovery) |
| 21 | `HostProjectileVisualSpawn` | Host→Client | 5.0-P2 | Enemy projectile visual hint (no damage) |
| 22 | `HostEnemyDamageEvent` | Host→Client | 5.1 | Enemy took damage (puppet health tracking) |
| 23 | `HostEnemyHealthState` | Host→Client | 5.1 | Authoritative enemy HP correction |
| 24 | `ClientHitRequest` | Client→Host | 5.3-B | Client dealt damage to a puppet NPC → host applies real damage |
| 25 | `HostLevelManifest` | Host→Client | 5.3-E | Level-gen result summary (seed/rooms/units/specials) for diffing/binding |
| 26 | `HostHitVisualEvent` | Host→Client | 5.3-F | Play the native white hit-flash on a puppet (visual only) |
| 27 | `ClientHostGenerationInputRequest` | Client→Host | 5.3-K | Gated client pulls the host's generation input (seed+used sets) |
| 28 | `ClientBossStartRequest` | Client→Host | 5.4-E | "I triggered this boss; start it authoritatively" |
| 29 | `HostBossEncounterStart` | Host→Client | 5.4-E | Authoritative boss-start broadcast |
| 30 | `ClientBossDialogCommitRequest` | Client→Host | 5.4-E3 | "My player ended the boss dialog; start the fight" |
| 31 | `HostBossDialogCommit` | Host→Client | 5.4-E3 | Authoritative dialog commit (finalize + start once) |
| 32 | `HostBossState` | Host→Client | 5.4-E3 | Authoritative boss phase + health/add summary |
| 33 | `HostBossDynamicSpawn` | Host→Client | 5.4-E4 | Boss-owned sub-entity spawned (encounter+addType+seq key) |
| 34 | `ClientBossHitRequest` | Client→Host | 5.4-F | Client hit a boss target-role → host applies real ReceiveDamage |
| 35 | `HostBossHitVisual` | Host→Client | 5.4-F2 | Boss hit accepted → play local hit visual (no re-damage) |
| 36 | `HostBossDiscreteEvent` | Host→Client | 5.4-F4 | Discrete boss mechanic (Cousin Submerge/MoveToNewPool/Reappear; reuse for CousinDeath) |
| 37 | `ClientLuciaEyeReport` | Client→Host | 5.4-F5 | "One Lucia eye defeated this cycle" (count/cycle only) |
| 38 | `HostLuciaEyeState` | Host→Client | 5.4-F5 | Authoritative remaining eye count + cycle |
| 39 | `HostLuciaDeath` | Host→Client | 5.4-F6 | Lucia terminal death (safe local death; loot/save isolated) |
| 40 | `HostWitchPhase` | Host→Client | 5.4-G2 | Witch phase transition + monotonic revision (phases cycle) |
| 41 | `HostWitchP2Manifest` | Host→Client | 5.4-G5 | Witch Phase-2 dome layout (real dome index) per cycle |
| 42 | `HostWitchP2Result` | Host→Client | 5.4-G5 | Phase-2 dome illusion defeated / real witch hit → client hides |
| 43 | `HostRuntimeSpawn` | Host→Client | 5.5-RT1 | Runtime (post-stabilization) unit spawn mirror (UnitId→UnitSO + SpawnIndex) |
| 44 | `PlayerWeaponFire` | Any→All | 5.6-WS | Player weapon barrage replay (visual only; damage host-authoritative) |
| 45 | `PlayerHeldWeapon` | Any→All | 5.6-WS2 | Remote held-weapon model (WeaponSO + attachments) on proxy hands |
| 46 | `ClientTransitionRequest` | Client→Host | 5.6-DL-Q2 | Client-led level transition relay (host leads + generates; gated client follows) |
| 47 | `BreakableBreak` | Any→All | 5.7-BR | In-scene destructible (`Breakable`) destruction mirror, keyed by deterministic spawn position |
| 48 | `WorldPickupSpawn` | Any→All | WI | A world pickup appeared (player drop now; all loot under Shared-loot). Carries `{ownerPeer,seq}` id + position + full `InventoryData` (gun DIY). Optimistic + peer-authoritative |
| 49 | `WorldPickupTakeRequest` | Client→Host | WI | "I want to take this pickup" (host grants first-come) |
| 50 | `WorldPickupRemoved` | Host→All | WI | Pickup taken/removed by `{netId}`; every peer removes its instance, the named taker adds the item |

> **Subsystem docs:** scene/run negotiation, the client load gate, join flow, the client transition relay and the explicit 联机状态 link state are documented in **[SceneTransitionAndLinkState.md](SceneTransitionAndLinkState.md)**. The boss pipeline (IDs 28–42) is documented in **[BossAuthority.md](BossAuthority.md)** (implementation) and **[BossSourceAudit.md](BossSourceAudit.md)** (reverse-engineered references).

---

## Semantic Layering

### Layer 1 — Reliable Event Channel

Used for: spawn, death, attack phase transitions, projectile visual hints, player life state.

**Properties:** Guaranteed delivery, no ordering requirement between events of different types. Each event carries scene context (ChapterName + LevelIndex + optional LevelSeed) so late-arriving events from stale scenes are discarded.

**Messages:** `HostWorldRoster`, `EnemyDeathEvent`, `ClientDeathClaim`, `PlayerLifeState`, `HostPlayerLifeState`, `HostAttackPhaseEvent`, `HostProjectileVisualSpawn`

### Layer 2 — Unreliable Snapshot

Used for: per-frame position/rotation/animator bulk snapshots. Dropped packets result in a 1-2 frame stutter; interpolation smooths over them.

**Properties:** No delivery guarantee. Receiver interpolates between the last two received snapshots. Sequence numbers allow out-of-order detection (stale snapshots discarded).

**Messages:** `PlayerPositionSync`, `EnemyStateSnapshot`

**Interest management:** Far enemies (>40u from host player) are sent at `EnemyFarSnapshotHz` (default 2 Hz) instead of every probe tick. Enemies with active combat actions skip the rate limit.

### Layer 3 — Ordered State (Roster/Session)

Used for: connection lifecycle, scene roster, join/leave.

**Properties:** Ordered delivery, session-scoped, must arrive in sequence.

**Messages:** `PlayerJoin`, `PlayerLeave`, `HostWorldRoster`

---

## Enemy Sync Protocol (Phase 5.0)

```
HOST                                CLIENT
 |                                    |
 | [ReliableOrdered] HostWorldRoster  |
 |─────────────────────────────────→  |  client builds HostSpawnIndex→puppet map
 |                                    |
 | [Unreliable] EnemyStateSnapshot    |
 |─────────────────────────────────→  |  client interpolates puppet position/rotation/animator
 |      (every tick, interest-culled) |
 |                                    |
 | [ReliableUnordered] HostAttackPhaseEvent
 |─────────────────────────────────→  |  client CrossFades puppet animator (no native replay)
 |      (on combat action detected)   |
 |                                    |
 | [ReliableOrdered] EnemyDeathEvent  |
 |─────────────────────────────────→  |  client plays death anim, removes puppet record
```

**Host is the only combat authority.** Clients never compute damage, never kill enemies, never trigger real projectile physics. Client puppet behavior is purely cosmetic.

---

## HostNetId / HostSpawnIndex

Every synced enemy is identified by its `HostSpawnIndex` — a monotonically increasing integer assigned by `HostWorldRoster` at spawn time. This is the sole stable cross-session identifier.

- `HostSpawnIndex` is stable for the duration of a level session.
- `UnitIdentifier` is included as a secondary hint for disambiguation when multiple enemies have the same kind.
- Clients look up puppet records via `EnemyPuppetRecord` keyed by HostSpawnIndex.
- No InstanceID, name, path, or transform-based matching.

---

## EnemyPuppetRecord Lifecycle

```
HostWorldRoster received
  → create EnemyPuppetRecord (HostSpawnIndex, mode=PassiveSnapshot)
  → disable NavMesh / BehaviorTree on client enemy object

EnemyStateSnapshot received
  → update puppet position/rotation target
  → apply animator parameter hints

HostAttackPhaseEvent received
  → call Animator.CrossFade with AnimatorFullPathHash (if HasAnimatorHint)
  → pulse melee bools based on phase (Windup/Active/Recovery)
  → do NOT replay native Npc methods

EnemyDeathEvent received
  → set puppet mode = Dead
  → trigger death animation
  → remove from active roster
```

---

## Client Damage Suppression (Phase 5.0 P0)

When `EnableHostDrivenEnemyProxy=true` and `SuppressAllClientPuppetDamage=true`:

1. `Npc.HandleMeleeHit` prefix: for puppet NPCs, always call `EnterClientEnemyNativeDamageSuppression()` and allow the call through (for visual animation). __state=true.
2. `Unit.ReceiveDamage` prefix: checks `IsInClientEnemyNativeDamageSuppression()` — if depth > 0, skips real damage application.
3. `Npc.HandleMeleeHit` postfix: calls `ExitClientEnemyNativeDamageSuppression()` when __state=true.

This ensures puppet enemy melee animations play on client (for visual fidelity) without dealing real HP damage to the local player.

---

## Subsystems beyond the enemy proxy

| Subsystem | Messages | Direction | Notes / doc |
|-----------|----------|-----------|-------------|
| Player visual proxy | `PlayerTransformVisual=14` | Any→All | Transform applied to local proxy GameObjects only, never the real Player/Unit. Host relays client→client. |
| Player weapon (visual) | `PlayerWeaponFire=44`, `PlayerHeldWeapon=45` | Any→All | Barrage replayed through the real `ProjectileSystem` with damage stripped; held-weapon model rebuilt on proxy hands (attachments change the model). Damage stays host-authoritative. |
| Player life / downed-revive | `PlayerLifeState=18` | Any→Host / Host→All | Each peer's own death is delayed/committed; co-op downed + revive-hold + downed-input blacklist. See **[PlayerLifeAndDownedInput.md](PlayerLifeAndDownedInput.md)**. |
| Boss authority | `28–42` | both | See **[BossAuthority.md](BossAuthority.md)**. |
| Runtime spawn | `HostRuntimeSpawn=43` | Host→Client | Post-stabilization spawns (boss adds + F3) mirrored via `UnitId→UnitSO` + HostSpawnIndex. |
| Destructibles (visual) | `BreakableBreak=47` | Any→All | Peer-authoritative EFFECT mirror: a peer that breaks a `Breakable` broadcasts the deterministic spawn-position key; receivers `Break()` the matching live local destructible. Loot stays per-peer. See **[Destructibles.md](Destructibles.md)**. |
| World item drops | `48–50` | both | Items in the world (player-thrown now; all loot under a Shared-loot toggle) synced with their full DIY `InventoryData`. Spawn optimistic/peer-authoritative; take host-authoritative (first picker wins). See **[WorldItemDrop.md](WorldItemDrop.md)** (impl) + **[WorldItemDropAudit.md](WorldItemDropAudit.md)** (reverse-engineering). |
| Scene / run / join / link state | `10–13`, `27`, `46` | both | See **[SceneTransitionAndLinkState.md](SceneTransitionAndLinkState.md)**. |

---

## Interpolation

Position snapshots are buffered per-puppet. Client maintains:
- `lastReceivedPosition` / `lastReceivedRotation`
- `targetPosition` / `targetRotation`
- `lastSnapshotTime`

Each Update, puppet moves toward target at a rate derived from the snapshot interval. Near-combat enemies receive higher-frequency snapshots (every probe tick) for smoother interpolation during attack animations.

---

## Versioning

Codec files (`*Codec.cs`) include a `Version` byte at the start of each message. Receivers reject messages with unknown versions. Current versions:

| Message | Codec Version |
|---------|--------------|
| `HostAttackPhaseEvent` | 1 |
| `HostProjectileVisualSpawn` | 1 |

---

## What Is Not Synced

- Enemy NavMesh path targets (host-side only)
- BehaviorTree internal state
- Enemy AI alert/aggro state
- Projectile physics (host runs real physics; P2 sends cosmetic hint only)
- Loot/drop transforms (synced via EnemyDeathEvent loot seed, not live position)
