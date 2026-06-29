namespace SULFURTogether.Networking
{
    /// <summary>
    /// Phase 5.6-LK: the explicit "联机状态 / Online-Linked state" — the single source of truth for whether the
    /// mod's multiplayer behavior is active for each role.
    ///
    /// CLIENT (<see cref="ClientLinked"/>, default OFF): while linked the client joins/follows the host AND
    /// forwards ALL of its own (non-host) map-switch intents to the host so the host leads everyone there. While
    /// UNLINKED the client plays its own run fully independently (no auto-follow, no relay, its own loads are
    /// never intercepted) — this is what lets a player finish a half-done solo run before joining. Toggled by the
    /// user: PageDown (ManualClientSceneFollowKey) links + follows, PageUp (ClientUnlinkKey) unlinks.
    ///
    /// HOST (<see cref="HostLinked"/>, default ON): the master switch for the host's multiplayer broadcasting /
    /// client-relay leading. When off the host behaves single-player. Convenient for later ping/connect UI and a
    /// global mod on/off. Toggled by HostLinkToggleKey.
    /// </summary>
    internal static class NetLinkState
    {
        private static bool _clientLinked;
        private static bool _hostLinked = true;

        public static bool ClientLinked => _clientLinked;
        public static bool HostLinked   => _hostLinked;

        /// <summary>Apply configured defaults at networking start (client OFF, host ON by default).</summary>
        public static void InitFromConfig()
        {
            try { _clientLinked = Plugin.Cfg.ClientLinkedByDefault.Value; } catch { _clientLinked = false; }
            try { _hostLinked   = Plugin.Cfg.HostLinkedByDefault.Value;   } catch { _hostLinked = true; }
            Plugin.Log.Info($"[LinkState] init clientLinked={_clientLinked} hostLinked={_hostLinked}");
        }

        public static void SetClientLinked(bool on, string reason)
        {
            if (_clientLinked == on)
            {
                Plugin.Log.Info($"[LinkState] client already {(on ? "LINKED" : "UNLINKED")} (reason={reason})");
                return;
            }
            _clientLinked = on;
            Plugin.Log.Info($"[LinkState] client {(on ? "LINKED (joining/following host)" : "UNLINKED (independent local run)")} reason={reason}");
            UI.CoopToasts.Notify(on ? "Linked to host" : "Playing solo");
            if (!on)
            {
                // Leaving the session clears joined-state so boss authority / auto-follow stop immediately, and
                // resets the load gate so any in-progress wait is abandoned (the local run continues untouched).
                NetClientJoinFlow.LeaveSession("client unlinked");
                NetClientLoadGate.Reset();
            }
        }

        public static void SetHostLinked(bool on, string reason)
        {
            if (_hostLinked == on)
            {
                Plugin.Log.Info($"[LinkState] host already {(on ? "ON" : "OFF")} (reason={reason})");
                return;
            }
            _hostLinked = on;
            Plugin.Log.Info($"[LinkState] host multiplayer {(on ? "ON (broadcasting + leading client relays)" : "OFF (single-player; ignoring relays)")} reason={reason}");
            UI.CoopToasts.Notify(on ? "Hosting ON" : "Hosting OFF");
        }

        public static void ToggleHost(string reason) => SetHostLinked(!_hostLinked, reason);

        /// <summary>Networking stopped / client disconnected — drop the client back to the safe (unlinked) default.</summary>
        public static void ResetClient(string reason) => SetClientLinked(false, reason);

        /// <summary>
        /// Phase 5.6-LK: returning to the main menu / loading a save resets the client's 联机状态 to its configured
        /// default (normally OFF). Without this, the link would persist across a menu round-trip and a freshly
        /// loaded save would be yanked to the host before the player chooses to link.
        /// </summary>
        public static void ResetClientToDefault(string reason)
        {
            bool def;
            try { def = Plugin.Cfg.ClientLinkedByDefault.Value; } catch { def = false; }
            if (_clientLinked == def) return;
            Plugin.Log.Info($"[LinkState] client 联机状态 reset to default ({(def ? "linked" : "unlinked")}) reason={reason}");
            SetClientLinked(def, reason);
        }

        public static string FormatStatus()
            => $"LinkState client={(_clientLinked ? "linked" : "off")} host={(_hostLinked ? "on" : "off")}";
    }
}
