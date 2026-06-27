using LiteNetLib.Utils;

namespace SULFURTogether.Networking.Gameplay
{
    internal static class NetClientArenaEnterCodec
    {
        private const byte Version = 1;

        public static void Write(NetDataWriter w, NetClientArenaEnter m)
        {
            w.Put(Version);
            w.Put(m.PeerId ?? "");
            w.Put(m.ChapterName ?? "");
            w.Put(m.LevelIndex);
            w.Put(m.HasLevelSeed);
            if (m.HasLevelSeed) w.Put(m.LevelSeed);
            w.Put(m.ArenaPos.x); w.Put(m.ArenaPos.y); w.Put(m.ArenaPos.z);
        }

        public static bool TryRead(NetDataReader r, out NetClientArenaEnter m)
        {
            m = new NetClientArenaEnter();
            try
            {
                byte ver = r.GetByte();
                if (ver != Version) return false;

                m.PeerId      = r.GetString();
                m.ChapterName = r.GetString();
                m.LevelIndex  = r.GetInt();
                m.HasLevelSeed = r.GetBool();
                if (m.HasLevelSeed) m.LevelSeed = r.GetInt();
                m.ArenaPos = new UnityEngine.Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                return true;
            }
            catch { return false; }
        }
    }
}
