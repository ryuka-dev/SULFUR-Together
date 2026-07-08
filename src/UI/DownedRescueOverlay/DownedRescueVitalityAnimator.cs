using UnityEngine;
using SULFURTogether.UI.RunStatsOverlay;

namespace SULFURTogether.UI.DownedRescueOverlay
{
    /// <summary>
    /// DR-4: derives a single continuous "vitality" value from rescue progress and drives every animated
    /// property from it — float height/speed, breathing scale, tilt, and (via DownedRescuePanelView.SetVitality)
    /// color/alpha. Never a discrete phase switch (design spec §6 explicitly forbids stage jump-cuts); vitality
    /// rises smoothly with progress via a smoothstep ease, with a small idle floor so the panel is never fully
    /// inert at 0%. Reuses RunStatsSpring (the existing underdamped-oscillator primitive) rather than inventing a
    /// second spring type. Runs on unscaledDeltaTime, matching the project's no-pause-invariant convention.
    /// </summary>
    internal sealed class DownedRescueVitalityAnimator
    {
        private const float IdleFloor = 0.05f;

        private readonly DownedRescuePanelView _panel;
        private readonly Vector2 _basePosition;

        private RunStatsSpring _floatSpring;
        private RunStatsSpring _breatheSpring;
        private RunStatsSpring _tiltSpring;
        private float _phase;

        public DownedRescueVitalityAnimator(DownedRescuePanelView panel)
        {
            _panel = panel;
            _basePosition = panel.Rect.anchoredPosition;
            Reset();
        }

        public void Reset()
        {
            _floatSpring.Snap(0f);
            _breatheSpring.Snap(1f);
            _tiltSpring.Snap(0f);
            _phase = 0f;
            _panel.Rect.anchoredPosition = _basePosition;
            _panel.Rect.localScale = Vector3.one;
            _panel.Rect.localRotation = Quaternion.identity;
        }

        public void Tick(float progress01, float dt)
        {
            float vitality = Mathf.Max(IdleFloor, Ease(progress01));
            _panel.SetVitality(vitality);

            _phase += dt * Mathf.Lerp(0.6f, 2.2f, vitality);

            float floatAmplitude = Mathf.Lerp(2f, 10f, vitality);
            _floatSpring.Tick(Mathf.Sin(_phase) * floatAmplitude, dt);

            float breatheAmplitude = Mathf.Lerp(0.01f, 0.06f, vitality);
            _breatheSpring.Tick(1f + Mathf.Sin(_phase * 0.8f + 1.3f) * breatheAmplitude, dt);

            float tiltAmplitude = Mathf.Lerp(0.5f, 3.5f, vitality);
            _tiltSpring.Tick(Mathf.Sin(_phase * 0.5f + 0.6f) * tiltAmplitude, dt);

            _panel.Rect.anchoredPosition = _basePosition + new Vector2(0f, _floatSpring.Value);
            _panel.Rect.localScale = new Vector3(_breatheSpring.Value, _breatheSpring.Value, 1f);
            _panel.Rect.localRotation = Quaternion.Euler(0f, 0f, _tiltSpring.Value);
        }

        private static float Ease(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t); // smoothstep — continuous, never a discrete stage jump
        }
    }
}
