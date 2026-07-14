using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Units;
using PerfectRandom.Sulfur.Core.Items;
using PerfectRandom.Sulfur.Core.Weapons;
using SULFURTogether.Networking.Gameplay;
using SULFURTogether.Networking.Gameplay.Boss;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// HZ-2 throwable tagging — marks the Breakable that <c>ThrowableWeapon.Throw</c> instantiates so its later
    /// <c>Breakable.Die</c> routes to the throwable-effect mirror (<see cref="ThrowableEffectManager"/>) instead of the
    /// in-scene BreakableBreak channel. <c>Throw</c> runs fully synchronously (Instantiate → SetOwner → SetStats → Spawn →
    /// AddForce), so an <c>_inThrow</c> flag bracketed around it is true exactly while the thrown Breakable calls
    /// <c>Unit.Spawn</c>. We tag on <c>Spawn</c> (virtual — reliably patchable) rather than <c>SetOwner</c> (a tiny
    /// non-virtual method the JIT inlines into <c>Throw</c>, so its Harmony patch never fires).
    /// </summary>
    internal static class ThrowablePatches
    {
        // True only during a ThrowableWeapon.Throw call on this frame — so only the thrown Breakable gets tagged.
        private static bool _inThrow;
        // The ItemId value of the weapon currently mid-Throw — stamped onto the thrown Breakable in Spawn_Post.
        private static int _pendingItemId;

        public static void Apply(Harmony harmony)
        {
            try
            {
                var throwableType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.Weapons.ThrowableWeapon");
                var throwMethod = throwableType != null ? AccessTools.Method(throwableType, "Throw") : null;
                if (throwMethod != null)
                    harmony.Patch(throwMethod,
                        prefix:    new HarmonyMethod(typeof(ThrowablePatches).GetMethod(nameof(Throw_Pre),  BindingFlags.Static | BindingFlags.NonPublic)),
                        finalizer: new HarmonyMethod(typeof(ThrowablePatches).GetMethod(nameof(Throw_Fin),  BindingFlags.Static | BindingFlags.NonPublic)));
                else
                    Plugin.Log.Warn("[ThrowableEffect] ThrowableWeapon.Throw not found — throwable-effect sync disabled.");

                // K-1 (issue #10): capture the projectile-path throwable (ThrowingKnives) at its canonical source. The
                // Custom projectile is built + dispatched inside Throw via ProjectileSystem.StartProjectile; a prefix
                // gated by _inThrow snapshots that exact ray (no throw-math reconstruction). Cheap: one bool for every
                // other projectile (bullets, replays), real work only during a throw.
                var projSysType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.ProjectileSystem");
                var startProjectile = projSysType != null ? AccessTools.Method(projSysType, "StartProjectile") : null;
                if (startProjectile != null)
                    harmony.Patch(startProjectile, prefix: new HarmonyMethod(
                        typeof(ThrowablePatches).GetMethod(nameof(StartProjectile_Pre), BindingFlags.Static | BindingFlags.NonPublic)));
                else
                    Plugin.Log.Warn("[ThrowableProjectile] ProjectileSystem.StartProjectile not found — knife flight sync disabled.");

                // Unit.Spawn (virtual) is called synchronously inside Throw on the thrown Breakable; tag it there.
                // (SetOwner — the more obvious hook — is a tiny non-virtual method that gets inlined into Throw, so its
                // patch never fires; 397 confirmed the Throw ran Breakable-path but no SetOwner postfix hit.)
                var unitType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.Units.Unit");
                var spawn = unitType != null ? AccessTools.Method(unitType, "Spawn") : null;
                if (spawn != null)
                    harmony.Patch(spawn, postfix: new HarmonyMethod(
                        typeof(ThrowablePatches).GetMethod(nameof(Spawn_Post), BindingFlags.Static | BindingFlags.NonPublic)));
                else
                    Plugin.Log.Warn("[ThrowableEffect] Unit.Spawn not found — throwable tagging disabled.");

                Plugin.Log.Info("[ThrowableEffect] Patched ThrowableWeapon.Throw + Unit.Spawn (throwable-effect tagging).");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[ThrowableEffect] Apply failed: {ex.Message}");
            }
        }

        private static void Throw_Pre(object __instance)
        {
            _inThrow = true;
            _pendingItemId = ReadWeaponItemId(__instance);
            if (Plugin.Cfg.LogThrowableEffectSync.Value)
                Plugin.Log.Info($"[ThrowableEffect] Throw weapon={(__instance as UnityEngine.Object)?.name} itemId={_pendingItemId}");
        }

        private static void Throw_Fin()  { _inThrow = false; _pendingItemId = 0; }

        // K-1 (issue #10): only the projectile-path throwable reaches StartProjectile inside Throw (the Breakable branch
        // never calls it), so _inThrow isolates exactly the ThrowingKnives dispatch. Snapshot the real ray + broadcast.
        private static void StartProjectile_Pre(ProjectileRay projState, ProjectileData projData)
        {
            if (!_inThrow) return;
            ThrowableProjectileManager.CaptureLocalThrow(projState, projData);
        }

        private static void Spawn_Post(Unit __instance)
        {
            if (!_inThrow) return;
            if (__instance is Breakable brk)
            {
                ThrowableEffectManager.MarkThrown(brk, _pendingItemId);
                // Throw's AddForce (Impulse) is applied on the NEXT physics step, not synchronously — so the body's
                // launch velocity isn't readable this frame (Log400: vel=0.0). Read it one FixedUpdate later, on the
                // body itself, and broadcast the HZ-3 flight then.
                brk.StartCoroutine(CaptureFlightAfterLaunch(brk, _pendingItemId));
                if (Plugin.Cfg.LogThrowableEffectSync.Value)
                    Plugin.Log.Info($"[ThrowableEffect] tagged thrown Breakable name={brk.name} itemId={_pendingItemId}");
            }
        }

        private static IEnumerator CaptureFlightAfterLaunch(Breakable brk, int itemId)
        {
            yield return new WaitForFixedUpdate(); // let Throw's AddForce(Impulse) land on the rigidbody
            if (brk == null) yield break;
            var rb = brk.Rigidbody;
            if (rb == null) yield break;
            ThrowableEffectManager.CaptureThrowFlight(itemId, brk.transform.position, rb.linearVelocity);
        }

        // Read the throwing weapon's stable ItemId value (Holdable.ItemDefinition.id.value). 0 if unavailable.
        private static int ReadWeaponItemId(object weapon)
        {
            try
            {
                if (BossReflect.GetMember(weapon, "ItemDefinition") is ItemDefinition def)
                    return def.id.value;
            }
            catch { }
            return 0;
        }
    }
}
