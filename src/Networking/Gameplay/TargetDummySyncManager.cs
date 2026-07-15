using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using PerfectRandom.Sulfur.Core.Units;
using PerfectRandom.Sulfur.Core.Stats;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// TD-1: shared damage-number display for the 0.18 unlockable target dummy (<c>DamageTracker</c>, in the Gameplay
    /// assembly). The dummy already flashes white on a local hit; this shares the NUMBERS so every player who has it
    /// unlocked sees one combined foot total and the same flying head numbers, no matter who is shooting.
    ///
    /// <para><b>Model</b> — peer-broadcast, each peer accumulates locally (eventually-consistent, no host authority over
    /// a non-puppet object). The dummy is damaged locally by every shooter (it is not a host-roster puppet, so a client's
    /// hit applies locally → the vanilla <c>onDamageRecieved → DamageTracker.ShowDamage</c> runs and the shooter sees full
    /// per-hit numbers). We capture that hit, broadcast it, and every OTHER peer replays the EFFECT via the same private
    /// <c>ShowDamage</c> (flying number + <c>AddToTotal</c>) — never re-applying real damage. Each peer's foot total is
    /// therefore its own local hits + everyone else's relayed hits = the same exact shared sum on all screens
    /// (ReliableOrdered delivery; the source peer never processes its own echo).</para>
    ///
    /// <para><b>Coalescing</b> (high fire-rate) — a shooter accumulates its hits per dummy over a short window and sends
    /// ONE message carrying the summed damage. The total stays exact; the message rate and the remote flying-number count
    /// stay bounded. The local shooter is untouched (full per-hit fidelity, vanilla).</para>
    ///
    /// <para><b>Unlock gating</b> — the dummy's subtree is <c>SetActive(false)</c> until the player unlocks it
    /// (<c>UnlockableStorage</c> / per-player church progress), so a locked peer's <c>DamageTracker.Start</c> never runs
    /// and it registers no tracker. A relayed hit that finds no local tracker is silently ignored — numbers are never
    /// shown to, and the dummy is never revealed for, a player who hasn't unlocked it.</para>
    ///
    /// <para>Reflection-only: <c>DamageTracker</c> lives in the Gameplay assembly the mod doesn't reference; the damage
    /// payload types (<c>DamageSourceData</c> / <c>DamageTypes</c>) are Core, which it does.</para>
    /// </summary>
    internal static class TargetDummySyncManager
    {
        // Live DamageTrackers (unlocked/active on this peer), keyed for relay lookup by authored world position.
        private static readonly Dictionary<Component, Vector3> _registry = new Dictionary<Component, Vector3>();

        // Per-dummy pending sender batch (coalesces rapid local hits before one broadcast).
        private sealed class Batch
        {
            public int     Sum;
            public byte    DamageType;
            public bool    AnyCrit;
            public Vector3 HitPoint;
            public float   FirstHitTime;
        }
        private static readonly Dictionary<Component, Batch> _pending = new Dictionary<Component, Batch>();
        private static readonly List<Component> _flushScratch = new List<Component>();

        private static bool _applyingMirror; // true while WE replay a relayed hit — the ShowDamage postfix must not re-capture
        private static int  _seq;

        private const float MatchEpsilon = 1.0f;
        private const float CoalesceWindow = 0.075f; // 75 ms: a burst inside this window ships as one summed number

        private static MethodInfo? _showDamage;
        private static bool _showDamageResolved;

        private static bool Active
        {
            get { try { return Plugin.Cfg.EnableTargetDummySync.Value && NetGameplaySyncBridge.IsSessionActive; } catch { return false; } }
        }
        private static bool LogOn
        {
            get { try { return Plugin.Cfg.LogTargetDummySync.Value; } catch { return false; } }
        }

        // ----------------------------------------------------------------- registry (DamageTracker.Start postfix)

        public static void Register(Component? tracker)
        {
            if (tracker == null) return;
            try { _registry[tracker] = tracker.transform.position; } catch { }
        }

        // ----------------------------------------------------------------- capture (DamageTracker.ShowDamage postfix)

        /// <summary>A real local hit was shown on our dummy — accumulate it for the next coalesced broadcast. The
        /// shooter's own display already happened (vanilla); this only feeds the network + remote peers.</summary>
        public static void OnLocalHit(Component? tracker, float damage, byte damageType, bool crit, Vector3? hitPoint)
        {
            try
            {
                if (_applyingMirror) return;   // our own relayed replay — never re-broadcast
                if (!Active) return;
                if (tracker == null || damage <= 0f) return;

                if (!_registry.TryGetValue(tracker, out Vector3 key)) { key = tracker.transform.position; _registry[tracker] = key; }

                if (!_pending.TryGetValue(tracker, out var b))
                {
                    b = new Batch { FirstHitTime = Time.time };
                    _pending[tracker] = b;
                }
                b.Sum += Mathf.Max(0, (int)damage);
                b.DamageType = damageType;
                b.AnyCrit |= crit;
                if (hitPoint.HasValue) b.HitPoint = hitPoint.Value;
            }
            catch (Exception ex) { NetLogger.Warn($"[TargetDummy] capture failed: {ex.Message}"); }
        }

        /// <summary>Main-thread flush (from NetService.Tick): ship each dummy's accumulated batch once its window has
        /// elapsed, as one summed message. Bounds the broadcast rate under high fire-rate.</summary>
        public static void Tick()
        {
            if (_pending.Count == 0) return;
            if (!Active) { _pending.Clear(); return; }

            float now = Time.time;
            _flushScratch.Clear();
            foreach (var kv in _pending)
                if (now - kv.Value.FirstHitTime >= CoalesceWindow) _flushScratch.Add(kv.Key);

            foreach (var tracker in _flushScratch)
            {
                var b = _pending[tracker];
                _pending.Remove(tracker);
                if (tracker == null || b.Sum <= 0) continue;

                Vector3 key = _registry.TryGetValue(tracker, out var k) ? k : tracker.transform.position;
                var msg = new NetTargetDummyDamage
                {
                    Sequence   = ++_seq,
                    Position   = key,
                    Amount     = b.Sum,
                    DamageType = b.DamageType,
                    IsCritical = b.AnyCrit,
                    HitPoint   = b.HitPoint,
                };
                NetGameplaySyncBridge.ReportLocalTargetDummyDamage(msg);
                if (LogOn) NetLogger.Info($"[TargetDummy] broadcast pos={key:F1} sum={b.Sum} type={b.DamageType} crit={b.AnyCrit}");
            }
        }

        // ----------------------------------------------------------------- relay apply (any peer)

        /// <summary>Replay a relayed hit's EFFECT on our matching local dummy — the flying head number + foot total —
        /// without touching the dummy's real health. No local tracker (dummy locked / different scene) → ignore.</summary>
        public static void ApplyRemote(NetTargetDummyDamage m)
        {
            try
            {
                if (!Plugin.Cfg.EnableTargetDummySync.Value || m == null) return;
                Component? tracker = FindMatch(m.Position);
                if (tracker == null)
                {
                    if (LogOn) NetLogger.Info($"[TargetDummy] relay peer={m.PeerId} no local dummy near {m.Position:F1} (locked / absent)");
                    return;
                }

                if (!ResolveShowDamage()) return;

                var sd = default(DamageSourceData);
                sd.damageType = (DamageTypes)m.DamageType;
                sd.isCritical = m.IsCritical;

                _applyingMirror = true;
                try { _showDamage!.Invoke(tracker, new object[] { null!, (float)m.Amount, sd, (Vector3?)m.HitPoint }); }
                finally { _applyingMirror = false; }

                if (LogOn) NetLogger.Info($"[TargetDummy] relay peer={m.PeerId} applied sum={m.Amount} near {m.Position:F1}");
            }
            catch (Exception ex) { NetLogger.Warn($"[TargetDummy] relay apply failed: {ex.Message}"); }
        }

        // ----------------------------------------------------------------- helpers

        private static bool ResolveShowDamage()
        {
            if (!_showDamageResolved)
            {
                _showDamageResolved = true;
                var t = AccessTools.TypeByName("PerfectRandom.Sulfur.Gameplay.DamageTracker");
                _showDamage = t != null ? AccessTools.Method(t, "ShowDamage") : null;
                if (_showDamage == null)
                    NetLogger.Warn("[TargetDummy] DamageTracker.ShowDamage not found — relayed numbers disabled.");
            }
            return _showDamage != null;
        }

        private static Component? FindMatch(Vector3 key)
        {
            Component? best = null;
            float bestSqr = MatchEpsilon * MatchEpsilon;
            List<Component>? dead = null;
            foreach (var kv in _registry)
            {
                Component c = kv.Key;
                if (c == null) { (dead ??= new List<Component>()).Add(kv.Key); continue; }
                float sqr = (kv.Value - key).sqrMagnitude;
                if (sqr <= bestSqr) { bestSqr = sqr; best = c; }
            }
            if (dead != null) foreach (var x in dead) { _registry.Remove(x); _pending.Remove(x); }
            return best;
        }

        public static void Clear()
        {
            _registry.Clear();
            _pending.Clear();
        }
    }
}
