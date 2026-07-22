using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using SULFURTogether.Networking;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// AWAIT-3: retire the abandoned <c>LevelGeneration.ShowLevelNode</c> when a peer is pulled out of the
    /// press-to-continue screen.
    ///
    /// <para><b>The defect.</b> <c>ShowLevelNode</c> is the last generation node. It parks on
    /// <c>while (gameManager.awaitingStartLevel) yield return null;</c> and only afterwards flips the world live —
    /// <c>SetState(Running)</c>, <c>SetTimeScale(1f)</c>, <c>LoadingFade(false)</c>, and re-enabling every NPC in
    /// the level context. In single player nothing can leave that screen except the keypress. In co-op a peer can
    /// be led away while parked, and <c>StartLevelRoutineGraph</c> begins with
    /// <c>ClearLevel(); SetAwaitBeforeStartLevel(false);</c> — which releases the OLD node's wait. Nothing ever
    /// stops that coroutine, so it resumes DURING the new level's teardown and generation and runs its tail:
    /// gameplay state goes Running mid-generation, the fresh black fade is torn off (the loading overlay stays up,
    /// so the player sees a half-built level behind the "press to continue" art), and the previous level's
    /// already-destroyed NPCs are re-enabled and pushed back into <c>GameManager.aliveNpcs</c>.</para>
    ///
    /// <para><b>The fix.</b> When a level transition starts while this peer is parked, arm a one-shot abandon.
    /// The next <c>MoveNext</c> of the stale node returns false, ending the coroutine before its tail runs. The
    /// node's remaining side effects are all re-applied by the incoming level's own <c>ShowLevelNode</c>
    /// (<c>Physics.simulationMode</c>, timeScale, game state, fade), and <c>SetAwaitBeforeStartLevel(false)</c>
    /// still restores input and clears the prompt, so nothing is lost by cutting it short.</para>
    ///
    /// <para>This is functional, not diagnostic, so it is applied unconditionally — no config gate.</para>
    /// </summary>
    internal static class AwaitStartLevelPatches
    {
        // A stale node is retired on the very next frame, so this only ever spans a frame or two. The frame budget
        // is a safety valve: if the abandoned coroutine is never pumped again (already stopped by something else),
        // the arm expires instead of lying in wait for the NEXT level's legitimate ShowLevelNode.
        private const int ArmFrameBudget = 10;

        private static bool   _armed;
        private static int    _armedFrame;
        private static string _armSource = "";

        public static int AbandonedNodesRetired;
        public static int ArmsExpiredUnused;

        public static void Apply(Harmony harmony)
        {
            Type? nodeType = FindShowLevelNode();
            if (nodeType == null)
            {
                Plugin.Log.Warn("[AwaitStart] ShowLevelNode type not found — stale-node retirement inactive.");
                return;
            }

            Type? sm = ResolveExecuteStateMachine(nodeType);
            MethodInfo? moveNext = sm?.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (moveNext == null)
            {
                Plugin.Log.Warn($"[AwaitStart] {nodeType.FullName}.Execute state machine not resolved — stale-node retirement inactive.");
                return;
            }

            try
            {
                harmony.Patch(moveNext, prefix: new HarmonyMethod(
                    typeof(AwaitStartLevelPatches).GetMethod(nameof(ShowLevelNode_MoveNext_Pre), BindingFlags.Static | BindingFlags.NonPublic)));
                Plugin.Log.Info($"[AwaitStart] patched {sm!.FullName}.MoveNext (stale ShowLevelNode retirement)");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[AwaitStart] ShowLevelNode MoveNext patch failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Called from the authoritative level-transition entry (<c>SwitchLevelRoutine</c>) — every route that
        /// reaches generation passes through it. A no-op unless this peer is actually parked at the prompt.
        /// </summary>
        public static void ArmIfParked(string source)
        {
            try
            {
                if (!NetAwaitStartLevel.IsLocalAwaitingStartLevel) return;
                _armed = true;
                _armedFrame = Time.frameCount;
                _armSource = source ?? "";
                Plugin.Log.Info($"[AwaitStart] transition while parked at press-to-continue — retiring stale ShowLevelNode source={_armSource}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[AwaitStart] arm failed: {ex.Message}"); }
        }

        // Returning false skips the original MoveNext; __result=false ends the coroutine.
        private static bool ShowLevelNode_MoveNext_Pre(ref bool __result)
        {
            if (!_armed) return true;

            try
            {
                if (Time.frameCount - _armedFrame > ArmFrameBudget)
                {
                    // The stale node never came back to be retired — drop the arm rather than risk killing the
                    // incoming level's own ShowLevelNode.
                    _armed = false;
                    ArmsExpiredUnused++;
                    Plugin.Log.Warn($"[AwaitStart] arm expired unused after {ArmFrameBudget} frames (source={_armSource}); stale node was not pumped");
                    return true;
                }

                _armed = false;
                AbandonedNodesRetired++;
                Plugin.Log.Info($"[AwaitStart] retired stale ShowLevelNode (source={_armSource}) — its tail will not run against the new level");
                __result = false;
                return false;
            }
            catch (Exception ex)
            {
                _armed = false;
                Plugin.Log.Warn($"[AwaitStart] retire check failed: {ex.Message}");
                return true;
            }
        }

        public static string FormatCounters()
            => $"staleNodesRetired={AbandonedNodesRetired} armsExpired={ArmsExpiredUnused}";

        // ---- type resolution ----

        private static Type? FindShowLevelNode()
        {
            string[] candidates =
            {
                "LevelGeneration.ShowLevelNode",
                "PerfectRandom.Sulfur.Core.LevelGeneration.ShowLevelNode",
                "ShowLevelNode",
            };
            foreach (var full in candidates)
            {
                var t = AccessTools.TypeByName(full);
                if (t != null) return t;
            }

            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException rtle) { types = rtle.Types.Where(t => t != null).ToArray()!; }
                    foreach (var t in types)
                        if (t != null && t.Name == "ShowLevelNode") return t;
                }
            }
            catch { }
            return null;
        }

        private static Type? ResolveExecuteStateMachine(Type nodeType)
        {
            try
            {
                var execMi = nodeType.GetMethod("Execute", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var attr = execMi?.GetCustomAttributes().FirstOrDefault(a => a.GetType().Name.Contains("StateMachine"));
                if (attr != null && attr.GetType().GetProperty("StateMachineType")?.GetValue(attr) is Type smt)
                    return smt;

                return nodeType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(nt => typeof(IEnumerator).IsAssignableFrom(nt) && nt.Name.Contains("Execute"));
            }
            catch { return null; }
        }
    }
}
