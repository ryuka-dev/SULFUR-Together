# Boss Sync Reuse Map

A navigation map, **not** a spec — it points at the detailed docs and says *what is reusable across bosses*,
*what is inherently per-boss*, and *the default recipe for a new boss*. Detail lives in:

- [BossAuthority.md](BossAuthority.md) — the per-boss quick map (start / damage / phase / spawn / death) + files.
- [BossPreFightFlow.md](BossPreFightFlow.md) — pre-fight flow: dialog gating, room membership (RM), FF14 arena
  lockdown (LD), teleport/convergence.
- [BossSourceAudit.md](BossSourceAudit.md) — reverse-engineered per-boss mechanics (the 10-point summary).
- [EmperorBossAudit.md](EmperorBossAudit.md) — the Emperor worm deep-dive (EMP-1…3d).
- [Versioning.md](Versioning.md) §4 — the live phase registry (status of every slice).

The lesson from doing Cousin, Lucia, Witch and Emperor: **the low-level primitives are genuinely universal; the
"framework" (registered encounter + adapter + dialog-commit + arena) is universal for *normal* bosses; Emperor is a
special case that reused the primitives but opted out of the framework.**

---

## 1. The toolkit (reusable building blocks)

Each block is host-authoritative. "msg N" = `NetMessageType` value.

| Block | What it does | Shared mechanism | Bosses using it |
|-------|--------------|------------------|-----------------|
| **Damage authority** | client hit → host applies real `ReceiveDamage` (fires `onDamageRecieved`, advances the real mechanic) | `BossDamageReflect.TryApplyRealDamage` (source = `GameManager.Instance.PlayerUnit`) | Witch/Lucia/Cousin via `TryClientBossHit`→`ClientBossHitRequest`(34); Emperor via bespoke `ClientEmperorWormHit`(60) |
| **Target resolution** | which unit a client hit maps to (main / eye / arm / tail / dome) | adapter `ResolveHitTargetRole` / `ResolveHostTargetForRole` | per-boss (this is the *only* damage part that isn't shared) |
| **Death authority** | host death → broadcast → client **replays the real death method** + terminal-suppression flag | replay native `Die()`/death coroutine; `_terminalDead` (Emperor: `_clientDeathApplied`) | all four |
| **Discrete event mirror** | host does mechanic X (+ optional position) → client replays native X instead of its own RNG | `NetBossDiscreteEvent`(36) — generic carrier (`EventName` + optional pos + seq) | Cousin pool moves + CousinDeath; **candidate carrier Emperor should have used** for 58/59 |
| **Runtime sub-entity binding** | boss adds spawned after level-load (arms/eyes/illusions/sections) bound host↔client by spawn sequence | RT3 boss-add manifest `HostBossDynamicSpawn`(33) via the `UnitSO.SpawnUnit` chokepoint | Cousin arms, Lucia eyes, Witch illusions, Terrorbaum — **Emperor sections did NOT** (see §3) |
| **State / phase sync** | health / phase index / add counts | `HostBossState`(32), `HostWitchPhase`(40), Witch P2 manifest/result(41/42), Lucia eye state(37/38) | per-boss shape, shared transport |
| **Pre-fight dialog gating** | fight starts only when an in-room player dismisses the intro dialog, host-authoritative on all ends | `NetBossDialogCommit` two-phase intro/fight commit (`ClientBossDialogCommitRequest`30 / `HostBossDialogCommit`31), `GateBossFightOnDialogClose`, `DialogueTree.currentDialogue.Stop(true)` | Cousin (`StartFight`), Lucia (`TriggerFight`); **Emperor next** (gate `StartMovement`) |
| **Room membership (RM)** | host-authoritative "who is in the boss room" set | `ClientRoomEnter`(51) / `HostRoomMembership`(52) | substrate for dialog-scoping + arena lockdown |
| **FF14 arena lockdown (LD)** | seal the room, popup+teleport out-of-room players in, release on death; grace period | gate mirror `GateState`(53)/`TriggerDoors`(54); `ClientArenaEnter`(55)/`ArenaCommand`(56) Seal/Popup/Release | generic (MetalGate + SetActive-door), any boss/elite room |
| **Intro cutscene scoped to in-room** | out-of-room players don't play dialog/camera/lock; late entrant catches up | `GateBossDialogToInRoom` (RM-2b) | Cousin; the "everyone *can* dialog, opt-outs don't have to" behaviour |

---

## 2. Shared vs inherently per-boss

**Universal (reuse verbatim):** `BossDamageReflect`; the "replay the real native method" philosophy (death,
discrete events); terminal-death suppression; RM; the FF14 arena lockdown (LD); the dialog-commit handshake; the
RT3 add-binding chokepoint.

**Inherently per-boss (write new each time, but plug into the above):**
- *Target resolution* — each boss exposes different hittable units.
- *Which native methods to hook* — `StartFight` vs `TriggerFight` vs `StartMovement`; `ChangePhase`; `DestroySection`.
- *Movement / position model* — this is the big axis: **event-mirrored** (Cousin fixed pools), **head-streamed**
  (Emperor physics worm), **static/roster-puppet** (most enemies), **phase-controller** (Witch), or **pure-puppet
  with discrete state mirror** (Terrorbaum, §6 — client copy fully inert, host broadcasts the visibility-state
  mutators as discrete events).

---

## 3. Default recipe for a NEW boss

0. **Audit the "remote start" surface first** (§6 lesson): walk the decompile from the start entrypoint to
   "remote player can see / be attacked by / damage the boss" and list every local-player-presence assumption
   (entrance PlayerTriggers, animation events, LOS target acquisition, damage windows, driver state machines).
   Plan the slice against that list — do not discover these one live-test at a time.
1. **Register it as an encounter** + write an `IBossEncounterAdapter` (identity, `IsStarted`, start-chain method,
   `Describe`). This unlocks the whole framework (dialog-commit, RM, state sync) keyed by `EncounterKey`.
2. **Pre-fight:** adopt RM + the **FF14 arena lockdown** (seal / popup / teleport / release) + **dialog-commit
   gating** by default — every future boss should have "all players *can* reach the dialog; players who don't want
   to don't have to; the fight starts host-authoritatively when an in-room player commits." Gate the boss's own
   fight-start method (find its `StartFight` equivalent).
3. **Damage:** implement `ResolveHitTargetRole` / `ResolveHostTargetForRole`; reuse `BossDamageAuthority` +
   `BossDamageReflect`.
4. **Adds:** let runtime spawns ride the RT3 `UnitSO.SpawnUnit` chokepoint (`HostBossDynamicSpawn`).
5. **Phase / discrete mechanics:** broadcast via `NetBossDiscreteEvent`(36); client replays the native method.
6. **Death:** host-authoritative death → broadcast → client replays the real death method + set the terminal flag.

Only drop to **bespoke** handling (raw messages, custom streaming) for genuinely unusual mechanics — see Emperor.

---

## 4. Emperor — special case + open dialog work

The Emperor deliberately sits outside the framework because its phase-1 worm is a **pre-placed, physics-driven,
multi-section body with no TriggerFight/dialog start hook in the boss code**. It reused the *primitives*
(`BossDamageReflect`, replay-native-method, terminal flag) but invented its own transport:

- **EMP-3a** head-streaming (57) — unique to a moving physics body; no other boss needs it.
- **EMP-3b/3c** section-destroy (58) / death+`StartPhase2` (59) — replay native `DestroySection` /
  `DeathAnimation`. *Could* have ridden `NetBossDiscreteEvent`(36); used bespoke messages because the Emperor is
  not a registered encounter (no `EncounterKey`).
- **EMP-3d** damage authority (60) — bespoke single-target route because it bypasses the encounter/roster path
  (the runtime-spawned tail quarantines as "client-only"). Still calls the shared `BossDamageReflect`.
- **EMP-4** fight-start (dialog) authority (61 client-request / 62 host-commit) — host-authoritative gate on
  `StartMovement` (the dialog-choice fight-start, §4a). A linked client blocks its own local start and requests;
  the host commits (its own dialog pick commits inline) and broadcasts so every end runs the SAME native
  `StartMovement` together (Initialize/section-spawn/emergence/music in step). Bespoke messages, NOT the
  encounter `ClientBossDialogCommit`(30/31) — the Emperor is not a registered encounter (§4c). See
  `NetEmperorWormSync.TryGateFightStart`.

### 4a. Fight-start IS dialog-gated — CONFIRMED by Log254 stack trace (corrects EmperorBossAudit §1)
Decompile fact: `EmperorBossFightHelper.OnPlayerSpawned → StartPhase1()` **only toggles the spider parent off** —
it does **not** call `StartMovement()`. **Log254's `StartMovement` stack-trace probe (EmperorBossAudit §10)
pins the exact caller**: a NodeCanvas `DialogueTree` — the pre-fight dialog's final `MultipleChoiceNode`
option runs a `Jumper → ActionNode → ExecuteFunction_Multiplatform` that reflection-invokes
`EmperorBossWorm.StartMovement()`. So fight start is a **dialog-choice callback**, same shape as Cousin/Lucia
(**not** a PlayerTrigger, **not** an animation event) — **audit §1's "StartMovement fires from
OnPlayerSpawned" is wrong** and is corrected here. Log254 also shows **both ends fire `StartMovement`
independently** on their own local dialog choice (host inst=-161832 / client inst=-231850, same root/pos) →
the unsynced-start desync is real and per-end.

→ **IMPLEMENTED as EMP-4** (bespoke, NOT via the encounter machinery — §4c explains why): a functional prefix
on `StartMovement` gates it. A linked client blocks its own local start and sends `ClientEmperorFightStart`(61);
the host commits (its own dialog pick commits inline, broadcasting; a client request commits + invokes the host
worm's `StartMovement` via reentry) and broadcasts `HostEmperorFightStart`(62) so **every end runs the same
native `StartMovement` together**. A reentry flag lets the authoritative invoke pass the gate; the commit is
keyed by worm instanceID so it self-rearms per encounter and fires exactly once. A client request that arrives
before the host worm is live is deferred to the next `HostCapture`. See `NetEmperorWormSync` EMP-4 region.
**Pending live test** (below).

### 4b. Multi-phase dialog — the phase-2 complication (open design note)
The Emperor has **two** dialogs: a phase-1 intro **and a separate phase-2 dialog** (fires when the spider
activates). The Cousin-style "remove/suppress the boss dialog interactable" (Fix A) risks making the phase-2
dialog unreachable. Design intent captured from discussion:

- **Handle phase-2 dialog per-client, not by a global "restart dialog" broadcast:** when a client's *own* player
  steps on the phase-2 dialog trigger, (re)open the dialog locally for that client.
- **Why per-client, not global:** players can be in different dialog states (some did the phase-1 dialog, some
  opted out). A global restart could re-fire the *phase-1* dialog out of order for a player who skipped it (i.e.
  finishing phase 1 wrongly triggering the phase-1 dialog). Per-client, trigger-driven handling keeps each end's
  phase ordering correct.
- This must compose with FF14 dialog-scoping: "everyone *can* dialog (per phase), opt-outs are respected."

### 4d. Emperor gate takeover + dialog behavioural model (user spec, 2026-07-02)

**Gate (mod fully owns the Emperor door).** Two triggers exist (Lucia-shaped):
- A **front-of-door trigger** (outside the arena) that drives the **door**. Any player crossing it opens the door
  for everyone already — synced, correct, and attributed to every peer registering the others as ghost players
  (the door's PlayerTrigger fires on each end's ghost too). **Leave this alone.**
- A **large inside trigger** (covers most of the interior, like Lucia's) that opens the **dialog** — and vanilla
  **bundles a door-CLOSE onto it**. That bundled close is what must be **excised** (do NOT let the inside dialog
  trigger close the door directly). The user's earlier "don't close the door immediately" is exactly this.
- **Requirement (simplified per user 2026-07-02):** the mod TAKES OVER the door — **once the mod closes it, it
  STAYS closed until release** (boss death / lockdown release). Do **not** chase *why* it reopens; instead, while a
  lockdown is **active** (post-seal, pre-release) for the arena, **block any `MetalGate.Open` on that arena's gate**,
  and allow it only on release.
- **IMPLEMENTED as LD-2f** (in `ArenaLockdownManager` + `MetalGatePatches`, no new config). Tracked by the **gate's
  InstanceID, NOT position** — Log257 proved the Emperor's dialog/seal trigger `NEWPlayerTrigger_StartEvent`
  (5.4,7.5,6.1, fires `[Npc.Interact, MetalGate.Close]`) is **~50 m from the gate** it closes (the gate the front
  trigger `PlayerTrigger_OpenDoor` @ -44,11,-0 opens), so a position radius never matched → grace didn't defer the
  close (door closed immediately) and held never blocked the reopen. Fixed by resolving the gate the trigger drives
  (`ArenaBarrierManager.ResolveMetalGateIds`, reads `onTriggerEvents` MetalGate.Close/Open targets):
  - **grace** = `_gracedGates` (arenaKey→gate ids): a `MetalGate.Close` prefix blocks that gate's close during the
    ~5 s grace, so the trigger's bundled close is deferred to the host's t0+5 s CloseDoor (matches "don't close
    immediately, close at the 5 s node").
  - **held** = `_heldGates` (arenaKey→gate ids): at CloseDoor the gate is closed for real + registered held; a
    `MetalGate.Open` prefix blocks that gate's reopen until release, EXCEPT host-vetted mirror opens and the one
    legit "all enemies dead" reopen (`AllDeadTrigger.CheckAllDead` prefix opens a short `_legitGateOpenUntil`
    window). Release / scene-change `Clear()` drop the hold.
  - **Safe for Cousin** (trigger≈gate; all-dead release passes the window). Emperor has no AllDeadTrigger → gate
    stays closed until scene-change = desired. SetActive-door arenas (Lucia) NOT covered (MetalGate only).
  - **LD-2g — the SECOND legit reopen: boss-death scene events.** Some boss arenas have no AllDeadTrigger at all:
    the gate reopen is wired to the boss **Unit's serialized `onDeathEvents`** (Terrorbaum: both TreeGates'
    `MetalGate.Open` hang on the tree's death). With only the all-dead window, LD-2f blocked that native open on
    BOTH ends (the fight ended, doors stayed shut forever). `Unit.Die` invokes `onDeath` (→
    `BossFightHelper.OnBossDead`) one line BEFORE `onDeathEvents`, so a postfix on the base `OnBossDead` opens the
    same `_legitGateOpenUntil` window just in time — on whichever end runs `Die` (host real death, client mirrored
    death). The host's now-passing open then drives the normal `OnGateOpened` → Release chain (held gates cleared
    everywhere, out-of-room players teleported in). A client that never subscribed `OnBossDead` (joined mid-fight,
    no TriggerFight) still opens via the host-vetted GateSync mirror (`IsApplyingMirror` bypasses the hold).
- Timing note: `SealDelaySeconds`/`TeleportDelaySeconds` are already centralised as consts in
  `ArenaLockdownManager` — the "change 5s/10s in one place" requirement is satisfied; keep any new door timing
  there too.

**Dialog (delete is NOT available on the Emperor — §4b — so CLOSE + gate is the model).** Reproduce Cousin's
combat/dialog behaviour, driven by room-membership:
1. **Before anyone triggers:** the native inside position-trigger opens the first dialog (vanilla).
2. **Before fight start, any player triggers → everyone *in the room* gets the dialog too** (catch-up for in-room
   players; opt-outs / out-of-room players are not dragged in). = Cousin RM-2b catch-up. **Reuse the existing
   in-room player list** — for the Emperor that is the **arena-lockdown in-room set** (already fed for it; RM's
   `_roomMembers` is not, since the Emperor isn't a registered encounter). This in-room list logic is a **shared
   primitive most bosses need** (see §5).
3. **After fight start: nobody can open the dialog** (block it — since we can't null the scene-scripted speaker's
   `dialog`, block `Npc.Interact`/the trigger for the encounter instead).
4. **Phase 2 REVIVES the dialog** (the separate phase-2 DialogueTree must become reachable again — do NOT
   permanently kill dialog the way Cousin does). Ties into §4b per-client phase-2 handling.

### 4c. Why the desync risk is smaller than Cousin (but still worth fixing)
Cousin *had* to sync fight-start because its behaviour tree / intro animation events run locally (e.g.
`DoneAppearing` clearing invuln at the end of the intro animation → a client that never finishes it leaves the
boss half-dead). The Emperor worm's combat is **already host-authoritative** (head-stream + EMP-3d damage +
EMP-3b/3c destroy/death), so an unsynced start mostly desyncs **music, emergence/`Initialize` timing, and the
dialog→fight transition**, not the mechanic itself. Still worth syncing for a clean transition — and note the
Log251/252 "host can't advance dialog on re-entry" known issue, which host-authoritative dialog handling may also
improve.

---

## 5. Forward goal + the two per-boss requirements to always check

Every future boss (and elite room) should ship with the **FF14 arena lockdown** (RM + seal/popup/teleport/release
+ grace) and the **dialog join optimization** (all players can reach the dialog per phase; opt-outs respected;
fight starts host-authoritatively) by default — these are framework features, not per-boss work, once the boss is
a registered encounter.

**When syncing ANY new boss, always evaluate these two requirements (they recur and are easy to miss):**
1. **Dialog sync** — the pre-fight dialog behavioural model (from §4d, generalised): before anyone triggers, the
   native trigger opens the first dialog; before fight-start any player triggering gives every *in-room* player the
   dialog (catch-up, opt-outs respected); after fight-start nobody can open it; **later phases that have their own
   dialog must REVIVE it** (don't permanently kill dialog). Prefer the real "delete" (`Npc.dialog=null` via
   `TryRemoveDialogInteractable`) when the speaker Npc is statically reachable; fall back to CLOSE + block when the
   dialog is scene-scripted with no static speaker (Emperor).
2. **Gate lock** — does the arena seal via a vanilla trigger→`MetalGate.Close` / door `SetActive`? If the CLOSE is
   **bundled onto a dialog/other trigger**, it must be excised so the mod owns the door (close at the lockdown t+5
   node, keep it closed through teleport-in). Confirm the trigger wiring with a probe before excising.

Both requirements sit on a **shared substrate: the in-room player list** (who has crossed into the arena). Most
bosses need it — for dialog catch-up/scoping AND for the lockdown's seal/teleport targeting. Reuse one in-room
list, don't grow a second per boss (Emperor uses the arena-lockdown in-room set; registered encounters can use
RM `_roomMembers`; unify where practical).

Keep the boundaries clean and the knobs central: door/lockdown timing lives as consts in `ArenaLockdownManager`
(`SealDelaySeconds`/`TeleportDelaySeconds`/…) — one place to change 5s/10s.

---

## 6. Terrorbaum — the pure-puppet recipe + the "remote start" audit lesson

Terrorbaum (TB, BossAuthority.md §10) is the reference implementation of the **pure-puppet** model: the client
copy runs NO mechanics at all (`ClientBossIsPurePuppet` + `SuppressClientMechanics`, re-asserted per Tick); the
host owns everything; the client mirrors position (roster puppet), health/death (BossState), discrete visibility
states (`TerrorDig`/`TerrorErupt:*`/`TerrorEruptAoe`/`TerrorRoot` via msg 36), the opening dialogue (TB-D) and the
entrance animation (TB-INTRO via the GateSync channel, `NetGateState.Kind=1`). Damage rides the standard
authority with a per-boss window (`role="eye"` → host lifts the standing invulnerability around `ReceiveDamage`,
exactly like the native `OnEyeHit`).

### The lesson (why TB took five test rounds — read this before syncing the next boss)

TB's fixes were all small; what cost the time was discovering, one live log at a time, that the vanilla fight has
**implicit "the interacting player is standing right here" assumptions** baked into independent subsystems. A
remotely-started fight (client picks the fight option, host player elsewhere) breaks every one of them:

1. **Entrance animation** — played by the room-entry `PlayerTrigger`'s persistent `Animator.SetTrigger`,
   local-player-only → the far end's boss never visually appears (and replays the entrance when that player
   finally walks in, because its own `onlyOnce` was never consumed). → TB-INTRO trigger mirror.
2. **Target acquisition** — the erupt behaviour-tree node requires `aiAgent.lastTarget != null`, acquirable only
   by LOS from a boss that digs underground at fight start → without a nearby player it never surfaces.
   → TB-TGT host target upkeep (nearest attackable player unit, ghosts included).
3. **Damage window** — the body is permanently invulnerable; only the local `OnEyeHit` (eye hitbox + fightStarted
   + !IsTransitioning) lifts it around `ReceiveDamage` → a plain host-side reflect is always rejected, and the
   window gate itself broke a second way when the puppet freeze left `bossPhases.isTransitioning=true` forever.
   → TB-DMG eye-window routing + host window replication + flag cleanup on suppression.
4. **Dialog interactable** — vanilla never guards a post-start `Npc.Interact` (in single-player the tree has
   moved off into combat) → the opening dialogue re-opens mid-fight for a late arriver. → TB-DLG started-block.

**Rule going forward (add to the new-boss checklist, §3):** before writing any sync code, walk the decompiled
chain **from the start entrypoint to "a remote player can see it, be attacked by it, and damage it"** and list
every step that implicitly requires the local player to be present or local code to be running: PlayerTriggers
(entrance/animator), animation events, LOS/target acquisition, per-end dialog state, damage gates
(invulnerability windows, hitbox parts), and driver state machines. Fix them as ONE slice against that list —
five of TB's six defects were all visible in the decompile up front; they were found by five live-test rounds
instead because the audit stopped at the start-chain (the 5.4-E5 lesson applied only to damage, not to the whole
"remote start" surface).

Two implementation lessons that generalise:

- **Freezing a native driver must also settle the state it holds mid-flight.** Disabling `BossPhases` left
  `isTransitioning=true` permanently, silently closing the native damage gate. When suppressing any
  driver/behaviour, enumerate its state-machine fields and normalise them at the freeze point (same family as the
  F4-ADDS "a mirrored unit must replicate native state, not just disable AI").
- **Replaying a native method on another end replays OUR hooks on it too.** The TB-INTRO mirror invokes the real
  `PlayerTrigger.Trigger`, which fed our own membership postfix and marked the far-away player "in-room" (no
  pull-in popup + dialog wrongly mirrored, Log361). Before replaying any native method, grep the mod's own
  patches on it and guard the ones whose semantics are "a real local action happened here"
  (`IsApplyingTriggerMirror`-style reentry flags).

**Diagnosis lesson:** "invisible on one end" was chased through two wrong theories (underground position, render
culling) before a 5-second render/animator state probe (`[BossVis]`) settled it in one round — the boss was
rendering fine, stuck in its pre-entrance animator pose. For presentation-layer reports, ship a state probe FIRST
(renderers / animator / GO-active / rooms), then reason.
