using UnityEngine;

namespace SULFURTogether.UI.RunStatsOverlay
{
    /// <summary>
    /// RS-4: single-focus-index carousel over the card Track. One scroll/d-pad input moves the focus by exactly
    /// one player, clamped at both ends (no wraparound); the Track's actual X position eases toward the target
    /// with a critically-damped spring (<see cref="Mathf.SmoothDamp"/>) for the slide feel. With &lt;=4 players
    /// the focus never needs to move past index 0, so the Track simply never moves and every card stays fully
    /// visible — this only becomes an active carousel once there are enough cards to overflow the viewport.
    /// </summary>
    internal sealed class RunStatsCarouselController
    {
        private const float SmoothTime = 0.18f;

        private RectTransform? _track;
        private int _cardCount;
        private int _focusIndex;
        private float _velocity;

        public int FocusIndex => _focusIndex;

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
            _velocity = 0f;
            SnapToFocus();
        }

        public void MoveFocus(int delta)
        {
            if (_cardCount <= 1) return;
            _focusIndex = Mathf.Clamp(_focusIndex + delta, 0, _cardCount - 1);
        }

        public void Tick(float deltaTime)
        {
            if (_track == null) return;
            float targetX = -_focusIndex * RunStatsCanvasBuilder.CardPitch;
            var pos = _track.anchoredPosition;
            pos.x = Mathf.SmoothDamp(pos.x, targetX, ref _velocity, SmoothTime, Mathf.Infinity, deltaTime);
            _track.anchoredPosition = pos;
        }

        private void SnapToFocus()
        {
            if (_track == null) return;
            var pos = _track.anchoredPosition;
            pos.x = -_focusIndex * RunStatsCanvasBuilder.CardPitch;
            _track.anchoredPosition = pos;
        }
    }
}
