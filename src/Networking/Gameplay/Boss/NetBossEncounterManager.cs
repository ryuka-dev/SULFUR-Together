using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay.Boss
{
    /// <summary>
    /// Phase 5.4-E: generic Boss Encounter Authority. Owns adapter dispatch, encounter identity/registry,
    /// dedup, the reentry guard, session/run validation and the network start handshake. Per-Boss knowledge
    /// lives in the adapters; this class is Boss-agnostic so new bosses are added by registering an adapter.
    ///
    /// This phase implements only the START handshake (host-authoritative). Health / phase / death are reserved
    /// (state already carries position + phase-index fields). Nothing here freezes gameplay or touches loot.
    /// </summary>
    internal static class NetBossEncounterManager
    {
        private static readonly object _lock = new object();

        // Adapter order matters: specific standalone systems BEFORE the generic BossFightHelper base.
        private static readonly IBossEncounterAdapter[] _adapters =
        {
            new WitchBossControllerAdapter(),
            new CousinHelperAdapter(),
            new LuciaBossFightHelperAdapter(), // BEFORE generic: Lucia derives from BossFightHelper
            new EmperorBossAdapter(),
            new BossFightHelperAdapter(),
        };

        private sealed class Entry { public IBossEncounterAdapter Adapter = null!; public object Component = null!; }
        private static readonly Dictionary<string, Entry> _registry = new Dictionary<string, Entry>();

        private static readonly HashSet<string> _hostBroadcast = new HashSet<string>();
        private static readonly HashSet<string> _clientRequested = new HashSet<string>();
        private static readonly HashSet<string> _appliedStart = new HashSet<string>();
        private static string _runScope = "";
        private static int _reentryDepth;

        // ---- Phase 5.4-E2: host-authorized continuation windows (per encounter key) ----
        // After a Client applies a host start, the game drives the REST of the start chain (intro coroutine ->
        // fight-start; player-paced dialogue for Cousin) across later frames. Those later entrypoints must NOT be
        // treated as unauthorized local starts. A window stays open from apply until the fight is observed started
        // (+ a short grace) or the run changes — deliberately NOT a fixed timeout, because Cousin's dialogue-driven
        // StartFight can arrive many seconds later.
        private sealed class ContinuationWindow
        {
            public IBossEncounterAdapter Adapter = null!;
            public object Component = null!;
            public string OpenedBy = "";
            public float OpenedAt;
            public float LastActivityAt;
            public bool StartObserved;
            public float StartObservedAt;
        }
        private static readonly Dictionary<string, ContinuationWindow> _continuation = new Dictionary<string, ContinuationWindow>();

        // ---- Phase 5.4-E2: deferred post-apply verification (the start chain is async; check a beat later) ----
        private sealed class PendingVerify { public string Key = ""; public string Source = ""; public string Invoked = ""; public string Before = ""; public float DueAt; }
        private static readonly List<PendingVerify> _pendingVerify = new List<PendingVerify>();

        // ---- Phase 5.4-E4: deferred dialog finalize (the local boss dialog may open just AFTER the commit) ----
        private sealed class PendingDialogFinalize { public string Key = ""; public float Until; }
        private static readonly List<PendingDialogFinalize> _pendingDialogFinalize = new List<PendingDialogFinalize>();
        private const float DialogFinalizeWindowSeconds = 6f;
        // Phase 5.4-E4.2: how often the host pushes boss health/state (boss bar smoothness vs bandwidth).
        private const float BossStateBroadcastInterval = 0.4f;

        // counters
        public static int BossEncounterDiscovered;
        public static int BossStartBroadcast;
        public static int BossStartRequestSent;
        public static int BossStartRequestReceived;
        public static int BossStartApplied;
        public static int BossStartApplyFailed;
        public static int BossStartRejectedSession;
        public static int BossEncounterNotFound;
        public static int BossLocalStartBlocked;
        // E2 counters
        public static int BossContinuationAllowed;
        public static int BossContinuationBlocked;
        public static int BossApplyIncomplete;
        public static int BossDuplicateSuppressed;
        // E3 counters
        public static int BossDialogCommitRequested;
        public static int BossDialogCommitBroadcast;
        public static int BossDialogCommitApplied;
        public static int BossDialogEntrySuppressed;
        public static int BossStateBroadcast;
        public static int BossStateApplied;
        // F counters (BossDamageAuthority)
        public static int BossHitClientSent;
        public static int BossHitHostRecv;
        public static int BossHitHostApplied;
        public static int BossHitHostRejected;
        public static int BossHitVisualSent;
        public static int BossHitVisualPlayed;
        private static int _bossHitSeq;
        private static int _bossHitVisualSeq;
        // F5 counters (Lucia eye defeat authority)
        public static int LuciaEyeReportSent;
        public static int LuciaEyeReportRecv;
        public static int LuciaEyeAccepted;
        public static int LuciaEyeRejected;
        public static int LuciaEyeStateBroadcast;
        public static int LuciaEyeStateApplied;
        public static int LuciaEyeResidualCleared;
        private static int _luciaEyeReportSeq;
        private static int _luciaEyeStateRevision;
        // host dedup: "peerId:seq" reports already consumed. client dedup: "key:cycle" cycle-complete already applied.
        private static readonly HashSet<string> _luciaEyeConsumed = new HashSet<string>();
        private static readonly HashSet<string> _luciaEyeCycleComplete = new HashSet<string>();
        // F6: deferred client after-snapshot for the eye-phase RestartPhases (the recovery is a ~9s coroutine).
        private sealed class PendingEyeComplete
        {
            public string Key = ""; public int Cycle;
            public int PhaseBefore; public int RestartBefore; public bool InvulnBefore; public Vector3 PosBefore;
            public float DueAt;
        }
        private static readonly List<PendingEyeComplete> _pendingEyeComplete = new List<PendingEyeComplete>();
        // F2: throttle the host->client hit-visual broadcast (hits arrive fast).
        private static readonly Dictionary<string, float> _lastHitVisualAt = new Dictionary<string, float>();
        private const float HitVisualMinInterval = 0.1f;

        // E3 per-key dedup sets for dialog commit (cleared on level change).
        private static readonly HashSet<string> _dialogCommitBroadcast = new HashSet<string>();
        private static readonly HashSet<string> _dialogCommitApplied = new HashSet<string>();
        // Fix A (root): per-encounter once-guard so the boss dialog interactable is removed exactly once per fight.
        private static readonly HashSet<string> _dialogInteractableRemoved = new HashSet<string>();
        // Phase PF (Plan B): dialog-close-gated fight start. The intro+dialog play (behavior tree), but StartFight is
        // blocked until an in-room player dismisses the dialog; that close is a host-authoritative FIGHT commit.
        private static string? _dialogOpenKey;                                                 // gated boss whose intro dialog is open on THIS end
        private static readonly HashSet<string> _fightCommitted = new HashSet<string>();        // fight start applied locally (per key)
        private static readonly HashSet<string> _fightCommitRequested = new HashSet<string>();  // client sent a fight-commit request (per key)
        private static readonly HashSet<string> _fightCommitBroadcast = new HashSet<string>();  // host broadcast a fight-commit (per key)
        // Phase RM (room-membership substrate): host-authoritative "who is in the boss room" set. Observe-only for now.
        private const string HostPlayerId = "host";
        private static readonly Dictionary<string, HashSet<string>> _roomMembers = new Dictionary<string, HashSet<string>>();        // host-authoritative: key -> player ids
        private static readonly Dictionary<string, HashSet<string>> _roomMembersClientView = new Dictionary<string, HashSet<string>>(); // client cache of last host broadcast
        private static readonly HashSet<string> _roomEnterReported = new HashSet<string>();                                          // per-end once-per-key (room entry reported)
        // Phase RM-2b: scope the synced intro cutscene to IN-ROOM players. A "dialog session" is active per encounter from
        // the intro trigger until the fight commits; only ends whose local player is in-room play the cutscene, and a late
        // entrant (walked in OR teleported in by the lockdown) catches it up while the session is still active.
        private static readonly HashSet<string> _dialogSessionActive = new HashSet<string>();                                       // per-key: intro session open (until fight commit)
        private static readonly HashSet<string> _cutscenePlayed = new HashSet<string>();                                            // per-key: THIS end already played the intro cutscene
        private static readonly HashSet<string> _localTeleportedIn = new HashSet<string>();                                         // per-key: lockdown teleported the local player in (counts as in-room)
        private static readonly Dictionary<string, NetBossDialogCommit> _lastIntroCommit = new Dictionary<string, NetBossDialogCommit>(); // per-key: last intro commit (to replay on catch-up)
        // Per-key: the boss intro already RAN on this end while OUT of the room (effects suppressed, dialog blocked) → the
        // boss has appeared (introPlayed=true) so a catch-up can't re-TriggerIntro; it must open the dialog DIRECTLY.
        private static readonly HashSet<string> _introRanWhileOutOfRoom = new HashSet<string>();
        private static int _bossStateRevision;
        // E4.2: client-side per-key "boss bar already attached" guard.
        private static readonly HashSet<string> _bossUiAttached = new HashSet<string>();

        private static bool Enabled
        {
            get { try { return Plugin.Cfg.EnableBossEncounterSync.Value; } catch { return false; } }
        }
        private static bool LogOn
        {
            get { try { return Plugin.Cfg.LogBossEncounter.Value; } catch { return false; } }
        }
        private static float ContinuationGraceSeconds
        {
            get { try { return Mathf.Max(0f, Plugin.Cfg.BossContinuationGraceSeconds.Value); } catch { return 5f; } }
        }
        private static bool GateFightActive
        {
            get { try { return Plugin.Cfg.GateBossFightOnDialogClose.Value; } catch { return false; } }
        }

        private static bool DeferIntroArmActive
        {
            get { try { return Plugin.Cfg.DeferBossIntroArm.Value; } catch { return false; } }
        }
        private static bool RoomMembershipActive
        {
            get { try { return Plugin.Cfg.EnableBossRoomMembership.Value; } catch { return false; } }
        }

        /// <summary>Reset boss-encounter state. <paramref name="fullSession"/>=true (connect/disconnect) clears
        /// everything; false (a per-GoToLevel reset) preserves the per-encounter state keyed by chapter:level:seed
        /// (room membership + fight-committed), which must survive a same-level GoToLevel churn — Log134 showed a
        /// spurious mid-encounter Reset dropping room membership / fight-committed. A genuine level change still clears
        /// them via <see cref="OnLevelChanged"/> (runScope comparison).</summary>
        public static void Reset(bool fullSession = true)
        {
            lock (_lock)
            {
                _registry.Clear();
                _hostBroadcast.Clear();
                _clientRequested.Clear();
                _appliedStart.Clear();
                _continuation.Clear();
                _pendingVerify.Clear();
                _pendingDialogFinalize.Clear();
                _dialogCommitBroadcast.Clear();
                _dialogCommitApplied.Clear();
                _dialogInteractableRemoved.Clear();
                _dialogOpenKey = null;
                if (fullSession)
                {
                    _fightCommitted.Clear();
                    _fightCommitRequested.Clear();
                    _fightCommitBroadcast.Clear();
                    // RM-2b dialog-session state is per-encounter (chapter:level:seed) like the fight-commit maps — it MUST
                    // survive same-level state churn (Reset(fullSession:false)), else a mid-encounter Reset wipes the active
                    // dialog session and a late entrant's catch-up finds nothing. Only a real level change clears it.
                    _dialogSessionActive.Clear();
                    _cutscenePlayed.Clear();
                    _localTeleportedIn.Clear();
                    _lastIntroCommit.Clear();
                    _introRanWhileOutOfRoom.Clear();
                    _roomMembers.Clear();
                    _roomMembersClientView.Clear();
                    _roomEnterReported.Clear();
                }
                _bossUiAttached.Clear();
                _lastHitVisualAt.Clear();
                _terminalDead.Clear();
                _cousinFightLoopStarted.Clear();
                _introArmReplayed.Clear();
                _preFightLogged.Clear();
                _luciaEyeConsumed.Clear();
                _luciaEyeCycleComplete.Clear();
                _pendingEyeComplete.Clear();
                _witchAppliedRevision.Clear();
                _witchPhaseRevision = 0;
                _lastHostWitchPhase = -1;
                _pendingP2Manifest = null;
                _witchP2Cycle = 0;
                _witchP2Active = false;
                _witchP2RealHitConsumed = false;
                _witchP2DefeatedDomes.Clear();
                _p2ClientAppliedCycle = -1;
                WitchBossControllerAdapter.ClearPhase2State();
                if (fullSession) _runScope = "";
                _reentryDepth = 0;
            }
        }

        /// <summary>Called on level change so per-run dedup/registry does not leak across scenes.</summary>
        public static void OnLevelChanged(string runScope)
        {
            lock (_lock)
            {
                if (_runScope == runScope) return;
                _runScope = runScope;
                _registry.Clear();
                _hostBroadcast.Clear();
                _clientRequested.Clear();
                _appliedStart.Clear();
                _continuation.Clear();
                _pendingVerify.Clear();
                _pendingDialogFinalize.Clear();
                _dialogCommitBroadcast.Clear();
                _dialogCommitApplied.Clear();
                _dialogInteractableRemoved.Clear();
                _dialogSessionActive.Clear();
                _cutscenePlayed.Clear();
                _localTeleportedIn.Clear();
                _lastIntroCommit.Clear();
                _introRanWhileOutOfRoom.Clear();
                _dialogOpenKey = null;
                _fightCommitted.Clear();
                _fightCommitRequested.Clear();
                _fightCommitBroadcast.Clear();
                _roomMembers.Clear();
                _roomMembersClientView.Clear();
                _roomEnterReported.Clear();
                _bossUiAttached.Clear();
                _lastHitVisualAt.Clear();
                _terminalDead.Clear();
                _cousinFightLoopStarted.Clear();
                _introArmReplayed.Clear();
                _preFightLogged.Clear();
                _luciaEyeConsumed.Clear();
                _luciaEyeCycleComplete.Clear();
                _pendingEyeComplete.Clear();
                _witchAppliedRevision.Clear();
                _witchPhaseRevision = 0;
                _lastHostWitchPhase = -1;
                _pendingP2Manifest = null;
                _witchP2Cycle = 0;
                _witchP2Active = false;
                _witchP2RealHitConsumed = false;
                _witchP2DefeatedDomes.Clear();
                _p2ClientAppliedCycle = -1;
                WitchBossControllerAdapter.ClearPhase2State();
            }
        }

        // ---- reentry guard (set while applying a host-driven start) ----
        private static void BeginApply() { lock (_lock) _reentryDepth++; }
        private static void EndApply() { lock (_lock) { if (_reentryDepth > 0) _reentryDepth--; } }
        private static bool InReentry { get { lock (_lock) return _reentryDepth > 0; } }

        // ---- Phase 5.4-E2: continuation window ----
        private static void OpenContinuation(string key, IBossEncounterAdapter adapter, object component, string openedBy)
        {
            lock (_lock)
            {
                _continuation[key] = new ContinuationWindow
                {
                    Adapter = adapter,
                    Component = component,
                    OpenedBy = openedBy,
                    OpenedAt = Time.realtimeSinceStartup,
                    LastActivityAt = Time.realtimeSinceStartup,
                };
            }
        }

        /// <summary>True while the host-authorized start chain for this key is still unfolding. Closes once the
        /// fight is observed started and a short grace elapses, or when the run changes (window cleared).</summary>
        private static bool IsContinuationActive(string key, out ContinuationWindow window)
        {
            lock (_lock)
            {
                if (!_continuation.TryGetValue(key, out window!)) return false;
                if (window.StartObserved && (Time.realtimeSinceStartup - window.StartObservedAt) > ContinuationGraceSeconds)
                {
                    _continuation.Remove(key);
                    return false;
                }
                return true;
            }
        }

        private static bool SafeStarted(IBossEncounterAdapter adapter, object component)
        {
            try { return adapter.IsStarted(component); } catch { return false; }
        }

        /// <summary>LD-Sandstorm / F4: true when a boss's per-frame combat should be SUPPRESSED on this end — i.e. we are
        /// a joined client and the boss has started (fightStarted). On the client the fight is host-authoritative, so the
        /// boss must not run its own aiming/firing/missile logic (that produced a divergent local fight + double damage).
        /// The boss stays visible (already assembled) + host-driven (puppet transform/animator, BossState health).</summary>
        public static bool ShouldSuppressClientBossCombat(object bossHelper)
        {
            try
            {
                if (!Enabled) return false;
                if (NetGameplaySyncBridge.BossMode != NetMode.Client) return false;
                return BossReflect.TryGetBool(bossHelper, "fightStarted", out bool fs) && fs;
            }
            catch { return false; }
        }

        /// <summary>LD-Sandstorm / F4: true on a joined client — the boss position is host-authoritative, so the client
        /// must NOT reposition the boss to its OWN camera during the intro (DesertClause.RepositionBossFromCamera places
        /// the rig 12 m in front of each end's camera → the ~35 m divergence). The host keeps its reposition (cinematic);
        /// the client leaves the boss at its placed position and follows the host. Not gated on fightStarted (pre-fight).</summary>
        public static bool ShouldSuppressClientBossReposition()
        {
            try { return Enabled && NetGameplaySyncBridge.BossMode == NetMode.Client; }
            catch { return false; }
        }

        /// <summary>LD-Sandstorm / F4: true while a registered boss that runs a LOCAL intro presentation (Desert) is
        /// mid-intro on this client — the host start has been applied but the fight has not yet started (the intro
        /// animation chain is playing toward TriggerFight). The enemy-state apply loop uses this to keep the boss out of
        /// the generic puppet system for the duration of its intro, so the intro runs locally (visible body + cutscene);
        /// once TriggerFight fires (IsStarted), the puppet resumes and the boss is host-authoritative again.</summary>
        public static bool IsLocalIntroPresentationActive()
        {
            lock (_lock)
            {
                foreach (var kv in _registry)
                {
                    var e = kv.Value;
                    if (e.Adapter == null || e.Component == null) continue;
                    try
                    {
                        if (!e.Adapter.RunsLocalIntroPresentation(e.Component)) continue;
                        if (_appliedStart.Contains(kv.Key) && !SafeStarted(e.Adapter, e.Component))
                            return true;
                    }
                    catch { }
                }
            }
            return false;
        }

        private static string SafeDescribe(IBossEncounterAdapter adapter, object component)
        {
            try { return adapter.DescribeForLog(component); } catch (Exception ex) { return $"describe-failed:{ex.GetType().Name}"; }
        }

        private static void TouchContinuation(ContinuationWindow window)
        {
            window.LastActivityAt = Time.realtimeSinceStartup;
            if (!window.StartObserved)
            {
                try
                {
                    if (window.Adapter.IsStarted(window.Component)) { window.StartObserved = true; window.StartObservedAt = Time.realtimeSinceStartup; }
                }
                catch { }
            }
        }

        // ---- context ----
        private static bool TryBuildContext(out BossEncounterContext ctx, out string runScope)
        {
            ctx = default; runScope = "";
            if (!NetRunStateBridge.TryGetLocalRunState(out var run) || !run.HasLevel) return false;
            ctx = new BossEncounterContext
            {
                ChapterName = run.ChapterName,
                LevelIndex = run.LevelIndex,
                HasSeed = run.HasLevelSeed,
                Seed = run.LevelSeed,
                GraphName = run.LevelGenerator ?? "",
                RunKey = $"{run.ChapterName}:{run.LevelIndex}:{(run.HasLevelSeed ? run.LevelSeed.ToString() : "?")}",
            };
            runScope = ctx.RunKey;
            return true;
        }

        private static IBossEncounterAdapter? ResolveAdapter(object component)
        {
            foreach (var a in _adapters)
            {
                try { if (a.CanHandle(component)) return a; } catch { }
            }
            return null;
        }

        /// <summary>Phase 5.4-E2: family-aware compact describe for the lifecycle probe (reuses the adapters).
        /// Returns null when no adapter handles the component (e.g. a BossPhase — the probe reads it generically).</summary>
        public static string? DescribeComponent(object component)
        {
            var a = ResolveAdapter(component);
            return a == null ? null : SafeDescribe(a, component);
        }

        /// <summary>Phase 5.4-E4: resolve the owning Boss encounter key for a boss MonoBehaviour (the spawn manifest
        /// uses this to attribute a runtime-spawned add to its encounter). Registers the encounter as a side effect.
        /// Returns false if the component is not a known Boss or there is no current run state.</summary>
        public static bool TryGetEncounterKeyForBoss(object bossComponent, out string key, out string bossType)
        {
            key = ""; bossType = "";
            try
            {
                if (!Enabled || bossComponent == null) return false;
                var adapter = ResolveAdapter(bossComponent);
                if (adapter == null) return false;
                if (!TryBuildContext(out var ctx, out var runScope)) return false;
                OnLevelChanged(runScope);
                var id = adapter.BuildEncounterId(bossComponent, in ctx);
                key = id.Key;
                bossType = bossComponent.GetType().Name;
                Register(key, adapter, bossComponent);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Phase 5.4-E4: current run identity (chapter/level/seed) for spawn-manifest validation.</summary>
        public static bool TryGetRunContext(out string chapter, out int level, out bool hasSeed, out int seed)
        {
            chapter = ""; level = -1; hasSeed = false; seed = 0;
            if (!TryBuildContext(out var ctx, out _)) return false;
            chapter = ctx.ChapterName; level = ctx.LevelIndex; hasSeed = ctx.HasSeed; seed = ctx.Seed;
            return true;
        }

        // ================================================================== local start entrypoint

        /// <summary>
        /// Called from the Boss start-entrypoint patches. Returns true to run the original method, false to BLOCK
        /// it (client defers to the host). Never throws.
        /// </summary>
        public static bool OnLocalStartEntrypoint(object component, string source)
        {
            try
            {
                if (!Enabled || component == null) return true;
                if (InReentry) return true; // we are applying a host-driven start — let the original run

                var adapter = ResolveAdapter(component);
                if (adapter == null) return true; // unknown component — not ours

                if (!TryBuildContext(out var ctx, out var runScope)) return true; // no run state yet — don't interfere
                OnLevelChanged(runScope);

                var id = adapter.BuildEncounterId(component, in ctx);
                string key = id.Key;
                Register(key, adapter, component);

                NetMode mode = NetGameplaySyncBridge.BossMode;
                bool joined = false;
                try { joined = NetClientJoinFlow.SessionJoinedHost; } catch { }

                // Phase PF-0: read-only convergence diagnostic. Captures, at the exact frame a boss pre-fight start
                // entrypoint fires on THIS end, whether the peers share the same boss level instance (scene+seed).
                // The known infinite-dialog bug (Log99) is a client that raced ahead into a divergent seed → orphan
                // boss; this line is meant to prove or refute that, plus give the relative host/client timing.
                LogPreFight(source, key, mode, joined, in ctx);

                // Phase RM (room-membership): record that THIS end's local player crossed into the boss room (its
                // room-entry trigger). Fires BEFORE any start gating/dedup so it is captured even when the start itself
                // is blocked or suppressed. PlayerTrigger.onlyOnce is per-end, so each player who reaches the boss fires
                // their own end's trigger → the host learns every in-room player. Observe-only (no behavior change).
                if (RoomMembershipActive && (mode == NetMode.Host || (mode == NetMode.Client && joined))
                    && adapter.IsRoomEntrySource(source))
                    ReportLocalRoomEntry(key, in ctx, source, mode);

                // G7c TERMINAL GATE: once an encounter is dead, NEVER re-run its start chain. LogOutput41: after the
                // witch died, the client returned to the entrance, re-triggered EventStarted and it slipped through the
                // STALE authorized-continuation window (currentPhase=8) → re-TeleportPlayerTo + re-StartFight + boss
                // music restart. A terminal encounter must reject every start source on both ends, ahead of the
                // continuation / appliedStart logic.
                bool terminal; lock (_lock) { terminal = _terminalDead.Contains(key); }
                if (terminal && mode == NetMode.Client && joined)
                {
                    BossLocalStartBlocked++;
                    if (LogOn) Plugin.Log.Info($"[BossEncounter] client blocked start on TERMINAL encounter source={source} key={key}");
                    return false;
                }

                // Phase PF (Plan B) FIGHT GATE: for a dialog-gated boss the intro+dialog play via the behavior tree,
                // but the fight must wait until an in-room player DISMISSES the dialog. In single-player the dialog
                // pauses the game (timeScale=0), freezing the WaitForSeconds before StartFight; co-op disables that
                // pause (Phase 5.7-NP), so the behavior tree's StartFight would fire ~1.1s after the dialog OPENS and
                // overlap it. Block every behavior-tree StartFight here — the real StartFight is driven only by the
                // host-authoritative dialog-close commit (CommitFightStartLocal), which runs under the reentry guard
                // and bypasses this via the InReentry early-out at the top. Intro steps and all else flow normally.
                if (GateFightActive && (mode == NetMode.Host || (mode == NetMode.Client && joined))
                    && source.EndsWith(".StartFight", StringComparison.Ordinal) && adapter.GatesFightOnDialogClose(component))
                {
                    // Block the behavior-tree StartFight ONLY while the fight has not actually started yet. Once the boss
                    // is started (our dialog-close commit already invoked StartFight), ALLOW a trailing behavior-tree
                    // StartFight to run. This is critical for the late-client case (Log134): when the FIGHT commit
                    // arrives before the client's intro (host triggered + the client loads the boss late), the commit
                    // starts the fight first, THEN Introduction runs late and RE-applies the Cinematic lock + invuln —
                    // the trailing StartFight is what clears them, so blocking it freezes the player forever. Keyed on
                    // the boss's real FightStarted (which lives on the Unit), NOT our _fightCommitted set (which an
                    // OnLevelChanged level-step refresh can clear mid-encounter → the Log134 committed=False).
                    if (!SafeStarted(adapter, component))
                    {
                        BossLocalStartBlocked++;
                        if (LogOn) Plugin.Log.Info($"[BossFightGate] blocked behavior-tree StartFight source={source} key={key} mode={mode} (fight starts only on a dialog-close commit)");
                        return false;
                    }
                    if (LogOn) Plugin.Log.Info($"[BossFightGate] allowed trailing StartFight (already started — clears a late intro's lock) source={source} key={key} mode={mode}");
                    return true;
                }

                if (mode == NetMode.Host)
                {
                    // HOST RE-ENTRY GUARD (Log123 root fix): vanilla relies on the encounter initiator firing exactly
                    // once (PlayerTrigger.onlyOnce / RemoveInteractable). In MP the fight can start REMOTELY (a client's
                    // commit), so the host's LOCAL initiator was never consumed — when the host player later reaches it,
                    // CousinHelper.Trigger re-runs the WHOLE start chain (re-Introduction / re-StartFight / re-broadcast)
                    // and the boss restarts its intro. The host had no "already started" guard (only the client did), so
                    // it re-ran every time. Block the re-entry here. (Reflection-applied chain steps bypass this via the
                    // InReentry early-out above, so the host's own first start is unaffected.)
                    if (SafeStarted(adapter, component) || _appliedStart.Contains(key))
                    {
                        BossDuplicateSuppressed++;
                        if (LogOn) Plugin.Log.Info($"[BossEncounter] host suppressed re-entry start source={source} key={key} (already started — initiator was not locally consumed)");
                        return false;
                    }

                    BroadcastStartOnce(key, adapter, component, in ctx, source);
                    // Dialog-gated bosses (Cousin / Lucia): when the host reaches the "fight committed" step, tell all
                    // clients to finalize their local dialog and start once (real dialog API on their end).
                    if (adapter.IsDialogBoss(component) && adapter.IsDialogCommitSource(source))
                    {
                        BroadcastDialogCommitOnce(key, adapter, component, in ctx, source);
                        // Remove the boss dialog interactable on the host too (FF14 spec: dialogs are removed once the
                        // fight starts). Now backed by the host re-entry guard, not the sole defence.
                        RemoveDialogInteractableOnce(key, adapter, component, "host-initiated");
                    }
                    return true; // host runs the original boss start
                }

                if (mode == NetMode.Client && joined)
                {
                    // (0.5) Phase PF FAITHFUL INTRO — runs FIRST: the boss's OWN behavior-tree intro DOWNSTREAM steps
                    // (Introduction / StartFight) must ALWAYS be allowed to run locally so the real intro + dialog plays.
                    // The behavior tree only reaches these once triggeredByPlayer is set, which on a client happens ONLY
                    // via an applied host commit (the initial Trigger is still blocked below to request authority), so
                    // this is not a private start. Blocking Introduction makes introPlayed never set, so the behavior
                    // tree re-runs the intro forever (LogOutput130/131 infinite dialog) — and gating on the window or
                    // _appliedStart is fragile (the window expires for a late client; _appliedStart is cleared on a
                    // run-scope change). Introduction/StartFight are internally idempotent (introPlayed / FightStarted),
                    // so allowing them unconditionally cannot loop. Initial Trigger/TriggerIntro are NOT allowed here.
                    bool faithful = false; try { faithful = Plugin.Cfg.EnableFaithfulBossIntro.Value; } catch { }
                    // LD-Sandstorm / F4: Desert's fight-start is TriggerFight (not Introduction/StartFight), fired by its
                    // OWN intro animation event after the host-authorized OnStartInteractWithBoss. The intro animation is
                    // slow, so the E2 continuation window is usually gone by the time TriggerFight fires → it was being
                    // blocked as a "private local start" → fightStarted never flips → the composite body never assembles
                    // (sandSantaAnimationSprite never hidden, "BossStarted" never set) → invisible on the client. Like
                    // Cousin's Introduction/StartFight, a client TriggerFight is only reachable after a host-authorized
                    // OnStartInteractWithBoss (the client's own initial interact is blocked below), so it is NOT a private
                    // start. Allow it once (guarded on !started so the non-idempotent TriggerFight can't double-run).
                    bool introTrigger = false;
                    try { introTrigger = source.EndsWith(".TriggerFight", StringComparison.Ordinal) && adapter.RunsLocalIntroPresentation(component) && !SafeStarted(adapter, component); }
                    catch { }
                    if (faithful && (source.EndsWith(".Introduction", StringComparison.Ordinal) || source.EndsWith(".StartFight", StringComparison.Ordinal) || introTrigger))
                    {
                        BossContinuationAllowed++;
                        if (LogOn) Plugin.Log.Info($"[BossEncounter] faithful intro: allowed native chain source={source} key={key} started={SafeStarted(adapter, component)}");
                        return true;
                    }

                    // (0) Duplicate dialog entry: after FightStarted, a re-fired Trigger/Introduction would re-open the
                    // boss dialog (LogOutput21: SetCurrentSpeakable after FightStarted=True). Suppress the original so
                    // the dialog is not reopened. StartFight is NOT a dialog-entry source, so it still flows below.
                    if (adapter.IsDialogBoss(component) && adapter.ShouldSuppressDuplicateDialogEntry(component, source))
                    {
                        BossDialogEntrySuppressed++;
                        if (LogOn) Plugin.Log.Info($"[BossEncounter] BossDialogCommit duplicate suppressed source={source} key={key} FightStarted={SafeStarted(adapter, component)} speakable={BossDialogReflect.CurrentSpeakableName()}");
                        return false;
                    }

                    // (1) Authorized continuation: a later step of a host-authorized start chain (e.g. Witch.StartFight
                    // fired by the intro coroutine, Cousin.Introduction/StartFight fired by dialogue). Let the original
                    // game logic run so the Boss reaches a consistent started state — this is NOT a private local start.
                    if (IsContinuationActive(key, out var win) && adapter.IsContinuationSource(source))
                    {
                        TouchContinuation(win);
                        BossContinuationAllowed++;
                        if (LogOn) Plugin.Log.Info($"[BossEncounter] client allowed authorized continuation source={source} key={key} reason=HostBossEncounterStart started={SafeStarted(adapter, component)}");
                        return true;
                    }

                    // (2) Host already drove this encounter to a started state (window closed): do not re-request a
                    // start — that caused duplicate ClientBossStartRequest -> host double-start. Just block silently.
                    if (_appliedStart.Contains(key) || SafeStarted(adapter, component))
                    {
                        BossDuplicateSuppressed++;
                        if (LogOn) Plugin.Log.Info($"[BossEncounter] client suppressed duplicate local start source={source} key={key} (already host-authorized/started)");
                        return false;
                    }

                    // (3) Genuine first local start before any host authority: defer to host. Dialog-gated bosses send
                    // an explicit dialog-commit request (sync the "I chose to fight" decision); others request a start.
                    bool block = true;
                    try { block = Plugin.Cfg.BossEncounterClientBlockLocalStart.Value; } catch { }
                    if (adapter.IsDialogBoss(component))
                        RequestDialogCommitOnce(key, adapter, component, in ctx, source);
                    else
                        RequestStartOnce(key, adapter, component, in ctx, source);
                    if (block)
                    {
                        BossLocalStartBlocked++;
                        BossContinuationBlocked++;
                        if (LogOn) Plugin.Log.Info($"[BossEncounter] client blocked local start source={source} {id.ToCompact()} — requested host authority");
                        return false; // block local independent start
                    }
                    return true;
                }

                // single-player / host-disconnected / client-not-joined: preserve own play.
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[BossEncounter] OnLocalStartEntrypoint failed: {ex.GetType().Name}: {ex.Message}");
                return true;
            }
        }

        private static bool PreFightLogOn
        {
            get { try { return Plugin.Cfg.LogBossPreFight.Value; } catch { return false; } }
        }

        /// <summary>Phase PF-0: one read-only line per pre-fight start entrypoint, with the convergence state at that
        /// instant. Compares THIS end's level instance to every known peer (host→clients, client→host). De-duplicated
        /// per (key, source, convergence) so a re-fired entrypoint with the same state does not spam.</summary>
        private static readonly HashSet<string> _preFightLogged = new HashSet<string>();
        private static void LogPreFight(string source, string key, NetMode mode, bool joined, in BossEncounterContext ctx)
        {
            if (!PreFightLogOn) return;
            try
            {
                bool terminal; lock (_lock) { terminal = _terminalDead.Contains(key); }
                string conv = NetGameplaySyncBridge.FormatBossConvergence(out bool allConverged);
                string dedupe = $"{key}|{source}|{allConverged}|{conv.GetHashCode()}";
                lock (_lock) { if (!_preFightLogged.Add(dedupe)) return; }
                Plugin.Log.Info($"[BossPreFight] entry source={source} mode={mode} joined={joined} terminal={terminal} key={key} allConverged={allConverged} | {conv}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[BossPreFight] log failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static void Register(string key, IBossEncounterAdapter adapter, object component)
        {
            lock (_lock)
            {
                if (!_registry.ContainsKey(key))
                {
                    BossEncounterDiscovered++;
                    if (LogOn) Plugin.Log.Info($"[BossEncounter] discovered {adapter.DescribeForLog(component)} key={key}");
                }
                _registry[key] = new Entry { Adapter = adapter, Component = component };
            }
        }

        private static void BroadcastStartOnce(string key, IBossEncounterAdapter adapter, object component, in BossEncounterContext ctx, string source)
        {
            lock (_lock) { if (!_hostBroadcast.Add(key)) return; }
            var state = BuildState(key, adapter, component, in ctx, source);
            BossStartBroadcast++;
            if (LogOn) Plugin.Log.Info($"[BossEncounter] host broadcasting BossEncounterStart {state.ToCompact()}");
            NetGameplaySyncBridge.BroadcastHostBossEncounterStart(state);
            TryBeginSandstormArena(adapter, component, in ctx);
        }

        /// <summary>HOST: if this boss fights inside a gate-less sandstorm arena (Desert), arm the arena-lockdown pull-in
        /// so out-of-room stragglers are teleported in a few seconds after the dialog trigger (the sandstorm outside the
        /// ring would otherwise grind them down). No-op for gated / normal bosses. Called from both host start-broadcast
        /// paths (host-triggered and client-request-driven). Never throws.</summary>
        private static void TryBeginSandstormArena(IBossEncounterAdapter adapter, object component, in BossEncounterContext ctx)
        {
            try
            {
                if (!NetGameplaySyncBridge.IsHost) return;
                if (!adapter.TryGetSandstormArenaSphere(component, out Vector3 center, out _)) return;
                ArenaLockdownManager.BeginSandstormArena(center, ctx.ChapterName, ctx.LevelIndex, ctx.HasSeed, ctx.Seed);
            }
            catch (Exception ex) { Plugin.Log.Warn($"[BossEncounter] TryBeginSandstormArena failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>LD-Sandstorm: resolve THIS end's live sandstorm-arena danger sphere (moving centre + world radius)
        /// from the registered boss (Desert). Used by the arena lockdown for an accurate, moving in/out test instead of a
        /// hardcoded radius. Returns false if no such boss is registered / the sphere can't be read.</summary>
        public static bool TryGetSandstormArenaSphere(out Vector3 center, out float radius)
        {
            center = Vector3.zero; radius = 0f;
            lock (_lock)
            {
                foreach (var kv in _registry)
                {
                    var e = kv.Value;
                    if (e.Adapter == null || e.Component == null) continue;
                    try { if (e.Adapter.TryGetSandstormArenaSphere(e.Component, out center, out radius)) return true; }
                    catch { }
                }
            }
            return false;
        }

        private static void RequestStartOnce(string key, IBossEncounterAdapter adapter, object component, in BossEncounterContext ctx, string source)
        {
            lock (_lock) { if (!_clientRequested.Add(key)) return; }
            var req = new NetClientBossStartRequest
            {
                EncounterKey = key,
                BossType = component.GetType().Name,
                GraphName = ctx.GraphName,
                RootName = BossReflect.RootName(component),
                ChapterName = ctx.ChapterName,
                LevelIndex = ctx.LevelIndex,
                HasSeed = ctx.HasSeed,
                Seed = ctx.Seed,
                StartSource = source,
                SentAt = Time.realtimeSinceStartup,
            };
            BossStartRequestSent++;
            if (LogOn) Plugin.Log.Info($"[BossEncounter] client sending ClientBossStartRequest {req.ToCompact()}");
            NetGameplaySyncBridge.SendClientBossStartRequest(req);
        }

        private static NetBossEncounterState BuildState(string key, IBossEncounterAdapter adapter, object component, in BossEncounterContext ctx, string source)
        {
            adapter.TryReadState(component, out bool hasPhase, out int phaseIndex, out bool hasPos, out Vector3 pos);
            return new NetBossEncounterState
            {
                EncounterKey = key,
                BossType = component.GetType().Name,
                GraphName = ctx.GraphName,
                RootName = BossReflect.RootName(component),
                ChapterName = ctx.ChapterName,
                LevelIndex = ctx.LevelIndex,
                HasSeed = ctx.HasSeed,
                Seed = ctx.Seed,
                Started = true,
                StartSource = source,
                HostRevision = 0,
                HostTimestamp = Time.realtimeSinceStartup,
                HasPosition = hasPos,
                Position = pos,
                HasPhaseIndex = hasPhase,
                PhaseIndex = phaseIndex,
            };
        }

        // ================================================================== E3: dialog commit

        private static NetBossDialogCommit BuildDialogCommit(string key, object component, in BossEncounterContext ctx, string source)
            => new NetBossDialogCommit
            {
                EncounterKey = key,
                BossType = component.GetType().Name,
                GraphName = ctx.GraphName,
                RootName = BossReflect.RootName(component),
                ChapterName = ctx.ChapterName,
                LevelIndex = ctx.LevelIndex,
                HasSeed = ctx.HasSeed,
                Seed = ctx.Seed,
                CommitSource = source,
                Revision = 0,
                Timestamp = Time.realtimeSinceStartup,
            };

        private static void BroadcastDialogCommitOnce(string key, IBossEncounterAdapter adapter, object component, in BossEncounterContext ctx, string source)
        {
            lock (_lock) { if (!_dialogCommitBroadcast.Add(key)) return; }
            var msg = BuildDialogCommit(key, component, in ctx, source);
            // RM-2b: only mark the cutscene "played locally" if THIS end is actually in-room. The host calls this both when
            // it ORIGINATED (host walked in → in-room → played natively) AND when RELAYING a client's commit (host may be
            // out-of-room → must NOT be marked played, or it skips the forced boss appearance + catch-up).
            MarkDialogSessionStarted(key, msg, playedLocally: LocalInRoom(key));
            BossDialogCommitBroadcast++;
            Plugin.Log.Info($"[BossDialogCommit] host confirmed + broadcasting {msg.ToCompact()} speakable={BossDialogReflect.CurrentSpeakableName()}");
            NetGameplaySyncBridge.BroadcastHostBossDialogCommit(msg);
        }

        private static void RequestDialogCommitOnce(string key, IBossEncounterAdapter adapter, object component, in BossEncounterContext ctx, string source)
        {
            lock (_lock) { if (!_clientRequested.Add(key)) return; }
            var msg = BuildDialogCommit(key, component, in ctx, source);
            MarkDialogSessionStarted(key, msg, playedLocally: LocalInRoom(key)); // RM-2b: client originated → in-room → played natively
            BossDialogCommitRequested++;
            Plugin.Log.Info($"[BossDialogCommit] request {msg.ToCompact()} speakable={BossDialogReflect.CurrentSpeakableName()}");
            NetGameplaySyncBridge.SendClientBossDialogCommitRequest(msg);
        }

        public static void HandleClientBossDialogCommitRequest(NetBossDialogCommit msg, string peerId)
        {
            try
            {
                if (!Enabled || msg == null || NetGameplaySyncBridge.BossMode != NetMode.Host) return;
                if (LogOn) Plugin.Log.Info($"[BossDialogCommit] host received request from {peerId}: {msg.ToCompact()}");

                if (!TryBuildContext(out var ctx, out _) || !string.Equals(ctx.ChapterName, msg.ChapterName, StringComparison.Ordinal)
                    || ctx.LevelIndex != msg.LevelIndex || (ctx.HasSeed && msg.HasSeed && ctx.Seed != msg.Seed))
                {
                    BossStartRejectedSession++;
                    Plugin.Log.Warn($"[BossDialogCommit] reject request from {peerId}: run mismatch req={msg.ChapterName}:{msg.LevelIndex} host={ctx.ChapterName}:{ctx.LevelIndex}");
                    return;
                }

                // Phase PF (Plan B): a client dismissed the gated boss's intro dialog → commit the fight authoritatively
                // for everyone (start it on the host + broadcast a FIGHT commit). Separate from the INTRO commit below.
                if (msg.IsFightCommit)
                {
                    if (!TryFindLocalEncounter(msg.EncounterKey, out var fAdapter, out var fComp))
                    {
                        BossEncounterNotFound++;
                        Plugin.Log.Warn($"[BossFightGate] host has no encounter for client fight-commit key={msg.EncounterKey}; candidates: {DescribeCandidates()}");
                        return;
                    }
                    Plugin.Log.Info($"[BossFightGate] host received client fight-commit from {peerId}: {msg.ToCompact()}");
                    CommitFightStart(msg.EncounterKey, fAdapter, fComp, "client:" + (msg.CommitSource ?? ""));
                    return;
                }

                if (!TryFindLocalEncounter(msg.EncounterKey, out var adapter, out var component))
                {
                    BossEncounterNotFound++;
                    Plugin.Log.Warn($"[BossDialogCommit] host has no local encounter for key={msg.EncounterKey}; candidates: {DescribeCandidates()}");
                    return;
                }

                // Host applies the commit authoritatively (finalize host dialog if any + ensure started once),
                // then broadcasts the commit to all clients. Reentry guard so the StartFight isn't blocked/re-requested.
                BeginApply();
                try { ApplyIntroCutsceneGated(msg.EncounterKey, adapter, component, msg, out string d); if (LogOn) Plugin.Log.Info($"[BossDialogCommit] host applied own commit key={msg.EncounterKey}: {d}"); }
                finally { EndApply(); }

                // Fix A (root): the fight is committed (client-initiated) — remove the host's own boss dialog
                // interactable so a host player arriving late can't open the stale dialog (the LogOutput121 loop).
                RemoveDialogInteractableOnce(msg.EncounterKey, adapter, component, "host-apply-client-commit");

                BroadcastDialogCommitOnce(msg.EncounterKey, adapter, component, in ctx, "ClientRequest:" + msg.CommitSource);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[BossDialogCommit] HandleClientBossDialogCommitRequest failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Fix A (root): remove the boss dialog interactable once per encounter, on whichever end calls this
        /// (host at commit/start, client at commit). After removal nothing can re-open the boss dialog, which is the
        /// real fix for the host stale-dialog loop on a remotely-started fight. Gated by config; safety-net suppression
        /// stays in place either way.</summary>
        private static void RemoveDialogInteractableOnce(string key, IBossEncounterAdapter adapter, object component, string ctx)
        {
            try
            {
                if (!Plugin.Cfg.RemoveBossDialogInteractableOnStart.Value) return;
                lock (_lock) { if (!_dialogInteractableRemoved.Add(key)) return; }
                bool ok = adapter.TryRemoveDialogInteractable(component, out string detail);
                Plugin.Log.Info($"[BossDialogFix] removed boss dialog interactable ({ctx}) key={key} ok={ok}: {detail}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[BossDialogFix] interactable removal failed key={key}: {ex.GetType().Name}: {ex.Message}"); }
        }

        // ================================================================== RM-2b: intro cutscene gated to in-room

        private static bool CutsceneGateActive
        {
            get { try { return Enabled && Plugin.Cfg.GateBossDialogToInRoom.Value && Plugin.Cfg.EnableFaithfulBossIntro.Value; } catch { return false; } }
        }

        /// <summary>RM-2b: has THIS end's local player entered the boss room (crossed the room-entry trigger, or been
        /// teleported in by the arena lockdown)? Drives whether this end plays the synced intro cutscene.</summary>
        private static bool LocalInRoom(string key)
        {
            lock (_lock) { return _roomEnterReported.Contains(key) || _localTeleportedIn.Contains(key); }
        }

        /// <summary>RM-2b: mark the intro dialog session open for a key (so late entrants can catch up). The originating
        /// end played the cutscene natively, so it is also marked played.</summary>
        private static void MarkDialogSessionStarted(string key, NetBossDialogCommit msg, bool playedLocally)
        {
            if (!CutsceneGateActive || string.IsNullOrEmpty(key)) return;
            lock (_lock)
            {
                _dialogSessionActive.Add(key);
                if (msg != null) _lastIntroCommit[key] = msg;
                if (playedLocally) _cutscenePlayed.Add(key);
            }
        }

        /// <summary>RM-2b: apply the intro cutscene replay (TryApplyDialogCommit) ONLY if this end is in-room; otherwise
        /// defer it (the out-of-room end is not pulled into the cutscene / camera lock). Tracks the session either way so
        /// a later entry can catch up. Caller wraps BeginApply/EndApply.</summary>
        private static bool ApplyIntroCutsceneGated(string key, IBossEncounterAdapter adapter, object component, NetBossDialogCommit msg, out string detail)
        {
            lock (_lock) { _dialogSessionActive.Add(key); if (msg != null) _lastIntroCommit[key] = msg; }

            if (CutsceneGateActive && !LocalInRoom(key))
            {
                // Out-of-room: the boss MUST still APPEAR here (invariant: boss appears on all ends, so mechanics stay in
                // sync), but WITHOUT the cutscene. Force the appearance directly via Introduction (idempotent) — the
                // suppression patches no-op its camera/Cinematic-lock and the dialog (Npc.Interact) is blocked, so it just
                // rises. Do NOT mark _cutscenePlayed: this player never saw the dialog, so a later entry/teleport catches
                // it up. (TriggerIntro is avoided — it only sets a flag the boss's behavior tree may never tick when the
                // boss isn't active yet, so the boss wouldn't appear until the player walks near — Log172.)
                BeginApply();
                try { BossReflect.TryInvoke(component, "Introduction", out detail); }
                finally { EndApply(); }
                detail = "out-of-room headless appear: " + detail;
                Plugin.Log.Info($"[BossDialogCutscene] {detail} key={key}");
                return true;
            }

            bool ok = adapter.TryApplyDialogCommit(component, msg, out detail);
            lock (_lock) { _cutscenePlayed.Add(key); }
            return ok;
        }

        /// <summary>RM-2b: a late entrant (walked in or got teleported in) — if the dialog session is still active and we
        /// haven't played the cutscene, replay the intro now so they catch up. No-op once the fight has committed.</summary>
        private static void TryCatchUpCutscene(string key)
        {
            try
            {
                if (!CutsceneGateActive) return;
                if (!TryFindLocalEncounter(key, out var adapter, out var component)) return;
                try { if (!adapter.GatesFightOnDialogClose(component)) return; } catch { return; }

                // The dialog session is "active" iff the boss has been triggered but the fight isn't committed yet — derive
                // it from RELIABLE state (the boss + _fightCommitted, which survives same-level churn) rather than the
                // fragile _dialogSessionActive flag. Gate the catch-up on: in-room, not yet caught up, fight not committed.
                bool inRoom, played, committed;
                lock (_lock)
                {
                    inRoom = _roomEnterReported.Contains(key) || _localTeleportedIn.Contains(key);
                    played = _cutscenePlayed.Contains(key);
                    committed = _fightCommitted.Contains(key);
                }
                bool started = false; try { started = SafeStarted(adapter, component); } catch { }
                // CRITICAL: catch-up is ONLY for a LATE entrant joining an ALREADY-APPEARED boss (someone else triggered
                // it; the dialog was blocked for this end while out-of-room). If the boss has NOT appeared (introPlayed
                // false), THIS player is effectively the trigger → let the NATIVE flow run (CousinHelper.Trigger → intro →
                // dialog); catching up here would hijack it and loop the dialog (Log170: host walk-in → infinite dialog +
                // boss not appearing).
                bool appeared = false; try { appeared = BossReflect.TryGetBool(component, "introPlayed", out bool ip) && ip; } catch { }
                if (!inRoom || played || committed || started || !appeared)
                {
                    if (LogOn) Plugin.Log.Info($"[BossDialogCutscene] catch-up SKIP key={key} inRoom={inRoom} played={played} committed={committed} started={started} appeared={appeared}");
                    return;
                }
                lock (_lock) { _appliedStart.Add(key); _cutscenePlayed.Add(key); }

                // Boss already appeared; its built-in dialog step is unreliable on a late trigger (Log167), so open the
                // dialog DIRECTLY — the reliable path.
                TryOpenBossDialogLocally(key);
                Plugin.Log.Info($"[BossDialogCutscene] catch-up dialog key={key} (boss already appeared)");
                try { adapter.OnClientPresentationStart(component); } catch { }
            }
            catch (Exception ex) { Plugin.Log.Warn($"[BossDialogCutscene] catch-up failed key={key}: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>RM-2b: the arena lockdown teleported the local player into the arena. Treat it as a room entry: if a
        /// dialog session is still active for an in-scene gated boss, catch up its cutscene. (Usually the fight has already
        /// started by teleport time, so this is a no-op then.)</summary>
        public static void OnLocalTeleportedIntoArena()
        {
            try
            {
                if (!CutsceneGateActive) return;
                // Teleported in = in-room for every gated in-scene boss. Iterate the live registry (not the fragile
                // _dialogSessionActive) and let TryCatchUpCutscene self-gate (appeared + !committed + !played).
                string[] keys; lock (_lock) { keys = _registry.Keys.ToArray(); }
                foreach (var key in keys)
                {
                    lock (_lock) { _localTeleportedIn.Add(key); }
                    TryCatchUpCutscene(key);
                }
            }
            catch (Exception ex) { Plugin.Log.Warn($"[BossDialogCutscene] OnLocalTeleportedIntoArena failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        // ---- RM-2b: suppress the player-facing intro cutscene (camera turn + Cinematic lock + dialog) for an OUT-OF-ROOM
        // player, even when the boss's NATIVE behavior tree runs the intro (it can fire on this end once the boss is woken
        // near a remote player). The boss still APPEARS (Introduction's teleport-to-pool + rise anim run); only the local
        // player's camera/controller-lock/dialog are no-op'd by the patches that read IsSuppressingBossCutscene.
        private static bool _suppressBossCutscene;
        public static bool IsSuppressingBossCutscene => _suppressBossCutscene;
        public static void SetSuppressBossCutscene(bool on) => _suppressBossCutscene = on;

        /// <summary>RM-2b: cutscene-gated AND the local player is OUT of this boss's room → suppress its intro effects.</summary>
        public static bool IsLocalOutOfRoomForBoss(object component)
        {
            try
            {
                if (!CutsceneGateActive || component == null) return false;
                if (!TryGetEncounterKeyForBoss(component, out string key, out _)) return false;
                return !LocalInRoom(key);
            }
            catch { return false; }
        }

        // ---- Invariant: the boss stays INVULNERABLE until the fight is committed (co-op's no-pause lets the boss's
        // DoneAppearing anim event clear its invuln mid-dialog, before StartFight — so players could damage it pre-fight).

        /// <summary>Boss rise-anim "DoneAppearing" fired (it clears the boss's invulnerability). If the fight for this boss
        /// isn't committed yet, re-assert invulnerability so it can't be damaged before the fight officially starts.</summary>
        public static void OnBossDoneAppearing(object component)
        {
            try
            {
                if (!Enabled || !GateFightActive) return;
                if (!TryGetEncounterKeyForBoss(component, out string key, out _)) return;
                bool committed; lock (_lock) { committed = _fightCommitted.Contains(key); }
                if (committed) return; // fight started → leave it vulnerable
                if (SetBossInvulnerable(component, true) && LogOn)
                    Plugin.Log.Info($"[BossInvuln] kept boss invulnerable pre-fight key={key}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[BossInvuln] OnBossDoneAppearing failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>Set the boss owner unit's invulnerability (reflection: CousinHelper.owner.SetInvulnerable, falling back
        /// to the adapter's health unit). Returns true if applied.</summary>
        private static bool SetBossInvulnerable(object component, bool invuln)
        {
            try
            {
                object unit = null;
                try { unit = HarmonyLib.AccessTools.Field(component.GetType(), "owner")?.GetValue(component); } catch { }
                if (unit == null) { try { unit = ResolveAdapter(component)?.GetHealthUnit(component); } catch { } }
                if (unit == null) return false;
                var mi = HarmonyLib.AccessTools.Method(unit.GetType(), "SetInvulnerable");
                if (mi == null) return false;
                mi.Invoke(unit, new object[] { invuln });
                return true;
            }
            catch { return false; }
        }

        /// <summary>RM-2b: record that the boss intro ran on this end while out-of-room (boss appeared, dialog blocked).
        /// A later catch-up must open the dialog directly (TriggerIntro would no-op — introPlayed is already set).</summary>
        public static void MarkIntroRanOutOfRoom(object component)
        {
            try { if (TryGetEncounterKeyForBoss(component, out string key, out _)) lock (_lock) { _introRanWhileOutOfRoom.Add(key); } }
            catch { }
        }

        /// <summary>RM-2b: open the boss dialog directly on THIS end (used by catch-up when the boss already appeared
        /// out-of-room, so the native intro won't re-open it). Invokes the boss health unit's Interact with the local
        /// player; NotifyBossDialogOpened (postfix) then arms the dialog-close fight commit as usual.</summary>
        private static void TryOpenBossDialogLocally(string key)
        {
            try
            {
                if (!TryFindLocalEncounter(key, out var adapter, out var component)) return;
                object healthUnit = null; try { healthUnit = adapter.GetHealthUnit(component); } catch { }
                if (healthUnit == null) return;
                var interact = HarmonyLib.AccessTools.Method(healthUnit.GetType(), "Interact");
                if (interact == null) return;
                object[] args = interact.GetParameters().Length == 1 ? new[] { ResolveLocalPlayerObject() } : System.Array.Empty<object>();
                BeginApply();
                try { interact.Invoke(healthUnit, args); } finally { EndApply(); }
                Plugin.Log.Info($"[BossDialogCutscene] catch-up opened boss dialog directly key={key}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[BossDialogCutscene] direct dialog open failed key={key}: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static object ResolveLocalPlayerObject()
        {
            try
            {
                var gmType = HarmonyLib.AccessTools.TypeByName("PerfectRandom.Sulfur.Core.GameManager");
                var gm = gmType == null ? null : HarmonyLib.AccessTools.Property(gmType, "Instance")?.GetValue(null, null);
                if (gm == null) return null;
                return HarmonyLib.AccessTools.Property(gmType, "PlayerObject")?.GetValue(gm, null)
                       ?? HarmonyLib.AccessTools.Field(gmType, "PlayerObject")?.GetValue(gm);
            }
            catch { return null; }
        }

        /// <summary>RM-2b: block the boss dialog (Npc.Interact) on THIS end when cutscene-gated and the local player is out
        /// of the room for the gated boss whose health unit is <paramref name="npc"/>.</summary>
        public static bool ShouldBlockBossDialogNpc(object npc)
        {
            try
            {
                if (!CutsceneGateActive || npc == null) return false;
                lock (_lock)
                {
                    foreach (var kv in _registry)
                    {
                        var e = kv.Value;
                        if (!(e.Component is UnityEngine.Object uo) || uo == null) continue;
                        try { if (!e.Adapter.GatesFightOnDialogClose(e.Component)) continue; } catch { continue; }
                        object healthUnit = null; try { healthUnit = e.Adapter.GetHealthUnit(e.Component); } catch { }
                        if (healthUnit != null && ReferenceEquals(healthUnit, npc))
                            return !(_roomEnterReported.Contains(kv.Key) || _localTeleportedIn.Contains(kv.Key));
                    }
                }
            }
            catch { }
            return false;
        }

        // ================================================================== PF (Plan B): dialog-close-gated fight start

        /// <summary>Phase PF (Plan B): the dialog-flow hook saw a BOSS Npc open its dialog. If it belongs to a gated
        /// encounter (its dialog speaker == the boss health unit), remember it so the matching dialog CLOSE can be read
        /// as the fight-start commit. Cheap registry scan; no-op for non-gated bosses. Never throws.</summary>
        public static void NotifyBossDialogOpened(object npc)
        {
            try
            {
                if (!Enabled || !GateFightActive || npc == null) return;
                NetMode mode = NetGameplaySyncBridge.BossMode;
                if (mode != NetMode.Host && mode != NetMode.Client) return;
                string? key = null;
                lock (_lock)
                {
                    foreach (var kv in _registry)
                    {
                        var e = kv.Value;
                        if (!(e.Component is UnityEngine.Object uo) || uo == null) continue;
                        try { if (!e.Adapter.GatesFightOnDialogClose(e.Component)) continue; } catch { continue; }
                        object? healthUnit = null; try { healthUnit = e.Adapter.GetHealthUnit(e.Component); } catch { }
                        if (healthUnit != null && ReferenceEquals(healthUnit, npc)) { key = kv.Key; break; }
                    }
                    if (key != null) _dialogOpenKey = key;
                }
                if (key != null && LogOn)
                    Plugin.Log.Info($"[BossFightGate] boss intro dialog opened key={key} (awaiting dismissal to commit fight)");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[BossFightGate] NotifyBossDialogOpened failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>Phase PF (Plan B): the dialog controller closed its active speakable (null). If a gated boss intro
        /// dialog was open on THIS end, treat the close as the in-room player's fight-start commit. Never throws.</summary>
        public static void NotifyDialogClosed()
        {
            try
            {
                if (!Enabled || !GateFightActive) return;
                string? key; lock (_lock) { key = _dialogOpenKey; _dialogOpenKey = null; }
                if (key == null) return;
                OnLocalBossDialogClosed(key);
            }
            catch (Exception ex) { Plugin.Log.Warn($"[BossFightGate] NotifyDialogClosed failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>An in-room player dismissed the gated boss's intro dialog on THIS end. Host: commit the fight
        /// authoritatively (start + broadcast). Client: report the dismissal to the host (which then broadcasts).</summary>
        private static void OnLocalBossDialogClosed(string key)
        {
            lock (_lock) { if (_fightCommitted.Contains(key)) return; } // fight already committed — further closes are no-ops
            if (!TryFindLocalEncounter(key, out var adapter, out var component)) return;
            try { if (!adapter.GatesFightOnDialogClose(component)) return; } catch { return; }

            // The boss may already be started even if _fightCommitted was cleared by a level-step refresh (Log134): a
            // late intro re-opens then closes the dialog on a boss whose fight already began. Don't re-commit it.
            if (SafeStarted(adapter, component))
            {
                if (LogOn) Plugin.Log.Info($"[BossFightGate] dialog close ignored — fight already started key={key}");
                return;
            }

            NetMode mode = NetGameplaySyncBridge.BossMode;
            if (mode == NetMode.Host)
            {
                Plugin.Log.Info($"[BossFightGate] host dialog dismissed → committing fight key={key}");
                CommitFightStart(key, adapter, component, "host-dialog-close");
                return;
            }
            if (mode == NetMode.Client)
            {
                bool joined = false; try { joined = NetClientJoinFlow.SessionJoinedHost; } catch { }
                if (!joined) return; // not networked: StartFight was never gated, nothing to commit
                lock (_lock) { if (!_fightCommitRequested.Add(key)) return; }
                if (!TryBuildContext(out var ctx, out _)) return;
                var msg = BuildDialogCommit(key, component, in ctx, "client-dialog-close");
                msg.IsFightCommit = true;
                Plugin.Log.Info($"[BossFightGate] client dialog dismissed → request fight commit {msg.ToCompact()}");
                NetGameplaySyncBridge.SendClientBossDialogCommitRequest(msg);
            }
        }

        /// <summary>HOST: apply the fight start locally then broadcast a FIGHT commit so every client starts together.</summary>
        private static void CommitFightStart(string key, IBossEncounterAdapter adapter, object component, string reason)
        {
            CommitFightStartLocal(key, adapter, component, reason);
            lock (_lock) { if (!_fightCommitBroadcast.Add(key)) return; }
            if (!TryBuildContext(out var ctx, out _)) return;
            var msg = BuildDialogCommit(key, component, in ctx, reason);
            msg.IsFightCommit = true;
            Plugin.Log.Info($"[BossFightGate] host broadcasting FIGHT commit {msg.ToCompact()}");
            NetGameplaySyncBridge.BroadcastHostBossDialogCommit(msg);
        }

        /// <summary>Start the gated fight on THIS end: invoke the real StartFight under the reentry guard (so the gate
        /// lets it through), close any lingering boss dialog (FF14: dialogs are removed once the fight starts), and mark
        /// the encounter committed. Idempotent per key. Never throws.</summary>
        private static bool CommitFightStartLocal(string key, IBossEncounterAdapter adapter, object component, string reason)
        {
            lock (_lock) { if (!_fightCommitted.Add(key)) return false; _dialogSessionActive.Remove(key); } // RM-2b: session ended → no more cutscene catch-up
            string startDetail = "already-started";
            BeginApply();
            try
            {
                if (!SafeStarted(adapter, component))
                {
                    // RM-2b: if this end DEFERRED the intro cutscene (out-of-room), the boss never played its appearance
                    // (Introduction), so it would stay hidden/unfightable after StartFight. Run Introduction now so the
                    // boss actually appears — it's idempotent (the boss's own introPlayed guard makes it a no-op on
                    // in-room ends), uses a deterministic pool (GetClosestPool is boss-position based, not player), and the
                    // Cinematic lock it sets is immediately released by StartFight below (net-zero — the out-of-room
                    // player isn't held; only a brief camera turn toward the boss).
                    bool deferred; lock (_lock) { deferred = CutsceneGateActive && !_cutscenePlayed.Contains(key); }
                    if (deferred)
                    {
                        // Suppress the player-facing effects so the forced appearance doesn't drag THIS (out-of-room)
                        // player's camera or lock them — only the boss's own appearance (teleport-to-pool + rise) runs.
                        SetSuppressBossCutscene(true);
                        try { BossReflect.TryInvoke(component, "Introduction", out string introDetail); Plugin.Log.Info($"[BossDialogCutscene] forced boss appearance (out-of-room) key={key}: {introDetail}"); }
                        finally { SetSuppressBossCutscene(false); }
                    }
                    BossReflect.TryInvoke(component, "StartFight", out startDetail);
                }
            }
            finally { EndApply(); }
            // Fight committed → the boss is now vulnerable (clears the pre-fight invulnerability we held via DoneAppearing).
            SetBossInvulnerable(component, false);
            bool finalized = BossDialogReflect.TryFinalizeCurrentDialog(out string dlgDetail);
            Plugin.Log.Info($"[BossFightGate] committed fight start key={key} reason={reason} start[{startDetail}] dialogClosed={finalized}({dlgDetail})");

            // PF-ArmDefer (issue 1): the boss's intro arm was blocked during the dialog (OnLocalIntroArmSpawn). Now that
            // the fight is committed, replay it once — vanilla timing (the arm appears at fight start, not during the
            // dialog). Runs under the reentry guard so its SpawnArm passes the gate. Idempotent per key. On the host the
            // replayed arm flows through the RT3-A pipeline + broadcasts; on the client the replayed arm binds to it.
            if (DeferIntroArmActive)
            {
                bool defers = false; try { defers = adapter.DefersIntroArmUntilCommit(component); } catch { }
                if (defers)
                {
                    bool firstReplay; lock (_lock) { firstReplay = _introArmReplayed.Add(key); }
                    if (firstReplay)
                    {
                        BeginApply();
                        bool armed; string armDetail;
                        try { armed = adapter.TryReplayIntroArm(component, out armDetail); }
                        catch (Exception ex) { armed = false; armDetail = $"ex {ex.GetType().Name}: {ex.Message}"; }
                        finally { EndApply(); }
                        Plugin.Log.Info($"[BossArmDefer] replayed intro arm at commit key={key} ok={armed} ({armDetail})");
                    }
                }
            }
            return true;
        }

        // PF-ArmDefer: encounters whose deferred intro arm we have already replayed at commit. Prevents a double replay
        // and lets the gate block a late-client's trailing behavior-tree intro arm (the duplicate of our replay).
        private static readonly HashSet<string> _introArmReplayed = new HashSet<string>();

        private static void MarkCousinFightLoopStarted(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (_lock) { _cousinFightLoopStarted.Add(key); }
        }

        /// <summary>PF-ArmDefer (issue 1) PREFIX for a deferred-intro-arm boss's arm spawn (Cousin.SpawnArm). Returns
        /// false to BLOCK the behavior-tree INTRO arm (which co-op's no-pause lets fire DURING the dialog); the real arm
        /// is replayed at the dialog-close fight commit (CommitFightStartLocal → TryReplayIntroArm). Mid-fight Reappear
        /// arms (after the fight loop has begun) and our own commit replay (under the reentry guard) run normally.</summary>
        public static bool OnLocalIntroArmSpawn(object component)
        {
            try
            {
                if (!Enabled || component == null || !DeferIntroArmActive) return true;
                if (InReentry) return true; // our commit replay (or any host-applied step) — allow

                var adapter = ResolveAdapter(component);
                if (adapter == null) return true;
                bool defers = false; try { defers = adapter.DefersIntroArmUntilCommit(component); } catch { }
                if (!defers) return true;

                NetMode mode = NetGameplaySyncBridge.BossMode;
                bool joined = false; try { joined = NetClientJoinFlow.SessionJoinedHost; } catch { }
                if (mode != NetMode.Host && !(mode == NetMode.Client && joined)) return true; // not networked

                if (!TryGetEncounterKeyForBoss(component, out string key, out _)) return true;

                bool loopStarted, terminal;
                lock (_lock) { loopStarted = _cousinFightLoopStarted.Contains(key); terminal = _terminalDead.Contains(key); }
                // Reappear arm (fight loop running) or a dying boss — let the native arm run.
                if (loopStarted || terminal) return true;

                // Pre-fight INTRO arm: defer it. Blocked here, replayed at the dialog-close fight commit.
                if (LogOn) Plugin.Log.Info($"[BossArmDefer] blocked intro arm (deferred to fight commit) key={key} mode={mode}");
                return false;
            }
            catch (Exception ex) { Plugin.Log.Warn($"[BossArmDefer] OnLocalIntroArmSpawn failed: {ex.GetType().Name}: {ex.Message}"); return true; }
        }

        // ================================================================== RM (room-membership substrate)

        /// <summary>Phase RM: THIS end's local player crossed into the boss room. Host: record itself + broadcast. Client:
        /// report to the host (which aggregates the authoritative set). Once per encounter per end. Never throws.</summary>
        private static void ReportLocalRoomEntry(string key, in BossEncounterContext ctx, string source, NetMode mode)
        {
            try
            {
                lock (_lock) { if (!_roomEnterReported.Add(key)) return; }
                TryCatchUpCutscene(key); // RM-2b: late entrant walked in while the dialog session is active → catch up the cutscene
                if (mode == NetMode.Host)
                {
                    MarkInRoom(key, HostPlayerId, source);
                }
                else // client joined
                {
                    var msg = new NetClientRoomEnter
                    {
                        EncounterKey = key, ChapterName = ctx.ChapterName, LevelIndex = ctx.LevelIndex,
                        HasSeed = ctx.HasSeed, Seed = ctx.Seed, EntrySource = source, Timestamp = Time.realtimeSinceStartup,
                    };
                    Plugin.Log.Info($"[RoomMembership] client reporting local room entry {msg.ToCompact()}");
                    NetGameplaySyncBridge.SendClientRoomEnter(msg);
                }
            }
            catch (Exception ex) { Plugin.Log.Warn($"[RoomMembership] ReportLocalRoomEntry failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        public static void HandleClientRoomEnter(NetClientRoomEnter msg, string peerId)
        {
            try
            {
                if (!Enabled || !RoomMembershipActive || msg == null || NetGameplaySyncBridge.BossMode != NetMode.Host) return;
                if (!TryBuildContext(out var ctx, out _) || !string.Equals(ctx.ChapterName, msg.ChapterName, StringComparison.Ordinal)
                    || ctx.LevelIndex != msg.LevelIndex || (ctx.HasSeed && msg.HasSeed && ctx.Seed != msg.Seed))
                {
                    if (LogOn) Plugin.Log.Warn($"[RoomMembership] host reject room-enter from {peerId}: run mismatch req={msg.ChapterName}:{msg.LevelIndex} host={ctx.ChapterName}:{ctx.LevelIndex}");
                    return;
                }
                MarkInRoom(msg.EncounterKey, peerId, "client:" + (msg.EntrySource ?? ""));
            }
            catch (Exception ex) { Plugin.Log.Warn($"[RoomMembership] HandleClientRoomEnter failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        public static void HandleHostRoomMembership(NetHostRoomMembership msg)
        {
            try
            {
                if (!Enabled || !RoomMembershipActive || msg == null || NetGameplaySyncBridge.BossMode != NetMode.Client) return;
                lock (_lock) { _roomMembersClientView[msg.EncounterKey] = new HashSet<string>(msg.PlayerIds ?? System.Array.Empty<string>()); }
                if (LogOn) Plugin.Log.Info($"[RoomMembership] client received {msg.ToCompact()}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[RoomMembership] HandleHostRoomMembership failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>HOST: add a player to a boss room's in-room set; on change, log + broadcast the new set.</summary>
        private static void MarkInRoom(string key, string playerId, string source)
        {
            bool changed; string members;
            lock (_lock)
            {
                if (!_roomMembers.TryGetValue(key, out var set)) { set = new HashSet<string>(); _roomMembers[key] = set; }
                changed = set.Add(playerId);
                members = string.Join(",", set);
            }
            if (!changed) return;
            Plugin.Log.Info($"[RoomMembership] host in-room += {playerId} (src={source}) key={key} members=[{members}]");
            BroadcastRoomMembership(key);
        }

        private static void BroadcastRoomMembership(string key)
        {
            if (NetGameplaySyncBridge.BossMode != NetMode.Host) return;
            if (!TryBuildContext(out var ctx, out _)) return;
            string[] ids; lock (_lock) { ids = _roomMembers.TryGetValue(key, out var set) ? set.ToArray() : System.Array.Empty<string>(); }
            var msg = new NetHostRoomMembership
            {
                EncounterKey = key, ChapterName = ctx.ChapterName, LevelIndex = ctx.LevelIndex,
                HasSeed = ctx.HasSeed, Seed = ctx.Seed, PlayerIds = ids, Timestamp = Time.realtimeSinceStartup,
            };
            NetGameplaySyncBridge.BroadcastHostRoomMembership(msg);
        }

        /// <summary>Phase RM API (for future consumers — dialog cutscene scoping, arena lockdown): is the given player id
        /// in-room for this encounter? Host reads its authoritative set; a client reads the last host-broadcast set.</summary>
        public static bool IsPlayerInRoom(string key, string playerId)
        {
            lock (_lock)
            {
                var src = NetGameplaySyncBridge.BossMode == NetMode.Host ? _roomMembers : _roomMembersClientView;
                return src.TryGetValue(key, out var set) && set.Contains(playerId);
            }
        }

        /// <summary>Phase RM API: snapshot of the in-room player ids for this encounter (host authoritative / client view).</summary>
        public static string[] GetRoomMembers(string key)
        {
            lock (_lock)
            {
                var src = NetGameplaySyncBridge.BossMode == NetMode.Host ? _roomMembers : _roomMembersClientView;
                return src.TryGetValue(key, out var set) ? set.ToArray() : System.Array.Empty<string>();
            }
        }

        public static void HandleHostBossDialogCommit(NetBossDialogCommit msg)
        {
            try
            {
                if (!Enabled || msg == null || NetGameplaySyncBridge.BossMode != NetMode.Client) return;
                if (LogOn) Plugin.Log.Info($"[BossDialogCommit] client received {msg.ToCompact()}");

                // Phase PF (Plan B): host FIGHT commit (an in-room player dismissed the dialog) → start the gated fight
                // locally + close any lingering boss dialog. Handled before the INTRO dedup below (same encounter key).
                if (msg.IsFightCommit)
                {
                    if (!TryFindLocalEncounter(msg.EncounterKey, out var fAdapter, out var fComp))
                    {
                        BossEncounterNotFound++;
                        Plugin.Log.Warn($"[BossFightGate] client has no encounter for host fight-commit key={msg.EncounterKey}; candidates: {DescribeCandidates()}");
                        return;
                    }
                    Plugin.Log.Info($"[BossFightGate] client received host fight-commit {msg.ToCompact()}");
                    CommitFightStartLocal(msg.EncounterKey, fAdapter, fComp, "host-fight-commit");
                    return;
                }

                lock (_lock) { if (!_dialogCommitApplied.Add(msg.EncounterKey)) { if (LogOn) Plugin.Log.Info($"[BossDialogCommit] already applied key={msg.EncounterKey}"); return; } }

                if (!TryFindLocalEncounter(msg.EncounterKey, out var adapter, out var component))
                {
                    BossEncounterNotFound++;
                    Plugin.Log.Warn($"[BossDialogCommit] client has no local encounter for key={msg.EncounterKey}; candidates: {DescribeCandidates()}");
                    return;
                }

                // Open a continuation window so the StartFight invoked by the commit (and any chain step) is allowed.
                OpenContinuation(msg.EncounterKey, adapter, component, "HostBossDialogCommit:" + msg.CommitSource);
                lock (_lock) { _appliedStart.Add(msg.EncounterKey); }

                BeginApply();
                bool ok; string detail;
                try { ok = ApplyIntroCutsceneGated(msg.EncounterKey, adapter, component, msg, out detail); }
                finally { EndApply(); }

                if (ok)
                {
                    BossDialogCommitApplied++;
                    Plugin.Log.Info($"[BossDialogCommit] applied + dialog finalized key={msg.EncounterKey}: {detail} after[{SafeDescribe(adapter, component)}]");
                    try { adapter.OnClientPresentationStart(component); } catch { } // F2: enter combat presentation
                }
                else
                {
                    Plugin.Log.Warn($"[BossDialogCommit] client failed to apply commit key={msg.EncounterKey}: {detail}");
                }

                // Phase PF FAITHFUL INTRO: when on, the client is about to play the boss's REAL intro dialog via its
                // native behavior-tree sequence — so we must NOT remove the dialog interactable or finalize/close the
                // dialog here (that would kill the very dialog we want to show). Removal-after-fight is handled later.
                bool faithful = false; try { faithful = Plugin.Cfg.EnableFaithfulBossIntro.Value; } catch { }
                if (!faithful)
                {
                    // Fix A (root): remove the boss dialog interactable on the client so its local player can never
                    // re-open the boss dialog after the host committed the fight.
                    RemoveDialogInteractableOnce(msg.EncounterKey, adapter, component, "client-commit");

                    // The client's local boss dialog often opens a beat AFTER the commit arrives. Register a short
                    // deferred finalize that Stops the dialog as soon as it appears, so the choice menu can't linger.
                    lock (_lock) { _pendingDialogFinalize.Add(new PendingDialogFinalize { Key = msg.EncounterKey, Until = Time.realtimeSinceStartup + DialogFinalizeWindowSeconds }); }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[BossDialogCommit] HandleHostBossDialogCommit failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ================================================================== E3: boss phase/state (Witch)

        /// <summary>Called by the host's WitchBossController.ChangePhase postfix to broadcast the new phase/state.</summary>
        public static void OnHostBossPhaseChanged(object component)
        {
            try
            {
                if (!Enabled || component == null || NetGameplaySyncBridge.BossMode != NetMode.Host) return;
                if (InReentry) return; // this ChangePhase was itself host-applied — don't echo
                var adapter = ResolveAdapter(component);
                if (adapter == null || !adapter.ProvidesPhaseState) return;
                if (!TryBuildContext(out var ctx, out var runScope)) return;
                OnLevelChanged(runScope);
                var id = adapter.BuildEncounterId(component, in ctx);
                Register(id.Key, adapter, component);
                BroadcastBossStateFor(id.Key, adapter, component, in ctx, verbose: true);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[BossState] OnHostBossPhaseChanged failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>True if the adapter participates in host-authoritative BossState sync (phase and/or health).</summary>
        private static bool ParticipatesInStateSync(IBossEncounterAdapter adapter, object component)
        {
            if (adapter.ProvidesPhaseState) return true;
            try { return adapter.GetHealthUnit(component) != null; } catch { return false; }
        }

        private static void BroadcastBossStateFor(string key, IBossEncounterAdapter adapter, object component, in BossEncounterContext ctx, bool verbose)
        {
            var state = new NetBossState
            {
                EncounterKey = key,
                BossType = component.GetType().Name,
                ChapterName = ctx.ChapterName,
                LevelIndex = ctx.LevelIndex,
                HasSeed = ctx.HasSeed,
                Seed = ctx.Seed,
                Revision = ++_bossStateRevision,
                Timestamp = Time.realtimeSinceStartup,
            };
            adapter.FillBossState(component, state);
            BossStateBroadcast++;
            if (LogOn && verbose)
            {
                string manifest = adapter is WitchBossControllerAdapter wa ? " | " + wa.DescribeAddManifest(component) : "";
                Plugin.Log.Info($"[BossState] host broadcasting {state.ToCompact()}{manifest}");
            }
            NetGameplaySyncBridge.BroadcastHostBossState(state);
        }

        // Phase 5.4-E4.2: periodic host health/state broadcast for ALL active bosses (not just Witch ChangePhase).
        // This is what gives the Client a synced boss health bar + death. Throttled; only started bosses.
        private static float _nextBossStateBroadcast;
        private static float _nextBossStateClientLog;
        private static int _lastLoggedClientPhase = -999;
        private static void TickHostBossStateBroadcast()
        {
            if (NetGameplaySyncBridge.BossMode != NetMode.Host) return;
            float now = Time.realtimeSinceStartup;
            if (now < _nextBossStateBroadcast) return;
            _nextBossStateBroadcast = now + BossStateBroadcastInterval;
            if (!TryBuildContext(out var ctx, out _)) return;

            List<KeyValuePair<string, Entry>> snapshot;
            lock (_lock) { snapshot = _registry.ToList(); }
            foreach (var kv in snapshot)
            {
                var e = kv.Value;
                if (!(e.Component is UnityEngine.Object uo) || uo == null) continue;
                try
                {
                    lock (_lock) { if (_terminalDead.Contains(kv.Key)) continue; } // dead — stop pushing state
                    if (!ParticipatesInStateSync(e.Adapter, e.Component)) continue;
                    if (!SafeStarted(e.Adapter, e.Component)) continue; // only sync once the fight is live
                    // Attach the boss bar on the HOST too (Log124/125 regression): for Cousin the vanilla bar attach is
                    // an intro-animation event, which used to fire because the host's physical trigger re-ran the intro.
                    // The host re-entry guard now blocks that re-run, so attach explicitly here, once, mirroring the
                    // client path (TryAttachBossBar = AttachToBossUI; the once-guard prevents the onHealthChange leak).
                    bool firstAttach; lock (_lock) { firstAttach = _bossUiAttached.Add(kv.Key); }
                    if (firstAttach && e.Adapter.TryAttachBossBar(e.Component) && LogOn)
                        Plugin.Log.Info($"[BossState] host attached boss bar key={kv.Key}");
                    BroadcastBossStateFor(kv.Key, e.Adapter, e.Component, in ctx, verbose: false);
                }
                catch { }
            }
        }

        public static void HandleHostBossState(NetBossState state)
        {
            try
            {
                if (!Enabled || state == null || NetGameplaySyncBridge.BossMode != NetMode.Client) return;
                if (LogOn) Plugin.Log.Info($"[BossState] client received {state.ToCompact()}");

                if (!TryFindLocalEncounter(state.EncounterKey, out var adapter, out var component))
                {
                    BossEncounterNotFound++;
                    Plugin.Log.Warn($"[BossState] client has no local encounter for key={state.EncounterKey}; candidates: {DescribeCandidates()}");
                    return;
                }
                if (!ParticipatesInStateSync(adapter, component)) return; // health-only bosses participate too

                // Attach the boss bar ONCE (Attach re-subscribes onHealthChange each call). This is what makes the
                // Client actually show a boss health bar ("客户端没出血条"). Then apply writes HP + fires the bar event.
                bool firstForKey;
                lock (_lock) { firstForKey = _bossUiAttached.Add(state.EncounterKey); }
                if (firstForKey && adapter.TryAttachBossBar(component) && LogOn)
                    Plugin.Log.Info($"[BossState] client attached boss bar key={state.EncounterKey}");

                BeginApply();
                bool ok; string detail;
                try { ok = adapter.TryApplyBossState(component, state, out detail); }
                finally { EndApply(); }

                if (ok)
                {
                    BossStateApplied++;
                    // Throttle the (0.4s) health stream to a readable cadence, but always log a phase change.
                    bool phaseChanged = state.PhaseIndex != _lastLoggedClientPhase;
                    _lastLoggedClientPhase = state.PhaseIndex;
                    float now = Time.realtimeSinceStartup;
                    if (LogOn && (phaseChanged || now >= _nextBossStateClientLog))
                    {
                        _nextBossStateClientLog = now + 1.5f;
                        Plugin.Log.Info($"[BossState] client applied key={state.EncounterKey} hp={(state.HasHealth ? $"{state.CurrentHealth:0}/{state.MaxHealth:0}" : "?")} phase={state.PhaseIndex}: {detail}");
                    }
                }
                else
                {
                    Plugin.Log.Warn($"[BossState] client failed to apply state key={state.EncounterKey}: {detail}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[BossState] HandleHostBossState failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ================================================================== F: BossDamageAuthority

        /// <summary>CLIENT: called from the Npc.ReceiveDamage prefix when the normal puppet/roster hit path did NOT
        /// claim this hit. If the hit Unit is a registered boss's target (main body this phase), route the hit to the
        /// Host through the real boss damage pipeline and suppress local damage (return true). Never throws.</summary>
        public static bool TryClientBossHit(object hitUnit, float damage, int damageTypeInt)
        {
            try
            {
                if (!Enabled || hitUnit == null || damage <= 0f) return false;
                try { if (!Plugin.Cfg.EnableBossDamageAuthority.Value) return false; } catch { return false; }
                if (NetGameplaySyncBridge.BossMode != NetMode.Client) return false;
                bool joined = false; try { joined = NetClientJoinFlow.SessionJoinedHost; } catch { }
                if (!joined) return false;

                // Find which registered boss this hit belongs to (by target role).
                IBossEncounterAdapter? adapter = null; object? component = null; string? role = null; string key = "";
                lock (_lock)
                {
                    foreach (var kv in _registry)
                    {
                        var e = kv.Value;
                        if (!(e.Component is UnityEngine.Object uo) || uo == null) continue;
                        string? r = null;
                        try { r = e.Adapter.ResolveHitTargetRole(e.Component, hitUnit); } catch { }
                        if (r != null) { adapter = e.Adapter; component = e.Component; role = r; key = kv.Key; break; }
                    }
                }
                if (adapter == null || component == null || role == null) return false;
                lock (_lock) { if (_terminalDead.Contains(key)) return true; } // boss is dead — suppress local damage, don't request

                if (!TryGetRunContext(out string chap, out int lvl, out bool hasSeed, out int seed)) return false;
                var req = new NetClientBossHitRequest
                {
                    EncounterKey = key, BossType = component.GetType().Name, RootName = BossReflect.RootName(component),
                    ChapterName = chap, LevelIndex = lvl, HasSeed = hasSeed, Seed = seed,
                    TargetRole = role, Damage = damage, DamageTypeInt = damageTypeInt,
                    RequestSeq = ++_bossHitSeq, SentAt = Time.realtimeSinceStartup,
                };
                BossHitClientSent++;
                if (LogOn) Plugin.Log.Info($"[BossDamage] client hit local target={BossReflect.RootName(hitUnit)} -> route to host (reusedRoster=false) {req.ToCompact()}");
                NetGameplaySyncBridge.SendClientBossHitRequest(req);
                return true; // suppress local damage; host owns the result
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[BossDamage] TryClientBossHit failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>HOST: resolve the boss target role to the real Unit and apply the client's damage through the
        /// vanilla Unit.ReceiveDamage so the boss's native mechanic (onDamageRecieved) advances.</summary>
        public static void HandleClientBossHitRequest(NetClientBossHitRequest req, string peerId)
        {
            try
            {
                if (!Enabled || req == null || NetGameplaySyncBridge.BossMode != NetMode.Host) return;
                try { if (!Plugin.Cfg.EnableBossDamageAuthority.Value) return; } catch { return; }
                BossHitHostRecv++;

                if (!TryBuildContext(out var ctx, out _) || !string.Equals(ctx.ChapterName, req.ChapterName, StringComparison.Ordinal)
                    || ctx.LevelIndex != req.LevelIndex || (ctx.HasSeed && req.HasSeed && ctx.Seed != req.Seed))
                {
                    BossHitHostRejected++;
                    Plugin.Log.Warn($"[BossDamage] host REJECT hit from {peerId}: run mismatch req={req.ChapterName}:{req.LevelIndex} host={ctx.ChapterName}:{ctx.LevelIndex}");
                    return;
                }
                if (!TryFindLocalEncounter(req.EncounterKey, out var adapter, out var component))
                {
                    BossHitHostRejected++;
                    Plugin.Log.Warn($"[BossDamage] host REJECT hit from {peerId}: no encounter key={req.EncounterKey} (missingEncounter)");
                    return;
                }
                object? target = null;
                try { target = adapter.ResolveHostTargetForRole(component, req.TargetRole); } catch { }
                if (target == null)
                {
                    BossHitHostRejected++;
                    // Include the Host's current state so a 'wrongRole' can be told apart from phase desync (Witch: the
                    // role names a phase whose witch the Host hasn't entered yet / already left) vs a real bad role.
                    Plugin.Log.Warn($"[BossDamage] host REJECT hit from {peerId}: role '{req.TargetRole}' resolved no target (wrongRole) key={req.EncounterKey} hostState[{SafeDescribe(adapter, component)}]");
                    return;
                }

                NetGameplayProbeManager.TryReadBossUnitHealth(target, out float before, out float maxHp);
                // The real IDamager source = GameManager.Instance.PlayerUnit (a Unit : IDamager). NOT the Player/PlayerScript.
                object? source = BossDamageReflect.ResolveHostPlayerUnit();
                string srcType = source?.GetType().FullName ?? "null";
                string targetUnitId = BossReflect.ReadUnitId(target);

                // The MAIN health unit may differ from the hit target (Witch: phase witchUnit forwards to witchMainUnit
                // via OnDamageMainWitch). Read it before/after so the main health drop is observable even when the hit
                // target is a forwarding phase witch. For Lucia/Cousin the health unit == target, so this is identical.
                object? healthUnit = null;
                try { healthUnit = adapter.GetHealthUnit(component); } catch { }
                bool sepHealth = healthUnit != null && !ReferenceEquals(healthUnit, target);
                float mainBefore = 0f, mainMax = 0f;
                if (sepHealth) NetGameplayProbeManager.TryReadBossUnitHealth(healthUnit, out mainBefore, out mainMax);

                bool ok = BossDamageReflect.TryApplyRealDamage(target, req.Damage, req.DamageTypeInt, source, out bool vanilla, out string detail);
                NetGameplayProbeManager.TryReadBossUnitHealth(target, out float after, out _);
                string mainHp = "";
                if (sepHealth)
                {
                    NetGameplayProbeManager.TryReadBossUnitHealth(healthUnit, out float mainAfter, out _);
                    mainHp = $" mainHp={mainBefore:0}->{mainAfter:0}/{mainMax:0}";
                }

                string diag = $"source=[{srcType} isIDamager={BossDamageReflect.IsValidDamageSource(source)}] target=[{target.GetType().Name} unitId={(string.IsNullOrEmpty(targetUnitId) ? "?" : targetUnitId)}]{mainHp} overload={BossDamageReflect.ReceiveDamageSignature}";
                if (ok && vanilla)
                {
                    BossHitHostApplied++;
                    Plugin.Log.Info($"[BossDamage] host APPLY hit from {peerId} key={req.EncounterKey} role={req.TargetRole} dmg={req.Damage:0.0} hp={before:0}->{after:0}/{maxHp:0} result=true {diag}");
                    // F2 feedback: tell clients to play the local hit visual (throttled; visual only).
                    bool sendVisual;
                    lock (_lock)
                    {
                        _lastHitVisualAt.TryGetValue(req.EncounterKey, out float last);
                        sendVisual = (Time.realtimeSinceStartup - last) >= HitVisualMinInterval;
                        if (sendVisual) _lastHitVisualAt[req.EncounterKey] = Time.realtimeSinceStartup;
                    }
                    if (sendVisual)
                    {
                        BossHitVisualSent++;
                        NetGameplaySyncBridge.BroadcastHostBossHitVisual(new NetHostBossHitVisual
                        {
                            EncounterKey = req.EncounterKey, BossType = req.BossType, TargetRole = req.TargetRole,
                            TargetUnitId = targetUnitId, Seq = ++_bossHitVisualSeq,
                        });
                    }
                }
                else
                {
                    BossHitHostRejected++;
                    // vanilla==false means invulnerable/parried (correct vanilla behavior); ok==false is a reflect/source problem.
                    string reason = ok ? "vanilla-false(invuln/hitbox-invuln/parry)" : "apply-failed";
                    Plugin.Log.Warn($"[BossDamage] host hit NOT applied from {peerId} key={req.EncounterKey} role={req.TargetRole} result=false reason={reason} detail={detail} hp={before:0}/{maxHp:0} {diag}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[BossDamage] HandleClientBossHitRequest failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ================================================================== F4: fixed-point discrete events (Cousin pools)

        public static int BossDiscreteSent;
        public static int BossDiscreteApplied;
        public static int BossDiscreteClientSelfFired;
        public static int BossDeathBroadcast;
        public static int BossDeathApplied;
        private static int _bossDiscreteSeq;
        // F4 death: encounters whose boss has died (terminal). Suppresses further hits + state broadcast. Per-run.
        private static readonly HashSet<string> _terminalDead = new HashSet<string>();
        // PF-ArmDefer: encounters whose fight loop has begun (a Submerge/Reappear/MoveToNewPool fired). After this, a
        // Cousin SpawnArm is a mid-fight Reappear arm and must NOT be deferred — only the pre-fight INTRO arm is.
        private static readonly HashSet<string> _cousinFightLoopStarted = new HashSet<string>();

        /// <summary>PREFIX for fixed-point discrete methods. Returns false to BLOCK the Client's own behaviour-tree
        /// call (LogOutput30 proved the client self-drives Submerge/MoveToNewPool/Reappear with its OWN random pool).
        /// The Host runs normally; the Client's mirror runs under the reentry guard (allowed).</summary>
        public static bool OnLocalBossDiscreteEvent_Pre(object component, string eventName)
        {
            try
            {
                if (!Enabled || component == null) return true;
                if (NetGameplaySyncBridge.BossMode != NetMode.Client) return true; // host runs normally
                if (InReentry) return true; // our own host-driven mirror — allow
                bool block = true; try { block = Plugin.Cfg.EnableBossDiscreteEventAuthority.Value; } catch { }
                if (!block) return true;
                BossDiscreteClientSelfFired++;
                if (LogOn) Plugin.Log.Info($"[CousinPool] client BLOCKED local '{eventName}' (host-authoritative; client must not pick its own pool)");
                return false; // block the client's independent pool decision
            }
            catch { return true; }
        }

        /// <summary>Called by the discrete-event postfix patch. HOST: read + broadcast the event so the Client mirrors
        /// the same pool/dig.</summary>
        public static void OnLocalBossDiscreteEvent(object component, string eventName)
        {
            try
            {
                if (!Enabled || component == null) return;
                var adapter = ResolveAdapter(component);
                if (adapter == null) return;
                NetMode mode = NetGameplaySyncBridge.BossMode;

                if (mode == NetMode.Host)
                {
                    if (!TryGetEncounterKeyForBoss(component, out string key, out string bossType)) return;
                    // PF-ArmDefer: the fight loop has begun on this encounter — subsequent SpawnArm calls are mid-fight
                    // Reappear arms, not the deferred intro arm. (Terminal "CousinDeath" is not a loop step.)
                    if (!adapter.IsTerminalEvent(eventName)) MarkCousinFightLoopStarted(key);
                    if (!TryGetRunContext(out string chap, out int lvl, out bool hasSeed, out int seed)) { chap = ""; lvl = -1; }
                    adapter.BuildDiscreteEvent(component, eventName, out bool hasPos, out Vector3 pos, out string diag);
                    if (LogOn && !string.IsNullOrEmpty(diag)) Plugin.Log.Info(diag);
                    var msg = new NetBossDiscreteEvent
                    {
                        EncounterKey = key, BossType = bossType, EventName = eventName, HasPos = hasPos, Position = pos,
                        ChapterName = chap, LevelIndex = lvl, HasSeed = hasSeed, Seed = seed, Seq = ++_bossDiscreteSeq,
                    };
                    BossDiscreteSent++;
                    if (LogOn) Plugin.Log.Info($"[CousinPool] host broadcasting discrete {msg.ToCompact()}");
                    NetGameplaySyncBridge.BroadcastHostBossDiscreteEvent(msg);
                }
                // Client: the prefix blocks self-fires; any postfix here is our own reentry mirror — nothing to do.
            }
            catch (Exception ex) { Plugin.Log.Warn($"[CousinPool] OnLocalBossDiscreteEvent failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        public static void HandleHostBossDiscreteEvent(NetBossDiscreteEvent msg)
        {
            try
            {
                if (!Enabled || msg == null || NetGameplaySyncBridge.BossMode != NetMode.Client) return;
                if (!TryFindLocalEncounter(msg.EncounterKey, out var adapter, out var component))
                {
                    if (LogOn) Plugin.Log.Info($"[CousinPool] client has no local encounter for {msg.ToCompact()}");
                    return;
                }
                bool terminal = false; try { terminal = adapter.IsTerminalEvent(msg.EventName); } catch { }
                // PF-ArmDefer: client mirrors the host's fight-loop step → arms after this are mid-fight Reappear arms.
                if (!terminal) MarkCousinFightLoopStarted(msg.EncounterKey);
                if (terminal)
                {
                    bool first; lock (_lock) { first = _terminalDead.Add(msg.EncounterKey); }
                    if (!first) { if (LogOn) Plugin.Log.Info($"[CousinDeath] client already terminal key={msg.EncounterKey}"); return; }
                    Plugin.Log.Info($"[CousinDeath] client received death event {msg.ToCompact()}; local Cousin resolved");
                }

                BeginApply();
                bool ok; string detail;
                try { ok = adapter.TryApplyDiscreteEvent(component, msg.EventName, msg.HasPos, msg.Position, out detail); }
                finally { EndApply(); }
                if (ok) BossDiscreteApplied++;
                if (terminal)
                {
                    if (ok) BossDeathApplied++;
                    Plugin.Log.Info($"[CousinDeath] applied local death key={msg.EncounterKey} ok={ok}: {detail}; further BossDamage suppressed");
                }
                else if (LogOn) Plugin.Log.Info($"[CousinPool] client applied discrete {msg.ToCompact()} ok={ok}: {detail}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[CousinPool] HandleHostBossDiscreteEvent failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>HOST: the boss's real terminal death method fired (Cousin: CousinDeath, from owner.onDeath). Mark
        /// the encounter terminal and broadcast a death event so the Client runs its real local death + stops hitting.</summary>
        public static void OnHostBossDeath(object component, string eventName)
        {
            try
            {
                if (!Enabled || component == null || NetGameplaySyncBridge.BossMode != NetMode.Host) return;
                try { if (!Plugin.Cfg.EnableBossDiscreteEventAuthority.Value) return; } catch { return; }
                if (InReentry) return; // our own apply (host doesn't apply death, but be safe)
                if (!TryGetEncounterKeyForBoss(component, out string key, out string bossType)) return;
                lock (_lock) { if (!_terminalDead.Add(key)) return; } // dedup
                if (!TryGetRunContext(out string chap, out int lvl, out bool hasSeed, out int seed)) { chap = ""; lvl = -1; }
                BossDeathBroadcast++;
                Plugin.Log.Info($"[CousinDeath] host detected {eventName}; broadcast encounter death key={key}");
                NetGameplaySyncBridge.BroadcastHostBossDiscreteEvent(new NetBossDiscreteEvent
                {
                    EncounterKey = key, BossType = bossType, EventName = eventName, HasPos = false,
                    ChapterName = chap, LevelIndex = lvl, HasSeed = hasSeed, Seed = seed, Seq = ++_bossDiscreteSeq,
                });
            }
            catch (Exception ex) { Plugin.Log.Warn($"[CousinDeath] OnHostBossDeath failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>CLIENT: the Host accepted a boss hit it routed — play the local hit visual on the matching boss
        /// target. Visual ONLY: never re-applies damage, never advances local mechanics.</summary>
        public static void HandleHostBossHitVisual(NetHostBossHitVisual msg)
        {
            try
            {
                if (!Enabled || msg == null || NetGameplaySyncBridge.BossMode != NetMode.Client) return;
                if (!TryFindLocalEncounter(msg.EncounterKey, out var adapter, out var component)) return;
                object? target = null;
                try { target = adapter.ResolveHostTargetForRole(component, msg.TargetRole); } catch { }
                if (target == null) return;
                bool played = NetGameplayProbeManager.TryPlayBossHitVisual(target);
                if (played) BossHitVisualPlayed++;
                if (LogOn) Plugin.Log.Info($"[BossDamage] client hit-visual {msg.ToCompact()} localTarget={BossReflect.RootName(target)} played={played} (visual-only, no local damage)");
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[BossDamage] HandleHostBossHitVisual failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ================================================================== F5: Lucia eye defeat authority

        private static bool LuciaEyeEnabled
        {
            get { try { return Plugin.Cfg.EnableLuciaEyeAuthority.Value; } catch { return false; } }
        }

        /// <summary>CLIENT (prefix of LuciaBossFightHelper.EyeDied): a local Lucia eye just died. Report it to the Host
        /// and BLOCK the local EyeDied so the Client never drives its own RestartPhases / body unlock — the Host owns
        /// the eye cycle. Returns true to run the original (Host / single-player / our own reentry mirror), false to
        /// block (joined Client). Never throws.</summary>
        public static bool OnLocalLuciaEyeDied(object component, object eyeUnit)
        {
            try
            {
                if (!Enabled || component == null || !LuciaEyeEnabled) return true;
                if (InReentry) return true;                 // our own host consume mirror — let vanilla EyeDied run
                var adapter = ResolveAdapter(component);
                if (adapter == null || !adapter.IsEyeBoss) return true;

                NetMode mode = NetGameplaySyncBridge.BossMode;
                if (mode == NetMode.Host) return true;       // host runs the real EyeDied (postfix broadcasts remaining)

                bool joined = false; try { joined = NetClientJoinFlow.SessionJoinedHost; } catch { }
                if (mode != NetMode.Client || !joined) return true; // single-player / not joined — keep own play

                if (!TryBuildContext(out var ctx, out var runScope)) return true;
                OnLevelChanged(runScope);
                var id = adapter.BuildEncounterId(component, in ctx);
                string key = id.Key;
                Register(key, adapter, component);

                adapter.TryReadEyePhase(component, out int cycle, out _);
                // Keep the local list accurate (drop the dead eye) WITHOUT triggering the local RestartPhases/unlock.
                bool removed = adapter.TryRemoveDeadEyeFromList(component, eyeUnit);
                adapter.TryReadEyePhase(component, out _, out int localRemaining);

                int seq = ++_luciaEyeReportSeq;
                var req = new NetLuciaEyeReport
                {
                    EncounterKey = key, BossType = component.GetType().Name,
                    ChapterName = ctx.ChapterName, LevelIndex = ctx.LevelIndex, HasSeed = ctx.HasSeed, Seed = ctx.Seed,
                    Cycle = cycle, LocalRemaining = localRemaining, ReportSeq = seq, ClientPeerId = "",
                    SentAt = Time.realtimeSinceStartup,
                };
                LuciaEyeReportSent++;
                Plugin.Log.Info($"[LuciaEye] local eye defeated key={key} encounter={component.GetType().Name} cycle={cycle} localRemaining={localRemaining} removedFromList={removed}");
                Plugin.Log.Info($"[LuciaEye] report sent {req.ToCompact()}");
                NetGameplaySyncBridge.SendClientLuciaEyeReport(req);
                return false; // block local EyeDied → no local RestartPhases / unlock (Host-authoritative cycle)
            }
            catch (Exception ex) { Plugin.Log.Warn($"[LuciaEye] OnLocalLuciaEyeDied failed: {ex.GetType().Name}: {ex.Message}"); return true; }
        }

        /// <summary>HOST (postfix of LuciaBossFightHelper.EyeDied): a host eye actually died — whether from the host
        /// player's own hit or from consuming a client report. Broadcast the authoritative remaining eye count so all
        /// clients align. Single broadcast path for both cases. The vanilla RestartPhases (last eye) is left untouched.</summary>
        public static void OnHostLuciaEyeDied(object component)
        {
            try
            {
                if (!Enabled || component == null || !LuciaEyeEnabled) return;
                if (NetGameplaySyncBridge.BossMode != NetMode.Host) return;
                var adapter = ResolveAdapter(component);
                if (adapter == null || !adapter.IsEyeBoss) return;
                if (!TryBuildContext(out var ctx, out var runScope)) return;
                OnLevelChanged(runScope);
                var id = adapter.BuildEncounterId(component, in ctx);
                Register(id.Key, adapter, component);
                adapter.TryReadEyePhase(component, out int cycle, out int living);
                if (living == 0)
                    Plugin.Log.Info($"[LuciaEye] host cycle complete (eyes→0, cycle={cycle}); vanilla RestartPhases runs host-side key={id.Key}");
                BroadcastLuciaEyeState(id.Key, adapter, component, in ctx);
            }
            catch (Exception ex) { Plugin.Log.Warn($"[LuciaEye] OnHostLuciaEyeDied failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static void BroadcastLuciaEyeState(string key, IBossEncounterAdapter adapter, object component, in BossEncounterContext ctx)
        {
            adapter.TryReadEyePhase(component, out int cycle, out int living);
            var msg = new NetLuciaEyeState
            {
                EncounterKey = key, BossType = component.GetType().Name,
                ChapterName = ctx.ChapterName, LevelIndex = ctx.LevelIndex, HasSeed = ctx.HasSeed, Seed = ctx.Seed,
                Cycle = cycle, LivingEyes = living, Revision = ++_luciaEyeStateRevision, Timestamp = Time.realtimeSinceStartup,
            };
            LuciaEyeStateBroadcast++;
            if (LogOn) Plugin.Log.Info($"[LuciaEye] host broadcasting eye state {msg.ToCompact()}");
            NetGameplaySyncBridge.BroadcastHostLuciaEyeState(msg);
        }

        /// <summary>HOST: a client reported a local eye kill. Validate (encounter is Lucia / eye phase / living eyes /
        /// not duplicate / run match), then consume ONE living host eye through the real death path so the vanilla
        /// EyeDied → RestartPhases runs natively. The EyeDied postfix broadcasts the new remaining count.</summary>
        public static void HandleClientLuciaEyeReport(NetLuciaEyeReport req, string peerId)
        {
            try
            {
                if (!Enabled || req == null || NetGameplaySyncBridge.BossMode != NetMode.Host || !LuciaEyeEnabled) return;
                LuciaEyeReportRecv++;
                Plugin.Log.Info($"[LuciaEye] report received from {peerId}: {req.ToCompact()}");

                if (!TryBuildContext(out var ctx, out _) || !string.Equals(ctx.ChapterName, req.ChapterName, StringComparison.Ordinal)
                    || ctx.LevelIndex != req.LevelIndex || (ctx.HasSeed && req.HasSeed && ctx.Seed != req.Seed))
                {
                    LuciaEyeRejected++;
                    Plugin.Log.Warn($"[LuciaEye] rejected from {peerId}: run mismatch (req={req.ChapterName}:{req.LevelIndex} host={ctx.ChapterName}:{ctx.LevelIndex})");
                    return;
                }

                string dedup = peerId + ":" + req.ReportSeq;
                lock (_lock)
                {
                    if (!_luciaEyeConsumed.Add(dedup))
                    {
                        LuciaEyeRejected++;
                        Plugin.Log.Info($"[LuciaEye] rejected from {peerId}: duplicate report (seq={req.ReportSeq})");
                        return;
                    }
                }

                if (!TryFindLocalEncounter(req.EncounterKey, out var adapter, out var component))
                {
                    LuciaEyeRejected++;
                    Plugin.Log.Warn($"[LuciaEye] rejected from {peerId}: missing host encounter key={req.EncounterKey}; candidates: {DescribeCandidates()}");
                    return;
                }
                if (!adapter.IsEyeBoss)
                {
                    LuciaEyeRejected++;
                    Plugin.Log.Warn($"[LuciaEye] rejected from {peerId}: wrong encounter (not an eye boss) key={req.EncounterKey}");
                    return;
                }

                bool inEyePhase = adapter.TryReadEyePhase(component, out int hostCycle, out int livingBefore);
                if (!inEyePhase)
                {
                    LuciaEyeRejected++;
                    Plugin.Log.Warn($"[LuciaEye] rejected from {peerId}: not in eye phase (hostCycle={hostCycle} livingEyes={livingBefore}) key={req.EncounterKey}");
                    return;
                }
                if (livingBefore <= 0)
                {
                    LuciaEyeRejected++;
                    Plugin.Log.Warn($"[LuciaEye] rejected from {peerId}: no living host eyes (hostCycle={hostCycle}) key={req.EncounterKey}");
                    return;
                }

                Plugin.Log.Info($"[LuciaEye] accepted from {peerId} key={req.EncounterKey} reportCycle={req.Cycle} hostCycle={hostCycle} hostLivingBefore={livingBefore}");

                // Consume under the reentry guard so the eye's vanilla EyeDied (and our own EyeDied postfix broadcast)
                // run, but the consume is not re-classified as a fresh client report.
                BeginApply();
                bool consumed; string detail;
                try { consumed = adapter.TryConsumeOneEye(component, out _, out detail); }
                finally { EndApply(); }

                adapter.TryReadEyePhase(component, out _, out int livingAfter);
                if (consumed)
                {
                    LuciaEyeAccepted++;
                    Plugin.Log.Info($"[LuciaEye] consumed host eye key={req.EncounterKey} {detail} hostLivingBefore={livingBefore} hostLivingAfter={livingAfter} EyeDiedObserved={livingAfter < livingBefore} RestartPhasesObserved(lastEye)={livingAfter == 0}");
                    // Note: the EyeDied postfix (OnHostLuciaEyeDied) already broadcast the new remaining count.
                }
                else
                {
                    LuciaEyeRejected++;
                    Plugin.Log.Warn($"[LuciaEye] consume FAILED key={req.EncounterKey}: {detail} (nativeDeathFailed); hostLivingBefore={livingBefore} hostLivingAfter={livingAfter}");
                }
            }
            catch (Exception ex) { Plugin.Log.Warn($"[LuciaEye] HandleClientLuciaEyeReport failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>CLIENT: apply the Host's authoritative remaining eye count. When it reaches 0 the Host's vanilla
        /// RestartPhases has run; mirror the cycle-complete presentation once (clear residual local eyes, lift the
        /// darkness). The body's phase/health/invulnerability stays Host-driven — never unlocked locally.</summary>
        public static void HandleHostLuciaEyeState(NetLuciaEyeState msg)
        {
            try
            {
                if (!Enabled || msg == null || NetGameplaySyncBridge.BossMode != NetMode.Client || !LuciaEyeEnabled) return;
                if (!TryFindLocalEncounter(msg.EncounterKey, out var adapter, out var component))
                {
                    if (LogOn) Plugin.Log.Info($"[LuciaEye] client has no local encounter for {msg.ToCompact()}");
                    return;
                }
                if (!adapter.IsEyeBoss) return;
                LuciaEyeStateApplied++;
                Plugin.Log.Info($"[LuciaEye] host remaining applied {msg.ToCompact()}");

                if (msg.LivingEyes <= 0)
                {
                    string ck = msg.EncounterKey + ":" + msg.Cycle;
                    bool first; lock (_lock) { first = _luciaEyeCycleComplete.Add(ck); }
                    if (!first) return;

                    Plugin.Log.Info($"[LuciaEye] client cycle-complete received key={msg.EncounterKey} cycle={msg.Cycle}");
                    // Snapshot BEFORE the host-authorized RestartPhases so we can log before→after (the recovery is a
                    // ~9s coroutine, so the after-state is captured by a deferred verify, not synchronously).
                    bool hasDiag = adapter.TryReadEyeCompletionDiag(component, out int phaseB, out int restartB, out bool invulnB, out Vector3 posB);

                    BeginApply();
                    bool ok; int cleared; string detail;
                    try { ok = adapter.TryApplyEyePhaseComplete(component, out cleared, out detail); }
                    finally { EndApply(); }
                    if (ok) LuciaEyeResidualCleared += cleared;

                    Plugin.Log.Info($"[LuciaEye] client RestartPhases {(ok ? "invoked" : "FAILED")} key={msg.EncounterKey} ({detail})");
                    Plugin.Log.Info($"[LuciaEye] local residual eyes cleared count={cleared}");
                    if (hasDiag)
                    {
                        Plugin.Log.Info($"[LuciaEye] before: currentPhase={phaseB} restartCounter={restartB} invulnerable={invulnB} pos={posB:F1}");
                        // Schedule a deferred after-snapshot (RestartRoutine is ~9s: waits 7+2s then restartCounter++ → StartPhase(1)).
                        lock (_lock)
                        {
                            _pendingEyeComplete.Add(new PendingEyeComplete
                            {
                                Key = msg.EncounterKey, Cycle = msg.Cycle,
                                PhaseBefore = phaseB, RestartBefore = restartB, InvulnBefore = invulnB, PosBefore = posB,
                                DueAt = Time.realtimeSinceStartup + 11f,
                            });
                        }
                    }
                }
            }
            catch (Exception ex) { Plugin.Log.Warn($"[LuciaEye] HandleHostLuciaEyeState failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        // ================================================================== F6: Lucia terminal death authority

        public static int LuciaDeathBroadcast;
        public static int LuciaDeathApplied;
        private static int _luciaDeathRevision;

        private static bool LuciaDeathEnabled
        {
            get { try { return Plugin.Cfg.EnableLuciaDeathAuthority.Value; } catch { return false; } }
        }

        /// <summary>PREFIX of LuciaBossFightHelper.OnBossDead(Unit): isolate the host-only world results. On the HOST
        /// (and single-player) the full vanilla OnBossDead runs (loot placement, checkpoint/save, presentation). On a
        /// joined CLIENT it returns false to BLOCK the body so the Client's bossUnit.Die() does NOT duplicate loot/save
        /// — the Client's presentation is replayed separately by TryApplyLuciaDeath. Never throws.</summary>
        public static bool OnLocalLuciaBossDead_Pre(object component, object unit)
        {
            try
            {
                if (!Enabled || component == null || !LuciaDeathEnabled) return true;
                if (InReentry) return true; // our own apply path — let it run (we don't wrap the client Die in reentry)
                NetMode mode = NetGameplaySyncBridge.BossMode;
                if (mode == NetMode.Host) return true; // host is authoritative for loot/save
                bool joined = false; try { joined = NetClientJoinFlow.SessionJoinedHost; } catch { }
                if (mode != NetMode.Client || !joined) return true; // single-player / not joined — keep own play
                // Joined client: block the loot/save body. Presentation is handled in TryApplyLuciaDeath.
                if (LogOn) Plugin.Log.Info($"[LuciaDeath] client OnBossDead loot/save body BLOCKED (isolated) root={BossReflect.RootName(component)}");
                return false;
            }
            catch { return true; }
        }

        /// <summary>POSTFIX of LuciaBossFightHelper.OnBossDead (HOST, ran original): the real boss death happened.
        /// Broadcast a Lucia terminal death + mark the encounter terminal (stops host state pushes for it).</summary>
        public static void OnHostLuciaBossDead_Post(object component, object unit)
        {
            try
            {
                if (!Enabled || component == null || !LuciaDeathEnabled) return;
                if (NetGameplaySyncBridge.BossMode != NetMode.Host) return;
                var adapter = ResolveAdapter(component);
                if (adapter == null || !adapter.IsEyeBoss) return; // Lucia only
                if (!TryGetEncounterKeyForBoss(component, out string key, out string bossType)) return;
                lock (_lock) { if (!_terminalDead.Add(key)) return; } // dedup
                Plugin.Log.Info($"[LuciaDeath] host detected real death key={key}");
                if (!TryGetRunContext(out string chap, out int lvl, out bool hasSeed, out int seed)) { chap = ""; lvl = -1; }
                LuciaDeathBroadcast++;
                var msg = new NetLuciaDeath
                {
                    EncounterKey = key, BossType = bossType, ChapterName = chap, LevelIndex = lvl,
                    HasSeed = hasSeed, Seed = seed, Revision = ++_luciaDeathRevision, Timestamp = Time.realtimeSinceStartup,
                };
                Plugin.Log.Info($"[LuciaDeath] host broadcast terminal event {msg.ToCompact()}");
                NetGameplaySyncBridge.BroadcastHostLuciaDeath(msg);
            }
            catch (Exception ex) { Plugin.Log.Warn($"[LuciaDeath] OnHostLuciaBossDead_Post failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>CLIENT: the Host's Lucia died. Run a safe local death (real Unit death + boss-end presentation, with
        /// loot/save isolated by the OnBossDead prefix), clear the boss bar, and enable terminal suppression so no more
        /// hit/state is sent for this encounter.</summary>
        public static void HandleHostLuciaDeath(NetLuciaDeath msg)
        {
            try
            {
                if (!Enabled || msg == null || NetGameplaySyncBridge.BossMode != NetMode.Client || !LuciaDeathEnabled) return;
                Plugin.Log.Info($"[LuciaDeath] client received {msg.ToCompact()}");

                bool first; lock (_lock) { first = _terminalDead.Add(msg.EncounterKey); }
                if (!first) { Plugin.Log.Info($"[LuciaDeath] client already terminal key={msg.EncounterKey}"); return; }

                if (!TryFindLocalEncounter(msg.EncounterKey, out var adapter, out var component))
                {
                    Plugin.Log.Warn($"[LuciaDeath] client has no local encounter for key={msg.EncounterKey}; candidates: {DescribeCandidates()}");
                    return;
                }
                if (!adapter.IsEyeBoss) { Plugin.Log.Warn($"[LuciaDeath] client encounter not Lucia key={msg.EncounterKey}"); return; }
                Plugin.Log.Info($"[LuciaDeath] local Lucia resolved key={msg.EncounterKey} root={BossReflect.RootName(component)}");

                // NOT wrapped in the reentry guard: the client's bossUnit.Die() must trigger the OnBossDead prefix so it
                // can block the loot/save body (reentry would let the full vanilla body run and duplicate loot).
                bool ok; string detail;
                try { ok = adapter.TryApplyLuciaDeath(component, out detail); }
                catch (Exception ex) { ok = false; detail = $"exception {ex.GetType().Name}: {ex.Message}"; }

                if (ok) LuciaDeathApplied++;
                Plugin.Log.Info($"[LuciaDeath] local death applied key={msg.EncounterKey} ok={ok}: {detail}");
                Plugin.Log.Info($"[LuciaDeath] boss bar cleared key={msg.EncounterKey}");
                Plugin.Log.Info($"[LuciaDeath] terminal hit/state suppression enabled key={msg.EncounterKey}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[LuciaDeath] HandleHostLuciaDeath failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        // ================================================================== G2: Witch phase revision authority

        public static int WitchPhaseBroadcast;
        public static int WitchPhaseApplied;
        public static int WitchPhaseBlockedLocal;
        public static int WitchPhaseStaleIgnored;
        private static int _witchPhaseRevision;          // host monotonic, ++ per real transition
        private static int _lastHostWitchPhase = -1;     // host: last broadcast phase (skip no-op ChangePhase returns)
        private static readonly Dictionary<string, int> _witchAppliedRevision = new Dictionary<string, int>(); // client

        private static bool WitchPhaseAuthorityEnabled
        {
            get { try { return Plugin.Cfg.EnableWitchPhaseAuthority.Value; } catch { return false; } }
        }

        /// <summary>PREFIX of WitchBossController.ChangePhase: the Client must NOT self-advance phases (LogOutput34: the
        /// Client's own phase controllers drove ChangePhase and drifted ahead of the Host → wrongRole). Block the
        /// Client's local transitions; the Host owns transitions and the Client applies them via revision (under the
        /// reentry guard). Host / single-player / our own reentry apply run normally. Returns true to run, false to block.</summary>
        public static bool OnLocalWitchChangePhase_Pre(object component, int phaseInt)
        {
            try
            {
                if (!Enabled || component == null || !WitchPhaseAuthorityEnabled) return true;
                if (InReentry) return true; // our own host-authorized apply
                NetMode mode = NetGameplaySyncBridge.BossMode;
                if (mode == NetMode.Host) return true;
                bool joined = false; try { joined = NetClientJoinFlow.SessionJoinedHost; } catch { }
                if (mode != NetMode.Client || !joined) return true; // single-player / not joined — keep own play
                WitchPhaseBlockedLocal++;
                if (LogOn) Plugin.Log.Info($"[WitchPhase] client blocked local transition ->{phaseInt} reason=host-authority (local self-advance suppressed)");
                return false;
            }
            catch { return true; }
        }

        /// <summary>POSTFIX of WitchBossController.ChangePhase (HOST, ran original): assign a new revision for a REAL
        /// transition and broadcast it. No-op ChangePhase returns (phase==current / fight-over) are skipped via the
        /// last-phase guard.</summary>
        public static void OnHostWitchChangePhase_Post(object component, int phaseInt)
        {
            try
            {
                if (!Enabled || component == null || !WitchPhaseAuthorityEnabled) return;
                if (NetGameplaySyncBridge.BossMode != NetMode.Host) return;
                var adapter = ResolveAdapter(component) as WitchBossControllerAdapter;
                if (adapter == null) return;
                int now = adapter.GetCurrentPhase(component);
                if (now != phaseInt) return;        // ChangePhase early-returned (no real transition)
                if (now == _lastHostWitchPhase) return; // duplicate of last broadcast
                _lastHostWitchPhase = now;
                if (now != 3) OnHostWitchPhase2Ended(); // G5: left Phase2_WitchDome → stop broadcasting Phase 2 results
                if (!TryGetEncounterKeyForBoss(component, out string key, out string bossType)) return;
                if (!TryGetRunContext(out string chap, out int lvl, out bool hasSeed, out int seed)) { chap = ""; lvl = -1; }
                int rev = ++_witchPhaseRevision;
                bool fightStarted = adapter.IsStarted(component);
                Plugin.Log.Info($"[WitchPhase] host transition phase={now} revision={rev} fightStarted={fightStarted} key={key}");
                NetGameplaySyncBridge.BroadcastHostWitchPhase(new NetWitchPhase
                {
                    EncounterKey = key, BossType = bossType, ChapterName = chap, LevelIndex = lvl,
                    HasSeed = hasSeed, Seed = seed, PhaseIndex = now, PhaseRevision = rev,
                    FightStarted = fightStarted, Timestamp = Time.realtimeSinceStartup,
                });
                WitchPhaseBroadcast++;
            }
            catch (Exception ex) { Plugin.Log.Warn($"[WitchPhase] OnHostWitchChangePhase_Post failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>CLIENT: apply a Host phase transition by REVISION (not enum magnitude — Witch phases cycle). Tear down
        /// the current local phase, then enter the Host phase via the real ChangePhase under the reentry guard so its
        /// presentation (witch spawn, animations, room teleport for 5/6) runs natively.</summary>
        public static void HandleHostWitchPhase(NetWitchPhase msg)
        {
            try
            {
                if (!Enabled || msg == null || NetGameplaySyncBridge.BossMode != NetMode.Client || !WitchPhaseAuthorityEnabled) return;
                if (!TryFindLocalEncounter(msg.EncounterKey, out var adapterBase, out var component))
                {
                    if (LogOn) Plugin.Log.Info($"[WitchPhase] client has no local encounter for {msg.ToCompact()}");
                    return;
                }
                if (!(adapterBase is WitchBossControllerAdapter adapter))
                {
                    if (LogOn) Plugin.Log.Info($"[WitchPhase] client encounter not a Witch key={msg.EncounterKey}");
                    return;
                }

                int appliedRev; lock (_lock) { _witchAppliedRevision.TryGetValue(msg.EncounterKey, out appliedRev); }
                int localBefore = adapter.GetCurrentPhase(component);
                Plugin.Log.Info($"[WitchPhase] client received phase={msg.PhaseIndex} revision={msg.PhaseRevision} (localPhase={localBefore} appliedRev={appliedRev})");

                if (msg.PhaseRevision <= appliedRev)
                {
                    WitchPhaseStaleIgnored++;
                    if (LogOn) Plugin.Log.Info($"[WitchPhase] client {(msg.PhaseRevision == appliedRev ? "duplicate" : "stale")} revision ignored (msg={msg.PhaseRevision} applied={appliedRev})");
                    return;
                }

                // 1. Tear down the current local phase OUTSIDE the reentry guard so EndPhase's inner ChangePhase(next)
                //    is blocked (we don't want the client's nextPhaseAfterThis — the Host decides the target).
                adapter.EndCurrentWitchPhase(component, out string endDetail);

                // 2. Enter the Host's phase UNDER the reentry guard so the prefix lets the real ChangePhase through.
                BeginApply();
                bool ok; string changeDetail;
                try { ok = adapter.ApplyHostPhase(component, msg.PhaseIndex, out changeDetail); }
                finally { EndApply(); }

                lock (_lock) { _witchAppliedRevision[msg.EncounterKey] = msg.PhaseRevision; }
                int localAfter = adapter.GetCurrentPhase(component);
                if (ok) WitchPhaseApplied++;
                Plugin.Log.Info($"[WitchPhase] client apply local={localBefore}/rev={appliedRev} -> host={msg.PhaseIndex}/rev={msg.PhaseRevision} changePhase={ok} (teardown: {endDetail}; {changeDetail}); nowPhase={localAfter} activeWitchUnit={adapter.ActivePhaseWitchExists(component)}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[WitchPhase] HandleHostWitchPhase failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        // ================================================================== G5: Witch Phase 2 dome manifest

        public static int WitchP2ManifestBroadcast;
        public static int WitchP2ManifestApplied;
        public static int WitchP2ResultBroadcast;
        public static int WitchP2ResultApplied;
        // client: a manifest that arrived before the local Phase 2 witches were ready (apply when ready in Tick).
        private sealed class PendingP2Manifest { public NetWitchP2Manifest Msg = null!; public float Until; }
        private static PendingP2Manifest? _pendingP2Manifest;
        // ---- G5 result lifecycle (host-authoritative, per Phase 2 CYCLE — NOT the global witch phase revision) ----
        // Each Host ShowWitches starts a new Phase 2 cycle; results are gated to the active cycle + first-transition only,
        // so continued fire / damage after the real hit (or after Phase 2 ends) does not re-broadcast (LogOutput38 spam).
        private static int _witchP2Cycle;                 // host: ++ per ShowWitches; tags manifest + results
        private static bool _witchP2Active;               // host: true between ShowWitches capture and leaving Phase 2
        private static bool _witchP2RealHitConsumed;      // host: realHit broadcast once this cycle
        private static readonly HashSet<int> _witchP2DefeatedDomes = new HashSet<int>(); // host: domes broadcast this cycle
        private static int _p2ClientAppliedCycle = -1;    // client: cycle of the applied manifest (drop stale results)

        private static bool WitchP2ManifestEnabled
        {
            get { try { return Plugin.Cfg.EnableWitchPhase2Manifest.Value; } catch { return false; } }
        }

        /// <summary>Static hook for the ordinary puppet transform loop: skip a runtime object that is a Phase 2 dome
        /// witch currently controlled by the manifest (otherwise the snapshot pulls it off its dome position).</summary>
        public static bool IsWitchPhase2Suppressed(object runtimeObject)
        {
            try
            {
                if (!Enabled || runtimeObject == null) return false;
                if (!WitchP2ManifestEnabled) return false;
                int id = BossReflect.InstanceId(runtimeObject);
                return id != 0 && WitchBossControllerAdapter.IsInstancePhase2Suppressed(id);
            }
            catch { return false; }
        }

        /// <summary>PREFIX of WitchPhase2.ShowWitches: the Client must NOT use its own random dome layout — the manifest
        /// (Host's ShowWitches result) drives it. Block on a joined Client; Host / single-player / reentry run normally.</summary>
        public static bool OnLocalWitchShowWitches_Pre(object witchPhase2)
        {
            try
            {
                if (!Enabled || witchPhase2 == null || !WitchP2ManifestEnabled) return true;
                if (InReentry) return true;
                NetMode mode = NetGameplaySyncBridge.BossMode;
                if (mode == NetMode.Host) return true;
                bool joined = false; try { joined = NetClientJoinFlow.SessionJoinedHost; } catch { }
                if (mode != NetMode.Client || !joined) return true;
                if (LogOn) Plugin.Log.Info("[WitchP2] client blocked local ShowWitches (manifest drives dome layout)");
                return false;
            }
            catch { return true; }
        }

        /// <summary>POSTFIX of WitchPhase2.ShowWitches (HOST): capture the final dome layout (after the shuffle) and
        /// broadcast it so the Client mirrors the same real/illusion-per-dome.</summary>
        public static void OnHostWitchShowWitches(object witchPhase2)
        {
            try
            {
                if (!Enabled || witchPhase2 == null || !WitchP2ManifestEnabled) return;
                if (NetGameplaySyncBridge.BossMode != NetMode.Host) return;
                // Resolve the owning WitchBossController (the manifest is keyed on the encounter, not the phase object).
                if (!TryFindWitchEncounter(out var adapter, out var controller, out string key)) return;
                int realDome = adapter.GetHostRealDomeIndex(controller);
                int domeCount = adapter.GetHostDomeCount(controller);
                if (realDome < 0 || domeCount <= 0) { Plugin.Log.Warn($"[WitchP2] host ShowWitches capture failed realDome={realDome} domes={domeCount}"); return; }
                if (!TryGetRunContext(out string chap, out int lvl, out bool hasSeed, out int seed)) { chap = ""; lvl = -1; }
                // Start a fresh Phase 2 cycle: new id, reset per-cycle result dedup, mark active.
                _witchP2Cycle++;
                _witchP2Active = true;
                _witchP2RealHitConsumed = false;
                _witchP2DefeatedDomes.Clear();
                var msg = new NetWitchP2Manifest
                {
                    EncounterKey = key, BossType = controller.GetType().Name, ChapterName = chap, LevelIndex = lvl,
                    HasSeed = hasSeed, Seed = seed, PhaseRevision = _witchP2Cycle, DomeCount = domeCount,
                    RealDomeIndex = realDome, Timestamp = Time.realtimeSinceStartup,
                };
                WitchP2ManifestBroadcast++;
                Plugin.Log.Info($"[WitchP2] host ShowWitches captured + broadcasting {msg.ToCompact()} (cycle={_witchP2Cycle})");
                NetGameplaySyncBridge.BroadcastHostWitchP2Manifest(msg);
            }
            catch (Exception ex) { Plugin.Log.Warn($"[WitchP2] OnHostWitchShowWitches failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>POSTFIX of WitchPhase2.RealWitchTakeDamage / IllusionTakeDamage (HOST): broadcast the result so the
        /// Client (whose local handlers never fire — hits route to the Host) hides the right dome(s). Gated to the active
        /// Phase 2 cycle and FIRST state transition only — continued fire/damage (or hits after Phase 2 ends) do not
        /// re-broadcast (LogOutput38: realHit + illusionDefeated were spamming, with stale revisions).</summary>
        public static void OnHostWitchP2Hit(object witchPhase2, object hitUnit, bool realHit)
        {
            try
            {
                if (!Enabled || witchPhase2 == null || !WitchP2ManifestEnabled) return;
                if (NetGameplaySyncBridge.BossMode != NetMode.Host) return;
                if (!_witchP2Active) return; // Phase 2 not currently active (already ended) — drop late damage results
                if (!TryFindWitchEncounter(out var adapter, out var controller, out string key)) return;
                var p2 = BossReflect.GetMember(controller, "phase2");

                int dome = -1; byte kind;
                if (realHit)
                {
                    // Only the FIRST real hit (illusionsDissappeared just became true) hides all illusions. The handler
                    // re-runs for every subsequent hit (it still forwards damage) but must not re-broadcast.
                    if (_witchP2RealHitConsumed) return;
                    bool dissappeared = BossReflect.TryGetBool(p2, "illusionsDissappeared", out bool d) && d;
                    if (!dissappeared) return; // body did not transition (e.g. !phaseActive)
                    _witchP2RealHitConsumed = true;
                    kind = NetWitchP2Result.KindRealHit;
                }
                else
                {
                    if (hitUnit == null) return;
                    // dome index = host spawnedWitches.IndexOf(hitUnit)
                    if (BossReflect.GetMember(p2, "spawnedWitches") is System.Collections.IList list)
                        for (int i = 0; i < list.Count; i++) if (ReferenceEquals(list[i], hitUnit)) { dome = i; break; }
                    if (dome < 0) return;
                    if (_witchP2DefeatedDomes.Contains(dome)) return; // already broadcast this cycle (continued fire)
                    // Confirm the body actually defeated it (appearingWitches[unit]==true), not a no-op re-hit.
                    bool defeated = false;
                    if (BossReflect.GetMember(p2, "appearingWitches") is System.Collections.IDictionary aw && aw.Contains(hitUnit) && aw[hitUnit] is bool b) defeated = b;
                    if (!defeated) return;
                    _witchP2DefeatedDomes.Add(dome);
                    kind = NetWitchP2Result.KindIllusionDefeated;
                }

                var msg = new NetWitchP2Result { EncounterKey = key, PhaseRevision = _witchP2Cycle, DomeIndex = dome, Kind = kind };
                WitchP2ResultBroadcast++;
                Plugin.Log.Info($"[WitchP2] host result + broadcasting {msg.ToCompact()} (cycle={_witchP2Cycle})");
                NetGameplaySyncBridge.BroadcastHostWitchP2Result(msg);
            }
            catch (Exception ex) { Plugin.Log.Warn($"[WitchP2] OnHostWitchP2Hit failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>HOST: mark Phase 2 inactive when leaving it, so no more Phase 2 results are broadcast (continued
        /// fire on hidden witches must not produce results once the witch has moved on).</summary>
        public static void OnHostWitchPhase2Ended()
        {
            _witchP2Active = false;
        }

        public static int WitchDeathTerminal;
        public static int WitchDeathClientReplica;

        private static void MarkWitchTerminal(object component, string ctx)
        {
            _witchP2Active = false;
            if (!TryGetEncounterKeyForBoss(component, out string key, out _)) return;
            bool first; lock (_lock) { first = _terminalDead.Add(key); }
            if (first) { WitchDeathTerminal++; Plugin.Log.Info($"[WitchDeath] witch terminal key={key} ({ctx}) — hits/state suppressed"); }
        }

        /// <summary>Phase 5.4-G7: POSTFIX of WitchBossController.WitchDeath on the HOST (the original ran fully, incl. the
        /// amulet block which only works host-side). Mark the encounter terminal.</summary>
        public static void OnHostWitchDeath(object component)
        {
            try
            {
                if (!Enabled || component == null) return;
                try { if (!Plugin.Cfg.EnableWitchDeathFix.Value) return; } catch { return; }
                MarkWitchTerminal(component, "host");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[WitchDeath] OnHostWitchDeath failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>Phase 5.4-G7b: PREFIX of WitchBossController.WitchDeath. On a joined CLIENT the original WitchDeath
        /// crashes at the amulet (no equipped Amulet → KeyNotFoundException), aborting the rest. BLOCK the original and
        /// run the safe replica (everything except amulet + host-only PlayerProgress). Returns true to run the original
        /// (host / single-player / reentry), false to block (joined client). Never throws.</summary>
        public static bool OnLocalWitchDeath_Pre(object component)
        {
            try
            {
                if (!Enabled || component == null) return true;
                try { if (!Plugin.Cfg.EnableWitchDeathFix.Value) return true; } catch { return true; }
                if (InReentry) return true;
                NetMode mode = NetGameplaySyncBridge.BossMode;
                if (mode == NetMode.Host) return true;
                bool joined = false; try { joined = NetClientJoinFlow.SessionJoinedHost; } catch { }
                if (mode != NetMode.Client || !joined) return true;
                var adapter = ResolveAdapter(component) as WitchBossControllerAdapter;
                if (adapter == null) return true;
                BeginApply();
                bool ok; string detail;
                try { ok = adapter.TryApplyWitchDeath(component, out detail); }
                finally { EndApply(); }
                if (ok) WitchDeathClientReplica++;
                Plugin.Log.Info($"[WitchDeath] client safe death replica ok={ok} ({detail})");
                MarkWitchTerminal(component, "client-replica");
                return false; // block the crashing original
            }
            catch (Exception ex) { Plugin.Log.Warn($"[WitchDeath] OnLocalWitchDeath_Pre failed: {ex.GetType().Name}: {ex.Message}"); return true; }
        }

        public static void HandleHostWitchP2Manifest(NetWitchP2Manifest msg)
        {
            try
            {
                if (!Enabled || msg == null || NetGameplaySyncBridge.BossMode != NetMode.Client || !WitchP2ManifestEnabled) return;
                if (!TryFindLocalEncounter(msg.EncounterKey, out var adapterBase, out var component) || !(adapterBase is WitchBossControllerAdapter adapter))
                { if (LogOn) Plugin.Log.Info($"[WitchP2] client has no Witch encounter for {msg.ToCompact()}"); return; }

                if (!adapter.IsPhase2Ready(component, msg.DomeCount))
                {
                    lock (_lock) { _pendingP2Manifest = new PendingP2Manifest { Msg = msg, Until = Time.realtimeSinceStartup + 10f }; }
                    Plugin.Log.Info($"[WitchP2] client manifest received but local not ready → pending {msg.ToCompact()}");
                    return;
                }
                ApplyP2ManifestNow(adapter, component, msg);
            }
            catch (Exception ex) { Plugin.Log.Warn($"[WitchP2] HandleHostWitchP2Manifest failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static void ApplyP2ManifestNow(WitchBossControllerAdapter adapter, object component, NetWitchP2Manifest msg)
        {
            BeginApply();
            bool ok; int placed; string detail;
            try { ok = adapter.ApplyP2Manifest(component, msg.RealDomeIndex, msg.DomeCount, out placed, out detail); }
            finally { EndApply(); }
            if (ok) { WitchP2ManifestApplied++; _p2ClientAppliedCycle = msg.PhaseRevision; }
            Plugin.Log.Info($"[WitchP2] client manifest applied ok={ok} {msg.ToCompact()} ({detail})");
        }

        public static void HandleHostWitchP2Result(NetWitchP2Result msg)
        {
            try
            {
                if (!Enabled || msg == null || NetGameplaySyncBridge.BossMode != NetMode.Client || !WitchP2ManifestEnabled) return;
                if (!TryFindLocalEncounter(msg.EncounterKey, out var adapterBase, out var component) || !(adapterBase is WitchBossControllerAdapter adapter))
                { if (LogOn) Plugin.Log.Info($"[WitchP2] client has no Witch encounter for result {msg.ToCompact()}"); return; }
                // Drop results from a different Phase 2 cycle than the one we applied (stale / next-cycle leakage).
                if (msg.PhaseRevision != _p2ClientAppliedCycle)
                { if (LogOn) Plugin.Log.Info($"[WitchP2] client dropped stale result {msg.ToCompact()} (appliedCycle={_p2ClientAppliedCycle})"); return; }
                BeginApply();
                bool ok; string detail;
                try { ok = adapter.ApplyP2Result(component, msg.DomeIndex, msg.Kind, out detail); }
                finally { EndApply(); }
                if (ok) WitchP2ResultApplied++;
                Plugin.Log.Info($"[WitchP2] client result applied ok={ok} {msg.ToCompact()} ({detail})");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[WitchP2] HandleHostWitchP2Result failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>Find the active Witch encounter (registry first, then scene scan), returning its concrete adapter.</summary>
        private static bool TryFindWitchEncounter(out WitchBossControllerAdapter adapter, out object controller, out string key)
        {
            adapter = null!; controller = null!; key = "";
            lock (_lock)
            {
                foreach (var kv in _registry)
                    if (kv.Value.Adapter is WitchBossControllerAdapter wa && kv.Value.Component is UnityEngine.Object uo && uo != null)
                    { adapter = wa; controller = kv.Value.Component; key = kv.Key; return true; }
            }
            // not registered yet — scan
            if (TryBuildContext(out var ctx, out _))
            {
                foreach (var a in _adapters)
                {
                    if (!(a is WitchBossControllerAdapter wa)) continue;
                    var t = a.ResolveType(); if (t == null) continue;
                    foreach (var obj in FindSceneComponents(t))
                    {
                        var id = a.BuildEncounterId(obj, in ctx);
                        Register(id.Key, a, obj);
                        adapter = wa; controller = obj; key = id.Key; return true;
                    }
                }
            }
            return false;
        }

        // ================================================================== host: client request

        public static void HandleClientBossStartRequest(NetClientBossStartRequest req, string peerId)
        {
            try
            {
                if (!Enabled || req == null) return;
                if (NetGameplaySyncBridge.BossMode != NetMode.Host) return;

                BossStartRequestReceived++;
                if (LogOn) Plugin.Log.Info($"[BossEncounter] host received ClientBossStartRequest from {peerId}: {req.ToCompact()}");

                // Session/run validation: the requested run must be the host's current run.
                if (!TryBuildContext(out var ctx, out _) || !string.Equals(ctx.ChapterName, req.ChapterName, StringComparison.Ordinal)
                    || ctx.LevelIndex != req.LevelIndex
                    || (ctx.HasSeed && req.HasSeed && ctx.Seed != req.Seed))
                {
                    BossStartRejectedSession++;
                    Plugin.Log.Warn($"[BossEncounter] reject boss start request from {peerId}: run mismatch req={req.ChapterName}:{req.LevelIndex} host={ctx.ChapterName}:{ctx.LevelIndex}");
                    return;
                }

                if (!TryFindLocalEncounter(req.EncounterKey, out var adapter, out var component))
                {
                    BossEncounterNotFound++;
                    Plugin.Log.Warn($"[BossEncounter] host has no local encounter for key={req.EncounterKey}; candidates: {DescribeCandidates()}");
                    return;
                }

                // If not already started, the host starts ITS boss (authoritative), then broadcasts to all.
                if (!adapter.IsStarted(component))
                {
                    BeginApply();
                    bool ok;
                    string detail;
                    try { ok = adapter.TryApplyHostStart(component, BuildState(req.EncounterKey, adapter, component, in ctx, "ClientRequest:" + req.StartSource), out detail); }
                    finally { EndApply(); }
                    if (!ok)
                    {
                        BossStartApplyFailed++;
                        Plugin.Log.Warn($"[BossEncounter] host failed to start boss for client request key={req.EncounterKey}: {detail}");
                        return;
                    }
                    if (LogOn) Plugin.Log.Info($"[BossEncounter] host started boss on client request key={req.EncounterKey}: {detail}");
                }

                // Broadcast the (now-started) state to all clients — including the requester.
                lock (_lock) { _hostBroadcast.Add(req.EncounterKey); }
                var state = BuildState(req.EncounterKey, adapter, component, in ctx, "ClientRequest:" + req.StartSource);
                BossStartBroadcast++;
                if (LogOn) Plugin.Log.Info($"[BossEncounter] host broadcasting BossEncounterStart (from request) {state.ToCompact()}");
                NetGameplaySyncBridge.BroadcastHostBossEncounterStart(state);
                TryBeginSandstormArena(adapter, component, in ctx);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[BossEncounter] HandleClientBossStartRequest failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ================================================================== client: host start

        public static void HandleHostBossEncounterStart(NetBossEncounterState state)
        {
            try
            {
                if (!Enabled || state == null) return;
                if (NetGameplaySyncBridge.BossMode != NetMode.Client) return;

                if (LogOn) Plugin.Log.Info($"[BossEncounter] client received HostBossEncounterStart {state.ToCompact()}");

                lock (_lock) { if (!_appliedStart.Add(state.EncounterKey)) { if (LogOn) Plugin.Log.Info($"[BossEncounter] start already applied key={state.EncounterKey}"); return; } }

                if (!TryFindLocalEncounter(state.EncounterKey, out var adapter, out var component))
                {
                    BossEncounterNotFound++;
                    Plugin.Log.Warn($"[BossEncounter] client has no local encounter for key={state.EncounterKey}; candidates: {DescribeCandidates()}");
                    return;
                }

                if (adapter.IsStarted(component))
                {
                    if (LogOn) Plugin.Log.Info($"[BossEncounter] client boss already started key={state.EncounterKey}");
                    return;
                }

                // Open the authorized-continuation window BEFORE invoking, so any chain step that fires synchronously
                // inside the apply (or on a later frame) is recognised as authorized rather than blocked.
                OpenContinuation(state.EncounterKey, adapter, component, "HostBossEncounterStart:" + state.StartSource);

                string before = SafeDescribe(adapter, component);
                BeginApply();
                bool ok; string detail;
                try { ok = adapter.TryApplyHostStart(component, state, out detail); }
                finally { EndApply(); }

                if (ok)
                {
                    BossStartApplied++;
                    Plugin.Log.Info($"[BossEncounter] client applied start source=HostAuthority key={state.EncounterKey} invoked={detail} before[{before}] after[{SafeDescribe(adapter, component)}]");
                    try { adapter.OnClientPresentationStart(component); } catch { } // F2: make the local boss enter combat presentation
                    // The chain is async (coroutines / dialogue). Verify a beat later that it actually reached started.
                    lock (_lock)
                    {
                        _pendingVerify.Add(new PendingVerify
                        {
                            Key = state.EncounterKey, Source = state.StartSource, Invoked = detail,
                            Before = before, DueAt = Time.realtimeSinceStartup + 2f,
                        });
                    }
                }
                else
                {
                    BossStartApplyFailed++;
                    Plugin.Log.Warn($"[BossEncounter] client failed to apply host start key={state.EncounterKey}: {detail}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[BossEncounter] HandleHostBossEncounterStart failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ================================================================== local encounter lookup

        private static bool TryFindLocalEncounter(string key, out IBossEncounterAdapter adapter, out object component)
        {
            lock (_lock)
            {
                if (_registry.TryGetValue(key, out var e) && e.Component is UnityEngine.Object uo && uo != null)
                {
                    adapter = e.Adapter; component = e.Component; return true;
                }
            }

            // Not registered yet (the host/client may not have interacted) — scan the active scene.
            if (!TryBuildContext(out var ctx, out _)) { adapter = null!; component = null!; return false; }

            foreach (var a in _adapters)
            {
                var t = a.ResolveType();
                if (t == null) continue;
                foreach (var obj in FindSceneComponents(t))
                {
                    var id = a.BuildEncounterId(obj, in ctx);
                    if (id.Key == key)
                    {
                        Register(key, a, obj);
                        adapter = a; component = obj; return true;
                    }
                }
            }

            adapter = null!; component = null!; return false;
        }

        private static string DescribeCandidates()
        {
            if (!TryBuildContext(out var ctx, out _)) return "no-run-state";
            var parts = new List<string>();
            foreach (var a in _adapters)
            {
                var t = a.ResolveType();
                if (t == null) continue;
                foreach (var obj in FindSceneComponents(t))
                {
                    var id = a.BuildEncounterId(obj, in ctx);
                    // Active-state is logged so we can tell "host has no such boss" from "host has it but the
                    // room/object is inactive because the remote player is far away" (Phase 5.4-E2 P1 probe).
                    string active = (obj is Component c && c != null) ? (c.gameObject.activeInHierarchy ? "active" : "inactive") : "?";
                    parts.Add($"{id.Key}[{active}]");
                    if (parts.Count >= 8) break;
                }
            }
            return parts.Count == 0 ? "none" : string.Join(" ; ", parts);
        }

        // Resources.FindObjectsOfTypeAll avoids the deprecated FindObjectsOfType(Type) and also finds inactive
        // boss objects; filtered to real loaded-scene instances (not prefab assets) to skip ScriptableObjects/prefabs.
        private static IEnumerable<object> FindSceneComponents(Type t)
        {
            UnityEngine.Object[] all;
            try { all = Resources.FindObjectsOfTypeAll(t); }
            catch { yield break; }
            foreach (var obj in all)
            {
                if (obj == null) continue;
                if (!(obj is Component c) || c == null || c.gameObject == null) continue;
                var scene = c.gameObject.scene;
                if (!scene.IsValid() || !scene.isLoaded) continue; // skip prefab assets
                yield return obj;
            }
        }

        // ================================================================== per-frame tick (from Plugin.Update)

        /// <summary>Phase 5.4-E2: drains deferred post-apply verifications and prunes stale continuation windows.
        /// Called from Plugin.Update. Never throws.</summary>
        public static void Tick()
        {
            try
            {
                if (!Enabled) return;

                TickHostBossStateBroadcast(); // Phase 5.4-E4.2: periodic boss health/state push (host)

                // Phase 5.4-E4: deferred dialog finalize — Stop the local boss dialog as soon as it becomes active
                // within the window (it often opens just after the commit). Prune under lock; finalize outside it.
                string? finalizeKey = null;
                lock (_lock)
                {
                    if (_pendingDialogFinalize.Count > 0)
                    {
                        float pnow = Time.realtimeSinceStartup;
                        for (int i = _pendingDialogFinalize.Count - 1; i >= 0; i--)
                            if (pnow > _pendingDialogFinalize[i].Until) _pendingDialogFinalize.RemoveAt(i);
                        if (_pendingDialogFinalize.Count > 0) finalizeKey = _pendingDialogFinalize[0].Key;
                    }
                }
                if (finalizeKey != null)
                {
                    try
                    {
                        if (BossDialogReflect.IsDialogActive() && BossDialogReflect.TryFinalizeCurrentDialog(out string d))
                            Plugin.Log.Info($"[BossDialogCommit] deferred dialog finalized key={finalizeKey}: {d}");
                    }
                    catch { }
                }

                // G5: apply a pending Witch Phase 2 manifest once the local dome witches are ready (async spawn).
                PendingP2Manifest? p2pending = null;
                lock (_lock) { p2pending = _pendingP2Manifest; }
                if (p2pending != null)
                {
                    float pnow = Time.realtimeSinceStartup;
                    if (pnow > p2pending.Until) { lock (_lock) { if (_pendingP2Manifest == p2pending) _pendingP2Manifest = null; } Plugin.Log.Warn($"[WitchP2] pending manifest expired (local never ready) {p2pending.Msg.ToCompact()}"); }
                    else if (TryFindLocalEncounter(p2pending.Msg.EncounterKey, out var pa, out var pc) && pa is WitchBossControllerAdapter pwa && pwa.IsPhase2Ready(pc, p2pending.Msg.DomeCount))
                    {
                        lock (_lock) { if (_pendingP2Manifest == p2pending) _pendingP2Manifest = null; }
                        Plugin.Log.Info($"[WitchP2] client applying pending manifest (now ready) {p2pending.Msg.ToCompact()}");
                        ApplyP2ManifestNow(pwa, pc, p2pending.Msg);
                    }
                }

                // F6: drain deferred eye-phase completion after-snapshots (log before→after of the host-authorized
                // RestartPhases; flag if the Client is still stuck in Phase 5 / invulnerable / did not return to centre).
                List<PendingEyeComplete>? eyeDue = null;
                lock (_lock)
                {
                    if (_pendingEyeComplete.Count > 0)
                    {
                        float now = Time.realtimeSinceStartup;
                        for (int i = _pendingEyeComplete.Count - 1; i >= 0; i--)
                            if (_pendingEyeComplete[i].DueAt <= now) { (eyeDue ??= new List<PendingEyeComplete>()).Add(_pendingEyeComplete[i]); _pendingEyeComplete.RemoveAt(i); }
                    }
                }
                if (eyeDue != null)
                {
                    foreach (var v in eyeDue)
                    {
                        if (!TryFindLocalEncounter(v.Key, out var a, out var c) || !a.TryReadEyeCompletionDiag(c, out int phaseA, out int restartA, out bool invulnA, out Vector3 posA))
                        { Plugin.Log.Warn($"[LuciaEye] cycle-complete verify: encounter gone key={v.Key} cycle={v.Cycle}"); continue; }
                        Plugin.Log.Info($"[LuciaEye] currentPhase {v.PhaseBefore} -> {phaseA}");
                        Plugin.Log.Info($"[LuciaEye] restartCounter {v.RestartBefore} -> {restartA}");
                        Plugin.Log.Info($"[LuciaEye] invulnerable {v.InvulnBefore} -> {invulnA}");
                        Plugin.Log.Info($"[LuciaEye] position {v.PosBefore:F1} -> {posA:F1}");
                        bool leftPhase5 = phaseA != 5;
                        bool restarted = restartA > v.RestartBefore;
                        if (leftPhase5 || restarted)
                            Plugin.Log.Info($"[LuciaEye] restart routine / StartPhase observed (phase {v.PhaseBefore}->{phaseA}, restartCounter {v.RestartBefore}->{restartA}) key={v.Key}");
                        else
                            Plugin.Log.Warn($"[LuciaEye] STILL stuck after RestartPhases key={v.Key}: phase remained {phaseA}{(phaseA == 5 ? " (phase remained 5)" : "")}{(invulnA ? " (invulnerable remained true)" : "")} (return-to-center / StartPhase did not run)");
                    }
                }

                List<PendingVerify>? due = null;
                lock (_lock)
                {
                    if (_pendingVerify.Count > 0)
                    {
                        float now = Time.realtimeSinceStartup;
                        for (int i = _pendingVerify.Count - 1; i >= 0; i--)
                        {
                            if (_pendingVerify[i].DueAt <= now)
                            {
                                (due ??= new List<PendingVerify>()).Add(_pendingVerify[i]);
                                _pendingVerify.RemoveAt(i);
                            }
                        }
                    }
                }

                if (due != null)
                {
                    foreach (var v in due)
                    {
                        if (!TryFindLocalEncounter(v.Key, out var adapter, out var component)) continue;
                        bool started = SafeStarted(adapter, component);
                        bool contActive = IsContinuationActive(v.Key, out _);
                        if (started)
                        {
                            if (LogOn) Plugin.Log.Info($"[BossEncounter] host-authority start verified key={v.Key} source={v.Source} after[{SafeDescribe(adapter, component)}] continuationActive={contActive}");
                        }
                        else
                        {
                            BossApplyIncomplete++;
                            Plugin.Log.Warn($"[BossEncounter] host-authority start incomplete key={v.Key} source={v.Source} invoked={v.Invoked} after[{SafeDescribe(adapter, component)}] continuationActive={contActive} expected={DescribeExpectedChain(adapter)}");
                        }
                    }
                }

                // Prune windows whose start+grace elapsed (cheap; IsContinuationActive removes them lazily).
                lock (_lock)
                {
                    if (_continuation.Count > 0)
                    {
                        float now = Time.realtimeSinceStartup;
                        List<string>? drop = null;
                        foreach (var kv in _continuation)
                        {
                            var w = kv.Value;
                            if (w.StartObserved && (now - w.StartObservedAt) > ContinuationGraceSeconds)
                                (drop ??= new List<string>()).Add(kv.Key);
                        }
                        if (drop != null) foreach (var k in drop) _continuation.Remove(k);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[BossEncounter] Tick failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static string DescribeExpectedChain(IBossEncounterAdapter adapter)
        {
            try { return adapter.StartChainMethods.Length == 0 ? "<none>" : string.Join("->", adapter.StartChainMethods); }
            catch { return "<err>"; }
        }

        public static string FormatCounters()
            => $"discovered={BossEncounterDiscovered} broadcast={BossStartBroadcast} reqSent={BossStartRequestSent} reqRecv={BossStartRequestReceived} " +
               $"applied={BossStartApplied} applyFailed={BossStartApplyFailed} blocked={BossLocalStartBlocked} rejectSession={BossStartRejectedSession} notFound={BossEncounterNotFound} " +
               $"contAllowed={BossContinuationAllowed} contBlocked={BossContinuationBlocked} applyIncomplete={BossApplyIncomplete} dupSuppressed={BossDuplicateSuppressed} " +
               $"dlgReq={BossDialogCommitRequested} dlgBroadcast={BossDialogCommitBroadcast} dlgApplied={BossDialogCommitApplied} dlgEntrySuppressed={BossDialogEntrySuppressed} stateBroadcast={BossStateBroadcast} stateApplied={BossStateApplied} " +
               $"hitSent={BossHitClientSent} hitRecv={BossHitHostRecv} hitApplied={BossHitHostApplied} hitRejected={BossHitHostRejected} hitVisSent={BossHitVisualSent} hitVisPlayed={BossHitVisualPlayed} " +
               $"poolSent={BossDiscreteSent} poolApplied={BossDiscreteApplied} poolClientBlocked={BossDiscreteClientSelfFired} deathBroadcast={BossDeathBroadcast} deathApplied={BossDeathApplied} terminal={_terminalDead.Count} " +
               $"eyeReportSent={LuciaEyeReportSent} eyeReportRecv={LuciaEyeReportRecv} eyeAccepted={LuciaEyeAccepted} eyeRejected={LuciaEyeRejected} eyeStateBroadcast={LuciaEyeStateBroadcast} eyeStateApplied={LuciaEyeStateApplied} eyeResidualCleared={LuciaEyeResidualCleared} " +
               $"luciaDeathBroadcast={LuciaDeathBroadcast} luciaDeathApplied={LuciaDeathApplied} " +
               $"witchPhaseBroadcast={WitchPhaseBroadcast} witchPhaseApplied={WitchPhaseApplied} witchPhaseBlockedLocal={WitchPhaseBlockedLocal} witchPhaseStaleIgnored={WitchPhaseStaleIgnored} " +
               $"witchP2ManifestBroadcast={WitchP2ManifestBroadcast} witchP2ManifestApplied={WitchP2ManifestApplied} witchP2ResultBroadcast={WitchP2ResultBroadcast} witchP2ResultApplied={WitchP2ResultApplied} registry={_registry.Count}";
    }
}
