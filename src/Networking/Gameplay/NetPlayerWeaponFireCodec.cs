using LiteNetLib.Utils;

namespace SULFURTogether.Networking.Gameplay
{
    internal static class NetPlayerWeaponFireCodec
    {
        // 2: WS-SND-1 appended the shot sounds, WS-SND-2 the beam loop transition (both before any release of v2).
        private const byte Version = 2;

        public static void Write(NetDataWriter w, NetPlayerWeaponFire m)
        {
            w.Put(Version);
            w.Put(m.PeerId ?? "");
            w.Put(m.ChapterName ?? "");
            w.Put(m.LevelIndex);
            w.Put(m.HasLevelSeed);
            if (m.HasLevelSeed) w.Put(m.LevelSeed);
            w.Put(m.Sequence);
            w.Put(m.SentAt);

            w.Put(m.Origin.x);   w.Put(m.Origin.y);   w.Put(m.Origin.z);
            w.Put(m.AimPoint.x); w.Put(m.AimPoint.y); w.Put(m.AimPoint.z);
            w.Put(m.Count);
            w.Put(m.Speed);
            w.Put(m.Spread);
            w.Put(m.IsSpray);
            w.Put(m.Homing);
            w.Put(m.IsRaygun);

            w.Put(m.ProjectileType);
            w.Put(m.Caliber);
            w.Put(m.Effect);
            w.Put(m.VfxAsset);
            w.Put(m.DamageType);

            w.Put(m.ColorR); w.Put(m.ColorG); w.Put(m.ColorB);
            w.Put(m.CoreColorR); w.Put(m.CoreColorG); w.Put(m.CoreColorB);

            w.Put(m.InnerWidth); w.Put(m.OuterWidth); w.Put(m.Radius);

            w.Put(m.Mass); w.Put(m.Drag);
            w.Put(m.GravityX); w.Put(m.GravityY); w.Put(m.GravityZ);

            w.Put(m.TimeScale); w.Put(m.LifeTime); w.Put(m.BehaviourTimeout); w.Put(m.BarrelLengthOffset);

            w.Put(m.BounceHits); w.Put(m.Bounciness);
            w.Put(m.PenetrationBehavior); w.Put(m.PenetrationsLeft); w.Put(m.PenetrationDamageMultiplier);

            w.Put(m.DrawDefaultBullet);
            w.Put(m.DrawLaserBeam);
            w.Put(m.IsRocket);
            w.Put(m.HasCustomColors);
            w.Put(m.HasCustomTrail);
            w.Put(m.CreateBulletHoles);
            w.Put(m.StickToGeometry);
            w.Put(m.PlayImpactSounds);

            var sounds = m.ShotSounds ?? System.Array.Empty<NetShotSound>();
            int n = System.Math.Min(sounds.Length, RemoteShotSound.MaxSoundsPerShot);
            w.Put((byte)n);
            for (int i = 0; i < n; i++)
            {
                w.Put((byte)sounds[i].Slot);
                w.Put(sounds[i].SourceId);
                w.Put((byte)sounds[i].ParamKind);
                w.Put(sounds[i].Param);
            }
            w.Put(m.BeamLoop);
            w.Put(m.BeamLoopEffect);
        }

        public static bool TryRead(NetDataReader r, out NetPlayerWeaponFire m)
        {
            m = new NetPlayerWeaponFire();
            try
            {
                byte ver = r.GetByte();
                if (ver != Version) return false;

                m.PeerId      = r.GetString();
                m.ChapterName = r.GetString();
                m.LevelIndex  = r.GetInt();
                m.HasLevelSeed = r.GetBool();
                if (m.HasLevelSeed) m.LevelSeed = r.GetInt();
                m.Sequence = r.GetInt();
                m.SentAt   = r.GetFloat();

                m.Origin   = new UnityEngine.Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                m.AimPoint = new UnityEngine.Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                m.Count    = r.GetInt();
                m.Speed    = r.GetFloat();
                m.Spread   = r.GetFloat();
                m.IsSpray  = r.GetBool();
                m.Homing   = r.GetBool();
                m.IsRaygun = r.GetBool();

                m.ProjectileType = r.GetInt();
                m.Caliber        = r.GetInt();
                m.Effect         = r.GetInt();
                m.VfxAsset       = r.GetInt();
                m.DamageType     = r.GetInt();

                m.ColorR = r.GetFloat(); m.ColorG = r.GetFloat(); m.ColorB = r.GetFloat();
                m.CoreColorR = r.GetFloat(); m.CoreColorG = r.GetFloat(); m.CoreColorB = r.GetFloat();

                m.InnerWidth = r.GetFloat(); m.OuterWidth = r.GetFloat(); m.Radius = r.GetFloat();

                m.Mass = r.GetFloat(); m.Drag = r.GetFloat();
                m.GravityX = r.GetFloat(); m.GravityY = r.GetFloat(); m.GravityZ = r.GetFloat();

                m.TimeScale = r.GetFloat(); m.LifeTime = r.GetFloat();
                m.BehaviourTimeout = r.GetFloat(); m.BarrelLengthOffset = r.GetFloat();

                m.BounceHits = r.GetInt(); m.Bounciness = r.GetFloat();
                m.PenetrationBehavior = r.GetInt(); m.PenetrationsLeft = r.GetInt();
                m.PenetrationDamageMultiplier = r.GetFloat();

                m.DrawDefaultBullet = r.GetBool();
                m.DrawLaserBeam     = r.GetBool();
                m.IsRocket          = r.GetBool();
                m.HasCustomColors   = r.GetBool();
                m.HasCustomTrail    = r.GetBool();
                m.CreateBulletHoles = r.GetBool();
                m.StickToGeometry   = r.GetBool();
                m.PlayImpactSounds  = r.GetBool();

                int n = r.GetByte();
                if (n > RemoteShotSound.MaxSoundsPerShot) return false;
                var sounds = n == 0 ? System.Array.Empty<NetShotSound>() : new NetShotSound[n];
                for (int i = 0; i < n; i++)
                {
                    sounds[i] = new NetShotSound
                    {
                        Slot      = (ShotSoundSlot)r.GetByte(),
                        SourceId  = r.GetInt(),
                        ParamKind = (ShotSoundParam)r.GetByte(),
                        Param     = r.GetFloat(),
                    };
                }
                m.ShotSounds = sounds;
                m.BeamLoop = r.GetByte();
                if (m.BeamLoop > RemoteShotSound.BeamLoopStop) return false;
                m.BeamLoopEffect = r.GetInt();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
