using System;
using System.Collections.Generic;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Units;
using SULFURTogether.Api;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SULFURTogether.Networking.Gameplay.TeamMarks
{
    /// <summary>
    /// Team marks (issue #26), what the local player sees: above every enemy that carries another player's mark, a skull
    /// icon over a small arrow (one, two or three skulls for the number of other players on it); next to the crosshair,
    /// the multiplier while the player aims at such an enemy; and once per install, a toast explaining the rule.
    ///
    /// <para>One object owns a pool of SpriteRenderers and places them each LateUpdate; nothing is parented to an enemy
    /// and nothing runs on a puppet. The icons draw over level geometry (a low ceiling would otherwise swallow an icon
    /// that sits above the head) and are shown only while the enemy's head is in sight, so they never show through a
    /// wall. A companion mod can take over the icon on the units it shows (<see cref="NetTeamMarks.RegisterMarkPresenter"/>)
    /// or the crosshair multiplier (<see cref="NetTeamMarks.RegisterHitPresenter"/>).
    /// <see cref="NetTeamMarks.MultiplierChanged"/> is raised from here when what this end shows changes.</para>
    /// </summary>
    internal sealed class TeamMarkPresenter : MonoBehaviour
    {
        private const float IconWorldWidth = 0.55f;     // metres at close range
        private const float MinAngularWidth = 0.035f;   // radians, so a far enemy's icon stays readable
        private const float GapAboveHead = 0.45f;       // clears an overhead health bar (Enemy Health Bars sits at +0.2)
        private const float SightPointBelowHead = 0.2f; // the point checked for line of sight, just under the head's top

        private const float CrosshairOffsetX = 30f, CrosshairOffsetY = -2f; // reference pixels (1920 x 1080), right of centre
        private const float CrosshairFontSize = 26f;
        private const float AimRange = 150f;
        private static readonly Color HudOrange = new Color32(245, 166, 59, 255);

        /// <summary>Binds the crosshair text to the game's round bold font when SULFUR Native UI Lib is loaded (it keeps
        /// it on the language's font); null without the lib, and the font of some live game text is used instead.</summary>
        internal static Action<TMP_Text>? BindCrosshairFont;

        private static TeamMarkPresenter? _instance;

        private readonly List<TeamMarkManager.Shown> _shown = new List<TeamMarkManager.Shown>();
        private readonly List<SpriteRenderer> _pool = new List<SpriteRenderer>();
        private readonly RaycastHit[] _hits = new RaycastHit[16];
        private Material? _onTopMaterial;
        private int _skipLayers = -1;
        private TextMeshProUGUI? _crosshairText;
        private float _crosshairShown;
        private int _lastSignature;
        private bool _hintDone, _warnedIcons, _warnedCrosshair;

        public static void EnsureCreated()
        {
            if (_instance != null) return;
            var go = new GameObject("SULFURTogether TeamMarks");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<TeamMarkPresenter>();
        }

        private void LateUpdate()
        {
            int used = 0;
            float crosshair = 1f;
            try
            {
                TeamMarkManager.CollectShown(_shown);
                TeamMarkArt.Tick();
                RaiseIfChanged();
                if (_shown.Count > 0)
                {
                    var cam = ResolveCamera();
                    if (cam != null)
                    {
                        used = PlaceIcons(cam, out bool seen);
                        if (seen) MaybeShowHint();
                        if (!TeamMarkExternalPresenters.HasHitPresenter) crosshair = AimedMultiplier(cam);
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_warnedIcons) { _warnedIcons = true; NetLogger.Warn($"[TeamMark] presenter failed (reported once): {ex.GetType().Name}: {ex.Message}"); }
            }
            for (int i = used; i < _pool.Count; i++) if (_pool[i].enabled) _pool[i].enabled = false;
            try { ShowCrosshair(crosshair); }
            catch (Exception ex)
            {
                if (!_warnedCrosshair) { _warnedCrosshair = true; NetLogger.Warn($"[TeamMark] crosshair text failed (reported once): {ex.GetType().Name}: {ex.Message}"); }
            }
        }

        // ----------------------------------------------------------------- icons

        /// <param name="seen">A mark is on the player's screen: an icon of ours in view, or a unit in view whose marks
        /// another mod shows.</param>
        private int PlaceIcons(Camera cam, out bool seen)
        {
            int used = 0;
            seen = false;
            Vector3 camPos = cam.transform.position;
            Quaternion facing = cam.transform.rotation;
            int geometry = GeometryMask();
            foreach (var s in _shown)
            {
                if (s.Unit == null || !s.Unit.gameObject.activeInHierarchy) continue;
                if (TeamMarkExternalPresenters.IsShownElsewhere(s.Unit)) { seen |= InView(cam, HeadAnchor(s.Unit)); continue; }
                var sprite = TeamMarkArt.Get(s.OtherMarks);
                if (sprite == null) continue;
                Vector3 head = HeadAnchor(s.Unit);
                if (Physics.Linecast(camPos, head - Vector3.up * SightPointBelowHead, geometry, QueryTriggerInteraction.Ignore)) continue;
                var sr = Get(used++);
                Vector3 pos = head + Vector3.up * GapAboveHead;
                float dist = Vector3.Distance(camPos, pos);
                float width = Mathf.Max(IconWorldWidth, dist * MinAngularWidth);
                sr.sprite = sprite;
                sr.transform.SetPositionAndRotation(pos, facing);
                float scale = width / sprite.bounds.size.x;
                sr.transform.localScale = new Vector3(scale, scale, scale);
                if (!sr.enabled) sr.enabled = true;
                seen |= InView(cam, pos);
            }
            return used;
        }

        private static bool InView(Camera cam, Vector3 world)
        {
            var vp = cam.WorldToViewportPoint(world);
            return vp.z > 0f && vp.x >= 0f && vp.x <= 1f && vp.y >= 0f && vp.y <= 1f;
        }

        /// <summary>The top of the enemy: its authored overhead point, else the top of its capsule while that is enabled
        /// (a disabled collider reports empty bounds at the origin), else 2 m above its feet. Npc.EyesPosition is the
        /// feet for an NPC, so it is not used.</summary>
        private static Vector3 HeadAnchor(Unit unit)
        {
            if (unit is Npc npc && npc.overhead != null) return npc.overhead.position;
            var capsule = unit.GetComponent<CapsuleCollider>();
            if (capsule != null && capsule.enabled)
            {
                Bounds b = capsule.bounds;
                return b.center + Vector3.up * b.extents.y;
            }
            return unit.transform.position + Vector3.up * 2f;
        }

        private SpriteRenderer Get(int i)
        {
            while (_pool.Count <= i)
            {
                var go = new GameObject("TeamMarkIcon");
                go.transform.SetParent(transform, false);
                var sr = go.AddComponent<SpriteRenderer>();
                var mat = OnTopMaterial();
                if (mat != null) sr.sharedMaterial = mat;
                sr.enabled = false;
                sr.sortingOrder = 100;
                _pool.Add(sr);
            }
            return _pool[i];
        }

        /// <summary>The UI sprite material with its depth test off, so geometry in front of the icon (not of the enemy;
        /// that is the line-of-sight check) does not hide it. Null leaves the sprite default, depth-tested.</summary>
        private Material? OnTopMaterial()
        {
            if (_onTopMaterial != null) return _onTopMaterial;
            try
            {
                var baseMat = Graphic.defaultGraphicMaterial;
                if (baseMat == null) return null;
                _onTopMaterial = new Material(baseMat) { name = "SULFURTogether TeamMark (on top)" };
                _onTopMaterial.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.Always);
            }
            catch (Exception ex) { NetLogger.Warn($"[TeamMark] on-top material unavailable, icons are depth-tested: {ex.Message}"); _onTopMaterial = null; }
            return _onTopMaterial;
        }

        private static int GeometryMask()
        {
            try { var gm = GameManager.Instance; if (gm != null) return gm.geometryMask; } catch { }
            return Physics.DefaultRaycastLayers;
        }

        // ----------------------------------------------------------------- change event and hint

        private void RaiseIfChanged()
        {
            int sig = 17;
            foreach (var s in _shown)
                sig = unchecked(sig * 31 + (s.Unit != null ? s.Unit.GetInstanceID() : 0) * 7 + s.OtherMarks);
            if (_shown.Count == 0) sig = 0;
            if (sig == _lastSignature) return;
            _lastSignature = sig;
            NetTeamMarks.RaiseMultiplierChanged();
        }

        /// <summary>The one-time explanation, once a mark is actually on the player's screen and a toast can show.</summary>
        private void MaybeShowHint()
        {
            if (_hintDone) return;
            try
            {
                if (Plugin.Cfg.TeamMarksHintShown.Value) { _hintDone = true; return; }
                if (!UI.CoopToasts.CanShow) return; // no toast surface (yet): keep it for when one exists
                Plugin.Cfg.TeamMarksHintShown.Value = true;
            }
            catch { return; }
            _hintDone = true;
            UI.CoopToasts.NotifyTeamMarksHint();
        }

        // ----------------------------------------------------------------- crosshair multiplier

        /// <summary>The multiplier of the enemy the crosshair is on: the first thing along the aim that is a unit, if it
        /// carries another player's mark. 1 when it is unmarked, when level geometry comes first, or when the crosshair is
        /// not in play.</summary>
        private float AimedMultiplier(Camera cam)
        {
            var gm = GameManager.Instance;
            if (gm == null || gm.PlayerScript == null || gm.PlayerScript.hideCrosshair) return 1f;
            var self = gm.PlayerUnit;
            if (self == null || !self.IsAlive || NetPlayerLifeManager.IsDownedLocalPlayerUnit(self)) return 1f;
            if (Cursor.lockState != CursorLockMode.Locked) return 1f; // a menu, the inventory or a dialogue is open

            if (_skipLayers < 0) _skipLayers = LayerMask.GetMask("Player", "Weapons", "MeleeColliders", "Triggers");
            var ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            int n = Physics.RaycastNonAlloc(ray, _hits, AimRange, ~_skipLayers, QueryTriggerInteraction.Ignore);
            if (n <= 0) return 1f;
            Array.Sort(_hits, 0, n, HitDistance.Instance);
            int geometry = gm.geometryMask;
            for (int i = 0; i < n; i++)
            {
                var col = _hits[i].collider;
                if (col == null) continue;
                var unit = col.GetComponentInParent<Unit>();
                if (unit == self) continue;
                if (unit != null)
                {
                    foreach (var s in _shown) if (s.Unit == unit) return TeamMarkManager.MultiplierFor(s.OtherMarks);
                    return 1f; // an unmarked unit is in front
                }
                if ((geometry & (1 << col.gameObject.layer)) != 0) return 1f; // a wall first
            }
            return 1f;
        }

        private sealed class HitDistance : IComparer<RaycastHit>
        {
            public static readonly HitDistance Instance = new HitDistance();
            public int Compare(RaycastHit a, RaycastHit b) => a.distance.CompareTo(b.distance);
        }

        private void ShowCrosshair(float multiplier)
        {
            if (multiplier <= 1f)
            {
                if (_crosshairText != null && _crosshairText.enabled) _crosshairText.enabled = false;
                _crosshairShown = 1f;
                return;
            }
            EnsureCrosshairText();
            if (_crosshairText == null) return;
            if (multiplier != _crosshairShown)
            {
                _crosshairText.text = "×" + multiplier.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                _crosshairShown = multiplier;
            }
            if (!_crosshairText.enabled) _crosshairText.enabled = true;
        }

        private void EnsureCrosshairText()
        {
            if (_crosshairText != null) return;
            var canvasGo = new GameObject("TeamMarkCrosshair", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 50;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            var textGo = new GameObject("Multiplier", typeof(RectTransform));
            textGo.transform.SetParent(canvasGo.transform, false);
            var rt = (RectTransform)textGo.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.anchoredPosition = new Vector2(CrosshairOffsetX, CrosshairOffsetY);
            rt.sizeDelta = new Vector2(160f, 40f);
            var text = textGo.AddComponent<TextMeshProUGUI>();
            if (BindCrosshairFont != null) BindCrosshairFont(text); // already the bold face
            else
            {
                var font = UI.NativeFontSampler.ResolveNativeFont();
                if (font != null) text.font = font;
                text.fontStyle = FontStyles.Bold;
            }
            text.fontSize = CrosshairFontSize;
            text.color = HudOrange;
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.raycastTarget = false;
            text.enabled = false;
            _crosshairText = text;
        }

        private static Camera? ResolveCamera()
        {
            try
            {
                var gm = GameManager.Instance;
                if (gm != null && gm.currentCamera != null) return gm.currentCamera;
            }
            catch { }
            return Camera.main;
        }
    }
}
