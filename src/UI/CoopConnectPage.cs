#if NATIVE_UI_LIB
using System;
using System.Collections.Generic;
using Ryuka.Sulfur.NativeUI;
using Steamworks;
using SULFURTogether.Networking;
using SULFURTogether.Networking.Vote;
using TMPro;
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
        private const string KoFiUrl = "https://ko-fi.com/ryukadev";

        private static readonly Color ErrorColor  = new Color(1f, 0.45f, 0.45f, 1f);
        private static readonly Color OkColor     = new Color(0.55f, 1f, 0.65f, 1f);
        private static readonly Color NeutralColor = new Color(0.80f, 0.82f, 0.88f, 1f);

        private static bool _registered;

        // Live handles for the current page build (null between/after builds). All updates go through these.
        private static SulfurOptionsContext _ctx;
        private static SulfurTextHandle     _statusHandle;
        private static SulfurTextHandle     _joinFeedbackHandle;
        private static SulfurTextHandle     _gateHintHandle;
        private static SulfurTextHandle     _lanIpHandle;
        private static SulfurButtonHandle   _createHandle;
        private static SulfurButtonHandle   _joinHandle;
        private static SulfurButtonHandle   _closeHandle;
        private static SulfurListHandle     _playerListHandle;
        private static string               _lastPlayerSig = "\0"; // forces a first paint

        // STEAM-4: Steam connect-method block handles.
        private static SulfurTextHandle   _steamUnavailableHandle;
        private static SulfurTextHandle   _yourSteamIdHandle;
        private static SulfurButtonHandle _steamInviteHandle;
        private static SulfurButtonHandle _steamJoinHandle;
        private static SulfurTextHandle   _steamPendingInviteHandle;
        private static TMP_InputField     _steamIdInputField;
        private static string             _autoFilledSteamId; // guards the pending-invite auto-fill to once per invite

        // FF-1: read-only session friendly-fire line (visible only while connected as a client).
        private static SulfurTextHandle   _ffSessionHandle;

        // VOTE-1 (issue #8): the "Session events" section — a status line + the "propose dev mode vote" button.
        private static SulfurTextHandle   _devVoteStatusHandle;
        private static SulfurButtonHandle _devVoteButton;

        // FF-1b/1c: the FF toggle's native row. On a client the whole "Session settings (host)" section must be
        // visibly non-operable: the row is dimmed + input-blocked (see ApplyFfRowLock for why a CanvasGroup, not the
        // native SetLocked) and the checkbox FOLLOWS the host's synced session value via SetIsOnWithoutNotify (the
        // client's own coop.json preference is never touched by that mirroring).
        private static PerfectRandom.Sulfur.Core.OptionsScreenOption _ffToggleOption;
        private static bool _ffLockApplied;
        private static readonly System.Reflection.FieldInfo FfCheckboxField =
            typeof(PerfectRandom.Sulfur.Core.OptionsScreenOption).GetField("checkboxToggle",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        private static readonly System.Reflection.FieldInfo FfIsLockedField =
            typeof(PerfectRandom.Sulfur.Core.OptionsScreenOption).GetField("IsLocked",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        // Draft field values, reloaded from config on every page build and pushed to config on Save/Create/Join.
        private static string _draftName;
        private static string _draftAddress;
        private static string _draftPort;
        private static string _draftKey;
        private static string _draftSteamId;

        // Auto-save: signature of the drafts last persisted to config. The fields write only to the drafts on edit;
        // Tick persists them when this signature changes (a built-in ~0.4s debounce, no per-keystroke disk writes).
        private static string _lastSavedSig;

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
                AutoSaveDrafts();
                string status = StatusLine();
                _statusHandle.SetText(status);
                _statusHandle.SetColor(StatusColor());
                ApplyButtonStates();
                ApplyJoinFeedback();
                ApplyHostLanIp();
                ApplySteamState();
                ApplySessionFriendlyFireControl();
                ApplyDevVoteControl();
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
            ctx.AddDescription(CoopLoc.Get("connect.desc.intro", "Early-preview co-op. Set up your connection below."));
            _statusHandle = ctx.AddTextRow(StatusLine());
            _statusHandle.SetColor(StatusColor());

            // --- Player ---------------------------------------------------------------------------------
            ctx.AddSection(CoopLoc.Get("connect.section.player", "Player"));
            ctx.AddInlineTextInput(CoopLoc.Get("connect.label.playerName", "Player name"), _draftName, v => _draftName = v);

            // --- Connection (host / join / leave) -------------------------------------------------------
            ctx.AddSection(CoopLoc.Get("connect.section.connection", "Connection"));
            ctx.AddDescription(CoopLoc.Get("connect.desc.connection", "Host a co-op session or join one. Your settings save automatically as you edit them. Close room (host) / Leave (client) ends the session for you."));
            ctx.AddInlineTextInput(CoopLoc.Get("connect.label.hostAddress", "Host address (IP)"), _draftAddress, v => _draftAddress = v);
            ctx.AddInlineTextInput(CoopLoc.Get("connect.label.port", "Port"), _draftPort, v => _draftPort = v);
            ctx.AddInlineTextInput(CoopLoc.Get("connect.label.connectionKey", "Connection key"), _draftKey, v => _draftKey = v);

            IReadOnlyList<SulfurButtonHandle> connButtons = ctx.AddButtonRow(
                new SulfurButton(CoopLoc.Get("connect.button.create", "Create game"), OnCreate, 170f),
                new SulfurButton(CoopLoc.Get("connect.button.join", "Join game"), OnJoin, 170f),
                new SulfurButton(CoopLoc.Get("connect.button.closeRoom", "Close room"), OnCloseRoom, 150f));
            _createHandle = Handle(connButtons, 0);
            _joinHandle   = Handle(connButtons, 1);
            _closeHandle  = Handle(connButtons, 2);

            _gateHintHandle = ctx.AddTextRow("");
            _gateHintHandle.SetVisible(false);

            // UI-3d: host LAN address — shown only while hosting so peers know what to type into "Host address".
            _lanIpHandle = ctx.AddTextRow("");
            _lanIpHandle.SetVisible(false);

            _joinFeedbackHandle = ctx.AddTextRow("");
            _joinFeedbackHandle.SetVisible(false);

            // --- Steam (second connection method — additive, Direct IP above is unchanged) --------------
            ctx.AddSection("Steam");
            ctx.AddDescription(CoopLoc.Get("connect.desc.steam", "Connect over Steam instead of a typed IP — no port forwarding needed. Works alongside Direct IP: a host can accept both at once. Create a game first — you can only invite friends while hosting."));
            _steamUnavailableHandle = ctx.AddTextRow(CoopLoc.Get("connect.steam.unavailable", "Steam is not available — connect method disabled."));
            _steamUnavailableHandle.SetColor(NeutralColor);
            _steamUnavailableHandle.SetVisible(false);

            _yourSteamIdHandle = ctx.AddTextRow("");
            _yourSteamIdHandle.SetVisible(false);

            IReadOnlyList<SulfurButtonHandle> inviteRow = ctx.AddButtonRow(
                new SulfurButton(CoopLoc.Get("connect.button.inviteFriends", "Invite Friends via Steam"), OnInviteFriends, 220f));
            _steamInviteHandle = Handle(inviteRow, 0);

            _steamIdInputField = ctx.AddInlineTextInput(CoopLoc.Get("connect.label.steamIdToJoin", "Steam ID to join"), _draftSteamId, v => _draftSteamId = v);
            IReadOnlyList<SulfurButtonHandle> steamJoinRow = ctx.AddButtonRow(
                new SulfurButton(CoopLoc.Get("connect.button.joinViaSteam", "Join via Steam"), OnJoinViaSteam, 170f));
            _steamJoinHandle = Handle(steamJoinRow, 0);

            _steamPendingInviteHandle = ctx.AddTextRow("");
            _steamPendingInviteHandle.SetVisible(false);

            // --- Players in session (live, read-only; ping + kick deferred §7) --------------------------
            ctx.AddSection(CoopLoc.Get("connect.section.players", "Players in session"));
            _playerListHandle = ctx.AddList();
            _lastPlayerSig = "\0";

            // --- Local preferences (per-player) ---------------------------------------------------------
            ctx.AddSection(CoopLoc.Get("connect.section.localPrefs", "Local preferences (only affect you)"));
            ctx.AddToggle(
                CoopLoc.Get("connect.label.showToasts", "Show player join/leave notifications"),
                CoopLoc.Get("connect.desc.showToasts", "Brief top-right toasts when a player joins or leaves."),
                ReadBool(() => Plugin.Cfg.EnableCoopToasts.Value, true),
                v => { try { Plugin.Cfg.EnableCoopToasts.Value = v; } catch { } });
            ctx.AddReadonlyText(CoopLoc.Get("connect.label.showHudStatus", "Show network status on HUD"), CoopLoc.Get("connect.value.comingSoon", "Coming soon"));
            ctx.AddReadonlyText(CoopLoc.Get("connect.label.showNames", "Show other players' names"), CoopLoc.Get("connect.value.comingSoon", "Coming soon"));
            ctx.AddReadonlyText(CoopLoc.Get("connect.label.rescueKey", "Rescue key"), KeyText(() => Plugin.Cfg.PlayerReviveHoldKey.Value.ToString()));
            ctx.AddReadonlyText(CoopLoc.Get("connect.label.confirmEnterKey", "Confirm-enter-boss-room key"), KeyText(() => Plugin.Cfg.ArenaEnterConfirmKey.Value.ToString()));

            // --- Session settings (host-authoritative; rest deferred §7) --------------------------------
            ctx.AddSection(CoopLoc.Get("connect.section.sessionSettings", "Session settings (host)"));
            ctx.AddReadonlyText(CoopLoc.Get("connect.label.lootMode", "Loot mode"), CoopLoc.Get("connect.value.lootIndependent", "Independent (Shared coming soon)"));
            ctx.AddReadonlyText(CoopLoc.Get("connect.label.clientMayStart", "Client may start next level"), CoopLoc.Get("connect.value.comingSoon", "Coming soon"));
            // FF-1: the toggle edits this machine's own setting (= the session setting when it hosts). While
            // connected as a CLIENT the row is locked and mirrors the host's synced session value instead (FF-1b,
            // driven from Tick); the read-only line below spells out who owns it.
            _ffToggleOption = ctx.AddToggle(
                CoopLoc.Get("session.friendlyFire.label", "Friendly fire"),
                CoopLoc.Get("connect.desc.friendlyFire", "Players can damage each other. The host's setting applies to the whole session."),
                ReadBool(() => Plugin.Cfg.FriendlyFire.Value, false),
                OnFriendlyFireToggled);
            _ffSessionHandle = ctx.AddTextRow("");
            _ffSessionHandle.SetVisible(false);

            // --- Session events (vote-gated; VOTE-1, issue #8) ------------------------------------------
            // Transient, everyone-must-agree events kept separate from the persistent local prefs / host settings
            // above. Developer mode is the first: any player can propose it, and it needs a unanimous vote (or every
            // player launching with dev access) — a single -dev player can no longer grief the others.
            ctx.AddSection(CoopLoc.Get("connect.section.sessionEvents", "Session events (need everyone's agreement)"));
            _devVoteStatusHandle = ctx.AddTextRow("");
            IReadOnlyList<SulfurButtonHandle> devVoteRow = ctx.AddButtonRow(
                new SulfurButton(CoopLoc.Get("connect.button.proposeDevMode", "Propose: enable developer mode"),
                    OnProposeDevModeVote, 320f));
            _devVoteButton = Handle(devVoteRow, 0);

            // --- About ----------------------------------------------------------------------------------
            ctx.AddSection(CoopLoc.Get("connect.section.about", "About"));
            ctx.AddReadonlyText(CoopLoc.Get("connect.label.version", "Version"), ModInfo.Version);
            // Explicit width: AddSmallButton's auto-size clamps at 180px, which ellipsises these ~16-char
            // labels ("Open-sourc…" / "Support on …"). The minWidth overload bypasses the clamp; the row is
            // full-option width so 260px fits comfortably.
            ctx.AddSmallButton(CoopLoc.Get("connect.button.repo", "Open-source repo"), () => OpenUrl(RepoUrl), 260f);
            ctx.AddSmallButton(CoopLoc.Get("connect.button.kofi", "Support on Ko-fi"), () => OpenUrl(KoFiUrl), 260f);

            // No footer / "Save settings" button — settings persist automatically (see AutoSaveDrafts). Seed the
            // auto-save baseline from the freshly loaded drafts so merely opening the page (incl. the Steam-name seed)
            // doesn't rewrite config; only an actual edit moves the signature and triggers a save.
            _lastSavedSig = DraftSig();

            // First paint of the live elements.
            ApplyButtonStates();
            ApplyJoinFeedback();
            ApplyHostLanIp();
            ApplySteamState();
            ApplySessionFriendlyFireControl();
            ApplyDevVoteControl();
            RefreshPlayerList();
        }

        // ----- Live-element updates ---------------------------------------------------------------------

        private static string StatusLine()
        {
            var svc = CoopConnection.Service;
            if (svc == null) return "● " + CoopLoc.Get("connect.status.notConnected", "Not connected");
            if (NetConnectFeedback.Connecting) return "◌ " + CoopLoc.Get("connect.status.connecting", "Connecting…");
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
            _closeHandle?.SetLabel(mode == NetMode.Client
                ? CoopLoc.Get("connect.button.leave", "Leave")
                : CoopLoc.Get("connect.button.closeRoom", "Close room"));

            if (_gateHintHandle != null)
            {
                bool showHint = !inGame && mode == NetMode.Off;
                if (showHint)
                {
                    _gateHintHandle.SetText(CoopLoc.Get("connect.gateHint", "Load a save first — co-op is hosted / joined from inside the game."));
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

        /// <summary>UI-3d: while hosting, show this machine's LAN address + port so peers on the same network know what
        /// to enter. Hidden when not hosting (a client/off has nothing to advertise).</summary>
        private static void ApplyHostLanIp()
        {
            if (_lanIpHandle == null) return;
            if (CoopConnection.CurrentMode == NetMode.Host && NetLocalAddress.TryGetLanIPv4(out var ip))
            {
                int port = ReadInt(() => Plugin.Cfg.HostPort.Value, 9050);
                _lanIpHandle.SetText(CoopLoc.Format("connect.lanAddress",
                    "Your LAN address: {ip}:{port}  (others on your network join with this)",
                    ("ip", ip.ToString()), ("port", port.ToString())));
                _lanIpHandle.SetColor(OkColor);
                _lanIpHandle.SetVisible(true);
            }
            else
            {
                _lanIpHandle.SetVisible(false);
            }
        }

        /// <summary>STEAM-4: live state for the Steam block — availability line, local Steam ID, Invite/Join
        /// button interactable state (mirrors the Direct-IP Create/Join mutual-exclusion), and the pending-invite
        /// banner (a friend accepted a Steam invite — auto-fills the join field so one click accepts it).</summary>
        private static void ApplySteamState()
        {
            bool available = SteamNetworkingSupportAvailable();
            if (_steamUnavailableHandle != null) _steamUnavailableHandle.SetVisible(!available);

            if (_yourSteamIdHandle != null)
            {
                if (available && TryGetLocalSteamId(out ulong localId))
                {
                    _yourSteamIdHandle.SetText(CoopLoc.Format("connect.steam.yourId",
                        "Your Steam ID: {id}  (share this, or use Invite Friends while hosting)", ("id", localId.ToString())));
                    _yourSteamIdHandle.SetColor(NeutralColor);
                    _yourSteamIdHandle.SetVisible(true);
                }
                else _yourSteamIdHandle.SetVisible(false);
            }

            var mode = CoopConnection.CurrentMode;
            bool inGame = IsInGame();
            _steamInviteHandle?.SetInteractable(available && inGame && mode == NetMode.Host);
            _steamInviteHandle?.SetLabel(CoopConnection.SteamHostingEnabled
                ? CoopLoc.Get("connect.button.inviteMoreFriends", "Invite more friends via Steam")
                : CoopLoc.Get("connect.button.inviteFriends", "Invite Friends via Steam"));
            _steamJoinHandle?.SetInteractable(available && inGame && mode == NetMode.Off);

            if (_steamPendingInviteHandle != null)
            {
                var pending = SteamRichPresenceJoin.PendingInviteHostId;
                if (pending.HasValue && mode == NetMode.Off)
                {
                    string idText = pending.Value.m_SteamID.ToString();
                    string friend = SteamRichPresenceJoin.PendingInviteFriendName;

                    if (inGame && _autoFilledSteamId != idText)
                    {
                        // First time we've seen this exact invite while free to join (not already hosting/
                        // connected) with a save loaded — accept it immediately, same as normal Steam
                        // multiplayer UX (the friends-list "Accept" click IS the whole action, not a shortcut
                        // to a field on a page the player has to remember to reopen). Tick() runs this every
                        // 0.4s regardless of whether the connect page is currently on-screen, so this fires
                        // even if the player is out in the game world when they accept.
                        _autoFilledSteamId = idText;
                        _draftSteamId = idText;
                        if (_steamIdInputField != null) _steamIdInputField.text = idText;
                        _steamPendingInviteHandle.SetText(string.IsNullOrEmpty(friend)
                            ? CoopLoc.Get("connect.steam.joiningFriend", "Joining a Steam friend's game…")
                            : CoopLoc.Format("connect.steam.joiningNamed", "Joining {name}'s SULFUR Together game…", ("name", friend)));
                        _steamPendingInviteHandle.SetColor(OkColor);
                        _steamPendingInviteHandle.SetVisible(true);
                        JoinViaSteam(pending.Value, "steam-invite-auto-join");
                    }
                    else if (!inGame)
                    {
                        // No save loaded yet — can't join. Keep the banner up; the auto-fill guard above hasn't
                        // latched yet, so this fires the auto-join itself the moment IsInGame() flips true.
                        _steamPendingInviteHandle.SetText(string.IsNullOrEmpty(friend)
                            ? CoopLoc.Get("connect.steam.invitedLoadSave", "A Steam friend invited you — load a save to join automatically.")
                            : CoopLoc.Format("connect.steam.invitedLoadSaveNamed", "{name} invited you — load a save to join automatically.", ("name", friend)));
                        _steamPendingInviteHandle.SetColor(OkColor);
                        _steamPendingInviteHandle.SetVisible(true);
                    }
                }
                else _steamPendingInviteHandle.SetVisible(false);
            }
        }

        private static bool SteamNetworkingSupportAvailable()
        {
            try { return SteamNetworkingSupport.IsAvailable; } catch { return false; }
        }

        private static bool TryGetLocalSteamId(out ulong steamId64)
        {
            steamId64 = 0;
            try
            {
                if (!SteamNetworkingSupport.TryGetLocalSteamId(out CSteamID id)) return false;
                steamId64 = id.m_SteamID;
                return true;
            }
            catch { return false; }
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
                    c.AddTextRow(CoopLoc.Get("connect.players.none", "No players connected."));
                    return;
                }
                foreach (var row in rows)
                    c.AddTextRow(row);
            });
        }

        // ----- Button actions ---------------------------------------------------------------------------

        private static void OnCreate()
        {
            if (!IsInGame()) return; // button is disabled out-of-game; the gate-hint row explains why
            SaveSettings();
            // The role is runtime-only now — Apply sets CoopConnection.CurrentMode; nothing is written to the .cfg.
            CoopConnection.Apply(NetMode.Host, "ui-create");
            ApplyButtonStates();
        }

        private static void OnJoin()
        {
            if (!IsInGame()) return; // button is disabled out-of-game; the gate-hint row explains why
            // Do NOT close the menu yet — only a *successful* join should return the player to the game; a failed
            // join keeps the menu open so the error feedback is visible. The close is deferred to PollJoinClose,
            // which fires the moment the handshake resolves. The wedge the menu could cause over the black-screen
            // load is independently handled host-side: the host-driven follow closes any open menu before its fade
            // (NetManualSceneFollower.CloseIfOpen("host-driven-follow")), which on a real join always runs first.
            _closeMenuOnJoinSuccess = true;
            SaveSettings();
            // The role is runtime-only now — Apply sets CoopConnection.CurrentMode; nothing is written to the .cfg.
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

        /// <summary>STEAM-4: open the already-running host to Steam P2P joins (additive to Direct IP — never a
        /// replacement) and pop the Steam overlay invite-friends dialog in the same click.</summary>
        private static void OnInviteFriends()
        {
            if (!IsInGame() || CoopConnection.CurrentMode != NetMode.Host) return;
            CoopConnection.EnableSteamHosting("ui-steam-invite");
            ApplySteamState();
        }

        /// <summary>STEAM-4: join a host via a pasted (or invite-auto-filled) Steam ID instead of a typed IP.
        /// Mirrors <see cref="OnJoin"/> — same deferred menu-close-on-success, same client link timing.</summary>
        private static void OnJoinViaSteam()
        {
            if (!IsInGame()) return;
            string raw = (_draftSteamId ?? "").Trim();
            if (!ulong.TryParse(raw, out ulong steamId64) || steamId64 == 0)
            {
                NetConnectFeedback.ReportError(CoopLoc.Get("connect.error.invalidSteamId", "Enter a valid Steam ID (numbers only) — or use Invite Friends / accept a Steam invite."));
                return;
            }
            JoinViaSteam(new CSteamID(steamId64), "ui-join-steam");
        }

        /// <summary>Shared by the manual "Join via Steam" button and the pending-invite auto-join in
        /// <see cref="ApplySteamState"/> — same drafts-save/consume-pending/menu-close/link-timing sequence
        /// either way, only the trigger differs.</summary>
        private static void JoinViaSteam(CSteamID hostId, string reason)
        {
            Plugin.Cfg.LastSteamIdToJoin.Value = hostId.m_SteamID.ToString();
            SteamRichPresenceJoin.ConsumePendingInvite();
            _closeMenuOnJoinSuccess = true;
            CoopConnection.ApplySteamClient(hostId, reason);
            NetLinkState.SetClientLinked(true, reason);
            ApplyButtonStates();
            ApplyJoinFeedback();
        }

        /// <summary>Hard stop: tear the network down entirely (host closes the room / client leaves).</summary>
        private static void OnCloseRoom()
        {
            _closeMenuOnJoinSuccess = false;
            CoopConnection.Stop("ui-close-room"); // returns CoopConnection.CurrentMode to Off
            ApplyButtonStates();
        }

        // ----- Drafts / config --------------------------------------------------------------------------

        private static void LoadDraftsFromConfig()
        {
            _draftName    = Plugin.Cfg.PlayerName.Value;
            _draftAddress = Plugin.Cfg.HostAddress.Value;
            _draftPort    = Plugin.Cfg.HostPort.Value.ToString();
            _draftKey     = Plugin.Cfg.ConnectionKey.Value;
            _draftSteamId = Plugin.Cfg.LastSteamIdToJoin.Value;

            // UI-3d: auto-seed the name from Steam while it's still the generic default — SULFUR already knows the
            // persona name (it logs "playing as …" at startup). A name the player has personally set is left alone.
            if ((string.IsNullOrWhiteSpace(_draftName) || _draftName == "Player")
                && SteamIdentity.TryGetPersonaName(out var steamName))
                _draftName = steamName;
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
            Plugin.Cfg.LastSteamIdToJoin.Value = _draftSteamId ?? "";
        }

        /// <summary>Identity of the current draft field values, used to detect edits for auto-save.</summary>
        private static string DraftSig()
            => $"{_draftName}\0{_draftAddress}\0{_draftPort}\0{_draftKey}\0{_draftSteamId}";

        /// <summary>Auto-save: persist the drafts whenever they've changed since the last save. Called from the
        /// throttled Tick, so editing a field saves within ~0.4s with no explicit Save button and no per-keystroke
        /// disk churn. Validation/fallbacks live in SaveSettings (an unparsable port just leaves the old one).</summary>
        private static void AutoSaveDrafts()
        {
            string sig = DraftSig();
            if (sig == _lastSavedSig) return;
            _lastSavedSig = sig;
            SaveSettings();
        }

        // ----- Helpers ----------------------------------------------------------------------------------

        private static void ResetHandles()
        {
            _ctx                = null;
            _statusHandle       = null;
            _joinFeedbackHandle = null;
            _gateHintHandle     = null;
            _lanIpHandle        = null;
            _createHandle       = null;
            _joinHandle         = null;
            _playerListHandle   = null;
            _lastPlayerSig      = "\0";
            _steamUnavailableHandle   = null;
            _yourSteamIdHandle        = null;
            _steamInviteHandle        = null;
            _steamJoinHandle          = null;
            _steamPendingInviteHandle = null;
            _steamIdInputField        = null;
            _ffSessionHandle          = null;
            _ffToggleOption           = null;
            _ffLockApplied            = false;
            _devVoteStatusHandle      = null;
            _devVoteButton            = null;
        }

        // FF-1: persist the toggle (coop.json, via Setting<T>) and, when currently hosting, broadcast the new
        // session value immediately so connected clients' gates and hit proxies react without a rejoin.
        // FF-1b: a client can never commit through here — the row is locked, but if a click slips through anyway
        // (e.g. between the role change and the next Tick) the value is discarded and the mirror re-asserted, so
        // the host's session value can never silently overwrite the client's own saved preference.
        private static void OnFriendlyFireToggled(bool value)
        {
            if (CoopConnection.CurrentMode == NetMode.Client)
            {
                ApplySessionFriendlyFireControl();
                return;
            }
            bool changed = false;
            try
            {
                changed = Plugin.Cfg.FriendlyFire.Value != value;
                Plugin.Cfg.FriendlyFire.Value = value;
            }
            catch { }
            try
            {
                if (CoopConnection.CurrentMode == NetMode.Host)
                {
                    CoopConnection.Service?.BroadcastSessionSettings("ui-toggle");
                    // SS-Toast: the host sees its own change too (clients toast on receive, see
                    // NetService.HandleSessionSettings). Offline toggles notify nobody.
                    if (changed)
                        CoopToasts.NotifySessionSetting(CoopLoc.Get("session.friendlyFire.label", "Friendly fire"), value);
                }
            }
            catch (Exception e) { Plugin.Log?.Warn($"[CoopUi] friendly-fire broadcast failed: {e.Message}"); }
        }

        // VOTE-1: the local player proposes the dev-mode vote. Host starts it locally; a client asks the host, which
        // validates and starts. The proposer implicitly agrees (CoopVoteManager).
        private static void OnProposeDevModeVote()
            => CoopVoteManager.ProposeDevModeVote(Time.realtimeSinceStartup);

        // VOTE-1: refresh the "Session events" status line + propose-button enabled state each tick.
        private static void ApplyDevVoteControl()
        {
            if (_devVoteStatusHandle == null || _devVoteButton == null) return;
            var mode = CoopConnection.CurrentMode;
            string text;
            if (mode == NetMode.Off)
                text = CoopLoc.Get("connect.devVote.solo", "Developer mode follows your launch setting while solo.");
            else if (NetSessionSettings.DeveloperModeEnabled)
                text = CoopLoc.Get("connect.devVote.on", "Developer mode: On (this session).");
            else
            {
                var vote = CoopVoteManager.Current;
                if (vote != null && vote.HasVote && vote.Phase == VotePhase.Active && vote.Kind == VoteKind.EnableDevMode)
                    text = CoopLoc.Format("connect.devVote.active",
                        "Vote in progress — {agree}/{total} agreed, {secs}s left. Press Y / N in game.",
                        ("agree", vote.AgreeCount.ToString()),
                        ("total", vote.Total.ToString()),
                        ("secs", Mathf.CeilToInt(vote.SecondsRemaining).ToString()));
                else
                    text = CoopLoc.Get("connect.devVote.off", "Developer mode: Off — propose a vote to enable it for everyone.");
            }
            _devVoteStatusHandle.SetText(text);
            _devVoteButton.SetInteractable(CoopVoteManager.CanProposeDevModeVote());
        }

        // FF-1b: keep the FF row role-correct each tick. Client: locked (label + checkbox greyed, mouse/keyboard
        // blocked) and the checkbox mirrors the host's synced session value via SetIsOnWithoutNotify (no callback,
        // no save). Host/off: unlocked and the checkbox shows this machine's own saved setting (also restores it
        // after leaving a room where the mirror had moved it).
        private static void ApplySessionFriendlyFireControl()
        {
            bool isClient = CoopConnection.CurrentMode == NetMode.Client;

            if (_ffToggleOption != null)
            {
                var checkbox = FfCheckboxField?.GetValue(_ffToggleOption) as UnityEngine.UI.Toggle;
                if (isClient != _ffLockApplied)
                {
                    ApplyFfRowLock(isClient);
                    _ffLockApplied = isClient;
                }
                bool desired = isClient ? NetSessionSettings.FriendlyFireEnabled
                                        : ReadBool(() => Plugin.Cfg.FriendlyFire.Value, false);
                if (checkbox != null && checkbox.isOn != desired)
                    checkbox.SetIsOnWithoutNotify(desired);
            }

            // The read-only "session friendly fire" line — shown only while connected as a client.
            if (_ffSessionHandle == null) return;
            if (isClient)
            {
                _ffSessionHandle.SetText(CoopLoc.Format("connect.ffSession", "Session friendly fire: {state} (set by host)",
                    ("state", NetSessionSettings.FriendlyFireEnabled ? CoopLoc.Get("common.onUpper", "ON") : CoopLoc.Get("common.offUpper", "OFF"))));
                _ffSessionHandle.SetColor(NeutralColor);
                _ffSessionHandle.SetVisible(true);
            }
            else
            {
                _ffSessionHandle.SetVisible(false);
            }
        }

        // FF-1c: lock/unlock the FF row's visuals + input. The native SetLocked is unusable on a lib-built row:
        // its IL recolors the label with the serialized lockedTextColor/normalTextColor fields, which are never
        // initialized outside the game's own prefab — both default to (0,0,0,0), so locking made the label VANISH
        // (and a later unlock would have wiped it permanently). Instead a CanvasGroup on the row root dims the whole
        // row uniformly (label + checkbox) and blocks all pointer input (`interactable` gates every child Selectable
        // incl. the Toggle; `blocksRaycasts=false` makes the row fully inert); the private IsLocked field is still
        // set so the keyboard/controller `Use()` path early-returns. No text color is ever touched.
        private static void ApplyFfRowLock(bool locked)
        {
            if (_ffToggleOption == null) return;
            try { FfIsLockedField?.SetValue(_ffToggleOption, locked); } catch { }
            var go = _ffToggleOption.gameObject;
            var cg = go.GetComponent<CanvasGroup>();
            if (cg == null) cg = go.AddComponent<CanvasGroup>();
            cg.alpha = locked ? 0.45f : 1f;
            cg.interactable = !locked;
            cg.blocksRaycasts = !locked;
        }

        private static SulfurButtonHandle Handle(IReadOnlyList<SulfurButtonHandle> handles, int index)
            => handles != null && index >= 0 && index < handles.Count ? handles[index] : null;

        private static bool ReadBool(Func<bool> read, bool fallback)
        {
            try { return read(); } catch { return fallback; }
        }

        private static int ReadInt(Func<int> read, int fallback)
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
