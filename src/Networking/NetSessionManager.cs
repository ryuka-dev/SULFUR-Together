using System.Collections.Generic;
using System.Linq;

namespace SULFURTogether.Networking
{
    /// <summary>
    /// Owns Phase 2.2 session metadata.
    /// Host assigns peer ids and slots. Client stores the accepted local slot and host metadata.
    /// No gameplay objects are created or referenced here.
    /// </summary>
    public sealed class NetSessionManager
    {
        private readonly Dictionary<string, NetPeerSession> _sessionsById = new Dictionary<string, NetPeerSession>();
        private int _nextClientPeerNumber = 1;

        public int ConnectedCount => _sessionsById.Values.Count(s => s.State == NetConnectionState.Connected);
        public int RemoteConnectedCount => _sessionsById.Values.Count(s => s.State == NetConnectionState.Connected && !s.IsLocal);
        public IReadOnlyCollection<NetPeerSession> Sessions => _sessionsById.Values.ToList().AsReadOnly();

        public void Clear()
        {
            _sessionsById.Clear();
            _nextClientPeerNumber = 1;
        }

        public NetPeerSession RegisterLocalHost(string playerName, string modVersion, float now)
        {
            var session = new NetPeerSession
            {
                PeerId     = "host",
                PlayerName = SanitizeName(playerName, "Host"),
                ModVersion = modVersion ?? "",
                EndPoint   = "local",
                Slot       = 0,
                Role       = NetPeerRole.Host,
                State      = NetConnectionState.Connected,
                JoinedAt   = now,
                LastSeen   = now,
                IsLocal    = true,
            };
            _sessionsById[session.PeerId] = session;
            return session;
        }

        public NetPeerSession RegisterLocalClient(string peerId, int slot, string playerName, string modVersion, float now)
        {
            peerId = string.IsNullOrWhiteSpace(peerId) ? "client-local" : peerId;
            var session = new NetPeerSession
            {
                PeerId     = peerId,
                PlayerName = SanitizeName(playerName, "Client"),
                ModVersion = modVersion ?? "",
                EndPoint   = "local",
                Slot       = slot,
                Role       = NetPeerRole.Client,
                State      = NetConnectionState.Connected,
                JoinedAt   = now,
                LastSeen   = now,
                IsLocal    = true,
            };
            _sessionsById[session.PeerId] = session;
            return session;
        }

        public NetPeerSession RegisterRemoteHost(string peerId, string playerName, string modVersion, string endPoint, float now)
        {
            peerId = string.IsNullOrWhiteSpace(peerId) ? "host" : peerId;
            var session = new NetPeerSession
            {
                PeerId     = peerId,
                PlayerName = SanitizeName(playerName, "Host"),
                ModVersion = modVersion ?? "",
                EndPoint   = endPoint ?? "",
                Slot       = 0,
                Role       = NetPeerRole.Host,
                State      = NetConnectionState.Connected,
                JoinedAt   = now,
                LastSeen   = now,
                IsLocal    = false,
            };
            _sessionsById[session.PeerId] = session;
            return session;
        }

        public NetPeerSession RegisterRemoteClient(string playerName, string modVersion, string endPoint, float now)
        {
            int slot = AllocateClientSlot();
            string peerId = $"client-{_nextClientPeerNumber++}";

            var session = new NetPeerSession
            {
                PeerId     = peerId,
                PlayerName = SanitizeName(playerName, peerId),
                ModVersion = modVersion ?? "",
                EndPoint   = endPoint ?? "",
                Slot       = slot,
                Role       = NetPeerRole.Client,
                State      = NetConnectionState.Connected,
                JoinedAt   = now,
                LastSeen   = now,
                IsLocal    = false,
            };
            _sessionsById[session.PeerId] = session;
            return session;
        }

        public void Touch(string peerId, float now)
        {
            if (string.IsNullOrWhiteSpace(peerId)) return;
            if (_sessionsById.TryGetValue(peerId, out var session))
                session.LastSeen = now;
        }

        public void MarkDisconnected(string peerId)
        {
            if (string.IsNullOrWhiteSpace(peerId)) return;
            if (_sessionsById.TryGetValue(peerId, out var session))
                session.State = NetConnectionState.Disconnected;
        }

        public void Remove(string peerId)
        {
            if (string.IsNullOrWhiteSpace(peerId)) return;
            _sessionsById.Remove(peerId);
        }

        public string FormatStatus()
        {
            if (_sessionsById.Count == 0) return "sessions=0";
            var parts = _sessionsById.Values
                .OrderBy(s => s.Slot)
                .ThenBy(s => s.PeerId)
                .Select(s => s.ToCompactString());
            return $"sessions={_sessionsById.Count} [{string.Join("; ", parts)}]";
        }

        /// <summary>MP-Cap: lowest free client slot, unbounded. Slot 0 is the host; slots are display/ordering
        /// identity only (status text, run-stats ordering), so there is no upper bound to run out of. The loop
        /// terminates because at most <c>_sessionsById.Count</c> slots can be occupied.</summary>
        private int AllocateClientSlot()
        {
            for (int slot = 1; ; slot++)
            {
                bool used = _sessionsById.Values.Any(s => s.State == NetConnectionState.Connected && s.Slot == slot);
                if (!used) return slot;
            }
        }

        private static string SanitizeName(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            value = value.Trim();
            return value.Length > 32 ? value.Substring(0, 32) : value;
        }
    }
}
