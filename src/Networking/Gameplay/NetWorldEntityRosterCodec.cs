using System.Collections.Generic;
using LiteNetLib.Utils;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    internal static class NetWorldEntityRosterCodec
    {
        public static void Write(NetDataWriter w, List<NetWorldEntityRecord> records)
        {
            w.Put((short)records.Count);
            foreach (var r in records)
            {
                w.Put(r.NetEntityId    ?? "");
                w.Put((byte)r.SyncCategory);
                w.Put(r.Category       ?? "");
                w.Put(r.UnitIdentifier ?? "");
                w.Put(r.ActorName      ?? "");
                w.Put(r.SpawnIndex);
                w.Put(r.HasPosition);
                if (r.HasPosition)
                {
                    w.Put(r.Position.x);
                    w.Put(r.Position.y);
                    w.Put(r.Position.z);
                }
                w.Put(r.SceneRevision);
                w.Put(r.ChapterName    ?? "");
                w.Put(r.LevelIndex);
                w.Put(r.HasLevelSeed);
                if (r.HasLevelSeed) w.Put(r.LevelSeed);
            }
        }

        public static List<NetWorldEntityRecord> Read(NetDataReader r)
        {
            var result = new List<NetWorldEntityRecord>();
            try
            {
                int count = r.GetShort();
                if (count < 0 || count > 1024) return result;
                for (int i = 0; i < count; i++)
                {
                    var rec = new NetWorldEntityRecord
                    {
                        NetEntityId    = r.GetString(),
                        SyncCategory   = r.GetByte(),
                        Category       = r.GetString(),
                        UnitIdentifier = r.GetString(),
                        ActorName      = r.GetString(),
                        SpawnIndex     = r.GetInt(),
                        HasPosition    = r.GetBool(),
                    };
                    if (rec.HasPosition)
                        rec.Position = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                    rec.SceneRevision = r.GetInt();
                    rec.ChapterName   = r.GetString();
                    rec.LevelIndex    = r.GetInt();
                    rec.HasLevelSeed  = r.GetBool();
                    if (rec.HasLevelSeed) rec.LevelSeed = r.GetInt();
                    result.Add(rec);
                }
            }
            catch { }
            return result;
        }
    }
}
