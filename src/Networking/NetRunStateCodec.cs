using LiteNetLib.Utils;

namespace SULFURTogether.Networking
{
    internal static class NetRunStateCodec
    {
        public static void Write(NetDataWriter w, NetRunState state)
        {
            w.Put(state.PeerId ?? "");
            w.Put(state.PlayerName ?? "");
            w.Put(state.ChapterName ?? "<unknown>");
            w.Put(state.LevelIndex);
            w.Put(state.LoadingMode ?? "");
            w.Put(state.SpawnIdentifier ?? "");
            w.Put(state.GameState ?? "<unknown>");
            w.Put(state.HasLevelSeed ? 1 : 0);
            w.Put(state.LevelSeed);
            w.Put(state.LevelGenerator ?? "");
            w.Put(state.Revision);
            w.Put(state.LastUpdatedAt);
        }

        public static bool TryRead(NetDataReader r, out NetRunState state)
        {
            state = new NetRunState();
            try
            {
                state.PeerId          = r.GetString();
                state.PlayerName      = r.GetString();
                state.ChapterName     = r.GetString();
                state.LevelIndex      = r.GetInt();
                state.LoadingMode     = r.GetString();
                state.SpawnIdentifier = r.GetString();
                state.GameState       = r.GetString();

                // Phase 3.1 fields. No compatibility requirement inside this dev branch, but keep safe fallback.
                if (r.AvailableBytes >= 4)
                    state.HasLevelSeed = r.GetInt() != 0;
                if (r.AvailableBytes >= 4)
                    state.LevelSeed = r.GetInt();
                if (r.AvailableBytes > 0)
                    state.LevelGenerator = r.GetString();

                if (r.AvailableBytes >= 4)
                    state.Revision = r.GetInt();
                if (r.AvailableBytes >= 4)
                    state.LastUpdatedAt = r.GetFloat();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
