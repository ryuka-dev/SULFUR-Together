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
        public ConfigEntry<bool>   EnablePlayerWeaponSync { get; }
        public ConfigEntry<bool>   LogPlayerWeaponSync { get; }
        public ConfigEntry<int>    PlayerWeaponSyncMaxProjectilesPerShot { get; }

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
        public ConfigEntry<bool>   EnableRemoteWeaponModel { get; }
        public ConfigEntry<bool>   LogRemoteWeaponModel { get; }

        // ----- Phase 5.6-WS-3 player visual (billboard sprite) discovery probe -----
        public ConfigEntry<bool>   LogPlayerVisualDiscovery { get; }
        public ConfigEntry<bool>   LogPlayerSpriteAssetScan { get; }

        // ----- Phase 5.6-WS-3 remote player billboard body -----
        // Priest (Father) sprite body (embedded front/back walk sheets) takes priority; NPC-prefab body is the fallback.
        public ConfigEntry<bool>   EnableRemotePlayerSpriteBody { get; }
        public ConfigEntry<bool>   EnableRemotePlayerNpcBody { get; }
        public ConfigEntry<string> RemotePlayerBodyUnitKeyword { get; }
        public ConfigEntry<bool>   LogRemotePlayerBody { get; }
        public ConfigEntry<float>  RemoteBodyScale { get; }
        public ConfigEntry<float>  RemoteBodyFeetYOffset { get; }
        public ConfigEntry<float>  RemoteWeaponScale { get; }
        public ConfigEntry<float>  RemoteWeaponHipHeight { get; }
        public ConfigEntry<float>  RemoteWeaponForward { get; }
        public ConfigEntry<float>  RemoteWeaponRight { get; }
        public ConfigEntry<float>  RemoteBodyPitchLimit { get; }
        public ConfigEntry<float>  RemoteBodyDepthBias { get; }
        public ConfigEntry<float>  RemoteNameSize { get; }
        public ConfigEntry<float>  RemoteNameHeight { get; }

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

        // ----- Phase 4.1-A host enemy state snapshot mirror experiment -----
        public ConfigEntry<bool>   EnableHostEnemyStateSnapshotMirror { get; }
        public ConfigEntry<float>  EnemyStateSnapshotSendRateHz { get; }
        public ConfigEntry<int>    EnemyStateSnapshotMaxEnemiesPerPacket { get; }
        public ConfigEntry<bool>   OnlySendAliveEnemyStateSnapshots { get; }
        public ConfigEntry<bool>   LogReceivedEnemyStateSnapshots { get; }
        public ConfigEntry<bool>   ApplyReceivedEnemyStateSnapshots { get; }
        public ConfigEntry<float>  EnemyStateSnapshotPositionTolerance { get; }
        public ConfigEntry<float>  EnemyStateSnapshotInterpolationSpeed { get; }
        public ConfigEntry<float>  EnemyStateSnapshotPlaybackDurationMultiplier { get; }
        public ConfigEntry<float>  EnemyStateSnapshotSnapDistance { get; }
        public ConfigEntry<bool>   EnemyStateSnapshotApplyRotationY { get; }
        public ConfigEntry<bool>   EnableClientEnemyAiSuppressionExperiment { get; }
        public ConfigEntry<bool>   SuppressClientEnemyAiWhenStateMirrorEnabled { get; }
        public ConfigEntry<bool>   LogSuppressedClientEnemyAi { get; }
        public ConfigEntry<bool>   EnableClientEnemyPuppetMode { get; }
        public ConfigEntry<bool>   LogClientEnemyPuppetMode { get; }
        public ConfigEntry<float>  ClientEnemyPuppetStaleReleaseSeconds { get; }
        public ConfigEntry<bool>   EnableHostEnemyAnimationMirror { get; }
        public ConfigEntry<bool>   ApplyReceivedEnemyAnimationMirror { get; }
        public ConfigEntry<bool>   LogEnemyAnimationMirror { get; }
        public ConfigEntry<float>  EnemyAnimationMirrorCrossFadeSeconds { get; }
        public ConfigEntry<float>  EnemyAnimationMirrorNormalizedTimeTolerance { get; }
        public ConfigEntry<bool>   EnemyAnimationMirrorApplyAnimatorStatePlayback { get; }
        public ConfigEntry<bool>   EnemyAnimationMirrorApplyHostCombatStatePlayback { get; }
        public ConfigEntry<bool>   EnemyAnimationMirrorReplayHostCombatMethods { get; }
        public ConfigEntry<bool>   EnemyAnimationMirrorApplyCombatAnimatorFallback { get; }
        public ConfigEntry<float>  EnemyAnimationMirrorHostCombatActionHoldSeconds { get; }
        public ConfigEntry<bool>   EnemyProjectileVisualMirrorEnabled { get; }
        public ConfigEntry<bool>   EnemyProjectileVisualMirrorUseNativeShootReplay { get; }
        public ConfigEntry<float>  EnemyProjectileVisualMirrorSpeed { get; }
        public ConfigEntry<float>  EnemyProjectileVisualMirrorLifetime { get; }
        public ConfigEntry<bool>   EnableGenericHostCombatAnimatorStateMirror { get; }
        public ConfigEntry<bool>   EnableHostAuthoritativeEnemyRangedDamage { get; }
        public ConfigEntry<bool>   EnableSyntheticRangedDamageFallback { get; }
        public ConfigEntry<bool>   EnableClientEnemyIntentDrivenMotion { get; }
        public ConfigEntry<bool>   LogEnemyAiIntentMirror { get; }
        public ConfigEntry<float>  EnemyIntentCorrectionDistance { get; }
        public ConfigEntry<float>  EnemyIntentHardSnapDistance { get; }
        public ConfigEntry<float>  EnemyIntentReplayMinIntervalSeconds { get; }
        public ConfigEntry<float>  EnemyHostProjectileHitRadius { get; }
        public ConfigEntry<float>  EnemyHostProjectileVerticalTolerance { get; }
        public ConfigEntry<float>  EnemyHostProjectileMaxDistance { get; }
        public ConfigEntry<float>  EnemyHostProjectileDamage { get; }
        public ConfigEntry<float>  EnemyHostProjectileDamageCooldownSeconds { get; }
        public ConfigEntry<int>    EnemyDamageDefaultType { get; }
        public ConfigEntry<bool>   EnableEnemyElementalStatusEffect { get; }
        public ConfigEntry<float>  EnemyElementalStatusAmount { get; }
        public ConfigEntry<bool>   LogEnemyHostDamageAuthority { get; }

        // ----- Phase 4.4.0-H host-authoritative enemy target/combat probe -----
        public ConfigEntry<bool>   EnableHostOnlyEnemyTargetAuthority { get; }
        public ConfigEntry<bool>   LogEnemyTargetAuthority { get; }
        public ConfigEntry<float>  EnemyTargetAuthorityProbeIntervalSeconds { get; }
        public ConfigEntry<bool>   EnableEnemyCombatProbe { get; }
        public ConfigEntry<bool>   LogEnemyCombatProbe { get; }

        // ----- Phase 4.4.0-O host-authorized enemy intent execution -----
        public ConfigEntry<bool>  EnableHostAuthorizedIntentExecution { get; }
        public ConfigEntry<float> HostAuthorizedIntentWindowSeconds { get; }
        public ConfigEntry<bool>  LogHostAuthorizedIntentExecution { get; }
        public ConfigEntry<bool>  EnableClientEnemyNativeDamageSuppression { get; }
        public ConfigEntry<bool>  EnableClientPuppetAimOverride { get; }

        public ConfigEntry<bool>   EnableEnemyStateSnapshotDeltaCompression { get; }
        public ConfigEntry<float>  EnemyStateSnapshotHeartbeatSeconds { get; }
        public ConfigEntry<float>  EnemyStateSnapshotPositionDeltaThreshold { get; }
        public ConfigEntry<float>  EnemyStateSnapshotRotationDeltaThresholdDegrees { get; }
        public ConfigEntry<float>  EnemyStateSnapshotAnimationTimeDeltaThreshold { get; }

        // ----- Phase 5.1 Host-authoritative enemy health sync -----
        // P0: Host broadcasts damage events; client tracks puppet health and late-binds deaths.
        public ConfigEntry<bool>   EnableHostEnemyDamageEventSync { get; }
        public ConfigEntry<bool>   EnableHostEnemyHealthStateSync { get; }
        public ConfigEntry<bool>   ApplyReceivedHostEnemyHealthState { get; }
        public ConfigEntry<bool>   LogHostEnemyDamageEvents { get; }
        public ConfigEntry<bool>   LogHostEnemyHealthState { get; }
        // P0: Death apply improvements — roster-bound deaths bypass position-drift rejection.
        public ConfigEntry<bool>   AllowRosterBoundDeathDespitePositionDrift { get; }
        public ConfigEntry<bool>   HostDeathSnapBeforeApply { get; }
        public ConfigEntry<bool>   AllowDeathLateRebind { get; }
        // P0: Suppress client-local death claims when host is authoritative on damage.
        public ConfigEntry<bool>   DisableClientEnemyDeathClaimWhenHostDamageSyncEnabled { get; }

        // ----- Phase 5.3-B Client → Host gameplay request pipeline -----
        public ConfigEntry<bool>   EnableClientHitRequest  { get; }
        public ConfigEntry<bool>   LogClientHitRequests    { get; }
        // Phase 5.5-RT3-A2: only forward LOCAL-PLAYER damage on a host-driven puppet; ignore physics/environment.
        public ConfigEntry<bool>   FilterNonPlayerPuppetDamage { get; }
        // Phase 5.5-RT3-A3: make host-driven puppets kinematic so transform-drags don't impart physics impulses.
        public ConfigEntry<bool>   MakeClientPuppetsKinematic { get; }
        // Phase 5.5-RT3-A7: keep a WorldRoster binding stable once established (don't re-match by position each revision).
        public ConfigEntry<bool>   StableWorldRosterBinding { get; }
        // Phase 5.7-SC3: when a host enemy's death is applied, release the bound client puppet + drop its binding so a
        // host-despawned enemy doesn't linger as a stale "host-bound" standing zombie on the client.
        public ConfigEntry<bool>   ReleasePuppetOnHostDeath { get; }
        // Phase 5.7-DB: keep the client hostIdx↔localKey binding maps strictly 1:1; release orphaned (disowned) host-bound puppets.
        public ConfigEntry<bool>   EvictStaleHostBindings { get; }
        // Phase 5.7-DB2: never (re)bind a host idx the client has already buried; release puppets stuck on a dead host idx.
        public ConfigEntry<bool>   SkipDeadHostIdxRebind { get; }
        // Phase 5.7-RB: retro-actively bind a host enemy whose roster/manifest record arrived before the client spawned it.
        public ConfigEntry<bool>   EnableRetroactiveEnemyBinding { get; }
        // Phase 5.7-RB: sweep destroyed (Unity-null) entries out of GameManager.units/aliveNpcs before the vanilla raycast Update.
        public ConfigEntry<bool>   EnableDestroyedUnitListSweep  { get; }
        public ConfigEntry<float>  ClientHitRequestMaxRangeMeters  { get; }
        public ConfigEntry<float>  ClientHitRequestRateLimitSeconds { get; }

        // ----- Phase 5.3-C/D Terminal dead latch + visual hit flash -----
        public ConfigEntry<bool>   EnableClientTerminalDeadLatch { get; }
        public ConfigEntry<bool>   LogClientTerminalDead         { get; }
        public ConfigEntry<bool>   EnableClientHitFlash          { get; }
        public ConfigEntry<bool>   LogClientHitFlash             { get; }
        public ConfigEntry<float>  ClientHitFlashDurationSeconds { get; }
        // ----- Phase 5.3-D two-phase death (PendingDead → TerminalDead) -----
        public ConfigEntry<bool>   EnableClientPendingDeadState        { get; }
        public ConfigEntry<bool>   EnableClientDeathVisualFallback     { get; }
        public ConfigEntry<float>  ClientDeathVisualFallbackDelaySeconds { get; }
        public ConfigEntry<bool>   LogClientPendingDead                { get; }

        // ----- Phase 5.3-E Host-authoritative level manifest -----
        public ConfigEntry<bool>   EnableHostLevelManifest             { get; }
        public ConfigEntry<bool>   LogLevelManifest                    { get; }
        public ConfigEntry<bool>   LogLevelManifestDiff                { get; }
        public ConfigEntry<bool>   QuarantineClientOnlyManifestEnemies { get; }

        // ----- Phase 5.3-F ClientHit visual + LevelGeneration trace -----
        public ConfigEntry<bool>   EnableClientHitVisual               { get; }
        public ConfigEntry<bool>   EnableLevelGenTrace                 { get; }
        public ConfigEntry<bool>   LogLevelGenTrace                    { get; }

        // ----- Phase 5.0 Host-Driven Proxy Architecture -----
        // P0: CombatEnemy client proxy — suppression and identity.
        public ConfigEntry<bool>   EnableHostDrivenEnemyProxy { get; }
        public ConfigEntry<bool>   SuppressAllClientPuppetDamage { get; }
        public ConfigEntry<bool>   LogClientPuppetDamageSuppression { get; }
        // P1: Attack phase reliable event channel.
        public ConfigEntry<bool>   EnableHostAttackPhaseEvents { get; }
        public ConfigEntry<bool>   LogHostAttackPhaseEvents { get; }
        public ConfigEntry<bool>   EnableClientAttackPhaseAnimatorDrive { get; }
        public ConfigEntry<float>  ClientAttackPhaseCrossFadeSeconds { get; }
        // P2: Projectile visual spawn event (reliable).
        public ConfigEntry<bool>   EnableHostProjectileVisualSpawnEvent { get; }
        public ConfigEntry<bool>   LogHostProjectileVisualSpawn { get; }
        // P2: Interest management — distance-based snapshot rate reduction.
        public ConfigEntry<bool>   EnableEnemyInterestManagement { get; }
        public ConfigEntry<bool>   IncludeRemotePlayersInInterest { get; }
        // Phase 5.7-RB2: an enemy that is actively engaged (has a combat target, or a client hit it recently) must sync at
        // full snapshot rate even when far from the Host player — otherwise its client puppet freezes mid-fight.
        public ConfigEntry<bool>   FullRateForEngagedEnemies { get; }
        public ConfigEntry<float>  ClientEngagedEnemyFullRateSeconds { get; }
        // Phase 5.7-RB3: only far-throttle an enemy when we actually have a remote-player position to prove it is far from
        // the client too. If the interest feed is momentarily empty, never throttle (a client is connected by definition here).
        public ConfigEntry<bool>   ThrottleOnlyWithKnownRemotePositions { get; }
        public ConfigEntry<bool>   LogEnemyInterestDiag { get; }
        // Phase 5.7-RB4: enemy positions are gameplay-critical; while a client is connected, never drop them via the
        // distance interest heuristic (which starves enemies a far-roaming client is fighting → frozen puppets). Delta
        // compression still bounds bandwidth (stationary enemies only heartbeat). The distance throttle was a premature
        // optimization that the (currently broken) remote-interest feed cannot safely support.
        public ConfigEntry<bool>   SendAllEnemySnapshotsToClients { get; }
        // Phase 5.7-NP: Minecraft-LAN-style — while in a co-op session, do not stop world time (inventory / ESC menu /
        // F3 dev tools / dialog / lost window focus). Pausing one side desyncs boss timelines and freezes the other's view.
        public ConfigEntry<bool>   DisablePauseInMultiplayer { get; }
        public ConfigEntry<bool>   LogPauseSuppression { get; }
        // Phase 5.7-HG: measure per-hit client damage-apply cost (only logs when it exceeds the threshold → no per-hit flood).
        public ConfigEntry<bool>   LogDamageApplyHitch { get; }
        public ConfigEntry<float>  DamageApplyHitchThresholdMs { get; }
        // Phase 5.5-P3-A2: host spawns a faction=Player targetable Unit proxy at each remote player so enemies aggro clients.
        public ConfigEntry<bool>   EnableRemotePlayerTargetProxy { get; }
        public ConfigEntry<bool>   LogRemotePlayerTargetProxy { get; }
        public ConfigEntry<bool>   RemotePlayerTargetProxySetIsPlayer { get; }
        public ConfigEntry<bool>   RemotePlayerTargetProxyForceAggro { get; }
        public ConfigEntry<float>  RemotePlayerTargetProxyAggroRange { get; }
        public ConfigEntry<bool>   RemotePlayerTargetProxyOnlyWhenCloser { get; }
        public ConfigEntry<int>    RemotePlayerTargetProxyHitboxLayer { get; }
        public ConfigEntry<bool>   RemotePlayerTargetProxyBodyBlocker { get; }
        public ConfigEntry<bool>   RemoveTargetProxyWhenPeerDowned { get; }
        public ConfigEntry<bool>   HideDownedLocalPlayerFromEnemies { get; }
        public ConfigEntry<bool>   LogPooledObjectDestroyDiag { get; }
        public ConfigEntry<bool>   ApplyHostPlayerDamageViaReceiveDamage { get; }
        public ConfigEntry<float>  EnemyNearCombatDistance { get; }
        public ConfigEntry<float>  EnemyFarDistance { get; }
        public ConfigEntry<float>  EnemyFarSnapshotHz { get; }
        public ConfigEntry<bool>   EnableCombatEventCoalescing { get; }
        public ConfigEntry<float>  EnemyToClientDamageCoalesceSeconds { get; }
        public ConfigEntry<float>  EnemyDamageEventMinIntervalSeconds { get; }
        public ConfigEntry<float>  AttackPhaseEventMinIntervalSeconds { get; }
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
            EnablePlayerWeaponSync = cfg.Bind("PlayerWeapon", "EnablePlayerWeaponSync", true,
                "Phase 5.6-WS: show other players' weapon fire (full barrage incl. multi-projectile/tracking/laser/rocket) as visual-only bullets. Damage stays host-authoritative. Reversible.");
            LogPlayerWeaponSync = cfg.Bind("PlayerWeapon", "LogPlayerWeaponSync", true,
                "Phase 5.6-WS: verbose log for player weapon sync (capture / broadcast / replay).");
            PlayerWeaponSyncMaxProjectilesPerShot = cfg.Bind("PlayerWeapon", "PlayerWeaponSyncMaxProjectilesPerShot", 256,
                "Phase 5.6-WS: safety clamp on visual projectiles replayed per fire event (huge modded barrages). Local damage is unaffected.");

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
            EnableRemoteWeaponModel = cfg.Bind("PlayerWeapon", "EnableRemoteWeaponModel", true,
                "Phase 5.6-WS-2: display the remote player's held weapon model (with attachments) in their proxy's hands. Visual only. Reversible.");
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
            EnableRemotePlayerSpriteBody = cfg.Bind("PlayerWeapon", "EnableRemotePlayerSpriteBody", true,
                "Phase 5.6-WS-3: show remote players as the priest (Father) billboard sprite (embedded front/back walk sheets, faces camera, front/back by facing). Takes priority over the NPC-prefab body. Reversible.");
            EnableRemotePlayerNpcBody = cfg.Bind("PlayerWeapon", "EnableRemotePlayerNpcBody", false,
                "Phase 5.6-WS-3 fallback: show remote players as an NPC billboard paper sprite instead of a capsule (used only if the Father sprite body is disabled/unavailable). Reversible.");
            RemotePlayerBodyUnitKeyword = cfg.Bind("PlayerWeapon", "RemotePlayerBodyUnitKeyword", "civilian,grocer,scholar,arthur,telia,citizen,man,woman",
                "Phase 5.6-WS-3: comma-separated name keywords; the first humanoid UnitSO whose name/displayName matches is reused as the remote player body. First match wins (in keyword order).");
            LogRemotePlayerBody = cfg.Bind("PlayerWeapon", "LogRemotePlayerBody", true,
                "Phase 5.6-WS-3: verbose log for the remote player NPC billboard body (resolve / load / build / attach).");
            RemoteBodyScale = cfg.Bind("PlayerWeapon", "RemoteBodyScale", 1.6f,
                "Phase 5.6-WS-3: uniform scale of the remote player billboard body (tune apparent height).");
            RemoteBodyFeetYOffset = cfg.Bind("PlayerWeapon", "RemoteBodyFeetYOffset", 0.0f,
                "Phase 5.6-WS-3: vertical offset (metres) of the body's feet relative to the remote player's ground position.");
            RemoteWeaponScale = cfg.Bind("PlayerWeapon", "RemoteWeaponScale", 1.4f,
                "Phase 5.6-WS-3: uniform scale of the remote player's held weapon model.");
            RemoteWeaponHipHeight = cfg.Bind("PlayerWeapon", "RemoteWeaponHipHeight", 1.2f,
                "Phase 5.6-WS-3: height (metres above feet) at which the held weapon is carried (waist level).");
            RemoteWeaponForward = cfg.Bind("PlayerWeapon", "RemoteWeaponForward", 0.30f,
                "Phase 5.6-WS-3: how far forward (metres, along look direction) the held weapon sits from the body.");
            RemoteWeaponRight = cfg.Bind("PlayerWeapon", "RemoteWeaponRight", 0.375f,
                "Phase 5.6-WS-3: how far to the right (metres) the held weapon sits.");
            RemoteBodyPitchLimit = cfg.Bind("PlayerWeapon", "RemoteBodyPitchLimit", 25f,
                "Phase 5.6-WS-3: max pitch (degrees) the body billboard tilts up/down toward the camera (like NPC sprites).");
            RemoteBodyDepthBias = cfg.Bind("PlayerWeapon", "RemoteBodyDepthBias", 0.0f,
                "Phase 5.6-WS-3: depth nudge (metres) for the body vs weapon. 0 = same layer (weapon intersects the paper sprite).");
            RemoteNameSize = cfg.Bind("PlayerWeapon", "RemoteNameSize", 0.03f,
                "Phase 5.6-WS-3: name label TextMesh character size (world).");
            RemoteNameHeight = cfg.Bind("PlayerWeapon", "RemoteNameHeight", 0.45f,
                "Phase 5.6-WS-3: extra height (metres) of the name label above the head (margin/gap).");

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

            // Phase 4.1-A enemy state mirror. Network matching/drift measurement only by default; no movement or AI mutation.
            EnableHostEnemyStateSnapshotMirror = cfg.Bind("NetworkEnemyStateExperimental", "EnableHostEnemyStateSnapshotMirror", true,
                "Experimental/current baseline: Host periodically sends enemy state snapshots to Clients. Default true for active multiplayer testing.");
            EnemyStateSnapshotSendRateHz = cfg.Bind("NetworkEnemyStateExperimental", "EnemyStateSnapshotSendRateHz", 6f,
                new ConfigDescription("How many Host enemy state snapshot batches to send per second. Phase 4.1.0-D default is 6Hz to reduce visible step-wise enemy motion.",
                    new AcceptableValueRange<float>(0.1f, 20f)));
            EnemyStateSnapshotMaxEnemiesPerPacket = cfg.Bind("NetworkEnemyStateExperimental", "EnemyStateSnapshotMaxEnemiesPerPacket", 16,
                new ConfigDescription("Maximum NPC snapshots included in one HostEnemyStateSnapshot packet chunk. Phase 4.4.0-D compact wire format can safely batch more enemies per packet than the old string-heavy format.",
                    new AcceptableValueRange<int>(1, 64)));
            OnlySendAliveEnemyStateSnapshots = cfg.Bind("NetworkEnemyStateExperimental", "OnlySendAliveEnemyStateSnapshots", true,
                "When true, Host state snapshot batches exclude entities already marked dead by the gameplay probe.");
            LogReceivedEnemyStateSnapshots = cfg.Bind("NetworkEnemyStateExperimental", "LogReceivedEnemyStateSnapshots", false,
                "Log Client-side matching and position-drift summaries for received Host enemy state snapshot batches. Default false to avoid log overhead during active testing.");
            ApplyReceivedEnemyStateSnapshots = cfg.Bind("NetworkEnemyStateExperimental", "ApplyReceivedEnemyStateSnapshots", true,
                "Experimental/current baseline: Client smoothly mirrors matched local enemy transforms toward Host enemy state snapshots. This does not disable local AI or change attacks/damage.");
            EnemyStateSnapshotPositionTolerance = cfg.Bind("NetworkEnemyStateExperimental", "EnemyStateSnapshotPositionTolerance", 5f,
                "Position drift threshold in meters used by Client logs when comparing local enemies against Host enemy state snapshots.");
            EnemyStateSnapshotInterpolationSpeed = cfg.Bind("NetworkEnemyStateExperimental", "EnemyStateSnapshotInterpolationSpeed", 18f,
                new ConfigDescription("How quickly Client enemy transforms move toward the current Host snapshot playback position when ApplyReceivedEnemyStateSnapshots=true.",
                    new AcceptableValueRange<float>(1f, 60f)));
            EnemyStateSnapshotPlaybackDurationMultiplier = cfg.Bind("NetworkEnemyStateExperimental", "EnemyStateSnapshotPlaybackDurationMultiplier", 1.10f,
                new ConfigDescription("Phase 4.1.0-D: Client plays each Host enemy movement segment over the observed snapshot interval multiplied by this value. Slightly above 1.0 reduces stop-and-go movement between packets.",
                    new AcceptableValueRange<float>(0.5f, 2.5f)));
            EnemyStateSnapshotSnapDistance = cfg.Bind("NetworkEnemyStateExperimental", "EnemyStateSnapshotSnapDistance", 10f,
                new ConfigDescription("Distance in meters above which a matched Client enemy snaps directly to the Host snapshot position instead of interpolating.",
                    new AcceptableValueRange<float>(1f, 100f)));
            EnemyStateSnapshotApplyRotationY = cfg.Bind("NetworkEnemyStateExperimental", "EnemyStateSnapshotApplyRotationY", true,
                "When true, Client also applies Host enemy Y rotation from snapshots. Low risk because SULFUR enemies are mostly billboard/2D-style visuals.");
            EnableClientEnemyAiSuppressionExperiment = cfg.Bind("NetworkEnemyStateExperimental", "EnableClientEnemyAiSuppressionExperiment", false,
                "Safety gate for Phase 4.1.0-C Client enemy AI suppression/probe patches. Keep false for normal testing; old suppression/log settings are ignored while this is false.");
            SuppressClientEnemyAiWhenStateMirrorEnabled = cfg.Bind("NetworkEnemyStateExperimental", "SuppressClientEnemyAiWhenStateMirrorEnabled", false,
                "Experimental Phase 4.1.0-C: Client-only. Only used when EnableClientEnemyAiSuppressionExperiment=true. When true, selected enemy AI target/movement entry points are skipped only for local NPCs already controlled by Host enemy state snapshots.");
            LogSuppressedClientEnemyAi = cfg.Bind("NetworkEnemyStateExperimental", "LogSuppressedClientEnemyAi", false,
                "Only used when EnableClientEnemyAiSuppressionExperiment=true. Log throttled Phase 4.1.0-C enemy AI suppression/probe decisions for Host-mirrored Client NPCs.");
            EnableClientEnemyPuppetMode = cfg.Bind("NetworkEnemyStateExperimental", "EnableClientEnemyPuppetMode", true,
                "Phase 4.4.0-B: Client-only. When Host enemy state mirror controls a local NPC, disable that local NPC's behaviour tree/NavMesh/RVO movement driver and let Host transform snapshots move it. This replaces the old high-frequency AI suppression experiment.");
            LogClientEnemyPuppetMode = cfg.Bind("NetworkEnemyStateExperimental", "LogClientEnemyPuppetMode", true,
                "Log one-line begin/end events when a Client NPC enters or leaves Host-mirrored puppet mode.");
            ClientEnemyPuppetStaleReleaseSeconds = cfg.Bind("NetworkEnemyStateExperimental", "ClientEnemyPuppetStaleReleaseSeconds", 3f,
                new ConfigDescription("How long after the last Host enemy snapshot before Client puppet mode releases a local NPC back to its own AI. Use 0 to keep puppet mode until level clear/death.",
                    new AcceptableValueRange<float>(0f, 30f)));
            EnableHostEnemyAnimationMirror = cfg.Bind("NetworkEnemyStateExperimental", "EnableHostEnemyAnimationMirror", true,
                "Phase 4.4.0-C: Host includes NPC Animator layer-0 state and known bool parameters in enemy state snapshots. Client applies them only to puppet-mode enemies.");
            ApplyReceivedEnemyAnimationMirror = cfg.Bind("NetworkEnemyStateExperimental", "ApplyReceivedEnemyAnimationMirror", true,
                "Phase 4.4.0-C: Client puppet enemies apply Host Animator state/parameters from enemy snapshots. Does not re-enable local AI.");
            LogEnemyAnimationMirror = cfg.Bind("NetworkEnemyStateExperimental", "LogEnemyAnimationMirror", false,
                "Log throttled Host/Client enemy animation mirror state changes. Keep false unless diagnosing sliding or missing attack animations.");
            EnemyAnimationMirrorCrossFadeSeconds = cfg.Bind("NetworkEnemyStateExperimental", "EnemyAnimationMirrorCrossFadeSeconds", 0.06f,
                new ConfigDescription("CrossFade duration used when a Client puppet switches to the Host Animator state. 0 uses Animator.Play immediately.",
                    new AcceptableValueRange<float>(0f, 0.5f)));
            EnemyAnimationMirrorNormalizedTimeTolerance = cfg.Bind("NetworkEnemyStateExperimental", "EnemyAnimationMirrorNormalizedTimeTolerance", 0.30f,
                new ConfigDescription("If Client puppet is already in the Host Animator state, resync normalized time only when the fractional time drift exceeds this value.",
                    new AcceptableValueRange<float>(0.02f, 1.0f)));
            EnemyAnimationMirrorApplyAnimatorStatePlayback = cfg.Bind("NetworkEnemyStateExperimental", "EnemyAnimationMirrorApplyAnimatorStatePlayback", false,
                "Phase 4.4.0-F: Client puppet enemies use Animator bools plus Host-motion-derived Moving by default. Full Animator.Play/CrossFade state-hash playback is disabled by default because it can fight Npc.Update locomotion and cause idle/walk flicker.");
            EnemyAnimationMirrorApplyHostCombatStatePlayback = cfg.Bind("NetworkEnemyStateExperimental", "EnemyAnimationMirrorApplyHostCombatStatePlayback", true,
                "Phase 4.4.0-I: allow selective Animator trigger/state playback only while the Host marks an enemy as actively attacking/shooting. This keeps locomotion motion-driven while letting Clients see Host-side combat animations.");
            EnemyAnimationMirrorReplayHostCombatMethods = cfg.Bind("NetworkEnemyStateExperimental", "EnemyAnimationMirrorReplayHostCombatMethods", true,
                "Phase 4.4.0-J/K: Client puppets may replay selected Host combat visual methods under an internal bypass. Damage remains blocked on Client puppets.");
            EnemyAnimationMirrorApplyCombatAnimatorFallback = cfg.Bind("NetworkEnemyStateExperimental", "EnemyAnimationMirrorApplyCombatAnimatorFallback", true,
                "Phase 4.4.0-K: When method replay is not enough, pulse likely combat triggers/bools on the Npc and child/weapon Animators for melee visual fallback.");
            EnemyAnimationMirrorHostCombatActionHoldSeconds = cfg.Bind("NetworkEnemyStateExperimental", "EnemyAnimationMirrorHostCombatActionHoldSeconds", 0.65f,
                new ConfigDescription("Seconds to keep a Host combat-action marker alive after TriggerAttackAnimation/TriggerShoot/SetShooting(true)/TriggerWeaponManually. This marker is serialized in enemy state snapshots.",
                    new AcceptableValueRange<float>(0.10f, 2.0f)));

            EnemyProjectileVisualMirrorEnabled = cfg.Bind("NetworkEnemyTargetExperimental", "EnemyProjectileVisualMirrorEnabled", true,
                "Phase 4.4.0-K: Create client-only no-damage visual projectiles for Host enemy shoot actions using Host origin/aim positions.");
            EnemyProjectileVisualMirrorUseNativeShootReplay = cfg.Bind("NetworkEnemyTargetExperimental", "EnemyProjectileVisualMirrorUseNativeShootReplay", false,
                "Development switch. False avoids native Client puppet TriggerShoot projectile direction bugs; visual projectile mirror is used instead.");
            EnemyProjectileVisualMirrorSpeed = cfg.Bind("NetworkEnemyTargetExperimental", "EnemyProjectileVisualMirrorSpeed", 18f,
                new ConfigDescription("Speed for client-only visual enemy projectile mirror objects.",
                    new AcceptableValueRange<float>(1f, 80f)));
            EnemyProjectileVisualMirrorLifetime = cfg.Bind("NetworkEnemyTargetExperimental", "EnemyProjectileVisualMirrorLifetime", 1.25f,
                new ConfigDescription("Maximum lifetime for client-only visual enemy projectile mirror objects.",
                    new AcceptableValueRange<float>(0.10f, 5.0f)));
            EnableGenericHostCombatAnimatorStateMirror = cfg.Bind("NetworkEnemyTargetExperimental", "EnableGenericHostCombatAnimatorStateMirror", true,
                "Phase 4.4.0-L: generic combat animation mirror. Host sends actual Animator state hashes for active combat Animators under the enemy; Client puppet plays matching relative-path Animator states instead of monster-specific trigger fallbacks.");
            EnableHostAuthoritativeEnemyRangedDamage = cfg.Bind("NetworkEnemyTargetExperimental", "EnableHostAuthoritativeEnemyRangedDamage", false,
                "Phase 4.4.0-L synthetic ranged damage (LEGACY/RETIRED). Superseded by real projectile hits on the target proxy. This key no longer gates the path on its own — the path now ALSO requires EnableSyntheticRangedDamageFallback (default false). Leave as-is.");
            EnableSyntheticRangedDamageFallback = cfg.Bind("NetworkEnemyTargetExperimental", "EnableSyntheticRangedDamageFallback", false,
                "Master gate for the synthetic distance-based enemy ranged damage path. DEFAULT OFF and should stay off: that path estimates hits from the aim line at shot time, so it ignores the client DODGING after the shot and ignores WALLS between the enemy and player (false hits). Real enemy projectiles already hit the target-proxy collider authoritatively (respecting walls + dodging). Only enable if a ranged enemy's projectile genuinely cannot collide with the proxy.");
            // Phase 5.5-RT3-A5: DEFAULT OFF. Intent-driven motion keeps the local AI agent moving the puppet's transform
            // (navmesh/RichAI) WHILE the host snapshot drift also writes transform.position every frame — two competing
            // position writers that fight each other = visible flicker (log53: clientAiIntents=314 + softDrift=12381 on
            // the same entities). Off = host snapshot is the SOLE position authority (classic puppet: agent frozen);
            // walk animation is still driven from the host position delta, so nothing is lost. Re-enable to experiment.
            EnableClientEnemyIntentDrivenMotion = cfg.Bind("NetworkEnemyIntentExperimental", "EnableClientEnemyIntentDrivenMotion", false,
                "Phase 4.4.0-N (default OFF since 5.5-RT3-A5): Client puppet enemies replay Host AI movement intent through local movement systems. OFF makes the host snapshot the single position authority (no flicker from two competing writers).");
            LogEnemyAiIntentMirror = cfg.Bind("NetworkEnemyIntentExperimental", "LogEnemyAiIntentMirror", true,
                "Log low-frequency Host AI intent capture and Client intent replay/correction summaries.");
            EnemyIntentCorrectionDistance = cfg.Bind("NetworkEnemyIntentExperimental", "EnemyIntentCorrectionDistance", 2.5f,
                new ConfigDescription("When intent-driven motion is active, Client enemies are not transform-dragged while drift is below this distance. Above it, a soft correction is applied.",
                    new AcceptableValueRange<float>(0.25f, 20f)));
            EnemyIntentHardSnapDistance = cfg.Bind("NetworkEnemyIntentExperimental", "EnemyIntentHardSnapDistance", 9f,
                new ConfigDescription("Hard snap distance for intent-driven Client enemies. This should be larger than normal movement drift so local AI animation is preserved.",
                    new AcceptableValueRange<float>(1f, 50f)));
            EnemyIntentReplayMinIntervalSeconds = cfg.Bind("NetworkEnemyIntentExperimental", "EnemyIntentReplayMinIntervalSeconds", 0.18f,
                new ConfigDescription("Minimum interval between applying the same Host AI intent destination to a Client puppet enemy.",
                    new AcceptableValueRange<float>(0.02f, 2f)));
            EnemyHostProjectileHitRadius = cfg.Bind("NetworkEnemyTargetExperimental", "EnemyHostProjectileHitRadius", 0.75f,
                new ConfigDescription("Horizontal capsule radius used when Host checks whether an enemy ranged attack path intersects a remote player.",
                    new AcceptableValueRange<float>(0.10f, 2.50f)));
            EnemyHostProjectileVerticalTolerance = cfg.Bind("NetworkEnemyTargetExperimental", "EnemyHostProjectileVerticalTolerance", 1.50f,
                new ConfigDescription("Vertical tolerance used by Host enemy ranged damage checks.",
                    new AcceptableValueRange<float>(0.10f, 4.00f)));
            EnemyHostProjectileMaxDistance = cfg.Bind("NetworkEnemyTargetExperimental", "EnemyHostProjectileMaxDistance", 28f,
                new ConfigDescription("Maximum Host authoritative enemy ranged damage check distance.",
                    new AcceptableValueRange<float>(2f, 80f)));
            EnemyHostProjectileDamage = cfg.Bind("NetworkEnemyTargetExperimental", "EnemyHostProjectileDamage", 10f,
                new ConfigDescription("Temporary fixed damage applied to a remote player hit by Host authoritative enemy ranged attack checks. Tune after confirming hit detection.",
                    new AcceptableValueRange<float>(1f, 200f)));
            EnemyHostProjectileDamageCooldownSeconds = cfg.Bind("NetworkEnemyTargetExperimental", "EnemyHostProjectileDamageCooldownSeconds", 0.45f,
                new ConfigDescription("Minimum seconds before the same Host combat action can damage the same remote peer again.",
                    new AcceptableValueRange<float>(0.05f, 5.0f)));
            EnemyDamageDefaultType = cfg.Bind("NetworkEnemyTargetExperimental", "EnemyDamageDefaultType", 7,
                new ConfigDescription("PerfectRandom.Sulfur.Core.Stats.DamageTypes value used when applying host enemy damage to the local player and the real type is unknown (synthetic ranged hits). Unit.ReceiveDamage REJECTS None(0) outright. 7=Normal (generic physical), 8=Physics. Melee hits forward their real type.",
                    new AcceptableValueRange<int>(1, 16)));
            EnableEnemyElementalStatusEffect = cfg.Bind("NetworkEnemyTargetExperimental", "EnableEnemyElementalStatusEffect", true,
                "When host enemy damage of an elemental type lands on the local player, also apply the matching status (Electric->Electrocuted, Fire->Burning, Frost->Frozen, Poison->Poisoned, Water->Wet, Bleed->Bleed) via Stats.ModifyStatus. This drives the element-specific hurt SCREEN effect (the game renders/decays it from the status). Faithful to single-player (you'd get electrocuted/burned too). Disable for damage-numbers-only.");
            EnemyElementalStatusAmount = cfg.Bind("NetworkEnemyTargetExperimental", "EnemyElementalStatusAmount", 25f,
                new ConfigDescription("Amount of the elemental status applied per elemental hit (status range ~0-100; statuses decay over time). Higher = stronger/longer screen effect and gameplay debuff (e.g. Frozen slows, near 100 freezes solid). Lower = fainter, briefer.",
                    new AcceptableValueRange<float>(1f, 100f)));
            LogEnemyHostDamageAuthority = cfg.Bind("NetworkEnemyTargetExperimental", "LogEnemyHostDamageAuthority", false,
                "Log Host authoritative enemy ranged damage checks and client-side damage applications. Default OFF: this fires once PER HIT, so on a busy fight it is synchronous per-hit disk I/O that stutters the client (confirmed LogOutput108). Turn on only to debug damage routing.");

            EnableHostOnlyEnemyTargetAuthority = cfg.Bind("NetworkEnemyTargetExperimental", "EnableHostOnlyEnemyTargetAuthority", true,
                "Private Phase 4.4.0-H baseline. Host keeps the real enemy AI/target selection; Clients clear/block local puppet enemy targets and local attack triggers.");
            LogEnemyTargetAuthority = cfg.Bind("NetworkEnemyTargetExperimental", "LogEnemyTargetAuthority", true,
                "Log low-frequency HostOnly enemy target authority/probe lines for Client puppet enemies.");
            EnemyTargetAuthorityProbeIntervalSeconds = cfg.Bind("NetworkEnemyTargetExperimental", "EnemyTargetAuthorityProbeIntervalSeconds", 2.0f,
                new ConfigDescription("Minimum seconds between repeated target authority probe logs for the same enemy/agent.",
                    new AcceptableValueRange<float>(0.25f, 30f)));
            EnableEnemyCombatProbe = cfg.Bind("NetworkEnemyTargetExperimental", "EnableEnemyCombatProbe", true,
                "Log and block Client-local puppet enemy combat triggers so combat authority stays on Host. Does not yet make enemies attack Client players.");
            LogEnemyCombatProbe = cfg.Bind("NetworkEnemyTargetExperimental", "LogEnemyCombatProbe", false,
                "Default OFF — high volume: gates the per-shot [Npc] TriggerShoot/SetShooting/SetAimTarget lines + [EnemyCombatProbe]. Floods when many enemies are active (e.g. a ranged group infighting). EnableEnemyCombatProbe (functional puppet-combat blocking) stays independent. Enable to debug enemy combat.");

            EnableHostAuthorizedIntentExecution = cfg.Bind("NetworkEnemyIntentExperimental", "EnableHostAuthorizedIntentExecution", true,
                "Phase 4.4.0-O: Client creates per-NPC authorization windows when Host combat-action snapshots arrive. Authorized windows allow TriggerWeaponManually/TriggerShoot/HandleMeleeHit etc. through instead of blocking every puppet combat call.");
            HostAuthorizedIntentWindowSeconds = cfg.Bind("NetworkEnemyIntentExperimental", "HostAuthorizedIntentWindowSeconds", 1.0f,
                new ConfigDescription("How many seconds a Host-authorized intent window stays open after the triggering snapshot arrives.",
                    new AcceptableValueRange<float>(0.1f, 5.0f)));
            LogHostAuthorizedIntentExecution = cfg.Bind("NetworkEnemyIntentExperimental", "LogHostAuthorizedIntentExecution", true,
                "Log when Client puppet combat methods pass through Host-authorized intent windows.");
            EnableClientEnemyNativeDamageSuppression = cfg.Bind("NetworkEnemyIntentExperimental", "EnableClientEnemyNativeDamageSuppression", true,
                "Phase 4.4.0-O: Suppress native enemy-to-player damage on the Client while HandleMeleeHit runs inside an authorized window. Host authoritative HostDamageRequest packets are the sole damage source.");
            EnableClientPuppetAimOverride = cfg.Bind("NetworkEnemyIntentExperimental", "EnableClientPuppetAimOverride", true,
                "Phase 5.5-P3: When a Client puppet replays a ranged TriggerShoot, override its native Npc.GetAimPosition() with the Host-authoritative aim (head/camera) carried by the intent window. Fixes the native projectile flying at the target's feet.");

            EnableEnemyStateSnapshotDeltaCompression = cfg.Bind("NetworkEnemyStateExperimental", "EnableEnemyStateSnapshotDeltaCompression", true,
                "Phase 4.4.0-D: Host skips enemy state snapshots whose position/rotation/animation did not change enough, while still sending heartbeat updates.");
            EnemyStateSnapshotHeartbeatSeconds = cfg.Bind("NetworkEnemyStateExperimental", "EnemyStateSnapshotHeartbeatSeconds", 0.75f,
                new ConfigDescription("Maximum seconds before Host sends an otherwise unchanged enemy snapshot. Prevents Client puppet stale release while reducing idle traffic.",
                    new AcceptableValueRange<float>(0.1f, 5f)));
            EnemyStateSnapshotPositionDeltaThreshold = cfg.Bind("NetworkEnemyStateExperimental", "EnemyStateSnapshotPositionDeltaThreshold", 0.04f,
                new ConfigDescription("Minimum Host enemy position movement in meters before a new snapshot is sent when delta compression is enabled.",
                    new AcceptableValueRange<float>(0f, 1f)));
            EnemyStateSnapshotRotationDeltaThresholdDegrees = cfg.Bind("NetworkEnemyStateExperimental", "EnemyStateSnapshotRotationDeltaThresholdDegrees", 3.0f,
                new ConfigDescription("Minimum Host enemy Y-rotation change in degrees before a new snapshot is sent when delta compression is enabled.",
                    new AcceptableValueRange<float>(0f, 45f)));
            EnemyStateSnapshotAnimationTimeDeltaThreshold = cfg.Bind("NetworkEnemyStateExperimental", "EnemyStateSnapshotAnimationTimeDeltaThreshold", 0.10f,
                new ConfigDescription("Minimum Animator normalized-time fractional drift before a new animation snapshot is sent when delta compression is enabled.",
                    new AcceptableValueRange<float>(0f, 1f)));

            // Phase 5.1 Host-authoritative enemy health sync.
            EnableHostEnemyDamageEventSync = cfg.Bind("HostDrivenProxy", "EnableHostEnemyDamageEventSync", true,
                "Phase 5.1 P0: Host broadcasts HostEnemyDamageEvent when a combat NPC takes damage. Clients track puppet health and receive reliable death signal.");
            EnableHostEnemyHealthStateSync = cfg.Bind("HostDrivenProxy", "EnableHostEnemyHealthStateSync", true,
                "Phase 5.1 P0: Host broadcasts HostEnemyHealthState alongside damage events when NPC health fields are readable via reflection.");
            ApplyReceivedHostEnemyHealthState = cfg.Bind("HostDrivenProxy", "ApplyReceivedHostEnemyHealthState", true,
                "Phase 5.1 P1: Client attempts to write the host-authoritative health value to the puppet's health field via reflection. Skipped if the field write causes native gameplay side-effects.");
            LogHostEnemyDamageEvents = cfg.Bind("HostDrivenProxy", "LogHostEnemyDamageEvents", true,
                "Log HostEnemyDamageEvent send (Host) and receive/apply (Client) for debugging.");
            LogHostEnemyHealthState = cfg.Bind("HostDrivenProxy", "LogHostEnemyHealthState", false,
                "Log HostEnemyHealthState send and receive. Verbose — disable once stable.");
            AllowRosterBoundDeathDespitePositionDrift = cfg.Bind("HostDrivenProxy", "AllowRosterBoundDeathDespitePositionDrift", true,
                "Phase 5.1 P0: When a death event matches a roster-bound entity, skip the position-drift rejection. The binding is the primary trust anchor; drift is expected due to puppet interpolation lag.");
            HostDeathSnapBeforeApply = cfg.Bind("HostDrivenProxy", "HostDeathSnapBeforeApply", true,
                "Phase 5.1 P0: Before invoking Die() on a roster-bound puppet, snap it to the host death position. Produces a cleaner visual death at the correct location.");
            AllowDeathLateRebind = cfg.Bind("HostDrivenProxy", "AllowDeathLateRebind", true,
                "Phase 5.1 P0: When a death event's hostSpawnIndex has no existing roster binding, attempt a one-shot late-bind by UnitIdentifier + optional position proximity.");
            DisableClientEnemyDeathClaimWhenHostDamageSyncEnabled = cfg.Bind("HostDrivenProxy", "DisableClientEnemyDeathClaimWhenHostDamageSyncEnabled", true,
                "Phase 5.1 P0: Suppress client-local enemy death claims when host damage sync is enabled. Host deaths are authoritative; client claims are a legacy fallback that can cause divergent kills.");

            // Phase 5.3-B Client → Host gameplay hit request pipeline.
            EnableClientHitRequest = cfg.Bind("HostDrivenProxy", "EnableClientHitRequest", true,
                "Phase 5.3-B P0: When client player deals damage to a host-bound puppet NPC, suppress local damage and send ClientHitRequest to host. Host validates and applies authoritative damage.");
            LogClientHitRequests = cfg.Bind("HostDrivenProxy", "LogClientHitRequests", true,
                "Log ClientHitRequest send (Client) and receive/apply/reject (Host) for debugging.");
            // Phase 5.5-RT3-A2: a host-driven puppet is host-authoritative over ALL its damage. The client must forward
            // only genuine local-player attacks (DamageSourceData.damagedByPlayer). Physics/explosion/environment damage
            // (e.g. a barrel a snapped add overlapped, exploding only on the client) must be ignored locally and never
            // wrapped as a ClientHitRequest — that is what mis-killed host adds ~0.2s after spawn. Reversible.
            FilterNonPlayerPuppetDamage = cfg.Bind("HostDrivenProxy", "FilterNonPlayerPuppetDamage", true,
                "Phase 5.5-RT3-A2: on a host-driven puppet, ignore non-local-player damage (physics/environment) instead of forwarding it to the host. Reversible.");
            MakeClientPuppetsKinematic = cfg.Bind("HostDrivenProxy", "MakeClientPuppetsKinematic", true,
                "Phase 5.5-RT3-A3: make host-driven enemy puppets' Rigidbody kinematic so transform-drags can't impart physics impulses into scene props (which fling the client player off the map). Reversible.");
            StableWorldRosterBinding = cfg.Bind("HostDrivenProxy", "StableWorldRosterBinding", true,
                "Phase 5.5-RT3-A7: once a host enemy is bound to a local entity, keep that binding across roster revisions as long as both still exist (instead of re-matching by 5m position each time). Fixes enemies that lose their binding/death-sync after moving (e.g. a caster repositioning >5m). Reversible.");
            ReleasePuppetOnHostDeath = cfg.Bind("HostDrivenProxy", "ReleasePuppetOnHostDeath", true,
                "Phase 5.7-SC3: when a host enemy death is applied to a bound client puppet, release that puppet and drop its host binding. Without this, a host enemy that despawns far from the host player (Npc.Die damageCount=0) while the client is fighting it leaves an orphaned 'host-bound' puppet that ReleaseStaleEnemyPuppets keeps suppressing → it stands in place forever (LogOutput111). Reversible.");
            EvictStaleHostBindings = cfg.Bind("HostDrivenProxy", "EvictStaleHostBindings", true,
                "Phase 5.7-DB: keep the client's hostIdx↔localKey binding maps strictly 1:1. When a host enemy re-binds to a new local entity, evict the old local key's reverse entry; and in ReleaseStaleEnemyPuppets, release any orphaned puppet whose hostIdx the forward map now points elsewhere. Without this, a duplicate/re-binding leaves an orphan flagged 'host-bound' that never receives snapshots (recv=never) and is suppressed from release forever → it stands frozen in place (LogOutput116: a melee Bruiser stuck after climbing onto the player's platform, hostIdx=1 had 3 local keys bound to it). Reversible.");
            SkipDeadHostIdxRebind = cfg.Bind("HostDrivenProxy", "SkipDeadHostIdxRebind", true,
                "Phase 5.7-DB2: the WorldRoster/manifest is the static level-gen roster and still lists enemies the client already buried (applied a terminal death for). Never (re)bind a live local entity to such a dead host idx, and release any puppet stuck on one. Without this, when two same-type enemies exist (e.g. two BlackGuildTrackers) and one dies, the roster keeps re-binding the dead idx to the surviving sibling — which then never receives snapshots (recv=never) while the real sibling's death finds 'never bound, late-bind failed' and is never applied → the survivor stands frozen (LogOutput117). Reversible.");
            EnableRetroactiveEnemyBinding = cfg.Bind("HostDrivenProxy", "EnableRetroactiveEnemyBinding", true,
                "Phase 5.7-RB: when a host enemy's roster/manifest record arrives before the client has locally spawned that enemy (timing race historically recorded 'hostOnly, no local candidate' and never bound), bind it the moment the client later spawns the matching unit. Fixes the recurring 'rosterBound << rosterReceived' / unbound-enemy desync (each-side-simulates, host can't damage, death/state mismatch). Reversible.");
            EnableDestroyedUnitListSweep = cfg.Bind("HostDrivenProxy", "EnableDestroyedUnitListSweep", true,
                "Phase 5.7-RB: before BatchedNPCRaycasts.Update runs, remove destroyed (Unity-null) entries from GameManager.units/aliveNpcs. Prevents the vanilla Update from dereferencing a destroyed puppet's transform (the Transform.get_position NRE flood seen when an unbound puppet is left in the live lists). Reversible.");
            ClientHitRequestMaxRangeMeters = cfg.Bind("HostDrivenProxy", "ClientHitRequestMaxRangeMeters", 30f,
                new ConfigDescription("Max distance (meters) allowed between client attacker and host target for ClientHitRequest validation. 0 = skip range check.",
                    new AcceptableValueRange<float>(0f, 100f)));
            ClientHitRequestRateLimitSeconds = cfg.Bind("HostDrivenProxy", "ClientHitRequestRateLimitSeconds", 0.08f,
                new ConfigDescription("Minimum seconds between accepted ClientHitRequests per target (per hostSpawnIndex). Caps max hit rate at ~12.5/s.",
                    new AcceptableValueRange<float>(0f, 2f)));

            // Phase 5.3-C Terminal dead latch + visual hit flash.
            EnableClientTerminalDeadLatch = cfg.Bind("HostDrivenProxy", "EnableClientTerminalDeadLatch", true,
                "Phase 5.3-C P0-1: Once a host enemy is confirmed dead (DeathEvent applied, HealthState isDead/hp<=0), latch it terminal-dead on the client. Blocks all subsequent Animator/movement/hit overrides so the corpse cannot twitch between Hit/Idle/Standing.");
            LogClientTerminalDead = cfg.Bind("HostDrivenProxy", "LogClientTerminalDead", true,
                "Log when a host enemy is latched terminal-dead and when overrides are blocked because of it.");
            EnableClientHitFlash = cfg.Bind("HostDrivenProxy", "EnableClientHitFlash", true,
                "Phase 5.3-C P0-2: On HostEnemyDamageEvent, play a visual-only white hit flash on the puppet renderer. Never calls ReceiveDamage and never changes health. Skipped for terminal-dead targets.");
            LogClientHitFlash = cfg.Bind("HostDrivenProxy", "LogClientHitFlash", true,
                "Log visual hit-flash play/skip events for debugging.");
            ClientHitFlashDurationSeconds = cfg.Bind("HostDrivenProxy", "ClientHitFlashDurationSeconds", 0.08f,
                new ConfigDescription("Duration (seconds) the MaterialPropertyBlock tint fallback flash stays applied before it is cleared. Only used by the last-resort tint path; native DoWhiteFlash/SetHitEffect manage their own timing.",
                    new AcceptableValueRange<float>(0.02f, 0.5f)));

            // Phase 5.3-D two-phase death state.
            EnableClientPendingDeadState = cfg.Bind("HostDrivenProxy", "EnableClientPendingDeadState", true,
                "Phase 5.3-D P0-2: When the host reports an enemy hp<=0/isDead, mark it PendingDead instead of immediately terminal-dead. PendingDead lets the HostDeathEvent / Npc.Die() death animation run first, preventing the corpse from freezing in a standing frame.");
            EnableClientDeathVisualFallback = cfg.Bind("HostDrivenProxy", "EnableClientDeathVisualFallback", true,
                "Phase 5.3-D P0-4: If a PendingDead enemy receives no HostDeathEvent within the grace delay, run a VISUAL-ONLY death shim (Animator Dead=true, disable nav/RVO, toggle off behaviour tree). Never triggers gameplay death/loot/exp/analytics.");
            ClientDeathVisualFallbackDelaySeconds = cfg.Bind("HostDrivenProxy", "ClientDeathVisualFallbackDelaySeconds", 0.25f,
                new ConfigDescription("Grace period (seconds) to wait for a HostDeathEvent before applying the visual-only death shim to a PendingDead enemy.",
                    new AcceptableValueRange<float>(0f, 2f)));
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

            // Phase 5.3-F ClientHit visual + LevelGeneration trace.
            EnableClientHitVisual = cfg.Bind("HostDrivenProxy", "EnableClientHitVisual", true,
                "Phase 5.3-F: When the host applies a validated ClientHitRequest, play the native white hit flash on the host's own NPC and broadcast a HostHitVisualEvent so the client mirrors it. Visual only — health/death stay host-authoritative; never calls ReceiveDamage.");
            EnableLevelGenTrace = cfg.Bind("LevelManifest", "EnableLevelGenTrace", true,
                "Phase 5.3-F: Hook LevelGeneration nodes (FinalizeConnection / Connector.FinalizeSpawn / AddExtraRoomsNode) to trace the real generation flow and locate the first host/client divergence. Discovery-first: logs real method signatures, never guesses.");
            LogLevelGenTrace = cfg.Bind("LevelManifest", "LogLevelGenTrace", true,
                "Log per-connector / per-room LevelGeneration trace lines. Verbose during generation — disable once the divergence cause is understood.");

            // Phase 5.0 Host-Driven Proxy Architecture.
            EnableHostDrivenEnemyProxy = cfg.Bind("HostDrivenProxy", "EnableHostDrivenEnemyProxy", true,
                "Phase 5.0: Master switch for Host-Driven Proxy Architecture. When true, client enemies act as pure visual proxies — no autonomous AI decisions, attacks, or damage.");
            SuppressAllClientPuppetDamage = cfg.Bind("HostDrivenProxy", "SuppressAllClientPuppetDamage", true,
                "Phase 5.0 P0: Suppress ALL damage dealt by client puppet enemies to the local player. Prevents divergence from client-local enemy AI. Host-authoritative damage arrives via HostDamageRequest.");
            LogClientPuppetDamageSuppression = cfg.Bind("HostDrivenProxy", "LogClientPuppetDamageSuppression", true,
                "Log when client puppet enemy damage is suppressed by the host-driven proxy gate.");
            EnableHostAttackPhaseEvents = cfg.Bind("HostDrivenProxy", "EnableHostAttackPhaseEvents", true,
                "Phase 5.0 P1: Host sends reliable HostAttackPhaseEvent packets on each CombatEnemy attack phase transition (Windup/Active/Recovery). Replaces the fragile authorized-window approach.");
            LogHostAttackPhaseEvents = cfg.Bind("HostDrivenProxy", "LogHostAttackPhaseEvents", false,
                "Default OFF — high volume (one line per enemy attack action): logs HostAttackPhaseEvent send (Host) / receive (Client). The network broadcast itself is gated by EnableHostAttackPhaseEvents (stays on). Enable to debug attack-animation sync.");
            EnableClientAttackPhaseAnimatorDrive = cfg.Bind("HostDrivenProxy", "EnableClientAttackPhaseAnimatorDrive", true,
                "Phase 5.0 P1: Client applies received HostAttackPhaseEvent directly to the puppet Animator via CrossFade/Play. Does not invoke native attack methods.");
            ClientAttackPhaseCrossFadeSeconds = cfg.Bind("HostDrivenProxy", "ClientAttackPhaseCrossFadeSeconds", 0.05f,
                new ConfigDescription("CrossFade duration used when a Client puppet applies a HostAttackPhaseEvent animator state hint. 0 uses Animator.Play immediately.",
                    new AcceptableValueRange<float>(0f, 0.3f)));
            EnableHostProjectileVisualSpawnEvent = cfg.Bind("HostDrivenProxy", "EnableHostProjectileVisualSpawnEvent", false,
                "Phase 5.0 P2: Host sends reliable HostProjectileVisualSpawn events when an enemy fires. Client spawns a cosmetic no-damage visual proxy. Disabled by default until P1 is stable.");
            LogHostProjectileVisualSpawn = cfg.Bind("HostDrivenProxy", "LogHostProjectileVisualSpawn", false,
                "Log HostProjectileVisualSpawn events (very noisy in ranged combat).");
            IncludeRemotePlayersInInterest = cfg.Bind("HostDrivenProxy", "IncludeRemotePlayersInInterest", true,
                "Phase 5.5-P1: treat remote players (clients) as interest sources too, so enemies a client is fighting far from the Host player still get full-rate snapshots instead of being throttled (fixes client-side enemy freeze/stutter). Reversible.");
            EnableRemotePlayerTargetProxy = cfg.Bind("HostDrivenProxy", "EnableRemotePlayerTargetProxy", false,
                "Phase 5.5-P3-A2 (EXPERIMENTAL, default OFF): Host spawns a minimal faction=Player targetable Unit at each remote player's position so enemy AI detects/aggros/attacks clients. Damage routing (A3) not done yet. Enable to test.");
            LogRemotePlayerTargetProxy = cfg.Bind("HostDrivenProxy", "LogRemotePlayerTargetProxy", true,
                "Phase 5.5-P3-A2: verbose log for remote-player target proxies (create/update/destroy).");
            RemotePlayerTargetProxySetIsPlayer = cfg.Bind("HostDrivenProxy", "RemotePlayerTargetProxySetIsPlayer", false,
                "Phase 5.5-P3-A2 EXPERIMENTAL: set Unit.isPlayer=true on the proxy. Off by default — we reverse-engineer the real detection entry instead of guessing init flags. May affect game logic that counts isPlayer units.");
            RemotePlayerTargetProxyForceAggro = cfg.Bind("HostDrivenProxy", "RemotePlayerTargetProxyForceAggro", true,
                "Phase 5.5-P3-A2: drive enemies near a remote player to target its proxy via AiAgent.overridetargets.AddUnits (the game's force-target API). This is what makes clients able to aggro/fight enemies. Reversible.");
            RemotePlayerTargetProxyAggroRange = cfg.Bind("HostDrivenProxy", "RemotePlayerTargetProxyAggroRange", 30f,
                new ConfigDescription("Phase 5.5-P3-A2: enemies within this distance of a remote player's proxy are forced to target it.",
                    new AcceptableValueRange<float>(5f, 100f)));
            RemotePlayerTargetProxyOnlyWhenCloser = cfg.Bind("HostDrivenProxy", "RemotePlayerTargetProxyOnlyWhenCloser", false,
                "Phase 5.5-P3-A2: only override an enemy when the remote player is closer to it than the host player. OFF by default — enemy AI only wakes near the host, so the host is usually 'closer' and this gate would suppress all overrides.");
            RemotePlayerTargetProxyHitboxLayer = cfg.Bind("HostDrivenProxy", "RemotePlayerTargetProxyHitboxLayer", 6,
                new ConfigDescription("Phase 5.5-P3-A3: Unity layer for the proxy's hitbox collider (must be in the enemy's hitboxMask; melee raycast = hitboxMask 64 => layer 6).",
                    new AcceptableValueRange<int>(0, 31)));
            RemotePlayerTargetProxyBodyBlocker = cfg.Bind("HostDrivenProxy", "RemotePlayerTargetProxyBodyBlocker", false,
                "EXPERIMENTAL (default OFF). Add a solid body collider on the real player's body layer (a child of the target proxy) so a charging/dashing enemy physically collides with the remote player and stops. KNOWN ISSUE (LogOutput72): when ON it corrupts the host's projectile AutoPool (NRE in AutoPool.PoolData.Get during Weapon.DispatchProjectile) so ranged enemies can't fire — leave OFF until the layer interaction is solved.");
            RemoveTargetProxyWhenPeerDowned = cfg.Bind("HostDrivenProxy", "RemoveTargetProxyWhenPeerDowned", true,
                "A3.2: while a remote player is downed/dead, remove its enemy-target proxy on the host so enemies stop attacking the downed player (and drop aggro). The proxy is recreated when the player revives. Reversible.");
            HideDownedLocalPlayerFromEnemies = cfg.Bind("HostDrivenProxy", "HideDownedLocalPlayerFromEnemies", true,
                "A3.2 (local player): while THIS machine's player is downed, null it out of AiAgent.GetTarget so enemies stop targeting/attacking the downed local player (covers the host's own real player, which has no proxy). Re-targets on revive. Reversible.");
            LogPooledObjectDestroyDiag = cfg.Bind("HostDrivenProxy", "LogPooledObjectDestroyDiag", false,
                "DIAGNOSTIC (default OFF; culprit found = AutoPool.ResetPools on level switch). Logs a stack trace whenever something destroys a still-pooled AutoPooledObject. Has a perf cost (inspects every Object.Destroy). Only enable to re-investigate pool corruption.");
            ApplyHostPlayerDamageViaReceiveDamage = cfg.Bind("HostDrivenProxy", "ApplyHostPlayerDamageViaReceiveDamage", true,
                "Phase 5.5-P3-A3: apply host enemy damage to the local player via the real Unit.ReceiveDamage (fires native hit feedback: flash/shake/sound/blood + armor + downed) instead of a raw health write. Falls back to the raw write if it fails. Reversible.");
            EnableEnemyInterestManagement = cfg.Bind("HostDrivenProxy", "EnableEnemyInterestManagement", true,
                "Phase 5.0 P2: Reduce enemy snapshot rate for entities far from the local player. Near-combat enemies get full rate; distant enemies get EnemyFarSnapshotHz.");
            FullRateForEngagedEnemies = cfg.Bind("HostDrivenProxy", "FullRateForEngagedEnemies", true,
                "Phase 5.7-RB2: an enemy that is engaged (has a combat target, or a client hit it within ClientEngagedEnemyFullRateSeconds) always gets full-rate snapshots even when far from the Host player. Without this, a client fighting an enemy far from the (stationary) Host gets a frozen puppet between attack frames — it stands still while still attacking/damaging the client (LogOutput104 GoblinSpearman idx=12). Only IDLE distant enemies are throttled. Reversible.");
            ClientEngagedEnemyFullRateSeconds = cfg.Bind("HostDrivenProxy", "ClientEngagedEnemyFullRateSeconds", 8f,
                new ConfigDescription("How long after a client's last hit on an enemy to keep that enemy at full snapshot rate (engagement window).",
                    new AcceptableValueRange<float>(1f, 30f)));
            ThrottleOnlyWithKnownRemotePositions = cfg.Bind("HostDrivenProxy", "ThrottleOnlyWithKnownRemotePositions", true,
                "Phase 5.7-RB3: only apply the far-enemy snapshot throttle when the host has at least one remote-player interest position this tick (so it can actually prove the enemy is far from the client, not just from the stationary Host). If the interest feed is empty, never throttle — a client is connected by definition when snapshots are being sent. Fixes whole mobs freezing on the client when the Host stands far away. Reversible.");
            LogEnemyInterestDiag = cfg.Bind("Debug", "LogEnemyInterestDiag", false,
                "Diagnostic: log per-enemy interest decisions (distHost / distRemoteMin / remoteCount / engaged) and the remote-interest feed (collected positions vs host position). Throttled. Default OFF.");
            SendAllEnemySnapshotsToClients = cfg.Bind("HostDrivenProxy", "SendAllEnemySnapshotsToClients", true,
                "Phase 5.7-RB4: while a client is connected, send every (delta-changed) enemy state snapshot at full rate instead of distance-throttling. Fixes enemies a far-from-host client is fighting freezing in place (their puppet starves of position updates; the corpse only snaps to the right place on death). Delta compression still limits bandwidth. Set false to restore the distance-based interest throttle. Reversible.");
            DisablePauseInMultiplayer = cfg.Bind("Multiplayer", "DisablePauseInMultiplayer", true,
                "Phase 5.7-NP: Minecraft-LAN-style no-pause. While in a co-op session (client linked, or host linked) the world keeps simulating when you open the inventory/backpack, the ESC pause menu, F3 dev tools, dialog, or lose window focus — the UI still opens and your input is still gated, only time no longer stops. Pausing one side desyncs boss timelines (they are time-axis driven) and freezes the other player's enemies. Also sets Application.runInBackground so an unfocused second instance keeps running. Reversible.");
            LogPauseSuppression = cfg.Bind("Debug", "LogPauseSuppression", false,
                "Diagnostic: log when a world-pause (inventory/ESC/F3/dialog/focus) is suppressed in multiplayer. Default OFF.");
            LogDamageApplyHitch = cfg.Bind("Debug", "LogDamageApplyHitch", false,
                "Diagnostic: time the client's per-hit host-damage apply (includes the native ReceiveDamage feedback) and log only when it exceeds DamageApplyHitchThresholdMs. Locates the 'hitch on every hit'. Default OFF.");
            DamageApplyHitchThresholdMs = cfg.Bind("Debug", "DamageApplyHitchThresholdMs", 3f,
                new ConfigDescription("Threshold (ms) above which a single client damage-apply is logged as a hitch.",
                    new AcceptableValueRange<float>(0.5f, 50f)));
            EnemyNearCombatDistance = cfg.Bind("HostDrivenProxy", "EnemyNearCombatDistance", 20f,
                new ConfigDescription("Enemies within this distance from the local player always receive full-rate snapshots.",
                    new AcceptableValueRange<float>(5f, 100f)));
            EnemyFarDistance = cfg.Bind("HostDrivenProxy", "EnemyFarDistance", 40f,
                new ConfigDescription("Enemies beyond this distance receive reduced-rate snapshots (EnemyFarSnapshotHz).",
                    new AcceptableValueRange<float>(10f, 200f)));
            EnemyFarSnapshotHz = cfg.Bind("HostDrivenProxy", "EnemyFarSnapshotHz", 2f,
                new ConfigDescription("Snapshot rate for enemies beyond EnemyFarDistance. 0 disables snapshots for those enemies entirely.",
                    new AcceptableValueRange<float>(0f, 10f)));
            EnableCombatEventCoalescing = cfg.Bind("HostDrivenProxy", "EnableCombatEventCoalescing", true,
                "Default ON (5.7-B8): unified per-entity coalescing/throttling of high-frequency combat broadcasts (supersedes the target-aware burst suppression). (1) enemy→client damage is ACCUMULATED per (peer,type) and flushed once per window — total damage preserved, feedback batched; (2) enemy→NPC damage/health events are THROTTLED per entity (display only; periodic enemy-state snapshots converge health); (3) attack-phase animation events are THROTTLED per enemy. A machine-gun enemy hitting the client no longer floods. Reversible.");
            EnemyToClientDamageCoalesceSeconds = cfg.Bind("HostDrivenProxy", "EnemyToClientDamageCoalesceSeconds", 0.1f,
                new ConfigDescription("(1) Window over which enemy→client damage is accumulated into one message per damage type. 0 = per-hit (off).",
                    new AcceptableValueRange<float>(0f, 1f)));
            EnemyDamageEventMinIntervalSeconds = cfg.Bind("HostDrivenProxy", "EnemyDamageEventMinIntervalSeconds", 0.07f,
                new ConfigDescription("(2) Min seconds between enemy-damage/health broadcasts PER enemy (throttle; intermediate hits dropped — health converges via snapshot). 0 = per-hit (off).",
                    new AcceptableValueRange<float>(0f, 1f)));
            AttackPhaseEventMinIntervalSeconds = cfg.Bind("HostDrivenProxy", "AttackPhaseEventMinIntervalSeconds", 0.08f,
                new ConfigDescription("(3) Min seconds between attack-phase (animation) broadcasts PER enemy (throttle). 0 = per-action (off).",
                    new AcceptableValueRange<float>(0f, 1f)));

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
            // Phase 5.6-WS — player weapon bullet sync (visual-only barrage replay) default on.
            EnablePlayerWeaponSync.Value = true;
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
            // Phase 5.6-WS-2 — remote held weapon model default on.
            EnableRemoteWeaponModel.Value = true;
            LogRemoteWeaponModel.Value = true;
            // Phase 5.6-WS-3 — player visual discovery probes default OFF now (player has no sprite art; scans done).
            LogPlayerVisualDiscovery.Value = false;
            LogPlayerSpriteAssetScan.Value = false;
            // Phase 5.6-WS-3 — remote player Father sprite body default on; NPC-prefab body off (fallback only).
            EnableRemotePlayerSpriteBody.Value = true;
            EnableRemotePlayerNpcBody.Value = false;
            LogRemotePlayerBody.Value = true;
            // Force body/weapon size + carry pose while still being tuned (overrides stale configs from earlier builds).
            RemoteBodyScale.Value = 1.2f;
            RemoteWeaponScale.Value = 1.4f;
            RemoteWeaponHipHeight.Value = 1.0f;
            RemoteWeaponForward.Value = 0.30f;
            RemoteWeaponRight.Value = 0.4f;
            RemoteBodyDepthBias.Value = 0.0f;   // same layer — weapon intersects the paper sprite
            RemoteNameSize.Value = 0.03f;
            RemoteNameHeight.Value = 0.45f;

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

            EnableHostEnemyStateSnapshotMirror.Value = true;
            EnemyStateSnapshotSendRateHz.Value = 6f;
            EnemyStateSnapshotMaxEnemiesPerPacket.Value = 4; // O3: reduced from 16; SendHostEnemyStateSnapshotChunk now auto-splits on byte limit
            OnlySendAliveEnemyStateSnapshots.Value = true;
            LogReceivedEnemyStateSnapshots.Value = false;
            ApplyReceivedEnemyStateSnapshots.Value = true;
            EnemyStateSnapshotPositionTolerance.Value = 5f;
            EnemyStateSnapshotInterpolationSpeed.Value = 18f;
            EnemyStateSnapshotPlaybackDurationMultiplier.Value = 1.10f;
            EnemyStateSnapshotSnapDistance.Value = 10f;
            EnemyStateSnapshotApplyRotationY.Value = true;
            EnableClientEnemyAiSuppressionExperiment.Value = false;
            SuppressClientEnemyAiWhenStateMirrorEnabled.Value = false;
            LogSuppressedClientEnemyAi.Value = false;
            EnableClientEnemyPuppetMode.Value = true;
            LogClientEnemyPuppetMode.Value = true;
            ClientEnemyPuppetStaleReleaseSeconds.Value = 3f;
            EnableHostEnemyAnimationMirror.Value = true;
            ApplyReceivedEnemyAnimationMirror.Value = true;
            LogEnemyAnimationMirror.Value = false;
            EnemyAnimationMirrorCrossFadeSeconds.Value = 0.06f;
            EnemyAnimationMirrorNormalizedTimeTolerance.Value = 0.30f;
            EnemyAnimationMirrorApplyAnimatorStatePlayback.Value = false;
            EnemyAnimationMirrorApplyHostCombatStatePlayback.Value = true;
            EnemyAnimationMirrorReplayHostCombatMethods.Value = true;
            EnemyAnimationMirrorApplyCombatAnimatorFallback.Value = false;
            EnemyAnimationMirrorHostCombatActionHoldSeconds.Value = 0.80f;
            EnemyProjectileVisualMirrorEnabled.Value = false;
            EnemyProjectileVisualMirrorUseNativeShootReplay.Value = false;
            EnemyProjectileVisualMirrorSpeed.Value = 26f;
            EnemyProjectileVisualMirrorLifetime.Value = 2.0f;
            EnableGenericHostCombatAnimatorStateMirror.Value = true;
            EnableHostAuthoritativeEnemyRangedDamage.Value = false;
            EnableSyntheticRangedDamageFallback.Value = false;
            // Phase 5.5-RT3-A5: host snapshot is the single position authority (no flicker from competing writers).
            EnableClientEnemyIntentDrivenMotion.Value = false;
            LogEnemyAiIntentMirror.Value = true;
            EnemyIntentCorrectionDistance.Value = 2.5f;
            EnemyIntentHardSnapDistance.Value = 9f;
            EnemyIntentReplayMinIntervalSeconds.Value = 0.18f;
            EnemyHostProjectileHitRadius.Value = 0.75f;
            EnemyHostProjectileVerticalTolerance.Value = 1.50f;
            EnemyHostProjectileMaxDistance.Value = 28f;
            EnemyHostProjectileDamage.Value = 10f;
            EnemyHostProjectileDamageCooldownSeconds.Value = 0.45f;
            EnemyDamageDefaultType.Value = 7;
            EnableEnemyElementalStatusEffect.Value = true;
            EnemyElementalStatusAmount.Value = 25f;
            // Phase 5.7-HG: LogEnemyHostDamageAuthority is now Bind-default OFF and intentionally NOT forced here, so the
            // user can toggle it from the .cfg (per the Log* convention). It fires once per hit = per-hit disk I/O.
            // Keep the threshold hitch probe auto-on this investigation round (threshold-gated → no per-hit flood; remove once root-caused).
            LogDamageApplyHitch.Value = true;
            DamageApplyHitchThresholdMs.Value = 3f;
            EnableHostOnlyEnemyTargetAuthority.Value = true;
            LogEnemyTargetAuthority.Value = true;
            EnemyTargetAuthorityProbeIntervalSeconds.Value = 2.0f;
            EnableEnemyCombatProbe.Value = true;          // functional (puppet-combat blocking) stays on; LogEnemyCombatProbe defaults OFF (bind)
            EnableHostAuthorizedIntentExecution.Value = true;
            HostAuthorizedIntentWindowSeconds.Value = 1.0f;
            LogHostAuthorizedIntentExecution.Value = true;
            EnableClientEnemyNativeDamageSuppression.Value = true;
            EnableClientPuppetAimOverride.Value = true;
            // Phase 5.5-RT3-A2 — filter non-player damage on host-driven puppets, default on.
            FilterNonPlayerPuppetDamage.Value = true;
            // Phase 5.5-RT3-A3 — kinematic host-driven puppets, default on.
            MakeClientPuppetsKinematic.Value = true;
            // Phase 5.5-RT3-A7 — stable world roster binding, default on.
            StableWorldRosterBinding.Value = true;
            // Phase 5.7-SC3 — release the client puppet + binding when a host enemy death is applied (kills standing zombies).
            ReleasePuppetOnHostDeath.Value = true;
            // Phase 5.7-DB — strict 1:1 host-binding maps + orphan release (kills the frozen duplicate-binding zombie).
            EvictStaleHostBindings.Value = true;
            // Phase 5.7-DB2 — never rebind a buried host idx; release puppets stuck on a dead host idx.
            SkipDeadHostIdxRebind.Value = true;
            // Phase 5.7-RB — retro-active enemy binding + destroyed-unit list sweep, default on.
            EnableRetroactiveEnemyBinding.Value = true;
            EnableDestroyedUnitListSweep.Value = true;
            // Phase 5.7-RB2 — full snapshot rate for engaged (targeting / recently-client-hit) enemies, default on.
            FullRateForEngagedEnemies.Value = true;
            ClientEngagedEnemyFullRateSeconds.Value = 8f;
            // Phase 5.7-RB3 — only throttle when a remote position is known; diag forced on for this test round.
            ThrottleOnlyWithKnownRemotePositions.Value = true;
            LogEnemyInterestDiag.Value = true;
            // Phase 5.7-RB4 — send all (delta-changed) enemy snapshots while a client is connected (no distance throttle).
            SendAllEnemySnapshotsToClients.Value = true;
            // Phase 5.7-NP — Minecraft-LAN no-pause in co-op; diag forced on for this test round.
            DisablePauseInMultiplayer.Value = true;
            LogPauseSuppression.Value = true;

            EnableEnemyStateSnapshotDeltaCompression.Value = true;
            EnemyStateSnapshotHeartbeatSeconds.Value = 0.75f;
            EnemyStateSnapshotPositionDeltaThreshold.Value = 0.04f;
            EnemyStateSnapshotRotationDeltaThresholdDegrees.Value = 3.0f;
            EnemyStateSnapshotAnimationTimeDeltaThreshold.Value = 0.10f;

            // Phase 5.3-B Client → Host hit request — on by default.
            EnableClientHitRequest.Value = true;
            LogClientHitRequests.Value = true;
            ClientHitRequestMaxRangeMeters.Value = 30f;
            ClientHitRequestRateLimitSeconds.Value = 0.08f;

            // Phase 5.3-C/D Terminal dead latch + visual hit flash — on by default.
            EnableClientTerminalDeadLatch.Value = true;
            LogClientTerminalDead.Value = true;
            EnableClientHitFlash.Value = true;
            LogClientHitFlash.Value = true;
            ClientHitFlashDurationSeconds.Value = 0.08f;

            // Phase 5.3-D two-phase death — on by default; visual fallback enabled (visual-only).
            EnableClientPendingDeadState.Value = true;
            EnableClientDeathVisualFallback.Value = true;
            ClientDeathVisualFallbackDelaySeconds.Value = 0.25f;
            LogClientPendingDead.Value = true;

            // Phase 5.3-E level manifest — on by default; diagnostics + quarantine enabled.
            EnableHostLevelManifest.Value = true;
            LogLevelManifest.Value = true;
            LogLevelManifestDiff.Value = true;
            QuarantineClientOnlyManifestEnemies.Value = true;

            // Phase 5.3-F ClientHit visual + LevelGeneration trace — on by default.
            EnableClientHitVisual.Value = true;
            EnableLevelGenTrace.Value = true;
            LogLevelGenTrace.Value = true;

            // Phase 5.1 Host-authoritative enemy health sync — all P0/P1 features on.
            EnableHostEnemyDamageEventSync.Value = true;
            EnableHostEnemyHealthStateSync.Value = true;
            ApplyReceivedHostEnemyHealthState.Value = true;
            LogHostEnemyDamageEvents.Value = true;
            LogHostEnemyHealthState.Value = false;
            AllowRosterBoundDeathDespitePositionDrift.Value = true;
            HostDeathSnapBeforeApply.Value = true;
            AllowDeathLateRebind.Value = true;
            DisableClientEnemyDeathClaimWhenHostDamageSyncEnabled.Value = true;

            // Phase 5.0 Host-Driven Proxy — testing defaults: all P0/P1 features on, P2 off.
            EnableHostDrivenEnemyProxy.Value = true;
            SuppressAllClientPuppetDamage.Value = true;
            LogClientPuppetDamageSuppression.Value = true;
            EnableHostAttackPhaseEvents.Value = true;
            // 5.7-B3: high-frequency per-enemy-action combat LOGS (LogHostAttackPhaseEvents / LogEnemyCombatProbe /
            // LogTeleportDiag) now default OFF via their bind defaults — NOT force-set here, so they can be flipped on
            // in the config file to debug a specific area. Functional events/probes stay on (EnableHostAttackPhaseEvents
            // above, EnableEnemyCombatProbe below).
            EnableClientAttackPhaseAnimatorDrive.Value = true;
            ClientAttackPhaseCrossFadeSeconds.Value = 0.05f;
            EnableHostProjectileVisualSpawnEvent.Value = false; // P2: enable when P1 stable
            LogHostProjectileVisualSpawn.Value = false;
            EnableEnemyInterestManagement.Value = true;
            IncludeRemotePlayersInInterest.Value = true;
            EnemyNearCombatDistance.Value = 20f;
            EnemyFarDistance.Value = 40f;
            EnemyFarSnapshotHz.Value = 2f;

            cfg.Save();
        }
    }
}
