using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SULFURTogether.Networking
{
    /// <summary>
    /// Phase 5.5-P3-A2 (HOST only): for each remote player standing in the host's scene, maintain a minimal targetable
    /// <c>Unit</c> ("target proxy") at the remote player's position. Reverse-engineering (LogOutput57.x) showed the host's
    /// enemy AI (<c>AiAgent</c>, <c>onlyTargetPlayer=False</c>) acquires targets by scanning <c>GameManager.units</c> for
    /// hostile-faction Units within line-of-sight (range ~30). A Unit whose faction is overridden to Player is therefore
    /// hostile to Goblins/Congregation/etc and gets detected → aggro → path → attack — which is exactly what we want so a
    /// client can wake/fight enemies the host is far from.
    ///
    /// This is NOT a Player: no PlayerScript / EquipmentManager / InputReader / camera. Just a Unit + collider + kinematic
    /// rigidbody, faction overridden to Player, registered in GameManager.units. Damage routing to the client and remote
    /// player down/death are later steps (A3/A4). Gated OFF by default (EnableRemotePlayerTargetProxy) — experimental.
    /// </summary>
    internal sealed class RemotePlayerTargetProxyManager
    {
        private sealed class Proxy
        {
            public GameObject Go = null!;
            public object Unit = null!;
            public float LastUpdatedAt;
        }

        private readonly Dictionary<string, Proxy> _proxies = new Dictionary<string, Proxy>();

        // Proxy Unit instance id -> owning peerId. Static so Harmony patches can (a) skip game init paths
        // (SetupBreakableArmor) that NRE on our minimal GameObject and (b) route damage dealt to the proxy back to the
        // owning client, without needing a manager instance.
        private static readonly Dictionary<int, string> _proxyUnitPeers = new Dictionary<int, string>();
        public static bool IsProxyUnit(object? unit)
        {
            try { if (unit is UnityEngine.Object uo && uo != null) { lock (_proxyUnitPeers) return _proxyUnitPeers.ContainsKey(uo.GetInstanceID()); } }
            catch { }
            return false;
        }
        /// <summary>Register an externally-built unit (e.g. the Plan B headless Player's unit) as a proxy unit so the
        /// shared guards apply — most importantly <c>Unit_SetupBreakableArmor_Pre</c> skips it (a minimal GameObject NREs
        /// in SetupBreakableArmor). Call BEFORE activating the GameObject so Unit.Start sees the registration.</summary>
        public static void RegisterExternalProxyUnit(object? unit, string peerId)
        {
            try { if (unit is UnityEngine.Object uo && uo != null) { lock (_proxyUnitPeers) _proxyUnitPeers[uo.GetInstanceID()] = peerId; } } catch { }
        }
        public static void UnregisterExternalProxyUnit(object? unit)
        {
            try { if (unit is UnityEngine.Object uo && uo != null) { lock (_proxyUnitPeers) _proxyUnitPeers.Remove(uo.GetInstanceID()); } } catch { }
        }

        /// <summary>Host: if <paramref name="unit"/> is a remote-player target proxy, returns its owning peerId.</summary>
        public static bool TryGetProxyPeer(object? unit, out string peerId)
        {
            peerId = "";
            try { if (unit is UnityEngine.Object uo && uo != null) { lock (_proxyUnitPeers) return _proxyUnitPeers.TryGetValue(uo.GetInstanceID(), out peerId); } }
            catch { }
            return false;
        }

        // ---- reflection cache ----
        private static bool _resolveAttempted;
        private static bool _resolveOk;
        private static Type? _unitType;
        private static Type? _factionType;
        private static object? _factionPlayer;
        private static PropertyInfo? _gmInstance;
        private static PropertyInfo? _gmUnits;
        private static PropertyInfo? _gmPlayerUnit;
        private static MethodInfo? _setStats;
        private static MethodInfo? _overrideFaction;
        private static MethodInfo? _spawn;
        private static FieldInfo? _unitSOField;
        private static FieldInfo? _isPlayerField;
        // OverrideTarget API (AiAgent.overridetargets.AddUnits(List<Unit>, TargetType) — the game's force-target hook).
        private static FieldInfo? _overrideTargetsField;
        private static MethodInfo? _addUnits;
        private static MethodInfo? _clearUnits;
        private static MethodInfo? _hasTargetsGetter;
        private static object? _targetTypeValue;
        private static Type? _unitListType;
        // Hitmesh (so the native melee/ranged hit-detection raycast — on hitboxMask layer — finds the proxy and damages it).
        private static Type? _hitmeshType;
        private static FieldInfo? _hitmeshOwnerField;
        private static FieldInfo? _hitmeshColliderField;

        private static bool Enabled { get { try { return Plugin.Cfg.EnableRemotePlayerTargetProxy.Value; } catch { return false; } } }
        private static bool LogOn  { get { try { return Plugin.Cfg.LogRemotePlayerTargetProxy.Value; } catch { return false; } } }

        public int ProxyCount => _proxies.Count;

        /// <summary>Host per-frame: create/update a target proxy for every in-scene remote player; remove the rest.</summary>
        public void Tick(NetRemotePlayerProxyManager visualProxies, float now, float maxAgeSeconds)
        {
            if (!Enabled) { if (_proxies.Count > 0) Clear(); return; }
            if (!EnsureResolved()) return;

            bool removeWhenDowned = true;
            try { removeWhenDowned = Plugin.Cfg.RemoveTargetProxyWhenPeerDowned.Value; } catch { }

            _seenThisTick.Clear();
            visualProxies.ForEachInScenePlayer((peerId, pos) =>
            {
                // A3.2: a downed/dead remote player has no target proxy — leaving it out drops it from _seenThisTick so
                // the removal pass below destroys its proxy (and DriveAggro releases enemies), so enemies stop attacking
                // the downed player. Recreated automatically once the player revives (no longer down/dead).
                if (removeWhenDowned && Gameplay.NetPlayerLifeManager.IsPeerDownOrDead(peerId)) return;
                _seenThisTick.Add(peerId);
                UpdateOrCreate(peerId, pos, now);
            }, now, maxAgeSeconds);

            // Remove proxies for players no longer present.
            if (_proxies.Count > 0)
            {
                _removeScratch.Clear();
                foreach (var kv in _proxies) if (!_seenThisTick.Contains(kv.Key)) _removeScratch.Add(kv.Key);
                foreach (var id in _removeScratch) Destroy(id, "player gone");
            }

            DriveAggro(now);
            DiagProxies(now);
        }

        // OverrideTarget instances we have forced to target a proxy (enemyAiId -> OverrideTarget) so we can ClearUnits
        // them when the remote player leaves their range.
        private readonly Dictionary<int, object> _overriddenOt = new Dictionary<int, object>();
        private readonly HashSet<int> _aggroSeen = new HashSet<int>();
        private readonly List<int> _staleAggro = new List<int>();
        private float _lastAggroAt;

        private void DriveAggro(float now)
        {
            bool on; try { on = Plugin.Cfg.RemotePlayerTargetProxyForceAggro.Value; } catch { on = false; }
            if (!on || _addUnits == null || _overrideTargetsField == null || _unitListType == null || _targetTypeValue == null)
            { if (_overriddenOt.Count > 0) ClearAllOverrides(); return; }
            if (now - _lastAggroAt < 0.5f) return; // refresh rate
            _lastAggroAt = now;

            float fallbackRange; try { fallbackRange = Plugin.Cfg.RemotePlayerTargetProxyAggroRange.Value; } catch { fallbackRange = 30f; }
            try
            {
                var gm = _gmInstance!.GetValue(null, null);
                if (gm == null) return;
                // Scene-ready guard: don't run before the host has a live level.
                if (!NetRunStateBridge.TryGetLocalRunState(out var hostState) || !hostState.HasLevel) { if (_overriddenOt.Count > 0) ClearAllOverrides(); return; }

                var units = _gmUnits?.GetValue(gm, null) as IEnumerable;
                if (units == null) return;

                // Host player position — only override enemies that are CLOSER to a remote player than to the host
                // (so we don't steal the host's enemies, and an enemy commits to whoever it's nearest = fairness + no flip-flop).
                bool onlyCloser; try { onlyCloser = Plugin.Cfg.RemotePlayerTargetProxyOnlyWhenCloser.Value; } catch { onlyCloser = false; }
                object? hostPlayer = onlyCloser ? _gmPlayerUnit?.GetValue(gm, null) : null;
                bool hasHost = hostPlayer is Component hpc && hpc != null;
                Vector3 hostPos = hasHost ? ((Component)hostPlayer!).transform.position : Vector3.zero;

                _aggroSeen.Clear();
                foreach (var kv in _proxies)
                {
                    if (kv.Value.Go == null) continue;
                    Vector3 ppos = kv.Value.Go.transform.position;
                    object proxyUnit = kv.Value.Unit;
                    foreach (var u in units)
                    {
                        if (ReferenceEquals(u, proxyUnit)) continue;
                        if (!(u is Component uc) || uc == null) continue;
                        var ai = Member(u, "AiAgent");
                        if (ai == null) continue;
                        Vector3 epos = uc.transform.position;
                        float distToProxy = Vector3.Distance(epos, ppos);

                        // Use the enemy's own detection range (fallback to config) instead of a flat number.
                        float range = fallbackRange;
                        try { if (Member(ai, "currentLosRange") is float los && los > 1f) range = los; } catch { }
                        if (distToProxy > range) continue;
                        // Optional fairness gate (default off): only take this enemy if the remote player is closer than
                        // the host. Off because enemy AI only wakes near the host, so the host is usually 'closer'.
                        if (onlyCloser && hasHost && Vector3.Distance(epos, hostPos) <= distToProxy) continue;

                        var ot = _overrideTargetsField.GetValue(ai);
                        if (ot == null) continue;

                        int id = (ai is UnityEngine.Object aio && aio != null) ? aio.GetInstanceID() : ai.GetHashCode();
                        // Don't clobber a NATIVE override we didn't write.
                        if (!_overriddenOt.ContainsKey(id) && _hasTargetsGetter != null)
                        { try { if (_hasTargetsGetter.Invoke(ot, null) is bool ht && ht) continue; } catch { } }

                        var list = (IList)Activator.CreateInstance(_unitListType!)!;
                        list.Add(proxyUnit);
                        try { _addUnits.Invoke(ot, new object[] { list, _targetTypeValue! }); } catch { continue; }

                        _aggroSeen.Add(id);
                        _overriddenOt[id] = ot;
                    }
                }

                // Release enemies we previously overrode that are no longer near any proxy.
                if (_overriddenOt.Count > _aggroSeen.Count)
                {
                    _staleAggro.Clear();
                    foreach (var kv in _overriddenOt) if (!_aggroSeen.Contains(kv.Key)) _staleAggro.Add(kv.Key);
                    foreach (var id in _staleAggro)
                    {
                        try { _clearUnits?.Invoke(_overriddenOt[id], null); } catch { }
                        _overriddenOt.Remove(id);
                    }
                }
                if (LogOn) Plugin.Log.Info($"[TargetProxy] aggro driven enemies={_aggroSeen.Count} tracked={_overriddenOt.Count} (per-enemy LOS range, closer-than-host)");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[TargetProxy] DriveAggro failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private void ClearAllOverrides()
        {
            foreach (var ot in _overriddenOt.Values) { try { _clearUnits?.Invoke(ot, null); } catch { } }
            _overriddenOt.Clear();
        }

        // Periodic: report each proxy's nearest unit so we can tell whether an enemy was even within LOS range (~30m)
        // when the proxy went un-targeted (vs a detection/faction/alive problem).
        private float _lastDiagAt;
        private void DiagProxies(float now)
        {
            if (!LogOn || _proxies.Count == 0 || now - _lastDiagAt < 3f) return;
            _lastDiagAt = now;
            try
            {
                var gm = _gmInstance!.GetValue(null, null);
                var units = gm == null ? null : _gmUnits?.GetValue(gm, null) as IEnumerable;
                foreach (var kv in _proxies)
                {
                    if (kv.Value.Go == null) continue;
                    object proxyUnit = kv.Value.Unit;
                    Vector3 ppos = kv.Value.Go.transform.position;

                    // Nearest ENEMY unit (has an AiAgent) so we can inspect its detection of the proxy.
                    float nearest = float.MaxValue; string nearestName = "none"; object? nearestEnemy = null;
                    if (units != null)
                        foreach (var u in units)
                        {
                            if (ReferenceEquals(u, proxyUnit)) continue;
                            if (!(u is Component uc) || uc == null) continue;
                            if (Member(u, "AiAgent") == null) continue; // enemies have an AiAgent; player does not via this path
                            float d = Vector3.Distance(uc.transform.position, ppos);
                            if (d < nearest) { nearest = d; nearestName = uc.gameObject.name; nearestEnemy = u; }
                        }

                    // Proxy state: did OverrideFaction take? is it alive? is it in the enemy's hostile list?
                    string faction = Describe(Member(proxyUnit, "FactionId"));
                    string dead = Describe(Member(proxyUnit, "IsDead") ?? Member(proxyUnit, "isDead"));
                    string isP = Describe(Member(proxyUnit, "isPlayer"));
                    string state = Describe(Member(proxyUnit, "UnitState") ?? Member(proxyUnit, "unitState"));
                    string alive = Describe(Member(proxyUnit, "IsAlive"));
                    string invuln = Describe(Member(proxyUnit, "isInvulnerable") ?? Member(proxyUnit, "IsInvulnerable") ?? Member(proxyUnit, "get_isInvulnerable"));
                    string enemyTarget = "?", enemyHostiles = "?", hostileTo = "?";
                    if (nearestEnemy != null)
                    {
                        var ai = Member(nearestEnemy, "AiAgent");
                        enemyTarget = Describe(Member(ai, "target"));
                        enemyHostiles = DescribeEnumerable(Member(ai, "hostilesInLOS"));
                        try { var mh = proxyUnit.GetType().GetMethod("IsHostileTo", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (mh != null) hostileTo = Describe(mh.Invoke(proxyUnit, new[] { nearestEnemy })); } catch { }
                    }
                    Plugin.Log.Info($"[TargetProxy] diag peer={kv.Key} pos={ppos:F1} proxy[faction={faction} state={state} alive={alive} invuln={invuln} hostileToNearest={hostileTo}] nearestEnemy={nearestName} dist={(nearest == float.MaxValue ? -1f : nearest):F1} enemyTarget={enemyTarget} enemyHostiles={enemyHostiles}");
                }
            }
            catch (Exception ex) { Plugin.Log.Warn($"[TargetProxy] diag failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static object? Member(object? o, string name)
        {
            try
            {
                if (o == null) return null;
                const BindingFlags f = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                for (Type? c = o.GetType(); c != null && c != typeof(object); c = c.BaseType)
                {
                    var p = c.GetProperty(name, f); if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(o, null);
                    var fi = c.GetField(name, f); if (fi != null) return fi.GetValue(o);
                }
            }
            catch { }
            return null;
        }

        private static string Describe(object? v)
        {
            if (v == null) return "null";
            if (v is UnityEngine.Object uo) return uo == null ? "null(destroyed)" : $"{v.GetType().Name}:{uo.name}";
            return v.ToString();
        }

        private static string DescribeEnumerable(object? c)
        {
            if (c == null) return "null";
            if (c is IEnumerable en && !(c is string))
            {
                var sb = new System.Text.StringBuilder(); int n = 0;
                foreach (var e in en) { if (n > 0) sb.Append(", "); sb.Append(Describe(e)); if (++n >= 8) { sb.Append(",..."); break; } }
                return $"[{n}]{{{sb}}}";
            }
            return Describe(c);
        }

        private readonly HashSet<string> _seenThisTick = new HashSet<string>();
        private readonly List<string> _removeScratch = new List<string>();

        private void UpdateOrCreate(string peerId, Vector3 pos, float now)
        {
            if (_proxies.TryGetValue(peerId, out var existing))
            {
                if (existing.Go == null) { _proxies.Remove(peerId); }
                else
                {
                    existing.Go.transform.position = pos;
                    existing.LastUpdatedAt = now;
                    return;
                }
            }
            TryCreate(peerId, pos, now);
        }

        private void TryCreate(string peerId, Vector3 pos, float now)
        {
            try
            {
                var gm = _gmInstance!.GetValue(null, null);
                if (gm == null) { if (LogOn) Plugin.Log.Info("[TargetProxy] GM null, defer create"); return; }
                object? playerUnit = _gmPlayerUnit?.GetValue(gm, null);
                object? playerUnitSO = (playerUnit != null && _unitSOField != null) ? _unitSOField.GetValue(playerUnit) : null;
                int layer = (playerUnit is Component pc && pc != null) ? pc.gameObject.layer : 0;

                // The hitbox collider must sit on the enemy melee/ranged hit layer (hitboxMask 64 => layer 6) so the
                // native HitmeshHitByMelee raycast finds it; otherwise the body layer is invisible to attacks.
                int hitboxLayer = layer;
                try { hitboxLayer = Plugin.Cfg.RemotePlayerTargetProxyHitboxLayer.Value; } catch { }

                // Configure while INACTIVE so Unit.Awake()/Hitmesh.OnEnable() run only after fields are set.
                var go = new GameObject($"SULFURTogether RemotePlayerTargetProxy - {peerId}");
                go.SetActive(false);
                go.layer = hitboxLayer;
                go.transform.position = pos;

                var col = go.AddComponent<CapsuleCollider>();
                col.height = 1.9f; col.radius = 0.4f; col.center = new Vector3(0f, 0.95f, 0f); col.isTrigger = false;
                var rb = go.AddComponent<Rigidbody>();
                rb.isKinematic = true; rb.useGravity = false;

                var unit = go.AddComponent(_unitType!);
                if (_unitSOField != null && playerUnitSO != null) { try { _unitSOField.SetValue(unit, playerUnitSO); } catch { } }
                if (unit is UnityEngine.Object uo) { lock (_proxyUnitPeers) _proxyUnitPeers[uo.GetInstanceID()] = peerId; } // mark before Awake/Start run

                // A3: add a Hitmesh (owner=proxy, collider=our capsule) so native attacks hit it -> Unit.ReceiveDamage
                // -> our forward hook -> client takes damage. Configured before activation so OnEnable has its fields.
                if (_hitmeshType != null)
                {
                    try
                    {
                        var hm = go.AddComponent(_hitmeshType);
                        _hitmeshOwnerField?.SetValue(hm, unit);
                        _hitmeshColliderField?.SetValue(hm, col);
                    }
                    catch (Exception ex) { Plugin.Log.Warn($"[TargetProxy] Hitmesh add failed: {ex.GetType().Name}: {ex.Message}"); }
                }

                // Optional body blocker: a child collider on the REAL player's body layer so a charging/dashing enemy's
                // locomotion physically collides with the remote player (the hitbox collider on hitboxLayer is invisible
                // to body-vs-body collision). Parented to the kinematic-rb proxy -> acts as an immovable blocker.
                bool bodyBlocker = true; try { bodyBlocker = Plugin.Cfg.RemotePlayerTargetProxyBodyBlocker.Value; } catch { }
                if (bodyBlocker && layer != hitboxLayer)
                {
                    try
                    {
                        var body = new GameObject("BodyBlocker");
                        body.layer = layer; // real player body layer -> enemy-body collision matrix applies
                        body.transform.SetParent(go.transform, false);
                        var bcol = body.AddComponent<CapsuleCollider>();
                        bcol.height = 1.9f; bcol.radius = 0.4f; bcol.center = new Vector3(0f, 0.95f, 0f); bcol.isTrigger = false;
                    }
                    catch (Exception ex) { Plugin.Log.Warn($"[TargetProxy] body blocker add failed: {ex.GetType().Name}: {ex.Message}"); }
                }

                go.SetActive(true); // Unit.Awake() + Hitmesh.OnEnable() run here

                // Ensure stats/health, bring it ALIVE (Spawn — a bare Unit defaults to state=Dead and the enemy scan
                // skips dead units), then make it Player faction (hostile to enemy factions → enemies target it).
                if (_setStats != null && playerUnitSO != null) { try { _setStats.Invoke(unit, new[] { playerUnitSO }); } catch (Exception ex) { Plugin.Log.Warn($"[TargetProxy] SetStats failed: {ex.GetType().Name}: {ex.Message}"); } }
                if (_spawn != null) { try { _spawn.Invoke(unit, null); } catch (Exception ex) { Plugin.Log.Warn($"[TargetProxy] Spawn failed: {ex.GetType().Name}: {ex.Message}"); } }
                if (_overrideFaction != null && _factionPlayer != null) { try { _overrideFaction.Invoke(unit, new object[] { _factionPlayer, 999999f }); } catch (Exception ex) { Plugin.Log.Warn($"[TargetProxy] OverrideFaction failed: {ex.GetType().Name}: {ex.Message}"); } }
                // The enemy AI hunts THE player (IsPlayerInSight/playerUnit) — faction alone isn't detected (log61: proxy
                // alive+Player but never in hostilesInLOS). isPlayer=True tests whether detection keys on this flag.
                bool setIsPlayer = false; try { setIsPlayer = Plugin.Cfg.RemotePlayerTargetProxySetIsPlayer.Value; } catch { }
                if (setIsPlayer && _isPlayerField != null) { try { _isPlayerField.SetValue(unit, true); } catch (Exception ex) { Plugin.Log.Warn($"[TargetProxy] set isPlayer failed: {ex.GetType().Name}: {ex.Message}"); } }

                // Register in GameManager.units so the enemy faction-LOS scan considers it (Unit.Awake may already have).
                try
                {
                    if (_gmUnits?.GetValue(gm, null) is IList units && !units.Contains(unit)) units.Add(unit);
                }
                catch (Exception ex) { Plugin.Log.Warn($"[TargetProxy] units.Add failed: {ex.GetType().Name}: {ex.Message}"); }

                _proxies[peerId] = new Proxy { Go = go, Unit = unit, LastUpdatedAt = now };
                Plugin.Log.Info($"[TargetProxy] created proxy peer={peerId} pos={pos:F1} layer={layer} unitSO={(playerUnitSO == null ? "null" : "ok")} faction={(_factionPlayer?.ToString() ?? "?")}");
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[TargetProxy] create failed peer={peerId}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void Destroy(string peerId, string reason)
        {
            if (!_proxies.TryGetValue(peerId, out var p)) return;
            _proxies.Remove(peerId);
            try { if (p.Unit is UnityEngine.Object uo && uo != null) { lock (_proxyUnitPeers) _proxyUnitPeers.Remove(uo.GetInstanceID()); } } catch { }
            try
            {
                var gm = _gmInstance?.GetValue(null, null);
                if (gm != null && _gmUnits?.GetValue(gm, null) is IList units) units.Remove(p.Unit);
            }
            catch { }
            try { if (p.Go != null) UnityEngine.Object.Destroy(p.Go); } catch { }
            if (LogOn) Plugin.Log.Info($"[TargetProxy] destroyed proxy peer={peerId} reason={reason}");
        }

        public void Clear()
        {
            ClearAllOverrides();
            foreach (var id in new List<string>(_proxies.Keys)) Destroy(id, "clear");
            _proxies.Clear();
        }

        private static bool EnsureResolved()
        {
            if (_resolveAttempted) return _resolveOk;
            _resolveAttempted = true;
            try
            {
                _unitType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.Units.Unit");
                var gmType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.GameManager");
                if (_unitType == null || gmType == null) { Plugin.Log.Warn("[TargetProxy] resolve failed: Unit/GameManager type missing"); return false; }

                _gmInstance = AccessTools.Property(gmType, "Instance");
                _gmUnits = AccessTools.Property(gmType, "units");
                _gmPlayerUnit = AccessTools.Property(gmType, "PlayerUnit");
                _setStats = AccessTools.Method(_unitType, "SetStats");
                _overrideFaction = AccessTools.Method(_unitType, "OverrideFaction");
                _spawn = AccessTools.Method(_unitType, "Spawn", Type.EmptyTypes);
                _unitSOField = AccessTools.Field(_unitType, "unitSO");
                _isPlayerField = AccessTools.Field(_unitType, "isPlayer");

                if (_overrideFaction != null)
                {
                    _factionType = _overrideFaction.GetParameters()[0].ParameterType;
                    try { _factionPlayer = Enum.Parse(_factionType, "Player"); }
                    catch (Exception ex) { Plugin.Log.Warn($"[TargetProxy] FactionIds.Player parse failed: {ex.Message}"); }
                }

                // OverrideTarget force-target API.
                var aiType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.Units.AI.AiAgent");
                _overrideTargetsField = aiType == null ? null : AccessTools.Field(aiType, "overridetargets");
                var otType = _overrideTargetsField?.FieldType;
                if (otType != null)
                {
                    _addUnits = AccessTools.Method(otType, "AddUnits");
                    _clearUnits = AccessTools.Method(otType, "ClearUnits");
                    _hasTargetsGetter = AccessTools.PropertyGetter(otType, "HasTargets");
                    var ttType = _addUnits != null && _addUnits.GetParameters().Length == 2 ? _addUnits.GetParameters()[1].ParameterType : null;
                    if (ttType != null && ttType.IsEnum) { try { _targetTypeValue = Enum.Parse(ttType, "Closest"); } catch { try { _targetTypeValue = Enum.Parse(ttType, "First"); } catch { } } }
                    try { _unitListType = typeof(System.Collections.Generic.List<>).MakeGenericType(_unitType!); } catch { }
                }

                _hitmeshType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.Hitmesh");
                if (_hitmeshType != null)
                {
                    _hitmeshOwnerField = AccessTools.Field(_hitmeshType, "owner");
                    _hitmeshColliderField = AccessTools.Field(_hitmeshType, "hitmeshCollider");
                }

                _resolveOk = _gmInstance != null && _gmUnits != null && _overrideFaction != null && _factionPlayer != null;
                Plugin.Log.Info($"[TargetProxy] resolve ok={_resolveOk} unit={_unitType != null} gmInstance={_gmInstance != null} units={_gmUnits != null} setStats={_setStats != null} overrideFaction={_overrideFaction != null} unitSOField={_unitSOField != null} factionPlayer={_factionPlayer}");
                return _resolveOk;
            }
            catch (Exception ex) { Plugin.Log.Warn($"[TargetProxy] resolve exception: {ex.GetType().Name}: {ex.Message}"); return false; }
        }
    }
}
