namespace SULFURTogether.Networking.Gameplay.Boss
{
    /// <summary>
    /// Phase 5.4-E: a cross-end-stable identity for a Boss encounter. The Key is intentionally built from
    /// values that are deterministic across host and client given identical level generation (run key +
    /// graph + boss type + boss root object name). InstanceId is for diagnostics only — never part of Key.
    /// </summary>
    internal struct NetBossEncounterId
    {
        public string RunKey;      // chapter:level:seed
        public string ChapterName;
        public int    LevelIndex;
        public bool   HasSeed;
        public int    Seed;
        public string GraphName;
        public string BossType;    // controller/helper short type name
        public string RootName;    // boss root GameObject name (stable across ends)
        public string RootPath;    // full hierarchy path (diagnostic / disambiguation)
        public int    InstanceId;  // diagnostic only

        /// <summary>Cross-end primary key. Excludes InstanceId and full path.</summary>
        public string Key => $"{RunKey}|{BossType}|{RootName}";

        public string ToCompact()
            => $"key={Key} graph={(string.IsNullOrEmpty(GraphName) ? "?" : GraphName)} type={BossType} root={RootName} path={RootPath} inst={InstanceId}";
    }
}
