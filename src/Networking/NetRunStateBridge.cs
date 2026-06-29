namespace SULFURTogether.Networking
{
    /// <summary>
    /// Small static bridge from reverse-probe patches into NetService.
    /// Kept mostly metadata-only; the local player object is used only to track the local Transform for visual proxy packets.
    /// </summary>
    public static class NetRunStateBridge
    {
        private static NetService? _service;

        // Last local level seen, cached even while networking is OFF (service == null). The explicit Create/Join
        // flow starts networking AFTER a save is already loaded, so the GoToLevel that loaded it fired before any
        // NetService existed; without this, the freshly started service reports <unknown>:-1 to peers until the
        // next transition — and a joining client then can't auto-follow until the host's next periodic broadcast
        // (~10s later). Log186: the host's on-handshake scene request was empty for exactly this reason.
        private static bool   _haveCachedLevel;
        private static string _cachedChapter = "";
        private static int    _cachedLevelIndex = -1;
        private static string _cachedLoadingMode = "";
        private static string _cachedSpawn = "";

        internal static void Attach(NetService? service)
        {
            _service = service;
        }

        public static void ReportGoToLevel(string chapterName, int levelIndex, string loadingMode, string spawnIdentifier)
        {
            _haveCachedLevel   = true;
            _cachedChapter     = chapterName;
            _cachedLevelIndex  = levelIndex;
            _cachedLoadingMode = loadingMode;
            _cachedSpawn       = spawnIdentifier;
            _service?.ReportLocalGoToLevel(chapterName, levelIndex, loadingMode, spawnIdentifier);
        }

        /// <summary>Seed a freshly started service with the last known local level + current seed, so a service
        /// started after a save was already loaded reports the real scene to peers from the first broadcast
        /// (host handshake → joining client auto-follows immediately) instead of <unknown>:-1.</summary>
        public static void PrimeServiceFromCache(NetService service)
        {
            if (service == null) return;
            if (_haveCachedLevel && !string.IsNullOrWhiteSpace(_cachedChapter) && _cachedChapter != "<unknown>")
                service.ReportLocalGoToLevel(_cachedChapter, _cachedLevelIndex, _cachedLoadingMode, _cachedSpawn);
            // Re-read the current generation seed straight from GameManager (latch reset so an unchanged seed is
            // still delivered to the new service) → the scene request carries it from the first broadcast.
            try { NetLevelSeed.ResetReportLatch(); NetLevelSeed.ReportObservedGameManagerSeed("service-start-prime"); }
            catch { }
        }

        public static void ReportGameState(string gameState)
        {
            _service?.ReportLocalGameState(gameState);
        }

        public static void ReportClearLevel()
        {
            // NOTE: do NOT clear the cached level here. ClearLevel fires as a normal part of every level
            // transition (the old level is torn down) — it does not mean we returned to the main menu. The
            // in-game gate must stay "in a non-menu level" across a transition; returning to the title is
            // captured instead by the next GoToLevel carrying loadingMode=Menu. Clearing here wrongly disabled
            // Create/Join while in a loaded run (Log188: GoToLevel Act_01_Caves:0 Normal → ClearLevel wiped it).
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
