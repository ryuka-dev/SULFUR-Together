using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PerfectRandom.Sulfur.Core;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay.Boss
{
    /// <summary>
    /// F4-MISSILE D2 (GU102-6): the Desert boss's homing missiles, shown on the OTHER players too.
    /// <para>Every end fires its own homing stream at its own local player (<c>DesertMissileBase.SpawnRealRocket</c> reads
    /// <c>GameManager.PlayerUnit</c>), and that missile only damages whoever stands where it lands — so nobody sees the
    /// missiles hunting their teammates. For each real missile this end fires, this spawns one visual-only twin per remote
    /// player, chasing that player's visual proxy.</para>
    /// <para>Game 1.0.2 rebuilt the missile as data instead of a component: a ground marker that tracks the player for 2 s,
    /// then a persistent <c>VFX_Persistent.RPG_DesertClaus</c> effect that falls (its position lives in
    /// <c>VFXPlayManager.positionScaleValues</c>, uploaded in the manager's LateUpdate) and a one-shot explosion when it
    /// lands. A twin replays exactly that with the base's own tuning (horizontal speed, fall speed, start height, ground
    /// raycast) but no damage and no screen shake. Its marker comes from a pool owned here — the base's marker cache is
    /// sized for its own barrage and pops without a check — sharing the base's marker material so it blinks in step.</para>
    /// <para>Ticked from <see cref="NetBossEncounterManager.Tick"/>; cleared on level reset. A twin whose base is gone
    /// releases its effect slot and marker on the next tick. Fail-open: a lookup miss only skips the visuals.</para>
    /// </summary>
    internal static class DesertGhostMissiles
    {
        private const float LockOnSeconds = 2f;   // DesertMissileBase.SpawnRealRocketAtPosition: startTime = now + 2
        private const int MaxLive = 64;            // safety cap; a normal fight keeps a handful alive

        private sealed class Ghost
        {
            public Component Base = null!;
            public Transform? Target;               // the remote player's visual proxy (may vanish: keep last position)
            public Transform Marker = null!;
            public GameObject MarkerPrefab = null!;
            public Vector3 Position;                // missile x/z follow the marker; y is the start height
            public Vector3 LastTarget;
            public float GroundY;
            public float LaunchAt;
            public float HorizontalSpeed;
            public float FallSpeed;
            public bool Launched;
            public short VfxId;
        }

        private static readonly List<Ghost> _live = new List<Ghost>();
        private static readonly Dictionary<int, Stack<Transform>> _markerPool = new Dictionary<int, Stack<Transform>>();
        private static readonly System.Random _rng = new System.Random(); // never the game's shared RNG (D3b reseeds that)
        private static readonly int RpgSlot = (int)VFX_Persistent.RPG_DesertClaus - 1; // VFXPlayManager indexes effect - 1

        private static MethodInfo? _groundHit;
        private static MethodInfo? _soundPlayAtPosition;
        private static bool _resolved;
        private static float _logNext;

        public static int LiveCount => _live.Count;

        /// <summary>BOTH ends: <paramref name="missileBase"/> just fired a real homing missile at the local player. Spawn a
        /// visual twin for every remote player this end can see.</summary>
        public static void SpawnForRemotePlayers(Component missileBase)
        {
            try
            {
                if (missileBase == null || !NetGameplaySyncBridge.IsSessionActive) return;
                if (!(BossReflect.GetMember(missileBase, "missileMarkerPrefab") is GameObject markerPrefab) || markerPrefab == null) return;
                Resolve(missileBase.GetType());

                float horizontal = ReadFloat(missileBase, "missileHorizontalSpeed", 0f);
                float fall = ReadFloat(missileBase, "missileFallSpeed", 0f);
                if (fall <= 0f) return; // nothing would ever land
                float startOffset = ReadFloat(missileBase, "missileStartYOffset", 30f);
                float radius = ReadFloat(missileBase, "missileStartRandomRadius", 10f);
                bool raycast = BossReflect.GetMember(missileBase, "raycastGround") is bool rg && rg;
                float fixedGround = ReadFloat(missileBase, "yPosWorldToHit", -5.2f);
                var material = BossReflect.GetMember(missileBase, "missileMarkerMaterialInstance") as Material;

                int spawned = 0, others = 0;
                NetGameplaySyncBridge.ForEachRemotePlayerTransform((peerId, proxy) =>
                {
                    others++;
                    if (proxy == null || _live.Count >= MaxLive) return;
                    double angle = _rng.NextDouble() * Math.PI * 2.0, dist = _rng.NextDouble() * radius;
                    Vector3 start = proxy.position + new Vector3((float)(Math.Cos(angle) * dist), startOffset, (float)(Math.Sin(angle) * dist));
                    float ground = raycast ? GroundHit(missileBase, start, fixedGround) : fixedGround;

                    Transform? marker = TakeMarker(markerPrefab, material);
                    if (marker == null) return;
                    marker.localPosition = new Vector3(start.x, ground, start.z);
                    marker.localRotation = Quaternion.identity;

                    _live.Add(new Ghost
                    {
                        Base = missileBase, Target = proxy, Marker = marker, MarkerPrefab = markerPrefab,
                        Position = start, LastTarget = proxy.position, GroundY = ground,
                        LaunchAt = Time.time + LockOnSeconds, HorizontalSpeed = horizontal, FallSpeed = fall,
                    });
                    spawned++;
                });

                if (Plugin.Cfg.LogBossPreFight.Value && Time.realtimeSinceStartup >= _logNext)
                { _logNext = Time.realtimeSinceStartup + 1f; Plugin.Log.Info($"[DesertMissile] ghost missiles: proxies={others} spawned={spawned} live={_live.Count}"); }
            }
            catch (Exception ex) { Plugin.Log.Warn($"[DesertMissile] ghost missile spawn failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>Per frame (main thread, before the VFX manager's LateUpdate upload): advance every twin.</summary>
        public static void Tick()
        {
            if (_live.Count == 0) return;
            try
            {
                VFXPlayManager vfx = StaticInstance<VFXPlayManager>.Instance;
                float now = Time.time, dt = Time.deltaTime;
                for (int i = _live.Count - 1; i >= 0; i--)
                {
                    Ghost g = _live[i];
                    if (g.Base == null || g.Marker == null || vfx == null) { Release(i, vfx, explode: false); continue; }
                    if (g.Target != null) g.LastTarget = g.Target.position;

                    if (!g.Launched)
                    {
                        if (now < g.LaunchAt)
                        {
                            // Native lock-on: the marker slides toward the player, the missile's x/z follow the marker.
                            Vector3 toward = g.LastTarget - g.Marker.localPosition;
                            toward = new Vector3(toward.x, 0f, toward.z).normalized;
                            g.Marker.Translate(g.HorizontalSpeed * dt * toward);
                            g.Position = new Vector3(g.Marker.position.x, g.Position.y, g.Marker.position.z);
                            continue;
                        }
                        if (!vfx.TryReservePersistentEffect(VFX_Persistent.RPG_DesertClaus, out g.VfxId)) { Release(i, vfx, explode: false); continue; }
                        g.Launched = true;
                        vfx.StartPersistentEffect(VFX_Persistent.RPG_DesertClaus, g.Position, Vector3.down, 1f, g.VfxId);
                        PlaySound(g.Base, "missileSingleChargeSound", g.Marker.localPosition);
                        continue;
                    }

                    ref NativeArray<float4> positions = ref vfx.positionScaleValues.ElementAt(RpgSlot);
                    float4 value = positions[g.VfxId];
                    value.xyz += g.FallSpeed * dt * math.down();
                    if (value.y > g.GroundY) { positions[g.VfxId] = value; continue; }

                    value.y = g.GroundY;
                    positions[g.VfxId] = value;
                    g.Position = value.xyz;
                    Release(i, vfx, explode: true);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[DesertMissile] ghost missile tick failed: {ex.GetType().Name}: {ex.Message} — dropping {_live.Count} ghost(s)");
                Clear();
            }
        }

        /// <summary>Drop every twin. A twin whose base still exists is in the current level, so its effect slot is still
        /// live and is stopped (otherwise it would hang in the sky until the next level, e.g. when the session ends
        /// mid-fight); after a level change the VFX manager has rebuilt its slots, so a dead base's slot is only dropped.
        /// Markers are destroyed.</summary>
        public static void Clear()
        {
            VFXPlayManager? vfx = null;
            try { vfx = StaticInstance<VFXPlayManager>.Instance; } catch { }
            foreach (var g in _live)
            {
                try { if (g.Launched && g.Base != null && vfx != null) vfx.StopPersistentEffect(VFX_Persistent.RPG_DesertClaus, g.VfxId); } catch { }
                DestroyMarker(g.Marker);
            }
            _live.Clear();
            foreach (var stack in _markerPool.Values) while (stack.Count > 0) DestroyMarker(stack.Pop());
            _markerPool.Clear();
        }

        // ------------------------------------------------------------------ helpers

        private static void Release(int index, VFXPlayManager? vfx, bool explode)
        {
            Ghost g = _live[index];
            _live.RemoveAt(index);
            try
            {
                if (g.Launched && vfx != null)
                {
                    if (explode)
                    {
                        vfx.PlayOneShotEffect(VFX_OneShot.ExplosionDesertClauseRocket, g.Position, Vector3.up, 1f + (float)_rng.NextDouble() * 0.2f);
                        if (g.Base != null) PlaySound(g.Base, "missileSingleExplosion", g.Position);
                    }
                    vfx.StopPersistentEffect(VFX_Persistent.RPG_DesertClaus, g.VfxId);
                }
            }
            catch { }
            ReturnMarker(g);
        }

        private static Transform? TakeMarker(GameObject prefab, Material? material)
        {
            int key = prefab.GetInstanceID();
            if (_markerPool.TryGetValue(key, out var stack))
                while (stack.Count > 0)
                {
                    var pooled = stack.Pop();
                    if (pooled == null) continue;
                    // The pool is per prefab, but the material is per base (each base owns its blinking instance).
                    if (material != null && pooled.GetComponent<MeshRenderer>() is MeshRenderer pr) pr.sharedMaterial = material;
                    pooled.gameObject.SetActive(true);
                    return pooled;
                }

            var root = StaticInstance<GameManager>.Instance?.effectRoot;
            if (root == null) return null;
            GameObject go = UnityEngine.Object.Instantiate(prefab, root);
            go.name = "CoopGhostMissileMarker";
            if (material != null && go.GetComponent<MeshRenderer>() is MeshRenderer mr) mr.sharedMaterial = material; // blink with the base
            go.SetActive(true);
            return go.transform;
        }

        private static void ReturnMarker(Ghost g)
        {
            if (g.Marker == null) return;
            g.Marker.gameObject.SetActive(false);
            int key = g.MarkerPrefab != null ? g.MarkerPrefab.GetInstanceID() : 0;
            if (!_markerPool.TryGetValue(key, out var stack)) _markerPool[key] = stack = new Stack<Transform>();
            stack.Push(g.Marker);
        }

        private static void DestroyMarker(Transform? marker)
        {
            try { if (marker != null) UnityEngine.Object.Destroy(marker.gameObject); } catch { }
        }

        private static float GroundHit(Component missileBase, Vector3 position, float fallback)
        {
            try { if (_groundHit?.Invoke(missileBase, new object[] { position }) is float y) return y; } catch { }
            return fallback;
        }

        private static void PlaySound(Component missileBase, string field, Vector3 position)
        {
            try
            {
                object? sound = BossReflect.GetMember(missileBase, field);
                if (sound == null) return;
                _soundPlayAtPosition ??= AccessTools.Method(sound.GetType(), "PlayAtPosition", new[] { typeof(Transform), typeof(Vector3) });
                _soundPlayAtPosition?.Invoke(sound, new object[] { missileBase.transform, position });
            }
            catch { }
        }

        private static void Resolve(Type baseType)
        {
            if (_resolved) return;
            _resolved = true;
            _groundHit = AccessTools.Method(baseType, "GetGroundHit", new[] { typeof(Vector3) });
            if (_groundHit == null) Plugin.Log.Warn("[DesertMissile] DesertMissileBase.GetGroundHit(Vector3) not found — ghost missiles land at the fixed height.");
        }

        private static float ReadFloat(object obj, string member, float fallback)
            => BossReflect.GetMember(obj, member) is float f ? f : fallback;
    }
}
