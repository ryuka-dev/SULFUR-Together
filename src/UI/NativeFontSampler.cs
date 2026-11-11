using TMPro;
using UnityEngine;

namespace SULFURTogether.UI
{
    /// <summary>
    /// The font for SULFUR Together's own overlays. With SULFUR Native UI Lib loaded it is the game's font for a
    /// fixed role, with the lib's CJK and symbol fallback attached (<see cref="RoleFont"/>, wired by the plugin).
    ///
    /// Without the lib it samples the font off a currently active native TextMeshProUGUI. That is a guess: which
    /// text comes first is arbitrary, and since game 1.0.4 it can be a Latin-only display face (Fredoka), which
    /// renders Chinese, Japanese or Korean text as blanks. The lib path exists because of that. Falls back to the
    /// project's TMP default if no active native text can be found (e.g. a bare loading screen with no TMP
    /// components at all).
    ///
    /// Promoted out of RunStatsOverlayManager (RS-4) so DownedRescueOverlay (DR-2) doesn't duplicate it — both
    /// overlays need the identical "current native UI font" and should never drift into two different answers.
    /// </summary>
    internal static class NativeFontSampler
    {
        /// <summary>Set by the plugin when SULFUR Native UI Lib's role fonts are available; null otherwise.</summary>
        internal static System.Func<TMP_FontAsset?>? RoleFont;

        public static TMP_FontAsset? ResolveNativeFont()
        {
            if (RoleFont != null)
            {
                try { var f = RoleFont(); if (f != null) return f; }
                catch { /* fall through to sampling */ }
            }

            try
            {
                foreach (var tmp in Object.FindObjectsByType<TextMeshProUGUI>(FindObjectsSortMode.None))
                {
                    if (tmp == null || tmp.font == null) continue;
                    if (!tmp.gameObject.activeInHierarchy) continue;
                    return tmp.font;
                }
            }
            catch { /* best-effort — fall through to default */ }

            try { return TMP_Settings.defaultFontAsset; }
            catch { return null; }
        }
    }
}
