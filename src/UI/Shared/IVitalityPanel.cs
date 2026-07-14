using UnityEngine;

namespace SULFURTogether.UI.Shared
{
    /// <summary>
    /// The surface <see cref="VitalityAnimator"/> drives: it moves the panel's <see cref="Rect"/> (float / breathe /
    /// tilt) and pushes a single continuous 0..1 "vitality" value the panel maps to its own colours. Lets the
    /// downed/rescue panel and the vote panel share one animator without sharing their palettes.
    /// </summary>
    internal interface IVitalityPanel
    {
        RectTransform Rect { get; }
        void SetVitality(float vitality01);
    }
}
