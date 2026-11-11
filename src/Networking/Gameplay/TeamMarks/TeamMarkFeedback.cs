using System;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Audio;
using PerfectRandom.Sulfur.Core.Units;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay.TeamMarks
{
    /// <summary>
    /// Team marks (issue #26): a short sound when the local player's direct hit took the team bonus. The host plays it
    /// from its own applied hit (<c>TeamMarkPatches</c>); a client from the host's result for that hit
    /// (<c>NetHostHitResult.TeamMultiplier</c>, in <c>ClientHitTracker</c>), so it never guesses from the icon. One sound
    /// per shot and at most <see cref="MaxPerSecond"/> a second, so a shotgun or a fast gun does not turn into a buzz.
    /// Uses the game's own crit impact sound.
    /// </summary>
    internal static class TeamMarkFeedback
    {
        private const int MaxPerSecond = 4;

        private static float _windowStart;
        private static int _inWindow, _lastShotFrame = -1;

        public static int Played, Throttled;

        /// <param name="shotFrame">The frame the shot was fired in: every pellet and damage component of one shot hits in
        /// that frame, while a client's results for them can arrive over several.</param>
        public static void Play(Unit at, int shotFrame)
        {
            if (shotFrame == _lastShotFrame) return; // another pellet / component of the same shot
            _lastShotFrame = shotFrame;
            float now = Time.realtimeSinceStartup;
            if (now - _windowStart >= 1f) { _windowStart = now; _inWindow = 0; }
            if (_inWindow >= MaxPerSecond) { Throttled++; return; }
            _inWindow++;
            try
            {
                var bank = StaticInstance<SoundBank>.Instance;
                if (bank != null && bank.soundImpactHitCrit != null && at != null) { bank.soundImpactHitCrit.Play(at.transform); Played++; }
            }
            catch (Exception ex) { NetLogger.Warn($"[TeamMark] sound failed: {ex.Message}"); }
        }
    }
}
