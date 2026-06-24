using LiteNetLib.Utils;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    internal static class NetClientHitRequestCodec
    {
        public static void Write(NetDataWriter w, NetClientHitRequest r)
        {
            w.Put(r.ChapterName ?? "");
            w.Put(r.LevelIndex);
            w.Put(r.HasLevelSeed);
            if (r.HasLevelSeed) w.Put(r.LevelSeed);

            w.Put(r.RequestSeq);
            w.Put(r.ClientPeerId ?? "");

            w.Put(r.TargetHostSpawnIndex);
            w.Put(r.TargetUnitIdentifier ?? "");

            w.Put(r.DamageCandidate);

            w.Put(r.HasAttackerPosition);
            if (r.HasAttackerPosition)
            {
                w.Put(r.AttackerPosition.x);
                w.Put(r.AttackerPosition.y);
                w.Put(r.AttackerPosition.z);
            }

            w.Put(r.SentAt);
        }

        public static bool TryRead(NetDataReader r, out NetClientHitRequest result)
        {
            result = null!;
            try
            {
                var req = new NetClientHitRequest();
                req.ChapterName  = r.GetString();
                req.LevelIndex   = r.GetInt();
                req.HasLevelSeed = r.GetBool();
                if (req.HasLevelSeed) req.LevelSeed = r.GetInt();

                req.RequestSeq   = r.GetInt();
                req.ClientPeerId = r.GetString();

                req.TargetHostSpawnIndex = r.GetInt();
                req.TargetUnitIdentifier = r.GetString();

                req.DamageCandidate = r.GetFloat();

                req.HasAttackerPosition = r.GetBool();
                if (req.HasAttackerPosition)
                    req.AttackerPosition = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());

                req.SentAt = r.GetFloat();

                result = req;
                return true;
            }
            catch { return false; }
        }
    }
}
