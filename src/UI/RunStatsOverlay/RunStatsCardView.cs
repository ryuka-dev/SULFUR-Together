using TMPro;
using UnityEngine;
using UnityEngine.UI;
using SULFURTogether.Networking.Gameplay;

namespace SULFURTogether.UI.RunStatsOverlay
{
    /// <summary>
    /// RS-2/RS-3: a single run-stats card — player name + the 7 always-visible stat rows. Built entirely from
    /// plain Unity UI/TMP primitives (no bundled art): a dark panel + gold accent border for the local player's
    /// own card, using whatever TMP font <see cref="RunStatsOverlayManager"/> resolved from the currently active
    /// native UI (so it matches the game's own current-language font, CJK included).
    ///
    /// The whole card is one VerticalLayoutGroup + ContentSizeFitter (height only) so its height is always
    /// exactly the content's real height — the 7 rows can never spill outside the background (the original bug:
    /// a fixed pixel height with childControlHeight=false left every row at Unity's default 100px height).
    /// </summary>
    internal sealed class RunStatsCardView
    {
        // English placeholders — Docs/Localization.md registry row 20 (localization layer not implemented yet).
        private static readonly string[] Labels =
        {
            "Shots Fired",
            "Damage Dealt",
            "Kills",
            "Times Downed",
            "Rescues",
            "Damage Taken",
            "Destructibles Destroyed",
        };

        private static readonly Color NormalBg     = new Color(0.09f, 0.10f, 0.13f, 0.93f);
        private static readonly Color NormalBorder = new Color(0.75f, 0.78f, 0.85f, 0.9f);
        private static readonly Color LocalBorder  = new Color(1f, 0.80f, 0.25f, 1f);
        private static readonly Color LabelColor   = new Color(0.85f, 0.86f, 0.90f, 1f); // bright, never dull grey
        private static readonly Color ValueColor   = Color.white;
        private static readonly Color NameColor    = Color.white;
        private static readonly Color LocalNameColor = new Color(1f, 0.82f, 0.30f, 1f);

        private readonly GameObject _root;
        private readonly RectTransform _rect;
        private readonly TextMeshProUGUI _nameText;
        private readonly TextMeshProUGUI[] _valueTexts;
        private readonly Outline _border;

        private RunStatsCardView(GameObject root, TextMeshProUGUI nameText, TextMeshProUGUI[] valueTexts, Outline border)
        {
            _root = root;
            _rect = (RectTransform)root.transform;
            _nameText = nameText;
            _valueTexts = valueTexts;
            _border = border;
        }

        public GameObject Root => _root;
        public RectTransform Rect => _rect;

        public static RunStatsCardView Create(Transform parent, TMP_FontAsset? font)
        {
            var root = new GameObject("RunStatsCard", typeof(RectTransform), typeof(Image),
                typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            root.transform.SetParent(parent, false);
            var rootRect = (RectTransform)root.transform;
            rootRect.sizeDelta = new Vector2(RunStatsCanvasBuilder.CardWidth, rootRect.sizeDelta.y);

            var bg = root.GetComponent<Image>();
            bg.color = NormalBg;
            // Hover/carousel detection is pure RectTransformUtility geometry (see RunStatsCardHoverAnimator) —
            // nothing here needs Unity's raycast/EventSystem routing, and leaving it on was intercepting clicks
            // meant for native UI underneath (e.g. Hub dialogue) whenever a card visually overlapped it.
            bg.raycastTarget = false;

            // Border — an Outline effect on the background graphic, so it never consumes extra layout space.
            var border = root.GetComponent<Outline>();
            if (border == null) border = root.AddComponent<Outline>();
            border.effectColor = NormalBorder;
            border.effectDistance = new Vector2(2f, -2f);
            border.useGraphicAlpha = false;

            var vlayout = root.GetComponent<VerticalLayoutGroup>();
            vlayout.padding = new RectOffset(20, 20, 18, 18);
            vlayout.spacing = 6f;
            vlayout.childAlignment = TextAnchor.UpperCenter;
            vlayout.childForceExpandWidth = true;
            vlayout.childForceExpandHeight = false;
            vlayout.childControlWidth = true;
            vlayout.childControlHeight = true;

            // Width is fixed (set above); only height is driven by content, which is exactly what keeps every
            // card in a Run the same height (same 8 rows) while never clipping a taller/shorter one.
            var fitter = root.GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var nameText = CreateText(root.transform, font, 30f, FontStyles.Bold, TextAlignmentOptions.Center, 38f);

            var spacer = new GameObject("NameSeparator", typeof(RectTransform), typeof(LayoutElement));
            spacer.transform.SetParent(root.transform, false);
            spacer.GetComponent<LayoutElement>().minHeight = 6f;

            var valueTexts = new TextMeshProUGUI[Labels.Length];
            for (int i = 0; i < Labels.Length; i++)
            {
                var rowGo = new GameObject($"Row_{i}", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
                rowGo.transform.SetParent(root.transform, false);
                rowGo.GetComponent<LayoutElement>().minHeight = 30f;
                var rowLayout = rowGo.GetComponent<HorizontalLayoutGroup>();
                rowLayout.childForceExpandWidth = true;
                rowLayout.childForceExpandHeight = true;
                rowLayout.childControlWidth = true;
                rowLayout.childControlHeight = true;
                rowLayout.spacing = 14f;

                var labelText = CreateText(rowGo.transform, font, 19f, FontStyles.Normal, TextAlignmentOptions.Left, 28f);
                labelText.text = Labels[i];
                labelText.color = LabelColor;
                labelText.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;

                var valueText = CreateText(rowGo.transform, font, 23f, FontStyles.Bold, TextAlignmentOptions.Right, 28f);
                valueText.color = ValueColor;
                valueText.gameObject.AddComponent<LayoutElement>().minWidth = 70f;
                valueTexts[i] = valueText;
            }

            return new RunStatsCardView(root, nameText, valueTexts, border);
        }

        private static TextMeshProUGUI CreateText(Transform parent, TMP_FontAsset? font, float size, FontStyles style, TextAlignmentOptions align, float minHeight)
        {
            var go = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var tmp = go.GetComponent<TextMeshProUGUI>();
            if (font != null) tmp.font = font;
            tmp.fontSize = size;
            tmp.fontStyle = style;
            tmp.alignment = align;
            tmp.color = Color.white;
            tmp.raycastTarget = false; // see the raycastTarget note on the card background above
            // Digit count varies (0 vs 5-digit damage totals) and names vary in length — auto-size with a
            // generous floor so overflow is solved by real layout, not by silently shrinking text to near-nothing.
            tmp.enableAutoSizing = true;
            tmp.fontSizeMin = size * 0.7f;
            tmp.fontSizeMax = size;
            var le = go.AddComponent<LayoutElement>();
            le.minHeight = minHeight;
            return tmp;
        }

        public void Bind(NetRunStats stats, bool isLocalPlayer)
        {
            string displayName = string.IsNullOrWhiteSpace(stats.PlayerName) ? stats.PeerId : stats.PlayerName;
            _nameText.text = isLocalPlayer ? displayName + " (You)" : displayName; // Docs/Localization.md row 20
            _nameText.color = isLocalPlayer ? LocalNameColor : NameColor;
            _border.effectColor = isLocalPlayer ? LocalBorder : NormalBorder;

            SetValue(0, stats.ShotsFired);
            SetValue(1, stats.DamageDealt);
            SetValue(2, stats.Kills);
            SetValue(3, stats.TimesDowned);
            SetValue(4, stats.Rescues);
            SetValue(5, stats.DamageTaken);
            SetValue(6, stats.DestructiblesDestroyed);
        }

        /// <summary>Placeholder frame shown the moment the loading screen starts, before the finalized broadcast
        /// has actually arrived (only really reachable on a laggy client — the Host has its own data instantly).</summary>
        public void SetEmpty()
        {
            _nameText.text = "…"; // Docs/Localization.md row 20
            _nameText.color = NameColor;
            _border.effectColor = NormalBorder;
            for (int i = 0; i < _valueTexts.Length; i++)
                _valueTexts[i].text = "…";
        }

        private void SetValue(int index, int value)
        {
            _valueTexts[index].text = value.ToString();
        }
    }
}
