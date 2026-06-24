using LiteNetLib.Utils;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    internal static class NetGameplayDeathEventCodec
    {
        public static void Write(NetDataWriter w, NetGameplayDeathEvent evt)
        {
            w.Put(evt.EventId ?? "");
            w.Put(evt.SourcePeerId ?? "host");
            w.Put(evt.ChapterName ?? "<unknown>");
            w.Put(evt.LevelIndex);
            w.Put(evt.HasLevelSeed ? 1 : 0);
            w.Put(evt.LevelSeed);
            w.Put(evt.SourceRevision);
            w.Put(evt.Sequence);

            w.Put(evt.SpawnIndex);
            w.Put(evt.CandidateKey ?? "");
            w.Put(evt.LocalInstanceId ?? "");
            w.Put(evt.UnityInstanceId);
            w.Put(evt.TypeName ?? "");
            w.Put(evt.UnitIdentifier ?? "");
            w.Put(evt.UnitGlobalId ?? "");
            w.Put(evt.Category ?? "Npc");
            w.Put(evt.ActorName ?? "<unknown>");
            w.Put(evt.HasPosition ? 1 : 0);
            w.Put(evt.Position.x);
            w.Put(evt.Position.y);
            w.Put(evt.Position.z);
            w.Put(evt.DamageCount);
            w.Put(evt.Source ?? "");
            w.Put(evt.SentAt);
        }

        public static bool TryRead(NetDataReader r, out NetGameplayDeathEvent evt)
        {
            evt = new NetGameplayDeathEvent();
            try
            {
                evt.EventId        = r.GetString();
                evt.SourcePeerId   = r.GetString();
                evt.ChapterName    = r.GetString();
                evt.LevelIndex     = r.GetInt();
                evt.HasLevelSeed   = r.GetInt() != 0;
                evt.LevelSeed      = r.GetInt();
                evt.SourceRevision = r.GetInt();
                evt.Sequence       = r.GetInt();

                evt.SpawnIndex     = r.GetInt();
                evt.CandidateKey   = r.GetString();
                evt.LocalInstanceId = r.GetString();
                evt.UnityInstanceId = r.GetInt();
                evt.TypeName       = r.GetString();
                evt.UnitIdentifier = r.GetString();
                evt.UnitGlobalId   = r.GetString();
                evt.Category       = r.GetString();
                evt.ActorName      = r.GetString();
                evt.HasPosition    = r.GetInt() != 0;
                float x            = r.GetFloat();
                float y            = r.GetFloat();
                float z            = r.GetFloat();
                evt.Position       = new Vector3(x, y, z);
                evt.DamageCount    = r.GetInt();
                evt.Source         = r.GetString();
                evt.SentAt         = r.GetFloat();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
