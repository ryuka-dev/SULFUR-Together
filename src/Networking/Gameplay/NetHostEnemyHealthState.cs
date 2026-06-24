using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase 5.1 Host-authoritative NPC health snapshot.
    /// Sent alongside HostEnemyDamageEvent or as a periodic low-frequency correction.
    /// Client uses this to keep the puppet health cache aligned with the host's reality,
    /// so that the death event arrives into a client that already knows the target is near-dead.
    /// </summary>
    internal sealed class NetHostEnemyHealthState
    {
        // Scene context.
        public string ChapterName  { get; set; } = "";
        public int    LevelIndex   { get; set; } = -1;
        public bool   HasLevelSeed { get; set; }
        public int    LevelSeed    { get; set; }

        // Entity identity.
        public int    HostSpawnIndex  { get; set; }
        public string UnitIdentifier  { get; set; } = "";

        public int Sequence { get; set; }

        // Health values — all optional; host sends whatever it can read.
        public bool  HasCurrentHealth    { get; set; }
        public float CurrentHealth       { get; set; }
        public bool  HasMaxHealth        { get; set; }
        public float MaxHealth           { get; set; }
        public bool  HasNormalizedHealth { get; set; }
        public float NormalizedHealth    { get; set; }

        public bool IsDead { get; set; }

        // Optional position for late-bind matching.
        public bool    HasPosition { get; set; }
        public Vector3 Position    { get; set; }

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
