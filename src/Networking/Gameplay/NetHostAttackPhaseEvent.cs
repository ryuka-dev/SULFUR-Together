using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase 5.0 Host-Driven Proxy: reliable attack phase event.
    /// Sent by Host whenever a CombatEnemy enters a meaningful attack phase transition.
    /// Client applies this to the puppet enemy Animator directly — no native method replay.
    /// </summary>
    internal sealed class NetHostAttackPhaseEvent
    {
        // Attack phase constants (semantic layer above CombatActionKind).
        public const int PhaseNone      = 0;
        public const int PhaseWindup    = 1; // attack starting; windup animation
        public const int PhaseActive    = 2; // damage window open
        public const int PhaseRecovery  = 3; // damage window closed; returning to idle
        public const int PhaseCancelled = 4; // attack interrupted

        // Attack kind constants.
        public const int KindNone        = 0;
        public const int KindMelee       = 1;
        public const int KindRanged      = 2;
        public const int KindWeaponAction = 3;

        // Scene context — used to reject mismatched packets.
        public string ChapterName  { get; set; } = "";
        public int    LevelIndex   { get; set; } = -1;
        public bool   HasLevelSeed { get; set; }
        public int    LevelSeed    { get; set; }

        // Entity identity — HostSpawnIndex is the P0 primary key via HostWorldRoster binding.
        public int    HostSpawnIndex  { get; set; }
        public string UnitIdentifier  { get; set; } = ""; // type guard

        // Semantic attack phase.
        public int AttackPhase { get; set; }    // Phase* constant above
        public int AttackKind  { get; set; }    // Kind* constant above

        // Raw action kind from the host probe (CombatAction* constants in ProbeManager).
        // Preserved so the client can map back if needed.
        public int ActionKind  { get; set; }
        public int ActionState { get; set; }
        public int Sequence    { get; set; }    // monotonic; de-dup on client

        // Aim data — available when host could resolve target position.
        public bool    HasAimData       { get; set; }
        public Vector3 OriginPosition   { get; set; }
        public Vector3 AimPosition      { get; set; }

        // Animator state hint — set when host has an active Animator at combat time.
        // Client uses this for CrossFade-based animation instead of trigger guessing.
        public bool  HasAnimatorHint          { get; set; }
        public int   AnimatorFullPathHash     { get; set; }
        public float AnimatorNormalizedTime   { get; set; }

        public float SentAt { get; set; }

        public string SceneKey => string.IsNullOrWhiteSpace(ChapterName) ? "<unknown>:-1" : $"{ChapterName}:{LevelIndex}";

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
