using LiteNetLib.Utils;

namespace SULFURTogether.Networking
{
    internal static class NetHandshake
    {
        public const string ProtocolMagic    = "SULFUR_TOGETHER";
        // 2: FF-1 added PlayerFriendlyFireHit (69) + SessionSettings (70) — older builds don't know either message.
        // 3: WID-2 added WorldPickupSettle (75) — world-drop rest-position sync; peers must agree on the wire set.
        // 4: DEV-1 added the dev-entitlement handshake field + DeveloperMode in the session-settings snapshot.
        // 5: DB-1 added DoorBlockerOpen (79) — the 0.18 inter-chunk door sync; peers must agree on the wire set.
        // 6: K-1 added ThrowableProjectile (80) — ThrowingKnives flight visual sync; peers must agree on the wire set.
        // 7: SL-2 added ChestOpenRequest (81) + ChestOpened (82) — shared-loot chest sync; peers must agree on the wire set.
        // 8: SL-4 added a SharedLoot field to the SessionSettings (70) snapshot — peers must agree on its wire shape.
        // 9: SL-2b added LootableTriggerRequest (83) + LootableTriggered (84) — food/material/register loot sync.
        // 10: TD-1 added TargetDummyDamage (85) — shared target-dummy damage numbers; peers must agree on the wire set.
        public const int    ProtocolVersion  = 11;

        // Client writes after connection is established.
        public static void WriteRequest(NetDataWriter w, string playerName)
        {
            w.Put(ProtocolMagic);
            w.Put(ProtocolVersion);
            w.Put(ModInfo.Version);
            w.Put(playerName);
            w.Put(Plugin.Cfg.ConnectionKey.Value);
            w.Put(CoopDevEntitlement.Local); // DEV-1: does this client have developer access (-dev true / DevToolsEnabled)?
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
                data.DevEntitlement  = r.GetBool();
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
        public bool   DevEntitlement;   // DEV-1: client launched with developer access
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
