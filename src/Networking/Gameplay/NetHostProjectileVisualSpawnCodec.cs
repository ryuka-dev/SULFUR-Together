using LiteNetLib.Utils;

namespace SULFURTogether.Networking.Gameplay
{
    internal static class NetHostProjectileVisualSpawnCodec
    {
        private const byte Version = 1;

        public static void Write(NetDataWriter w, NetHostProjectileVisualSpawn evt)
        {
            w.Put(Version);
            w.Put(evt.ChapterName ?? "");
            w.Put(evt.LevelIndex);
            w.Put(evt.HasLevelSeed);
            if (evt.HasLevelSeed) w.Put(evt.LevelSeed);
            w.Put(evt.HostSpawnIndex);
            w.Put(evt.UnitIdentifier ?? "");
            w.Put(evt.Sequence);
            w.Put(evt.Origin.x);   w.Put(evt.Origin.y);   w.Put(evt.Origin.z);
            w.Put(evt.Velocity.x); w.Put(evt.Velocity.y); w.Put(evt.Velocity.z);
            w.Put(evt.Lifetime);
            w.Put(evt.ProjectileKind ?? "");
            w.Put(evt.SentAt);
        }

        public static bool TryRead(NetDataReader r, out NetHostProjectileVisualSpawn evt)
        {
            evt = new NetHostProjectileVisualSpawn();
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
                evt.Sequence = r.GetInt();
                evt.Origin   = new UnityEngine.Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                evt.Velocity = new UnityEngine.Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                evt.Lifetime = r.GetFloat();
                evt.ProjectileKind = r.GetString();
                evt.SentAt = r.GetFloat();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
