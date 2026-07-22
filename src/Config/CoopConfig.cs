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
        public ConfigEntry<bool> LogUnitReceiveDamage { get; }  // pure diagnostic log; the damage patch itself is functional + always-on
        // Issue #2 diag: ground-hazard / DoT status on the local player (poison/burning/bleed) — source identity + per-change
        // edge + live DoT coroutine count, to locate the client-side "poison stacks infinitely, instantly downs" runaway.
        public ConfigEntry<bool> LogHazardProbe { get; }
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
        public Fixed<bool>       LogAiTargetingReverseDump   { get; }  // P3 one-shot dump completed (hardcoded off)
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
        public Setting<string> ConnectionKey          { get; }
        public Setting<bool>   RequireSameModVersion  { get; }
        public Setting<string> LastSteamIdToJoin      { get; } // STEAM-2: last SteamID64 pasted into "Steam ID to join"
        public ConfigEntry<float>  SendPingIntervalSeconds { get; }

        /// <summary>Backing JSON store for the co-op settings exposed above and <see cref="EnableCoopToasts"/>.</summary>
        public CoopSettingsStore CoopSettings { get; }

        // ----- Run / Scene state negotiation (metadata only) -----
        public Fixed<bool>         EnableRunStateNegotiation      { get; } // functional: always on (release-hardcoded)
        public Fixed<float>        RunStateBroadcastIntervalSeconds { get; } // functional tuning (release-hardcoded)
        public ConfigEntry<bool>   WarnOnRunStateMismatch         { get; }
        public Fixed<bool>         EnableHostSceneAuthority       { get; }
        public ConfigEntry<bool>   WarnOnClientSceneDrift         { get; }
        public Fixed<bool>         EnableHostSceneRequestProtocol { get; }
        public Fixed<bool>         AutoSendHostSceneRequestOnDrift { get; }
        public Fixed<float>        HostSceneRequestIntervalSeconds { get; }
        public Fixed<bool>         EnableManualClientSceneFollow  { get; }
        public ConfigEntry<KeyboardShortcut> ManualClientSceneFollowKey { get; }
        public Fixed<bool>         ManualClientSceneFollowRequiresHostRequest { get; }

        // ----- Phase 3.1 level seed authority ----- (functional: always on, release-hardcoded)
        public Fixed<bool>         EnableLevelSeedAuthority { get; }
        public Fixed<bool>         RequireSameLevelSeedForSceneMatch { get; }
        public Fixed<bool>         ApplyHostLevelSeedOnManualFollow { get; }
        public Fixed<bool>         HideRemoteVisualWhenLevelSeedMismatch { get; }

        // ----- Phase 5.3-I generation-input (used sets) sync -----
        public Fixed<bool>         SyncHostUsedSetsOnManualFollow { get; } // functional: always on (release-hardcoded)
        public ConfigEntry<bool>   LogUsedSetsTrace { get; }

        // ----- Phase 5.3-J client join load gate -----
        public Fixed<bool>         ClientWaitHostGenerationInputBeforeFirstLoad { get; }
        public Fixed<float>        ClientLoadGateTimeoutSeconds { get; }
        public Fixed<bool>         ClientLoadGateAllowFallbackAfterTimeout { get; }
        public Fixed<float>        ClientLoadGateRequestIntervalSeconds { get; }
        // Phase 5.6-DL P3: gate the client's DEATH respawn to the hub so it converges on the HOST's hub seed
        // (same safe-zone instance) instead of generating its own. Timeout falls back to a local hub respawn.
        public Fixed<bool>         ClientGateDeathRespawnUntilHostHub { get; }
        public Fixed<float>        ClientGateDeathRespawnTimeoutSeconds { get; }
        // Phase 5.6-DL-Q2: let the client LEAD level transitions. When the client walks into an exit, relay the
        // target to the host; the host performs the transition authoritatively (host player moves + generates),
        // then the gated client follows. The host still owns generation.
        public Fixed<bool>         EnableClientTransitionRelay { get; }
        // Phase 5.6-CL: "allow client to load level". The in-run extension of the relay above — when a JOINED
        // client walks into a NextLevelTrigger (CompleteLevel, the sub-level advance that never goes through
        // GoToLevel), do NOT generate locally: show a native loading fade, tell the host, and let the host LEAD
        // the transition (host moves + generates) so everyone advances together. Needs EnableClientTransitionRelay
        // (the transport). On timeout the client falls back to advancing locally so it is never stuck.
        public Fixed<bool>         AllowClientInitiatedLevelLoad { get; }
        public Fixed<float>        ClientInitiatedLoadTimeoutSeconds { get; }
        // Phase F3-Reload: when a linked client F3's to the level both ends are ALREADY in (reload-in-place), the
        // client must NOT self-reload off the host's stale "I'm here" request (that diverged — Log147) — it relays
        // and waits for the host to RE-LEAD the reload so both regenerate together.
        public Fixed<bool>         EnableClientReloadInPlaceRelay { get; }

        // Phase 5.6-LK: explicit "联机状态 / Online-Linked state". The master switch for whether the mod's
        // multiplayer behavior is active. CLIENT default OFF — the player presses ManualClientSceneFollowKey
        // (PageDown) to LINK (join + follow the host), and ClientUnlinkKey (PageUp) to UNLINK and go back to
        // playing their own run independently. While linked the client forwards ALL of its own (non-host) map
        // switches to the host so the host leads everyone. HOST default ON (debug-friendly) — when off the host
        // acts single-player (no broadcasts, ignores client relays). HostLinkToggleKey toggles it.
        public ConfigEntry<bool>   ClientLinkedByDefault { get; }
        // Hardcoded ON: the host's multiplayer master switch must default to broadcasting, otherwise a joining
        // client never receives the host's scene request and cannot auto-follow (SendHostSceneRequest bails when
        // !HostLinked) — a silent, easy-to-hit trap when the .cfg value was left/toggled off. Still toggleable
        // in-game with HostLinkToggleKey; removed from the .cfg so a stale/off value can't break joining.
        public Fixed<bool>         HostLinkedByDefault { get; }
        public ConfigEntry<KeyboardShortcut> ClientUnlinkKey { get; }
        public ConfigEntry<KeyboardShortcut> HostLinkToggleKey { get; }

        // ----- Phase 5.4-A client join flow -----
        public ConfigEntry<string> ClientJoinMode { get; }

        // ----- Phase 5.4-E Boss encounter authority (release-hardcoded) -----
        public Fixed<bool>         EnableBossEncounterSync { get; }
        public Fixed<bool>         BossEncounterClientBlockLocalStart { get; }
        public ConfigEntry<bool>   LogBossEncounter { get; }
        // ----- Phase 5.4-E2 BossStart chain completion + lifecycle probe (release-hardcoded) -----
        public Fixed<float>        BossContinuationGraceSeconds { get; }
        public Fixed<bool>         EnableBossLifecycleProbe { get; }
        public ConfigEntry<bool>   LogBossLifecycle { get; }
        // ----- Phase PF-0 boss pre-fight convergence diagnostic -----
        public ConfigEntry<bool>   LogBossPreFight { get; }
        // ----- Fix A: faithful intro runs dialog → do NOT pre-remove it (hardcoded off) -----
        public Fixed<bool>         RemoveBossDialogInteractableOnStart { get; }
        // ----- Phase PF: faithful synced boss intro (release-hardcoded) -----
        public Fixed<bool>         EnableFaithfulBossIntro { get; }
        // ----- Phase PF (Plan B): gate fight start on dialog dismiss (release-hardcoded) -----
        public Fixed<bool>         GateBossFightOnDialogClose { get; }
        // ----- Phase PF-ArmDefer (issue 1): defer Cousin intro arm (release-hardcoded) -----
        public Fixed<bool>         DeferBossIntroArm { get; }
        // ----- Phase RM: host-authoritative room-membership substrate (release-hardcoded) -----
        public Fixed<bool>         EnableBossRoomMembership { get; }
        // ----- Phase RM-2b: scope synced intro to in-room players (release-hardcoded) -----
        public Fixed<bool>         GateBossDialogToInRoom { get; }
        // ----- Phase RT3-Cousin-arms-Room: exclude out-of-room players from arm attacks (release-hardcoded) -----
        public Fixed<bool>         ExcludeOutOfRoomPlayersFromBossAttacks { get; }
        // ----- Phase 5.4-E3 BossDialogCommit + Lucia + Witch state + Emperor worm -----
        public ConfigEntry<bool>   EnableEmperorWormDiagnostics { get; }
        // EMP-6a: observe-only probe for the Emperor phase-2 SPIDER (validate the sync model before writing sync code).
        public ConfigEntry<bool>   EnableEmperorSpiderDiagnostics { get; }
        // EMP-1b: default-OFF stopwatch to localize the ground-slam frame hitch (native vs mod).
        public ConfigEntry<bool>   LogEmperorWormPerf { get; }
        public ConfigEntry<float>  EmperorWormPerfThresholdMs { get; }
        public ConfigEntry<bool>   LogBossTransitionDiagnostics { get; }
        // ----- Phase 5.4-E4 Boss dynamic spawn manifest (release-hardcoded) -----
        public Fixed<bool>         EnableBossDynamicSpawnManifest { get; }
        public ConfigEntry<bool>   LogBossDynamicSpawn { get; }
        // ----- Phase RT3-Cousin-arms: Cousin arm co-op de-fang + host group-AoE (release-hardcoded) -----
        public Fixed<bool>         EnableCousinArmSync { get; }
        // ----- Phase 5.4-F BossDamageAuthority (release-hardcoded) -----
        public Fixed<bool>         EnableBossDamageAuthority { get; }
        // ----- Phase 5.4-F2 BossStartPresentation (rolled back, hardcoded off) -----
        public Fixed<bool>         EnableBossClientPresentation { get; }
        // ----- Phase 5.4-F4 fixed-point boss discrete-event authority (release-hardcoded) -----
        public Fixed<bool>         EnableBossDiscreteEventAuthority { get; }
        // ----- Phase 5.4-F5 Lucia eye defeat authority (release-hardcoded) -----
        public Fixed<bool>         EnableLuciaEyeAuthority { get; }
        // ----- Phase 5.4-F6 Lucia terminal death authority (release-hardcoded) -----
        public Fixed<bool>         EnableLuciaDeathAuthority { get; }
        // ----- Phase 5.4-G Witch visible phase-witch damage authority (release-hardcoded) -----
        public Fixed<bool>         EnableWitchPhaseDamageAuthority { get; }
        // ----- Phase 5.4-G2 Witch phase revision authority (release-hardcoded) -----
        public Fixed<bool>         EnableWitchPhaseAuthority { get; }
        // ----- Phase 5.4-G4 Witch Phase 2 timing probe (diagnostic) -----
        public ConfigEntry<bool>   LogWitchPhase2Probe { get; }
        // ----- Phase 5.4-G5 Witch Phase 2 dome manifest authority (release-hardcoded) -----
        public Fixed<bool>         EnableWitchPhase2Manifest { get; }
        // ----- Phase 5.4-G7 Witch death fix (release-hardcoded) -----
        public Fixed<bool>         EnableWitchDeathFix { get; }
        // ----- Phase 5.5-RT1 runtime spawn sync ----- (functional: always on, release-hardcoded)
        public Fixed<bool>         EnableRuntimeSpawnSync { get; }
        public ConfigEntry<bool>   LogRuntimeSpawnSync { get; }
        // ----- Issue #5: host-authoritative one-shot TriggerSpawner (skeleton ambush) sync -----
        public Fixed<bool>         EnableTriggerSpawnSync { get; }
        public ConfigEntry<bool>   LogTriggerSpawnSync { get; }
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
        // K-1 (issue #10): projectile-path throwable (ThrowingKnives) flight visual sync. Functional, always on.
        public Fixed<bool>         EnablePlayerThrowableProjectileSync { get; }
        public ConfigEntry<bool>   LogPlayerThrowableProjectileSync { get; }
        public Fixed<int>          PlayerWeaponSyncMaxProjectilesPerShot { get; } // safety clamp (release-hardcoded)

        // ----- Phase 5.7-BR in-scene destructible (Breakable) sync -----
        public Fixed<bool>         EnableBreakableSync { get; }   // functional: always on (release-hardcoded)
        public ConfigEntry<bool>   LogBreakableSync { get; }
        public Fixed<bool>         EnableThrowableEffectSync { get; }  // HZ-2 functional: always on (release-hardcoded)
        public ConfigEntry<bool>   LogThrowableEffectSync { get; }
        // ----- Phase LD-1 generic combat-room gate (MetalGate) open/close sync -----
        public Fixed<bool>         EnableGateSync { get; }        // functional: always on (release-hardcoded)
        public ConfigEntry<bool>   LogGateSync { get; }
        // ----- Phase LD-1b combat-room door sync, GameObject.SetActive variant (Lucia etc.) -----
        public Fixed<bool>         EnableTriggerDoorSync { get; } // functional: always on (release-hardcoded)
        public ConfigEntry<bool>   LogTriggerDoorSync { get; }
        // ----- Phase DB-1 inter-chunk hold-to-open door (DoorBlocker) sync -----
        public Fixed<bool>         EnableDoorBlockerSync { get; } // functional: always on (release-hardcoded)
        public ConfigEntry<bool>   LogDoorBlockerSync { get; }
        // ----- Phase KD (crypt sync) locked OpenableDoor (crypt key door) open sync -----
        public Fixed<bool>         EnableOpenableDoorSync { get; } // functional: always on (release-hardcoded)
        public ConfigEntry<bool>   LogOpenableDoorSync { get; }
        // SL-2 (Shared-loot): host-authoritative chest (Container) open + state sync. Functional, always on (but only
        // takes effect while shared loot is enabled — ShareAllLoot).
        public Fixed<bool>         EnableChestSync { get; }
        public ConfigEntry<bool>   LogChestSync { get; }
        // SL-2b (Shared-loot): host-authoritative LootableObject (food/material/register) sync. Runtime-gated by ShareAllLoot.
        public Fixed<bool>         EnableLootableSync { get; }
        public ConfigEntry<bool>   LogLootableSync { get; }
        // TD-1: shared damage numbers on the unlockable target dummy (DamageTracker). Functional, always on; independent
        // of the loot mode. Self-gates on an active session and on the local player having the dummy unlocked.
        public Fixed<bool>         EnableTargetDummySync { get; }
        public ConfigEntry<bool>   LogTargetDummySync { get; }
        // ----- Phase LD-2 FF14-style arena lockdown (release-hardcoded) -----
        public Fixed<bool>         EnableArenaLockdown { get; }
        public ConfigEntry<bool>   LogArenaLockdown { get; }
        public ConfigEntry<KeyboardShortcut> ArenaEnterConfirmKey { get; }
        public Fixed<bool>         EnableArenaGracePeriod { get; }

        // ----- In-game co-op UI (toasts / status) via SULFUR Native UI Lib (soft dependency) -----
        public Setting<bool>   EnableCoopToasts { get; }

        // ----- FF-1 friendly fire (host-authoritative session setting; connect-page toggle, coop.json) -----
        public Setting<bool>       FriendlyFire { get; }
        public ConfigEntry<bool>   LogFriendlyFire { get; }

        // ----- EM-4 Endless progression mode (host-authoritative session setting; connect-page toggle, coop.json).
        // Shared (default) = one shared XP pool + card level; Independent = each player has their own. See EM-5/EM-6.
        public Setting<bool>       SharedEndlessProgress { get; }

        // ----- EM-5c Independent-mode XP attribution (host-authoritative session setting; connect-page toggle, coop.json).
        // Only meaningful in Independent mode: XP goes to whoever first-damaged the enemy (true) or landed the killing
        // blow (false, default = last-hit).
        public Setting<bool>       EndlessXpFirstDamage { get; }

        // ----- World item-drop sync (player-thrown items first; forward-compatible with a Shared-loot toggle) -----
        public Fixed<bool>         EnableWorldItemDropSync { get; }
        public ConfigEntry<bool>   LogWorldItemDropSync { get; }
        public ConfigEntry<bool>   ShareAllLoot { get; }

        // ----- Phase EM Endless Mode co-op sync -----
        // EM-0 seed-parity probe (diagnostic): confirms GameManager.currentSeed + the chosen arena match on both ends at
        // Endless entry — the arena/burst-RNG parity the host-authoritative plan depends on. Log-only, no behavior change.
        public ConfigEntry<bool>   LogEndlessSync { get; }
        // EM-1/EM-2 world layer (functional): the host is authoritative for the Endless arena's wave enemies; a linked
        // client suppresses its own local wave driver/spawns and mirrors the host's enemies through the runtime-spawn
        // pipeline. Release-hardcoded (always on), reversible.
        public Fixed<bool>         EnableEndlessSync { get; }

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

        // ----- Phase 5.3-M P1 auto-follow + load barrier (release-hardcoded) -----
        public Fixed<bool>         EnableAutoFollowHostSceneRequest { get; }
        public Fixed<bool>         EnableLoadBarrier { get; }
        public Fixed<float>        LoadBarrierTimeoutSeconds { get; }
        public Fixed<bool>         LoadBarrierBlockHostAdvance { get; }
        public Fixed<bool>         LoadBarrierLogOnlyMode { get; }

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
        // Issue #4: pure diagnostic log for client-side weapon-XP crediting (the credit itself is always-on).
        public ConfigEntry<bool>   LogWeaponXpCredit { get; }
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
        public Fixed<bool>         EnableClientEnemyPuppetMode { get; }
        public ConfigEntry<bool>   LogClientEnemyPuppetMode { get; }
        public ConfigEntry<bool>   LogClientFrameHitch { get; }   // TB: attribute the client combat-frame hitch (craw count vs frame ms)
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
        // ST-1/ST-2 enemy status effect authority — functional, always on; this is its diagnostic log only.
        public ConfigEntry<bool>   LogUnitStatusSync       { get; }
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
        public Fixed<bool>         EnableHostLevelManifest             { get; }
        public ConfigEntry<bool>   LogLevelManifest                    { get; }
        public ConfigEntry<bool>   LogLevelManifestDiff                { get; }
        public Fixed<bool>         QuarantineClientOnlyManifestEnemies { get; }

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
        // Plan B: multiplayer NPC activation + headless Player registry — verified working (release-hardcoded).
        public Fixed<bool>         EnableMultiPlayerNpcActivation { get; }
        public Fixed<float>        MultiPlayerNpcActivationDistance { get; }
        public Fixed<int>          MultiPlayerNpcActivationsPerFrame { get; }
        public Fixed<bool>         EnableRemotePlayerInPlayersList { get; }
        public Fixed<bool>         EnableGhostPlayerHitbox { get; }
        public ConfigEntry<bool>   LogRemotePlayerRegistry { get; }
        // ----- Ghost-during-load freeze fix (LevelGeneration.ShowLevelNode NRE) -----
        public Fixed<bool>         SuppressGhostsWhileLoading { get; }

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
            RequireSameModVersion = new Setting<bool>  (() => store.Values.requireSameModVersion, v => { store.Values.requireSameModVersion = v; store.Save(); });
            EnableCoopToasts      = new Setting<bool>  (() => store.Values.enableCoopToasts,      v => { store.Values.enableCoopToasts = v; store.Save(); });
            LastSteamIdToJoin     = new Setting<string>(() => store.Values.lastSteamIdToJoin,      v => { store.Values.lastSteamIdToJoin = v ?? ""; store.Save(); });
            FriendlyFire          = new Setting<bool>  (() => store.Values.friendlyFire,           v => { store.Values.friendlyFire = v; store.Save(); });
            SharedEndlessProgress = new Setting<bool>  (() => store.Values.sharedEndlessProgress,  v => { store.Values.sharedEndlessProgress = v; store.Save(); });
            EndlessXpFirstDamage  = new Setting<bool>  (() => store.Values.endlessXpFirstDamage,   v => { store.Values.endlessXpFirstDamage = v; store.Save(); });

            // master
            EnableDebugLog     = cfg.Bind("Debug", "EnableDebugLog",     false, "Verbose debug output.");
            EnableReverseProbe = cfg.Bind("Debug", "EnableReverseProbe", true,  "Master switch for all reverse probes.");

            // probe categories
            EnablePlayerProbe = cfg.Bind("Probe", "EnablePlayerProbe", true, "Log GameManager player/state events.");
            EnableUnitProbe   = cfg.Bind("Probe", "EnableUnitProbe",   true, "Log Unit / UnitManager lifecycle.");
            EnableNpcProbe    = cfg.Bind("Probe", "EnableNpcProbe",    true, "Log Npc events.");
            LogUnitReceiveDamage = cfg.Bind("Probe", "LogUnitReceiveDamage", false,
                "Log a line per Unit/Npc ReceiveDamage call (high-frequency in combat — default OFF). NOTE: this is a PURE LOG. The damage patches themselves (downed/client damage suppression, client→host hit forwarding, host Npc health-sync) are functional and always on, independent of this switch — see ApplyDamageForwardPatches. The old EnableDamageProbe/EnableNpcProbe gates conflated the two and could break combat when toggled off as a 'log'.");
            LogTeleportDiag   = cfg.Bind("Probe", "LogTeleportDiag",   false, "Diag (default OFF — high volume; also drives the per-enemy [PosDiag] line): TeleportPlayer.DoTeleport trigger + stack, local-player TeleportTo from/to, full DamageSourceData on suppressed puppet hits. Enable to debug teleport/position.");
            LogHazardProbe    = cfg.Bind("Probe", "LogHazardProbe",    false,
                "Diag (default OFF — issue #2): per-change [Hazard] lines for damaging statuses (Poisoned/Burning/Bleed/Electrocuted) on the LOCAL player — the applying source's identity (player weapon vs enemy vs environment), the change edge (Apply 0→+, Inc, Tick/Dec, Remove +→0), the live DoT coroutine count, and current HP. The status value itself is hard-capped at 7 for players, so a climbing coroutine count is the signal for the runaway. PURE LOG; no functional effect.");
            LogFriendlyFire   = cfg.Bind("Probe", "LogFriendlyFire",   false,
                "Diag (default OFF): FF-1 friendly-fire lines — per-proxy-hit source classification (sampled), FF hit request send/receive, session-settings broadcast/apply. PURE LOG; the friendly-fire feature itself is controlled by the connect-page toggle, not this switch.");
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
            LogAiTargetingReverseDump   = new Fixed<bool>(false); // P3 one-shot dump completed; Plan B ghost registry is the solution.
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
            // RequireSameModVersion) moved to CoopSettings (JSON store), bound above. Only the ping tuning
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

            // host scene authority — functional, hardcoded.
            EnableHostSceneAuthority = new Fixed<bool>(true);
            WarnOnClientSceneDrift = cfg.Bind("NetworkSceneAuthority", "WarnOnClientSceneDrift", true,
                "Warn when a Client is not in the Host scene. Warning-only; no correction is performed.");
            EnableHostSceneRequestProtocol = new Fixed<bool>(true);
            AutoSendHostSceneRequestOnDrift = new Fixed<bool>(true);
            HostSceneRequestIntervalSeconds = new Fixed<float>(10f);

            // manual scene follow keybind only.
            EnableManualClientSceneFollow = new Fixed<bool>(true);
            ManualClientSceneFollowKey = cfg.Bind("NetworkSceneAuthority", "ManualClientSceneFollowKey", new KeyboardShortcut(KeyCode.PageDown),
                "Client-only manual follow key. Press this after receiving HostSceneRequest to attempt local GoToLevel. Avoid F1-F12 because SULFUR's DevTools/F-key bindings may toggle invulnerability or other debug states.");
            ManualClientSceneFollowRequiresHostRequest = new Fixed<bool>(true);

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
            ClientWaitHostGenerationInputBeforeFirstLoad = new Fixed<bool>(true);
            ClientLoadGateTimeoutSeconds = new Fixed<float>(30f);
            ClientLoadGateAllowFallbackAfterTimeout = new Fixed<bool>(false);
            ClientLoadGateRequestIntervalSeconds = new Fixed<float>(2f);
            ClientGateDeathRespawnUntilHostHub = new Fixed<bool>(true);
            ClientGateDeathRespawnTimeoutSeconds = new Fixed<float>(12f);
            EnableClientTransitionRelay = new Fixed<bool>(true);
            AllowClientInitiatedLevelLoad = new Fixed<bool>(true);
            ClientInitiatedLoadTimeoutSeconds = new Fixed<float>(15f);
            EnableClientReloadInPlaceRelay = new Fixed<bool>(true);
            ClientLinkedByDefault = cfg.Bind("NetworkSceneAuthority", "ClientLinkedByDefault", false,
                "联机状态: whether the CLIENT starts LINKED (joining/following the host). Default false so an in-progress solo run is never hijacked — the player presses ManualClientSceneFollowKey (PageDown) to link and ClientUnlinkKey to unlink.");
            // Hardcoded ON (retired from .cfg): the host always starts broadcasting so a joining client can
            // auto-follow. Off would silence the host's scene requests → client can't join. Toggle in-game with
            // HostLinkToggleKey if a temporary single-player host is needed.
            HostLinkedByDefault = new Fixed<bool>(true);
            ClientUnlinkKey = cfg.Bind("NetworkSceneAuthority", "ClientUnlinkKey", new KeyboardShortcut(KeyCode.PageUp),
                "Client only: key to LEAVE 联机状态 (stop following/relaying and play the local run independently). PageDown links, this unlinks.");
            HostLinkToggleKey = cfg.Bind("NetworkSceneAuthority", "HostLinkToggleKey", new KeyboardShortcut(KeyCode.PageDown),
                "Host only: key to TOGGLE 联机状态 (mod multiplayer on/off).");

            // Phase 5.4-E: host-authoritative Boss encounter start (functional, hardcoded).
            EnableBossEncounterSync = new Fixed<bool>(true);
            BossEncounterClientBlockLocalStart = new Fixed<bool>(true);
            LogBossEncounter = cfg.Bind("NetworkBoss", "LogBossEncounter", true,
                "Log boss encounter discovery / start request / broadcast / apply for debugging.");

            // Phase 5.4-E2: a Boss start is a CHAIN (interact/intro -> coroutine/dialogue -> fight-start). After the
            // Client applies a host start, a per-encounter authorized-continuation window keeps the later chain steps
            // from being blocked as unauthorized local starts. The window closes once the fight is observed started
            // plus this grace, or when the run changes — deliberately not a fixed timeout (Cousin is dialogue-paced).
            BossContinuationGraceSeconds = new Fixed<float>(5f);
            EnableBossLifecycleProbe = new Fixed<bool>(true);   // functional: gates unauthorized boss chain steps on the client
            LogBossLifecycle = cfg.Bind("NetworkBoss", "LogBossLifecycle", true,
                "Phase 5.4-E2: log the boss lifecycle probe state-change lines. Compact and state-change-gated to avoid spam.");
            LogBossPreFight = cfg.Bind("NetworkBoss", "LogBossPreFight", true,
                "Phase PF-0: read-only diagnostic. When a boss pre-fight start entrypoint fires, log local+remote scene/seed convergence (did the client race ahead into a divergent boss instance?) and the room-seal/teleport timing. No gameplay change.");
            // Fix A: faithful intro runs the real dialog, so we do NOT pre-remove the interactable (Witch-only vanilla path).
            RemoveBossDialogInteractableOnStart = new Fixed<bool>(false);
            EnableFaithfulBossIntro = new Fixed<bool>(true);
            GateBossFightOnDialogClose = new Fixed<bool>(true);
            DeferBossIntroArm = new Fixed<bool>(true);
            EnableBossRoomMembership = new Fixed<bool>(true);
            GateBossDialogToInRoom = new Fixed<bool>(true);
            ExcludeOutOfRoomPlayersFromBossAttacks = new Fixed<bool>(true);

            // Phase 5.4-E3: dialog-gated bosses (Cousin / Lucia) sync the "fight committed" decision via BossDialogCommit
            // and finalize the local dialog with the real Graph.Stop(true). Witch broadcasts a minimal phase/state skeleton.
            EnableEmperorWormDiagnostics = cfg.Bind("NetworkBoss", "EnableEmperorWormDiagnostics", true,
                "Phase 5.4-E3: probe + log Emperor worm controller state (identify the double-worm source). Diagnostic only; does not change gameplay.");
            EnableEmperorSpiderDiagnostics = cfg.Bind("NetworkBoss", "EnableEmperorSpiderDiagnostics", true,
                "EMP-6a: observe-only probe for the Emperor phase-2 spider (lifecycle/startup-caller/position-divergence/defend/death). Diagnostic only; does not change gameplay.");
            LogEmperorWormPerf = cfg.Bind("NetworkBoss", "LogEmperorWormPerf", false,
                "EMP-1b (default OFF): stopwatch the Emperor worm's FixedUpdate + ground/underground native calls; log only frames slower than EmperorWormPerfThresholdMs to localize the ground-slam hitch.");
            EmperorWormPerfThresholdMs = cfg.Bind("NetworkBoss", "EmperorWormPerfThresholdMs", 6f,
                "EMP-1b: only log a worm perf sample when the measured call exceeds this many milliseconds.");
            LogBossTransitionDiagnostics = cfg.Bind("NetworkBoss", "LogBossTransitionDiagnostics", true,
                "Phase 5.4-E3 (P2): log extra context when a Client receives a HostSceneRequest while already loading/transitioning. Diagnostic only — does not change transition behavior.");

            // Phase 5.4-E4: Host records boss-owned runtime adds (CousinArm/LuciaEye/...) by (encounter, addType, seq)
            // and broadcasts so the Client can bind local[seq] to host[seq] instead of failing proximity/roster binding.
            EnableBossDynamicSpawnManifest = new Fixed<bool>(true);
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
            EnableCousinArmSync = new Fixed<bool>(true);

            // Phase 5.4-F: route a client's hit on a boss MAIN BODY to the Host's real Unit.ReceiveDamage so the boss
            // mechanic (onDamageRecieved) advances host-side, instead of the client locally deducting HP that the host
            // health broadcast then overwrites. Main-body only this phase (no sub-units / special targets).
            EnableBossDamageAuthority = new Fixed<bool>(true);

            // Phase 5.4-F2: experimental client presentation shortcuts (Cousin AI activation + Desert TriggerFight
            // skip). LogOutput29 proved BOTH wrong: Cousin still stands still (it's the intro ANIMATION chain that
            // clears owner invuln, not AI activation), and the Desert skip made the old man invisible again. Default
            // OFF (rolled back); the diagnostics still log. F3 replaces this with a real local presentation chain.
            EnableBossClientPresentation = new Fixed<bool>(false); // rolled back (F3 real presentation chain supersedes).

            // Phase 5.4-F4: Cousin is a fixed-point pool boss; LogOutput30 proved Host AND Client each independently run
            // Submerge/MoveToNewPool/Reappear with their own random pool. The Host is authoritative: it broadcasts the
            // events, the Client BLOCKS its own and mirrors the Host's pool, so both dig out of the same hole.
            EnableBossDiscreteEventAuthority = new Fixed<bool>(true);

            // Phase 5.4-F5: Lucia's eye phase locks the body invulnerable until all spawned eyes die (EyeDied →
            // RestartPhases). The Client reports a local eye kill; the Host consumes one of ITS living eyes through the
            // real death path so the vanilla cycle runs host-authoritatively. Count/cycle only (no per-eye mapping).
            EnableLuciaEyeAuthority = new Fixed<bool>(true);

            // Phase 5.4-F6: Lucia eye-phase completion (Client runs the real RestartPhases when the Host's eyes hit 0 so
            // it leaves Phase 5 / returns to centre) + Lucia terminal death (Client runs a safe local death with the
            // host-only loot/checkpoint/save isolated). Rides the F5 eye gate for completion; this gates the death part.
            EnableLuciaDeathAuthority = new Fixed<bool>(true);

            // Phase 5.4-G: Witch players shoot the per-phase visible witch (the phase controller's witchUnit), NOT
            // witchMainUnit. Route a Client hit on a phase witch to the Host's matching phase witch so its real
            // ReceiveDamage runs (OnDamageMainWitch drops the shared health AND the phase mechanic — e.g. Phase 4
            // RegisterInstance/GoDown — advances). Phase 2 dome (real/illusion) is reserved for a later manifest.
            EnableWitchPhaseDamageAuthority = new Fixed<bool>(true);

            // Phase 5.4-G2: Witch phases CYCLE (Phase6→Phase1), so the old forward-only phase compare desynced the ends
            // and broke damage routing (wrongRole). The Host owns phase transitions with a monotonic revision; the Client
            // applies by revision (even when the enum goes backwards) and is blocked from self-advancing its own phase.
            EnableWitchPhaseAuthority = new Fixed<bool>(true);

            // Phase 5.4-G4: diagnostic timing probe for WitchPhase2.InitPhase/ShowWitches — confirms whether the Client's
            // first Phase 2 round runs ShowWitches at all (suspected race: Host leaves Phase 2 before the Client's local
            // delayPhaseStart elapses). Read-only; pre-requisite to implementing the full WitchPhase2Manifest.
            LogWitchPhase2Probe = cfg.Bind("NetworkBoss", "LogWitchPhase2Probe", true,
                "Phase 5.4-G4: log WitchPhase2 InitPhase/ShowWitches timing (witchesCreated, spawn/dome counts, delayTimer, final real dome index) on both ends. Diagnostic-only.");

            // Phase 5.4-G5: host-authoritative Witch Phase 2 dome layout. Host captures the post-shuffle layout at
            // ShowWitches (real dome index) and broadcasts it; the Client mirrors real/illusion per dome (blocking its own
            // random ShowWitches), routes dome-index hits to the Host's matching witch, applies host hide results, and
            // isolates Phase 2 witches from the ordinary puppet transform so the dome placement holds.
            EnableWitchPhase2Manifest = new Fixed<bool>(true);

            // Phase 5.4-G7: Witch death cleanup. The Client runs WitchDeath via the enemy death mirror, but WitchDeath
            // calls AmuletHelper.RemoveAllCharges → ModifyWorldResource("Amulet") which throws KeyNotFoundException on the
            // Client (it never picked up the amulet), aborting the rest of WitchDeath. Swallow that one call so the death
            // completes, and mark the encounter terminal so no more hits/state are sent for the dead witch.
            EnableWitchDeathFix = new Fixed<bool>(true);

            // Phase 5.5-RT1: runtime (post-level-load) unit spawn sync. Stage 1: the Host's F3 DevTools spawns are
            // mirrored to the Client and bound into the puppet pipeline (one-sided, no double-spawn). Boss adds + client
            // F3 spawns come in later stages. Reversible.
            EnableRuntimeSpawnSync = new Fixed<bool>(true); // Phase 5.5-RT1 runtime spawn sync — functional, always on.
            EnableTriggerSpawnSync = new Fixed<bool>(true); // Issue #5 host-authoritative trigger-spawn — functional, always on.
            LogTriggerSpawnSync = cfg.Bind("NetworkEnemy", "LogTriggerSpawnSync", false,
                "Log host-authoritative one-shot TriggerSpawner (skeleton ambush) sync (Issue #5). Diagnostic only; sync is always on.");
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
            EnablePlayerThrowableProjectileSync = new Fixed<bool>(true); // K-1 (issue #10) ThrowingKnives flight — functional, always on.
            LogPlayerThrowableProjectileSync = cfg.Bind("PlayerWeapon", "LogPlayerThrowableProjectileSync", true,
                "K-1 (issue #10): verbose log for projectile-path throwable (ThrowingKnives) flight sync (capture / replay). PURE LOG.");
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
            EnableThrowableEffectSync = new Fixed<bool>(true); // HZ-2 throwable-effect sync — functional, always on.
            LogThrowableEffectSync = cfg.Bind("Destructibles", "LogThrowableEffectSync", true,
                "HZ-2: verbose log for thrown-throwable effect sync (capture / mirror spawn+break). Default ON while stabilizing.");

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

            // Phase DB-1: SULFUR 0.18 places hold-to-open doors between chunks (level gen, seeded — so both ends own
            // the same doors at the same positions). The hold is driven by the local player's interaction only, so a
            // door one player opens stays shut, and physically impassable, for everyone else. Mirror the open by
            // position; the trap doors' slam-shut stays per-end (out of scope).
            EnableDoorBlockerSync = new Fixed<bool>(true); // Phase DB-1 inter-chunk door sync — functional, always on.
            LogDoorBlockerSync = cfg.Bind("Destructibles", "LogDoorBlockerSync", true,
                "Phase DB-1: verbose log for inter-chunk door sync (capture / broadcast / mirror match).");

            // Phase KD (crypt sync): mirror a locked OpenableDoor's open to every end, keyed by the door's world
            // position. The desert crypt key door (and other one-way doors) open off the local player's key/interaction
            // only, so a door one player opens stays shut and impassable for everyone else — and with a single shared
            // crypt key the other player cannot otherwise get in. Closeable toggle doors are excluded (open-only sync).
            EnableOpenableDoorSync = new Fixed<bool>(true); // Phase KD crypt/locked door open sync — functional, always on.
            LogOpenableDoorSync = cfg.Bind("Destructibles", "LogOpenableDoorSync", true,
                "Phase KD (crypt sync): verbose log for locked OpenableDoor open sync (capture / broadcast / mirror match).");

            EnableChestSync = new Fixed<bool>(true); // SL-2 shared-loot chest sync — functional, gated at runtime by ShareAllLoot.
            LogChestSync = cfg.Bind("WorldItems", "LogChestSync", true,
                "SL-2: verbose log for shared-loot chest (Container) sync (request / host open / broadcast / mirror).");

            EnableLootableSync = new Fixed<bool>(true); // SL-2b shared-loot LootableObject sync — gated at runtime by ShareAllLoot.
            LogLootableSync = cfg.Bind("WorldItems", "LogLootableSync", true,
                "SL-2b: verbose log for shared-loot LootableObject (food/material/scavenge hatbox + cash register) sync.");

            EnableTargetDummySync = new Fixed<bool>(true); // TD-1 shared target-dummy damage numbers — functional, always on.
            LogTargetDummySync = cfg.Bind("TargetDummy", "LogTargetDummySync", false,
                "TD-1: verbose log for shared target-dummy damage numbers (coalesced broadcast / relay apply). High volume while shooting the dummy.");

            // Phase EM-0: Endless Mode seed-parity probe. Logs GameManager.currentSeed + the chosen arena (name/index) and
            // net role when the Endless arena loads, so host and client entries can be compared. Diagnostic only — the
            // Endless sync itself is not implemented yet. Default ON while the parity assumption is being confirmed.
            LogEndlessSync = cfg.Bind("EndlessMode", "LogEndlessSync", true,
                "Phase EM-0: log the Endless-mode seed + chosen arena + net role at arena entry (seed-parity probe). Diagnostic only.");

            // Phase EM-1/EM-2: host-authoritative Endless world layer. On a linked client the local EndlessModeManager
            // becomes a slave — it keeps the (seed-identical) arena it built but does not drive its own waves or spawn its
            // own enemies; the host's wave enemies are mirrored via the runtime-spawn pipeline (EndlessModeManager added to
            // RuntimeSpawnManager.ClassifyOwner). Functional, always on.
            EnableEndlessSync = new Fixed<bool>(true);

            // Phase LD-2: FF14-style arena lockdown. A player crossing a combat-room seal trigger is "in-room"; the first
            // cross anchors a timer; after 5s the non-in-room players in that level are force-sealed with an invisible
            // two-way barrier at their local door (LD-2b), after 10s they get a confirm prompt → on confirm (or on boss
            // death) they teleport in and the barrier drops (LD-2c). Host-authoritative membership + timer.
            EnableArenaLockdown = new Fixed<bool>(true);
            LogArenaLockdown = cfg.Bind("NetworkBoss", "LogArenaLockdown", true,
                "Phase LD-2: verbose log for arena lockdown (local crossings, in-room set, seal/popup/release/teleport).");
            ArenaEnterConfirmKey = cfg.Bind("NetworkBoss", "ArenaEnterConfirmKey", new KeyboardShortcut(KeyCode.Return),
                "Phase LD-2c: key an out-of-room player presses to confirm teleporting into a locked-down arena (the confirm prompt). Default Enter.");
            EnableArenaGracePeriod = new Fixed<bool>(true);

            // World item-drop sync: items that appear in the world are mirrored across peers. Spawn is optimistic +
            // peer-authoritative (instant local drop, then broadcast); take is host-authoritative (first picker wins, the
            // item vanishes for everyone and only the winner receives it). The DIY gun state (attachments / enchantments /
            // caliber / ammo / durability+experience) travels with the item. Reversible.
            EnableWorldItemDropSync = new Fixed<bool>(true);
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
            LogPlayerVisualDiscovery = cfg.Bind("PlayerWeapon", "LogPlayerVisualDiscovery", false,
                "Phase 5.6-WS-3: dump the local player's visual hierarchy once (find the directional billboard sprites). Diagnostic only; default off.");
            LogPlayerSpriteAssetScan = cfg.Bind("PlayerWeapon", "LogPlayerSpriteAssetScan", false,
                "Phase 5.6-WS-3b: one-shot scan of all loaded Sprites/Textures/Prefabs/Animators + Addressables keys for player/character art. Diagnostic only; default off.");

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

            // Phase 5.3-M P1: automatic scene follow + lightweight load barrier (functional, hardcoded).
            EnableAutoFollowHostSceneRequest = new Fixed<bool>(true);
            EnableLoadBarrier = new Fixed<bool>(true);
            LoadBarrierTimeoutSeconds = new Fixed<float>(30f);
            LoadBarrierBlockHostAdvance = new Fixed<bool>(false);
            LoadBarrierLogOnlyMode = new Fixed<bool>(true);

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
            LogWeaponXpCredit = cfg.Bind("NetworkGameplaySyncExperimental", "LogWeaponXpCredit", false,
                "Log client-side weapon-XP crediting on enemy kills (Issue #4). Diagnostic only; crediting is always on.");
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
            PlayerReviveHoldSeconds = new Fixed<float>(5.0f);
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
            EnableClientEnemyPuppetMode = new Fixed<bool>(true);
            LogClientEnemyPuppetMode = cfg.Bind("NetworkEnemyStateExperimental", "LogClientEnemyPuppetMode", true,
                "Log one-line begin/end events when a Client NPC enters or leaves Host-mirrored puppet mode.");
            LogClientFrameHitch = cfg.Bind("NetworkEnemyStateExperimental", "LogClientFrameHitch", true,
                "Diagnostic: on a slow client frame during combat, log the frame time + active enemy-puppet / craw count, to attribute the Terrorbaum craw hitch. Only logs frames under ~20 fps (silent during smooth play).");
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
            LogUnitStatusSync = cfg.Bind("HostDrivenProxy", "LogUnitStatusSync", false,
                "Log enemy status effect sync — a client forwarding its weapon enchantment procs (ST-1) and the host " +
                "broadcasting status start/end edges (ST-2). Per-hit volume; off by default.");
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
            EnableHostLevelManifest = new Fixed<bool>(true);
            LogLevelManifest = cfg.Bind("LevelManifest", "LogLevelManifest", true,
                "Log manifest build summaries (Host built / Client built) and reconcile completion.");
            LogLevelManifestDiff = cfg.Bind("LevelManifest", "LogLevelManifestDiff", true,
                "Log detailed per-room / per-unit / per-special diff lines. Verbose — disable once the divergence cause is understood.");
            QuarantineClientOnlyManifestEnemies = new Fixed<bool>(true);

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
            IncludeRemotePlayersInInterest = new Fixed<bool>(true);
            EnableRemotePlayerTargetProxy = new Fixed<bool>(false); // retired (Plan B ghost registry supersedes it).
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
            // Plan B: verified working (Docs/EnemyActivationAndPlayersRegistry.md) — all functional, hardcoded on.
            EnableMultiPlayerNpcActivation = new Fixed<bool>(true);
            MultiPlayerNpcActivationDistance = new Fixed<float>(60f);
            MultiPlayerNpcActivationsPerFrame = new Fixed<int>(8);
            EnableRemotePlayerInPlayersList = new Fixed<bool>(true);
            EnableGhostPlayerHitbox = new Fixed<bool>(true);
            LogRemotePlayerRegistry = cfg.Bind("PlayerRegistry", "LogRemotePlayerRegistry", true,
                "Plan B: verbose log for the headless Player registry + activation pass (create/update/destroy/register/activate).");
            SuppressGhostsWhileLoading = new Fixed<bool>(true);

            // Last: strip the retired co-op keys (connection settings now in CoopSettings + the dropped role keys)
            // from the .cfg. Must run after every bind/.Value-set above, since each save re-emits orphaned keys.
            CoopSettingsStore.PruneRetiredCfgKeys(cfg);
        }
    }
}
