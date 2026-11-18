using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using SULFURTogether.Networking.Gameplay;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// Issue #5 / TS-2: intercept one-shot enemy spawners so their spawn becomes host-authoritative instead of
    /// per-machine-local.
    ///
    /// <para><c>PerfectRandom.Sulfur.Core.World.TriggerSpawner.Spawn(GameObject)</c> is the chokepoint every such spawn
    /// goes through: the Caves maze reaches it via <c>Triggerable.Trigger</c>, the Forest ambush events call it directly
    /// from <c>PlayerTrigger.onTriggerEvents</c>. TS-1 hooked <c>Triggerable.Trigger</c> and so missed the ambushes.
    /// The prefix hands off to <see cref="TriggerSpawnSyncManager"/>: on a client it suppresses the local spawn and asks
    /// the host; on the host it de-duplicates (first-trigger-wins) and lets the real spawn run, which the runtime-spawn
    /// mirror then carries to every peer. Solo play falls through to vanilla untouched. The trigger's own effects
    /// (the ambush animation, particles, sound) stay local on every machine.</para>
    /// </summary>
    // Executes on: All (Host dedups, Client redirects)
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

                var spawnerType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.World.TriggerSpawner");
                var spawn = spawnerType != null
                    ? AccessTools.Method(spawnerType, "Spawn", new[] { typeof(GameObject) })
                    : null;

                if (spawn != null)
                {
                    harmony.Patch(spawn, prefix: new HarmonyMethod(
                        typeof(TriggerSpawnPatches).GetMethod(nameof(Spawn_Pre), BindingFlags.Static | BindingFlags.NonPublic)));
                    Plugin.Log.Info("[TriggerSpawn] Patched TriggerSpawner.Spawn (host-authoritative trigger spawns).");
                }
                else
                {
                    Plugin.Log.Warn("[TriggerSpawn] TriggerSpawner.Spawn(GameObject) not found — trigger-spawn sync disabled.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[TriggerSpawn] Apply failed: {ex.Message}");
            }
        }

        // Returns false to suppress the vanilla local spawn (client redirected to host / host de-duplicated).
        private static bool Spawn_Pre(object __instance)
        {
            return TriggerSpawnSyncManager.OnTriggerSpawnerSpawn(__instance);
        }
    }
}
