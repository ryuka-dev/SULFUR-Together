using TMPro;
using UnityEngine;
using UnityEngine.UI;
using SULFURTogether.Networking.Gameplay;

namespace SULFURTogether.UI.RunStatsOverlay
{
    /// <summary>
    /// RS-4: a single run-stats card — player name + the 7 always-visible stat rows, in the SULFUR "fire lake"
    /// palette: warm charcoal body, old-silver border (sulfur-gold for the local player's own card), ember
    /// highlight. Built entirely from plain Unity UI/TMP primitives — no bundled art; the "texture" is layered
    /// translucent panels + vertex-effect shadow/border/glow, and the font is whatever
    /// <see cref="RunStatsOverlayManager"/> sampled from the game's own current-language UI.
    ///
    /// Layer stack (bottom to top): drop shadow (vertex effect, counter-moves on hover) → charcoal body +
    /// border Outline (+ gold glow Outline on the own card) → inner warm panel (static, breaks the flatness) →
    /// title band behind the name row (warms up on hover) → name/stat text. The band and inner panel are
    /// <c>ignoreLayout</c> children so the VerticalLayoutGroup never repositions them.
    ///
    /// The whole card is one VerticalLayoutGroup + ContentSizeFitter (height only) so its height is always
    /// exactly the content's real height — the 7 rows can never spill outside the background.
    /// </summary>
    internal sealed class RunStatsCardView
    {
        // Localization keys + English fallbacks for the 7 stat rows (Docs/Localization.md registry row 20).
        // Resolved to display strings in Create() — cards are rebuilt each run-end, so they pick up the current
        // language without any live-refresh path.
        private static readonly (string key, string en)[] LabelDefs =
        {
            ("runstats.stat.shotsFired",             "Shots Fired"),
            ("runstats.stat.damageDealt",            "Damage Dealt"),
            ("runstats.stat.kills",                  "Kills"),
            ("runstats.stat.timesDowned",            "Times Downed"),
            ("runstats.stat.rescues",                "Rescues"),
            ("runstats.stat.damageTaken",            "Damage Taken"),
            ("runstats.stat.destructiblesDestroyed", "Destructibles Destroyed"),
        };

        // Fire-lake palette: charred body, old-silver vs sulfur-gold accents, ember warmth. Deliberately warm
        // dark (brown-black), never pure black, so the card separates from the black loading backdrop.
        private static readonly Color BodyColor     = new Color(0.085f, 0.065f, 0.050f, 0.97f);
        private static readonly Color InnerTint     = new Color(1f, 0.58f, 0.28f, 0.045f);
        private static readonly Color NormalBorder  = new Color(0.72f, 0.71f, 0.66f, 0.95f); // old silver
        private static readonly Color LocalBorder   = new Color(0.99f, 0.72f, 0.18f, 1f);    // sulfur gold
        private static readonly Color LocalGlow     = new Color(1f, 0.55f, 0.15f, 0.30f);    // restrained ember halo
        private static readonly Color DropShadow    = new Color(0f, 0f, 0f, 0.55f);
        private static readonly Color TitleBandColor = new Color(1f, 0.50f, 0.20f, 1f);      // alpha driven by animator
        private static readonly Color LabelColor    = new Color(0.84f, 0.81f, 0.76f, 1f);    // warm light grey
        private static readonly Color ValueColor    = new Color(0.99f, 0.97f, 0.94f, 1f);
        // RS-6 best-stat highlight: bright ember orange. Deliberately a third accent, apart from BOTH the plain
        // near-white values (clearly warm) and the sulfur-gold "this is you" accents (orange vs yellow-gold) —
        // gold marks whose card it is, ember marks the best number in the whole Run.
        private static readonly Color BestValueColor = new Color(1f, 0.60f, 0.25f, 1f);
        private static readonly Color NameColor     = Color.white;
        private static readonly Color LocalNameColor = new Color(1f, 0.80f, 0.28f, 1f);

        public const float RestHighlightAlpha = 0.10f;
        public const float HotHighlightAlpha = 0.16f;

        // Title band: header strip behind the player-name row (top padding + name row height), a deliberate
        // part of the card body design — not a free-floating "highlight slab" cut off mid-card.
        private const float TitleBandHeight = 62f;

        private readonly GameObject _root;
        private readonly RectTransform _rect;
        private readonly TextMeshProUGUI _nameText;
        private readonly TextMeshProUGUI[] _valueTexts;
        private readonly Outline _border;
        private readonly Outline _glow;

        private RunStatsCardView(GameObject root, TextMeshProUGUI nameText, TextMeshProUGUI[] valueTexts,
            Outline border, Outline glow, Shadow shadow, RectTransform titleBandRect, Image titleBandImage)
        {
            _root = root;
            _rect = (RectTransform)root.transform;
            _nameText = nameText;
            _valueTexts = valueTexts;
            _border = border;
            _glow = glow;
            ShadowFx = shadow;
            TitleBandRect = titleBandRect;
            TitleBandImage = titleBandImage;
        }

        public GameObject Root => _root;
        public RectTransform Rect => _rect;
        public Shadow ShadowFx { get; }
        public RectTransform TitleBandRect { get; }
        public Image TitleBandImage { get; }

        public static RunStatsCardView Create(Transform parent, TMP_FontAsset? font)
        {
            var root = new GameObject("RunStatsCard", typeof(RectTransform), typeof(Image),
                typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            root.transform.SetParent(parent, false);
            var rootRect = (RectTransform)root.transform;
            rootRect.sizeDelta = new Vector2(RunStatsCanvasBuilder.CardWidth, rootRect.sizeDelta.y);

            var bg = root.GetComponent<Image>();
            bg.color = BodyColor;
            // Hover/carousel detection is pure RectTransformUtility geometry — nothing here needs Unity's
            // raycast/EventSystem routing, and leaving it on was intercepting clicks meant for native UI
            // underneath (e.g. Hub dialogue) whenever a card visually overlapped it (Log374).
            bg.raycastTarget = false;

            // Vertex effects on the body image, applied in component order: border outline first (crisp edge),
            // then the gold glow (local card only — duplicates body+border in translucent gold, reading as a
            // restrained halo), then the drop shadow LAST so it re-duplicates everything above into one dark
            // grounded copy whose offset the hover animator counter-moves.
            var border = root.AddComponent<Outline>();
            border.effectColor = NormalBorder;
            border.effectDistance = new Vector2(2f, -2f);
            border.useGraphicAlpha = false;

            var glow = root.AddComponent<Outline>();
            glow.effectColor = LocalGlow;
            glow.effectDistance = new Vector2(4.5f, -4.5f);
            glow.useGraphicAlpha = false;
            glow.enabled = false;

            var shadow = root.AddComponent<Shadow>();
            shadow.effectColor = DropShadow;
            shadow.effectDistance = new Vector2(5f, -5f);
            shadow.useGraphicAlpha = false;

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

            // -- decoration layers (ignoreLayout: positioned by anchors, invisible to the VerticalLayoutGroup) --

            var inner = CreateDecoration(root.transform, "InnerPanel", InnerTint);
            inner.rect.anchorMin = Vector2.zero;
            inner.rect.anchorMax = Vector2.one;
            inner.rect.offsetMin = new Vector2(5f, 5f);
            inner.rect.offsetMax = new Vector2(-5f, -5f);

            // Header band behind the name row — anchored to the card's top with a fixed height matching the
            // name area, so it reads as the card's title plate (it warms up slightly under the cursor).
            var titleBand = CreateDecoration(root.transform, "TitleBand",
                new Color(TitleBandColor.r, TitleBandColor.g, TitleBandColor.b, RestHighlightAlpha));
            titleBand.rect.anchorMin = new Vector2(0f, 1f);
            titleBand.rect.anchorMax = new Vector2(1f, 1f);
            titleBand.rect.pivot = new Vector2(0.5f, 1f);
            titleBand.rect.offsetMin = new Vector2(5f, -TitleBandHeight);
            titleBand.rect.offsetMax = new Vector2(-5f, -5f);

            // -- text content (layout children) --

            var nameText = CreateText(root.transform, font, 30f, FontStyles.Bold, TextAlignmentOptions.Center, 38f);

            var spacer = new GameObject("NameSeparator", typeof(RectTransform), typeof(LayoutElement));
            spacer.transform.SetParent(root.transform, false);
            spacer.GetComponent<LayoutElement>().minHeight = 6f;

            var valueTexts = new TextMeshProUGUI[LabelDefs.Length];
            for (int i = 0; i < LabelDefs.Length; i++)
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
                labelText.text = CoopLoc.Get(LabelDefs[i].key, LabelDefs[i].en);
                labelText.color = LabelColor;
                labelText.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;

                var valueText = CreateText(rowGo.transform, font, 23f, FontStyles.Bold, TextAlignmentOptions.Right, 28f);
                valueText.color = ValueColor;
                valueText.gameObject.AddComponent<LayoutElement>().minWidth = 70f;
                valueTexts[i] = valueText;
            }

            return new RunStatsCardView(root, nameText, valueTexts, border, glow, shadow, titleBand.rect, titleBand.image);
        }

        private static (RectTransform rect, Image image) CreateDecoration(Transform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            go.GetComponent<LayoutElement>().ignoreLayout = true;
            var image = go.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return ((RectTransform)go.transform, image);
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

        public void Bind(NetRunStats stats, bool isLocalPlayer, RunStatsBestMarks best)
        {
            string displayName = string.IsNullOrWhiteSpace(stats.PlayerName) ? stats.PeerId : stats.PlayerName;
            _nameText.text = isLocalPlayer
                ? CoopLoc.Format("runstats.youSuffix", "{name} (You)", ("name", displayName))
                : displayName;
            _nameText.color = isLocalPlayer ? LocalNameColor : NameColor;
            _border.effectColor = isLocalPlayer ? LocalBorder : NormalBorder;
            _glow.enabled = isLocalPlayer;

            SetValue(0, stats.ShotsFired, best);
            SetValue(1, stats.DamageDealt, best);
            SetValue(2, stats.Kills, best);
            SetValue(3, stats.TimesDowned, best);
            SetValue(4, stats.Rescues, best);
            SetValue(5, stats.DamageTaken, best);
            SetValue(6, stats.DestructiblesDestroyed, best);
        }

        /// <summary>Placeholder frame shown the moment the loading screen starts, before the finalized broadcast
        /// has actually arrived (only really reachable on a laggy client — the Host has its own data instantly).</summary>
        public void SetEmpty()
        {
            _nameText.text = "…"; // Docs/Localization.md row 20
            _nameText.color = NameColor;
            _border.effectColor = NormalBorder;
            _glow.enabled = false;
            for (int i = 0; i < _valueTexts.Length; i++)
            {
                _valueTexts[i].text = "…";
                _valueTexts[i].color = ValueColor;
            }
        }

        /// <summary>The RS-6 highlight lives only in the value text's color, set here at bind time and never
        /// touched by the hover animator (which animates transform/shadow/title-band alpha only) — so hovering,
        /// tilting or scrolling can neither obscure it nor change who reads as best.</summary>
        private void SetValue(int index, int value, RunStatsBestMarks best)
        {
            _valueTexts[index].text = value.ToString();
            _valueTexts[index].color = best.IsBest(index, value) ? BestValueColor : ValueColor;
        }
    }
}
