using System;
using SULFURTogether.Networking.Gameplay;

namespace SULFURTogether.Networking.Session
{
    /// <summary>
    /// Every item id a gameplay message carries, in one place. Each method takes a message and the route it is about
    /// to travel and returns what may travel: the same instance when nothing changes, a copy when an id was mapped or
    /// left out, or null when the message is about an item that does not cross and must not be sent or applied.
    ///
    /// The input is never modified: the host hands one message object to several peers, each with its own route.
    ///
    /// Item names on the wire (the Endless card state: card keys, <c>RemovedCardItems.ItemName</c>, prefab names)
    /// are not here. A name is the same on every machine, and a name a machine does not know matches nothing.
    /// </summary>
    internal static class ItemIdMessageFilter
    {
        /// <summary>A dropped or mirrored pickup. Without its item there is no pickup; an attachment or enchantment
        /// that does not cross is left off the copy the receiver gets.</summary>
        public static NetWorldPickupSpawn? Apply(NetWorldPickupSpawn msg, ItemIdRoute route)
        {
            if (!route.TryMap(msg.ItemId, out int item)) return null;

            ushort[] attachments = MapList(msg.AttachmentIds, route);
            ushort[] enchantments = MapList(msg.EnchantmentIds, route);
            if (item == msg.ItemId && ReferenceEquals(attachments, msg.AttachmentIds) && ReferenceEquals(enchantments, msg.EnchantmentIds))
                return msg;

            NetWorldPickupSpawn copy = msg.ShallowCopy();
            copy.ItemId = (ushort)item;
            copy.AttachmentIds = attachments;
            copy.EnchantmentIds = enchantments;
            return copy;
        }

        /// <summary>The weapon a player holds. A weapon that does not cross is reported as empty hands, so the
        /// receiver clears whatever model it showed before.</summary>
        public static NetPlayerHeldWeapon Apply(NetPlayerHeldWeapon msg, ItemIdRoute route)
        {
            if (!msg.HasWeapon || msg.WeaponItemId == 0) return msg;

            if (!route.TryMap(msg.WeaponItemId, out int weapon))
            {
                NetPlayerHeldWeapon empty = msg.ShallowCopy();
                empty.HasWeapon = false;
                empty.WeaponItemId = 0;
                empty.AttachmentItemIds = Array.Empty<ushort>();
                return empty;
            }

            ushort[] attachments = MapList(msg.AttachmentItemIds, route);
            if (weapon == msg.WeaponItemId && ReferenceEquals(attachments, msg.AttachmentItemIds)) return msg;

            NetPlayerHeldWeapon copy = msg.ShallowCopy();
            copy.WeaponItemId = (ushort)weapon;
            copy.AttachmentItemIds = attachments;
            return copy;
        }

        /// <summary>A trigger pull. Only its weapon-sourced sounds name an item; one that does not cross is not
        /// played. A pull that fired nothing and has no sound left is nothing at all.</summary>
        public static NetPlayerWeaponFire? Apply(NetPlayerWeaponFire msg, ItemIdRoute route)
        {
            NetShotSound[] sounds = msg.ShotSounds ?? Array.Empty<NetShotSound>();
            NetShotSound[]? kept = null; // allocated on the first change
            int n = 0;
            for (int i = 0; i < sounds.Length; i++)
            {
                NetShotSound s = sounds[i];
                bool keep = true;
                if (RemoteShotSound.SourceIsWeaponItem(s.Slot))
                {
                    keep = route.TryMap(s.SourceId, out int mapped);
                    if (keep && mapped != s.SourceId)
                    {
                        kept ??= CopyPrefix(sounds, i);
                        s.SourceId = mapped;
                    }
                }
                if (!keep) kept ??= CopyPrefix(sounds, i);
                if (keep)
                {
                    if (kept != null) kept[n] = s;
                    n++;
                }
            }

            if (kept == null) return msg;
            if (n == 0 && msg.Count <= 0 && msg.BeamLoop == 0) return null;

            if (n != kept.Length) Array.Resize(ref kept, n);
            NetPlayerWeaponFire copy = msg.ShallowCopy();
            copy.ShotSounds = kept;
            return copy;
        }

        public static NetThrowableEffect? Apply(NetThrowableEffect msg, ItemIdRoute route)
        {
            if (!route.TryMap(msg.ItemIdValue, out int item)) return null;
            if (item == msg.ItemIdValue) return msg;
            NetThrowableEffect copy = msg.ShallowCopy();
            copy.ItemIdValue = item;
            return copy;
        }

        public static NetThrowableFlight? Apply(NetThrowableFlight msg, ItemIdRoute route)
        {
            if (!route.TryMap(msg.ItemIdValue, out int item)) return null;
            if (item == msg.ItemIdValue) return msg;
            NetThrowableFlight copy = msg.ShallowCopy();
            copy.ItemIdValue = item;
            return copy;
        }

        public static NetThrowableProjectile? Apply(NetThrowableProjectile msg, ItemIdRoute route)
        {
            if (!route.TryMap(msg.ItemIdValue, out int item)) return null;
            if (item == msg.ItemIdValue) return msg;
            NetThrowableProjectile copy = msg.ShallowCopy();
            copy.ItemIdValue = item;
            return copy;
        }

        /// <summary>An Endless card's chest or station. 0 means it holds no fixed item. When its fixed item does not
        /// cross, the whole object is left out: a chest mirrored without it would roll its own loot and hand the
        /// receiver a different item.</summary>
        public static NetEndlessInteractable? Apply(NetEndlessInteractable msg, ItemIdRoute route)
        {
            if (msg.StaticLootItemId == 0) return msg;
            if (!route.TryMap(msg.StaticLootItemId, out int item)) return null;
            if (item == msg.StaticLootItemId) return msg;
            NetEndlessInteractable copy = msg.ShallowCopy();
            copy.StaticLootItemId = item;
            return copy;
        }

        // The same array when every id maps to itself; otherwise a new one holding the mapped survivors in order.
        private static ushort[] MapList(ushort[]? ids, ItemIdRoute route)
        {
            if (ids == null || ids.Length == 0) return ids ?? Array.Empty<ushort>();

            ushort[]? result = null;
            int n = 0;
            for (int i = 0; i < ids.Length; i++)
            {
                bool keep = route.TryMap(ids[i], out int mapped);
                if (result == null && (!keep || mapped != ids[i]))
                {
                    result = new ushort[ids.Length];
                    Array.Copy(ids, result, i);
                }
                if (!keep) continue;
                if (result != null) result[n] = (ushort)mapped;
                n++;
            }

            if (result == null) return ids;
            if (n != result.Length) Array.Resize(ref result, n);
            return result;
        }

        private static NetShotSound[] CopyPrefix(NetShotSound[] source, int count)
        {
            var copy = new NetShotSound[source.Length];
            Array.Copy(source, copy, count);
            return copy;
        }
    }
}
