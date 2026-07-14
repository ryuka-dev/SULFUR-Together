using System;

namespace SULFURTogether.Networking
{
    /// <summary>FF-1 wire DTO for <see cref="NetMessageType.SessionSettings"/> — the host-authoritative session
    /// settings snapshot (currently just the friendly-fire flag) plus a monotonic revision so clients ignore
    /// stale re-sends.</summary>
    public sealed class NetSessionSettingsState
    {
        public int  Revision;
        public bool FriendlyFire;
        public bool DeveloperMode;   // DEV-1: host-authoritative session developer mode (vote/entitlement gated, transient)
    }

    /// <summary>DEV-1: which session-setting fields changed in a live (non-initial) apply, so the caller toasts
    /// exactly the ones that moved (each with its own message).</summary>
    public struct SessionSettingsChange
    {
        public bool FriendlyFire;
        public bool DeveloperMode;
        public bool Any => FriendlyFire || DeveloperMode;
    }

    /// <summary>
    /// FF-1: the client-side mirror + single read point for host-authoritative session settings.
    ///
    /// Ownership: the HOST's <c>Plugin.Cfg.FriendlyFire</c> (coop.json) is the sole authority — the host reads it
    /// live so the connect-page toggle takes effect immediately. A CLIENT only ever sees the value the host
    /// broadcast (msg 70, on toggle change + once per handshake); it defaults to OFF until told otherwise, so a
    /// client that never receives the message behaves exactly like today (no friendly fire, no hit proxies).
    /// </summary>
    public static class NetSessionSettings
    {
        private static bool _receivedFriendlyFire;
        private static bool _receivedDeveloperMode;
        private static int  _lastAppliedRevision = -1;

        /// <summary>The effective friendly-fire setting for the local end, whatever its role.</summary>
        public static bool FriendlyFireEnabled
        {
            get
            {
                switch (NetConfig.GetMode())
                {
                    case NetMode.Host:   return ReadHostConfig();
                    case NetMode.Client: return _receivedFriendlyFire;
                    default:             return false;
                }
            }
        }

        /// <summary>DEV-1: the effective session developer-mode flag for the local end. The host's truth is the
        /// <see cref="CoopDevAuthority"/> decision (vote/entitlement), not a saved config value; a client mirrors
        /// what the host broadcast and defaults OFF until told otherwise.</summary>
        public static bool DeveloperModeEnabled
        {
            get
            {
                switch (NetConfig.GetMode())
                {
                    case NetMode.Host:   return CoopDevAuthority.HostSessionDevEnabled;
                    case NetMode.Client: return _receivedDeveloperMode;
                    default:             return false;
                }
            }
        }

        private static bool ReadHostConfig()
        {
            try { return Plugin.Cfg.FriendlyFire.Value; }
            catch { return false; }
        }

        /// <summary>Client: apply a received host snapshot. Stale revisions (reordered re-sends) are dropped.
        /// Returns the set of fields that changed in a live (non-initial) apply — a value that differs from the
        /// previous snapshot AND is not the initial join-time sync — so the caller can notify the player
        /// (SS-Toast) per field without toasting on join.</summary>
        public static SessionSettingsChange ApplyReceived(NetSessionSettingsState state)
        {
            var change = new SessionSettingsChange();
            if (state == null) return change;
            if (state.Revision <= _lastAppliedRevision) return change;
            bool initial = _lastAppliedRevision < 0;
            _lastAppliedRevision = state.Revision;

            bool ffChanged  = _receivedFriendlyFire  != state.FriendlyFire;
            bool devChanged = _receivedDeveloperMode != state.DeveloperMode;
            _receivedFriendlyFire  = state.FriendlyFire;
            _receivedDeveloperMode = state.DeveloperMode;

            if ((ffChanged || devChanged) && Plugin.Cfg.LogFriendlyFire.Value)
                Plugin.Log.Info($"[SessionSettings] applied: friendlyFire={state.FriendlyFire} developerMode={state.DeveloperMode} rev={state.Revision}");

            change.FriendlyFire  = ffChanged  && !initial;
            change.DeveloperMode = devChanged && !initial;
            return change;
        }

        /// <summary>Reset on session teardown so a later session starts from the safe default (OFF).</summary>
        public static void ResetSession()
        {
            _receivedFriendlyFire  = false;
            _receivedDeveloperMode = false;
            _lastAppliedRevision = -1;
        }
    }
}
