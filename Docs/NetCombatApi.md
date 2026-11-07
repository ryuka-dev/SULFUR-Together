# NetCombat — client hit results for companion mods

`SULFURTogether.Api.NetCombat` (ApiVersion **1**, introduced in phase HR-2, protocol 40) tells a mod running on a
co-op **client** what the host decided for each of the client's own hits, and tells a mod on the **host** when the
damage it sees is a remote player's.

## Why a client needs it

In a session, an enemy belongs to the host. When the client hits one, the client's own `Npc.ReceiveDamage` call is
suppressed (a Harmony prefix returns false) and the hit is sent to the host, which settles it with the game's real
`Unit.ReceiveDamage` on its own unit: crit roll, hitbox multiplier, headshot enchantment, resistances, invulnerable
parts, parry, and every `onDamageRecieved` consumer (Crusader's Holy damage, Voodoo, boss mechanics). Nothing of that
happens on the client, so a mod that reads the client's `ReceiveDamage` result or `CreateDamageLog` sees nothing, or
has to guess. These events carry the host's actual numbers back.

On the host, the remote hit runs through the real `Unit.ReceiveDamage` — the same methods a damage-feedback mod
patches — but with `DamageSourceData.isPlayer = false` and, as `sourceUnit`, the stand-in unit the host keeps for each
remote player (its `Unit.isPlayer` is false), or null while that player has none (downed, between scenes). A mod that
requires `isPlayer || sourceUnit.isPlayer` therefore normally ignores it; `IsResolvingRemoteHit` says so explicitly.
See the host section below for the cases where the two tests differ.

The host also raises the target's `Unit.onHitboxHit` after an ordinary enemy's remote hit (the game raises it after
`ReceiveDamage` on the shooter's machine), inside the same scope. A few enemy handlers that look for a player's hit
(CrawSentinel and ShavWaWarden blocking) see the stand-in's `Unit.isPlayer` raised for the duration of that one call;
a mod reading it from inside such a handler can see true there.

## Members

```csharp
namespace SULFURTogether.Api
{
    public enum HitOutcome : byte { Applied = 1, Rejected = 2, Invulnerable = 3, TargetDead = 4, Lost = 5 }

    public static class NetCombat
    {
        public static int   ApiVersion { get; }            // 1 — a property, read at run time
        public static float ResultTimeoutSeconds { get; }  // 2

        public static event Action<LocalHitForwardedArgs>? LocalHitForwarded;   // client only
        public static event Action<LocalHitResolvedArgs>?  LocalHitResolved;    // client only
        public static event Action<LocalKillCreditedArgs>? LocalKillCredited;   // client only

        public static bool    IsResolvingRemoteHit { get; } // host only (false elsewhere)
        public static string? RemoteHitPeerId { get; }      // host only (null elsewhere)
    }
}
```

### `LocalHitForwarded` — `LocalHitForwardedArgs`

Raised synchronously, on the main thread, inside the game damage call the client just suppressed — once per forwarded
damage component. A projectile with several components (base + fire + dark, ...) calls `ReceiveDamage` once per
component, and each becomes its own request. A hit the client swallows without sending (target already dead on the
host, a not-yet-bound unit) raises nothing. If the connection to the host is gone at that moment the request goes
nowhere, and the hit resolves `Lost`.

| Field | Type | Meaning |
|---|---|---|
| `RequestSeq` | int | Increasing for the life of the process; the matching `LocalHitResolved` carries it. |
| `Target` | object? | The local `Unit` hit. |
| `Weapon` | object? | The local `Weapon` (`DamageSourceData.sourceWeapon`); null for weaponless damage such as an explosion. |
| `Melee` | bool | `DamageSourceData.melee`. |
| `DamageType` | int | `DamageTypes` value. |
| `HitPart` | int | `HitboxColliders.Parts` value; 0 (None) when the damage had no hitbox. |
| `HitShapeId` | ushort | Full `ShapeId.partAndIndex` (part in the low byte). |
| `HitPosition` | Vector3? | Local world position of the hit, when the game gave one. |
| `RequestedDamage` | float | The suppressed call's damage argument — before crit, hitbox multiplier and resistances. |

### `LocalHitResolved` — `LocalHitResolvedArgs`

Exactly once per `RequestSeq`, on the main thread, in `RequestSeq` order.

| Field | Type | Meaning |
|---|---|---|
| `RequestSeq` | int | As forwarded. |
| `Target` | object? | The local unit, or null when it no longer exists locally. |
| `Outcome` | HitOutcome | See below. |
| `Damage` | float | The amount the host's `Unit.CreateDamageLog` logged for this hit: after crit, hitbox multiplier and mitigation. |
| `HealthRemoved` | float | Health this hit itself took off the host's unit (excludes `Secondary`); can be below `Damage` on a killing hit. |
| `IncomingDamage` | float | `CreateDamageLog`'s pre-mitigation figure. |
| `DamageType` | int | As settled. |
| `IsCritical` | bool | The host's crit roll (the client sends its crit chance; the host rolls). |
| `HitPart` | int | From the client's own request. |
| `HitMultiplier` | float | `Hitmesh.Data.GetShapeMultiplier()` of the part (Eye 1.5, Head 1.0, Custom 1.0, Thorax 0.75, Body/Groin 0.5, Arm/Leg 0.25, Block 0). **0 when the hit had no part** (the game itself applies 1.0 then) and on every result the host did not settle through the game (Rejected, Lost, TargetDead, a boss gate's Invulnerable). The headshot enchantment factor is applied on top inside the game and is not included. Colour by `HitPart`, or by `HitMultiplier` for `Applied` results only. |
| `Weapon` | object? | From the client's own request. |
| `Melee` | bool | From the client's own request. |
| `HitPosition` | Vector3? | From the client's own request. |
| `WasAlive` | bool | The host's unit was alive when the hit arrived. |
| `Killed` | bool | Settling this hit (its own damage or a secondary instance) killed the target. |
| `Secondary` | IReadOnlyList\<SecondaryHit\> | Further damage the game dealt to the same target inside this hit, in order — e.g. Crusader's Holy damage. Read-only, empty when none, at most 8 entries. |

`SecondaryHit`: `Damage`, `HealthRemoved`, `DamageType`, `IsCritical`, `Killed` (the death happened during that
instance — on a Crusader killing hit the hit's own damage can take the last health and the game's death check runs
inside the Holy instance, so this can be true with `HealthRemoved` 0).

Outcomes:

| Outcome | When |
|---|---|
| `Applied` | The game's `ReceiveDamage` took health off the unit. |
| `Rejected` | The host refused the request before reaching the game: wrong scene, unknown or mismatched target, invalid or out-of-range input, rate budget. |
| `Invulnerable` | The target was alive and the game's `ReceiveDamage` returned false: invulnerable unit or hitbox, Block, parry, petrified vs ranged, protected NPC, damage disabled in the game's settings, a same-faction or healing hit, a hit fully mitigated to 0 (logged with `Damage` 0), or a boss's native damage gate. |
| `TargetDead` | The host's target was already dead. A forwarded **corpse hit** resolves like this with `Damage` 0 (or `Rejected` when the host refuses it); a resulting burst shows through the game's own gib effect. Only hits that can burst a body are forwarded — on a frozen-solid corpse, or Explosive damage above 75; other hits on a body stay local and raise nothing. |
| `Lost` | No result within `ResultTimeoutSeconds` (measured past the network poll, so a client stall does not lose results already received), the host answered a later hit first, or the session ended. A later result is dropped. |

### `LocalKillCredited` — `LocalKillCreditedArgs`

At most once per enemy, on the main thread, after every hit the client sent at it has resolved — so always after the
killing hit's `LocalHitResolved`. For an ordinary enemy it also waits for the enemy's local death; for a boss it fires
from the killing hit's result (see below). Never raised from inside a session teardown.

If the killing hit itself resolved `Lost`, an ordinary enemy's credit reports an assist (`KillingBlow` false) and a
boss gets no credit.

| Field | Type | Meaning |
|---|---|---|
| `Target` | object? | The local unit, or null when it no longer exists locally. |
| `KillingBlow` | bool | One of the client's hits killed it; false for an assist. |
| `KillingRequestSeq` | int | The killing hit's seq, or -1 for an assist. |
| `Weapon` | object? | The killing hit's weapon, or for an assist the weapon of the client's last hit on it. |
| `IsHeadshot` | bool | The killing hit landed on the Head or an Eye. |
| `Experience` | int | The base experience SULFUR Together passed to the game's `DistributeExperience` for this kill (before the local player's bonus-XP multiplier); 0 when none (bosses). |
| `DamageDealt` | float | Health the client's resolved hits took off this target, secondary damage included — counted over the current engagement: hits that resolved `Lost` count 0, and the tally (with the assist `Weapon`) is forgotten after 30 s with no hit forwarded or resolved on that target. |

Who is credited follows the existing weapon-XP rule: any forwarded hit on an ordinary enemy makes its death a credit,
even if every hit came back `TargetDead` or `Invulnerable` (a kill-steal race) — `DamageDealt` is then 0. A feedback
mod that wants to skip those can check `DamageDealt > 0`, keeping in mind the 30 s window above. Bosses (boss roles, the
Emperor worm and spider) are credited from the killing hit only, and only for a unit of the game's `UnitType.Boss` — a
Witch illusion, a phase witch or a worm section dying is not a credit; a fight whose final blow lands on a unit that is
not Boss-typed raises no boss credit.

### `IsResolvingRemoteHit` / `RemoteHitPeerId` (host)

True / the session peer id while the host settles a remote peer's hit — including damage the game deals from inside it
(Crusader's Holy, Voodoo spread, thorns reflected at the remote player's stand-in).

Damage outside that scope is not always the host's. A status a client applied (poison, burning, frozen-solid's
shatter, ...) later ticks on the host outside the scope, with the client's stand-in unit as `sourceUnit` and
`isPlayer` false. Conversely, inside the scope a Crusader proc can carry the host's own player as `sourceUnit` when the
host applied that Crusader, or when the client had no stand-in at the time. To tell "the host player's damage" apart,
combine both: outside the scope **and** `isPlayer || sourceUnit.isPlayer` — and null-check `sourceUnit`, which can be
null for a remote hit.

## Guarantees and limits

- Raised on the main thread; never offline or on the host. On the host, the game's own damage call is the event.
- A subscriber that throws is logged; the other subscribers and SULFUR Together carry on.
- Per peer, requests and results travel reliable-ordered on their own channel; `RequestSeq` increases for the life of
  the game process. Other host messages (the enemy's death, its health) travel on another channel, so a `Killed`
  result can arrive while the enemy is still alive locally, or after its local death.
- Not reported: damage-over-time ticks (the host runs the status; a client never forwards a tick), damage the game
  deals to *other* units inside a client's hit (Voodoo spread to linked enemies, thorns), hits on other players
  (friendly fire has its own path), and the client's own hits on objects it owns locally.
- What the game does for the attacker at the end of a hit happens on the client, from the host's result, whether or
  not anything subscribes: right after `LocalHitResolved`, the client's own lifesteal (`Unit.OnGaveDamage` on its
  player, once per damage instance, plus one call for the Voodoo shares the hit spread to other units); just before
  `LocalKillCredited`, its kill count (`ENEMIES_KILLED` and the `enemies_killed` stat, for a killing blow or an assist with
  damage dealt), and for an ordinary enemy's killing blow the damage effects of that hit on the dead local copy —
  the game's organ roll, blood and bullet hole (also for the client's hits of the same volley that arrived after the
  death).
- Known gaps in what the host reproduces: `DamageReverser` does not put its status on a client (there is no path to
  apply a status to a remote player); enemies do not take defensive positions against a client
  (`AiAgent.TryFindDefensivePosition` reads the attacker's `isPlayer`). Voodoo spreads one share per projectile, but
  a shot whose requests reach the host spread over more than two host frames splits again.

## Migrating a companion mod

**Damage numbers.** On the client, subscribe to `LocalHitResolved`: show `Damage` for `Applied` hits (colour by
`HitMultiplier` / `IsCritical` as for a local hit) and each `Secondary` entry as its own number; ignore other outcomes.
`Target` and `HitPosition` place it; `RequestSeq` orders it. The existing `ReceiveDamage` + `CreateDamageLog` path keeps
working offline and on the host, where `IsResolvingRemoteHit` (or the existing `isPlayer || sourceUnit.isPlayer` test)
excludes remote players' damage.

**Hit and kill feedback.** On the client, play the hit sound from `LocalHitResolved` (`Applied`, headshot when
`HitPart` is Head or Eye) instead of inferring it from a suppressed `Npc.ReceiveDamage`, and the kill feedback from
`LocalKillCredited` (`KillingBlow`, `IsHeadshot`, `Experience`) instead of inferring a kill from
`DistributeExperience`. SULFUR Together still calls `DistributeExperience` on the client for a credited kill, exactly
as before; nothing else gives either side extra weapon XP.

Check `NetCombat.ApiVersion >= 1` (or the type's presence) before subscribing; keep the subscription optional so the mod
runs without SULFUR Together.
