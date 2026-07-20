using System.Collections.Generic;

namespace SULFURTogether.Api
{
    /// <summary>The local peer's role in the current session.</summary>
    public enum SessionRole : byte
    {
        Offline = 0,
        Host    = 1,
        Client  = 2,
    }

    /// <summary>
    /// A connected session member. Identity only — no gameplay Player/Unit reference, no transport handle, no
    /// position. A companion mod maps this to its own participant concept; <see cref="PlayerIndex"/> is the one
    /// bridge to the game roster, so an authoritative host can address a specific peer's player.
    /// </summary>
    public readonly struct ExternalPeer
    {
        public ExternalPeer(string peerId, bool isHost, bool isLocal, int playerIndex = -1)
        {
            PeerId      = peerId ?? "";
            IsHost      = isHost;
            IsLocal     = isLocal;
            PlayerIndex = playerIndex;
        }

        /// <summary>Stable session peer id (the host's id is "host"). Not a display name, not a transport handle.</summary>
        public string PeerId  { get; }
        public bool   IsHost  { get; }
        public bool   IsLocal { get; }

        /// <summary>
        /// The index of this peer's player in the <b>local</b> machine's <c>GameManager.Players</c>, or <c>-1</c>
        /// when not resolvable here. The host populates it for remote peers (from the headless player it already
        /// tracks per peer); a client keeps no such per-peer players, so it reports <c>-1</c> for others. A companion
        /// mod resolves its own local player index directly and uses this only to route a decision about a remote
        /// peer's player back to that peer. Not stable across machines — it is each machine's own roster index.
        /// </summary>
        public int PlayerIndex { get; }
    }

    /// <summary>
    /// Public, read-only view of SULFUR Together's current session membership and role, for a companion mod that
    /// runs its own host-authoritative flow (e.g. an arena ready gate) over <see cref="NetExternalChannel"/>.
    /// Membership is observed by polling these members; nothing here carries gameplay authority.
    /// </summary>
    public static class NetSessionInfo
    {
        /// <summary>The local peer's role, or <see cref="SessionRole.Offline"/> when no session is running.</summary>
        public static SessionRole Role => SULFURTogether.Networking.NetExternalBridge.Role;

        /// <summary>True while a host or client session is running.</summary>
        public static bool IsSessionActive => SULFURTogether.Networking.NetExternalBridge.IsSessionActive;

        /// <summary>The local peer's stable session id ("" when offline).</summary>
        public static string LocalPeerId => SULFURTogether.Networking.NetExternalBridge.LocalPeerId;

        /// <summary>Snapshot of currently-connected session members, including the local peer. Empty when offline.</summary>
        public static IReadOnlyList<ExternalPeer> Peers => SULFURTogether.Networking.NetExternalBridge.Peers;
    }
}
