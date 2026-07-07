using LiteNetLib.Utils;

namespace SULFURTogether.Networking
{
    internal static class NetHandshake
    {
        public const string ProtocolMagic    = "SULFUR_TOGETHER";
        // 2: FF-1 added PlayerFriendlyFireHit (69) + SessionSettings (70) — older builds don't know either message.
        public const int    ProtocolVersion  = 2;

        // Client writes after connection is established.
        public static void WriteRequest(NetDataWriter w, string playerName)
        {
            w.Put(ProtocolMagic);
            w.Put(ProtocolVersion);
            w.Put(ModInfo.Version);
            w.Put(playerName);
            w.Put(Plugin.Cfg.ConnectionKey.Value);
        }

        // Host reads from incoming HandshakeRequest payload.
        public static bool TryReadRequest(NetDataReader r, out HandshakeData data)
        {
            data = new HandshakeData();
            try
            {
                data.Magic           = r.GetString();
                data.ProtocolVersion = r.GetInt();
                data.ModVersion      = r.GetString();
                data.PlayerName      = r.GetString();
                data.ConnectionKey   = r.GetString();
                return true;
            }
            catch { return false; }
        }

        // Host writes after accepting the client. This is Phase 2.2 session metadata only.
        public static void WriteAccepted(
            NetDataWriter w,
            string assignedPeerId,
            int assignedSlot,
            string hostPeerId,
            string hostPlayerName,
            int maxPlayers)
        {
            w.Put(assignedPeerId ?? "");
            w.Put(assignedSlot);
            w.Put(hostPeerId ?? "host");
            w.Put(hostPlayerName ?? "Host");
            w.Put(ModInfo.Version);
            w.Put(maxPlayers);
        }

        // Client reads assigned peer id / slot from HandshakeAccepted.
        public static bool TryReadAccepted(NetDataReader r, out HandshakeAcceptedData data)
        {
            data = new HandshakeAcceptedData();
            try
            {
                data.AssignedPeerId = r.GetString();
                data.AssignedSlot   = r.GetInt();
                data.HostPeerId     = r.GetString();
                data.HostPlayerName = r.GetString();
                data.HostModVersion = r.GetString();
                data.MaxPlayers     = r.GetInt();
                return true;
            }
            catch { return false; }
        }
    }

    internal class HandshakeData
    {
        public string Magic           = "";
        public int    ProtocolVersion;
        public string ModVersion      = "";
        public string PlayerName      = "";
        public string ConnectionKey   = "";
    }

    internal class HandshakeAcceptedData
    {
        public string AssignedPeerId = "";
        public int    AssignedSlot   = -1;
        public string HostPeerId     = "host";
        public string HostPlayerName = "Host";
        public string HostModVersion = "";
        public int    MaxPlayers     = 1;
    }
}
