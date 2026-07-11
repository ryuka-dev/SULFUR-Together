using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.World;
using SULFURTogether.Networking.Gameplay.Boss;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Issue #5: host-authoritative one-shot <c>TriggerSpawner</c> spawns (e.g. the Caves maze skeleton ambush).
    ///
    /// <para>Vanilla runs <c>PlayerTrigger → Triggerable.Trigger → TriggerSpawner.Spawn</c> locally and one-shot per
    /// machine, so each player triggers their own local, unsynced skeletons. This manager routes every trigger through
    /// the host: a client blocks its local spawn and asks the host (<see cref="NetTriggerSpawn"/>); the host runs the
    /// real spawn once per trigger (first-trigger-wins, keyed by world position) and it reaches all peers through the
    /// existing runtime-spawn mirror (<see cref="RuntimeSpawnManager"/>, which now classifies a <c>TriggerSpawner</c>
    /// owner as broadcastable). A host that triggers it itself is the same authoritative path.</para>
    /// </summary>
    internal static class TriggerSpawnSyncManager
    {
        private static bool Enabled { get { try { return Plugin.Cfg.EnableTriggerSpawnSync.Value; } catch { return false; } } }
        private static bool LogOn  { get { try { return Plugin.Cfg.LogTriggerSpawnSync.Value; } catch { return false; } } }

        // HOST: set of trigger keys already fired this level (first-trigger-wins across all players).
        private static readonly HashSet<long> _consumed = new HashSet<long>();
        // HOST: true while replaying a client's requested trigger, so the Triggerable.Trigger prefix lets it run.
        private static bool _applyingRemote;

        // diagnostics
        public static int ClientRequestsSent;
        public static int HostLocalTriggers;
        public static int HostRemoteTriggersApplied;
        public static int HostTriggersDeduped;
        public static int HostRequestsUnmatched;

        // cached reflection (defensive — the trigger types live behind the game boundary)
        private static FieldInfo? _unitToSpawnField;   // TriggerSpawner.unitToSpawn
        private static FieldInfo? _unitIdField;        // UnitSO.id
        private static FieldInfo? _unitIdValueField;   // UnitId.value

        public static void Clear()
        {
            _consumed.Clear();
            _applyingRemote = false;
        }

        // ---- key: quantize the world position so host & client agree despite tiny float drift ----
        private const float KeyQuantum = 0.25f;
        private static long PositionKey(Vector3 p)
        {
            long qx = (long)Mathf.Round(p.x / KeyQuantum);
            long qy = (long)Mathf.Round(p.y / KeyQuantum);
            long qz = (long)Mathf.Round(p.z / KeyQuantum);
            // pack (each clamped to ~21 bits — ample for level coordinates)
            return ((qx & 0x1FFFFF) << 42) ^ ((qy & 0x1FFFFF) << 21) ^ (qz & 0x1FFFFF);
        }

        // ============================================================ patch entry (Triggerable.Trigger prefix)

        /// <summary>Called from the <c>Triggerable.Trigger</c> prefix. Returns true to let the vanilla local spawn run,
        /// false to suppress it (client redirected to host, or host de-duplicated).</summary>
        public static bool OnTriggerableTrigger(object triggerable)
        {
            try
            {
                if (!Enabled) return true;                                  // feature off → vanilla
                if (!NetGameplaySyncBridge.IsSessionActive) return true;    // solo → vanilla
                if (triggerable is not Triggerable t) return true;

                // Only the skeleton/enemy spawners matter; non-spawner Triggerables (fog, ...) stay purely local.
                var spawner = t.GetComponent<TriggerSpawner>();
                if (spawner == null) return true;

                Vector3 pos = t.transform.position;

                var mode = NetGameplaySyncBridge.BossMode;
                if (mode == NetMode.Host)
                {
                    if (_applyingRemote) return true;                       // servicing a client request — let it spawn
                    long key = PositionKey(pos);
                    if (_consumed.Contains(key))
                    {
                        HostTriggersDeduped++;
                        if (LogOn) Plugin.Log.Info($"[TriggerSpawn] host dedup (already fired) pos={pos:F1}");
                        return false;                                      // already fired for someone — no double
                    }
                    _consumed.Add(key);
                    HostLocalTriggers++;
                    if (LogOn) Plugin.Log.Info($"[TriggerSpawn] host local trigger pos={pos:F1} (spawn broadcasts via runtime-spawn)");
                    return true;                                            // real spawn; RuntimeSpawnManager broadcasts it
                }

                if (mode == NetMode.Client)
                {
                    SendHostRequest(spawner, pos);
                    return false;                                           // never spawn locally; host is authoritative
                }

                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[TriggerSpawn] OnTriggerableTrigger failed: {ex.GetType().Name}: {ex.Message}");
                return true; // fail-open to vanilla so a bug never silently deletes the ambush
            }
        }

        // ============================================================ client → host request

        private static void SendHostRequest(TriggerSpawner spawner, Vector3 pos)
        {
            try
            {
                NetBossEncounterManager.TryGetRunContext(out string chap, out int lvl, out bool hasSeed, out int seed);
                var msg = new NetTriggerSpawn
                {
                    Position = pos,
                    UnitIdValue = ReadUnitIdValue(spawner),
                    ChapterName = chap ?? "",
                    LevelIndex = lvl,
                    HasSeed = hasSeed,
                    Seed = seed,
                };
                ClientRequestsSent++;
                if (LogOn) Plugin.Log.Info($"[TriggerSpawn] client → host request {msg.ToCompact()} (local spawn suppressed)");
                NetGameplaySyncBridge.SendClientTriggerSpawn(msg);
            }
            catch (Exception ex) { Plugin.Log.Warn($"[TriggerSpawn] SendHostRequest failed: {ex.Message}"); }
        }

        // ============================================================ host handling

        /// <summary>HOST: a client asked us to run a trigger. Fire it once (first-trigger-wins) by invoking the real
        /// local <c>Triggerable</c> at that position, so the vanilla spawn + activation runs and the runtime-spawn
        /// mirror carries it to every peer.</summary>
        public static void HandleClientTriggerRequest(NetTriggerSpawn msg, string peerId)
        {
            try
            {
                if (!Enabled || msg == null) return;
                if (NetGameplaySyncBridge.BossMode != NetMode.Host) return;

                // Reject a request for a level the host is no longer in — or a DIFFERENT generated instance of the same
                // level (client raced ahead into its own seed): the trigger position is meaningless in the host's maze,
                // so dropping cleanly here is safer than mis-matching a far Triggerable (Log403: the desynced Caves:4
                // requests). The genuine same-level, same-seed case still passes.
                if (NetBossEncounterManager.TryGetRunContext(out string chap, out int lvl, out bool hasSeed, out int seed)
                    && (!string.Equals(chap, msg.ChapterName, StringComparison.Ordinal)
                        || lvl != msg.LevelIndex
                        || (hasSeed && msg.HasSeed && seed != msg.Seed)))
                {
                    if (LogOn) Plugin.Log.Info($"[TriggerSpawn] host drop (run mismatch) {msg.ToCompact()} local={chap}:{lvl} seed={(hasSeed ? seed.ToString() : "?")}");
                    return;
                }

                long key = PositionKey(msg.Position);
                if (_consumed.Contains(key))
                {
                    HostTriggersDeduped++;
                    if (LogOn) Plugin.Log.Info($"[TriggerSpawn] host dedup client request (already fired) {msg.ToCompact()} peer={peerId}");
                    return; // already fired — the requester already received the earlier broadcast
                }

                var triggerable = FindTriggerableNear(msg.Position, out float dist);
                if (triggerable == null)
                {
                    HostRequestsUnmatched++;
                    Plugin.Log.Warn($"[TriggerSpawn] host could not find a Triggerable near {msg.Position:F1} (nearest={dist:F1}m) — skeleton not spawned for peer={peerId}");
                    return;
                }

                _consumed.Add(key);
                HostRemoteTriggersApplied++;
                if (LogOn) Plugin.Log.Info($"[TriggerSpawn] host applying client request {msg.ToCompact()} peer={peerId} match={dist:F2}m");

                // Aggro/log inside Trigger dereference the triggering object — pass the host player (never null-crash).
                GameObject triggeringObject = ResolveHostPlayerObject() ?? triggerable.gameObject;

                _applyingRemote = true;
                try { triggerable.Trigger(triggeringObject); }
                finally { _applyingRemote = false; }
            }
            catch (Exception ex) { Plugin.Log.Warn($"[TriggerSpawn] HandleClientTriggerRequest failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static Triggerable? FindTriggerableNear(Vector3 pos, out float bestDist)
        {
            bestDist = float.MaxValue;
            Triggerable? best = null;
            // Rare event (one-shot triggers) — a scene scan is acceptable and avoids a stale registry.
            // (FindObjectsByType/FindObjectSortMode aren't in this Unity version; the codebase uses FindObjectsOfType.)
#pragma warning disable CS0618
            var all = UnityEngine.Object.FindObjectsOfType<Triggerable>();
#pragma warning restore CS0618
            foreach (var t in all)
            {
                if (t == null || t.GetComponent<TriggerSpawner>() == null) continue;
                float d = Vector3.Distance(t.transform.position, pos);
                if (d < bestDist) { bestDist = d; best = t; }
            }
            // Positions are seed-deterministic; a generous tolerance still can't collide with a different, far trigger.
            return (best != null && bestDist <= 1.5f) ? best : null;
        }

        // ============================================================ reflection helpers

        private static int ReadUnitIdValue(TriggerSpawner spawner)
        {
            try
            {
                _unitToSpawnField ??= AccessTools.Field(typeof(TriggerSpawner), "unitToSpawn");
                object? unitSO = _unitToSpawnField?.GetValue(spawner);
                if (unitSO == null) return 0;
                _unitIdField ??= AccessTools.Field(unitSO.GetType(), "id");
                object? id = _unitIdField?.GetValue(unitSO);
                if (id == null) return 0;
                _unitIdValueField ??= AccessTools.Field(id.GetType(), "value");
                object? val = _unitIdValueField?.GetValue(id);
                return val != null ? Convert.ToInt32(val) : 0;
            }
            catch { return 0; }
        }

        private static GameObject? ResolveHostPlayerObject()
        {
            try
            {
                GameManager gm = GameManager.Instance;
                return gm != null ? gm.PlayerObject : null;
            }
            catch { return null; }
        }
    }
}
