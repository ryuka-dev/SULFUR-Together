# Emperor Boss Audit — Phase 1 Worm

Reverse-engineered from `PerfectRandom.Sulfur.Gameplay.dll` via ilspycmd. This is the deep reference for
the Emperor phase-1 worm sync. It extends the 10-point summary in
[BossSourceAudit.md](BossSourceAudit.md) §6 with the full mechanic, the RNG sources, and the resulting
sync design. **No sync feature code is written from this audit** — the immediate deliverable is the
enhanced probe (`EmperorWormDiagnostics`); the sync pipeline comes after the probe validates the model on
real host+client logs.

The Emperor is a two-phase encounter: **Phase 1 = the worm** (this doc), **Phase 2 = the spider**
(`EmperorBossSpider`, deferred — see §9). Method/field names are exact; line refs are into the decompiled
bodies.

---

## 1. Entities and ownership

| Type | Namespace | Role |
|------|-----------|------|
| `EmperorBossFightHelper` | `PerfectRandom` | Thin orchestrator. `Start` subscribes `GameManager.onPlayerSpawned`. On spawn → `StartPhase1()` (deactivate spider parent) or `StartPhase2()` (activate spider). |
| `EmperorBossWorm` | `PerfectRandom.Sulfur.Gameplay` | The worm "head"/root: Rigidbody physics body, movement driver, section manager, weakpoint/health owner, death sequence, phase-2 handoff. |
| `EmperorWormSectionController` | `PerfectRandom.Sulfur.Gameplay` | One per body section. Owns the section `Npc`, `SetInvulnerability(bool)`, and a physics-trigger `BelowGround` flag (`AboveGroundTrigger`). |

The worm is **pre-placed in the scene** under `phase1Root` (not spawned by a dialog/TriggerFight). Its
`StartMovement()` is what makes it active. The body `npc` (the head) is permanently invulnerable; damage
only ever lands on the vulnerable tail section (see §5).

### Start path (no dialog gate in the boss code)
`EmperorBossFightHelper.OnPlayerSpawned` (fires from `GameManager.onPlayerSpawned`) → `StartPhase1()`.
There **is** a scripted pre-fight NPC dialog, but that is a separate scripted trigger, not part of the
worm/helper code — its client-input freeze is out of scope of the boss adapter (same as the other bosses,
see [BossPreFightFlow.md](BossPreFightFlow.md)). `EmperorBossWorm.StartMovement()` is the real activation:
it plays music, captures the player transform, `await Initialize()` (spawns sections), then kicks the
first `JumpTo`.

> **Double-worm reality (the core sync problem):** both host and client load the same scene, so both run
> `EmperorBossFightHelper.OnPlayerSpawned` → both spawn their own worm and both run `StartMovement`. The
> movement is physics + RNG + local-player-position driven (§4), so the two worms **diverge immediately**.
> This is why the worm position/section-state must be host-authoritative; the client worm's autonomous
> drive has to be suppressed and mirrored. (`EmperorWormDiagnostics` already ships a reversible
> client-suppression scaffold on `StartMovement`, default off.)

---

## 2. Section spawn — `Initialize()` (async, `:289`)

Runs inside `StartMovement` (`await Initialize()`). Deterministic *count*, non-deterministic *runtime
positions*:

- `numberOfSections` — the serialized field *default* is `5`, but the real Emperor asset ships **10**
  (Log216: `numberOfSections=10 spawned=10 vulnerableIndex=9`; 9× `ShavwaEmperorWormSection` + 1×
  `ShavwaEmperorWormSectionVulnerable`). So `healthPerSection = 1/(10-1) ≈ 0.111` and the bar shows 9
  chunks. Loop `k = 0..numberOfSections-1`:
  - `createVulnerableSection = k >= numberOfSections-1` → **only the last section (k=4) is vulnerable.**
  - `unit = await (vulnerable ? wormSectionVulnerableUnitSO : wormSectionUnitSO).GetAsset().SpawnUnitAsync(this, spawnPos)`.
    - **Two distinct `UnitId`s**: `wormSectionUnitSO` (invulnerable body) and `wormSectionVulnerableUnitSO`
      (tail). `mono` owner = the worm (`this`).
  - `unit.name = "WormSection_{k}"`; added to `wormNpcs` (List&lt;Unit&gt;), `wormSections` (List&lt;Transform&gt;),
    `targetPositions`, `sectionControllers`.
  - Collisions between the worm body and every section, and between sections, are disabled
    (`Physics.IgnoreCollision`).
  - `spacerVisualPrefab` instantiated between adjacent sections (visual connectors → `spacers`).
  - Invulnerable sections → `SetInvulnerability(true)`. The vulnerable tail →
    `SetInvulnerability(false)`, `lastActiveIndex = k`, `lastSectionNpc = unit`, subscribe
    `onDamageRecieved += WeakpointHit`, `AttachToBossUI(true)`, `onDeath += OnDeath`,
    `healthPerSection = 1/(numberOfSections-1)` = **0.25**, `lastSectionSprite = controller.sprite`.

**Spawn implications for sync:** 5 sections, spawned through `SpawnUnitAsync(this, …)` — i.e. the same
`UnitSO.SpawnUnit` chokepoint the RT3 boss-add pipeline already hooks (mono = the worm). Count is fixed and
the order is deterministic (index = spawn order), so sections can be bound host↔client by **spawn sequence
index**, not by position (positions diverge). The vulnerable one is always the last spawned. See §7.4.

---

## 3. State model (fields that matter for authority)

| Field | Meaning |
|-------|---------|
| `bossActive` / `BossActive` (getter gates on `activateDelay`) | worm is driving movement |
| `rb` (`Rigidbody`), `col` | physics body; `rb.isKinematic=false` while moving |
| `isUnderground` | worm is below ground (between a ground-hit and the next emerge) |
| `isTravelingUnderground` | underground travel visual phase active |
| `currentJumpCount` | jumps taken in the current "random jumps before targeting player" cycle |
| `canDoNextJump` | gate set true on ground hit, false on jump launch |
| `hitGroundYValue` | Y threshold that triggers `HitGround` on descent |
| `targetPosition` | current jump destination |
| `lastSectionNpc` | the single vulnerable tail Npc — **health owner + boss-bar unit** |
| `lastActiveIndex` | index of the vulnerable section within `wormSections` (moves up as sections die) |
| `wormSections` / `wormNpcs` / `sectionControllers` / `spacers` | the body (shrinks on section death) |
| `jumpsBeforeTargetingPlayer` | random-jump budget; shrinks with health |
| `chosenRagePath` / rage fields | scripted path mode below 0.8 health |
| `isDead` / `inDeathAnimation` | terminal flags |

---

## 4. Movement model — the hard part

Ballistic + physics + RNG. **Nothing here is reproducible on the client** without the host's inputs.

### 4.1 Jump launch — `JumpTo(Vector3 targetPosition, float jumpTime)` (`:698`)
Computes a projectile launch velocity to reach `targetPosition` in `jumpTime`, writes
`rb.linearVelocity = v * 1.04f`, faces the worm along the horizontal direction, snaps all sections behind
the head along `-forward`, emits ground-exit particles, sets `isUnderground=false` and
`hitGroundYValue = targetPosition.y`. `jumpTime` = `jumpTimeRelativeToHealth.Evaluate(1 - lastSectionNpc.normalizedHealth)`
(curve → jumps change with health).

### 4.2 Per-frame physics — `FixedUpdate()` (`:423`)
- Bails while `rb.isKinematic || isDead || inDeathAnimation`.
- Fail-safe: if it falls &gt;100 below `startPos`, reset to `startPos` and re-target.
- On descent past `hitGroundYValue` → `HitGround()`.
- Adds a **health-scaled** extra gravity/velocity: `jumpCustomTimeScale.Evaluate(1 - normalizedHealth)`
  (worm speeds up as it loses health).
- **Homing:** when above ground, no rage path, and `currentJumpCount==0`, adds a force toward the player
  (`homingForce`-ish, scaled by distance). → jump destinations depend on the **local player position**.
- Ground/underground particle + flying-sound state gated by `AllSectionsAboveGround`/`AllSectionsBelowGround`
  (which read each `sectionController.BelowGround` — physics trigger driven, §6).
- Calls `UpdateWormSections()` (§4.5).

### 4.3 Ground hit → underground → emerge
`HitGround()` (`:788`): capture entry point, `SetExitPoint(entry)`, `StartUndergroundTravel()`, sound,
`isUnderground=true`. `UpdateUndergroundTravelVisuals()` (`:745`) advances a lerp from entry to exit; when
the travel completes (or `undergroundDelay` elapses) it teleports the worm to `groundExitPoint` and calls
`JumpToNextTarget()` (`:544`).

### 4.4 Target selection — `JumpToNextTarget` / `SetExitPoint` (RNG-heavy)
- **`GetRandomPointAroundPlayer()`** — `Random.Range` angle around player, radius `randomPointAroundPlayerRadius`.
- **`TryFindValidJumpPosition()`** — up to 3 random points + `Physics.SphereCast` validation.
- **`SetExitPoint()`** — up to 8 `GetDownwardBiasedDirection()` (`Random.onUnitSphere`) + spherecast to
  pick where the worm re-emerges.
- **Rage paths** (below 0.8 health): `ragePaths` are pre-placed `Transform` sequences; `GetClosestRagePath`
  picks one and the worm follows its nodes (`TakeRagePosition`). `maxRageJumps`, `rageJumpCurrentCount`
  (halved below 0.5 health).

**RNG sources that must be host-authoritative** (client cannot reproduce): every `Random.*` call above,
plus the physics integration itself (velocity, ground contacts) and the dependence on **local player
position** (each client has a different local player). → the worm's transform is fundamentally host state.

### 4.5 Section follow — `UpdateWormSections()` (`:477`)
Each section lerps to trail the previous one at `sectionSpacing` behind `root.position`; spacer connectors
interpolate between adjacent sections biased by the local player direction; the tail sprite is oriented.
**This is a pure visual follow derived from `root.position` (+ local player pos for spacer bias).** If the
client is given the head transform, it *can* run this locally — but the spacer bias and per-section
`BelowGround` still reference the local player, so a faithful mirror should stream at least the head
transform + velocity and let the follow run, or stream section transforms directly (bandwidth trade-off,
§7.2).

---

## 5. Damage / health / section-destruction — `WeakpointHit` (`:879`)

The **only** damage entry for the worm. Subscribed to `lastSectionNpc.onDamageRecieved` (the tail). The
head `npc` and all non-tail sections are invulnerable, so every player hit that "counts" reaches this one
Npc.

Mechanic:
1. If `lastSectionNpc.GetNormalizedHealth() <= 0.001` → `isDead=true`, start `DeathAnimation()` (§6).
2. Section-destruction gate: `num = floor((1 - normalizedHealth)/healthPerSection)` vs
   `num2 = numberOfSections - lastActiveIndex - 1`. There is a **health-floor top-up** (re-adds health so
   the tail can't be over-killed past the current section) and a 1s `invincibilityTimer` between
   destructions.
3. When health has crossed enough section thresholds (`num6 > num2`): for each crossed step →
   `DestroySection(lastActiveIndex - 1)` + `MoveVulnerableSectionUp()`, set `invincibilityTimer = now + 1`.
4. Recompute `jumpsBeforeTargetingPlayer = max(1, floor(startingJumps * normalizedHealth))` and
   `rageJumpCurrentCount` (halved below 0.5).

`DestroySection(index)` (`:938`): white-flash all sections, frag explosion + gib set at the section,
`Npc.Die()` on that section, mark hitboxes invulnerable, remove from `wormSections`/`sectionControllers`,
destroy its spacer. `MoveVulnerableSectionUp()` teleports the vulnerable tail's transform up to fill the
gap and decrements `lastActiveIndex`.

**Key authority facts:**
- **Single health owner:** `lastSectionNpc.Stats(92)` is the whole worm's health. `healthPerSection=0.25`
  means the 5-section worm shows 4 "chunks" (numberOfSections-1) of the bar, each destroy = one chunk.
- **Section destruction is host-derived from health crossings** — but the health-floor top-up + 1s
  invincibility make it stateful. To stay in lockstep, the host should **broadcast explicit
  `DestroySection(index)` events** rather than letting the client re-derive from health (RNG in gibs, and
  the top-up math is fragile to reproduce).
- **Client→host damage target:** the client's local vulnerable tail → route to the host's `lastSectionNpc.ReceiveDamage`
  (single target — much simpler than Witch/Lucia multi-unit). Reuse `BossDamageAuthority`.
- The bar attaches to `lastSectionNpc` once (in `Initialize`) and stays there (the tail Npc persists; only
  its transform teleports up), so bar sync = stream `lastSectionNpc` health.

---

## 6. Ground state, death, phase-2 handoff

- **`BelowGround` per section** (`EmperorWormSectionController`): set by `OnTriggerEnter/Exit` with a
  collider tagged `Trigger` named `AboveGroundTrigger`. Drives only sound/particle gating
  (`AllSectionsAboveGround/BelowGround`) and `isUnderground` visuals — **not** vulnerability (that's the
  mesh + `npc.SetInvulnerable` set once per section). So for sync it's cosmetic; if the client mirrors the
  head transform through the same trigger volume it derives locally. Worth sampling in the probe to confirm.
- **Death:** `WeakpointHit` at ~0 health → `DeathAnimation()` (`:1009`): stop sounds/music, lerp to
  `roomCenter`, shake + fog blink, slam into the ground, frag explosion + gibs, disable/enable the death
  object lists, `lastSectionNpc.AttachToBossUI(false)`, `npc.Die()`, `CleanupDestroyedSections()`,
  **`emperorBossFightHelper.StartPhase2()`**, `Destroy(gameObject)`. → this is the phase-1→phase-2 seam.
- `OnDeath(unit)` (`:870`, the tail's `onDeath`) only disables `OnTriggerSpawn`s.

**Death sync:** `Stats.SetStatus(92,0)` does not run this — the host must drive the real lethal
`WeakpointHit`/death and broadcast a terminal event; the client plays its own `DeathAnimation` (visual) and
takes the `StartPhase2()` handoff from the host so both sides transition together. Reuse the terminal-death
pattern from Lucia/Cousin/Witch (`_terminalDead` suppression).

---

## 7. Sync design (host-authoritative) — the plan the probe must validate

### 7.1 Authority invariants (must NOT be client-decided)
- Worm head transform + rigidbody velocity (all movement RNG + physics + player-relative homing).
- All jump targets / rage-path choices / exit points.
- `lastSectionNpc` health (Stats 92) and every section-destruction event + `lastActiveIndex`.
- `isUnderground` / phase transition (`StartPhase2`) / death.

### 7.2 Position streaming (the expensive channel)
Two candidate designs — probe divergence data decides:
- **(A) Stream head transform + velocity**, client runs `UpdateWormSections` locally. Cheapest; risk =
  spacer bias / `BelowGround` reference the local player, minor cosmetic drift. Needs the client worm's
  autonomous physics **fully suppressed** (block `FixedUpdate` movement + `JumpTo`), keeping only the
  section-follow + visuals.
- **(B) Stream every section transform.** Heavier, exact. Fallback if (A) drifts visibly.
Start with (A); the probe's position-divergence timeline (§8) tells us the drift magnitude and whether
`BelowGround`/sound gating stays acceptable. → **(A) shipped as EMP-3a (§8.6)** — head-only stream, client
kinematic, colliders disabled (visual + one hittable weakpoint), snapshot-interpolated.

### 7.3 Client suppression
Extend the existing `EmperorWormDiagnostics` scaffold: on the client, block the autonomous movement (already
blocks `StartMovement`; for design (A) we instead let `Initialize` run — to get the local sections to mirror
onto — but block the *movement* driver: `JumpTo` / the `FixedUpdate` force integration / `JumpToNextTarget`).
Keep `UpdateWormSections` + visuals. Never `Destroy` locally except via a host death/destroy event.

### 7.4 Section binding + lifecycle
- Bind host↔client sections by **spawn sequence index** (deterministic, positions diverge). The sections go
  through `SpawnUnitAsync(this,…)` → the RT3 boss-add manifest chokepoint (`UnitSO.SpawnUnit`, mono = the
  worm). Confirm in the probe that both ends spawn exactly 5, same two UnitIds, same order.
- Host broadcasts `DestroySection(index)` events; client mirrors (destroy its bound section + move its
  vulnerable tail up) instead of re-deriving from health.

### 7.5 Damage routing
Client hit on its local vulnerable tail → `ClientBossHitRequest` (role = worm tail / single target) → host
`lastSectionNpc.ReceiveDamage` → native `WeakpointHit` advances → host broadcasts health + any destroy
events. Single-target, so this is the *simplest* of all the bosses' damage routing.

### 7.6 Death + phase-2 handoff
Host drives real death; broadcasts terminal + `StartPhase2` trigger; client plays visual death and takes
the handoff. Phase 2 (spider) authority is a separate effort (§9).

---

## 7b. Log216 validation (first host+client capture)

The first two-end capture (host `inst=-361564`, client `inst=-448520`) confirms the model:
- **Manifest identical both ends** — `numberOfSections=10 spawned=10 vulnerableIndex=9`, same UnitIds in the
  same order. → seq-binding (§7.4) is sound.
- **Double worm confirmed** — each end runs its own `EmperorBossWorm` with its own `StartMovement` + its own
  `JumpTo` targets. The targets diverge on the axis that depends on the local player (host jump z ≈ −1.8 vs
  client z ≈ +0.6 for the "same" jump). → position must be host-streamed; the client's movement driver must
  be suppressed (§7.2/7.3).
- **Damage already routes today** — `[ClientHit] ACCEPT … unit=ShavwaEmperorWormSectionVulnerable dmg=30.0`;
  the client's hits reach the host's vulnerable section through the *existing* roster-bound `ClientHitRequest`
  pipeline (the sections are ordinary roster Npcs). Non-vulnerable sections resolve too but are armored. So
  §7.5 is **already working** for the worm — no Emperor-specific damage adapter is needed for the main body.
- **Health mirrors to the client** — client `tailHp` tracks the host (1.000 → 0.224 over the fight). The bar
  unit is the persistent tail Npc, so health sync is already covered by the general enemy state path.

**Net:** for the worm, the remaining work is **position/movement authority + section-destruction event
mirroring + death/phase-2 handoff** — not damage or health (those already work). This narrows EMP-2.

### Known issue — ground-slam frame hitch (EMP-1b)
Both ends drop to ~1 fps each time the worm slams into / travels underground, recovering to ~100 fps while
airborne. Because it is ground-*specific* it points at the native ground/underground path
(`UpdateUndergroundTravelVisuals` raycast + particle play/stop, `SetGroundEnterParticles` emitting against
all 10 section Npcs) rather than uniform per-frame cost — but Unity's fixed-timestep "spiral of death" means
any per-`FixedUpdate` mod overhead amplifies it. The probe was therefore changed to be **event-driven only**
(no per-`FixedUpdate` reflection) and a default-off stopwatch (`LogEmperorWormPerf`, `[EmperorWormPerf]`)
wraps `FixedUpdate` + the suspected native ground calls to attribute the cost on the next capture.

## 8.5 EMP-2 client burrow hitch — what shipped, and the still-open native hitch

Two separate things were chased under EMP-2: (1) a **constant** client-side ground-fight hitch (fixed), and
(2) a **burrow-only** client hitch (still open).

### Shipped (fixes / improvements)
- **Double-worm handled by NOT freezing the client worm.** Earlier attempt: freeze the client `EmperorBossWorm`
  (kinematic + skip `FixedUpdate`/`JumpTo`) and drive the 10 sections purely from the host stream. That was
  wrong — it left the worm *head* frozen at spawn (only the body sections were position-mirrored) and the
  per-frame teleport-mirror of 30 section colliders churned the PhysX broadphase (its own client-only hitch).
  **Reverted.** The client now runs its own worm locally (smooth `UpdateWormSections`); positions diverge from
  the host (double-worm), which is accepted — **damage stays host-authoritative through the roster
  `ClientHitRequest` path** (§7b), so divergence is cosmetic.
- **Emperor worm sections are excluded from the generic enemy-state transform mirror**
  (`IsEmperorWormSectionSnapshot` → `continue`, same pattern as the Cousin arm). The generic mirror sets
  `transform.position` on every puppet each frame; for a 10-section/30-collider boss that teleport churned the
  broadphase and was the **constant** ground-fight hitch. Skipping it (let the local worm move them smoothly)
  removed that constant hitch. This layer did not cover the Emperor back in the Witch-era, which is why the
  boss only started hitching after the general enemy-state mirror shipped.
- The default-off scaffold config `EnableEmperorClientWormSuppression` was removed and retired (feature, not a
  toggle — per project policy config is diagnostics-only).

### Root-caused (Log241–244) — the client's own local worm IS the hitch; stopgap = suppress it
The client drops to ~1 fps *the whole time its local phase-1 worm is alive* — not only underground; the
earlier "only while underground" framing was a sampling artefact (the watchdog's `_wormActive` flag was
inadvertently gated behind `LogBossEncounter`, so captures were partial). **Root cause: the client runs its
own full autonomous `EmperorBossWorm`, and that worm's native `FixedUpdate` physics is what tanks the frame.**

Proof (behavioural + measured):
- **Worm alive = ~1 fps; worm dead = instantly smooth; level-reload = smooth** (a reload with the host gone
  respawns a *clean* worm ⇒ smooth). **Single-player with the SAME worm is smooth.** So the cost is the worm,
  and it is coop-client-specific.
- **F3/pause stops it** (world time frozen ⇒ `FixedUpdate` stops), **network-independent** (persists after
  Leave-room and even after the host process is closed — it is a local scene object, not driven by any packet).
- The cost is **native, inside `EmperorBossWorm.FixedUpdate` / the PhysX simulate of its non-kinematic body** —
  NOT our C# (`[UpdateProf]` fired once at 99 ms across a whole laggy fight; gameplay/boss ticks ~0 ms), NOT the
  wrapped ground methods (`[EmperorWormPerf]`: `UpdateUndergroundTravelVisuals` 8 ms, `SetGroundEnterParticles`
  13 ms), NOT GC (`gc0Δ` 0–1), NOT the section colliders (an earlier ablation disabling all 34 didn't help —
  consistent: it's the rigidbody integration, not the broadphase). The worm behaves *normally* (one instance,
  10 sections, sane jump targets, tail HP 1.0→0.13) — it is simply too heavy for this client, which (carrying
  coop overhead) crosses `Time.maximumDeltaTime` (0.333 s) into the fixed-step catch-up spiral.

> **The earlier "Shipped" note above is now known to be wrong on one point:** *not* freezing the client worm was
> the wrong call. Freezing is the correct stopgap — the reason it churned the broadphase before was the *other*
> layer (the per-frame teleport-mirror of the sections), which is now gone (`IsEmperorWormSectionSnapshot` skips
> the apply, and the **host no longer even sends** those snapshots — send-side exclusion in
> `CollectHostEnemyStateSnapshots`). With no mirror, a frozen worm just sits still — no churn, no lag.

**Stopgap shipped (EMP-2b):** on a **linked client only**, prefix-block `EmperorBossWorm.StartMovement`
(`EmperorWormDiagnostics.StartMovement_SuppressPre`, registered *unconditionally* before the diagnostics gate so
`EnableEmperorWormDiagnostics` can't re-enable the lag). `StartMovement` is what sets `rb.isKinematic=false` +
`bossActive=true`; blocking it leaves `rb` kinematic so `FixedUpdate` early-returns (§4.2) → **zero physics cost,
0 hitches** (Log244). Host / single-player / an **unlinked** solo client are unaffected. **Accepted cost:** the
worm is pre-placed underground and only emerges via `StartMovement→Initialize`+first jump, so a suppressed worm
**never appears** on the client (invisible, unfightable) — acceptable only because worm sync isn't built yet.

**Real fix = EMP-3 / §7.2 head-streaming:** let `Initialize` run (sections spawn + emerge, visible) but keep the
worm kinematic (no autonomous physics), stream the host worm's **head transform** each tick, and drive the client
worm from it (head applied from the stream, `UpdateWormSections` run locally for the cheap section follow). That
gives a visible, moving, synced, non-laggy client worm and supersedes the EMP-2b stopgap. **Shipped as EMP-3a —
see §8.6.**

## 8.6 EMP-3a shipped — head-streaming (design A), and the two residual fixes it took

Head-streaming (§7.2 design **A**) replaces the EMP-2b stopgap. `EmperorWormDiagnostics` now hooks
`EmperorBossWorm.FixedUpdate` (registered unconditionally, before the diagnostics gate). On the **host** the prefix
captures the worm head and lets native run; on a **linked client** it drives the local worm and returns `false`
(skips native — redundant belt-and-suspenders, since native `FixedUpdate` already bails on a kinematic body, §4.2).
`NetEmperorWormSync` is the core:

- **Host** (`HostCapture`): throttled **20 Hz**, broadcast `HostEmperorWormHead` (msg 57 — 4 floats + seq,
  `DeliveryMethod.Unreliable`): head `position` + `eulerAngles.y`.
- **Client** (`DriveClientWorm`): keep `rb` kinematic → native physics never integrates; apply the streamed head to
  the worm root + the `root` follow-anchor; run `UpdateWormSections` locally (the game's own cheap section trail).
  The 10 sections spawn kinematic already, so the *worm logic* costs ~nothing.

That alone did **not** fix the client lag — it took two more iterations, each pinned from a diagnostic build's
`[EmperorWormHead]` / `[EmperorWormFrame]` / `[UpdateProf]` lines (Log245–249):

1. **The real residual was PhysX broadphase churn, not the worm logic (Log247).** With the worm kinematic and its
   logic inert, the client was *still* ~1 fps — and the hitch tracked head-jump distance **exactly**: smooth while
   the head sat still, 1–8 fps the instant it began its 20 Hz long-range teleports, worse on wide burrow spacing
   than on stairs (the user's own terrain observation). Cause: driving the head streams ~11 **kinematic colliders**
   (head + 10 sections) as hard teleports across the static arena every substep; PhysX rebuilds the broadphase AABBs
   + regenerates contact pairs against every wall/floor/breakable in the sweep — cost ∝ teleport distance × collider
   count, amplified by the fixed-step catch-up. (This corrects §8.5's "it's the rigidbody integration, *not* the
   broadphase" — the native integration was the EMP-2b cause; going kinematic then exposed a *second*,
   broadphase cost that only appears once you move the colliders.) **Fix (`EnsureWormVisualOnly`):** the client worm
   is visual + one hittable weakpoint, so disable colliders on the head + the 9 non-weakpoint sections, keeping only
   the current tail (`lastActiveIndex`, which moves as sections die) enabled so the player can still shoot it.
   Per-frame cost is a cached enabled-flag toggle (no alloc). **Log248: ~1 fps → occasional ~180 ms blips, playable.**
   (An earlier guess — that the *sections'* rigidbodies needed pinning kinematic — was a dead end: they already spawn
   kinematic, `pinned 0/10`.)
2. **Snapshot interpolation for the visual stair-step (Log249).** The 20 Hz head was applied as a hard snap each
   sample → the worm teleport-flickered and the body stretched+snapped. `OnHeadReceived` now keeps the last two
   samples and `DriveClientWorm` renders between them over the measured interval, ~one interval (~50 ms) in the past —
   standard entity interpolation: continuous motion, a small **fixed** delay (not a velocity-proportional trail, so it
   still tracks fast jumps), `Unreliable` drops handled by clamping `t`. Removes the flicker; the head no longer
   displaces far within a single frame.

**Final state (Log249):** the "fixed 10 000 times, never fixed" client Emperor phase-1 lag is resolved — from
sustained ~1 fps (unplayable) to occasional brief ~155 ms blips (playable, smooth motion). The remaining blips are
**not** the worm: correlating each against neighbouring log lines shows fight-start init, a one-shot
`PlayerSpriteAssetScanProbe` frame, the diagnostic build's own `[ProbeSummary]` synchronous disk flush, and general
co-op combat mirroring (12 NPCs / 72 units / boss adds + projectiles). A diagnostics-off build drops the
logging-I/O share. **Deferred:** section-destruction sync (EMP-3b), death + phase-2 handoff (EMP-3c, §7.6); the
client's local `WeakpointHit` bookkeeping still diverges (cosmetic — damage is host-authoritative, §7.5).

## 8. Probe plan — `EmperorWormDiagnostics` (validate before building)

Observe-only, config-gated (`EnableEmperorWormDiagnostics` + `LogBossEncounter`), tagged with side
(host/client) so the two logs diff to expose divergence. Captures:

1. **Section spawn manifest** — confirm both ends spawn 5 sections, same two UnitIds, same order, which is
   vulnerable. (Validates §7.4 seq binding.)
2. **Jump targets** — `JumpTo(targetPosition, jumpTime)` args + resulting velocity + head pos, per jump.
   The host and client target lists **will not match** → the smoking gun that proves the worm must be
   host-streamed (validates §7.1/7.2).
3. **Weakpoint / health** — `WeakpointHit` damage + `lastSectionNpc` normalized health + `lastActiveIndex`
   + `wormSections.Count`. Confirms whether the client's `WeakpointHit` ever fires (it should not, once
   damage is routed) and how section destruction tracks health (validates §5/§7.5).
4. **Section destruction** — `DestroySection(index)` + resulting count. (Validates §7.4 lifecycle events.)
5. **Position/state timeline** — throttled `FixedUpdate` sampler: head pos, velocity, `isUnderground`,
   `currentJumpCount`, section count, tail health. The two side-by-side timelines quantify the drift for
   the §7.2 (A)-vs-(B) decision.
6. **Death** — `DeathAnimation`/`OnDeath` + phase-2 handoff.

**Reading the logs:** collect one host + one client log of the same fight. Line up by wall-clock/jump index.
Expect: identical section manifest (count/UnitId/order), **divergent** jump targets and position timelines,
and client `WeakpointHit` firing on *its own* worm from *its own* hits (proving the double-worm). That
divergence is the empirical justification for the host-authoritative design above — captured *before* any
sync code, so the pipeline is built once against real data.

---

## 9. Deferred — Phase 2 (spider)

`EmperorBossSpider` (+ `EmperorBossSpiderClaw`, `EmperorBossSpiderRocket(Launcher)`, `EmperorPillar(Controller)`,
`EmperorHoleBlower`) is a large separate coroutine-driven boss (defend phases, leg destruction, rocket
bursts, pillars). It is out of scope for the phase-1 worm effort and gets its own audit + pipeline once the
worm is synced. `EmperorBossFightHelper.StartPhase2()` is the seam; `*Endless` variants are the endless-mode
worm/spider and are not part of the campaign encounter.
