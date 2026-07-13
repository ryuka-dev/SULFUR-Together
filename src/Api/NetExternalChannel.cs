using System;

namespace SULFURTogether.Api
{
    /// <summary>How an external payload is delivered over the session transport.</summary>
    public enum ExternalDelivery : byte
    {
        ReliableOrdered = 0,
        Unreliable      = 1,
    }

    /// <summary>Who an external payload is addressed to.</summary>
    public enum ExternalTarget : byte
    {
        /// <summary>Client -> the host. Valid only while running as a client.</summary>
        Host         = 0,
        /// <summary>Host -> every connected client. Valid only while running as the host.</summary>
        AllClients   = 1,
        /// <summary>A specific peer by its session peer id (the host addressing a client, or a client addressing "host").</summary>
        SpecificPeer = 2,
    }

    /// <summary>A live external-channel subscription. Dispose to unregister.</summary>
    public interface IExternalChannelRegistration : IDisposable { }

    /// <summary>
    /// Public, mod-neutral opaque message channel over SULFUR Together's session transport. A companion mod
    /// registers a handler for a channel id (a stable string it owns) and sends raw byte payloads that ST never
    /// interprets. The sender identity delivered to the handler is stamped by ST from the authenticated
    /// connection, never trusted from the wire.
    ///
    /// Threading: receive handlers are invoked on the Unity main thread (during the network poll in
    /// <c>Plugin.Update</c>). Send is expected to be called from the main thread.
    ///
    /// This is deliberately transport-neutral: a caller never sees LiteNetLib, Steam, peers, or message ids.
    /// </summary>
    public static class NetExternalChannel
    {
        /// <summary>Bumped on any breaking change to this API or its wire framing. A companion mod may gate on it.</summary>
        public const int ApiVersion = 1;

        /// <summary>Largest single payload, in bytes. Larger payloads are rejected (Send returns false).</summary>
        public const int MaxPayloadBytes = 60000;

        /// <summary>
        /// Register a receiver for <paramref name="channelId"/>. Dispose the returned token to unregister.
        /// <paramref name="onReceive"/> is invoked on the Unity main thread as (senderPeerId, payload).
        /// Throws <see cref="ArgumentException"/> for an empty/oversized id, <see cref="ArgumentNullException"/>
        /// for a null handler, and <see cref="InvalidOperationException"/> if the id is already registered.
        /// </summary>
        public static IExternalChannelRegistration Register(string channelId, Action<string, byte[]> onReceive)
            => SULFURTogether.Networking.NetExternalBridge.Register(channelId, onReceive);

        /// <summary>
        /// Send an opaque payload. Returns false (never throws for these) when there is no live session, the
        /// current role does not match <paramref name="target"/>, the payload exceeds
        /// <see cref="MaxPayloadBytes"/>, or a <see cref="ExternalTarget.SpecificPeer"/> id is unknown. A
        /// transport error is logged and also returns false.
        /// </summary>
        public static bool Send(string channelId, byte[] payload, ExternalDelivery delivery, ExternalTarget target, string? targetPeerId = null)
            => SULFURTogether.Networking.NetExternalBridge.Send(channelId, payload, delivery, target, targetPeerId);
    }
}
