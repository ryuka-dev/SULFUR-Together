using System.Collections.Generic;
using SULFURTogether.Networking;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Bridge from local gameplay probes into NetService for Phase 4 experimental
    /// host-authoritative event mirroring and player lifecycle packets.
    /// </summary>
    internal static class NetGameplaySyncBridge
    {
        private static NetService? _service;

        public static void Attach(NetService? service)
        {
            _service = service;
        }

        public static void ReportLocalEnemyDeath(NetGameplayDeathEvent deathEvent)
        {
            _service?.ReportLocalEnemyDeathEvent(deathEvent);
        }

        public static void ReportClientEnemyDeathClaim(NetGameplayDeathEvent deathEvent)
        {
            _service?.ReportClientEnemyDeathClaim(deathEvent);
        }

        public static void ReportPlayerLifeState(NetPlayerLifeState state)
        {
            _service?.ReportPlayerLifeState(state);
        }

        public static void ReportHostPlayerLifeStateToAll(NetPlayerLifeState state)
        {
            _service?.ReportHostPlayerLifeStateToAll(state);
        }

        public static IReadOnlyCollection<string> GetKnownPlayerLifePeerIds()
        {
            return _service?.GetKnownPlayerLifePeerIds() ?? new List<string>().AsReadOnly();
        }

        public static IReadOnlyCollection<NetPeerSession> GetSessionsSnapshot()
        {
            return _service?.GetSessionsSnapshot() ?? new List<NetPeerSession>().AsReadOnly();
        }

        public static void BroadcastRunStatsFinalized(NetRunStatsList list)
        {
            _service?.BroadcastRunStatsFinalized(list);
        }

        // Phase 5.0 Host-Driven Proxy — attack phase reliable event channel.
        public static void ReportHostAttackPhaseEvent(NetHostAttackPhaseEvent evt)
        {
            _service?.ReportHostAttackPhaseEvent(evt);
        }

        // Phase 5.0 Host-Driven Proxy — projectile visual spawn reliable event channel.
        public static void ReportHostProjectileVisualSpawn(NetHostProjectileVisualSpawn evt)
        {
            _service?.ReportHostProjectileVisualSpawn(evt);
        }

        // Phase 5.6-WS — true if a co-op session (Host or Client) is running. Cheap gate for per-shot capture.
        public static bool IsSessionActive => _service != null && _service.Mode != NetMode.Off;

        // World item-drop sync — local identity + role helpers.
        public static bool IsHost => _service != null && _service.Mode == NetMode.Host;
        public static string LocalPeerId => _service != null ? _service.LocalPeerId : "";

        // Phase 5.6-WS — player weapon fire (visual barrage) channel. The firing peer reports its local barrage;
        // NetService stamps PeerId + scene context and routes it (Client→Host, Host→all clients + replay locally).
        public static void ReportLocalPlayerWeaponFire(NetPlayerWeaponFire msg)
        {
            _service?.BroadcastLocalPlayerWeaponFire(msg);
        }

        // Phase 5.6-WS-2 — remote held weapon model channel (same topology).
        public static void ReportLocalHeldWeapon(NetPlayerHeldWeapon msg)
        {
            _service?.BroadcastLocalHeldWeapon(msg);
        }

        // Phase 5.7-BR — in-scene destructible break channel. The peer that broke a destructible reports it; NetService
        // stamps PeerId + scene context and routes it (Client→Host→relay to other Clients; firing peer never mirrors).
        public static void ReportLocalBreakableBreak(NetBreakableBreak msg)
        {
            _service?.BroadcastLocalBreakableBreak(msg);
        }

        // HZ-2 — thrown Breakable-throwable effect channel. The peer that threw + broke it reports it; NetService stamps
        // PeerId + scene context and routes it (Client→Host→relay to other Clients; throwing peer never mirrors its own).
        public static void ReportLocalThrowableEffect(NetThrowableEffect msg)
        {
            _service?.BroadcastLocalThrowableEffect(msg);
        }

        // HZ-3 — thrown throwable in-flight body channel (broadcast at release so peers can see the throw arc).
        public static void ReportLocalThrowableFlight(NetThrowableFlight msg)
        {
            _service?.BroadcastLocalThrowableFlight(msg);
        }

        // K-1 (issue #10) — projectile-path throwable (ThrowingKnives) flight visual. The throwing peer reports it;
        // NetService stamps PeerId + scene context and routes it (Client→Host→relay; throwing peer never replays its own).
        public static void ReportLocalThrowableProjectile(NetThrowableProjectile msg)
        {
            _service?.BroadcastLocalThrowableProjectile(msg);
        }

        // SL-2 — shared-loot chest (host-authoritative). A client asks the host to open a chest.
        public static void ReportChestOpenRequest(NetChestOpen msg)
        {
            _service?.SendChestOpenRequest(msg);
        }

        // SL-2 — the host broadcasts a chest it opened so every peer plays the open animation.
        public static void ReportChestOpened(NetChestOpen msg)
        {
            _service?.BroadcastChestOpened(msg);
        }

        // SL-2b — shared-loot LootableObject (food/material/register). A client asks the host to trigger its copy.
        public static void ReportLootableRequest(NetChestOpen msg)
        {
            _service?.SendLootableRequest(msg);
        }

        // SL-2b — the host broadcasts a LootableObject it triggered so every peer replays the animation.
        public static void ReportLootableTriggered(NetChestOpen msg)
        {
            _service?.BroadcastLootableTriggered(msg);
        }

        // TD-1 — shared target-dummy damage numbers. The peer that hit the dummy reports a coalesced batch; NetService
        // stamps PeerId + scene context and routes it (Client→Host→relay to other Clients; the hitting peer never
        // replays its own — it already showed the numbers locally).
        public static void ReportLocalTargetDummyDamage(NetTargetDummyDamage msg)
        {
            _service?.BroadcastLocalTargetDummyDamage(msg);
        }

        // Phase LD-1 — generic combat-room gate (MetalGate) open/close channel. The peer that changed a gate reports it;
        // NetService stamps PeerId + scene context and routes it (Client→Host→relay to other Clients; firing peer never
        // mirrors its own). See GateSyncManager.
        public static void ReportLocalGateState(NetGateState msg)
        {
            _service?.BroadcastLocalGateState(msg);
        }

        // Phase LD-1b — combat-room door (GameObject.SetActive variant, Lucia etc.) channel. Same routing as GateState.
        public static void ReportLocalTriggerDoors(NetTriggerDoors msg)
        {
            _service?.BroadcastLocalTriggerDoors(msg);
        }

        // Phase DB-1 — inter-chunk hold-to-open door (DoorBlocker) channel. Same routing as GateState.
        public static void ReportLocalDoorBlockerOpen(NetDoorBlockerOpen msg)
        {
            _service?.BroadcastLocalDoorBlockerOpen(msg);
        }

        // Phase LD-2a — arena lockdown membership + run-state queries (host membership/timer; see ArenaLockdownManager).
        public static void SendClientArenaEnter(NetClientArenaEnter msg) => _service?.SendClientArenaEnter(msg);

        // Phase LD-2b/c — host-authoritative arena-lockdown command (Host→clients): Seal / Popup / Release.
        public static void BroadcastArenaCommand(NetArenaCommand msg) => _service?.BroadcastArenaCommand(msg);

        public static bool TryGetLocalScene(out string chapter, out int level, out bool hasSeed, out int seed)
        {
            chapter = ""; level = -1; hasSeed = false; seed = 0;
            return _service != null && _service.TryGetLocalScene(out chapter, out level, out hasSeed, out seed);
        }

        public static System.Collections.Generic.List<string> GetPeerIdsInLevel(string chapter, int level, bool hasSeed, int seed)
            => _service?.GetPeerIdsInLevel(chapter, level, hasSeed, seed) ?? new System.Collections.Generic.List<string>();

        // World item-drop sync — spawn (optimistic, peer-authoritative; Client→Host→relay), take request (Client→Host),
        // removal broadcast (Host→all). See WorldPickupManager.
        public static void ReportLocalWorldPickupSpawn(NetWorldPickupSpawn msg)
        {
            _service?.BroadcastLocalWorldPickupSpawn(msg);
        }

        public static void SendWorldPickupTakeRequest(string ownerPeerId, ushort seq)
        {
            _service?.SendWorldPickupTakeRequest(ownerPeerId, seq);
        }

        // WID-2: owner reports its dropped pickup's authoritative rest position (owner→Host→relay).
        public static void ReportLocalWorldPickupSettle(NetWorldPickupSettle msg)
        {
            _service?.BroadcastLocalWorldPickupSettle(msg);
        }

        public static void BroadcastWorldPickupRemoved(NetWorldPickupRemoved msg)
        {
            _service?.BroadcastWorldPickupRemoved(msg);
        }

        // Phase 5.1 Host-authoritative enemy health sync.
        public static void ReportHostEnemyDamageEvent(NetHostEnemyDamageEvent evt)
        {
            _service?.ReportHostEnemyDamageEvent(evt);
        }

        public static void ReportHostEnemyHealthState(NetHostEnemyHealthState state)
        {
            _service?.ReportHostEnemyHealthState(state);
        }

        // Phase 5.3-B Client → Host hit request.
        public static void SendClientHitRequest(NetClientHitRequest request)
        {
            _service?.SendClientHitRequest(request);
        }

        // Issue #5 Client → Host one-shot TriggerSpawner request.
        public static void SendClientTriggerSpawn(NetTriggerSpawn msg)
        {
            _service?.SendClientTriggerSpawn(msg);
        }

        // Phase 5.3-E Host → Client level manifest.
        public static void SendHostLevelManifest(NetLevelManifest manifest)
        {
            _service?.SendHostLevelManifest(manifest);
        }

        // Phase 5.3-F Host → Client hit visual event.
        public static void ReportHostHitVisualEvent(NetHostHitVisualEvent evt)
        {
            _service?.ReportHostHitVisualEvent(evt);
        }

        // Phase 5.4-E Boss Encounter Authority.
        public static NetMode BossMode => _service?.Mode ?? NetMode.Off;

        /// <summary>Phase PF-0: local-vs-remote scene+seed convergence summary for the boss pre-fight probe.</summary>
        public static string FormatBossConvergence(out bool allConverged)
        {
            allConverged = false;
            if (_service == null) return "no-session";
            return _service.FormatBossConvergence(out allConverged);
        }

        public static void SendClientBossStartRequest(SULFURTogether.Networking.Gameplay.Boss.NetClientBossStartRequest req)
        {
            _service?.SendClientBossStartRequest(req);
        }

        public static void BroadcastHostBossEncounterStart(SULFURTogether.Networking.Gameplay.Boss.NetBossEncounterState state)
        {
            _service?.BroadcastHostBossEncounterStart(state);
        }

        // Phase 5.4-E3 Boss dialog commit + boss state.
        public static void SendClientBossDialogCommitRequest(SULFURTogether.Networking.Gameplay.Boss.NetBossDialogCommit msg)
            => _service?.SendClientBossDialogCommitRequest(msg);

        public static void BroadcastHostBossDialogCommit(SULFURTogether.Networking.Gameplay.Boss.NetBossDialogCommit msg)
            => _service?.BroadcastHostBossDialogCommit(msg);

        public static void BroadcastHostBossState(SULFURTogether.Networking.Gameplay.Boss.NetBossState msg)
            => _service?.BroadcastHostBossState(msg);

        // EMP-3a: Emperor phase-1 worm head stream (host → clients).
        public static void BroadcastEmperorWormHead(float x, float y, float z, float rotY, float tailHp, int seq)
            => _service?.BroadcastEmperorWormHead(x, y, z, rotY, tailHp, seq);

        // EMP-3b: Emperor worm section-destruction event (host → clients).
        public static void BroadcastEmperorWormSectionDestroy(int seq)
            => _service?.BroadcastEmperorWormSectionDestroy(seq);

        // EMP-3c: Emperor worm terminal death (host → clients).
        public static void BroadcastEmperorWormDeath(int seq)
            => _service?.BroadcastEmperorWormDeath(seq);

        // EMP-3d: Emperor worm damage authority (client → host).
        public static void SendClientEmperorWormHit(float damage, int damageTypeInt, int seq)
            => _service?.SendClientEmperorWormHit(damage, damageTypeInt, seq);

        // EMP-4: Emperor fight-start (dialog) commit — client requests, host broadcasts the authoritative start.
        public static void SendClientEmperorFightStart()
            => _service?.SendClientEmperorFightStart();

        public static void BroadcastEmperorFightStart()
            => _service?.BroadcastEmperorFightStart();

        // EMP-6b: Emperor phase-2 spider.
        public static void BroadcastEmperorSpiderTransform(float x, float y, float z, float rotY, int wp, int tgt, float hp, int seq)
            => _service?.BroadcastEmperorSpiderTransform(x, y, z, rotY, wp, tgt, hp, seq);
        public static void SendClientEmperorSpiderFightStart()
            => _service?.SendClientEmperorSpiderFightStart();
        public static void BroadcastEmperorSpiderFightStart(string starterPeerId)
            => _service?.BroadcastEmperorSpiderFightStart(starterPeerId);
        public static void SendClientEmperorSpiderHit(float damage, int damageTypeInt, int seq)
            => _service?.SendClientEmperorSpiderHit(damage, damageTypeInt, seq);
        public static void BroadcastEmperorSpiderEvent(int eventCode, int seq)
            => _service?.BroadcastEmperorSpiderEvent(eventCode, seq);

        // Phase RM: room-membership substrate.
        public static void SendClientRoomEnter(SULFURTogether.Networking.Gameplay.Boss.NetClientRoomEnter msg)
            => _service?.SendClientRoomEnter(msg);

        public static void BroadcastHostRoomMembership(SULFURTogether.Networking.Gameplay.Boss.NetHostRoomMembership msg)
            => _service?.BroadcastHostRoomMembership(msg);

        // Phase 5.4-E4 Boss dynamic spawn manifest.
        public static void BroadcastHostBossDynamicSpawn(SULFURTogether.Networking.Gameplay.Boss.NetBossDynamicSpawn msg)
            => _service?.BroadcastHostBossDynamicSpawn(msg);

        // Phase RT3-Cousin-arms: client-side enumeration of remote player positions (for visual-only arm balls).
        public static void ForEachRemotePlayerPosition(System.Action<UnityEngine.Vector3> action)
            => _service?.ForEachRemotePlayerPosition(action);

        // Phase RT3-Cousin-arms-Room: same, carrying peerId so out-of-room remote players can be skipped.
        public static void ForEachRemotePlayerPositionWithPeer(System.Action<string, UnityEngine.Vector3> action)
            => _service?.ForEachRemotePlayerPositionWithPeer(action);

        // F4-MISSILE D2: remote player visual TRANSFORMS (live homing targets for ghost visual rockets, both ends).
        public static void ForEachRemotePlayerTransform(System.Action<string, UnityEngine.Transform> action)
            => _service?.ForEachRemotePlayerTransform(action);

        // Phase 5.4-F BossDamageAuthority.
        public static void SendClientBossHitRequest(SULFURTogether.Networking.Gameplay.Boss.NetClientBossHitRequest req)
            => _service?.SendClientBossHitRequest(req);

        // Phase 5.4-F2 BossDamage feedback.
        public static void BroadcastHostBossHitVisual(SULFURTogether.Networking.Gameplay.Boss.NetHostBossHitVisual msg)
            => _service?.BroadcastHostBossHitVisual(msg);

        // Phase 5.4-F4 fixed-point discrete event.
        public static void BroadcastHostBossDiscreteEvent(SULFURTogether.Networking.Gameplay.Boss.NetBossDiscreteEvent msg)
            => _service?.BroadcastHostBossDiscreteEvent(msg);

        // Phase 5.4-F5 Lucia eye defeat authority.
        public static void SendClientLuciaEyeReport(SULFURTogether.Networking.Gameplay.Boss.NetLuciaEyeReport req)
            => _service?.SendClientLuciaEyeReport(req);

        public static void BroadcastHostLuciaEyeState(SULFURTogether.Networking.Gameplay.Boss.NetLuciaEyeState msg)
            => _service?.BroadcastHostLuciaEyeState(msg);

        // Phase 5.4-F6 Lucia terminal death authority.
        public static void BroadcastHostLuciaDeath(SULFURTogether.Networking.Gameplay.Boss.NetLuciaDeath msg)
            => _service?.BroadcastHostLuciaDeath(msg);

        // Phase 5.4-G2 Witch phase revision authority.
        public static void BroadcastHostWitchPhase(SULFURTogether.Networking.Gameplay.Boss.NetWitchPhase msg)
            => _service?.BroadcastHostWitchPhase(msg);

        // Phase 5.4-G5 Witch Phase 2 dome manifest + result.
        public static void BroadcastHostWitchP2Manifest(SULFURTogether.Networking.Gameplay.Boss.NetWitchP2Manifest msg)
            => _service?.BroadcastHostWitchP2Manifest(msg);

        public static void BroadcastHostWitchP2Result(SULFURTogether.Networking.Gameplay.Boss.NetWitchP2Result msg)
            => _service?.BroadcastHostWitchP2Result(msg);

        // Phase 5.5-RT1 runtime spawn sync.
        public static void BroadcastHostRuntimeSpawn(SULFURTogether.Networking.Gameplay.NetRuntimeSpawn msg)
            => _service?.BroadcastHostRuntimeSpawn(msg);

        public static void BroadcastHostEndlessWaveState(SULFURTogether.Networking.Gameplay.NetEndlessWaveState msg)
            => _service?.BroadcastHostEndlessWaveState(msg);

        public static void BroadcastHostEndlessXpDrop(SULFURTogether.Networking.Gameplay.NetEndlessXpDrop msg)
            => _service?.BroadcastHostEndlessXpDrop(msg);

        public static void SendEndlessXpCollectRequest(int dropId)
            => _service?.SendEndlessXpCollectRequest(dropId);

        public static void BroadcastHostEndlessXpCollected(SULFURTogether.Networking.Gameplay.NetEndlessXpCollect msg)
            => _service?.BroadcastHostEndlessXpCollected(msg);

        // EM req 2: Client → Host, "I entered/left Independent-mode card selection" (host suppresses my ghost as a target).
        public static void SendEndlessCardSelect(bool selecting)
            => _service?.SendEndlessCardSelect(selecting);

        // EM-6b: Host → all, the N cards the host rolled for one shared card-select event.
        public static void BroadcastHostEndlessCardManifest(SULFURTogether.Networking.Gameplay.NetEndlessCardManifest msg)
            => _service?.BroadcastHostEndlessCardManifest(msg);

        // EM-6b-2: Host → all, the pre-roll card RNG + selection state so the client reproduces identical 3D cards.
        public static void BroadcastHostEndlessCardRoll(SULFURTogether.Networking.Gameplay.NetEndlessCardRoll msg)
            => _service?.BroadcastHostEndlessCardRoll(msg);

        // EM-6b-3a: Host → all, the shared card-vote snapshot (tally + resolved index).
        public static void BroadcastHostEndlessCardVoteState(SULFURTogether.Networking.Gameplay.NetEndlessCardVoteState msg)
            => _service?.BroadcastHostEndlessCardVoteState(msg);

        // EM-6b-3a: Client → Host, "my player cast a vote for this ordinary card index".
        public static void SendEndlessCardVoteCast(int cardEventId, int votedIndex, byte kind)
            => _service?.SendEndlessCardVoteCast(cardEventId, votedIndex, kind);

        // FF-1: Client → Host friendly-fire hit report.
        public static void SendFriendlyFireHit(NetFriendlyFireHit msg)
            => _service?.SendFriendlyFireHit(msg);
    }
}
