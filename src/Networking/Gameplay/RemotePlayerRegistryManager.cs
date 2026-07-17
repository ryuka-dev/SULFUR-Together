using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Plan B (HOST only). See <c>Docs/EnemyActivationAndPlayersRegistry.md</c>.
    ///
    /// Two cooperating jobs, both keyed off the in-scene remote players the host already tracks:
    ///
    /// 1. <b>Activation</b> (<see cref="ActivateNpcsNearRemotePlayers"/>, called from a
    ///    <c>NpcUpdateManager.LateUpdate</c> postfix): the vanilla wake LOD only checks the host
    ///    singleton, so NPCs far from the host never <c>SetActive</c>/<c>ActivateBehaviour</c> even
    ///    when a client is standing next to them. We supplement it by waking inactive NPCs within
    ///    <c>MultiPlayerNpcActivationDistance</c> of any remote player.
    ///
    /// 2. <b>Registry</b> (<see cref="Tick"/>): register each remote player as a headless
    ///    <c>Player</c> entry in <c>GameManager.Players</c> (inserted directly, never via
    ///    <c>AddPlayer</c> which overwrites the host singletons). The game's detection layer
    ///    (<c>BatchedNPCRaycasts</c>) is already multiplayer-aware — it raycasts every
    ///    <c>GameManager.Players</c> entry and fills each NPC's <c>hostilesInLOS</c> with
    ///    <c>players[i].playerUnit</c> — so a registered ghost is detected/targeted natively.
    ///    The ghost <c>Player</c> is kept <c>enabled = false</c> so its Start/OnEnable/Update never
    ///    run (they would NRE on a headless player / clobber our fields / register camera callbacks).
    /// </summary>
    internal sealed class RemotePlayerRegistryManager
    {
        private sealed class Ghost
        {
            public GameObject Go = null!;
            public Component Player = null!;
            public object Unit = null!;
            public float LastUpdatedAt;
        }

        private readonly Dictionary<string, Ghost> _ghosts = new Dictionary<string, Ghost>();

        /// <summary>HOST: the live ghost-player target Units by peer id (static so other host systems — e.g. Endless
        /// targeting, whose enemies use overridetargets instead of the hostilesInLOS scan — can add them as targets).</summary>
        internal static readonly Dictionary<string, object> GhostUnitsByPeer = new Dictionary<string, object>();
        private readonly HashSet<string> _seenThisTick = new HashSet<string>();
        private readonly List<string> _removeScratch = new List<string>();

        // Root GameObject instance ids of live ghosts, so world player-trigger volumes (e.g. NextLevelTrigger) can skip
        // them — a ghost must never drive a level transition or other per-player trigger on the host.
        private static readonly HashSet<int> _ghostGoIds = new HashSet<int>();
        public static bool IsGhostCollider(Collider? col)
        {
            try { return col != null && _ghostGoIds.Contains(col.transform.root.gameObject.GetInstanceID()); }
            catch { return false; }
        }

        /// <summary>True when this Player component belongs to one of our ghosts. Used by callers that must not run
        /// real per-player logic on a headless stand-in; see <see cref="Patches.GhostPlayerPatches"/>.</summary>
        public static bool IsGhostPlayer(object? player)
        {
            try
            {
                return player is Component c && c != null
                    && _ghostGoIds.Contains(c.transform.root.gameObject.GetInstanceID());
            }
            catch { return false; }
        }

        // ---- remote-player positions published for the activation postfix (host-only, main thread) ----
        private static readonly List<Vector3> _remotePositions = new List<Vector3>();
        private static int _activateCursor;

        private static bool RegistryEnabled { get { try { return Plugin.Cfg.EnableRemotePlayerInPlayersList.Value; } catch { return false; } } }
        private static bool ActivationEnabled { get { try { return Plugin.Cfg.EnableMultiPlayerNpcActivation.Value; } catch { return false; } } }
        private static bool LogOn { get { try { return Plugin.Cfg.LogRemotePlayerRegistry.Value; } catch { return false; } } }

        // Freeze fix: the host is mid level-load (ShowLevelNode would NRE on a camera-less ghost in GameManager.Players).
        // Only "Loading"/"Uninitialized" are treated as load — NOT "Cinematic" (that is in-level gameplay where the ghost
        // must keep targeting). Returns false on any failure so the registry behaves as before if state is unreadable.
        private static bool IsHostLoading()
        {
            try
            {
                if (!Plugin.Cfg.SuppressGhostsWhileLoading.Value) return false;
                if (!NetRunStateBridge.TryGetLocalRunState(out var rs) || rs == null) return false;
                string gs = (rs.GameState ?? "").Trim();
                return gs == "Loading" || gs == "Uninitialized";
            }
            catch { return false; }
        }

        // ---- reflection cache ----
        private static bool _resolveAttempted, _resolveOk;
        private static Type? _unitType, _playerType, _npcType;
        private static PropertyInfo? _gmInstance, _gmPlayers, _gmNpcs, _gmPlayerUnit;
        private static MethodInfo? _setStats, _overrideFaction, _spawn, _activateBehaviour;
        private static FieldInfo? _unitSOField, _playerUnitField, _cameraRootField, _playerVisualsField, _excludeFromLodField;
        private static PropertyInfo? _activeHoldableRenderersProp, _playerIndexProp;
        private static PropertyInfo? _batchedInstance;
        private static MethodInfo? _setupNpcList;
        private static Type? _hitmeshType;
        private static FieldInfo? _hitmeshOwnerField, _hitmeshColliderField, _hitmeshHitShapesField;
        private static object? _factionPlayer;

        // ================================================================= host tick (registry)

        /// <summary>Host per-frame: refresh the remote-position buffer (for activation) and, if enabled,
        /// maintain a headless Player per in-scene alive remote player.</summary>
        public void Tick(NetRemotePlayerProxyManager visualProxies, float now, float maxAgeSeconds)
        {
            // Freeze fix: while the host is loading a level, do not maintain ghost Players. Vanilla
            // LevelGeneration.ShowLevelNode (the final generation step) iterates GameManager.Players and dereferences
            // every entry's weaponCamera/playerCamera; a camera-less ghost re-registered mid-load throws an NRE that
            // kills the generation coroutine -> the loading screen hangs at 17/17 (LogOutput139/140). Ghosts only matter
            // during active gameplay and re-register the moment the level is Running again.
            bool registry = RegistryEnabled && EnsureResolved() && !IsHostLoading();

            // One pass: refresh the activation buffer (the postfix reads it) and, if enabled,
            // create/update a ghost Player per in-scene alive remote player.
            _remotePositions.Clear();
            _seenThisTick.Clear();
            visualProxies.ForEachInScenePlayer((peerId, pos) =>
            {
                if (NetPlayerLifeManager.IsPeerDownOrDead(peerId)) return;
                _remotePositions.Add(pos);
                if (registry) { _seenThisTick.Add(peerId); UpdateOrCreate(peerId, pos, now); }
            }, now, maxAgeSeconds);

            // Remove ghosts whose peer is gone / downed (or all of them if the registry was turned off).
            if (_ghosts.Count > 0)
            {
                _removeScratch.Clear();
                foreach (var kv in _ghosts) if (!registry || !_seenThisTick.Contains(kv.Key)) _removeScratch.Add(kv.Key);
                foreach (var id in _removeScratch) Destroy(id, registry ? "player gone" : "registry off");
            }
        }

        public int GhostCount => _ghosts.Count;

        /// <summary>CLIENT (symmetric activation): refresh ONLY the remote-position buffer the activation postfix reads —
        /// no ghost Player registry. A client's enemies are host-driven puppets, so it doesn't need detection ghosts; it
        /// only needs the NPC GameObjects near a remote player (the host / other clients) woken so they can be puppeted
        /// and rendered instead of staying disabled (frozen "木桩"). Fixes the activation asymmetry where the vanilla wake
        /// LOD only considers the client's own local player.</summary>
        public static void RefreshActivationBuffer(NetRemotePlayerProxyManager visualProxies, float now, float maxAgeSeconds)
        {
            if (!ActivationEnabled || visualProxies == null) return;
            _remotePositions.Clear();
            visualProxies.ForEachInScenePlayer((peerId, pos) =>
            {
                if (NetPlayerLifeManager.IsPeerDownOrDead(peerId)) return;
                _remotePositions.Add(pos);
            }, now, maxAgeSeconds);
        }

        private void UpdateOrCreate(string peerId, Vector3 pos, float now)
        {
            if (_ghosts.TryGetValue(peerId, out var g))
            {
                if (g.Go == null) { _ghosts.Remove(peerId); }
                else { g.Go.transform.position = pos; g.LastUpdatedAt = now; return; }
            }
            TryCreate(peerId, pos, now);
        }

        private void TryCreate(string peerId, Vector3 pos, float now)
        {
            try
            {
                var gm = _gmInstance!.GetValue(null, null);
                if (gm == null) { if (LogOn) Plugin.Log.Info("[PlayerRegistry] GM null, defer create"); return; }
                if (!(_gmPlayers!.GetValue(gm, null) is IList playersList))
                { Plugin.Log.Warn("[PlayerRegistry] GameManager.Players not an IList"); return; }

                object? hostPlayerUnit = _gmPlayerUnit?.GetValue(gm, null);
                object? playerUnitSO = (hostPlayerUnit != null && _unitSOField != null) ? _unitSOField.GetValue(hostPlayerUnit) : null;

                // Build the GameObject INACTIVE so Unit.Awake runs only after fields/components are set.
                var go = new GameObject($"SULFURTogether RemotePlayerGhost - {peerId}");
                go.SetActive(false);
                go.transform.position = pos;

                // Item ①: when the ghost is "hittable", put its body on the enemy attack layer (hitboxMask 64 => layer 6)
                // so the native melee/ranged hit raycast finds it; otherwise enemies swing through it harmlessly.
                bool hittable; try { hittable = Plugin.Cfg.EnableGhostPlayerHitbox.Value; } catch { hittable = false; }
                int hitboxLayer = 6; try { hitboxLayer = Plugin.Cfg.RemotePlayerTargetProxyHitboxLayer.Value; } catch { }
                if (hittable) go.layer = hitboxLayer;

                var col = go.AddComponent<CapsuleCollider>();
                col.height = 1.9f; col.radius = 0.4f; col.center = new Vector3(0f, 0.95f, 0f); col.isTrigger = false;
                var rb = go.AddComponent<Rigidbody>();
                rb.isKinematic = true; rb.useGravity = false;

                // playerUnit: a minimal alive Player-faction Unit (the thing BatchedNPCRaycasts puts in hostilesInLOS).
                var unit = go.AddComponent(_unitType!);
                if (_unitSOField != null && playerUnitSO != null) { try { _unitSOField.SetValue(unit, playerUnitSO); } catch { } }
                // Mark as a proxy unit BEFORE activation so (a) Unit.Start's SetupBreakableArmor is skipped (it NREs on our
                // minimal GameObject) and (b) the existing A3 Unit_ReceiveDamage_Pre forward routes any hit to this peer.
                Networking.RemotePlayerTargetProxyManager.RegisterExternalProxyUnit(unit, peerId);

                // Item ①: Hitmesh (owner=ghost unit, collider=our capsule) so a native attack landing on the body is
                // attributed to this unit → Unit.ReceiveDamage → the A3 forward (ReverseProbePatches.Unit_ReceiveDamage_Pre)
                // sends the hit to the owning client's real player. Configured before activation so Hitmesh.OnEnable has
                // its fields. The proxy's own health loss is suppressed by that A3 prefix, so it stays alive = persistent.
                if (hittable && _hitmeshType != null)
                {
                    try
                    {
                        var hm = go.AddComponent(_hitmeshType);
                        _hitmeshOwnerField?.SetValue(hm, unit);
                        _hitmeshColliderField?.SetValue(hm, col);
                        // hitShapes defaults to null on our bare Hitmesh; the game's GetPhysicsMaterial does
                        // `hitShapes.Length` with no null check → NRE on every melee hit (cosmetic hit-sound path).
                        // An empty array makes it return null → PlayMeleeSound no-ops (no sound, no NRE).
                        try { if (_hitmeshHitShapesField?.FieldType.GetElementType() is Type est) _hitmeshHitShapesField.SetValue(hm, Array.CreateInstance(est, 0)); } catch { }
                    }
                    catch (Exception ex) { Plugin.Log.Warn($"[PlayerRegistry] Hitmesh add failed: {ex.Message}"); }
                }

                // cameraRoot: LOS ray origin used by BatchedNPCRaycasts (players[j].cameraRoot.position).
                var eyes = new GameObject("cameraRoot");
                eyes.transform.SetParent(go.transform, false);
                eyes.transform.localPosition = new Vector3(0f, 1.6f, 0f);

                // Headless Player — DISABLED so Start/OnEnable/Update never run.
                var player = (Component)go.AddComponent(_playerType!);
                ((Behaviour)player).enabled = false;
                try { _playerUnitField?.SetValue(player, unit); } catch { }
                try { _cameraRootField?.SetValue(player, eyes.transform); } catch { }
                try { _playerVisualsField?.SetValue(player, Array.CreateInstance(_playerVisualsField.FieldType.GetElementType()!, 0)); } catch { }
                try { if (_activeHoldableRenderersProp?.PropertyType.GetElementType() is Type rt) _activeHoldableRenderersProp.SetValue(player, Array.CreateInstance(rt, 0), null); } catch { }
                try { _playerIndexProp?.SetValue(player, playersList.Count, null); } catch { }

                go.SetActive(true); // Unit.Awake runs; Player stays disabled.

                if (_setStats != null && playerUnitSO != null) { try { _setStats.Invoke(unit, new[] { playerUnitSO }); } catch (Exception ex) { Plugin.Log.Warn($"[PlayerRegistry] SetStats failed: {ex.Message}"); } }
                if (_spawn != null) { try { _spawn.Invoke(unit, null); } catch (Exception ex) { Plugin.Log.Warn($"[PlayerRegistry] Spawn failed: {ex.Message}"); } }
                if (_overrideFaction != null && _factionPlayer != null) { try { _overrideFaction.Invoke(unit, new object[] { _factionPlayer, 999999f }); } catch (Exception ex) { Plugin.Log.Warn($"[PlayerRegistry] OverrideFaction failed: {ex.Message}"); } }

                // Register directly in GameManager.Players (NOT AddPlayer — it overwrites the host singletons).
                try { if (!playersList.Contains(player)) playersList.Add(player); }
                catch (Exception ex) { Plugin.Log.Warn($"[PlayerRegistry] Players.Add failed: {ex.Message}"); }

                // BatchedNPCRaycasts sizes its native arrays (playerPositionCache / LOS mappings) to Players.Count at
                // SetupNpcList time. Vanilla only adds players at level start, so adding one mid-level desyncs the arrays
                // → IndexOutOfRange every frame in BatchedNPCRaycasts.Update. Re-run SetupNpcList to resize.
                RefreshBatchedRaycasts();

                _ghosts[peerId] = new Ghost { Go = go, Player = player, Unit = unit, LastUpdatedAt = now };
                if (unit != null) GhostUnitsByPeer[peerId] = unit;
                _ghostGoIds.Add(go.GetInstanceID());
                Plugin.Log.Info($"[PlayerRegistry] registered ghost player peer={peerId} pos={pos:F1} players={playersList.Count} unitSO={(playerUnitSO == null ? "null" : "ok")}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[PlayerRegistry] create failed peer={peerId}: {ex.GetType().Name}: {ex.Message}"); }
        }

        private void Destroy(string peerId, string reason)
        {
            if (!_ghosts.TryGetValue(peerId, out var g)) return;
            _ghosts.Remove(peerId);
            GhostUnitsByPeer.Remove(peerId);
            try { if (g.Go != null) _ghostGoIds.Remove(g.Go.GetInstanceID()); } catch { }
            bool removedFromPlayers = false;
            try
            {
                var gm = _gmInstance?.GetValue(null, null);
                if (gm != null && _gmPlayers?.GetValue(gm, null) is IList players && g.Player != null) removedFromPlayers = RemoveFromList(players, g.Player);
            }
            catch { }
            try { Networking.RemotePlayerTargetProxyManager.UnregisterExternalProxyUnit(g.Unit); } catch { }
            try { if (g.Go != null) UnityEngine.Object.Destroy(g.Go); } catch { }
            if (removedFromPlayers) RefreshBatchedRaycasts(); // resize native arrays back down
            if (LogOn) Plugin.Log.Info($"[PlayerRegistry] removed ghost player peer={peerId} reason={reason}");
        }

        private static bool RemoveFromList(IList list, object item)
        {
            if (!list.Contains(item)) return false;
            list.Remove(item);
            return true;
        }

        /// <summary>Re-run BatchedNPCRaycasts.SetupNpcList so its native arrays match the current Players.Count.</summary>
        private static void RefreshBatchedRaycasts()
        {
            try
            {
                var inst = _batchedInstance?.GetValue(null, null);
                if (inst != null) _setupNpcList?.Invoke(inst, null);
            }
            catch (Exception ex) { Plugin.Log.Warn($"[PlayerRegistry] SetupNpcList refresh failed: {ex.Message}"); }
        }

        public void Clear()
        {
            foreach (var id in new List<string>(_ghosts.Keys)) Destroy(id, "clear");
            _ghosts.Clear();
            _remotePositions.Clear();
        }

        // ================================================================= activation postfix

        /// <summary>Called from the <c>NpcUpdateManager.LateUpdate</c> postfix. Wakes inactive NPCs near any
        /// remote player. Rate-limited and round-robined so a big roster does not stall a frame.</summary>
        public static void ActivateNpcsNearRemotePlayers()
        {
            if (!ActivationEnabled) return;
            if (_remotePositions.Count == 0) return;
            if (!EnsureResolved()) return;

            float dist; try { dist = Plugin.Cfg.MultiPlayerNpcActivationDistance.Value; } catch { dist = 60f; }
            int budget; try { budget = Plugin.Cfg.MultiPlayerNpcActivationsPerFrame.Value; } catch { budget = 8; }
            float distSq = dist * dist;

            try
            {
                var gm = _gmInstance!.GetValue(null, null);
                if (gm == null) return;
                if (!(_gmNpcs!.GetValue(gm, null) is IList npcs) || npcs.Count == 0) return;

                int count = npcs.Count;
                int scanned = 0, activated = 0;
                // Round-robin start so we don't always favour the same NPCs when the budget is small.
                int start = (_activateCursor % count + count) % count;
                for (int n = 0; n < count && activated < budget; n++)
                {
                    var npc = npcs[(start + n) % count] as Component;
                    scanned++;
                    if (npc == null) continue;
                    var goNpc = npc.gameObject;
                    if (goNpc.activeSelf) continue; // already awake — vanilla LateUpdate owns it
                    if (_excludeFromLodField != null && _excludeFromLodField.GetValue(npc) is bool ex && ex) continue;

                    Vector3 p = npc.transform.position;
                    bool near = false;
                    for (int i = 0; i < _remotePositions.Count; i++)
                    {
                        if ((_remotePositions[i] - p).sqrMagnitude < distSq) { near = true; break; }
                    }
                    if (!near) continue;

                    try
                    {
                        goNpc.SetActive(true);
                        _activateBehaviour?.Invoke(npc, null);
                        activated++;
                    }
                    catch (Exception ex2) { Plugin.Log.Warn($"[PlayerRegistry] activate failed: {ex2.Message}"); }
                }
                _activateCursor = (start + scanned) % count;
                if (activated > 0 && LogOn) Plugin.Log.Info($"[PlayerRegistry] activation woke {activated} npc(s) near {_remotePositions.Count} remote player(s)");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[PlayerRegistry] activation pass failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        // ================================================================= reflection

        private static bool EnsureResolved()
        {
            if (_resolveAttempted) return _resolveOk;
            _resolveAttempted = true;
            try
            {
                _unitType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.Units.Unit");
                _playerType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.Units.Player");
                _npcType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.Units.Npc");
                var gmType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.GameManager");
                if (_unitType == null || _playerType == null || gmType == null)
                { Plugin.Log.Warn("[PlayerRegistry] resolve failed: Unit/Player/GameManager type missing"); return false; }

                _gmInstance = AccessTools.Property(gmType, "Instance");
                _gmPlayers = AccessTools.Property(gmType, "Players");
                _gmNpcs = AccessTools.Property(gmType, "npcs");
                _gmPlayerUnit = AccessTools.Property(gmType, "PlayerUnit");

                _setStats = AccessTools.Method(_unitType, "SetStats");
                _overrideFaction = AccessTools.Method(_unitType, "OverrideFaction");
                _spawn = AccessTools.Method(_unitType, "Spawn", Type.EmptyTypes);
                _unitSOField = AccessTools.Field(_unitType, "unitSO");

                _playerUnitField = AccessTools.Field(_playerType, "playerUnit");
                _cameraRootField = AccessTools.Field(_playerType, "cameraRoot");
                _playerVisualsField = AccessTools.Field(_playerType, "playerVisuals");
                _activeHoldableRenderersProp = AccessTools.Property(_playerType, "activeHoldableRenderers");
                _playerIndexProp = AccessTools.Property(_playerType, "playerIndex");

                if (_npcType != null)
                {
                    _excludeFromLodField = AccessTools.Field(_npcType, "excludeFromNpcLOD");
                    _activateBehaviour = AccessTools.Method(_npcType, "ActivateBehaviour");
                }

                if (_overrideFaction != null)
                {
                    var factionType = _overrideFaction.GetParameters()[0].ParameterType;
                    try { _factionPlayer = Enum.Parse(factionType, "Player"); } catch (Exception ex) { Plugin.Log.Warn($"[PlayerRegistry] FactionIds.Player parse failed: {ex.Message}"); }
                }

                var batchedType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.BatchedNPCRaycasts");
                if (batchedType != null)
                {
                    _batchedInstance = AccessTools.Property(batchedType, "Instance");
                    _setupNpcList = AccessTools.Method(batchedType, "SetupNpcList");
                }

                // Item ①: Hitmesh so enemy attacks land on the ghost and route to the client via A3.
                _hitmeshType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.Hitmesh");
                if (_hitmeshType != null)
                {
                    _hitmeshOwnerField = AccessTools.Field(_hitmeshType, "owner");
                    _hitmeshColliderField = AccessTools.Field(_hitmeshType, "hitmeshCollider");
                    _hitmeshHitShapesField = AccessTools.Field(_hitmeshType, "hitShapes");
                }

                _resolveOk = _gmInstance != null && _gmPlayers != null && _gmNpcs != null;
                Plugin.Log.Info($"[PlayerRegistry] resolve ok={_resolveOk} player={_playerType != null} players={_gmPlayers != null} npcs={_gmNpcs != null} activateBehaviour={_activateBehaviour != null} excludeLod={_excludeFromLodField != null} setupNpcList={_setupNpcList != null} faction={_factionPlayer}");
                return _resolveOk;
            }
            catch (Exception ex) { Plugin.Log.Warn($"[PlayerRegistry] resolve exception: {ex.GetType().Name}: {ex.Message}"); return false; }
        }
    }
}
