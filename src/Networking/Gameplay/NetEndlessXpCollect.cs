using LiteNetLib.Utils;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase EM-5b: the collection half of a host-authoritative Endless XP pickup. Used both ways:
    /// <list type="bullet">
    /// <item><b>EndlessXpCollectRequest</b> (client → host): a client's local player reached the pickup — asks the host
    /// to award it. Only <see cref="DropId"/> is meaningful.</item>
    /// <item><b>EndlessXpCollected</b> (host → all): the host resolved first-collector-wins. Every end removes the
    /// pickup's orbs; in Independent mode the peer whose id equals <see cref="CollectorPeerId"/> gains
    /// <see cref="TotalXp"/> (the host applies the Shared-mode pool add itself before broadcasting).</item>
    /// </list>
    /// </summary>
    internal sealed class NetEndlessXpCollect
    {
        public int    DropId         { get; set; }
        public string CollectorPeerId { get; set; } = "";
        public int    TotalXp        { get; set; }
    }

    internal static class NetEndlessXpCollectCodec
    {
        private const byte Version = 1;

        public static void Write(NetDataWriter w, NetEndlessXpCollect m)
        {
            w.Put(Version);
            w.Put(m.DropId);
            w.Put(m.CollectorPeerId ?? "");
            w.Put(m.TotalXp);
        }

        public static bool TryRead(NetDataReader r, out NetEndlessXpCollect m)
        {
            m = new NetEndlessXpCollect();
            try
            {
                byte ver = r.GetByte();
                if (ver != Version) return false;
                m.DropId          = r.GetInt();
                m.CollectorPeerId = r.GetString();
                m.TotalXp         = r.GetInt();
                return true;
            }
            catch { return false; }
        }
    }
}
