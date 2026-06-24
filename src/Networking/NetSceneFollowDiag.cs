using System.Threading;

namespace SULFURTogether.Networking
{
    /// <summary>
    /// Phase 5.3-I counters for the used-sets generation-input sync path. Purely diagnostic;
    /// these answer "did the Host capture/send used sets, did the Client receive/apply them, and
    /// what were the before/after counts on follow".
    /// </summary>
    internal static class NetSceneFollowDiag
    {
        public static int HostSceneUsedSetsCaptured;
        public static int HostSceneUsedSetsSent;
        public static int ClientSceneUsedSetsReceived;
        public static int ClientFollowUsedSetsApplied;
        public static int ClientFollowUsedSetsApplyFailed;

        public static int ClientFollowUsedChunksBeforeCount;
        public static int ClientFollowUsedChunksAfterCount;
        public static int ClientFollowUsedEventsRunBeforeCount;
        public static int ClientFollowUsedEventsRunAfterCount;
        public static int ClientFollowUsedEventsEnvBeforeCount;
        public static int ClientFollowUsedEventsEnvAfterCount;

        // Phase 5.3-J P1 load barrier (logging-only, reuses the ClientSceneAck channel).
        public static int ClientLoadedAckSent;
        public static int HostClientLoadedAckReceived;

        // Phase 5.3-K host generation-input push.
        public static int HostGenerationInputSentOnHandshake;
        public static int HostGenerationInputSentOnRequest;
        public static int HostGenerationInputNoSnapshotOnHandshake;
        public static int HostGenerationInputNoSnapshotOnRequest;

        // Phase 5.4-D-0 hub/safezone return finalized-request counters.
        public static int HubSnapshotIdentityCorrected;   // finalize: combat chapter + hub graph -> corrected
        public static int HubSnapshotIdentityFallback;    // finalize: chapter derived from graph name (no live hub state)
        public static int HubSnapshotPendingCombatIgnored;// finalize: stale pending combat chapter ignored for a hub graph
        public static int HubReturnDeferredMissingSeed;   // host send: hub target but finalized hub seed not ready
        public static int HubReturnPreliminaryRequestSent;// host send: preliminary hub request (AutoLoadAllowed=false)
        public static int HubReturnFinalizedRequestSent;  // host send: finalized hub request (AutoLoadAllowed=true)
        public static int HubAutoFollowSkippedMissingSeed;// client: generated hub request without seed -> skip
        public static int HubAutoFollowWaitingFinalized;  // client: waiting for finalized hub seed
        public static int HubAutoFollowStartedFinalized;  // client: started host-driven hub load with finalized seed
        public static int HubSeedMismatchReload;          // client: reloaded hub because local seed != host seed
        public static int HubSeedMatched;                 // client: hub seed already matched

        // Phase 5.4-D-1 graph-identity counters (catch wrong-run follows like request=Hedgemaze local=Sewers1).
        public static int SceneAckGraphMismatch;
        public static int SceneAckGraphMatched;
        public static int AutoFollowReloadGraphMismatch;

        public static string FormatGraphIdentity()
            => $"ackGraphMismatch={SceneAckGraphMismatch} ackGraphMatched={SceneAckGraphMatched} autoFollowReloadGraphMismatch={AutoFollowReloadGraphMismatch}";

        public static string FormatHubReturn()
            => $"snapCorrected={HubSnapshotIdentityCorrected} snapFallback={HubSnapshotIdentityFallback} snapPendingCombatIgnored={HubSnapshotPendingCombatIgnored} " +
               $"deferredMissingSeed={HubReturnDeferredMissingSeed} preliminarySent={HubReturnPreliminaryRequestSent} finalizedSent={HubReturnFinalizedRequestSent} " +
               $"clientSkipMissingSeed={HubAutoFollowSkippedMissingSeed} clientWaiting={HubAutoFollowWaitingFinalized} clientStartedFinalized={HubAutoFollowStartedFinalized} " +
               $"seedMismatchReload={HubSeedMismatchReload} seedMatched={HubSeedMatched}";

        public static void IncCaptured()       => Interlocked.Increment(ref HostSceneUsedSetsCaptured);
        public static void IncSent()           => Interlocked.Increment(ref HostSceneUsedSetsSent);
        public static void IncReceived()        => Interlocked.Increment(ref ClientSceneUsedSetsReceived);
        public static void IncApplied()         => Interlocked.Increment(ref ClientFollowUsedSetsApplied);
        public static void IncApplyFailed()     => Interlocked.Increment(ref ClientFollowUsedSetsApplyFailed);

        public static string Format()
            => $"captured={HostSceneUsedSetsCaptured} sent={HostSceneUsedSetsSent} received={ClientSceneUsedSetsReceived} " +
               $"applied={ClientFollowUsedSetsApplied} applyFailed={ClientFollowUsedSetsApplyFailed} " +
               $"chunks={ClientFollowUsedChunksBeforeCount}->{ClientFollowUsedChunksAfterCount} " +
               $"eventsRun={ClientFollowUsedEventsRunBeforeCount}->{ClientFollowUsedEventsRunAfterCount} " +
               $"eventsEnv={ClientFollowUsedEventsEnvBeforeCount}->{ClientFollowUsedEventsEnvAfterCount}";
    }
}
