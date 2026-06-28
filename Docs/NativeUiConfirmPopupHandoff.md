# Handoff — In-game confirm popup for LD-2c (to be built in SULFUR Native UI Lib)

**Status:** spec only. No code on the Native UI Lib side yet.
**Owner of the consumer side:** SULFUR Together (this repo), phase LD-2c.
**Owner of the producer side:** `SULFUR Native UI Lib` (`ryuka.sulfur.nativeui`,
`C:\Users\LingYun67\source\SULFUR Native UI Lib`).

This document is the contract so the popup can be implemented in a session rooted in the
Native UI Lib repo (its own git / build / docs / conventions) and then wired back here.

---

## 1. Why this lives in Native UI Lib (and the dependency direction)

The FF14 arena lockdown (phase LD-2) seals out-of-room players behind an invisible barrier
(LD-2b) and then, at t0+10 s, shows a confirm prompt → on confirm (or boss death) the player
teleports into the arena and the barrier drops (LD-2c). See
[`Docs/BossPreFightFlow.md`](BossPreFightFlow.md) §2 and the phase memory `phase-ld-arena-lockdown`.

The game's own `Dialog` MonoBehaviour (`PerfectRandom.Sulfur.Core.Dialog`,
`SetDialogText`/`SetState(UIState)`) was investigated and is an **orphan class**: no field holds
it, no prefab references it, nothing instantiates it, and its `Start()` dereferences serialized
fields (`textObject`/`background`) that are only set on a prefab. It cannot be reliably reused.

So a **reusable native-style popup belongs in the UI library**, not bolted into the co-op mod.

**Dependency direction (important):** Native UI Lib must NOT reference SULFUR Together. The
co-op mod takes a `HardDependency` on `ryuka.sulfur.nativeui` and calls into the lib. The lib
just exposes a generic popup API; it knows nothing about arenas or networking.

Current Native UI Lib scope is **OptionsScreen pages only** (left-side category, native setting
rows). This popup is a **new surface**: an in-game HUD overlay that can appear during normal
gameplay / combat, independent of the options screen. That is the bulk of the new work.

---

## 2. The seam that already exists on the consumer side

`SULFURTogether.Networking.Gameplay.ArenaLockdownManager` already has the integration points
(committed in `0dbfaa5`):

```csharp
// Default null → the manager only logs the prompt; confirm still works via the configured key.
public static Action<string> ShowPrompt;  // called with the prompt text when the popup should appear
public static Action         HidePrompt;  // called when the player enters / the prompt is dismissed
```

- `ShowPrompt(text)` is invoked at t0+10 s with text like `"Press [Return] to enter the arena"`.
- `HidePrompt()` is invoked when the player teleports in (confirm / boss-death release) or on
  scene change / `Clear()`.
- **The confirm keypress is owned by the mod**, not the popup. `ArenaLockdownManager.LocalTick()`
  polls `Plugin.Cfg.ArenaEnterConfirmKey` (default `Return`) every frame while armed. So for
  LD-2c the popup is **display-only** — it does not need to read input or invoke a callback.

These two `Action`s are the wiring slots. SULFUR Together will assign them (from its own `Plugin`
init) to call the Native UI Lib API once that API exists — Native UI Lib never touches these.

---

## 3. API to add in Native UI Lib

### 3.1 Minimum (covers LD-2c) — a display-only banner/modal

```csharp
namespace Ryuka.Sulfur.NativeUI
{
    public static class SulfurPopupApi
    {
        /// Show (or update) a single persistent in-game message banner/modal. Idempotent —
        /// calling again replaces the text. Survives until Hide().
        public static void ShowBanner(string text);

        /// Hide the banner if shown. Safe to call when nothing is shown.
        public static void HideBanner();
    }
}
```

SULFUR Together then wires (in its `Plugin.Awake`, guarded by the soft/hard dependency):

```csharp
ArenaLockdownManager.ShowPrompt = SulfurPopupApi.ShowBanner;
ArenaLockdownManager.HidePrompt = SulfurPopupApi.HideBanner;
```

That is the whole LD-2c need. Everything below is optional reuse value.

### 3.2 Optional (general reuse) — a confirm modal that owns its own input

```csharp
/// Show a Yes-only / press-to-continue modal. onConfirm fires when the user presses confirmKey.
/// Returns a handle so the caller can dismiss it programmatically (e.g. boss death).
public static IPopupHandle ShowConfirm(string text, KeyCode confirmKey, Action onConfirm);

public interface IPopupHandle { void Dismiss(); }
```

If you build this, LD-2c could switch to it later and drop its own key-poll — but that's a
follow-up, not required now. Ship 3.1 first.

---

## 4. Requirements / acceptance for the banner

- **In-game HUD overlay**, not the options screen. Must render during normal play and combat.
- **Persistent** until `HideBanner()` (the prompt stays for the whole t0+10 s → confirm window,
  which can be many seconds).
- **Centered, readable, native-looking.** Match the game's UI font/style where practical.
- **Does not pause the game / steal input.** The mod runs a no-pause multiplayer model (see
  [`Docs/MultiplayerPauseAudit.md`](MultiplayerPauseAudit.md)); the banner must be passive — no
  `Time.timeScale`, no input capture, no cursor lock changes.
- **Single instance** — repeated `ShowBanner` updates text, doesn't stack.
- **Cheap when hidden** — no per-frame cost while not shown.

## 5. Implementation options for the Native UI Lib side (your call there)

1. **Self-built uGUI Canvas overlay (recommended).** A persistent `Canvas` (ScreenSpaceOverlay,
   high sort order) + a `Text`/`TextMeshProUGUI` + a translucent background, created once and
   toggled active. Full control, robust, reusable, no dependency on fragile game prefabs. Reuse
   `SulfurReflection` only if you want to pull the game's font for visual match.
2. **Reuse a game UI prefab if one is findable at runtime.** Riskier; the `Dialog` class is
   orphaned, so don't rely on it. Only pursue if you find a clean, instantiable native modal.
3. **IMGUI `OnGUI` box (fallback).** Simplest, least pretty; the co-op mod already uses this
   pattern for the downed/revive prompt (`NetPlayerLifeManager.DrawCenterPrompt`). Acceptable as
   a stopgap but the point of putting this in the UI lib is to do better than IMGUI.

## 6. After the lib ships

Back in SULFUR Together:
1. Add the BepInEx dependency on `ryuka.sulfur.nativeui` (hard or soft+guarded).
2. Assign `ArenaLockdownManager.ShowPrompt` / `HidePrompt` to the lib API in `Plugin` init.
3. In-game verify: out-of-room player at t0+10 s sees the centered prompt; pressing the confirm
   key teleports them in and the prompt disappears; boss death also clears it.

Until then LD-2c is fully functional minus the visual — the confirm key already teleports the
player; the prompt is only logged.
