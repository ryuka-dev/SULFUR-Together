using System.Collections.Generic;
using UnityEngine;

namespace SULFURTogether.ReverseProbe
{
    /// <summary>
    /// Tracks CURRENTLY LIVE game objects by instanceId (strings/ints only — no Unity strong refs).
    /// Objects are removed on death/pickup/clear so counts reflect the live state.
    /// All access is on the Unity main thread via probe patch callbacks.
    /// </summary>
    public static class ReverseProbeKnownObjects
    {
        public class UnitInfo
        {
            public string InstanceId   = "";
            public string Name         = "";
            public string TypeCategory = "";  // "Player", "Npc", "Breakable", "Other"
            public float  LastSeenTime;
            public int    SpawnCount;
            public int    DamageCount;
        }

        public class PickupInfo
        {
            public string InstanceId   = "";
            public string ObjectName   = "";
            public string ItemName     = "<unknown>";
            public float  LastSeenTime;
        }

        private static readonly Dictionary<string, UnitInfo>   _players = new Dictionary<string, UnitInfo>();
        private static readonly Dictionary<string, UnitInfo>   _npcs    = new Dictionary<string, UnitInfo>();
        private static readonly Dictionary<string, UnitInfo>   _units   = new Dictionary<string, UnitInfo>();
        private static readonly Dictionary<string, PickupInfo> _pickups = new Dictionary<string, PickupInfo>();

        public static int KnownPlayerCount => _players.Count;
        public static int KnownNpcCount    => _npcs.Count;
        public static int KnownPickupCount => _pickups.Count;
        public static int KnownUnitCount   => _units.Count;

        // ---- registration ----

        public static void RegisterPlayer(string instanceId, string name)
        {
            if (instanceId == null) return;
            if (!_players.TryGetValue(instanceId, out var info))
                _players[instanceId] = info = new UnitInfo { InstanceId = instanceId, TypeCategory = "Player" };
            info.Name = name;
            info.LastSeenTime = Time.realtimeSinceStartup;
            info.SpawnCount++;
        }

        public static void RegisterNpc(string instanceId, string name)
        {
            if (instanceId == null) return;
            if (!_npcs.TryGetValue(instanceId, out var info))
                _npcs[instanceId] = info = new UnitInfo { InstanceId = instanceId, TypeCategory = "Npc" };
            info.Name = name;
            info.LastSeenTime = Time.realtimeSinceStartup;
            info.SpawnCount++;
        }

        public static void RegisterSpawn(string instanceId, string name, string category)
        {
            if (instanceId == null) return;
            if (category == "Player") { RegisterPlayer(instanceId, name); return; }
            if (category == "Npc")    { RegisterNpc(instanceId, name);    return; }

            if (!_units.TryGetValue(instanceId, out var info))
                _units[instanceId] = info = new UnitInfo { InstanceId = instanceId, TypeCategory = category };
            info.Name = name;
            info.LastSeenTime = Time.realtimeSinceStartup;
            info.SpawnCount++;
        }

        public static void RegisterDamage(string instanceId)
        {
            if (instanceId == null) return;
            float now = Time.realtimeSinceStartup;
            if (_players.TryGetValue(instanceId, out var p)) { p.DamageCount++; p.LastSeenTime = now; return; }
            if (_npcs.TryGetValue(instanceId, out var n))    { n.DamageCount++; n.LastSeenTime = now; return; }
            if (_units.TryGetValue(instanceId, out var u))   { u.DamageCount++; u.LastSeenTime = now; }
        }

        /// <summary>Removes the unit from all live dicts. Death delta count stays in ProbeSummary.</summary>
        public static void RegisterDeath(string instanceId)
        {
            if (instanceId == null) return;
            _players.Remove(instanceId);
            _npcs.Remove(instanceId);
            _units.Remove(instanceId);
        }

        public static void RegisterPickupSpawned(string instanceId, string objectName)
        {
            RegisterPickupSpawned(instanceId, objectName, "<unknown>");
        }

        public static void RegisterPickupSpawned(string instanceId, string objectName, string itemName)
        {
            if (instanceId == null) return;
            _pickups[instanceId] = new PickupInfo
            {
                InstanceId   = instanceId,
                ObjectName   = objectName ?? "<unknown>",
                ItemName     = string.IsNullOrWhiteSpace(itemName) ? "<unknown>" : itemName,
                LastSeenTime = Time.realtimeSinceStartup,
            };
        }

        public static bool TryGetPickupItemName(string instanceId, out string itemName)
        {
            itemName = "<unknown>";
            if (instanceId == null) return false;
            if (!_pickups.TryGetValue(instanceId, out var info)) return false;
            itemName = string.IsNullOrWhiteSpace(info.ItemName) ? "<unknown>" : info.ItemName;
            info.LastSeenTime = Time.realtimeSinceStartup;
            return true;
        }

        public static string GetPickupItemNameOrFallback(string instanceId, string fallback)
        {
            return TryGetPickupItemName(instanceId, out var itemName) ? itemName : fallback;
        }

        /// <summary>Removes pickup from live dict (despawned by game).</summary>
        public static void RegisterPickupRemoved(string instanceId)
        {
            if (instanceId == null) return;
            _pickups.Remove(instanceId);
        }

        /// <summary>Removes pickup from live dict (collected by player).</summary>
        public static void RegisterPickupExecuted(string instanceId)
        {
            if (instanceId == null) return;
            _pickups.Remove(instanceId);
        }

        // ---- level lifecycle ----

        /// <summary>
        /// Called on GameManager.ClearLevel. Clears all level-scoped objects so the
        /// next level starts with clean counts. Players re-register on the next AddPlayer.
        /// </summary>
        public static void ClearLevelScopedObjects()
        {
            _players.Clear();
            _npcs.Clear();
            _units.Clear();
            _pickups.Clear();
        }

        /// <summary>Called on LootManager.ClearOnNewLevel — clears only pickup tracking.</summary>
        public static void ClearPickups()
        {
            _pickups.Clear();
        }
    }
}
