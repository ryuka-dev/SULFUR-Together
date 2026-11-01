using System;
using System.Reflection;
using HarmonyLib;
using SULFURTogether.Networking;
using SULFURTogether.Networking.Gameplay;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// SL-1 (Shared-loot, Model A: fully host-authoritative loot rolling). When shared loot is active, a CLIENT must
    /// never roll its own loot — the host is the sole roller and every rolled pickup mirrors down through the existing
    /// world-drop channel (<see cref="WorldPickupManager"/> + the <c>ShareAllLoot</c> capture filter). This prefix-blocks
    /// the whole <c>LootManager</c> roll surface on clients: <c>SpawnLootFromKillOrDestroy</c> (enemy kills and
    /// breakables: money/ammo/global; named <c>SpawnGlobalLoot</c> before game 1.0.2) and <c>SpawnLootFrom</c> (override
    /// tables, lootables, projectile <c>lootOnDestroy</c>). All are <c>void</c> and their callers ignore the return, so
    /// blocking is safe (unlike the <c>SpawnPickup</c> chokepoint, whose result callers dereference —
    /// <c>SpawnLootFrom(...).body.AddForce</c>).
    /// <para>Every name in <see cref="RollMethodNames"/> must bind at least one overload. A rename of any one of them
    /// leaves the others patched, so a "nothing patched" check alone stays silent while that roll path runs unsuppressed
    /// on clients — which is exactly how the 1.0.2 rename of <c>SpawnGlobalLoot</c> went unnoticed.</para>
    /// <para>NOT covered here (by design): chest loot goes through <c>Container.SetContainedItem → SpawnPickup</c>
    /// directly and is made host-authoritative separately (SL-2); player drops carry <c>inventoryData</c> and are the WID
    /// base (always synced, never a "roll"); the mirror path calls <c>SpawnPickup</c> directly, not <c>LootManager</c>, so
    /// it is untouched. The host and solo play are never suppressed.</para>
    /// </summary>
    internal static class LootRollSuppressionPatches
    {
        // Public instance methods on LootManager that roll loot. Each is required: see the class summary.
        private static readonly string[] RollMethodNames = { "SpawnLootFrom", "SpawnLootFromKillOrDestroy" };

        public static void Apply(Harmony harmony)
        {
            try
            {
                var lmType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.Items.LootManager");
                if (lmType == null)
                {
                    Plugin.Log.Warn("[SharedLoot] LootManager type not found — client loot-roll suppression disabled.");
                    return;
                }

                var block = new HarmonyMethod(typeof(LootRollSuppressionPatches)
                    .GetMethod(nameof(SuppressClientLootRoll), BindingFlags.Static | BindingFlags.NonPublic));

                var methods = lmType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
                int patched = 0;
                foreach (var name in RollMethodNames)
                {
                    int forName = 0;
                    foreach (var m in methods)
                    {
                        if (m.Name != name) continue;
                        harmony.Patch(m, prefix: block);
                        forName++;
                    }

                    if (forName == 0)
                        Plugin.Log.Warn($"[SharedLoot] LootManager.{name} not found — client rolls through it are NOT suppressed in shared-loot mode.");
                    patched += forName;
                }

                Plugin.Log.Info($"[SharedLoot] Patched {patched} LootManager roll method(s) (client loot-roll suppression).");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[SharedLoot] Apply failed: {ex.Message}");
            }
        }

        // Prefix: return false → skip the vanilla loot roll. Only on a CLIENT, only while shared loot is active.
        private static bool SuppressClientLootRoll(MethodBase __originalMethod)
        {
            try
            {
                if (!NetSessionSettings.SharedLootEnabled) return true;   // Independent mode / solo — vanilla per-peer loot
                if (NetGameplaySyncBridge.IsHost) return true;            // host is the sole authoritative roller

                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[SharedLoot] suppressed a client loot roll via {__originalMethod?.Name}/{__originalMethod?.GetParameters().Length} (host is authoritative)");
                return false;
            }
            catch { return true; } // fail-open: never break vanilla loot on an unexpected error
        }
    }
}
