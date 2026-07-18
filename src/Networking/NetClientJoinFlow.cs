namespace SULFURTogether.Networking
{
    /// <summary>
    /// Phase 5.4-A: Client Join Flow. Decides WHEN a Client may auto-join / auto-follow the Host versus
    /// preserving the player's own local run (their own save, an in-progress combat run, or a level-transition
    /// "ESC save" between caves). The guiding rule is conservative: only auto-join from a known-safe state;
    /// in any uncertain state do NOT interrupt the player — just keep the Host request available for a manual
    /// follow. This is the single source of truth for the join policy; NetClientLoadGate / NetService consult it.
    /// </summary>
    internal static class NetClientJoinFlow
    {
        public enum ClientJoinMode
        {
            ManualOnly,
            AutoJoinFromHubOnly,
            AskBeforeLeavingLocalRun,
            ForceAutoJoin,
        }

        public enum ClientLocalJoinState
        {
            HubOrMenu,
            CombatLevel,
            TransitionOrLoading,
            Unknown,
        }

        public enum JoinDecision
        {
            Allow,
            SkipManualOnly,
            SkipPreserveCombat,
            SkipPreserveTransition,
            SkipUnknown,
            ForceWarn,
        }

        // ---- session state ----
        public static bool   SessionJoinedHost { get; private set; }
        public static string JoinedTargetKey  { get; private set; } = "";
        public static string LastSkipReason   { get; private set; } = "";

        // ---- counters (diagnostic) ----
        public static int JoinFlowAutoAllowedFromHub;
        public static int JoinFlowSkippedManualOnly;
        public static int JoinFlowSkippedPreserveCombatRun;
        public static int JoinFlowSkippedPreserveTransition;
        public static int JoinFlowSkippedUnknown;
        public static int JoinFlowForceAutoJoinWarning;
        public static int JoinFlowManualAvailable;
        public static int JoinFlowLocalLoadPreserved;
        public static int JoinFlowSessionJoined;
        public static int JoinFlowSessionLeft;

        // classification log throttle
        private static ClientLocalJoinState _lastLoggedState = (ClientLocalJoinState)(-1);
        private static string _lastLoggedSceneKey = "";
        private static bool _hubReadyLogged;

        public static void Reset()
        {
            SessionJoinedHost = false;
            JoinedTargetKey = "";
            LastSkipReason = "";
            _lastLoggedState = (ClientLocalJoinState)(-1);
            _lastLoggedSceneKey = "";
            _hubReadyLogged = false;
        }

        /// <summary>
        /// P1: when a connected Client sits in a Hub/SafeZone and the Host has not requested combat yet, log
        /// once that it is ready to auto-join as soon as the Host enters combat. Re-armed on join/leave.
        /// </summary>
        public static void NoteHubJoinReady()
        {
            if (SessionJoinedHost || _hubReadyLogged) return;
            if (ClassifyLocalJoinState() != ClientLocalJoinState.HubOrMenu) return;
            _hubReadyLogged = true;
            Plugin.Log.Info("[JoinFlow] hub join ready; waiting host combat request");
        }

        public static ClientJoinMode Mode
        {
            get
            {
                string raw;
                try { raw = Plugin.Cfg.ClientJoinMode.Value; } catch { return ClientJoinMode.AutoJoinFromHubOnly; }
                switch ((raw ?? "").Trim())
                {
                    case "ManualOnly":             return ClientJoinMode.ManualOnly;
                    case "AskBeforeLeavingLocalRun": return ClientJoinMode.AskBeforeLeavingLocalRun;
                    case "ForceAutoJoin":          return ClientJoinMode.ForceAutoJoin;
                    case "AutoJoinFromHubOnly":
                    default:                       return ClientJoinMode.AutoJoinFromHubOnly;
                }
            }
        }

        // ---- P0-A: local join-state classification ----

        public static ClientLocalJoinState ClassifyLocalJoinState()
            => ClassifyLocalJoinState(out _, out _, out _);

        public static ClientLocalJoinState ClassifyLocalJoinState(out string chapter, out int level, out string loadingMode)
        {
            chapter = "<unknown>"; level = -1; loadingMode = "";
            if (!NetRunStateBridge.TryGetLocalRunState(out var run) || !run.HasLevel)
            {
                MaybeLogClassification(ClientLocalJoinState.Unknown, chapter, level, loadingMode);
                return ClientLocalJoinState.Unknown;
            }

            chapter = run.ChapterName;
            level = run.LevelIndex;
            loadingMode = run.LoadingMode;
            var state = Classify(run.ChapterName, run.LoadingMode, run.GameState);
            MaybeLogClassification(state, chapter, level, loadingMode);
            return state;
        }

        private static ClientLocalJoinState Classify(string chapter, string loadingMode, string gameState)
        {
            string c = (chapter ?? "").ToLowerInvariant();
            string m = (loadingMode ?? "").ToLowerInvariant();
            string g = (gameState ?? "").ToLowerInvariant();

            if (m.Contains("menu")) return ClientLocalJoinState.HubOrMenu;

            // Explicit loading/transition signals (best effort; conservative).
            if (g.Contains("loading") || g.Contains("cinematic") || g.Contains("transition")
                || m.Contains("loading") || m.Contains("transition") || m.Contains("nextlevel") || m.Contains("next_level"))
                return ClientLocalJoinState.TransitionOrLoading;

            if (c.Contains("hub") || c.Contains("church") || c.Contains("safezone") || c.Contains("safe_zone")
                || c.Contains("hideout") || c.Contains("town") || c.Contains("vendor"))
                return ClientLocalJoinState.HubOrMenu;

            if (c.Contains("act_") || c.Contains("act0") || c.Contains("caves") || c.Contains("combat")
                || c.Contains("dungeon") || c.Contains("level"))
                return ClientLocalJoinState.CombatLevel;

            return ClientLocalJoinState.Unknown;
        }

        private static void MaybeLogClassification(ClientLocalJoinState state, string chapter, int level, string loadingMode)
        {
            string sceneKey = $"{chapter}:{level}:{loadingMode}";
            if (state == _lastLoggedState && sceneKey == _lastLoggedSceneKey) return;
            _lastLoggedState = state;
            _lastLoggedSceneKey = sceneKey;

            string suffix = state == ClientLocalJoinState.Unknown ? " action=manual-only" : "";
            Plugin.Log.Info($"[JoinFlow] local state classified state={state} chapter={chapter} level={level} loadingMode={loadingMode}{suffix}");
        }

        // ---- P0-B: auto take-over decision (shared by auto-follow) ----

        public static JoinDecision DecideAutoTakeover(ClientLocalJoinState state)
        {
            // Phase 5.6-LK: while LINKED (联机状态 on) the client always follows the host, regardless of its own
            // local state — leading/following the host is the whole point of being linked.
            if (NetLinkState.ClientLinked) return JoinDecision.Allow;
            // Already in the Host session: combat auto-follow to the next level always continues.
            if (SessionJoinedHost) return JoinDecision.Allow;

            switch (Mode)
            {
                case ClientJoinMode.ManualOnly:
                    return JoinDecision.SkipManualOnly;

                case ClientJoinMode.ForceAutoJoin:
                    return JoinDecision.ForceWarn;

                case ClientJoinMode.AskBeforeLeavingLocalRun:
                case ClientJoinMode.AutoJoinFromHubOnly:
                default:
                    if (state == ClientLocalJoinState.HubOrMenu) return JoinDecision.Allow;
                    if (state == ClientLocalJoinState.CombatLevel) return JoinDecision.SkipPreserveCombat;
                    if (state == ClientLocalJoinState.TransitionOrLoading) return JoinDecision.SkipPreserveTransition;
                    return JoinDecision.SkipUnknown;
            }
        }

        /// <summary>
        /// Evaluate whether an auto-follow / auto-join may proceed for the given Host target. Returns true to
        /// proceed. Always logs the decision and (when skipping) that a manual follow is available, plus bumps
        /// the matching counter. Never throws.
        /// </summary>
        public static bool TryAuthorizeAutoFollow(string targetKey, string graph)
        {
            // Phase 5.6-LK: the explicit 联机状态 is the single authority. A LINKED client always follows the host;
            // an UNLINKED client never auto-follows (it is playing its own run). The older ClientJoinMode / local-
            // state preservation logic is subsumed by this user-controlled toggle.
            if (!NetLinkState.ClientLinked)
            {
                LastSkipReason = "not-linked";
                JoinFlowManualAvailable++;
                Plugin.Log.Info($"[JoinFlow] auto-follow skipped reason=not-linked (联机状态 off) target={targetKey}");
                // Connected but unlinked — surface the link key (throttled); the no-hijack skip itself stays.
                SULFURTogether.UI.CoopToasts.NotifyLinkHint();
                return false;
            }

            var state = ClassifyLocalJoinState();
            var decision = DecideAutoTakeover(state);
            var mode = Mode;

            switch (decision)
            {
                case JoinDecision.Allow:
                    LastSkipReason = "";
                    if (SessionJoinedHost)
                        Plugin.Log.Info($"[JoinFlow] auto-follow allowed (in session) mode={mode} state={state} target={targetKey} graph={Graph(graph)}");
                    else
                    {
                        JoinFlowAutoAllowedFromHub++;
                        Plugin.Log.Info($"[JoinFlow] auto-join allowed mode={mode} state={state} target={targetKey} graph={Graph(graph)}");
                    }
                    return true;

                case JoinDecision.ForceWarn:
                    LastSkipReason = "";
                    JoinFlowForceAutoJoinWarning++;
                    Plugin.Log.Warn($"[JoinFlow] auto-join FORCED mode=ForceAutoJoin state={state} target={targetKey} graph={Graph(graph)} (test-only; may interrupt local run)");
                    return true;

                case JoinDecision.SkipManualOnly:
                    JoinFlowSkippedManualOnly++;
                    LogSkip(mode, state, "manual-only", targetKey);
                    return false;

                case JoinDecision.SkipPreserveCombat:
                    JoinFlowSkippedPreserveCombatRun++;
                    LogSkip(mode, state, "preserve-local-run", targetKey);
                    return false;

                case JoinDecision.SkipPreserveTransition:
                    JoinFlowSkippedPreserveTransition++;
                    LogSkip(mode, state, "preserve-transition-save", targetKey);
                    return false;

                case JoinDecision.SkipUnknown:
                default:
                    JoinFlowSkippedUnknown++;
                    LogSkip(mode, state, "unknown-state-manual-only", targetKey);
                    return false;
            }
        }

        private static void LogSkip(ClientJoinMode mode, ClientLocalJoinState state, string reason, string targetKey)
        {
            LastSkipReason = reason;
            Plugin.Log.Info($"[JoinFlow] auto-join skipped mode={mode} state={state} reason={reason}");
            JoinFlowManualAvailable++;
            Plugin.Log.Info($"[JoinFlow] manual follow available target={targetKey}");
        }

        // ---- gate: should the Client's OWN combat GoToLevel be intercepted/taken over? ----
        // When not joined we never intercept the player's own load (preserve local run / transition save).
        // Joining happens via auto-follow (Host request while safe) or manual follow, not by hijacking loads.
        public static bool ShouldGateOwnCombatLoad(out string reason)
        {
            // Phase 5.6-LK: superseded by the explicit 联机状态. Linked → intercept/relay the client's own loads;
            // unlinked → preserve the local run. (Kept for any legacy callers; the gate now checks ClientLinked.)
            if (NetLinkState.ClientLinked) { reason = "linked"; return true; }
            JoinFlowLocalLoadPreserved++;
            reason = "not-linked-preserve-local-run";
            return false;
        }

        // ---- P0-C: session join/leave ----

        public static void MarkJoinedHost(string targetKey, string graph, string seed)
        {
            if (SessionJoinedHost && JoinedTargetKey == targetKey) return;
            bool wasJoined = SessionJoinedHost;
            SessionJoinedHost = true;
            JoinedTargetKey = targetKey;
            if (!wasJoined)
            {
                JoinFlowSessionJoined++;
                Plugin.Log.Info($"[JoinFlow] session joined host target={targetKey} graph={Graph(graph)} seed={(string.IsNullOrEmpty(seed) ? "?" : seed)}");
            }
        }

        public static void LeaveSession(string reason)
        {
            if (!SessionJoinedHost) return;
            SessionJoinedHost = false;
            JoinedTargetKey = "";
            _hubReadyLogged = false;
            JoinFlowSessionLeft++;
            Plugin.Log.Info($"[JoinFlow] session left reason={reason}");
        }

        // ---- P1: status / future-UI readable snapshot ----

        public static string FormatStatus(string latestHostTarget)
        {
            var state = ClassifyLocalJoinState();
            string latest = string.IsNullOrWhiteSpace(latestHostTarget) ? "none" : latestHostTarget;
            string skip = string.IsNullOrWhiteSpace(LastSkipReason) ? "none" : LastSkipReason;
            return $"JoinFlow mode={Mode} state={state} joined={SessionJoinedHost} latestHost={latest} skipped={skip}";
        }

        public static string FormatCounters()
            => $"autoAllowedHub={JoinFlowAutoAllowedFromHub} skipManual={JoinFlowSkippedManualOnly} " +
               $"skipCombat={JoinFlowSkippedPreserveCombatRun} skipTransition={JoinFlowSkippedPreserveTransition} " +
               $"skipUnknown={JoinFlowSkippedUnknown} forceWarn={JoinFlowForceAutoJoinWarning} " +
               $"manualAvail={JoinFlowManualAvailable} localLoadPreserved={JoinFlowLocalLoadPreserved} " +
               $"joined={JoinFlowSessionJoined} left={JoinFlowSessionLeft}";

        private static string Graph(string g) => string.IsNullOrEmpty(g) ? "?" : g;
    }
}
