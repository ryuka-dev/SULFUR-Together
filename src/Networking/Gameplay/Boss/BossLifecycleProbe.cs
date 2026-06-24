using System;
using System.Collections.Generic;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay.Boss
{
    /// <summary>
    /// Phase 5.4-E2: lightweight, read-only diagnostic over the real boss lifecycle methods (resolved by
    /// reflection and patched as postfixes in <see cref="SULFURTogether.Patches.BossEncounterPatches"/>). It does
    /// NOT drive or block anything — it only logs compact state so we can confirm the host-authorized start chain
    /// actually completes (fightStarted / phase / death) on both ends. High-frequency damage hooks are throttled.
    /// </summary>
    internal static class BossLifecycleProbe
    {
        private static bool Enabled
        {
            get { try { return Plugin.Cfg.EnableBossLifecycleProbe.Value; } catch { return false; } }
        }
        private static bool LogOn
        {
            get { try { return Plugin.Cfg.LogBossLifecycle.Value; } catch { return false; } }
        }

        // Throttle map for high-frequency methods (damage): instanceId|method -> last log time.
        private static readonly Dictionary<string, float> _lastThrottled = new Dictionary<string, float>();
        private const float ThrottleSeconds = 1f;

        /// <summary>Called from the lifecycle postfix patches. Never throws. <paramref name="throttle"/> caps
        /// high-frequency methods to one line per second per instance.</summary>
        public static void OnLifecycle(object? instance, string method, bool throttle, bool ran)
        {
            try
            {
                if (!Enabled || !LogOn || instance == null || !ran) return;

                if (throttle)
                {
                    string tk = BossReflect.InstanceId(instance) + "|" + method;
                    float now = Time.realtimeSinceStartup;
                    if (_lastThrottled.TryGetValue(tk, out float last) && (now - last) < ThrottleSeconds) return;
                    _lastThrottled[tk] = now;
                }

                Plugin.Log.Info($"[BossLifecycle] {instance.GetType().Name}.{method} | {DescribeState(instance)}");
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[BossLifecycle] probe failed for {method}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Family-aware compact state: prefer the adapter describe; otherwise read generic boss/phase fields
        /// (covers BossPhase, whose internal transitions have no adapter).</summary>
        private static string DescribeState(object instance)
        {
            var adapterDesc = NetBossEncounterManager.DescribeComponent(instance);
            if (adapterDesc != null) return adapterDesc;

            var parts = new List<string>();
            if (BossReflect.TryGetBool(instance, "fightStarted", out bool fs)) parts.Add($"fightStarted={fs}");
            if (BossReflect.TryGetBool(instance, "FightStarted", out bool FS)) parts.Add($"FightStarted={FS}");
            if (BossReflect.TryGetInt(instance, "currentPhaseIndex", out int cpi)) parts.Add($"currentPhaseIndex={cpi}");
            if (BossReflect.TryGetBool(instance, "isTransitioning", out bool tr)) parts.Add($"isTransitioning={tr}");
            var cbp = BossReflect.GetMember(instance, "currentBossPhase");
            if (cbp != null && BossReflect.TryGetInt(cbp, "bossPhaseIndex", out int bpi)) parts.Add($"bossPhaseIndex={bpi}");
            return parts.Count == 0 ? "<no-readable-state>" : string.Join(" ", parts);
        }
    }
}
