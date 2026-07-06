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

        // LD-Sandstorm (Desert): a GATE-LESS arena. There is no seal trigger / door and no barrier — the sandstorm
        // damage ring is the wall. The fight-start (boss dialog trigger) creates one of these; at t0+3 s the host tells
        // every end in the level to pull its own local player in IF it is outside the arena radius. In/out is decided
        // per-end by distance to the centre (no doorway sensor). Keyed by the arena centre position.
        private sealed class SandstormArena
        {
            public Vector3 Pos;
            public float   T0;
            public string  Chapter = ""; public int Level = -1; public bool HasSeed; public int Seed;
            public bool    PullFired;
        }
        private static readonly Dictionary<string, SandstormArena> _sandstormArenas = new Dictionary<string, SandstormArena>();

        // LD-2d grace + LD-2f mod-owned door, tracked by the GATE's InstanceID — NOT by position. The seal trigger can
        // be ~50 m from the gate it drives (Emperor: dialog/seal trigger deep inside, gate at the entrance), so a
        // position radius can't identify the gate; object identity can (the id the trigger's onTriggerEvents targets is
        // the same id the MetalGate.Close/Open prefix sees, and the same gate a nearby open-door trigger reopens).
        //   _gracedGates: arenaKey → gate ids held OPEN during the ~5 s grace (Close blocked); moved to _heldGates at t0+5.
        //   _heldGates:   arenaKey → gate ids the mod holds CLOSED post-seal (Open blocked) until release.
        private static readonly Dictionary<string, HashSet<int>> _gracedGates = new Dictionary<string, HashSet<int>>();
        private static readonly Dictionary<string, HashSet<int>> _heldGates   = new Dictionary<string, HashSet<int>>();

        // The one legit reopen is "all enemies dead" (AllDeadTrigger → gate Open → release). CheckAllDead sets this
        // short window so that open passes the hold; every other open while held is blocked. Generic: AllDeadTrigger is
        // the standard MetalGate-room reopen (covers Cousin etc.); an arena with no AllDeadTrigger (Emperor) simply
        // stays closed until scene-change Clear — which is exactly "the mod keeps it closed."
        private static float _legitGateOpenUntil = -999f;
        private const float LegitGateOpenWindowSeconds = 2f;

        // LD-2e: arenas (key) whose seal trigger THIS end's local player has crossed at least once (fallback in/out when
        // no doorway sensor data exists).
        private static readonly HashSet<string> _localCrossed = new HashSet<string>();

        // LD-2e: per-arena count of doorway traversals by THIS end's local player (ArenaDoorwaySensor). Odd = inside.
        private static readonly Dictionary<string, int> _doorwayCrossings = new Dictionary<string, int>();

        // RT3-Cousin-arms-Room: the active arena (most recent membership change) + the client's cached view of the host's
        // broadcast in-room set. Used by the Cousin arm group-attack to skip out-of-room players. Unlike the boss-trigger
        // room membership (seed-keyed, churns), ArenaLockdown's set is keyed by stable arena position and accumulates
        // everyone incl. late walk-ins, so it is the reliable source for "who is in the boss arena".
        private static string _activeArenaKey = "";
        private static readonly HashSet<string> _clientArenaMembers = new HashSet<string>();
        private static string _clientArenaKey = "";

        private const float SealDelaySeconds     = 5f;
        private const float TeleportDelaySeconds = 10f;
        // LD-Sandstorm: how long after the boss dialog trigger before out-of-room players are pulled into the arena,
        // and how far from the centre still counts as "in" (matches vanilla DesertClause's >20 m keep-in threshold).
        private const float SandstormPullDelaySeconds = 3f;
        // Fallback radius only — the real in/out test uses the live DesertClausePerimeter sphere (centre + SphereRadius).
        private const float SandstormArenaRadius      = 20f;
        // How far BESIDE the first in-arena player pulled-in stragglers land (outward from the sphere centre, so they
        // don't drop onto the boss which sits at the centre).
        private const float SandstormPullBesideOffset = 3f;
        // Gate-lockdown (no sphere): how close a teammate must be to the seal trigger to count as "in the arena" and
        // serve as the valid-floor teleport anchor. Generous — the trigger and the arena floor are co-located.
        private const float GateEntryAnchorRadius = 25f;
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
                if (_gracedGates.ContainsKey(key)) return;
                var ids = ArenaBarrierManager.ResolveMetalGateIds(trigger); // the gate this trigger drives (id, not pos)
                _gracedGates[key] = new HashSet<int>(ids);
                if (LogOn) NetLogger.Info($"[ArenaLockdown] grace begin arena={key} gates=[{string.Join(",", ids)}] (held open ~{SealDelaySeconds:0}s)");
            }
            catch (Exception ex) { NetLogger.Warn($"[ArenaLockdown] BeginLocalGrace failed: {ex.Message}"); }
        }

        /// <summary>MetalGate.Close PREFIX: true if THIS gate (by id) is being held OPEN during a grace window (→ block
        /// the close so the gate stays open until the host's t0+5 s CloseDoor).</summary>
        public static bool IsGateGraced(int gateId)
        {
            foreach (var kv in _gracedGates) if (kv.Value.Contains(gateId)) return true;
            return false;
        }

        private static void EndLocalGrace(Vector3 arenaPos)
        {
            string key = Key(arenaPos);
            if (_gracedGates.Remove(key) && LogOn) NetLogger.Info($"[ArenaLockdown] grace end arena={key}");
        }

        // ----------------------------------------------------------------- LD-2f mod-owned door (keep closed until release)

        /// <summary>MetalGate.Open PREFIX query: true if THIS gate (by id) is being held CLOSED by the mod (post-seal),
        /// so a spurious reopen should be blocked.</summary>
        public static bool IsGateHeld(int gateId)
        {
            foreach (var kv in _heldGates) if (kv.Value.Contains(gateId)) return true;
            return false;
        }

        /// <summary>True during the brief window after all enemies died (AllDeadTrigger) — the ONE legit gate reopen,
        /// which must pass the hold so the arena releases normally.</summary>
        public static bool IsLegitGateOpenWindow() => Time.unscaledTime <= _legitGateOpenUntil;

        /// <summary>AllDeadTrigger.CheckAllDead detected all enemies dead → it is about to Open the gate. Open a short
        /// window so that (and only that) open passes the mod door-hold.</summary>
        public static void NotifyAllEnemiesDead()
        {
            _legitGateOpenUntil = Time.unscaledTime + LegitGateOpenWindowSeconds;
            if (LogOn) NetLogger.Info($"[ArenaLockdown] all-enemies-dead → legit gate-open window ({LegitGateOpenWindowSeconds:0}s)");
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
            {
                NetLogger.Info($"[ArenaLockdown] in-room += {peerId} arena={key} members=[{string.Join(",", ld.InRoom)}]");
                _activeArenaKey = key;
                BroadcastArenaMembership(ld); // RT3-Cousin-arms-Room: tell clients the new in-room set (for arm filtering)
            }

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
            TickSandstormDownedRescue();
        }

        // LD-Sandstorm downed rescue: a DOWNED local player left outside the moving sandstorm sphere for this long is
        // teleported in beside the group. Alive out-of-arena players are deliberately NOT dragged along (the native fight
        // teleports everyone with the moving arena; in co-op an alive straggler is left to walk).
        private const float DownedRescueSeconds = 5f;
        private static float _downedOutsideSince;

        /// <summary>EVERY end, per frame: while the Desert sandstorm fight is running, a local player who is DOWNED and
        /// outside the (moving) danger sphere for <see cref="DownedRescueSeconds"/> is pulled in beside the group — a
        /// downed body abandoned outside the ring can never be revived. Reuses the PullIn apply (self in/out check,
        /// teleport, toast, dialog catch-up). The timer resets whenever the player is inside, revived, or the fight ends.</summary>
        private static void TickSandstormDownedRescue()
        {
            try
            {
                if (!Boss.NetBossEncounterManager.TryGetActiveSandstormArenaSphere(out var center, out float radius) || radius <= 0f)
                { _downedOutsideSince = 0f; return; }
                if (!NetPlayerLifeManager.ShouldSuppressLocalPlayerControls()) { _downedOutsideSince = 0f; return; } // not downed
                object pu = ResolveLocalPlayerUnit();
                if (!(pu is Component pc) || pc == null) { _downedOutsideSince = 0f; return; }
                float dist = Vector3.Distance(pc.transform.position, center);
                if (dist <= radius) { _downedOutsideSince = 0f; return; } // downed but inside — teammates can reach them
                float now = Time.unscaledTime;
                if (_downedOutsideSince <= 0f) { _downedOutsideSince = now; return; }
                if (now - _downedOutsideSince < DownedRescueSeconds) return;
                _downedOutsideSince = 0f;
                NetLogger.Info($"[ArenaLockdown] downed rescue: local player downed OUTSIDE the sandstorm for {DownedRescueSeconds:0}s (dist={dist:0.0}m r={radius:0.0}) — pulling in");
                ApplyLocalCommand(ArenaCommandKind.PullIn, ResolveNearFirstPlayerTarget(center));
            }
            catch { _downedOutsideSince = 0f; }
        }

        // ----------------------------------------------------------------- LD-Sandstorm (Desert): gate-less keep-in

        /// <summary>HOST: a gate-less sandstorm boss (Desert) started — its dialog trigger fired. Register the arena so
        /// that <see cref="SandstormPullDelaySeconds"/> later every out-of-room player in the level is pulled to the
        /// centre. Called once per encounter from the boss-start broadcast. No-op off-host / feature-disabled.</summary>
        public static void BeginSandstormArena(Vector3 center, string chapter, int level, bool hasSeed, int seed)
        {
            try
            {
                if (!Enabled || !NetGameplaySyncBridge.IsHost || !NetGameplaySyncBridge.IsSessionActive) return;
                string key = Key(center);
                if (_sandstormArenas.ContainsKey(key)) return; // already armed for this arena
                _sandstormArenas[key] = new SandstormArena
                {
                    Pos = center, T0 = Time.unscaledTime, Chapter = chapter, Level = level, HasSeed = hasSeed, Seed = seed,
                };
                NetLogger.Info($"[ArenaLockdown] sandstorm arena START arena={key} level={chapter}:{level} pull in {SandstormPullDelaySeconds:0}s");
            }
            catch (Exception ex) { NetLogger.Warn($"[ArenaLockdown] BeginSandstormArena failed: {ex.Message}"); }
        }

        private static void HostTickSandstorm()
        {
            if (_sandstormArenas.Count == 0) return;
            float now = Time.unscaledTime;
            foreach (var kv in _sandstormArenas)
            {
                var sa = kv.Value;
                if (sa.PullFired) continue;
                if (now - sa.T0 >= SandstormPullDelaySeconds)
                {
                    sa.PullFired = true;
                    IssueSandstormPull(sa);
                }
            }
        }

        /// <summary>HOST: tell every end in the arena's level to pull its own local player in if it is outside the
        /// sandstorm sphere. Reuses the LD command transport; each end self-decides in/out against its own live sphere
        /// (PullIn). The command carries the TELEPORT TARGET (near the first in-arena player), not the sphere centre.</summary>
        private static void IssueSandstormPull(SandstormArena sa)
        {
            var targets = NetGameplaySyncBridge.GetPeerIdsInLevel(sa.Chapter, sa.Level, sa.HasSeed, sa.Seed);
            if (targets.Count == 0)
            {
                if (LogOn) NetLogger.Info($"[ArenaLockdown] PullIn arena={Key(sa.Pos)} — no targets, skip");
                return;
            }
            Vector3 tpTarget = ResolveNearFirstPlayerTarget(sa.Pos);
            NetLogger.Info($"[ArenaLockdown] PullIn arena={Key(sa.Pos)} target={tpTarget:F0} targets=[{string.Join(",", targets)}] (out-of-arena players teleport in)");

            if (targets.Contains(NetGameplaySyncBridge.LocalPeerId))
                ApplyLocalCommand(ArenaCommandKind.PullIn, tpTarget);

            NetGameplaySyncBridge.BroadcastArenaCommand(new NetArenaCommand
            {
                Kind = ArenaCommandKind.PullIn, ArenaPos = tpTarget, TargetPeerIds = targets,
            });
        }

        /// <summary>HOST: pick a teleport target for pulled-in stragglers — a spot BESIDE the first player already in the
        /// arena, so they land with the group rather than on the boss at the sphere centre. "First player" is approximated
        /// as the player nearest the sphere centre that is inside the sphere (the one fighting the boss). Falls back to the
        /// sphere centre if nobody is inside yet.</summary>
        private static Vector3 ResolveNearFirstPlayerTarget(Vector3 fallbackCenter)
        {
            Vector3 center = fallbackCenter; float radius = SandstormArenaRadius;
            if (Boss.NetBossEncounterManager.TryGetSandstormArenaSphere(out var c, out var r) && r > 0f) { center = c; radius = r; }
            // includeLocal: the sandstorm pull is computed HOST-side and the host may itself be the in-arena player;
            // an out-of-arena player is naturally excluded (dist > radius). Fallback = centre (sandstorm has no floor issue).
            return ResolveArenaEntryTarget(center, radius, includeLocal: true, out _);
        }

        /// <summary>Pick a teleport target BESIDE the nearest player already inside <paramref name="radius"/> of
        /// <paramref name="center"/> — their feet are on valid standing floor, and the outward offset keeps stragglers
        /// off the boss (which sits at the centre) and off each other. <paramref name="foundAnchor"/> is false when no
        /// eligible player is inside yet → the caller decides the fallback. <paramref name="includeLocal"/> excludes THIS
        /// end's player when it is the one being pulled in (its own position is outside / possibly below floor).</summary>
        private static Vector3 ResolveArenaEntryTarget(Vector3 center, float radius, bool includeLocal, out bool foundAnchor)
        {
            Vector3 anchor = Vector3.zero; bool found = false; float bestSqr = float.MaxValue; float r2 = radius * radius;
            void Consider(Vector3 p)
            {
                float d2 = (p - center).sqrMagnitude;
                if (d2 <= r2 && d2 < bestSqr) { bestSqr = d2; anchor = p; found = true; }
            }
            if (includeLocal)
                try { if (ResolveLocalPlayerUnit() is Component hc && hc != null) Consider(hc.transform.position); } catch { }
            try { NetGameplaySyncBridge.ForEachRemotePlayerPositionWithPeer((peer, pos) => Consider(pos)); } catch { }

            foundAnchor = found;
            if (!found) return center; // nobody inside yet → centre

            Vector3 outward = anchor - center; outward.y = 0f;
            if (outward.sqrMagnitude < 0.01f) outward = Vector3.forward; // anchor at centre → arbitrary horizontal dir
            return anchor + outward.normalized * SandstormPullBesideOffset;
        }

        /// <summary>Gate-lockdown teleport destination. The seal trigger's own pivot Y is often BELOW the standing floor,
        /// so teleporting straight to it (the old <c>arenaPos + up*0.5</c>) drops the player through the map. Prefer to
        /// land beside a teammate already in the arena (valid floor — the host is in-room by the time a client confirms),
        /// else raycast down onto the floor, else fall back to the trigger pos.</summary>
        private static Vector3 ResolveGateTeleportDest(Vector3 arenaPos)
        {
            // Exclude the local player: it is the one being pulled in (outside, maybe below floor). Only teammates anchor.
            Vector3 beside = ResolveArenaEntryTarget(arenaPos, GateEntryAnchorRadius, includeLocal: false, out bool found);
            if (found) return beside + Vector3.up * 0.5f;
            // Solo / everyone entering at once: raycast straight down onto the floor (ignore trigger colliders).
            if (Physics.Raycast(arenaPos + Vector3.up * 30f, Vector3.down, out RaycastHit hit, 60f,
                                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                return hit.point + Vector3.up * 0.1f;
            return arenaPos + Vector3.up * 0.5f; // last resort (may be slightly low, but better than nothing)
        }

        private static void HostTick()
        {
            HostTickSandstorm();
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
            // RT3-Cousin-arms-Room: membership broadcast — cached by EVERY client (no per-target side effect), so the
            // arm group-attack can tell which remote players are in the boss arena.
            if (m.Kind == ArenaCommandKind.Membership)
            {
                _clientArenaKey = Key(m.ArenaPos);
                _clientArenaMembers.Clear();
                if (m.TargetPeerIds != null) foreach (var p in m.TargetPeerIds) _clientArenaMembers.Add(p);
                if (LogOn) NetLogger.Info($"[ArenaLockdown] client received membership arena={_clientArenaKey} members=[{string.Join(",", _clientArenaMembers)}]");
                return;
            }
            // LD-Sandstorm diagnostic: log PullIn receipt UNCONDITIONALLY (once per fight) so we can tell "command never
            // arrived" from "arrived but not a target" from "arrived + processed".
            if (m.Kind == ArenaCommandKind.PullIn)
            {
                bool targeted = m.TargetPeerIds != null && m.TargetPeerIds.Contains(localPeerId);
                NetLogger.Info($"[ArenaLockdown] client received PullIn arena={Key(m.ArenaPos)} me={localPeerId} targets=[{(m.TargetPeerIds == null ? "" : string.Join(",", m.TargetPeerIds))}] targeted={targeted}");
            }
            if (m.TargetPeerIds == null || !m.TargetPeerIds.Contains(localPeerId)) return;
            ApplyLocalCommand(m.Kind, m.ArenaPos);
        }

        /// <summary>HOST: broadcast an arena's current in-room peer set so clients can filter the boss arm's group attack.</summary>
        private static void BroadcastArenaMembership(Lockdown ld)
        {
            try
            {
                NetGameplaySyncBridge.BroadcastArenaCommand(new NetArenaCommand
                {
                    Kind = ArenaCommandKind.Membership, ArenaPos = ld.Pos, TargetPeerIds = ld.InRoom.ToList(),
                });
            }
            catch (Exception ex) { NetLogger.Warn($"[ArenaLockdown] BroadcastArenaMembership failed: {ex.Message}"); }
        }

        // ----------------------------------------------------------------- RT3-Cousin-arms-Room: arm-attack room queries

        /// <summary>The in-room peer set of the active boss arena (host: authoritative live set; client: last host
        /// broadcast). Returns false when no active arena membership is known — the caller then fail-opens (no room
        /// filtering) so the boss can never become un-attackable. Members may include "host" and client peer ids.</summary>
        public static bool TryGetActiveArenaInRoom(out HashSet<string> members)
        {
            members = null;
            try
            {
                if (!Enabled) return false;
                if (NetGameplaySyncBridge.IsHost)
                {
                    if (!string.IsNullOrEmpty(_activeArenaKey) && _locks.TryGetValue(_activeArenaKey, out var ld) && !ld.Released && ld.InRoom.Count > 0)
                    { members = ld.InRoom; return true; }
                    // Fallback: the most-recent non-released lock that has members.
                    Lockdown best = null;
                    foreach (var kv in _locks) { var l = kv.Value; if (l.Released || l.InRoom.Count == 0) continue; if (best == null || l.T0 > best.T0) best = l; }
                    if (best != null) { members = best.InRoom; return true; }
                    return false;
                }
                if (_clientArenaMembers.Count > 0) { members = _clientArenaMembers; return true; }
                return false;
            }
            catch { return false; }
        }


        /// <summary>TB-D: is THIS end's local player in the active boss arena? Reads the host-authoritative in-room set
        /// (client: last host broadcast) and tests the local peer id. Fail-OPEN (true) when no arena membership is known,
        /// so a caller gating a player-facing thing (a boss dialog) never wrongly hides it when the arena is unknown.</summary>
        public static bool IsLocalPlayerInActiveArena()
        {
            try
            {
                if (!TryGetActiveArenaInRoom(out var members) || members == null) return true; // unknown → don't filter
                return members.Contains(NetGameplaySyncBridge.LocalPeerId);
            }
            catch { return true; }
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
                        EndLocalGrace(arenaPos); // clears the grace hold so the close below isn't blocked
                        int closedDoors = ArenaBarrierManager.CloseArenaDoorsLocal(arenaPos);
                        // LD-2f: from now the mod OWNS this door — hold the gate (by id) closed until release.
                        var heldIds = ArenaBarrierManager.ResolveMetalGateIdsFromArena(arenaPos);
                        if (heldIds.Count > 0) _heldGates[Key(arenaPos)] = new HashSet<int>(heldIds);
                        if (LogOn) NetLogger.Info($"[ArenaLockdown] CloseDoor arena={Key(arenaPos)} closedDoors={closedDoors} heldGates=[{string.Join(",", heldIds)}] (door now held closed)");
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
                        // LD-2f: fight over — the mod releases the door (stop blocking reopens for this arena).
                        _heldGates.Remove(Key(arenaPos));
                        _gracedGates.Remove(Key(arenaPos));
                        if (IsEffectivelyInArena(arenaPos)) break; // already inside — nothing to do
                        if (LogOn) NetLogger.Info($"[ArenaLockdown] RELEASE arena=({arenaPos.x:0.0},{arenaPos.y:0.0},{arenaPos.z:0.0}) — fight over, entering");
                        TeleportIntoArena(arenaPos);
                        break;

                    case ArenaCommandKind.PullIn:
                        // LD-Sandstorm (Desert): no gate/barrier — pull the local player in ONLY if it is outside the
                        // danger sphere. In/out is tested against THIS end's LIVE moving sphere (centre + SphereRadius,
                        // the game's own out-of-bounds test), not a hardcoded radius. arenaPos here is the TELEPORT TARGET
                        // (a spot near the first in-arena player, computed by the host) — NOT the sphere centre — because
                        // the boss sits at the centre, so we drop stragglers beside the group instead of on the boss.
                        // Decision logged UNCONDITIONALLY (once per player per fight) to diagnose without LogArenaLockdown.
                        {
                            object pu = ResolveLocalPlayerUnit();
                            if (pu == null)
                            {
                                NetLogger.Info($"[ArenaLockdown] PullIn — local player unit MISSING, cannot teleport");
                                break;
                            }
                            Vector3 ppos = (pu is Component pc && pc != null) ? pc.transform.position : Vector3.positiveInfinity;
                            Vector3 center; float radius;
                            if (!Boss.NetBossEncounterManager.TryGetSandstormArenaSphere(out center, out radius) || radius <= 0f)
                            { center = arenaPos; radius = SandstormArenaRadius; } // fallback: no live sphere → target + const
                            float dist = Vector3.Distance(ppos, center);
                            if (dist <= radius)
                            {
                                NetLogger.Info($"[ArenaLockdown] PullIn local player INSIDE sphere (dist={dist:0.0}m r={radius:0.0} ppos={ppos:F0}) — no teleport");
                                break;
                            }
                            Vector3 dest = arenaPos + Vector3.up * 0.5f;
                            NetLogger.Info($"[ArenaLockdown] PullIn local player OUTSIDE sphere (dist={dist:0.0}m r={radius:0.0} ppos={ppos:F0}) — teleporting to {dest:F0} (near first player)");
                            MoveLocalPlayerTo(pu, dest);
                            // Player-facing → localize (Docs/Localization.md).
                            Toast("Sandstorm Arena", "Pulled into the arena — the sandstorm outside would grind you down.");
                            // Teleported in counts as entering the boss room — catch up the intro dialog if still open.
                            try { Boss.NetBossEncounterManager.OnLocalTeleportedIntoArena(); } catch { }
                        }
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
                    // Land on valid floor (beside a teammate / raycast to ground), NOT the seal trigger pivot whose Y is
                    // often below the floor → the player would fall through the map.
                    Vector3 dest = ResolveGateTeleportDest(arenaPos);
                    var tp = AccessTools.Method(unit.GetType(), "TeleportTo", new[] { typeof(Vector3) })
                          ?? AccessTools.Method(unit.GetType(), "TeleportTo");
                    if (tp != null) tp.Invoke(unit, new object[] { dest });
                    else if (unit is Component c && c != null) c.transform.position = dest;
                    NetLogger.Info($"[ArenaLockdown] teleported local player into arena arena=({arenaPos.x:0.0},{arenaPos.y:0.0},{arenaPos.z:0.0}) dest={dest:F1}");
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
            // LD-2f note: a spurious reopen is already blocked by the MetalGate.Open prefix (by gate id), so it never
            // reaches here; only a genuine open (enter, or all-dead) does. Release detection stays position-based below.
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

        /// <summary>Teleport an already-resolved local player unit to a world position (real <c>Unit.TeleportTo</c> if
        /// present, else a raw transform move). Returns false if the unit is missing.</summary>
        private static bool MoveLocalPlayerTo(object unit, Vector3 dest)
        {
            try
            {
                if (unit == null) { NetLogger.Warn("[ArenaLockdown] teleport: local player unit missing"); return false; }
                var tp = AccessTools.Method(unit.GetType(), "TeleportTo", new[] { typeof(Vector3) })
                      ?? AccessTools.Method(unit.GetType(), "TeleportTo");
                if (tp != null) tp.Invoke(unit, new object[] { dest });
                else if (unit is Component c && c != null) c.transform.position = dest;
                else return false;
                return true;
            }
            catch (Exception ex) { NetLogger.Warn($"[ArenaLockdown] teleport failed: {ex.Message}"); return false; }
        }

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
            _sandstormArenas.Clear();
            _gracedGates.Clear();
            _heldGates.Clear();
            _legitGateOpenUntil = -999f;
            _localCrossed.Clear();
            _doorwayCrossings.Clear();
            _activeArenaKey = "";
            _clientArenaMembers.Clear();
            _clientArenaKey = "";
            ArenaBarrierManager.Clear();
            _armed = false;
            if (HidePrompt != null) { try { HidePrompt(); } catch { } }
        }
    }
}
