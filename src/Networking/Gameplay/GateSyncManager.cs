using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase LD-1 generic combat-room gate (MetalGate) sync — peer-authoritative EFFECT mirror.
    /// <para>SULFUR seals combat rooms (boss arenas AND ordinary elite rooms) with a <c>MetalGate</c> closed by a
    /// <c>PlayerTrigger</c> the player crosses on entry. Gates are per-end independent (each end's local trigger only
    /// closes its own gate), so an out-of-room / AFK player's gate is left open. This channel mirrors every gate
    /// open/close to all ends, keyed by the gate's deterministic world position.</para>
    /// <para><c>MetalGate</c> lives in the Gameplay assembly (not referenced), so it is handled by reflection — the gate
    /// is held as a <see cref="Component"/> and <c>Close()</c>/<c>Open()</c> are invoked via cached <see cref="MethodInfo"/>.
    /// Reproducing the real method reproduces whatever that gate does (animation + optional collider/navmesh).</para>
    /// <para>Foundation for the FF14 arena lockdown (LD-2 layers host-authoritative seal of out-of-room players + the
    /// confirm popup + teleport on top of this).</para>
    /// </summary>
    internal static class GateSyncManager
    {
        // Registered live gates → their world position (gates are static, so position is stable).
        private static readonly Dictionary<Component, Vector3> _registry = new Dictionary<Component, Vector3>();
        // Last state we broadcast per gate (skip redundant re-sends of the same state, e.g. OnEnable re-close).
        private static readonly Dictionary<Component, bool> _lastState = new Dictionary<Component, bool>();

        // True while applying a mirrored open/close locally — the Close/Open postfix must NOT re-broadcast it.
        private static bool _applyingMirror;

        private static int _captureSeq;

        // Max distance (m) between an incoming position key and a local gate to count as a match. Deterministic
        // generation makes these identical; the slack absorbs float drift.
        private const float MatchEpsilon = 1.0f;

        // Reflection: MetalGate.Close() / Open() (Gameplay assembly, not referenced).
        private static MethodInfo? _closeMethod;
        private static MethodInfo? _openMethod;
        private static bool _methodsResolved;

        public static bool IsApplyingMirror => _applyingMirror;

        private static bool Enabled
        {
            get { try { return Plugin.Cfg.EnableGateSync.Value; } catch { return false; } }
        }
        private static bool LogOn
        {
            get { try { return Plugin.Cfg.LogGateSync.Value; } catch { return false; } }
        }

        // ----------------------------------------------------------------- registry (MetalGate.Awake postfix)

        public static void Register(object gate)
        {
            if (!(gate is Component c) || c == null) return;
            try { _registry[c] = c.transform.position; }
            catch { }
        }

        // ----------------------------------------------------------------- capture (MetalGate.Close/Open postfix)

        public static void CaptureLocalGate(object gate, bool closed)
        {
            try
            {
                if (_applyingMirror) return;                          // mirrored change — never re-broadcast
                if (!Enabled) return;
                if (!NetGameplaySyncBridge.IsSessionActive) return;  // skip all work in solo play
                if (!(gate is Component c) || c == null) return;

                // Skip redundant re-sends of the same state (e.g. OnEnable→Close init, or a double Close).
                if (_lastState.TryGetValue(c, out bool prev) && prev == closed) return;
                _lastState[c] = closed;

                Vector3 key = ResolvePos(c);

                NetGameplaySyncBridge.ReportLocalGateState(new NetGateState
                {
                    Sequence = ++_captureSeq,
                    Position = key,
                    Closed   = closed,
                });

                if (LogOn) NetLogger.Info($"[GateSync] capture name={c.name} {(closed ? "CLOSE" : "OPEN")} pos={key}");
            }
            catch (Exception ex) { NetLogger.Warn($"[GateSync] capture failed: {ex.Message}"); }
        }

        // ----------------------------------------------------------------- mirror (receiving peer)

        public static void ApplyRemoteGate(NetGateState m)
        {
            try
            {
                if (!Enabled || m == null) return;
                if (!ResolveMethods()) { if (LogOn) NetLogger.Info("[GateSync] MetalGate methods unresolved"); return; }

                Component target = FindMatch(m.Position);
                if (target == null)
                {
                    if (LogOn) NetLogger.Info($"[GateSync] mirror peer={m.PeerId} no gate near {m.Position}");
                    return;
                }

                var method = m.Closed ? _closeMethod : _openMethod;
                if (method == null) return;

                // Guard so the resulting Close/Open postfix does NOT re-broadcast (would echo).
                _applyingMirror = true;
                try { method.Invoke(target, null); }
                finally { _applyingMirror = false; }

                // Record the applied state so a subsequent local same-state change isn't redundantly re-broadcast.
                _lastState[target] = m.Closed;

                if (LogOn) NetLogger.Info($"[GateSync] mirror peer={m.PeerId} {(m.Closed ? "CLOSE" : "OPEN")} name={target.name} near {m.Position}");
            }
            catch (Exception ex) { NetLogger.Warn($"[GateSync] mirror failed: {ex.Message}"); }
        }

        // ----------------------------------------------------------------- helpers

        private static Vector3 ResolvePos(Component c)
        {
            if (_registry.TryGetValue(c, out var pos)) return pos;
            try { var p = c.transform.position; _registry[c] = p; return p; } catch { return Vector3.zero; }
        }

        /// <summary>Nearest live registered gate within <see cref="MatchEpsilon"/> of the incoming key.</summary>
        private static Component FindMatch(Vector3 key)
        {
            Component best = null;
            float bestSqr = MatchEpsilon * MatchEpsilon;
            List<Component> dead = null;

            foreach (var kv in _registry)
            {
                Component c = kv.Key;
                if (c == null) { (dead ??= new List<Component>()).Add(c); continue; }
                float sqr = (kv.Value - key).sqrMagnitude;
                if (sqr <= bestSqr) { bestSqr = sqr; best = c; }
            }

            if (dead != null) foreach (var d in dead) { _registry.Remove(d); _lastState.Remove(d); }
            return best;
        }

        private static bool ResolveMethods()
        {
            if (_methodsResolved) return _closeMethod != null && _openMethod != null;
            _methodsResolved = true;
            try
            {
                var t = HarmonyLib.AccessTools.TypeByName("PerfectRandom.Sulfur.Gameplay.Mechanisms.MetalGate.MetalGate");
                if (t != null)
                {
                    _closeMethod = HarmonyLib.AccessTools.Method(t, "Close", Type.EmptyTypes);
                    _openMethod  = HarmonyLib.AccessTools.Method(t, "Open",  Type.EmptyTypes);
                }
            }
            catch { }
            return _closeMethod != null && _openMethod != null;
        }

        // Scene change — drop stale gates from the previous level.
        public static void Clear()
        {
            _registry.Clear();
            _lastState.Clear();
        }
    }
}
