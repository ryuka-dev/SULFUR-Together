using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Events;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase LD-2a — FF14-style arena lockdown, host-authoritative MEMBERSHIP + TIMER (decision-logging only for now;
    /// LD-2b adds the two-way barrier, LD-2c the popup + teleport).
    /// <para>Pinned spec: any player crossing an arena seal trigger (a <c>PlayerTrigger</c> that closes a combat-room
    /// gate/door — LD-1 MetalGate.Close or LD-1b door SetActive) is "in-room" for that arena (keyed by the trigger's
    /// world position). The FIRST cross is t0. At t0+5 s every NON-in-room end in the same level is force-sealed; at
    /// t0+10 s they get a confirm popup → teleport in. In-room updates in real time, event-driven (a late crosser stops
    /// being a target). The host owns the set + timer; ends act locally on host commands (LD-2b/c).</para>
    /// <para>This phase only LOGS who would be sealed/teleported, so the membership + timing can be verified on real
    /// boss/elite rooms before any movement/teleport effect is wired.</para>
    /// </summary>
    internal static class ArenaLockdownManager
    {
        private sealed class Lockdown
        {
            public Vector3 Pos;
            public float   T0;
            public string  Chapter = ""; public int Level = -1; public bool HasSeed; public int Seed;
            public readonly HashSet<string> InRoom = new HashSet<string>();
            public bool SealFired;
            public bool TeleportFired;
        }

        private static readonly Dictionary<string, Lockdown> _locks = new Dictionary<string, Lockdown>();

        private const float SealDelaySeconds     = 5f;
        private const float TeleportDelaySeconds = 10f;

        private static FieldInfo _eventField;
        private static bool _eventFieldResolved;

        private static bool Enabled
        {
            get { try { return Plugin.Cfg.EnableArenaLockdown.Value; } catch { return false; } }
        }
        private static bool LogOn
        {
            get { try { return Plugin.Cfg.LogArenaLockdown.Value; } catch { return false; } }
        }

        // ----------------------------------------------------------------- local entry (PlayerTrigger.Trigger postfix)

        /// <summary>Any end: the local player crossed a PlayerTrigger. If it is an arena seal trigger, report in-room.</summary>
        public static void OnLocalTriggerFired(object trigger)
        {
            try
            {
                if (!Enabled || !NetGameplaySyncBridge.IsSessionActive) return;
                if (!IsSealTrigger(trigger, out Vector3 pos)) return;
                if (!NetGameplaySyncBridge.TryGetLocalScene(out string chap, out int lvl, out bool hasSeed, out int seed)) return;

                if (LogOn) NetLogger.Info($"[ArenaLockdown] local crossed seal trigger arena=({pos.x:0.0},{pos.y:0.0},{pos.z:0.0}) level={chap}:{lvl}");

                if (NetGameplaySyncBridge.IsHost)
                    ReportEntry(NetGameplaySyncBridge.LocalPeerId, pos, chap, lvl, hasSeed, seed);
                else
                    NetGameplaySyncBridge.SendClientArenaEnter(new NetClientArenaEnter
                    {
                        ArenaPos = pos, ChapterName = chap, LevelIndex = lvl, HasLevelSeed = hasSeed, LevelSeed = seed,
                    });
            }
            catch (Exception ex) { NetLogger.Warn($"[ArenaLockdown] OnLocalTriggerFired failed: {ex.Message}"); }
        }

        /// <summary>HOST: a client reported crossing a seal trigger.</summary>
        public static void HandleClientArenaEnter(NetClientArenaEnter m, string peerId)
        {
            if (!Enabled || !NetGameplaySyncBridge.IsHost || m == null) return;
            ReportEntry(peerId, m.ArenaPos, m.ChapterName, m.LevelIndex, m.HasLevelSeed, m.LevelSeed);
        }

        // ----------------------------------------------------------------- host membership + timer

        private static void ReportEntry(string peerId, Vector3 pos, string chap, int lvl, bool hasSeed, int seed)
        {
            if (!NetGameplaySyncBridge.IsHost) return;
            string key = Key(pos);
            if (!_locks.TryGetValue(key, out var ld))
            {
                ld = new Lockdown { Pos = pos, T0 = Time.unscaledTime, Chapter = chap, Level = lvl, HasSeed = hasSeed, Seed = seed };
                _locks[key] = ld;
                NetLogger.Info($"[ArenaLockdown] START arena={key} level={chap}:{lvl} seed={(hasSeed ? seed.ToString() : "?")} t0 by {peerId}");
            }
            if (ld.InRoom.Add(peerId))
                NetLogger.Info($"[ArenaLockdown] in-room += {peerId} arena={key} members=[{string.Join(",", ld.InRoom)}]");
        }

        /// <summary>HOST: drive the lockdown timers (called from Plugin.Update). Decision-logging only this phase.</summary>
        public static void Tick()
        {
            if (!Enabled || !NetGameplaySyncBridge.IsHost || _locks.Count == 0) return;
            float now = Time.unscaledTime;
            foreach (var kv in _locks)
            {
                var ld = kv.Value;
                float el = now - ld.T0;
                if (!ld.SealFired && el >= SealDelaySeconds)
                {
                    ld.SealFired = true;
                    var non = ComputeNonInRoom(ld);
                    NetLogger.Info($"[ArenaLockdown] t+{SealDelaySeconds:0}s SEAL arena={kv.Key} inRoom=[{string.Join(",", ld.InRoom)}] WOULD-SEAL nonInRoom=[{string.Join(",", non)}] (LD-2b)");
                }
                if (!ld.TeleportFired && el >= TeleportDelaySeconds)
                {
                    ld.TeleportFired = true;
                    var non = ComputeNonInRoom(ld);
                    NetLogger.Info($"[ArenaLockdown] t+{TeleportDelaySeconds:0}s TELEPORT arena={kv.Key} WOULD-TELEPORT nonInRoom=[{string.Join(",", non)}] (LD-2c)");
                }
            }
        }

        /// <summary>Ends in the arena's level that have NOT crossed (the seal/teleport targets). Real-time: a late
        /// crosser has since been added to InRoom and drops out here.</summary>
        private static List<string> ComputeNonInRoom(Lockdown ld)
        {
            var endsInLevel = NetGameplaySyncBridge.GetPeerIdsInLevel(ld.Chapter, ld.Level, ld.HasSeed, ld.Seed);
            return endsInLevel.Where(id => !ld.InRoom.Contains(id)).ToList();
        }

        // ----------------------------------------------------------------- seal-trigger detection

        /// <summary>True if this PlayerTrigger's onTriggerEvents seals a combat room — i.e. closes a MetalGate
        /// (<c>MetalGate.Close</c>) or activates a door-named GameObject (<c>GameObject.SetActive</c>).</summary>
        private static bool IsSealTrigger(object trigger, out Vector3 pos)
        {
            pos = Vector3.zero;
            try
            {
                if (!(trigger is Component c) || c == null) return false;
                pos = c.transform.position;

                if (!_eventFieldResolved)
                {
                    _eventFieldResolved = true;
                    _eventField = trigger.GetType().GetField("onTriggerEvents", BindingFlags.Public | BindingFlags.Instance);
                }
                if (!(_eventField?.GetValue(trigger) is UnityEventBase evt)) return false;

                int n = evt.GetPersistentEventCount();
                for (int i = 0; i < n; i++)
                {
                    string method = evt.GetPersistentMethodName(i);
                    var target = evt.GetPersistentTarget(i);
                    if (target == null) continue;

                    if (string.Equals(method, "Close", StringComparison.Ordinal)
                        && target.GetType().Name.IndexOf("MetalGate", StringComparison.Ordinal) >= 0)
                        return true;

                    if (string.Equals(method, "SetActive", StringComparison.Ordinal))
                    {
                        GameObject go = target as GameObject ?? (target as Component)?.gameObject;
                        if (go != null && go.name.IndexOf("door", StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static string Key(Vector3 p)
            => $"{Mathf.RoundToInt(p.x)}_{Mathf.RoundToInt(p.y)}_{Mathf.RoundToInt(p.z)}";

        // Scene change — drop previous level's lockdowns.
        public static void Clear()
        {
            _locks.Clear();
        }
    }
}
