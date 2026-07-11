using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using SULFURTogether.Networking.Gameplay;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// Issue #5: intercept the one-shot enemy-ambush trigger so its spawn becomes host-authoritative instead of
    /// per-machine-local.
    ///
    /// <para><c>PerfectRandom.Sulfur.Core.World.Triggerable.Trigger(GameObject)</c> is the synchronous chokepoint that
    /// <c>PlayerTrigger</c> calls to fire each of its <c>Triggerable</c>s; a <c>Triggerable</c> that also has a
    /// <c>TriggerSpawner</c> spawns the skeleton(s). The prefix hands off to <see cref="TriggerSpawnSyncManager"/>: on a
    /// client it suppresses the local spawn and asks the host; on the host it de-duplicates (first-trigger-wins) and lets
    /// the real spawn run, which the runtime-spawn mirror then carries to every peer. Non-spawner Triggerables (fog, ...)
    /// and solo play fall through to vanilla untouched.</para>
    /// </summary>
    internal static class TriggerSpawnPatches
    {
        public static void Apply(Harmony harmony)
        {
            try
            {
                if (!Plugin.Cfg.EnableTriggerSpawnSync.Value)
                {
                    Plugin.Log.Info("[TriggerSpawn] trigger-spawn sync disabled by config.");
                    return;
                }

                var triggerableType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.World.Triggerable");
                var trigger = triggerableType != null
                    ? AccessTools.Method(triggerableType, "Trigger", new[] { typeof(GameObject) })
                    : null;

                if (trigger != null)
                {
                    harmony.Patch(trigger, prefix: new HarmonyMethod(
                        typeof(TriggerSpawnPatches).GetMethod(nameof(Trigger_Pre), BindingFlags.Static | BindingFlags.NonPublic)));
                    Plugin.Log.Info("[TriggerSpawn] Patched Triggerable.Trigger (host-authoritative trigger spawns).");
                }
                else
                {
                    Plugin.Log.Warn("[TriggerSpawn] Triggerable.Trigger(GameObject) not found — trigger-spawn sync disabled.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[TriggerSpawn] Apply failed: {ex.Message}");
            }
        }

        // Returns false to suppress the vanilla local spawn (client redirected to host / host de-duplicated).
        private static bool Trigger_Pre(object __instance)
        {
            return TriggerSpawnSyncManager.OnTriggerableTrigger(__instance);
        }
    }
}
