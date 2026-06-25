using System;
using System.Reflection;
using HarmonyLib;
using PerfectRandom.Sulfur.Core.Units;
using SULFURTogether.Networking.Gameplay;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// Phase 5.7-BR in-scene destructible (Breakable) sync — capture + registry hooks.
    /// <para><c>Breakable.Start</c> postfix: record the destructible's deterministic spawn position (before any physics
    /// movement) so both ends share a stable cross-peer key.</para>
    /// <para><c>Breakable.Die</c> prefix: when a destructible actually breaks LOCALLY (real bullet / physics / explosion
    /// → ReceiveDamage → Die), broadcast a break event so other peers mirror the EFFECT. A mirrored break sets
    /// <see cref="BreakableBreakManager.IsApplyingMirror"/> so it (and its child / linked cascade) never re-broadcasts.</para>
    /// </summary>
    internal static class BreakablePatches
    {
        public static void Apply(Harmony harmony)
        {
            try
            {
                var breakableType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.Units.Breakable");
                if (breakableType == null)
                {
                    Plugin.Log.Warn("[BreakableBreak] Breakable type not found — destructible sync disabled.");
                    return;
                }

                // Breakable declares its own overrides of Start() and Die() — patch those exact declarations.
                var start = AccessTools.DeclaredMethod(breakableType, "Start");
                if (start != null)
                    harmony.Patch(start, postfix: new HarmonyMethod(
                        typeof(BreakablePatches).GetMethod(nameof(Breakable_Start_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                else
                    Plugin.Log.Warn("[BreakableBreak] Breakable.Start not found — spawn-position registry disabled.");

                var die = AccessTools.DeclaredMethod(breakableType, "Die");
                if (die != null)
                    harmony.Patch(die, prefix: new HarmonyMethod(
                        typeof(BreakablePatches).GetMethod(nameof(Breakable_Die_Pre), BindingFlags.Static | BindingFlags.NonPublic)));
                else
                    Plugin.Log.Warn("[BreakableBreak] Breakable.Die not found — destructible sync disabled.");

                Plugin.Log.Info("[BreakableBreak] Patched Breakable.Start/Die (in-scene destructible sync).");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[BreakableBreak] Apply failed: {ex.Message}");
            }
        }

        private static void Breakable_Start_Post(Breakable __instance)
        {
            if (!Plugin.Cfg.EnableBreakableSync.Value) return;
            BreakableBreakManager.Register(__instance);
        }

        private static void Breakable_Die_Pre(Breakable __instance)
        {
            // Capture FIRST (reads the spawn-position registry), then drop it from the live registry so it can't match again.
            BreakableBreakManager.CaptureLocalBreak(__instance);
            BreakableBreakManager.Unregister(__instance);
        }
    }
}
