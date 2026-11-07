using System;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Stats;
using PerfectRandom.Sulfur.Core.Units;
using PerfectRandom.Sulfur.Core.Utilities;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// HR-1, client side: read one suppressed <c>Npc.ReceiveDamage(float, DamageSourceData, Hitmesh.Data, Vector3?)</c>
    /// call into a <see cref="NetHitInput"/>. Everything the game's damage settlement reads comes from the
    /// <c>DamageSourceData</c> the weapon already filled — the only value that needs the weapon object itself is the
    /// Ghost-type bonus (<c>weapon.Damage × ExtraDamageGhost</c>), which the host cannot compute without it.
    /// </summary>
    internal static class NetHitInputCapture
    {
        /// <param name="args">The patched call's <c>__args</c>: [damage, DamageSourceData, Hitmesh.Data, Vector3?].</param>
        public static bool TryCapture(object target, float damage, object[]? args, out NetHitInput hit)
        {
            hit = default;
            if (args == null || args.Length < 3) return false;
            if (args[1] is not DamageSourceData sd) return false;
            Hitmesh.Data hitbox = args[2] is Hitmesh.Data hb ? hb : Hitmesh.Data.Default;
            // A boxed Vector3? arrives as either null or a boxed Vector3.
            bool hasPos = args.Length > 3 && args[3] is Vector3;
            Vector3 worldPos = hasPos ? (Vector3)args[3] : Vector3.zero;

            var s = sd.states;
            NetHitStateFlags flags = NetHitStateFlags.None;
            if (s.alwaysSpawnOrgans)       flags |= NetHitStateFlags.AlwaysSpawnOrgans;
            if (s.corpseBounceExplosion)   flags |= NetHitStateFlags.CorpseBounceExplosion;
            if (s.foulFateBounceExplosion) flags |= NetHitStateFlags.FoulFateBounceExplosion;
            if (s.isSilenced)              flags |= NetHitStateFlags.IsSilenced;
            if (s.notCreatedByPlayer)      flags |= NetHitStateFlags.NotCreatedByPlayer;
            if (s.dropDisabledMoney)       flags |= NetHitStateFlags.DropDisabledMoney;
            if (s.dropDisabledOrgan)       flags |= NetHitStateFlags.DropDisabledOrgan;
            if (s.disableBloodOnHit)       flags |= NetHitStateFlags.DisableBloodOnHit;
            if (s.isHealingAllyBullet)     flags |= NetHitStateFlags.IsHealingAllyBullet;

            hit = new NetHitInput
            {
                RawDamage              = damage,
                DamageType             = (byte)sd.damageType,
                CritChance             = sd.critChance,
                Melee                  = sd.melee,
                ShapeId                = hitbox.shapeId.partAndIndex,
                IncreaseHeadshotDamage = s.increaseHeadshotDamage,
                EffectScale            = s.effectScale,
                StateFlags             = (ushort)flags,
                ReplacesBloodType      = (int)s.replacesBloodType,
                CaliberType            = (byte)sd.projectile.caliberType,
                ProjectileEffect       = (int)sd.projectile.effect,
                GhostBonus             = ReadGhostBonus(target, sd),
            };

            if (hasPos && target is Component c && c != null)
            {
                hit.HasHitOffset = true;
                hit.HitOffset    = c.transform.InverseTransformPoint(worldPos);
            }
            return true;
        }

        /// <summary>Vanilla <c>Unit.ReceiveDamage</c>: a Ghost-type target hit by a weapon with ExtraDamageGhost takes
        /// <c>weapon.Damage × value</c> on top, after the crit and Oily doubling and before the hitbox multiplier.</summary>
        private static float ReadGhostBonus(object target, in DamageSourceData sd)
        {
            try
            {
                if (target is not Unit u || u == null || u.unitSO == null) return 0f;
                if (u.unitSO.unitType != UnitType.Ghost) return 0f;
                var weapon = sd.sourceWeapon;
                if (weapon == null || weapon.ItemStats == null) return 0f;
                if (!weapon.ItemStats.TryGetAttribute(ItemAttributes.ExtraDamageGhost, out float value) || !(value > 0f)) return 0f;
                return weapon.Damage * value;
            }
            catch (Exception) { return 0f; }
        }
    }
}
