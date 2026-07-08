using TMPro;
using UnityEngine;

namespace SULFURTogether.UI
{
    /// <summary>
    /// Samples the font off any currently active native TextMeshProUGUI — the same trick the SULFUR Native UI Lib
    /// uses for its own banner/toasts (Docs/Localization.md "Producer note"): the game already keeps its own UI
    /// text components on the correct current-language font (CJK fallback chain included), so copying that
    /// reference is simpler and more correct than trying to track the language ourselves. Falls back to the
    /// project's TMP default if no active native text can be found (e.g. a bare loading screen with no TMP
    /// components at all).
    ///
    /// Promoted out of RunStatsOverlayManager (RS-4) so DownedRescueOverlay (DR-2) doesn't duplicate it — both
    /// overlays need the identical "current native UI font" and should never drift into two different answers.
    /// </summary>
    internal static class NativeFontSampler
    {
        public static TMP_FontAsset? ResolveNativeFont()
        {
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
