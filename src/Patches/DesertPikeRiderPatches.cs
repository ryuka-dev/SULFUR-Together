using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Units;
using UnityEngine;
using SULFURTogether.Networking;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// PK-6: the riders of a desert level pike are the same on every machine.
    ///
    /// <para><c>DesertPikeCarrier.OnSpawn</c> (run from <c>Start</c> for a level-spawned pike) picks each seat's rider
    /// with the global <c>UnityEngine.Random.Range</c>, and <c>AttachUnit</c> picks each rider's seat the same way. Every
    /// machine rolls its own, so the same pike carried a Digga and a Shoota on the host and two Diggas on the client
    /// (round 665): the host's Shoota never existed on the client (invisible there, killable by the host), and the
    /// client's second Digga had no host counterpart (local AI, followed the client, unkillable).</para>
    ///
    /// <para>The prefix replaces <c>OnSpawn</c> with the same steps, but draws the rider types from a generator seeded
    /// with the level seed and the pike's spawn position, and seeds the global generator around the synchronous
    /// <c>AttachUnit</c> call (restored afterwards). The level is generated from the same seed on every machine, so the
    /// pike stands at the same spot and the result is identical without a message. This applies outside a session as
    /// well: a host who started the level alone must have rolled the same riders a joining client will roll. The visible
    /// effect for a solo player is only that a given level always seats the same riders.</para>
    ///
    /// <para>The seed is the game's own <c>GameManager.currentSeed</c>, assigned right before the level graph runs (so
    /// before any pike's <c>Start</c>) and read without a network session — the run-state seed is only tracked once a
    /// session exists. Falls back to the vanilla method when the seed reads 0 or a game member is missing.</para>
    /// </summary>
    // Executes on: All
    internal static class DesertPikeRiderPatches
    {
        private static FieldInfo? _isLevelSpawning;   // bool
        private static FieldInfo? _spawningEnemies;   // UnitId[]
        private static FieldInfo? _pointsToMount;     // GameObject[]
        private static FieldInfo? _owner;             // Npc
        private static FieldInfo? _undergroundDepth;  // float jumpUndergroundDepth
        private static MethodInfo? _attachUnit;       // AttachUnit(List<Unit>)
        private static bool _warnedNoSeed;

        public static void Apply(Harmony harmony)
        {
            try
            {
                if (!Plugin.Cfg.EnableSeededPikeRiders.Value)
                {
                    Plugin.Log.Info("[PikeRiders] seeded pike riders disabled by config.");
                    return;
                }
                var t = AccessTools.TypeByName("PerfectRandom.Sulfur.Gameplay.DesertPikeCarrier");
                var onSpawn = t == null ? null : AccessTools.DeclaredMethod(t, "OnSpawn", Type.EmptyTypes);
                _isLevelSpawning  = t == null ? null : AccessTools.Field(t, "isLevelSpawning");
                _spawningEnemies  = t == null ? null : AccessTools.Field(t, "spawningEnemies");
                _pointsToMount    = t == null ? null : AccessTools.Field(t, "pointsToMount");
                _owner            = t == null ? null : AccessTools.Field(t, "owner");
                _undergroundDepth = t == null ? null : AccessTools.Field(t, "jumpUndergroundDepth");
                _attachUnit       = t == null ? null : AccessTools.DeclaredMethod(t, "AttachUnit", new[] { typeof(List<Unit>) });
                if (onSpawn == null || _isLevelSpawning == null || _spawningEnemies == null || _pointsToMount == null
                    || _owner == null || _undergroundDepth == null || _attachUnit == null
                    || _spawningEnemies.FieldType != typeof(UnitId[]) || _owner.FieldType != typeof(Npc)
                    || _undergroundDepth.FieldType != typeof(float))
                {
                    Plugin.Log.Warn("[PikeRiders] DesertPikeCarrier.OnSpawn or one of its members not found — pike riders are rolled per machine and can differ between players");
                    return;
                }
                harmony.Patch(onSpawn, prefix: new HarmonyMethod(
                    typeof(DesertPikeRiderPatches).GetMethod(nameof(OnSpawn_Pre), BindingFlags.Static | BindingFlags.NonPublic)));
                Plugin.Log.Info("[PikeRiders] Patched DesertPikeCarrier.OnSpawn (riders seeded from the level).");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[PikeRiders] Apply failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        // Returns false when the seeded spawn took over; true runs the vanilla method.
        private static bool OnSpawn_Pre(MonoBehaviour __instance)
        {
            try
            {
                if (__instance == null) return true;
                if (!(_isLevelSpawning!.GetValue(__instance) is bool levelSpawning) || !levelSpawning) return true; // vanilla returns at once
                if (!(_owner!.GetValue(__instance) is Npc owner) || owner == null) return true;
                if (!(_spawningEnemies!.GetValue(__instance) is UnitId[] types) || types.Length == 0) return true;
                if (!(_pointsToMount!.GetValue(__instance) is GameObject[] seats) || seats.Length == 0) return true;
                if (!TryGetLevelSeed(out int levelSeed))
                {
                    if (!_warnedNoSeed)
                    {
                        _warnedNoSeed = true;
                        Plugin.Log.Warn("[PikeRiders] level seed unknown when a pike spawned its riders — vanilla roll used (riders may differ between players)");
                    }
                    return true;
                }

                int seed = MixSeed(levelSeed, owner.transform.position);
                var rng = new System.Random(seed);
                var picks = new UnitId[seats.Length];
                for (int i = 0; i < picks.Length; i++) picks[i] = types[rng.Next(types.Length)];

                SpawnRiders(__instance, owner, picks, seed);
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[PikeRiders] OnSpawn prefix failed, vanilla roll used: {ex.GetType().Name}: {ex.Message}");
                return true;
            }
        }

        /// <summary>The vanilla <c>OnSpawn</c> body, step for step, with the seat types decided up front.</summary>
        private static async void SpawnRiders(MonoBehaviour carrier, Npc owner, UnitId[] picks, int seed)
        {
            try
            {
                owner.SetInvulnerable(true);
                owner.SetHitboxesInvulnerable(true);
                float depth = (float)_undergroundDepth!.GetValue(carrier);
                Vector3 p = owner.transform.position;
                owner.transform.position = new Vector3(p.x, p.y - depth, p.z);

                var spawned = new List<Unit>(picks.Length);
                foreach (var id in picks)
                {
                    Unit unit = await id.GetAsset().SpawnUnitAsync(carrier, carrier.transform.position);
                    if (unit == null) continue;
                    unit.SetInvulnerable(true);
                    unit.SetHitboxesInvulnerable(true);
                    spawned.Add(unit);
                    if (unit is Npc npc) GameManager.Instance.aliveNpcs.Remove(npc);
                }
                if (carrier == null) return; // level torn down while the riders loaded

                // AttachUnit draws each rider's seat from the global generator; it is synchronous, so seed it for this
                // call only and put the global state back.
                var saved = UnityEngine.Random.state;
                UnityEngine.Random.InitState(unchecked(seed * 31 + 7));
                try { _attachUnit!.Invoke(carrier, new object[] { spawned }); }
                finally { UnityEngine.Random.state = saved; }

                NetLogger.Debug($"[PikeRiders] pike at {owner.transform.position:F1} seated {string.Join("+", Array.ConvertAll(picks, x => x.GetAsset()?.name ?? "?"))} seed={seed}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[PikeRiders] seeded rider spawn failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static bool TryGetLevelSeed(out int seed)
            => NetLevelSeed.TryReadCurrentSeed(GameManager.Instance, out seed, out _);

        /// <summary>Level seed and the pike's spawn position (quarter-metre grid, so float noise cannot split peers).</summary>
        private static int MixSeed(int levelSeed, Vector3 pos)
        {
            unchecked
            {
                int h = levelSeed;
                h = h * 486187739 + Mathf.RoundToInt(pos.x * 4f);
                h = h * 486187739 + Mathf.RoundToInt(pos.y * 4f);
                h = h * 486187739 + Mathf.RoundToInt(pos.z * 4f);
                return h;
            }
        }
    }
}
