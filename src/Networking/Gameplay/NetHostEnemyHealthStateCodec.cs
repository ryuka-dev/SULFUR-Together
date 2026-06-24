using LiteNetLib.Utils;

namespace SULFURTogether.Networking.Gameplay
{
    internal static class NetHostEnemyHealthStateCodec
    {
        private const byte Version = 1;

        public static void Write(NetDataWriter w, NetHostEnemyHealthState state)
        {
            w.Put(Version);
            w.Put(state.ChapterName ?? "");
            w.Put(state.LevelIndex);
            w.Put(state.HasLevelSeed);
            if (state.HasLevelSeed) w.Put(state.LevelSeed);
            w.Put(state.HostSpawnIndex);
            w.Put(state.UnitIdentifier ?? "");
            w.Put(state.Sequence);
            w.Put(state.HasCurrentHealth);
            if (state.HasCurrentHealth)
            {
                w.Put(state.CurrentHealth);
                w.Put(state.HasMaxHealth);
                if (state.HasMaxHealth) w.Put(state.MaxHealth);
                w.Put(state.HasNormalizedHealth);
                if (state.HasNormalizedHealth) w.Put(state.NormalizedHealth);
            }
            w.Put(state.IsDead);
            w.Put(state.HasPosition);
            if (state.HasPosition)
            {
                w.Put(state.Position.x);
                w.Put(state.Position.y);
                w.Put(state.Position.z);
            }
            w.Put(state.SentAt);
        }

        public static bool TryRead(NetDataReader r, out NetHostEnemyHealthState state)
        {
            state = new NetHostEnemyHealthState();
            try
            {
                byte ver = r.GetByte();
                if (ver != Version) return false;

                state.ChapterName  = r.GetString();
                state.LevelIndex   = r.GetInt();
                state.HasLevelSeed = r.GetBool();
                if (state.HasLevelSeed) state.LevelSeed = r.GetInt();
                state.HostSpawnIndex = r.GetInt();
                state.UnitIdentifier = r.GetString();
                state.Sequence = r.GetInt();
                state.HasCurrentHealth = r.GetBool();
                if (state.HasCurrentHealth)
                {
                    state.CurrentHealth = r.GetFloat();
                    state.HasMaxHealth  = r.GetBool();
                    if (state.HasMaxHealth) state.MaxHealth = r.GetFloat();
                    state.HasNormalizedHealth = r.GetBool();
                    if (state.HasNormalizedHealth) state.NormalizedHealth = r.GetFloat();
                }
                state.IsDead = r.GetBool();
                state.HasPosition = r.GetBool();
                if (state.HasPosition)
                    state.Position = new UnityEngine.Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                state.SentAt = r.GetFloat();
                return true;
            }
            catch { return false; }
        }
    }
}
