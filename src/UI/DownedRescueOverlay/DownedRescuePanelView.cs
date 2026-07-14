using TMPro;
using UnityEngine;
using UnityEngine.UI;
using SULFURTogether.UI.Shared;

namespace SULFURTogether.UI.DownedRescueOverlay
{
    /// <summary>
    /// DR-2/DR-3: the downed/rescue panel body. Same layered-primitive technique as RunStatsCardView (this mod's
    /// only precedent for a hand-built "SULFUR house style" panel — no native texture extraction exists anywhere
    /// in this codebase): drop shadow → charcoal body → inner tint panel → two stacked PerimeterProgressGraphic
    /// strokes (dim complete base + bright live progress) → main/sub TMP text. A "dim/dread → warm/alive" palette
    /// rather than RunStatsCardView's "fire lake" card look, reusing its ember/gold accents rather than inventing
    /// a new hue (DR-4 lerps toward these as the rescue's vitality rises).
    /// </summary>
    internal sealed class DownedRescuePanelView : IVitalityPanel
    {
        // Dread/vitality palette. Deliberately desaturated/cold at rest, warming toward the reused RunStatsCardView
        // ember/gold accents as vitality rises — never a hue this panel invents on its own.
        private static readonly Color BodyColor = new Color(0.05f, 0.05f, 0.055f, 0.92f);
        private static readonly Color InnerTintDim = new Color(0.4f, 0.42f, 0.46f, 0.035f);
        private static readonly Color InnerTintAlive = new Color(1f, 0.55f, 0.15f, 0.06f);
        private static readonly Color DropShadow = new Color(0f, 0f, 0f, 0.55f);
        private static readonly Color BaseStrokeColor = new Color(0.42f, 0.41f, 0.44f, 0.4f); // dim complete outline, always visible
        private static readonly Color ProgressStrokeDim = new Color(0.55f, 0.54f, 0.56f, 0.55f);
        private static readonly Color ProgressStrokeAlive = new Color(1f, 0.60f, 0.25f, 1f); // RunStatsCardView BestValueColor (ember)
        private static readonly Color TextColorDim = new Color(0.55f, 0.54f, 0.52f, 0.75f);
        private static readonly Color TextColorAlive = new Color(0.99f, 0.97f, 0.94f, 1f); // RunStatsCardView ValueColor
        private static readonly Color SubTextColorDim = new Color(0.45f, 0.44f, 0.42f, 0.6f);
        private static readonly Color SubTextColorAlive = new Color(0.84f, 0.81f, 0.76f, 1f); // RunStatsCardView LabelColor

        private readonly GameObject _root;
        private readonly RectTransform _rect;
        private readonly CanvasGroup _canvasGroup;
        private readonly Image _innerTint;
        private readonly PerimeterProgressGraphic _progressStroke;
        private readonly TextMeshProUGUI _mainText;
        private readonly TextMeshProUGUI _subText;

        private DownedRescuePanelView(GameObject root, CanvasGroup canvasGroup, Image innerTint,
            PerimeterProgressGraphic progressStroke, TextMeshProUGUI mainText, TextMeshProUGUI subText)
        {
            _root = root;
            _rect = (RectTransform)root.transform;
            _canvasGroup = canvasGroup;
            _innerTint = innerTint;
            _progressStroke = progressStroke;
            _mainText = mainText;
            _subText = subText;
        }

        public GameObject Root => _root;
        public RectTransform Rect => _rect;
        public CanvasGroup CanvasGroup => _canvasGroup;

        public static DownedRescuePanelView Create(Transform parent, TMP_FontAsset? font)
        {
            var root = new GameObject("DownedRescuePanel", typeof(RectTransform), typeof(CanvasGroup), typeof(Image), typeof(Shadow));
            root.transform.SetParent(parent, false);
            var rect = (RectTransform)root.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            var body = root.GetComponent<Image>();
            body.color = BodyColor;
            body.raycastTarget = false; // passive display only — never steal a click meant for native UI underneath

            var shadow = root.GetComponent<Shadow>();
            shadow.effectColor = DropShadow;
            shadow.effectDistance = new Vector2(4f, -4f);
            shadow.useGraphicAlpha = false;

            var canvasGroup = root.GetComponent<CanvasGroup>();
            canvasGroup.blocksRaycasts = false;
            canvasGroup.interactable = false;

            var inner = CreateFullRectImage(root.transform, "InnerTint", InnerTintDim);

            var baseStroke = CreateBorderStroke(root.transform, "BorderBase", BaseStrokeColor, thickness: 3f, inset: 0f);
            baseStroke.SetProgress(1f);

            var progressStroke = CreateBorderStroke(root.transform, "BorderProgress", ProgressStrokeDim, thickness: 5f, inset: 4f);
            progressStroke.SetProgress(0f);

            var mainText = CreateText(root.transform, font, 30f, FontStyles.Bold, TextAlignmentOptions.Center);
            var mainRect = mainText.rectTransform;
            mainRect.anchorMin = new Vector2(0f, 0.5f);
            mainRect.anchorMax = new Vector2(1f, 1f);
            mainRect.offsetMin = new Vector2(24f, 0f);
            mainRect.offsetMax = new Vector2(-24f, -18f);

            var subText = CreateText(root.transform, font, 19f, FontStyles.Normal, TextAlignmentOptions.Center);
            var subRect = subText.rectTransform;
            subRect.anchorMin = new Vector2(0f, 0f);
            subRect.anchorMax = new Vector2(1f, 0.5f);
            subRect.offsetMin = new Vector2(24f, 14f);
            subRect.offsetMax = new Vector2(-24f, 0f);

            return new DownedRescuePanelView(root, canvasGroup, inner, progressStroke, mainText, subText);
        }

        private static Image CreateFullRectImage(Transform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(6f, 6f);
            rect.offsetMax = new Vector2(-6f, -6f);
            var image = go.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        private static PerimeterProgressGraphic CreateBorderStroke(Transform parent, string name, Color color, float thickness, float inset)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(PerimeterProgressGraphic));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
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

        public void SetMainText(string text) => _mainText.text = text;
        public void SetSubText(string text) => _subText.text = text;

        public void SetBorderProgress(float progress01) => _progressStroke.SetProgress(progress01);

        /// <summary>DR-4: the single continuous 0..1 "vitality" value drives every color lerp on the panel — the
        /// border progress stroke, both text colors, and the inner tint — from dim/desaturated toward the reused
        /// ember/gold accent. Never a discrete phase switch (design spec §6).</summary>
        public void SetVitality(float vitality01)
        {
            float v = Mathf.Clamp01(vitality01);
            _progressStroke.color = Color.Lerp(ProgressStrokeDim, ProgressStrokeAlive, v);
            _mainText.color = Color.Lerp(TextColorDim, TextColorAlive, v);
            _subText.color = Color.Lerp(SubTextColorDim, SubTextColorAlive, v);
            _innerTint.color = Color.Lerp(InnerTintDim, InnerTintAlive, v);
        }
    }
}
