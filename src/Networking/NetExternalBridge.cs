using System;
using System.Collections.Generic;
using SULFURTogether.Api;

namespace SULFURTogether.Networking
{
    /// <summary>
    /// Internal plumbing behind the public <see cref="NetExternalChannel"/> / <see cref="NetSessionInfo"/>
    /// facades. Owns the channel-id -> handler registry (which is independent of the <see cref="NetService"/>
    /// lifecycle, so a companion mod registers once at load and the subscription survives host/client restarts),
    /// routes sends through the live service, and dispatches inbound payloads.
    ///
    /// Reads the live service through <see cref="CoopConnection.Service"/> so there is a single owner of that
    /// reference; this bridge is not attached/detached like the gameplay bridges.
    /// </summary>
    internal static class NetExternalBridge
    {
        internal const int MaxChannelIdLength = 128;

        // Kept within a single reliable payload; ST does not fragment external payloads.
        internal const int MaxPayloadBytes = NetExternalChannel.MaxPayloadBytes;

        private static readonly object _gate = new object();
        private static readonly Dictionary<string, Action<string, byte[]>> _handlers =
            new Dictionary<string, Action<string, byte[]>>(StringComparer.Ordinal);

        // ---- registration ----

        internal static IExternalChannelRegistration Register(string channelId, Action<string, byte[]> onReceive)
        {
            if (string.IsNullOrEmpty(channelId)) throw new ArgumentException("channelId must be non-empty", nameof(channelId));
            if (channelId.Length > MaxChannelIdLength) throw new ArgumentException($"channelId exceeds {MaxChannelIdLength} chars", nameof(channelId));
            if (onReceive == null) throw new ArgumentNullException(nameof(onReceive));

            lock (_gate)
            {
                if (_handlers.ContainsKey(channelId))
                    throw new InvalidOperationException($"external channel '{channelId}' is already registered");
                _handlers[channelId] = onReceive;
            }
            NetLogger.Info($"[ExternalChannel] registered '{channelId}'");
            return new Registration(channelId);
        }

        private static void Unregister(string channelId)
        {
            bool removed;
            lock (_gate) removed = _handlers.Remove(channelId);
            if (removed) NetLogger.Info($"[ExternalChannel] unregistered '{channelId}'");
        }

        // ---- send / receive ----

        internal static bool Send(string channelId, byte[] payload, ExternalDelivery delivery, ExternalTarget target, string? targetPeerId)
        {
            if (string.IsNullOrEmpty(channelId) || channelId.Length > MaxChannelIdLength) return false;
            if (payload == null || payload.Length > MaxPayloadBytes) return false;

            var svc = CoopConnection.Service;
            if (svc == null) return false;
            return svc.SendExternalPayload(channelId, payload, delivery, target, targetPeerId);
        }

        /// <summary>Called from <see cref="NetService.OnNetworkReceive"/> on the Unity main thread.</summary>
        internal static void Dispatch(string senderPeerId, string channelId, byte[] payload)
        {
            Action<string, byte[]>? handler;
            lock (_gate) _handlers.TryGetValue(channelId, out handler);

            if (handler == null)
            {
                // A companion mod may be present on one end only; dropping is the correct, quiet outcome.
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[ExternalChannel] no handler for '{channelId}' (payload dropped, {payload?.Length ?? 0} bytes)");
                return;
            }

            try { handler(senderPeerId, payload); }
            catch (Exception ex) { NetLogger.Warn($"[ExternalChannel] handler for '{channelId}' threw: {ex.Message}"); }
        }

        // ---- session snapshot (read-only) ----

        internal static SessionRole Role =>
            CoopConnection.CurrentMode switch
            {
                NetMode.Host   => SessionRole.Host,
                NetMode.Client => SessionRole.Client,
                _              => SessionRole.Offline,
            };

        internal static bool IsSessionActive => CoopConnection.IsRunning;

        internal static string LocalPeerId => CoopConnection.Service?.LocalPeerId ?? "";

        internal static IReadOnlyList<ExternalPeer> Peers
        {
            get
            {
                var svc = CoopConnection.Service;
                if (svc == null) return Array.Empty<ExternalPeer>();

                var list = new List<ExternalPeer>();
                foreach (var s in svc.SessionSnapshot)
                {
                    if (!s.IsConnected) continue;
                    list.Add(new ExternalPeer(s.PeerId, s.Role == NetPeerRole.Host, s.IsLocal));
                }
                return list;
            }
        }

        private sealed class Registration : IExternalChannelRegistration
        {
            private string? _channelId;
            public Registration(string channelId) => _channelId = channelId;

            public void Dispose()
            {
                var id = _channelId;
                _channelId = null;
                if (id != null) Unregister(id);
            }
        }
    }
}
