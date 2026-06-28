using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase LD-2 — FF14-style arena lockdown, host-authoritative membership + timer + force-seal + teleport.
    /// <para>Pinned spec: any player crossing an arena seal trigger (a <c>PlayerTrigger</c> that closes a combat-room
    /// gate/door — LD-1 MetalGate.Close or LD-1b door SetActive) is "in-room" for that arena (keyed by the trigger's
    /// world position). The FIRST cross is t0. At t0+5 s every NON-in-room end in the same level is force-sealed with an
    /// invisible two-way barrier (LD-2b, <see cref="ArenaBarrierManager"/>); at t0+10 s they get a confirm popup → on
    /// confirm (or on boss death, via <see cref="OnGateOpened"/>) they teleport in and the barrier drops (LD-2c).
    /// In-room updates in real time, event-driven (a late crosser stops being a target). The host owns the set + timer;
    /// each end acts on host commands (<see cref="NetArenaCommand"/>) against its OWN local door/player.</para>
    /// <para>LD-2a (membership + timer) was decision-logging only; LD-2b/c add the real barrier + teleport. The popup
    /// VISUAL is behind <see cref="ShowPrompt"/>/<see cref="HidePrompt"/> seams so a native popup (e.g. SULFUR Native
    /// UI Lib) can be plugged in later — until then the prompt is logged and confirm is the configured key.</para>
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
            public bool Released;
        }

        private static readonly Dictionary<string, Lockdown> _locks = new Dictionary<string, Lockdown>();

        private const float SealDelaySeconds     = 5f;
        private const float TeleportDelaySeconds = 10f;
        // How close a gate-open (boss death / AllDeadTrigger) must be to an arena to count as "this fight ended".
        private const float GateReleaseRadius    = 10f;

        private static FieldInfo _eventField;
        private static bool _eventFieldResolved;

        // ---- LD-2c local arm state (the popup target waits for confirm / boss death) ----
        private static bool    _armed;
        private static Vector3 _armedArena;

        /// <summary>LD-2c popup UI seam — a confirm prompt. Defaults to logging only; a native UI (SULFUR Native UI Lib)
        /// can assign these later to render a real popup. <see cref="ShowPrompt"/> gets the prompt text; <see cref="HidePrompt"/>
        /// is called when the player enters / the prompt is dismissed.</summary>
        public static Action<string> ShowPrompt;
        public static Action         HidePrompt;

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

                if (LogOn) NetLogger.Info($"[ArenaLockdown] local crossed seal trigger arena=({pos.x:0.0},{pos.y:0.0},{pos.z:0.0})");
                ReportLocalInRoom(pos);
            }
            catch (Exception ex) { NetLogger.Warn($"[ArenaLockdown] OnLocalTriggerFired failed: {ex.Message}"); }
        }

        /// <summary>Report that the local player is now in-room for this arena (crossed the trigger or teleported in).</summary>
        private static void ReportLocalInRoom(Vector3 pos)
        {
            if (!NetGameplaySyncBridge.TryGetLocalScene(out string chap, out int lvl, out bool hasSeed, out int seed)) return;
            if (NetGameplaySyncBridge.IsHost)
                ReportEntry(NetGameplaySyncBridge.LocalPeerId, pos, chap, lvl, hasSeed, seed);
            else
                NetGameplaySyncBridge.SendClientArenaEnter(new NetClientArenaEnter
                {
                    ArenaPos = pos, ChapterName = chap, LevelIndex = lvl, HasLevelSeed = hasSeed, LevelSeed = seed,
                });
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

        /// <summary>Driven from Plugin.Update on EVERY end: host runs the lockdown timers; every end polls its own
        /// confirm key for an armed teleport.</summary>
        public static void Tick()
        {
            if (!Enabled || !NetGameplaySyncBridge.IsSessionActive) return;
            if (NetGameplaySyncBridge.IsHost) HostTick();
            LocalTick();
        }

        private static void HostTick()
        {
            if (_locks.Count == 0) return;
            float now = Time.unscaledTime;
            foreach (var kv in _locks)
            {
                var ld = kv.Value;
                if (ld.Released) continue;
                float el = now - ld.T0;
                if (!ld.SealFired && el >= SealDelaySeconds)
                {
                    ld.SealFired = true;
                    IssueCommand(ld, ArenaCommandKind.Seal);
                }
                if (!ld.TeleportFired && el >= TeleportDelaySeconds)
                {
                    ld.TeleportFired = true;
                    IssueCommand(ld, ArenaCommandKind.Popup);
                }
            }
        }

        /// <summary>HOST: send a command to this arena's current non-in-room targets (and apply the host's own target
        /// locally). Real-time: a player who has since entered drops out of the target set.</summary>
        private static void IssueCommand(Lockdown ld, ArenaCommandKind kind)
        {
            var targets = ComputeNonInRoom(ld);
            if (targets.Count == 0)
            {
                if (LogOn) NetLogger.Info($"[ArenaLockdown] {kind} arena={Key(ld.Pos)} — no non-in-room targets, skip");
                return;
            }

            NetLogger.Info($"[ArenaLockdown] {kind} arena={Key(ld.Pos)} inRoom=[{string.Join(",", ld.InRoom)}] targets=[{string.Join(",", targets)}]");

            // Host applies its own target locally (no packet to itself).
            if (targets.Contains(NetGameplaySyncBridge.LocalPeerId))
                ApplyLocalCommand(kind, ld.Pos);

            NetGameplaySyncBridge.BroadcastArenaCommand(new NetArenaCommand
            {
                Kind = kind, ArenaPos = ld.Pos, TargetPeerIds = targets,
            });
        }

        /// <summary>CLIENT: a host arena command arrived. Act only if this end is a target.</summary>
        public static void HandleArenaCommand(NetArenaCommand m, string localPeerId)
        {
            if (!Enabled || m == null) return;
            if (m.TargetPeerIds == null || !m.TargetPeerIds.Contains(localPeerId)) return;
            ApplyLocalCommand(m.Kind, m.ArenaPos);
        }

        /// <summary>Run a command against THIS end's local door / player.</summary>
        private static void ApplyLocalCommand(ArenaCommandKind kind, Vector3 arenaPos)
        {
            try
            {
                switch (kind)
                {
                    case ArenaCommandKind.Seal:
                        ArenaBarrierManager.Seal(arenaPos);
                        break;

                    case ArenaCommandKind.Popup:
                        _armed = true;
                        _armedArena = arenaPos;
                        string keyName = "?";
                        try { keyName = Plugin.Cfg.ArenaEnterConfirmKey.Value.ToString(); } catch { }
                        string text = $"Press [{keyName}] to enter the arena";
                        if (ShowPrompt != null) { try { ShowPrompt(text); } catch (Exception ex) { NetLogger.Warn($"[ArenaLockdown] ShowPrompt failed: {ex.Message}"); } }
                        else NetLogger.Info($"[ArenaLockdown] POPUP armed teleport (UI deferred to Native UI Lib) — {text}");
                        break;

                    case ArenaCommandKind.Release:
                        if (LogOn) NetLogger.Info($"[ArenaLockdown] RELEASE arena=({arenaPos.x:0.0},{arenaPos.y:0.0},{arenaPos.z:0.0}) — fight over, entering");
                        TeleportIntoArena(arenaPos);
                        break;
                }
            }
            catch (Exception ex) { NetLogger.Warn($"[ArenaLockdown] ApplyLocalCommand({kind}) failed: {ex.Message}"); }
        }

        /// <summary>Every end: poll the confirm key while a teleport is armed.</summary>
        private static void LocalTick()
        {
            if (!_armed) return;
            try
            {
                if (Plugin.Cfg.ArenaEnterConfirmKey.Value.IsDown())
                    TeleportIntoArena(_armedArena);
            }
            catch { }
        }

        /// <summary>Teleport the local player into the arena, drop the barrier, and become in-room.</summary>
        private static void TeleportIntoArena(Vector3 arenaPos)
        {
            try
            {
                object unit = ResolveLocalPlayerUnit();
                if (unit != null)
                {
                    Vector3 dest = arenaPos + Vector3.up * 0.5f;
                    var tp = AccessTools.Method(unit.GetType(), "TeleportTo", new[] { typeof(Vector3) })
                          ?? AccessTools.Method(unit.GetType(), "TeleportTo");
                    if (tp != null) tp.Invoke(unit, new object[] { dest });
                    else if (unit is Component c && c != null) c.transform.position = dest;
                    NetLogger.Info($"[ArenaLockdown] teleported local player into arena ({arenaPos.x:0.0},{arenaPos.y:0.0},{arenaPos.z:0.0})");
                }
                else NetLogger.Warn("[ArenaLockdown] teleport: local player unit missing");
            }
            catch (Exception ex) { NetLogger.Warn($"[ArenaLockdown] teleport failed: {ex.Message}"); }

            ArenaBarrierManager.Unseal(arenaPos);
            if (_armed && Key(_armedArena) == Key(arenaPos))
            {
                _armed = false;
                if (HidePrompt != null) { try { HidePrompt(); } catch { } }
            }
            // Now in-room: tell the host (so we drop out of the target set; host self-reports if it teleported).
            ReportLocalInRoom(arenaPos);
        }

        /// <summary>HOST: a gate near an arena re-opened (AllDeadTrigger = all enemies dead / boss died). Release the
        /// lockdown so any still-out-of-room players teleport in and the barrier drops. Generic for MetalGate arenas;
        /// SetActive-door arenas (Lucia) rely on the confirm path / scene-change cleanup.</summary>
        public static void OnGateOpened(Vector3 gatePos)
        {
            if (!Enabled || !NetGameplaySyncBridge.IsHost || _locks.Count == 0) return;
            try
            {
                Lockdown best = null; float bestSqr = GateReleaseRadius * GateReleaseRadius;
                foreach (var kv in _locks)
                {
                    if (kv.Value.Released) continue;
                    float sqr = (kv.Value.Pos - gatePos).sqrMagnitude;
                    if (sqr <= bestSqr) { bestSqr = sqr; best = kv.Value; }
                }
                if (best == null) return;
                best.Released = true;
                NetLogger.Info($"[ArenaLockdown] gate-open near arena={Key(best.Pos)} → RELEASE");
                IssueCommand(best, ArenaCommandKind.Release);
            }
            catch (Exception ex) { NetLogger.Warn($"[ArenaLockdown] OnGateOpened failed: {ex.Message}"); }
        }

        /// <summary>Ends in the arena's level that have NOT crossed (the seal/teleport targets). Real-time: a late
        /// crosser has since been added to InRoom and drops out here.</summary>
        private static List<string> ComputeNonInRoom(Lockdown ld)
        {
            var endsInLevel = NetGameplaySyncBridge.GetPeerIdsInLevel(ld.Chapter, ld.Level, ld.HasSeed, ld.Seed);
            return endsInLevel.Where(id => !ld.InRoom.Contains(id)).ToList();
        }

        // ----------------------------------------------------------------- local player resolution

        private static object ResolveLocalPlayerUnit()
        {
            try
            {
                Type gmType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.GameManager");
                object gm = gmType == null ? null : AccessTools.Property(gmType, "Instance")?.GetValue(null, null);
                if (gm == null) return null;
                object pu = AccessTools.Property(gmType, "PlayerUnit")?.GetValue(gm, null);
                if (pu is UnityEngine.Object uo && uo == null) return null;
                return pu;
            }
            catch { return null; }
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

        // Scene change — drop previous level's lockdowns + barriers + armed state.
        public static void Clear()
        {
            _locks.Clear();
            ArenaBarrierManager.Clear();
            _armed = false;
            if (HidePrompt != null) { try { HidePrompt(); } catch { } }
        }
    }
}
