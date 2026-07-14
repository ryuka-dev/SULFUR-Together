using UnityEngine;
using SULFURTogether.Networking;
using SULFURTogether.Networking.Vote;
using SULFURTogether.UI.Shared;

namespace SULFURTogether.UI.VoteOverlay
{
    /// <summary>
    /// UI-VOTE: drives the session-vote HUD overlay's lifecycle, content and animation. Mirrors the downed/rescue
    /// overlay's structure (build once, hidden by default, topmost sorting reapplied on show, lifecycle kept in a
    /// separate try/catch failure domain from animation).
    ///
    /// Reads only <see cref="CoopVoteManager.Current"/> — the host-authoritative snapshot — and never decides a vote
    /// result locally. Everything shown (border fractions, per-player squares, countdown) derives from that one
    /// snapshot, so the visual and the animation can never diverge (the DR lesson).
    /// </summary>
    internal static class VoteOverlayManager
    {
        private const float FadeSeconds = 0.4f;

        private static GameObject? _root;
        private static VoteOverlayPanelView? _panel;
        private static VitalityAnimator? _animator;

        private static bool  _shown;
        private static bool  _fading;
        private static float _fadeStart;
        private static float _lastEnergy;

        public static void Tick()
        {
            bool shouldAnimate;
            try
            {
                shouldAnimate = ApplyLifecycle();
            }
            catch (System.Exception ex)
            {
                WarnOnce($"lifecycle failed: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            if (!shouldAnimate) return;
            try { _animator!.Tick(_lastEnergy, Time.unscaledDeltaTime); }
            catch (System.Exception ex) { WarnOnce($"animation failed (panel stays shown): {ex.GetType().Name}: {ex.Message}"); }
        }

        public static void Shutdown()
        {
            if (_root != null) _root.SetActive(false);
            _shown = false;
            _fading = false;
        }

        private static bool ApplyLifecycle()
        {
            var snap = CoopVoteManager.Current;
            bool hasVote = snap != null && snap.HasVote;

            if (hasVote)
            {
                _fading = false;
                EnsureBuilt();
                if (!_root!.activeSelf)
                {
                    _root.SetActive(true);
                    ApplyTopmostSortingOrder();
                    _animator!.Reset();
                }
                _panel!.CanvasGroup.alpha = 1f;

                string localId = CoopVoteManager.LocalPeerId;
                _panel.Render(snap, localId);
                ApplyText(snap);
                _lastEnergy = EnergyFor(snap);
                _shown = true;
                return true;
            }

            // No current vote — fade out whatever was shown, then hide.
            if (!_shown) return false;
            EnsureBuilt();
            if (!_fading) { _fading = true; _fadeStart = Time.unscaledTime; }
            float t = FadeSeconds > 0f ? Mathf.Clamp01((Time.unscaledTime - _fadeStart) / FadeSeconds) : 1f;
            _panel!.CanvasGroup.alpha = 1f - t;
            if (t >= 1f) { Hide(); return false; }
            return true; // keep the gentle idle motion running during the fade
        }

        private static float EnergyFor(VoteStateSnapshot snap)
        {
            if (snap.Phase == VotePhase.Active) return snap.AgreeFraction;
            return snap.Outcome == VoteOutcome.Passed ? 1f : 0.08f;
        }

        private static void ApplyText(VoteStateSnapshot snap)
        {
            if (snap.Phase == VotePhase.Active)
            {
                _panel!.SetTitle(TitleForKind(snap.Kind));
                _panel.SetSub(CoopLoc.Format("vote.prompt",
                    "[Y] Agree   [N] Decline — {agree}/{total} agreed, {secs}s",
                    ("agree", snap.AgreeCount.ToString()),
                    ("total", snap.Total.ToString()),
                    ("secs", Mathf.CeilToInt(snap.SecondsRemaining).ToString())));
                return;
            }

            switch (snap.Outcome)
            {
                case VoteOutcome.Passed:
                    _panel!.SetTitle(CoopLoc.Get("vote.result.devEnabled", "Developer mode enabled"));
                    _panel.SetSub(CoopLoc.Format("vote.result.tally", "{agree}/{total} agreed",
                        ("agree", snap.AgreeCount.ToString()), ("total", snap.Total.ToString())));
                    break;
                case VoteOutcome.Cancelled:
                    _panel!.SetTitle(CoopLoc.Get("vote.result.cancelled", "Vote cancelled"));
                    _panel.SetSub(CoopLoc.Get("vote.result.cancelledSub", "Players changed"));
                    break;
                default: // Failed
                    _panel!.SetTitle(CoopLoc.Get("vote.result.failed", "Vote failed"));
                    _panel.SetSub(CoopLoc.Get("vote.result.devStaysOff", "Developer mode stays off"));
                    break;
            }
        }

        private static string TitleForKind(VoteKind kind)
        {
            switch (kind)
            {
                case VoteKind.EnableDevMode: return CoopLoc.Get("vote.title.devMode", "Enable developer mode?");
                default:                     return CoopLoc.Get("vote.title.generic", "Vote");
            }
        }

        private static void Hide()
        {
            if (_root != null) _root.SetActive(false);
            _shown = false;
            _fading = false;
        }

        private static void EnsureBuilt()
        {
            if (_root != null) return;
            _root = VoteOverlayCanvasBuilder.BuildRoot(out var panelRect);
            _panel = VoteOverlayPanelView.Create(panelRect, NativeFontSampler.ResolveNativeFont());
            _animator = new VitalityAnimator(_panel);
            Object.DontDestroyOnLoad(_root);
            _root.SetActive(false);
        }

        private static void ApplyTopmostSortingOrder()
        {
            var canvas = _root!.GetComponent<Canvas>();
            int max = 0;
            foreach (var other in Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None))
            {
                if (other == canvas) continue;
                if (other.sortingOrder > max) max = other.sortingOrder;
            }
            canvas.sortingOrder = max + 100;
        }

        private static string _lastWarned = "";
        private static void WarnOnce(string message)
        {
            if (message == _lastWarned) return;
            _lastWarned = message;
            NetLogger.Warn($"[VoteOverlay] {message}");
        }
    }
}
