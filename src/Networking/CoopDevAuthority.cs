using System;
using System.Collections.Generic;
using UnityEngine;
using PerfectRandom.Sulfur.Core;

namespace SULFURTogether.Networking
{
    /// <summary>
    /// DEV-1: the local player's immutable <b>developer entitlement</b> — did this game instance launch with
    /// dev access? Separate concept from the live <see cref="GameManager.DeveloperMode"/> flag (CLAUDE.md §3):
    /// entitlement is a fixed fact about the launch, the effective flag is a session/mode-dependent state that
    /// <see cref="CoopDevAuthority"/> drives.
    ///
    /// Sources mirror the game's own dev unlocks: the <c>-dev true</c> command-line arg (read once by
    /// <c>AsyncAssetLoading.Awake</c>) and the persisted <c>DevToolsEnabled</c> PlayerPref (set by the secret
    /// unlock combos). Re-read live so a secret-combo unlock during a solo session is honoured; the command line
    /// is constant and the PlayerPref read is cheap.
    /// </summary>
    internal static class CoopDevEntitlement
    {
        public static bool Local
        {
            get
            {
                try
                {
                    string args = string.Join(" ", System.Environment.GetCommandLineArgs());
                    if (args.Contains("-dev true")) return true;
                }
                catch { /* command line unavailable — fall through */ }
                try { return PlayerPrefs.GetInt("DevToolsEnabled", 0) == 1; }
                catch { return false; }
            }
        }
    }

    internal enum CoopDevRole { Solo, Host, Client }

    /// <summary>
    /// DEV-1: single authority for the effective <see cref="GameManager.DeveloperMode"/> flag.
    ///
    /// - <b>Solo</b> (no live session): the flag follows the local <see cref="CoopDevEntitlement"/> — a lone
    ///   player's <c>-dev true</c>/secret-combo unlock means exactly what it does in vanilla. The authority
    ///   applies this once on the session→solo transition and then leaves the flag alone (so the vanilla
    ///   secret combos still work solo).
    /// - <b>Session</b> (hosting, or connected as a client): the flag is host-authoritative and uniform for
    ///   every peer. The host enables it only when <b>all</b> connected participants are dev-entitled (auto)
    ///   or a unanimous vote passed (VOTE-1); a membership change voids that consensus and fails closed.
    ///   The value is synced through <see cref="NetSessionSettings"/> (the FF-1 session-settings channel).
    ///   While in a session the authority re-asserts the flag on a slow cadence so a client's local secret
    ///   combo (or the game's own <c>LocalizationManager.Reset</c>) cannot bypass the session decision.
    ///
    /// The authority never writes the <c>DevToolsEnabled</c> PlayerPref — the session flag is transient and
    /// must not persist as a preference (issue #8).
    /// </summary>
    internal static class CoopDevAuthority
    {
        private static CoopDevRole _role = CoopDevRole.Solo;

        // Host only: peerId → entitlement for each connected participant, including the host itself ("host").
        private static readonly Dictionary<string, bool> _peerEntitlement = new Dictionary<string, bool>();

        private static bool  _hostSessionDev;   // host-authoritative session dev flag (the wire truth on the host)
        private static bool  _voteForcedDev;     // VOTE-1: a passed EnableDevMode vote forces dev on until membership changes
        private static float _nextReassert;

        /// <summary>Host-authoritative session dev flag — the value written into the settings broadcast.</summary>
        public static bool HostSessionDevEnabled => _hostSessionDev;

        // ---------------------------------------------------------------- lifecycle transitions (from NetService)

        public static void OnHostStarted()
        {
            _role = CoopDevRole.Host;
            _peerEntitlement.Clear();
            _peerEntitlement["host"] = CoopDevEntitlement.Local;
            _voteForcedDev = false;
            _hostSessionDev = false;
            RecomputeHost("host started");
        }

        public static void OnClientConnected()
        {
            _role = CoopDevRole.Client;
            ApplyEffective("client connected");
        }

        public static void OnSessionEnded(string reason)
        {
            _role = CoopDevRole.Solo;
            _peerEntitlement.Clear();
            _hostSessionDev = false;
            _voteForcedDev = false;
            ApplyEffective("session ended: " + reason);
        }

        // ---------------------------------------------------------------- host membership + vote inputs

        public static void HostPeerJoined(string peerId, bool entitled)
        {
            if (_role != CoopDevRole.Host) return;
            // A membership change voids any prior vote consensus — it was reached among a different set of players.
            _voteForcedDev = false;
            _peerEntitlement[peerId] = entitled;
            RecomputeHost($"peer joined ({peerId} entitled={entitled})");
        }

        public static void HostPeerLeft(string peerId)
        {
            if (_role != CoopDevRole.Host) return;
            _voteForcedDev = false;
            if (_peerEntitlement.Remove(peerId))
                RecomputeHost($"peer left ({peerId})");
        }

        /// <summary>VOTE-1: a passed unanimous EnableDevMode vote forces session dev on (until membership changes).</summary>
        public static void HostApplyVoteResult(bool devEnabled)
        {
            if (_role != CoopDevRole.Host) return;
            _voteForcedDev = devEnabled;
            RecomputeHost($"vote result devEnabled={devEnabled}");
        }

        // ---------------------------------------------------------------- client settings mirror

        /// <summary>Client: the host's session-settings snapshot was applied — re-assert the local flag.</summary>
        public static void OnClientSessionSettingsApplied()
        {
            if (_role != CoopDevRole.Client) return;
            ApplyEffective("client session settings applied");
        }

        // ---------------------------------------------------------------- per-frame guard (from NetService.Tick)

        /// <summary>
        /// Slow re-assert while in a session so a client's local secret combo or a vanilla stomp of
        /// <see cref="GameManager.DeveloperMode"/> cannot diverge from the host-authoritative decision. In solo
        /// the flag is left alone (vanilla dev unlocks keep working).
        /// </summary>
        public static void TickReassert(float now)
        {
            if (_role == CoopDevRole.Solo) return;
            if (now < _nextReassert) return;
            _nextReassert = now + 0.5f;
            ApplyEffective("reassert");
        }

        // ---------------------------------------------------------------- internals

        private static bool AllEntitled()
        {
            if (_peerEntitlement.Count == 0) return false;
            foreach (var entitled in _peerEntitlement.Values)
                if (!entitled) return false;
            return true;
        }

        private static void RecomputeHost(string reason)
        {
            bool allEntitled = AllEntitled();
            bool target = allEntitled || _voteForcedDev;
            bool changed = target != _hostSessionDev;

            if (changed)
            {
                _hostSessionDev = target;
                Plugin.Log?.Info($"[Dev] host session dev {(target ? "ENABLED" : "disabled")} (allEntitled={allEntitled} vote={_voteForcedDev}) reason={reason}");
                // Push the new value to every client (they mirror + toast via HandleSessionSettings) and tell the host.
                CoopConnection.Service?.BroadcastSessionSettings("dev mode: " + reason);
                UI.CoopToasts.NotifyDeveloperMode(target);
            }

            ApplyEffective("host recompute: " + reason);
        }

        private static void ApplyEffective(string reason)
        {
            bool target = _role == CoopDevRole.Solo
                ? CoopDevEntitlement.Local
                : NetSessionSettings.DeveloperModeEnabled;

            try
            {
                if (GameManager.DeveloperMode != target)
                {
                    GameManager.DeveloperMode = target;
                    Plugin.Log?.Info($"[Dev] GameManager.DeveloperMode -> {target} (role={_role} reason={reason})");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.Warn($"[Dev] failed to apply DeveloperMode: {ex.Message}");
            }
        }
    }
}
