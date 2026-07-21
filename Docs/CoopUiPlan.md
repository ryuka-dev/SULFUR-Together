# Co-op UI Plan

**Status:** in progress. Phase area code: **UI** (standalone, like PF / RM / LD). Lifecycle per
[Versioning.md](Versioning.md): `designed` → implement per sub-step. UI-1 committed; UI-3a built;
UI-3b was the interim page (Save/Connect/Disconnect). The **§8 live-update handles shipped in the UI lib
0.10** (`SulfurTextHandle` / `SulfurButtonHandle` / `SulfurListHandle`) and were verified by the lib's
0.10 comprehensive test (host Log182.5 — 18/18 PASS). **UI-3c** then reworked the page onto those handles
to the §5 target layout (no-unknowns body — Steam-name auto-seed and host LAN-IP display still deferred);
built + deployed, pending in-game verify. See the registry in [Versioning.md](Versioning.md) for live status.

> §1–4 below are the original incremental breakdown (history). The **canonical target** for the connect
> page is **§5**; §6 records the settled decisions, §7 the deferred backlog, §8 the UI-lib prerequisites
> (the next thing to build — handles the page leans on).

The downed/revive prompt used to be the mod's last raw IMGUI surface (`NetPlayerLifeManager.DrawCenterPrompt`,
a bare `GUI.Box`) — **replaced by DR-1..DR-5** (see UI-4 below): a host-authoritative rescue-progress protocol
plus a real uGUI overlay (`src/UI/DownedRescueOverlay/`) styled after the RunStatsOverlay precedent. `Plugin.OnGUI`
itself has been removed; there is no more IMGUI in this mod.

Everything else (host/join, link on/off, who's connected) is **config-file toggles + hidden hotkeys**
(`PageDown` link / `PageUp` unlink / host toggle key). For a public release this is unusable: a player must
edit a `.cfg` and restart the game to even pick Host vs Client.

---

## 1. What already exists (so we don't rebuild it)

### Producer side — `SULFUR Native UI Lib` (`ryuka.sulfur.nativeui`) is **shipped and ready**

The handoff doc [NativeUiConfirmPopupHandoff.md](NativeUiConfirmPopupHandoff.md) is **stale** — it says
"spec only, no code". In fact the lib now ships (its `CHANGELOG`):

| API | Surface | Signature |
|-----|---------|-----------|
| `SulfurOptionsApi.RegisterPage(SulfurOptionsPage)` | Native **options screen page** (left-side category + native setting rows: toggles, dropdowns, text input, buttons, foldouts) | `0.7.x` |
| `SulfurPopupApi.ShowBanner(text)` / `HideBanner()` | Persistent **center HUD banner** (display-only, no pause/input steal) | `0.8.0` |
| `SulfurToastApi.Show(title, msg, durationSeconds)` | **Top-right transient toasts**, stack, fire-and-forget | `0.9.0` |

All three are dependency-free static entry points in namespace `Ryuka.Sulfur.NativeUI`.

### Consumer side — this mod already has the seam

- `Plugin.cs` declares `[BepInDependency("ryuka.sulfur.nativeui", SoftDependency)]` and
  `WireArenaLockdownPopup()` resolves `SulfurPopupApi` / `SulfurToastApi` **by reflection** (so we never
  hard-link the assembly) and assigns `ArenaLockdownManager.ShowPrompt/HidePrompt/ShowToast`.
- **This reflection-wiring pattern is the template for all UI work below.** Absent lib → seam stays null →
  feature degrades to log-only. No hard dependency, no load-order risk.

### Connection state we can surface

- Config: `NetworkMode` (Off/Host/Client), `HostAddress`, `HostPort`, `PlayerName`,
  `ConnectionKey` (all `[Network]` in `CoopConfig.cs`).
- `NetLinkState` — client linked (PageDown) / host on (toggle). Single source of truth, has
  `FormatStatus()`.
- `NetService.OnPeerConnected` / `OnPeerDisconnected` + `NetSessionManager` (peer list, names, slots) →
  feed join/leave events.
- **Gap:** `NetService.Start(mode)` runs once in `Awake` (only when `EnableNetworking && NetworkMode!=Off`).
  There is a `Stop()`. There is **no runtime start/restart** — that is the one real engineering item.

---

## 2. Deliverables (prioritized)

### UI-1 — Session toasts (smallest, lib 100% ready, no new engineering)

Wire `SulfurToastApi.Show` (reflection seam, same as ArenaLockdown) to existing events:

- peer connected → `Show("Co-op", "{name} joined")`
- peer disconnected → `Show("Co-op", "{name} left")`
- client link on/off (`NetLinkState.SetClientLinked`) → `"Linked to host" / "Playing solo"`
- host link toggle → `"Co-op hosting ON/OFF"`
- (later) "X is joining…" at handshake-accept, before they're in-scene.

Implementation: a small `CoopToast` helper holding the resolved `Action<string,string>` seam (or reuse
`ArenaLockdownManager.ShowToast`'s resolution, promoted to a shared `CoopUi` wiring class). Hook points are
already there. Delivers the long-planned non-intrusive in-game co-op notifications. Config
`EnableCoopToasts` (default on). **No protocol change, no NetService change.**

### UI-2 — Link-state HUD indicator (small)

A tiny always-visible corner indicator of co-op state (e.g. "CO-OP ▸ linked / solo / hosting (n peers)").
Two options:
- **a.** Persistent `SulfurPopupApi.ShowBanner` — but banner is single-instance + center-screen, wrong
  shape for a status corner; would fight the ArenaLockdown prompt. **Reject.**
- **b.** Keep it in the mod's own IMGUI (`OnGUI`) as a small unobtrusive label, OR (better) ask the UI lib
  for a future persistent-status-chip API. For now: **mod-side IMGUI corner label**, cheap, no lib need.

Config `ShowCoopStatusIndicator` (default on). Drawn next to the existing downed-prompt OnGUI.

### UI-3 — Connect / lobby page (the big one, the 🔴 release blocker)

Register a native options page via `SulfurOptionsApi.RegisterPage` — **"SULFUR Together"** category in the
game's own options screen. Rows (native-styled, via the lib's row API):

- **Mode**: dropdown Off / Host / Client.
- **Host address** + **Port**: text rows (client mode).
- **Player name**, **Max players**, **Connection key**: text/number rows.
- **Connect / Start Host** button → applies rows to config → starts networking.
- **Disconnect** button → `Stop()` + state reset.
- **Status** (read-only row): `Off / Hosting :9050 (2/4) / Connecting… / Connected to 1.2.3.4`.
- **Peer list** (read-only rows): name + slot + ping per connected peer.

**Engineering prerequisite — runtime start/stop in `NetService`:** today `Start(mode)` is called once and
the listener/managers are built inline in `Awake`. Need to:
1. Make `Start(mode)` callable at runtime (it mostly is — it builds `_listener`/`_net` and resets managers).
2. Make `Stop()` fully tear down (`_net.Stop()`, dispose listener, reset `NetLinkState`, clear sessions,
   `NetClientLoadGate.Reset()`, drop remote-player proxies) so a subsequent `Start` is clean.
3. A `Restart(mode)` = Stop + Start for switching Host↔Client without a game restart.
4. Decouple "should networking run" from the `Awake`-time `EnableNetworking && NetworkMode!=Off` check —
   the page's Connect button becomes the trigger; config just seeds defaults.

This is the bulk of the work and the only part touching netcode. Everything else is presentation.

### UI-4 — Revive/downed HUD polish (done — Phase DR)

**Done.** The bare `GUI.Box` downed/revive prompt is replaced by Phase DR: a host-authoritative rescue-progress
protocol (DR-1, reusing the `PlayerLifeState` message/DTO — see `NetPlayerLifeState.cs`/`NetPlayerLifeManager.cs`)
feeding a real uGUI overlay (`src/UI/DownedRescueOverlay/`, DR-2 through DR-5) built on the RunStatsOverlay
pattern: a perimeter-tracing border-progress stroke (DR-3, `DownedRescueBorderProgress`), a continuous
"vitality" animation from near-death to alive (DR-4, `DownedRescueVitalityAnimator`, reusing `RunStatsSpring`),
and a brief completion-text hold + fade (DR-5; a cancelled rescue just fades, no text). Both the rescuer's and
the downed player's views read the same host-authoritative progress value — never independently timed locally.
See `phase-dr-downed-rescue-ui` memory for the design/implementation trail.

---

## 3. Dependency & risk notes

- **Soft dependency only.** Every lib call goes through a reflection seam resolved once at init (the
  `WireArenaLockdownPopup` pattern, generalized into a `CoopUi` wiring class). Lib absent → toasts/page
  degrade to log-only; the mod still runs. No hard assembly link, no load-order coupling.
- **UI lib version floor:** UI-1 needs `0.9.0` (toasts), UI-3 needs the OptionsApi row set (`0.7.x`).
  Record the floor in the wiring log line.
- **No-pause invariant:** all surfaces must stay passive (no `Time.timeScale`, no input capture, no cursor
  lock) per [MultiplayerPauseAudit.md](MultiplayerPauseAudit.md). The lib's banner/toast already honor this;
  the options page only runs while the options screen is open (already paused by the game), so it's exempt.
- **Localization:** all new player-visible strings are English placeholders registered in
  [Localization.md](Localization.md) until the localization layer lands (project rule). The UI lib owns
  font/locale for its own surfaces.

---

## 4. Suggested order

1. **UI-1 toasts** — immediate win, zero risk, delivers the in-game co-op notifications. (~½ day)
2. **UI-3 connect page** — the release blocker; do the `NetService` runtime start/stop first, then the page.
   (the real project) Split: **UI-3a** netservice runtime restart, **UI-3b** the options page on top.
3. **UI-2 status indicator** — folds in cheaply once link state is user-driven.
4. **UI-4 revive polish** — done (Phase DR), out of order relative to UI-3/UI-2 (user-requested ahead of schedule).

Stop after each sub-step and verify in a real co-op session (per the phase-gate rule) before the next.

---

## 5. Target connect-page design (canonical)

One native Options page, `SulfurOptionsApi.RegisterPage`, category **"SULFUR Together"**. Top-to-bottom:

### Current status
- A status line/indicator: `● Not connected` etc. **Host and client show different text** (host: hosting +
  N players; client: connected to / not connected). Needs a **live text handle** (§8) so it updates without
  reopening the page.

### Player info
- **Player name** — text input. Seeded **once** on first run from the player's Steam name (fallback `Player`
  if the grab fails); editable afterward and never auto-overwritten again. Store a `NameInitialized` flag in
  config so the seed happens only once. (Feasibility: Steam name via Steamworks `SteamFriends.GetPersonaName()`
  — to verify the game exposes Steamworks.)

### Start co-op
- **IP** — text input. After the player presses **Create game**, this row turns into a read-only display
  `Your LAN address: x.x.x.x` (the host's own local IPv4, enumerated from the network interfaces). On a
  client it stays an input box (the address to join).
- **Port** — text input.
- **Connection key** — text input (visible, not masked).
- **[ Create game ]** — host. Mutually exclusive with Join: while a room exists, **Join is disabled**.
- **[ Join game ]** — client. **On failure, surface the reason in the menu**, not just the log: e.g.
  `Mod version mismatch — host 0.8.1, you 0.8.0`, or `Connection timed out`. The handshake already computes
  these reasons (`RejectPeer` strings; LiteNetLib `ConnectionFailed`); the client must capture the last
  failure and display it (needs a live text/error element, §8). Create disabled while joined.

### Steam connect method (STEAM-1..4 — implemented; a second, additive way to connect)
A second, additive section below "Start co-op" — Direct IP above is untouched and still works exactly as
before. See `Docs/NetworkingArchitecture.md` "Connection methods" for the transport design (a loopback relay
under the same LiteNetLib socket, not a rewrite).
- **Availability line** — shown only when Steam isn't up ("Steam is not available — connect method disabled");
  hidden otherwise.
- **Your Steam ID** — read-only, shown whenever Steam is available (host or client), so it can be shared/pasted
  the same way the LAN IP row already works for Direct IP.
- **[ Invite Friends via Steam ]** — host only, enabled once hosting. A deliberate opt-in (not automatic on
  Create) — opens the host to Steam P2P joins *in addition to* Direct IP and pops the Steam overlay invite
  dialog in the same click. Label switches to "Invite more friends via Steam" once already enabled, and the
  overlay dialog re-opens on every click (not just the first) so a host can invite a second/third friend, or
  re-invite one who missed the popup, without closing and recreating the room.
- **Steam ID to join** — text input (persisted, like Host address/Port/Key) + **[ Join via Steam ]** — client.
  Same mutual-exclusion rule as Join game (disabled once connected). Errors surface through the same
  `NetConnectFeedback` line Direct IP uses.
- **Pending-invite banner + auto-join** — accepting a friend's Steam invite (their friends list "Join Game" or
  the overlay) joins automatically, matching ordinary Steam multiplayer UX — no extra click needed. The banner
  shows "Joining `<friend>`'s SULFUR Together game…" as feedback; if no save is loaded yet it instead reads
  "load a save to join automatically" and fires the join itself the moment one is. Works even if the connect
  page isn't open when the invite is accepted (the background tick that drives this runs regardless of page
  visibility). Only auto-joins while free to (not already hosting/connected) — mid-session, an old accepted
  invite stays latched but doesn't interrupt the current game.

### Local preferences (per-player, client-editable for itself)
- **Show player join/leave notifications** `[on]` → `EnableCoopToasts`. (The earlier "co-op notifications"
  label was a duplicate of this — collapsed into this single toggle.)
- **Show network status on HUD** `[on]` → future UI-2 HUD indicator.
- **Show other players' names** `[on]` → future.
- **Rescue key** `[E]` → `PlayerReviveHoldKey`.
- **Confirm-enter-boss-room key** `[Enter]` → `ArenaEnterConfirmKey`.

### Session settings (host-authoritative; client sees them read-only, synced)

**Non-host rule (FF-1b):** while connected as a **client**, every control in this section must be **visually
non-operable** — the native row lock (`OptionsScreenOption.SetLocked`: greyed label + blocked keyboard/controller
`Use()`) **plus** the underlying Unity control made non-interactable (a mouse click lands directly on the `Toggle`
graphic and bypasses `SetLocked` — verified in the game IL). A locked control **mirrors the host's synced session
value live** (updates via `SetIsOnWithoutNotify`, never firing the change callback, so the client's own saved
preference is untouched). Any future live session-settings row (loot mode, client-may-start-next-level, …) must
follow this same pattern. Off/host = editable: outside a session the toggle prepares your own future room.

**Change-notification rule (SS-Toast):** whenever the host changes **any** session setting during a live session,
**every player is told via a toast** — the host at its commit point (the settings-row callback, after the
broadcast), each client where the received snapshot is applied (`NetService.HandleSessionSettings`, gated by
`NetSessionSettings.ApplyReceived` returning true only for a live change). Both ends share one formatter,
`CoopToasts.NotifySessionSetting(label, enabled)`, so the text exists once. The **join-time initial sync is
silent** (the client's first `ApplyReceived` never toasts — the locked row already shows the value), and toggling
a setting **outside a session notifies nobody**. This rule applies to every future session-settings row.

`NotifySessionSetting` deliberately **bypasses the local `EnableCoopToasts` preference** (the personal "show
player join/leave notifications" toggle): a host changing a session rule (e.g. friendly fire) affects gameplay and
must reach every player regardless of their join/leave-notification choice — otherwise a client with join/leave
toasts off silently never learns FF was turned on. Only the join/leave/link/connect toasts stay gated by that
preference.

**Checklist for adding a new session setting** (each step has an FF-1 reference implementation):
1. Field on `NetSessionSettingsState` + read/write in `NetSessionSettingsCodec`.
2. Host authority: config value read live on the host; broadcast on change (`BroadcastSessionSettings`) and once
   per handshake (`SendSessionSettingsToPeer`).
3. Client mirror in `NetSessionSettings` with per-field change detection in `ApplyReceived` (feeds the SS-Toast
   return value) and a reset in `ResetSession`.
4. Connect-page row locked + live-mirrored on clients (non-host rule above).
5. SS-Toast on both ends via `CoopToasts.NotifySessionSetting` (host commit point + the `ApplyReceived` signal).
6. Register the player-facing strings in `Docs/Localization.md`; ledger row in `Docs/Versioning.md` §4.
- **Player table** — who is in the room, **ping per player**, and a **kick button** per row (host only).
  Needs a refreshable list (§8).
- **Loot mode** `[ Independent ▼ ]` — Independent works today; **Shared is deferred** (§7).
- **Client may initiate the next level** — toggle (deferred gating, §7).
- **Friendly fire** — **implemented (FF-1)**: a live toggle (default OFF) backed by a real player-vs-player damage
  path in both directions. The host's value is the session truth, broadcast via the new `SessionSettings` message
  (on toggle + per handshake); on a client the toggle itself is locked and mirrors the host's live value (FF-1b,
  per the non-host rule above), with a read-only line under it spelling out the ownership (its own saved
  preference — the default for rooms it hosts later — is never touched by the mirroring). Implementing it also removed the unintended
  always-on host→client player damage (a side effect of the enemy-damage-authority forwarding).
- Showing the remaining rows on a client read-only requires extending the session-settings broadcast (§7) —
  the message exists now (FF-1), carrying just the friendly-fire flag.

### Room control
- **[ Close co-op world ]** — soft: the host stops sharing the world / leading, **socket stays up, players
  stay connected** — the host-side equivalent of the link toggle. Maps to `NetLinkState.SetHostLinked(false)`
  (mechanism already exists).
- **[ Close room ]** — hard: tear down the network/handshake entirely (everyone disconnects). Maps to
  `CoopConnection.Stop` (already exists).

### About
- Mod version, **[ Open-source repo ]** and **[ Ko-fi ]** buttons → `Application.OpenURL` (no lib change).

### Player cap
- **No artificial cap.** This is a private virtual-LAN game (direct IP:port UDP over Radmin/ZeroTier/etc.),
  not a public server. The real ceiling is performance (per-client enemy-snapshot bandwidth/CPU scales with
  player count; ~4–8 is the practical limit), not a hard-coded number. `MaxPlayers` becomes advisory/removed.
  **Done (MP-Cap):** the setting and both gates are gone — see `Docs/Versioning.md` §4.

---

## 6. Settled decisions

- **Notification toggle** — one toggle only: "Show player join/leave notifications" (`EnableCoopToasts`).
  The separate "co-op notifications" label was redundant and is dropped.
- **Two-level shutdown** — *Close co-op world* = `NetLinkState.SetHostLinked(false)` (soft, keep socket);
  *Close room* = `CoopConnection.Stop` (hard, drop socket). Both mechanisms already exist.
- **Deferred features are not built now** — they get a paper trail here (§7) and the UI shows them as
  placeholders (greyed / "coming soon"); the UI must not pretend a feature exists when its netcode doesn't.
- **Build order** — the **UI lib live-update handles (§8) come first**; the target page (§5) is reworked on
  top of them. The interim Save/Connect/Disconnect page stands until then.

---

## 7. Deferred features (target design references them; netcode not built)

Tracked here so the design isn't lost; **do not implement until scheduled**.

- **Shared loot mode** — only Independent loot exists (Phase 6 is partial). The loot-mode dropdown ships with
  Independent live and Shared as a disabled "coming soon" option until shared-loot sync lands.
- ~~**Friendly fire toggle**~~ — **done (FF-1)**, see §5: full player-vs-player damage path + host-authoritative
  toggle + the first session-settings broadcast.
- **Kick player** — host disconnects a chosen peer (+ a "kicked" reason to that client). New, small.
- **Client may initiate the next level** — a host-authoritative gate on client-initiated transitions
  (the relay exists; the permission switch does not).
- **Host→client session-settings sync** — the `SessionSettings` broadcast shipped with FF-1 carrying the
  friendly-fire flag; extending it to loot mode / client-transition permission remains.
- **HUD network-status indicator** + **show other players' names** — the UI-2 family, not yet built.

---

## 8. UI-lib prerequisites (the next thing to build — handoff to the UI lib)

The target page (§5) leans on **live updates**: status text, the join-failure reason, the host's LAN IP,
the player/ping table, and the read-only synced host settings all change while the page is open. The lib
today can only update the **footer status** (`SetFooterStatus`) without a full `ctx.Rebuild()`. The lib
needs **update handles**, mirroring the existing `SulfurSettingHandle` for setting rows:

| Capability | Used by | Lib status |
|------------|---------|------------|
| **Text handle** — set text without rebuild | status line, join-failure reason, host LAN IP, read-only synced host settings | **shipped 0.10** (`SulfurTextHandle`: `SetText` / `SetColor` / `SetVisible`) |
| **Button handle** — set label / enabled | Create↔Join mutual-exclusion disable, room-control button states | **shipped 0.10** (`SulfurButtonHandle`: `SetLabel` / `SetInteractable` / `SetVisible`; `AddButtonRow` returns the handles) |
| **Refreshable list/table** — per-row dynamic content | player table (name + ping + kick button), ping ticking live | **shipped 0.10** (`SulfurListHandle`: `AddList` → `Update(buildDelegate)` / `Clear` / `SetVisible`) |
| **Read-only / disabled control state** — show a value, not editable | client viewing host's session settings | `AddReadonlyText` (value display); button handles also expose `SetInteractable(false)` |
| URL button | About (repo / Ko-fi) | already covered by `AddSmallButton` + `Application.OpenURL` |

Core ask (**delivered in lib 0.10**): handles for text, buttons, and a refreshable list — set text / enabled /
visible without rebuilding the whole page. UI-3c consumes them: the status line, Create/Join enabled state,
join-failure line and player list all update through these handles, driven from `CoopConnectPage.Tick`.
