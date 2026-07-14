using UnityEngine;
using UnityEngine.UI;

namespace SULFURTogether.UI.VoteOverlay
{
    /// <summary>
    /// UI-VOTE: builds the root ScreenSpaceOverlay canvas + a single panel RectTransform for the session-vote HUD.
    /// Same construction technique as the downed/rescue and run-stats overlays (this mod's proven passive-overlay
    /// template) — plain Unity UI, no Native UI Lib dependency.
    ///
    /// Positioned <b>top-center, a fixed distance below the top edge</b> so it sits clear of the boss health bar
    /// (which occupies the very top): the panel is anchored to the top-center and pushed down by
    /// <see cref="TopOffsetPx"/>. Smaller than the downed panel — a lightweight, glanceable strip.
    /// </summary>
    internal static class VoteOverlayCanvasBuilder
    {
        public const float PanelWidth  = 480f;
        public const float PanelHeight = 132f;

        // Reference-resolution (1920x1080) units. Distance from the top edge to the panel's top — kept below the
        // boss health bar band. Verify against the real boss bar in game and nudge if they overlap.
        private const float TopOffsetPx = 150f;

        public static GameObject BuildRoot(out RectTransform panel)
        {
            var root = new GameObject("VoteOverlayCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));

            var canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 0; // corrected to topmost by VoteOverlayManager right before each show

            var scaler = root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            var panelGo = new GameObject("Panel", typeof(RectTransform));
            panelGo.transform.SetParent(root.transform, false);
            var panelRect = (RectTransform)panelGo.transform;
            panelRect.anchorMin = new Vector2(0.5f, 1f);
            panelRect.anchorMax = new Vector2(0.5f, 1f);
            panelRect.pivot     = new Vector2(0.5f, 1f);
            panelRect.anchoredPosition = new Vector2(0f, -TopOffsetPx);
            panelRect.sizeDelta = new Vector2(PanelWidth, PanelHeight);

            panel = panelRect;
            return root;
        }
    }
}
