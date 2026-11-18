using PerfectRandom.Sulfur.Core;
using UnityEngine;

namespace SULFURTogether.Networking
{
    /// <summary>
    /// JF-1: "this peer is between levels" — from the start of a level transition until the new level is generated.
    ///
    /// <para>The game has no such flag. <c>GameManager.GoToLevel</c> starts <c>SwitchLevelRoutine</c>, which keeps
    /// <c>GameState</c> where it was for half a second before <c>SetState(Loading)</c>; generation then runs as a
    /// node graph and ends in <c>LevelGeneration.ShowLevelNode</c>, which either parks on the press-to-continue screen
    /// (<c>SetAwaitBeforeStartLevel(true)</c>, environments with <c>awaitUserStartLevel</c>) or hands the level over
    /// at once (<c>SetState(Running)</c>). Reading <c>GameState</c> alone misses the first half second, and a parked
    /// peer reads <c>Loading</c> too (AWAIT-1).</para>
    ///
    /// <para>Why it matters: a client that started a host-driven <c>GoToLevel</c> while it was still generating the
    /// previous level ran two transitions at once. The old <c>ShowLevelNode</c> finished during the new teardown and
    /// set the game Running with no local player; <c>ProjectileSystem</c>, <c>BatchedNPCRaycasts</c> and
    /// <c>DistanceLODManager</c> then threw every frame (round 671: 950, and 1070 on 1.8.0 in round 672), and the
    /// forced seed was lost, so the client generated the level with random seeds until the host re-sent its request.
    /// AWAIT-3 only retires a node that is parked, which a node mid-generation is not.</para>
    ///
    /// <para>Owner: the game's own transition calls (<c>LocalTransitionPatches</c>). Readers only observe. Set on
    /// <c>SwitchLevelRoutine</c>; cleared when the new level parks at press-to-continue, or
    /// when it goes Running after this transition's Loading, or after a safety timeout. It tracks the local game, not
    /// the session, so nothing resets it on a session change: the game's own next transition does.</para>
    /// </summary>
    internal static class NetLocalTransition
    {
        private const float MaxSeconds = 90f;

        private static bool _active;
        private static bool _sawLoading;
        private static float _since;
        private static string _source = "";

        public static void Begin(string source)
        {
            bool wasActive = _active;
            _active = true;
            _sawLoading = false;
            _since = Time.realtimeSinceStartup;
            _source = source ?? "";
            if (!wasActive) Plugin.Log.Info($"[LocalTransition] begin source={_source}");
        }

        /// <summary>From <c>GameManager.SetState</c>.</summary>
        public static void OnSetState(GameState state)
        {
            if (!_active) return;
            if (state == GameState.Loading) { _sawLoading = true; return; }
            if (state == GameState.Running && _sawLoading && !NetAwaitStartLevel.IsLocalAwaitingStartLevel)
                End("level running");
        }

        /// <summary>From <c>GameManager.SetAwaitBeforeStartLevel</c>.</summary>
        public static void OnAwaitStartLevel(bool state)
        {
            if (state && _active) End("press-to-continue");
        }

        /// <summary>True from the start of a level transition until the new level is generated (parked or running).</summary>
        public static bool InFlight
        {
            get
            {
                if (_active && Time.realtimeSinceStartup - _since > MaxSeconds)
                    End($"timeout after {MaxSeconds:F0}s");
                return _active;
            }
        }

        private static void End(string reason)
        {
            if (!_active) return;
            _active = false;
            Plugin.Log.Info($"[LocalTransition] end reason={reason} (began {_source}, {Time.realtimeSinceStartup - _since:F1}s)");
            _source = "";
        }
    }
}
