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
    /// position. A companion mod maps this to its own participant concept.
    /// </summary>
    public readonly struct ExternalPeer
    {
        public ExternalPeer(string peerId, bool isHost, bool isLocal)
        {
            PeerId  = peerId ?? "";
            IsHost  = isHost;
            IsLocal = isLocal;
        }

        /// <summary>Stable session peer id (the host's id is "host"). Not a display name, not a transport handle.</summary>
        public string PeerId  { get; }
        public bool   IsHost  { get; }
        public bool   IsLocal { get; }
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
