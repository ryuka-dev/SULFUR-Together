using LiteNetLib.Utils;

namespace SULFURTogether.Networking.Gameplay
{
    internal static class NetThrowableProjectileCodec
    {
        private const byte Version = 1;

        public static void Write(NetDataWriter w, NetThrowableProjectile m)
        {
            w.Put(Version);
            w.Put(m.PeerId ?? "");
            w.Put(m.ChapterName ?? "");
            w.Put(m.LevelIndex);
            w.Put(m.HasLevelSeed);
            if (m.HasLevelSeed) w.Put(m.LevelSeed);
            w.Put(m.Sequence);
            w.Put(m.SentAt);

            w.Put(m.ItemIdValue);
            w.Put(m.StartPos.x); w.Put(m.StartPos.y); w.Put(m.StartPos.z);
            w.Put(m.Velocity.x); w.Put(m.Velocity.y); w.Put(m.Velocity.z);
            w.Put(m.Mass);
            w.Put(m.Drag);
            w.Put(m.StickToGeometry);
            w.Put(m.Caliber);
            w.Put(m.DamageType);
            w.Put(m.BarrelLengthOffset);
        }

        public static bool TryRead(NetDataReader r, out NetThrowableProjectile m)
        {
            m = new NetThrowableProjectile();
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

                m.ItemIdValue = r.GetInt();
                m.StartPos = new UnityEngine.Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                m.Velocity = new UnityEngine.Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                m.Mass = r.GetFloat();
                m.Drag = r.GetFloat();
                m.StickToGeometry = r.GetBool();
                m.Caliber = r.GetInt();
                m.DamageType = r.GetInt();
                m.BarrelLengthOffset = r.GetFloat();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
