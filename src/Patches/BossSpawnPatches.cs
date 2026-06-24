using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using SULFURTogether.Networking.Gameplay.Boss;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// Phase 5.4-E4: hooks the universal unit-spawn chokepoint so the boss dynamic-spawn manifest can capture
    /// boss-owned runtime adds (CousinArm, LuciaEye, ...). Confirmed by decompilation of
    /// PerfectRandom.Sulfur.Core.Units.UnitSO:
    ///   - instance `Task&lt;Unit&gt; SpawnUnitAsync(MonoBehaviour mono, Vector3 pos, Quaternion rot)` (and an altIndex
    ///     overload) — carries the OWNING MonoBehaviour (the boss). We record it as a pending owner.
    ///   - static `Unit SpawnUnit(UnitSO unitSo, GameObject prefab, Vector3 pos, Quaternion rot)` — the single
    ///     synchronous creation point that returns the Unit. We claim the matching pending owner by UnitSO reference.
    /// Read-only: this only observes spawns; it never blocks or alters them.
    /// </summary>
    internal static class BossSpawnPatches
    {
        public static void Apply(Harmony harmony)
        {
            // Phase 5.5-RT1: this UnitSO.SpawnUnit hook now also feeds the runtime spawn sync, so apply it when EITHER
            // the boss dynamic-spawn manifest OR runtime spawn sync is enabled.
            bool bossManifest = Plugin.Cfg.EnableBossEncounterSync.Value && Plugin.Cfg.EnableBossDynamicSpawnManifest.Value;
            bool runtimeSpawn = Plugin.Cfg.EnableRuntimeSpawnSync.Value;
            if (!bossManifest && !runtimeSpawn)
            {
                Plugin.Log.Info("[BossSpawn] dynamic spawn manifest + runtime spawn sync disabled by config.");
                return;
            }

            var unitSO = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.Units.UnitSO");
            if (unitSO == null) { Plugin.Log.Info("[BossSpawn] UnitSO type not found."); return; }

            var asyncPre = new HarmonyMethod(typeof(BossSpawnPatches).GetMethod(nameof(SpawnUnitAsync_Pre), BindingFlags.Static | BindingFlags.NonPublic));
            var spawnPost = new HarmonyMethod(typeof(BossSpawnPatches).GetMethod(nameof(SpawnUnit_Post), BindingFlags.Static | BindingFlags.NonPublic));

            int patched = 0;
            foreach (var mi in AccessTools.GetDeclaredMethods(unitSO).Where(m => m.Name == "SpawnUnitAsync" && !m.IsStatic))
            {
                // First parameter is the owning MonoBehaviour on both overloads.
                var ps = mi.GetParameters();
                if (ps.Length == 0 || ps[0].ParameterType.Name != "MonoBehaviour") continue;
                try { harmony.Patch(mi, prefix: asyncPre); patched++; }
                catch (Exception ex) { Plugin.Log.Error($"[BossSpawn] patch SpawnUnitAsync failed: {ex.Message}"); }
            }

            var spawnUnit = AccessTools.GetDeclaredMethods(unitSO).FirstOrDefault(m => m.Name == "SpawnUnit" && m.IsStatic);
            if (spawnUnit != null)
            {
                try { harmony.Patch(spawnUnit, postfix: spawnPost); patched++; }
                catch (Exception ex) { Plugin.Log.Error($"[BossSpawn] patch SpawnUnit failed: {ex.Message}"); }
            }
            else Plugin.Log.Info("[BossSpawn] static UnitSO.SpawnUnit not found.");

            Plugin.Log.Info($"[BossSpawn] dynamic spawn manifest hooks registered ({patched}).");
        }

        // __instance is the UnitSO; the first arg is the owning MonoBehaviour (boss, DevTools, ...).
        private static void SpawnUnitAsync_Pre(object __instance, MonoBehaviour mono)
        {
            BossDynamicSpawnManifest.NotePendingSpawn(__instance, mono);
            SULFURTogether.Networking.Gameplay.RuntimeSpawnManager.NotePendingSpawn(__instance, mono); // Phase 5.5-RT1
        }

        // unitSo matches the pending owner's UnitSO; __result is the freshly spawned Unit (may be null).
        private static void SpawnUnit_Post(object unitSo, Vector3 position, object? __result)
        {
            if (__result == null) return;
            BossDynamicSpawnManifest.OnUnitSpawned(unitSo, __result, position);
            SULFURTogether.Networking.Gameplay.RuntimeSpawnManager.OnUnitSpawned(unitSo, __result, position); // Phase 5.5-RT1
        }
    }
}
