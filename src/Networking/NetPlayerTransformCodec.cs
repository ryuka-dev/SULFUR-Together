using LiteNetLib.Utils;
using UnityEngine;

namespace SULFURTogether.Networking
{
    internal static class NetPlayerTransformCodec
    {
        public static void Write(NetDataWriter w, NetPlayerTransformState state)
        {
            w.Put(state.PeerId ?? "");
            w.Put(state.PlayerName ?? "");
            w.Put(state.ChapterName ?? "<unknown>");
            w.Put(state.LevelIndex);
            w.Put(state.HasLevelSeed ? 1 : 0);
            w.Put(state.LevelSeed);
            w.Put(state.Sequence);
            w.Put(state.SentAt);
            w.Put(state.Position.x);
            w.Put(state.Position.y);
            w.Put(state.Position.z);
            w.Put(state.RotationY);
            w.Put(state.LookYaw);
            w.Put(state.Moving);
            w.Put(state.LookPitch);
        }

        public static bool TryRead(NetDataReader r, out NetPlayerTransformState state)
        {
            state = new NetPlayerTransformState();
            try
            {
                state.PeerId      = r.GetString();
                state.PlayerName  = r.GetString();
                state.ChapterName = r.GetString();
                state.LevelIndex  = r.GetInt();
                if (r.AvailableBytes >= 4) state.HasLevelSeed = r.GetInt() != 0;
                if (r.AvailableBytes >= 4) state.LevelSeed = r.GetInt();
                state.Sequence    = r.GetInt();
                state.SentAt      = r.GetFloat();
                float x           = r.GetFloat();
                float y           = r.GetFloat();
                float z           = r.GetFloat();
                state.Position    = new Vector3(x, y, z);
                state.RotationY   = r.GetFloat();
                state.LookYaw     = r.AvailableBytes >= 4 ? r.GetFloat() : state.RotationY;
                state.Moving      = r.AvailableBytes >= 1 && r.GetBool();
                state.LookPitch   = r.AvailableBytes >= 4 ? r.GetFloat() : 0f;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
