using TMPro;
using UnityEngine;
using UnityEngine.UI;
using SULFURTogether.UI.Shared;
using SULFURTogether.Networking.Vote;

namespace SULFURTogether.UI.VoteOverlay
{
    /// <summary>
    /// UI-VOTE: the session-vote panel body. Same layered hand-built technique as the downed/rescue panel (drop
    /// shadow → charcoal body → inner tint → stacked perimeter strokes → TMP text) plus a per-player tally strip.
    ///
    /// Border model (single source of truth = the snapshot, driven from the manager):
    /// <list type="bullet">
    /// <item><b>base</b> — a dim full-perimeter outline, always drawn.</item>
    /// <item><b>red</b> — decline. In a Unanimous vote it appears only at a fail, filling the whole ring underneath
    /// the yellow (so the achieved agreement stays yellow and the rest reads red). In a Majority vote it grows
    /// counter-clockwise = the decline fraction.</item>
    /// <item><b>yellow</b> — agreement, clockwise = the agree fraction, drawn on top.</item>
    /// </list>
    /// The shared <see cref="VitalityAnimator"/> drives motion only; <see cref="SetVitality"/> here nudges the inner
    /// glow, never the strokes/text the manager owns — so animation and the rendered vote value can't disagree.
    /// </summary>
    internal sealed class VoteOverlayPanelView : IVitalityPanel
    {
        private static readonly Color BodyColor    = new Color(0.05f, 0.05f, 0.055f, 0.93f);
        private static readonly Color DropShadow   = new Color(0f, 0f, 0f, 0.55f);
        private static readonly Color BaseStroke   = new Color(0.42f, 0.41f, 0.44f, 0.42f);
        private static readonly Color InnerTintLo  = new Color(0.4f, 0.42f, 0.46f, 0.03f);
        private static readonly Color InnerTintHi  = new Color(1f, 0.72f, 0.2f, 0.06f);

        public static readonly Color AgreeColor    = new Color(1f, 0.76f, 0.22f, 1f);   // gold — agreement (reused ember/gold family)
        public static readonly Color DeclineColor  = new Color(0.86f, 0.24f, 0.24f, 1f); // red — decline / fail
        public static readonly Color PassTextColor = new Color(0.99f, 0.97f, 0.94f, 1f);
        public static readonly Color DimTextColor  = new Color(0.62f, 0.61f, 0.60f, 0.9f);

        private readonly GameObject _root;
        private readonly RectTransform _rect;
        private readonly CanvasGroup _canvasGroup;
        private readonly Image _innerTint;
        private readonly PerimeterProgressGraphic _redStroke;
        private readonly PerimeterProgressGraphic _yellowStroke;
        private readonly TextMeshProUGUI _titleText;
        private readonly TextMeshProUGUI _subText;
        private readonly VoteTallyStrip _tally;

        private VoteOverlayPanelView(GameObject root, CanvasGroup cg, Image innerTint,
            PerimeterProgressGraphic red, PerimeterProgressGraphic yellow,
            TextMeshProUGUI title, TextMeshProUGUI sub, VoteTallyStrip tally)
        {
            _root = root;
            _rect = (RectTransform)root.transform;
            _canvasGroup = cg;
            _innerTint = innerTint;
            _redStroke = red;
            _yellowStroke = yellow;
            _titleText = title;
            _subText = sub;
            _tally = tally;
        }

        public GameObject Root => _root;
        public RectTransform Rect => _rect;
        public CanvasGroup CanvasGroup => _canvasGroup;

        public static VoteOverlayPanelView Create(Transform parent, TMP_FontAsset? font)
        {
            var root = new GameObject("VotePanel", typeof(RectTransform), typeof(CanvasGroup), typeof(Image), typeof(Shadow));
            root.transform.SetParent(parent, false);
            var rect = (RectTransform)root.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            var body = root.GetComponent<Image>();
            body.color = BodyColor;
            body.raycastTarget = false; // passive display only

            var shadow = root.GetComponent<Shadow>();
            shadow.effectColor = DropShadow;
            shadow.effectDistance = new Vector2(4f, -4f);
            shadow.useGraphicAlpha = false;

            var cg = root.GetComponent<CanvasGroup>();
            cg.blocksRaycasts = false;
            cg.interactable = false;

            var inner = CreateFullRectImage(root.transform, "InnerTint", InnerTintLo, 6f);

            var baseStroke = CreateStroke(root.transform, "BorderBase", BaseStroke, thickness: 3f, inset: 0f);
            baseStroke.SetProgress(1f);

            var redStroke = CreateStroke(root.transform, "BorderRed", DeclineColor, thickness: 5f, inset: 4f);
            redStroke.SetProgress(0f);

            var yellowStroke = CreateStroke(root.transform, "BorderYellow", AgreeColor, thickness: 5f, inset: 4f);
            yellowStroke.SetProgress(0f);

            var title = CreateText(root.transform, font, 24f, FontStyles.Bold, TextAlignmentOptions.Center);
            var tr = title.rectTransform;
            tr.anchorMin = new Vector2(0f, 0.66f); tr.anchorMax = new Vector2(1f, 1f);
            tr.offsetMin = new Vector2(20f, 0f);   tr.offsetMax = new Vector2(-20f, -12f);

            var tally = VoteTallyStrip.Create(root.transform,
                new Vector2(0f, 0.40f), new Vector2(1f, 0.66f),
                new Vector2(20f, 0f), new Vector2(-20f, 0f));

            var sub = CreateText(root.transform, font, 17f, FontStyles.Normal, TextAlignmentOptions.Center);
            var sr = sub.rectTransform;
            sr.anchorMin = new Vector2(0f, 0f);   sr.anchorMax = new Vector2(1f, 0.40f);
            sr.offsetMin = new Vector2(20f, 12f); sr.offsetMax = new Vector2(-20f, 0f);

            return new VoteOverlayPanelView(root, cg, inner, redStroke, yellowStroke, title, sub, tally);
        }

        private static Image CreateFullRectImage(Transform parent, string name, Color color, float pad)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(pad, pad); rect.offsetMax = new Vector2(-pad, -pad);
            var image = go.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        private static PerimeterProgressGraphic CreateStroke(Transform parent, string name, Color color, float thickness, float inset)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(PerimeterProgressGraphic));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero; rect.offsetMax = Vector2.zero;
            var stroke = go.GetComponent<PerimeterProgressGraphic>();
            stroke.color = color;
            stroke.Thickness = thickness;
            stroke.Inset = inset;
            stroke.raycastTarget = false;
            return stroke;
        }

        private static TextMeshProUGUI CreateText(Transform parent, TMP_FontAsset? font, float size, FontStyles style, TextAlignmentOptions align)
        {
            var go = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var tmp = go.GetComponent<TextMeshProUGUI>();
            if (font != null) tmp.font = font;
            tmp.fontSize = size;
            tmp.fontStyle = style;
            tmp.alignment = align;
            tmp.color = Color.white;
            tmp.raycastTarget = false;
            tmp.enableAutoSizing = true;
            tmp.fontSizeMin = size * 0.6f;
            tmp.fontSizeMax = size;
            return tmp;
        }

        public void SetTitle(string text) => _titleText.text = text;
        public void SetSub(string text)   => _subText.text = text;

        /// <summary>Render the border strokes + text colours from the authoritative snapshot. One call per tick, from
        /// the manager, so the visual is always the current vote value (never split across code paths).</summary>
        public void Render(VoteStateSnapshot snap, string localPeerId)
        {
            float agree   = snap.AgreeFraction;
            float decline = snap.DeclineFraction;
            bool failedTerminal = snap.Phase == VotePhase.Resolved &&
                                  (snap.Outcome == VoteOutcome.Failed || snap.Outcome == VoteOutcome.Cancelled);

            _yellowStroke.SetProgress(agree);

            if (snap.Rule == VoteRule.Unanimous)
            {
                // Red only at a fail: fill the whole ring under the yellow (achieved agreement stays yellow).
                _redStroke.SetClockwise(true);
                _redStroke.SetProgress(failedTerminal && snap.Outcome == VoteOutcome.Failed ? 1f : 0f);
            }
            else // Majority: red decline fraction grows counter-clockwise
            {
                _redStroke.SetClockwise(false);
                _redStroke.SetProgress(decline);
            }

            // Cancelled: neutralise both to a dim ring.
            if (snap.Outcome == VoteOutcome.Cancelled)
            {
                _yellowStroke.SetProgress(0f);
                _redStroke.SetProgress(0f);
            }

            _titleText.color = snap.Outcome == VoteOutcome.Passed ? AgreeColor
                             : failedTerminal && snap.Outcome == VoteOutcome.Failed ? DeclineColor
                             : PassTextColor;

            _tally.Apply(snap, localPeerId);
        }

        // Shared VitalityAnimator hook — inner-glow only; strokes/text are the manager's (via Render).
        public void SetVitality(float vitality01)
            => _innerTint.color = Color.Lerp(InnerTintLo, InnerTintHi, Mathf.Clamp01(vitality01));
    }
}
