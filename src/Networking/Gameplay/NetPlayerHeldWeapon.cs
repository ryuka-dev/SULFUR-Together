namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase 5.6-WS-2 remote held weapon model. Broadcast by a peer whenever its local player's held weapon (or its
    /// installed attachments) changes — plus a low-rate heartbeat so late-joining peers catch up. Receivers rebuild the
    /// weapon model from the WeaponSO id + attachment ItemIds (attachments change the model) and attach it to that
    /// player's proxy hands. VISUAL ONLY.
    /// </summary>
    internal sealed class NetPlayerHeldWeapon
    {
        public string PeerId { get; set; } = "";

        // false → the player is holding no weapon (melee/empty/holstered) → clear the proxy's weapon model.
        public bool HasWeapon { get; set; }

        // WeaponSO id (ItemId.value) — resolved on the receiver via AsyncAssetLoading.itemDatabase.
        public ushort WeaponItemId { get; set; }

        // Installed attachment ItemIds (ItemId.value). Order-independent; the receiver shows the matching
        // WeaponAttachmentVisual children on the rebuilt model.
        public ushort[] AttachmentItemIds { get; set; } = System.Array.Empty<ushort>();

        public float SentAt { get; set; }

        /// <summary>Stable signature so receivers only rebuild the model when the weapon or attachment set changes.</summary>
        public string Signature()
        {
            if (!HasWeapon) return "none";
            var ids = AttachmentItemIds ?? System.Array.Empty<ushort>();
            var sorted = (ushort[])ids.Clone();
            System.Array.Sort(sorted);
            return WeaponItemId + ":" + string.Join(",", sorted);
        }
    }
}
