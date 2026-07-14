using UnityEngine;
using UnityEngine.UI;

namespace SULFURTogether.UI.Shared
{
    /// <summary>
    /// A custom-mesh border stroke that traces its RectTransform's rectangular perimeter starting at the top-center
    /// (12 o'clock), filling to <see cref="SetProgress"/>. Not a fill bar, not a circular radial. Sharp corners (a
    /// grittier/plainer stroke reads more SULFUR-house than a smooth vector-rounded rect).
    ///
    /// Originally DR-3's <c>DownedRescueBorderProgress</c>; promoted to a shared widget (issue #8 vote overlay reuses
    /// it) with a <see cref="Clockwise"/> option so a second stroke can trace the opposite way (the majority-vote
    /// red "decline" arc grows counter-clockwise while the yellow "agree" arc grows clockwise).
    ///
    /// Clockwise path: top-center → top-right → right edge → bottom-right → bottom edge → bottom-left → left edge →
    /// top-left → back to top-center. Counter-clockwise mirrors it to the left first. Corner squares plug the gap a
    /// plain axis-aligned quad strip would otherwise leave at each 90° turn.
    /// </summary>
    internal sealed class PerimeterProgressGraphic : MaskableGraphic
    {
        public float Thickness = 4f;
        // Shrinks the traced rect relative to the RectTransform so stacked strokes don't fight for the same pixels.
        public float Inset = 0f;
        // Trace direction from the top-center start. True = clockwise (the default / DR behavior).
        public bool Clockwise = true;

        private float _progress01 = 1f;
        private readonly Vector2[] _corners = new Vector2[6];
        private readonly float[] _segLen = new float[5];

        public void SetProgress(float value)
        {
            value = Mathf.Clamp01(value);
            if (Mathf.Approximately(value, _progress01)) return;
            _progress01 = value;
            SetVerticesDirty();
        }

        public void SetClockwise(bool clockwise)
        {
            if (Clockwise == clockwise) return;
            Clockwise = clockwise;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();

            var r = GetPixelAdjustedRect();
            float hw = r.width * 0.5f - Inset;
            float hh = r.height * 0.5f - Inset;
            if (hw <= 0f || hh <= 0f) return;

            float sx = Clockwise ? 1f : -1f; // mirror the horizontal turn direction for counter-clockwise
            _corners[0] = new Vector2(0f, hh);
            _corners[1] = new Vector2(sx * hw, hh);
            _corners[2] = new Vector2(sx * hw, -hh);
            _corners[3] = new Vector2(-sx * hw, -hh);
            _corners[4] = new Vector2(-sx * hw, hh);
            _corners[5] = new Vector2(0f, hh);

            _segLen[0] = hw;
            _segLen[1] = 2f * hh;
            _segLen[2] = 2f * hw;
            _segLen[3] = 2f * hh;
            _segLen[4] = hw;
            float total = _segLen[0] + _segLen[1] + _segLen[2] + _segLen[3] + _segLen[4];
            float target = _progress01 * total;

            Color32 c = color;
            float consumed = 0f;
            for (int i = 0; i < 5; i++)
            {
                float segStart = consumed;
                float segEnd = consumed + _segLen[i];
                if (target <= segStart) break;

                Vector2 a = _corners[i];
                Vector2 b = _corners[i + 1];
                bool full = target >= segEnd;
                Vector2 endPoint = full ? b : Vector2.Lerp(a, b, (target - segStart) / _segLen[i]);

                AddSegmentQuad(vh, a, endPoint, c);
                if (full && i < 4)
                    AddCornerSquare(vh, b, c);

                consumed = segEnd;
                if (!full) break;
            }
        }

        private void AddSegmentQuad(VertexHelper vh, Vector2 a, Vector2 b, Color32 c)
        {
            if ((b - a).sqrMagnitude < 0.0001f) return;
            Vector2 dir = (b - a).normalized;
            Vector2 normal = new Vector2(-dir.y, dir.x) * (Thickness * 0.5f);

            int start = vh.currentVertCount;
            vh.AddVert(a - normal, c, Vector2.zero);
            vh.AddVert(a + normal, c, Vector2.zero);
            vh.AddVert(b + normal, c, Vector2.zero);
            vh.AddVert(b - normal, c, Vector2.zero);
            vh.AddTriangle(start, start + 1, start + 2);
            vh.AddTriangle(start, start + 2, start + 3);
        }

        private void AddCornerSquare(VertexHelper vh, Vector2 center, Color32 c)
        {
            float half = Thickness * 0.5f;
            int start = vh.currentVertCount;
            vh.AddVert(center + new Vector2(-half, -half), c, Vector2.zero);
            vh.AddVert(center + new Vector2(-half, half), c, Vector2.zero);
            vh.AddVert(center + new Vector2(half, half), c, Vector2.zero);
            vh.AddVert(center + new Vector2(half, -half), c, Vector2.zero);
            vh.AddTriangle(start, start + 1, start + 2);
            vh.AddTriangle(start, start + 2, start + 3);
        }
    }
}
