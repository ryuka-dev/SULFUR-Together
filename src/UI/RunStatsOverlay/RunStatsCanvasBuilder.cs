using UnityEngine;
using UnityEngine.UI;

namespace SULFURTogether.UI.RunStatsOverlay
{
    /// <summary>
    /// RS-3/RS-4: builds the root ScreenSpaceOverlay canvas + the carousel's Viewport/Track pair, bottom-anchored
    /// above a safe margin so cards never cover the native loading screen's "press ... to continue" prompt.
    /// Plain Unity UI construction only — no Native UI Lib dependency (this overlay isn't optional chrome, it
    /// needs to exist even when that soft dependency is absent).
    ///
    /// Viewport is a fixed-width <see cref="RectMask2D"/> window (~4 full cards); Track is the unclipped row of
    /// every card, laid out by a HorizontalLayoutGroup and then translated in X by
    /// <see cref="RunStatsCarouselController"/> — cards past the viewport edge are simply clipped to a peek
    /// sliver, which is the whole "4 full + edge peek for 5+" effect. With <=4 players the track never needs to
    /// move and nothing is ever clipped, so this collapses to the plain static Phase-3 layout for free.
    /// </summary>
    internal static class RunStatsCanvasBuilder
    {
        public const float CardWidth = 340f;
        public const float CardSpacing = 28f;
        public const float CardPitch = CardWidth + CardSpacing;

        // Reference-resolution units (1920x1080). Comfortably clears the native bottom-of-screen loading prompt.
        private const float BottomSafeMarginPx = 190f;
        private const float ViewportWidthPx = 1600f;  // ~4 full cards + slivers of the neighbors either side
        private const float ViewportHeightPx = 520f;  // generous — only horizontal clipping is wanted

        public static GameObject BuildRoot(out RectTransform track)
        {
            var root = new GameObject("RunStatsOverlayCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));

            var canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 0; // corrected to topmost by RunStatsOverlayManager right before each Show

            var scaler = root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            // Biased toward matching HEIGHT: the card row's vertical extent is what must never exceed the
            // screen (width has room to spare — cards stay centered either way), so on a short/squarish window
            // the whole canvas scales down before anything could overflow the bottom of the screen.
            scaler.matchWidthOrHeight = 0.85f;

            var viewportGo = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
            viewportGo.transform.SetParent(root.transform, false);
            var viewportRect = (RectTransform)viewportGo.transform;
            viewportRect.anchorMin = new Vector2(0.5f, 0f);
            viewportRect.anchorMax = new Vector2(0.5f, 0f);
            viewportRect.pivot = new Vector2(0.5f, 0f);
            viewportRect.anchoredPosition = new Vector2(0f, BottomSafeMarginPx);
            viewportRect.sizeDelta = new Vector2(ViewportWidthPx, ViewportHeightPx);

            var trackGo = new GameObject("Track", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(ContentSizeFitter));
            trackGo.transform.SetParent(viewportGo.transform, false);
            var trackRect = (RectTransform)trackGo.transform;
            trackRect.anchorMin = new Vector2(0.5f, 0f);
            trackRect.anchorMax = new Vector2(0.5f, 0f);
            trackRect.pivot = new Vector2(0.5f, 0f);
            trackRect.anchoredPosition = Vector2.zero; // moved in X by RunStatsCarouselController

            var layout = trackGo.GetComponent<HorizontalLayoutGroup>();
            layout.childAlignment = TextAnchor.LowerCenter; // bottom-align every card to one common baseline
            layout.spacing = CardSpacing;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            layout.childControlWidth = false;
            layout.childControlHeight = false; // each card's own ContentSizeFitter owns its height

            var fitter = trackGo.GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            track = trackRect;
            return root;
        }
    }
}
