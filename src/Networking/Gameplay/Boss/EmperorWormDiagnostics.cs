using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay.Boss
{
    /// <summary>
    /// Phase 5.4-E3 (P1-2): Emperor is a multi-entity worm boss. LogOutput21 shows "two worms" on the client —
    /// likely the host-bound representation PLUS the client's own EmperorBossWorm controller driving a second one.
    /// Confirmed by decompilation (PerfectRandom.Sulfur.Gameplay.EmperorBossWorm):
    ///   - StartMovement() (public async void) sets `bossActive = true`; FixedUpdate() then drives jumps/underground.
    ///   - State fields: bossActive, isUnderground, isTravelingUnderground, currentJumpCount, canDoNextJump,
    ///     numberOfSections, wormNpcs, wormSections, sectionControllers.
    /// This class is DIAGNOSTIC-FIRST: it logs each worm's identity/section/active state so we can explain the two
    /// worms. The suppression is a REVERSIBLE SCAFFOLD (config default OFF, client-only) that blocks the local
    /// worm's autonomous movement driver (StartMovement) so the client stops driving a second worm. It never
    /// Destroys anything and only affects EmperorBossWorm.
    /// </summary>
    internal static class EmperorWormDiagnostics
    {
        private static readonly Dictionary<string, float> _lastLog = new Dictionary<string, float>();
        private const float ThrottleSeconds = 1.5f;

        private static bool DiagEnabled { get { try { return Plugin.Cfg.EnableEmperorWormDiagnostics.Value; } catch { return false; } } }
        private static bool LogOn { get { try { return Plugin.Cfg.LogBossEncounter.Value; } catch { return false; } } }
        private static bool SuppressEnabled { get { try { return Plugin.Cfg.EnableEmperorClientWormSuppression.Value; } catch { return false; } } }

        public static void Apply(Harmony harmony)
        {
            if (!DiagEnabled) { Plugin.Log.Info("[EmperorWorm] diagnostics disabled by config."); return; }

            var worm = AccessTools.TypeByName("PerfectRandom.Sulfur.Gameplay.EmperorBossWorm");
            if (worm == null) { Plugin.Log.Info("[EmperorWorm] EmperorBossWorm type not found."); return; }

            var post = new HarmonyMethod(typeof(EmperorWormDiagnostics).GetMethod(nameof(Worm_Post), BindingFlags.Static | BindingFlags.NonPublic));
            var preStart = new HarmonyMethod(typeof(EmperorWormDiagnostics).GetMethod(nameof(StartMovement_Pre), BindingFlags.Static | BindingFlags.NonPublic));

            foreach (var name in new[] { "StartMovement", "JumpTo", "StartUndergroundTravel", "HitGround", "OnDeath" })
            {
                foreach (var mi in AccessTools.GetDeclaredMethods(worm).Where(m => m.Name == name && !m.IsStatic))
                {
                    try { harmony.Patch(mi, postfix: post); }
                    catch (Exception ex) { Plugin.Log.Error($"[EmperorWorm] probe patch {name} failed: {ex.Message}"); }
                }
            }

            // Reversible client-side suppression scaffold on the autonomous-drive entry.
            foreach (var mi in AccessTools.GetDeclaredMethods(worm).Where(m => m.Name == "StartMovement" && !m.IsStatic))
            {
                try { harmony.Patch(mi, prefix: preStart); }
                catch (Exception ex) { Plugin.Log.Error($"[EmperorWorm] suppression patch failed: {ex.Message}"); }
            }

            Plugin.Log.Info("[EmperorWorm] diagnostics registered (suppression scaffold default off).");
        }

        // Prefix: on the CLIENT, when suppression is enabled, block the local worm's autonomous movement start so the
        // client stops driving an independent second worm. Reversible (config toggle). Host is never affected.
        private static bool StartMovement_Pre(object __instance)
        {
            try
            {
                if (!SuppressEnabled) return true;
                if (NetGameplaySyncBridge.BossMode != NetMode.Client) return true;
                Plugin.Log.Info($"[EmperorWorm] client suppression: blocking local StartMovement on {Identify(__instance)} (scaffold; host-authoritative worm only)");
                return false; // block client-local autonomous worm movement
            }
            catch { return true; }
        }

        private static void Worm_Post(object __instance, MethodBase __originalMethod, bool __runOriginal)
        {
            try
            {
                if (!DiagEnabled || !LogOn || __instance == null || !__runOriginal) return;
                string method = __originalMethod?.Name ?? "?";
                string tk = BossReflect.InstanceId(__instance) + "|" + method;
                float now = Time.realtimeSinceStartup;
                if (_lastLog.TryGetValue(tk, out float last) && (now - last) < ThrottleSeconds) return;
                _lastLog[tk] = now;
                Plugin.Log.Info($"[EmperorWorm] {method} | {Describe(__instance)}");
            }
            catch { }
        }

        public static string Identify(object worm)
        {
            string root = BossReflect.RootName(worm);
            int id = BossReflect.InstanceId(worm);
            string path = BossReflect.GameObjectPath(worm);
            string mode = NetGameplaySyncBridge.BossMode.ToString();
            return $"root={root} inst={id} path={path} side={mode}";
        }

        public static string Describe(object worm)
        {
            BossReflect.TryGetBool(worm, "bossActive", out bool active);
            BossReflect.TryGetBool(worm, "isUnderground", out bool under);
            BossReflect.TryGetBool(worm, "isTravelingUnderground", out bool travel);
            BossReflect.TryGetInt(worm, "currentJumpCount", out int jumps);
            BossReflect.TryGetInt(worm, "numberOfSections", out int sections);
            int sectionCtrls = (BossReflect.GetMember(worm, "sectionControllers") is System.Collections.ICollection c) ? c.Count : -1;
            int npcs = (BossReflect.GetMember(worm, "wormNpcs") is System.Collections.ICollection c2) ? c2.Count : -1;
            Vector3 pos = (worm is Component cc && cc != null) ? cc.transform.position : Vector3.zero;
            return $"{Identify(worm)} bossActive={active} underground={under} traveling={travel} jumps={jumps} sections={sections} sectionCtrls={sectionCtrls} wormNpcs={npcs} pos={pos:F1}";
        }

        /// <summary>One-shot scene scan that explains how many worms exist and which side drives them.</summary>
        public static void LogWormManifest()
        {
            try
            {
                var worm = AccessTools.TypeByName("PerfectRandom.Sulfur.Gameplay.EmperorBossWorm");
                if (worm == null) return;
                var all = Resources.FindObjectsOfTypeAll(worm);
                var live = all.Where(o => o is Component cc && cc != null && cc.gameObject.scene.IsValid() && cc.gameObject.scene.isLoaded).ToList();
                Plugin.Log.Info($"[EmperorWorm] manifest: {live.Count} live EmperorBossWorm instance(s)");
                foreach (var o in live) Plugin.Log.Info($"[EmperorWorm]   - {Describe(o)}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[EmperorWorm] manifest failed: {ex.GetType().Name}: {ex.Message}"); }
        }
    }
}
