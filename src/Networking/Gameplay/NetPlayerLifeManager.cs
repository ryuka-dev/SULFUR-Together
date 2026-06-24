using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase 4.3.0-A local-player death interception prototype.
    /// Player inventory/difficulty/death penalty remain local: original Unit.Die is only delayed while
    /// at least one known co-op peer is still alive and can revive this peer.
    /// </summary>
    internal static class NetPlayerLifeManager
    {
        internal sealed class PeerCombatPosition
        {
            public string PeerId = "";
            public string PlayerName = "";
            public Vector3 Position;
            public string SceneKey = "<unknown>";
        }

        private sealed class PeerLife
        {
            public string PeerId = "";
            public string PlayerName = "";
            public NetPlayerLifeStateKind Kind = NetPlayerLifeStateKind.Alive;
            public bool HasPosition;
            public Vector3 Position;
            public string SceneKey = "<unknown>";
            public bool HasLevelSeed;
            public int LevelSeed;
            public int Sequence;
            public float LastUpdatedAt;

            public bool IsDownOrDead => Kind == NetPlayerLifeStateKind.Downed || Kind == NetPlayerLifeStateKind.NativeDeathCommit;
        }

        private const int StatusCurrentHealth = 92;
        private const int AttributeMaxHealth = 60;

        private static readonly Dictionary<string, PeerLife> PeerStates = new Dictionary<string, PeerLife>();
        private static readonly Dictionary<string, float> LastLogByKey = new Dictionary<string, float>();

        private static object? _localPlayerUnit;
        private static Transform? _localTransform;
        private static bool _localDowned;
        private static bool _localNativeDeathCommitted;
        // Phase 5.6-DL: set when the local player commits native death (run over -> the game respawns it to the
        // hub via GoToLevel(mode=Death)). It SURVIVES the respawn's ClearLevelScoped and is cleared only once the
        // player has settled back into a hub/safezone scene (or a safety timeout). While active, a dead/respawning
        // player must NEVER be auto-followed into a combat level — that double-load is what produced the
        // "loads the first level then the hub" + doubled-inventory bug when both players die together.
        private static bool _localDeathRespawnActive;
        private static float _localDeathRespawnStartedAt;
        private static bool _healthDiagDumped; // P3-A3: one-time dump of the player Stats health API
        private static bool _localControlLocksApplied;
        // Combat-related GameManager.PlayerLocks blocked while downed (blacklist). Camera/Interaction/UseHUD and
        // the pause/menu/quit path are intentionally left usable. See ApplyLocalControlLocks.
        private static readonly string[] DownedInputBlacklist = { "PlayerMovement", "Weapon", "Inventory" };
        private static float _localDownedAt;
        private static int _nativeDeathBypassDepth;
        public static int HostDamageApplyDepth;
        private static int _sequence;
        private static string _activeReviveTarget = "";
        private static float _activeReviveHoldStartedAt;
        private static bool _activeReviveRequestSent;

        public static void ClearLevelScoped(string source)
        {
            if (_localControlLocksApplied)
                ApplyLocalControlLocks(false);

            _localDowned = false;
            _localNativeDeathCommitted = false;
            _localDownedAt = 0f;
            _activeReviveTarget = "";
            _activeReviveHoldStartedAt = 0f;
            _activeReviveRequestSent = false;
            PeerStates.Clear();

            if (Plugin.Cfg.EnableCoopPlayerDownedRevive.Value && Plugin.Cfg.LogPlayerLifeSync.Value)
                NetLogger.Info($"[PlayerLife] Cleared level-scoped player life state source={source}");
        }

        public static void Tick()
        {
            if (!IsEnabled()) return;

            RefreshLocalPositionCache();
            HandleDownedTimeout();
            HandleReviveHoldInput();
            UpdateDeathRespawnGuard();
            FlushPendingEnemyToClientDamage();
        }

        public static void ReportLocalPlayerObject(object? player)
        {
            if (player == null) return;

            object? unit = ResolvePlayerUnit(player);
            if (unit != null)
            {
                _localPlayerUnit = unit;
                _localTransform = ExtractTransform(unit) ?? ExtractTransform(player);
                if (Plugin.Cfg.EnableCoopPlayerDownedRevive.Value && Plugin.Cfg.LogPlayerLifeSync.Value)
                    NetLogger.Info($"[PlayerLife] Local player unit captured from {player.GetType().Name}: {DescribeObject(unit)}");
                return;
            }

            var transform = ExtractTransform(player);
            if (transform != null)
                _localTransform = transform;
        }

        public static bool TryBlockLocalPlayerDeath(object? unit, string source, out string detail)
        {
            detail = "";
            if (!IsEnabled()) return false;
            if (unit == null) return false;
            if (_nativeDeathBypassDepth > 0) return false;
            if (!LooksLikePlayerUnit(unit)) return false;

            _localPlayerUnit = unit;
            _localTransform = ExtractTransform(unit) ?? _localTransform;

            if (_localNativeDeathCommitted)
            {
                detail = "native death already committed";
                return false;
            }

            if (_localDowned)
            {
                StabilizeDownedLocalPlayer("repeat Unit.Die while already downed");
                detail = "already downed; suppressed repeated Unit.Die";
                return true;
            }

            if (ShouldAllowImmediateNativeDeath(out var reason))
            {
                _localNativeDeathCommitted = true;
                MarkLocalDeathRespawnStarted("immediate native death/" + reason);
                if (NetConfig.GetMode() == NetMode.Host && reason.StartsWith("all other", StringComparison.OrdinalIgnoreCase))
                    NetGameplaySyncBridge.ReportHostPlayerLifeStateToAll(BuildHostState(NetPlayerLifeStateKind.NativeDeathCommit, "*", source + "/" + reason));
                else
                    PublishLocalLifeState(NetPlayerLifeStateKind.NativeDeathCommit, "", source + "/" + reason);
                detail = reason;
                return false;
            }

            EnterLocalDownedState(unit, source);
            detail = "entered co-op downed state";
            return true;
        }

        public static void ReportRemotePlayerTransform(NetPlayerTransformState state)
        {
            if (!IsEnabled()) return;
            if (string.IsNullOrWhiteSpace(state.PeerId)) return;
            if (state.PeerId == GetLocalPeerId()) return;

            var peer = GetOrCreatePeer(state.PeerId);
            peer.PlayerName = state.PlayerName;
            peer.HasPosition = true;
            peer.Position = state.Position;
            peer.SceneKey = state.SceneCompareKey();
            peer.HasLevelSeed = state.HasLevelSeed;
            peer.LevelSeed = state.LevelSeed;
            peer.LastUpdatedAt = Time.realtimeSinceStartup;
        }

        public static void HandleNetworkState(NetPlayerLifeState state, bool receivedOnHost)
        {
            if (!IsEnabled()) return;
            if (state == null) return;

            string localPeerId = GetLocalPeerId();
            bool targetsLocal = state.TargetPeerId == localPeerId || (state.TargetPeerId == "*" && state.Kind == NetPlayerLifeStateKind.NativeDeathCommit);

            if (state.Kind == NetPlayerLifeStateKind.ReviveRequest)
            {
                if (receivedOnHost)
                    HandleReviveRequestOnHost(state);
                return;
            }

            if (state.Kind == NetPlayerLifeStateKind.ReviveAccepted)
            {
                if (targetsLocal)
                    ReviveLocalPlayer("revive accepted by host");

                if (!string.IsNullOrWhiteSpace(state.TargetPeerId))
                    MarkPeerAlive(state.TargetPeerId, state);
                return;
            }

            if (state.Kind == NetPlayerLifeStateKind.NativeDeathCommit)
            {
                bool sourceIsLocal = !string.IsNullOrWhiteSpace(state.SourcePeerId) && state.SourcePeerId == localPeerId;
                if (targetsLocal || (string.IsNullOrWhiteSpace(state.TargetPeerId) && sourceIsLocal))
                    CommitLocalNativeDeath("network native death commit");

                string key = string.IsNullOrWhiteSpace(state.TargetPeerId) || state.TargetPeerId == "*"
                    ? state.SourcePeerId
                    : state.TargetPeerId;
                if (!string.IsNullOrWhiteSpace(key))
                    MarkPeerState(key, state, NetPlayerLifeStateKind.NativeDeathCommit);

                if (receivedOnHost)
                    CheckAllDownedOnHost("native death commit update");
                return;
            }

            if (state.Kind == NetPlayerLifeStateKind.HostDamageRequest)
            {
                if (targetsLocal)
                    ApplyHostDamageToLocalPlayer(state);
                return;
            }

            string peerId = string.IsNullOrWhiteSpace(state.SourcePeerId) ? state.TargetPeerId : state.SourcePeerId;
            if (!string.IsNullOrWhiteSpace(peerId))
                MarkPeerState(peerId, state, state.Kind);

            if (receivedOnHost && state.Kind == NetPlayerLifeStateKind.Downed)
                CheckAllDownedOnHost("remote downed update");
        }

        public static IReadOnlyCollection<string> GetKnownDownedPeerIds()
        {
            return PeerStates.Values.Where(p => p.Kind == NetPlayerLifeStateKind.Downed).Select(p => p.PeerId).ToList().AsReadOnly();
        }

        /// <summary>True if the given remote peer is currently downed or dead (A3.2: host stops aggroing its target proxy).</summary>
        public static bool IsPeerDownOrDead(string peerId)
        {
            if (string.IsNullOrWhiteSpace(peerId)) return false;
            return PeerStates.TryGetValue(peerId, out var peer) && peer.IsDownOrDead;
        }

        public static IReadOnlyList<PeerCombatPosition> GetHostKnownAliveRemotePeerPositions()
        {
            var result = new List<PeerCombatPosition>();
            if (NetConfig.GetMode() != NetMode.Host) return result.AsReadOnly();
            if (!NetRunStateBridge.TryGetLocalRunState(out var local) || !local.HasLevel) return result.AsReadOnly();

            string localPeerId = GetLocalPeerId();
            foreach (var peer in PeerStates.Values)
            {
                if (peer == null) continue;
                if (peer.PeerId == localPeerId) continue;
                if (peer.IsDownOrDead) continue;
                if (!peer.HasPosition) continue;
                if (string.IsNullOrWhiteSpace(peer.SceneKey) || peer.SceneKey != local.SceneKey()) continue;
                if (Plugin.Cfg.RequireSameLevelSeedForSceneMatch.Value && peer.HasLevelSeed && local.HasLevelSeed && peer.LevelSeed != local.LevelSeed) continue;
                result.Add(new PeerCombatPosition
                {
                    PeerId = peer.PeerId,
                    PlayerName = peer.PlayerName,
                    Position = peer.Position,
                    SceneKey = peer.SceneKey
                });
            }

            return result.AsReadOnly();
        }

        // (1) 5.7-B8: enemy→client damage is ACCUMULATED per (peer,damageType) and flushed once per window. A machine-gun
        // enemy hitting the client sent one NetPlayerLifeState per pellet (LogOutput102: 841). Total damage is preserved
        // (accumulated, never dropped) — only the feedback batches. Keyed per damageType so status effects aren't merged.
        private static readonly Dictionary<string, float>   _enemyDmgPending     = new Dictionary<string, float>();
        private static readonly Dictionary<string, float>   _enemyDmgLastSentAt  = new Dictionary<string, float>();
        private static readonly Dictionary<string, Vector3> _enemyDmgLastPos     = new Dictionary<string, Vector3>();
        private static readonly Dictionary<string, string>  _enemyDmgLastReason  = new Dictionary<string, string>();
        private static readonly List<string> _enemyDmgFlushScratch = new List<string>();

        public static void ReportHostAuthoritativeEnemyDamage(string targetPeerId, float damage, string reason, Vector3 hitPosition, int damageTypeInt = 0)
        {
            if (NetConfig.GetMode() != NetMode.Host) return;
            if (string.IsNullOrWhiteSpace(targetPeerId)) return;
            if (targetPeerId == GetLocalPeerId()) return;
            if (damage <= 0f) return;

            float window = 0f;
            try { if (Plugin.Cfg.EnableCombatEventCoalescing.Value) window = Plugin.Cfg.EnemyToClientDamageCoalesceSeconds.Value; } catch { }
            if (window <= 0f) { SendEnemyDamageToClient(targetPeerId, damage, reason, hitPosition, damageTypeInt); return; }

            string key = targetPeerId + "|" + damageTypeInt;
            float now = Time.realtimeSinceStartup;
            _enemyDmgLastPos[key] = hitPosition;
            _enemyDmgLastReason[key] = reason ?? "enemy damage";
            _enemyDmgLastSentAt.TryGetValue(key, out float lastAt);
            if (now - lastAt < window)
            {
                _enemyDmgPending.TryGetValue(key, out float p);
                _enemyDmgPending[key] = p + damage;           // accumulate; flushed by Tick
                return;
            }
            _enemyDmgLastSentAt[key] = now;
            _enemyDmgPending.TryGetValue(key, out float pend);
            if (pend > 0f) _enemyDmgPending.Remove(key);
            SendEnemyDamageToClient(targetPeerId, damage + pend, reason, hitPosition, damageTypeInt);
        }

        /// <summary>Host Tick: flush accumulated enemy→client damage whose window has elapsed (the tail of a burst).</summary>
        public static void FlushPendingEnemyToClientDamage()
        {
            if (NetConfig.GetMode() != NetMode.Host || _enemyDmgPending.Count == 0) return;
            float window; try { window = Plugin.Cfg.EnemyToClientDamageCoalesceSeconds.Value; } catch { window = 0.1f; }
            if (window <= 0f) { _enemyDmgPending.Clear(); return; }
            float now = Time.realtimeSinceStartup;
            _enemyDmgFlushScratch.Clear();
            foreach (var kv in _enemyDmgPending)
            {
                _enemyDmgLastSentAt.TryGetValue(kv.Key, out float lastAt);
                if (now - lastAt >= window) _enemyDmgFlushScratch.Add(kv.Key);
            }
            foreach (var key in _enemyDmgFlushScratch)
            {
                float dmg = _enemyDmgPending[key]; _enemyDmgPending.Remove(key);
                if (dmg <= 0f) continue;
                int sep = key.LastIndexOf('|');
                string peer = sep > 0 ? key.Substring(0, sep) : key;
                int type = 0; if (sep > 0) int.TryParse(key.Substring(sep + 1), out type);
                Vector3 pos = _enemyDmgLastPos.TryGetValue(key, out var pp) ? pp : Vector3.zero;
                string reason = _enemyDmgLastReason.TryGetValue(key, out var rr) ? rr : "enemy damage (coalesced)";
                _enemyDmgLastSentAt[key] = now;
                SendEnemyDamageToClient(peer, dmg, reason, pos, type);
            }
        }

        private static void SendEnemyDamageToClient(string targetPeerId, float damage, string reason, Vector3 hitPosition, int damageTypeInt)
        {
            NetRunStateBridge.TryGetLocalRunState(out var runState);
            int seq = ++_sequence;
            var state = new NetPlayerLifeState
            {
                EventId = $"host:enemyDamage:{seq}:{targetPeerId}",
                SourcePeerId = "host",
                TargetPeerId = targetPeerId,
                PlayerName = Plugin.Cfg.PlayerName.Value,
                ChapterName = runState.ChapterName,
                LevelIndex = runState.LevelIndex,
                HasLevelSeed = runState.HasLevelSeed,
                LevelSeed = runState.LevelSeed,
                Sequence = seq,
                Kind = NetPlayerLifeStateKind.HostDamageRequest,
                HasPosition = true,
                Position = hitPosition,
                SentAt = Time.realtimeSinceStartup,
                Reason = reason ?? "enemy ranged damage",
                DamageAmount = damage,
                DamageType = damageTypeInt
            };

            NetGameplaySyncBridge.ReportHostPlayerLifeStateToAll(state);
            if (Plugin.Cfg.LogEnemyHostDamageAuthority.Value)
                NetLogger.Info($"[EnemyDamageAuthority] Host sent damage target={targetPeerId} amount={damage:F1} pos=({hitPosition.x:F2},{hitPosition.y:F2},{hitPosition.z:F2}) reason={reason}");
        }

        public static bool ShouldSuppressLocalPlayerControls()
        {
            return IsEnabled() && _localDowned && !_localNativeDeathCommitted;
        }

        /// <summary>Phase 5.6-DL: true while the local player is dead and the game is respawning it back to the hub.
        /// Both ends consult this so a dead/respawning player is never advertised (host) or auto-followed (client)
        /// into a combat level. Cleared once the player settles into a hub scene (see UpdateDeathRespawnGuard).</summary>
        public static bool IsLocalDeathRespawnInProgress => _localDeathRespawnActive;

        private static void MarkLocalDeathRespawnStarted(string reason)
        {
            if (_localDeathRespawnActive) return;
            _localDeathRespawnActive = true;
            _localDeathRespawnStartedAt = Time.realtimeSinceStartup;
            // Stamp the load-gate's death epoch so the death-respawn gate follows only the host destination
            // broadcast AFTER this moment (never the stale pre-death combat request).
            NetClientLoadGate.NoteLocalDeathRespawnArmed();
            if (Plugin.Cfg.LogPlayerLifeSync.Value)
                NetLogger.Info($"[PlayerLife] Death-respawn guard armed (suppress combat follow until hub) reason={reason}");
        }

        // Cleared once the player has respawned back into a hub/safezone scene and is alive again, or after a
        // safety timeout so the guard can never stick forever. Runs every Tick.
        private static void UpdateDeathRespawnGuard()
        {
            if (!_localDeathRespawnActive) return;

            const float maxHoldSeconds = 30f;
            if (Time.realtimeSinceStartup - _localDeathRespawnStartedAt > maxHoldSeconds)
            {
                _localDeathRespawnActive = false;
                if (Plugin.Cfg.LogPlayerLifeSync.Value)
                    NetLogger.Info("[PlayerLife] Death-respawn guard cleared (timeout)");
                return;
            }

            if (_localDowned || _localNativeDeathCommitted) return; // still dead/downed — respawn not finished
            if (!NetRunStateBridge.TryGetLocalRunState(out var run) || !run.HasLevel) return; // mid-load

            bool inHub = NetSceneClassify.IsHubOrSafeZoneChapter(run.ChapterName)
                || NetSceneClassify.IsHubOrSafeZoneGraph(run.LevelGenerator);
            if (!inHub) return;

            _localDeathRespawnActive = false;
            if (Plugin.Cfg.LogPlayerLifeSync.Value)
                NetLogger.Info($"[PlayerLife] Death-respawn guard cleared; settled in hub chapter={run.ChapterName}");
        }

        public static bool ShouldBlockLocalPlayerDamage(object? unit)
        {
            if (!ShouldSuppressLocalPlayerControls()) return false;
            if (unit == null) return false;
            if (!LooksLikePlayerUnit(unit)) return false;

            StabilizeDownedLocalPlayer("blocked damage while downed");
            return true;
        }

        public static bool IsLocalPlayerUnit(object? unit)
        {
            if (unit == null) return false;
            return LooksLikePlayerUnit(unit);
        }

        /// <summary>A3.2: true if <paramref name="unit"/> is THIS machine's local player AND it is currently downed
        /// (not native-dead). Used by the AiAgent.GetTarget patch to make enemies drop a downed local player.</summary>
        public static bool IsDownedLocalPlayerUnit(object? unit)
        {
            if (unit == null || !_localDowned || _localNativeDeathCommitted) return false;
            object? local = ResolveCurrentLocalPlayerUnit();
            return local != null && ReferenceEquals(unit, local);
        }


        public static void DrawOnGUI()
        {
            if (!IsEnabled()) return;

            try
            {
                if (_localDowned && !_localNativeDeathCommitted)
                {
                    DrawCenterPrompt("DOWNED\nWaiting for a teammate to revive you");
                    return;
                }

                if (_localNativeDeathCommitted) return;
                if (_localTransform == null) return;

                if (TryFindNearestDownedPeer(out var target, out var distance))
                {
                    float required = Math.Max(0.1f, Plugin.Cfg.PlayerReviveHoldSeconds.Value);
                    float progress = 0f;
                    if (_activeReviveTarget == target.PeerId && _activeReviveHoldStartedAt > 0f)
                        progress = Mathf.Clamp01((Time.realtimeSinceStartup - _activeReviveHoldStartedAt) / required);

                    string name = string.IsNullOrWhiteSpace(target.PlayerName) ? target.PeerId : target.PlayerName;
                    string key = Plugin.Cfg.PlayerReviveHoldKey.Value.MainKey.ToString();
                    DrawCenterPrompt($"Hold [{key}] to revive {name}\n{distance:F1}m  {progress * 100f:F0}%");
                }
            }
            catch { }
        }

        private static void DrawCenterPrompt(string text)
        {
            float width = 460f;
            float height = 72f;
            var rect = new Rect((Screen.width - width) * 0.5f, Screen.height * 0.72f, width, height);
            GUI.Box(rect, text);
        }

        private static void EnterLocalDownedState(object unit, string source)
        {
            _localDowned = true;
            _localNativeDeathCommitted = false;
            _localDownedAt = Time.realtimeSinceStartup;
            _activeReviveTarget = "";
            _activeReviveHoldStartedAt = 0f;
            _activeReviveRequestSent = false;

            StabilizeDownedLocalPlayer("enter downed");
            PublishLocalLifeState(NetPlayerLifeStateKind.Downed, "", source);
            MarkLocalState(NetPlayerLifeStateKind.Downed, source);

            NetLogger.Info($"[PlayerLife] Local player death intercepted; entered downed state wait={(Plugin.Cfg.PlayerDownedRescueTimeoutSeconds.Value <= 0f ? "infinite" : Plugin.Cfg.PlayerDownedRescueTimeoutSeconds.Value.ToString("F1") + "s")} source={source}");

            if (NetConfig.GetMode() == NetMode.Host)
                CheckAllDownedOnHost("local downed");
        }

        private static void HandleDownedTimeout()
        {
            if (!_localDowned || _localNativeDeathCommitted) return;

            float timeout = Plugin.Cfg.PlayerDownedRescueTimeoutSeconds.Value;
            if (timeout <= 0f) return;

            float elapsed = Time.realtimeSinceStartup - _localDownedAt;
            if (elapsed < timeout) return;

            PublishLocalLifeState(NetPlayerLifeStateKind.NativeDeathCommit, GetLocalPeerId(), "downed timeout");
            CommitLocalNativeDeath($"downed timeout elapsed={elapsed:F1}s");
        }

        private static void HandleReviveHoldInput()
        {
            if (_localDowned || _localNativeDeathCommitted) return;
            if (_localTransform == null) return;
            if (!Plugin.Cfg.PlayerReviveHoldKey.Value.IsPressed())
            {
                ResetReviveHold();
                return;
            }

            if (!TryFindNearestDownedPeer(out var target, out var distance))
            {
                ResetReviveHold();
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (_activeReviveTarget != target.PeerId)
            {
                _activeReviveTarget = target.PeerId;
                _activeReviveHoldStartedAt = now;
                _activeReviveRequestSent = false;
                if (Plugin.Cfg.LogPlayerLifeSync.Value)
                    NetLogger.Info($"[PlayerLife] Started revive hold target={target.PeerId} distance={distance:F2}m hold={Plugin.Cfg.PlayerReviveHoldSeconds.Value:F1}s");
            }

            if (_activeReviveRequestSent) return;

            float required = Plugin.Cfg.PlayerReviveHoldSeconds.Value;
            if (required < 0.1f) required = 0.1f;
            if (now - _activeReviveHoldStartedAt < required) return;

            _activeReviveRequestSent = true;
            SendReviveRequest(target.PeerId, distance);
        }

        private static void SendReviveRequest(string targetPeerId, float distance)
        {
            var request = BuildLocalState(NetPlayerLifeStateKind.ReviveRequest, targetPeerId, $"hold revive distance={distance:F2}m");
            NetGameplaySyncBridge.ReportPlayerLifeState(request);
            NetLogger.Info($"[PlayerLife] Revive request sent target={targetPeerId} distance={distance:F2}m");

            if (NetConfig.GetMode() == NetMode.Host)
                HandleReviveRequestOnHost(request);
        }

        private static void HandleReviveRequestOnHost(NetPlayerLifeState request)
        {
            string sourcePeerId = request.SourcePeerId;
            string targetPeerId = request.TargetPeerId;
            if (string.IsNullOrWhiteSpace(sourcePeerId) || string.IsNullOrWhiteSpace(targetPeerId))
            {
                NetLogger.Warn($"[PlayerLife] Reject revive request: malformed {request.ToCompactString()}");
                return;
            }

            if (sourcePeerId == targetPeerId)
            {
                NetLogger.Warn($"[PlayerLife] Reject revive request: source equals target {sourcePeerId}");
                return;
            }

            if (!TryGetPeerLife(targetPeerId, out var target) || target.Kind != NetPlayerLifeStateKind.Downed)
            {
                NetLogger.Warn($"[PlayerLife] Reject revive request: target not downed target={targetPeerId}");
                return;
            }

            if (TryGetPeerLife(sourcePeerId, out var source) && source.IsDownOrDead)
            {
                NetLogger.Warn($"[PlayerLife] Reject revive request: source is not alive source={sourcePeerId} state={source.Kind}");
                return;
            }

            if (Plugin.Cfg.RequireReviveDistanceValidationOnHost.Value)
            {
                if (!TryGetPeerPosition(sourcePeerId, out var sourcePos) || !TryGetPeerPosition(targetPeerId, out var targetPos))
                {
                    NetLogger.Warn($"[PlayerLife] Reject revive request: missing position source={sourcePeerId} target={targetPeerId}");
                    return;
                }

                float distance = Vector3.Distance(sourcePos, targetPos);
                float radius = Math.Max(0.5f, Plugin.Cfg.PlayerReviveDistance.Value + 1.0f);
                if (distance > radius)
                {
                    NetLogger.Warn($"[PlayerLife] Reject revive request: too far source={sourcePeerId} target={targetPeerId} distance={distance:F2}m limit={radius:F2}m");
                    return;
                }
            }

            var accepted = BuildHostState(NetPlayerLifeStateKind.ReviveAccepted, targetPeerId, $"revived by {sourcePeerId}");
            MarkPeerAlive(targetPeerId, accepted);
            NetGameplaySyncBridge.ReportHostPlayerLifeStateToAll(accepted);

            if (targetPeerId == GetLocalPeerId())
                ReviveLocalPlayer($"revived by {sourcePeerId}");

            NetLogger.Info($"[PlayerLife] Revive accepted source={sourcePeerId} target={targetPeerId}");
        }

        private static void CheckAllDownedOnHost(string reason)
        {
            if (NetConfig.GetMode() != NetMode.Host) return;

            var knownPeers = NetGameplaySyncBridge.GetKnownPlayerLifePeerIds().Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();
            if (knownPeers.Count <= 1) return;

            foreach (string peerId in knownPeers)
            {
                if (peerId == GetLocalPeerId())
                {
                    if (!_localDowned && !_localNativeDeathCommitted)
                        return;
                    continue;
                }

                if (!TryGetPeerLife(peerId, out var peer) || !peer.IsDownOrDead)
                    return;
            }

            NetLogger.Info($"[PlayerLife] All known players are down/dead; committing native death for all. reason={reason} peers={string.Join(",", knownPeers)}");
            var commit = BuildHostState(NetPlayerLifeStateKind.NativeDeathCommit, "*", "all players downed");
            NetGameplaySyncBridge.ReportHostPlayerLifeStateToAll(commit);
            CommitLocalNativeDeath("all players downed");
        }

        private static void CommitLocalNativeDeath(string reason)
        {
            if (_localNativeDeathCommitted && !_localDowned) return;
            object? unit = ResolveCurrentLocalPlayerUnit();
            if (unit == null)
            {
                NetLogger.Warn($"[PlayerLife] Cannot commit native death: local player unit missing reason={reason}");
                return;
            }

            _localNativeDeathCommitted = true;
            _localDowned = false;
            MarkLocalDeathRespawnStarted(reason);
            ApplyLocalControlLocks(false);

            try
            {
                _nativeDeathBypassDepth++;
                var die = AccessTools.Method(unit.GetType(), "Die") ?? AccessTools.Method(AccessTools.TypeByName("PerfectRandom.Sulfur.Core.Units.Unit"), "Die");
                if (die == null)
                {
                    NetLogger.Warn($"[PlayerLife] Cannot commit native death: Unit.Die not found reason={reason}");
                    return;
                }

                NetLogger.Info($"[PlayerLife] Committing original player death: {reason}");
                die.Invoke(unit, null);
            }
            catch (TargetInvocationException ex)
            {
                NetLogger.Warn($"[PlayerLife] Original player death invoke failed: {ex.InnerException?.Message ?? ex.Message}");
            }
            catch (Exception ex)
            {
                NetLogger.Warn($"[PlayerLife] Original player death invoke failed: {ex.Message}");
            }
            finally
            {
                if (_nativeDeathBypassDepth > 0) _nativeDeathBypassDepth--;
            }
        }

        private static bool ReviveLocalPlayer(string reason)
        {
            if (!_localDowned || _localNativeDeathCommitted) return false;
            object? unit = ResolveCurrentLocalPlayerUnit();
            if (unit == null)
            {
                NetLogger.Warn($"[PlayerLife] Cannot revive local player: local unit missing reason={reason}");
                return false;
            }

            _localDowned = false;
            _localNativeDeathCommitted = false;
            ApplyLocalControlLocks(false);
            SetUnitState(unit, "Alive");
            HealUnitForRevive(unit);
            InvokeMethod(unit, "SetInvulnerableForDuration", Math.Max(0.1f, Plugin.Cfg.PlayerReviveInvulnerabilitySeconds.Value));
            PublishLocalLifeState(NetPlayerLifeStateKind.Alive, "", "revived/" + reason);
            MarkLocalState(NetPlayerLifeStateKind.Alive, reason);
            ResetReviveHold();
            NetLogger.Info($"[PlayerLife] Local player revived: {reason}");
            return true;
        }

        // A non-player IDamager Unit to attribute incoming player damage to, so Unit.ReceiveDamage applies + plays the
        // "got hit by enemy" feedback rather than being friendly-fire-blocked by a self/player source.
        // Phase 5.7-HG: cache a valid hostile IDamager source so we don't rescan GameManager.units (reflection) on
        // every single hit. The source is only used to satisfy ReceiveDamage's "not friendly-fire" requirement — any
        // live hostile enemy works, so a short cache is fine. Re-resolve when stale or the cached one died/despawned.
        private static object? _cachedHostileSource;
        private static float _cachedHostileSourceAt;
        private const float HostileSourceCacheSeconds = 2f;

        private static object? ResolveAnyHostileEnemy(object playerUnit)
        {
            float now = Time.realtimeSinceStartup;
            if (_cachedHostileSource != null
                && now - _cachedHostileSourceAt < HostileSourceCacheSeconds
                && !ReferenceEquals(_cachedHostileSource, playerUnit)
                && !(_cachedHostileSource is UnityEngine.Object uo && uo == null)
                && Boss.BossDamageReflect.IsValidDamageSource(_cachedHostileSource))
            {
                return _cachedHostileSource;
            }

            try
            {
                Type? gmType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.GameManager");
                object? gm = gmType == null ? null : AccessTools.Property(gmType, "Instance")?.GetValue(null, null);
                if (gm == null) return null;
                if (TryGetMemberValue(gm, "units") is System.Collections.IEnumerable units)
                    foreach (var u in units)
                    {
                        if (u == null || ReferenceEquals(u, playerUnit)) continue;
                        if (!Boss.BossDamageReflect.IsValidDamageSource(u)) continue; // must be IDamager
                        if (TryGetMemberValue(u, "isPlayer") is bool ip && ip) continue; // skip players
                        _cachedHostileSource = u;
                        _cachedHostileSourceAt = now;
                        return u;
                    }
            }
            catch { }
            return null;
        }

        private static System.Reflection.MethodInfo? _modifyStatusMethod;
        private static Type? _entityAttributesType;
        private static bool _elementalStatusResolveFailed;

        // Maps a DamageTypes value to the EntityAttributes negative-effect status that drives its hurt screen overlay.
        private static int MapDamageTypeToStatus(int damageTypeInt)
        {
            switch (damageTypeInt)
            {
                case 2:  return 22;  // Electric -> NegativeEffect_Electrocuted
                case 4:  return 19;  // Fire     -> NegativeEffect_Burning
                case 5:  return 25;  // Frost    -> NegativeEffect_Frozen
                case 9:  return 28;  // Poison   -> NegativeEffect_Poisoned
                case 13: return 33;  // Water    -> NegativeEffect_Wet
                case 16: return 102; // Bleed    -> NegativeEffect_Bleed
                default: return 0;
            }
        }

        // Applies the elemental status on the local player so the game's own AttributeEffect renders the matching
        // fullscreen hurt overlay (electrocuted/burning/frozen/...). Pure-additive; the game decays it over time.
        private static void TryApplyElementalHurtStatus(object unit, int damageTypeInt)
        {
            try
            {
                if (!Plugin.Cfg.EnableEnemyElementalStatusEffect.Value) return;
                int statusInt = MapDamageTypeToStatus(damageTypeInt);
                if (statusInt == 0) return;

                object? stats = TryGetMemberValue(unit, "Stats");
                if (stats == null) return;

                if (_modifyStatusMethod == null && !_elementalStatusResolveFailed)
                {
                    _entityAttributesType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.Stats.EntityAttributes");
                    // Real signature is ModifyStatus(EntityAttributes, float, bool skipOwnerCallback=false). The bool
                    // MUST stay false so owner.OnStatusUpdated fires — that callback drives the hurt-screen overlay.
                    _modifyStatusMethod = _entityAttributesType == null ? null
                        : AccessTools.Method(stats.GetType(), "ModifyStatus", new[] { _entityAttributesType, typeof(float), typeof(bool) });
                    if (_modifyStatusMethod == null) { _elementalStatusResolveFailed = true; NetLogger.Warn("[EnemyDamageAuthority] ModifyStatus(EntityAttributes,float,bool) not found — elemental status disabled"); return; }
                }
                if (_modifyStatusMethod == null || _entityAttributesType == null) return;

                object attr = Enum.ToObject(_entityAttributesType, statusInt);
                float amount = Plugin.Cfg.EnemyElementalStatusAmount.Value;
                _modifyStatusMethod.Invoke(stats, new object[] { attr, amount, false });
            }
            catch (Exception ex) { NetLogger.Warn($"[EnemyDamageAuthority] elemental status apply failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static void ApplyHostDamageToLocalPlayer(NetPlayerLifeState state)
        {
            // Phase 5.7-HG: time the whole per-hit apply (the native Unit.ReceiveDamage feedback runs synchronously inside
            // TryApplyRealDamage, so its cost is captured here). Logs only when over threshold → locates the per-hit hitch
            // without a per-hit log flood.
            if (!Plugin.Cfg.LogDamageApplyHitch.Value) { ApplyHostDamageToLocalPlayerInner(state); return; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { ApplyHostDamageToLocalPlayerInner(state); }
            finally
            {
                sw.Stop();
                double ms = sw.Elapsed.TotalMilliseconds;
                if (ms >= Plugin.Cfg.DamageApplyHitchThresholdMs.Value)
                    NetLogger.Info($"[DamageHitch] client apply took {ms:F1}ms reason={state?.Reason} type={state?.DamageType} dmg={(state == null ? 0f : state.DamageAmount):F1}");
            }
        }

        private static void ApplyHostDamageToLocalPlayerInner(NetPlayerLifeState state)
        {
            if (state == null) return;
            if (!NetRunStateBridge.TryGetLocalRunState(out var local) || !state.MatchesScene(local)) return;
            if (_localNativeDeathCommitted) return;

            object? unit = ResolveCurrentLocalPlayerUnit();
            if (unit == null)
            {
                NetLogger.Warn($"[EnemyDamageAuthority] Cannot apply Host damage: local player unit missing amount={state.DamageAmount:F1} reason={state.Reason}");
                return;
            }

            float damage = state.DamageAmount;
            if (damage <= 0f) damage = Plugin.Cfg.EnemyHostProjectileDamage.Value;
            if (damage <= 0f) return;

            if (_localDowned)
            {
                StabilizeDownedLocalPlayer("host damage while already downed");
                return;
            }

            // Preferred: apply through the real Unit.ReceiveDamage so the player gets native hit feedback (flash/shake/
            // sound/blood) + armor + the existing Unit.Die->downed interception. HostDamageApplyDepth bypasses the
            // client native-enemy-damage suppression. Needs a hostile IDamager source so it isn't friendly-fire-blocked.
            if (Plugin.Cfg.ApplyHostPlayerDamageViaReceiveDamage.Value)
            {
                object source = ResolveAnyHostileEnemy(unit) ?? unit;
                // Unit.ReceiveDamage rejects DamageTypes.None(0) outright ("Missing DamageType!" -> returns false), which
                // is why client damage never landed and the error spam stuttered the client. Use the host-forwarded real
                // type when present; otherwise fall back to the configured physical default (Normal=7).
                int damageTypeInt = state.DamageType != 0 ? state.DamageType : Plugin.Cfg.EnemyDamageDefaultType.Value;
                if (damageTypeInt == 0) damageTypeInt = 7;
                bool ok = false;
                try
                {
                    HostDamageApplyDepth++;
                    ok = Boss.BossDamageReflect.TryApplyRealDamage(unit, damage, damageTypeInt, source, out bool vanillaApplied, out string detail);
                    if (Plugin.Cfg.LogEnemyHostDamageAuthority.Value)
                        NetLogger.Info($"[EnemyDamageAuthority] via ReceiveDamage amount={damage:F1} type={damageTypeInt} ok={ok} applied={vanillaApplied} src={(source == unit ? "self" : source.GetType().Name)} detail={detail} reason={state.Reason}");
                    // Drive the element-specific hurt SCREEN effect by applying the matching status (the base
                    // ReceiveDamage(element) does NOT apply it — the projectile does, and that happens on the host's
                    // suppressed proxy). Only when damage actually landed (not invulnerable/parried).
                    if (ok && vanillaApplied)
                        TryApplyElementalHurtStatus(unit, damageTypeInt);
                }
                catch (Exception ex) { NetLogger.Warn($"[EnemyDamageAuthority] ReceiveDamage path failed: {ex.GetType().Name}: {ex.Message}"); }
                finally { if (HostDamageApplyDepth > 0) HostDamageApplyDepth--; }
                if (ok) return; // native path handled health + feedback + death/downed
                // else fall through to the raw health-write path below.
            }

            float current = GetUnitCurrentHealth(unit);
            if (current <= 0f) current = GetUnitMaxHealth(unit);
            if (current <= 0f) current = 100f;
            float next = current - damage;

            if (Plugin.Cfg.LogEnemyHostDamageAuthority.Value)
                NetLogger.Info($"[EnemyDamageAuthority] Client applying Host enemy damage amount={damage:F1} hp={current:F1}->{Math.Max(0f, next):F1} reason={state.Reason}");

            if (next <= 0f)
            {
                try { HostDamageApplyDepth++; SetUnitCurrentHealth(unit, 0f); } finally { if (HostDamageApplyDepth > 0) HostDamageApplyDepth--; }
                EnterLocalDownedState(unit, "host enemy damage/" + state.Reason);
                return;
            }

            try { HostDamageApplyDepth++; SetUnitCurrentHealth(unit, next); } finally { if (HostDamageApplyDepth > 0) HostDamageApplyDepth--; }
            // Diag: does the write stick immediately? (client HP keeps re-reading 100 → write not persisting / reset).
            if (Plugin.Cfg.LogEnemyHostDamageAuthority.Value)
            {
                float readback = GetUnitCurrentHealth(unit);
                NetLogger.Info($"[EnemyDamageAuthority] readback after SetCurrentHealth({next:F1}) = {readback:F1} (max={GetUnitMaxHealth(unit):F1})");
                if (!_healthDiagDumped)
                {
                    _healthDiagDumped = true;
                    try
                    {
                        const System.Reflection.BindingFlags BF = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                        string Match(Type? t, params string[] kws) => t == null ? "" : string.Join(",", t.GetMethods(BF)
                            .Where(m => !m.IsSpecialName && kws.Any(k => m.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
                            .Select(m => $"{m.Name}({m.GetParameters().Length})").Distinct().Take(30));
                        object? stats = TryGetMemberValue(unit, "Stats");
                        NetLogger.Info($"[EnemyDamageAuthority] HEALTHDIAG unit={unit.GetType().FullName} stats={(stats == null ? "null" : stats.GetType().Name)} statHealth=[{Match(stats?.GetType(), "Health", "Status", "Attribute")}]");
                        // Player hit-feedback methods (flash/shake/hurt sound/blood/vignette) — to call them on client damage.
                        NetLogger.Info($"[EnemyDamageAuthority] HEALTHDIAG Unit feedback=[{Match(unit.GetType(), "Damage", "Hit", "Hurt", "Flash", "Shake", "React", "Blood", "Vignette", "Feedback", "Effect", "Pain")}]");
                        object? playerScript = TryGetMemberValue(unit, "PlayerScript");
                        NetLogger.Info($"[EnemyDamageAuthority] HEALTHDIAG Player={(playerScript == null ? "null" : playerScript.GetType().Name)} feedback=[{Match(playerScript?.GetType(), "Damage", "Hit", "Hurt", "Flash", "Shake", "React", "Blood", "Vignette", "Feedback", "Effect", "Pain")}]");
                    }
                    catch (Exception ex) { NetLogger.Warn($"[EnemyDamageAuthority] HEALTHDIAG failed: {ex.Message}"); }
                }
            }
            TryInvokeHostDamageFeedback(unit, damage);
        }

        private static void TryInvokeHostDamageFeedback(object unit, float damage)
        {
            try
            {
                InvokeMethod(unit, "OnHealthChange");
                InvokeMethod(unit, "SetInvulnerableForDuration", 0.05f);
            }
            catch { }
        }

        private static void StabilizeDownedLocalPlayer(string reason)
        {
            object? unit = _localPlayerUnit;
            if (unit == null) return;

            SetUnitState(unit, "Incapacitated");
            SetUnitCurrentHealth(unit, Math.Max(1f, Plugin.Cfg.PlayerDownedHealthFloor.Value));
            InvokeMethod(unit, "SetInvulnerableForDuration", 999999f);
            ApplyLocalControlLocks(true);

            if (ShouldLogThrottled("stabilize", 5f) && Plugin.Cfg.LogPlayerLifeSync.Value)
                NetLogger.Info($"[PlayerLife] Stabilized downed local player reason={reason}");
        }

        private static void HealUnitForRevive(object unit)
        {
            float maxHp = GetUnitMaxHealth(unit);
            if (maxHp <= 0f) maxHp = 100f;
            float ratio = Plugin.Cfg.PlayerReviveHealthRatio.Value;
            if (ratio < 0.01f) ratio = 0.01f;
            if (ratio > 1f) ratio = 1f;
            SetUnitCurrentHealth(unit, Math.Max(1f, maxHp * ratio));
        }

        private static bool ShouldAllowImmediateNativeDeath(out string reason)
        {
            reason = "";
            var knownPeers = NetGameplaySyncBridge.GetKnownPlayerLifePeerIds().Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();
            string localPeerId = GetLocalPeerId();

            if (knownPeers.Count <= 1)
            {
                reason = "no connected co-op peer";
                return true;
            }

            foreach (string peerId in knownPeers)
            {
                if (peerId == localPeerId) continue;
                if (!TryGetPeerLife(peerId, out var peer) || !peer.IsDownOrDead)
                {
                    reason = $"peer still alive or unknown: {peerId}";
                    return false;
                }
            }

            reason = "all other known peers already down/dead";
            return true;
        }

        private static bool TryFindNearestDownedPeer(out PeerLife target, out float distance)
        {
            target = null!;
            distance = float.MaxValue;
            if (_localTransform == null) return false;

            string localPeerId = GetLocalPeerId();
            float radius = Math.Max(0.5f, Plugin.Cfg.PlayerReviveDistance.Value);
            Vector3 localPos = _localTransform.position;

            foreach (var peer in PeerStates.Values)
            {
                if (peer.PeerId == localPeerId) continue;
                if (peer.Kind != NetPlayerLifeStateKind.Downed) continue;
                if (!peer.HasPosition) continue;
                if (!IsSameCurrentScene(peer)) continue;

                float d = Vector3.Distance(localPos, peer.Position);
                if (d <= radius && d < distance)
                {
                    target = peer;
                    distance = d;
                }
            }

            return target != null;
        }

        private static bool IsSameCurrentScene(PeerLife peer)
        {
            if (!NetRunStateBridge.TryGetLocalRunState(out var local) || !local.HasLevel) return false;
            if (string.IsNullOrWhiteSpace(peer.SceneKey) || peer.SceneKey == "<unknown>") return false;
            if (peer.SceneKey != local.SceneKey()) return false;
            if (Plugin.Cfg.RequireSameLevelSeedForSceneMatch.Value && peer.HasLevelSeed && local.HasLevelSeed && peer.LevelSeed != local.LevelSeed) return false;
            return true;
        }

        private static void ResetReviveHold()
        {
            _activeReviveTarget = "";
            _activeReviveHoldStartedAt = 0f;
            _activeReviveRequestSent = false;
        }

        private static void PublishLocalLifeState(NetPlayerLifeStateKind kind, string targetPeerId, string reason)
        {
            var state = BuildLocalState(kind, targetPeerId, reason);
            NetGameplaySyncBridge.ReportPlayerLifeState(state);
        }

        private static NetPlayerLifeState BuildLocalState(NetPlayerLifeStateKind kind, string targetPeerId, string reason)
        {
            NetRunStateBridge.TryGetLocalRunState(out var runState);
            string peerId = string.IsNullOrWhiteSpace(runState.PeerId) ? GetLocalPeerId() : runState.PeerId;
            string playerName = string.IsNullOrWhiteSpace(runState.PlayerName) ? Plugin.Cfg.PlayerName.Value : runState.PlayerName;
            Vector3 pos = _localTransform != null ? _localTransform.position : Vector3.zero;
            bool hasPos = _localTransform != null;
            int seq = ++_sequence;

            return new NetPlayerLifeState
            {
                EventId = $"{peerId}:life:{seq}:{kind}",
                SourcePeerId = peerId,
                TargetPeerId = targetPeerId ?? "",
                PlayerName = playerName,
                ChapterName = runState.ChapterName,
                LevelIndex = runState.LevelIndex,
                HasLevelSeed = runState.HasLevelSeed,
                LevelSeed = runState.LevelSeed,
                Sequence = seq,
                Kind = kind,
                HasPosition = hasPos,
                Position = pos,
                SentAt = Time.realtimeSinceStartup,
                Reason = reason ?? "",
                DamageAmount = 0f,
            };
        }

        private static NetPlayerLifeState BuildHostState(NetPlayerLifeStateKind kind, string targetPeerId, string reason)
        {
            NetRunStateBridge.TryGetLocalRunState(out var runState);
            int seq = ++_sequence;
            return new NetPlayerLifeState
            {
                EventId = $"host:life:{seq}:{kind}:{targetPeerId}",
                SourcePeerId = "host",
                TargetPeerId = targetPeerId ?? "",
                PlayerName = Plugin.Cfg.PlayerName.Value,
                ChapterName = runState.ChapterName,
                LevelIndex = runState.LevelIndex,
                HasLevelSeed = runState.HasLevelSeed,
                LevelSeed = runState.LevelSeed,
                Sequence = seq,
                Kind = kind,
                HasPosition = _localTransform != null,
                Position = _localTransform != null ? _localTransform.position : Vector3.zero,
                SentAt = Time.realtimeSinceStartup,
                Reason = reason ?? "",
                DamageAmount = 0f,
            };
        }

        private static void RefreshLocalPeerAliveState()
        {
            if (_localDowned || _localNativeDeathCommitted) return;
            if (!IsEnabled()) return;
            MarkLocalState(NetPlayerLifeStateKind.Alive, "tick");
        }

        private static void MarkLocalState(NetPlayerLifeStateKind kind, string reason)
        {
            string peerId = GetLocalPeerId();
            if (string.IsNullOrWhiteSpace(peerId)) return;
            var state = BuildLocalState(kind, "", reason);
            MarkPeerState(peerId, state, kind);
        }

        private static void MarkPeerAlive(string peerId, NetPlayerLifeState state)
        {
            MarkPeerState(peerId, state, NetPlayerLifeStateKind.Alive);
        }

        private static void MarkPeerState(string peerId, NetPlayerLifeState state, NetPlayerLifeStateKind kind)
        {
            if (string.IsNullOrWhiteSpace(peerId)) return;
            var peer = GetOrCreatePeer(peerId);
            peer.Kind = kind;
            peer.PlayerName = state.PlayerName;
            peer.Sequence = Math.Max(peer.Sequence, state.Sequence);
            peer.LastUpdatedAt = Time.realtimeSinceStartup;
            if (state.HasPosition)
            {
                peer.HasPosition = true;
                peer.Position = state.Position;
            }
            if (state.HasScene)
            {
                peer.SceneKey = state.SceneKey;
                peer.HasLevelSeed = state.HasLevelSeed;
                peer.LevelSeed = state.LevelSeed;
            }

            if (Plugin.Cfg.LogPlayerLifeSync.Value)
                NetLogger.Info($"[PlayerLife] State update {peerId} => {kind} ({state.ToCompactString()})");
        }

        private static PeerLife GetOrCreatePeer(string peerId)
        {
            if (!PeerStates.TryGetValue(peerId, out var peer))
            {
                peer = new PeerLife { PeerId = peerId, Kind = NetPlayerLifeStateKind.Alive };
                PeerStates[peerId] = peer;
            }
            return peer;
        }

        private static bool TryGetPeerLife(string peerId, out PeerLife peer)
        {
            if (peerId == GetLocalPeerId())
            {
                peer = GetOrCreatePeer(peerId);
                peer.Kind = _localNativeDeathCommitted ? NetPlayerLifeStateKind.NativeDeathCommit : _localDowned ? NetPlayerLifeStateKind.Downed : NetPlayerLifeStateKind.Alive;
                if (_localTransform != null)
                {
                    peer.HasPosition = true;
                    peer.Position = _localTransform.position;
                }
                return true;
            }
            return PeerStates.TryGetValue(peerId, out peer);
        }

        private static bool TryGetPeerPosition(string peerId, out Vector3 position)
        {
            position = Vector3.zero;
            if (peerId == GetLocalPeerId())
            {
                if (_localTransform == null) return false;
                position = _localTransform.position;
                return true;
            }
            if (PeerStates.TryGetValue(peerId, out var peer) && peer.HasPosition)
            {
                position = peer.Position;
                return true;
            }
            return false;
        }

        private static string GetLocalPeerId()
        {
            if (NetRunStateBridge.TryGetLocalRunState(out var state) && !string.IsNullOrWhiteSpace(state.PeerId))
                return state.PeerId;
            return NetConfig.GetMode() == NetMode.Host ? "host" : "client-local";
        }

        private static bool IsEnabled()
        {
            if (!Plugin.Cfg.EnableCoopPlayerDownedRevive.Value) return false;
            if (!Plugin.Cfg.EnableNetworking.Value) return false;
            return NetConfig.GetMode() != NetMode.Off;
        }

        private static bool LooksLikePlayerUnit(object value)
        {
            try
            {
                if (!IsUnityObjectAlive(value)) return false;

                Type type = value.GetType();
                string typeName = type.Name ?? "";
                if (typeName == "Npc" || typeName == "Breakable") return false;
                if (ReferenceEquals(value, _localPlayerUnit)) return true;
                if (typeName == "Player") return true;

                if (value is Component c)
                {
                    string objectName = c.name ?? "";
                    if (objectName.IndexOf("Unit_Player", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                    if (objectName.IndexOf("Unit_Enemy", StringComparison.OrdinalIgnoreCase) >= 0)
                        return false;
                }

                object? isPlayer = TryGetMemberValue(value, "isPlayer");
                if (isPlayer is bool b && b && typeName != "Npc") return true;
            }
            catch { }
            return false;
        }

        private static object? ResolvePlayerUnit(object value)
        {
            if (LooksLikePlayerUnit(value)) return value;

            object? unit = TryGetMemberValue(value, "playerUnit")
                ?? TryGetMemberValue(value, "PlayerUnit")
                ?? TryGetMemberValue(value, "unit")
                ?? TryGetMemberValue(value, "Unit");
            if (unit != null && LooksLikePlayerUnit(unit)) return unit;
            return null;
        }

        private static Transform? ExtractTransform(object? value)
        {
            if (value == null) return null;
            if (!IsUnityObjectAlive(value)) return null;

            try
            {
                if (value is Transform t) return t;
                if (value is GameObject go) return go.transform;
                if (value is Component c) return c.transform;
            }
            catch { return null; }

            try
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var prop = value.GetType().GetProperty("transform", flags);
                if (prop != null && prop.GetValue(value, null) is Transform pt && pt != null) return pt;
                var field = value.GetType().GetField("transform", flags);
                if (field != null && field.GetValue(value) is Transform ft && ft != null) return ft;
            }
            catch { }
            return null;
        }

        private static bool IsUnityObjectAlive(object? value)
        {
            try
            {
                if (value == null) return false;
                if (value is UnityEngine.Object unityObject)
                    return unityObject != null;
                return true;
            }
            catch { return false; }
        }

        private static object? ResolveCurrentLocalPlayerUnit()
        {
            // Must be the Unit (has Stats/health/ReceiveDamage), NOT the Player component. Re-resolve if a cached value
            // has no Stats (log68: this used to return Player → Stats=null → all health reads/writes silently failed).
            if (_localPlayerUnit != null && IsUnityObjectAlive(_localPlayerUnit) && TryGetMemberValue(_localPlayerUnit, "Stats") != null)
                return _localPlayerUnit;

            try
            {
                Type? gmType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.GameManager");
                object? gm = gmType == null ? null : AccessTools.Property(gmType, "Instance")?.GetValue(null, null);
                if (gm != null)
                {
                    // Prefer GameManager.PlayerUnit (the Unit with Stats) over PlayerObject (which resolves to Player).
                    object? pu = AccessTools.Property(gmType, "PlayerUnit")?.GetValue(gm, null);
                    if (pu != null && IsUnityObjectAlive(pu) && TryGetMemberValue(pu, "Stats") != null)
                    {
                        _localPlayerUnit = pu;
                        _localTransform = ExtractTransform(pu);
                        return pu;
                    }

                    object? playerObject = TryGetMemberValue(gm, "PlayerObject");
                    if (playerObject != null)
                    {
                        object? unit = ResolvePlayerUnit(playerObject);
                        if (unit != null && TryGetMemberValue(unit, "Stats") != null)
                        {
                            _localPlayerUnit = unit;
                            _localTransform = ExtractTransform(unit) ?? ExtractTransform(playerObject);
                            return unit;
                        }
                    }
                }
            }
            catch { }

            _localPlayerUnit = null;
            _localTransform = null;
            return null;
        }

        private static object? TryGetMemberValue(object value, string name)
        {
            try
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var prop = value.GetType().GetProperty(name, flags);
                if (prop != null && prop.GetIndexParameters().Length == 0)
                    return prop.GetValue(value, null);
                var field = value.GetType().GetField(name, flags);
                if (field != null) return field.GetValue(value);
            }
            catch { }
            return null;
        }

        private static bool InvokeMethod(object target, string methodName, params object[] args)
        {
            try
            {
                var method = AccessTools.Method(target.GetType(), methodName);
                if (method == null) return false;
                method.Invoke(target, args);
                return true;
            }
            catch (Exception ex)
            {
                if (ShouldLogThrottled("invoke_" + methodName, 5f))
                    NetLogger.Warn($"[PlayerLife] Invoke {methodName} failed: {ex.Message}");
                return false;
            }
        }

        private static void SetUnitState(object unit, string stateName)
        {
            try
            {
                var method = AccessTools.Method(unit.GetType(), "SetUnitState")
                    ?? AccessTools.Method(AccessTools.TypeByName("PerfectRandom.Sulfur.Core.Units.Unit"), "SetUnitState");
                if (method == null) return;
                var p = method.GetParameters();
                if (p.Length != 1 || !p[0].ParameterType.IsEnum) return;
                object state = Enum.Parse(p[0].ParameterType, stateName);
                method.Invoke(unit, new[] { state });
            }
            catch (Exception ex)
            {
                if (ShouldLogThrottled("SetUnitState", 5f))
                    NetLogger.Warn($"[PlayerLife] SetUnitState({stateName}) failed: {ex.Message}");
            }
        }

        private static void SetUnitCurrentHealth(object unit, float hp)
        {
            try
            {
                object? stats = TryGetMemberValue(unit, "Stats");
                if (stats == null) return;

                var method = AccessTools.GetDeclaredMethods(stats.GetType())
                    .FirstOrDefault(m =>
                    {
                        if (m.Name != "SetStatus") return false;
                        var p = m.GetParameters();
                        if (p.Length != 3) return false;
                        if (p[1].ParameterType != typeof(float)) return false;
                        return p[0].ParameterType == typeof(int) || p[0].ParameterType.IsEnum;
                    });
                if (method == null) return;

                var parameters = method.GetParameters();
                object status = parameters[0].ParameterType.IsEnum
                    ? Enum.ToObject(parameters[0].ParameterType, StatusCurrentHealth)
                    : (object)StatusCurrentHealth;

                method.Invoke(stats, new[] { status, (object)hp, (object)false });
            }
            catch (Exception ex)
            {
                if (ShouldLogThrottled("SetUnitCurrentHealth", 5f))
                    NetLogger.Warn($"[PlayerLife] Set current health failed: {ex.Message}");
            }
        }

        private static float GetUnitCurrentHealth(object unit)
        {
            try
            {
                object? stats = TryGetMemberValue(unit, "Stats");
                if (stats == null) return 0f;

                var direct = AccessTools.Method(stats.GetType(), "GetCurrentHealth");
                if (direct != null)
                {
                    object? value = direct.Invoke(stats, null);
                    return value is float f ? f : Convert.ToSingle(value);
                }

                var method = AccessTools.GetDeclaredMethods(stats.GetType())
                    .FirstOrDefault(m =>
                    {
                        if (m.Name != "TryGetStatus" && m.Name != "GetStatus") return false;
                        var p = m.GetParameters();
                        return p.Length >= 1 && (p[0].ParameterType == typeof(int) || p[0].ParameterType.IsEnum);
                    });
                if (method == null) return 0f;
                var parameters = method.GetParameters();
                object status = parameters[0].ParameterType.IsEnum
                    ? Enum.ToObject(parameters[0].ParameterType, StatusCurrentHealth)
                    : (object)StatusCurrentHealth;
                object? result = method.Invoke(stats, new[] { status });
                return result is float f2 ? f2 : Convert.ToSingle(result);
            }
            catch { return 0f; }
        }

        private static float GetUnitMaxHealth(object unit)
        {
            try
            {
                object? stats = TryGetMemberValue(unit, "Stats");
                if (stats == null) return 0f;
                var method = AccessTools.Method(stats.GetType(), "GetAttribute");
                if (method == null) return 0f;
                var p = method.GetParameters();
                if (p.Length < 1 || !p[0].ParameterType.IsEnum) return 0f;
                object attr = Enum.ToObject(p[0].ParameterType, AttributeMaxHealth);
                object? value = method.Invoke(stats, new[] { attr });
                return value is float f ? f : Convert.ToSingle(value);
            }
            catch { return 0f; }
        }

        private static void ApplyLocalControlLocks(bool locked)
        {
            if (_localControlLocksApplied == locked) return;
            _localControlLocksApplied = locked;

            try
            {
                Type? gmType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.GameManager");
                if (gmType == null) return;
                object? gm = AccessTools.Property(gmType, "Instance")?.GetValue(null, null);
                if (gm == null) return;

                var addLock = AccessTools.Method(gmType, "AddLock");
                if (addLock == null) return;
                var p = addLock.GetParameters();
                if (p.Length < 2 || !p[0].ParameterType.IsEnum) return;

                // Downed-player input = a BLACKLIST of combat-related player systems (GameManager.PlayerLocks,
                // a [Flags] enum: Camera/Interaction/Weapon/PlayerMovement/Inventory/UseHUD). We lock ONLY the
                // combat actions the design wants gone while downed; everything else stays usable:
                //   - PlayerMovement → movement
                //   - Inventory      → opening the backpack
                //   - Weapon         → all weapon actions (shoot / reload / switch / melee)
                // We deliberately do NOT lock Camera (look around), Interaction, or UseHUD. The pause/menu/quit
                // path is NOT lock-gated at all (InputReader.TogglePause → GameManager.PauseGame, which only needs
                // gameState==Running) and the downed state keeps gameState Running — so a downed player can always
                // open the menu and quit, using whatever key they bound to TogglePause (we never hardcode ESC).
                foreach (string lockName in DownedInputBlacklist)
                {
                    if (!Enum.IsDefined(p[0].ParameterType, lockName)) continue;
                    object lockValue = Enum.Parse(p[0].ParameterType, lockName);
                    addLock.Invoke(gm, new[] { lockValue, (object)locked });
                }

                if (locked)
                {
                    object? equipment = TryGetMemberValue(gm, "EquipmentManager");
                    if (equipment != null)
                    {
                        InvokeMethod(equipment, "ReleaseTrigger");
                        InvokeMethod(equipment, "UnsightWeapon");
                    }
                }
            }
            catch (Exception ex)
            {
                if (ShouldLogThrottled("ControlLocks", 5f))
                    NetLogger.Warn($"[PlayerLife] Control lock {(locked ? "apply" : "release")} failed: {ex.Message}");
            }
        }

        private static void RefreshLocalPositionCache()
        {
            if (_localTransform != null && _localTransform) return;
            _localTransform = null;

            if (_localPlayerUnit != null && IsUnityObjectAlive(_localPlayerUnit))
                _localTransform = ExtractTransform(_localPlayerUnit);

            if (_localTransform == null)
                ResolveCurrentLocalPlayerUnit();
        }

        private static bool ShouldLogThrottled(string key, float interval)
        {
            float now = Time.realtimeSinceStartup;
            if (LastLogByKey.TryGetValue(key, out float last) && now - last < interval) return false;
            LastLogByKey[key] = now;
            return true;
        }

        private static string DescribeObject(object value)
        {
            try
            {
                var c = value as Component;
                if (c != null && c) return $"{value.GetType().Name}:{c.name}#{c.GetInstanceID()}";
                return $"{value.GetType().Name}#{value.GetHashCode()}";
            }
            catch { return value.GetType().Name; }
        }
    }
}
