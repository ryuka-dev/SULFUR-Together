using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using PerfectRandom.Sulfur.Core.Units;
using SULFURTogether.Networking.Gameplay;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// TD-1 hooks on <c>PerfectRandom.Sulfur.Gameplay.DamageTracker</c> — the damage-number display on the 0.18
    /// unlockable target dummy (subscribes to its Unit's <c>onDamageRecieved</c>; each hit flies a single-hit number and
    /// adds to the foot total). Reflection-only (the mod doesn't reference the Gameplay assembly).
    /// <para><c>Start</c> registers the tracker by position — it only runs for a peer that has the dummy UNLOCKED, since
    /// a locked dummy's subtree is <c>SetActive(false)</c>. That is the unlock gate: no registration → relayed numbers
    /// are ignored and nothing is ever revealed.</para>
    /// <para><c>ShowDamage</c> captures each real local hit for the coalesced broadcast; a relayed replay re-enters here
    /// and is filtered by the manager's mirror guard.</para>
    /// </summary>
    internal static class TargetDummyPatches
    {
        public static void Apply(Harmony harmony)
        {
            try
            {
                var dtType = AccessTools.TypeByName("PerfectRandom.Sulfur.Gameplay.DamageTracker");
                if (dtType == null)
                {
                    Plugin.Log.Warn("[TargetDummy] DamageTracker type not found — shared target-dummy numbers disabled.");
                    return;
                }

                var start = AccessTools.DeclaredMethod(dtType, "Start");
                if (start != null)
                    harmony.Patch(start, postfix: new HarmonyMethod(
                        typeof(TargetDummyPatches).GetMethod(nameof(Start_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                else
                    Plugin.Log.Error("[TargetDummy] DamageTracker.Start not found — dummy registry INACTIVE (no shared numbers).");

                var show = AccessTools.DeclaredMethod(dtType, "ShowDamage");
                if (show != null)
                    harmony.Patch(show, postfix: new HarmonyMethod(
                        typeof(TargetDummyPatches).GetMethod(nameof(ShowDamage_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                else
                    Plugin.Log.Error("[TargetDummy] DamageTracker.ShowDamage not found — shared target-dummy numbers INACTIVE.");

                Plugin.Log.Info("[TargetDummy] Patched DamageTracker.Start/ShowDamage (shared target-dummy damage numbers).");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[TargetDummy] Apply failed: {ex.Message}");
            }
        }

        private static void Start_Post(object __instance)
        {
            TargetDummySyncManager.Register(__instance as Component);
        }

        private static void ShowDamage_Post(object __instance, float damage, DamageSourceData sourceData, Vector3? collisionPoint)
        {
            TargetDummySyncManager.OnLocalHit(__instance as Component, damage,
                (byte)sourceData.damageType, sourceData.isCritical, collisionPoint);
        }

    }
}
