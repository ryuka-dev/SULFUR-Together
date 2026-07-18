using LiteNetLib.Utils;

namespace SULFURTogether.Networking.Gameplay
{
    internal static class NetEndlessWaveStateCodec
    {
        private const byte Version = 2; // v2: EM-7b loot-locator beam (active + position)

        public static void Write(NetDataWriter w, NetEndlessWaveState m)
        {
            w.Put(Version);
            w.Put(m.ChapterName ?? "");
            w.Put(m.LevelIndex);
            w.Put(m.HasLevelSeed);
            if (m.HasLevelSeed) w.Put(m.LevelSeed);

            w.Put(m.Revision);

            w.Put(m.CurrentStage);
            w.Put(m.CurrentWave);
            w.Put(m.CurrentBurstIndex);
            w.Put(m.LoopCount);
            w.Put(m.TransitionState);

            w.Put(m.CurrentXP);
            w.Put(m.NextCardThresholdXP);
            w.Put(m.CurrentCardLevel);

            w.Put(m.LootBeamActive);
            if (m.LootBeamActive) { w.Put(m.LootBeamX); w.Put(m.LootBeamY); w.Put(m.LootBeamZ); }
        }

        public static bool TryRead(NetDataReader r, out NetEndlessWaveState m)
        {
            m = new NetEndlessWaveState();
            try
            {
                byte ver = r.GetByte();
                if (ver != Version) return false;

                m.ChapterName  = r.GetString();
                m.LevelIndex   = r.GetInt();
                m.HasLevelSeed = r.GetBool();
                if (m.HasLevelSeed) m.LevelSeed = r.GetInt();

                m.Revision = r.GetInt();

                m.CurrentStage      = r.GetInt();
                m.CurrentWave       = r.GetInt();
                m.CurrentBurstIndex = r.GetInt();
                m.LoopCount         = r.GetInt();
                m.TransitionState   = r.GetByte();

                m.CurrentXP           = r.GetFloat();
                m.NextCardThresholdXP = r.GetFloat();
                m.CurrentCardLevel    = r.GetInt();

                m.LootBeamActive = r.GetBool();
                if (m.LootBeamActive) { m.LootBeamX = r.GetFloat(); m.LootBeamY = r.GetFloat(); m.LootBeamZ = r.GetFloat(); }
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
