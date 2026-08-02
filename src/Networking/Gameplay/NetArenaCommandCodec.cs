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
            // LD-TP: appended after the v1 payload and read behind an AvailableBytes guard, so the wire format stays
            // readable by a v1 reader (which simply stops at the id list) instead of failing the version check outright.
            w.Put(m.HasEntryPos);
            w.Put(m.EntryPos.x); w.Put(m.EntryPos.y); w.Put(m.EntryPos.z);
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
                if (r.AvailableBytes >= 1) m.HasEntryPos = r.GetBool();
                if (r.AvailableBytes >= 12)
                    m.EntryPos = new UnityEngine.Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                else m.HasEntryPos = false; // truncated tail — treat the destination as unknown, never as (0,0,0)
                return true;
            }
            catch { return false; }
        }
    }
}
