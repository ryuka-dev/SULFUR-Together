using System;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    internal static class NetPlayerLifeStateCodec
    {
        public static void Write(NetDataWriter w, NetPlayerLifeState state)
        {
            w.Put(state.EventId ?? "");
            w.Put(state.SourcePeerId ?? "");
            w.Put(state.TargetPeerId ?? "");
            w.Put(state.PlayerName ?? "");
            w.Put(state.ChapterName ?? "<unknown>");
            w.Put(state.LevelIndex);
            w.Put(state.HasLevelSeed);
            w.Put(state.LevelSeed);
            w.Put(state.Sequence);
            w.Put((byte)state.Kind);
            w.Put(state.HasPosition);
            w.Put(state.Position.x);
            w.Put(state.Position.y);
            w.Put(state.Position.z);
            w.Put(state.SentAt);
            w.Put(state.Reason ?? "");
            w.Put(state.DamageAmount);
            w.Put(state.DamageType);
            w.Put(state.Progress);
        }

        public static bool TryRead(NetPacketReader r, out NetPlayerLifeState state)
        {
            state = new NetPlayerLifeState();
            try
            {
                state.EventId = r.GetString();
                state.SourcePeerId = r.GetString();
                state.TargetPeerId = r.GetString();
                state.PlayerName = r.GetString();
                state.ChapterName = r.GetString();
                state.LevelIndex = r.GetInt();
                state.HasLevelSeed = r.GetBool();
                state.LevelSeed = r.GetInt();
                state.Sequence = r.GetInt();
                state.Kind = (NetPlayerLifeStateKind)r.GetByte();
                state.HasPosition = r.GetBool();
                float x = r.GetFloat();
                float y = r.GetFloat();
                float z = r.GetFloat();
                state.Position = new Vector3(x, y, z);
                state.SentAt = r.GetFloat();
                state.Reason = r.GetString();
                state.DamageAmount = r.GetFloat();
                state.DamageType = r.GetInt();
                state.Progress = r.GetFloat();
                return true;
            }
            catch (Exception ex)
            {
                NetLogger.Warn($"[PlayerLife] Failed to decode PlayerLifeState: {ex.Message}");
                return false;
            }
        }
    }
}
