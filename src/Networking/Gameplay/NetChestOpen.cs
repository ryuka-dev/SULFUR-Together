using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// SL-2 (Shared-loot) chest (<c>Container</c>) open sync — carried by BOTH the client→host request
    /// (<c>ChestOpenRequest</c>) and the host→all broadcast (<c>ChestOpened</c>); same shape, different direction.
    /// <para>Model A (host-authoritative rolling): the chest's loot is rolled with local RNG in
    /// <c>Container.SetContainedItem</c>, so only the HOST may roll it. A client that interacts blocks its own open and
    /// asks the host (this message as a request); the host opens its matching chest — which rolls + <c>SpawnPickup</c>s,
    /// mirrored to everyone by the shared-loot world-drop channel — then broadcasts this message as <c>ChestOpened</c> so
    /// every peer plays the open animation and marks the chest looted (visual only, no re-roll).</para>
    /// <para>Identity is the chest's deterministic world position (level-gen places containers from the synced seed), the
    /// same keying as <see cref="NetDoorBlockerOpen"/> / <see cref="NetBreakableBreak"/>.</para>
    /// </summary>
    internal sealed class NetChestOpen
    {
        // Which player opened / requested (stamped by the Host from the source peer).
        public string PeerId { get; set; } = "";

        // Scene context (a receiver in a different level must ignore it).
        public string ChapterName  { get; set; } = "";
        public int    LevelIndex   { get; set; } = -1;
        public bool   HasLevelSeed { get; set; }
        public int    LevelSeed    { get; set; }

        // De-dup / ordering.
        public int   Sequence { get; set; }
        public float SentAt   { get; set; }

        // Deterministic world-position key of the container (level-gen placed; the root never moves).
        public Vector3 Position { get; set; }

        public bool MatchesScene(NetRunState localState)
        {
            if (!localState.HasLevel) return false;
            if (!string.Equals(localState.ChapterName, ChapterName, System.StringComparison.Ordinal)) return false;
            if (localState.LevelIndex != LevelIndex) return false;
            if (Plugin.Cfg.EnableLevelSeedAuthority.Value)
            {
                if (!HasLevelSeed || !localState.HasLevelSeed) return false;
                if (localState.LevelSeed != LevelSeed) return false;
            }
            return true;
        }
    }
}
