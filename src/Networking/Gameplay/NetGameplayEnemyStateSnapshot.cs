using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase 4.1 host enemy state snapshot.
    /// Clients can match and measure drift. In Phase 4.1.0-B, matched Client enemies
    /// may optionally mirror Host transform targets, but AI/attacks/damage are still untouched.
    /// </summary>
    internal sealed class NetGameplayEnemyStateSnapshot
    {
        public int Sequence { get; set; }
        public string SourcePeerId { get; set; } = "host";
        public string ChapterName { get; set; } = "";
        public int LevelIndex { get; set; } = -1;
        public bool HasLevelSeed { get; set; }
        public int LevelSeed { get; set; }
        public int SourceRevision { get; set; }
        public float SentAt { get; set; }

        public int SpawnIndex { get; set; }
        public string CandidateKey { get; set; } = "";
        public string LocalInstanceId { get; set; } = "";
        public int UnityInstanceId { get; set; }
        public string TypeName { get; set; } = "";
        public string UnitIdentifier { get; set; } = "";
        public string UnitGlobalId { get; set; } = "";
        public string Category { get; set; } = "Npc";
        public string ActorName { get; set; } = "<unknown>";

        public bool HasPosition { get; set; }
        public Vector3 Position { get; set; }
        public bool HasRotationY { get; set; }
        public float RotationY { get; set; }
        public bool IsDead { get; set; }

        // Phase 4.4.0-C: Host visual state for Client puppet enemies.
        // This is intentionally Animator-state-only; Client AI/BT/NavMesh stay disabled.
        public bool HasAnimatorState { get; set; }
        public int AnimatorLayer { get; set; }
        public int AnimatorFullPathHash { get; set; }
        public int AnimatorShortNameHash { get; set; }
        public float AnimatorNormalizedTime { get; set; }
        public float AnimatorSpeed { get; set; }
        public bool HasAnimatorMovingBool { get; set; }
        public bool AnimatorMovingBool { get; set; }
        public bool HasAnimatorAttackBool { get; set; }
        public bool AnimatorAttackBool { get; set; }
        public bool HasAnimatorCoweringBool { get; set; }
        public bool AnimatorCoweringBool { get; set; }

        // Phase 4.4.0-J: Host combat-action event marker. This is separate
        // from Animator bools so Clients can replay weapon-specific visual
        // methods such as TriggerWeaponManually(state) without restoring full
        // locomotion state playback.
        public bool HasHostCombatAction { get; set; }
        public int HostCombatActionKind { get; set; }
        public int HostCombatActionState { get; set; }
        public int HostCombatActionSequence { get; set; }
        public bool HasHostCombatAim { get; set; }
        public Vector3 HostCombatOriginPosition { get; set; }
        public Vector3 HostCombatAimPosition { get; set; }

        // Phase 4.4.0-N: Host AI intent mirror.
        // Clients use this as a destination/look-at intent and let local movement/Animator
        // systems simulate between corrections instead of being transform-dragged every tick.
        public bool HasAiIntent { get; set; }
        public int AiIntentSequence { get; set; }
        public int AiIntentKind { get; set; }
        public Vector3 AiIntentDestination { get; set; }
        public bool HasAiIntentLookAt { get; set; }
        public Vector3 AiIntentLookAt { get; set; }

        // Phase 4.4.0-L: generic combat animation state mirror.
        // Up to four active Animator states under the Host enemy are sent during
        // combat windows. Clients match by relative transform path hash and play
        // the exact state hash, avoiding monster-specific trigger-name guesses.
        public int HostCombatAnimatorStateCount { get; set; }
        public int[] HostCombatAnimatorPathHashes { get; } = new int[4];
        public int[] HostCombatAnimatorLayers { get; } = new int[4];
        public int[] HostCombatAnimatorFullPathHashes { get; } = new int[4];
        public float[] HostCombatAnimatorNormalizedTimes { get; } = new float[4];
        public float[] HostCombatAnimatorSpeeds { get; } = new float[4];

        // Phase 4.4.0-O: Host-authorized enemy intent execution.
        // HasEnemyIntent bridges HasHostCombatAction into an explicit typed intent so the
        // Client can create per-NPC authorization windows and allow the native Npc combat
        // pipeline to run instead of blocking every puppet combat call.
        public bool HasEnemyIntent { get; set; }
        public int EnemyIntentKind { get; set; }
        public int EnemyIntentSequence { get; set; }
        public float EnemyIntentDuration { get; set; }
        public bool EnemyIntentHasTargetPosition { get; set; }
        public Vector3 EnemyIntentTargetPosition { get; set; }
        public bool EnemyIntentHasAimPosition { get; set; }
        public Vector3 EnemyIntentAimPosition { get; set; }
        public bool EnemyIntentHasOriginPosition { get; set; }
        public Vector3 EnemyIntentOriginPosition { get; set; }
        public int EnemyIntentWeaponActionState { get; set; }

        // Phase 5.4-C G: host-authoritative target identity. Lets the Client know who the Host AI is currently
        // engaging so it can stop driving its own local target. Position is authoritative; kind is best-effort.
        // HostTargetKind: 0=None, 1=HostPlayer, 2=RemotePlayer, 3=Unknown.
        public bool HasHostTarget { get; set; }
        public byte HostTargetKind { get; set; }
        public Vector3 HostTargetPosition { get; set; }

        public string SceneKey => string.IsNullOrWhiteSpace(ChapterName) ? "<unknown>:-1" : $"{ChapterName}:{LevelIndex}";
        public string SeedText => HasLevelSeed ? LevelSeed.ToString() : "?";
        public string PositionText => HasPosition ? $"({Position.x:F2},{Position.y:F2},{Position.z:F2})" : "(?)";

        public bool MatchesScene(NetRunState localState)
        {
            if (!localState.HasLevel) return false;
            if (!string.Equals(localState.ChapterName, ChapterName, System.StringComparison.Ordinal)) return false;
            if (localState.LevelIndex != LevelIndex) return false;
            if (Plugin.Cfg.EnableLevelSeedAuthority.Value)
            {
                if (!HasLevelSeed || !localState.HasLevelSeed) return false;
                if (localState.LevelSeed != LevelSeed) return false;
            }
            return true;
        }
    }
}
