using LiteNetLib.Utils;

namespace SULFURTogether.Networking.Gameplay
{
    internal static class NetHostHitVisualEventCodec
    {
        public static void Write(NetDataWriter w, NetHostHitVisualEvent e)
        {
            w.Put(e.ChapterName ?? "");
            w.Put(e.LevelIndex);
            w.Put(e.HasLevelSeed);
            if (e.HasLevelSeed) w.Put(e.LevelSeed);
            w.Put(e.HostSpawnIndex);
            w.Put(e.UnitIdentifier ?? "");
            w.Put(e.Sequence);
            w.Put(e.IsFatal);
            w.Put(e.SentAt);
        }

        public static bool TryRead(NetDataReader r, out NetHostHitVisualEvent e)
        {
            e = null!;
            try
            {
                var evt = new NetHostHitVisualEvent
                {
                    ChapterName  = r.GetString(),
                    LevelIndex   = r.GetInt(),
                    HasLevelSeed = r.GetBool(),
                };
                if (evt.HasLevelSeed) evt.LevelSeed = r.GetInt();
                evt.HostSpawnIndex = r.GetInt();
                evt.UnitIdentifier = r.GetString();
                evt.Sequence       = r.GetInt();
                evt.IsFatal        = r.GetBool();
                evt.SentAt         = r.GetFloat();
                e = evt;
                return true;
            }
            catch { return false; }
        }
    }
}
