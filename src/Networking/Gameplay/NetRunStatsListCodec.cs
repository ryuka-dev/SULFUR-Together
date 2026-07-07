using System;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;

namespace SULFURTogether.Networking.Gameplay
{
    internal static class NetRunStatsListCodec
    {
        public static void Write(NetDataWriter w, NetRunStatsList list)
        {
            w.Put(list.RunSeq);
            w.Put((short)list.Players.Count);
            foreach (var stats in list.Players)
                NetRunStatsCodec.Write(w, stats);
        }

        public static bool TryRead(NetPacketReader r, out NetRunStatsList list)
        {
            list = new NetRunStatsList();
            try
            {
                list.RunSeq = r.GetInt();
                int count = r.GetShort();
                if (count < 0 || count > 64) return false;
                for (int i = 0; i < count; i++)
                {
                    if (!NetRunStatsCodec.TryRead(r, out var stats)) return false;
                    list.Players.Add(stats);
                }
                return true;
            }
            catch (Exception ex)
            {
                NetLogger.Warn($"[RunStats] Failed to decode NetRunStatsList: {ex.Message}");
                return false;
            }
        }
    }
}
