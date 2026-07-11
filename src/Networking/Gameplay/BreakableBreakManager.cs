using System;
using System.Collections.Generic;
using UnityEngine;
using PerfectRandom.Sulfur.Core.Units;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase 5.7-BR in-scene destructible (Breakable) sync — peer-authoritative EFFECT mirror.
    /// <para>Each peer breaks its OWN destructibles for real (its real bullet/physics/explosion → Unit.ReceiveDamage →
    /// Breakable.Die). When that happens we broadcast a break event keyed by the breakable's deterministic spawn
    /// position. Receivers find the matching still-alive local Breakable and call <c>Break()</c>, mirroring the EFFECT
    /// (shatter / sound / onBreakEvents / child cascade / loot / destroy) so the world stays consistent on every screen.</para>
    /// <para>Remote players' replayed visual bullets (WS-1) carry no damage and never break anything themselves; this
    /// channel is what propagates the destruction. Loot stays per-peer (loot is not networked — same as enemy deaths).</para>
    /// </summary>
    internal static class BreakableBreakManager
    {
        // Alive registered breakables → their SPAWN position (recorded at Start, before any physics movement).
        private static readonly Dictionary<Breakable, Vector3> _registry = new Dictionary<Breakable, Vector3>();

        // True while we are applying a mirrored break locally — the Die prefix must NOT re-broadcast it (would echo).
        private static bool _applyingMirror;

        // Breakables we've already broadcast a break for (avoid a second send if Die runs twice).
        private static readonly HashSet<Breakable> _broadcasted = new HashSet<Breakable>();

        private static int _captureSeq;

        // Max distance (m) between an incoming spawn-position key and a local breakable's spawn position to count as a
        // match. Deterministic generation makes these near-identical; the slack absorbs float drift / minor divergence.
        private const float MatchEpsilon = 0.75f;

        public static bool IsApplyingMirror => _applyingMirror;

        // ----------------------------------------------------------------- registry (Breakable.Start postfix)

        public static void Register(Breakable b)
        {
            if (b == null) return;
            try { _registry[b] = b.transform.position; }
            catch { }
        }

        // Called from the Die prefix (real OR mirrored break): the breakable is dying, so drop it from the live registry
        // so it can't be matched again. We deliberately KEEP its _broadcasted mark (cleared only on scene Clear) so a
        // second Die on the same instance never re-broadcasts.
        public static void Unregister(Breakable b)
        {
            if (b == null) return;
            try { _registry.Remove(b); }
            catch { }
        }

        // ----------------------------------------------------------------- capture (Breakable.Die prefix, real break)

        public static void CaptureLocalBreak(Breakable b)
        {
            try
            {
                if (_applyingMirror) return;                                // mirrored break — never re-broadcast
                if (ThrowableEffectManager.IsApplyingMirror) return;       // HZ-2: a mirrored throwable's spawn+break — not a real in-scene break
                if (!Plugin.Cfg.EnableBreakableSync.Value) return;
                if (!NetGameplaySyncBridge.IsSessionActive) return;        // skip all work in solo play
                if (b == null) return;
                if (!_broadcasted.Add(b)) return;                          // already broadcast this breakable

                Vector3 key = ResolveSpawnPos(b);

                var msg = new NetBreakableBreak
                {
                    Sequence = ++_captureSeq,
                    Position = key,
                };

                NetGameplaySyncBridge.ReportLocalBreakableBreak(msg);

                if (Plugin.Cfg.LogBreakableSync.Value)
                    NetLogger.Info($"[BreakableBreak] capture name={b.name} pos={key}");
            }
            catch (Exception ex)
            {
                NetLogger.Warn($"[BreakableBreak] capture failed: {ex.Message}");
            }
        }

        // ----------------------------------------------------------------- mirror (receiving peer)

        public static void ApplyRemoteBreak(NetBreakableBreak m)
        {
            try
            {
                if (!Plugin.Cfg.EnableBreakableSync.Value) return;
                if (m == null) return;

                Breakable target = FindMatch(m.Position);
                if (target == null)
                {
                    if (Plugin.Cfg.LogBreakableSync.Value)
                        NetLogger.Info($"[BreakableBreak] mirror peer={m.PeerId} no alive match near {m.Position}");
                    return;
                }

                // Guard so the resulting Die (and any cascade to child / linked breakables) does NOT re-broadcast.
                _applyingMirror = true;
                try { target.Break(); }
                finally { _applyingMirror = false; }

                if (Plugin.Cfg.LogBreakableSync.Value)
                    NetLogger.Info($"[BreakableBreak] mirror peer={m.PeerId} broke name={target.name} near {m.Position}");
            }
            catch (Exception ex)
            {
                NetLogger.Warn($"[BreakableBreak] mirror failed: {ex.Message}");
            }
        }

        // ----------------------------------------------------------------- helpers

        private static Vector3 ResolveSpawnPos(Breakable b)
        {
            if (_registry.TryGetValue(b, out var pos)) return pos;
            try { return b.transform.position; } catch { return Vector3.zero; }
        }

        /// <summary>Nearest still-alive registered breakable within <see cref="MatchEpsilon"/> of the incoming key.</summary>
        private static Breakable FindMatch(Vector3 key)
        {
            Breakable best = null;
            float bestSqr = MatchEpsilon * MatchEpsilon;
            List<Breakable> dead = null;

            foreach (var kv in _registry)
            {
                Breakable b = kv.Key;
                if (b == null)                                  // destroyed by other means — schedule cleanup
                {
                    (dead ??= new List<Breakable>()).Add(b);
                    continue;
                }
                if (b.UnitState == UnitState.Dead) continue;

                float sqr = (kv.Value - key).sqrMagnitude;
                if (sqr <= bestSqr)
                {
                    bestSqr = sqr;
                    best = b;
                }
            }

            if (dead != null)
                foreach (var d in dead) _registry.Remove(d);

            return best;
        }

        // Scene change — drop stale registrations from the previous level.
        public static void Clear()
        {
            _registry.Clear();
            _broadcasted.Clear();
        }
    }
}
