namespace SULFURTogether.Networking
{
    /// <summary>
    /// Phase 5.4-D-0: shared scene classification by GRAPH name or CHAPTER name. Hubs / safe zones
    /// (ChurchHub, ChurchHub_Xmas, ...) are MakerGraph-generated and therefore DO need a seed, unlike a
    /// pure menu. This is keyword-based and deliberately conservative; when a name cannot be classified the
    /// callers must log/dump rather than guess (see [HubReturnDiag]).
    /// </summary>
    internal static class NetSceneClassify
    {
        public static bool IsHubOrSafeZoneGraph(string? graphName) => MatchesHub(graphName);

        public static bool IsHubOrSafeZoneChapter(string? chapter) => MatchesHub(chapter);

        private static bool MatchesHub(string? s)
        {
            string n = (s ?? string.Empty).ToLowerInvariant();
            if (n.Length == 0) return false;
            // NOTE (5.4-D-1): bare "church" is intentionally NOT a hub keyword — it falsely matched the COMBAT
            // boss chapter Act_03_EndChurch. ChurchHub / ChurchHub_Xmas are still caught via "hub".
            return n.Contains("hub") || n.Contains("safezone") || n.Contains("safe_zone")
                || n.Contains("town") || n.Contains("hideout") || n.Contains("vendor") || n.Contains("xmas")
                || n.Contains("sanctuary") || n.Contains("camp") || n.Contains("market");
        }

        /// <summary>A stale combat-run chapter (Act_xx / Caves / Fortress / ...) — used to detect when a hub
        /// graph's finalized snapshot wrongly inherited the previous combat chapter.</summary>
        public static bool IsCombatLikeChapter(string? chapter)
        {
            string n = (chapter ?? string.Empty).ToLowerInvariant();
            if (n.Length == 0) return false;
            if (MatchesHub(n)) return false;
            return n.Contains("act_") || n.Contains("act0") || n.Contains("caves") || n.Contains("fortress")
                || n.Contains("dungeon") || n.Contains("combat") || n.Contains("crypt") || n.Contains("lair")
                || n.Contains("level");
        }
    }
}
