using System;
using HarmonyLib;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Items;
using PerfectRandom.Sulfur.Core.Units;
using SULFURTogether.Networking;
using SULFURTogether.Networking.Gameplay;
using UnityEngine;

namespace SULFURTogether.Patches
{
    /// <summary>
    /// ED-3: the game seams the forwarded-hit fixes need.
    /// <list type="bullet">
    /// <item><c>ExplosionManager.Update</c> prefix, HOST — every explosion this Update resolves (queued, or written by a
    /// projectile's hit job) is read here; an enemy's is sent to nearby clients (<see cref="EnemyExplosionRelay"/>).</item>
    /// <item><c>Unit.TryParryWithWeapon</c>, HOST — the game offering the stand-in a parry right before a swing's or a
    /// projectile's damage marks that damage as parryable (<see cref="CopyDamagePatches.NoteParryOpportunity"/>).</item>
    /// <item><c>Unit.TryParryWithWeapon</c>, CLIENT — a copy's hit on the local player is answered with, or answers, the
    /// parry of the forwarded hit of the same attack (<see cref="ForwardedHitParry"/>).</item>
    /// </list>
    /// </summary>
    internal static class ForwardedHitPatches
    {
        public static void Apply(Harmony harmony)
        {
            bool explosions = Patch(harmony,
                AccessTools.DeclaredMethod(typeof(ExplosionManager), "Update", Type.EmptyTypes),
                prefix: nameof(ExplosionManager_Update_Pre));
            bool parry = Patch(harmony,
                AccessTools.DeclaredMethod(typeof(Unit), nameof(Unit.TryParryWithWeapon),
                    new[] { typeof(Vector3), typeof(MeleeParryTypes), typeof(Unit) }),
                prefix: nameof(TryParryWithWeapon_Pre), postfix: nameof(TryParryWithWeapon_Post));
            if (explosions && parry && EnemyExplosionRelay.CanReadQueue)
                Plugin.Log.Info("[ForwardedHit] Patched ExplosionManager.Update / Unit.TryParryWithWeapon (enemy explosions resolved on the client; forwarded hits parryable).");
            else
                Plugin.Log.Warn($"[ForwardedHit] a seam is missing (ExplosionManager.Update={explosions}, queue={EnemyExplosionRelay.CanReadQueue}, TryParryWithWeapon={parry}) — a client may not take an enemy explosion, or parry a forwarded hit");
        }

        private static bool Patch(Harmony harmony, System.Reflection.MethodInfo? method, string? prefix = null, string? postfix = null)
        {
            if (method == null) return false;
            try
            {
                harmony.Patch(method,
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(ForwardedHitPatches), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(ForwardedHitPatches), postfix));
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[ForwardedHit] patching {method.DeclaringType?.Name}.{method.Name} failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static void ExplosionManager_Update_Pre(ExplosionManager __instance)
        {
            try { EnemyExplosionRelay.OnHostExplosionsResolving(__instance); }
            catch { }
        }

        // CLIENT: the copy's hit, when the forwarded hit of the same attack came first, takes its answer instead of a
        // second test (so one attack never spends two parry charges). __state = answered here.
        private static bool TryParryWithWeapon_Pre(Unit __instance, MeleeParryTypes parryType, Unit attackingUnit, ref bool __result, out bool __state)
        {
            __state = false;
            try
            {
                if (!IsCopyHitOnLocalPlayer(__instance, attackingUnit)) return true;
                if (!ForwardedHitParry.TryAnswerCopyParry(attackingUnit, (byte)parryType, out bool parried)) return true;
                __state = true;
                __result = parried;
                return false;
            }
            catch { return true; }
        }

        private static void TryParryWithWeapon_Post(Unit __instance, bool __result, MeleeParryTypes parryType, Unit attackingUnit, bool __state)
        {
            try
            {
                NetMode mode = NetConfig.GetMode();
                if (mode == NetMode.Host)
                {
                    // The game offered the stand-in a parry: the damage that follows in this hit is parryable.
                    if (RemotePlayerTargetProxyManager.IsProxyUnit(__instance))
                        CopyDamagePatches.NoteParryOpportunity((byte)parryType);
                    return;
                }
                if (__state || !IsCopyHitOnLocalPlayer(__instance, attackingUnit)) return;
                ForwardedHitParry.RecordCopyResult(attackingUnit, (byte)parryType, __result);
            }
            catch { }
        }

        private static bool IsCopyHitOnLocalPlayer(Unit victim, Unit attacker)
        {
            if (attacker == null || ForwardedHitParry.InForwardTest) return false;
            if (NetConfig.GetMode() != NetMode.Client || !NetGameplaySyncBridge.IsSessionActive) return false;
            if (!NetPlayerLifeManager.IsLocalPlayerUnit(victim)) return false;
            return NetGameplayProbeManager.TryGetCopyHostSpawnIndex(attacker, out _);
        }
    }
}
