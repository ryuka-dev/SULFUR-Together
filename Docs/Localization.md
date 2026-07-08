# Localization — standing rule + string registry

**Status:** rule recorded; implementation **deferred**. No localization
infrastructure exists in this mod yet. This document tracks the rule and every
player-facing string so the later localization pass has a complete list.

---

## The rule

> **Every piece of text shown to the player must be localized.**

"Shown to the player" = anything rendered on screen for the player to read: HUD
banners, toasts, IMGUI prompts, any in-world/menu UI text this mod produces.

This is a **hard requirement** for any user-facing string. Until the localization
layer is built, new player-facing text is written as an English placeholder **and
registered in the table below** so the later localization pass has a complete list.

### What is NOT player-facing (stays English, not localized)

- **Logs** — `NetLogger.*`, `Plugin.Log.*`, every `[Tag] …` diagnostic line. These
  are developer-facing only.
- **Config entry descriptions** — BepInEx `cfg.Bind(..., "description")` text is for
  the config file / config editor; treated as dev/maintainer-facing English by
  convention (see [`ConfigAndLoggingConventions.md`](ConfigAndLoggingConventions.md)).
- **Code comments / XML docs.**

If in doubt: does the end player read it on screen during play? → localize it.

---

## Producer note (SULFUR Native UI Lib)

The UI lib (`ryuka.sulfur.nativeui`) renders our banner + toasts with a
**language-correct font** (it samples a live localized game `TextMeshProUGUI`, so
CJK/Cyrillic render instead of blank boxes). **But the STRINGS are ours.** The lib
takes whatever text we pass; supplying the *localized* string is this mod's job.
The lib also has its own `Localization/` for its own option labels — that does not
cover our strings.

---

## Registry of player-facing strings (all currently hardcoded English → TODO localize)

| # | String (current English) | Source | Notes |
|---|---|---|---|
| 1 | `Press [{key}] to enter the arena` | `ArenaLockdownManager.cs` (LD-2c popup banner, via `ShowPrompt`) | `{key}` = `ArenaEnterConfirmKey`. Shown to the out-of-room player at t0+10 s. |
| 2 | `Waiting for a teammate to revive you` | `DownedRescueOverlayManager` (DR-2) | Downed player's idle prompt — shown before anyone has started rescuing them. Replaces the old IMGUI `DOWNED\n...` text. |
| 3 | `{name} is rescuing you` / `Hang on` | `DownedRescueOverlayManager` (DR-2) | Downed player's prompt (main/sub text) while a teammate is actively rescuing them; the shown progress comes from the host-authoritative rescue state (DR-1), never timed locally. Replaces the old IMGUI revive prompt. |
| 4 | title `Arena Lockdown` / msg `A teammate entered the arena — head in now to join them!` | `ArenaLockdownManager.cs` (LD-2c `Notify` toast, t0, via `ShowToast`) | Heads-up at t0; LD-2d grace keeps the door open ~5 s, so it invites the player to run in. |
| 5 | title `Arena Lockdown` / msg `You've been sealed out — you'll be brought in shortly.` | `ArenaLockdownManager.cs` (LD-2c `Seal` toast, t0+5 s) | Explains the otherwise-invisible barrier. |
| 6 | title `Arena` / msg `Entering the arena.` | `ArenaLockdownManager.cs` (LD-2c teleport toast) | Fired on teleport-in (confirm / boss-death release). |
| 7 | title `Arena Lockdown` / msg `You entered the arena — the gate seals in a few seconds; teammates can still run in.` | `ArenaLockdownManager.cs` (LD-2e `NotifyEntered` toast, t0) | Heads-up to the player(s) who entered first. |
| 8 | title `Together` (default toast heading) | `CoopToasts.cs` (UI-1) | Heading used for all co-op event toasts that don't pass an explicit title. |
| 9 | `{name} joined` | `NetService.HandleHandshakeRequest` (UI-1, host) | `{name}` = the joining player's display name. |
| 10 | `Connected to {host}` | `NetService.HandleHandshakeAccepted` (UI-1, client) | `{host}` = host display name. |
| 11 | `{name} left` / `Disconnected from host` | `NetService.OnPeerDisconnected` (UI-1) | First form host-side (a client left); second client-side (the host dropped). |
| 12 | `Linked to host` / `Playing solo` | `NetLinkState.SetClientLinked` (UI-1) | Client link toggled on/off (PageDown / PageUp). |
| 13 | `Hosting ON` / `Hosting OFF` | `NetLinkState.SetHostLinked` (UI-1) | Host multiplayer master switch toggled. |
| 14 | Connect page (UI-3b): section `SULFUR Together`; labels `Role` / `Player name` / `Host address` / `Port` / `Max players` / `Connection key`; role values `Host` / `Client`; buttons `Save settings` / `Connect` / `Disconnect`; status `Settings saved.`; descriptions `Set up your co-op session, then Save to keep the settings or Connect to join the room. Editing a field on its own changes nothing until you press a button.` and `Host (other players join you) or Client (join a host).` | `CoopConnectPage.cs` | Native Options-screen page. Save persists settings only; Connect = handshake + enter room; Disconnect leaves + closes the socket. |
| 15 | `Hosting on port {port} — {n} player(s) connected` / `Connected to host {addr}:{port}` / `Connecting to {addr}:{port}…` / `Off` | `NetService.GetConnectionSummary` (UI-3) | Connect-page status line. |
| 16 | Connect page (UI-3c rework) — sections `SULFUR Together` / `Player` / `Connection` / `Players in session` / `Local preferences (only affect you)` / `Session settings (host)` / `About`; labels `Player name` / `Host address (IP)` / `Port` / `Connection key` / `Show player join/leave notifications` (desc `Brief top-right toasts when a player joins or leaves.`) / `Show network status on HUD` / `Show other players' names` / `Rescue key` / `Confirm-enter-boss-room key` / `Loot mode` / `Client may start next level` / `Friendly fire` / `Version`; buttons `Create game` / `Join game` / `Close room` (host) ↔ `Leave` (client) / `Open-source repo` / `Support on Ko-fi`; descriptions `Early-preview co-op. Set up your connection below.` and `Host a co-op session or join one. Your settings save automatically as you edit them. Close room (host) / Leave (client) ends the session for you.`; deferred-placeholder values `Coming soon` / `Independent (Shared coming soon)`; status `● Not connected` / `◌ Connecting…` / `No players connected.` / `Load a save first — co-op is hosted / joined from inside the game.`; player row `{name} (you) — slot {n} — {state}` | `CoopConnectPage.cs` (UI-3c, auto-save UI-3e) | Reworked native Options page on the 0.10 live handles. Supersedes the UI-3b strings (row 14): Role dropdown → Create/Join buttons, Max-players row dropped, live player list + join-failure line added. The end-session button label is host/client-aware (`Close room` / `Leave`). UI-3e dropped the footer `Save settings` button + `Settings saved.` / `Load a save first.` statuses — settings auto-save as you type. |
| 17 | `Host rejected: {reason}` / `Connection failed — host unreachable. Check the address, port and that the host is up.` / `Could not start networking ({type}). The port may be in use or LiteNetLib is missing.` / `⚠ {reason}` | `NetConnectFeedback` (set by `NetService` / `CoopConnection`, UI-3c) | Client join-failure reason surfaced in the connect page. `{reason}` = host's `RejectPeer` string; the `⚠ ` prefix is added by the page. |
| 18 | `Your LAN address: {ip}:{port}  (others on your network join with this)` | `CoopConnectPage.ApplyHostLanIp` (UI-3d) | Shown only while hosting; `{ip}` from `NetLocalAddress`, `{port}` from config. The Steam-name auto-seed (UI-3d) inserts the player's own persona name into the existing "Player name" field — player data, not a translatable literal, so no row of its own. |
| 19 | title `Sandstorm Arena` / msg `Pulled into the arena — the sandstorm outside would grind you down.` | `ArenaLockdownManager.cs` (LD-Sandstorm `PullIn` toast) | Desert boss: fired when an out-of-arena player is teleported to the arena centre ~3 s after the dialog trigger (gate-less arena; the sandstorm ring is the wall). |

| 20 | Run Stats card (RS-2): 7 stat row labels `Shots Fired` / `Damage Dealt` / `Kills` / `Times Downed` / `Rescues` / `Damage Taken` / `Destructibles Destroyed`; name-row suffix `{name} (You)` for the local player's own card; placeholder `…` shown before the finalized broadcast has arrived | `RunStatsCardView.cs` | End-of-Run card overlay shown over the Hub-return loading screen. Player names themselves are user data, not translatable literals. |
| 21 | `Friendly fire` toggle (desc `Players can damage each other. The host's setting applies to the whole session.`) / `Session friendly fire: ON (set by host)` / `Session friendly fire: OFF (set by host)` | `CoopConnectPage.cs` (FF-1) | Replaces the row-16 `Friendly fire` + `Coming soon` placeholder with a live toggle; the session line is read-only and shown only while connected as a client. |
| 22 | `{label}: On` / `{label}: Off` session-setting change toast; current `{label}` value: `Friendly fire` | `CoopToasts.NotifySessionSetting` (SS-Toast) | Fired on host + every client when the host changes a session setting mid-session (join-time sync is silent). Every future session setting reuses this formatter with its own label. |
| 23 | `Rescuing {name}` / `Hold [{key}]` | `DownedRescueOverlayManager` (DR-2) | Rescuer's prompt (main/sub text) while actively holding the revive key near a downed teammate. |
| 24 | `Rescue {name}` / `Hold [{key}]` | `DownedRescueOverlayManager` (DR-2) | Rescuer's idle hint — shown when near a downed teammate but not yet holding the key (progress 0, a local-only proximity check, not network state). |
| 25 | `Rescue complete` / `Restored` | `DownedRescueOverlayManager` (DR-5) | Brief completion text — first form shown to the rescuer, second to the just-revived player — held ~0.6s before the panel fades out. Never shown on a cancelled rescue (that just fades, no text change). |

### Planned, not yet written (register here when added)

- _(none at present)_

---

## When localization is implemented (sketch, not a commitment)

Likely mirror the UI lib's documented layout — per-language JSON under the plugin's
`lang/` folder (`en.json`, `ja.json`, `zh-CN.json`; see the lib README "Recommended
file layout") — with a small `key → string` lookup and `{placeholder}` interpolation,
selected by the game's current language. Replace each hardcoded string above with a
lookup by key. Keep log/config text out of it.

**Until then:** do not add new on-screen text without adding a row to the registry
above.
