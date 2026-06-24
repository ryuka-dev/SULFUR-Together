using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace SULFURTogether.Networking
{
    /// <summary>
    /// Phase 5.3-I: reflective read/write of GameManager's deterministic generation-input sets.
    /// Field names confirmed from the DLL:
    ///   usedChunksThisRun, usedUniqueEventThisRun, usedUniqueEventThisEnvironment.
    /// All access is defensive (BindingFlags, try/catch) and never assumes a field is public.
    /// </summary>
    internal static class NetGameManagerUsedSets
    {
        public const string ChunksField   = "usedChunksThisRun";
        public const string EventsRunField = "usedUniqueEventThisRun";
        public const string EventsEnvField = "usedUniqueEventThisEnvironment";

        private const BindingFlags FieldFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;

        // The most recent local GameManager used-sets snapshot, captured at GoToLevel entry.
        // On the Host this is the pre-generation input for the level it is loading, which is exactly
        // what a drifted Client needs to reproduce the same candidate pools.
        public static NetHostUsedSets? LastLocalSnapshot { get; private set; }
        public static int LastSnapshotLevelIndex { get; private set; } = -1;
        public static string LastSnapshotChapter { get; private set; } = "<unknown>";

        private static bool _elementTypesDumped;

        // ---- read ----

        public static bool TryRead(object gameManager, out NetHostUsedSets sets, out string error)
        {
            sets = new NetHostUsedSets();
            error = "";
            if (gameManager == null) { error = "gameManager is null"; return false; }

            try
            {
                Type gmType = gameManager.GetType();
                sets.UsedChunksThisRun         = ReadKeys(gameManager, gmType, ChunksField);
                sets.UsedEventsThisRun         = ReadKeys(gameManager, gmType, EventsRunField);
                sets.UsedEventsThisEnvironment = ReadKeys(gameManager, gmType, EventsEnvField);
                sets.Captured = true;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>Capture local used sets at level entry and remember them for later sending/diagnostics.</summary>
        public static NetHostUsedSets? CaptureLocal(object gameManager, string chapter, int levelIndex)
        {
            if (!TryRead(gameManager, out var sets, out var error))
            {
                if (Plugin.Cfg.LogUsedSetsTrace.Value)
                    Plugin.Log.Warn($"[UsedSetsTrace] capture failed: {error}");
                return null;
            }

            LastLocalSnapshot = sets;
            LastSnapshotChapter = string.IsNullOrWhiteSpace(chapter) ? "<unknown>" : chapter;
            LastSnapshotLevelIndex = levelIndex;
            return sets;
        }

        private static List<string> ReadKeys(object owner, Type ownerType, string fieldName)
        {
            var result = new List<string>();
            var field = FindField(ownerType, fieldName);
            if (field == null) return result;

            object? value;
            try { value = field.GetValue(owner); } catch { return result; }
            if (value is IEnumerable en && !(value is string))
            {
                foreach (var item in en)
                    result.Add(KeyOf(item));
            }
            return result;
        }

        private static string KeyOf(object? item)
        {
            if (item == null) return "<null>";
            if (item is string s) return s;
            if (item is UnityEngine.Object uo) return uo != null ? uo.name : "<destroyed>";
            return item.ToString();
        }

        // ---- write ----

        public sealed class ApplyOutcome
        {
            public bool ChunksApplied;
            public bool EventsRunApplied;
            public bool EventsEnvApplied;
            public int ChunksBefore, ChunksAfter;
            public int EventsRunBefore, EventsRunAfter;
            public int EventsEnvBefore, EventsEnvAfter;
            public readonly List<string> Errors = new List<string>();

            public bool AnyApplied => ChunksApplied || EventsRunApplied || EventsEnvApplied;
            public bool AnyFailed  => Errors.Count > 0;
        }

        /// <summary>
        /// Overwrite the local GameManager used sets with the supplied host snapshot. Only collections
        /// whose element type is string are repopulated; for any other element type we leave the set
        /// untouched and record a precise error so the type can be handled in a later iteration.
        /// </summary>
        public static ApplyOutcome Apply(object gameManager, NetHostUsedSets hostSets)
        {
            var outcome = new ApplyOutcome();
            if (gameManager == null) { outcome.Errors.Add("gameManager is null"); return outcome; }
            if (hostSets == null) { outcome.Errors.Add("hostSets is null"); return outcome; }

            Type gmType = gameManager.GetType();

            if (!_elementTypesDumped)
            {
                _elementTypesDumped = true;
                DumpElementType(gameManager, gmType, ChunksField);
                DumpElementType(gameManager, gmType, EventsRunField);
                DumpElementType(gameManager, gmType, EventsEnvField);
            }

            ApplyOne(gameManager, gmType, ChunksField, hostSets.UsedChunksThisRun, outcome,
                v => { outcome.ChunksApplied = v; }, (b, a) => { outcome.ChunksBefore = b; outcome.ChunksAfter = a; });
            ApplyOne(gameManager, gmType, EventsRunField, hostSets.UsedEventsThisRun, outcome,
                v => { outcome.EventsRunApplied = v; }, (b, a) => { outcome.EventsRunBefore = b; outcome.EventsRunAfter = a; });
            ApplyOne(gameManager, gmType, EventsEnvField, hostSets.UsedEventsThisEnvironment, outcome,
                v => { outcome.EventsEnvApplied = v; }, (b, a) => { outcome.EventsEnvBefore = b; outcome.EventsEnvAfter = a; });

            return outcome;
        }

        private static void ApplyOne(object owner, Type ownerType, string fieldName, List<string> hostKeys,
            ApplyOutcome outcome, Action<bool> setApplied, Action<int, int> setCounts)
        {
            var field = FindField(ownerType, fieldName);
            if (field == null)
            {
                outcome.Errors.Add($"{fieldName}:field-not-found");
                setApplied(false);
                return;
            }

            object? collection;
            try { collection = field.GetValue(owner); }
            catch (Exception ex) { outcome.Errors.Add($"{fieldName}:get-failed:{ex.Message}"); setApplied(false); return; }

            if (collection == null)
            {
                outcome.Errors.Add($"{fieldName}:collection-null");
                setApplied(false);
                return;
            }

            int before = CountOf(collection);
            Type? elemType = ElementType(collection);

            if (elemType != typeof(string))
            {
                outcome.Errors.Add($"{fieldName}:non-string-element({elemType?.Name ?? "?"})");
                setApplied(false);
                setCounts(before, before);
                return;
            }

            var clear = collection.GetType().GetMethod("Clear", Type.EmptyTypes);
            var add   = collection.GetType().GetMethod("Add", new[] { typeof(string) });
            if (clear == null || add == null)
            {
                outcome.Errors.Add($"{fieldName}:no-clear-or-add");
                setApplied(false);
                setCounts(before, before);
                return;
            }

            try
            {
                clear.Invoke(collection, null);
                if (hostKeys != null)
                    foreach (var key in hostKeys)
                        add.Invoke(collection, new object[] { key ?? "" });

                int after = CountOf(collection);
                setApplied(true);
                setCounts(before, after);
            }
            catch (Exception ex)
            {
                outcome.Errors.Add($"{fieldName}:write-failed:{ex.Message}");
                setApplied(false);
                setCounts(before, CountOf(collection));
            }
        }

        private static void DumpElementType(object owner, Type ownerType, string fieldName)
        {
            try
            {
                var field = FindField(ownerType, fieldName);
                if (field == null) { Plugin.Log.Info($"[UsedSetsTrace] field {fieldName} NOT FOUND on {ownerType.Name}"); return; }
                object? value = field.GetValue(owner);
                string declared = field.FieldType.Name;
                string elem = value != null ? (ElementType(value)?.Name ?? "?") : "?";
                Plugin.Log.Info($"[UsedSetsTrace] field {fieldName} declared={declared} runtime={value?.GetType().Name ?? "<null>"} element={elem}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[UsedSetsTrace] DumpElementType {fieldName} failed: {ex.Message}"); }
        }

        // ---- helpers ----

        private static FieldInfo? FindField(Type type, string name)
        {
            for (Type? t = type; t != null; t = t.BaseType)
            {
                var f = t.GetField(name, FieldFlags);
                if (f != null) return f;
            }
            return null;
        }

        private static int CountOf(object collection)
        {
            if (collection is ICollection col) return col.Count;
            int n = 0;
            if (collection is IEnumerable en) foreach (var _ in en) n++;
            return n;
        }

        private static Type? ElementType(object collection)
        {
            var t = collection.GetType();
            if (t.IsGenericType)
            {
                var args = t.GetGenericArguments();
                if (args.Length == 1) return args[0];
            }
            foreach (var i in t.GetInterfaces())
            {
                if (i.IsGenericType)
                {
                    var def = i.GetGenericTypeDefinition();
                    if (def == typeof(ICollection<>) || def == typeof(IEnumerable<>))
                        return i.GetGenericArguments()[0];
                }
            }
            return null;
        }
    }
}
