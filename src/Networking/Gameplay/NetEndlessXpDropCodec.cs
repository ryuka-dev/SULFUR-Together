using LiteNetLib.Utils;

namespace SULFURTogether.Networking.Gameplay
{
    internal static class NetEndlessXpDropCodec
    {
        private const byte Version = 2;

        public static void Write(NetDataWriter w, NetEndlessXpDrop m)
        {
            w.Put(Version);
            w.Put(m.ChapterName ?? "");
            w.Put(m.LevelIndex);
            w.Put(m.HasLevelSeed);
            if (m.HasLevelSeed) w.Put(m.LevelSeed);
            w.Put(m.DropId);
            w.Put(m.Position.x); w.Put(m.Position.y); w.Put(m.Position.z);
            w.Put(m.TotalXp);
            w.Put(m.OrbCount);
        }

        public static bool TryRead(NetDataReader r, out NetEndlessXpDrop m)
        {
            m = new NetEndlessXpDrop();
            try
            {
                byte ver = r.GetByte();
                if (ver != Version) return false;

                m.ChapterName  = r.GetString();
                m.LevelIndex   = r.GetInt();
                m.HasLevelSeed = r.GetBool();
                if (m.HasLevelSeed) m.LevelSeed = r.GetInt();
                m.DropId   = r.GetInt();
                m.Position = new UnityEngine.Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                m.TotalXp  = r.GetInt();
                m.OrbCount = r.GetInt();
                return true;
            }
            catch { return false; }
        }
    }
}
