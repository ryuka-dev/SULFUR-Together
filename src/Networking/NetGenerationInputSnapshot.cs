namespace SULFURTogether.Networking
{
    /// <summary>
    /// Phase 5.3-J: a snapshot of the level generator's deterministic inputs, captured at the moment
    /// generation STARTS (StartLevelRoutineGraph prefix) — i.e. BEFORE this level mutates the used sets.
    /// This is exactly what a drifted Client needs to reproduce the Host's level.
    /// </summary>
    public sealed class NetGenerationInputSnapshot
    {
        public string GraphName       { get; set; } = "";
        public string Chapter         { get; set; } = "<unknown>";
        public int    LevelIndex      { get; set; } = -1;
        public bool   HasSeed         { get; set; }
        public int    Seed            { get; set; }
        public int    SeedCandidate   { get; set; }   // the (unreliable) seed read at StartLevelRoutineGraph prefix
        public string LoadingMode     { get; set; } = "";
        public string SpawnIdentifier { get; set; } = "";
        public int    Revision        { get; set; }
        public string RunId           { get; set; } = "";

        /// <summary>True once the seed + graph name were confirmed from the live MakerGraph context.</summary>
        public bool   Finalized       { get; set; }

        // Phase 5.4-D-1: the chapter/level came from the authoritative SwitchLevelRoutine/GoToLevel target
        // (not the stale generation pending). TargetEnvId is the WorldEnvironmentIds enum value (diagnostic).
        public bool   FromTransition  { get; set; }
        public string TransitionSource { get; set; } = "";
        public int    TargetEnvId     { get; set; } = -1;

        public NetHostUsedSets UsedSets { get; set; } = new NetHostUsedSets();

        public NetGenerationInputSnapshot Clone() => new NetGenerationInputSnapshot
        {
            GraphName = GraphName,
            Chapter = Chapter,
            LevelIndex = LevelIndex,
            HasSeed = HasSeed,
            Seed = Seed,
            SeedCandidate = SeedCandidate,
            LoadingMode = LoadingMode,
            SpawnIdentifier = SpawnIdentifier,
            Revision = Revision,
            RunId = RunId,
            Finalized = Finalized,
            FromTransition = FromTransition,
            TransitionSource = TransitionSource,
            TargetEnvId = TargetEnvId,
            UsedSets = UsedSets?.Clone() ?? new NetHostUsedSets(),
        };

        /// <summary>Match key by seed + level (the fields a HostSceneRequest can supply).</summary>
        public static string SeedLevelKey(bool hasSeed, int seed, int levelIndex)
            => $"{(hasSeed ? seed.ToString() : "?")}|L{levelIndex}";

        public string SeedLevelKey() => SeedLevelKey(HasSeed, Seed, LevelIndex);

        public string RunKey()
            => $"{Chapter}:{LevelIndex}:{(HasSeed ? Seed.ToString() : "?")}:{(string.IsNullOrEmpty(GraphName) ? "?" : GraphName)}:r{Revision}";

        public string ToCompactString()
            => $"graph={(string.IsNullOrEmpty(GraphName) ? "?" : GraphName)} seed={(HasSeed ? Seed.ToString() : "?")} chapter={Chapter} level={LevelIndex} {UsedSets.ToCompactString()}";
    }
}
