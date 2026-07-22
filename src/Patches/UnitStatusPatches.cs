using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PerfectRandom.Sulfur.Core.Items;
using PerfectRandom.Sulfur.Core.Stats;
using PerfectRandom.Sulfur.Core.Units;
using SULFURTogether.Networking.Gameplay;
using Unity.Collections;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// ST-1 / ST-2 enemy status-effect authority — the two canonical hooks (see <see cref="UnitStatusSyncManager"/>).
    ///
    /// <para><c>Unit.ApplyHitModifiers</c> prefix (ST-1): the single chokepoint where an attack's on-hit status
    /// modifiers are applied — both the projectile path (<c>ProjectileUtilities.ProcessUnitHit</c>) and the melee path
    /// (<c>Hitmesh</c>) route through it. It is a separate call from <c>ReceiveDamage</c>, which is why the existing
    /// client→host damage channel never carried enchantment effects.</para>
    ///
    /// <para><c>Unit.OnStatusUpdated</c> postfix (ST-2): the callback the game itself uses to raise and remove an
    /// effect's presentation, so it is also the authoritative transition point to broadcast from. High-frequency
    /// (every health change, every decay tick) — the manager's first test rejects non-edges.</para>
    ///
    /// <para>The game's <c>FixedList32Bytes&lt;ModifierData&gt;</c> is translated to plain tuples here so the sync
    /// manager stays free of Unity.Collections and game struct types.</para>
    /// </summary>
    internal static class UnitStatusPatches
    {
        public static void Apply(Harmony harmony)
        {
            try
            {
                var applyHitModifiers = AccessTools.DeclaredMethod(typeof(Unit), "ApplyHitModifiers");
                if (applyHitModifiers != null)
                    harmony.Patch(applyHitModifiers, prefix: new HarmonyMethod(
                        typeof(UnitStatusPatches).GetMethod(nameof(ApplyHitModifiers_Pre), BindingFlags.Static | BindingFlags.NonPublic)));
                else
                    Plugin.Log.Error("[UnitStatus] Unit.ApplyHitModifiers not found — client enchantment effects will NOT reach the host.");

                var onStatusUpdated = AccessTools.DeclaredMethod(typeof(Unit), "OnStatusUpdated");
                if (onStatusUpdated != null)
                    harmony.Patch(onStatusUpdated, postfix: new HarmonyMethod(
                        typeof(UnitStatusPatches).GetMethod(nameof(OnStatusUpdated_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                else
                    Plugin.Log.Error("[UnitStatus] Unit.OnStatusUpdated not found — host status effects will NOT be mirrored to clients.");

                Plugin.Log.Info($"[UnitStatus] Patched Unit.ApplyHitModifiers({applyHitModifiers != null})/OnStatusUpdated({onStatusUpdated != null}) (enemy status effect sync).");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[UnitStatus] Apply failed: {ex.Message}");
            }
        }

        private static bool ApplyHitModifiers_Pre(Unit __instance, FixedList32Bytes<ModifierData> modifiersOnHit, Unit attacker)
        {
            try
            {
                int count = modifiersOnHit.Length;
                if (count <= 0) return true;

                var entries = new List<(ushort Attribute, float Value, float ProcChance)>(count);
                for (int i = 0; i < count; i++)
                {
                    ModifierData data = modifiersOnHit[i];
                    entries.Add(((ushort)data.attribute, data.value, data.procChance));
                }

                return !UnitStatusSyncManager.TryInterceptClientPuppetHitModifiers(__instance, entries, attacker);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[UnitStatus] ApplyHitModifiers_Pre failed: {ex.GetType().Name}: {ex.Message}");
                return true; // on our own failure, let the vanilla application run
            }
        }

        private static void OnStatusUpdated_Post(Unit __instance, EntityAttributes id, float prevValue, float newValue)
        {
            UnitStatusSyncManager.ReportHostUnitStatusEdge(__instance, id, prevValue, newValue);
        }
    }
}
