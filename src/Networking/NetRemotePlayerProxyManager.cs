using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SULFURTogether.Networking
{
    /// <summary>
    /// Owns local-only visual proxy GameObjects. Does not create or touch gameplay Player/Unit objects.
    /// </summary>
    public sealed class NetRemotePlayerProxyManager
    {
        private readonly Dictionary<string, NetRemotePlayerProxy> _proxies = new Dictionary<string, NetRemotePlayerProxy>();

        public int ProxyCount => _proxies.Count;
        public int VisibleCount => _proxies.Values.Count(p => p.IsVisible);

        public void Clear()
        {
            foreach (var proxy in _proxies.Values)
                proxy.Destroy();
            _proxies.Clear();
        }

        public void HideAll()
        {
            foreach (var proxy in _proxies.Values)
                proxy.Hide();
        }

        public void Remove(string peerId)
        {
            if (string.IsNullOrWhiteSpace(peerId)) return;
            if (_proxies.TryGetValue(peerId, out var proxy))
                proxy.Destroy();
            _proxies.Remove(peerId);
        }

        // Diagnostic throttles (Log196/197: client can't see host on first join — pin down hidden vs frozen vs
        // co-located). Gated behind EnableDebugLog so it is silent in normal play but available on demand.
        private float _nextHideDiagAt;
        private float _nextProxyDiagAt;
        private static bool DiagOn { get { try { return Plugin.Cfg.LogRemotePlayerBody.Value; } catch { return false; } } }

        public void Apply(NetPlayerTransformState state, NetRunState localRunState, float now, bool requireSeedMatch)
        {
            if (string.IsNullOrWhiteSpace(state.PeerId)) return;
            if (!CanDisplayInLocalScene(state, localRunState, requireSeedMatch))
            {
                if (_proxies.TryGetValue(state.PeerId, out var existing))
                    existing.Hide();
                // Diagnostic: a remote transform DID arrive but can't be shown (different scene/seed) — log the
                // comparison even when no proxy exists yet, so "transform arrives but proxy never created" is
                // visible (Log198: client joined but never showed host). Throttled; gated behind LogRemotePlayerBody.
                if (DiagOn && now >= _nextHideDiagAt)
                {
                    _nextHideDiagAt = now + 2f;
                    NetLogger.Info($"[RemoteProxyDiag] REJECTED peer={state.PeerId} hadProxy={(existing != null)}: remote {state.ChapterName}:{state.LevelIndex} seed={(state.HasLevelSeed ? state.LevelSeed.ToString() : "?")} vs local {localRunState.ChapterName}:{localRunState.LevelIndex} seed={(localRunState.HasLevelSeed ? localRunState.LevelSeed.ToString() : "?")} requireSeedMatch={requireSeedMatch}");
                }
                return;
            }

            if (!_proxies.TryGetValue(state.PeerId, out var proxy))
            {
                proxy = new NetRemotePlayerProxy(state.PeerId);
                _proxies[state.PeerId] = proxy;
                NetLogger.Info($"[RemotePlayer] Visual proxy created for {state.PeerId} at {state.Position} scene={state.ChapterName}:{state.LevelIndex} seed={(state.HasLevelSeed ? state.LevelSeed.ToString() : "?")}");
            }

            proxy.Apply(state, now);
        }

        public void Tick(float deltaTime, float now, float timeoutSeconds, float interpolationSpeed, float snapDistance)
        {
            foreach (var proxy in _proxies.Values.ToArray())
                proxy.Tick(deltaTime, now, timeoutSeconds, interpolationSpeed, snapDistance);

            // Diagnostic (Log196/197): every 2s, dump each proxy's visibility / position / staleness so we can
            // tell whether an existing-but-unseen proxy is hidden, frozen (host stopped sending → age grows), or
            // just far from / co-located with the local player. Gated behind EnableDebugLog (off in normal play).
            if (DiagOn && _proxies.Count > 0 && now >= _nextProxyDiagAt)
            {
                _nextProxyDiagAt = now + 2f;
                foreach (var kv in _proxies)
                {
                    var p = kv.Value;
                    NetLogger.Info($"[RemoteProxyDiag] peer={kv.Key} visible={p.IsVisible} pos={p.TargetPosition} ageSinceUpdate={(now - p.LastUpdatedAt):0.0}s");
                }
            }
        }

        public string FormatStatus()
        {
            return $"remoteVisuals={VisibleCount}/{ProxyCount}";
        }

        /// <summary>Soft player-vs-player collision: nudge the LOCAL player horizontally out of overlap with each visible
        /// remote proxy (capped per-frame so it feels soft, not a hard wall). Each machine runs this on its own player,
        /// so when one walks into the other, the other is squeezed out on their machine — mutual, no fling, no head-stand.</summary>
        // Below this overlap (meters) we don't push — a deadzone that stops micro-jitter at the contact boundary.
        private const float SoftCollisionDeadzone = 0.03f;

        public void ApplySoftCollision(Transform localPlayer, float radius, float maxPushSpeed, float deltaTime)
        {
            if (localPlayer == null || radius <= 0f || deltaTime <= 0f) return;
            // Move via the RIGIDBODY, not the transform — the player is a Rigidbody-based CMF controller and writing
            // transform.position desyncs it from the physics body (that was the twitching). MovePosition is interpolated.
            var rb = localPlayer.GetComponentInParent<Rigidbody>();
            Vector3 p = rb != null ? rb.position : localPlayer.position;
            float maxStep = maxPushSpeed * deltaTime;
            float pushX = 0f, pushZ = 0f;
            foreach (var proxy in _proxies.Values)
            {
                if (proxy == null || !proxy.IsVisible) continue;
                Vector3 c = proxy.VisualPosition;
                float dx = p.x - c.x;
                float dz = p.z - c.z;
                float distSq = dx * dx + dz * dz;
                if (distSq >= radius * radius) continue; // not overlapping
                float dist = Mathf.Sqrt(distSq);
                float overlap = radius - dist;
                if (overlap <= SoftCollisionDeadzone) continue; // ignore tiny overlaps -> no jitter
                float step = Mathf.Min(overlap - SoftCollisionDeadzone, maxStep);
                if (dist > 1e-4f)
                {
                    pushX += dx / dist * step;
                    pushZ += dz / dist * step;
                }
                else
                {
                    pushX += step; // centers coincide — deterministic horizontal nudge
                }
            }
            if (pushX == 0f && pushZ == 0f) return;
            Vector3 target = new Vector3(p.x + pushX, p.y, p.z + pushZ);
            if (rb != null) rb.MovePosition(target);
            else localPlayer.position = target;
        }

        /// <summary>Phase 5.5-P1: collect the world positions of remote players currently shown in the local scene
        /// (recent, same-scene). On the Host these are the clients — used as additional interest sources so enemies a
        /// client is fighting (far from the Host player) still get full-rate snapshots instead of being throttled.</summary>
        public void CollectInterestPositions(List<Vector3> into, float now, float maxAgeSeconds)
        {
            if (into == null) return;
            foreach (var proxy in _proxies.Values)
            {
                if (!proxy.IsVisible) continue;
                if (maxAgeSeconds > 0f && now - proxy.LastUpdatedAt > maxAgeSeconds) continue;
                into.Add(proxy.TargetPosition);
            }
        }

        /// <summary>Phase 5.5-P3-A2: invoke <paramref name="action"/> with (peerId, worldPosition) for each remote player
        /// currently shown in the local scene (recent, same-scene). Used to maintain enemy-targetable proxy Units (Host).</summary>
        public void ForEachInScenePlayer(System.Action<string, Vector3> action, float now, float maxAgeSeconds)
        {
            if (action == null) return;
            foreach (var kv in _proxies)
            {
                var proxy = kv.Value;
                if (!proxy.IsVisible) continue;
                if (maxAgeSeconds > 0f && now - proxy.LastUpdatedAt > maxAgeSeconds) continue;
                action(kv.Key, proxy.TargetPosition);
            }
        }

        /// <summary>Phase 5.6-WS-2: iterate (peerId, proxy) for every known proxy (visible or not) so the held-weapon
        /// sync can attach/update/clear weapon models. Snapshotted to tolerate mutation during iteration.</summary>
        internal void ForEachProxy(System.Action<string, NetRemotePlayerProxy> action)
        {
            if (action == null) return;
            foreach (var kv in _proxies.ToArray())
                action(kv.Key, kv.Value);
        }

        private static bool CanDisplayInLocalScene(NetPlayerTransformState state, NetRunState localRunState, bool requireSeedMatch)
        {
            if (!state.HasScene || !localRunState.HasLevel) return false;
            if (!NetSceneName.SameScene(state.ChapterName, state.LevelIndex, localRunState.ChapterName, localRunState.LevelIndex))
                return false;

            if (!requireSeedMatch) return true;

            // When seed authority is enabled, unknown seed is not enough to prove both peers
            // are in the same generated level instance. Keep the visual marker hidden until
            // both sides report a concrete matching levelSeed.
            if (!state.HasLevelSeed || !localRunState.HasLevelSeed) return false;
            return state.LevelSeed == localRunState.LevelSeed;
        }
    }
}
