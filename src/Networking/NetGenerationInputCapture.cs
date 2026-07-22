using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace SULFURTogether.Networking
{
    /// <summary>
    /// Phase 5.3-J: host-side cache of GenerationInputSnapshots captured at StartLevelRoutineGraph.
    /// HostSceneRequest sending prefers the snapshot matching the current seed/level over the
    /// (possibly post-generation) live GameManager used sets.
    /// </summary>
    internal static class NetGenerationInputCapture
    {
        private static readonly object _lock = new object();

        // FINALIZED snapshots (seed + graph confirmed from the live MakerGraph context), keyed by runKey
        // and by seed+level for fast send-time lookup.
        private static readonly Dictionary<string, NetGenerationInputSnapshot> _byRunKey =
            new Dictionary<string, NetGenerationInputSnapshot>();
        private static readonly Dictionary<string, NetGenerationInputSnapshot> _bySeedLevel =
            new Dictionary<string, NetGenerationInputSnapshot>();

        // PENDING snapshot from the StartLevelRoutineGraph prefix (stable inputs + used sets, but the seed
        // is only a candidate). Finalized at the first MakerGraphContext.ResetContext of the new graph.
        private static NetGenerationInputSnapshot? _pending;
        private static string _lastFinalizedKey = "";

        public static NetGenerationInputSnapshot? LastSnapshot => LastFinalizedSnapshot;
        public static NetGenerationInputSnapshot? LastFinalizedSnapshot { get; private set; }

        // Diagnostics
        public static int Captured;
        public static int Finalized;
        public static int MatchedOnSend;
        public static int FallbackOnSend;

        // Phase 5.3-M P0-E run-state sync counters.
        public static int RunStateSnapshotAppliedHost;
        public static int RunStateSnapshotAppliedClient;
        public static int RunStateSnapshotOverrideUsed;
        public static int RunStateSnapshotRevisionBumped;

        // Phase 5.4-D-1: authoritative level-transition target captured from SwitchLevelRoutine/GoToLevel.
        // The generation pending only knows the OLD run's chapter on a special jump (DesertBoss->WitchLevel,
        // SewersBoss->Hedgemaze); the transition target is the real chapter the host is loading.
        private sealed class PendingTransition
        {
            public string EnvName = "<unknown>";
            public int    EnvId = -1;
            public int    LevelIndex = -1;
            public string LoadingMode = "";
            public string Spawn = "";
            public string Source = "";
            public int    Revision;
            public float  CapturedAt;
        }
        private static PendingTransition? _pendingTransition;

        public static int TransitionCapturedGoToLevel;
        public static int TransitionCapturedSwitchLevel;
        public static int TransitionOverrideOldPending;
        public static int TransitionUsedForFinalize;
        public static int TransitionFinalizeWithoutTransition;

        public static string FormatTransitionCounters()
            => $"capturedGoTo={TransitionCapturedGoToLevel} capturedSwitch={TransitionCapturedSwitchLevel} " +
               $"overrideOldPending={TransitionOverrideOldPending} usedForFinalize={TransitionUsedForFinalize} " +
               $"finalizeWithoutTransition={TransitionFinalizeWithoutTransition}";

        /// <summary>
        /// Phase 5.4-D-1 P0-B: record the authoritative target the host is switching to. SwitchLevelRoutine is
        /// the final authority (CompleteLevel / debug "complete level" / necklace return / special jumps all go
        /// through it); GoToLevel is an earlier hint. The latest capture wins (SwitchLevelRoutine fires last).
        /// </summary>
        public static void CaptureLevelTransition(string envName, int envId, int levelIndex, string loadingMode, string spawn, string source)
        {
            int rev;
            bool hadOld;
            lock (_lock)
            {
                hadOld = _pendingTransition != null;
                rev = (_pendingTransition?.Revision ?? 0) + 1;
                _pendingTransition = new PendingTransition
                {
                    EnvName = string.IsNullOrWhiteSpace(envName) ? "<unknown>" : envName.Trim(),
                    EnvId = envId,
                    LevelIndex = levelIndex,
                    LoadingMode = loadingMode ?? "",
                    Spawn = spawn ?? "",
                    Source = source ?? "",
                    Revision = rev,
                    CapturedAt = UnityEngine.Time.realtimeSinceStartup,
                };
                if (hadOld) TransitionOverrideOldPending++;
                if ((source ?? "").IndexOf("Switch", StringComparison.OrdinalIgnoreCase) >= 0) TransitionCapturedSwitchLevel++;
                else TransitionCapturedGoToLevel++;
            }
            Plugin.Log.Info($"[LevelTransition] captured source={source} target={(string.IsNullOrWhiteSpace(envName) ? "<unknown>" : envName)}:{levelIndex} envId={envId} loadingMode={loadingMode} spawn={spawn} rev={rev}");
        }

        public static string FormatRunStateSyncCounters()
            => $"appliedHost={RunStateSnapshotAppliedHost} appliedClient={RunStateSnapshotAppliedClient} " +
               $"overrideUsed={RunStateSnapshotOverrideUsed} revisionBumped={RunStateSnapshotRevisionBumped}";

        private static void StoreFinalized(NetGenerationInputSnapshot snapshot)
        {
            if (snapshot == null) return;
            lock (_lock)
            {
                LastFinalizedSnapshot = snapshot;
                _byRunKey[snapshot.RunKey()] = snapshot;
                _bySeedLevel[snapshot.SeedLevelKey()] = snapshot;
                Finalized++;
            }
        }

        /// <summary>Find a finalized snapshot whose seed+level matches the supplied values.</summary>
        public static bool TryGet(bool hasSeed, int seed, int levelIndex, out NetGenerationInputSnapshot snapshot)
        {
            lock (_lock)
            {
                if (_bySeedLevel.TryGetValue(NetGenerationInputSnapshot.SeedLevelKey(hasSeed, seed, levelIndex), out var s))
                {
                    snapshot = s;
                    return true;
                }
            }
            snapshot = null!;
            return false;
        }

        // Note: there is deliberately no Clear(). This state is derived purely from local generation events and
        // describes the currently loaded level; each new generation supersedes it, and while no level is loaded
        // (main menu) the consumers classify the target as menu and never read it. Clearing it on a NETWORKING
        // lifecycle event (the old NetService.Stop() call) wedged a host who had started+stopped a session while
        // sitting in the hub: no level load ever re-finalized the snapshot, so every hub scene request deferred
        // forever (missing-finalized-hub-seed) and joining clients were never pulled in (Log469).

        // ---- StartLevelRoutineGraph capture (host side, generation-start = pre-used-set-mutation) ----

        private static bool _argsResolved;
        private static int _seedArgIndex = -1;
        private static int _graphArgIndex = -1;
        private static int _levelArgIndex = -1;
        private static int _loadingModeArgIndex = -1;
        private static int _spawnArgIndex = -1;

        /// <summary>
        /// Called from a StartLevelRoutineGraph prefix on the Host (and harmlessly on the Client).
        /// Reads the used sets BEFORE this level's generation mutates them. Parameter indices are
        /// resolved by NAME from the real method (discovery-first; never positional guessing).
        /// </summary>
        public static void CaptureFromStartLevelRoutineGraph(object gameManager, object[] args, MethodBase method)
        {
            try
            {
                if (!_argsResolved) ResolveArgIndices(method);

                var snap = new NetGenerationInputSnapshot { Finalized = false };

                // used sets at generation start (pre-mutation for this level).
                if (NetGameManagerUsedSets.TryRead(gameManager, out var sets, out var readErr))
                    snap.UsedSets = sets;
                else if (Plugin.Cfg.LogUsedSetsTrace.Value)
                    Plugin.Log.Warn($"[GenerationInputSnapshot] used-set read failed: {readErr}");

                // seed CANDIDATE only — the prefix runs before this graph sets its real seed, so the value
                // read here is often the PREVIOUS level's seed. It is finalized at ResetContext.
                if (_seedArgIndex >= 0 && args != null && _seedArgIndex < args.Length && TryToInt(args[_seedArgIndex], out int argSeed) && argSeed != 0)
                    snap.SeedCandidate = argSeed;
                else if (NetLevelSeed.TryReadCurrentSeed(gameManager, out int curSeed, out _))
                    snap.SeedCandidate = curSeed;

                // stable inputs from the method args (level/loadingMode/spawn resolved by parameter name).
                if (_levelArgIndex >= 0 && args != null && _levelArgIndex < args.Length && TryToInt(args[_levelArgIndex], out int lvl))
                    snap.LevelIndex = lvl;
                if (_loadingModeArgIndex >= 0 && args != null && _loadingModeArgIndex < args.Length)
                    snap.LoadingMode = args[_loadingModeArgIndex]?.ToString() ?? "";
                if (_spawnArgIndex >= 0 && args != null && _spawnArgIndex < args.Length)
                    snap.SpawnIdentifier = args[_spawnArgIndex]?.ToString() ?? "";
                snap.GraphName = ResolveGraphName(args); // may be empty; finalized from MakerSet later

                if (NetRunStateBridge.TryGetLocalRunState(out var run))
                {
                    if (run.HasLevel)
                    {
                        snap.Chapter = run.ChapterName;
                        if (snap.LevelIndex < 0) snap.LevelIndex = run.LevelIndex;
                    }
                    snap.Revision = run.Revision;
                }

                lock (_lock)
                {
                    _pending = snap;
                    Captured++;
                }

                if (Plugin.Cfg.LogUsedSetsTrace.Value)
                {
                    Plugin.Log.Info($"[GenerationInputSnapshot] pending captured phase=StartLevelRoutineGraphPrefix chapter={snap.Chapter} level={snap.LevelIndex} seedCandidate={snap.SeedCandidate} usedChunks={NetHostUsedSets.Summary(snap.UsedSets.UsedChunksThisRun)}");
                    Plugin.Log.Info($"[GenerationInputSnapshot]   pending usedEventsRun={NetHostUsedSets.Summary(snap.UsedSets.UsedEventsThisRun)} usedEventsEnv={NetHostUsedSets.Summary(snap.UsedSets.UsedEventsThisEnvironment)}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[GenerationInputSnapshot] capture failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Phase 5.3-L P0-A: finalize the snapshot from the live MakerGraph context (first ResetContext of
        /// the new graph). graphName + graphSeed here are the REAL generation inputs; the pending prefix
        /// seed is the previous level's. Called on both Host and Client (harmless on the Client).
        /// </summary>
        public static void FinalizeFromMakerContext(string graphName, long graphSeed)
        {
            try
            {
                // Finalize once per (graph, seed) — ResetContext fires for every node, and a graph may run
                // without a pending prefix; this key guard prevents per-node re-finalize spam either way.
                string key = (graphName ?? "?") + "|" + graphSeed;
                NetGenerationInputSnapshot snap;
                bool hadPending;
                lock (_lock)
                {
                    if (key == _lastFinalizedKey) return;
                    _lastFinalizedKey = key;
                    hadPending = _pending != null;
                    snap = _pending != null ? _pending.Clone() : new NetGenerationInputSnapshot();
                    snap.SeedCandidate = _pending?.SeedCandidate ?? 0;
                }

                // SEED-1: this generation has consumed GlobalSettings.ForceLevelSeed (GameManager.currentSeed is
                // already fixed by the time the first graph node runs). Release it so it cannot pin the NEXT
                // local generation to this map — a client that falls back to generating on its own, or unlinks
                // and plays solo, must roll a fresh seed.
                NetLevelSeed.ReleaseAppliedForceLevelSeed("generation-finalize");

                if (!hadPending && NetRunStateBridge.TryGetLocalRunState(out var run))
                {
                    if (run.HasLevel) { snap.Chapter = run.ChapterName; if (snap.LevelIndex < 0) snap.LevelIndex = run.LevelIndex; }
                    snap.Revision = run.Revision;
                }

                string g = graphName ?? "";
                if (g.Length > 0 && g != "?") snap.GraphName = g; // else keep pending/empty graph name
                snap.Seed = unchecked((int)graphSeed);
                snap.HasSeed = true;
                snap.Finalized = true;

                // Phase 5.4-D-1 P0-C: the AUTHORITATIVE chapter/level come from the latest SwitchLevelRoutine /
                // GoToLevel target, not the (possibly stale) generation pending. MakerGraph only contributes
                // graphName + seed + usedSets. This fixes special jumps (DesertBoss->Act_03_EndChurch/WitchLevel,
                // SewersBoss->Act_01_Hedgemaze/Hedgemaze) where the pending still held the previous run's chapter.
                PendingTransition? trans;
                lock (_lock) { trans = _pendingTransition; }
                bool usedTransition = false;
                if (trans != null && !string.IsNullOrWhiteSpace(trans.EnvName) && trans.EnvName != "<unknown>")
                {
                    string oldChapter = snap.Chapter;
                    int oldLevel = snap.LevelIndex;
                    snap.Chapter = trans.EnvName;
                    if (trans.LevelIndex >= 0) snap.LevelIndex = trans.LevelIndex;
                    if (!string.IsNullOrWhiteSpace(trans.LoadingMode)) snap.LoadingMode = trans.LoadingMode;
                    if (trans.Spawn != null) snap.SpawnIdentifier = trans.Spawn;
                    snap.FromTransition = true;
                    snap.TransitionSource = trans.Source;
                    snap.TargetEnvId = trans.EnvId;
                    usedTransition = true;
                    TransitionUsedForFinalize++;
                    Plugin.Log.Info($"[LevelTransition] finalized target={snap.Chapter}:{snap.LevelIndex} graph={(string.IsNullOrEmpty(snap.GraphName) ? "?" : snap.GraphName)} seed={snap.Seed} source={trans.Source} envId={trans.EnvId} rev={trans.Revision} (was {oldChapter}:{oldLevel})");
                }
                else
                {
                    TransitionFinalizeWithoutTransition++;
                    Plugin.Log.Warn($"[LevelTransition] finalize without transition; fallback current GameManager state graph={(string.IsNullOrEmpty(snap.GraphName) ? "?" : snap.GraphName)} seed={snap.Seed} chapter={snap.Chapter} level={snap.LevelIndex}");
                }

                // Phase 5.4-D-0 P0-A (fallback only): when there was NO authoritative transition, a hub/safezone
                // graph must still not inherit the previous combat run's chapter.
                if (!usedTransition && NetSceneClassify.IsHubOrSafeZoneGraph(snap.GraphName))
                {
                    if (NetSceneClassify.IsCombatLikeChapter(snap.Chapter))
                    {
                        string oldChapter = snap.Chapter;
                        int oldLevel = snap.LevelIndex;
                        string corrected;
                        bool fromLive = false;
                        // Prefer the live LocalState chapter when it is itself a hub (handles special env names like
                        // ChurchHub_Xmas); otherwise fall back to the graph name as the chapter.
                        if (NetRunStateBridge.TryGetLocalRunState(out var liveRun) && liveRun.HasLevel
                            && NetSceneClassify.IsHubOrSafeZoneChapter(liveRun.ChapterName))
                        {
                            corrected = liveRun.ChapterName;
                            fromLive = true;
                        }
                        else
                        {
                            corrected = snap.GraphName;
                        }
                        snap.Chapter = corrected;
                        snap.LevelIndex = 0; // hubs/safezones are level 0
                        NetSceneFollowDiag.HubSnapshotIdentityCorrected++;
                        if (!fromLive) NetSceneFollowDiag.HubSnapshotIdentityFallback++;
                        if (hadPending) NetSceneFollowDiag.HubSnapshotPendingCombatIgnored++;
                        Plugin.Log.Info($"[GenerationInputSnapshot] hub identity corrected oldChapter={oldChapter} oldLevel={oldLevel} graph={snap.GraphName} newChapter={snap.Chapter} newLevel={snap.LevelIndex} seed={snap.Seed} source={(fromLive ? "liveLocalState" : "graphName")}");
                    }
                    else
                    {
                        Plugin.Log.Info($"[GenerationInputSnapshot] safezone graph finalized graph={snap.GraphName} chapter={snap.Chapter} level={snap.LevelIndex} seed={snap.Seed}");
                    }
                }

                snap.RunId = snap.RunKey();

                StoreFinalized(snap);

                if (Plugin.Cfg.LogUsedSetsTrace.Value)
                {
                    string src = hadPending ? "ResetContext" : "ResetContext(no-pending,current-state)";
                    if (!hadPending)
                        Plugin.Log.Warn($"[GenerationInputSnapshot] finalize without pending; using current state");
                    Plugin.Log.Info($"[GenerationInputSnapshot] finalized graph={(string.IsNullOrEmpty(snap.GraphName) ? "?" : snap.GraphName)} seed={snap.Seed} chapter={snap.Chapter} level={snap.LevelIndex} usedChunks={NetHostUsedSets.Summary(snap.UsedSets.UsedChunksThisRun)} source={src}");
                }

                // Phase 5.3-M P0-D: single notification entry — push the finalized snapshot into the network
                // run state so NetRunState/LocalState/manifest gate/scene match stop using the stale level=0.
                NetRunStateBridge.ApplyFinalizedGenerationSnapshot(snap);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[GenerationInputSnapshot] finalize failed: {ex.GetType().Name}: {ex.Message}");
            }
        }


        private static void ResolveArgIndices(MethodBase method)
        {
            _argsResolved = true;
            try
            {
                var ps = method?.GetParameters() ?? Array.Empty<ParameterInfo>();
                var dump = new List<string>();
                for (int i = 0; i < ps.Length; i++)
                {
                    var p = ps[i];
                    dump.Add($"[{i}]{p.ParameterType.Name} {p.Name}");
                    string n = (p.Name ?? "").ToLowerInvariant();
                    if (_seedArgIndex < 0 && n.Contains("seed")) _seedArgIndex = i;
                    if (_levelArgIndex < 0 && (n == "levelindex" || n.Contains("level") && p.ParameterType == typeof(int))) _levelArgIndex = i;
                    if (_graphArgIndex < 0 && (n.Contains("graph") || n.Contains("set") || n.Contains("maker"))) _graphArgIndex = i;
                    if (_loadingModeArgIndex < 0 && n.Contains("loading")) _loadingModeArgIndex = i;
                    if (_spawnArgIndex < 0 && n.Contains("spawn")) _spawnArgIndex = i;
                }
                Plugin.Log.Info($"[GenerationInputSnapshot] StartLevelRoutineGraph params: {string.Join(", ", dump)} " +
                    $"=> seedIdx={_seedArgIndex} graphIdx={_graphArgIndex} levelIdx={_levelArgIndex} loadingIdx={_loadingModeArgIndex} spawnIdx={_spawnArgIndex}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[GenerationInputSnapshot] ResolveArgIndices failed: {ex.Message}"); }
        }

        private static string ResolveGraphName(object[]? args)
        {
            if (args == null) return "";
            // Prefer the resolved graph/set parameter.
            if (_graphArgIndex >= 0 && _graphArgIndex < args.Length)
            {
                string n = NameOf(args[_graphArgIndex]);
                if (!string.IsNullOrEmpty(n)) return n;
            }
            // Else: first arg that looks like a graph/set object or a non-trivial string.
            foreach (var a in args)
            {
                if (a is UnityEngine.Object) { string n = NameOf(a); if (!string.IsNullOrEmpty(n)) return n; }
            }
            return "";
        }

        private static string NameOf(object? o)
        {
            if (o == null) return "";
            if (o is string s) return s;
            if (o is UnityEngine.Object uo) return uo != null ? uo.name : "";
            return o.ToString() ?? "";
        }

        private static bool TryToInt(object? o, out int value)
        {
            value = 0;
            if (o == null) return false;
            try
            {
                switch (o)
                {
                    case int i: value = i; return true;
                    case uint ui: value = unchecked((int)ui); return true;
                    case long l: value = unchecked((int)l); return true;
                    case short sh: value = sh; return true;
                    default: return false;
                }
            }
            catch { return false; }
        }
    }
}
