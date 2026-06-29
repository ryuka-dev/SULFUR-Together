using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SULFURTogether.Networking
{
    /// <summary>
    /// Closes the in-game pause/options menu and returns to gameplay, mirroring the player pressing ESC out of
    /// the options screen and then again to resume. The menu is two-level: the pause menu
    /// (<c>PerfectRandom.Sulfur.Core.UI.PauseMenu</c> — Resume / Options / Quit) hosts the options sub-screen
    /// (<c>PerfectRandom.Sulfur.Core.OptionsScreen</c>) where the connect page lives. Closing it cleanly needs
    /// <c>OptionsScreen.Hide()</c> (when its <c>IsShown</c> is set) followed by <c>GameManager.ResumeGame()</c> —
    /// a lone <c>ResumeGame</c> leaves the options sub-screen up and wedges the menu (Log192/193).
    ///
    /// Used both when the local player starts a co-op action from the connect page (the active side) and when
    /// this side is <i>passively</i> dragged into a host-driven level load (auto-follow / relayed transition) —
    /// in either case a menu left open over the black-screen load can wedge the game if the player hits Space.
    /// Pure game reflection (no UI-lib dependency), so it is always compiled and callable from the netcode path.
    /// </summary>
    internal static class CoopMenu
    {
        private static bool _resolved;
        private static Type _optionsScreenType;
        private static Type _pauseMenuType;
        private static MethodInfo _optionsHide;
        private static PropertyInfo _optionsIsShown;
        private static MethodInfo _resumeGame;
        private static PropertyInfo _gmInstance;

        private static void Resolve()
        {
            if (_resolved) return;
            try
            {
                _optionsScreenType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.OptionsScreen");
                if (_optionsScreenType != null)
                {
                    _optionsHide    = AccessTools.Method(_optionsScreenType, "Hide");
                    _optionsIsShown = AccessTools.Property(_optionsScreenType, "IsShown");
                }

                _pauseMenuType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.UI.PauseMenu");

                var gmType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.GameManager");
                if (gmType != null)
                {
                    _gmInstance = AccessTools.Property(gmType, "Instance");
                    _resumeGame = AccessTools.Method(gmType, "ResumeGame");
                }

                // Latch only once the resume path (the essential part) resolved; else retry next call.
                if (_resumeGame != null && _gmInstance != null) _resolved = true;
            }
            catch { }
        }

        /// <summary>True when the pause menu or its options sub-screen is currently open.</summary>
        public static bool IsOpen()
        {
            Resolve();
            return IsOptionsShown() || IsPauseMenuActive();
        }

        /// <summary>Close the pause/options menu if one is open. Safe no-op otherwise.</summary>
        public static void CloseIfOpen(string reason)
        {
            try
            {
                Resolve();

                bool optionsShown = IsOptionsShown(out var optionsScreen);
                bool pauseActive  = IsPauseMenuActive();
                if (!optionsShown && !pauseActive) return;

                bool acted = false;

                // Layer 2: back out of the options sub-screen first (where the connect page lives).
                if (optionsShown && _optionsHide != null && optionsScreen != null)
                {
                    _optionsHide.Invoke(optionsScreen, null);
                    acted = true;
                }

                // Layer 1: close the pause menu and resume gameplay.
                if (_resumeGame != null && _gmInstance != null)
                {
                    object gm = _gmInstance.GetValue(null, null);
                    if (gm != null) { _resumeGame.Invoke(gm, null); acted = true; }
                }

                if (acted) Plugin.Log?.Info($"[CoopUi] closed in-game menu (reason={reason}).");
            }
            catch (Exception e) { Plugin.Log?.Warn($"[CoopUi] close menu failed: {e.Message}"); }
        }

        private static bool IsOptionsShown() => IsOptionsShown(out _);

        private static bool IsOptionsShown(out UnityEngine.Object optionsScreen)
        {
            optionsScreen = null;
            if (_optionsScreenType == null || _optionsIsShown == null) return false;
            try
            {
                optionsScreen = UnityEngine.Object.FindObjectOfType(_optionsScreenType);
                if (optionsScreen == null) return false;
                return _optionsIsShown.GetValue(optionsScreen, null) is bool b && b;
            }
            catch { return false; }
        }

        // The PauseMenu component is active only while the pause-menu screen itself is visible (it is disabled
        // while the options sub-screen is shown and while the menu is closed), so an active instance == open.
        private static bool IsPauseMenuActive()
        {
            if (_pauseMenuType == null) return false;
            try
            {
                var pm = UnityEngine.Object.FindObjectOfType(_pauseMenuType);
                if (pm == null) return false;
                return pm is Behaviour bh ? bh.isActiveAndEnabled : true;
            }
            catch { return false; }
        }
    }
}
