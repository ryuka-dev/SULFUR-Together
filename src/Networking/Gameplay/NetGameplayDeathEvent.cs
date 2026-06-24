using UnityEngine;
using SULFURTogether.Networking;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase 4.0.0-B host enemy-death event mirror payload.
    ///
    /// This is a network log/matching payload only. It is not a command to mutate
    /// client gameplay unless a future apply path is implemented and explicitly enabled.
    /// </summary>
    internal sealed class NetGameplayDeathEvent
    {
        public string EventId { get; set; } = "";
        public string SourcePeerId { get; set; } = "host";
        public string ChapterName { get; set; } = "<unknown>";
        public int LevelIndex { get; set; } = -1;
        public bool HasLevelSeed { get; set; }
        public int LevelSeed { get; set; }
        public int SourceRevision { get; set; }
        public int Sequence { get; set; }

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
        public int DamageCount { get; set; }
        public string Source { get; set; } = "";
        public float SentAt { get; set; }

        public string SceneKey => $"{ChapterName}:{LevelIndex}";
        public string SeedText => HasLevelSeed ? LevelSeed.ToString() : "?";
        public string PositionText => HasPosition ? $"({Position.x:F2},{Position.y:F2},{Position.z:F2})" : "(?)";

        public bool MatchesScene(NetRunState localState)
        {
            if (!localState.HasLevel) return false;
            if (!NetSceneName.SameScene(localState.ChapterName, localState.LevelIndex, ChapterName, LevelIndex)) return false;
            if (Plugin.Cfg.EnableLevelSeedAuthority.Value)
                return HasLevelSeed && localState.HasLevelSeed && LevelSeed == localState.LevelSeed;
            return true;
        }

        public string ToCompactString()
        {
            return $"event={EventId} src={SourcePeerId} seq={Sequence} idx={SpawnIndex} candidate={CandidateKey} category={Category} actor={ActorName} pos={PositionText} scene={SceneKey} seed={SeedText} damageCount={DamageCount} source={Source}";
        }
    }
}
