using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

namespace SULFURTogether.Networking
{
    /// <summary>
    /// Phase 5.6-CL: minimal native loading-fade control for the client transition gate.
    ///
    /// When a client-initiated level load is intercepted (the client walked into a NextLevelTrigger but we
    /// block the local generation and wait for the host to LEAD), the game's own loading flow never runs, so
    /// without this the player just stands frozen in the old level while the relay round-trips — looks broken.
    ///
    /// We show the game's OWN black fade (UIManager.LoadingFade(true) → LoadingFade.FadeOut(black), a single
    /// animator bool) plus the loading overlay, exactly like OnCompleteLevelRoutine does. The level-generation
    /// completion (in the level-gen graph) fades back in and hides the overlay on its own, so the black
    /// SELF-CLEARS in the success path when the host-driven follow finishes loading — Hide() is only a safety
    /// net for the timeout/abort/reset paths so the client can never be stuck behind a black screen.
    ///
    /// All reflection so it fails safe on game updates: if anything cannot be resolved the call is a no-op.
    /// </summary>
    internal static class NetLoadingFade
    {
        // Cached (stable across scenes): method/enum metadata. The UIManager instance + overlay field are
        // re-fetched on every call because the overlay object is recreated per scene.
        private static bool _metaResolved;
        private static bool _metaFailed;
        private static Type? _uiManagerType;
        private static MethodInfo? _instanceGetter; // StaticInstance<UIManager>.Instance
        private static MethodInfo? _loadingFade;     // void LoadingFade(bool, LoadingMode)
        private static object? _normalLoadingMode;   // LoadingMode.Normal
        private static FieldInfo? _loadingOverlayField;
        private static MethodInfo? _overlaySetState;  // void SetState(UIState)
        private static MethodInfo? _overlaySetText;   // void SetText(string, string)
        private static object? _uiStateShown;
        private static object? _uiStateHidden;

        public static bool Active { get; private set; }

        public static void Show()
        {
            try
            {
                if (!ResolveMeta()) return;
                object? ui = GetUiManager();
                if (ui == null) return;

                // Black fade (the essential, self-clearing piece).
                _loadingFade!.Invoke(ui, new[] { (object)true, _normalLoadingMode! });

                // Loading overlay (art / hints) — best-effort, mirrors OnCompleteLevelRoutine.
                object? overlay = _loadingOverlayField?.GetValue(ui);
                if (overlay != null)
                {
                    try { _overlaySetText?.Invoke(overlay, new object[] { "", "" }); } catch { }
                    try { if (_uiStateShown != null) _overlaySetState?.Invoke(overlay, new[] { _uiStateShown }); } catch { }
                }

                Active = true;
                Plugin.Log.Info("[ClientLoadFade] shown (black fade + loading overlay)");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[ClientLoadFade] show failed: {ex.Message}"); }
        }

        public static void Hide()
        {
            try
            {
                if (!Active) return;
                Active = false;
                if (!ResolveMeta()) return;
                object? ui = GetUiManager();
                if (ui == null) return;

                _loadingFade!.Invoke(ui, new[] { (object)false, _normalLoadingMode! }); // FadeIn

                object? overlay = _loadingOverlayField?.GetValue(ui);
                if (overlay != null && _uiStateHidden != null)
                {
                    try { _overlaySetState?.Invoke(overlay, new[] { _uiStateHidden }); } catch { }
                }
                Plugin.Log.Info("[ClientLoadFade] hidden (fade back in)");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[ClientLoadFade] hide failed: {ex.Message}"); }
        }

        private static object? GetUiManager()
        {
            try { return _instanceGetter?.Invoke(null, null); }
            catch { return null; }
        }

        private static bool ResolveMeta()
        {
            if (_metaResolved) return !_metaFailed;
            _metaResolved = true;
            try
            {
                _uiManagerType = FindType(
                    "PerfectRandom.Sulfur.Core.UI.UIManager, PerfectRandom.Sulfur.Core",
                    "PerfectRandom.Sulfur.Core.UI.UIManager");
                if (_uiManagerType == null) { _metaFailed = true; return false; }

                // StaticInstance<UIManager>.Instance (declared on the generic base — FlattenHierarchy).
                const BindingFlags sflags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;
                var instanceProp = _uiManagerType.GetProperty("Instance", sflags);
                _instanceGetter = instanceProp?.GetGetMethod(true);
                if (_instanceGetter == null) { _metaFailed = true; return false; }

                const BindingFlags iflags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                foreach (var m in _uiManagerType.GetMethods(iflags))
                {
                    if (m.Name != "LoadingFade") continue;
                    var p = m.GetParameters();
                    if (p.Length >= 1 && p[0].ParameterType == typeof(bool)) { _loadingFade = m; break; }
                }
                if (_loadingFade == null) { _metaFailed = true; return false; }

                // LoadingMode.Normal from the LoadingFade second parameter's enum type.
                var fp = _loadingFade.GetParameters();
                if (fp.Length >= 2 && fp[1].ParameterType.IsEnum)
                    _normalLoadingMode = ParseEnum(fp[1].ParameterType, "Normal");
                if (_normalLoadingMode == null && fp.Length >= 2)
                    _normalLoadingMode = Activator.CreateInstance(fp[1].ParameterType); // default(LoadingMode)

                // loadingOverlay field + LoadingOverlay.SetState/SetText + UIState.Shown/Hidden (all best-effort).
                _loadingOverlayField = _uiManagerType.GetField("loadingOverlay", iflags);
                Type? overlayType = _loadingOverlayField?.FieldType;
                if (overlayType != null)
                {
                    foreach (var m in overlayType.GetMethods(iflags))
                    {
                        var p = m.GetParameters();
                        if (m.Name == "SetState" && p.Length == 1 && p[0].ParameterType.IsEnum)
                        {
                            _overlaySetState = m;
                            _uiStateShown = ParseEnum(p[0].ParameterType, "Shown");
                            _uiStateHidden = ParseEnum(p[0].ParameterType, "Hidden");
                        }
                        else if (m.Name == "SetText" && p.Length >= 1 && p[0].ParameterType == typeof(string))
                        {
                            _overlaySetText = m;
                        }
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                _metaFailed = true;
                Plugin.Log.Warn($"[ClientLoadFade] resolve failed: {ex.Message}");
                return false;
            }
        }

        private static object? ParseEnum(Type enumType, string name)
        {
            try { return Enum.Parse(enumType, name, ignoreCase: true); }
            catch { return null; }
        }

        private static Type? FindType(params string[] names)
        {
            foreach (var name in names)
            {
                var t = Type.GetType(name, false);
                if (t != null) return t;
            }
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var name in names)
                {
                    var simple = name.Split(',')[0].Trim();
                    var t = asm.GetType(simple, false);
                    if (t != null) return t;
                }
            }
            return null;
        }
    }
}
