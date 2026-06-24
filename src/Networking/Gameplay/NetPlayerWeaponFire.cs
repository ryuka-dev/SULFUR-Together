using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase 5.6-WS player weapon bullet sync — one fire event per trigger pull (one <c>Weapon.Shoot()</c>).
    /// The firing peer captures the computed projectile template from
    /// <c>equipmentManager.lastFiredProjectile.ray</c> (every bullet in the barrage shares the same visual
    /// fields; only the per-bullet spread direction differs) plus the barrage count / spread / aim.
    /// Receivers replay the whole barrage through the game's real <c>ProjectileSystem.StartProjectile</c>
    /// with damage stripped (empty damageComps + explicitDamage = 0 → zero damage). VISUAL ONLY.
    /// </summary>
    internal sealed class NetPlayerWeaponFire
    {
        // Identity — which player fired (stamped by the Host from the source peer).
        public string PeerId { get; set; } = "";

        // Scene context (so a receiver in a different level ignores it).
        public string ChapterName  { get; set; } = "";
        public int    LevelIndex   { get; set; } = -1;
        public bool   HasLevelSeed { get; set; }
        public int    LevelSeed    { get; set; }

        // De-dup / ordering.
        public int   Sequence { get; set; }
        public float SentAt   { get; set; }

        // ---- Barrage geometry ----
        public Vector3 Origin   { get; set; } // BarrelTransform.position
        public Vector3 AimPoint { get; set; } // central aim point (BarrelTransform looks here before per-bullet spread)
        public int     Count    { get; set; } // number of projectiles fired this trigger pull
        public float   Speed    { get; set; } // effective per-bullet speed (template velocity magnitude)
        public float   Spread   { get; set; } // computedSpread (cone half-angle used by Helpers.GetRandomDirectionInCode)
        public bool    IsSpray  { get; set; } // ProjectileMoveAsSpray (per-bullet 0.7..1.3 speed jitter)
        public bool    Homing   { get; set; } // Homing > 0 — receiver re-selects a local target
        public bool    IsRaygun { get; set; } // ProjectileMoveAsLight / railgun → RailgunSystem.FireRailgun

        // ---- Projectile visual template (from the captured ProjectileRay) ----
        public int     ProjectileType { get; set; } // ProjectileTypes (int)
        public int     Caliber        { get; set; } // CaliberTypes (int)
        public int     Effect         { get; set; } // ProjectileEffect (int)
        public int     VfxAsset       { get; set; } // VFX_Persistent (int)
        public int     DamageType     { get; set; } // DamageTypes (int) — impact blood/fx only (no damage applied)

        public float   ColorR { get; set; }
        public float   ColorG { get; set; }
        public float   ColorB { get; set; }
        public float   CoreColorR { get; set; }
        public float   CoreColorG { get; set; }
        public float   CoreColorB { get; set; }

        public float   InnerWidth { get; set; }
        public float   OuterWidth { get; set; }
        public float   Radius     { get; set; }

        public float   Mass     { get; set; }
        public float   Drag     { get; set; }
        public float   GravityX { get; set; }
        public float   GravityY { get; set; }
        public float   GravityZ { get; set; }

        public float   TimeScale         { get; set; }
        public float   LifeTime          { get; set; }
        public float   BehaviourTimeout  { get; set; }
        public float   BarrelLengthOffset { get; set; }

        public int     BounceHits  { get; set; }
        public float   Bounciness  { get; set; }
        public int     PenetrationBehavior { get; set; }
        public int     PenetrationsLeft    { get; set; }
        public float   PenetrationDamageMultiplier { get; set; }

        // Visual flags.
        public bool DrawDefaultBullet { get; set; }
        public bool DrawLaserBeam     { get; set; }
        public bool IsRocket          { get; set; }
        public bool HasCustomColors   { get; set; }
        public bool HasCustomTrail    { get; set; }
        public bool CreateBulletHoles { get; set; }
        public bool StickToGeometry   { get; set; }
        public bool PlayImpactSounds  { get; set; }

        public bool MatchesScene(NetRunState localState)
        {
            if (!localState.HasLevel) return false;
            if (!string.Equals(localState.ChapterName, ChapterName, System.StringComparison.Ordinal)) return false;
            if (localState.LevelIndex != LevelIndex) return false;
            if (Plugin.Cfg.EnableLevelSeedAuthority.Value)
            {
                if (!HasLevelSeed || !localState.HasLevelSeed) return false;
                if (localState.LevelSeed != LevelSeed) return false;
            }
            return true;
        }
    }
}
