using LiteNetLib.Utils;

namespace SULFURTogether.Networking.Gameplay
{
    internal static class NetCardinalFightEventCodec
    {
        private const byte Version = 1;

        public static void Write(NetDataWriter w, NetCardinalFightEvent m)
        {
            w.Put(Version);
            w.Put(m.PeerId ?? "");
            w.Put(m.ChapterName ?? "");
            w.Put(m.LevelIndex);
            w.Put(m.HasLevelSeed);
            if (m.HasLevelSeed) w.Put(m.LevelSeed);
            w.Put(m.Sequence);
            w.Put(m.SentAt);

            w.Put(m.Kind);
            w.Put(m.HelperPosition.x); w.Put(m.HelperPosition.y); w.Put(m.HelperPosition.z);

            if (m.Kind == NetCardinalFightEvent.KindTeleportBegin || m.Kind == NetCardinalFightEvent.KindTeleportExecute)
                w.Put(m.CardinalIndex);

            if (m.Kind == NetCardinalFightEvent.KindTeleportExecute)
            {
                w.Put(m.SpawnIndex);
                w.Put(m.DestinationPosition.x); w.Put(m.DestinationPosition.y); w.Put(m.DestinationPosition.z);
            }
        }

        public static bool TryRead(NetDataReader r, out NetCardinalFightEvent m)
        {
            m = new NetCardinalFightEvent();
            try
            {
                byte ver = r.GetByte();
                if (ver != Version) return false;

                m.PeerId       = r.GetString();
                m.ChapterName  = r.GetString();
                m.LevelIndex   = r.GetInt();
                m.HasLevelSeed = r.GetBool();
                if (m.HasLevelSeed) m.LevelSeed = r.GetInt();
                m.Sequence = r.GetInt();
                m.SentAt   = r.GetFloat();

                m.Kind = r.GetByte();
                m.HelperPosition = new UnityEngine.Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());

                if (m.Kind == NetCardinalFightEvent.KindTeleportBegin || m.Kind == NetCardinalFightEvent.KindTeleportExecute)
                    m.CardinalIndex = r.GetInt();

                if (m.Kind == NetCardinalFightEvent.KindTeleportExecute)
                {
                    m.SpawnIndex = r.GetInt();
                    m.DestinationPosition = new UnityEngine.Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                }
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
