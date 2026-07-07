using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Host-authoritative live tracker for the current Run's per-player statistics (RS-1). The Host observes or
    /// computes every one of these events already (client-to-host-star topology — the Host is either the authority
    /// computing the real post-mitigation number, or the relay hub every peer's report passes through), so this
    /// accumulates incrementally during play rather than collecting a snapshot from clients at Run-end time.
    /// All public methods are no-ops on a Client.
    /// </summary>
    internal static class NetRunStatsManager
    {
        private static readonly Dictionary<string, NetRunStats> _live = new Dictionary<string, NetRunStats>();

        // Kill attribution: last peer to damage a given entity, keyed by a stable runtime identity (EntityKey).
        private static readonly Dictionary<int, string> _lastDamager = new Dictionary<int, string>();
        // Dedup so a death reached via more than one Harmony hook (e.g. Npc.Die delegating into Unit.Die) never
        // double-counts a kill for the same entity.
        private static readonly HashSet<int> _killedKeys = new HashSet<int>();

        // Two-phase (Pre captures "before" HP, Post reads "after" HP) damage-dealt bookkeeping for hits the Host
        // applies to its OWN local player's targets directly (no client round trip involved).
        private static readonly Dictionary<int, (string PeerId, float BeforeHp)> _pendingHit =
            new Dictionary<int, (string PeerId, float BeforeHp)>();

        private static float _hostOwnPlayerPendingBeforeHp = -1f;
        private static int _runSeq;

        public static bool RunActive { get; private set; }

        private static bool IsHost => NetConfig.GetMode() == NetMode.Host;

        /// <summary>Stable per-instance identity for run-stats bookkeeping — Unity's own InstanceID when available,
        /// otherwise a runtime object hash. Deliberately independent of NetGameplayProbeManager's snapshot registry
        /// so this feature never has to reach into (or risk perturbing) that system's internals.</summary>
        public static int EntityKey(object entity)
        {
            if (entity is Object uo && uo != null) return uo.GetInstanceID();
            return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(entity);
        }

        public static void BeginRun()
        {
            if (!IsHost) return;
            if (RunActive) return; // already mid-run — do not reset counters (e.g. a stray non-hub transition inside the same run)
            _live.Clear();
            _lastDamager.Clear();
            _killedKeys.Clear();
            _pendingHit.Clear();
            _hostOwnPlayerPendingBeforeHp = -1f;
            RunActive = true;
            NetLogger.Info("[RunStats] Run begun");
        }

        /// <summary>Builds the final, host-ordered snapshot (by session Slot) for broadcast/display, filling in a
        /// zero-stat entry for any connected peer that never triggered a tracked event, and clears Run state.</summary>
        public static NetRunStatsList FinalizeAndSnapshot()
        {
            if (!IsHost)
            {
                RunActive = false;
                return new NetRunStatsList { RunSeq = 0, Players = new List<NetRunStats>() };
            }

            var sessions = NetGameplaySyncBridge.GetSessionsSnapshot()
                .Where(s => s.State == NetConnectionState.Connected)
                .OrderBy(s => s.Slot)
                .ToList();

            var ordered = new List<NetRunStats>(sessions.Count);
            foreach (var session in sessions)
            {
                var stats = GetOrCreate(session.PeerId).Clone();
                stats.PlayerName = session.PlayerName;
                ordered.Add(stats);
            }

            RunActive = false;
            var result = new NetRunStatsList { RunSeq = ++_runSeq, Players = ordered };
            NetLogger.Info($"[RunStats] Finalized runSeq={result.RunSeq} {ordered.Count} player(s)");
            foreach (var s in ordered) NetLogger.Info($"[RunStats]   {s.ToCompactString()}");

            _live.Clear();
            _lastDamager.Clear();
            _killedKeys.Clear();
            _pendingHit.Clear();
            _hostOwnPlayerPendingBeforeHp = -1f;
            return result;
        }

        private static NetRunStats GetOrCreate(string peerId)
        {
            if (!_live.TryGetValue(peerId, out var stats))
            {
                stats = new NetRunStats { PeerId = peerId };
                _live[peerId] = stats;
            }
            return stats;
        }

        public static void RecordShotFired(string peerId)
        {
            if (!IsHost || !RunActive || string.IsNullOrWhiteSpace(peerId)) return;
            GetOrCreate(peerId).ShotsFired++;
        }

        public static void RecordDestructibleDestroyed(string peerId)
        {
            if (!IsHost || !RunActive || string.IsNullOrWhiteSpace(peerId)) return;
            GetOrCreate(peerId).DestructiblesDestroyed++;
        }

        public static void RecordDowned(string peerId)
        {
            if (!IsHost || !RunActive || string.IsNullOrWhiteSpace(peerId)) return;
            GetOrCreate(peerId).TimesDowned++;
        }

        public static void RecordRescue(string rescuerPeerId)
        {
            if (!IsHost || !RunActive || string.IsNullOrWhiteSpace(rescuerPeerId)) return;
            GetOrCreate(rescuerPeerId).Rescues++;
        }

        public static void RecordDamageTaken(string peerId, float actualAmount)
        {
            if (!IsHost || !RunActive || string.IsNullOrWhiteSpace(peerId) || actualAmount <= 0f) return;
            GetOrCreate(peerId).DamageTaken += Mathf.RoundToInt(actualAmount);
        }

        /// <summary>Record a fully-known damage-dealt delta (e.g. a boss hit, where before/after HP are already read
        /// within a single call) and note the peer as the entity's last damager for kill attribution.</summary>
        public static void RecordDamageDealtDelta(string peerId, int entityKey, float delta)
        {
            if (!IsHost || !RunActive || string.IsNullOrWhiteSpace(peerId) || delta <= 0f) return;
            GetOrCreate(peerId).DamageDealt += Mathf.RoundToInt(delta);
            _lastDamager[entityKey] = peerId;
        }

        /// <summary>Pre-phase half of a two-phase local hit (Host's own local player hitting a regular Npc): cache
        /// the pre-damage HP so the Post-phase can compute the real, post-mitigation delta.</summary>
        public static void NotePendingLocalHit(int entityKey, string peerId, float beforeHp)
        {
            if (!IsHost || !RunActive || string.IsNullOrWhiteSpace(peerId)) return;
            _pendingHit[entityKey] = (peerId, beforeHp);
        }

        /// <summary>Post-phase half of a two-phase local hit: read the real post-damage HP, compute the delta, and
        /// record it exactly like a fully-known delta.</summary>
        public static void ResolvePendingLocalHit(int entityKey, float afterHp)
        {
            if (!IsHost || !_pendingHit.TryGetValue(entityKey, out var pending)) return;
            _pendingHit.Remove(entityKey);
            RecordDamageDealtDelta(pending.PeerId, entityKey, pending.BeforeHp - afterHp);
        }

        public static void NoteHostOwnPlayerBeforeHp(float beforeHp)
        {
            if (!IsHost || !RunActive) return;
            _hostOwnPlayerPendingBeforeHp = beforeHp;
        }

        public static void ResolveHostOwnPlayerDamageTaken(float afterHp)
        {
            if (!IsHost || _hostOwnPlayerPendingBeforeHp < 0f) return;
            float before = _hostOwnPlayerPendingBeforeHp;
            _hostOwnPlayerPendingBeforeHp = -1f;
            RecordDamageTaken("host", before - afterHp);
        }

        /// <summary>Called when a lethal hit on the Host's own player is converted into the co-op "downed" state
        /// instead of a real death. By the time Unit.ReceiveDamage's postfix runs, the downed-state stabilization
        /// has already healed the unit back up (it runs synchronously inside the same native call, via the nested
        /// Unit.Die intercept), so the postfix's "after HP" no longer reflects the real lethal delta. Treat the
        /// pending pre-hit HP itself as the damage taken and clear it so the postfix's later resolve is a no-op.</summary>
        public static void ResolveHostOwnPlayerDowned()
        {
            if (!IsHost || _hostOwnPlayerPendingBeforeHp < 0f) return;
            float before = _hostOwnPlayerPendingBeforeHp;
            _hostOwnPlayerPendingBeforeHp = -1f;
            RecordDamageTaken("host", before);
        }

        /// <summary>Attribute a kill to whoever last damaged this entity (falling back to the Host's own peer id for
        /// unattributed kills — environment, host-only damage sources, etc.), deduped per entity.</summary>
        public static void RecordKill(int entityKey, string fallbackPeerId)
        {
            if (!IsHost || !RunActive) return;
            if (!_killedKeys.Add(entityKey)) return; // already attributed (e.g. Npc.Die + Unit.Die both firing)

            string peerId = _lastDamager.TryGetValue(entityKey, out var lastDamager) ? lastDamager : fallbackPeerId;
            _lastDamager.Remove(entityKey);
            if (string.IsNullOrWhiteSpace(peerId)) return;
            GetOrCreate(peerId).Kills++;
        }
    }
}
