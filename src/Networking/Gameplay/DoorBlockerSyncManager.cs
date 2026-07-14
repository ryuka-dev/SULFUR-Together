using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase DB-1 inter-chunk door (<c>DoorBlocker</c>) sync — peer-authoritative EFFECT mirror.
    /// <para>SULFUR 0.18 places hold-to-open doors between chunks. The hold runs entirely off the LOCAL player's
    /// interaction, so a door one player opens stays shut — and physically impassable — on every other end. This
    /// mirrors the open to all ends, keyed by the door's deterministic world position (level gen places doors from
    /// the synced seed, so both ends own the same set at the same positions).</para>
    /// <para>Only doors that START CLOSED are registered (<c>ActivateDoorBlocker(isAnClosingDoor: false)</c> — the
    /// plain openable door and the trap pair's far door). Those wire no <c>DoorBlockerTrigger</c>, so they never
    /// <c>CloseDoor</c> and never run <c>EnemyOpeningDoor</c>: their <c>OnFinishedHolding</c> can only ever come from
    /// a player finishing a hold. The trap doors' slam-shut is deliberately out of scope.</para>
    /// <para>The mirror invokes the door's real <c>OnFinishedHolding</c>, so it reproduces whatever the game does
    /// (lock sound, <c>OpenDoor</c> animation that swings the physical door, LOS collider + navmesh cut removal).
    /// Camera shake and rumble self-gate on the local player's distance to the door, so a far-away peer gets none.</para>
    /// </summary>
    internal static class DoorBlockerSyncManager
    {
        // Registered live openable doors → their world position (level-gen placed; the root never moves).
        private static readonly Dictionary<DoorBlocker, Vector3> _registry = new Dictionary<DoorBlocker, Vector3>();

        // True while applying a mirrored open locally — the OnFinishedHolding postfix must NOT re-broadcast it.
        private static bool _applyingMirror;

        private static int _captureSeq;

        // Max distance (m) between an incoming position key and a local door to count as a match. Deterministic
        // generation makes these identical; the slack absorbs float drift.
        private const float MatchEpsilon = 1.0f;

        // Reflection: protected members of DoorBlocker / HoldingInteractable.
        private static MethodInfo? _onFinishedHolding;
        private static FieldInfo? _isHoldingFinished;
        private static bool _membersResolved;

        public static bool IsApplyingMirror => _applyingMirror;

        private static bool Enabled
        {
            get { try { return Plugin.Cfg.EnableDoorBlockerSync.Value; } catch { return false; } }
        }
        private static bool LogOn
        {
            get { try { return Plugin.Cfg.LogDoorBlockerSync.Value; } catch { return false; } }
        }

        // ----------------------------------------------------------------- registry (ActivateDoorBlocker postfix)

        /// <summary>Register a door that starts closed. Trap doors (<c>isAnClosingDoor: true</c>) are skipped — their
        /// open can also come from <c>EnemyOpeningDoor</c>, and their slam-shut is out of scope.</summary>
        public static void Register(DoorBlocker door, bool isAnClosingDoor)
        {
            if (door == null || isAnClosingDoor) return;
            try { _registry[door] = door.transform.position; }
            catch { }
        }

        // ----------------------------------------------------------------- capture (OnFinishedHolding postfix)

        public static void CaptureLocalOpen(DoorBlocker door)
        {
            try
            {
                if (_applyingMirror) return;                          // mirrored open — never re-broadcast
                if (!Enabled) return;
                if (!NetGameplaySyncBridge.IsSessionActive) return;   // skip all work in solo play
                if (door == null) return;
                if (!_registry.TryGetValue(door, out Vector3 key)) return; // not an openable door we track

                _registry.Remove(door); // one-way transition — it can never open again, so it can never match again

                NetGameplaySyncBridge.ReportLocalDoorBlockerOpen(new NetDoorBlockerOpen
                {
                    Sequence = ++_captureSeq,
                    Position = key,
                });

                if (LogOn) NetLogger.Info($"[ChunkDoor] capture OPEN name={door.name} pos={key:F1}");
            }
            catch (Exception ex) { NetLogger.Warn($"[ChunkDoor] capture failed: {ex.Message}"); }
        }

        // ----------------------------------------------------------------- mirror (receiving peer)

        public static void ApplyRemoteOpen(NetDoorBlockerOpen m)
        {
            try
            {
                if (!Enabled || m == null) return;
                if (!ResolveMembers()) { if (LogOn) NetLogger.Info("[ChunkDoor] DoorBlocker members unresolved"); return; }

                DoorBlocker? target = FindMatch(m.Position);
                if (target == null)
                {
                    if (LogOn) NetLogger.Info($"[ChunkDoor] mirror peer={m.PeerId} no door near {m.Position:F1}");
                    return;
                }

                // Already open locally (both players finished the same door at once) — the open is one-way, so a
                // second apply would only replay the sound/shake and restart the collider-disable coroutine.
                if (_isHoldingFinished?.GetValue(target) is bool done && done)
                {
                    _registry.Remove(target);
                    if (LogOn) NetLogger.Info($"[ChunkDoor] mirror peer={m.PeerId} already open locally name={target.name}");
                    return;
                }

                _registry.Remove(target);

                // Guard so the resulting OnFinishedHolding postfix does NOT re-broadcast (would echo).
                _applyingMirror = true;
                try { _onFinishedHolding!.Invoke(target, null); }
                finally { _applyingMirror = false; }

                if (LogOn) NetLogger.Info($"[ChunkDoor] mirror peer={m.PeerId} OPEN name={target.name} near {m.Position:F1}");
            }
            catch (Exception ex) { NetLogger.Warn($"[ChunkDoor] mirror failed: {ex.Message}"); }
        }

        // ----------------------------------------------------------------- helpers

        /// <summary>Nearest live registered door within <see cref="MatchEpsilon"/> of the incoming key.</summary>
        private static DoorBlocker? FindMatch(Vector3 key)
        {
            DoorBlocker? best = null;
            float bestSqr = MatchEpsilon * MatchEpsilon;
            List<DoorBlocker>? dead = null;

            foreach (var kv in _registry)
            {
                DoorBlocker d = kv.Key;
                if (d == null) { (dead ??= new List<DoorBlocker>()).Add(d); continue; }
                float sqr = (kv.Value - key).sqrMagnitude;
                if (sqr <= bestSqr) { bestSqr = sqr; best = d; }
            }

            if (dead != null) foreach (var x in dead) _registry.Remove(x);
            return best;
        }

        private static bool ResolveMembers()
        {
            if (_membersResolved) return _onFinishedHolding != null && _isHoldingFinished != null;
            _membersResolved = true;
            try
            {
                // OnFinishedHolding: DoorBlocker's own override (the real open — sound, animation, collider removal).
                _onFinishedHolding = AccessTools.DeclaredMethod(typeof(DoorBlocker), "OnFinishedHolding");
                // isHoldingFinished: declared on the HoldingInteractable base — AccessTools walks the hierarchy.
                _isHoldingFinished = AccessTools.Field(typeof(DoorBlocker), "isHoldingFinished");
            }
            catch { }
            return _onFinishedHolding != null && _isHoldingFinished != null;
        }

        // Scene change — drop stale doors from the previous level.
        public static void Clear()
        {
            _registry.Clear();
        }
    }
}
