using LiteNetLib.Utils;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase 5.5-RT1: a host-authoritative runtime (post-level-stabilization) unit spawn. Level-load enemies are bound
    /// via the WorldRoster (position match); runtime spawns (boss adds, F3 debug spawns) are NOT in the roster, so the
    /// client cannot bind them and the host's state/attack/death broadcasts (keyed by SpawnIndex) have no local target.
    ///
    /// The cross-end-stable identity is <see cref="UnitIdValue"/> = UnitSO.id.value (ushort; the same UnitIds registry on
    /// both ends) — the client resolves it via UnitDatabase[new UnitId(value)] → UnitSO and mirror-spawns the same unit
    /// at the host position, then binds it to <see cref="HostSpawnIndex"/> so the existing EnemyPuppet pipeline drives it.
    ///
    /// Stage 1 carries HOST-side one-sided spawns (F3 DevTools) only; boss adds (spawned on both ends) come later with
    /// client-side suppression so they don't double.
    /// </summary>
    internal sealed class NetRuntimeSpawn
    {
        public int     UnitIdValue   { get; set; }   // ushort UnitSO.id.value
        public Vector3 Position      { get; set; }
        public float   RotationY     { get; set; }
        public int     HostSpawnIndex { get; set; }
        public string  ChapterName   { get; set; } = "";
        public int     LevelIndex    { get; set; } = -1;
        public bool    HasSeed       { get; set; }
        public int     Seed          { get; set; }
        public string  Source        { get; set; } = "";

        public string ToCompact()
            => $"unitId={UnitIdValue} pos={Position:F1} hostIdx={HostSpawnIndex} run={ChapterName}:{LevelIndex} src={Source}";
    }

    internal static class NetRuntimeSpawnCodec
    {
        private const byte Version = 1;

        public static void Write(NetDataWriter w, NetRuntimeSpawn m)
        {
            w.Put(Version);
            w.Put(m.UnitIdValue);
            w.Put(m.Position.x); w.Put(m.Position.y); w.Put(m.Position.z);
            w.Put(m.RotationY);
            w.Put(m.HostSpawnIndex);
            w.Put(m.ChapterName ?? "");
            w.Put(m.LevelIndex);
            w.Put(m.HasSeed);
            if (m.HasSeed) w.Put(m.Seed);
            w.Put(m.Source ?? "");
        }

        public static bool TryRead(NetDataReader r, out NetRuntimeSpawn result)
        {
            result = null!;
            try
            {
                if (r.GetByte() != Version) return false;
                var m = new NetRuntimeSpawn
                {
                    UnitIdValue = r.GetInt(),
                    Position = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat()),
                    RotationY = r.GetFloat(),
                    HostSpawnIndex = r.GetInt(),
                    ChapterName = r.GetString(),
                    LevelIndex = r.GetInt(),
                    HasSeed = r.GetBool(),
                };
                if (m.HasSeed) m.Seed = r.GetInt();
                m.Source = r.GetString();
                result = m;
                return true;
            }
            catch { return false; }
        }
    }
}
