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
        Notify   = 4, // LD-2c t0: heads-up status toast only (a teammate entered the arena), no side effect.
    }

    internal sealed class NetArenaCommand
    {
        public ArenaCommandKind Kind { get; set; }
        public Vector3 ArenaPos { get; set; }
        public List<string> TargetPeerIds { get; set; } = new List<string>();
    }
}
