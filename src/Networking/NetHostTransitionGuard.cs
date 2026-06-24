using UnityEngine;

namespace SULFURTogether.Networking
{
    /// <summary>
    /// Phase 5.6-LK-P2 (Type B fix): a host-side "transition in progress" latch that closes the both-ends-jump
    /// double-generate race.
    ///
    /// When BOTH the host and a client walk into the same exit at the same time, the host runs its OWN
    /// CompleteLevel (advancing + generating the next level) while the client's relay ALSO arrives. The relay
    /// landed in the ~0.5s window inside OnCompleteLevelRoutine where the host is in Cinematic — LocalState still
    /// reads the OLD level and GameState is not yet "Loading" — so the relay handler's "already at target" /
    /// "host is loading" guards both miss and the host LEADS a SECOND GoToLevel, generating the same level twice
    /// with two different seeds. The client then follows both broadcasts → double load.
    ///
    /// This latch is set at the EARLIEST point of any host-initiated transition (CompleteLevel / GoToLevel /
    /// SwitchLevelRoutine) and cleared when the host's finalized snapshot for the new level is applied. While it
    /// is active the relay handler DEFERS — the client keeps retrying, and by the time it clears the host is at
    /// the new level, so the retried relay sees "already at target" and is ignored. One generation, one load.
    ///
    /// A safety timeout auto-clears the latch so a missed End can never wedge all future relays.
    /// </summary>
    internal static class NetHostTransitionGuard
    {
        private static readonly object _lock = new object();
        private static bool   _active;
        private static float  _startedAt;
        private static string _source = "";
        private const float MaxSeconds = 30f;

        public static void Begin(string source)
        {
            lock (_lock)
            {
                _startedAt = Time.realtimeSinceStartup;
                _source = source ?? "";
                if (!_active)
                {
                    _active = true;
                    Plugin.Log.Info($"[HostTransitionGuard] begin source={_source}");
                }
            }
        }

        public static void End(string source)
        {
            lock (_lock)
            {
                if (!_active) return;
                _active = false;
                Plugin.Log.Info($"[HostTransitionGuard] end source={source} (was {_source})");
                _source = "";
            }
        }

        public static bool IsActive
        {
            get
            {
                lock (_lock)
                {
                    if (_active && Time.realtimeSinceStartup - _startedAt > MaxSeconds)
                    {
                        _active = false;
                        Plugin.Log.Warn($"[HostTransitionGuard] auto-cleared after {MaxSeconds:F0}s (stuck guard safety; last source={_source})");
                    }
                    return _active;
                }
            }
        }

        public static void Reset() { lock (_lock) { _active = false; _source = ""; } }
    }
}
