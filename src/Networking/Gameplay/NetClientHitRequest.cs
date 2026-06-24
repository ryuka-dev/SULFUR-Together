using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase 5.3-B: Client → Host gameplay damage request.
    /// Sent when the client player deals damage to a host-bound puppet NPC.
    /// Host validates, applies damage to the real NPC, then existing HealthState/DeathEvent
    /// broadcast handles the result.  Architecture: "Client reports intent. Host owns result."
    /// </summary>
    internal sealed class NetClientHitRequest
    {
        // Scene context — must match host's current scene.
        public string ChapterName  { get; set; } = "";
        public int    LevelIndex   { get; set; } = -1;
        public bool   HasLevelSeed { get; set; }
        public int    LevelSeed    { get; set; }

        // Request identity — monotonic per client, used for rate-limit and de-dup.
        public int    RequestSeq   { get; set; }
        public string ClientPeerId { get; set; } = "";

        // Target — identified by Host roster spawnIndex, guarded by UnitIdentifier type check.
        public int    TargetHostSpawnIndex  { get; set; } = -1;
        public string TargetUnitIdentifier  { get; set; } = "";

        // Damage evidence — host validates and may clamp; final result is host-authoritative.
        public float  DamageCandidate { get; set; }

        // Optional attacker position — allows host range check.
        public bool    HasAttackerPosition { get; set; }
        public Vector3 AttackerPosition    { get; set; }

        public float SentAt { get; set; }

        public string SceneKey => string.IsNullOrWhiteSpace(ChapterName)
            ? "<unknown>:-1"
            : $"{ChapterName}:{LevelIndex}";

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
