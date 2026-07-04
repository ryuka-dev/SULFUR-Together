using System;
using System.Collections.Generic;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay.Boss
{
    /// <summary>
    /// Phase 5.4-E4: Host-authoritative manifest of boss-owned sub-entities spawned at runtime.
    ///
    /// Boss adds (CousinArm, BlackGuildLuciaEye, Witch illusions, ...) are NOT present at level load — boss phase /
    /// mechanic code creates them via UnitSO.SpawnUnitAsync(mono, ...) AFTER the dialog/phase starts, independently
    /// on each end. The normal WorldRoster (frozen at scene stabilization) never contains them, and proximity binding
    /// fails because timing/count/position diverge (Lucia eyes even spawn at the SAME position). LogOutput22:
    /// "host=BlackGuildLuciaEye client=&lt;none&gt;", "CousinArm ambiguous hostOnly/clientOnly".
    ///
    /// The only cross-end-stable identity is (EncounterKey, AddType, SequenceIndex): each end spawns the Nth add of a
    /// given type in the same deterministic order (SpawnEyes loops eyesAmount; SpawnArm loops pools). The Host records
    /// and broadcasts every boss-owned spawn; the Client records its own and binds local[seq] ↔ host[seq].
    ///
    /// This phase is a MANIFEST + BINDING-CLASSIFICATION foundation: it never Destroys, never force-syncs health, and
    /// never treats adds as normal random enemies. It produces the explicit binding picture the next phase needs.
    /// </summary>
    internal static class BossDynamicSpawnManifest
    {
        private static readonly object _lock = new object();

        // ---- owner correlation across the async SpawnUnitAsync -> SpawnUnit boundary (match by UnitSO reference) ----
        private sealed class Pending { public object UnitSO = null!; public object Owner = null!; public float At; }
        private static readonly List<Pending> _pending = new List<Pending>();
        private const float PendingTtlSeconds = 8f;

        // ---- per-end records keyed by (encounterKey|addType) ----
        private sealed class LocalAdd { public int Seq; public object Unit = null!; public string UnitId = ""; public int InstanceId; public Vector3 Pos; public int BoundHostSeq = -1; }
        private sealed class HostEntry { public NetBossDynamicSpawn Msg = null!; public bool BoundLocal; public int BoundLocalInstanceId; public float ReceivedAt; public bool HostOnlyResolved; }

        private static readonly Dictionary<string, List<LocalAdd>> _localBySlot = new Dictionary<string, List<LocalAdd>>();   // local spawns (both ends)
        private static readonly Dictionary<string, int> _localSeqCounter = new Dictionary<string, int>();                      // per-slot local counter
        private static readonly Dictionary<string, int> _hostSeqCounter = new Dictionary<string, int>();                       // per-slot host counter (host side)
        private static readonly Dictionary<string, HostEntry> _hostEntries = new Dictionary<string, HostEntry>();             // key = slot|hostSeq (client side)
        private static int _revision;

        // ---- counters ----
        public static int SpawnsHostBroadcast;
        public static int SpawnsClientLocal;
        public static int SpawnsBound;
        public static int SpawnsHostOnlyNoLocal;
        public static int SpawnsAttributed;

        // ---- RT3-A: bind correction (snap-on-bind + inert + hit-gate) ----
        // Keyed by Unity instance id (== LocalAdd.InstanceId == BossReflect.InstanceId == GetInstanceID()).
        private sealed class GatedAdd { public object Unit = null!; public float At; public bool Frozen; }
        private static readonly Dictionary<int, GatedAdd> _gatedAdds = new Dictionary<int, GatedAdd>();
        private const float GatedInertTtlSeconds = 4f; // safety: release if a host manifest never arrives
        private const float HostOnlyGraceSeconds = 2f;  // wait this long for a local before mirroring a host add as host-extra
        public static int SpawnsGatedInert;
        public static int SpawnsSnappedOnBind;
        public static int HitsGateSwallowed;

        private static bool Enabled { get { try { return Plugin.Cfg.EnableBossDynamicSpawnManifest.Value; } catch { return false; } } }
        private static bool LogOn  { get { try { return Plugin.Cfg.LogBossDynamicSpawn.Value; } catch { return false; } } }

        private static string Slot(string key, string addType) => key + "|" + addType;

        public static void Reset()
        {
            lock (_lock)
            {
                _pending.Clear();
                _localBySlot.Clear();
                _localSeqCounter.Clear();
                _hostSeqCounter.Clear();
                _hostEntries.Clear();
                // RT3-A: lift any lingering freeze before dropping the gate map (adds usually destroyed on reset, but a
                // surviving one must not be left standing still / un-attackable).
                foreach (var g in _gatedAdds.Values) if (g.Frozen) TryUnfreezeMovement(g.Unit);
                _gatedAdds.Clear();
            }
        }

        // ================================================================== spawn capture (both ends)

        /// <summary>Prefix on UnitSO.SpawnUnitAsync: remember the owning boss so the async-spawned Unit can be
        /// attributed. Only records when the owner is a known Boss (keeps non-boss spawns out entirely).</summary>
        public static void NotePendingSpawn(object unitSO, object owner)
        {
            try
            {
                if (!Enabled || unitSO == null || owner == null) return;
                // F4-ADDS: accept the Desert perimeter as an owner too (it spawns the saddled pikes; RecordSpawn resolves
                // it to the owning boss) — the boss-only filter here rejected those spawns before attribution ever ran.
                if (!NetBossEncounterManager.TryGetEncounterKeyForBoss(owner, out _, out _)
                    && !NetBossEncounterManager.TryGetEncounterKeyForDesertPerimeter(owner, out _, out _)) return; // not a boss owner
                lock (_lock)
                {
                    float now = Time.realtimeSinceStartup;
                    _pending.RemoveAll(p => now - p.At > PendingTtlSeconds);
                    _pending.Add(new Pending { UnitSO = unitSO, Owner = owner, At = now });
                }
            }
            catch { }
        }

        /// <summary>Postfix on static UnitSO.SpawnUnit: claim the matching pending owner (by UnitSO reference) and
        /// record/attribute the freshly-spawned add.</summary>
        public static void OnUnitSpawned(object unitSO, object spawnedUnit, Vector3 position)
        {
            try
            {
                if (!Enabled || unitSO == null || spawnedUnit == null) return;
                object? owner = null;
                lock (_lock)
                {
                    for (int i = 0; i < _pending.Count; i++)
                    {
                        if (ReferenceEquals(_pending[i].UnitSO, unitSO)) { owner = _pending[i].Owner; _pending.RemoveAt(i); break; }
                    }
                }
                if (owner == null) return; // not a boss-owned spawn
                RecordSpawn(owner, spawnedUnit, position);
            }
            catch (Exception ex) { Plugin.Log.Warn($"[BossSpawn] OnUnitSpawned failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static void RecordSpawn(object owner, object spawnedUnit, Vector3 position)
        {
            // F4-ADDS: the Desert SADDLED pikes are spawned by the perimeter (GetPikeEnemyAsync's mono is the
            // DesertClausePerimeter, not the boss helper), so the direct boss lookup never matched and they were never
            // broadcast — the client has never seen a saddled pike. Resolve the perimeter to its owning boss.
            if (!NetBossEncounterManager.TryGetEncounterKeyForBoss(owner, out string key, out string ownerType)
                && !NetBossEncounterManager.TryGetEncounterKeyForDesertPerimeter(owner, out key, out ownerType)) return;

            string unitId = BossReflect.ReadUnitId(spawnedUnit);
            // IMPORTANT (E4 fix): the C# runtime type collapses to "Npc" for EVERY boss add (BlackGuildAssassin,
            // BlackGuildMaiden, BlackGuildLuciaEye, GoblinCousinArm are all Npc instances), which made one shared
            // sequence counter bind different unit TYPES together across ends (LogOutput24: Assassin↔Maiden, Assassin↔Eye).
            // The UnitIdentifier (e.g. "BlackGuildLuciaEye") is stable cross-end and the real type — slot by it.
            string addType = !string.IsNullOrEmpty(unitId) ? unitId : spawnedUnit.GetType().Name;
            int instId = BossReflect.InstanceId(spawnedUnit);
            string slot = Slot(key, addType);
            SpawnsAttributed++;

            NetMode mode = NetGameplaySyncBridge.BossMode;

            int localSeq;
            lock (_lock)
            {
                _localSeqCounter.TryGetValue(slot, out int c);
                localSeq = c;
                _localSeqCounter[slot] = c + 1;
                if (!_localBySlot.TryGetValue(slot, out var list)) { list = new List<LocalAdd>(); _localBySlot[slot] = list; }
                list.Add(new LocalAdd { Seq = localSeq, Unit = spawnedUnit, UnitId = unitId, InstanceId = instId, Pos = position });
            }

            if (mode == NetMode.Host)
            {
                int hostSeq;
                lock (_lock) { _hostSeqCounter.TryGetValue(slot, out int hc); hostSeq = hc; _hostSeqCounter[slot] = hc + 1; }
                if (!NetBossEncounterManager.TryGetRunContext(out string chap, out int lvl, out bool hasSeed, out int seed)) { chap = ""; lvl = -1; }
                var msg = new NetBossDynamicSpawn
                {
                    EncounterKey = key, OwnerBossType = ownerType, AddType = addType, AddUnitId = unitId,
                    SequenceIndex = hostSeq, Position = position, HostInstanceId = instId,
                    ChapterName = chap, LevelIndex = lvl, HasSeed = hasSeed, Seed = seed,
                    Revision = ++_revision, Timestamp = Time.realtimeSinceStartup,
                    // RT3: carry the cross-end UnitSO.id.value (for mirror-spawn) + host SpawnIndex (for puppet binding).
                    UnitIdValue = ReadUnitIdValueFromUnit(spawnedUnit),
                    HostSpawnIndex = NetGameplayProbeManager.GetSpawnIndexForObject(spawnedUnit),
                };
                SpawnsHostBroadcast++;
                if (LogOn) Plugin.Log.Info($"[BossSpawn] host add spawned + broadcasting {msg.ToCompact()}");
                NetGameplaySyncBridge.BroadcastHostBossDynamicSpawn(msg);
            }
            else if (mode == NetMode.Client)
            {
                SpawnsClientLocal++;
                if (LogOn) Plugin.Log.Info($"[BossSpawn] client local add spawned slot={slot} localSeq={localSeq} unitId={(string.IsNullOrEmpty(unitId) ? "?" : unitId)} inst={instId} pos={position:F1}");
                // RT3-A: the local spawn point diverges from the host's (RNG/pool order differs), so until this add is
                // bound and snapped to the host position we (a) gate client hit-claims on it (no spurious mis-kill) and
                // (b) optionally freeze its movement so it doesn't wander at the wrong point. Cleared in FinishBind.
                if (RuntimeSyncEnabled() && !IsSpecialAdd(addType))
                    BeginGatedInert(spawnedUnit, instId);
                TryBindLocal(slot, localSeq);
            }
        }

        // ================================================================== client: host manifest

        public static void HandleHostBossDynamicSpawn(NetBossDynamicSpawn msg)
        {
            try
            {
                if (!Enabled || msg == null || NetGameplaySyncBridge.BossMode != NetMode.Client) return;
                string slot = Slot(msg.EncounterKey, msg.AddType);
                string hkey = slot + "|" + msg.SequenceIndex;
                lock (_lock) { if (!_hostEntries.ContainsKey(hkey)) _hostEntries[hkey] = new HostEntry { Msg = msg, ReceivedAt = Time.realtimeSinceStartup }; }
                if (LogOn) Plugin.Log.Info($"[BossSpawn] client received host add {msg.ToCompact()}");
                TryBindHost(slot, msg.SequenceIndex);
            }
            catch (Exception ex) { Plugin.Log.Warn($"[BossSpawn] HandleHostBossDynamicSpawn failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        // Bind by sequence: the Nth local add of a type binds to the Nth host add of that type within an encounter.
        private static void TryBindLocal(string slot, int localSeq)
        {
            HostEntry? he; LocalAdd? la;
            lock (_lock)
            {
                _hostEntries.TryGetValue(slot + "|" + localSeq, out he);
                la = FindLocal(slot, localSeq);
            }
            FinishBind(slot, localSeq, he, la);
        }

        private static void TryBindHost(string slot, int hostSeq)
        {
            HostEntry? he; LocalAdd? la;
            lock (_lock)
            {
                _hostEntries.TryGetValue(slot + "|" + hostSeq, out he);
                la = FindLocal(slot, hostSeq);
            }
            FinishBind(slot, hostSeq, he, la);
        }

        private static LocalAdd? FindLocal(string slot, int seq)
        {
            if (_localBySlot.TryGetValue(slot, out var list))
                foreach (var a in list) if (a.Seq == seq) return a;
            return null;
        }

        private static void FinishBind(string slot, int seq, HostEntry? he, LocalAdd? la)
        {
            if (he == null) return;
            if (la != null && !he.BoundLocal)
            {
                he.BoundLocal = true; he.BoundLocalInstanceId = la.InstanceId; la.BoundHostSeq = seq;
                SpawnsBound++;
                Plugin.Log.Info($"[BossSpawn] BOUND slot={slot} seq={seq} hostUnitId={(string.IsNullOrEmpty(he.Msg.AddUnitId) ? "?" : he.Msg.AddUnitId)} localUnitId={(string.IsNullOrEmpty(la.UnitId) ? "?" : la.UnitId)} localInst={la.InstanceId} hostPos={he.Msg.Position:F1} localPos={la.Pos:F1}");
                // RT3: drive the client's existing local add as a host puppet (transform / attack / death by host
                // SpawnIndex). Skip special units that own their lifecycle (LuciaEye=F5/F6; GoblinCousinArm self-animates
                // via its own behaviour tree — see IsSpecialAdd) to avoid disabling the BT that drives their animation.
                if (RuntimeSyncEnabled() && he.Msg.HostSpawnIndex > 0 && !IsSpecialAdd(he.Msg.AddType))
                {
                    bool bound = NetGameplayProbeManager.RegisterMirroredRuntimeSpawn(la.Unit, he.Msg.HostSpawnIndex);
                    Plugin.Log.Info($"[BossSpawn] RT3 drive bound add as host puppet slot={slot} seq={seq} hostIdx={he.Msg.HostSpawnIndex} bound={bound}");
                    // RT3-A: hard-snap the local add to the HOST spawn position (the local RNG position is wrong) and
                    // release the hit-gate / inert freeze. From here the host snapshot stream drives it by SpawnIndex, so
                    // puppet[seq] becomes the host[seq] representative regardless of which divergent local point it spawned at.
                    FinishGatedRelease(la.Unit, la.InstanceId, he.Msg.Position, snap: bound, slot: slot, seq: seq);
                }
            }
            else if (la == null && !he.BoundLocal)
            {
                // RT3-A FIX: do NOT mirror-spawn immediately. For Cousin/boss adds the client's own boss AI spawns the
                // same add locally, just a few frames after the host manifest arrives (a network/timing race). The old
                // code treated "manifest arrived before local" as a host-extra, mirror-spawned a DUPLICATE and set
                // BoundLocal=true, which then BLOCKED the real local add from binding → orphaned local goblin (no host
                // claim → no damage feedback) + a duplicate. Instead leave the entry unbound and wait: the local will
                // spawn and bind via TryBindLocal. Only if the local never appears (grace window) does it mirror as a
                // genuine host-extra (see TickResolveDeferredHostOnly).
                if (LogOn) Plugin.Log.Info($"[BossSpawn] host add pending local (await local spawn) slot={slot} seq={seq} {he.Msg.ToCompact()}");
            }
        }

        private static bool RuntimeSyncEnabled() { try { return Plugin.Cfg.EnableRuntimeSpawnSync.Value; } catch { return false; } }
        private static bool SnapEnabled()  { try { return Plugin.Cfg.EnableRuntimeSpawnSnapOnBind.Value; } catch { return false; } }
        private static bool InertEnabled() { try { return Plugin.Cfg.EnableRuntimeSpawnInertUntilBound.Value; } catch { return false; } }

        // ================================================================== RT3-A: snap-on-bind + inert + hit-gate

        /// <summary>Client-side: a local boss-add was just captured but not yet bound to a host add. Gate hit-claims on
        /// it (so a divergent local-physics hit can't mis-kill a far-away host add) and optionally freeze its movement so
        /// it doesn't wander at the wrong local spawn point. Both are released in <see cref="FinishGatedRelease"/>.</summary>
        private static void BeginGatedInert(object unit, int instId)
        {
            try
            {
                if (unit == null || instId == 0) return;
                bool freeze = InertEnabled() && TryFreezeMovement(unit);
                lock (_lock)
                {
                    SweepStaleGatedLocked(Time.realtimeSinceStartup);
                    _gatedAdds[instId] = new GatedAdd { Unit = unit, At = Time.realtimeSinceStartup, Frozen = freeze };
                }
                SpawnsGatedInert++;
                if (LogOn) Plugin.Log.Info($"[BossSpawn] RT3-A gated inst={instId} frozen={freeze}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[BossSpawn] BeginGatedInert failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>Client-side: the add is now bound to a host add. Snap it to the authoritative host spawn position
        /// (discarding the divergent local position) and lift the hit-gate / movement freeze. If it did not actually
        /// bind, restore local movement so it still behaves as a normal enemy rather than standing frozen.</summary>
        private static void FinishGatedRelease(object unit, int instId, Vector3 hostPos, bool snap, string slot, int seq)
        {
            try
            {
                GatedAdd? g;
                lock (_lock) { _gatedAdds.TryGetValue(instId, out g); _gatedAdds.Remove(instId); }

                bool teleported = false;
                if (snap && SnapEnabled() && IsFinite(hostPos))
                {
                    teleported = BossReflect.TryInvokeArg(unit, "TeleportTo", hostPos, out _);
                    if (teleported) SpawnsSnappedOnBind++;
                }

                // If bound, the host snapshot stream takes over movement (puppet mode). If NOT bound, un-freeze so the
                // add isn't left standing still and un-attackable.
                if ((g?.Frozen ?? false) && !snap)
                    TryUnfreezeMovement(unit);

                if (LogOn) Plugin.Log.Info($"[BossSpawn] RT3-A released inst={instId} slot={slot} seq={seq} snap={snap} teleported={teleported} hostPos={hostPos:F1}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[BossSpawn] FinishGatedRelease failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>Client hit path: true while a local boss-add is captured but not yet bound+snapped to the host.
        /// The caller swallows the hit (no local damage, no host claim) so a mis-located local hit can't mis-kill.</summary>
        public static bool IsHitGated(object? npc)
        {
            try
            {
                if (npc == null || !RuntimeSyncEnabled() || !SnapEnabled()) return false;
                int id = BossReflect.InstanceId(npc);
                if (id == 0) return false;
                bool gated; lock (_lock) gated = _gatedAdds.ContainsKey(id);
                if (gated) HitsGateSwallowed++;
                return gated;
            }
            catch { return false; }
        }

        /// <summary>Per-frame safety sweep (client): release adds that were gated but never received a host manifest, so
        /// they don't stay frozen / un-hittable forever.</summary>
        public static void TickReleaseStaleGated()
        {
            try
            {
                // F4-ADDS fix: resolve deferred host-only adds BEFORE the gated-adds early-return. On a boss whose client
                // phase logic is fully suppressed (Desert) the client never spawns a local add, so _gatedAdds stays empty
                // forever and the old ordering skipped the host-extra mirror entirely — every host minion broadcast sat
                // "pending local" for good (Log340: 11 received, 0 mirrored).
                TickResolveDeferredHostOnly();
                if (_gatedAdds.Count == 0) return;
                List<GatedAdd>? expired = null;
                float now = Time.realtimeSinceStartup;
                lock (_lock)
                {
                    foreach (var kv in _gatedAdds)
                        if (now - kv.Value.At > GatedInertTtlSeconds) (expired ??= new List<GatedAdd>()).Add(kv.Value);
                    if (expired != null)
                        foreach (var g in expired) _gatedAdds.Remove(BossReflect.InstanceId(g.Unit));
                }
                if (expired != null)
                    foreach (var g in expired)
                    {
                        if (g.Frozen) TryUnfreezeMovement(g.Unit);
                        if (LogOn) Plugin.Log.Info($"[BossSpawn] RT3-A stale gate released (no host manifest) inst={BossReflect.InstanceId(g.Unit)}");
                    }
            }
            catch { }
        }

        /// <summary>Per-frame (client): a host add whose local counterpart never arrived within the grace window is a
        /// genuine host-extra — mirror-spawn it now. Deferring this (instead of mirroring on first manifest) is what
        /// prevents the manifest-before-local race from duplicate-spawning + orphaning the real local add.</summary>
        private static void TickResolveDeferredHostOnly()
        {
            if (!RuntimeSyncEnabled()) return;
            float now = Time.realtimeSinceStartup;
            List<HostEntry>? toMirror = null;
            lock (_lock)
            {
                foreach (var he in _hostEntries.Values)
                {
                    if (he.BoundLocal || he.HostOnlyResolved) continue;
                    if (now - he.ReceivedAt < HostOnlyGraceSeconds) continue;          // still waiting for the local
                    if (IsSpecialAdd(he.Msg.AddType)) { he.HostOnlyResolved = true; continue; } // special adds own their lifecycle
                    if (he.Msg.UnitIdValue == 0 || he.Msg.HostSpawnIndex <= 0)
                    {
                        he.HostOnlyResolved = true;
                        // Diagnostic (F4-ADDS): this silent give-up is the blind spot when a host add never appears on the
                        // client — the broadcast arrived but carried no mirrorable identity (UnitIdValue) or no puppet
                        // binding index (HostSpawnIndex), so nothing was ever spawned.
                        Plugin.Log.Warn($"[BossSpawn] host-only add NOT mirrorable (unitIdValue={he.Msg.UnitIdValue} hostIdx={he.Msg.HostSpawnIndex}) {he.Msg.ToCompact()}");
                        continue;
                    }
                    he.BoundLocal = true; he.HostOnlyResolved = true; // claim so we mirror exactly once
                    (toMirror ??= new List<HostEntry>()).Add(he);
                }
            }
            if (toMirror != null)
                foreach (var he in toMirror)
                {
                    SpawnsHostOnlyNoLocal++;
                    Plugin.Log.Info($"[BossSpawn] RT3 mirror host-only add (local never arrived) seq={he.Msg.SequenceIndex} unitId={he.Msg.UnitIdValue} hostIdx={he.Msg.HostSpawnIndex} pos={he.Msg.Position:F1}");
                    RuntimeSpawnManager.MirrorBossAdd(he.Msg.UnitIdValue, he.Msg.Position, he.Msg.HostSpawnIndex);
                }
        }

        private static bool TryFreezeMovement(object unit)
        {
            try
            {
                object? aiAgent = BossReflect.GetMember(unit, "AiAgent") ?? BossReflect.GetMember(unit, "aiAgent");
                if (aiAgent != null)
                {
                    BossReflect.TryInvoke(aiAgent, "StopOnCurrentPosition", out _);
                    BossReflect.TryInvokeArg(aiAgent, "SetCanMove", false, out _);
                }
                var rb = (BossReflect.GetMember(unit, "Rigidbody") ?? BossReflect.GetMember(unit, "rigidbody")) as Rigidbody;
                if (rb != null && !rb.isKinematic) rb.linearVelocity = Vector3.zero; // kinematic bodies reject velocity writes
                return true;
            }
            catch { return false; }
        }

        private static void TryUnfreezeMovement(object unit)
        {
            try
            {
                object? aiAgent = BossReflect.GetMember(unit, "AiAgent") ?? BossReflect.GetMember(unit, "aiAgent");
                if (aiAgent != null) BossReflect.TryInvokeArg(aiAgent, "SetCanMove", true, out _);
            }
            catch { }
        }

        private static void SweepStaleGatedLocked(float now)
        {
            if (_gatedAdds.Count == 0) return;
            List<int>? rm = null;
            foreach (var kv in _gatedAdds)
                if (now - kv.Value.At > GatedInertTtlSeconds) (rm ??= new List<int>()).Add(kv.Key);
            if (rm != null) foreach (var id in rm) _gatedAdds.Remove(id);
        }

        private static bool IsFinite(Vector3 v)
            => !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)
              || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));

        // Boss-owned spawns handled by dedicated systems — must NOT also be driven as generic runtime puppets:
        //   BlackGuildLuciaEye (Lucia eye count/death authority, F5/F6).
        private static readonly HashSet<string> _specialAdds = new HashSet<string> { "BlackGuildLuciaEye" };

        // Phase RT3-Cousin-arms-Anim: GoblinCousinArm is a SCRIPTED PROP — it pops out of a pool, idles, throws, and
        // retracts/disappears, an animation sequence driven entirely by its OWN behaviour tree + animation events. It does
        // NOT navigate. An earlier revision routed it through the RT3-A puppet pipeline (bind local arm to host[seq] →
        // mirrored puppet) to stop double-spawn/double-damage, but ApplyClientEnemyPuppetMode DISABLES the puppet's
        // behaviour tree, and the puppet animator-mirror only reproduces Animator states while the host marks the unit as
        // actively attacking (TryPopulateHostCombatAnimatorStates is called only inside the host combat-action branch).
        // Result: on the client the arm's appear (default state) and attack (host combat window) animations played, but
        // its IDLE and DISAPPEAR states (non-combat, BT-driven) were never reproduced — with the BT dead the Animator just
        // looped its default Appear state. So we keep the arm ALWAYS special-excluded (never a puppet): its own behaviour
        // tree drives the full appear→idle→attack→disappear sequence faithfully (single-player path). Double-spawn is
        // already prevented elsewhere (intro-arm defer + special host-only skip = one local arm per end, no mirror), and
        // the throw is still de-fanged to 0 damage on the client by CousinArmPatches (gated on EnableCousinArmSync), so
        // damage stays host-authoritative. EnableCousinArmSync now gates only the de-fang + group-AoE throw, not puppeting.
        private static bool IsSpecialAdd(string addType)
        {
            if (addType == null) return false;
            if (_specialAdds.Contains(addType)) return true;
            if (addType == "GoblinCousinArm") return true; // never puppet-ize: BT drives appear/idle/attack/disappear
            // F4-ADDS: the Desert BOSS pike exists natively on both ends (each end's own TriggerFight chain spawns it) and
            // the client's copy is driven by the replayed native PikeJump arcs (F4-P1JMP). RT3-binding it as a position
            // puppet double-drives it against that replay (Log344: the client boss pike dragged around / overlapping the
            // minion pikes) — never bind or mirror it.
            if (addType == "HellshrewDesertClausePike") return true;
            return false;
        }

        private static int ReadUnitIdValueFromUnit(object spawnedUnit)
        {
            try
            {
                var unitSO = BossReflect.GetMember(spawnedUnit, "unitSO");
                if (unitSO == null) return 0;
                var idObj = BossReflect.GetMember(unitSO, "id");
                var v = idObj == null ? null : BossReflect.GetMember(idObj, "value");
                return v == null ? 0 : System.Convert.ToInt32(v);
            }
            catch { return 0; }
        }

        // ================================================================== diagnostics

        public static string FormatCounters()
            => $"attributed={SpawnsAttributed} hostBroadcast={SpawnsHostBroadcast} clientLocal={SpawnsClientLocal} bound={SpawnsBound} hostOnly={SpawnsHostOnlyNoLocal} " +
               $"gatedInert={SpawnsGatedInert} snapped={SpawnsSnappedOnBind} hitGateSwallowed={HitsGateSwallowed}";
    }
}
