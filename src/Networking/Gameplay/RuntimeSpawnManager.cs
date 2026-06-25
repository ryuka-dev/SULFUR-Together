using System;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;
using SULFURTogether.Networking.Gameplay.Boss;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase 5.5-RT1: host-authoritative runtime (post-level-load) unit spawn sync.
    ///
    /// Stage 1 (this): HOST one-sided runtime spawns (F3 DevTools) → broadcast → CLIENT mirror-spawns + binds into the
    /// existing EnemyPuppet pipeline. The cross-end key is UnitSO.id.value (ushort, same UnitIds registry); the client
    /// resolves it via AsyncAssetLoading.unitDatabase[new UnitId(value)] → UnitSO and spawns the same unit at the host
    /// position, then binds host SpawnIndex ↔ local key so host state/attack/death drive it.
    ///
    /// Later stages: boss adds (spawned on both ends → need client-side suppression so they don't double) and client F3
    /// spawns (route a request to the host). All reflection is defensive (never throws into game code).
    /// </summary>
    internal static class RuntimeSpawnManager
    {
        private static bool Enabled { get { try { return Plugin.Cfg.EnableRuntimeSpawnSync.Value; } catch { return false; } } }
        private static bool LogOn  { get { try { return Plugin.Cfg.LogRuntimeSpawnSync.Value; } catch { return false; } } }

        public static int RuntimeSpawnBroadcast;
        public static int RuntimeSpawnMirrored;
        public static int RuntimeSpawnMirrorFailed;

        // Phase 5.7-DS: death-spawn (MutationDefinition.OnDeathSpawnUnitsFunc) sync counters.
        public static int DeathSpawnHostBroadcast;
        public static int DeathSpawnClientSuppressed;

        // ---- pending owner correlation across the async SpawnUnitAsync -> static SpawnUnit boundary (by UnitSO ref) ----
        private sealed class Pending { public object UnitSO = null!; public string Source = ""; public float At; }
        private static readonly System.Collections.Generic.List<Pending> _pending = new System.Collections.Generic.List<Pending>();
        private const float PendingTtl = 8f;

        // ---- cached reflection ----
        private static bool _resolved;
        private static Type? _unitIdType;
        private static MethodInfo? _spawnUnitAsync; // UnitSO.SpawnUnitAsync(MonoBehaviour, Vector3, Quaternion)
        private static PropertyInfo? _unitDbProp;   // AsyncAssetLoading.unitDatabase
        private static MethodInfo? _unitDbIndexer;  // UnitDatabase get_Item(UnitId)
        private static object? _asyncAssetLoadingInstance;

        public static void Reset()
        {
            lock (_pending) _pending.Clear();
            _hostDeathSpawnDepth = 0;
        }

        // ================================================================== Phase 5.7-DS: death-spawn sync
        //
        // The "spawn a random enemy on death" mutation (MutationDefinition.unitsToSpawnOnDeath) picks WHICH unit to spawn
        // with the global UnityEngine.Random (in AddSpawnUnit), so host and client independently bake DIFFERENT units into
        // the dying enemy's onDeath delegate — on death each side spawns a different enemy (LogOutput117: host ShavwaLurk,
        // client something else). Fix = host-authoritative: the client suppresses its local death-spawn and mirrors the
        // host's via the existing runtime-spawn pipeline.
        //
        // The actual spawn in OnDeathSpawnUnitsFunc goes through the STATIC UnitSO.SpawnUnit (no owner arg, so the
        // SpawnUnitAsync NotePendingSpawn path never sees it). We instead bracket OnDeathSpawnUnitsFunc with a host-only
        // flag; the SpawnUnit postfix (OnUnitSpawned) broadcasts whatever was spawned while the flag is set. The
        // non-endless path of OnDeathSpawnUnitsFunc has no await before SpawnUnit, so it runs fully synchronously and the
        // prefix/postfix bracket the spawn correctly.
        private static bool DeathSpawnEnabled { get { try { return Plugin.Cfg.EnableDeathSpawnSync.Value; } catch { return false; } } }
        [ThreadStatic] private static int _hostDeathSpawnDepth;

        /// <summary>HOST: a death-spawn (OnDeathSpawnUnitsFunc) is about to run; flag it so the spawn is broadcast.</summary>
        public static void BeginHostDeathSpawn()
        {
            if (NetGameplaySyncBridge.BossMode == NetMode.Host) _hostDeathSpawnDepth++;
        }

        public static void EndHostDeathSpawn()
        {
            if (_hostDeathSpawnDepth > 0) _hostDeathSpawnDepth--;
        }

        /// <summary>CLIENT: should the local death-spawn be suppressed (host is authoritative and will mirror its own)?</summary>
        public static bool ClientShouldSuppressDeathSpawn()
        {
            if (!Enabled || !DeathSpawnEnabled) return false;
            if (NetGameplaySyncBridge.BossMode != NetMode.Client) return false;
            DeathSpawnClientSuppressed++;
            if (LogOn) Plugin.Log.Info($"[RuntimeSpawn] client suppressed local death-spawn (#{DeathSpawnClientSuppressed}) — mirroring host's instead");
            return true;
        }

        // ================================================================== capture (from BossSpawnPatches hooks)

        /// <summary>Prefix on UnitSO.SpawnUnitAsync: note a runtime-spawn owner we care about (Stage 1: DevToolsManager,
        /// i.e. the F3 debug spawn). Only used on the HOST.</summary>
        public static void NotePendingSpawn(object unitSO, object owner)
        {
            try
            {
                if (!Enabled || unitSO == null || owner == null) return;
                if (NetGameplaySyncBridge.BossMode != NetMode.Host) return;
                string? src = ClassifyOwner(owner);
                if (src == null) return; // not a source we sync in this stage
                lock (_pending)
                {
                    float now = Time.realtimeSinceStartup;
                    _pending.RemoveAll(p => now - p.At > PendingTtl);
                    _pending.Add(new Pending { UnitSO = unitSO, Source = src, At = now });
                }
            }
            catch { }
        }

        /// <summary>Postfix on static UnitSO.SpawnUnit: if the spawned unit matches a noted runtime-spawn owner, broadcast
        /// it (HOST). Returns without effect for non-tracked spawns.</summary>
        public static void OnUnitSpawned(object unitSO, object spawnedUnit, Vector3 position)
        {
            try
            {
                if (!Enabled || unitSO == null || spawnedUnit == null) return;
                if (NetGameplaySyncBridge.BossMode != NetMode.Host) return;
                string? src = null;
                lock (_pending)
                {
                    for (int i = 0; i < _pending.Count; i++)
                        if (ReferenceEquals(_pending[i].UnitSO, unitSO)) { src = _pending[i].Source; _pending.RemoveAt(i); break; }
                }
                // Phase 5.7-DS: death-spawn (static UnitSO.SpawnUnit inside OnDeathSpawnUnitsFunc) has no pending owner; the
                // host-only bracket flag tells us this spawn must be broadcast so the client mirrors it instead of rolling
                // its own divergent unit.
                if (src == null && _hostDeathSpawnDepth > 0 && DeathSpawnEnabled) { src = "DeathSpawn"; DeathSpawnHostBroadcast++; }
                if (src == null) return; // not a tracked runtime spawn
                BroadcastHostRuntimeSpawn(spawnedUnit, unitSO, position, src);
            }
            catch (Exception ex) { Plugin.Log.Warn($"[RuntimeSpawn] OnUnitSpawned failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>Stage 1 owner filter: only the F3 DevTools spawn is one-sided/safe to mirror without suppression.</summary>
        private static string? ClassifyOwner(object owner)
        {
            string tn = owner.GetType().Name;
            if (tn == "DevToolsManager") return "DevTools";
            return null;
        }

        // ================================================================== host broadcast

        private static void BroadcastHostRuntimeSpawn(object spawnedUnit, object unitSO, Vector3 position, string source)
        {
            int unitIdValue = ReadUnitIdValue(unitSO);
            if (unitIdValue == 0) { if (LogOn) Plugin.Log.Info($"[RuntimeSpawn] host skip (no unitId) src={source}"); return; }
            int hostIdx = NetGameplayProbeManager.GetSpawnIndexForObject(spawnedUnit);
            if (hostIdx <= 0) { Plugin.Log.Warn($"[RuntimeSpawn] host skip src={source} unitId={unitIdValue}: spawnIndex not ready"); return; }
            if (!NetBossEncounterManager.TryGetRunContext(out string chap, out int lvl, out bool hasSeed, out int seed)) { chap = ""; lvl = -1; }
            float rotY = 0f;
            try { if (spawnedUnit is Component c && c != null) rotY = c.transform.eulerAngles.y; } catch { }

            var msg = new NetRuntimeSpawn
            {
                UnitIdValue = unitIdValue, Position = position, RotationY = rotY, HostSpawnIndex = hostIdx,
                ChapterName = chap, LevelIndex = lvl, HasSeed = hasSeed, Seed = seed, Source = source,
            };
            RuntimeSpawnBroadcast++;
            Plugin.Log.Info($"[RuntimeSpawn] host broadcasting {msg.ToCompact()}");
            NetGameplaySyncBridge.BroadcastHostRuntimeSpawn(msg);
        }

        // ================================================================== client mirror

        public static void HandleHostRuntimeSpawn(NetRuntimeSpawn msg)
        {
            try
            {
                if (!Enabled || msg == null || NetGameplaySyncBridge.BossMode != NetMode.Client) return;
                // run validation (don't spawn into the wrong level)
                if (NetBossEncounterManager.TryGetRunContext(out string chap, out int lvl, out _, out _)
                    && (!string.Equals(chap, msg.ChapterName, StringComparison.Ordinal) || lvl != msg.LevelIndex))
                { if (LogOn) Plugin.Log.Info($"[RuntimeSpawn] client drop (run mismatch) {msg.ToCompact()} local={chap}:{lvl}"); return; }

                EnsureResolved();
                object? unitSO = ResolveUnitSO(msg.UnitIdValue);
                if (unitSO == null) { RuntimeSpawnMirrorFailed++; Plugin.Log.Warn($"[RuntimeSpawn] client cannot resolve UnitSO for unitId={msg.UnitIdValue}"); return; }
                Plugin.Log.Info($"[RuntimeSpawn] client mirroring {msg.ToCompact()}");
                MirrorSpawnAsync(unitSO, msg.Position, msg.RotationY, msg.HostSpawnIndex);
            }
            catch (Exception ex) { Plugin.Log.Warn($"[RuntimeSpawn] HandleHostRuntimeSpawn failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>Phase 5.5-RT3: mirror a HOST-only boss add (the client never spawned it locally). Resolves the
        /// UnitSO from the broadcast id and spawns + binds it like RT1. Called from the boss dynamic-spawn manifest.</summary>
        public static void MirrorBossAdd(int unitIdValue, Vector3 position, int hostSpawnIndex)
        {
            try
            {
                if (!Enabled || NetGameplaySyncBridge.BossMode != NetMode.Client) return;
                EnsureResolved();
                object? unitSO = ResolveUnitSO(unitIdValue);
                if (unitSO == null) { RuntimeSpawnMirrorFailed++; Plugin.Log.Warn($"[RuntimeSpawn] boss-add mirror cannot resolve unitId={unitIdValue}"); return; }
                MirrorSpawnAsync(unitSO, position, 0f, hostSpawnIndex);
            }
            catch (Exception ex) { Plugin.Log.Warn($"[RuntimeSpawn] MirrorBossAdd failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static async void MirrorSpawnAsync(object unitSO, Vector3 position, float rotY, int hostSpawnIndex)
        {
            try
            {
                if (_spawnUnitAsync == null) { RuntimeSpawnMirrorFailed++; return; }
                object? mono = ResolveGameManager();
                if (mono == null) { RuntimeSpawnMirrorFailed++; Plugin.Log.Warn("[RuntimeSpawn] client mirror: no GameManager"); return; }
                var rot = Quaternion.Euler(0f, rotY, 0f);
                var taskObj = _spawnUnitAsync.Invoke(unitSO, new object[] { mono, position, rot });
                object? unit = null;
                if (taskObj is Task task)
                {
                    await task;
                    unit = task.GetType().GetProperty("Result")?.GetValue(task);
                }
                if (unit == null) { RuntimeSpawnMirrorFailed++; Plugin.Log.Warn($"[RuntimeSpawn] client mirror produced null unit hostIdx={hostSpawnIndex}"); return; }
                bool bound = NetGameplayProbeManager.RegisterMirroredRuntimeSpawn(unit, hostSpawnIndex);
                if (bound) RuntimeSpawnMirrored++; else RuntimeSpawnMirrorFailed++;
                Plugin.Log.Info($"[RuntimeSpawn] client mirrored unit hostIdx={hostSpawnIndex} bound={bound} unit={BossReflect.RootName(unit)}");
            }
            catch (Exception ex) { RuntimeSpawnMirrorFailed++; Plugin.Log.Warn($"[RuntimeSpawn] MirrorSpawnAsync failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        // ================================================================== reflection

        private static int ReadUnitIdValue(object unitSO)
        {
            try
            {
                var idObj = BossReflect.GetMember(unitSO, "id");           // UnitId struct
                if (idObj == null) return 0;
                var v = BossReflect.GetMember(idObj, "value");             // ushort
                if (v == null) return 0;
                return Convert.ToInt32(v);
            }
            catch { return 0; }
        }

        private static void EnsureResolved()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                var unitSoType = BossReflect.FindType("UnitSO", "PerfectRandom.Sulfur.Core.Units.UnitSO");
                _unitIdType = BossReflect.FindType("UnitId", "PerfectRandom.Sulfur.Core.UnitId");
                if (unitSoType != null)
                {
                    foreach (var mi in unitSoType.GetMethods(BindingFlags.Instance | BindingFlags.Public))
                    {
                        if (mi.Name != "SpawnUnitAsync") continue;
                        var ps = mi.GetParameters();
                        if (ps.Length == 3 && ps[0].ParameterType.Name == "MonoBehaviour" && ps[1].ParameterType == typeof(Vector3))
                        { _spawnUnitAsync = mi; break; }
                    }
                }
                var aalType = BossReflect.FindType("AsyncAssetLoading", "PerfectRandom.Sulfur.Core.AsyncAssetLoading");
                if (aalType != null)
                {
                    _asyncAssetLoadingInstance = ResolveStaticInstance(aalType);
                    _unitDbProp = aalType.GetProperty("unitDatabase", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
                var dbType = BossReflect.FindType("UnitDatabase", "PerfectRandom.Sulfur.Core.UnitDatabase");
                if (dbType != null && _unitIdType != null)
                    _unitDbIndexer = dbType.GetMethod("get_Item", BindingFlags.Instance | BindingFlags.Public, null, new[] { _unitIdType }, null);
                Plugin.Log.Info($"[RuntimeSpawn] resolved spawnAsync={_spawnUnitAsync != null} unitId={_unitIdType != null} db={_unitDbIndexer != null}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[RuntimeSpawn] EnsureResolved failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static object? ResolveUnitSO(int unitIdValue)
        {
            try
            {
                if (_unitIdType == null || _unitDbIndexer == null) return null;
                object? db = _unitDbProp != null && _asyncAssetLoadingInstance != null ? _unitDbProp.GetValue(_asyncAssetLoadingInstance) : null;
                if (db == null) return null;
                object unitId = Activator.CreateInstance(_unitIdType, (ushort)unitIdValue);
                return _unitDbIndexer.Invoke(db, new[] { unitId });
            }
            catch (Exception ex) { Plugin.Log.Warn($"[RuntimeSpawn] ResolveUnitSO failed: {ex.GetType().Name}: {ex.Message}"); return null; }
        }

        private static object? _gmCached;
        private static object? ResolveGameManager()
        {
            try
            {
                if (_gmCached is UnityEngine.Object uo && uo != null) return _gmCached;
                var gmType = BossReflect.FindType("GameManager", "PerfectRandom.Sulfur.Core.GameManager", "PerfectRandom.Sulfur.Gameplay.GameManager");
                _gmCached = gmType == null ? null : ResolveStaticInstance(gmType);
                return _gmCached;
            }
            catch { return null; }
        }

        /// <summary>Resolve StaticInstance&lt;T&gt;.Instance (the game's singletons inherit StaticInstance).</summary>
        private static object? ResolveStaticInstance(Type t)
        {
            try
            {
                var p = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                return p?.GetValue(null);
            }
            catch { return null; }
        }
    }
}
