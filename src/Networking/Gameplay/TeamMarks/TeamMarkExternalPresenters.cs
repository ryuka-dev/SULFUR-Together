using System;
using System.Collections.Generic;
using PerfectRandom.Sulfur.Core.Units;

namespace SULFURTogether.Networking.Gameplay.TeamMarks
{
    /// <summary>
    /// Team marks (issue #26): what other mods registered through <c>SULFURTogether.Api.NetTeamMarks</c> to show
    /// themselves. A mark presenter (a health bar mod) takes over the icon on the units it says it shows; a hit
    /// presenter (a damage number mod) takes over the crosshair multiplier. Main thread only.
    /// </summary>
    internal static class TeamMarkExternalPresenters
    {
        private sealed class Registration : IDisposable
        {
            private readonly List<Registration> _owner;
            public readonly Func<object, bool>? IsShowing;
            public Registration(List<Registration> owner, Func<object, bool>? isShowing) { _owner = owner; IsShowing = isShowing; }
            public void Dispose() { _owner.Remove(this); _warned.Remove(this); }
        }

        private static readonly List<Registration> _markPresenters = new List<Registration>();
        private static readonly List<Registration> _hitPresenters = new List<Registration>();

        public static IDisposable AddMarkPresenter(Func<object, bool> isShowing)
        {
            if (isShowing == null) throw new ArgumentNullException(nameof(isShowing));
            var r = new Registration(_markPresenters, isShowing);
            _markPresenters.Add(r);
            return r;
        }

        public static IDisposable AddHitPresenter()
        {
            var r = new Registration(_hitPresenters, null);
            _hitPresenters.Add(r);
            return r;
        }

        public static bool HasHitPresenter => _hitPresenters.Count > 0;

        /// <summary>Some registered mark presenter shows this unit's marks itself. A presenter that throws counts as not
        /// showing it and is logged once.</summary>
        public static bool IsShownElsewhere(Unit unit)
        {
            // Backwards, so a presenter that disposes itself from its callback does not make the loop skip another.
            for (int i = _markPresenters.Count - 1; i >= 0; i--)
            {
                if (i >= _markPresenters.Count) continue;
                var r = _markPresenters[i];
                try { if (r.IsShowing!(unit)) return true; }
                catch (Exception ex)
                {
                    if (_warned.Add(r))
                        Plugin.Log.Warn($"[TeamMark] a mark presenter ({r.IsShowing!.Method.DeclaringType?.FullName}.{r.IsShowing.Method.Name}) threw: {ex.GetType().Name}: {ex.Message}");
                }
            }
            return false;
        }

        private static readonly HashSet<Registration> _warned = new HashSet<Registration>();
    }
}
