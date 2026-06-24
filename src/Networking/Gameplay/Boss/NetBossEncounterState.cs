using UnityEngine;

namespace SULFURTogether.Networking.Gameplay.Boss
{
    /// <summary>
    /// Phase 5.4-E: host-authoritative Boss encounter state. This phase only carries the START signal plus
    /// extension fields (position / phase index) reserved for later BossHealth / BossPhase / BossDeath sync.
    /// </summary>
    internal sealed class NetBossEncounterState
    {
        public string EncounterKey { get; set; } = "";
        public string BossType     { get; set; } = "";
        public string GraphName    { get; set; } = "";
        public string RootName     { get; set; } = "";

        public string ChapterName  { get; set; } = "";
        public int    LevelIndex   { get; set; } = -1;
        public bool   HasSeed      { get; set; }
        public int    Seed         { get; set; }

        public bool   Started      { get; set; }
        public string StartSource  { get; set; } = "";
        public int    HostRevision { get; set; }
        public float  HostTimestamp { get; set; }

        // Reserved extension points (not yet authoritative this phase).
        public bool   HasPosition  { get; set; }
        public Vector3 Position    { get; set; }
        public bool   HasPhaseIndex { get; set; }
        public int    PhaseIndex   { get; set; }

        public string RunKey => $"{(string.IsNullOrEmpty(ChapterName) ? "<unknown>" : ChapterName)}:{LevelIndex}:{(HasSeed ? Seed.ToString() : "?")}";

        public string ToCompact()
            => $"key={EncounterKey} type={BossType} graph={(string.IsNullOrEmpty(GraphName) ? "?" : GraphName)} run={RunKey} started={Started} src={StartSource} phase={(HasPhaseIndex ? PhaseIndex.ToString() : "?")} rev={HostRevision}";
    }
}
