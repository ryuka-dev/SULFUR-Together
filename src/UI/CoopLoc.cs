using System;
using HarmonyLib;

namespace SULFURTogether.UI
{
    /// <summary>
    /// Project-owned localization boundary. Every player-facing string this mod shows goes through here so the
    /// rest of the code never references a third-party localization type directly (CLAUDE.md §2).
    ///
    /// Under the hood this reuses <c>SULFUR Native UI Lib</c>'s public plugin-localization system
    /// (<c>Ryuka.Sulfur.NativeUI.SulfurLocalization</c>): it loads our <c>lang/*.json</c> from next to our DLL,
    /// follows the game's current language (via I2 Localization), and resolves a key with a built-in
    /// <c>current-code → base-code → "en"</c> fallback chain. See <c>Docs/Localization.md</c>.
    ///
    /// The lib is a SOFT dependency, so this is wired by reflection (same seam as <see cref="Plugin.WireCoopUi"/>):
    /// when the lib is absent every lookup simply returns the English fallback that each call site passes in, so
    /// behavior is unchanged and the mod still runs. Fonts are handled by the lib (its rows/toasts/banner use the
    /// current-language font) and by <see cref="NativeFontSampler"/> (our uGUI overlays) — not this class.
    /// </summary>
    internal static class CoopLoc
    {
        // Bound to SulfurLocalization.Get(pluginGuid, key, fallback). Null until wired / when the lib is absent.
        private static Func<string, string, string, string>? _get;
        // Bound to SulfurLocalization.LanguageVersion getter — bumps when the game language changes.
        private static Func<int>? _languageVersion;

        /// <summary>
        /// Resolve the lib's localization API by reflection and load our own <c>lang/*.json</c>. Safe to call when
        /// the lib is absent (no-op → English fallbacks). <paramref name="pluginAssemblyLocation"/> is this
        /// plugin's DLL path (<c>Info.Location</c>); the lib reads <c>lang/</c> next to it.
        /// </summary>
        public static void Wire(string pluginAssemblyLocation)
        {
            try
            {
                var type = AccessTools.TypeByName("Ryuka.Sulfur.NativeUI.SulfurLocalization");
                if (type == null)
                {
                    Plugin.Log?.Info("[CoopLoc] SulfurLocalization not present — player-facing text stays English.");
                    return;
                }

                var load = AccessTools.Method(type, "LoadPluginLocalization", new[] { typeof(string), typeof(string) });
                if (load != null && !string.IsNullOrEmpty(pluginAssemblyLocation))
                    load.Invoke(null, new object[] { ModInfo.GUID, pluginAssemblyLocation });
                else
                    Plugin.Log?.Warn("[CoopLoc] LoadPluginLocalization missing or no assembly location — lang files not loaded.");

                var get = AccessTools.Method(type, "Get", new[] { typeof(string), typeof(string), typeof(string) });
                if (get != null)
                    _get = (Func<string, string, string, string>)Delegate.CreateDelegate(
                        typeof(Func<string, string, string, string>), get);

                var langVerGetter = AccessTools.PropertyGetter(type, "LanguageVersion");
                if (langVerGetter != null)
                    _languageVersion = (Func<int>)Delegate.CreateDelegate(typeof(Func<int>), langVerGetter);

                Plugin.Log?.Info($"[CoopLoc] localization wired (lookup={_get != null}, languageVersion={_languageVersion != null}).");
            }
            catch (Exception e)
            {
                Plugin.Log?.Warn($"[CoopLoc] wire failed — player-facing text stays English: {e.Message}");
            }
        }

        /// <summary>Localized string for <paramref name="key"/>, or the English <paramref name="fallback"/> when
        /// the key/lang/lib is missing. The fallback is also the canonical <c>en.json</c> source text.</summary>
        public static string Get(string key, string fallback)
        {
            var get = _get;
            if (get == null) return fallback;
            try { return get(ModInfo.GUID, key, fallback); }
            catch { return fallback; }
        }

        /// <summary>Like <see cref="Get"/>, then substitutes <c>{token}</c> placeholders. Tokens are kept identical
        /// across every language so a translated template still interpolates correctly.</summary>
        public static string Format(string key, string fallback, params (string token, string value)[] args)
        {
            string text = Get(key, fallback);
            if (args != null)
            {
                foreach (var (token, value) in args)
                    text = text.Replace("{" + token + "}", value ?? "");
            }
            return text;
        }

        /// <summary>Version counter that increments when the game language changes; 0 when the lib is absent.
        /// Already-built UI (the connect page) polls this to know when to re-apply its localized labels.</summary>
        public static int LanguageVersion
        {
            get
            {
                var v = _languageVersion;
                if (v == null) return 0;
                try { return v(); } catch { return 0; }
            }
        }
    }
}
