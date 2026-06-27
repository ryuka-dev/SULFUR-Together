using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase LD-2a — a client reports "my local player crossed an arena seal trigger" (Client→Host). The seal trigger
    /// is the <c>PlayerTrigger</c> that closes a combat-room gate/door (LD-1 MetalGate.Close or LD-1b door SetActive).
    /// The host uses these to build the host-authoritative in-room set per arena (keyed by the trigger's world position)
    /// and to anchor the lockdown timer (first cross = t0). The host's own crossings are reported locally (no message).
    /// </summary>
    internal sealed class NetClientArenaEnter
    {
        public string PeerId { get; set; } = ""; // stamped by the host from the source peer

        public string ChapterName  { get; set; } = "";
        public int    LevelIndex   { get; set; } = -1;
        public bool   HasLevelSeed { get; set; }
        public int    LevelSeed    { get; set; }

        // World-position key of the seal trigger the local player crossed.
        public Vector3 ArenaPos { get; set; }
    }
}
