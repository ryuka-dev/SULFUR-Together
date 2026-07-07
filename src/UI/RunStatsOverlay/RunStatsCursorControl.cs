using UnityEngine;

namespace SULFURTogether.UI.RunStatsOverlay
{
    /// <summary>
    /// RS-3: acquire/release pair that temporarily frees the mouse cursor while the run-stats cards are shown
    /// (so the player can look the cards over, and Phase 4's hover/scroll can reuse the same freed state).
    ///
    /// The pre-existing Cursor state is saved exactly once on <see cref="Acquire"/> and restored exactly once
    /// on <see cref="Release"/> — never re-saved every tick, so a save can't accidentally capture our own
    /// "freed" state instead of the real original. <see cref="Enforce"/> is cheap and safe to call every tick
    /// while held: the native loading flow can re-lock the cursor out from under us mid-display, so the freed
    /// state has to be re-asserted, not just set once.
    /// </summary>
    internal static class RunStatsCursorControl
    {
        private static bool _held;
        private static bool _savedVisible;
        private static CursorLockMode _savedLockState;

        public static void Acquire()
        {
            if (_held) return;
            _savedVisible = Cursor.visible;
            _savedLockState = Cursor.lockState;
            _held = true;
            Enforce();
        }

        public static void Enforce()
        {
            if (!_held) return;
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }

        /// <summary>Idempotent — safe to call even when not currently held.</summary>
        public static void Release()
        {
            if (!_held) return;
            _held = false;
            Cursor.visible = _savedVisible;
            Cursor.lockState = _savedLockState;
        }
    }
}
