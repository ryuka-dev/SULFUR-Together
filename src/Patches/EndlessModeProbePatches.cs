using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using SULFURTogether.Networking;
using SULFURTogether.Networking.Gameplay;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// Phase EM-0 — Endless Mode seed-parity probe. PURE DIAGNOSTIC: no functional behavior, gated behind
    /// <see cref="CoopConfig.LogEndlessSync"/>.
    ///
    /// <para>The planned host-authoritative Endless sync (see Docs/EndlessModeSyncPlan.md) assumes both ends load the
    /// <b>same arena</b> from the <b>same seed</b>. In vanilla, <c>EndlessModeManager.Awake</c> captures
    /// <c>seed = (uint)GameManager.currentSeed</c>, then selects the arena via <c>gameplayRandom.NextInt(0, arenaPrefabs.Count)</c>
    /// — a pure function of the seed. So if <c>currentSeed</c> matches on both ends (client applies
    /// <c>GlobalSettings.ForceLevelSeed</c> via <see cref="NetLevelSeed"/> when it follows the host in), the chosen arena
    /// is identical for free.</para>
    ///
    /// <para>This probe logs, at the moment the Endless arena loads: the net role (Host/Client/Off), the captured
    /// <c>seed</c>, <c>GlobalSettings.ForceLevelSeed</c> (to see whether the client's forced seed was in effect), the
    /// arena-prefab pool size, and the chosen arena's name. Comparing the host and client log lines confirms (or refutes)
    /// the parity assumption before any behavior is built on it.</para>
    /// </summary>
    internal static class EndlessModeProbePatches
    {
        private static FieldInfo? _seedField;         // EndlessModeManager.seed : uint
        private static FieldInfo? _currentArenaField; // EndlessModeManager.currentArena : GameObject (private)
        private static FieldInfo? _arenaPrefabsField; // EndlessModeManager.arenaPrefabs : List<GameObject> (private)
        private static FieldInfo? _forceLevelSeedField;   // GlobalSettings.ForceLevelSeed : static
        private static bool _forceSeedResolved;

        public static void Apply(Harmony harmony)
        {
            try
            {
                // EndlessModeManager is not under the PerfectRandom.Sulfur.Core namespace (verified in-game: the qualified
                // lookup logs a HarmonyX "could not find type" warning and the bare name resolves it), so try the bare
                // name first to keep the log clean.
                var emType = AccessTools.TypeByName("EndlessModeManager")
                          ?? AccessTools.TypeByName("PerfectRandom.Sulfur.Core.EndlessModeManager");
                if (emType == null)
                {
                    Plugin.Log.Info("[Endless] EndlessModeManager type not found — EM-0 seed probe disabled.");
                    return;
                }

                _seedField         = AccessTools.Field(emType, "seed");
                _currentArenaField = AccessTools.Field(emType, "currentArena");
                _arenaPrefabsField = AccessTools.Field(emType, "arenaPrefabs");

                var awake = AccessTools.DeclaredMethod(emType, "Awake");
                if (awake == null) { Plugin.Log.Info("[Endless] EndlessModeManager.Awake not found — EM-0 seed probe disabled."); return; }

                harmony.Patch(awake, postfix: new HarmonyMethod(
                    typeof(EndlessModeProbePatches).GetMethod(nameof(Awake_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                Plugin.Log.Info("[Endless] EM-0 seed-parity probe patched (EndlessModeManager.Awake).");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[Endless] Apply failed: {ex.Message}");
            }
        }

        // Postfix: fires once when the Endless arena loads, after seed capture + arena selection.
        private static void Awake_Post(object __instance)
        {
            try
            {
                if (!Plugin.Cfg.LogEndlessSync.Value) return;

                string role = NetGameplaySyncBridge.BossMode.ToString();

                uint seed = 0;
                try { if (_seedField?.GetValue(__instance) is uint s) seed = s; } catch { }

                int poolCount = -1;
                try { if (_arenaPrefabsField?.GetValue(__instance) is ICollection c) poolCount = c.Count; } catch { }

                string arenaName = "?";
                try { if (_currentArenaField?.GetValue(__instance) is GameObject go && go != null) arenaName = go.name; } catch { }

                string forceSeed = ReadForceLevelSeed();

                Plugin.Log.Info($"[Endless] EM-0 arena entry: role={role} seed={seed} forceLevelSeed={forceSeed} " +
                                $"arenaPool={poolCount} chosenArena='{arenaName}'  (host & client seed+arena must match)");
            }
            catch (Exception ex) { Plugin.Log.Error($"[Endless.EM0] {ex.Message}"); }
        }

        /// <summary>Read the static <c>GlobalSettings.ForceLevelSeed</c> (int) for the probe; "&lt;unset&gt;" if 0/unavailable.</summary>
        private static string ReadForceLevelSeed()
        {
            try
            {
                if (!_forceSeedResolved)
                {
                    _forceSeedResolved = true;
                    var gs = AccessTools.TypeByName("GlobalSettings")
                          ?? AccessTools.TypeByName("PerfectRandom.Sulfur.Core.GlobalSettings");
                    _forceLevelSeedField = gs != null
                        ? gs.GetField("ForceLevelSeed", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                        : null;
                }
                if (_forceLevelSeedField == null) return "<n/a>";
                object? v = _forceLevelSeedField.GetValue(null);
                if (v == null) return "<null>";
                long lv = Convert.ToInt64(v);
                return lv == 0 ? "<unset>" : lv.ToString();
            }
            catch { return "<err>"; }
        }
    }
}
