using System;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay.Boss
{
    /// <summary>Context supplied by the manager when building an encounter id (current run identity).</summary>
    internal struct BossEncounterContext
    {
        public string RunKey;
        public string ChapterName;
        public int    LevelIndex;
        public bool   HasSeed;
        public int    Seed;
        public string GraphName;
    }

    /// <summary>
    /// Phase 5.4-E: per-Boss-family adapter. The generic manager owns networking / state / dedup / reentry /
    /// session validation; each adapter knows ONLY how to recognise, identify, read, and start its Boss family.
    /// This keeps Boss logic out of one giant switch and lets later families be added by dropping in an adapter.
    /// </summary>
    internal interface IBossEncounterAdapter
    {
        string AdapterName { get; }

        /// <summary>The controller Type this adapter handles (used for scene scans). May be null until resolved.</summary>
        Type? ResolveType();

        /// <summary>True if the component is a Boss controller this adapter is responsible for.</summary>
        bool CanHandle(object component);

        NetBossEncounterId BuildEncounterId(object component, in BossEncounterContext ctx);

        /// <summary>Has the fight already started locally? (best-effort; false when unknown).</summary>
        bool IsStarted(object component);

        /// <summary>
        /// Phase 5.4-E2: the ordered start-chain method names for this Boss family (entry first). A Boss start is
        /// rarely a single synchronous call — it is a chain (interact/intro -> coroutine -> fight-start) that the
        /// game drives across frames via animation/dialogue events. These names let the manager (a) recognise a
        /// later chain step as an authorized continuation rather than an unauthorized local start, and (b) pick the
        /// right method to invoke when applying a host start. Empty = diagnostic-only adapter (no auto-start).
        /// </summary>
        string[] StartChainMethods { get; }

        /// <summary>True if <paramref name="source"/> (a "Type.Method" or "ClientRequest:Type.Method" string)
        /// names one of this Boss's start-chain steps — i.e. a host-authorized continuation, not a fresh start.</summary>
        bool IsContinuationSource(string source);

        /// <summary>
        /// Phase 5.4-E2: apply a host-authoritative start to the local Boss, honouring the host's broadcast
        /// <see cref="NetBossEncounterState.StartSource"/> so the SAME entry method replays and the game's own
        /// chain (intro -> fight-start) unfolds naturally. Must be idempotent and never throw.
        /// </summary>
        bool TryApplyHostStart(object component, NetBossEncounterState state, out string detail);

        /// <summary>Best-effort read of extension state (phase index / position) for diagnostics + reserved sync.</summary>
        void TryReadState(object component, out bool hasPhase, out int phaseIndex, out bool hasPos, out Vector3 pos);

        string DescribeForLog(object component);

        // ---- Phase 5.4-E3: dialog-gated bosses (Cousin / Lucia) ----

        /// <summary>True if this Boss instance starts through a dialog/interact choice that must be committed
        /// host-authoritatively. Component-aware so one adapter can cover both a dialog-gated boss (DesertClause has
        /// OnStartInteractWithBoss) and a non-dialog one (Terrorbaum uses TriggerFight only).</summary>
        bool IsDialogBoss(object component);

        /// <summary>True if this source method represents the "fight committed" decision (e.g. Introduction/StartFight,
        /// Lucia.TriggerFight). Used to fire / recognise a BossDialogCommit.</summary>
        bool IsDialogCommitSource(string source);

        /// <summary>Phase PF (Plan B): true if the fight must wait for the intro DIALOG to be dismissed by an in-room
        /// player instead of auto-starting from the behavior tree (the single-player pause used to gate this; co-op
        /// disables pause). When true the manager blocks StartFight until a host-authoritative dialog-close commit.</summary>
        bool GatesFightOnDialogClose(object component);

        /// <summary>Phase PF-ArmDefer (issue 1): true if this boss spawns a single INTRO arm/add from its behavior tree
        /// DURING the intro dialog that, in single-player, is delayed until the dialog closes (the pause freezes the
        /// behavior tree's WaitForSeconds). Co-op disables that pause, so the intro arm pokes out during the dialog.
        /// When true the manager BLOCKS the behavior-tree intro arm and replays it on the dialog-close fight commit
        /// (vanilla timing). Cousin = its SpawnArm; default = no deferred intro arm.</summary>
        bool DefersIntroArmUntilCommit(object component);

        /// <summary>Phase PF-ArmDefer: invoke the boss's REAL intro arm spawn now (the manager calls this from the
        /// dialog-close fight commit, under the reentry guard so the SpawnArm gate lets it through). Idempotent-safe,
        /// never throws. Default = no-op (boss has no deferred intro arm).</summary>
        bool TryReplayIntroArm(object component, out string detail);

        /// <summary>Phase RM (room-membership): true if this start source marks the local player having crossed into the
        /// boss room (the room-entry trigger). Each end fires its own (PlayerTrigger.onlyOnce is per-end), so it
        /// captures every player who reaches the boss. Default = the boss's primary trigger; Cousin = "Trigger".</summary>
        bool IsRoomEntrySource(string source);

        /// <summary>True if, AFTER the fight is started, this source would re-open/re-enter the dialog and must be
        /// suppressed (e.g. Cousin.Introduction/Trigger after FightStarted).</summary>
        bool ShouldSuppressDuplicateDialogEntry(object component, string source);

        /// <summary>Apply a host-authoritative dialog commit: finalize any local dialog via the real dialog API and
        /// ensure the fight is started exactly once. Idempotent, never throws.</summary>
        bool TryApplyDialogCommit(object component, NetBossDialogCommit commit, out string detail);

        /// <summary>Fix A (root): remove this boss's dialog interactable so it can never re-open the boss dialog after
        /// the fight has started (the same thing vanilla WitchBossController.FightStartRoutine does). Runs on every
        /// end at fight-start, regardless of who triggered. Idempotent, never throws.</summary>
        bool TryRemoveDialogInteractable(object component, out string detail);

        // ---- Phase 5.4-E4.2: boss health sync ----

        /// <summary>The single damageable Boss Unit whose health is authoritative (witchMainUnit / bossUnit / owner).
        /// Null if this boss has no single health unit (Emperor multi-section). Drives the boss health bar + death.</summary>
        object? GetHealthUnit(object component);

        // ---- LD-Sandstorm (Desert): gate-less arena keep-in ----

        /// <summary>True if this boss fights inside a GATE-LESS "sandstorm" arena — the battlefield is ringed by a
        /// damage zone (not a door), so out-of-room players must be pulled in rather than sealed. Returns the arena's
        /// live danger-zone SPHERE: <paramref name="center"/> = the sphere's world position, <paramref name="radius"/> =
        /// its world radius. This is the game's own in/out test (decompiled DesertClausePerimeter: a unit is outside iff
        /// <c>Distance(unit, sphereCollider.transform.position) &gt; SphereRadius</c>, where
        /// <c>SphereRadius = sphereCollider.radius * |lossyScale.x|</c>). The sphere MOVES and can be resized during the
        /// fight, so both are read live. The manager starts an <see cref="Gameplay.ArenaLockdownManager"/> sandstorm
        /// lockdown at fight-start that pulls in stragglers a few seconds later. Default = false. Only DesertClause.</summary>
        bool TryGetSandstormArenaSphere(object component, out Vector3 center, out float radius);

        /// <summary>LD-Sandstorm / F4: true if this boss assembles its visible body through a LOCAL intro presentation
        /// (an animation chain that must run client-side to become visible) rather than appearing fully-formed. Such a
        /// boss is kept OUT of the generic enemy-puppet system while its intro plays, so the intro runs locally instead
        /// of the puppet snapping its transform + mirroring the host animator (which stalls the intro → invisible boss).
        /// Default = false. Only DesertClause (composite: sandSantaAnimationSprite → intro anim → TriggerFight).</summary>
        bool RunsLocalIntroPresentation(object component);

        /// <summary>Attach the boss health bar to this boss's health unit. Called ONCE per encounter by the manager
        /// (the native Attach re-subscribes each call). Returns false if there is no health unit.</summary>
        bool TryAttachBossBar(object component);

        // ---- Phase 5.4-F BossDamageAuthority: target-role mapping ----

        /// <summary>On the CLIENT: if <paramref name="hitUnit"/> (the local Unit the player just hit) belongs to this
        /// boss, return its target role ("main" for the boss body; later "eye"/"arm"/"illusion"). Null otherwise.</summary>
        string? ResolveHitTargetRole(object component, object hitUnit);

        /// <summary>On the HOST: resolve a target role to the real Unit to damage via ReceiveDamage (role "main" →
        /// the boss's health unit). Null if the role is unknown / not yet supported.</summary>
        object? ResolveHostTargetForRole(object component, string role);

        // ---- Phase 5.4-F2 BossStartPresentation: make the local boss ENTER combat presentation on the Client ----

        /// <summary>Called on the CLIENT after a host-driven start/commit is applied. Default no-op. Bosses whose
        /// start methods set flags but don't by themselves drive the local AI/animation (Cousin stands still)
        /// override this to activate the real presentation (behaviour tree / movement). Diagnostic + minimal.</summary>
        void OnClientPresentationStart(object component);

        // ---- Phase 5.4-F4 fixed-point boss discrete-event authority (Cousin pools) ----

        /// <summary>Method names of host-authoritative discrete mechanic events to hook (Cousin: Submerge /
        /// MoveToNewPool / Reappear). Empty = this boss has no fixed-point discrete events. The Host broadcasts when
        /// they fire; the Client mirrors via TryApplyDiscreteEvent. Keeps the random pool choice host-authoritative.</summary>
        string[] DiscreteEventMethods { get; }

        /// <summary>HOST: read the world data for a fired discrete event (e.g. the chosen pool's appear position).
        /// Returns false if the event carries no position. <paramref name="diag"/> is a per-event log line.</summary>
        bool BuildDiscreteEvent(object component, string eventName, out bool hasPos, out Vector3 pos, out string diag);

        /// <summary>CLIENT: mirror a host discrete event (teleport to the host's pool, play dig/emerge presentation).
        /// Idempotent, never throws.</summary>
        bool TryApplyDiscreteEvent(object component, string eventName, bool hasPos, Vector3 pos, out string detail);

        /// <summary>True if this discrete event is the boss's terminal death/encounter-end (Cousin: "CousinDeath").
        /// The manager marks the encounter terminal: the Client runs the real local death and stops sending hits.</summary>
        bool IsTerminalEvent(string eventName);

        // ---- Phase 5.4-F5: Lucia eye defeat authority (count/cycle, not per-eye identity) ----

        /// <summary>True if this boss has the Lucia-style "eye phase": the body is locked invulnerable while a set of
        /// spawned eyes is alive, and only clears when all eyes die (EyeDied → RestartPhases). Only Lucia this phase.</summary>
        bool IsEyeBoss { get; }

        /// <summary>Read the eye-phase snapshot. Returns true if the boss is CURRENTLY in its eye phase (Lucia
        /// currentPhase==5). <paramref name="cycle"/> is the Host-stable eye wave (restartCounter); <paramref name="livingEyes"/>
        /// is the live spawnedEyes count. Both ends read this from their own component.</summary>
        bool TryReadEyePhase(object component, out int cycle, out int livingEyes);

        /// <summary>HOST: consume ONE still-living eye through the real death path (<c>owner.Die()</c> → <c>EyeDied</c>),
        /// so the vanilla cycle (spawnedEyes shrink → RestartPhases on the last) runs natively. The manager wraps this
        /// in the reentry guard. <paramref name="remaining"/> is spawnedEyes.Count AFTER. Idempotent-safe, never throws.</summary>
        bool TryConsumeOneEye(object component, out int remaining, out string detail);

        /// <summary>CLIENT: remove the just-died local eye from the local spawnedEyes list WITHOUT triggering the local
        /// RestartPhases (the Client must not decide the unlock). Returns true if an entry was removed.</summary>
        bool TryRemoveDeadEyeFromList(object component, object eyeUnit);

        /// <summary>CLIENT: mirror the Host's "all eyes gone" cycle-complete by running the boss's REAL eye-phase
        /// recovery (Lucia: <c>RestartPhases()</c> → RestartRoutine → return-to-center, candle burn, restartCounter++,
        /// StartPhase(1)). This is what stops the Phase-5 fly-around and resumes normal phases — a host-authorized
        /// invocation, NOT the Client deciding the unlock. <paramref name="cleared"/> = residual local eyes removed.
        /// Idempotent, never throws.</summary>
        bool TryApplyEyePhaseComplete(object component, out int cleared, out string detail);

        /// <summary>Read a Lucia eye-phase completion diagnostic snapshot (currentPhase / restartCounter / body
        /// invulnerable / position). Used to log before→after across the host-authorized RestartPhases. Returns false
        /// if this adapter has no such state.</summary>
        bool TryReadEyeCompletionDiag(object component, out int phase, out int restartCounter, out bool invulnerable, out Vector3 pos);

        // ---- Phase 5.4-F6: Lucia terminal death authority ----

        /// <summary>CLIENT: run a SAFE local Lucia death — real Unit death (dead state / death animation / despawn) +
        /// the boss-end presentation (bar detach, "Dead" anim, stop phase routines, music stop), but WITHOUT the
        /// host-only world results (loot placement, checkpoint/save). Those are isolated by the OnBossDead prefix.
        /// Idempotent, never throws.</summary>
        bool TryApplyLuciaDeath(object component, out string detail);

        // ---- Phase 5.4-E3: phase/state broadcast (Witch and other phase bosses) ----

        /// <summary>True if this adapter can produce/consume a host-authoritative BossState (phase/health).</summary>
        bool ProvidesPhaseState { get; }

        /// <summary>Fill the host-authoritative phase/state snapshot from the live component (best-effort).</summary>
        void FillBossState(object component, NetBossState state);

        /// <summary>Apply a host phase/state snapshot to the local Boss (best-effort, idempotent, never throws).</summary>
        bool TryApplyBossState(object component, NetBossState state, out string detail);
    }
}
