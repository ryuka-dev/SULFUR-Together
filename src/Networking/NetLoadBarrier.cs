using System.Collections.Generic;
using System.Linq;

namespace SULFURTogether.Networking
{
    /// <summary>
    /// Phase 5.3-M P1-G: lightweight HOST-side load barrier. It tracks which connected clients have
    /// acknowledged loading the Host's current run (waiting -> client loaded -> all clients loaded).
    ///
    /// This phase is LOG/STATUS ONLY by default (LoadBarrierLogOnlyMode=true, LoadBarrierBlockHostAdvance=false):
    /// it never freezes host gameplay, suppresses runtime sync, or blocks the host from advancing. It only
    /// makes the host "look like it is waiting for clients" in the log/status and exposes whether all
    /// clients have caught up. Real host-side gating (door/AI freeze) is intentionally deferred.
    /// </summary>
    internal static class NetLoadBarrier
    {
        private static readonly object _lock = new object();

        private static string _runKey = "";
        private static readonly Dictionary<string, bool> _peerLoaded = new Dictionary<string, bool>();
        private static float _startedAt;
        private static bool _timedOut;
        private static bool _allLoadedLogged;

        // ---- counters (diagnostic) ----
        public static int LoadBarrierStarted;
        public static int LoadBarrierClientPending;
        public static int LoadBarrierClientLoaded;
        public static int LoadBarrierAllLoaded;
        public static int LoadBarrierTimeout;
        public static int LoadBarrierLogOnly;

        public static void Reset()
        {
            lock (_lock)
            {
                _runKey = "";
                _peerLoaded.Clear();
                _startedAt = 0f;
                _timedOut = false;
                _allLoadedLogged = false;
            }
        }

        private static bool Enabled
        {
            get { try { return Plugin.Cfg.EnableLoadBarrier.Value; } catch { return false; } }
        }

        // Match on chapter:level:seed; graph is appended for readability only.
        public static string RunKeyFor(string chapter, int level, bool hasSeed, int seed, string graph)
            => $"{(string.IsNullOrWhiteSpace(chapter) ? "<unknown>" : chapter)}:{level}:{(hasSeed ? seed.ToString() : "?")}";

        /// <summary>Host: a (combat or hub-return, auto-load) HostSceneRequest for runKey was sent to peerId.</summary>
        public static void MarkPending(string peerId, string runKey, string graph, string reason = "combat")
        {
            if (!Enabled || string.IsNullOrWhiteSpace(peerId) || string.IsNullOrWhiteSpace(runKey)) return;
            lock (_lock)
            {
                bool logOnly;
                try { logOnly = Plugin.Cfg.LoadBarrierLogOnlyMode.Value; } catch { logOnly = true; }
                if (logOnly) LoadBarrierLogOnly++;

                if (runKey != _runKey)
                {
                    // New run — start a fresh barrier.
                    _runKey = runKey;
                    _peerLoaded.Clear();
                    _startedAt = Now();
                    _timedOut = false;
                    _allLoadedLogged = false;
                    LoadBarrierStarted++;
                }

                if (!_peerLoaded.ContainsKey(peerId))
                {
                    _peerLoaded[peerId] = false;
                    LoadBarrierClientPending++;
                    NetLogger.Info($"[LoadBarrier] waiting clients run={_runKey} graph={(string.IsNullOrEmpty(graph) ? "?" : graph)} pending={PendingCountLocked()} reason={(string.IsNullOrEmpty(reason) ? "combat" : reason)}");
                }
            }
        }

        /// <summary>Host: peerId reported it loaded runKey (ClientSceneAck IsInTargetScene).</summary>
        public static void MarkLoaded(string peerId, string runKey, string graph)
        {
            if (!Enabled || string.IsNullOrWhiteSpace(peerId)) return;
            lock (_lock)
            {
                // Only accept an ack for the run the barrier is currently tracking. An ack for a stale/other
                // run (e.g. the previous level) is ignored so it cannot prematurely satisfy the barrier.
                if (_runKey.Length > 0 && !string.IsNullOrWhiteSpace(runKey) && runKey != _runKey)
                    return;

                bool was = _peerLoaded.TryGetValue(peerId, out var loaded) && loaded;
                _peerLoaded[peerId] = true;
                if (!was)
                {
                    LoadBarrierClientLoaded++;
                    NetLogger.Info($"[LoadBarrier] client loaded peer={peerId} run={(_runKey.Length > 0 ? _runKey : runKey)} graph={(string.IsNullOrEmpty(graph) ? "?" : graph)} pending={PendingCountLocked()}");
                }

                if (!_allLoadedLogged && _peerLoaded.Count > 0 && PendingCountLocked() == 0)
                {
                    _allLoadedLogged = true;
                    LoadBarrierAllLoaded++;
                    NetLogger.Info($"[LoadBarrier] all clients loaded run={(_runKey.Length > 0 ? _runKey : runKey)} clients={_peerLoaded.Count}");
                }
            }
        }

        public static void RemovePeer(string peerId)
        {
            if (string.IsNullOrWhiteSpace(peerId)) return;
            lock (_lock) { _peerLoaded.Remove(peerId); }
        }

        /// <summary>Host: per-frame timeout check (log only).</summary>
        public static void Tick()
        {
            if (!Enabled) return;
            lock (_lock)
            {
                if (_timedOut || _peerLoaded.Count == 0) return;
                if (PendingCountLocked() == 0) return;
                float timeout;
                try { timeout = Plugin.Cfg.LoadBarrierTimeoutSeconds.Value; } catch { timeout = 30f; }
                if (Now() - _startedAt < timeout) return;
                _timedOut = true;
                LoadBarrierTimeout++;
                NetLogger.Warn($"[LoadBarrier] timeout waiting clients run={_runKey} pending={PendingCountLocked()} (log-only; host not blocked)");
            }
        }

        public static string FormatStatus()
        {
            if (!Enabled) return "loadBarrier=off";
            lock (_lock)
            {
                if (_peerLoaded.Count == 0) return "loadBarrier=idle";
                return $"loadBarrier[run={_runKey} pending={PendingCountLocked()}/{_peerLoaded.Count} timedOut={_timedOut}]";
            }
        }

        public static string FormatCounters()
            => $"started={LoadBarrierStarted} pending={LoadBarrierClientPending} loaded={LoadBarrierClientLoaded} allLoaded={LoadBarrierAllLoaded} timeout={LoadBarrierTimeout} logOnly={LoadBarrierLogOnly}";

        private static int PendingCountLocked() => _peerLoaded.Count(kv => !kv.Value);

        private static float Now() => UnityEngine.Time.realtimeSinceStartup;
    }
}
