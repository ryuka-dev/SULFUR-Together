using System;
using PerfectRandom.Sulfur.Core.Units;
using SULFURTogether.Networking.Gameplay.TeamMarks;

namespace SULFURTogether.Api
{
    /// <summary>
    /// Team marks (API 2, see <see cref="NetCombat.ApiVersion"/>). In a co-op session each player's direct gun or melee
    /// hits put their mark on an enemy; another player's direct hit on a marked enemy deals more damage: x1.5 for one
    /// mark that is not the attacker's own, x1.75 for two or more. The host decides every bonus; a client sees the marks
    /// the host last sent. SULFUR Together shows a skull icon above a marked enemy and the multiplier next to the
    /// crosshair; a companion mod can take either over.
    ///
    /// <para>Everything here is main thread only and answers for the local player. Offline, with the host's Team marks
    /// setting off, or for a unit that is not an ordinary enemy, the multiplier is 1 and the mark count 0.</para>
    /// </summary>
    public static class NetTeamMarks
    {
        /// <summary>The multiplier the local player's next direct hit on <paramref name="unit"/> (a game <c>Unit</c>) would
        /// get from the other players' marks, as this end knows them now: 1, 1.5 or 1.75. On a client it is a forecast
        /// from the host's last message; the bonus a hit actually got is <see cref="LocalHitResolvedArgs.TeamMultiplier"/>
        /// (client) or <see cref="NetCombat.LocalHostHitMultiplier"/> (host).</summary>
        public static float GetMultiplier(object unit) => TeamMarkManager.MultiplierFor(GetOtherMarkCount(unit));

        /// <summary>How many players other than the local one have their mark on <paramref name="unit"/> (a game
        /// <c>Unit</c>). This is the number of skulls SULFUR Together's icon shows.</summary>
        public static int GetOtherMarkCount(object unit)
            => unit is Unit u && u != null ? TeamMarkManager.LocalOtherMarkCount(u) : 0;

        /// <summary>Raised on the main thread, at most once per frame, when the marks this end shows change: a mark was
        /// placed, moved or ran out, a marked enemy died, or the setting or the session changed (detected from a per-frame
        /// summary, so in a rare collision one change can go unreported until the next). Read
        /// <see cref="GetOtherMarkCount"/> for the units you show. A subscriber that throws is logged and does not stop
        /// the others.</summary>
        public static event Action? MultiplierChanged;

        /// <summary>Register a mod that shows marks on enemies itself (e.g. on its health bars). While
        /// <paramref name="isShowing"/> returns true for a unit (a game <c>Unit</c>), SULFUR Together does not draw its
        /// icon above that unit. It is called every frame for each marked unit, so keep it cheap. Dispose the result to
        /// unregister.</summary>
        public static IDisposable RegisterMarkPresenter(Func<object, bool> isShowing)
            => TeamMarkExternalPresenters.AddMarkPresenter(isShowing);

        /// <summary>Register a mod that shows the multiplier of each hit itself (e.g. next to its damage numbers). While
        /// any is registered, SULFUR Together does not show the multiplier next to the crosshair. Register only while the
        /// multiplier can actually be shown. Dispose the result to unregister.</summary>
        public static IDisposable RegisterHitPresenter() => TeamMarkExternalPresenters.AddHitPresenter();

        internal static void RaiseMultiplierChanged()
        {
            var handlers = MultiplierChanged;
            if (handlers == null) return;
            foreach (var d in handlers.GetInvocationList())
            {
                try { ((Action)d)(); }
                catch (Exception ex)
                {
                    Plugin.Log.Warn($"[NetTeamMarks] a MultiplierChanged subscriber ({d.Method.DeclaringType?.FullName}.{d.Method.Name}) threw: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }
}
