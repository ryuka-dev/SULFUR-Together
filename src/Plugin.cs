using System;
using BepInEx;
using HarmonyLib;
using SULFURTogether.Config;
using SULFURTogether.Logging;
using SULFURTogether.Networking;
using SULFURTogether.Networking.Gameplay;
using SULFURTogether.Patches;
using SULFURTogether.ReverseProbe;

namespace SULFURTogether
{
    [BepInPlugin(ModInfo.GUID, ModInfo.Name, ModInfo.Version)]
    public class Plugin : BaseUnityPlugin
    {
        public static Plugin     Instance { get; private set; } = null!;
        public static STLogger   Log      { get; private set; } = null!;
        public static CoopConfig Cfg      { get; private set; } = null!;

        private NetService? _netService;

        private void Awake()
        {
            Instance = this;
            Cfg      = new CoopConfig(Config);
            Log      = new STLogger(Logger, Cfg);

            Log.Info($"v{ModInfo.Version} by {ModInfo.Author} loading...");
            Log.Info("[Build] Phase 5.7-SC4: SC3 host-death puppet release locked in (verified); + [RetroBindDiag] probe left in place to self-document the rare same-seed spawn-divergence standing-still next time it recurs 2026-06-25");
            var harmony = new Harmony(ModInfo.GUID);
            PatchBootstrap.ApplyAll(harmony);

            // Phase 2 network — dead when EnableNetworking=false or NetworkMode=Off
            try
            {
                var mode = NetConfig.GetMode();
                if (mode != NetMode.Off)
                {
                    _netService = new NetService();
                    NetRunStateBridge.Attach(_netService);
                    NetGameplaySyncBridge.Attach(_netService);
                    _netService.Start(mode);
                }
                else
                {
                    NetRunStateBridge.Attach(null);
                    NetGameplaySyncBridge.Attach(null);
                    Log.Info("[Net] Networking disabled (EnableNetworking=false or NetworkMode=Off).");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[Net] Failed to start — LiteNetLib.dll missing or failed to load. ({ex.GetType().Name}: {ex.Message})");
            }

            Log.Info("[ConfigPolicy] Private development build: active experimental gameplay defaults are forced on load; connection settings such as NetworkMode/HostAddress/HostPort/PlayerName are left user-owned.");
            Log.Info($"[Config] EnableDebugLog={Cfg.EnableDebugLog.Value} | EnableReverseProbe={Cfg.EnableReverseProbe.Value} | EnableNetworking={Cfg.EnableNetworking.Value} | NetworkMode={Cfg.NetworkMode.Value}");
            Log.Info($"[Config] EnableInventorySerializationProbe={Cfg.EnableInventorySerializationProbe.Value} | EnableAiUpdateTargetProbe={Cfg.EnableAiUpdateTargetProbe.Value} | EnableAiSetDestinationProbe={Cfg.EnableAiSetDestinationProbe.Value}");
            Log.Info($"[Config] EnableVerbosePickupProbe={Cfg.EnableVerbosePickupProbe.Value} | EnableVerboseLootProbe={Cfg.EnableVerboseLootProbe.Value} | CompactPickupLogs={Cfg.CompactPickupLogs.Value} | CompactLootLogs={Cfg.CompactLootLogs.Value}");
            Log.Info($"[Config] EnableRunStateNegotiation={Cfg.EnableRunStateNegotiation.Value} | RunStateBroadcastIntervalSeconds={Cfg.RunStateBroadcastIntervalSeconds.Value} | WarnOnRunStateMismatch={Cfg.WarnOnRunStateMismatch.Value}");
            Log.Info($"[Config] EnableHostSceneAuthority={Cfg.EnableHostSceneAuthority.Value} | WarnOnClientSceneDrift={Cfg.WarnOnClientSceneDrift.Value}");
            Log.Info($"[Config] EnableHostSceneRequestProtocol={Cfg.EnableHostSceneRequestProtocol.Value} | AutoSendHostSceneRequestOnDrift={Cfg.AutoSendHostSceneRequestOnDrift.Value} | HostSceneRequestIntervalSeconds={Cfg.HostSceneRequestIntervalSeconds.Value}");
            Log.Info($"[Config] EnableManualClientSceneFollow={Cfg.EnableManualClientSceneFollow.Value} | ManualClientSceneFollowKey={Cfg.ManualClientSceneFollowKey.Value} | ManualClientSceneFollowRequiresHostRequest={Cfg.ManualClientSceneFollowRequiresHostRequest.Value}");
            Log.Info($"[Config] EnableLevelSeedAuthority={Cfg.EnableLevelSeedAuthority.Value} | RequireSameLevelSeedForSceneMatch={Cfg.RequireSameLevelSeedForSceneMatch.Value} | ApplyHostLevelSeedOnManualFollow={Cfg.ApplyHostLevelSeedOnManualFollow.Value} | HideRemoteVisualWhenLevelSeedMismatch={Cfg.HideRemoteVisualWhenLevelSeedMismatch.Value}");
            Log.Info($"[Config] EnableRemotePlayerVisualProxy={Cfg.EnableRemotePlayerVisualProxy.Value} | RemotePlayerTransformSendRateHz={Cfg.RemotePlayerTransformSendRateHz.Value} | RemotePlayerVisualTimeoutSeconds={Cfg.RemotePlayerVisualTimeoutSeconds.Value} | RemotePlayerVisualInterpolationSpeed={Cfg.RemotePlayerVisualInterpolationSpeed.Value} | RemotePlayerVisualSnapDistance={Cfg.RemotePlayerVisualSnapDistance.Value}");
            Log.Info($"[Config] EnableGameplayEntityProbe={Cfg.EnableGameplayEntityProbe.Value} | GameplayEntityProbeSummaryIntervalSeconds={Cfg.GameplayEntityProbeSummaryIntervalSeconds.Value} | LogGameplayEntitySpawn={Cfg.LogGameplayEntitySpawn.Value} | LogGameplayEntityDamage={Cfg.LogGameplayEntityDamage.Value} | LogGameplayEntityDeath={Cfg.LogGameplayEntityDeath.Value} | RequireStableSceneAndSeedForGameplayProbe={Cfg.RequireStableSceneAndSeedForGameplayProbe.Value}");
            Log.Info($"[Config] EnableHostEnemyDeathEventMirror={Cfg.EnableHostEnemyDeathEventMirror.Value} | LogReceivedEnemyDeathEvents={Cfg.LogReceivedEnemyDeathEvents.Value} | ApplyReceivedEnemyDeathEvents={Cfg.ApplyReceivedEnemyDeathEvents.Value} | EnemyDeathMirrorPositionTolerance={Cfg.EnemyDeathMirrorPositionTolerance.Value} | EnemyDeathMirrorUseHorizontalPositionTolerance={Cfg.EnemyDeathMirrorUseHorizontalPositionTolerance.Value}");
            Log.Info($"[Config] EnableClientEnemyDeathClaim={Cfg.EnableClientEnemyDeathClaim.Value} | LogReceivedClientEnemyDeathClaims={Cfg.LogReceivedClientEnemyDeathClaims.Value} | ApplyReceivedClientEnemyDeathClaimsOnHost={Cfg.ApplyReceivedClientEnemyDeathClaimsOnHost.Value}");
            Log.Info($"[Config] EnableCoopPlayerDownedRevive={Cfg.EnableCoopPlayerDownedRevive.Value} | PlayerDownedRescueTimeoutSeconds={Cfg.PlayerDownedRescueTimeoutSeconds.Value} | PlayerReviveHoldSeconds={Cfg.PlayerReviveHoldSeconds.Value} | PlayerReviveDistance={Cfg.PlayerReviveDistance.Value} | PlayerReviveHoldKey={Cfg.PlayerReviveHoldKey.Value} | PlayerReviveHealthRatio={Cfg.PlayerReviveHealthRatio.Value} | RequireReviveDistanceValidationOnHost={Cfg.RequireReviveDistanceValidationOnHost.Value}");
            Log.Info($"[Config] EnableHostEnemyStateSnapshotMirror={Cfg.EnableHostEnemyStateSnapshotMirror.Value} | EnemyStateSnapshotSendRateHz={Cfg.EnemyStateSnapshotSendRateHz.Value} | EnemyStateSnapshotMaxEnemiesPerPacket={Cfg.EnemyStateSnapshotMaxEnemiesPerPacket.Value} | OnlySendAliveEnemyStateSnapshots={Cfg.OnlySendAliveEnemyStateSnapshots.Value} | LogReceivedEnemyStateSnapshots={Cfg.LogReceivedEnemyStateSnapshots.Value} | ApplyReceivedEnemyStateSnapshots={Cfg.ApplyReceivedEnemyStateSnapshots.Value} | EnemyStateSnapshotPositionTolerance={Cfg.EnemyStateSnapshotPositionTolerance.Value}");
            Log.Info($"[Config] EnemyStateSnapshotInterpolationSpeed={Cfg.EnemyStateSnapshotInterpolationSpeed.Value} | EnemyStateSnapshotPlaybackDurationMultiplier={Cfg.EnemyStateSnapshotPlaybackDurationMultiplier.Value} | EnemyStateSnapshotSnapDistance={Cfg.EnemyStateSnapshotSnapDistance.Value} | EnemyStateSnapshotApplyRotationY={Cfg.EnemyStateSnapshotApplyRotationY.Value} | EnableClientEnemyAiSuppressionExperiment={Cfg.EnableClientEnemyAiSuppressionExperiment.Value} | SuppressClientEnemyAiWhenStateMirrorEnabled={Cfg.SuppressClientEnemyAiWhenStateMirrorEnabled.Value} | LogSuppressedClientEnemyAi={Cfg.LogSuppressedClientEnemyAi.Value} | EnableClientEnemyPuppetMode={Cfg.EnableClientEnemyPuppetMode.Value} | ClientEnemyPuppetStaleReleaseSeconds={Cfg.ClientEnemyPuppetStaleReleaseSeconds.Value} | LogClientEnemyPuppetMode={Cfg.LogClientEnemyPuppetMode.Value}");
            Log.Info($"[Config] EnableHostEnemyAnimationMirror={Cfg.EnableHostEnemyAnimationMirror.Value} | ApplyReceivedEnemyAnimationMirror={Cfg.ApplyReceivedEnemyAnimationMirror.Value} | LogEnemyAnimationMirror={Cfg.LogEnemyAnimationMirror.Value} | EnemyAnimationMirrorCrossFadeSeconds={Cfg.EnemyAnimationMirrorCrossFadeSeconds.Value} | EnemyAnimationMirrorNormalizedTimeTolerance={Cfg.EnemyAnimationMirrorNormalizedTimeTolerance.Value} | EnemyAnimationMirrorApplyAnimatorStatePlayback={Cfg.EnemyAnimationMirrorApplyAnimatorStatePlayback.Value} | EnemyAnimationMirrorApplyHostCombatStatePlayback={Cfg.EnemyAnimationMirrorApplyHostCombatStatePlayback.Value} | EnemyAnimationMirrorReplayHostCombatMethods={Cfg.EnemyAnimationMirrorReplayHostCombatMethods.Value} | EnemyAnimationMirrorApplyCombatAnimatorFallback={Cfg.EnemyAnimationMirrorApplyCombatAnimatorFallback.Value} | EnemyAnimationMirrorHostCombatActionHoldSeconds={Cfg.EnemyAnimationMirrorHostCombatActionHoldSeconds.Value}");
            Log.Info($"[Config] EnableHostOnlyEnemyTargetAuthority={Cfg.EnableHostOnlyEnemyTargetAuthority.Value} | EnemyProjectileVisualMirrorEnabled={Cfg.EnemyProjectileVisualMirrorEnabled.Value} | EnemyProjectileVisualMirrorUseNativeShootReplay={Cfg.EnemyProjectileVisualMirrorUseNativeShootReplay.Value} | EnableGenericHostCombatAnimatorStateMirror={Cfg.EnableGenericHostCombatAnimatorStateMirror.Value} | EnableHostAuthoritativeEnemyRangedDamage={Cfg.EnableHostAuthoritativeEnemyRangedDamage.Value} | EnableClientEnemyIntentDrivenMotion={Cfg.EnableClientEnemyIntentDrivenMotion.Value} | EnemyIntentCorrectionDistance={Cfg.EnemyIntentCorrectionDistance.Value} | EnemyIntentHardSnapDistance={Cfg.EnemyIntentHardSnapDistance.Value} | LogEnemyTargetAuthority={Cfg.LogEnemyTargetAuthority.Value} | EnemyTargetAuthorityProbeIntervalSeconds={Cfg.EnemyTargetAuthorityProbeIntervalSeconds.Value} | EnableEnemyCombatProbe={Cfg.EnableEnemyCombatProbe.Value} | LogEnemyCombatProbe={Cfg.LogEnemyCombatProbe.Value} | EnemyHostProjectileHitRadius={Cfg.EnemyHostProjectileHitRadius.Value} | EnemyHostProjectileDamage={Cfg.EnemyHostProjectileDamage.Value}");
            Log.Info($"[Config] EnableEnemyStateSnapshotDeltaCompression={Cfg.EnableEnemyStateSnapshotDeltaCompression.Value} | EnemyStateSnapshotHeartbeatSeconds={Cfg.EnemyStateSnapshotHeartbeatSeconds.Value} | EnemyStateSnapshotPositionDeltaThreshold={Cfg.EnemyStateSnapshotPositionDeltaThreshold.Value} | EnemyStateSnapshotRotationDeltaThresholdDegrees={Cfg.EnemyStateSnapshotRotationDeltaThresholdDegrees.Value} | EnemyStateSnapshotAnimationTimeDeltaThreshold={Cfg.EnemyStateSnapshotAnimationTimeDeltaThreshold.Value}");
            Log.Info("Ready.");
        }

        private void Update()
        {
            ReverseProbeSummary.Tick();
            PlayerVisualDiscoveryProbe.TryDumpOnce();
            PlayerSpriteAssetScanProbe.TryScanOnce();
            NetGameplayProbeManager.Tick();
            NetPlayerLifeManager.Tick();
            Networking.Gameplay.Boss.NetBossEncounterManager.Tick();
            Networking.Gameplay.Boss.BossDynamicSpawnManifest.TickReleaseStaleGated(); // RT3-A safety: release stuck gates
            _netService?.Tick();
        }

        private void FixedUpdate()
        {
            _netService?.FixedTick();
        }

        private void OnGUI()
        {
            NetPlayerLifeManager.DrawOnGUI();
        }

        private void OnDestroy()
        {
            NetRunStateBridge.Attach(null);
            NetGameplaySyncBridge.Attach(null);
            _netService?.Stop();
        }
    }
}
