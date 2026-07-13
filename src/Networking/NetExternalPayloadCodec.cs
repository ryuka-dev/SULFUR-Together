using LiteNetLib.Utils;

namespace SULFURTogether.Networking
{
    /// <summary>
    /// Wire framing for <see cref="NetMessageType.ExternalModPayload"/>: an opaque {channelId, payload} an
    /// external mod ships over the session transport. ST writes and reads the envelope but never interprets the
    /// payload bytes. The sender peer id is NOT on the wire — it is stamped from the authenticated connection.
    /// </summary>
    internal static class NetExternalPayloadCodec
    {
        private const byte Version = 1;

        public static void Write(NetDataWriter w, string channelId, byte[] payload)
        {
            w.Put(Version);
            w.Put(channelId ?? "");
            w.PutBytesWithLength(payload ?? System.Array.Empty<byte>());
        }

        public static bool TryRead(NetDataReader r, out string channelId, out byte[] payload)
        {
            channelId = "";
            payload = System.Array.Empty<byte>();
            try
            {
                byte ver = r.GetByte();
                if (ver != Version) return false;

                channelId = r.GetString();
                if (string.IsNullOrEmpty(channelId) || channelId.Length > NetExternalBridge.MaxChannelIdLength) return false;

                byte[] buf = r.GetBytesWithLength();
                if (buf == null || buf.Length > NetExternalBridge.MaxPayloadBytes) return false;

                payload = buf;
                return true;
            }
            catch { return false; }
        }
    }
}
