using System;
using System.Collections;
using System.IO;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;

namespace SULFURTogether.Config
{
    /// <summary>
    /// Persists the co-op mod's own player-facing settings — the ones the in-game connect page owns — to a private
    /// JSON file next to the BepInEx <c>.cfg</c>. The file uses a <c>.json</c> extension precisely so Gale's
    /// config editor (which only scans <c>BepInEx/config/*.cfg</c>) never sees it: the result is one storage area
    /// for standard, Gale-editable config (debug/probe toggles, still in the <c>.cfg</c>) and a separate one for our
    /// settings-page data, with no overlap or duplicated UI.
    ///
    /// On first run it migrates any values the user previously had in the old BepInEx keys, then
    /// <see cref="PruneRetiredCfgKeys"/> physically removes those keys from the <c>.cfg</c> so they don't reappear
    /// in Gale (BepInEx otherwise preserves now-unbound keys as "orphaned entries").
    /// </summary>
    public sealed class CoopSettingsStore
    {
        /// <summary>The connection/UI settings, serialized as-is by <see cref="JsonUtility"/> (public fields only).</summary>
        [Serializable]
        public sealed class Data
        {
            public string playerName            = "Player";
            public string hostAddress           = "127.0.0.1";
            public int    hostPort              = 9050;
            public string connectionKey         = "SULFUR_TOGETHER_DEV";
            public int    maxPlayers            = 4;
            public bool   requireSameModVersion = true;
            public bool   enableCoopToasts      = true;
        }

        // BepInEx keys that no longer exist as bound entries: the connection/UI settings that moved into this store,
        // plus the role keys dropped earlier (now runtime-only). Pruned from the .cfg on every load so Gale never
        // shows a stale co-op entry. Keys are unique across our sections, so matching by key name is sufficient.
        private static readonly string[] RetiredCfgKeys =
        {
            "EnableNetworking", "NetworkMode",
            "PlayerName", "HostAddress", "HostPort", "ConnectionKey", "MaxPlayers", "RequireSameModVersion",
            "EnableCoopToasts",
            // Release cleanup — functional Enable* flags hardcoded (Fixed<bool>), removed from the .cfg, batch by batch.
            // Batch 1 (Destructibles):
            "EnableBreakableSync", "EnableGateSync", "EnableTriggerDoorSync",
            // Batch 2 (NetworkRunState + NetworkLevelSeed):
            "EnableRunStateNegotiation", "RunStateBroadcastIntervalSeconds",
            "EnableLevelSeedAuthority", "RequireSameLevelSeedForSceneMatch", "ApplyHostLevelSeedOnManualFollow",
            "HideRemoteVisualWhenLevelSeedMismatch", "SyncHostUsedSetsOnManualFollow",
            // Batch 3 (NetworkVisualProxy):
            "EnableRemotePlayerVisualProxy", "RemotePlayerTransformSendRateHz", "RemotePlayerVisualTimeoutSeconds",
            "RemotePlayerVisualInterpolationSpeed", "RemotePlayerVisualSnapDistance", "EnableRemotePlayerProxyCollision",
            "RemotePlayerCollisionSoft", "RemotePlayerSoftCollisionRadius", "RemotePlayerSoftCollisionPushSpeed",
            // Batch 4 (NetworkEnemy runtime-spawn + NetworkGameplaySyncExperimental enemy-death mirror):
            "EnableRuntimeSpawnSync", "EnableRuntimeSpawnSnapOnBind", "EnableRuntimeSpawnInertUntilBound",
            "EnableDeathSpawnSync", "EnableMinionSpawnSync",
            "EnableHostEnemyDeathEventMirror", "ApplyReceivedEnemyDeathEvents", "EnemyDeathMirrorPositionTolerance",
            "EnemyDeathMirrorUseHorizontalPositionTolerance", "EnableClientEnemyDeathClaim", "ApplyReceivedClientEnemyDeathClaimsOnHost",
            // Batch 5 (NetworkPlayerLifeExperimental downed/revive — keybind + Log kept):
            "EnableCoopPlayerDownedRevive", "PlayerDownedRescueTimeoutSeconds", "PlayerReviveHoldSeconds",
            "PlayerReviveDistance", "PlayerReviveHealthRatio", "PlayerReviveInvulnerabilitySeconds",
            "PlayerDownedHealthFloor", "RequireReviveDistanceValidationOnHost",
            // Batch 6 (PlayerWeapon — weapon sync + remote body/weapon appearance finalized; 5 Log* kept):
            "EnablePlayerWeaponSync", "PlayerWeaponSyncMaxProjectilesPerShot", "EnableRemoteWeaponModel",
            "EnableRemotePlayerSpriteBody", "EnableRemotePlayerNpcBody", "RemotePlayerBodyUnitKeyword",
            "RemoteBodyScale", "RemoteBodyFeetYOffset", "RemoteWeaponScale", "RemoteWeaponHipHeight",
            "RemoteWeaponForward", "RemoteWeaponRight", "RemoteBodyPitchLimit", "RemoteBodyDepthBias",
            "RemoteNameSize", "RemoteNameHeight",
            // Batch 7 (NetworkEnemyIntentExperimental — 2 Log* kept):
            "EnableClientEnemyIntentDrivenMotion", "EnemyIntentCorrectionDistance", "EnemyIntentHardSnapDistance",
            "EnemyIntentReplayMinIntervalSeconds", "EnableHostAuthorizedIntentExecution", "HostAuthorizedIntentWindowSeconds",
            "EnableClientEnemyNativeDamageSuppression", "EnableClientPuppetAimOverride",
            // Batch 8 (NetworkEnemyStateExperimental — 4 Log* kept):
            "EnableHostEnemyStateSnapshotMirror", "EnemyStateSnapshotSendRateHz", "EnemyStateSnapshotMaxEnemiesPerPacket",
            "OnlySendAliveEnemyStateSnapshots", "ApplyReceivedEnemyStateSnapshots", "EnemyStateSnapshotPositionTolerance",
            "EnemyStateSnapshotInterpolationSpeed", "EnemyStateSnapshotPlaybackDurationMultiplier", "EnemyStateSnapshotSnapDistance",
            "EnemyStateSnapshotApplyRotationY", "EnableClientEnemyAiSuppressionExperiment", "SuppressClientEnemyAiWhenStateMirrorEnabled",
            "EnableClientEnemyPuppetMode", "ClientEnemyPuppetStaleReleaseSeconds", "EnableHostEnemyAnimationMirror",
            "ApplyReceivedEnemyAnimationMirror", "EnemyAnimationMirrorCrossFadeSeconds", "EnemyAnimationMirrorNormalizedTimeTolerance",
            "EnemyAnimationMirrorApplyAnimatorStatePlayback", "EnemyAnimationMirrorApplyHostCombatStatePlayback",
            "EnemyAnimationMirrorReplayHostCombatMethods", "EnemyAnimationMirrorApplyCombatAnimatorFallback",
            "EnemyAnimationMirrorHostCombatActionHoldSeconds", "EnableEnemyStateSnapshotDeltaCompression",
            "EnemyStateSnapshotHeartbeatSeconds", "EnemyStateSnapshotPositionDeltaThreshold",
            "EnemyStateSnapshotRotationDeltaThresholdDegrees", "EnemyStateSnapshotAnimationTimeDeltaThreshold",
            // Batch 9 (NetworkEnemyTargetExperimental — 3 Log* kept):
            "EnemyProjectileVisualMirrorEnabled", "EnemyProjectileVisualMirrorUseNativeShootReplay",
            "EnemyProjectileVisualMirrorSpeed", "EnemyProjectileVisualMirrorLifetime", "EnableGenericHostCombatAnimatorStateMirror",
            "EnableHostAuthoritativeEnemyRangedDamage", "EnableSyntheticRangedDamageFallback", "EnemyHostProjectileHitRadius",
            "EnemyHostProjectileVerticalTolerance", "EnemyHostProjectileMaxDistance", "EnemyHostProjectileDamage",
            "EnemyHostProjectileDamageCooldownSeconds", "EnemyDamageDefaultType", "EnableEnemyElementalStatusEffect",
            "EnemyElementalStatusAmount", "EnableHostOnlyEnemyTargetAuthority", "EnemyTargetAuthorityProbeIntervalSeconds",
            "EnableEnemyCombatProbe",
        };

        private readonly string _jsonPath;

        public Data Values { get; private set; } = new Data();

        public CoopSettingsStore(ConfigFile cfg)
        {
            _jsonPath = DeriveJsonPath(cfg.ConfigFilePath);

            if (!TryLoadJson())
            {
                // First run on a version that has this store: pull whatever the user had in the old .cfg keys so
                // their name / host / key aren't reset to defaults, then write our file.
                MigrateFromCfg(cfg);
                Save();
            }
        }

        private static string DeriveJsonPath(string cfgFilePath)
        {
            string dir = Path.GetDirectoryName(cfgFilePath) ?? ".";
            return Path.Combine(dir, ModInfo.GUID + ".coop.json");
        }

        private bool TryLoadJson()
        {
            try
            {
                if (!File.Exists(_jsonPath)) return false;
                var loaded = JsonUtility.FromJson<Data>(File.ReadAllText(_jsonPath));
                if (loaded != null) Values = loaded;
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log?.Warn($"[CoopSettings] load failed ({e.GetType().Name}), using defaults: {e.Message}");
                return false;
            }
        }

        public void Save()
        {
            try { File.WriteAllText(_jsonPath, JsonUtility.ToJson(Values, prettyPrint: true)); }
            catch (Exception e) { Plugin.Log?.Warn($"[CoopSettings] save failed: {e.Message}"); }
        }

        // ----- Migration / cleanup of the old BepInEx keys ----------------------------------------------------

        private void MigrateFromCfg(ConfigFile cfg)
        {
            var orphans = GetOrphans(cfg);
            if (orphans == null) return;

            if (TryGetOrphan(orphans, "PlayerName", out var s))            Values.playerName    = s;
            if (TryGetOrphan(orphans, "HostAddress", out s))              Values.hostAddress   = s;
            if (TryGetOrphan(orphans, "HostPort", out s) && int.TryParse(s, out var port)) Values.hostPort = port;
            if (TryGetOrphan(orphans, "ConnectionKey", out s))            Values.connectionKey = s;
            if (TryGetOrphan(orphans, "MaxPlayers", out s) && int.TryParse(s, out var mp))  Values.maxPlayers = Mathf.Clamp(mp, 2, 4);
            if (TryGetOrphan(orphans, "RequireSameModVersion", out s) && bool.TryParse(s, out var rq)) Values.requireSameModVersion = rq;
            if (TryGetOrphan(orphans, "EnableCoopToasts", out s) && bool.TryParse(s, out var ct))      Values.enableCoopToasts = ct;
        }

        /// <summary>Physically delete the retired co-op keys from the <c>.cfg</c> (BepInEx keeps unbound keys as
        /// "orphaned entries" and writes them back otherwise). Call once after all binds. No-op when nothing matches.</summary>
        public static void PruneRetiredCfgKeys(ConfigFile cfg)
        {
            try
            {
                var orphans = GetOrphans(cfg);
                if (orphans == null) return;

                var toRemove = new System.Collections.Generic.List<object>();
                foreach (DictionaryEntry e in orphans)
                {
                    if (Array.IndexOf(RetiredCfgKeys, KeyOf(e.Key)) >= 0)
                        toRemove.Add(e.Key);
                }
                if (toRemove.Count == 0) return;

                foreach (var k in toRemove) orphans.Remove(k);
                cfg.Save(); // rewrite the .cfg without the pruned keys
                Plugin.Log?.Info($"[CoopSettings] pruned {toRemove.Count} retired co-op key(s) from the .cfg (moved to {ModInfo.GUID}.coop.json).");
            }
            catch (Exception e)
            {
                Plugin.Log?.Warn($"[CoopSettings] prune failed: {e.Message}");
            }
        }

        private static IDictionary GetOrphans(ConfigFile cfg)
        {
            var pi = typeof(ConfigFile).GetProperty("OrphanedEntries",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return pi?.GetValue(cfg) as IDictionary;
        }

        private static bool TryGetOrphan(IDictionary orphans, string key, out string value)
        {
            foreach (DictionaryEntry e in orphans)
            {
                if (string.Equals(KeyOf(e.Key), key, StringComparison.Ordinal))
                {
                    value = e.Value as string ?? "";
                    return !string.IsNullOrEmpty(value);
                }
            }
            value = null;
            return false;
        }

        /// <summary>Read <c>ConfigDefinition.Key</c> off a boxed definition without a hard type reference.</summary>
        private static string KeyOf(object configDefinition)
            => configDefinition?.GetType().GetProperty("Key")?.GetValue(configDefinition) as string ?? "";
    }
}
