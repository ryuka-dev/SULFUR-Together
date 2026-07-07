using UnityEngine;
using UnityEngine.UI;

namespace SULFURTogether.UI.RunStatsOverlay
{
    /// <summary>
    /// RS-4: Balatro-style "pick it up and look at it" card feel, adapted to an orthographic overlay canvas.
    /// Real X/Y-axis rotation is INVISIBLE here — a ScreenSpaceOverlay canvas has no perspective, so tilting
    /// the quad in 3D projects to a ~1% flat squash (Log377: "only the scale shows"). The 3D illusion is
    /// therefore faked with the transforms an orthographic UI CAN show, all following the cursor's normalized
    /// offset from card center (aim, -1..1 per axis):
    ///
    ///   lean     — small Z rotation toward the cursor's side (the "tips under your finger" read)
    ///   squash   — directional foreshortening: width shrinks as the cursor nears a left/right edge, height
    ///              as it nears top/bottom (what a perspective tilt WOULD have looked like)
    ///   lift     — a few px toward the cursor + upward, plus ~5% scale (picked up off the table)
    ///   shadow   — the drop shadow slides OPPOSITE the cursor (depth cue: light source at the cursor)
    ///   ember    — the warm highlight strip drifts WITH the cursor and brightens (firelight passing over)
    ///
    /// Everything moves on the shared under-damped spring, so the card trails the cursor slightly, and on
    /// release settles back with one soft overshoot — never a snap. All motion is deltas around the layout's
    /// own baseline (captured once at construction, after a forced layout pass), so the layout system and the
    /// animation never fight over the same values.
    /// </summary>
    internal sealed class RunStatsCardHoverAnimator
    {
        private const float MaxLeanDegrees = 3.5f;
        private const float MaxSquash = 0.045f;      // ~ what an 8° perspective tilt would foreshorten
        private const float HotScale = 1.05f;
        private const float SlideTowardCursor = 5f;  // px
        private const float LiftUp = 8f;             // px
        private const float ShadowCounterSlide = 4f; // px
        private const float HighlightSlide = 7f;     // px

        private readonly RectTransform _rect;
        private readonly Shadow _shadow;
        private readonly RectTransform _highlightRect;
        private readonly Image _highlightImage;

        private readonly Vector2 _baselinePos;       // layout-assigned; animation is deltas around this
        private readonly Vector2 _shadowBaseDistance;
        private readonly Vector3 _restScale;

        private RunStatsSpring _aimX; // cursor offset from card center, -1..1, springs to 0 on release
        private RunStatsSpring _aimY;
        private RunStatsSpring _hot;  // 0..1 hover blend, drives lift/scale/highlight strength

        public RunStatsCardHoverAnimator(RunStatsCardView card)
        {
            _rect = card.Rect;
            _shadow = card.ShadowFx;
            _highlightRect = card.HighlightRect;
            _highlightImage = card.HighlightImage;
            _baselinePos = _rect.anchoredPosition;
            _shadowBaseDistance = _shadow.effectDistance;
            _restScale = _rect.localScale;
        }

        /// <summary>ScreenSpaceOverlay canvases render with no camera — pass null, which is the correct/required
        /// camera argument for that render mode (a real camera would misconvert the screen point).</summary>
        public bool IsPointerOver(Vector2 pointerPosition)
        {
            return RectTransformUtility.RectangleContainsScreenPoint(_rect, pointerPosition, null);
        }

        public void Tick(float deltaTime, bool isHot, Vector2 pointerPosition)
        {
            float targetAimX = 0f, targetAimY = 0f, targetHot = 0f;
            if (isHot && RectTransformUtility.ScreenPointToLocalPointInRectangle(_rect, pointerPosition, null, out var local))
            {
                Rect r = _rect.rect;
                // local is relative to the pivot; offset to be relative to the rect CENTER before normalizing,
                // otherwise a non-centered pivot skews which point counts as "aim zero".
                targetAimX = r.width > 0.01f ? Mathf.Clamp((local.x - r.center.x) / (r.width * 0.5f), -1f, 1f) : 0f;
                targetAimY = r.height > 0.01f ? Mathf.Clamp((local.y - r.center.y) / (r.height * 0.5f), -1f, 1f) : 0f;
                targetHot = 1f;
            }

            float aimX = _aimX.Tick(targetAimX, deltaTime);
            float aimY = _aimY.Tick(targetAimY, deltaTime);
            float hot = Mathf.Clamp01(_hot.Tick(targetHot, deltaTime));

            // Lean toward the cursor's side (cursor left => top edge sways left).
            _rect.localRotation = Quaternion.Euler(0f, 0f, aimX * MaxLeanDegrees);

            // Directional foreshortening + pick-up scale.
            float lift = 1f + (HotScale - 1f) * hot;
            _rect.localScale = new Vector3(
                _restScale.x * lift * (1f - MaxSquash * Mathf.Abs(aimX)),
                _restScale.y * lift * (1f - MaxSquash * Mathf.Abs(aimY)),
                _restScale.z);

            // Slide a touch toward the cursor and rise while held.
            _rect.anchoredPosition = _baselinePos
                + new Vector2(aimX * SlideTowardCursor, aimY * SlideTowardCursor + LiftUp * hot);

            // Depth cues: shadow slips away from the cursor, the ember highlight drifts with it and brightens.
            _shadow.effectDistance = _shadowBaseDistance
                + new Vector2(-aimX * ShadowCounterSlide, -aimY * ShadowCounterSlide);
            _highlightRect.anchoredPosition = new Vector2(aimX * HighlightSlide, aimY * HighlightSlide);
            var hl = _highlightImage.color;
            hl.a = Mathf.Lerp(RunStatsCardView.RestHighlightAlpha, RunStatsCardView.HotHighlightAlpha, hot);
            _highlightImage.color = hl;
        }
    }
}
