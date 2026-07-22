using LiteNetLib.Utils;

namespace SULFURTogether.Networking.Gameplay
{
    internal static class NetCryptChallengeStateCodec
    {
        private const byte Version = 1;

        public static void Write(NetDataWriter w, NetCryptChallengeState m)
        {
            w.Put(Version);
            w.Put(m.PeerId ?? "");
            w.Put(m.ChapterName ?? "");
            w.Put(m.LevelIndex);
            w.Put(m.HasLevelSeed);
            if (m.HasLevelSeed) w.Put(m.LevelSeed);
            w.Put(m.Sequence);
            w.Put(m.SentAt);

            w.Put(m.Phase);
            if (m.Phase == NetCryptChallengeState.PhaseCompleted || m.Phase == NetCryptChallengeState.PhaseFailed)
            {
                w.Put(m.Position.x); w.Put(m.Position.y); w.Put(m.Position.z);
            }
            else if (m.Phase == NetCryptChallengeState.PhaseUiUpdate)
            {
                w.Put(m.Info ?? "");
                w.Put(m.UseTimer);
            }
        }

        public static bool TryRead(NetDataReader r, out NetCryptChallengeState m)
        {
            m = new NetCryptChallengeState();
            try
            {
                byte ver = r.GetByte();
                if (ver != Version) return false;

                m.PeerId      = r.GetString();
                m.ChapterName = r.GetString();
                m.LevelIndex  = r.GetInt();
                m.HasLevelSeed = r.GetBool();
                if (m.HasLevelSeed) m.LevelSeed = r.GetInt();
                m.Sequence = r.GetInt();
                m.SentAt   = r.GetFloat();

                m.Phase = r.GetByte();
                if (m.Phase == NetCryptChallengeState.PhaseCompleted || m.Phase == NetCryptChallengeState.PhaseFailed)
                {
                    m.Position = new UnityEngine.Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                }
                else if (m.Phase == NetCryptChallengeState.PhaseUiUpdate)
                {
                    m.Info = r.GetString();
                    m.UseTimer = r.GetBool();
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
