using UnityEngine;
using UnityEngine.UI;

namespace SULFURTogether.UI.RunStatsOverlay
{
    /// <summary>
    /// RS-4: builds the root ScreenSpaceOverlay canvas + the carousel's Viewport/Track pair, bottom-anchored
    /// above a safe margin so cards never cover the native loading screen's "press ... to continue" prompt.
    /// Plain Unity UI construction only — no Native UI Lib dependency (this overlay isn't optional chrome, it
    /// needs to exist even when that soft dependency is absent).
    ///
    /// Viewport is a fixed-width <see cref="RectMask2D"/> window (~4 full cards); Track is the unclipped row of
    /// every card, laid out by a HorizontalLayoutGroup and then translated in X by
    /// <see cref="RunStatsCarouselController"/> — cards past the viewport edge are simply clipped to a peek
    /// sliver, which is the whole "4 full + edge peek for 5+" effect. The Track is anchored to the viewport's
    /// LEFT edge (not centered): centering would clip BOTH ends of an overflowing row at once, while the
    /// controller's base-pad math centers a non-overflowing row explicitly.
    /// </summary>
    internal static class RunStatsCanvasBuilder
    {
        public const float CardWidth = 340f;
        public const float ViewportWidth = 1600f; // ~4 full cards + slivers of the neighbors either side

        /// <summary>Card gap actually in effect — responsive: roomier on normal/wide screens, tightened on
        /// narrow windows so the row never gets squeezed off. Written by RunStatsOverlayManager right before
        /// each rebuild; the carousel controller's pitch math reads it back.</summary>
        public static float ActiveCardSpacing = 44f;

        // Reference-resolution units (1920x1080).
        // The viewport (card group) is vertically CENTERED with a slight upward bias: the native loading
        // screen's "press ... to continue" prompt lives at the bottom of the screen, so the bias keeps the
        // row's bottom edge well clear of it at every aspect ratio (the scaler is height-matched, so these
        // reference offsets track the window height).
        private const float VerticalCenterBiasPx = 60f;
        private const float ViewportHeightPx = 560f; // ~200px taller than a card: hover lift/scale/lean must
                                                     // never reach the RectMask2D's top/bottom edges

        public static GameObject BuildRoot(out RectTransform viewport, out RectTransform track)
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
            viewportRect.anchorMin = new Vector2(0.5f, 0.5f);
            viewportRect.anchorMax = new Vector2(0.5f, 0.5f);
            viewportRect.pivot = new Vector2(0.5f, 0.5f);
            viewportRect.anchoredPosition = new Vector2(0f, VerticalCenterBiasPx);
            viewportRect.sizeDelta = new Vector2(ViewportWidth, ViewportHeightPx);

            // Track is vertically CENTERED inside the viewport (not bottom-flush): the ~100px of mask slack
            // above AND below the cards is what lets a hovered card tilt/scale/lift without ever crossing the
            // RectMask2D edge and getting its corners shaved off.
            var trackGo = new GameObject("Track", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(ContentSizeFitter));
            trackGo.transform.SetParent(viewportGo.transform, false);
            var trackRect = (RectTransform)trackGo.transform;
            trackRect.anchorMin = new Vector2(0f, 0.5f);
            trackRect.anchorMax = new Vector2(0f, 0.5f);
            trackRect.pivot = new Vector2(0f, 0.5f);
            trackRect.anchoredPosition = Vector2.zero; // X owned by RunStatsCarouselController

            var layout = trackGo.GetComponent<HorizontalLayoutGroup>();
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.spacing = ActiveCardSpacing;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            layout.childControlWidth = false;
            layout.childControlHeight = false; // each card's own ContentSizeFitter owns its height

            var fitter = trackGo.GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            viewport = viewportRect;
            track = trackRect;
            return root;
        }
    }
}
