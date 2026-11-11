using System;
using SULFURTogether.Networking;

namespace SULFURTogether.UI
{
    /// <summary>
    /// #26: the connect page's "Balance (host)" section — three host-authoritative session settings. One definition per
    /// setting (its labels, its stored value, its effective session value), shared by the page row, the change toast
    /// on both ends and anything else that names it.
    /// </summary>
    internal sealed class BalanceSetting
    {
        public readonly string LabelKey, LabelFallback, DescKey, DescFallback;
        private readonly Func<bool> _readStored;
        private readonly Action<bool> _writeStored;
        private readonly Func<bool> _readSession;

        public BalanceSetting(string labelKey, string labelFallback, string descKey, string descFallback,
            Func<bool> readStored, Action<bool> writeStored, Func<bool> readSession)
        {
            LabelKey = labelKey; LabelFallback = labelFallback; DescKey = descKey; DescFallback = descFallback;
            _readStored = readStored; _writeStored = writeStored; _readSession = readSession;
        }

        public string Label => CoopLoc.Get(LabelKey, LabelFallback);
        public string Description => CoopLoc.Get(DescKey, DescFallback);

        /// <summary>This machine's own stored choice (the session value while it hosts).</summary>
        public bool Stored { get { try { return _readStored(); } catch { return false; } } set { try { _writeStored(value); } catch { } } }

        /// <summary>What applies to the local end right now (host: its own choice; client: the host's).</summary>
        public bool Session { get { try { return _readSession(); } catch { return false; } } }
    }

    internal static class BalanceSettings
    {
        public static readonly BalanceSetting TeamMarks = new BalanceSetting(
            "session.teamMarks.label", "Team marks",
            "connect.desc.teamMarks", "Hitting an enemy a teammate just hit deals 50% more damage (75% with two teammates on it), and skulls above it show how many teammates are on it. The host's setting applies to the whole session.",
            () => Plugin.Cfg.TeamMarks.Value, v => Plugin.Cfg.TeamMarks.Value = v, () => NetSessionSettings.TeamMarksEnabled);

        public static readonly BalanceSetting EnemyHealth = new BalanceSetting(
            "session.enemyHealth.label", "Tougher enemies: health",
            "connect.desc.enemyHealth", "Enemies other than bosses have more health: x1.5 with 2 players, x1.75 with 3 or more. Takes effect from the next level. The host's setting applies to the whole session.",
            () => Plugin.Cfg.EnemyHealthBoost.Value, v => Plugin.Cfg.EnemyHealthBoost.Value = v, () => NetSessionSettings.EnemyHealthBoostEnabled);

        public static readonly BalanceSetting EnemyMutations = new BalanceSetting(
            "session.enemyMutations.label", "Tougher enemies: mutations",
            "connect.desc.enemyMutations", "Twice as many enemies are mutated. Takes effect from the next level. The host's setting applies to the whole session.",
            () => Plugin.Cfg.EnemyMutationBoost.Value, v => Plugin.Cfg.EnemyMutationBoost.Value = v, () => NetSessionSettings.EnemyMutationBoostEnabled);

        public static readonly BalanceSetting[] All = { TeamMarks, EnemyHealth, EnemyMutations };
    }
}
