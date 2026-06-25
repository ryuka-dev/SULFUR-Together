using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// World item-drop sync (player-thrown items first; all loot later under a host room-setting toggle).
    /// <para>Every dynamic world pickup is born through the single chokepoint
    /// <c>InteractionManager.SpawnPickup(...)</c>. A pickup that carries a non-null <c>InventoryData</c> was
    /// <b>dropped by the player</b> (<c>Pickup.DroppedByPlayer</c>); loot carries <c>null</c>. The mode filter keys on
    /// exactly that: Independent mode syncs only player drops, Shared mode syncs every pickup.</para>
    ///
    /// <para><b>Identity.</b> World-pickup positions are NOT deterministic across peers (drop position depends on the
    /// local player's aim raycast; loot uses random impulse + per-peer RNG), so — unlike <see cref="NetBreakableBreak"/>
    /// — we cannot match by position. Each synced pickup gets a composite id <c>{OwnerPeerId, Seq}</c> assigned by the
    /// dropping peer (globally unique with no round-trip), carried on the spawn event and referenced by the take/removed
    /// events.</para>
    ///
    /// <para><b>Topology.</b> Spawn is optimistic + peer-authoritative (the dropping peer spawns its real pickup
    /// instantly and broadcasts; same Client→Host→relay shape as <c>PlayerWeaponFire</c>/<c>BreakableBreak</c>). Take is
    /// host-authoritative (first requester wins): a client asks the host, the host grants exactly one and broadcasts the
    /// removal so the item vanishes on every screen and only the winner receives it. This gives the future Shared-loot
    /// "first picker takes it, it disappears for everyone" semantics for free.</para>
    /// </summary>
    internal sealed class NetWorldPickupSpawn
    {
        // ---- identity ----
        public string OwnerPeerId { get; set; } = ""; // dropping peer (set by the dropper, not re-stamped by host)
        public ushort Seq         { get; set; }       // dropper-local monotonic sequence

        // ---- scene context (a receiver in a different level ignores it) ----
        public string ChapterName  { get; set; } = "";
        public int    LevelIndex   { get; set; } = -1;
        public bool   HasLevelSeed { get; set; }
        public int    LevelSeed    { get; set; }

        // ---- world placement ----
        public Vector3 Position { get; set; }

        // ---- item ----
        public ushort ItemId { get; set; } // ItemDefinition / WeaponSO id (ItemId.value)

        // ---- DIY payload (only meaningful when HasData; loot has none) ----
        public bool     HasData        { get; set; }
        public ushort[] AttachmentIds  { get; set; } = System.Array.Empty<ushort>();
        public ushort[] EnchantmentIds { get; set; } = System.Array.Empty<ushort>();
        public int      CaliberId      { get; set; } // CaliberTypes enum
        public int      CurrentAmmo    { get; set; }
        public int      Quantity       { get; set; } = 1;
        public bool     Rotated        { get; set; }
        // Serialized item attributes that round-trip on re-pickup (only Durability+Experience survive
        // ItemStats.LoadAttributesFromData; everything else is rebuilt from attachments/enchantments/caliber).
        public ushort[] AttrIds        { get; set; } = System.Array.Empty<ushort>();   // ItemAttributes enum
        public float[]  AttrValues     { get; set; } = System.Array.Empty<float>();    // CharacterStat.BaseValue

        public float SentAt { get; set; }

        public string Key => OwnerPeerId + "#" + Seq;

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

    /// <summary>Client → Host: "I want to take this world pickup." The host grants the first valid requester.</summary>
    internal sealed class NetWorldPickupTake
    {
        public string OwnerPeerId { get; set; } = "";
        public ushort Seq         { get; set; }
        public float  SentAt      { get; set; }

        public string Key => OwnerPeerId + "#" + Seq;
    }

    /// <summary>
    /// Host → All: "this world pickup was taken (or removed)." Every peer removes its local instance; the
    /// <see cref="TakenByPeerId"/> peer adds the item to its inventory (using the InventoryData it already holds from the
    /// spawn). Empty <see cref="TakenByPeerId"/> = removed without anyone taking it (reserved).
    /// </summary>
    internal sealed class NetWorldPickupRemoved
    {
        public string OwnerPeerId   { get; set; } = "";
        public ushort Seq           { get; set; }
        public string TakenByPeerId { get; set; } = "";
        public float  SentAt        { get; set; }

        public string Key => OwnerPeerId + "#" + Seq;
    }
}
