using LiteNetLib.Utils;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase 5.3-E: codec for NetLevelManifest. Sent over a ReliableOrdered channel, which
    /// LiteNetLib auto-fragments — no manual chunking needed (same pattern as HostWorldRoster).
    /// </summary>
    internal static class NetLevelManifestCodec
    {
        public static void Write(NetDataWriter w, NetLevelManifest m)
        {
            var h = m.Header;
            w.Put(h.ManifestVersion);
            w.Put(h.Role ?? "");
            w.Put(h.SceneName ?? "");
            w.Put(h.LevelIndex);
            w.Put(h.HasLevelSeed);
            w.Put(h.LevelSeed);
            w.Put(h.GenerationRevision);
            w.Put(h.RoomCount);
            w.Put(h.UnitCount);
            w.Put(h.CombatEnemyCount);
            w.Put(h.SpecialEventCount);
            w.Put(h.GenerationHash);
            w.Put(h.RuntimeHash);
            w.Put(h.BuiltAt);

            w.Put(m.Rooms.Count);
            foreach (var r in m.Rooms)
            {
                w.Put(r.RoomIndex);
                w.Put(r.RoomName ?? "");
                w.Put(r.HasPosition);
                if (r.HasPosition) { w.Put(r.Position.x); w.Put(r.Position.y); w.Put(r.Position.z); }
                w.Put(r.ChildCount);
            }

            w.Put(m.Units.Count);
            foreach (var u in m.Units)
            {
                w.Put(u.ManifestIndex);
                w.Put(u.SpawnIndex);
                w.Put(u.UnitIdentifier ?? "");
                w.Put(u.ActorName ?? "");
                w.Put(u.GameObjectName ?? "");
                w.Put(u.SyncCategory);
                w.Put(u.Category ?? "");
                w.Put(u.IsCombatEnemy);
                w.Put(u.HasPosition);
                if (u.HasPosition) { w.Put(u.Position.x); w.Put(u.Position.y); w.Put(u.Position.z); }
                w.Put(u.HasInitialPosition);
                if (u.HasInitialPosition) { w.Put(u.InitialPosition.x); w.Put(u.InitialPosition.y); w.Put(u.InitialPosition.z); }
                w.Put(u.ModifierFlags ?? "");
                w.Put(u.ComponentFingerprint);
                w.Put(u.IsDead);
            }

            w.Put(m.Specials.Count);
            foreach (var s in m.Specials)
            {
                w.Put(s.Type ?? "");
                w.Put(s.Name ?? "");
                w.Put(s.SyncCategory);
                w.Put(s.HasPosition);
                if (s.HasPosition) { w.Put(s.Position.x); w.Put(s.Position.y); w.Put(s.Position.z); }
            }
        }

        public static bool TryRead(NetDataReader r, out NetLevelManifest m)
        {
            m = null!;
            try
            {
                var manifest = new NetLevelManifest();
                var h = manifest.Header;
                h.ManifestVersion    = r.GetInt();
                h.Role               = r.GetString();
                h.SceneName          = r.GetString();
                h.LevelIndex         = r.GetInt();
                h.HasLevelSeed       = r.GetBool();
                h.LevelSeed          = r.GetInt();
                h.GenerationRevision = r.GetInt();
                h.RoomCount          = r.GetInt();
                h.UnitCount          = r.GetInt();
                h.CombatEnemyCount   = r.GetInt();
                h.SpecialEventCount  = r.GetInt();
                h.GenerationHash     = r.GetUInt();
                h.RuntimeHash        = r.GetUInt();
                h.BuiltAt            = r.GetFloat();

                int roomCount = r.GetInt();
                for (int i = 0; i < roomCount; i++)
                {
                    var room = new NetLevelManifestRoom
                    {
                        RoomIndex   = r.GetInt(),
                        RoomName    = r.GetString(),
                        HasPosition = r.GetBool(),
                    };
                    if (room.HasPosition) room.Position = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                    room.ChildCount = r.GetInt();
                    manifest.Rooms.Add(room);
                }

                int unitCount = r.GetInt();
                for (int i = 0; i < unitCount; i++)
                {
                    var u = new NetLevelManifestUnit
                    {
                        ManifestIndex  = r.GetInt(),
                        SpawnIndex     = r.GetInt(),
                        UnitIdentifier = r.GetString(),
                        ActorName      = r.GetString(),
                        GameObjectName = r.GetString(),
                        SyncCategory   = r.GetInt(),
                        Category       = r.GetString(),
                        IsCombatEnemy  = r.GetBool(),
                        HasPosition    = r.GetBool(),
                    };
                    if (u.HasPosition) u.Position = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                    u.HasInitialPosition = r.GetBool();
                    if (u.HasInitialPosition) u.InitialPosition = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                    u.ModifierFlags        = r.GetString();
                    u.ComponentFingerprint = r.GetUInt();
                    u.IsDead               = r.GetBool();
                    manifest.Units.Add(u);
                }

                int specialCount = r.GetInt();
                for (int i = 0; i < specialCount; i++)
                {
                    var s = new NetLevelManifestSpecial
                    {
                        Type         = r.GetString(),
                        Name         = r.GetString(),
                        SyncCategory = r.GetInt(),
                        HasPosition  = r.GetBool(),
                    };
                    if (s.HasPosition) s.Position = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                    manifest.Specials.Add(s);
                }

                m = manifest;
                return true;
            }
            catch { return false; }
        }
    }
}
