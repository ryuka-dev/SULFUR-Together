# Localization — standing rule + string registry

**Status:** rule recorded; implementation **deferred** (user, 2026-06-28). No
localization infrastructure exists in this mod yet. This document exists so the
requirement is not forgotten and so every new player-facing string is tracked.

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
| 4 | title `Arena Lockdown` / msg `A teammate started the arena fight.` | `ArenaLockdownManager.cs` (LD-2c `Notify` toast, t0, via `ShowToast`) | Heads-up toast to out-of-room players when the lockdown starts. |
| 5 | title `Arena Lockdown` / msg `You've been sealed out — you'll be brought in shortly.` | `ArenaLockdownManager.cs` (LD-2c `Seal` toast, t0+5 s) | Explains the otherwise-invisible barrier. |
| 6 | title `Arena` / msg `Entering the arena.` | `ArenaLockdownManager.cs` (LD-2c teleport toast) | Fired on teleport-in (confirm / boss-death release). |

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
