using System.Collections.Generic;
using UnityEngine;

namespace SULFURTogether.ReverseProbe
{
    /// <summary>
    /// Shared throttle state for all reverse probes.
    /// All access is on the Unity main thread.
    /// </summary>
    public static class ReverseProbeState
    {
        // ---- legacy per-category timer (kept for backward compat) ----
        public static float LastNpcUpdateLog = -9999f;
        public const  float NpcUpdateInterval = 10f;

        // ---- per-key throttle (two dicts — no ValueTuple on net46) ----
        private static readonly Dictionary<string, float> _keyLastTime = new Dictionary<string, float>();
        private static readonly Dictionary<string, int>   _keyCount    = new Dictionary<string, int>();

        /// <summary>
        /// Returns true if the key should be logged now.
        /// maxPerWindow = 0  → one log per window (throttle mode).
        /// maxPerWindow > 0  → up to N logs within the same window, then silence.
        /// </summary>
        public static bool ShouldLog(string key, float intervalSeconds, int maxPerWindow = 0)
        {
            float now = Time.realtimeSinceStartup;

            if (_keyLastTime.TryGetValue(key, out float last) && (now - last) < intervalSeconds)
            {
                // still within the throttle window
                if (maxPerWindow <= 0) return false;  // one-per-window mode: suppress
                int count = _keyCount.TryGetValue(key, out int c) ? c : 0;
                if (count >= maxPerWindow) return false;
                _keyCount[key] = count + 1;
                return true;
            }

            // window expired or first call — open a new window
            _keyLastTime[key] = now;
            _keyCount[key]    = 1;
            return true;
        }

        // ---- AI target change detection ----
        // agentInstanceId → last known target instanceId
        private static readonly Dictionary<string, string> _aiTargets
            = new Dictionary<string, string>();

        /// <summary>Returns true if the agent's target has changed since the last call.</summary>
        public static bool HasAiTargetChanged(string agentId, string newTargetId)
        {
            if (_aiTargets.TryGetValue(agentId, out string prev) && prev == newTargetId)
                return false;
            _aiTargets[agentId] = newTargetId;
            return true;
        }

        // ---- signature discovery dedup ----
        private static readonly HashSet<string> _reportedSignatures = new HashSet<string>();

        public static bool ShouldReportSignature(string sig) => _reportedSignatures.Add(sig);
    }
}
