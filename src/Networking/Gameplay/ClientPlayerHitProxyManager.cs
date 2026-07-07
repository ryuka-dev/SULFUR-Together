using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// FF-1 (CLIENT only, session friendly-fire ON only): maintains a minimal hittable Unit at each remote player
    /// (the host and other clients) so the LOCAL player's real bullets/attacks can land on them. A hit routes through
    /// the existing <c>Unit_ReceiveDamage_Pre</c> proxy mapping (the same registry the host's Plan B ghosts use) into
    /// a <see cref="NetMessageType.PlayerFriendlyFireHit"/> request; the proxy itself never loses health.
    ///
    /// This is a stripped-down mirror of the host ghost recipe (<see cref="RemotePlayerRegistryManager"/>): capsule
    /// collider + kinematic Rigidbody + Unit + Hitmesh on the enemy-hitbox layer, registered via
    /// <c>RemotePlayerTargetProxyManager.RegisterExternalProxyUnit</c> BEFORE activation (so SetupBreakableArmor is
    /// skipped and the damage prefix can map unit→peer). Deliberately WITHOUT the ghost's Player component,
    /// GameManager.Players registration, faction override or camera root — it exists purely to catch hits, not to be
    /// detected by enemy AI, so with FF OFF (no proxies at all) the client behaves exactly as today.
    /// </summary>
    internal sealed class ClientPlayerHitProxyManager
    {
        private sealed class HitProxy
        {
            public GameObject Go = null!;
            public object Unit = null!;
            public float LastUpdatedAt;
        }

        private readonly Dictionary<string, HitProxy> _proxies = new Dictionary<string, HitProxy>();
        private readonly HashSet<string> _seenThisTick = new HashSet<string>();
        private readonly List<string> _removeScratch = new List<string>();

        private static bool LogOn { get { try { return Plugin.Cfg.LogFriendlyFire.Value; } catch { return false; } } }

        // ---- reflection cache (the minimal subset of the ghost recipe) ----
        private static bool _resolveAttempted, _resolveOk;
        private static Type? _unitType;
        private static PropertyInfo? _gmInstance, _gmPlayerUnit, _gmUnits;
        private static MethodInfo? _setStats, _spawn;
        private static FieldInfo? _unitSOField;
        private static Type? _hitmeshType;
        private static FieldInfo? _hitmeshOwnerField, _hitmeshColliderField, _hitmeshHitShapesField;

        /// <summary>Client per-frame. Self-gated: outside Client mode or with the session friendly-fire setting OFF
        /// it tears everything down and returns, so proxies appear/disappear reactively when the host flips the
        /// toggle mid-session.</summary>
        public void Tick(NetRemotePlayerProxyManager visualProxies, float now, float maxAgeSeconds)
        {
            if (NetConfig.GetMode() != NetMode.Client || !NetSessionSettings.FriendlyFireEnabled || visualProxies == null)
            {
                if (_proxies.Count > 0) Clear();
                return;
            }
            if (!EnsureResolved()) return;

            _seenThisTick.Clear();
            visualProxies.ForEachInScenePlayer((peerId, pos) =>
            {
                if (NetPlayerLifeManager.IsPeerDownOrDead(peerId)) return; // downed players are not FF targets
                _seenThisTick.Add(peerId);
                UpdateOrCreate(peerId, pos, now);
            }, now, maxAgeSeconds);

            if (_proxies.Count > 0)
            {
                _removeScratch.Clear();
                foreach (var kv in _proxies) if (!_seenThisTick.Contains(kv.Key)) _removeScratch.Add(kv.Key);
                foreach (var id in _removeScratch) Destroy(id, "player gone/downed");
            }
        }

        public void Clear()
        {
            foreach (var id in new List<string>(_proxies.Keys)) Destroy(id, "clear");
            _proxies.Clear();
        }

        private void UpdateOrCreate(string peerId, Vector3 pos, float now)
        {
            if (_proxies.TryGetValue(peerId, out var p))
            {
                if (p.Go == null) { _proxies.Remove(peerId); }
                else { p.Go.transform.position = pos; p.LastUpdatedAt = now; return; }
            }
            TryCreate(peerId, pos, now);
        }

        private void TryCreate(string peerId, Vector3 pos, float now)
        {
            try
            {
                var gm = _gmInstance!.GetValue(null, null);
                if (gm == null) return; // defer until GameManager exists
                object? localPlayerUnit = _gmPlayerUnit?.GetValue(gm, null);
                object? playerUnitSO = (localPlayerUnit != null && _unitSOField != null) ? _unitSOField.GetValue(localPlayerUnit) : null;

                // Build INACTIVE so Unit.Awake runs only after fields/registration are in place (ghost recipe).
                var go = new GameObject($"SULFURTogether FFHitProxy - {peerId}");
                go.SetActive(false);
                go.transform.position = pos;

                // Same enemy-hitbox layer the host ghosts use — the native weapon hit raycast demonstrably finds it.
                int hitboxLayer = 6; try { hitboxLayer = Plugin.Cfg.RemotePlayerTargetProxyHitboxLayer.Value; } catch { }
                go.layer = hitboxLayer;

                var col = go.AddComponent<CapsuleCollider>();
                col.height = 1.9f; col.radius = 0.4f; col.center = new Vector3(0f, 0.95f, 0f); col.isTrigger = false;
                var rb = go.AddComponent<Rigidbody>();
                rb.isKinematic = true; rb.useGravity = false;

                var unit = go.AddComponent(_unitType!);
                if (_unitSOField != null && playerUnitSO != null) { try { _unitSOField.SetValue(unit, playerUnitSO); } catch { } }
                // BEFORE activation: SetupBreakableArmor skip + the Unit_ReceiveDamage_Pre unit→peer mapping.
                RemotePlayerTargetProxyManager.RegisterExternalProxyUnit(unit, peerId);

                // Hitmesh so a landing hit is attributed to this unit → Unit.ReceiveDamage → the FF client branch.
                if (_hitmeshType != null)
                {
                    try
                    {
                        var hm = go.AddComponent(_hitmeshType);
                        _hitmeshOwnerField?.SetValue(hm, unit);
                        _hitmeshColliderField?.SetValue(hm, col);
                        // Bare Hitmesh: hitShapes must be an empty array, not null (GetPhysicsMaterial NREs — ghost recipe note).
                        try { if (_hitmeshHitShapesField?.FieldType.GetElementType() is Type est) _hitmeshHitShapesField.SetValue(hm, Array.CreateInstance(est, 0)); } catch { }
                    }
                    catch (Exception ex) { Plugin.Log.Warn($"[FF] hit-proxy Hitmesh add failed: {ex.Message}"); }
                }

                go.SetActive(true); // Unit.Awake runs

                if (_setStats != null && playerUnitSO != null) { try { _setStats.Invoke(unit, new[] { playerUnitSO }); } catch (Exception ex) { Plugin.Log.Warn($"[FF] hit-proxy SetStats failed: {ex.Message}"); } }
                if (_spawn != null) { try { _spawn.Invoke(unit, null); } catch (Exception ex) { Plugin.Log.Warn($"[FF] hit-proxy Spawn failed: {ex.Message}"); } }

                _proxies[peerId] = new HitProxy { Go = go, Unit = unit, LastUpdatedAt = now };
                if (LogOn) Plugin.Log.Info($"[FF] hit proxy created peer={peerId} pos={pos:F1} layer={hitboxLayer} unitSO={(playerUnitSO == null ? "null" : "ok")}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[FF] hit-proxy create failed peer={peerId}: {ex.GetType().Name}: {ex.Message}"); }
        }

        private void Destroy(string peerId, string reason)
        {
            if (!_proxies.TryGetValue(peerId, out var p)) return;
            _proxies.Remove(peerId);
            try { RemotePlayerTargetProxyManager.UnregisterExternalProxyUnit(p.Unit); } catch { }
            // Unit.Awake may have self-registered into GameManager.units — remove defensively (target-proxy recipe).
            try
            {
                var gm = _gmInstance?.GetValue(null, null);
                if (gm != null && _gmUnits?.GetValue(gm, null) is IList units) units.Remove(p.Unit);
            }
            catch { }
            try { if (p.Go != null) UnityEngine.Object.Destroy(p.Go); } catch { }
            if (LogOn) Plugin.Log.Info($"[FF] hit proxy destroyed peer={peerId} reason={reason}");
        }

        private static bool EnsureResolved()
        {
            if (_resolveAttempted) return _resolveOk;
            _resolveAttempted = true;
            try
            {
                _unitType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.Units.Unit");
                var gmType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.GameManager");
                if (_unitType == null || gmType == null)
                { Plugin.Log.Warn("[FF] hit-proxy resolve failed: Unit/GameManager type missing"); return false; }

                _gmInstance = AccessTools.Property(gmType, "Instance");
                _gmPlayerUnit = AccessTools.Property(gmType, "PlayerUnit");
                _gmUnits = AccessTools.Property(gmType, "units");

                _setStats = AccessTools.Method(_unitType, "SetStats");
                _spawn = AccessTools.Method(_unitType, "Spawn", Type.EmptyTypes);
                _unitSOField = AccessTools.Field(_unitType, "unitSO");

                _hitmeshType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.Hitmesh");
                if (_hitmeshType != null)
                {
                    _hitmeshOwnerField = AccessTools.Field(_hitmeshType, "owner");
                    _hitmeshColliderField = AccessTools.Field(_hitmeshType, "hitmeshCollider");
                    _hitmeshHitShapesField = AccessTools.Field(_hitmeshType, "hitShapes");
                }

                _resolveOk = _gmInstance != null && _gmPlayerUnit != null;
                if (LogOn) Plugin.Log.Info($"[FF] hit-proxy resolve ok={_resolveOk} hitmesh={_hitmeshType != null}");
                return _resolveOk;
            }
            catch (Exception ex) { Plugin.Log.Warn($"[FF] hit-proxy resolve exception: {ex.GetType().Name}: {ex.Message}"); return false; }
        }
    }
}
