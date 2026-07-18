using System;
using System.Runtime.InteropServices;
using HarmonyLib;
using Steamworks;

namespace SULFURTogether.Networking
{
    /// <summary>
    /// STEAM-1: thin, always-on wrapper around the game's already-initialized Steam session
    /// (<c>Steamworks.SteamNetworkingMessages</c>) for the co-op relay bridge (STEAM-2). Unlike
    /// <see cref="SteamIdentity"/> (pure reflection, one-liner), this compiles against the game's own
    /// <c>com.rlabrecque.steamworks.net.dll</c> directly (see the csproj <c>Reference</c>,
    /// <c>Private=false</c> — nothing is bundled, we only compile against the copy the game already ships and
    /// has already called <c>SteamAPI_Init</c> for, under SULFUR's real Steam AppID). The one soft-dependency
    /// check left is <c>SteamManager.Initialized</c> (a game-side MonoBehaviour, no compile-time type available
    /// for it), read the same way <see cref="SteamIdentity"/> already does.
    ///
    /// No behaviour change from this file alone: it only exposes availability + local identity + raw
    /// send/receive/accept wrappers. Nothing calls these yet (STEAM-2 wires the actual relay bridge).
    /// </summary>
    internal static class SteamNetworkingSupport
    {
        /// <summary>Steam networking-messages channel reserved for the co-op relay bridge's raw UDP-over-Steam
        /// byte pump (STEAM-2). A fixed, private channel — nothing else in the mod uses SteamNetworkingMessages.</summary>
        public const int RelayChannel = 0;

        private static bool _resolved;
        private static bool _available;
        private static Callback<SteamNetworkingMessagesSessionRequest_t> _sessionRequestCallback;
        private static Callback<SteamNetworkingMessagesSessionFailed_t> _sessionFailedCallback;

        /// <summary>Fired when a remote Steam user opens a P2P session with us (they tried to send us a
        /// message). STEAM-2's host-side relay subscribes to auto-accept + spin up a per-peer loopback relay.</summary>
        public static event Action<CSteamID> SessionRequested;

        /// <summary>Fired when a P2P session with a peer ends (negotiation timeout, or a previously-working
        /// session dies). The relay bridge subscribes so a peer's stale bridge entry is dropped — otherwise a
        /// retry from the same peer would find "already bridged" for a session that no longer exists and never
        /// re-<c>AcceptSessionWithUser</c>.</summary>
        public static event Action<CSteamID> SessionFailed;

        /// <summary>True once Steam is confirmed up and our session-request callback is registered. Resolved
        /// once and cached — matches <see cref="SteamIdentity"/>'s soft-dependency pattern (Steam not running /
        /// SteamManager missing / any failure ⇒ false, Steam connect method stays disabled).</summary>
        public static bool IsAvailable
        {
            get { EnsureResolved(); return _available; }
        }

        /// <summary>The local Steam user's id, e.g. to display "Your Steam ID" on the connect page (STEAM-4).</summary>
        public static bool TryGetLocalSteamId(out CSteamID id)
        {
            if (!IsAvailable) { id = CSteamID.Nil; return false; }
            try
            {
                id = SteamUser.GetSteamID();
                return id.IsValid();
            }
            catch (Exception ex)
            {
                Plugin.Log?.Info($"[SteamNet] GetSteamID failed: {ex.Message}");
                id = CSteamID.Nil;
                return false;
            }
        }

        /// <summary>Accept an inbound P2P session from <paramref name="remote"/> (call in response to
        /// <see cref="SessionRequested"/> once the host decides to allow it, e.g. it's currently hosting).</summary>
        public static bool AcceptSession(CSteamID remote)
        {
            if (!IsAvailable) return false;
            try
            {
                var identity = new SteamNetworkingIdentity();
                identity.SetSteamID(remote);
                return SteamNetworkingMessages.AcceptSessionWithUser(ref identity);
            }
            catch (Exception ex)
            {
                Plugin.Log?.Warn($"[SteamNet] AcceptSession({remote.m_SteamID}) failed: {ex.Message}");
                return false;
            }
        }

        public static void CloseSession(CSteamID remote)
        {
            if (!IsAvailable) return;
            try
            {
                var identity = new SteamNetworkingIdentity();
                identity.SetSteamID(remote);
                SteamNetworkingMessages.CloseSessionWithUser(ref identity);
            }
            catch (Exception ex)
            {
                Plugin.Log?.Info($"[SteamNet] CloseSession({remote.m_SteamID}) failed: {ex.Message}");
            }
        }

        // Reliable (guarantees the session-negotiation packet isn't silently dropped pre-session) + NoNagle (no
        // send-side batching delay) + AutoRestartBrokenSession (a mid-game Steam P2P hiccup re-negotiates the
        // session transparently on the next send instead of leaving every following send failing with
        // k_EResultConnectFailed until something manually reopens it).
        private const int SendFlags = Constants.k_nSteamNetworkingSend_Reliable
            | Constants.k_nSteamNetworkingSend_NoNagle
            | Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession;

        /// <summary>Send a raw byte payload to <paramref name="remote"/> on <see cref="RelayChannel"/> — see
        /// <see cref="SendFlags"/> for why reliable (not unreliable): Steam's <c>SendMessageToUser</c> only
        /// queues-and-negotiates a P2P session for reliable sends — an unreliable send before a session exists is
        /// simply dropped and never triggers <see cref="SteamNetworkingMessagesSessionRequest_t"/> on the far side
        /// at all. Since every relayed byte here is really a LiteNetLib datagram (which already carries its own
        /// reliability/ordering semantics one layer up), asking Steam to also guarantee delivery underneath
        /// doesn't change LiteNetLib's own behavior — it just stops this transport layer from being the thing
        /// that silently eats packets.</summary>
        public static bool SendRaw(CSteamID remote, byte[] data, int length)
        {
            if (!IsAvailable || data == null || length <= 0) return false;
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.Copy(data, 0, buffer, length);
                var identity = new SteamNetworkingIdentity();
                identity.SetSteamID(remote);
                EResult result = SteamNetworkingMessages.SendMessageToUser(
                    ref identity, buffer, (uint)length, SendFlags, RelayChannel);
                if (result != EResult.k_EResultOK)
                    Plugin.Log?.Warn($"[SteamNet] SendMessageToUser({remote.m_SteamID}, {length}B) returned {result}");
                return result == EResult.k_EResultOK;
            }
            catch (Exception ex)
            {
                Plugin.Log?.Info($"[SteamNet] SendRaw({remote.m_SteamID}, {length}B) failed: {ex.Message}");
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>Drain up to <paramref name="maxMessages"/> pending messages on <see cref="RelayChannel"/>,
        /// invoking <paramref name="onMessage"/>(senderId, bytes) for each. Returns the number processed.</summary>
        public static int PumpReceive(int maxMessages, Action<CSteamID, byte[]> onMessage)
        {
            if (!IsAvailable || onMessage == null || maxMessages <= 0) return 0;
            var pointers = new IntPtr[maxMessages];
            int count;
            try
            {
                count = SteamNetworkingMessages.ReceiveMessagesOnChannel(RelayChannel, pointers, maxMessages);
            }
            catch (Exception ex)
            {
                Plugin.Log?.Info($"[SteamNet] ReceiveMessagesOnChannel failed: {ex.Message}");
                return 0;
            }

            for (int i = 0; i < count; i++)
            {
                try
                {
                    SteamNetworkingMessage_t msg = SteamNetworkingMessage_t.FromIntPtr(pointers[i]);
                    var bytes = new byte[msg.m_cbSize];
                    if (msg.m_cbSize > 0) Marshal.Copy(msg.m_pData, bytes, 0, msg.m_cbSize);
                    CSteamID sender = msg.m_identityPeer.GetSteamID();
                    onMessage(sender, bytes);
                }
                catch (Exception ex)
                {
                    Plugin.Log?.Info($"[SteamNet] failed to process received message {i}: {ex.Message}");
                }
                finally
                {
                    SteamNetworkingMessage_t.Release(pointers[i]);
                }
            }
            return count;
        }

        private static void EnsureResolved()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                var steamManagerType = AccessTools.TypeByName("SteamManager");
                var initializedProp = steamManagerType != null ? AccessTools.Property(steamManagerType, "Initialized") : null;
                _available = initializedProp != null && initializedProp.GetValue(null) is bool ok && ok;
            }
            catch (Exception ex)
            {
                _available = false;
                Plugin.Log?.Info($"[SteamNet] availability check failed: {ex.Message}");
            }

            if (!_available)
            {
                Plugin.Log?.Info("[SteamNet] not available (Steam not running, or SteamManager missing) — Steam connect method disabled.");
                return;
            }

            try
            {
                // Kept alive for the process lifetime (a static field) — Callback<T>'s finalizer unregisters it,
                // so a local/temporary would silently stop delivering callbacks once GC'd.
                _sessionRequestCallback = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSessionRequest);
                // Diagnostic-only: tells us *why* a P2P session never came up (NAT/firewall/relay problem, timeout,
                // etc. — via m_info.m_eEndReason / m_szEndDebug) instead of the two ends just silently never seeing
                // each other, which is otherwise indistinguishable from "callback never fired".
                _sessionFailedCallback = Callback<SteamNetworkingMessagesSessionFailed_t>.Create(OnSessionFailed);
                // Valve-recommended warm-up: pre-fetch the relay network config + ticket NOW (async) so the
                // first real P2P connection doesn't pay that round-trip inside its own session-negotiation
                // window — a cold first join was exceeding LiteNetLib's connect window before the tunnel opened.
                try { SteamNetworkingUtils.InitRelayNetworkAccess(); }
                catch (Exception ex) { Plugin.Log?.Info($"[SteamNet] InitRelayNetworkAccess failed (non-fatal): {ex.Message}"); }
                string idText = TryGetLocalSteamId(out var id) ? id.m_SteamID.ToString() : "?";
                Plugin.Log?.Info($"[SteamNet] available; local SteamID={idText}");
            }
            catch (Exception ex)
            {
                _available = false;
                Plugin.Log?.Warn($"[SteamNet] callback registration failed, disabling Steam connect method: {ex.Message}");
            }
        }

        private static void OnSessionRequest(SteamNetworkingMessagesSessionRequest_t evt)
        {
            CSteamID remote = evt.m_identityRemote.GetSteamID();
            Plugin.Log?.Info($"[SteamNet] inbound P2P session request from {remote.m_SteamID}");
            try { SessionRequested?.Invoke(remote); }
            catch (Exception ex) { Plugin.Log?.Warn($"[SteamNet] SessionRequested handler threw: {ex.Message}"); }
        }

        private static void OnSessionFailed(SteamNetworkingMessagesSessionFailed_t evt)
        {
            SteamNetConnectionInfo_t info = evt.m_info;
            CSteamID remote = info.m_identityRemote.GetSteamID();
            Plugin.Log?.Warn($"[SteamNet] P2P session FAILED with {remote.m_SteamID} — reason={info.m_eEndReason} debug='{info.m_szEndDebug}' state={info.m_eState}");
            try { SessionFailed?.Invoke(remote); }
            catch (Exception ex) { Plugin.Log?.Warn($"[SteamNet] SessionFailed handler threw: {ex.Message}"); }
        }
    }
}
