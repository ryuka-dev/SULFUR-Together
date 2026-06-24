# Multiplayer Pause Audit & No-Pause (Phase 5.7-NP)

**Requirement (project owner, 2026-06-24):** like Minecraft opening a single-player world to LAN, a
co-op session must **not** stop world time. The game pauses on opening the inventory/backpack, the ESC
menu, F3 dev tools, NPC dialog, and on losing window focus. Any one side pausing freezes the other
player's enemies and — critically — **desyncs boss timelines**, which are time-axis driven (a boss
advances on game time; if one side stops, the two ends no longer agree on the boss's phase/clock).

## Audit — how SULFUR pauses (decompiled `PerfectRandom.Sulfur.Core`)

Central API: **`GameManager.ModifyGamePauseState(LockStatePadlock lockState, bool state)`**
- Maintains a `gamePausedByState` `HashSet<LockStatePadlock>`. Adds the padlock on `state==true`,
  removes on `false`.
- Then, when `gameState != Loading`: if **any** padlock is held → `SetState(GameState.Paused)`,
  else → `SetState(GameState.Running)`.

`GameManager.Update()` maps state → time:
```
gameState == Running          -> Time.timeScale = timeScale   (the live gameplay scale)
gameState == Paused | Loading -> Time.timeScale = 0           (world stops)
```
So **a single padlock stops the entire world.**

`LockStatePadlock` enum: `Inventory, DevTools, Loading, Cinematic, Paused, Dialog, Vehicle, Tutorial,
Amulet, Flashback, HoldingInteract`.

### World-pausing entry points (callers of `ModifyGamePauseState(_, true)`)
| Trigger | Padlock | Notes |
|---|---|---|
| Inventory / backpack open | `Inventory` | the symptom the owner hit |
| ESC menu — `GameManager.PauseGame()` | `Paused` | the **only** one gated by the game's own `IsPausePrevented` (`pausePreventedBy.Count > 0`); inventory/dialog/devtools call `ModifyGamePauseState` directly and bypass it |
| F3 dev tools | `DevTools` | |
| NPC dialog | `Dialog` | |
| Cutscene | `Cinematic` | **left alone** for now (boss intros may rely on it) |
| Real scene load | `Loading` | **must** keep stopping time |

### Separate path — lost window focus
`MenuManager.OnApplicationFocus(bool hasFocus)` sets `Time.timeScale = 0` directly on `!hasFocus`.
Important when running two instances on one PC (clicking the client pauses the host) and for real
alt-tab.

### Why UI still works when we drop the pause padlock
Opening the inventory/menu also calls **separate** systems — `ModifyCursorState`,
`ModifyControllerLock`, `InventoryUI.SetState`. Those are independent of the pause padlock, so the bag
still opens, the cursor still appears, and the player's own movement is still gated. Removing only the
pause padlock keeps `gameState == Running` → time keeps flowing → enemies and boss timelines advance,
while the player stands in their bag. This is exactly the Minecraft behavior (mobs keep attacking you
while your inventory is open).

## Fix — `src/Patches/PauseControlPatches.cs` (gate `DisablePauseInMultiplayer`, default ON)

`SuppressPause()` = a co-op session is active for this instance:
`mode==Client → NetLinkState.ClientLinked`, `mode==Host → NetLinkState.HostLinked`, `Off → false`
(single-player keeps normal pause).

1. **Prefix `GameManager.ModifyGamePauseState`** — when `state==true` and `SuppressPause()` and the
   padlock is one of `{Inventory, Paused, DevTools, Dialog}` (compared **by enum name**, never by its
   integer value), return `false` to skip adding it. `gameState` stays `Running`, `Time.timeScale`
   stays live. Only **adds** are blocked — removes always run, so nothing can get stuck paused.
   `Loading`, `Cinematic`, etc. behave normally.
2. **Prefix `MenuManager.OnApplicationFocus`** — when `!hasFocus` and `SuppressPause()`, return `false`
   so focus loss no longer zeroes `Time.timeScale`.
3. **`Application.runInBackground = true`** (set in `Apply`) so a second instance on the same PC keeps
   running `Update` while unfocused (otherwise Unity would freeze the background window regardless of
   `timeScale`).

Registered from `PatchBootstrap.ApplyAll`. Diagnostics: `LogPauseSuppression` →
`[PauseControl] suppressed world pause padlock=…` / `ignored focus-loss pause`.

**Verified (owner):** opening backpack / ESC / F3 / dialog / clicking the other window no longer
freezes the world in co-op; single-player (unlinked / `Off`) still pauses normally.

## Open / future
- **Cinematic** padlock is intentionally not blocked. If a boss intro cutscene desyncs because the host
  pauses on it, add `"Cinematic"` to the blocked set (or handle boss intros explicitly).
- The pause menu still appears on ESC (world running behind it) — acceptable / Minecraft-like. If a
  dedicated co-op pause UX is wanted later, build it on `SuppressPause()`.
