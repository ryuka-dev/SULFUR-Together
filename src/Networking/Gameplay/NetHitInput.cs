using System;
using LiteNetLib.Utils;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// HR-1: what a client knows about ONE damage component of one of its own hits — the inputs the game's
    /// <c>Unit.ReceiveDamage</c> reads, and nothing it decides. The host turns this back into a
    /// <c>DamageSourceData</c> + <c>Hitmesh.Data</c> and lets the game settle the hit (crit roll, hitbox multiplier,
    /// headshot bonus, resistances, invulnerability, onDamageRecieved). See <see cref="RemoteHitResolver"/>.
    ///
    /// <para>One instance per <c>Npc.ReceiveDamage</c> call: a projectile with several damage components (base + fire
    /// + dark, ...) calls ReceiveDamage once per component, and each call becomes its own request.</para>
    ///
    /// <para>Project-owned on purpose: the wire carries plain numbers, and only the client capture
    /// (<see cref="NetHitInputCapture"/>) and the host resolver touch the game's structs.</para>
    /// </summary>
    internal struct NetHitInput
    {
        /// <summary>The damage argument of the suppressed ReceiveDamage call — before crit, hitbox and resistances.</summary>
        public float RawDamage;
        /// <summary><c>DamageTypes</c> value (the enum is <c>: byte</c>).</summary>
        public byte DamageType;
        /// <summary>Chance in [0,1] the host rolls for a crit (vanilla: <c>DamageSourceData.critChance</c>).</summary>
        public float CritChance;
        public bool Melee;
        /// <summary><c>HitboxColliders.ShapeId.partAndIndex</c>; 0 = no hitbox (explosion, DoT, ...).</summary>
        public ushort ShapeId;
        /// <summary>The hit point relative to the target's transform — the client puppet and the host unit are not at
        /// exactly the same world position, so a world point would land beside the host unit.</summary>
        public bool HasHitOffset;
        public Vector3 HitOffset;
        /// <summary><c>AttackStates.increaseHeadshotDamage</c> (the weapon's headshot enchantment factor; 0 = none).</summary>
        public float IncreaseHeadshotDamage;
        /// <summary><c>AttackStates.effectScale</c>.</summary>
        public float EffectScale;
        /// <summary>The boolean <c>AttackStates</c> flags, see <see cref="NetHitStateFlags"/>.</summary>
        public ushort StateFlags;
        /// <summary><c>AttackStates.replacesBloodType</c> (<c>DecalType</c>).</summary>
        public int ReplacesBloodType;
        /// <summary><c>FiredProjectileState.caliberType</c> (<c>CaliberTypes : byte</c>).</summary>
        public byte CaliberType;
        /// <summary><c>FiredProjectileState.effect</c> (<c>ProjectileEffect</c>).</summary>
        public int ProjectileEffect;
        /// <summary>The weapon's flat bonus against Ghost-type units, <c>weapon.Damage × ExtraDamageGhost</c>, computed
        /// by the client because the host has no weapon object. 0 unless the target is a Ghost and the weapon has it.</summary>
        public float GhostBonus;
        /// <summary>HR-3: projectiles the weapon fired in the shot this hit came from
        /// (<c>Weapon.lastAmountShotProjectiles</c>, at least 1; 1 without a weapon). Vanilla Voodoo spreads one share per
        /// projectile and reads it from the weapon object, which the host does not have.</summary>
        public byte ProjectileCount;
    }

    /// <summary>The boolean fields of the game's <c>AttackStates</c>. <c>isPeriodicModifier</c> is deliberately absent:
    /// a client never forwards a damage-over-time tick (the host runs the status itself).</summary>
    [Flags]
    internal enum NetHitStateFlags : ushort
    {
        None                    = 0,
        AlwaysSpawnOrgans       = 1 << 0,
        CorpseBounceExplosion   = 1 << 1,
        FoulFateBounceExplosion = 1 << 2,
        IsSilenced              = 1 << 3,
        NotCreatedByPlayer      = 1 << 4,
        DropDisabledMoney       = 1 << 5,
        DropDisabledOrgan       = 1 << 6,
        DisableBloodOnHit       = 1 << 7,
        IsHealingAllyBullet     = 1 << 8,
        All                     = (1 << 9) - 1,
    }

    internal static class NetHitInputCodec
    {
        public static void Write(NetDataWriter w, in NetHitInput h)
        {
            w.Put(h.RawDamage);
            w.Put(h.DamageType);
            w.Put(h.CritChance);
            w.Put(h.Melee);
            w.Put(h.ShapeId);
            w.Put(h.HasHitOffset);
            if (h.HasHitOffset)
            {
                w.Put(h.HitOffset.x);
                w.Put(h.HitOffset.y);
                w.Put(h.HitOffset.z);
            }
            w.Put(h.IncreaseHeadshotDamage);
            w.Put(h.EffectScale);
            w.Put(h.StateFlags);
            w.Put(h.ReplacesBloodType);
            w.Put(h.CaliberType);
            w.Put(h.ProjectileEffect);
            w.Put(h.GhostBonus);
            w.Put(h.ProjectileCount);
        }

        public static NetHitInput Read(NetDataReader r)
        {
            var h = new NetHitInput
            {
                RawDamage    = r.GetFloat(),
                DamageType   = r.GetByte(),
                CritChance   = r.GetFloat(),
                Melee        = r.GetBool(),
                ShapeId      = r.GetUShort(),
                HasHitOffset = r.GetBool(),
            };
            if (h.HasHitOffset) h.HitOffset = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
            h.IncreaseHeadshotDamage = r.GetFloat();
            h.EffectScale            = r.GetFloat();
            h.StateFlags             = r.GetUShort();
            h.ReplacesBloodType      = r.GetInt();
            h.CaliberType            = r.GetByte();
            h.ProjectileEffect       = r.GetInt();
            h.GhostBonus             = r.GetFloat();
            h.ProjectileCount        = r.GetByte();
            return h;
        }
    }
}
