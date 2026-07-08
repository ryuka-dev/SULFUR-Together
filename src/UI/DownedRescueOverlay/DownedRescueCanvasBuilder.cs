using UnityEngine;
using UnityEngine.UI;

namespace SULFURTogether.UI.DownedRescueOverlay
{
    /// <summary>
    /// DR-2: builds the root ScreenSpaceOverlay canvas + a single centered panel RectTransform for the
    /// downed/rescue HUD. Same construction technique as RunStatsCanvasBuilder (this mod's only proven template
    /// for a passive, always-running uGUI overlay) — plain Unity UI, no Native UI Lib dependency, since this is
    /// not optional chrome.
    ///
    /// Positioned center screen with a downward bias (design spec §4): clear of the crosshair (exact center),
    /// the boss health bar (top), and the bottom subtitle/interaction-prompt band. The vertical offset mirrors
    /// where the old IMGUI prompt already sat (Screen.height*0.72f from the top), so the new panel occupies the
    /// same screen real estate that was already proven clear of other HUD elements.
    /// </summary>
    internal static class DownedRescueCanvasBuilder
    {
        public const float PanelWidth = 620f;
        public const float PanelHeight = 150f;

        // Reference-resolution units (1920x1080), matching RunStatsCanvasBuilder's reference frame.
        private const float VerticalCenterOffsetPx = -220f; // negative = below screen center

        public static GameObject BuildRoot(out RectTransform panel)
        {
            var root = new GameObject("DownedRescueOverlayCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));

            var canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 0; // corrected to topmost by DownedRescueOverlayManager right before each show

            var scaler = root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            var panelGo = new GameObject("Panel", typeof(RectTransform));
            panelGo.transform.SetParent(root.transform, false);
            var panelRect = (RectTransform)panelGo.transform;
            panelRect.anchorMin = new Vector2(0.5f, 0.5f);
            panelRect.anchorMax = new Vector2(0.5f, 0.5f);
            panelRect.pivot = new Vector2(0.5f, 0.5f);
            panelRect.anchoredPosition = new Vector2(0f, VerticalCenterOffsetPx);
            panelRect.sizeDelta = new Vector2(PanelWidth, PanelHeight);

            panel = panelRect;
            return root;
        }
    }
}
