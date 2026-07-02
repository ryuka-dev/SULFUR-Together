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
  (Emperor physics worm), **static/roster-puppet** (most enemies), or **phase-controller** (Witch).

---

## 3. Default recipe for a NEW boss

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

### 4a. Fight-start IS dialog-gated (corrects EmperorBossAudit §1)
Decompile fact: `EmperorBossFightHelper.OnPlayerSpawned → StartPhase1()` **only toggles the spider parent off** —
it does **not** call `StartMovement()`. `StartMovement()` is `public` and is invoked by an **external scripted
trigger**, almost certainly the pre-fight NPC dialog ("最神圣的皇帝陛下", seen in Log216 `[DialogFlow]`) on
completion. So the Emperor's fight start is dialog/trigger-gated, same shape as Cousin/Lucia — **audit §1's
"StartMovement fires from OnPlayerSpawned" is wrong** and is corrected here.

→ **To reuse dialog sync:** register the Emperor as an encounter, then gate **`StartMovement`** as its
"StartFight equivalent" (client prefix-blocks local `StartMovement`; host-authoritative fight-commit fires it on
all ends together). Map the dialog actor "最神圣的皇帝陛下" to the Emperor encounter so dialog-commit knows the
dialog belongs to it (it may be a separate speaker NPC, not the worm — confirm).

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

### 4c. Why the desync risk is smaller than Cousin (but still worth fixing)
Cousin *had* to sync fight-start because its behaviour tree / intro animation events run locally (e.g.
`DoneAppearing` clearing invuln at the end of the intro animation → a client that never finishes it leaves the
boss half-dead). The Emperor worm's combat is **already host-authoritative** (head-stream + EMP-3d damage +
EMP-3b/3c destroy/death), so an unsynced start mostly desyncs **music, emergence/`Initialize` timing, and the
dialog→fight transition**, not the mechanic itself. Still worth syncing for a clean transition — and note the
Log251/252 "host can't advance dialog on re-entry" known issue, which host-authoritative dialog handling may also
improve.

---

## 5. Forward goal
Every future boss (and elite room) should ship with the **FF14 arena lockdown** (RM + seal/popup/teleport/release
+ grace) and the **dialog join optimization** (all players can reach the dialog per phase; opt-outs respected;
fight starts host-authoritatively) by default — these are framework features, not per-boss work, once the boss is
a registered encounter.
