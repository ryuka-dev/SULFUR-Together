using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SULFURTogether.ReverseProbe
{
    /// <summary>
    /// Accumulates probe hit counts and emits periodic summaries.
    /// Call Tick() from Plugin.Update(). All counters are on the Unity main thread.
    /// Two summary tracks:
    ///   - ProbeSummary: 30s rolling counters for all probe categories
    ///   - PickupSummary / LootSummary: 5s burst windows for high-frequency events
    /// </summary>
    public static class ReverseProbeSummary
    {
        private static float _lastSummaryTime = -9999f;

        // ---- delta counters — reset on each ProbeSummary flush ----
        private static int _gmEvents;
        private static int _playersAdded;
        private static int _unitSpawns;
        private static int _npcSpawns;
        private static int _inventorySetups;
        private static int _pickupExecutes;
        private static int _pickupSpawns;
        private static int _lootSpawns;
        private static int _levelGenEvents;

        // damage/death by category
        private static int _unitDamageCount;
        private static int _npcDamageCount;
        private static int _breakableDamageCount;
        private static int _unitDeaths;
        private static int _npcDeaths;
        private static int _breakableDeaths;

        // inventory item method counters (always counted, verbose log gated by config)
        private static int _inventoryGetSerializedCount;
        private static int _inventoryDestroyCount;
        private static int _inventoryTransferCount;
        private static int _inventoryDropCount;
        private static int _inventoryMoveToPlayerCount;

        // ---- pickup burst state ----
        private static float _lastPickupBurstFlush = -9999f;
        private static int _burstPickupSpawnedCount;
        private static int _burstPickupExecutedCount;
        private static int _burstPickupRemovedCount;
        private static readonly Dictionary<string, int> _burstPickupItemCounts = new Dictionary<string, int>();

        // ---- loot burst state ----
        private static float _lastLootBurstFlush = -9999f;
        private static int _burstLootRegisteredCount;
        private static int _burstLootSpawnedCount;
        private static readonly Dictionary<string, int> _burstLootItemCounts = new Dictionary<string, int>();

        // ---- ProbeSummary increment methods ----
        public static void IncrementGmEvent()        => _gmEvents++;
        public static void IncrementPlayerAdded()    => _playersAdded++;
        public static void IncrementUnitSpawn()      => _unitSpawns++;
        public static void IncrementNpcSpawn()       => _npcSpawns++;
        public static void IncrementInventorySetup() => _inventorySetups++;
        public static void IncrementPickupExecute()  => _pickupExecutes++;
        public static void IncrementPickupSpawn()    => _pickupSpawns++;
        public static void IncrementLootSpawn()      => _lootSpawns++;
        public static void IncrementLevelGen()       => _levelGenEvents++;

        public static void IncrementDamage(string category)
        {
            if      (category == "Npc")       _npcDamageCount++;
            else if (category == "Breakable") _breakableDamageCount++;
            else                              _unitDamageCount++;
        }

        public static void IncrementDeath(string category)
        {
            if      (category == "Npc")       _npcDeaths++;
            else if (category == "Breakable") _breakableDeaths++;
            else                              _unitDeaths++;
        }

        public static void IncrementInventoryGetSerialized() => _inventoryGetSerializedCount++;
        public static void IncrementInventoryDestroy()       => _inventoryDestroyCount++;
        public static void IncrementInventoryTransfer()      => _inventoryTransferCount++;
        public static void IncrementInventoryDrop()          => _inventoryDropCount++;
        public static void IncrementInventoryMoveToPlayer()  => _inventoryMoveToPlayerCount++;

        // ---- burst increment methods ----

        public static void AddPickupSpawnedBurst(string itemName)
        {
            _burstPickupSpawnedCount++;
            AddItemName(_burstPickupItemCounts, itemName);
        }

        public static void AddPickupExecutedBurst(string itemName)
        {
            _burstPickupExecutedCount++;
            AddItemName(_burstPickupItemCounts, itemName);
        }

        public static void AddPickupRemovedBurst()
        {
            _burstPickupRemovedCount++;
        }

        public static void AddLootRegisteredBurst(string itemName)
        {
            _burstLootRegisteredCount++;
            AddItemName(_burstLootItemCounts, itemName);
        }

        public static void AddLootSpawnedBurst()
        {
            _burstLootSpawnedCount++;
        }

        private static void AddItemName(Dictionary<string, int> dict, string name)
        {
            if (string.IsNullOrEmpty(name)) name = "<unknown>";
            dict.TryGetValue(name, out int c);
            dict[name] = c + 1;
        }

        // ---- tick ----

        /// <summary>Called every frame from Plugin.Update().</summary>
        public static void Tick()
        {
            if (!Plugin.Cfg.EnableProbeSummary.Value) return;
            float now = Time.realtimeSinceStartup;

            if (now - _lastSummaryTime >= Plugin.Cfg.ProbeSummaryIntervalSeconds.Value)
            {
                _lastSummaryTime = now;
                Flush();
            }

            if (Plugin.Cfg.CompactPickupLogs.Value &&
                now - _lastPickupBurstFlush >= Plugin.Cfg.PickupBurstSummaryIntervalSeconds.Value)
            {
                _lastPickupBurstFlush = now;
                FlushPickupBurst();
            }

            if (Plugin.Cfg.CompactLootLogs.Value &&
                now - _lastLootBurstFlush >= Plugin.Cfg.LootBurstSummaryIntervalSeconds.Value)
            {
                _lastLootBurstFlush = now;
                FlushLootBurst();
            }
        }

        // ---- burst flush ----

        private static void FlushPickupBurst()
        {
            if (_burstPickupSpawnedCount == 0 && _burstPickupExecutedCount == 0 && _burstPickupRemovedCount == 0)
                return;

            string topItems = FormatTopItems(_burstPickupItemCounts);
            Plugin.Log.Info($"[PickupSummary] spawned={_burstPickupSpawnedCount} executed={_burstPickupExecutedCount} removed={_burstPickupRemovedCount} topItems={topItems}");

            _burstPickupSpawnedCount = _burstPickupExecutedCount = _burstPickupRemovedCount = 0;
            _burstPickupItemCounts.Clear();
        }

        private static void FlushLootBurst()
        {
            if (_burstLootRegisteredCount == 0 && _burstLootSpawnedCount == 0) return;

            string topItems = FormatTopItems(_burstLootItemCounts);
            Plugin.Log.Info($"[LootSummary] registered={_burstLootRegisteredCount} spawned={_burstLootSpawnedCount} topItems={topItems}");

            _burstLootRegisteredCount = _burstLootSpawnedCount = 0;
            _burstLootItemCounts.Clear();
        }

        private static string FormatTopItems(Dictionary<string, int> itemCounts, int maxItems = 5)
        {
            if (itemCounts.Count == 0) return "(none)";
            var sorted = new List<KeyValuePair<string, int>>(itemCounts);
            sorted.Sort((a, b) => b.Value.CompareTo(a.Value));
            var sb = new StringBuilder();
            for (int i = 0; i < sorted.Count && i < maxItems; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(sorted[i].Key).Append(" x").Append(sorted[i].Value);
            }
            return sb.ToString();
        }

        // ---- main probe summary flush ----

        private static void Flush()
        {
            bool allZero =
                _gmEvents == 0 && _playersAdded == 0 && _unitSpawns == 0 && _npcSpawns == 0 &&
                _inventorySetups == 0 && _pickupExecutes == 0 && _pickupSpawns == 0 &&
                _lootSpawns == 0 && _levelGenEvents == 0 &&
                _unitDamageCount == 0 && _npcDamageCount == 0 && _breakableDamageCount == 0 &&
                _unitDeaths == 0 && _npcDeaths == 0 && _breakableDeaths == 0 &&
                _inventoryGetSerializedCount == 0 && _inventoryDestroyCount == 0 &&
                _inventoryTransferCount == 0 && _inventoryDropCount == 0 && _inventoryMoveToPlayerCount == 0;

            if (allZero && Plugin.Cfg.SuppressEmptyProbeSummary.Value) return;

            float interval = Plugin.Cfg.ProbeSummaryIntervalSeconds.Value;
            var   log      = Plugin.Log;

            log.Info($"[ProbeSummary] ── {interval:F0}s snapshot ──────────────────────");
            log.Info($"[ProbeSummary]  GameManager events : {_gmEvents}");
            log.Info($"[ProbeSummary]  Players added      : {_playersAdded}");
            log.Info($"[ProbeSummary]  Unit spawns        : {_unitSpawns}   Npc spawns: {_npcSpawns}");
            log.Info($"[ProbeSummary]  InventoryItem setup: {_inventorySetups}");
            log.Info($"[ProbeSummary]  Pickup spawned     : {_pickupSpawns}   Executed: {_pickupExecutes}");
            log.Info($"[ProbeSummary]  Loot spawns        : {_lootSpawns}");
            log.Info($"[ProbeSummary]  LevelGen events    : {_levelGenEvents}");
            log.Info($"[ProbeSummary]  Damage: unit={_unitDamageCount} npc={_npcDamageCount} breakable={_breakableDamageCount}");
            log.Info($"[ProbeSummary]  Deaths: unit={_unitDeaths} npc={_npcDeaths} breakable={_breakableDeaths}");
            log.Info($"[ProbeSummary]  Inventory: serialized={_inventoryGetSerializedCount} destroy={_inventoryDestroyCount} transfer={_inventoryTransferCount} drop={_inventoryDropCount} moveToPlayer={_inventoryMoveToPlayerCount}");
            log.Info($"[ProbeSummary]  Known live: players={ReverseProbeKnownObjects.KnownPlayerCount} npcs={ReverseProbeKnownObjects.KnownNpcCount} units={ReverseProbeKnownObjects.KnownUnitCount} pickups={ReverseProbeKnownObjects.KnownPickupCount}");
            log.Info("[ProbeSummary] ─────────────────────────────────────");

            // reset delta counters (live Known counts are not reset — they're point-in-time)
            _gmEvents = _playersAdded = _unitSpawns = _npcSpawns =
            _inventorySetups = _pickupExecutes = _pickupSpawns = _lootSpawns = _levelGenEvents =
            _unitDamageCount = _npcDamageCount = _breakableDamageCount =
            _unitDeaths = _npcDeaths = _breakableDeaths =
            _inventoryGetSerializedCount = _inventoryDestroyCount = _inventoryTransferCount =
            _inventoryDropCount = _inventoryMoveToPlayerCount = 0;
        }
    }
}
