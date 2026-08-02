using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using SULFURTogether.Networking;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// Phase 5.7-NP — Minecraft-LAN-style "no world pause" in co-op.
    ///
    /// AUDIT of SULFUR's pause (decompiled PerfectRandom.Sulfur.Core):
    ///   • Central API <c>GameManager.ModifyGamePauseState(LockStatePadlock, bool)</c> adds/removes a padlock to a
    ///     <c>gamePausedByState</c> set, then (when not Loading) calls <c>SetState(Paused)</c> if any padlock is held,
    ///     else <c>SetState(Running)</c>. <c>GameManager.Update()</c> maps gameState→Time.timeScale: Running→running,
    ///     Paused/Loading→0. So a single padlock stops the whole world.
    ///   • World-pausing callers: Inventory/backpack open → padlock <c>Inventory</c>; ESC menu (<c>PauseGame</c>) →
    ///     <c>Paused</c>; F3 dev tools → <c>DevTools</c>; NPC dialog → <c>Dialog</c>. (Loading/Cinematic/Vehicle/etc.
    ///     are left alone — Loading must still stop time during real scene loads.)
    ///   • <c>MenuManager.OnApplicationFocus(false)</c> sets <c>Time.timeScale = 0</c> directly on lost window focus.
    ///
    /// FIX: while a co-op session is active, (a) drop the four gameplay pause padlocks at the source so gameState stays
    /// Running (UI/cursor/controller-lock are separate calls and still work — the bag still opens, you still can't move,
    /// but enemies and boss timelines keep advancing on both ends), and (b) ignore the focus-loss pause + enable
    /// runInBackground so a second instance on the same PC keeps simulating when unfocused.
    /// </summary>
    internal static class PauseControlPatches
    {
        private static int _logCount;

        // Tracks the previous suppression state so we can act on the false→true edge (a session just became active).
        private static bool _wasSuppressing;

        // Cached reflection for the retroactive padlock release (resolved lazily on first need).
        private static Type _gmType;
        private static System.Reflection.MethodInfo _modifyMethod;
        private static Type _padlockEnumType;
        private static Func<object> _gmInstanceGetter;

        public static void Apply(Harmony harmony)
        {
            // A second instance on the same machine must keep ticking while unfocused (also helps real alt-tab).
            try { Application.runInBackground = true; } catch { }

            var gm = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.GameManager");
            if (gm != null)
            {
                var modify = AccessTools.Method(gm, "ModifyGamePauseState");
                if (modify != null)
                {
                    try
                    {
                        harmony.Patch(modify, prefix: new HarmonyMethod(
                            typeof(PauseControlPatches).GetMethod(nameof(ModifyGamePauseState_Pre),
                                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));
                        Plugin.Log.Info("[PauseControl] Patched GameManager.ModifyGamePauseState");
                    }
                    catch (Exception ex) { Plugin.Log.Warn($"[PauseControl] ModifyGamePauseState patch failed: {ex.Message}"); }
                }
                else Plugin.Log.Warn("[PauseControl] GameManager.ModifyGamePauseState not found");
            }
            else Plugin.Log.Warn("[PauseControl] GameManager type not found");

            var menu = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.MenuManager");
            if (menu != null)
            {
                var focus = AccessTools.Method(menu, "OnApplicationFocus");
                if (focus != null)
                {
                    try
                    {
                        harmony.Patch(focus, prefix: new HarmonyMethod(
                            typeof(PauseControlPatches).GetMethod(nameof(OnApplicationFocus_Pre),
                                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));
                        Plugin.Log.Info("[PauseControl] Patched MenuManager.OnApplicationFocus");
                    }
                    catch (Exception ex) { Plugin.Log.Warn($"[PauseControl] OnApplicationFocus patch failed: {ex.Message}"); }
                }
            }

            ApplyDevToolsAiPatches(harmony);
        }

        // ----------------------------------------------------------------- NP-4: F3 dev tools freeze every enemy

        // The dev tools (F3) are a player-facing creative mode here, not just a developer aid — and opening them stops
        // every enemy dead, on every end, because the HOST's behaviour trees stop. This is NOT the pause padlock (that
        // one is already suppressed above): `BehaviourTreeCode_Gameplay.ManualUpdate` — the driver that ticks every
        // NPC's behaviour tree, registered into `ProjectileSystem.RunWhileWritingInstanceData` — opens with a hard
        //     if (StaticInstance<DevToolsManager>.Instance.shouldShow) return;
        // so no amount of keeping gameState Running or Time.timeScale live can reach it. `GooEyeManager.ManualUpdate`
        // carries the same guard.
        //
        // Rather than reimplement those loops (they walk private `trees`/`npcs` lists), splice a single call in right
        // after the `shouldShow` read and let our own predicate decide whether the freeze still applies. That is one
        // instruction against an IL shape we do not otherwise depend on, it costs nothing per frame (no reflection),
        // and outside a co-op session it returns the value the game just computed — single-player is untouched.
        private static void ApplyDevToolsAiPatches(Harmony harmony)
        {
            TryPatchDevToolsGuard(harmony, "PerfectRandom.Sulfur.Gameplay.BehaviourTreeCode_Gameplay", "ManualUpdate");
            TryPatchDevToolsGuard(harmony, "PerfectRandom.Sulfur.Gameplay.GooEyeManager", "ManualUpdate");
        }

        private static void TryPatchDevToolsGuard(Harmony harmony, string typeName, string methodName)
        {
            try
            {
                var type = AccessTools.TypeByName(typeName);
                if (type == null) { Plugin.Log.Warn($"[PauseControl] {typeName} not found — F3 will still freeze its AI"); return; }
                var method = AccessTools.Method(type, methodName);
                if (method == null) { Plugin.Log.Warn($"[PauseControl] {typeName}.{methodName} not found — F3 will still freeze its AI"); return; }

                _guardSpliceCount = 0;
                harmony.Patch(method, transpiler: new HarmonyMethod(
                    typeof(PauseControlPatches).GetMethod(nameof(DevToolsGuard_Transpiler),
                        BindingFlags.Static | BindingFlags.NonPublic)));

                if (_guardSpliceCount > 0)
                    Plugin.Log.Info($"[PauseControl] Patched {type.Name}.{methodName} — dev-tools AI freeze is now co-op aware ({_guardSpliceCount} guard(s))");
                else
                    Plugin.Log.Warn($"[PauseControl] {type.Name}.{methodName} has no shouldShow guard to splice — F3 behaviour unchanged there");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[PauseControl] {typeName}.{methodName} transpiler failed: {ex.Message}"); }
        }

        private static int _guardSpliceCount;

        private static IEnumerable<CodeInstruction> DevToolsGuard_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var hook = typeof(PauseControlPatches).GetMethod(nameof(DevToolsShouldFreezeAi),
                BindingFlags.Static | BindingFlags.Public);

            foreach (var ins in instructions)
            {
                yield return ins;
                // `... .shouldShow` leaves a bool on the stack; our hook consumes and replaces it, so the surrounding
                // branch is untouched whatever shape it has (a bare `if` here, one arm of an `||` chain there).
                if ((ins.opcode == OpCodes.Call || ins.opcode == OpCodes.Callvirt)
                    && ins.operand is MethodInfo m
                    && string.Equals(m.Name, "get_shouldShow", StringComparison.Ordinal)
                    && hook != null)
                {
                    yield return new CodeInstruction(OpCodes.Call, hook);
                    _guardSpliceCount++;
                }
            }
        }

        /// <summary>NP-4: does the dev-tools panel still stop AI? Only outside a co-op session. Public because the
        /// transpiler above emits a call to it.</summary>
        public static bool DevToolsShouldFreezeAi(bool shouldShow)
        {
            if (!shouldShow) return false;      // panel closed — nothing changes
            try { return !SuppressPause(); }    // linked session — the other player's world keeps running
            catch { return true; }              // anything unexpected → vanilla behaviour
        }

        /// <summary>
        /// Phase 5.7-NP2 — seamless un-pause when a session starts with a menu already open. The add-blocking prefix
        /// only stops <em>future</em> pause padlocks; a menu opened <em>before</em> the session (e.g. the Options menu
        /// you press "Create game" from) has already placed its padlock, so the world stays frozen until that menu is
        /// closed and reopened. On the false→true suppression edge we retroactively drop the four gameplay padlocks so
        /// the world resumes immediately, with the menu still open — no close/reopen needed. Driven from Plugin.Update.
        /// </summary>
        public static void Tick()
        {
            bool now = SuppressPause();
            if (now && !_wasSuppressing)
                ReleaseHeldGameplayPadlocks();
            _wasSuppressing = now;
        }

        /// <summary>Remove the four gameplay pause padlocks if currently held, so gameState falls back to Running while
        /// any open menu UI stays up. Removal is always permitted by the prefix and is idempotent (a later menu-close
        /// removing an absent padlock is a harmless no-op), so this is safe to call on the session-start edge.</summary>
        private static void ReleaseHeldGameplayPadlocks()
        {
            try
            {
                if (_gmType == null)
                {
                    _gmType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.GameManager");
                    _modifyMethod = _gmType != null ? AccessTools.Method(_gmType, "ModifyGamePauseState") : null;
                    _padlockEnumType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.LockStatePadlock");
                    _gmInstanceGetter = BuildInstanceGetter(_gmType);
                }
                if (_modifyMethod == null || _padlockEnumType == null || _gmInstanceGetter == null) return;

                object gm = _gmInstanceGetter();
                if (gm == null) return;

                foreach (var name in new[] { "Inventory", "Paused", "DevTools", "Dialog" })
                {
                    object padlock;
                    try { padlock = Enum.Parse(_padlockEnumType, name); }
                    catch { continue; } // enum member absent in this build — skip it
                    _modifyMethod.Invoke(gm, new[] { padlock, (object)false }); // remove → SetState(Running) if last
                }

                if (Plugin.Cfg.LogPauseSuppression.Value)
                    Plugin.Log.Info("[PauseControl] session started — released held gameplay pause padlocks (menu stays open, world resumes)");
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[PauseControl] retroactive padlock release failed: {ex.Message}");
            }
        }

        /// <summary>Resolve the static <c>GameManager.Instance</c> accessor as either a property or a field.</summary>
        private static Func<object> BuildInstanceGetter(Type gmType)
        {
            if (gmType == null) return null;
            var prop = AccessTools.Property(gmType, "Instance");
            if (prop != null && prop.GetGetMethod(true) != null) return () => prop.GetValue(null);
            var field = AccessTools.Field(gmType, "Instance");
            if (field != null) return () => field.GetValue(null);
            return null;
        }

        /// <summary>True when the local instance is in an active co-op session (so pausing it would desync the other).</summary>
        private static bool SuppressPause()
        {
            try
            {
                if (!Plugin.Cfg.DisablePauseInMultiplayer.Value) return false;
                switch (NetConfig.GetMode())
                {
                    case NetMode.Client: return NetLinkState.ClientLinked;
                    case NetMode.Host:   return NetLinkState.HostLinked;
                    default:             return false; // Off = single-player, keep normal pause
                }
            }
            catch { return false; }
        }

        // Block the four gameplay padlocks that stop world time. lockState arrives as the boxed LockStatePadlock enum;
        // compare by name so we never hard-code its integer values. Only block ADDING (state==true) — always let removes
        // run so nothing can get stuck paused. Loading / Cinematic / Vehicle / etc. are intentionally NOT blocked.
        private static bool ModifyGamePauseState_Pre(object lockState, bool state)
        {
            if (!state) return true;            // removing a padlock — always allow
            if (!SuppressPause()) return true;  // single-player — normal pause

            string name = lockState?.ToString() ?? "";
            if (name == "Inventory" || name == "Paused" || name == "DevTools" || name == "Dialog")
            {
                if (Plugin.Cfg.LogPauseSuppression.Value && _logCount++ < 60)
                    Plugin.Log.Info($"[PauseControl] suppressed world pause padlock={name} (multiplayer: world keeps running)");
                return false; // skip — do not add the padlock, gameState stays Running, Time.timeScale stays live
            }
            return true; // other padlocks (Loading, Cinematic, ...) behave normally
        }

        // The game pauses on lost window focus. In co-op that freezes the other player's world; skip it (runInBackground
        // keeps Update ticking, and GameManager.Update keeps Time.timeScale live while gameState==Running).
        private static bool OnApplicationFocus_Pre(bool hasFocus)
        {
            if (!hasFocus && SuppressPause())
            {
                if (Plugin.Cfg.LogPauseSuppression.Value && _logCount++ < 60)
                    Plugin.Log.Info("[PauseControl] ignored focus-loss pause (multiplayer)");
                return false; // do not zero Time.timeScale on focus loss
            }
            return true;
        }
    }
}
