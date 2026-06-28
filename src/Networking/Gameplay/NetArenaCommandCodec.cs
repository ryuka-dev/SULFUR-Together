using System.Collections.Generic;
using LiteNetLib.Utils;

namespace SULFURTogether.Networking.Gameplay
{
    internal static class NetArenaCommandCodec
    {
        private const byte Version = 1;

        public static void Write(NetDataWriter w, NetArenaCommand m)
        {
            w.Put(Version);
            w.Put((byte)m.Kind);
            w.Put(m.ArenaPos.x); w.Put(m.ArenaPos.y); w.Put(m.ArenaPos.z);
            var ids = m.TargetPeerIds ?? new List<string>();
            w.Put(ids.Count);
            foreach (var id in ids) w.Put(id ?? "");
        }

        public static bool TryRead(NetDataReader r, out NetArenaCommand m)
        {
            m = new NetArenaCommand();
            try
            {
                byte ver = r.GetByte();
                if (ver != Version) return false;

                m.Kind = (ArenaCommandKind)r.GetByte();
                m.ArenaPos = new UnityEngine.Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                int count = r.GetInt();
                for (int i = 0; i < count; i++) m.TargetPeerIds.Add(r.GetString());
                return true;
            }
            catch { return false; }
        }
    }
}
