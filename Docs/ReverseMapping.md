# SULFUR Together — Reverse Mapping

**Author:** ryuka  
**Main game DLL:** `PerfectRandom.Sulfur.Core.dll` (confirmed — NOT Assembly-CSharp.dll)

> **Rules**
> - Set `Verified In Game` to `Yes` only after the probe log fires in a live session.
> - No gameplay patch may target an entry until `Verified In Game = Yes`.

| System | DLL | Namespace | Class | Method | Signature | Patch Type | Purpose | Verified In Game | Risk | Notes |
|--------|-----|-----------|-------|--------|-----------|-----------|---------|-----------------|------|-------|
| Player | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core | GameManager | AddPlayer | (Player player) | Postfix | Log player join | Yes | Low | Confirmed via probe log |
| Player | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core | GameManager | AddNpc | (Npc npc) | Postfix | Log NPC registration | Yes | Low | Confirmed via probe log |
| Player | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core | GameManager | RemoveNpc | (Npc npc) | Prefix | Log NPC removal | No | Low | |
| Player | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core | GameManager | SetState | (GameState state) | Prefix | Log game state changes | Yes | High | Confirmed via probe log |
| Player | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core | GameManager | GoToLevel | (WorldEnvironmentIds chapterSO, int levelIndex, LoadingMode loadingMode, string spawnIdentifier) | Prefix | Log level transitions | Yes | High | Confirmed via probe log |
| Player | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core | GameManager | CompleteLevel | () | Prefix | Log level completion | No | High | |
| Player | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core | GameManager | ClearLevel | () | Prefix | Log level clear | Yes | High | Confirmed via probe log |
| Player | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core | GameManager | PlayerDied | () | Prefix | Log player death event | No | High | |
| Unit | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core.Units | Unit | Spawn | () | Postfix | Log unit spawn | Yes | Medium | Confirmed; Breakable filtered by default (EnableBreakableSpawnProbe) |
| Unit | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core.Units | Unit | Die | () | Prefix | Log unit death | Yes | Medium | Confirmed via probe log; category (Unit/Npc/Breakable) derived from type name |
| Unit | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core.Units | Unit | SpawnLoot | () | Prefix | Log loot spawn from unit | Yes | High | Confirmed via probe log |
| Unit | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core.Units | Unit | SetUnitState | (UnitState state) | Prefix | Log unit state changes | No | Medium | |
| Unit | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core.Units | Unit | TeleportTo | (Vector3 position, Quaternion rotation) | Prefix | Log teleport with rotation | No | Medium | Overload 1 |
| Unit | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core.Units | Unit | TeleportTo | (Vector3 position) | Prefix | Log teleport position-only | No | Medium | Overload 2 |
| Health/Damage | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core.Units | Unit | ReceiveDamage | (float damage, DamageTypes damageType, IDamager source, Data hitbox, Nullable\<Vector3\> collisionPoint) | Prefix | Log damage received | Yes | High | Confirmed via probe log; overload 1 |
| Health/Damage | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core.Units | Unit | ReceiveDamage | (float damage, DamageTypes damageType, DamageSourceData sourceData, Data hitbox, Nullable\<Vector3\> hitPosition) | Prefix | Log damage received | Yes | High | Confirmed via probe log; overload 2 |
| Npc | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core.Units | Npc | Spawn | () | Postfix | Log NPC spawn | Yes | Low | Confirmed via probe log |
| Npc | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core.Units | Npc | Die | () | Prefix | Log NPC death | Yes | Low | Confirmed via probe log |
| Npc | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core.Units | Npc | TriggerAttackAnimation | () | Prefix | Log attack animation | No | Low | |
| Npc | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core.Units | Npc | TriggerShoot | () | Prefix | Log shoot trigger | No | Low | |
| Npc | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core.Units | Npc | SetShooting | (bool state) | Prefix | Log shooting state | No | Low | |
| Npc | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core.Units | Npc | TriggerWeaponManually | (int state) | Prefix | Log weapon trigger | No | Low | |
| Npc | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core.Units | Npc | SetAimTarget | (Unit target) | Prefix | Log aim target change | No | Medium | |
| Npc | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core.Units | Npc | HandleMeleeHit | () | Prefix | Log melee hit | Yes | Medium | Verified but too noisy — disabled by default (EnableNpcMeleeProbe) |
| Health/Damage | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core.Units | Npc | ReceiveDamage | (float damage, DamageTypes damageType, DamageSourceData source, Data hitbox, Nullable\<Vector3\> hitPosition) | Prefix | Log NPC damage | Yes | High | Confirmed via probe log |
| Npc | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core.Units | Npc | Update | () | Postfix | Throttled alive-check log | No | High | Gated by EnableNpcUpdateProbe (default off); per-NPC throttle key; extremely noisy |
| Npc | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core.Units.AI | AiAgent | UpdateTarget | () | Prefix | Throttled AI target log | No | High | Gated by EnableAiUpdateTargetProbe (default off); per-agent throttle key |
| Npc | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core.Units.AI | AiAgent | SetDestination | (Vector3 newDestination) | Prefix | Log move destination | No | Medium | Gated by EnableAiSetDestinationProbe (default off); per-agent throttle key; overload 1 |
| Npc | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core.Units.AI | AiAgent | SetDestination | (Unit unit) | Prefix | Log chase target | No | Medium | Gated by EnableAiSetDestinationProbe (default off); per-agent throttle key; overload 2 |
| Npc | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core.Units.AI | AiAgent | SetNavMeshAgentState | (bool state) | Prefix | Log NavMesh enable/disable | No | Low | Gated by EnableAiNavMeshStateProbe (default off) |
| Npc | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core.Units.AI | AiAgent | SetCanMove | (bool state) | Prefix | Log movement lock | No | Low | Gated by EnableAiCanMoveProbe (default off) |
| Npc | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core.Units.AI | AiAgent | GetTarget | () | Postfix | Log current AI target | No | Low | Gated by EnableAiTargetProbe (default off); fires only on target change |
| Unit | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core.Units | UnitManager | AddUnit | (Unit unit) | Postfix | Log unit registration | No | Low | |
| Unit | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core.Units | UnitManager | OnUnitDeath | (Unit unit) | Prefix | Log death event routing | Yes | Low | Confirmed via probe log |
| Unit | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core.Units | UnitManager | GetAllNpcs | (bool includeDead) | Postfix | Log NPC list query | No | Low | |
| Item/Loot | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core.Items | LootManager | RegisterLootDropped | (ItemDefinition item) | Prefix | Log loot registration | Yes | High | Confirmed via probe log |
| Item/Loot | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core.Items | LootManager | OnNewLevel | () | Prefix | Log loot reset on level | No | High | |
| Item/Loot | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core.Items | LootManager | ClearOnNewLevel | () | Prefix | Log loot clear | No | High | |
| Item/Loot | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core.Items | LootManager | SpawnGlobalLoot | (multiple overloads) | Prefix | Log global loot spawn | No | High | Enumerate overloads at runtime |
| Item/Loot | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core.Items | LootManager | SpawnLootFrom | (multiple overloads) | Prefix | Log loot spawn from source | Yes | High | Confirmed via probe log; enumerate overloads at runtime |
| Item/Loot | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core | InteractionManager | ExecutePickup | (Pickup pickup) | Prefix+Postfix | Log pickup execution | Yes | High | Confirmed via probe log |
| Item/Loot | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core | InteractionManager | RemovePickup | (Pickup pickup) | Prefix | Log pickup removal | No | Medium | |
| Item/Loot | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core | InteractionManager | SpawnPickup | (Vector3, bool, ItemDefinition, Room, InventoryData, Container, float) | Prefix+Postfix | Log pickup spawn | Yes | High | Confirmed via probe log |
| Inventory | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core.Items | InventoryItem | GetSerialized | () | Postfix | Log serialized form | No | Low | |
| Inventory | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core.Items | InventoryItem | DropFromPlayer | () | Prefix | Log item drop | No | High | |
| Inventory | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core.Items | InventoryItem | DestroyFromInventory | () | Prefix | Log item destroy | No | High | |
| Inventory | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core.Items | InventoryItem | TryMoveToPlayerInventory | () | Prefix | Log inventory move | No | High | |
| Inventory | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core.Items | InventoryItem | TransferOwnership | (Unit unit, bool isTransaction) | Prefix | Log ownership transfer | No | High | |
| Inventory | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core.Items | InventoryItem | Setup | (multiple overloads) | Postfix | Log item setup | Yes | Medium | Confirmed; very noisy — count-only by default (EnableVerboseInventoryProbe) |
| Scene/Level | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core.LevelGeneration | NextLevelTrigger | MakeTransition | () | Prefix | Log transition start | No | High | |
| Scene/Level | PerfectRandom.Sulfur.Core.dll | PerfectRandom.Sulfur.Core.LevelGeneration | NextLevelTrigger | OnTriggerEnter | (Collider collider) | Prefix | Log collider enter | No | Low | |
| Scene/Level | Assembly-CSharp.dll | LevelGeneration | SpawnPlayerNode | Execute | () | Prefix+Postfix | Log player spawn node | Yes | Medium | Confirmed via probe log |
| Scene/Level | Assembly-CSharp.dll | LevelGeneration | SpawnPlayerNode | SpawnPlayer | (Unit prefab, int playerIndex, Rect viewport, LevelGenerationContext fullContext) | Prefix+Postfix | Log player spawn | Yes | Medium | Confirmed; returns void — no __result in postfix |
| Scene/Level | Assembly-CSharp.dll | LevelGeneration | SpawnEnemiesNode | Execute | () | Prefix+Postfix | Log enemy spawn node | Yes | Medium | Confirmed via probe log |
| Scene/Level | Assembly-CSharp.dll | LevelGeneration | SpawnEnemiesNode | CreateAndRegisterEnemy | (Transform unitRoot, Vector3 spawnPosition, Room inRoom, Unit prefabComp) | Prefix+Postfix | Log enemy creation | No | Medium | |
| Scene/Level | Assembly-CSharp.dll | LevelGeneration | FinalizeAndMutateUnitsNode | Execute | () | Prefix+Postfix | Log finalize node | Yes | Medium | Confirmed via probe log |
| Scene/Level | Assembly-CSharp.dll | LevelGeneration | FinalizeAndMutateUnitsNode | RegisterAndSpawnUnit | (LevelGenerationContext fullContext, Npc npc, Room inRoom) | Prefix+Postfix | Log unit registration | Yes | Medium | Confirmed via probe log |
| Scene/Level | Assembly-CSharp.dll | LevelGeneration | SetupLootNode | Execute | () | Prefix+Postfix | Log loot setup node | Yes | Medium | Confirmed via probe log |

---

## Notes

**BatchedNPCRaycasts:** AiAgent likely uses batched raycasts (e.g., `Physics.RaycastNonAlloc`) internally. Do not patch raycasting helpers directly — probe only the high-level AI methods (UpdateTarget, SetDestination, GetTarget). Patching low-level Physics calls from multiple agents simultaneously would cause severe performance issues.

## Phase 4.0.0-A Structured Gameplay Probe Mapping

The following already-targeted hooks now also feed `NetGameplayProbeManager` for local-only structured logging:

| Source hook | Probe event | Notes |
|---|---|---|
| `Unit.Spawn` | Spawn candidate | Duplicate-safe; ignores Player and Breakable categories in gameplay probe. |
| `Npc.Spawn` | Spawn candidate | Main NPC spawn confirmation hook. |
| `UnitManager.AddUnit` | Spawn candidate | Registration-order candidate. Useful for comparing spawnIndex stability. |
| `SpawnEnemiesNode.CreateAndRegisterEnemy` | Spawn candidate | Level-generation enemy creation source. |
| `FinalizeAndMutateUnitsNode.RegisterAndSpawnUnit` | Spawn candidate | Level-generation final registration source. |
| `Unit.ReceiveDamage` | Damage candidate | Counted; individual logs default off. |
| `Npc.ReceiveDamage` | Damage candidate | Counted; individual logs default off. |
| `Unit.Die` | Death candidate | Log-only. |
| `Npc.Die` | Death candidate | Log-only. |
| `UnitManager.OnUnitDeath` | Death candidate | Death routing candidate. |
| `GameManager.GoToLevel` | Probe clear | Clears local entity table and spawn index. |
| `GameManager.ClearLevel` | Probe clear | Clears local entity table and spawn index. |

Important:
- `NetGameplayEntityId` is not authoritative.
- The reflected `UnitIdentifier` / `AsGlobalId` values are investigation candidates only.
- Do not use these values to sync gameplay until Host/Client logs prove stability under matching seed.

## Phase 4.0.0-B Host Enemy Death Event Mirror Mapping

Death mirror source:
- The network event is emitted from `NetGameplayProbeManager.ReportDeath` after local death de-duplication.
- Existing hooks that can feed the same death are still `Unit.Die`, `Npc.Die`, and `UnitManager.OnUnitDeath`, but only the first observed death for a local entity produces one mirror event.
- The mirror is limited to `Category == Npc`.

Client matching order:
1. Same scene and known equal seed.
2. Same `spawnIndex` and position distance within `EnemyDeathMirrorPositionTolerance`.
3. Unique `UnitGlobalId` fallback when available.
4. Unique candidate key fallback.

This mapping remains investigation-only. Do not call `Npc.Die`, `Unit.Die`, `UnitManager.OnUnitDeath`, or loot/drop methods from a received network event until a later phase explicitly proves the id mapping is stable and adds a safe apply path.

## Phase 4.1.0-A observation note — enemy AI/state

Directly adapting enemy AI is not attempted yet. The current safe path is to observe Host enemy state drift before patching AI target selection or movement. The reason is that AI target selection, pathing, attack windows, and damage application may live in separate classes/methods, while the current remote player proxy is intentionally not a real gameplay Player.

Implemented bridge:

- Host collects already-probed NPC snapshots from `NetGameplayProbeManager`.
- Client matches by spawnIndex first, with existing candidate key fallbacks.
- Client records position drift and dead/alive divergence.
- No transform application or AI suppression occurs in this phase.

If logs show stable matching and meaningful drift, the next investigation should focus on the smallest safe Client-side AI suppression or Host transform application entry point rather than registering remote visual proxies into gameplay systems.

## Phase 4.1.0-B transform mirror note

This phase intentionally does not add new Harmony patches. It uses runtime object references already captured by the structured gameplay probe and applies transform targets through Unity `Transform` access on matched local NPC objects.

Reasoning:
- The smallest safe enemy AI hook is still unknown.
- The remote player proxy must remain a visual-only object and must not be registered as a gameplay Player/Unit.
- Logs showed death-event application can fail when Client AI has moved the same spawnIndex enemy far from Host position.

Future AI work should investigate high-level AI target/path methods before suppressing or replacing local enemy logic:
- `AiAgent.GetTarget`
- `AiAgent.SetTarget`
- `AiAgent.TryFindValidTarget`
- `AiAgent.UpdateMovement` / `AIUpdate`
- `AiSensor.ScanForTarget`
- `Npc.ShootAt` / melee attack methods

Do not patch low-level physics or raycast helpers as a synchronization strategy.

---

## Phase 5.3–5.6 — Level Transition & Generation Reverse Map

All from `PerfectRandom.Sulfur.Core.dll` via ilspycmd; verified in game. This is the foundation for the scene/join/link-state system (see **SceneTransitionAndLinkState.md**).

**Transition chain (GameManager):**

| Method | Signature | What it does | Used by the mod |
|--------|-----------|--------------|-----------------|
| `GoToLevel` | `(WorldEnvironmentIds chapterSO, int levelIndex, LoadingMode loadingMode, string spawn="")` | Chapter-level entry. `StartCoroutine(SwitchLevelRoutine(...))` | **bool prefix** = the client load gate; also where the host-transition guard + main-menu link reset hook |
| `SwitchLevelRoutine` | `IEnumerator (chapterSO, levelIndex, loadingMode, spawn="")` | **Authoritative** transition entry (all paths route here). Teardown: `DisableAllUnits`, `UnloadUnits`, `Destroy(PlayerObject)`, `LoadingFade(true)`, then `StartLevelRoutineGraph(...)` | prefix captures the real transition target (boss special jumps too); host-transition guard |
| `StartLevelRoutineGraph` | `IEnumerator (chapterSO, levelIndex, loadingMode, spawn)` | Sets `currentLevelIndex=levelIndex`; clears `usedUniqueEventThisEnvironment` only when `levelIndex==0` (and run sets at ChurchHub level 0); reads `GlobalSettings.ForceLevelSeed`; `MakerGraphContext.ExecuteGraph` | prefix = pending generation-input snapshot |
| `CompleteLevel` | `() void` → `OnCompleteLevelRoutine()` | In-run sub-level advance. `OnCompleteLevelRoutine`: `SetState(Cinematic)` → wait 0.5s → `SetState(Loading)` → `currentLevelIndex++` → `SwitchLevelRoutine(currentEnvironment.id, currentLevelIndex)` (or `GetNextEnvironment()` at 0 on overflow) | **bool prefix** = client-led relay intercept; host-transition guard latch (Cinematic window) |
| `GetNextEnvironment` | `() WorldEnvironmentIds` | Next env in the current act, or `ChurchHub` at act end | used to compute the CompleteLevel relay target |
| `GoToMainMenu` | `(bool preserveInventory)` → `GoToMainMenuRoutine` | Loads the **separate** `Scenes/MainMenu.unity` (NOT ChurchHub) | prefix = reset the client's 联机状态 to default |
| `currentEnvironment` / `currentLevelIndex` | props | current run position | reflected to compute relay targets |

**Level-exit trigger:**

| Class | Member | Behaviour |
|-------|--------|-----------|
| `NextLevelTrigger` | `OnTriggerEnter` (one-shot `triggered`) → `MakeTransition()` | `specificEnvironment==None` → `CompleteLevel()`; `==ChurchHub` → `GoToChurchHub`; else → `GoToLevel(specificEnvironment,0,Normal,spawn)` |

**Loading fade (UI):**

| Class | Member | Behaviour |
|-------|--------|-----------|
| `UI.UIManager` | `LoadingFade(bool state, LoadingMode=Normal)` | `state` → `fadeEffect.FadeOut(loadingMode)`, else `FadeIn()` |
| `UI.LoadingFade` | `FadeOut(LoadingMode)` → `FadeOut(Color.black)` | sets animator bool `IsFadedOut=true`; `FadeIn()` sets it false. Single bool, self-clears at level-gen completion |
| `UI.UIManager` | `loadingOverlay` (field, `LoadingOverlay`) | `SetState(UIState.Shown/Hidden)`, `SetText(string,string="")` — the loading art/hints overlay |

**Generation determinism:** a level = `graph (MakerSet name)` + `seed (MakerGraphContext.Seed)` + the three GameManager used-sets (`usedChunksThisRun`, `usedUniqueEventThisRun`, `usedUniqueEventThisEnvironment`, all `HashSet`). `GoToLevel`'s `levelIndex` arg is often 0 for chapter entry; the real graph/level/seed must be read from the `MakerGraphContext` at generation time, not from `GoToLevel`.

**Witch death crash entry (5.4-G7):** `EquipmentManager.AmuletHoldable` getter → `GetHoldableInSlot` → `equippedItems[InventorySlot.Amulet]` throws `KeyNotFoundException` on a client with no Amulet slot key — reached via `WitchDeath`. The client's `WitchDeath` is prefix-blocked and replaced with a safe reflection replica.

---

## Player Input / Locks / Downed Controls

Used by the co-op downed/revive system (see **PlayerLifeAndDownedInput.md**). All from `PerfectRandom.Sulfur.Core.dll`.

**`GameManager.PlayerLocks`** — a `[Flags]` enum gating gameplay systems (checked via `GameManager.HasLock(...)`, set via `GameManager.AddLock(PlayerLocks, bool)`):

| Member | Value | Gates (HasLock checked in) |
|--------|-------|----------------------------|
| `None` | 0 | — |
| `Camera` | 1 | `InputReader.Update` camera look |
| `Interaction` | 2 | interaction; inventory-open (with Inventory); player crouch enablement |
| `Weapon` | 4 | shoot, ADS, reload, melee, weapon switch (`Weapon`/equipment code) |
| `PlayerMovement` | 8 | movement controller |
| `Inventory` | 0x10 | opening the backpack (`InventoryUI.Update`) |
| `UseHUD` | 0x20 | HUD use |

> Note: `LockStatePadlock` (Inventory/DevTools/Loading/Cinematic/Paused/Dialog/Vehicle/Tutorial/Amulet/Flashback/HoldingInteract) is a SEPARATE enum used for controller/cursor/pause locks (`ModifyControllerLock` / `ModifyCursorState` / `ModifyGamePauseState`). Don't confuse it with `PlayerLocks`.

**Input system** — Unity Input System asset `PlayerInputActions` with maps `OnFoot`, `UI`, `FKeys`, `DevTools`, `Loading`, `Cinematic`. `InputReader : CharacterInput` subscribes the handlers:

| Action | Map | Handler | Gating |
|--------|-----|---------|--------|
| `TogglePause` | OnFoot | `InputReader.PauseMenu` → `GameManager.PauseGame()` | NOT lock-gated; `PauseGame` needs `gameState==Running && !IsPausePrevented` |
| `F3DevTools` | **FKeys** | `InputReader.DevToolsToggle` → `DevToolsManager.Toggle` | needs `GameManager.DeveloperMode` |
| `Look` | OnFoot | `InputReader.LookPerformed` | `HasLock(Camera)` |
| `Movement` | OnFoot | polled `Get*MovementInput` | `HasLock(PlayerMovement)` |
| weapon slot select | OnFoot | `SelectSlot1-5` / `SelectNext/PreviousSlot` / `SelectByScroll` / `SelectLastUsedWeapon` | — (InputReader callbacks) |
| `Crouch` / `ToggleCrouch` | OnFoot | `Movement.ExtendedAdvancedWalkerController.UpdateCrouching` → `ToggleCrouch(bool)` | not an InputReader callback |
| `OpenInventory` | OnFoot | polled in `InventoryUI.Update` | `HasLock(Inventory)` |

`GameManager.PauseGame(showMenu=true)`: only acts when `!IsPausePrevented && gameState==Running`; shows `pauseMenu`. `IsPausePrevented` = `pausePreventedBy.Count > 0`, populated by `ModifyGamePauseState`. `InputReader.PauseMenu` calls `ExitFromTransition()` instead when `awaitingStartLevel`.

`ExtendedAdvancedWalkerController` (`PerfectRandom.Sulfur.Core.Movement`): `bool isCrouching`; `public void ToggleCrouch(bool)` sets the crouch animators + collider heights; `private void UpdateCrouching()` reads `OnFoot.Crouch`/`ToggleCrouch`. Forcing `ToggleCrouch(true)` = the downed crouch pose.
