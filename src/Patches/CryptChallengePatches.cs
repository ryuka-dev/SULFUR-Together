using System;
using System.Reflection;
using HarmonyLib;
using PerfectRandom.Sulfur.Core;
using SULFURTogether.Networking;
using SULFURTogether.Networking.Gameplay;
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

        /// <summary>SP (crypt sync phase 3): a linked client owns no crypt challenge — the host does. The host runs the
        /// challenge and its <c>CryptPeriodicEnemySpawner</c> spawns are mirrored to the client 1:1 (RuntimeSpawn), so a
        /// host kill maps exactly. If the client ALSO ran the challenge it would spawn its own divergent enemies and,
        /// worse, its own win/lose timer (a failure calls <c>PlayerUnit.Die()</c>). Block the whole thing on the client.</summary>
        private static bool IsLinkedClient =>
            NetGameplaySyncBridge.BossMode == NetMode.Client && NetLinkState.ClientLinked;

        private static bool CryptSyncEnabled { get { try { return Plugin.Cfg.EnableCryptSync.Value; } catch { return false; } } }
        private static bool LogOn { get { try { return Plugin.Cfg.LogCryptSync.Value; } catch { return false; } } }

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

            // SP: block the whole crypt challenge on a linked client (host-authoritative; the client mirrors host enemies).
            var start = AccessTools.DeclaredMethod(typeof(CryptChallengeManager), "StartChallenge");
            if (start != null)
            {
                try
                {
                    harmony.Patch(start, prefix: new HarmonyMethod(
                        typeof(CryptChallengePatches).GetMethod(nameof(StartChallenge_Pre), BindingFlags.Static | BindingFlags.NonPublic)));
                    Plugin.Log.Info("[CryptChallenge] patched CryptChallengeManager.StartChallenge (client challenge suppression).");
                }
                catch (Exception ex) { Plugin.Log.Error($"[CryptChallenge] StartChallenge patch failed: {ex.Message}"); }
            }
            else Plugin.Log.Warn("[CryptChallenge] CryptChallengeManager.StartChallenge not found — client challenge suppression disabled.");

            // AC: host broadcasts the challenge OUTCOME so the client opens the reward room (completion) / shares the
            // death (failure). OnChallengeCompleted / OnChallengeFailed are the private methods that run the outcome.
            HookOutcome(harmony, "OnChallengeCompleted", nameof(OnChallengeCompleted_Post));
            HookOutcome(harmony, "OnChallengeFailed",    nameof(OnChallengeFailed_Post));

            // AC: mirror the native CryptUI bar to the client (host-localized label). Hook the singleton's UpdateInfo /
            // TurnOff — the single chokepoint for the crypt progress bar.
            var cryptUi = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.CryptUI") ?? AccessTools.TypeByName("CryptUI");
            if (cryptUi != null)
            {
                var update = AccessTools.Method(cryptUi, "UpdateInfo", new[] { typeof(string), typeof(bool) });
                if (update != null)
                    TryPatch(harmony, update, nameof(CryptUI_UpdateInfo_Post));
                var turnOff = AccessTools.Method(cryptUi, "TurnOff", Type.EmptyTypes);
                if (turnOff != null)
                    TryPatch(harmony, turnOff, nameof(CryptUI_TurnOff_Post));
                Plugin.Log.Info($"[CryptChallenge] patched CryptUI.UpdateInfo({update != null})/TurnOff({turnOff != null}) (client bar mirror).");
            }
            else Plugin.Log.Warn("[CryptChallenge] CryptUI type not found — client bar mirror disabled.");
        }

        private static void HookOutcome(Harmony harmony, string method, string postfixName)
        {
            var mi = AccessTools.DeclaredMethod(typeof(CryptChallengeManager), method);
            if (mi == null) { Plugin.Log.Warn($"[CryptChallenge] CryptChallengeManager.{method} not found — outcome sync partial."); return; }
            try
            {
                harmony.Patch(mi, postfix: new HarmonyMethod(
                    typeof(CryptChallengePatches).GetMethod(postfixName, BindingFlags.Static | BindingFlags.NonPublic)));
            }
            catch (Exception ex) { Plugin.Log.Error($"[CryptChallenge] {method} patch failed: {ex.Message}"); }
        }

        private static void TryPatch(Harmony harmony, MethodInfo target, string postfixName)
        {
            try
            {
                harmony.Patch(target, postfix: new HarmonyMethod(
                    typeof(CryptChallengePatches).GetMethod(postfixName, BindingFlags.Static | BindingFlags.NonPublic)));
            }
            catch (Exception ex) { Plugin.Log.Error($"[CryptChallenge] {target.Name} patch failed: {ex.Message}"); }
        }

        // Host: the challenge just completed / failed on this end — broadcast so the client replays the outcome.
        private static void OnChallengeCompleted_Post(CryptChallengeManager __instance, bool __runOriginal)
        {
            if (!__runOriginal) return;
            CryptChallengeSyncManager.HostBroadcastOutcome(__instance, completed: true);
        }

        private static void OnChallengeFailed_Post(CryptChallengeManager __instance, bool __runOriginal)
        {
            if (!__runOriginal) return;
            CryptChallengeSyncManager.HostBroadcastOutcome(__instance, completed: false);
        }

        // Host: the crypt bar label changed / cleared — mirror it to the client (a re-entrant apply is guarded out).
        private static void CryptUI_UpdateInfo_Post(string challengeInfo, bool useAsTimer, bool __runOriginal)
        {
            if (!__runOriginal || CryptChallengeSyncManager.IsApplyingUi) return;
            CryptChallengeSyncManager.HostBroadcastUi(challengeInfo, useAsTimer);
        }

        private static void CryptUI_TurnOff_Post(bool __runOriginal)
        {
            if (!__runOriginal || CryptChallengeSyncManager.IsApplyingUi) return;
            CryptChallengeSyncManager.HostBroadcastUiClear();
        }

        // Returns false (skip original) on a linked client so its crypt challenge — selection, spawners, timer, win/lose —
        // never runs. The host is authoritative; the client only sees host-mirrored crypt enemies + a host outcome (AC).
        // Before skipping, replay the challenge's client-VISIBLE static setup that OnStartChallenge would have done —
        // for the Protect trial that means revealing the altars + their see-through highlight (otherwise the client sees
        // nothing to protect: the altars sit hidden by the challenge's own Start() and are only un-hidden in the blocked
        // OnStartChallenge). Enemy visuals come from SP's host mirror; the bar comes from AC.
        private static bool StartChallenge_Pre(CryptChallengeManager __instance)
        {
            if (CryptSyncEnabled && IsLinkedClient)
            {
                RevealProtectAltarsIfSelected(__instance);
                if (LogOn) Plugin.Log.Info("[CryptChallenge] linked client — crypt challenge suppressed (host-authoritative; mirroring host enemies)");
                return false;
            }
            return true;
        }

        // Client visual-only: if the selected challenge is a Protect trial, un-hide its altars + outline highlight the
        // same way CryptProtectTargetChallenge.OnStartChallenge does — WITHOUT spawning the units or running any
        // gameplay (no roster entry, no damage handlers, no spawners). Altar damage state stays host-side; this is
        // purely "the client can see what it is protecting".
        private static void RevealProtectAltarsIfSelected(CryptChallengeManager mgr)
        {
            try
            {
                if (mgr == null) return;
                _selectedChallengeField ??= AccessTools.Field(typeof(CryptChallengeManager), "selectedChallenge");
                if (_selectedChallengeField?.GetValue(mgr) is not CryptProtectTargetChallenge protect) return;
                if (protect.protectTargets == null) return;

                int shown = 0;
                foreach (var target in protect.protectTargets)
                {
                    var unit = target?.unit;
                    if (unit == null) continue;
                    if (!unit.TryGetComponent<CryptColumnDamageController>(out var col)) continue;
                    if (col.root != null) col.root.SetActive(true);
                    if (col.outlineObjects != null)
                        foreach (var o in col.outlineObjects)
                            if (o != null) o.SetActive(true);
                    shown++;
                }
                if (LogOn) Plugin.Log.Info($"[CryptChallenge] client revealed {shown} protect altar(s) + outline highlight");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[CryptChallenge] reveal altars failed: {ex.Message}"); }
        }

        private static FieldInfo? _selectedChallengeField;

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
