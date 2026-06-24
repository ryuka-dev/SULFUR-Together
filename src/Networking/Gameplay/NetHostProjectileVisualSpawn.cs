using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase 5.0 Host-Driven Proxy: reliable projectile visual spawn event.
    /// Host sends this when an enemy fires a projectile. Client spawns a visual-only
    /// no-damage proxy so remote players see the effect.
    /// Damage and hit detection remain host-authoritative; client projectile is purely cosmetic.
    /// </summary>
    internal sealed class NetHostProjectileVisualSpawn
    {
        // Scene context.
        public string ChapterName  { get; set; } = "";
        public int    LevelIndex   { get; set; } = -1;
        public bool   HasLevelSeed { get; set; }
        public int    LevelSeed    { get; set; }

        // Source entity — bound via HostWorldRoster.
        public int    HostSpawnIndex { get; set; }
        public string UnitIdentifier { get; set; } = "";

        // Projectile identity — de-dup on client.
        public int Sequence { get; set; }

        // Trajectory.
        public Vector3 Origin   { get; set; } // world-space spawn position
        public Vector3 Velocity { get; set; } // direction * speed (m/s)
        public float   Lifetime { get; set; } // max lifetime in seconds

        // Optional kind hint — future: can match to different visual prefab/color.
        public string ProjectileKind { get; set; } = "";

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
