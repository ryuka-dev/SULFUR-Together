using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Units;
using PerfectRandom.Sulfur.Core.World;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// SL-2 (Shared-loot) chest (<c>Container</c>) sync — host-authoritative open + roll (Model A), keyed by the
    /// container's deterministic world position (same keying as <see cref="DoorBlockerSyncManager"/>).
    /// <list type="bullet">
    /// <item><b>Host interacts:</b> vanilla <c>OnInteract</c> runs (rolls loot + <c>SpawnPickup</c>, mirrored to peers by
    /// the shared-loot world-drop channel + plays the open animation); a postfix broadcasts <c>ChestOpened</c>.</item>
    /// <item><b>Client interacts:</b> the local open is blocked (no roll) and a <c>ChestOpenRequest</c> goes to the host;
    /// the chest opens when the host's <c>ChestOpened</c> arrives.</item>
    /// <item><b>Host receives a request:</b> invokes the real <c>OnInteract</c> on its matching container (→ roll + spawn +
    /// the same broadcast). Its own <c>looted</c> flag is the first-come arbiter — a second request no-ops.</item>
    /// <item><b>Any peer receives <c>ChestOpened</c>:</b> marks the container looted + plays the open animation, VISUAL
    /// ONLY (never re-rolls). The loot arrives separately via the world-drop mirror.</item>
    /// </list>
    /// Only active while shared loot is on; in Independent mode chests stay per-peer (vanilla).
    /// </summary>
    internal static class ChestSyncManager
    {
        // Registered live containers → their world position (level-gen placed; the root never moves).
        private static readonly Dictionary<Container, Vector3> _registry = new Dictionary<Container, Vector3>();

        // Client-side: containers we've already asked the host to open, so a held/spammed interact sends one request.
        private static readonly HashSet<Container> _requested = new HashSet<Container>();

        // True while applying a mirrored open locally — the OnInteract postfix must NOT re-broadcast it.
        private static bool _applyingMirror;
        public static bool IsApplyingMirror => _applyingMirror;

        private static int _captureSeq;

        private const float MatchEpsilon = 1.0f;

        private static FieldInfo _lootedField;      // Container.looted (private bool)
        private static FieldInfo _modelAnimField;   // Container.modelAnimator (private Animator)
        private static bool _membersResolved;

        private static bool SharedLootActive
        {
            get { try { return Plugin.Cfg.EnableChestSync.Value && NetSessionSettings.SharedLootEnabled; } catch { return false; } }
        }
        private static bool LogOn
        {
            get { try { return Plugin.Cfg.LogChestSync.Value; } catch { return false; } }
        }

        // ----------------------------------------------------------------- registry (Container.Start postfix)

        public static void Register(Container c)
        {
            if (c == null) return;
            try { _registry[c] = c.transform.position; } catch { }
        }

        // ----------------------------------------------------------------- client interact → request the host

        /// <summary>Client side: block the local open and ask the host to open this chest (once). Returns true if the
        /// caller should skip the vanilla open (i.e. we took ownership of the interaction).</summary>
        public static bool TryRequestOpen(Container c)
        {
            try
            {
                if (!SharedLootActive) return false;         // Independent mode / solo — vanilla
                if (NetGameplaySyncBridge.IsHost) return false; // host opens locally (authoritative)
                if (c == null) return false;
                if (IsLooted(c)) return false;               // already open — let vanilla no-op

                if (!_requested.Add(c))                       // already requested — swallow the repeat interact
                    return true;

                if (!_registry.TryGetValue(c, out Vector3 key)) key = c.transform.position;

                NetGameplaySyncBridge.ReportChestOpenRequest(new NetChestOpen
                {
                    Sequence = ++_captureSeq,
                    Position = key,
                });

                if (LogOn) NetLogger.Info($"[ChestSync] request OPEN name={c.name} pos={key:F1}");
                return true;
            }
            catch (Exception ex) { NetLogger.Warn($"[ChestSync] request failed: {ex.Message}"); return false; }
        }

        // ----------------------------------------------------------------- host: a client asked us to open a chest

        public static void HandleOpenRequest(NetChestOpen m)
        {
            try
            {
                if (!SharedLootActive || !NetGameplaySyncBridge.IsHost || m == null) return;

                Container target = FindMatch(m.Position);
                if (target == null)
                {
                    if (LogOn) NetLogger.Info($"[ChestSync] host: no chest near {m.Position:F1} for request peer={m.PeerId}");
                    return;
                }
                if (IsLooted(target))
                {
                    if (LogOn) NetLogger.Info($"[ChestSync] host: chest already looted name={target.name} (request ignored)");
                    return;
                }

                // Run the real open on the host: rolls loot + SpawnPickup (mirrored via shared-loot) + open anim; the
                // OnInteract postfix then broadcasts ChestOpened to everyone (including the requester).
                Player hostPlayer = GameManager.Instance != null ? GameManager.Instance.PlayerScript : null;
                target.OnInteract(hostPlayer);

                if (LogOn) NetLogger.Info($"[ChestSync] host opened chest name={target.name} for peer={m.PeerId}");
            }
            catch (Exception ex) { NetLogger.Warn($"[ChestSync] host open-request failed: {ex.Message}"); }
        }

        // ----------------------------------------------------------------- host: broadcast our own real open

        /// <summary>Host side, from the Container.OnInteract postfix after a real open (its own or a request's).</summary>
        public static void BroadcastLocalOpen(Container c)
        {
            try
            {
                if (_applyingMirror) return;
                if (!SharedLootActive || !NetGameplaySyncBridge.IsHost || c == null) return;

                if (!_registry.TryGetValue(c, out Vector3 key)) key = c.transform.position;

                NetGameplaySyncBridge.ReportChestOpened(new NetChestOpen
                {
                    Sequence = ++_captureSeq,
                    Position = key,
                });

                if (LogOn) NetLogger.Info($"[ChestSync] host broadcast OPENED name={c.name} pos={key:F1}");
            }
            catch (Exception ex) { NetLogger.Warn($"[ChestSync] broadcast failed: {ex.Message}"); }
        }

        // ----------------------------------------------------------------- any peer: mirror an opened chest (visual)

        public static void ApplyRemoteOpened(NetChestOpen m)
        {
            try
            {
                if (!Plugin.Cfg.EnableChestSync.Value || m == null) return;
                if (!ResolveMembers()) { if (LogOn) NetLogger.Info("[ChestSync] Container members unresolved"); return; }

                Container target = FindMatch(m.Position);
                if (target == null)
                {
                    if (LogOn) NetLogger.Info($"[ChestSync] mirror peer={m.PeerId} no chest near {m.Position:F1}");
                    return;
                }

                _requested.Remove(target);
                if (IsLooted(target))
                {
                    if (LogOn) NetLogger.Info($"[ChestSync] mirror peer={m.PeerId} already open locally name={target.name}");
                    return;
                }

                // Visual-only open: mark looted + play the Open animation. NEVER call OnInteract / SetContainedItem here —
                // that would roll a second (divergent) loot item. The authoritative loot arrives via the world-drop mirror.
                _applyingMirror = true;
                try
                {
                    _lootedField?.SetValue(target, true);
                    Animator anim = _modelAnimField?.GetValue(target) as Animator;
                    if (anim == null) anim = target.GetComponentInChildren<Animator>();
                    if (anim != null) anim.SetTrigger("Open");
                }
                finally { _applyingMirror = false; }

                if (LogOn) NetLogger.Info($"[ChestSync] mirror peer={m.PeerId} OPENED name={target.name} near {m.Position:F1}");
            }
            catch (Exception ex) { NetLogger.Warn($"[ChestSync] mirror failed: {ex.Message}"); }
        }

        // ----------------------------------------------------------------- helpers

        private static Container FindMatch(Vector3 key)
        {
            Container best = null;
            float bestSqr = MatchEpsilon * MatchEpsilon;
            List<Container> dead = null;

            foreach (var kv in _registry)
            {
                Container c = kv.Key;
                if (c == null) { (dead ??= new List<Container>()).Add(c); continue; }
                float sqr = (kv.Value - key).sqrMagnitude;
                if (sqr <= bestSqr) { bestSqr = sqr; best = c; }
            }

            if (dead != null) foreach (var x in dead) _registry.Remove(x);
            return best;
        }

        private static bool IsLooted(Container c)
        {
            try { return ResolveMembers() && _lootedField.GetValue(c) is bool b && b; }
            catch { return false; }
        }

        private static bool ResolveMembers()
        {
            if (_membersResolved) return _lootedField != null;
            _membersResolved = true;
            try
            {
                _lootedField    = AccessTools.Field(typeof(Container), "looted");
                _modelAnimField = AccessTools.Field(typeof(Container), "modelAnimator");
            }
            catch { }
            return _lootedField != null;
        }

        public static void Clear()
        {
            _registry.Clear();
            _requested.Clear();
        }
    }
}
