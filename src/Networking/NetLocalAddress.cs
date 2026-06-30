using System;
using System.Net;
using System.Net.Sockets;

namespace SULFURTogether.Networking
{
    /// <summary>
    /// UI-3d: resolves this machine's LAN IPv4 so the connect page can show a host the address other players on the
    /// same network should type into "Host address (IP)". (The host binds to all interfaces; it has no single
    /// address of its own to report, so we derive the one a peer would route to.)
    ///
    /// Primary method is the standard UDP-connect trick: connecting a datagram socket to an arbitrary external
    /// address sends nothing but makes the OS pick the outbound interface, whose local endpoint is the LAN IP peers
    /// reach us on — correct even with multiple adapters (VPN/VMware/etc.) where enumerating all addresses is
    /// ambiguous. Falls back to the first non-loopback IPv4 host address. Cached (the machine's LAN IP is stable for
    /// a session); <see cref="Invalidate"/> forces a re-resolve.
    /// </summary>
    internal static class NetLocalAddress
    {
        private static string _cached;
        private static bool   _tried;

        public static bool TryGetLanIPv4(out string ip)
        {
            if (!_tried)
            {
                _tried  = true;
                _cached = Resolve();
            }
            ip = _cached;
            return !string.IsNullOrEmpty(_cached);
        }

        public static void Invalidate()
        {
            _tried  = false;
            _cached = null;
        }

        private static string Resolve()
        {
            try
            {
                using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    s.Connect("8.8.8.8", 65530); // UDP connect transmits nothing; just fixes the local endpoint
                    if (s.LocalEndPoint is IPEndPoint ep && !IPAddress.IsLoopback(ep.Address))
                        return ep.Address.ToString();
                }
            }
            catch { /* no route / offline — fall through to the host-entry scan */ }

            try
            {
                foreach (var addr in Dns.GetHostAddresses(Dns.GetHostName()))
                    if (addr.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(addr))
                        return addr.ToString();
            }
            catch { /* give up — the page just won't show the address */ }

            return null;
        }
    }
}
