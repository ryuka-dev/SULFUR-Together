using System;
using Steamworks;
using SULFURTogether.Networking.Gameplay;

namespace SULFURTogether.Networking
{
    /// <summary>
    /// Owns the <see cref="NetService"/> lifecycle and lets networking be started, stopped, or switched
    /// (Off/Host/Client) <b>at runtime</b> — not just once at <c>Plugin.Awake</c>. This is the seam the
    /// connect UI drives: the options page applies its rows to config and then calls <see cref="Apply"/>.
    ///
    /// A clean restart is a full <see cref="NetService.Stop"/> + a fresh instance + <see cref="NetService.Start"/>;
    /// both already reset all per-session state, so reusing the process across reconnects is safe. The two
    /// static bridges (<see cref="NetRunStateBridge"/>, <see cref="NetGameplaySyncBridge"/>) are re-attached to
    /// the live service (or to null when off), so gameplay code that reaches the service through them no-ops
    /// while networking is down.
    /// </summary>
    internal static class CoopConnection
    {
        private static NetService _service;

        // Signature of the connection-relevant settings the running service was last started with. Lets a repeat
        // Apply with no actual change be a no-op instead of a socket restart that would kick connected peers.
        private static string _lastSignature;

        // STEAM-2: a one-shot connect-target override consumed by the next Apply(Client, ...) — set by
        // ApplySteamClient right before starting the service, so NetService.ConnectToHost targets the local
        // SteamRelayBridge loopback port instead of the configured Direct-IP HostAddress/HostPort.
        private static (string Address, int Port, string Label)? _pendingSteamJoinTarget;

        /// <summary>True once a running Host has also opened itself to Steam P2P joins (via the connect page's
        /// "Invite Friends" — a deliberate, separate action from Create; Direct-IP hosting works without it).</summary>
        public static bool SteamHostingEnabled { get; private set; }

        /// <summary>The live service, or null when networking is off. Plugin's Unity loop drives it via
        /// <see cref="Tick"/>/<see cref="FixedTick"/>; gameplay code uses the bridges, not this.</summary>
        public static NetService Service => _service;

        public static bool IsRunning => _service != null;

        /// <summary>The mode networking is currently running in (Off when stopped).</summary>
        public static NetMode CurrentMode { get; private set; } = NetMode.Off;

        /// <summary>
        /// Called at <c>Plugin.Awake</c>. Networking <b>no longer auto-starts from config</b>: opening the game
        /// must not silently host or auto-connect a client. The player starts a session explicitly from the
        /// in-game connect page (Create / Join) <i>after</i> loading a save. This just establishes the clean Off
        /// state (null bridges). The config still seeds the connect page's field defaults; <see cref="NetConfig"/>
        /// is no longer consulted to decide whether networking runs.
        /// </summary>
        public static void Initialize() => Apply(NetMode.Off, "startup");

        /// <summary>
        /// Make networking match <paramref name="mode"/>. Tears down any running service first (so this doubles
        /// as restart and as Host↔Client switch), then starts fresh unless <paramref name="mode"/> is Off.
        /// Safe to call repeatedly and at runtime. Failures (e.g. LiteNetLib missing, port in use) leave
        /// networking cleanly Off rather than half-initialized.
        /// </summary>
        public static void Apply(NetMode mode, string reason)
        {
            string signature = BuildSignature(mode);
            if (_service != null && mode == CurrentMode && signature == _lastSignature)
            {
                Plugin.Log.Info($"[CoopConn] already running mode={mode} with unchanged settings — no restart (reason={reason}).");
                return;
            }

            if (_service != null)
            {
                try { _service.Stop(); }
                catch (Exception ex) { Plugin.Log.Warn($"[CoopConn] stop during apply failed: {ex.Message}"); }
                NetRunStateBridge.Attach(null);
                NetGameplaySyncBridge.Attach(null);
                _service = null;
                Plugin.Log.Info($"[CoopConn] stopped previous networking (was {CurrentMode}).");
            }
            // STEAM-2: a torn-down service has nothing left to bridge into — drop both Steam relay roles here,
            // EXCEPT a client-join bridge that ApplySteamClient just opened moments ago for THIS very call (its
            // target is sitting in _pendingSteamJoinTarget, about to be consumed below as the new service starts).
            // Unconditionally stopping it here would close the loopback relay socket before NetService.Start()
            // ever gets a chance to connect through it — the join would fail before Steam was even involved.
            if (SteamRelayBridge.IsHostingActive) SteamRelayBridge.StopHosting();
            bool joiningNow = mode == NetMode.Client && _pendingSteamJoinTarget.HasValue;
            if (SteamRelayBridge.IsClientActive && !joiningNow) SteamRelayBridge.StopJoining();
            if (SteamHostingEnabled) SteamRichPresenceJoin.StopAdvertisingHosting();
            SteamHostingEnabled = false;
            CurrentMode = NetMode.Off;
            _lastSignature = null;

            if (mode == NetMode.Off)
            {
                NetConnectFeedback.Clear();
                Plugin.Log.Info($"[CoopConn] networking Off (reason={reason}).");
                return;
            }

            // A client Join enters the "connecting…" state; the handshake result (accepted / rejected / timed
            // out) resolves it asynchronously in NetService. Hosting has nothing to connect to, so leave it clear.
            if (mode == NetMode.Client) NetConnectFeedback.BeginAttempt();
            else NetConnectFeedback.Clear();

            try
            {
                var service = new NetService();
                NetRunStateBridge.Attach(service);
                NetGameplaySyncBridge.Attach(service);

                // STEAM-2: consume (one-shot) the pending Steam join target set by ApplySteamClient, if any —
                // NetService.Start(Client) connects synchronously at the end, so this must be set first.
                if (mode == NetMode.Client && _pendingSteamJoinTarget.HasValue)
                {
                    var t = _pendingSteamJoinTarget.Value;
                    service.SetConnectTarget(t.Address, t.Port, t.Label);
                }
                _pendingSteamJoinTarget = null;

                service.Start(mode);
                // HOST ONLY: networking now starts AFTER the save is loaded (explicit Create), so the GoToLevel
                // that loaded the host's current level fired before this service existed. Seed the run-state from
                // the cached last level + current seed so the host's handshake scene request to a joining client
                // is valid immediately (the client then auto-follows on join, not ~10s later — Log186).
                //
                // Do NOT prime the CLIENT: its pre-join run-state is irrelevant (it immediately follows the host,
                // and the finalized generation snapshot sets the real seed). Priming the client captured its
                // pre-join level seed and latched it; when the client then followed into a SAME-NAMED level the
                // game's GameManager.currentSeed was not refreshed, so the stale primed seed stuck in the run
                // state and the proxy seed-match check rejected the host → the client never showed the host even
                // though it had correctly joined the host's instance (Log198).
                if (mode == NetMode.Host)
                    NetRunStateBridge.PrimeServiceFromCache(service);

                // BOTH roles: capture the CURRENT local player immediately. AddPlayer fired when the save loaded
                // (before this service existed), so without this the local player is only captured on the next
                // level change — a host that stays put (e.g. in the hub) would never broadcast its transform and
                // the client would never see it (Log199). No staleness concern (it's the live player object).
                NetRunStateBridge.PrimeLocalPlayer(service);
                _service = service;
                CurrentMode = mode;
                _lastSignature = signature;
                Plugin.Log.Info($"[CoopConn] networking started mode={mode} (reason={reason}).");
            }
            catch (Exception ex)
            {
                // Roll back to a clean Off state — a half-started service must not be left attached.
                NetRunStateBridge.Attach(null);
                NetGameplaySyncBridge.Attach(null);
                _service = null;
                CurrentMode = NetMode.Off;
                _lastSignature = null;
                NetConnectFeedback.ReportError(UI.CoopLoc.Format("connect.error.couldNotStart",
                    "Could not start networking ({type}). The port may be in use or LiteNetLib is missing.",
                    ("type", ex.GetType().Name)));
                Plugin.Log.Error($"[CoopConn] failed to start mode={mode} — LiteNetLib missing or socket error. ({ex.GetType().Name}: {ex.Message})");
            }
        }

        /// <summary>
        /// STEAM-2: join a host over Steam P2P instead of Direct IP. Opens the client-side
        /// <see cref="SteamRelayBridge"/> (a loopback UDP relay to <paramref name="hostId"/> over Steam), then
        /// applies exactly like a normal Client join except <see cref="NetService.ConnectToHost"/> targets that
        /// local relay port instead of the configured HostAddress/HostPort. The Connection key still gates the
        /// handshake — same validation as Direct IP, untouched.
        /// </summary>
        public static void ApplySteamClient(CSteamID hostId, string reason)
        {
            NetConnectFeedback.BeginAttempt();
            if (!SteamRelayBridge.StartJoining(hostId, out int localPort, out string error))
            {
                NetConnectFeedback.ReportError(error ?? UI.CoopLoc.Get("connect.error.steamStart", "Could not start the Steam connection."));
                return;
            }
            _pendingSteamJoinTarget = ("127.0.0.1", localPort, $"Steam ({hostId.m_SteamID})");
            Apply(NetMode.Client, reason);
            if (CurrentMode != NetMode.Client)
            {
                // Apply rolled back to Off (start failure) — don't leave an orphaned relay with no service
                // feeding it.
                SteamRelayBridge.StopJoining();
            }
        }

        /// <summary>
        /// STEAM-2/3: open the currently-running Host to Steam P2P joins, in addition to (never instead of)
        /// Direct IP — a deliberate opt-in (the connect page's "Invite Friends"), not automatic on Create, so a
        /// host who only wants LAN friends never exposes Steam-facing surface. No-op (false) when not hosting or
        /// Steam isn't available. Safe to call repeatedly: the bridge itself only starts once, but the Steam
        /// overlay invite dialog is re-opened on every call so a host can invite a second/third friend (or
        /// re-invite one who missed the popup) without closing and recreating the whole room just to see it again.
        /// </summary>
        public static bool EnableSteamHosting(string reason)
        {
            if (CurrentMode != NetMode.Host || _service == null) return false;
            if (!SteamHostingEnabled)
            {
                if (!SteamRelayBridge.StartHosting(BuildHostGamePort()))
                {
                    NetConnectFeedback.ReportError(UI.CoopLoc.Get("connect.error.steamUnavailable", "Steam is not available."));
                    return false;
                }
                SteamHostingEnabled = true;
                Plugin.Log.Info($"[CoopConn] Steam hosting enabled (reason={reason}).");
            }
            if (SteamNetworkingSupport.TryGetLocalSteamId(out var localId))
                SteamRichPresenceJoin.AdvertiseHosting(localId);
            return true;
        }

        public static void DisableSteamHosting(string reason)
        {
            if (!SteamHostingEnabled) return;
            SteamRelayBridge.StopHosting();
            SteamRichPresenceJoin.StopAdvertisingHosting();
            SteamHostingEnabled = false;
            Plugin.Log.Info($"[CoopConn] Steam hosting disabled (reason={reason}).");
        }

        private static int BuildHostGamePort()
        {
            try { return Plugin.Cfg.HostPort.Value; }
            catch { return 9050; }
        }

        // Connection-relevant settings; a change here means a Connect must actually restart the socket.
        private static string BuildSignature(NetMode mode)
        {
            if (mode == NetMode.Off) return "off";
            try
            {
                return string.Join("|", new[]
                {
                    mode.ToString(),
                    Plugin.Cfg.HostAddress.Value,
                    Plugin.Cfg.HostPort.Value.ToString(),
                    Plugin.Cfg.MaxPlayers.Value.ToString(),
                    Plugin.Cfg.ConnectionKey.Value,
                    Plugin.Cfg.PlayerName.Value,
                });
            }
            catch { return mode.ToString(); }
        }

        /// <summary>Stop networking and return to Off.</summary>
        public static void Stop(string reason) => Apply(NetMode.Off, reason);

        public static void Tick()
        {
            _service?.Tick();
            SteamRelayBridge.Tick(); // STEAM-2: pump the Steam<->loopback byte relay (no-op when neither role is active)
        }

        public static void FixedTick() => _service?.FixedTick();
    }
}
