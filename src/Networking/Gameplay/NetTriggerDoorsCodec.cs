using LiteNetLib.Utils;

namespace SULFURTogether.Networking.Gameplay
{
    internal static class NetTriggerDoorsCodec
    {
        private const byte Version = 1;

        public static void Write(NetDataWriter w, NetTriggerDoors m)
        {
            w.Put(Version);
            w.Put(m.PeerId ?? "");
            w.Put(m.ChapterName ?? "");
            w.Put(m.LevelIndex);
            w.Put(m.HasLevelSeed);
            if (m.HasLevelSeed) w.Put(m.LevelSeed);
            w.Put(m.Sequence);
            w.Put(m.SentAt);

            w.Put(m.TriggerPos.x); w.Put(m.TriggerPos.y); w.Put(m.TriggerPos.z);

            int n = m.Doors?.Count ?? 0;
            w.Put(n);
            for (int i = 0; i < n; i++)
            {
                w.Put(m.Doors[i].Name ?? "");
                w.Put(m.Doors[i].Active);
            }
        }

        public static bool TryRead(NetDataReader r, out NetTriggerDoors m)
        {
            m = new NetTriggerDoors();
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

                m.TriggerPos = new UnityEngine.Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());

                int n = r.GetInt();
                for (int i = 0; i < n; i++)
                {
                    string name = r.GetString();
                    bool active = r.GetBool();
                    m.Doors.Add(new NetTriggerDoors.DoorEntry { Name = name, Active = active });
                }
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
