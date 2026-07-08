using System;

namespace SULFURTogether.UI
{
    /// <summary>
    /// In-game co-op notifications (a player joins/leaves, link state changes) shown as brief toasts
    /// through SULFUR Native UI Lib's toast surface.
    ///
    /// The lib is a soft dependency resolved by reflection at startup (see <c>Plugin.WireCoopUi</c>):
    /// when it is present the toast is shown; when it is absent the event is written to the log only,
    /// so the mod never hard-links the assembly and degrades gracefully.
    ///
    /// Toasts are passive — fire-and-forget, no game pause, no input capture (consistent with the
    /// no-pause multiplayer model).
    /// </summary>
    internal static class CoopToasts
    {
        // (title, message) → SulfurToastApi.Show(title, message). Null until wired / when the lib is absent.
        private static Action<string, string> _show;

        /// <summary>Assign the resolved toast seam. Null is allowed (lib absent → log-only).</summary>
        public static void Wire(Action<string, string> showToast) => _show = showToast;

        public static void Notify(string message) => Notify(null, message);

        /// <summary>
        /// SS-Toast: session-setting change toast, same text on the host and every client so all players
        /// see the host's change the moment it lands. English placeholder; see Docs/Localization.md.
        ///
        /// Deliberately <b>bypasses the local <c>EnableCoopToasts</c> preference</b>: that toggle is the
        /// personal "show player join/leave notifications" switch, but a host changing a session setting
        /// (e.g. friendly fire) is a gameplay-affecting, host-authoritative announcement every player must
        /// see regardless of their join/leave preference — the CoopUiPlan §5 change-notification rule
        /// ("every player is told"). Only the join/leave/link/connect toasts stay gated by the preference.
        /// </summary>
        public static void NotifySessionSetting(string settingLabel, bool enabled)
            => Notify(null, CoopLoc.Format("session.settingChanged", "{label}: {state}",
                ("label", settingLabel),
                ("state", enabled ? CoopLoc.Get("common.on", "On") : CoopLoc.Get("common.off", "Off"))),
                respectPreference: false);

        /// <summary>
        /// Show a co-op toast (and always log the event). No-op when <c>EnableCoopToasts</c> is off.
        /// Safe to call when the UI Lib is absent — the event is logged only.
        /// </summary>
        public static void Notify(string title, string message) => Notify(title, message, respectPreference: true);

        /// <summary>
        /// Core toast path. <paramref name="respectPreference"/> false forces the toast through even when the
        /// local <c>EnableCoopToasts</c> join/leave preference is off (session-setting announcements — see
        /// <see cref="NotifySessionSetting"/>).
        /// </summary>
        private static void Notify(string title, string message, bool respectPreference)
        {
            if (string.IsNullOrEmpty(message)) return;

            if (respectPreference)
            {
                bool enabled = true;
                try { enabled = Plugin.Cfg.EnableCoopToasts.Value; } catch { /* config not ready yet */ }
                if (!enabled) return;
            }

            string heading = string.IsNullOrEmpty(title) ? CoopLoc.Get("toast.title.default", "Together") : title;

            // Always log so the event is observable even without the UI Lib installed.
            Plugin.Log?.Info($"[CoopToast] {heading}: {message}");

            try { _show?.Invoke(heading, message); }
            catch (Exception e) { Plugin.Log?.Warn($"[CoopToast] toast show failed: {e.Message}"); }
        }
    }
}
