using System;
using System.Collections.Generic;
using PerfectRandom.Sulfur.Core.Units;
using PerfectRandom.Sulfur.Core.Utilities;

namespace SULFURTogether.Networking.Gameplay.TeamMarks
{
    /// <summary>
    /// #26 "Tougher enemies": the per-level factors the two balance settings put on a co-op level.
    ///
    /// <para><b>Health</b> multiplies the max health of ordinary enemies (x1.5 with 2 players, x1.75 with 3 or more).
    /// <b>Mutations</b> multiplies the level generator's mutation chance (x2), which decides at generation time how many
    /// enemies are mutated — so both ends must generate a level with the same value or they grow different enemies.</para>
    ///
    /// <para><b>Owner.</b> The host fixes both when a level transition reaches generation (SwitchLevelRoutine, which every
    /// route goes through), from its own settings and the players on this run with it; a setting change or a player
    /// joining takes effect from the next level. The level then keeps the factors it was generated with, whatever
    /// happens to the session afterwards. The values travel with that level's generation input (the host scene
    /// request), and a client takes them from the host request for the level it is entering.</para>
    /// </summary>
    internal static class CoopBalance
    {
        public const float TwoPlayerHealthFactor = 1.5f;
        public const float ThreePlusPlayerHealthFactor = 1.75f;
        public const float MutationChanceFactor = 2f;

        /// <summary>The factors of the level this end is in (or generating).</summary>
        public static float LevelHealthFactor { get; private set; } = 1f;
        public static float LevelMutationFactor { get; private set; } = 1f;

        // Host: the factors each recent level was generated with, so a request names the factors of the level it
        // describes even while the host is already loading the next one.
        private static readonly Dictionary<string, (float Health, float Mutation)> _byLevel = new Dictionary<string, (float, float)>();
        private static readonly Queue<string> _byLevelOrder = new Queue<string>();
        private const int RememberedLevels = 8;

        private static string _prevChapter = "";
        private static int _prevLevel = -1;
        private static readonly Dictionary<int, bool> _healthEligible = new Dictionary<int, bool>();

        /// <summary>Any end: a level transition is about to generate <paramref name="chapter"/>:<paramref name="levelIndex"/>.</summary>
        public static void OnLevelTransition(string chapter, int levelIndex)
        {
            _healthEligible.Clear();
            float health = 1f, mutation = 1f;
            string source;
            if (!NetGameplaySyncBridge.IsSessionActive)
                source = "offline";
            else if (NetGameplaySyncBridge.IsHost)
            {
                if (!NetLinkState.HostLinked)
                    source = "host playing on its own";
                else
                {
                    int players = NetGameplaySyncBridge.CountPlayersWithHost(_prevChapter, _prevLevel, chapter, levelIndex);
                    if (NetSessionSettings.EnemyHealthBoostEnabled)
                        health = players >= 3 ? ThreePlusPlayerHealthFactor : players >= 2 ? TwoPlayerHealthFactor : 1f;
                    if (NetSessionSettings.EnemyMutationBoostEnabled && players >= 2)
                        mutation = MutationChanceFactor;
                    source = $"host players={players}";
                }
                Remember(chapter, levelIndex, health, mutation);
            }
            else if (NetClientLoadGate.TryGetLatestHostRequest(out var req) && req != null
                     && string.Equals(req.ChapterName, chapter, StringComparison.Ordinal) && req.LevelIndex == levelIndex)
            {
                health = req.EnemyHealthFactor;
                mutation = req.EnemyMutationFactor;
                source = "host request";
            }
            else
                source = "no matching host request";

            LevelHealthFactor = health;
            LevelMutationFactor = mutation;
            _prevChapter = chapter ?? "";
            _prevLevel = levelIndex;
            NetLogger.Info($"[Balance] {chapter}:{levelIndex} enemy health x{health:0.##}, mutation chance x{mutation:0.##} ({source})");
        }

        /// <summary>HOST: the factors <paramref name="chapter"/>:<paramref name="levelIndex"/> was generated with (1 when unknown).</summary>
        public static void GetFactorsFor(string chapter, int levelIndex, out float health, out float mutation)
        {
            if (_byLevel.TryGetValue(Key(chapter, levelIndex), out var f)) { health = f.Health; mutation = f.Mutation; }
            else { health = 1f; mutation = 1f; }
        }

        private static void Remember(string chapter, int levelIndex, float health, float mutation)
        {
            string key = Key(chapter, levelIndex);
            if (!_byLevel.ContainsKey(key))
            {
                _byLevelOrder.Enqueue(key);
                while (_byLevelOrder.Count > RememberedLevels) _byLevel.Remove(_byLevelOrder.Dequeue());
            }
            _byLevel[key] = (health, mutation);
        }

        private static string Key(string chapter, int levelIndex) => (chapter ?? "") + ":" + levelIndex;

        /// <summary>The max-health factor for <paramref name="owner"/> on this level: ordinary enemies only — never a
        /// player, a boss, a boss's add, or a protected NPC (traders, civilians).</summary>
        public static float HealthFactorFor(Unit owner)
        {
            float f = LevelHealthFactor;
            if (f == 1f || owner == null || owner.isPlayer || !(owner is Npc)) return 1f;
            int key = owner.GetInstanceID();
            if (_healthEligible.TryGetValue(key, out bool eligible)) return eligible ? f : 1f;

            var so = owner.unitSO;
            if (so == null || (so.unitType & UnitType.Boss) != 0 || so.isProtectedNpc || Boss.BossDynamicSpawnManifest.IsBossAdd(owner))
            {
                _healthEligible[key] = false;
                return 1f;
            }
            // A boss add reads its stats inside its own spawn, before it is recorded: leave it alone, uncached.
            if (Boss.BossDynamicSpawnManifest.IsBossSpawnPendingFor(owner)) return 1f;
            _healthEligible[key] = true;
            return f;
        }
    }
}
