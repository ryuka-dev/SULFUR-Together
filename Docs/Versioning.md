# Versioning & Phase Labels — the governing convention

This repo uses **two** numbering systems. They are not the same thing; mixing them up is what makes labels
like "RM-2a" look opaque. This document is the single source of truth for both.

---

## 1. Plugin version (semver) — the release number

- Defined in **`src/ModInfo.cs`** (`ModInfo.Version`) and **`Thunderstore/manifest.json`**. Current: **`0.2.0`**.
- This is the only number a *player* ever sees (Thunderstore, BepInEx load line).
- Bumped **only at a Thunderstore release milestone**, MAJOR.MINOR.PATCH:
  - **PATCH** (0.2.0 → 0.2.1): bug-fix-only release, no new networked feature, wire-compatible.
  - **MINOR** (0.2.0 → 0.3.0): new synced feature(s) shipped; may change the net protocol (see §3).
  - **MAJOR**: reserved for a broad rewrite / breaking redesign.
- Bumping it is a deliberate release act (commit subject `Bump to X.Y.Z`), **not** something every dev change does.

## 2. Phase labels — the internal dev increments

Day-to-day work is tracked as **phases**, not version bumps. A phase is one coherent slice of work (design →
implement → verify). Phases are what commit messages, memory notes, and `[LogTag]` lines refer to.

### Label grammar

```
Phase <major>.<minor>-<AREA><iteration><revision>      e.g.  Phase 5.4-E2 , Phase 5.4-G7b
<AREA>-<iteration><revision>                            e.g.  RM-2a , PF-0      (named areas, not under a 5.x number)
```

- **`<major>.<minor>`** — the broad track (mirrors the era of work, loosely aligned with the eventual semver
  minor). E.g. `5.4` = the boss-authority track.
- **`<AREA>`** — a short code for the sub-system. A letter when it lives under a numbered track
  (`5.4-E` = boss **E**ncounter start, `5.4-F` = boss damage, `5.4-G` = **W**itch phases), or a **named code**
  when it's a standalone area:
  - **PF** = boss **P**re-**F**ight flow (room seal / dialog / teleport / convergence)
  - **RM** = **R**oom **M**embership substrate (who is in the boss room)
- **`<iteration>`** — a number for the n-th distinct step of that area (`RM-1`, `RM-2`). Bigger = later.
- **`<revision>`** — a trailing lowercase letter for a fix/revision of that same step (`RM-2a`, `5.4-G7b`).
  `RM-2a` = Room-Membership area, step 2, revision a.

### Lifecycle status (used in memory + the registry below)

`designed` → `implemented` → `deployed` (built + copied to both profiles) → `verified` (passed a real co-op
test, with the Log number) → `committed <hash>`. Behaviour changes are **only committed after `verified`**.

## 3. Net protocol version

Each networked message carries its own `*Version` byte in its codec (see `NetBossEncounterMessages.cs`,
`NetMessageType.cs`). When a wire format changes, bump that message's version byte — independent of the plugin
semver. A plugin MINOR bump is the moment to assume clients/hosts on older protocol bytes are incompatible.

---

## 4. Active phase registry

The current line of work. Older phases (Phase 1.x–4.x) live in `DevelopmentPlan.md`; the full boss line
(5.3–5.7) is indexed in the agent memory `MEMORY.md`. Keep this table updated as the active work moves.

| Label | What it is | Status |
|-------|------------|--------|
| **PF-0** | Read-only pre-fight convergence + arena-door probes (`[BossPreFight]`/`[ArenaDoor]`) | committed |
| **PF Plan B** | Gate the Cousin fight start on the intro **dialog being dismissed** (host-authoritative); restores the SP pause-gate that co-op's no-pause mode removed | verified Log133 · committed `d3130d1` |
| **PF Plan B (late-client fix)** | Gate StartFight on the boss's real `FightStarted`, not our `_fightCommitted` (which a level-step Reset can clear), so a late intro's Cinematic lock still gets cleared — no freeze | verified Log135 · committed `1cc779c` |
| **RM-1** | Room-membership substrate: host-authoritative "who is in the boss room" set, fed by each end crossing the boss trigger (msgs 51/52, `[RoomMembership]`). **Observe-only**, changes no behaviour | verified Log134/135 · committed `1cc779c` |
| **RM-2a** | Fix the membership/fight-committed maps being wiped mid-encounter by a per-GoToLevel `Reset()`: `Reset(fullSession:false)` preserves the per-`chapter:level:seed` state; only a session reset or a genuine level change clears it | verified Log137 (no member drop; churn not re-triggered) · committed `d5c748a` |
| **RM-2b** | First consumer — scope the synced cutscene to in-room players (fix "not-in-room player pulled into the cutscene") | **shelved** (needs headless host start; edge case hasn't bitten) |
| **RT3-Cousin-arms** | Route `GoblinCousinArm` through the RT3-A boss-add pipeline (was wrongly in `_specialAdds`) so the client mirrors one host-authoritative puppet arm instead of running its own — fixes double-spawn, double damage, desync. Client mud-ball **visual** = the arm's own `CousinArm.ThrowProjectile` de-fanged + target-fixed via `CousinArmPatches` (anim-event aligned). The throw is also turned into an **all-players AoE** (one physical ball per `GameManager.Players` entry: host real damage, client de-fanged); vanilla lob arc kept (each ball lands on its own target). Config `EnableCousinArmSync`. | verified Log138 (sync) / Log144 (visual aligned) / Log145 (AoE) · committed `541cea4` |
| **F3-Reload** | Fix the client F3-ing to the level both ends are already in (reload-in-place). The client's load gate self-released on the host's STALE scene request and reloaded locally → divergent fresh instance (Log147: laggy, link breaks, save persists the split; host never even got a relay). Client (`NetClientLoadGate`): a client-led transition whose target == the client's current scene (`_clientLedReloadInPlace`) only releases on a host request received after `_clientLedEpoch` → it relays + waits instead of self-reloading. Host (`HandleClientTransitionRequest`): a same-scene relay (only ever an explicit reload — a catch-up follows the existing broadcast and never relays) now RE-LEADS the reload (`TryFollow`, new seed) instead of `already-at-target→ignore`, so both regenerate together (resets the in-progress fight, by design). Config `EnableClientReloadInPlaceRelay` (default on, needs `EnableClientTransitionRelay`). The reload-in-place release keys on the host request's **seed differing** from the client's current seed (proof the host actually re-generated) — a timestamp-only check released the client on the host's same-seed gen-input echo, leaving it one seed behind + a rapid double-reload NRE (Log149). | verified Log150 (client ignores same-seed echo → releases on host's new-seed reload → both converge each F3, 0 Error/NRE) · committed |
| **Ghost-load-freeze fix** | `RemotePlayerRegistryManager.Tick` suppresses headless ghost Players while the host `GameState` is Loading/Uninitialized — a camera-less ghost re-registered mid-load made vanilla `LevelGeneration.ShowLevelNode` NRE on `Players[i].weaponCamera`, hanging the loading screen at 17/17 (Log139/140). Config `SuppressGhostsWhileLoading`. | verified Log141–145 (no recurrence) · committed `46fdce5` |
| **PF-ArmDefer** (RT3-Cousin-arms issue 1) | Defer the Cousin intro arm to the dialog-close fight commit (vanilla timing) instead of letting co-op's no-pause behavior tree pop it *during* the dialog. Prefix-block `CousinHelper.SpawnArm` while the fight loop hasn't begun (`OnLocalIntroArmSpawn` / `_cousinFightLoopStarted`); replay it once at `CommitFightStartLocal` under the reentry guard (`TryReplayIntroArm` / `_introArmReplayed`). Host's replay flows through RT3-A + broadcasts, client's binds to it (one puppet, no double-spawn); the `fight-loop-started` gate (not `FightStarted`) blocks a late-client's trailing duplicate. Config `DeferBossIntroArm` (default on, needs `GateBossFightOnDialogClose`). Log tag `[BossArmDefer]`. | verified Log147/148 (both ends: `blocked → replayed ok=True` → intro arm BOUND seq=0, Reappear arms unaffected, 0 Error/NRE) · committed |
| **RT3-Cousin-arms client-visual-all-balls** | Client now also spawns a visual-only mud ball toward every remote player's proxy position (`NetService.ForEachRemotePlayerPosition` → `CousinArmPatches.ClientVisualThrowAt`, a re-impl of the arm's ballistic throw with `explicitDamage=0`), so a client sees the boss attacking everyone, not just itself. Damage stays host-authoritative. | verified Log146 (remoteVisual=1, 0 errors) · committed `04fd831` |
| **LD-1 / LD-1b** | Combat-room **gate** sync — peer-authoritative effect mirror of `MetalGate.Close/Open` (LD-1, msg 53) and the `GameObject.SetActive("door")` variant via PlayerTrigger (LD-1b, msg 54), keyed by world pos. `[GateSync]`/`[DoorSync]`. | verified Log153–156 · committed `181bfc7` |
| **LD-2a** | Arena lockdown **membership + timer** (host-authoritative): who crossed each seal trigger (in-room), first cross = t0, t+5/t+10 timeline. Observe-only (decision-logging). msg 55, `[ArenaLockdown]`. | verified Log157 · committed `b6b2c52` |
| **LD-2b / LD-2c** | Force-seal **barrier** (invisible two-way `BoxCollider` at the out-of-room door) + **popup-confirm/boss-death teleport** in. Host `NetArenaCommand` (msg 56: Seal/Popup/Release/Notify), confirm key + Native UI Lib banner/toasts (soft dep, reflection). | verified Log158 (confirm path) · committed `0dbfaa5`/`336cd71`/`5213ba9` |
| **LD-2d** | **Grace period**: hold the MetalGate open ~5 s (prefix-block `Close` while a local grace window is active) so teammates can still walk in together; host `CloseDoor` (msg 56 kind 5) closes every end's gate at t0+5 s, then the barrier seals whoever's still out. Config `EnableArenaGracePeriod`. SetActive-door (Lucia) still closes at t0. | built · pending in-game | 

## 5. Rule going forward

- Every behaviour-changing commit names its phase label in the subject, and that label appears in this registry
  (or in `DevelopmentPlan.md` / memory for older tracks) with a one-line meaning + status.
- When unsure what a label means, this file (§4) and the agent memory index are the lookup.
