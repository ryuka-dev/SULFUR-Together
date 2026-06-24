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

        // Phase 5.4-E4 Boss dynamic spawn manifest.
        public static void BroadcastHostBossDynamicSpawn(SULFURTogether.Networking.Gameplay.Boss.NetBossDynamicSpawn msg)
            => _service?.BroadcastHostBossDynamicSpawn(msg);

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
    }
}
