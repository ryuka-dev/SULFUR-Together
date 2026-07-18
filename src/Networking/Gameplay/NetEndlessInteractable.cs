using System;
using LiteNetLib.Utils;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// EM-7e (host → all, Shared mode): one non-unit interactable a card spawned into the shared world
    /// (<c>FloatingCardManager.ExecuteReward</c> → <c>CardRewardType.SpawnInteractable</c>: chests, storage stashes,
    /// service stations). These are plain <c>Interactable</c> prefabs instantiated with <c>Object.Instantiate</c> — not
    /// Units — so they can't ride the RuntimeSpawn puppet pipeline (which keys on <c>UnitSO.id</c>). The host is
    /// authoritative: it spawns the object once (vanilla), reads the spawned root's world position + prefab name, and
    /// broadcasts this so the client instantiates the same prefab at the same spot. Without it, both ends run
    /// <c>ExecuteReward</c> and each spawns its own interactable at a <b>player-motion-dependent</b> position (§1.4),
    /// producing two divergent chests.
    ///
    /// <para>The prefab is resolved cross-end via the reward: <c>CardReward(cardKey).itemPool</c> is a serialized array
    /// identical on both builds, so the prefab whose <c>Object.name</c> equals <see cref="PrefabName"/> is the same asset
    /// on the client — no runtime RNG parity needed.</para>
    /// </summary>
    internal sealed class NetEndlessInteractable
    {
        public string  ChapterName = "";
        public int     LevelIndex;
        public bool    HasLevelSeed;
        public int     LevelSeed;
        public int     SpawnId;        // monotonic per host run; dedup + client cleanup keying
        public string  CardKey = "";   // CardReward.cardKey → resolves the reward (its itemPool OR containerPrefab holds the prefab)
        public string  PrefabName = ""; // the spawned root prefab's Object.name (itemPool / containerPrefab name; "(Clone)" stripped)
        public int     StaticLootItemId; // Container path only (SpawnFromLootTable): the chest's StaticLoot ItemId (0 = none)
        public Vector3 Position;
        public float   RotationY;
    }

    internal static class NetEndlessInteractableCodec
    {
        private const byte Version = 1;

        public static void Write(NetDataWriter w, NetEndlessInteractable m)
        {
            w.Put(Version);
            w.Put(m.ChapterName ?? "");
            w.Put(m.LevelIndex);
            w.Put(m.HasLevelSeed);
            w.Put(m.LevelSeed);
            w.Put(m.SpawnId);
            w.Put(m.CardKey ?? "");
            w.Put(m.PrefabName ?? "");
            w.Put(m.StaticLootItemId);
            w.Put(m.Position.x); w.Put(m.Position.y); w.Put(m.Position.z);
            w.Put(m.RotationY);
        }

        public static bool TryRead(NetDataReader r, out NetEndlessInteractable m)
        {
            m = new NetEndlessInteractable();
            try
            {
                byte ver = r.GetByte();
                if (ver != Version) return false;
                m.ChapterName  = r.GetString();
                m.LevelIndex   = r.GetInt();
                m.HasLevelSeed = r.GetBool();
                m.LevelSeed    = r.GetInt();
                m.SpawnId          = r.GetInt();
                m.CardKey          = r.GetString();
                m.PrefabName       = r.GetString();
                m.StaticLootItemId = r.GetInt();
                float x = r.GetFloat(), y = r.GetFloat(), z = r.GetFloat();
                m.Position     = new Vector3(x, y, z);
                m.RotationY    = r.GetFloat();
                return true;
            }
            catch { return false; }
        }
    }
}
