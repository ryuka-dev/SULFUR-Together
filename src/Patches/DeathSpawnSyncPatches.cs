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

            // Phase 5.7-DS2: SpawnMinions (spawnMinionsOnDeath) — N same-type minions via async SpawnUnitAsync.
            if (Plugin.Cfg.EnableMinionSpawnSync.Value)
            {
                var minions = AccessTools.GetDeclaredMethods(mutationDef).FirstOrDefault(m => m.Name == "SpawnMinions");
                if (minions == null) { Plugin.Log.Info("[DeathSpawn] SpawnMinions not found."); return; }
                _spawnMinionsField = AccessTools.Field(mutationDef, "spawnMinionsOnDeath");
                try
                {
                    harmony.Patch(minions,
                        prefix: new HarmonyMethod(typeof(DeathSpawnSyncPatches).GetMethod(nameof(SpawnMinions_Pre), BindingFlags.Static | BindingFlags.NonPublic)));
                    Plugin.Log.Info("[DeathSpawn] Patched MutationDefinition.SpawnMinions (minion-spawn sync).");
                }
                catch (Exception ex) { Plugin.Log.Error($"[DeathSpawn] patch SpawnMinions failed: {ex.Message}"); }
            }
        }

        private static FieldInfo? _spawnMinionsField;       // MutationDefinition.spawnMinionsOnDeath
        private static FieldInfo? _amountToSpawnField;      // SpawnMinionsOnDeathData.amountToSpawn
        private static PropertyInfo? _unitUnitSOProp;       // Unit.unitSO
        private static FieldInfo? _unitUnitSOField;

        // owner = the dying Unit; __instance = the MutationDefinition. Client: skip (suppress local minions). Host: tag the
        // parent UnitSO so the next `amountToSpawn` async spawns of it are broadcast as DeathMinion.
        private static bool SpawnMinions_Pre(object __instance, object owner)
        {
            try
            {
                if (SULFURTogether.Networking.Gameplay.RuntimeSpawnManager.ClientShouldSuppressMinions())
                    return false;

                object? parentUnitSO = ReadUnitSO(owner);
                int count = ReadAmountToSpawn(__instance);
                if (parentUnitSO != null && count > 0)
                    SULFURTogether.Networking.Gameplay.RuntimeSpawnManager.BeginHostMinionContext(parentUnitSO, count);
            }
            catch { }
            return true;
        }

        private static object? ReadUnitSO(object? unit)
        {
            if (unit == null) return null;
            try
            {
                if (_unitUnitSOProp == null && _unitUnitSOField == null)
                {
                    _unitUnitSOProp  = AccessTools.Property(unit.GetType(), "unitSO");
                    if (_unitUnitSOProp == null) _unitUnitSOField = AccessTools.Field(unit.GetType(), "unitSO");
                }
                return _unitUnitSOProp != null ? _unitUnitSOProp.GetValue(unit) : _unitUnitSOField?.GetValue(unit);
            }
            catch { return null; }
        }

        private static int ReadAmountToSpawn(object mutationDef)
        {
            try
            {
                object? data = _spawnMinionsField?.GetValue(mutationDef);
                if (data == null) return 0;
                if (_amountToSpawnField == null) _amountToSpawnField = AccessTools.Field(data.GetType(), "amountToSpawn");
                object? v = _amountToSpawnField?.GetValue(data);
                return v is int n ? n : 0;
            }
            catch { return 0; }
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
