using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using SULFURTogether.Networking;
using SULFURTogether.Networking.Gameplay;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// Phase EM-1 — Endless Mode client slave (host-authoritative world layer). See Docs/EndlessModeSyncPlan.md §4.
    ///
    /// <para>In vanilla, every peer's <c>EndlessModeManager</c> independently drives its own arena, waves, and enemy
    /// spawns. EM-0 (Log439) confirmed both ends load the <b>same arena from the same seed</b>, so a linked client keeps
    /// the arena it already built — but it must stop driving its own <b>waves</b> and <b>enemy spawns</b>, or it fights a
    /// second, unsynchronized Endless run overlaid on the host's. This makes the client a slave:</para>
    /// <list type="bullet">
    /// <item><b>StartEnemySpawning</b> (the per-wave burst coroutine) is skipped — no local enemies, no spurious
    /// Stage/Wave alerts, no local burst-index churn. The host's wave enemies arrive instead through the runtime-spawn
    /// mirror (EM-2: <c>EndlessModeManager</c> is now classified in <see cref="RuntimeSpawnManager.ClassifyOwner"/>), bound
    /// as host-authoritative puppets like DevTools/TriggerSpawn/boss-add spawns.</item>
    /// <item><b>Update</b> (the wave state machine + local XP/card triggers) is skipped — the client's <c>spawnedUnits</c>
    /// list stays empty (mirrored puppets are owned by GameManager, not the manager), which would otherwise read as
    /// 100% killed and drive divergent wave/stage/arena transitions. Host-driven wave/XP state is (re)introduced in EM-3.</item>
    /// </list>
    ///
    /// <para>The host runs everything normally; all suppression is gated on being a <b>linked client</b>
    /// (<c>BossMode == Client &amp;&amp; NetLinkState.ClientLinked</c>) so single-player and the host are untouched. No
    /// arena-selection suppression is needed (seed parity already makes the client pick the same arena — EM-0).</para>
    /// </summary>
    internal static class EndlessSyncPatches
    {
        public static int ClientWaveDriverSkipped;
        public static int ClientSpawnCoroutineSkipped;

        private static bool Enabled { get { try { return Plugin.Cfg.EnableEndlessSync.Value; } catch { return false; } } }
        private static bool LogOn  { get { try { return Plugin.Cfg.LogEndlessSync.Value; } catch { return false; } } }

        /// <summary>True on a client that is linked into a host session — the host owns the Endless world.</summary>
        private static bool IsLinkedClient =>
            NetGameplaySyncBridge.BossMode == NetMode.Client && NetLinkState.ClientLinked;

        public static void Apply(Harmony harmony)
        {
            if (!Enabled) { Plugin.Log.Info("[Endless] EM-1 client slave disabled by config."); return; }
            try
            {
                var emType = AccessTools.TypeByName("EndlessModeManager")
                          ?? AccessTools.TypeByName("PerfectRandom.Sulfur.Core.EndlessModeManager");
                if (emType == null) { Plugin.Log.Info("[Endless] EndlessModeManager type not found — EM-1 client slave disabled."); return; }

                int patched = 0;

                var update = AccessTools.DeclaredMethod(emType, "Update");
                if (update != null)
                {
                    harmony.Patch(update, prefix: new HarmonyMethod(
                        typeof(EndlessSyncPatches).GetMethod(nameof(Update_Pre), BindingFlags.Static | BindingFlags.NonPublic)));
                    patched++;
                }
                else Plugin.Log.Info("[Endless] EndlessModeManager.Update not found — wave-driver suppression disabled.");

                var startSpawning = AccessTools.DeclaredMethod(emType, "StartEnemySpawning");
                if (startSpawning != null)
                {
                    harmony.Patch(startSpawning, prefix: new HarmonyMethod(
                        typeof(EndlessSyncPatches).GetMethod(nameof(StartEnemySpawning_Pre), BindingFlags.Static | BindingFlags.NonPublic)));
                    patched++;
                }
                else Plugin.Log.Info("[Endless] EndlessModeManager.StartEnemySpawning not found — spawn suppression disabled.");

                Plugin.Log.Info($"[Endless] EM-1 client slave patched ({patched}/2). EM-2 mirror via RuntimeSpawnManager.ClassifyOwner(Endless).");
            }
            catch (Exception ex) { Plugin.Log.Error($"[Endless] EM-1 Apply failed: {ex.Message}"); }
        }

        // Driven slave: on a linked client skip the vanilla wave state machine + XP-threshold/card-selection triggers
        // (all host-authoritative), but still render the Endless HUD from the host-synced fields (EM-3). Return false =
        // original Update not run.
        private static bool Update_Pre(object __instance)
        {
            if (!Enabled || !IsLinkedClient) return true;
            ClientWaveDriverSkipped++;
            EndlessSyncManager.ClientRenderUI(__instance); // EM-3: host-driven HUD; no-op until state resolves
            return false;
        }

        // Skip the per-wave burst spawn coroutine on a linked client; hand StartCoroutine a valid (empty) enumerator so
        // the caller doesn't NRE. Enemies come from the host runtime-spawn mirror instead.
        private static bool StartEnemySpawning_Pre(ref IEnumerator __result)
        {
            if (!Enabled || !IsLinkedClient) return true;
            ClientSpawnCoroutineSkipped++;
            if (LogOn) Plugin.Log.Info($"[Endless] EM-1 client suppressed local StartEnemySpawning (#{ClientSpawnCoroutineSkipped}) — mirroring host waves");
            __result = EmptyRoutine();
            return false;
        }

        private static IEnumerator EmptyRoutine() { yield break; }
    }
}
