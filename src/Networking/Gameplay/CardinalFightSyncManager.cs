using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SULFURTogether.Networking;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// Phase BGC — host authority for the Black Guild Cardinal alcove fight (<c>CardinalFightHelper</c> /
    /// <c>CardinalHelper</c> / <c>CardinalSpawn</c>, all in <c>PerfectRandom.Sulfur.Gameplay</c>, so everything here
    /// is reflection — that assembly is not referenced).
    ///
    /// <para>Vanilla decides three things locally that must not be decided locally in co-op; the reverse-engineered
    /// walk is in <c>Docs/CardinalFightSync.md</c>:</para>
    /// <list type="number">
    ///   <item><b>Which cardinals exist</b> — <c>Start()</c> destroys <c>5 - difficulty</c> of them picked with the
    ///   global <c>UnityEngine.Random</c>. Fixed in <c>CardinalPatches</c> (BGC-1) by seeding that one call from the
    ///   level seed, not here: it has to happen before this manager could possibly know about the room, and once both
    ///   ends keep the same cardinals every identity below is a plain list index.</item>
    ///   <item><b>When the fight starts</b> — <c>StartFight</c> has no code caller (scene-data wired), so it runs on
    ///   whichever end's player crossed the trigger. It is the step that lifts every cardinal's invulnerability, so an
    ///   end that never runs it keeps a room of inert, unkillable cardinals. BGC-2: a linked client blocks its own
    ///   start and requests; the host runs the real one and commits to everybody.</item>
    ///   <item><b>Where a cardinal teleports</b> — <c>getUnoccupiedSpawn()</c> is another global-RNG roll. BGC-3: the
    ///   host broadcasts the alcove it picked and every other end replays the <b>real</b> <c>ExecuteTeleport</c> with
    ///   that alcove forced, so the alcove door, the audio and the occupancy bookkeeping all follow natively.</item>
    /// </list>
    ///
    /// <para>BGC-4 additionally vetoes a destination that sits on top of <i>any</i> player: vanilla reserves only the
    /// alcove nearest <c>GameManager.PlayerObject</c> (the local player), which in co-op leaves every remote player a
    /// valid landing spot — and a native <c>Unit.TeleportTo</c> materialising a collider inside a player is the PK-3
    /// fling.</para>
    /// </summary>
    internal static class CardinalFightSyncManager
    {
        // How close a destination alcove may be to a player before the host re-picks (BGC-4). Vanilla reserves exactly
        // one alcove — the nearest to the local player — so this is the same rule generalised, with a radius rather
        // than a rank so it also covers "two players stand at two different alcoves".
        private const float PlayerSafeRadius = 4.0f;

        // A client that blocked its own StartFight but never heard a commit back (host in another level, packet lost)
        // must not be left with an unkillable room. Fail open after this long.
        private const float StartCommitTimeoutSeconds = 5.0f;

        private const float HelperScanIntervalSeconds = 1.0f;
        private const float HelperMatchEpsilon        = 2.0f;

        private static bool Enabled { get { try { return Plugin.Cfg.EnableCardinalSync.Value; } catch { return false; } } }
        private static bool LogOn  { get { try { return Plugin.Cfg.LogCardinalSync.Value; } catch { return false; } } }

        private static bool IsHost         => NetGameplaySyncBridge.BossMode == NetMode.Host;
        private static bool IsLinkedClient => NetGameplaySyncBridge.BossMode == NetMode.Client && NetLinkState.ClientLinked;
        private static bool SessionActive  { get { try { return NetGameplaySyncBridge.IsSessionActive; } catch { return false; } } }

        private static int _seq;

        // Instance ids of helpers whose fight start has already been broadcast / applied on this end (dedup: the
        // trigger can fire more than once, and the host also re-enters StartFight when serving a client request).
        private static readonly HashSet<int> _startedHelpers = new HashSet<int>();

        // Client: helpers whose local StartFight we blocked, and when — drives the fail-open timeout.
        private static readonly Dictionary<int, float> _pendingClientStarts = new Dictionary<int, float>();

        // Set while this end is replaying a host-authored event. Every capture hook checks it: a replay is not a local
        // decision and must neither be blocked nor re-broadcast (BossSyncReuseMap §6 — "replaying a native method
        // replays OUR hooks on it too").
        private static bool _applyingMirror;
        public static bool IsApplyingMirror => _applyingMirror;

        // The alcove a mirrored ExecuteTeleport must use, consumed by the getUnoccupiedSpawn prefix.
        private static object? _forcedSpawn;

        public static int StartsCommitted, StartsRequested, StartsFailedOpen, TeleportsBroadcast, TeleportsApplied, SpawnPicksVetoed;

        // ───────────────────────────────────────────────────────────── reflection cache
        // Resolved once. AccessTools.TypeByName walks every loaded assembly, so calling it per event would repeat the
        // EMP-DW mistake (an uncached lookup inside a physics callback cost the Emperor fight half its frame rate).

        private static bool _typesResolved;
        private static Type? _tFightHelper, _tCardinal, _tSpawn;
        private static FieldInfo? _fSpawnPoints, _fCardinalObjects, _fSequence, _fCurrentSpawn, _fIsOccupied;
        private static MethodInfo? _mStartFightHelper, _mExecuteTeleport, _mStartTeleport;

        internal static Type? FightHelperType { get { EnsureTypes(); return _tFightHelper; } }
        internal static Type? CardinalType    { get { EnsureTypes(); return _tCardinal; } }

        internal static bool EnsureTypes()
        {
            if (_typesResolved) return _tFightHelper != null && _tCardinal != null;
            _typesResolved = true;
            try
            {
                _tFightHelper = AccessTools.TypeByName("PerfectRandom.Sulfur.Gameplay.CardinalFightHelper") ?? AccessTools.TypeByName("CardinalFightHelper");
                _tCardinal    = AccessTools.TypeByName("PerfectRandom.Sulfur.Gameplay.CardinalHelper")      ?? AccessTools.TypeByName("CardinalHelper");
                _tSpawn       = AccessTools.TypeByName("PerfectRandom.Sulfur.Gameplay.CardinalSpawn")       ?? AccessTools.TypeByName("CardinalSpawn");
                if (_tFightHelper == null || _tCardinal == null)
                {
                    Plugin.Log.Warn("[Cardinal] CardinalFightHelper/CardinalHelper not found — cardinal fight sync disabled.");
                    return false;
                }

                _fSpawnPoints     = AccessTools.Field(_tFightHelper, "spawnPoints");
                _fCardinalObjects = AccessTools.Field(_tFightHelper, "cardinalObjects");
                _mStartFightHelper = AccessTools.DeclaredMethod(_tFightHelper, "StartFight");

                _fSequence     = AccessTools.Field(_tCardinal, "sequence");
                _fCurrentSpawn = AccessTools.Field(_tCardinal, "currentSpawn");
                _mExecuteTeleport = AccessTools.DeclaredMethod(_tCardinal, "ExecuteTeleport");
                _mStartTeleport   = AccessTools.DeclaredMethod(_tCardinal, "StartTeleport");

                if (_tSpawn != null) _fIsOccupied = AccessTools.Field(_tSpawn, "isOccupied");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[Cardinal] type resolution failed: {ex.Message}");
                return false;
            }
        }

        // ───────────────────────────────────────────────────────────── BGC-2 fight start

        /// <summary>Client prefix decision for <c>CardinalFightHelper.StartFight</c>: block the local start and ask the
        /// host to run the real one. Returns true when the caller must skip the original.</summary>
        public static bool ShouldBlockLocalFightStart(object helper)
        {
            try
            {
                if (!Enabled || _applyingMirror) return false;
                if (!IsLinkedClient || !SessionActive) return false;
                if (!(helper is Component c) || c == null) return false;

                int id = c.GetInstanceID();
                if (_startedHelpers.Contains(id)) return true; // already committed here — the trigger refired

                if (!_pendingClientStarts.ContainsKey(id))
                {
                    _pendingClientStarts[id] = Time.realtimeSinceStartup;
                    Send(NetCardinalFightEvent.KindFightStartRequest, c.transform.position);
                    StartsRequested++;
                    if (LogOn) NetLogger.Info($"[Cardinal] client blocked local StartFight → requested host start pos={c.transform.position:F1}");
                }
                return true;
            }
            catch (Exception ex) { NetLogger.Warn($"[Cardinal] ShouldBlockLocalFightStart failed: {ex.Message}"); return false; }
        }

        /// <summary>Postfix on a <c>StartFight</c> that actually ran on this end. On the host that is the authoritative
        /// start — commit it to every client. Fires once per helper.</summary>
        public static void OnLocalFightStarted(object helper)
        {
            try
            {
                if (!Enabled) return;
                if (!(helper is Component c) || c == null) return;

                int id = c.GetInstanceID();
                bool fresh = _startedHelpers.Add(id);
                _pendingClientStarts.Remove(id);
                if (!fresh) return;

                if (_applyingMirror) return;                 // a client replaying the host's commit — not a decision
                if (!IsHost || !SessionActive) return;

                Send(NetCardinalFightEvent.KindFightStartCommit, c.transform.position);
                StartsCommitted++;
                if (LogOn) NetLogger.Info($"[Cardinal] host started the cardinal fight → committed to clients pos={c.transform.position:F1}");
            }
            catch (Exception ex) { NetLogger.Warn($"[Cardinal] OnLocalFightStarted failed: {ex.Message}"); }
        }

        // ───────────────────────────────────────────────────────────── BGC-3 teleport

        /// <summary>Client prefix for <c>CardinalHelper.StartTeleport</c> / <c>ExecuteTeleport</c>: the client copy is a
        /// host-driven puppet, but its animator still runs — and <c>ExecuteTeleport</c> is reachable from the teleport
        /// clip's own animation event, which would roll a divergent destination. Block both; the host's broadcast
        /// replays them.</summary>
        public static bool ShouldBlockLocalTeleport(object cardinal)
        {
            if (!Enabled || _applyingMirror) return false;
            return IsLinkedClient && SessionActive;
        }

        /// <summary>Host postfix on <c>CardinalHelper.StartTeleport</c> — broadcast the wind-up so the client plays the
        /// same animation and source-alcove sound instead of the cardinal simply vanishing.</summary>
        public static void OnHostTeleportBegin(object cardinal)
        {
            try
            {
                if (!Enabled || _applyingMirror || !IsHost || !SessionActive) return;
                if (!TryDescribeCardinal(cardinal, out var helper, out int cardinalIndex)) return;
                Send(NetCardinalFightEvent.KindTeleportBegin, helper.transform.position, cardinalIndex);
            }
            catch (Exception ex) { NetLogger.Warn($"[Cardinal] OnHostTeleportBegin failed: {ex.Message}"); }
        }

        /// <summary>Host postfix on <c>CardinalHelper.ExecuteTeleport</c> — the destination alcove is already written to
        /// <c>currentSpawn</c> by the native method, so read it back and broadcast its index.</summary>
        public static void OnHostTeleportExecuted(object cardinal)
        {
            try
            {
                if (!Enabled || _applyingMirror || !IsHost || !SessionActive) return;
                if (!TryDescribeCardinal(cardinal, out var helper, out int cardinalIndex)) return;

                object? spawn = _fCurrentSpawn?.GetValue(cardinal);
                int spawnIndex = IndexOfSpawn(helper, spawn);
                Vector3 dest = spawn is Component sc && sc != null ? sc.transform.position
                             : cardinal is Component cc && cc != null ? cc.transform.position : Vector3.zero;

                Send(NetCardinalFightEvent.KindTeleportExecute, helper.transform.position, cardinalIndex, spawnIndex, dest);
                TeleportsBroadcast++;
                if (LogOn) NetLogger.Info($"[Cardinal] host teleport cardinal={cardinalIndex} → spawn={spawnIndex} pos={dest:F1}");
            }
            catch (Exception ex) { NetLogger.Warn($"[Cardinal] OnHostTeleportExecuted failed: {ex.Message}"); }
        }

        /// <summary>Prefix hook for <c>CardinalFightHelper.getUnoccupiedSpawn</c>. While a mirrored
        /// <c>ExecuteTeleport</c> is being replayed, hand back the alcove the host chose instead of rolling one.</summary>
        public static bool TryTakeForcedSpawn(out object? spawn)
        {
            spawn = _forcedSpawn;
            _forcedSpawn = null;
            return spawn != null;
        }

        // ───────────────────────────────────────────────────────────── BGC-4 keep destinations off players

        /// <summary>Postfix hook for <c>CardinalFightHelper.getUnoccupiedSpawn</c>: vanilla only ever reserves the alcove
        /// nearest the LOCAL player (<c>SetClosestSpawnsToOccupiedRoutine</c>), so in co-op a cardinal can teleport
        /// straight into a remote player. Re-pick when the chosen alcove is within <see cref="PlayerSafeRadius"/> of any
        /// player unit (host player + ghosts). Fails open: if no alternative is free, vanilla's pick stands.</summary>
        public static void VetoSpawnNearPlayers(object helper, ref object picked)
        {
            try
            {
                if (!Enabled || !SessionActive || picked == null) return;
                if (!(helper is Component hc) || hc == null) return;
                if (!(_fSpawnPoints?.GetValue(helper) is Array spawns) || spawns.Length == 0) return;
                if (!IsNearAnyPlayer(picked)) return;

                foreach (var candidate in spawns)
                {
                    if (candidate == null || ReferenceEquals(candidate, picked)) continue;
                    if (_fIsOccupied != null && _fIsOccupied.GetValue(candidate) is bool occupied && occupied) continue;
                    if (IsNearAnyPlayer(candidate)) continue;
                    picked = candidate;
                    SpawnPicksVetoed++;
                    if (LogOn) NetLogger.Info("[Cardinal] destination alcove sat on a player — re-picked");
                    return;
                }
            }
            catch (Exception ex) { NetLogger.Warn($"[Cardinal] VetoSpawnNearPlayers failed: {ex.Message}"); }
        }

        private static bool IsNearAnyPlayer(object spawn)
        {
            if (!(spawn is Component c) || c == null) return false;
            Vector3 p = c.transform.position;
            float sqr = PlayerSafeRadius * PlayerSafeRadius;
            foreach (var unit in SULFURTogether.Patches.CousinArmPatches.GatherPlayerUnits())
            {
                if (!(unit is Component uc) || uc == null) continue;
                if ((uc.transform.position - p).sqrMagnitude <= sqr) return true;
            }
            return false;
        }

        // ───────────────────────────────────────────────────────────── receive

        /// <summary>Host: a client's player fired the room trigger. Run the REAL start chain here; the postfix commits
        /// it to everybody (including the requesting client).</summary>
        public static void HandleClientStartRequest(NetCardinalFightEvent m)
        {
            try
            {
                if (!Enabled || m == null || !IsHost) return;
                var helper = FindHelper(m.HelperPosition);
                if (helper == null)
                {
                    if (LogOn) NetLogger.Info($"[Cardinal] host has no CardinalFightHelper near {m.HelperPosition:F1} — start request dropped");
                    return;
                }
                if (_startedHelpers.Contains(helper.GetInstanceID()))
                {
                    Send(NetCardinalFightEvent.KindFightStartCommit, helper.transform.position); // late joiner / lost commit
                    return;
                }
                _mStartFightHelper?.Invoke(helper, null);
                if (LogOn) NetLogger.Info($"[Cardinal] host ran StartFight for a client request pos={m.HelperPosition:F1}");
            }
            catch (Exception ex) { NetLogger.Warn($"[Cardinal] HandleClientStartRequest failed: {ex.Message}"); }
        }

        /// <summary>Client: apply a host-authored event by replaying the real native method under the mirror guard.</summary>
        public static void ApplyRemote(NetCardinalFightEvent m)
        {
            if (!Enabled || m == null) return;
            try
            {
                var helper = FindHelper(m.HelperPosition);
                if (helper == null)
                {
                    if (LogOn) NetLogger.Info($"[Cardinal] no local CardinalFightHelper near {m.HelperPosition:F1} — kind={m.Kind} dropped");
                    return;
                }

                switch (m.Kind)
                {
                    case NetCardinalFightEvent.KindFightStartCommit: ApplyFightStart(helper); break;
                    case NetCardinalFightEvent.KindTeleportBegin:    ApplyTeleport(helper, m, execute: false); break;
                    case NetCardinalFightEvent.KindTeleportExecute:  ApplyTeleport(helper, m, execute: true);  break;
                }
            }
            catch (Exception ex) { NetLogger.Warn($"[Cardinal] ApplyRemote failed: {ex.Message}"); }
        }

        private static void ApplyFightStart(Component helper)
        {
            int id = helper.GetInstanceID();
            _pendingClientStarts.Remove(id);
            if (_startedHelpers.Contains(id)) return;

            _applyingMirror = true;
            try { _mStartFightHelper?.Invoke(helper, null); }
            finally { _applyingMirror = false; }

            _startedHelpers.Add(id);
            if (LogOn) NetLogger.Info("[Cardinal] client applied host fight start (cardinals armed + music)");
        }

        private static void ApplyTeleport(Component helper, NetCardinalFightEvent m, bool execute)
        {
            object? cardinal = ResolveCardinal(helper, m.CardinalIndex);
            if (cardinal == null)
            {
                if (LogOn) NetLogger.Info($"[Cardinal] no local cardinal at index {m.CardinalIndex} — teleport dropped");
                return;
            }

            if (!execute)
            {
                _applyingMirror = true;
                try { _mStartTeleport?.Invoke(cardinal, null); }
                finally { _applyingMirror = false; }
                return;
            }

            // Force the host's alcove into the native pick, then run the real ExecuteTeleport so the alcove door
            // animator, both teleport sounds, the physics/navmesh nudge and the isOccupied bookkeeping all happen
            // exactly the way they do on the host. Positional fallback keeps a stale/short spawnPoints array from
            // silently degrading into a local roll.
            _forcedSpawn = ResolveSpawn(helper, m.SpawnIndex) ?? ResolveSpawnByPosition(helper, m.DestinationPosition);
            if (_forcedSpawn == null)
            {
                // Never run the native teleport without the host's destination: it would roll its own and put this
                // end's cardinal somewhere the host is not. The cardinal is a host-driven puppet, so leaving it alone
                // means the position snapshot carries it — a missed animation, not a divergence.
                if (LogOn) NetLogger.Info($"[Cardinal] host alcove {m.SpawnIndex} did not resolve locally — teleport left to the puppet snapshot");
                return;
            }
            _applyingMirror = true;
            try { _mExecuteTeleport?.Invoke(cardinal, null); }
            finally { _applyingMirror = false; _forcedSpawn = null; }

            TeleportsApplied++;
            if (LogOn) NetLogger.Info($"[Cardinal] client applied teleport cardinal={m.CardinalIndex} spawn={m.SpawnIndex}");
        }

        // ───────────────────────────────────────────────────────────── tick / lifecycle

        public static void Tick()
        {
            if (!Enabled || _pendingClientStarts.Count == 0) return;
            try
            {
                float now = Time.realtimeSinceStartup;
                List<int>? expired = null;
                foreach (var kv in _pendingClientStarts)
                    if (now - kv.Value > StartCommitTimeoutSeconds)
                        (expired ??= new List<int>()).Add(kv.Key);
                if (expired == null) return;

                foreach (int id in expired)
                {
                    _pendingClientStarts.Remove(id);
                    var helper = FindHelperById(id);
                    if (helper == null) continue;

                    // Fail open. A blocked start that never comes back leaves a room of permanently invulnerable
                    // cardinals — strictly worse than an unsynced start.
                    ApplyFightStart(helper);
                    StartsFailedOpen++;
                    NetLogger.Warn($"[Cardinal] no host commit within {StartCommitTimeoutSeconds:0}s — starting the cardinal fight locally");
                }
            }
            catch (Exception ex) { NetLogger.Warn($"[Cardinal] Tick failed: {ex.Message}"); }
        }

        /// <summary>Drop per-level state. Called on level change / session reset — instance ids do not survive a scene
        /// load and a stale "already started" entry would suppress the next room's commit.</summary>
        public static void Clear()
        {
            _startedHelpers.Clear();
            _pendingClientStarts.Clear();
            _helperCache.Clear();
            _forcedSpawn = null;
            _applyingMirror = false;
        }

        public static string FormatCounters()
            => $"cardinalStarts={StartsCommitted} req={StartsRequested} failOpen={StartsFailedOpen} "
             + $"tpSent={TeleportsBroadcast} tpApplied={TeleportsApplied} spawnVetoed={SpawnPicksVetoed}";

        // ───────────────────────────────────────────────────────────── identity helpers

        private static readonly List<Component> _helperCache = new List<Component>();
        private static float _lastHelperScan = -999f;

        private static void RefreshHelperCache()
        {
            float now = Time.realtimeSinceStartup;
            bool destroyed = false;
            for (int i = 0; i < _helperCache.Count; i++)
                if (_helperCache[i] == null) { destroyed = true; break; }
            // Rescan when a cached helper died (level change) or once a second at most — a full-scene type scan is far
            // too expensive to run per teleport packet.
            if (!destroyed && now - _lastHelperScan <= HelperScanIntervalSeconds) return;

            _lastHelperScan = now;
            _helperCache.Clear();
            if (!EnsureTypes() || _tFightHelper == null) return;
            foreach (var o in UnityEngine.Object.FindObjectsByType(_tFightHelper, FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (o is Component c && c != null) _helperCache.Add(c);
        }

        private static Component? FindHelper(Vector3 key)
        {
            RefreshHelperCache();
            Component? best = null;
            float bestSqr = HelperMatchEpsilon * HelperMatchEpsilon;
            foreach (var c in _helperCache)
            {
                if (c == null) continue;
                float sqr = (c.transform.position - key).sqrMagnitude;
                if (sqr <= bestSqr) { bestSqr = sqr; best = c; }
            }
            return best;
        }

        private static Component? FindHelperById(int instanceId)
        {
            RefreshHelperCache();
            foreach (var c in _helperCache)
                if (c != null && c.GetInstanceID() == instanceId) return c;
            return null;
        }

        /// <summary>Resolve a cardinal's owning helper + its index in <c>cardinalObjects</c>. That list is the identity:
        /// it is serialized order minus the seeded cull, so both ends agree on it (BGC-1).</summary>
        private static bool TryDescribeCardinal(object cardinal, out Component helper, out int index)
        {
            helper = null!;
            index = -1;
            if (!(_fSequence?.GetValue(cardinal) is Component seq) || seq == null) return false;
            helper = seq;
            if (!(_fCardinalObjects?.GetValue(seq) is IList list)) return false;
            if (!(cardinal is Component cc) || cc == null) return false;

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is GameObject go && go != null && go == cc.gameObject) { index = i; return true; }
            }
            return false;
        }

        private static object? ResolveCardinal(Component helper, int index)
        {
            if (index < 0) return null;
            if (!(_fCardinalObjects?.GetValue(helper) is IList list)) return null;
            if (index >= list.Count) return null;
            if (!(list[index] is GameObject go) || go == null) return null;
            return _tCardinal == null ? null : go.GetComponent(_tCardinal);
        }

        private static object? ResolveSpawn(Component helper, int index)
        {
            if (index < 0) return null;
            if (!(_fSpawnPoints?.GetValue(helper) is Array spawns)) return null;
            if (index >= spawns.Length) return null;
            var s = spawns.GetValue(index);
            return s is UnityEngine.Object uo && uo == null ? null : s;
        }

        private static object? ResolveSpawnByPosition(Component helper, Vector3 pos)
        {
            if (!(_fSpawnPoints?.GetValue(helper) is Array spawns)) return null;
            object? best = null;
            float bestSqr = HelperMatchEpsilon * HelperMatchEpsilon;
            foreach (var s in spawns)
            {
                if (!(s is Component c) || c == null) continue;
                float sqr = (c.transform.position - pos).sqrMagnitude;
                if (sqr <= bestSqr) { bestSqr = sqr; best = s; }
            }
            return best;
        }

        private static int IndexOfSpawn(Component helper, object? spawn)
        {
            if (spawn == null) return -1;
            if (!(_fSpawnPoints?.GetValue(helper) is Array spawns)) return -1;
            for (int i = 0; i < spawns.Length; i++)
                if (ReferenceEquals(spawns.GetValue(i), spawn)) return i;
            return -1;
        }

        // ───────────────────────────────────────────────────────────── send

        private static void Send(byte kind, Vector3 helperPos, int cardinalIndex = -1, int spawnIndex = -1, Vector3 dest = default)
        {
            NetGameplaySyncBridge.ReportLocalCardinalFightEvent(new NetCardinalFightEvent
            {
                Sequence            = ++_seq,
                Kind                = kind,
                HelperPosition      = helperPos,
                CardinalIndex       = cardinalIndex,
                SpawnIndex          = spawnIndex,
                DestinationPosition = dest,
            });
        }
    }
}
