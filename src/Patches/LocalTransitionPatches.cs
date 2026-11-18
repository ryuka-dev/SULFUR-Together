using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PerfectRandom.Sulfur.Core;
using SULFURTogether.Networking;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// JF-1: feeds <see cref="NetLocalTransition"/> from the game's own transition calls. Functional, not diagnostic:
    /// a client defers host-driven follows on it, so these patches stay out of the ReverseProbe set (whose patches
    /// sit behind probe switches) and only observe — they never change an argument or skip the original.
    /// <para>The start is <c>SwitchLevelRoutine</c>, not <c>GoToLevel</c> or <c>CompleteLevel</c>: other prefixes skip
    /// those on a client (the load gate hands a client's own transitions to the host), and a skipped call must not
    /// look like a transition. <c>SwitchLevelRoutine</c> is only called when one really starts, and the postfix
    /// only counts a call whose original produced the routine.</para>
    /// <para>Not covered: the Endless arena swap (it does not go through <c>SwitchLevelRoutine</c>), and the window
    /// of <c>OnCompleteLevelRoutine</c> before it reaches <c>SwitchLevelRoutine</c>.</para>
    /// </summary>
    // Executes on: All
    internal static class LocalTransitionPatches
    {
        public static void Apply(Harmony harmony)
        {
            try
            {
                var t = typeof(GameManager);
                int patched = 0;
                foreach (var m in AccessTools.GetDeclaredMethods(t).Where(m => m.Name == "SwitchLevelRoutine"))
                {
                    harmony.Patch(m, postfix: Hm(nameof(SwitchLevelRoutine_Post)));
                    patched++;
                }
                var setState = AccessTools.DeclaredMethod(t, "SetState", new[] { typeof(GameState) });
                if (setState != null) { harmony.Patch(setState, postfix: Hm(nameof(SetState_Post))); patched++; }
                var awaitStart = AccessTools.DeclaredMethod(t, "SetAwaitBeforeStartLevel", new[] { typeof(bool) });
                if (awaitStart != null) { harmony.Patch(awaitStart, postfix: Hm(nameof(SetAwait_Post))); patched++; }

                if (patched < 3)
                    Plugin.Log.Warn($"[LocalTransition] only {patched} of 3 GameManager hooks found — a client may start a host follow mid-generation");
                else
                    Plugin.Log.Info("[LocalTransition] Patched GameManager transitions (client follows wait for the current level).");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[LocalTransition] Apply failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static HarmonyMethod Hm(string name)
            => new HarmonyMethod(typeof(LocalTransitionPatches).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));

        // A postfix on the iterator method: it returns the routine only when the original ran, so a call another
        // mod's prefix skipped (null result) is not taken for a transition. GoToLevel starts the routine at once.
        private static void SwitchLevelRoutine_Post(object __result)
        {
            try { if (__result != null) NetLocalTransition.Begin("SwitchLevelRoutine"); }
            catch (Exception ex) { Plugin.Log.Error($"[LocalTransition] SwitchLevelRoutine_Post: {ex.Message}"); }
        }

        private static void SetState_Post(GameState state)
        {
            try { NetLocalTransition.OnSetState(state); }
            catch (Exception ex) { Plugin.Log.Error($"[LocalTransition] SetState_Post: {ex.Message}"); }
        }

        private static void SetAwait_Post(bool state)
        {
            try { NetLocalTransition.OnAwaitStartLevel(state); }
            catch (Exception ex) { Plugin.Log.Error($"[LocalTransition] SetAwait_Post: {ex.Message}"); }
        }
    }
}
