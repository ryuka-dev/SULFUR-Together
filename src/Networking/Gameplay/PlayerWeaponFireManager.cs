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
using PerfectRandom.Sulfur.Core.Utilities;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase 5.6-WS player weapon bullet sync.
    /// <para>Capture (firing peer): on every <c>Weapon.Shoot()</c> by the LOCAL player, read the computed projectile
    /// template (<c>equipmentManager.lastFiredProjectile.ray</c>) + barrage count/spread/aim and broadcast one event.</para>
    /// <para>Replay (receiving peer): rebuild the barrage through the game's real <c>ProjectileSystem.StartProjectile</c>
    /// with damage stripped (empty <c>damageComps</c> + <c>explicitDamage = 0</c> → zero damage, verified safe via
    /// <c>ProjectileUtilities.ProcessUnitHit</c>). VISUAL ONLY — damage stays host-authoritative.</para>
    /// </summary>
    internal static class PlayerWeaponFireManager
    {
        // Non-zero sentinel owner id (StartProjectile rejects ownerInstID == 0). No real collider has this id, so the
        // replayed bullet never self-excludes a real hitbox; it is harmless because no damage is ever applied anyway.
        private const int VisualOwnerInstId = unchecked((int)0x5710B017);

        private static int _captureSeq;

        // Weapon.iMaxAmmoPerShot is private — cached reflection so god-mode / ConsumeAmmoChance-miss shots (which fire
        // projectiles without decrementing ammo, leaving lastConsumptionAmount == 0) still compute the real shot count.
        private static FieldInfo _maxAmmoPerShotField;
        private static bool _maxAmmoPerShotFieldResolved;

        // ----------------------------------------------------------------- capture (firing peer)

        public static void CaptureLocalFire(Weapon weapon)
        {
            try
            {
                if (!Plugin.Cfg.EnablePlayerWeaponSync.Value) return;
                if (!NetGameplaySyncBridge.IsSessionActive) return; // skip all work in solo play
                if (weapon == null) return;

                Unit owner = weapon.SourceUnit;
                if (owner == null || !owner.isPlayer) return;
                if (!NetPlayerLifeManager.IsLocalPlayerUnit(owner)) return;

                WeaponSO def = weapon.weaponDefinition;
                if (def == null || def.IsMelee) return;

                ItemStats stats = weapon.ItemStats;
                if (stats == null) return;

                int projAmount = (int)stats.GetAttribute(ItemAttributes.ProjectileAmount);
                if (projAmount <= 0) return;

                // Bullets fired this shot. Normal play: lastConsumptionAmount (= ammo consumed = num3). God mode /
                // ConsumeAmmoChance-miss: ammo isn't decremented so lastConsumptionAmount stays 0 — fall back to
                // min(iAmmoCurrent, iMaxAmmoPerShot), which (ammo unchanged) still equals the real num3.
                int shots = weapon.lastConsumptionAmount;
                if (shots <= 0) shots = ResolveShotsFromAmmo(weapon);
                if (shots <= 0) return; // nothing actually fired

                if (stats.GetAttribute(ItemAttributes.EnchantmentRemoveBullets) > 0f) return; // barrage produces no bullets

                bool isSpray = stats.GetAttribute(ItemAttributes.ProjectileMoveAsSpray) > 0f;
                int count = projAmount * shots;
                if (isSpray) count *= 7; // mirrors Weapon.Shoot's SprayProjectiles(num9 * 7)
                if (count <= 0) return;

                EquipmentManager em = weapon.equipmentManager;
                if (em == null) return;

                FullProjectileDescription last = em.lastFiredProjectile;
                ProjectileRay ray = last.ray;
                if (!ray.IsValid()) return; // last shot didn't actually dispatch a projectile

                Vector3 origin = ToVector3(ray.barrelPosition); // == BarrelTransform.position at dispatch time
                Vector3 aim = weapon.AimAtPosition;

                float speed = math.length(ray.velocity);
                if (speed < 0.01f) speed = weapon.bulletSpeed;

                var msg = new NetPlayerWeaponFire
                {
                    Sequence = ++_captureSeq,
                    Origin   = origin,
                    AimPoint = aim,
                    Count    = count,
                    Speed    = speed,
                    Spread   = weapon.computedSpread,
                    IsSpray  = isSpray,
                    Homing   = stats.GetAttribute(ItemAttributes.Homing) > 0f,
                    IsRaygun = last.isRaygun,

                    ProjectileType = (int)ray.type,
                    Caliber        = (int)last.data.caliber,
                    Effect         = (int)ray.effect,
                    VfxAsset       = (int)ray.vfxAsset,
                    DamageType     = (int)last.data.damageType,

                    ColorR = ray.color.x, ColorG = ray.color.y, ColorB = ray.color.z,
                    CoreColorR = ray.coreColor.x, CoreColorG = ray.coreColor.y, CoreColorB = ray.coreColor.z,

                    InnerWidth = ray.innerWidth, OuterWidth = ray.outerWidth, Radius = ray.radius,

                    Mass = ray.mass, Drag = ray.drag,
                    GravityX = ray.gravity.x, GravityY = ray.gravity.y, GravityZ = ray.gravity.z,

                    TimeScale = ray.timeScale, LifeTime = ray.lifeTime,
                    BehaviourTimeout = ray.behaviourTimeout, BarrelLengthOffset = ray.barrelLengthOffset,

                    BounceHits = ray.bounceHits, Bounciness = ray.bounciness,
                    PenetrationBehavior = (int)ray.penetrationBehavior,
                    PenetrationsLeft = ray.penetrationsLeft,
                    PenetrationDamageMultiplier = ray.penetrationDamageMultiplier,

                    DrawDefaultBullet = ray.drawDefaultBullet,
                    DrawLaserBeam     = ray.drawLaserBeam,
                    IsRocket          = ray.isRocket,
                    HasCustomColors   = ray.hasCustomColors,
                    HasCustomTrail    = ray.hasCustomTrail,
                    CreateBulletHoles = ray.createBulletHoles,
                    StickToGeometry   = ray.stickToGeometry,
                    PlayImpactSounds  = ray.playImpactSounds,
                };

                NetGameplaySyncBridge.ReportLocalPlayerWeaponFire(msg);

                if (Plugin.Cfg.LogPlayerWeaponSync.Value)
                    NetLogger.Info($"[PlayerWeaponFire] capture weapon={def.name} count={count} spray={isSpray} homing={msg.Homing} raygun={msg.IsRaygun} type={(ProjectileTypes)msg.ProjectileType} cal={(CaliberTypes)msg.Caliber} speed={speed:F1} spread={msg.Spread:F3}");
            }
            catch (Exception ex)
            {
                NetLogger.Warn($"[PlayerWeaponFire] capture failed: {ex.Message}");
            }
        }

        // ----------------------------------------------------------------- replay (receiving peer)

        public static void Replay(NetPlayerWeaponFire m)
        {
            try
            {
                if (!Plugin.Cfg.EnablePlayerWeaponSync.Value) return;
                if (m == null || m.Count <= 0) return;

                ProjectileSystem projSys = ProjectileSystem.Instance;
                if (projSys == null) return;

                int maxCount = Plugin.Cfg.PlayerWeaponSyncMaxProjectilesPerShot.Value;
                if (maxCount < 1) maxCount = 1;
                int count = Mathf.Min(m.Count, maxCount);

                Vector3 origin = m.Origin;
                Vector3 central = m.AimPoint - origin;
                if (central.sqrMagnitude < 1e-6f) central = Vector3.forward;
                central.Normalize();

                Npc homingTarget = m.Homing ? FindHomingTarget(origin, central) : null;
                RailgunSystem railgun = m.IsRaygun ? RailgunSystem.Instance : null;

                int spawned = 0;
                for (int i = 0; i < count; i++)
                {
                    Vector3 dir = (m.Spread > 0f)
                        ? Helpers.GetRandomDirectionInCode(m.Spread, central)
                        : central;

                    float speed = m.Speed;
                    if (m.IsSpray) speed *= UnityEngine.Random.Range(0.7f, 1.3f);

                    ProjectileRay ray = BuildRay(m, origin, dir, speed);
                    ProjectileData data = BuildData(m, homingTarget);

                    if (railgun != null) railgun.FireRailgun(ray, data);
                    else projSys.StartProjectile(ray, data, null);
                    spawned++;
                }

                if (Plugin.Cfg.LogPlayerWeaponSync.Value)
                    NetLogger.Info($"[PlayerWeaponFire] replay peer={m.PeerId} spawned={spawned}/{m.Count} type={(ProjectileTypes)m.ProjectileType} raygun={m.IsRaygun} homing={(homingTarget != null)}");
            }
            catch (Exception ex)
            {
                NetLogger.Warn($"[PlayerWeaponFire] replay failed: {ex.Message}");
            }
        }

        private static ProjectileRay BuildRay(NetPlayerWeaponFire m, Vector3 origin, Vector3 dir, float speed)
        {
            float3 o = new float3(origin.x, origin.y, origin.z);
            ProjectileRay ray = new ProjectileRay(o, (ProjectileTypes)m.ProjectileType);

            ray.velocity   = new float3(dir.x * speed, dir.y * speed, dir.z * speed);
            ray.color      = new float3(m.ColorR, m.ColorG, m.ColorB);
            ray.coreColor  = new float3(m.CoreColorR, m.CoreColorG, m.CoreColorB);
            ray.innerWidth = m.InnerWidth;
            ray.outerWidth = m.OuterWidth;
            ray.radius     = m.Radius > 0f ? m.Radius : 0.05f;
            ray.mass       = m.Mass > 0f ? m.Mass : ray.mass;
            ray.drag       = m.Drag;
            ray.gravity    = new float3(m.GravityX, m.GravityY, m.GravityZ);
            ray.timeScale  = m.TimeScale > 0f ? m.TimeScale : 1f;
            ray.lifeTime   = m.LifeTime > 0f ? m.LifeTime : 30f;
            ray.behaviourTimeout   = m.BehaviourTimeout;
            ray.barrelLengthOffset = m.BarrelLengthOffset;

            ray.effect   = (ProjectileEffect)m.Effect;
            ray.vfxAsset = (VFX_Persistent)m.VfxAsset;
            ray.isRocket = m.IsRocket;

            ray.drawDefaultBullet = m.DrawDefaultBullet;
            ray.drawLaserBeam     = m.DrawLaserBeam;
            ray.hasCustomColors   = m.HasCustomColors;
            ray.hasCustomTrail    = m.HasCustomTrail;
            ray.createBulletHoles = m.CreateBulletHoles;
            ray.stickToGeometry   = m.StickToGeometry;
            ray.playImpactSounds  = m.PlayImpactSounds;

            ray.bounceHits  = m.BounceHits;
            ray.bounciness  = m.Bounciness;
            ray.penetrationBehavior = (PenetrationBehavior)m.PenetrationBehavior;
            ray.penetrationsLeft    = m.PenetrationsLeft;
            ray.penetrationDamageMultiplier = m.PenetrationDamageMultiplier;

            ray.shotOrLastBounceFrom = o;
            ray.barrelPosition       = o;
            ray.startTime  = Time.time;
            ray.ownerInstID = VisualOwnerInstId;
            // damageComps intentionally left EMPTY → ProcessUnitHit applies zero damage. VISUAL ONLY.
            return ray;
        }

        private static ProjectileData BuildData(NetPlayerWeaponFire m, Npc homingTarget)
        {
            return new ProjectileData
            {
                damageType     = (DamageTypes)m.DamageType,
                caliber        = (CaliberTypes)m.Caliber,
                isPlayer       = false,
                explicitDamage = 0f,
                homingTarget   = homingTarget,
            };
        }

        /// <summary>Receiver-local homing: pick the nearest alive hostile Npc roughly in front of the shot ray.</summary>
        private static Npc FindHomingTarget(Vector3 origin, Vector3 dir)
        {
            try
            {
                GameManager gm = GameManager.Instance;
                if (gm == null || gm.aliveNpcs == null) return null;

                Npc best = null;
                float bestScore = float.MaxValue;
                const float maxDist = 35f;

                foreach (Npc npc in gm.aliveNpcs)
                {
                    if (npc == null || npc.transform == null) continue;
                    if (npc.IsProtectedNpc || npc.IsPlayerFaction) continue;

                    Vector3 to = npc.transform.position + Vector3.up - origin;
                    float dist = to.magnitude;
                    if (dist > maxDist || dist < 0.01f) continue;
                    if (Vector3.Dot(dir, to / dist) < 0.5f) continue; // ~60° cone in front

                    if (dist < bestScore)
                    {
                        bestScore = dist;
                        best = npc;
                    }
                }
                return best;
            }
            catch { return null; }
        }

        // num3 = min(iAmmoCurrent, iMaxAmmoPerShot) — see Weapon.Shoot. Used only when lastConsumptionAmount == 0.
        private static int ResolveShotsFromAmmo(Weapon weapon)
        {
            try
            {
                if (!_maxAmmoPerShotFieldResolved)
                {
                    _maxAmmoPerShotFieldResolved = true;
                    _maxAmmoPerShotField = AccessTools.Field(typeof(Weapon), "iMaxAmmoPerShot");
                }
                int current = weapon.iAmmoCurrent;
                if (current <= 0) return 0;
                if (_maxAmmoPerShotField == null) return current;
                int maxPerShot = (int)_maxAmmoPerShotField.GetValue(weapon);
                if (maxPerShot <= 0) return current;
                return Mathf.Min(current, maxPerShot);
            }
            catch { return 0; }
        }

        private static Vector3 ToVector3(float3 v) => new Vector3(v.x, v.y, v.z);
    }
}
