namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase EM-3: host-authoritative snapshot of the Endless run's progression + wave state (host → all clients,
    /// ReliableOrdered, low-rate). The client's <c>EndlessModeManager</c> is a slave (EM-1) — it does not drive its own
    /// waves/XP — so it applies this snapshot to its local manager fields and renders the vanilla Endless UI from them
    /// (XP bar, stage/wave/level labels), keeping both HUDs in agreement with the host.
    /// <para>Carries the same run context as other gameplay messages so a client in a different level ignores it. The
    /// <see cref="Revision"/> is monotonic per host run; the client drops stale/older revisions.</para>
    /// </summary>
    internal sealed class NetEndlessWaveState
    {
        // Run context (a receiver not in this exact Endless run ignores it).
        public string ChapterName  { get; set; } = "";
        public int    LevelIndex   { get; set; } = -1;
        public bool   HasLevelSeed { get; set; }
        public int    LevelSeed    { get; set; }

        // Monotonic per host run; the client ignores an older revision (also detects a fresh run / reset).
        public int Revision { get; set; }

        // Wave/stage machine (host-owned).
        public int  CurrentStage      { get; set; }
        public int  CurrentWave       { get; set; }
        public int  CurrentBurstIndex { get; set; }
        public int  LoopCount         { get; set; }
        public byte TransitionState   { get; set; } // TransitionState enum: 0 None,1 WaveActive,2 CardSelection,3 RewardCollection,4 ArenaTransition

        // XP / card progression (host-owned pool; default Shared mode — the client shows the shared value).
        public float CurrentXP           { get; set; }
        public float NextCardThresholdXP { get; set; }
        public int   CurrentCardLevel    { get; set; }

        // EM-7b: the host-owned card-spawn locator beam (FloatingCardManager.lootLightEffect) — a single shared pillar
        // that moves to the latest world-card spawn (loot / chest / NPC) and turns off at the run reset. Mirrored so the
        // client sees the same locator; the client's own beam is host-driven (its local SpawnLootLightEffect is suppressed).
        public bool  LootBeamActive { get; set; }
        public float LootBeamX      { get; set; }
        public float LootBeamY      { get; set; }
        public float LootBeamZ      { get; set; }

        public bool MatchesScene(NetRunState localState)
        {
            if (localState == null || !localState.HasLevel) return false;
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
