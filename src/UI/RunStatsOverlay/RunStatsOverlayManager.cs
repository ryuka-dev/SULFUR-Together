using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using SULFURTogether.Networking;
using SULFURTogether.Networking.Gameplay;

namespace SULFURTogether.UI.RunStatsOverlay
{
    /// <summary>
    /// RS-2/RS-3/RS-4: drives the run-stats card overlay's lifecycle. Shows the moment a finalized broadcast is
    /// pending AND the local machine is in a loading-like game state (so it appears over the Hub-return loading
    /// screen on both Host and Client), hides + consumes the pending data the instant that loading state ends —
    /// plus a few forced-hide safety nets (network session ended, left the game scene) so the overlay and its
    /// cursor-freeing can never get stuck on. While shown, also drives the carousel scroll input and each
    /// card's hover-tilt animation.
    /// </summary>
    internal static class RunStatsOverlayManager
    {
        private static GameObject? _root;
        private static RectTransform? _viewport;
        private static RectTransform? _track;
        private static readonly RunStatsCarouselController _carousel = new RunStatsCarouselController();
        private static readonly List<RunStatsCardView> _cards = new List<RunStatsCardView>();
        private static readonly List<RunStatsCardHoverAnimator> _hoverAnimators = new List<RunStatsCardHoverAnimator>();
        private static IReadOnlyList<NetRunStats>? _boundList;
        private static bool _shownLastTick;

        public static void Tick()
        {
            // Lifecycle (show/hide/cursor) and interaction (scroll/hover) are deliberately separate failure
            // domains: LogOutput374/375 proved a per-frame exception in the interaction code silently aborted
            // Tick BEFORE _shownLastTick was committed, wedging the overlay on-screen forever (the hide branch
            // keys on that flag). Interaction is decoration — it must never be able to corrupt the lifecycle.
            bool shouldShow;
            try
            {
                shouldShow = ApplyLifecycle();
            }
            catch (System.Exception ex)
            {
                WarnOnce($"lifecycle failed: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            if (!shouldShow) return;
            try
            {
                TickInteraction(Time.unscaledDeltaTime);
            }
            catch (System.Exception ex)
            {
                WarnOnce($"interaction failed (cards stay shown, animation skipped): {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Runs the show/hide state machine and commits <see cref="_shownLastTick"/>. Returns whether
        /// the overlay is currently shown.</summary>
        private static bool ApplyLifecycle()
        {
            // No maximum display time by design: while the loading screen persists the player may study the
            // cards for as long as they like; the display ends exactly when the loading state ends (or one of
            // the safety conditions below goes false — session over, left the game scene).
            bool loading = NetRunStateBridge.TryGetLocalRunState(out var state) && state.IsLoadingLikeState;
            bool pending = NetRunStatsClientCache.PendingRunEndDisplay;
            bool shouldShow = pending && loading && IsNetworkSessionActive() && IsInGameScene();

            if (shouldShow)
            {
                EnsureBuilt();
                if (_root != null && !_root.activeSelf)
                {
                    _root.SetActive(true);
                    ApplyTopmostSortingOrder();
                    NetLogger.Info("[RunStatsOverlay] show");
                }
                RunStatsCursorControl.Acquire();
                RunStatsCursorControl.Enforce();
                RefreshIfChanged();
            }
            else if (_shownLastTick)
            {
                NetLogger.Info($"[RunStatsOverlay] hide (loading={loading} pending={pending} networkActive={IsNetworkSessionActive()} inGameScene={IsInGameScene()})");
                HideAndClear();
            }

            _shownLastTick = shouldShow;
            return shouldShow;
        }

        // One line per distinct failure (not per frame): Unity's own exception log isn't passed through to the
        // BepInEx log in this profile, so without this the failure is completely invisible (LogOutput375).
        private static string _lastWarned = "";
        private static void WarnOnce(string message)
        {
            if (message == _lastWarned) return;
            _lastWarned = message;
            NetLogger.Warn($"[RunStatsOverlay] {message}");
        }

        /// <summary>Force everything off — called from Plugin.OnDestroy so a mod reload/shutdown can never
        /// leave the cursor stuck freed.</summary>
        public static void Shutdown()
        {
            if (_shownLastTick) HideAndClear();
            _shownLastTick = false;
        }

        private static void TickInteraction(float dt)
        {
            int delta = RunStatsInputReader.PollDelta();
            if (delta != 0) _carousel.MoveFocus(delta);
            _carousel.Tick(dt);

            // Exactly one "hot" card: the first one under the pointer, and only while the pointer is inside
            // the viewport — a card scrolled out of the visible window is clipped invisible and must not keep
            // reacting to a cursor that happens to sit over its clipped-away rect.
            bool havePointer = RunStatsInputReader.TryGetPointerPosition(out var pointer)
                && _viewport != null
                && RectTransformUtility.RectangleContainsScreenPoint(_viewport, pointer, null);
            int hotIndex = -1;
            if (havePointer)
            {
                for (int i = 0; i < _hoverAnimators.Count; i++)
                {
                    if (!_hoverAnimators[i].IsPointerOver(pointer)) continue;
                    hotIndex = i;
                    break;
                }
            }

            for (int i = 0; i < _hoverAnimators.Count; i++)
                _hoverAnimators[i].Tick(dt, i == hotIndex, pointer);
        }

        private static bool IsNetworkSessionActive()
        {
            return NetConfig.GetMode() switch
            {
                NetMode.Host => true,
                NetMode.Client => NetClientJoinFlow.SessionJoinedHost,
                _ => false,
            };
        }

        /// <summary>SULFUR runs all gameplay (hub + every generated level) inside one persistent scene,
        /// <c>GameScene</c>; the title screen is its own <c>MainMenu</c> scene (same signal CoopConnectPage
        /// uses to gate Create/Join). Guards against the overlay/cursor-free surviving a return to the title.</summary>
        private static bool IsInGameScene()
        {
            try { return string.Equals(SceneManager.GetActiveScene().name, "GameScene", System.StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        private static void EnsureBuilt()
        {
            if (_root != null) return;
            _root = RunStatsCanvasBuilder.BuildRoot(out var viewport, out var track);
            _viewport = viewport;
            _track = track;
            _carousel.SetTrack(track);
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _root.SetActive(false);
        }

        /// <summary>Recomputed on every show (not cached once) — a scene/loading-screen canvas created after ours
        /// could otherwise end up drawing above the cards.</summary>
        private static void ApplyTopmostSortingOrder()
        {
            var canvas = _root!.GetComponent<Canvas>();
            int max = 0;
            foreach (var other in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None))
            {
                if (other == canvas) continue;
                if (other.sortingOrder > max) max = other.sortingOrder;
            }
            canvas.sortingOrder = max + 100;
        }

        private static void RefreshIfChanged()
        {
            var list = NetRunStatsClientCache.LastFinalized;
            if (ReferenceEquals(list, _boundList)) return;
            _boundList = list;
            RebuildCards(list);
        }

        private static void RebuildCards(IReadOnlyList<NetRunStats>? list)
        {
            // Deactivate before Destroy (which is deferred to end-of-frame) so a stale card's own TMP text can
            // never be mistaken for a "currently active native" font source by ResolveNativeFont below.
            foreach (var card in _cards) { card.Root.SetActive(false); UnityEngine.Object.Destroy(card.Root); }
            _cards.Clear();
            _hoverAnimators.Clear();
            if (_track == null) return;

            // Re-resolved per Run (not cached for the process lifetime) so a language change since the last
            // Run is picked up, and every card in this batch shares one consistent font.
            var font = ResolveNativeFont();

            if (list == null || list.Count == 0)
            {
                var placeholder = RunStatsCardView.Create(_track, font);
                placeholder.SetEmpty();
                _cards.Add(placeholder);
            }
            else
            {
                string localPeerId = NetRunStateBridge.TryGetLocalRunState(out var local) ? local.PeerId : "";
                foreach (var stats in list)
                {
                    var card = RunStatsCardView.Create(_track, font);
                    card.Bind(stats, stats.PeerId == localPeerId);
                    _cards.Add(card);
                }
            }

            _carousel.ResetForNewData(_cards.Count);

            // Force the layout pass NOW so each hover animator captures the card's real layout-assigned
            // baseline position (its animation is deltas around that baseline; capturing before layout ran
            // would bake a (0,0) baseline in and teleport every card on the first animated frame).
            // Cards first (their ContentSizeFitters resolve their heights), then the track row over them.
            foreach (var card in _cards)
                UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(card.Rect);
            UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(_track);
            foreach (var card in _cards)
                _hoverAnimators.Add(new RunStatsCardHoverAnimator(card));
        }

        private static void HideAndClear()
        {
            if (_root != null) _root.SetActive(false);
            RunStatsCursorControl.Release();
            NetRunStatsClientCache.ConsumeAndClear();
            _boundList = null;
        }

        /// <summary>Samples the font off any currently active native TextMeshProUGUI — the same trick the SULFUR
        /// Native UI Lib uses for its own banner/toasts (Docs/Localization.md "Producer note"): the game already
        /// keeps its own UI text components on the correct current-language font (CJK fallback chain included),
        /// so copying that reference is simpler and more correct than trying to track the language ourselves.
        /// Falls back to the project's TMP default if no active native text can be found (e.g. a bare loading
        /// screen with no TMP components at all).</summary>
        private static TMP_FontAsset? ResolveNativeFont()
        {
            try
            {
                foreach (var tmp in UnityEngine.Object.FindObjectsByType<TextMeshProUGUI>(FindObjectsSortMode.None))
                {
                    if (tmp == null || tmp.font == null) continue;
                    if (!tmp.gameObject.activeInHierarchy) continue;
                    return tmp.font;
                }
            }
            catch { /* best-effort — fall through to default */ }

            try { return TMP_Settings.defaultFontAsset; }
            catch { return null; }
        }
    }
}
