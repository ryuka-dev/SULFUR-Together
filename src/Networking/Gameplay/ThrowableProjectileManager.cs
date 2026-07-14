using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Unity.Mathematics;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Items;
using PerfectRandom.Sulfur.Core.Stats;
using PerfectRandom.Sulfur.Core.Units;
using PerfectRandom.Sulfur.Core.Weapons;
using SULFURTogether.Networking.Gameplay.Boss;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// K-1 (issue #10) projectile-path throwable sync — ThrowingKnives.
    /// <para>Capture (throwing peer): a <c>ProjectileSystem.StartProjectile</c> prefix, gated by the
    /// <c>ThrowableWeapon.Throw</c> in-flight flag, snapshots the real <c>Custom</c> projectile ray the game built (its
    /// canonical source — no throw-math reconstruction) and broadcasts the weapon ItemId + that ray.</para>
    /// <para>Replay (receiving peer): rebuild ONE damage-stripped <c>Custom</c> projectile through the real
    /// <c>ProjectileSystem.StartProjectile</c>, passing the weapon's resolved <c>customVisuals</c> (the knife has
    /// <c>drawDefaultBullet == false</c>, so a null visual would render nothing). Empty <c>damageComps</c> +
    /// <c>explicitDamage 0</c> → zero damage (verified safe via <c>ProjectileUtilities.ProcessUnitHit</c>). VISUAL ONLY —
    /// damage stays host-authoritative on the existing hit route.</para>
    /// </summary>
    internal static class ThrowableProjectileManager
    {
        // Non-zero sentinel owner id (StartProjectile rejects ownerInstID == 0); distinct from the bullet channel's.
        // No real collider has it, and no damage is ever applied, so it can never self-hit or hurt anyone.
        private const int VisualOwnerInstId = unchecked((int)0x5710B02F);

        private static int _captureSeq;
        private static FieldInfo _customVisualsField; // ThrowableWeapon.customVisuals (private)

        // ----------------------------------------------------------------- capture (throwing peer)

        public static void CaptureLocalThrow(ProjectileRay projState, ProjectileData projData)
        {
            try
            {
                if (!Plugin.Cfg.EnablePlayerThrowableProjectileSync.Value) return;
                if (!NetGameplaySyncBridge.IsSessionActive) return; // skip all work in solo play

                Unit owner = projData.sourceUnit;
                if (owner == null || !owner.isPlayer) return;
                if (!NetPlayerLifeManager.IsLocalPlayerUnit(owner)) return;

                Weapon weapon = projData.sourceWeapon;
                if (weapon == null) return;

                int itemId = ReadItemId(weapon);
                if (itemId == 0) return;

                var msg = new NetThrowableProjectile
                {
                    Sequence           = ++_captureSeq,
                    ItemIdValue        = itemId,
                    StartPos           = ToVector3(projState.barrelPosition),
                    Velocity           = ToVector3(projState.velocity),
                    Mass               = projState.mass,
                    Drag               = projState.drag,
                    StickToGeometry    = projState.stickToGeometry,
                    Caliber            = (int)projData.caliber,
                    DamageType         = (int)projData.damageType,
                    BarrelLengthOffset = projState.barrelLengthOffset,
                };

                NetGameplaySyncBridge.ReportLocalThrowableProjectile(msg);

                if (Plugin.Cfg.LogPlayerThrowableProjectileSync.Value)
                    NetLogger.Info($"[ThrowableProjectile] capture weapon={weapon.name} itemId={itemId} start={msg.StartPos} vel={msg.Velocity.magnitude:F1} stick={msg.StickToGeometry}");
            }
            catch (Exception ex)
            {
                NetLogger.Warn($"[ThrowableProjectile] capture failed: {ex.Message}");
            }
        }

        // ----------------------------------------------------------------- replay (receiving peer)

        public static void ApplyRemoteThrow(NetThrowableProjectile m)
        {
            try
            {
                if (!Plugin.Cfg.EnablePlayerThrowableProjectileSync.Value) return;
                if (m == null) return;

                ProjectileSystem projSys = ProjectileSystem.Instance;
                if (projSys == null) return;

                ProjectileCustomVisuals cv = ResolveCustomVisuals(m.ItemIdValue);

                float3 o = new float3(m.StartPos.x, m.StartPos.y, m.StartPos.z);
                // Mirror ThrowableWeapon.Throw's projectile branch exactly, minus the real damage.
                ProjectileRay ray = new ProjectileRay(o);
                ray.type                 = ProjectileTypes.Custom;
                ray.velocity             = new float3(m.Velocity.x, m.Velocity.y, m.Velocity.z);
                ray.shotOrLastBounceFrom = o;
                ray.barrelPosition       = o;
                ray.startTime            = Time.time;
                ray.mass                 = m.Mass > 0f ? m.Mass : 1f;
                ray.drag                 = m.Drag;
                ray.gravity              = ProjectileRay.GRAVITY;
                ray.radius               = 0.05f;
                ray.stickToGeometry      = m.StickToGeometry;
                ray.ownerInstID          = VisualOwnerInstId;
                ray.drawDefaultBullet    = false;
                ray.barrelLengthOffset   = m.BarrelLengthOffset;
                // damageComps intentionally left EMPTY → ProcessUnitHit applies zero damage. VISUAL ONLY.

                ProjectileData data = new ProjectileData
                {
                    damageType     = (DamageTypes)m.DamageType,
                    caliber        = (CaliberTypes)m.Caliber,
                    isPlayer       = false,
                    explicitDamage = 0f,
                };

                projSys.StartProjectile(ray, data, cv);

                if (Plugin.Cfg.LogPlayerThrowableProjectileSync.Value)
                    NetLogger.Info($"[ThrowableProjectile] replay peer={m.PeerId} itemId={m.ItemIdValue} start={m.StartPos} customVisuals={(cv != null ? cv.name : "<null>")}");
            }
            catch (Exception ex)
            {
                NetLogger.Warn($"[ThrowableProjectile] replay failed: {ex.Message}");
            }
        }

        // ----------------------------------------------------------------- helpers

        // ItemId → weapon prefab → its ThrowableWeapon.customVisuals (the knife's visible body prefab). Same asset on
        // every peer since it's a serialized field on the item-database prefab.
        private static ProjectileCustomVisuals ResolveCustomVisuals(int itemIdValue)
        {
            var weaponPrefab = RuntimeSpawnManager.ResolveItemPrefab(itemIdValue);
            var tw = weaponPrefab != null ? weaponPrefab.GetComponent<ThrowableWeapon>() : null;
            if (tw == null) return null;
            _customVisualsField ??= AccessTools.Field(typeof(ThrowableWeapon), "customVisuals");
            return _customVisualsField?.GetValue(tw) as ProjectileCustomVisuals;
        }

        private static int ReadItemId(Weapon weapon)
        {
            try
            {
                if (BossReflect.GetMember(weapon, "ItemDefinition") is ItemDefinition def)
                    return def.id.value;
            }
            catch { }
            return 0;
        }

        private static Vector3 ToVector3(float3 v) => new Vector3(v.x, v.y, v.z);
    }
}
