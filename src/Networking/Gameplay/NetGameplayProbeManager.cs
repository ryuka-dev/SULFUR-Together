using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using SULFURTogether.Networking;
using SULFURTogether.ReverseProbe;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase 4.0.0-A gameplay entity probe.
    ///
    /// It only observes existing reverse-probe hook events and writes structured logs.
    /// It never sends network messages, creates gameplay objects, registers players/NPCs,
    /// changes health, changes AI, or edits loot/pickup state.
    /// </summary>
    internal static class NetGameplayProbeManager
    {
        private const int CombatActionNone = 0;
        private const int CombatActionAttackAnimation = 1;
        private const int CombatActionShoot = 2;
        private const int CombatActionSetShooting = 3;
        private const int CombatActionTriggerWeapon = 4;
        // Phase 4.4.0-O: extended combat-action kind constants.
        private const int CombatActionTriggerShootFromAnimation = 5;
        private const int CombatActionStartMeleeDamage = 6;
        private const int CombatActionEndMeleeDamage = 7;
        private const int CombatActionSetRangedAttacking = 8;
        private const int CombatActionSetAttacking = 9;
        private const int CombatActionDoneAttacking = 10;
        private const int CombatActionDoneShooting = 11;

        // Phase EA-B experiment gate (hardcoded, no .cfg switch — see no-new-config-switches). When true, the two
        // per-frame "Play host's instantaneous animator state hash" paths are SKIPPED for the attack/combat state
        // (ApplyClientEnemyAnimationMirror's full-state Play + TryApplyGenericHostCombatAnimatorStates). Diagnostic
        // LogOutput212 proved that per-frame Play(hostHash, t≈0) replays each of GoblinYoung's ~6 attack sub-states
        // from the start every frame (changed=True 85%) — the "crouch animation repeats several times" thrash. With
        // this on, the attack animation is driven instead by the discrete attack-phase events (reliable Windup/Active
        // broadcast) + the combat animator triggers (SetTrigger Attack/Shoot) + the Attack/Moving bool params, letting
        // the client's own animator controller self-play the full attack sequence — the same model that already makes
        // locomotion smooth. Flip to false to restore the legacy per-frame hash mirror.
        private const bool EaSelfPlayAttackAnimation = false;

        private const int AiIntentKindNone = 0;
        private const int AiIntentKindDestination = 1;
        private const int AiIntentKindLookAt = 2;
        private const int AiIntentKindCombat = 3;
        // Phase 4.4.0-O: EnemyIntentKind values (superset of AiIntentKind).
        private const int EnemyIntentKindNone = 0;
        private const int EnemyIntentKindAttackMelee = 4;
        private const int EnemyIntentKindAttackRanged = 5;
        private const int EnemyIntentKindWeaponAction = 6;

        // Phase 4.4.0-O3: entity sync category. Drives what operations are allowed per entity type.
        private const int SyncCatUnknown       = 0;
        private const int SyncCatCombatEnemy   = 1;
        private const int SyncCatTrader        = 2;
        private const int SyncCatInteractNpc   = 3;
        private const int SyncCatGhost         = 4;
        private const int SyncCatEventNpc      = 5;
        private const int SyncCatAmbient       = 6;
        private const int SyncCatHazard        = 7;

        private sealed class EnemyStateTarget
        {
            public NetGameplayEntitySnapshot Snapshot { get; set; } = null!;
            public NetGameplayEnemyStateSnapshot HostSnapshot { get; set; } = null!;
            public Vector3 StartPosition { get; set; }
            public Vector3 Position { get; set; }
            public bool HasPosition { get; set; }
            public bool HasRotationY { get; set; }
            public float RotationY { get; set; }
            public int Sequence { get; set; }
            public float PlaybackStartedAt { get; set; }
            public float PlaybackDuration { get; set; }
            public float LastUpdatedAt { get; set; }
            public object? RuntimeObject { get; set; }
            public Transform? Transform { get; set; }
            public bool AnimationAppliedForSequence { get; set; }
            public bool LoggedSnap { get; set; }
        }

        private sealed class EnemyPuppetRecord
        {
            public string Key { get; set; } = "";
            public NetGameplayEntitySnapshot Snapshot { get; set; } = null!;
            public object? Npc { get; set; }
            public object? AiAgent { get; set; }
            public object? MovementDriver { get; set; }
            public int NpcId { get; set; }
            public int AiAgentId { get; set; }
            public int MovementDriverId { get; set; }
            public bool Applied { get; set; }
            public float LastSeenAt { get; set; }
            public float LastAppliedAt { get; set; }
            public bool? OriginalDisableVerifyPosition { get; set; }
            public bool? OriginalPreventNavMeshActivation { get; set; }
            public bool? OriginalRigidbodyIsKinematic { get; set; }   // RT3-A3: host-driven puppet made kinematic to stop physics-impulse chaos
            public NetGameplayEnemyStateSnapshot? LastHostSnapshot { get; set; }
            public int LastAppliedAnimatorFullPathHash { get; set; }
            public float LastAnimatorApplyAt { get; set; }
            public bool LastLoggedAnimatorState { get; set; }
            public Animator? CachedAnimator { get; set; }
            public int CachedAnimatorId { get; set; }
            // Phase 5.3-C P0-2: cached renderers for visual hit flash.
            public Renderer[]? CachedRenderers { get; set; }
            public bool RenderersCached { get; set; }
            public bool AnimatorParamCacheInitialized { get; set; }
            public int MovingParamHash { get; set; }
            public int AttackParamHash { get; set; }
            public int CoweringParamHash { get; set; }
            public bool HasMovingParam { get; set; }
            public bool HasAttackParam { get; set; }
            public bool HasCoweringParam { get; set; }
            public List<int> CombatTriggerParamHashes { get; } = new List<int>();
            public int LastAppliedCombatTriggerSequence { get; set; }
            public float LastAppliedCombatTriggerAt { get; set; }
            public int LastAppliedCombatVisualActionSequence { get; set; }
            public float LastAppliedCombatVisualActionAt { get; set; }
            public int LastAppliedCombatAnimatorFallbackSequence { get; set; }
            public float LastAppliedCombatAnimatorFallbackAt { get; set; }
            public int LastAppliedGenericCombatAnimatorSignature { get; set; }
            public float LastAppliedGenericCombatAnimatorAt { get; set; }
            public int LastVisualProjectileSequence { get; set; }
            public float LastVisualProjectileAt { get; set; }
            public bool HasMotionDerivedMoving { get; set; }
            public bool MotionDerivedMoving { get; set; }
            public float LastMotionDerivedMovingAt { get; set; }
            // Diagnostic (gated by LogEnemyAnimationMirror): track actionState flips to locate the combat/locomotion
            // animation thrash. actionState = host-combat-window playback vs locomotion; flips are the suspected
            // source of the "idle/attack animation gets intermittently inserted" loop on client puppet enemies.
            public bool HasLastActionState { get; set; }
            public bool LastActionState { get; set; }
            public float LastActionStateFlipAt { get; set; }
            public float LastRawActionTrueAt { get; set; }   // Phase EA-A: actionState release-grace timer.
            public int LastReplayHash { get; set; }          // Phase EA diag: consecutive same-hash Play counter.
            public int ReplayCount { get; set; }
            public float LastTargetAuthorityApplyAt { get; set; }
            public float LastTargetAuthorityLogAt { get; set; }
            public int LastTargetAuthorityClearedCount { get; set; }
            public string LastTargetAuthoritySummary { get; set; } = "";
            // EMP-1b perf: skip the expensive boss child-scan (GetComponentsInChildren + reflection) for records
            // that repeatedly clear nothing — e.g. the 10 Emperor worm sections, which have no clearable AI target
            // members. Running that scan per-section every 0.5s was the client's ground-fight GC/CPU stall (~1.7s
            // frames in Log219). Root-level clears (cheap, direct reflection) still run.
            public int TargetAuthorityBarrenStreak { get; set; }
            public bool TargetAuthoritySectionScanDisabled { get; set; }
            public float LastCombatProbeLogAt { get; set; }
            public int LastAppliedAiIntentSequence { get; set; }
            public float LastAppliedAiIntentAt { get; set; }
            public float LastIntentCorrectionAt { get; set; }
            // Phase 4.4.0-O2: control mode and movement idempotency.
            public ClientEnemyControlMode ControlMode { get; set; }
            public Vector3 LastAppliedMoveTargetPosition { get; set; }
        }

        private sealed class HostEnemyAiIntent
        {
            public float ExpiresAt { get; set; }
            public string Source { get; set; } = "";
            public int Sequence { get; set; }
            public int Kind { get; set; }
            public bool HasDestination { get; set; }
            public Vector3 Destination { get; set; }
            public bool HasLookAt { get; set; }
            public Vector3 LookAt { get; set; }
        }

        private sealed class HostEnemyCombatAction
        {
            public float ExpiresAt { get; set; }
            public string Source { get; set; } = "";
            public int Sequence { get; set; }
            public int Kind { get; set; }
            public int State { get; set; }
            public bool HasAim { get; set; }
            public Vector3 OriginPosition { get; set; }
            public Vector3 AimPosition { get; set; }
        }

        private sealed class ClientVisualProjectile
        {
            public GameObject? GameObject { get; set; }
            public Vector3 Position { get; set; }
            public Vector3 Target { get; set; }
            public float Speed { get; set; }
            public float ExpiresAt { get; set; }
        }

        private sealed class AnimatorBoolReset
        {
            public Animator? Animator { get; set; }
            public int Hash { get; set; }
            public float ResetAt { get; set; }
        }

        // Phase 4.4.0-O2: per-Client-puppet control mode determines which systems may act.
        private enum ClientEnemyControlMode
        {
            PassiveSnapshot  = 0, // snapshot lerp/snap drives position; no NavMesh steering
            IntentLocomotion = 1, // NavMesh drives movement toward Host AiIntent destination
            AuthorizedCombat = 2, // native combat pipeline runs; NavMesh steering blocked
            Lunge            = 3, // dedicated lunge path (future)
            Dead             = 4,
        }

        // Phase 4.4.0-O: per-NPC Client authorization window opened when a Host combat-action
        // snapshot arrives. While the window is active, ShouldBlockClientEnemyPuppetNpcCombat
        // allows the native Npc combat pipeline to run instead of blocking every call.
        private sealed class ClientEnemyAuthorizedIntentWindow
        {
            public int NpcId { get; set; }
            public int Sequence { get; set; }
            public int Kind { get; set; }
            public float ExpiresAt { get; set; }
            public int WeaponActionState { get; set; }
            public bool HasAimPosition { get; set; }
            public Vector3 AimPosition { get; set; }
            public bool HasOriginPosition { get; set; }
            public Vector3 OriginPosition { get; set; }
            // Phase 4.4.0-O2: sequence-based idempotency — root method replayed exactly once per sequence.
            public bool RootReplayed { get; set; }
        }

        private sealed class HostEnemyStateSendCache
        {
            public float LastSentAt { get; set; }
            public bool HasPosition { get; set; }
            public Vector3 Position { get; set; }
            public bool HasRotationY { get; set; }
            public float RotationY { get; set; }
            public bool IsDead { get; set; }
            public bool HasAnimatorState { get; set; }
            public int AnimatorFullPathHash { get; set; }
            public float AnimatorNormalizedTime { get; set; }
            public bool HasMovingBool { get; set; }
            public bool MovingBool { get; set; }
            public bool HasAttackBool { get; set; }
            public bool AttackBool { get; set; }
            public bool HasCoweringBool { get; set; }
            public bool CoweringBool { get; set; }
            public bool HasHostCombatAction { get; set; }
            public int HostCombatActionKind { get; set; }
            public int HostCombatActionState { get; set; }
            public int HostCombatActionSequence { get; set; }
            public bool HasHostCombatAim { get; set; }
            public Vector3 HostCombatOriginPosition { get; set; }
            public Vector3 HostCombatAimPosition { get; set; }
            public int HostCombatAnimatorStateCount { get; set; }
            public bool HasAiIntent { get; set; }
            public int AiIntentSequence { get; set; }
            public int AiIntentKind { get; set; }
            public Vector3 AiIntentDestination { get; set; }
            public bool HasAiIntentLookAt { get; set; }
            public Vector3 AiIntentLookAt { get; set; }
            public int[] HostCombatAnimatorPathHashes { get; } = new int[4];
            public int[] HostCombatAnimatorFullPathHashes { get; } = new int[4];
            public float[] HostCombatAnimatorNormalizedTimes { get; } = new float[4];
        }

        private static readonly Dictionary<string, NetGameplayEntitySnapshot> EntitiesByLocalId = new Dictionary<string, NetGameplayEntitySnapshot>();
        private static readonly Dictionary<string, EnemyStateTarget> PendingEnemyStateTargets = new Dictionary<string, EnemyStateTarget>();
        private static readonly Dictionary<string, EnemyPuppetRecord> ActiveEnemyPuppets = new Dictionary<string, EnemyPuppetRecord>();
        private static readonly Dictionary<int, EnemyPuppetRecord> ActiveEnemyPuppetsByNpcId = new Dictionary<int, EnemyPuppetRecord>();
        private static readonly Dictionary<int, HostEnemyStateSendCache> HostEnemyStateSendCacheBySpawnIndex = new Dictionary<int, HostEnemyStateSendCache>();
        private static readonly Dictionary<int, HostEnemyCombatAction> HostEnemyCombatActionsByNpcId = new Dictionary<int, HostEnemyCombatAction>();
        private static readonly Dictionary<int, HostEnemyAiIntent> HostEnemyAiIntentsByNpcId = new Dictionary<int, HostEnemyAiIntent>();
        private static readonly Dictionary<int, float> EnemyCombatProbeLastLogAtById = new Dictionary<int, float>();
        private static readonly Dictionary<string, float> ProjectileProbeLastLogAtByKey = new Dictionary<string, float>();
        private static readonly Dictionary<string, float> HostEnemyDamageLastAtByKey = new Dictionary<string, float>();
        private static readonly List<ClientVisualProjectile> ClientVisualProjectiles = new List<ClientVisualProjectile>(32);
        private static readonly List<AnimatorBoolReset> PendingAnimatorBoolResets = new List<AnimatorBoolReset>(64);
        private static readonly List<string> ScratchEnemyStateTargetKeys = new List<string>(64);
        private static readonly HashSet<int> ClientPuppetNpcIds = new HashSet<int>();
        private static readonly HashSet<int> ClientPuppetAiAgentIds = new HashSet<int>();
        // EMP-1b hot-path skip: AiAgent ids whose target-authority apply has been proven barren (section-scan-disabled).
        // For these (e.g. the 10 Emperor worm sections) the block path still returns true — the local AI target is
        // still suppressed — but we skip the per-frame record lookup + reflection clear + block-local-ai-target log.
        // That log was the dominant recurring emitter in the client's boss-combat log, and the client runs sandboxed
        // (Sandboxie) where each synchronous BepInEx write is far slower than on the host.
        private static readonly HashSet<int> BarrenTargetAuthorityAiIds = new HashSet<int>();
        private static readonly HashSet<int> ClientPuppetMovementDriverIds = new HashSet<int>();
        private static readonly HashSet<string> PendingStableSpawnLogs = new HashSet<string>();
        // Phase 4.4.0-O: per-NPC Host-authorized intent windows (Client side only).
        private static readonly Dictionary<int, ClientEnemyAuthorizedIntentWindow> _clientAuthorizedIntentByNpcId = new Dictionary<int, ClientEnemyAuthorizedIntentWindow>();
        // Phase 4.4.0-O3-B: Host roster binding — maps Host spawnIndex → local entity key (Client side only).
        private static readonly Dictionary<int, string> ClientHostToLocalKeyByHostSpawnIndex = new Dictionary<int, string>();
        private static int _clientRosterRevision = -1;
        // Phase 4.4.0-O3-C: Reverse binding — maps client localKey → Host spawnIndex (de-dup detection).
        private static readonly Dictionary<string, int> ClientLocalKeyToHostSpawnIndex = new Dictionary<string, int>();
        // Phase 5.5-RT3-A6: authoritative record of RT3 runtime-spawn bindings (hostIdx → localKey). Survives the roster
        // reconcile (which Clears the dicts above) so runtime boss adds keep their binding instead of being re-stolen.
        private static readonly Dictionary<int, string> _runtimeSpawnBindingsByHostIdx = new Dictionary<int, string>();
        // Phase 5.7-SC2: client-side last time a host enemy-state snapshot was RECEIVED for a hostIdx (regardless of
        // whether it matched/applied). Lets the stale-release log distinguish "host not sending" from "received-not-applied".
        private static readonly Dictionary<int, float> _clientLastSnapshotRecvByHostIdx = new Dictionary<int, float>();
        // Phase 5.7-RB (retro-bind): host roster/manifest records that arrived BEFORE the client had locally spawned the
        // matching enemy were classified hostOnly and never bound (the recurring "record only, no spawn in v1" gap —
        // LogOutput94/103: rosterBound 8/27). Each such unmatched host entry is parked here (keyed by stable hostIdx) so
        // that when the client later spawns a unit of the same type (ReportSpawn), it is bound on the spot ("v2").
        private struct PendingHostBind
        {
            public int     HostSpawnIndex;
            public string  UnitIdentifier;
            public string  Category;
            public bool    HasPosition;
            public Vector3 Position;
            public float   RecordedAt;
        }
        private static readonly Dictionary<int, PendingHostBind> _pendingHostBindLedger = new Dictionary<int, PendingHostBind>();
        private const float RetroBindPosTolerance   = 5f;    // same as the roster proximity tolerance
        private const float RetroBindLedgerTtlSeconds = 30f;  // drop a parked entry the client never spawns a match for
        private static int _retroBindLedgerAdds;
        private static int _retroBindSuccess;
        private static int _retroBindAmbiguousDeferred;
        private static int _retroBindLedgerExpired;
        // Phase 4.4.0-O3-C: Quarantined client-only CombatEnemies — no Host roster binding.
        private static readonly HashSet<string> ClientQuarantinedEntities = new HashSet<string>();
        // Phase 5.0: interest management — host-side local player position hint for rate reduction.
        private static bool    _hostPlayerPositionHintValid;
        private static Vector3 _hostPlayerPositionHint;
        // Phase 5.5-P1: additional interest sources = positions of remote players in the host's scene (the clients).
        private static readonly List<Vector3> _remoteInterestPositions = new List<Vector3>();
        // Phase 5.0: per-spawnIndex last-sent time for interest-managed (far) enemies.
        private static readonly Dictionary<int, float> FarEnemyLastSentAtBySpawnIndex = new Dictionary<int, float>();
        // Phase 5.0: client-side de-dup for received attack phase events (spawnIndex → last applied sequence).
        private static readonly Dictionary<int, int> ClientAttackPhaseLastSeqBySpawnIndex = new Dictionary<int, int>();
        // Phase 5.0 counters.
        private static int _clientPuppetDamageSuppressed;
        private static int _hostAttackPhaseEventsSent;
        private static int _clientAttackPhaseEventsReceived;
        private static int _clientAttackPhaseAnimatorApplies;
        private static int _interestManagementFarSkipped;
        private static int _interestEngagedExempt;   // Phase 5.7-RB2: snapshots kept full-rate because the enemy is client-engaged
        // Phase 5.1: per-spawnIndex puppet health cache (client-side only).
        private static readonly Dictionary<int, float> ClientPuppetHealthBySpawnIndex = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> ClientPuppetMaxHealthBySpawnIndex = new Dictionary<int, float>();
        // Phase 5.1: host-side per-spawnIndex damage event sequence number.
        private static readonly Dictionary<int, int> HostDamageEventSeqBySpawnIndex = new Dictionary<int, int>();
        // Phase 5.1 counters.
        private static int _hostEnemyDamageEventsSent;
        private static int _hostEnemyHealthStatesSent;
        private static int _clientEnemyDamageEventsReceived;
        private static int _clientEnemyHealthStatesReceived;
        private static int _clientEnemyHealthStatesApplied;
        private static int _clientEnemyHealthApplySkippedNoBinding;
        private static int _hostDeathAppliedByRosterBinding;
        private static int _hostDeathAppliedAfterSnap;
        private static int _hostDeathRejectedNoBinding;
        private static int _hostDeathLateBindAttempts;
        private static int _hostDeathLateBindSuccess;
        private static int _clientDeathClaimsSuppressedByHostAuthority;
        // Phase 5.2 death diagnostics.
        private static int _hostDeathTombstoneHit;    // binding existed recently but puppet was released
        private static int _hostDeathNeverBound;       // binding never established (roster gap)

        // P0-1: HealthState apply failure breakdown — each counter covers one silent exit.
        private static int _clientHealthApplyDisabled;    // ApplyReceivedHostEnemyHealthState=false
        private static int _clientHealthNoCurrentHp;      // !state.HasCurrentHealth after binding found
        private static int _clientHealthNoEntity;         // EntitiesByLocalId miss for localKey
        private static int _clientHealthNoRuntimeObj;     // TryGetRuntimeObject returned null
        private static int _clientHealthUnityDestroyed;   // runtimeObj is UnityEngine.Object == null
        private static int _clientHealthNoStats;          // GetUnitStatsObject returned null
        private static int _clientHealthSetStatusMissing; // SetStatus method not found on Stats type
        private static int _clientHealthEnumArgFailed;    // Enum arg build threw (underlying type mismatch)
        private static int _clientHealthSetStatusFailed;  // SetStatus Invoke threw
        private static int _clientHealthWriteFailed;      // TryWriteUnitHealthNative returned false (other)
        private static int _clientHealthWriteReadBackUnchanged; // Invoke succeeded but HP unchanged after write
        // P0-2: puppet lifecycle — suppressed releases while awaiting Host death event.
        private static int _puppetReleaseSuppressedAwaitingHost;
        private static int _puppetStaleReleaseSuppressed; // stale timer fired but puppet is host-bound

        // Phase 5.3-B: Client → Host hit request pipeline.
        private static int _clientHitRequestSeq;
        private static int _clientHitRequestsSent;
        private static int _clientHitRequestsSkippedNoPuppet;    // NPC is not a puppet
        private static int _clientHitRequestsSkippedNoBinding;   // puppet has no host roster binding
        private static int _clientPuppetNonPlayerDamageIgnored;  // RT3-A2: physics/environment damage dropped (not forwarded)
        private static int _puppetDamageSourceSamplesLogged;     // RT3-A2: throttle for source-flag diagnostic
        private static int _suppressedSourceSamplesLogged;       // RT3-A2: dedicated throttle for suppressed (non-player) source dump
        private static int _hostHitRequestsRecv;
        private static int _hostHitRequestsRejectedScene;
        private static int _hostHitRequestsRejectedNoTarget;
        private static int _hostHitRequestsRejectedTypeMismatch;
        private static int _hostHitRequestsRejectedDead;
        private static int _hostHitRequestsRejectedRateLimit;
        private static int _hostHitRequestsAccepted;
        private static int _hostHitRequestsDamageApplied;
        private static int _hostHitRequestsDamageFailed;
        // Per-target last-accepted time for rate limiting (host side).
        private static readonly Dictionary<int, float> _hostHitRequestLastAtByHostIdx = new Dictionary<int, float>();
        // Phase 5.5-RT3-A4: damage from hits that arrive inside the per-target window is COALESCED here (not dropped) and
        // applied on the next accepted hit / on Tick flush — so burst & multi-pellet weapons don't lose 50%+ of damage.
        private static readonly Dictionary<int, float> _hostHitPendingDamageByHostIdx = new Dictionary<int, float>();
        private static int _hostHitRequestsCoalesced;
        // Phase 5.3-B: host hit result + client predicted/confirmed tracking.
        private static int _hostHitResultHealthStateSent;
        private static int _clientLocalHitPredicted;
        private static int _clientLocalHitConfirmed;
        // Client: hostIdx → time we last sent a hit request, used to confirm via subsequent HealthState.
        private static readonly Dictionary<int, float> _clientPendingHitByHostIdx = new Dictionary<int, float>();
        private const float ClientHitConfirmWindowSeconds = 2f;

        // ----------------------------------------------------------------
        // Phase 5.3-D P0-2: Two-phase death state (client side).
        //   PendingDead  = Host reports hp<=0/isDead, but client death VISUAL not done yet.
        //                  Blocks new hit flash / new ClientHit / clearly-non-death AttackPhase,
        //                  but MUST NOT block HostDeathEvent apply / death visual / Animator Dead.
        //   TerminalDead = death visual actually applied (Npc.Die success OR visual-only shim).
        //                  Blocks all non-death overrides so the corpse can't twitch/stand-freeze.
        // HealthState hp<=0 must only ever produce PendingDead — never TerminalDead directly.
        // ----------------------------------------------------------------
        private static readonly HashSet<int> _clientTerminalDeadHostIdx = new HashSet<int>();
        private sealed class PendingDeadEntry
        {
            public float MarkedAt;
            public string Reason = "";
            public bool VisualFallbackAttempted;
        }
        private static readonly Dictionary<int, PendingDeadEntry> _clientPendingDeadHostIdx = new Dictionary<int, PendingDeadEntry>();

        // PendingDead counters.
        private static int _pendingDeadMarked;
        private static int _pendingDeadHostDeathApplied;
        private static int _pendingDeadVisualFallbackAttempted;
        private static int _pendingDeadVisualFallbackSucceeded;
        private static int _pendingDeadVisualFallbackFailed;
        private static int _pendingDeadBlockedHitFlash;
        private static int _pendingDeadBlockedClientHit;
        // TerminalDead counters.
        private static int _terminalDeadMarkedAfterDie;
        private static int _terminalDeadMarkedAfterVisualFallback;
        private static int _terminalDeadMarkedFromHealthOnly; // MUST stay 0 — guard against regression.
        private static int _terminalDeadBlockedAttackPhase;
        private static int _terminalDeadBlockedGenericReplay;
        private static int _terminalDeadBlockedMovement;
        private static int _terminalDeadBlockedHitReaction;
        private static int _terminalDeadHealthUpdatesIgnored;
        private static int _terminalDeadDeathReapplySkipped;
        // ClientHit skip-on-dead counters.
        private static int _clientHitSkipPendingDead;
        private static int _clientHitSkipTerminalDead;

        // ----------------------------------------------------------------
        // Phase 5.3-E: Host-authoritative level manifest + diff/reconcile.
        // ----------------------------------------------------------------
        private static int _hostManifestBuilt;
        private static int _clientManifestBuilt;
        private static int _hostManifestSent;            // host: number of manifests broadcast
        private static int _clientManifestReceived;
        private static int _manifestHashMatch;
        private static int _manifestHashMismatch;
        private static int _generationHashMatch;
        private static int _generationHashMismatch;
        private static int _runtimeHashMatch;
        private static int _runtimeHashMismatch;
        // Phase 5.3-H I: generation-hash stability diagnosis (self, same run key across rebuilds).
        private static string _lastGenRunKey = "";
        private static List<string>? _lastGenSignature;
        private static int _generationHashStableSameRevision;
        private static int _generationHashChangedSameRevision;
        private static int _manifestRoomMismatch;
        private static int _manifestUnitMismatch;
        private static int _manifestSpecialMismatch;
        private static int _manifestHostOnlyUnits;
        private static int _manifestClientOnlyUnits;
        private static int _manifestClientOnlyCombatQuarantined;
        private static int _manifestHostEnemyBoundExisting;
        private static int _manifestHostEnemyBindFailedNoCandidate;
        private static int _manifestHostEnemyBindFailedAmbiguous;
        private static int _manifestHostEnemyModifierMismatch;
        private static int _manifestSeedMismatch;
        private static float _clientFirstLevelEntitySeenAt;  // when client first had level entities
        private static bool _clientManifestProvisionalLogged;
        // Phase 5.3-H A: run-state gate for incoming host manifests.
        private static int _manifestDeferredSceneMismatch;
        private static int _manifestDeferredLevelMismatch;
        private static int _manifestDeferredSeedMismatch;
        private static int _manifestProcessedMatchingRun;
        private static int _manifestDroppedStaleRun;
        // Phase 5.3-M P0-C: did a level that previously kept deferring now accept after the run-state fix?
        private static int _manifestAcceptedAfterRunStateFix;
        private static int _manifestDeferredLevelMismatchAfterFix;
        private static NetLevelManifest? _deferredHostManifest;
        // Phase 5.3-H B: player exclusion.
        private static int _manifestPlayerExcluded;
        private static int _quarantinePlayerSkipped;
        // Phase 5.3-E quarantine reason breakdown (P0 #6).
        private static int _manifestClientOnlyQuarantineNoLocalKey;
        private static int _manifestClientOnlyQuarantineNoRuntime;
        private static int _manifestClientOnlyAlreadyQuarantined;
        private static int _manifestClientOnlyNonCombatSkipped;
        private static int _manifestClientOnlyQuarantineApplied;
        private static int _manifestClientOnlyQuarantineFailed;
        // Phase 5.3-G: current-reconcile-pass snapshot (overwritten each reconcile) vs cumulative
        // totals above — keeps the summary from comparing a running total against a single pass.
        private static int _currentClientOnlyUnits;
        private static int _currentClientOnlyCombatUnits;
        private static int _currentClientOnlyNonCombatUnits;
        private static int _currentClientOnlyQuarantineApplied;
        private static int _currentClientOnlyAlreadyQuarantined;
        private static int _currentClientOnlyNoRuntime;
        private static int _totalClientOnlyQuarantineApplied;

        // ----------------------------------------------------------------
        // Phase 5.3-F: ClientHit visual chain (Host plays + broadcasts; Client mirrors).
        // ----------------------------------------------------------------
        private static int _hostHitVisualPlayed;
        private static int _hostHitVisualFailedNoNpc;
        private static int _hostHitVisualEventSent;
        private static int _hostHitVisualSeq;
        private static int _clientHitVisualEventRecv;
        private static int _clientHitVisualPlayed;
        private static int _clientHitVisualSkipNoBinding;
        private static int _clientHitVisualSkipPendingDead;
        private static int _clientHitVisualSkipTerminalDead;
        private static int _clientHitVisualDuplicateSeq;
        // Phase 5.3-G fatal-hit visual tracking.
        private static int _hostHitFatalVisualPlayed;
        private static int _clientHitFatalVisualPlayed;
        private static int _clientHitVisualSkippedFatalTerminalDead;
        private static readonly Dictionary<int, int> _clientHitVisualLastSeqByHostIdx = new Dictionary<int, int>();

        // ----------------------------------------------------------------
        // Phase 5.3-G: per-hostIdx drift diagnosis.
        // ----------------------------------------------------------------
        private sealed class DriftStat
        {
            public string Unit = "";
            public int    Count;
            public float  SumHostError;
            public float  MaxHostError;
            public float  LastHostError;
            public int    Snaps;
            public int    Repeated;   // consecutive frames with large (>4m) error
        }
        private static readonly Dictionary<int, DriftStat> _driftByHostIdx = new Dictionary<int, DriftStat>();
        private static int _puppetTransformCorrectionLarge;   // single correction > snapDistance
        private static int _puppetTransformCorrectionRepeated; // repeated large error on same idx
        private static int _localNavMeshStillEnabledOnHostBound;
        private static int _localAiCanMoveOnHostBound;
        private static int _rvoStillEnabledOnHostBound;
        private static int _hostBoundPuppetsAudited;

        private static void NoteDrift(string key, float hostError, bool snapped, string unit)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (!ClientLocalKeyToHostSpawnIndex.TryGetValue(key, out int hostIdx)) return; // host-bound only
            if (!_driftByHostIdx.TryGetValue(hostIdx, out var st))
            {
                st = new DriftStat { Unit = unit ?? "" };
                _driftByHostIdx[hostIdx] = st;
            }
            st.Count++;
            st.SumHostError += hostError;
            st.LastHostError = hostError;
            if (hostError > st.MaxHostError) st.MaxHostError = hostError;
            if (snapped) st.Snaps++;
            if (hostError > 4f)
            {
                st.Repeated++;
                _puppetTransformCorrectionRepeated++;
            }
            float snapDistance = Plugin.Cfg.EnemyStateSnapshotSnapDistance.Value;
            if (hostError > Mathf.Max(snapDistance, 0.1f)) _puppetTransformCorrectionLarge++;
        }

        // Audits host-bound puppets to see whether local NavMesh / AI movement / RVO are still
        // active — i.e. whether the corpse is fighting the host transform stream. Diagnostic only.
        private static void AuditHostBoundPuppetLocalAi()
        {
            _localNavMeshStillEnabledOnHostBound = 0;
            _localAiCanMoveOnHostBound = 0;
            _rvoStillEnabledOnHostBound = 0;
            _hostBoundPuppetsAudited = 0;
            foreach (var pair in ActiveEnemyPuppets)
            {
                if (!ClientLocalKeyToHostSpawnIndex.ContainsKey(pair.Key)) continue;
                var rec = pair.Value;
                _hostBoundPuppetsAudited++;
                try
                {
                    // NavMeshAgent enabled? (resolve by type name to avoid an AIModule assembly ref)
                    var navComp = TryFindComponentByTypeName(rec.Npc!, "NavMeshAgent")
                                  ?? (rec.AiAgent != null ? TryFindComponentByTypeName(rec.AiAgent, "NavMeshAgent") : null);
                    if (navComp is Behaviour nav && nav != null && nav.enabled)
                        _localNavMeshStillEnabledOnHostBound++;

                    // AiAgent CanMove / RVO still on?
                    if (rec.AiAgent != null)
                    {
                        var canMove = ReadBoolMember(rec.AiAgent, "canMove") ?? ReadBoolMember(rec.AiAgent, "CanMove");
                        if (canMove == true) _localAiCanMoveOnHostBound++;
                        var rvo = ReadBoolMember(rec.AiAgent, "rvoEnabled") ?? ReadBoolMember(rec.AiAgent, "useRVO");
                        if (rvo == true) _rvoStillEnabledOnHostBound++;
                    }
                }
                catch { }
            }
        }

        private static bool? ReadBoolMember(object obj, string name)
        {
            try
            {
                var t = obj.GetType();
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null && f.FieldType == typeof(bool)) return (bool)f.GetValue(obj);
                var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null && p.PropertyType == typeof(bool) && p.CanRead) return (bool)p.GetValue(obj);
            }
            catch { }
            return null;
        }

        private static string FormatTopDrift(int topN)
        {
            if (_driftByHostIdx.Count == 0) return "none";
            var top = _driftByHostIdx
                .OrderByDescending(kv => kv.Value.MaxHostError)
                .Take(topN);
            var sb = new System.Text.StringBuilder();
            foreach (var kv in top)
            {
                var s = kv.Value;
                float avg = s.Count > 0 ? s.SumHostError / s.Count : 0f;
                sb.Append($" [idx={kv.Key} unit={s.Unit} avgErr={avg:F2} maxErr={s.MaxHostError:F2} snaps={s.Snaps} repeated={s.Repeated} n={s.Count}]");
            }
            return sb.ToString();
        }

        // Phase 5.3-F/H LevelGeneration trace counters (incremented by LevelGenTracePatches).
        private static int _levelGenFinalizeConnectionTraced;
        private static int _levelGenConnectorFinalizeSpawnTraced;
        private static int _levelGenExtraRoomTraced;
        private static int _levelGenDoorBlockerRecorded;
        private static int _levelGenStepTraced;
        private static int _levelGenNodeCoroutineTraced;

        /// <summary>Called by LevelGenTracePatches to feed trace activity into the unified summary.</summary>
        public static void NoteLevelGenTrace(string kind, int doorBlockerDelta = 0)
        {
            switch (kind)
            {
                case "FinalizeConnection":     _levelGenFinalizeConnectionTraced++; break;
                case "ConnectorFinalizeSpawn": _levelGenConnectorFinalizeSpawnTraced++; break;
                case "ExtraRoom":              _levelGenExtraRoomTraced++; break;
                case "LevelStep":              _levelGenStepTraced++; break;
                case "NodeCoroutine":          _levelGenNodeCoroutineTraced++; break;
            }
            if (doorBlockerDelta != 0) _levelGenDoorBlockerRecorded += doorBlockerDelta;
        }

        /// <summary>Compact local run key (scene:level:seed:revision) for cross-host/client trace correlation.</summary>
        public static string GetLocalRunKey()
        {
            if (NetRunStateBridge.TryGetLocalRunState(out var run) && run.HasLevel)
                return $"{run.ChapterName}:{run.LevelIndex}:{(run.HasLevelSeed ? run.LevelSeed.ToString() : "?")}:r{run.Revision}";
            return "<no-run>";
        }

        // ----------------------------------------------------------------
        // Phase 5.3-D P0-1: Visual-only hit flash via native Npc.DoWhiteFlash().
        //   Primary:  Npc.DoWhiteFlash()
        //   Fallback: Npc.SetHitEffect(1)
        //   Fallback: material.SetFloat("_HitTime", Time.time) + SetFloat("_HitType", 1)
        //   Last:     MaterialPropertyBlock _Color/_EmissionColor tint (not preferred)
        // Never calls ReceiveDamage.
        // ----------------------------------------------------------------
        private sealed class PendingHitFlash
        {
            public Renderer[] Renderers = System.Array.Empty<Renderer>();
            public float ResetAt;
        }
        private static readonly List<PendingHitFlash> _pendingHitFlashes = new List<PendingHitFlash>(32);
        private static readonly Dictionary<int, int> _clientDamageVisualLastSeqByHostIdx = new Dictionary<int, int>();
        private static readonly int _hitFlashColorPropId    = Shader.PropertyToID("_Color");
        private static readonly int _hitFlashEmissionPropId = Shader.PropertyToID("_EmissionColor");
        private static readonly int _hitFlashHitTimePropId  = Shader.PropertyToID("_HitTime");
        private static readonly int _hitFlashHitTypePropId  = Shader.PropertyToID("_HitType");
        // Per-Npc-type reflection caches for the native flash methods.
        private static readonly Dictionary<string, MethodInfo?> _npcDoWhiteFlashCache = new Dictionary<string, MethodInfo?>();
        private static readonly Dictionary<string, MethodInfo?> _npcSetHitEffectCache = new Dictionary<string, MethodInfo?>();
        private static readonly HashSet<string> _flashApiLoggedTypes = new HashSet<string>();
        private static int _damageVisualReactionsPlayed;
        private static int _dmgVisualNativeDoWhiteFlash;
        private static int _dmgVisualNativeSetHitEffect;
        private static int _dmgVisualMaterialHitTime;
        private static int _dmgVisualFallbackColor;
        private static int _dmgVisualFailedNoNpc;
        private static int _dmgVisualFailedNoMethod;
        private static int _dmgVisualFailedNoMaterial;
        private static int _damageVisualReactionSkippedTerminalDead;
        private static int _damageVisualReactionSkippedPendingDead;
        private static int _damageVisualReactionSkippedNoBinding;
        private static int _damageVisualReactionSkippedDuplicateSeq;

        // Phase 5.2: tombstone cache — records recently released binding so death events can
        // distinguish "puppet was destroyed before host death arrived" from "never bound".
        private struct BindingTombstone
        {
            public int    HostSpawnIndex;
            public string LocalKey;
            public string UnitIdentifier;
            public float  ReleasedAt;
            public string ReleaseReason;
        }
        private static readonly Dictionary<int, BindingTombstone> _bindingTombstones = new Dictionary<int, BindingTombstone>();
        private const float TombstoneMaxAge = 60f;

        // Phase 5.2 P0-2: pending HealthState queue — when Client receives a HealthState but
        // has no binding yet, cache the latest state per hostIdx so it can be applied when
        // the roster binding arrives later.
        private sealed class PendingHealthEntry
        {
            public NetHostEnemyHealthState State  { get; set; } = null!;
            public float ReceivedAt               { get; set; }
        }
        private static readonly Dictionary<int, PendingHealthEntry> _pendingHealthByHostIdx = new Dictionary<int, PendingHealthEntry>();
        private const float PendingHealthMaxAge = 8f;
        private static int _clientHealthStatesPendingQueued;
        private static int _clientHealthStatesPendingApplied;
        private static int _clientHealthStatesPendingExpired;

        // Phase 4.4.0-O3-C: One-to-one binding counters.
        private static int _rosterOneToOneBound;
        private static int _rosterPreservedBound; // RT3-A7: bindings kept stable across a reconcile (not re-matched by position)
        // Phase 4.4.0-O3-C: Death-via-binding counters.
        private static int _deathAppliedByBinding;
        private static int _deathAppliedBoundDespiteDrift;
        private static int _deathBoundDriftWarning;
        private static int _deathRejectedUnboundNetEntity;
        // Phase 4.4.0-O3-C: Quarantine counters.
        private static int _clientOnlyCombatQuarantined;
        private static int _quarantinedCombatSuppressed;

        private static int _nextSpawnIndex;
        private static int _spawnEvents;
        private static int _newSpawns;
        private static int _duplicateSpawnEvents;
        private static int _damageEvents;
        private static int _deathEvents;
        private static int _suppressedContextEvents;
        private static int _deathMirrorSequence;
        private static int _hostDeathApplyDepth;
        private static int _enemyStateTargetsQueued;
        private static int _enemyStateTargetsApplied;
        private static int _enemyStateTargetsSnapped;
        private static int _clientEnemyPuppetsActivated;
        private static int _clientEnemyPuppetsReleased;
        private static int _clientEnemyPuppetTargetClears;
        private static int _clientEnemyPuppetTargetBlocks;
        private static int _clientEnemyPuppetCombatBlocks;
        private static int _enemyCombatProbeEvents;

        // Phase 5.4-C boss/enemy target authority counters.
        private static int _enemyTargetLocalCleared;
        private static int _enemyTargetLocalOverwrite;
        private static int _enemyTargetNoKnownMember;
        private static int _enemyTargetBossMembersDiscovered;
        private static int _enemyTargetBossSuppressionApplied;
        private static int _enemyTargetSuppressionFailed;
        private static int _enemyTargetHostTargetApplied;
        private static readonly HashSet<string> _targetProbeDumpedTypes = new HashSet<string>();
        private static int _hostEnemyCombatActionSequence;
        private static int _hostEnemyCombatActionMarks;
        private static int _clientCombatAnimatorTriggerApplies;
        private static int _clientCombatAnimatorStateApplies;
        private static int _clientCombatVisualActionReplays;
        private static int _clientCombatAnimatorFallbacks;
        private static int _clientVisualProjectileMirrors;
        private static int _projectileProbeEvents;
        private static int _hostEnemyDamageChecks;
        private static int _hostEnemyDamageHits;
        private static int _clientGenericCombatAnimatorStateApplies;
        private static int _hostEnemyAiIntentMarks;
        private static int _clientEnemyAiIntentApplies;
        private static int _clientEnemyAiIntentCorrections;
        private static int _clientPuppetIntentReplayDepth;
        private static int _clientPuppetInternalTargetClearDepth;
        private static int _clientPuppetCombatVisualReplayDepth;
        // Phase 4.4.0-O counters.
        private static int _clientIntentWindows;
        private static int _clientAuthorizedAttackPasses;
        private static int _clientUnauthorizedAttackBlocks;
        private static int _clientAuthorizedMeleeEvents;
        private static int _clientSuppressedNativeEnemyDamage;
        private static int _clientEnemyNativeDamageSuppressDepth;
        // Phase 4.4.0-O2 counters.
        private static int _clientCombatRootReplays;
        private static int _clientCombatRootReplaySkippedDuplicate;
        private static int _clientAuthorizedChildPasses;
        private static int _clientBlockedSpontaneousCombatCalls;
        private static int _driftSkippedIntentLocomotion;
        private static int _driftSkippedAuthorizedCombat;
        private static int _hardDriftCorrections;
        private static int _softDriftCorrections;
        private static int _genericCombatStateSkippedDuringAuthorizedIntent;
        private static int _clientAuthorizedCombatRootReplayDepth;
        // Phase 4.4.0-O3 counters.
        private static int _traderExcludedFromEnemySync;
        private static int _nonCombatExcludedFromEnemySync;
        private static int _deathClaimRejectedNonCombat;
        private static int _combatProbeRejectedNonCombat;
        // Phase 4.4.0-O3-B root replay counters.
        private static int _clientRootReplayAttempts;
        private static int _clientRootReplaySkippedDuplicate;
        private static int _clientRootReplayUnsupported;
        private static int _clientRootReplayFailed;
        // Phase 4.4.0-O3-B child gate counters.
        private static int _clientAuthorizedChildAfterRoot;
        private static int _clientChildBlockedBeforeRootReplay;
        // Phase 4.4.0-O3-B entity type mismatch counters.
        private static int _entityTypeMismatchRejected;
        private static int _deathMirrorRejectedTypeMismatch;
        private static int _stateApplyRejectedTypeMismatch;
        // Phase 4.4.0-O3-B roster counters.
        private static int _hostRosterRecordsSent;
        private static int _clientRosterRecordsReceived;
        private static int _clientRosterBound;
        private static int _clientRosterHostOnlyMissing;
        private static int _clientRosterClientOnlyQuarantined;
        private static int _clientRosterTypeMismatch;
        private static int _clientRosterFingerprintMismatch;
        private static float _lastSummaryAt;

        public static void Tick()
        {
            if (!IsEnabled()) return;

            TryFlushPendingStableSpawnLogs();
            ApplyPendingEnemyStateTargets();
            UpdateCombatAnimatorBoolResets();
            UpdateClientVisualProjectiles();
            UpdateClientHitFlashes();
            UpdateClientPendingDead();
            FlushPendingClientHitDamage();
            TryProcessDeferredManifest();
            ReleaseStaleEnemyPuppets();
            PruneExpiredHostEnemyCombatActions();
            PruneExpiredHostEnemyAiIntents();
            PruneExpiredAuthorizedIntentWindows(Time.realtimeSinceStartup);
            ExpireOldPendingHealthStates();
            ExpireStalePendingHostBinds();   // Phase 5.7-RB
            MaybeLogSummary();
        }

        public static void ClearLevelScoped(string source)
        {
            if (!IsEnabled()) return;

            int count = EntitiesByLocalId.Count;
            ReleaseAllEnemyPuppets("level scoped clear: " + source);
            EntitiesByLocalId.Clear();
            PendingEnemyStateTargets.Clear();
            HostEnemyStateSendCacheBySpawnIndex.Clear();
            HostEnemyCombatActionsByNpcId.Clear();
            HostEnemyAiIntentsByNpcId.Clear();
            EnemyCombatProbeLastLogAtById.Clear();
            ProjectileProbeLastLogAtByKey.Clear();
            HostEnemyDamageLastAtByKey.Clear();
            DestroyAllClientVisualProjectiles();
            PendingAnimatorBoolResets.Clear();
            PendingStableSpawnLogs.Clear();
            NetPlayerLifeManager.ClearLevelScoped(source);
            _nextSpawnIndex = 0;
            _enemyStateTargetsQueued = 0;
            _enemyStateTargetsApplied = 0;
            _enemyStateTargetsSnapped = 0;
            _clientEnemyPuppetsActivated = 0;
            _clientEnemyPuppetsReleased = 0;
            _clientEnemyPuppetTargetClears = 0;
            _clientEnemyPuppetTargetBlocks = 0;
            _clientEnemyPuppetCombatBlocks = 0;
            _enemyCombatProbeEvents = 0;
            _enemyTargetLocalCleared = 0;
            _enemyTargetLocalOverwrite = 0;
            _enemyTargetNoKnownMember = 0;
            _enemyTargetBossMembersDiscovered = 0;
            _enemyTargetBossSuppressionApplied = 0;
            _enemyTargetSuppressionFailed = 0;
            _enemyTargetHostTargetApplied = 0;
            _targetProbeDumpedTypes.Clear();
            _hostEnemyCombatActionSequence = 0;
            _hostEnemyCombatActionMarks = 0;
            _clientCombatAnimatorTriggerApplies = 0;
            _clientCombatAnimatorStateApplies = 0;
            _clientCombatVisualActionReplays = 0;
            _clientCombatAnimatorFallbacks = 0;
            _clientVisualProjectileMirrors = 0;
            _projectileProbeEvents = 0;
            _hostEnemyDamageChecks = 0;
            _hostEnemyDamageHits = 0;
            _clientGenericCombatAnimatorStateApplies = 0;
            _hostEnemyAiIntentMarks = 0;
            _clientEnemyAiIntentApplies = 0;
            _clientEnemyAiIntentCorrections = 0;
            _clientPuppetIntentReplayDepth = 0;
            _clientAuthorizedIntentByNpcId.Clear();
            _clientIntentWindows = 0;
            _clientAuthorizedAttackPasses = 0;
            _clientUnauthorizedAttackBlocks = 0;
            _clientAuthorizedMeleeEvents = 0;
            _clientSuppressedNativeEnemyDamage = 0;
            _clientEnemyNativeDamageSuppressDepth = 0;
            _clientCombatRootReplays = 0;
            _clientCombatRootReplaySkippedDuplicate = 0;
            _clientAuthorizedChildPasses = 0;
            _clientBlockedSpontaneousCombatCalls = 0;
            _driftSkippedIntentLocomotion = 0;
            _driftSkippedAuthorizedCombat = 0;
            _hardDriftCorrections = 0;
            _softDriftCorrections = 0;
            _genericCombatStateSkippedDuringAuthorizedIntent = 0;
            _clientAuthorizedCombatRootReplayDepth = 0;
            _traderExcludedFromEnemySync = 0;
            _nonCombatExcludedFromEnemySync = 0;
            _deathClaimRejectedNonCombat = 0;
            _combatProbeRejectedNonCombat = 0;
            _clientRootReplayAttempts = 0;
            _clientRootReplaySkippedDuplicate = 0;
            _clientRootReplayUnsupported = 0;
            _clientRootReplayFailed = 0;
            _clientAuthorizedChildAfterRoot = 0;
            _clientChildBlockedBeforeRootReplay = 0;
            _entityTypeMismatchRejected = 0;
            _deathMirrorRejectedTypeMismatch = 0;
            _stateApplyRejectedTypeMismatch = 0;
            _hostRosterRecordsSent = 0;
            _clientRosterRecordsReceived = 0;
            _clientRosterBound = 0;
            _clientRosterHostOnlyMissing = 0;
            _clientRosterClientOnlyQuarantined = 0;
            _clientRosterTypeMismatch = 0;
            _clientRosterFingerprintMismatch = 0;
            ClientHostToLocalKeyByHostSpawnIndex.Clear();
            ClientLocalKeyToHostSpawnIndex.Clear();
            _interestEngagedExempt = 0;             // RB2: engaged-enemy full-rate exemptions
            _interestDiagLogged = 0;                // RB3: per-level interest diag budget
            _snapCollExclLogged = 0;                // SC: per-level snapshot-collection exclusion diag budget
            _lastSnapCollLogAt = 0f;
            _clientLastSnapshotRecvByHostIdx.Clear(); // SC2
            _retroBindDiagLogged = 0;               // SC4: per-level retro-bind defer diag budget
            _runtimeSpawnBindingsByHostIdx.Clear(); // RT3-A6: runtime bindings are level-scoped
            _pendingHostBindLedger.Clear();         // RB: retro-bind ledger is level-scoped
            _retroBindLedgerAdds = 0;
            _retroBindSuccess = 0;
            _retroBindAmbiguousDeferred = 0;
            _retroBindLedgerExpired = 0;
            ClientQuarantinedEntities.Clear();
            _clientRosterRevision = -1;
            FarEnemyLastSentAtBySpawnIndex.Clear();
            ClientAttackPhaseLastSeqBySpawnIndex.Clear();
            _hostPlayerPositionHintValid = false;
            _rosterOneToOneBound = 0;
            _deathAppliedByBinding = 0;
            _deathAppliedBoundDespiteDrift = 0;
            _deathBoundDriftWarning = 0;
            _deathRejectedUnboundNetEntity = 0;
            _clientOnlyCombatQuarantined = 0;
            _quarantinedCombatSuppressed = 0;
            // Phase 5.1: clear health caches and event sequences.
            ClientPuppetHealthBySpawnIndex.Clear();
            ClientPuppetMaxHealthBySpawnIndex.Clear();
            HostDamageEventSeqBySpawnIndex.Clear();
            _hostEnemyDamageEventsSent = 0;
            _hostEnemyHealthStatesSent = 0;
            _clientEnemyDamageEventsReceived = 0;
            _clientEnemyHealthStatesReceived = 0;
            _clientEnemyHealthStatesApplied = 0;
            _clientEnemyHealthApplySkippedNoBinding = 0;
            _hostDeathAppliedByRosterBinding = 0;
            _hostDeathAppliedAfterSnap = 0;
            _hostDeathRejectedNoBinding = 0;
            _hostDeathLateBindAttempts = 0;
            _hostDeathLateBindSuccess = 0;
            _clientDeathClaimsSuppressedByHostAuthority = 0;
            _hostDeathTombstoneHit = 0;
            _hostDeathNeverBound = 0;
            _bindingTombstones.Clear();
            _pendingHealthByHostIdx.Clear();
            _clientHealthStatesPendingQueued = 0;
            _clientHealthStatesPendingApplied = 0;
            _clientHealthStatesPendingExpired = 0;
            _clientHealthApplyDisabled = 0;
            _clientHealthNoCurrentHp = 0;
            _clientHealthNoEntity = 0;
            _clientHealthNoRuntimeObj = 0;
            _clientHealthUnityDestroyed = 0;
            _clientHealthNoStats = 0;
            _clientHealthSetStatusMissing = 0;
            _clientHealthEnumArgFailed = 0;
            _clientHealthSetStatusFailed = 0;
            _clientHealthWriteFailed = 0;
            _clientHealthWriteReadBackUnchanged = 0;
            _puppetReleaseSuppressedAwaitingHost = 0;
            _puppetStaleReleaseSuppressed = 0;
            _clientHitRequestSeq = 0;
            _clientHitRequestsSent = 0;
            _clientHitRequestsSkippedNoPuppet = 0;
            _clientHitRequestsSkippedNoBinding = 0;
            _clientPuppetNonPlayerDamageIgnored = 0;
            _hostHitRequestsRecv = 0;
            _hostHitRequestsRejectedScene = 0;
            _hostHitRequestsRejectedNoTarget = 0;
            _hostHitRequestsRejectedTypeMismatch = 0;
            _hostHitRequestsRejectedDead = 0;
            _hostHitRequestsRejectedRateLimit = 0;
            _hostHitRequestsCoalesced = 0;
            _hostHitPendingDamageByHostIdx.Clear();
            _hostHitRequestsAccepted = 0;
            _hostHitRequestsDamageApplied = 0;
            _hostHitRequestsDamageFailed = 0;
            _hostHitRequestLastAtByHostIdx.Clear();
            _hostHitResultHealthStateSent = 0;
            _clientLocalHitPredicted = 0;
            _clientLocalHitConfirmed = 0;
            _clientPendingHitByHostIdx.Clear();
            // Phase 5.3-D two-phase death + hit flash.
            _clientTerminalDeadHostIdx.Clear();
            _clientPendingDeadHostIdx.Clear();
            _pendingDeadMarked = 0;
            _pendingDeadHostDeathApplied = 0;
            _pendingDeadVisualFallbackAttempted = 0;
            _pendingDeadVisualFallbackSucceeded = 0;
            _pendingDeadVisualFallbackFailed = 0;
            _pendingDeadBlockedHitFlash = 0;
            _pendingDeadBlockedClientHit = 0;
            _terminalDeadMarkedAfterDie = 0;
            _terminalDeadMarkedAfterVisualFallback = 0;
            _terminalDeadMarkedFromHealthOnly = 0;
            _terminalDeadBlockedAttackPhase = 0;
            _terminalDeadBlockedGenericReplay = 0;
            _terminalDeadBlockedMovement = 0;
            _terminalDeadBlockedHitReaction = 0;
            _terminalDeadHealthUpdatesIgnored = 0;
            _terminalDeadDeathReapplySkipped = 0;
            _clientHitSkipPendingDead = 0;
            _clientHitSkipTerminalDead = 0;
            _pendingHitFlashes.Clear();
            _clientDamageVisualLastSeqByHostIdx.Clear();
            _damageVisualReactionsPlayed = 0;
            _dmgVisualNativeDoWhiteFlash = 0;
            _dmgVisualNativeSetHitEffect = 0;
            _dmgVisualMaterialHitTime = 0;
            _dmgVisualFallbackColor = 0;
            _dmgVisualFailedNoNpc = 0;
            _dmgVisualFailedNoMethod = 0;
            _dmgVisualFailedNoMaterial = 0;
            _damageVisualReactionSkippedTerminalDead = 0;
            _damageVisualReactionSkippedPendingDead = 0;
            _damageVisualReactionSkippedNoBinding = 0;
            _damageVisualReactionSkippedDuplicateSeq = 0;
            // Phase 5.3-E manifest.
            _hostManifestBuilt = 0;
            _clientManifestBuilt = 0;
            _hostManifestSent = 0;
            _clientManifestReceived = 0;
            _manifestHashMatch = 0;
            _manifestHashMismatch = 0;
            _manifestRoomMismatch = 0;
            _manifestUnitMismatch = 0;
            _manifestSpecialMismatch = 0;
            _manifestHostOnlyUnits = 0;
            _manifestClientOnlyUnits = 0;
            _manifestClientOnlyCombatQuarantined = 0;
            _manifestHostEnemyBoundExisting = 0;
            _manifestHostEnemyBindFailedNoCandidate = 0;
            _manifestHostEnemyBindFailedAmbiguous = 0;
            _manifestHostEnemyModifierMismatch = 0;
            _manifestSeedMismatch = 0;
            _clientFirstLevelEntitySeenAt = 0f;
            _clientManifestProvisionalLogged = false;
            _manifestDeferredSceneMismatch = 0;
            _manifestDeferredLevelMismatch = 0;
            _manifestDeferredSeedMismatch = 0;
            _manifestProcessedMatchingRun = 0;
            _manifestDroppedStaleRun = 0;
            _manifestAcceptedAfterRunStateFix = 0;
            _manifestDeferredLevelMismatchAfterFix = 0;
            _deferredHostManifest = null;
            _manifestPlayerExcluded = 0;
            _quarantinePlayerSkipped = 0;
            _generationHashMatch = 0;
            _generationHashMismatch = 0;
            _runtimeHashMatch = 0;
            _runtimeHashMismatch = 0;
            _generationHashStableSameRevision = 0;
            _generationHashChangedSameRevision = 0;
            _lastGenRunKey = "";
            _lastGenSignature = null;
            _manifestClientOnlyQuarantineNoLocalKey = 0;
            _manifestClientOnlyQuarantineNoRuntime = 0;
            _manifestClientOnlyAlreadyQuarantined = 0;
            _manifestClientOnlyNonCombatSkipped = 0;
            _manifestClientOnlyQuarantineApplied = 0;
            _manifestClientOnlyQuarantineFailed = 0;
            // Phase 5.3-F hit visual.
            _hostHitVisualPlayed = 0;
            _hostHitVisualFailedNoNpc = 0;
            _hostHitVisualEventSent = 0;
            _hostHitVisualSeq = 0;
            _clientHitVisualEventRecv = 0;
            _clientHitVisualPlayed = 0;
            _clientHitVisualSkipNoBinding = 0;
            _clientHitVisualSkipPendingDead = 0;
            _clientHitVisualSkipTerminalDead = 0;
            _clientHitVisualDuplicateSeq = 0;
            _hostHitFatalVisualPlayed = 0;
            _clientHitFatalVisualPlayed = 0;
            _clientHitVisualSkippedFatalTerminalDead = 0;
            _clientHitVisualLastSeqByHostIdx.Clear();
            _levelGenFinalizeConnectionTraced = 0;
            _levelGenConnectorFinalizeSpawnTraced = 0;
            _levelGenExtraRoomTraced = 0;
            _levelGenDoorBlockerRecorded = 0;
            _levelGenStepTraced = 0;
            _levelGenNodeCoroutineTraced = 0;
            // Phase 5.3-G drift + quarantine-current.
            _driftByHostIdx.Clear();
            _puppetTransformCorrectionLarge = 0;
            _puppetTransformCorrectionRepeated = 0;
            _localNavMeshStillEnabledOnHostBound = 0;
            _localAiCanMoveOnHostBound = 0;
            _rvoStillEnabledOnHostBound = 0;
            _hostBoundPuppetsAudited = 0;
            _currentClientOnlyUnits = 0;
            _currentClientOnlyCombatUnits = 0;
            _currentClientOnlyNonCombatUnits = 0;
            _currentClientOnlyQuarantineApplied = 0;
            _currentClientOnlyAlreadyQuarantined = 0;
            _currentClientOnlyNoRuntime = 0;
            _totalClientOnlyQuarantineApplied = 0;

            if (count > 0 || Plugin.Cfg.EnableDebugLog.Value)
                Plugin.Log.Info($"[GameplayProbe] Clear level scoped entities count={count} source={Clean(source)}");
        }

        public static void ReportSpawn(object? entity, string source, string category)
        {
            if (!IsEnabled()) return;
            if (entity == null) return;

            // Plan B: never track a remote-player proxy/ghost unit. It is a host-only construct; tracking it would leak it
            // into BuildHostWorldRoster / BuildLevelManifest and broadcast it to clients as a phantom "Other" entity they
            // cannot match (LogOutput98: 62 lines of "No local match / special mismatch host=RemotePlayerGhost").
            if (SULFURTogether.Networking.RemotePlayerTargetProxyManager.IsProxyUnit(entity)) return;

            category = CleanCategory(category, entity);
            if (!ShouldTrackCategory(category)) return;

            _spawnEvents++;

            var id = NetGameplayEntityId.FromObject(entity);
            string key = id.LocalInstanceId;
            if (string.IsNullOrWhiteSpace(key) || key == "null")
                key = id.CandidateKey;

            bool isNew = !EntitiesByLocalId.TryGetValue(key, out var snapshot);
            if (isNew)
            {
                snapshot = BuildSnapshot(entity, id, source, category);
                snapshot.SpawnIndex = ++_nextSpawnIndex;
                EntitiesByLocalId[key] = snapshot;
                _newSpawns++;

                // Phase 5.7-RB: a host roster/manifest record for this enemy may have arrived before we spawned it
                // (timing race → parked as hostOnly). Bind it now that the matching local entity exists.
                if (NetConfig.GetMode() == NetMode.Client)
                    TryRetroactiveBindNewLocalEntity(snapshot);

                // Phase 5.3-E: note when the client first generated level entities, so the manifest
                // diff can report whether the client built a provisional world before the host's
                // authoritative manifest arrived.
                if (NetConfig.GetMode() == NetMode.Client && _clientFirstLevelEntitySeenAt <= 0f)
                {
                    _clientFirstLevelEntitySeenAt = Time.realtimeSinceStartup;
                    if (Plugin.Cfg.EnableHostLevelManifest.Value && _clientManifestReceived == 0 && !_clientManifestProvisionalLogged)
                    {
                        _clientManifestProvisionalLogged = true;
                        NetLogger.Info("[LevelManifest] Client generated provisional level before host manifest");
                    }
                }
            }
            else
            {
                _duplicateSpawnEvents++;
                snapshot!.LastSeenAt = Time.realtimeSinceStartup;
                snapshot.SetRuntimeObject(entity);
                if (!string.IsNullOrWhiteSpace(source)) snapshot.Source = Clean(source);
                if (!snapshot.HasPosition && TryGetPosition(entity, out var pos))
                {
                    snapshot.HasPosition = true;
                    snapshot.Position = pos;
                }
                RefreshSceneContext(snapshot);
            }

            if (CanLogWithCurrentContext(out _))
            {
                if (snapshot.PendingStableContextLog)
                {
                    snapshot.PendingStableContextLog = false;
                    PendingStableSpawnLogs.Remove(key);
                }

                if (isNew && Plugin.Cfg.LogGameplayEntitySpawn.Value)
                    Plugin.Log.Info(snapshot.FormatSpawnLine(""));
            }
            else
            {
                snapshot.PendingStableContextLog = true;
                PendingStableSpawnLogs.Add(key);
                _suppressedContextEvents++;
            }
        }

        public static void ReportDeath(object? entity, string source, string category)
        {
            if (!IsEnabled()) return;
            if (entity == null) return;

            category = CleanCategory(category, entity);
            if (!ShouldTrackCategory(category)) return;

            _deathEvents++;

            var snapshot = GetOrCreateSnapshot(entity, source, category);
            if (snapshot.IsDead) return;

            snapshot.IsDead = true;
            snapshot.LastSeenAt = Time.realtimeSinceStartup;
            RefreshSceneContext(snapshot);

            if (!CanLogWithCurrentContext(out _))
            {
                _suppressedContextEvents++;
                return;
            }

            float lifetime = Math.Max(0f, snapshot.LastSeenAt - snapshot.FirstSeenAt);
            if (Plugin.Cfg.LogGameplayEntityDeath.Value)
                Plugin.Log.Info(snapshot.FormatDeathLine(Clean(source), lifetime));

            TryReportHostEnemyDeathMirror(snapshot, Clean(source));
            TryReportClientEnemyDeathClaim(snapshot, Clean(source));
        }

        public static void ReportDamage(object? entity, string source, string category, float damage, object? damageType)
        {
            if (!IsEnabled()) return;
            if (entity == null) return;

            category = CleanCategory(category, entity);
            if (!ShouldTrackCategory(category)) return;

            _damageEvents++;

            var snapshot = GetOrCreateSnapshot(entity, source, category);
            snapshot.DamageCount++;
            snapshot.LastSeenAt = Time.realtimeSinceStartup;
            RefreshSceneContext(snapshot);

            if (!Plugin.Cfg.LogGameplayEntityDamage.Value) return;
            if (!CanLogWithCurrentContext(out _))
            {
                _suppressedContextEvents++;
                return;
            }

            Plugin.Log.Info(snapshot.FormatDamageLine(Clean(source), damage, damageType));
        }

        public static bool IsEnemyStateMirrorControlling(object? npcOrAgentOrMovement, out NetGameplayEntitySnapshot? snapshot, out string detail)
        {
            snapshot = null;
            detail = "";

            if (!IsEnabled())
            {
                detail = "gameplay entity probe disabled";
                return false;
            }

            if (NetConfig.GetMode() != NetMode.Client)
            {
                detail = "local peer is not Client";
                return false;
            }

            if (!Plugin.Cfg.EnableHostEnemyStateSnapshotMirror.Value)
            {
                detail = "EnableHostEnemyStateSnapshotMirror=false";
                return false;
            }

            if (!Plugin.Cfg.ApplyReceivedEnemyStateSnapshots.Value)
            {
                detail = "ApplyReceivedEnemyStateSnapshots=false";
                return false;
            }

            if (PendingEnemyStateTargets.Count == 0)
            {
                detail = "no active Host enemy state targets";
                return false;
            }

            if (!CanLogWithCurrentContext(out var contextReason))
            {
                detail = "unstable local scene/seed context: " + contextReason;
                return false;
            }

            object? entity = ResolveMirroredEnemyObject(npcOrAgentOrMovement, out var resolveDetail);
            if (entity == null)
            {
                detail = "could not resolve mirrored NPC from " + resolveDetail;
                return false;
            }

            snapshot = FindSnapshotForRuntimeObject(entity);
            if (snapshot == null)
            {
                detail = "no tracked gameplay snapshot for resolved object: " + resolveDetail;
                return false;
            }

            if (snapshot.IsDead)
            {
                detail = $"tracked NPC already dead idx={snapshot.SpawnIndex}";
                return false;
            }

            if (!string.Equals(snapshot.Category, "Npc", StringComparison.OrdinalIgnoreCase))
            {
                detail = $"tracked category is not Npc: {snapshot.Category}";
                return false;
            }

            string targetKey = GetSnapshotTargetKey(snapshot);
            if (string.IsNullOrWhiteSpace(targetKey) || !PendingEnemyStateTargets.TryGetValue(targetKey, out var target))
            {
                detail = $"no active Host state target for idx={snapshot.SpawnIndex}";
                return false;
            }

            float age = Time.realtimeSinceStartup - target.LastUpdatedAt;
            if (!target.HasPosition || age > 2f)
            {
                detail = $"Host state target stale idx={snapshot.SpawnIndex} age={age:F2}s";
                return false;
            }

            detail = $"idx={snapshot.SpawnIndex} actor={snapshot.ActorName} targetSeq={target.Sequence} targetAge={age:F2}s resolved={resolveDetail}";
            return true;
        }

        private static NetGameplayEntitySnapshot? FindSnapshotForRuntimeObject(object entity)
        {
            foreach (var snapshot in EntitiesByLocalId.Values)
            {
                if (snapshot.TryGetRuntimeObject(out var runtimeObject) && ReferenceEquals(runtimeObject, entity))
                    return snapshot;
            }

            var id = NetGameplayEntityId.FromObject(entity);
            string key = string.IsNullOrWhiteSpace(id.LocalInstanceId) || id.LocalInstanceId == "null"
                ? id.CandidateKey
                : id.LocalInstanceId;

            if (!string.IsNullOrWhiteSpace(key) && EntitiesByLocalId.TryGetValue(key, out var byKey))
                return byKey;

            if (!string.IsNullOrWhiteSpace(id.CandidateKey))
            {
                foreach (var snapshot in EntitiesByLocalId.Values)
                {
                    if (string.Equals(snapshot.EntityId.CandidateKey, id.CandidateKey, StringComparison.Ordinal))
                        return snapshot;
                }
            }

            return null;
        }

        private static object? ResolveMirroredEnemyObject(object? value, out string detail)
        {
            detail = value == null ? "null" : value.GetType().FullName ?? value.GetType().Name;
            if (value == null) return null;

            if (LooksLikeTrackedNpc(value)) return value;

            object? owner = TryGetMemberValue(value, "Owner")
                ?? TryGetMemberValue(value, "owner")
                ?? TryGetMemberValue(value, "Unit")
                ?? TryGetMemberValue(value, "unit")
                ?? TryGetMemberValue(value, "Npc")
                ?? TryGetMemberValue(value, "npc");
            if (owner != null && LooksLikeTrackedNpc(owner))
            {
                detail += " -> Owner";
                return owner;
            }

            object? aiAgent = TryGetMemberValue(value, "aiAgent")
                ?? TryGetMemberValue(value, "AiAgent")
                ?? TryGetMemberValue(value, "AI")
                ?? TryGetMemberValue(value, "ai");
            if (aiAgent != null && !ReferenceEquals(aiAgent, value))
            {
                object? aiOwner = TryGetMemberValue(aiAgent, "Owner")
                    ?? TryGetMemberValue(aiAgent, "owner");
                if (aiOwner != null && LooksLikeTrackedNpc(aiOwner))
                {
                    detail += " -> aiAgent.Owner";
                    return aiOwner;
                }
            }

            return null;
        }

        private static bool LooksLikeTrackedNpc(object value)
        {
            try
            {
                Type type = value.GetType();
                if (type.Name == "Npc") return true;
                for (Type? current = type.BaseType; current != null; current = current.BaseType)
                {
                    if (current.Name == "Npc") return true;
                }
            }
            catch { }

            return false;
        }

        private static bool IsDeathPositionWithinTolerance(Vector3 localPosition, Vector3 hostPosition, float tolerance, out string detail)
        {
            if (!IsFinite(localPosition) || !IsFinite(hostPosition))
            {
                detail = "non-finite position";
                return false;
            }

            float fullDistance = Vector3.Distance(localPosition, hostPosition);
            if (!Plugin.Cfg.EnemyDeathMirrorUseHorizontalPositionTolerance.Value)
            {
                detail = $"distance={fullDistance:F2}m";
                return fullDistance <= tolerance;
            }

            float dx = localPosition.x - hostPosition.x;
            float dz = localPosition.z - hostPosition.z;
            float horizontalDistance = Mathf.Sqrt(dx * dx + dz * dz);
            float verticalDistance = Mathf.Abs(localPosition.y - hostPosition.y);
            detail = $"horizontal={horizontalDistance:F2}m vertical={verticalDistance:F2}m full={fullDistance:F2}m";
            return horizontalDistance <= tolerance;
        }

        public static bool TryFindLocalMatch(NetGameplayDeathEvent deathEvent, out NetGameplayEntitySnapshot? snapshot, out string detail)
        {
            snapshot = null;
            detail = "";

            if (!IsEnabled())
            {
                detail = "gameplay entity probe disabled";
                return false;
            }

            if (!NetRunStateBridge.TryGetLocalRunState(out var localState))
            {
                detail = "local run state unavailable";
                return false;
            }

            if (!deathEvent.MatchesScene(localState))
            {
                detail = $"scene/seed mismatch local={localState.ToCompactString()} remote={deathEvent.SceneKey} seed={deathEvent.SeedText}";
                return false;
            }

            // O3-C: Roster-binding-first death matching.
            // Host spawnIndex translates to a bound client localKey via the roster; apply death
            // to that entity even if it has drifted in position since the roster was captured.
            if (ClientHostToLocalKeyByHostSpawnIndex.TryGetValue(deathEvent.SpawnIndex, out var boundDeathKey)
                && EntitiesByLocalId.TryGetValue(boundDeathKey, out var boundDeathSnapshot)
                && !boundDeathSnapshot.IsDead)
            {
                if (!string.IsNullOrWhiteSpace(deathEvent.UnitIdentifier)
                    && !string.IsNullOrWhiteSpace(boundDeathSnapshot.EntityId.UnitIdentifier)
                    && !string.Equals(deathEvent.UnitIdentifier, boundDeathSnapshot.EntityId.UnitIdentifier, StringComparison.Ordinal))
                {
                    _deathMirrorRejectedTypeMismatch++;
                    _entityTypeMismatchRejected++;
                    detail = $"roster-bound but unitId mismatch host={deathEvent.UnitIdentifier} client={boundDeathSnapshot.EntityId.UnitIdentifier}";
                    Plugin.Log.Info($"[DeathMatch] Reject unitId mismatch via binding hostIdx={deathEvent.SpawnIndex}");
                    return false;
                }
                snapshot = boundDeathSnapshot;
                if (deathEvent.HasPosition && boundDeathSnapshot.HasPosition)
                {
                    float bindTol = Plugin.Cfg.EnemyDeathMirrorPositionTolerance.Value;
                    if (bindTol < 0f) bindTol = 0f;
                    float drift = Vector3.Distance(deathEvent.Position, boundDeathSnapshot.Position);
                    if (drift > bindTol)
                    {
                        _deathBoundDriftWarning++;
                        Plugin.Log.Info($"[DeathMatch] Bound drift={drift:F1}m > tol={bindTol:F1}m; applying anyway hostIdx={deathEvent.SpawnIndex} unit={deathEvent.UnitIdentifier}");
                        if (drift > bindTol * 2f)
                            _deathAppliedBoundDespiteDrift++;
                    }
                }
                _deathAppliedByBinding++;
                detail = "roster-bound death match";
                return true;
            }

            // O3-C: Roster is active with at least one binding — entity not in roster means host-only.
            // Phase 5.1 P0: before rejecting, try a one-shot late-bind by UnitIdentifier proximity.
            if (ClientHostToLocalKeyByHostSpawnIndex.Count > 0)
            {
                _hostDeathLateBindAttempts++;
                if (TryLateBindForDeathEvent(deathEvent, out var lateBoundSnap) && lateBoundSnap != null)
                {
                    snapshot = lateBoundSnap;
                    _hostDeathLateBindSuccess++;
                    detail = $"late-bind death match hostIdx={deathEvent.SpawnIndex} unit={deathEvent.UnitIdentifier}";
                    return true;
                }
                _deathRejectedUnboundNetEntity++;
                _hostDeathRejectedNoBinding++;

                // Phase 5.2: tombstone check for better diagnostics.
                float nowTs = Time.realtimeSinceStartup;
                if (_bindingTombstones.TryGetValue(deathEvent.SpawnIndex, out var ts)
                    && nowTs - ts.ReleasedAt <= TombstoneMaxAge)
                {
                    _hostDeathTombstoneHit++;
                    detail = $"roster-active no-binding, tombstone: puppet released {nowTs - ts.ReleasedAt:F1}s ago reason={ts.ReleaseReason} key={ts.LocalKey} unit={ts.UnitIdentifier}";
                }
                else
                {
                    _hostDeathNeverBound++;
                    detail = $"roster active (rev={_clientRosterRevision} bindings={ClientHostToLocalKeyByHostSpawnIndex.Count}) hostIdx={deathEvent.SpawnIndex} never bound, late-bind failed";
                }
                return false;
            }

            float tolerance = Plugin.Cfg.EnemyDeathMirrorPositionTolerance.Value;
            if (tolerance < 0f) tolerance = 0f;

            var candidates = EntitiesByLocalId.Values
                .Where(s => !s.IsDead && string.Equals(s.Category, deathEvent.Category, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var bySpawnIndex = candidates.FirstOrDefault(s => s.SpawnIndex == deathEvent.SpawnIndex);
            if (bySpawnIndex != null)
            {
                // O3-B: Reject if both sides have a UnitIdentifier and they differ (e.g. GhostApparition vs GhostWraith)
                if (!string.IsNullOrWhiteSpace(deathEvent.UnitIdentifier)
                    && !string.IsNullOrWhiteSpace(bySpawnIndex.EntityId.UnitIdentifier)
                    && !string.Equals(deathEvent.UnitIdentifier, bySpawnIndex.EntityId.UnitIdentifier, StringComparison.Ordinal))
                {
                    _deathMirrorRejectedTypeMismatch++;
                    _entityTypeMismatchRejected++;
                    detail = $"spawnIndex matched but unitId mismatch host={deathEvent.UnitIdentifier} client={bySpawnIndex.EntityId.UnitIdentifier}";
                    Plugin.Log.Info($"[EntityBind] Reject type mismatch hostUnit={deathEvent.UnitIdentifier} clientUnit={bySpawnIndex.EntityId.UnitIdentifier} hostIdx={deathEvent.SpawnIndex} clientIdx={bySpawnIndex.SpawnIndex}");
                    return false;
                }

                snapshot = bySpawnIndex;
                if (deathEvent.HasPosition && bySpawnIndex.HasPosition)
                {
                    if (IsDeathPositionWithinTolerance(bySpawnIndex.Position, deathEvent.Position, tolerance, out var distanceDetail))
                    {
                        detail = $"spawnIndex+position {distanceDetail}";
                        return true;
                    }

                    detail = $"spawnIndex matched but position {distanceDetail} > tolerance={tolerance:F2}m";
                    return false;
                }

                detail = "spawnIndex matched; position unavailable on one side";
                return true;
            }

            if (!string.IsNullOrWhiteSpace(deathEvent.UnitGlobalId))
            {
                var globalMatches = candidates
                    .Where(s => string.Equals(s.EntityId.UnitGlobalId, deathEvent.UnitGlobalId, StringComparison.Ordinal))
                    .ToList();
                if (globalMatches.Count == 1)
                {
                    snapshot = globalMatches[0];
                    detail = "unique UnitGlobalId fallback match";
                    return true;
                }
            }

            if (!string.IsNullOrWhiteSpace(deathEvent.CandidateKey))
            {
                var keyMatches = candidates
                    .Where(s => string.Equals(s.EntityId.CandidateKey, deathEvent.CandidateKey, StringComparison.Ordinal))
                    .ToList();
                if (keyMatches.Count == 1)
                {
                    snapshot = keyMatches[0];
                    detail = "unique candidateKey fallback match";
                    return true;
                }
            }

            detail = $"no local alive match candidates={candidates.Count}";
            return false;
        }

        public static bool TryApplyHostDeathToLocalMatch(NetGameplayDeathEvent deathEvent, NetGameplayEntitySnapshot? snapshot, out string detail)
        {
            if (!Plugin.Cfg.ApplyReceivedEnemyDeathEvents.Value)
            {
                detail = "ApplyReceivedEnemyDeathEvents=false";
                return false;
            }

            return TryApplyMatchedNpcDeath(deathEvent, snapshot, "HostEnemyDeathEvent", out detail);
        }

        public static bool TryApplyClientDeathClaimToHostMatch(NetGameplayDeathEvent deathEvent, NetGameplayEntitySnapshot? snapshot, out string detail)
        {
            if (!Plugin.Cfg.ApplyReceivedClientEnemyDeathClaimsOnHost.Value)
            {
                detail = "ApplyReceivedClientEnemyDeathClaimsOnHost=false";
                return false;
            }

            // Phase 4.4.0-O3: Reject death claims that matched a Trader or interactive NPC on this Host
            if (snapshot != null && IsNonCombatForSync(snapshot))
            {
                detail = $"[DeathClaim] Rejected category={SyncCatName(snapshot.SyncCategory)} actor={snapshot.ActorName}";
                _deathClaimRejectedNonCombat++;
                Plugin.Log.Info(detail);
                return false;
            }

            return TryApplyMatchedNpcDeath(deathEvent, snapshot, "ClientEnemyDeathClaim", out detail);
        }

        private static bool TryApplyMatchedNpcDeath(NetGameplayDeathEvent deathEvent, NetGameplayEntitySnapshot? snapshot, string reason, out string detail)
        {
            detail = "";

            if (!IsEnabled())
            {
                detail = "gameplay entity probe disabled";
                return false;
            }

            if (snapshot == null)
            {
                detail = "matched snapshot is null";
                return false;
            }

            if (snapshot.IsDead)
            {
                detail = $"local entity already dead idx={snapshot.SpawnIndex}";
                return false;
            }

            // Phase 5.3-C P0-1: don't re-run Die() on a corpse already latched terminal dead.
            if (deathEvent.SpawnIndex >= 0 && IsClientTerminalDead(deathEvent.SpawnIndex))
            {
                _terminalDeadDeathReapplySkipped++;
                detail = $"terminal-dead latch already set hostIdx={deathEvent.SpawnIndex}";
                return false;
            }

            if (!string.Equals(snapshot.Category, "Npc", StringComparison.OrdinalIgnoreCase))
            {
                detail = $"category '{snapshot.Category}' is not eligible";
                return false;
            }

            if (!NetRunStateBridge.TryGetLocalRunState(out var localState) || !deathEvent.MatchesScene(localState))
            {
                detail = "local scene/seed no longer matches death event";
                return false;
            }

            if (!snapshot.TryGetRuntimeObject(out var runtimeObject) || runtimeObject == null)
            {
                detail = $"runtime object is no longer available idx={snapshot.SpawnIndex}";
                return false;
            }

            if (runtimeObject is UnityEngine.Object unityObject && unityObject == null)
            {
                detail = $"runtime Unity object was destroyed idx={snapshot.SpawnIndex}";
                return false;
            }

            // Phase 5.1 P0: determine whether this death was matched via roster binding.
            // Roster-bound deaths bypass the position-drift rejection — the binding is the
            // primary trust anchor and drift is expected due to puppet interpolation lag.
            bool isRosterBound = Plugin.Cfg.AllowRosterBoundDeathDespitePositionDrift.Value
                && deathEvent.SpawnIndex >= 0
                && ClientHostToLocalKeyByHostSpawnIndex.TryGetValue(deathEvent.SpawnIndex, out var rosterBoundKey)
                && EntitiesByLocalId.TryGetValue(rosterBoundKey, out var rosterBoundSnap)
                && ReferenceEquals(rosterBoundSnap, snapshot);

            if (TryGetPosition(runtimeObject, out var currentPosition))
            {
                snapshot.HasPosition = true;
                snapshot.Position = currentPosition;

                if (deathEvent.HasPosition)
                {
                    if (isRosterBound)
                    {
                        // Log drift but do not reject — binding is authoritative.
                        float drift = Vector3.Distance(currentPosition, deathEvent.Position);
                        if (drift > 2f)
                            Plugin.Log.Info($"[DeathApply] Roster-bound death drift={drift:F1}m hostIdx={deathEvent.SpawnIndex} — applying anyway");
                    }
                    else
                    {
                        float tolerance = Plugin.Cfg.EnemyDeathMirrorPositionTolerance.Value;
                        if (tolerance < 0f) tolerance = 0f;

                        if (!IsDeathPositionWithinTolerance(currentPosition, deathEvent.Position, tolerance, out var distanceDetail))
                        {
                            detail = $"position drift before apply {distanceDetail} > tolerance={tolerance:F2}m";
                            return false;
                        }
                    }
                }
            }

            // Phase 5.1 P0: snap puppet to host death position before invoking Die() so the
            // death animation plays at the correct world location instead of the lagged proxy position.
            if (isRosterBound && Plugin.Cfg.HostDeathSnapBeforeApply.Value && deathEvent.HasPosition)
            {
                TrySnapEntityToPosition(runtimeObject, deathEvent.Position);
                _hostDeathAppliedAfterSnap++;
            }

            if (isRosterBound) _hostDeathAppliedByRosterBinding++;

            var die = FindNoArgInstanceMethod(runtimeObject.GetType(), "Die");
            if (die == null)
            {
                detail = $"no no-arg Die method on {runtimeObject.GetType().FullName}";
                return false;
            }

            try
            {
                _hostDeathApplyDepth++;
                bool wasPendingDead = deathEvent.SpawnIndex >= 0 && IsClientPendingDead(deathEvent.SpawnIndex);
                die.Invoke(runtimeObject, null);
                snapshot.IsDead = true;
                snapshot.LastSeenAt = Time.realtimeSinceStartup;
                // Phase 5.3-D P0-2/P0-3: death VISUAL has now run (Die set Animator "Dead"), so latch
                // terminal dead. This clears any PendingDead and blocks later non-death overrides.
                if (deathEvent.SpawnIndex >= 0)
                {
                    if (wasPendingDead) _pendingDeadHostDeathApplied++;
                    _terminalDeadMarkedAfterDie++;
                    MarkClientTerminalDead(deathEvent.SpawnIndex, "Npc.Die applied via " + reason);
                }
                // Phase 5.7-SC3: the host enemy is gone — release its client puppet + drop the binding. Otherwise the
                // puppet lingers in ActiveEnemyPuppets, ReleaseStaleEnemyPuppets keeps suppressing its release (because it
                // is still "host-bound"), and a host-despawned enemy stands in place forever as a stale zombie (LogOutput111
                // idx=1 Tracker: host Npc.Die damageCount=0 → death applied → puppet still stale-suppressed 20s+).
                if (Plugin.Cfg.ReleasePuppetOnHostDeath.Value)
                    ReleaseClientEnemyPuppetOnHostDeath(snapshot, deathEvent.SpawnIndex, reason);
                detail = $"invoked {die.DeclaringType?.Name ?? runtimeObject.GetType().Name}.Die() idx={snapshot.SpawnIndex} reason={reason} rosterBound={isRosterBound} wasPendingDead={wasPendingDead}";
                return true;
            }
            catch (TargetInvocationException ex)
            {
                detail = $"Die() threw {ex.InnerException?.GetType().Name ?? ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}";
                return false;
            }
            catch (Exception ex)
            {
                detail = $"Die() invoke failed: {ex.Message}";
                return false;
            }
            finally
            {
                if (_hostDeathApplyDepth > 0) _hostDeathApplyDepth--;
            }
        }

        private static void TryReportHostEnemyDeathMirror(NetGameplayEntitySnapshot snapshot, string source)
        {
            if (!Plugin.Cfg.EnableHostEnemyDeathEventMirror.Value) return;
            if (!string.Equals(snapshot.Category, "Npc", StringComparison.OrdinalIgnoreCase)) return;
            // Phase 4.4.0-O3: Traders and interactive NPCs must never enter enemy death mirror
            if (IsNonCombatForSync(snapshot)) { _nonCombatExcludedFromEnemySync++; return; }
            if (!NetRunStateBridge.TryGetLocalRunState(out var state)) return;
            if (!state.HasLevel) return;
            if (Plugin.Cfg.EnableLevelSeedAuthority.Value && !state.HasLevelSeed) return;

            _deathMirrorSequence++;
            var evt = new NetGameplayDeathEvent
            {
                EventId = $"{state.PeerId}:{state.Revision}:{snapshot.SpawnIndex}:{_deathMirrorSequence}",
                SourcePeerId = string.IsNullOrWhiteSpace(state.PeerId) ? "host" : state.PeerId,
                ChapterName = state.ChapterName,
                LevelIndex = state.LevelIndex,
                HasLevelSeed = state.HasLevelSeed,
                LevelSeed = state.LevelSeed,
                SourceRevision = state.Revision,
                Sequence = _deathMirrorSequence,
                SpawnIndex = snapshot.SpawnIndex,
                CandidateKey = snapshot.EntityId.CandidateKey,
                LocalInstanceId = snapshot.EntityId.LocalInstanceId,
                UnityInstanceId = snapshot.EntityId.UnityInstanceId,
                TypeName = snapshot.EntityId.TypeName,
                UnitIdentifier = snapshot.EntityId.UnitIdentifier,
                UnitGlobalId = snapshot.EntityId.UnitGlobalId,
                Category = snapshot.Category,
                ActorName = snapshot.ActorName,
                HasPosition = snapshot.HasPosition,
                Position = snapshot.Position,
                DamageCount = snapshot.DamageCount,
                Source = source,
                SentAt = Time.realtimeSinceStartup,
            };

            NetGameplaySyncBridge.ReportLocalEnemyDeath(evt);
        }

        private static void TryReportClientEnemyDeathClaim(NetGameplayEntitySnapshot snapshot, string source)
        {
            if (!Plugin.Cfg.EnableClientEnemyDeathClaim.Value) return;
            if (_hostDeathApplyDepth > 0) return;
            if (NetConfig.GetMode() != NetMode.Client) return;
            if (!string.Equals(snapshot.Category, "Npc", StringComparison.OrdinalIgnoreCase)) return;
            // Phase 5.1 P0: suppress client death claims when host damage sync is authoritative.
            // Host owns all combat results; client claims would cause divergent kills on Host.
            if (Plugin.Cfg.EnableHostEnemyDamageEventSync.Value
                && Plugin.Cfg.DisableClientEnemyDeathClaimWhenHostDamageSyncEnabled.Value)
            {
                _clientDeathClaimsSuppressedByHostAuthority++;
                return;
            }
            // Phase 4.4.0-O3: Traders and interactive NPCs must never send a death claim
            if (IsNonCombatForSync(snapshot)) { _deathClaimRejectedNonCombat++; return; }
            // O3-C: Quarantined client-only CombatEnemies have no Host binding — suppress death claim.
            {
                string qKey = GetSnapshotTargetKey(snapshot);
                if (!string.IsNullOrWhiteSpace(qKey) && ClientQuarantinedEntities.Contains(qKey))
                { _quarantinedCombatSuppressed++; return; }
            }
            if (!NetRunStateBridge.TryGetLocalRunState(out var state)) return;
            if (!state.HasLevel) return;
            if (Plugin.Cfg.EnableLevelSeedAuthority.Value && !state.HasLevelSeed) return;

            _deathMirrorSequence++;
            var evt = new NetGameplayDeathEvent
            {
                EventId = $"{state.PeerId}:{state.Revision}:{snapshot.SpawnIndex}:claim:{_deathMirrorSequence}",
                SourcePeerId = string.IsNullOrWhiteSpace(state.PeerId) ? "client" : state.PeerId,
                ChapterName = state.ChapterName,
                LevelIndex = state.LevelIndex,
                HasLevelSeed = state.HasLevelSeed,
                LevelSeed = state.LevelSeed,
                SourceRevision = state.Revision,
                Sequence = _deathMirrorSequence,
                SpawnIndex = snapshot.SpawnIndex,
                CandidateKey = snapshot.EntityId.CandidateKey,
                LocalInstanceId = snapshot.EntityId.LocalInstanceId,
                UnityInstanceId = snapshot.EntityId.UnityInstanceId,
                TypeName = snapshot.EntityId.TypeName,
                UnitIdentifier = snapshot.EntityId.UnitIdentifier,
                UnitGlobalId = snapshot.EntityId.UnitGlobalId,
                Category = snapshot.Category,
                ActorName = snapshot.ActorName,
                HasPosition = snapshot.HasPosition,
                Position = snapshot.Position,
                DamageCount = snapshot.DamageCount,
                Source = source + "/ClientDeathClaim",
                SentAt = Time.realtimeSinceStartup,
            };

            NetGameplaySyncBridge.ReportClientEnemyDeathClaim(evt);
        }

        private static NetGameplayEntitySnapshot GetOrCreateSnapshot(object entity, string source, string category)
        {
            var id = NetGameplayEntityId.FromObject(entity);
            string key = string.IsNullOrWhiteSpace(id.LocalInstanceId) || id.LocalInstanceId == "null"
                ? id.CandidateKey
                : id.LocalInstanceId;

            if (EntitiesByLocalId.TryGetValue(key, out var snapshot))
            {
                snapshot.SetRuntimeObject(entity);
                return snapshot;
            }

            snapshot = BuildSnapshot(entity, id, source, category);
            snapshot.SpawnIndex = ++_nextSpawnIndex;
            EntitiesByLocalId[key] = snapshot;
            _newSpawns++;
            return snapshot;
        }

        private static NetGameplayEntitySnapshot BuildSnapshot(object entity, NetGameplayEntityId id, string source, string category)
        {
            var snapshot = new NetGameplayEntitySnapshot
            {
                EntityId = id,
                Category = Clean(category),
                ActorName = GetActorName(entity),
                Source = Clean(source),
                FirstSeenAt = Time.realtimeSinceStartup,
                LastSeenAt = Time.realtimeSinceStartup,
            };
            snapshot.SetRuntimeObject(entity);
            // Phase 4.4.0-O3: classify entity so downstream gates can protect traders/NPCs
            snapshot.SyncCategory = ClassifyEntitySyncCategory(entity, snapshot.ActorName);

            if (TryGetPosition(entity, out var position))
            {
                snapshot.HasPosition = true;
                snapshot.Position = position;
                // Capture the spawn position once — used by the stable generation hash.
                snapshot.HasInitialPosition = true;
                snapshot.InitialPosition = position;
            }

            RefreshSceneContext(snapshot);
            return snapshot;
        }

        private static void RefreshSceneContext(NetGameplayEntitySnapshot snapshot)
        {
            if (NetRunStateBridge.TryGetLocalRunState(out var state) && state.HasLevel)
            {
                snapshot.SceneKey = state.SceneKey();
                snapshot.HasLevelSeed = state.HasLevelSeed;
                snapshot.LevelSeed = state.LevelSeed;
                snapshot.GameState = string.IsNullOrWhiteSpace(state.GameState) ? "<unknown>" : state.GameState;
            }
        }

        private static void TryFlushPendingStableSpawnLogs()
        {
            if (PendingStableSpawnLogs.Count == 0) return;
            if (!Plugin.Cfg.LogGameplayEntitySpawn.Value) return;
            if (!CanLogWithCurrentContext(out _)) return;

            foreach (var key in PendingStableSpawnLogs.ToArray())
            {
                if (!EntitiesByLocalId.TryGetValue(key, out var snapshot))
                {
                    PendingStableSpawnLogs.Remove(key);
                    continue;
                }

                if (snapshot.IsDead)
                {
                    PendingStableSpawnLogs.Remove(key);
                    continue;
                }

                RefreshSceneContext(snapshot);
                snapshot.PendingStableContextLog = false;
                PendingStableSpawnLogs.Remove(key);
                Plugin.Log.Info(snapshot.FormatSpawnLine("[late-context]"));
            }
        }

        private static void MaybeLogSummary()
        {
            float interval = Plugin.Cfg.GameplayEntityProbeSummaryIntervalSeconds.Value;
            if (interval < 1f) interval = 1f;

            float now = Time.realtimeSinceStartup;
            if (now - _lastSummaryAt < interval) return;
            _lastSummaryAt = now;

            int alive = EntitiesByLocalId.Values.Count(s => !s.IsDead);
            int dead = EntitiesByLocalId.Values.Count(s => s.IsDead);
            int npc = EntitiesByLocalId.Values.Count(s => s.Category == "Npc" && !s.IsDead);
            string context = NetRunStateBridge.TryGetLocalRunState(out var state)
                ? state.ToCompactString()
                : "local=<no-net-run-state>";

            if (_spawnEvents == 0 && _damageEvents == 0 && _deathEvents == 0 && alive == 0 && dead == 0)
                return;

            Plugin.Log.Info($"[GameplayProbe] Summary traderExcluded={_traderExcludedFromEnemySync} nonCombatExcluded={_nonCombatExcludedFromEnemySync} deathClaimRejectedNonCombat={_deathClaimRejectedNonCombat} combatProbeRejectedNonCombat={_combatProbeRejectedNonCombat}");
            Plugin.Log.Info($"[GameplayProbe] Summary rootReplayAttempts={_clientRootReplayAttempts} rootReplays={_clientCombatRootReplays} rootReplaySkippedDup={_clientRootReplaySkippedDuplicate} rootReplayUnsupported={_clientRootReplayUnsupported} rootReplayFailed={_clientRootReplayFailed} childAfterRoot={_clientAuthorizedChildAfterRoot} childBlockedBeforeRoot={_clientChildBlockedBeforeRootReplay}");
            Plugin.Log.Info($"[GameplayProbe] Summary typeMismatch={_entityTypeMismatchRejected} deathTypeMismatch={_deathMirrorRejectedTypeMismatch} stateTypeMismatch={_stateApplyRejectedTypeMismatch}");
            Plugin.Log.Info($"[GameplayProbe] Summary rosterSent={_hostRosterRecordsSent} rosterReceived={_clientRosterRecordsReceived} rosterBound={_clientRosterBound} rosterOneToOne={_rosterOneToOneBound} rosterHostOnly={_clientRosterHostOnlyMissing} rosterClientOnly={_clientRosterClientOnlyQuarantined} quarantined={_clientOnlyCombatQuarantined} quarantineSuppressed={_quarantinedCombatSuppressed} rosterTypeMismatch={_clientRosterTypeMismatch} rosterFingerprintMismatch={_clientRosterFingerprintMismatch} rosterBindings={ClientHostToLocalKeyByHostSpawnIndex.Count} deathBound={_deathAppliedByBinding} deathUnbound={_deathRejectedUnboundNetEntity} deathBoundDrift={_deathBoundDriftWarning} deathBoundBigDrift={_deathAppliedBoundDespiteDrift}");
            Plugin.Log.Info($"[GameplayProbe] Summary alive={alive} npcAlive={npc} dead={dead} totalSeen={EntitiesByLocalId.Count} newSpawns={_newSpawns} spawnEvents={_spawnEvents} duplicateSpawnEvents={_duplicateSpawnEvents} damageEvents={_damageEvents} deathEvents={_deathEvents} enemyStateTargets={PendingEnemyStateTargets.Count} enemyStateQueued={_enemyStateTargetsQueued} enemyStateApplied={_enemyStateTargetsApplied} enemyStateSnapped={_enemyStateTargetsSnapped} enemyPuppets={ActiveEnemyPuppets.Count} enemyPuppetsActivated={_clientEnemyPuppetsActivated} enemyPuppetsReleased={_clientEnemyPuppetsReleased} puppetTargetClears={_clientEnemyPuppetTargetClears} puppetTargetBlocks={_clientEnemyPuppetTargetBlocks} puppetCombatBlocks={_clientEnemyPuppetCombatBlocks} enemyCombatProbeEvents={_enemyCombatProbeEvents} hostCombatActions={_hostEnemyCombatActionMarks} clientCombatTriggers={_clientCombatAnimatorTriggerApplies} clientCombatStates={_clientCombatAnimatorStateApplies} clientCombatVisualReplays={_clientCombatVisualActionReplays} clientCombatFallbacks={_clientCombatAnimatorFallbacks} genericCombatStates={_clientGenericCombatAnimatorStateApplies} genericCombatSkipped={_genericCombatStateSkippedDuringAuthorizedIntent} visualProjectiles={_clientVisualProjectileMirrors} activeVisualProjectiles={ClientVisualProjectiles.Count} projectileProbeEvents={_projectileProbeEvents} hostDamageChecks={_hostEnemyDamageChecks} hostDamageHits={_hostEnemyDamageHits} hostAiIntents={_hostEnemyAiIntentMarks} clientAiIntents={_clientEnemyAiIntentApplies} clientAiCorrections={_clientEnemyAiIntentCorrections} driftSkipLoco={_driftSkippedIntentLocomotion} driftSkipCombat={_driftSkippedAuthorizedCombat} hardDrift={_hardDriftCorrections} softDrift={_softDriftCorrections} clientIntentWindows={_clientIntentWindows} activeIntentWindows={_clientAuthorizedIntentByNpcId.Count} clientRootReplays={_clientCombatRootReplays} clientRootSkipped={_clientCombatRootReplaySkippedDuplicate} clientAuthorizedAttacks={_clientAuthorizedAttackPasses} clientAuthorizedChild={_clientAuthorizedChildPasses} clientBlockedSpontaneous={_clientBlockedSpontaneousCombatCalls} clientUnauthorizedBlocks={_clientUnauthorizedAttackBlocks} clientAuthorizedMelee={_clientAuthorizedMeleeEvents} clientSuppressedDamage={_clientSuppressedNativeEnemyDamage} pendingContext={PendingStableSpawnLogs.Count} suppressedContextEvents={_suppressedContextEvents} context={context}");
            Plugin.Log.Info($"[GameplayProbe] Phase5.0 hostAttackPhaseEventsSent={_hostAttackPhaseEventsSent} clientAttackPhaseEventsReceived={_clientAttackPhaseEventsReceived} clientAttackPhaseAnimatorApplies={_clientAttackPhaseAnimatorApplies} clientPuppetDamageSuppressed={_clientPuppetDamageSuppressed} interestManagementFarSkipped={_interestManagementFarSkipped} interestEngagedExempt={_interestEngagedExempt} attackPhaseThrottled={_attackPhaseThrottled} enemyDamageEventThrottled={_enemyDamageEventThrottled}");
            Plugin.Log.Info($"[GameplayProbe] Phase5.2 hostDmgEventsSent={_hostEnemyDamageEventsSent} hostHealthStatesSent={_hostEnemyHealthStatesSent} clientDmgEventsRecv={_clientEnemyDamageEventsReceived} clientHealthStatesRecv={_clientEnemyHealthStatesReceived} clientHealthApplied={_clientEnemyHealthStatesApplied} clientHealthNoBinding={_clientEnemyHealthApplySkippedNoBinding} healthPendingQueued={_clientHealthStatesPendingQueued} healthPendingApplied={_clientHealthStatesPendingApplied} healthPendingExpired={_clientHealthStatesPendingExpired} deathByRosterBind={_hostDeathAppliedByRosterBinding} deathSnapped={_hostDeathAppliedAfterSnap} deathRejectedNoBinding={_hostDeathRejectedNoBinding} deathTombstone={_hostDeathTombstoneHit} deathNeverBound={_hostDeathNeverBound} lateBindAttempts={_hostDeathLateBindAttempts} lateBindSuccess={_hostDeathLateBindSuccess} claimsSuppressed={_clientDeathClaimsSuppressedByHostAuthority}");
            Plugin.Log.Info($"[GameplayProbe] Phase5.2-diag healthFail_disabled={_clientHealthApplyDisabled} healthFail_noHp={_clientHealthNoCurrentHp} healthFail_noEntity={_clientHealthNoEntity} healthFail_noRuntimeObj={_clientHealthNoRuntimeObj} healthFail_unityDestroyed={_clientHealthUnityDestroyed} healthFail_noStats={_clientHealthNoStats} healthFail_noSetStatus={_clientHealthSetStatusMissing} healthFail_enumArgBuild={_clientHealthEnumArgFailed} healthFail_invokeFail={_clientHealthSetStatusFailed} healthFail_write={_clientHealthWriteFailed} healthFail_readBackUnchanged={_clientHealthWriteReadBackUnchanged} puppetSuppressedAwaitingHost={_puppetReleaseSuppressedAwaitingHost} puppetStaleSuppressed={_puppetStaleReleaseSuppressed}");
            Plugin.Log.Info($"[GameplayProbe] Phase5.3-B clientHitSent={_clientHitRequestsSent} clientHitSkipNoPuppet={_clientHitRequestsSkippedNoPuppet} clientHitSkipNoBinding={_clientHitRequestsSkippedNoBinding} puppetNonPlayerDmgIgnored={_clientPuppetNonPlayerDamageIgnored} clientHitSkipPendingDead={_clientHitSkipPendingDead} clientHitSkipTerminalDead={_clientHitSkipTerminalDead} clientHitPredicted={_clientLocalHitPredicted} clientHitConfirmed={_clientLocalHitConfirmed} hostHitRecv={_hostHitRequestsRecv} hostHitRejectScene={_hostHitRequestsRejectedScene} hostHitRejectNoTarget={_hostHitRequestsRejectedNoTarget} hostHitRejectType={_hostHitRequestsRejectedTypeMismatch} hostHitRejectDead={_hostHitRequestsRejectedDead} hostHitRejectRateLimit={_hostHitRequestsRejectedRateLimit} hostHitCoalesced={_hostHitRequestsCoalesced} hostHitAccepted={_hostHitRequestsAccepted} hostHitDmgApplied={_hostHitRequestsDamageApplied} hostHitDmgFailed={_hostHitRequestsDamageFailed} hostHitResultHealthSent={_hostHitResultHealthStateSent}");
            Plugin.Log.Info($"[GameplayProbe] Phase5.3-D-death pendingDeadMarked={_pendingDeadMarked} pendingDeadHostDeathApplied={_pendingDeadHostDeathApplied} pendingDeadAwaitingHostDeath={_clientPendingDeadHostIdx.Count} pdVisualFallbackAttempted={_pendingDeadVisualFallbackAttempted} pdVisualFallbackSucceeded={_pendingDeadVisualFallbackSucceeded} pdVisualFallbackFailed={_pendingDeadVisualFallbackFailed} pdBlockedHitFlash={_pendingDeadBlockedHitFlash} pdBlockedClientHit={_pendingDeadBlockedClientHit} terminalDeadMarkedAfterDie={_terminalDeadMarkedAfterDie} terminalDeadMarkedAfterVisualFallback={_terminalDeadMarkedAfterVisualFallback} terminalDeadMarkedFromHealthOnly={_terminalDeadMarkedFromHealthOnly} activeTerminalDead={_clientTerminalDeadHostIdx.Count} tdBlockedAttackPhase={_terminalDeadBlockedAttackPhase} tdBlockedGenericReplay={_terminalDeadBlockedGenericReplay} tdBlockedMovement={_terminalDeadBlockedMovement} tdBlockedHitReaction={_terminalDeadBlockedHitReaction} tdHealthIgnored={_terminalDeadHealthUpdatesIgnored} tdDeathReapplySkipped={_terminalDeadDeathReapplySkipped}");
            Plugin.Log.Info($"[GameplayProbe] Phase5.3-D-flash dmgVisualPlayed={_damageVisualReactionsPlayed} dmgVisualDoWhiteFlash={_dmgVisualNativeDoWhiteFlash} dmgVisualSetHitEffect={_dmgVisualNativeSetHitEffect} dmgVisualMaterialHitTime={_dmgVisualMaterialHitTime} dmgVisualFallbackColor={_dmgVisualFallbackColor} dmgVisualFailNoNpc={_dmgVisualFailedNoNpc} dmgVisualFailNoMethod={_dmgVisualFailedNoMethod} dmgVisualFailNoMaterial={_dmgVisualFailedNoMaterial} dmgVisualSkipTerminalDead={_damageVisualReactionSkippedTerminalDead} dmgVisualSkipPendingDead={_damageVisualReactionSkippedPendingDead} dmgVisualSkipNoBinding={_damageVisualReactionSkippedNoBinding} dmgVisualSkipDupSeq={_damageVisualReactionSkippedDuplicateSeq} activeFlashes={_pendingHitFlashes.Count}");
            Plugin.Log.Info($"[GameplayProbe] Phase5.3-E-manifest hostManifestBuilt={_hostManifestBuilt} clientManifestBuilt={_clientManifestBuilt} hostManifestSent={_hostManifestSent} clientManifestReceived={_clientManifestReceived} seedMismatch={_manifestSeedMismatch} roomMismatch={_manifestRoomMismatch} unitMismatch={_manifestUnitMismatch} specialMismatch={_manifestSpecialMismatch} modifierMismatch={_manifestHostEnemyModifierMismatch} hostOnlyUnits={_manifestHostOnlyUnits} clientOnlyUnits={_manifestClientOnlyUnits} hostEnemyBoundExisting={_manifestHostEnemyBoundExisting} hostEnemyBindFailedNoCandidate={_manifestHostEnemyBindFailedNoCandidate} hostEnemyBindFailedAmbiguous={_manifestHostEnemyBindFailedAmbiguous}");
            Plugin.Log.Info($"[GameplayProbe] Phase5.3-F-generation-diff generationHashMatch={_generationHashMatch} generationHashMismatch={_generationHashMismatch} runtimeHashMatch={_runtimeHashMatch} runtimeHashMismatch={_runtimeHashMismatch} genHashStableSameRev={_generationHashStableSameRevision} genHashChangedSameRev={_generationHashChangedSameRevision}");
            Plugin.Log.Info($"[GameplayProbe] Phase5.3-F-quarantine clientOnlyQuarantined={_manifestClientOnlyCombatQuarantined} qApplied={_manifestClientOnlyQuarantineApplied} qFailed={_manifestClientOnlyQuarantineFailed} qNoLocalKey={_manifestClientOnlyQuarantineNoLocalKey} qNoRuntime={_manifestClientOnlyQuarantineNoRuntime} qNonCombatSkipped={_manifestClientOnlyNonCombatSkipped} qAlready={_manifestClientOnlyAlreadyQuarantined}");
            Plugin.Log.Info($"[GameplayProbe] Phase5.3-G-client-hit-visual hostHitVisualPlayed={_hostHitVisualPlayed} hostHitFatalVisualPlayed={_hostHitFatalVisualPlayed} hostHitVisualFailedNoNpc={_hostHitVisualFailedNoNpc} hostHitVisualEventSent={_hostHitVisualEventSent} clientHitVisualEventRecv={_clientHitVisualEventRecv} clientHitVisualPlayed={_clientHitVisualPlayed} clientHitFatalVisualPlayed={_clientHitFatalVisualPlayed} clientHitVisualSkipNoBinding={_clientHitVisualSkipNoBinding} clientHitVisualSkipPendingDead={_clientHitVisualSkipPendingDead} clientHitVisualSkipTerminalDead={_clientHitVisualSkipTerminalDead} clientHitVisualSkipFatalTerminalDead={_clientHitVisualSkippedFatalTerminalDead} clientHitVisualDuplicateSeq={_clientHitVisualDuplicateSeq}");
            Plugin.Log.Info($"[GameplayProbe] Phase5.3-H-levelstep-trace levelStepResetContext={_levelGenStepTraced} nodeCoroutineMoveNext={_levelGenNodeCoroutineTraced}");
            Plugin.Log.Info($"[GameplayProbe] Phase5.3-G-connector-trace finalizeConnection={_levelGenFinalizeConnectionTraced} connectorFinalizeSpawn={_levelGenConnectorFinalizeSpawnTraced} doorBlockerRecorded={_levelGenDoorBlockerRecorded}");
            Plugin.Log.Info($"[GameplayProbe] Phase5.3-G-extra-room-trace extraRoomMoveNext={_levelGenExtraRoomTraced}");
            Plugin.Log.Info($"[GameplayProbe] Phase5.3-H-manifest-gate processedMatchingRun={_manifestProcessedMatchingRun} deferredSceneMismatch={_manifestDeferredSceneMismatch} deferredLevelMismatch={_manifestDeferredLevelMismatch} deferredSeedMismatch={_manifestDeferredSeedMismatch} droppedStaleRun={_manifestDroppedStaleRun} acceptedAfterRunStateFix={_manifestAcceptedAfterRunStateFix} deferredLevelMismatchAfterFix={_manifestDeferredLevelMismatchAfterFix} hasDeferred={(_deferredHostManifest != null)} playerExcluded={_manifestPlayerExcluded} quarantinePlayerSkipped={_quarantinePlayerSkipped}");
            Plugin.Log.Info($"[GameplayProbe] Phase5.3-G-manifest-quarantine currentClientOnly={_currentClientOnlyUnits} currentCombat={_currentClientOnlyCombatUnits} currentNonCombat={_currentClientOnlyNonCombatUnits} currentQuarantineApplied={_currentClientOnlyQuarantineApplied} currentAlready={_currentClientOnlyAlreadyQuarantined} currentNoRuntime={_currentClientOnlyNoRuntime} totalQuarantineApplied={_totalClientOnlyQuarantineApplied} totalClientOnlyUnitsSeen={_manifestClientOnlyUnits}");
            AuditHostBoundPuppetLocalAi();
            Plugin.Log.Info($"[GameplayProbe] Phase5.3-G-drift-diagnosis puppetsAudited={_hostBoundPuppetsAudited} navMeshStillEnabled={_localNavMeshStillEnabledOnHostBound} aiCanMove={_localAiCanMoveOnHostBound} rvoEnabled={_rvoStillEnabledOnHostBound} correctionLarge={_puppetTransformCorrectionLarge} correctionRepeated={_puppetTransformCorrectionRepeated} topDrift={FormatTopDrift(5)}");
            Plugin.Log.Info($"[GameplayProbe] Phase5.4-C-target-authority localCleared={_enemyTargetLocalCleared} localOverwrite={_enemyTargetLocalOverwrite} noKnownMember={_enemyTargetNoKnownMember} bossMembersDiscovered={_enemyTargetBossMembersDiscovered} bossSuppressed={_enemyTargetBossSuppressionApplied} suppressionFailed={_enemyTargetSuppressionFailed} hostTargetApplied={_enemyTargetHostTargetApplied}");
        }


        // Phase 5.5-RT3 diag: the host snapshot uses Npc.transform.position. If the enemy actually paths/renders via a
        // DIFFERENT transform (AiAgent / RichAI / Animator root / Renderer / Collider), the client faithfully follows a
        // transform that barely moves while the host's visible enemy walks elsewhere — exactly the "client enemy stuck,
        // host enemy paths around" Caves symptom. Log all sources (throttled per-enemy) so divergence is directly visible.
        private static readonly Dictionary<int, float> _enemyPosDiagLastAtByIdx = new Dictionary<int, float>();
        private static void LogEnemyPositionSourcesDiag(object npc, NetGameplayEntitySnapshot snapshot, Vector3 npcPos, float now)
        {
            try
            {
                if (!Plugin.Cfg.LogTeleportDiag.Value) return;
                int idx = snapshot.SpawnIndex;
                _enemyPosDiagLastAtByIdx.TryGetValue(idx, out float last);
                if (now - last < 1.0f) return;
                _enemyPosDiagLastAtByIdx[idx] = now;

                string P(object? comp)
                {
                    try { if (comp is Component c && c != null) return c.transform.position.ToString("F1"); } catch { }
                    return "-";
                }
                object? aiAgent = TryFindComponentByTypeName(npc, "AiAgent");
                object? richAi  = TryFindComponentByTypeName(npc, "CustomRichAI") ?? TryFindComponentByTypeName(npc, "RichAI");
                object? animator = TryFindComponentByTypeName(npc, "Animator");

                string richPos = "-";
                if (richAi != null)
                {
                    var p = TryGetMemberValue(richAi, "position");
                    richPos = p is Vector3 rv ? rv.ToString("F1") : P(richAi);
                }
                string rend = "-", col = "-", dest = "-", canMove = "-", vel = "-";
                if (npc is Component nc && nc != null)
                {
                    try { var r = nc.GetComponentInChildren<Renderer>(); if (r != null) rend = r.bounds.center.ToString("F1"); } catch { }
                    try { var co = nc.GetComponentInChildren<Collider>(); if (co != null) col = co.bounds.center.ToString("F1"); } catch { }
                }
                if (aiAgent != null)
                {
                    var d = TryGetMemberValue(aiAgent, "destination") ?? (richAi != null ? TryGetMemberValue(richAi, "destination") : null);
                    if (d is Vector3 dv) dest = dv.ToString("F1");
                    if (TryGetBoolMember(aiAgent, "canMove", out bool cm)) canMove = cm.ToString();
                    var v = TryGetMemberValue(aiAgent, "velocity") ?? (richAi != null ? TryGetMemberValue(richAi, "velocity") : null);
                    if (v is Vector3 vv) vel = vv.magnitude.ToString("F1");
                }
                Plugin.Log.Info($"[PosDiag] HOST idx={idx} unit={snapshot.EntityId.UnitIdentifier} npc={npcPos.ToString("F1")} aiAgent={P(aiAgent)} richAI={richPos} animator={P(animator)} renderer={rend} collider={col} dest={dest} canMove={canMove} vel={vel}");
            }
            catch { }
        }

        public static List<NetGameplayEnemyStateSnapshot> CollectHostEnemyStateSnapshots(NetRunState state, bool aliveOnly, int maxCount, ref int sequence)
        {
            var result = new List<NetGameplayEnemyStateSnapshot>();
            if (!IsEnabled()) return result;
            if (!Plugin.Cfg.EnableHostEnemyStateSnapshotMirror.Value) return result;
            if (!state.HasLevel) return result;
            if (Plugin.Cfg.EnableLevelSeedAuthority.Value && !state.HasLevelSeed) return result;

            if (maxCount <= 0) maxCount = 64;
            if (maxCount > 256) maxCount = 256;

            float now = Time.realtimeSinceStartup;

            // Phase 5.7-SC diag: count why alive enemies don't make it into the snapshot batch (the client only goes
            // stale when the host stops sending for a bound enemy → it must be excluded here).
            int scTotalNpc = 0, scExDead = 0, scExSpecial = 0, scExDestroyed = 0, scCollected = 0;
            foreach (var snapshot in EntitiesByLocalId.Values
                         .Where(s => string.Equals(s.Category, "Npc", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(s => s.SpawnIndex))
            {
                scTotalNpc++;
                if (aliveOnly && snapshot.IsDead)
                {
                    scExDead++;
                    MaybeLogSnapCollExclusion(snapshot, "dead");
                    continue;
                }
                // Phase 4.4.0-O3: Traders, interactive NPCs, and ambient entities must not enter enemy state snapshots
                if (snapshot.SyncCategory == SyncCatTrader
                    || snapshot.SyncCategory == SyncCatInteractNpc
                    || snapshot.SyncCategory == SyncCatAmbient)
                {
                    _traderExcludedFromEnemySync++;
                    scExSpecial++;
                    MaybeLogSnapCollExclusion(snapshot, "special:" + SyncCatName(snapshot.SyncCategory));
                    continue;
                }
                // EMP-2 (send side): the Emperor phase-1 worm's 10 body sections are NEVER applied on the client —
                // it runs its own local worm and the generic transform mirror skips them (IsEmperorWormSectionSnapshot
                // in the apply loop). Sending their high-rate snapshots (10 fast-moving units) was pure waste: the
                // client queued thousands and applied zero (enemyStateQueued climbing while enemyStateApplied=0), and
                // that receive/deserialize load helped push the client past Time.maximumDeltaTime into the fixed-step
                // catch-up spiral — the client-only Emperor hitch. Damage stays host-authoritative via the roster
                // ClientHit path, which does not use these snapshots. So never collect/send them.
                if (IsEmperorWormSectionSnapshot(snapshot))
                {
                    scExSpecial++;
                    MaybeLogSnapCollExclusion(snapshot, "emperor-worm-section");
                    continue;
                }
                if (result.Count >= maxCount) break;

                if (snapshot.TryGetRuntimeObject(out var runtimeObject) && runtimeObject != null)
                {
                    if (runtimeObject is UnityEngine.Object unityObject && unityObject == null)
                    {
                        scExDestroyed++;
                        MaybeLogSnapCollExclusion(snapshot, "destroyed-runtime");
                        continue;
                    }

                    if (TryGetPosition(runtimeObject, out var currentPosition))
                    {
                        snapshot.HasPosition = true;
                        snapshot.Position = currentPosition;
                        LogEnemyPositionSourcesDiag(runtimeObject, snapshot, currentPosition, now);
                    }
                }

                sequence++;
                var stateSnapshot = new NetGameplayEnemyStateSnapshot
                {
                    Sequence = sequence,
                    SourcePeerId = string.IsNullOrWhiteSpace(state.PeerId) ? "host" : state.PeerId,
                    ChapterName = state.ChapterName,
                    LevelIndex = state.LevelIndex,
                    HasLevelSeed = state.HasLevelSeed,
                    LevelSeed = state.LevelSeed,
                    SourceRevision = state.Revision,
                    SentAt = Time.realtimeSinceStartup,

                    SpawnIndex = snapshot.SpawnIndex,
                    CandidateKey = snapshot.EntityId.CandidateKey,
                    LocalInstanceId = snapshot.EntityId.LocalInstanceId,
                    UnityInstanceId = snapshot.EntityId.UnityInstanceId,
                    TypeName = snapshot.EntityId.TypeName,
                    UnitIdentifier = snapshot.EntityId.UnitIdentifier,
                    UnitGlobalId = snapshot.EntityId.UnitGlobalId,
                    Category = snapshot.Category,
                    ActorName = snapshot.ActorName,
                    HasPosition = snapshot.HasPosition,
                    Position = snapshot.Position,
                    IsDead = snapshot.IsDead,
                };

                if (snapshot.TryGetRuntimeObject(out var rotationObject) && rotationObject != null && TryGetRotationY(rotationObject, out var rotationY))
                {
                    stateSnapshot.HasRotationY = true;
                    stateSnapshot.RotationY = rotationY;
                }

                if (Plugin.Cfg.EnableHostEnemyAnimationMirror.Value
                    && snapshot.TryGetRuntimeObject(out var animatorObject)
                    && animatorObject != null)
                {
                    TryPopulateEnemyAnimatorSnapshot(animatorObject, stateSnapshot);
                }

                if (snapshot.TryGetRuntimeObject(out var intentObject) && intentObject != null)
                {
                    TryPopulateEnemyAiIntentSnapshot(intentObject, stateSnapshot);
                    // Phase 5.4-C G: host-authoritative target identity so the Client knows who the Host AI engages.
                    TryPopulateHostTargetIdentity(intentObject, stateSnapshot);
                }

                if (!ShouldSendHostEnemyStateSnapshot(stateSnapshot, now))
                    continue;

                // Phase 5.0 P2: interest management — reduce rate for distant IDLE enemies.
                // Phase 5.7-RB2: an enemy that has a combat target (HasHostTarget) is engaged and must sync at full rate
                // even when far from the (possibly stationary) Host player — not only during the brief attack-action frames.
                // Otherwise a client fighting it far away gets a frozen puppet between attacks (LogOutput104 idx=12).
                bool hasActiveCombat = (stateSnapshot.HasHostCombatAction
                                        && stateSnapshot.HostCombatActionKind != CombatActionNone)
                    || (Plugin.Cfg.FullRateForEngagedEnemies.Value && stateSnapshot.HasHostTarget);
                if (!ShouldSendByInterestManagement(snapshot, hasActiveCombat, now))
                    continue;

                RememberHostEnemyStateSnapshotSent(stateSnapshot, now);
                result.Add(stateSnapshot);
                scCollected++;
            }

            // Phase 5.7-SC: periodic batch summary — if collected << alive, enemies are being excluded (dead/destroyed on
            // host) which is exactly why a client's bound puppet starves and stands still.
            if (Plugin.Cfg.LogEnemyInterestDiag.Value && now - _lastSnapCollLogAt > 1f)
            {
                _lastSnapCollLogAt = now;
                NetLogger.Info($"[SnapColl] npcTracked={scTotalNpc} collected={scCollected} sent={result.Count} excludedDead={scExDead} excludedSpecial={scExSpecial} excludedDestroyed={scExDestroyed}");
            }

            return result;
        }

        private static float _lastSnapCollLogAt;
        private static int _snapCollExclLogged;
        private static void MaybeLogSnapCollExclusion(NetGameplayEntitySnapshot snap, string reason)
        {
            if (!Plugin.Cfg.LogEnemyInterestDiag.Value) return;
            if (_snapCollExclLogged >= 80) return;
            _snapCollExclLogged++;
            NetLogger.Info($"[SnapColl] excluded idx={snap.SpawnIndex} unit={snap.EntityId.UnitIdentifier} actor={snap.ActorName} reason={reason}");
        }

        private static bool ShouldSendHostEnemyStateSnapshot(NetGameplayEnemyStateSnapshot snapshot, float now)
        {
            if (snapshot == null) return false;
            if (!Plugin.Cfg.EnableEnemyStateSnapshotDeltaCompression.Value) return true;

            int spawnIndex = snapshot.SpawnIndex;
            if (spawnIndex <= 0) return true;
            if (!HostEnemyStateSendCacheBySpawnIndex.TryGetValue(spawnIndex, out var previous))
                return true;

            float heartbeat = Plugin.Cfg.EnemyStateSnapshotHeartbeatSeconds.Value;
            if (heartbeat < 0.1f) heartbeat = 0.1f;
            if (now - previous.LastSentAt >= heartbeat)
                return true;

            if (snapshot.IsDead != previous.IsDead)
                return true;

            if (snapshot.HasPosition != previous.HasPosition)
                return true;

            if (snapshot.HasPosition && previous.HasPosition)
            {
                float positionThreshold = Plugin.Cfg.EnemyStateSnapshotPositionDeltaThreshold.Value;
                if (positionThreshold < 0f) positionThreshold = 0f;
                if (Vector3.Distance(snapshot.Position, previous.Position) >= positionThreshold)
                    return true;
            }

            if (snapshot.HasRotationY != previous.HasRotationY)
                return true;

            if (snapshot.HasRotationY && previous.HasRotationY)
            {
                float rotationThreshold = Plugin.Cfg.EnemyStateSnapshotRotationDeltaThresholdDegrees.Value;
                if (rotationThreshold < 0f) rotationThreshold = 0f;
                if (Mathf.Abs(Mathf.DeltaAngle(snapshot.RotationY, previous.RotationY)) >= rotationThreshold)
                    return true;
            }

            if (snapshot.HasAnimatorState != previous.HasAnimatorState)
                return true;

            if (snapshot.HasAnimatorState && previous.HasAnimatorState)
            {
                if (snapshot.AnimatorFullPathHash != previous.AnimatorFullPathHash)
                    return true;

                float animThreshold = Plugin.Cfg.EnemyStateSnapshotAnimationTimeDeltaThreshold.Value;
                if (animThreshold < 0f) animThreshold = 0f;
                if (animThreshold > 1f) animThreshold = 1f;
                if (FractionDistance(snapshot.AnimatorNormalizedTime, previous.AnimatorNormalizedTime) >= animThreshold)
                    return true;

                if (snapshot.HasAnimatorMovingBool != previous.HasMovingBool || snapshot.AnimatorMovingBool != previous.MovingBool)
                    return true;
                if (snapshot.HasAnimatorAttackBool != previous.HasAttackBool || snapshot.AnimatorAttackBool != previous.AttackBool)
                    return true;
                if (snapshot.HasAnimatorCoweringBool != previous.HasCoweringBool || snapshot.AnimatorCoweringBool != previous.CoweringBool)
                    return true;
            }

            if (snapshot.HasHostCombatAction != previous.HasHostCombatAction)
                return true;
            if (snapshot.HasHostCombatAction && previous.HasHostCombatAction)
            {
                if (snapshot.HostCombatActionSequence != previous.HostCombatActionSequence) return true;
                if (snapshot.HostCombatActionKind != previous.HostCombatActionKind) return true;
                if (snapshot.HostCombatActionState != previous.HostCombatActionState) return true;
            }
            if (snapshot.HasHostCombatAim != previous.HasHostCombatAim) return true;
            if (snapshot.HasHostCombatAim && previous.HasHostCombatAim)
            {
                if (Vector3.Distance(snapshot.HostCombatOriginPosition, previous.HostCombatOriginPosition) > 0.05f) return true;
                if (Vector3.Distance(snapshot.HostCombatAimPosition, previous.HostCombatAimPosition) > 0.05f) return true;
            }
            if (snapshot.HasAiIntent != previous.HasAiIntent) return true;
            if (snapshot.HasAiIntent && previous.HasAiIntent)
            {
                if (snapshot.AiIntentSequence != previous.AiIntentSequence) return true;
                if (snapshot.AiIntentKind != previous.AiIntentKind) return true;
                if (Vector3.Distance(snapshot.AiIntentDestination, previous.AiIntentDestination) > 0.10f) return true;
            }
            if (snapshot.HasAiIntentLookAt != previous.HasAiIntentLookAt) return true;
            if (snapshot.HasAiIntentLookAt && previous.HasAiIntentLookAt && Vector3.Distance(snapshot.AiIntentLookAt, previous.AiIntentLookAt) > 0.10f) return true;

            if (snapshot.HostCombatAnimatorStateCount != previous.HostCombatAnimatorStateCount) return true;
            int combatStateCount = snapshot.HostCombatAnimatorStateCount;
            if (combatStateCount > 4) combatStateCount = 4;
            for (int i = 0; i < combatStateCount; i++)
            {
                if (snapshot.HostCombatAnimatorPathHashes[i] != previous.HostCombatAnimatorPathHashes[i]) return true;
                if (snapshot.HostCombatAnimatorFullPathHashes[i] != previous.HostCombatAnimatorFullPathHashes[i]) return true;
                if (FractionDistance(snapshot.HostCombatAnimatorNormalizedTimes[i], previous.HostCombatAnimatorNormalizedTimes[i]) > 0.20f) return true;
            }

            return false;
        }

        private static void RememberHostEnemyStateSnapshotSent(NetGameplayEnemyStateSnapshot snapshot, float now)
        {
            if (snapshot == null || snapshot.SpawnIndex <= 0) return;
            HostEnemyStateSendCacheBySpawnIndex[snapshot.SpawnIndex] = new HostEnemyStateSendCache
            {
                LastSentAt = now,
                HasPosition = snapshot.HasPosition,
                Position = snapshot.Position,
                HasRotationY = snapshot.HasRotationY,
                RotationY = snapshot.RotationY,
                IsDead = snapshot.IsDead,
                HasAnimatorState = snapshot.HasAnimatorState,
                AnimatorFullPathHash = snapshot.AnimatorFullPathHash,
                AnimatorNormalizedTime = snapshot.AnimatorNormalizedTime,
                HasMovingBool = snapshot.HasAnimatorMovingBool,
                MovingBool = snapshot.AnimatorMovingBool,
                HasAttackBool = snapshot.HasAnimatorAttackBool,
                AttackBool = snapshot.AnimatorAttackBool,
                HasCoweringBool = snapshot.HasAnimatorCoweringBool,
                CoweringBool = snapshot.AnimatorCoweringBool,
                HasHostCombatAction = snapshot.HasHostCombatAction,
                HostCombatActionKind = snapshot.HostCombatActionKind,
                HostCombatActionState = snapshot.HostCombatActionState,
                HostCombatActionSequence = snapshot.HostCombatActionSequence,
                HasHostCombatAim = snapshot.HasHostCombatAim,
                HostCombatOriginPosition = snapshot.HostCombatOriginPosition,
                HostCombatAimPosition = snapshot.HostCombatAimPosition,
                HasAiIntent = snapshot.HasAiIntent,
                AiIntentSequence = snapshot.AiIntentSequence,
                AiIntentKind = snapshot.AiIntentKind,
                AiIntentDestination = snapshot.AiIntentDestination,
                HasAiIntentLookAt = snapshot.HasAiIntentLookAt,
                AiIntentLookAt = snapshot.AiIntentLookAt,
                HostCombatAnimatorStateCount = snapshot.HostCombatAnimatorStateCount,
            };
            int combatStateCount = snapshot.HostCombatAnimatorStateCount;
            if (combatStateCount > 4) combatStateCount = 4;
            for (int i = 0; i < combatStateCount; i++)
            {
                HostEnemyStateSendCacheBySpawnIndex[snapshot.SpawnIndex].HostCombatAnimatorPathHashes[i] = snapshot.HostCombatAnimatorPathHashes[i];
                HostEnemyStateSendCacheBySpawnIndex[snapshot.SpawnIndex].HostCombatAnimatorFullPathHashes[i] = snapshot.HostCombatAnimatorFullPathHashes[i];
                HostEnemyStateSendCacheBySpawnIndex[snapshot.SpawnIndex].HostCombatAnimatorNormalizedTimes[i] = snapshot.HostCombatAnimatorNormalizedTimes[i];
            }
        }

        public static void ProcessHostEnemyStateSnapshots(List<NetGameplayEnemyStateSnapshot> snapshots)
        {
            if (!IsEnabled()) return;
            if (snapshots == null || snapshots.Count == 0) return;

            if (!NetRunStateBridge.TryGetLocalRunState(out var localState))
            {
                if (Plugin.Cfg.LogReceivedEnemyStateSnapshots.Value)
                    NetLogger.Warn($"[EnemyStateMirror] Ignoring HostEnemyStateSnapshot batch because local run state is unavailable count={snapshots.Count}");
                return;
            }

            int sceneSkipped = 0;
            int matched = 0;
            int unmatched = 0;
            int deadMismatch = 0;
            int drifted = 0;
            int queued = 0;
            float totalDelta = 0f;
            float maxDelta = 0f;
            string maxDeltaDetail = "";
            float tolerance = Plugin.Cfg.EnemyStateSnapshotPositionTolerance.Value;
            if (tolerance < 0f) tolerance = 0f;

            foreach (var hostSnapshot in snapshots)
            {
                if (!hostSnapshot.MatchesScene(localState))
                {
                    sceneSkipped++;
                    continue;
                }

                // SC2: record receipt per hostIdx (before match), so a stale puppet can be classified received-vs-not.
                if (hostSnapshot.SpawnIndex > 0)
                    _clientLastSnapshotRecvByHostIdx[hostSnapshot.SpawnIndex] = Time.realtimeSinceStartup;

                if (!TryFindLocalEnemyStateMatch(hostSnapshot, out var localSnapshot, out var detail))
                {
                    unmatched++;
                    if (Plugin.Cfg.EnableDebugLog.Value)
                        NetLogger.Debug($"[EnemyStateMirror] No local match for host snapshot idx={hostSnapshot.SpawnIndex} actor={hostSnapshot.ActorName} detail={detail}");
                    continue;
                }

                matched++;
                if (localSnapshot != null && localSnapshot.IsDead != hostSnapshot.IsDead)
                    deadMismatch++;

                if (localSnapshot != null && hostSnapshot.HasPosition && localSnapshot.HasPosition)
                {
                    float delta = Vector3.Distance(localSnapshot.Position, hostSnapshot.Position);
                    totalDelta += delta;
                    if (delta > tolerance) drifted++;
                    if (delta > maxDelta)
                    {
                        maxDelta = delta;
                        maxDeltaDetail = $"idx={hostSnapshot.SpawnIndex} actor={hostSnapshot.ActorName} local={localSnapshot.PositionText} host={hostSnapshot.PositionText}";
                    }
                }

                string queueDetail = "apply disabled";
                if (Plugin.Cfg.ApplyReceivedEnemyStateSnapshots.Value && TryQueueEnemyStateTarget(localSnapshot, hostSnapshot, out queueDetail))
                {
                    queued++;
                }
                else if (Plugin.Cfg.ApplyReceivedEnemyStateSnapshots.Value && Plugin.Cfg.EnableDebugLog.Value)
                {
                    NetLogger.Debug($"[EnemyStateMirror] Snapshot apply not queued idx={hostSnapshot.SpawnIndex} actor={hostSnapshot.ActorName} detail={queueDetail}");
                }
            }

            if (!Plugin.Cfg.LogReceivedEnemyStateSnapshots.Value) return;

            float avgDelta = matched > 0 ? totalDelta / matched : 0f;
            bool apply = Plugin.Cfg.ApplyReceivedEnemyStateSnapshots.Value;
            var first = snapshots[0];
            NetLogger.Info($"[EnemyStateMirror] Batch seq={first.Sequence}-{snapshots[snapshots.Count - 1].Sequence} count={snapshots.Count} matched={matched} unmatched={unmatched} sceneSkipped={sceneSkipped} deadMismatch={deadMismatch} drift>{tolerance:F1}m={drifted} avgDelta={avgDelta:F2}m maxDelta={maxDelta:F2}m queued={queued} activeTargets={PendingEnemyStateTargets.Count} scene={first.SceneKey} seed={first.SeedText} apply={apply}");

            if (Plugin.Cfg.EnableDebugLog.Value && maxDelta > tolerance && !string.IsNullOrWhiteSpace(maxDeltaDetail))
                NetLogger.Debug($"[EnemyStateMirror] Worst drift: {maxDeltaDetail}");
        }

        private static bool TryQueueEnemyStateTarget(NetGameplayEntitySnapshot? localSnapshot, NetGameplayEnemyStateSnapshot hostSnapshot, out string detail)
        {
            detail = "";

            if (localSnapshot == null)
            {
                detail = "local snapshot is null";
                return false;
            }

            if (localSnapshot.IsDead)
            {
                detail = $"local entity already dead idx={localSnapshot.SpawnIndex}";
                return false;
            }

            if (hostSnapshot.IsDead)
            {
                detail = $"host entity is dead idx={hostSnapshot.SpawnIndex}";
                return false;
            }

            if (!hostSnapshot.HasPosition)
            {
                detail = "host snapshot has no position";
                return false;
            }

            if (!IsFinite(hostSnapshot.Position))
            {
                detail = $"host snapshot position is not finite idx={hostSnapshot.SpawnIndex}";
                return false;
            }

            if (localSnapshot.HasPosition && IsFinite(localSnapshot.Position))
            {
                float applyDistance = Vector3.Distance(localSnapshot.Position, hostSnapshot.Position);
                float maxApplyDistance = Math.Max(100f, Plugin.Cfg.EnemyStateSnapshotSnapDistance.Value * 10f);
                if (applyDistance > maxApplyDistance)
                {
                    detail = $"unsafe apply distance={applyDistance:F2}m > max={maxApplyDistance:F2}m idx={hostSnapshot.SpawnIndex}";
                    return false;
                }
            }

            if (!localSnapshot.TryGetRuntimeObject(out var runtimeObject) || runtimeObject == null)
            {
                detail = $"runtime object unavailable idx={localSnapshot.SpawnIndex}";
                return false;
            }

            if (runtimeObject is UnityEngine.Object unityObject && unityObject == null)
            {
                detail = $"runtime Unity object destroyed idx={localSnapshot.SpawnIndex}";
                return false;
            }

            if (!TryGetTransform(runtimeObject, out var transform) || transform == null)
            {
                detail = $"runtime object has no transform idx={localSnapshot.SpawnIndex}";
                return false;
            }

            string key = GetSnapshotTargetKey(localSnapshot);
            if (string.IsNullOrWhiteSpace(key))
            {
                detail = $"no stable local key idx={localSnapshot.SpawnIndex}";
                return false;
            }

            if (PendingEnemyStateTargets.TryGetValue(key, out var existingTarget) && hostSnapshot.Sequence <= existingTarget.Sequence)
            {
                detail = $"stale transform target idx={localSnapshot.SpawnIndex} incomingSeq={hostSnapshot.Sequence} currentSeq={existingTarget.Sequence}";
                return false;
            }

            float now = Time.realtimeSinceStartup;
            Vector3 startPosition = localSnapshot.HasPosition && IsFinite(localSnapshot.Position)
                ? localSnapshot.Position
                : hostSnapshot.Position;
            float playbackDuration = GetEnemyStateSnapshotPlaybackDuration(key, now, startPosition, hostSnapshot.Position, hostSnapshot, out var playbackDetail);

            PendingEnemyStateTargets[key] = new EnemyStateTarget
            {
                Snapshot = localSnapshot,
                HostSnapshot = hostSnapshot,
                StartPosition = startPosition,
                HasPosition = true,
                Position = hostSnapshot.Position,
                HasRotationY = hostSnapshot.HasRotationY,
                RotationY = hostSnapshot.RotationY,
                Sequence = hostSnapshot.Sequence,
                PlaybackStartedAt = now,
                PlaybackDuration = playbackDuration,
                LastUpdatedAt = now,
                RuntimeObject = runtimeObject,
                Transform = transform,
            };
            _enemyStateTargetsQueued++;
            detail = $"queued transform target idx={localSnapshot.SpawnIndex} seq={hostSnapshot.Sequence} playback={playbackDetail}";
            return true;
        }

        private static float GetEnemyStateSnapshotPlaybackDuration(string key, float now,
            Vector3 startPosition, Vector3 hostPosition,
            NetGameplayEnemyStateSnapshot? hostSnapshot, out string detail)
        {
            // Phase 5.2: combat snapshots snap immediately — no interpolation lag during attacks.
            bool isActiveCombat = hostSnapshot != null
                && hostSnapshot.HasHostCombatAction
                && hostSnapshot.HostCombatActionKind != CombatActionNone;
            if (isActiveCombat)
            {
                detail = "combat-snap playback=0";
                return 0f;
            }

            float snapDistance = Plugin.Cfg.EnemyStateSnapshotSnapDistance.Value;
            if (snapDistance < 0.1f) snapDistance = 0.1f;
            float distance = Vector3.Distance(startPosition, hostPosition);
            if (distance >= snapDistance)
            {
                detail = $"snap distance={distance:F2}m duration=0";
                return 0f;
            }

            float hz = Plugin.Cfg.EnemyStateSnapshotSendRateHz.Value;
            if (hz < 0.1f) hz = 0.1f;
            float expectedInterval = 1f / hz;

            float observedInterval = expectedInterval;
            if (!string.IsNullOrWhiteSpace(key) && PendingEnemyStateTargets.TryGetValue(key, out var previous))
            {
                float age = now - previous.LastUpdatedAt;
                if (age >= 0.02f && age <= 2f)
                    observedInterval = age;
            }

            float multiplier = Plugin.Cfg.EnemyStateSnapshotPlaybackDurationMultiplier.Value;
            if (multiplier < 0.5f) multiplier = 0.5f;
            if (multiplier > 2.5f) multiplier = 2.5f;

            float duration = observedInterval * multiplier;
            float minDuration = Mathf.Max(0.03f, expectedInterval * 0.5f);
            float maxDuration = Mathf.Min(1.25f, expectedInterval * 2.5f);
            duration = Mathf.Clamp(duration, minDuration, maxDuration);

            detail = $"duration={duration:F3}s observed={observedInterval:F3}s expected={expectedInterval:F3}s distance={distance:F2}m";
            return duration;
        }

        private static void ApplyPendingEnemyStateTargets()
        {
            if (PendingEnemyStateTargets.Count == 0) return;

            if (!Plugin.Cfg.ApplyReceivedEnemyStateSnapshots.Value)
            {
                PendingEnemyStateTargets.Clear();
                return;
            }

            float now = Time.realtimeSinceStartup;
            float interpolationSpeed = Plugin.Cfg.EnemyStateSnapshotInterpolationSpeed.Value;
            if (interpolationSpeed < 1f) interpolationSpeed = 1f;
            float snapDistance = Plugin.Cfg.EnemyStateSnapshotSnapDistance.Value;
            if (snapDistance < 0.1f) snapDistance = 0.1f;
            bool applyRotationY = Plugin.Cfg.EnemyStateSnapshotApplyRotationY.Value;

            ScratchEnemyStateTargetKeys.Clear();
            ScratchEnemyStateTargetKeys.AddRange(PendingEnemyStateTargets.Keys);

            foreach (var key in ScratchEnemyStateTargetKeys)
            {
                if (!PendingEnemyStateTargets.TryGetValue(key, out var target))
                    continue;

                var snapshot = target.Snapshot;

                if (snapshot == null)
                {
                    ReleaseEnemyPuppet(key, "snapshot null");
                    PendingEnemyStateTargets.Remove(key);
                    continue;
                }

                // Phase 5.3-C P0-1: terminal dead corpses stay where the death snap placed them.
                // Drop the pending movement target — corpse position is final.
                if (IsClientTerminalDeadByKey(key))
                {
                    _terminalDeadBlockedMovement++;
                    PendingEnemyStateTargets.Remove(key);
                    continue;
                }

                if (snapshot.IsDead || !target.HasPosition)
                {
                    // Host owns death/despawn. Never release a host-bound puppet just because the
                    // local snapshot is marked dead or lacks position — wait for the HostDeathEvent
                    // or HostRoster removal to make that decision authoritatively.
                    bool isHostBound = ClientLocalKeyToHostSpawnIndex.ContainsKey(key);
                    if (isHostBound)
                    {
                        _puppetReleaseSuppressedAwaitingHost++;
                        PendingEnemyStateTargets.Remove(key);
                    }
                    else
                    {
                        ReleaseEnemyPuppet(key, snapshot.IsDead ? "local-snapshot dead (unbound)" : "no position (unbound)");
                        PendingEnemyStateTargets.Remove(key);
                    }
                    continue;
                }

                if (now - target.LastUpdatedAt > 2f)
                {
                    // Puppet release itself is governed by ClientEnemyPuppetStaleReleaseSeconds.
                    PendingEnemyStateTargets.Remove(key);
                    continue;
                }

                object? runtimeObject = target.RuntimeObject;
                Transform? transform = target.Transform;

                bool runtimeInvalid = runtimeObject == null
                    || (runtimeObject is UnityEngine.Object runtimeUnity && runtimeUnity == null)
                    || transform == null
                    || (transform is UnityEngine.Object transformUnity && transformUnity == null);

                if (runtimeInvalid)
                {
                    bool hostBoundForRuntime = ClientLocalKeyToHostSpawnIndex.ContainsKey(key);
                    if (!snapshot.TryGetRuntimeObject(out runtimeObject) || runtimeObject == null)
                    {
                        ReleaseEnemyPuppet(key, hostBoundForRuntime
                            ? "runtime object unavailable (host-bound, likely scene reload)"
                            : "runtime object unavailable");
                        PendingEnemyStateTargets.Remove(key);
                        continue;
                    }

                    if (runtimeObject is UnityEngine.Object unityObject && unityObject == null)
                    {
                        ReleaseEnemyPuppet(key, hostBoundForRuntime
                            ? "runtime Unity object destroyed (host-bound)"
                            : "runtime Unity object destroyed");
                        PendingEnemyStateTargets.Remove(key);
                        continue;
                    }

                    if (!TryGetTransform(runtimeObject, out transform) || transform == null)
                    {
                        ReleaseEnemyPuppet(key, hostBoundForRuntime
                            ? "runtime object has no transform (host-bound)"
                            : "runtime object has no transform");
                        PendingEnemyStateTargets.Remove(key);
                        continue;
                    }

                    target.RuntimeObject = runtimeObject;
                    target.Transform = transform;
                }

                // Phase 5.4-G5: Witch Phase 2 dome witches are positioned by the host-authoritative dome manifest. Skip
                // the ordinary transform snapshot for them, or it would pull them off their dome positions (the witches
                // are bound puppets but their layout is owned by WitchPhase2Manifest while Phase 2 is active).
                if (Boss.NetBossEncounterManager.IsWitchPhase2Suppressed(runtimeObject))
                    continue;

                // Phase RT3-Cousin-arms-Anim: the Cousin arm is a self-animating scripted prop — it pops out of a pool,
                // idles, throws, and retracts/disappears, an Animator sequence driven entirely by its OWN behaviour tree.
                // It must NEVER be puppet-ized (puppet mode disables the BT) NOR transform-dragged: the puppet animator
                // mirror only reproduces Animator states during host ATTACK windows, so the arm's idle + disappear states
                // were never reproduced and its Animator looped its default Appear state (the reported bug). Skipping the
                // whole snapshot application here lets its BT run locally (faithful appear→idle→attack→disappear). Death
                // still comes through EnemyDeathMirror (HostEnemyDeathEvent) and the throw is de-fanged by CousinArmPatches,
                // so damage stays host-authoritative. Skipping the RT3 puppet-drive registration alone (BossDynamicSpawn-
                // Manifest.IsSpecialAdd) was insufficient — the host also sends ordinary enemy-state snapshots for the arm
                // by SpawnIndex, and the client late-binds + puppet-izes it through THIS path (LogOutput174).
                if (IsSelfAnimatingClientBossAdd(snapshot))
                    continue;

                // EMP-2: the Emperor worm is a 10-section boss with its OWN smooth section-follow (UpdateWormSections,
                // driven from the local worm each frame). Routing its sections through the generic per-frame transform
                // mirror teleports 30 colliders large distances every frame, which churns the PhysX broadphase — the
                // client-only native ~1fps (proven: disabling the section colliders dropped the hitch 61→3; moving via
                // Rigidbody.position instead of transform.position did NOT help, because the colliders are still in the
                // broadphase being teleported). Skip the snapshot here and let the local worm move them smoothly (same
                // pattern as the Cousin arm above). Positions diverge from host (double-worm) — the pre-existing no-lag
                // behavior — but damage still routes host-authoritatively through the roster ClientHit path. A proper
                // non-teleport worm position sync (stream the head, run UpdateWormSections locally) is a later design.
                if (IsEmperorWormSectionSnapshot(snapshot))
                    continue;

                // LD-Sandstorm / F4: while the Desert boss INTRO is playing on this client, keep it out of the generic
                // puppet system so its own intro chain runs locally (visible body + intro presentation). Only during the
                // intro (fightStarted==false, player Cinematic-locked + invulnerable) — once TriggerFight fires the puppet
                // resumes (host-driven position/animator, AI suppressed) so the boss can't double-hit the player. See
                // IsDesertBossSnapshot for the full rationale.
                if (IsDesertBossSnapshot(snapshot) && Boss.NetBossEncounterManager.IsLocalIntroPresentationActive())
                    continue;

                // LD-Sandstorm / F4-P1JMP: during the Desert fight the boss PIKE's position is owned by the host-replayed
                // native jumps (PikeJump events) — the transform puppet fighting the local arc snapped 53% of frames
                // (Log314). And while the boss BODY is welded to a pike mount (parent "CarrySpot*", the carrier zeroes
                // its localPosition every frame) the mount owns its position — snapshot-snapping the welded body caused
                // the 132 m spikes. Off the mount (dismount reparents to unitRoot) the puppet resumes (Log290 model).
                // POSITION-only exemption: the puppet record, intent windows and combat root-replay below must keep
                // running (the boss's machine-gun fire mirror rides them) — only the transform/animator apply is skipped
                // (see the second check further down).
                bool desertPositionExempt =
                       (IsDesertBossPikeSnapshot(snapshot) && Boss.NetBossEncounterManager.IsDesertPikePuppetExempt())
                    || (IsDesertBossSnapshot(snapshot) && !IsDesertBossPikeSnapshot(snapshot) && Boss.NetBossEncounterManager.IsDesertBossBodyPuppetExempt());

                EnsureClientEnemyPuppetMode(key, snapshot, target.HostSnapshot, runtimeObject, now);

                // Phase 4.4.0-O/O2: create/refresh per-NPC authorization window; determine control mode.
                int puppetNpcId = ObjectIdentity(runtimeObject);
                var controlMode = ClientEnemyControlMode.PassiveSnapshot;
                if (target.HostSnapshot != null && NetConfig.GetMode() == NetMode.Client)
                {
                    if (puppetNpcId != 0)
                    {
                        bool isNewIntentSeq = TryCreateOrRefreshClientIntentWindow(puppetNpcId, target.HostSnapshot, now);
                        // O3-B: when a new combat intent sequence arrives, immediately execute root replay.
                        if (isNewIntentSeq
                            && _clientAuthorizedIntentByNpcId.TryGetValue(puppetNpcId, out var newIntentWindow)
                            && ActiveEnemyPuppetsByNpcId.TryGetValue(puppetNpcId, out var rootReplayRecord)
                            && rootReplayRecord.Npc != null)
                        {
                            TryExecuteClientEnemyIntentRootReplay(rootReplayRecord, newIntentWindow, rootReplayRecord.Npc, target.HostSnapshot);
                        }
                    }

                    if (puppetNpcId != 0 && _clientAuthorizedIntentByNpcId.TryGetValue(puppetNpcId, out var cw) && now <= cw.ExpiresAt)
                    {
                        bool isCombatKind = cw.Kind == EnemyIntentKindAttackMelee
                                         || cw.Kind == EnemyIntentKindAttackRanged
                                         || cw.Kind == EnemyIntentKindWeaponAction;
                        controlMode = isCombatKind ? ClientEnemyControlMode.AuthorizedCombat : ClientEnemyControlMode.IntentLocomotion;
                    }
                    else if (IsClientEnemyIntentDrivenMotionEnabled() && target.HostSnapshot.HasAiIntent)
                    {
                        controlMode = ClientEnemyControlMode.IntentLocomotion;
                    }
                    if (ActiveEnemyPuppets.TryGetValue(key, out var cmRec))
                        cmRec.ControlMode = controlMode;
                }

                // F4-P1JMP position-only exemption (see above): the replayed jumps / the mount own this transform, and its
                // animator is native-driven by the local replayed jump — skip the position + animator apply, keep the rest.
                if (desertPositionExempt)
                    continue;

                Vector3 current = transform.position;
                if (!IsFinite(current) || !IsFinite(target.Position))
                {
                    PendingEnemyStateTargets.Remove(key);
                    continue;
                }

                float playbackT = target.PlaybackDuration <= 0.001f
                    ? 1f
                    : Mathf.Clamp01((now - target.PlaybackStartedAt) / target.PlaybackDuration);
                Vector3 desiredPosition = Vector3.Lerp(target.StartPosition, target.Position, playbackT);

                bool intentDriven = IsClientEnemyIntentDrivenMotionEnabled() && target.HostSnapshot.HasAiIntent;
                // O2: AuthorizedCombat blocks NavMesh steering to prevent ranged enemies chasing targets.
                if (intentDriven && controlMode != ClientEnemyControlMode.AuthorizedCombat
                    && ActiveEnemyPuppets.TryGetValue(key, out var intentRecord))
                    TryApplyClientEnemyAiIntent(intentRecord, runtimeObject, target.HostSnapshot, now);

                float distanceToHostTarget = Vector3.Distance(current, target.Position);
                float distanceToDesired = Vector3.Distance(current, desiredPosition);
                Vector3 nextPosition;
                bool snapped = false;
                if (intentDriven)
                {
                    float correctionDistance = Plugin.Cfg.EnemyIntentCorrectionDistance.Value;
                    if (correctionDistance < 0.25f) correctionDistance = 0.25f;
                    float hardSnap = Plugin.Cfg.EnemyIntentHardSnapDistance.Value;
                    if (hardSnap < correctionDistance) hardSnap = correctionDistance + 0.50f;
                    if (distanceToHostTarget >= hardSnap)
                    {
                        nextPosition = target.Position;
                        snapped = true;
                        _hardDriftCorrections++;
                        _clientEnemyAiIntentCorrections++;
                    }
                    else if (controlMode == ClientEnemyControlMode.AuthorizedCombat)
                    {
                        // Always drift toward host position during combat — no dead zone.
                        // 10% lerp rate is gentle enough to preserve attack animations.
                        float dt2 = Mathf.Max(Time.deltaTime, 1f / 60f);
                        float t2 = Mathf.Clamp01(interpolationSpeed * dt2 * 0.10f);
                        nextPosition = Vector3.Lerp(current, target.Position, t2);
                        _softDriftCorrections++;
                    }
                    else if (controlMode == ClientEnemyControlMode.IntentLocomotion)
                    {
                        // Phase 5.2: apply gentle correction when significantly drifted.
                        // Prevents NavMesh locomotion from accumulating large position error.
                        float gentleThreshold = Mathf.Max(0.4f, correctionDistance * 0.5f);
                        if (distanceToHostTarget >= gentleThreshold)
                        {
                            float dt2 = Mathf.Max(Time.deltaTime, 1f / 60f);
                            float t2 = Mathf.Clamp01(interpolationSpeed * dt2 * 0.15f);
                            nextPosition = Vector3.Lerp(current, desiredPosition, t2);
                            _softDriftCorrections++;
                        }
                        else
                        {
                            nextPosition = current;
                            _driftSkippedIntentLocomotion++;
                        }
                    }
                    else if (distanceToHostTarget >= correctionDistance)
                    {
                        float dt = Mathf.Max(Time.deltaTime, 1f / 60f);
                        float t = Mathf.Clamp01(interpolationSpeed * dt * 0.35f);
                        nextPosition = Vector3.Lerp(current, desiredPosition, t);
                        _softDriftCorrections++;
                        _clientEnemyAiIntentCorrections++;
                    }
                    else
                    {
                        // Intent-driven path: let local movement/Animator advance naturally.
                        nextPosition = current;
                    }
                }
                else if (distanceToHostTarget >= snapDistance)
                {
                    nextPosition = target.Position;
                    snapped = true;
                }
                else if (distanceToDesired >= snapDistance)
                {
                    nextPosition = desiredPosition;
                    snapped = true;
                }
                else
                {
                    float dt = Mathf.Max(Time.deltaTime, 1f / 60f);
                    float t = Mathf.Clamp01(interpolationSpeed * dt);
                    nextPosition = Vector3.Lerp(current, desiredPosition, t);
                }

                if (Vector3.Distance(current, nextPosition) > 0.001f)
                    transform.position = nextPosition;
                snapshot.HasPosition = true;
                snapshot.Position = nextPosition;
                snapshot.LastSeenAt = now;

                // Phase 5.3-G drift diagnosis: record per-hostIdx host-position error so the summary
                // can name the worst-drifting enemies and whether it correlates with snap/hard/soft.
                NoteDrift(key, distanceToHostTarget, snapped, snapshot.EntityId.UnitIdentifier);

                ApplyClientEnemyPuppetMotionAnimation(key, target.HostSnapshot, runtimeObject, current, nextPosition, target.Position, now);
                if (!target.AnimationAppliedForSequence)
                {
                    ApplyClientEnemyAnimationMirror(key, target.HostSnapshot, runtimeObject, now);
                    target.AnimationAppliedForSequence = true;
                }

                _enemyStateTargetsApplied++;

                if (snapped)
                {
                    _enemyStateTargetsSnapped++;
                    if (Plugin.Cfg.EnableDebugLog.Value && !target.LoggedSnap)
                    {
                        target.LoggedSnap = true;
                        NetLogger.Debug($"[EnemyStateMirror] Snapped local enemy to Host playback idx={snapshot.SpawnIndex} actor={snapshot.ActorName} desiredDistance={distanceToDesired:F2}m hostDistance={distanceToHostTarget:F2}m target=({target.Position.x:F2},{target.Position.y:F2},{target.Position.z:F2})");
                    }
                }

                if (applyRotationY && target.HasRotationY && IsFinite(target.RotationY))
                {
                    Vector3 euler = transform.eulerAngles;
                    if (IsFinite(euler))
                    {
                        float rotT = snapped ? 1f : Mathf.Clamp01(interpolationSpeed * Mathf.Max(Time.deltaTime, 1f / 60f));
                        euler.y = Mathf.LerpAngle(euler.y, target.RotationY, rotT);
                        if (IsFinite(euler))
                            transform.eulerAngles = euler;
                    }
                }

                if (playbackT >= 1f && Vector3.Distance(nextPosition, target.Position) < 0.05f && now - target.LastUpdatedAt > 0.75f)
                    PendingEnemyStateTargets.Remove(key);
            }
        }


        public static bool ShouldSkipClientEnemyPuppetFinalMovement(object? movementDriver)
        {
            if (!IsClientEnemyPuppetModeEnabled()) return false;
            if (movementDriver == null) return false;
            if (IsClientEnemyIntentDrivenMotionEnabled()) return false;
            return ClientPuppetMovementDriverIds.Contains(ObjectIdentity(movementDriver));
        }

        public static bool ShouldBlockClientEnemyPuppetAiAgentState(object? aiAgent, bool requestedState)
        {
            if (!IsClientEnemyPuppetModeEnabled()) return false;
            if (aiAgent == null) return false;
            // Allow the game to disable movement. In intent-driven mode, only our replay path may re-enable it.
            if (!requestedState) return false;
            if (_clientPuppetIntentReplayDepth > 0) return false;
            return ClientPuppetAiAgentIds.Contains(ObjectIdentity(aiAgent));
        }

        public static bool ShouldBlockClientEnemyPuppetAiAgentMovement(object? aiAgent)
        {
            if (!IsClientEnemyPuppetModeEnabled()) return false;
            if (aiAgent == null) return false;
            if (_clientPuppetIntentReplayDepth > 0) return false;
            return ClientPuppetAiAgentIds.Contains(ObjectIdentity(aiAgent));
        }

        public static bool ShouldBlockClientEnemyPuppetNpcMovement(object? npc)
        {
            if (!IsClientEnemyPuppetModeEnabled()) return false;
            if (npc == null) return false;
            if (_clientPuppetIntentReplayDepth > 0) return false;
            return ClientPuppetNpcIds.Contains(ObjectIdentity(npc));
        }

        public static bool ShouldBlockClientEnemyPuppetBehaviourTreeActivation(object? npc, bool requestedState)
        {
            if (!IsClientEnemyPuppetModeEnabled()) return false;
            if (npc == null) return false;
            return requestedState && ClientPuppetNpcIds.Contains(ObjectIdentity(npc));
        }

        public static bool ShouldBlockClientEnemyPuppetNpcTargeting(object? npc, object? target, string source, out string detail)
        {
            detail = "";
            if (!IsHostOnlyEnemyTargetAuthorityEnabled()) return false;
            if (npc == null) return false;
            if (_clientPuppetInternalTargetClearDepth > 0) return false;

            int npcId = ObjectIdentity(npc);
            if (npcId == 0 || !ActiveEnemyPuppetsByNpcId.TryGetValue(npcId, out var record)) return false;

            float now = Time.realtimeSinceStartup;
            _clientEnemyPuppetTargetBlocks++;
            ApplyClientEnemyTargetAuthority(record, now, "blocked " + Clean(source));
            detail = $"idx={record.Snapshot.SpawnIndex} actor={record.Snapshot.ActorName} source={Clean(source)} target={DescribeTarget(target)}";
            MaybeLogClientEnemyTargetAuthority(record, now, "block-local-npc-target", detail, force: false);
            return true;
        }

        public static bool ShouldBlockClientEnemyPuppetAiAgentTargeting(object? aiAgent, string source, out string detail)
        {
            detail = "";
            if (!IsHostOnlyEnemyTargetAuthorityEnabled()) return false;
            if (aiAgent == null) return false;

            int aiId = ObjectIdentity(aiAgent);
            if (aiId == 0 || !ClientPuppetAiAgentIds.Contains(aiId)) return false;

            _clientEnemyPuppetTargetBlocks++;
            // EMP-1b: proven-barren sections (worm) — keep blocking, but skip the record lookup + reflection apply
            // + per-frame log entirely. The suppression (return true) is what matters; the apply clears nothing here.
            if (BarrenTargetAuthorityAiIds.Contains(aiId))
                return true;

            float now = Time.realtimeSinceStartup;
            if (TryGetPuppetRecordForAiAgent(aiAgent, out var record))
            {
                ApplyClientEnemyTargetAuthority(record, now, "blocked " + Clean(source));
                detail = $"idx={record.Snapshot.SpawnIndex} actor={record.Snapshot.ActorName} source={Clean(source)} ai={aiAgent.GetType().Name}";
                MaybeLogClientEnemyTargetAuthority(record, now, "block-local-ai-target", detail, force: false);
            }
            else
            {
                detail = $"source={Clean(source)} ai={aiAgent.GetType().Name}";
            }
            return true;
        }

        public static void ReportClientEnemyPuppetAiTargetResult(object? aiAgent, object? result, string source)
        {
            if (!IsHostOnlyEnemyTargetAuthorityEnabled()) return;
            if (aiAgent == null || result == null) return;

            int aiId = ObjectIdentity(aiAgent);
            if (aiId == 0 || !ClientPuppetAiAgentIds.Contains(aiId)) return;

            float now = Time.realtimeSinceStartup;
            _clientEnemyPuppetTargetBlocks++;
            if (TryGetPuppetRecordForAiAgent(aiAgent, out var record))
            {
                ApplyClientEnemyTargetAuthority(record, now, "observed " + Clean(source));
                string detail = $"idx={record.Snapshot.SpawnIndex} actor={record.Snapshot.ActorName} source={Clean(source)} returnedTarget={DescribeTarget(result)}";
                MaybeLogClientEnemyTargetAuthority(record, now, "observed-local-ai-target", detail, force: false);
            }
        }

        public static bool ShouldBlockClientEnemyPuppetNpcCombat(object? npc, string source, out string detail)
        {
            detail = "";
            if (!IsEnemyCombatProbeEnabled()) return false;
            if (!IsClientEnemyPuppetModeEnabled()) return false;
            if (npc == null) return false;

            // Phase 4.4.0-O3: non-combat entities must never be blocked (they are not puppeted)
            if (TryGetSnapshotForEntity(npc, out var npcEntitySnapshot) && npcEntitySnapshot != null && IsNonCombatForSync(npcEntitySnapshot))
            {
                _combatProbeRejectedNonCombat++;
                return false;
            }
            // O3-C: Quarantined client-only CombatEnemies must have all combat blocked.
            if (npcEntitySnapshot != null)
            {
                string qKey = GetSnapshotTargetKey(npcEntitySnapshot);
                if (!string.IsNullOrWhiteSpace(qKey) && ClientQuarantinedEntities.Contains(qKey))
                {
                    _quarantinedCombatSuppressed++;
                    detail = $"quarantined client-only unit={npcEntitySnapshot.EntityId.UnitIdentifier} localIdx={npcEntitySnapshot.SpawnIndex}";
                    return true;
                }
            }

            int npcId = ObjectIdentity(npc);
            if (npcId == 0 || !ActiveEnemyPuppetsByNpcId.TryGetValue(npcId, out var record)) return false;

            float now = Time.realtimeSinceStartup;

            // Phase 4.4.0-O2: refined authorization with root/child distinction.
            if (Plugin.Cfg.EnableHostAuthorizedIntentExecution.Value
                && _clientAuthorizedIntentByNpcId.TryGetValue(npcId, out var authWindow)
                && now <= authWindow.ExpiresAt)
            {
                bool isMeleeHit  = IsMeleeHitCombatSource(source);
                bool isRootCombat = IsRootCombatSource(source);
                bool isChildCombat = IsChildCombatSource(source);

                if (isMeleeHit)
                {
                    // HandleMeleeHit runs for visual animation; patch enters native-damage suppression.
                    _clientAuthorizedMeleeEvents++;
                    detail = $"authorized-melee idx={record.Snapshot.SpawnIndex} actor={record.Snapshot.ActorName} seq={authWindow.Sequence}";
                    if (Plugin.Cfg.LogHostAuthorizedIntentExecution.Value)
                        MaybeLogEnemyCombatProbe(npc, source, "authorized-melee", detail, force: false);
                    return false;
                }

                if (isRootCombat)
                {
                    // Root methods only pass when we explicitly initiated the replay.
                    // Spontaneous calls from the local AI are blocked to prevent duplicate attacks.
                    if (_clientAuthorizedCombatRootReplayDepth > 0)
                    {
                        _clientAuthorizedAttackPasses++;
                        detail = $"authorized-root idx={record.Snapshot.SpawnIndex} actor={record.Snapshot.ActorName} source={Clean(source)} seq={authWindow.Sequence}";
                        if (Plugin.Cfg.LogHostAuthorizedIntentExecution.Value)
                            MaybeLogEnemyCombatProbe(npc, source, "authorized-root", detail, force: false);
                        return false;
                    }
                    // Spontaneous root call — block it.
                    _clientBlockedSpontaneousCombatCalls++;
                    _clientEnemyPuppetCombatBlocks++;
                    detail = $"blocked-spontaneous idx={record.Snapshot.SpawnIndex} actor={record.Snapshot.ActorName} source={Clean(source)} seq={authWindow.Sequence}";
                    if (Plugin.Cfg.LogHostAuthorizedIntentExecution.Value)
                        MaybeLogEnemyCombatProbe(npc, source, "blocked-spontaneous", detail, force: false);
                    return true;
                }

                if (isChildCombat)
                {
                    // O3-B: child events only run after root replay so they have correct animation context.
                    // Exception: DoneAttacking / DoneShooting always pass to clean up state.
                    bool isDoneEvent = source.IndexOf("DoneAttacking", StringComparison.OrdinalIgnoreCase) >= 0
                                   || source.IndexOf("DoneShooting",  StringComparison.OrdinalIgnoreCase) >= 0;

                    if (authWindow.RootReplayed || isDoneEvent)
                    {
                        _clientAuthorizedChildAfterRoot++;
                        _clientAuthorizedChildPasses++;
                        detail = $"authorized-child-after-root idx={record.Snapshot.SpawnIndex} actor={record.Snapshot.ActorName} source={Clean(source)} seq={authWindow.Sequence} rootReplayed={authWindow.RootReplayed}";
                        if (Plugin.Cfg.LogHostAuthorizedIntentExecution.Value)
                            MaybeLogEnemyCombatProbe(npc, source, "authorized-child-after-root", detail, force: false);
                        return false;
                    }

                    // Child arrived before root replay — block and let root replay fire first.
                    _clientChildBlockedBeforeRootReplay++;
                    _clientEnemyPuppetCombatBlocks++;
                    detail = $"blocked-child-before-root idx={record.Snapshot.SpawnIndex} actor={record.Snapshot.ActorName} source={Clean(source)} seq={authWindow.Sequence}";
                    if (Plugin.Cfg.LogHostAuthorizedIntentExecution.Value)
                        MaybeLogEnemyCombatProbe(npc, source, "blocked-child-before-root", detail, force: false);
                    return true;
                }
            }

            // Legacy depth check: active during TryReplayClientHostCombatVisualAction (non-melee only).
            if ((_clientPuppetCombatVisualReplayDepth > 0 || _clientAuthorizedCombatRootReplayDepth > 0)
                && !IsMeleeHitCombatSource(source))
                return false;

            _clientUnauthorizedAttackBlocks++;
            _clientEnemyPuppetCombatBlocks++;
            ApplyClientEnemyTargetAuthority(record, now, "blocked combat " + Clean(source));
            detail = $"idx={record.Snapshot.SpawnIndex} actor={record.Snapshot.ActorName} source={Clean(source)}";
            if (!IsLowValueCombatProbeSource(source) || Plugin.Cfg.EnableDebugLog.Value)
                MaybeLogEnemyCombatProbe(npc, source, "blocked-client-puppet", detail, force: false);
            return true;
        }

        // Phase 4.4.0-O: native damage suppression depth — entered while HandleMeleeHit runs in
        // an authorized window so that Unit.ReceiveDamage is suppressed for the local Player.
        public static bool IsClientEnemyPuppetNpc(object? npc)
        {
            if (npc == null) return false;
            int npcId = ObjectIdentity(npc);
            return npcId != 0 && ActiveEnemyPuppetsByNpcId.ContainsKey(npcId);
        }

        /// <summary>
        /// Client-side aim override for a puppet enemy that is currently replaying a host combat
        /// action. The native Weapon.DispatchProjectile re-aims via Npc.GetAimPosition() using the
        /// puppet's LOCAL aimTarget (the target root = feet), which makes the native projectile fly
        /// at the floor. When a puppet has an active intent window carrying the host's authoritative
        /// aim (head/camera after TryBuildHostCombatAim), redirect the native aim there so the
        /// visual projectile flies toward the real target. Returns false for non-puppet NPCs and on
        /// the host, leaving GetAimPosition untouched.
        /// </summary>
        public static bool TryGetClientPuppetAimOverride(object? npc, out Vector3 aim)
        {
            aim = Vector3.zero;
            if (npc == null) return false;
            if (!Plugin.Cfg.EnableClientPuppetAimOverride.Value) return false;
            if (!IsClientEnemyPuppetModeEnabled()) return false;
            int npcId = ObjectIdentity(npc);
            if (npcId == 0) return false;
            if (!ActiveEnemyPuppetsByNpcId.ContainsKey(npcId)) return false;
            if (!_clientAuthorizedIntentByNpcId.TryGetValue(npcId, out var window) || window == null) return false;
            if (Time.realtimeSinceStartup > window.ExpiresAt) return false;
            if (!window.HasAimPosition || !IsFinite(window.AimPosition)) return false;
            aim = window.AimPosition;
            return true;
        }

        public static bool IsInClientEnemyNativeDamageSuppression() => _clientEnemyNativeDamageSuppressDepth > 0;

        public static void EnterClientEnemyNativeDamageSuppression()
        {
            _clientEnemyNativeDamageSuppressDepth++;
        }

        public static void ExitClientEnemyNativeDamageSuppression()
        {
            if (_clientEnemyNativeDamageSuppressDepth > 0) _clientEnemyNativeDamageSuppressDepth--;
        }

        public static void CountSuppressedNativeEnemyDamage()
        {
            _clientSuppressedNativeEnemyDamage++;
        }

        // Returns true when a NEW sequence was created/arrived (root replay should be triggered).
        private static bool TryCreateOrRefreshClientIntentWindow(int npcId, NetGameplayEnemyStateSnapshot hostSnapshot, float now)
        {
            if (!Plugin.Cfg.EnableHostAuthorizedIntentExecution.Value) return false;
            if (hostSnapshot == null) return false;

            bool hasIntent = hostSnapshot.HasEnemyIntent
                || (hostSnapshot.HasHostCombatAction && hostSnapshot.HostCombatActionKind != CombatActionNone);
            if (!hasIntent) return false;

            float windowSeconds = Plugin.Cfg.HostAuthorizedIntentWindowSeconds.Value;
            if (windowSeconds < 0.1f) windowSeconds = 0.1f;
            if (windowSeconds > 5f) windowSeconds = 5f;

            int kind;
            int weaponState;
            bool hasAim;
            Vector3 aim;
            Vector3 origin;

            if (hostSnapshot.HasEnemyIntent)
            {
                kind = hostSnapshot.EnemyIntentKind;
                weaponState = hostSnapshot.EnemyIntentWeaponActionState;
                hasAim = hostSnapshot.EnemyIntentHasAimPosition;
                aim = hostSnapshot.EnemyIntentAimPosition;
                origin = hostSnapshot.EnemyIntentHasOriginPosition ? hostSnapshot.EnemyIntentOriginPosition : Vector3.zero;
            }
            else
            {
                kind = HostCombatActionKindToEnemyIntentKind(hostSnapshot.HostCombatActionKind);
                weaponState = hostSnapshot.HostCombatActionState;
                hasAim = hostSnapshot.HasHostCombatAim;
                aim = hostSnapshot.HostCombatAimPosition;
                origin = hostSnapshot.HasHostCombatAim ? hostSnapshot.HostCombatOriginPosition : Vector3.zero;
            }

            int seq = hostSnapshot.HasHostCombatAction ? hostSnapshot.HostCombatActionSequence : hostSnapshot.Sequence;

            bool isNew = !_clientAuthorizedIntentByNpcId.TryGetValue(npcId, out var existing);
            bool isNewSeq = isNew || existing.Sequence != seq;

            if (!_clientAuthorizedIntentByNpcId.TryGetValue(npcId, out var window))
            {
                window = new ClientEnemyAuthorizedIntentWindow { NpcId = npcId };
                _clientAuthorizedIntentByNpcId[npcId] = window;
            }

            window.Sequence = seq;
            window.Kind = kind;
            // Only reset the expiry on a new sequence — same-sequence snapshots keep the existing window.
            // Without this guard, continuous snapshot ticks refresh ExpiresAt every frame, making the
            // window permanent and causing genericCombatMirror + rootReplay to be skipped indefinitely.
            if (isNewSeq) window.ExpiresAt = now + windowSeconds;
            window.WeaponActionState = weaponState;
            window.HasAimPosition = hasAim;
            window.AimPosition = aim;
            window.HasOriginPosition = hasAim;
            window.OriginPosition = origin;
            // O2: reset root-replay idempotency flag when sequence changes.
            if (isNewSeq) window.RootReplayed = false;

            if (isNewSeq)
            {
                _clientIntentWindows++;
                // Section VIII: log combat intent receipt so cliff-approach bugs can be diagnosed.
                if (Plugin.Cfg.LogHostAuthorizedIntentExecution.Value
                    && (kind == EnemyIntentKindAttackMelee || kind == EnemyIntentKindAttackRanged || kind == EnemyIntentKindWeaponAction))
                {
                    string kindName = kind == EnemyIntentKindAttackMelee ? "AttackMelee"
                                    : kind == EnemyIntentKindAttackRanged ? "AttackRanged" : "WeaponAction";
                    Vector3 npcPos = Vector3.zero;
                    bool hasNpcPos = ActiveEnemyPuppetsByNpcId.TryGetValue(npcId, out var logRec)
                                  && logRec.Snapshot != null && logRec.Snapshot.HasPosition;
                    if (hasNpcPos) npcPos = logRec!.Snapshot.Position;
                    Plugin.Log.Info($"[EnemyIntent] Client AuthCombat npcId={npcId} seq={seq} kind={kindName} controlMode=AuthorizedCombat setDestination=false reportLastSeen=false hostPos={(hasNpcPos ? $"({npcPos.x:F2},{npcPos.y:F2},{npcPos.z:F2})" : "?")} aimPos={(hasAim ? $"({aim.x:F2},{aim.y:F2},{aim.z:F2})" : "?")}");
                }
            }
            return isNewSeq;
        }

        // Phase 4.4.0-O3-B: authoritative root combat replay driven directly by combat intent window,
        // independent of the visual replay / EnemyAnimationMirrorReplayHostCombatMethods gate.
        private static void TryExecuteClientEnemyIntentRootReplay(
            EnemyPuppetRecord record,
            ClientEnemyAuthorizedIntentWindow window,
            object runtimeNpc,
            NetGameplayEnemyStateSnapshot? hostSnapshot)
        {
            _clientRootReplayAttempts++;

            if (record == null || window == null || runtimeNpc == null)
            {
                _clientRootReplayFailed++;
                if (Plugin.Cfg.LogHostAuthorizedIntentExecution.Value)
                    Plugin.Log.Info($"[EnemyIntent] Client root-replay skipped npcId=0 seq=? reason=no npc/window/runtime");
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (now > window.ExpiresAt)
            {
                _clientRootReplayFailed++;
                if (Plugin.Cfg.LogHostAuthorizedIntentExecution.Value)
                    Plugin.Log.Info($"[EnemyIntent] Client root-replay skipped npcId={record.NpcId} seq={window.Sequence} reason=stale window");
                return;
            }

            if (window.RootReplayed)
            {
                _clientRootReplaySkippedDuplicate++;
                return;
            }

            int kind = window.Kind;
            bool isRanged = kind == EnemyIntentKindAttackRanged;
            bool isMelee  = kind == EnemyIntentKindAttackMelee;
            bool isWeapon = kind == EnemyIntentKindWeaponAction;

            if (!isRanged && !isMelee && !isWeapon)
            {
                _clientRootReplayUnsupported++;
                if (Plugin.Cfg.LogHostAuthorizedIntentExecution.Value)
                    Plugin.Log.Info($"[EnemyIntent] Client root-replay skipped npcId={record.NpcId} seq={window.Sequence} reason=unsupported kind={kind}");
                return;
            }

            bool invoked = false;
            string method = "?";

            try
            {
                _clientAuthorizedCombatRootReplayDepth++;
                _clientPuppetCombatVisualReplayDepth++;

                // If Host sent an explicit CombatActionKind, use that for better method selection.
                int combatKind = (hostSnapshot != null && hostSnapshot.HasHostCombatAction)
                    ? hostSnapshot.HostCombatActionKind : CombatActionNone;

                if (combatKind == CombatActionTriggerWeapon)
                {
                    method = "TriggerWeaponManually";
                    invoked = TryInvokeInstanceMethod(runtimeNpc, method, window.WeaponActionState);
                }
                else if (combatKind == CombatActionShoot)
                {
                    method = "TriggerShoot";
                    invoked = TryInvokeInstanceMethod(runtimeNpc, method);
                }
                else if (combatKind == CombatActionAttackAnimation)
                {
                    method = "TriggerAttackAnimation";
                    invoked = TryInvokeInstanceMethod(runtimeNpc, method);
                }
                else if (combatKind == CombatActionSetShooting)
                {
                    // SetShooting is sticky; prefer one-shot TriggerShoot when available.
                    method = "TriggerShoot";
                    invoked = TryInvokeInstanceMethod(runtimeNpc, method);
                    if (!invoked)
                    {
                        method = "SetShooting";
                        invoked = TryInvokeInstanceMethod(runtimeNpc, method, true);
                    }
                }
                else if (isWeapon || isMelee)
                {
                    // Intent-only melee/weapon path.
                    method = "TriggerWeaponManually";
                    invoked = TryInvokeInstanceMethod(runtimeNpc, method, window.WeaponActionState);
                    if (!invoked)
                    {
                        method = "TriggerAttackAnimation";
                        invoked = TryInvokeInstanceMethod(runtimeNpc, method);
                    }
                }
                else
                {
                    // Intent-only ranged path: SetRangedAttacking → SetShooting → TriggerShoot
                    method = "SetRangedAttacking";
                    invoked = TryInvokeInstanceMethod(runtimeNpc, method, true);
                    if (!invoked)
                    {
                        method = "SetShooting";
                        invoked = TryInvokeInstanceMethod(runtimeNpc, method, true);
                    }
                    if (!invoked)
                    {
                        method = "TriggerShoot";
                        invoked = TryInvokeInstanceMethod(runtimeNpc, method);
                    }
                }
            }
            catch (Exception ex)
            {
                invoked = false;
                method = $"?exception:{ex.Message}";
            }
            finally
            {
                if (_clientAuthorizedCombatRootReplayDepth > 0) _clientAuthorizedCombatRootReplayDepth--;
                if (_clientPuppetCombatVisualReplayDepth > 0) _clientPuppetCombatVisualReplayDepth--;
            }

            if (!invoked)
            {
                _clientRootReplayFailed++;
                if (Plugin.Cfg.LogHostAuthorizedIntentExecution.Value)
                    Plugin.Log.Info($"[EnemyIntent] Client root-replay skipped npcId={record.NpcId} seq={window.Sequence} kind={kind} method={method} reason=missing method");
                return;
            }

            window.RootReplayed = true;
            _clientCombatRootReplays++;
            Plugin.Log.Info($"[EnemyIntent] Client root-replay npcId={record.NpcId} seq={window.Sequence} kind={kind} method={method} actor={record.Snapshot?.ActorName ?? "?"}");
            // Phase RT3-Cousin-arms: the Cousin arm's mud-ball visual is produced by its own animation-event throw
            // (CousinArmPatches.ThrowProjectile_Pre fixes up the target + zeroes damage on the client). The
            // SetRangedAttacking replay above plays that animation, so the throw stays aligned with the animation.
        }

        private static void PruneExpiredAuthorizedIntentWindows(float now)
        {
            if (_clientAuthorizedIntentByNpcId.Count == 0) return;
            foreach (var key in _clientAuthorizedIntentByNpcId.Keys.ToArray())
            {
                if (_clientAuthorizedIntentByNpcId.TryGetValue(key, out var w) && now > w.ExpiresAt)
                    _clientAuthorizedIntentByNpcId.Remove(key);
            }
        }

        public static void ReportProjectileProbe(object? sourceObject, string source, string stage)
        {
            if (!IsEnabled()) return;
            try
            {
                string role = NetConfig.GetMode().ToString();
                string key = $"{role}:{Clean(source)}:{Clean(stage)}:{ObjectIdentity(sourceObject)}";
                float now = Time.realtimeSinceStartup;
                float interval = Plugin.Cfg.EnableDebugLog.Value ? 0.15f : 1.0f;
                if (ProjectileProbeLastLogAtByKey.TryGetValue(key, out var last) && now - last < interval) return;
                ProjectileProbeLastLogAtByKey[key] = now;
                _projectileProbeEvents++;
                NetLogger.Info($"[ProjectileProbe] role={role} source={Clean(source)} stage={Clean(stage)} obj={(sourceObject == null ? "<static>" : DescribeTarget(sourceObject))}");
            }
            catch { }
        }

        public static void ReportEnemyAiIntent(object? sourceObject, string source, object[]? args)
        {
            if (!IsEnabled()) return;
            if (sourceObject == null) return;
            try
            {
                if (NetConfig.GetMode() != NetMode.Host) return;
                if (!Plugin.Cfg.EnableClientEnemyIntentDrivenMotion.Value) return;

                object? npc = ResolveMirroredEnemyObject(sourceObject, out _);
                if (npc == null) return;
                if (npc is UnityEngine.Object unityObject && unityObject == null) return;

                var snapshot = FindSnapshotForRuntimeObject(npc);
                if (snapshot == null || snapshot.IsDead) return;
                if (!string.Equals(snapshot.Category, "Npc", StringComparison.OrdinalIgnoreCase)) return;

                Vector3 destination;
                bool hasDestination = TryExtractIntentDestination(args, out destination);
                if (!hasDestination)
                    hasDestination = TryResolveCombatTargetPosition(npc, out destination);

                if (!hasDestination || !IsFinite(destination)) return;

                int npcId = ObjectIdentity(npc);
                if (npcId == 0) return;
                float now = Time.realtimeSinceStartup;

                var intent = new HostEnemyAiIntent
                {
                    ExpiresAt = now + 1.50f,
                    Source = Clean(source),
                    Sequence = ++_hostEnemyAiIntentMarks,
                    Kind = AiIntentKindDestination,
                    HasDestination = true,
                    Destination = destination,
                    HasLookAt = true,
                    LookAt = destination
                };

                HostEnemyAiIntentsByNpcId[npcId] = intent;

                if (Plugin.Cfg.LogEnemyAiIntentMirror.Value && Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[EnemyAiIntent] Host captured idx={snapshot.SpawnIndex} actor={snapshot.ActorName} source={Clean(source)} dest=({destination.x:F2},{destination.y:F2},{destination.z:F2}) seq={intent.Sequence}");
            }
            catch { }
        }

        private static bool TryExtractIntentDestination(object[]? args, out Vector3 destination)
        {
            destination = Vector3.zero;
            if (args == null) return false;
            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    object? arg = args[i];
                    if (arg == null) continue;
                    if (arg is Vector3 v && IsFinite(v))
                    {
                        destination = v;
                        return true;
                    }
                    if (TryGetPosition(arg, out var p) && IsFinite(p))
                    {
                        destination = p;
                        return true;
                    }
                    if (TryGetVector3Member(arg, "position", out var memberPos) && IsFinite(memberPos))
                    {
                        destination = memberPos;
                        return true;
                    }
                    if (TryGetVector3Member(arg, "Position", out memberPos) && IsFinite(memberPos))
                    {
                        destination = memberPos;
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        public static void ReportEnemyCombatProbe(object? npc, string source, string detail = "")
        {
            MarkHostEnemyCombatActionIfUseful(npc, source, detail);
            if (!IsEnemyCombatProbeEnabled()) return;
            if (IsLowValueCombatProbeSource(source) && !Plugin.Cfg.EnableDebugLog.Value) return;
            MaybeLogEnemyCombatProbe(npc, source, "observe", detail, force: false);
        }

        private static bool IsLowValueCombatProbeSource(string source)
        {
            return IsMeleeHitCombatSource(source);
        }

        private static bool IsMeleeHitCombatSource(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return false;
            return source.IndexOf("HandleMeleeHit", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsHostCombatActionSource(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return false;
            if (source.IndexOf("TriggerAttackAnimation", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (source.IndexOf("TriggerShoot", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (source.IndexOf("SetShooting", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (source.IndexOf("TriggerWeaponManually", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            // Phase 4.4.0-O extended sources.
            if (source.IndexOf("TriggerShootFromAnimation", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (source.IndexOf("StartMeleeDamageState", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (source.IndexOf("EndMeleeDamageState", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (source.IndexOf("SetRangedAttacking", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (source.IndexOf("SetAttacking", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (source.IndexOf("DoneAttacking", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (source.IndexOf("DoneShooting", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static bool IsAuthorizedCombatSource(string source)
        {
            // All IsHostCombatActionSource checks already include the O-version extended sources.
            return IsHostCombatActionSource(source);
        }

        // Phase 4.4.0-O2: root methods — the entry point that initiates an attack pipeline.
        // Only allowed when _clientAuthorizedCombatRootReplayDepth > 0 (i.e., we explicitly initiated it).
        private static bool IsRootCombatSource(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return false;
            if (source.IndexOf("TriggerWeaponManually", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (source.IndexOf("TriggerShoot", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (source.IndexOf("SetShooting", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (source.IndexOf("TriggerAttackAnimation", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // Phase 4.4.0-O2: child methods — called inside an active attack pipeline, always allowed in window.
        private static bool IsChildCombatSource(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return false;
            if (source.IndexOf("StartMeleeDamageState", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (source.IndexOf("EndMeleeDamageState", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (source.IndexOf("TriggerShootFromAnimation", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (source.IndexOf("DoneAttacking", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (source.IndexOf("DoneShooting", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (source.IndexOf("SetRangedAttacking", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (source.IndexOf("SetAttacking", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // Phase 4.4.0-O2: true when the NPC has an active auth window with a combat-kind intent.
        private static bool HasActiveAuthorizedCombatIntentWindow(int npcId, float now)
        {
            if (!_clientAuthorizedIntentByNpcId.TryGetValue(npcId, out var w)) return false;
            if (now > w.ExpiresAt) return false;
            return w.Kind == EnemyIntentKindAttackMelee
                || w.Kind == EnemyIntentKindAttackRanged
                || w.Kind == EnemyIntentKindWeaponAction;
        }

        private static void MarkHostEnemyCombatActionIfUseful(object? npc, string source, string detail)
        {
            if (npc == null) return;
            if (!IsHostCombatActionSource(source)) return;
            try
            {
                if (NetConfig.GetMode() != NetMode.Host) return;
                if (!Plugin.Cfg.EnableHostEnemyAnimationMirror.Value) return;

                int npcId = ObjectIdentity(npc);
                if (npcId == 0) return;

                float now = Time.realtimeSinceStartup;
                float hold = Plugin.Cfg.EnemyAnimationMirrorHostCombatActionHoldSeconds.Value;
                if (hold < 0.10f) hold = 0.10f;
                if (hold > 2.0f) hold = 2.0f;

                int kind = GetHostCombatActionKind(source);
                int state = kind == CombatActionTriggerWeapon ? ParseStateDetail(detail, 0) : 0;

                // F4-P1FIRE: Npc.TriggerWeaponManually's body immediately calls SetShooting, whose own report was
                // OVERWRITING the just-written TriggerWeapon mark (kind 4 state 1 → kind 3 state 0, since "state=True"
                // parses to 0) microseconds later. Clients then replayed a one-shot TriggerShoot instead of holding the
                // machine-gun trigger (Log316: Desert boss bursts never mirrored). A live TriggerWeapon mark wins over a
                // nested/redundant SetShooting mark; bare SetShooting users (no TriggerWeapon call) are unaffected.
                if (kind == CombatActionSetShooting
                    && HostEnemyCombatActionsByNpcId.TryGetValue(npcId, out var existingMark)
                    && existingMark.Kind == CombatActionTriggerWeapon
                    && now < existingMark.ExpiresAt)
                    return;

                bool hasAim = TryBuildHostCombatAim(npc, out var origin, out var aim);

                var action = new HostEnemyCombatAction
                {
                    ExpiresAt = now + hold,
                    Source = Clean(source),
                    Sequence = ++_hostEnemyCombatActionSequence,
                    Kind = kind,
                    State = state,
                    HasAim = hasAim,
                    OriginPosition = origin,
                    AimPosition = aim
                };
                HostEnemyCombatActionsByNpcId[npcId] = action;
                _hostEnemyCombatActionMarks++;
                TryApplyHostAuthoritativeEnemyDamage(npc, action, now);
                // Phase 5.0: broadcast reliable attack phase event to all clients.
                TryBroadcastHostAttackPhaseEvent(npc, action);
            }
            catch { }
        }

        private static void TryBroadcastHostAttackPhaseEvent(object npc, HostEnemyCombatAction action)
        {
            try
            {
                if (!Plugin.Cfg.EnableHostAttackPhaseEvents.Value) return;
                if (!Plugin.Cfg.EnableHostDrivenEnemyProxy.Value) return;

                var snapshot = FindSnapshotForRuntimeObject(npc);
                if (snapshot == null) return;
                // Only send for CombatEnemy entities.
                if (snapshot.SyncCategory != SyncCatCombatEnemy) return;
                // (3) Throttle attack-animation events per enemy — a machine-gun enemy doesn't need every shot's animation
                // synced. Intermediate events are dropped (cosmetic). Applies to ALL enemies uniformly (no target check).
                if (Plugin.Cfg.EnableCombatEventCoalescing.Value
                    && ThrottlePerEntity(_attackPhaseLastSentBySpawnIdx, snapshot.SpawnIndex, Plugin.Cfg.AttackPhaseEventMinIntervalSeconds.Value, Time.realtimeSinceStartup))
                { _attackPhaseThrottled++; return; }

                if (!NetRunStateBridge.TryGetLocalRunState(out var state)) return;
                if (!state.HasLevel) return;

                int attackPhase = CombatActionKindToAttackPhase(action.Kind);
                int attackKind  = CombatActionKindToAttackKind(action.Kind);

                var evt = new NetHostAttackPhaseEvent
                {
                    ChapterName    = state.ChapterName,
                    LevelIndex     = state.LevelIndex,
                    HasLevelSeed   = state.HasLevelSeed,
                    LevelSeed      = state.LevelSeed,
                    HostSpawnIndex = snapshot.SpawnIndex,
                    UnitIdentifier = snapshot.EntityId.UnitIdentifier ?? "",
                    AttackPhase    = attackPhase,
                    AttackKind     = attackKind,
                    ActionKind     = action.Kind,
                    ActionState    = action.State,
                    Sequence       = action.Sequence,
                    HasAimData     = action.HasAim,
                    OriginPosition = action.OriginPosition,
                    AimPosition    = action.AimPosition,
                    SentAt         = Time.realtimeSinceStartup,
                };

                // Populate animator hint from the entity's cached animator if available.
                if (snapshot.TryGetRuntimeObject(out var runtimeObj) && runtimeObj != null)
                    TryPopulateAttackPhaseAnimatorHint(runtimeObj, evt);

                NetGameplaySyncBridge.ReportHostAttackPhaseEvent(evt);
                _hostAttackPhaseEventsSent++;

                if (Plugin.Cfg.LogHostAttackPhaseEvents.Value)
                    Plugin.Log.Info($"[AttackPhase] Host broadcast idx={snapshot.SpawnIndex} actor={snapshot.ActorName} phase={attackPhase} kind={attackKind} actionKind={action.Kind} seq={action.Sequence} hasAnim={evt.HasAnimatorHint}");
            }
            catch { }
        }

        private static int CombatActionKindToAttackPhase(int kind)
        {
            switch (kind)
            {
                case CombatActionAttackAnimation:
                case CombatActionSetAttacking:
                case CombatActionSetRangedAttacking:
                case CombatActionShoot:
                case CombatActionSetShooting:
                case CombatActionTriggerShootFromAnimation:
                case CombatActionTriggerWeapon:
                    return NetHostAttackPhaseEvent.PhaseWindup;
                case CombatActionStartMeleeDamage:
                    return NetHostAttackPhaseEvent.PhaseActive;
                case CombatActionEndMeleeDamage:
                    return NetHostAttackPhaseEvent.PhaseRecovery;
                case CombatActionDoneAttacking:
                case CombatActionDoneShooting:
                    return NetHostAttackPhaseEvent.PhaseNone;
                default:
                    return NetHostAttackPhaseEvent.PhaseNone;
            }
        }

        private static int CombatActionKindToAttackKind(int kind)
        {
            switch (kind)
            {
                case CombatActionAttackAnimation:
                case CombatActionStartMeleeDamage:
                case CombatActionEndMeleeDamage:
                case CombatActionSetAttacking:
                case CombatActionDoneAttacking:
                    return NetHostAttackPhaseEvent.KindMelee;
                case CombatActionShoot:
                case CombatActionSetShooting:
                case CombatActionTriggerShootFromAnimation:
                case CombatActionSetRangedAttacking:
                case CombatActionDoneShooting:
                    return NetHostAttackPhaseEvent.KindRanged;
                case CombatActionTriggerWeapon:
                    return NetHostAttackPhaseEvent.KindWeaponAction;
                default:
                    return NetHostAttackPhaseEvent.KindNone;
            }
        }

        private static void TryPopulateAttackPhaseAnimatorHint(object runtimeObj, NetHostAttackPhaseEvent evt)
        {
            try
            {
                // Walk the object hierarchy looking for an Animator component.
                Animator? animator = null;
                if (runtimeObj is Component component)
                    animator = component.GetComponentInChildren<Animator>();
                else if (runtimeObj is GameObject go)
                    animator = go.GetComponentInChildren<Animator>();

                if (animator == null || !animator.isActiveAndEnabled) return;
                if (animator.layerCount == 0) return;

                var info = animator.GetCurrentAnimatorStateInfo(0);
                if (info.fullPathHash == 0) return;

                evt.HasAnimatorHint        = true;
                evt.AnimatorFullPathHash   = info.fullPathHash;
                evt.AnimatorNormalizedTime = info.normalizedTime;
            }
            catch { }
        }

        private static int GetHostCombatActionKind(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return CombatActionNone;
            if (source.IndexOf("TriggerWeaponManually", StringComparison.OrdinalIgnoreCase) >= 0) return CombatActionTriggerWeapon;
            if (source.IndexOf("TriggerShootFromAnimation", StringComparison.OrdinalIgnoreCase) >= 0) return CombatActionTriggerShootFromAnimation;
            if (source.IndexOf("TriggerShoot", StringComparison.OrdinalIgnoreCase) >= 0) return CombatActionShoot;
            if (source.IndexOf("SetShooting", StringComparison.OrdinalIgnoreCase) >= 0) return CombatActionSetShooting;
            if (source.IndexOf("TriggerAttackAnimation", StringComparison.OrdinalIgnoreCase) >= 0) return CombatActionAttackAnimation;
            if (source.IndexOf("StartMeleeDamageState", StringComparison.OrdinalIgnoreCase) >= 0) return CombatActionStartMeleeDamage;
            if (source.IndexOf("EndMeleeDamageState", StringComparison.OrdinalIgnoreCase) >= 0) return CombatActionEndMeleeDamage;
            if (source.IndexOf("SetRangedAttacking", StringComparison.OrdinalIgnoreCase) >= 0) return CombatActionSetRangedAttacking;
            if (source.IndexOf("SetAttacking", StringComparison.OrdinalIgnoreCase) >= 0) return CombatActionSetAttacking;
            if (source.IndexOf("DoneAttacking", StringComparison.OrdinalIgnoreCase) >= 0) return CombatActionDoneAttacking;
            if (source.IndexOf("DoneShooting", StringComparison.OrdinalIgnoreCase) >= 0) return CombatActionDoneShooting;
            return CombatActionNone;
        }

        private static int HostCombatActionKindToEnemyIntentKind(int combatActionKind)
        {
            switch (combatActionKind)
            {
                case CombatActionTriggerWeapon:
                case CombatActionAttackAnimation:
                case CombatActionStartMeleeDamage:
                case CombatActionEndMeleeDamage:
                case CombatActionSetAttacking:
                case CombatActionDoneAttacking:
                    return EnemyIntentKindAttackMelee;
                case CombatActionShoot:
                case CombatActionSetShooting:
                case CombatActionTriggerShootFromAnimation:
                case CombatActionSetRangedAttacking:
                case CombatActionDoneShooting:
                    return EnemyIntentKindAttackRanged;
                default:
                    return EnemyIntentKindNone;
            }
        }

        private static int ParseStateDetail(string detail, int fallback)
        {
            if (string.IsNullOrWhiteSpace(detail)) return fallback;
            try
            {
                const string token = "state=";
                int start = detail.IndexOf(token, StringComparison.OrdinalIgnoreCase);
                if (start < 0) return fallback;
                start += token.Length;
                int end = start;
                while (end < detail.Length && (char.IsDigit(detail[end]) || detail[end] == '-' || detail[end] == '+'))
                    end++;
                if (end <= start) return fallback;
                if (int.TryParse(detail.Substring(start, end - start), out var value))
                    return value;
            }
            catch { }
            return fallback;
        }

        private static bool TryBuildHostCombatAim(object npc, out Vector3 origin, out Vector3 aim)
        {
            origin = Vector3.zero;
            aim = Vector3.zero;
            try
            {
                // Origin: prefer the weapon's real barrel/muzzle transform (exact spawn point), then
                // fall back to a chest-height approximation above the NPC root.
                bool haveOrigin = TryResolveHostCombatBarrelOrigin(npc, out origin) && IsFinite(origin);
                if (!haveOrigin)
                {
                    if (!TryExtractWorldPosition(npc, out origin))
                        return false;
                    origin += Vector3.up * 0.85f;
                }

                // Aim: prefer the NPC's real GetAimPosition() — this is exactly what the host weapon
                // fires at. For a player target it resolves to the player's camera/head; for non-player
                // targets it is root + up. The legacy resolver below extracted the target ROOT transform
                // (the feet), which is why the client visual projectile aimed at the floor.
                bool haveAim = TryInvokeVector3Method(npc, "GetAimPosition", out aim) && IsFinite(aim);
                if (!haveAim)
                {
                    if (!TryResolveCombatTargetPosition(npc, out aim))
                    {
                        if (TryGetTransform(npc, out var transform) && transform != null)
                            aim = origin + transform.forward * 8f;
                        else
                            return false;
                    }
                }

                if (!IsFinite(origin) || !IsFinite(aim)) return false;
                if ((aim - origin).sqrMagnitude < 0.05f) return false;
                return true;
            }
            catch
            {
                origin = Vector3.zero;
                aim = Vector3.zero;
                return false;
            }
        }

        // Resolve the real muzzle/barrel world position the host weapon fires from, so the client
        // visual projectile starts at the gun rather than a guessed chest point.
        private static bool TryResolveHostCombatBarrelOrigin(object npc, out Vector3 origin)
        {
            origin = Vector3.zero;
            try
            {
                object? weapon = TryFindLikelyWeaponObject(npc);
                if (weapon == null) return false;
                // Weapon.BarrelTransform is a property (honours barrel overrides); fall back to the field.
                object? barrel = TryGetMemberValue(weapon, "BarrelTransform") ?? TryGetMemberValue(weapon, "barrelTransform");
                if (barrel is Transform t && t != null)
                {
                    origin = t.position;
                    return IsFinite(origin);
                }
            }
            catch { }
            return false;
        }

        // Phase 5.4-C G (host): record who/where the Host AI is currently targeting. Reuses the existing combat
        // target-position resolver. Kind is best-effort: today the Host AI only engages the Host player, so a
        // resolved target is reported as HostPlayer; this is the data the Client uses to stop self-targeting.
        private static void TryPopulateHostTargetIdentity(object npc, NetGameplayEnemyStateSnapshot snapshot)
        {
            try
            {
                if (npc == null || snapshot == null) return;
                if (!TryResolveCombatTargetPosition(npc, out var targetPos)) return;
                if (!IsFinite(targetPos)) return;
                snapshot.HasHostTarget = true;
                snapshot.HostTargetKind = 1; // HostPlayer (only target the host AI currently selects)
                snapshot.HostTargetPosition = targetPos;
            }
            catch { }
        }

        private static bool TryResolveCombatTargetPosition(object npc, out Vector3 position)
        {
            position = Vector3.zero;
            try
            {
                if (TryResolveCombatTargetPositionFromObject(npc, out position)) return true;

                object? aiAgent = TryGetMemberValue(npc, "AiAgent") ?? TryGetMemberValue(npc, "aiAgent");
                if (TryResolveCombatTargetPositionFromObject(aiAgent, out position)) return true;

                object? movementDriver = aiAgent == null ? null : TryGetMemberValue(aiAgent, "ai");
                if (TryResolveCombatTargetPositionFromObject(movementDriver, out position)) return true;

                object? weapon = TryFindLikelyWeaponObject(npc);
                if (TryResolveCombatTargetPositionFromObject(weapon, out position)) return true;

                object? behaviourTree = TryGetMemberValue(npc, "behaviourTree");
                if (TryResolveCombatTargetPositionFromObject(behaviourTree, out position)) return true;

                object? blackboard = TryGetMemberValue(npc, "blackboard")
                    ?? TryGetMemberValue(npc, "Blackboard")
                    ?? TryGetMemberValue(aiAgent, "blackboard")
                    ?? TryGetMemberValue(aiAgent, "Blackboard")
                    ?? TryGetMemberValue(behaviourTree, "blackboard")
                    ?? TryGetMemberValue(behaviourTree, "Blackboard");
                if (TryResolveCombatTargetPositionFromObject(blackboard, out position)) return true;
            }
            catch { }
            position = Vector3.zero;
            return false;
        }

        private static bool TryResolveCombatTargetPositionFromObject(object? container, out Vector3 position)
        {
            position = Vector3.zero;
            if (container == null) return false;
            try
            {
                foreach (string memberName in TargetAuthorityMemberNames)
                {
                    object? value = TryGetMemberValue(container, memberName);
                    if (value == null) continue;
                    if (TryExtractWorldPosition(value, out position)) return true;
                }

                string[] extraNames = { "aimTarget", "AimTarget", "attackTarget", "AttackTarget", "targetUnit", "TargetUnit", "targetTransform", "TargetTransform", "lastTarget", "LastTarget", "targetPosition", "TargetPosition", "targetPos", "TargetPos", "attackingPosition", "AttackingPosition" };
                for (int i = 0; i < extraNames.Length; i++)
                {
                    object? value = TryGetMemberValue(container, extraNames[i]);
                    if (value == null) continue;
                    if (TryExtractWorldPosition(value, out position)) return true;
                }
            }
            catch { }
            position = Vector3.zero;
            return false;
        }

        private static bool TryExtractWorldPosition(object? value, out Vector3 position)
        {
            position = Vector3.zero;
            if (value == null) return false;
            try
            {
                if (value is Vector3 vector)
                {
                    if (!IsFinite(vector)) return false;
                    position = vector;
                    return true;
                }

                if (value is Transform transform && transform != null)
                {
                    position = transform.position;
                    return IsFinite(position);
                }

                if (value is Component component && component != null)
                {
                    position = component.transform.position;
                    return IsFinite(position);
                }

                if (value is GameObject gameObject && gameObject != null)
                {
                    position = gameObject.transform.position;
                    return IsFinite(position);
                }

                if (TryGetTransform(value, out var extractedTransform) && extractedTransform != null)
                {
                    position = extractedTransform.position;
                    return IsFinite(position);
                }

                object? posValue = TryGetMemberValue(value, "position") ?? TryGetMemberValue(value, "Position");
                if (posValue is Vector3 memberPosition && IsFinite(memberPosition))
                {
                    position = memberPosition;
                    return true;
                }
            }
            catch { }
            position = Vector3.zero;
            return false;
        }

        private static bool TryGetActiveHostEnemyCombatAction(object? npc, float now, out HostEnemyCombatAction action)
        {
            action = null!;
            if (npc == null) return false;
            int npcId = ObjectIdentity(npc);
            if (npcId == 0) return false;
            if (!HostEnemyCombatActionsByNpcId.TryGetValue(npcId, out action)) return false;
            if (now <= action.ExpiresAt) return true;
            HostEnemyCombatActionsByNpcId.Remove(npcId);
            action = null!;
            return false;
        }

        private static void PruneExpiredHostEnemyCombatActions()
        {
            if (HostEnemyCombatActionsByNpcId.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            var remove = new List<int>();
            foreach (var item in HostEnemyCombatActionsByNpcId)
            {
                if (now > item.Value.ExpiresAt)
                    remove.Add(item.Key);
            }
            for (int i = 0; i < remove.Count; i++)
                HostEnemyCombatActionsByNpcId.Remove(remove[i]);
        }

        private static bool TryGetActiveHostEnemyAiIntent(object? npc, float now, out HostEnemyAiIntent intent)
        {
            intent = null!;
            if (npc == null) return false;
            int npcId = ObjectIdentity(npc);
            if (npcId == 0) return false;
            if (!HostEnemyAiIntentsByNpcId.TryGetValue(npcId, out intent)) return false;
            if (now <= intent.ExpiresAt) return true;
            HostEnemyAiIntentsByNpcId.Remove(npcId);
            intent = null!;
            return false;
        }

        private static void PruneExpiredHostEnemyAiIntents()
        {
            if (HostEnemyAiIntentsByNpcId.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            var remove = new List<int>();
            foreach (var item in HostEnemyAiIntentsByNpcId)
            {
                if (now > item.Value.ExpiresAt)
                    remove.Add(item.Key);
            }
            for (int i = 0; i < remove.Count; i++)
                HostEnemyAiIntentsByNpcId.Remove(remove[i]);
        }

        private static bool IsClientEnemyIntentDrivenMotionEnabled()
        {
            try
            {
                return Plugin.Cfg.EnableClientEnemyIntentDrivenMotion.Value && IsClientEnemyPuppetModeEnabled();
            }
            catch { return false; }
        }

        private static bool IsHostOnlyEnemyTargetAuthorityEnabled()
        {
            try
            {
                return Plugin.Cfg.EnableHostOnlyEnemyTargetAuthority.Value
                    && IsClientEnemyPuppetModeEnabled();
            }
            catch { return false; }
        }

        private static bool IsEnemyCombatProbeEnabled()
        {
            try
            {
                return Plugin.Cfg.EnableEnemyCombatProbe.Value;
            }
            catch { return false; }
        }

        private static bool TryGetPuppetRecordForAiAgent(object? aiAgent, out EnemyPuppetRecord record)
        {
            record = null!;
            if (aiAgent == null) return false;
            int aiId = ObjectIdentity(aiAgent);
            if (aiId == 0) return false;
            foreach (var item in ActiveEnemyPuppets.Values)
            {
                if (item.AiAgentId == aiId || ReferenceEquals(item.AiAgent, aiAgent))
                {
                    record = item;
                    return true;
                }
            }
            return false;
        }

        private static void ApplyClientEnemyTargetAuthority(EnemyPuppetRecord record, float now, string reason)
        {
            if (!IsHostOnlyEnemyTargetAuthorityEnabled()) return;
            if (record == null) return;
            if (record.Npc == null && record.AiAgent == null && record.MovementDriver == null) return;

            if (record.LastTargetAuthorityApplyAt > 0f && now - record.LastTargetAuthorityApplyAt < 0.50f)
                return;

            bool boss = LooksLikeBoss(record.Snapshot?.ActorName) || LooksLikeBoss(record.Snapshot?.EntityId.TypeName);

            int cleared = 0;
            var clearedOn = new List<string>(4);

            _clientPuppetInternalTargetClearDepth++;
            try
            {
                cleared += TryClearKnownTargetMembers(record.Npc, "npc", clearedOn);
                cleared += TryClearKnownTargetMembers(record.AiAgent, "aiAgent", clearedOn);
                cleared += TryClearKnownTargetMembers(record.MovementDriver, "movement", clearedOn);

                object? behaviourTree = TryGetMemberValue(record.Npc, "behaviourTree");
                cleared += TryClearKnownTargetMembers(behaviourTree, "behaviourTree", clearedOn);

                object? blackboard = TryGetMemberValue(record.Npc, "blackboard")
                    ?? TryGetMemberValue(record.Npc, "Blackboard")
                    ?? TryGetMemberValue(record.AiAgent, "blackboard")
                    ?? TryGetMemberValue(record.AiAgent, "Blackboard")
                    ?? TryGetMemberValue(behaviourTree, "blackboard")
                    ?? TryGetMemberValue(behaviourTree, "Blackboard");
                cleared += TryClearKnownTargetMembers(blackboard, "blackboard", clearedOn);

                // Phase 5.4-C F: bosses (e.g. ShavwaEmperorWorm) are multi-section — their per-section AiAgents /
                // brains live on CHILD GameObjects and keep their own target, so clearing only the root leaves the
                // section AI fighting the host snapshot. Walk AI-like child components and clear their targets too.
                // Only done for bosses, or when the root clear found nothing, so normal enemies skip the child scan.
                if ((boss || cleared == 0) && !record.TargetAuthoritySectionScanDisabled)
                    cleared += TryClearBossSectionTargets(record, clearedOn);
            }
            finally
            {
                if (_clientPuppetInternalTargetClearDepth > 0)
                    _clientPuppetInternalTargetClearDepth--;
            }

            record.LastTargetAuthorityApplyAt = now;
            if (cleared > 0)
            {
                _clientEnemyPuppetTargetClears += cleared;
                _enemyTargetLocalCleared += cleared;
            }

            if (boss)
            {
                if (cleared > 0) _enemyTargetBossSuppressionApplied++;
                else _enemyTargetSuppressionFailed++;
            }
            if (cleared == 0)
            {
                _enemyTargetNoKnownMember++;
                // EMP-1b: after 6 consecutive barren applies (~3s), stop the expensive child-scan for this record.
                // Safe because it provably clears nothing; root clears above still run every cycle.
                if (!record.TargetAuthoritySectionScanDisabled && ++record.TargetAuthorityBarrenStreak >= 6)
                {
                    record.TargetAuthoritySectionScanDisabled = true;
                    // EMP-1b: also fast-path this AiAgent out of the block hot path (skip lookup/apply/log next frame on).
                    if (record.AiAgentId != 0) BarrenTargetAuthorityAiIds.Add(record.AiAgentId);
                    MaybeLogClientEnemyTargetAuthority(record, now, "section-scan-disabled",
                        $"boss={boss} reason=barren-6x (no clearable target members; skipping per-frame child scan + block-path apply/log)", force: true);
                }
            }
            else
            {
                record.TargetAuthorityBarrenStreak = 0;
            }

            // Phase 5.4-C E: when we found no target member (or this is a boss), dump the real type/members once
            // so the actual target fields can be discovered instead of guessed.
            MaybeDumpEnemyTargetMembers(record, noKnownMembers: cleared == 0, boss: boss);

            string summary = clearedOn.Count == 0 ? "no-known-target-members" : string.Join(",", clearedOn.Take(8).ToArray());
            record.LastTargetAuthorityClearedCount = cleared;
            record.LastTargetAuthoritySummary = summary;
            // Once a record's child-scan is disabled (barren) its result never changes — stop the per-apply logging
            // to cut both I/O and the string allocation on the hot path.
            if (!record.TargetAuthoritySectionScanDisabled)
            {
                MaybeLogClientEnemyTargetAuthority(record, now, "apply-host-only-target", $"reason={Clean(reason)} cleared={cleared} boss={boss} members={summary}", force: false);
                if (cleared == 0)
                    MaybeLogClientEnemyTargetAuthority(record, now, "suppression-failed", $"reason=no-target-member boss={boss}", force: false);
            }

            // Phase 5.4-C G: surface the host-authoritative target the Client should defer to (data pipe for the
            // next phase's "enemies attack all players"). The Client does not yet re-assign the target object;
            // it suppresses local selection above and reports what the Host says here.
            var hostSnap = record.LastHostSnapshot;
            if (hostSnap != null && hostSnap.HasHostTarget)
            {
                _enemyTargetHostTargetApplied++;
                string kind = hostSnap.HostTargetKind == 1 ? "HostPlayer"
                    : hostSnap.HostTargetKind == 2 ? "RemotePlayer"
                    : hostSnap.HostTargetKind == 0 ? "None" : "Unknown";
                MaybeLogClientEnemyTargetAuthority(record, now, "client applied host target",
                    $"targetKind={kind} targetPos=({hostSnap.HostTargetPosition.x:F1},{hostSnap.HostTargetPosition.y:F1},{hostSnap.HostTargetPosition.z:F1})", force: false);
            }
        }

        // Phase 5.4-C: clear target members on AI-like components living on the boss's child sections.
        private static int TryClearBossSectionTargets(EnemyPuppetRecord record, List<string> clearedOn)
        {
            GameObject? root = AsGameObject(record.Npc) ?? AsGameObject(record.AiAgent) ?? AsGameObject(record.MovementDriver);
            if (root == null) return 0;

            int cleared = 0;
            int scanned = 0;
            try
            {
                var components = root.GetComponentsInChildren<Component>(true);
                foreach (var comp in components)
                {
                    if (comp == null) continue;
                    string tn = comp.GetType().Name;
                    // Only AI / target-holder-like components; skip transforms/renderers/colliders/animators etc.
                    if (!LooksLikeAiOrTargetHolder(tn)) continue;
                    if (++scanned > 48) break; // hard cap so a huge boss prefab cannot stall the tick
                    cleared += TryClearKnownTargetMembers(comp, "section." + tn, clearedOn);
                }
            }
            catch { }
            return cleared;
        }

        private static bool LooksLikeAiOrTargetHolder(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return false;
            string t = typeName.ToLowerInvariant();
            return t.Contains("ai") || t.Contains("brain") || t.Contains("section") || t.Contains("worm")
                || t.Contains("combat") || t.Contains("enemy") || t.Contains("boss") || t.Contains("target")
                || t.Contains("aggro") || t.Contains("attack") || t.Contains("npc");
        }

        private static bool LooksLikeBoss(string? name)
        {
            string n = (name ?? string.Empty).ToLowerInvariant();
            if (n.Length == 0) return false;
            return n.Contains("boss") || n.Contains("emperor") || n.Contains("worm") || n.Contains("shavwa")
                || n.Contains("section") || n.Contains("colossus") || n.Contains("giant") || n.Contains("titan");
        }

        private static GameObject? AsGameObject(object? obj)
        {
            try
            {
                if (obj is GameObject go) return go != null ? go : null;
                if (obj is Component c) return c != null ? c.gameObject : null;
            }
            catch { }
            return null;
        }

        // Phase 5.4-C E: throttled, once-per-type reflection dump of a boss/unknown enemy's real target members.
        private static void MaybeDumpEnemyTargetMembers(EnemyPuppetRecord record, bool noKnownMembers, bool boss)
        {
            if (!Plugin.Cfg.LogEnemyTargetAuthority.Value) return;
            if (!boss && !noKnownMembers) return;

            object? primary = record.Npc ?? record.AiAgent ?? record.MovementDriver;
            if (primary == null) return;

            string typeName = primary.GetType().FullName ?? primary.GetType().Name;
            if (!_targetProbeDumpedTypes.Add(typeName)) return; // once per type

            _enemyTargetBossMembersDiscovered++;
            string actor = record.Snapshot?.ActorName ?? "<unknown>";
            NetLogger.Info($"[EnemyTargetAuthorityProbe] type={typeName} actor={actor} boss={boss} noKnownMembers={noKnownMembers}");
            DumpCandidateTargetMembers(primary, "primary");

            // Boss section children: list the AI-like component types + their candidate target members.
            GameObject? root = AsGameObject(primary);
            if (root != null)
            {
                try
                {
                    var seen = new HashSet<string>();
                    int listed = 0;
                    foreach (var comp in root.GetComponentsInChildren<Component>(true))
                    {
                        if (comp == null) continue;
                        string ctn = comp.GetType().Name;
                        if (!LooksLikeAiOrTargetHolder(ctn)) continue;
                        if (!seen.Add(comp.GetType().FullName ?? ctn)) continue;
                        if (++listed > 16) break;
                        NetLogger.Info($"[EnemyTargetAuthorityProbe] boss section root={root.name} component={comp.GetType().FullName} obj={comp.gameObject.name}");
                        DumpCandidateTargetMembers(comp, "section." + ctn);
                    }
                }
                catch { }
            }
        }

        private static void DumpCandidateTargetMembers(object obj, string label)
        {
            try
            {
                var t = obj.GetType();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var hits = new List<string>();
                for (Type? cur = t; cur != null && cur != typeof(object) && cur != typeof(UnityEngine.Object) && hits.Count < 24; cur = cur.BaseType)
                {
                    foreach (var f in cur.GetFields(flags | BindingFlags.DeclaredOnly))
                    {
                        if (LooksLikeTargetMemberName(f.Name) && hits.Count < 24)
                            hits.Add($"field {f.Name}:{f.FieldType.Name}");
                    }
                    foreach (var p in cur.GetProperties(flags | BindingFlags.DeclaredOnly))
                    {
                        if (p.GetIndexParameters().Length == 0 && LooksLikeTargetMemberName(p.Name) && hits.Count < 24)
                            hits.Add($"prop {p.Name}:{p.PropertyType.Name}");
                    }
                }
                NetLogger.Info($"[EnemyTargetAuthorityProbe] {label} type={t.Name} candidateTargetMembers={(hits.Count == 0 ? "none" : string.Join(", ", hits))}");
            }
            catch (Exception ex) { if (Plugin.Cfg.EnableDebugLog.Value) Plugin.Log.Debug($"[EnemyTargetAuthorityProbe] dump failed: {ex.Message}"); }
        }

        private static bool LooksLikeTargetMemberName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            return n.Contains("target") || n.Contains("aim") || n.Contains("focus") || n.Contains("aggro")
                || n.Contains("player") || n.Contains("brain") || n.Contains("blackboard") || n.Contains("threat")
                || n.Contains("victim") || n.Contains("prey") || n.Contains("enemy") || n.Contains("hostile");
        }

        private static int TryClearKnownTargetMembers(object? target, string label, List<string> clearedOn)
        {
            if (target == null) return 0;
            try
            {
                if (target is UnityEngine.Object unityObject && unityObject == null) return 0;
            }
            catch { return 0; }

            int cleared = 0;
            foreach (string memberName in TargetAuthorityMemberNames)
            {
                if (TryGetMemberValue(target, memberName) == null) continue;
                if (TrySetObjectMember(target, memberName, null))
                {
                    cleared++;
                    clearedOn.Add(label + "." + memberName);
                }
            }
            return cleared;
        }

        private static readonly string[] TargetAuthorityMemberNames = new[]
        {
            "OverrideTarget", "overrideTarget",
            "targetUnit", "TargetUnit",
            "target", "Target",
            "currentTarget", "CurrentTarget",
            "aimTarget", "AimTarget",
            "targetTransform", "TargetTransform",
            "targetObject", "TargetObject",
            "playerTarget", "PlayerTarget",
            "lastTarget", "LastTarget",
            "attackTarget", "AttackTarget",
            "shootTarget", "ShootTarget"
        };

        private static void MaybeLogClientEnemyTargetAuthority(EnemyPuppetRecord record, float now, string action, string detail, bool force)
        {
            if (!Plugin.Cfg.LogEnemyTargetAuthority.Value && !force) return;
            float interval = GetTargetAuthorityProbeInterval();
            if (!force && record.LastTargetAuthorityLogAt > 0f && now - record.LastTargetAuthorityLogAt < interval) return;
            record.LastTargetAuthorityLogAt = now;
            NetLogger.Info($"[EnemyTargetAuthority] mode=HostOnly action={Clean(action)} idx={record.Snapshot.SpawnIndex} actor={record.Snapshot.ActorName} {Clean(detail)}");
        }

        private static void MaybeLogEnemyCombatProbe(object? npc, string source, string action, string detail, bool force)
        {
            if (!Plugin.Cfg.LogEnemyCombatProbe.Value && !force) return;

            float now = Time.realtimeSinceStartup;
            int id = ObjectIdentity(npc);
            if (id == 0) id = Clean(source).GetHashCode();
            float interval = GetTargetAuthorityProbeInterval();
            if (!force && EnemyCombatProbeLastLogAtById.TryGetValue(id, out var last) && now - last < interval) return;
            EnemyCombatProbeLastLogAtById[id] = now;
            _enemyCombatProbeEvents++;

            string role = SafeNetModeText();
            string actor = "<unknown>";
            int spawnIndex = 0;
            if (npc != null)
            {
                var snapshot = FindSnapshotForRuntimeObject(npc);
                if (snapshot != null)
                {
                    actor = snapshot.ActorName;
                    spawnIndex = snapshot.SpawnIndex;
                }
                else
                {
                    actor = Clean(GetActorName(npc));
                }
            }

            NetLogger.Info($"[EnemyCombatProbe] role={role} action={Clean(action)} source={Clean(source)} idx={spawnIndex} actor={actor} {Clean(detail)}");
        }

        private static float GetTargetAuthorityProbeInterval()
        {
            try
            {
                float value = Plugin.Cfg.EnemyTargetAuthorityProbeIntervalSeconds.Value;
                if (value < 0.25f) value = 0.25f;
                if (value > 30f) value = 30f;
                return value;
            }
            catch { return 2f; }
        }

        private static string DescribeTarget(object? target)
        {
            if (target == null) return "null";
            try
            {
                if (target is UnityEngine.Object unityObject && unityObject == null) return "destroyed";
                string type = target.GetType().Name;
                string name = target is UnityEngine.Object uo && !string.IsNullOrWhiteSpace(uo.name)
                    ? Clean(uo.name)
                    : Clean(TryGetMemberValue(target, "name")?.ToString() ?? TryGetMemberValue(target, "Name")?.ToString());
                return $"{type}/{name}/id={ObjectIdentity(target)}";
            }
            catch { return "<target-error>"; }
        }

        private static string SafeNetModeText()
        {
            try { return NetConfig.GetMode().ToString(); }
            catch { return "Unknown"; }
        }

        private static bool IsClientEnemyPuppetModeEnabled()
        {
            try
            {
                return Plugin.Cfg.EnableClientEnemyPuppetMode.Value
                    && Plugin.Cfg.EnableHostEnemyStateSnapshotMirror.Value
                    && Plugin.Cfg.ApplyReceivedEnemyStateSnapshots.Value
                    && NetConfig.GetMode() == NetMode.Client;
            }
            catch { return false; }
        }

        // Phase RT3-Cousin-arms-Anim: boss adds that are scripted props driven by their own behaviour tree must keep that
        // BT alive on the client (puppet mode disables it), so they're exempt from the whole client snapshot/puppet path.
        // Currently just the Cousin arm (appear→idle→attack→disappear sequence). Death/despawn still arrives via the
        // independent EnemyDeathMirror path; damage is host-authoritative (the throw is de-fanged by CousinArmPatches).
        private static bool IsSelfAnimatingClientBossAdd(NetGameplayEntitySnapshot? snapshot)
        {
            if (snapshot == null) return false;
            try { return string.Equals(snapshot.EntityId?.UnitIdentifier, "GoblinCousinArm", StringComparison.Ordinal); }
            catch { return false; }
        }

        // EMP-2: identify the Emperor phase-1 worm body sections (10 per worm, UnitIds ShavwaEmperorWormSection /
        // ShavwaEmperorWormSectionVulnerable). These are excluded from the generic transform mirror — see the skip in
        // the enemy-state apply loop for why (broadphase churn from per-frame teleports). Match on UnitId prefix so
        // both the plain and vulnerable tail section qualify.
        private static bool IsEmperorWormSectionSnapshot(NetGameplayEntitySnapshot? snapshot)
        {
            if (snapshot == null) return false;
            try
            {
                string uid = snapshot.EntityId?.UnitIdentifier ?? "";
                return uid.IndexOf("EmperorWormSection", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        // LD-Sandstorm / F4: the Desert boss (DesertClauseBossFightHelper, UnitId "HellshrewDesertClause") is a COMPOSITE
        // boss whose visible body is assembled by its OWN local intro chain (OnStartInteractWithBoss -> DelayIntro ->
        // bossAnimator "IntroStarted" -> animation event -> TriggerFight, which hides sandSantaAnimationSprite and sets
        // "BossStarted"). Roster-binding it as a generic enemy puppet snaps its transform to the host position (overriding
        // the intro's RepositionBossFromCamera) AND mirrors the host animator (overriding the local "IntroStarted"), so
        // the intro never completes locally and the real body never appears -> invisible on the client. Excluding it from
        // the snapshot apply (same as the Cousin arm / Emperor worm above) lets the client run its real intro to completion
        // -> boss visible + the intro presentation plays. Health stays host-authoritative via HostBossState; hits via the
        // boss damage-authority path; neither uses this snapshot.
        private static bool IsDesertBossSnapshot(NetGameplayEntitySnapshot? snapshot)
        {
            if (snapshot == null) return false;
            try
            {
                string uid = snapshot.EntityId?.UnitIdentifier ?? "";
                return uid.IndexOf("DesertClause", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        // F4 fire-mirror diagnosis: one-line state of the enemy fire mirror pipeline for a given npc, both roles.
        // HOST: is a combat-action mark alive for it (fed by the TriggerWeaponManually prefix)? CLIENT: did the last
        // received host snapshot carry the combat action/intent, and is an authorized intent window open? Pinpoints
        // which link of mark → snapshot → window → root-replay is broken. Diagnostic only.
        internal static string DescribeEnemyFireMirrorState(object npc)
        {
            try
            {
                int id = ObjectIdentity(npc);
                if (id == 0) return "id0";
                if (NetConfig.GetMode() == NetMode.Host)
                {
                    if (HostEnemyCombatActionsByNpcId.TryGetValue(id, out var a))
                        return $"mark[kind={a.Kind} state={a.State} seq={a.Sequence} expIn={a.ExpiresAt - Time.realtimeSinceStartup:F2}]";
                    return "mark[none]";
                }
                string snap = "hostSnap[none]";
                if (ActiveEnemyPuppetsByNpcId.TryGetValue(id, out var rec) && rec.LastHostSnapshot != null)
                {
                    var hs = rec.LastHostSnapshot;
                    snap = $"hostSnap[combat={hs.HasHostCombatAction}:{hs.HostCombatActionKind}/{hs.HostCombatActionState} intent={hs.HasEnemyIntent}:{hs.EnemyIntentKind} seq={hs.HostCombatActionSequence}]";
                }
                else if (!ActiveEnemyPuppetsByNpcId.ContainsKey(id)) snap = "hostSnap[no-puppet-record]";
                string win = _clientAuthorizedIntentByNpcId.TryGetValue(id, out var w)
                    ? $"window[kind={w.Kind} seq={w.Sequence} expIn={w.ExpiresAt - Time.realtimeSinceStartup:F2} rootReplayed={w.RootReplayed}]"
                    : "window[none]";
                return snap + " " + win;
            }
            catch (Exception ex) { return "err:" + ex.GetType().Name + ":" + ex.Message; }
        }

        // F4-P1JMP: the boss's pike mount specifically ("HellshrewDesertClausePike" — note it also matches the broader
        // IsDesertBossSnapshot "DesertClause" substring, so pike-specific checks must run the narrower match).
        private static bool IsDesertBossPikeSnapshot(NetGameplayEntitySnapshot? snapshot)
        {
            if (snapshot == null) return false;
            try
            {
                string uid = snapshot.EntityId?.UnitIdentifier ?? "";
                return uid.IndexOf("DesertClausePike", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        private static void EnsureClientEnemyPuppetMode(string key, NetGameplayEntitySnapshot snapshot, NetGameplayEnemyStateSnapshot hostSnapshot, object runtimeObject, float now)
        {
            if (!IsClientEnemyPuppetModeEnabled()) return;
            if (snapshot == null || snapshot.IsDead) return;
            if (!string.Equals(snapshot.Category, "Npc", StringComparison.OrdinalIgnoreCase)) return;
            // Phase 4.4.0-O3: Traders and interactive NPCs must never enter puppet mode
            if (IsNonCombatForSync(snapshot)) return;
            if (string.IsNullOrWhiteSpace(key)) return;

            object? npc = ResolveMirroredEnemyObject(runtimeObject, out _);
            if (npc == null) return;
            if (npc is UnityEngine.Object unityObject && unityObject == null) return;

            if (!ActiveEnemyPuppets.TryGetValue(key, out var record))
            {
                record = new EnemyPuppetRecord
                {
                    Key = key,
                    Snapshot = snapshot,
                    Npc = npc,
                    NpcId = ObjectIdentity(npc),
                    LastSeenAt = now,
                };
                ActiveEnemyPuppets[key] = record;
            }

            record.Snapshot = snapshot;
            record.LastHostSnapshot = hostSnapshot;
            record.Npc = npc;
            int newNpcId = ObjectIdentity(npc);
            if (record.NpcId != 0 && record.NpcId != newNpcId)
                ActiveEnemyPuppetsByNpcId.Remove(record.NpcId);
            record.NpcId = newNpcId;
            if (record.NpcId != 0)
                ActiveEnemyPuppetsByNpcId[record.NpcId] = record;
            record.LastSeenAt = now;

            if (record.Applied && now - record.LastAppliedAt < 0.50f)
                return;

            ApplyClientEnemyPuppetMode(record, now);
        }

        private static void ApplyClientEnemyPuppetMode(EnemyPuppetRecord record, float now)
        {
            object? npc = record.Npc;
            if (npc == null) return;
            if (npc is UnityEngine.Object unityObject && unityObject == null) return;

            object? aiAgent = TryGetMemberValue(npc, "AiAgent") ?? TryGetMemberValue(npc, "aiAgent");
            object? movementDriver = aiAgent == null ? null : TryGetMemberValue(aiAgent, "ai");
            if (movementDriver == null)
                movementDriver = TryFindComponentByTypeName(npc, "CustomRichAI");

            record.AiAgent = aiAgent;
            record.MovementDriver = movementDriver;
            record.AiAgentId = aiAgent == null ? 0 : ObjectIdentity(aiAgent);
            record.MovementDriverId = movementDriver == null ? 0 : ObjectIdentity(movementDriver);

            ClientPuppetNpcIds.Add(record.NpcId);
            if (record.AiAgentId != 0) ClientPuppetAiAgentIds.Add(record.AiAgentId);
            if (record.MovementDriverId != 0) ClientPuppetMovementDriverIds.Add(record.MovementDriverId);

            if (!record.OriginalDisableVerifyPosition.HasValue && TryGetBoolMember(npc, "disableVerifyPosition", out var originalDisableVerifyPosition))
                record.OriginalDisableVerifyPosition = originalDisableVerifyPosition;
            if (!record.OriginalPreventNavMeshActivation.HasValue && TryGetBoolMember(npc, "preventNavMeshActivation", out var originalPreventNavMeshActivation))
                record.OriginalPreventNavMeshActivation = originalPreventNavMeshActivation;

            // Phase 5.5-RT3-A3: a host-driven puppet's transform is dragged to host positions (snap on bind + per-frame
            // drift correction). If its Rigidbody stays non-kinematic, those transform jumps make the physics solver
            // compute huge velocities → deep-penetration impulses into the arena's physics props (GoblinTowerBrick /
            // WoodenCrate / WoodenBarrel), which on the CLIENT ONLY (host=0 such damage) fling those props and the player
            // (Y=-115 out-of-bounds). NavMeshAgent + kinematic Rigidbody is the standard combo, so movement is unaffected.
            MakePuppetRigidbodyKinematic(record, npc);

            bool intentDriven = IsClientEnemyIntentDrivenMotionEnabled();

            // Reverse basis:
            // - Classic puppet mode disables local movement entirely and transform-drags to Host snapshots.
            // - Intent-driven mode keeps target/BT/combat disabled, but leaves local movement systems alive
            //   so the enemy can walk/turn/animate naturally toward Host-provided destinations.
            if (intentDriven)
            {
                _clientPuppetIntentReplayDepth++;
                try
                {
                    TryInvokeInstanceMethod(aiAgent, "SetCanMove", true);
                    TryInvokeInstanceMethod(aiAgent, "SetNavMeshAgentState", true);
                    TryInvokeInstanceMethod(aiAgent, "ToggleRVO", true);
                }
                finally
                {
                    if (_clientPuppetIntentReplayDepth > 0) _clientPuppetIntentReplayDepth--;
                }
                TryInvokeInstanceMethod(npc, "ToggleBehaviourTree", false);
                TrySetBoolMember(npc, "disableVerifyPosition", true);
                TrySetBoolMember(npc, "preventNavMeshActivation", false);
                TrySetBoolMember(npc, "NavMeshEnabledTarget", true);
                TryDisableBehaviourComponent(TryGetMemberValue(npc, "behaviourTree"));
            }
            else
            {
                TryInvokeInstanceMethod(aiAgent, "StopOnCurrentPosition");
                TryInvokeInstanceMethod(aiAgent, "SetCanMove", false);
                TryInvokeInstanceMethod(aiAgent, "SetNavMeshAgentState", false);
                TryInvokeInstanceMethod(aiAgent, "ToggleRVO", false);
                TryInvokeInstanceMethod(npc, "ToggleBehaviourTree", false);

                TrySetBoolMember(npc, "disableVerifyPosition", true);
                TrySetBoolMember(npc, "preventNavMeshActivation", true);
                TrySetBoolMember(npc, "NavMeshEnabledTarget", false);

                TryDisableBehaviourComponent(TryGetMemberValue(npc, "behaviourTree"));
                TryDisableNavMeshObject(TryGetMemberValue(npc, "navMeshAgent"));
                TryDisableRvoController(TryGetMemberValue(aiAgent, "rvoController"));
                TryZeroRigidbodyVelocity(TryGetMemberValue(npc, "Rigidbody") ?? TryGetMemberValue(npc, "rigidbody"));
            }
            ApplyClientEnemyTargetAuthority(record, now, intentDriven ? "intent puppet apply" : "puppet apply");

            bool firstApply = !record.Applied;
            record.Applied = true;
            record.LastAppliedAt = now;
            if (firstApply)
            {
                _clientEnemyPuppetsActivated++;
                if (Plugin.Cfg.LogClientEnemyPuppetMode.Value)
                    NetLogger.Info($"[EnemyPuppet] Activated Client puppet mode idx={record.Snapshot.SpawnIndex} actor={record.Snapshot.ActorName} aiAgent={(aiAgent == null ? "none" : aiAgent.GetType().Name)} movement={(movementDriver == null ? "none" : movementDriver.GetType().Name)}");
            }
        }


        private static void TryApplyClientEnemyAiIntent(EnemyPuppetRecord record, object runtimeObject, NetGameplayEnemyStateSnapshot hostSnapshot, float now)
        {
            if (record == null || runtimeObject == null || hostSnapshot == null) return;
            if (!IsClientEnemyIntentDrivenMotionEnabled()) return;
            if (!hostSnapshot.HasAiIntent) return;
            if (!IsFinite(hostSnapshot.AiIntentDestination)) return;

            Vector3 dest = hostSnapshot.AiIntentDestination;

            float minInterval = Plugin.Cfg.EnemyIntentReplayMinIntervalSeconds.Value;
            if (minInterval < 0.02f) minInterval = 0.02f;
            if (minInterval > 2f) minInterval = 2f;
            // O2: position-based idempotency — same sequence + destination not moved enough → skip.
            // This is not a cooldown; same Host intent with same destination is idempotent.
            const float minMoveThreshold = 0.75f;
            bool sameSeq = record.LastAppliedAiIntentSequence == hostSnapshot.AiIntentSequence;
            bool destClose = sameSeq && Vector3.Distance(record.LastAppliedMoveTargetPosition, dest) < minMoveThreshold;
            if (destClose) return;
            if (now - record.LastAppliedAiIntentAt < minInterval) return;

            object? npc = record.Npc ?? ResolveMirroredEnemyObject(runtimeObject, out _);
            object? aiAgent = record.AiAgent ?? (npc == null ? null : (TryGetMemberValue(npc, "AiAgent") ?? TryGetMemberValue(npc, "aiAgent")));
            object? movementDriver = record.MovementDriver ?? (aiAgent == null ? null : TryGetMemberValue(aiAgent, "ai"));
            bool applied = false;
            try
            {
                _clientPuppetIntentReplayDepth++;

                if (aiAgent != null)
                {
                    TryInvokeInstanceMethod(aiAgent, "SetCanMove", true);
                    TryInvokeInstanceMethod(aiAgent, "SetNavMeshAgentState", true);
                    TryInvokeInstanceMethod(aiAgent, "ToggleRVO", true);
                    applied |= TryInvokeInstanceMethod(aiAgent, "SetDestination", dest);
                    applied |= TryInvokeInstanceMethod(aiAgent, "SetDestination", dest, false);
                    applied |= TryInvokeInstanceMethod(aiAgent, "SetDestination", dest, true);
                    applied |= TrySetVector3Member(aiAgent, "destination", dest);
                    applied |= TrySetVector3Member(aiAgent, "Destination", dest);
                    applied |= TrySetVector3Member(aiAgent, "targetPosition", dest);
                    applied |= TrySetVector3Member(aiAgent, "TargetPosition", dest);
                }

                if (movementDriver != null)
                {
                    applied |= TryInvokeInstanceMethod(movementDriver, "SetDestination", dest);
                    applied |= TryInvokeInstanceMethod(movementDriver, "SetDestination", dest, false);
                    applied |= TrySetVector3Member(movementDriver, "destination", dest);
                    applied |= TrySetVector3Member(movementDriver, "targetPosition", dest);
                }

                if (npc != null)
                {
                    applied |= TryInvokeInstanceMethod(npc, "SetForcedDestination", dest);
                    applied |= TryInvokeInstanceMethod(npc, "SetDestination", dest);
                    TrySetBoolMember(npc, "NavMeshEnabledTarget", true);
                    TrySetBoolMember(npc, "preventNavMeshActivation", false);
                }
            }
            catch { }
            finally
            {
                if (_clientPuppetIntentReplayDepth > 0) _clientPuppetIntentReplayDepth--;
            }

            if (hostSnapshot.HasAiIntentLookAt)
                TryFaceHostAiIntent(runtimeObject, hostSnapshot);

            if (!applied) return;
            record.LastAppliedAiIntentSequence = hostSnapshot.AiIntentSequence;
            record.LastAppliedAiIntentAt = now;
            record.LastAppliedMoveTargetPosition = dest;
            _clientEnemyAiIntentApplies++;

            if (Plugin.Cfg.LogEnemyAiIntentMirror.Value && (Plugin.Cfg.EnableDebugLog.Value || _clientEnemyAiIntentApplies <= 12))
                NetLogger.Info($"[EnemyAiIntent] Client replayed Host intent idx={hostSnapshot.SpawnIndex} actor={hostSnapshot.ActorName} seq={hostSnapshot.AiIntentSequence} dest=({dest.x:F2},{dest.y:F2},{dest.z:F2}) applied={applied}");
        }

        private static void TryFaceHostAiIntent(object runtimeObject, NetGameplayEnemyStateSnapshot hostSnapshot)
        {
            if (runtimeObject == null || hostSnapshot == null || !hostSnapshot.HasAiIntentLookAt) return;
            if (!TryGetTransform(runtimeObject, out var transform) || transform == null) return;
            Vector3 delta = hostSnapshot.AiIntentLookAt - transform.position;
            delta.y = 0f;
            if (delta.sqrMagnitude < 0.001f) return;
            try { transform.rotation = Quaternion.LookRotation(delta.normalized, Vector3.up); }
            catch { }
        }


        private static void TryApplyHostAuthoritativeEnemyDamage(object npc, HostEnemyCombatAction action, float now)
        {
            if (npc == null || action == null) return;
            try
            {
                // RETIRED by default: the synthetic distance path ignores walls + post-shot dodging (false hits). It now
                // requires the fresh EnableSyntheticRangedDamageFallback key (default false) so a stale true on the old
                // EnableHostAuthoritativeEnemyRangedDamage key (which the game keeps rewriting) can't silently re-enable it.
                if (!Plugin.Cfg.EnableSyntheticRangedDamageFallback.Value) return;
                if (!Plugin.Cfg.EnableHostAuthoritativeEnemyRangedDamage.Value) return;
                if (NetConfig.GetMode() != NetMode.Host) return;
                if (action.Kind != CombatActionShoot && action.Kind != CombatActionSetShooting) return;
                if (!action.HasAim) return;
                if (!IsFinite(action.OriginPosition) || !IsFinite(action.AimPosition)) return;

                Vector3 origin = action.OriginPosition;
                Vector3 aim = action.AimPosition;
                Vector3 delta = aim - origin;
                float maxDistance = Plugin.Cfg.EnemyHostProjectileMaxDistance.Value;
                if (maxDistance < 2f) maxDistance = 2f;
                if (maxDistance > 80f) maxDistance = 80f;
                if (delta.sqrMagnitude < 0.05f) return;
                if (delta.magnitude > maxDistance)
                    aim = origin + delta.normalized * maxDistance;

                float radius = Plugin.Cfg.EnemyHostProjectileHitRadius.Value;
                if (radius < 0.10f) radius = 0.10f;
                if (radius > 2.50f) radius = 2.50f;
                float vertical = Plugin.Cfg.EnemyHostProjectileVerticalTolerance.Value;
                if (vertical < 0.10f) vertical = 0.10f;
                if (vertical > 4.00f) vertical = 4.00f;
                float damage = Plugin.Cfg.EnemyHostProjectileDamage.Value;
                if (damage <= 0f) return;
                float cooldown = Plugin.Cfg.EnemyHostProjectileDamageCooldownSeconds.Value;
                if (cooldown < 0.05f) cooldown = 0.05f;
                if (cooldown > 5f) cooldown = 5f;

                var peers = NetPlayerLifeManager.GetHostKnownAliveRemotePeerPositions();
                if (peers == null || peers.Count == 0) return;

                // Elemental ranged enemies carry their attribute in Npc.damageTypeOverride (Fire/Frost/Poison/Electric/...).
                // Forward it so the client's real ReceiveDamage drives the matching hurt-screen effect; plain archers have
                // None(0) -> client falls back to the configured physical default. (Weapon.GetDamageType uses this too.)
                int damageTypeInt = 0;
                try
                {
                    var dto = TryGetMemberValue(npc, "damageTypeOverride");
                    if (dto != null) { int v = Convert.ToInt32(dto); if (v != 0) damageTypeInt = v; }
                }
                catch { }

                for (int i = 0; i < peers.Count; i++)
                {
                    var peer = peers[i];
                    if (peer == null || string.IsNullOrWhiteSpace(peer.PeerId)) continue;
                    _hostEnemyDamageChecks++;

                    Vector3 peerCenter = peer.Position + Vector3.up * 0.85f;
                    if (!TrySegmentCapsuleHit(origin, aim, peerCenter, radius, vertical, out var closest, out var horizontalDistance))
                        continue;

                    string hitKey = $"{ObjectIdentity(npc)}:{peer.PeerId}";
                    if (HostEnemyDamageLastAtByKey.TryGetValue(hitKey, out var last) && now - last < cooldown)
                        continue;
                    HostEnemyDamageLastAtByKey[hitKey] = now;
                    _hostEnemyDamageHits++;

                    string enemyName = Clean(GetActorName(npc));
                    string reason = $"enemy={enemyName};kind={action.Kind};seq={action.Sequence};dist={horizontalDistance:F2}";
                    NetPlayerLifeManager.ReportHostAuthoritativeEnemyDamage(peer.PeerId, damage, reason, closest, damageTypeInt);

                    if (Plugin.Cfg.LogEnemyHostDamageAuthority.Value)
                        NetLogger.Info($"[EnemyDamageAuthority] Host projectile hit peer={peer.PeerId} actor={enemyName} seq={action.Sequence} damage={damage:F1} closest=({closest.x:F2},{closest.y:F2},{closest.z:F2}) dist={horizontalDistance:F2}");
                }
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[EnemyDamageAuthority] Host damage check failed: {ex.Message}");
            }
        }

        private static bool TrySegmentCapsuleHit(Vector3 start, Vector3 end, Vector3 point, float radius, float verticalTolerance, out Vector3 closest, out float horizontalDistance)
        {
            closest = start;
            horizontalDistance = float.MaxValue;
            try
            {
                Vector3 flatStart = new Vector3(start.x, 0f, start.z);
                Vector3 flatEnd = new Vector3(end.x, 0f, end.z);
                Vector3 flatPoint = new Vector3(point.x, 0f, point.z);
                Vector3 segment = flatEnd - flatStart;
                float lenSq = segment.sqrMagnitude;
                if (lenSq < 0.0001f) return false;
                float t = Mathf.Clamp01(Vector3.Dot(flatPoint - flatStart, segment) / lenSq);
                Vector3 flatClosest = flatStart + segment * t;
                horizontalDistance = Vector3.Distance(flatPoint, flatClosest);
                if (horizontalDistance > radius) return false;

                closest = Vector3.Lerp(start, end, t);
                float verticalDistance = Mathf.Abs(point.y - closest.y);
                if (verticalDistance > verticalTolerance) return false;
                return true;
            }
            catch { return false; }
        }

        private static void TryPopulateHostCombatAnimatorStates(object runtimeObject, NetGameplayEnemyStateSnapshot stateSnapshot)
        {
            if (runtimeObject == null || stateSnapshot == null) return;
            if (!Plugin.Cfg.EnableGenericHostCombatAnimatorStateMirror.Value) return;
            try
            {
                if (!TryGetTransform(runtimeObject, out var root) || root == null) return;

                Animator[] animators;
                if (runtimeObject is Component component && component != null)
                    animators = component.GetComponentsInChildren<Animator>(true);
                else if (runtimeObject is GameObject gameObject && gameObject != null)
                    animators = gameObject.GetComponentsInChildren<Animator>(true);
                else
                    animators = root.GetComponentsInChildren<Animator>(true);

                int count = 0;
                for (int i = 0; i < animators.Length && count < 4; i++)
                {
                    Animator animator = animators[i];
                    if (animator == null) continue;
                    if (animator is UnityEngine.Object uo && uo == null) continue;
                    if (animator.layerCount <= 0) continue;

                    int layer = 0;
                    AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(layer);
                    if (info.fullPathHash == 0 && info.shortNameHash == 0) continue;

                    int pathHash = GetRelativeAnimatorPathHash(root, animator.transform);
                    stateSnapshot.HostCombatAnimatorPathHashes[count] = pathHash;
                    stateSnapshot.HostCombatAnimatorLayers[count] = layer;
                    stateSnapshot.HostCombatAnimatorFullPathHashes[count] = info.fullPathHash;
                    stateSnapshot.HostCombatAnimatorNormalizedTimes[count] = IsFinite(info.normalizedTime) ? info.normalizedTime : 0f;
                    stateSnapshot.HostCombatAnimatorSpeeds[count] = IsFinite(animator.speed) ? animator.speed : 1f;
                    count++;
                }

                stateSnapshot.HostCombatAnimatorStateCount = count;
            }
            catch { }
        }

        private static int GetRelativeAnimatorPathHash(Transform root, Transform target)
        {
            try
            {
                if (root == null || target == null) return 0;
                if (ReferenceEquals(root, target)) return Animator.StringToHash("<root>");
                var names = new List<string>(8);
                Transform? current = target;
                while (current != null && !ReferenceEquals(current, root))
                {
                    names.Add(Clean(current.name));
                    current = current.parent;
                }
                names.Reverse();
                string path = names.Count == 0 ? "<root>" : string.Join("/", names.ToArray());
                return Animator.StringToHash(path);
            }
            catch { return 0; }
        }

        private static void TryApplyGenericHostCombatAnimatorStates(EnemyPuppetRecord record, object runtimeObject, NetGameplayEnemyStateSnapshot hostSnapshot, int actionSequence, float now)
        {
            if (record == null || runtimeObject == null || hostSnapshot == null) return;
            if (EaSelfPlayAttackAnimation) return;   // EA-B: stop the per-frame combat-state hash mirror; triggers + attack-phase events self-play.
            if (!Plugin.Cfg.EnableGenericHostCombatAnimatorStateMirror.Value) return;
            if (hostSnapshot.HostCombatAnimatorStateCount <= 0) return;
            // O2: when authorized combat intent is active, native root replay drives the Animator;
            // generic mirror would overwrite the correct state with potentially stale Host data.
            if (record.NpcId != 0 && HasActiveAuthorizedCombatIntentWindow(record.NpcId, now))
            {
                _genericCombatStateSkippedDuringAuthorizedIntent++;
                return;
            }

            int signature = ComputeGenericCombatAnimatorSignature(hostSnapshot, actionSequence);
            // Phase 4.4.0-M: do not treat a Host combat action as a one-shot animation apply.
            // In practice the first snapshot after TriggerWeaponManually/TriggerShoot can still be
            // walk/idle; the actual attack state often appears one or two network ticks later. Keep
            // resyncing changed combat states for the short Host action window, but rate-limit enough
            // to avoid reintroducing locomotion flicker.
            if (signature == record.LastAppliedGenericCombatAnimatorSignature && now - record.LastAppliedGenericCombatAnimatorAt < 0.10f) return;
            if (now - record.LastAppliedGenericCombatAnimatorAt < 0.055f) return;

            int applied = 0;
            try
            {
                if (!TryGetTransform(runtimeObject, out var root) || root == null) return;
                for (int i = 0; i < hostSnapshot.HostCombatAnimatorStateCount && i < 4; i++)
                {
                    int hostHash = hostSnapshot.HostCombatAnimatorFullPathHashes[i];
                    if (hostHash == 0) continue;
                    int pathHash = hostSnapshot.HostCombatAnimatorPathHashes[i];
                    if (!TryFindAnimatorByRelativePathHash(root, pathHash, out var animator) || animator == null) continue;
                    int layer = hostSnapshot.HostCombatAnimatorLayers[i];
                    if (layer < 0 || layer >= animator.layerCount) layer = 0;
                    float hostTime = Fraction01(hostSnapshot.HostCombatAnimatorNormalizedTimes[i]);
                    float fade = Plugin.Cfg.EnemyAnimationMirrorCrossFadeSeconds.Value;
                    try
                    {
                        var localInfo = animator.GetCurrentAnimatorStateInfo(layer);
                        bool stateChanged = localInfo.fullPathHash != hostHash;
                        float localTime = Fraction01(localInfo.normalizedTime);
                        float drift = FractionDistance(hostTime, localTime);
                        if (!stateChanged && drift < 0.12f && signature == record.LastAppliedGenericCombatAnimatorSignature)
                            continue;

                        if (fade <= 0f || !stateChanged)
                            animator.Play(hostHash, layer, hostTime);
                        else
                            animator.CrossFade(hostHash, Mathf.Clamp(fade, 0f, 0.5f), layer, hostTime);
                        float speed = hostSnapshot.HostCombatAnimatorSpeeds[i];
                        if (IsFinite(speed) && speed >= 0f && speed <= 5f)
                            animator.speed = speed < 0.01f ? 1f : speed;
                        applied++;
                    }
                    catch { }
                }
            }
            catch { }

            if (applied <= 0) return;
            record.LastAppliedGenericCombatAnimatorSignature = signature;
            record.LastAppliedGenericCombatAnimatorAt = now;
            _clientGenericCombatAnimatorStateApplies += applied;
            if (Plugin.Cfg.LogEnemyAnimationMirror.Value)
                NetLogger.Info($"[EnemyCombatAnim] Client applied continuous Host combat Animator states idx={hostSnapshot.SpawnIndex} actor={hostSnapshot.ActorName} seq={actionSequence} states={hostSnapshot.HostCombatAnimatorStateCount} applied={applied} sig={signature}");
        }

        private static int ComputeGenericCombatAnimatorSignature(NetGameplayEnemyStateSnapshot hostSnapshot, int actionSequence)
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + actionSequence;
                int count = hostSnapshot.HostCombatAnimatorStateCount;
                if (count > 4) count = 4;
                for (int i = 0; i < count; i++)
                {
                    hash = hash * 31 + hostSnapshot.HostCombatAnimatorPathHashes[i];
                    hash = hash * 31 + hostSnapshot.HostCombatAnimatorLayers[i];
                    hash = hash * 31 + hostSnapshot.HostCombatAnimatorFullPathHashes[i];
                    hash = hash * 31 + Mathf.RoundToInt(Fraction01(hostSnapshot.HostCombatAnimatorNormalizedTimes[i]) * 20f);
                }
                return hash;
            }
        }

        private static bool TryFindAnimatorByRelativePathHash(Transform root, int pathHash, out Animator? animator)
        {
            animator = null;
            try
            {
                if (root == null) return false;
                var animators = root.GetComponentsInChildren<Animator>(true);
                for (int i = 0; i < animators.Length; i++)
                {
                    var candidate = animators[i];
                    if (candidate == null) continue;
                    if (candidate is UnityEngine.Object uo && uo == null) continue;
                    if (GetRelativeAnimatorPathHash(root, candidate.transform) == pathHash)
                    {
                        animator = candidate;
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }



        private static void TryPopulateEnemyAiIntentSnapshot(object runtimeObject, NetGameplayEnemyStateSnapshot stateSnapshot)
        {
            if (runtimeObject == null || stateSnapshot == null) return;
            try
            {
                if (!Plugin.Cfg.EnableClientEnemyIntentDrivenMotion.Value) return;
                if (TryGetActiveHostEnemyAiIntent(runtimeObject, Time.realtimeSinceStartup, out var intent))
                {
                    if (!intent.HasDestination || !IsFinite(intent.Destination)) return;
                    stateSnapshot.HasAiIntent = true;
                    stateSnapshot.AiIntentSequence = intent.Sequence;
                    stateSnapshot.AiIntentKind = intent.Kind;
                    stateSnapshot.AiIntentDestination = intent.Destination;
                    stateSnapshot.HasAiIntentLookAt = intent.HasLookAt && IsFinite(intent.LookAt);
                    stateSnapshot.AiIntentLookAt = intent.LookAt;
                }
            }
            catch { }
        }

        private static void TryPopulateEnemyAnimatorSnapshot(object runtimeObject, NetGameplayEnemyStateSnapshot stateSnapshot)
        {
            if (runtimeObject == null || stateSnapshot == null) return;
            try
            {
                if (!TryGetAnimator(runtimeObject, out var animator) || animator == null) return;
                if (animator.layerCount <= 0) return;

                int layer = 0;
                AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(layer);
                if (info.fullPathHash == 0 && info.shortNameHash == 0) return;

                stateSnapshot.HasAnimatorState = true;
                stateSnapshot.AnimatorLayer = layer;
                stateSnapshot.AnimatorFullPathHash = info.fullPathHash;
                stateSnapshot.AnimatorShortNameHash = info.shortNameHash;
                stateSnapshot.AnimatorNormalizedTime = IsFinite(info.normalizedTime) ? info.normalizedTime : 0f;
                stateSnapshot.AnimatorSpeed = IsFinite(animator.speed) ? animator.speed : 1f;

                if (TryGetAnimatorBool(animator, "Moving", out var moving))
                {
                    stateSnapshot.HasAnimatorMovingBool = true;
                    stateSnapshot.AnimatorMovingBool = moving;
                }
                if (TryGetAnimatorBool(animator, "Attack", out var attack))
                {
                    stateSnapshot.HasAnimatorAttackBool = true;
                    stateSnapshot.AnimatorAttackBool = attack;
                }
                if (TryGetAnimatorBool(animator, "Cowering", out var cowering))
                {
                    stateSnapshot.HasAnimatorCoweringBool = true;
                    stateSnapshot.AnimatorCoweringBool = cowering;
                }

                if (TryGetActiveHostEnemyCombatAction(runtimeObject, Time.realtimeSinceStartup, out var hostCombatAction))
                {
                    // Keep the old Attack bool marker for I-version style state playback, but also
                    // send an explicit action kind/state so Client puppets can replay weapon-specific
                    // visual methods such as TriggerWeaponManually(state) and TriggerShoot().
                    stateSnapshot.HasAnimatorAttackBool = true;
                    stateSnapshot.AnimatorAttackBool = true;
                    stateSnapshot.HasHostCombatAction = true;
                    stateSnapshot.HostCombatActionKind = hostCombatAction.Kind;
                    stateSnapshot.HostCombatActionState = hostCombatAction.State;
                    stateSnapshot.HostCombatActionSequence = hostCombatAction.Sequence;
                    stateSnapshot.HasHostCombatAim = hostCombatAction.HasAim;
                    stateSnapshot.HostCombatOriginPosition = hostCombatAction.OriginPosition;
                    stateSnapshot.HostCombatAimPosition = hostCombatAction.AimPosition;
                    TryPopulateHostCombatAnimatorStates(runtimeObject, stateSnapshot);

                    // Phase 4.4.0-O: bridge HostCombatAction into the typed EnemyIntent field so
                    // Clients can create per-NPC authorization windows from a single snapshot field.
                    stateSnapshot.HasEnemyIntent = true;
                    stateSnapshot.EnemyIntentKind = HostCombatActionKindToEnemyIntentKind(hostCombatAction.Kind);
                    stateSnapshot.EnemyIntentSequence = hostCombatAction.Sequence;
                    stateSnapshot.EnemyIntentWeaponActionState = hostCombatAction.State;
                    stateSnapshot.EnemyIntentDuration = Plugin.Cfg.EnemyAnimationMirrorHostCombatActionHoldSeconds.Value;
                    stateSnapshot.EnemyIntentHasAimPosition = hostCombatAction.HasAim;
                    stateSnapshot.EnemyIntentAimPosition = hostCombatAction.AimPosition;
                    stateSnapshot.EnemyIntentHasOriginPosition = hostCombatAction.HasAim;
                    stateSnapshot.EnemyIntentOriginPosition = hostCombatAction.OriginPosition;

                    if (Plugin.Cfg.LogEnemyAnimationMirror.Value && Plugin.Cfg.EnableDebugLog.Value)
                        NetLogger.Debug($"[EnemyCombatAnim] Host marked combat action idx={stateSnapshot.SpawnIndex} actor={stateSnapshot.ActorName} kind={hostCombatAction.Kind} state={hostCombatAction.State} source={hostCombatAction.Source} seq={hostCombatAction.Sequence}");
                }

                if (Plugin.Cfg.LogEnemyAnimationMirror.Value && Plugin.Cfg.EnableDebugLog.Value)
                {
                    NetLogger.Debug($"[EnemyAnimMirror] Host snapshot idx={stateSnapshot.SpawnIndex} actor={stateSnapshot.ActorName} state={stateSnapshot.AnimatorFullPathHash} short={stateSnapshot.AnimatorShortNameHash} t={Fraction01(stateSnapshot.AnimatorNormalizedTime):F2} moving={BoolText(stateSnapshot.HasAnimatorMovingBool, stateSnapshot.AnimatorMovingBool)} attack={BoolText(stateSnapshot.HasAnimatorAttackBool, stateSnapshot.AnimatorAttackBool)} cowering={BoolText(stateSnapshot.HasAnimatorCoweringBool, stateSnapshot.AnimatorCoweringBool)} combat={stateSnapshot.HasHostCombatAction}:{stateSnapshot.HostCombatActionKind}/{stateSnapshot.HostCombatActionState}#{stateSnapshot.HostCombatActionSequence}");
                }
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[EnemyAnimMirror] Host animator snapshot failed: {ex.Message}");
            }
        }

        public static void ApplyClientEnemyPuppetAnimationPostUpdate(object? npc)
        {
            if (!IsEnemyAnimationMirrorApplyEnabled()) return;
            if (npc == null) return;
            try
            {
                int npcId = ObjectIdentity(npc);
                if (npcId == 0 || !ActiveEnemyPuppetsByNpcId.TryGetValue(npcId, out var record)) return;
                if (record.LastHostSnapshot == null) return;

                float now = Time.realtimeSinceStartup;
                ApplyClientEnemyAnimationMirror(record.Key, record.LastHostSnapshot, npc, now, parametersOnly: true);
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[EnemyAnimMirror] Puppet post-update apply failed: {ex.Message}");
            }
        }

        private static void ApplyClientEnemyPuppetMotionAnimation(string key, NetGameplayEnemyStateSnapshot? hostSnapshot, object runtimeObject, Vector3 currentPosition, Vector3 nextPosition, Vector3 hostTargetPosition, float now)
        {
            if (!IsEnemyAnimationMirrorApplyEnabled()) return;
            if (string.IsNullOrWhiteSpace(key)) return;
            if (!ActiveEnemyPuppets.TryGetValue(key, out var record)) return;
            if (runtimeObject == null) return;
            if (runtimeObject is UnityEngine.Object runtimeUnityObject && runtimeUnityObject == null) return;

            float appliedDistance = IsFinite(currentPosition) && IsFinite(nextPosition)
                ? Vector3.Distance(currentPosition, nextPosition)
                : 0f;
            float remainingDistance = IsFinite(nextPosition) && IsFinite(hostTargetPosition)
                ? Vector3.Distance(nextPosition, hostTargetPosition)
                : 0f;

            // The Client puppet's NavMesh/BT is disabled, so vanilla Npc.Update often sees
            // no local AI movement and writes Animator Moving=false.  Drive locomotion from
            // the Host transform stream instead; this is the reliable authority signal for
            // whether the puppet should be walking between snapshots.
            bool moving = appliedDistance > 0.0025f || remainingDistance > 0.035f;
            if (hostSnapshot != null && hostSnapshot.HasAnimatorMovingBool && hostSnapshot.AnimatorMovingBool)
                moving = true;

            record.HasMotionDerivedMoving = true;
            record.MotionDerivedMoving = moving;
            record.LastMotionDerivedMovingAt = now;

            ApplyClientEnemyAnimationMirror(key, hostSnapshot, runtimeObject, now, parametersOnly: true);
        }

        private static void ApplyClientEnemyAnimationMirror(string key, NetGameplayEnemyStateSnapshot? hostSnapshot, object runtimeObject, float now, bool parametersOnly = false)
        {
            if (!IsEnemyAnimationMirrorApplyEnabled()) return;
            if (hostSnapshot == null || !hostSnapshot.HasAnimatorState) return;
            if (runtimeObject == null) return;
            if (runtimeObject is UnityEngine.Object runtimeUnityObject && runtimeUnityObject == null) return;

            // Phase 5.3-C P0-1: terminal dead corpses must not be re-animated to Idle/Moving/Hit.
            if (IsClientTerminalDeadByKey(key))
            {
                _terminalDeadBlockedGenericReplay++;
                return;
            }

            try
            {
                ActiveEnemyPuppets.TryGetValue(key, out var record);
                if (record == null) return;

                if (!TryGetCachedAnimator(record, runtimeObject, out var animator) || animator == null) return;
                if (animator.layerCount <= 0) return;

                int layer = hostSnapshot.AnimatorLayer;
                if (layer < 0 || layer >= animator.layerCount) layer = 0;

                EnsureAnimatorParameterCache(record, animator);

                // Phase EA-A: actionState release grace. The host combat-action hold window (0.80s) is often shorter
                // than a standing enemy's attack loop (~1.0s), so actionState drops to false for the ~0.17s gap each
                // cycle and locomotion yanks the animator back to idle — the "standing animation inserted between
                // attacks" thrash (LogOutput210). For a short grace after the raw signal drops, skip this frame's
                // mirror entirely so the animator stays on the attack state it is already playing (no Moving=false
                // write, no idle re-Play). A continuing attack re-arms within the grace; a real stop releases to
                // locomotion once the grace elapses — self-tuning to the attack cadence, independent of the fixed
                // hold value. (No config switch — see no-new-config-switches.) Treats only the layer-1 idle gap;
                // the in-window sub-state thrash (layer 2) is a separate later step.
                const float actionStateReleaseGraceSeconds = 0.35f;
                bool rawActionState = IsClientEnemyActionPlaybackSnapshot(hostSnapshot);
                if (rawActionState)
                    record.LastRawActionTrueAt = now;
                else if (record.LastRawActionTrueAt > 0f && now - record.LastRawActionTrueAt <= actionStateReleaseGraceSeconds)
                    return;

                if (record.HasMovingParam)
                {
                    bool hasMovingValue = hostSnapshot.HasAnimatorMovingBool;
                    bool movingValue = hasMovingValue && hostSnapshot.AnimatorMovingBool;
                    if (record.HasMotionDerivedMoving && now - record.LastMotionDerivedMovingAt <= 0.25f)
                    {
                        hasMovingValue = true;
                        if (record.MotionDerivedMoving)
                            movingValue = true;
                        else if (!hostSnapshot.AnimatorMovingBool)
                            movingValue = false;
                    }
                    if (hasMovingValue)
                        animator.SetBool(record.MovingParamHash, movingValue);
                }
                if (hostSnapshot.HasAnimatorAttackBool && record.HasAttackParam)
                    animator.SetBool(record.AttackParamHash, hostSnapshot.AnimatorAttackBool);
                if (hostSnapshot.HasAnimatorCoweringBool && record.HasCoweringParam)
                    animator.SetBool(record.CoweringParamHash, hostSnapshot.AnimatorCoweringBool);

                // Reuse the raw value computed above for the grace check; a grace-held frame already returned, so
                // here actionState reflects the effective (post-grace) playback state — the probe logs that.
                bool actionState = rawActionState;
                LogClientActionStateFlipIfChanged(record, hostSnapshot, actionState, now);
                if (actionState && Plugin.Cfg.EnemyAnimationMirrorApplyHostCombatStatePlayback.Value)
                {
                    TryApplyClientCombatAnimatorTriggers(record, animator, hostSnapshot, now);
                    TryReplayClientHostCombatVisualAction(record, runtimeObject, hostSnapshot, now);
                }

                if (IsFinite(hostSnapshot.AnimatorSpeed) && hostSnapshot.AnimatorSpeed >= 0f && hostSnapshot.AnimatorSpeed <= 5f)
                    animator.speed = animator.speed < 0.01f && actionState ? 1f : hostSnapshot.AnimatorSpeed;

                // Full state-hash playback remains off by default for locomotion. Phase 4.4.0-I
                // allows only short Host-marked combat snapshots to use state playback, so walk/idle
                // stays motion-driven while attacks/shots can become visible on Client puppets.
                bool allowFullStatePlayback = Plugin.Cfg.EnemyAnimationMirrorApplyAnimatorStatePlayback.Value;
                bool allowHostCombatStatePlayback = Plugin.Cfg.EnemyAnimationMirrorApplyHostCombatStatePlayback.Value && actionState;
                if (parametersOnly || (!allowFullStatePlayback && !allowHostCombatStatePlayback))
                    return;

                bool puppetMoving = record.HasMotionDerivedMoving
                    && record.MotionDerivedMoving
                    && now - record.LastMotionDerivedMovingAt <= 0.25f;
                if (puppetMoving && !actionState)
                    return;

                int hostHash = hostSnapshot.AnimatorFullPathHash;
                if (hostHash == 0) return;

                AnimatorStateInfo localInfo = animator.GetCurrentAnimatorStateInfo(layer);
                bool stateChanged = localInfo.fullPathHash != hostHash;
                float hostTime = Fraction01(hostSnapshot.AnimatorNormalizedTime);
                float localTime = Fraction01(localInfo.normalizedTime);
                float drift = FractionDistance(hostTime, localTime);

                float tolerance = Plugin.Cfg.EnemyAnimationMirrorNormalizedTimeTolerance.Value;
                if (tolerance < 0.02f) tolerance = 0.02f;
                if (tolerance > 1f) tolerance = 1f;

                bool selfPlayAttack = EaSelfPlayAttackAnimation && actionState;
                bool shouldTimeResync = !stateChanged && actionState && drift > tolerance && now - record.LastAnimatorApplyAt > 0.10f;
                if (selfPlayAttack)
                {
                    // EA-B: attack state self-plays via combat triggers + attack-phase events; skip the per-frame hash
                    // Play that replayed each sub-state from t≈0 (the crouch-repeat thrash). Throttled diag only.
                    if (Plugin.Cfg.LogEnemyAnimationMirror.Value && (stateChanged || shouldTimeResync) && now - record.LastAnimatorApplyAt > 0.25f)
                    {
                        record.LastAnimatorApplyAt = now;
                        NetLogger.Info($"[EnemyAnimMirror] idx={hostSnapshot.SpawnIndex} unit={hostSnapshot.UnitIdentifier} SELF-PLAY skip-hash hostState={hostHash} changed={stateChanged} drift={drift:F2}");
                    }
                }
                else if (stateChanged || shouldTimeResync)
                {
                    float fade = Plugin.Cfg.EnemyAnimationMirrorCrossFadeSeconds.Value;
                    if (fade <= 0f || shouldTimeResync)
                        animator.Play(hostHash, layer, hostTime);
                    else
                        animator.CrossFade(hostHash, Mathf.Clamp(fade, 0f, 0.5f), layer, hostTime);

                    // Phase EA diag: count consecutive Plays of the SAME state hash. A high replay count on one hash
                    // with changed=False = the shouldTimeResync path repeatedly re-Playing a looping windup (e.g.
                    // GoblinYoung's crouch) back to host t — the "crouch animation repeats several times" bug. A churn
                    // of changed=True across hashes = host cycling sub-states. unit= names the enemy (GoblinYoung/...).
                    if (hostHash == record.LastReplayHash) record.ReplayCount++;
                    else { record.LastReplayHash = hostHash; record.ReplayCount = 1; }

                    bool log = Plugin.Cfg.LogEnemyAnimationMirror.Value
                        && (record.LastAppliedAnimatorFullPathHash != hostHash || shouldTimeResync || !record.LastLoggedAnimatorState);
                    record.LastAppliedAnimatorFullPathHash = hostHash;
                    record.LastAnimatorApplyAt = now;
                    record.LastLoggedAnimatorState = true;
                    if (actionState && !Plugin.Cfg.EnemyAnimationMirrorApplyAnimatorStatePlayback.Value)
                        _clientCombatAnimatorStateApplies++;
                    if (log)
                        NetLogger.Info($"[EnemyAnimMirror] idx={hostSnapshot.SpawnIndex} unit={hostSnapshot.UnitIdentifier} state={hostHash} t={hostTime:F2} changed={stateChanged} resync={shouldTimeResync} replay={record.ReplayCount} drift={drift:F2} action={actionState} moving={puppetMoving}");
                }
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[EnemyAnimMirror] Apply failed idx={hostSnapshot.SpawnIndex} actor={hostSnapshot.ActorName}: {ex.Message}");
            }
        }

        private static bool IsClientEnemyActionPlaybackSnapshot(NetGameplayEnemyStateSnapshot hostSnapshot)
        {
            if (hostSnapshot == null) return false;
            if (hostSnapshot.IsDead) return true;
            if (hostSnapshot.HasHostCombatAction && hostSnapshot.HostCombatActionKind != CombatActionNone) return true;
            if (hostSnapshot.HasAnimatorAttackBool && hostSnapshot.AnimatorAttackBool) return true;
            if (hostSnapshot.HasAnimatorCoweringBool && hostSnapshot.AnimatorCoweringBool) return true;
            return false;
        }

        // Diagnostic probe (gated by the existing LogEnemyAnimationMirror — no new config switch): log every
        // actionState flip with the reason that drove it + the interval since the previous flip. actionState gates
        // whether the client puppet's Animator is force-played to the host combat state vs left to locomotion; rapid
        // flips are the suspected source of the "idle/attack animation intermittently inserted" thrash (same root as
        // the Cousin-arm looping-appear bug, but on locomotion enemies). Reading the reason + sinceLastFlip columns
        // tells us which boundary dominates: combatAction window churn, attackBool jitter, or the 0.80s hold edge.
        private static void LogClientActionStateFlipIfChanged(EnemyPuppetRecord record, NetGameplayEnemyStateSnapshot hostSnapshot, bool actionState, float now)
        {
            if (record == null || hostSnapshot == null) return;
            if (!Plugin.Cfg.LogEnemyAnimationMirror.Value) return;
            if (record.HasLastActionState && record.LastActionState == actionState) return;

            float sinceLast = record.HasLastActionState && record.LastActionStateFlipAt > 0f
                ? now - record.LastActionStateFlipAt
                : -1f;

            string reason;
            if (!actionState) reason = "none";
            else if (hostSnapshot.IsDead) reason = "dead";
            else if (hostSnapshot.HasHostCombatAction && hostSnapshot.HostCombatActionKind != CombatActionNone)
                reason = $"combatAction(kind={hostSnapshot.HostCombatActionKind},seq={hostSnapshot.HostCombatActionSequence})";
            else if (hostSnapshot.HasAnimatorAttackBool && hostSnapshot.AnimatorAttackBool) reason = "attackBool";
            else if (hostSnapshot.HasAnimatorCoweringBool && hostSnapshot.AnimatorCoweringBool) reason = "cowering";
            else reason = "?";

            bool moving = record.HasMotionDerivedMoving && record.MotionDerivedMoving
                && now - record.LastMotionDerivedMovingAt <= 0.25f;
            string prev = record.HasLastActionState ? record.LastActionState.ToString() : "init";

            NetLogger.Info($"[ActionStateFlip] idx={hostSnapshot.SpawnIndex} unit={hostSnapshot.UnitIdentifier} {prev}->{actionState} reason={reason} sinceLastFlip={sinceLast:F2}s moving={moving} attackBool={(hostSnapshot.HasAnimatorAttackBool ? hostSnapshot.AnimatorAttackBool.ToString() : "-")}");

            record.HasLastActionState = true;
            record.LastActionState = actionState;
            record.LastActionStateFlipAt = now;
        }

        private static void TryReplayClientHostCombatVisualAction(EnemyPuppetRecord record, object runtimeObject, NetGameplayEnemyStateSnapshot hostSnapshot, float now)
        {
            if (record == null || runtimeObject == null || hostSnapshot == null) return;
            if (!hostSnapshot.HasHostCombatAction) return;
            if (hostSnapshot.HostCombatActionKind == CombatActionNone) return;

            int actionSequence = hostSnapshot.HostCombatActionSequence;
            if (actionSequence <= 0) actionSequence = hostSnapshot.Sequence;

            TryFaceHostCombatAim(runtimeObject, hostSnapshot);
            TryApplyGenericHostCombatAnimatorStates(record, runtimeObject, hostSnapshot, actionSequence, now);
            TrySpawnClientVisualProjectileForHostCombat(record, hostSnapshot, actionSequence, now);
            TryApplyClientCombatAnimatorFallback(record, runtimeObject, hostSnapshot, actionSequence, now);

            if (!Plugin.Cfg.EnemyAnimationMirrorReplayHostCombatMethods.Value) return;
            if (now - record.LastAppliedCombatVisualActionAt < 0.08f) return;

            // O2: sequence-based idempotency — replay the root method exactly once per sequence.
            int replayNpcId = record.NpcId;
            ClientEnemyAuthorizedIntentWindow? replayWindow = null;
            bool hasReplayWindow = replayNpcId != 0
                && _clientAuthorizedIntentByNpcId.TryGetValue(replayNpcId, out replayWindow)
                && now <= replayWindow!.ExpiresAt;
            if (hasReplayWindow && replayWindow!.RootReplayed && replayWindow.Sequence == actionSequence)
            {
                _clientCombatRootReplaySkippedDuplicate++;
                return;
            }
            // Legacy per-record duplicate check (fallback when auth window not available).
            if (!hasReplayWindow && record.LastAppliedCombatVisualActionSequence == actionSequence
                && now - record.LastAppliedCombatVisualActionAt < 0.75f)
                return;

            bool invoked = false;
            string method = "";
            try
            {
                // O2: use dedicated root replay depth so ShouldBlock distinguishes us from local AI.
                _clientAuthorizedCombatRootReplayDepth++;
                _clientPuppetCombatVisualReplayDepth++;

                switch (hostSnapshot.HostCombatActionKind)
                {
                    case CombatActionTriggerWeapon:
                        method = "TriggerWeaponManually";
                        invoked = TryInvokeInstanceMethod(runtimeObject, method, hostSnapshot.HostCombatActionState);
                        break;
                    case CombatActionShoot:
                        if (!Plugin.Cfg.EnemyProjectileVisualMirrorUseNativeShootReplay.Value)
                            break;
                        method = "TriggerShoot";
                        invoked = TryInvokeInstanceMethod(runtimeObject, method);
                        break;
                    case CombatActionAttackAnimation:
                        method = "TriggerAttackAnimation";
                        invoked = TryInvokeInstanceMethod(runtimeObject, method);
                        break;
                    case CombatActionSetShooting:
                        if (!Plugin.Cfg.EnemyProjectileVisualMirrorUseNativeShootReplay.Value)
                            break;
                        // SetShooting(true) can be sticky on some enemies. Prefer a one-shot TriggerShoot
                        // visual replay when available, and fall back to SetShooting(true) only if needed.
                        method = "TriggerShoot";
                        invoked = TryInvokeInstanceMethod(runtimeObject, method);
                        if (!invoked)
                        {
                            method = "SetShooting";
                            invoked = TryInvokeInstanceMethod(runtimeObject, method, true);
                        }
                        break;
                }
            }
            catch { invoked = false; }
            finally
            {
                if (_clientAuthorizedCombatRootReplayDepth > 0) _clientAuthorizedCombatRootReplayDepth--;
                if (_clientPuppetCombatVisualReplayDepth > 0) _clientPuppetCombatVisualReplayDepth--;
            }

            if (!invoked) return;

            // Mark sequence as replayed so spontaneous duplicates are blocked.
            if (hasReplayWindow && replayWindow != null && replayWindow.Sequence == actionSequence)
                replayWindow.RootReplayed = true;

            record.LastAppliedCombatVisualActionSequence = actionSequence;
            record.LastAppliedCombatVisualActionAt = now;
            _clientCombatVisualActionReplays++;
            _clientCombatRootReplays++;

            if (Plugin.Cfg.LogEnemyAnimationMirror.Value)
                NetLogger.Info($"[EnemyCombatAnim] Client replayed Host combat visual idx={hostSnapshot.SpawnIndex} actor={hostSnapshot.ActorName} kind={hostSnapshot.HostCombatActionKind} state={hostSnapshot.HostCombatActionState} seq={actionSequence} method={method}");
        }

        private static void TrySpawnClientVisualProjectileForHostCombat(EnemyPuppetRecord record, NetGameplayEnemyStateSnapshot hostSnapshot, int actionSequence, float now)
        {
            if (record == null || hostSnapshot == null) return;
            if (!Plugin.Cfg.EnemyProjectileVisualMirrorEnabled.Value) return;
            if (hostSnapshot.HostCombatActionKind != CombatActionShoot && hostSnapshot.HostCombatActionKind != CombatActionSetShooting) return;
            if (!hostSnapshot.HasHostCombatAim) return;
            if (record.LastVisualProjectileSequence == actionSequence && now - record.LastVisualProjectileAt < 0.75f) return;
            if (now - record.LastVisualProjectileAt < 0.30f) return;
            if (!IsFinite(hostSnapshot.HostCombatOriginPosition) || !IsFinite(hostSnapshot.HostCombatAimPosition)) return;

            Vector3 origin = hostSnapshot.HostCombatOriginPosition;
            Vector3 target = hostSnapshot.HostCombatAimPosition;
            Vector3 delta = target - origin;
            if (delta.sqrMagnitude < 0.05f)
                return;

            float speed = Plugin.Cfg.EnemyProjectileVisualMirrorSpeed.Value;
            if (speed < 1f) speed = 1f;
            if (speed > 80f) speed = 80f;
            float lifetime = Plugin.Cfg.EnemyProjectileVisualMirrorLifetime.Value;
            if (lifetime < 0.10f) lifetime = 0.10f;
            if (lifetime > 5f) lifetime = 5f;

            try
            {
                GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                go.name = $"SULFUR Together Enemy Projectile Visual #{actionSequence}";
                go.transform.position = origin;
                Vector3 dir = delta.normalized;
                go.transform.rotation = Quaternion.LookRotation(dir, Vector3.up) * Quaternion.Euler(90f, 0f, 0f);
                go.transform.localScale = new Vector3(0.045f, 0.55f, 0.045f);

                var collider = go.GetComponent<Collider>();
                if (collider != null)
                    UnityEngine.Object.Destroy(collider);

                ClientVisualProjectiles.Add(new ClientVisualProjectile
                {
                    GameObject = go,
                    Position = origin,
                    Target = target,
                    Speed = speed,
                    ExpiresAt = now + lifetime
                });

                record.LastVisualProjectileSequence = actionSequence;
                record.LastVisualProjectileAt = now;
                _clientVisualProjectileMirrors++;

                if (Plugin.Cfg.LogEnemyAnimationMirror.Value)
                    NetLogger.Info($"[EnemyProjectileVisual] Spawned visual projectile idx={hostSnapshot.SpawnIndex} actor={hostSnapshot.ActorName} seq={actionSequence} origin=({origin.x:F2},{origin.y:F2},{origin.z:F2}) target=({target.x:F2},{target.y:F2},{target.z:F2})");
            }
            catch { }
        }

        private static void UpdateClientVisualProjectiles()
        {
            if (ClientVisualProjectiles.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            float dt = Mathf.Max(Time.deltaTime, 1f / 60f);
            for (int i = ClientVisualProjectiles.Count - 1; i >= 0; i--)
            {
                var item = ClientVisualProjectiles[i];
                if (item == null || item.GameObject == null || (item.GameObject is UnityEngine.Object uo && uo == null) || now >= item.ExpiresAt)
                {
                    DestroyClientVisualProjectileAt(i);
                    continue;
                }

                Vector3 toTarget = item.Target - item.Position;
                float distance = toTarget.magnitude;
                if (distance <= 0.05f)
                {
                    DestroyClientVisualProjectileAt(i);
                    continue;
                }

                Vector3 step = toTarget.normalized * item.Speed * dt;
                if (step.magnitude >= distance)
                    item.Position = item.Target;
                else
                    item.Position += step;

                item.GameObject.transform.position = item.Position;
                if ((item.Target - item.Position).sqrMagnitude > 0.0001f)
                    item.GameObject.transform.rotation = Quaternion.LookRotation((item.Target - item.Position).normalized, Vector3.up) * Quaternion.Euler(90f, 0f, 0f);
            }
        }

        private static void DestroyAllClientVisualProjectiles()
        {
            for (int i = ClientVisualProjectiles.Count - 1; i >= 0; i--)
                DestroyClientVisualProjectileAt(i);
            ClientVisualProjectiles.Clear();
        }

        private static void DestroyClientVisualProjectileAt(int index)
        {
            if (index < 0 || index >= ClientVisualProjectiles.Count) return;
            try
            {
                var item = ClientVisualProjectiles[index];
                if (item != null && item.GameObject != null)
                    UnityEngine.Object.Destroy(item.GameObject);
            }
            catch { }
            ClientVisualProjectiles.RemoveAt(index);
        }

        private static void TryApplyClientCombatAnimatorFallback(EnemyPuppetRecord record, object runtimeObject, NetGameplayEnemyStateSnapshot hostSnapshot, int actionSequence, float now)
        {
            if (record == null || runtimeObject == null || hostSnapshot == null) return;
            if (!Plugin.Cfg.EnemyAnimationMirrorApplyCombatAnimatorFallback.Value) return;
            if (record.LastAppliedCombatAnimatorFallbackSequence == actionSequence && now - record.LastAppliedCombatAnimatorFallbackAt < 0.75f) return;
            if (now - record.LastAppliedCombatAnimatorFallbackAt < 0.08f) return;

            int applied = 0;
            try
            {
                if (runtimeObject is Component component && component != null)
                {
                    var animators = component.GetComponentsInChildren<Animator>(true);
                    for (int i = 0; i < animators.Length; i++)
                        applied += PulseCombatAnimator(animators[i], hostSnapshot, now);
                }
                else if (runtimeObject is GameObject gameObject && gameObject != null)
                {
                    var animators = gameObject.GetComponentsInChildren<Animator>(true);
                    for (int i = 0; i < animators.Length; i++)
                        applied += PulseCombatAnimator(animators[i], hostSnapshot, now);
                }

                object? weapon = TryFindLikelyWeaponObject(runtimeObject);
                applied += TryPulseWeaponVisualMethods(weapon, hostSnapshot);
                if (weapon is Component weaponComponent && weaponComponent != null)
                {
                    var weaponAnimators = weaponComponent.GetComponentsInChildren<Animator>(true);
                    for (int i = 0; i < weaponAnimators.Length; i++)
                        applied += PulseCombatAnimator(weaponAnimators[i], hostSnapshot, now);
                }
            }
            catch { }

            if (applied <= 0) return;

            record.LastAppliedCombatAnimatorFallbackSequence = actionSequence;
            record.LastAppliedCombatAnimatorFallbackAt = now;
            _clientCombatAnimatorFallbacks++;

            if (Plugin.Cfg.LogEnemyAnimationMirror.Value)
                NetLogger.Info($"[EnemyCombatAnim] Client combat animator fallback idx={hostSnapshot.SpawnIndex} actor={hostSnapshot.ActorName} kind={hostSnapshot.HostCombatActionKind} seq={actionSequence} applied={applied}");
        }

        private static int PulseCombatAnimator(Animator? animator, NetGameplayEnemyStateSnapshot hostSnapshot, float now)
        {
            if (animator == null) return 0;
            try
            {
                if (animator is UnityEngine.Object uo && uo == null) return 0;
                int applied = 0;
                foreach (var parameter in animator.parameters)
                {
                    if (parameter == null || string.IsNullOrWhiteSpace(parameter.name)) continue;
                    string name = parameter.name;
                    if (!LooksLikeCombatAnimatorParameter(name, hostSnapshot.HostCombatActionKind)) continue;

                    if (parameter.type == AnimatorControllerParameterType.Trigger)
                    {
                        try { animator.SetTrigger(parameter.nameHash); applied++; } catch { }
                    }
                    else if (parameter.type == AnimatorControllerParameterType.Bool)
                    {
                        try
                        {
                            animator.SetBool(parameter.nameHash, true);
                            PendingAnimatorBoolResets.Add(new AnimatorBoolReset { Animator = animator, Hash = parameter.nameHash, ResetAt = now + 0.35f });
                            applied++;
                        }
                        catch { }
                    }
                    else if (parameter.type == AnimatorControllerParameterType.Int)
                    {
                        try { animator.SetInteger(parameter.nameHash, hostSnapshot.HostCombatActionState); applied++; } catch { }
                    }
                }
                return applied;
            }
            catch { return 0; }
        }

        private static void UpdateCombatAnimatorBoolResets()
        {
            if (PendingAnimatorBoolResets.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            for (int i = PendingAnimatorBoolResets.Count - 1; i >= 0; i--)
            {
                var item = PendingAnimatorBoolResets[i];
                if (item == null || now < item.ResetAt) continue;
                try
                {
                    if (item.Animator != null)
                        item.Animator.SetBool(item.Hash, false);
                }
                catch { }
                PendingAnimatorBoolResets.RemoveAt(i);
            }
        }

        private static bool LooksLikeCombatAnimatorParameter(string name, int kind)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string n = name.ToLowerInvariant();
            if (n.Contains("move") || n.Contains("moving") || n.Contains("idle") || n.Contains("cower")) return false;
            if (kind == CombatActionShoot || kind == CombatActionSetShooting)
                return n.Contains("shoot") || n.Contains("fire") || n.Contains("attack") || n.Contains("ranged");
            return n.Contains("attack") || n.Contains("melee") || n.Contains("weapon") || n.Contains("swing") || n.Contains("stab") || n.Contains("lunge");
        }

        private static int TryPulseWeaponVisualMethods(object? weapon, NetGameplayEnemyStateSnapshot hostSnapshot)
        {
            if (weapon == null) return 0;
            int applied = 0;
            try
            {
                _clientPuppetCombatVisualReplayDepth++;
                if (hostSnapshot.HostCombatActionKind == CombatActionTriggerWeapon || hostSnapshot.HostCombatActionKind == CombatActionAttackAnimation)
                {
                    if (TryInvokeInstanceMethod(weapon, "TriggerAttackAnimation")) applied++;
                    if (TryInvokeInstanceMethod(weapon, "SetAttacking", true)) applied++;
                    if (TryInvokeInstanceMethod(weapon, "MeleeWeaponAttack")) applied++;
                    if (TryInvokeInstanceMethod(weapon, "MeleeAttack")) applied++;
                }
                else if (hostSnapshot.HostCombatActionKind == CombatActionShoot || hostSnapshot.HostCombatActionKind == CombatActionSetShooting)
                {
                    if (TryInvokeInstanceMethod(weapon, "PlayWeaponShootAnimation")) applied++;
                    if (TryInvokeInstanceMethod(weapon, "SetRangedAttacking", true)) applied++;
                }
            }
            catch { }
            finally
            {
                if (_clientPuppetCombatVisualReplayDepth > 0)
                    _clientPuppetCombatVisualReplayDepth--;
            }
            return applied;
        }

        private static object? TryFindLikelyWeaponObject(object runtimeObject)
        {
            if (runtimeObject == null) return null;
            string[] names = { "weapon", "Weapon", "currentWeapon", "CurrentWeapon", "equippedWeapon", "EquippedWeapon", "heldWeapon", "HeldWeapon", "activeWeapon", "ActiveWeapon", "inventoryItem", "InventoryItem" };
            for (int i = 0; i < names.Length; i++)
            {
                object? value = TryGetMemberValue(runtimeObject, names[i]);
                if (value != null) return value;
            }
            return TryFindComponentByTypeName(runtimeObject, "Weapon");
        }

        private static void TryFaceHostCombatAim(object runtimeObject, NetGameplayEnemyStateSnapshot hostSnapshot)
        {
            if (runtimeObject == null || hostSnapshot == null || !hostSnapshot.HasHostCombatAim) return;
            if (!TryGetTransform(runtimeObject, out var transform) || transform == null) return;
            Vector3 delta = hostSnapshot.HostCombatAimPosition - transform.position;
            delta.y = 0f;
            if (delta.sqrMagnitude < 0.001f) return;
            try { transform.rotation = Quaternion.LookRotation(delta.normalized, Vector3.up); }
            catch { }
        }

        private static void TryApplyClientCombatAnimatorTriggers(EnemyPuppetRecord record, Animator animator, NetGameplayEnemyStateSnapshot hostSnapshot, float now)
        {
            if (record == null || animator == null || hostSnapshot == null) return;
            if (record.CombatTriggerParamHashes.Count == 0) return;
            if (record.LastAppliedCombatTriggerSequence == hostSnapshot.Sequence && now - record.LastAppliedCombatTriggerAt < 0.50f) return;
            if (now - record.LastAppliedCombatTriggerAt < 0.08f) return;

            for (int i = 0; i < record.CombatTriggerParamHashes.Count; i++)
            {
                try { animator.SetTrigger(record.CombatTriggerParamHashes[i]); }
                catch { }
            }
            record.LastAppliedCombatTriggerSequence = hostSnapshot.Sequence;
            record.LastAppliedCombatTriggerAt = now;
            _clientCombatAnimatorTriggerApplies++;
        }

        private static bool IsKnownCombatTriggerParameterName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            return string.Equals(name, "Attack", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Shoot", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Shooting", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Fire", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Melee", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Attacking", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "MeleeAttack", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "WeaponAttack", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "RangedAttack", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "TriggerAttack", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "TriggerShoot", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsEnemyAnimationMirrorApplyEnabled()
        {
            try
            {
                return Plugin.Cfg.EnableHostEnemyAnimationMirror.Value
                    && Plugin.Cfg.ApplyReceivedEnemyAnimationMirror.Value
                    && IsClientEnemyPuppetModeEnabled();
            }
            catch { return false; }
        }

        private static bool TryGetCachedAnimator(EnemyPuppetRecord record, object runtimeObject, out Animator? animator)
        {
            animator = null;
            try
            {
                if (record.CachedAnimator != null)
                {
                    if (record.CachedAnimator is UnityEngine.Object cachedUnity && cachedUnity != null)
                    {
                        animator = record.CachedAnimator;
                        return true;
                    }

                    record.CachedAnimator = null;
                    record.CachedAnimatorId = 0;
                    record.AnimatorParamCacheInitialized = false;
                }

                if (!TryGetAnimator(runtimeObject, out animator) || animator == null)
                    return false;

                record.CachedAnimator = animator;
                record.CachedAnimatorId = ObjectIdentity(animator);
                record.AnimatorParamCacheInitialized = false;
                return true;
            }
            catch
            {
                animator = null;
                return false;
            }
        }

        private static void EnsureAnimatorParameterCache(EnemyPuppetRecord record, Animator animator)
        {
            if (record.AnimatorParamCacheInitialized) return;

            record.MovingParamHash = Animator.StringToHash("Moving");
            record.AttackParamHash = Animator.StringToHash("Attack");
            record.CoweringParamHash = Animator.StringToHash("Cowering");

            record.HasMovingParam = false;
            record.HasAttackParam = false;
            record.HasCoweringParam = false;

            try
            {
                record.CombatTriggerParamHashes.Clear();
                foreach (var parameter in animator.parameters)
                {
                    if (parameter == null)
                        continue;

                    if (parameter.type == AnimatorControllerParameterType.Bool)
                    {
                        if (parameter.nameHash == record.MovingParamHash)
                            record.HasMovingParam = true;
                        else if (parameter.nameHash == record.AttackParamHash)
                            record.HasAttackParam = true;
                        else if (parameter.nameHash == record.CoweringParamHash)
                            record.HasCoweringParam = true;
                    }
                    else if (parameter.type == AnimatorControllerParameterType.Trigger && IsKnownCombatTriggerParameterName(parameter.name))
                    {
                        if (!record.CombatTriggerParamHashes.Contains(parameter.nameHash))
                            record.CombatTriggerParamHashes.Add(parameter.nameHash);
                    }
                }
            }
            catch
            {
                // If parameters are not available, leave all flags false.
            }

            record.AnimatorParamCacheInitialized = true;
        }

        private static bool TryGetAnimator(object target, out Animator? animator)
        {
            animator = null;
            if (target == null) return false;
            try
            {
                if (target is UnityEngine.Object unityObject && unityObject == null) return false;

                object? direct = TryGetMemberValue(target, "animator")
                    ?? TryGetMemberValue(target, "Animator");
                if (direct is Animator directAnimator && directAnimator != null)
                {
                    animator = directAnimator;
                    return true;
                }

                if (target is Component component && component != null)
                {
                    animator = component.GetComponent<Animator>();
                    if (animator != null) return true;
                    animator = component.GetComponentInChildren<Animator>(true);
                    return animator != null;
                }

                if (target is GameObject gameObject && gameObject != null)
                {
                    animator = gameObject.GetComponent<Animator>();
                    if (animator != null) return true;
                    animator = gameObject.GetComponentInChildren<Animator>(true);
                    return animator != null;
                }
            }
            catch { }
            return false;
        }

        private static bool TryGetAnimatorBool(Animator animator, string parameterName, out bool value)
        {
            value = false;
            try
            {
                if (!AnimatorHasParameter(animator, parameterName, AnimatorControllerParameterType.Bool)) return false;
                value = animator.GetBool(parameterName);
                return true;
            }
            catch { return false; }
        }

        private static bool TrySetAnimatorBool(Animator animator, string parameterName, bool value)
        {
            try
            {
                if (!AnimatorHasParameter(animator, parameterName, AnimatorControllerParameterType.Bool)) return false;
                animator.SetBool(parameterName, value);
                return true;
            }
            catch { return false; }
        }

        private static bool AnimatorHasParameter(Animator animator, string parameterName, AnimatorControllerParameterType type)
        {
            if (animator == null) return false;
            try
            {
                var parameters = animator.parameters;
                if (parameters == null) return false;
                for (int i = 0; i < parameters.Length; i++)
                {
                    var p = parameters[i];
                    if (p != null && p.type == type && string.Equals(p.name, parameterName, StringComparison.Ordinal))
                        return true;
                }
            }
            catch { }
            return false;
        }

        private static float Fraction01(float value)
        {
            if (!IsFinite(value)) return 0f;
            value = value - Mathf.Floor(value);
            if (value < 0f) value += 1f;
            return Mathf.Clamp01(value);
        }

        private static float FractionDistance(float a, float b)
        {
            float d = Mathf.Abs(Fraction01(a) - Fraction01(b));
            return Mathf.Min(d, 1f - d);
        }

        private static string BoolText(bool has, bool value)
        {
            return has ? (value ? "true" : "false") : "n/a";
        }

        private static void ReleaseStaleEnemyPuppets()
        {
            if (ActiveEnemyPuppets.Count == 0) return;

            if (!IsClientEnemyPuppetModeEnabled())
            {
                ReleaseAllEnemyPuppets("puppet mode disabled");
                return;
            }

            float timeout = Plugin.Cfg.ClientEnemyPuppetStaleReleaseSeconds.Value;
            if (timeout <= 0f) return;

            float now = Time.realtimeSinceStartup;
            foreach (var pair in ActiveEnemyPuppets.ToArray())
            {
                if (now - pair.Value.LastSeenAt <= timeout) continue;
                // Host owns death/despawn — don't release a host-bound puppet on stale timer.
                bool isHostBound = ClientLocalKeyToHostSpawnIndex.TryGetValue(pair.Key, out int boundHostIdx);
                // Phase 5.7-DB: an ORPHANED binding — this local key still claims hostIdx=H, but the forward map for H
                // now points at a DIFFERENT local key (H re-bound to another local entity). This puppet will never get
                // another snapshot (recv=never) yet "host-bound" keeps suppressing its release → frozen zombie. Release it.
                bool orphanDisowned = isHostBound && Plugin.Cfg.EvictStaleHostBindings.Value
                    && (!ClientHostToLocalKeyByHostSpawnIndex.TryGetValue(boundHostIdx, out var fwdLocal)
                        || !string.Equals(fwdLocal, pair.Key, StringComparison.Ordinal));
                // DB2: bound to a host enemy the client already buried — it will never get another snapshot. Release it.
                bool boundToDead = isHostBound && IsClientKnownDeadHostIdx(boundHostIdx);
                if (orphanDisowned || boundToDead)
                {
                    ClientLocalKeyToHostSpawnIndex.Remove(pair.Key);
                    if (ClientHostToLocalKeyByHostSpawnIndex.TryGetValue(boundHostIdx, out var fl)
                        && string.Equals(fl, pair.Key, StringComparison.Ordinal))
                        ClientHostToLocalKeyByHostSpawnIndex.Remove(boundHostIdx);
                    _evictedStaleHostBindings++;
                    string why = boundToDead ? "bound to buried hostIdx" : "forward map disowned";
                    ReleaseEnemyPuppet(pair.Key, $"stale orphan binding hostIdx={boundHostIdx} ({why}, dbEvicted={_evictedStaleHostBindings})");
                    continue;
                }
                if (isHostBound)
                {
                    _puppetStaleReleaseSuppressed++;
                    float staleAge = now - pair.Value.LastSeenAt;
                    // Reset stale timer — host snapshots may arrive intermittently but binding still valid.
                    pair.Value.LastSeenAt = now;
                    if (Plugin.Cfg.LogClientEnemyPuppetMode.Value)
                    {
                        // SC2: split the cause — was a snapshot RECEIVED for this hostIdx recently (→ received-not-applied)
                        // or not (→ host not sending it)?
                        string recvTag = "recv=never";
                        if (ClientLocalKeyToHostSpawnIndex.TryGetValue(pair.Key, out int hostIdx)
                            && _clientLastSnapshotRecvByHostIdx.TryGetValue(hostIdx, out float lastRecv))
                            recvTag = $"hostIdx={hostIdx} lastRecv={now - lastRecv:F2}s";
                        else if (ClientLocalKeyToHostSpawnIndex.TryGetValue(pair.Key, out int hi2))
                            recvTag = $"hostIdx={hi2} recv=never";
                        NetLogger.Info($"[EnemyPuppet] Stale-release suppressed (host-bound) idx={pair.Value.Snapshot.SpawnIndex} actor={pair.Value.Snapshot.ActorName} stale={staleAge:F2}s {recvTag} — timer reset");
                    }
                }
                else
                    ReleaseEnemyPuppet(pair.Key, $"stale for {now - pair.Value.LastSeenAt:F2}s (unbound)");
            }
        }

        private static void ReleaseAllEnemyPuppets(string reason)
        {
            if (ActiveEnemyPuppets.Count == 0)
            {
                ClientPuppetNpcIds.Clear();
                ClientPuppetAiAgentIds.Clear();
                BarrenTargetAuthorityAiIds.Clear();
                ClientPuppetMovementDriverIds.Clear();
                ActiveEnemyPuppetsByNpcId.Clear();
                return;
            }

            foreach (var key in ActiveEnemyPuppets.Keys.ToArray())
                ReleaseEnemyPuppet(key, reason);

            ClientPuppetNpcIds.Clear();
            ClientPuppetAiAgentIds.Clear();
            BarrenTargetAuthorityAiIds.Clear();
            ClientPuppetMovementDriverIds.Clear();
            ActiveEnemyPuppetsByNpcId.Clear();
        }

        // Phase 5.7-SC3: on a confirmed host death, release the bound client puppet and drop its binding so it can't
        // linger as a stale "host-bound" zombie. Called after Die() is applied. _releasedPuppetsOnHostDeath counts it.
        private static int _releasedPuppetsOnHostDeath;
        private static void ReleaseClientEnemyPuppetOnHostDeath(NetGameplayEntitySnapshot snapshot, int hostIdx, string reason)
        {
            try
            {
                string localKey = GetSnapshotTargetKey(snapshot);
                if (!string.IsNullOrWhiteSpace(localKey))
                {
                    if (ActiveEnemyPuppets.ContainsKey(localKey))
                    {
                        ReleaseEnemyPuppet(localKey, "host death (" + reason + ")");
                        _releasedPuppetsOnHostDeath++;
                    }
                    // Drop the binding both ways so ReleaseStaleEnemyPuppets no longer treats it as host-bound.
                    if (ClientLocalKeyToHostSpawnIndex.TryGetValue(localKey, out int boundHostIdx))
                    {
                        ClientLocalKeyToHostSpawnIndex.Remove(localKey);
                        ClientHostToLocalKeyByHostSpawnIndex.Remove(boundHostIdx);
                        _runtimeSpawnBindingsByHostIdx.Remove(boundHostIdx);
                    }
                }
                if (hostIdx > 0)
                {
                    if (ClientHostToLocalKeyByHostSpawnIndex.TryGetValue(hostIdx, out var hk))
                        ClientLocalKeyToHostSpawnIndex.Remove(hk);
                    ClientHostToLocalKeyByHostSpawnIndex.Remove(hostIdx);
                    _runtimeSpawnBindingsByHostIdx.Remove(hostIdx);
                    _clientLastSnapshotRecvByHostIdx.Remove(hostIdx);
                }
            }
            catch { }
        }

        private static void ReleaseEnemyPuppet(string key, string reason)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            if (!ActiveEnemyPuppets.TryGetValue(key, out var record)) return;

            ActiveEnemyPuppets.Remove(key);
            if (record.NpcId != 0)
            {
                ClientPuppetNpcIds.Remove(record.NpcId);
                ActiveEnemyPuppetsByNpcId.Remove(record.NpcId);
            }
            if (record.AiAgentId != 0)
            {
                ClientPuppetAiAgentIds.Remove(record.AiAgentId);
                BarrenTargetAuthorityAiIds.Remove(record.AiAgentId);
            }
            if (record.MovementDriverId != 0) ClientPuppetMovementDriverIds.Remove(record.MovementDriverId);

            object? npc = record.Npc;
            object? aiAgent = record.AiAgent;
            if (npc is UnityEngine.Object npcUnity && npcUnity == null) npc = null;
            if (aiAgent is UnityEngine.Object aiUnity && aiUnity == null) aiAgent = null;

            if (npc != null)
            {
                if (record.OriginalDisableVerifyPosition.HasValue)
                    TrySetBoolMember(npc, "disableVerifyPosition", record.OriginalDisableVerifyPosition.Value);
                if (record.OriginalPreventNavMeshActivation.HasValue)
                    TrySetBoolMember(npc, "preventNavMeshActivation", record.OriginalPreventNavMeshActivation.Value);
            }

            if (aiAgent != null)
            {
                TryInvokeInstanceMethod(aiAgent, "SetCanMove", true);
                TryInvokeInstanceMethod(aiAgent, "SetNavMeshAgentState", true);
                TryInvokeInstanceMethod(aiAgent, "ToggleRVO", true);
            }

            if (npc != null)
                TryInvokeInstanceMethod(npc, "ToggleBehaviourTree", true);

            RestorePuppetRigidbody(record); // RT3-A3: undo the kinematic override if the enemy survives release

            // Phase 5.2: record tombstone so death events arriving after puppet release can be
            // classified as "puppet destroyed" rather than "never bound".
            int releaseSpawnIdx = record.Snapshot.SpawnIndex;
            if (releaseSpawnIdx > 0 && ClientHostToLocalKeyByHostSpawnIndex.ContainsKey(releaseSpawnIdx))
            {
                _bindingTombstones[releaseSpawnIdx] = new BindingTombstone
                {
                    HostSpawnIndex = releaseSpawnIdx,
                    LocalKey       = key,
                    UnitIdentifier = record.Snapshot.EntityId.UnitIdentifier ?? "",
                    ReleasedAt     = Time.realtimeSinceStartup,
                    ReleaseReason  = reason,
                };
            }

            _clientEnemyPuppetsReleased++;
            // Always log releases so lifecycle can be correlated against HealthState/DeathEvent.
            bool wasHostBound = ClientLocalKeyToHostSpawnIndex.ContainsKey(key);
            NetLogger.Info($"[EnemyPuppet] {NetDbg.Ctx("Release")} idx={record.Snapshot.SpawnIndex} actor={record.Snapshot.ActorName} hostBound={wasHostBound} reason={Clean(reason)}");
        }

        private static int ObjectIdentity(object? obj)
        {
            if (obj == null) return 0;
            try
            {
                if (obj is UnityEngine.Object unityObject)
                    return unityObject == null ? 0 : unityObject.GetInstanceID();
            }
            catch { }
            return RuntimeHelpers.GetHashCode(obj);
        }

        private static object? TryFindComponentByTypeName(object owner, string typeName)
        {
            try
            {
                Component? component = null;
                if (owner is Component c) component = c;
                else if (owner is GameObject go) component = go.GetComponent<Component>();

                if (component == null) return null;
                var components = component.GetComponentsInChildren<Component>(true);
                foreach (var item in components)
                {
                    if (item == null) continue;
                    Type type = item.GetType();
                    if (type.Name == typeName || (type.FullName != null && type.FullName.EndsWith("." + typeName, StringComparison.Ordinal)))
                        return item;
                }
            }
            catch { }
            return null;
        }

        private static bool TryInvokeInstanceMethod(object? target, string methodName, params object[] args)
        {
            if (target == null) return false;
            try
            {
                if (target is UnityEngine.Object unityObject && unityObject == null) return false;
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                Type[] argTypes = args.Select(a => a == null ? typeof(object) : a.GetType()).ToArray();
                for (Type? current = target.GetType(); current != null; current = current.BaseType)
                {
                    MethodInfo? method = null;
                    try { method = current.GetMethod(methodName, flags, null, argTypes, null); }
                    catch { }
                    if (method == null)
                    {
                        foreach (var candidate in current.GetMethods(flags).Where(m => m.Name == methodName))
                        {
                            var parameters = candidate.GetParameters();
                            if (parameters.Length != args.Length) continue;
                            bool compatible = true;
                            for (int i = 0; i < parameters.Length; i++)
                            {
                                if (args[i] == null) continue;
                                if (!parameters[i].ParameterType.IsInstanceOfType(args[i]))
                                {
                                    compatible = false;
                                    break;
                                }
                            }
                            if (compatible)
                            {
                                method = candidate;
                                break;
                            }
                        }
                    }
                    if (method == null) continue;
                    method.Invoke(target, args);
                    return true;
                }
            }
            catch { }
            return false;
        }

        // Invoke a no-argument instance method that returns a Vector3 (e.g. Npc.GetAimPosition()).
        // Used to read the real host aim point instead of reflecting raw target transforms.
        private static bool TryInvokeVector3Method(object? target, string methodName, out Vector3 result)
        {
            result = Vector3.zero;
            if (target == null) return false;
            try
            {
                if (target is UnityEngine.Object unityObject && unityObject == null) return false;
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                for (Type? current = target.GetType(); current != null; current = current.BaseType)
                {
                    MethodInfo? method = null;
                    try { method = current.GetMethod(methodName, flags, null, Type.EmptyTypes, null); }
                    catch { }
                    if (method == null || method.ReturnType != typeof(Vector3)) continue;
                    object? value = method.Invoke(target, null);
                    if (value is Vector3 v)
                    {
                        result = v;
                        return true;
                    }
                    return false;
                }
            }
            catch { }
            return false;
        }

        private static bool TryGetBoolMember(object? target, string memberName, out bool value)
        {
            value = false;
            if (target == null) return false;
            try
            {
                if (target is UnityEngine.Object unityObject && unityObject == null) return false;
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                for (Type? current = target.GetType(); current != null; current = current.BaseType)
                {
                    var field = current.GetField(memberName, flags);
                    if (field != null && field.FieldType == typeof(bool))
                    {
                        value = (bool)field.GetValue(target);
                        return true;
                    }

                    var prop = current.GetProperty(memberName, flags);
                    if (prop != null && prop.PropertyType == typeof(bool) && prop.GetIndexParameters().Length == 0)
                    {
                        value = (bool)prop.GetValue(target, null);
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static bool TrySetBoolMember(object? target, string memberName, bool value)
        {
            if (target == null) return false;
            try
            {
                if (target is UnityEngine.Object unityObject && unityObject == null) return false;
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                for (Type? current = target.GetType(); current != null; current = current.BaseType)
                {
                    var field = current.GetField(memberName, flags);
                    if (field != null && field.FieldType == typeof(bool))
                    {
                        field.SetValue(target, value);
                        return true;
                    }

                    var prop = current.GetProperty(memberName, flags);
                    if (prop != null && prop.PropertyType == typeof(bool) && prop.CanWrite && prop.GetIndexParameters().Length == 0)
                    {
                        prop.SetValue(target, value, null);
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static bool TrySetObjectMember(object? target, string memberName, object? value)
        {
            if (target == null) return false;
            try
            {
                if (target is UnityEngine.Object unityObject && unityObject == null) return false;
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                for (Type? current = target.GetType(); current != null; current = current.BaseType)
                {
                    var field = current.GetField(memberName, flags);
                    if (field != null)
                    {
                        if (value == null && field.FieldType.IsValueType && Nullable.GetUnderlyingType(field.FieldType) == null)
                            return false;
                        if (value != null && !field.FieldType.IsInstanceOfType(value))
                            return false;
                        field.SetValue(target, value);
                        return true;
                    }

                    var prop = current.GetProperty(memberName, flags);
                    if (prop != null && prop.CanWrite && prop.GetIndexParameters().Length == 0)
                    {
                        if (value == null && prop.PropertyType.IsValueType && Nullable.GetUnderlyingType(prop.PropertyType) == null)
                            return false;
                        if (value != null && !prop.PropertyType.IsInstanceOfType(value))
                            return false;
                        prop.SetValue(target, value, null);
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static void TryDisableBehaviourComponent(object? maybeBehaviour)
        {
            try
            {
                if (maybeBehaviour is Behaviour behaviour && behaviour != null)
                    behaviour.enabled = false;
            }
            catch { }
        }

        private static void TryDisableNavMeshObject(object? navMeshAgent)
        {
            if (navMeshAgent == null) return;
            try
            {
                if (navMeshAgent is UnityEngine.Object unityObject && unityObject == null) return;
                TrySetBoolMember(navMeshAgent, "canMove", false);
                TrySetBoolMember(navMeshAgent, "enabled", false);
                TrySetBoolMember(navMeshAgent, "updatePosition", false);
                TrySetBoolMember(navMeshAgent, "updateRotation", false);
            }
            catch { }
        }

        private static void TryDisableRvoController(object? rvoController)
        {
            if (rvoController == null) return;
            TrySetBoolMember(rvoController, "enabled", false);
        }

        private static void TryZeroRigidbodyVelocity(object? rigidbody)
        {
            if (rigidbody == null) return;
            try
            {
                if (rigidbody is UnityEngine.Object unityObject && unityObject == null) return;

                // Unity 6 logs warnings when velocity/angularVelocity is assigned on kinematic bodies.
                // Puppet enemies are often already kinematic after NavMesh/BT shutdown, and repeated
                // writes here can spam tens of thousands of warnings without improving sync.
                if (TryGetBoolMember(rigidbody, "isKinematic", out var isKinematic) && isKinematic)
                    return;

                Vector3 zero = Vector3.zero;
                TrySetVector3Member(rigidbody, "velocity", zero);
                TrySetVector3Member(rigidbody, "linearVelocity", zero);
                TrySetVector3Member(rigidbody, "angularVelocity", zero);
            }
            catch { }
        }

        // RT3-A3: make a host-driven puppet's Rigidbody kinematic so transform-drags can't impart physics impulses.
        private static void MakePuppetRigidbodyKinematic(EnemyPuppetRecord record, object npc)
        {
            try
            {
                if (!Plugin.Cfg.MakeClientPuppetsKinematic.Value) return;
                var rb = TryGetMemberValue(npc, "Rigidbody") ?? TryGetMemberValue(npc, "rigidbody");
                if (rb == null) return;
                if (rb is UnityEngine.Object uo && uo == null) return;
                if (!record.OriginalRigidbodyIsKinematic.HasValue && TryGetBoolMember(rb, "isKinematic", out var original))
                    record.OriginalRigidbodyIsKinematic = original;
                TrySetBoolMember(rb, "isKinematic", true);
            }
            catch { }
        }

        // RT3-A3: restore the Rigidbody's original isKinematic when the puppet is released (best-effort; the object is
        // usually destroyed, but a surviving enemy must not be left permanently kinematic).
        private static void RestorePuppetRigidbody(EnemyPuppetRecord record)
        {
            try
            {
                if (!record.OriginalRigidbodyIsKinematic.HasValue) return;
                object? npc = record.Npc;
                if (npc == null || (npc is UnityEngine.Object uo && uo == null)) return;
                var rb = TryGetMemberValue(npc, "Rigidbody") ?? TryGetMemberValue(npc, "rigidbody");
                if (rb == null || (rb is UnityEngine.Object ruo && ruo == null)) return;
                TrySetBoolMember(rb, "isKinematic", record.OriginalRigidbodyIsKinematic.Value);
            }
            catch { }
        }

        private static bool TryGetVector3Member(object? target, string memberName, out Vector3 value)
        {
            value = Vector3.zero;
            if (target == null) return false;
            try
            {
                if (target is UnityEngine.Object unityObject && unityObject == null) return false;
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                for (Type? current = target.GetType(); current != null; current = current.BaseType)
                {
                    var field = current.GetField(memberName, flags);
                    if (field != null && field.FieldType == typeof(Vector3))
                    {
                        value = (Vector3)field.GetValue(target);
                        return IsFinite(value);
                    }

                    var prop = current.GetProperty(memberName, flags);
                    if (prop != null && prop.PropertyType == typeof(Vector3) && prop.GetIndexParameters().Length == 0)
                    {
                        value = (Vector3)prop.GetValue(target, null);
                        return IsFinite(value);
                    }
                }
            }
            catch { }
            value = Vector3.zero;
            return false;
        }

        private static bool TrySetVector3Member(object? target, string memberName, Vector3 value)
        {
            if (target == null) return false;
            try
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                for (Type? current = target.GetType(); current != null; current = current.BaseType)
                {
                    var field = current.GetField(memberName, flags);
                    if (field != null && field.FieldType == typeof(Vector3))
                    {
                        field.SetValue(target, value);
                        return true;
                    }

                    var prop = current.GetProperty(memberName, flags);
                    if (prop != null && prop.PropertyType == typeof(Vector3) && prop.CanWrite && prop.GetIndexParameters().Length == 0)
                    {
                        prop.SetValue(target, value, null);
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static string GetSnapshotTargetKey(NetGameplayEntitySnapshot snapshot)
        {
            if (!string.IsNullOrWhiteSpace(snapshot.EntityId.LocalInstanceId) && snapshot.EntityId.LocalInstanceId != "null")
                return snapshot.EntityId.LocalInstanceId;
            if (!string.IsNullOrWhiteSpace(snapshot.EntityId.CandidateKey))
                return snapshot.EntityId.CandidateKey;
            return snapshot.SpawnIndex > 0 ? "spawn:" + snapshot.SpawnIndex : "";
        }

        private static bool TryFindLocalEnemyStateMatch(NetGameplayEnemyStateSnapshot hostSnapshot, out NetGameplayEntitySnapshot? snapshot, out string detail)
        {
            snapshot = null;
            detail = "";

            // O3-B: If Host roster binding is available, use it as primary lookup.
            if (ClientHostToLocalKeyByHostSpawnIndex.TryGetValue(hostSnapshot.SpawnIndex, out var boundKey)
                && EntitiesByLocalId.TryGetValue(boundKey, out var boundSnapshot))
            {
                // Verify the bound snapshot hasn't diverged in unitId.
                if (!string.IsNullOrWhiteSpace(hostSnapshot.UnitIdentifier)
                    && !string.IsNullOrWhiteSpace(boundSnapshot.EntityId.UnitIdentifier)
                    && !string.Equals(hostSnapshot.UnitIdentifier, boundSnapshot.EntityId.UnitIdentifier, StringComparison.Ordinal))
                {
                    _stateApplyRejectedTypeMismatch++;
                    _entityTypeMismatchRejected++;
                    detail = $"roster-bound but unitId mismatch host={hostSnapshot.UnitIdentifier} client={boundSnapshot.EntityId.UnitIdentifier}";
                    Plugin.Log.Info($"[EnemyState] Rejected type mismatch hostUnit={hostSnapshot.UnitIdentifier} clientUnit={boundSnapshot.EntityId.UnitIdentifier} hostIdx={hostSnapshot.SpawnIndex}");
                    return false;
                }
                snapshot = boundSnapshot;
                RefreshRuntimePositionIfAvailable(snapshot);
                detail = "roster-bound match";
                return true;
            }

            int candidateCount = 0;
            NetGameplayEntitySnapshot? uniqueGlobal = null;
            int globalMatches = 0;
            NetGameplayEntitySnapshot? uniqueCandidateKey = null;
            int keyMatches = 0;

            bool wantGlobal = !string.IsNullOrWhiteSpace(hostSnapshot.UnitGlobalId);
            bool wantKey = !string.IsNullOrWhiteSpace(hostSnapshot.CandidateKey);

            foreach (var candidate in EntitiesByLocalId.Values)
            {
                if (!string.Equals(candidate.Category, hostSnapshot.Category, StringComparison.OrdinalIgnoreCase))
                    continue;

                candidateCount++;

                if (candidate.SpawnIndex == hostSnapshot.SpawnIndex)
                {
                    // O3-B: Reject spawnIndex match if unitIds are both present and differ.
                    if (!string.IsNullOrWhiteSpace(hostSnapshot.UnitIdentifier)
                        && !string.IsNullOrWhiteSpace(candidate.EntityId.UnitIdentifier)
                        && !string.Equals(hostSnapshot.UnitIdentifier, candidate.EntityId.UnitIdentifier, StringComparison.Ordinal))
                    {
                        _stateApplyRejectedTypeMismatch++;
                        _entityTypeMismatchRejected++;
                        if (Plugin.Cfg.EnableDebugLog.Value)
                            Plugin.Log.Info($"[EnemyState] Rejected type mismatch hostUnit={hostSnapshot.UnitIdentifier} clientUnit={candidate.EntityId.UnitIdentifier} hostIdx={hostSnapshot.SpawnIndex}");
                        continue; // skip this candidate, keep searching
                    }
                    snapshot = candidate;
                    RefreshRuntimePositionIfAvailable(snapshot);
                    detail = "spawnIndex match";
                    return true;
                }

                if (wantGlobal && string.Equals(candidate.EntityId.UnitGlobalId, hostSnapshot.UnitGlobalId, StringComparison.Ordinal))
                {
                    uniqueGlobal = candidate;
                    globalMatches++;
                }

                if (wantKey && string.Equals(candidate.EntityId.CandidateKey, hostSnapshot.CandidateKey, StringComparison.Ordinal))
                {
                    uniqueCandidateKey = candidate;
                    keyMatches++;
                }
            }

            if (globalMatches == 1 && uniqueGlobal != null)
            {
                snapshot = uniqueGlobal;
                RefreshRuntimePositionIfAvailable(snapshot);
                detail = "unique UnitGlobalId fallback match";
                return true;
            }

            if (keyMatches == 1 && uniqueCandidateKey != null)
            {
                snapshot = uniqueCandidateKey;
                RefreshRuntimePositionIfAvailable(snapshot);
                detail = "unique candidateKey fallback match";
                return true;
            }

            detail = $"no local entity match candidates={candidateCount}";
            return false;
        }

        private static void RefreshRuntimePositionIfAvailable(NetGameplayEntitySnapshot snapshot)
        {
            try
            {
                if (snapshot == null) return;
                if (!snapshot.TryGetRuntimeObject(out var runtimeObject) || runtimeObject == null) return;
                if (runtimeObject is UnityEngine.Object unityObject && unityObject == null) return;
                if (TryGetPosition(runtimeObject, out var currentPosition))
                {
                    snapshot.HasPosition = true;
                    snapshot.Position = currentPosition;
                }
            }
            catch { }
        }

        private static bool CanLogWithCurrentContext(out string reason)
        {
            reason = "";
            if (!Plugin.Cfg.RequireStableSceneAndSeedForGameplayProbe.Value) return true;

            if (!NetRunStateBridge.TryGetLocalRunState(out var state))
            {
                reason = "no local run state";
                return false;
            }

            if (!state.HasLevel)
            {
                reason = "no chapter/level";
                return false;
            }

            if (Plugin.Cfg.EnableLevelSeedAuthority.Value && !state.HasLevelSeed)
            {
                reason = "level seed unknown";
                return false;
            }

            return true;
        }

        private static MethodInfo? FindNoArgInstanceMethod(Type type, string methodName)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            for (Type? current = type; current != null; current = current.BaseType)
            {
                try
                {
                    var method = current.GetMethod(methodName, flags, null, Type.EmptyTypes, null);
                    if (method != null) return method;
                }
                catch { }
            }

            return null;
        }

        private static bool IsEnabled()
        {
            try { return Plugin.Cfg.EnableGameplayEntityProbe.Value; }
            catch { return false; }
        }

        private static bool ShouldTrackCategory(string category)
        {
            if (category == "Player") return false;
            if (category == "Breakable") return false;
            return true;
        }

        // ---- Phase 4.4.0-O3-B: Host world roster ----

        public static List<NetWorldEntityRecord> BuildHostWorldRoster()
        {
            var result = new List<NetWorldEntityRecord>();
            if (!IsEnabled()) return result;
            if (!NetRunStateBridge.TryGetLocalRunState(out var state) || !state.HasLevel) return result;

            foreach (var snapshot in EntitiesByLocalId.Values
                         .Where(s => !s.IsDead)
                         .OrderBy(s => s.SpawnIndex))
            {
                // Refresh runtime position
                if (snapshot.TryGetRuntimeObject(out var rto) && rto != null
                    && !(rto is UnityEngine.Object uo && uo == null)
                    && TryGetPosition(rto, out var freshPos))
                {
                    snapshot.HasPosition = true;
                    snapshot.Position = freshPos;
                }

                var rec = new NetWorldEntityRecord
                {
                    NetEntityId    = ComputeNetEntityId(snapshot, state),
                    SyncCategory   = snapshot.SyncCategory,
                    Category       = snapshot.Category,
                    UnitIdentifier = snapshot.EntityId.UnitIdentifier ?? "",
                    ActorName      = snapshot.ActorName ?? "",
                    SpawnIndex     = snapshot.SpawnIndex,
                    HasPosition    = snapshot.HasPosition,
                    Position       = snapshot.Position,
                    SceneRevision  = state.Revision,
                    ChapterName    = state.ChapterName ?? "",
                    LevelIndex     = state.LevelIndex,
                    HasLevelSeed   = state.HasLevelSeed,
                    LevelSeed      = state.LevelSeed,
                };
                result.Add(rec);
            }
            _hostRosterRecordsSent = result.Count;
            return result;
        }

        // ---- Phase 5.5-RT1: runtime spawn sync helpers ----

        private static string LocalKeyForObject(object entity)
        {
            var id = NetGameplayEntityId.FromObject(entity);
            string key = id.LocalInstanceId;
            if (string.IsNullOrWhiteSpace(key) || key == "null") key = id.CandidateKey;
            return key;
        }

        /// <summary>The local SpawnIndex assigned to a tracked entity (0 if not yet registered via ReportSpawn).</summary>
        public static int GetSpawnIndexForObject(object entity)
        {
            try
            {
                if (entity == null) return 0;
                if (EntitiesByLocalId.TryGetValue(LocalKeyForObject(entity), out var snap)) return snap.SpawnIndex;
            }
            catch { }
            return 0;
        }

        /// <summary>Bind a client-mirrored runtime spawn to the host SpawnIndex so the existing EnemyPuppet / state /
        /// death pipeline (all keyed by host SpawnIndex) drives the mirrored unit.</summary>
        public static bool RegisterMirroredRuntimeSpawn(object entity, int hostSpawnIndex)
        {
            try
            {
                if (entity == null || hostSpawnIndex <= 0) return false;
                if (NetConfig.GetMode() != NetMode.Client) return false;
                string key = LocalKeyForObject(entity);
                if (string.IsNullOrWhiteSpace(key)) return false;
                SetClientHostBinding(hostSpawnIndex, key);
                // Phase 5.5-RT3-A6: runtime (post-level-load) boss adds are NOT in the host WorldRoster. ProcessHostWorldRoster
                // wipes the binding dicts every reconcile; without this authoritative record the RT3 binding is lost and the
                // roster then proximity-rebinds the entity to the WRONG hostIdx (log54: -349692 RT3-bound to 25 but death of
                // 24 matched it → corpse, then 25 kept writing health back). Keep it so the reconcile can re-assert it.
                _runtimeSpawnBindingsByHostIdx[hostSpawnIndex] = key;
                return true;
            }
            catch { return false; }
        }

        // Phase 5.7-DB: every binding write must keep ClientHostToLocalKeyByHostSpawnIndex (hostIdx→localKey) and
        // ClientLocalKeyToHostSpawnIndex (localKey→hostIdx) as a strict 1:1 pair. The additive write sites (manifest
        // reconcile, retro-bind, RT3 mirror) previously overwrote one side without evicting the stale reverse entry,
        // so a hostIdx that re-bound to a NEW local entity left the OLD local key still flagged "host-bound". That
        // orphan never received another snapshot (recv=never), went stale at 3s, and ReleaseStaleEnemyPuppets kept
        // suppressing its release because it was "host-bound" → frozen standing zombie (LogOutput116 hostIdx=1: local
        // [3]/[16] Bruisers both mapped to hostIdx=1 while [0] was the live binding). Route all writes through here.
        private static int _evictedStaleHostBindings;
        // Phase 5.7-DB2: the client has already applied a terminal death for this host enemy. Never (re)bind a live local
        // entity to it — a dead host idx that the WorldRoster still lists would otherwise steal the binding of a surviving
        // same-type sibling, which then never receives snapshots (recv=never) while the real sibling's death can't find a
        // local match ("never bound, late-bind failed") → that sibling stands frozen (LogOutput117: two BlackGuildTrackers,
        // hostIdx=12 died but kept re-binding local [16], starving the alive hostIdx=17 tracker → zombie).
        private static bool IsClientKnownDeadHostIdx(int hostIdx)
        {
            return hostIdx > 0
                && Plugin.Cfg.SkipDeadHostIdxRebind.Value
                && _clientTerminalDeadHostIdx.Contains(hostIdx);
        }

        private static void SetClientHostBinding(int hostIdx, string localKey)
        {
            if (hostIdx <= 0 || string.IsNullOrWhiteSpace(localKey)) return;
            if (IsClientKnownDeadHostIdx(hostIdx)) return; // DB2: don't bind a host enemy the client already buried

            if (Plugin.Cfg.EvictStaleHostBindings.Value)
            {
                // 1) This hostIdx is currently bound to a DIFFERENT local key → that old local key is about to become an
                //    orphan. Drop its reverse entry so it is no longer treated as host-bound (frees it via stale-release).
                if (ClientHostToLocalKeyByHostSpawnIndex.TryGetValue(hostIdx, out var prevLocal)
                    && !string.IsNullOrWhiteSpace(prevLocal)
                    && !string.Equals(prevLocal, localKey, StringComparison.Ordinal))
                {
                    ClientLocalKeyToHostSpawnIndex.Remove(prevLocal);
                    _evictedStaleHostBindings++;
                }
                // 2) This local key is currently bound to a DIFFERENT hostIdx → drop that stale forward entry.
                if (ClientLocalKeyToHostSpawnIndex.TryGetValue(localKey, out var prevHost)
                    && prevHost != hostIdx
                    && ClientHostToLocalKeyByHostSpawnIndex.TryGetValue(prevHost, out var prevHostLocal)
                    && string.Equals(prevHostLocal, localKey, StringComparison.Ordinal))
                {
                    ClientHostToLocalKeyByHostSpawnIndex.Remove(prevHost);
                    _evictedStaleHostBindings++;
                }
            }

            ClientHostToLocalKeyByHostSpawnIndex[hostIdx] = localKey;
            ClientLocalKeyToHostSpawnIndex[localKey] = hostIdx;
        }

        private static string ComputeNetEntityId(NetGameplayEntitySnapshot snapshot, NetRunState state)
        {
            try
            {
                int seed = state.HasLevelSeed ? state.LevelSeed : 0;
                if (snapshot.HasPosition)
                {
                    int qx = Mathf.RoundToInt(snapshot.Position.x * 4);
                    int qy = Mathf.RoundToInt(snapshot.Position.y * 4);
                    int qz = Mathf.RoundToInt(snapshot.Position.z * 4);
                    return $"{state.ChapterName}|{state.LevelIndex}|{seed}|{snapshot.EntityId.UnitIdentifier}|{snapshot.SpawnIndex}|{qx}|{qy}|{qz}";
                }
                return $"{state.ChapterName}|{state.LevelIndex}|{seed}|{snapshot.EntityId.UnitIdentifier}|{snapshot.SpawnIndex}";
            }
            catch { return $"idx:{snapshot.SpawnIndex}"; }
        }

        // ── Phase 5.7-RB retro-bind ledger ────────────────────────────────────
        // Park an unmatched host entry so a later local spawn of the same unit can bind it.
        private static void RecordPendingHostBind(int hostIdx, string? unitId, string? category, bool hasPos, Vector3 pos)
        {
            if (!Plugin.Cfg.EnableRetroactiveEnemyBinding.Value) return;
            if (string.IsNullOrWhiteSpace(unitId)) return;                          // need a unitId for a safe retro-match
            if (ClientHostToLocalKeyByHostSpawnIndex.ContainsKey(hostIdx)) return;  // already bound — nothing to park
            _pendingHostBindLedger[hostIdx] = new PendingHostBind
            {
                HostSpawnIndex = hostIdx,
                UnitIdentifier = unitId!,
                Category       = category ?? "",
                HasPosition    = hasPos,
                Position       = pos,
                RecordedAt     = Time.realtimeSinceStartup,
            };
            _retroBindLedgerAdds++;
        }

        // Drop ledger entries whose hostIdx got bound (by this or a prior reconcile pass).
        private static void PruneBoundPendingHostBinds()
        {
            if (_pendingHostBindLedger.Count == 0) return;
            List<int>? remove = null;
            foreach (var kv in _pendingHostBindLedger)
                if (ClientHostToLocalKeyByHostSpawnIndex.ContainsKey(kv.Key))
                    (remove ??= new List<int>()).Add(kv.Key);
            if (remove != null) foreach (var k in remove) _pendingHostBindLedger.Remove(k);
        }

        // Expire entries the client never produced a local match for (host enemy despawned, or genuinely divergent spawn).
        private static void ExpireStalePendingHostBinds()
        {
            if (_pendingHostBindLedger.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            List<int>? remove = null;
            foreach (var kv in _pendingHostBindLedger)
                if (now - kv.Value.RecordedAt > RetroBindLedgerTtlSeconds)
                    (remove ??= new List<int>()).Add(kv.Key);
            if (remove != null)
            {
                foreach (var k in remove) _pendingHostBindLedger.Remove(k);
                _retroBindLedgerExpired += remove.Count;
            }
        }

        // Called from ReportSpawn (Client) when a NEW local entity appears: bind it to a parked host record if one matches.
        private static void TryRetroactiveBindNewLocalEntity(NetGameplayEntitySnapshot snap)
        {
            if (!Plugin.Cfg.EnableRetroactiveEnemyBinding.Value) return;
            if (_pendingHostBindLedger.Count == 0) return;
            if (snap == null || snap.IsDead) return;
            if (IsPlayerEntitySnapshot(snap)) return;
            string unitId = snap.EntityId.UnitIdentifier ?? "";
            if (string.IsNullOrWhiteSpace(unitId)) return;

            string localKey = GetSnapshotTargetKey(snap);
            if (string.IsNullOrWhiteSpace(localKey)) return;
            if (ClientLocalKeyToHostSpawnIndex.ContainsKey(localKey)) return; // already bound to a host idx

            int bestHostIdx = -1;
            float bestDist = float.MaxValue;
            int matchCount = 0;          // unbound parked entries of this unitId
            int sameUnitInLedger = 0;    // all parked entries of this unitId (bound or not)
            foreach (var kv in _pendingHostBindLedger)
            {
                var p = kv.Value;
                if (!string.Equals(p.UnitIdentifier, unitId, StringComparison.Ordinal)) continue;
                if (IsClientKnownDeadHostIdx(p.HostSpawnIndex)) continue; // DB2: never retro-bind to a buried host idx
                sameUnitInLedger++;
                if (ClientHostToLocalKeyByHostSpawnIndex.ContainsKey(p.HostSpawnIndex)) continue; // bound since parked
                matchCount++;
                float dist = (p.HasPosition && snap.HasPosition) ? Vector3.Distance(p.Position, snap.Position) : 0f;
                if (dist < bestDist) { bestDist = dist; bestHostIdx = p.HostSpawnIndex; }
            }

            if (bestHostIdx < 0)
            {
                // Phase 5.7-SC4 diag: a client enemy spawned but no UNBOUND parked host record of its unitId remained
                // (all host slots of this type already bound elsewhere). Leaves this client enemy unmatched → if it ends
                // up host-bound to a far/dormant host sibling it can appear to stand still. Captures the rare
                // same-seed spawn-divergence case (LogOutput112 Shanty4, retroLedger unconsumed).
                if (sameUnitInLedger > 0) MaybeLogRetroBindDiag(snap, unitId, "no-unbound-slot",
                    sameUnitInLedger, matchCount, float.NaN);
                return;
            }

            // When several same-type entries remain and we have positions, require a tight match so we don't mis-assign a
            // late spawn to the wrong sibling (deterministic level-gen → the local spawn pos ≈ host's roster pos).
            if (matchCount > 1 && snap.HasPosition && bestDist > RetroBindPosTolerance)
            {
                _retroBindAmbiguousDeferred++;
                // SC4 diag: this is the prime suspect for the rare standing-still — same unitId parked but the client's
                // spawn position diverged from every host record by > tolerance, so we defer rather than mis-bind.
                MaybeLogRetroBindDiag(snap, unitId, "ambiguous-pos-defer", sameUnitInLedger, matchCount, bestDist);
                return;
            }

            SetClientHostBinding(bestHostIdx, localKey);
            ClientQuarantinedEntities.Remove(localKey);
            _pendingHostBindLedger.Remove(bestHostIdx);
            _retroBindSuccess++;
            TryApplyPendingHealthState(bestHostIdx, localKey);
            if (Plugin.Cfg.LogLevelManifestDiff.Value)
                Plugin.Log.Info($"[RetroBind] bound late-spawned localUnit={unitId} localIdx={snap.SpawnIndex} → hostIdx={bestHostIdx} dist={bestDist:F1}m (ledger now {_pendingHostBindLedger.Count}, retroBound={_retroBindSuccess})");
        }

        // Phase 5.7-SC4: diagnostic for the rare standing-still — why a late-spawned client enemy couldn't retro-bind to a
        // parked host record (same-seed spawn divergence). Throttled per level. Dumps the local spawn pos plus each parked
        // host record's pos for that unitId so the position divergence is visible in the next reproduction's log.
        private static int _retroBindDiagLogged;
        private static void MaybeLogRetroBindDiag(NetGameplayEntitySnapshot snap, string unitId, string reason,
            int sameUnitInLedger, int unboundMatches, float bestDist)
        {
            if (!Plugin.Cfg.LogEnemyInterestDiag.Value) return;
            if (_retroBindDiagLogged >= 60) return;
            _retroBindDiagLogged++;
            string parked = "";
            int shown = 0;
            foreach (var kv in _pendingHostBindLedger)
            {
                var p = kv.Value;
                if (!string.Equals(p.UnitIdentifier, unitId, StringComparison.Ordinal)) continue;
                bool bound = ClientHostToLocalKeyByHostSpawnIndex.ContainsKey(p.HostSpawnIndex);
                float d = (p.HasPosition && snap.HasPosition) ? Vector3.Distance(p.Position, snap.Position) : -1f;
                parked += $" [hostIdx={p.HostSpawnIndex} pos={p.Position.ToString("F0")} dist={d:F1} bound={bound}]";
                if (++shown >= 6) break;
            }
            string bd = float.IsNaN(bestDist) ? "n/a" : bestDist.ToString("F1");
            NetLogger.Info($"[RetroBindDiag] reason={reason} localUnit={unitId} localIdx={snap.SpawnIndex} localPos={(snap.HasPosition ? snap.Position.ToString("F0") : "?")} sameUnitParked={sameUnitInLedger} unboundMatches={unboundMatches} bestDist={bd}m parked:{parked}");
        }

        public static void ProcessHostWorldRoster(List<NetWorldEntityRecord> records, int hostRevision)
        {
            if (records == null || records.Count == 0) return;
            if (NetConfig.GetMode() != NetMode.Client) return;

            _clientRosterRecordsReceived += records.Count;

            if (hostRevision == _clientRosterRevision)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    Plugin.Log.Info($"[WorldRoster] Client skipping duplicate roster revision={hostRevision}");
                return;
            }
            _clientRosterRevision = hostRevision;

            // Phase 5.5-RT3-A7: snapshot existing bindings BEFORE the clear so a stable hostIdx↔localKey identity can be
            // preserved across this reconcile (instead of re-matching by 5m position — which loses the binding the
            // moment a host enemy moves >5m while the client puppet hasn't followed; log55 mage moved 6.66m → quarantined).
            var preservedHostToLocal = new Dictionary<int, string>(ClientHostToLocalKeyByHostSpawnIndex);

            ClientHostToLocalKeyByHostSpawnIndex.Clear();
            ClientLocalKeyToHostSpawnIndex.Clear();    // O3-C
            ClientQuarantinedEntities.Clear();         // O3-C
            _clientRosterBound = 0;
            _clientRosterHostOnlyMissing = 0;
            _clientRosterClientOnlyQuarantined = 0;
            _clientRosterTypeMismatch = 0;
            _clientRosterFingerprintMismatch = 0;
            _rosterOneToOneBound = 0;                  // O3-C
            _rosterPreservedBound = 0;                 // RT3-A7
            _clientOnlyCombatQuarantined = 0;          // O3-C

            Plugin.Log.Info($"[WorldRoster] Client received Host roster count={records.Count} revision={hostRevision}");

            const float posTolerance = 5f;
            // O3-C: Track bound local keys per pass to enforce strict 1:1 Host→Client mapping.
            var usedLocalKeys = new HashSet<string>();

            // Phase 5.5-RT3-A6: re-assert RT3 runtime-spawn bindings the Clear() above just wiped, and mark their local
            // keys as used so the proximity pass below can't steal a runtime add for a (different) roster hostIdx.
            int runtimeReasserted = 0;
            foreach (var kv in _runtimeSpawnBindingsByHostIdx)
            {
                ClientHostToLocalKeyByHostSpawnIndex[kv.Key] = kv.Value;
                ClientLocalKeyToHostSpawnIndex[kv.Value] = kv.Key;
                usedLocalKeys.Add(kv.Value);
                runtimeReasserted++;
            }
            if (runtimeReasserted > 0)
                Plugin.Log.Info($"[WorldRoster] re-asserted {runtimeReasserted} RT3 runtime bindings across reconcile");

            foreach (var rec in records)
            {
                // Phase 5.7-DB2: the WorldRoster is the static level-gen roster and still lists enemies the client has
                // already buried. Skip them — otherwise this proximity pass re-binds a dead host idx to a surviving
                // same-type sibling and starves the real one (LogOutput117 two Trackers; hostIdx=12 dead kept stealing
                // local [16], so alive hostIdx=17's death found "never bound" → frozen).
                if (IsClientKnownDeadHostIdx(rec.SpawnIndex)) continue;

                // Phase 5.5-RT3-A7: if this host id was already bound to a still-alive local entity of the same unit type,
                // KEEP that binding regardless of how far it has drifted. Identity is established once; only death /
                // despawn / level change releases it. This is what stops a moving caster from being quarantined.
                if (Plugin.Cfg.StableWorldRosterBinding.Value
                    && preservedHostToLocal.TryGetValue(rec.SpawnIndex, out var prevKey)
                    && !string.IsNullOrWhiteSpace(prevKey)
                    && !usedLocalKeys.Contains(prevKey)
                    && EntitiesByLocalId.TryGetValue(prevKey, out var prevEntity)
                    && !prevEntity.IsDead
                    && (string.IsNullOrWhiteSpace(rec.UnitIdentifier)
                        || string.IsNullOrWhiteSpace(prevEntity.EntityId.UnitIdentifier)
                        || string.Equals(rec.UnitIdentifier, prevEntity.EntityId.UnitIdentifier, StringComparison.Ordinal)))
                {
                    ClientHostToLocalKeyByHostSpawnIndex[rec.SpawnIndex] = prevKey;
                    ClientLocalKeyToHostSpawnIndex[prevKey] = rec.SpawnIndex;
                    usedLocalKeys.Add(prevKey);
                    _clientRosterBound++;
                    _rosterOneToOneBound++;
                    _rosterPreservedBound++;
                    TryApplyPendingHealthState(rec.SpawnIndex, prevKey);
                    continue; // stable identity preserved — skip the position re-match entirely
                }

                NetGameplayEntitySnapshot? bestMatch = null;
                float bestDist = float.MaxValue;
                string matchReason = "";

                bool hostHasUnit = !string.IsNullOrWhiteSpace(rec.UnitIdentifier);

                foreach (var candidate in EntitiesByLocalId.Values)
                {
                    if (candidate.IsDead) continue;
                    if (!string.Equals(candidate.Category, rec.Category, StringComparison.OrdinalIgnoreCase)) continue;

                    // O3-C: Skip candidates already bound in this pass (1:1 constraint).
                    string candidateKey = GetSnapshotTargetKey(candidate);
                    if (usedLocalKeys.Contains(candidateKey)) continue;

                    bool candHasUnit = !string.IsNullOrWhiteSpace(candidate.EntityId.UnitIdentifier);

                    if (hostHasUnit && candHasUnit)
                    {
                        if (!string.Equals(rec.UnitIdentifier, candidate.EntityId.UnitIdentifier, StringComparison.Ordinal))
                        {
                            _clientRosterTypeMismatch++;
                            continue;
                        }
                        // Same unitId — select closest by position
                        if (rec.HasPosition && candidate.HasPosition)
                        {
                            float dist = Vector3.Distance(rec.Position, candidate.Position);
                            if (dist <= posTolerance && dist < bestDist)
                            {
                                bestMatch = candidate;
                                bestDist = dist;
                                matchReason = $"unitId+position dist={dist:F1}m";
                            }
                        }
                        else if (bestMatch == null)
                        {
                            bestMatch = candidate;
                            bestDist = 0f;
                            matchReason = "unitId (no position)";
                        }
                    }
                    else
                    {
                        // One side has no unitId — position-only fallback (generous 2m)
                        if (rec.HasPosition && candidate.HasPosition)
                        {
                            float dist = Vector3.Distance(rec.Position, candidate.Position);
                            if (dist < 2f && dist < bestDist)
                            {
                                bestMatch = candidate;
                                bestDist = dist;
                                matchReason = $"position-only dist={dist:F1}m";
                                _clientRosterFingerprintMismatch++;
                            }
                        }
                    }
                }

                if (bestMatch != null)
                {
                    string localKey = GetSnapshotTargetKey(bestMatch);
                    if (!string.IsNullOrWhiteSpace(localKey))
                    {
                        // O3-C: Bind 1:1 — record in both forward and reverse maps and mark used.
                        ClientHostToLocalKeyByHostSpawnIndex[rec.SpawnIndex] = localKey;
                        ClientLocalKeyToHostSpawnIndex[localKey] = rec.SpawnIndex;
                        usedLocalKeys.Add(localKey);
                        _clientRosterBound++;
                        _rosterOneToOneBound++;

                        // Propagate Host's category classification to local snapshot if unknown.
                        if (bestMatch.SyncCategory == SyncCatUnknown && rec.SyncCategory != SyncCatUnknown)
                            bestMatch.SyncCategory = rec.SyncCategory;

                        Plugin.Log.Info($"[WorldRoster] Bound hostIdx={rec.SpawnIndex} hostUnit={rec.UnitIdentifier} hostActor={rec.ActorName} → localIdx={bestMatch.SpawnIndex} localUnit={bestMatch.EntityId.UnitIdentifier} reason={matchReason}");

                        // Drain pending HealthState if one was queued while binding was absent.
                        TryApplyPendingHealthState(rec.SpawnIndex, localKey);
                    }
                }
                else
                {
                    _clientRosterHostOnlyMissing++;
                    // Phase 5.7-RB: park this unmatched host enemy so a later local spawn of the same unit binds it,
                    // instead of leaving it permanently hostOnly (the recurring timing-race gap).
                    RecordPendingHostBind(rec.SpawnIndex, rec.UnitIdentifier, rec.Category, rec.HasPosition, rec.Position);
                    Plugin.Log.Info($"[WorldRoster] No local match for hostIdx={rec.SpawnIndex} hostUnit={rec.UnitIdentifier} hostActor={rec.ActorName} hostCat={rec.Category}");
                }
            }

            // O3-C: Identify client-only entities; quarantine CombatEnemies with no Host binding.
            foreach (var candidate in EntitiesByLocalId.Values)
            {
                if (candidate.IsDead) continue;
                // Phase 5.3-H B: never quarantine a player entity.
                if (IsPlayerEntitySnapshot(candidate)) { _quarantinePlayerSkipped++; continue; }
                string localKey = GetSnapshotTargetKey(candidate);
                if (string.IsNullOrWhiteSpace(localKey)) continue;
                if (usedLocalKeys.Contains(localKey)) continue;   // bound — not client-only
                _clientRosterClientOnlyQuarantined++;
                if (IsCombatEnemyForSync(candidate))
                {
                    ClientQuarantinedEntities.Add(localKey);
                    _clientOnlyCombatQuarantined++;
                    Plugin.Log.Info($"[WorldRoster] Quarantine client-only CombatEnemy localIdx={candidate.SpawnIndex} unit={candidate.EntityId.UnitIdentifier} actor={candidate.ActorName}");
                }
                else
                {
                    Plugin.Log.Info($"[WorldRoster] Client-only non-combat localIdx={candidate.SpawnIndex} unit={candidate.EntityId.UnitIdentifier} actor={candidate.ActorName} cat={candidate.Category} syncCat={SyncCatName(candidate.SyncCategory)}");
                }
            }

            // Phase 5.7-RB: drop ledger entries that got bound in this pass; the rest stay parked for later local spawns.
            PruneBoundPendingHostBinds();

            Plugin.Log.Info($"[WorldRoster] Client reconciliation complete bound={_clientRosterBound} (1:1={_rosterOneToOneBound} preserved={_rosterPreservedBound}) hostOnly={_clientRosterHostOnlyMissing} clientOnly={_clientRosterClientOnlyQuarantined} quarantined={_clientOnlyCombatQuarantined} typeMismatch={_clientRosterTypeMismatch} retroLedger={_pendingHostBindLedger.Count} retroBound={_retroBindSuccess}");
        }

        // ================================================================
        // Phase 5.3-E: Host-authoritative semantic level manifest
        // ================================================================

        /// <summary>
        /// Builds a semantic manifest of the current level from the local entity registry plus a
        /// best-effort room scan. Used by Host to broadcast and by Client to build the provisional
        /// local manifest for diffing. NOT a full Unity serialization — generation RESULT summary only.
        /// </summary>
        public static NetLevelManifest? BuildLevelManifest(string role)
        {
            if (!IsEnabled()) return null;
            if (!NetRunStateBridge.TryGetLocalRunState(out var state) || !state.HasLevel) return null;

            var manifest = new NetLevelManifest();
            var h = manifest.Header;
            h.ManifestVersion    = 1;
            h.Role               = role;
            h.SceneName          = state.ChapterName ?? "";
            h.LevelIndex         = state.LevelIndex;
            h.HasLevelSeed       = state.HasLevelSeed;
            h.LevelSeed          = state.HasLevelSeed ? state.LevelSeed : 0;
            h.GenerationRevision = state.Revision;
            h.BuiltAt            = Time.realtimeSinceStartup;

            // ── Units / enemies / specials ────────────────────────────────────
            int manifestIndex = 0;
            foreach (var snapshot in EntitiesByLocalId.Values.OrderBy(s => s.SpawnIndex))
            {
                // Phase 5.3-H B: players are never level-generated content — keep them out of the
                // unit manifest entirely so they cannot be bound or quarantined as enemies.
                if (IsPlayerEntitySnapshot(snapshot)) { _manifestPlayerExcluded++; continue; }

                // Refresh runtime position + names from the live object when possible.
                object? rto = null;
                if (snapshot.TryGetRuntimeObject(out rto) && rto != null
                    && !(rto is UnityEngine.Object uo && uo == null)
                    && TryGetPosition(rto, out var freshPos))
                {
                    snapshot.HasPosition = true;
                    snapshot.Position = freshPos;
                }

                string goName = TryGetGameObjectName(rto);
                string modifierFlags = ExtractModifierFlags(goName, snapshot.ActorName, snapshot.EntityId.UnitIdentifier);
                uint compFingerprint = ComputeComponentFingerprint(rto);
                bool isCombat = IsCombatEnemyForSync(snapshot);

                var u = new NetLevelManifestUnit
                {
                    ManifestIndex        = manifestIndex++,
                    SpawnIndex           = snapshot.SpawnIndex,
                    UnitIdentifier       = snapshot.EntityId.UnitIdentifier ?? "",
                    ActorName            = snapshot.ActorName ?? "",
                    GameObjectName       = goName,
                    SyncCategory         = snapshot.SyncCategory,
                    Category             = snapshot.Category ?? "",
                    IsCombatEnemy        = isCombat,
                    HasPosition          = snapshot.HasPosition,
                    Position             = snapshot.Position,
                    HasInitialPosition   = snapshot.HasInitialPosition,
                    InitialPosition      = snapshot.InitialPosition,
                    ModifierFlags        = modifierFlags,
                    ComponentFingerprint = compFingerprint,
                    IsDead               = snapshot.IsDead,
                };
                manifest.Units.Add(u);

                // Specials: traders/ghosts/event NPCs are level-generation artefacts, not runtime state.
                if (snapshot.SyncCategory == SyncCatTrader
                    || snapshot.SyncCategory == SyncCatGhost
                    || snapshot.SyncCategory == SyncCatEventNpc
                    || snapshot.SyncCategory == SyncCatInteractNpc)
                {
                    manifest.Specials.Add(new NetLevelManifestSpecial
                    {
                        Type         = SyncCatName(snapshot.SyncCategory),
                        Name         = !string.IsNullOrWhiteSpace(snapshot.EntityId.UnitIdentifier)
                                          ? snapshot.EntityId.UnitIdentifier
                                          : snapshot.ActorName ?? "",
                        SyncCategory = snapshot.SyncCategory,
                        HasPosition  = snapshot.HasPosition,
                        Position     = snapshot.Position,
                    });
                }
            }

            // ── Best-effort room scan (read-only; no LevelGeneration API guessing) ──
            try
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                if (scene.IsValid() && scene.isLoaded)
                {
                    int roomIdx = 0;
                    foreach (var root in scene.GetRootGameObjects())
                    {
                        if (root == null) continue;
                        string n = root.name ?? "";
                        if (n.IndexOf("room", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        manifest.Rooms.Add(new NetLevelManifestRoom
                        {
                            RoomIndex   = roomIdx++,
                            RoomName    = Clean(n),
                            HasPosition = true,
                            Position    = root.transform.position,
                            ChildCount  = root.transform.childCount,
                        });
                        if (manifest.Rooms.Count >= 128) break;
                    }
                    manifest.Rooms = manifest.Rooms.OrderBy(rm => rm.RoomName, StringComparer.Ordinal).ToList();
                    for (int i = 0; i < manifest.Rooms.Count; i++) manifest.Rooms[i].RoomIndex = i;
                }
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[LevelManifest] room scan failed: {ex.Message}");
            }

            h.RoomCount         = manifest.Rooms.Count;
            h.UnitCount         = manifest.Units.Count;
            h.CombatEnemyCount  = manifest.Units.Count(x => x.IsCombatEnemy);
            h.SpecialEventCount = manifest.Specials.Count;
            h.GenerationHash    = ComputeGenerationHash(manifest);
            h.RuntimeHash       = ComputeRuntimeHash(manifest);

            // Phase 5.3-H I: diagnose generation-hash instability on the repeatedly-rebuilding side.
            if (role == "Host") DiagnoseGenerationHashStability(manifest);

            if (role == "Host")
            {
                _hostManifestBuilt++;
                _hostManifestSent++;
            }
            else
            {
                _clientManifestBuilt++;
            }

            if (Plugin.Cfg.LogLevelManifest.Value)
                NetLogger.Info($"[LevelManifest] {role} built manifest scene={h.SceneName} level={h.LevelIndex} seed={(h.HasLevelSeed ? h.LevelSeed.ToString() : "?")} rooms={h.RoomCount} units={h.UnitCount} combat={h.CombatEnemyCount} specials={h.SpecialEventCount} genHash={h.GenerationHash} runtimeHash={h.RuntimeHash}");

            return manifest;
        }

        /// <summary>
        /// Client: received Host's authoritative manifest. Build the local provisional manifest,
        /// diff the two, log clear divergence, then quarantine client-only combat enemies and bind
        /// host enemies to existing local instances.
        /// </summary>
        public static void ProcessHostLevelManifest(NetLevelManifest hostManifest)
        {
            if (hostManifest == null) return;
            if (NetConfig.GetMode() != NetMode.Client) return;

            _clientManifestReceived++;

            // Phase 5.3-H A: HARD GATE — a manifest from a scene/level/seed the client is not
            // currently in must NEVER reach diff/reconcile/quarantine/binding (it pollutes bindings,
            // generationHash, and drift). Defer the latest mismatching one; Tick retries when matched.
            if (!TryGateHostManifest(hostManifest, "recv")) return;

            RunManifestDiffAndReconcile(hostManifest);
        }

        // Returns true only when the local run state matches the manifest's scene/level/seed.
        private static bool TryGateHostManifest(NetLevelManifest m, string source)
        {
            var hh = m.Header;
            if (!NetRunStateBridge.TryGetLocalRunState(out var run) || !run.HasLevel)
            {
                DeferManifest(m, "no-local-run-state", source);
                return false;
            }
            if (!string.Equals(run.ChapterName ?? "", hh.SceneName ?? "", StringComparison.Ordinal))
            {
                _manifestDeferredSceneMismatch++;
                DeferManifest(m, $"scene host={hh.SceneName} client={run.ChapterName}", source);
                return false;
            }
            if (run.LevelIndex != hh.LevelIndex)
            {
                _manifestDeferredLevelMismatch++;
                _manifestDeferredLevelMismatchAfterFix++;
                DeferManifest(m, $"level host={hh.LevelIndex} client={run.LevelIndex}", source);
                return false;
            }
            // Seed must match when both sides know it; mismatched seed = different generated world.
            if (hh.HasLevelSeed != run.HasLevelSeed
                || (hh.HasLevelSeed && run.HasLevelSeed && run.LevelSeed != hh.LevelSeed))
            {
                _manifestDeferredSeedMismatch++;
                DeferManifest(m, $"seed host={(hh.HasLevelSeed ? hh.LevelSeed.ToString() : "?")} client={(run.HasLevelSeed ? run.LevelSeed.ToString() : "?")}", source);
                return false;
            }
            _manifestProcessedMatchingRun++;
            if (source == "deferred") _manifestAcceptedAfterRunStateFix++;
            NetLogger.Info($"[ManifestGate] accepted matching run chapter={run.ChapterName} level={hh.LevelIndex} graph={(string.IsNullOrEmpty(run.LevelGenerator) ? "?" : run.LevelGenerator)} seed={(hh.HasLevelSeed ? hh.LevelSeed.ToString() : "?")} source={source}");
            return true;
        }

        private static void DeferManifest(NetLevelManifest m, string reason, string source)
        {
            if (_deferredHostManifest != null && !ReferenceEquals(_deferredHostManifest, m))
                _manifestDroppedStaleRun++;
            _deferredHostManifest = m;
            if (Plugin.Cfg.LogLevelManifest.Value)
                NetLogger.Info($"[LevelManifest] DEFER manifest (run mismatch) source={source} reason={Clean(reason)} — not reconciled");
        }

        // Called from Tick: if a deferred manifest now matches the local run, process it once.
        private static void TryProcessDeferredManifest()
        {
            if (_deferredHostManifest == null) return;
            var m = _deferredHostManifest;
            if (!TryGateHostManifest(m, "deferred")) return;
            _deferredHostManifest = null;
            if (Plugin.Cfg.LogLevelManifest.Value)
                NetLogger.Info("[LevelManifest] Deferred manifest now matches local run — processing");
            RunManifestDiffAndReconcile(m);
        }

        private static void RunManifestDiffAndReconcile(NetLevelManifest hostManifest)
        {
            float elapsed = _clientFirstLevelEntitySeenAt > 0f
                ? Time.realtimeSinceStartup - _clientFirstLevelEntitySeenAt
                : -1f;
            NetLogger.Info($"[LevelManifest] Host manifest accepted (run match) elapsed={(elapsed >= 0f ? elapsed.ToString("F2") + "s" : "n/a")}");

            var clientManifest = BuildLevelManifest("Client");
            if (clientManifest == null)
            {
                NetLogger.Warn("[LevelManifest] Client could not build local manifest (no level state) — cannot reconcile yet");
                return;
            }

            DiffManifests(hostManifest, clientManifest);
            ReconcileHostManifest(hostManifest, clientManifest);
        }

        private static void DiffManifests(NetLevelManifest host, NetLevelManifest client)
        {
            var hh = host.Header;
            var ch = client.Header;

            bool genMatch = hh.GenerationHash == ch.GenerationHash;
            bool runtimeMatch = hh.RuntimeHash == ch.RuntimeHash;
            if (genMatch) _generationHashMatch++; else _generationHashMismatch++;
            if (runtimeMatch) _runtimeHashMatch++; else _runtimeHashMismatch++;
            // Keep the legacy aggregate counters tracking the generation hash (the meaningful one).
            if (genMatch) _manifestHashMatch++; else _manifestHashMismatch++;

            NetLogger.Info($"[LevelManifestDiff] generationHash host={hh.GenerationHash} client={ch.GenerationHash} match={genMatch}");
            NetLogger.Info($"[LevelManifestDiff] runtimeHash host={hh.RuntimeHash} client={ch.RuntimeHash} match={runtimeMatch} (runtime divergence is expected)");

            // Seed.
            bool seedMatch = hh.HasLevelSeed && ch.HasLevelSeed && hh.LevelSeed == ch.LevelSeed;
            if (!seedMatch)
            {
                _manifestSeedMismatch++;
                NetLogger.Info($"[LevelManifestDiff] seed mismatch host={(hh.HasLevelSeed ? hh.LevelSeed.ToString() : "?")} client={(ch.HasLevelSeed ? ch.LevelSeed.ToString() : "?")} scene={hh.SceneName} level={hh.LevelIndex}");
            }
            else if (Plugin.Cfg.LogLevelManifestDiff.Value)
            {
                NetLogger.Info($"[LevelManifestDiff] seed host={hh.LevelSeed} client={ch.LevelSeed} (match)");
            }

            // Rooms.
            int roomMismatch = 0;
            int roomMax = Math.Max(host.Rooms.Count, client.Rooms.Count);
            for (int i = 0; i < roomMax; i++)
            {
                var hr = i < host.Rooms.Count ? host.Rooms[i] : null;
                var cr = i < client.Rooms.Count ? client.Rooms[i] : null;
                bool same = hr != null && cr != null && string.Equals(hr.RoomName, cr.RoomName, StringComparison.Ordinal);
                if (!same)
                {
                    roomMismatch++;
                    if (Plugin.Cfg.LogLevelManifestDiff.Value && roomMismatch <= 12)
                        NetLogger.Info($"[LevelManifestDiff] room mismatch idx={i} host={(hr?.RoomName ?? "<none>")} client={(cr?.RoomName ?? "<none>")}");
                }
            }
            if (roomMismatch > 0) _manifestRoomMismatch += roomMismatch;
            NetLogger.Info($"[LevelManifestDiff] rooms host={host.Rooms.Count} client={client.Rooms.Count} mismatch={roomMismatch}");

            // Units — index-aligned mismatch + host-only / client-only by (unitId|modifier|pos) signature.
            int unitMax = Math.Max(host.Units.Count, client.Units.Count);
            int unitMismatch = 0;
            for (int i = 0; i < unitMax; i++)
            {
                var hu = i < host.Units.Count ? host.Units[i] : null;
                var cu = i < client.Units.Count ? client.Units[i] : null;
                bool sameUnit = hu != null && cu != null
                    && string.Equals(hu.UnitIdentifier, cu.UnitIdentifier, StringComparison.Ordinal);
                if (!sameUnit)
                {
                    unitMismatch++;
                    if (Plugin.Cfg.LogLevelManifestDiff.Value && unitMismatch <= 16)
                        NetLogger.Info($"[LevelManifestDiff] unit mismatch idx={i} host={(hu?.UnitIdentifier ?? "<none>")}{PosTag(hu)} client={(cu?.UnitIdentifier ?? "<none>")}{PosTag(cu)}");
                }
                else if (hu != null && cu != null
                         && !string.Equals(hu.ModifierFlags ?? "", cu.ModifierFlags ?? "", StringComparison.Ordinal))
                {
                    _manifestHostEnemyModifierMismatch++;
                    if (Plugin.Cfg.LogLevelManifestDiff.Value)
                        NetLogger.Info($"[LevelManifestDiff] modifier mismatch idx={i} unit={hu.UnitIdentifier} host={(string.IsNullOrEmpty(hu.ModifierFlags) ? "None" : hu.ModifierFlags)} client={(string.IsNullOrEmpty(cu.ModifierFlags) ? "None" : cu.ModifierFlags)}");
                }
            }
            if (unitMismatch > 0) _manifestUnitMismatch += unitMismatch;

            // Host-only / client-only by signature multiset.
            var clientSigs = new Dictionary<string, int>();
            foreach (var cu in client.Units) Increment(clientSigs, UnitSignature(cu));
            var hostSigs = new Dictionary<string, int>();
            foreach (var hu in host.Units) Increment(hostSigs, UnitSignature(hu));

            int hostOnly = 0, clientOnly = 0;
            foreach (var hu in host.Units)
            {
                string sig = UnitSignature(hu);
                if (GetCount(clientSigs, sig) > 0) Decrement(clientSigs, sig);
                else
                {
                    hostOnly++;
                    if (Plugin.Cfg.LogLevelManifestDiff.Value && hostOnly <= 16)
                        NetLogger.Info($"[LevelManifestDiff] hostOnly unit idx={hu.ManifestIndex} unit={hu.UnitIdentifier}{ModTag(hu)}{PosTag(hu)}");
                }
            }
            foreach (var cu in client.Units)
            {
                string sig = UnitSignature(cu);
                if (GetCount(hostSigs, sig) > 0) Decrement(hostSigs, sig);
                else
                {
                    clientOnly++;
                    if (Plugin.Cfg.LogLevelManifestDiff.Value && clientOnly <= 16)
                        NetLogger.Info($"[LevelManifestDiff] clientOnly unit idx={cu.ManifestIndex} unit={cu.UnitIdentifier}{ModTag(cu)}{PosTag(cu)}");
                }
            }
            _manifestHostOnlyUnits += hostOnly;
            _manifestClientOnlyUnits += clientOnly;
            NetLogger.Info($"[LevelManifestDiff] units host={host.Units.Count} client={client.Units.Count} mismatch={unitMismatch} hostOnly={hostOnly} clientOnly={clientOnly}");

            // Specials.
            int specialMismatch = 0;
            int specMax = Math.Max(host.Specials.Count, client.Specials.Count);
            for (int i = 0; i < specMax; i++)
            {
                var hs = i < host.Specials.Count ? host.Specials[i] : null;
                var cs = i < client.Specials.Count ? client.Specials[i] : null;
                bool same = hs != null && cs != null && string.Equals(hs.Name, cs.Name, StringComparison.Ordinal);
                if (!same)
                {
                    specialMismatch++;
                    if (Plugin.Cfg.LogLevelManifestDiff.Value && specialMismatch <= 12)
                        NetLogger.Info($"[LevelManifestDiff] special mismatch idx={i} host={(hs?.Name ?? "<none>")} client={(cs?.Name ?? "<none>")}");
                }
            }
            if (specialMismatch > 0) _manifestSpecialMismatch += specialMismatch;
            NetLogger.Info($"[LevelManifestDiff] specials host={host.Specials.Count} client={client.Specials.Count} mismatch={specialMismatch}");

            // ── First divergence point ────────────────────────────────────────
            // Answers: "same seed but where does it first diverge?" Categories are checked in the
            // order they occur in generation: seed → rooms → units → specials. This is a coarse
            // result-level locator; the LevelGenTrace patches pinpoint the generating node.
            string firstCat = "none", firstReason = "";
            int firstIdx = -1;
            if (!seedMatch) { firstCat = "Seed"; firstReason = $"host={(hh.HasLevelSeed ? hh.LevelSeed.ToString() : "?")} client={(ch.HasLevelSeed ? ch.LevelSeed.ToString() : "?")}"; }
            else if (roomMismatch > 0)
            {
                firstCat = "Room";
                for (int i = 0; i < roomMax; i++)
                {
                    var hr = i < host.Rooms.Count ? host.Rooms[i] : null;
                    var cr = i < client.Rooms.Count ? client.Rooms[i] : null;
                    if (hr == null || cr == null || !string.Equals(hr.RoomName, cr.RoomName, StringComparison.Ordinal))
                    { firstIdx = i; firstReason = $"host={(hr?.RoomName ?? "<none>")} client={(cr?.RoomName ?? "<none>")}"; break; }
                }
            }
            else if (unitMismatch > 0)
            {
                firstCat = "Unit";
                for (int i = 0; i < unitMax; i++)
                {
                    var hu = i < host.Units.Count ? host.Units[i] : null;
                    var cu = i < client.Units.Count ? client.Units[i] : null;
                    if (hu == null || cu == null || !string.Equals(hu.UnitIdentifier, cu.UnitIdentifier, StringComparison.Ordinal))
                    { firstIdx = i; firstReason = $"host={(hu?.UnitIdentifier ?? "<none>")} client={(cu?.UnitIdentifier ?? "<none>")}"; break; }
                }
            }
            else if (specialMismatch > 0) { firstCat = "Special"; firstReason = "see special mismatch lines above"; }
            else if (_manifestHostEnemyModifierMismatch > 0) { firstCat = "Modifier"; firstReason = "see modifier mismatch lines above"; }

            NetLogger.Info($"[LevelGenDiff] firstDivergence category={firstCat} idx={firstIdx} reason={firstReason}");
        }

        /// <summary>
        /// Quarantine client-only combat enemies (not in host manifest) and bind host enemies to
        /// existing local instances. First version never destroys — quarantine is reversible.
        /// </summary>
        private static void ReconcileHostManifest(NetLevelManifest host, NetLevelManifest client)
        {
            int bound = 0, quarantined = 0, hostOnly = 0;

            // Build a lookup of local snapshots by unitId for binding.
            var localByUnit = new Dictionary<string, List<NetGameplayEntitySnapshot>>();
            foreach (var snap in EntitiesByLocalId.Values)
            {
                if (snap.IsDead) continue;
                string uid = snap.EntityId.UnitIdentifier ?? "";
                if (string.IsNullOrWhiteSpace(uid)) continue;
                if (!localByUnit.TryGetValue(uid, out var list)) { list = new List<NetGameplayEntitySnapshot>(); localByUnit[uid] = list; }
                list.Add(snap);
            }

            var usedLocalKeys = new HashSet<string>();

            // ── Bind host enemies to existing local instances ────────────────
            foreach (var hu in host.Units)
            {
                if (!hu.IsCombatEnemy) continue;
                if (string.IsNullOrWhiteSpace(hu.UnitIdentifier)) continue;
                if (IsClientKnownDeadHostIdx(hu.SpawnIndex)) continue; // DB2: don't bind a buried host idx to a live local

                if (!localByUnit.TryGetValue(hu.UnitIdentifier, out var candidates) || candidates.Count == 0)
                {
                    _manifestHostEnemyBindFailedNoCandidate++;
                    hostOnly++;
                    // Phase 5.7-RB ("v2"): park it so a later local spawn binds it, instead of "record only, no spawn".
                    RecordPendingHostBind(hu.SpawnIndex, hu.UnitIdentifier, hu.Category, hu.HasPosition, hu.Position);
                    if (Plugin.Cfg.LogLevelManifestDiff.Value)
                        NetLogger.Info($"[LevelManifest] hostOnly enemy unit={hu.UnitIdentifier}{ModTag(hu)}{PosTag(hu)} — no local candidate (parked for retro-bind)");
                    continue;
                }

                // Choose closest unused candidate by position.
                NetGameplayEntitySnapshot? best = null;
                float bestDist = float.MaxValue;
                int availCount = 0;
                foreach (var cand in candidates)
                {
                    string ck = GetSnapshotTargetKey(cand);
                    if (string.IsNullOrWhiteSpace(ck) || usedLocalKeys.Contains(ck)) continue;
                    availCount++;
                    float dist = (hu.HasPosition && cand.HasPosition) ? Vector3.Distance(hu.Position, cand.Position) : 0f;
                    if (dist < bestDist) { bestDist = dist; best = cand; }
                }

                if (best == null)
                {
                    _manifestHostEnemyBindFailedNoCandidate++;
                    continue;
                }
                if (availCount > 1 && hu.HasPosition && best.HasPosition && bestDist > 8f)
                {
                    // Multiple same-type candidates and the closest is still far — ambiguous.
                    _manifestHostEnemyBindFailedAmbiguous++;
                    if (Plugin.Cfg.LogLevelManifestDiff.Value)
                        NetLogger.Info($"[LevelManifest] bind ambiguous hostUnit={hu.UnitIdentifier} candidates={availCount} closest={bestDist:F1}m");
                    continue;
                }

                string localKey = GetSnapshotTargetKey(best);
                SetClientHostBinding(hu.SpawnIndex, localKey);
                usedLocalKeys.Add(localKey);
                ClientQuarantinedEntities.Remove(localKey);
                _manifestHostEnemyBoundExisting++;
                bound++;
                TryApplyPendingHealthState(hu.SpawnIndex, localKey);
            }

            // ── Quarantine client-only combat enemies ─────────────────────────
            // Iterate ALL unbound entities and classify the outcome so a high clientOnly count with
            // zero quarantine is never silent. "current*" reflects THIS reconcile pass only; the
            // "_manifest*" fields keep cumulative totals (Phase 5.3-G — split stat scopes).
            _currentClientOnlyUnits = 0;
            _currentClientOnlyCombatUnits = 0;
            _currentClientOnlyNonCombatUnits = 0;
            _currentClientOnlyQuarantineApplied = 0;
            _currentClientOnlyAlreadyQuarantined = 0;
            _currentClientOnlyNoRuntime = 0;

            foreach (var snap in EntitiesByLocalId.Values)
            {
                if (snap.IsDead) continue;
                // Phase 5.3-H B: never quarantine a player entity.
                if (IsPlayerEntitySnapshot(snap)) { _quarantinePlayerSkipped++; continue; }
                string localKey = GetSnapshotTargetKey(snap);

                if (string.IsNullOrWhiteSpace(localKey))
                {
                    _manifestClientOnlyQuarantineNoLocalKey++;
                    continue;
                }
                if (usedLocalKeys.Contains(localKey)) continue; // bound to a host enemy — not client-only
                // Phase 5.5-RT3-A: a runtime boss-add bound via RegisterMirroredRuntimeSpawn is host-authoritative and
                // intentionally absent from the (level-load) manifest. The reconcile must NOT treat it as a rogue
                // client-only enemy and disable its AI — that is exactly the "standing still, can't attack" symptom.
                if (ClientLocalKeyToHostSpawnIndex.ContainsKey(localKey)) continue;

                _currentClientOnlyUnits++;

                if (!IsCombatEnemyForSync(snap))
                {
                    _manifestClientOnlyNonCombatSkipped++;
                    _currentClientOnlyNonCombatUnits++;
                    continue; // non-combat props/ambient: not a gameplay threat, leave alone
                }

                _currentClientOnlyCombatUnits++;

                if (ClientQuarantinedEntities.Contains(localKey))
                {
                    _manifestClientOnlyAlreadyQuarantined++;
                    _currentClientOnlyAlreadyQuarantined++;
                    continue;
                }

                if (!snap.TryGetRuntimeObject(out var rObj) || rObj == null || (rObj is UnityEngine.Object ruo && ruo == null))
                {
                    _manifestClientOnlyQuarantineNoRuntime++;
                    _currentClientOnlyNoRuntime++;
                    if (Plugin.Cfg.LogLevelManifestDiff.Value)
                        NetLogger.Info($"[LevelManifest] client-only enemy idx={snap.SpawnIndex} unit={snap.EntityId.UnitIdentifier} — runtime object gone, cannot quarantine");
                    continue;
                }

                ClientQuarantinedEntities.Add(localKey);
                _manifestClientOnlyCombatQuarantined++;
                quarantined++;
                if (TryQuarantineClientOnlyEnemy(snap, localKey))
                {
                    _manifestClientOnlyQuarantineApplied++;
                    _totalClientOnlyQuarantineApplied++;
                    _currentClientOnlyQuarantineApplied++;
                }
                else
                    _manifestClientOnlyQuarantineFailed++;
            }

            // Phase 5.7-RB: drop ledger entries bound this pass; keep the unmatched ones parked for later local spawns.
            PruneBoundPendingHostBinds();

            NetLogger.Info($"[LevelManifest] Reconcile complete bound={bound} hostOnly={hostOnly} currentClientOnly={_currentClientOnlyUnits} (combat={_currentClientOnlyCombatUnits} nonCombat={_currentClientOnlyNonCombatUnits}) quarantineAppliedThisPass={_currentClientOnlyQuarantineApplied} alreadyQuarantined={_currentClientOnlyAlreadyQuarantined} noRuntime={_currentClientOnlyNoRuntime} retroLedger={_pendingHostBindLedger.Count} retroBound={_retroBindSuccess}");
        }

        /// <summary>
        /// Best-effort, reversible quarantine of a client-only combat enemy: disable AI/behaviour,
        /// suppress combat, and stop it counting as a host-bind candidate. Never destroys it.
        /// </summary>
        private static bool TryQuarantineClientOnlyEnemy(NetGameplayEntitySnapshot snap, string localKey)
        {
            if (Plugin.Cfg.LogLevelManifestDiff.Value)
                NetLogger.Info($"[LevelManifest] quarantine client-only enemy localKey={Clean(localKey)} idx={snap.SpawnIndex} unit={snap.EntityId.UnitIdentifier} actor={snap.ActorName} reason=not-in-host-manifest");

            // Quarantine state is recorded regardless; the AI-disable below is the only optional part.
            if (!Plugin.Cfg.QuarantineClientOnlyManifestEnemies.Value) return true;
            if (!snap.TryGetRuntimeObject(out var npc) || npc == null) return false;
            if (npc is UnityEngine.Object uo && uo == null) return false;

            try
            {
                // Disable behaviour tree / AI (best-effort, bool param).
                TryInvokeNoArgOrBool(npc, "ToggleBehaviourTree", false);
                object? aiAgent = TryFindComponentByTypeName(npc, "AiAgent");
                if (aiAgent != null)
                {
                    TryInvokeNoArgOrBool(aiAgent, "SetNavMeshAgentState", false);
                    TryInvokeNoArgOrBool(aiAgent, "SetCanMove", false);
                    TryInvokeNoArgOrBool(aiAgent, "ToggleRVO", false);
                }
                return true;
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[LevelManifest] quarantine AI disable failed: {ex.Message}");
                return false;
            }
        }

        // ── manifest helper utilities ─────────────────────────────────────────

        private static string TryGetGameObjectName(object? runtimeObj)
        {
            try
            {
                if (runtimeObj is UnityEngine.Object uo && uo != null && !string.IsNullOrWhiteSpace(uo.name))
                    return Clean(uo.name);
            }
            catch { }
            return "";
        }

        // Extracts special-attribute markers from the GameObject/actor name. SULFUR tags affixed
        // enemies in the object name, e.g. "GoblinSpearman (Offensive)".
        private static string ExtractModifierFlags(string goName, string actorName, string unitId)
        {
            var flags = new List<string>();
            string combined = ((goName ?? "") + " " + (actorName ?? "") + " " + (unitId ?? ""));
            string lower = combined.ToLowerInvariant();
            if (lower.Contains("(offensive)") || lower.Contains("offensive")) flags.Add("Offensive");
            if (lower.Contains("(defensive)") || lower.Contains("defensive")) flags.Add("Defensive");
            if (lower.Contains("elite"))    flags.Add("Elite");
            if (lower.Contains("champion")) flags.Add("Champion");
            if (lower.Contains("buffed"))   flags.Add("Buffed");
            return flags.Count == 0 ? "" : string.Join("|", flags);
        }

        private static uint ComputeComponentFingerprint(object? runtimeObj)
        {
            try
            {
                if (!(runtimeObj is Component comp) || comp == null) return 0u;
                var components = comp.GetComponents<Component>();
                if (components == null || components.Length == 0) return 0u;
                var names = new List<string>(components.Length);
                foreach (var c in components)
                    if (c != null) names.Add(c.GetType().Name);
                names.Sort(StringComparer.Ordinal);
                return Fnv1a(string.Join(",", names));
            }
            catch { return 0u; }
        }

        // generationHash: structural generation result only — stable across host/client for the
        // same generated world. Excludes health, live position, and dead/alive runtime state.
        private static uint ComputeGenerationHash(NetLevelManifest m)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append(m.Header.HasLevelSeed ? m.Header.LevelSeed : 0).Append(';');
                var unitSigs = m.Units.Select(UnitSignature).OrderBy(s => s, StringComparer.Ordinal);
                foreach (var s in unitSigs) sb.Append(s).Append('|');
                sb.Append("#rooms#");
                var roomSigs = m.Rooms.Select(r => r.RoomName).OrderBy(s => s, StringComparer.Ordinal);
                foreach (var r in roomSigs) sb.Append(r).Append('|');
                sb.Append("#specials#");
                var specSigs = m.Specials.Select(s => s.Type + ":" + s.Name).OrderBy(s => s, StringComparer.Ordinal);
                foreach (var s in specSigs) sb.Append(s).Append('|');
                return Fnv1a(sb.ToString());
            }
            catch { return 0u; }
        }

        // Phase 5.3-H I: when the SAME run key rebuilds with a different generation signature, log
        // exactly which signature entries were added/removed — names the pollution source instead of
        // blindly changing the hash rule.
        private static void DiagnoseGenerationHashStability(NetLevelManifest m)
        {
            try
            {
                string runKey = $"{m.Header.SceneName}:{m.Header.LevelIndex}:{(m.Header.HasLevelSeed ? m.Header.LevelSeed.ToString() : "?")}:r{m.Header.GenerationRevision}";
                var sig = m.Units.Select(UnitSignature).OrderBy(s => s, StringComparer.Ordinal).ToList();

                if (runKey == _lastGenRunKey && _lastGenSignature != null)
                {
                    var prev = new HashSet<string>(_lastGenSignature);
                    var cur = new HashSet<string>(sig);
                    var added = sig.Where(s => !prev.Contains(s)).Take(20).ToList();
                    var removed = _lastGenSignature.Where(s => !cur.Contains(s)).Take(20).ToList();
                    if (added.Count == 0 && removed.Count == 0)
                    {
                        _generationHashStableSameRevision++;
                    }
                    else
                    {
                        _generationHashChangedSameRevision++;
                        NetLogger.Info($"[GenHashDiag] generation signature CHANGED same runKey={runKey} added={added.Count} removed={removed.Count}");
                        foreach (var a in added)   NetLogger.Info($"[GenHashDiag]   + {a}");
                        foreach (var r in removed) NetLogger.Info($"[GenHashDiag]   - {r}");
                    }
                }
                _lastGenRunKey = runKey;
                _lastGenSignature = sig;
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.EnableDebugLog.Value) NetLogger.Debug($"[GenHashDiag] failed: {ex.Message}");
            }
        }

        // runtimeHash: volatile per-unit state (live position, dead flag). Allowed to differ.
        private static uint ComputeRuntimeHash(NetLevelManifest m)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                var sigs = m.Units
                    .Select(u =>
                    {
                        int qx = u.HasPosition ? Mathf.RoundToInt(u.Position.x) : 0;
                        int qz = u.HasPosition ? Mathf.RoundToInt(u.Position.z) : 0;
                        return $"{u.UnitIdentifier}:{(u.IsDead ? 1 : 0)}:{qx},{qz}";
                    })
                    .OrderBy(s => s, StringComparer.Ordinal);
                foreach (var s in sigs) sb.Append(s).Append('|');
                return Fnv1a(sb.ToString());
            }
            catch { return 0u; }
        }

        // Generation signature — uses the STABLE initial spawn position (never the live position),
        // quantized, so the generation hash does not drift as units move at runtime.
        // Phase 5.7-GD: do NOT fold ModifierFlags (the Offensive/Defensive AgentRole) into the signature. That role is
        // rolled per-enemy with the GLOBAL, non-seeded UnityEngine.Random in AiAgent.AssignStartRole() (verified in the
        // decomp), so it diverges ~50% between host and client even on an identical level. Baking it in made genHash
        // permanently mismatch and counted the SAME enemy as both hostOnly (host's role) and clientOnly (client's role),
        // drowning out any REAL generation divergence. unitId + quantized initial position is the deterministic identity.
        // The role divergence is still surfaced explicitly by the "[LevelManifestDiff] modifier mismatch" line.
        private static string UnitSignature(NetLevelManifestUnit u)
        {
            if (u.HasInitialPosition)
            {
                int qx = Mathf.RoundToInt(u.InitialPosition.x / 2f);
                int qz = Mathf.RoundToInt(u.InitialPosition.z / 2f);
                return $"{u.UnitIdentifier}#{qx},{qz}";
            }
            // No initial position captured — fall back to identity only (still stable across host/client).
            return $"{u.UnitIdentifier}#?";
        }

        private static string PosTag(NetLevelManifestUnit? u)
            => (u != null && u.HasPosition) ? $" pos=({u.Position.x:F1},{u.Position.y:F1},{u.Position.z:F1})" : "";

        private static string ModTag(NetLevelManifestUnit? u)
            => (u != null && !string.IsNullOrEmpty(u.ModifierFlags)) ? $" mod={u.ModifierFlags}" : "";

        private static uint Fnv1a(string s)
        {
            unchecked
            {
                uint hash = 2166136261u;
                foreach (char c in s) { hash ^= c; hash *= 16777619u; }
                return hash;
            }
        }

        private static void Increment(Dictionary<string, int> d, string k) { d.TryGetValue(k, out int v); d[k] = v + 1; }
        private static void Decrement(Dictionary<string, int> d, string k) { if (d.TryGetValue(k, out int v)) { if (v <= 1) d.Remove(k); else d[k] = v - 1; } }
        private static int  GetCount(Dictionary<string, int> d, string k) => d.TryGetValue(k, out int v) ? v : 0;

        // ---- Phase 4.4.0-O3: classify entity for sync purposes.
        // Component checks use reflection because we don't have compile-time access to game types.
        // Conservative: only mark as Trader / InteractiveNpc when we're highly confident.
        private static int ClassifyEntitySyncCategory(object entity, string actorName)
        {
            if (entity == null) return SyncCatUnknown;
            try
            {
                // LD-Sandstorm / F4 Stage 3: a boss unit is a combat enemy even though it may carry a dialog component
                // (the Desert boss opens airstrike/sniper/terminator mid-fight dialogs). Without this the DialogSpeaker/
                // Speakable check below classifies it as InteractiveNpc → it is excluded from the client puppet system
                // (no AI/weapon suppression, no animation mirror, no host-driven position), so the client boss runs its
                // own AI (shoots the client), shows a local pose (gun instead of the host's walkie-talkie), and doesn't
                // follow the host. Honour the game's own Boss flag so the boss enters the puppet system as intended
                // (§9 "once TriggerFight fires the puppet resumes host-driven position/animator, AI suppressed").
                if (IsBossUnit(entity)) return SyncCatCombatEnemy;

                // Component-first: most reliable
                bool hasShopKeeper = TryFindComponentByTypeName(entity, "ShopKeeper") != null
                                  || TryGetMemberValue(entity, "ShopKeeper") != null;
                bool hasShop = TryFindComponentByTypeName(entity, "Shop") != null;
                if (hasShopKeeper || hasShop) return SyncCatTrader;

                bool hasDialogSpeaker = TryFindComponentByTypeName(entity, "DialogSpeaker") != null;
                bool hasSpeakable = TryFindComponentByTypeName(entity, "Speakable") != null;
                if (hasDialogSpeaker || hasSpeakable) return SyncCatInteractNpc;

                // Actor/unit name-based fallback
                string lower = (actorName ?? "").ToLowerInvariant();
                if (lower.Contains("trader") || lower.Contains("shopkeeper")
                    || lower.Contains("skrip") || lower.Contains("fex")
                    || lower.Contains("grocer") || lower.Contains("stiffleg")
                    || lower.Contains("qiosk") || lower.Contains("unit_trader_"))
                    return SyncCatTrader;

                if (lower.Contains("ghost") || lower.Contains("spirit") || lower.Contains("haunt"))
                    return SyncCatGhost;

                if (lower.Contains("chicken") || lower.Contains("ambient")
                    || lower.Contains("shanty") || lower.Contains("critter"))
                    return SyncCatAmbient;

                // Type-name checks
                string typeName = entity.GetType().Name;
                if (typeName.Contains("Trader") || typeName.Contains("ShopKeeper"))
                    return SyncCatTrader;
                if (typeName.Contains("Ghost")) return SyncCatGhost;

                // Default: assume combat enemy for Npc category
                if (typeName == "Npc") return SyncCatCombatEnemy;
            }
            catch { }
            return SyncCatUnknown;
        }

        // LD-Sandstorm / F4 Stage 3: is this runtime unit a boss, per the game's own UnitType.Boss flag (unitSO.unitType)?
        // Used to keep bosses out of the InteractiveNpc bucket (they carry dialog components but are combat entities).
        private static bool IsBossUnit(object entity)
        {
            try
            {
                var so = TryGetMemberValue(entity, "unitSO");
                if (so == null) return false;
                var ut = TryGetMemberValue(so, "unitType");
                if (ut == null) return false;
                int flags = Convert.ToInt32(ut);
                int bossBit = Convert.ToInt32(Enum.Parse(ut.GetType(), "Boss")); // UnitType.Boss (=8), read by name
                return (flags & bossBit) != 0;
            }
            catch { return false; }
        }

        private static bool IsCombatEnemyForSync(NetGameplayEntitySnapshot snapshot)
        {
            if (snapshot == null) return false;
            // Phase 5.3-H B: players are never combat enemies — never bind/quarantine them.
            if (IsPlayerEntitySnapshot(snapshot)) return false;
            int cat = snapshot.SyncCategory;
            // Unknown is treated as CombatEnemy to avoid false negatives.
            return cat == SyncCatCombatEnemy || cat == SyncCatUnknown;
        }

        // Phase 5.3-H B: identify a local/remote player entity so it can be excluded from the enemy
        // manifest, combat classification, and quarantine. Players must never enter enemy reconcile.
        private static bool IsPlayerEntitySnapshot(NetGameplayEntitySnapshot snapshot)
        {
            if (snapshot == null) return false;
            if (string.Equals(snapshot.Category, "Player", StringComparison.OrdinalIgnoreCase)) return true;
            string actor = snapshot.ActorName ?? "";
            if (actor.IndexOf("Unit_Player", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            string uid = snapshot.EntityId?.UnitIdentifier ?? "";
            if (uid.IndexOf("Unit_Player", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            string src = snapshot.Source ?? "";
            if (src.IndexOf("Player", StringComparison.OrdinalIgnoreCase) >= 0
                && src.IndexOf("Players", StringComparison.OrdinalIgnoreCase) < 0) return true;
            return false;
        }

        private static bool IsNonCombatForSync(NetGameplayEntitySnapshot snapshot)
        {
            if (snapshot == null) return false;
            int cat = snapshot.SyncCategory;
            return cat == SyncCatTrader || cat == SyncCatInteractNpc;
        }

        private static string SyncCatName(int cat) => cat switch
        {
            SyncCatCombatEnemy => "CombatEnemy",
            SyncCatTrader      => "Trader",
            SyncCatInteractNpc => "InteractiveNpc",
            SyncCatGhost       => "Ghost",
            SyncCatEventNpc    => "EventNpc",
            SyncCatAmbient     => "Ambient",
            SyncCatHazard      => "Hazard",
            _                  => "Unknown"
        };

        private static string CleanCategory(string category, object entity)
        {
            category = Clean(category);
            if (string.IsNullOrWhiteSpace(category) || category == "<unknown>")
            {
                string typeName = entity.GetType().Name;
                if (typeName.Contains("Breakable")) return "Breakable";
                if (typeName == "Npc") return "Npc";
                if (typeName == "Player") return "Player";
                return "Unit";
            }
            return category;
        }

        private static string GetActorName(object entity)
        {
            try
            {
                if (entity is UnityEngine.Object unityObject && !string.IsNullOrWhiteSpace(unityObject.name))
                    return Clean(unityObject.name);

                object? value = TryGetMemberValue(entity, "DisplayName")
                    ?? TryGetMemberValue(entity, "displayName")
                    ?? TryGetMemberValue(entity, "Name")
                    ?? TryGetMemberValue(entity, "name")
                    ?? TryGetMemberValue(entity, "unitName")
                    ?? TryGetMemberValue(entity, "UnitName");

                string result = Clean(value?.ToString());
                if (!string.IsNullOrWhiteSpace(result) && result != "<unknown>")
                    return result;
            }
            catch { }

            return entity.GetType().Name;
        }


        private static bool TryGetTransform(object entity, out Transform? transform)
        {
            transform = null;
            try
            {
                if (entity is Component component && component.transform != null)
                {
                    transform = component.transform;
                    return true;
                }

                if (entity is GameObject gameObject && gameObject.transform != null)
                {
                    transform = gameObject.transform;
                    return true;
                }

                object? transformValue = TryGetMemberValue(entity, "transform")
                    ?? TryGetMemberValue(entity, "Transform");
                if (transformValue is Transform value)
                {
                    transform = value;
                    return true;
                }
            }
            catch { }

            return false;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool TryGetRotationY(object entity, out float rotationY)
        {
            rotationY = 0f;
            try
            {
                if (entity is Component component && component.transform != null)
                {
                    rotationY = component.transform.eulerAngles.y;
                    return IsFinite(rotationY);
                }

                if (entity is GameObject gameObject && gameObject.transform != null)
                {
                    rotationY = gameObject.transform.eulerAngles.y;
                    return IsFinite(rotationY);
                }

                object? transformValue = TryGetMemberValue(entity, "transform")
                    ?? TryGetMemberValue(entity, "Transform");
                if (transformValue is Transform transform)
                {
                    rotationY = transform.eulerAngles.y;
                    return IsFinite(rotationY);
                }
            }
            catch { }

            return false;
        }

        private static bool TryGetPosition(object entity, out Vector3 position)
        {
            position = default;
            try
            {
                if (entity is Component component && component.transform != null)
                {
                    position = component.transform.position;
                    return true;
                }

                if (entity is GameObject gameObject && gameObject.transform != null)
                {
                    position = gameObject.transform.position;
                    return true;
                }

                object? transformValue = TryGetMemberValue(entity, "transform")
                    ?? TryGetMemberValue(entity, "Transform");
                if (transformValue is Transform transform)
                {
                    position = transform.position;
                    return true;
                }

                object? positionValue = TryGetMemberValue(entity, "position")
                    ?? TryGetMemberValue(entity, "Position");
                if (positionValue is Vector3 vector)
                {
                    position = vector;
                    return true;
                }
            }
            catch { }

            return false;
        }

        // Phase 4.4.0-O3: look up entity snapshot by object identity
        private static bool TryGetSnapshotForEntity(object entity, out NetGameplayEntitySnapshot? snapshot)
        {
            snapshot = null;
            if (entity == null) return false;
            try
            {
                var id = NetGameplayEntityId.FromObject(entity);
                string key = string.IsNullOrWhiteSpace(id.LocalInstanceId) || id.LocalInstanceId == "null"
                    ? id.CandidateKey : id.LocalInstanceId;
                if (string.IsNullOrWhiteSpace(key)) return false;
                return EntitiesByLocalId.TryGetValue(key, out snapshot);
            }
            catch { return false; }
        }

        private static object? TryGetMemberValue(object obj, string memberName)
        {
            try
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                Type type = obj.GetType();

                var prop = type.GetProperty(memberName, flags);
                if (prop != null && prop.GetIndexParameters().Length == 0)
                    return prop.GetValue(obj, null);

                var field = type.GetField(memberName, flags);
                if (field != null)
                    return field.GetValue(obj);
            }
            catch { }

            return null;
        }

        private static string Clean(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "<unknown>";
            string text = value.Trim();
            if (text.Length > 96) text = text.Substring(0, 96);
            return text;
        }

        // ================================================================
        // Phase 5.0 Host-Driven Proxy — public API
        // ================================================================

        /// <summary>
        /// Called by NetService (Host side) to update the player position hint used for
        /// interest management. NetService reads this from NetLocalPlayerTracker.
        /// </summary>
        public static void SetLocalPlayerPositionHint(Vector3 position)
        {
            _hostPlayerPositionHintValid = true;
            _hostPlayerPositionHint = position;
        }

        /// <summary>Phase 5.5-P1: Host updates the set of remote-player (client) interest positions each frame, so the
        /// interest manager treats every online player — not just the Host — as a source of "near" for snapshot rate.</summary>
        public static void SetRemotePlayerInterestPositions(List<Vector3> positions)
        {
            _remoteInterestPositions.Clear();
            if (positions != null) _remoteInterestPositions.AddRange(positions);
        }

        /// <summary>
        /// P0: gate called by ReceiveDamage Harmony patch (Client side).
        /// When EnableHostDrivenEnemyProxy and SuppressAllClientPuppetDamage are true,
        /// returns true for ANY damage where the source is a client puppet enemy.
        /// This prevents any client-local enemy AI from dealing real damage, regardless of
        /// whether an authorized window is open. Host-authoritative damage arrives via
        /// HostDamageRequest packets and is not suppressed here.
        /// </summary>
        public static bool ShouldSuppressAllClientPuppetDamage(object? npcSource)
        {
            if (!Plugin.Cfg.EnableHostDrivenEnemyProxy.Value) return false;
            if (!Plugin.Cfg.SuppressAllClientPuppetDamage.Value) return false;
            if (NetConfig.GetMode() != NetMode.Client) return false;
            if (npcSource == null) return false;

            // Check if the damaging entity is a known puppet.
            int npcId = ObjectIdentity(npcSource);
            if (npcId == 0) return false;

            bool isPuppet = ActiveEnemyPuppetsByNpcId.ContainsKey(npcId);

            // Also suppress for quarantined client-only entities (no host binding).
            if (!isPuppet)
            {
                var snap = FindSnapshotForRuntimeObject(npcSource);
                if (snap != null)
                {
                    string qKey = GetSnapshotTargetKey(snap);
                    if (!string.IsNullOrWhiteSpace(qKey) && ClientQuarantinedEntities.Contains(qKey))
                        isPuppet = true;
                }
            }

            if (!isPuppet) return false;

            _clientPuppetDamageSuppressed++;
            if (Plugin.Cfg.LogClientPuppetDamageSuppression.Value)
            {
                int id = npcId;
                if (ActiveEnemyPuppetsByNpcId.TryGetValue(id, out var rec))
                    Plugin.Log.Info($"[Phase5] Suppressed puppet damage src={rec.Snapshot.ActorName} idx={rec.Snapshot.SpawnIndex}");
            }
            return true;
        }

        /// <summary>
        /// P1: Client-side handler for received HostAttackPhaseEvent reliable packets.
        /// Applies the attack phase to the matching puppet enemy Animator without invoking
        /// native combat methods — pure Animator state drive.
        /// </summary>
        public static void ProcessHostAttackPhaseEvent(NetHostAttackPhaseEvent evt)
        {
            if (evt == null) return;
            if (!IsEnabled()) return;
            if (!Plugin.Cfg.EnableHostDrivenEnemyProxy.Value) return;
            if (!Plugin.Cfg.EnableClientAttackPhaseAnimatorDrive.Value) return;
            if (NetConfig.GetMode() != NetMode.Client) return;

            _clientAttackPhaseEventsReceived++;

            if (!NetRunStateBridge.TryGetLocalRunState(out var localState)) return;
            if (!evt.MatchesScene(localState)) return;

            // Resolve the puppet via roster binding.
            if (!ClientHostToLocalKeyByHostSpawnIndex.TryGetValue(evt.HostSpawnIndex, out var localKey))
            {
                if (Plugin.Cfg.LogHostAttackPhaseEvents.Value)
                    NetLogger.Info($"[AttackPhase] Client no roster binding for hostIdx={evt.HostSpawnIndex} unit={evt.UnitIdentifier} phase={evt.AttackPhase}");
                return;
            }

            if (!ActiveEnemyPuppets.TryGetValue(localKey, out var puppet))
            {
                if (Plugin.Cfg.LogHostAttackPhaseEvents.Value)
                    NetLogger.Info($"[AttackPhase] Client no active puppet for key={localKey} hostIdx={evt.HostSpawnIndex} phase={evt.AttackPhase}");
                return;
            }

            // Phase 5.3-C P0-1: terminal dead corpses must never play attack animations.
            if (IsClientTerminalDead(evt.HostSpawnIndex))
            {
                _terminalDeadBlockedAttackPhase++;
                if (Plugin.Cfg.LogClientTerminalDead.Value)
                    NetLogger.Info($"[TerminalDead] blocked AttackPhase hostIdx={evt.HostSpawnIndex} phase={evt.AttackPhase} reason=terminal-dead");
                return;
            }

            // De-duplicate by sequence.
            if (ClientAttackPhaseLastSeqBySpawnIndex.TryGetValue(evt.HostSpawnIndex, out int lastSeq)
                && evt.Sequence <= lastSeq
                && evt.AttackPhase != NetHostAttackPhaseEvent.PhaseNone)
            {
                return; // stale or duplicate
            }
            ClientAttackPhaseLastSeqBySpawnIndex[evt.HostSpawnIndex] = evt.Sequence;

            if (Plugin.Cfg.LogHostAttackPhaseEvents.Value)
                NetLogger.Info($"[AttackPhase] Client received hostIdx={evt.HostSpawnIndex} actor={puppet.Snapshot.ActorName} phase={evt.AttackPhase} kind={evt.AttackKind} seq={evt.Sequence} hasAnimHint={evt.HasAnimatorHint}");

            // Apply to puppet Animator.
            TryApplyAttackPhaseToClientPuppet(puppet, evt);
        }

        private static void TryApplyAttackPhaseToClientPuppet(EnemyPuppetRecord puppet, NetHostAttackPhaseEvent evt)
        {
            // Only apply Windup and Active phases to animator — Recovery/None let locomotion handle it.
            if (evt.AttackPhase != NetHostAttackPhaseEvent.PhaseWindup
                && evt.AttackPhase != NetHostAttackPhaseEvent.PhaseActive)
                return;

            try
            {
                // Ensure we have a cached animator.
                Animator? animator = puppet.CachedAnimator;
                if (animator == null && puppet.Npc != null)
                {
                    object? npc = puppet.Npc;
                    if (npc is Component c) animator = c.GetComponentInChildren<Animator>();
                    else if (npc is GameObject go) animator = go.GetComponentInChildren<Animator>();
                    if (animator != null)
                    {
                        puppet.CachedAnimator = animator;
                        puppet.CachedAnimatorId = animator.GetInstanceID();
                    }
                }

                if (animator == null || !animator.isActiveAndEnabled) return;

                // If we have an Animator state hint from the host, use CrossFade/Play.
                if (evt.HasAnimatorHint && evt.AnimatorFullPathHash != 0)
                {
                    float crossFade = Plugin.Cfg.ClientAttackPhaseCrossFadeSeconds.Value;
                    float normalizedTime = Mathf.Clamp01(evt.AnimatorNormalizedTime);

                    if (crossFade <= 0.001f)
                        animator.Play(evt.AnimatorFullPathHash, -1, normalizedTime);
                    else
                        animator.CrossFadeInFixedTime(evt.AnimatorFullPathHash, crossFade, -1, normalizedTime);

                    _clientAttackPhaseAnimatorApplies++;
                    if (Plugin.Cfg.LogHostAttackPhaseEvents.Value)
                        NetLogger.Info($"[AttackPhase] Client applied Animator state={evt.AnimatorFullPathHash} t={normalizedTime:F2} crossFade={crossFade:F3} idx={evt.HostSpawnIndex} actor={puppet.Snapshot.ActorName}");
                    return;
                }

                // No animator hint: pulse the attack bool if we have it cached.
                if (puppet.HasAttackParam)
                {
                    animator.SetBool(puppet.AttackParamHash, true);
                    _clientAttackPhaseAnimatorApplies++;

                    // Schedule reset after 0.5s so the attack animation plays through.
                    PendingAnimatorBoolResets.Add(new AnimatorBoolReset
                    {
                        Animator = animator,
                        Hash     = puppet.AttackParamHash,
                        ResetAt  = Time.realtimeSinceStartup + 0.5f,
                    });

                    if (Plugin.Cfg.LogHostAttackPhaseEvents.Value)
                        NetLogger.Info($"[AttackPhase] Client pulsed attack bool idx={evt.HostSpawnIndex} actor={puppet.Snapshot.ActorName} phase={evt.AttackPhase}");
                }
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[AttackPhase] Apply failed idx={evt.HostSpawnIndex}: {ex.Message}");
            }
        }

        // ================================================================
        // Phase 5.1: Host-authoritative enemy health sync
        // ================================================================

        /// <summary>
        /// Called from Npc_ReceiveDamage_Pre on Host to capture and broadcast enemy damage.
        /// Sends HostEnemyDamageEvent and (when health is readable) HostEnemyHealthState.
        /// </summary>
        public static void ReportHostNpcDamageForSync(object npc, float damage)
        {
            if (npc == null || damage <= 0f) return;
            try
            {
                if (NetConfig.GetMode() != NetMode.Host) return;
                if (!Plugin.Cfg.EnableHostEnemyDamageEventSync.Value) return;
                if (!IsEnabled()) return;

                var snapshot = TryFindSnapshotForNpc(npc);
                if (snapshot == null) return;
                // Skip non-combat NPCs (traders, interactive NPCs, ambient).
                if (IsNonCombatForSync(snapshot)) return;

                if (!NetRunStateBridge.TryGetLocalRunState(out var state) || !state.HasLevel) return;

                int spawnIdx = snapshot.SpawnIndex;
                // (2) Throttle per enemy — drop intermediate damage events during a burst (cosmetic flash; health
                // converges via the periodic enemy-state snapshot + the post-burst hit).
                if (Plugin.Cfg.EnableCombatEventCoalescing.Value
                    && ThrottlePerEntity(_enemyDmgEventLastSentBySpawnIdx, spawnIdx, Plugin.Cfg.EnemyDamageEventMinIntervalSeconds.Value, Time.realtimeSinceStartup))
                { _enemyDamageEventThrottled++; return; }

                // Assign sequence number per spawn index.
                HostDamageEventSeqBySpawnIndex.TryGetValue(spawnIdx, out int lastSeq);
                int seq = lastSeq + 1;
                HostDamageEventSeqBySpawnIndex[spawnIdx] = seq;

                // Prefix fires BEFORE ReceiveDamage runs — health is read post-damage by
                // Npc_ReceiveDamage_Post / ReportHostNpcHealthAfterDamage instead.
                // Here we only send the damage amount and sequence so clients can track timing.
                var evt = new NetHostEnemyDamageEvent
                {
                    ChapterName    = state.ChapterName,
                    LevelIndex     = state.LevelIndex,
                    HasLevelSeed   = state.HasLevelSeed,
                    LevelSeed      = state.LevelSeed,
                    HostSpawnIndex = spawnIdx,
                    UnitIdentifier = snapshot.EntityId.UnitIdentifier ?? "",
                    Sequence       = seq,
                    DamageAmount   = damage,
                    IsDead         = false, // resolved in postfix after damage applies
                    SentAt         = Time.realtimeSinceStartup,
                };

                NetGameplaySyncBridge.ReportHostEnemyDamageEvent(evt);
                _hostEnemyDamageEventsSent++;

                if (Plugin.Cfg.LogHostEnemyDamageEvents.Value)
                    NetLogger.Info($"[EnemyDamage] Host sent idx={spawnIdx} unit={evt.UnitIdentifier} dmg={damage:F1} (health state follows in postfix)");
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[EnemyDamage] ReportHostNpcDamageForSync failed: {ex.Message}");
            }
        }

        private static NetGameplayEntitySnapshot? TryFindSnapshotForNpc(object npc)
        {
            try { return GetOrCreateSnapshot(npc, "damage-sync", "Npc"); }
            catch { return null; }
        }

        /// <summary>
        /// Called from Npc_ReceiveDamage_Post (postfix) on Host to read actual post-damage health
        /// and broadcast a reliable HostEnemyHealthState to all clients.
        /// </summary>
        public static void ReportHostNpcHealthAfterDamage(object npc, float damageAmount)
        {
            if (npc == null) return;
            try
            {
                if (NetConfig.GetMode() != NetMode.Host) return;
                if (!Plugin.Cfg.EnableHostEnemyHealthStateSync.Value) return;
                if (!IsEnabled()) return;

                var snapshot = TryFindSnapshotForNpc(npc);
                if (snapshot == null) return;
                if (IsNonCombatForSync(snapshot)) return;

                if (!NetRunStateBridge.TryGetLocalRunState(out var state) || !state.HasLevel) return;

                // Phase 5.2: use native Unit.Stats API (GetCurrentHealth/Stats.GetStatus(92))
                // instead of field scanning — health lives in Unit.Stats, not plain fields.
                if (!TryReadUnitHealthNative(npc, out float currentHp, out float maxHp)) return;

                int spawnIdx = snapshot.SpawnIndex;
                HostDamageEventSeqBySpawnIndex.TryGetValue(spawnIdx, out int seq);

                bool isDead = currentHp <= 0f;

                // (2) Throttle intermediate health states per enemy (health converges via snapshot). ALWAYS send the
                // death state (isDead) so the client's death sync is never dropped.
                if (!isDead && Plugin.Cfg.EnableCombatEventCoalescing.Value
                    && ThrottlePerEntity(_enemyHealthLastSentBySpawnIdx, spawnIdx, Plugin.Cfg.EnemyDamageEventMinIntervalSeconds.Value, Time.realtimeSinceStartup))
                    return;

                bool hasPos = TryGetPosition(npc, out var pos);

                var healthState = new NetHostEnemyHealthState
                {
                    ChapterName         = state.ChapterName,
                    LevelIndex          = state.LevelIndex,
                    HasLevelSeed        = state.HasLevelSeed,
                    LevelSeed           = state.LevelSeed,
                    HostSpawnIndex      = spawnIdx,
                    UnitIdentifier      = snapshot.EntityId.UnitIdentifier ?? "",
                    Sequence            = seq,
                    HasCurrentHealth    = true,
                    CurrentHealth       = currentHp,
                    HasMaxHealth        = maxHp > 0f,
                    MaxHealth           = maxHp,
                    HasNormalizedHealth = maxHp > 0f,
                    NormalizedHealth    = maxHp > 0f ? Mathf.Clamp01(currentHp / maxHp) : 0f,
                    IsDead              = isDead,
                    HasPosition         = hasPos,
                    Position            = hasPos ? pos : Vector3.zero,
                    SentAt              = Time.realtimeSinceStartup,
                };

                NetGameplaySyncBridge.ReportHostEnemyHealthState(healthState);
                _hostEnemyHealthStatesSent++;

                if (Plugin.Cfg.LogHostEnemyHealthState.Value)
                {
                    float norm = maxHp > 0f ? Mathf.Clamp01(currentHp / maxHp) : 0f;
                    NetLogger.Info($"{NetDbg.Ctx("HealthState", seq: seq, hostIdx: spawnIdx, rev: _clientRosterRevision)} " +
                        $"sent hp={currentHp:F1}/{maxHp:F1} norm={norm:P0} dead={isDead} unit={healthState.UnitIdentifier}");
                }
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[EnemyHealth] ReportHostNpcHealthAfterDamage failed: {ex.Message}");
            }
        }

        // ----------------------------------------------------------------
        // Phase 5.3-B: Client → Host gameplay hit request pipeline
        // ----------------------------------------------------------------

        /// <summary>
        /// Called from Npc_ReceiveDamage_Pre on the CLIENT when a puppet NPC is about to take damage.
        /// Returns true if the request was sent (caller should suppress local damage).
        /// Returns false if not applicable (e.g. Host mode, not a puppet, no binding).
        /// </summary>
        /// <summary>
        /// Phase 5.5-RT3-A2: a host-driven puppet is host-authoritative over ALL its damage and death. The client must
        /// forward ONLY genuine local-player attacks; physics / explosion / environment damage (e.g. a barrel that a
        /// snapped add overlapped — exploding only on the client) must be dropped locally and NEVER wrapped as a
        /// ClientHitRequest. Returns true when the caller should swallow the damage (suppress local, do not forward).
        /// Conservative: only suppresses when we can POSITIVELY tell the player did not cause the hit — an unreadable
        /// source never blocks a real player hit.
        /// </summary>
        public static bool ShouldIgnoreNonPlayerPuppetDamage(object? npc, object? source)
        {
            try
            {
                if (npc == null) return false;
                if (NetConfig.GetMode() != NetMode.Client) return false;
                if (!Plugin.Cfg.FilterNonPlayerPuppetDamage.Value) return false;
                if (!IsClientEnemyPuppetNpc(npc)) return false;          // only host-driven puppets are host-authoritative

                bool nonPlayer = DamageSourceConfidentlyNonPlayer(source);
                // Diagnostic: log a throttled sample of BOTH outcomes so the next log shows what a real player hit's
                // DamageSourceData looks like vs an environment/physics one (confirms the discriminator is correct).
                if (Plugin.Cfg.LogClientHitRequests.Value && _puppetDamageSourceSamplesLogged < 12)
                {
                    _puppetDamageSourceSamplesLogged++;
                    NetLogger.Info($"[ClientHit] puppet damage source eval npc={ObjectIdentity(npc)} nonPlayer={nonPlayer} source={DescribeDamageSource(source)}");
                }
                if (!nonPlayer) return false;

                _clientPuppetNonPlayerDamageIgnored++;
                // Dedicated throttle so the REAL source of the suppressed (e.g. fixed-800) hits is captured even after
                // the shared sample budget is spent on player hits — this is what tells us what the 800 actually is.
                if (Plugin.Cfg.LogTeleportDiag.Value && _suppressedSourceSamplesLogged < 24)
                {
                    _suppressedSourceSamplesLogged++;
                    NetLogger.Info($"[ClientHit] SUPPRESSED non-player puppet dmg npc={ObjectIdentity(npc)} source={DescribeDamageSource(source)}");
                }
                return true;
            }
            catch { return false; }
        }

        // Real field (verified against PerfectRandom.Sulfur.Core.Units.DamageSourceData): public bool `isPlayer` —
        // set when the damage originated from the player (melee/weapon/projectile). Other fields: states,
        // sourcePosition, alertPosition, damageType, projectile, sourceUnit, sourceWeapon, instanceId, melee,
        // sourceEffect, name. (There is NO damagedByPlayer / factionId — earlier guess was wrong, log49 byPlayer=?.)
        // Conservative: only return true when we can POSITIVELY read isPlayer==false; an unreadable source is left
        // "unknown" → not suppressed, so a real player hit is never blocked.
        private static bool DamageSourceConfidentlyNonPlayer(object? source)
        {
            if (source == null) return false; // Npc.ReceiveDamage normally carries a DamageSourceData; null → don't risk it
            if (TryGetBoolMember(source, "isPlayer", out bool isPlayer)) return !isPlayer;
            return false; // can't read the source → don't suppress (never block a real player hit)
        }

        // Real DamageSourceData fields (verified by DLL reverse: states, sourcePosition, alertPosition, damageType,
        // projectile, sourceUnit, sourceWeapon, instanceId, melee, isPlayer, sourceEffect, name).
        private static string DescribeDamageSource(object? source)
        {
            if (source == null) return "null";
            try
            {
                string isPlayer = TryGetBoolMember(source, "isPlayer", out bool ip) ? ip.ToString() : "?";
                string melee = TryGetBoolMember(source, "melee", out bool m) ? m.ToString() : "?";
                return $"{source.GetType().Name}(isPlayer={isPlayer},melee={melee}," +
                       $"name={DescribeField(source, "name")},damageType={DescribeField(source, "damageType")}," +
                       $"sourceUnit={DescribeField(source, "sourceUnit")},sourceWeapon={DescribeField(source, "sourceWeapon")}," +
                       $"projectile={DescribeField(source, "projectile")},sourceEffect={DescribeField(source, "sourceEffect")}," +
                       $"instanceId={DescribeField(source, "instanceId")})";
            }
            catch { return source.GetType().Name; }
        }

        private static string DescribeField(object source, string field)
        {
            try
            {
                var v = TryGetMemberValue(source, field);
                if (v == null) return "null";
                if (v is string || v.GetType().IsValueType) return v.ToString();
                if (v is UnityEngine.Object uo) return $"{v.GetType().Name}:{uo.name}";
                return v.GetType().Name;
            }
            catch { return "?"; }
        }

        public static bool TrySendClientHitRequest(object? npc, float damage, object? damageType)
        {
            if (npc == null || damage <= 0f) return false;
            try
            {
                if (NetConfig.GetMode() != NetMode.Client) return false;
                if (!Plugin.Cfg.EnableClientHitRequest.Value) return false;
                if (!Plugin.Cfg.EnableHostDrivenEnemyProxy.Value) return false;

                // Phase 5.5-RT3-A: this is a boss-add captured but not yet bound+snapped to its host counterpart. Its
                // local position diverges from the host's, so a local-physics/explosion hit here would mis-kill a far-away
                // host add. Swallow the hit (no local damage, no host claim) until it binds and snaps to the host point.
                if (Boss.BossDynamicSpawnManifest.IsHitGated(npc))
                    return true;

                // Only intercept if NPC is a host-driven puppet.
                if (!IsClientEnemyPuppetNpc(npc))
                {
                    _clientHitRequestsSkippedNoPuppet++;
                    return false;
                }

                // Get local key and puppet record for this NPC instance.
                EnemyPuppetRecord? record = null;
                string? localKey = null;
                int npcId = ObjectIdentity(npc);
                if (npcId != 0 && ActiveEnemyPuppetsByNpcId.TryGetValue(npcId, out var foundRecord))
                {
                    record = foundRecord;
                    localKey = record.Key;
                }

                if (string.IsNullOrEmpty(localKey))
                {
                    _clientHitRequestsSkippedNoBinding++;
                    return false;
                }

                // Look up host spawn index via reverse binding.
                if (!ClientLocalKeyToHostSpawnIndex.TryGetValue(localKey, out int hostIdx))
                {
                    _clientHitRequestsSkippedNoBinding++;
                    return false;
                }

                // Phase 5.3-D P0: don't keep attacking a corpse — reduces Host "rejectDead".
                if (IsClientTerminalDead(hostIdx))
                {
                    _clientHitSkipTerminalDead++;
                    return true; // suppress local damage; target is already dead on host
                }
                if (IsClientPendingDead(hostIdx))
                {
                    _clientHitSkipPendingDead++;
                    _pendingDeadBlockedClientHit++;
                    return true; // suppress local damage; host already resolving this death
                }

                if (!NetRunStateBridge.TryGetLocalRunState(out var state) || !state.HasLevel)
                    return false;

                string unitIdentifier = record?.Snapshot?.EntityId?.UnitIdentifier ?? "";

                var req = new NetClientHitRequest
                {
                    ChapterName            = state.ChapterName,
                    LevelIndex             = state.LevelIndex,
                    HasLevelSeed           = state.HasLevelSeed,
                    LevelSeed              = state.LevelSeed,
                    RequestSeq             = ++_clientHitRequestSeq,
                    ClientPeerId           = state.PeerId ?? "",
                    TargetHostSpawnIndex   = hostIdx,
                    TargetUnitIdentifier   = unitIdentifier,
                    DamageCandidate        = damage,
                    HasAttackerPosition    = false,
                    SentAt                 = Time.realtimeSinceStartup,
                };

                NetGameplaySyncBridge.SendClientHitRequest(req);
                _clientHitRequestsSent++;
                _clientLocalHitPredicted++;
                // Record so the resulting HostEnemyHealthState can confirm this hit.
                _clientPendingHitByHostIdx[hostIdx] = Time.realtimeSinceStartup;

                if (Plugin.Cfg.LogClientHitRequests.Value)
                    NetLogger.Info($"[ClientHit] Sent seq={req.RequestSeq} hostIdx={hostIdx} unit={unitIdentifier} dmg={damage:F1} type={damageType} (predicted)");

                return true;
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[ClientHit] TrySendClientHitRequest failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Called on HOST when a ClientHitRequest packet arrives from a client peer.
        /// Validates scene, target, type, rate-limit, then applies damage via Stats.SetStatus.
        /// Existing HealthState/DeathEvent broadcast handles the result.
        /// </summary>
        /// <summary>Host Tick: apply coalesced damage whose target's rate-limit window has elapsed with no further hit to
        /// flush it (e.g. the last pellets of a burst). Without this, a burst's tail damage would be stranded.</summary>
        private static void FlushPendingClientHitDamage()
        {
            if (NetConfig.GetMode() != NetMode.Host) return;
            if (_hostHitPendingDamageByHostIdx.Count == 0) return;
            float rateLimit = Plugin.Cfg.ClientHitRequestRateLimitSeconds.Value;
            if (rateLimit <= 0f) { _hostHitPendingDamageByHostIdx.Clear(); return; }
            float now = Time.realtimeSinceStartup;
            if (!NetRunStateBridge.TryGetLocalRunState(out var hostState)) return;

            List<int>? ready = null;
            foreach (var kv in _hostHitPendingDamageByHostIdx)
            {
                _hostHitRequestLastAtByHostIdx.TryGetValue(kv.Key, out float lastAt);
                if (now - lastAt >= rateLimit) (ready ??= new List<int>()).Add(kv.Key);
            }
            if (ready == null) return;

            foreach (int hostIdx in ready)
            {
                float pending = _hostHitPendingDamageByHostIdx[hostIdx];
                _hostHitPendingDamageByHostIdx.Remove(hostIdx);
                if (pending <= 0f) continue;

                NetGameplayEntitySnapshot? target = null;
                foreach (var snap in EntitiesByLocalId.Values)
                    if (snap.SpawnIndex == hostIdx) { target = snap; break; }
                if (target == null || target.IsDead) continue; // target gone/dead — drop the stranded damage

                _hostHitRequestLastAtByHostIdx[hostIdx] = now;
                if (TryApplyHostHitDamage(target, pending, out _, out bool fatal))
                {
                    _hostHitRequestsDamageApplied++;
                    BroadcastHostHitVisual(target, hostState, fatal);
                    if (Plugin.Cfg.LogClientHitRequests.Value)
                        NetLogger.Info($"[ClientHit] FLUSH coalesced hostIdx={hostIdx} dmg={pending:F1} fatal={fatal}");
                }
            }
        }

        public static void ProcessClientHitRequest(NetClientHitRequest request, string peerId)
        {
            if (request == null) return;
            try
            {
                if (NetConfig.GetMode() != NetMode.Host) return;
                if (!Plugin.Cfg.EnableClientHitRequest.Value) return;
                if (!IsEnabled()) return;

                _hostHitRequestsRecv++;

                // Scene validation.
                if (!NetRunStateBridge.TryGetLocalRunState(out var hostState) || !request.MatchesScene(hostState))
                {
                    _hostHitRequestsRejectedScene++;
                    if (Plugin.Cfg.LogClientHitRequests.Value)
                        NetLogger.Warn($"[ClientHit] REJECT scene-mismatch peer={peerId} seq={request.RequestSeq} reqScene={request.SceneKey} hostScene={hostState?.ChapterName}:{hostState?.LevelIndex}");
                    return;
                }

                // Validate damage is positive.
                if (request.DamageCandidate <= 0f)
                {
                    _hostHitRequestsRejectedNoTarget++;
                    return;
                }

                // Find target entity by spawnIndex on host.
                NetGameplayEntitySnapshot? targetSnapshot = null;
                foreach (var snap in EntitiesByLocalId.Values)
                {
                    if (snap.SpawnIndex == request.TargetHostSpawnIndex) { targetSnapshot = snap; break; }
                }

                if (targetSnapshot == null)
                {
                    _hostHitRequestsRejectedNoTarget++;
                    if (Plugin.Cfg.LogClientHitRequests.Value)
                        NetLogger.Warn($"[ClientHit] REJECT no-target peer={peerId} seq={request.RequestSeq} hostIdx={request.TargetHostSpawnIndex}");
                    return;
                }

                // Unit identifier type guard.
                string hostUnitId = targetSnapshot.EntityId?.UnitIdentifier ?? "";
                if (!string.IsNullOrEmpty(request.TargetUnitIdentifier)
                    && !string.Equals(request.TargetUnitIdentifier, hostUnitId, StringComparison.Ordinal))
                {
                    _hostHitRequestsRejectedTypeMismatch++;
                    if (Plugin.Cfg.LogClientHitRequests.Value)
                        NetLogger.Warn($"[ClientHit] REJECT type-mismatch peer={peerId} seq={request.RequestSeq} clientUnit={request.TargetUnitIdentifier} hostUnit={hostUnitId}");
                    return;
                }

                // Dead check — don't damage already-dead entities.
                if (targetSnapshot.IsDead)
                {
                    _hostHitRequestsRejectedDead++;
                    if (Plugin.Cfg.LogClientHitRequests.Value)
                        NetLogger.Info($"[ClientHit] REJECT dead peer={peerId} seq={request.RequestSeq} hostIdx={request.TargetHostSpawnIndex} unit={hostUnitId}");
                    return;
                }

                // Rate limit per target — but COALESCE, don't drop. The limit exists to cap SetStatus/broadcast frequency,
                // not to discard damage. A hit inside the window adds its damage to the target's pending accumulator,
                // applied on the next accepted hit or flushed in Tick. Otherwise burst/multi-pellet weapons (8 pellets in
                // one frame on the same goblin) lose all but one hit (log52: 104/192 = 54% rate-limited away).
                float rateLimit = Plugin.Cfg.ClientHitRequestRateLimitSeconds.Value;
                if (rateLimit > 0f)
                {
                    float now = Time.realtimeSinceStartup;
                    _hostHitRequestLastAtByHostIdx.TryGetValue(request.TargetHostSpawnIndex, out float lastAt);
                    if (now - lastAt < rateLimit)
                    {
                        _hostHitPendingDamageByHostIdx.TryGetValue(request.TargetHostSpawnIndex, out float pend);
                        _hostHitPendingDamageByHostIdx[request.TargetHostSpawnIndex] = pend + request.DamageCandidate;
                        _hostHitRequestsCoalesced++;
                        return;
                    }
                    _hostHitRequestLastAtByHostIdx[request.TargetHostSpawnIndex] = now;
                }

                // Optional range check.
                float maxRange = Plugin.Cfg.ClientHitRequestMaxRangeMeters.Value;
                if (maxRange > 0f && request.HasAttackerPosition && TryGetPosition(targetSnapshot, out var targetPos))
                {
                    float dist = Vector3.Distance(request.AttackerPosition, targetPos);
                    if (dist > maxRange)
                    {
                        _hostHitRequestsRejectedRateLimit++;
                        if (Plugin.Cfg.LogClientHitRequests.Value)
                            NetLogger.Warn($"[ClientHit] REJECT range peer={peerId} seq={request.RequestSeq} dist={dist:F1} max={maxRange:F1}");
                        return;
                    }
                }

                _hostHitRequestsAccepted++;

                // Fold in any damage coalesced while this target was inside its rate-limit window.
                float applyDamage = request.DamageCandidate;
                if (_hostHitPendingDamageByHostIdx.TryGetValue(request.TargetHostSpawnIndex, out float pendingExtra) && pendingExtra > 0f)
                {
                    applyDamage += pendingExtra;
                    _hostHitPendingDamageByHostIdx.Remove(request.TargetHostSpawnIndex);
                }

                if (Plugin.Cfg.LogClientHitRequests.Value)
                    NetLogger.Info($"[ClientHit] ACCEPT peer={peerId} seq={request.RequestSeq} hostIdx={request.TargetHostSpawnIndex} unit={hostUnitId} dmg={applyDamage:F1}{(pendingExtra > 0f ? $" (+{pendingExtra:F1} coalesced)" : "")}");

                // Apply damage to the real host-side NPC.
                if (TryApplyHostHitDamage(targetSnapshot, applyDamage, out string applyResult, out bool fatal))
                {
                    _hostHitRequestsDamageApplied++;
                    // Phase 5.3-F: broadcast a hit-visual event so the client mirrors the white flash.
                    // (HealthState handles HP; this is visual-only.)
                    BroadcastHostHitVisual(targetSnapshot, hostState, fatal);
                }
                else
                {
                    _hostHitRequestsDamageFailed++;
                    NetLogger.Warn($"[ClientHit] Damage apply failed peer={peerId} seq={request.RequestSeq} hostIdx={request.TargetHostSpawnIndex} result={applyResult}");
                }
            }
            catch (Exception ex)
            {
                NetLogger.Warn($"[ClientHit] ProcessClientHitRequest failed: {ex.Message}");
            }
        }

        private static bool TryApplyHostHitDamage(NetGameplayEntitySnapshot snapshot, float damage, out string result, out bool fatal)
        {
            result = "";
            fatal = false;
            if (!snapshot.TryGetRuntimeObject(out var runtimeObj) || runtimeObj == null)
            { result = "noRuntimeObj"; return false; }

            if (runtimeObj is UnityEngine.Object u && u == null)
            { result = "unityDestroyed"; return false; }

            if (!TryReadUnitHealthNative(runtimeObj, out float currentHp, out _))
            { result = "cantReadHp"; return false; }

            if (currentHp <= 0f)
            { result = "alreadyDead"; return false; }

            float newHp = Mathf.Max(0f, currentHp - damage);
            fatal = newHp <= 0f;

            if (!TryWriteUnitHealthNative(runtimeObj, newHp))
            { result = $"writeHpFailed hp={currentHp:F1}→{newHp:F1}"; return false; }

            result = $"hp={currentHp:F1}→{newHp:F1} dmg={damage:F1}";

            // Trigger host health-state broadcast via existing pipeline.
            ReportHostNpcHealthAfterDamage(runtimeObj, damage);
            _hostHitResultHealthStateSent++;

            // Phase 5.3-G: play the white flash on the host's OWN real NPC for EVERY landed hit,
            // including the fatal one, so client attacks always produce host-visible feedback.
            // (The death animation still runs afterwards via the Die() call below.)
            if (Plugin.Cfg.EnableClientHitVisual.Value)
            {
                Renderer[]? hostRenderers = null;
                try
                {
                    if (runtimeObj is Component hc) hostRenderers = hc.GetComponentsInChildren<Renderer>(true);
                    else if (runtimeObj is GameObject hgo) hostRenderers = hgo.GetComponentsInChildren<Renderer>(true);
                }
                catch { }
                string path = InvokeNpcWhiteFlash(runtimeObj, hostRenderers, snapshot.SpawnIndex);
                if (path != "none" && path != "none-noMaterial")
                {
                    _hostHitVisualPlayed++;
                    if (fatal) _hostHitFatalVisualPlayed++;
                }
                else _hostHitVisualFailedNoNpc++;
                if (Plugin.Cfg.LogClientHitFlash.Value)
                    NetLogger.Info($"[HitVisual] Host local flash hostIdx={snapshot.SpawnIndex} unit={snapshot.EntityId.UnitIdentifier} fatal={fatal} path={path}");
            }

            // If HP hit zero, trigger NPC death on host via reflection.
            if (newHp <= 0f && !snapshot.IsDead)
            {
                try
                {
                    var dieMi = runtimeObj.GetType()
                        .GetMethod("Die", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                    if (dieMi != null)
                    {
                        dieMi.Invoke(runtimeObj, null);
                        result += " triggered-die";
                    }
                    else
                        result += " die-notfound";
                }
                catch (Exception ex)
                {
                    result += $" die-failed({ex.GetType().Name})";
                }
            }

            return true;
        }

        // Helper: get position from a snapshot's runtime object for range checking.
        private static bool TryGetPosition(NetGameplayEntitySnapshot snapshot, out Vector3 pos)
        {
            pos = Vector3.zero;
            if (!snapshot.TryGetRuntimeObject(out var obj) || obj == null) return false;
            return TryGetPosition(obj, out pos);
        }

        // Phase 5.3-F: Host broadcasts a hit-visual event so the client mirrors the white flash.
        private static void BroadcastHostHitVisual(NetGameplayEntitySnapshot snapshot, NetRunState state, bool fatal)
        {
            if (!Plugin.Cfg.EnableClientHitVisual.Value) return;
            try
            {
                var evt = new NetHostHitVisualEvent
                {
                    ChapterName    = state.ChapterName ?? "",
                    LevelIndex     = state.LevelIndex,
                    HasLevelSeed   = state.HasLevelSeed,
                    LevelSeed      = state.HasLevelSeed ? state.LevelSeed : 0,
                    HostSpawnIndex = snapshot.SpawnIndex,
                    UnitIdentifier = snapshot.EntityId.UnitIdentifier ?? "",
                    Sequence       = ++_hostHitVisualSeq,
                    IsFatal        = fatal,
                    SentAt         = Time.realtimeSinceStartup,
                };
                NetGameplaySyncBridge.ReportHostHitVisualEvent(evt);
                _hostHitVisualEventSent++;
                if (Plugin.Cfg.LogClientHitFlash.Value)
                    NetLogger.Info($"[HitVisual] Host sent hit-visual hostIdx={evt.HostSpawnIndex} unit={evt.UnitIdentifier} seq={evt.Sequence} fatal={fatal}");
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[HitVisual] BroadcastHostHitVisual failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Client: received a host hit-visual event — play the native white flash on the matching
        /// puppet. Visual-only; never touches health/death.
        /// </summary>
        public static void ProcessHostHitVisualEvent(NetHostHitVisualEvent evt)
        {
            if (evt == null) return;
            if (NetConfig.GetMode() != NetMode.Client) return;
            try
            {
                _clientHitVisualEventRecv++;

                if (!NetRunStateBridge.TryGetLocalRunState(out var localState) || !evt.MatchesScene(localState)) return;

                // Phase 5.3-G: a fatal hit STILL gets one flash (the killing-blow feedback) unless the
                // corpse is already terminal-dead/animating its death. Pending-dead allows the flash.
                if (IsClientTerminalDead(evt.HostSpawnIndex))
                {
                    if (evt.IsFatal) _clientHitVisualSkippedFatalTerminalDead++;
                    else _clientHitVisualSkipTerminalDead++;
                    return;
                }
                if (!evt.IsFatal && IsClientPendingDead(evt.HostSpawnIndex)) { _clientHitVisualSkipPendingDead++; return; }

                if (!ClientHostToLocalKeyByHostSpawnIndex.TryGetValue(evt.HostSpawnIndex, out var localKey)
                    || !ActiveEnemyPuppets.TryGetValue(localKey, out var puppet))
                {
                    _clientHitVisualSkipNoBinding++;
                    return;
                }

                // De-duplicate by sequence.
                if (evt.Sequence > 0
                    && _clientHitVisualLastSeqByHostIdx.TryGetValue(evt.HostSpawnIndex, out int lastSeq)
                    && evt.Sequence <= lastSeq)
                {
                    _clientHitVisualDuplicateSeq++;
                    return;
                }
                if (evt.Sequence > 0) _clientHitVisualLastSeqByHostIdx[evt.HostSpawnIndex] = evt.Sequence;

                object? npc = puppet.Npc;
                if (npc == null || (npc is UnityEngine.Object u && u == null)) { _clientHitVisualSkipNoBinding++; return; }

                var renderers = GetPuppetRenderers(puppet);
                string path = InvokeNpcWhiteFlash(npc, renderers, evt.HostSpawnIndex);
                if (path != "none" && path != "none-noMaterial")
                {
                    _clientHitVisualPlayed++;
                    if (evt.IsFatal) _clientHitFatalVisualPlayed++;
                    if (Plugin.Cfg.LogClientHitFlash.Value)
                        NetLogger.Info($"[HitVisual] Client played flash hostIdx={evt.HostSpawnIndex} unit={evt.UnitIdentifier} seq={evt.Sequence} fatal={evt.IsFatal} path={path}");
                }
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[HitVisual] ProcessHostHitVisualEvent failed: {ex.Message}");
            }
        }

        // ----------------------------------------------------------------
        // Phase 5.3-D P0-2: Two-phase death state (PendingDead → TerminalDead)
        // ----------------------------------------------------------------

        /// <summary>
        /// Host reports the enemy is dead (hp&lt;=0 / isDead) but the client death VISUAL has
        /// not run yet. This must NOT block HostDeathEvent apply or the death animation — it only
        /// suppresses new hit flashes / new ClientHitRequests / clearly-non-death combat.
        /// </summary>
        private static void MarkClientPendingDead(int hostIdx, string reason)
        {
            if (hostIdx < 0) return;
            if (!Plugin.Cfg.EnableClientPendingDeadState.Value) return;
            if (NetConfig.GetMode() != NetMode.Client) return;
            if (_clientTerminalDeadHostIdx.Contains(hostIdx)) return; // already terminal
            if (_clientPendingDeadHostIdx.ContainsKey(hostIdx)) return;

            _clientPendingDeadHostIdx[hostIdx] = new PendingDeadEntry
            {
                MarkedAt = Time.realtimeSinceStartup,
                Reason = reason,
            };
            _pendingDeadMarked++;
            if (Plugin.Cfg.LogClientPendingDead.Value)
                NetLogger.Info($"[PendingDead] mark hostIdx={hostIdx} unit={UnitForHostIdx(hostIdx)} reason={Clean(reason)}");
        }

        /// <summary>
        /// The death VISUAL has actually been applied (Npc.Die succeeded, or the visual-only shim
        /// set Animator Dead). Now the corpse is final — block all non-death overrides.
        /// HealthState/hp must NEVER reach this directly: that is tracked by
        /// _terminalDeadMarkedFromHealthOnly which must stay 0.
        /// </summary>
        private static void MarkClientTerminalDead(int hostIdx, string reason)
        {
            if (hostIdx < 0) return;
            if (!Plugin.Cfg.EnableClientTerminalDeadLatch.Value) return;
            if (NetConfig.GetMode() != NetMode.Client) return;

            // Regression guard: terminal-dead must only come from a real death visual path.
            if (reason != null && reason.IndexOf("HealthState", StringComparison.OrdinalIgnoreCase) >= 0)
                _terminalDeadMarkedFromHealthOnly++;

            _clientPendingDeadHostIdx.Remove(hostIdx);
            if (_clientTerminalDeadHostIdx.Add(hostIdx))
            {
                if (Plugin.Cfg.LogClientTerminalDead.Value)
                    NetLogger.Info($"[TerminalDead] mark hostIdx={hostIdx} unit={UnitForHostIdx(hostIdx)} reason={Clean(reason)}");
            }
        }

        private static string UnitForHostIdx(int hostIdx)
        {
            if (ClientHostToLocalKeyByHostSpawnIndex.TryGetValue(hostIdx, out var k)
                && EntitiesByLocalId.TryGetValue(k, out var snap) && snap != null)
                return snap.EntityId?.UnitIdentifier ?? "";
            return "";
        }

        private static bool IsClientTerminalDead(int hostIdx)
            => Plugin.Cfg.EnableClientTerminalDeadLatch.Value
               && hostIdx >= 0
               && _clientTerminalDeadHostIdx.Contains(hostIdx);

        private static bool IsClientTerminalDeadByKey(string? key)
            => Plugin.Cfg.EnableClientTerminalDeadLatch.Value
               && !string.IsNullOrEmpty(key)
               && ClientLocalKeyToHostSpawnIndex.TryGetValue(key!, out int idx)
               && _clientTerminalDeadHostIdx.Contains(idx);

        private static bool IsClientPendingDead(int hostIdx)
            => hostIdx >= 0 && _clientPendingDeadHostIdx.ContainsKey(hostIdx);

        private static bool IsClientPendingDeadByKey(string? key)
            => !string.IsNullOrEmpty(key)
               && ClientLocalKeyToHostSpawnIndex.TryGetValue(key!, out int idx)
               && _clientPendingDeadHostIdx.ContainsKey(idx);

        /// <summary>
        /// Tick: for PendingDead enemies that have not received a HostDeathEvent within the grace
        /// window, optionally run a visual-only death shim so the corpse plays its death animation
        /// instead of freezing standing. This never triggers gameplay death (loot/exp/analytics).
        /// </summary>
        private static void UpdateClientPendingDead()
        {
            if (_clientPendingDeadHostIdx.Count == 0) return;
            if (!Plugin.Cfg.EnableClientDeathVisualFallback.Value) return;
            if (NetConfig.GetMode() != NetMode.Client) return;

            float now = Time.realtimeSinceStartup;
            float delay = Plugin.Cfg.ClientDeathVisualFallbackDelaySeconds.Value;
            if (delay < 0f) delay = 0f;

            // Collect keys first — the dictionary is mutated by MarkClientTerminalDead.
            List<int>? toProcess = null;
            foreach (var kv in _clientPendingDeadHostIdx)
            {
                if (kv.Value.VisualFallbackAttempted) continue;
                if (now - kv.Value.MarkedAt < delay) continue;
                (toProcess ??= new List<int>()).Add(kv.Key);
            }
            if (toProcess == null) return;

            foreach (int hostIdx in toProcess)
            {
                if (!_clientPendingDeadHostIdx.TryGetValue(hostIdx, out var entry)) continue;
                entry.VisualFallbackAttempted = true;
                _pendingDeadVisualFallbackAttempted++;

                if (TryApplyVisualOnlyDeathShim(hostIdx, out string detail))
                {
                    _pendingDeadVisualFallbackSucceeded++;
                    _terminalDeadMarkedAfterVisualFallback++;
                    MarkClientTerminalDead(hostIdx, "VisualFallback death shim");
                    if (Plugin.Cfg.LogClientPendingDead.Value)
                        NetLogger.Info($"[PendingDead] visual-only death shim applied hostIdx={hostIdx} {detail} -> terminal");
                }
                else
                {
                    _pendingDeadVisualFallbackFailed++;
                    if (Plugin.Cfg.LogClientPendingDead.Value)
                        NetLogger.Info($"[PendingDead] visual-only death shim FAILED hostIdx={hostIdx} {detail}");
                }
            }
        }

        /// <summary>
        /// Visual-only subset of Npc.Die(): sets Animator "Dead"/"DeathRight", disables nav/RVO,
        /// toggles off the behaviour tree. Deliberately avoids SpawnLoot/GiveExperience/Analytics/
        /// RegisterKill/Unit.Die/GameManager removal so no gameplay side-effects fire on the client.
        /// </summary>
        private static bool TryApplyVisualOnlyDeathShim(int hostIdx, out string detail)
        {
            detail = "";
            if (!ClientHostToLocalKeyByHostSpawnIndex.TryGetValue(hostIdx, out var key)
                || !ActiveEnemyPuppets.TryGetValue(key, out var puppet))
            { detail = "no puppet"; return false; }

            object? npc = puppet.Npc;
            if (npc == null || (npc is UnityEngine.Object u && u == null)) { detail = "npc null/destroyed"; return false; }

            try
            {
                Animator? animator = puppet.CachedAnimator;
                if (animator == null)
                {
                    if (npc is Component c) animator = c.GetComponentInChildren<Animator>(true);
                    else if (npc is GameObject go) animator = go.GetComponentInChildren<Animator>(true);
                    if (animator != null) { puppet.CachedAnimator = animator; puppet.CachedAnimatorId = animator.GetInstanceID(); }
                }
                if (animator == null) { detail = "no animator"; return false; }

                // Reset combat triggers so no attack pose lingers.
                TryResetAnimatorTrigger(animator, "Attack");
                TryResetAnimatorTrigger(animator, "Jump");
                TryResetAnimatorTrigger(animator, "Land");

                // Disable steering so the corpse can't drift.
                TryInvokeNoArgOrBool(puppet.AiAgent, "SetNavMeshAgentState", false);
                TryInvokeNoArgOrBool(puppet.AiAgent, "ToggleRVO", false);

                bool deathRight = UnityEngine.Random.Range(0, 2) == 0;
                animator.SetBool("Dead", true);
                animator.SetBool("DeathRight", deathRight);

                // Toggle off behaviour tree (best-effort, bool param).
                TryInvokeNoArgOrBool(npc, "ToggleBehaviourTree", false);

                detail = $"animator Dead=true DeathRight={deathRight}";
                return true;
            }
            catch (Exception ex)
            {
                detail = $"exception {ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        private static void TryResetAnimatorTrigger(Animator animator, string trigger)
        {
            try { animator.ResetTrigger(trigger); } catch { }
        }

        private static void TryInvokeNoArgOrBool(object? target, string methodName, bool boolArg)
        {
            if (target == null) return;
            try
            {
                Type t = target.GetType();
                const BindingFlags bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var miBool = t.GetMethod(methodName, bf, null, new[] { typeof(bool) }, null);
                if (miBool != null) { miBool.Invoke(target, new object[] { boolArg }); return; }
                var miVoid = t.GetMethod(methodName, bf, null, Type.EmptyTypes, null);
                if (miVoid != null) miVoid.Invoke(target, null);
            }
            catch { /* best-effort visual shim */ }
        }

        // ----------------------------------------------------------------
        // Phase 5.3-D P0-1: Visual-only hit flash via native Npc.DoWhiteFlash()
        // ----------------------------------------------------------------

        /// <summary>
        /// Plays the native white hit flash on a host-bound puppet via Npc.DoWhiteFlash()
        /// (fallback SetHitEffect(1), then material _HitTime/_HitType, then MPB tint).
        /// Never modifies health or calls ReceiveDamage. Skipped for pending/terminal dead.
        /// </summary>
        private static void TryPlayDamageVisualReaction(int hostIdx, int seq, string unit, string source)
        {
            try
            {
                if (!Plugin.Cfg.EnableClientHitFlash.Value) return;
                if (NetConfig.GetMode() != NetMode.Client) return;

                if (IsClientTerminalDead(hostIdx))
                {
                    _damageVisualReactionSkippedTerminalDead++;
                    if (Plugin.Cfg.LogClientHitFlash.Value)
                        NetLogger.Info($"[DamageVisual] skip hostIdx={hostIdx} seq={seq} reason=terminal-dead");
                    return;
                }
                if (IsClientPendingDead(hostIdx))
                {
                    _damageVisualReactionSkippedPendingDead++;
                    _pendingDeadBlockedHitFlash++;
                    if (Plugin.Cfg.LogClientHitFlash.Value)
                        NetLogger.Info($"[DamageVisual] skip hostIdx={hostIdx} seq={seq} reason=pending-dead");
                    return;
                }

                if (!ClientHostToLocalKeyByHostSpawnIndex.TryGetValue(hostIdx, out var key)
                    || !ActiveEnemyPuppets.TryGetValue(key, out var puppet))
                {
                    _damageVisualReactionSkippedNoBinding++;
                    return;
                }

                // De-duplicate by damage-event sequence.
                if (seq > 0
                    && _clientDamageVisualLastSeqByHostIdx.TryGetValue(hostIdx, out int lastSeq)
                    && seq <= lastSeq)
                {
                    _damageVisualReactionSkippedDuplicateSeq++;
                    return;
                }
                if (seq > 0) _clientDamageVisualLastSeqByHostIdx[hostIdx] = seq;

                object? npc = puppet.Npc;
                if (npc == null || (npc is UnityEngine.Object u && u == null))
                {
                    _dmgVisualFailedNoNpc++;
                    if (Plugin.Cfg.LogClientHitFlash.Value)
                        NetLogger.Info($"[DamageVisual] fail hostIdx={hostIdx} seq={seq} reason=no-npc");
                    return;
                }

                string path = TryPlayNativeWhiteFlash(npc, puppet, hostIdx);
                if (path != "none")
                {
                    _damageVisualReactionsPlayed++;
                    if (Plugin.Cfg.LogClientHitFlash.Value)
                        NetLogger.Info($"[DamageVisual] {path} hostIdx={hostIdx} seq={seq} unit={unit} source={source}");
                }
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[DamageVisual] TryPlayDamageVisualReaction failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Tries, in order: Npc.DoWhiteFlash() → Npc.SetHitEffect(1) → material _HitTime/_HitType →
        /// MaterialPropertyBlock _Color/_EmissionColor tint. Returns the path name used or "none".
        /// </summary>
        private static string TryPlayNativeWhiteFlash(object npc, EnemyPuppetRecord puppet, int hostIdx)
        {
            var renderers = GetPuppetRenderers(puppet);
            string path = InvokeNpcWhiteFlash(npc, renderers, hostIdx);
            // Map the path used to the client-side damage-visual counters.
            switch (path)
            {
                case "DoWhiteFlash":              _dmgVisualNativeDoWhiteFlash++; break;
                case "SetHitEffect(1)":           _dmgVisualNativeSetHitEffect++; break;
                case "material _HitTime/_HitType":_dmgVisualMaterialHitTime++;    break;
                case "fallback MPB _Color tint":  _dmgVisualFallbackColor++;      break;
                case "none-noMaterial":           _dmgVisualFailedNoMaterial++;   break;
                case "none":                      _dmgVisualFailedNoMethod++;     break;
            }
            return path;
        }

        /// <summary>
        /// Shared white-flash core used by BOTH the client puppet path and the host-local hit-visual
        /// path. Tries Npc.DoWhiteFlash() → SetHitEffect(1) → material _HitTime/_HitType → MPB tint.
        /// Does NOT increment any counters — the caller maps the returned path string to its own
        /// counter set. Never calls ReceiveDamage.
        /// </summary>
        /// <summary>Phase 5.4-F2: play the native white-flash hit feedback on a boss Unit (reuses the regular-enemy
        /// flash path). Visual ONLY — never touches health or ReceiveDamage. Used by the Client when the Host
        /// confirms a boss hit it routed.</summary>
        public static bool TryPlayBossHitVisual(object unit)
        {
            if (unit == null) return false;
            try
            {
                Renderer[]? renderers = null;
                if (unit is Component c) renderers = c.GetComponentsInChildren<Renderer>(true);
                else if (unit is GameObject go) renderers = go.GetComponentsInChildren<Renderer>(true);
                string path = InvokeNpcWhiteFlash(unit, renderers, -1);
                return path != "none" && path != "none-noMaterial";
            }
            catch { return false; }
        }

        private static string InvokeNpcWhiteFlash(object npc, Renderer[]? renderers, int hostIdx)
        {
            Type t = npc.GetType();
            string typeName = t.FullName ?? t.Name;
            const BindingFlags bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            // ── 1. DoWhiteFlash() ────────────────────────────────────────────
            if (!_npcDoWhiteFlashCache.TryGetValue(typeName, out var doFlashMi))
            {
                doFlashMi = null;
                for (Type? tt = t; tt != null && doFlashMi == null; tt = tt.BaseType)
                    doFlashMi = tt.GetMethod("DoWhiteFlash", bf, null, Type.EmptyTypes, null);
                _npcDoWhiteFlashCache[typeName] = doFlashMi;
                LogFlashApiOnce(typeName, "DoWhiteFlash", doFlashMi);
            }
            if (doFlashMi != null)
            {
                try { doFlashMi.Invoke(npc, null); return "DoWhiteFlash"; }
                catch (Exception ex) { if (Plugin.Cfg.EnableDebugLog.Value) NetLogger.Debug($"[DamageVisual] DoWhiteFlash threw: {ex.Message}"); }
            }

            // ── 2. SetHitEffect(1) ───────────────────────────────────────────
            if (!_npcSetHitEffectCache.TryGetValue(typeName, out var setHitMi))
            {
                setHitMi = null;
                for (Type? tt = t; tt != null && setHitMi == null; tt = tt.BaseType)
                    setHitMi = tt.GetMethod("SetHitEffect", bf, null, new[] { typeof(int) }, null);
                _npcSetHitEffectCache[typeName] = setHitMi;
                LogFlashApiOnce(typeName, "SetHitEffect(int)", setHitMi);
            }
            if (setHitMi != null)
            {
                try { setHitMi.Invoke(npc, new object[] { 1 }); return "SetHitEffect(1)"; }
                catch (Exception ex) { if (Plugin.Cfg.EnableDebugLog.Value) NetLogger.Debug($"[DamageVisual] SetHitEffect threw: {ex.Message}"); }
            }

            // ── 3. material _HitTime / _HitType ──────────────────────────────
            if (renderers != null && renderers.Length > 0)
            {
                int applied = 0;
                float hitTime = Time.time;
                foreach (var r in renderers)
                {
                    if (r == null) continue;
                    try
                    {
                        var mat = r.material; // SULFUR's SetMaterialFloat uses .material (instance)
                        if (mat == null) continue;
                        if (mat.HasProperty(_hitFlashHitTimePropId)) mat.SetFloat(_hitFlashHitTimePropId, hitTime);
                        if (mat.HasProperty(_hitFlashHitTypePropId)) mat.SetFloat(_hitFlashHitTypePropId, 1f);
                        applied++;
                    }
                    catch { }
                }
                if (applied > 0)
                {
                    if (Plugin.Cfg.LogClientHitFlash.Value)
                        NetLogger.Info($"[DamageVisual] fallback material _HitTime/_HitType hostIdx={hostIdx} renderers={applied}");
                    return "material _HitTime/_HitType";
                }

                // ── 4. Last resort: MPB color tint (not preferred) ───────────
                var mpb = new MaterialPropertyBlock();
                Color flash = Color.white;
                int tinted = 0;
                foreach (var r in renderers)
                {
                    if (r == null) continue;
                    r.GetPropertyBlock(mpb);
                    mpb.SetColor(_hitFlashColorPropId, flash);
                    mpb.SetColor(_hitFlashEmissionPropId, flash * 0.6f);
                    r.SetPropertyBlock(mpb);
                    tinted++;
                }
                if (tinted > 0)
                {
                    float dur = Plugin.Cfg.ClientHitFlashDurationSeconds.Value;
                    if (dur < 0.02f) dur = 0.02f;
                    _pendingHitFlashes.Add(new PendingHitFlash { Renderers = renderers, ResetAt = Time.realtimeSinceStartup + dur });
                    return "fallback MPB _Color tint";
                }

                return "none-noMaterial";
            }

            return "none";
        }

        private static void LogFlashApiOnce(string typeName, string label, MethodInfo? mi)
        {
            string key = label + ":" + typeName;
            if (!_flashApiLoggedTypes.Add(key)) return;
            if (mi != null)
            {
                var ps = mi.GetParameters();
                string sig = string.Join(", ", System.Array.ConvertAll(ps, p => p.ParameterType.Name + " " + p.Name));
                NetLogger.Info($"[DamageVisual] resolved {mi.DeclaringType?.Name ?? typeName}.{mi.Name}({sig}) returns {mi.ReturnType.Name}");
            }
            else
            {
                NetLogger.Info($"[DamageVisual] {label} not found on {typeName} — will try next fallback");
            }
        }

        private static Renderer[]? GetPuppetRenderers(EnemyPuppetRecord puppet)
        {
            if (puppet.RenderersCached) return puppet.CachedRenderers;
            puppet.RenderersCached = true;
            try
            {
                object? npc = puppet.Npc;
                if (npc is Component c)
                    puppet.CachedRenderers = c.GetComponentsInChildren<Renderer>(true);
                else if (npc is GameObject go)
                    puppet.CachedRenderers = go.GetComponentsInChildren<Renderer>(true);
                else
                    puppet.CachedRenderers = null;
            }
            catch { puppet.CachedRenderers = null; }
            return puppet.CachedRenderers;
        }

        private static void UpdateClientHitFlashes()
        {
            if (_pendingHitFlashes.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            for (int i = _pendingHitFlashes.Count - 1; i >= 0; i--)
            {
                var f = _pendingHitFlashes[i];
                if (now < f.ResetAt) continue;
                try
                {
                    var empty = new MaterialPropertyBlock();
                    foreach (var r in f.Renderers)
                        if (r != null) r.SetPropertyBlock(empty);
                }
                catch { /* renderer destroyed — ignore */ }
                _pendingHitFlashes.RemoveAt(i);
            }
        }

        // ----------------------------------------------------------------
        // Phase 5.2: Native Unit Stats health API
        // Reverse-engineered: health is Unit.Stats.GetStatus(92) / GetAttribute(60).
        // EntityAttributes.Status_CurrentHealth = 92, Stat_MaxHealth = 60.
        // ----------------------------------------------------------------

        // Per-type method/member caches — populated once, reused.
        private static readonly Dictionary<string, MethodInfo?> _unitGetHpMiCache       = new Dictionary<string, MethodInfo?>();
        private static readonly Dictionary<string, MethodInfo?> _unitGetNormHpMiCache   = new Dictionary<string, MethodInfo?>();
        private static readonly Dictionary<string, PropertyInfo?> _unitStatsPropCache   = new Dictionary<string, PropertyInfo?>();
        private static readonly Dictionary<string, FieldInfo?>    _unitStatsFieldCache  = new Dictionary<string, FieldInfo?>();
        private static readonly Dictionary<string, MethodInfo?>   _statsGetStatusCache  = new Dictionary<string, MethodInfo?>();
        private static readonly Dictionary<string, MethodInfo?>   _statsGetAttrCache    = new Dictionary<string, MethodInfo?>();
        private static readonly Dictionary<string, MethodInfo?>   _statsSetStatusCache  = new Dictionary<string, MethodInfo?>();
        private static readonly HashSet<string> _nativeApiLoggedTypes = new HashSet<string>();

        private const int EntityStatusCurrentHealth = 92;
        private const int EntityStatMaxHealth       = 60;

        // ----------------------------------------------------------------
        // Legacy field-scan fallback (kept for TryWriteNpcHealth discovery cache compat).
        // Phase 5.2: TryReadNpcHealth replaced by TryReadUnitHealthNative.
        // ----------------------------------------------------------------

        // Fast-path names tried first (exact, case-sensitive).
        private static readonly string[] NpcHealthFieldNamesExact = {
            "health", "Health", "currentHealth", "CurrentHealth", "_health",
            "hp", "HP", "Hp", "_hp", "hitPoints", "HitPoints", "_hitPoints",
            "lifePoints", "LifePoints", "vitality", "Vitality"
        };
        private static readonly string[] NpcMaxHpFieldNamesExact = {
            "maxHealth", "MaxHealth", "maxHp", "MaxHp", "_maxHealth", "maxHP", "MaxHP",
            "maxHitPoints", "MaxHitPoints", "maxVitality", "MaxVitality"
        };

        // Keywords used in discovery scan (case-insensitive contains).
        private static readonly string[] HealthKeywords = { "health", "hp", "hitpoint", "lifepoint", "vitality", "leben" };

        // Cache: type full-name → discovered field/property name for current HP (null = not found).
        private static readonly Dictionary<string, string?> DiscoveredCurrentHpMember = new Dictionary<string, string?>();
        private static readonly Dictionary<string, string?> DiscoveredMaxHpMember     = new Dictionary<string, string?>();

        // ----------------------------------------------------------------
        // Native Stats API helpers (Phase 5.2)
        // ----------------------------------------------------------------

        private static object? GetUnitStatsObject(object unit, Type t, string typeName)
        {
            // Try property "Stats" first (likely public on Unit).
            if (!_unitStatsPropCache.TryGetValue(typeName, out var prop))
            {
                prop = null;
                for (Type? tt = t; tt != null && prop == null; tt = tt.BaseType)
                    prop = tt.GetProperty("Stats", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _unitStatsPropCache[typeName] = prop;
            }
            if (prop != null) { try { return prop.GetValue(unit, null); } catch { } }

            // Fallback: field named "stats" or "Stats".
            if (!_unitStatsFieldCache.TryGetValue(typeName, out var fld))
            {
                fld = null;
                const BindingFlags bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                for (Type? tt = t; tt != null && fld == null; tt = tt.BaseType)
                    fld = tt.GetField("Stats", bf) ?? tt.GetField("stats", bf) ?? tt.GetField("_stats", bf);
                _unitStatsFieldCache[typeName] = fld;
            }
            if (fld != null) { try { return fld.GetValue(unit); } catch { } }
            return null;
        }

        private static float InvokeStatsMethod(object stats, string methodName, int enumId,
                                                Dictionary<string, MethodInfo?> miCache)
        {
            string statsTypeName = stats.GetType().FullName ?? stats.GetType().Name;
            if (!miCache.TryGetValue(statsTypeName, out var mi))
            {
                mi = null;
                foreach (var m in stats.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (m.Name != methodName) continue;
                    var ps = m.GetParameters();
                    if (ps.Length >= 1 && (ps[0].ParameterType == typeof(int) || ps[0].ParameterType.IsEnum))
                    {
                        mi = m;
                        break;
                    }
                }
                miCache[statsTypeName] = mi;
            }
            if (mi == null) return -1f;
            try
            {
                var ps = mi.GetParameters();
                object arg = ps[0].ParameterType == typeof(int)
                    ? (object)enumId
                    : BuildEnumArgument(ps[0].ParameterType, enumId);
                object? result = mi.Invoke(stats, new object[] { arg });
                return BoxedToFloat(result, out float v) ? v : -1f;
            }
            catch { return -1f; }
        }

        // Phase 5.4-E4.2: public health read/write for the Boss encounter system. Boss bodies are normal Units whose
        // health lives in Stats(92/60), so the Boss health sync reuses the same reverse-engineered native path
        // (no ReceiveDamage side-effects) instead of duplicating the reflection.
        public static bool TryReadBossUnitHealth(object unit, out float currentHp, out float maxHp)
            => TryReadUnitHealthNative(unit, out currentHp, out maxHp);
        public static bool TryWriteBossUnitHealth(object unit, float hp)
            => TryWriteUnitHealthNative(unit, hp);

        /// <summary>
        /// Phase 5.2: reads currentHp/maxHp using the native Unit.Stats API
        /// (Unit.GetCurrentHealth(), GetNormalizedHealth(), Stats.GetAttribute(60), Stats.GetStatus(92)).
        /// Falls back to legacy field scan only if all native paths fail.
        /// </summary>
        private static bool TryReadUnitHealthNative(object unit, out float currentHp, out float maxHp)
        {
            currentHp = 0f;
            maxHp     = 0f;
            if (unit == null) return false;
            try
            {
                Type   t        = unit.GetType();
                string typeName = t.FullName ?? t.Name;
                const BindingFlags bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                // ── 1. GetCurrentHealth() ─────────────────────────────────────────
                if (!_unitGetHpMiCache.TryGetValue(typeName, out var getHpMi))
                {
                    getHpMi = null;
                    for (Type? tt = t; tt != null && getHpMi == null; tt = tt.BaseType)
                        getHpMi = tt.GetMethod("GetCurrentHealth", bf, null, Type.EmptyTypes, null);
                    _unitGetHpMiCache[typeName] = getHpMi;
                    if (!_nativeApiLoggedTypes.Contains(typeName))
                    {
                        _nativeApiLoggedTypes.Add(typeName);
                        if (getHpMi != null)
                            NetLogger.Info($"[HP-NativeAPI] GetCurrentHealth() found on {typeName}");
                        else
                            NetLogger.Warn($"[HP-NativeAPI] GetCurrentHealth() not found on {typeName} — will try Stats API");
                    }
                }

                if (getHpMi != null)
                {
                    object? hpResult = getHpMi.Invoke(unit, null);
                    if (BoxedToFloat(hpResult, out currentHp) && currentHp >= 0f)
                    {
                        // ── 2. GetNormalizedHealth() for max HP ───────────────────
                        if (!_unitGetNormHpMiCache.TryGetValue(typeName, out var getNormMi))
                        {
                            getNormMi = null;
                            for (Type? tt = t; tt != null && getNormMi == null; tt = tt.BaseType)
                                getNormMi = tt.GetMethod("GetNormalizedHealth", bf, null, Type.EmptyTypes, null);
                            _unitGetNormHpMiCache[typeName] = getNormMi;
                        }
                        if (getNormMi != null)
                        {
                            object? normResult = getNormMi.Invoke(unit, null);
                            if (BoxedToFloat(normResult, out float normalized) && normalized > 0.001f)
                            {
                                maxHp = currentHp / normalized;
                                return true;
                            }
                        }
                        // ── 3. Stats.GetAttribute(60) for max HP ─────────────────
                        object? stats = GetUnitStatsObject(unit, t, typeName);
                        if (stats != null)
                        {
                            float attrMax = InvokeStatsMethod(stats, "GetAttribute", EntityStatMaxHealth, _statsGetAttrCache);
                            if (attrMax > 0f) maxHp = attrMax;
                        }
                        return true;
                    }
                }

                // ── 4. Fallback: Stats.GetStatus(92) / GetAttribute(60) ──────────
                {
                    object? stats = GetUnitStatsObject(unit, t, typeName);
                    if (stats != null)
                    {
                        float statusHp = InvokeStatsMethod(stats, "GetStatus", EntityStatusCurrentHealth, _statsGetStatusCache);
                        if (statusHp >= 0f)
                        {
                            currentHp = statusHp;
                            float attrMax = InvokeStatsMethod(stats, "GetAttribute", EntityStatMaxHealth, _statsGetAttrCache);
                            if (attrMax > 0f) maxHp = attrMax;
                            return true;
                        }
                    }
                }

                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// Safely converts an integer constant to an enum value even when the enum's underlying
        /// type is not System.Int32 (e.g. EntityAttributes uses UInt16).
        /// Direct Enum.ToObject(type, int) throws ArgumentException when the underlying type differs.
        /// </summary>
        private static object BuildEnumArgument(Type enumType, int intValue)
        {
            if (!enumType.IsEnum) return Convert.ChangeType(intValue, enumType, CultureInfo.InvariantCulture);
            Type underlying = Enum.GetUnderlyingType(enumType);
            object rawValue = Convert.ChangeType(intValue, underlying, CultureInfo.InvariantCulture);
            return Enum.ToObject(enumType, rawValue);
        }

        /// <summary>
        /// Phase 5.2: writes newHealth to a puppet via Stats.SetStatus(92, value, false).
        /// Does NOT call ReceiveDamage or any method that triggers damage side-effects.
        /// Reflection is used because EntityAttribute and EntityStats are in PerfectRandom.Sulfur.Core.dll
        /// which is not directly referenced as a strongly-typed assembly in this project.
        /// </summary>
        /// <summary>Public health-write wrapper (EMP-6c: the Emperor phase-2 arena has no net-run-state so the generic
        /// enemy-state snapshot mirror is inactive — the spider streams its health explicitly and writes it here, with
        /// <paramref name="raiseEvent"/> true so the attached boss health bar updates).</summary>
        public static bool TryWriteUnitHealth(object unit, float newHealth, bool raiseEvent)
            => TryWriteUnitHealthNative(unit, newHealth, raiseEvent);

        private static bool TryWriteUnitHealthNative(object unit, float newHealth, bool raiseEvent = false)
        {
            if (unit == null) return false;
            try
            {
                Type   t        = unit.GetType();
                string typeName = t.FullName ?? t.Name;

                object? stats = GetUnitStatsObject(unit, t, typeName);
                if (stats == null) { _clientHealthNoStats++; return false; }

                string statsTypeName = stats.GetType().FullName ?? stats.GetType().Name;
                if (!_statsSetStatusCache.TryGetValue(statsTypeName, out var setStatusMi))
                {
                    // Collect all SetStatus overloads with >= 2 parameters.
                    var candidates = new System.Collections.Generic.List<MethodInfo>();
                    foreach (var m in stats.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (m.Name != "SetStatus") continue;
                        var ps = m.GetParameters();
                        if (ps.Length >= 2) candidates.Add(m);
                    }

                    // Strict preference: (enum-or-int, float/Single, bool) 3-param form.
                    setStatusMi = null;
                    foreach (var m in candidates)
                    {
                        var ps = m.GetParameters();
                        if (ps.Length == 3
                            && (ps[0].ParameterType.IsEnum || ps[0].ParameterType == typeof(int))
                            && (ps[1].ParameterType == typeof(float))
                            && ps[2].ParameterType == typeof(bool))
                        {
                            setStatusMi = m;
                            break;
                        }
                    }
                    // Fall back: 2-param (enum-or-int, float).
                    if (setStatusMi == null)
                    {
                        foreach (var m in candidates)
                        {
                            var ps = m.GetParameters();
                            if (ps.Length == 2
                                && (ps[0].ParameterType.IsEnum || ps[0].ParameterType == typeof(int))
                                && ps[1].ParameterType == typeof(float))
                            {
                                setStatusMi = m;
                                break;
                            }
                        }
                    }
                    // Last resort: any candidate with >= 2 params.
                    if (setStatusMi == null && candidates.Count > 0) setStatusMi = candidates[0];

                    _statsSetStatusCache[statsTypeName] = setStatusMi;

                    if (!_nativeApiLoggedTypes.Contains("SetStatus:" + statsTypeName))
                    {
                        _nativeApiLoggedTypes.Add("SetStatus:" + statsTypeName);
                        if (setStatusMi != null)
                        {
                            var ps2    = setStatusMi.GetParameters();
                            string sig = string.Join(", ", System.Array.ConvertAll(ps2, p => p.ParameterType.Name + " " + p.Name));
                            NetLogger.Info($"[Reflect] Selected {setStatusMi.DeclaringType?.Name ?? statsTypeName}.{setStatusMi.Name}({sig}) returns {setStatusMi.ReturnType.Name}");
                            NetLogger.Info($"[HP-NativeAPI] SetStatus overload chosen: declaringType={setStatusMi.DeclaringType?.FullName} paramCount={ps2.Length}");
                        }
                        else
                        {
                            string allCands = candidates.Count == 0 ? "none"
                                : string.Join(" | ", candidates.ConvertAll(m =>
                                    $"{m.Name}({string.Join(",", System.Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name))})"));
                            NetLogger.Warn($"[HP-NativeAPI] SetStatus not found on {statsTypeName}. Candidates with >=2 params: {allCands}");
                        }
                    }
                }
                if (setStatusMi == null) { _clientHealthSetStatusMissing++; return false; }

                var pms     = setStatusMi.GetParameters();
                Type arg0Type = pms[0].ParameterType;

                // Build the enum / plain-int argument safely.
                // EntityAttributes has underlying type UInt16 — direct Enum.ToObject(type, int) throws.
                object enumArg;
                try
                {
                    enumArg = arg0Type.IsEnum || arg0Type == typeof(int)
                        ? BuildEnumArgument(arg0Type, EntityStatusCurrentHealth)
                        : Convert.ChangeType(EntityStatusCurrentHealth, arg0Type, CultureInfo.InvariantCulture);
                }
                catch (Exception ex)
                {
                    _clientHealthEnumArgFailed++;
                    NetLogger.Warn($"[HealthWrite] FAIL enumArg build arg0={arg0Type.FullName} underlying={( arg0Type.IsEnum ? Enum.GetUnderlyingType(arg0Type).Name : "n/a")} value={EntityStatusCurrentHealth} ex={ex.GetType().Name}: {ex.Message}");
                    return false;
                }

                // Log arg0 resolution once per type.
                if (!_nativeApiLoggedTypes.Contains("SetStatus:arg0:" + statsTypeName))
                {
                    _nativeApiLoggedTypes.Add("SetStatus:arg0:" + statsTypeName);
                    string underlyingName = arg0Type.IsEnum ? Enum.GetUnderlyingType(arg0Type).Name : arg0Type.Name;
                    NetLogger.Info($"[HP-NativeAPI] SetStatus arg0: type={arg0Type.FullName} underlying={underlyingName} builtValue={enumArg} (Status_CurrentHealth={EntityStatusCurrentHealth})");
                }

                object[] args = pms.Length >= 3
                    ? new object[] { enumArg, newHealth, raiseEvent }
                    : new object[] { enumArg, newHealth };
                try
                {
                    setStatusMi.Invoke(stats, args);
                    return true;
                }
                catch (Exception ex)
                {
                    _clientHealthSetStatusFailed++;
                    var inner = ex is System.Reflection.TargetInvocationException ? ex.InnerException : null;
                    string paramTypes = string.Join(", ", System.Array.ConvertAll(pms, p => p.ParameterType.Name));
                    NetLogger.Warn($"[HealthWrite] FAIL SetStatus invoke {setStatusMi.DeclaringType?.Name}.{setStatusMi.Name}({paramTypes}) args=({enumArg},{newHealth}) ex={ex.GetType().Name}: {(inner ?? ex).Message}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                NetLogger.Warn($"[HealthWrite] outer exception: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        // ----------------------------------------------------------------
        // Legacy field-scan health read (kept as documented fallback; no longer on hot path).
        // ----------------------------------------------------------------

        private static bool TryReadNpcHealth(object npc, out float currentHp, out float maxHp)
        {
            currentHp = 0f;
            maxHp     = 0f;
            if (npc == null) return false;
            try
            {
                Type t = npc.GetType();
                string typeName = t.FullName ?? t.Name;
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                // Phase 1: fast-path — try known exact names.
                foreach (string name in NpcHealthFieldNamesExact)
                {
                    if (TryGetNumericMember(npc, name, flags, out float hp) && hp >= 0f)
                    {
                        currentHp = hp;
                        DiscoveredCurrentHpMember[typeName] = name;
                        goto foundCurrent;
                    }
                }

                // Phase 2: check cache from a previous discovery scan.
                if (DiscoveredCurrentHpMember.TryGetValue(typeName, out string? cached))
                {
                    if (cached == null) return false; // previously failed
                    if (TryGetNumericMember(npc, cached, flags, out float hpC) && hpC >= 0f)
                        { currentHp = hpC; goto foundCurrent; }
                }

                // Phase 3: enumerate all float/int fields+properties in the type hierarchy.
                string? discovered = DiscoverHealthMember(npc, t, flags, false, out float hpD);
                DiscoveredCurrentHpMember[typeName] = discovered; // cache result (null = not found)
                if (discovered == null) return false;
                currentHp = hpD;

                foundCurrent:
                // Try max HP — fast path first.
                foreach (string name in NpcMaxHpFieldNamesExact)
                {
                    if (TryGetNumericMember(npc, name, flags, out float mhp) && mhp > 0f)
                    {
                        maxHp = mhp;
                        DiscoveredMaxHpMember[typeName] = name;
                        return true;
                    }
                }
                // Check max cache.
                if (DiscoveredMaxHpMember.TryGetValue(typeName, out string? maxCached) && maxCached != null)
                {
                    if (TryGetNumericMember(npc, maxCached, flags, out float mhpC) && mhpC > 0f)
                        { maxHp = mhpC; return true; }
                }
                // Discover max.
                string? discoveredMax = DiscoverHealthMember(npc, t, flags, true, out float mhpD);
                DiscoveredMaxHpMember[typeName] = discoveredMax;
                if (discoveredMax != null && mhpD > 0f) maxHp = mhpD;
                return true;
            }
            catch { return false; }
        }

        private static bool TryGetNumericMember(object obj, string name, BindingFlags flags, out float value)
        {
            value = 0f;
            try
            {
                Type t = obj.GetType();
                var field = t.GetField(name, flags);
                if (field != null)
                {
                    object? v = field.GetValue(obj);
                    return BoxedToFloat(v, out value);
                }
                var prop = t.GetProperty(name, flags);
                if (prop != null && prop.CanRead && prop.GetIndexParameters().Length == 0)
                {
                    object? v = prop.GetValue(obj, null);
                    return BoxedToFloat(v, out value);
                }
            }
            catch { }
            return false;
        }

        private static bool BoxedToFloat(object? v, out float value)
        {
            value = 0f;
            if (v is float f)  { value = f; return true; }
            if (v is int   i)  { value = i; return true; }
            if (v is double d) { value = (float)d; return true; }
            if (v is uint  u)  { value = u; return true; }
            if (v is short s)  { value = s; return true; }
            return false;
        }

        private static string? DiscoverHealthMember(object npc, Type t, BindingFlags flags, bool wantMax, out float value)
        {
            value = 0f;
            var allFound = new System.Collections.Generic.List<(string name, float val)>();

            for (Type? scan = t; scan != null && scan != typeof(object); scan = scan.BaseType)
            {
                foreach (var field in scan.GetFields(flags | BindingFlags.DeclaredOnly))
                {
                    if (field.FieldType != typeof(float) && field.FieldType != typeof(int)
                        && field.FieldType != typeof(double) && field.FieldType != typeof(uint)) continue;
                    string lower = field.Name.ToLowerInvariant();
                    if (!ContainsHealthKeyword(lower)) continue;
                    bool hasMax = lower.StartsWith("max") || lower.Contains("max");
                    if (wantMax != hasMax) continue;
                    try
                    {
                        if (BoxedToFloat(field.GetValue(npc), out float fv) && fv >= 0f)
                            allFound.Add((field.Name, fv));
                    }
                    catch { }
                }
                foreach (var prop in scan.GetProperties(flags | BindingFlags.DeclaredOnly))
                {
                    if (prop.PropertyType != typeof(float) && prop.PropertyType != typeof(int)
                        && prop.PropertyType != typeof(double)) continue;
                    if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                    string lower = prop.Name.ToLowerInvariant();
                    if (!ContainsHealthKeyword(lower)) continue;
                    bool hasMax = lower.StartsWith("max") || lower.Contains("max");
                    if (wantMax != hasMax) continue;
                    try
                    {
                        if (BoxedToFloat(prop.GetValue(npc, null), out float fv) && fv >= 0f)
                            allFound.Add((prop.Name, fv));
                    }
                    catch { }
                }
            }

            if (allFound.Count > 0)
            {
                value = allFound[0].val;
                if (Plugin.Cfg.EnableDebugLog.Value && !wantMax)
                    NetLogger.Debug($"[EnemyHealth-Legacy] Discovered HP member '{allFound[0].name}'={value:F1} on {t.Name} (all candidates: {string.Join(", ", allFound.Select(x => $"{x.name}={x.val:F1}"))})");
                return allFound[0].name;
            }

            if (Plugin.Cfg.EnableDebugLog.Value && !wantMax)
            {
                var allNumeric = new System.Collections.Generic.List<string>();
                for (Type? scan = t; scan != null && scan != typeof(object); scan = scan.BaseType)
                {
                    foreach (var f in scan.GetFields(flags | BindingFlags.DeclaredOnly))
                        if (f.FieldType == typeof(float) || f.FieldType == typeof(int) || f.FieldType == typeof(double))
                            allNumeric.Add($"{scan.Name}.{f.Name}");
                    foreach (var p in scan.GetProperties(flags | BindingFlags.DeclaredOnly))
                        if (p.PropertyType == typeof(float) || p.PropertyType == typeof(int) || p.PropertyType == typeof(double))
                            allNumeric.Add($"{scan.Name}.{p.Name}(prop)");
                }
                var sample = allNumeric.Take(30).ToList();
                NetLogger.Debug($"[EnemyHealth-Legacy] No health field found on {t.Name}. Numeric members ({allNumeric.Count} total, showing {sample.Count}): {string.Join(", ", sample)}");
            }
            return null;
        }

        private static bool ContainsHealthKeyword(string lowerName)
        {
            foreach (string kw in HealthKeywords)
                if (lowerName.Contains(kw)) return true;
            return false;
        }

        /// <summary>
        /// Client receives HostEnemyDamageEvent: update puppet health cache, apply isDead if set.
        /// </summary>
        public static void ProcessHostEnemyDamageEvent(NetHostEnemyDamageEvent evt)
        {
            if (evt == null) return;
            try
            {
                _clientEnemyDamageEventsReceived++;

                if (!NetRunStateBridge.TryGetLocalRunState(out var localState) || !evt.MatchesScene(localState)) return;

                if (!ClientHostToLocalKeyByHostSpawnIndex.TryGetValue(evt.HostSpawnIndex, out var localKey))
                {
                    _clientEnemyHealthApplySkippedNoBinding++;
                    if (Plugin.Cfg.LogHostEnemyDamageEvents.Value)
                        NetLogger.Info($"[EnemyDamage] Client recv hostIdx={evt.HostSpawnIndex} unit={evt.UnitIdentifier} dmg={evt.DamageAmount:F1} — no roster binding");
                    return;
                }

                if (Plugin.Cfg.LogHostEnemyDamageEvents.Value)
                {
                    string healthStr = evt.HasRemainingHealth
                        ? $" remaining={evt.RemainingHealth:F1}{(evt.HasMaxHealth ? $"/{evt.MaxHealth:F1}" : "")} isDead={evt.IsDead}"
                        : " (no health data)";
                    NetLogger.Info($"[EnemyDamage] Client recv hostIdx={evt.HostSpawnIndex} unit={evt.UnitIdentifier} dmg={evt.DamageAmount:F1}{healthStr}");
                }

                // Cache host-authoritative health.
                if (evt.HasRemainingHealth)
                    ClientPuppetHealthBySpawnIndex[evt.HostSpawnIndex] = evt.RemainingHealth;
                if (evt.HasMaxHealth)
                    ClientPuppetMaxHealthBySpawnIndex[evt.HostSpawnIndex] = evt.MaxHealth;

                // Phase 5.3-D P0-2: a fatal hit only marks PendingDead — the death VISUAL is owned by
                // the HostDeathEvent / Npc.Die() path, which sets Animator "Dead".
                bool damageFatal = evt.IsDead || (evt.HasRemainingHealth && evt.RemainingHealth <= 0f);
                if (damageFatal)
                    MarkClientPendingDead(evt.HostSpawnIndex, "DamageEvent isDead");

                // Phase 5.3-D P0-1: visual-only white hit flash (native DoWhiteFlash, no ReceiveDamage).
                // Skipped automatically for pending/terminal dead and duplicate seq inside the helper.
                if (!damageFatal && evt.DamageAmount > 0f)
                    TryPlayDamageVisualReaction(evt.HostSpawnIndex, evt.Sequence, evt.UnitIdentifier, "HostEnemyDamageEvent");
                else if (damageFatal)
                    _terminalDeadBlockedHitReaction++;

                if (!EntitiesByLocalId.TryGetValue(localKey, out var snapshot) || snapshot == null) return;

                // P1: write corrected health to puppet via native Stats.SetStatus(92).
                // Terminal-dead corpses keep their final HP; do not re-write.
                if (Plugin.Cfg.ApplyReceivedHostEnemyHealthState.Value && evt.HasRemainingHealth
                    && !IsClientTerminalDead(evt.HostSpawnIndex)
                    && snapshot.TryGetRuntimeObject(out var runtimeObj) && runtimeObj != null)
                {
                    TryWriteUnitHealthNative(runtimeObj, evt.RemainingHealth);
                }
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[EnemyDamage] ProcessHostEnemyDamageEvent failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Client receives HostEnemyHealthState: align puppet health cache with host values.
        /// </summary>
        public static void ProcessHostEnemyHealthState(NetHostEnemyHealthState state)
        {
            if (state == null) return;
            try
            {
                _clientEnemyHealthStatesReceived++;

                if (!NetRunStateBridge.TryGetLocalRunState(out var localState) || !state.MatchesScene(localState)) return;

                bool logHealth = Plugin.Cfg.LogHostEnemyHealthState.Value;
                string dbgPfx = logHealth
                    ? NetDbg.Ctx("HealthState", seq: state.Sequence, hostIdx: state.HostSpawnIndex,
                                  rev: _clientRosterRevision, sendAt: state.SentAt)
                    : "";

                // ── Binding lookup with rich diagnostics ──────────────────────────────
                if (!ClientHostToLocalKeyByHostSpawnIndex.TryGetValue(state.HostSpawnIndex, out var localKey))
                {
                    _clientEnemyHealthApplySkippedNoBinding++;

                    // Cache the latest state for this hostIdx so it can be applied when binding arrives.
                    _pendingHealthByHostIdx[state.HostSpawnIndex] = new PendingHealthEntry
                        { State = state, ReceivedAt = Time.realtimeSinceStartup };
                    _clientHealthStatesPendingQueued++;

                    if (logHealth)
                    {
                        // Describe why binding is absent.
                        string bindingDiag;
                        float now = Time.realtimeSinceStartup;
                        if (_bindingTombstones.TryGetValue(state.HostSpawnIndex, out var ts)
                            && now - ts.ReleasedAt <= TombstoneMaxAge)
                            bindingDiag = $"tombstone(released {now - ts.ReleasedAt:F1}s ago reason={ts.ReleaseReason})";
                        else if (ClientHostToLocalKeyByHostSpawnIndex.Count > 0)
                            bindingDiag = $"neverBound(roster has {ClientHostToLocalKeyByHostSpawnIndex.Count} bindings)";
                        else
                            bindingDiag = "noRosterYet";

                        NetLogger.Info($"{dbgPfx} recv hp={state.CurrentHealth:F1}/{state.MaxHealth:F1} unit={state.UnitIdentifier} " +
                            $"binding=MISSING({bindingDiag}) → queued pending");
                    }
                    return;
                }

                // ── Found binding → cache + apply ────────────────────────────────────
                if (state.HasCurrentHealth)
                    ClientPuppetHealthBySpawnIndex[state.HostSpawnIndex] = state.CurrentHealth;
                if (state.HasMaxHealth)
                    ClientPuppetMaxHealthBySpawnIndex[state.HostSpawnIndex] = state.MaxHealth;

                // Phase 5.3-B: confirm a recent client hit request via the resulting HealthState.
                if (_clientPendingHitByHostIdx.TryGetValue(state.HostSpawnIndex, out var hitSentAt))
                {
                    _clientPendingHitByHostIdx.Remove(state.HostSpawnIndex);
                    if (Time.realtimeSinceStartup - hitSentAt <= ClientHitConfirmWindowSeconds)
                        _clientLocalHitConfirmed++;
                }

                // Phase 5.3-D P0-2: hp<=0 / isDead only ever produces PendingDead here — NEVER
                // TerminalDead. TerminalDead is reserved for the actual death-visual path so the
                // HostDeathEvent / Npc.Die() (which sets Animator "Dead") is allowed to run first.
                bool hostReportsDead = state.IsDead || (state.HasCurrentHealth && state.CurrentHealth <= 0f);
                if (hostReportsDead)
                    MarkClientPendingDead(state.HostSpawnIndex, state.IsDead ? "HealthState isDead" : "HealthState hp<=0");

                // Already-terminal corpses: keep caching health, but never write it back (avoids
                // re-triggering native hit reactions) and never run apply-side animation.
                if (IsClientTerminalDead(state.HostSpawnIndex))
                {
                    _terminalDeadHealthUpdatesIgnored++;
                    return;
                }

                if (!Plugin.Cfg.ApplyReceivedHostEnemyHealthState.Value)
                {
                    _clientHealthApplyDisabled++;
                    // Always log so summary is informative even without LogHostEnemyHealthState.
                    if (logHealth || Plugin.Cfg.EnableDebugLog.Value)
                        NetLogger.Debug($"{dbgPfx} cached hp={state.CurrentHealth:F1} (ApplyReceivedHostEnemyHealthState=false)");
                    return;
                }
                if (!state.HasCurrentHealth)
                {
                    _clientHealthNoCurrentHp++;
                    if (logHealth || Plugin.Cfg.EnableDebugLog.Value)
                        NetLogger.Debug($"{dbgPfx} no HasCurrentHealth unit={state.UnitIdentifier}");
                    return;
                }

                if (!EntitiesByLocalId.TryGetValue(localKey, out var snapshot) || snapshot == null)
                {
                    _clientHealthNoEntity++;
                    NetLogger.Info($"{dbgPfx} FAIL noEntity localKey={localKey} hp={state.CurrentHealth:F1} unit={state.UnitIdentifier}");
                    return;
                }
                if (!snapshot.TryGetRuntimeObject(out var runtimeObj) || runtimeObj == null)
                {
                    _clientHealthNoRuntimeObj++;
                    NetLogger.Info($"{dbgPfx} FAIL noRuntimeObj localKey={localKey} hp={state.CurrentHealth:F1} unit={state.UnitIdentifier}");
                    return;
                }
                // Unity engine object may be destroyed even when the C# reference is non-null.
                if (runtimeObj is UnityEngine.Object unityRuntimeObj && unityRuntimeObj == null)
                {
                    _clientHealthUnityDestroyed++;
                    NetLogger.Info($"{dbgPfx} FAIL runtimeObj unity-destroyed localKey={localKey} unit={state.UnitIdentifier}");
                    return;
                }

                // Snapshot health before write for comparison.
                TryReadUnitHealthNative(runtimeObj, out float hpBefore, out _);

                if (TryWriteUnitHealthNative(runtimeObj, state.CurrentHealth))
                {
                    _clientEnemyHealthStatesApplied++;
                    TryReadUnitHealthNative(runtimeObj, out float hpAfter, out _);
                    bool readBackUnchanged = Math.Abs(state.CurrentHealth - hpBefore) > 0.5f
                                         && Math.Abs(hpAfter - hpBefore) < 0.1f;
                    if (readBackUnchanged) _clientHealthWriteReadBackUnchanged++;
                    NetLogger.Info($"{dbgPfx} OK applied hp={state.CurrentHealth:F1} before={hpBefore:F1} after={hpAfter:F1}" +
                                   (readBackUnchanged ? " WARNING:readBackUnchanged" : "") +
                                   $" unit={state.UnitIdentifier}");
                }
                else
                {
                    _clientHealthWriteFailed++;
                    NetLogger.Info($"{dbgPfx} FAIL write noStats={_clientHealthNoStats} noSetStatus={_clientHealthSetStatusMissing} invokeFail={_clientHealthSetStatusFailed} before={hpBefore:F1} unit={state.UnitIdentifier}");
                }
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[EnemyHealth] ProcessHostEnemyHealthState failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Called from the roster-binding path after a new binding is established.
        /// Checks if there's a recently received HealthState waiting for this hostIdx and applies it.
        /// </summary>
        public static void TryApplyPendingHealthState(int hostSpawnIndex, string localKey)
        {
            if (!_pendingHealthByHostIdx.TryGetValue(hostSpawnIndex, out var entry)) return;
            _pendingHealthByHostIdx.Remove(hostSpawnIndex);

            float now = Time.realtimeSinceStartup;
            float age = now - entry.ReceivedAt;
            if (age > PendingHealthMaxAge)
            {
                _clientHealthStatesPendingExpired++;
                if (Plugin.Cfg.LogHostEnemyHealthState.Value)
                    NetLogger.Info($"[EnemyHealth] Pending HealthState hostIdx={hostSpawnIndex} expired ageMs={(age * 1000f):F0}ms");
                return;
            }

            if (!Plugin.Cfg.ApplyReceivedHostEnemyHealthState.Value) return;
            var state = entry.State;
            if (!state.HasCurrentHealth) return;

            if (!EntitiesByLocalId.TryGetValue(localKey, out var snapshot) || snapshot == null) return;
            if (!snapshot.TryGetRuntimeObject(out var runtimeObj) || runtimeObj == null) return;

            // Verify UnitIdentifier matches to prevent stale pending being applied to wrong puppet.
            if (!string.IsNullOrWhiteSpace(state.UnitIdentifier)
                && !string.IsNullOrWhiteSpace(snapshot.EntityId.UnitIdentifier)
                && !string.Equals(state.UnitIdentifier, snapshot.EntityId.UnitIdentifier, StringComparison.Ordinal))
            {
                if (Plugin.Cfg.LogHostEnemyHealthState.Value)
                    NetLogger.Info($"[EnemyHealth] Pending HealthState hostIdx={hostSpawnIndex} UnitId mismatch pending={state.UnitIdentifier} local={snapshot.EntityId.UnitIdentifier} — discarded");
                return;
            }

            TryReadUnitHealthNative(runtimeObj, out float pendingHpBefore, out _);
            if (TryWriteUnitHealthNative(runtimeObj, state.CurrentHealth))
            {
                _clientEnemyHealthStatesApplied++;
                _clientHealthStatesPendingApplied++;
                TryReadUnitHealthNative(runtimeObj, out float pendingHpAfter, out _);
                bool readBackUnchanged = Math.Abs(state.CurrentHealth - pendingHpBefore) > 0.5f
                                     && Math.Abs(pendingHpAfter - pendingHpBefore) < 0.1f;
                if (readBackUnchanged) _clientHealthWriteReadBackUnchanged++;
                if (Plugin.Cfg.LogHostEnemyHealthState.Value)
                    NetLogger.Info($"[EnemyHealth] Pending HealthState applied after binding: hostIdx={hostSpawnIndex} hp={state.CurrentHealth:F1} before={pendingHpBefore:F1} after={pendingHpAfter:F1} ageMs={(age * 1000f):F0}ms" +
                                   (readBackUnchanged ? " WARNING:readBackUnchanged" : ""));
            }
        }

        private static void ExpireOldPendingHealthStates()
        {
            if (_pendingHealthByHostIdx.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            var expired = new System.Collections.Generic.List<int>();
            foreach (var kv in _pendingHealthByHostIdx)
                if (now - kv.Value.ReceivedAt > PendingHealthMaxAge)
                    expired.Add(kv.Key);
            foreach (var k in expired)
            {
                _pendingHealthByHostIdx.Remove(k);
                _clientHealthStatesPendingExpired++;
            }
        }

        private static bool TryWriteNpcHealth(object runtimeObj, float newHealth)
        {
            // Only write to writable fields — never call ReceiveDamage or equivalent.
            try
            {
                Type t = runtimeObj.GetType();
                const BindingFlags wflags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                string typeName = t.FullName ?? t.Name;

                // Fast-path: use the name already discovered by TryReadNpcHealth.
                if (DiscoveredCurrentHpMember.TryGetValue(typeName, out string? cached) && cached != null)
                {
                    if (TrySetNumericMember(runtimeObj, cached, wflags, newHealth))
                        return true;
                }

                // Fallback: iterate the known-exact list.
                foreach (string name in NpcHealthFieldNamesExact)
                {
                    if (TrySetNumericMember(runtimeObj, name, wflags, newHealth))
                    {
                        DiscoveredCurrentHpMember[typeName] = name; // prime cache for next call
                        return true;
                    }
                }
                return false;
            }
            catch { return false; }
        }

        private static bool TrySetNumericMember(object obj, string name, BindingFlags flags, float value)
        {
            Type t = obj.GetType();
            var field = t.GetField(name, flags);
            if (field != null && (field.FieldType == typeof(float) || field.FieldType == typeof(int)))
            {
                field.SetValue(obj, field.FieldType == typeof(int) ? (object)(int)value : (object)value);
                return true;
            }
            var prop = t.GetProperty(name, flags);
            if (prop != null && prop.CanWrite && (prop.PropertyType == typeof(float) || prop.PropertyType == typeof(int)))
            {
                prop.SetValue(obj, prop.PropertyType == typeof(int) ? (object)(int)value : (object)value, null);
                return true;
            }
            return false;
        }

        private static bool TryLateBindForDeathEvent(NetGameplayDeathEvent death, out NetGameplayEntitySnapshot? snapshot)
        {
            snapshot = null;
            if (!Plugin.Cfg.AllowDeathLateRebind.Value) return false;
            try
            {
                // Collect unbound alive NPC candidates (not already in ClientLocalKeyToHostSpawnIndex).
                var candidates = new System.Collections.Generic.List<NetGameplayEntitySnapshot>();
                foreach (var s in EntitiesByLocalId.Values)
                {
                    if (s.IsDead) continue;
                    if (!string.Equals(s.Category, "Npc", StringComparison.OrdinalIgnoreCase)) continue;
                    if (IsNonCombatForSync(s)) continue;
                    string sKey = GetSnapshotTargetKey(s);
                    if (ClientLocalKeyToHostSpawnIndex.ContainsKey(sKey)) continue; // already bound
                    candidates.Add(s);
                }

                bool logLateBind = Plugin.Cfg.LogReceivedEnemyDeathEvents.Value;
                if (candidates.Count == 0)
                {
                    if (logLateBind)
                        NetLogger.Info($"[LateBind] No unbound alive NPC candidates for hostIdx={death.SpawnIndex} unit={death.UnitIdentifier}");
                    return false;
                }

                // ── Strict UnitIdentifier filter ────────────────────────────────────
                // If the host death event carries a UnitIdentifier, candidates that do NOT
                // match it are HARD-REJECTED — no fallback to all candidates.
                // This prevents cross-type kills (Goblin death late-bound to Ghost).
                bool deathHasUnit = !string.IsNullOrWhiteSpace(death.UnitIdentifier);
                var unitFiltered = new System.Collections.Generic.List<NetGameplayEntitySnapshot>();
                foreach (var c in candidates)
                {
                    bool cHasUnit = !string.IsNullOrWhiteSpace(c.EntityId.UnitIdentifier);
                    if (deathHasUnit && cHasUnit)
                    {
                        // Both sides have a UnitIdentifier — must match exactly.
                        if (!string.Equals(c.EntityId.UnitIdentifier, death.UnitIdentifier, StringComparison.Ordinal))
                            continue;
                    }
                    else if (deathHasUnit && !cHasUnit)
                    {
                        // Host has UnitId but local candidate doesn't — uncertain, skip.
                        continue;
                    }
                    unitFiltered.Add(c);
                }

                if (logLateBind)
                {
                    var rejectedUnits = candidates
                        .Where(c => !unitFiltered.Contains(c))
                        .Select(c => c.EntityId.UnitIdentifier ?? "?")
                        .Distinct().Take(5);
                    NetLogger.Info($"[LateBind] hostIdx={death.SpawnIndex} unit={death.UnitIdentifier} " +
                        $"unboundTotal={candidates.Count} strictUnitMatch={unitFiltered.Count} " +
                        $"rejected=[{string.Join(",", rejectedUnits)}]");
                }

                // If UnitIdentifier was present but nothing matched → hard reject.
                if (deathHasUnit && unitFiltered.Count == 0)
                {
                    if (logLateBind)
                        NetLogger.Info($"[LateBind] Hard-reject: host unitId={death.UnitIdentifier} has no matching local candidate");
                    return false;
                }

                var working = unitFiltered.Count > 0 ? unitFiltered : candidates;

                if (working.Count == 1)
                {
                    snapshot = working[0];
                    return true;
                }

                // Multiple candidates — position proximity as final tiebreaker.
                if (death.HasPosition && working.Count > 1)
                {
                    NetGameplayEntitySnapshot? best = null;
                    float bestDist = float.MaxValue;
                    foreach (var c in working)
                    {
                        if (!c.HasPosition) continue;
                        float d = Vector3.Distance(c.Position, death.Position);
                        if (d < bestDist) { bestDist = d; best = c; }
                    }
                    if (logLateBind)
                        NetLogger.Info($"[LateBind] Position tiebreak: best dist={bestDist:F1}m (threshold=10m) candidates={working.Count}");
                    if (best != null && bestDist < 10f) // tightened from 15m to 10m
                    {
                        snapshot = best;
                        return true;
                    }
                }

                if (logLateBind)
                    NetLogger.Info($"[LateBind] Failed: unitMatch={unitFiltered.Count} hasDeathPos={death.HasPosition}");
                return false;
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[LateBind] Exception: {ex.Message}");
                return false;
            }
        }

        private static void TrySnapEntityToPosition(object runtimeObj, Vector3 targetPosition)
        {
            try
            {
                if (!TryGetTransform(runtimeObj, out var transform) || transform == null) return;
                transform.position = targetPosition;
            }
            catch { }
        }

        // ================================================================
        // Phase 5.0: Interest management helper used by CollectHostEnemyStateSnapshots
        // ================================================================

        // ---- 5.7-B8: unified per-entity combat-event coalescing/throttling ----
        // (3) attack-phase animation throttle + (2) enemy-damage/health throttle. Both DROP intermediate events per
        // entity (cosmetic; health converges via the periodic enemy-state snapshot). (1) enemy→client damage is in
        // NetPlayerLifeManager (ACCUMULATED, not dropped — real player damage must be preserved).
        private static readonly Dictionary<int, float> _attackPhaseLastSentBySpawnIdx = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> _enemyDmgEventLastSentBySpawnIdx = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> _enemyHealthLastSentBySpawnIdx = new Dictionary<int, float>();
        private static int _attackPhaseThrottled, _enemyDamageEventThrottled;

        private static bool ThrottlePerEntity(Dictionary<int, float> lastSentByIdx, int spawnIdx, float minInterval, float now)
        {
            if (minInterval <= 0f) return false; // throttle off → always send
            lastSentByIdx.TryGetValue(spawnIdx, out float lastAt);
            if (now - lastAt < minInterval) return true; // within window → drop
            lastSentByIdx[spawnIdx] = now;
            return false;
        }

        private static bool ShouldSendByInterestManagement(NetGameplayEntitySnapshot snapshot, bool hasActiveCombatAction, float now)
        {
            // Phase 5.7-RB4: enemy positions are gameplay-critical. While a client is connected (we only collect host
            // snapshots when sending to clients), do not drop them via the distance heuristic — it starves enemies a
            // far-roaming client is fighting (frozen puppet; corpse only snaps on death). Delta compression upstream
            // already limits bandwidth. This is the structural fix; the distance throttle / remote-interest feed remains
            // available behind the flags but is off by default because its feed cannot be trusted (reports host position).
            if (Plugin.Cfg.SendAllEnemySnapshotsToClients.Value) return true;

            if (!Plugin.Cfg.EnableEnemyInterestManagement.Value) return true;
            if (!_hostPlayerPositionHintValid) return true; // no hint: always send

            if (!snapshot.HasPosition || !IsFinite(snapshot.Position)) return true;

            // Phase 5.5-P1: "near" = near ANY online player, not just the Host. An enemy a client is fighting (far from
            // the Host player) must still get full-rate snapshots, otherwise the client's puppet of it freezes/stutters.
            float distHost = Vector3.Distance(_hostPlayerPositionHint, snapshot.Position);
            float dist = distHost;
            float distRemoteMin = float.PositiveInfinity;
            int remoteCount = 0;
            if (Plugin.Cfg.IncludeRemotePlayersInInterest.Value)
            {
                remoteCount = _remoteInterestPositions.Count;
                for (int i = 0; i < _remoteInterestPositions.Count; i++)
                {
                    float d = Vector3.Distance(_remoteInterestPositions[i], snapshot.Position);
                    if (d < distRemoteMin) distRemoteMin = d;
                    if (d < dist) dist = d;
                }
            }
            float nearDist = Plugin.Cfg.EnemyNearCombatDistance.Value;
            float farDist  = Plugin.Cfg.EnemyFarDistance.Value;

            // Near or attacking/engaged: always full rate.
            if (dist <= nearDist || hasActiveCombatAction) return true;

            // Phase 5.7-RB2: an enemy a client hit recently is one a client is actively fighting (possibly from range,
            // far from the Host). Keep it at full rate through the engagement window even if its target identity didn't
            // resolve this tick — its client puppet must not freeze mid-fight.
            if (Plugin.Cfg.FullRateForEngagedEnemies.Value
                && _hostHitRequestLastAtByHostIdx.TryGetValue(snapshot.SpawnIndex, out float lastClientHitAt)
                && now - lastClientHitAt <= Plugin.Cfg.ClientEngagedEnemyFullRateSeconds.Value)
            {
                _interestEngagedExempt++;
                return true;
            }

            // Mid range: full rate.
            if (dist <= farDist) return true;

            // Phase 5.7-RB3: we only reach here as a Host actively sending snapshots to a connected client. If we have NO
            // remote-player position this tick, we cannot prove this enemy is far from the client — so do NOT throttle it
            // (the interest feed being empty must never freeze the client's distant fights). Only throttle when we have a
            // remote position and the enemy is genuinely far from every known player.
            if (Plugin.Cfg.ThrottleOnlyWithKnownRemotePositions.Value && remoteCount == 0)
            {
                MaybeLogInterestDiag(snapshot, distHost, distRemoteMin, remoteCount, hasActiveCombatAction, "send-no-remote-pos");
                return true;
            }

            MaybeLogInterestDiag(snapshot, distHost, distRemoteMin, remoteCount, hasActiveCombatAction, "far");

            // Beyond far distance: throttle by EnemyFarSnapshotHz.
            float farHz = Plugin.Cfg.EnemyFarSnapshotHz.Value;
            if (farHz <= 0f) { _interestManagementFarSkipped++; return false; }

            float farInterval = 1f / farHz;
            if (!FarEnemyLastSentAtBySpawnIndex.TryGetValue(snapshot.SpawnIndex, out float lastSent)
                || now - lastSent >= farInterval)
            {
                FarEnemyLastSentAtBySpawnIndex[snapshot.SpawnIndex] = now;
                return true;
            }

            _interestManagementFarSkipped++;
            return false;
        }

        private static int _interestDiagLogged;
        private static void MaybeLogInterestDiag(NetGameplayEntitySnapshot snapshot, float distHost, float distRemoteMin,
            int remoteCount, bool engaged, string decision)
        {
            if (!Plugin.Cfg.LogEnemyInterestDiag.Value) return;
            if (_interestDiagLogged >= 120) return;     // bounded — first 120 lines per level
            _interestDiagLogged++;
            string remote = float.IsInfinity(distRemoteMin) ? "none" : distRemoteMin.ToString("F1");
            bool recentHit = _hostHitRequestLastAtByHostIdx.ContainsKey(snapshot.SpawnIndex);
            NetLogger.Info($"[InterestDiag] idx={snapshot.SpawnIndex} unit={snapshot.EntityId.UnitIdentifier} distHost={distHost:F1} distRemoteMin={remote} remoteCount={remoteCount} engaged={engaged} everHit={recentHit} decision={decision}");
        }
    }
}
