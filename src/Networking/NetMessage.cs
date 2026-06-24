using LiteNetLib.Utils;

namespace SULFURTogether.Networking
{
    // Factory: creates a NetDataWriter with the message type byte already written.
    internal static class NetMessage
    {
        public static NetDataWriter For(NetMessageType type)
        {
            var w = new NetDataWriter();
            w.Put((byte)type);
            return w;
        }
    }
}
