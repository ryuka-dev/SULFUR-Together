using LiteNetLib.Utils;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Issue #5: a client's request for the HOST to run a one-shot <c>TriggerSpawner</c> (e.g. the Caves maze skeleton
    /// ambush) authoritatively.
    ///
    /// <para>Vanilla each machine runs its own <c>PlayerTrigger → Triggerable.Trigger → TriggerSpawner.Spawn</c> locally
    /// and one-shot per machine, so every player triggers their own local, unsynced skeletons. The fix makes the spawn
    /// host-authoritative: the client blocks its local spawn and sends this request; the host — if that trigger has not
    /// already fired for anyone (first-trigger-wins) — runs the real spawn, which then reaches every peer through the
    /// existing runtime-spawn mirror (<see cref="RuntimeSpawnManager"/>).</para>
    ///
    /// <para>The cross-machine identity is the trigger's world <see cref="Position"/> — the level is generated from the
    /// same seed on every peer, so the <c>Triggerable</c>/<c>TriggerSpawner</c> object sits at the same spot everywhere
    /// (the same determinism the BreakableBreak/roster-binding systems already rely on).</para>
    /// </summary>
    internal sealed class NetTriggerSpawn
    {
        public Vector3 Position    { get; set; }        // Triggerable/TriggerSpawner world position — the shared key.
        public int     UnitIdValue { get; set; }        // ushort UnitSO.id.value — diagnostic/validation only.
        public string  ChapterName { get; set; } = "";
        public int     LevelIndex  { get; set; } = -1;
        public bool    HasSeed     { get; set; }
        public int     Seed        { get; set; }

        public string ToCompact()
            => $"pos={Position:F1} unitId={UnitIdValue} run={ChapterName}:{LevelIndex}";
    }

    internal static class NetTriggerSpawnCodec
    {
        private const byte Version = 1;

        public static void Write(NetDataWriter w, NetTriggerSpawn m)
        {
            w.Put(Version);
            w.Put(m.Position.x); w.Put(m.Position.y); w.Put(m.Position.z);
            w.Put(m.UnitIdValue);
            w.Put(m.ChapterName ?? "");
            w.Put(m.LevelIndex);
            w.Put(m.HasSeed);
            if (m.HasSeed) w.Put(m.Seed);
        }

        public static bool TryRead(NetDataReader r, out NetTriggerSpawn result)
        {
            result = null!;
            try
            {
                if (r.GetByte() != Version) return false;
                var m = new NetTriggerSpawn
                {
                    Position = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat()),
                    UnitIdValue = r.GetInt(),
                    ChapterName = r.GetString(),
                    LevelIndex = r.GetInt(),
                    HasSeed = r.GetBool(),
                };
                if (m.HasSeed) m.Seed = r.GetInt();
                result = m;
                return true;
            }
            catch { return false; }
        }
    }
}
