using System;
using HarmonyLib;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Units;
using SULFURTogether.Networking.Gameplay;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// RM-1: the host tells clients when a live unit is destroyed without dying. <c>GameManager.RemoveNpc</c> is the
    /// game's own bookkeeping for a destroyed NPC (its only caller is <c>Npc.OnDestroy</c>), so it is the one event every
    /// such removal passes through, whoever destroyed the unit. See
    /// <see cref="NetGameplayProbeManager.ReportHostNpcDestroyed"/> for what counts.
    /// </summary>
    internal static class EnemyRemovalPatches
    {
        public static void Apply(Harmony harmony)
        {
            try
            {
                var removeNpc = AccessTools.DeclaredMethod(typeof(GameManager), "RemoveNpc", new[] { typeof(Npc) });
                if (removeNpc == null)
                {
                    Plugin.Log.Warn("[EnemyRemoval] GameManager.RemoveNpc not found — a unit the host destroys without killing stays on clients");
                    return;
                }
                harmony.Patch(removeNpc, prefix: new HarmonyMethod(AccessTools.Method(typeof(EnemyRemovalPatches), nameof(RemoveNpc_Pre))));

                // The moments when everything is destroyed and nothing may be reported: quitting, and going back to the
                // main menu, during which the game stays in its Running state. A level load ends the latter.
                UnityEngine.Application.quitting += NetGameplayProbeManager.NoteApplicationQuitting;
                var goToMainMenu = AccessTools.DeclaredMethod(typeof(GameManager), "GoToMainMenu", new[] { typeof(bool) });
                var setState = AccessTools.DeclaredMethod(typeof(GameManager), "SetState", new[] { typeof(GameState) });
                if (goToMainMenu != null)
                    harmony.Patch(goToMainMenu, prefix: new HarmonyMethod(AccessTools.Method(typeof(EnemyRemovalPatches), nameof(GoToMainMenu_Pre))));
                if (setState != null)
                    harmony.Patch(setState, prefix: new HarmonyMethod(AccessTools.Method(typeof(EnemyRemovalPatches), nameof(SetState_Pre))));
                if (goToMainMenu == null || setState == null)
                    Plugin.Log.Warn($"[EnemyRemoval] GoToMainMenu={goToMainMenu != null} SetState={setState != null} — leaving to the main menu relies on the scene check alone");
                Plugin.Log.Info("[EnemyRemoval] Patched GameManager.RemoveNpc.");
            }
            catch (Exception ex) { Plugin.Log.Error($"[EnemyRemoval] Apply failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static void RemoveNpc_Pre(Npc npc)
        {
            NetGameplayProbeManager.ReportHostNpcDestroyed(npc);
        }

        private static void GoToMainMenu_Pre() => NetGameplayProbeManager.NoteLeavingToMainMenu();

        private static void SetState_Pre(GameState state)
        {
            if (state == GameState.Loading) NetGameplayProbeManager.NoteLevelLoadStarted();
        }
    }
}
