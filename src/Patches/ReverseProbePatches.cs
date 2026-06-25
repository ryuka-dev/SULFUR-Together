using HarmonyLib;
using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using SULFURTogether.Config;
using SULFURTogether.Logging;
using SULFURTogether.Networking;
using SULFURTogether.Networking.Gameplay;
using SULFURTogether.ReverseProbe;

namespace SULFURTogether.Patches
{
    internal static class ReverseProbePatches
    {
        // ------------------------------------------------------------------ entry

        public static void Apply(Harmony harmony)
        {
            if (!Cfg.EnableReverseProbe.Value)
            {
                Log.Info("[ReverseProbe] Disabled by config.");
                return;
            }

            ApplyGameManagerPatches(harmony);
            ApplyUnitPatches(harmony);
            ApplyNpcPatches(harmony);
            ApplyAiAgentPatches(harmony);
            ApplyUnitManagerPatches(harmony);
            ApplyLootManagerPatches(harmony);
            ApplyInteractionManagerPatches(harmony);
            ApplyInventoryItemPatches(harmony);
            ApplyWeaponProjectilePatches(harmony);
            ApplyNextLevelTriggerPatches(harmony);
            ApplyInputReaderPatches(harmony);
            ApplyLevelGenPatches(harmony);
            ApplyTeleportDiagPatches(harmony);
            ApplyRemotePlayerTargetProxyPatches(harmony);
            ApplyDownedPlayerUntargetablePatches(harmony);
            ApplyMultiPlayerActivationPatches(harmony);
            ApplyBatchedRaycastSweepPatches(harmony);
            ApplyAutoPoolGetSafetyPatches(harmony);
            ApplyPooledDestroyDiagPatches(harmony);

            Log.Info("[ReverseProbe] All probes registered.");
        }

        // ------------------------------------------------------------------ shortcuts

        private static STLogger   Log => Plugin.Log;
        private static CoopConfig Cfg => Plugin.Cfg;

        private static string F(object? o)        => ReverseProbeFormatter.FormatInstance(o);
        private static string ID(object? o)       => ReverseProbeFormatter.GetInstanceId(o);
        private static string V(Vector3 v)        => ReverseProbeFormatter.FormatVector3(v);
        private static string ItemName(object? o) => ReverseProbeFormatter.FormatItemName(o);

        private static HarmonyMethod Pre(string name) =>
            new HarmonyMethod(typeof(ReverseProbePatches)
                .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));

        private static HarmonyMethod Post(string name) =>
            new HarmonyMethod(typeof(ReverseProbePatches)
                .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));

        private static HarmonyMethod Fin(string name) =>
            new HarmonyMethod(typeof(ReverseProbePatches)
                .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));

        // ------------------------------------------------------------------ infrastructure

        private static Type? FindType(string fullName)
        {
            var t = AccessTools.TypeByName(fullName);
            if (t == null) Log.Warn($"[ReverseProbe] Type not found: {fullName}");
            return t;
        }

        private static void TryPatch(Harmony harmony, Type type, string method,
            HarmonyMethod? prefix, HarmonyMethod? postfix, Type[]? paramTypes = null)
        {
            try
            {
                var mi = paramTypes != null
                    ? AccessTools.Method(type, method, paramTypes)
                    : AccessTools.Method(type, method);
                if (mi == null) { Log.Warn($"[ReverseProbe] Method not found: {type.Name}.{method}"); return; }
                harmony.Patch(mi, prefix: prefix, postfix: postfix);
                Log.Debug($"[ReverseProbe] Patched {type.Name}.{method}");
            }
            catch (Exception ex)
            {
                Log.Error($"[ReverseProbe] Patch failed {type.Name}.{method}: {ex.Message}");
            }
        }

        private static void PatchAllOverloads(Harmony harmony, Type type, string methodName,
            HarmonyMethod? prefix = null, HarmonyMethod? postfix = null)
        {
            var overloads = AccessTools.GetDeclaredMethods(type)
                .Where(m => m.Name == methodName).ToList();

            if (overloads.Count == 0) { Log.Warn($"[ReverseProbe] No overloads: {type.Name}.{methodName}"); return; }

            Log.Info($"[ReverseProbe] {type.Name}.{methodName} — {overloads.Count} overload(s):");
            foreach (var m in overloads)
            {
                var sig = $"({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})";
                Log.Info($"  [{m.GetParameters().Length}] {sig}");
                try { harmony.Patch(m, prefix: prefix, postfix: postfix); }
                catch (Exception ex) { Log.Error($"[ReverseProbe] Overload patch failed {type.Name}.{methodName}{sig}: {ex.Message}"); }
            }
        }

        private static void PatchReceiveDamageOverloads(Harmony harmony, Type type, string prefixName)
        {
            if (!Cfg.EnableDamageProbe.Value) return;
            var overloads = AccessTools.GetDeclaredMethods(type)
                .Where(m => m.Name == "ReceiveDamage").ToList();
            if (overloads.Count == 0) { Log.Warn($"[ReverseProbe] No ReceiveDamage on {type.Name}"); return; }
            Log.Info($"[ReverseProbe] {type.Name}.ReceiveDamage candidates ({overloads.Count}):");
            foreach (var m in overloads)
            {
                var sig = $"({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})";
                Log.Info($"  [{m.GetParameters().Length}] {sig}");
                try { harmony.Patch(m, prefix: Pre(prefixName)); }
                catch (Exception ex) { Log.Error($"[ReverseProbe] ReceiveDamage patch failed {type.Name} {sig}: {ex.Message}"); }
            }
        }

        // helper: derive category from instance type name
        private static string GetUnitCategory(object? instance)
        {
            string tn = instance?.GetType().Name ?? "";
            if (tn.Contains("Breakable")) return "Breakable";
            if (tn == "Npc")             return "Npc";
            if (tn == "Player")          return "Player";
            return "Unit";
        }


        // ==================================================================
        // 1. GameManager
        // ==================================================================

        private static void ApplyGameManagerPatches(Harmony harmony)
        {
            if (!Cfg.EnablePlayerProbe.Value) return;
            var t = FindType("PerfectRandom.Sulfur.Core.GameManager");
            if (t == null) return;

            TryPatch(harmony, t, "AddPlayer",     null,                              Post(nameof(GM_AddPlayer_Post)));
            TryPatch(harmony, t, "AddNpc",        null,                              Post(nameof(GM_AddNpc_Post)));
            TryPatch(harmony, t, "RemoveNpc",     Pre(nameof(GM_RemoveNpc_Pre)),     null);
            TryPatch(harmony, t, "SetState",      Pre(nameof(GM_SetState_Pre)),      null);
            TryPatch(harmony, t, "GoToLevel",     Pre(nameof(GM_GoToLevel_Pre)),     null);
            // Phase 5.4-D-1 P0-B: capture the AUTHORITATIVE level-transition target. SwitchLevelRoutine is the
            // final authority (CompleteLevel / debug "complete level" / necklace return / special boss jumps all
            // route through it), so its target — not the stale generation pending — defines chapter/level.
            PatchAllOverloads(harmony, t, "SwitchLevelRoutine", prefix: Pre(nameof(GM_SwitchLevelRoutine_Pre)));
            // Phase 5.3-J P0-4: capture generation inputs at the START of generation (before this level
            // mutates the used sets). Patched as a prefix on every StartLevelRoutineGraph overload.
            PatchAllOverloads(harmony, t, "StartLevelRoutineGraph", prefix: Pre(nameof(GM_StartLevelRoutineGraph_Pre)));
            TryPatch(harmony, t, "CompleteLevel", Pre(nameof(GM_CompleteLevel_Pre)), null);
            TryPatch(harmony, t, "ClearLevel",    Pre(nameof(GM_ClearLevel_Pre)),    null);
            // Phase 5.6-LK-P3: returning to the actual main menu (separate MainMenu.unity scene) resets the
            // client's 联机状态 so a re-entered save starts unlinked.
            TryPatch(harmony, t, "GoToMainMenu",  Pre(nameof(GM_GoToMainMenu_Pre)),  null);
            TryPatch(harmony, t, "PlayerDied",    Pre(nameof(GM_PlayerDied_Pre)),    null);
        }

        private static void GM_AddPlayer_Post(object __instance, object player)
        {
            try
            {
                ReverseProbeSummary.IncrementPlayerAdded();
                ReverseProbeSummary.IncrementGmEvent();
                NetLevelSeed.ObserveGameManager(__instance, "GameManager.AddPlayer");
                ReverseProbeKnownObjects.RegisterPlayer(ID(player), F(player));
                NetRunStateBridge.ReportLocalPlayerObject(player);
                NetPlayerLifeManager.ReportLocalPlayerObject(player);
                Log.Info($"[GM] AddPlayer >> {F(player)}");
            }
            catch (Exception ex) { Log.Error($"[GM.AddPlayer] {ex.Message}"); }
        }
        private static void GM_AddNpc_Post(object __instance, object npc)
        {
            try
            {
                ReverseProbeSummary.IncrementGmEvent();
                ReverseProbeKnownObjects.RegisterNpc(ID(npc), F(npc));
                Log.Info($"[GM] AddNpc >> {F(npc)}");
            }
            catch (Exception ex) { Log.Error($"[GM.AddNpc] {ex.Message}"); }
        }
        private static void GM_RemoveNpc_Pre(object __instance, object npc)
        {
            try { ReverseProbeSummary.IncrementGmEvent(); Log.Info($"[GM] RemoveNpc << {F(npc)}"); }
            catch (Exception ex) { Log.Error($"[GM.RemoveNpc] {ex.Message}"); }
        }
        private static void GM_SetState_Pre(object __instance, object state)
        {
            try
            {
                ReverseProbeSummary.IncrementGmEvent();
                NetLevelSeed.ObserveGameManager(__instance, "GameManager.SetState");
                string stateName = state?.ToString() ?? "<unknown>";
                NetRunStateBridge.ReportGameState(stateName);
                Log.Info($"[GM] SetState << {stateName}");
            }
            catch (Exception ex) { Log.Error($"[GM.SetState] {ex.Message}"); }
        }
        // Phase 5.3-J P0-1: returns false to BLOCK the original GoToLevel. The Client load gate blocks
        // the first local (wrong-seed) generation and waits for the Host's generation input.
        private static bool GM_GoToLevel_Pre(object __instance, object chapterSO,
            int levelIndex, object loadingMode, string spawnIdentifier)
        {
            try
            {
                string chapterForGate = chapterSO?.ToString() ?? "<unknown>";
                string loadingModeForGate = loadingMode?.ToString() ?? "";
                // Phase 5.6-LK: while linked the client hands this load to the host. Capture the exact original
                // args so the gate can re-invoke a real local GoToLevel if the host never leads (timeout fallback).
                object gm = __instance; object chap = chapterSO; object lm = loadingMode; string sp = spawnIdentifier ?? "";
                System.Action fb = () => InvokeNativeGoToLevel(gm, chap, levelIndex, lm, sp);
                if (NetClientLoadGate.ShouldInterceptGoToLevel(chapterForGate, levelIndex, loadingModeForGate, sp, fb))
                {
                    NetLoadingFade.Show(); // native black fade while the relay round-trips (Phase 5.6-CL)
                    return false; // blocked: local generation suppressed until the host leads
                }
            }
            catch (Exception ex) { Log.Error($"[ClientLoadGate] GoToLevel gate check failed: {ex.Message}"); }

            try
            {
                ReverseProbeSummary.IncrementGmEvent();
                NetLevelSeed.BeginLevelTransition(__instance, "GameManager.GoToLevel");
                NetGameplayProbeManager.ClearLevelScoped("GameManager.GoToLevel");
                SULFURTogether.Networking.Gameplay.BreakableBreakManager.Clear(); // Phase 5.7-BR drop prev level's breakable registry
                string chapterName = chapterSO?.ToString() ?? "<unknown>";
                string loadingModeName = loadingMode?.ToString() ?? "";
                string spawn = spawnIdentifier ?? "";

                // NOTE: ChurchHub (the in-game hub / safe zone) ALWAYS loads with loadingMode=Menu, so "menu" is
                // NOT a main-menu signal — the 联机状态 reset on save re-entry is hooked on GoToMainMenu instead
                // (which loads the separate MainMenu.unity scene). Keying it here wrongly unlinked the client on
                // every hub follow.

                // Phase 5.6-LK-P2 (Type B): latch the host's transition (its own F3/GoToLevel or a relay-led one)
                // so concurrent client relays defer instead of double-generating. Cleared at finalized snapshot.
                if (NetClientLoadGate.CurrentMode == NetMode.Host
                    && loadingModeName.IndexOf("menu", StringComparison.OrdinalIgnoreCase) < 0)
                    NetHostTransitionGuard.Begin("host-GoToLevel");
                NetRunStateBridge.ReportGoToLevel(chapterName, levelIndex, loadingModeName, spawn);
                // 5.4-D-1 P0-B: GoToLevel is an early hint for the transition target (SwitchLevelRoutine overrides).
                NetGenerationInputCapture.CaptureLevelTransition(chapterName, EnumToInt(chapterSO), levelIndex, loadingModeName, spawn, "GoToLevelPrefix");
                Log.Info($"[GM] GoToLevel << chapter={chapterName} level={levelIndex} mode={loadingModeName} spawn={spawn}");

                // Phase 5.3-I task C: snapshot the cross-level "used" exclusion sets at level entry. On the
                // Host this is the pre-generation input for the level it is about to load — exactly what a
                // drifted Client needs to reproduce the same candidate pools. Logged on both Host and Client.
                var usedSets = NetGameManagerUsedSets.CaptureLocal(__instance, chapterName, levelIndex);
                if (usedSets != null && Cfg.LogUsedSetsTrace.Value)
                {
                    Log.Info($"[UsedSetsTrace] GoToLevel chapter={chapterName} level={levelIndex} {usedSets.ToCompactString()}");
                    Log.Info($"[UsedSetsTrace]   usedChunks={NetHostUsedSets.Summary(usedSets.UsedChunksThisRun)}");
                    Log.Info($"[UsedSetsTrace]   usedEventsRun={NetHostUsedSets.Summary(usedSets.UsedEventsThisRun)}");
                    Log.Info($"[UsedSetsTrace]   usedEventsEnv={NetHostUsedSets.Summary(usedSets.UsedEventsThisEnvironment)}");
                }
            }
            catch (Exception ex) { Log.Error($"[GM.GoToLevel] {ex.Message}"); }
            return true; // allow original GoToLevel
        }

        // Phase 5.4-D-1 P0-B: SwitchLevelRoutine is the authoritative transition entry point. The prefix runs
        // when the iterator is created and receives the real (chapterSO, levelIndex, loadingMode, spawn). This
        // captures special boss jumps (e.g. Act_03_EndChurch:0, Act_01_Hedgemaze:0) that never go through GoToLevel.
        private static void GM_SwitchLevelRoutine_Pre(object[] __args)
        {
            try
            {
                if (__args == null || __args.Length == 0) return;
                object? chapterSO = __args[0];
                string chapterName = chapterSO?.ToString() ?? "<unknown>";
                int envId = EnumToInt(chapterSO);

                int levelIndex = -1;
                for (int i = 1; i < __args.Length; i++) { if (__args[i] is int li) { levelIndex = li; break; } }

                string loadingMode = __args.Length > 2 && __args[2] != null ? __args[2]!.ToString() ?? "" : "";
                string spawn = "";
                for (int i = __args.Length - 1; i >= 1; i--) { if (__args[i] is string s) { spawn = s; break; } }

                NetGenerationInputCapture.CaptureLevelTransition(chapterName, envId, levelIndex, loadingMode, spawn, "SwitchLevelRoutinePrefix");

                // Phase 5.6-LK-P2 (Type B): catch-all latch for any host transition reaching generation.
                if (NetClientLoadGate.CurrentMode == NetMode.Host
                    && (loadingMode ?? "").IndexOf("menu", StringComparison.OrdinalIgnoreCase) < 0)
                    NetHostTransitionGuard.Begin("host-SwitchLevelRoutine");
            }
            catch (Exception ex) { Log.Error($"[GM.SwitchLevelRoutine] {ex.Message}"); }
        }

        private static int EnumToInt(object? o)
        {
            try { if (o != null && o.GetType().IsEnum) return Convert.ToInt32(o); } catch { }
            return -1;
        }

        // Phase 5.3-J P0-4: host generation-input snapshot at generation start.
        private static void GM_StartLevelRoutineGraph_Pre(object __instance, object[] __args, MethodBase __originalMethod)
        {
            try
            {
                NetGenerationInputCapture.CaptureFromStartLevelRoutineGraph(__instance, __args, __originalMethod);
            }
            catch (Exception ex) { Log.Error($"[GenerationInputSnapshot] StartLevelRoutineGraph prefix failed: {ex.Message}"); }
        }

        // Phase 5.6-CL: bool prefix. SULFUR advances in-run sub-levels via CompleteLevel → SwitchLevelRoutine
        // (never GoToLevel), so the combat GoToLevel gate never sees an in-run advance. When a JOINED client
        // walks into a NextLevelTrigger we hand the transition to the host (relay) and block the local advance,
        // showing a native loading fade meanwhile, so everyone advances together instead of the client generating
        // its own level and getting yanked back by the host's run state.
        private static bool GM_CompleteLevel_Pre(object __instance)
        {
            try { ReverseProbeSummary.IncrementGmEvent(); Log.Info("[GM] CompleteLevel <<"); }
            catch (Exception ex) { Log.Error($"[GM.CompleteLevel] {ex.Message}"); }

            // Phase 5.6-LK-P2 (Type B): the host's OWN CompleteLevel starts an authoritative transition. Latch it
            // at the earliest point (the Cinematic window) so a client relay arriving mid-completion is deferred
            // instead of triggering a second generation of the same level.
            try { if (NetClientLoadGate.CurrentMode == NetMode.Host) NetHostTransitionGuard.Begin("host-CompleteLevel"); }
            catch (Exception ex) { Log.Error($"[HostTransitionGuard] CompleteLevel begin failed: {ex.Message}"); }

            try
            {
                if (TryComputeNextLevelTarget(__instance, out string chapter, out int level))
                {
                    object gm = __instance;
                    if (NetClientLoadGate.TryBeginClientLevelCompleteRelay(chapter, level, () => InvokeNativeCompleteLevel(gm), out string reason))
                    {
                        NetLoadingFade.Show();
                        Log.Info($"[ClientLoadGate] CompleteLevel intercepted -> host lead requested target={chapter}:{level} ({reason})");
                        return false; // block local advance; the host leads and the gated client follows
                    }
                    else
                    {
                        Log.Info($"[ClientLoadGate] CompleteLevel allowed locally target={chapter}:{level} reason={reason}");
                    }
                }
            }
            catch (Exception ex) { Log.Error($"[ClientLoadGate] CompleteLevel relay check failed: {ex.Message}"); }

            return true; // allow original CompleteLevel
        }

        // Reproduce GameManager.OnCompleteLevelRoutine's target math from the reverse-engineered source so the
        // relay carries the exact chapter:level the local complete would have produced: next sub-level, or the
        // next environment at level 0 on overflow.
        private static bool TryComputeNextLevelTarget(object gm, out string chapter, out int level)
        {
            chapter = ""; level = -1;
            if (gm == null) return false;
            Type t = gm.GetType();
            const BindingFlags f = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            object? env = t.GetProperty("currentEnvironment", f)?.GetValue(gm)
                          ?? t.GetField("currentEnvironment", f)?.GetValue(gm);
            if (env == null) return false;

            object? curObj = t.GetProperty("currentLevelIndex", f)?.GetValue(gm)
                             ?? t.GetField("currentLevelIndex", f)?.GetValue(gm);
            if (curObj == null) return false;
            int curIndex = Convert.ToInt32(curObj);
            if (curIndex < 0) return false;

            // currentEnvironment.levels.Count
            int count = -1;
            object? levels = env.GetType().GetProperty("levels", f)?.GetValue(env)
                             ?? env.GetType().GetField("levels", f)?.GetValue(env);
            if (levels is System.Collections.ICollection col) count = col.Count;

            int next = curIndex + 1;
            if (count >= 0 && next >= count)
            {
                // Environment overflow → next environment, level 0 (GetNextEnvironment returns ChurchHub at act end).
                object? nextEnv = t.GetMethod("GetNextEnvironment", f)?.Invoke(gm, null);
                if (nextEnv == null) return false;
                chapter = nextEnv.ToString();
                level = 0;
            }
            else
            {
                object? id = env.GetType().GetProperty("id", f)?.GetValue(env)
                             ?? env.GetType().GetField("id", f)?.GetValue(env);
                if (id == null) return false;
                chapter = id.ToString();
                level = next;
            }
            return !string.IsNullOrWhiteSpace(chapter);
        }

        // Phase 5.6-CL timeout fallback: run the real local CompleteLevel under the reentry guard so our own
        // prefix does not re-intercept it (host was unresponsive — the client advances on its own).
        private static void InvokeNativeCompleteLevel(object gm)
        {
            try
            {
                var mi = gm.GetType().GetMethod("CompleteLevel", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (mi == null) { Log.Warn("[ClientLoadGate] local CompleteLevel fallback: method not found"); return; }
                NetClientLoadGate.BeginHostDrivenLoad();
                try { mi.Invoke(gm, null); }
                finally { NetClientLoadGate.EndHostDrivenLoad(); }
            }
            catch (Exception ex) { Log.Error($"[ClientLoadGate] local CompleteLevel fallback failed: {ex.Message}"); }
        }

        // Phase 5.6-LK timeout fallback for a client-led GoToLevel: re-invoke the original GoToLevel locally (under
        // the reentry guard so our own prefix does not re-intercept it) when the host never led — the client then
        // reaches its chosen level on its own (host unresponsive; local divergence no longer matters).
        private static void InvokeNativeGoToLevel(object gm, object chapterSO, int levelIndex, object loadingMode, string spawn)
        {
            try
            {
                MethodInfo mi = null;
                foreach (var m in gm.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (m.Name != "GoToLevel") continue;
                    var p = m.GetParameters();
                    if (p.Length == 4 && p[1].ParameterType == typeof(int) && p[3].ParameterType == typeof(string)) { mi = m; break; }
                }
                if (mi == null) { Log.Warn("[ClientLoadGate] local GoToLevel fallback: method not found"); return; }
                NetClientLoadGate.BeginHostDrivenLoad();
                try { mi.Invoke(gm, new object[] { chapterSO, levelIndex, loadingMode, spawn ?? "" }); }
                finally { NetClientLoadGate.EndHostDrivenLoad(); }
            }
            catch (Exception ex) { Log.Error($"[ClientLoadGate] local GoToLevel fallback failed: {ex.Message}"); }
        }
        // Phase 5.6-LK-P3: the real "return to main menu" (loads MainMenu.unity). Reset the client's 联机状态 to
        // its default so re-entering a save afterwards starts unlinked (not yanked to the host).
        private static void GM_GoToMainMenu_Pre(object __instance)
        {
            try
            {
                if (NetClientLoadGate.CurrentMode == NetMode.Client)
                    NetLinkState.ResetClientToDefault("returned-to-main-menu");
            }
            catch (Exception ex) { Log.Error($"[LinkState] GoToMainMenu reset failed: {ex.Message}"); }
        }
        private static void GM_ClearLevel_Pre(object __instance)
        {
            try
            {
                ReverseProbeSummary.IncrementGmEvent();
                NetLevelSeed.ObserveGameManager(__instance, "GameManager.ClearLevel");
                ReverseProbeKnownObjects.ClearLevelScopedObjects();
                NetGameplayProbeManager.ClearLevelScoped("GameManager.ClearLevel");
                SULFURTogether.Networking.Gameplay.BreakableBreakManager.Clear(); // Phase 5.7-BR drop prev level's breakable registry
                NetRunStateBridge.ReportClearLevel();
                Log.Info("[GM] ClearLevel <<");
            }
            catch (Exception ex) { Log.Error($"[GM.ClearLevel] {ex.Message}"); }
        }
        private static void GM_PlayerDied_Pre(object __instance)
        {
            try { ReverseProbeSummary.IncrementGmEvent(); Log.Info("[GM] PlayerDied <<"); }
            catch (Exception ex) { Log.Error($"[GM.PlayerDied] {ex.Message}"); }
        }

        // ==================================================================
        // 2. Unit — noise-filtered spawn
        // ==================================================================

        private static void ApplyUnitPatches(Harmony harmony)
        {
            if (!Cfg.EnableUnitProbe.Value) return;
            var t = FindType("PerfectRandom.Sulfur.Core.Units.Unit");
            if (t == null) return;

            TryPatch(harmony, t, "Spawn",        null,                               Post(nameof(Unit_Spawn_Post)));
            TryPatch(harmony, t, "Die",          Pre(nameof(Unit_Die_Pre)),          null);
            TryPatch(harmony, t, "SpawnLoot",    Pre(nameof(Unit_SpawnLoot_Pre)),    null);
            TryPatch(harmony, t, "SetUnitState", Pre(nameof(Unit_SetUnitState_Pre)), null);
            PatchAllOverloads(harmony, t, "TeleportTo", Pre(nameof(Unit_TeleportTo_Pre)));
            PatchReceiveDamageOverloads(harmony, t, nameof(Unit_ReceiveDamage_Pre));

            // Phase 5.2 P0: postfix reads health AFTER Unit.ReceiveDamage runs (where damage is
            // actually applied to Stats.ModifyStatus) — avoids the field-scan approach that failed.
            // Only Npc category is forwarded; Player damage is filtered inside ProbeManager.
            if (Cfg.EnableDamageProbe.Value)
            {
                var dmgOverloads = AccessTools.GetDeclaredMethods(t)
                    .Where(m => m.Name == "ReceiveDamage").ToList();
                foreach (var m in dmgOverloads)
                {
                    try { harmony.Patch(m, postfix: Post(nameof(Unit_ReceiveDamage_Post))); }
                    catch (Exception ex) { Log.Error($"[ReverseProbe] Unit.ReceiveDamage postfix failed: {ex.Message}"); }
                }
            }
        }

        private static void Unit_Spawn_Post(object __instance)
        {
            try
            {
                ReverseProbeSummary.IncrementUnitSpawn();
                string typeName = __instance?.GetType().Name ?? "null";
                bool isBreakable = typeName.Contains("Breakable");

                string category = isBreakable ? "Breakable"
                                : typeName == "Npc"    ? "Npc"
                                : typeName == "Player" ? "Player"
                                : "Other";
                ReverseProbeKnownObjects.RegisterSpawn(ID(__instance), F(__instance), category);
                NetGameplayProbeManager.ReportSpawn(__instance, "Unit.Spawn", category);
                if (typeName == "Player") NetPlayerLifeManager.ReportLocalPlayerObject(__instance);

                if (isBreakable)
                {
                    if (!Cfg.EnableBreakableSpawnProbe.Value) return;
                }
                else if (typeName != "Npc" && typeName != "Player" && !Cfg.EnableVerboseUnitSpawnProbe.Value)
                {
                    return;
                }

                Log.Info($"[Unit] Spawn >> {F(__instance)}");
            }
            catch (Exception ex) { Log.Error($"[Unit.Spawn] {ex.Message}"); }
        }

        private static bool Unit_Die_Pre(object __instance)
        {
            try
            {
                if (NetPlayerLifeManager.TryBlockLocalPlayerDeath(__instance, "Unit.Die", out var playerLifeDetail))
                {
                    if (Cfg.LogPlayerLifeSync.Value)
                        Log.Info($"[Unit] Player Die intercepted by co-op downed/revive layer: {playerLifeDetail} inst={F(__instance)}");
                    return false;
                }

                string category = GetUnitCategory(__instance);
                ReverseProbeSummary.IncrementDeath(category);
                ReverseProbeKnownObjects.RegisterDeath(ID(__instance));
                NetGameplayProbeManager.ReportDeath(__instance, "Unit.Die", category);
                Log.Info($"[Unit] Die << {F(__instance)}");
            }
            catch (Exception ex) { Log.Error($"[Unit.Die] {ex.Message}"); }
            return true;
        }
        private static void Unit_SpawnLoot_Pre(object __instance)
        {
            try { ReverseProbeSummary.IncrementLootSpawn(); Log.Info($"[Unit] SpawnLoot << {F(__instance)}"); }
            catch (Exception ex) { Log.Error($"[Unit.SpawnLoot] {ex.Message}"); }
        }
        private static void Unit_SetUnitState_Pre(object __instance, object state)
        {
            try { Log.Info($"[Unit] SetUnitState << inst={F(__instance)} state={state}"); }
            catch (Exception ex) { Log.Error($"[Unit.SetUnitState] {ex.Message}"); }
        }
        private static int   _playerTpDiagCount;
        private static float _playerTpDiagWindowStart;
        private static void Unit_TeleportTo_Pre(object __instance, object[] __args)
        {
            try
            {
                // Diag: for the LOCAL PLAYER, capture from/to + a stack trace so the repeated-teleport caller is visible.
                if (Cfg.LogTeleportDiag.Value && NetPlayerLifeManager.IsLocalPlayerUnit(__instance))
                {
                    string from = (__instance is Component ic && ic != null) ? V(ic.transform.position) : "?";
                    string to   = (__args != null && __args.Length > 0 && __args[0] is Vector3 v) ? V(v) : "?";
                    float now = Time.realtimeSinceStartup;
                    if (now - _playerTpDiagWindowStart > 1f) { _playerTpDiagWindowStart = now; _playerTpDiagCount = 0; }
                    _playerTpDiagCount++;
                    // log the first few of each 1s burst (with stack) + a periodic heartbeat — enough to see the loop
                    if (_playerTpDiagCount <= 3 || _playerTpDiagCount % 25 == 0)
                        Log.Info($"[TeleportDiag] PLAYER TeleportTo from={from} to={to} rapidCount/sec={_playerTpDiagCount}\n{new System.Diagnostics.StackTrace(1, true)}");
                    return;
                }
                Log.Info($"[Unit] TeleportTo << {F(__instance)}");
            }
            catch (Exception ex) { Log.Error($"[Unit.TeleportTo] {ex.Message}"); }
        }

        // ---- Diag: TeleportPlayer.DoTeleport — identify what repeatedly triggers the client teleport loop ----
        private static int   _doTpDiagCount;
        private static float _doTpDiagWindowStart;
        private static void TeleportPlayer_DoTeleport_Pre(object __instance)
        {
            try
            {
                if (!Cfg.LogTeleportDiag.Value || __instance == null) return;
                string comp = "?";
                try { if (__instance is Component c && c != null) comp = $"{c.gameObject.name}@{V(c.transform.position)}"; } catch { }
                string dest = "?";
                foreach (var fn in new[] { "destination", "teleportDestination", "target", "destinationTransform", "teleportTo" })
                {
                    try
                    {
                        var d = AccessTools.Field(__instance.GetType(), fn)?.GetValue(__instance);
                        if (d != null) { dest = DescribeTransformish(d); break; }
                    }
                    catch { }
                }
                float now = Time.realtimeSinceStartup;
                if (now - _doTpDiagWindowStart > 1f) { _doTpDiagWindowStart = now; _doTpDiagCount = 0; }
                _doTpDiagCount++;
                if (_doTpDiagCount <= 3 || _doTpDiagCount % 25 == 0)
                    Log.Info($"[TeleportDiag] TeleportPlayer.DoTeleport comp={comp} dest={dest} rapidCount/sec={_doTpDiagCount}\n{new System.Diagnostics.StackTrace(1, true)}");
            }
            catch (Exception ex) { Log.Error($"[TeleportDiag.DoTeleport] {ex.Message}"); }
        }

        private static string DescribeTransformish(object d)
        {
            try
            {
                if (d is Transform tr) return $"{tr.name}@{V(tr.position)}";
                if (d is GameObject g) return $"{g.name}@{V(g.transform.position)}";
                if (d is Component co) return $"{co.GetType().Name}:{co.name}@{V(co.transform.position)}";
                return d.GetType().Name;
            }
            catch { return d?.GetType().Name ?? "null"; }
        }

        private static void ApplyTeleportDiagPatches(Harmony harmony)
        {
            if (!Cfg.LogTeleportDiag.Value) return;
            var t = FindType("PerfectRandom.Sulfur.Core.World.TeleportPlayer");
            if (t == null) return; // type name unverified at runtime — guarded, just skip
            TryPatch(harmony, t, "DoTeleport", Pre(nameof(TeleportPlayer_DoTeleport_Pre)), null);
        }

        // P3-A2: always-applied (functional, not a probe). Skips Unit.SetupBreakableArmor for our minimal target-proxy
        // Units — it NREs on them (no armor components) and aborts Unit.Start(), leaving the proxy non-targetable.
        private static void ApplyRemotePlayerTargetProxyPatches(Harmony harmony)
        {
            var t = FindType("PerfectRandom.Sulfur.Core.Units.Unit");
            if (t == null) return;
            TryPatch(harmony, t, "SetupBreakableArmor", Pre(nameof(Unit_SetupBreakableArmor_Pre)), null);
        }

        private static bool Unit_SetupBreakableArmor_Pre(object __instance)
        {
            // false = skip original for proxy units only; real units run normally.
            return !SULFURTogether.Networking.RemotePlayerTargetProxyManager.IsProxyUnit(__instance);
        }

        // A3.2 (local player): always-applied functional patch (distinct from the probe-gated GetTarget patch). Makes
        // enemies drop THIS machine's player while it is downed — the local/host player is real (no proxy), so the
        // client-proxy removal path doesn't cover it. Nulling GetTarget's result is the single chokepoint all enemy
        // target selection routes through, so they go idle/re-acquire instead of attacking the downed player.
        private static void ApplyDownedPlayerUntargetablePatches(Harmony harmony)
        {
            var t = FindType("PerfectRandom.Sulfur.Core.Units.AI.AiAgent");
            if (t == null) return;
            TryPatch(harmony, t, "GetTarget", null, Post(nameof(AiAgent_GetTarget_HideDowned_Post)));
        }

        private static void AiAgent_GetTarget_HideDowned_Post(ref object __result)
        {
            try
            {
                if (__result == null) return;
                if (!Cfg.HideDownedLocalPlayerFromEnemies.Value) return;
                if (NetPlayerLifeManager.IsDownedLocalPlayerUnit(__result))
                    __result = null;
            }
            catch { }
        }

        // Plan B: the vanilla NpcUpdateManager.LateUpdate wake LOD only checks the host singleton, so NPCs far from
        // the host never activate even with a client standing next to them. A postfix runs a supplementary pass that
        // wakes inactive NPCs near any remote player. Gated by EnableMultiPlayerNpcActivation (default OFF); the patch
        // is always installed but the body no-ops when disabled / no remote players present.
        private static void ApplyMultiPlayerActivationPatches(Harmony harmony)
        {
            var t = FindType("PerfectRandom.Sulfur.Core.NpcUpdateManager");
            if (t == null) return;
            TryPatch(harmony, t, "LateUpdate", null, Post(nameof(NpcUpdateManager_LateUpdate_Post)));
        }

        private static void NpcUpdateManager_LateUpdate_Post()
        {
            try { Networking.Gameplay.RemotePlayerRegistryManager.ActivateNpcsNearRemotePlayers(); }
            catch (Exception ex) { Log.Warn($"[PlayerRegistry] LateUpdate postfix failed: {ex.Message}"); }
        }

        // DEFENSIVE FIX (Phase 5.7-RB): vanilla BatchedNPCRaycasts.Update iterates GameManager.units WITHOUT a
        // null/destroyed guard (units[i].transform.position, line ~392 of the decompiled source) — so a destroyed
        // puppet Unit still referenced in `units` makes it dereference a null Transform → Transform.get_position NRE
        // flooding every frame (LogOutput103: 12×). SetupNpcList/LateUpdate already guard aliveNpcs/npcMapping, but
        // Update's `units` loop does not. A prefix sweeps Unity-null entries out of units (and aliveNpcs for parity)
        // before the native body runs; removing destroyed objects from the live lists is always corrective.
        private static System.Reflection.PropertyInfo? _bnrGmInstance;
        private static System.Reflection.PropertyInfo? _bnrGmUnits;
        private static System.Reflection.PropertyInfo? _bnrGmAliveNpcs;
        private static bool _bnrResolved;
        private static int  _bnrSweepLogCount;

        private static void ApplyBatchedRaycastSweepPatches(Harmony harmony)
        {
            var t = FindType("PerfectRandom.Sulfur.Core.BatchedNPCRaycasts");
            if (t == null) return;
            var upd = AccessTools.Method(t, "Update");
            if (upd == null) { Log.Warn("[NpcListSweep] BatchedNPCRaycasts.Update not found"); return; }
            try { harmony.Patch(upd, prefix: Pre(nameof(BatchedNPCRaycasts_Update_Pre))); }
            catch (Exception ex) { Log.Warn($"[NpcListSweep] patch failed: {ex.Message}"); }
        }

        private static void BatchedNPCRaycasts_Update_Pre()
        {
            try
            {
                if (!Cfg.EnableDestroyedUnitListSweep.Value) return;
                if (!_bnrResolved)
                {
                    _bnrResolved = true;
                    var gmType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.GameManager");
                    if (gmType != null)
                    {
                        _bnrGmInstance  = AccessTools.Property(gmType, "Instance");
                        _bnrGmUnits     = AccessTools.Property(gmType, "units");
                        _bnrGmAliveNpcs = AccessTools.Property(gmType, "aliveNpcs");
                    }
                }
                var gm = _bnrGmInstance?.GetValue(null, null);
                if (gm == null) return;
                int removed = 0;
                removed += SweepDestroyed(_bnrGmUnits?.GetValue(gm, null) as System.Collections.IList);
                removed += SweepDestroyed(_bnrGmAliveNpcs?.GetValue(gm, null) as System.Collections.IList);
                if (removed > 0 && _bnrSweepLogCount++ < 12)
                    Log.Warn($"[NpcListSweep] removed {removed} destroyed Unit(s) from GameManager.units/aliveNpcs before BatchedNPCRaycasts.Update (prevents Transform.get_position NRE).");
            }
            catch { /* never let the guard break the frame */ }
        }

        // Remove Unity-null (destroyed) and plain-null entries from a live unit list, back-to-front.
        private static int SweepDestroyed(System.Collections.IList? list)
        {
            if (list == null) return 0;
            int removed = 0;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var u = list[i] as UnityEngine.Object;
                if (u == null) { list.RemoveAt(i); removed++; } // Unity '==' treats a destroyed object as null
            }
            return removed;
        }

        // DEFENSIVE FIX: AutoPool.PoolData.Get() NREs when the pooled instance it pops was destroyed externally
        // (Component.get_gameObject on a destroyed object). A finalizer catches that and mints a fresh instance via the
        // pool's own createNew factory, so projectiles never "disappear" regardless of what destroyed the pooled object.
        private static System.Reflection.PropertyInfo? _poolDataCreateNewProp;
        private static int _autoPoolSafeLogCount;

        private static void ApplyAutoPoolGetSafetyPatches(Harmony harmony)
        {
            var t = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.AutoPool+PoolData");
            if (t == null) { Log.Warn("[AutoPoolSafe] AutoPool+PoolData type not found"); return; }
            var get = AccessTools.Method(t, "Get");
            if (get == null) { Log.Warn("[AutoPoolSafe] PoolData.Get not found"); return; }
            try { harmony.Patch(get, finalizer: Fin(nameof(PoolData_Get_Finalizer))); }
            catch (Exception ex) { Log.Warn($"[AutoPoolSafe] patch failed: {ex.Message}"); }
        }

        private static Exception? PoolData_Get_Finalizer(object __instance, ref object __result, Exception __exception)
        {
            if (__exception == null) return null; // normal path — leave result untouched
            try
            {
                if (_poolDataCreateNewProp == null)
                    _poolDataCreateNewProp = AccessTools.Property(__instance.GetType(), "createNew");
                if (_poolDataCreateNewProp?.GetValue(__instance) is Delegate createNew)
                {
                    object? fresh = createNew.DynamicInvoke();
                    if (fresh is Component c && c != null)
                    {
                        c.gameObject.SetActive(true);
                        __result = fresh;
                        if (_autoPoolSafeLogCount++ < 12)
                            Log.Warn("[AutoPoolSafe] PoolData.Get hit a destroyed pooled object (AutoPool.ResetPools level-switch race); recovered with a fresh instance (projectile preserved).");
                        return null; // swallow the NRE
                    }
                }
            }
            catch (Exception ex) { Log.Warn($"[AutoPoolSafe] recovery failed: {ex.GetType().Name}: {ex.Message}"); }
            return __exception; // couldn't recover — let it throw as before
        }

        // DIAGNOSTIC: who destroys a still-pooled AutoPooledObject (corrupts AutoPool -> projectile NRE)? Prefix on
        // UnityEngine.Object.Destroy logs a stack trace when the destroyed object (or a child) is an AutoPooledObject
        // still flagged actuallyPooled (the pool clears that flag BEFORE its own Destroy, so true == rogue destroy).
        private static Type? _autoPooledType;
        private static System.Reflection.PropertyInfo? _apActuallyPooledProp;
        private static System.Reflection.FieldInfo? _apComponentTypeField;
        private static System.Reflection.FieldInfo? _apPoolTypeField;
        private static int _poolDestroyDiagCount;

        private static void ApplyPooledDestroyDiagPatches(Harmony harmony)
        {
            var ot = typeof(UnityEngine.Object);
            try
            {
                var m1 = AccessTools.Method(ot, "Destroy", new[] { typeof(UnityEngine.Object) });
                if (m1 != null) harmony.Patch(m1, prefix: Pre(nameof(Object_Destroy_PoolDiag_Pre)));
                var m2 = AccessTools.Method(ot, "Destroy", new[] { typeof(UnityEngine.Object), typeof(float) });
                if (m2 != null) harmony.Patch(m2, prefix: Pre(nameof(Object_Destroy_PoolDiag_Pre)));
            }
            catch (Exception ex) { Log.Warn($"[PoolDestroyDiag] patch failed: {ex.Message}"); }
        }

        private static void Object_Destroy_PoolDiag_Pre(UnityEngine.Object obj)
        {
            try
            {
                if (!Cfg.LogPooledObjectDestroyDiag.Value || _poolDestroyDiagCount > 40) return;
                if (obj == null) return;
                GameObject? go = obj as GameObject ?? (obj as Component)?.gameObject;
                if (go == null) return;

                if (_autoPooledType == null)
                {
                    _autoPooledType = FindType("PerfectRandom.Sulfur.Core.AutoPooledObject");
                    if (_autoPooledType == null) return;
                    _apActuallyPooledProp = AccessTools.Property(_autoPooledType, "actuallyPooled");
                    _apComponentTypeField = AccessTools.Field(_autoPooledType, "componentType");
                    _apPoolTypeField = AccessTools.Field(_autoPooledType, "poolType");
                }

                var comps = go.GetComponentsInChildren(_autoPooledType, true);
                if (comps == null) return;
                foreach (var ap in comps)
                {
                    if (!(_apActuallyPooledProp?.GetValue(ap) is bool pooled) || !pooled) continue;
                    _poolDestroyDiagCount++;
                    string apName = (ap as Component)?.gameObject.name ?? "?";
                    Log.Warn($"[PoolDestroyDiag] ROGUE destroy of pooled obj: destroyedGO='{go.name}' pooledGO='{apName}' poolType={_apPoolTypeField?.GetValue(ap)} compType={_apComponentTypeField?.GetValue(ap)}\nSTACK:\n{Environment.StackTrace}");
                    break;
                }
            }
            catch { }
        }
        private static bool Unit_ReceiveDamage_Pre(object __instance, float damage, object damageType)
        {
            try
            {
                // Phase 5.5-P3-A3: damage dealt to a remote-player target proxy (Host) is routed to the owning client
                // (its real player takes the hit via the existing HostDamageRequest channel). Suppress the proxy's own
                // health loss so it stays alive = persistent aggro; the proxy's life is tied to the client (A3.2 next).
                if (damage > 0f && NetConfig.GetMode() == NetMode.Host
                    && SULFURTogether.Networking.RemotePlayerTargetProxyManager.TryGetProxyPeer(__instance, out var proxyPeer))
                {
                    Vector3 hitPos = (__instance is Component pcx && pcx != null) ? pcx.transform.position : UnityEngine.Vector3.zero;
                    // Forward the real DamageTypes value (enum) so the client applies the correct type instead of None(0),
                    // which Unit.ReceiveDamage rejects. The synthetic ranged-hit path has no real type and passes 0.
                    int damageTypeInt = 0;
                    try { if (damageType != null) damageTypeInt = Convert.ToInt32(damageType); } catch { }
                    NetPlayerLifeManager.ReportHostAuthoritativeEnemyDamage(proxyPeer, damage, "enemy via target proxy", hitPos, damageTypeInt);
                    return false; // suppress local proxy damage
                }

                if (NetPlayerLifeManager.ShouldBlockLocalPlayerDamage(__instance))
                {
                    if (Cfg.LogPlayerLifeSync.Value)
                        Log.Debug($"[Unit] ReceiveDamage suppressed while local player is downed dmg={damage} inst={F(__instance)}");
                    return false;
                }

                // Phase 4.4.0-O: suppress native enemy damage to the local player while inside an
                // authorized HandleMeleeHit call — Host sends real damage via HostDamageRequest.
                if (Cfg.EnableClientEnemyNativeDamageSuppression.Value
                    && NetGameplayProbeManager.IsInClientEnemyNativeDamageSuppression()
                    && NetPlayerLifeManager.IsLocalPlayerUnit(__instance)
                    && NetPlayerLifeManager.HostDamageApplyDepth == 0)
                {
                    NetGameplayProbeManager.CountSuppressedNativeEnemyDamage();
                    Log.Debug($"[Unit] ReceiveDamage suppressed (client native damage suppression) dmg={damage} inst={F(__instance)}");
                    return false;
                }

                string category = GetUnitCategory(__instance);
                ReverseProbeSummary.IncrementDamage(category);
                ReverseProbeKnownObjects.RegisterDamage(ID(__instance));
                NetGameplayProbeManager.ReportDamage(__instance, "Unit.ReceiveDamage", category, damage, damageType);
                Log.Info($"[Unit] ReceiveDamage << {F(__instance)} dmg={damage} type={damageType}");
            }
            catch (Exception ex) { Log.Error($"[Unit.ReceiveDamage] {ex.Message}"); }
            return true;
        }

        // Phase 5.2 P0: fires AFTER damage is committed to Unit.Stats — reliable HP read point.
        // Filters to Npc only so player damage does not trigger health broadcast.
        private static void Unit_ReceiveDamage_Post(object __instance, float damage)
        {
            try
            {
                if (GetUnitCategory(__instance) != "Npc") return;
                NetGameplayProbeManager.ReportHostNpcHealthAfterDamage(__instance, damage);
            }
            catch (Exception ex) { Log.Error($"[Unit.ReceiveDamage.Post] {ex.Message}"); }
        }

        // ==================================================================
        // 3. Npc
        // ==================================================================

        private static void ApplyNpcPatches(Harmony harmony)
        {
            if (!Cfg.EnableNpcProbe.Value) return;
            var t = FindType("PerfectRandom.Sulfur.Core.Units.Npc");
            if (t == null) return;

            TryPatch(harmony, t, "Spawn",                  null,                                Post(nameof(Npc_Spawn_Post)));
            TryPatch(harmony, t, "Die",                    Pre(nameof(Npc_Die_Pre)),             null);
            TryPatch(harmony, t, "TriggerAttackAnimation", Pre(nameof(Npc_AttackAnim_Pre)),      null);
            TryPatch(harmony, t, "TriggerShoot",           Pre(nameof(Npc_TriggerShoot_Pre)),    null);
            TryPatch(harmony, t, "SetShooting",            Pre(nameof(Npc_SetShooting_Pre)),     null);
            TryPatch(harmony, t, "TriggerWeaponManually",  Pre(nameof(Npc_TriggerWeapon_Pre)),   null);
            TryPatch(harmony, t, "SetAimTarget",           Pre(nameof(Npc_SetAimTarget_Pre)),    null);
            TryPatch(harmony, t, "GetAimPosition",         null,                                Post(nameof(Npc_GetAimPosition_Post)));
            TryPatch(harmony, t, "HandleMeleeHit",         Pre(nameof(Npc_MeleeHit_Pre)),        Post(nameof(Npc_MeleeHit_Post)));
            TryPatch(harmony, t, "TriggerShootFromAnimation", Pre(nameof(Npc_TriggerShootFromAnimation_Pre)), null);
            TryPatch(harmony, t, "StartMeleeDamageState",  Pre(nameof(Npc_StartMeleeDamageState_Pre)), null);
            TryPatch(harmony, t, "EndMeleeDamageState",    Pre(nameof(Npc_EndMeleeDamageState_Pre)),   null);
            TryPatch(harmony, t, "SetRangedAttacking",     Pre(nameof(Npc_SetRangedAttacking_Pre)),    null);
            TryPatch(harmony, t, "SetAttacking",           Pre(nameof(Npc_SetAttacking_Pre)),          null);
            TryPatch(harmony, t, "DoneAttacking",          Pre(nameof(Npc_DoneAttacking_Pre)),         null);
            TryPatch(harmony, t, "DoneShooting",           Pre(nameof(Npc_DoneShooting_Pre)),          null);
            TryPatch(harmony, t, "ToggleBehaviourTree",    Pre(nameof(Npc_ToggleBehaviourTree_Pre)), null);
            TryPatch(harmony, t, "ActivateBehaviour",      Pre(nameof(Npc_ActivateBehaviour_Pre)),   null);
            TryPatch(harmony, t, "ActivateBehaviourTree",  Pre(nameof(Npc_ActivateBehaviour_Pre)),   null);
            TryPatch(harmony, t, "MoveToPlayer",           Pre(nameof(Npc_PuppetBlockMovement_Pre)),  null);
            TryPatch(harmony, t, "MoveToSpawnPosition",    Pre(nameof(Npc_PuppetBlockMovement_Pre)),  null);
            TryPatch(harmony, t, "SetForcedDestination",   Pre(nameof(Npc_PuppetBlockMovement_Pre)),  null);
            TryPatch(harmony, t, "PerformLunge",           Pre(nameof(Npc_PuppetBlockMovement_Pre)),  null);
            TryPatch(harmony, t, "JumpApplyForce",         Pre(nameof(Npc_PuppetBlockMovement_Pre)),  null);
            // Update: always patched; method body gates by EnableNpcUpdateProbe
            TryPatch(harmony, t, "Update",                 null,                                Post(nameof(Npc_Update_Post)));

            PatchReceiveDamageOverloads(harmony, t, nameof(Npc_ReceiveDamage_Pre));
            // Phase 5.2: health reading moved to Unit.ReceiveDamage postfix (see ApplyUnitPatches).
            // Npc.ReceiveDamage postfix removed to avoid double-send on each damage event.
        }

        private static void Npc_Spawn_Post(object __instance)
        {
            try
            {
                ReverseProbeSummary.IncrementNpcSpawn();
                ReverseProbeKnownObjects.RegisterSpawn(ID(__instance), F(__instance), "Npc");
                NetGameplayProbeManager.ReportSpawn(__instance, "Npc.Spawn", "Npc");
                Log.Info($"[Npc] Spawn >> {F(__instance)}");
            }
            catch (Exception ex) { Log.Error($"[Npc.Spawn] {ex.Message}"); }
        }
        private static void Npc_Die_Pre(object __instance)
        {
            try
            {
                ReverseProbeSummary.IncrementDeath("Npc");
                ReverseProbeKnownObjects.RegisterDeath(ID(__instance));
                NetGameplayProbeManager.ReportDeath(__instance, "Npc.Die", "Npc");
                Log.Info($"[Npc] Die << {F(__instance)}");
            }
            catch (Exception ex) { Log.Error($"[Npc.Die] {ex.Message}"); }
        }
        private static bool Npc_AttackAnim_Pre(object __instance)
        {
            try
            {
                if (NetGameplayProbeManager.ShouldBlockClientEnemyPuppetNpcCombat(__instance, "Npc.TriggerAttackAnimation", out _))
                    return false;
                NetGameplayProbeManager.ReportEnemyCombatProbe(__instance, "Npc.TriggerAttackAnimation");
                Log.Info($"[Npc] TriggerAttackAnimation << {F(__instance)}");
            }
            catch (Exception ex) { Log.Error($"[Npc.TriggerAttackAnimation] {ex.Message}"); }
            return true;
        }
        private static bool Npc_TriggerShoot_Pre(object __instance)
        {
            try
            {
                if (NetGameplayProbeManager.ShouldBlockClientEnemyPuppetNpcCombat(__instance, "Npc.TriggerShoot", out _))
                    return false;
                NetGameplayProbeManager.ReportEnemyCombatProbe(__instance, "Npc.TriggerShoot");
                if (Cfg.LogEnemyCombatProbe.Value) Log.Info($"[Npc] TriggerShoot << {F(__instance)}");
            }
            catch (Exception ex) { Log.Error($"[Npc.TriggerShoot] {ex.Message}"); }
            return true;
        }
        private static bool Npc_SetShooting_Pre(object __instance, bool state)
        {
            try
            {
                if (state && NetGameplayProbeManager.ShouldBlockClientEnemyPuppetNpcCombat(__instance, "Npc.SetShooting", out _))
                    return false;
                if (state)
                    NetGameplayProbeManager.ReportEnemyCombatProbe(__instance, "Npc.SetShooting", $"state={state}");
                if (Cfg.LogEnemyCombatProbe.Value) Log.Info($"[Npc] SetShooting << {F(__instance)} state={state}");
            }
            catch (Exception ex) { Log.Error($"[Npc.SetShooting] {ex.Message}"); }
            return true;
        }
        private static bool Npc_TriggerWeapon_Pre(object __instance, int state)
        {
            try
            {
                if (NetGameplayProbeManager.ShouldBlockClientEnemyPuppetNpcCombat(__instance, "Npc.TriggerWeaponManually", out _))
                    return false;
                NetGameplayProbeManager.ReportEnemyCombatProbe(__instance, "Npc.TriggerWeaponManually", $"state={state}");
                if (Cfg.LogEnemyCombatProbe.Value) Log.Info($"[Npc] TriggerWeaponManually << {F(__instance)} state={state}");
            }
            catch (Exception ex) { Log.Error($"[Npc.TriggerWeaponManually] {ex.Message}"); }
            return true;
        }
        private static bool Npc_SetAimTarget_Pre(object __instance, object target)
        {
            try
            {
                if (NetGameplayProbeManager.ShouldBlockClientEnemyPuppetNpcTargeting(__instance, target, "Npc.SetAimTarget", out _))
                    return false;
                if (Cfg.LogEnemyCombatProbe.Value) Log.Info($"[Npc] SetAimTarget << {F(__instance)} target={F(target)}");
            }
            catch (Exception ex) { Log.Error($"[Npc.SetAimTarget] {ex.Message}"); }
            return true;
        }

        // Phase 5.5-P3: redirect a Client puppet's native projectile aim to the Host-authoritative aim
        // point. Weapon.DispatchProjectile re-aims through Npc.GetAimPosition() (target root = feet) when
        // the replayed TriggerShoot fires; overriding the result makes the native arrow fly toward the
        // real target (head/camera). No-op for non-puppet NPCs and on the host.
        private static void Npc_GetAimPosition_Post(object __instance, ref Vector3 __result)
        {
            try
            {
                if (NetGameplayProbeManager.TryGetClientPuppetAimOverride(__instance, out var aim))
                    __result = aim;
            }
            catch (Exception ex) { Log.Error($"[Npc.GetAimPosition/AimOverride] {ex.Message}"); }
        }

        private static bool Npc_ToggleBehaviourTree_Pre(object __instance, bool state)
        {
            try
            {
                if (NetGameplayProbeManager.ShouldBlockClientEnemyPuppetBehaviourTreeActivation(__instance, state))
                    return false;
            }
            catch (Exception ex) { Log.Error($"[Npc.ToggleBehaviourTree/Puppet] {ex.Message}"); }
            return true;
        }

        private static bool Npc_ActivateBehaviour_Pre(object __instance)
        {
            try
            {
                if (NetGameplayProbeManager.ShouldBlockClientEnemyPuppetNpcMovement(__instance))
                    return false;
            }
            catch (Exception ex) { Log.Error($"[Npc.ActivateBehaviour/Puppet] {ex.Message}"); }
            return true;
        }

        private static bool Npc_PuppetBlockMovement_Pre(object __instance, MethodBase __originalMethod, object[] __args)
        {
            try
            {
                string source = __originalMethod == null ? "Npc.Movement" : "Npc." + __originalMethod.Name;
                NetGameplayProbeManager.ReportEnemyAiIntent(__instance, source, __args);
                if (NetGameplayProbeManager.ShouldBlockClientEnemyPuppetNpcMovement(__instance))
                    return false;
            }
            catch (Exception ex) { Log.Error($"[Npc.PuppetBlockMovement] {ex.Message}"); }
            return true;
        }

        // HandleMeleeHit — off by default, per-NPC throttled.
        // Phase 4.4.0-O: in an authorized window we allow the call through for visual animation but
        // enter native damage suppression depth so Unit.ReceiveDamage is suppressed for the local player.
        // __state tracks whether THIS invocation entered suppression so the postfix exits exactly once.
        private static bool Npc_MeleeHit_Pre(object __instance, ref bool __state)
        {
            __state = false;
            try
            {
                // Phase 5.0 P0: when host-driven proxy is active, always allow HandleMeleeHit to
                // run for visual animation, but suppress all resulting damage to the local player.
                // This replaces the old authorized-window approach for melee animation visibility.
                if (Cfg.EnableHostDrivenEnemyProxy.Value
                    && Cfg.SuppressAllClientPuppetDamage.Value
                    && NetGameplayProbeManager.IsClientEnemyPuppetNpc(__instance))
                {
                    NetGameplayProbeManager.EnterClientEnemyNativeDamageSuppression();
                    __state = true;
                    NetGameplayProbeManager.ReportEnemyCombatProbe(__instance, "Npc.HandleMeleeHit");
                    return true; // allow: visual animation runs; damage blocked by ReceiveDamage suppression
                }

                if (NetGameplayProbeManager.ShouldBlockClientEnemyPuppetNpcCombat(__instance, "Npc.HandleMeleeHit", out _))
                    return false;
                NetGameplayProbeManager.ReportEnemyCombatProbe(__instance, "Npc.HandleMeleeHit");
                // Only suppress for puppet NPCs — non-puppet NPCs deal damage natively and must not be blocked.
                if (Cfg.EnableClientEnemyNativeDamageSuppression.Value
                    && NetGameplayProbeManager.IsClientEnemyPuppetNpc(__instance))
                {
                    NetGameplayProbeManager.EnterClientEnemyNativeDamageSuppression();
                    __state = true;
                }
                if (!Cfg.EnableNpcMeleeProbe.Value) return true;
                string key = $"MeleeHit_{ID(__instance)}";
                if (!ReverseProbeState.ShouldLog(key, Cfg.ProbeThrottleSeconds.Value, Cfg.MaxRepeatedLogPerKey.Value)) return true;
                Log.Info($"[Npc] HandleMeleeHit << {F(__instance)}");
            }
            catch (Exception ex) { Log.Error($"[Npc.HandleMeleeHit] {ex.Message}"); }
            return true;
        }

        private static void Npc_MeleeHit_Post(bool __state)
        {
            try
            {
                if (__state)
                    NetGameplayProbeManager.ExitClientEnemyNativeDamageSuppression();
            }
            catch { }
        }

        // Phase 4.4.0-O: new Npc method probes — all route through the same combat gating.
        private static bool Npc_TriggerShootFromAnimation_Pre(object __instance)
        {
            try
            {
                if (NetGameplayProbeManager.ShouldBlockClientEnemyPuppetNpcCombat(__instance, "Npc.TriggerShootFromAnimation", out _))
                    return false;
                NetGameplayProbeManager.ReportEnemyCombatProbe(__instance, "Npc.TriggerShootFromAnimation");
            }
            catch (Exception ex) { Log.Error($"[Npc.TriggerShootFromAnimation] {ex.Message}"); }
            return true;
        }

        private static bool Npc_StartMeleeDamageState_Pre(object __instance)
        {
            try
            {
                if (NetGameplayProbeManager.ShouldBlockClientEnemyPuppetNpcCombat(__instance, "Npc.StartMeleeDamageState", out _))
                    return false;
                NetGameplayProbeManager.ReportEnemyCombatProbe(__instance, "Npc.StartMeleeDamageState");
            }
            catch (Exception ex) { Log.Error($"[Npc.StartMeleeDamageState] {ex.Message}"); }
            return true;
        }

        private static bool Npc_EndMeleeDamageState_Pre(object __instance)
        {
            try
            {
                if (NetGameplayProbeManager.ShouldBlockClientEnemyPuppetNpcCombat(__instance, "Npc.EndMeleeDamageState", out _))
                    return false;
                NetGameplayProbeManager.ReportEnemyCombatProbe(__instance, "Npc.EndMeleeDamageState");
            }
            catch (Exception ex) { Log.Error($"[Npc.EndMeleeDamageState] {ex.Message}"); }
            return true;
        }

        private static bool Npc_SetRangedAttacking_Pre(object __instance, bool state)
        {
            try
            {
                if (state && NetGameplayProbeManager.ShouldBlockClientEnemyPuppetNpcCombat(__instance, "Npc.SetRangedAttacking", out _))
                    return false;
                if (state)
                    NetGameplayProbeManager.ReportEnemyCombatProbe(__instance, "Npc.SetRangedAttacking", $"state={state}");
            }
            catch (Exception ex) { Log.Error($"[Npc.SetRangedAttacking] {ex.Message}"); }
            return true;
        }

        // SetAttacking() has no 'state' parameter in the game binary — the method always means "start attacking".
        private static bool Npc_SetAttacking_Pre(object __instance)
        {
            try
            {
                if (NetGameplayProbeManager.ShouldBlockClientEnemyPuppetNpcCombat(__instance, "Npc.SetAttacking", out _))
                    return false;
                NetGameplayProbeManager.ReportEnemyCombatProbe(__instance, "Npc.SetAttacking");
            }
            catch (Exception ex) { Log.Error($"[Npc.SetAttacking] {ex.Message}"); }
            return true;
        }

        private static bool Npc_DoneAttacking_Pre(object __instance)
        {
            try
            {
                if (NetGameplayProbeManager.ShouldBlockClientEnemyPuppetNpcCombat(__instance, "Npc.DoneAttacking", out _))
                    return false;
                NetGameplayProbeManager.ReportEnemyCombatProbe(__instance, "Npc.DoneAttacking");
            }
            catch (Exception ex) { Log.Error($"[Npc.DoneAttacking] {ex.Message}"); }
            return true;
        }

        private static bool Npc_DoneShooting_Pre(object __instance)
        {
            try
            {
                if (NetGameplayProbeManager.ShouldBlockClientEnemyPuppetNpcCombat(__instance, "Npc.DoneShooting", out _))
                    return false;
                NetGameplayProbeManager.ReportEnemyCombatProbe(__instance, "Npc.DoneShooting");
            }
            catch (Exception ex) { Log.Error($"[Npc.DoneShooting] {ex.Message}"); }
            return true;
        }

        // Npc.Update — gated by EnableNpcUpdateProbe; per-NPC throttle
        private static void Npc_Update_Post(object __instance)
        {
            try
            {
                // Keep Host-driven puppet animation after vanilla Npc.Update, because the
                // original method writes Animator bool "Moving" from Client-local movement.
                NetGameplayProbeManager.ApplyClientEnemyPuppetAnimationPostUpdate(__instance);

                if (!Cfg.EnableNpcUpdateProbe.Value) return;
                string key = $"Npc_Update_{ID(__instance)}";
                if (!ReverseProbeState.ShouldLog(key, Cfg.ProbeThrottleSeconds.Value)) return;
                Log.Debug($"[Npc] Update (throttled) >> {F(__instance)}");
            }
            catch (Exception ex) { Log.Error($"[Npc.Update] {ex.Message}"); }
        }

        // Phase 5.3-B: returns bool so we can suppress local damage when client redirects to host.
        // 'source' binds to the DamageSourceData parameter of Npc.ReceiveDamage(float, DamageTypes, DamageSourceData
        // source, Hitmesh.Data, Vector3?) — its sole overload. Used to tell a real player hit from physics/environment.
        private static bool Npc_ReceiveDamage_Pre(object __instance, float damage, object damageType, object source)
        {
            try
            {
                // Phase 5.4-G3: BossDamageAuthority takes PRIORITY over the ordinary puppet path. A boss phase target may
                // ALSO be a pre-placed roster puppet (LogOutput35: Witch_Phase_5 is bound as a Client puppet), so if the
                // ordinary path ran first it would claim the hit and the boss never sees it (no main-health drop).
                // TryClientBossHit only claims a hit on a REGISTERED boss's current target (Witch phase1/3/4/5/6 witchUnit
                // + main, Lucia, Cousin); ordinary enemies, Witch egg and adds are NOT matched and fall through below.
                int damageTypeInt = 0; try { if (damageType != null) damageTypeInt = System.Convert.ToInt32(damageType); } catch { }
                if (SULFURTogether.Networking.Gameplay.Boss.NetBossEncounterManager.TryClientBossHit(__instance, damage, damageTypeInt))
                    return false;

                // Phase 5.5-RT3-A2: a host-driven puppet is host-authoritative over all its damage. Drop non-player
                // (physics/explosion/environment) damage locally and DO NOT forward it — the host simulates the
                // environment authoritatively and syncs HP back. This is what mis-killed snapped adds ~0.2s after spawn.
                if (NetGameplayProbeManager.ShouldIgnoreNonPlayerPuppetDamage(__instance, source))
                    return false;

                // Phase 5.3-B: intercept client player → puppet NPC damage and redirect to Host.
                // If TrySendClientHitRequest returns true, the host will apply authoritative damage;
                // suppress local application to avoid divergence.
                if (NetGameplayProbeManager.TrySendClientHitRequest(__instance, damage, damageType))
                    return false;

                ReverseProbeSummary.IncrementDamage("Npc");
                ReverseProbeKnownObjects.RegisterDamage(ID(__instance));
                NetGameplayProbeManager.ReportDamage(__instance, "Npc.ReceiveDamage", "Npc", damage, damageType);
                // Phase 5.2: damage event (amount only) sent here; health is read in
                // Unit_ReceiveDamage_Post after the actual Stats deduction in Unit.ReceiveDamage.
                NetGameplayProbeManager.ReportHostNpcDamageForSync(__instance, damage);
                Log.Info($"[Npc] ReceiveDamage << {F(__instance)} dmg={damage} type={damageType}");
            }
            catch (Exception ex) { Log.Error($"[Npc.ReceiveDamage] {ex.Message}"); }
            return true;
        }

        // ==================================================================
        // 3b. Weapon / Projectile visual probe
        // ==================================================================

        private static void ApplyWeaponProjectilePatches(Harmony harmony)
        {
            var weaponType = FindType("PerfectRandom.Sulfur.Core.Weapons.Weapon");
            if (weaponType != null)
                TryPatch(harmony, weaponType, "DispatchProjectile", Pre(nameof(Weapon_DispatchProjectile_Pre)), null);

            var projectileSystemType = FindType("PerfectRandom.Sulfur.Core.ProjectileSystem");
            if (projectileSystemType != null)
                PatchAllOverloads(harmony, projectileSystemType, "StartProjectile", prefix: Pre(nameof(ProjectileSystem_StartProjectile_Pre)));
        }

        private static void Weapon_DispatchProjectile_Pre(object __instance)
        {
            try
            {
                NetGameplayProbeManager.ReportProjectileProbe(__instance, "Weapon.DispatchProjectile", "pre");
            }
            catch (Exception ex) { Log.Error($"[Weapon.DispatchProjectile/Probe] {ex.Message}"); }
        }

        private static void ProjectileSystem_StartProjectile_Pre()
        {
            try
            {
                NetGameplayProbeManager.ReportProjectileProbe(null, "ProjectileSystem.StartProjectile", "pre");
            }
            catch (Exception ex) { Log.Error($"[ProjectileSystem.StartProjectile/Probe] {ex.Message}"); }
        }

        // ==================================================================
        // PlayerLife input lock — blocks all actions except camera look while locally downed
        // ==================================================================

        private static void ApplyInputReaderPatches(Harmony harmony)
        {
            if (!Cfg.EnableCoopPlayerDownedRevive.Value) return;
            var t = FindType("PerfectRandom.Sulfur.Core.Input.InputReader");
            if (t == null) return;

            TryPatch(harmony, t, "GetMovementInput", Pre(nameof(InputReader_GetVector2ZeroWhileDowned_Pre)), null);
            TryPatch(harmony, t, "GetRawMovementInput", Pre(nameof(InputReader_GetVector2ZeroWhileDowned_Pre)), null);
            TryPatch(harmony, t, "GetHorizontalMovementInput", Pre(nameof(InputReader_GetFloatZeroWhileDowned_Pre)), null);
            TryPatch(harmony, t, "GetVerticalMovementInput", Pre(nameof(InputReader_GetFloatZeroWhileDowned_Pre)), null);
            TryPatch(harmony, t, "IsJumpKeyPressed", Pre(nameof(InputReader_GetBoolFalseWhileDowned_Pre)), null);

            // Downed input is a BLACKLIST of combat actions only — NOT a whitelist that locks everything. Here we
            // block weapon SWITCHING (slot selectors). Weapon fire/reload/melee are covered by the Weapon PlayerLock
            // and movement by the Get*MovementInput patches above + the PlayerMovement lock. Crouch is forced via
            // the UpdateCrouching patch below. Everything ELSE stays usable — crucially PauseMenu (ESC → the game's
            // own pause/quit, read from the player's own keybind) and DevToolsToggle (F3), plus UI navigation — so a
            // downed player can always open the menu and quit.
            foreach (string method in new[]
            {
                "SelectLastUsedWeapon", "SelectNextSlot", "SelectPreviousSlot", "SelectByScroll",
                "SelectSlot1", "SelectSlot2", "SelectSlot3", "SelectSlot4", "SelectSlot5",
            })
            {
                TryPatch(harmony, t, method, Pre(nameof(InputReader_BlockActionWhileDowned_Pre)), null);
            }

            // Force a permanent crouch pose while downed and ignore crouch toggling (player can't stand up).
            var walker = FindType("PerfectRandom.Sulfur.Core.Movement.ExtendedAdvancedWalkerController");
            if (walker != null)
                TryPatch(harmony, walker, "UpdateCrouching", Pre(nameof(ExtendedWalker_UpdateCrouching_Pre)), null);
        }

        private static bool _forcedDownedCrouch;

        // Downed: hold the local player in a crouch (the "downed" pose) and ignore crouch input. On revive, stand
        // back up once, then let the game's own UpdateCrouching resume.
        private static bool ExtendedWalker_UpdateCrouching_Pre(object __instance)
        {
            if (__instance == null) return true;
            var type = __instance.GetType();
            if (NetPlayerLifeManager.ShouldSuppressLocalPlayerControls())
            {
                try
                {
                    var f = AccessTools.Field(type, "isCrouching");
                    bool crouching = f != null && f.GetValue(__instance) is bool b && b;
                    if (!crouching)
                        AccessTools.Method(type, "ToggleCrouch")?.Invoke(__instance, new object[] { true });
                }
                catch { }
                _forcedDownedCrouch = true;
                return false; // skip input-driven crouch toggling while downed
            }

            if (_forcedDownedCrouch)
            {
                _forcedDownedCrouch = false;
                try { AccessTools.Method(type, "ToggleCrouch")?.Invoke(__instance, new object[] { false }); } catch { }
            }
            return true;
        }

        private static bool InputReader_BlockActionWhileDowned_Pre()
        {
            return !NetPlayerLifeManager.ShouldSuppressLocalPlayerControls();
        }

        private static bool InputReader_GetVector2ZeroWhileDowned_Pre(ref Vector2 __result)
        {
            if (!NetPlayerLifeManager.ShouldSuppressLocalPlayerControls()) return true;
            __result = Vector2.zero;
            return false;
        }

        private static bool InputReader_GetFloatZeroWhileDowned_Pre(ref float __result)
        {
            if (!NetPlayerLifeManager.ShouldSuppressLocalPlayerControls()) return true;
            __result = 0f;
            return false;
        }

        private static bool InputReader_GetBoolFalseWhileDowned_Pre(ref bool __result)
        {
            if (!NetPlayerLifeManager.ShouldSuppressLocalPlayerControls()) return true;
            __result = false;
            return false;
        }

        // ==================================================================
        // 4. AiAgent — all methods default off; per-instance throttle keys
        // ==================================================================

        private static void ApplyAiAgentPatches(Harmony harmony)
        {
            if (!Cfg.EnableNpcProbe.Value) return;
            var t = FindType("PerfectRandom.Sulfur.Core.Units.AI.AiAgent");
            if (t == null) return;

            TryPatch(harmony, t, "UpdateTarget",         Pre(nameof(AI_UpdateTarget_Pre)),    null);
            TryPatch(harmony, t, "SetNavMeshAgentState", Pre(nameof(AI_SetNavMeshState_Pre)), null);
            TryPatch(harmony, t, "SetCanMove",           Pre(nameof(AI_SetCanMove_Pre)),      null);
            TryPatch(harmony, t, "GetTarget",            null,                                 Post(nameof(AI_GetTarget_Post)));
            PatchAllOverloads(harmony, t, "SetDestination", Pre(nameof(AI_SetDestination_Pre)));

            var customRichAi = FindType("PerfectRandom.Sulfur.Core.Units.AI.CustomRichAI");
            if (customRichAi != null)
                TryPatch(harmony, customRichAi, "FinalMovement", Pre(nameof(CustomRichAI_FinalMovement_Pre)), null);
        }

        private static bool AI_UpdateTarget_Pre(object __instance)
        {
            try
            {
                DumpAiTargetingOnce(__instance);
                if (NetGameplayProbeManager.ShouldBlockClientEnemyPuppetAiAgentTargeting(__instance, "AiAgent.UpdateTarget", out _))
                    return false;
                if (!Cfg.EnableAiUpdateTargetProbe.Value) return true;
                string key = $"AI_UpdateTarget_{ID(__instance)}";
                if (!ReverseProbeState.ShouldLog(key, Cfg.ProbeThrottleSeconds.Value)) return true;
                Log.Debug($"[AI] UpdateTarget (throttled) << {F(__instance)}");
            }
            catch (Exception ex) { Log.Error($"[AI.UpdateTarget] {ex.Message}"); }
            return true;
        }
        private static bool AI_SetDestination_Pre(object __instance, MethodBase __originalMethod, object[] __args)
        {
            try
            {
                string source = __originalMethod == null ? "AiAgent.SetDestination" : "AiAgent." + __originalMethod.Name;
                NetGameplayProbeManager.ReportEnemyAiIntent(__instance, source, __args);
                if (NetGameplayProbeManager.ShouldBlockClientEnemyPuppetAiAgentMovement(__instance))
                    return false;
                if (!Cfg.EnableAiSetDestinationProbe.Value) return true;
                string key = $"AI_SetDestination_{ID(__instance)}";
                if (!ReverseProbeState.ShouldLog(key, Cfg.ProbeThrottleSeconds.Value)) return true;
                Log.Debug($"[AI] SetDestination (throttled) << {F(__instance)}");
            }
            catch (Exception ex) { Log.Error($"[AI.SetDestination] {ex.Message}"); }
            return true;
        }
        private static bool AI_SetNavMeshState_Pre(object __instance, bool state)
        {
            try
            {
                if (NetGameplayProbeManager.ShouldBlockClientEnemyPuppetAiAgentState(__instance, state))
                    return false;
                if (!Cfg.EnableAiNavMeshStateProbe.Value) return true;
                Log.Debug($"[AI] SetNavMeshAgentState << {F(__instance)} state={state}");
            }
            catch (Exception ex) { Log.Error($"[AI.SetNavMeshAgentState] {ex.Message}"); }
            return true;
        }
        private static bool AI_SetCanMove_Pre(object __instance, bool state)
        {
            try
            {
                if (NetGameplayProbeManager.ShouldBlockClientEnemyPuppetAiAgentState(__instance, state))
                    return false;
                if (!Cfg.EnableAiCanMoveProbe.Value) return true;
                Log.Debug($"[AI] SetCanMove << {F(__instance)} state={state}");
            }
            catch (Exception ex) { Log.Error($"[AI.SetCanMove] {ex.Message}"); }
            return true;
        }

        private static bool CustomRichAI_FinalMovement_Pre(object __instance)
        {
            try
            {
                if (NetGameplayProbeManager.ShouldSkipClientEnemyPuppetFinalMovement(__instance))
                    return false;
            }
            catch (Exception ex) { Log.Error($"[CustomRichAI.FinalMovement/Puppet] {ex.Message}"); }
            return true;
        }

        // ---- P3 (remote-player targeting) reverse dump v3: faction VALUES + an AGGROED enemy's target/hostilesInLOS ----
        private static int  _aiDumpEnemyCount;
        private static bool _aiDumpGmDone;
        private static bool _aiDumpAggroDone;
        private static void DumpAiTargetingOnce(object aiAgent)
        {
            if (aiAgent == null || !Cfg.LogAiTargetingReverseDump.Value) return;
            try
            {
                DumpDetectionIL(aiAgent.GetType());
                if (!_aiDumpGmDone) TryDumpGameManagerPlayers();

                object? owner = TryGetMember(aiAgent, "Owner");
                string ownerName = DescribeVal(owner);
                if (ownerName.IndexOf("Trader", StringComparison.OrdinalIgnoreCase) >= 0) return; // want combat enemies

                object? target = TryGetMember(aiAgent, "target");
                object? hostiles = TryGetMember(aiAgent, "hostilesInLOS");

                // Capture the first AGGROED enemy (has a live target) — shows what it targets + its hostile list contents.
                if (!_aiDumpAggroDone && target != null)
                {
                    _aiDumpAggroDone = true;
                    Log.Info($"[AiDump] AGGRO enemy owner={ownerName} faction={ReadFaction(owner)} target={DescribeVal(target)} targetFaction={ReadFaction(target)} targetIsPlayer={TryGetMember(target, "isPlayer")} hostilesInLOS={DescribeEnumerable(hostiles)}");
                }

                if (_aiDumpEnemyCount >= 3) return;
                _aiDumpEnemyCount++;
                Log.Info($"[AiDump] ENEMY agent owner={ownerName} faction={ReadFaction(owner)} onlyTargetPlayer={TryGetMember(aiAgent, "onlyTargetPlayer")} target={DescribeVal(target)} playerUnit={DescribeVal(TryGetMember(aiAgent, "playerUnit"))}");
            }
            catch (Exception ex) { Log.Error($"[AiDump] {ex.Message}"); }
        }

        // Read a Unit's effective faction — overriddenFactionId is None by default, so the real one is a property/method.
        private static string ReadFaction(object? unit)
        {
            if (unit == null) return "null";
            foreach (var n in new[] { "FactionId", "factionId", "Faction", "CurrentFactionId", "GetFactionId", "GetFaction" })
            {
                try
                {
                    var m = TryGetMember(unit, n);
                    if (m != null) return $"{n}={DescribeVal(m)}";
                    var meth = unit.GetType().GetMethod(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                    if (meth != null) { var r = meth.Invoke(unit, null); return $"{n}()={DescribeVal(r)}"; }
                }
                catch { }
            }
            return "faction?";
        }

        private static object? TryGetMember(object obj, string name)
        {
            try
            {
                const BindingFlags fl = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                for (Type? c = obj.GetType(); c != null && c != typeof(object); c = c.BaseType)
                {
                    var p = c.GetProperty(name, fl); if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(obj, null);
                    var f = c.GetField(name, fl); if (f != null) return f.GetValue(obj);
                }
            }
            catch { }
            return null;
        }

        private static void TryDumpGameManagerPlayers()
        {
            try
            {
                var gmType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.GameManager");
                var gm = gmType == null ? null : AccessTools.Property(gmType, "Instance")?.GetValue(null, null);
                if (gm == null) return; // retry next call
                object? players = AccessTools.Property(gmType, "Players")?.GetValue(gm, null);
                object? playerUnit = AccessTools.Property(gmType, "PlayerUnit")?.GetValue(gm, null);
                object? units = AccessTools.Property(gmType, "units")?.GetValue(gm, null);
                if (playerUnit == null && !(players is System.Collections.ICollection pc0 && pc0.Count > 0)) return; // not populated yet

                Log.Info($"[AiDump] GM.Players={DescribeEnumerable(players)} units={DescribeVal(units)}");
                if (playerUnit != null)
                {
                    Log.Info($"[AiDump] GM.PlayerUnit={DescribeVal(playerUnit)} faction={ReadFaction(playerUnit)} isPlayer={TryGetMember(playerUnit, "isPlayer")} type={playerUnit.GetType().FullName} interfaces=[{string.Join(",", playerUnit.GetType().GetInterfaces().Select(i => i.Name))}]");
                    DumpFields("PlayerUnit", playerUnit, new[] { "faction", "team", "player" });

                    // A2 prep: is the player in GameManager.units? (the likely list the faction-LOS scan iterates) + what
                    // Unit construction/registration/stats/collider members exist (so a runtime proxy Unit can be built safely).
                    bool playerInUnits = false;
                    if (units is System.Collections.IEnumerable ue) foreach (var u in ue) if (ReferenceEquals(u, playerUnit)) { playerInUnits = true; break; }
                    Log.Info($"[AiDump] playerInGM.units={playerInUnits}");
                    DumpMethods(playerUnit.GetType(), new[] { "register", "awake", "init", "setup", "spawn", "faction", "addto", "stats", "setstatus", "collider", "enable", "isplayer", "setplayer" });
                    Log.Info($"[AiDump] Unit components=[{string.Join(",", (playerUnit is Component pc2 && pc2 != null ? pc2.GetComponents<Component>().Select(c => c.GetType().Name) : new string[0]))}]");
                }
                _aiDumpGmDone = true;
            }
            catch (Exception ex) { Log.Error($"[AiDump.GM] {ex.Message}"); }
        }

        private static string DescribeEnumerable(object? c)
        {
            if (c == null) return "null";
            if (c is string s) return s;
            if (c is System.Collections.IEnumerable en)
            {
                var sb = new System.Text.StringBuilder(); int n = 0;
                foreach (var e in en) { if (n > 0) sb.Append(", "); sb.Append(DescribeVal(e)); if (++n >= 12) { sb.Append(",..."); break; } }
                return $"[{n}]{{{sb}}}";
            }
            return DescribeVal(c);
        }

        private static bool NameHas(string name, params string[] kws)
        {
            string n = name.ToLowerInvariant();
            foreach (var k in kws) if (n.Contains(k)) return true;
            return false;
        }

        // ---- P3: IL reverse — dump every method call / field access in a method body, no decompiler needed ----
        private static bool _ilDumped;
        private static System.Collections.Generic.Dictionary<int, System.Reflection.Emit.OpCode>? _ilOpcodes;
        private static void DumpDetectionIL(Type aiAgentType)
        {
            if (_ilDumped) return;
            _ilDumped = true;
            foreach (var name in new[] { "UpdateTarget", "IsPlayerInSight", "HandleDetectionDistance", "GetTarget" })
                DumpMethodIL(aiAgentType, name);

            // A3: how does the enemy's melee actually DEAL damage? (proxy gets targeted but never ReceiveDamage'd)
            var npcType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.Units.Npc");
            if (npcType != null)
                foreach (var name in new[] { "HandleMeleeHit", "HitmeshHitByMelee", "StartMeleeDamageState" })
                    if (AccessTools.Method(npcType, name) != null) DumpMethodIL(npcType, name);

            // A3: the melee damages a Hitmesh.owner. Reverse the Hitmesh type + the player's hitbox setup so we can put
            // a working Hitmesh on the proxy (collider layer + owner) and let the native melee/ranged pipeline hit it.
            try
            {
                var hitmeshType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.Units.Hitmesh")
                                  ?? AccessTools.TypeByName("Hitmesh");
                if (hitmeshType != null)
                {
                    Log.Info($"[AiDump] === Hitmesh type={hitmeshType.FullName} baseType={hitmeshType.BaseType?.Name} ===");
                    foreach (var f in hitmeshType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        Log.Info($"[AiDump] Hitmesh field {f.Name} : {f.FieldType.Name}");
                    foreach (var m in hitmeshType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        if (!m.IsSpecialName || m.Name == "Awake" || m.Name == "Start" || m.Name == "OnEnable")
                            Log.Info($"[AiDump] Hitmesh method {m.Name}({string.Join(",", System.Linq.Enumerable.Select(m.GetParameters(), p => p.ParameterType.Name))})");
                }
                var pu = SULFURTogether.Networking.Gameplay.Boss.BossDamageReflect.ResolveHostPlayerUnit();
                if (pu is Component puc && puc != null && hitmeshType != null)
                {
                    var comps = puc.GetComponentsInChildren(hitmeshType, true);
                    Log.Info($"[AiDump] player Hitmesh count={comps.Length}");
                    foreach (var c in comps)
                    {
                        var go = (c as Component)?.gameObject;
                        var col = go == null ? null : go.GetComponent<Collider>();
                        var owner = TryGetMember(c, "owner");
                        Log.Info($"[AiDump] Hitmesh on go={go?.name} layer={go?.layer} collider={(col == null ? "none" : col.GetType().Name + " trigger=" + col.isTrigger)} owner={DescribeVal(owner)}");
                    }
                }
            }
            catch (Exception ex) { Log.Error($"[AiDump] Hitmesh dump failed: {ex.Message}"); }

            // Find what POPULATES hostilesInLOS: scan every AiAgent method for one that touches the list + Adds to it.
            try
            {
                var populators = new System.Collections.Generic.List<string>();
                foreach (var m in aiAgentType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (m.IsAbstract || m.ContainsGenericParameters) continue;
                    CollectMethodRefs(m, out var calls, out var fields);
                    bool touchesHostiles = calls.Contains("get_hostilesInLOS") || fields.Contains("<hostilesInLOS>k__BackingField") || fields.Contains("hostilesInLOS");
                    if (!touchesHostiles) continue;
                    bool add = calls.Contains("Add"), clear = calls.Contains("Clear");
                    Log.Info($"[ILDump] hostilesInLOS touched by {m.Name}(add={add} clear={clear}) calls=[{string.Join(",", System.Linq.Enumerable.Take(calls, 24))}]");
                    if (add) populators.Add(m.Name);
                }
                foreach (var name in populators) DumpMethodIL(aiAgentType, name);

                // Reverse the OverrideTarget API (GetTarget uses overridetargets.HasTargets/GrabHostileUnit early — the
                // intended "force this AI to target X" hook). Dump its methods + key IL so we can drive it.
                var otField = aiAgentType.GetField("overridetargets", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var otType = otField?.FieldType ?? AccessTools.TypeByName("OverrideTarget");
                if (otType != null)
                {
                    Log.Info($"[ILDump] === OverrideTarget type={otType.FullName} ===");
                    foreach (var mm in otType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (mm.IsSpecialName && !mm.Name.StartsWith("get_") && !mm.Name.StartsWith("set_")) continue;
                        Log.Info($"[ILDump] OverrideTarget.{mm.Name}({string.Join(",", System.Linq.Enumerable.Select(mm.GetParameters(), p => p.ParameterType.Name + " " + p.Name))}) -> {mm.ReturnType.Name}");
                    }
                    foreach (var f in otType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        Log.Info($"[ILDump] OverrideTarget field {f.Name} : {f.FieldType.Name}");
                    foreach (var name in new[] { "AddUnits", "ClearUnits", "GrabHostileUnit", "get_HasTargets" })
                        if (otType.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null)
                            DumpMethodIL(otType, name);
                    // TargetType enum values (param of AddUnits) — so we pass the right one.
                    var addUnits = otType.GetMethod("AddUnits", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var ttType = addUnits?.GetParameters().Length == 2 ? addUnits.GetParameters()[1].ParameterType : null;
                    if (ttType != null && ttType.IsEnum)
                        Log.Info($"[ILDump] TargetType enum {ttType.FullName} values=[{string.Join(",", Enum.GetNames(ttType))}]");
                }

                // The GetTarget selection predicate (LastOrDefault filter) lives in a nested <>c class — dump it.
                foreach (var nt in aiAgentType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                    foreach (var nm in nt.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        if (nm.Name.Contains("GetTarget")) DumpMethodIL(nt, nm.Name);
            }
            catch (Exception ex) { Log.Error($"[ILDump] populator scan failed: {ex.Message}"); }
        }

        private static void CollectMethodRefs(MethodInfo m, out System.Collections.Generic.HashSet<string> calls, out System.Collections.Generic.HashSet<string> fields)
        {
            calls = new System.Collections.Generic.HashSet<string>();
            fields = new System.Collections.Generic.HashSet<string>();
            try
            {
                if (_ilOpcodes == null)
                {
                    _ilOpcodes = new System.Collections.Generic.Dictionary<int, System.Reflection.Emit.OpCode>();
                    foreach (var fi in typeof(System.Reflection.Emit.OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                        if (fi.FieldType == typeof(System.Reflection.Emit.OpCode)) { var op = (System.Reflection.Emit.OpCode)fi.GetValue(null)!; _ilOpcodes[(ushort)op.Value] = op; }
                }
                var il = m.GetMethodBody()?.GetILAsByteArray();
                if (il == null) return;
                var module = m.Module;
                Type[]? ta = m.DeclaringType != null && m.DeclaringType.IsGenericType ? m.DeclaringType.GetGenericArguments() : null;
                Type[]? ma = m.IsGenericMethod ? m.GetGenericArguments() : null;
                int i = 0;
                while (i < il.Length)
                {
                    int code = il[i++];
                    if (code == 0xFE && i < il.Length) code = 0xFE00 | il[i++];
                    if (!_ilOpcodes.TryGetValue(code, out var op)) break;
                    switch (op.OperandType)
                    {
                        case System.Reflection.Emit.OperandType.InlineNone: break;
                        case System.Reflection.Emit.OperandType.ShortInlineBrTarget:
                        case System.Reflection.Emit.OperandType.ShortInlineI:
                        case System.Reflection.Emit.OperandType.ShortInlineVar: i += 1; break;
                        case System.Reflection.Emit.OperandType.InlineVar: i += 2; break;
                        case System.Reflection.Emit.OperandType.InlineI8:
                        case System.Reflection.Emit.OperandType.InlineR: i += 8; break;
                        case System.Reflection.Emit.OperandType.InlineSwitch: { int n = BitConverter.ToInt32(il, i); i += 4 + 4 * n; break; }
                        case System.Reflection.Emit.OperandType.InlineMethod:
                            { int tok = BitConverter.ToInt32(il, i); i += 4; try { var mb = module.ResolveMethod(tok, ta, ma); if (mb != null) calls.Add(mb.Name); } catch { } break; }
                        case System.Reflection.Emit.OperandType.InlineField:
                            { int tok = BitConverter.ToInt32(il, i); i += 4; try { var f = module.ResolveField(tok, ta, ma); if (f != null) fields.Add(f.Name); } catch { } break; }
                        case System.Reflection.Emit.OperandType.InlineTok:
                            { i += 4; break; }
                        default: i += 4; break;
                    }
                }
            }
            catch { }
        }

        private static void DumpMethodIL(Type t, string methodName)
        {
            try
            {
                if (_ilOpcodes == null)
                {
                    _ilOpcodes = new System.Collections.Generic.Dictionary<int, System.Reflection.Emit.OpCode>();
                    foreach (var fi in typeof(System.Reflection.Emit.OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                        if (fi.FieldType == typeof(System.Reflection.Emit.OpCode))
                        { var op = (System.Reflection.Emit.OpCode)fi.GetValue(null)!; _ilOpcodes[(ushort)op.Value] = op; }
                }
                var m = AccessTools.Method(t, methodName);
                var il = m?.GetMethodBody()?.GetILAsByteArray();
                if (m == null || il == null) { Log.Info($"[ILDump] {t.Name}.{methodName} no IL"); return; }
                var module = m.Module;
                Type[]? ta = t.IsGenericType ? t.GetGenericArguments() : null;
                Type[]? ma = m.IsGenericMethod ? m.GetGenericArguments() : null;
                Log.Info($"[ILDump] ===== {t.Name}.{methodName} (IL {il.Length}B) =====");
                int i = 0, logged = 0;
                while (i < il.Length && logged < 200)
                {
                    int code = il[i++];
                    if (code == 0xFE && i < il.Length) code = 0xFE00 | il[i++];
                    if (!_ilOpcodes.TryGetValue(code, out var op)) break;
                    switch (op.OperandType)
                    {
                        case System.Reflection.Emit.OperandType.InlineNone: break;
                        case System.Reflection.Emit.OperandType.ShortInlineBrTarget:
                        case System.Reflection.Emit.OperandType.ShortInlineI:
                        case System.Reflection.Emit.OperandType.ShortInlineVar: i += 1; break;
                        case System.Reflection.Emit.OperandType.InlineVar: i += 2; break;
                        case System.Reflection.Emit.OperandType.InlineI8:
                        case System.Reflection.Emit.OperandType.InlineR: i += 8; break;
                        case System.Reflection.Emit.OperandType.InlineSwitch: { int n = BitConverter.ToInt32(il, i); i += 4 + 4 * n; break; }
                        case System.Reflection.Emit.OperandType.InlineMethod:
                            { int tok = BitConverter.ToInt32(il, i); i += 4; try { var mb = module.ResolveMethod(tok, ta, ma); Log.Info($"[ILDump]   {op.Name} {mb?.DeclaringType?.Name}.{mb?.Name}"); logged++; } catch { } break; }
                        case System.Reflection.Emit.OperandType.InlineField:
                            { int tok = BitConverter.ToInt32(il, i); i += 4; try { var f = module.ResolveField(tok, ta, ma); Log.Info($"[ILDump]   {op.Name} {f?.DeclaringType?.Name}.{f?.Name}"); logged++; } catch { } break; }
                        case System.Reflection.Emit.OperandType.InlineTok:
                            { int tok = BitConverter.ToInt32(il, i); i += 4; try { var mem = module.ResolveMember(tok, ta, ma); Log.Info($"[ILDump]   {op.Name} {mem}"); logged++; } catch { } break; }
                        default: i += 4; break; // InlineBrTarget/InlineI/InlineString/InlineSig/InlineType/ShortInlineR
                    }
                }
            }
            catch (Exception ex) { Log.Error($"[ILDump] {t.Name}.{methodName} failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static string DescribeVal(object? v)
        {
            if (v == null) return "null";
            if (v is UnityEngine.Object uo) return uo == null ? "null(destroyed)" : $"{v.GetType().Name}:{uo.name}";
            var t = v.GetType();
            if (v is Vector3 vec) return vec.ToString("F1");
            if (t.IsPrimitive || v is string || t.IsEnum) return v.ToString();
            if (v is System.Collections.ICollection col) return $"{t.Name}[{col.Count}]";
            return t.Name;
        }

        private static void DumpFields(string label, object obj, string[]? filter)
        {
            int count = 0;
            for (Type? cur = obj.GetType(); cur != null && cur != typeof(object) && cur != typeof(UnityEngine.MonoBehaviour); cur = cur.BaseType)
            {
                foreach (var f in cur.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (filter != null && !NameHas(f.Name, filter)) continue;
                    string val = ""; try { val = DescribeVal(f.GetValue(obj)); } catch { val = "<err>"; }
                    Log.Info($"[AiDump] {label}({cur.Name}).{f.Name} : {f.FieldType.Name} = {val}");
                    if (++count > 140) { Log.Info("[AiDump] ...(field dump truncated)"); return; }
                }
            }
        }

        private static void DumpMethods(Type t, string[] keywords)
        {
            foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.DeclaringType == typeof(object) || m.IsSpecialName) continue;
                if (!NameHas(m.Name, keywords)) continue;
                string ps = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                Log.Info($"[AiDump] method {m.DeclaringType?.Name}.{m.Name}({ps}) -> {m.ReturnType.Name}");
            }
        }

        // GetTarget — off by default; logs only on target change. Client puppet target result is observed/cleared through puppet authority where possible.
        private static void AI_GetTarget_Post(object __instance, object __result)
        {
            try
            {
                NetGameplayProbeManager.ReportClientEnemyPuppetAiTargetResult(__instance, __result, "AiAgent.GetTarget");
                if (!Cfg.EnableAiTargetProbe.Value) return;
                string agentId    = ID(__instance);
                string newTargetId = ID(__result);
                if (!ReverseProbeState.HasAiTargetChanged(agentId, newTargetId)) return;
                Log.Debug($"[AI] GetTarget changed >> agent={F(__instance)} newTarget={F(__result)}");
            }
            catch (Exception ex) { Log.Error($"[AI.GetTarget] {ex.Message}"); }
        }

        // ==================================================================
        // 5. UnitManager
        // ==================================================================

        private static void ApplyUnitManagerPatches(Harmony harmony)
        {
            if (!Cfg.EnableUnitProbe.Value) return;
            var t = FindType("PerfectRandom.Sulfur.Core.Units.UnitManager");
            if (t == null) return;

            TryPatch(harmony, t, "AddUnit",     null,                            Post(nameof(UM_AddUnit_Post)));
            TryPatch(harmony, t, "OnUnitDeath", Pre(nameof(UM_OnUnitDeath_Pre)), null);
            TryPatch(harmony, t, "GetAllNpcs",  null,                            Post(nameof(UM_GetAllNpcs_Post)));
        }

        private static void UM_AddUnit_Post(object __instance, object unit)
        {
            try
            {
                string category = GetUnitCategory(unit);
                NetGameplayProbeManager.ReportSpawn(unit, "UnitManager.AddUnit", category);
                Log.Info($"[UnitManager] AddUnit >> {F(unit)}");
            }
            catch (Exception ex) { Log.Error($"[UnitManager.AddUnit] {ex.Message}"); }
        }
        private static void UM_OnUnitDeath_Pre(object __instance, object unit)
        {
            try
            {
                string category = GetUnitCategory(unit);
                NetGameplayProbeManager.ReportDeath(unit, "UnitManager.OnUnitDeath", category);
                Log.Info($"[UnitManager] OnUnitDeath << {F(unit)}");
            }
            catch (Exception ex) { Log.Error($"[UnitManager.OnUnitDeath] {ex.Message}"); }
        }
        private static void UM_GetAllNpcs_Post(object __instance, bool includeDead, object __result)
        {
            try { Log.Debug($"[UnitManager] GetAllNpcs >> includeDead={includeDead} result={__result}"); }
            catch (Exception ex) { Log.Error($"[UnitManager.GetAllNpcs] {ex.Message}"); }
        }

        // ==================================================================
        // 6. LootManager
        // ==================================================================

        private static void ApplyLootManagerPatches(Harmony harmony)
        {
            if (!Cfg.EnableLootProbe.Value) return;
            var t = FindType("PerfectRandom.Sulfur.Core.Items.LootManager");
            if (t == null) return;

            TryPatch(harmony, t, "RegisterLootDropped", Pre(nameof(LM_RegisterLootDropped_Pre)), null);
            TryPatch(harmony, t, "OnNewLevel",          Pre(nameof(LM_OnNewLevel_Pre)),           null);
            TryPatch(harmony, t, "ClearOnNewLevel",     Pre(nameof(LM_ClearOnNewLevel_Pre)),      null);
            PatchAllOverloads(harmony, t, "SpawnGlobalLoot", Pre(nameof(LM_SpawnGlobalLoot_Pre)));
            PatchAllOverloads(harmony, t, "SpawnLootFrom",   Pre(nameof(LM_SpawnLootFrom_Pre)));
        }

        private static void LM_RegisterLootDropped_Pre(object __instance, object item)
        {
            try
            {
                if (!Cfg.EnableLootRegisterProbe.Value) return;
                string itemName = ItemName(item);
                ReverseProbeSummary.IncrementLootSpawn();
                if (Cfg.CompactLootLogs.Value)
                    ReverseProbeSummary.AddLootRegisteredBurst(itemName);
                if (!Cfg.CompactLootLogs.Value || Cfg.EnableVerboseLootProbe.Value)
                    Log.Info($"[LootManager] RegisterLootDropped << item={itemName} raw={F(item)}");
            }
            catch (Exception ex) { Log.Error($"[LootManager.RegisterLootDropped] {ex.Message}"); }
        }
        private static void LM_OnNewLevel_Pre(object __instance)
        {
            try { Log.Info("[LootManager] OnNewLevel <<"); }
            catch (Exception ex) { Log.Error($"[LootManager.OnNewLevel] {ex.Message}"); }
        }
        private static void LM_ClearOnNewLevel_Pre(object __instance)
        {
            try
            {
                ReverseProbeKnownObjects.ClearPickups();
                Log.Info("[LootManager] ClearOnNewLevel <<");
            }
            catch (Exception ex) { Log.Error($"[LootManager.ClearOnNewLevel] {ex.Message}"); }
        }
        private static void LM_SpawnGlobalLoot_Pre(object __instance)
        {
            try
            {
                if (!Cfg.EnableLootSpawnProbe.Value) return;
                ReverseProbeSummary.IncrementLootSpawn();
                if (Cfg.CompactLootLogs.Value)
                    ReverseProbeSummary.AddLootSpawnedBurst();
                if (!Cfg.CompactLootLogs.Value || Cfg.EnableVerboseLootProbe.Value)
                    Log.Info("[LootManager] SpawnGlobalLoot << (overload triggered)");
            }
            catch (Exception ex) { Log.Error($"[LootManager.SpawnGlobalLoot] {ex.Message}"); }
        }
        private static void LM_SpawnLootFrom_Pre(object __instance)
        {
            try
            {
                if (!Cfg.EnableLootSpawnProbe.Value) return;
                ReverseProbeSummary.IncrementLootSpawn();
                if (Cfg.CompactLootLogs.Value)
                    ReverseProbeSummary.AddLootSpawnedBurst();
                if (!Cfg.CompactLootLogs.Value || Cfg.EnableVerboseLootProbe.Value)
                    Log.Info("[LootManager] SpawnLootFrom << (overload triggered)");
            }
            catch (Exception ex) { Log.Error($"[LootManager.SpawnLootFrom] {ex.Message}"); }
        }

        // ==================================================================
        // 7. InteractionManager
        // ==================================================================

        private static void ApplyInteractionManagerPatches(Harmony harmony)
        {
            if (!Cfg.EnablePickupProbe.Value) return;
            var t = FindType("PerfectRandom.Sulfur.Core.InteractionManager");
            if (t == null) return;

            TryPatch(harmony, t, "ExecutePickup", Pre(nameof(IM_ExecutePickup_Pre)), Post(nameof(IM_ExecutePickup_Post)));
            TryPatch(harmony, t, "RemovePickup",  Pre(nameof(IM_RemovePickup_Pre)),  null);
            TryPatch(harmony, t, "SpawnPickup",   Pre(nameof(IM_SpawnPickup_Pre)),   Post(nameof(IM_SpawnPickup_Post)));
        }

        private static void IM_ExecutePickup_Pre(object __instance, object pickup)
        {
            try
            {
                if (!Cfg.EnablePickupExecuteProbe.Value) return;
                string pickupId = ID(pickup);
                string itemName = ReverseProbeKnownObjects.GetPickupItemNameOrFallback(pickupId, F(pickup));
                ReverseProbeSummary.IncrementPickupExecute();
                if (Cfg.CompactPickupLogs.Value)
                    ReverseProbeSummary.AddPickupExecutedBurst(itemName);
                ReverseProbeKnownObjects.RegisterPickupExecuted(pickupId);
                if (!Cfg.CompactPickupLogs.Value || Cfg.EnableVerbosePickupProbe.Value)
                    Log.Info($"[InteractionManager] ExecutePickup << item={itemName} pickup={F(pickup)}");
            }
            catch (Exception ex) { Log.Error($"[InteractionManager.ExecutePickup] {ex.Message}"); }
        }
        private static void IM_ExecutePickup_Post(object __instance, object pickup)
        {
            try
            {
                if (Cfg.EnableVerbosePickupProbe.Value)
                    Log.Info("[InteractionManager] ExecutePickup >>");
            }
            catch (Exception ex) { Log.Error($"[InteractionManager.ExecutePickup Post] {ex.Message}"); }
        }
        private static void IM_RemovePickup_Pre(object __instance, object pickup)
        {
            try
            {
                if (!Cfg.EnablePickupSpawnProbe.Value) return;
                string pickupId = ID(pickup);
                string itemName = ReverseProbeKnownObjects.GetPickupItemNameOrFallback(pickupId, F(pickup));
                ReverseProbeKnownObjects.RegisterPickupRemoved(pickupId);
                if (Cfg.CompactPickupLogs.Value)
                    ReverseProbeSummary.AddPickupRemovedBurst();
                if (!Cfg.CompactPickupLogs.Value || Cfg.EnableVerbosePickupProbe.Value)
                    Log.Info($"[InteractionManager] RemovePickup << item={itemName} pickup={F(pickup)}");
            }
            catch (Exception ex) { Log.Error($"[InteractionManager.RemovePickup] {ex.Message}"); }
        }
        private static void IM_SpawnPickup_Pre(object __instance, Vector3 position, bool motionTowardsPlayer, object item, ref string __state)
        {
            try
            {
                __state = ItemName(item);
                if (!Cfg.EnablePickupSpawnProbe.Value) return;
                if (Cfg.EnableVerbosePickupProbe.Value)
                    Log.Info($"[InteractionManager] SpawnPickup << pos={V(position)} motion={motionTowardsPlayer} item={__state} raw={F(item)}");
            }
            catch (Exception ex)
            {
                __state = "<unknown>";
                Log.Error($"[InteractionManager.SpawnPickup] {ex.Message}");
            }
        }
        private static void IM_SpawnPickup_Post(object __instance, object __result, string __state)
        {
            try
            {
                if (!Cfg.EnablePickupSpawnProbe.Value) return;
                string itemName = string.IsNullOrWhiteSpace(__state) ? "<unknown>" : __state;
                ReverseProbeSummary.IncrementPickupSpawn();
                ReverseProbeKnownObjects.RegisterPickupSpawned(ID(__result), F(__result), itemName);
                if (Cfg.CompactPickupLogs.Value)
                    ReverseProbeSummary.AddPickupSpawnedBurst(itemName);
                if (!Cfg.CompactPickupLogs.Value || Cfg.EnableVerbosePickupProbe.Value)
                    Log.Info($"[InteractionManager] SpawnPickup >> item={itemName} pickup={F(__result)}");
            }
            catch (Exception ex) { Log.Error($"[InteractionManager.SpawnPickup Post] {ex.Message}"); }
        }

        // ==================================================================
        // 8. InventoryItem — count mode by default
        // ==================================================================

        private static void ApplyInventoryItemPatches(Harmony harmony)
        {
            if (!Cfg.EnableLootProbe.Value) return;
            var t = FindType("PerfectRandom.Sulfur.Core.Items.InventoryItem");
            if (t == null) return;

            TryPatch(harmony, t, "GetSerialized",            null,                                Post(nameof(II_GetSerialized_Post)));
            TryPatch(harmony, t, "DropFromPlayer",           Pre(nameof(II_DropFromPlayer_Pre)),   null);
            TryPatch(harmony, t, "DestroyFromInventory",     Pre(nameof(II_DestroyFromInventory_Pre)), null);
            TryPatch(harmony, t, "TryMoveToPlayerInventory", Pre(nameof(II_TryMoveToPlayer_Pre)),  null);
            TryPatch(harmony, t, "TransferOwnership",        Pre(nameof(II_TransferOwnership_Pre)), null);
            PatchAllOverloads(harmony, t, "Setup", null, Post(nameof(II_Setup_Post)));
        }

        private static void II_GetSerialized_Post(object __instance, object __result)
        {
            try
            {
                ReverseProbeSummary.IncrementInventoryGetSerialized();
                if (!Cfg.EnableInventorySerializationProbe.Value) return;
                Log.Debug($"[InventoryItem] GetSerialized >> {F(__instance)} result={__result}");
            }
            catch (Exception ex) { Log.Error($"[InventoryItem.GetSerialized] {ex.Message}"); }
        }
        private static void II_DropFromPlayer_Pre(object __instance)
        {
            try
            {
                ReverseProbeSummary.IncrementInventoryDrop();
                if (!Cfg.EnableInventoryDropProbe.Value) return;
                Log.Info($"[InventoryItem] DropFromPlayer << {F(__instance)}");
            }
            catch (Exception ex) { Log.Error($"[InventoryItem.DropFromPlayer] {ex.Message}"); }
        }
        private static void II_DestroyFromInventory_Pre(object __instance)
        {
            try
            {
                ReverseProbeSummary.IncrementInventoryDestroy();
                if (!Cfg.EnableInventoryDestroyProbe.Value) return;
                Log.Info($"[InventoryItem] DestroyFromInventory << {F(__instance)}");
            }
            catch (Exception ex) { Log.Error($"[InventoryItem.DestroyFromInventory] {ex.Message}"); }
        }
        private static void II_TryMoveToPlayer_Pre(object __instance)
        {
            try
            {
                ReverseProbeSummary.IncrementInventoryMoveToPlayer();
                if (!Cfg.EnableInventoryTransferProbe.Value) return;
                Log.Info($"[InventoryItem] TryMoveToPlayerInventory << {F(__instance)}");
            }
            catch (Exception ex) { Log.Error($"[InventoryItem.TryMoveToPlayerInventory] {ex.Message}"); }
        }
        private static void II_TransferOwnership_Pre(object __instance, object unit, bool isTransaction)
        {
            try
            {
                ReverseProbeSummary.IncrementInventoryTransfer();
                if (!Cfg.EnableInventoryTransferProbe.Value) return;
                Log.Info($"[InventoryItem] TransferOwnership << {F(__instance)} target={F(unit)} transaction={isTransaction}");
            }
            catch (Exception ex) { Log.Error($"[InventoryItem.TransferOwnership] {ex.Message}"); }
        }

        // Setup — default: count only; verbose: log each item
        private static void II_Setup_Post(object __instance)
        {
            try
            {
                ReverseProbeSummary.IncrementInventorySetup();
                if (Cfg.EnableVerboseInventoryProbe.Value)
                    Log.Info($"[InventoryItem] Setup >> {F(__instance)}");
            }
            catch (Exception ex) { Log.Error($"[InventoryItem.Setup] {ex.Message}"); }
        }

        // ==================================================================
        // 9. NextLevelTrigger
        // ==================================================================

        private static void ApplyNextLevelTriggerPatches(Harmony harmony)
        {
            var t = FindType("PerfectRandom.Sulfur.Core.LevelGeneration.NextLevelTrigger");
            if (t == null) return;

            // Functional guard — ALWAYS installed (not gated by EnableLevelProbe): a Plan B headless ghost must never
            // drive a level transition. The ghost's collider sits on the enemy-hit layer and can overlap a
            // NextLevelTrigger volume (LogOutput98); without this, the ghost following a client into an exit could
            // trip a spurious host-side transition.
            TryPatch(harmony, t, "OnTriggerEnter", Pre(nameof(NLT_OnTriggerEnter_Pre)), null);

            if (!Cfg.EnableLevelProbe.Value) return;
            TryPatch(harmony, t, "MakeTransition", Pre(nameof(NLT_MakeTransition_Pre)), null);
        }

        private static void NLT_MakeTransition_Pre(object __instance)
        {
            try { Log.Info("[NextLevelTrigger] MakeTransition <<"); }
            catch (Exception ex) { Log.Error($"[NextLevelTrigger.MakeTransition] {ex.Message}"); }
        }
        private static bool NLT_OnTriggerEnter_Pre(object __instance, Collider collider)
        {
            try
            {
                // Plan B: ghost players are host-only proxies — block them from triggering level transitions.
                if (Networking.Gameplay.RemotePlayerRegistryManager.IsGhostCollider(collider)) return false;
                if (Cfg.EnableLevelProbe.Value) Log.Info($"[NextLevelTrigger] OnTriggerEnter << collider={collider?.name}");
            }
            catch (Exception ex) { Log.Error($"[NextLevelTrigger.OnTriggerEnter] {ex.Message}"); }
            return true;
        }

        // ==================================================================
        // 10–13. Level Generation nodes
        // ==================================================================

        private static void ApplyLevelGenPatches(Harmony harmony)
        {
            if (!Cfg.EnableLevelProbe.Value) return;

            {
                var t = FindType("LevelGeneration.SpawnPlayerNode");
                if (t != null)
                {
                    TryPatch(harmony, t, "Execute",     Pre(nameof(SPNode_Execute_Pre)),    Post(nameof(SPNode_Execute_Post)));
                    TryPatch(harmony, t, "SpawnPlayer", Pre(nameof(SPNode_SpawnPlayer_Pre)), Post(nameof(SPNode_SpawnPlayer_Post)));
                }
            }
            {
                var t = FindType("LevelGeneration.SpawnEnemiesNode");
                if (t != null)
                {
                    TryPatch(harmony, t, "Execute",                Pre(nameof(SENode_Execute_Pre)),     Post(nameof(SENode_Execute_Post)));
                    TryPatch(harmony, t, "CreateAndRegisterEnemy", Pre(nameof(SENode_CreateEnemy_Pre)), Post(nameof(SENode_CreateEnemy_Post)));
                }
            }
            {
                var t = FindType("LevelGeneration.FinalizeAndMutateUnitsNode");
                if (t != null)
                {
                    TryPatch(harmony, t, "Execute",              Pre(nameof(FMNode_Execute_Pre)),  Post(nameof(FMNode_Execute_Post)));
                    TryPatch(harmony, t, "RegisterAndSpawnUnit", Pre(nameof(FMNode_RegSpawn_Pre)), Post(nameof(FMNode_RegSpawn_Post)));
                }
            }
            {
                var t = FindType("LevelGeneration.SetupLootNode");
                if (t != null)
                    TryPatch(harmony, t, "Execute", Pre(nameof(SLNode_Execute_Pre)), Post(nameof(SLNode_Execute_Post)));
            }
        }

        // SpawnPlayerNode
        private static void SPNode_Execute_Pre(object __instance)
        {
            try { ReverseProbeSummary.IncrementLevelGen(); Log.Info("[LevelGen] SpawnPlayerNode.Execute <<"); }
            catch (Exception ex) { Log.Error($"[LevelGen.SpawnPlayerNode.Execute] {ex.Message}"); }
        }
        private static void SPNode_Execute_Post(object __instance)
        {
            try { NetLevelSeed.ReportObservedGameManagerSeed("LevelGeneration.SpawnPlayerNode.Execute"); Log.Info("[LevelGen] SpawnPlayerNode.Execute >>"); }
            catch (Exception ex) { Log.Error($"[LevelGen.SpawnPlayerNode.Execute Post] {ex.Message}"); }
        }
        private static void SPNode_SpawnPlayer_Pre(object __instance, object prefab, int playerIndex, Rect viewport)
        {
            try { Log.Info($"[LevelGen] SpawnPlayer << playerIndex={playerIndex} viewport={viewport}"); }
            catch (Exception ex) { Log.Error($"[LevelGen.SpawnPlayer] {ex.Message}"); }
        }
        // SpawnPlayer returns void — no __result parameter
        private static void SPNode_SpawnPlayer_Post(object __instance)
        {
            try { Log.Info("[LevelGen] SpawnPlayer >>"); }
            catch (Exception ex) { Log.Error($"[LevelGen.SpawnPlayer Post] {ex.Message}"); }
        }

        // SpawnEnemiesNode
        private static void SENode_Execute_Pre(object __instance)
        {
            try { ReverseProbeSummary.IncrementLevelGen(); Log.Info("[LevelGen] SpawnEnemiesNode.Execute <<"); }
            catch (Exception ex) { Log.Error($"[LevelGen.SpawnEnemiesNode.Execute] {ex.Message}"); }
        }
        private static void SENode_Execute_Post(object __instance)
        {
            try { NetLevelSeed.ReportObservedGameManagerSeed("LevelGeneration.SpawnEnemiesNode.Execute"); Log.Info("[LevelGen] SpawnEnemiesNode.Execute >>"); }
            catch (Exception ex) { Log.Error($"[LevelGen.SpawnEnemiesNode.Execute Post] {ex.Message}"); }
        }
        private static void SENode_CreateEnemy_Pre(object __instance, Transform unitRoot, Vector3 spawnPosition)
        {
            try { Log.Info($"[LevelGen] CreateAndRegisterEnemy << pos={V(spawnPosition)}"); }
            catch (Exception ex) { Log.Error($"[LevelGen.CreateAndRegisterEnemy] {ex.Message}"); }
        }
        private static void SENode_CreateEnemy_Post(object __instance, object __result)
        {
            try
            {
                NetGameplayProbeManager.ReportSpawn(__result, "SpawnEnemiesNode.CreateAndRegisterEnemy", GetUnitCategory(__result));
                Log.Info($"[LevelGen] CreateAndRegisterEnemy >> {F(__result)}");
            }
            catch (Exception ex) { Log.Error($"[LevelGen.CreateAndRegisterEnemy Post] {ex.Message}"); }
        }

        // FinalizeAndMutateUnitsNode
        private static void FMNode_Execute_Pre(object __instance)
        {
            try { ReverseProbeSummary.IncrementLevelGen(); Log.Info("[LevelGen] FinalizeAndMutateUnitsNode.Execute <<"); }
            catch (Exception ex) { Log.Error($"[LevelGen.FinalizeAndMutateUnitsNode.Execute] {ex.Message}"); }
        }
        private static void FMNode_Execute_Post(object __instance)
        {
            try { NetLevelSeed.ReportObservedGameManagerSeed("LevelGeneration.FinalizeAndMutateUnitsNode.Execute"); Log.Info("[LevelGen] FinalizeAndMutateUnitsNode.Execute >>"); }
            catch (Exception ex) { Log.Error($"[LevelGen.FinalizeAndMutateUnitsNode.Execute Post] {ex.Message}"); }
        }
        private static void FMNode_RegSpawn_Pre(object __instance, object npc, object inRoom)
        {
            try
            {
                NetGameplayProbeManager.ReportSpawn(npc, "FinalizeAndMutateUnitsNode.RegisterAndSpawnUnit", GetUnitCategory(npc));
                Log.Info($"[LevelGen] RegisterAndSpawnUnit << npc={F(npc)}");
            }
            catch (Exception ex) { Log.Error($"[LevelGen.RegisterAndSpawnUnit] {ex.Message}"); }
        }
        private static void FMNode_RegSpawn_Post(object __instance)
        {
            try { Log.Info("[LevelGen] RegisterAndSpawnUnit >>"); }
            catch (Exception ex) { Log.Error($"[LevelGen.RegisterAndSpawnUnit Post] {ex.Message}"); }
        }

        // SetupLootNode
        private static void SLNode_Execute_Pre(object __instance)
        {
            try { ReverseProbeSummary.IncrementLevelGen(); Log.Info("[LevelGen] SetupLootNode.Execute <<"); }
            catch (Exception ex) { Log.Error($"[LevelGen.SetupLootNode.Execute] {ex.Message}"); }
        }
        private static void SLNode_Execute_Post(object __instance)
        {
            try { NetLevelSeed.ReportObservedGameManagerSeed("LevelGeneration.SetupLootNode.Execute"); Log.Info("[LevelGen] SetupLootNode.Execute >>"); }
            catch (Exception ex) { Log.Error($"[LevelGen.SetupLootNode.Execute Post] {ex.Message}"); }
        }
    }
}
