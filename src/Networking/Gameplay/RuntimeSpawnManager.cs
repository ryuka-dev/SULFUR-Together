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
            lock (_pending) { _pending.Clear(); _minionCtx.Clear(); }
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

        // ----- Phase 5.7-DS2: SpawnMinions (spawnMinionsOnDeath) — N minions of the PARENT's type, spawned async via
        // SpawnUnitAsync(GameManager,…) so the synchronous death-spawn flag bracket can't hold across the awaits. Instead
        // the host registers a short-lived "minion context" keyed by the parent UnitSO + remaining count; NotePendingSpawn
        // (already on the SpawnUnitAsync prefix, async-safe) tags matching spawns as DeathMinion so they broadcast. The
        // client suppresses its whole SpawnMinions (loot/gibs are host-authoritative anyway) and mirrors the host's. -----
        private static bool MinionSpawnEnabled { get { try { return Plugin.Cfg.EnableMinionSpawnSync.Value; } catch { return false; } } }
        public static int MinionHostBroadcast;
        public static int MinionClientSuppressed;

        private sealed class MinionCtx { public object UnitSO = null!; public int Remaining; public float Deadline; }
        private static readonly System.Collections.Generic.List<MinionCtx> _minionCtx = new System.Collections.Generic.List<MinionCtx>();

        /// <summary>HOST: a SpawnMinions of `count` minions of `parentUnitSO` is about to run; tag its async spawns.</summary>
        public static void BeginHostMinionContext(object parentUnitSO, int count)
        {
            if (NetGameplaySyncBridge.BossMode != NetMode.Host || parentUnitSO == null || count <= 0) return;
            lock (_pending)
            {
                float now = Time.realtimeSinceStartup;
                _minionCtx.RemoveAll(c => now > c.Deadline);
                _minionCtx.Add(new MinionCtx { UnitSO = parentUnitSO, Remaining = count, Deadline = now + 5f });
            }
        }

        /// <summary>Consume one slot of an active host minion context matching this UnitSO. Returns true if this spawn is a
        /// death-minion that should be broadcast.</summary>
        private static bool TryClaimMinionContext(object unitSO)
        {
            if (!MinionSpawnEnabled || unitSO == null) return false;
            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < _minionCtx.Count; i++)
            {
                var c = _minionCtx[i];
                if (now > c.Deadline) continue;
                if (!ReferenceEquals(c.UnitSO, unitSO)) continue;
                c.Remaining--;
                if (c.Remaining <= 0) _minionCtx.RemoveAt(i);
                return true;
            }
            return false;
        }

        /// <summary>CLIENT: suppress the whole local SpawnMinions (host mirrors its own minions).</summary>
        public static bool ClientShouldSuppressMinions()
        {
            if (!Enabled || !MinionSpawnEnabled) return false;
            if (NetGameplaySyncBridge.BossMode != NetMode.Client) return false;
            MinionClientSuppressed++;
            if (LogOn) Plugin.Log.Info($"[RuntimeSpawn] client suppressed local SpawnMinions (#{MinionClientSuppressed}) — mirroring host's instead");
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
                lock (_pending)
                {
                    float now = Time.realtimeSinceStartup;
                    // Phase 5.7-DS2: SpawnMinions passes owner=GameManager (unclassified); a live host minion context for
                    // this UnitSO means this is a death-minion that must be broadcast.
                    if (src == null && TryClaimMinionContext(unitSO)) { src = "DeathMinion"; MinionHostBroadcast++; }
                    if (src == null) return; // not a source we sync in this stage
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
            // Issue #5: a one-shot TriggerSpawner (Caves maze skeleton ambush, ...) is host-authoritative — the client's
            // local spawn is suppressed (see TriggerSpawnSyncManager), so the host's is one-sided and safe to mirror.
            if (tn == "TriggerSpawner") return "TriggerSpawn";
            // Phase EM-2: Endless Mode wave enemies are spawned by EndlessModeManager.SetEnemy(SpawnUnitAsync(this,…)).
            // The host is authoritative for the Endless world; a linked client suppresses its own wave driver
            // (EndlessSyncPatches), so the host's spawns are one-sided and safe to mirror as puppets.
            if (tn == "EndlessModeManager")
            {
                try { if (!Plugin.Cfg.EnableEndlessSync.Value) return null; } catch { return null; }
                return "Endless";
            }
            // Phase EM-7c: Endless companion cards spawn allies via FloatingCardManager.SpawnCompanion → SpawnUnitAsync(this,…).
            // FloatingCardManager also spawns shop NPCs (SpawnNPC), so mirror ONLY companion spawns — the host brackets
            // SpawnCompanion with a depth counter (shop NPCs are EM-7d, needing ServiceStation/inventory work). The client
            // suppresses its own companion spawn (EndlessSyncPatches.ExecuteReward_Pre), so the host's is one-sided.
            if (tn == "FloatingCardManager")
            {
                try { if (!Plugin.Cfg.EnableEndlessSync.Value) return null; } catch { return null; }
                if (Patches.EndlessSyncPatches.HostCompanionSpawnDepth > 0) return "EndlessCompanion";
                if (Patches.EndlessSyncPatches.HostShopSpawnDepth > 0) return "EndlessShop"; // EM-7d
                return null;
            }
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
                MirrorSpawnAsync(unitSO, msg.Position, msg.RotationY, msg.HostSpawnIndex, msg.Source);
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
                MirrorSpawnAsync(unitSO, position, 0f, hostSpawnIndex, "");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[RuntimeSpawn] MirrorBossAdd failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static async void MirrorSpawnAsync(object unitSO, Vector3 position, float rotY, int hostSpawnIndex, string source = "")
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
                // F4-ADDS: boss-specific taming of special mirrors (Desert enemy pikes: physics/trigger-pounce off).
                Boss.NetBossEncounterManager.OnRuntimeMirrorSpawned(unit);
                // EM-7c: an Endless companion is a charmed ally on the host; the puppet mirror doesn't carry the charm.
                // Apply the vanilla charmed presentation (heart symbol + allied faction) on the client so it reads as a
                // companion, not an enemy. The unit's AI is puppet-suppressed, so forcedCharmingOwner is inert (visual only).
                if (string.Equals(source, "EndlessCompanion", StringComparison.Ordinal))
                    ApplyEndlessCompanionCharm(unit);
                else if (string.Equals(source, "EndlessShop", StringComparison.Ordinal))
                    ApplyEndlessShopSetup(unit);
            }
            catch (Exception ex) { RuntimeSpawnMirrorFailed++; Plugin.Log.Warn($"[RuntimeSpawn] MirrorSpawnAsync failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static MethodInfo? _applyForcedCharmed;   // Unit/Npc.ApplyForcedCharmed(Unit) — sets the charmed heart symbol + faction
        private static PropertyInfo? _gmPlayerUnitProp;   // GameManager.PlayerUnit

        /// <summary>EM-7c (CLIENT): replicate the vanilla charmed-companion presentation on a mirrored companion puppet —
        /// the heart symbol above its head + allied faction — which <c>ApplyForcedCharmed</c> sets on the host but the
        /// puppet mirror (position/animation/health only) doesn't carry.</summary>
        private static void ApplyEndlessCompanionCharm(object unit)
        {
            try
            {
                object? gm = ResolveGameManager();
                if (gm == null) return;
                _gmPlayerUnitProp ??= gm.GetType().GetProperty("PlayerUnit",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                object? playerUnit = _gmPlayerUnitProp?.GetValue(gm);
                if (playerUnit == null) return;
                if (_applyForcedCharmed == null)
                {
                    // Resolve the single-parameter overload explicitly (a plain GetMethod matched a different arity).
                    foreach (var m in unit.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        if (m.Name == "ApplyForcedCharmed" && m.GetParameters().Length == 1) { _applyForcedCharmed = m; break; }
                }
                if (_applyForcedCharmed == null) { if (LogOn) Plugin.Log.Info("[RuntimeSpawn] companion charm: ApplyForcedCharmed(Unit) not found."); return; }
                _applyForcedCharmed.Invoke(unit, new object[] { playerUnit });
                if (LogOn) Plugin.Log.Info($"[RuntimeSpawn] EM-7c applied companion charm visual to mirror unit={BossReflect.RootName(unit)}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[RuntimeSpawn] companion charm failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static Type? _serviceStationType;    // ServiceStation (base; DoSetup is virtual)
        private static Type? _unitInteractableType;   // UnitInteractable : Interactable
        private static MethodInfo? _addInteractable;  // InteractionManager.AddInteractable(Interactable)
        private static MethodInfo? _doSetup;          // ServiceStation.DoSetup()

        /// <summary>EM-7d (CLIENT): make a mirrored Endless shop NPC usable. The host does this in ExecuteReward's SpawnNPC
        /// branch (register the UnitInteractable + run each ServiceStation.DoSetup), which the client suppresses — so the
        /// puppet has the components but isn't set up. We replicate it here. Vendor stock is rolled by DoSetup and may
        /// differ from the host until stock sync (a later slice); per the design decision, purchases are local for now.</summary>
        private static void ApplyEndlessShopSetup(object unit)
        {
            try
            {
                if (unit is not Component comp) return;
                _serviceStationType   ??= BossReflect.FindType("PerfectRandom.Sulfur.Core.ServiceStation", "ServiceStation");
                _unitInteractableType ??= BossReflect.FindType("UnitInteractable", "PerfectRandom.Sulfur.Core.UnitInteractable");

                // Register the interactable so the client can target + open the shop.
                if (_unitInteractableType != null)
                {
                    var imType = BossReflect.FindType("InteractionManager", "PerfectRandom.Sulfur.Core.InteractionManager");
                    object? im = imType == null ? null : ResolveStaticInstance(imType);
                    var ui = comp.GetComponent(_unitInteractableType);
                    if (im != null && ui != null)
                    {
                        _addInteractable ??= imType!.GetMethod("AddInteractable", BindingFlags.Public | BindingFlags.Instance);
                        _addInteractable?.Invoke(im, new object[] { ui });
                    }
                }

                // Run each ServiceStation's setup (virtual DoSetup dispatches to the concrete shop/stash type).
                if (_serviceStationType != null)
                {
                    _doSetup ??= _serviceStationType.GetMethod("DoSetup", BindingFlags.Public | BindingFlags.Instance);
                    var stations = comp.GetComponents(_serviceStationType);
                    int n = 0;
                    if (stations != null)
                        foreach (var s in stations)
                            try { _doSetup?.Invoke(s, null); n++; }
                            catch (Exception e) { Plugin.Log.Warn($"[RuntimeSpawn] shop DoSetup failed: {e.GetType().Name}: {e.Message}"); }
                    if (LogOn) Plugin.Log.Info($"[RuntimeSpawn] EM-7d applied shop setup ({n} station(s)) to mirror unit={BossReflect.RootName(unit)}");
                }
            }
            catch (Exception ex) { Plugin.Log.Warn($"[RuntimeSpawn] shop setup failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        // ---- HZ-2: reused by the throwable-effect mirror (resolve the throwing weapon's prefab from its ItemId).

        private static PropertyInfo? _itemDbProp;      // AsyncAssetLoading.itemDatabase
        private static Type?         _itemIdType;      // PerfectRandom.Sulfur.Core.ItemId (struct : ctor(ushort))
        private static MethodInfo?   _itemDbIndexer;   // ItemDatabase get_Item(ItemId)
        private static FieldInfo?    _itemDefPrefabField; // ItemDefinition.prefab
        private static bool          _itemResolved;

        /// <summary>The GameManager singleton (or null) — used by the throwable mirror to parent the spawned effect and
        /// resolve the local player as its owner.</summary>
        internal static object? GameManagerInstance() => ResolveGameManager();

        /// <summary>HZ-2: resolve a weapon's world prefab from its <c>ItemId</c> value via the item database
        /// (<c>AsyncAssetLoading.itemDatabase[new ItemId(value)].prefab</c>). Returns the weapon prefab GameObject (which
        /// carries the <c>ThrowableWeapon</c> component) or null. All reflection is cached after the first call.</summary>
        internal static GameObject? ResolveItemPrefab(int itemIdValue)
        {
            try
            {
                EnsureResolved(); // resolves _asyncAssetLoadingInstance
                EnsureItemResolved();
                if (_asyncAssetLoadingInstance == null || _itemDbProp == null || _itemDbIndexer == null || _itemIdType == null) return null;
                object? db = _itemDbProp.GetValue(_asyncAssetLoadingInstance);
                if (db == null) return null;
                object itemId = Activator.CreateInstance(_itemIdType, (ushort)itemIdValue);
                object? itemDef = _itemDbIndexer.Invoke(db, new[] { itemId });
                if (itemDef == null) return null;
                _itemDefPrefabField ??= itemDef.GetType().GetField("prefab", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return _itemDefPrefabField?.GetValue(itemDef) as GameObject;
            }
            catch (Exception ex) { Plugin.Log.Warn($"[ThrowableEffect] ResolveItemPrefab failed: {ex.GetType().Name}: {ex.Message}"); return null; }
        }

        private static void EnsureItemResolved()
        {
            if (_itemResolved) return;
            _itemResolved = true;
            try
            {
                var aalType = BossReflect.FindType("AsyncAssetLoading", "PerfectRandom.Sulfur.Core.AsyncAssetLoading");
                _itemDbProp = aalType?.GetProperty("itemDatabase", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _itemIdType = BossReflect.FindType("ItemId", "PerfectRandom.Sulfur.Core.ItemId");
                var itemDbType = BossReflect.FindType("ItemDatabase", "PerfectRandom.Sulfur.Core.ItemDatabase");
                if (itemDbType != null && _itemIdType != null)
                    _itemDbIndexer = itemDbType.GetMethod("get_Item", BindingFlags.Instance | BindingFlags.Public, null, new[] { _itemIdType }, null);
                Plugin.Log.Info($"[ThrowableEffect] item-db resolved itemDb={_itemDbProp != null} itemId={_itemIdType != null} indexer={_itemDbIndexer != null}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[ThrowableEffect] EnsureItemResolved failed: {ex.GetType().Name}: {ex.Message}"); }
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
