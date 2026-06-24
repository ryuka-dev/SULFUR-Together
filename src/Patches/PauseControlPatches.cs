using System;
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
