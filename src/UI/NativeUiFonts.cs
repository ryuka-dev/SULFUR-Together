#if NATIVE_UI_LIB
using Ryuka.Sulfur.NativeUI;
using TMPro;

namespace SULFURTogether.UI
{
    /// <summary>The game's fonts by role, through SULFUR Native UI Lib (0.11.0+). Only called after the plugin checked the
    /// lib's <c>SulfurFonts</c> type is loaded, so the soft dependency holds.</summary>
    internal static class NativeUiFonts
    {
        /// <summary>The game's current serif semibold face (small HUD text, dialogue body), with the lib's CJK and symbol
        /// fallback attached, so it renders every language.</summary>
        public static TMP_FontAsset? OverlayFont() => SulfurFonts.Get(SulfurFontRole.SerifSemibold);
    }
}
#endif
