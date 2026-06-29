# Co-op UI Plan

**Status:** design. Phase area code: **UI** (standalone, like PF / RM / LD). Lifecycle per
[Versioning.md](Versioning.md): `designed` → implement per sub-step.

The **UI track** has had no feature work yet. Today the mod's entire on-screen surface is two IMGUI calls:

- [`NetPlayerLifeManager.DrawCenterPrompt`](../src/Networking/Gameplay/NetPlayerLifeManager.cs) — downed /
  revive prompt, a bare `GUI.Box` with the default skin.
- [`Plugin.OnGUI`](../src/Plugin.cs) — just forwards to the above.

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

- Config: `NetworkMode` (Off/Host/Client), `HostAddress`, `HostPort`, `PlayerName`, `MaxPlayers`,
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

### UI-4 — Revive/downed HUD polish (medium, optional)

Replace the bare `GUI.Box` downed/revive prompt with a nicer surface. The downed banner ("waiting for
revive") is a natural `SulfurPopupApi.ShowBanner` use; the "hold [K] to revive {name} {progress}%" is a
live prompt better kept as the mod's own HUD (updates every frame, has a progress value). Low priority vs
UI-3; do after the connect page proves the lib integration end-to-end.

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
4. **UI-4 revive polish** — last, cosmetic.

Stop after each sub-step and verify in a real co-op session (per the phase-gate rule) before the next.
