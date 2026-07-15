using LiteNetLib.Utils;

namespace SULFURTogether.Networking.Gameplay
{
    internal static class NetTargetDummyDamageCodec
    {
        private const byte Version = 1;

        public static void Write(NetDataWriter w, NetTargetDummyDamage m)
        {
            w.Put(Version);
            w.Put(m.PeerId ?? "");
            w.Put(m.ChapterName ?? "");
            w.Put(m.LevelIndex);
            w.Put(m.HasLevelSeed);
            if (m.HasLevelSeed) w.Put(m.LevelSeed);
            w.Put(m.Sequence);
            w.Put(m.SentAt);

            w.Put(m.Position.x); w.Put(m.Position.y); w.Put(m.Position.z);
            w.Put(m.Amount);
            w.Put(m.DamageType);
            w.Put(m.IsCritical);
            w.Put(m.HitPoint.x); w.Put(m.HitPoint.y); w.Put(m.HitPoint.z);
        }

        public static bool TryRead(NetDataReader r, out NetTargetDummyDamage m)
        {
            m = new NetTargetDummyDamage();
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

                m.Position = new UnityEngine.Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                m.Amount     = r.GetInt();
                m.DamageType = r.GetByte();
                m.IsCritical = r.GetBool();
                m.HitPoint = new UnityEngine.Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
