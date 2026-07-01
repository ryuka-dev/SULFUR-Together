# Boss Source Audit (Phase 5.4-E5)

Reverse-engineered from the real game DLLs (`PerfectRandom.Sulfur.Core.dll`, `PerfectRandom.Sulfur.Gameplay.dll`) via ilspycmd. This is the authoritative reference for the multiplayer boss pipeline. **No feature code was written for this phase** — the goal is to stop log-driven guessing and pin down each boss's real damage / phase / spawn / dialog links before implementing `BossDamageAuthority`.

Method/field names below are exact. Line references are into the decompiled bodies (for re-derivation).

> **What was built on top of this audit** (Phases 5.4-E → G7, runtime spawn 5.5-RT) is documented in **[BossAuthority.md](BossAuthority.md)** — the architecture, message map, and per-boss implementation. This file stays the *reverse-engineered reference*; that file is the *implementation map*.

---

## 0. Common infrastructure (ground truth)

### Damage pipeline — the ONE real entry
- `Unit.ReceiveDamage(float damage, DamageTypes, DamageSourceData, Hitmesh.Data hitbox, Vector3? hitPosition)` (`Unit.cs:1600`, `virtual bool`) is the real damage application:
  - fires `onHitRecieved?.Invoke(this, hitPosition)` and `onHitReceivedFrom`
  - parry / `hitbox.isInvulnerable` / `isInvulnerable` / petrify → returns `false` (no damage)
  - applies `Stats.ModifyStatus(Status_CurrentHealth, -num)` (`Unit.cs:1803`)
  - fires `onDamageRecieved?.Invoke(this, num, sourceData)` (`Unit.cs:1805`) ← **all boss mechanics hang off this delegate**
- `Npc.ReceiveDamage(...)` (`Npc.cs:1741`) overrides: if `unitState==Dead` handle gibs/corpse, else `base.ReceiveDamage(...)`.
- **Implication:** to make a client hit "count" on a boss sub-unit, the HOST must call that unit's `ReceiveDamage` (or the specific mechanic delegate). Writing `Stats.SetStatus(92,...)` alone does NOT fire `onDamageRecieved`, so it never advances mechanics.

### Health + bar
- Health lives in `Unit.Stats`: `Status_CurrentHealth = 92` (current), `Stat_MaxHealth = 60` (max). `Unit.GetCurrentHealth()` = `Stats.GetStatus(92)`; `Unit.normalizedHealth` / `GetNormalizedHealth()`.
- Boss bar: `Unit.AttachToBossUI(bool)` → `UIManager.bossUI` (type `PerfectRandom.Sulfur.Core.UI.BossHealth`) `.Attach(this)` / `.Detach(this)`.
  - `BossHealth.Attach(Unit)` sets `unitTracked` and **subscribes `unit.onHealthChange += h => newFillAmount = h`** via `Delegate.Combine` (re-subscribes every call → attach ONCE).
  - The bar value is driven by the `onHealthChange` event, NOT by polling. `Unit.OnStatusUpdated(id, prev, new)` fires `onHealthChange?.Invoke(normalizedHealth)` when `id==Status_CurrentHealth`.
  - `SetStatus(92, v, false)` writes silently (no event) → bar won't move unless we fire `onHealthChange` ourselves (current E4.2 workaround) OR we route through real damage which fires it.

### Dynamic spawns
- `UnitSO.SpawnUnitAsync(MonoBehaviour mono, Vector3, Quaternion)` (instance, `mono` = owning boss) → `await load` → static `UnitSO.SpawnUnit(UnitSO, GameObject, Vector3, Quaternion)` (sync, returns the Unit). Static `SpawnUnit` also does `GameManager.units.Add` + `AddNpc`.
- The spawned Unit's C# type is `Npc` for almost everything; the **UnitId (`unitSO`)** is the real discriminator (e.g. `BlackGuildLuciaEye`, `GoblinCousinArm`).

### Generic phase engine (`BossPhase`)
- `BossPhase.Update()` → `CheckValidTransition()` → iterates `bossPhaseConditions` and `CanTransitionToPhase(cond)` (health-percent gated via `IndexPhaseAtHealth`), then `SetTransitionVars` → `StartTransition()` → `bossFightHelper.StartPhase(currentPhaseIndex)`.
- So **generic** `BossFightHelper` bosses transition by `bossUnit` health %. Lucia and Witch do NOT use `BossPhase` (they have their own).

### Dialog
- `DialogController` (per-end) subscribes static `DialogueTree.On*`. `DialogueTree.currentDialogue` (static) + inherited `Graph.Stop(bool)` finishes a running dialog (→ `OnDialogueFinished` → unlock + `SetCurrentSpeakable(null)`). The boss start methods are called from NodeCanvas ExecuteFunction nodes inside the dialog graph (data, not code).

---

## 1. Generic `BossFightHelper` (base; e.g. Terrorbaum)
1. **Start entry:** `TriggerFight()` (virtual). Sets `fightStarted=true`, `playerUnit`, `bossUnit.AttachToBossUI(true)`, `bossUnit.onDeath += OnBossDead`, loot/music, `bossPhases.StartBossPhases(bossUnit, playerUnit)`.
2. **Dialog entry:** none generic (some subclasses add interact).
3. **Main health Unit:** `bossUnit` (`[SerializeField] Npc`).
4. **Bar attach:** `bossUnit` (in `TriggerFight`).
5. **Real hit entry:** `bossUnit.ReceiveDamage` → `OnDamageReceieved(unit, dmg, src)` (only does `regainPhaseHealth` phase-floor protection; NOT a generic mechanic driver).
6. **Mechanic progression:** `BossPhase` (health-% conditions) via `bossPhases`.
7. **Dynamic sub-units:** none generic.
8. **Spawn type:** n/a.
9. **Client→host target:** `bossUnit`.
10. **Host-authoritative:** `bossUnit` health, `bossPhases.currentPhaseIndex`.

## 2. `DesertClauseBossFightHelper` (composite)
1. **Start entry:** `OnStartInteractWithBoss()` (`:1055`, player interact → camera + `DelayIntro` coroutine → animator "IntroStarted" → anim event → override `TriggerFight()` `:388` which calls `base.TriggerFight` + disables collider/physics, animator "TriggerFight"/"BossStarted").
2. **Dialog entry:** boss NPC dialog; `bossNPC.dialog.graph` is swapped (`sniperDialogue`/`terminatorDialogue`). Interact = `OnStartInteractWithBoss`.
3. **Main health Unit:** `bossUnit` / `bossNPC` (inherited; the old-man body).
4. **Bar attach:** `bossUnit` (base `TriggerFight`).
5. **Real hit entry:** `bossUnit.ReceiveDamage` → base `OnDamageReceieved`.
6. **Mechanic progression:** `BossPhase` (health-%) + scripted sub-mechanics: `sniperBase`/`terminatorBase` (`DesertMissileBase`), `desertClausePerimeter`, digga enemies (`diggaEnemySO`), pike enemies on the **mount** (`livingPikeAmountFirst/Second`, `min/maxAmountToSpawnOnMount*`). **Composite**: the "mount/坐骑" the user saw is part of the boss rig, plus spawned pike/digga adds.
7. **Dynamic sub-units:** digga enemies, pike enemies (spawned), missiles.
8. **Spawn type:** mixed — `SpawnUnitAsync` (digga/pike) + pre-placed `DesertMissileBase` activated.
9. **Client→host target:** main = `bossUnit`; adds = digga/pike Npcs.
10. **Host-authoritative:** `bossUnit` health, `bossPhases`, perimeter/sniper/terminator state, digga/pike spawns.

## 3. `LuciaBossFightHelper` (health-driven, custom phases)
1. **Start entry:** `public new void TriggerFight()` (`:236`, hides base virtual). Sets `fightStarted=true`, `bossUnit.AttachToBossUI(true)`, `bossUnit.onDeath += OnBossDead`, loot/music, `StartPhase(1)`. Reached via dialog → `LuciaBossFightTrigger.TriggerFight()` → `luciaHelper.TriggerFight()`.
2. **Dialog entry:** dialog "attack" option → `LuciaBossFightTrigger.TriggerFight`.
3. **Main health Unit:** `bossUnit` (inherited). (`BossFightHelper: No valid phases defined!` is harmless — Lucia ignores `bossPhases`.)
4. **Bar attach:** `bossUnit`.
5. **Real hit entry:** `bossUnit.ReceiveDamage`. Eyes: `BlackGuildLuciaEye` units, `unit.onDeath += EyeDied`. Henchmen: `activeHenchmen` / on death → `deadHenchmen`.
6. **Mechanic progression:** own `PhaseUpdate()` (`:305`) gated on **`bossUnit.normalizedHealth` thresholds** (0.9→p2, 0.8 or all henchmen dead→p3, 0.7→p4; second loop 0.5/0.4/0.3) AND `deadHenchmen.Count==activeHenchmen.Count`. Eyes: `EyeDied` → when `spawnedEyes.Count==0` → `RestartPhases()`.
7. **Dynamic sub-units:** `spawnedEyes` (`BlackGuildLuciaEye`, `SpawnEyes` loop, same position), `activeHenchmen` (`BlackGuildAssassin`), `spawnedMaidens` (`BlackGuildMaiden`, from corpses).
8. **Spawn type:** all `SpawnUnitAsync` (eyes/henchmen/maidens), deterministic count loops.
9. **Client→host target:** main=`bossUnit`; eye hit→host eye + `EyeDied`; henchman death→host `deadHenchmen`.
10. **Host-authoritative:** `bossUnit` health (drives phases), `activeHenchmen`/`deadHenchmen` counts, `spawnedEyes` lifecycle, `currentPhase`, `restartCounter`.
- **User's key point confirmed:** syncing health to the client UI is NOT enough — the client's hits must reach the HOST's `bossUnit.ReceiveDamage` / eye `onDeath` / henchman death so `deadHenchmen`/`spawnedEyes`/phase advance on the host. Otherwise client damage "reverts".

## 4. `WitchBossController` + `WitchPhase2` (real/illusion)
### WitchBossController
1. **Start entry:** `EventStarted()` → `FightStartRoutine` coroutine → `StartFight()` (private; sets `fightStarted=true`, `witchMainUnit.AttachToBossUI(true)`, locks).
2. **Dialog entry:** `WitchFightDialogTrigger` (separate). 
3. **Main health Unit:** `witchMainUnit`.
4. **Bar attach:** `witchMainUnit` (in `StartFight`).
5. **Real hit entry:** `witchMainUnit.onDamageRecieved += OnDamageMainWitch` (phase 5 `AttachDamagers`); during phase 2 the REAL hit comes via `WitchPhase2.RealWitchTakeDamage` → `bossController.OnDamageMainWitch`.
6. **Mechanic progression:** `ChangePhase(WitchPhase)` (`Waiting=0,Intro=1,Phase1_EightFlying=2,...Phase6_EggLaying=7,Dead=8`), driven by phase controllers (mechanic completion), NOT pure health. `phase1..phase6` are `WitchPhaseController`.
7. **Dynamic sub-units:** per-phase: phase2 witches/illusions, `aliveAdds` (craw adds), phase3+ units, eggs.
8. **Spawn type:** `SpawnUnitAsync` per phase.
9/10. **WitchPhase2 (CRITICAL — its own manifest):**
   - `SpawnWitches()` (`:80`): `realWitchIndex = Random.Range(0, domePositions.Count)`. Spawns `witchUnit` at realIndex → `realWitchUnit`, `onDamageRecieved += RealWitchTakeDamage`; `illusionUnit` elsewhere → `onDamageRecieved += IllusionTakeDamage`; all to `spawnedWitches`, illusions also `spawnedIllusions`. All spawn at `(0,100,0)`.
   - `ShowWitches()` (`:111`): **re-shuffles** `spawnedWitches` (`OrderBy(Random.value)`) then moves `spawnedWitches[num] → domePositions[num]` and enables. → **the visible dome-position ↔ real/illusion mapping is TWO host random rolls** (realIndex + shuffle). Client cannot reproduce it.
   - `RealWitchTakeDamage` → `bossController.OnDamageMainWitch` (real health), `illusionsDissappeared=true`, `IllusionsDisappearAll()`, `EightFlying()` → `SpawnAdds()` (craw) → `EndPhase()`.
   - `IllusionTakeDamage` → disable illusion, `appearingWitches[u]=true`, remove from `aliveNpcs`.
   - **Needed:** `WitchPhase2Manifest` = host sends final `spawnedWitches` order, which dome index is the real one, illusion visible/disabled state; client routes a hit on dome-index-k to host's witch-k → `RealWitchTakeDamage` or `IllusionTakeDamage`. **Mirror alone is wrong** — if real/illusion identity differs, client sees but hits the wrong one.

## 5. `CousinHelper`
1. **Start entry:** `Trigger()`→`TriggerIntro()` (sets `triggeredByPlayer`) — that is *all* the class does. The rest is an **external behavior tree** (`U_GoblinCousin_Sequencer_1`) that reads `triggeredByPlayer`: `Introduction()` (`introPlayed=true`, teleport, camera, animator "Intro") → 2s → `Npc.Interact()` (real intro dialog) → 1s → `SpawnArm(pos)` → 0.1s → `AttachToBossUI` + `StartFight()` (`FightStarted=true`, unlock, `EnablePoolDamagers(true)`). **`StartFight` is a behavior-tree step, NOT an animation event.** The dialog is non-interactive; the SP gate is the pause freezing the tree's `WaitForSeconds` (see BossPreFightFlow.md §0/§3d).
2. **Dialog entry:** `PlayerTrigger "CousinTrigger"` → `Trigger` (ITriggerable). The dialog itself opens from the behavior tree's `Npc.Interact()`, not the trigger.
3. **Main health Unit:** `owner` (`GetComponent<Unit>()`).
4. **Bar attach:** `owner` — **confirmed (full decompile): `AttachToBossUI` is a behavior-tree step right before `StartFight`, NOT in `StartFight` itself and NOT an animation event.** The host re-entry guard (commit `c853553`) stops the tree's intro from re-running, so the E4.2 manager force-attaches `owner` once instead.
5. **Real hit entry:** `owner.onDamageRecieved` (in `Setup`) decrements `damageUntilSubmerge -= damage`. `owner.onDeath += CousinDeath`. Arm: `currentArm` (`armUnit`), invulnerable, `onDeath += OnArmDeath`.
6. **Mechanic progression:** damage-driven submerge: when `damageUntilSubmerge` hits 0 → `Submerge()` (animation) → `Reappear()` → `MoveToNewPool()` (`damageUntilSubmerge = max` reset). Pool damagers, henchmen waves (`SpawnHenchmen`).
7. **Dynamic sub-units:** `currentArm`/`spawnedArms` (`GoblinCousinArm`, `SpawnArm`/`SpawnArmsInLoop`), henchmen (`henchmenFirstSpawn`/`SecondSpawn`).
8. **Spawn type:** `SpawnUnitAsync` (arms, henchmen). Arms tracked in `spawnedArms`, die via `DieDelayed`/`OnArmDeath`.
9. **Client→host target:** main=`owner`; arm hit/death→host arm `OnArmDeath`.
10. **Host-authoritative:** `owner` health + `damageUntilSubmerge`, `isSubmerged`/`waitingToReappear`, `currentPool`, `currentArm`/`spawnedArms` lifecycle, `HasSpawnedArm`.

## 6. `EmperorBossFightHelper` + `EmperorBossWorm` + `EmperorWormSectionController` (multi-entity)
> **Deep dive:** [EmperorBossAudit.md](EmperorBossAudit.md) — full phase-1 worm reverse-engineering (spawn,
> ballistic movement + RNG sources, weakpoint/section-destruction mechanic, death/phase-2 seam, sync design,
> and the probe plan). The 10 points below are the summary.

1. **Start entry:** `EmperorBossFightHelper.OnPlayerSpawned` → `StartPhase1()`/`StartPhase2()` (auto, NOT dialog/TriggerFight). There IS a pre-fight dialog (separate scripted NPC) — that dialog's client-input freeze is NOT our boss code.
2. **Dialog entry:** separate scripted trigger (out of scope of boss adapters).
3. **Main health Unit:** **the worm has no single health Unit.** `EmperorBossWorm` builds `wormNpcs` (List<Unit>) + `sectionControllers` (List<EmperorWormSectionController>); the bar attaches to `lastSectionNpc` (tail), `healthPerSection = 1/(numberOfSections-1)`, last section HP set to `GetCurrentHealth()*0.1f` (`EmperorWorm.cs:367-376`).
4. **Bar attach:** `lastSectionNpc.AttachToBossUI(true)` — moves as sections are destroyed.
5. **Real hit entry:** per-section Npc `ReceiveDamage`; `SetInvulnerability(bool)` per section gates which is vulnerable. Weakpoint = the vulnerable section.
6. **Mechanic progression:** `StartMovement()`→`bossActive=true`; `FixedUpdate` drives jumps (`JumpTo`, `jumpTimeRelativeToHealth.Evaluate(1-lastSectionNpc.normalizedHealth)`), underground travel; sections destroyed shorten the worm. Spider phase = `EmperorBossSpider`.
7. **Dynamic sub-units:** worm sections (`wormNpcs`/`wormSections`), spider + claws + rockets.
8. **Spawn type:** sections spawned at `Initialize`; spider pre-placed root activated.
9. **Client→host target:** section index ↔ host section; vulnerable/destroyed/belowGround per section.
10. **Host-authoritative:** section count, per-section HP/invulnerability/destroyed, `lastSectionNpc`, `isUnderground`/`currentJumpCount`/`targetPosition`, spider state. **Double-worm** = client's local `EmperorBossWorm.StartMovement` drives its own worm independently (E3 has a reversible suppression scaffold, default off).

---

## 7. Cross-cutting conclusions → `BossDamageAuthority`

**The core architecture the next phase must build:**

```
Client local hit on a boss-related Unit
  → identify (encounter, target-role, sub-unit identity)
  → send ClientBossHitRequest to Host (reuse/extend existing ClientHitRequest)
  → Host resolves the REAL target Unit / special handler:
        Lucia main      → bossUnit.ReceiveDamage
        Lucia eye       → eye.ReceiveDamage (→ onDeath → EyeDied)
        Lucia henchman  → henchman.ReceiveDamage (→ deadHenchmen)
        Witch p2 real   → RealWitchTakeDamage path (OnDamageMainWitch)
        Witch p2 illus  → IllusionTakeDamage
        Witch main      → witchMainUnit.ReceiveDamage / OnDamageMainWitch
        Cousin main     → owner.ReceiveDamage (→ damageUntilSubmerge)
        Cousin arm      → arm.ReceiveDamage / death
        Desert main     → bossUnit.ReceiveDamage
        Emperor section → section Npc.ReceiveDamage (vulnerable only)
  → Host's native mechanic advances (phase/submerge/eye-clear/illusion-vanish)
  → Host broadcasts authoritative health + phase + sub-unit state
  → Client mirrors (writes health, fires bar event, applies sub-unit visible/dead state)
```

**Each adapter must declare its target-role map** (which local Unit maps to which host handler). Identity keys per family:
- Generic / Desert / Cousin main / Lucia main / Witch main: the single health Unit (bound via roster/placed).
- Lucia eyes / Cousin arms / Lucia henchmen: the **dynamic-spawn manifest** (encounter, unitId, sequence) — already built (E4), needs lifecycle (death/despawn) + hit routing.
- **Witch phase-2 real/illusion: its own `WitchPhase2Manifest`** keyed by **dome-position index**, carrying host's real index + shuffle order + per-illusion visible/disabled state + damage route.
- Emperor: per-section manifest (index, vulnerable, destroyed, belowGround) — later.

**Host-authoritative invariants (must NOT be client-decided):** `realWitchIndex` + ShowWitches order; which Lucia eye/henchman is alive; Cousin `damageUntilSubmerge`/submerge state; Emperor section vulnerability/destruction; all boss `Stats(92)` health; all phase indices.

**What's safe to keep doing both ends (mechanic visuals):** the per-phase animations/mechanics run locally on both ends (user confirmed "mechanics all correct"); only the AUTHORITATIVE STATE (health, real/illusion identity, sub-unit alive/dead, phase index) must come from the host.

## 8. Recommended implementation order (next phases)
1. **BossDamageAuthority core**: extend `ClientHitRequest` so a client hit on a boss-bound Unit (main bodies first — they're in the roster) routes to the host's real `ReceiveDamage`. Validate with Lucia main + Cousin main (single health unit). This makes client damage "count" and phases advance host-side.
2. **Boss sub-unit lifecycle** on the E4 manifest: bind host↔client adds by (unitId, seq), sync death/despawn (`EyeDied`, `OnArmDeath`, henchman death) so Lucia phase gates (`deadHenchmen`) and eye-clear advance in sync.
3. **WitchPhase2Manifest**: host real index + shuffle + illusion visible state + hit routing by dome index.
4. **Boss death sync**: `SetStatus(92,0)` does not kill — drive the real death (`ReceiveDamage` lethal or the boss's death entry) host-side and broadcast.
5. **Emperor section manifest** (separate, multi-entity).

> Until BossDamageAuthority (step 1) exists, syncing health-to-UI only will keep "reverting" because client damage never enters the host mechanic pipeline — exactly the symptom observed.
