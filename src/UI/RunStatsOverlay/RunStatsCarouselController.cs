using UnityEngine;

namespace SULFURTogether.UI.RunStatsOverlay
{
    /// <summary>
    /// RS-4: single-focus-index carousel over the card Track. One scroll/d-pad input moves the focus by exactly
    /// one card, clamped so the row can never scroll past its first or last card; the Track's actual X position
    /// follows the target through the shared under-damped spring, giving each step a distinct slide-and-settle
    /// (not a continuous pan). A row of &lt;=4 cards fits the viewport whole: its max focus is 0, so input
    /// moves nothing and the row just sits centered.
    /// </summary>
    internal sealed class RunStatsCarouselController
    {
        private const int VisibleCards = 4;
        private const float OverflowLeftPad = 24f;

        private RectTransform? _track;
        private int _cardCount;
        private int _focusIndex;
        private RunStatsSpring _x;

        public void SetTrack(RectTransform track)
        {
            _track = track;
        }

        /// <summary>Called once per newly displayed Run — resets focus to the first card and snaps the Track
        /// there immediately (no slide-in from a stale previous position).</summary>
        public void ResetForNewData(int cardCount)
        {
            _cardCount = Mathf.Max(0, cardCount);
            _focusIndex = 0;
            _x.Snap(TargetX());
            Apply();
        }

        public void MoveFocus(int delta)
        {
            _focusIndex = Mathf.Clamp(_focusIndex + delta, 0, MaxFocus());
        }

        public void Tick(float deltaTime)
        {
            _x.Tick(TargetX(), deltaTime);
            Apply();
        }

        /// <summary>The last focus position still has a full 4-card page to show — scrolling further would just
        /// drag the row off the left edge for nothing. 0 when everything already fits.</summary>
        private int MaxFocus() => Mathf.Max(0, _cardCount - VisibleCards);

        private float TargetX()
        {
            float rowWidth = _cardCount * RunStatsCanvasBuilder.CardWidth
                             + Mathf.Max(0, _cardCount - 1) * RunStatsCanvasBuilder.CardSpacing;
            // Fits → center the row; overflows → small left pad so the first card sits flush with a peek margin.
            float basePad = Mathf.Max(OverflowLeftPad, (RunStatsCanvasBuilder.ViewportWidth - rowWidth) * 0.5f);
            return basePad - _focusIndex * RunStatsCanvasBuilder.CardPitch;
        }

        private void Apply()
        {
            if (_track == null) return;
            var pos = _track.anchoredPosition;
            pos.x = _x.Value;
            _track.anchoredPosition = pos;
        }
    }
}
