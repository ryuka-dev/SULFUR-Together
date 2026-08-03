using System;
using System.Reflection;
using HarmonyLib;
using PerfectRandom.Sulfur.Core;
using SULFURTogether.Networking.Gameplay;
using UnityEngine;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// Phase BGC — Black Guild Cardinal alcove fight. The three vanilla decisions that are made locally, and therefore
    /// diverge in co-op, are captured here at their own methods; the authority lives in
    /// <see cref="CardinalFightSyncManager"/>. Every game type is reflection-resolved: they live in
    /// <c>PerfectRandom.Sulfur.Gameplay</c>, which this project does not reference.
    ///
    /// <list type="bullet">
    ///   <item><b>BGC-1 — <c>CardinalFightHelper.Start</c>.</b> Vanilla destroys <c>5 - difficulty</c> cardinals chosen
    ///   with the GLOBAL <c>UnityEngine.Random</c>, so two peers on the same seed keep DIFFERENT cardinals. The count
    ///   matches (it is pure difficulty), the identities do not — and the roster binds by
    ///   <c>chapter|level|seed|unitId|idx|position</c>, so each end's unmatched cardinal becomes a client-only
    ///   quarantined statue on one side and an invisible-but-shooting enemy on the other (the GH-1 / issue #6 shape).
    ///   Fix: seed the RNG from the level seed for exactly that one call and restore the state after — the same scoped
    ///   reseed as <see cref="CryptChallengePatches"/> and <see cref="GhostSpawnPatches"/>, which does not depend on
    ///   the two ends having issued the same number of global-RNG draws beforehand. Everything downstream (the
    ///   teleport indices) then rides plain list indices.</item>
    ///   <item><b>BGC-2 — <c>CardinalFightHelper.StartFight</c>.</b> No code caller: it is invoked from scene data, so
    ///   it runs on whichever end's player crossed the trigger. It is also the step that lifts every cardinal's
    ///   invulnerability (<c>StartFightRoutine</c> → each <c>CardinalHelper.StartFight</c>), so an end that never runs
    ///   it keeps a room of inert, unkillable cardinals and the other end's client hits are rejected host-side.
    ///   Host-authoritative: a linked client blocks + requests, the host runs the real chain and commits.</item>
    ///   <item><b>BGC-3 — <c>CardinalHelper.StartTeleport</c> / <c>ExecuteTeleport</c>.</b> The destination comes from
    ///   <c>getUnoccupiedSpawn()</c>, another global-RNG roll. The host broadcasts what it picked and the client
    ///   replays the real methods with that alcove forced.</item>
    ///   <item><b>BGC-4 — <c>CardinalFightHelper.getUnoccupiedSpawn</c> postfix.</b> Vanilla reserves only the alcove
    ///   nearest the LOCAL player, which in co-op leaves every remote player a valid landing spot for a native
    ///   <c>Unit.TeleportTo</c> — a kinematic collider materialising inside a player is the PK-3 fling.</item>
    /// </list>
    /// </summary>
    internal static class CardinalPatches
    {
        private const int SeedSalt = 0x43617264; // "Card"

        public static int CullsSeeded;
        public static int CullsSkippedNoSeed;

        private const BindingFlags Bf = BindingFlags.Static | BindingFlags.NonPublic;

        public static void Apply(Harmony harmony)
        {
            try
            {
                if (!CardinalFightSyncManager.EnsureTypes()) return;
                var helperType   = CardinalFightSyncManager.FightHelperType;
                var cardinalType = CardinalFightSyncManager.CardinalType;
                if (helperType == null || cardinalType == null) return;

                // BGC-1
                Hook(harmony, helperType, "Start", nameof(Helper_Start_Pre), nameof(Helper_Start_Post));
                // BGC-2
                Hook(harmony, helperType, "StartFight", nameof(Helper_StartFight_Pre), nameof(Helper_StartFight_Post));
                // BGC-3 destination override + BGC-4 player-safety veto (one method, two concerns)
                Hook(harmony, helperType, "getUnoccupiedSpawn", nameof(Helper_GetSpawn_Pre), nameof(Helper_GetSpawn_Post));
                // BGC-3
                Hook(harmony, cardinalType, "StartTeleport",   nameof(Cardinal_StartTeleport_Pre),   nameof(Cardinal_StartTeleport_Post));
                Hook(harmony, cardinalType, "ExecuteTeleport", nameof(Cardinal_ExecuteTeleport_Pre), nameof(Cardinal_ExecuteTeleport_Post));

                Plugin.Log.Info("[Cardinal] Patched CardinalFightHelper/CardinalHelper (Black Guild Cardinal fight sync).");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[Cardinal] Apply failed: {ex.Message}");
            }
        }

        private static void Hook(Harmony harmony, Type type, string method, string? prefix, string? postfix)
        {
            try
            {
                var mi = AccessTools.DeclaredMethod(type, method);
                if (mi == null)
                {
                    Plugin.Log.Warn($"[Cardinal] {type.Name}.{method} not found — that part of the cardinal sync is disabled.");
                    return;
                }
                harmony.Patch(mi,
                    prefix:  prefix  == null ? null : new HarmonyMethod(typeof(CardinalPatches).GetMethod(prefix,  Bf)),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(CardinalPatches).GetMethod(postfix, Bf)));
            }
            catch (Exception ex) { Plugin.Log.Error($"[Cardinal] {type.Name}.{method} patch failed: {ex.Message}"); }
        }

        // ───────────────────────────────────────────── BGC-1: make the cull a function of the level seed

        private static void Helper_Start_Pre(object __instance, ref UnityEngine.Random.State? __state)
        {
            __state = null;
            try
            {
                if (!Plugin.Cfg.EnableCardinalSync.Value) return;

                long seed = 0;
                var gm = StaticInstance<GameManager>.Instance;
                if (gm != null) seed = gm.currentSeed;
                if (seed == 0)
                {
                    // No level seed to derive from — leave vanilla behaviour rather than inventing one.
                    CullsSkippedNoSeed++;
                    Plugin.Log.Warn("[Cardinal] no level seed available; the cardinal cull is left to the global RNG (the two ends may keep different cardinals)");
                    return;
                }

                int derived = unchecked((int)seed ^ SeedSalt ^ PositionHash(__instance));
                __state = UnityEngine.Random.state;
                UnityEngine.Random.InitState(derived);
                CullsSeeded++;
                Plugin.Log.Info($"[Cardinal] seeding the cardinal cull from level seed={seed} derived={derived}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Cardinal] cull seed failed: {ex.Message}"); }
        }

        private static void Helper_Start_Post(UnityEngine.Random.State? __state)
        {
            try { if (__state.HasValue) UnityEngine.Random.state = __state.Value; }
            catch (Exception ex) { Plugin.Log.Warn($"[Cardinal] RNG state restore failed: {ex.Message}"); }
        }

        // ───────────────────────────────────────────── BGC-2: host-authoritative fight start

        private static bool Helper_StartFight_Pre(object __instance)
            => !CardinalFightSyncManager.ShouldBlockLocalFightStart(__instance);

        private static void Helper_StartFight_Post(object __instance, bool __runOriginal)
        {
            if (!__runOriginal) return;
            CardinalFightSyncManager.OnLocalFightStarted(__instance);
        }

        // ───────────────────────────────────────────── BGC-3/4: destination authority + player safety

        // Skips vanilla's roll entirely while a host-authored ExecuteTeleport is being replayed. `ref object __result`
        // on a reflection-resolved return type is the same shape as the proven AiAgent.GetTarget prefixes in
        // ReverseProbePatches.
        private static bool Helper_GetSpawn_Pre(ref object __result)
        {
            if (!CardinalFightSyncManager.IsApplyingMirror) return true;
            if (!CardinalFightSyncManager.TryTakeForcedSpawn(out var forced)) return true;
            __result = forced!;
            return false;
        }

        private static void Helper_GetSpawn_Post(object __instance, ref object __result, bool __runOriginal)
        {
            if (!__runOriginal) return;
            CardinalFightSyncManager.VetoSpawnNearPlayers(__instance, ref __result);
        }

        private static bool Cardinal_StartTeleport_Pre(object __instance)
            => !CardinalFightSyncManager.ShouldBlockLocalTeleport(__instance);

        private static void Cardinal_StartTeleport_Post(object __instance, bool __runOriginal)
        {
            if (!__runOriginal) return;
            CardinalFightSyncManager.OnHostTeleportBegin(__instance);
        }

        private static bool Cardinal_ExecuteTeleport_Pre(object __instance)
            => !CardinalFightSyncManager.ShouldBlockLocalTeleport(__instance);

        private static void Cardinal_ExecuteTeleport_Post(object __instance, bool __runOriginal)
        {
            if (!__runOriginal) return;
            CardinalFightSyncManager.OnHostTeleportExecuted(__instance);
        }

        // Rounded world position → a stable per-room salt, so two cardinal rooms in one level do not cull identically.
        // Deterministic generation places the room at the same spot on both ends; rounding to the metre absorbs drift.
        private static int PositionHash(object instance)
        {
            try
            {
                if (!(instance is Component c) || c == null) return 0;
                Vector3 p = c.transform.position;
                int x = Mathf.RoundToInt(p.x), y = Mathf.RoundToInt(p.y), z = Mathf.RoundToInt(p.z);
                return unchecked((x * 73856093) ^ (y * 19349663) ^ (z * 83492791));
            }
            catch { return 0; }
        }

        public static string FormatCounters()
            => $"cardinalCullSeeded={CullsSeeded} skippedNoSeed={CullsSkippedNoSeed}";
    }
}
