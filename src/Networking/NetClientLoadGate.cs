using UnityEngine;

namespace SULFURTogether.Networking
{
    /// <summary>
    /// Phase 5.3-J/K: Client join loading gate (scheme A — intercept GameManager.GoToLevel).
    ///
    /// Only COMBAT level loads are gated. Hub / Menu / SafeZone / unknown loads are always allowed so the
    /// Client can reach ChurchHub and never deadlocks on a loading screen. When a combat load is gated the
    /// gate actively requests the Host's generation input (instead of passively waiting), then performs a
    /// single host-driven GoToLevel under a reentry guard once a matching (combat) input arrives.
    ///
    /// All state is static because the GoToLevel Harmony prefix runs in a static context.
    /// </summary>
    internal static class NetClientLoadGate
    {
        private enum State { Idle, Waiting, HostDrivenInProgress }
        private enum LevelKind { Hub, Menu, Combat, Unknown }
        // Phase 5.6-DL P3/P4: what the gate is currently waiting for. Combat waits for the host's combat seed;
        // DeathRespawn waits for whatever destination the host broadcasts AFTER this death (hub on an all-die,
        // or a combat level if the host F3/teleports elsewhere) so both land in the SAME instance.
        private enum PendingKind { None, Combat, DeathRespawn }

        private static readonly object _lock = new object();

        private static NetMode _mode = NetMode.Off;
        private static bool    _connectedToHost;
        private static bool    _reentry;           // a host-driven (or manual) GoToLevel is executing
        private static State   _state = State.Idle;
        private static PendingKind _pendingKind = PendingKind.None;

        // pending local (combat) request — for logging / fallback
        private static string _pendingChapter = "<unknown>";
        private static int    _pendingLevel = -1;
        private static string _pendingLoadingMode = "";
        private static string _pendingSpawn = "";
        private static float  _waitingSince;
        private static bool   _timedOut;
        private static bool   _fallbackLogged;
        private static float  _nextRetryTime;
        private static bool   _missingSeedLogged;

        // active host-input request throttle
        private static float _nextRequestTime;
        private static int   _requestAttempt;

        // Phase 5.6-DL-Q2 client transition relay throttle (push the client's intended target to the host)
        private static float _nextRelayTime;
        private static int   _relayAttempt;

        // Phase F3-Reload: a client-led transition whose target is the scene the client is ALREADY in (F3 reload-in-
        // place). Such a target matches the host's STALE "I'm here" request, so without a freshness check the gate
        // self-releases and reloads locally — diverging from the host's live instance (Log147). When this is set we
        // require the host's release request to be FRESH (received after _clientLedEpoch), i.e. the host actually
        // re-led the reload. A normal MOVE to a different scene keeps the old behaviour (no epoch).
        private static bool  _clientLedReloadInPlace;
        private static float _clientLedEpoch;
        // The seed the client is reloading FROM (its current level seed at F3 time). A reload-in-place only releases
        // on a host request carrying a DIFFERENT seed — proof the host actually RE-GENERATED. The host's reply to our
        // gen-input request echoes its CURRENT (same) seed with a fresh timestamp, so a timestamp-only check released
        // us prematurely on the stale seed (Log149: client stayed one seed behind the host). Seed-change is the signal.
        private static bool  _clientLedReloadFromHasSeed;
        private static int   _clientLedReloadFromSeed;

        // Phase 5.6-CL: this combat wait was started by the client walking into an in-run NextLevelTrigger
        // (CompleteLevel). On timeout it must advance LOCALLY (the one-shot trigger is already consumed, so the
        // player would otherwise be stuck) rather than stay blocked forever. _clientInitiatedFallback runs the
        // real local CompleteLevel under the reentry guard.
        private static bool          _clientInitiated;
        private static System.Action? _clientInitiatedFallback;

        private static NetHostSceneRequest? _latestHostRequest;
        private static float _latestHostRequestAt;   // Phase 5.6-DL P4: when _latestHostRequest was received
        private static float _deathFollowEpoch;       // armed at local death; only host destinations learned AFTER
                                                       // this are followed (never the stale pre-death combat request)

        // ---- counters (diagnostic) ----
        public static int ClientLoadGateInterceptedClientLevelComplete; // Phase 5.6-CL
        public static int ClientLoadGateClientLevelCompleteFollowed;
        public static int ClientLoadGateClientLevelCompleteLocalFallback;
        public static int ClientLoadGateInterceptedCombat;
        public static int ClientLoadGateInterceptedHubDeath;   // Phase 5.6-DL P3: gated death respawn
        public static int ClientLoadGateHubDeathFollowed;       // landed on host hub seed
        public static int ClientLoadGateHubDeathFallback;       // timed out -> local hub
        public static int ClientLoadGateBypassedHub;
        public static int ClientLoadGateBypassedMenu;
        public static int ClientLoadGateBypassedHostMode;
        public static int ClientLoadGateBypassedSinglePlayer;
        public static int ClientLoadGateBypassedDisabled;
        public static int ClientLoadGateBypassedReentry;
        public static int ClientLoadGateBypassedUnknownLevelKind;
        public static int ClientLoadGateBypassedPreserveLocalRun; // Phase 5.4-A: not joined, own load preserved
        public static int ClientLoadGateHostInputRequestSent;
        public static int ClientLoadGateHostInputRequestRetry;
        public static int ClientLoadGateHostInputReceived;
        public static int ClientLoadGateStartedHostDrivenLoad;
        public static int ClientLoadGateApplyFailed;
        public static int ClientLoadGateMissingSeed;
        public static int ClientLoadGateTimeouts;

        // Phase 5.3-M P1-F auto-follow counters.
        public static int AutoFollowRequestReceived;
        public static int AutoFollowStarted;
        public static int AutoFollowSkippedDisabled;
        public static int AutoFollowSkippedHub;
        public static int AutoFollowSkippedAlreadyMatching;
        public static int AutoFollowSkippedReentry;
        public static int AutoFollowSkippedDeathRespawn; // Phase 5.6-DL: dead player respawning to hub
        public static int AutoFollowFailed;

        // Phase 5.4-B session-return (host returns to Hub/SafeZone) counters.
        public static int SessionReturnHubRequestReceived;
        public static int SessionReturnAutoFollowStarted;
        public static int SessionReturnSkippedNotJoined;
        public static int SessionReturnCompleted;
        public static int SessionReturnFailed;

        // auto-follow throttle (avoid double-driving the same target during the load transition window)
        private static string _lastAutoFollowKey = "";
        private static float  _nextAutoFollowTime;

        // ---- wiring from NetService ----

        public static void SetMode(NetMode mode)
        {
            lock (_lock)
            {
                if (_mode != mode)
                {
                    _mode = mode;
                    ResetLocked();
                }
            }
        }

        public static void UpdateNetState(NetMode mode, bool connectedToHost)
        {
            lock (_lock)
            {
                _mode = mode;
                _connectedToHost = connectedToHost;
            }
        }

        public static void Reset() { lock (_lock) { ResetLocked(); } }

        /// <summary>Phase 5.6-DL P4: the local player just committed native death. Stamp the death epoch so the
        /// death-respawn gate follows only the host destination the host broadcasts AFTER this moment — never the
        /// stale pre-death combat request still cached in _latestHostRequest (which would double-load).</summary>
        public static void NoteLocalDeathRespawnArmed()
        {
            lock (_lock) { _deathFollowEpoch = Now(); }
        }

        private static void ResetLocked()
        {
            _state = State.Idle;
            _pendingKind = PendingKind.None;
            _reentry = false;
            _timedOut = false;
            _fallbackLogged = false;
            _missingSeedLogged = false;
            _pendingChapter = "<unknown>";
            _pendingLevel = -1;
            _pendingLoadingMode = "";
            _pendingSpawn = "";
            _nextRequestTime = 0f;
            _requestAttempt = 0;
            _latestHostRequest = null;
            _lastAutoFollowKey = "";
            _nextAutoFollowTime = 0f;
            _clientInitiated = false;
            _clientInitiatedFallback = null;
            _clientLedReloadInPlace = false;
            _clientLedEpoch = 0f;
            _clientLedReloadFromHasSeed = false;
            _clientLedReloadFromSeed = 0;
            // Safety net: never leave the client stuck behind the black loading fade on a mode change / disconnect.
            if (NetLoadingFade.Active) NetLoadingFade.Hide();
        }

        // Reentry guard — wrapped around the actual host-driven/manual GoToLevel invoke so the prefix
        // does not re-intercept our own load. Callers MUST pair Begin/End (use try/finally).
        public static void BeginHostDrivenLoad() { lock (_lock) { _reentry = true; } }
        public static void EndHostDrivenLoad()   { lock (_lock) { _reentry = false; } }

        /// <summary>Phase 5.4-E3 (P2) diagnostic: true while a host-driven/manual GoToLevel is in progress.</summary>
        public static bool IsHostDrivenLoadInProgress { get { lock (_lock) return _reentry; } }

        /// <summary>Phase 5.6-LK: the gate's current net mode (so patches can tell client from host).</summary>
        public static NetMode CurrentMode { get { lock (_lock) return _mode; } }

        // ---- the GoToLevel prefix decision ----

        public static bool ShouldInterceptGoToLevel(string chapter, int levelIndex, string loadingMode, string spawn, System.Action? localFallback = null)
        {
            lock (_lock)
            {
                if (_reentry) { ClientLoadGateBypassedReentry++; LogBypass(chapter, levelIndex, loadingMode, "reentry"); return false; }
                if (_mode == NetMode.Off) { ClientLoadGateBypassedSinglePlayer++; return false; }
                if (_mode != NetMode.Client) { ClientLoadGateBypassedHostMode++; return false; }

                bool enabled;
                try { enabled = Plugin.Cfg.ClientWaitHostGenerationInputBeforeFirstLoad.Value; }
                catch { enabled = false; }
                if (!enabled) { ClientLoadGateBypassedDisabled++; LogBypass(chapter, levelIndex, loadingMode, "disabled"); return false; }

                // Phase 5.6-LK: only a LINKED client (联机状态 on) hands its loads to the host. An UNLINKED client
                // plays its own run — never intercept its loads so a half-done solo run is left completely untouched.
                if (!NetLinkState.ClientLinked)
                {
                    ClientLoadGateBypassedPreserveLocalRun++;
                    LogBypass(chapter, levelIndex, loadingMode, "not-linked-preserve-local-run");
                    return false;
                }

                LevelKind kind = ClassifyLevelKind(chapter, loadingMode, levelIndex);

                // Phase 5.6-DL: a DEATH respawn (always a hub) keeps its own gate — converge on the host's
                // destination instead of generating a divergent local hub.
                if (kind == LevelKind.Hub && IsDeathRespawn(loadingMode) && GateDeathRespawnEnabled())
                {
                    _pendingChapter = string.IsNullOrWhiteSpace(chapter) ? "<unknown>" : chapter;
                    _pendingLevel = levelIndex;
                    _pendingLoadingMode = loadingMode ?? "";
                    _pendingSpawn = spawn ?? "";
                    _waitingSince = Now();
                    _timedOut = false;
                    _fallbackLogged = false;
                    _missingSeedLogged = false;
                    _nextRetryTime = 0f;
                    _nextRequestTime = 0f;
                    _requestAttempt = 0;
                    _pendingKind = PendingKind.DeathRespawn;
                    _state = State.Waiting;
                    _clientInitiated = false;
                    _clientInitiatedFallback = null;
                    ClientLoadGateInterceptedHubDeath++;
                    Plugin.Log.Info($"[ClientLoadGate] intercepted death respawn chapter={_pendingChapter} reason=waiting-host-destination hostDestReady={HasFreshHostDestinationLocked()} timeout={SafeHubDeathTimeout():F0}s");
                    return true;
                }

                // Only the main menu (Menu loading mode = quitting the game) is never relayed — that is not a
                // co-op destination, and returning to menu also resets 联机状态.
                if (kind == LevelKind.Menu) { ClientLoadGateBypassedMenu++; LogBypass(chapter, levelIndex, loadingMode, "menu"); return false; }

                // Phase 5.6-LK-P2 (Type A): map TYPE no longer decides whether a transition is POSSIBLE — a linked
                // client may lead the host ANYWHERE (combat, hub/safe zone, or an F3/Unknown chapter like
                // DebugChapter), mirroring the host being able to pull the client anywhere. (Per-target PERMISSION
                // — "client may advance to next level" vs "client may F3 to any level" — is a future host-controlled
                // toggle, not a hard-coded kind gate.) Relay the target; the host leads + broadcasts; the client
                // follows. This removes the old "Unknown → bypass local → yanked back" double load.
                if (kind == LevelKind.Unknown) ClientLoadGateBypassedUnknownLevelKind++; // diagnostic count only
                string kindLabel = kind == LevelKind.Hub ? "hub" : kind == LevelKind.Unknown ? "unknown" : "combat";
                BeginClientLedTransitionLocked(chapter, levelIndex, loadingMode ?? "", spawn ?? "", localFallback, kindLabel);
                return true;
            }
        }

        // Phase 5.6-LK: shared setup for a client-led transition (the linked client chose a target; relay it to the
        // host and wait for the host's authoritative broadcast). Used by the GoToLevel intercept and the in-run
        // CompleteLevel intercept. Caller holds _lock.
        private static void BeginClientLedTransitionLocked(string chapter, int level, string loadingMode, string spawn, System.Action? localFallback, string kindLabel)
        {
            _pendingChapter = string.IsNullOrWhiteSpace(chapter) ? "<unknown>" : chapter;
            _pendingLevel = level;
            _pendingLoadingMode = loadingMode ?? "";
            _pendingSpawn = spawn ?? "";
            _waitingSince = Now();
            _timedOut = false;
            _fallbackLogged = false;
            _missingSeedLogged = false;
            _nextRetryTime = 0f;
            _nextRequestTime = 0f; // request host input on the next tick
            _requestAttempt = 0;
            _nextRelayTime = 0f;   // relay the intended target to the host on the next tick
            _relayAttempt = 0;
            _pendingKind = PendingKind.Combat;  // "Combat" here means "waiting to be led to _pending target"
            _state = State.Waiting;
            _clientInitiated = true;
            _clientInitiatedFallback = localFallback;

            // Phase F3-Reload: is the target the scene we are ALREADY in? (F3 reload-in-place vs a real move). The
            // local run state at this point still reads the CURRENT scene (the GoToLevel prefix runs before the load).
            _clientLedEpoch = Now();
            _clientLedReloadFromHasSeed = false;
            _clientLedReloadFromSeed = 0;
            bool sameScene = NetRunStateBridge.TryGetLocalRunState(out var curRun) && curRun.HasLevel
                && NetSceneName.SameScene(curRun.ChapterName, curRun.LevelIndex, chapter, level);
            _clientLedReloadInPlace = ReloadInPlaceRelayEnabled() && sameScene;
            if (_clientLedReloadInPlace && curRun.HasLevelSeed)
            {
                _clientLedReloadFromHasSeed = true;
                _clientLedReloadFromSeed = curRun.LevelSeed;
            }

            ClientLoadGateInterceptedCombat++;
            Plugin.Log.Info($"[ClientLoadGate] intercepted client-led {kindLabel} transition target={_pendingChapter}:{_pendingLevel} loadingMode={_pendingLoadingMode} spawn={_pendingSpawn} reason=waiting-host-lead reloadInPlace={_clientLedReloadInPlace} connected={_connectedToHost}");
        }

        private static void LogBypass(string chapter, int levelIndex, string loadingMode, string reason)
            => Plugin.Log.Info($"[ClientLoadGate] bypass GoToLevel chapter={chapter} level={levelIndex} loadingMode={loadingMode} reason={reason}");

        // ---- Phase 5.6-CL: client-initiated in-run level advance (CompleteLevel / NextLevelTrigger) ----

        /// <summary>
        /// Called from the CompleteLevel prefix when a JOINED client walks into an in-run exit. SULFUR advances
        /// sub-levels via CompleteLevel → SwitchLevelRoutine (never GoToLevel), so the combat GoToLevel gate never
        /// sees it and the client would generate its own divergent level then get yanked back by the host's run
        /// state. Instead we take over: enter the combat wait, relay the target so the host LEADS the transition,
        /// and the gated client follows the host's broadcast. The caller blocks the local CompleteLevel and shows
        /// a loading fade when this returns true. <paramref name="localFallback"/> runs the real local CompleteLevel
        /// if the host never leads (timeout) — the one-shot NextLevelTrigger is already consumed, so we must not
        /// leave the player stuck.
        /// </summary>
        public static bool TryBeginClientLevelCompleteRelay(string chapter, int level, System.Action localFallback, out string reason)
        {
            reason = "";
            lock (_lock)
            {
                if (_reentry) { reason = "reentry"; return false; }
                if (_mode != NetMode.Client) { reason = "not-client"; return false; }

                bool allow, relay;
                try { allow = Plugin.Cfg.AllowClientInitiatedLevelLoad.Value; } catch { allow = false; }
                try { relay = Plugin.Cfg.EnableClientTransitionRelay.Value; } catch { relay = false; }
                if (!allow) { reason = "disabled"; return false; }
                if (!relay) { reason = "relay-disabled"; return false; }              // no transport to notify host
                if (!NetLinkState.ClientLinked) { reason = "not-linked"; return false; } // Phase 5.6-LK: 联机状态 off
                if (_state != State.Idle) { reason = "gate-busy-" + _state; return false; }

                // Phase 5.6-LK-P2 (Type A): relay the next level whatever its kind (the normal in-run advance is
                // combat; end-of-act overflow is the ChurchHub safe zone — both should pull everyone together).
                if (ClassifyLevelKind(chapter, "Normal", level) == LevelKind.Menu) { reason = "menu-target"; return false; }

                BeginClientLedTransitionLocked(chapter, level, "Normal", "", localFallback, "level-complete");
                ClientLoadGateInterceptedClientLevelComplete++;
                reason = "waiting-host-lead";
                Plugin.Log.Info($"[ClientLoadGate] client level-complete relay armed target={_pendingChapter}:{_pendingLevel} timeout={SafeClientInitiatedTimeout():F0}s");
                return true;
            }
        }

        // ---- host generation input arrival ----

        public static void OnHostGenerationInput(NetHostSceneRequest request)
        {
            if (request == null) return;
            lock (_lock)
            {
                _latestHostRequest = request;
                _latestHostRequestAt = Now();
                ClientLoadGateHostInputReceived++;
                int chunks = request.HasUsedSets ? request.UsedChunksThisRun.Count : 0;
                int run = request.HasUsedSets ? request.UsedEventsThisRun.Count : 0;
                int env = request.HasUsedSets ? request.UsedEventsThisEnvironment.Count : 0;
                Plugin.Log.Info($"[ClientLoadGate] host generation input received graph={GraphName(request)} seed={(request.HasLevelSeed ? request.LevelSeed.ToString() : "?")} level={request.LevelIndex} usedChunks={chunks} usedRun={run} usedEnv={env} kind={ClassifyLevelKind(request.ChapterName, request.LoadingMode, request.LevelIndex)} state={_state}");
            }
            // Try to act immediately (outside the GoToLevel call stack since this runs from packet handling).
            Tick();

            // Phase 5.3-M P1-F: if the gate is idle (the Client never tried a local combat GoToLevel — the
            // common case when only the Host advances), auto-follow a Host-authorized combat request.
            TryAutoFollow(request);
        }

        // ---- Phase 5.3-M P1-F: automatic scene follow ----

        public static bool IsCombatTarget(string chapter, string loadingMode, int levelIndex)
            => ClassifyLevelKind(chapter, loadingMode, levelIndex) == LevelKind.Combat;

        // Phase 5.4-B: hub / safezone / menu targets are followable only as a SESSION RETURN (already joined).
        public static bool IsHubOrMenuTarget(string chapter, string loadingMode, int levelIndex)
        {
            var k = ClassifyLevelKind(chapter, loadingMode, levelIndex);
            return k == LevelKind.Hub || k == LevelKind.Menu;
        }

        public static string FormatSessionReturnCounters()
            => $"hubRecv={SessionReturnHubRequestReceived} hubStarted={SessionReturnAutoFollowStarted} " +
               $"hubDone={SessionReturnCompleted} hubSkippedNotJoined={SessionReturnSkippedNotJoined} hubFailed={SessionReturnFailed}";

        private static void TryAutoFollow(NetHostSceneRequest req)
        {
            if (req == null) return;

            bool enabled;
            try { enabled = Plugin.Cfg.EnableAutoFollowHostSceneRequest.Value; }
            catch { enabled = false; }

            lock (_lock)
            {
                if (_mode != NetMode.Client) return;
            }

            AutoFollowRequestReceived++;
            string target = req.TargetSceneKey();
            string graph = GraphName(req);
            string seed = req.HasLevelSeed ? req.LevelSeed.ToString() : "?";

            if (!enabled)
            {
                AutoFollowSkippedDisabled++;
                Plugin.Log.Info($"[AutoFollow] skipped reason=disabled target={target} graph={graph}");
                return;
            }

            if (!req.AutoLoadAllowed || !req.HasTargetScene)
            {
                AutoFollowSkippedHub++;
                Plugin.Log.Info($"[AutoFollow] skipped reason=not-authorized target={target} graph={graph} autoLoad={req.AutoLoadAllowed}");
                return;
            }

            // Classify by chapter AND graph (a special hub like ChurchHub_Xmas may carry a non-hub chapter).
            bool hub = IsHubOrMenuTarget(req.ChapterName, req.LoadingMode, req.LevelIndex)
                || NetSceneClassify.IsHubOrSafeZoneGraph(req.GraphName)
                || NetSceneClassify.IsHubOrSafeZoneChapter(req.ChapterName);
            // Phase LK-UnknownFollow: an Unknown-but-seeded host target (DebugChapter, DLC_01_RobotHeaven_*,
            // EndlessMode, ChallengeEnvironments, Onboarding) is a generated level the client must follow just like
            // combat. The host only marks such a target AutoLoadAllowed + seeded once it has finalized generation, so
            // gating on the seed here is safe — a real menu carries no seed and hubs are matched above.
            bool menu = !hub && IsHubOrMenuTarget(req.ChapterName, req.LoadingMode, req.LevelIndex);
            bool combat = !hub && (IsCombatTarget(req.ChapterName, req.LoadingMode, req.LevelIndex)
                || (req.HasLevelSeed && !menu));
            bool joined = NetLinkState.ClientLinked; // Phase 5.6-LK: 联机状态 is the authority for following the host

            // Phase 5.6-DL P4: the death-respawn GATE (ShouldInterceptGoToLevel -> Waiting(DeathRespawn)) now owns
            // following the host AFTER a death — it converges on the host's current destination (hub or, if the
            // host F3/teleports elsewhere, that combat level) using an epoch filter to skip the stale pre-death
            // request. So there is no longer a blanket "refuse combat while dead" block here; the gate path drives
            // it and the host-side combat-broadcast suppression prevents the all-die stale-combat double load.

            // Phase 5.4-B: a hub/safezone/menu target is followable only as a SESSION RETURN — the host went
            // back to the hub. An UNJOINED client must NOT be pulled into the host hub (preserve its own run).
            bool sessionReturn = false;
            if (!combat)
            {
                if (hub)
                {
                    SessionReturnHubRequestReceived++;
                    if (!joined)
                    {
                        SessionReturnSkippedNotJoined++;
                        Plugin.Log.Info($"[AutoFollow] skipped target={target} reason=not-joined-hub-request-waiting");
                        return;
                    }

                    // Phase 5.4-D-0 P0-D: a GENERATED hub (ChurchHub / ChurchHub_Xmas / ...) needs a seed. Never
                    // GoToLevel a seedless generated-hub request — that produces a divergent local hub seed and the
                    // client ends up in a different ChurchHub than the host. Wait for the finalized request instead.
                    bool generatedHub = NetSceneClassify.IsHubOrSafeZoneGraph(req.GraphName)
                        || NetSceneClassify.IsHubOrSafeZoneChapter(req.ChapterName);
                    if (generatedHub && !req.HasLevelSeed)
                    {
                        NetSceneFollowDiag.HubAutoFollowSkippedMissingSeed++;
                        NetSceneFollowDiag.HubAutoFollowWaitingFinalized++;
                        Plugin.Log.Info($"[AutoFollow] skipped target={target} reason=waiting-finalized-hub-seed graph={graph} seed={seed}");
                        Plugin.Log.Info($"[JoinFlow] hub return pending finalized seed target={target}");
                        return;
                    }

                    Plugin.Log.Info($"[JoinFlow] session return allowed target={target} reason=host-returned-hub");
                    sessionReturn = true;
                }
                else
                {
                    AutoFollowSkippedHub++;
                    Plugin.Log.Info($"[AutoFollow] skipped reason=non-combat-non-hub target={target} graph={graph}");
                    return;
                }
            }
            else if (!req.HasLevelSeed)
            {
                // Generated combat levels need the seed to reproduce; hub targets do not.
                AutoFollowSkippedHub++;
                Plugin.Log.Info($"[AutoFollow] skipped reason=no-seed target={target} graph={graph}");
                return;
            }

            lock (_lock)
            {
                if (_reentry) { AutoFollowSkippedReentry++; Plugin.Log.Info($"[AutoFollow] skipped reason=reentry target={target}"); return; }
                if (_state != State.Idle)
                {
                    // The gate already intercepted a local combat load; its own Waiting->drive path handles it.
                    Plugin.Log.Info($"[AutoFollow] skipped reason=gate-{_state} (gate path drives) target={target}");
                    return;
                }

                string key = target + "|" + (req.HasLevelSeed ? req.LevelSeed.ToString() : "?") + "|" + graph;
                if (key == _lastAutoFollowKey && Now() < _nextAutoFollowTime)
                {
                    AutoFollowSkippedAlreadyMatching++;
                    return;
                }
            }

            // Already in the requested host scene? Combat compares seed. For a hub, when the (finalized) request
            // carries a seed we must compare it too: if the client is in ChurchHub:0 with a DIFFERENT seed than the
            // host, it is NOT really in the host scene and must reload to the finalized seed (Phase 5.4-D-0 P0-E).
            if (NetRunStateBridge.TryGetLocalRunState(out var run) && run.HasLevel
                && NetSceneName.SameScene(run.ChapterName, run.LevelIndex, req.ChapterName, req.LevelIndex))
            {
                bool aligned;
                if (combat)
                {
                    aligned = !RequireSeedMatch() || (run.HasLevelSeed && req.HasLevelSeed && run.LevelSeed == req.LevelSeed);
                }
                else if (req.HasLevelSeed)
                {
                    aligned = run.HasLevelSeed && run.LevelSeed == req.LevelSeed;
                    if (!aligned)
                    {
                        NetSceneFollowDiag.HubSeedMismatchReload++;
                        Plugin.Log.Info($"[AutoFollow] hub seed mismatch; reloading finalized hub target={target} localSeed={(run.HasLevelSeed ? run.LevelSeed.ToString() : "?")} hostSeed={req.LevelSeed}");
                    }
                }
                else
                {
                    aligned = true; // seedless menu — scene match is enough
                }

                // Phase 5.4-D-1 P0-H: same chapter/level(/seed) but a DIFFERENT graph means the client is in the
                // wrong run (e.g. local Sewers1 vs host Hedgemaze). Reload to the host target instead of skipping.
                if (aligned && !string.IsNullOrWhiteSpace(req.GraphName) && !string.IsNullOrWhiteSpace(run.LevelGenerator)
                    && !string.Equals(req.GraphName, run.LevelGenerator, System.StringComparison.OrdinalIgnoreCase))
                {
                    aligned = false;
                    NetSceneFollowDiag.AutoFollowReloadGraphMismatch++;
                    Plugin.Log.Info($"[AutoFollow] graph mismatch; reloading host target requestGraph={req.GraphName} localGraph={run.LevelGenerator}");
                }

                if (aligned)
                {
                    if (!combat) NetSceneFollowDiag.HubSeedMatched++;
                    AutoFollowSkippedAlreadyMatching++;
                    Plugin.Log.Info($"[AutoFollow] skipped reason=already-matching target={target} graph={graph}");
                    return;
                }
            }

            // Phase 5.4-A: the join policy decides whether an UNJOINED client may auto-take-over now. Once
            // joined, this always allows (combat auto-follow / hub session-return continues). When skipped,
            // the latest host request is preserved for a manual follow.
            if (!NetClientJoinFlow.TryAuthorizeAutoFollow(target, graph))
                return;

            lock (_lock)
            {
                if (_state != State.Idle) return;
                _state = State.HostDrivenInProgress;
                _lastAutoFollowKey = target + "|" + (req.HasLevelSeed ? req.LevelSeed.ToString() : "?") + "|" + graph;
                _nextAutoFollowTime = Now() + 5f;
            }

            AutoFollowStarted++;
            if (sessionReturn)
            {
                SessionReturnAutoFollowStarted++;
                NetSceneFollowDiag.HubAutoFollowStartedFinalized++;
            }
            string kindLabel = sessionReturn ? "HubOrMenu" : "Combat";
            string action = sessionReturn ? "start-session-return" : "start";
            Plugin.Log.Info($"[AutoFollow] received host request target={target} graph={graph} seed={seed} kind={kindLabel} joined={joined} autoLoad=True action={action}");
            Plugin.Log.Info($"[AutoFollow] starting host-driven GoToLevel target={target} graph={graph} seed={seed}");

            bool ok;
            string result;
            try { ok = NetManualSceneFollower.TryFollow(req, out result); }
            catch (System.Exception ex) { ok = false; result = ex.GetType().Name + ": " + ex.Message; }

            lock (_lock) { _state = State.Idle; }

            if (ok)
            {
                if (sessionReturn) SessionReturnCompleted++;
                Plugin.Log.Info($"[AutoFollow] host-driven GoToLevel invoked: {result}");
            }
            else
            {
                AutoFollowFailed++;
                if (sessionReturn) SessionReturnFailed++;
                Plugin.Log.Warn($"[AutoFollow] host-driven GoToLevel failed: {result}");
            }
        }

        private static bool RequireSeedMatch()
        {
            try
            {
                return Plugin.Cfg.EnableLevelSeedAuthority.Value
                    && Plugin.Cfg.RequireSameLevelSeedForSceneMatch.Value;
            }
            catch { return false; }
        }

        // ---- per-frame ----

        public static void Tick()
        {
            NetHostSceneRequest? req = null;
            bool doHubDeathFallback = false;
            bool doClientInitiatedFallback = false;
            System.Action? clientFallback = null;
            lock (_lock)
            {
                if (_mode != NetMode.Client) return;
                if (_state != State.Waiting) return;

                // The release condition depends on what we are waiting for: a combat gate needs the host's
                // combat seed; a death respawn needs the host's current destination (hub or combat) learned
                // after this death.
                if (!HasMatchingReleaseRequestLocked())
                {
                    float waited = Now() - _waitingSince;

                    if (_pendingKind == PendingKind.DeathRespawn)
                    {
                        // A dead player must reach a hub even if the host never broadcasts a destination (lone
                        // death, host crashed). On timeout, fall back to a LOCAL hub respawn.
                        if (!_timedOut && waited >= SafeHubDeathTimeout())
                        {
                            _timedOut = true;
                            ClientLoadGateTimeouts++;
                            _state = State.HostDrivenInProgress; // block re-intercept while we invoke the fallback
                            doHubDeathFallback = true;
                            Plugin.Log.Warn("[ClientLoadGate] timeout waiting host destination; falling back to local hub respawn");
                        }
                        if (!doHubDeathFallback) return;
                    }
                    else
                    {
                        float timeout = _clientInitiated ? SafeClientInitiatedTimeout() : SafeTimeout();
                        if (!_timedOut && waited >= timeout)
                        {
                            _timedOut = true;
                            ClientLoadGateTimeouts++;
                            if (_clientInitiated)
                            {
                                // Client-initiated in-run advance: the one-shot NextLevelTrigger is already
                                // consumed, so we cannot just stay blocked — advance LOCALLY so the player is
                                // never stuck behind the black fade waiting on an unresponsive host.
                                Plugin.Log.Warn("[ClientLoadGate] timeout waiting host lead for client level-complete; advancing locally");
                                clientFallback = _clientInitiatedFallback;
                                _clientInitiatedFallback = null;
                                _clientInitiated = false;
                                _state = State.HostDrivenInProgress; // block re-intercept while the local complete runs
                                doClientInitiatedFallback = true;
                            }
                            else
                            {
                                Plugin.Log.Warn("[ClientLoadGate] timeout waiting host generation input; local combat load still blocked (still requesting)");
                                if (SafeFallback() && !_fallbackLogged)
                                {
                                    _fallbackLogged = true;
                                    Plugin.Log.Warn("[ClientLoadGate] timeout fallback allowed; local combat load may diverge");
                                    _state = State.Idle; // allow a subsequent local GoToLevel to proceed
                                }
                            }
                        }
                        if (!doClientInitiatedFallback) return;
                    }
                }
                else
                {
                    if (Now() < _nextRetryTime) return;
                    req = _latestHostRequest;
                    // For a death respawn, follow the host's destination SEED but (for a hub) keep the DEATH
                    // loading mode + Respawn spawn so the player respawns correctly.
                    if (_pendingKind == PendingKind.DeathRespawn && req != null)
                        req = BuildDeathFollowRequest(req);
                }
            }

            if (doHubDeathFallback) { DoHubDeathLocalFallback(); return; }
            if (doClientInitiatedFallback) { DoClientInitiatedLocalFallback(clientFallback); return; }
            TryStartHostDrivenGoToLevel(req!);
        }

        // Death-respawn timed out waiting for the host's hub seed — load the local hub (own seed) so the player
        // is never stuck on a loading screen. Last resort; the common all-players-down case follows the host hub.
        private static void DoHubDeathLocalFallback()
        {
            var req = new NetHostSceneRequest
            {
                ChapterName     = _pendingChapter,
                LevelIndex      = _pendingLevel,
                LoadingMode     = string.IsNullOrEmpty(_pendingLoadingMode) ? "Death" : _pendingLoadingMode,
                SpawnIdentifier = string.IsNullOrEmpty(_pendingSpawn) ? "Respawn" : _pendingSpawn,
                HasLevelSeed    = false, // own generation — last resort
                AutoLoadAllowed = false,
            };

            bool ok; string result;
            try { ok = NetManualSceneFollower.TryFollow(req, out result); }
            catch (System.Exception ex) { ok = false; result = ex.GetType().Name + ": " + ex.Message; }

            lock (_lock) { _state = State.Idle; _pendingKind = PendingKind.None; }
            ClientLoadGateHubDeathFallback++;
            Plugin.Log.Warn($"[ClientLoadGate] hub-death local fallback invoked ok={ok}: {result}");
        }

        // Phase 5.6-CL: the host never led a client-initiated in-run advance; run the real local CompleteLevel so
        // the player advances on their own (host unresponsive — local divergence no longer matters). The black
        // fade is kept up; the native CompleteLevel/OnCompleteLevelRoutine shows its own loading flow which
        // seamlessly takes over and clears the fade when the local level finishes generating.
        private static void DoClientInitiatedLocalFallback(System.Action? fallback)
        {
            ClientLoadGateClientLevelCompleteLocalFallback++;
            bool ok = true; string err = "";
            try { fallback?.Invoke(); }
            catch (System.Exception ex) { ok = false; err = ex.GetType().Name + ": " + ex.Message; }

            lock (_lock) { _state = State.Idle; _pendingKind = PendingKind.None; _clientInitiated = false; }
            if (ok)
            {
                Plugin.Log.Warn("[ClientLoadGate] client level-complete local fallback invoked (host did not lead)");
            }
            else
            {
                if (NetLoadingFade.Active) NetLoadingFade.Hide(); // no native load took over — clear the black
                Plugin.Log.Warn($"[ClientLoadGate] client level-complete local fallback failed: {err}");
            }
        }

        /// <summary>
        /// Called from NetService.Tick: returns true (and logs) when a host-input request should be sent
        /// to the host now. NetService performs the actual network send.
        /// </summary>
        public static bool TryConsumeHostInputRequestDue(out int attempt)
        {
            attempt = 0;
            lock (_lock)
            {
                if (_mode != NetMode.Client) return false;
                if (_state != State.Waiting) return false;
                if (HasMatchingReleaseRequestLocked() && Now() >= _nextRetryTime) return false; // about to drive; no need to request
                if (Now() < _nextRequestTime) return false;

                _nextRequestTime = Now() + SafeRequestInterval();
                _requestAttempt++;
                attempt = _requestAttempt;
                if (attempt <= 1) ClientLoadGateHostInputRequestSent++;
                else ClientLoadGateHostInputRequestRetry++;
                Plugin.Log.Info($"[ClientLoadGate] requesting host generation input reason=gate-wait attempt={attempt}");
                return true;
            }
        }

        // Follow the host's destination (chapter/level/seed/graph/usedSets = the host's instance). For a HUB
        // destination keep the client's original DEATH/Respawn loading so the player respawns through the death
        // path; for a COMBAT destination (host F3/teleported elsewhere) use the host's own loading mode so the
        // client enters the level normally rather than as a "respawn".
        private static NetHostSceneRequest BuildDeathFollowRequest(NetHostSceneRequest hostDest)
        {
            bool hubTarget = ClassifyLevelKind(hostDest.ChapterName, hostDest.LoadingMode, hostDest.LevelIndex) == LevelKind.Hub
                || NetSceneClassify.IsHubOrSafeZoneGraph(hostDest.GraphName)
                || NetSceneClassify.IsHubOrSafeZoneChapter(hostDest.ChapterName);

            var merged = new NetHostSceneRequest
            {
                RequestId       = hostDest.RequestId,
                HostPeerId      = hostDest.HostPeerId,
                HostPlayerName  = hostDest.HostPlayerName,
                ChapterName     = hostDest.ChapterName,
                LevelIndex      = hostDest.LevelIndex,
                LoadingMode     = hubTarget && !string.IsNullOrEmpty(_pendingLoadingMode) ? _pendingLoadingMode : hostDest.LoadingMode,
                SpawnIdentifier = hubTarget && !string.IsNullOrEmpty(_pendingSpawn) ? _pendingSpawn : hostDest.SpawnIdentifier,
                HostGameState   = hostDest.HostGameState,
                HasLevelSeed    = hostDest.HasLevelSeed,
                LevelSeed       = hostDest.LevelSeed,
                LevelGenerator  = hostDest.LevelGenerator,
                GraphName       = hostDest.GraphName,
                GenerationRunId = hostDest.GenerationRunId,
                HostRevision    = hostDest.HostRevision,
                Reason          = "DeathRespawnFollow",
                AutoLoadAllowed = hostDest.AutoLoadAllowed,
            };
            if (hostDest.HasUsedSets) merged.SetUsedSets(hostDest.ToUsedSets());
            return merged;
        }

        /// <summary>
        /// Phase 5.6-DL-Q2: while a CLIENT-initiated combat transition is gated (the client walked into an exit),
        /// returns true (once per throttle interval) with the target so NetService can relay it to the host. The
        /// host then performs the transition authoritatively and the gated client follows the host's broadcast.
        /// Stops as soon as the host's matching combat input arrives (the relay worked / host is leading anyway).
        /// </summary>
        public static bool TryConsumeTransitionRelayDue(out string chapter, out int level, out string mode, out string spawn, out int attempt)
        {
            chapter = ""; level = -1; mode = ""; spawn = ""; attempt = 0;

            bool enabled;
            try { enabled = Plugin.Cfg.EnableClientTransitionRelay.Value; }
            catch { enabled = false; }
            if (!enabled) return false;

            lock (_lock)
            {
                if (_mode != NetMode.Client) return false;
                if (_state != State.Waiting) return false;
                if (_pendingKind != PendingKind.Combat) return false;   // only relay client-initiated combat exits
                if (HasMatchingReleaseRequestLocked()) return false;     // host already gave us the target
                if (Now() < _nextRelayTime) return false;

                _nextRelayTime = Now() + SafeRequestInterval();
                _relayAttempt++;
                attempt = _relayAttempt;
                chapter = _pendingChapter;
                level   = _pendingLevel;
                mode    = _pendingLoadingMode;
                spawn   = _pendingSpawn;
                return true;
            }
        }

        private static void TryStartHostDrivenGoToLevel(NetHostSceneRequest req)
        {
            lock (_lock)
            {
                if (_state != State.Waiting) return;

                if (!req.HasLevelSeed)
                {
                    ClientLoadGateMissingSeed++;
                    if (!_missingSeedLogged)
                    {
                        _missingSeedLogged = true;
                        Plugin.Log.Warn("[ClientLoadGate] host generation input has no seed; cannot start host-driven load yet");
                    }
                    _nextRetryTime = Now() + 3f;
                    return;
                }

                _state = State.HostDrivenInProgress;
            }

            int chunks = req.HasUsedSets ? req.UsedChunksThisRun.Count : 0;
            Plugin.Log.Info($"[ClientLoadGate] starting host-driven GoToLevel chapter={req.ChapterName} level={req.LevelIndex} graph={GraphName(req)} seed={req.LevelSeed} usedChunks={chunks}");

            bool ok;
            string result;
            try
            {
                // NetManualSceneFollower.TryFollow applies seed + used sets and invokes GoToLevel under
                // its own reentry guard, so this gate's prefix will not re-intercept the load.
                ok = NetManualSceneFollower.TryFollow(req, out result);
            }
            catch (System.Exception ex)
            {
                ok = false;
                result = ex.GetType().Name + ": " + ex.Message;
            }

            lock (_lock)
            {
                if (ok)
                {
                    ClientLoadGateStartedHostDrivenLoad++;
                    if (_pendingKind == PendingKind.DeathRespawn) ClientLoadGateHubDeathFollowed++;
                    if (_clientInitiated) ClientLoadGateClientLevelCompleteFollowed++;
                    Plugin.Log.Info($"[ClientLoadGate] host-driven GoToLevel invoked: {result}");
                    _state = State.Idle; // load started; next local combat GoToLevel will re-enter the gate
                    _pendingKind = PendingKind.None;
                    _clientInitiated = false;
                    _clientInitiatedFallback = null;
                    // The host-driven follow runs the game's own SwitchLevelRoutine load flow, which keeps the
                    // black fade up through generation and fades back in on completion — so the client-load fade
                    // we showed self-clears here; no explicit Hide() needed.
                    // The gate just drove this target — populate the auto-follow throttle so the TryAutoFollow
                    // call that follows OnHostGenerationInput does not re-drive the same load (double load).
                    _lastAutoFollowKey = req.TargetSceneKey() + "|" + (req.HasLevelSeed ? req.LevelSeed.ToString() : "?") + "|" + GraphName(req);
                    _nextAutoFollowTime = Now() + 5f;
                }
                else
                {
                    ClientLoadGateApplyFailed++;
                    Plugin.Log.Warn($"[ClientLoadGate] host-driven GoToLevel failed: {result}");
                    _state = State.Waiting;        // remain blocked; retry on next input/throttle
                    _nextRetryTime = Now() + 3f;
                }
            }
        }

        // ---- classification ----

        private static LevelKind ClassifyLevelKind(string chapter, string loadingMode, int levelIndex)
        {
            string c = (chapter ?? "").ToLowerInvariant();
            string m = (loadingMode ?? "").ToLowerInvariant();

            if (m.Contains("menu")) return LevelKind.Menu;
            // 5.4-D-1: bare "church" removed — it falsely matched the COMBAT boss chapter Act_03_EndChurch.
            // ChurchHub is still Hub via "hub"; Act_03_EndChurch now correctly classifies as Combat via "act_".
            if (c.Contains("hub") || c.Contains("safezone") || c.Contains("safe_zone")
                || c.Contains("hideout") || c.Contains("town") || c.Contains("vendor")) return LevelKind.Hub;
            if (c.Contains("act_") || c.Contains("act0") || c.Contains("caves") || c.Contains("combat")
                || c.Contains("dungeon") || c.Contains("level")) return LevelKind.Combat;
            return LevelKind.Unknown;
        }

        private static bool HasCombatRequestLocked()
        {
            if (_latestHostRequest == null || !_latestHostRequest.HasTargetScene) return false;
            return ClassifyLevelKind(_latestHostRequest.ChapterName, _latestHostRequest.LoadingMode, _latestHostRequest.LevelIndex) == LevelKind.Combat;
        }

        // Combat gate release: the host advertises a COMBAT scene the client is NOT already in. This handles both
        // cases cleanly: a JOINING/drifted client re-syncs to the host's current scene, and a LEADING client (it
        // walked into an exit) waits for the host to actually reach the new level — it never re-loads the scene the
        // client is currently trying to leave just because the host hasn't transitioned yet (Phase 5.6-DL-Q2).
        private static bool HasCombatReleaseRequestLocked()
        {
            var r = _latestHostRequest;
            if (r == null || !r.HasTargetScene) return false;
            if (ClassifyLevelKind(r.ChapterName, r.LoadingMode, r.LevelIndex) != LevelKind.Combat) return false;
            if (NetRunStateBridge.TryGetLocalRunState(out var run) && run.HasLevel
                && NetSceneName.SameScene(run.ChapterName, run.LevelIndex, r.ChapterName, r.LevelIndex))
                return false; // host is still in the scene we are in — nothing to follow yet
            return true;
        }

        // The current gate's release condition:
        //  - death respawn: the host's CURRENT destination (hub or combat) learned AFTER this death;
        //  - client-led (Phase 5.6-LK): the host has reached the exact target the client is leading to;
        //  - (legacy combat resync): the host advertises a different combat scene.
        private static bool HasMatchingReleaseRequestLocked()
        {
            if (_pendingKind == PendingKind.DeathRespawn) return HasFreshHostDestinationLocked();
            if (_clientInitiated) return HasClientLedReleaseRequestLocked();
            return HasCombatReleaseRequestLocked();
        }

        // Phase 5.6-LK: a client-led transition releases when the host's latest broadcast is heading to / at the
        // exact target the client chose (combat level, in-run next level, or a hub like ChurchHub from an F3 jump),
        // carrying a seed so the generated level/hub can be reproduced. Matching the pending target (not just "a
        // different scene") is what makes leading the host to an arbitrary place — including back to a safe zone —
        // resolve correctly.
        private static bool HasClientLedReleaseRequestLocked()
        {
            var r = _latestHostRequest;
            if (r == null || !r.HasTargetScene || !r.HasLevelSeed) return false;
            if (!NetSceneName.SameScene(r.ChapterName, r.LevelIndex, _pendingChapter, _pendingLevel)) return false;
            // Phase F3-Reload: for a reload-in-place (F3 to the scene we are already in), only release once the host
            // has ACTUALLY re-generated — its request carries a DIFFERENT seed than the one we are reloading FROM.
            // The host's reply to our gen-input request echoes its CURRENT (same) seed with a fresh timestamp, so a
            // timestamp/epoch check alone released us prematurely on the stale seed (Log149: client stayed one seed
            // behind the host). Match the host's NEW-seed reload broadcast; never the same-seed echo. A real MOVE to a
            // different scene keeps releasing on any matching request.
            if (_clientLedReloadInPlace)
            {
                if (_latestHostRequestAt < _clientLedEpoch) return false;     // pre-F3 stale request
                if (_clientLedReloadFromHasSeed && r.LevelSeed == _clientLedReloadFromSeed) return false; // host hasn't re-rolled yet
            }
            return true;
        }

        // Phase 5.6-DL P4: true when the latest host input carries a seed AND was received AFTER this death —
        // i.e. it is the host's current destination (hub on an all-die, or a combat level on an F3/teleport),
        // not the stale pre-death combat request. The epoch filter is what makes following combat safe here.
        private static bool HasFreshHostDestinationLocked()
        {
            var r = _latestHostRequest;
            if (r == null || !r.HasTargetScene || !r.HasLevelSeed) return false;
            return _latestHostRequestAt >= _deathFollowEpoch;
        }

        private static bool IsDeathRespawn(string loadingMode)
            => (loadingMode ?? "").ToLowerInvariant().Contains("death");

        private static bool GateDeathRespawnEnabled()
        {
            try { return Plugin.Cfg.ClientGateDeathRespawnUntilHostHub.Value; }
            catch { return false; }
        }

        private static bool ReloadInPlaceRelayEnabled()
        {
            try { return Plugin.Cfg.EnableClientReloadInPlaceRelay.Value && Plugin.Cfg.EnableClientTransitionRelay.Value; }
            catch { return false; }
        }

        private static float SafeHubDeathTimeout()
        {
            try { return Mathf.Max(1f, Plugin.Cfg.ClientGateDeathRespawnTimeoutSeconds.Value); }
            catch { return 8f; }
        }

        private static float SafeClientInitiatedTimeout()
        {
            try { return Mathf.Max(1f, Plugin.Cfg.ClientInitiatedLoadTimeoutSeconds.Value); }
            catch { return 15f; }
        }

        public static string FormatCounters()
            => $"interceptedCombat={ClientLoadGateInterceptedCombat} clientComplete[intercepted={ClientLoadGateInterceptedClientLevelComplete},followed={ClientLoadGateClientLevelCompleteFollowed},localFallback={ClientLoadGateClientLevelCompleteLocalFallback}] interceptedDeath={ClientLoadGateInterceptedHubDeath} deathFollow[followed={ClientLoadGateHubDeathFollowed},fallback={ClientLoadGateHubDeathFallback}] reqSent={ClientLoadGateHostInputRequestSent} reqRetry={ClientLoadGateHostInputRequestRetry} " +
               $"hostInput={ClientLoadGateHostInputReceived} started={ClientLoadGateStartedHostDrivenLoad} applyFailed={ClientLoadGateApplyFailed} " +
               $"missingSeed={ClientLoadGateMissingSeed} timeouts={ClientLoadGateTimeouts} bypass[hub={ClientLoadGateBypassedHub},menu={ClientLoadGateBypassedMenu}," +
               $"host={ClientLoadGateBypassedHostMode},sp={ClientLoadGateBypassedSinglePlayer},disabled={ClientLoadGateBypassedDisabled},reentry={ClientLoadGateBypassedReentry},unknown={ClientLoadGateBypassedUnknownLevelKind},preserveLocal={ClientLoadGateBypassedPreserveLocalRun}] " +
               $"autoFollow[recv={AutoFollowRequestReceived},started={AutoFollowStarted},disabled={AutoFollowSkippedDisabled},hub={AutoFollowSkippedHub},match={AutoFollowSkippedAlreadyMatching},reentry={AutoFollowSkippedReentry},deathRespawn={AutoFollowSkippedDeathRespawn},failed={AutoFollowFailed}]";

        // ---- helpers ----

        private static float Now() => Time.realtimeSinceStartup;

        private static float SafeTimeout()
        {
            try { return Mathf.Max(1f, Plugin.Cfg.ClientLoadGateTimeoutSeconds.Value); }
            catch { return 30f; }
        }

        private static float SafeRequestInterval()
        {
            try { return Mathf.Max(0.5f, Plugin.Cfg.ClientLoadGateRequestIntervalSeconds.Value); }
            catch { return 2f; }
        }

        private static bool SafeFallback()
        {
            try { return Plugin.Cfg.ClientLoadGateAllowFallbackAfterTimeout.Value; }
            catch { return false; }
        }

        private static string GraphName(NetHostSceneRequest req)
            => string.IsNullOrEmpty(req.GraphName) ? "?" : req.GraphName;
    }
}
