using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SULFURTogether.Networking
{
    /// <summary>
    /// Phase 2.6 manual client scene follow helper.
    /// This runs only when the user presses the configured key. It is never automatic.
    /// It uses reflection so this phase stays tolerant to game updates and fails safely.
    /// </summary>
    internal static class NetManualSceneFollower
    {
        public static bool TryFollow(NetHostSceneRequest request, out string result)
        {
            result = "";

            if (request == null || !request.HasTargetScene)
            {
                result = "No valid HostSceneRequest target is available.";
                return false;
            }

            Type? gameManagerType = FindType(
                "PerfectRandom.Sulfur.Core.GameManager, PerfectRandom.Sulfur.Core",
                "PerfectRandom.Sulfur.GameManager, PerfectRandom.Sulfur.Core",
                "GameManager, Assembly-CSharp");
            if (gameManagerType == null)
            {
                result = "GameManager type was not found.";
                return false;
            }

            object? gameManager = FindGameManager(gameManagerType);
            if (gameManager == null)
            {
                result = "GameManager instance was not found.";
                return false;
            }

            MethodInfo? goToLevel = FindGoToLevelMethod(gameManagerType);
            if (goToLevel == null)
            {
                result = "GameManager.GoToLevel(WorldEnvironment,int,LoadingMode,string) was not found.";
                return false;
            }

            var parameters = goToLevel.GetParameters();
            if (parameters.Length < 4)
            {
                result = "GameManager.GoToLevel signature is shorter than expected.";
                return false;
            }

            object? worldEnvironment = FindWorldEnvironment(parameters[0].ParameterType, request.ChapterName);
            if (worldEnvironment == null)
            {
                result = $"WorldEnvironment '{request.ChapterName}' was not found in loaded resources.";
                return false;
            }

            string seedApplyDetail = "";
            if (SULFURTogether.Plugin.Cfg.ApplyHostLevelSeedOnManualFollow.Value && request.HasLevelSeed)
            {
                if (NetLevelSeed.TryApplyForceLevelSeed(request.LevelSeed, out var seedResult))
                    seedApplyDetail = " seed=" + request.LevelSeed + " (" + seedResult + ")";
                else
                    seedApplyDetail = " seed=" + request.LevelSeed + " (not applied: " + seedResult + ")";
            }

            // Phase 5.3-I: overwrite local GameManager used sets with the Host's BEFORE GoToLevel, so the
            // generator excludes the same chunks/events the Host excluded. The Client may have generated a
            // wrong-seed local level first, contaminating these sets; this removes that contamination.
            string usedSetsDetail = ApplyHostUsedSets(gameManager, request);

            object? loadingMode = BuildLoadingMode(parameters[2].ParameterType, request.LoadingMode);
            if (loadingMode == null && parameters[2].ParameterType.IsValueType)
            {
                result = $"Could not build LoadingMode '{request.LoadingMode}' for {parameters[2].ParameterType.Name}.";
                return false;
            }

            try
            {
                object?[] args =
                {
                    worldEnvironment,
                    request.LevelIndex,
                    loadingMode,
                    request.SpawnIdentifier ?? "",
                };

                // Reentry guard: the Client load gate intercepts GameManager.GoToLevel; this is OUR
                // host-driven (or manual) load, so suppress interception for the duration of the invoke.
                NetClientLoadGate.BeginHostDrivenLoad();
                try
                {
                    goToLevel.Invoke(goToLevel.IsStatic ? null : gameManager, args);
                }
                finally
                {
                    NetClientLoadGate.EndHostDrivenLoad();
                }
                result = $"Manual GoToLevel invoked for {request.TargetSceneKey()} mode='{request.LoadingMode}' spawn='{request.SpawnIdentifier}'{seedApplyDetail}{usedSetsDetail}.";
                return true;
            }
            catch (TargetInvocationException ex)
            {
                result = $"GoToLevel threw {ex.InnerException?.GetType().Name ?? ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}";
                return false;
            }
            catch (Exception ex)
            {
                result = $"GoToLevel invoke failed: {ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        // Phase 5.3-I: apply the Host's deterministic generation-input used sets onto the local
        // GameManager before GoToLevel. Logs [FollowPrep] before/after and updates diagnostics counters.
        private static string ApplyHostUsedSets(object gameManager, NetHostSceneRequest request)
        {
            try
            {
                if (!SULFURTogether.Plugin.Cfg.SyncHostUsedSetsOnManualFollow.Value)
                    return " usedSets=disabled";

                bool log = SULFURTogether.Plugin.Cfg.LogUsedSetsTrace.Value;

                if (log)
                    SULFURTogether.Plugin.Log.Info($"[FollowPrep] host seed={(request.HasLevelSeed ? request.LevelSeed.ToString() : "?")} level={request.LevelIndex} env={request.ChapterName} hasUsedSets={request.HasUsedSets}");

                // Read local-before for logging regardless of whether the host sent sets.
                if (NetGameManagerUsedSets.TryRead(gameManager, out var localBefore, out var readErr))
                {
                    if (log)
                    {
                        SULFURTogether.Plugin.Log.Info($"[FollowPrep] local before usedChunks={NetHostUsedSets.Summary(localBefore.UsedChunksThisRun)}");
                        SULFURTogether.Plugin.Log.Info($"[FollowPrep] local before usedEventsRun={NetHostUsedSets.Summary(localBefore.UsedEventsThisRun)}");
                        SULFURTogether.Plugin.Log.Info($"[FollowPrep] local before usedEventsEnv={NetHostUsedSets.Summary(localBefore.UsedEventsThisEnvironment)}");
                    }
                }
                else if (log)
                {
                    SULFURTogether.Plugin.Log.Warn($"[FollowPrep] local before read failed: {readErr}");
                }

                if (!request.HasUsedSets)
                {
                    if (log) SULFURTogether.Plugin.Log.Info("[FollowPrep] host sent no used sets; local sets left unchanged.");
                    return " usedSets=hostNone";
                }

                var hostSets = request.ToUsedSets();
                var outcome = NetGameManagerUsedSets.Apply(gameManager, hostSets);

                NetSceneFollowDiag.ClientFollowUsedChunksBeforeCount    = outcome.ChunksBefore;
                NetSceneFollowDiag.ClientFollowUsedChunksAfterCount     = outcome.ChunksAfter;
                NetSceneFollowDiag.ClientFollowUsedEventsRunBeforeCount = outcome.EventsRunBefore;
                NetSceneFollowDiag.ClientFollowUsedEventsRunAfterCount  = outcome.EventsRunAfter;
                NetSceneFollowDiag.ClientFollowUsedEventsEnvBeforeCount = outcome.EventsEnvBefore;
                NetSceneFollowDiag.ClientFollowUsedEventsEnvAfterCount  = outcome.EventsEnvAfter;

                if (outcome.AnyApplied) NetSceneFollowDiag.IncApplied();
                if (outcome.AnyFailed)  NetSceneFollowDiag.IncApplyFailed();

                if (log)
                {
                    SULFURTogether.Plugin.Log.Info($"[FollowPrep] applied host usedChunks={NetHostUsedSets.Summary(hostSets.UsedChunksThisRun)} (count {outcome.ChunksBefore}->{outcome.ChunksAfter} applied={outcome.ChunksApplied})");
                    SULFURTogether.Plugin.Log.Info($"[FollowPrep] applied host usedEventsRun={NetHostUsedSets.Summary(hostSets.UsedEventsThisRun)} (count {outcome.EventsRunBefore}->{outcome.EventsRunAfter} applied={outcome.EventsRunApplied})");
                    SULFURTogether.Plugin.Log.Info($"[FollowPrep] applied host usedEventsEnv={NetHostUsedSets.Summary(hostSets.UsedEventsThisEnvironment)} (count {outcome.EventsEnvBefore}->{outcome.EventsEnvAfter} applied={outcome.EventsEnvApplied})");
                    if (outcome.AnyFailed)
                        SULFURTogether.Plugin.Log.Warn($"[FollowPrep] apply errors: {string.Join("; ", outcome.Errors)}");
                    SULFURTogether.Plugin.Log.Info($"[FollowPrep] counters {NetSceneFollowDiag.Format()}");
                }

                return $" usedSets=chunks{outcome.ChunksBefore}->{outcome.ChunksAfter},eventsRun{outcome.EventsRunBefore}->{outcome.EventsRunAfter},eventsEnv{outcome.EventsEnvBefore}->{outcome.EventsEnvAfter}{(outcome.AnyFailed ? "(partial)" : "")}";
            }
            catch (Exception ex)
            {
                NetSceneFollowDiag.IncApplyFailed();
                return " usedSets=error:" + ex.Message;
            }
        }

        private static Type? FindType(params string[] names)
        {
            foreach (var name in names)
            {
                var type = Type.GetType(name, false);
                if (type != null) return type;
            }

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var name in names)
                {
                    var simple = name.Split(',')[0].Trim();
                    var type = asm.GetType(simple, false);
                    if (type != null) return type;
                }
            }

            return null;
        }

        private static object? FindGameManager(Type gameManagerType)
        {
            object? fromSingleton = TryGetStaticMember(gameManagerType, "Instance")
                ?? TryGetStaticMember(gameManagerType, "instance")
                ?? TryGetStaticMember(gameManagerType, "Current")
                ?? TryGetStaticMember(gameManagerType, "current");
            if (fromSingleton != null) return fromSingleton;

            var objects = Resources.FindObjectsOfTypeAll(gameManagerType);
            return objects != null && objects.Length > 0 ? objects[0] : null;
        }

        private static object? TryGetStaticMember(Type type, string name)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            try
            {
                var prop = type.GetProperty(name, flags);
                if (prop != null && prop.GetIndexParameters().Length == 0)
                    return prop.GetValue(null, null);
            }
            catch { }

            try
            {
                var field = type.GetField(name, flags);
                if (field != null)
                    return field.GetValue(null);
            }
            catch { }

            return null;
        }

        private static MethodInfo? FindGoToLevelMethod(Type gameManagerType)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            foreach (var method in gameManagerType.GetMethods(flags))
            {
                if (method.Name != "GoToLevel") continue;
                var p = method.GetParameters();
                if (p.Length != 4) continue;
                if (p[1].ParameterType != typeof(int)) continue;
                if (p[3].ParameterType != typeof(string)) continue;
                return method;
            }
            return null;
        }

        private static object? FindWorldEnvironment(Type expectedType, string chapterName)
        {
            string wanted = NetSceneName.Clean(chapterName);
            if (string.IsNullOrWhiteSpace(wanted) || wanted == "<unknown>") return null;

            // Current SULFUR builds use WorldEnvironmentIds as the first GoToLevel parameter.
            // This is not a UnityEngine.Object, so Resources.FindObjectsOfTypeAll must not be called on it.
            object? enumValue = TryBuildWorldEnvironmentId(expectedType, wanted);
            if (enumValue != null) return enumValue;

            if (typeof(UnityEngine.Object).IsAssignableFrom(expectedType))
            {
                object? direct = FindLoadedObjectByName(expectedType, wanted);
                if (direct != null) return direct;
            }

            Type? worldEnvType = expectedType;
            if (worldEnvType == typeof(object) || !typeof(UnityEngine.Object).IsAssignableFrom(worldEnvType))
            {
                worldEnvType = FindType(
                    "PerfectRandom.Sulfur.Core.LevelGeneration.WorldEnvironment, PerfectRandom.Sulfur.Core",
                    "PerfectRandom.Sulfur.Core.WorldEnvironment, PerfectRandom.Sulfur.Core");
            }

            if (worldEnvType != null && typeof(UnityEngine.Object).IsAssignableFrom(worldEnvType))
            {
                object? direct = FindLoadedObjectByName(worldEnvType, wanted);
                if (direct != null && expectedType.IsInstanceOfType(direct)) return direct;
            }

            object? fromLists = FindWorldEnvironmentFromLists(expectedType, wanted);
            if (fromLists != null) return fromLists;

            return null;
        }

        private static object? TryBuildWorldEnvironmentId(Type expectedType, string chapterName)
        {
            if (!expectedType.IsEnum) return null;

            foreach (var candidate in NetSceneName.LookupCandidates(chapterName))
            {
                try
                {
                    return Enum.Parse(expectedType, candidate, ignoreCase: true);
                }
                catch { }
            }

            var canonical = NetSceneName.Canonicalize(chapterName);
            foreach (var enumName in Enum.GetNames(expectedType))
            {
                if (string.Equals(enumName, canonical, StringComparison.OrdinalIgnoreCase))
                {
                    try { return Enum.Parse(expectedType, enumName); } catch { }
                }
            }

            return null;
        }

        private static object? FindLoadedObjectByName(Type type, string wanted)
        {
            if (!typeof(UnityEngine.Object).IsAssignableFrom(type)) return null;

            try
            {
                var objects = Resources.FindObjectsOfTypeAll(type);
                foreach (var obj in objects)
                {
                    if (MatchesObjectName(obj, wanted)) return obj;
                }
            }
            catch { }
            return null;
        }

        private static object? FindWorldEnvironmentFromLists(Type expectedType, string wanted)
        {
            Type? listType = FindType(
                "PerfectRandom.Sulfur.Core.WorldEnvironmentList, PerfectRandom.Sulfur.Core",
                "PerfectRandom.Sulfur.Core.LevelGeneration.WorldEnvironmentList, PerfectRandom.Sulfur.Core");
            if (listType == null) return null;

            try
            {
                var lists = Resources.FindObjectsOfTypeAll(listType);
                foreach (var list in lists)
                {
                    var found = ScanObjectGraphForWorldEnvironment(list, expectedType, wanted, 0);
                    if (found != null) return found;
                }
            }
            catch { }

            return null;
        }

        private static object? ScanObjectGraphForWorldEnvironment(object? source, Type expectedType, string wanted, int depth)
        {
            if (source == null || depth > 2) return null;
            if (expectedType.IsInstanceOfType(source) && MatchesObjectName(source, wanted)) return source;

            var type = source.GetType();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            foreach (var field in type.GetFields(flags))
            {
                object? value;
                try { value = field.GetValue(source); } catch { continue; }
                var found = ScanValueForWorldEnvironment(value, expectedType, wanted, depth + 1);
                if (found != null) return found;
            }

            foreach (var prop in type.GetProperties(flags))
            {
                if (prop.GetIndexParameters().Length != 0) continue;
                object? value;
                try { value = prop.GetValue(source, null); } catch { continue; }
                var found = ScanValueForWorldEnvironment(value, expectedType, wanted, depth + 1);
                if (found != null) return found;
            }

            return null;
        }

        private static object? ScanValueForWorldEnvironment(object? value, Type expectedType, string wanted, int depth)
        {
            if (value == null || depth > 2) return null;
            if (expectedType.IsInstanceOfType(value) && MatchesObjectName(value, wanted)) return value;

            if (value is string) return null;
            if (value is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item == null) continue;
                    if (expectedType.IsInstanceOfType(item) && MatchesObjectName(item, wanted)) return item;
                    var found = ScanObjectGraphForWorldEnvironment(item, expectedType, wanted, depth + 1);
                    if (found != null) return found;
                }
            }

            return null;
        }

        private static bool MatchesObjectName(object obj, string wanted)
        {
            if (obj == null) return false;
            var candidates = new HashSet<string>(NetSceneName.LookupCandidates(wanted), StringComparer.OrdinalIgnoreCase);
            if (candidates.Count == 0) return false;

            if (candidates.Contains(NetSceneName.Clean(obj.ToString()))) return true;

            if (obj is UnityEngine.Object uo && candidates.Contains(NetSceneName.Clean(uo.name))) return true;

            string[] names = { "Name", "name", "Id", "ID", "id", "Identifier", "identifier", "EnvironmentName", "environmentName" };
            var type = obj.GetType();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            foreach (var name in names)
            {
                try
                {
                    var prop = type.GetProperty(name, flags);
                    if (prop != null && prop.GetIndexParameters().Length == 0 && candidates.Contains(NetSceneName.Clean(prop.GetValue(obj, null)?.ToString())))
                        return true;
                }
                catch { }

                try
                {
                    var field = type.GetField(name, flags);
                    if (field != null && candidates.Contains(NetSceneName.Clean(field.GetValue(obj)?.ToString())))
                        return true;
                }
                catch { }
            }

            return false;
        }

        private static object? BuildLoadingMode(Type type, string value)
        {
            if (type == typeof(string)) return value ?? "";
            if (type.IsEnum)
            {
                string clean = Clean(value);
                if (!string.IsNullOrWhiteSpace(clean) && clean != "<unknown>")
                {
                    try { return Enum.Parse(type, clean, ignoreCase: true); } catch { }
                }

                foreach (var name in Enum.GetNames(type))
                {
                    if (string.Equals(name, "Normal", StringComparison.OrdinalIgnoreCase))
                        return Enum.Parse(type, name);
                }

                var values = Enum.GetValues(type);
                return values.Length > 0 ? values.GetValue(0) : null;
            }

            if (type == typeof(object)) return value ?? "";
            return null;
        }

        private static string Clean(string? value)
        {
            return NetSceneName.Clean(value);
        }
    }
}
