# World Item Drop Sync (Milestone 1 — player-thrown items)

Mirrors items that appear in the world across peers, carrying their full DIY `InventoryData` (attachments /
enchantments / caliber / loaded ammo / durability + experience). Built on the audit in
**[WorldItemDropAudit.md](WorldItemDropAudit.md)**. Forward-compatible with the planned host room-setting toggle
*Shared loot* (sync every pickup) vs *Independent loot* (default — only player-thrown items synced).

## Model

- **Single chokepoint:** every dynamic world pickup is born through `InteractionManager.SpawnPickup(...)`. The
  `inventoryData != null` argument (`Pickup.DroppedByPlayer`) is the player-drop-vs-loot discriminator the mode
  filter keys on. Static scene pickups are deterministic per-scene → not synced (dedup like `Breakable`).
- **Identity:** world positions are NOT deterministic across peers (drop = local aim raycast; loot = random
  impulse + per-peer RNG), so — unlike `BreakableBreak` — we cannot match by position. Each synced pickup gets a
  composite id `{OwnerPeerId, Seq}` assigned by the dropping peer (globally unique, no round-trip).
- **Spawn = optimistic + peer-authoritative:** the dropping peer's real pickup appears instantly; we broadcast
  `WorldPickupSpawn` (same `Client→Host→relay` shape as `PlayerWeaponFire`/`BreakableBreak`); receivers
  mirror-spawn the same pickup (with the rebuilt `InventoryData`) and register it under the id.
- **Take = host-authoritative, first-come-wins:** a synced pickup's local `ExecutePickup` is blocked; a client
  asks the host (`WorldPickupTakeRequest`), the host grants exactly one and broadcasts `WorldPickupRemoved`; every
  peer removes its instance and only the named taker `AddItem`s it. This is the future Shared-loot "first picker
  takes it, it vanishes for everyone" semantics for free. The host self-grants inline (no round-trip).

## Wire payload (`NetWorldPickupSpawn`)

Manual codec (the game ships no JSON serializer; `InventoryData`/`CharacterStat` carry runtime junk). Reduced to
the fields that actually round-trip on re-pickup (`InventoryItem.Setup`): `ItemId`, `AttachmentIds[]`,
`EnchantmentIds[]`, `CaliberId`, `CurrentAmmo`, `Quantity`, `Rotated`, and the serialized attributes — only
`Durability`+`Experience` survive `ItemStats.LoadAttributesFromData`; everything else is rebuilt from
attachments/enchantments/caliber. Plus the world `Position` and scene context for gating.

## Flow

```
Dropping peer:  DropFromPlayer → SpawnPickup(data) → [postfix] CaptureLocalSpawn
                  └ assign {peer,seq}, register, broadcast WorldPickupSpawn   (real pickup stays — instant)
Receivers:      WorldPickupSpawn → MatchesScene? → [_applyingMirror] SpawnPickup(pos,item,rebuilt data) → register

Any peer takes: ExecutePickup(synced) → [prefix] TryBeginTake → BLOCK vanilla
                  ├ host:   HostGrant(self)                → broadcast WorldPickupRemoved + enqueue local removal
                  └ client: WorldPickupTakeRequest → host  → HostGrant(client) → broadcast WorldPickupRemoved
Removed:        every peer enqueues; next NetService Tick → taker AddItem(data) + RemoveInteractable+RemovePickup;
                  non-takers just remove. (Deferred to Tick so it never mutates InteractionManager lists mid-Update.)
```

`MatchesScene` (chapter|level|seed) gates cross-level spawns; `_deadKeys` drops out-of-order spawns for an
already-taken pickup; registry cleared on `GoToLevel`/`ClearLevel` (alongside the Breakable registry).

## Rest-position sync + separation (WID-2)

Spawn only carries the *initial* drop position; from there each peer runs the pickup's `Rigidbody` physics
independently, and co-op's no-pause bag dumps let drops collide and scatter locally — so the same item can rest a
short distance apart on each screen (the binding/take stayed correct, but the visible position desynced). Two
additions, both cheap:

- **Owner-authoritative settle (one-shot).** The dropping peer already owns the `{peer,seq}` id and runs the
  authoritative body. `WorldPickupManager.Tick` watches its own drops; once a body comes to rest — the game's own
  criterion, `linearVelocity² < ~0.0025` held `0.4 s`, plus an `8 s` hard cap — it broadcasts `WorldPickupSettle`
  **once** (`owner → Host → relay`, `ReliableOrdered`). Mirrors snap their instance to that position and freeze it
  (`isKinematic`, matching what the game does to a landed pickup), so it can no longer drift. Re-sent only if a
  settled drop is later shoved > `0.3 m` (explosion / a later drop landing on it) and re-settles. One tiny message
  per drop — no streaming.
- **Anti-tower separation.** Because the vanilla pause-then-fling behaviour is gone in co-op, rapid drops stack into
  one indistinguishable tower. On each real local spawn (drops + own loot; mirrors are excluded — they get the
  owner's settled position), a drop that lands amid other recent drops gets a gentle horizontal shove away from them
  (golden-angle fan when perfectly stacked), so a bag dump fans out on the ground. Local + cosmetic; for a synced
  drop the owner's settle re-syncs the resulting rest position so all peers still converge. **Single-player is
  untouched** (gated on an active co-op session — the game pauses there and drops behave).

## Mirror settle glide (WID-3)

`ApplySettle` used to *snap* a mirror pickup from wherever its own physics rested it to the owner's authoritative
position — a visible teleport, more noticeable for host-rolled shared loot whose mirror falls straight down from the
initial spawn point while the host's copy was flung. It now plays a short **local** glide instead: the body is frozen
up front (kinematic, no collisions — the same end-state the game gives a landed pickup, so physics can't fight the
move) and `WorldPickupManager.Tick` drives the transform to the target over a distance-scaled window
(`distance / 6 m/s`, clamped `0.12–0.5 s`) with an **ease-out** horizontal path plus a small sine **hop** (peaks
mid-glide, back to zero at the target — capped at `0.4 m`). Deliberately not uniform/linear. A correction under
`5 cm` still snaps (imperceptible, skips the work). Purely cosmetic and local — no protocol, authority, or wire change
(the owner still settles the one authoritative position); a re-settle simply retargets the glide from the current
spot. Single-player is untouched (no session → no settle messages → no glides).

## Files

| File | Role |
|------|------|
| `NetMessageType` `48/49/50/75` | `WorldPickupSpawn` / `WorldPickupTakeRequest` / `WorldPickupRemoved` / `WorldPickupSettle` |
| `Gameplay/NetWorldPickup.cs` | 4 DTOs (spawn payload + take + removed + settle) |
| `Gameplay/NetWorldPickupCodec.cs` | wire codecs (manual) |
| `Gameplay/WorldPickupManager.cs` | registry, capture/mirror, take arbitration, deferred removal, settle tracking + `ApplySettle`, spawn separation, `Tick`, `Clear` |
| `Patches/PickupPatches.cs` | `InteractionManager.SpawnPickup` postfix (capture + separation) / `ExecutePickup` prefix / `RemovePickup` prefix |
| `NetService.cs` | broadcast/send/handle/relay + dispatch + `Tick` drain + `LocalPeerId` |
| `NetGameplaySyncBridge` | `IsHost` / `LocalPeerId` / report methods |
| `NetHandshake` | `ProtocolVersion 3` (WID-2 added `WorldPickupSettle`) |
| `CoopConfig` `[WorldItems]` | `EnableWorldItemDropSync`, `LogWorldItemDropSync` (on), `ShareAllLoot` (off) |

## Known boundaries (v1)

- **Loot stays per-peer** (`ShareAllLoot=false`). Flipping `ShareAllLoot=true` only widens the filter to all
  pickups; true shared loot also needs **host-authoritative loot rolling** (suppress client rolls) — not yet done.
- **Room pre-check must mirror `AddItem`:** the taker's block-vs-request decision (`TryBeginTake` →
  `HasRoomFor`) faithfully replicates `ItemGrid.AddItem(isPickup:true)`'s real success predicate — accepts if the
  item is auto-consumed, fits in **either** orientation, or a free equipment slot of its type exists. A conservative
  check (default grid orientation only) was a false-negative for rotatable / equippable / consumable drops: since a
  synced pickup's vanilla take is blocked and a `false` here issues no take request, the drop was left permanently
  un-collectable on every affected peer (**issue #11**, fixed).
- **Bag full on grant:** the taker pre-checks room before requesting; if its bag fills in the (small) window
  between request and grant, `AddItem` fails on grant and the item is lost (logged), not orphaned. Rare; refine with
  a reservation later.
- **Physics divergence:** mirror pickups settle with their own physics. Player drops have no random throw force, so
  they land near-identically; acceptable (loot already diverges).
- **Take latency for clients:** one host round-trip before a client receives a contested/dropped item (host picks
  up instantly). Acceptable for low-frequency player drops.
- Reversible: `EnableWorldItemDropSync = false`.

## Validation (LogOutput115, 2026-06-25) — PASSED

Symmetric, zero errors/warnings/`bag full`/`malformed`/`no local instance`:

- 68 drops total (host 27 / client 41); each mirrored to the other peer (host 27 cap = client 27 mirror;
  client 41 cap = host 41 mirror).
- 68 takes, all host-arbitrated, exactly one taker each (39 host + 29 client); client 29 take-requests = 29
  client receives. First-picker-wins held (a contested id granted once → received once).
- `hasData=True` on every weapon/item — full DIY payload carried (`Weapon_ChimeraRapid`, `Weapon_Dirk`,
  `Attachment_ReconScope`, `Item_PizzaLarge`, `Item_Beans`, …).
