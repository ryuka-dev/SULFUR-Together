using System;
using System.Reflection;
using HarmonyLib;
using PerfectRandom.Sulfur.Core;
using UnityEngine;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// CS (crypt sync phase 2): make the desert crypt's CHALLENGE SELECTION follow the level seed.
    ///
    /// <para><c>CryptChallengeManager.Awake</c> picks which challenge runs via
    /// <c>challengeComponents[UnityEngine.Random.Range(0, count)]</c> on the GLOBAL <c>UnityEngine.Random</c>, then
    /// <c>SetActive</c>s only the chosen child. Two peers generating the same seed therefore agree the crypt exists but
    /// disagree on its task — one hunts chickens while the other kills enemies (Log521:
    /// <c>firstDivergence host=CorruptedAmalgamation client=CryptTurtle</c>, <c>mismatch=71</c>). Same defect shape as
    /// GH-1's ghost species.</para>
    ///
    /// <para><b>Fix.</b> Seed the global RNG from the level seed (salted, and mixed with the crypt's rounded world
    /// position so multiple crypts in one level still vary) for exactly the one <c>SelectRandomChallenge</c> call, then
    /// restore the previous <c>UnityEngine.Random.state</c>. Scoping it to that single call is the design point — like
    /// GH-1, it does not depend on the two peers issuing the same number of global-RNG calls anywhere else. The rounded
    /// position is deterministic across ends (level gen places the crypt identically), so both ends compute the same
    /// pick with no network round-trip; the pick is host-authoritative in effect.</para>
    ///
    /// <para><b>Intentional behaviour change, single player included:</b> the crypt challenge becomes a function of the
    /// level seed rather than re-rolled per playthrough — distribution unchanged, consistent with every other seeded
    /// generation choice. Not gated on multiplayer (that would make one seed mean different things in the same install).
    /// This aligns the crypt unit sets across ends; enemy spawning itself is host-authoritative under SP (phase 3).</para>
    /// </summary>
    internal static class CryptChallengePatches
    {
        private const int SeedSalt = 0x43727970; // "Cryp"

        public static int SelectionsSeeded;
        public static int SelectionsSkippedNoSeed;

        public static void Apply(Harmony harmony)
        {
            var mi = AccessTools.DeclaredMethod(typeof(CryptChallengeManager), "SelectRandomChallenge");
            if (mi == null)
            {
                Plugin.Log.Warn("[CryptChallenge] CryptChallengeManager.SelectRandomChallenge not found — challenge selection stays unseeded.");
                return;
            }
            try
            {
                harmony.Patch(mi,
                    prefix:  new HarmonyMethod(typeof(CryptChallengePatches).GetMethod(nameof(SelectRandomChallenge_Pre),  BindingFlags.Static | BindingFlags.NonPublic)),
                    postfix: new HarmonyMethod(typeof(CryptChallengePatches).GetMethod(nameof(SelectRandomChallenge_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                Plugin.Log.Info("[CryptChallenge] patched CryptChallengeManager.SelectRandomChallenge (seeded crypt challenge selection).");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[CryptChallenge] SelectRandomChallenge patch failed: {ex.Message}");
            }
        }

        private static void SelectRandomChallenge_Pre(CryptChallengeManager __instance, ref UnityEngine.Random.State? __state)
        {
            __state = null;
            try
            {
                long seed = 0;
                var gm = StaticInstance<GameManager>.Instance;
                if (gm != null) seed = gm.currentSeed;
                if (seed == 0)
                {
                    // No level seed to derive from — leave vanilla behaviour rather than inventing one.
                    SelectionsSkippedNoSeed++;
                    Plugin.Log.Warn("[CryptChallenge] no level seed available; crypt challenge left to the global RNG");
                    return;
                }

                int derived = unchecked((int)seed ^ SeedSalt ^ PositionHash(__instance));
                __state = UnityEngine.Random.state;
                UnityEngine.Random.InitState(derived);
                SelectionsSeeded++;
                Plugin.Log.Info($"[CryptChallenge] seeding crypt challenge from level seed={seed} derived={derived}");
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[CryptChallenge] seed failed: {ex.Message}");
            }
        }

        private static void SelectRandomChallenge_Post(UnityEngine.Random.State? __state)
        {
            try
            {
                if (__state.HasValue) UnityEngine.Random.state = __state.Value;
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[CryptChallenge] RNG state restore failed: {ex.Message}");
            }
        }

        // Rounded world position → a stable per-crypt salt. Deterministic gen places the crypt at the same spot on both
        // ends; rounding to the metre absorbs any float drift so both derive the identical value.
        private static int PositionHash(CryptChallengeManager mgr)
        {
            try
            {
                Vector3 p = mgr.transform.position;
                int x = Mathf.RoundToInt(p.x), y = Mathf.RoundToInt(p.y), z = Mathf.RoundToInt(p.z);
                return unchecked((x * 73856093) ^ (y * 19349663) ^ (z * 83492791));
            }
            catch { return 0; }
        }

        public static string FormatCounters()
            => $"cryptChallengeSeeded={SelectionsSeeded} skippedNoSeed={SelectionsSkippedNoSeed}";
    }
}
