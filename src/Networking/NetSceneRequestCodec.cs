using LiteNetLib.Utils;

namespace SULFURTogether.Networking
{
    internal static class NetSceneRequestCodec
    {
        public static void WriteHostRequest(NetDataWriter w, NetHostSceneRequest request)
        {
            w.Put(request.RequestId ?? "");
            w.Put(request.HostPeerId ?? "host");
            w.Put(request.HostPlayerName ?? "Host");
            w.Put(request.ChapterName ?? "<unknown>");
            w.Put(request.LevelIndex);
            w.Put(request.LoadingMode ?? "");
            w.Put(request.SpawnIdentifier ?? "");
            w.Put(request.HostGameState ?? "<unknown>");
            w.Put(request.HasLevelSeed ? 1 : 0);
            w.Put(request.LevelSeed);
            w.Put(request.LevelGenerator ?? "");
            w.Put(request.HostRevision);
            w.Put(request.Reason ?? "HostSceneAuthority");
            w.Put(request.AutoLoadAllowed ? 1 : 0);

            // Phase 5.3-I: deterministic generation-input used sets (always written, count=0 when empty
            // so the Client knows the Host explicitly cleared them rather than just omitting them).
            w.Put(request.HasUsedSets ? 1 : 0);
            WriteStringList(w, request.UsedChunksThisRun);
            WriteStringList(w, request.UsedEventsThisRun);
            WriteStringList(w, request.UsedEventsThisEnvironment);

            // Phase 5.3-J P0-6: graph name + run id (append-only; older readers stop before this).
            w.Put(request.GraphName ?? "");
            w.Put(request.GenerationRunId ?? "");
        }

        private static void WriteStringList(NetDataWriter w, System.Collections.Generic.List<string>? list)
        {
            int count = list?.Count ?? 0;
            w.Put(count);
            for (int i = 0; i < count; i++)
                w.Put(list![i] ?? "");
        }

        private static System.Collections.Generic.List<string> ReadStringList(NetDataReader r)
        {
            var list = new System.Collections.Generic.List<string>();
            if (r.AvailableBytes < 4) return list;
            int count = r.GetInt();
            for (int i = 0; i < count && r.AvailableBytes > 0; i++)
                list.Add(r.GetString());
            return list;
        }

        public static bool TryReadHostRequest(NetDataReader r, out NetHostSceneRequest request)
        {
            request = new NetHostSceneRequest();
            try
            {
                request.RequestId       = r.GetString();
                request.HostPeerId      = r.GetString();
                request.HostPlayerName  = r.GetString();
                request.ChapterName     = r.GetString();
                request.LevelIndex      = r.GetInt();
                request.LoadingMode     = r.GetString();
                request.SpawnIdentifier = r.GetString();
                request.HostGameState   = r.GetString();
                if (r.AvailableBytes >= 4) request.HasLevelSeed = r.GetInt() != 0;
                if (r.AvailableBytes >= 4) request.LevelSeed = r.GetInt();
                if (r.AvailableBytes > 0) request.LevelGenerator = r.GetString();
                request.HostRevision    = r.GetInt();
                request.Reason          = r.GetString();
                request.AutoLoadAllowed = r.GetInt() != 0;

                // Phase 5.3-I used sets (tolerant of older senders that omit them).
                if (r.AvailableBytes >= 4)
                {
                    request.HasUsedSets               = r.GetInt() != 0;
                    request.UsedChunksThisRun         = ReadStringList(r);
                    request.UsedEventsThisRun         = ReadStringList(r);
                    request.UsedEventsThisEnvironment = ReadStringList(r);
                }
                if (r.AvailableBytes > 0) request.GraphName = r.GetString();
                if (r.AvailableBytes > 0) request.GenerationRunId = r.GetString();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void WriteClientResponse(NetDataWriter w, NetClientSceneResponse response)
        {
            w.Put(response.RequestId ?? "");
            w.Put(response.ClientPeerId ?? "");
            w.Put(response.ClientPlayerName ?? "");
            w.Put(response.ChapterName ?? "<unknown>");
            w.Put(response.LevelIndex);
            w.Put(response.GameState ?? "<unknown>");
            w.Put(response.HasLevelSeed ? 1 : 0);
            w.Put(response.LevelSeed);
            w.Put(response.LevelGenerator ?? "");
            w.Put(response.LocalRevision);
            w.Put(response.IsInTargetScene ? 1 : 0);
            w.Put(response.FollowPhase ?? "None");
            w.Put(response.Message ?? "");
        }

        public static bool TryReadClientResponse(NetDataReader r, out NetClientSceneResponse response)
        {
            response = new NetClientSceneResponse();
            try
            {
                response.RequestId        = r.GetString();
                response.ClientPeerId     = r.GetString();
                response.ClientPlayerName = r.GetString();
                response.ChapterName      = r.GetString();
                response.LevelIndex       = r.GetInt();
                response.GameState        = r.GetString();
                if (r.AvailableBytes >= 4) response.HasLevelSeed = r.GetInt() != 0;
                if (r.AvailableBytes >= 4) response.LevelSeed = r.GetInt();
                if (r.AvailableBytes > 0) response.LevelGenerator = r.GetString();
                response.LocalRevision    = r.GetInt();
                response.IsInTargetScene  = r.GetInt() != 0;
                if (r.AvailableBytes > 0)
                    response.FollowPhase = r.GetString();
                else
                    response.FollowPhase = response.IsInTargetScene ? "Arrived" : "Refused";
                if (r.AvailableBytes > 0)
                    response.Message = r.GetString();
                else
                    response.Message = "";
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
