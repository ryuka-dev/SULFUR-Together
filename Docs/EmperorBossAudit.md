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

### Start path — **CORRECTION (see [BossSyncReuseMap.md](BossSyncReuseMap.md) §4a)**
`EmperorBossFightHelper.OnPlayerSpawned` (fires from `GameManager.onPlayerSpawned`) → `StartPhase1()`, which
**only toggles the phase-2 spider parent off** — it does **NOT** call `StartMovement()`. `EmperorBossWorm.StartMovement()`
is `public` and is invoked by an **external scripted trigger — almost certainly the pre-fight NPC dialog** ("最神圣的
皇帝陛下", Log216 `[DialogFlow]`) on completion. So the fight start **IS dialog/trigger-gated**, same shape as
Cousin/Lucia — the earlier "no dialog gate in the boss code" framing here was misleading (the *helper* doesn't gate,
but the dialog does). This is the hook to reuse the dialog-commit sync (gate `StartMovement` as the Emperor's
"StartFight equivalent"). `StartMovement` itself plays music, captures the player transform, `await Initialize()`
(spawns sections), then kicks the first `JumpTo`.

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
Client hit on its local vulnerable tail → host `lastSectionNpc.ReceiveDamage` → native `WeakpointHit` advances →
host broadcasts health + any destroy events. Single-target, so this is the *simplest* of all the bosses' damage
routing. **Originally assumed to ride the ordinary roster `ClientHitRequest` path (Log216); that turned out to be
wrong once the worm is head-streamed** — the runtime-spawned tail gets quarantined as "client-only" and hits never
reach the host (see §8.8). Implemented instead as a dedicated single-target route, **EMP-3d** (`ClientEmperorWormHit`,
msg 60 → `BossDamageReflect.TryApplyRealDamage` on the host's `lastSectionNpc`), bypassing the roster bind.

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

## 8.7 EMP-3b/3c shipped — section destroy + terminal death, both by replaying the real native method

Both follow-ups reuse the same trick as §7.5's damage routing: instead of re-deriving the host's stateful math on
the client, replay the host's exact **call**, through reflection, on the client's own worm. Neither needed a new
suppression layer, because §7b already established that the client's local `WeakpointHit` never actually fires —
its damage is fully redirected to the host through the ordinary roster `ClientHitRequest` path — so the client
never independently reaches a section-destroy or a lethal-health threshold on its own.

- **EMP-3b (section destroy):** a host postfix on the real `DestroySection(index)` broadcasts
  (`HostEmperorWormSectionDestroy`, msg 58, `ReliableOrdered`). The client mirrors by invoking the same native
  `DestroySection(index)` **followed by** `MoveVulnerableSectionUp()` on its own worm — the exact pair
  `WeakpointHit` calls (§5) — with `index` recomputed as `lastActiveIndex - 1` from the client's OWN state. This
  stays in lockstep because the client's `lastActiveIndex` only ever changes in response to this same event
  (`MoveVulnerableSectionUp` decrements it), so host and client apply the identical sequence of indices even
  though neither side sends the index over the wire. Reusing the native call gets the white-flash, frag
  explosion, gib spawn, `Npc.Die()`, hitbox-invulnerable, and list-shrink bookkeeping for free — none of that is
  reimplemented.
- **Bug found while wiring EMP-3b, fixed in the same change:** `EnsureWormVisualOnly` (EMP-3a) picked which
  section's collider to keep enabled by indexing the **unshrunk** `wormNpcs` list (every section ever spawned,
  original order, never shrinks) with `lastActiveIndex` — an index into the **shrinking** `wormSections`/
  `sectionControllers` lists. That only happens to line up before the first section dies (`lastActiveIndex == 9`
  == `wormNpcs.Count - 1`); after the first `DestroySection`, `lastActiveIndex` decrements to 8 but
  `wormNpcs[8]` is still the *original* section 8 (now destroyed) — the real, still-alive vulnerable tail
  (`lastSectionNpc`, always `wormNpcs[9]`) would have its collider left disabled, i.e. unhittable on the client
  after the very first section died. Fixed by keying off the `lastSectionNpc` field's object identity instead of
  any index — that field never changes across the worm's life, only its transform teleports (§5).
- **EMP-3c (terminal death + phase-2 handoff):** a host postfix on `DeathAnimation()`'s kickoff — the outer
  method that constructs the compiler-generated coroutine state machine, which Harmony patches at the point
  `StartCoroutine(DeathAnimation())` is called, i.e. immediately when `WeakpointHit` decides the worm is dead, not
  5 s later when the coroutine body finishes — broadcasts (`HostEmperorWormDeath`, msg 59, `ReliableOrdered`,
  one-shot). The client mirrors by reflectively invoking `DeathAnimation()` on its own worm and passing the
  returned `IEnumerator` to its own `StartCoroutine`. Because `DeathAnimation` is the real method, the client
  automatically gets the full vanilla sequence — lerp to `roomCenter`, camera shake, fog blink, ground slam,
  frag explosion + gibs, `lastSectionNpc.AttachToBossUI(false)`, `npc.Die()`, `CleanupDestroyedSections()`,
  **and `emperorBossFightHelper.StartPhase2()`** (the phase-1→phase-2 seam, §6) — all without a single line of
  it being reimplemented. The client's head-stream driver (`DriveClientWorm`, EMP-3a) checks a
  `_clientDeathApplied` flag and stops writing `transform`/`rb.position` once death starts, so it doesn't fight
  the coroutine's own position lerp.
- **Scope boundary:** `StartPhase2()` only **activates** the client's own Phase-2 spider root — same
  each-side-runs-its-own-instance model as the rest of this boss. It does not sync spider position, legs, or
  attacks; that is its own large effort (§9), deferred.
- Both messages are functional patches registered unconditionally (same reasoning as the FixedUpdate head-sync
  patch, §8.6): they must survive `EnableEmperorWormDiagnostics` being turned off. Build 0 errors; not yet
  verified against a real two-end fight through to a section kill / worm death.

## 8.8 Log250–252 — EMP-3b/3c untestable; the real blocker was client→host damage (EMP-3d)

The first two-end test of EMP-3b/3c (Log250 host+client, then 251/252) surfaced four things. Recording all of
them so the next attempt starts from fact, not re-derivation.

### (a) EMP-3b/3c never executed — nothing to verify yet
Across all three host logs, `host DestroySection`/`host DeathAnimation` broadcasts fired **0 times** and there
were **0 exceptions**. The worm's tail HP never dropped below ~0.978 (WeakpointHit only ever saw the host's own
slow hits), so no section was ever destroyed and the worm never died → the EMP-3b/3c code paths were never
reached. They remain **unverified**, blocked behind (b).

### (b) Client→host worm damage was fully broken → the actual blocker (fixed as EMP-3d)
All six logs: host `hitRecv=0`, client `clientHitSent=0`. The worm's vulnerable tail
(`ShavwaEmperorWormSectionVulnerable`) was repeatedly **quarantined as "client-only CombatEnemy"** (16× in Log250,
31× in Log251). Root cause: the tail is a runtime `SpawnUnitAsync(this,…)` add (not in the level-load manifest),
and the Emperor is **not a registered boss encounter** (`EmperorBossAdapter` is diagnostic-only, empty start
chain), so it is caught by neither the RT3 boss-add binding (needs a registered encounter) nor a stable roster
bind (the head-streamed local worm's tail position/timing diverges from the host's at reconcile time). The generic
roster path then quarantines it as a rogue client-only enemy → it is not a puppet → `TrySendClientHitRequest`
skips it (`clientHitSkipNoPuppet`) → the host never receives the hit. §7b's "damage already routes (Log216)" no
longer holds once the worm is head-streamed (EMP-3a) — that assumption is retired.

**Fix — EMP-3d (dedicated single-target damage authority), the §7.5 design done explicitly instead of relying on
the roster path:** a client hit on the worm's vulnerable tail (identified by comparing the damaged Npc's
GameObject to the client worm's `lastSectionNpc`) is forwarded to the host (`ClientEmperorWormHit`, msg 60,
ReliableOrdered — damage + damageType + seq) and the local damage is suppressed; the host applies it to its **real**
`lastSectionNpc` through the vanilla `ReceiveDamage` (`BossDamageReflect.TryApplyRealDamage`, source =
`GameManager.Instance.PlayerUnit`), firing `onDamageRecieved → WeakpointHit` so the real mechanic advances
(health, section-destroy, death). Health syncs back through the existing enemy health-state path; destroy/death
through EMP-3b/3c. This bypasses the fragile roster bind/quarantine entirely. Inserted in `Npc_ReceiveDamage_Pre`
**before** the ordinary `TrySendClientHitRequest`. Only the tail's collider is left enabled (§8.6
`EnsureWormVisualOnly`), so the tail is the only worm part the client can hit anyway. `[EmperorWorm] client -> host
worm hit …` / `host applied client worm hit …`. Build 0 err, deployed — **pending real-match verification** (the
first test that can actually reach a section kill + worm death, which then also validates EMP-3b/3c).

### (c) Host frame hitch — NOT caused by EMP-3b/3c; the EMP-2 native-physics spiral, now on the host
The host dropped to 1–3 fps (`[EmperorWormFrame]` dt=1000–3400 ms) in the 2nd/3rd encounters, escalating per
encounter and surviving a same-level reload. But: EMP-3b/3c broadcasts never fired (b), 0 exceptions, `gc0Δ≈0`
(not GC), and `[UpdateProf]` showed our Update body at only 50–118 ms while the stall was 1000–3400 ms → **the
stall is native (physics/render), outside our C#**. This is the same fundamental "raw `EmperorBossWorm.FixedUpdate`
ballistic physics is too heavy under co-op per-frame overhead → fixed-step catch-up spiral" that §8.5 pinned on
the *client*. The host is the authority and has always run the full native worm; the EMP-3a verification (Log249)
was a single short encounter so it never exercised the host's multi-encounter degradation. **Not an EMP-3
regression — a pre-existing hard problem** (needs a real Unity Profiler, per §8.5's own conclusion). One
contributing, reducible lever seen here: `NetBossEncounterManager.Tick` (`bossTick`) cost 50–118 ms/frame on the
host during the fight — co-op overhead that compounds the spiral; worth profiling separately. **Deferred /
known issue.**

### (d) Host cannot advance the pre-fight dialog on a 2nd/3rd re-entry
0 exceptions; the client could still dialog. Matches the known "F3 / client-ahead premature boss-level load →
seed/progress split → phase-2 dialog orphaned" issue (see [BossPreFightFlow.md](BossPreFightFlow.md) and the
Cousin infinite-dialog / seed-split note). Unrelated to EMP-3; **known issue**, not addressed here.

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

---

## 10. EMP fight-start probe result (Log254) — CONFIRMED dialog-gated

One-shot `StackTrace` probe on `EmperorBossWorm.StartMovement` (the fight-start method). Captured on both
ends, fresh Emperor entry with the intro dialog played from the start. **Result is definitive** and corrects
the last unknown blocking the dialog-sync design.

### 10.1 What invokes `StartMovement` (the exact chain, identical host + client)
```
DialogController.SelectOption            (player picks a dialog choice)
  → MultipleChoiceNode.OnOptionSelected
  → DialogueTree.Continue(index)
  → DialogueTree.EnterNode → Jumper.OnExecute → EnterNode
  → ActionNode.OnExecute
  → ActionTask.Execute
  → ExecuteFunction_Multiplatform.OnExecute      (NodeCanvas action task)
  → MethodBase.Invoke                            (reflection)
  → EmperorBossWorm.StartMovement()
```
So the pre-fight sequence is a **NodeCanvas `DialogueTree`** whose final **`MultipleChoiceNode`** option runs a
`Jumper` into an `ActionNode` carrying an **`ExecuteFunction_Multiplatform`** action that reflection-invokes
`EmperorBossWorm.StartMovement()`. **The Emperor's fight start is a dialog-choice callback**, same *shape* as
Cousin/Lucia — confirms BossSyncReuseMap §4a; **audit §1's "StartMovement fires from OnPlayerSpawned" is
wrong** (already corrected) and 4a's "almost certainly the pre-fight dialog" is now **CONFIRMED**, with the
precise wiring (NodeCanvas action task, not a PlayerTrigger and not an animation event).

### 10.2 The desync is real and per-end (the thing to fix)
Both ends fired `StartMovement` **independently**, each on its *own* local dialog-choice selection:
- Host `inst=-161832`, Client `inst=-231850` — different worm instances (expected: pre-placed per scene), but
  **same** `root=[0]-ShavwaEmperorWorm`, same `path`, same `pos=(15.3,-0.7,0.1)`, `sections=10`,
  `tailHp=1.000`. Worm placement/state is deterministic across ends → good; only the *start trigger* diverges.
- Each player picking the dialog option starts the worm locally → **unsynced fight start**, exactly the
  Cousin/Lucia failure mode (music / emergence / `Initialize` timing / dialog→fight transition drift). The
  worm *combat* is already host-authoritative (EMP-3a…3d), so this is a presentation/transition desync, not a
  mechanic desync (BossSyncReuseMap §4c) — still worth a clean gate.

### 10.3 Why the existing StartFight machinery still doesn't apply
The trigger is a **generic `DialogueTree` action node**, not a `BossFightHelper.TriggerFight` / controller
`StartFight` (the log confirms only the four registered controller bosses got `[BossEncounter] patched …`).
There is no `EncounterKey` and no registered adapter for the Emperor. Retrofitting `NetBossEncounterManager`
(which keys on `source.EndsWith(".StartFight")` + registered encounter) would be forcing a square peg — the
bespoke gate is the right call, reusing only the *primitives*.

### 10.4 The gate — IMPLEMENTED (EMP-4)
`StartMovement` is Harmony-hooked with a functional prefix (`EmperorWormDiagnostics.StartMovement_FightGate_Pre`
→ `NetEmperorWormSync.TryGateFightStart`), registered unconditionally alongside the EMP-3 functional patches.
- **Client (linked):** the prefix blocks the local `StartMovement` (its own dialog choice must not start the
  worm) and sends `ClientEmperorFightStart`(61) once per worm; the worm starts when the host's commit arrives.
- **Host:** its own dialog pick commits inline — the prefix marks committed (keyed by worm instanceID),
  broadcasts `HostEmperorFightStart`(62), and lets the native `StartMovement` run. A client request
  (`HostOnClientFightStartRequest`) commits the same way but *also* invokes the host worm's `StartMovement` via
  a reentry flag (`_inFightStartCommit`) that bypasses the gate. A request that lands before the host worm is
  live is deferred to the next `HostCapture`.
- **Every client** mirrors the commit (`OnFightStartCommitReceived`) by invoking its own worm's native
  `StartMovement` via the same reentry flag → Initialize (section spawn) / emergence / music in step.
- **Idempotent per encounter:** the commit is keyed by worm instanceID, so a re-encounter/reload self-rearms
  and each worm commits exactly once; single-player / unlinked-solo fall through to the vanilla start.
- **Dialog scoping:** the fight-start option is a `MultipleChoiceNode` choice → "players who want to dialog can;
  whoever picks the fight-start option commits; the fight starts host-authoritatively" (FF14 RM intent).
- **Phase-2 dialog (still open, §4b):** the phase-2 dialog is a *separate* `DialogueTree`; handle it per-client
  on the local phase-2 trigger, not a global restart. NOT part of EMP-4.

**Dialog close (Log255 fix):** the picking end closes its own pre-fight dialog natively (by selecting the
option); the **non-picking** end never picks, so its dialog stayed open when the worm emerged via the broadcast
(user report: "boss came out but the dialog wasn't deleted"). Fixed by finalizing the local dialog
(`BossDialogReflect.TryFinalizeCurrentDialog` = the real `Graph.Stop(true)`, no-op when nothing is open) in the
two mirror-apply paths — `OnFightStartCommitReceived` (a client applying the host commit) and
`HostCommitFightStart` (the host committing from a client's request). The picking end is deliberately NOT
finalized inline: that call runs inside the dialog tree's own action-node execution (StartMovement prefix), so
stopping the tree mid-execute is unsafe — native close handles it. This matches the FF14 intent "committing the
fight closes every player's boss dialog".

**Status: EMP-4 fight-start sync VERIFIED (Log255 Test A + Test B both lockstep); dialog-close fix implemented,
build clean, deployed. Pending re-test of the dialog close** (see §10.5).

### 10.5 EMP-4 live test procedure
1. Host + client both load into the Emperor level; both walk to the pre-fight dialog (worm pre-placed, not yet
   moving). Enable `LogBossEncounter` for the `[EmperorWorm]` lines.
2. **Test A (host picks first):** host advances the dialog to the fight-start option and selects it. Expect
   host log `host fight-start (local dialog) … -> broadcast commit`; each client log `client mirrored fight-start
   (host commit)`. Both worms emerge + play music at the same time; client did NOT double-start.
3. **Test B (client picks first):** fresh encounter; the *client* selects the fight-start option. Expect client
   log `client fight-start (local dialog) … -> requested host commit (local start blocked)`, host log `host
   fight-start (client request) … -> committed + broadcast`, then the client's `client mirrored fight-start`.
   The worm starts on both ends together, host worm authoritative.
4. **Test C (opt-out client):** one player commits while the other is still in / hasn't opened the dialog — the
   worm must still start for the non-committing client (via the broadcast). Its dialog staying open is the known
   cosmetic loose end above.
5. In all cases confirm EMP-3a…3d still work end-to-end after the synced start (head stream, section destroy,
   client→host tail damage, death→StartPhase2) and there are 0 exceptions.
