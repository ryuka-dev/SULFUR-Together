# Player Life: Co-op Downed / Revive & Downed Input

The co-op downed/revive lifecycle (Phase 4.3) and how a downed player's input is restricted. Config group: `NetworkPlayerLifeExperimental`. Message: `PlayerLifeState=18`. Main file: `src/Networking/Gameplay/NetPlayerLifeManager.cs`.

> Reverse-engineered references (PlayerLocks, InputReader, PauseGame, crouch controller) live in **[ReverseMapping.md](ReverseMapping.md)** under "Player Input / Locks / Downed Controls".

---

## 1. Downed / revive lifecycle

Each peer owns its **own** death — the mod only delays/commits it; it never syncs inventory or death penalties.

- The local player's lethal `Unit.Die` is intercepted (`Unit_Die_Pre` → `NetPlayerLifeManager.TryBlockLocalPlayerDeath`). Instead of dying, the player enters a **downed** state (`EnterLocalDownedState`): `SetUnitState("Incapacitated")`, health pinned to `PlayerDownedHealthFloor`, `SetInvulnerableForDuration(huge)`, control locks applied, and a `Downed` `PlayerLifeState` is published.
- A teammate revives by holding `PlayerReviveHoldKey` (default `E`) near the downed player for `PlayerReviveHoldSeconds` within `PlayerReviveDistance`. Revive heals to `PlayerReviveHealthRatio` of max HP.
- **Native death commits** (the real death actually happens) when: the host decides all players are down, the rescue times out (`PlayerDownedRescueTimeoutSeconds`, `0` = infinite wait), or a network `NativeDeathCommit` arrives. Committing arms the death-respawn epoch (see [SceneTransitionAndLinkState.md](SceneTransitionAndLinkState.md) §7).

Key flags: `_localDowned`, `_localNativeDeathCommitted`, `_localControlLocksApplied`. Config: `EnableCoopPlayerDownedRevive` (master), `LogPlayerLifeSync` (logging).

---

## 2. Downed input = a combat BLACKLIST (not a whitelist)

While downed the player keeps **camera look, the pause/menu (ESC → quit), and F3 dev tools** usable, and is blocked only from combat-related actions. This is enforced by **two complementary mechanisms** — both gated by `ShouldSuppressLocalPlayerControls()` (true only while the local player is downed):

**A. `GameManager.PlayerLocks` (a `[Flags]` enum)** — `ApplyLocalControlLocks` locks ONLY:
| Lock | Blocks |
|------|--------|
| `PlayerMovement` | movement |
| `Weapon` | all weapon actions (shoot / reload / melee / switch / ADS) |
| `Inventory` | opening the backpack |

Deliberately NOT locked: `Camera` (look), `Interaction`, `UseHUD`. The blacklist set is `NetPlayerLifeManager.DownedInputBlacklist`.

**B. `InputReader` action patches** (`ApplyInputReaderPatches`) — a small blacklist:
- `Get*MovementInput` / `IsJumpKeyPressed` → forced to zero/false (movement).
- Weapon **switch** selectors (`SelectSlot1-5`, `SelectNext/PreviousSlot`, `SelectByScroll`, `SelectLastUsedWeapon`) → blocked.
- **Everything else stays allowed** — crucially `PauseMenu` (ESC) and `DevToolsToggle` (F3) plus UI navigation, so a downed player can always open the menu and quit.

> History: this used to block ~60 InputReader actions (incl. `PauseMenu`/`DevToolsToggle`) — an effective whitelist that meant a downed player couldn't even quit. It was trimmed to combat-only. The pause/quit is the game's own `InputReader.PauseMenu → GameManager.PauseGame()`, so it uses **whatever key the player bound to TogglePause** (no hardcoded ESC). `PauseGame` only needs `gameState == Running`, which the downed state preserves (death is blocked before the game's Cinematic transition).

**C. Forced crouch** — patch on `ExtendedAdvancedWalkerController.UpdateCrouching`: while downed it forces `ToggleCrouch(true)` (the downed pose) and ignores crouch input; on revive it stands up once, then the game's own crouch logic resumes. (Crouch is `OnFoot.Crouch`, handled by the movement controller, not an InputReader callback — so it needs its own patch.)

---

## 3. Config (`NetworkPlayerLifeExperimental`)

`EnableCoopPlayerDownedRevive`, `LogPlayerLifeSync`, `PlayerDownedRescueTimeoutSeconds` (0=infinite), `PlayerReviveHoldSeconds`, `PlayerReviveDistance`, `PlayerReviveHealthRatio`, `PlayerDownedHealthFloor`, `PlayerReviveHoldKey` (default E).

---

## 4. Known gaps

- Occasionally a client cannot rescue a downed host (no rescue prompt); not consistently reproducible — under watch.
- The downed-input blacklist is intentionally small/explicit so it can grow later (e.g. blocking interaction, or allowing a "last-stand" shot would need finer per-action hooks since the `Weapon` lock is all-or-nothing).
