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

        private void Awake()
        {
            Instance = this;
            Cfg      = new CoopConfig(Config);
            Log      = new STLogger(Logger, Cfg);

            Log.Info($"v{ModInfo.Version} by {ModInfo.Author} loading...");
            Log.Info("[Build] CfgCleanup batch 4 (NetworkEnemy + NetworkGameplaySyncExperimental): 11 runtime-spawn + enemy-death-mirror functional/tuning flags hardcoded (Fixed), removed from .cfg. Log* kept. 2026-06-30");
            Log.Info("[Build] CfgCleanup batch 3 (NetworkVisualProxy): 9 remote-visual-proxy functional/tuning flags hardcoded (Fixed), removed from .cfg. 2026-06-30");
            Log.Info("[Build] CfgCleanup batch 2 (NetworkRunState+NetworkLevelSeed): EnableRunStateNegotiation/RunStateBroadcastIntervalSeconds/EnableLevelSeedAuthority/RequireSameLevelSeedForSceneMatch/ApplyHostLevelSeedOnManualFollow/HideRemoteVisualWhenLevelSeedMismatch/SyncHostUsedSetsOnManualFollow hardcoded (Fixed), removed from .cfg. Warn*/Log* kept. 2026-06-30");
            Log.Info("[Build] UI-CleanRole: networking role is runtime-only (NetworkMode/EnableNetworking dropped from the .cfg); connection settings (name/IP/port/key/maxplayers/version + EnableCoopToasts) moved to a private JSON store (CoopSettingsStore) so they stay out of Gale; retired .cfg keys pruned via OrphanedEntries reflection. + REGRESSION FIX: promoted Plan B targeting flags EnableRemotePlayerInPlayersList + EnableGhostPlayerHitbox into the dev-defaults (were local-cfg-only; a fresh/deleted config left enemies idle = 站桩). 2026-06-30");
            var harmony = new Harmony(ModInfo.GUID);
            PatchBootstrap.ApplyAll(harmony);

            WireCoopUi();

            // Phase 2 network — boots Off. The role (Host/Client) is decided at runtime by the connect UI (UI-3)
            // via CoopConnection and is never read from the .cfg; Initialize just establishes the clean Off state.
            CoopConnection.Initialize();

            Log.Info("[ConfigPolicy] Private development build: active experimental gameplay defaults are forced on load; connection settings such as HostAddress/HostPort/PlayerName are left user-owned. The networking role is runtime-only (not persisted).");
            Log.Info($"[Config] EnableDebugLog={Cfg.EnableDebugLog.Value} | EnableReverseProbe={Cfg.EnableReverseProbe.Value} | NetMode={NetConfig.GetMode()}");
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

#if NATIVE_UI_LIB
                // UI-3b: register the in-game connect page. Guarded by the runtime type check so the lib
                // assembly is only touched when it's actually loaded (soft dependency holds).
                if (AccessTools.TypeByName("Ryuka.Sulfur.NativeUI.SulfurOptionsApi") != null)
                {
                    UI.CoopConnectPage.Register();
                    Log.Info("[CoopUi] connect page registered (SulfurOptionsApi).");
                }
                else Log.Info("[CoopUi] SulfurOptionsApi not present — connect page unavailable.");
#endif
            }
            catch (Exception ex)
            {
                Log.Warn($"[CoopUi] failed to wire Native UI Lib surfaces: {ex.Message}");
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
            CoopConnection.Tick();
            Patches.PauseControlPatches.Tick(); // 5.7-NP2: seamless un-pause when a session starts with a menu open
#if NATIVE_UI_LIB
            UI.CoopConnectPage.Tick(); // UI-3c: drive the connect page's live status/buttons/list handles
#endif
        }

        private void FixedUpdate()
        {
            CoopConnection.FixedTick();
        }

        private void OnGUI()
        {
            NetPlayerLifeManager.DrawOnGUI();
        }

        private void OnDestroy()
        {
#if NATIVE_UI_LIB
            try { UI.CoopConnectPage.Unregister(); } catch { /* lib may be gone */ }
#endif
            CoopConnection.Stop("plugin destroyed");
        }
    }
}
