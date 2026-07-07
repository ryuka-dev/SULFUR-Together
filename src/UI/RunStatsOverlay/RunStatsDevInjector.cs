using System.Collections.Generic;
using UnityEngine.InputSystem;
using PerfectRandom.Sulfur.Core;
using SULFURTogether.Networking;
using SULFURTogether.Networking.Gameplay;

namespace SULFURTogether.UI.RunStatsOverlay
{
    /// <summary>
    /// RS-5: development-only simulated-player injector, so one developer can verify multi-card layout, hover
    /// animation and the 5+-card carousel (edge peek, one-step scroll, end clamping) without gathering real
    /// players. Each End-key press appends one simulated player with a distinct name and randomized values for
    /// all seven stats to a PURELY LOCAL display list.
    ///
    /// Strictly display-side by design: the simulated entries are never broadcast, never enter
    /// <see cref="NetRunStatsManager"/>'s authoritative tally, never touch saves, and never affect transitions
    /// or run finalization — <see cref="RunStatsOverlayManager"/> merely appends them after the real finalized
    /// list when building cards, and clears them every time the overlay hides, so nothing carries into the
    /// next Run. Gated on the game's own <c>GameManager.DeveloperMode</c> (the switch behind the F3 dev
    /// tools): in a normal player session the End key does nothing, with no mod config entry involved.
    /// </summary>
    internal static class RunStatsDevInjector
    {
        private static readonly List<NetRunStats> _simulated = new List<NetRunStats>();
        // System.Random on purpose: UnityEngine.Random's state is shared with (seeded) gameplay systems and a
        // dev key must not be able to perturb it.
        private static readonly System.Random _rng = new System.Random();
        private static int _nextId = 1;
        private static bool _keyboardBroken;

        /// <summary>Bumped on every add/clear; RunStatsOverlayManager rebuilds the cards when it changes.</summary>
        public static int Version { get; private set; }

        public static IReadOnlyList<NetRunStats> Simulated => _simulated;

        /// <summary>Polled once per frame while in the game scene. Legacy <c>Input.GetKeyDown</c> would throw in
        /// this game (legacy input is disabled build-wide — see RunStatsInputReader), so the key is read through
        /// the Input System, with the same one-shot self-disable guard as the overlay's other input sources.</summary>
        public static void Poll()
        {
            if (_keyboardBroken || !GameManager.DeveloperMode) return;

            try
            {
                if (!EndKeyPressedThisFrame()) return;
            }
            catch (System.Exception ex)
            {
                _keyboardBroken = true;
                NetLogger.Warn($"[RunStatsOverlay] dev-injector keyboard poll failed — End key disabled: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            int id = _nextId++;
            _simulated.Add(new NetRunStats
            {
                // "sim-" ids can never collide with real peer ids ("host"/"client-N"), so a simulated card can
                // never be mistaken for the local player's own (gold-accented) card.
                PeerId = $"sim-{id}",
                PlayerName = $"Tester {id}",
                ShotsFired = _rng.Next(0, 500),
                DamageDealt = _rng.Next(0, 8000),
                Kills = _rng.Next(0, 60),
                TimesDowned = _rng.Next(0, 6),
                Rescues = _rng.Next(0, 6),
                DamageTaken = _rng.Next(0, 2000),
                DestructiblesDestroyed = _rng.Next(0, 40),
            });
            Version++;
            NetLogger.Info($"[RunStatsOverlay] dev-injector added simulated player {id} (total {_simulated.Count})");
        }

        /// <summary>Called every time the overlay hides — simulated players never survive into the next Run.</summary>
        public static void Clear()
        {
            if (_simulated.Count == 0) return;
            _simulated.Clear();
            _nextId = 1;
            Version++;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool EndKeyPressedThisFrame()
        {
            var keyboard = Keyboard.current;
            return keyboard != null && keyboard.endKey.wasPressedThisFrame;
        }
    }
}
