using LiteNetLib.Utils;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// EM req 2 (client → host): the client entered (<see cref="Selecting"/> = true) or left (false) Independent-mode
    /// Endless card selection. The host marks that client's ghost unit invulnerable so Endless enemies stop targeting the
    /// selecting player (GrabHostileUnit excludes invulnerable units). The sending peer is identified by the transport
    /// peer on the host, so no peer id travels in the payload.
    /// </summary>
    internal sealed class NetEndlessCardSelect
    {
        public bool Selecting { get; set; }
    }

    internal static class NetEndlessCardSelectCodec
    {
        private const byte Version = 1;

        public static void Write(NetDataWriter w, NetEndlessCardSelect m)
        {
            w.Put(Version);
            w.Put(m.Selecting);
        }

        public static bool TryRead(NetDataReader r, out NetEndlessCardSelect m)
        {
            m = new NetEndlessCardSelect();
            try
            {
                byte ver = r.GetByte();
                if (ver != Version) return false;
                m.Selecting = r.GetBool();
                return true;
            }
            catch { return false; }
        }
    }
}
