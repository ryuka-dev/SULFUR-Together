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
        // Rescuer-local edge detection only (DR-1): which downed peer we've told the Host we're holding for.
        // The Host owns the actual rescue clock — this field never drives a progress value, only start/stop edges.
        private static string _activeReviveTarget = "";

        internal enum RescueEndReason { None, Completed, Cancelled }

        internal struct RescueDisplayState
        {
            public string RescuerPeerId;
            public string TargetPeerId;
            public float Progress;
            public bool Active;
            public RescueEndReason LastEndReason;
        }

        // DR-1: the one authoritative rescue-progress value both the rescuer's and the downed player's UI read.
        // Written directly by the Host's own tick (host path) and by HandleNetworkState on RescueProgress/
        // RescueCancelled/ReviveAccepted (client path) — never computed independently by either UI.
        private static RescueDisplayState _rescueDisplay;
        public static RescueDisplayState CurrentRescueDisplay => _rescueDisplay;

        // DR-2: read-only surface for the uGUI overlay — never a second source of truth (the fields above remain
        // the only owners), just projections of already-private state into public, UI-friendly shapes.
        public static string LocalPeerId => GetLocalPeerId();

        public static string GetKnownPeerDisplayName(string peerId)
        {
            if (string.IsNullOrWhiteSpace(peerId)) return "";
            if (peerId == GetLocalPeerId()) return Plugin.Cfg.PlayerName.Value;
            return PeerStates.TryGetValue(peerId, out var peer) && !string.IsNullOrWhiteSpace(peer.PlayerName) ? peer.PlayerName : peerId;
        }

        /// <summary>Local-only proximity hint (not network state): is the local player near a downed teammate
        /// they could start rescuing? Lets the rescuer's UI show a "Hold [key]" prompt before a hold has actually
        /// started (matching the pre-DR-1 UX) without waiting on a round trip to the Host.</summary>
        public static bool TryGetNearestDownedPeerHint(out string peerId, out string playerName, out float distance)
        {
            peerId = ""; playerName = ""; distance = 0f;
            if (!IsEnabled() || _localDowned || _localNativeDeathCommitted) return false;
            if (!TryFindNearestDownedPeer(out var target, out var dist)) return false;
            peerId = target.PeerId;
            playerName = string.IsNullOrWhiteSpace(target.PlayerName) ? target.PeerId : target.PlayerName;
            distance = dist;
            return true;
        }

        // Host-only: one active rescue per downed target (keyed by TargetPeerId so multiple simultaneous
        // rescues of different downed players — 3+ player sessions — don't collide). First rescuer to reach a
        // given target wins the slot (same first-come rule as WorldPickupTakeRequest).
        private sealed class ActiveRescueRecord
        {
            public string RescuerPeerId = "";
            public string TargetPeerId = "";
            public float StartedAt;
            public float LastBroadcastAt;
        }
        private static readonly Dictionary<string, ActiveRescueRecord> _hostActiveRescues = new Dictionary<string, ActiveRescueRecord>();
        private static readonly List<string> _rescueTickScratch = new List<string>();
        private const float RescueProgressBroadcastIntervalSeconds = 0.12f;

        public static void ClearLevelScoped(string source)
        {
            if (_localControlLocksApplied)
                ApplyLocalControlLocks(false);

            _localDowned = false;
            _localNativeDeathCommitted = false;
            _localDownedAt = 0f;
            _activeReviveTarget = "";
            _rescueDisplay = default;
            _hostActiveRescues.Clear();
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
            if (NetConfig.GetMode() == NetMode.Host)
                TickHostActiveRescues();
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

            if (state.Kind == NetPlayerLifeStateKind.RescueHoldStart)
            {
                if (receivedOnHost)
                    HandleRescueHoldStartOnHost(state);
                return;
            }

            if (state.Kind == NetPlayerLifeStateKind.RescueHoldStop)
            {
                if (receivedOnHost)
                    HandleRescueHoldStopOnHost(state);
                return;
            }

            if (state.Kind == NetPlayerLifeStateKind.RescueProgress)
            {
                ApplyRescueDisplay(state.SourcePeerId, state.TargetPeerId, state.Progress, true, RescueEndReason.None);
                return;
            }

            if (state.Kind == NetPlayerLifeStateKind.RescueCancelled)
            {
                ApplyRescueDisplay(state.SourcePeerId, state.TargetPeerId, 0f, false, RescueEndReason.Cancelled);
                return;
            }

            if (state.Kind == NetPlayerLifeStateKind.ReviveAccepted)
            {
                if (targetsLocal)
                    ReviveLocalPlayer("revive accepted by host");

                if (!string.IsNullOrWhiteSpace(state.TargetPeerId))
                    MarkPeerAlive(state.TargetPeerId, state);

                // DR-1: if this is the rescue our own UI was showing, mark it completed (vs cancelled) so DR-2's
                // panel can play the completion text before fading, instead of just going dark.
                if (_rescueDisplay.Active && _rescueDisplay.TargetPeerId == state.TargetPeerId)
                    ApplyRescueDisplay(_rescueDisplay.RescuerPeerId, _rescueDisplay.TargetPeerId, 1f, false, RescueEndReason.Completed);
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

            if (state.Kind == NetPlayerLifeStateKind.DamageTakenReport)
            {
                if (receivedOnHost)
                    NetRunStatsManager.RecordDamageTaken(state.SourcePeerId, state.DamageAmount);
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


        private static void EnterLocalDownedState(object unit, string source)
        {
            // DR-1: if this player was mid-hold rescuing someone else the instant they themselves went down,
            // tell the Host that hold ended (otherwise the Host's own eligibility re-check catches it next tick
            // anyway, but this avoids even the one-tick lag).
            StopLocalRescueHold();

            _localDowned = true;
            _localNativeDeathCommitted = false;
            _localDownedAt = Time.realtimeSinceStartup;

            // RS-1: this may be the Host's own native lethal hit being intercepted here (Unit.Die called from
            // inside the still-executing Unit.ReceiveDamage). StabilizeDownedLocalPlayer below heals the unit
            // back up before ReceiveDamage's postfix ever reads "after HP", so resolve the pending damage-taken
            // now, using the pre-hit HP, before that healed value can mask it. No-op on a Client (that path
            // reports its own damage-taken explicitly via ReportActualDamageTakenToHost).
            if (NetConfig.GetMode() == NetMode.Host)
                NetRunStatsManager.ResolveHostOwnPlayerDowned();

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

        // DR-1: this only ever reports hold START/STOP edges to the Host — it never times or computes progress
        // itself. The Host owns the rescue clock (TickHostActiveRescues) so both ends read one authoritative
        // value (CurrentRescueDisplay) instead of racing two independent local timers.
        private static void HandleReviveHoldInput()
        {
            if (_localDowned || _localNativeDeathCommitted) return;
            if (_localTransform == null) return;

            if (!Plugin.Cfg.PlayerReviveHoldKey.Value.IsPressed() || !TryFindNearestDownedPeer(out var target, out var distance))
            {
                StopLocalRescueHold();
                return;
            }

            if (_activeReviveTarget == target.PeerId) return; // already holding for this target; Host owns timing

            if (!string.IsNullOrEmpty(_activeReviveTarget))
                StopLocalRescueHold(); // switched target without releasing the key first

            _activeReviveTarget = target.PeerId;
            SendRescueHoldStart(target.PeerId, distance);
        }

        private static void SendRescueHoldStart(string targetPeerId, float distance)
        {
            var request = BuildLocalState(NetPlayerLifeStateKind.RescueHoldStart, targetPeerId, $"hold revive distance={distance:F2}m");
            NetGameplaySyncBridge.ReportPlayerLifeState(request);
            if (NetConfig.GetMode() == NetMode.Host)
                HandleRescueHoldStartOnHost(request);

            if (Plugin.Cfg.LogPlayerLifeSync.Value)
                NetLogger.Info($"[PlayerLife] Rescue hold start reported target={targetPeerId} distance={distance:F2}m");
        }

        private static void StopLocalRescueHold()
        {
            string targetPeerId = _activeReviveTarget;
            _activeReviveTarget = "";
            if (string.IsNullOrEmpty(targetPeerId)) return;

            var request = BuildLocalState(NetPlayerLifeStateKind.RescueHoldStop, targetPeerId, "hold released/lost target");
            NetGameplaySyncBridge.ReportPlayerLifeState(request);
            if (NetConfig.GetMode() == NetMode.Host)
                HandleRescueHoldStopOnHost(request);
        }

        // ----- Host-authoritative rescue clock (DR-1) -----

        private static bool TryValidateRescueEligibility(string rescuerPeerId, string targetPeerId, out string reason)
        {
            reason = "";
            if (!TryGetPeerLife(targetPeerId, out var target) || target.Kind != NetPlayerLifeStateKind.Downed)
            {
                reason = "target not downed";
                return false;
            }

            if (TryGetPeerLife(rescuerPeerId, out var rescuer) && rescuer.IsDownOrDead)
            {
                reason = "rescuer is not alive";
                return false;
            }

            if (Plugin.Cfg.RequireReviveDistanceValidationOnHost.Value)
            {
                if (!TryGetPeerPosition(rescuerPeerId, out var rescuerPos) || !TryGetPeerPosition(targetPeerId, out var targetPos))
                {
                    reason = "missing position";
                    return false;
                }

                float distance = Vector3.Distance(rescuerPos, targetPos);
                float radius = Math.Max(0.5f, Plugin.Cfg.PlayerReviveDistance.Value + 1.0f);
                if (distance > radius)
                {
                    reason = $"too far ({distance:F2}m > {radius:F2}m)";
                    return false;
                }
            }

            return true;
        }

        private static void HandleRescueHoldStartOnHost(NetPlayerLifeState request)
        {
            string rescuerPeerId = request.SourcePeerId;
            string targetPeerId = request.TargetPeerId;
            if (string.IsNullOrWhiteSpace(rescuerPeerId) || string.IsNullOrWhiteSpace(targetPeerId) || rescuerPeerId == targetPeerId)
            {
                NetLogger.Warn($"[PlayerLife] Reject rescue hold start: malformed {request.ToCompactString()}");
                return;
            }

            if (_hostActiveRescues.ContainsKey(targetPeerId))
                return; // another rescuer already holds this target's slot — first-come-wins (WorldPickupTakeRequest rule)

            if (!TryValidateRescueEligibility(rescuerPeerId, targetPeerId, out string reason))
            {
                if (Plugin.Cfg.LogPlayerLifeSync.Value)
                    NetLogger.Warn($"[PlayerLife] Reject rescue hold start: {reason} rescuer={rescuerPeerId} target={targetPeerId}");
                return;
            }

            float now = Time.realtimeSinceStartup;
            var record = new ActiveRescueRecord { RescuerPeerId = rescuerPeerId, TargetPeerId = targetPeerId, StartedAt = now, LastBroadcastAt = now };
            _hostActiveRescues[targetPeerId] = record;
            BroadcastRescueProgress(record, 0f);

            if (Plugin.Cfg.LogPlayerLifeSync.Value)
                NetLogger.Info($"[PlayerLife] Rescue started rescuer={rescuerPeerId} target={targetPeerId}");
        }

        private static void HandleRescueHoldStopOnHost(NetPlayerLifeState request)
        {
            if (!_hostActiveRescues.TryGetValue(request.TargetPeerId, out var record)) return;
            if (record.RescuerPeerId != request.SourcePeerId) return; // stale stop from a rescuer that already lost the slot
            CancelHostActiveRescue(record, "rescuer released hold");
        }

        private static void TickHostActiveRescues()
        {
            if (_hostActiveRescues.Count == 0) return;

            _rescueTickScratch.Clear();
            _rescueTickScratch.AddRange(_hostActiveRescues.Keys);

            float required = Math.Max(0.1f, Plugin.Cfg.PlayerReviveHoldSeconds.Value);
            float now = Time.realtimeSinceStartup;

            foreach (string targetPeerId in _rescueTickScratch)
            {
                if (!_hostActiveRescues.TryGetValue(targetPeerId, out var record)) continue;

                // Re-validated every tick (not just at hold-start) — this is what gives "rescuer leaves range /
                // dies / target stops being downed" auto-cancel, matching the design spec's interruption rules.
                if (!TryValidateRescueEligibility(record.RescuerPeerId, targetPeerId, out string invalidReason))
                {
                    CancelHostActiveRescue(record, invalidReason);
                    continue;
                }

                float progress = Mathf.Clamp01((now - record.StartedAt) / required);
                if (progress >= 1f)
                {
                    CompleteHostActiveRescue(record);
                    continue;
                }

                if (now - record.LastBroadcastAt >= RescueProgressBroadcastIntervalSeconds)
                {
                    record.LastBroadcastAt = now;
                    BroadcastRescueProgress(record, progress);
                }
            }
        }

        private static void BroadcastRescueProgress(ActiveRescueRecord record, float progress)
        {
            var state = BuildHostState(NetPlayerLifeStateKind.RescueProgress, record.TargetPeerId, "rescue progress");
            // RescueProgress/RescueCancelled repurpose SourcePeerId as "the rescuer's peer id" (mirroring the
            // client-sent RescueHoldStart/Stop convention) rather than "who sent this packet" (always the Host
            // for these two Kinds) — BuildHostState's default of "host" is overwritten here on purpose.
            state.SourcePeerId = record.RescuerPeerId;
            state.Progress = progress;
            NetGameplaySyncBridge.ReportHostPlayerLifeStateToAll(state);
            ApplyRescueDisplay(record.RescuerPeerId, record.TargetPeerId, progress, true, RescueEndReason.None);
        }

        private static void CancelHostActiveRescue(ActiveRescueRecord record, string reason)
        {
            _hostActiveRescues.Remove(record.TargetPeerId);

            var state = BuildHostState(NetPlayerLifeStateKind.RescueCancelled, record.TargetPeerId, reason);
            state.SourcePeerId = record.RescuerPeerId;
            NetGameplaySyncBridge.ReportHostPlayerLifeStateToAll(state);
            ApplyRescueDisplay(record.RescuerPeerId, record.TargetPeerId, 0f, false, RescueEndReason.Cancelled);

            if (Plugin.Cfg.LogPlayerLifeSync.Value)
                NetLogger.Info($"[PlayerLife] Rescue cancelled rescuer={record.RescuerPeerId} target={record.TargetPeerId} reason={reason}");
        }

        private static void CompleteHostActiveRescue(ActiveRescueRecord record)
        {
            _hostActiveRescues.Remove(record.TargetPeerId);

            var accepted = BuildHostState(NetPlayerLifeStateKind.ReviveAccepted, record.TargetPeerId, $"revived by {record.RescuerPeerId}");
            MarkPeerAlive(record.TargetPeerId, accepted);
            NetGameplaySyncBridge.ReportHostPlayerLifeStateToAll(accepted);
            NetRunStatsManager.RecordRescue(record.RescuerPeerId);
            ApplyRescueDisplay(record.RescuerPeerId, record.TargetPeerId, 1f, false, RescueEndReason.Completed);

            if (record.TargetPeerId == GetLocalPeerId())
                ReviveLocalPlayer($"revived by {record.RescuerPeerId}");

            NetLogger.Info($"[PlayerLife] Revive accepted rescuer={record.RescuerPeerId} target={record.TargetPeerId}");
        }

        // Every broadcast fans out to ALL peers regardless of who's actually involved (ReportHostPlayerLifeStateToAll
        // is unconditional) — in a 3+ player session, two independent simultaneous rescues would otherwise stomp
        // each other in this single cached slot. The DR-2 UI only ever needs to show a rescue the LOCAL player is
        // personally part of (rescuer or downed target), so anything else is filtered out right here, once, rather
        // than at every call site.
        private static void ApplyRescueDisplay(string rescuerPeerId, string targetPeerId, float progress, bool active, RescueEndReason endReason)
        {
            string localPeerId = GetLocalPeerId();
            if (rescuerPeerId != localPeerId && targetPeerId != localPeerId) return;

            _rescueDisplay = new RescueDisplayState
            {
                RescuerPeerId = rescuerPeerId ?? "",
                TargetPeerId = targetPeerId ?? "",
                Progress = Mathf.Clamp01(progress),
                Active = active,
                LastEndReason = endReason
            };
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

        /// <summary>FF-1: apply a validated client friendly-fire hit to the HOST's own player. Thin wrapper over the
        /// same apply path a client uses for a HostDamageRequest — native ReceiveDamage feedback, armor, elemental
        /// status and the downed/death interception all come along. The state is built locally so
        /// <c>MatchesScene</c> passes (the host is by definition in its own scene).</summary>
        internal static void ApplyFriendlyFireDamageToLocalHost(float damage, int damageTypeInt, Vector3 hitPos, string reason)
        {
            if (NetConfig.GetMode() != NetMode.Host || damage <= 0f) return;
            var state = BuildLocalState(NetPlayerLifeStateKind.HostDamageRequest, GetLocalPeerId(), reason);
            state.DamageAmount = damage;
            state.DamageType = damageTypeInt;
            if (hitPos != Vector3.zero) { state.HasPosition = true; state.Position = hitPos; }
            ApplyHostDamageToLocalPlayer(state);
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

            // RS-1: only the Client can read its own real pre/post-mitigation HP — cache "before" so each success
            // path below can report the actual delta back to the Host's run-stats tracker.
            float beforeHp = GetUnitCurrentHealth(unit);

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
                if (ok)
                {
                    ReportActualDamageTakenToHost(beforeHp - GetUnitCurrentHealth(unit));
                    return; // native path handled health + feedback + death/downed
                }
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
                ReportActualDamageTakenToHost(beforeHp);
                EnterLocalDownedState(unit, "host enemy damage/" + state.Reason);
                return;
            }

            try { HostDamageApplyDepth++; SetUnitCurrentHealth(unit, next); } finally { if (HostDamageApplyDepth > 0) HostDamageApplyDepth--; }
            ReportActualDamageTakenToHost(beforeHp - next);
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

        // RS-1: report the real, post-mitigation damage this Client's own player just took, so the Host's run-stats
        // tracker (the only place that decides final displayed content) gets an accurate number — the Host cannot
        // compute this itself, since the actual HP deduction only ever happens on this Client's own machine.
        private static void ReportActualDamageTakenToHost(float actualAmount)
        {
            if (actualAmount <= 0f) return;
            if (NetConfig.GetMode() != NetMode.Client) return;
            var state = BuildLocalState(NetPlayerLifeStateKind.DamageTakenReport, "", "actual damage taken");
            state.DamageAmount = actualAmount;
            NetGameplaySyncBridge.ReportPlayerLifeState(state);
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

        // Defensive local-only clear (no network send) for the impossible-in-practice case of a just-revived
        // player's own rescuer-hold tracking still being set (HandleReviveHoldInput never runs while downed, so
        // this is belt-and-suspenders, not a real path). Use StopLocalRescueHold() for an actual release.
        private static void ResetReviveHold()
        {
            _activeReviveTarget = "";
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
            var previousKind = peer.Kind;
            peer.Kind = kind;
            peer.PlayerName = state.PlayerName;
            // RS-1: count a downed transition once, on the edge into Downed — not on every periodic Alive/position
            // refresh that also flows through this shared apply point.
            if (kind == NetPlayerLifeStateKind.Downed && previousKind != NetPlayerLifeStateKind.Downed)
                NetRunStatsManager.RecordDowned(peerId);
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
            return NetConfig.GetMode() != NetMode.Off; // GetMode()==Off already means no live session
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
