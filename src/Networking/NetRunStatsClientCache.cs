using System.Collections.Generic;
using SULFURTogether.Networking.Gameplay;

namespace SULFURTogether.Networking
{
    /// <summary>
    /// RS-2: every peer's local cache of the Host's finalized end-of-Run statistics broadcast. Populated on a Client
    /// when the packet arrives, and locally on the Host at the moment it finalizes (no network round trip needed for
    /// its own copy). The overlay UI (Phase 3+) consumes <see cref="PendingRunEndDisplay"/> to know a card set is
    /// waiting to be shown, and calls <see cref="ConsumeAndClear"/> once it has shown (or given up on) it.
    /// </summary>
    internal static class NetRunStatsClientCache
    {
        public static IReadOnlyList<NetRunStats>? LastFinalized { get; private set; }
        public static bool PendingRunEndDisplay { get; private set; }
        private static int _lastAppliedRunSeq = -1;

        public static void ApplyReceivedBroadcast(NetRunStatsList list)
        {
            if (list == null) return;
            if (list.RunSeq <= _lastAppliedRunSeq) return; // stale/duplicate resend
            _lastAppliedRunSeq = list.RunSeq;
            LastFinalized = list.Players.AsReadOnly();
            PendingRunEndDisplay = true;
            NetLogger.Info($"[RunStats] Client received finalized runSeq={list.RunSeq} {list.Players.Count} player(s)");
            foreach (var s in list.Players) NetLogger.Info($"[RunStats]   {s.ToCompactString()}");
        }

        /// <summary>Host-local shortcut: the Host already has the exact data it just finalized, no decode needed.</summary>
        public static void ApplyLocalFinalize(int runSeq, IReadOnlyList<NetRunStats> players)
        {
            if (runSeq <= _lastAppliedRunSeq) return;
            _lastAppliedRunSeq = runSeq;
            LastFinalized = players;
            PendingRunEndDisplay = true;
        }

        public static void ConsumeAndClear()
        {
            PendingRunEndDisplay = false;
            LastFinalized = null;
        }
    }
}
