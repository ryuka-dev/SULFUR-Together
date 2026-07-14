using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using SULFURTogether.Networking.Vote;

namespace SULFURTogether.UI.VoteOverlay
{
    /// <summary>
    /// UI-VOTE: a centered row of one square per participant — white = not voted, green = agreed, red = declined —
    /// with the local player's square outlined so a player can find themselves at a glance. The per-player breakdown
    /// that complements the border's agree-fraction gauge. Rebuilt only when the participant count changes; colours
    /// refresh every tick.
    /// </summary>
    internal sealed class VoteTallyStrip
    {
        private static readonly Color ColorNotVoted = new Color(0.85f, 0.85f, 0.87f, 0.92f);
        private static readonly Color ColorAgree    = new Color(0.30f, 0.80f, 0.36f, 1f);
        private static readonly Color ColorDecline  = new Color(0.86f, 0.26f, 0.26f, 1f);
        private static readonly Color LocalOutline  = new Color(1f, 0.80f, 0.30f, 1f);

        private const float SquareSize = 20f;
        private const float Gap        = 9f;
        private const float OutlinePad = 3f;

        private readonly RectTransform _container;
        private readonly List<Image> _squares  = new List<Image>();
        private readonly List<Image> _outlines = new List<Image>();
        private int _count = -1;

        private VoteTallyStrip(RectTransform container) => _container = container;

        public static VoteTallyStrip Create(Transform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            var go = new GameObject("TallyStrip", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
            return new VoteTallyStrip(rect);
        }

        /// <summary>Update the squares to the snapshot's participants (rebuilding the row only when the count moved).
        /// The <paramref name="localPeerId"/> square is outlined.</summary>
        public void Apply(VoteStateSnapshot snap, string localPeerId)
        {
            int n = snap?.Participants.Count ?? 0;
            if (n != _count) Rebuild(n);

            if (n == 0) return;
            for (int i = 0; i < n; i++)
            {
                var p = snap.Participants[i];
                _squares[i].color = p.Choice == VoteChoice.Agree   ? ColorAgree
                                  : p.Choice == VoteChoice.Decline ? ColorDecline
                                  : ColorNotVoted;
                bool isLocal = !string.IsNullOrEmpty(localPeerId) && p.PeerId == localPeerId;
                _outlines[i].enabled = isLocal;
            }
        }

        private void Rebuild(int n)
        {
            foreach (var s in _squares)  if (s != null) Object.Destroy(s.gameObject);
            foreach (var o in _outlines) if (o != null) Object.Destroy(o.gameObject);
            _squares.Clear();
            _outlines.Clear();
            _count = n;
            if (n <= 0) return;

            float totalWidth = n * SquareSize + (n - 1) * Gap;
            float startX = -totalWidth * 0.5f + SquareSize * 0.5f;

            for (int i = 0; i < n; i++)
            {
                float x = startX + i * (SquareSize + Gap);

                // Local-player outline sits behind the square, slightly larger.
                var outlineGo = new GameObject($"Outline{i}", typeof(RectTransform), typeof(Image));
                outlineGo.transform.SetParent(_container, false);
                var oRect = (RectTransform)outlineGo.transform;
                oRect.anchorMin = oRect.anchorMax = new Vector2(0.5f, 0.5f);
                oRect.pivot = new Vector2(0.5f, 0.5f);
                oRect.anchoredPosition = new Vector2(x, 0f);
                oRect.sizeDelta = new Vector2(SquareSize + OutlinePad * 2f, SquareSize + OutlinePad * 2f);
                var oImg = outlineGo.GetComponent<Image>();
                oImg.color = LocalOutline;
                oImg.raycastTarget = false;
                oImg.enabled = false;
                _outlines.Add(oImg);

                var sqGo = new GameObject($"Square{i}", typeof(RectTransform), typeof(Image));
                sqGo.transform.SetParent(_container, false);
                var sRect = (RectTransform)sqGo.transform;
                sRect.anchorMin = sRect.anchorMax = new Vector2(0.5f, 0.5f);
                sRect.pivot = new Vector2(0.5f, 0.5f);
                sRect.anchoredPosition = new Vector2(x, 0f);
                sRect.sizeDelta = new Vector2(SquareSize, SquareSize);
                var sImg = sqGo.GetComponent<Image>();
                sImg.color = ColorNotVoted;
                sImg.raycastTarget = false;
                _squares.Add(sImg);
            }
        }
    }
}
