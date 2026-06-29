using System;
using BepInEx;
using HarmonyLib;
using SULFURTogether.Config;
using SULFURTogether.Logging;
using SULFURTogether.Networking;
using SULFURTogether.Networking.Gameplay;
using SULFURTogether.Patches;
using SULFURTogether.ReverseProbe;
using SULFURTogether.UI;

namespace SULFURTogether
{
    [BepInPlugin(ModInfo.GUID, ModInfo.Name, ModInfo.Version)]
    // LD-2c popup: optional native banner from SULFUR Native UI Lib. Soft — the mod runs fine without it
    // (the arena-lockdown prompt just logs and confirm still works via the key); when present it provides the visual.
    [BepInDependency("ryuka.sulfur.nativeui", BepInDependency.DependencyFlags.SoftDependency)]
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
            Log.Info("[Build] Phase 5.7-DS2: host-authoritative SpawnMinions sync (spawnMinionsOnDeath) — host tags the parent UnitSO so the async minion spawns broadcast via the runtime pipeline; client suppresses its local SpawnMinions and mirrors. Fixes LogOutput118 'never bound, late-bind failed' on a GoblinYoung minion wave. + BatchedNPCRaycasts.LateUpdate finalizer swallows the roster/Players index race (1× IndexOutOfRange during runtime spawns). gates EnableMinionSpawnSync / EnableDestroyedUnitListSweep 2026-06-25");
            var harmony = new Harmony(ModInfo.GUID);
            PatchBootstrap.ApplyAll(harmony);

            WireCoopUi();

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

        /// <summary>Wire the optional SULFUR Native UI Lib surfaces (soft dependency, resolved by reflection so we
        /// don't hard-link the assembly). The lib exposes <c>Ryuka.Sulfur.NativeUI.SulfurPopupApi.ShowBanner/HideBanner</c>
        /// (LD-2c arena-lockdown confirm prompt) and <c>SulfurToastApi.Show(title, message)</c> (UI-1 co-op event
        /// toasts). Absent → the seams stay null and events are logged only; gameplay is unaffected.</summary>
        private void WireCoopUi()
        {
            try
            {
                var apiType = AccessTools.TypeByName("Ryuka.Sulfur.NativeUI.SulfurPopupApi");
                if (apiType == null)
                {
                    Log.Info("[ArenaLockdown] SULFUR Native UI Lib not present — confirm prompt will be logged only (UI optional).");
                    return;
                }
                var show = AccessTools.Method(apiType, "ShowBanner", new[] { typeof(string) });
                var hide = AccessTools.Method(apiType, "HideBanner", Type.EmptyTypes);
                if (show == null || hide == null)
                {
                    Log.Warn("[ArenaLockdown] SulfurPopupApi found but ShowBanner/HideBanner missing — prompt logged only.");
                    return;
                }
                ArenaLockdownManager.ShowPrompt = text => show.Invoke(null, new object[] { text });
                ArenaLockdownManager.HidePrompt = () => hide.Invoke(null, null);
                Log.Info("[ArenaLockdown] confirm prompt wired to SULFUR Native UI Lib banner (SulfurPopupApi).");

                // Toast surface (UI Lib 0.9.0) — shared by LD-2c wait toasts and UI-1 co-op event toasts.
                // Optional — absent → toasts are logged only.
                var toastType = AccessTools.TypeByName("Ryuka.Sulfur.NativeUI.SulfurToastApi");
                var showToast = toastType == null ? null : AccessTools.Method(toastType, "Show", new[] { typeof(string), typeof(string) });
                if (showToast != null)
                {
                    Action<string, string> toast = (title, msg) => showToast.Invoke(null, new object[] { title, msg });
                    ArenaLockdownManager.ShowToast = toast;
                    CoopToasts.Wire(toast);
                    Log.Info("[CoopUi] toasts wired to SULFUR Native UI Lib (SulfurToastApi).");
                }
                else Log.Info("[CoopUi] SulfurToastApi not present — toasts logged only.");
            }
            catch (Exception ex)
            {
                Log.Warn($"[ArenaLockdown] failed to wire Native UI Lib popup: {ex.Message}");
            }
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
            Networking.Gameplay.ArenaLockdownManager.Tick(); // LD-2a: host arena lockdown timers
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
