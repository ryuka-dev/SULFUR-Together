# Localization — standing rule + string registry

**Status:** rule recorded; layer **implemented**. Every player-facing string in the
registry below is now looked up by key from per-language `lang/*.json`, with the
English text kept in-code as the fallback. This document tracks the rule, the
mechanism, and the key→string mapping for every on-screen string.

---

## Mechanism (implemented)

The mod does **not** ship its own localization engine — it reuses the one already in
**SULFUR Native UI Lib** (`ryuka.sulfur.nativeui`), a *soft* dependency by the same
author. That lib wraps the game's own **I2 Localization** and follows the game's
current language automatically.

- **Boundary:** all access goes through one project-owned static, `CoopLoc`
  (`src/UI/CoopLoc.cs`). It resolves `SulfurLocalization` by reflection (`AccessTools`)
  so the lib type never leaks into gameplay code and the mod degrades to English when
  the lib is absent (CLAUDE.md §2). It is wired once from `Plugin.WireCoopUi` via
  `CoopLoc.Wire(Info.Location)`.
- **Lookup:** `CoopLoc.Get(key, englishFallback)` → `SulfurLocalization.Get(GUID, key,
  fallback)`. The lib's fallback chain is `current-code → base-code → "en" → the
  passed fallback`, so a missing key or missing file always resolves to English.
- **Interpolation:** `CoopLoc.Format(key, englishFallback, (token, value)…)` does
  `{token}` → value replacement after lookup. Tokens (`{name}`, `{key}`, `{host}`,
  `{port}`, `{ip}`, `{label}`, `{state}`, `{reason}`, `{type}`, `{n}`, `{id}`,
  `{steam}`) are **identical across every language file**.
- **Files:** `lang/en.json` (canonical, mirrors the in-code fallbacks) plus one file
  per language, schema `{ "entries": [ { "key", "value" }, … ] }`, UTF-8. Loaded by
  `SulfurLocalization.LoadPluginLocalization(GUID, pluginLocation)` from a `lang/`
  folder beside the plugin DLL; the csproj `Deploy` target copies `lang/*.json` into
  each profile's `SULFUR Together\lang`.
- **Language switching:** the connect page tracks `CoopLoc.LanguageVersion` and
  re-applies its static labels when it changes; toasts/overlays fetch their strings
  per-show, so they follow the language with no extra work.
- **Fonts:** handled entirely upstream — lib rows/toasts/banner sample the game's
  current-language `TextMeshProUGUI`, and our two uGUI overlays sample the live native
  font (`NativeFontSampler`). The game ships font groups for its interface languages,
  so CJK/Cyrillic render instead of tofu with no font work in this mod.

### Per-language review status

`en` is canonical. `zh-CN` and `ja` are hand-translated. The remaining languages
(`ko`, `ru`, `pl`, `tr`, `fr`, `de`, `es`, `it`, `pt`, `sv`, `ar`) are
machine-translated and **want a native-speaker review** — text only; keys and tokens
are fixed. **Arabic (`ar`) caveat:** `ar` is not one of SULFUR's shipped interface
font groups, so unless the game itself selects Arabic the file is inert; if selected,
RTL/glyph shaping depends on the game having an Arabic font loaded. Provided per
request.

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

All strings below are looked up by the listed **key(s)** from `lang/*.json`; the
"String (English)" column is the canonical `en.json` value and the in-code fallback.

| # | String (English) | Key(s) | Source | Notes |
|---|---|---|---|---|
| 1 | `Press [{key}] to enter the arena` | `arena.enterPrompt` | `ArenaLockdownManager.cs` (LD-2c popup banner, via `ShowPrompt`) | `{key}` = `ArenaEnterConfirmKey`. Shown to the out-of-room player at t0+10 s. |
| 2 | `Waiting for a teammate to revive you` | `rescue.downed.waiting` | `DownedRescueOverlayManager` (DR-2) | Downed player's idle prompt — shown before anyone has started rescuing them. Replaces the old IMGUI `DOWNED\n...` text. |
| 3 | `{name} is rescuing you` / `Hang on` | `rescue.downed.active`, `rescue.downed.hangOn` | `DownedRescueOverlayManager` (DR-2) | Downed player's prompt (main/sub text) while a teammate is actively rescuing them; the shown progress comes from the host-authoritative rescue state (DR-1), never timed locally. Replaces the old IMGUI revive prompt. |
| 4 | title `Arena Lockdown` / msg `A teammate entered the arena — head in now to join them!` | `arena.toast.title`, `arena.toast.teammateEntered` | `ArenaLockdownManager.cs` (LD-2c `Notify` toast, t0, via `ShowToast`) | Heads-up at t0; LD-2d grace keeps the door open ~5 s, so it invites the player to run in. |
| 5 | title `Arena Lockdown` / msg `You've been sealed out — you'll be brought in shortly.` | `arena.toast.title`, `arena.toast.sealedOut` | `ArenaLockdownManager.cs` (LD-2c `Seal` toast, t0+5 s) | Explains the otherwise-invisible barrier. |
| 6 | title `Arena` / msg `Entering the arena.` | `arena.entering.title`, `arena.entering.msg` | `ArenaLockdownManager.cs` (LD-2c teleport toast) | Fired on teleport-in (confirm / boss-death release). |
| 7 | title `Arena Lockdown` / msg `You entered the arena — the gate seals in a few seconds; teammates can still run in.` | `arena.toast.title`, `arena.toast.youEntered` | `ArenaLockdownManager.cs` (LD-2e `NotifyEntered` toast, t0) | Heads-up to the player(s) who entered first. |
| 8 | title `Together` (default toast heading) | `toast.title.default` | `CoopToasts.cs` (UI-1) | Heading used for all co-op event toasts that don't pass an explicit title. |
| 9 | `{name} joined` | `toast.playerJoined` | `NetService.HandleHandshakeRequest` (UI-1, host) | `{name}` = the joining player's display name. |
| 10 | `Connected to {host}` | `toast.connectedTo` | `NetService.HandleHandshakeAccepted` (UI-1, client) | `{host}` = host display name. |
| 11 | `{name} left` / `Disconnected from host` | `toast.playerLeft`, `toast.disconnectedFromHost` | `NetService.OnPeerDisconnected` (UI-1) | First form host-side (a client left); second client-side (the host dropped). |
| 12 | `Linked to host` / `Playing solo` | `link.linkedToHost`, `link.playingSolo` | `NetLinkState.SetClientLinked` (UI-1) | Client link toggled on/off (PageDown / PageUp). |
| 13 | `Hosting ON` / `Hosting OFF` | `link.hostingOn`, `link.hostingOff` | `NetLinkState.SetHostLinked` (UI-1) | Host multiplayer master switch toggled. |
| 14 | _(superseded by row 16 — UI-3b page replaced by UI-3c rework)_ | — | `CoopConnectPage.cs` | Historical; no live strings. |
| 15 | `Hosting on port {port}{steam} — {n} player(s) connected` / `Connected to host {label}` / `Connecting to {label}…` / `Off` / ` (Steam invites open)` | `connect.summary.hosting`, `connect.summary.connectedHost`, `connect.summary.connecting`, `connect.summary.off`, `connect.summary.steamSuffix` | `NetService.GetConnectionSummary` (UI-3) | Connect-page status line. `{steam}` = the steamSuffix fragment when Steam invites are open. |
| 16 | Connect page (UI-3c rework) — sections `Player` / `Connection` / `Players in session` / `Local preferences (only affect you)` / `Session settings (host)` / `About`; labels `Player name` / `Host address (IP)` / `Port` / `Connection key` / `Show player join/leave notifications` (desc) / `Show network status on HUD` / `Show other players' names` / `Rescue key` / `Confirm-enter-boss-room key` / `Loot mode` / `Client may start next level` / `Version`; buttons `Create game` / `Join game` / `Close room` (host) ↔ `Leave` (client) / `Open-source repo` / `Support on Ko-fi`; descriptions (intro, connection); placeholder values `Coming soon` / `Independent (Shared coming soon)`; status `Not connected` / `Connecting…` / `No players connected.` / gate hint | `connect.section.*` (`player`/`connection`/`players`/`localPrefs`/`sessionSettings`/`about`), `connect.label.*` (`playerName`/`hostAddress`/`port`/`connectionKey`/`showToasts`/`showHudStatus`/`showNames`/`rescueKey`/`confirmEnterKey`/`lootMode`/`clientMayStart`/`version`), `connect.button.*` (`create`/`join`/`closeRoom`/`leave`/`repo`/`kofi`), `connect.desc.intro`, `connect.desc.connection`, `connect.desc.showToasts`, `connect.value.comingSoon`, `connect.value.lootIndependent`, `connect.status.notConnected`, `connect.status.connecting`, `connect.players.none`, `connect.gateHint` | `CoopConnectPage.cs` (UI-3c, auto-save UI-3e) | Reworked native Options page on the 0.10 live handles. Brand section header `SULFUR Together` and the `Steam` block header stay untranslated (proper nouns). End-session button label is host/client-aware. |
| 16b | Steam block — `Connect over Steam…` desc / `Steam is not available…` / `Invite Friends via Steam` / `Invite more friends via Steam` / `Steam ID to join` / `Join via Steam` / `Your Steam ID: {id}…` / `Joining a Steam friend's game…` / `Joining {name}'s SULFUR Together game…` / `A Steam friend invited you…` / `{name} invited you…` / `Enter a valid Steam ID…` | `connect.desc.steam`, `connect.steam.unavailable`, `connect.button.inviteFriends`, `connect.button.inviteMoreFriends`, `connect.label.steamIdToJoin`, `connect.button.joinViaSteam`, `connect.steam.yourId`, `connect.steam.joiningFriend`, `connect.steam.joiningNamed`, `connect.steam.invitedLoadSave`, `connect.steam.invitedLoadSaveNamed`, `connect.error.invalidSteamId` | `CoopConnectPage.cs` (Steam block) | Steam connectivity sub-section of the connect page. |
| 17 | `Host rejected: {reason}` / `Connection failed — host unreachable…` / `Could not start networking ({type})…` / `Could not start the Steam connection.` / `Steam is not available.` | `connect.error.hostRejected`, `connect.error.unreachable`, `connect.error.couldNotStart`, `connect.error.steamStart`, `connect.error.steamUnavailable` | `NetConnectFeedback` (set by `NetService` / `CoopConnection`, UI-3c) | Client join-failure reason surfaced in the connect page. `{reason}` = host's `RejectPeer` string. |
| 18 | `Your LAN address: {ip}:{port}  (others on your network join with this)` | `connect.lanAddress` | `CoopConnectPage.ApplyHostLanIp` (UI-3d) | Shown only while hosting; `{ip}` from `NetLocalAddress`, `{port}` from config. The Steam-name auto-seed inserts the player's own persona name into the "Player name" field — player data, not a translatable literal. |
| 19 | title `Sandstorm Arena` / msg `Pulled into the arena — the sandstorm outside would grind you down.` | `arena.sandstorm.title`, `arena.sandstorm.pulledIn` | `ArenaLockdownManager.cs` (LD-Sandstorm `PullIn` toast) | Desert boss: fired when an out-of-arena player is teleported to the arena centre ~3 s after the dialog trigger (gate-less arena; the sandstorm ring is the wall). |
| 20 | Run Stats card (RS-2): 7 stat row labels `Shots Fired` / `Damage Dealt` / `Kills` / `Times Downed` / `Rescues` / `Damage Taken` / `Destructibles Destroyed`; name-row suffix `{name} (You)` | `runstats.stat.shotsFired`, `runstats.stat.damageDealt`, `runstats.stat.kills`, `runstats.stat.timesDowned`, `runstats.stat.rescues`, `runstats.stat.damageTaken`, `runstats.stat.destructiblesDestroyed`, `runstats.youSuffix` | `RunStatsCardView.cs` | End-of-Run card overlay shown over the Hub-return loading screen. Player names themselves are user data, not translatable literals. |
| 21 | `Friendly fire` label (desc) / `Session friendly fire: {state} (set by host)` with `{state}` = `ON`/`OFF` | `session.friendlyFire.label`, `connect.desc.friendlyFire`, `connect.ffSession`, `common.onUpper`, `common.offUpper` | `CoopConnectPage.cs` (FF-1) | Live toggle; the session line is read-only and shown only while connected as a client. |
| 22 | `{label}: {state}` session-setting change toast, `{state}` = `On`/`Off`; current `{label}` = `Friendly fire` | `session.settingChanged`, `common.on`, `common.off`, `session.friendlyFire.label` | `CoopToasts.NotifySessionSetting` (SS-Toast) | Fired on host + every client when the host changes a session setting mid-session (join-time sync is silent). Every future session setting reuses this formatter with its own label. |
| 23 | `Rescuing {name}` / `Hold [{key}]` | `rescue.rescuer.active`, `rescue.hold` | `DownedRescueOverlayManager` (DR-2) | Rescuer's prompt (main/sub text) while actively holding the revive key near a downed teammate. |
| 24 | `Rescue {name}` / `Hold [{key}]` | `rescue.rescuer.idle`, `rescue.hold` | `DownedRescueOverlayManager` (DR-2) | Rescuer's idle hint — shown when near a downed teammate but not yet holding the key (progress 0, a local-only proximity check, not network state). |
| 25 | `Rescue complete` / `Restored` | `rescue.complete.rescuer`, `rescue.complete.restored` | `DownedRescueOverlayManager` (DR-5) | Brief completion text — first form shown to the rescuer, second to the just-revived player — held ~0.6s before the panel fades out. Never shown on a cancelled rescue (that just fades, no text change). |
| 26 | Developer-mode session toast — `Developer mode enabled` / `Developer mode disabled — not all players have dev access` | `session.devmode.enabled`, `session.devmode.disabled` | `CoopToasts.NotifyDeveloperMode` (DEV-1) | Fired on host + every client when the session dev-mode flag flips (vote passed / all players entitled / a non-dev player joined). Dev-specific wording so a disabled state explains *why*. |
| 27 | Connect-page "Session events" section — heading, propose button, and the four dev-vote status lines | `connect.section.sessionEvents`, `connect.button.proposeDevMode`, `connect.devVote.solo`, `connect.devVote.on`, `connect.devVote.active`, `connect.devVote.off` | `CoopConnectPage.cs` (VOTE-1) | The propose surface + live status for the dev-mode vote. `{agree}`/`{total}`/`{secs}` tokens on the active line. |
| 28 | Vote overlay — title, prompt, and result lines | `vote.title.devMode`, `vote.title.generic`, `vote.prompt`, `vote.result.devEnabled`, `vote.result.tally`, `vote.result.failed`, `vote.result.devStaysOff`, `vote.result.cancelled`, `vote.result.cancelledSub` | `VoteOverlayManager` (UI-VOTE) | The in-world vote HUD. `{agree}`/`{total}`/`{secs}` tokens. Result lines shown during the 5s residual before the panel fades. |
| 29 | `Shared loot` label (desc) / `Session shared loot: {state} (set by host)` with `{state}` = `ON`/`OFF` | `session.sharedLoot.label`, `connect.desc.sharedLoot`, `connect.slSession`, `common.onUpper`, `common.offUpper` | `CoopConnectPage.cs` (SL-4) | Host-authoritative session toggle replacing the old `Loot mode: Independent (Shared coming soon)` read-only row; the session line is read-only, shown only while connected as a client. Reuses the SS-Toast formatter (row 22) with the `Shared loot` label. |
| 30 | `Shared endless progress` label (desc) / `Session endless progress: {state} (set by host)` with `{state}` = `Shared`/`Independent` | `session.sharedEndless.label`, `connect.desc.sharedEndless`, `connect.epSession`, `common.shared`, `common.independent` | `CoopConnectPage.cs` (EM-4) | Host-authoritative Endless progression-mode session toggle (Shared vs Independent), same lock/mirror shape as row 29; the session line is read-only, shown only while connected as a client. Reuses the SS-Toast formatter (row 22) with the `Shared endless progress` label. |
| 31 | Shared card-vote status hint — pre-vote prompt, waiting line, resolved line (incl. Skip/Reroll outcome) | `endless.cardvote.prompt`, `endless.cardvote.waiting`, `endless.cardvote.rolling`, `endless.cardvote.resolved`, `endless.cardvote.resolved_skip`, `endless.cardvote.resolved_reroll` | `EndlessCardVoteOverlay` (EM-6b-3a/3b) | Bottom-centre line under the shared 1-of-N card vote. `{voted}`/`{total}`/`{secs}` tokens on the waiting line (countdown only runs after the first cast); `{index}` (1-based) on the ordinary resolved line; `_skip`/`_reroll` replace it when the vote resolves to the Skip/Reroll card (EM-6b-3b). On-card voter *names* are player-supplied, not localized. |
| 32 | Unlinked-client join hint — `You're not in the host's world yet — press [{key}] to join` | `link.pressToJoin` | `CoopToasts.NotifyLinkHint` (LK-Hint) | Shown (throttled, 90s) when a connected but UNLINKED client's auto-follow skips a followable host target — the deliberate no-hijack skip used to be silent, leaving a first-time joiner invisible in their own hub instance with no clue the link key exists. `{key}` = configured `ManualClientSceneFollowKey`. |

> Rows 26–31 added to `en.json` (canonical) + hand-translated `zh-CN.json` / `ja.json`; the other 11
> language files fall back to English via the `current → base → en` chain until synced.

### Planned, not yet written (register here when added)

- _(none at present)_

---

## Adding a new player-facing string

1. Call `CoopLoc.Get("your.key", "English text")` (or `CoopLoc.Format(...)` with
   `{token}` placeholders) at the call site — the English literal stays as the fallback.
2. Add the key + English value to `lang/en.json`, and the same key to **every** other
   `lang/*.json` (translated; keep tokens verbatim). Key sets must stay identical
   across all files.
3. Add a row to the registry above with the key(s) and source.

Keep log/config text out of it (see "What is NOT player-facing"). Do not add new
on-screen text without registering it here.
