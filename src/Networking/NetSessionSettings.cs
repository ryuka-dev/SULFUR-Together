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

        private static bool ReadHostConfig()
        {
            try { return Plugin.Cfg.FriendlyFire.Value; }
            catch { return false; }
        }

        /// <summary>Client: apply a received host snapshot. Stale revisions (reordered re-sends) are dropped.</summary>
        public static void ApplyReceived(NetSessionSettingsState state)
        {
            if (state == null) return;
            if (state.Revision <= _lastAppliedRevision) return;
            _lastAppliedRevision = state.Revision;
            bool changed = _receivedFriendlyFire != state.FriendlyFire;
            _receivedFriendlyFire = state.FriendlyFire;
            if (changed && Plugin.Cfg.LogFriendlyFire.Value)
                Plugin.Log.Info($"[FF] session settings applied: friendlyFire={state.FriendlyFire} rev={state.Revision}");
        }

        /// <summary>Reset on session teardown so a later session starts from the safe default (OFF).</summary>
        public static void ResetSession()
        {
            _receivedFriendlyFire = false;
            _lastAppliedRevision = -1;
        }
    }
}
