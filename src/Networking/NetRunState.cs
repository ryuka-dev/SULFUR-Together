namespace SULFURTogether.Networking
{
    /// <summary>
    /// Phase 2.3+ run/scene state metadata only.
    /// This is not scene synchronization and never loads or changes levels.
    /// </summary>
    public sealed class NetRunState
    {
        public string PeerId          { get; set; } = "";
        public string PlayerName      { get; set; } = "";
        public string ChapterName     { get; set; } = "<unknown>";
        public int    LevelIndex      { get; set; } = -1;
        public string LoadingMode     { get; set; } = "";
        public string SpawnIdentifier { get; set; } = "";
        public string GameState       { get; set; } = "<unknown>";
        public bool   HasLevelSeed    { get; set; }
        public int    LevelSeed       { get; set; }
        public string LevelGenerator  { get; set; } = "";
        public int    Revision        { get; set; }
        public float  LastUpdatedAt   { get; set; }

        public bool HasLevel => !string.IsNullOrWhiteSpace(ChapterName) && ChapterName != "<unknown>" && LevelIndex >= 0;
        public bool HasKnownGameState => !string.IsNullOrWhiteSpace(GameState) && GameState != "<unknown>";
        public bool IsLoadingLikeState => IsLoadingLike(GameState);
        public bool IsStableGameState => HasKnownGameState && !IsLoadingLikeState;

        public NetRunState Clone()
        {
            return new NetRunState
            {
                PeerId          = PeerId,
                PlayerName      = PlayerName,
                ChapterName     = ChapterName,
                LevelIndex      = LevelIndex,
                LoadingMode     = LoadingMode,
                SpawnIdentifier = SpawnIdentifier,
                GameState       = GameState,
                HasLevelSeed    = HasLevelSeed,
                LevelSeed       = LevelSeed,
                LevelGenerator  = LevelGenerator,
                Revision        = Revision,
                LastUpdatedAt   = LastUpdatedAt,
            };
        }

        public bool SameSceneAs(NetRunState other)
        {
            if (other == null) return false;
            return HasLevel
                && other.HasLevel
                && NetSceneName.SameScene(ChapterName, LevelIndex, other.ChapterName, other.LevelIndex);
        }

        public bool SameLevelSeedAs(NetRunState other)
        {
            if (other == null) return false;
            if (!HasLevelSeed || !other.HasLevelSeed) return false;
            return LevelSeed == other.LevelSeed;
        }

        public bool SameLevelInstanceAs(NetRunState other, bool requireSeed)
        {
            if (!SameSceneAs(other)) return false;
            if (!requireSeed) return true;

            // A generated SULFUR level is only the same level instance when both
            // peers know the concrete seed and that seed matches. Unknown seed is
            // treated as not-yet-proven rather than assumed compatible.
            if (!HasLevelSeed || !other.HasLevelSeed) return false;
            return LevelSeed == other.LevelSeed;
        }

        public bool HasKnownSeedMismatch(NetRunState other)
        {
            if (other == null) return false;
            return SameSceneAs(other) && HasLevelSeed && other.HasLevelSeed && LevelSeed != other.LevelSeed;
        }

        public bool SameStableStateAs(NetRunState other)
        {
            if (other == null) return false;
            return IsStableGameState
                && other.IsStableGameState
                && Clean(GameState) == Clean(other.GameState);
        }

        public string SceneKey()
        {
            return $"{Clean(ChapterName)}:{LevelIndex}";
        }

        public string CompareKey()
        {
            return $"{Clean(ChapterName)}:{LevelIndex}:{Clean(GameState)}";
        }

        public string SceneCompareKey()
        {
            return NetSceneName.SceneCompareKey(ChapterName, LevelIndex);
        }

        public string LevelInstanceKey()
        {
            string seed = HasLevelSeed ? LevelSeed.ToString() : "?";
            return $"{SceneCompareKey()}#seed={seed}";
        }

        public string ToCompactString()
        {
            string peer = string.IsNullOrWhiteSpace(PeerId) ? "?" : PeerId;
            string name = string.IsNullOrWhiteSpace(PlayerName) ? "?" : PlayerName;
            string seed = HasLevelSeed ? $",seed={LevelSeed}" : ",seed=?";
            return $"{name}(id={peer},scene={SceneKey()}{seed},state={Clean(GameState)},rev={Revision})";
        }

        private static bool IsLoadingLike(string value)
        {
            value = Clean(value);
            return value == "<unknown>"
                || value == "Uninitialized"
                || value == "Loading"
                || value == "Cinematic";
        }

        private static string Clean(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "<unknown>" : value.Trim();
        }
    }
}
