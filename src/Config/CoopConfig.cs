using BepInEx.Configuration;
using UnityEngine;

namespace SULFURTogether.Config
{
    public class CoopConfig
    {
        // ----- Debug / Probe master -----
        public ConfigEntry<bool> EnableDebugLog     { get; }
        public ConfigEntry<bool> EnableReverseProbe { get; }

        // ----- Probe categories -----
        public ConfigEntry<bool> EnablePlayerProbe { get; }
        public ConfigEntry<bool> EnableUnitProbe   { get; }
        public ConfigEntry<bool> EnableNpcProbe    { get; }
        public ConfigEntry<bool> EnableDamageProbe { get; }
        // Phase 5.5 diag: player teleport trigger (TeleportPlayer.DoTeleport) + local-player TeleportTo from/to + full
        // DamageSourceData dump on suppressed puppet hits — to identify the client teleport loop and the real 800 source.
        public ConfigEntry<bool> LogTeleportDiag   { get; }
        public ConfigEntry<bool> EnableLootProbe   { get; }
        public ConfigEntry<bool> EnablePickupProbe { get; }
        public ConfigEntry<bool> EnableLevelProbe  { get; }

        // ----- Noise-reduction probes (all default off) -----
        public ConfigEntry<bool> EnableVerboseUnitSpawnProbe { get; }
        public ConfigEntry<bool> EnableBreakableSpawnProbe   { get; }
        public ConfigEntry<bool> EnableVerboseInventoryProbe { get; }
        public ConfigEntry<bool> EnableNpcMeleeProbe         { get; }

        // AI method probes — each is default off to prevent high-frequency log spam
        public ConfigEntry<bool> EnableAiTargetProbe         { get; }  // AiAgent.GetTarget
        public ConfigEntry<bool> EnableAiUpdateTargetProbe   { get; }  // AiAgent.UpdateTarget
        public ConfigEntry<bool> EnableAiSetDestinationProbe { get; }  // AiAgent.SetDestination
        public ConfigEntry<bool> EnableAiNavMeshStateProbe   { get; }  // AiAgent.SetNavMeshAgentState
        public ConfigEntry<bool> LogAiTargetingReverseDump   { get; }  // P3: one-time dump of AiAgent targeting + player enumeration
        public ConfigEntry<bool> EnableAiCanMoveProbe        { get; }  // AiAgent.SetCanMove
        public ConfigEntry<bool> EnableNpcUpdateProbe        { get; }  // Npc.Update

        // Inventory item probes — all default off (high-frequency in normal gameplay)
        public ConfigEntry<bool> EnableInventorySerializationProbe { get; }  // GetSerialized
        public ConfigEntry<bool> EnableInventoryDestroyProbe       { get; }  // DestroyFromInventory
        public ConfigEntry<bool> EnableInventoryTransferProbe      { get; }  // TransferOwnership + TryMoveToPlayer
        public ConfigEntry<bool> EnableInventoryDropProbe          { get; }  // DropFromPlayer

        // ----- Pickup / Loot compact probes -----
        // Enable flags (true = count + burst; false = skip entirely)
        public ConfigEntry<bool>  EnablePickupSpawnProbe            { get; }
        public ConfigEntry<bool>  EnablePickupExecuteProbe          { get; }
        public ConfigEntry<bool>  EnableLootRegisterProbe           { get; }
        public ConfigEntry<bool>  EnableLootSpawnProbe              { get; }
        // Verbose per-item flags (false = burst summary only)
        public ConfigEntry<bool>  EnableVerbosePickupProbe          { get; }
        public ConfigEntry<bool>  EnableVerboseLootProbe            { get; }
        // Compact burst summary
        public ConfigEntry<bool>  CompactPickupLogs                 { get; }
        public ConfigEntry<bool>  CompactLootLogs                   { get; }
        public ConfigEntry<float> PickupBurstSummaryIntervalSeconds { get; }
        public ConfigEntry<float> LootBurstSummaryIntervalSeconds   { get; }

        // ----- Throttle -----
        public ConfigEntry<float> ProbeThrottleSeconds { get; }
        public ConfigEntry<int>   MaxRepeatedLogPerKey { get; }

        // ----- Probe Summary -----
        public ConfigEntry<bool>  EnableProbeSummary          { get; }
        public ConfigEntry<float> ProbeSummaryIntervalSeconds { get; }
        public ConfigEntry<bool>  SuppressEmptyProbeSummary   { get; }

        // ----- Network -----
        // The host/client ROLE is not persisted (runtime-only, CoopConnection.CurrentMode). The connection
        // settings below are persisted too, but in our own JSON store (CoopSettingsStore) rather than the BepInEx
        // .cfg, so they stay out of external config managers (Gale) and don't duplicate the in-game connect page.
        // They keep the ConfigEntry-style `.Value` surface via Setting<T>, so call sites are unchanged.
        public Setting<string> HostAddress            { get; }
        public Setting<int>    HostPort               { get; }
        public Setting<string> PlayerName             { get; }
        public Setting<int>    MaxPlayers             { get; }
        public Setting<string> ConnectionKey          { get; }
        public Setting<bool>   RequireSameModVersion  { get; }
        public ConfigEntry<float>  SendPingIntervalSeconds { get; }

        /// <summary>Backing JSON store for the co-op settings exposed above and <see cref="EnableCoopToasts"/>.</summary>
        public CoopSettingsStore CoopSettings { get; }

        // ----- Run / Scene state negotiation (metadata only) -----
        public Fixed<bool>         EnableRunStateNegotiation      { get; } // functional: always on (release-hardcoded)
        public Fixed<float>        RunStateBroadcastIntervalSeconds { get; } // functional tuning (release-hardcoded)
        public ConfigEntry<bool>   WarnOnRunStateMismatch         { get; }
        public ConfigEntry<bool>   EnableHostSceneAuthority       { get; }
        public ConfigEntry<bool>   WarnOnClientSceneDrift         { get; }
        public ConfigEntry<bool>   EnableHostSceneRequestProtocol { get; }
        public ConfigEntry<bool>   AutoSendHostSceneRequestOnDrift { get; }
        public ConfigEntry<float>  HostSceneRequestIntervalSeconds { get; }
        public ConfigEntry<bool>   EnableManualClientSceneFollow { get; }
        public ConfigEntry<KeyboardShortcut> ManualClientSceneFollowKey { get; }
        public ConfigEntry<bool>   ManualClientSceneFollowRequiresHostRequest { get; }

        // ----- Phase 3.1 level seed authority ----- (functional: always on, release-hardcoded)
        public Fixed<bool>         EnableLevelSeedAuthority { get; }
        public Fixed<bool>         RequireSameLevelSeedForSceneMatch { get; }
        public Fixed<bool>         ApplyHostLevelSeedOnManualFollow { get; }
        public Fixed<bool>         HideRemoteVisualWhenLevelSeedMismatch { get; }

        // ----- Phase 5.3-I generation-input (used sets) sync -----
        public Fixed<bool>         SyncHostUsedSetsOnManualFollow { get; } // functional: always on (release-hardcoded)
        public ConfigEntry<bool>   LogUsedSetsTrace { get; }

        // ----- Phase 5.3-J client join load gate -----
        public ConfigEntry<bool>   ClientWaitHostGenerationInputBeforeFirstLoad { get; }
        public ConfigEntry<float>  ClientLoadGateTimeoutSeconds { get; }
        public ConfigEntry<bool>   ClientLoadGateAllowFallbackAfterTimeout { get; }
        public ConfigEntry<float>  ClientLoadGateRequestIntervalSeconds { get; }
        // Phase 5.6-DL P3: gate the client's DEATH respawn to the hub so it converges on the HOST's hub seed
        // (same safe-zone instance) instead of generating its own. Timeout falls back to a local hub respawn.
        public ConfigEntry<bool>   ClientGateDeathRespawnUntilHostHub { get; }
        public ConfigEntry<float>  ClientGateDeathRespawnTimeoutSeconds { get; }
        // Phase 5.6-DL-Q2: let the client LEAD level transitions. When the client walks into an exit, relay the
        // target to the host; the host performs the transition authoritatively (host player moves + generates),
        // then the gated client follows. The host still owns generation.
        public ConfigEntry<bool>   EnableClientTransitionRelay { get; }
        // Phase 5.6-CL: "allow client to load level". The in-run extension of the relay above — when a JOINED
        // client walks into a NextLevelTrigger (CompleteLevel, the sub-level advance that never goes through
        // GoToLevel), do NOT generate locally: show a native loading fade, tell the host, and let the host LEAD
        // the transition (host moves + generates) so everyone advances together. Needs EnableClientTransitionRelay
        // (the transport). On timeout the client falls back to advancing locally so it is never stuck.
        public ConfigEntry<bool>   AllowClientInitiatedLevelLoad { get; }
        public ConfigEntry<float>  ClientInitiatedLoadTimeoutSeconds { get; }
        // Phase F3-Reload: when a linked client F3's to the level both ends are ALREADY in (reload-in-place), the
        // client must NOT self-reload off the host's stale "I'm here" request (that diverged — Log147) — it relays
        // and waits for the host to RE-LEAD the reload so both regenerate together.
        public ConfigEntry<bool>   EnableClientReloadInPlaceRelay { get; }

        // Phase 5.6-LK: explicit "联机状态 / Online-Linked state". The master switch for whether the mod's
        // multiplayer behavior is active. CLIENT default OFF — the player presses ManualClientSceneFollowKey
        // (PageDown) to LINK (join + follow the host), and ClientUnlinkKey (PageUp) to UNLINK and go back to
        // playing their own run independently. While linked the client forwards ALL of its own (non-host) map
        // switches to the host so the host leads everyone. HOST default ON (debug-friendly) — when off the host
        // acts single-player (no broadcasts, ignores client relays). HostLinkToggleKey toggles it.
        public ConfigEntry<bool>   ClientLinkedByDefault { get; }
        public ConfigEntry<bool>   HostLinkedByDefault { get; }
        public ConfigEntry<KeyboardShortcut> ClientUnlinkKey { get; }
        public ConfigEntry<KeyboardShortcut> HostLinkToggleKey { get; }

        // ----- Phase 5.4-A client join flow -----
        public ConfigEntry<string> ClientJoinMode { get; }

        // ----- Phase 5.4-E Boss encounter authority -----
        public ConfigEntry<bool>   EnableBossEncounterSync { get; }
        public ConfigEntry<bool>   BossEncounterClientBlockLocalStart { get; }
        public ConfigEntry<bool>   LogBossEncounter { get; }
        // ----- Phase 5.4-E2 BossStart chain completion + lifecycle probe -----
        public ConfigEntry<float>  BossContinuationGraceSeconds { get; }
        public ConfigEntry<bool>   EnableBossLifecycleProbe { get; }
        public ConfigEntry<bool>   LogBossLifecycle { get; }
        // ----- Phase PF-0 boss pre-fight convergence diagnostic -----
        public ConfigEntry<bool>   LogBossPreFight { get; }
        // ----- Fix A (root): remove the boss dialog interactable on fight start (vanilla Witch pattern) -----
        public ConfigEntry<bool>   RemoveBossDialogInteractableOnStart { get; }
        // ----- Phase PF: faithful synced boss intro (run the real behavior-tree intro/dialog on the client) -----
        public ConfigEntry<bool>   EnableFaithfulBossIntro { get; }
        // ----- Phase PF (Plan B): gate the boss fight start on the intro dialog being dismissed (host-authoritative) -----
        public ConfigEntry<bool>   GateBossFightOnDialogClose { get; }
        // ----- Phase PF-ArmDefer (issue 1): defer the Cousin intro arm until the dialog-close fight commit -----
        public ConfigEntry<bool>   DeferBossIntroArm { get; }
        // ----- Phase RM: host-authoritative room-membership substrate (who is in the boss room). Observe-only for now -----
        public ConfigEntry<bool>   EnableBossRoomMembership { get; }
        // ----- Phase RM-2b: scope the synced boss intro cutscene to in-room players (Cousin) -----
        public ConfigEntry<bool>   GateBossDialogToInRoom { get; }
        // ----- Phase RT3-Cousin-arms-Room: don't target out-of-room players with the Cousin arm group attack -----
        public ConfigEntry<bool>   ExcludeOutOfRoomPlayersFromBossAttacks { get; }
        // ----- Phase 5.4-E3 BossDialogCommit + Lucia + Witch state + Emperor worm -----
        public ConfigEntry<bool>   EnableEmperorWormDiagnostics { get; }
        public ConfigEntry<bool>   EnableEmperorClientWormSuppression { get; }
        public ConfigEntry<bool>   LogBossTransitionDiagnostics { get; }
        // ----- Phase 5.4-E4 Boss dynamic spawn manifest -----
        public ConfigEntry<bool>   EnableBossDynamicSpawnManifest { get; }
        public ConfigEntry<bool>   LogBossDynamicSpawn { get; }
        // ----- Phase RT3-Cousin-arms: route GoblinCousinArm through the standard RT3-A boss-add pipeline -----
        public ConfigEntry<bool>   EnableCousinArmSync { get; }
        // ----- Phase 5.4-F BossDamageAuthority -----
        public ConfigEntry<bool>   EnableBossDamageAuthority { get; }
        // ----- Phase 5.4-F2 BossStartPresentation -----
        public ConfigEntry<bool>   EnableBossClientPresentation { get; }
        // ----- Phase 5.4-F4 fixed-point boss discrete-event authority (Cousin pools) -----
        public ConfigEntry<bool>   EnableBossDiscreteEventAuthority { get; }
        // ----- Phase 5.4-F5 Lucia eye defeat authority -----
        public ConfigEntry<bool>   EnableLuciaEyeAuthority { get; }
        // ----- Phase 5.4-F6 Lucia terminal death authority -----
        public ConfigEntry<bool>   EnableLuciaDeathAuthority { get; }
        // ----- Phase 5.4-G Witch visible phase-witch damage authority -----
        public ConfigEntry<bool>   EnableWitchPhaseDamageAuthority { get; }
        // ----- Phase 5.4-G2 Witch phase revision authority -----
        public ConfigEntry<bool>   EnableWitchPhaseAuthority { get; }
        // ----- Phase 5.4-G4 Witch Phase 2 timing probe (diagnostic) -----
        public ConfigEntry<bool>   LogWitchPhase2Probe { get; }
        // ----- Phase 5.4-G5 Witch Phase 2 dome manifest authority -----
        public ConfigEntry<bool>   EnableWitchPhase2Manifest { get; }
        // ----- Phase 5.4-G7 Witch death收尾 (amulet crash + terminal) -----
        public ConfigEntry<bool>   EnableWitchDeathFix { get; }
        // ----- Phase 5.5-RT1 runtime spawn sync ----- (functional: always on, release-hardcoded)
        public Fixed<bool>         EnableRuntimeSpawnSync { get; }
        public ConfigEntry<bool>   LogRuntimeSpawnSync { get; }
        // ----- Phase 5.5-RT3-A bind correction (snap-on-bind + inert + hit-gate) -----
        public Fixed<bool>         EnableRuntimeSpawnSnapOnBind { get; }
        public Fixed<bool>         EnableRuntimeSpawnInertUntilBound { get; }
        // ----- Phase 5.7-DS death-spawn ("spawn random enemy on death" mutation) host-authoritative sync -----
        public Fixed<bool>         EnableDeathSpawnSync { get; }
        // ----- Phase 5.7-DS2 minion-spawn (spawnMinionsOnDeath mutation) host-authoritative sync -----
        public Fixed<bool>         EnableMinionSpawnSync { get; }

        // ----- Phase 5.6-WS player weapon bullet sync (visual-only barrage replay) -----
        public Fixed<bool>         EnablePlayerWeaponSync { get; }   // functional: always on (release-hardcoded)
        public ConfigEntry<bool>   LogPlayerWeaponSync { get; }
        public Fixed<int>          PlayerWeaponSyncMaxProjectilesPerShot { get; } // safety clamp (release-hardcoded)

        // ----- Phase 5.7-BR in-scene destructible (Breakable) sync -----
        public Fixed<bool>         EnableBreakableSync { get; }   // functional: always on (release-hardcoded)
        public ConfigEntry<bool>   LogBreakableSync { get; }
        // ----- Phase LD-1 generic combat-room gate (MetalGate) open/close sync -----
        public Fixed<bool>         EnableGateSync { get; }        // functional: always on (release-hardcoded)
        public ConfigEntry<bool>   LogGateSync { get; }
        // ----- Phase LD-1b combat-room door sync, GameObject.SetActive variant (Lucia etc.) -----
        public Fixed<bool>         EnableTriggerDoorSync { get; } // functional: always on (release-hardcoded)
        public ConfigEntry<bool>   LogTriggerDoorSync { get; }
        // ----- Phase LD-2 FF14-style arena lockdown (host-authoritative membership + timer + force-seal barrier + teleport) -----
        public ConfigEntry<bool>   EnableArenaLockdown { get; }
        public ConfigEntry<bool>   LogArenaLockdown { get; }
        public ConfigEntry<KeyboardShortcut> ArenaEnterConfirmKey { get; }
        public ConfigEntry<bool>   EnableArenaGracePeriod { get; }

        // ----- In-game co-op UI (toasts / status) via SULFUR Native UI Lib (soft dependency) -----
        public Setting<bool>   EnableCoopToasts { get; }

        // ----- World item-drop sync (player-thrown items first; forward-compatible with a Shared-loot toggle) -----
        public ConfigEntry<bool>   EnableWorldItemDropSync { get; }
        public ConfigEntry<bool>   LogWorldItemDropSync { get; }
        public ConfigEntry<bool>   ShareAllLoot { get; }

        // ----- Phase 5.6-WS-2 remote held weapon model (with attachments) -----
        public Fixed<bool>         EnableRemoteWeaponModel { get; } // functional: always on (release-hardcoded)
        public ConfigEntry<bool>   LogRemoteWeaponModel { get; }

        // ----- Phase 5.6-WS-3 player visual (billboard sprite) discovery probe -----
        public ConfigEntry<bool>   LogPlayerVisualDiscovery { get; }
        public ConfigEntry<bool>   LogPlayerSpriteAssetScan { get; }

        // ----- Phase 5.6-WS-3 remote player billboard body ----- (functional + appearance finalized, release-hardcoded)
        // Priest (Father) sprite body (embedded front/back walk sheets) takes priority; NPC-prefab body is the fallback.
        public Fixed<bool>         EnableRemotePlayerSpriteBody { get; }
        public Fixed<bool>         EnableRemotePlayerNpcBody { get; }
        public Fixed<string>       RemotePlayerBodyUnitKeyword { get; }
        public ConfigEntry<bool>   LogRemotePlayerBody { get; }
        public Fixed<float>        RemoteBodyScale { get; }
        public Fixed<float>        RemoteBodyFeetYOffset { get; }
        public Fixed<float>        RemoteWeaponScale { get; }
        public Fixed<float>        RemoteWeaponHipHeight { get; }
        public Fixed<float>        RemoteWeaponForward { get; }
        public Fixed<float>        RemoteWeaponRight { get; }
        public Fixed<float>        RemoteBodyPitchLimit { get; }
        public Fixed<float>        RemoteBodyDepthBias { get; }
        public Fixed<float>        RemoteNameSize { get; }
        public Fixed<float>        RemoteNameHeight { get; }

        // ----- Phase 5.3-M P1 auto-follow + load barrier -----
        public ConfigEntry<bool>   EnableAutoFollowHostSceneRequest { get; }
        public ConfigEntry<bool>   EnableLoadBarrier { get; }
        public ConfigEntry<float>  LoadBarrierTimeoutSeconds { get; }
        public ConfigEntry<bool>   LoadBarrierBlockHostAdvance { get; }
        public ConfigEntry<bool>   LoadBarrierLogOnlyMode { get; }

        // ----- Phase 3.0 remote visual proxy only ----- (functional + tuning: release-hardcoded)
        public Fixed<bool>         EnableRemotePlayerVisualProxy { get; }
        public Fixed<float>        RemotePlayerTransformSendRateHz { get; }
        public Fixed<float>        RemotePlayerVisualTimeoutSeconds { get; }
        public Fixed<float>        RemotePlayerVisualInterpolationSpeed { get; }
        public Fixed<float>        RemotePlayerVisualSnapDistance { get; }
        public Fixed<bool>         EnableRemotePlayerProxyCollision { get; }
        public Fixed<bool>         RemotePlayerCollisionSoft { get; }
        public Fixed<float>        RemotePlayerSoftCollisionRadius { get; }
        public Fixed<float>        RemotePlayerSoftCollisionPushSpeed { get; }

        // ----- Phase 4.0 gameplay entity probe only -----
        public ConfigEntry<bool>   EnableGameplayEntityProbe { get; }
        public ConfigEntry<float>  GameplayEntityProbeSummaryIntervalSeconds { get; }
        public ConfigEntry<bool>   LogGameplayEntitySpawn { get; }
        public ConfigEntry<bool>   LogGameplayEntityDamage { get; }
        public ConfigEntry<bool>   LogGameplayEntityDeath { get; }
        public ConfigEntry<bool>   RequireStableSceneAndSeedForGameplayProbe { get; }

        // ----- Phase 4.0-B host enemy death event mirror experiment ----- (functional: always on, release-hardcoded; Log* kept)
        public Fixed<bool>         EnableHostEnemyDeathEventMirror { get; }
        public ConfigEntry<bool>   LogReceivedEnemyDeathEvents { get; }
        public Fixed<bool>         ApplyReceivedEnemyDeathEvents { get; }
        public Fixed<float>        EnemyDeathMirrorPositionTolerance { get; }
        public Fixed<bool>         EnemyDeathMirrorUseHorizontalPositionTolerance { get; }
        public Fixed<bool>         EnableClientEnemyDeathClaim { get; }
        public ConfigEntry<bool>   LogReceivedClientEnemyDeathClaims { get; }
        public Fixed<bool>         ApplyReceivedClientEnemyDeathClaimsOnHost { get; }

        // ----- Phase 4.3-A co-op player downed / revive experiment ----- (functional + tuning hardcoded; Log + keybind kept)
        public Fixed<bool>         EnableCoopPlayerDownedRevive { get; }
        public ConfigEntry<bool>   LogPlayerLifeSync { get; }
        public Fixed<float>        PlayerDownedRescueTimeoutSeconds { get; }
        public Fixed<float>        PlayerReviveHoldSeconds { get; }
        public Fixed<float>        PlayerReviveDistance { get; }
        public Fixed<float>        PlayerReviveHealthRatio { get; }
        public Fixed<float>        PlayerReviveInvulnerabilitySeconds { get; }
        public Fixed<float>        PlayerDownedHealthFloor { get; }
        public ConfigEntry<KeyboardShortcut> PlayerReviveHoldKey { get; }
        public Fixed<bool>         RequireReviveDistanceValidationOnHost { get; }

        // ----- Phase 4.1-A host enemy state snapshot mirror experiment ----- (functional + tuning hardcoded; Log* kept)
        public Fixed<bool>         EnableHostEnemyStateSnapshotMirror { get; }
        public Fixed<float>        EnemyStateSnapshotSendRateHz { get; }
        public Fixed<int>          EnemyStateSnapshotMaxEnemiesPerPacket { get; }
        public Fixed<bool>         OnlySendAliveEnemyStateSnapshots { get; }
        public ConfigEntry<bool>   LogReceivedEnemyStateSnapshots { get; }
        public Fixed<bool>         ApplyReceivedEnemyStateSnapshots { get; }
        public Fixed<float>        EnemyStateSnapshotPositionTolerance { get; }
        public Fixed<float>        EnemyStateSnapshotInterpolationSpeed { get; }
        public Fixed<float>        EnemyStateSnapshotPlaybackDurationMultiplier { get; }
        public Fixed<float>        EnemyStateSnapshotSnapDistance { get; }
        public Fixed<bool>         EnemyStateSnapshotApplyRotationY { get; }
        public Fixed<bool>         EnableClientEnemyAiSuppressionExperiment { get; }
        public Fixed<bool>         SuppressClientEnemyAiWhenStateMirrorEnabled { get; }
        public ConfigEntry<bool>   LogSuppressedClientEnemyAi { get; }
        public Fixed<bool>         EnableClientEnemyPuppetMode { get; }
        public ConfigEntry<bool>   LogClientEnemyPuppetMode { get; }
        public Fixed<float>        ClientEnemyPuppetStaleReleaseSeconds { get; }
        public Fixed<bool>         EnableHostEnemyAnimationMirror { get; }
        public Fixed<bool>         ApplyReceivedEnemyAnimationMirror { get; }
        public ConfigEntry<bool>   LogEnemyAnimationMirror { get; }
        public Fixed<float>        EnemyAnimationMirrorCrossFadeSeconds { get; }
        public Fixed<float>        EnemyAnimationMirrorNormalizedTimeTolerance { get; }
        public Fixed<bool>         EnemyAnimationMirrorApplyAnimatorStatePlayback { get; }
        public Fixed<bool>         EnemyAnimationMirrorApplyHostCombatStatePlayback { get; }
        public Fixed<bool>         EnemyAnimationMirrorReplayHostCombatMethods { get; }
        public Fixed<bool>         EnemyAnimationMirrorApplyCombatAnimatorFallback { get; }
        public Fixed<float>        EnemyAnimationMirrorHostCombatActionHoldSeconds { get; }
        public Fixed<bool>         EnemyProjectileVisualMirrorEnabled { get; }   // hardcoded (effective OFF)
        public Fixed<bool>         EnemyProjectileVisualMirrorUseNativeShootReplay { get; }
        public Fixed<float>        EnemyProjectileVisualMirrorSpeed { get; }
        public Fixed<float>        EnemyProjectileVisualMirrorLifetime { get; }
        public Fixed<bool>         EnableGenericHostCombatAnimatorStateMirror { get; }
        public Fixed<bool>         EnableHostAuthoritativeEnemyRangedDamage { get; }  // hardcoded (effective OFF)
        public Fixed<bool>         EnableSyntheticRangedDamageFallback { get; }       // hardcoded (effective OFF)
        public Fixed<bool>         EnableClientEnemyIntentDrivenMotion { get; } // rolled-back experiment: hardcoded OFF
        public ConfigEntry<bool>   LogEnemyAiIntentMirror { get; }
        public Fixed<float>        EnemyIntentCorrectionDistance { get; }
        public Fixed<float>        EnemyIntentHardSnapDistance { get; }
        public Fixed<float>        EnemyIntentReplayMinIntervalSeconds { get; }
        public Fixed<float>        EnemyHostProjectileHitRadius { get; }
        public Fixed<float>        EnemyHostProjectileVerticalTolerance { get; }
        public Fixed<float>        EnemyHostProjectileMaxDistance { get; }
        public Fixed<float>        EnemyHostProjectileDamage { get; }
        public Fixed<float>        EnemyHostProjectileDamageCooldownSeconds { get; }
        public Fixed<int>          EnemyDamageDefaultType { get; }
        public Fixed<bool>         EnableEnemyElementalStatusEffect { get; }
        public Fixed<float>        EnemyElementalStatusAmount { get; }
        public ConfigEntry<bool>   LogEnemyHostDamageAuthority { get; }

        // ----- Phase 4.4.0-H host-authoritative enemy target/combat probe ----- (functional hardcoded; Log* kept)
        public Fixed<bool>         EnableHostOnlyEnemyTargetAuthority { get; }
        public ConfigEntry<bool>   LogEnemyTargetAuthority { get; }
        public Fixed<float>        EnemyTargetAuthorityProbeIntervalSeconds { get; }
        public Fixed<bool>         EnableEnemyCombatProbe { get; }    // functional: puppet-combat blocking
        public ConfigEntry<bool>   LogEnemyCombatProbe { get; }

        // ----- Phase 4.4.0-O host-authorized enemy intent execution ----- (functional + tuning hardcoded; Log* kept)
        public Fixed<bool>        EnableHostAuthorizedIntentExecution { get; }
        public Fixed<float>       HostAuthorizedIntentWindowSeconds { get; }
        public ConfigEntry<bool>  LogHostAuthorizedIntentExecution { get; }
        public Fixed<bool>        EnableClientEnemyNativeDamageSuppression { get; }
        public Fixed<bool>        EnableClientPuppetAimOverride { get; }

        public Fixed<bool>         EnableEnemyStateSnapshotDeltaCompression { get; }
        public Fixed<float>        EnemyStateSnapshotHeartbeatSeconds { get; }
        public Fixed<float>        EnemyStateSnapshotPositionDeltaThreshold { get; }
        public Fixed<float>        EnemyStateSnapshotRotationDeltaThresholdDegrees { get; }
        public Fixed<float>        EnemyStateSnapshotAnimationTimeDeltaThreshold { get; }

        // ----- Phase 5.1 Host-authoritative enemy health sync ----- (functional hardcoded; Log* kept)
        public Fixed<bool>         EnableHostEnemyDamageEventSync { get; }
        public Fixed<bool>         EnableHostEnemyHealthStateSync { get; }
        public Fixed<bool>         ApplyReceivedHostEnemyHealthState { get; }
        public ConfigEntry<bool>   LogHostEnemyDamageEvents { get; }
        public ConfigEntry<bool>   LogHostEnemyHealthState { get; }
        public Fixed<bool>         AllowRosterBoundDeathDespitePositionDrift { get; }
        public Fixed<bool>         HostDeathSnapBeforeApply { get; }
        public Fixed<bool>         AllowDeathLateRebind { get; }
        public Fixed<bool>         DisableClientEnemyDeathClaimWhenHostDamageSyncEnabled { get; }

        // ----- Phase 5.3-B Client → Host gameplay request pipeline ----- (functional + tuning hardcoded; Log* kept)
        public Fixed<bool>         EnableClientHitRequest  { get; }
        public ConfigEntry<bool>   LogClientHitRequests    { get; }
        public Fixed<bool>         FilterNonPlayerPuppetDamage { get; }      // RT3-A2: only forward local-player damage
        public Fixed<bool>         MakeClientPuppetsKinematic { get; }       // RT3-A3: kinematic host-driven puppets
        public Fixed<bool>         StableWorldRosterBinding { get; }         // RT3-A7: stable roster binding
        public Fixed<bool>         ReleasePuppetOnHostDeath { get; }         // SC3: release puppet on host death
        public Fixed<bool>         EvictStaleHostBindings { get; }           // DB: strict 1:1 binding maps
        public Fixed<bool>         SkipDeadHostIdxRebind { get; }            // DB2: never rebind a buried host idx
        public Fixed<bool>         EnableRetroactiveEnemyBinding { get; }    // RB: retro-bind late-spawned host enemies
        public Fixed<bool>         EnableDestroyedUnitListSweep  { get; }    // RB: sweep destroyed units pre-raycast
        public Fixed<float>        ClientHitRequestMaxRangeMeters  { get; }
        public Fixed<float>        ClientHitRequestRateLimitSeconds { get; }

        // ----- Phase 5.3-C/D Terminal dead latch + visual hit flash ----- (functional + tuning hardcoded; Log* kept)
        public Fixed<bool>         EnableClientTerminalDeadLatch { get; }
        public ConfigEntry<bool>   LogClientTerminalDead         { get; }
        public Fixed<bool>         EnableClientHitFlash          { get; }
        public ConfigEntry<bool>   LogClientHitFlash             { get; }
        public Fixed<float>        ClientHitFlashDurationSeconds { get; }
        // ----- Phase 5.3-D two-phase death (PendingDead → TerminalDead) -----
        public Fixed<bool>         EnableClientPendingDeadState        { get; }
        public Fixed<bool>         EnableClientDeathVisualFallback     { get; }
        public Fixed<float>        ClientDeathVisualFallbackDelaySeconds { get; }
        public ConfigEntry<bool>   LogClientPendingDead                { get; }

        // ----- Phase 5.3-E Host-authoritative level manifest -----
        public ConfigEntry<bool>   EnableHostLevelManifest             { get; }
        public ConfigEntry<bool>   LogLevelManifest                    { get; }
        public ConfigEntry<bool>   LogLevelManifestDiff                { get; }
        public ConfigEntry<bool>   QuarantineClientOnlyManifestEnemies { get; }

        // ----- Phase 5.3-F ClientHit visual + LevelGeneration trace -----
        public Fixed<bool>         EnableClientHitVisual               { get; } // functional: always on (release-hardcoded)
        public ConfigEntry<bool>   EnableLevelGenTrace                 { get; }
        public ConfigEntry<bool>   LogLevelGenTrace                    { get; }

        // ----- Phase 5.0 Host-Driven Proxy Architecture ----- (functional/tuning hardcoded; Log*/Debug diagnostics kept)
        public Fixed<bool>         EnableHostDrivenEnemyProxy { get; }
        public Fixed<bool>         SuppressAllClientPuppetDamage { get; }
        public ConfigEntry<bool>   LogClientPuppetDamageSuppression { get; }
        public Fixed<bool>         EnableHostAttackPhaseEvents { get; }
        public ConfigEntry<bool>   LogHostAttackPhaseEvents { get; }
        public Fixed<bool>         EnableClientAttackPhaseAnimatorDrive { get; }
        public Fixed<float>        ClientAttackPhaseCrossFadeSeconds { get; }
        public Fixed<bool>         EnableHostProjectileVisualSpawnEvent { get; } // P2: retired/off
        public ConfigEntry<bool>   LogHostProjectileVisualSpawn { get; }
        public Fixed<bool>         EnableEnemyInterestManagement { get; }
        public Fixed<bool>         IncludeRemotePlayersInInterest { get; }
        public Fixed<bool>         FullRateForEngagedEnemies { get; }
        public Fixed<float>        ClientEngagedEnemyFullRateSeconds { get; }
        public Fixed<bool>         ThrottleOnlyWithKnownRemotePositions { get; }
        public ConfigEntry<bool>   LogEnemyInterestDiag { get; }
        public Fixed<bool>         SendAllEnemySnapshotsToClients { get; }
        public Fixed<bool>         DisablePauseInMultiplayer { get; }
        public ConfigEntry<bool>   LogPauseSuppression { get; }
        public ConfigEntry<bool>   LogDamageApplyHitch { get; }
        public ConfigEntry<float>  DamageApplyHitchThresholdMs { get; }
        // Phase 5.5-P3-A2 remote-player target proxy — RETIRED (Plan B ghost registry supersedes); hardcoded off/effective.
        public Fixed<bool>         EnableRemotePlayerTargetProxy { get; }
        public ConfigEntry<bool>   LogRemotePlayerTargetProxy { get; }
        public Fixed<bool>         RemotePlayerTargetProxySetIsPlayer { get; }
        public Fixed<bool>         RemotePlayerTargetProxyForceAggro { get; }
        public Fixed<float>        RemotePlayerTargetProxyAggroRange { get; }
        public Fixed<bool>         RemotePlayerTargetProxyOnlyWhenCloser { get; }
        public Fixed<int>          RemotePlayerTargetProxyHitboxLayer { get; }
        public Fixed<bool>         RemotePlayerTargetProxyBodyBlocker { get; }
        public Fixed<bool>         RemoveTargetProxyWhenPeerDowned { get; }
        public Fixed<bool>         HideDownedLocalPlayerFromEnemies { get; }
        public Fixed<bool>         BalanceCoopEnemyTargeting { get; }
        public ConfigEntry<bool>   LogPooledObjectDestroyDiag { get; }
        public Fixed<bool>         ApplyHostPlayerDamageViaReceiveDamage { get; }
        public Fixed<float>        EnemyNearCombatDistance { get; }
        public Fixed<float>        EnemyFarDistance { get; }
        public Fixed<float>        EnemyFarSnapshotHz { get; }
        public Fixed<bool>         EnableCombatEventCoalescing { get; }
        public Fixed<float>        EnemyToClientDamageCoalesceSeconds { get; }
        public Fixed<float>        EnemyDamageEventMinIntervalSeconds { get; }
        public Fixed<float>        AttackPhaseEventMinIntervalSeconds { get; }
        // Plan B: multiplayer NPC activation + headless Player registry (see Docs/EnemyActivationAndPlayersRegistry.md).
        public ConfigEntry<bool>   EnableMultiPlayerNpcActivation { get; }
        public ConfigEntry<float>  MultiPlayerNpcActivationDistance { get; }
        public ConfigEntry<int>    MultiPlayerNpcActivationsPerFrame { get; }
        public ConfigEntry<bool>   EnableRemotePlayerInPlayersList { get; }
        public ConfigEntry<bool>   EnableGhostPlayerHitbox { get; }
        public ConfigEntry<bool>   LogRemotePlayerRegistry { get; }
        // ----- Ghost-during-load freeze fix (LevelGeneration.ShowLevelNode NRE) -----
        public ConfigEntry<bool>   SuppressGhostsWhileLoading { get; }

        public CoopConfig(ConfigFile cfg)
        {
            // Co-op settings live in our own JSON store, not the .cfg (kept out of Gale; see CoopSettingsStore).
            // Construct it first so its first-run migration can still read the old .cfg keys before we prune them.
            CoopSettings = new CoopSettingsStore(cfg);
            var store = CoopSettings;
            PlayerName            = new Setting<string>(() => store.Values.playerName,            v => { store.Values.playerName = v; store.Save(); });
            HostAddress           = new Setting<string>(() => store.Values.hostAddress,           v => { store.Values.hostAddress = v; store.Save(); });
            HostPort              = new Setting<int>   (() => store.Values.hostPort,              v => { store.Values.hostPort = v; store.Save(); });
            ConnectionKey         = new Setting<string>(() => store.Values.connectionKey,         v => { store.Values.connectionKey = v ?? ""; store.Save(); });
            MaxPlayers            = new Setting<int>   (() => store.Values.maxPlayers,            v => { store.Values.maxPlayers = Mathf.Clamp(v, 2, 4); store.Save(); });
            RequireSameModVersion = new Setting<bool>  (() => store.Values.requireSameModVersion, v => { store.Values.requireSameModVersion = v; store.Save(); });
            EnableCoopToasts      = new Setting<bool>  (() => store.Values.enableCoopToasts,      v => { store.Values.enableCoopToasts = v; store.Save(); });

            // master
            EnableDebugLog     = cfg.Bind("Debug", "EnableDebugLog",     false, "Verbose debug output.");
            EnableReverseProbe = cfg.Bind("Debug", "EnableReverseProbe", true,  "Master switch for all reverse probes.");

            // probe categories
            EnablePlayerProbe = cfg.Bind("Probe", "EnablePlayerProbe", true, "Log GameManager player/state events.");
            EnableUnitProbe   = cfg.Bind("Probe", "EnableUnitProbe",   true, "Log Unit / UnitManager lifecycle.");
            EnableNpcProbe    = cfg.Bind("Probe", "EnableNpcProbe",    true, "Log Npc events.");
            EnableDamageProbe = cfg.Bind("Probe", "EnableDamageProbe", true, "Log ReceiveDamage calls.");
            LogTeleportDiag   = cfg.Bind("Probe", "LogTeleportDiag",   false, "Diag (default OFF — high volume; also drives the per-enemy [PosDiag] line): TeleportPlayer.DoTeleport trigger + stack, local-player TeleportTo from/to, full DamageSourceData on suppressed puppet hits. Enable to debug teleport/position.");
            EnableLootProbe   = cfg.Bind("Probe", "EnableLootProbe",   true, "Log LootManager and InventoryItem events.");
            EnablePickupProbe = cfg.Bind("Probe", "EnablePickupProbe", true, "Log InteractionManager pickup events.");
            EnableLevelProbe  = cfg.Bind("Probe", "EnableLevelProbe",  true, "Log level generation and transition events.");

            // noise-reduction (all default off)
            EnableVerboseUnitSpawnProbe = cfg.Bind("ProbeNoise", "EnableVerboseUnitSpawnProbe", false,
                "Log every Unit.Spawn (default: Player/Npc only).");
            EnableBreakableSpawnProbe   = cfg.Bind("ProbeNoise", "EnableBreakableSpawnProbe", false,
                "Log Breakable unit spawns (very noisy).");
            EnableVerboseInventoryProbe = cfg.Bind("ProbeNoise", "EnableVerboseInventoryProbe", false,
                "Log every InventoryItem.Setup individually (default: summary only).");
            EnableNpcMeleeProbe         = cfg.Bind("ProbeNoise", "EnableNpcMeleeProbe", false,
                "Log Npc.HandleMeleeHit (very noisy in merchant areas).");

            // AI method probes (all default off — high-frequency spam risk)
            EnableAiTargetProbe         = cfg.Bind("ProbeNoise", "EnableAiTargetProbe", false,
                "Log AiAgent.GetTarget (fires only on target change).");
            EnableAiUpdateTargetProbe   = cfg.Bind("ProbeNoise", "EnableAiUpdateTargetProbe", false,
                "Log AiAgent.UpdateTarget per-agent (throttled per instance).");
            LogAiTargetingReverseDump   = cfg.Bind("ProbeNoise", "LogAiTargetingReverseDump", true,
                "P3 (remote-player targeting): one-time reflection dump of AiAgent targeting fields/methods + GameManager player enumeration + Unit faction members, to ground the targetable-proxy design.");
            EnableAiSetDestinationProbe = cfg.Bind("ProbeNoise", "EnableAiSetDestinationProbe", false,
                "Log AiAgent.SetDestination per-agent (throttled per instance).");
            EnableAiNavMeshStateProbe   = cfg.Bind("ProbeNoise", "EnableAiNavMeshStateProbe", false,
                "Log AiAgent.SetNavMeshAgentState.");
            EnableAiCanMoveProbe        = cfg.Bind("ProbeNoise", "EnableAiCanMoveProbe", false,
                "Log AiAgent.SetCanMove.");
            EnableNpcUpdateProbe        = cfg.Bind("ProbeNoise", "EnableNpcUpdateProbe", false,
                "Log Npc.Update per-NPC (throttled, extremely noisy).");

            // inventory item probes (all default off — high-frequency in normal gameplay)
            EnableInventorySerializationProbe = cfg.Bind("ProbeNoise", "EnableInventorySerializationProbe", false,
                "Log InventoryItem.GetSerialized (fires on every save/serialize — very noisy).");
            EnableInventoryDestroyProbe       = cfg.Bind("ProbeNoise", "EnableInventoryDestroyProbe", false,
                "Log InventoryItem.DestroyFromInventory calls.");
            EnableInventoryTransferProbe      = cfg.Bind("ProbeNoise", "EnableInventoryTransferProbe", false,
                "Log InventoryItem.TransferOwnership and TryMoveToPlayerInventory.");
            EnableInventoryDropProbe          = cfg.Bind("ProbeNoise", "EnableInventoryDropProbe", false,
                "Log InventoryItem.DropFromPlayer.");

            // pickup / loot probe enable flags (true = count + burst active; false = skip entirely)
            EnablePickupSpawnProbe   = cfg.Bind("Probe", "EnablePickupSpawnProbe", true,
                "Count and burst-summarize InteractionManager.SpawnPickup / RemovePickup events.");
            EnablePickupExecuteProbe = cfg.Bind("Probe", "EnablePickupExecuteProbe", true,
                "Count and burst-summarize InteractionManager.ExecutePickup events.");
            EnableLootRegisterProbe  = cfg.Bind("Probe", "EnableLootRegisterProbe", true,
                "Count and burst-summarize LootManager.RegisterLootDropped events.");
            EnableLootSpawnProbe     = cfg.Bind("Probe", "EnableLootSpawnProbe", true,
                "Count and burst-summarize LootManager.SpawnGlobalLoot / SpawnLootFrom events.");

            // verbose per-item flags (false = burst summary only; true = log every item)
            EnableVerbosePickupProbe = cfg.Bind("ProbeNoise", "EnableVerbosePickupProbe", false,
                "Log every pickup spawn/execute/remove individually. Very noisy during loot explosions.");
            EnableVerboseLootProbe   = cfg.Bind("ProbeNoise", "EnableVerboseLootProbe", false,
                "Log every loot drop/spawn individually.");

            // compact burst settings
            CompactPickupLogs = cfg.Bind("ProbeSummary", "CompactPickupLogs", true,
                "Replace per-pickup log lines with a burst summary every PickupBurstSummaryIntervalSeconds.");
            CompactLootLogs   = cfg.Bind("ProbeSummary", "CompactLootLogs", true,
                "Replace per-loot log lines with a burst summary every LootBurstSummaryIntervalSeconds.");
            PickupBurstSummaryIntervalSeconds = cfg.Bind("ProbeSummary", "PickupBurstSummaryIntervalSeconds", 5f,
                "How often (seconds) to flush the pickup burst summary.");
            LootBurstSummaryIntervalSeconds   = cfg.Bind("ProbeSummary", "LootBurstSummaryIntervalSeconds", 5f,
                "How often (seconds) to flush the loot burst summary.");

            // throttle
            ProbeThrottleSeconds = cfg.Bind("ProbeNoise", "ProbeThrottleSeconds", 10f,
                "Minimum seconds between repeated logs for the same throttle key.");
            MaxRepeatedLogPerKey = cfg.Bind("ProbeNoise", "MaxRepeatedLogPerKey", 3,
                "Max times a key with maxPerWindow can log within one throttle window.");

            // probe summary
            EnableProbeSummary          = cfg.Bind("ProbeSummary", "EnableProbeSummary", true,
                "Emit a periodic counter summary to the log.");
            ProbeSummaryIntervalSeconds = cfg.Bind("ProbeSummary", "ProbeSummaryIntervalSeconds", 30f,
                "How often (seconds) to flush the probe summary.");
            SuppressEmptyProbeSummary   = cfg.Bind("ProbeSummary", "SuppressEmptyProbeSummary", true,
                "Skip the summary output if all delta counters are zero.");

            // network — role is runtime-only; the connection settings (PlayerName/HostAddress/HostPort/ConnectionKey/
            // MaxPlayers/RequireSameModVersion) moved to CoopSettings (JSON store), bound above. Only the ping tuning
            // knob stays a standard .cfg entry.
            SendPingIntervalSeconds = cfg.Bind("Network", "SendPingIntervalSeconds", 2f,
                "How often (seconds) to send a Ping to peers.");

            // in-game co-op UI toggle (EnableCoopToasts) moved to CoopSettings (JSON store), bound above — it's a
            // connect-page preference, so it stays out of the .cfg / Gale like the other co-op settings.

            // run / scene metadata only. This never loads levels or synchronizes gameplay.
            EnableRunStateNegotiation = new Fixed<bool>(true);          // functional: run-state metadata always exchanged.
            RunStateBroadcastIntervalSeconds = new Fixed<float>(2f);    // re-send interval (seconds) — tuned value.
            WarnOnRunStateMismatch = cfg.Bind("NetworkRunState", "WarnOnRunStateMismatch", true,
                "Log a warning when local and remote chapter/level metadata differ. Loading vs Running on the same scene is ignored.");

            // host scene authority skeleton. Warning/metadata only; it never calls GoToLevel or changes gameplay.
            EnableHostSceneAuthority = cfg.Bind("NetworkSceneAuthority", "EnableHostSceneAuthority", true,
                "Treat the Host run state as the authoritative scene metadata. Warning-only; never auto-loads scenes.");
            WarnOnClientSceneDrift = cfg.Bind("NetworkSceneAuthority", "WarnOnClientSceneDrift", true,
                "Warn when a Client is not in the Host scene. Warning-only; no correction is performed.");
            EnableHostSceneRequestProtocol = cfg.Bind("NetworkSceneAuthority", "EnableHostSceneRequestProtocol", true,
                "Enable Phase 2.5 HostSceneRequest / ClientSceneAck protocol skeleton. It never auto-loads scenes.");
            AutoSendHostSceneRequestOnDrift = cfg.Bind("NetworkSceneAuthority", "AutoSendHostSceneRequestOnDrift", true,
                "When Host sees a Client in a different scene, send HostSceneRequest metadata. Client only replies; it does not auto-load.");
            HostSceneRequestIntervalSeconds = cfg.Bind("NetworkSceneAuthority", "HostSceneRequestIntervalSeconds", 10f,
                "Minimum seconds before repeating the same HostSceneRequest to the same Client.");

            // manual scene follow. Only runs when the local Client user presses the configured key.
            EnableManualClientSceneFollow = cfg.Bind("NetworkSceneAuthority", "EnableManualClientSceneFollow", true,
                "Allow Client to manually attempt following the latest HostSceneRequest by pressing ManualClientSceneFollowKey. Never automatic.");
            ManualClientSceneFollowKey = cfg.Bind("NetworkSceneAuthority", "ManualClientSceneFollowKey", new KeyboardShortcut(KeyCode.PageDown),
                "Client-only manual follow key. Press this after receiving HostSceneRequest to attempt local GoToLevel. Avoid F1-F12 because SULFUR's DevTools/F-key bindings may toggle invulnerability or other debug states.");
            ManualClientSceneFollowRequiresHostRequest = cfg.Bind("NetworkSceneAuthority", "ManualClientSceneFollowRequiresHostRequest", true,
                "Only allow manual follow when a HostSceneRequest has been received.");

            // Phase 3.1 level seed authority — functional: scene equality is always seed-strict (release-hardcoded).
            EnableLevelSeedAuthority = new Fixed<bool>(true);
            RequireSameLevelSeedForSceneMatch = new Fixed<bool>(true);
            ApplyHostLevelSeedOnManualFollow = new Fixed<bool>(true);
            HideRemoteVisualWhenLevelSeedMismatch = new Fixed<bool>(true);

            // Phase 5.3-I: the level generator's deterministic inputs are not just the seed. They also
            // include GameManager's cross-level exclusion sets (usedChunksThisRun, usedUniqueEventThisRun,
            // usedUniqueEventThisEnvironment). The Host sends them in HostSceneRequest; the Client overwrites
            // its local sets before manual follow so generation candidate pools match.
            SyncHostUsedSetsOnManualFollow = new Fixed<bool>(true); // functional: always sync host used-sets before follow.
            LogUsedSetsTrace = cfg.Bind("NetworkLevelSeed", "LogUsedSetsTrace", true,
                "Log [UsedSetsTrace]/[FollowPrep] details (GameManager used sets on level entry, and before/after applying Host used sets on follow).");

            // Phase 5.3-J: the Client load gate intercepts the first local GoToLevel so the Client does not
            // generate a wrong-seed local level before following the Host. It waits for the Host's
            // GenerationInput (HostSceneRequest), then performs a single host-driven GoToLevel.
            ClientWaitHostGenerationInputBeforeFirstLoad = cfg.Bind("NetworkSceneAuthority", "ClientWaitHostGenerationInputBeforeFirstLoad", true,
                "Client only: intercept GameManager.GoToLevel and block local level generation until the Host's generation input (seed + used sets) arrives, then load the Host's level.");
            ClientLoadGateTimeoutSeconds = cfg.Bind("NetworkSceneAuthority", "ClientLoadGateTimeoutSeconds", 30f,
                "How long the Client load gate waits for Host generation input before logging a timeout. The local load stays blocked even after timeout.");
            ClientLoadGateAllowFallbackAfterTimeout = cfg.Bind("NetworkSceneAuthority", "ClientLoadGateAllowFallbackAfterTimeout", false,
                "If true, allow falling back to a local load after the gate times out. Test builds keep this false so a wrong-seed local map is never silently generated.");
            ClientLoadGateRequestIntervalSeconds = cfg.Bind("NetworkSceneAuthority", "ClientLoadGateRequestIntervalSeconds", 2f,
                "While the Client load gate waits, how often (seconds) to actively re-request the Host's generation input.");
            ClientGateDeathRespawnUntilHostHub = cfg.Bind("NetworkSceneAuthority", "ClientGateDeathRespawnUntilHostHub", true,
                "Client only: when joined, gate the DEATH respawn to the hub and wait for the Host's hub seed so both land in the SAME safe-zone instance, instead of the client generating its own hub. Falls back to a local hub respawn on timeout.");
            ClientGateDeathRespawnTimeoutSeconds = cfg.Bind("NetworkSceneAuthority", "ClientGateDeathRespawnTimeoutSeconds", 12f,
                "How long the client waits for the Host's hub seed during a death respawn before falling back to a local hub load (so a lone death or a non-respawning host never deadlocks the client).");
            EnableClientTransitionRelay = cfg.Bind("NetworkSceneAuthority", "EnableClientTransitionRelay", true,
                "Let the client LEAD level transitions: when the client walks into an exit, relay the target to the host so the host performs the transition authoritatively (host player moves + generates) and the gated client follows. When false the client just waits for the host to go there on its own.");
            AllowClientInitiatedLevelLoad = cfg.Bind("NetworkSceneAuthority", "AllowClientInitiatedLevelLoad", true,
                "Allow the client to load the next level. When a joined client walks into an in-run NextLevelTrigger (CompleteLevel — the sub-level advance that does NOT go through GoToLevel), instead of generating its own level the client shows a native loading fade, tells the host, and the host LEADS the transition so everyone advances together. Requires EnableClientTransitionRelay. On timeout the client advances locally so it is never stuck.");
            ClientInitiatedLoadTimeoutSeconds = cfg.Bind("NetworkSceneAuthority", "ClientInitiatedLoadTimeoutSeconds", 15f,
                "How long a client-initiated level load waits for the host to lead before falling back to advancing locally (so an unresponsive host never leaves the client stuck behind a black loading fade).");
            EnableClientReloadInPlaceRelay = cfg.Bind("NetworkSceneAuthority", "EnableClientReloadInPlaceRelay", true,
                "Phase F3-Reload: fix the client F3-ing to the level both ends are already in (reload-in-place). Without this the client self-reloads off the host's stale scene request and diverges into its own fresh instance (Log147: laggy, link breaks, save persists the split). When on, the client relays the reload to the host and waits; the host RE-LEADS the reload so both regenerate the level together (this resets an in-progress fight, by design). Off = legacy (client self-reloads in place). Requires EnableClientTransitionRelay.");
            ClientLinkedByDefault = cfg.Bind("NetworkSceneAuthority", "ClientLinkedByDefault", false,
                "联机状态: whether the CLIENT starts LINKED (joining/following the host). Default false so an in-progress solo run is never hijacked — the player presses ManualClientSceneFollowKey (PageDown) to link and ClientUnlinkKey to unlink.");
            HostLinkedByDefault = cfg.Bind("NetworkSceneAuthority", "HostLinkedByDefault", true,
                "联机状态: whether the HOST starts LINKED (mod multiplayer active: broadcasts scene changes, leads client-relayed transitions). Default true for debugging. When off the host behaves single-player. Toggle in-game with HostLinkToggleKey.");
            ClientUnlinkKey = cfg.Bind("NetworkSceneAuthority", "ClientUnlinkKey", new KeyboardShortcut(KeyCode.PageUp),
                "Client only: key to LEAVE 联机状态 (stop following/relaying and play the local run independently). PageDown links, this unlinks.");
            HostLinkToggleKey = cfg.Bind("NetworkSceneAuthority", "HostLinkToggleKey", new KeyboardShortcut(KeyCode.PageDown),
                "Host only: key to TOGGLE 联机状态 (mod multiplayer on/off).");

            // Phase 5.4-E: host-authoritative Boss encounter start. The Client requests a boss start from the
            // Host instead of starting locally; the Host broadcasts the authoritative start to all clients.
            EnableBossEncounterSync = cfg.Bind("NetworkBoss", "EnableBossEncounterSync", true,
                "Phase 5.4-E: detect boss encounters and synchronize the fight START host-authoritatively. Health/phase/death are NOT synced yet. Adapter-based; unknown bosses are only probed, never force-started.");
            BossEncounterClientBlockLocalStart = cfg.Bind("NetworkBoss", "BossEncounterClientBlockLocalStart", true,
                "Phase 5.4-E: when a joined Client triggers a boss start, block the local start and request the Host to start it authoritatively. If false, the Client still requests but is allowed to start locally (diagnostic).");
            LogBossEncounter = cfg.Bind("NetworkBoss", "LogBossEncounter", true,
                "Log boss encounter discovery / start request / broadcast / apply for debugging.");

            // Phase 5.4-E2: a Boss start is a CHAIN (interact/intro -> coroutine/dialogue -> fight-start). After the
            // Client applies a host start, a per-encounter authorized-continuation window keeps the later chain steps
            // from being blocked as unauthorized local starts. The window closes once the fight is observed started
            // plus this grace, or when the run changes — deliberately not a fixed timeout (Cousin is dialogue-paced).
            BossContinuationGraceSeconds = cfg.Bind("NetworkBoss", "BossContinuationGraceSeconds", 5f,
                "Phase 5.4-E2: seconds to keep the authorized-continuation window open AFTER the boss fight is observed started, before closing it.");
            EnableBossLifecycleProbe = cfg.Bind("NetworkBoss", "EnableBossLifecycleProbe", true,
                "Phase 5.4-E2: postfix-probe key boss lifecycle methods (TriggerFight/StartFight/ChangePhase/transitions/death) and log compact state changes.");
            LogBossLifecycle = cfg.Bind("NetworkBoss", "LogBossLifecycle", true,
                "Phase 5.4-E2: log the boss lifecycle probe state-change lines. Compact and state-change-gated to avoid spam.");
            LogBossPreFight = cfg.Bind("NetworkBoss", "LogBossPreFight", true,
                "Phase PF-0: read-only diagnostic. When a boss pre-fight start entrypoint fires, log local+remote scene/seed convergence (did the client race ahead into a divergent boss instance?) and the room-seal/teleport timing. No gameplay change.");
            RemoveBossDialogInteractableOnStart = cfg.Bind("NetworkBoss", "RemoveBossDialogInteractableOnStart", true,
                "Fix A (root): when a boss fight starts, remove that boss's dialog interactable on EVERY end (the same thing vanilla WitchBossController.FightStartRoutine does via RemoveInteractable). This is the PRIMARY fix for the host stale-dialog loop when the fight is started remotely; the duplicate-dialog suppression remains only as a safety net.");
            EnableFaithfulBossIntro = cfg.Bind("NetworkBoss", "EnableFaithfulBossIntro", true,
                "Phase PF: on a joined client, instead of fake-starting the boss via direct Introduction()/StartFight() reflection (which skips the real intro dialog), set the boss's own trigger flag so its native behavior-tree intro sequence runs locally — reproducing the REAL intro animation + dialog + camera + boss bar ~99% faithfully. The fight mechanic stays host-authoritative. Cousin first.");
            GateBossFightOnDialogClose = cfg.Bind("NetworkBoss", "GateBossFightOnDialogClose", true,
                "Phase PF (Plan B): for dialog-gated bosses (Cousin), block the behavior-tree StartFight until an in-room player dismisses the intro dialog, then start the fight host-authoritatively on every end and close all remaining boss dialogs. Restores the single-player gate (the dialog used to PAUSE the game, freezing the WaitForSeconds before StartFight) that co-op's no-pause mode removed, so the fight no longer auto-starts on top of the dialog.");
            DeferBossIntroArm = cfg.Bind("NetworkBoss", "DeferBossIntroArm", true,
                "Phase PF-ArmDefer (issue 1): defer the Cousin's intro arm so it appears AFTER the intro dialog closes (at fight start), matching single-player. Co-op's no-pause mode (Phase 5.7-NP) lets the behavior-tree SpawnArm fire ~1s into the dialog, so the arm pokes out during the cutscene. When on, the behavior-tree intro arm is blocked and the real arm is replayed on the dialog-close fight commit (with EnableCousinArmSync the host's replayed arm flows through the RT3-A pipeline and the client mirrors it). Cosmetic-only; mid-fight Reappear arms are unaffected. Off = legacy (arm appears during the dialog). Requires GateBossFightOnDialogClose.");
            EnableBossRoomMembership = cfg.Bind("NetworkBoss", "EnableBossRoomMembership", true,
                "Phase RM (substrate): track which players are 'in the boss room' (host-authoritative). Each end reports when its local player crosses the boss's room-entry trigger; the host aggregates the in-room set and broadcasts it. Observe-only for now (logs '[RoomMembership]', changes no behavior) — the shared foundation for restricting the synced intro cutscene to in-room players and for the future arena lockdown (AFK exclusion).");
            GateBossDialogToInRoom = cfg.Bind("NetworkBoss", "GateBossDialogToInRoom", true,
                "Phase RM-2b: scope the synced boss intro cutscene to IN-ROOM players. When a player triggers the fight, only ends whose local player has entered the boss room play the intro+dialog (camera lock + invuln); out-of-room ends (incl. an AFK host) are NOT pulled into the cutscene. A player who enters the room (walks in OR is teleported in by the arena lockdown) while the dialog is still running catches up the cutscene. When the dialog ends the fight commits host-authoritatively and the dialog is removed everywhere, so no one can re-open it. Cousin first; off = legacy (cutscene replays on every end). Logs '[BossDialogCutscene]'.");
            ExcludeOutOfRoomPlayersFromBossAttacks = cfg.Bind("NetworkBoss", "ExcludeOutOfRoomPlayersFromBossAttacks", true,
                "Phase RT3-Cousin-arms-Room: don't aim the Cousin arm's group throw at players who are NOT in the boss arena (e.g. an AFK teammate who never entered) or who are downed — same intent as the downed-player-untargetable rule. \"In arena\" comes from the ArenaLockdown membership (host-authoritative, doorway-parity, broadcast to clients) which reliably accumulates everyone incl. late walk-ins, NOT the seed-keyed boss-trigger room membership (which churns). The host throws one real ball per Player (its own + each client ghost proxy); the client throws at its local player + a visual ball at each remote proxy. Local presence uses the reliable local doorway signal; remote presence uses the arena member set. Fail-open (no arena filtering, downed filter still applies) when no active arena membership is known, so the boss never becomes un-attackable. Requires EnableArenaLockdown. Reversible.");

            // Phase 5.4-E3: dialog-gated bosses (Cousin / Lucia) sync the "fight committed" decision via BossDialogCommit
            // and finalize the local dialog with the real Graph.Stop(true). Witch broadcasts a minimal phase/state skeleton.
            EnableEmperorWormDiagnostics = cfg.Bind("NetworkBoss", "EnableEmperorWormDiagnostics", true,
                "Phase 5.4-E3: probe + log Emperor worm controller state (identify the double-worm source). Diagnostic only; does not change gameplay.");
            EnableEmperorClientWormSuppression = cfg.Bind("NetworkBoss", "EnableEmperorClientWormSuppression", false,
                "Phase 5.4-E3 SCAFFOLD (default OFF, reversible): on the Client, block the local EmperorBossWorm.StartMovement so the client stops driving an independent second worm. Never destroys anything; Emperor-only.");
            LogBossTransitionDiagnostics = cfg.Bind("NetworkBoss", "LogBossTransitionDiagnostics", true,
                "Phase 5.4-E3 (P2): log extra context when a Client receives a HostSceneRequest while already loading/transitioning. Diagnostic only — does not change transition behavior.");

            // Phase 5.4-E4: Host records boss-owned runtime adds (CousinArm/LuciaEye/...) by (encounter, addType, seq)
            // and broadcasts so the Client can bind local[seq] to host[seq] instead of failing proximity/roster binding.
            EnableBossDynamicSpawnManifest = cfg.Bind("NetworkBoss", "EnableBossDynamicSpawnManifest", true,
                "Phase 5.4-E4: capture boss-spawned runtime sub-entities (via UnitSO.SpawnUnit) into a host-authoritative manifest and classify client binding. Foundation only — never destroys or force-syncs this phase.");
            LogBossDynamicSpawn = cfg.Bind("NetworkBoss", "LogBossDynamicSpawn", true,
                "Phase 5.4-E4: log each boss dynamic spawn + binding result (bound / host-only / client-extra).");

            // Phase RT3-Cousin-arms(-Anim): GoblinCousinArm is a scripted prop (appear→idle→attack→disappear) driven by
            // its own behaviour tree, not a locomotion enemy. It is ALWAYS special-excluded from the RT3-A puppet pipeline
            // (see IsSpecialAdd): puppet mode disables the BT and the puppet animator-mirror only reproduces host ATTACK
            // windows, so the client arm's idle/disappear states were never reproduced (the Animator looped its default
            // Appear state). Self-animating restores them faithfully. This toggle now gates only the co-op de-fang + group
            // throw: when on, CousinArmPatches de-fangs the client arm's throw to 0 damage and the host throws one real mud
            // ball per player (host + remote-player proxies) so damage stays host-authoritative. Off = legacy per-end
            // independent arms (each end's arm throws its own real damage). Double-spawn is prevented by the intro-arm
            // defer + special host-only skip (one local arm per end, no mirror) regardless of this toggle.
            EnableCousinArmSync = cfg.Bind("NetworkBoss", "EnableCousinArmSync", true,
                "Phase RT3-Cousin-arms-Anim: the Cousin arm self-animates via its own behaviour tree on every end (faithful appear/idle/attack/disappear); it is never a host puppet. This toggle gates the co-op de-fang + group-AoE throw: on = the client arm's throw is de-fanged to 0 damage and the host throws one real mud ball per player (damage host-authoritative). Off = legacy per-end independent arms (double real damage). Reversible.");

            // Phase 5.4-F: route a client's hit on a boss MAIN BODY to the Host's real Unit.ReceiveDamage so the boss
            // mechanic (onDamageRecieved) advances host-side, instead of the client locally deducting HP that the host
            // health broadcast then overwrites. Main-body only this phase (no sub-units / special targets).
            EnableBossDamageAuthority = cfg.Bind("NetworkBoss", "EnableBossDamageAuthority", true,
                "Phase 5.4-F: Client boss-body hits are applied by the Host through the real ReceiveDamage pipeline (host-authoritative boss damage). Main body only.");

            // Phase 5.4-F2: experimental client presentation shortcuts (Cousin AI activation + Desert TriggerFight
            // skip). LogOutput29 proved BOTH wrong: Cousin still stands still (it's the intro ANIMATION chain that
            // clears owner invuln, not AI activation), and the Desert skip made the old man invisible again. Default
            // OFF (rolled back); the diagnostics still log. F3 replaces this with a real local presentation chain.
            EnableBossClientPresentation = cfg.Bind("NetworkBoss", "EnableBossClientPresentation", false,
                "Phase 5.4-F2 (rolled back, default off): experimental client boss presentation shortcuts (Cousin AI activation, Desert intro skip). Proven insufficient; kept only for diagnostics/toggling.");

            // Phase 5.4-F4: Cousin is a fixed-point pool boss; LogOutput30 proved Host AND Client each independently run
            // Submerge/MoveToNewPool/Reappear with their own random pool. The Host is authoritative: it broadcasts the
            // events, the Client BLOCKS its own and mirrors the Host's pool, so both dig out of the same hole.
            EnableBossDiscreteEventAuthority = cfg.Bind("NetworkBoss", "EnableBossDiscreteEventAuthority", true,
                "Phase 5.4-F4: host-authoritative fixed-point boss events (Cousin Submerge/MoveToNewPool/Reappear). Client blocks its own and mirrors the host's chosen pool. Reversible.");

            // Phase 5.4-F5: Lucia's eye phase locks the body invulnerable until all spawned eyes die (EyeDied →
            // RestartPhases). The Client reports a local eye kill; the Host consumes one of ITS living eyes through the
            // real death path so the vanilla cycle runs host-authoritatively. Count/cycle only (no per-eye mapping).
            EnableLuciaEyeAuthority = cfg.Bind("NetworkBoss", "EnableLuciaEyeAuthority", true,
                "Phase 5.4-F5: host-authoritative Lucia eye defeat. Client reports a local eye kill; the Host consumes one living eye via the real death path so vanilla EyeDied/RestartPhases runs and the body unlocks naturally. Reversible.");

            // Phase 5.4-F6: Lucia eye-phase completion (Client runs the real RestartPhases when the Host's eyes hit 0 so
            // it leaves Phase 5 / returns to centre) + Lucia terminal death (Client runs a safe local death with the
            // host-only loot/checkpoint/save isolated). Rides the F5 eye gate for completion; this gates the death part.
            EnableLuciaDeathAuthority = cfg.Bind("NetworkBoss", "EnableLuciaDeathAuthority", true,
                "Phase 5.4-F6: host-authoritative Lucia terminal death. On the Host's real Lucia death the Client runs a safe local death (real Unit death + boss-end presentation; loot/checkpoint/save isolated) and stops sending hits/state. Reversible.");

            // Phase 5.4-G: Witch players shoot the per-phase visible witch (the phase controller's witchUnit), NOT
            // witchMainUnit. Route a Client hit on a phase witch to the Host's matching phase witch so its real
            // ReceiveDamage runs (OnDamageMainWitch drops the shared health AND the phase mechanic — e.g. Phase 4
            // RegisterInstance/GoDown — advances). Phase 2 dome (real/illusion) is reserved for a later manifest.
            EnableWitchPhaseDamageAuthority = cfg.Bind("NetworkBoss", "EnableWitchPhaseDamageAuthority", true,
                "Phase 5.4-G: route Client hits on Witch phase 1/3/4/5/6 visible witches to the Host's matching phase witch real ReceiveDamage (advances both shared health and the phase mechanic). Phase 2 dome reserved. Reversible.");

            // Phase 5.4-G2: Witch phases CYCLE (Phase6→Phase1), so the old forward-only phase compare desynced the ends
            // and broke damage routing (wrongRole). The Host owns phase transitions with a monotonic revision; the Client
            // applies by revision (even when the enum goes backwards) and is blocked from self-advancing its own phase.
            EnableWitchPhaseAuthority = cfg.Bind("NetworkBoss", "EnableWitchPhaseAuthority", true,
                "Phase 5.4-G2: host-authoritative Witch phase transitions by revision. Client blocks its own ChangePhase and applies the Host's phase by revision (handles the Phase6→Phase1 cycle). Keeps the ends in the same phase so damage routing works. Reversible.");

            // Phase 5.4-G4: diagnostic timing probe for WitchPhase2.InitPhase/ShowWitches — confirms whether the Client's
            // first Phase 2 round runs ShowWitches at all (suspected race: Host leaves Phase 2 before the Client's local
            // delayPhaseStart elapses). Read-only; pre-requisite to implementing the full WitchPhase2Manifest.
            LogWitchPhase2Probe = cfg.Bind("NetworkBoss", "LogWitchPhase2Probe", true,
                "Phase 5.4-G4: log WitchPhase2 InitPhase/ShowWitches timing (witchesCreated, spawn/dome counts, delayTimer, final real dome index) on both ends. Diagnostic-only.");

            // Phase 5.4-G5: host-authoritative Witch Phase 2 dome layout. Host captures the post-shuffle layout at
            // ShowWitches (real dome index) and broadcasts it; the Client mirrors real/illusion per dome (blocking its own
            // random ShowWitches), routes dome-index hits to the Host's matching witch, applies host hide results, and
            // isolates Phase 2 witches from the ordinary puppet transform so the dome placement holds.
            EnableWitchPhase2Manifest = cfg.Bind("NetworkBoss", "EnableWitchPhase2Manifest", true,
                "Phase 5.4-G5: host-authoritative Witch Phase 2 dome manifest (real/illusion per dome from the Host's ShowWitches), dome-index hit routing, host hide results, and ordinary-puppet isolation for Phase 2 witches. Reversible.");

            // Phase 5.4-G7: Witch death cleanup. The Client runs WitchDeath via the enemy death mirror, but WitchDeath
            // calls AmuletHelper.RemoveAllCharges → ModifyWorldResource("Amulet") which throws KeyNotFoundException on the
            // Client (it never picked up the amulet), aborting the rest of WitchDeath. Swallow that one call so the death
            // completes, and mark the encounter terminal so no more hits/state are sent for the dead witch.
            EnableWitchDeathFix = cfg.Bind("NetworkBoss", "EnableWitchDeathFix", true,
                "Phase 5.4-G7: make the Client's Witch death complete (swallow the amulet RemoveAllCharges KeyNotFoundException) and mark the witch encounter terminal on death (stop hits/state). Reversible.");

            // Phase 5.5-RT1: runtime (post-level-load) unit spawn sync. Stage 1: the Host's F3 DevTools spawns are
            // mirrored to the Client and bound into the puppet pipeline (one-sided, no double-spawn). Boss adds + client
            // F3 spawns come in later stages. Reversible.
            EnableRuntimeSpawnSync = new Fixed<bool>(true); // Phase 5.5-RT1 runtime spawn sync — functional, always on.
            LogRuntimeSpawnSync = cfg.Bind("NetworkEnemy", "LogRuntimeSpawnSync", true,
                "Phase 5.5-RT1: verbose log for runtime spawn sync (broadcast / mirror / bind).");

            // Phase 5.5-RT3-A: the Client and Host pick spawn POINTS in divergent order/sets (RNG diverges), so the
            // reused local boss-add never matches the host add by (encounter, type, seq) — wrong position + spurious
            // mis-kills (client physics hits a local add at point A, claims the host add at point B). Snap-on-bind hard-
            // teleports the bound local add to the host's broadcast spawn position (discarding the divergent local pos);
            // the hit-gate swallows client hit-claims on an add until it is bound+snapped (kills the mis-kill).
            EnableRuntimeSpawnSnapOnBind = new Fixed<bool>(true); // Phase 5.5-RT3-A snap-on-bind + hit-gate — functional.
            // Inert-until-bound: freeze the local boss-add's movement (stop AI + zero velocity) between local spawn and
            // bind so it doesn't wander at the divergent local spawn point. Damage is already covered by the hit-gate, so
            // this is a movement freeze only (no collider disable — avoids fall-through risk). Independently toggleable.
            EnableRuntimeSpawnInertUntilBound = new Fixed<bool>(true); // Phase 5.5-RT3-A inert-until-bound — functional.
            EnableDeathSpawnSync = new Fixed<bool>(true);  // Phase 5.7-DS death-spawn mutation sync — functional.
            EnableMinionSpawnSync = new Fixed<bool>(true); // Phase 5.7-DS2 spawnMinionsOnDeath sync — functional.

            // Phase 5.6-WS: replicate each player's weapon barrage onto every OTHER peer as VISUAL-ONLY bullets.
            // The firing peer captures the computed projectile template (equipmentManager.lastFiredProjectile.ray) plus
            // count/spread/aim and broadcasts one fire event per trigger pull; receivers replay the barrage through the
            // game's real ProjectileSystem with damage stripped (empty damageComps + explicitDamage=0 → zero damage,
            // verified safe). Damage stays host-authoritative via the existing ClientHitRequest pipeline.
            EnablePlayerWeaponSync = new Fixed<bool>(true); // Phase 5.6-WS player weapon visual sync — functional, always on.
            LogPlayerWeaponSync = cfg.Bind("PlayerWeapon", "LogPlayerWeaponSync", true,
                "Phase 5.6-WS: verbose log for player weapon sync (capture / broadcast / replay).");
            PlayerWeaponSyncMaxProjectilesPerShot = new Fixed<int>(256); // safety clamp on replayed visual projectiles.

            // Phase 5.7-BR: sync in-scene destructibles (Units.Breakable). Each peer breaks its own destructibles for
            // real; when one breaks we broadcast a break event keyed by the breakable's deterministic spawn position,
            // and receivers call Break() on the matching local destructible so it shatters/loots/cascades the same on
            // every screen. Peer-authoritative EFFECT mirror (loot stays per-peer — loot is not networked). Reversible.
            EnableBreakableSync = new Fixed<bool>(true); // Phase 5.7-BR destructible sync — functional, always on.
            LogBreakableSync = cfg.Bind("Destructibles", "LogBreakableSync", true,
                "Phase 5.7-BR: verbose log for destructible sync (capture / broadcast / mirror match).");

            // Phase LD-1: sync combat-room gates (MetalGate). SULFUR seals combat rooms (boss arenas AND ordinary elite
            // rooms) with a MetalGate closed by a PlayerTrigger the entering player crosses; gates are per-end independent
            // so an out-of-room / AFK player's gate is left open. Each peer's MetalGate.Close()/Open() is broadcast and
            // mirrored by position on the others. Foundation for the FF14-style arena lockdown.
            EnableGateSync = new Fixed<bool>(true); // Phase LD-1 combat-room gate (MetalGate) sync — functional, always on.
            LogGateSync = cfg.Bind("Destructibles", "LogGateSync", true,
                "Phase LD-1: verbose log for gate sync (capture / broadcast / mirror match).");

            // Phase LD-1b: some arenas (Lucia) seal not with a MetalGate but with a PlayerTrigger firing
            // GameObject.SetActive(Doors, true). Mirror those door-named SetActive targets across peers, keyed by the
            // trigger's position (the receiver reads its own trigger's event to get its local door reference).
            EnableTriggerDoorSync = new Fixed<bool>(true); // Phase LD-1b SetActive door sync (Lucia) — functional, always on.
            LogTriggerDoorSync = cfg.Bind("Destructibles", "LogTriggerDoorSync", true,
                "Phase LD-1b: verbose log for trigger-door sync (capture / broadcast / mirror match).");

            // Phase LD-2: FF14-style arena lockdown. A player crossing a combat-room seal trigger is "in-room"; the first
            // cross anchors a timer; after 5s the non-in-room players in that level are force-sealed with an invisible
            // two-way barrier at their local door (LD-2b), after 10s they get a confirm prompt → on confirm (or on boss
            // death) they teleport in and the barrier drops (LD-2c). Host-authoritative membership + timer.
            EnableArenaLockdown = cfg.Bind("NetworkBoss", "EnableArenaLockdown", true,
                "Phase LD-2: FF14-style arena lockdown — host tracks who crossed each combat-room seal trigger (in-room) and runs the t0/+5s/+10s timeline. At +5s non-in-room ends raise an invisible two-way barrier at their local door (anti-cheat); at +10s they get a confirm prompt → teleport in on confirm or boss death, dropping the barrier. Reversible.");
            LogArenaLockdown = cfg.Bind("NetworkBoss", "LogArenaLockdown", true,
                "Phase LD-2: verbose log for arena lockdown (local crossings, in-room set, seal/popup/release/teleport).");
            ArenaEnterConfirmKey = cfg.Bind("NetworkBoss", "ArenaEnterConfirmKey", new KeyboardShortcut(KeyCode.Return),
                "Phase LD-2c: key an out-of-room player presses to confirm teleporting into a locked-down arena (the confirm prompt). Default Enter.");
            EnableArenaGracePeriod = cfg.Bind("NetworkBoss", "EnableArenaGracePeriod", true,
                "Phase LD-2d: grace mode. The vanilla combat-room gate normally slams shut the instant the first player crosses; with this on, the gate is kept OPEN for the seal delay (~5 s) so teammates can still walk in together, then it closes + the barrier goes up. MetalGate arenas only (SetActive-door arenas like Lucia still close at t0). Off = old instant close. Reversible.");

            // World item-drop sync: items that appear in the world are mirrored across peers. Spawn is optimistic +
            // peer-authoritative (instant local drop, then broadcast); take is host-authoritative (first picker wins, the
            // item vanishes for everyone and only the winner receives it). The DIY gun state (attachments / enchantments /
            // caliber / ammo / durability+experience) travels with the item. Reversible.
            EnableWorldItemDropSync = cfg.Bind("WorldItems", "EnableWorldItemDropSync", true,
                "Sync items that appear in the world across peers. With ShareAllLoot=false (default) only player-thrown items/guns are synced; with ShareAllLoot=true every world pickup is synced. Spawn is optimistic; take is host-authoritative (first picker wins). Reversible.");
            LogWorldItemDropSync = cfg.Bind("WorldItems", "LogWorldItemDropSync", true,
                "Verbose log for world item-drop sync (capture / mirror / take request / host grant / removal).");
            ShareAllLoot = cfg.Bind("WorldItems", "ShareAllLoot", false,
                "FUTURE host room-setting: when true, ALL world pickups (loot included) are synced and shared (first picker takes it, it vanishes for everyone). When false (default), loot stays per-peer and only player-thrown items/guns are synced. NOTE: full shared-loot also needs host-authoritative loot rolling (suppress client rolls) — not yet implemented; flipping this now only widens the sync filter.");

            // Phase 5.6-WS-2: show each remote player's currently held weapon model (rebuilt from WeaponSO + installed
            // attachments, since attachments change the model) in their proxy's hands. Visual only.
            EnableRemoteWeaponModel = new Fixed<bool>(true); // Phase 5.6-WS-2 remote held weapon model — functional, always on.
            LogRemoteWeaponModel = cfg.Bind("PlayerWeapon", "LogRemoteWeaponModel", true,
                "Phase 5.6-WS-2: verbose log for remote weapon model sync (broadcast / build / attach).");

            // Phase 5.6-WS-3: one-shot runtime dump of the local Player(Clone) visual hierarchy (playerVisuals array,
            // all SpriteRenderers/Animators/Renderers, full transform tree) to locate the player's directional billboard
            // sprites (front/back/side) so remote players can be shown as paper sprites like enemies.
            LogPlayerVisualDiscovery = cfg.Bind("PlayerWeapon", "LogPlayerVisualDiscovery", true,
                "Phase 5.6-WS-3: dump the local player's visual hierarchy once (find the directional billboard sprites). Diagnostic only.");
            LogPlayerSpriteAssetScan = cfg.Bind("PlayerWeapon", "LogPlayerSpriteAssetScan", true,
                "Phase 5.6-WS-3b: one-shot scan of all loaded Sprites/Textures/Prefabs/Animators + Addressables keys for player/character art. Diagnostic only.");

            // Phase 5.6-WS-3: the player has no directional sprite art, so represent remote players with an NPC's
            // billboard paper sprite (visual-only, gameplay stripped) instead of the plain capsule. Faces the camera via
            // the game's BillboardSpriteManager; the held weapon model stays attached.
            // Phase 5.6-WS-3 remote player body + carry pose — appearance finalized, hardcoded to the effective values
            // (forced ones used their dev-default; un-forced ones their bind default). Only Log* stays in cfg.
            EnableRemotePlayerSpriteBody = new Fixed<bool>(true);
            EnableRemotePlayerNpcBody = new Fixed<bool>(false);
            RemotePlayerBodyUnitKeyword = new Fixed<string>("civilian,grocer,scholar,arthur,telia,citizen,man,woman");
            LogRemotePlayerBody = cfg.Bind("PlayerWeapon", "LogRemotePlayerBody", true,
                "Phase 5.6-WS-3: verbose log for the remote player NPC billboard body (resolve / load / build / attach).");
            RemoteBodyScale = new Fixed<float>(1.2f);
            RemoteBodyFeetYOffset = new Fixed<float>(0.0f);
            RemoteWeaponScale = new Fixed<float>(1.4f);
            RemoteWeaponHipHeight = new Fixed<float>(1.0f);
            RemoteWeaponForward = new Fixed<float>(0.30f);
            RemoteWeaponRight = new Fixed<float>(0.4f);
            RemoteBodyPitchLimit = new Fixed<float>(25f);
            RemoteBodyDepthBias = new Fixed<float>(0.0f);
            RemoteNameSize = new Fixed<float>(0.03f);
            RemoteNameHeight = new Fixed<float>(0.45f);

            // Phase 5.4-A: how a Client decides to join the Host. Default only auto-joins from a safe state
            // (Hub/Menu/SafeZone) so a player's own combat run or level-transition save is never hijacked.
            ClientJoinMode = cfg.Bind("NetworkSceneAuthority", "ClientJoinMode", "AutoJoinFromHubOnly",
                new ConfigDescription(
                    "Client join policy. ManualOnly: never auto-join (use the manual follow key). AutoJoinFromHubOnly: auto-join/auto-follow only when the Client is in Hub/Menu/SafeZone; preserve any in-progress local combat/transition run. AskBeforeLeavingLocalRun: like AutoJoinFromHubOnly but logs a manual-confirm hint when not in a hub. ForceAutoJoin: always auto-join (test only; warns). Once joined, combat auto-follow to the Host's next level always continues regardless of this mode.",
                    new AcceptableValueList<string>("ManualOnly", "AutoJoinFromHubOnly", "AskBeforeLeavingLocalRun", "ForceAutoJoin")));

            // Phase 5.3-M P1: automatic scene follow + lightweight load barrier.
            EnableAutoFollowHostSceneRequest = cfg.Bind("NetworkSceneAuthority", "EnableAutoFollowHostSceneRequest", true,
                "Phase 5.3-M P1: when the Host enters a combat level it sends a HostSceneRequest with AutoLoadAllowed=true. The Client then follows automatically (host-driven GoToLevel) without the manual follow key. Hub/Menu requests never auto-load.");
            EnableLoadBarrier = cfg.Bind("NetworkSceneAuthority", "EnableLoadBarrier", true,
                "Phase 5.3-M P1: Host tracks which connected clients have acknowledged loading the current run (waiting -> client loaded -> all clients loaded). Log/status only by default; does not freeze host gameplay.");
            LoadBarrierTimeoutSeconds = cfg.Bind("NetworkSceneAuthority", "LoadBarrierTimeoutSeconds", 30f,
                new ConfigDescription("How long the Host load barrier waits for all clients to acknowledge a run before logging a timeout. The host is not blocked while LoadBarrierBlockHostAdvance=false.",
                    new AcceptableValueRange<float>(1f, 120f)));
            LoadBarrierBlockHostAdvance = cfg.Bind("NetworkSceneAuthority", "LoadBarrierBlockHostAdvance", false,
                "Phase 5.3-M P1: reserved. When false (current default) the load barrier never blocks the host from advancing; it only logs/reports waiting state.");
            LoadBarrierLogOnlyMode = cfg.Bind("NetworkSceneAuthority", "LoadBarrierLogOnlyMode", true,
                "Phase 5.3-M P1: when true the load barrier only logs/reports and never suppresses host runtime sync. Keep true until real host-side gating is implemented.");

            // Phase 3.0 remote player visual proxy only — functional behaviour + tuned values (release-hardcoded).
            EnableRemotePlayerVisualProxy = new Fixed<bool>(true);
            RemotePlayerTransformSendRateHz = new Fixed<float>(10f);
            RemotePlayerVisualTimeoutSeconds = new Fixed<float>(3f);
            RemotePlayerVisualInterpolationSpeed = new Fixed<float>(12f);
            RemotePlayerVisualSnapDistance = new Fixed<float>(8f);
            EnableRemotePlayerProxyCollision = new Fixed<bool>(true);
            RemotePlayerCollisionSoft = new Fixed<bool>(true);
            RemotePlayerSoftCollisionRadius = new Fixed<float>(0.8f);
            RemotePlayerSoftCollisionPushSpeed = new Fixed<float>(0.5f);

            // Phase 4.0-A gameplay entity probe. Observation/logging only; no gameplay or network changes.
            EnableGameplayEntityProbe = cfg.Bind("NetworkGameplayProbe", "EnableGameplayEntityProbe", true,
                "Enable local-only structured gameplay entity probe logs. This never syncs or changes gameplay.");
            GameplayEntityProbeSummaryIntervalSeconds = cfg.Bind("NetworkGameplayProbe", "GameplayEntityProbeSummaryIntervalSeconds", 10f,
                "How often (seconds) to emit gameplay probe summaries.");
            LogGameplayEntitySpawn = cfg.Bind("NetworkGameplayProbe", "LogGameplayEntitySpawn", true,
                "Log first observed spawn/registration of each tracked gameplay entity.");
            LogGameplayEntityDamage = cfg.Bind("NetworkGameplayProbe", "LogGameplayEntityDamage", false,
                "Log individual damage events for tracked gameplay entities. Very noisy; keep false for first tests.");
            LogGameplayEntityDeath = cfg.Bind("NetworkGameplayProbe", "LogGameplayEntityDeath", true,
                "Log death events for tracked gameplay entities.");
            RequireStableSceneAndSeedForGameplayProbe = cfg.Bind("NetworkGameplayProbe", "RequireStableSceneAndSeedForGameplayProbe", true,
                "Delay per-entity gameplay probe logs until local chapter/level and levelSeed are known. Events are observed locally only and late-logged when context becomes available.");

            // Phase 4.0-B enemy death event mirror. Network log/matching only by default; no gameplay mutation.
            // Phase 4.0-B enemy death event mirror — functional, always on (release-hardcoded); only Log* stays in cfg.
            EnableHostEnemyDeathEventMirror = new Fixed<bool>(true);
            LogReceivedEnemyDeathEvents = cfg.Bind("NetworkGameplaySyncExperimental", "LogReceivedEnemyDeathEvents", true,
                "Log Host enemy death events received by a Client and the local entity match result.");
            ApplyReceivedEnemyDeathEvents = new Fixed<bool>(true);
            EnemyDeathMirrorPositionTolerance = new Fixed<float>(2.5f);
            EnemyDeathMirrorUseHorizontalPositionTolerance = new Fixed<bool>(true);
            EnableClientEnemyDeathClaim = new Fixed<bool>(true);
            LogReceivedClientEnemyDeathClaims = cfg.Bind("NetworkGameplaySyncExperimental", "LogReceivedClientEnemyDeathClaims", true,
                "Log Client enemy death claims received by the Host and the Host-side local entity match/apply result.");
            ApplyReceivedClientEnemyDeathClaimsOnHost = new Fixed<bool>(true);

            // Phase 4.3-A co-op player downed / revive experiment. Defaults are active for this test build only; no forced config overwrite is performed.
            // Phase 4.3-A downed/revive — functional + tuned values hardcoded (Fixed); the Log* and the keybind stay in cfg.
            EnableCoopPlayerDownedRevive = new Fixed<bool>(true);
            LogPlayerLifeSync = cfg.Bind("NetworkPlayerLifeExperimental", "LogPlayerLifeSync", true,
                "Log player downed/revive/native-death lifecycle packets and local decisions.");
            PlayerDownedRescueTimeoutSeconds = new Fixed<float>(0f);   // 0 = infinite wait before forced death.
            PlayerReviveHoldSeconds = new Fixed<float>(2.0f);
            PlayerReviveDistance = new Fixed<float>(2.5f);
            PlayerReviveHealthRatio = new Fixed<float>(0.35f);
            PlayerReviveInvulnerabilitySeconds = new Fixed<float>(2f);
            PlayerDownedHealthFloor = new Fixed<float>(1.0f);
            PlayerReviveHoldKey = cfg.Bind("NetworkPlayerLifeExperimental", "PlayerReviveHoldKey", new KeyboardShortcut(KeyCode.E),
                "Temporary revive key used by Phase 4.3.0-A. Default E matches normal interact/pickup on many setups; can be changed if your binding differs.");
            RequireReviveDistanceValidationOnHost = new Fixed<bool>(true);

            // Phase 4.1-A enemy state mirror — functional + tuned values hardcoded (Fixed); only the 4 Log* stay in cfg.
            EnableHostEnemyStateSnapshotMirror = new Fixed<bool>(true);
            EnemyStateSnapshotSendRateHz = new Fixed<float>(6f);
            EnemyStateSnapshotMaxEnemiesPerPacket = new Fixed<int>(4); // O3: auto-splits on byte limit.
            OnlySendAliveEnemyStateSnapshots = new Fixed<bool>(true);
            LogReceivedEnemyStateSnapshots = cfg.Bind("NetworkEnemyStateExperimental", "LogReceivedEnemyStateSnapshots", false,
                "Log Client-side matching and position-drift summaries for received Host enemy state snapshot batches. Default false to avoid log overhead during active testing.");
            ApplyReceivedEnemyStateSnapshots = new Fixed<bool>(true);
            EnemyStateSnapshotPositionTolerance = new Fixed<float>(5f);
            EnemyStateSnapshotInterpolationSpeed = new Fixed<float>(18f);
            EnemyStateSnapshotPlaybackDurationMultiplier = new Fixed<float>(1.10f);
            EnemyStateSnapshotSnapDistance = new Fixed<float>(10f);
            EnemyStateSnapshotApplyRotationY = new Fixed<bool>(true);
            EnableClientEnemyAiSuppressionExperiment = new Fixed<bool>(false); // rolled back (puppet mode replaced it).
            SuppressClientEnemyAiWhenStateMirrorEnabled = new Fixed<bool>(false);
            LogSuppressedClientEnemyAi = cfg.Bind("NetworkEnemyStateExperimental", "LogSuppressedClientEnemyAi", false,
                "Only used when EnableClientEnemyAiSuppressionExperiment=true. Log throttled Phase 4.1.0-C enemy AI suppression/probe decisions for Host-mirrored Client NPCs.");
            EnableClientEnemyPuppetMode = new Fixed<bool>(true);
            LogClientEnemyPuppetMode = cfg.Bind("NetworkEnemyStateExperimental", "LogClientEnemyPuppetMode", true,
                "Log one-line begin/end events when a Client NPC enters or leaves Host-mirrored puppet mode.");
            ClientEnemyPuppetStaleReleaseSeconds = new Fixed<float>(3f);
            EnableHostEnemyAnimationMirror = new Fixed<bool>(true);
            ApplyReceivedEnemyAnimationMirror = new Fixed<bool>(true);
            LogEnemyAnimationMirror = cfg.Bind("NetworkEnemyStateExperimental", "LogEnemyAnimationMirror", false,
                "Log throttled Host/Client enemy animation mirror state changes. Keep false unless diagnosing sliding or missing attack animations.");
            EnemyAnimationMirrorCrossFadeSeconds = new Fixed<float>(0.06f);
            EnemyAnimationMirrorNormalizedTimeTolerance = new Fixed<float>(0.30f);
            EnemyAnimationMirrorApplyAnimatorStatePlayback = new Fixed<bool>(false);
            EnemyAnimationMirrorApplyHostCombatStatePlayback = new Fixed<bool>(true);
            EnemyAnimationMirrorReplayHostCombatMethods = new Fixed<bool>(true);
            EnemyAnimationMirrorApplyCombatAnimatorFallback = new Fixed<bool>(false); // effective value (forced off).
            EnemyAnimationMirrorHostCombatActionHoldSeconds = new Fixed<float>(0.80f); // effective value (forced 0.80).

            // Enemy projectile visual mirror (retired — host snapshots/real proxy hits supersede) + synthetic ranged
            // damage gates (retired/off): hardcoded to their effective values.
            EnemyProjectileVisualMirrorEnabled = new Fixed<bool>(false);
            EnemyProjectileVisualMirrorUseNativeShootReplay = new Fixed<bool>(false);
            EnemyProjectileVisualMirrorSpeed = new Fixed<float>(26f);
            EnemyProjectileVisualMirrorLifetime = new Fixed<float>(2.0f);
            EnableGenericHostCombatAnimatorStateMirror = new Fixed<bool>(true);
            EnableHostAuthoritativeEnemyRangedDamage = new Fixed<bool>(false);
            EnableSyntheticRangedDamageFallback = new Fixed<bool>(false);
            // Phase 5.5-RT3-A5: DEFAULT OFF. Intent-driven motion keeps the local AI agent moving the puppet's transform
            // (navmesh/RichAI) WHILE the host snapshot drift also writes transform.position every frame — two competing
            // position writers that fight each other = visible flicker (log53: clientAiIntents=314 + softDrift=12381 on
            // the same entities). Off = host snapshot is the SOLE position authority (classic puppet: agent frozen);
            // walk animation is still driven from the host position delta, so nothing is lost. Re-enable to experiment.
            // Phase 4.4.0-N intent-driven motion — rolled back (5.5-RT3-A5): host snapshot is the sole position authority.
            EnableClientEnemyIntentDrivenMotion = new Fixed<bool>(false);
            LogEnemyAiIntentMirror = cfg.Bind("NetworkEnemyIntentExperimental", "LogEnemyAiIntentMirror", true,
                "Log low-frequency Host AI intent capture and Client intent replay/correction summaries.");
            EnemyIntentCorrectionDistance = new Fixed<float>(2.5f);
            EnemyIntentHardSnapDistance = new Fixed<float>(9f);
            EnemyIntentReplayMinIntervalSeconds = new Fixed<float>(0.18f);
            // Host enemy ranged-damage check params + default damage type + elemental status — hardcoded (Fixed).
            EnemyHostProjectileHitRadius = new Fixed<float>(0.75f);
            EnemyHostProjectileVerticalTolerance = new Fixed<float>(1.50f);
            EnemyHostProjectileMaxDistance = new Fixed<float>(28f);
            EnemyHostProjectileDamage = new Fixed<float>(10f);
            EnemyHostProjectileDamageCooldownSeconds = new Fixed<float>(0.45f);
            EnemyDamageDefaultType = new Fixed<int>(7);   // 7=Normal; ReceiveDamage rejects None(0).
            EnableEnemyElementalStatusEffect = new Fixed<bool>(true);
            EnemyElementalStatusAmount = new Fixed<float>(25f);
            LogEnemyHostDamageAuthority = cfg.Bind("NetworkEnemyTargetExperimental", "LogEnemyHostDamageAuthority", false,
                "Log Host authoritative enemy ranged damage checks and client-side damage applications. Default OFF: this fires once PER HIT, so on a busy fight it is synchronous per-hit disk I/O that stutters the client (confirmed LogOutput108). Turn on only to debug damage routing.");

            // Host-only enemy target/combat authority — functional hardcoded (Fixed); the 2 Log* stay in cfg.
            EnableHostOnlyEnemyTargetAuthority = new Fixed<bool>(true);
            LogEnemyTargetAuthority = cfg.Bind("NetworkEnemyTargetExperimental", "LogEnemyTargetAuthority", true,
                "Log low-frequency HostOnly enemy target authority/probe lines for Client puppet enemies.");
            EnemyTargetAuthorityProbeIntervalSeconds = new Fixed<float>(2.0f);
            EnableEnemyCombatProbe = new Fixed<bool>(true); // functional: blocks client-local puppet combat triggers.
            LogEnemyCombatProbe = cfg.Bind("NetworkEnemyTargetExperimental", "LogEnemyCombatProbe", false,
                "Default OFF — high volume: gates the per-shot [Npc] TriggerShoot/SetShooting/SetAimTarget lines + [EnemyCombatProbe]. Floods when many enemies are active (e.g. a ranged group infighting). EnableEnemyCombatProbe (functional puppet-combat blocking) stays independent. Enable to debug enemy combat.");

            // Phase 4.4.0-O host-authorized intent execution — functional + window tuning hardcoded (Fixed); Log* kept.
            EnableHostAuthorizedIntentExecution = new Fixed<bool>(true);
            HostAuthorizedIntentWindowSeconds = new Fixed<float>(1.0f);
            LogHostAuthorizedIntentExecution = cfg.Bind("NetworkEnemyIntentExperimental", "LogHostAuthorizedIntentExecution", true,
                "Log when Client puppet combat methods pass through Host-authorized intent windows.");
            EnableClientEnemyNativeDamageSuppression = new Fixed<bool>(true);
            EnableClientPuppetAimOverride = new Fixed<bool>(true);

            // Enemy snapshot delta compression — functional + thresholds hardcoded (Fixed).
            EnableEnemyStateSnapshotDeltaCompression = new Fixed<bool>(true);
            EnemyStateSnapshotHeartbeatSeconds = new Fixed<float>(0.75f);
            EnemyStateSnapshotPositionDeltaThreshold = new Fixed<float>(0.04f);
            EnemyStateSnapshotRotationDeltaThresholdDegrees = new Fixed<float>(3.0f);
            EnemyStateSnapshotAnimationTimeDeltaThreshold = new Fixed<float>(0.10f);

            // Phase 5.1 Host-authoritative enemy health sync — functional hardcoded (Fixed); Log* kept.
            EnableHostEnemyDamageEventSync = new Fixed<bool>(true);
            EnableHostEnemyHealthStateSync = new Fixed<bool>(true);
            ApplyReceivedHostEnemyHealthState = new Fixed<bool>(true);
            LogHostEnemyDamageEvents = cfg.Bind("HostDrivenProxy", "LogHostEnemyDamageEvents", true,
                "Log HostEnemyDamageEvent send (Host) and receive/apply (Client) for debugging.");
            LogHostEnemyHealthState = cfg.Bind("HostDrivenProxy", "LogHostEnemyHealthState", false,
                "Log HostEnemyHealthState send and receive. Verbose — disable once stable.");
            // Phase 5.1 P0 death-apply improvements — all functional, hardcoded (Fixed).
            AllowRosterBoundDeathDespitePositionDrift = new Fixed<bool>(true);
            HostDeathSnapBeforeApply = new Fixed<bool>(true);
            AllowDeathLateRebind = new Fixed<bool>(true);
            DisableClientEnemyDeathClaimWhenHostDamageSyncEnabled = new Fixed<bool>(true);

            // Phase 5.3-B Client → Host gameplay hit request pipeline — functional + tuning hardcoded (Fixed); Log* kept.
            EnableClientHitRequest = new Fixed<bool>(true);
            LogClientHitRequests = cfg.Bind("HostDrivenProxy", "LogClientHitRequests", true,
                "Log ClientHitRequest send (Client) and receive/apply/reject (Host) for debugging.");
            // RT3-A2/A3/A7 + SC3 + DB/DB2 + RB puppet-binding hardening + hit-request range/rate — all functional/tuning,
            // hardcoded (Fixed). (Behaviour and rationale documented in git history / EnemyActivation docs.)
            FilterNonPlayerPuppetDamage = new Fixed<bool>(true);
            MakeClientPuppetsKinematic = new Fixed<bool>(true);
            StableWorldRosterBinding = new Fixed<bool>(true);
            ReleasePuppetOnHostDeath = new Fixed<bool>(true);
            EvictStaleHostBindings = new Fixed<bool>(true);
            SkipDeadHostIdxRebind = new Fixed<bool>(true);
            EnableRetroactiveEnemyBinding = new Fixed<bool>(true);
            EnableDestroyedUnitListSweep = new Fixed<bool>(true);
            ClientHitRequestMaxRangeMeters = new Fixed<float>(30f);
            ClientHitRequestRateLimitSeconds = new Fixed<float>(0.08f);

            // Phase 5.3-C/D terminal-dead latch + hit flash + two-phase death — functional + tuning hardcoded (Fixed); Log* kept.
            EnableClientTerminalDeadLatch = new Fixed<bool>(true);
            LogClientTerminalDead = cfg.Bind("HostDrivenProxy", "LogClientTerminalDead", true,
                "Log when a host enemy is latched terminal-dead and when overrides are blocked because of it.");
            EnableClientHitFlash = new Fixed<bool>(true);
            LogClientHitFlash = cfg.Bind("HostDrivenProxy", "LogClientHitFlash", true,
                "Log visual hit-flash play/skip events for debugging.");
            ClientHitFlashDurationSeconds = new Fixed<float>(0.08f);
            EnableClientPendingDeadState = new Fixed<bool>(true);
            EnableClientDeathVisualFallback = new Fixed<bool>(true);
            ClientDeathVisualFallbackDelaySeconds = new Fixed<float>(0.25f);
            LogClientPendingDead = cfg.Bind("HostDrivenProxy", "LogClientPendingDead", true,
                "Log PendingDead mark / host-death-applied / visual-fallback events.");

            // Phase 5.3-E Host-authoritative level manifest.
            EnableHostLevelManifest = cfg.Bind("LevelManifest", "EnableHostLevelManifest", true,
                "Phase 5.3-E: Host broadcasts a semantic level manifest (seed, rooms, units, specials) after level stabilization. Client diffs its provisional local world, quarantines client-only combat enemies, and binds host enemies to local instances before runtime sync.");
            LogLevelManifest = cfg.Bind("LevelManifest", "LogLevelManifest", true,
                "Log manifest build summaries (Host built / Client built) and reconcile completion.");
            LogLevelManifestDiff = cfg.Bind("LevelManifest", "LogLevelManifestDiff", true,
                "Log detailed per-room / per-unit / per-special diff lines. Verbose — disable once the divergence cause is understood.");
            QuarantineClientOnlyManifestEnemies = cfg.Bind("LevelManifest", "QuarantineClientOnlyManifestEnemies", true,
                "Phase 5.3-E: Apply reversible quarantine (disable AI/behaviour tree) to client-only combat enemies not present in the host manifest. Never destroys them (GameManager/UnitManager/TickManager may still hold references).");

            // Phase 5.3-F ClientHit visual — functional, hardcoded (Fixed). (LevelGenTrace below is diagnostic, kept.)
            EnableClientHitVisual = new Fixed<bool>(true);
            EnableLevelGenTrace = cfg.Bind("LevelManifest", "EnableLevelGenTrace", true,
                "Phase 5.3-F: Hook LevelGeneration nodes (FinalizeConnection / Connector.FinalizeSpawn / AddExtraRoomsNode) to trace the real generation flow and locate the first host/client divergence. Discovery-first: logs real method signatures, never guesses.");
            LogLevelGenTrace = cfg.Bind("LevelManifest", "LogLevelGenTrace", true,
                "Log per-connector / per-room LevelGeneration trace lines. Verbose during generation — disable once the divergence cause is understood.");

            // Phase 5.0 Host-Driven Proxy + attack-phase events + remote-player target proxy (retired, off) — functional/
            // tuning hardcoded (Fixed) to effective values; only Log* stay in cfg.
            EnableHostDrivenEnemyProxy = new Fixed<bool>(true);
            SuppressAllClientPuppetDamage = new Fixed<bool>(true);
            LogClientPuppetDamageSuppression = cfg.Bind("HostDrivenProxy", "LogClientPuppetDamageSuppression", true,
                "Log when client puppet enemy damage is suppressed by the host-driven proxy gate.");
            EnableHostAttackPhaseEvents = new Fixed<bool>(true);
            LogHostAttackPhaseEvents = cfg.Bind("HostDrivenProxy", "LogHostAttackPhaseEvents", false,
                "Default OFF — high volume (one line per enemy attack action): logs HostAttackPhaseEvent send (Host) / receive (Client). The network broadcast itself is gated by EnableHostAttackPhaseEvents (stays on). Enable to debug attack-animation sync.");
            EnableClientAttackPhaseAnimatorDrive = new Fixed<bool>(true);
            ClientAttackPhaseCrossFadeSeconds = new Fixed<float>(0.05f);
            EnableHostProjectileVisualSpawnEvent = new Fixed<bool>(false); // P2: retired/off.
            LogHostProjectileVisualSpawn = cfg.Bind("HostDrivenProxy", "LogHostProjectileVisualSpawn", false,
                "Log HostProjectileVisualSpawn events (very noisy in ranged combat).");
            IncludeRemotePlayersInInterest = new Fixed<bool>(true);
            EnableRemotePlayerTargetProxy = new Fixed<bool>(false); // retired (Plan B ghost registry supersedes it).
            LogRemotePlayerTargetProxy = cfg.Bind("HostDrivenProxy", "LogRemotePlayerTargetProxy", true,
                "Phase 5.5-P3-A2: verbose log for remote-player target proxies (create/update/destroy).");
            RemotePlayerTargetProxySetIsPlayer = new Fixed<bool>(false);
            RemotePlayerTargetProxyForceAggro = new Fixed<bool>(true);
            RemotePlayerTargetProxyAggroRange = new Fixed<float>(30f);
            RemotePlayerTargetProxyOnlyWhenCloser = new Fixed<bool>(false);
            RemotePlayerTargetProxyHitboxLayer = new Fixed<int>(6);
            RemotePlayerTargetProxyBodyBlocker = new Fixed<bool>(false); // known AutoPool corruption when on — stays off.
            // A3.2 downed-proxy removal + P3-A3 real ReceiveDamage feedback + interest management (RB2/RB3/RB4) —
            // functional/tuning hardcoded (Fixed); the 2 Log* (incl. Debug LogEnemyInterestDiag) stay in cfg.
            RemoveTargetProxyWhenPeerDowned = new Fixed<bool>(true);
            HideDownedLocalPlayerFromEnemies = new Fixed<bool>(true);
            // Coop fair targeting: vanilla AiAgent.GetTarget (onlyTargetPlayer=false enemies) returns
            // hostilesInLOS.LastOrDefault(alive). Players are appended to hostilesInLOS in GameManager.Players index
            // order, so our injected ghost (the client) is always the LAST player => enemies always pick the client
            // whenever both players are in LOS. Rebalance to the NEAREST living player so host & client are symmetric.
            BalanceCoopEnemyTargeting = new Fixed<bool>(true);
            LogPooledObjectDestroyDiag = cfg.Bind("HostDrivenProxy", "LogPooledObjectDestroyDiag", false,
                "DIAGNOSTIC (default OFF; culprit found = AutoPool.ResetPools on level switch). Logs a stack trace whenever something destroys a still-pooled AutoPooledObject. Has a perf cost (inspects every Object.Destroy). Only enable to re-investigate pool corruption.");
            ApplyHostPlayerDamageViaReceiveDamage = new Fixed<bool>(true);
            EnableEnemyInterestManagement = new Fixed<bool>(true);
            FullRateForEngagedEnemies = new Fixed<bool>(true);
            ClientEngagedEnemyFullRateSeconds = new Fixed<float>(8f);
            ThrottleOnlyWithKnownRemotePositions = new Fixed<bool>(true);
            LogEnemyInterestDiag = cfg.Bind("Debug", "LogEnemyInterestDiag", false,
                "Diagnostic: log per-enemy interest decisions (distHost / distRemoteMin / remoteCount / engaged) and the remote-interest feed (collected positions vs host position). Throttled. Default OFF.");
            SendAllEnemySnapshotsToClients = new Fixed<bool>(true);
            // Phase 5.7-NP no-pause — functional, hardcoded (Fixed); the Debug Log*/threshold diagnostics stay in cfg.
            DisablePauseInMultiplayer = new Fixed<bool>(true);
            LogPauseSuppression = cfg.Bind("Debug", "LogPauseSuppression", false,
                "Diagnostic: log when a world-pause (inventory/ESC/F3/dialog/focus) is suppressed in multiplayer. Default OFF.");
            LogDamageApplyHitch = cfg.Bind("Debug", "LogDamageApplyHitch", false,
                "Diagnostic: time the client's per-hit host-damage apply (includes the native ReceiveDamage feedback) and log only when it exceeds DamageApplyHitchThresholdMs. Locates the 'hitch on every hit'. Default OFF.");
            DamageApplyHitchThresholdMs = cfg.Bind("Debug", "DamageApplyHitchThresholdMs", 3f,
                new ConfigDescription("Threshold (ms) above which a single client damage-apply is logged as a hitch.",
                    new AcceptableValueRange<float>(0.5f, 50f)));
            // Interest distance bands + combat-event coalescing — tuning hardcoded (Fixed).
            EnemyNearCombatDistance = new Fixed<float>(20f);
            EnemyFarDistance = new Fixed<float>(40f);
            EnemyFarSnapshotHz = new Fixed<float>(2f);
            EnableCombatEventCoalescing = new Fixed<bool>(true);
            EnemyToClientDamageCoalesceSeconds = new Fixed<float>(0.1f);
            EnemyDamageEventMinIntervalSeconds = new Fixed<float>(0.07f);
            AttackPhaseEventMinIntervalSeconds = new Fixed<float>(0.08f);

            // ----- Plan B: multiplayer enemy activation + headless Player registry -----
            EnableMultiPlayerNpcActivation = cfg.Bind("PlayerRegistry", "EnableMultiPlayerNpcActivation", false,
                "Plan B (BOTH ends): patch NpcUpdateManager.LateUpdate so enemies also wake near a REMOTE player, not just the local one. Host feeds the buffer from its ghost registry; the client feeds it from its remote-player proxies (no ghosts — its enemies are host-driven puppets). Fixes 'a player walks ahead of a stationary teammate → enemies near the teammate stay inert statues' on BOTH the host and the client. Reversible.");
            MultiPlayerNpcActivationDistance = cfg.Bind("PlayerRegistry", "MultiPlayerNpcActivationDistance", 60f,
                new ConfigDescription("Plan B: an inactive NPC within this distance of any remote player is activated (SetActive+ActivateBehaviour). The vanilla host gate uses npcActiveDistanceToPlayer (default 200); 60 keeps the wake bubble local to the client.",
                    new AcceptableValueRange<float>(10f, 200f)));
            MultiPlayerNpcActivationsPerFrame = cfg.Bind("PlayerRegistry", "MultiPlayerNpcActivationsPerFrame", 8,
                new ConfigDescription("Plan B: max NPCs activated per frame by the remote-player pass (mirrors the vanilla 16/frame budget).",
                    new AcceptableValueRange<int>(1, 64)));
            EnableRemotePlayerInPlayersList = cfg.Bind("PlayerRegistry", "EnableRemotePlayerInPlayersList", false,
                "Plan B (HOST, EXPERIMENTAL, default OFF): register each remote player as a headless Player entry in GameManager.Players so the game's multiplayer-aware detection (BatchedNPCRaycasts) targets clients natively — no faction hacks/ForceAggro needed. Inserts directly into the list (never GameManager.AddPlayer, which overwrites the host singletons). Reversible.");
            EnableGhostPlayerHitbox = cfg.Bind("PlayerRegistry", "EnableGhostPlayerHitbox", false,
                "Plan B item ① (HOST, default OFF; needs EnableRemotePlayerInPlayersList AND EnableDamageProbe — the A3 forward Unit_ReceiveDamage_Pre lives inside the damage probe prefix): give the headless ghost a Hitmesh on the enemy attack layer so enemy hits land on it and route to the client's real player via that forward. OFF = enemies path to the ghost but swing through it (client unharmed). Reversible.");
            LogRemotePlayerRegistry = cfg.Bind("PlayerRegistry", "LogRemotePlayerRegistry", true,
                "Plan B: verbose log for the headless Player registry + activation pass (create/update/destroy/register/activate).");
            SuppressGhostsWhileLoading = cfg.Bind("PlayerRegistry", "SuppressGhostsWhileLoading", true,
                "Freeze fix: do NOT register/keep headless ghost Players while the host is loading a level (GameState Loading/Uninitialized). " +
                "Vanilla LevelGeneration.ShowLevelNode iterates GameManager.Players and dereferences each one's weaponCamera/playerCamera; a " +
                "camera-less ghost re-registered mid-load throws a NullReferenceException that kills the generation coroutine -> the loading " +
                "screen hangs at the final step (17/17). Ghosts are only needed during active gameplay; they re-register once the level is Running. Reversible.");

            ApplyUnpublishedDevelopmentDefaults(cfg);

            // Last: strip the retired co-op keys (connection settings now in CoopSettings + the dropped role keys)
            // from the .cfg. Must run after every bind/.Value-set above, since each save re-emits orphaned keys.
            CoopSettingsStore.PruneRetiredCfgKeys(cfg);
        }

        private void ApplyUnpublishedDevelopmentDefaults(ConfigFile cfg)
        {
            // This mod is still private/unpublished. Connection identity settings stay user-owned (now in the
            // CoopSettings JSON store, not touched here), but hard-reset the active experimental gameplay baseline
            // so stale cfg values from earlier internal builds cannot silently re-enable old behavior.
            // EnableRunStateNegotiation + RunStateBroadcastIntervalSeconds are now hardcoded (Fixed); only Warn* stays.
            WarnOnRunStateMismatch.Value = true;
            EnableHostSceneAuthority.Value = true;
            WarnOnClientSceneDrift.Value = true;
            EnableHostSceneRequestProtocol.Value = true;
            AutoSendHostSceneRequestOnDrift.Value = true;
            HostSceneRequestIntervalSeconds.Value = 10f;
            EnableManualClientSceneFollow.Value = true;
            ManualClientSceneFollowKey.Value = new KeyboardShortcut(KeyCode.PageDown);
            ManualClientSceneFollowRequiresHostRequest.Value = true;

            // EnableLevelSeedAuthority/RequireSameLevelSeedForSceneMatch/ApplyHostLevelSeedOnManualFollow/
            // HideRemoteVisualWhenLevelSeedMismatch/SyncHostUsedSetsOnManualFollow are now hardcoded (Fixed); Log* stays.
            LogUsedSetsTrace.Value = true;
            ClientWaitHostGenerationInputBeforeFirstLoad.Value = true;
            ClientLoadGateTimeoutSeconds.Value = 30f;
            ClientLoadGateAllowFallbackAfterTimeout.Value = false;
            ClientLoadGateRequestIntervalSeconds.Value = 2f;
            ClientGateDeathRespawnUntilHostHub.Value = true;
            ClientGateDeathRespawnTimeoutSeconds.Value = 12f;
            EnableClientTransitionRelay.Value = true;
            AllowClientInitiatedLevelLoad.Value = true;
            ClientInitiatedLoadTimeoutSeconds.Value = 15f;
            EnableClientReloadInPlaceRelay.Value = true;
            ClientLinkedByDefault.Value = false;
            HostLinkedByDefault.Value = true;
            ClientUnlinkKey.Value = new KeyboardShortcut(KeyCode.PageUp);
            HostLinkToggleKey.Value = new KeyboardShortcut(KeyCode.PageDown);

            // Phase 5.3-M P1 auto-follow + load barrier — test defaults on (log/status only barrier).
            EnableAutoFollowHostSceneRequest.Value = true;
            EnableLoadBarrier.Value = true;
            LoadBarrierTimeoutSeconds.Value = 30f;
            LoadBarrierBlockHostAdvance.Value = false;
            LoadBarrierLogOnlyMode.Value = true;

            // Phase 5.4-E Boss encounter authority — test default on (start handshake only).
            EnableBossEncounterSync.Value = true;
            BossEncounterClientBlockLocalStart.Value = true;
            LogBossEncounter.Value = true;

            // Phase 5.4-E2 BossStart chain completion + lifecycle probe — diagnostics default on (safe; no global AI suppression).
            BossContinuationGraceSeconds.Value = 5f;
            EnableBossLifecycleProbe.Value = true;
            LogBossLifecycle.Value = true;
            LogBossPreFight.Value = true;
            // Faithful intro runs the boss's own intro dialog on the client; we must NOT pre-remove its dialog, so keep this off while faithful intro is on.
            RemoveBossDialogInteractableOnStart.Value = false;
            EnableFaithfulBossIntro.Value = true;
            GateBossFightOnDialogClose.Value = true;
            DeferBossIntroArm.Value = true;
            EnableBossRoomMembership.Value = true;
            GateBossDialogToInRoom.Value = true;
            ExcludeOutOfRoomPlayersFromBossAttacks.Value = true;
            // Plan B enemy activation + targeting (VERIFIED WORKING — Docs/EnemyActivationAndPlayersRegistry.md).
            // All three are required for a client to fight enemies ahead of a stationary host:
            //  · activation postfix wakes NPCs near any remote player (the "won't wake" fix);
            //  · the headless ghost Player registers the client in GameManager.Players so host enemies natively
            //    detect/aggro/target it — WITHOUT this the client is no one's target on the host, so host enemies
            //    stand idle and the client's puppets mirror that idle = the 站桩 regression;
            //  · the ghost hitbox routes enemy hits to the client (needs EnableDamageProbe, which is bind-default true).
            // Previously these lived only in a local .cfg; promoted here so a fresh/deleted config (and release) work.
            EnableMultiPlayerNpcActivation.Value = true;
            EnableRemotePlayerInPlayersList.Value = true;
            EnableGhostPlayerHitbox.Value = true;

            // Phase 5.4-E3 — dialog commit + Lucia + Witch state default on; Emperor worm DIAGNOSTIC on, SUPPRESSION off (reversible).
            EnableEmperorWormDiagnostics.Value = true;
            EnableEmperorClientWormSuppression.Value = false;
            LogBossTransitionDiagnostics.Value = true;

            // Phase 5.4-E4 — boss dynamic spawn manifest diagnostics default on.
            EnableBossDynamicSpawnManifest.Value = true;
            LogBossDynamicSpawn.Value = true;
            // Phase RT3-Cousin-arms — route GoblinCousinArm through the RT3-A boss-add pipeline default on.
            EnableCousinArmSync.Value = true;

            // Phase 5.4-F — boss main-body damage authority default on.
            EnableBossDamageAuthority.Value = true;
            // Phase 5.4-F2 — ROLLED BACK (LogOutput29): Cousin AI activation + Desert intro-skip both proven wrong.
            EnableBossClientPresentation.Value = false;
            // Phase 5.4-F4 — Cousin fixed-point pool authority default on.
            EnableBossDiscreteEventAuthority.Value = true;
            // Phase 5.4-F5 — Lucia eye defeat authority default on.
            EnableLuciaEyeAuthority.Value = true;
            // Phase 5.4-F6 — Lucia terminal death authority default on.
            EnableLuciaDeathAuthority.Value = true;
            // Phase 5.4-G — Witch phase-witch damage authority default on.
            EnableWitchPhaseDamageAuthority.Value = true;
            // Phase 5.4-G2 — Witch phase revision authority default on.
            EnableWitchPhaseAuthority.Value = true;
            // Phase 5.4-G4 — Witch Phase 2 timing probe default on (diagnostic).
            LogWitchPhase2Probe.Value = true;
            // Phase 5.4-G5 — Witch Phase 2 dome manifest authority default on.
            EnableWitchPhase2Manifest.Value = true;
            // Phase 5.4-G7 — Witch death cleanup default on.
            EnableWitchDeathFix.Value = true;
            // Phase 5.5-RT1 / RT3-A / 5.7-DS / DS2 — runtime/death/minion spawn sync Enable* now hardcoded (Fixed); Log* stays.
            LogRuntimeSpawnSync.Value = true;
            // Phase 5.6-WS — EnablePlayerWeaponSync + max-projectiles hardcoded (Fixed); Log* stays.
            LogPlayerWeaponSync.Value = true;
            // Phase 5.7-BR / LD-1 / LD-1b — Enable* flags are now hardcoded (Fixed<bool>); only their Log* stay forced.
            LogBreakableSync.Value = true;
            LogGateSync.Value = true;
            LogTriggerDoorSync.Value = true;
            // Phase LD-2 — arena lockdown membership + timer + force-seal barrier + teleport default on.
            EnableArenaLockdown.Value = true;
            LogArenaLockdown.Value = true;
            ArenaEnterConfirmKey.Value = new KeyboardShortcut(KeyCode.Return);
            EnableArenaGracePeriod.Value = true;
            // World item-drop sync default on (player-thrown items); shared-loot widening stays off until host-roll exists.
            EnableWorldItemDropSync.Value = true;
            LogWorldItemDropSync.Value = true;
            ShareAllLoot.Value = false;
            // Phase 5.6-WS-2/WS-3 — remote weapon model + sprite body Enable* + all appearance values now hardcoded
            // (Fixed); only the Log* flags stay forced/in cfg.
            LogRemoteWeaponModel.Value = true;
            LogPlayerVisualDiscovery.Value = false;
            LogPlayerSpriteAssetScan.Value = false;
            LogRemotePlayerBody.Value = true;

            // NetworkVisualProxy — all hardcoded now (Fixed); nothing to force here.

            EnableGameplayEntityProbe.Value = true;
            GameplayEntityProbeSummaryIntervalSeconds.Value = 10f;
            LogGameplayEntitySpawn.Value = true;
            LogGameplayEntityDamage.Value = false;
            LogGameplayEntityDeath.Value = true;
            RequireStableSceneAndSeedForGameplayProbe.Value = true;

            // Phase 4.0-B enemy death mirror — Enable*/Apply*/tolerance now hardcoded (Fixed); only Log* stays forced.
            LogReceivedEnemyDeathEvents.Value = true;
            LogReceivedClientEnemyDeathClaims.Value = true;

            // Downed/revive Enable* + tuning now hardcoded (Fixed); only the Log* and the keybind stay forced/bound.
            LogPlayerLifeSync.Value = true;
            PlayerReviveHoldKey.Value = new KeyboardShortcut(KeyCode.E);

            // NetworkEnemyStateExperimental — Enable*/Apply*/tuning all hardcoded (Fixed); only the 4 Log* stay forced.
            LogReceivedEnemyStateSnapshots.Value = false;
            LogSuppressedClientEnemyAi.Value = false;
            LogClientEnemyPuppetMode.Value = true;
            LogEnemyAnimationMirror.Value = false;
            // NetworkEnemyTargetExperimental projectile-visual + ranged-damage gates now hardcoded (Fixed).
            // Phase 5.5-RT3-A5: host snapshot is the single position authority (no flicker from competing writers).
            // Intent-driven motion + correction/snap tuning now hardcoded (Fixed); only LogEnemyAiIntentMirror stays.
            LogEnemyAiIntentMirror.Value = true;
            // Host enemy ranged-damage params + default type + elemental status now hardcoded (Fixed).
            // Phase 5.7-HG: LogEnemyHostDamageAuthority is now Bind-default OFF and intentionally NOT forced here, so the
            // user can toggle it from the .cfg (per the Log* convention). It fires once per hit = per-hit disk I/O.
            // Keep the threshold hitch probe auto-on this investigation round (threshold-gated → no per-hit flood; remove once root-caused).
            LogDamageApplyHitch.Value = true;
            DamageApplyHitchThresholdMs.Value = 3f;
            // EnableHostOnlyEnemyTargetAuthority + probe interval + EnableEnemyCombatProbe hardcoded (Fixed); Log* stays.
            LogEnemyTargetAuthority.Value = true;
            // Host-authorized intent execution Enable*/window now hardcoded (Fixed); only Log* stays.
            LogHostAuthorizedIntentExecution.Value = true;
            // RT3-A2/A3/A7 + SC3 + DB/DB2 + RB puppet-binding flags now hardcoded (Fixed).
            // RB2/RB3/RB4 interest + NP no-pause Enable*/tuning now hardcoded (Fixed); only the Log* diags stay forced.
            LogEnemyInterestDiag.Value = true;
            LogPauseSuppression.Value = true;

            // Enemy snapshot delta-compression — all hardcoded (Fixed); nothing to force here.

            // Phase 5.3-B/C/D — hit-request + terminal-dead/flash + two-phase-death Enable*/tuning hardcoded (Fixed); Log* only.
            LogClientHitRequests.Value = true;
            LogClientTerminalDead.Value = true;
            LogClientHitFlash.Value = true;
            LogClientPendingDead.Value = true;

            // Phase 5.3-E level manifest — on by default; diagnostics + quarantine enabled.
            EnableHostLevelManifest.Value = true;
            LogLevelManifest.Value = true;
            LogLevelManifestDiff.Value = true;
            QuarantineClientOnlyManifestEnemies.Value = true;

            // Phase 5.3-F — EnableClientHitVisual hardcoded (Fixed); LevelGenTrace (diagnostic) stays.
            EnableLevelGenTrace.Value = true;
            LogLevelGenTrace.Value = true;

            // Phase 5.1 Host-authoritative enemy health sync — Enable*/Apply*/death-apply hardcoded (Fixed); Log* only.
            LogHostEnemyDamageEvents.Value = true;
            LogHostEnemyHealthState.Value = false;

            // Phase 5.0 Host-Driven Proxy + attack-phase + interest bands — Enable*/tuning hardcoded (Fixed); only Log* stay.
            LogClientPuppetDamageSuppression.Value = true;
            LogHostProjectileVisualSpawn.Value = false;

            cfg.Save();
        }
    }
}
