namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase 5.3-F: Host → Client hit visual event. Sent when the host applies a validated
    /// ClientHitRequest (or any non-fatal NPC hit it wants mirrored visually). Carries no health —
    /// it only tells the client to play the native white hit flash on the matching puppet.
    /// Health stays owned by HostEnemyHealthState; death by HostDeathEvent / terminal-dead latch.
    /// </summary>
    internal sealed class NetHostHitVisualEvent
    {
        public string ChapterName  { get; set; } = "";
        public int    LevelIndex   { get; set; } = -1;
        public bool   HasLevelSeed { get; set; }
        public int    LevelSeed    { get; set; }

        public int    HostSpawnIndex { get; set; }
        public string UnitIdentifier { get; set; } = "";
        public int    Sequence       { get; set; }
        public bool   IsFatal        { get; set; }   // host already resolved this as a kill — client skips flash
        public float  SentAt         { get; set; }

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
