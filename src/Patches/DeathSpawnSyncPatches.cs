using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SULFURTogether.Networking.Gameplay;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// Phase 5.7-DS: host-authoritative sync of the "spawn a random enemy on death" mutation
    /// (MutationDefinition.unitsToSpawnOnDeath). Confirmed by decompilation of
    /// PerfectRandom.Sulfur.Core.Stats.MutationDefinition:
    ///   - `AddSpawnUnit(Npc)` picks WHICH unit to spawn with the global UnityEngine.Random, so host and client bake
    ///     DIFFERENT units into the dying enemy's onDeath delegate → on death each side spawns a different enemy.
    ///   - `OnDeathSpawnUnitsFunc(Unit, UnitSO, GameObject)` (the onDeath delegate) does the actual spawn through the
    ///     static UnitSO.SpawnUnit. In normal (non-endless) play it has no await before that call, so it runs fully
    ///     synchronously — a prefix/postfix bracket holds across the spawn.
    ///
    /// Fix: the CLIENT skips its local death-spawn (suppresses the divergent unit); the HOST flags the bracket so the
    /// existing runtime-spawn pipeline (RuntimeSpawnManager → NetRuntimeSpawn) broadcasts the spawned unit, which the
    /// client then mirrors + binds like any other host-driven runtime spawn. Reversible via EnableDeathSpawnSync.
    /// </summary>
    internal static class DeathSpawnSyncPatches
    {
        public static void Apply(Harmony harmony)
        {
            if (!Plugin.Cfg.EnableRuntimeSpawnSync.Value || !Plugin.Cfg.EnableDeathSpawnSync.Value)
            {
                Plugin.Log.Info("[DeathSpawn] death-spawn sync disabled by config.");
                return;
            }

            var mutationDef = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.Stats.MutationDefinition");
            if (mutationDef == null) { Plugin.Log.Info("[DeathSpawn] MutationDefinition type not found."); return; }

            var target = AccessTools.GetDeclaredMethods(mutationDef)
                .FirstOrDefault(m => m.Name == "OnDeathSpawnUnitsFunc");
            if (target == null) { Plugin.Log.Info("[DeathSpawn] OnDeathSpawnUnitsFunc not found."); return; }

            try
            {
                harmony.Patch(target,
                    prefix:  new HarmonyMethod(typeof(DeathSpawnSyncPatches).GetMethod(nameof(OnDeathSpawnUnits_Pre), BindingFlags.Static | BindingFlags.NonPublic)),
                    postfix: new HarmonyMethod(typeof(DeathSpawnSyncPatches).GetMethod(nameof(OnDeathSpawnUnits_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                Plugin.Log.Info("[DeathSpawn] Patched MutationDefinition.OnDeathSpawnUnitsFunc (death-spawn sync).");
            }
            catch (Exception ex) { Plugin.Log.Error($"[DeathSpawn] patch OnDeathSpawnUnitsFunc failed: {ex.Message}"); }
        }

        // Returns false to SKIP the original (client suppression). On the host, flags the spawn so the postfix on
        // UnitSO.SpawnUnit broadcasts it.
        private static bool OnDeathSpawnUnits_Pre()
        {
            try
            {
                if (RuntimeSpawnManager.ClientShouldSuppressDeathSpawn())
                    return false; // client: don't spawn the local (divergent) unit; we mirror the host's instead
                RuntimeSpawnManager.BeginHostDeathSpawn();
            }
            catch { }
            return true;
        }

        private static void OnDeathSpawnUnits_Post()
        {
            try { RuntimeSpawnManager.EndHostDeathSpawn(); }
            catch { }
        }
    }
}
