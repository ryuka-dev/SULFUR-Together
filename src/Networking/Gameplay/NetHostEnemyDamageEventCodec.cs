using LiteNetLib.Utils;

namespace SULFURTogether.Networking.Gameplay
{
    internal static class NetHostEnemyDamageEventCodec
    {
        private const byte Version = 1;

        public static void Write(NetDataWriter w, NetHostEnemyDamageEvent evt)
        {
            w.Put(Version);
            w.Put(evt.ChapterName ?? "");
            w.Put(evt.LevelIndex);
            w.Put(evt.HasLevelSeed);
            if (evt.HasLevelSeed) w.Put(evt.LevelSeed);
            w.Put(evt.HostSpawnIndex);
            w.Put(evt.UnitIdentifier ?? "");
            w.Put(evt.Sequence);
            w.Put(evt.DamageAmount);
            w.Put(evt.HasRemainingHealth);
            if (evt.HasRemainingHealth)
            {
                w.Put(evt.RemainingHealth);
                w.Put(evt.HasMaxHealth);
                if (evt.HasMaxHealth) w.Put(evt.MaxHealth);
            }
            w.Put(evt.IsDead);
            w.Put(evt.HasHitPosition);
            if (evt.HasHitPosition)
            {
                w.Put(evt.HitPosition.x);
                w.Put(evt.HitPosition.y);
                w.Put(evt.HitPosition.z);
            }
            w.Put(evt.SentAt);
        }

        public static bool TryRead(NetDataReader r, out NetHostEnemyDamageEvent evt)
        {
            evt = new NetHostEnemyDamageEvent();
            try
            {
                byte ver = r.GetByte();
                if (ver != Version) return false;

                evt.ChapterName  = r.GetString();
                evt.LevelIndex   = r.GetInt();
                evt.HasLevelSeed = r.GetBool();
                if (evt.HasLevelSeed) evt.LevelSeed = r.GetInt();
                evt.HostSpawnIndex = r.GetInt();
                evt.UnitIdentifier = r.GetString();
                evt.Sequence    = r.GetInt();
                evt.DamageAmount = r.GetFloat();
                evt.HasRemainingHealth = r.GetBool();
                if (evt.HasRemainingHealth)
                {
                    evt.RemainingHealth = r.GetFloat();
                    evt.HasMaxHealth    = r.GetBool();
                    if (evt.HasMaxHealth) evt.MaxHealth = r.GetFloat();
                }
                evt.IsDead = r.GetBool();
                evt.HasHitPosition = r.GetBool();
                if (evt.HasHitPosition)
                    evt.HitPosition = new UnityEngine.Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                evt.SentAt = r.GetFloat();
                return true;
            }
            catch { return false; }
        }
    }
}
