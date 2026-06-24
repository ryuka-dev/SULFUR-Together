using LiteNetLib.Utils;

namespace SULFURTogether.Networking.Gameplay
{
    internal static class NetPlayerHeldWeaponCodec
    {
        private const byte Version = 1;
        private const int  MaxAttachments = 16; // sanity bound

        public static void Write(NetDataWriter w, NetPlayerHeldWeapon m)
        {
            w.Put(Version);
            w.Put(m.PeerId ?? "");
            w.Put(m.HasWeapon);
            w.Put(m.WeaponItemId);

            var ids = m.AttachmentItemIds ?? System.Array.Empty<ushort>();
            int n = ids.Length;
            if (n > MaxAttachments) n = MaxAttachments;
            w.Put((byte)n);
            for (int i = 0; i < n; i++)
                w.Put(ids[i]);

            w.Put(m.SentAt);
        }

        public static bool TryRead(NetDataReader r, out NetPlayerHeldWeapon m)
        {
            m = new NetPlayerHeldWeapon();
            try
            {
                byte ver = r.GetByte();
                if (ver != Version) return false;

                m.PeerId = r.GetString();
                m.HasWeapon = r.GetBool();
                m.WeaponItemId = r.GetUShort();

                int n = r.GetByte();
                if (n < 0 || n > MaxAttachments) return false;
                var ids = new ushort[n];
                for (int i = 0; i < n; i++)
                    ids[i] = r.GetUShort();
                m.AttachmentItemIds = ids;

                m.SentAt = r.GetFloat();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
