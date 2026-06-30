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
