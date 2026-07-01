# Scene Transition, Join Flow & Link State

Covers how players load levels together: the host is the authority for **which level and which seed**, and a client never plays in a level/seed the host has not specified. This is the architecture behind Phases 5.3-I → 5.6-LK.

> Companion: `ReverseMapping.md` (the level-transition method chain) and `NetworkingArchitecture.md` (message table). Messages used here: `RunStateUpdate=10`, `HostSceneRequest=11`, `ClientSceneAck=12`, `ClientSceneRefused=13`, `ClientHostGenerationInputRequest=27`, `ClientTransitionRequest=46`.

---

## 1. Core principle

**The host owns "what level + what seed".** A SULFUR level is generated deterministically from `GlobalSettings.ForceLevelSeed` plus the GameManager cross-level exclusion sets (`usedChunksThisRun`, `usedUniqueEventThisRun`, `usedUniqueEventThisEnvironment`). Same graph + seed + used-sets ⇒ identical map (verified: matching generation/runtime hashes). So to put two players in the same instance the client must reproduce the host's **seed and used-sets**, not just the scene name.

Two directions, both keep the host as the single generation authority:
- **Host leads** → host advances, broadcasts `HostSceneRequest`, the client **auto-follows**.
- **Client leads** → the client's own load is intercepted, relayed (`ClientTransitionRequest`), the host performs the transition (host player moves + generates), then the existing finalized broadcast brings the client along.

---

## 2. The level-transition method chain (reverse-engineered)

```
GameManager.GoToLevel(chapterSO, levelIndex, loadingMode, spawn)   // chapter-level entry (hub→combat, F3, death, special jumps)
    └─ StartCoroutine(SwitchLevelRoutine(...))                     // AUTHORITATIVE entry; teardown + Destroy(PlayerObject)
            └─ StartCoroutine(StartLevelRoutineGraph(...))         // sets currentLevelIndex; clears env used-sets only at level 0; generates
GameManager.CompleteLevel()                                        // in-run sub-level advance (NextLevelTrigger / DevTools "complete level")
    └─ OnCompleteLevelRoutine()                                    // Cinematic 0.5s → Loading → currentLevelIndex++ → SwitchLevelRoutine(...)
GameManager.GoToMainMenu(bool)                                     // returns to the SEPARATE MainMenu.unity scene (NOT ChurchHub)
```

Key facts that drive the design:
- **In-run sub-levels advance via `CompleteLevel` → `SwitchLevelRoutine`, NOT `GoToLevel`.** Gating only `GoToLevel` misses in-run advancement (fixed in 5.6-CL by also intercepting `CompleteLevel`).
- `NextLevelTrigger.OnTriggerEnter` is **one-shot** (`triggered=true`): `specificEnvironment==None` → `CompleteLevel()`; `==ChurchHub` → `GoToChurchHub`; else → `GoToLevel(env,0)`.
- The real generation **seed/graph/level come from the `MakerGraphContext`**, not from `GoToLevel`'s args (its `LevelIndex` is often 0 for chapter entry). Captured at generation finalize.
- **ChurchHub (the in-game hub/safe zone) always loads with `loadingMode=Menu`** — "Menu" is NOT a main-menu signal. The actual main menu is `GoToMainMenu` → `MainMenu.unity`.
- The black loading fade is the game's own `UIManager.LoadingFade(bool)` → `LoadingFade.FadeOut(Color.black)`/`FadeIn()` (a single animator bool, self-clearing on level-gen completion).

---

## 3. Generation-input capture (host) — `NetGenerationInputCapture`

The host captures the deterministic generation input so a client can reproduce it:
- **Pending** at `StartLevelRoutineGraph` prefix (stable inputs: chapter/level/usedSets/loadingMode/spawn).
- **Finalized** at the first level-gen node (`LevelGenTracePatches.ResetContext_Post`): real `graphName` (= `MakerGraphContext.Set.name`) + real `graphSeed` (= `context.Seed`). Stored in `LastFinalizedSnapshot`.
- `HostSceneRequest` is built from the **finalized snapshot** (not stale `LocalState`), attaching seed + graph + used-sets.
- Used-sets sync: `NetGameManagerUsedSets` reflects the three GameManager HashSets; the client overwrites them BEFORE `GoToLevel` (`NetManualSceneFollower.ApplyHostUsedSets`).

---

## 4. The client load gate — `NetClientLoadGate`

A static gate (the `GoToLevel`/`CompleteLevel` Harmony prefixes run in a static context). It intercepts the client's own loads and, instead of generating locally, drives a **host-seeded** load.

State machine: `Idle → Waiting → HostDrivenInProgress`. `PendingKind`: `Combat` (waiting to be led to the pending target), `DeathRespawn`.

- **`ShouldInterceptGoToLevel`** (from `GM_GoToLevel_Pre`): bypass on reentry / host-mode / disabled / **not-linked**. When linked: a death-respawn hub → death gate; `Menu` → bypass; **everything else (Combat / Hub / Unknown) → client-led relay** (Phase 5.6-LK-P2 — map type does not decide *whether* a transition works).
- **`TryBeginClientLevelCompleteRelay`** (from `GM_CompleteLevel_Pre`): same, for in-run advancement.
- **Reentry guard** (`BeginHostDrivenLoad`/`EndHostDrivenLoad`): wraps the host-driven/manual `GoToLevel` invoke so the prefix doesn't re-intercept our own follow.
- **Release** (`HasClientLedReleaseRequestLocked`): a client-led wait releases when the host's latest broadcast targets the *exact* pending scene **with a seed** — this is what lets the client lead the host to an arbitrary place (incl. back to a safe zone).
- **Timeout fallback**: a client-led load that the host never leads falls back to a real local load (`InvokeNativeGoToLevel` / `InvokeNativeCompleteLevel` under the reentry guard) so the player is never stuck (`ClientInitiatedLoadTimeoutSeconds`, default 15s).

### Auto-follow (host leads)
`OnHostGenerationInput` → `TryAutoFollow`: when the host broadcasts a combat/hub target with a seed and the client is **linked**, the client drives a host-driven `GoToLevel` with the host's seed/used-sets. Hub follows compare the **seed** too (same `ChurchHub:0` with a different seed = different instance → `HubSeedMismatchReload`).

---

## 5. 联机状态 / Link state — `NetLinkState` (the current top-level model)

Explicit, user-controlled state that is **the single authority** for whether the mod's multiplayer behaviour is active. It supersedes the older implicit `SessionJoinedHost` latch and `ClientJoinMode` heuristics (those still exist but are fully subordinate: linked → always follow, unlinked → independent).

- **`ClientLinked`** (default OFF): while linked the client joins/follows the host AND relays ALL of its own (non-host) map switches so the host leads everyone. While unlinked the client plays its own run untouched (no intercept, no auto-follow, no relay) — this is what lets a player finish a half-done solo run before joining.
- **`HostLinked`** (default ON, **hardcoded**): master switch for the host's broadcasting / relay-leading. Off ⇒ host behaves single-player (`SendHostSceneRequest` early-returns, relays ignored). The startup default `HostLinkedByDefault` is now `Fixed<bool>(true)` (retired from the `.cfg`, pruned on load): a stale/off `.cfg` value silenced the host's scene requests so a joining client never received a target and could never auto-follow ("进不去" — a silent, easy-to-hit trap). A temporary single-player host is still available in-game via `HostLinkToggleKey`.

Controls (client): **PageDown** = `ManualClientSceneFollowKey` → link + follow host; **PageUp** = `ClientUnlinkKey` → unlink. Host: `HostLinkToggleKey` (PageDown) toggles `HostLinked`.

Resets to default: networking stop/disconnect, and **`GoToMainMenu`** (re-entering a save starts unlinked — `ResetClientToDefault`). NOT on hub loads (ChurchHub is Menu mode — keying the reset on "menu" was a regression, fixed in LK-P4).

**Safe-zone join (LK-P5):** the host only broadcasts a `HostSceneRequest` on scene-NAME drift, so when both sit in the same-named hub (`ChurchHub:0`, different seeds) it never sends one. A LINKED client's manual follow therefore falls back to the host's **run state** directly (which carries the host's hub scene + seed) instead of requiring a `HostSceneRequest`, reloads to the host seed, and joins the same instance.

---

## 6. Client-led transition relay — `ClientTransitionRequest=46`

When a linked client's load is intercepted (combat exit, in-run `CompleteLevel`, F3 jump, returning to a safe zone):
1. The gate enters `Waiting`, shows the loading fade, and `TryConsumeTransitionRelayDue` throttle-sends `ClientTransitionRequest{chapter,level,mode,spawn,clientCurChapter,clientCurLevel}`.
2. Host `HandleClientTransitionRequest`: validates `HostLinked` + not-already-there + not-busy + **host-transition guard** (see §8); then `NetManualSceneFollower.TryFollow` runs the host's own `GoToLevel` (host player moves + generates authoritatively, no forced seed).
3. The host's finalized broadcast releases the gated client, which follows with the host's seed.

No same-scene guard: only a linked client ever relays, and a linked client may lead the host anywhere (Phase 5.6-LK).

---

## 7. Death-respawn guard — `NetPlayerLifeManager` + gate `PendingKind.DeathRespawn`

All-players-down used to double-load the client (stale combat request → combat level → hub). Guard:
- On local native death the client arms a death epoch (`NoteLocalDeathRespawnArmed`).
- The hub death respawn is gated and follows whatever destination the host broadcasts **after** that epoch (hub on an all-die, or a combat level if the host F3/teleports), using the host's seed → same instance. Timeout → local hub fallback (`ClientGateDeathRespawnTimeoutSeconds`, default 12s).
- Host suppresses combat re-advertisement during its own death-respawn window.

---

## 8. Loading fade & the both-ends race

- **Loading fade** (`NetLoadingFade`, Phase 5.6-CL): when a client-initiated load is intercepted, show the game's own black `LoadingFade(true)` + loading overlay so the player isn't frozen during the relay round-trip. It self-clears when the host-driven follow's generation completes; `Hide()` is only a timeout/reset safety net.
- **Host transition guard** (`NetHostTransitionGuard`, Phase 5.6-LK-P2): when both players walk into the same exit, the host's own `CompleteLevel` and the client's relay would each generate the next level (two seeds → double load). The guard latches at the earliest point of any host transition (`CompleteLevel`/`GoToLevel`/`SwitchLevelRoutine` prefixes) and clears when the host applies its finalized snapshot; the relay handler **defers** while it is active. 30s safety auto-clear.

---

## 9. Key files & config

| File | Role |
|------|------|
| `NetClientLoadGate.cs` | The load gate (intercept, auto-follow, client-led relay, death gate, timeouts) |
| `NetLinkState.cs` | Explicit 联机状态 (ClientLinked / HostLinked) |
| `NetManualSceneFollower.cs` | Applies host seed+used-sets and invokes `GoToLevel` under the reentry guard |
| `NetGenerationInputCapture.cs` | Host pending/finalized generation-input snapshots |
| `NetGameManagerUsedSets.cs` | Reflect/read/write the 3 GameManager used-set HashSets |
| `NetClientJoinFlow.cs` | Legacy join policy — now subordinate to `ClientLinked` |
| `NetLoadingFade.cs` | Native black fade for client-initiated loads |
| `NetHostTransitionGuard.cs` | Both-ends double-generate race guard |
| `NetSceneClassify.cs` / `NetSceneName.cs` | Hub/combat classification + name canonicalization |

Config (group `NetworkSceneAuthority` unless noted): `ClientWaitHostGenerationInputBeforeFirstLoad`, `ClientLoadGateTimeoutSeconds`, `ClientLoadGateRequestIntervalSeconds`, `EnableAutoFollowHostSceneRequest`, `EnableClientTransitionRelay`, `AllowClientInitiatedLevelLoad`, `ClientInitiatedLoadTimeoutSeconds`, `ClientGateDeathRespawnUntilHostHub`, `ClientGateDeathRespawnTimeoutSeconds`, `ClientLinkedByDefault`, `ClientUnlinkKey`, `HostLinkToggleKey`, `ManualClientSceneFollowKey`, `SyncHostUsedSetsOnManualFollow`. (`HostLinkedByDefault` is hardcoded `Fixed<bool>(true)` — retired from the `.cfg`.) Seed match: `EnableLevelSeedAuthority` / `RequireSameLevelSeedForSceneMatch` (group `NetworkLevelSeed`).

---

## 10. Known gaps / future

- Auto-join (no PageDown) in a same-named safe zone needs the host's drift check to treat a **seed** mismatch as drift so it broadcasts a request. Currently the explicit PageDown path covers it.
- A host in a boss fight can be pulled out by a client relay (no boss-active guard) — accepted because boss rooms have no exit.
- The future host/permission UI (ping/connect, "client may advance to next level" vs "client may F3 to any level") will replace config toggles; the relay already treats permission as a separate concern from map type.
