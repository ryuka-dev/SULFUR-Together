using System.Collections.Generic;

namespace SULFURTogether.Networking.Session
{
    /// <summary>
    /// How item ids cross the link to one peer. An item id is a number each process assigns to its own content, so
    /// the same number only means the same item when both ends have that content in the same state. A policy answers,
    /// for one id and one direction, either "this is its number on the wire" or "it does not cross".
    ///
    /// Both ends of a link apply their policy to the same id: the sender with <see cref="TryToWire"/>, the receiver
    /// with <see cref="TryFromWire"/>. So a link has exactly one numbering on the wire, fixed by what the two ends
    /// learned about each other in the handshake, and each end converts between that and its own. For the policies
    /// below the wire numbering is every machine's own, which is the same thing for each id they let through. A
    /// policy that translates must name one numbering per link and convert to and from it on both sides; it must not
    /// send the receiver's numbers, which the receiver would convert a second time.
    /// </summary>
    internal interface IPeerItemIdPolicy
    {
        /// <summary>An id of this machine, about to be sent to the peer.</summary>
        bool TryToWire(int localId, out int wireId);

        /// <summary>An id as the peer sent it, about to be used on this machine.</summary>
        bool TryFromWire(int wireId, out int localId);
    }

    /// <summary>Both ends have the same content: every id means the same thing on either side.</summary>
    internal sealed class MatchedItemIdPolicy : IPeerItemIdPolicy
    {
        public static readonly MatchedItemIdPolicy Instance = new MatchedItemIdPolicy();
        private MatchedItemIdPolicy() { }

        public bool TryToWire(int localId, out int wireId)   { wireId = localId; return true; }
        public bool TryFromWire(int wireId, out int localId) { localId = wireId; return true; }
    }

    /// <summary>
    /// Nothing is known about the peer's content yet: it has not completed the handshake, or the link dropped. Only
    /// the game's own ids cross. Everything above them could be another mod's numbering and is withheld.
    /// </summary>
    internal sealed class UnknownPeerItemIdPolicy : IPeerItemIdPolicy
    {
        public static readonly UnknownPeerItemIdPolicy Instance = new UnknownPeerItemIdPolicy();
        private UnknownPeerItemIdPolicy() { }

        public bool TryToWire(int localId, out int wireId)
        {
            wireId = localId;
            return localId <= VanillaItemIds.Ceiling;
        }

        public bool TryFromWire(int wireId, out int localId)
        {
            localId = wireId;
            return wireId <= VanillaItemIds.Ceiling;
        }
    }

    /// <summary>
    /// One direction of travel for an item id, resolved to a single question a message filter can ask per field.
    /// Every id is checked against the id domain first: a wire field wider than an item id must not be able to carry
    /// a value that only becomes a real id once it is narrowed.
    /// </summary>
    internal readonly struct ItemIdRoute
    {
        private readonly IPeerItemIdPolicy? _from; // the peer the id came from; null when it is this machine's own
        private readonly IPeerItemIdPolicy? _to;   // the peer the id goes to; null when it is used on this machine

        public ItemIdRoute(IPeerItemIdPolicy? from, IPeerItemIdPolicy? to)
        {
            _from = from;
            _to = to;
        }

        /// <summary>
        /// Carry <paramref name="id"/> along this route. False when it does not cross, in which case whatever it
        /// identified is left out for the receiver. The mapped id is always a valid item id.
        /// </summary>
        public bool TryMap(int id, out int mapped)
        {
            mapped = 0;
            if (id < 1 || id > ushort.MaxValue) return false;

            int value = id;
            if (_from != null && !_from.TryFromWire(value, out value)) return false;
            if (value < 1 || value > ushort.MaxValue) return false;
            if (_to != null && !_to.TryToWire(value, out value)) return false;
            if (value < 1 || value > ushort.MaxValue) return false;

            mapped = value;
            return true;
        }
    }

    /// <summary>
    /// The item-id policy of every peer of one network session. Owned by the session's <c>NetService</c> and dropped
    /// with it. Peers are named by their session peer id; the client's single link to the host uses
    /// <see cref="HostLink"/>, never a string the host sent.
    ///
    /// A peer without an entry is unknown (<see cref="UnknownPeerItemIdPolicy"/>): the policy is set when the
    /// handshake has compared both ends' content and cleared when the peer goes.
    /// </summary>
    internal sealed class NetItemIdSeam
    {
        /// <summary>The key a client uses for its link to the host.</summary>
        public const string HostLink = "@host-link"; // not a value a session peer id can take

        private readonly Dictionary<string, IPeerItemIdPolicy> _policies = new Dictionary<string, IPeerItemIdPolicy>();

        public void SetPolicy(string peerId, IPeerItemIdPolicy policy)
        {
            if (string.IsNullOrEmpty(peerId) || policy == null) return;
            _policies[peerId] = policy;
        }

        public void Clear(string? peerId)
        {
            if (!string.IsNullOrEmpty(peerId)) _policies.Remove(peerId!);
        }

        public void ClearAll() => _policies.Clear();

        private IPeerItemIdPolicy PolicyOf(string? peerId)
            => !string.IsNullOrEmpty(peerId) && _policies.TryGetValue(peerId!, out var p) ? p : UnknownPeerItemIdPolicy.Instance;

        /// <summary>Whether something this machine owns under <paramref name="itemId"/> can be shared over the link
        /// to <paramref name="toPeerId"/> at all.</summary>
        public bool CanLeaveOver(string? toPeerId, int itemId) => Outbound(toPeerId).TryMap(itemId, out _);

        /// <summary>An id of this machine on its way to <paramref name="toPeerId"/>.</summary>
        public ItemIdRoute Outbound(string? toPeerId) => new ItemIdRoute(null, PolicyOf(toPeerId));

        /// <summary>An id <paramref name="fromPeerId"/> sent, on its way into this machine.</summary>
        public ItemIdRoute Inbound(string? fromPeerId) => new ItemIdRoute(PolicyOf(fromPeerId), null);

        /// <summary>
        /// An id <paramref name="fromPeerId"/> sent, forwarded by this machine to <paramref name="toPeerId"/>. The
        /// forwarding machine's own numbering is only a stop on the way here; a policy that translates between two
        /// peers directly replaces this one method.
        /// </summary>
        public ItemIdRoute Relay(string? fromPeerId, string? toPeerId) => new ItemIdRoute(PolicyOf(fromPeerId), PolicyOf(toPeerId));
    }
}
