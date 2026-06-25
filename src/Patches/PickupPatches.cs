using System;
using System.Reflection;
using HarmonyLib;
using PerfectRandom.Sulfur.Core;
using SULFURTogether.Networking.Gameplay;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// World item-drop sync hooks on <c>InteractionManager</c> — the single chokepoint for every dynamic world pickup.
    /// <list type="bullet">
    /// <item><c>SpawnPickup</c> postfix: a pickup was created locally → capture + broadcast it (filtered by mode: player
    /// drops only in Independent mode, all pickups in Shared mode). Skipped while we are mirror-spawning a remote one.</item>
    /// <item><c>ExecutePickup</c> prefix: if the pickup is synced, block the local vanilla pickup and route the take
    /// through host arbitration instead (first-come-wins; the winner receives the item, it vanishes everywhere).</item>
    /// <item><c>RemovePickup</c> prefix: drop the pickup from the sync registry (covers AutoPool release / destroy /
    /// pooled reuse).</item>
    /// </list>
    /// </summary>
    internal static class PickupPatches
    {
        public static void Apply(Harmony harmony)
        {
            try
            {
                var imType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.InteractionManager");
                if (imType == null)
                {
                    Plugin.Log.Warn("[WorldPickup] InteractionManager type not found — world item-drop sync disabled.");
                    return;
                }

                var spawn = AccessTools.DeclaredMethod(imType, "SpawnPickup");
                if (spawn != null)
                    harmony.Patch(spawn, postfix: new HarmonyMethod(
                        typeof(PickupPatches).GetMethod(nameof(SpawnPickup_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                else
                    Plugin.Log.Warn("[WorldPickup] InteractionManager.SpawnPickup not found — drop capture disabled.");

                var execute = AccessTools.DeclaredMethod(imType, "ExecutePickup");
                if (execute != null)
                    harmony.Patch(execute, prefix: new HarmonyMethod(
                        typeof(PickupPatches).GetMethod(nameof(ExecutePickup_Pre), BindingFlags.Static | BindingFlags.NonPublic)));
                else
                    Plugin.Log.Warn("[WorldPickup] InteractionManager.ExecutePickup not found — take arbitration disabled.");

                var remove = AccessTools.DeclaredMethod(imType, "RemovePickup");
                if (remove != null)
                    harmony.Patch(remove, prefix: new HarmonyMethod(
                        typeof(PickupPatches).GetMethod(nameof(RemovePickup_Pre), BindingFlags.Static | BindingFlags.NonPublic)));
                else
                    Plugin.Log.Warn("[WorldPickup] InteractionManager.RemovePickup not found — registry cleanup degraded.");

                Plugin.Log.Info("[WorldPickup] Patched InteractionManager.SpawnPickup/ExecutePickup/RemovePickup (world item-drop sync).");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[WorldPickup] Apply failed: {ex.Message}");
            }
        }

        private static void SpawnPickup_Post(Pickup __result)
        {
            if (!Plugin.Cfg.EnableWorldItemDropSync.Value) return;
            if (__result == null) return;
            WorldPickupManager.CaptureLocalSpawn(__result);
        }

        // Block the local pickup of a synced item; the take is host-arbitrated. Returns false → skip original.
        private static bool ExecutePickup_Pre(Pickup pickup, ref bool __result)
        {
            if (!Plugin.Cfg.EnableWorldItemDropSync.Value) return true;
            if (WorldPickupManager.TryBeginTake(pickup))
            {
                __result = false; // vanilla "didn't pick up" — leave it in the world until the host resolves the take
                return false;
            }
            return true;
        }

        private static void RemovePickup_Pre(Pickup pickup)
        {
            WorldPickupManager.UnregisterPickup(pickup);
        }
    }
}
