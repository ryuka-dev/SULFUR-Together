using System.Collections.Generic;
using SULFURTogether.Networking.Gameplay;

namespace SULFURTogether.UI.RunStatsOverlay
{
    /// <summary>
    /// RS-6: per-stat "best performance across all players of this Run" marks, computed locally from the
    /// finalized stat list (no network message needed — every end already holds the identical Slot-ordered
    /// broadcast, and the rules below are pure functions of its values, so Host and Clients always derive the
    /// same highlights regardless of player order).
    ///
    /// Rules: five stats are best-when-highest (shots, damage dealt, kills, rescues, destructibles) and two are
    /// best-when-lowest (times downed, damage taken); every player tied on the best value is marked (never
    /// tie-broken by order); a stat where ALL players have the same value gets no mark at all — highlighting
    /// everyone (e.g. downed 0/0/0) distinguishes nobody and just adds noise.
    /// </summary>
    internal sealed class RunStatsBestMarks
    {
        // Stat index order matches RunStatsCardView's rows / NetRunStats field order:
        // 0 shots, 1 damage dealt, 2 kills, 3 times downed, 4 rescues, 5 damage taken, 6 destructibles.
        public const int StatCount = 7;
        private static readonly bool[] LowerIsBetter = { false, false, false, true, false, true, false };

        private readonly int[] _bestValue = new int[StatCount];
        private readonly bool[] _marked = new bool[StatCount];

        private RunStatsBestMarks() { }

        /// <summary>A no-mark instance for the placeholder card (no data to compare yet).</summary>
        public static readonly RunStatsBestMarks None = new RunStatsBestMarks();

        public static RunStatsBestMarks Compute(IReadOnlyList<NetRunStats> players)
        {
            var marks = new RunStatsBestMarks();
            if (players.Count == 0) return marks;

            for (int stat = 0; stat < StatCount; stat++)
            {
                int min = int.MaxValue, max = int.MinValue;
                foreach (var player in players)
                {
                    int value = GetStat(player, stat);
                    if (value < min) min = value;
                    if (value > max) max = value;
                }
                marks._bestValue[stat] = LowerIsBetter[stat] ? min : max;
                // min == max also covers a single-player Run: alone, nobody is "best at everything".
                marks._marked[stat] = min != max;
            }
            return marks;
        }

        public bool IsBest(int statIndex, int value)
        {
            return _marked[statIndex] && value == _bestValue[statIndex];
        }

        private static int GetStat(NetRunStats stats, int statIndex)
        {
            switch (statIndex)
            {
                case 0: return stats.ShotsFired;
                case 1: return stats.DamageDealt;
                case 2: return stats.Kills;
                case 3: return stats.TimesDowned;
                case 4: return stats.Rescues;
                case 5: return stats.DamageTaken;
                default: return stats.DestructiblesDestroyed;
            }
        }
    }
}
