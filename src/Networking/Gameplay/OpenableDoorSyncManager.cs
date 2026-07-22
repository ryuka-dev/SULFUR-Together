using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PerfectRandom.Sulfur.Core;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase KD (crypt sync) <c>OpenableDoor</c> open sync — peer-authoritative EFFECT mirror.
    /// <para>A locked <c>OpenableDoor</c> (desert crypt key door, and other one-way doors) opens off the LOCAL player's
    /// interaction only, so it stays shut and impassable on every other end — and with a single shared crypt key the
    /// second player cannot otherwise get in. This mirrors every such open to all ends, keyed by the door's
    /// deterministic world position.</para>
    /// <para>The mirror invokes the door's real private <c>Open()</c> (the single funnel that plays the open animation +
    /// fires <c>onTriggerEventsOpen</c>), which reproduces exactly what the game does WITHOUT consuming a key or needing
    /// a <c>KeyStation</c> on the receiving end. <c>Open()</c> does not check the key requirement (only <c>AttemptOpen</c>
    /// does), so the mirror opens cleanly for a keyless peer.</para>
    /// <para>Only NON-closeable doors are captured. A closeable toggle door would desync under a one-way open mirror
    /// (its later close is not tracked), so it is deliberately excluded — the same spirit as DB-1 skipping the trap
    /// door's slam-shut.</para>
    /// </summary>
    internal static class OpenableDoorSyncManager
    {
        // Registered live openable doors → their world position (level-gen placed; the root never moves).
        private static readonly Dictionary<OpenableDoor, Vector3> _registry = new Dictionary<OpenableDoor, Vector3>();

        // True while applying a mirrored open locally — the Open postfix must NOT re-broadcast it.
        private static bool _applyingMirror;

        private static int _captureSeq;

        // Max distance (m) between an incoming position key and a local door to count as a match. Deterministic
        // generation makes these identical; the slack absorbs float drift.
        private const float MatchEpsilon = 1.0f;

        // Reflection: OpenableDoor.Open() (private) + isCloseable (private serialized).
        private static MethodInfo? _openMethod;
        private static FieldInfo? _isCloseableField;
        private static bool _membersResolved;

        public static bool IsApplyingMirror => _applyingMirror;

        private static bool Enabled
        {
            get { try { return Plugin.Cfg.EnableOpenableDoorSync.Value; } catch { return false; } }
        }
        private static bool LogOn
        {
            get { try { return Plugin.Cfg.LogOpenableDoorSync.Value; } catch { return false; } }
        }

        // ----------------------------------------------------------------- capture (OpenableDoor.Open postfix)

        public static void CaptureLocalOpen(OpenableDoor door)
        {
            try
            {
                if (_applyingMirror) return;                          // mirrored open — never re-broadcast
                if (!Enabled) return;
                if (!NetGameplaySyncBridge.IsSessionActive) return;  // skip all work in solo play
                if (door == null) return;
                if (!ResolveMembers()) return;
                if (IsCloseable(door)) return;                       // closeable toggle door — out of scope

                Vector3 key;
                if (!_registry.TryGetValue(door, out key))
                {
                    key = door.transform.position;
                    _registry[door] = key;
                }

                NetGameplaySyncBridge.ReportLocalOpenableDoorOpen(new NetOpenableDoorOpen
                {
                    Sequence = ++_captureSeq,
                    Position = key,
                });

                if (LogOn) NetLogger.Info($"[KeyDoor] capture OPEN name={door.name} pos={key:F1}");
            }
            catch (Exception ex) { NetLogger.Warn($"[KeyDoor] capture failed: {ex.Message}"); }
        }

        // ----------------------------------------------------------------- mirror (receiving peer)

        public static void ApplyRemoteOpen(NetOpenableDoorOpen m)
        {
            try
            {
                if (!Enabled || m == null) return;
                if (!ResolveMembers()) { if (LogOn) NetLogger.Info("[KeyDoor] OpenableDoor members unresolved"); return; }

                OpenableDoor? target = FindMatch(m.Position);
                if (target == null)
                {
                    if (LogOn) NetLogger.Info($"[KeyDoor] mirror peer={m.PeerId} no door near {m.Position:F1}");
                    return;
                }

                // Guard so the resulting Open postfix does NOT re-broadcast (would echo).
                _applyingMirror = true;
                try { _openMethod!.Invoke(target, null); }
                finally { _applyingMirror = false; }

                if (LogOn) NetLogger.Info($"[KeyDoor] mirror peer={m.PeerId} OPEN name={target.name} near {m.Position:F1}");
            }
            catch (Exception ex) { NetLogger.Warn($"[KeyDoor] mirror failed: {ex.Message}"); }
        }

        // ----------------------------------------------------------------- helpers

        /// <summary>Nearest live door within <see cref="MatchEpsilon"/> of the incoming key. Doors are found live from
        /// the scene (unlike DB-1, OpenableDoor has no registry-building Activate hook), then cached.</summary>
        private static OpenableDoor? FindMatch(Vector3 key)
        {
            OpenableDoor? best = null;
            float bestSqr = MatchEpsilon * MatchEpsilon;

            foreach (var d in UnityEngine.Object.FindObjectsByType<OpenableDoor>(FindObjectsSortMode.None))
            {
                if (d == null) continue;
                Vector3 pos = d.transform.position;
                _registry[d] = pos;
                float sqr = (pos - key).sqrMagnitude;
                if (sqr <= bestSqr) { bestSqr = sqr; best = d; }
            }
            return best;
        }

        private static bool IsCloseable(OpenableDoor door)
        {
            try { return _isCloseableField?.GetValue(door) is bool b && b; }
            catch { return false; }
        }

        private static bool ResolveMembers()
        {
            if (_membersResolved) return _openMethod != null;
            _membersResolved = true;
            try
            {
                _openMethod = AccessTools.DeclaredMethod(typeof(OpenableDoor), "Open", Type.EmptyTypes);
                _isCloseableField = AccessTools.Field(typeof(OpenableDoor), "isCloseable");
            }
            catch { }
            return _openMethod != null;
        }

        // Scene change — drop stale doors from the previous level.
        public static void Clear()
        {
            _registry.Clear();
        }
    }
}
