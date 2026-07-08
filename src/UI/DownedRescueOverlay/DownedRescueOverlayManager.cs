using UnityEngine;
using SULFURTogether.Networking;
using SULFURTogether.Networking.Gameplay;

namespace SULFURTogether.UI.DownedRescueOverlay
{
    /// <summary>
    /// DR-2 through DR-5: drives the downed/rescue HUD overlay's lifecycle, content, and animation. Mirrors
    /// RunStatsOverlayManager's structure (this mod's only proven template for a passive, always-running uGUI
    /// overlay) — built once, hidden by default, topmost sorting reapplied on every show, and lifecycle kept in
    /// a separate try/catch failure domain from animation (the hard-learned RS lesson: a broken animation must
    /// never wedge the show/hide flag — see phase-rs-runstats-cards memory, Log374/375).
    ///
    /// Reads only NetPlayerLifeManager's public, host-authoritative rescue state — this class is never a second
    /// source of truth for whether/how a rescue is progressing.
    /// </summary>
    internal static class DownedRescueOverlayManager
    {
        private enum EndFadePhase { None, Hold, Fade }

        private const float CompletedHoldSeconds = 0.6f;
        private const float CompletedFadeSeconds = 0.4f;
        private const float CancelledFadeSeconds = 0.3f;

        private static GameObject? _root;
        private static DownedRescuePanelView? _panel;
        private static DownedRescueVitalityAnimator? _animator;

        private static bool _shownLastTick;
        private static EndFadePhase _endPhase = EndFadePhase.None;
        private static float _endPhaseStartedAt;
        private static string _endMainText = "";
        private static string _endSubText = "";

        // The progress value actually being rendered THIS tick (border stroke + vitality both read this, never
        // NetPlayerLifeManager.CurrentRescueDisplay.Progress directly) — that cache is host-authoritative but
        // NOT reset back to 0 once a rescue completes (it's left at 1 so the completion hold/fade above can show
        // a closed border); reading it straight from Tick() made a brand-new idle/hint display (no active rescue
        // at all) inherit the PREVIOUS rescue's 100% vitality until the next hold start/stop touched it. Set
        // alongside every SetBorderProgress call so the two can never disagree.
        private static float _currentProgressForAnimation;

        public static void Tick()
        {
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
                _animator!.Tick(_currentProgressForAnimation, Time.unscaledDeltaTime);
            }
            catch (System.Exception ex)
            {
                WarnOnce($"animation failed (panel stays shown, animation skipped): {ex.GetType().Name}: {ex.Message}");
            }
        }

        public static void Shutdown()
        {
            if (_root != null) _root.SetActive(false);
            _shownLastTick = false;
            _endPhase = EndFadePhase.None;
        }

        private static string _lastWarned = "";
        private static void WarnOnce(string message)
        {
            if (message == _lastWarned) return;
            _lastWarned = message;
            NetLogger.Warn($"[DownedRescueOverlay] {message}");
        }

        private static bool ApplyLifecycle()
        {
            string localPeerId = NetPlayerLifeManager.LocalPeerId;
            var rescue = NetPlayerLifeManager.CurrentRescueDisplay;
            bool locallyDowned = NetPlayerLifeManager.ShouldSuppressLocalPlayerControls();

            bool rescueInvolvesLocal = rescue.RescuerPeerId == localPeerId || rescue.TargetPeerId == localPeerId;

            // A new rescue started for the local player before the previous one's end-fade finished (e.g. the
            // rescuer immediately holds for a different downed teammate) — abandon the fade and resume live
            // display instead of fighting it.
            if (_endPhase != EndFadePhase.None && rescue.Active && rescueInvolvesLocal)
                _endPhase = EndFadePhase.None;

            // DR-5: the instant Active flips false, the role that was just active (rescuer or downed target)
            // would otherwise vanish with no feedback. Start a brief hold+fade so a completed rescue can show its
            // "done" text before disappearing; a cancelled one just fades quickly with no text change.
            if (_endPhase == EndFadePhase.None && _wasActiveLastTick && !rescue.Active && rescueInvolvedLocalLastTick)
            {
                _endPhase = rescue.LastEndReason == NetPlayerLifeManager.RescueEndReason.Completed ? EndFadePhase.Hold : EndFadePhase.Fade;
                _endPhaseStartedAt = Time.unscaledTime;
                bool wasDownedTarget = _lastTargetPeerId == localPeerId;
                _endMainText = wasDownedTarget
                    ? CoopLoc.Get("rescue.complete.restored", "Restored")
                    : CoopLoc.Get("rescue.complete.rescuer", "Rescue complete");
                _endSubText = "";
            }

            _wasActiveLastTick = rescue.Active;
            rescueInvolvedLocalLastTick = rescueInvolvesLocal;
            _lastTargetPeerId = rescue.TargetPeerId;

            if (_endPhase != EndFadePhase.None)
                return TickEndFade(rescue);

            string hintPeerId = "", hintName = "";
            float hintDistance = 0f;
            bool haveNearestDownedHint = !locallyDowned && NetPlayerLifeManager.TryGetNearestDownedPeerHint(out hintPeerId, out hintName, out hintDistance);

            bool showAsRescuer = !locallyDowned && rescue.Active && rescue.RescuerPeerId == localPeerId;
            bool showAsDownedActive = locallyDowned && rescue.Active && rescue.TargetPeerId == localPeerId;
            bool showAsDownedIdle = locallyDowned && !showAsDownedActive;
            bool showAsRescuerIdle = !locallyDowned && !showAsRescuer && haveNearestDownedHint;

            bool shouldShow = showAsRescuer || showAsDownedActive || showAsDownedIdle || showAsRescuerIdle;

            if (!shouldShow)
            {
                if (_shownLastTick) Hide();
                return false;
            }

            EnsureBuilt();
            if (!_root!.activeSelf)
            {
                _root.SetActive(true);
                ApplyTopmostSortingOrder();
                _animator!.Reset();
            }
            _panel!.CanvasGroup.alpha = 1f;

            if (showAsDownedActive)
            {
                string rescuerName = NetPlayerLifeManager.GetKnownPeerDisplayName(rescue.RescuerPeerId);
                _panel.SetMainText(CoopLoc.Format("rescue.downed.active", "{name} is rescuing you", ("name", rescuerName)));
                _panel.SetSubText(CoopLoc.Get("rescue.downed.hangOn", "Hang on"));
                _panel.SetBorderProgress(rescue.Progress);
                _currentProgressForAnimation = rescue.Progress;
            }
            else if (showAsDownedIdle)
            {
                _panel.SetMainText(CoopLoc.Get("rescue.downed.waiting", "Waiting for a teammate to revive you"));
                _panel.SetSubText("");
                _panel.SetBorderProgress(0f);
                _currentProgressForAnimation = 0f;
            }
            else if (showAsRescuer)
            {
                string targetName = NetPlayerLifeManager.GetKnownPeerDisplayName(rescue.TargetPeerId);
                string key = Plugin.Cfg.PlayerReviveHoldKey.Value.MainKey.ToString();
                _panel.SetMainText(CoopLoc.Format("rescue.rescuer.active", "Rescuing {name}", ("name", targetName)));
                _panel.SetSubText(CoopLoc.Format("rescue.hold", "Hold [{key}]", ("key", key)));
                _panel.SetBorderProgress(rescue.Progress);
                _currentProgressForAnimation = rescue.Progress;
            }
            else // showAsRescuerIdle
            {
                string key = Plugin.Cfg.PlayerReviveHoldKey.Value.MainKey.ToString();
                _panel.SetMainText(CoopLoc.Format("rescue.rescuer.idle", "Rescue {name}", ("name", hintName)));
                _panel.SetSubText(CoopLoc.Format("rescue.hold", "Hold [{key}]", ("key", key)));
                _panel.SetBorderProgress(0f);
                _currentProgressForAnimation = 0f;
            }

            _shownLastTick = true;
            return true;
        }

        private static bool _wasActiveLastTick;
        private static bool rescueInvolvedLocalLastTick;
        private static string _lastTargetPeerId = "";

        private static bool TickEndFade(NetPlayerLifeManager.RescueDisplayState rescue)
        {
            EnsureBuilt();
            float elapsed = Time.unscaledTime - _endPhaseStartedAt;

            if (_endPhase == EndFadePhase.Hold)
            {
                if (!_root!.activeSelf) { _root.SetActive(true); ApplyTopmostSortingOrder(); }
                _panel!.CanvasGroup.alpha = 1f;
                _panel.SetMainText(_endMainText);
                _panel.SetSubText(_endSubText);
                _panel.SetBorderProgress(1f);
                _currentProgressForAnimation = 1f;
                if (elapsed >= CompletedHoldSeconds)
                {
                    _endPhase = EndFadePhase.Fade;
                    _endPhaseStartedAt = Time.unscaledTime;
                }
                _shownLastTick = true;
                return true;
            }

            // Fade
            float fadeDuration = _endMainText.Length > 0 ? CompletedFadeSeconds : CancelledFadeSeconds;
            float t = fadeDuration > 0f ? Mathf.Clamp01(elapsed / fadeDuration) : 1f;
            if (!_root!.activeSelf) { _root.SetActive(true); ApplyTopmostSortingOrder(); }
            _panel!.CanvasGroup.alpha = 1f - t;

            if (t >= 1f)
            {
                Hide();
                _endPhase = EndFadePhase.None;
                _endMainText = "";
                return false;
            }

            _shownLastTick = true;
            return true;
        }

        private static void Hide()
        {
            if (_root != null) _root.SetActive(false);
            _shownLastTick = false;
            _currentProgressForAnimation = 0f; // never read while hidden — reset only so a stale value can't look like a bug to a future reader
        }

        private static void EnsureBuilt()
        {
            if (_root != null) return;
            _root = DownedRescueCanvasBuilder.BuildRoot(out var panelRect);
            _panel = DownedRescuePanelView.Create(panelRect, NativeFontSampler.ResolveNativeFont());
            _animator = new DownedRescueVitalityAnimator(_panel);
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
    }
}
