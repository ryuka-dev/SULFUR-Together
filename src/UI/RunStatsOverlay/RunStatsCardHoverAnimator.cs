using UnityEngine;

namespace SULFURTogether.UI.RunStatsOverlay
{
    /// <summary>
    /// RS-4: Balatro-style "card feel" — whichever card the free-roaming cursor (see
    /// <see cref="RunStatsCursorControl"/>) is currently over tilts to follow the cursor's offset from the
    /// card's center and lifts slightly; every other card eases back to its resting pose. Independent of the
    /// carousel's focus index — hover reacts to the literal cursor position, scrolling is a separate concern.
    /// Polled every tick (no Unity event-system dependency) via <see cref="RectTransformUtility"/> geometry
    /// checks. The pointer position is read once per frame by the manager (via
    /// <see cref="RunStatsInputReader.TryGetPointerPosition"/> — Input System; legacy Input throws in this
    /// game, LogOutput376) and passed in.
    /// </summary>
    internal sealed class RunStatsCardHoverAnimator
    {
        private const float MaxTiltDegrees = 8f;
        private const float HotScale = 1.05f;
        private const float LerpSpeed = 12f;

        private readonly RectTransform _rect;
        private readonly Vector3 _restScale;
        private Quaternion _targetRot = Quaternion.identity;
        private Vector3 _targetScale;

        public RunStatsCardHoverAnimator(RectTransform rect)
        {
            _rect = rect;
            _restScale = rect.localScale;
            _targetScale = _restScale;
        }

        /// <summary>ScreenSpaceOverlay canvases render with no camera — pass null, which is the correct/required
        /// camera argument for that render mode (a real camera would misconvert the screen point).</summary>
        public bool IsPointerOver(Vector2 pointerPosition)
        {
            return RectTransformUtility.RectangleContainsScreenPoint(_rect, pointerPosition, null);
        }

        public void Tick(float deltaTime, bool isHot, Vector2 pointerPosition)
        {
            if (isHot && RectTransformUtility.ScreenPointToLocalPointInRectangle(_rect, pointerPosition, null, out var local))
            {
                Rect r = _rect.rect;
                float nx = r.width > 0.01f ? Mathf.Clamp(local.x / (r.width * 0.5f), -1f, 1f) : 0f;
                float ny = r.height > 0.01f ? Mathf.Clamp(local.y / (r.height * 0.5f), -1f, 1f) : 0f;
                _targetRot = Quaternion.Euler(-ny * MaxTiltDegrees, nx * MaxTiltDegrees, 0f);
                _targetScale = _restScale * HotScale;
            }
            else
            {
                _targetRot = Quaternion.identity;
                _targetScale = _restScale;
            }

            float t = 1f - Mathf.Exp(-LerpSpeed * deltaTime);
            _rect.localRotation = Quaternion.Slerp(_rect.localRotation, _targetRot, t);
            _rect.localScale = Vector3.Lerp(_rect.localScale, _targetScale, t);
        }
    }
}
