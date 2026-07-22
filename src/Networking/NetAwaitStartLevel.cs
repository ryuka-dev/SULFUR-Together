using System;
using PerfectRandom.Sulfur.Core;

namespace SULFURTogether.Networking
{
    /// <summary>
    /// AWAIT-1: the "level generated, player has not entered yet" state.
    ///
    /// After a level finishes generating, <c>LevelGeneration.ShowLevelNode</c> parks on the press-to-continue
    /// screen (<c>GameManager.SetAwaitBeforeStartLevel(true)</c>) and only then flips the world live:
    /// <c>SetState(Running)</c>, <c>SetTimeScale(1f)</c>, fade out, and enabling every NPC. So a peer sitting on
    /// that screen has a fully generated map but <c>GameState == Loading</c>, <c>timeScale == 0</c> and no active
    /// enemies — and it can stay there indefinitely, because it is waiting on a keypress.
    ///
    /// The mod used to model loading as a single boolean read off <c>GameState</c>, which lumped this window in
    /// with "still generating". That mismatch is what let a host park here and silently defer every client relay
    /// until the client timed out and generated its own divergent level.
    ///
    /// This is a READ-ONLY view of the game's own flag — the game owns the state, we never mirror it. It is
    /// deliberately local-only: every consumer asks about the peer it is running on, so there is nothing to put
    /// on the wire and no protocol change.
    /// </summary>
    internal static class NetAwaitStartLevel
    {
        /// <summary>
        /// True while THIS peer is parked on the press-to-continue screen with a finished level behind it.
        /// Fails open (false) if the GameManager is not reachable, so callers keep their previous behaviour.
        /// </summary>
        public static bool IsLocalAwaitingStartLevel
        {
            get
            {
                try
                {
                    var gm = StaticInstance<GameManager>.Instance;
                    return gm != null && gm.awaitingStartLevel;
                }
                catch { return false; }
            }
        }

        /// <summary>
        /// Diagnostic only: called from the <c>SetAwaitBeforeStartLevel</c> patch so the transition logs show
        /// exactly when a peer enters and leaves the window. Nothing branches on this.
        /// </summary>
        public static void NoteAwaitStateChanged(bool state)
        {
            try
            {
                string scene = NetRunStateBridge.TryGetLocalRunState(out var run) ? run.SceneKey() : "?";
                Plugin.Log.Info($"[AwaitStart] {(state ? "entered" : "left")} press-to-continue scene={scene} role={NetClientLoadGate.CurrentMode}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[AwaitStart] note failed: {ex.Message}"); }
        }
    }
}
