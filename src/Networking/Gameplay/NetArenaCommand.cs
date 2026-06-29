using System.Collections.Generic;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase LD-2b/c — host-authoritative arena-lockdown command (Host→all clients). The host computes the non-in-room
    /// targets for an arena and tells those specific ends what to do. A receiving end acts only if its local peer id is
    /// in <see cref="TargetPeerIds"/>; the host applies its own ("host") target locally without a packet.
    /// </summary>
    internal enum ArenaCommandKind : byte
    {
        Seal     = 1, // LD-2b t0+5 s: raise the invisible two-way barrier at the local door.
        Popup    = 2, // LD-2c t0+10 s: show the confirm prompt + arm teleport (enter on confirm / boss death).
        Release  = 3, // LD-2c boss death / fight over: force teleport in + drop the barrier.
        Notify   = 4, // LD-2c t0: heads-up status toast to the OUT-OF-ROOM players (a teammate entered), no side effect.
        CloseDoor = 5, // LD-2d t0+5 s: grace over — close the local combat-room gate that was kept open during grace.
        NotifyEntered = 6, // LD-2e t0: heads-up toast to the player(s) who entered first (so they know it started too).
        Membership = 7, // RT3-Cousin-arms-Room: host broadcasts the arena's current in-room peer set (TargetPeerIds) so
                        // clients can filter the boss arm's group attack. Cached by ALL clients, no per-target side effect.
    }

    internal sealed class NetArenaCommand
    {
        public ArenaCommandKind Kind { get; set; }
        public Vector3 ArenaPos { get; set; }
        public List<string> TargetPeerIds { get; set; } = new List<string>();
    }
}
