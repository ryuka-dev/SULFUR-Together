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

                // EM-5: per-player XP (host broadcasts each enemy's XP drop) + non-freezing Independent-mode card select.
                var onEnemyDied = AccessTools.DeclaredMethod(emType, "OnEnemyDied");
                if (onEnemyDied != null)
                    harmony.Patch(onEnemyDied, postfix: new HarmonyMethod(
                        typeof(EndlessSyncPatches).GetMethod(nameof(OnEnemyDied_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                else Plugin.Log.Info("[Endless] EndlessModeManager.OnEnemyDied not found — EM-5 XP drop broadcast disabled.");

                var startTransition = AccessTools.DeclaredMethod(emType, "StartTransition");
                if (startTransition != null)
                    harmony.Patch(startTransition, postfix: new HarmonyMethod(
                        typeof(EndlessSyncPatches).GetMethod(nameof(StartTransition_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                else Plugin.Log.Info("[Endless] EndlessModeManager.StartTransition not found — EM-5 no-freeze card select disabled.");

                var cardSpin = AccessTools.DeclaredMethod(emType, "CardSpinComplete");
                if (cardSpin != null)
                    harmony.Patch(cardSpin, postfix: new HarmonyMethod(
                        typeof(EndlessSyncPatches).GetMethod(nameof(CardSpinComplete_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                else Plugin.Log.Info("[Endless] EndlessModeManager.CardSpinComplete not found — EM-5 invuln clear disabled.");

                Plugin.Log.Info($"[Endless] EM-1/EM-5 client slave + progression patched ({patched} core). EM-2 mirror via RuntimeSpawnManager.ClassifyOwner(Endless).");
            }
            catch (Exception ex) { Plugin.Log.Error($"[Endless] EM-1 Apply failed: {ex.Message}"); }
        }

        // Mode-aware client Update.
        //  - SHARED mode: driven slave (EM-3) — skip the vanilla wave state machine + XP-threshold/card triggers (all
        //    host-authoritative) and render the HUD from the host-synced fields.
        //  - INDEPENDENT mode (EM-5): let vanilla Update run so the client drives its OWN XP/leveling/card flow. The
        //    world stays host-authoritative for free — the wave-advance guard `kill% >= 100 && allBurstsSpawnedForCurrentWave`
        //    can never fire on the client because StartEnemySpawning (which alone sets that flag true) is suppressed.
        // Host + single-player always run vanilla.
        private static bool Update_Pre(object __instance)
        {
            if (!Enabled || !IsLinkedClient) return true;
            if (EndlessSyncManager.IsIndependentMode)
            {
                EndlessSyncManager.EnsureInvulnClearedIfNotSelecting(__instance); // safety: drop the bubble once the pick ends
                return true; // run vanilla Update — own progression
            }
            ClientWaveDriverSkipped++;
            EndlessSyncManager.ClientRenderUI(__instance); // EM-3 shared: host-driven HUD; no-op until state resolves
            return false;
        }

        // EM-5 (HOST): the canonical Endless XP source. When an enemy dies, broadcast its BASE XP drop
        // (unitSO.ExperienceOnKill orbs) at the corpse so each client can spawn its own (Independent mode). The melee-XP-
        // bonus doubling is deliberately NOT applied here — it is the host's personal endless card and must not leak to
        // clients; the host's own orbs (vanilla OnEnemyDied, still running) keep the host's bonus.
        private static void OnEnemyDied_Post(object __instance, object npc, object unitSO)
        {
            try
            {
                if (!Enabled || NetGameplaySyncBridge.BossMode != NetMode.Host) return;
                int xpOnKill = ReadInt(unitSO, "ExperienceOnKill");
                if (xpOnKill <= 0) return;
                if (!TryReadCorpsePosition(npc, out UnityEngine.Vector3 pos)) return;
                EndlessSyncManager.HostBroadcastXpDrop(pos, 1, xpOnKill);
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] OnEnemyDied_Post failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        // EM-5 (BOTH ENDS, Independent mode): the vanilla card-selection freezes the whole game (SetTimeScale 0), which
        // in co-op freezes the shared world for the other player too. In Independent mode the world must keep running, so
        // undo the freeze the instant it starts and give the selecting player a brief local invuln bubble instead.
        private static void StartTransition_Post(object newState)
        {
            try
            {
                if (!Enabled || NetGameplaySyncBridge.BossMode == NetMode.Off) return;
                if (!EndlessSyncManager.IsIndependentMode) return;
                if (Convert.ToInt32(newState) != 2 /* TransitionState.CardSelection */) return;
                EndlessSyncManager.UndoCardSelectFreeze();
                EndlessSyncManager.OnIndependentCardSelectOpened();
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] StartTransition_Post failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        // EM-5: the card spin finished (a card was picked) — drop the invuln bubble.
        private static void CardSpinComplete_Post()
        {
            try { EndlessSyncManager.ClearCardSelectInvuln(); } catch { }
        }

        private static int ReadInt(object obj, string member)
        {
            try
            {
                const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var t = obj.GetType();
                var p = t.GetProperty(member, bf); if (p != null) return Convert.ToInt32(p.GetValue(obj));
                var f = t.GetField(member, bf);    if (f != null) return Convert.ToInt32(f.GetValue(obj));
            }
            catch { }
            return 0;
        }

        // Corpse position with the same small upward offset vanilla uses for the orb spawn origin.
        private static bool TryReadCorpsePosition(object npc, out UnityEngine.Vector3 pos)
        {
            pos = default;
            try
            {
                if (npc is UnityEngine.Component c && c != null) { pos = c.transform.position + new UnityEngine.Vector3(0f, 1f, 0f); return true; }
            }
            catch { }
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
