using LiteNetLib.Utils;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    internal static class NetFriendlyFireHitCodec
    {
        public static void Write(NetDataWriter w, NetFriendlyFireHit h)
        {
            w.Put(h.ChapterName ?? "");
            w.Put(h.LevelIndex);
            w.Put(h.HasLevelSeed);
            if (h.HasLevelSeed) w.Put(h.LevelSeed);

            w.Put(h.Seq);
            w.Put(h.SourcePeerId ?? "");
            w.Put(h.VictimPeerId ?? "");

            w.Put(h.Damage);
            w.Put(h.DamageTypeInt);

            w.Put(h.HasPosition);
            if (h.HasPosition)
            {
                w.Put(h.Position.x);
                w.Put(h.Position.y);
                w.Put(h.Position.z);
            }

            w.Put(h.SentAt);
        }

        public static bool TryRead(NetDataReader r, out NetFriendlyFireHit result)
        {
            result = null!;
            try
            {
                var h = new NetFriendlyFireHit();
                h.ChapterName  = r.GetString();
                h.LevelIndex   = r.GetInt();
                h.HasLevelSeed = r.GetBool();
                if (h.HasLevelSeed) h.LevelSeed = r.GetInt();

                h.Seq          = r.GetInt();
                h.SourcePeerId = r.GetString();
                h.VictimPeerId = r.GetString();

                h.Damage        = r.GetFloat();
                h.DamageTypeInt = r.GetInt();

                h.HasPosition = r.GetBool();
                if (h.HasPosition)
                    h.Position = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());

                h.SentAt = r.GetFloat();

                result = h;
                return true;
            }
            catch { return false; }
        }
    }
}
