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

                // LD-2c: a gate re-opening (AllDeadTrigger = all enemies dead / boss died) is the host's "fight over"
                // signal — release any arena lockdown near it (let still-out-of-room players in, drop the barrier).
                if (!closed) ArenaLockdownManager.OnGateOpened(key);
            }
            catch (Exception ex) { NetLogger.Warn($"[GateSync] capture failed: {ex.Message}"); }
        }

        // ----------------------------------------------------------------- mirror (receiving peer)

        public static void ApplyRemoteGate(NetGateState m)
        {
            try
            {
                if (!Enabled || m == null) return;
                if (m.Kind == 1) { ApplyRemoteRoomTrigger(m); return; } // TB-INTRO: a room-event trigger fire, not a gate
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

                // LD-2c: an in-room client's gate re-opening is mirrored here on the host — same "fight over" release
                // signal as a local open (covers the case where the host is the out-of-room one).
                if (!m.Closed) ArenaLockdownManager.OnGateOpened(m.Position);
            }
            catch (Exception ex) { NetLogger.Warn($"[GateSync] mirror failed: {ex.Message}"); }
        }

        /// <summary>LD-2d: close a SPECIFIC MetalGate instance for real (used when the grace period ends — the gate was
        /// kept open during grace and is now closed). The gate object is resolved directly from the seal trigger's event
        /// (robust, unlike a registry/position lookup). Real <c>Close()</c>, so its postfix captures + syncs as normal.</summary>
        public static bool CloseGate(object gate)
        {
            try
            {
                if (gate == null || !ResolveMethods()) return false;
                _closeMethod.Invoke(gate, null);
                return true;
            }
            catch (Exception ex) { NetLogger.Warn($"[GateSync] CloseGate failed: {ex.Message}"); return false; }
        }

        // ================================================================= TB-INTRO: room-event trigger mirror
        // The boss's ENTRANCE (Terrorbaum bursting out of the soil) is played by the room-entry PlayerTrigger's
        // persistent Animator.SetTrigger — fired only by the LOCAL player crossing it, so a far-away player's boss
        // never "appears" even though its mechanics run (Log360: renderers fine, animator stuck pre-appear). Mirror the
        // FIRST fire to every end (everyone sees the entrance together, Cousin-style), invoking the receiver's own
        // local PlayerTrigger — which also consumes its hasBeenTriggered, so a player walking in later doesn't replay
        // the entrance mid-fight. A trigger qualifies when its persistent calls include an Animator."SetTrigger".

        private static bool _applyingTriggerMirror;
        public static bool IsApplyingTriggerMirror => _applyingTriggerMirror;

        private static Type _playerTriggerType;
        private static MethodInfo _triggerMethod;
        private static FieldInfo _hasBeenTriggeredField;
        private static FieldInfo _onTriggerEventsField;

        /// <summary>Local postfix feed (any end): a PlayerTrigger's Trigger() just ran. Broadcast it iff it REALLY fired
        /// now (pre-state false → true; refires early-return inside the native method) and it is an appear-class trigger.
        /// The mirror's own invoke is guarded out.</summary>
        public static void OnLocalPlayerTriggerFired(object playerTrigger, bool wasTriggeredBefore)
        {
            try
            {
                if (_applyingTriggerMirror) return; // this IS a mirror apply — never echo it back
                if (!Enabled || !NetGameplaySyncBridge.IsSessionActive) return;
                if (!(playerTrigger is Component c) || c == null) return;
                if (!EnsureTriggerReflect(c)) return;
                bool firedNow = _hasBeenTriggeredField != null && _hasBeenTriggeredField.GetValue(playerTrigger) is bool b && b;
                if (wasTriggeredBefore || !firedNow) return;       // not a fresh fire
                if (!HasAppearAnimatorPersistent(playerTrigger)) return; // only the appear-class room triggers

                Vector3 key = c.transform.position;
                NetGameplaySyncBridge.ReportLocalGateState(new NetGateState
                {
                    Sequence = ++_captureSeq,
                    Position = key,
                    Kind     = 1,
                });
                NetLogger.Info($"[BossIntroSync] capture room-event trigger name={c.name} pos={key:F1}");
            }
            catch (Exception ex) { NetLogger.Warn($"[BossIntroSync] capture failed: {ex.Message}"); }
        }

        /// <summary>Receiver: invoke OUR local PlayerTrigger at the same position (full native effect: gate closes are
        /// idempotent, the Animator.SetTrigger plays the boss entrance, hasBeenTriggered is consumed so a later local
        /// crossing is a no-op). The native (hasBeenTriggered &amp;&amp; onlyOnce) guard makes a double-apply harmless.</summary>
        private static void ApplyRemoteRoomTrigger(NetGateState m)
        {
            try
            {
                var target = FindLocalPlayerTrigger(m.Position, out string detail);
                if (target == null)
                {
                    NetLogger.Warn($"[BossIntroSync] mirror peer={m.PeerId} no PlayerTrigger near {m.Position:F1} ({detail})");
                    return;
                }
                if (!EnsureTriggerReflect(target) || _triggerMethod == null) return;
                if (_hasBeenTriggeredField?.GetValue(target) is bool already && already)
                {
                    if (LogOn) NetLogger.Info($"[BossIntroSync] mirror peer={m.PeerId} already fired locally name={target.name}");
                    return;
                }
                GameObject invoker = ResolveLocalPlayerGo() ?? target.gameObject; // Trigger() reads a Unit off it; player GO is the faithful arg
                _applyingTriggerMirror = true;
                try { _triggerMethod.Invoke(target, new object[] { invoker }); }
                finally { _applyingTriggerMirror = false; }
                NetLogger.Info($"[BossIntroSync] mirror peer={m.PeerId} fired local room-event trigger name={target.name} pos={m.Position:F1}");
            }
            catch (Exception ex) { NetLogger.Warn($"[BossIntroSync] mirror failed: {ex.Message}"); }
        }

        /// <summary>Does this PlayerTrigger's persistent UnityEvent include an Animator."SetTrigger" call — the marker of
        /// a room-entry BOSS-APPEAR trigger (Terrorbaum: [MetalGate.Close, MetalGate.Close, Animator.SetTrigger])?</summary>
        internal static bool HasAppearAnimatorPersistent(object playerTrigger)
        {
            try
            {
                if (playerTrigger == null || !EnsureTriggerReflect(playerTrigger as Component)) return false;
                if (!(_onTriggerEventsField?.GetValue(playerTrigger) is UnityEngine.Events.UnityEventBase evt)) return false;
                int n = evt.GetPersistentEventCount();
                for (int i = 0; i < n; i++)
                {
                    if (evt.GetPersistentTarget(i) is Animator && evt.GetPersistentMethodName(i) == "SetTrigger")
                        return true;
                }
                return false;
            }
            catch { return false; }
        }

        internal static bool TryReadHasBeenTriggered(object playerTrigger, out bool fired)
        {
            fired = false;
            try
            {
                if (!EnsureTriggerReflect(playerTrigger as Component) || _hasBeenTriggeredField == null) return false;
                fired = _hasBeenTriggeredField.GetValue(playerTrigger) is bool b && b;
                return true;
            }
            catch { return false; }
        }

        private static bool EnsureTriggerReflect(Component sample)
        {
            try
            {
                if (_playerTriggerType == null)
                {
                    _playerTriggerType = sample != null && sample.GetType().Name == "PlayerTrigger"
                        ? sample.GetType()
                        : HarmonyLib.AccessTools.TypeByName("PerfectRandom.Sulfur.Core.World.PlayerTrigger")
                          ?? HarmonyLib.AccessTools.TypeByName("PlayerTrigger");
                    if (_playerTriggerType != null)
                    {
                        const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                        _triggerMethod = _playerTriggerType.GetMethod("Trigger", BF, null, new[] { typeof(GameObject) }, null);
                        _hasBeenTriggeredField = _playerTriggerType.GetField("hasBeenTriggered", BF);
                        _onTriggerEventsField = _playerTriggerType.GetField("onTriggerEvents", BF);
                    }
                }
                return _playerTriggerType != null;
            }
            catch { return false; }
        }

        private static Component FindLocalPlayerTrigger(Vector3 key, out string detail)
        {
            detail = "";
            try
            {
                if (!EnsureTriggerReflect(null)) { detail = "PlayerTrigger type unresolved"; return null; }
                Component best = null; float bestSqr = MatchEpsilon * MatchEpsilon;
                foreach (var obj in UnityEngine.Object.FindObjectsOfType(_playerTriggerType))
                {
                    if (!(obj is Component c) || c == null) continue;
                    float sqr = (c.transform.position - key).sqrMagnitude;
                    if (sqr <= bestSqr) { bestSqr = sqr; best = c; }
                }
                if (best == null) detail = "no match within epsilon";
                return best;
            }
            catch (Exception ex) { detail = ex.GetType().Name; return null; }
        }

        private static GameObject ResolveLocalPlayerGo()
        {
            try { return (Boss.BossDamageReflect.ResolveHostPlayerUnit() as Component)?.gameObject; }
            catch { return null; }
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
