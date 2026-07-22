using LiteNetLib.Utils;

namespace SULFURTogether.Networking.Gameplay
{
    internal static class NetUnitStatusCodec
    {
        public static void WriteClientRequest(NetDataWriter w, NetClientUnitStatusRequest m)
        {
            w.Put(m.ChapterName ?? "");
            w.Put(m.LevelIndex);
            w.Put(m.HasLevelSeed);
            if (m.HasLevelSeed) w.Put(m.LevelSeed);
            w.Put(m.RequestSeq);
            w.Put(m.TargetHostSpawnIndex);
            w.Put(m.TargetUnitIdentifier ?? "");

            var attrs  = m.Attributes ?? System.Array.Empty<ushort>();
            var values = m.Values     ?? System.Array.Empty<float>();
            int count = attrs.Length;
            if (values.Length < count) count = values.Length;
            if (count > NetClientUnitStatusRequest.MaxEntries) count = NetClientUnitStatusRequest.MaxEntries;
            w.Put((byte)count);
            for (int i = 0; i < count; i++)
            {
                w.Put(attrs[i]);
                w.Put(values[i]);
            }
            w.Put(m.SentAt);
        }

        public static bool TryReadClientRequest(NetDataReader r, out NetClientUnitStatusRequest m)
        {
            m = null!;
            try
            {
                var msg = new NetClientUnitStatusRequest
                {
                    ChapterName  = r.GetString(),
                    LevelIndex   = r.GetInt(),
                    HasLevelSeed = r.GetBool(),
                };
                if (msg.HasLevelSeed) msg.LevelSeed = r.GetInt();
                msg.RequestSeq           = r.GetInt();
                msg.TargetHostSpawnIndex = r.GetInt();
                msg.TargetUnitIdentifier = r.GetString();

                int count = r.GetByte();
                // Untrusted length: a peer that claims more entries than the format allows is malformed, not clamped —
                // silently truncating would leave the rest of the packet misaligned.
                if (count > NetClientUnitStatusRequest.MaxEntries) return false;
                var attrs  = new ushort[count];
                var values = new float[count];
                for (int i = 0; i < count; i++)
                {
                    attrs[i]  = r.GetUShort();
                    values[i] = r.GetFloat();
                }
                msg.Attributes = attrs;
                msg.Values     = values;
                msg.SentAt     = r.GetFloat();
                m = msg;
                return true;
            }
            catch { return false; }
        }

        public static void WriteHostState(NetDataWriter w, NetHostUnitStatusState m)
        {
            w.Put(m.ChapterName ?? "");
            w.Put(m.LevelIndex);
            w.Put(m.HasLevelSeed);
            if (m.HasLevelSeed) w.Put(m.LevelSeed);
            w.Put(m.HostSpawnIndex);
            w.Put(m.UnitIdentifier ?? "");
            w.Put(m.Attribute);
            w.Put(m.Value);
            w.Put(m.Sequence);
            w.Put(m.SentAt);
        }

        public static bool TryReadHostState(NetDataReader r, out NetHostUnitStatusState m)
        {
            m = null!;
            try
            {
                var msg = new NetHostUnitStatusState
                {
                    ChapterName  = r.GetString(),
                    LevelIndex   = r.GetInt(),
                    HasLevelSeed = r.GetBool(),
                };
                if (msg.HasLevelSeed) msg.LevelSeed = r.GetInt();
                msg.HostSpawnIndex = r.GetInt();
                msg.UnitIdentifier = r.GetString();
                msg.Attribute      = r.GetUShort();
                msg.Value          = r.GetFloat();
                msg.Sequence       = r.GetInt();
                msg.SentAt         = r.GetFloat();
                m = msg;
                return true;
            }
            catch { return false; }
        }
    }
}
