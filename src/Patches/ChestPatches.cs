using System;
using System.Reflection;
using HarmonyLib;
using PerfectRandom.Sulfur.Core.World;
using SULFURTogether.Networking.Gameplay;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// SL-2 (Shared-loot) chest hooks on <c>Container</c> (the openable loot chest):
    /// <list type="bullet">
    /// <item><c>Start</c> postfix — register the container by its deterministic world position.</item>
    /// <item><c>OnInteract</c> prefix — on a CLIENT with shared loot on, block the local open (no roll) and ask the host
    /// to open it instead; the host and Independent mode run vanilla.</item>
    /// <item><c>OnInteract</c> postfix — on the HOST, after a real open, broadcast it so every peer plays the open
    /// animation (loot itself mirrors via the shared-loot world-drop channel).</item>
    /// </list>
    /// </summary>
    internal static class ChestPatches
    {
        public static void Apply(Harmony harmony)
        {
            try
            {
                var start = AccessTools.DeclaredMethod(typeof(Container), "Start");
                if (start != null)
                    harmony.Patch(start, postfix: new HarmonyMethod(
                        typeof(ChestPatches).GetMethod(nameof(Start_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                else
                    Plugin.Log.Warn("[ChestSync] Container.Start not found — chest registry disabled.");

                var onInteract = AccessTools.DeclaredMethod(typeof(Container), "OnInteract");
                if (onInteract != null)
                    harmony.Patch(onInteract,
                        prefix:  new HarmonyMethod(typeof(ChestPatches).GetMethod(nameof(OnInteract_Pre),  BindingFlags.Static | BindingFlags.NonPublic)),
                        postfix: new HarmonyMethod(typeof(ChestPatches).GetMethod(nameof(OnInteract_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                else
                    Plugin.Log.Warn("[ChestSync] Container.OnInteract not found — chest open sync disabled.");

                Plugin.Log.Info("[ChestSync] Patched Container.Start/OnInteract (shared-loot chest sync).");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[ChestSync] Apply failed: {ex.Message}");
            }
        }

        private static void Start_Post(Container __instance)
        {
            ChestSyncManager.Register(__instance);
        }

        // Client + shared loot: take ownership of the interaction (block vanilla, ask the host). Otherwise vanilla runs.
        private static bool OnInteract_Pre(Container __instance, ref bool __result)
        {
            if (ChestSyncManager.TryRequestOpen(__instance))
            {
                __result = false; // vanilla "didn't open" — the chest opens when the host's broadcast arrives
                return false;
            }
            return true;
        }

        // Host: a real open just happened (__result true) — broadcast it so peers animate. (BroadcastLocalOpen self-gates
        // on host + shared-loot + not-mirroring.)
        private static void OnInteract_Post(Container __instance, bool __result)
        {
            if (__result)
                ChestSyncManager.BroadcastLocalOpen(__instance);
        }
    }
}
