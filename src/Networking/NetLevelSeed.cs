using System;
using System.Reflection;

namespace SULFURTogether.Networking
{
    /// <summary>
    /// Phase 3.4.1 level seed helper.
    ///
    /// This intentionally avoids fragile hooks on tiny auto-property setters and
    /// compiler-generated coroutine methods. Instead, existing stable GameManager
    /// hooks provide the current GameManager instance, then level-generation node
    /// hooks / the network tick poll GameManager.currentSeed after the game has
    /// chosen the seed.
    ///
    /// Apply path remains targeted: GlobalSettings.ForceLevelSeed.
    /// </summary>
    internal static class NetLevelSeed
    {
        private static bool _forceLevelSeedResolved;
        private static FieldInfo? _forceLevelSeedField;

        // SEED-1: ForceLevelSeed is a ONE-SHOT generation input, not a persistent setting. It is written to
        // reproduce the host's instance for the NEXT generation and must be released once that generation has
        // consumed it — StartLevelRoutineGraph only rolls a fresh random seed when the field reads 0, so a
        // leftover value silently pins every later local generation to the same map. Remember what we wrote so
        // a value set by someone else (game dev tools) is never cleared out from under them.
        private static bool _forceSeedAppliedByUs;
        private static int  _forceSeedAppliedValue;

        private static bool _currentSeedResolved;
        private static PropertyInfo? _currentSeedProperty;
        private static FieldInfo? _currentSeedField;
        private static Type? _currentSeedOwnerType;

        private static WeakReference? _observedGameManager;
        private static string _observedSource = "";
        private static int _lastReportedSeed;
        private static int _transitionBaselineSeed;
        private static bool _transitionBaselineKnown;

        public static void ObserveGameManager(object? gameManager, string source)
        {
            if (gameManager == null) return;
            _observedGameManager = new WeakReference(gameManager);
            _observedSource = string.IsNullOrWhiteSpace(source) ? "GameManager" : source;
        }

        /// <summary>Clear the "already reported this seed" latch so the next <see cref="ReportObservedGameManagerSeed"/>
        /// re-reports the current seed even if it is unchanged. Used when networking (re)starts after a save was
        /// already loaded: the latch was set while no service was attached, so the freshly started service would
        /// otherwise never receive the current seed (Log186).</summary>
        public static void ResetReportLatch()
        {
            _lastReportedSeed = 0;
            _transitionBaselineKnown = false;
        }

        public static void BeginLevelTransition(object? gameManager, string source)
        {
            ObserveGameManager(gameManager, source);

            _transitionBaselineKnown = false;
            _transitionBaselineSeed = 0;

            if (gameManager == null) return;
            if (TryReadCurrentSeed(gameManager, out var seed, out _) && seed != 0)
            {
                _transitionBaselineKnown = true;
                _transitionBaselineSeed = seed;
            }
        }

        public static void ReportObservedGameManagerSeed(string source)
        {
            try
            {
                if (!SULFURTogether.Plugin.Cfg.EnableLevelSeedAuthority.Value) return;
                if (_observedGameManager == null) return;
                var gameManager = _observedGameManager.Target;
                if (gameManager == null) return;

                if (!TryReadCurrentSeed(gameManager, out var seed, out var detail)) return;
                if (seed == _lastReportedSeed) return;
                if (_transitionBaselineKnown && seed == _transitionBaselineSeed) return;

                _transitionBaselineKnown = false;
                _lastReportedSeed = seed;
                var generatorName = string.IsNullOrWhiteSpace(source)
                    ? _observedSource
                    : source;
                if (string.IsNullOrWhiteSpace(generatorName)) generatorName = "GameManager.currentSeed";

                NetRunStateBridge.ReportLevelSeed(seed, generatorName);

                if (SULFURTogether.Plugin.Cfg.EnableDebugLog.Value)
                    SULFURTogether.Plugin.Log.Debug($"[LevelSeed] Captured currentSeed={seed} via {detail} source={generatorName}");
            }
            catch { }
        }

        public static bool TryReadCurrentSeed(object? gameManager, out int seed, out string detail)
        {
            seed = 0;
            detail = "";
            if (gameManager == null)
            {
                detail = "GameManager instance is null.";
                return false;
            }

            try
            {
                ResolveCurrentSeedMembers(gameManager.GetType());

                object? value = null;
                if (_currentSeedProperty != null)
                {
                    value = _currentSeedProperty.GetValue(gameManager, null);
                    detail = $"{gameManager.GetType().FullName}.currentSeed";
                }
                else if (_currentSeedField != null)
                {
                    value = _currentSeedField.GetValue(gameManager);
                    detail = $"{gameManager.GetType().FullName}.{_currentSeedField.Name}";
                }

                if (value == null)
                {
                    detail = "GameManager currentSeed member was not found.";
                    return false;
                }

                seed = Convert.ToInt32(value);
                if (seed == 0)
                {
                    detail += " is 0; treating as not ready.";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                detail = $"Read currentSeed failed: {ex.GetType().Name}: {ex.Message}";
                seed = 0;
                return false;
            }
        }

        public static bool TryApplyForceLevelSeed(int seed, out string result)
        {
            result = "";
            try
            {
                var field = ResolveForceLevelSeedField();
                if (field == null)
                {
                    result = "GlobalSettings.ForceLevelSeed was not found.";
                    return false;
                }

                field.SetValue(null, seed);
                _forceSeedAppliedByUs = true;
                _forceSeedAppliedValue = seed;
                result = $"Set {field.DeclaringType?.FullName ?? "GlobalSettings"}.ForceLevelSeed={seed}";
                return true;
            }
            catch (Exception ex)
            {
                result = $"ForceLevelSeed apply failed: {ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// SEED-1: release the forced seed once the generation it was written for has consumed it (called at the
        /// generation-input finalize point, where GameManager.currentSeed is already fixed). Only clears the exact
        /// value this class wrote — if the field now holds something else, someone else owns it and we leave it.
        /// </summary>
        public static void ReleaseAppliedForceLevelSeed(string source)
        {
            if (!_forceSeedAppliedByUs) return;
            try
            {
                var field = ResolveForceLevelSeedField();
                if (field == null) { _forceSeedAppliedByUs = false; return; }

                int current = Convert.ToInt32(field.GetValue(null) ?? 0);
                if (current != _forceSeedAppliedValue)
                {
                    // Overwritten by the game / dev tools since we applied it — not ours to clear.
                    _forceSeedAppliedByUs = false;
                    return;
                }

                field.SetValue(null, 0);
                _forceSeedAppliedByUs = false;
                SULFURTogether.Plugin.Log.Info($"[LevelSeed] released ForceLevelSeed={_forceSeedAppliedValue} (consumed by generation) source={source}");
            }
            catch (Exception ex)
            {
                _forceSeedAppliedByUs = false;
                SULFURTogether.Plugin.Log.Warn($"[LevelSeed] release ForceLevelSeed failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void ResolveCurrentSeedMembers(Type gameManagerType)
        {
            if (_currentSeedResolved && _currentSeedOwnerType == gameManagerType) return;
            _currentSeedResolved = true;
            _currentSeedOwnerType = gameManagerType;

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            _currentSeedProperty = gameManagerType.GetProperty("currentSeed", flags);
            _currentSeedField = gameManagerType.GetField("<currentSeed>k__BackingField", flags)
                ?? gameManagerType.GetField("currentSeed", flags);
        }

        private static FieldInfo? ResolveForceLevelSeedField()
        {
            if (_forceLevelSeedResolved) return _forceLevelSeedField;
            _forceLevelSeedResolved = true;

            Type? globalSettingsType = Type.GetType("GlobalSettings, PerfectRandom.Sulfur.Core", false);
            if (globalSettingsType == null)
            {
                try { globalSettingsType = typeof(global::GlobalSettings); }
                catch { }
            }
            if (globalSettingsType == null) return null;

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            _forceLevelSeedField = globalSettingsType.GetField("ForceLevelSeed", flags);
            return _forceLevelSeedField;
        }
    }
}
