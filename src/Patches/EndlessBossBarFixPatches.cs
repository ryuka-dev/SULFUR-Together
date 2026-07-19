using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// EM-Boss residual-health-bar fix. The endless boss crypt bar (<c>CryptProgressBar</c>) does not hide instantly:
    /// <c>Hide()</c> starts <c>HideCoroutine</c>, which slides the bar to its hidden position and only then calls
    /// <c>gameObject.SetActive(false)</c>. That slide is driven in <c>Update</c> by
    /// <c>Vector2.Lerp(anchoredPosition, targetPosition, showHideLerpSpeed * Time.deltaTime)</c> — using SCALED time.
    ///
    /// <para>When an endless boss dies, the wave completes and an arena (stage) transition begins immediately; that
    /// transition freezes time (<c>Time.timeScale == 0</c>). With scaled time frozen, <c>Time.deltaTime</c> is 0, the
    /// Lerp never advances, the coroutine's "slide finished" condition is never met, and <c>SetActive(false)</c> never
    /// runs — so the bar lingers on-screen into the next stage(s). Observed as the residual boss health bar in co-op,
    /// where the boss-death → stage-transition freeze lands right on top of the hide slide (host & client crypt roster
    /// both correctly emptied to 0 segments, yet the bar stayed visible).</para>
    ///
    /// <para>Fix: rewrite the single <c>Time.deltaTime</c> read in <c>CryptProgressBar.Update</c> to
    /// <c>Time.unscaledDeltaTime</c> so the show/hide slide keeps advancing while time is frozen, letting the hide
    /// coroutine reach <c>SetActive(false)</c>. Unscaled ≈ scaled during normal play (timeScale 1), so the animation
    /// feel is unchanged; it only differs while frozen, which is exactly when the bar must still be able to slide away.
    /// Narrow: only the crypt bar's Update is touched (the segment-fill lerp in <c>UpdateSegments</c> is a separate
    /// method and is left alone).</para>
    /// </summary>
    internal static class EndlessBossBarFixPatches
    {
        public static void Apply(Harmony harmony)
        {
            try
            {
                var cryptType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.UI.CryptProgressBar")
                             ?? AccessTools.TypeByName("CryptProgressBar");
                if (cryptType == null)
                {
                    Plugin.Log.Info("[EM-Boss] CryptProgressBar type not found — residual-bar fix disabled.");
                    return;
                }

                var update = AccessTools.DeclaredMethod(cryptType, "Update");
                if (update == null)
                {
                    Plugin.Log.Info("[EM-Boss] CryptProgressBar.Update not found — residual-bar fix disabled.");
                    return;
                }

                harmony.Patch(update, transpiler: new HarmonyMethod(
                    typeof(EndlessBossBarFixPatches).GetMethod(nameof(UpdateSlideUnscaledTranspiler),
                        BindingFlags.Static | BindingFlags.NonPublic)));
                Plugin.Log.Info("[EM-Boss] CryptProgressBar hide-slide fix patched (Update → unscaled time).");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[EM-Boss] EndlessBossBarFixPatches.Apply failed: {ex.Message}");
            }
        }

        // Replace every `Time.deltaTime` getter call in CryptProgressBar.Update with `Time.unscaledDeltaTime` so the
        // show/hide slide advances even while Time.timeScale == 0 (stage-transition freeze).
        private static IEnumerable<CodeInstruction> UpdateSlideUnscaledTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var deltaTime = AccessTools.PropertyGetter(typeof(Time), nameof(Time.deltaTime));
            var unscaled = AccessTools.PropertyGetter(typeof(Time), nameof(Time.unscaledDeltaTime));
            int swapped = 0;

            foreach (var ci in instructions)
            {
                if (deltaTime != null && unscaled != null && ci.Calls(deltaTime))
                {
                    ci.operand = unscaled;
                    swapped++;
                }
                yield return ci;
            }

            Plugin.Log.Info($"[EM-Boss] CryptProgressBar.Update transpiler swapped {swapped} deltaTime→unscaledDeltaTime.");
        }
    }
}
