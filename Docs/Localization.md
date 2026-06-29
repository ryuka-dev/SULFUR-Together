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
| 2 | `DOWNED\nWaiting for a teammate to revive you` | `NetPlayerLifeManager.DrawCenterPrompt` | IMGUI center prompt while locally downed. |
| 3 | `Hold [{key}] to revive {name}\n{dist}m  {pct}%` | `NetPlayerLifeManager.DrawCenterPrompt` | IMGUI revive prompt; interpolates key/name/distance/progress. |
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
