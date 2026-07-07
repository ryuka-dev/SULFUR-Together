using System;
using Steamworks;

namespace SULFURTogether.Networking
{
    /// <summary>
    /// STEAM-3: the "Invite Friends" convenience layer on top of the already-working STEAM-2 manual-paste join.
    /// Uses Steam Rich Presence's special <c>"connect"</c> key — no Lobby needed: setting it makes a "Join Game"
    /// entry appear next to our name in a friend's Steam friends list, and <c>ActivateGameOverlayInviteDialogConnectString</c>
    /// pops the overlay's invite picker with the same string baked in. Either path, on the invitee's side Steam
    /// fires <see cref="GameRichPresenceJoinRequested_t"/> with our connect string back — which is just the
    /// host's SteamID64, the exact same "address" STEAM-2's manual field takes. No lobby object, no lobby
    /// lifecycle, one Steam API surface instead of two.
    ///
    /// We only ever touch our own <c>"connect"</c> rich-presence key (set "" to clear it) — never
    /// <c>ClearRichPresence()</c>, which would also wipe whatever status text the base game's own
    /// <c>SteamworksManager</c>/<c>SetSteamDisplay*</c> calls have set.
    /// </summary>
    internal static class SteamRichPresenceJoin
    {
        private const string ConnectKey = "connect";

        private static Callback<GameRichPresenceJoinRequested_t> _joinRequestedCallback;
        private static bool _callbackRegistered;

        /// <summary>Fired when a friend accepts a Steam invite / clicks "Join Game" — carries the host's
        /// CSteamID (parsed from our own connect string) and the inviting friend's persona name.</summary>
        public static event Action<CSteamID, string> JoinRequested;

        /// <summary>Latched by <see cref="OnJoinRequested"/> so the connect page can show "Join &lt;friend&gt;'s
        /// game" even if it wasn't open when the invite was accepted (Steam can launch/foreground the game at
        /// any time). Consumed via <see cref="ConsumePendingInvite"/>.</summary>
        public static CSteamID? PendingInviteHostId { get; private set; }
        public static string PendingInviteFriendName { get; private set; }

        public static void ConsumePendingInvite()
        {
            PendingInviteHostId = null;
            PendingInviteFriendName = null;
        }

        /// <summary>Registers the inbound-invite callback so a friend's invite is caught even if we never host
        /// ourselves this session (an invitee never calls <see cref="AdvertiseHosting"/>). Call once at startup
        /// (<c>Plugin.Awake</c>) alongside <c>SteamNetworkingSupport.IsAvailable</c> — cheap no-op if Steam isn't up.</summary>
        public static void Initialize() => EnsureCallbackRegistered();

        /// <summary>Advertise this host as joinable via Steam's "connect" rich-presence key, and pop the overlay
        /// invite-friends dialog. Call once Steam hosting is enabled (see <c>CoopConnection.EnableSteamHosting</c>).</summary>
        public static void AdvertiseHosting(CSteamID localId)
        {
            if (!SteamNetworkingSupport.IsAvailable) return;
            EnsureCallbackRegistered();
            try
            {
                string idText = localId.m_SteamID.ToString();
                SteamFriends.SetRichPresence(ConnectKey, idText);
                SteamFriends.ActivateGameOverlayInviteDialogConnectString(idText);
                Plugin.Log?.Info("[SteamRichPresence] hosting advertised — Invite Friends dialog opened.");
            }
            catch (Exception ex)
            {
                Plugin.Log?.Warn($"[SteamRichPresence] AdvertiseHosting failed: {ex.Message}");
            }
        }

        /// <summary>Stop advertising this host as Steam-joinable (clears only our own key).</summary>
        public static void StopAdvertisingHosting()
        {
            if (!SteamNetworkingSupport.IsAvailable) return;
            try { SteamFriends.SetRichPresence(ConnectKey, ""); }
            catch (Exception ex) { Plugin.Log?.Info($"[SteamRichPresence] StopAdvertisingHosting failed: {ex.Message}"); }
        }

        private static void EnsureCallbackRegistered()
        {
            if (_callbackRegistered) return;
            if (!SteamNetworkingSupport.IsAvailable) return;
            _callbackRegistered = true;
            try
            {
                // Kept alive for the process lifetime — same Callback<T> GC/finalizer caveat as SteamNetworkingSupport.
                _joinRequestedCallback = Callback<GameRichPresenceJoinRequested_t>.Create(OnJoinRequested);
            }
            catch (Exception ex)
            {
                _callbackRegistered = false;
                Plugin.Log?.Warn($"[SteamRichPresence] callback registration failed: {ex.Message}");
            }
        }

        private static void OnJoinRequested(GameRichPresenceJoinRequested_t evt)
        {
            string connect = evt.m_rgchConnect;
            if (string.IsNullOrWhiteSpace(connect) || !ulong.TryParse(connect, out ulong steamId64))
            {
                Plugin.Log?.Info($"[SteamRichPresence] ignored join request with unparseable connect string '{connect}'.");
                return;
            }
            var hostId = new CSteamID(steamId64);
            string friendName = null;
            try { friendName = SteamFriends.GetFriendPersonaName(evt.m_steamIDFriend); } catch { /* cosmetic only */ }
            Plugin.Log?.Info($"[SteamRichPresence] join requested — host={steamId64}, friend={friendName ?? "?"}");
            PendingInviteHostId = hostId;
            PendingInviteFriendName = friendName;
            try { JoinRequested?.Invoke(hostId, friendName); }
            catch (Exception ex) { Plugin.Log?.Warn($"[SteamRichPresence] JoinRequested handler threw: {ex.Message}"); }
        }
    }
}
