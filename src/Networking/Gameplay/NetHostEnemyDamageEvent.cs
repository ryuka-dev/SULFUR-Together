using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase 5.1 Host-authoritative enemy damage event.
    /// Sent by Host whenever a combat NPC receives damage, so clients can track health state
    /// and reduce death-sync drift.  Client uses this to update the puppet health cache and
    /// to apply death when isDead is set — without relying on client-local gameplay logic.
    /// </summary>
    internal sealed class NetHostEnemyDamageEvent
    {
        // Scene context — used to reject mismatched packets.
        public string ChapterName  { get; set; } = "";
        public int    LevelIndex   { get; set; } = -1;
        public bool   HasLevelSeed { get; set; }
        public int    LevelSeed    { get; set; }

        // Entity identity — HostSpawnIndex is the P0 primary key via HostWorldRoster binding.
        public int    HostSpawnIndex  { get; set; }
        public string UnitIdentifier  { get; set; } = "";

        public int Sequence { get; set; }

        // Damage payload.
        public float DamageAmount { get; set; }

        // Health state after damage — populated when the host can read the NPC health field.
        public bool  HasRemainingHealth  { get; set; }
        public float RemainingHealth     { get; set; }
        public bool  HasMaxHealth        { get; set; }
        public float MaxHealth           { get; set; }

        // True when the NPC died from this hit (or was already dead when probed after).
        public bool IsDead { get; set; }

        // Optional hit position for visual feedback.
        public bool    HasHitPosition { get; set; }
        public Vector3 HitPosition    { get; set; }

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
