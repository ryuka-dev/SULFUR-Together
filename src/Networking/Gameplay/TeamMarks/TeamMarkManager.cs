using System;
using System.Collections.Generic;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Units;
using PerfectRandom.Sulfur.Core.Utilities;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay.TeamMarks
{
    /// <summary>
    /// Team marks (issue #26). Each player has one mark, on the enemy they last hit directly with a gun or
    /// melee. Hitting an enemy that carries someone else's mark deals more damage; your own mark does nothing for you.
    ///
    /// <para><b>Host owns the table.</b> It records the host's own direct hits (<see cref="TeamMarkPatches"/>) and the
    /// clients' (settled in <c>NetGameplayProbeManager.SettleRosterHit</c>), applies the multiplier there, and sends the
    /// whole table to the clients when it changes and every <see cref="ResendSeconds"/>. A client only displays what it
    /// was sent. Ordinary roster enemies only: bosses, boss parts and boss adds never carry a mark.</para>
    ///
    /// <para><b>Lifecycle.</b> Owned by this class's own <see cref="Tick"/>, not by any probe: the table is stamped with
    /// the level it belongs to and drops itself when the local run moves to another level, so it never depends on a
    /// diagnostic switch or on a teardown hook being reached. <see cref="Reset"/> on session start/stop.</para>
    /// </summary>
    internal static class TeamMarkManager
    {
        // ----------------------------------------------------------------- rule (issue #26)

        public const float MarkSeconds = 3f;          // a mark lasts this long; every direct hit on its enemy refreshes it
        public const float MoveCooldownSeconds = 1.5f; // a mark moves no sooner than this after it was placed
        public const int   MoveHits = 2;              // direct hits on the new enemy it takes to move a mark there
        public const float LingerSeconds = 0.8f;      // the old enemy keeps the mark this long after it moves or runs out
        public const float OneMarkMultiplier = 1.5f;  // one other player's mark: +50%
        public const float TwoMarkMultiplier = 1.75f; // a second adds +25%, and that is the cap (a third adds nothing)
        public const float ResendSeconds = 2.5f;
        // One trigger pull reaches ReceiveDamage once per pellet and per damage component, on the host and in the
        // client's requests alike; damage calls closer together than this are the same shot for the move count.
        public const float SameShotSeconds = 0.05f;

        private sealed class PeerMark
        {
            public Unit? Target; public int TargetIdx = -1; public float PlacedAt; public float ExpiresAt;
            public Unit? Pending; public int PendingHits; public float LastCountedAt = -10f;
            public Unit? Linger; public int LingerIdx; public float LingerUntil;
        }

        private static readonly Dictionary<string, PeerMark> _byPeer = new Dictionary<string, PeerMark>();
        private static readonly List<string> _idlePeers = new List<string>();
        private static readonly Dictionary<int, int> _spawnIdxByInstance = new Dictionary<int, int>(); // -1 = not eligible
        private static string _scope = "";
        private static string _lastSentSignature = "";
        private static float _nextResendAt;
        private static int _revision;

        // client view: host spawn index → the peers marking it
        private static readonly Dictionary<int, List<string>> _clientView = new Dictionary<int, List<string>>();
        private static int _clientRevision = -1;

        public static int HostHitsBoosted, HostMarksPlaced, HostMarksMoved, MessagesSent, MessagesApplied;

        // ----------------------------------------------------------------- lifecycle

        public static void Reset()
        {
            _byPeer.Clear();
            _spawnIdxByInstance.Clear();
            _clientView.Clear();
            _clientRevision = -1;
            _scope = "";
            _lastSentSignature = "";
        }

        public static void Tick()
        {
            try
            {
                if (!NetGameplaySyncBridge.IsSessionActive) { if (_byPeer.Count > 0 || _clientView.Count > 0) Reset(); return; }
                TeamMarkPresenter.EnsureCreated();
                if (!RollScope()) return;
                if (!NetGameplaySyncBridge.IsHost) return;
                if (!NetSessionSettings.TeamMarksEnabled && _byPeer.Count > 0) _byPeer.Clear(); // switched off mid-level

                float now = Time.time;
                foreach (var kv in _byPeer)
                {
                    var m = kv.Value;
                    if (m.Target != null && (now >= m.ExpiresAt || !IsAlive(m.Target) || IsPeerDown(kv.Key)))
                    {
                        // Runs out, enemy dead or player downed: the enemy keeps it briefly unless it died.
                        if (IsAlive(m.Target) && !IsPeerDown(kv.Key)) { m.Linger = m.Target; m.LingerIdx = m.TargetIdx; m.LingerUntil = now + LingerSeconds; }
                        NetLogger.Info($"[TeamMark] {kv.Key} mark ended on idx={m.TargetIdx}");
                        m.Target = null; m.TargetIdx = -1; m.Pending = null; m.PendingHits = 0;
                    }
                    if (m.Linger != null && (now >= m.LingerUntil || !IsAlive(m.Linger) || IsPeerDown(kv.Key))) m.Linger = null;
                    if (m.Target == null && m.Linger == null) _idlePeers.Add(kv.Key);
                }
                if (_idlePeers.Count > 0) { foreach (var id in _idlePeers) _byPeer.Remove(id); _idlePeers.Clear(); }

                // Nothing held and nothing on the clients' screens: no work until the periodic resend.
                if (_byPeer.Count == 0 && _lastSentSignature.Length == 0 && Time.realtimeSinceStartup < _nextResendAt) return;
                string sig = Signature(now);
                if (sig != _lastSentSignature || Time.realtimeSinceStartup >= _nextResendAt) Send(sig, now);
            }
            catch (Exception ex) { NetLogger.Warn($"[TeamMark] Tick failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>Drop everything when the local run is on another level. False while there is no level.</summary>
        private static bool RollScope()
        {
            if (!NetRunStateBridge.TryGetLocalRunState(out var run) || run == null || !run.HasLevel) return false;
            string scope = $"{run.ChapterName}:{run.LevelIndex}:{(run.HasLevelSeed ? run.LevelSeed.ToString() : "?")}";
            if (scope == _scope) return true;
            _byPeer.Clear(); _spawnIdxByInstance.Clear(); _clientView.Clear(); _clientRevision = -1;
            _lastSentSignature = ""; _nextResendAt = 0f;
            _scope = scope;
            return true;
        }

        // ----------------------------------------------------------------- host: hits

        /// <summary>The damage multiplier for a direct hit on an enemy carrying <paramref name="otherMarks"/> marks of
        /// players other than the attacker.</summary>
        public static float MultiplierFor(int otherMarks)
            => otherMarks <= 0 ? 1f : otherMarks == 1 ? OneMarkMultiplier : TwoMarkMultiplier;

        /// <summary>HOST: the multiplier a direct hit by <paramref name="attackerPeer"/> on <paramref name="target"/> gets
        /// from the other players' marks. 1 = none.</summary>
        public static float GetMultiplier(Unit target, string attackerPeer)
            => MultiplierFor(HostCountOtherMarks(target, attackerPeer));

        private static int HostCountOtherMarks(Unit target, string attackerPeer)
        {
            if (target == null || _byPeer.Count == 0 || !NetSessionSettings.TeamMarksEnabled) return 0;
            float now = Time.time;
            int others = 0;
            foreach (var kv in _byPeer)
            {
                if (string.Equals(kv.Key, attackerPeer, StringComparison.Ordinal)) continue;
                if (Marks(kv.Value, target, now)) others++;
            }
            return others;
        }

        /// <summary>HOST: a direct gun or melee hit by <paramref name="peerId"/> landed on a live <paramref name="target"/>.</summary>
        public static void OnDirectHit(string peerId, Unit target)
        {
            if (string.IsNullOrEmpty(peerId) || target == null || !NetSessionSettings.TeamMarksEnabled || IsPeerDown(peerId)) return;
            if (!TryGetEligibleIndex(target, out int idx)) return;
            float now = Time.time;
            if (!_byPeer.TryGetValue(peerId, out var m)) _byPeer[peerId] = m = new PeerMark();

            if (m.Target == target) { m.ExpiresAt = now + MarkSeconds; return; }

            if (m.Target == null)
            {
                Place(m, target, idx, now);
                HostMarksPlaced++;
                NetLogger.Info($"[TeamMark] {peerId} placed mark on idx={idx} {target.name}");
                return;
            }

            if (m.Pending != target) { m.Pending = target; m.PendingHits = 1; m.LastCountedAt = now; }
            else if (now - m.LastCountedAt >= SameShotSeconds) { m.PendingHits++; m.LastCountedAt = now; }
            if (m.PendingHits >= MoveHits && now - m.PlacedAt >= MoveCooldownSeconds)
            {
                m.Linger = m.Target; m.LingerIdx = m.TargetIdx; m.LingerUntil = now + LingerSeconds;
                int from = m.TargetIdx;
                Place(m, target, idx, now);
                HostMarksMoved++;
                NetLogger.Info($"[TeamMark] {peerId} moved mark idx={from} -> idx={idx} {target.name}");
            }
        }

        private static void Place(PeerMark m, Unit target, int idx, float now)
        {
            m.Target = target; m.TargetIdx = idx; m.PlacedAt = now; m.ExpiresAt = now + MarkSeconds;
            m.Pending = null; m.PendingHits = 0; m.LastCountedAt = -10f;
            if (m.Linger == target) m.Linger = null;
        }

        // A dead enemy carries no mark, whatever the table still holds until the next Tick.
        private static bool Marks(PeerMark m, Unit target, float now)
            => IsAlive(target) && ((m.Target == target && now < m.ExpiresAt) || (m.Linger == target && now < m.LingerUntil));

        /// <summary>HOST: an ordinary roster enemy that may carry a mark, and its host spawn index.</summary>
        public static bool TryGetEligibleIndex(Unit unit, out int idx)
        {
            idx = -1;
            if (!(unit is Npc) || unit == null || !unit.IsAlive) return false;
            int key = unit.GetInstanceID();
            if (_spawnIdxByInstance.TryGetValue(key, out idx)) return idx > 0;
            idx = -1;
            var so = unit.unitSO;
            bool eligible = so != null && (so.unitType & UnitType.Boss) == 0
                            && !Boss.BossDynamicSpawnManifest.IsBossAdd(unit)
                            && NetGameplayProbeManager.TryGetHostRosterIdentity(unit, out idx, out _) && idx > 0;
            if (!eligible) idx = -1;
            // A unit that is not in the roster yet may be added later; only remember a definite answer.
            if (eligible || so == null || (so.unitType & UnitType.Boss) != 0 || Boss.BossDynamicSpawnManifest.IsBossAdd(unit))
                _spawnIdxByInstance[key] = idx;
            return eligible;
        }

        // ----------------------------------------------------------------- host: sync

        private static string Signature(float now)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var kv in _byPeer)
            {
                var m = kv.Value;
                if (m.Target != null && now < m.ExpiresAt) sb.Append(kv.Key).Append('>').Append(m.TargetIdx).Append(';');
                if (m.Linger != null && now < m.LingerUntil && m.LingerIdx != m.TargetIdx) sb.Append(kv.Key).Append('~').Append(m.LingerIdx).Append(';');
            }
            return sb.ToString();
        }

        private static void Send(string sig, float now)
        {
            if (!NetRunStateBridge.TryGetLocalRunState(out var run) || run == null) return;
            var msg = new NetHostTeamMarks
            {
                ChapterName = run.ChapterName, LevelIndex = run.LevelIndex, HasLevelSeed = run.HasLevelSeed, LevelSeed = run.LevelSeed,
                Revision = ++_revision,
            };
            foreach (var kv in _byPeer)
            {
                var m = kv.Value;
                if (m.Target != null && now < m.ExpiresAt) msg.Entries.Add(new NetHostTeamMarks.Entry { PeerId = kv.Key, HostSpawnIndex = m.TargetIdx });
                if (m.Linger != null && now < m.LingerUntil && m.LingerIdx != m.TargetIdx) msg.Entries.Add(new NetHostTeamMarks.Entry { PeerId = kv.Key, HostSpawnIndex = m.LingerIdx });
            }
            _lastSentSignature = sig;
            _nextResendAt = Time.realtimeSinceStartup + ResendSeconds;
            MessagesSent++;
            NetGameplaySyncBridge.BroadcastHostTeamMarks(msg);
        }

        // ----------------------------------------------------------------- client

        public static void Process(NetHostTeamMarks msg)
        {
            try
            {
                if (!NetRunStateBridge.TryGetLocalRunState(out var local) || local == null || !msg.MatchesScene(local)) return;
                RollScope();
                if (msg.Revision <= _clientRevision) return; // reliable-ordered, but a resend after a gap can only be newer
                _clientRevision = msg.Revision;
                _clientView.Clear();
                foreach (var e in msg.Entries)
                {
                    if (!_clientView.TryGetValue(e.HostSpawnIndex, out var peers)) _clientView[e.HostSpawnIndex] = peers = new List<string>(2);
                    if (!peers.Contains(e.PeerId)) peers.Add(e.PeerId);
                }
                MessagesApplied++;
            }
            catch (Exception ex) { NetLogger.Warn($"[TeamMark] Process failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        // ----------------------------------------------------------------- both ends: what to show

        public struct Shown { public Unit Unit; public int OtherMarks; }

        /// <summary>Every enemy carrying at least one mark that is not the local player's, with how many such marks.</summary>
        public static void CollectShown(List<Shown> into)
        {
            into.Clear();
            if (!NetGameplaySyncBridge.IsSessionActive || !NetSessionSettings.TeamMarksEnabled) return;
            string self = NetGameplaySyncBridge.LocalPeerId;
            if (NetGameplaySyncBridge.IsHost)
            {
                if (_byPeer.Count == 0) return;
                float now = Time.time;
                foreach (var kv in _byPeer)
                {
                    if (string.Equals(kv.Key, self, StringComparison.Ordinal)) continue;
                    var m = kv.Value;
                    if (m.Target != null && now < m.ExpiresAt) AddShown(into, m.Target);
                    if (m.Linger != null && now < m.LingerUntil && m.Linger != m.Target) AddShown(into, m.Linger);
                }
                return;
            }
            foreach (var kv in _clientView)
            {
                int others = 0;
                foreach (var p in kv.Value) if (!string.Equals(p, self, StringComparison.Ordinal)) others++;
                if (others == 0) continue;
                if (NetGameplayProbeManager.IsClientDeadOrDying(kv.Key)) continue;
                if (!NetGameplayProbeManager.TryGetClientPuppetNpc(kv.Key, out var npc) || !(npc is Unit u) || u == null) continue;
                into.Add(new Shown { Unit = u, OtherMarks = others });
            }
        }

        private static void AddShown(List<Shown> into, Unit unit)
        {
            if (unit == null || !unit.IsAlive) return;
            for (int i = 0; i < into.Count; i++)
                if (into[i].Unit == unit) { into[i] = new Shown { Unit = unit, OtherMarks = into[i].OtherMarks + 1 }; return; }
            into.Add(new Shown { Unit = unit, OtherMarks = 1 });
        }

        /// <summary>Either end: how many players other than the local one have their mark on <paramref name="unit"/>, as
        /// this end knows it (the host from its table, a client from the host's last message). 0 for anything else.</summary>
        public static int LocalOtherMarkCount(Unit unit)
        {
            if (unit == null || !NetGameplaySyncBridge.IsSessionActive || !NetSessionSettings.TeamMarksEnabled) return 0;
            string self = NetGameplaySyncBridge.LocalPeerId;
            if (NetGameplaySyncBridge.IsHost) return HostCountOtherMarks(unit, self);
            if (_clientView.Count == 0 || !unit.IsAlive) return 0;
            if (!NetGameplayProbeManager.TryGetClientPuppetBinding(unit, out int idx, out _) || !_clientView.TryGetValue(idx, out var peers)) return 0;
            if (NetGameplayProbeManager.IsClientDeadOrDying(idx)) return 0; // as CollectShown: the host reported it dead
            int others = 0;
            foreach (var p in peers) if (!string.Equals(p, self, StringComparison.Ordinal)) others++;
            return others;
        }

        // ----------------------------------------------------------------- helpers

        private static bool IsAlive(Unit u) => u != null && u.IsAlive;

        private static bool IsPeerDown(string peerId)
        {
            if (string.Equals(peerId, NetGameplaySyncBridge.LocalPeerId, StringComparison.Ordinal))
                return NetPlayerLifeManager.IsDownedLocalPlayerUnit(GameManager.Instance?.PlayerUnit);
            return NetPlayerLifeManager.IsPeerDownOrDead(peerId);
        }

        public static string FormatSummary()
            => $"[TeamMark] hostBoosted={HostHitsBoosted} placed={HostMarksPlaced} moved={HostMarksMoved} sent={MessagesSent} applied={MessagesApplied} peers={_byPeer.Count} clientView={_clientView.Count}";
    }
}
