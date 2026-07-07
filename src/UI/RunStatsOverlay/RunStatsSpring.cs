using UnityEngine;

namespace SULFURTogether.UI.RunStatsOverlay
{
    /// <summary>
    /// RS-4: a slightly under-damped harmonic spring — the shared motion primitive behind the Balatro-style
    /// card feel (hover follow, release rebound with a small overshoot, carousel slide). Under-damped on
    /// purpose: a critically-damped ease (SmoothDamp/exponential lerp) reads as a mechanical menu tween,
    /// while a damping ratio just below 1 gives the soft "settles with a breath" motion the spec asks for
    /// without visible wobble.
    /// </summary>
    internal struct RunStatsSpring
    {
        // angularFrequency: response speed (rad/s); dampingRatio < 1 = slight overshoot on arrival.
        private const float AngularFrequency = 14f;
        private const float DampingRatio = 0.75f;
        // A hitch frame with a huge dt would make the integrator overshoot wildly — clamp to a 30fps step.
        private const float MaxStep = 1f / 30f;

        public float Value;
        private float _velocity;

        public void Snap(float value)
        {
            Value = value;
            _velocity = 0f;
        }

        public float Tick(float target, float deltaTime)
        {
            float dt = Mathf.Min(deltaTime, MaxStep);
            _velocity += (-2f * DampingRatio * AngularFrequency * _velocity
                          - AngularFrequency * AngularFrequency * (Value - target)) * dt;
            Value += _velocity * dt;
            return Value;
        }
    }
}
