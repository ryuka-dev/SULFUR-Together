using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using SULFURTogether.Networking.Gameplay.Boss;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// EM-7e: host-authoritative mirror for card-spawned <b>non-unit interactables</b> (chests / storage stashes /
    /// service stations) — the deferred EM-7 slice for <c>CardRewardType.SpawnInteractable</c>. See
    /// Docs/EndlessModeSyncPlan.md §4.5 / §7.5.
    ///
    /// <para>Unlike enemies / companions / shop NPCs (Units that ride the RuntimeSpawn puppet pipeline), these are plain
    /// <c>Interactable</c> prefabs created with <c>Object.Instantiate</c>, so there is no <c>UnitSO.id</c> to key on and a
    /// new mirror mechanism is needed. Flow (Shared mode only):</para>
    /// <list type="bullet">
    /// <item><b>HOST</b> runs the vanilla <c>SpawnInteractable</c> branch (correct spawn + setup + arena-transition
    /// cleanup), then reads each newly-spawned root's world position + prefab name and broadcasts it
    /// (<see cref="NetEndlessInteractable"/>).</item>
    /// <item><b>CLIENT</b> suppresses its own local <c>SpawnInteractable</c> (see <c>EndlessSyncPatches.ExecuteReward_Pre</c>)
    /// and instead instantiates the same prefab at the host position on receive, re-running the vanilla setup
    /// (AddInteractable + child interactables + <c>ServiceStation.DoSetup</c> + <c>HiddenChest</c> registration).</item>
    /// </list>
    ///
    /// <para>The client's <c>EndlessModeManager</c> is a slave whose <c>Update</c> is suppressed, so it never runs
    /// <c>ArenaTransitionRoutine</c> (which destroys <c>spawnedInteractables</c> on a stage change). This manager therefore
    /// tracks the mirrored objects itself and destroys them on the host-synced <c>ArenaTransition</c> edge (from the
    /// wave-state snapshot) and on a run reset.</para>
    /// </summary>
    internal static class EndlessInteractableManager
    {
        private static bool Enabled { get { try { return Plugin.Cfg.EnableEndlessSync.Value; } catch { return false; } } }
        private static bool LogOn  { get { try { return Plugin.Cfg.LogEndlessSync.Value; } catch { return false; } } }

        public static int HostBroadcast;
        public static int ClientMirrored;
        public static int ClientMirrorFailed;

        // ---- host capture (armed around ExecuteReward's synchronous SpawnInteractable branch) ----
        private static int _hostSpawnId;
        private static object? _hostCaptureReward;   // the CardReward being executed (for its cardKey)
        private static int _hostCaptureBaseline = -1; // spawnedInteractables.Count before the branch ran

        // ---- client tracking for host-synced cleanup ----
        private static readonly List<GameObject> _clientMirrored = new List<GameObject>();
        private static int _clientLastTransition = -1;

        // ---- reflection ----
        private static bool _resolved;
        private static Type? _interactableType, _serviceStationType, _hiddenChestType, _containerType;
        private static FieldInfo? _fcmSpawnedInteractables; // FloatingCardManager.spawnedInteractables : List<Interactable>
        private static FieldInfo? _crCardKey;               // CardReward.cardKey : string
        private static FieldInfo? _containerStaticLootField; // Container.staticLoot : ItemDefinition (private backing field)
        private static PropertyInfo? _containerStaticLootProp; // Container.StaticLoot { set } — public setter
        private static FieldInfo? _itemDefIdField;          // ItemDefinition.id : ItemId
        private static FieldInfo? _itemIdValueField;        // ItemId.value : ushort
        private static MethodInfo? _addInteractable;        // InteractionManager.AddInteractable(Interactable)
        private static MethodInfo? _doSetup;                // ServiceStation.DoSetup()
        private static MethodInfo? _registerHiddenChest;    // LootManager.RegisterHiddenChest(GameObject)
        private static Type? _imType, _lmType;              // resolve the StaticInstance singletons fresh (may be recreated per scene)

        public static void Reset()
        {
            ClientClearMirrored("run reset");
            _hostCaptureReward = null;
            _hostCaptureBaseline = -1;
            _clientLastTransition = -1;
        }

        // ================================================================== HOST

        /// <summary>HOST (Shared mode): arm capture just before the vanilla <c>SpawnInteractable</c> branch runs. Records
        /// the reward (for its cardKey) and the current <c>spawnedInteractables</c> count so the postfix can read only the
        /// entries this branch adds.</summary>
        public static void HostArmCapture(object reward)
        {
            try
            {
                if (!Enabled || NetGameplaySyncBridge.BossMode != NetMode.Host) return;
                EnsureResolved();
                _hostCaptureReward = reward;
                _hostCaptureBaseline = ReadSpawnedInteractablesCount();
            }
            catch { _hostCaptureReward = null; _hostCaptureBaseline = -1; }
        }

        /// <summary>HOST: after the (synchronous) <c>SpawnInteractable</c> branch completed, broadcast each newly-spawned
        /// root interactable. Roots are the entries whose parent is the Endless manager transform (children of the prefab
        /// carry the root with them and must not be mirrored separately).</summary>
        public static void HostCaptureAndBroadcast()
        {
            object? reward = _hostCaptureReward;
            int baseline = _hostCaptureBaseline;
            _hostCaptureReward = null;
            _hostCaptureBaseline = -1;
            try
            {
                if (reward == null || baseline < 0) return;
                if (!Enabled || NetGameplaySyncBridge.BossMode != NetMode.Host) return;

                object? fcm = EndlessCardManager.ResolveLocalCardManager();
                if (fcm == null || _fcmSpawnedInteractables?.GetValue(fcm) is not System.Collections.IList list) return;

                Transform? managerTransform = ResolveManagerTransform();
                string cardKey = _crCardKey?.GetValue(reward) as string ?? "";
                if (!NetBossEncounterManager.TryGetRunContext(out string chap, out int lvl, out bool hasSeed, out int seed)) { chap = ""; lvl = -1; }

                for (int i = baseline; i < list.Count; i++)
                {
                    if (list[i] is not Component root || root == null) continue;
                    // Only the instantiated roots (parent == manager transform); children of the prefab ride the root.
                    if (managerTransform != null && root.transform.parent != managerTransform) continue;

                    var msg = new NetEndlessInteractable
                    {
                        ChapterName = chap, LevelIndex = lvl, HasLevelSeed = hasSeed, LevelSeed = seed,
                        SpawnId    = ++_hostSpawnId,
                        CardKey    = cardKey,
                        PrefabName = StripClone(root.gameObject.name),
                        StaticLootItemId = ReadContainerStaticLootItemId(root), // Container path only; 0 otherwise
                        Position   = root.transform.position,
                        RotationY  = root.transform.eulerAngles.y,
                    };
                    HostBroadcast++;
                    if (LogOn) Plugin.Log.Info($"[Endless] EM-7e host interactable id={msg.SpawnId} card={cardKey} prefab={msg.PrefabName} loot={msg.StaticLootItemId} pos={msg.Position}");
                    NetGameplaySyncBridge.BroadcastHostEndlessInteractable(msg);
                }
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] EM-7e HostCaptureAndBroadcast failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        // ================================================================== CLIENT

        /// <summary>CLIENT (Shared mode): instantiate the host's interactable prefab at the host position and re-run the
        /// vanilla setup so it is usable. Tracked for host-synced cleanup.</summary>
        public static void ApplyMirror(NetEndlessInteractable msg)
        {
            try
            {
                if (!Enabled || msg == null || NetGameplaySyncBridge.BossMode != NetMode.Client) return;
                if (EndlessSyncManager.IsIndependentMode) return; // this slice is Shared mode only

                // Run-context validation (don't spawn into the wrong level), matching the runtime-spawn mirror.
                if (NetBossEncounterManager.TryGetRunContext(out string chap, out int lvl, out _, out _)
                    && (!string.Equals(chap, msg.ChapterName, StringComparison.Ordinal) || lvl != msg.LevelIndex))
                { if (LogOn) Plugin.Log.Info($"[Endless] EM-7e client drop (run mismatch) id={msg.SpawnId} {msg.ChapterName}:{msg.LevelIndex} local={chap}:{lvl}"); return; }

                EnsureResolved();
                UnityEngine.Object? prefab = EndlessCardManager.ResolveInteractablePrefab(msg.CardKey, msg.PrefabName);
                if (prefab == null)
                {
                    ClientMirrorFailed++;
                    Plugin.Log.Warn($"[Endless] EM-7e client cannot resolve interactable prefab card={msg.CardKey} prefab={msg.PrefabName}");
                    return;
                }

                Transform? parent = ResolveManagerTransform();
                var rot = Quaternion.Euler(0f, msg.RotationY, 0f);
                UnityEngine.Object instObj = parent != null
                    ? UnityEngine.Object.Instantiate(prefab, msg.Position, rot, parent)
                    : UnityEngine.Object.Instantiate(prefab, msg.Position, rot);
                if (instObj == null) { ClientMirrorFailed++; return; }

                GameObject go = instObj is Component c ? c.gameObject : instObj as GameObject;
                if (go == null) { ClientMirrorFailed++; return; }

                // Container path (SpawnFromLootTable): restore the chest's StaticLoot so it contains the card's intended
                // item (needed in Independent-loot mode; in Shared-loot the open is host-authoritative via SL-2 anyway).
                if (msg.StaticLootItemId != 0) ApplyContainerStaticLoot(instObj, msg.StaticLootItemId);

                SetupMirroredInteractable(go);
                _clientMirrored.Add(go);
                ClientMirrored++;
                if (LogOn) Plugin.Log.Info($"[Endless] EM-7e client mirrored interactable id={msg.SpawnId} prefab={msg.PrefabName} pos={msg.Position}");
            }
            catch (Exception ex) { ClientMirrorFailed++; Plugin.Log.Warn($"[Endless] EM-7e ApplyMirror failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        // Reproduce the vanilla FloatingCardManager SpawnInteractable post-instantiate wiring (register root + child
        // interactables, run each ServiceStation.DoSetup, register a HiddenChest with the LootManager).
        private static void SetupMirroredInteractable(GameObject go)
        {
            try
            {
                if (_interactableType == null) return;
                object? interactionMgr = _imType == null ? null : ResolveStaticInstance(_imType);
                Component? root = go.GetComponent(_interactableType) as Component;
                if (root != null && interactionMgr != null && _addInteractable != null)
                    try { _addInteractable.Invoke(interactionMgr, new object[] { root }); } catch { }

                var interactables = go.GetComponentsInChildren(_interactableType);
                if (interactables != null)
                    foreach (var child in interactables)
                        if (child is Component cc && cc != null && !ReferenceEquals(cc, root) && interactionMgr != null && _addInteractable != null)
                            try { _addInteractable.Invoke(interactionMgr, new object[] { cc }); } catch { }

                if (_serviceStationType != null && _doSetup != null)
                {
                    var stations = go.GetComponentsInChildren(_serviceStationType);
                    if (stations != null)
                        foreach (var s in stations)
                            try { _doSetup.Invoke(s, null); } catch (Exception e) { if (LogOn) Plugin.Log.Info($"[Endless] EM-7e DoSetup failed: {e.GetType().Name}"); }
                }

                if (_hiddenChestType != null && _registerHiddenChest != null)
                {
                    object? lootMgr = _lmType == null ? null : ResolveStaticInstance(_lmType);
                    var hc = go.GetComponent(_hiddenChestType);
                    if (hc != null && lootMgr != null)
                        try { _registerHiddenChest.Invoke(lootMgr, new object[] { go }); } catch { }
                }
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] EM-7e SetupMirroredInteractable failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        // HOST: read a spawned root's Container.StaticLoot ItemId (0 if it isn't a loot Container or has no static loot).
        private static int ReadContainerStaticLootItemId(Component root)
        {
            try
            {
                if (_containerType == null || !_containerType.IsInstanceOfType(root)) return 0;
                object? itemDef = _containerStaticLootField?.GetValue(root);
                if (itemDef == null) return 0;
                object? idObj = _itemDefIdField?.GetValue(itemDef);
                if (idObj == null) return 0;
                object? val = _itemIdValueField?.GetValue(idObj);
                return val == null ? 0 : Convert.ToInt32(val);
            }
            catch { return 0; }
        }

        // CLIENT: set the mirrored Container's StaticLoot from the broadcast ItemId so it holds the card's intended item.
        private static void ApplyContainerStaticLoot(UnityEngine.Object instObj, int itemIdValue)
        {
            try
            {
                if (_containerType == null || !_containerType.IsInstanceOfType(instObj)) return;
                object? itemDef = RuntimeSpawnManager.ResolveItemDefinition(itemIdValue);
                if (itemDef == null) { if (LogOn) Plugin.Log.Info($"[Endless] EM-7e client could not resolve StaticLoot item {itemIdValue}"); return; }
                if (_containerStaticLootProp?.GetSetMethod() != null) _containerStaticLootProp.SetValue(instObj, itemDef);
                else _containerStaticLootField?.SetValue(instObj, itemDef);
            }
            catch (Exception ex) { if (LogOn) Plugin.Log.Info($"[Endless] EM-7e ApplyContainerStaticLoot failed: {ex.GetType().Name}"); }
        }

        /// <summary>CLIENT: destroy mirrored interactables on the host-synced <c>ArenaTransition</c> edge — the client's
        /// slave manager never runs the vanilla arena-transition cleanup that destroys <c>spawnedInteractables</c>.</summary>
        public static void OnClientTransition(int transitionState)
        {
            try
            {
                if (NetGameplaySyncBridge.BossMode != NetMode.Client) return;
                int prev = _clientLastTransition;
                _clientLastTransition = transitionState;
                // TransitionState.ArenaTransition == 4: a stage changed → the host destroyed its interactables; do the same.
                if (transitionState == 4 && prev != 4) ClientClearMirrored("arena transition");
            }
            catch { }
        }

        private static void ClientClearMirrored(string reason)
        {
            if (_clientMirrored.Count == 0) return;
            int n = 0;
            foreach (var go in _clientMirrored)
                try { if (go != null) { UnityEngine.Object.Destroy(go); n++; } } catch { }
            _clientMirrored.Clear();
            if (LogOn && n > 0) Plugin.Log.Info($"[Endless] EM-7e client cleared {n} mirrored interactable(s) ({reason})");
        }

        // ================================================================== reflection

        private static int ReadSpawnedInteractablesCount()
        {
            try
            {
                object? fcm = EndlessCardManager.ResolveLocalCardManager();
                if (fcm != null && _fcmSpawnedInteractables?.GetValue(fcm) is System.Collections.IList list) return list.Count;
            }
            catch { }
            return -1;
        }

        private static Transform? ResolveManagerTransform()
        {
            try { return EndlessSyncManager.ResolveEndlessInstance() is Component c && c != null ? c.transform : null; }
            catch { return null; }
        }

        private static string StripClone(string name)
        {
            if (string.IsNullOrEmpty(name)) return name ?? "";
            int i = name.IndexOf("(Clone)", StringComparison.Ordinal);
            return i >= 0 ? name.Substring(0, i).Trim() : name.Trim();
        }

        private static void EnsureResolved()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                _interactableType   = BossReflect.FindType("Interactable", "PerfectRandom.Sulfur.Core.World.Interactable");
                _serviceStationType = BossReflect.FindType("PerfectRandom.Sulfur.Core.ServiceStation", "ServiceStation");
                _hiddenChestType    = BossReflect.FindType("HiddenChest", "PerfectRandom.Sulfur.Core.HiddenChest");
                _containerType      = BossReflect.FindType("Container", "PerfectRandom.Sulfur.Core.World.Container");
                if (_containerType != null)
                {
                    _containerStaticLootField = _containerType.GetField("staticLoot", bf);
                    _containerStaticLootProp  = _containerType.GetProperty("StaticLoot", bf);
                }
                var itemDefType = BossReflect.FindType("ItemDefinition", "PerfectRandom.Sulfur.Core.Items.ItemDefinition", "PerfectRandom.Sulfur.Core.ItemDefinition");
                _itemDefIdField = itemDefType?.GetField("id", bf);
                var itemIdType  = BossReflect.FindType("ItemId", "PerfectRandom.Sulfur.Core.ItemId");
                _itemIdValueField = itemIdType?.GetField("value", bf);

                var fcmType = AccessTools.TypeByName("FloatingCardManager") ?? AccessTools.TypeByName("PerfectRandom.Sulfur.Core.FloatingCardManager");
                _fcmSpawnedInteractables = fcmType?.GetField("spawnedInteractables", bf);

                var crType = AccessTools.TypeByName("CardReward") ?? AccessTools.TypeByName("PerfectRandom.Sulfur.Core.CardReward");
                _crCardKey = crType?.GetField("cardKey", bf);

                _imType = BossReflect.FindType("InteractionManager", "PerfectRandom.Sulfur.Core.InteractionManager");
                if (_imType != null && _interactableType != null)
                    _addInteractable = _imType.GetMethod("AddInteractable", BindingFlags.Public | BindingFlags.Instance, null, new[] { _interactableType }, null)
                                    ?? _imType.GetMethod("AddInteractable", BindingFlags.Public | BindingFlags.Instance);

                if (_serviceStationType != null)
                    _doSetup = _serviceStationType.GetMethod("DoSetup", BindingFlags.Public | BindingFlags.Instance);

                _lmType = BossReflect.FindType("LootManager", "PerfectRandom.Sulfur.Core.Items.LootManager");
                if (_lmType != null)
                    _registerHiddenChest = _lmType.GetMethod("RegisterHiddenChest", BindingFlags.Public | BindingFlags.Instance);

                Plugin.Log.Info($"[Endless] EM-7e resolved interactable={_interactableType != null} station={_serviceStationType != null} chest={_hiddenChestType != null} " +
                                $"spawnedList={_fcmSpawnedInteractables != null} cardKey={_crCardKey != null} addInteractable={_addInteractable != null} doSetup={_doSetup != null} registerChest={_registerHiddenChest != null}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] EM-7e EnsureResolved failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static object? ResolveStaticInstance(Type t)
        {
            try { return t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null); }
            catch { return null; }
        }
    }
}
