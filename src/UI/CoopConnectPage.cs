#if NATIVE_UI_LIB
using System;
using System.Collections.Generic;
using Ryuka.Sulfur.NativeUI;
using SULFURTogether.Networking;
using UnityEngine;

namespace SULFURTogether.UI
{
    /// <summary>
    /// UI-3c: the in-game co-op connect page, reworked from the interim UI-3b page onto the SULFUR Native UI
    /// Lib 0.10 live-update handles. Layout follows the canonical target design in <c>Docs/CoopUiPlan.md</c> §5
    /// (this slice covers the "no-unknowns" body — Steam-name auto-seed and the host LAN-IP display are still
    /// deferred). The status line, Create/Join enabled state, join-failure reason, and live player list are
    /// updated in place each tick via the handles — no full-page <c>Rebuild</c>.
    ///
    /// Compiled only when the lib reference is available (<c>NATIVE_UI_LIB</c>) and registered at runtime only
    /// when the lib is actually loaded (see <c>Plugin.WireCoopUi</c>), so the soft dependency holds: without the
    /// lib the mod runs, just without this page.
    /// </summary>
    internal static class CoopConnectPage
    {
        private const string PageId  = "ryuka.sulfur.together";
        private const string RepoUrl = "https://github.com/ryuka-dev/SULFUR-Together";

        private static readonly Color ErrorColor  = new Color(1f, 0.45f, 0.45f, 1f);
        private static readonly Color OkColor     = new Color(0.55f, 1f, 0.65f, 1f);
        private static readonly Color NeutralColor = new Color(0.80f, 0.82f, 0.88f, 1f);

        private static bool _registered;

        // Live handles for the current page build (null between/after builds). All updates go through these.
        private static SulfurOptionsContext _ctx;
        private static SulfurTextHandle     _statusHandle;
        private static SulfurTextHandle     _joinFeedbackHandle;
        private static SulfurTextHandle     _gateHintHandle;
        private static SulfurButtonHandle   _createHandle;
        private static SulfurButtonHandle   _joinHandle;
        private static SulfurButtonHandle   _closeHandle;
        private static SulfurListHandle     _playerListHandle;
        private static string               _lastPlayerSig = "\0"; // forces a first paint

        // Draft field values, reloaded from config on every page build and pushed to config on Save/Create/Join.
        private static string _draftName;
        private static string _draftAddress;
        private static string _draftPort;
        private static string _draftKey;

        private static float _nextTick;

        // A Join is in flight and the menu should close once (and only if) the connection is confirmed. Set on
        // Join, consumed in Tick when the attempt resolves: closed on success, dropped (menu kept open) on failure.
        private static bool _closeMenuOnJoinSuccess;

        public static void Register()
        {
            if (_registered) return;
            SulfurOptionsApi.RegisterPage(new SulfurOptionsPage
            {
                PageId      = PageId,
                DisplayName = "SULFUR Together",
                SortOrder   = 1000,
                BuildPage   = BuildPage,
            });
            _registered = true;
        }

        public static void Unregister()
        {
            if (!_registered) return;
            SulfurOptionsApi.UnregisterPage(PageId);
            _registered = false;
            ResetHandles();
        }

        /// <summary>Drive the live handles. Called from Plugin.Update; throttled. Safe (and a no-op) when the
        /// page was never built or is currently closed — handle calls on a closed page are harmless.</summary>
        public static void Tick()
        {
            if (!_registered || _ctx == null || _statusHandle == null) return;
            float now = Time.unscaledTime;
            if (now < _nextTick) return;
            _nextTick = now + 0.4f;

            try
            {
                string status = StatusLine();
                _statusHandle.SetText(status);
                _statusHandle.SetColor(StatusColor());
                _ctx.SetFooterStatus(status);
                ApplyButtonStates();
                ApplyJoinFeedback();
                RefreshPlayerList();
                PollJoinClose();
            }
            catch
            {
                // The options page may have been torn down between ticks; ignore — the next BuildPage re-arms us.
            }
        }

        private static void BuildPage(SulfurOptionsContext ctx)
        {
            ResetHandles();
            _ctx = ctx;
            LoadDraftsFromConfig();

            // --- Current status -------------------------------------------------------------------------
            ctx.AddSection("SULFUR Together");
            ctx.AddDescription("Early-preview co-op. Set up your connection below.");
            _statusHandle = ctx.AddTextRow(StatusLine());
            _statusHandle.SetColor(StatusColor());

            // --- Player ---------------------------------------------------------------------------------
            ctx.AddSection("Player");
            ctx.AddInlineTextInput("Player name", _draftName, v => _draftName = v);

            // --- Connection (host / join / leave) -------------------------------------------------------
            ctx.AddSection("Connection");
            ctx.AddDescription("Host a co-op session or join one. Editing the fields changes nothing until you press a button. Close room (host) / Leave (client) ends the session for you.");
            ctx.AddInlineTextInput("Host address (IP)", _draftAddress, v => _draftAddress = v);
            ctx.AddInlineTextInput("Port", _draftPort, v => _draftPort = v);
            ctx.AddInlineTextInput("Connection key", _draftKey, v => _draftKey = v);

            IReadOnlyList<SulfurButtonHandle> connButtons = ctx.AddButtonRow(
                new SulfurButton("Create game", OnCreate, 170f),
                new SulfurButton("Join game", OnJoin, 170f),
                new SulfurButton("Close room", OnCloseRoom, 150f));
            _createHandle = Handle(connButtons, 0);
            _joinHandle   = Handle(connButtons, 1);
            _closeHandle  = Handle(connButtons, 2);

            _gateHintHandle = ctx.AddTextRow("");
            _gateHintHandle.SetVisible(false);

            _joinFeedbackHandle = ctx.AddTextRow("");
            _joinFeedbackHandle.SetVisible(false);

            // --- Players in session (live, read-only; ping + kick deferred §7) --------------------------
            ctx.AddSection("Players in session");
            _playerListHandle = ctx.AddList();
            _lastPlayerSig = "\0";

            // --- Local preferences (per-player) ---------------------------------------------------------
            ctx.AddSection("Local preferences (only affect you)");
            ctx.AddToggle(
                "Show player join/leave notifications",
                "Brief top-right toasts when a player joins or leaves.",
                ReadBool(() => Plugin.Cfg.EnableCoopToasts.Value, true),
                v => { try { Plugin.Cfg.EnableCoopToasts.Value = v; } catch { } });
            ctx.AddReadonlyText("Show network status on HUD", "Coming soon");
            ctx.AddReadonlyText("Show other players' names", "Coming soon");
            ctx.AddReadonlyText("Rescue key", KeyText(() => Plugin.Cfg.PlayerReviveHoldKey.Value.ToString()));
            ctx.AddReadonlyText("Confirm-enter-boss-room key", KeyText(() => Plugin.Cfg.ArenaEnterConfirmKey.Value.ToString()));

            // --- Session settings (host-authoritative; all deferred §7) ---------------------------------
            ctx.AddSection("Session settings (host)");
            ctx.AddReadonlyText("Loot mode", "Independent (Shared coming soon)");
            ctx.AddReadonlyText("Client may start next level", "Coming soon");
            ctx.AddReadonlyText("Friendly fire", "Coming soon");

            // --- About ----------------------------------------------------------------------------------
            ctx.AddSection("About");
            ctx.AddReadonlyText("Version", ModInfo.Version);
            ctx.AddSmallButton("Open-source repo", () => OpenUrl(RepoUrl));
            ctx.AddReadonlyText("Ko-fi", "Coming soon");

            ctx.SetFooter("SULFUR Together", StatusLine(), "Save settings", () =>
            {
                SaveSettings();
                ctx.SetFooterStatus("Settings saved.");
            });

            // First paint of the live elements.
            ApplyButtonStates();
            ApplyJoinFeedback();
            RefreshPlayerList();
        }

        // ----- Live-element updates ---------------------------------------------------------------------

        private static string StatusLine()
        {
            var svc = CoopConnection.Service;
            if (svc == null) return "● Not connected";
            if (NetConnectFeedback.Connecting) return "◌ Connecting…";
            return "● " + svc.GetConnectionSummary();
        }

        private static Color StatusColor()
        {
            if (CoopConnection.Service == null) return NeutralColor;
            return NetConnectFeedback.Connecting ? NeutralColor : OkColor;
        }

        /// <summary>Create and Join are mutually exclusive: only one role can be live at a time (CoopUiPlan §5).
        /// The third button ends the session — "Close room" for a host, "Leave" for a client — and is disabled
        /// while networking is off.</summary>
        private static void ApplyButtonStates()
        {
            var mode = CoopConnection.CurrentMode;
            // Create/Join require being in a loaded save — co-op is started from inside the game, not the main
            // menu (the whole world/run is host-authoritative, so there must be a run to host or join).
            bool inGame = IsInGame();
            // Off → Create + Join available (in-game), end-session disabled. Host running → re-Create allowed
            // (re-apply), Join blocked. Client → only the end-session ("Leave") button is live.
            _createHandle?.SetInteractable(inGame && mode != NetMode.Client);
            _joinHandle?.SetInteractable(inGame && mode == NetMode.Off);
            _closeHandle?.SetInteractable(mode != NetMode.Off);
            _closeHandle?.SetLabel(mode == NetMode.Client ? "Leave" : "Close room");

            if (_gateHintHandle != null)
            {
                bool showHint = !inGame && mode == NetMode.Off;
                if (showHint)
                {
                    _gateHintHandle.SetText("Load a save first — co-op is hosted / joined from inside the game.");
                    _gateHintHandle.SetColor(NeutralColor);
                }
                _gateHintHandle.SetVisible(showHint);
            }
        }

        private static void ApplyJoinFeedback()
        {
            if (_joinFeedbackHandle == null) return;
            string err = NetConnectFeedback.LastError;
            if (string.IsNullOrEmpty(err))
            {
                _joinFeedbackHandle.SetVisible(false);
                return;
            }
            _joinFeedbackHandle.SetText("⚠ " + err);
            _joinFeedbackHandle.SetColor(ErrorColor);
            _joinFeedbackHandle.SetVisible(true);
        }

        private static void RefreshPlayerList()
        {
            if (_playerListHandle == null) return;
            var svc = CoopConnection.Service;
            IReadOnlyList<string> rows = svc != null ? svc.GetPlayerRows() : Array.Empty<string>();

            string sig = string.Join("|", rows);
            if (sig == _lastPlayerSig) return; // unchanged — don't rebuild the list rows
            _lastPlayerSig = sig;

            _playerListHandle.Update(c =>
            {
                if (rows.Count == 0)
                {
                    c.AddTextRow("No players connected.");
                    return;
                }
                foreach (var row in rows)
                    c.AddTextRow(row);
            });
        }

        // ----- Button actions ---------------------------------------------------------------------------

        private static void OnCreate()
        {
            if (!IsInGame()) { _ctx?.SetFooterStatus("Load a save first."); return; }
            SaveSettings();
            Plugin.Cfg.NetworkMode.Value = NetMode.Host.ToString();
            try { Plugin.Cfg.EnableNetworking.Value = true; } catch { }
            CoopConnection.Apply(NetMode.Host, "ui-create");
            ApplyButtonStates();
        }

        private static void OnJoin()
        {
            if (!IsInGame()) { _ctx?.SetFooterStatus("Load a save first."); return; }
            // Do NOT close the menu yet — only a *successful* join should return the player to the game; a failed
            // join keeps the menu open so the error feedback is visible. The close is deferred to PollJoinClose,
            // which fires the moment the handshake resolves. The wedge the menu could cause over the black-screen
            // load is independently handled host-side: the host-driven follow closes any open menu before its fade
            // (NetManualSceneFollower.CloseIfOpen("host-driven-follow")), which on a real join always runs first.
            _closeMenuOnJoinSuccess = true;
            SaveSettings();
            Plugin.Cfg.NetworkMode.Value = NetMode.Client.ToString();
            try { Plugin.Cfg.EnableNetworking.Value = true; } catch { }
            CoopConnection.Apply(NetMode.Client, "ui-join"); // synchronously enters NetConnectFeedback.Connecting
            // Link synchronously, before the (async) handshake. The host sends its current scene request on
            // handshake, so the now-linked client's auto-follow then brings it into the host's scene — no
            // separate follow trigger needed (a second one would double-load and corrupt the join; Log185).
            NetLinkState.SetClientLinked(true, "ui-join");
            ApplyButtonStates();
            ApplyJoinFeedback();
        }

        /// <summary>Resolve a pending Join: close the menu once the connection is confirmed, drop the request
        /// (leaving the menu open with its error) if it failed or was torn down. Connecting → keep waiting.</summary>
        private static void PollJoinClose()
        {
            if (!_closeMenuOnJoinSuccess) return;
            if (NetConnectFeedback.Connecting) return; // attempt still in flight

            // Resolved. Success = still in Client mode with no recorded error; anything else is a failure/abort.
            bool joined = CoopConnection.CurrentMode == NetMode.Client
                          && string.IsNullOrEmpty(NetConnectFeedback.LastError);
            _closeMenuOnJoinSuccess = false;
            if (joined) CoopMenu.CloseIfOpen("ui-join-success");
        }

        /// <summary>Hard stop: tear the network down entirely (host closes the room / client leaves).</summary>
        private static void OnCloseRoom()
        {
            _closeMenuOnJoinSuccess = false;
            try { Plugin.Cfg.EnableNetworking.Value = false; } catch { }
            CoopConnection.Stop("ui-close-room");
            ApplyButtonStates();
        }

        // ----- Drafts / config --------------------------------------------------------------------------

        private static void LoadDraftsFromConfig()
        {
            _draftName    = Plugin.Cfg.PlayerName.Value;
            _draftAddress = Plugin.Cfg.HostAddress.Value;
            _draftPort    = Plugin.Cfg.HostPort.Value.ToString();
            _draftKey     = Plugin.Cfg.ConnectionKey.Value;
        }

        /// <summary>Persist the edited fields to config. Does NOT touch the connection or NetworkMode (the role
        /// is decided by Create vs Join).</summary>
        private static void SaveSettings()
        {
            Plugin.Cfg.PlayerName.Value  = string.IsNullOrWhiteSpace(_draftName) ? "Player" : _draftName.Trim();
            Plugin.Cfg.HostAddress.Value = string.IsNullOrWhiteSpace(_draftAddress) ? "127.0.0.1" : _draftAddress.Trim();
            if (int.TryParse(_draftPort, out var port) && port > 0 && port < 65536)
                Plugin.Cfg.HostPort.Value = port;
            Plugin.Cfg.ConnectionKey.Value = _draftKey ?? "";
        }

        // ----- Helpers ----------------------------------------------------------------------------------

        private static void ResetHandles()
        {
            _ctx                = null;
            _statusHandle       = null;
            _joinFeedbackHandle = null;
            _gateHintHandle     = null;
            _createHandle       = null;
            _joinHandle         = null;
            _playerListHandle   = null;
            _lastPlayerSig      = "\0";
        }

        private static SulfurButtonHandle Handle(IReadOnlyList<SulfurButtonHandle> handles, int index)
            => handles != null && index >= 0 && index < handles.Count ? handles[index] : null;

        private static bool ReadBool(Func<bool> read, bool fallback)
        {
            try { return read(); } catch { return fallback; }
        }

        private static string KeyText(Func<string> read)
        {
            try { return read(); } catch { return "(unset)"; }
        }

        private static void OpenUrl(string url)
        {
            try { Application.OpenURL(url); }
            catch (Exception e) { Plugin.Log?.Warn($"[CoopUi] open URL failed: {e.Message}"); }
        }

        /// <summary>True when a save is loaded and the player is in a run (not the title screen). SULFUR runs all
        /// gameplay — the hub and every generated level — inside one persistent Unity scene, <c>GameScene</c>
        /// (loaded by the game's <c>LoadGameScene</c>); the title screen is its own <c>MainMenu</c> scene. The
        /// active Unity scene is therefore the authoritative "is a save loaded" signal. (loadingMode is NOT: a
        /// save loaded into the hub uses loadingMode=Menu, same as the title backdrop — Log189. PlayerUnit is
        /// NOT either: the menu backdrop has one too.) Networking-independent, so the gate holds before connect.</summary>
        private static bool IsInGame()
        {
            try
            {
                string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? "";
                return string.Equals(scene, "GameScene", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }
}
#endif
