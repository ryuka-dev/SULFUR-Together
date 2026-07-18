using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Steamworks;

namespace SULFURTogether.Networking
{
    /// <summary>
    /// STEAM-2: the byte pump between LiteNetLib's UDP traffic (always loopback here) and Steam P2P
    /// (<see cref="SteamNetworkingSupport"/>). <c>NetService</c>, <c>NetMessage</c>, every gameplay codec and
    /// <c>NetHandshake</c> never know this exists — to them a Steam-joined peer is an ordinary LiteNetLib remote
    /// endpoint. This class is the only thing that knows the "wire" underneath happens to be Steam.
    ///
    /// Host side: one small loopback <see cref="UdpClient"/> per connected Steam peer, "connected" (UDP sense)
    /// to <c>127.0.0.1:&lt;game port&gt;</c> — from the host's real <c>NetManager</c>'s perspective each is just
    /// another remote endpoint (a distinct source port on loopback), exactly like a real LAN client.
    /// Client side: one loopback socket the local LiteNetLib client <c>Connect()</c>s to instead of a real IP;
    /// bytes it sends get learned (source endpoint) and forwarded to the host's <see cref="CSteamID"/> over Steam.
    ///
    /// See <c>Docs/NetworkingArchitecture.md</c> "Transport: connection methods" for the full design writeup.
    /// </summary>
    internal static class SteamRelayBridge
    {
        // Generous per-tick receive budget — LiteNetLib's own send cadence is the real throughput cap; this just
        // needs to drain the Steam channel faster than traffic can pile up.
        private const int ReceivePumpBudgetPerTick = 64;

        private sealed class HostPeerRelay
        {
            public CSteamID SteamId;
            public UdpClient Socket;
        }

        // ---- Host: per-Steam-peer loopback relay ----
        private static readonly Dictionary<ulong, HostPeerRelay> _hostPeers = new Dictionary<ulong, HostPeerRelay>();
        private static bool _hostingActive;
        private static int _hostGamePort;

        // ---- Client: single loopback relay to the host ----
        private static UdpClient _clientSocket;
        private static IPEndPoint _clientLiteNetLibEndpoint; // learned once the local LiteNetLib client sends its first datagram
        private static CSteamID _clientHostId;
        private static bool _clientActive;

        public static bool IsHostingActive => _hostingActive;
        public static bool IsClientActive => _clientActive;

        private static bool _initialized;

        /// <summary>Registers the inbound-session handler unconditionally, once, at startup (call from
        /// <c>Plugin.Awake</c> alongside <c>SteamNetworkingSupport.IsAvailable</c>/<c>SteamRichPresenceJoin.Initialize</c>).
        /// A joining friend's first Steam send can arrive before we ever click "Invite Friends"; without a
        /// permanent subscriber that request goes unanswered and Steam only gives up ~30s later ("App did not
        /// respond"). <see cref="OnHostSessionRequested"/> itself still only accepts while <see cref="_hostingActive"/>.</summary>
        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            SteamNetworkingSupport.SessionRequested += OnHostSessionRequested;
            // A peer's session ending (negotiated failure, or a previously-bridged one dying) must drop its
            // _hostPeers entry — otherwise a retry from that same SteamID hits the "already bridged" short-circuit
            // below forever and is never re-accepted, even though the old bridge is long dead.
            SteamNetworkingSupport.SessionFailed += OnHostPeerSessionFailed;
        }

        // ================= Host =================

        /// <summary>Start accepting Steam P2P joins for the currently-running host, bridging each into the local
        /// LiteNetLib host socket already bound at <c>127.0.0.1:gamePort</c>. Additive to Direct-IP hosting —
        /// never replaces it.</summary>
        public static bool StartHosting(int gamePort)
        {
            if (_hostingActive) return true;
            if (!SteamNetworkingSupport.IsAvailable) return false;
            _hostGamePort = gamePort;
            _hostingActive = true;
            Plugin.Log?.Info($"[SteamRelay] hosting enabled — Steam peers bridge to loopback game port {gamePort}.");
            return true;
        }

        public static void StopHosting()
        {
            if (!_hostingActive) return;
            foreach (var relay in _hostPeers.Values)
            {
                try { relay.Socket.Close(); } catch { /* already gone */ }
                SteamNetworkingSupport.CloseSession(relay.SteamId);
            }
            _hostPeers.Clear();
            _hostingActive = false;
            Plugin.Log?.Info("[SteamRelay] hosting disabled.");
        }

        private static void OnHostPeerSessionFailed(CSteamID remote)
        {
            if (!_hostPeers.TryGetValue(remote.m_SteamID, out var relay)) return;
            try { relay.Socket.Close(); } catch { /* already gone */ }
            _hostPeers.Remove(remote.m_SteamID);
            Plugin.Log?.Info($"[SteamRelay] dropped stale bridge for {remote.m_SteamID} after session failure — a retry will be re-accepted.");
        }

        private static void OnHostSessionRequested(CSteamID remote)
        {
            if (!_hostingActive)
            {
                Plugin.Log?.Info($"[SteamRelay] ignored session request from {remote.m_SteamID} — Steam hosting not enabled.");
                // Close instead of leaving the request pending: the requester's reliable send otherwise sits in
                // "connecting" until Steam's ~20s timeout ("App did not respond") — an explicit close fails their
                // session fast so their UI can say "connection failed" immediately.
                SteamNetworkingSupport.CloseSession(remote);
                return;
            }
            if (_hostPeers.ContainsKey(remote.m_SteamID)) return; // already bridged

            if (!SteamNetworkingSupport.AcceptSession(remote))
            {
                Plugin.Log?.Warn($"[SteamRelay] failed to accept session from {remote.m_SteamID}");
                return;
            }

            try
            {
                var socket = new UdpClient(0, AddressFamily.InterNetwork);
                socket.Connect(IPAddress.Loopback, _hostGamePort);
                _hostPeers[remote.m_SteamID] = new HostPeerRelay { SteamId = remote, Socket = socket };
                int relayPort = ((IPEndPoint)socket.Client.LocalEndPoint).Port;
                Plugin.Log?.Info($"[SteamRelay] accepted Steam peer {remote.m_SteamID} -> loopback relay port {relayPort}");
            }
            catch (Exception ex)
            {
                Plugin.Log?.Warn($"[SteamRelay] failed to open relay socket for {remote.m_SteamID}: {ex.Message}");
            }
        }

        // ================= Client =================

        /// <summary>Start joining <paramref name="hostId"/> over Steam. On success, <paramref name="localPort"/>
        /// is the loopback port the local LiteNetLib client should <c>Connect()</c> to instead of a real IP.</summary>
        public static bool StartJoining(CSteamID hostId, out int localPort, out string error)
        {
            localPort = 0;
            error = null;
            if (!SteamNetworkingSupport.IsAvailable) { error = "Steam is not available."; return false; }
            if (!hostId.IsValid()) { error = "That doesn't look like a valid Steam ID."; return false; }

            StopJoining(); // a re-join cleanly replaces any previous attempt

            try
            {
                _clientSocket = new UdpClient(0, AddressFamily.InterNetwork);
                localPort = ((IPEndPoint)_clientSocket.Client.LocalEndPoint).Port;
                _clientHostId = hostId;
                _clientLiteNetLibEndpoint = null;
                _clientActive = true;
                Plugin.Log?.Info($"[SteamRelay] joining {hostId.m_SteamID} — local loopback relay port {localPort}.");
                return true;
            }
            catch (Exception ex)
            {
                error = $"Could not open a local relay socket ({ex.GetType().Name}).";
                return false;
            }
        }

        public static void StopJoining()
        {
            if (!_clientActive) return;
            try { _clientSocket?.Close(); } catch { /* already gone */ }
            _clientSocket = null;
            _clientLiteNetLibEndpoint = null;
            if (_clientHostId.IsValid()) SteamNetworkingSupport.CloseSession(_clientHostId);
            _clientHostId = CSteamID.Nil;
            _clientActive = false;
        }

        // ================= Pump (called every frame from CoopConnection.Tick) =================

        public static void Tick()
        {
            if (_hostingActive) PumpHost();
            if (_clientActive) PumpClient();
        }

        private static void PumpHost()
        {
            // Steam -> loopback (route each message to its sender's relay socket).
            SteamNetworkingSupport.PumpReceive(ReceivePumpBudgetPerTick, (sender, bytes) =>
            {
                if (!_hostPeers.TryGetValue(sender.m_SteamID, out var relay)) return; // not accepted/mapped — drop
                try { relay.Socket.Send(bytes, bytes.Length); }
                catch (Exception ex) { Plugin.Log?.Info($"[SteamRelay] host->loopback send failed for {sender.m_SteamID}: {ex.Message}"); }
            });

            // loopback -> Steam (drain every peer's relay socket).
            foreach (var relay in _hostPeers.Values)
            {
                while (relay.Socket.Available > 0)
                {
                    IPEndPoint from = null;
                    byte[] data;
                    try { data = relay.Socket.Receive(ref from); }
                    catch (Exception ex)
                    {
                        Plugin.Log?.Info($"[SteamRelay] loopback receive failed for {relay.SteamId.m_SteamID}: {ex.Message}");
                        break;
                    }
                    SteamNetworkingSupport.SendRaw(relay.SteamId, data, data.Length);
                }
            }
        }

        private static void PumpClient()
        {
            // Steam -> loopback (only from the host we're joining).
            SteamNetworkingSupport.PumpReceive(ReceivePumpBudgetPerTick, (sender, bytes) =>
            {
                if (sender.m_SteamID != _clientHostId.m_SteamID) return;
                if (_clientLiteNetLibEndpoint == null) return; // haven't heard from the local LiteNetLib client yet
                try { _clientSocket.Send(bytes, bytes.Length, _clientLiteNetLibEndpoint); }
                catch (Exception ex) { Plugin.Log?.Info($"[SteamRelay] client loopback send failed: {ex.Message}"); }
            });

            // loopback -> Steam (learn the local LiteNetLib client's ephemeral source port from its first datagram).
            while (_clientSocket != null && _clientSocket.Available > 0)
            {
                IPEndPoint from = null;
                byte[] data;
                try { data = _clientSocket.Receive(ref from); }
                catch (Exception ex) { Plugin.Log?.Info($"[SteamRelay] client loopback receive failed: {ex.Message}"); break; }
                _clientLiteNetLibEndpoint = from;
                SteamNetworkingSupport.SendRaw(_clientHostId, data, data.Length);
            }
        }
    }
}
