using System;
using System.Reflection;
using HarmonyLib;
using SULFURTogether.Networking.Gameplay;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// Keeps the host's headless remote-player stand-ins (Phase 5.7-B ghosts, registered into
    /// <c>GameManager.Players</c> so vanilla enemy detection and targeting see remote players) out of vanilla
    /// per-player logic that assumes a fully-built local Player.
    /// <para>Phase 5.7-B relied on <c>enabled = false</c> for this: a ghost carried only the fields the detection
    /// paths read (unit, cameraRoot, visuals), and Unity simply never delivered <c>Update</c> to a disabled
    /// component. The game's manual-update refactor removed <c>Player.Update</c> entirely and replaced it with
    /// <c>Player.ManualUpdate</c>, which <c>ProjectileSystem.Update</c> calls directly while its Burst job runs:</para>
    /// <code>
    /// StaticInstance&lt;BatchedNPCRaycasts&gt;.Instance.ManualUpdate();
    /// List&lt;Player&gt; players = StaticInstance&lt;GameManager&gt;.Instance.Players;
    /// for (int i = 0; i &lt; players.Count; i++) players[i].ManualUpdate();
    /// if (RunWhileWritingInstanceData != null) RunWhileWritingInstanceData();
    /// </code>
    /// <para>A direct call ignores <c>enabled</c>, so every frame a ghost reached <c>ManualUpdate</c> and threw an
    /// NRE on the fields it does not carry (inputReader / cameraRootAnimator). That aborted
    /// <c>ProjectileSystem.Update</c> mid-loop, so <c>RunWhileWritingInstanceData</c> — where
    /// <c>BehaviourTreeCode_Gameplay.ManualUpdate</c> (the enemy behaviour-tree driver), <c>PlayerHUD</c> and
    /// <c>GooEyeManager</c> register — never ran, and <c>_lastJobHandle.Complete()</c> never completed: enemy AI
    /// froze host-side, and clients saw it through their puppets. Host Player.log carried the NRE once per frame
    /// (12485 in one session) while the BepInEx log stayed clean, because BepInEx does not capture Unity's own
    /// exceptions.</para>
    /// <para>So the skip is now explicit rather than a side effect of <c>enabled</c>. Suppressing the ghost while
    /// the host loads (RemotePlayerRegistryManager.IsHostLoading, the ShowLevelNode camera NRE) does not help here:
    /// this fires during ordinary gameplay, which is exactly when the ghost has to exist.</para>
    /// </summary>
    internal static class GhostPlayerPatches
    {
        public static void Apply(Harmony harmony)
        {
            try
            {
                var playerType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.Units.Player");
                if (playerType == null) { Plugin.Log.Warn("[GhostPlayer] Player type not found — ghosts will run vanilla per-player logic"); return; }

                var manualUpdate = AccessTools.Method(playerType, "ManualUpdate");
                if (manualUpdate == null)
                {
                    // Not a probe: without this skip a ghost throws inside ProjectileSystem.Update every frame and
                    // takes the enemy behaviour-tree driver down with it. Loud on purpose — a rename must not pass
                    // as a silent no-op the way AccessTools' null return otherwise would.
                    Plugin.Log.Error("[GhostPlayer] Player.ManualUpdate not found — if the game drives players directly, ghosts will throw every frame and freeze enemy AI");
                    return;
                }

                harmony.Patch(manualUpdate, prefix: new HarmonyMethod(
                    typeof(GhostPlayerPatches).GetMethod(nameof(Player_ManualUpdate_Pre), BindingFlags.Static | BindingFlags.NonPublic)));
                Plugin.Log.Info("[GhostPlayer] patched Player.ManualUpdate (ghost skip)");
            }
            catch (Exception ex) { Plugin.Log.Error($"[GhostPlayer] apply failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static int _skipLogCount;

        /// <summary>Skips vanilla per-player update for a ghost. Real players are untouched, so a lookup that ever
        /// misidentified one would only cost that player their update — never silently swallow a real exception.</summary>
        private static bool Player_ManualUpdate_Pre(object __instance)
        {
            try
            {
                if (!RemotePlayerRegistryManager.IsGhostPlayer(__instance)) return true;
                if (_skipLogCount++ < 3)
                    Plugin.Log.Info("[GhostPlayer] skipping Player.ManualUpdate for a ghost (headless stand-in carries no inputReader/animators)");
                return false;
            }
            catch { return true; } // never block a real player's update because our own check threw
        }
    }
}
