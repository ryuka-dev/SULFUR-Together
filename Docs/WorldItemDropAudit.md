# World Item Drop / Throw Sync — Reverse-Engineering Audit

**Status:** Audit complete → Milestone 1 (player-thrown items) **implemented** per the recommended
host-authoritative-take + optimistic-spawn design. Implementation doc: **[WorldItemDrop.md](WorldItemDrop.md)**.
This file is the reverse-engineering reference behind it. Goal: sync items that appear in the world (player-thrown
items/guns first; all loot later) across peers, in a way that is **forward-compatible** with the planned
host-room-setting toggle: *Shared loot* (every world item synced; first picker takes it, it vanishes for
everyone) vs. *Independent loot* (today's behaviour — each peer's loot is private; **only** player-thrown
items/guns are synced).

All types below are from `PerfectRandom.Sulfur.Core.dll` unless noted. Decompiles kept under `.scratch/decomp/`
(gitignored).

---

## 1. What can be in the world (single-player baseline)

Two and only two dynamic sources put an `Interactable` "pickup" into the world, plus one static source:

| Source | Path | Carries `InventoryData`? |
|--------|------|--------------------------|
| **Player drop** (drag out of bag / Ctrl-click / discard) | `InventoryItem.DropFromPlayer()` | **Yes** (full DIY state) |
| **Loot** (enemy death, containers, coins, ammo, global loot, loot tables) | `LootManager.*` | No (`null`) |
| **Static scene pickup** | `Pickup.staticItem` → `Pickup.Start()` → `SetupAndSpawn(...)` | No |

The dynamic ones (`Pickup`) both funnel through **one chokepoint** (see §2). Static pickups are placed
deterministically by the scene/level-gen (host-authoritative seed already synced), so — exactly like
`Breakable` destructibles — both peers already spawn the same set; they need **dedup**, not event sync.

---

## 2. The single chokepoint — `InteractionManager.SpawnPickup`

```csharp
public Pickup SpawnPickup(Vector3 position, bool motionTowardsPlayer, ItemDefinition item,
                          Room insideRoom, InventoryData inventoryData = null,
                          Container spawnedIn = null, float minPickupDelay = 0f)
```

- Pulls a `Pickup` from `AutoPool` (pooled, reused), `SetupAndSpawn(...)`, registers it in the
  interactable/moving lists, calls `LootManager.RegisterLootDropped(item)`, returns the `Pickup`.
- **Every** dynamic world item is born here:
  - `InventoryItem.DropFromPlayer()` → `SpawnPickup(pos, false, item, room, inventoryData)` — **non-null** data.
  - `LootManager.SpawnGlobalLoot / SpawnLootFrom / coins / rubber-banding ammo / …` → `SpawnPickup(... , null, ...)`.

**This is the natural place to tap for sync.** The `inventoryData != null` argument is precisely the
"player-thrown vs. loot" discriminator the future mode toggle needs:

```csharp
// Pickup.cs
private bool DroppedByPlayer => inventoryData != null;
```

> **Filter rule (forward-compatible):**
> - *Independent mode* → sync the pickup **iff** `inventoryData != null` (player drops/guns only).
> - *Shared mode* → sync **every** pickup from `SpawnPickup`.
> The same hook serves both; only the predicate changes.

---

## 3. The payload — `InventoryData` (the gun "DIY" state)

`[Serializable] PerfectRandom.Sulfur.Core.Items.InventoryData`. Built by `InventoryItem.GetSerialized()` /
`DropFromPlayer()` from `id`, `currentAmmo`, caliber, attachments, enchantments, serialized attributes.

Fields, and which ones actually round-trip when an item is rebuilt from a pickup (`InventoryItem.Setup` →
`SetupStats/SetupAttachmentsFromData/SetupEnchantmentsFromData/SetupCaliber`):

| Field | Type | Needed on wire? | Notes |
|-------|------|-----------------|-------|
| `id` | `ItemId` (`ushort value`) | **Yes** | The WeaponSO/ItemSO. Resolve via `AsyncAssetLoading.itemDatabase[new ItemId(v)]`. |
| `attachmentIds` | `ItemId[]` | **Yes** | DIY attachments. Re-applied via `AddAttachment` → rebuilds stat modifiers. |
| `enchantmentIds` | `ItemId[]` | **Yes** | DIY enchantments (oils). Re-applied via `AddEnchantment`. |
| `caliberId` | `CaliberTypes` (enum) | **Yes** | Caliber mod. Re-applied via `ChangeWeaponCaliber`. |
| `currentAmmo` | `int` | **Yes** | Loaded ammo (read live from `Weapon.iAmmoCurrent` via `UpdateAmmo()`). |
| `quantity` | `int` | **Yes** | Stack count (consumables/ammo). |
| `rotated` | `bool` | Yes (cosmetic on re-add) | Grid rotation; harmless to carry. |
| `boughtFor` | `int` | Optional | Resale price memory. **Note:** both ctors force `boughtFor = 0` regardless of arg — effectively always 0 today. |
| `attributes` | `ItemAttributeCollectionData` = `ItemAttributeData[]{ ItemAttributes id, CharacterStat value }` | **Yes (reduced)** | See below. |
| `position[2]`, `inventorySlot`, `xSize`, `ySize`, `equipped`, `selected` | ints/bool | **No** | Grid-placement bookkeeping; irrelevant to a world object. Receiver lets `AddItem` place it. |
| `identifier`, `attachments[]`, `enchantments[]`, `caliber` (strings) | legacy | **No** | Legacy-save migration only (`HANDLE_LEGACY`). Don't send. |

**Attributes round-trip is tiny.** `ItemStats.LoadAttributesFromData` only consumes **`Durability`** and
**`Experience`**, and explicitly drops `statModifiers` (rebuilt from attachments/enchantments). Everything
else (damage/spread/projectile count…) is recomputed by `Setup`. So the faithful attribute payload is just
the active `{ItemAttributes id → float BaseValue}` pairs (in practice Durability + Experience).

> **Wire format:** `InventoryData` is `[Serializable]` but the game ships **no JSON serializer**
> (no Newtonsoft / no `JsonUtility` use found in `Core.dll`), and `CharacterStat` carries runtime junk
> (`Action OnChanged`, dirty flags, modifier lists). **Do not** blind-serialize the object. Write a **manual
> codec** over the reduced field set above — it is flat and small:
> `id:u16 | attach[]:u16 | ench[]:u16 | caliber:u16 | ammo:i32 | qty:i32 | rotated:bool | attrs[]:(id:u16,val:f32)`.
> This is a strict superset of the existing `NetPlayerHeldWeapon` codec (which already sends WeaponSO id +
> attachment ids for the **visual** held model, Phase 5.6-WS2) — reuse that pattern and extend it.

---

## 4. Removal / pickup — `InteractionManager.ExecutePickup`

```
ExecutePickup(Pickup) →
   spawnedIn?.ReportItemTaken()
   PlayerBackpackGrid.AddItem(pickup.ItemSO, isPickup:true, announce:true, pickup.inventoryData)
       ├─ success → onTriggerEvents?.Invoke(); RemovePickup(pickup); return true
       └─ full    → pop item back out (impulse), re-add to _movingPickups; return false
RemovePickup(Pickup) → AutoPool.ReleaseInstance(...) or Destroy(gameObject)
```

- `AddItem(..., inventoryData)` is where the DIY state is re-hydrated into the taker's inventory
  (→ `InventoryItem.Setup(..., attachedData)`). Faithful gun transfer == passing the same `InventoryData`.
- **Auto-pickup** (coins/ammo/resources with `ItemSO.automaticPickup`) also calls `ExecutePickup` from
  `InteractionManager.Update` when the player is within `autoPickupRadius`. So "who grabbed it" must cover the
  auto path too, not just manual interact.
- For **Shared mode**, "first picker wins → vanishes for others" == a networked `RemovePickup` keyed by a
  pickup identity (§5). For **Independent mode**, a player-thrown item another peer picks up is still a real
  transfer (you can hand a gun to a friend) → same removal event applies.

---

## 5. The hard part — identity (why the Destructible trick won't work)

`Breakable` sync (Phase 5.7-BR) keys cross-peer identity on **deterministic spawn position** because level-gen
places destructibles identically on every peer. **World pickups are not deterministic across peers:**

- Player drop position = `PlayerScript.GeometryHitDirectionLooking() + up*0.1` — depends on the *local*
  player's aim and geometry raycast; never the same on a remote peer.
- Loot uses `motionTowardsPlayer` + `Quaternion.AngleAxis(Random.Range(-45,45), up)` impulse and per-peer
  loot-table RNG. Today loot is rolled **independently per peer** (documented in `Destructibles.md`: "Loot
  still spawns per-peer").

So we **cannot** match by position. Each synced pickup needs an explicit **`NetPickupId`** assigned by an
authority and carried in the spawn event, then referenced by the removal event. Two established patterns in
the codebase to model it on:

- **`HostSpawnIndex` / `NetBossDynamicSpawn` / `RuntimeSpawnManager`** — host-authoritative, monotonic id,
  host broadcasts spawn, clients mirror and bind. (Closest analogue for **shared loot**: host rolls, clients
  suppress local roll and mirror.)
- **`PlayerWeaponFire` / `BreakableBreak` relay topology** — `Client → Host → relay to other Clients`,
  `_applyingMirror` guard against echo, `MatchesScene` gating, registry cleared on level transition. Reuse
  these mechanics regardless of authority choice.

---

## 6. Architecture options (the one real decision)

A unified **`NetWorldPickup`** subsystem: registry of synced pickups keyed by `NetPickupId`, with a
mode-driven filter at the `SpawnPickup` tap (§2), a spawn event carrying the §3 `InventoryData` codec + the
authoritative world position, and a removal event (§4). The open fork is **authority**:

### Option A — Host-authoritative world pickups *(recommended for the shared-loot endgame)*
- Host owns every synced pickup and the `NetPickupId` allocation.
- Client drop → `ClientPickupDropRequest{data,pos}` → host `SpawnPickup` + assign id + broadcast `PickupSpawn`.
- Client take (manual or auto) → `ClientPickupTakeRequest{netId}` → host arbitrates first-come →
  broadcast `PickupRemoved{netId, takenBy}`; loser's local pickup attempt is rejected.
- Shared mode: host also owns loot rolls; clients **suppress** local loot spawn and mirror host (same shape
  as boss-add / runtime-spawn sync).
- **Pros:** clean "first picker wins, vanishes for all"; no double-grab; matches existing enemy/boss/runtime
  authority model. **Cons:** drop/pickup have a request→confirm round-trip (need optimistic local + reconcile,
  or a small delay); suppressing client loot is the larger lift.

### Option B — Peer-authoritative drop + arbitrated take *(lighter for the player-thrown-only first phase)*
- The dropping peer spawns its **own** real `Pickup` immediately and broadcasts
  `PickupSpawn{netId = ownerPeer+seq, data, pos}`; receivers spawn mirrors at `pos`.
- Taking a synced pickup → ask the owner/host to arbitrate → `PickupRemoved{netId, takenBy}`.
- **Pros:** instant local feel for drops; minimal change; ships the immediate "throw an item/gun" feature in
  independent mode without touching loot. **Cons:** loot has no natural owning peer (host would have to own
  loot anyway for shared mode), so it doesn't extend to shared loot as cleanly — you'd graft Option A on top
  later for loot.

**Recommendation:** build the registry + codec + filter now (shared by both), and adopt **Option A's
host-authoritative removal** as the identity backbone, but allow **optimistic local spawn** for player drops
so throwing feels instant. This ships the player-thrown feature first and drops shared-loot in later by only
flipping the filter to "all" and adding host loot-roll suppression.

---

## 7. Proposed sync flow (player-thrown items, first milestone)

```
Dropping peer:  InventoryItem.DropFromPlayer → SpawnPickup(data != null)
   └ SpawnPickup postfix: if filter(mode, pickup) → assign NetPickupId, register,
                          broadcast PickupSpawn{ netId, codec(InventoryData), worldPos }
Receivers:      HandlePickupSpawn → MatchesScene? → InteractionManager.SpawnPickup(pos,false,item,room,
                          decode(InventoryData))  [_applyingMirror guard so it isn't re-broadcast]
                          → register mirror under netId

Any peer takes: ExecutePickup(pickup) → look up netId → ClientPickupTakeRequest / host arbitrate
   └ host → broadcast PickupRemoved{ netId, takenBy }
Receivers:      HandlePickupRemoved → find local pickup by netId → RemovePickup (mirror guard)
```

Registry cleared on `GoToLevel` / `GM_ClearLevel_Pre` (same as Breakable/runtime-spawn registries).

---

## 8. Open questions / risks to resolve before coding

1. **Authority model** — Option A vs B (§6). *Primary decision.*
2. **AutoPool reuse:** `Pickup` objects are pooled and reused (`autoPoolRef`). A `NetPickupId` must live on a
   side-table keyed by the live instance, cleared on `RemovePickup`/pool-release, never baked into the prefab.
3. **Auto-pickup race:** coins/ammo get sucked up automatically within `autoPickupRadius` on *every* peer's
   `Update`; in shared mode the take must be arbitrated or two peers double-collect. (Coins/resources may be
   better left per-peer even in shared mode — confirm desired behaviour for currency.)
4. **`onTriggerEvents` / `spawnedIn` containers:** container-sourced pickups call `ReportItemTaken()` and may
   fire `onTriggerEvents`. Decide whether container contents are host-rolled (shared) or per-peer.
5. **`AddItem` failure (bag full):** `ExecutePickup` returns false and pops the item back out — the take must
   only network-remove on the *success* branch, else the item vanishes for others but stays for the taker.
6. **Quantity/stacking & partial pickups:** ammo/consumable stacks may be partially absorbed (resource caps).
   The removal event must reflect actual consumption, not assume full removal.
7. **Existing held-weapon visual (5.6-WS2)** already shows a remote's *equipped* gun model; the dropped-item
   model is the world `Pickup` sprite (billboard), independent of that — no conflict, but reuse the
   id+attachment codec.

---

## 9. Key references (verified via decompile this session)

| Type.Member | Role |
|-------------|------|
| `InteractionManager.SpawnPickup(...)` | **Spawn chokepoint** for all dynamic pickups |
| `InteractionManager.ExecutePickup(Pickup)` / `RemovePickup(Pickup)` | Take + remove |
| `Pickup` (`: Interactable`, ns `PerfectRandom.Sulfur.Core`) — `ItemSO`, `inventoryData`, `spawnPoint`, `spawnedIn`, `autoPoolRef`, `DroppedByPlayer` | World object + identity flag |
| `Pickup.SetupAndSpawn(pos, motionTowardsPlayer, item, room, inventoryData, spawnedIn, minPickupDelay)` | Init (incl. static path from `Pickup.Start`) |
| `InventoryItem.DropFromPlayer()` / `GetSerialized()` | Build `InventoryData` + drop |
| `InventoryItem.Setup(..., InventoryData)` + `SetupStats/SetupAttachments/SetupEnchantments/SetupCaliber` | Rebuild item from data on pickup |
| `InventoryData` (`Items`) ctors + fields | Payload |
| `ItemStats.SerializedAttributeData()` / `LoadAttributesFromData(...)` | Attribute round-trip = Durability+Experience only |
| `LootManager.SpawnGlobalLoot / SpawnLootFrom / RegisterLootDropped` | Loot → `SpawnPickup` |
| `AsyncAssetLoading.itemDatabase[new ItemId(v)]` | id → `ItemDefinition` on receiver |

**Prior art to reuse:** `Docs/Destructibles.md` (relay topology, mirror guard, scene gating, registry clear),
`RuntimeSpawnManager` / `NetBossDynamicSpawn` (host-authoritative spawn mirror + id binding),
`NetPlayerHeldWeapon`/`PlayerHeldWeaponManager` (id+attachment codec pattern).
