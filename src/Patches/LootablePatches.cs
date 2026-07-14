using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using SULFURTogether.Networking.Gameplay;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// SL-2b (Shared-loot) hooks on <c>PerfectRandom.Sulfur.Gameplay.LootableObject</c> (food / material / scavenge
    /// hatboxes + the cash register — a non-<c>Container</c> loot class). Reflection-only (the mod doesn't reference the
    /// Gameplay assembly). Registers each object by position on <c>Start</c> and routes its <c>Trigger</c> through
    /// <see cref="LootableSyncManager"/> so a client loot makes the host roll it (host-authoritative, Model A) and every
    /// peer replays the animation. Inherited by <c>ChurchCollectionLootable</c>, so it's covered too.
    /// </summary>
    internal static class LootablePatches
    {
        public static void Apply(Harmony harmony)
        {
            try
            {
                var loType = AccessTools.TypeByName("PerfectRandom.Sulfur.Gameplay.LootableObject");
                if (loType == null)
                {
                    Plugin.Log.Warn("[LootableSync] LootableObject type not found — food/material/register sync disabled.");
                    return;
                }

                var start = AccessTools.DeclaredMethod(loType, "Start");
                if (start != null)
                    harmony.Patch(start, postfix: new HarmonyMethod(
                        typeof(LootablePatches).GetMethod(nameof(Start_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                else
                    Plugin.Log.Warn("[LootableSync] LootableObject.Start not found — registry disabled.");

                var trigger = AccessTools.DeclaredMethod(loType, "Trigger");
                if (trigger != null)
                    harmony.Patch(trigger, postfix: new HarmonyMethod(
                        typeof(LootablePatches).GetMethod(nameof(Trigger_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                else
                    Plugin.Log.Warn("[LootableSync] LootableObject.Trigger not found — sync disabled.");

                Plugin.Log.Info("[LootableSync] Patched LootableObject.Start/Trigger (shared-loot food/material/register sync).");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[LootableSync] Apply failed: {ex.Message}");
            }
        }

        private static void Start_Post(object __instance)
        {
            LootableSyncManager.Register(__instance as Component);
        }

        private static void Trigger_Post(object __instance)
        {
            LootableSyncManager.OnLocalTrigger(__instance as Component);
        }
    }
}
