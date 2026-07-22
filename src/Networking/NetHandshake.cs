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
        // 16: EM req 2 added EndlessCardSelect (90) — client→host card-select target suppression; peers must agree on the wire set.
        // 17: EM-6b added EndlessCardManifest (91) — host→all shared card set; peers must agree on the wire set.
        // 18: EM-6b-2 added EndlessCardRoll (92) — host→all pre-roll card RNG+state; peers must agree on the wire set.
        // 19: EM-6b-3a added EndlessCardVoteState (93) + EndlessCardVoteCast (94) — shared 1-of-N card vote; wire set must match.
        // 23: EM-7b added a loot-locator beam (active + position) to the EndlessWaveState (86) snapshot (codec v2) — wire shape must match.
        // 24: EM-7e added EndlessInteractable (95) — host→all mirror of card-spawned non-unit interactables (chests/stations); wire set must match.
        // 25: IND-1 added EndlessWorldCard (96) — client→host Independent-mode companion routing; wire set must match.
        // 26: EM-Arena added arena-transition (event id + prefab index) to the EndlessWaveState (86) snapshot (codec v3) — wire shape must match.
        // 27: MP-Cap dropped the trailing maxPlayers field from HandshakeAccepted — the player cap is gone entirely.
        // 28: PK-1 added shouldJumpOff/inverted to the PikeJump payload of HostBossDiscreteEvent (codec v3) — wire shape must match.
        // 29: PK-2 added ClientPikeJump (97) — client→host desert-pike ambush request; wire set must match.
        // 30: ST-1/ST-2 added ClientUnitStatusRequest (98) + HostUnitStatusState (99) — enemy status effect authority;
        //     wire set must match (an older peer would apply enchantment statuses locally only, the bug this fixes).
        // 31: KD added OpenableDoorOpen (100) — crypt/locked door open sync; peers must agree on the wire set.
        // 32: AC added CryptChallengeState (101) — host-authoritative crypt challenge outcome + UI; wire set must match.
        public const int    ProtocolVersion  = 32;

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
            string hostPlayerName)
        {
            w.Put(assignedPeerId ?? "");
            w.Put(assignedSlot);
            w.Put(hostPeerId ?? "host");
            w.Put(hostPlayerName ?? "Host");
            w.Put(ModInfo.Version);
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
    }
}
