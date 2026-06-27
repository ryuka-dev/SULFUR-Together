using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using SULFURTogether.Networking.Gameplay;

namespace SULFURTogether.Networking
{
    /// <summary>
    /// Network service for connection, handshake, session metadata, scene/run metadata,
    /// visual proxy packets, and explicitly enabled experimental Phase 4 gameplay probes.
    /// Dead when EnableNetworking=false.
    /// Call Tick() from Plugin.Update() (Unity main thread).
    /// </summary>
    public class NetService
    {
        private NetManager?            _net;
        private EventBasedNetListener? _listener;
        private NetMode                _mode;
        private float                  _lastPingTime;
        private float                  _lastStatusTime;
        private float                  _lastRunStateSendTime;
        private float                  _nextClientReconnectTime;
        private int                    _clientReconnectAttempts;
        private bool                   _clientConnectInProgress;
        private NetPeer?               _hostPeer; // client: connection to host

        private readonly List<NetPeer> _clients = new List<NetPeer>(); // host: connected clients
        private readonly Dictionary<NetPeer, string> _peerIds = new Dictionary<NetPeer, string>();
        private readonly NetSessionManager _sessions = new NetSessionManager();
        private readonly NetRunStateManager _runStates = new NetRunStateManager();
        private readonly NetSceneRequestManager _sceneRequests = new NetSceneRequestManager();
        private readonly NetLocalPlayerTracker _localPlayer = new NetLocalPlayerTracker();
        private readonly NetRemotePlayerProxyManager _visualProxies = new NetRemotePlayerProxyManager();
        private readonly RemotePlayerTargetProxyManager _targetProxies = new RemotePlayerTargetProxyManager(); // P3-A2: host-only enemy-targetable proxies
        internal static readonly Gameplay.RemotePlayerRegistryManager PlayerRegistry = new Gameplay.RemotePlayerRegistryManager(); // Plan B: host-only headless Player registry + activation
        private readonly System.Collections.Generic.List<UnityEngine.Vector3> _remoteInterestScratch = new System.Collections.Generic.List<UnityEngine.Vector3>(); // P1: reused per snapshot tick
        private readonly HashSet<string> _receivedEnemyDeathEvents = new HashSet<string>();
        private readonly HashSet<string> _receivedClientEnemyDeathClaims = new HashSet<string>();
        private float _lastPlayerVisualSendTime;
        private float _lastLevelSeedPollTime;
        private float _lastEnemyStateSnapshotSendTime;
        private float _lastEnemyStateSnapshotSendErrorLogTime;
        private float _lastInterestFeedLogAt; // RB4 diag throttle
        private bool _enemyStateSnapshotPacketClampWarningShown;
        private int   _playerVisualSequence;
        private int   _enemyStateSnapshotSequence;
        private bool  _manualFollowKeyWarningShown;
        // Phase 4.4.0-O3-B: Host world roster
        private float _lastWorldRosterSendTime;
        private int   _lastWorldRosterSentRevision = -1;

        public void Start(NetMode mode)
        {
            _mode = mode;
            NetClientLoadGate.SetMode(mode);
            _sessions.Clear();
            _runStates.Clear();
            _sceneRequests.Clear();
            _localPlayer.Clear();
            _visualProxies.Clear();
            Gameplay.PlayerHeldWeaponManager.Reset();
            _targetProxies.Clear();
            PlayerRegistry.Clear();
            _receivedEnemyDeathEvents.Clear();
            _receivedClientEnemyDeathClaims.Clear();
            _peerIds.Clear();
            _clients.Clear();
            _hostPeer = null;
            _lastPingTime = 0f;
            _lastStatusTime = 0f;
            _lastRunStateSendTime = 0f;
            _nextClientReconnectTime = 0f;
            _clientReconnectAttempts = 0;
            _clientConnectInProgress = false;
            _lastPlayerVisualSendTime = 0f;
            _lastLevelSeedPollTime = 0f;
            _lastEnemyStateSnapshotSendTime = 0f;
            _lastEnemyStateSnapshotSendErrorLogTime = 0f;
            _enemyStateSnapshotPacketClampWarningShown = false;
            _playerVisualSequence = 0;
            _enemyStateSnapshotSequence = 0;
            _snapshotChunkBytesMax = 0;
            _snapshotChunkSplit = 0;
            _snapshotChunkTooLargeRejected = 0;
            _snapshotChunkSendFailed = 0;
            _manualFollowKeyWarningShown = false;
            NetLoadBarrier.Reset();
            NetClientJoinFlow.Reset();
            NetLinkState.InitFromConfig();
            Gameplay.Boss.NetBossEncounterManager.Reset();
            Gameplay.Boss.BossDynamicSpawnManifest.Reset();

            _listener = new EventBasedNetListener();
            _listener.ConnectionRequestEvent += OnConnectionRequest;
            _listener.PeerConnectedEvent     += OnPeerConnected;
            _listener.NetworkReceiveEvent    += OnNetworkReceive;
            _listener.PeerDisconnectedEvent  += OnPeerDisconnected;

            _net = new NetManager(_listener) { AutoRecycle = true };

            if (mode == NetMode.Host)
            {
                _sessions.RegisterLocalHost(Plugin.Cfg.PlayerName.Value, ModInfo.Version, Now());
                _runStates.SetLocalIdentity("host", Plugin.Cfg.PlayerName.Value);
                _net.Start(Plugin.Cfg.HostPort.Value);
                NetLogger.Info($"[Net] Host started on port {Plugin.Cfg.HostPort.Value}");
                NetLogger.Info($"[Session] Host session ready: {_sessions.FormatStatus()}");
                if (Plugin.Cfg.EnableHostSceneAuthority.Value)
                    NetLogger.Info("[SceneAuthority] Host scene authority enabled (metadata-only, warning-only).");
                if (Plugin.Cfg.EnableHostSceneRequestProtocol.Value)
                    NetLogger.Info("[SceneRequest] HostSceneRequest protocol enabled (request/ack/refused only; no auto-load).");
                if (Plugin.Cfg.EnableLevelSeedAuthority.Value)
                    NetLogger.Info("[LevelSeed] Host level seed authority enabled (metadata/probe based).");
                if (Plugin.Cfg.EnableRemotePlayerVisualProxy.Value)
                    NetLogger.Info("[RemotePlayer] Visual proxy enabled (local-only GameObjects, no gameplay sync).");
                if (Plugin.Cfg.EnableHostEnemyDeathEventMirror.Value)
                    NetLogger.Info("[EnemyDeathMirror] Host enemy death event mirror enabled (host sends NPC death events).");
                if (Plugin.Cfg.EnableClientEnemyDeathClaim.Value)
                    NetLogger.Info($"[ClientDeathClaim] Host client enemy death claim receive enabled (apply={Plugin.Cfg.ApplyReceivedClientEnemyDeathClaimsOnHost.Value}).");
                if (Plugin.Cfg.EnableHostEnemyStateSnapshotMirror.Value)
                    NetLogger.Info($"[EnemyStateMirror] Host enemy state snapshot mirror enabled (rate={Plugin.Cfg.EnemyStateSnapshotSendRateHz.Value}Hz, apply={Plugin.Cfg.ApplyReceivedEnemyStateSnapshots.Value}).");
                if (Plugin.Cfg.EnableCoopPlayerDownedRevive.Value)
                    NetLogger.Info($"[PlayerLife] Host co-op downed/revive enabled (timeout={Plugin.Cfg.PlayerDownedRescueTimeoutSeconds.Value}s, reviveHold={Plugin.Cfg.PlayerReviveHoldSeconds.Value}s, reviveDistance={Plugin.Cfg.PlayerReviveDistance.Value}m).");
            }
            else
            {
                _runStates.SetLocalIdentity("client-local", Plugin.Cfg.PlayerName.Value);
                _net.Start();
                if (Plugin.Cfg.EnableHostSceneAuthority.Value)
                    NetLogger.Info("[SceneAuthority] Client will treat host scene metadata as authoritative (warning-only).");
                if (Plugin.Cfg.EnableHostSceneRequestProtocol.Value)
                    NetLogger.Info("[SceneRequest] ClientSceneAck/Refused protocol enabled (reply-only; no auto-load).");
                if (Plugin.Cfg.EnableManualClientSceneFollow.Value)
                    NetLogger.Info($"[SceneFollow] Manual client scene follow enabled. Press {Plugin.Cfg.ManualClientSceneFollowKey.Value} after HostSceneRequest.");
                if (Plugin.Cfg.EnableLevelSeedAuthority.Value)
                    NetLogger.Info("[LevelSeed] Client will compare host level seed when known.");
                if (Plugin.Cfg.EnableRemotePlayerVisualProxy.Value)
                    NetLogger.Info("[RemotePlayer] Visual proxy enabled (local-only GameObjects, no gameplay sync).");
                if (Plugin.Cfg.EnableHostEnemyDeathEventMirror.Value)
                    NetLogger.Info($"[EnemyDeathMirror] Client enemy death event mirror receive enabled (apply={Plugin.Cfg.ApplyReceivedEnemyDeathEvents.Value}).");
                if (Plugin.Cfg.EnableClientEnemyDeathClaim.Value)
                    NetLogger.Info("[ClientDeathClaim] Client enemy death claim send enabled (local NPC deaths are reported to Host for safe authoritative apply).");
                if (Plugin.Cfg.EnableHostEnemyStateSnapshotMirror.Value)
                    NetLogger.Info($"[EnemyStateMirror] Client enemy state snapshot mirror receive enabled (apply={Plugin.Cfg.ApplyReceivedEnemyStateSnapshots.Value}).");
                if (Plugin.Cfg.EnableCoopPlayerDownedRevive.Value)
                    NetLogger.Info($"[PlayerLife] Client co-op downed/revive enabled (timeout={Plugin.Cfg.PlayerDownedRescueTimeoutSeconds.Value}s, reviveHold={Plugin.Cfg.PlayerReviveHoldSeconds.Value}s, reviveDistance={Plugin.Cfg.PlayerReviveDistance.Value}m).");
                ConnectToHost(initial: true);
            }
        }

        public void Stop()
        {
            _net?.Stop();
            _net      = null;
            _listener = null;
            _hostPeer = null;
            NetClientLoadGate.SetMode(NetMode.Off);
            NetGenerationInputCapture.Clear();
            NetLoadBarrier.Reset();
            NetClientJoinFlow.LeaveSession("networking stopped");
            NetClientJoinFlow.Reset();
            NetLinkState.ResetClient("networking stopped");
            NetHostTransitionGuard.Reset();
            Gameplay.Boss.NetBossEncounterManager.Reset();
            Gameplay.Boss.BossDynamicSpawnManifest.Reset();
            _clientConnectInProgress = false;
            _nextClientReconnectTime = 0f;
            _clientReconnectAttempts = 0;
            _lastEnemyStateSnapshotSendTime = 0f;
            _lastEnemyStateSnapshotSendErrorLogTime = 0f;
            _enemyStateSnapshotPacketClampWarningShown = false;
            _enemyStateSnapshotSequence = 0;
            _manualFollowKeyWarningShown = false;
            _lastWorldRosterSendTime = 0f;
            _lastWorldRosterSentRevision = -1;
            _clients.Clear();
            _peerIds.Clear();
            _sessions.Clear();
            _runStates.Clear();
            _sceneRequests.Clear();
            _localPlayer.Clear();
            _visualProxies.Clear();
            Gameplay.PlayerHeldWeaponManager.Reset();
            _targetProxies.Clear();
            PlayerRegistry.Clear();
            _receivedEnemyDeathEvents.Clear();
            _receivedClientEnemyDeathClaims.Clear();
        }

        /// <summary>Must be called every frame from Plugin.Update().</summary>
        public void Tick()
        {
            _net?.PollEvents();
            NetClientLoadGate.UpdateNetState(_mode, _hostPeer != null);
            NetClientLoadGate.Tick();
            HandleClientLoadGateRequestTimer();
            HandleClientReconnectTimer();
            HandleManualClientSceneFollowInput();
            HandlePingTimer();
            HandleLevelSeedPollTimer();
            HandleRunStateTimer();
            HandleRemotePlayerVisualProxyTimer();
            HandleEnemyStateSnapshotTimer();
            HandleWorldRosterTimer();
            if (_mode == NetMode.Host) NetLoadBarrier.Tick();
            _visualProxies.Tick(
                UnityEngine.Time.deltaTime,
                Now(),
                Plugin.Cfg.RemotePlayerVisualTimeoutSeconds.Value,
                Plugin.Cfg.RemotePlayerVisualInterpolationSpeed.Value,
                Plugin.Cfg.RemotePlayerVisualSnapDistance.Value);
            // P3-A2: host maintains enemy-targetable Unit proxies at each remote player (so enemies aggro clients).
            if (_mode == NetMode.Host)
                _targetProxies.Tick(_visualProxies, Now(), Plugin.Cfg.RemotePlayerVisualTimeoutSeconds.Value);

            // Plan B: host registers each remote player in GameManager.Players (native detection) and feeds the
            // remote-position buffer the NpcUpdateManager.LateUpdate activation postfix reads.
            if (_mode == NetMode.Host)
                PlayerRegistry.Tick(_visualProxies, Now(), Plugin.Cfg.RemotePlayerVisualTimeoutSeconds.Value);

            // WS-2: broadcast local held weapon on change + rebuild/attach remote weapon models.
            if (_mode != NetMode.Off)
                Gameplay.PlayerHeldWeaponManager.Tick(_visualProxies);

            // World item-drop sync: drain queued pickup removals (AddItem + remove-from-world) on the main thread.
            if (_mode != NetMode.Off)
                Gameplay.WorldPickupManager.Tick();

            // WS-3: give remote proxies a billboard body (visual only). Priest sprite body takes priority; NPC-prefab body
            // is the fallback (only used if the sprite body is disabled/unavailable).
            if (_mode != NetMode.Off)
            {
                if (Plugin.Cfg.EnableRemotePlayerSpriteBody.Value)
                    Gameplay.RemotePlayerSpriteBody.SyncProxyBodies(_visualProxies);
                else if (Plugin.Cfg.EnableRemotePlayerNpcBody.Value)
                    Gameplay.RemotePlayerBodyManager.SyncProxyBodies(_visualProxies);
            }

            HandleStatusTimer();
        }

        /// <summary>Physics-step update. Soft player-vs-player collision nudges the local player's RIGIDBODY out of
        /// overlap here (FixedUpdate) so it doesn't fight/desync the Rigidbody-based CMF controller (which caused the
        /// twitching when we moved the transform in Update).</summary>
        public void FixedTick()
        {
            if (Plugin.Cfg.EnableRemotePlayerProxyCollision.Value && Plugin.Cfg.RemotePlayerCollisionSoft.Value
                && _localPlayer.HasTransform && _localPlayer.LocalTransform != null)
            {
                _visualProxies.ApplySoftCollision(
                    _localPlayer.LocalTransform,
                    Plugin.Cfg.RemotePlayerSoftCollisionRadius.Value,
                    Plugin.Cfg.RemotePlayerSoftCollisionPushSpeed.Value,
                    UnityEngine.Time.fixedDeltaTime);
            }
        }

        // ---- client reconnect timer ----

        private void ConnectToHost(bool initial)
        {
            if (_net == null || _mode != NetMode.Client) return;
            if (_hostPeer != null || _clientConnectInProgress) return;

            _clientConnectInProgress = true;
            _clientReconnectAttempts++;

            _net.Connect(
                Plugin.Cfg.HostAddress.Value,
                Plugin.Cfg.HostPort.Value,
                Plugin.Cfg.ConnectionKey.Value);

            if (initial)
                NetLogger.Info($"[Net] Client connecting to {Plugin.Cfg.HostAddress.Value}:{Plugin.Cfg.HostPort.Value}");
            else
                NetLogger.Info($"[Net] Client reconnecting to {Plugin.Cfg.HostAddress.Value}:{Plugin.Cfg.HostPort.Value} attempt={_clientReconnectAttempts}");
        }

        private void HandleClientReconnectTimer()
        {
            if (_mode != NetMode.Client || _net == null) return;
            if (_hostPeer != null || _clientConnectInProgress) return;

            float now = Now();
            if (now < _nextClientReconnectTime) return;

            _nextClientReconnectTime = now + 5f;
            ConnectToHost(initial: false);
        }

        // ---- ping timer ----

        private void HandlePingTimer()
        {
            if (_mode == NetMode.Off || _net == null) return;
            float now = Now();
            if (now - _lastPingTime < Plugin.Cfg.SendPingIntervalSeconds.Value) return;
            _lastPingTime = now;
            SendPingToAll();
        }

        private void SendPingToAll()
        {
            var w = NetMessage.For(NetMessageType.Ping);
            if (_mode == NetMode.Host)
            {
                foreach (var peer in _clients) peer.Send(w, DeliveryMethod.Unreliable);
            }
            else if (_hostPeer != null)
            {
                _hostPeer.Send(w, DeliveryMethod.Unreliable);
            }
        }

        private void HandleStatusTimer()
        {
            if (_mode == NetMode.Off || _net == null) return;
            float now = Now();
            if (now - _lastStatusTime < 30f) return;
            _lastStatusTime = now;

            string authority = Plugin.Cfg.EnableHostSceneAuthority.Value ? $" {_runStates.FormatAuthorityStatus(_mode)}" : "";
            string sceneReq = Plugin.Cfg.EnableHostSceneRequestProtocol.Value ? $" {_sceneRequests.FormatStatus()}" : "";
            string visual = Plugin.Cfg.EnableRemotePlayerVisualProxy.Value ? $" {_localPlayer.FormatStatus()} {_visualProxies.FormatStatus()}" : "";

            string chunkStats = _mode == NetMode.Host
                ? $" snapshotChunkBytesMax={_snapshotChunkBytesMax} snapshotChunkSplit={_snapshotChunkSplit} snapshotChunkTooLarge={_snapshotChunkTooLargeRejected} snapshotChunkSendFailed={_snapshotChunkSendFailed}"
                : "";
            if (_mode == NetMode.Host)
                NetLogger.Info($"[Net] Status: mode=Host peers={_clients.Count} {NetLinkState.FormatStatus()} {_sessions.FormatStatus()} {_runStates.FormatStatus()}{authority}{sceneReq}{visual}{chunkStats} runStateSync[{NetGenerationInputCapture.FormatRunStateSyncCounters()}] {NetLoadBarrier.FormatStatus()} loadBarrier[{NetLoadBarrier.FormatCounters()}] hubReturn[{NetSceneFollowDiag.FormatHubReturn()}] transition[{NetGenerationInputCapture.FormatTransitionCounters()}] graphId[{NetSceneFollowDiag.FormatGraphIdentity()}] boss[{Gameplay.Boss.NetBossEncounterManager.FormatCounters()}] bossSpawn[{Gameplay.Boss.BossDynamicSpawnManifest.FormatCounters()}]");
            else
            {
                string latestHostTarget = _sceneRequests.HasLastHostRequestTarget(out var lastReq) ? lastReq.TargetSceneKey() : "";
                NetLogger.Info($"[Net] Status: mode=Client connected={(_hostPeer != null)} {NetLinkState.FormatStatus()} {_sessions.FormatStatus()} {_runStates.FormatStatus()}{authority}{sceneReq}{visual} runStateSync[{NetGenerationInputCapture.FormatRunStateSyncCounters()}] {NetClientJoinFlow.FormatStatus(latestHostTarget)} joinFlow[{NetClientJoinFlow.FormatCounters()}] SessionReturn[{NetClientLoadGate.FormatSessionReturnCounters()}] hubReturn[{NetSceneFollowDiag.FormatHubReturn()}] transition[{NetGenerationInputCapture.FormatTransitionCounters()}] graphId[{NetSceneFollowDiag.FormatGraphIdentity()}] boss[{Gameplay.Boss.NetBossEncounterManager.FormatCounters()}] bossSpawn[{Gameplay.Boss.BossDynamicSpawnManifest.FormatCounters()}] loadGate[{NetClientLoadGate.FormatCounters()}]");
            }
        }

        // ---- run/scene state metadata ----

        public void ReportLocalGoToLevel(string chapterName, int levelIndex, string loadingMode, string spawnIdentifier)
        {
            if (!Plugin.Cfg.EnableRunStateNegotiation.Value) return;
            _localPlayer.Clear();
            _visualProxies.Clear();
            Gameplay.PlayerHeldWeaponManager.Reset();
            _targetProxies.Clear();
            PlayerRegistry.Clear();
            _receivedEnemyDeathEvents.Clear();
            _receivedClientEnemyDeathClaims.Clear();
            _enemyStateSnapshotSequence = 0;
            _lastEnemyStateSnapshotSendTime = 0f;
            _lastEnemyStateSnapshotSendErrorLogTime = 0f;
            _playerVisualSequence = 0;

            var state = _runStates.UpdateLocalGoToLevel(chapterName, levelIndex, loadingMode, spawnIdentifier, Now());
            // 5.4-E: clear boss registry/dedup on level change. fullSession:false preserves per-encounter state keyed by
            // chapter:level:seed (room membership / fight-committed) so a same-level GoToLevel churn doesn't drop it
            // (Log134); a genuine level change still clears it via OnLevelChanged's runScope comparison.
            Gameplay.Boss.NetBossEncounterManager.Reset(fullSession: false);
            Gameplay.Boss.BossDynamicSpawnManifest.Reset(); // 5.4-E4: clear dynamic spawn manifest on level change
            NetLogger.Info($"[RunState] Local GoToLevel: {state.ToCompactString()} mode={loadingMode} spawn={spawnIdentifier}");
            SendLocalRunStateToConnectedPeers();
            TrySendClientSceneAckForLastHostRequest("local GoToLevel");
        }

        public void ReportLocalGameState(string gameState)
        {
            if (!Plugin.Cfg.EnableRunStateNegotiation.Value) return;
            var state = _runStates.UpdateLocalGameState(gameState, Now());
            if (Plugin.Cfg.EnableDebugLog.Value)
                NetLogger.Debug($"[RunState] Local GameState: {state.ToCompactString()}");
            SendLocalRunStateToConnectedPeers();
            TrySendClientSceneAckForLastHostRequest("local GameState");
        }

        public void ReportLocalClearLevel()
        {
            if (!Plugin.Cfg.EnableRunStateNegotiation.Value) return;
            _runStates.UpdateLocalClearLevel(Now());
            _localPlayer.Clear();
            _visualProxies.Clear();
            Gameplay.PlayerHeldWeaponManager.Reset();
            _targetProxies.Clear();
            PlayerRegistry.Clear();
            _receivedEnemyDeathEvents.Clear();
            _receivedClientEnemyDeathClaims.Clear();
            _enemyStateSnapshotSequence = 0;
            _lastEnemyStateSnapshotSendTime = 0f;
            _lastEnemyStateSnapshotSendErrorLogTime = 0f;
            if (Plugin.Cfg.EnableDebugLog.Value)
                NetLogger.Debug("[RunState] Local ClearLevel observed; local player/proxies cleared");
            SendLocalRunStateToConnectedPeers();
        }

        public void ReportLocalLevelSeed(int seed, string generatorName)
        {
            if (!Plugin.Cfg.EnableRunStateNegotiation.Value) return;
            if (!Plugin.Cfg.EnableLevelSeedAuthority.Value) return;
            if (_runStates.LocalState.HasLevelSeed && _runStates.LocalState.LevelSeed == seed) return;

            var state = _runStates.UpdateLocalLevelSeed(seed, generatorName, Now());
            _visualProxies.HideAll();
            NetLogger.Info($"[LevelSeed] Local level seed captured: generator={generatorName} seed={seed} state={state.ToCompactString()}");
            SendLocalRunStateToConnectedPeers();
            SendHostSceneRequestsForDriftedClients();
            TrySendClientSceneAckForLastHostRequest("local LevelSeed");
        }

        public void ReportLocalPlayerObject(object player)
        {
            if (!Plugin.Cfg.EnableRemotePlayerVisualProxy.Value) return;
            if (_localPlayer.TrySetLocalPlayerObject(player, out var description))
                NetLogger.Info($"[RemotePlayer] Local player transform captured: {description}");
            else if (Plugin.Cfg.EnableDebugLog.Value)
                NetLogger.Debug("[RemotePlayer] Local player transform capture failed");
        }

        public NetRunState GetLocalRunStateSnapshot()
        {
            return _runStates.LocalState.Clone();
        }

        /// <summary>Phase PF-0: local-vs-remote scene+seed convergence summary for the boss pre-fight probe.</summary>
        public string FormatBossConvergence(out bool allConverged)
        {
            return _runStates.FormatBossConvergence(out allConverged, out _, out _);
        }

        /// <summary>Phase 5.4-E: current network role, used by the Boss encounter manager.</summary>
        public NetMode Mode => _mode;

        // ---- Phase 5.4-E Boss encounter authority ----

        internal void SendClientBossStartRequest(Gameplay.Boss.NetClientBossStartRequest req)
        {
            if (_mode != NetMode.Client || _net == null || _hostPeer == null) return;
            if (!Plugin.Cfg.EnableBossEncounterSync.Value) return;
            if (req == null) return;
            try
            {
                var w = NetMessage.For(NetMessageType.ClientBossStartRequest);
                Gameplay.Boss.NetBossEncounterCodec.WriteRequest(w, req);
                _hostPeer.Send(w, DeliveryMethod.ReliableOrdered);
            }
            catch (Exception ex)
            {
                NetLogger.Warn($"[BossEncounter] failed to send ClientBossStartRequest: {ex.Message}");
            }
        }

        internal void BroadcastHostBossEncounterStart(Gameplay.Boss.NetBossEncounterState state)
        {
            if (_mode != NetMode.Host || _net == null) return;
            if (!Plugin.Cfg.EnableBossEncounterSync.Value) return;
            if (state == null || _clients.Count == 0) return;
            foreach (var peer in _clients.ToArray())
            {
                try
                {
                    var w = NetMessage.For(NetMessageType.HostBossEncounterStart);
                    Gameplay.Boss.NetBossEncounterCodec.WriteState(w, state);
                    peer.Send(w, DeliveryMethod.ReliableOrdered);
                }
                catch (Exception ex)
                {
                    NetLogger.Warn($"[BossEncounter] failed to send HostBossEncounterStart: {ex.Message}");
                }
            }
        }

        private void HandleClientBossStartRequest(NetPeer peer, NetDataReader reader)
        {
            if (_mode != NetMode.Host) return;
            if (!Plugin.Cfg.EnableBossEncounterSync.Value) return;
            if (!Gameplay.Boss.NetBossEncounterCodec.TryReadRequest(reader, out var req))
            {
                NetLogger.Warn("[BossEncounter] malformed ClientBossStartRequest packet");
                return;
            }
            string peerId = _peerIds.TryGetValue(peer, out var mapped) ? mapped : peer.Address.ToString();
            req.ClientPeerId = peerId;
            Gameplay.Boss.NetBossEncounterManager.HandleClientBossStartRequest(req, peerId);
        }

        private void HandleHostBossEncounterStart(NetPeer peer, NetDataReader reader)
        {
            if (_mode != NetMode.Client) return;
            if (!Plugin.Cfg.EnableBossEncounterSync.Value) return;
            if (!Gameplay.Boss.NetBossEncounterCodec.TryReadState(reader, out var state))
            {
                NetLogger.Warn("[BossEncounter] malformed HostBossEncounterStart packet");
                return;
            }
            Gameplay.Boss.NetBossEncounterManager.HandleHostBossEncounterStart(state);
        }

        // ---- Phase 5.4-E3 Boss dialog commit + boss state ----

        internal void SendClientBossDialogCommitRequest(Gameplay.Boss.NetBossDialogCommit msg)
        {
            if (_mode != NetMode.Client || _net == null || _hostPeer == null) return;
            if (!Plugin.Cfg.EnableBossEncounterSync.Value || msg == null) return;
            try
            {
                var w = NetMessage.For(NetMessageType.ClientBossDialogCommitRequest);
                Gameplay.Boss.NetBossEncounterCodec.WriteDialogCommit(w, msg);
                _hostPeer.Send(w, DeliveryMethod.ReliableOrdered);
            }
            catch (Exception ex) { NetLogger.Warn($"[BossDialogCommit] failed to send request: {ex.Message}"); }
        }

        internal void BroadcastHostBossDialogCommit(Gameplay.Boss.NetBossDialogCommit msg)
        {
            if (_mode != NetMode.Host || _net == null) return;
            if (!Plugin.Cfg.EnableBossEncounterSync.Value || msg == null || _clients.Count == 0) return;
            foreach (var peer in _clients.ToArray())
            {
                try
                {
                    var w = NetMessage.For(NetMessageType.HostBossDialogCommit);
                    Gameplay.Boss.NetBossEncounterCodec.WriteDialogCommit(w, msg);
                    peer.Send(w, DeliveryMethod.ReliableOrdered);
                }
                catch (Exception ex) { NetLogger.Warn($"[BossDialogCommit] failed to broadcast: {ex.Message}"); }
            }
        }

        internal void BroadcastHostBossState(Gameplay.Boss.NetBossState msg)
        {
            if (_mode != NetMode.Host || _net == null) return;
            if (!Plugin.Cfg.EnableBossEncounterSync.Value || msg == null || _clients.Count == 0) return;
            foreach (var peer in _clients.ToArray())
            {
                try
                {
                    var w = NetMessage.For(NetMessageType.HostBossState);
                    Gameplay.Boss.NetBossEncounterCodec.WriteBossState(w, msg);
                    peer.Send(w, DeliveryMethod.ReliableOrdered);
                }
                catch (Exception ex) { NetLogger.Warn($"[BossState] failed to broadcast: {ex.Message}"); }
            }
        }

        private void HandleClientBossDialogCommitRequest(NetPeer peer, NetDataReader reader)
        {
            if (_mode != NetMode.Host) return;
            if (!Plugin.Cfg.EnableBossEncounterSync.Value) return;
            if (!Gameplay.Boss.NetBossEncounterCodec.TryReadDialogCommit(reader, out var msg))
            {
                NetLogger.Warn("[BossDialogCommit] malformed ClientBossDialogCommitRequest packet");
                return;
            }
            string peerId = _peerIds.TryGetValue(peer, out var mapped) ? mapped : peer.Address.ToString();
            Gameplay.Boss.NetBossEncounterManager.HandleClientBossDialogCommitRequest(msg, peerId);
        }

        private void HandleHostBossDialogCommit(NetPeer peer, NetDataReader reader)
        {
            if (_mode != NetMode.Client) return;
            if (!Plugin.Cfg.EnableBossEncounterSync.Value) return;
            if (!Gameplay.Boss.NetBossEncounterCodec.TryReadDialogCommit(reader, out var msg))
            {
                NetLogger.Warn("[BossDialogCommit] malformed HostBossDialogCommit packet");
                return;
            }
            Gameplay.Boss.NetBossEncounterManager.HandleHostBossDialogCommit(msg);
        }

        private void HandleHostBossState(NetPeer peer, NetDataReader reader)
        {
            if (_mode != NetMode.Client) return;
            if (!Plugin.Cfg.EnableBossEncounterSync.Value) return;
            if (!Gameplay.Boss.NetBossEncounterCodec.TryReadBossState(reader, out var msg))
            {
                NetLogger.Warn("[BossState] malformed HostBossState packet");
                return;
            }
            Gameplay.Boss.NetBossEncounterManager.HandleHostBossState(msg);
        }

        // ---- Phase RM: room-membership substrate ----
        internal void SendClientRoomEnter(Gameplay.Boss.NetClientRoomEnter msg)
        {
            if (_mode != NetMode.Client || _net == null || _hostPeer == null) return;
            if (!Plugin.Cfg.EnableBossEncounterSync.Value || msg == null) return;
            try
            {
                var w = NetMessage.For(NetMessageType.ClientRoomEnter);
                Gameplay.Boss.NetBossEncounterCodec.WriteRoomEnter(w, msg);
                _hostPeer.Send(w, DeliveryMethod.ReliableOrdered);
            }
            catch (Exception ex) { NetLogger.Warn($"[RoomMembership] failed to send ClientRoomEnter: {ex.Message}"); }
        }

        internal void BroadcastHostRoomMembership(Gameplay.Boss.NetHostRoomMembership msg)
        {
            if (_mode != NetMode.Host || _net == null) return;
            if (!Plugin.Cfg.EnableBossEncounterSync.Value || msg == null || _clients.Count == 0) return;
            foreach (var peer in _clients.ToArray())
            {
                try
                {
                    var w = NetMessage.For(NetMessageType.HostRoomMembership);
                    Gameplay.Boss.NetBossEncounterCodec.WriteRoomMembership(w, msg);
                    peer.Send(w, DeliveryMethod.ReliableOrdered);
                }
                catch (Exception ex) { NetLogger.Warn($"[RoomMembership] failed to broadcast: {ex.Message}"); }
            }
        }

        private void HandleClientRoomEnter(NetPeer peer, NetDataReader reader)
        {
            if (_mode != NetMode.Host) return;
            if (!Plugin.Cfg.EnableBossEncounterSync.Value) return;
            if (!Gameplay.Boss.NetBossEncounterCodec.TryReadRoomEnter(reader, out var msg))
            {
                NetLogger.Warn("[RoomMembership] malformed ClientRoomEnter packet");
                return;
            }
            string peerId = _peerIds.TryGetValue(peer, out var mapped) ? mapped : peer.Address.ToString();
            Gameplay.Boss.NetBossEncounterManager.HandleClientRoomEnter(msg, peerId);
        }

        private void HandleHostRoomMembership(NetPeer peer, NetDataReader reader)
        {
            if (_mode != NetMode.Client) return;
            if (!Plugin.Cfg.EnableBossEncounterSync.Value) return;
            if (!Gameplay.Boss.NetBossEncounterCodec.TryReadRoomMembership(reader, out var msg))
            {
                NetLogger.Warn("[RoomMembership] malformed HostRoomMembership packet");
                return;
            }
            Gameplay.Boss.NetBossEncounterManager.HandleHostRoomMembership(msg);
        }

        // Phase RT3-Cousin-arms: enumerate every in-scene remote player's world position (recent, same-scene). Used
        // client-side to spawn visual-only arm mud balls toward team-mates (the client has no ghost Units for them).
        internal void ForEachRemotePlayerPosition(System.Action<UnityEngine.Vector3> action)
        {
            if (action == null) return;
            _visualProxies.ForEachInScenePlayer((peerId, pos) => action(pos), Now(), Plugin.Cfg.RemotePlayerVisualTimeoutSeconds.Value);
        }

        internal void BroadcastHostBossDynamicSpawn(Gameplay.Boss.NetBossDynamicSpawn msg)
        {
            if (_mode != NetMode.Host || _net == null) return;
            if (!Plugin.Cfg.EnableBossEncounterSync.Value || msg == null || _clients.Count == 0) return;
            foreach (var peer in _clients.ToArray())
            {
                try
                {
                    var w = NetMessage.For(NetMessageType.HostBossDynamicSpawn);
                    Gameplay.Boss.NetBossEncounterCodec.WriteDynamicSpawn(w, msg);
                    peer.Send(w, DeliveryMethod.ReliableOrdered);
                }
                catch (Exception ex) { NetLogger.Warn($"[BossSpawn] failed to broadcast: {ex.Message}"); }
            }
        }

        private void HandleHostBossDynamicSpawn(NetPeer peer, NetDataReader reader)
        {
            if (_mode != NetMode.Client) return;
            if (!Plugin.Cfg.EnableBossEncounterSync.Value) return;
            if (!Gameplay.Boss.NetBossEncounterCodec.TryReadDynamicSpawn(reader, out var msg))
            {
                NetLogger.Warn("[BossSpawn] malformed HostBossDynamicSpawn packet");
                return;
            }
            Gameplay.Boss.BossDynamicSpawnManifest.HandleHostBossDynamicSpawn(msg);
        }

        internal void SendClientBossHitRequest(Gameplay.Boss.NetClientBossHitRequest req)
        {
            if (_mode != NetMode.Client || _net == null || _hostPeer == null) return;
            if (!Plugin.Cfg.EnableBossEncounterSync.Value || req == null) return;
            try
            {
                var w = NetMessage.For(NetMessageType.ClientBossHitRequest);
                Gameplay.Boss.NetBossEncounterCodec.WriteBossHit(w, req);
                _hostPeer.Send(w, DeliveryMethod.ReliableOrdered);
            }
            catch (Exception ex) { NetLogger.Warn($"[BossDamage] failed to send ClientBossHitRequest: {ex.Message}"); }
        }

        private void HandleClientBossHitRequest(NetPeer peer, NetDataReader reader)
        {
            if (_mode != NetMode.Host) return;
            if (!Plugin.Cfg.EnableBossEncounterSync.Value) return;
            if (!Gameplay.Boss.NetBossEncounterCodec.TryReadBossHit(reader, out var req))
            {
                NetLogger.Warn("[BossDamage] malformed ClientBossHitRequest packet");
                return;
            }
            string peerId = _peerIds.TryGetValue(peer, out var mapped) ? mapped : peer.Address.ToString();
            Gameplay.Boss.NetBossEncounterManager.HandleClientBossHitRequest(req, peerId);
        }

        internal void BroadcastHostBossHitVisual(Gameplay.Boss.NetHostBossHitVisual msg)
        {
            if (_mode != NetMode.Host || _net == null) return;
            if (!Plugin.Cfg.EnableBossEncounterSync.Value || msg == null || _clients.Count == 0) return;
            foreach (var peer in _clients.ToArray())
            {
                try
                {
                    var w = NetMessage.For(NetMessageType.HostBossHitVisual);
                    Gameplay.Boss.NetBossEncounterCodec.WriteBossHitVisual(w, msg);
                    peer.Send(w, DeliveryMethod.ReliableOrdered);
                }
                catch (Exception ex) { NetLogger.Warn($"[BossDamage] failed to broadcast hit visual: {ex.Message}"); }
            }
        }

        private void HandleHostBossHitVisual(NetPeer peer, NetDataReader reader)
        {
            if (_mode != NetMode.Client) return;
            if (!Plugin.Cfg.EnableBossEncounterSync.Value) return;
            if (!Gameplay.Boss.NetBossEncounterCodec.TryReadBossHitVisual(reader, out var msg))
            {
                NetLogger.Warn("[BossDamage] malformed HostBossHitVisual packet");
                return;
            }
            Gameplay.Boss.NetBossEncounterManager.HandleHostBossHitVisual(msg);
        }

        internal void BroadcastHostBossDiscreteEvent(Gameplay.Boss.NetBossDiscreteEvent msg)
        {
            if (_mode != NetMode.Host || _net == null) return;
            if (!Plugin.Cfg.EnableBossEncounterSync.Value || msg == null || _clients.Count == 0) return;
            foreach (var peer in _clients.ToArray())
            {
                try
                {
                    var w = NetMessage.For(NetMessageType.HostBossDiscreteEvent);
                    Gameplay.Boss.NetBossEncounterCodec.WriteDiscreteEvent(w, msg);
                    peer.Send(w, DeliveryMethod.ReliableOrdered);
                }
                catch (Exception ex) { NetLogger.Warn($"[CousinPool] failed to broadcast discrete event: {ex.Message}"); }
            }
        }

        private void HandleHostBossDiscreteEvent(NetPeer peer, NetDataReader reader)
        {
            if (_mode != NetMode.Client) return;
            if (!Plugin.Cfg.EnableBossEncounterSync.Value) return;
            if (!Gameplay.Boss.NetBossEncounterCodec.TryReadDiscreteEvent(reader, out var msg))
            {
                NetLogger.Warn("[CousinPool] malformed HostBossDiscreteEvent packet");
                return;
            }
            Gameplay.Boss.NetBossEncounterManager.HandleHostBossDiscreteEvent(msg);
        }

        // Phase 5.4-F5: Lucia eye defeat authority.
        internal void SendClientLuciaEyeReport(Gameplay.Boss.NetLuciaEyeReport req)
        {
            if (_mode != NetMode.Client || _net == null || _hostPeer == null) return;
            if (!Plugin.Cfg.EnableBossEncounterSync.Value || req == null) return;
            try
            {
                var w = NetMessage.For(NetMessageType.ClientLuciaEyeReport);
                Gameplay.Boss.NetBossEncounterCodec.WriteLuciaEyeReport(w, req);
                _hostPeer.Send(w, DeliveryMethod.ReliableOrdered);
            }
            catch (Exception ex) { NetLogger.Warn($"[LuciaEye] failed to send ClientLuciaEyeReport: {ex.Message}"); }
        }

        private void HandleClientLuciaEyeReport(NetPeer peer, NetDataReader reader)
        {
            if (_mode != NetMode.Host) return;
            if (!Plugin.Cfg.EnableBossEncounterSync.Value) return;
            if (!Gameplay.Boss.NetBossEncounterCodec.TryReadLuciaEyeReport(reader, out var req))
            {
                NetLogger.Warn("[LuciaEye] malformed ClientLuciaEyeReport packet");
                return;
            }
            string peerId = _peerIds.TryGetValue(peer, out var mapped) ? mapped : peer.Address.ToString();
            Gameplay.Boss.NetBossEncounterManager.HandleClientLuciaEyeReport(req, peerId);
        }

        internal void BroadcastHostLuciaEyeState(Gameplay.Boss.NetLuciaEyeState msg)
        {
            if (_mode != NetMode.Host || _net == null) return;
            if (!Plugin.Cfg.EnableBossEncounterSync.Value || msg == null || _clients.Count == 0) return;
            foreach (var peer in _clients.ToArray())
            {
                try
                {
                    var w = NetMessage.For(NetMessageType.HostLuciaEyeState);
                    Gameplay.Boss.NetBossEncounterCodec.WriteLuciaEyeState(w, msg);
                    peer.Send(w, DeliveryMethod.ReliableOrdered);
                }
                catch (Exception ex) { NetLogger.Warn($"[LuciaEye] failed to broadcast eye state: {ex.Message}"); }
            }
        }

        private void HandleHostLuciaEyeState(NetPeer peer, NetDataReader reader)
        {
            if (_mode != NetMode.Client) return;
            if (!Plugin.Cfg.EnableBossEncounterSync.Value) return;
            if (!Gameplay.Boss.NetBossEncounterCodec.TryReadLuciaEyeState(reader, out var msg))
            {
                NetLogger.Warn("[LuciaEye] malformed HostLuciaEyeState packet");
                return;
            }
            Gameplay.Boss.NetBossEncounterManager.HandleHostLuciaEyeState(msg);
        }

        // Phase 5.4-F6: Lucia terminal death authority.
        internal void BroadcastHostLuciaDeath(Gameplay.Boss.NetLuciaDeath msg)
        {
            if (_mode != NetMode.Host || _net == null) return;
            if (!Plugin.Cfg.EnableBossEncounterSync.Value || msg == null || _clients.Count == 0) return;
            foreach (var peer in _clients.ToArray())
            {
                try
                {
                    var w = NetMessage.For(NetMessageType.HostLuciaDeath);
                    Gameplay.Boss.NetBossEncounterCodec.WriteLuciaDeath(w, msg);
                    peer.Send(w, DeliveryMethod.ReliableOrdered);
                }
                catch (Exception ex) { NetLogger.Warn($"[LuciaDeath] failed to broadcast death: {ex.Message}"); }
            }
        }

        private void HandleHostLuciaDeath(NetPeer peer, NetDataReader reader)
        {
            if (_mode != NetMode.Client) return;
            if (!Plugin.Cfg.EnableBossEncounterSync.Value) return;
            if (!Gameplay.Boss.NetBossEncounterCodec.TryReadLuciaDeath(reader, out var msg))
            {
                NetLogger.Warn("[LuciaDeath] malformed HostLuciaDeath packet");
                return;
            }
            Gameplay.Boss.NetBossEncounterManager.HandleHostLuciaDeath(msg);
        }

        // Phase 5.4-G2: Witch phase revision authority.
        internal void BroadcastHostWitchPhase(Gameplay.Boss.NetWitchPhase msg)
        {
            if (_mode != NetMode.Host || _net == null) return;
            if (!Plugin.Cfg.EnableBossEncounterSync.Value || msg == null || _clients.Count == 0) return;
            foreach (var peer in _clients.ToArray())
            {
                try
                {
                    var w = NetMessage.For(NetMessageType.HostWitchPhase);
                    Gameplay.Boss.NetBossEncounterCodec.WriteWitchPhase(w, msg);
                    peer.Send(w, DeliveryMethod.ReliableOrdered);
                }
                catch (Exception ex) { NetLogger.Warn($"[WitchPhase] failed to broadcast phase: {ex.Message}"); }
            }
        }

        private void HandleHostWitchPhase(NetPeer peer, NetDataReader reader)
        {
            if (_mode != NetMode.Client) return;
            if (!Plugin.Cfg.EnableBossEncounterSync.Value) return;
            if (!Gameplay.Boss.NetBossEncounterCodec.TryReadWitchPhase(reader, out var msg))
            {
                NetLogger.Warn("[WitchPhase] malformed HostWitchPhase packet");
                return;
            }
            Gameplay.Boss.NetBossEncounterManager.HandleHostWitchPhase(msg);
        }

        // Phase 5.4-G5: Witch Phase 2 dome manifest + hit result.
        internal void BroadcastHostWitchP2Manifest(Gameplay.Boss.NetWitchP2Manifest msg)
        {
            if (_mode != NetMode.Host || _net == null) return;
            if (!Plugin.Cfg.EnableBossEncounterSync.Value || msg == null || _clients.Count == 0) return;
            foreach (var peer in _clients.ToArray())
            {
                try
                {
                    var w = NetMessage.For(NetMessageType.HostWitchP2Manifest);
                    Gameplay.Boss.NetBossEncounterCodec.WriteWitchP2Manifest(w, msg);
                    peer.Send(w, DeliveryMethod.ReliableOrdered);
                }
                catch (Exception ex) { NetLogger.Warn($"[WitchP2] failed to broadcast manifest: {ex.Message}"); }
            }
        }

        private void HandleHostWitchP2Manifest(NetPeer peer, NetDataReader reader)
        {
            if (_mode != NetMode.Client) return;
            if (!Plugin.Cfg.EnableBossEncounterSync.Value) return;
            if (!Gameplay.Boss.NetBossEncounterCodec.TryReadWitchP2Manifest(reader, out var msg))
            {
                NetLogger.Warn("[WitchP2] malformed HostWitchP2Manifest packet");
                return;
            }
            Gameplay.Boss.NetBossEncounterManager.HandleHostWitchP2Manifest(msg);
        }

        internal void BroadcastHostWitchP2Result(Gameplay.Boss.NetWitchP2Result msg)
        {
            if (_mode != NetMode.Host || _net == null) return;
            if (!Plugin.Cfg.EnableBossEncounterSync.Value || msg == null || _clients.Count == 0) return;
            foreach (var peer in _clients.ToArray())
            {
                try
                {
                    var w = NetMessage.For(NetMessageType.HostWitchP2Result);
                    Gameplay.Boss.NetBossEncounterCodec.WriteWitchP2Result(w, msg);
                    peer.Send(w, DeliveryMethod.ReliableOrdered);
                }
                catch (Exception ex) { NetLogger.Warn($"[WitchP2] failed to broadcast result: {ex.Message}"); }
            }
        }

        private void HandleHostWitchP2Result(NetPeer peer, NetDataReader reader)
        {
            if (_mode != NetMode.Client) return;
            if (!Plugin.Cfg.EnableBossEncounterSync.Value) return;
            if (!Gameplay.Boss.NetBossEncounterCodec.TryReadWitchP2Result(reader, out var msg))
            {
                NetLogger.Warn("[WitchP2] malformed HostWitchP2Result packet");
                return;
            }
            Gameplay.Boss.NetBossEncounterManager.HandleHostWitchP2Result(msg);
        }

        // Phase 5.5-RT1: runtime spawn sync.
        internal void BroadcastHostRuntimeSpawn(Gameplay.NetRuntimeSpawn msg)
        {
            if (_mode != NetMode.Host || _net == null) return;
            if (msg == null || _clients.Count == 0) return;
            foreach (var peer in _clients.ToArray())
            {
                try
                {
                    var w = NetMessage.For(NetMessageType.HostRuntimeSpawn);
                    Gameplay.NetRuntimeSpawnCodec.Write(w, msg);
                    peer.Send(w, DeliveryMethod.ReliableOrdered);
                }
                catch (Exception ex) { NetLogger.Warn($"[RuntimeSpawn] failed to broadcast: {ex.Message}"); }
            }
        }

        private void HandleHostRuntimeSpawn(NetPeer peer, NetDataReader reader)
        {
            if (_mode != NetMode.Client) return;
            if (!Gameplay.NetRuntimeSpawnCodec.TryRead(reader, out var msg))
            {
                NetLogger.Warn("[RuntimeSpawn] malformed HostRuntimeSpawn packet");
                return;
            }
            Gameplay.RuntimeSpawnManager.HandleHostRuntimeSpawn(msg);
        }

        // ----------------------------------------------------------------- Phase 5.6-WS player weapon bullet sync

        // Called on the FIRING peer (via NetGameplaySyncBridge). Stamps identity + scene context, then routes the event
        // (Client → Host; Host → all clients). The firing peer never replays locally — it already has the real bullets.
        internal void BroadcastLocalPlayerWeaponFire(Gameplay.NetPlayerWeaponFire msg)
        {
            if (_net == null || _mode == NetMode.Off || msg == null) return;
            if (!Plugin.Cfg.EnablePlayerWeaponSync.Value) return;

            var local = _runStates.LocalState;
            msg.PeerId = local.PeerId;
            msg.ChapterName = local.ChapterName;
            msg.LevelIndex = local.LevelIndex;
            msg.HasLevelSeed = local.HasLevelSeed;
            msg.LevelSeed = local.LevelSeed;
            msg.SentAt = Now();

            if (_mode == NetMode.Host)
            {
                foreach (var peer in _clients.ToArray())
                    SendPlayerWeaponFire(peer, msg);
            }
            else if (_hostPeer != null)
            {
                SendPlayerWeaponFire(_hostPeer, msg);
            }
        }

        private void SendPlayerWeaponFire(NetPeer peer, Gameplay.NetPlayerWeaponFire msg)
        {
            try
            {
                var w = NetMessage.For(NetMessageType.PlayerWeaponFire);
                Gameplay.NetPlayerWeaponFireCodec.Write(w, msg);
                peer.Send(w, DeliveryMethod.ReliableOrdered);
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[PlayerWeaponFire] failed to send: {ex.Message}");
            }
        }

        private void HandlePlayerWeaponFire(NetPeer peer, NetDataReader reader)
        {
            if (!Plugin.Cfg.EnablePlayerWeaponSync.Value) return;
            if (!Gameplay.NetPlayerWeaponFireCodec.TryRead(reader, out var msg))
            {
                NetLogger.Warn("[PlayerWeaponFire] malformed PlayerWeaponFire packet");
                return;
            }

            if (_mode == NetMode.Host)
            {
                if (!_peerIds.TryGetValue(peer, out var peerId))
                {
                    if (Plugin.Cfg.EnableDebugLog.Value)
                        NetLogger.Debug($"[PlayerWeaponFire] ignoring fire from unregistered peer {peer.Address}");
                    return;
                }
                msg.PeerId = peerId;

                if (msg.MatchesScene(_runStates.LocalState))
                    Gameplay.PlayerWeaponFireManager.Replay(msg);

                RelayPlayerWeaponFireToOtherClients(peer, msg);
                return;
            }

            if (_mode == NetMode.Client)
            {
                if (msg.PeerId == _runStates.LocalState.PeerId) return; // never replay my own barrage
                if (msg.MatchesScene(_runStates.LocalState))
                    Gameplay.PlayerWeaponFireManager.Replay(msg);
            }
        }

        private void RelayPlayerWeaponFireToOtherClients(NetPeer sourcePeer, Gameplay.NetPlayerWeaponFire msg)
        {
            if (_mode != NetMode.Host) return;
            foreach (var client in _clients.ToArray())
            {
                if (client == sourcePeer) continue;
                SendPlayerWeaponFire(client, msg);
            }
        }

        // ---------------------------------------------------------------- Phase 5.7-BR destructible (Breakable) break
        // Same topology as PlayerWeaponFire: the peer that broke a destructible reports it; the Host stamps the PeerId,
        // mirrors it locally (if in the same scene) and relays to all other clients. The firing peer never mirrors its own.

        internal void BroadcastLocalBreakableBreak(Gameplay.NetBreakableBreak msg)
        {
            if (_net == null || _mode == NetMode.Off || msg == null) return;
            if (!Plugin.Cfg.EnableBreakableSync.Value) return;

            var local = _runStates.LocalState;
            msg.PeerId = local.PeerId;
            msg.ChapterName = local.ChapterName;
            msg.LevelIndex = local.LevelIndex;
            msg.HasLevelSeed = local.HasLevelSeed;
            msg.LevelSeed = local.LevelSeed;
            msg.SentAt = Now();

            if (_mode == NetMode.Host)
            {
                foreach (var peer in _clients.ToArray())
                    SendBreakableBreak(peer, msg);
            }
            else if (_hostPeer != null)
            {
                SendBreakableBreak(_hostPeer, msg);
            }
        }

        private void SendBreakableBreak(NetPeer peer, Gameplay.NetBreakableBreak msg)
        {
            try
            {
                var w = NetMessage.For(NetMessageType.BreakableBreak);
                Gameplay.NetBreakableBreakCodec.Write(w, msg);
                peer.Send(w, DeliveryMethod.ReliableOrdered);
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[BreakableBreak] failed to send: {ex.Message}");
            }
        }

        private void HandleBreakableBreak(NetPeer peer, NetDataReader reader)
        {
            if (!Plugin.Cfg.EnableBreakableSync.Value) return;
            if (!Gameplay.NetBreakableBreakCodec.TryRead(reader, out var msg))
            {
                NetLogger.Warn("[BreakableBreak] malformed BreakableBreak packet");
                return;
            }

            if (_mode == NetMode.Host)
            {
                if (!_peerIds.TryGetValue(peer, out var peerId))
                {
                    if (Plugin.Cfg.EnableDebugLog.Value)
                        NetLogger.Debug($"[BreakableBreak] ignoring break from unregistered peer {peer.Address}");
                    return;
                }
                msg.PeerId = peerId;

                if (msg.MatchesScene(_runStates.LocalState))
                    Gameplay.BreakableBreakManager.ApplyRemoteBreak(msg);

                RelayBreakableBreakToOtherClients(peer, msg);
                return;
            }

            if (_mode == NetMode.Client)
            {
                if (msg.PeerId == _runStates.LocalState.PeerId) return; // never mirror my own break
                if (msg.MatchesScene(_runStates.LocalState))
                    Gameplay.BreakableBreakManager.ApplyRemoteBreak(msg);
            }
        }

        private void RelayBreakableBreakToOtherClients(NetPeer sourcePeer, Gameplay.NetBreakableBreak msg)
        {
            if (_mode != NetMode.Host) return;
            foreach (var client in _clients.ToArray())
            {
                if (client == sourcePeer) continue;
                SendBreakableBreak(client, msg);
            }
        }

        // ---------------------------------------------------------------- Phase LD-1 combat-room gate (MetalGate) sync
        // Same topology as BreakableBreak: the peer that opened/closed a gate reports it; the Host stamps the PeerId,
        // mirrors it locally (same scene) and relays to all other clients. The firing peer never mirrors its own.

        internal void BroadcastLocalGateState(Gameplay.NetGateState msg)
        {
            if (_net == null || _mode == NetMode.Off || msg == null) return;
            if (!Plugin.Cfg.EnableGateSync.Value) return;

            var local = _runStates.LocalState;
            msg.PeerId = local.PeerId;
            msg.ChapterName = local.ChapterName;
            msg.LevelIndex = local.LevelIndex;
            msg.HasLevelSeed = local.HasLevelSeed;
            msg.LevelSeed = local.LevelSeed;
            msg.SentAt = Now();

            if (_mode == NetMode.Host)
            {
                foreach (var peer in _clients.ToArray())
                    SendGateState(peer, msg);
            }
            else if (_hostPeer != null)
            {
                SendGateState(_hostPeer, msg);
            }
        }

        private void SendGateState(NetPeer peer, Gameplay.NetGateState msg)
        {
            try
            {
                var w = NetMessage.For(NetMessageType.GateState);
                Gameplay.NetGateStateCodec.Write(w, msg);
                peer.Send(w, DeliveryMethod.ReliableOrdered);
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[GateSync] failed to send: {ex.Message}");
            }
        }

        private void HandleGateState(NetPeer peer, NetDataReader reader)
        {
            if (!Plugin.Cfg.EnableGateSync.Value) return;
            if (!Gameplay.NetGateStateCodec.TryRead(reader, out var msg))
            {
                NetLogger.Warn("[GateSync] malformed GateState packet");
                return;
            }

            if (_mode == NetMode.Host)
            {
                if (!_peerIds.TryGetValue(peer, out var peerId))
                {
                    if (Plugin.Cfg.EnableDebugLog.Value)
                        NetLogger.Debug($"[GateSync] ignoring gate from unregistered peer {peer.Address}");
                    return;
                }
                msg.PeerId = peerId;

                if (msg.MatchesScene(_runStates.LocalState))
                    Gameplay.GateSyncManager.ApplyRemoteGate(msg);

                RelayGateStateToOtherClients(peer, msg);
                return;
            }

            if (_mode == NetMode.Client)
            {
                if (msg.PeerId == _runStates.LocalState.PeerId) return; // never mirror my own change
                if (msg.MatchesScene(_runStates.LocalState))
                    Gameplay.GateSyncManager.ApplyRemoteGate(msg);
            }
        }

        private void RelayGateStateToOtherClients(NetPeer sourcePeer, Gameplay.NetGateState msg)
        {
            if (_mode != NetMode.Host) return;
            foreach (var client in _clients.ToArray())
            {
                if (client == sourcePeer) continue;
                SendGateState(client, msg);
            }
        }

        // ---------------------------------------------------------------- Phase LD-1b combat-room door (SetActive) sync

        internal void BroadcastLocalTriggerDoors(Gameplay.NetTriggerDoors msg)
        {
            if (_net == null || _mode == NetMode.Off || msg == null) return;
            if (!Plugin.Cfg.EnableTriggerDoorSync.Value) return;

            var local = _runStates.LocalState;
            msg.PeerId = local.PeerId;
            msg.ChapterName = local.ChapterName;
            msg.LevelIndex = local.LevelIndex;
            msg.HasLevelSeed = local.HasLevelSeed;
            msg.LevelSeed = local.LevelSeed;
            msg.SentAt = Now();

            if (_mode == NetMode.Host)
            {
                foreach (var peer in _clients.ToArray())
                    SendTriggerDoors(peer, msg);
            }
            else if (_hostPeer != null)
            {
                SendTriggerDoors(_hostPeer, msg);
            }
        }

        private void SendTriggerDoors(NetPeer peer, Gameplay.NetTriggerDoors msg)
        {
            try
            {
                var w = NetMessage.For(NetMessageType.TriggerDoors);
                Gameplay.NetTriggerDoorsCodec.Write(w, msg);
                peer.Send(w, DeliveryMethod.ReliableOrdered);
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[DoorSync] failed to send: {ex.Message}");
            }
        }

        private void HandleTriggerDoors(NetPeer peer, NetDataReader reader)
        {
            if (!Plugin.Cfg.EnableTriggerDoorSync.Value) return;
            if (!Gameplay.NetTriggerDoorsCodec.TryRead(reader, out var msg))
            {
                NetLogger.Warn("[DoorSync] malformed TriggerDoors packet");
                return;
            }

            if (_mode == NetMode.Host)
            {
                if (!_peerIds.TryGetValue(peer, out var peerId))
                {
                    if (Plugin.Cfg.EnableDebugLog.Value)
                        NetLogger.Debug($"[DoorSync] ignoring doors from unregistered peer {peer.Address}");
                    return;
                }
                msg.PeerId = peerId;

                if (msg.MatchesScene(_runStates.LocalState))
                    Gameplay.TriggerDoorSyncManager.ApplyRemote(msg);

                RelayTriggerDoorsToOtherClients(peer, msg);
                return;
            }

            if (_mode == NetMode.Client)
            {
                if (msg.PeerId == _runStates.LocalState.PeerId) return; // never mirror my own
                if (msg.MatchesScene(_runStates.LocalState))
                    Gameplay.TriggerDoorSyncManager.ApplyRemote(msg);
            }
        }

        private void RelayTriggerDoorsToOtherClients(NetPeer sourcePeer, Gameplay.NetTriggerDoors msg)
        {
            if (_mode != NetMode.Host) return;
            foreach (var client in _clients.ToArray())
            {
                if (client == sourcePeer) continue;
                SendTriggerDoors(client, msg);
            }
        }

        // ---------------------------------------------------------------- Phase LD-2a arena lockdown (membership feed)

        internal void SendClientArenaEnter(Gameplay.NetClientArenaEnter msg)
        {
            if (_net == null || _mode != NetMode.Client || _hostPeer == null || msg == null) return;
            if (!Plugin.Cfg.EnableArenaLockdown.Value) return;
            try
            {
                var w = NetMessage.For(NetMessageType.ClientArenaEnter);
                Gameplay.NetClientArenaEnterCodec.Write(w, msg);
                _hostPeer.Send(w, DeliveryMethod.ReliableOrdered);
            }
            catch (Exception ex) { NetLogger.Warn($"[ArenaLockdown] failed to send ClientArenaEnter: {ex.Message}"); }
        }

        private void HandleClientArenaEnter(NetPeer peer, NetDataReader reader)
        {
            if (_mode != NetMode.Host) return;
            if (!Plugin.Cfg.EnableArenaLockdown.Value) return;
            if (!Gameplay.NetClientArenaEnterCodec.TryRead(reader, out var msg))
            {
                NetLogger.Warn("[ArenaLockdown] malformed ClientArenaEnter packet");
                return;
            }
            if (!_peerIds.TryGetValue(peer, out var peerId)) return;
            Gameplay.ArenaLockdownManager.HandleClientArenaEnter(msg, peerId);
        }

        /// <summary>LD-2a: the local end's current level (host's own run state).</summary>
        internal bool TryGetLocalScene(out string chapter, out int level, out bool hasSeed, out int seed)
        {
            var ls = _runStates.LocalState;
            chapter = ls.ChapterName; level = ls.LevelIndex; hasSeed = ls.HasLevelSeed; seed = ls.LevelSeed;
            return ls.HasLevel;
        }

        /// <summary>LD-2a (host): every end (host + connected clients) currently in the given level. PeerId per end
        /// ("host" for the local host state). Used to compute the non-in-room lockdown targets.</summary>
        internal List<string> GetPeerIdsInLevel(string chapter, int level, bool hasSeed, int seed)
        {
            var result = new List<string>();
            bool seedAuth = Plugin.Cfg.EnableLevelSeedAuthority.Value;
            if (SceneMatch(_runStates.LocalState, chapter, level, hasSeed, seed, seedAuth)) result.Add(_runStates.LocalState.PeerId);
            foreach (var rs in _runStates.RemoteStates)
                if (SceneMatch(rs, chapter, level, hasSeed, seed, seedAuth)) result.Add(rs.PeerId);
            return result;
        }

        private static bool SceneMatch(NetRunState s, string chapter, int level, bool hasSeed, int seed, bool seedAuth)
        {
            if (s == null || !s.HasLevel) return false;
            if (!string.Equals(s.ChapterName, chapter, StringComparison.Ordinal)) return false;
            if (s.LevelIndex != level) return false;
            if (seedAuth) { if (!hasSeed || !s.HasLevelSeed) return false; if (s.LevelSeed != seed) return false; }
            return true;
        }

        // ---------------------------------------------------------------- World item-drop sync (player drops first)
        // Spawn: optimistic + peer-authoritative — the dropping peer broadcasts (Client→Host→relay to other Clients;
        // the dropper never mirrors its own). Take: host-authoritative — a Client requests, the Host grants exactly one
        // and broadcasts the removal (Host→all Clients). See WorldPickupManager.

        // Local identity (the dropper stamps the world-pickup owner id; also used by the take/grant logic).
        internal string LocalPeerId => _runStates?.LocalState.PeerId ?? "";

        internal void BroadcastLocalWorldPickupSpawn(Gameplay.NetWorldPickupSpawn msg)
        {
            if (_net == null || _mode == NetMode.Off || msg == null) return;
            if (!Plugin.Cfg.EnableWorldItemDropSync.Value) return;

            var local = _runStates.LocalState;
            msg.OwnerPeerId = local.PeerId;
            msg.ChapterName = local.ChapterName;
            msg.LevelIndex = local.LevelIndex;
            msg.HasLevelSeed = local.HasLevelSeed;
            msg.LevelSeed = local.LevelSeed;
            msg.SentAt = Now();

            if (_mode == NetMode.Host)
            {
                foreach (var peer in _clients.ToArray())
                    SendWorldPickupSpawn(peer, msg);
            }
            else if (_hostPeer != null)
            {
                SendWorldPickupSpawn(_hostPeer, msg);
            }
        }

        private void SendWorldPickupSpawn(NetPeer peer, Gameplay.NetWorldPickupSpawn msg)
        {
            try
            {
                var w = NetMessage.For(NetMessageType.WorldPickupSpawn);
                Gameplay.NetWorldPickupCodec.WriteSpawn(w, msg);
                peer.Send(w, DeliveryMethod.ReliableOrdered);
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[WorldPickup] spawn send failed: {ex.Message}");
            }
        }

        private void HandleWorldPickupSpawn(NetPeer peer, NetDataReader reader)
        {
            if (!Plugin.Cfg.EnableWorldItemDropSync.Value) return;
            if (!Gameplay.NetWorldPickupCodec.TryReadSpawn(reader, out var msg))
            {
                NetLogger.Warn("[WorldPickup] malformed WorldPickupSpawn packet");
                return;
            }

            if (_mode == NetMode.Host)
            {
                if (!_peerIds.TryGetValue(peer, out var peerId))
                {
                    if (Plugin.Cfg.EnableDebugLog.Value)
                        NetLogger.Debug($"[WorldPickup] spawn from unregistered peer {peer.Address}");
                    return;
                }
                msg.OwnerPeerId = peerId; // stamp the dropper's authoritative identity

                if (msg.MatchesScene(_runStates.LocalState))
                    Gameplay.WorldPickupManager.ApplyRemoteSpawn(msg);

                RelayWorldPickupSpawnToOtherClients(peer, msg);
                return;
            }

            if (_mode == NetMode.Client)
            {
                if (msg.OwnerPeerId == _runStates.LocalState.PeerId) return; // never mirror my own drop
                if (msg.MatchesScene(_runStates.LocalState))
                    Gameplay.WorldPickupManager.ApplyRemoteSpawn(msg);
            }
        }

        private void RelayWorldPickupSpawnToOtherClients(NetPeer sourcePeer, Gameplay.NetWorldPickupSpawn msg)
        {
            if (_mode != NetMode.Host) return;
            foreach (var client in _clients.ToArray())
            {
                if (client == sourcePeer) continue;
                SendWorldPickupSpawn(client, msg);
            }
        }

        internal void SendWorldPickupTakeRequest(string ownerPeerId, ushort seq)
        {
            if (_mode != NetMode.Client || _hostPeer == null) return;
            if (!Plugin.Cfg.EnableWorldItemDropSync.Value) return;
            try
            {
                var msg = new Gameplay.NetWorldPickupTake { OwnerPeerId = ownerPeerId, Seq = seq, SentAt = Now() };
                var w = NetMessage.For(NetMessageType.WorldPickupTakeRequest);
                Gameplay.NetWorldPickupCodec.WriteTake(w, msg);
                _hostPeer.Send(w, DeliveryMethod.ReliableOrdered);
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[WorldPickup] take request send failed: {ex.Message}");
            }
        }

        private void HandleWorldPickupTakeRequest(NetPeer peer, NetDataReader reader)
        {
            if (!Plugin.Cfg.EnableWorldItemDropSync.Value) return;
            if (_mode != NetMode.Host) return;
            if (!Gameplay.NetWorldPickupCodec.TryReadTake(reader, out var msg))
            {
                NetLogger.Warn("[WorldPickup] malformed WorldPickupTakeRequest packet");
                return;
            }
            if (!_peerIds.TryGetValue(peer, out var requesterPeerId))
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[WorldPickup] take request from unregistered peer {peer.Address}");
                return;
            }
            Gameplay.WorldPickupManager.HostHandleTakeRequest(msg, requesterPeerId);
        }

        internal void BroadcastWorldPickupRemoved(Gameplay.NetWorldPickupRemoved msg)
        {
            if (_net == null || _mode != NetMode.Host || msg == null) return;
            if (!Plugin.Cfg.EnableWorldItemDropSync.Value) return;
            msg.SentAt = Now();
            foreach (var peer in _clients.ToArray())
                SendWorldPickupRemoved(peer, msg);
        }

        private void SendWorldPickupRemoved(NetPeer peer, Gameplay.NetWorldPickupRemoved msg)
        {
            try
            {
                var w = NetMessage.For(NetMessageType.WorldPickupRemoved);
                Gameplay.NetWorldPickupCodec.WriteRemoved(w, msg);
                peer.Send(w, DeliveryMethod.ReliableOrdered);
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[WorldPickup] removed send failed: {ex.Message}");
            }
        }

        private void HandleWorldPickupRemoved(NetPeer peer, NetDataReader reader)
        {
            if (!Plugin.Cfg.EnableWorldItemDropSync.Value) return;
            if (_mode != NetMode.Client) return; // only the Host originates removals
            if (!Gameplay.NetWorldPickupCodec.TryReadRemoved(reader, out var msg))
            {
                NetLogger.Warn("[WorldPickup] malformed WorldPickupRemoved packet");
                return;
            }
            Gameplay.WorldPickupManager.ApplyRemoved(msg.Key, msg.TakenByPeerId);
        }

        // ----------------------------------------------------------------- Phase 5.6-WS-2 remote held weapon model

        internal void BroadcastLocalHeldWeapon(Gameplay.NetPlayerHeldWeapon msg)
        {
            if (_net == null || _mode == NetMode.Off || msg == null) return;
            if (!Plugin.Cfg.EnableRemoteWeaponModel.Value) return;

            msg.PeerId = _runStates.LocalState.PeerId;
            msg.SentAt = Now();

            if (_mode == NetMode.Host)
            {
                foreach (var peer in _clients.ToArray())
                    SendPlayerHeldWeapon(peer, msg);
            }
            else if (_hostPeer != null)
            {
                SendPlayerHeldWeapon(_hostPeer, msg);
            }
        }

        private void SendPlayerHeldWeapon(NetPeer peer, Gameplay.NetPlayerHeldWeapon msg)
        {
            try
            {
                var w = NetMessage.For(NetMessageType.PlayerHeldWeapon);
                Gameplay.NetPlayerHeldWeaponCodec.Write(w, msg);
                peer.Send(w, DeliveryMethod.ReliableOrdered);
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[HeldWeapon] failed to send: {ex.Message}");
            }
        }

        private void HandlePlayerHeldWeapon(NetPeer peer, NetDataReader reader)
        {
            if (!Plugin.Cfg.EnableRemoteWeaponModel.Value) return;
            if (!Gameplay.NetPlayerHeldWeaponCodec.TryRead(reader, out var msg))
            {
                NetLogger.Warn("[HeldWeapon] malformed PlayerHeldWeapon packet");
                return;
            }

            if (_mode == NetMode.Host)
            {
                if (!_peerIds.TryGetValue(peer, out var peerId))
                    return;
                msg.PeerId = peerId;

                Gameplay.PlayerHeldWeaponManager.Apply(msg);

                foreach (var client in _clients.ToArray())
                {
                    if (client == peer) continue;
                    SendPlayerHeldWeapon(client, msg);
                }
                return;
            }

            if (_mode == NetMode.Client)
            {
                if (msg.PeerId == _runStates.LocalState.PeerId) return;
                Gameplay.PlayerHeldWeaponManager.Apply(msg);
            }
        }

        /// <summary>
        /// Phase 5.3-M P0-A/B/D: apply a FINALIZED generation-input snapshot to the local run state.
        /// On the Host this corrects the stale GoToLevel sub-level (Act_01_Caves:0) to the real generated
        /// cave (Act_01_Caves:1 graph=Caves2) so the published run state, manifest header, scene match and
        /// enemy snapshot run key all stop using level=0. On the Client it reinforces the host-driven load.
        /// </summary>
        public void ApplyFinalizedGenerationSnapshot(NetGenerationInputSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.Finalized) return;
            if (_mode == NetMode.Off || _net == null) return;
            if (!Plugin.Cfg.EnableRunStateNegotiation.Value) return;
            if (snapshot.LevelIndex < 0) return;

            bool changed = _runStates.ApplyFinalizedGenerationSnapshot(
                snapshot.Chapter,
                snapshot.LevelIndex,
                snapshot.HasSeed,
                snapshot.Seed,
                snapshot.GraphName,
                Now(),
                out bool levelCorrected,
                out var updated);

            if (_mode == NetMode.Host) NetGenerationInputCapture.RunStateSnapshotAppliedHost++;
            else NetGenerationInputCapture.RunStateSnapshotAppliedClient++;
            if (levelCorrected) NetGenerationInputCapture.RunStateSnapshotOverrideUsed++;

            if (!changed) return;

            NetGenerationInputCapture.RunStateSnapshotRevisionBumped++;
            NetLogger.Info($"[RunStateSync] applied finalized snapshot role={_mode} chapter={updated.ChapterName} level={updated.LevelIndex} graph={(string.IsNullOrEmpty(snapshot.GraphName) ? "?" : snapshot.GraphName)} seed={(updated.HasLevelSeed ? updated.LevelSeed.ToString() : "?")} revision={updated.Revision}");

            // Phase 5.6-LK-P2 (Type B): the host reached the new level — clear the transition latch so client
            // relays may lead again (the next one to a DIFFERENT target).
            if (_mode == NetMode.Host) NetHostTransitionGuard.End("finalized-snapshot");

            // Republish the corrected run state immediately and refresh scene requests for drifted clients
            // (host-gated inside) so the remote side sees the new level without waiting for the next timer.
            SendLocalRunStateToConnectedPeers();
            SendHostSceneRequestsForDriftedClients();
        }

        internal void ReportLocalEnemyDeathEvent(NetGameplayDeathEvent deathEvent)
        {
            if (_mode != NetMode.Host || _net == null) return;
            if (!Plugin.Cfg.EnableHostEnemyDeathEventMirror.Value) return;
            if (deathEvent == null) return;
            if (_clients.Count == 0) return;

            foreach (var peer in _clients.ToArray())
                SendHostEnemyDeathEvent(peer, deathEvent);
        }

        internal void ReportClientEnemyDeathClaim(NetGameplayDeathEvent deathEvent)
        {
            if (_mode != NetMode.Client || _net == null) return;
            if (!Plugin.Cfg.EnableClientEnemyDeathClaim.Value) return;
            if (deathEvent == null) return;
            if (_hostPeer == null) return;

            SendClientEnemyDeathClaim(_hostPeer, deathEvent);
        }

        internal void ReportPlayerLifeState(NetPlayerLifeState state)
        {
            if (_mode == NetMode.Off || _net == null) return;
            if (!Plugin.Cfg.EnableCoopPlayerDownedRevive.Value) return;
            if (state == null) return;

            if (_mode == NetMode.Host)
            {
                if (string.IsNullOrWhiteSpace(state.SourcePeerId)) state.SourcePeerId = "host";
                SendPlayerLifeStateToClients(state, except: null);
            }
            else if (_mode == NetMode.Client && _hostPeer != null)
            {
                SendPlayerLifeState(_hostPeer, state);
            }
        }

        internal void ReportHostPlayerLifeStateToAll(NetPlayerLifeState state)
        {
            if (_mode != NetMode.Host || _net == null) return;
            if (!Plugin.Cfg.EnableCoopPlayerDownedRevive.Value) return;
            if (state == null) return;
            if (string.IsNullOrWhiteSpace(state.SourcePeerId)) state.SourcePeerId = "host";
            SendPlayerLifeStateToClients(state, except: null);
        }

        internal IReadOnlyCollection<string> GetKnownPlayerLifePeerIds()
        {
            var ids = _sessions.Sessions
                .Where(s => s.State == NetConnectionState.Connected)
                .Select(s => s.PeerId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct()
                .ToList();

            if (ids.Count == 0 && !string.IsNullOrWhiteSpace(_runStates.LocalState.PeerId))
                ids.Add(_runStates.LocalState.PeerId);

            return ids.AsReadOnly();
        }

        private void HandleEnemyStateSnapshotTimer()
        {
            if (_mode != NetMode.Host || _net == null) return;
            if (!Plugin.Cfg.EnableHostEnemyStateSnapshotMirror.Value) return;
            if (_clients.Count == 0) return;

            float hz = Plugin.Cfg.EnemyStateSnapshotSendRateHz.Value;
            if (hz <= 0f) return;
            float interval = 1f / hz;

            float now = Now();
            if (now - _lastEnemyStateSnapshotSendTime < interval) return;
            _lastEnemyStateSnapshotSendTime = now;

            // Phase 5.5-P1: refresh remote-player (client) interest positions so CollectHostEnemyStateSnapshots' interest
            // management treats enemies near a client — not just near the Host — as "near" (full snapshot rate).
            _remoteInterestScratch.Clear();
            _visualProxies.CollectInterestPositions(_remoteInterestScratch, now,
                Plugin.Cfg.RemotePlayerVisualTimeoutSeconds.Value);
            NetGameplayProbeManager.SetRemotePlayerInterestPositions(_remoteInterestScratch);

            // Phase 5.7-RB4 diag: measure the remote-interest feed directly — collected client position(s) vs the host
            // player position. LogOutput106 showed distRemoteMin == distHost exactly, i.e. the "remote" position equals
            // the host's. Dump real coordinates (time-throttled) to locate why the proxy reports the host position.
            if (Plugin.Cfg.LogEnemyInterestDiag.Value && now - _lastInterestFeedLogAt > 1f)
            {
                _lastInterestFeedLogAt = now;
                var ht = _localPlayer.LocalTransform;
                string hostPos = ht != null ? ht.position.ToString("F1") : "<none>";
                string firstRemote = _remoteInterestScratch.Count > 0 ? _remoteInterestScratch[0].ToString("F1") : "none";
                NetLogger.Info($"[InterestFeed] collectedRemote={_remoteInterestScratch.Count} firstRemote={firstRemote} hostPlayer={hostPos} visibleProxies={_visualProxies.VisibleCount} totalProxies={_visualProxies.ProxyCount}");
            }

            int maxPerPacket = GetEnemyStateSnapshotMaxPerPacket();
            var snapshots = NetGameplayProbeManager.CollectHostEnemyStateSnapshots(
                _runStates.LocalState,
                Plugin.Cfg.OnlySendAliveEnemyStateSnapshots.Value,
                256,
                ref _enemyStateSnapshotSequence);

            if (snapshots.Count == 0) return;

            foreach (var peer in _clients.ToArray())
                SendHostEnemyStateSnapshots(peer, snapshots, maxPerPacket);
        }

        private void HandleWorldRosterTimer()
        {
            if (_mode != NetMode.Host || _net == null) return;
            if (_clients.Count == 0) return;
            if (!Plugin.Cfg.EnableHostEnemyStateSnapshotMirror.Value) return;

            var state = _runStates.LocalState;
            if (!state.HasLevel) return;

            float now = Now();
            bool revisionChanged = state.Revision != _lastWorldRosterSentRevision;
            bool periodicResend = now - _lastWorldRosterSendTime >= 30f;
            if (!revisionChanged && !periodicResend) return;
            if (now - _lastWorldRosterSendTime < 3f) return; // debounce after revision change

            _lastWorldRosterSendTime = now;
            _lastWorldRosterSentRevision = state.Revision;

            var records = NetGameplayProbeManager.BuildHostWorldRoster();
            if (records.Count == 0) return;

            foreach (var peer in _clients.ToArray())
                SendHostWorldRoster(peer, records, state.Revision);

            // Phase 5.3-E: alongside the roster, send the semantic level manifest so the client
            // can diff its provisional world and reconcile before runtime sync.
            if (Plugin.Cfg.EnableHostLevelManifest.Value)
            {
                var manifest = NetGameplayProbeManager.BuildLevelManifest("Host");
                if (manifest != null) SendHostLevelManifest(manifest);
            }
        }

        private void SendHostWorldRoster(NetPeer peer, List<NetWorldEntityRecord> records, int revision)
        {
            try
            {
                var w = NetMessage.For(NetMessageType.HostWorldRoster);
                w.Put(revision);
                NetWorldEntityRosterCodec.Write(w, records);
                peer.Send(w, DeliveryMethod.ReliableOrdered);
                NetLogger.Info($"[WorldRoster] Host sent roster peer={peer.Address} count={records.Count} revision={revision}");
            }
            catch (Exception ex)
            {
                NetLogger.Warn($"[WorldRoster] Host failed to send roster: {ex.Message}");
            }
        }

        private void HandleHostWorldRoster(NetPeer peer, NetDataReader reader)
        {
            try
            {
                if (_mode != NetMode.Client) return;
                int revision = reader.GetInt();
                var records = NetWorldEntityRosterCodec.Read(reader);
                NetGameplayProbeManager.ProcessHostWorldRoster(records, revision);
            }
            catch (Exception ex)
            {
                NetLogger.Warn($"[WorldRoster] Client failed to process roster: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------ Phase 5.0 attack phase event

        internal void ReportHostAttackPhaseEvent(NetHostAttackPhaseEvent evt)
        {
            if (_mode != NetMode.Host || _net == null) return;
            if (!Plugin.Cfg.EnableHostAttackPhaseEvents.Value) return;
            if (!Plugin.Cfg.EnableHostDrivenEnemyProxy.Value) return;
            if (evt == null) return;
            if (_clients.Count == 0) return;

            foreach (var peer in _clients.ToArray())
                SendHostAttackPhaseEvent(peer, evt);
        }

        private void SendHostAttackPhaseEvent(NetPeer peer, NetHostAttackPhaseEvent evt)
        {
            try
            {
                var w = NetMessage.For(NetMessageType.HostAttackPhaseEvent);
                NetHostAttackPhaseEventCodec.Write(w, evt);
                peer.Send(w, DeliveryMethod.ReliableUnordered);
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[AttackPhase] Failed to send: {ex.Message}");
            }
        }

        private void HandleHostAttackPhaseEvent(NetPeer peer, NetDataReader reader)
        {
            try
            {
                if (_mode != NetMode.Client) return;
                if (!Plugin.Cfg.EnableHostDrivenEnemyProxy.Value) return;
                if (!Plugin.Cfg.EnableHostAttackPhaseEvents.Value) return;

                if (!NetHostAttackPhaseEventCodec.TryRead(reader, out var evt))
                {
                    NetLogger.Warn("[AttackPhase] Malformed HostAttackPhaseEvent packet");
                    return;
                }

                NetGameplayProbeManager.ProcessHostAttackPhaseEvent(evt);
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[AttackPhase] Receive error: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------ Phase 5.0 projectile visual spawn (P2)

        internal void ReportHostProjectileVisualSpawn(NetHostProjectileVisualSpawn evt)
        {
            if (_mode != NetMode.Host || _net == null) return;
            if (!Plugin.Cfg.EnableHostProjectileVisualSpawnEvent.Value) return;
            if (!Plugin.Cfg.EnableHostDrivenEnemyProxy.Value) return;
            if (evt == null) return;
            if (_clients.Count == 0) return;

            foreach (var peer in _clients.ToArray())
                SendHostProjectileVisualSpawn(peer, evt);
        }

        private void SendHostProjectileVisualSpawn(NetPeer peer, NetHostProjectileVisualSpawn evt)
        {
            try
            {
                var w = NetMessage.For(NetMessageType.HostProjectileVisualSpawn);
                NetHostProjectileVisualSpawnCodec.Write(w, evt);
                peer.Send(w, DeliveryMethod.ReliableUnordered);
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[ProjectileVisual] Failed to send: {ex.Message}");
            }
        }

        private void HandleHostProjectileVisualSpawn(NetPeer peer, NetDataReader reader)
        {
            try
            {
                if (_mode != NetMode.Client) return;
                if (!Plugin.Cfg.EnableHostProjectileVisualSpawnEvent.Value) return;

                if (!NetHostProjectileVisualSpawnCodec.TryRead(reader, out var evt))
                {
                    NetLogger.Warn("[ProjectileVisual] Malformed HostProjectileVisualSpawn packet");
                    return;
                }

                if (!NetRunStateBridge.TryGetLocalRunState(out var localState)) return;
                if (!evt.MatchesScene(localState)) return;

                if (Plugin.Cfg.LogHostProjectileVisualSpawn.Value)
                    NetLogger.Info($"[ProjectileVisual] Received hostIdx={evt.HostSpawnIndex} seq={evt.Sequence} origin=({evt.Origin.x:F1},{evt.Origin.y:F1},{evt.Origin.z:F1}) speed={evt.Velocity.magnitude:F1}m/s");

                // P2 TODO: spawn a visual-only no-damage proxy GameObject here.
                // For now this path is disabled by default (EnableHostProjectileVisualSpawnEvent=false).
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[ProjectileVisual] Receive error: {ex.Message}");
            }
        }

        // ----------------------------------------------------------------
        // Phase 5.1: Host-authoritative enemy health sync
        // ----------------------------------------------------------------

        internal void ReportHostEnemyDamageEvent(NetHostEnemyDamageEvent evt)
        {
            if (_mode != NetMode.Host || _net == null) return;
            if (!Plugin.Cfg.EnableHostEnemyDamageEventSync.Value) return;
            if (evt == null) return;
            if (_clients.Count == 0) return;

            foreach (var peer in _clients.ToArray())
                SendHostEnemyDamageEvent(peer, evt);
        }

        private void SendHostEnemyDamageEvent(NetPeer peer, NetHostEnemyDamageEvent evt)
        {
            try
            {
                var w = NetMessage.For(NetMessageType.HostEnemyDamageEvent);
                NetHostEnemyDamageEventCodec.Write(w, evt);
                peer.Send(w, DeliveryMethod.ReliableUnordered);
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[EnemyDamage] Failed to send: {ex.Message}");
            }
        }

        private void HandleHostEnemyDamageEvent(NetPeer peer, NetDataReader reader)
        {
            try
            {
                if (_mode != NetMode.Client) return;

                if (!NetHostEnemyDamageEventCodec.TryRead(reader, out var evt))
                {
                    NetLogger.Warn("[EnemyDamage] Malformed HostEnemyDamageEvent packet");
                    return;
                }

                NetGameplayProbeManager.ProcessHostEnemyDamageEvent(evt);
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[EnemyDamage] Receive error: {ex.Message}");
            }
        }

        internal void ReportHostEnemyHealthState(NetHostEnemyHealthState state)
        {
            if (_mode != NetMode.Host || _net == null) return;
            if (!Plugin.Cfg.EnableHostEnemyHealthStateSync.Value) return;
            if (state == null) return;
            if (_clients.Count == 0) return;

            foreach (var peer in _clients.ToArray())
                SendHostEnemyHealthState(peer, state);
        }

        private void SendHostEnemyHealthState(NetPeer peer, NetHostEnemyHealthState state)
        {
            try
            {
                var w = NetMessage.For(NetMessageType.HostEnemyHealthState);
                NetHostEnemyHealthStateCodec.Write(w, state);
                peer.Send(w, DeliveryMethod.ReliableUnordered);
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[EnemyHealth] Failed to send: {ex.Message}");
            }
        }

        private void HandleHostEnemyHealthState(NetPeer peer, NetDataReader reader)
        {
            try
            {
                if (_mode != NetMode.Client) return;

                if (!NetHostEnemyHealthStateCodec.TryRead(reader, out var state))
                {
                    NetLogger.Warn("[EnemyHealth] Malformed HostEnemyHealthState packet");
                    return;
                }

                NetGameplayProbeManager.ProcessHostEnemyHealthState(state);
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[EnemyHealth] Receive error: {ex.Message}");
            }
        }

        // ----------------------------------------------------------------
        // Phase 5.3-B: Client → Host hit request
        // ----------------------------------------------------------------

        internal void SendClientHitRequest(NetClientHitRequest request)
        {
            if (_mode != NetMode.Client || _net == null) return;
            if (!Plugin.Cfg.EnableClientHitRequest.Value) return;
            if (request == null) return;
            if (_hostPeer == null) return;

            try
            {
                var w = NetMessage.For(NetMessageType.ClientHitRequest);
                NetClientHitRequestCodec.Write(w, request);
                _hostPeer.Send(w, DeliveryMethod.ReliableUnordered);
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[ClientHit] Failed to send: {ex.Message}");
            }
        }

        private void HandleClientHitRequest(NetPeer peer, NetDataReader reader)
        {
            try
            {
                if (_mode != NetMode.Host) return;
                if (!Plugin.Cfg.EnableClientHitRequest.Value) return;

                if (!NetClientHitRequestCodec.TryRead(reader, out var request))
                {
                    NetLogger.Warn("[ClientHit] Malformed ClientHitRequest packet");
                    return;
                }

                if (!_peerIds.TryGetValue(peer, out var peerId))
                {
                    NetLogger.Warn($"[ClientHit] Ignoring hit request from unregistered peer {peer.Address}");
                    return;
                }

                request.ClientPeerId = peerId;
                NetGameplayProbeManager.ProcessClientHitRequest(request, peerId);
            }
            catch (Exception ex)
            {
                NetLogger.Warn($"[ClientHit] Receive error: {ex.Message}");
            }
        }

        // ----------------------------------------------------------------
        // Phase 5.3-E: Host-authoritative level manifest
        // ----------------------------------------------------------------

        internal void SendHostLevelManifest(NetLevelManifest manifest)
        {
            if (_mode != NetMode.Host || _net == null) return;
            if (!Plugin.Cfg.EnableHostLevelManifest.Value) return;
            if (manifest == null) return;
            if (_clients.Count == 0) return;

            foreach (var peer in _clients.ToArray())
            {
                try
                {
                    var w = NetMessage.For(NetMessageType.HostLevelManifest);
                    NetLevelManifestCodec.Write(w, manifest);
                    peer.Send(w, DeliveryMethod.ReliableOrdered);
                }
                catch (Exception ex)
                {
                    NetLogger.Warn($"[LevelManifest] Host failed to send manifest: {ex.Message}");
                }
            }
            NetLogger.Info($"[LevelManifest] Host sent manifest peers={_clients.Count} rooms={manifest.Header.RoomCount} units={manifest.Header.UnitCount} combat={manifest.Header.CombatEnemyCount} specials={manifest.Header.SpecialEventCount} genHash={manifest.Header.GenerationHash} runtimeHash={manifest.Header.RuntimeHash}");
        }

        private void HandleHostLevelManifest(NetPeer peer, NetDataReader reader)
        {
            try
            {
                if (_mode != NetMode.Client) return;
                if (!Plugin.Cfg.EnableHostLevelManifest.Value) return;

                if (!NetLevelManifestCodec.TryRead(reader, out var manifest))
                {
                    NetLogger.Warn("[LevelManifest] Malformed HostLevelManifest packet");
                    return;
                }

                NetGameplayProbeManager.ProcessHostLevelManifest(manifest);
            }
            catch (Exception ex)
            {
                NetLogger.Warn($"[LevelManifest] Receive error: {ex.Message}");
            }
        }

        // ----------------------------------------------------------------
        // Phase 5.3-F: Host → Client hit visual event
        // ----------------------------------------------------------------

        internal void ReportHostHitVisualEvent(NetHostHitVisualEvent evt)
        {
            if (_mode != NetMode.Host || _net == null) return;
            if (!Plugin.Cfg.EnableClientHitVisual.Value) return;
            if (evt == null || _clients.Count == 0) return;

            foreach (var peer in _clients.ToArray())
            {
                try
                {
                    var w = NetMessage.For(NetMessageType.HostHitVisualEvent);
                    NetHostHitVisualEventCodec.Write(w, evt);
                    peer.Send(w, DeliveryMethod.ReliableUnordered);
                }
                catch (Exception ex)
                {
                    if (Plugin.Cfg.EnableDebugLog.Value)
                        NetLogger.Debug($"[HitVisual] Failed to send: {ex.Message}");
                }
            }
        }

        private void HandleHostHitVisualEvent(NetPeer peer, NetDataReader reader)
        {
            try
            {
                if (_mode != NetMode.Client) return;
                if (!Plugin.Cfg.EnableClientHitVisual.Value) return;

                if (!NetHostHitVisualEventCodec.TryRead(reader, out var evt))
                {
                    NetLogger.Warn("[HitVisual] Malformed HostHitVisualEvent packet");
                    return;
                }

                NetGameplayProbeManager.ProcessHostHitVisualEvent(evt);
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[HitVisual] Receive error: {ex.Message}");
            }
        }

        private void HandleLevelSeedPollTimer()
        {
            if (_mode == NetMode.Off || _net == null) return;
            if (!Plugin.Cfg.EnableLevelSeedAuthority.Value) return;

            float now = Now();
            if (now - _lastLevelSeedPollTime < 0.5f) return;
            _lastLevelSeedPollTime = now;

            NetLevelSeed.ReportObservedGameManagerSeed("NetService.LevelSeedPoll");
        }

        private void HandleRunStateTimer()
        {
            if (_mode == NetMode.Off || _net == null) return;
            if (!Plugin.Cfg.EnableRunStateNegotiation.Value) return;
            float interval = Plugin.Cfg.RunStateBroadcastIntervalSeconds.Value;
            if (interval < 1f) interval = 1f;

            float now = Now();
            if (now - _lastRunStateSendTime < interval) return;
            _lastRunStateSendTime = now;
            SendLocalRunStateToConnectedPeers();
            SendHostSceneRequestsForDriftedClients();
        }

        private void SendLocalRunStateToConnectedPeers()
        {
            if (_net == null || _mode == NetMode.Off) return;
            if (!Plugin.Cfg.EnableRunStateNegotiation.Value) return;

            if (_mode == NetMode.Host)
            {
                foreach (var peer in _clients.ToArray())
                    SendRunState(peer, _runStates.LocalState);
            }
            else if (_hostPeer != null)
            {
                SendRunState(_hostPeer, _runStates.LocalState);
            }
        }

        private void SendRunState(NetPeer peer, NetRunState state)
        {
            try
            {
                if (state.Revision <= 0) return;
                var w = NetMessage.For(NetMessageType.RunStateUpdate);
                NetRunStateCodec.Write(w, state);
                peer.Send(w, DeliveryMethod.ReliableOrdered);
            }
            catch (Exception ex)
            {
                NetLogger.Warn($"[RunState] Failed to send run state: {ex.Message}");
            }
        }

        // ---- LiteNetLib event handlers ----

        private void OnConnectionRequest(ConnectionRequest request)
        {
            if (_mode != NetMode.Host) { request.Reject(); return; }

            if (_clients.Count >= Plugin.Cfg.MaxPlayers.Value - 1)
            {
                NetLogger.Info($"[Net] Rejected (server full): {_clients.Count + 1}/{Plugin.Cfg.MaxPlayers.Value}");
                request.Reject();
                return;
            }

            // Basic accept. Full key + version validation happens in HandshakeRequest message.
            request.Accept();
        }

        private void OnPeerConnected(NetPeer peer)
        {
            if (_mode == NetMode.Host)
            {
                if (!_clients.Contains(peer)) _clients.Add(peer);
                NetLogger.Info($"[Net] Peer connected from {peer.Address} — awaiting HandshakeRequest");
            }
            else
            {
                _hostPeer = peer;
                _clientConnectInProgress = false;
                _nextClientReconnectTime = 0f;
                NetLogger.Info($"[Net] Connected to host at {peer.Address} — sending HandshakeRequest");
                var w = NetMessage.For(NetMessageType.HandshakeRequest);
                NetHandshake.WriteRequest(w, Plugin.Cfg.PlayerName.Value);
                peer.Send(w, DeliveryMethod.ReliableOrdered);
            }
        }

        private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
        {
            try
            {
                TouchPeer(peer);

                var msgType = (NetMessageType)reader.GetByte();
                switch (msgType)
                {
                    case NetMessageType.HandshakeRequest:
                        HandleHandshakeRequest(peer, reader);
                        break;

                    case NetMessageType.HandshakeAccepted:
                        HandleHandshakeAccepted(peer, reader);
                        break;

                    case NetMessageType.HandshakeRejected:
                        string rejectReason = reader.GetString();
                        NetLogger.Info($"[Net] Handshake rejected: {rejectReason}");
                        peer.Disconnect();
                        break;

                    case NetMessageType.Ping:
                        HandlePing(peer);
                        break;

                    case NetMessageType.Pong:
                        TouchPeer(peer);
                        if (Plugin.Cfg.EnableDebugLog.Value)
                            NetLogger.Debug("[Net] Pong received");
                        break;

                    case NetMessageType.RunStateUpdate:
                        HandleRunStateUpdate(peer, reader);
                        break;

                    case NetMessageType.HostSceneRequest:
                        HandleHostSceneRequest(peer, reader);
                        break;

                    case NetMessageType.ClientSceneAck:
                        HandleClientSceneResponse(peer, reader, accepted: true);
                        break;

                    case NetMessageType.ClientSceneRefused:
                        HandleClientSceneResponse(peer, reader, accepted: false);
                        break;

                    case NetMessageType.ClientHostGenerationInputRequest:
                        HandleClientHostGenerationInputRequest(peer, reader);
                        break;

                    case NetMessageType.ClientTransitionRequest:
                        HandleClientTransitionRequest(peer, reader);
                        break;

                    case NetMessageType.PlayerTransformVisual:
                        HandlePlayerTransformVisual(peer, reader);
                        break;

                    case NetMessageType.HostEnemyDeathEvent:
                        HandleHostEnemyDeathEvent(peer, reader);
                        break;

                    case NetMessageType.HostEnemyStateSnapshot:
                        HandleHostEnemyStateSnapshot(peer, reader);
                        break;

                    case NetMessageType.ClientEnemyDeathClaim:
                        HandleClientEnemyDeathClaim(peer, reader);
                        break;

                    case NetMessageType.PlayerLifeState:
                        HandlePlayerLifeState(peer, reader);
                        break;

                    case NetMessageType.HostWorldRoster:
                        HandleHostWorldRoster(peer, reader);
                        break;

                    case NetMessageType.HostAttackPhaseEvent:
                        HandleHostAttackPhaseEvent(peer, reader);
                        break;

                    case NetMessageType.HostProjectileVisualSpawn:
                        HandleHostProjectileVisualSpawn(peer, reader);
                        break;

                    case NetMessageType.HostEnemyDamageEvent:
                        HandleHostEnemyDamageEvent(peer, reader);
                        break;

                    case NetMessageType.HostEnemyHealthState:
                        HandleHostEnemyHealthState(peer, reader);
                        break;

                    case NetMessageType.ClientHitRequest:
                        HandleClientHitRequest(peer, reader);
                        break;

                    case NetMessageType.HostLevelManifest:
                        HandleHostLevelManifest(peer, reader);
                        break;

                    case NetMessageType.HostHitVisualEvent:
                        HandleHostHitVisualEvent(peer, reader);
                        break;

                    case NetMessageType.ClientBossStartRequest:
                        HandleClientBossStartRequest(peer, reader);
                        break;

                    case NetMessageType.HostBossEncounterStart:
                        HandleHostBossEncounterStart(peer, reader);
                        break;

                    case NetMessageType.ClientBossDialogCommitRequest:
                        HandleClientBossDialogCommitRequest(peer, reader);
                        break;

                    case NetMessageType.HostBossDialogCommit:
                        HandleHostBossDialogCommit(peer, reader);
                        break;

                    case NetMessageType.HostBossState:
                        HandleHostBossState(peer, reader);
                        break;

                    case NetMessageType.HostBossDynamicSpawn:
                        HandleHostBossDynamicSpawn(peer, reader);
                        break;

                    case NetMessageType.ClientBossHitRequest:
                        HandleClientBossHitRequest(peer, reader);
                        break;

                    case NetMessageType.HostBossHitVisual:
                        HandleHostBossHitVisual(peer, reader);
                        break;

                    case NetMessageType.HostBossDiscreteEvent:
                        HandleHostBossDiscreteEvent(peer, reader);
                        break;

                    case NetMessageType.ClientLuciaEyeReport:
                        HandleClientLuciaEyeReport(peer, reader);
                        break;

                    case NetMessageType.HostLuciaEyeState:
                        HandleHostLuciaEyeState(peer, reader);
                        break;

                    case NetMessageType.HostLuciaDeath:
                        HandleHostLuciaDeath(peer, reader);
                        break;

                    case NetMessageType.HostWitchPhase:
                        HandleHostWitchPhase(peer, reader);
                        break;

                    case NetMessageType.HostWitchP2Manifest:
                        HandleHostWitchP2Manifest(peer, reader);
                        break;

                    case NetMessageType.HostWitchP2Result:
                        HandleHostWitchP2Result(peer, reader);
                        break;

                    case NetMessageType.HostRuntimeSpawn:
                        HandleHostRuntimeSpawn(peer, reader);
                        break;

                    case NetMessageType.PlayerWeaponFire:
                        HandlePlayerWeaponFire(peer, reader);
                        break;

                    case NetMessageType.PlayerHeldWeapon:
                        HandlePlayerHeldWeapon(peer, reader);
                        break;

                    case NetMessageType.GateState:
                        HandleGateState(peer, reader);
                        break;
                    case NetMessageType.TriggerDoors:
                        HandleTriggerDoors(peer, reader);
                        break;
                    case NetMessageType.ClientArenaEnter:
                        HandleClientArenaEnter(peer, reader);
                        break;
                    case NetMessageType.BreakableBreak:
                        HandleBreakableBreak(peer, reader);
                        break;

                    case NetMessageType.WorldPickupSpawn:
                        HandleWorldPickupSpawn(peer, reader);
                        break;

                    case NetMessageType.WorldPickupTakeRequest:
                        HandleWorldPickupTakeRequest(peer, reader);
                        break;

                    case NetMessageType.WorldPickupRemoved:
                        HandleWorldPickupRemoved(peer, reader);
                        break;

                    case NetMessageType.ClientRoomEnter:
                        HandleClientRoomEnter(peer, reader);
                        break;

                    case NetMessageType.HostRoomMembership:
                        HandleHostRoomMembership(peer, reader);
                        break;

                    case NetMessageType.Disconnect:
                        string disconnMsg = reader.GetString();
                        NetLogger.Info($"[Net] Disconnect: {disconnMsg}");
                        break;

                    case NetMessageType.SessionSnapshot:
                    case NetMessageType.PeerJoined:
                    case NetMessageType.PeerLeft:
                        NetLogger.Warn($"[Net] Session message type {(byte)msgType} is reserved but not implemented yet");
                        break;

                    default:
                        NetLogger.Warn($"[Net] Unknown message type: {(byte)msgType}");
                        break;
                }
            }
            catch (Exception ex)
            {
                NetLogger.Error($"[Net] Receive error: {ex.Message}");
            }
        }

        private void HandleHandshakeRequest(NetPeer peer, NetPacketReader reader)
        {
            if (_mode != NetMode.Host) return;

            if (!NetHandshake.TryReadRequest(reader, out var data))
            {
                RejectPeer(peer, "Malformed handshake packet");
                return;
            }
            if (data.Magic != NetHandshake.ProtocolMagic)
            {
                RejectPeer(peer, $"Wrong protocol magic: '{data.Magic}'");
                return;
            }
            if (data.ProtocolVersion != NetHandshake.ProtocolVersion)
            {
                RejectPeer(peer, $"Protocol version mismatch: client={data.ProtocolVersion} host={NetHandshake.ProtocolVersion}");
                return;
            }
            if (data.ConnectionKey != Plugin.Cfg.ConnectionKey.Value)
            {
                RejectPeer(peer, "Wrong connection key");
                return;
            }
            if (Plugin.Cfg.RequireSameModVersion.Value && data.ModVersion != ModInfo.Version)
            {
                RejectPeer(peer, $"Mod version mismatch: client={data.ModVersion} host={ModInfo.Version}");
                return;
            }

            float now = Now();
            var session = _sessions.RegisterRemoteClient(
                data.PlayerName,
                data.ModVersion,
                peer.Address.ToString(),
                Plugin.Cfg.MaxPlayers.Value,
                now);
            _peerIds[peer] = session.PeerId;

            NetLogger.Info($"[Net] Handshake OK — player='{session.PlayerName}' v={data.ModVersion}");
            NetLogger.Info($"[Session] Peer joined: id={session.PeerId} slot={session.Slot} name='{session.PlayerName}' endpoint={session.EndPoint}");

            var w = NetMessage.For(NetMessageType.HandshakeAccepted);
            NetHandshake.WriteAccepted(
                w,
                session.PeerId,
                session.Slot,
                "host",
                Plugin.Cfg.PlayerName.Value,
                Plugin.Cfg.MaxPlayers.Value);
            peer.Send(w, DeliveryMethod.ReliableOrdered);
            SendRunState(peer, _runStates.LocalState);

            // Phase 5.3-K P0-3: proactively push the latest host generation input so a Client whose load
            // gate is waiting does not depend on a keypress or drift detection.
            if (Plugin.Cfg.EnableHostSceneRequestProtocol.Value)
                SendCurrentHostSceneRequestToPeer(peer, "handshake");
        }

        private void HandleHandshakeAccepted(NetPeer peer, NetPacketReader reader)
        {
            if (_mode != NetMode.Client) return;

            float now = Now();
            if (NetHandshake.TryReadAccepted(reader, out var data))
            {
                var local = _sessions.RegisterLocalClient(
                    data.AssignedPeerId,
                    data.AssignedSlot,
                    Plugin.Cfg.PlayerName.Value,
                    ModInfo.Version,
                    now);
                _sessions.RegisterRemoteHost(
                    data.HostPeerId,
                    data.HostPlayerName,
                    data.HostModVersion,
                    peer.Address.ToString(),
                    now);
                _peerIds[peer] = data.HostPeerId;
                _runStates.SetLocalIdentity(local.PeerId, local.PlayerName);

                NetLogger.Info("[Net] Handshake accepted by host — session established");
                NetLogger.Info($"[Session] Local session assigned: id={local.PeerId} slot={local.Slot} name='{local.PlayerName}'");
                NetLogger.Info($"[Session] Host session known: id={data.HostPeerId} name='{data.HostPlayerName}' maxPlayers={data.MaxPlayers}");
                SendRunState(peer, _runStates.LocalState);
            }
            else
            {
                // Backward-compatible fallback for older host builds that sent an empty accepted packet.
                _sessions.RegisterLocalClient("client-local", -1, Plugin.Cfg.PlayerName.Value, ModInfo.Version, now);
                _sessions.RegisterRemoteHost("host", "Host", "", peer.Address.ToString(), now);
                _peerIds[peer] = "host";
                _runStates.SetLocalIdentity("client-local", Plugin.Cfg.PlayerName.Value);
                NetLogger.Info("[Net] Handshake accepted by host — session established (legacy payload)");
                SendRunState(peer, _runStates.LocalState);
            }
        }

        private void HandleRunStateUpdate(NetPeer peer, NetPacketReader reader)
        {
            if (!Plugin.Cfg.EnableRunStateNegotiation.Value) return;

            if (!NetRunStateCodec.TryRead(reader, out var state))
            {
                NetLogger.Warn("[RunState] Malformed RunStateUpdate packet");
                return;
            }

            string peerId;
            if (_mode == NetMode.Host)
            {
                if (!_peerIds.TryGetValue(peer, out peerId))
                {
                    NetLogger.Warn($"[RunState] Ignoring state from unregistered peer {peer.Address}");
                    return;
                }
            }
            else
            {
                peerId = string.IsNullOrWhiteSpace(state.PeerId) ? "host" : state.PeerId;
            }

            _runStates.UpdateRemote(peerId, state, Now());
            if (Plugin.Cfg.EnableDebugLog.Value)
                NetLogger.Debug($"[RunState] Remote update: {state.ToCompactString()}");

            ReportRunStateDiagnostics(peerId);
            TrySendHostSceneRequest(peerId);
        }

        private void ReportRunStateDiagnostics(string peerId)
        {
            if (Plugin.Cfg.EnableHostSceneAuthority.Value && Plugin.Cfg.WarnOnClientSceneDrift.Value)
            {
                if (_mode == NetMode.Client && peerId == "host")
                {
                    if (_runStates.TryBuildHostAuthorityWarning(out var authorityWarning))
                        NetLogger.Warn(authorityWarning);
                    return;
                }

                if (_mode == NetMode.Host)
                {
                    if (_runStates.TryBuildClientSceneDriftWarning(peerId, out var driftWarning))
                        NetLogger.Warn(driftWarning);
                    return;
                }
            }

            if (Plugin.Cfg.WarnOnRunStateMismatch.Value && _runStates.TryBuildMismatchWarning(peerId, out var warning))
            {
                NetLogger.Warn(warning);
                return;
            }

            if (Plugin.Cfg.EnableDebugLog.Value && _runStates.TryBuildStateDifferenceNotice(peerId, out var stateNotice))
                NetLogger.Debug(stateNotice);
        }

        // ---- Phase 2.6 manual client scene follow ----

        private void HandleManualClientSceneFollowInput()
        {
            if (_net == null) return;
            if (!Plugin.Cfg.EnableHostSceneRequestProtocol.Value) return;

            // Phase 5.6-LK: Host toggles its 联机状态 (mod multiplayer on/off).
            if (_mode == NetMode.Host)
            {
                try
                {
                    var hk = Plugin.Cfg.HostLinkToggleKey.Value;
                    if (!IsUnsafeManualFollowKey(hk.MainKey) && hk.IsDown())
                        NetLinkState.ToggleHost("host key");
                }
                catch (Exception ex) { NetLogger.Warn($"[LinkState] host toggle key check failed: {ex.Message}"); }
                return;
            }

            if (_mode != NetMode.Client || _hostPeer == null) return;
            if (!Plugin.Cfg.EnableManualClientSceneFollow.Value) return;

            // Phase 5.6-LK: PageUp UNLINKS — leave 联机状态 and play the local run independently.
            try
            {
                var uk = Plugin.Cfg.ClientUnlinkKey.Value;
                if (!IsUnsafeManualFollowKey(uk.MainKey) && uk.IsDown())
                {
                    NetLinkState.SetClientLinked(false, "user-unlink-key");
                    return;
                }
            }
            catch (Exception ex) { NetLogger.Warn($"[LinkState] unlink key check failed: {ex.Message}"); }

            try
            {
                var shortcut = Plugin.Cfg.ManualClientSceneFollowKey.Value;
                if (IsUnsafeManualFollowKey(shortcut.MainKey))
                {
                    if (!_manualFollowKeyWarningShown)
                    {
                        _manualFollowKeyWarningShown = true;
                        NetLogger.Warn($"[SceneFollow] Manual follow key '{shortcut}' is reserved by SULFUR DevTools/F-key bindings and is ignored to avoid toggling invulnerability. Change NetworkSceneAuthority.ManualClientSceneFollowKey to PageDown or another non-F-key.");
                    }
                    return;
                }

                if (!shortcut.IsDown()) return;
            }
            catch (Exception ex)
            {
                NetLogger.Warn($"[SceneFollow] Manual follow key check failed: {ex.Message}");
                return;
            }

            // Phase 5.6-LK: PageDown LINKS — enter 联机状态 then follow the host.
            NetLinkState.SetClientLinked(true, "user-link-key");
            AttemptManualClientSceneFollow();
        }

        private static bool IsUnsafeManualFollowKey(KeyCode key)
        {
            // SULFUR binds several F-keys to DevTools features. F6 was observed toggling
            // Player invulnerability / GodMode, which then caused infinite ammo and no-damage behavior.
            // Treat all F-keys as reserved for this early co-op prototype.
            return key >= KeyCode.F1 && key <= KeyCode.F15;
        }

        private void AttemptManualClientSceneFollow()
        {
            if (_mode != NetMode.Client || _hostPeer == null) return;

            if (!_sceneRequests.HasLastHostRequestTarget(out var request))
            {
                // Phase 5.6-LK-P5: the host only broadcasts a HostSceneRequest on scene-NAME drift, so when both
                // players sit in the SAME-named hub (ChurchHub) with different instance seeds it never sends one,
                // and the client could never join from a safe zone. A LINKED client has explicitly asked to join,
                // so fall back to the host's RUN STATE (which carries the host's hub scene + seed) directly even
                // when ManualClientSceneFollowRequiresHostRequest is set.
                if (Plugin.Cfg.ManualClientSceneFollowRequiresHostRequest.Value && !NetLinkState.ClientLinked)
                {
                    NetLogger.Info("[SceneFollow] Manual follow ignored: no HostSceneRequest has been received yet.");
                    return;
                }

                if (!_runStates.TryGetRemote("host", out var hostState) || !hostState.HasLevel)
                {
                    NetLogger.Info("[SceneFollow] Manual follow ignored: no host run state target is known.");
                    return;
                }

                NetLogger.Info($"[SceneFollow] Manual follow using host run state (no HostSceneRequest; linked={NetLinkState.ClientLinked}) target={hostState.ChapterName}:{hostState.LevelIndex} seed={(hostState.HasLevelSeed ? hostState.LevelSeed.ToString() : "?")}");

                request = new NetHostSceneRequest
                {
                    RequestId       = "manual-host-state-" + (int)(Now() * 1000f),
                    HostPeerId      = string.IsNullOrWhiteSpace(hostState.PeerId) ? "host" : hostState.PeerId,
                    HostPlayerName  = hostState.PlayerName,
                    ChapterName     = hostState.ChapterName,
                    LevelIndex      = hostState.LevelIndex,
                    LoadingMode     = hostState.LoadingMode,
                    SpawnIdentifier = hostState.SpawnIdentifier,
                    HostGameState   = hostState.GameState,
                    HasLevelSeed    = hostState.HasLevelSeed,
                    LevelSeed       = hostState.LevelSeed,
                    LevelGenerator  = hostState.LevelGenerator,
                    HostRevision    = hostState.Revision,
                    Reason          = "ManualFollowHostRunState",
                    AutoLoadAllowed = false,
                };
            }

            // Phase 5.6-DL P2: refuse a STALE manual follow. After an all-players-down the host returns to the
            // hub, but the client may still have the PRE-DEATH combat request cached as its "last host request".
            // Following it on a key press double-loads (combat level then hub) and corrupts the player spawn —
            // the player unit unbinds and only the camera is controllable. If the host's CURRENT known run state
            // has moved away from the cached request's scene, the cached request is stale: refuse and let the hub
            // session-return auto-follow bring the client to the host's actual location.
            if (request.HasTargetScene
                && _runStates.TryGetRemote("host", out var currentHostState) && currentHostState.HasLevel
                && !NetSceneName.SameScene(currentHostState.ChapterName, currentHostState.LevelIndex, request.ChapterName, request.LevelIndex))
            {
                NetLogger.Info($"[SceneFollow] Manual follow refused: cached host request target={request.TargetSceneKey()} is stale (host now at {currentHostState.ChapterName}:{currentHostState.LevelIndex}); auto-follow will reconcile.");
                return;
            }

            if (_runStates.LocalState.HasLevel
                && request.HasTargetScene
                && NetSceneName.SameScene(_runStates.LocalState.ChapterName, _runStates.LocalState.LevelIndex, request.ChapterName, request.LevelIndex)
                && (!Plugin.Cfg.RequireSameLevelSeedForSceneMatch.Value || (request.HasLevelSeed && _runStates.LocalState.HasLevelSeed && request.LevelSeed == _runStates.LocalState.LevelSeed)))
            {
                var already = _sceneRequests.BuildClientResponse(
                    request,
                    _runStates.LocalState,
                    _runStates.LocalState.PeerId,
                    Plugin.Cfg.PlayerName.Value,
                    "Manual follow skipped; client is already in requested host scene.",
                    "Arrived");
                SendClientSceneResponse(_hostPeer, already, ack: true);
                return;
            }

            _sceneRequests.RecordManualFollowAttempt(request);
            NetLogger.Info($"[SceneFollow] Manual follow requested by user: {request.ToCompactString()}");

            if (NetManualSceneFollower.TryFollow(request, out var result))
            {
                NetLogger.Info($"[SceneFollow] {result}");
                // Phase 5.4-A P0-D: manual follow is an explicit user action — never blocked by ClientJoinMode.
                // The session is marked joined when the resulting local GoToLevel confirms the target scene.
                NetLogger.Info($"[JoinFlow] manual follow confirmed target={request.TargetSceneKey()} graph={(string.IsNullOrEmpty(request.GraphName) ? "?" : request.GraphName)} preserving-local-run=false");

                var response = _sceneRequests.BuildClientResponse(
                    request,
                    _runStates.LocalState,
                    _runStates.LocalState.PeerId,
                    Plugin.Cfg.PlayerName.Value,
                    "Manual scene follow invoked; waiting for local GoToLevel/RunState update.",
                    "FollowInvoked");
                SendClientSceneResponse(_hostPeer, response, ack: response.IsInTargetScene);
            }
            else
            {
                NetLogger.Warn($"[SceneFollow] Manual follow failed: {result}");

                var response = _sceneRequests.BuildClientResponse(
                    request,
                    _runStates.LocalState,
                    _runStates.LocalState.PeerId,
                    Plugin.Cfg.PlayerName.Value,
                    "Manual scene follow failed: " + result,
                    "Refused");
                SendClientSceneResponse(_hostPeer, response, ack: false);
            }
        }

        // ---- Phase 2.5 host-scene request protocol skeleton ----

        private void SendHostSceneRequestsForDriftedClients()
        {
            if (_mode != NetMode.Host || _net == null) return;
            if (!Plugin.Cfg.EnableHostSceneAuthority.Value) return;
            if (!Plugin.Cfg.EnableHostSceneRequestProtocol.Value) return;
            if (!Plugin.Cfg.AutoSendHostSceneRequestOnDrift.Value) return;
            if (!_runStates.LocalState.HasLevel) return;

            foreach (var kv in _peerIds)
                TrySendHostSceneRequest(kv.Value);
        }

        private void TrySendHostSceneRequest(string peerId)
        {
            if (_mode != NetMode.Host || _net == null) return;
            if (!Plugin.Cfg.EnableHostSceneAuthority.Value) return;
            if (!Plugin.Cfg.EnableHostSceneRequestProtocol.Value) return;
            if (!Plugin.Cfg.AutoSendHostSceneRequestOnDrift.Value) return;
            if (string.IsNullOrWhiteSpace(peerId)) return;
            if (!_runStates.TryGetRemote(peerId, out var clientState)) return;

            var peer = FindPeerById(peerId);
            if (peer == null) return;

            if (!_sceneRequests.TryCreateHostSceneRequest(
                peerId,
                Plugin.Cfg.PlayerName.Value,
                _runStates.LocalState,
                clientState,
                Now(),
                Plugin.Cfg.HostSceneRequestIntervalSeconds.Value,
                out var request))
            {
                return;
            }

            SendHostSceneRequest(peer, request);
        }

        private void SendHostSceneRequest(NetPeer peer, NetHostSceneRequest request)
        {
            // Phase 5.6-LK: when the host's 联机状态 is off it acts single-player — broadcast no scene changes.
            if (_mode == NetMode.Host && !NetLinkState.HostLinked) return;
            try
            {
                // Classify the request's target using BOTH chapter and graph. Hubs/safezones (ChurchHub,
                // ChurchHub_Xmas, ...) are MakerGraph-generated and DO need a seed; only a pure menu may be seedless.
                var snap = NetGenerationInputCapture.LastFinalizedSnapshot;
                bool hubTarget = request.HasTargetScene
                    && (NetSceneClassify.IsHubOrSafeZoneChapter(request.ChapterName)
                        || NetSceneClassify.IsHubOrSafeZoneGraph(request.GraphName)
                        || NetClientLoadGate.IsHubOrMenuTarget(request.ChapterName, request.LoadingMode, request.LevelIndex));
                bool combatTarget = request.HasTargetScene && !hubTarget
                    && NetClientLoadGate.IsCombatTarget(request.ChapterName, request.LoadingMode, request.LevelIndex);
                bool snapIsHub = snap != null && snap.Finalized && NetSceneClassify.IsHubOrSafeZoneGraph(snap.GraphName);
                bool deferHubReturn = false;

                // Phase 5.6-DL: while the host is dead and respawning back to the hub, do NOT advertise the combat
                // level it is abandoning. The host's stale autoLoad combat request (sent just before its own
                // GoToLevel(mode=Death) on an all-players-down) was what dragged the client through a wasteful
                // combat load before the hub — producing the doubled inventory. The hub-return request still goes.
                if (combatTarget && NetPlayerLifeManager.IsLocalDeathRespawnInProgress)
                {
                    NetLogger.Info($"[HostSceneRequest] suppress combat advertisement during death-respawn target={request.TargetSceneKey()} graph={(string.IsNullOrEmpty(request.GraphName) ? "?" : request.GraphName)}");
                    return;
                }

                // Phase 5.3-L P0-B: for a COMBAT target the chapter/level/seed/graph/usedSets must come from the
                // finalized GenerationInputSnapshot (the host's real current generation), NOT the stale LocalState.
                if (combatTarget && snap != null && snap.Finalized && !snapIsHub)
                {
                    if (!string.IsNullOrWhiteSpace(snap.Chapter) && snap.Chapter != "<unknown>")
                        request.ChapterName = snap.Chapter;
                    request.LevelIndex      = snap.LevelIndex;
                    request.HasLevelSeed    = snap.HasSeed;
                    request.LevelSeed       = snap.Seed;
                    request.GraphName       = snap.GraphName;
                    request.GenerationRunId = snap.RunId;
                    if (!string.IsNullOrWhiteSpace(snap.LoadingMode)) request.LoadingMode = snap.LoadingMode;
                    if (!string.IsNullOrWhiteSpace(snap.SpawnIdentifier)) request.SpawnIdentifier = snap.SpawnIdentifier;
                    request.SetUsedSets(snap.UsedSets);
                    NetGenerationInputCapture.MatchedOnSend++;
                    NetSceneFollowDiag.IncCaptured();
                    NetSceneFollowDiag.IncSent();
                    NetLogger.Info($"[HostSceneRequestBuild] source={(snap.FromTransition ? "finalizedTransition" : "finalizedSnapshot")} target={request.TargetSceneKey()} graph={(string.IsNullOrEmpty(request.GraphName) ? "?" : request.GraphName)} seed={(request.HasLevelSeed ? request.LevelSeed.ToString() : "?")} usedChunks={request.UsedChunksThisRun.Count}");
                }
                else if (combatTarget)
                {
                    var fallback = NetGameManagerUsedSets.LastLocalSnapshot;
                    if (fallback != null)
                    {
                        request.SetUsedSets(fallback);
                        NetSceneFollowDiag.IncSent();
                    }
                    else request.HasUsedSets = false;
                    NetGenerationInputCapture.FallbackOnSend++;
                    NetLogger.Warn($"[HostSceneRequestBuild] source=LocalStateFallback WARNING may be stale target={request.TargetSceneKey()} graph={(string.IsNullOrEmpty(request.GraphName) ? "?" : request.GraphName)} seed={(request.HasLevelSeed ? request.LevelSeed.ToString() : "?")}");
                }
                else if (hubTarget && snapIsHub && snap!.HasSeed)
                {
                    // Phase 5.4-D-0 P0-C: hub/safezone return ALSO uses the finalized snapshot (corrected hub
                    // identity + real graph seed) so the client reproduces the SAME ChurchHub the host generated.
                    if (!string.IsNullOrWhiteSpace(snap.Chapter) && snap.Chapter != "<unknown>")
                        request.ChapterName = snap.Chapter;
                    request.LevelIndex      = snap.LevelIndex;
                    request.HasLevelSeed    = snap.HasSeed;
                    request.LevelSeed       = snap.Seed;
                    request.GraphName       = snap.GraphName;
                    request.GenerationRunId = snap.RunId;
                    request.HasUsedSets     = false;
                    NetSceneFollowDiag.HubReturnFinalizedRequestSent++;
                    NetLogger.Info($"[HostSceneRequestBuild] source={(snap.FromTransition ? "finalizedTransition" : "finalizedSnapshot")} target={request.TargetSceneKey()} graph={(string.IsNullOrEmpty(request.GraphName) ? "?" : request.GraphName)} seed={(request.HasLevelSeed ? request.LevelSeed.ToString() : "?")} autoLoad=True reason=session-return-finalized");
                }
                else if (hubTarget)
                {
                    // Phase 5.4-D-0 P0-B: the host is heading to a hub but the finalized hub seed is not ready yet.
                    // Send a PRELIMINARY request only (AutoLoadAllowed=false) so the client records the target but
                    // does NOT GoToLevel with a missing seed (which would generate a divergent local hub). The
                    // finalized request follows once GenerationInputSnapshot finalizes the hub graph+seed.
                    deferHubReturn = true;
                    request.HasUsedSets = false;
                    NetSceneFollowDiag.HubReturnDeferredMissingSeed++;
                    NetSceneFollowDiag.HubReturnPreliminaryRequestSent++;
                    NetLogger.Info($"[HostSceneRequestBuild] defer hub return until finalized target={request.TargetSceneKey()} graph={(string.IsNullOrEmpty(request.GraphName) ? "?" : request.GraphName)} seed={(request.HasLevelSeed ? request.LevelSeed.ToString() : "?")} reason=missing-finalized-hub-seed");
                }
                else
                {
                    // Pure menu / unknown — the target comes straight from LocalState; no generation seed/used sets.
                    request.HasUsedSets = false;
                    NetLogger.Info($"[HostSceneRequestBuild] source=localState target={request.TargetSceneKey()} graph={(string.IsNullOrEmpty(request.GraphName) ? "?" : request.GraphName)} seed={(request.HasLevelSeed ? request.LevelSeed.ToString() : "?")} reason=non-combat");
                }

                // Phase 5.4-D-0 P1: unified hub-return classification diagnostic so special/new safe zones can be
                // understood from the log without guessing.
                if (hubTarget)
                    NetLogger.Info($"[HubReturnDiag] localStateTarget={request.TargetSceneKey()} finalizedGraph={(snap?.GraphName ?? "?")} finalizedChapter={(snap?.Chapter ?? "?")} snapIsHub={snapIsHub} snapSeed={(snap != null && snap.HasSeed ? snap.Seed.ToString() : "?")} reqGraph={(string.IsNullOrEmpty(request.GraphName) ? "?" : request.GraphName)} reqSeed={(request.HasLevelSeed ? request.LevelSeed.ToString() : "?")} action={(deferHubReturn ? "defer" : "finalized")}");

                // Authorize auto-load for combat targets and for FINALIZED hub/safezone returns. A deferred (not
                // finalized) hub return stays AutoLoadAllowed=false. The Client additionally gates hub auto-load on
                // SessionJoinedHost, so an unjoined player in their own hub is never pulled away.
                bool autoFollowCfg = false;
                try { autoFollowCfg = Plugin.Cfg.EnableAutoFollowHostSceneRequest.Value; } catch { }
                request.AutoLoadAllowed = autoFollowCfg && (combatTarget || (hubTarget && !deferHubReturn));

                // Phase 5.3-M P0-B: normalize the request id to the true target (graph+level+seed) instead of a
                // stale Act_01_Caves-0, so it is not mistaken for a level-0 request in diagnostics.
                string graphTag = string.IsNullOrEmpty(request.GraphName) ? "?" : request.GraphName;
                string seedTag = request.HasLevelSeed ? request.LevelSeed.ToString() : "noseed";
                request.RequestId = $"hsr-{PeerIdOrAddress(peer)}-{request.TargetSceneKey().Replace(':', '-')}-{graphTag}-seed{seedTag}";

                // Phase 5.3-M/5.4-B/5.4-D-0 P1-G: register the lightweight load barrier for combat + finalized
                // hub-return auto-loads.
                if (request.AutoLoadAllowed)
                    NetLoadBarrier.MarkPending(
                        PeerIdOrAddress(peer),
                        NetLoadBarrier.RunKeyFor(request.ChapterName, request.LevelIndex, request.HasLevelSeed, request.LevelSeed, request.GraphName),
                        request.GraphName,
                        hubTarget ? "session-return-finalized" : "combat");

                var w = NetMessage.For(NetMessageType.HostSceneRequest);
                NetSceneRequestCodec.WriteHostRequest(w, request);
                peer.Send(w, DeliveryMethod.ReliableOrdered);
                NetLogger.Info($"[SceneRequest] HostSceneRequest sent to {PeerIdOrAddress(peer)}: {request.ToCompactString()} graph={(string.IsNullOrEmpty(request.GraphName) ? "?" : request.GraphName)} usedSets={(request.HasUsedSets ? $"chunks={request.UsedChunksThisRun.Count},eventsRun={request.UsedEventsThisRun.Count},eventsEnv={request.UsedEventsThisEnvironment.Count}" : "none")} action={(request.AutoLoadAllowed ? "auto-load" : "request-only")}");
            }
            catch (Exception ex)
            {
                NetLogger.Warn($"[SceneRequest] Failed to send HostSceneRequest: {ex.Message}");
            }
        }

        // ---- Phase 5.3-K: client actively pulls host generation input while its load gate waits ----

        private void HandleClientLoadGateRequestTimer()
        {
            if (_mode != NetMode.Client || _net == null || _hostPeer == null) return;
            if (!Plugin.Cfg.EnableHostSceneRequestProtocol.Value) return;
            if (NetClientLoadGate.TryConsumeHostInputRequestDue(out int attempt))
                SendClientHostGenerationInputRequest(attempt);

            // Phase 5.6-DL-Q2: also relay the client's intended transition target so the host LEADS there.
            if (NetClientLoadGate.TryConsumeTransitionRelayDue(out var chapter, out int level, out var mode, out var spawn, out int relayAttempt))
                SendClientTransitionRequest(chapter, level, mode, spawn, relayAttempt);
        }

        private void SendClientTransitionRequest(string chapter, int level, string mode, string spawn, int attempt)
        {
            if (_hostPeer == null) return;
            try
            {
                NetRunStateBridge.TryGetLocalRunState(out var local);
                var w = NetMessage.For(NetMessageType.ClientTransitionRequest);
                w.Put(chapter ?? "");
                w.Put(level);
                w.Put(mode ?? "");
                w.Put(spawn ?? "");
                w.Put(local.ChapterName ?? "");   // client's CURRENT scene, for the host's same-level validation
                w.Put(local.LevelIndex);
                w.Put(attempt);
                _hostPeer.Send(w, DeliveryMethod.ReliableOrdered);
                if (attempt <= 1)
                    NetLogger.Info($"[TransitionRelay] client requesting host lead transition target={chapter}:{level} mode={mode} spawn={spawn} from={local.ChapterName}:{local.LevelIndex}");
            }
            catch (Exception ex)
            {
                NetLogger.Warn($"[TransitionRelay] failed to send client transition request: {ex.Message}");
            }
        }

        private void SendClientHostGenerationInputRequest(int attempt)
        {
            if (_hostPeer == null) return;
            try
            {
                var w = NetMessage.For(NetMessageType.ClientHostGenerationInputRequest);
                w.Put("gate-wait");
                w.Put(attempt);
                _hostPeer.Send(w, DeliveryMethod.ReliableOrdered);
            }
            catch (Exception ex)
            {
                NetLogger.Warn($"[ClientLoadGate] failed to send host generation input request: {ex.Message}");
            }
        }

        private void HandleClientHostGenerationInputRequest(NetPeer peer, NetPacketReader reader)
        {
            if (_mode != NetMode.Host) return;
            if (!Plugin.Cfg.EnableHostSceneRequestProtocol.Value) return;

            string reason = "request";
            int attempt = 0;
            try { if (reader.AvailableBytes > 0) reason = reader.GetString(); } catch { }
            try { if (reader.AvailableBytes >= 4) attempt = reader.GetInt(); } catch { }

            string peerId = _peerIds.TryGetValue(peer, out var mapped) ? mapped : peer.Address.ToString();
            NetLogger.Info($"[HostGenerationInput] pull request from peer={peerId} reason={reason} attempt={attempt}");
            SendCurrentHostSceneRequestToPeer(peer, "request");
        }

        // Phase 5.6-DL-Q2: dedup the same client-requested transition target within a short window (the client
        // retries until it observes the host move).
        private string _lastClientTransitionKey = "";
        private float  _lastClientTransitionTime;

        // Phase 5.6-DL-Q2: a joined client walked into an exit and asked the host to LEAD the transition. The host
        // validates (client is in the host's scene, host not already there / not loading), then performs the
        // transition itself with NO forced seed — it generates authoritatively. The existing finalized broadcast
        // then brings the gated client along. Host stays the single source of truth for generation.
        private void HandleClientTransitionRequest(NetPeer peer, NetPacketReader reader)
        {
            string chapter = "", mode = "", spawn = "", clientChapter = "";
            int level = -1, clientLevel = -1, attempt = 0;
            try
            {
                chapter       = reader.GetString();
                level         = reader.GetInt();
                mode          = reader.GetString();
                spawn         = reader.GetString();
                clientChapter = reader.GetString();
                clientLevel   = reader.GetInt();
                if (reader.AvailableBytes >= 4) attempt = reader.GetInt();
            }
            catch (Exception ex) { NetLogger.Warn($"[TransitionRelay] malformed client transition request: {ex.Message}"); return; }

            if (_mode != NetMode.Host) return;
            if (!Plugin.Cfg.EnableClientTransitionRelay.Value) return;
            if (!Plugin.Cfg.EnableHostSceneRequestProtocol.Value) return;

            string peerId = _peerIds.TryGetValue(peer, out var mapped) ? mapped : peer.Address.ToString();

            // Phase 5.6-LK: the host honors client relays only while its own 联机状态 is on (mod multiplayer active).
            if (!NetLinkState.HostLinked)
            {
                NetLogger.Info($"[TransitionRelay] ignore from {peerId}: host multiplayer (联机状态) is off");
                return;
            }

            if (string.IsNullOrWhiteSpace(chapter) || level < 0)
            {
                NetLogger.Warn($"[TransitionRelay] reject from {peerId}: bad target {chapter}:{level}");
                return;
            }

            var host = _runStates.LocalState;
            if (!host.HasLevel)
            {
                NetLogger.Info($"[TransitionRelay] reject from {peerId}: host has no level yet");
                return;
            }

            // Phase 5.6-LK: NO same-scene guard anymore. A relay only ever comes from a LINKED client (its gate
            // relays only while 联机状态 is on), and a linked client explicitly leads the host wherever it goes —
            // including F3 jumps, returning to a safe zone, or a scene the host is not currently in. Just note it.
            if (!NetSceneName.SameScene(host.ChapterName, host.LevelIndex, clientChapter, clientLevel))
                NetLogger.Info($"[TransitionRelay] cross-scene lead from {peerId}: client={clientChapter}:{clientLevel} host={host.ChapterName}:{host.LevelIndex} target={chapter}:{level}");

            // Phase F3-Reload: a same-scene relay from a linked client is an explicit RELOAD-IN-PLACE (F3 to the level
            // both ends are already in). The client's gate now relays + waits for us to RE-LEAD instead of self-
            // reloading off our stale "I'm here" request (which diverged into its own fresh instance — Log147). Lead
            // the reload so BOTH regenerate the level together (resets an in-progress fight, by design — the user's
            // chosen behaviour). A client merely catching up to our scene follows our existing broadcast and never
            // relays, so a same-scene relay reaching us is always an explicit reload.
            bool reloadInPlace = NetSceneName.SameScene(host.ChapterName, host.LevelIndex, chapter, level);
            bool reloadInPlaceEnabled; try { reloadInPlaceEnabled = Plugin.Cfg.EnableClientReloadInPlaceRelay.Value; } catch { reloadInPlaceEnabled = false; }
            if (reloadInPlace && !reloadInPlaceEnabled)
            {
                // Legacy: the in-flight / finalized broadcast already brings the client; do nothing.
                NetLogger.Info($"[TransitionRelay] ignore from {peerId}: host already at target {chapter}:{level} (reload-in-place off)");
                return;
            }
            if (reloadInPlace)
                NetLogger.Info($"[TransitionRelay] reload-in-place lead from {peerId}: host re-generating current level {chapter}:{level}");

            // Host busy loading — let the in-flight transition settle; the client follows it.
            if (!string.IsNullOrEmpty(host.GameState) && host.GameState.ToLowerInvariant().Contains("load"))
            {
                NetLogger.Info($"[TransitionRelay] defer from {peerId}: host is loading (state={host.GameState})");
                return;
            }

            // Phase 5.6-LK-P2 (Type B): the host is mid its OWN transition (the Cinematic window before GameState
            // flips to Loading, where run state still reads the old level). Defer so we never double-generate the
            // same next level. The client keeps retrying; once the host arrives it sees "already at target".
            if (NetHostTransitionGuard.IsActive)
            {
                NetLogger.Info($"[TransitionRelay] defer from {peerId}: host mid-transition (guard active) target={chapter}:{level}");
                return;
            }

            string key = chapter + ":" + level;
            float now = Now();
            if (key == _lastClientTransitionKey && now - _lastClientTransitionTime < 5f)
                return;
            _lastClientTransitionKey = key;
            _lastClientTransitionTime = now;

            NetLogger.Info($"[TransitionRelay] host LEADING client-requested transition target={chapter}:{level} mode={mode} spawn={spawn} requestedBy={peerId} attempt={attempt}");

            var req = new NetHostSceneRequest
            {
                ChapterName     = chapter,
                LevelIndex      = level,
                LoadingMode     = mode ?? "",
                SpawnIdentifier = spawn ?? "",
                HasLevelSeed    = false,   // host generates authoritatively
                AutoLoadAllowed = false,
            };

            bool ok; string result;
            try { ok = NetManualSceneFollower.TryFollow(req, out result); }
            catch (Exception ex) { ok = false; result = ex.GetType().Name + ": " + ex.Message; }

            if (ok) NetLogger.Info($"[TransitionRelay] host transition invoked: {result}");
            else NetLogger.Warn($"[TransitionRelay] host transition failed: {result}");
        }

        // Build a HostSceneRequest from the Host's CURRENT run state and send it (with the matching
        // StartLevelRoutineGraph snapshot's used sets) to a specific peer. Used on handshake and on pull.
        private void SendCurrentHostSceneRequestToPeer(NetPeer peer, string reason)
        {
            if (_mode != NetMode.Host) return;
            bool onHandshake = reason == "handshake";
            string peerId = _peerIds.TryGetValue(peer, out var mapped) ? mapped : peer.Address.ToString();

            var hostState = _runStates.LocalState;
            bool haveFinalized = NetGenerationInputCapture.LastFinalizedSnapshot != null;
            if (!hostState.HasLevel && !haveFinalized)
            {
                if (onHandshake) NetSceneFollowDiag.HostGenerationInputNoSnapshotOnHandshake++;
                else NetSceneFollowDiag.HostGenerationInputNoSnapshotOnRequest++;
                NetLogger.Warn($"[HostGenerationInput] no latest snapshot to send reason={reason} peer={peerId} (host not in a level yet)");
                return;
            }

            var request = new NetHostSceneRequest
            {
                RequestId       = $"hgi-{peerId}-{hostState.LevelInstanceKey().Replace(':', '-').Replace('#', '-').Replace('=', '-')}-r{hostState.Revision}-{(int)(Now() * 1000f)}",
                HostPeerId      = string.IsNullOrWhiteSpace(hostState.PeerId) ? "host" : hostState.PeerId,
                HostPlayerName  = Plugin.Cfg.PlayerName.Value,
                ChapterName     = hostState.ChapterName,
                LevelIndex      = hostState.LevelIndex,
                LoadingMode     = hostState.LoadingMode,
                SpawnIdentifier = hostState.SpawnIdentifier,
                HostGameState   = hostState.GameState,
                HasLevelSeed    = hostState.HasLevelSeed,
                LevelSeed       = hostState.LevelSeed,
                LevelGenerator  = hostState.LevelGenerator,
                GraphName       = hostState.LevelGenerator, // 5.4-D-0: lets the send path classify hub vs combat
                HostRevision    = hostState.Revision,
                Reason          = onHandshake ? "HostGenerationInputHandshake" : "HostGenerationInputRequest",
                AutoLoadAllowed = false,
            };

            // SendHostSceneRequest attaches the matching snapshot's used sets + graph name and logs.
            SendHostSceneRequest(peer, request);

            if (onHandshake) NetSceneFollowDiag.HostGenerationInputSentOnHandshake++;
            else NetSceneFollowDiag.HostGenerationInputSentOnRequest++;
            int chunks = request.HasUsedSets ? request.UsedChunksThisRun.Count : 0;
            NetLogger.Info($"[HostGenerationInput] sending latest snapshot to peer={peerId} reason={reason} graph={(string.IsNullOrEmpty(request.GraphName) ? "?" : request.GraphName)} seed={(request.HasLevelSeed ? request.LevelSeed.ToString() : "?")} usedChunks={chunks}");
        }

        private void HandleHostSceneRequest(NetPeer peer, NetPacketReader reader)
        {
            if (!Plugin.Cfg.EnableHostSceneRequestProtocol.Value) return;

            if (_mode != NetMode.Client)
            {
                NetLogger.Warn("[SceneRequest] Ignoring HostSceneRequest because this instance is not a Client");
                return;
            }

            if (!NetSceneRequestCodec.TryReadHostRequest(reader, out var request))
            {
                NetLogger.Warn("[SceneRequest] Malformed HostSceneRequest packet");
                return;
            }

            _sceneRequests.RecordHostRequest(request);

            // Phase 5.4-E3 P2: diagnostic only — if a host scene request arrives while we are already loading /
            // following, record the context (current vs target, load-in-progress, auto-follow state). Does NOT change
            // any transition behavior (HostLevelTransitionDescriptor / JoinFlow untouched).
            try
            {
                if (Plugin.Cfg.LogBossTransitionDiagnostics.Value)
                {
                    bool loadInProgress = NetClientLoadGate.IsHostDrivenLoadInProgress;
                    string currentScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                    string target = request.TargetSceneKey();
                    if (loadInProgress || !string.Equals(currentScene, target, StringComparison.Ordinal))
                    {
                        NetLogger.Info($"[TransitionDiag] HostSceneRequest while busy: current={currentScene} target={target} loadInProgress={loadInProgress} autoLoadAllowed={request.AutoLoadAllowed} autoFollowStarted={NetClientLoadGate.AutoFollowStarted} autoFollowAlreadyMatching={NetClientLoadGate.AutoFollowSkippedAlreadyMatching} hostDrivenLoadsStarted={NetClientLoadGate.ClientLoadGateStartedHostDrivenLoad}");
                    }
                }
            }
            catch { }

            if (request.HasUsedSets) NetSceneFollowDiag.IncReceived();
            // Phase 5.3-L G: explicit received line with target/graph/seed/usedChunks for Host vs Client verification.
            NetLogger.Info($"[HostSceneRequest] received target={request.TargetSceneKey()} graph={(string.IsNullOrEmpty(request.GraphName) ? "?" : request.GraphName)} seed={(request.HasLevelSeed ? request.LevelSeed.ToString() : "?")} usedChunks={(request.HasUsedSets ? request.UsedChunksThisRun.Count : 0)}");
            // Phase 5.3-J/M: feed the load gate so a waiting Client can start the host-driven load, and
            // (5.3-M P1-F) auto-follow when the Host authorized AutoLoadAllowed for a combat target.
            NetClientLoadGate.OnHostGenerationInput(request);
            NetLogger.Info($"[SceneRequest] HostSceneRequest received: {request.ToCompactString()} usedSets={(request.HasUsedSets ? $"chunks={request.UsedChunksThisRun.Count},eventsRun={request.UsedEventsThisRun.Count},eventsEnv={request.UsedEventsThisEnvironment.Count}" : "none")} action={(request.AutoLoadAllowed ? "auto-load" : "no-auto-load")}");

            // Phase 5.4-A P1: a non-combat (request-only) host request while the client sits in a hub means the
            // host has not started combat yet — note hub readiness once instead of auto-loading anything.
            if (!request.AutoLoadAllowed) NetClientJoinFlow.NoteHubJoinReady();

            var response = _sceneRequests.BuildClientResponse(
                request,
                _runStates.LocalState,
                _runStates.LocalState.PeerId,
                Plugin.Cfg.PlayerName.Value);

            SendClientSceneResponse(peer, response, response.IsInTargetScene);
        }

        private void SendClientSceneResponse(NetPeer peer, NetClientSceneResponse response, bool ack)
        {
            if (!_sceneRequests.TryRecordSentClientResponse(response, out var duplicateReason))
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[SceneRequest] Client response skipped: {duplicateReason}");
                return;
            }

            var w = NetMessage.For(ack ? NetMessageType.ClientSceneAck : NetMessageType.ClientSceneRefused);
            NetSceneRequestCodec.WriteClientResponse(w, response);
            peer.Send(w, DeliveryMethod.ReliableOrdered);

            if (ack)
            {
                NetLogger.Info($"[SceneRequest] ClientSceneAck sent: {response.ToCompactString()}");
                if (response.IsInTargetScene)
                {
                    NetSceneFollowDiag.ClientLoadedAckSent++;
                    NetLogger.Info($"[LoadBarrier] client loaded (ack sent) graph={(string.IsNullOrEmpty(response.LevelGenerator) ? "?" : response.LevelGenerator)} seed={(response.HasLevelSeed ? response.LevelSeed.ToString() : "?")} level={response.LevelIndex}");
                    // Phase 5.4-A P0-C: the client confirmed it is in the host's target scene → session joined.
                    NetClientJoinFlow.MarkJoinedHost(
                        response.SceneKey(),
                        response.LevelGenerator,
                        response.HasLevelSeed ? response.LevelSeed.ToString() : "");
                }
            }
            else
                NetLogger.Info($"[SceneRequest] ClientSceneRefused sent: {response.ToCompactString()}");
        }

        private void TrySendClientSceneAckForLastHostRequest(string source)
        {
            if (_mode != NetMode.Client || _hostPeer == null) return;
            if (!Plugin.Cfg.EnableHostSceneRequestProtocol.Value) return;
            if (!_sceneRequests.HasLastHostRequestTarget(out var request)) return;

            var response = _sceneRequests.BuildClientResponse(
                request,
                _runStates.LocalState,
                _runStates.LocalState.PeerId,
                Plugin.Cfg.PlayerName.Value,
                "Client arrived in requested host scene after " + source + ".",
                "Arrived");

            if (!response.IsInTargetScene) return;

            SendClientSceneResponse(_hostPeer, response, ack: true);
        }

        private void HandleClientSceneResponse(NetPeer peer, NetPacketReader reader, bool accepted)
        {
            if (!Plugin.Cfg.EnableHostSceneRequestProtocol.Value) return;

            if (_mode != NetMode.Host)
            {
                NetLogger.Warn("[SceneRequest] Ignoring ClientScene response because this instance is not a Host");
                return;
            }

            if (!NetSceneRequestCodec.TryReadClientResponse(reader, out var response))
            {
                NetLogger.Warn("[SceneRequest] Malformed ClientScene response packet");
                return;
            }

            string peerId = response.ClientPeerId;
            if (_peerIds.TryGetValue(peer, out var mappedPeerId))
                peerId = mappedPeerId;

            _sceneRequests.RecordClientResponse(peerId, response);

            string kind = accepted ? "ClientSceneAck" : "ClientSceneRefused";
            NetLogger.Info($"[SceneRequest] {kind} from {peerId}: {response.ToCompactString()}");

            // Phase 5.3-J P1: lightweight load barrier — note when a client reports it reached the scene.
            if (accepted && response.IsInTargetScene)
            {
                NetSceneFollowDiag.HostClientLoadedAckReceived++;
                // Phase 5.3-M P1-G/H: feed the lightweight load barrier with the run the client confirmed.
                NetLoadBarrier.MarkLoaded(
                    peerId,
                    NetLoadBarrier.RunKeyFor(response.ChapterName, response.LevelIndex, response.HasLevelSeed, response.LevelSeed, response.LevelGenerator),
                    response.LevelGenerator);
            }
        }

        private NetPeer? FindPeerById(string peerId)
        {
            foreach (var kv in _peerIds)
            {
                if (kv.Value == peerId)
                    return kv.Key;
            }
            return null;
        }

        private string PeerIdOrAddress(NetPeer peer)
        {
            if (_peerIds.TryGetValue(peer, out var peerId))
                return peerId;
            return peer.Address.ToString();
        }


        // ---- Phase 3.0 remote player visual proxy ----

        private void HandleRemotePlayerVisualProxyTimer()
        {
            if (_mode == NetMode.Off || _net == null) return;
            if (!Plugin.Cfg.EnableRemotePlayerVisualProxy.Value) return;
            if (!_runStates.LocalState.HasLevel) return;

            float rate = Plugin.Cfg.RemotePlayerTransformSendRateHz.Value;
            if (rate < 1f) rate = 1f;
            if (rate > 30f) rate = 30f;
            float interval = 1f / rate;

            float now = Now();
            if (now - _lastPlayerVisualSendTime < interval) return;
            _lastPlayerVisualSendTime = now;

            if (!_localPlayer.TryBuildState(
                _runStates.LocalState.PeerId,
                Plugin.Cfg.PlayerName.Value,
                _runStates.LocalState,
                ++_playerVisualSequence,
                now,
                out var state))
            {
                return;
            }

            // Phase 5.0 P2: update interest management position hint for host-side rate reduction.
            if (_mode == NetMode.Host && Plugin.Cfg.EnableEnemyInterestManagement.Value)
                NetGameplayProbeManager.SetLocalPlayerPositionHint(state.Position);

            SendPlayerTransformToConnectedPeers(state);
        }

        private void SendPlayerTransformToConnectedPeers(NetPlayerTransformState state)
        {
            if (_net == null || _mode == NetMode.Off) return;

            if (_mode == NetMode.Host)
            {
                foreach (var peer in _clients.ToArray())
                    SendPlayerTransform(peer, state);
            }
            else if (_hostPeer != null)
            {
                SendPlayerTransform(_hostPeer, state);
            }
        }

        private void SendPlayerTransform(NetPeer peer, NetPlayerTransformState state)
        {
            try
            {
                var w = NetMessage.For(NetMessageType.PlayerTransformVisual);
                NetPlayerTransformCodec.Write(w, state);
                peer.Send(w, DeliveryMethod.Unreliable);
            }
            catch (Exception ex)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[RemotePlayer] Failed to send visual transform: {ex.Message}");
            }
        }

        private void HandlePlayerTransformVisual(NetPeer peer, NetPacketReader reader)
        {
            if (!Plugin.Cfg.EnableRemotePlayerVisualProxy.Value) return;

            if (!NetPlayerTransformCodec.TryRead(reader, out var state))
            {
                NetLogger.Warn("[RemotePlayer] Malformed PlayerTransformVisual packet");
                return;
            }

            if (_mode == NetMode.Host)
            {
                if (!_peerIds.TryGetValue(peer, out var peerId))
                {
                    if (Plugin.Cfg.EnableDebugLog.Value)
                        NetLogger.Debug($"[RemotePlayer] Ignoring visual transform from unregistered peer {peer.Address}");
                    return;
                }

                state.PeerId = peerId;
                if (_runStates.TryGetRemote(peerId, out var remoteState) && !string.IsNullOrWhiteSpace(remoteState.PlayerName))
                    state.PlayerName = remoteState.PlayerName;

                ApplyRemotePlayerVisual(state);
                RelayPlayerTransformToOtherClients(peer, state);
                return;
            }

            if (_mode == NetMode.Client)
            {
                if (state.PeerId == _runStates.LocalState.PeerId)
                    return;
                ApplyRemotePlayerVisual(state);
            }
        }

        private void ApplyRemotePlayerVisual(NetPlayerTransformState state)
        {
            if (string.IsNullOrWhiteSpace(state.PeerId)) return;
            if (state.PeerId == _runStates.LocalState.PeerId) return;
            NetPlayerLifeManager.ReportRemotePlayerTransform(state);
            _visualProxies.Apply(state, _runStates.LocalState, Now(), Plugin.Cfg.HideRemoteVisualWhenLevelSeedMismatch.Value);
        }

        private void RelayPlayerTransformToOtherClients(NetPeer sourcePeer, NetPlayerTransformState state)
        {
            if (_mode != NetMode.Host) return;
            foreach (var client in _clients.ToArray())
            {
                if (client == sourcePeer) continue;
                SendPlayerTransform(client, state);
            }
        }

        // ---- Phase 4 gameplay enemy event/state mirror experiments ----

        private void SendHostEnemyDeathEvent(NetPeer peer, NetGameplayDeathEvent deathEvent)
        {
            try
            {
                var w = NetMessage.For(NetMessageType.HostEnemyDeathEvent);
                NetGameplayDeathEventCodec.Write(w, deathEvent);
                peer.Send(w, DeliveryMethod.ReliableOrdered);

                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[EnemyDeathMirror] Sent to {PeerIdOrAddress(peer)}: {deathEvent.ToCompactString()}");
            }
            catch (Exception ex)
            {
                NetLogger.Warn($"[EnemyDeathMirror] Failed to send host enemy death event: {ex.Message}");
            }
        }

        private void HandleHostEnemyDeathEvent(NetPeer peer, NetPacketReader reader)
        {
            if (!NetGameplayDeathEventCodec.TryRead(reader, out var deathEvent))
            {
                NetLogger.Warn("[EnemyDeathMirror] Malformed HostEnemyDeathEvent packet");
                return;
            }

            if (_mode != NetMode.Client)
            {
                NetLogger.Warn("[EnemyDeathMirror] Ignoring HostEnemyDeathEvent because this instance is not a Client");
                return;
            }

            if (!Plugin.Cfg.EnableHostEnemyDeathEventMirror.Value)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug("[EnemyDeathMirror] Ignoring HostEnemyDeathEvent because EnableHostEnemyDeathEventMirror=false");
                return;
            }

            string eventKey = string.IsNullOrWhiteSpace(deathEvent.EventId)
                ? $"{deathEvent.SourcePeerId}:{deathEvent.SceneKey}:{deathEvent.SeedText}:{deathEvent.SpawnIndex}:{deathEvent.Sequence}"
                : deathEvent.EventId;
            if (!_receivedEnemyDeathEvents.Add(eventKey))
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[EnemyDeathMirror] Duplicate HostEnemyDeathEvent ignored: {eventKey}");
                return;
            }

            if (Plugin.Cfg.LogReceivedEnemyDeathEvents.Value)
                NetLogger.Info($"[EnemyDeathMirror] Received host death: {deathEvent.ToCompactString()}");

            if (NetGameplayProbeManager.TryFindLocalMatch(deathEvent, out var match, out var detail))
            {
                if (Plugin.Cfg.LogReceivedEnemyDeathEvents.Value)
                    NetLogger.Info($"[EnemyDeathMirror] Matched local entity: idx={match!.SpawnIndex} candidate={match.EntityId.CandidateKey} actor={match.ActorName} pos={match.PositionText} match={detail} apply={Plugin.Cfg.ApplyReceivedEnemyDeathEvents.Value}");

                if (Plugin.Cfg.ApplyReceivedEnemyDeathEvents.Value)
                {
                    if (NetGameplayProbeManager.TryApplyHostDeathToLocalMatch(deathEvent, match, out var applyDetail))
                        NetLogger.Info($"[EnemyDeathMirror] Applied host death to local entity: {applyDetail}");
                    else
                        NetLogger.Warn($"[EnemyDeathMirror] Failed to apply host death to local entity: {applyDetail}");
                }
            }
            else if (Plugin.Cfg.LogReceivedEnemyDeathEvents.Value)
            {
                NetLogger.Info($"[EnemyDeathMirror] No safe local match for host death: {detail}");
            }
        }

        private void SendClientEnemyDeathClaim(NetPeer peer, NetGameplayDeathEvent deathEvent)
        {
            try
            {
                var w = NetMessage.For(NetMessageType.ClientEnemyDeathClaim);
                NetGameplayDeathEventCodec.Write(w, deathEvent);
                peer.Send(w, DeliveryMethod.ReliableOrdered);

                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[ClientDeathClaim] Sent to Host: {deathEvent.ToCompactString()}");
            }
            catch (Exception ex)
            {
                NetLogger.Warn($"[ClientDeathClaim] Failed to send client enemy death claim: {ex.Message}");
            }
        }

        private void HandleClientEnemyDeathClaim(NetPeer peer, NetPacketReader reader)
        {
            if (!NetGameplayDeathEventCodec.TryRead(reader, out var deathEvent))
            {
                NetLogger.Warn("[ClientDeathClaim] Malformed ClientEnemyDeathClaim packet");
                return;
            }

            if (_mode != NetMode.Host)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Warn("[ClientDeathClaim] Ignoring ClientEnemyDeathClaim because this instance is not a Host");
                return;
            }

            if (!Plugin.Cfg.EnableClientEnemyDeathClaim.Value)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug("[ClientDeathClaim] Ignoring ClientEnemyDeathClaim because EnableClientEnemyDeathClaim=false");
                return;
            }

            if (!_peerIds.TryGetValue(peer, out var peerId))
            {
                NetLogger.Warn($"[ClientDeathClaim] Ignoring claim from unregistered peer {peer.Address}");
                return;
            }

            deathEvent.SourcePeerId = peerId;

            string eventKey = string.IsNullOrWhiteSpace(deathEvent.EventId)
                ? $"{peerId}:{deathEvent.SceneKey}:{deathEvent.SeedText}:{deathEvent.SpawnIndex}:{deathEvent.Sequence}"
                : $"{peerId}:{deathEvent.EventId}";
            if (!_receivedClientEnemyDeathClaims.Add(eventKey))
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[ClientDeathClaim] Duplicate ClientEnemyDeathClaim ignored: {eventKey}");
                return;
            }

            if (Plugin.Cfg.LogReceivedClientEnemyDeathClaims.Value)
                NetLogger.Info($"[ClientDeathClaim] Received from {peerId}: {deathEvent.ToCompactString()}");

            if (NetGameplayProbeManager.TryFindLocalMatch(deathEvent, out var match, out var detail))
            {
                if (Plugin.Cfg.LogReceivedClientEnemyDeathClaims.Value)
                    NetLogger.Info($"[ClientDeathClaim] Matched Host local entity: idx={match!.SpawnIndex} candidate={match.EntityId.CandidateKey} actor={match.ActorName} pos={match.PositionText} match={detail} apply={Plugin.Cfg.ApplyReceivedClientEnemyDeathClaimsOnHost.Value}");

                if (Plugin.Cfg.ApplyReceivedClientEnemyDeathClaimsOnHost.Value)
                {
                    if (NetGameplayProbeManager.TryApplyClientDeathClaimToHostMatch(deathEvent, match, out var applyDetail))
                        NetLogger.Info($"[ClientDeathClaim] Applied client death claim on Host: {applyDetail}");
                    else
                        NetLogger.Warn($"[ClientDeathClaim] Failed to apply client death claim on Host: {applyDetail}");
                }
            }
            else if (Plugin.Cfg.LogReceivedClientEnemyDeathClaims.Value)
            {
                NetLogger.Info($"[ClientDeathClaim] No safe Host local match for client death claim: {detail}");
            }
        }

        private void SendPlayerLifeState(NetPeer peer, NetPlayerLifeState state)
        {
            try
            {
                var w = NetMessage.For(NetMessageType.PlayerLifeState);
                NetPlayerLifeStateCodec.Write(w, state);
                peer.Send(w, DeliveryMethod.ReliableOrdered);

                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug($"[PlayerLife] Sent to {PeerIdOrAddress(peer)}: {state.ToCompactString()}");
            }
            catch (Exception ex)
            {
                NetLogger.Warn($"[PlayerLife] Failed to send player life state: {ex.Message}");
            }
        }

        private void SendPlayerLifeStateToClients(NetPlayerLifeState state, NetPeer? except)
        {
            if (_mode != NetMode.Host) return;
            foreach (var client in _clients.ToArray())
            {
                if (except != null && ReferenceEquals(client, except)) continue;
                SendPlayerLifeState(client, state);
            }
        }

        private void HandlePlayerLifeState(NetPeer peer, NetPacketReader reader)
        {
            if (!NetPlayerLifeStateCodec.TryRead(reader, out var state))
            {
                NetLogger.Warn("[PlayerLife] Malformed PlayerLifeState packet");
                return;
            }

            if (!Plugin.Cfg.EnableCoopPlayerDownedRevive.Value)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug("[PlayerLife] Ignoring PlayerLifeState because EnableCoopPlayerDownedRevive=false");
                return;
            }

            if (_mode == NetMode.Host)
            {
                if (!_peerIds.TryGetValue(peer, out var peerId))
                {
                    NetLogger.Warn($"[PlayerLife] Ignoring PlayerLifeState from unregistered peer {peer.Address}");
                    return;
                }

                if (state.Kind == NetPlayerLifeStateKind.HostDamageRequest)
                {
                    NetLogger.Warn($"[PlayerLife] Ignoring client-sent HostDamageRequest from {peerId}");
                    return;
                }

                state.SourcePeerId = peerId;
                if (_runStates.TryGetRemote(peerId, out var remoteState) && !string.IsNullOrWhiteSpace(remoteState.PlayerName))
                    state.PlayerName = remoteState.PlayerName;

                if (Plugin.Cfg.LogPlayerLifeSync.Value)
                    NetLogger.Info($"[PlayerLife] Received from {peerId}: {state.ToCompactString()}");

                NetPlayerLifeManager.HandleNetworkState(state, receivedOnHost: true);

                if (state.Kind != NetPlayerLifeStateKind.ReviveRequest)
                    SendPlayerLifeStateToClients(state, except: peer);
                return;
            }

            if (_mode == NetMode.Client)
            {
                if (Plugin.Cfg.LogPlayerLifeSync.Value)
                    NetLogger.Info($"[PlayerLife] Received from Host: {state.ToCompactString()}");
                NetPlayerLifeManager.HandleNetworkState(state, receivedOnHost: false);
            }
        }

        private int GetEnemyStateSnapshotMaxPerPacket()
        {
            int configured = Plugin.Cfg.EnemyStateSnapshotMaxEnemiesPerPacket.Value;
            if (configured < 1) configured = 1;

            const int safeMaxPerPacket = 32;
            if (configured > safeMaxPerPacket)
            {
                if (!_enemyStateSnapshotPacketClampWarningShown)
                {
                    _enemyStateSnapshotPacketClampWarningShown = true;
                    NetLogger.Warn($"[EnemyStateMirror] EnemyStateSnapshotMaxEnemiesPerPacket={configured} is above the current safe packet split limit; using {safeMaxPerPacket} to stay below LiteNetLib packet size limits.");
                }
                return safeMaxPerPacket;
            }

            return configured;
        }

        private void SendHostEnemyStateSnapshots(NetPeer peer, List<NetGameplayEnemyStateSnapshot> snapshots, int maxPerPacket)
        {
            if (snapshots == null || snapshots.Count == 0) return;
            if (maxPerPacket < 1) maxPerPacket = 1;

            for (int offset = 0; offset < snapshots.Count; offset += maxPerPacket)
            {
                int count = Math.Min(maxPerPacket, snapshots.Count - offset);
                SendHostEnemyStateSnapshotChunk(peer, snapshots, offset, count, snapshots.Count);
            }
        }

        // O3: safe UDP payload limit for LiteNetLib unreliable packets.
        // LiteNetLib MTU is typically 1024 bytes; subtract ~50 for LiteNetLib + IP/UDP headers.
        private const int EnemySnapshotSafeByteLimit = 900;
        private int _snapshotChunkBytesMax;
        private int _snapshotChunkSplit;
        private int _snapshotChunkTooLargeRejected;
        private int _snapshotChunkSendFailed;

        private void SendHostEnemyStateSnapshotChunk(NetPeer peer, List<NetGameplayEnemyStateSnapshot> snapshots, int offset, int count, int total)
        {
            if (count <= 0) return;
            try
            {
                var w = NetMessage.For(NetMessageType.HostEnemyStateSnapshot);
                NetGameplayEnemyStateSnapshotCodec.WriteBatch(w, snapshots, offset, count);
                int byteLen = w.Length;
                if (byteLen > _snapshotChunkBytesMax) _snapshotChunkBytesMax = byteLen;

                if (byteLen > EnemySnapshotSafeByteLimit && count > 1)
                {
                    // Packet too large: split into two halves and retry each recursively.
                    _snapshotChunkSplit++;
                    int half = count / 2;
                    SendHostEnemyStateSnapshotChunk(peer, snapshots, offset,        half,         total);
                    SendHostEnemyStateSnapshotChunk(peer, snapshots, offset + half, count - half, total);
                    return;
                }

                if (byteLen > EnemySnapshotSafeByteLimit)
                {
                    // Single snapshot that still exceeds the limit — log and skip.
                    _snapshotChunkTooLargeRejected++;
                    float now = Now();
                    if (now - _lastEnemyStateSnapshotSendErrorLogTime > 5f)
                    {
                        _lastEnemyStateSnapshotSendErrorLogTime = now;
                        NetLogger.Warn($"[EnemyStateMirror] Single-snapshot chunk too large bytes={byteLen} limit={EnemySnapshotSafeByteLimit} offset={offset}");
                    }
                    return;
                }

                peer.Send(w, DeliveryMethod.Unreliable);
            }
            catch (Exception ex)
            {
                _snapshotChunkSendFailed++;
                float now = Now();
                if (now - _lastEnemyStateSnapshotSendErrorLogTime > 2f)
                {
                    _lastEnemyStateSnapshotSendErrorLogTime = now;
                    NetLogger.Warn($"[EnemyStateMirror] Failed to send HostEnemyStateSnapshot chunk offset={offset} count={count} total={total}: {ex.Message}");
                }
            }
        }

        private void HandleHostEnemyStateSnapshot(NetPeer peer, NetPacketReader reader)
        {
            if (!NetGameplayEnemyStateSnapshotCodec.TryReadBatch(reader, out var snapshots))
            {
                NetLogger.Warn("[EnemyStateMirror] Malformed HostEnemyStateSnapshot packet");
                return;
            }

            if (_mode != NetMode.Client)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Warn("[EnemyStateMirror] Ignoring HostEnemyStateSnapshot because this instance is not a Client");
                return;
            }

            if (!Plugin.Cfg.EnableHostEnemyStateSnapshotMirror.Value)
            {
                if (Plugin.Cfg.EnableDebugLog.Value)
                    NetLogger.Debug("[EnemyStateMirror] Ignoring HostEnemyStateSnapshot because EnableHostEnemyStateSnapshotMirror=false");
                return;
            }

            NetGameplayProbeManager.ProcessHostEnemyStateSnapshots(snapshots);
        }

        private void HandlePing(NetPeer peer)
        {
            TouchPeer(peer);
            if (Plugin.Cfg.EnableDebugLog.Value)
                NetLogger.Debug("[Net] Ping → Pong");
            peer.Send(NetMessage.For(NetMessageType.Pong), DeliveryMethod.Unreliable);
        }

        private void RejectPeer(NetPeer peer, string reason)
        {
            NetLogger.Info($"[Net] Rejecting peer {peer.Address}: {reason}");
            var w = NetMessage.For(NetMessageType.HandshakeRejected);
            w.Put(reason);
            peer.Send(w, DeliveryMethod.ReliableOrdered);
            peer.Disconnect();
            _clients.Remove(peer);
            if (_peerIds.TryGetValue(peer, out var peerId))
            {
                _sessions.Remove(peerId);
                _runStates.RemoveRemote(peerId);
                _sceneRequests.RemovePeer(peerId);
                _visualProxies.Remove(peerId);
                Gameplay.PlayerHeldWeaponManager.RemovePeer(peerId);
                NetLoadBarrier.RemovePeer(peerId);
                _peerIds.Remove(peer);
            }
        }

        private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            NetLogger.Info($"[Net] Peer disconnected: {peer.Address} reason={info.Reason}");
            _clients.Remove(peer);

            if (_peerIds.TryGetValue(peer, out var peerId))
            {
                if (_mode == NetMode.Host)
                    _sessions.Remove(peerId);
                else
                    _sessions.MarkDisconnected(peerId);
                _runStates.RemoveRemote(peerId);
                _sceneRequests.RemovePeer(peerId);
                _visualProxies.Remove(peerId);
                Gameplay.PlayerHeldWeaponManager.RemovePeer(peerId);
                NetLoadBarrier.RemovePeer(peerId);
                _peerIds.Remove(peer);
                NetLogger.Info($"[Session] Peer left: id={peerId}");
            }

            if (_mode == NetMode.Client)
            {
                if (peer == _hostPeer)
                {
                    _hostPeer = null;
                    NetClientJoinFlow.LeaveSession("disconnect");
                    NetLogger.Info("[Session] Host connection lost");
                }

                _clientConnectInProgress = false;
                _nextClientReconnectTime = Now() + 5f;

                if (info.Reason == DisconnectReason.ConnectionFailed)
                    NetLogger.Info("[Net] Client will retry connection in 5 seconds");
                else
                    NetLogger.Info("[Net] Client reconnect scheduled in 5 seconds");
            }
        }

        private void TouchPeer(NetPeer peer)
        {
            if (_peerIds.TryGetValue(peer, out var peerId))
                _sessions.Touch(peerId, Now());
        }

        private static float Now() => UnityEngine.Time.realtimeSinceStartup;
    }
}
