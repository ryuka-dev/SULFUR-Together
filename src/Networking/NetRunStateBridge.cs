namespace SULFURTogether.Networking
{
    /// <summary>
    /// Small static bridge from reverse-probe patches into NetService.
    /// Kept mostly metadata-only; the local player object is used only to track the local Transform for visual proxy packets.
    /// </summary>
    public static class NetRunStateBridge
    {
        private static NetService? _service;

        internal static void Attach(NetService? service)
        {
            _service = service;
        }

        public static void ReportGoToLevel(string chapterName, int levelIndex, string loadingMode, string spawnIdentifier)
        {
            _service?.ReportLocalGoToLevel(chapterName, levelIndex, loadingMode, spawnIdentifier);
        }

        public static void ReportGameState(string gameState)
        {
            _service?.ReportLocalGameState(gameState);
        }

        public static void ReportClearLevel()
        {
            _service?.ReportLocalClearLevel();
        }

        public static void ReportLevelSeed(int seed, string generatorName)
        {
            _service?.ReportLocalLevelSeed(seed, generatorName);
        }

        public static void ReportLocalPlayerObject(object player)
        {
            _service?.ReportLocalPlayerObject(player);
        }

        /// <summary>
        /// Phase 5.3-M P0-D: unified entry called when a GenerationInputSnapshot is finalized. The service
        /// applies it to the local run state (and, on the Host, rebroadcasts + refreshes scene requests).
        /// </summary>
        public static void ApplyFinalizedGenerationSnapshot(NetGenerationInputSnapshot snapshot)
        {
            _service?.ApplyFinalizedGenerationSnapshot(snapshot);
        }

        public static bool TryGetLocalRunState(out NetRunState state)
        {
            state = new NetRunState();
            if (_service == null) return false;
            state = _service.GetLocalRunStateSnapshot();
            return state.Revision > 0;
        }
    }
}
