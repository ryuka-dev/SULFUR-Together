using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PerfectRandom.Sulfur.Core;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// GH-1: make the level's ghost SPECIES follow the level seed.
    ///
    /// <para><c>LevelGeneration.SpawnEnemiesNode.SpawnGhost</c> decides <i>how many</i> ghosts to place, <i>which
    /// rooms</i> they go in, and their scatter offsets from the node's seeded RNG (<c>base._Random</c>) — but picks
    /// the species from the GLOBAL <c>UnityEngine.Random</c>:</para>
    /// <code>
    /// int count = LevelGenGraphUtilities.RouletteWheelSelection(ref base._Random, ghostWraithChances);
    /// switch (Random.Range(0, 3)) { Apparition / Poltergeist / Wraith }
    /// </code>
    /// <para>So two peers generating the same seed agree on the ghost's existence and position and disagree on its
    /// type two times out of three. A type mismatch is refused by the roster binder, which leaves the host's ghost
    /// with no representation on the client at all: it stays alive and keeps attacking, completely invisible there
    /// (Log517: <c>No local match for hostIdx=1 hostUnit=GhostPoltergeist</c> against
    /// <c>Client-only non-combat localIdx=1 unit=GhostWraith</c>, identical positions).</para>
    ///
    /// <para><b>Fix.</b> Seed the global RNG from the level seed for exactly the one call that picks the species,
    /// then put the previous RNG state back. Scoping it to the single <c>MoveNext</c> that runs the pick is what
    /// keeps this safe: nothing else on either peer is affected, so it does not depend on the two peers issuing
    /// the same number of <c>UnityEngine.Random</c> calls — an assumption that seeding the whole generation pass
    /// would rely on and that nothing enforces.</para>
    ///
    /// <para><b>Intentional behaviour change, single player included:</b> ghost species becomes a function of the
    /// level seed everywhere, rather than being re-rolled per playthrough. The distribution is unchanged (still a
    /// uniform pick of three) and it matches what every other random choice in the same method already does.
    /// Gating it on "is multiplayer active" was rejected: it would make a seed mean different things in the same
    /// install depending on session state, for no correctness gain.</para>
    /// </summary>
    internal static class GhostSpawnPatches
    {
        // Keeps the species pick out of the RNG stream everything else shares.
        private const int SeedSalt = 0x47686F73; // "Ghos"

        private static bool _seededThisGeneration;

        public static int SpeciesRollsSeeded;
        public static int SpeciesRollsSkippedNoSeed;

        public static void Apply(Harmony harmony)
        {
            Type? nodeType = FindType("SpawnEnemiesNode");
            MethodInfo? moveNext = ResolveIteratorMoveNext(nodeType, "SpawnGhost");
            if (moveNext == null)
            {
                Plugin.Log.Warn("[GhostSpawn] SpawnEnemiesNode.SpawnGhost state machine not resolved — ghost species stays unseeded.");
                return;
            }

            try
            {
                harmony.Patch(moveNext,
                    prefix:  new HarmonyMethod(typeof(GhostSpawnPatches).GetMethod(nameof(SpawnGhost_MoveNext_Pre),  BindingFlags.Static | BindingFlags.NonPublic)),
                    postfix: new HarmonyMethod(typeof(GhostSpawnPatches).GetMethod(nameof(SpawnGhost_MoveNext_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                Plugin.Log.Info($"[GhostSpawn] patched {moveNext.DeclaringType?.FullName}.MoveNext (seeded ghost species)");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[GhostSpawn] SpawnGhost MoveNext patch failed: {ex.Message}");
            }
        }

        /// <summary>A new generation pass has begun — the next SpawnGhost is a fresh species pick.</summary>
        public static void ResetForNewGeneration()
        {
            _seededThisGeneration = false;
        }

        // SpawnGhost is invoked once per generation and the species pick happens before its first yield, so
        // seeding on the first MoveNext of the pass covers exactly that call.
        private static void SpawnGhost_MoveNext_Pre(ref UnityEngine.Random.State? __state)
        {
            __state = null;
            try
            {
                if (_seededThisGeneration) return;
                _seededThisGeneration = true;

                long seed = 0;
                var gm = StaticInstance<GameManager>.Instance;
                if (gm != null) seed = gm.currentSeed;
                if (seed == 0)
                {
                    // No level seed to derive from — leave vanilla behaviour rather than inventing one.
                    SpeciesRollsSkippedNoSeed++;
                    Plugin.Log.Warn("[GhostSpawn] no level seed available; ghost species left to the global RNG");
                    return;
                }

                int derived = unchecked((int)seed ^ SeedSalt);
                __state = UnityEngine.Random.state;
                UnityEngine.Random.InitState(derived);
                SpeciesRollsSeeded++;
                Plugin.Log.Info($"[GhostSpawn] seeding ghost species from level seed={seed} derived={derived}");
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[GhostSpawn] seed failed: {ex.Message}");
            }
        }

        private static void SpawnGhost_MoveNext_Post(UnityEngine.Random.State? __state)
        {
            try
            {
                // Restore whatever the rest of the game was using, so only the species pick was ours.
                if (__state.HasValue) UnityEngine.Random.state = __state.Value;
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[GhostSpawn] RNG state restore failed: {ex.Message}");
            }
        }

        public static string FormatCounters()
            => $"ghostSpeciesSeeded={SpeciesRollsSeeded} skippedNoSeed={SpeciesRollsSkippedNoSeed}";

        // ---- resolution ----

        private static Type? FindType(string shortName)
        {
            string[] candidates =
            {
                "LevelGeneration." + shortName,
                "PerfectRandom.Sulfur.Core.LevelGeneration." + shortName,
                shortName,
            };
            foreach (var full in candidates)
            {
                var t = AccessTools.TypeByName(full);
                if (t != null) return t;
            }

            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException rtle) { types = rtle.Types.Where(t => t != null).ToArray()!; }
                    foreach (var t in types)
                        if (t != null && t.Name == shortName) return t;
                }
            }
            catch { }
            return null;
        }

        private static MethodInfo? ResolveIteratorMoveNext(Type? owner, string methodName)
        {
            if (owner == null) return null;
            try
            {
                var mi = owner.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var attr = mi?.GetCustomAttributes().FirstOrDefault(a => a.GetType().Name.Contains("StateMachine"));
                Type? sm = null;
                if (attr != null && attr.GetType().GetProperty("StateMachineType")?.GetValue(attr) is Type smt)
                    sm = smt;
                sm ??= owner.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(nt => typeof(IEnumerator).IsAssignableFrom(nt) && nt.Name.Contains(methodName));

                return sm?.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
            catch { return null; }
        }
    }
}
