using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// SL-2b (Shared-loot) sync for <c>PerfectRandom.Sulfur.Gameplay.LootableObject</c> — the food / material / scavenge
    /// hatboxes and the cash register (all use this class, NOT <c>Container</c>: an <c>Interactable</c> whose trigger
    /// event calls <c>LootableObject.Trigger()</c> → an RNG count → <c>LootManager.SpawnLootFrom</c> per item).
    /// <para>Because that loot goes through <c>LootManager.SpawnLootFrom</c>, SL-1 already suppresses a client's roll.
    /// The only missing piece for Model A is that the HOST must trigger its own copy when a client loots one — otherwise
    /// the host never rolls and, with the client suppressed, no loot appears anywhere. So: the interactor's <c>Trigger</c>
    /// runs normally (it animates; a client's loot is suppressed by SL-1), a client additionally asks the host to trigger
    /// its matching object (→ host rolls → the world-drop channel mirrors the items), and the host broadcasts the trigger
    /// so every OTHER peer replays the animation (their loot suppressed too). Keyed by deterministic world position.</para>
    /// Reflection-only (the mod doesn't reference the Gameplay assembly); covers <c>ChurchCollectionLootable</c> too since
    /// it inherits <c>Trigger</c>. Only active while shared loot is on.
    /// </summary>
    internal static class LootableSyncManager
    {
        private static readonly Dictionary<Component, Vector3> _registry = new Dictionary<Component, Vector3>();
        // Objects already triggered locally (own interaction) OR mirrored — so a peer never double-animates/re-requests.
        private static readonly HashSet<Component> _triggered = new HashSet<Component>();

        private static bool _applyingMirror;
        private static int _captureSeq;
        private const float MatchEpsilon = 1.0f;

        private static MethodInfo _triggerMethod;
        private static bool _triggerResolved;
        private static MethodInfo _forceLootMethod;
        private static bool _forceLootResolved;

        private static bool SharedLootActive
        {
            get { try { return Plugin.Cfg.EnableLootableSync.Value && NetSessionSettings.SharedLootEnabled; } catch { return false; } }
        }
        private static bool LogOn
        {
            get { try { return Plugin.Cfg.LogLootableSync.Value; } catch { return false; } }
        }

        // ----------------------------------------------------------------- registry (LootableObject.Start postfix)

        public static void Register(Component lootable)
        {
            if (lootable == null) return;
            try { _registry[lootable] = lootable.transform.position; } catch { }
        }

        // ----------------------------------------------------------------- interactor side (Trigger postfix)

        public static void OnLocalTrigger(Component lootable)
        {
            try
            {
                if (_applyingMirror) return;          // this Trigger was our own mirror replay — don't loop
                if (!SharedLootActive) return;        // Independent / solo — vanilla per-peer
                if (lootable == null) return;
                if (!_triggered.Add(lootable)) return; // already handled this object

                if (!_registry.TryGetValue(lootable, out Vector3 key)) key = lootable.transform.position;

                if (NetGameplaySyncBridge.IsHost)
                {
                    // Host is the authority: its own Trigger already rolled the loot (mirrored by the world-drop channel).
                    // Tell the other peers so they replay the animation.
                    NetGameplaySyncBridge.ReportLootableTriggered(new NetChestOpen { Sequence = ++_captureSeq, Position = key });
                    if (LogOn) NetLogger.Info($"[LootableSync] host broadcast TRIGGERED name={lootable.name} pos={key:F1}");
                }
                else
                {
                    // Client: our own loot was suppressed (SL-1); ask the host to roll it on its matching object.
                    NetGameplaySyncBridge.ReportLootableRequest(new NetChestOpen { Sequence = ++_captureSeq, Position = key });
                    if (LogOn) NetLogger.Info($"[LootableSync] client request TRIGGER name={lootable.name} pos={key:F1}");
                }
            }
            catch (Exception ex) { NetLogger.Warn($"[LootableSync] local-trigger failed: {ex.Message}"); }
        }

        // ----------------------------------------------------------------- host: a client asked us to trigger one

        public static void HandleRequest(NetChestOpen m)
        {
            try
            {
                if (!SharedLootActive || !NetGameplaySyncBridge.IsHost || m == null) return;
                Component target = FindMatch(m.Position);
                if (target == null)
                {
                    if (LogOn) NetLogger.Info($"[LootableSync] host: no lootable near {m.Position:F1} for peer={m.PeerId}");
                    return;
                }
                if (!_triggered.Add(target)) return; // already looted on the host

                // Play the open animation on the host too, so a client-opened object doesn't stay looking un-searched.
                // Guarded so the resulting Trigger postfix can't re-broadcast.
                _applyingMirror = true;
                try { InvokeTrigger(target); }
                finally { _applyingMirror = false; }
                // Roll the loot DIRECTLY (world-drop channel mirrors the items). We must NOT rely on Trigger()'s animation
                // event (TriggerLootFromAnimation): the host usually isn't looking at a client-opened object, so its
                // animator is culled and the event never fires → no loot (Log430: hatboxes triggered but dropped nothing).
                // Loot()'s own lootHasBeenSpawned guard means a later real animation event can't double-roll.
                InvokeForceLoot(target);
                NetGameplaySyncBridge.ReportLootableTriggered(new NetChestOpen { Sequence = ++_captureSeq, Position = m.Position });
                if (LogOn) NetLogger.Info($"[LootableSync] host animate+force-loot+broadcast name={target.name} for peer={m.PeerId}");
            }
            catch (Exception ex) { NetLogger.Warn($"[LootableSync] host request failed: {ex.Message}"); }
        }

        // ----------------------------------------------------------------- any peer: replay the animation (visual)

        public static void ApplyTriggered(NetChestOpen m)
        {
            try
            {
                if (!Plugin.Cfg.EnableLootableSync.Value || m == null) return;
                Component target = FindMatch(m.Position);
                if (target == null)
                {
                    if (LogOn) NetLogger.Info($"[LootableSync] mirror peer={m.PeerId} no lootable near {m.Position:F1}");
                    return;
                }
                if (!_triggered.Add(target)) return; // we already triggered/mirrored this one (e.g. the interactor)

                // Replay Trigger() for the open animation. On a client the resulting SpawnLootFrom is suppressed by SL-1,
                // so this only animates; the authoritative items arrive via the world-drop channel.
                _applyingMirror = true;
                try { InvokeTrigger(target); }
                finally { _applyingMirror = false; }

                if (LogOn) NetLogger.Info($"[LootableSync] mirror peer={m.PeerId} TRIGGERED name={target.name} near {m.Position:F1}");
            }
            catch (Exception ex) { NetLogger.Warn($"[LootableSync] mirror failed: {ex.Message}"); }
        }

        // ----------------------------------------------------------------- helpers

        // Replay the loot object's open on a mirror / host copy via its own Trigger(). Targeted (only the loot trigger
        // event, no risk of re-firing unrelated Interactable events). Animates the register (its animation is on the
        // "Loot" trigger); the hatboxes' open animation is NOT driven this way and stays un-animated on remote copies —
        // a known cosmetic limitation tracked as a GitHub issue (loot itself is correct via InvokeForceLoot). AlwaysAnimate
        // keeps the animator evaluating even off-screen so a culled peer still honours the trigger.
        private static void InvokeTrigger(Component lootable)
        {
            try
            {
                var anim = lootable.GetComponent<Animator>();
                if (anim != null) anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
            catch { }

            if (!_triggerResolved)
            {
                _triggerResolved = true;
                var t = AccessTools.TypeByName("PerfectRandom.Sulfur.Gameplay.LootableObject");
                _triggerMethod = t != null ? AccessTools.Method(t, "Trigger") : null;
            }
            _triggerMethod?.Invoke(lootable, null);
        }

        // Roll the loot directly, bypassing the animator-event path — TriggerLootFromAnimation() is the public method the
        // "Loot" animation calls; it rolls immediately regardless of whether the animator is culled (Loot() is guarded by
        // its own lootHasBeenSpawned, so a later real animation event can't double-roll).
        private static void InvokeForceLoot(Component lootable)
        {
            if (!_forceLootResolved)
            {
                _forceLootResolved = true;
                var t = AccessTools.TypeByName("PerfectRandom.Sulfur.Gameplay.LootableObject");
                _forceLootMethod = t != null ? AccessTools.Method(t, "TriggerLootFromAnimation") : null;
            }
            _forceLootMethod?.Invoke(lootable, null);
        }

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
            if (dead != null) foreach (var x in dead) _registry.Remove(x);
            return best;
        }

        public static void Clear()
        {
            _registry.Clear();
            _triggered.Clear();
        }
    }
}
