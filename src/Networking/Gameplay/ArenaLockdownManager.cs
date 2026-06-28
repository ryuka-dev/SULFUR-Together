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

        // LD-2d grace: arenas (key→trigger pos) where THIS end is holding its combat-room gate OPEN during the grace
        // window so teammates can still walk in. Closed (for real) when the host's CloseDoor command lands at t0+5 s.
        private static readonly Dictionary<string, Vector3> _localGrace = new Dictionary<string, Vector3>();

        // LD-2e: arenas (key) whose seal trigger THIS end's local player has crossed at least once (fallback in/out when
        // no doorway sensor data exists).
        private static readonly HashSet<string> _localCrossed = new HashSet<string>();

        // LD-2e: per-arena count of doorway traversals by THIS end's local player (ArenaDoorwaySensor). Odd = inside.
        private static readonly Dictionary<string, int> _doorwayCrossings = new Dictionary<string, int>();

        private const float SealDelaySeconds     = 5f;
        private const float TeleportDelaySeconds = 10f;
        // How close a gate-open (boss death / AllDeadTrigger) must be to an arena to count as "this fight ended".
        private const float GateReleaseRadius    = 10f;
        // Trigger→gate proximity: the seal trigger and the gate it controls are co-located but distinct objects.
        private const float GraceGateRadius      = 12f;

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

        /// <summary>LD-2c transient status toast seam (title, message) for the lockdown wait — a teammate entered /
        /// you've been sealed / entering. Defaults to logging only; SULFUR Native UI Lib's SulfurToastApi can be
        /// wired in. PLAYER-FACING text → must be localized (see Docs/Localization.md).</summary>
        public static Action<string, string> ShowToast;

        private static bool Enabled
        {
            get { try { return Plugin.Cfg.EnableArenaLockdown.Value; } catch { return false; } }
        }
        private static bool LogOn
        {
            get { try { return Plugin.Cfg.LogArenaLockdown.Value; } catch { return false; } }
        }
        private static bool GraceEnabled
        {
            get { try { return Plugin.Cfg.EnableArenaGracePeriod.Value; } catch { return false; } }
        }

        // ----------------------------------------------------------------- LD-2d grace (keep the gate open ~5 s)

        /// <summary>PlayerTrigger.Trigger PREFIX: if this is a seal trigger, start a local grace window BEFORE the
        /// trigger's events fire, so the same-frame <c>MetalGate.Close()</c> is blocked (the gate stays open until the
        /// host's t0+5 s CloseDoor). Reports in-room stays in the postfix (OnLocalTriggerFired) — grace only defers the
        /// door, not membership.</summary>
        public static void BeginLocalGraceIfSeal(object trigger)
        {
            try
            {
                if (!Enabled || !GraceEnabled || !NetGameplaySyncBridge.IsSessionActive) return;
                if (!IsSealTrigger(trigger, out Vector3 pos)) return;
                string key = Key(pos);
                if (_localGrace.ContainsKey(key)) return;
                _localGrace[key] = pos;
                if (LogOn) NetLogger.Info($"[ArenaLockdown] grace begin arena={key} (gate held open ~{SealDelaySeconds:0}s)");
            }
            catch (Exception ex) { NetLogger.Warn($"[ArenaLockdown] BeginLocalGrace failed: {ex.Message}"); }
        }

        /// <summary>MetalGate.Close PREFIX: true if a grace window is active near this gate (→ block the close so the
        /// gate stays open during grace).</summary>
        public static bool IsGateInLocalGrace(Vector3 gatePos)
        {
            if (_localGrace.Count == 0) return false;
            float r2 = GraceGateRadius * GraceGateRadius;
            foreach (var kv in _localGrace)
                if ((kv.Value - gatePos).sqrMagnitude <= r2) return true;
            return false;
        }

        private static void EndLocalGrace(Vector3 arenaPos)
        {
            string key = Key(arenaPos);
            if (_localGrace.Remove(key) && LogOn) NetLogger.Info($"[ArenaLockdown] grace end arena={key}");
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
                _localCrossed.Add(Key(pos)); // LD-2e: remember this end's player crossed (gates the t+5/t+10 in/out check)
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
            bool created = false;
            if (!_locks.TryGetValue(key, out var ld))
            {
                ld = new Lockdown { Pos = pos, T0 = Time.unscaledTime, Chapter = chap, Level = lvl, HasSeed = hasSeed, Seed = seed };
                _locks[key] = ld;
                created = true;
                NetLogger.Info($"[ArenaLockdown] START arena={key} level={chap}:{lvl} seed={(hasSeed ? seed.ToString() : "?")} t0 by {peerId}");
            }
            if (ld.InRoom.Add(peerId))
                NetLogger.Info($"[ArenaLockdown] in-room += {peerId} arena={key} members=[{string.Join(",", ld.InRoom)}]");

            // t0: heads-up toasts — the first crosser(s) get NotifyEntered ("you started it"), everyone else gets
            // Notify ("a teammate entered — come in"). Issued once, when the lockdown is created.
            if (created)
            {
                IssueCommand(ld, ArenaCommandKind.NotifyEntered);
                IssueCommand(ld, ArenaCommandKind.Notify);
            }
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
                    // Grace over: close the gate that was held open for everyone in the level, then barrier the out-of-room.
                    if (GraceEnabled) IssueCommand(ld, ArenaCommandKind.CloseDoor);
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
            // CloseDoor/Seal/Popup/Release go to EVERY end in the level — each self-decides in/out by its own local
            // player position (LD-2e real-time check), so a player who left within the window is sealed/offered the popup.
            // NotifyEntered → the first crosser(s); Notify → everyone not yet in-room.
            List<string> targets;
            switch (kind)
            {
                case ArenaCommandKind.CloseDoor:
                case ArenaCommandKind.Seal:
                case ArenaCommandKind.Popup:
                case ArenaCommandKind.Release:
                    targets = ComputeAllInLevel(ld); break;
                case ArenaCommandKind.NotifyEntered:
                    targets = ld.InRoom.ToList(); break;
                default: // Notify
                    targets = ComputeNonInRoom(ld); break;
            }
            if (targets.Count == 0)
            {
                if (LogOn) NetLogger.Info($"[ArenaLockdown] {kind} arena={Key(ld.Pos)} — no targets, skip");
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
                    case ArenaCommandKind.Notify:
                        // t0 heads-up to the OUT-OF-ROOM players: with grace the door stays open ~5 s, so invite them in.
                        // Player-facing → localize (Docs/Localization.md).
                        Toast("Arena Lockdown", "A teammate entered the arena — head in now to join them!");
                        break;

                    case ArenaCommandKind.NotifyEntered:
                        // t0 heads-up to the player(s) who entered first, so they know the lockdown started too.
                        // Player-facing → localize.
                        Toast("Arena Lockdown", "You entered the arena — the gate seals in a few seconds; teammates can still run in.");
                        break;

                    case ArenaCommandKind.CloseDoor:
                        // Grace over: end the local hold and close the door for real (replay the trigger's seal action
                        // on its actual door target — robust regardless of gate registry/position).
                        EndLocalGrace(arenaPos);
                        int closedDoors = ArenaBarrierManager.CloseArenaDoorsLocal(arenaPos);
                        if (LogOn) NetLogger.Info($"[ArenaLockdown] CloseDoor arena={Key(arenaPos)} closedDoors={closedDoors}");
                        break;

                    case ArenaCommandKind.Seal:
                        // LD-2e: only seal if THIS end's player is actually outside (didn't cross, or crossed then left).
                        if (IsEffectivelyInArena(arenaPos)) { if (LogOn) NetLogger.Info($"[ArenaLockdown] Seal arena={Key(arenaPos)} local player inside — no barrier"); break; }
                        ArenaBarrierManager.Seal(arenaPos);
                        // Explain the otherwise-invisible barrier. Player-facing → localize.
                        Toast("Arena Lockdown", "You've been sealed out — you'll be brought in shortly.");
                        break;

                    case ArenaCommandKind.Popup:
                        // LD-2e: only the players actually outside get the confirm popup (+ ensure their barrier is up).
                        if (IsEffectivelyInArena(arenaPos)) { if (LogOn) NetLogger.Info($"[ArenaLockdown] Popup arena={Key(arenaPos)} local player inside — no popup"); break; }
                        if (!ArenaBarrierManager.IsSealed(arenaPos)) ArenaBarrierManager.Seal(arenaPos);
                        _armed = true;
                        _armedArena = arenaPos;
                        string keyName = "?";
                        try { keyName = Plugin.Cfg.ArenaEnterConfirmKey.Value.ToString(); } catch { }
                        string text = $"Press [{keyName}] to enter the arena";
                        if (ShowPrompt != null) { try { ShowPrompt(text); } catch (Exception ex) { NetLogger.Warn($"[ArenaLockdown] ShowPrompt failed: {ex.Message}"); } }
                        else NetLogger.Info($"[ArenaLockdown] POPUP armed teleport (UI deferred to Native UI Lib) — {text}");
                        break;

                    case ArenaCommandKind.Release:
                        if (IsEffectivelyInArena(arenaPos)) break; // already inside — nothing to do
                        if (LogOn) NetLogger.Info($"[ArenaLockdown] RELEASE arena=({arenaPos.x:0.0},{arenaPos.y:0.0},{arenaPos.z:0.0}) — fight over, entering");
                        TeleportIntoArena(arenaPos);
                        break;
                }
            }
            catch (Exception ex) { NetLogger.Warn($"[ArenaLockdown] ApplyLocalCommand({kind}) failed: {ex.Message}"); }
        }

        /// <summary>Show a transient status toast (Native UI Lib if wired, else log). Text is PLAYER-FACING — keep it
        /// in the localization registry (Docs/Localization.md).</summary>
        private static void Toast(string title, string message)
        {
            if (ShowToast != null) { try { ShowToast(title, message); return; } catch (Exception ex) { NetLogger.Warn($"[ArenaLockdown] ShowToast failed: {ex.Message}"); } }
            if (LogOn) NetLogger.Info($"[ArenaLockdown] toast (UI deferred): {title} — {message}");
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
                    // Player-facing → localize (Docs/Localization.md).
                    Toast("Arena", "Entering the arena.");
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
            // Now inside (teleported, not walked through the door): mark crossed + force parity ODD = inside, so a later
            // walk-OUT toggles to outside correctly. Then tell the host.
            string k = Key(arenaPos);
            _localCrossed.Add(k);
            _doorwayCrossings.TryGetValue(k, out int dc);
            if (dc % 2 == 0) _doorwayCrossings[k] = dc + 1;
            ReportLocalInRoom(arenaPos);
            // RM-2b: teleported into the arena counts as entering the boss room — catch up the intro dialog if its
            // session is still active (usually the fight already started by now, so this is a no-op then).
            try { Boss.NetBossEncounterManager.OnLocalTeleportedIntoArena(); } catch { }
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

        /// <summary>Every end in the arena's level (CloseDoor/Seal/Popup/Release targets — each self-decides in/out).</summary>
        private static List<string> ComputeAllInLevel(Lockdown ld)
            => NetGameplaySyncBridge.GetPeerIdsInLevel(ld.Chapter, ld.Level, ld.HasSeed, ld.Seed);

        /// <summary>LD-2e real-time, event-driven in/out via DOORWAY-CROSSING PARITY (user-chosen): the local player is
        /// inside iff it has traversed this arena's doorway an odd number of times (in / out / in …). Counted by
        /// <see cref="ArenaDoorwaySensor"/> on the seal trigger — a pure event, independent of distance / arena shape.
        /// Evaluated when a command lands (t+5 seal, t+10 popup, boss-death release). Falls back to "did the local player
        /// cross at all" only if no sensor data exists (e.g. the seal trigger had no Start hook).</summary>
        private static bool IsEffectivelyInArena(Vector3 arenaPos)
        {
            string key = Key(arenaPos);
            if (_doorwayCrossings.TryGetValue(key, out int count))
                return (count % 2) == 1; // odd = inside, even = outside (authoritative when known)
            return _localCrossed.Contains(key); // no sensor data → best-effort: crossed once ⇒ assume inside
        }

        /// <summary>Called by <see cref="ArenaDoorwaySensor"/> each time the local player fully passes through the
        /// doorway. Toggles the inside/outside parity for that arena.</summary>
        public static void OnLocalDoorwayTraversed(string arenaKey, Vector3 arenaPos)
        {
            _doorwayCrossings.TryGetValue(arenaKey, out int c);
            c += 1;
            _doorwayCrossings[arenaKey] = c;
            if (LogOn) NetLogger.Info($"[ArenaLockdown] doorway traversal arena={arenaKey} count={c} inside={(c % 2 == 1)}");
        }

        /// <summary>Local player's root transform (for the doorway sensor to match its own player and ignore remote ghosts).</summary>
        public static Transform LocalPlayerRoot()
        {
            try { return ResolveLocalPlayerUnit() is Component c && c != null ? c.transform.root : null; }
            catch { return null; }
        }

        /// <summary>PlayerTrigger.Start hook: if this is a seal trigger, attach a doorway sensor so traversals are counted
        /// from the very first crossing (parity starts at 0 = outside).</summary>
        public static void AttachDoorwaySensorIfSeal(object trigger)
        {
            try
            {
                if (!Enabled || !NetGameplaySyncBridge.IsSessionActive) return;
                if (!(trigger is Component c) || c == null) return;
                if (!IsSealTrigger(trigger, out Vector3 pos)) return;
                if (c.GetComponent<ArenaDoorwaySensor>() != null) return; // already attached
                var sensor = c.gameObject.AddComponent<ArenaDoorwaySensor>();
                sensor.ArenaKey = Key(pos);
                sensor.ArenaPos = pos;
                if (LogOn) NetLogger.Info($"[ArenaLockdown] doorway sensor attached arena={Key(pos)}");
            }
            catch (Exception ex) { NetLogger.Warn($"[ArenaLockdown] AttachDoorwaySensor failed: {ex.Message}"); }
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
            _localGrace.Clear();
            _localCrossed.Clear();
            _doorwayCrossings.Clear();
            ArenaBarrierManager.Clear();
            _armed = false;
            if (HidePrompt != null) { try { HidePrompt(); } catch { } }
        }
    }
}
