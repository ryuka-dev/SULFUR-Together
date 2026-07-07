using System;
using LiteNetLib;
using LiteNetLib.Utils;

namespace SULFURTogether.Networking.Gameplay
{
    internal static class NetRunStatsCodec
    {
        public static void Write(NetDataWriter w, NetRunStats stats)
        {
            w.Put(stats.PeerId ?? "");
            w.Put(stats.PlayerName ?? "");
            w.Put(stats.ShotsFired);
            w.Put(stats.DamageDealt);
            w.Put(stats.Kills);
            w.Put(stats.TimesDowned);
            w.Put(stats.Rescues);
            w.Put(stats.DamageTaken);
            w.Put(stats.DestructiblesDestroyed);
        }

        public static bool TryRead(NetPacketReader r, out NetRunStats stats)
        {
            stats = new NetRunStats();
            try
            {
                stats.PeerId = r.GetString();
                stats.PlayerName = r.GetString();
                stats.ShotsFired = r.GetInt();
                stats.DamageDealt = r.GetInt();
                stats.Kills = r.GetInt();
                stats.TimesDowned = r.GetInt();
                stats.Rescues = r.GetInt();
                stats.DamageTaken = r.GetInt();
                stats.DestructiblesDestroyed = r.GetInt();
                return true;
            }
            catch (Exception ex)
            {
                NetLogger.Warn($"[RunStats] Failed to decode NetRunStats: {ex.Message}");
                return false;
            }
        }
    }
}
