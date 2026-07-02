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

## 8.9 Host 1 fps IS the client's original 1 fps (same root) — record + the "first fight is fine" puzzle

Recorded per user direction (Log264/265). The host-side 1 fps is **not a new problem and not the phase-2 spider** —
it is the *same* defect that plagued the client for ~12 log iterations and was fixed there: the native
**`EmperorBossWorm` ballistic rigidbody `FixedUpdate` physics** (the non-kinematic rigidbody integration) is too
heavy under co-op per-frame overhead and drives Unity's fixed-timestep into the "spiral of death."

**Do NOT re-run the experiments already eliminated on the client** (all were *measured*, not guessed — §8.5/§8.8c):
- NOT our C# (`[UpdateProf]` ≪ the hitch), NOT GC (`gc0Δ≈0`), NOT particles, NOT window focus.
- NOT the section colliders / PhysX broadphase: an ablation disabling **all 34** colliders on the *non-kinematic*
  worm did not help — "**it's the rigidbody integration, not the broadphase**." The broadphase only became a cost on
  the client *after* it was made kinematic (a second, separate cost, fixed by `EnsureWormVisualOnly`).
- NOT the travel distance / "boss racing to a far player": tested before; and Log265 confirms the lag also happens at
  normal in-arena distance (the host was only out-of-position once). **An earlier same-turn hypothesis that the host
  lag was broadphase churn from the worm racing to an out-of-position host is RETRACTED** — that is a retread of the
  eliminated broadphase/distance path. Teleport-in (#5) is a legitimate *feature* but is **not** the lag fix.
- NOT instance accumulation: the EMP-6e census proved there is always exactly **one** worm / one spider / 8 legs /
  one helper across every reload + hub round-trip (no leak). (That census's per-3 s `Resources.FindObjectsOfTypeAll`
  was itself the periodic hitch the user saw, and has been removed; the `_wormActive`→`WormTicking` heartbeat fix was
  kept so `[EmperorWormFrame]` no longer mis-tags loading/hub stalls.)

**The client's real fix, and the host's direction:** the client escaped by **not running the native physics** — keep
the worm `rb.isKinematic=true` so `FixedUpdate` early-returns (`if (rb.isKinematic || isDead || inDeathAnimation)
return;`), i.e. zero rigidbody integration, and drive position from a cheap external source (the host head stream +
`UpdateWormSections`). The host is the authority with no upstream to stream from, but the **same principle applies**:
drive the host worm kinematically from a **cheap scripted/authored approximation of the ballistic jumps** (compute
the authoritative path without PhysX rigidbody integration), then stream it to clients as today. That removes the
heavy physics on the host the same way it did on the client. This is the intended direction — **not** more collider
/ GC / distance testing.

**Open puzzle (user's only remaining question): why is the FIRST fight fine but every reload laggy?** — same single
worm, normal distance, same code. Working hypothesis (not yet proven): the fixed-timestep spiral is **self-sustaining
once entered**. Unity queues catch-up `FixedUpdate` substeps when frames run long; if the per-substep worm physics is
heavier than real time advances, the accumulator never drains and the game runs many FixedUpdates per render frame
*forever*. The **trigger is a burst of slow frames while the worm is already active** — a level **reload/regeneration**
is exactly that (heavy generation stutter with the worm present right after regen), so physics falls behind during the
stutter and, being too heavy to catch back up, stays spiralling. The **first** fight loads the worm "clean" (fresh
process, empty physics accumulator, no regen stutter grinding the worm) so it never falls behind → no spiral. This
also fits: survives a hub round-trip (the return is itself a regen that re-triggers, and/or the spiral simply
persists), and a **process restart fixes it** (resets the Time/physics accumulator). To *prove* it would need
`Time.deltaTime` + FixedUpdate-count-per-render-frame instrumentation — but the kinematic-host fix above sidesteps it
entirely (no heavy physics ⇒ nothing to spiral), so proving the exact trigger is optional.

## 8.10 EMP-6f — host kinematic worm drive (ATTEMPTED, then REVERTED)

The §8.9 direction was implemented and tested, then reverted (`git restore` to the EMP-6d commit). Recording it so
the next attempt starts from the findings, not from scratch.

**What it did.** On the co-op host, the worm's FixedUpdate prefix drove the worm **kinematically** (skip native
ballistic FixedUpdate, return false) and **reused every native decision method** (`HitGround`, `JumpToNextTarget`,
`UpdateUndergroundTravelVisuals`, `UpdateWormSections`, `AllSectionsBelowGround`), replacing only the three PhysX
integration lines with a manual integrator (`vel += scale*gravity*dt`, `pos += scale*vel*dt`).

**Result — the direction WORKS for the lag (Log266).** With the host worm kinematic, the host 1 fps was **gone**:
`[EmperorWormFrame]` hitches dropped from 100+ to 4, and those 4 were all stale startup frames
(`sinceGroundEvent ≈ 1,000,000 ms`). This confirms §8.9: the host lag is the native ballistic-rigidbody physics, and
not running it (kinematic) removes it — exactly as on the client.

**Two porting bugs found (the reason it was reverted, not the direction failing):**
1. **Free-fall (fixed):** a kinematic Rigidbody does not retain `linearVelocity` across frames (next-frame read
   returns 0), so using `rb.linearVelocity` as the velocity store made the worm free-fall (Log266: JumpTo set
   `vel.y=+25` but the worm only fell). Fixed by keeping our OWN `_hostVel`, captured from a functional JumpTo postfix
   the instant JumpTo sets it (same-frame read is valid).
2. **"Jumps up once, never cycles" (root-caused, NOT fixed — Log267):** the worm launched correctly but then fell
   past its ground-Y every time without `HitGround` completing the cycle, hit the −100 fail-safe, reset to spawn,
   re-jumped, fell again — a tight reset loop. **Root cause:** `EnsureWormVisualOnly` (copied from the client) disables
   all non-tail section colliders **including the per-section `AboveGroundTrigger` triggers**, and
   `AllSectionsBelowGround()` — which gates the underground-travel→re-emerge step — reads exactly those triggers. With
   them disabled it never returns true, so the worm never travels underground / re-emerges. The reset loop then
   hammered the native decision methods (`TryFindValidJumpPosition` 3× spherecast, `SetExitPoint` 8× spherecast) many
   times per frame → the "random hitches" seen in Log267 (11 real combat hitches vs 4 in the free-fall Log266). This
   is a **small fix** (on the host, don't disable those colliders, or replace `AllSectionsBelowGround` with a
   trigger-independent check), not a dead end.

**Workload for a full hand-written worm (no native decision calls), if that path is chosen instead:** ~400–600 lines
re-implementing the movement AI — ballistic integration (~50, done) + target selection with its validation
spherecasts + `jumpsBeforeTargetingPlayer` (~100) + underground travel with the 8-spherecast exit-point pick + timing
(~120) + rage paths (read the scene `ragePathDictionary` + sequencing) (~80) + RNG matching + reflect ~20 serialized
tunables (~80) — plus several test iterations to tune arcs/timing/RNG to feel like vanilla (the client movement took
~12). Sections / weakpoint / destroy / death / phase-2 handoff stay as-is (already synced). **Two caveats:** (a) a
hand-written worm still calls `Physics.SphereCast`/`Raycast` (picking valid jump/exit points against the level is a
Unity-API query, not a worm method) — so if the residual hitches come from those queries, a rewrite doesn't remove
them; (b) it is the authoritative boss, so a bug breaks P1 for everyone, and it permanently forks from any future
game update to the worm. **Recommendation if revisited: fix the collider / `AllSectionsBelowGround` bug first (~1
iteration) — the kinematic direction already killed the main lag (Log266) — before committing to a full rewrite.**

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

## 9. Phase 2 — the spider (EMP-6 audit + sync design)

Reverse-engineered from the same `PerfectRandom.Sulfur.Gameplay.dll`. Phase 2 is `EmperorBossSpider` (+
`EmperorBossSpiderClaw` IK legs, `EmperorBossSpiderRocketLauncher`/`EmperorBossSpiderRocket`, `EmperorHoleBlower`
death seam). `*Endless` variants are endless-mode and not part of the campaign encounter. Method/field names are
exact.

**Working method (same as the worm): probe first.** The immediate deliverable is the observe-only probe
`EmperorSpiderDiagnostics` (EMP-6a); the sync pipeline (EMP-6b+) is written only after the probe validates this
model on real host+client logs — the worm's EMP-2 saga (≈12 log iterations chasing the wrong hitch) is exactly
what probe-first avoids.

### 9.1 Entities and lifecycle
| Type | Role |
|------|------|
| `EmperorBossSpider` | Root body. Static `Instance`. Kinematic (`rb.isKinematic=true` set EVERY `FixedUpdate`+`LateUpdate`). Walks a FIXED `waypoints` path; owns the single health Npc, defend phase, damage stages, two-stage death. |
| `EmperorBossSpiderClaw[]` (`leftClaws`/`rightClaws`) | IK legs. Pure visual + RNG stepping. `PotentiallySpawnEnemy` (5 %/leg-reach) spawns adds via `spawnableUnit.GetAsset().SpawnUnitAsync(this,…)` (mono = the claw). |
| `EmperorBossSpiderRocketLauncher` | Bursts of rockets (`Instantiate` a prefab, NOT a Unit) at the LOCAL player/camera; rapid-fire below 0.1 health. |
| `EmperorBossSpiderRocket` | Homing corkscrew projectile; `ExplosionTypes.EmperorRocket` on impact (does damage). |
| `EmperorHoleBlower` | Death seam: `BlowHole` → desert transition + `SpawnLoot` (level completion). |

**Start seam:** `EmperorBossFightHelper.StartPhase2()` just `SetActive(true)` on `phase2Root` + the spider's
parent. It is reached two ways: (a) the phase-1 worm's `DeathAnimation` calls it at the end — **already mirrored
by EMP-3c** (the client replays `DeathAnimation`, so `StartPhase2` runs on both ends); (b) a scene
`SecondPhaseTrigger` (Log258 saw an F3'd client step it). Once active: `Awake` (npc invulnerable + `Instance=this`)
→ `Initialize()` → startup animation → `StandUp()` (becomes vulnerable).

**`Initialize()` and `StandUp()` are `public` with NO C# caller in the DLL** → driven by an **animation event /
scene UnityEvent**, the same shape as the worm's dialog-invoked `StartMovement` (Log254). `Initialize` captures
`playerTransform = LOCAL player`, `AttachToBossUI`, subscribes `onDamageRecieved += OnDamageTaken` /
`onDeath += OnNpcDeath`, caches `maxHealthValue`, plays the startup animation. `StandUp` (fires at the end of the
startup animation) inits the legs and clears invulnerability. **Confirming these callers + their host-vs-client
timing is EMP-6a probe unknown #1** (one-shot stack traces).

### 9.2 Movement model — kinematic waypoint follow (NOT ballistic)
`LateUpdate` → `MaintainDistance()` computes `currentSpeed` from `Vector3.Distance(transform, LOCAL player)`
(`baseMovespeed`→`maxMovespeed` by distance) → `MoveAlongPath()` advances along `waypoints[currentWaypointIndex]
→ [targetWaypointIndex]`, wrapping the index at each node. `UpdateRootTransform()` adds `LookAt(local player)` +
sin/perlin secondary motion. `UpdateStepping()`/`MoveLegsInGroup()` drive the IK legs (RNG: `legTargetRandomness`,
`groupTimingRandomness`, per-leg timing offsets).

> **Divergence source (the core sync problem, milder than the worm):** the spider is kinematic and follows a
> *deterministic* path — no ballistic RNG — but its **speed depends on the LOCAL player position**, so each end
> advances the path at a different rate and the two spiders drift in PATH PROGRESS (waypoint index + fractional
> position). There is **no physics spiral** (kinematic, transform-driven), so the client will NOT have the worm's
> 1 fps problem — the fix is purely positional, not a physics stopgap.

### 9.3 Damage / health / defend / stages
- **Single health owner:** the spider `npc` (`AttachToBossUI(true)` in `Initialize`). All hits land on it. It is
  **pre-placed in the scene** (under `phase2Root`) but activated late — **whether a client hit rides the ordinary
  roster `ClientHitRequest` path (`[ClientHit] ACCEPT`) or quarantines as "client-only" like the worm tail (needs
  a bespoke EMP-3d-style single-target route) is EMP-6a probe unknown #3.**
- **Defend phase:** `OnDamageTaken` at `health/max ≤ defendTriggerHealthPercent` (0.5), once (`hasTriggeredDefend`)
  → `TriggerDefendPhase` → `ExecuteDefendPhase` coroutine: **invulnerable for `defendDuration` (3 s)** while two
  legs cover the body. Host-authoritative (only the host reaches the threshold once damage is host-authoritative);
  mirror the trigger, client replays `TriggerDefendPhase`.
- **Damage particle stages:** `UpdateDamageParticleEffects` at `≤0.4 / 0.25 / 0.1` health plays particle systems;
  at `≤0.1` also `emperorBossSpiderRocketLauncher.ActivateRapidFire()` (doubles rockets, halves interval). Mostly
  cosmetic, but rapid-fire is a mechanic → mirror off the health-fraction crossings (host-authoritative).

### 9.4 Death — two stage
1. `OnNpcDeath` (npc `onDeath`): stops defend, destroys legs sequentially (`DestroyLegsSequentially`, RNG order),
   stops the rocket launcher, `isWalkingToDeath = true`, floors health to `maxHealthValue * minHealthThreshold`
   (0.03), blinks the boss bar + repeated white flash. The spider then **walks (fast) to a death waypoint**.
2. `CheckDeathTriggerReached` (in `LateUpdate` while walking) matches `waypoints[currentWaypointIndex]` against
   `deathWaypointData` → sets `blower` → `ExecuteActualDeath()`: `isDead`, death animation, detach boss UI,
   `PlayerProgress.SetCheckpointReached("BossDead_Emperor")`, stop music. `BlowHoleFromAnimation()` (an **animation
   event**) then fires `blower.BlowHole()` → the **desert transition + `SpawnLoot`** (level completion seam).

Death is host-authoritative and stateful (RNG leg order, walk-to-waypoint, floor-health top-up) → same lesson as
the worm: **replay the real native methods on the client** (mirror `OnNpcDeath`, and the walk-to-death converges
because position is host-streamed so both reach the same death waypoint; `ExecuteActualDeath`/`BlowHole` are
animation-event-driven and run per-end) rather than re-deriving. Loot / checkpoint stay host-authoritative
(client suppresses `PlaceLoot`/save like Lucia/Witch terminal death).

### 9.5 Sync design (host-authoritative) — the plan EMP-6a must validate
- **Position:** EMP-3a-style stream the spider root transform (position + yaw), client suppresses
  `MaintainDistance`/`MoveAlongPath`/`UpdateRootTransform` and runs `UpdateStepping`/IK locally for the legs. No
  kinematic/collider gymnastics needed (already kinematic, no broadphase churn like the worm — but confirm with the
  probe's position timeline). Snapshot-interpolate like the worm head (reuse the EMP-3a interpolation pattern).
- **Startup:** gate `Initialize`/`StandUp` so both ends stand up together (the invuln window must match) — approach
  depends on probe unknown #1 (if animation-event-driven and deterministic, may already be in step; if not, a
  host-authoritative commit like EMP-4).
- **Damage:** single target; reuse `BossDamageReflect.TryApplyRealDamage` on the host `npc`. Route via roster if
  the probe shows `[ClientHit] ACCEPT`, else a bespoke EMP-3d-style message.
- **Defend / stages:** broadcast the health-crossing events; client replays `TriggerDefendPhase` /
  `ActivateRapidFire` (don't re-derive from local health).
- **Adds (claw spawns):** ride the RT3 `UnitSO.SpawnUnit` chokepoint (mono = the claw); same special-add handling
  as the worm sections / Cousin arms.
- **Rockets:** each end fires at its own local player — evaluate from the probe whether that is acceptable
  (per-end cosmetic + host-authoritative player damage) or needs host-authoritative rocket spawn.
- **Death:** host-authoritative two-stage death; mirror `OnNpcDeath` (replay native), converge the walk via the
  position stream, suppress client loot/checkpoint.

### 9.6 EMP-6a probe — `EmperorSpiderDiagnostics` (observe-only, `EnableEmperorSpiderDiagnostics` + `LogBossEncounter`)
Postfix-only, tagged `side=host/client`. Captures: **`Awake`** (one-shot manifest: legs L/R, waypoints, npc UnitId,
add UnitId), **`Initialize`/`StandUp`** (+ one-shot caller **stack trace** — unknown #1), **`OnDamageTaken`**
(throttled: health-fraction + `hasTriggeredDefend`), **`TriggerDefendPhase`**, **`OnNpcDeath`** /
**`ExecuteActualDeath`** / **`BlowHoleFromAnimation`** (one-shot each — the two-stage death + desert seam), a
throttled **`LateUpdate` position/state timeline** (pos, `currentWaypointIndex→targetWaypointIndex`,
`currentSpeed`, health, `isInStartupAnimation`/`stoodUp`/`isDefending`/`isWalkingToDeath`/`isDead` — the
divergence data; alloc-free hot path via a direct-float throttle), and a throttled **`LaunchMissile`** marker
(per-end rocket firing). **No functional/suppression patches** — the sync code comes after this data.

**Live test (EMP-6a):** host + client both reach the Emperor and kill the phase-1 worm (EMP-3c hands off to
phase 2). Enable `LogBossEncounter`. Collect one host + one client log through: spider stand-up → damage it past
50 % (defend) → past 0.4/0.25/0.1 (stages/rapid-fire) → kill it (two-stage death + desert transition). Diff the
two logs to read: (1) who calls Initialize/StandUp and whether both fire on the client; (2) the position/waypoint
divergence magnitude; (3) whether client hits ACCEPT via roster or quarantine; (4) defend/stage/death timing per
end. That validates §9.5 before any sync code is written.

### 9.7 EMP-6a probe result (Log259 — one clean two-end run, 0 exceptions)
The full flow ran (worm killed → EMP-3c handoff → spider stand-up → defend at 50 % → stages → two-stage death →
desert transition). All three unknowns resolved:

- **#1 Initialize / StandUp callers (identical both ends):** `Initialize` is a **NodeCanvas graph action**
  (`ExecuteFunction_Multiplatform.OnExecute → reflection Invoke`) — the **same transport as the worm's
  StartMovement** (Log254), fired per-end. `StandUp` is an **animation event** (no managed caller above it in the
  stack) at the end of the startup animation, per-end. In this run both ends stood up at the same pos
  `(-15.6,-95.1,-2.5)` at nearly the same time (EMP-3c enters phase 2 together + fixed-length startup animation), so
  **startup is not badly desynced** → a startup gate is low priority.
- **#2 Position / path divergence:** the spider walks the SAME fixed 111-waypoint path; the two paths are
  shape-identical (both hug x≈81.4 climbing +z). Worst mid-fight gap ≈ **10 m in z** (at the death trigger: host
  z=86 vs client z=96, because damage timing differs → walk-to-death starts at different moments), and the two ends
  **re-converge exactly at the death waypoint** `(81.6,-103.8,111.8)` (`CheckDeathTriggerReached` snaps). Bounded,
  no physics spiral (kinematic) → EMP-3a-style root-transform streaming is straightforward.
- **#3 Damage routing (key):** the spider `npc` is a **normal level-manifest roster unit** (`[LevelGen]
  RegisterAndSpawnUnit` + `[WorldRoster] Bound 1:1 dist=0.0m`) — it does **NOT** quarantine like the worm tail (the
  quarantined units are the leftover phase-1 `ShavwaEmperorWormSection`s). **But damage is currently NOT synced at
  all:** `clientHitSent=0` / `hostHitRecv=0`, each end damages its own local spider → health diverges hard (host at
  z=87 already 0.030 floor while client at z=88 is still 0.255). The client's local hits on its own live boss are
  NOT forwarded (the client runs its own boss, not a puppet). So spider damage sync must be BUILT; a bespoke
  single-target route (EMP-3d analog) is the fit, since the client spider becomes a visual puppet under
  head-streaming (it won't run local combat).
- **Defend / stages** fired on both ends independently at their own local health crossing 0.5 → confirms they need
  host-authoritative mirroring. **EMP-3c handoff worked** (`host DeathAnimation broadcast seq=1` →
  `client mirrored DeathAnimation seq=1` → both StartPhase2 → both spiders). A **client double-spider spawn** was
  seen (a host-id-range instance spawned then `RemoveNpc`'d, the client's own `-250508` kept + bound) — the known
  client level-reload / roster-reconcile churn (the worm log also shows two "tracking a new worm instance"), not
  spider-specific; one live spider fought.

**Net:** §9.5 is validated. EMP-6b = (a) EMP-3a-style root-transform streaming + client suppression of
`MaintainDistance`/`MoveAlongPath`/`UpdateRootTransform` (keep legs/IK local, reuse the worm-head interpolation);
(b) EMP-3d-style bespoke single-target damage authority on the spider `npc` (health syncs back via the existing
roster enemy-state — verify); (c) host-authoritative defend/stage/rapid-fire broadcast → client replays the native
method; (d) host-authoritative two-stage death mirror (replay `OnNpcDeath`, converge the walk via the position
stream, suppress client loot/checkpoint). Startup gate is low priority (proven near-synced).

**User clarifications (2026-07-02) that reshaped EMP-6b:** (1) the near-synced startup in Log259 was **coincidental**
— both players finished the phase-2 dialog at ~the same time; dialog sync does NOT cover phase 2, so the startup
gate is actually **required**, not optional. (2) The phase-2 arena is **underground, entered by the player actively
jumping into the pit below the phase-1 arena** → real games WILL have players who haven't jumped down (or not yet)
when another player activates the boss. So the fight-start gate must be "whoever commits starts it for everyone,
out-of-pit players not required" (FF14 RM intent). The host-authoritative position stream makes this safe: a player
still up top just has a host-driven spider running down in the pit.

### 9.8 EMP-6b IMPLEMENTED (not yet tested) — spider sync
`NetEmperorSpiderSync` + functional patches in `EmperorSpiderDiagnostics` (registered unconditionally, before the
probe gate) + messages 63–67. All host-authoritative, reusing the worm's building blocks:
- **Position (msg 63, Unreliable):** host `LateUpdate` prefix captures the spider body transform + both waypoint
  indices (20 Hz); a linked client applies the snapshot-interpolated pose (reused worm-head interpolation) and snaps
  the waypoint indices, with `MaintainDistance` prefix-blocked so the local path advance can't fight the stream.
  Legs/IK + cosmetic root motion + `CheckDeathTriggerReached` run natively off the streamed pose.
- **Fight-start gate (msg 64/65, EMP-4 analog):** `Initialize` prefix — host commits inline + broadcasts + runs it;
  a linked client blocks its own local Initialize (its phase-2 dialog) and requests, then runs it via the reentry
  invoke. Keyed by spider instanceID (self-rearms per encounter). A client request before the host spider is live is
  deferred to the next `HostCapture`; a commit received before the client's Initialize uses the `Instance` fallback.
  → all ends stand up together regardless of who does the dialog / who has jumped into the pit.
- **Damage (msg 66, EMP-3d analog):** `TryClientSpiderHit` in `Npc_ReceiveDamage_Pre` (after the worm hook) routes a
  client hit on the spider npc to the host's real `ReceiveDamage`; host mechanic advances; health syncs back via the
  existing roster enemy-state (spider is roster-bound, §9.7).
- **Defend / rapid-fire / death (msg 67):** host postfix on `TriggerDefendPhase` / launcher `ActivateRapidFire` /
  `OnNpcDeath` broadcasts a coded event once per encounter; client replays the native method. Death replays
  `OnNpcDeath` (walk-to-death); the walk converges to the same death waypoint via the position stream, where the
  client's native `CheckDeathTriggerReached` fires the real `ExecuteActualDeath` (blower resolved). The stream keeps
  driving through walk-to-death and only stops once `isDead` (post-ExecuteActualDeath).
- **UNCERTAIN — verify in test (the one part not confidently reasoned):** how the client spider actually *dies*.
  With client damage suppressed, the client npc no longer dies from local damage; it dies via (a) our OnNpcDeath
  replay (which does NOT call `npc.Die()` → no `SpawnLoot`) and/or (b) the generic roster enemy-death-mirror (which
  DOES call `Die()` → `SpawnLoot`). Log259 showed BOTH ends `SpawnLoot` (double loot) in the pre-6b double-boss. The
  test must reveal whether 6b yields double loot / a double OnNpcDeath fire / an un-Die()'d lingering client npc — and
  whether client loot/checkpoint (`SpawnLoot`/`SetCheckpointReached("BossDead_Emperor")`) need suppression (Lucia/
  Witch pattern). Everything else (position / fight-start / damage / defend / rapid-fire) is the confident core.
- Pure-cosmetic damage-particle stages (0.4/0.25 blood) are NOT synced (client's `OnDamageTaken` no longer fires) —
  minor, deferred. Build 0 err, deployed both ends.

### 9.9 EMP-6b test (Log260) + EMP-6c fixes
**Log260 (one two-end run, 0 exceptions, user manually quit before the desert entrance):** position sync is
**lockstep** (host `sent transform seq/pos` ↔ client `recv` identical); the fight-start gate worked (client picked
first → requested → host committed → both Initialize'd); damage is host-authoritative and works — **18 client hits
= 18 host-applied**, the host spider went 0.999→0.030→death driven entirely by the client (the host player wasn't
in the pit); death mirrored cleanly. **Double-loot fear resolved:** host `SpawnLoot`=1, client `SpawnLoot`=**0** —
with client damage suppressed the client npc never `Die()`s (it dies via the OnNpcDeath replay + ExecuteActualDeath,
neither of which calls `Die()`), so loot is host-only for free.

**Three real issues found → fixed as EMP-6c:**
1. **Boss only chased the host (msg 63 unchanged, targeting fix):** the spider's `playerTransform` is its LOCAL
   player (host's own); with the host up top it raced at max speed hunting an absent player, so the client (down in
   the pit) couldn't catch it. Fix: on the host, `UpdateHostTarget` (every frame in `HostCapture`) points
   `playerTransform` at a small anchor Transform placed at the NEAREST of all players (host + remote ghosts via
   `ForEachRemotePlayerPosition`). Rockets are unaffected (they target `PlayerUnit`/camera separately).
2. **Client boss health bar frozen:** the P2 arena has **no net-run-state**, so the generic enemy-state health
   mirror is inactive (`enemyStateTargets=0`, client `hp` stuck at 1.000). Fix: the transform stream (msg 63) now
   also carries the spider's absolute `currentHealth`; the client writes it to its spider npc via
   `NetGameplayProbeManager.TryWriteUnitHealth(npc, hp, raiseEvent:true)` (new public wrapper — the existing writer
   hard-coded `raiseEvent:false`, which wouldn't update the attached bar). Written only on real change (>0.5 HP).
3. **Dialog not disabled after fight-start → a late player re-triggered it (2nd host `Initialize` mid-fight):** fix
   nulls the spider npc's `dialog` on every commit path (`DisableSpiderDialog`, same primitive as the worm/EMP-4)
   AND the fight-start gate now hard-blocks any `Initialize` once committed for that spider instance.

Deferred per user: rockets stay per-end/local (future: Cousin-arm-style multiplayer balance or pure-visual +
collidable); late-jumping players teleporting to the first phase-2-trigger player's position (future, via the pit
entrance trigger, no room-membership needed). Build 0 err, deployed both ends — pending re-test.

### 9.10 EMP-6c test (Log261) + EMP-6d fixes
**EMP-6c confirmed working (user):** the boss now chases the NEAREST player (#2 fixed — host absent, client in the
pit gets engaged), and a late-arriving player no longer re-triggers the dialog (#1 gate fixed). 0 real exceptions
(the one host warning is the benign pre-existing BatchedNPCRaycasts roster-race, already swallowed).

**Issues found → EMP-6d:**
1. **P2 spider dialog stuck open, not closeable by the host (the main bug):** EMP-6c added `DisableSpiderDialog`
   (nulls `npc.dialog`, prevents *re-opening*) but omitted the worm's `FinalizeLocalDialog` (`Graph.Stop(true)`,
   *closes* the currently-open dialog). So when the host committed, the non-picking client's already-open spider
   dialog was never closed. **Fix:** call `BossDialogReflect.TryFinalizeCurrentDialog` in the two mirror-apply paths
   (`OnFightStartCommitReceived`, `HostCommitFightStart`), exactly like the worm — now committing closes every
   non-picking end's dialog (P1 already behaved this way, which is why P1 "could be ended"). (The separate "client
   can't click-advance the boss dialog" is the general host-authoritative-dialog behaviour — the host driving the
   commit/close makes the flow functional; independent client advance is a bigger dialog-input item, deferred.)
2. **P2 no hit feedback (white flash):** the client's local damage is suppressed so vanilla `DoWhiteFlash` never
   fires. **Fix:** play `DoWhiteFlash` optimistically on the client spider npc in `TryClientSpiderHit`, skipped while
   `isDefending` (boss invulnerable → host rejects → no flash is correct) or dead. (Health bar itself synced fine —
   EMP-6c #4 confirmed working.)
3. **P1 worm client health bar intermittent:** same root cause as the P2 health gap — the worm arena doesn't reliably
   drive the generic enemy-state mirror and the worm tail is a *quarantined* runtime add, so the bar was stale
   between destroys. **Fix:** the worm head stream (msg 57) now also carries the tail's absolute currentHealth; the
   client writes it to its tail npc (`TryWriteUnitHealth`, raiseEvent, on-change) — the same trick the spider uses.
4. **Host 1 fps (NOT fixed — confirmed pre-existing, phase-1):** `[UpdateProf]` showed our Update body at 50–104 ms
   (one 551 ms) while the frame hitches were 1000–2391 ms with `gc0Δ=0` → the cost is **native, outside our C#**, and
   every hitch is tagged `[EmperorWormFrame]` (the phase-1 WORM watchdog). It is the same native `EmperorBossWorm`
   ballistic-rigidbody `FixedUpdate` physics that was the *client's* original 1 fps (§8.5/8.8c) — the user's
   connection is correct. The client escaped it by NOT running the physics (kinematic head-stream); the **host is the
   authority and must run the real worm**, and an earlier collider ablation showed the cost is the rigidbody
   integration itself, not the broadphase, so disabling host colliders won't help. This needs the worm re-architected
   off ballistic physics (loses the jump feel) or a real Unity Profiler pass — **deferred hard problem, orthogonal to
   the phase-2 spider work.** (Worse over a multi-encounter session: Log261 had 4 worm starts in one session.)

Build 0 err, deployed. EMP-6d re-test: P2 dialog closes on every end when either player commits; P2 shows white-flash
on client hits (not while defending); P1 client boss bar tracks the host smoothly.

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
