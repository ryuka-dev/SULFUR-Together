using LiteNetLib.Utils;

namespace SULFURTogether.Networking.Gameplay
{
    internal static class NetThrowableEffectCodec
    {
        private const byte Version = 1;

        public static void Write(NetDataWriter w, NetThrowableEffect m)
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
            w.Put(m.Position.x); w.Put(m.Position.y); w.Put(m.Position.z);
            w.Put(m.RotationY);
        }

        public static bool TryRead(NetDataReader r, out NetThrowableEffect m)
        {
            m = new NetThrowableEffect();
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
                m.Position = new UnityEngine.Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                m.RotationY = r.GetFloat();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
