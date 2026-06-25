using LiteNetLib.Utils;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>Wire codecs for the world item-drop messages (spawn / take / removed). Manual field-by-field — the game
    /// ships no JSON serializer and <c>InventoryData</c>/<c>CharacterStat</c> carry runtime junk, so we serialize only
    /// the reduced field set that actually round-trips on re-pickup.</summary>
    internal static class NetWorldPickupCodec
    {
        private const byte Version       = 1;
        private const int  MaxList       = 32; // sanity bound for attachment/enchantment/attribute lists

        // ---------------------------------------------------------------- Spawn

        public static void WriteSpawn(NetDataWriter w, NetWorldPickupSpawn m)
        {
            w.Put(Version);
            w.Put(m.OwnerPeerId ?? "");
            w.Put(m.Seq);

            w.Put(m.ChapterName ?? "");
            w.Put(m.LevelIndex);
            w.Put(m.HasLevelSeed);
            if (m.HasLevelSeed) w.Put(m.LevelSeed);

            w.Put(m.Position.x); w.Put(m.Position.y); w.Put(m.Position.z);
            w.Put(m.ItemId);

            w.Put(m.HasData);
            if (m.HasData)
            {
                PutUShortList(w, m.AttachmentIds);
                PutUShortList(w, m.EnchantmentIds);
                w.Put(m.CaliberId);
                w.Put(m.CurrentAmmo);
                w.Put(m.Quantity);
                w.Put(m.Rotated);

                var ids = m.AttrIds ?? System.Array.Empty<ushort>();
                var vals = m.AttrValues ?? System.Array.Empty<float>();
                int n = ids.Length;
                if (n > vals.Length) n = vals.Length;
                if (n > MaxList) n = MaxList;
                w.Put((byte)n);
                for (int i = 0; i < n; i++) { w.Put(ids[i]); w.Put(vals[i]); }
            }

            w.Put(m.SentAt);
        }

        public static bool TryReadSpawn(NetDataReader r, out NetWorldPickupSpawn m)
        {
            m = new NetWorldPickupSpawn();
            try
            {
                if (r.GetByte() != Version) return false;
                m.OwnerPeerId = r.GetString();
                m.Seq = r.GetUShort();

                m.ChapterName = r.GetString();
                m.LevelIndex = r.GetInt();
                m.HasLevelSeed = r.GetBool();
                if (m.HasLevelSeed) m.LevelSeed = r.GetInt();

                m.Position = new UnityEngine.Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                m.ItemId = r.GetUShort();

                m.HasData = r.GetBool();
                if (m.HasData)
                {
                    if (!TryGetUShortList(r, out var attach)) return false;
                    if (!TryGetUShortList(r, out var ench)) return false;
                    m.AttachmentIds = attach;
                    m.EnchantmentIds = ench;
                    m.CaliberId = r.GetInt();
                    m.CurrentAmmo = r.GetInt();
                    m.Quantity = r.GetInt();
                    m.Rotated = r.GetBool();

                    int n = r.GetByte();
                    if (n < 0 || n > MaxList) return false;
                    var ids = new ushort[n];
                    var vals = new float[n];
                    for (int i = 0; i < n; i++) { ids[i] = r.GetUShort(); vals[i] = r.GetFloat(); }
                    m.AttrIds = ids;
                    m.AttrValues = vals;
                }

                m.SentAt = r.GetFloat();
                return true;
            }
            catch { return false; }
        }

        // ---------------------------------------------------------------- Take

        public static void WriteTake(NetDataWriter w, NetWorldPickupTake m)
        {
            w.Put(Version);
            w.Put(m.OwnerPeerId ?? "");
            w.Put(m.Seq);
            w.Put(m.SentAt);
        }

        public static bool TryReadTake(NetDataReader r, out NetWorldPickupTake m)
        {
            m = new NetWorldPickupTake();
            try
            {
                if (r.GetByte() != Version) return false;
                m.OwnerPeerId = r.GetString();
                m.Seq = r.GetUShort();
                m.SentAt = r.GetFloat();
                return true;
            }
            catch { return false; }
        }

        // ---------------------------------------------------------------- Removed

        public static void WriteRemoved(NetDataWriter w, NetWorldPickupRemoved m)
        {
            w.Put(Version);
            w.Put(m.OwnerPeerId ?? "");
            w.Put(m.Seq);
            w.Put(m.TakenByPeerId ?? "");
            w.Put(m.SentAt);
        }

        public static bool TryReadRemoved(NetDataReader r, out NetWorldPickupRemoved m)
        {
            m = new NetWorldPickupRemoved();
            try
            {
                if (r.GetByte() != Version) return false;
                m.OwnerPeerId = r.GetString();
                m.Seq = r.GetUShort();
                m.TakenByPeerId = r.GetString();
                m.SentAt = r.GetFloat();
                return true;
            }
            catch { return false; }
        }

        // ---------------------------------------------------------------- helpers

        private static void PutUShortList(NetDataWriter w, ushort[] list)
        {
            var a = list ?? System.Array.Empty<ushort>();
            int n = a.Length;
            if (n > MaxList) n = MaxList;
            w.Put((byte)n);
            for (int i = 0; i < n; i++) w.Put(a[i]);
        }

        private static bool TryGetUShortList(NetDataReader r, out ushort[] list)
        {
            list = System.Array.Empty<ushort>();
            int n = r.GetByte();
            if (n < 0 || n > MaxList) return false;
            var a = new ushort[n];
            for (int i = 0; i < n; i++) a[i] = r.GetUShort();
            list = a;
            return true;
        }
    }
}
