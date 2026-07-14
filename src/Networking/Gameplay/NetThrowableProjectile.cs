using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// K-1 (issue #10) projectile-path throwable flight sync — ThrowingKnives and any throwable whose
    /// <c>ThrowableWeapon.Throw</c> takes the <c>prefabToThrow == null</c> branch (a real <c>ProjectileSystem</c>
    /// projectile of type <c>Custom</c>, NOT a Breakable — so HZ-2/HZ-3 don't apply to it).
    /// <para>The knife's only visible body is the weapon's serialized <c>customVisuals</c> prefab
    /// (<c>drawDefaultBullet == false</c>), so — unlike the bullet channel (<see cref="NetPlayerWeaponFire"/>) — a null
    /// custom-visual would render nothing. We carry the throwing weapon's <c>ItemId</c> so every peer resolves the same
    /// <c>customVisuals</c> (probe-confirmed: no <c>lootOnDestroy</c>, AutoPool-registered), plus the exact launch ray
    /// captured at <c>ProjectileSystem.StartProjectile</c>. Receivers replay one damage-stripped <c>Custom</c> projectile
    /// (empty <c>damageComps</c> + <c>explicitDamage 0</c>) through the real projectile system, so flight + stick match
    /// natively. VISUAL ONLY — knife damage stays on the existing host-authoritative hit route.</para>
    /// <para>Peer-broadcast, same topology as the bullet channel: the throwing peer never replays its own (it has the
    /// real knife); Client→Host→relay, receivers skip their own PeerId.</para>
    /// </summary>
    internal sealed class NetThrowableProjectile
    {
        // Which player threw it (stamped by the Host from the source peer).
        public string PeerId { get; set; } = "";

        // Scene context (a receiver in a different level ignores it).
        public string ChapterName  { get; set; } = "";
        public int    LevelIndex   { get; set; } = -1;
        public bool   HasLevelSeed { get; set; }
        public int    LevelSeed    { get; set; }

        // De-dup / ordering.
        public int   Sequence { get; set; }
        public float SentAt   { get; set; }

        // The throwing weapon's ItemId (ushort value) — resolves the weapon's customVisuals prefab on every peer.
        public int ItemIdValue { get; set; }

        // Exact launch ray captured on the owner (StartProjectile). Everything needed to rebuild a faithful, damage-free
        // flight; gravity is the shared constant ProjectileRay.GRAVITY and radius is the game's fixed 0.05 for throwables.
        public Vector3 StartPos           { get; set; }
        public Vector3 Velocity           { get; set; }
        public float   Mass               { get; set; }
        public float   Drag               { get; set; }
        public bool    StickToGeometry    { get; set; }
        public int     Caliber            { get; set; }
        public int     DamageType         { get; set; } // impact fx only — replay applies zero damage
        public float   BarrelLengthOffset { get; set; }

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
