using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// FH-6 — split <c>Npc.TriggerShoot</c>, the call FH-5 caught blocking for seconds. Diagnostic only.
    ///
    /// <para>FH-5 named it exactly: a client's replay of the host's ranged attack (<c>rootReplay</c> →
    /// <c>TriggerShoot</c>) on a <c>Blackguild_Rifleman</c> puppet took 7149 ms, and twice more at ~1.7 s. The method
    /// itself is four statements:</para>
    /// <code>
    /// SetAimTarget(AiAgent.target);
    /// … shootingDirection = (AiAgent.target.transform.position - billboard.sprite.position).normalized;
    /// onShootingDirection?.Invoke(shootingDirection);
    /// StartCoroutine(ShootTriggerRoutine());     // runs synchronously to its first yield, which calls SetShooting(true)
    /// </code>
    /// <para>so the time is in one of three places: <c>SetAimTarget</c>, the <c>onShootingDirection</c> subscribers, or
    /// the weapon actually starting to fire (<c>SetShooting</c> → <c>Weapon.SetTrigger</c>) inside the coroutine's first
    /// synchronous iteration. Already excluded by the same capture: <c>SetAimTarget</c>'s null-target branch, which
    /// would log <c>"Target is null!"</c> — that string does not appear once in the Player.log, so the target was
    /// live.</para>
    ///
    /// <para>Times the two sub-calls we can name and derives the third by subtraction, then dumps the split plus the
    /// state the method reads as soon as one <c>TriggerShoot</c> crosses the threshold. Two Stopwatch reads per call on
    /// a path that fires a few times a second per shooter; the accumulators only track calls nested inside a
    /// <c>TriggerShoot</c>, so the coroutine's later <c>SetShooting(false)</c> is not counted.</para>
    /// </summary>
    internal static class RangedShootProbePatches
    {
        private const long SlowTriggerShootMs = 50;

        private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

        private static int  _depth;          // >0 while inside a TriggerShoot on this thread (single-threaded: Unity main)
        private static long _shootBeganAt;
        private static long _aimMs, _setShootingMs;
        private static long _aimBeganAt, _setShootingBeganAt;

        private static bool LogOn
        {
            get { try { return Plugin.Cfg.LogClientFrameHitch.Value; } catch { return false; } }
        }

        public static void Apply(Harmony harmony)
        {
            Type? npc = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.Units.Npc")
                     ?? AccessTools.TypeByName("PerfectRandom.Sulfur.Core.Npc")
                     ?? AccessTools.TypeByName("Npc");
            if (npc == null) { Plugin.Log.Warn("[ShootProbe] Npc type not found — ranged-shoot split unavailable."); return; }

            Patch(harmony, npc, "TriggerShoot", nameof(TriggerShoot_Pre), nameof(TriggerShoot_Post));
            Patch(harmony, npc, "SetAimTarget",  nameof(Aim_Pre),         nameof(Aim_Post));
            Patch(harmony, npc, "SetShooting",   nameof(SetShooting_Pre), nameof(SetShooting_Post));
        }

        private static void Patch(Harmony harmony, Type type, string method, string pre, string post)
        {
            try
            {
                var mi = AccessTools.Method(type, method);
                if (mi == null) { Plugin.Log.Warn($"[ShootProbe] {type.Name}.{method} not found"); return; }
                harmony.Patch(mi,
                    prefix:  new HarmonyMethod(typeof(RangedShootProbePatches).GetMethod(pre,  BindingFlags.Static | BindingFlags.NonPublic)),
                    postfix: new HarmonyMethod(typeof(RangedShootProbePatches).GetMethod(post, BindingFlags.Static | BindingFlags.NonPublic)));
                Plugin.Log.Info($"[ShootProbe] patched {type.Name}.{method}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[ShootProbe] {type.Name}.{method} patch failed: {ex.Message}"); }
        }

        // ----------------------------------------------------------------- accounting

        private static void TriggerShoot_Pre()
        {
            if (!LogOn) return;
            if (_depth++ != 0) return;          // re-entrant (TriggerShootFromAnimation → TriggerShoot): keep the outer window
            _shootBeganAt = Clock.ElapsedMilliseconds;
            _aimMs = 0;
            _setShootingMs = 0;
        }

        private static void TriggerShoot_Post(object __instance)
        {
            if (!LogOn) return;
            if (--_depth != 0) return;
            long total = Clock.ElapsedMilliseconds - _shootBeganAt;
            if (total < SlowTriggerShootMs) return;

            long other = total - _aimMs - _setShootingMs;   // onShootingDirection subscribers + StartCoroutine
            Plugin.Log.Info($"[ShootProbe] SLOW TriggerShoot total={total}ms  setAimTarget={_aimMs}ms  setShooting/weaponTrigger={_setShootingMs}ms  onShootingDirection+startCoroutine={other}ms  {DescribeShooter(__instance)}");
        }

        private static void Aim_Pre()          { if (LogOn && _depth > 0) _aimBeganAt = Clock.ElapsedMilliseconds; }
        private static void Aim_Post()         { if (LogOn && _depth > 0) _aimMs += Clock.ElapsedMilliseconds - _aimBeganAt; }
        private static void SetShooting_Pre()  { if (LogOn && _depth > 0) _setShootingBeganAt = Clock.ElapsedMilliseconds; }
        private static void SetShooting_Post() { if (LogOn && _depth > 0) _setShootingMs += Clock.ElapsedMilliseconds - _setShootingBeganAt; }

        // ----------------------------------------------------------------- context for the slow call

        /// <summary>Everything <c>TriggerShoot</c> reads, so a slow call carries its own inputs. Each field is read
        /// defensively — this only ever runs on an already-pathological call, and a probe must not become the crash.</summary>
        private static string DescribeShooter(object npc)
        {
            try
            {
                var comp = npc as Component;
                string unit = comp != null ? comp.gameObject.name : "?";
                string active = comp != null ? comp.gameObject.activeInHierarchy.ToString() : "?";

                object? agent = Get(npc, "AiAgent");
                object? target = agent == null ? null : Get(agent, "target");
                string targetName = "null";
                string targetPos = "-";
                if (target is Component tc && tc != null)
                {
                    targetName = tc.gameObject.name;
                    Vector3 p = tc.transform.position;
                    targetPos = $"({p.x:F1},{p.y:F1},{p.z:F1}) finite={IsFinite(p)}";
                }

                object? weapon = Get(npc, "weapon");
                string weaponName = weapon is Component wc && wc != null ? wc.gameObject.name : (weapon == null ? "null" : weapon.GetType().Name);
                object? triggerActive = weapon == null ? null : Get(weapon, "bIsTriggerActive");

                return $"unit={unit} active={active} target={targetName} targetPos={targetPos} weapon={weaponName} triggerActive={triggerActive ?? "?"}";
            }
            catch (Exception ex) { return $"(context unavailable: {ex.Message})"; }
        }

        private static bool IsFinite(Vector3 v)
            => !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z)
            && !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);

        private static object? Get(object obj, string name)
        {
            try
            {
                var t = obj.GetType();
                var p = AccessTools.Property(t, name);
                if (p != null) return p.GetValue(obj);
                var f = AccessTools.Field(t, name);
                return f?.GetValue(obj);
            }
            catch { return null; }
        }
    }
}
