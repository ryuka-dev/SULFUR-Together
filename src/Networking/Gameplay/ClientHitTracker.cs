using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using PerfectRandom.Sulfur.Core.Units;
using PerfectRandom.Sulfur.Core.Utilities;
using SULFURTogether.Api;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// HR-2 (CLIENT): pairs each forwarded hit with the host's <see cref="NetHostHitResult"/> and raises the public
    /// <see cref="NetCombat"/> events. Owns the client side of the request/result contract:
    /// <list type="bullet">
    /// <item>every forwarded RequestSeq resolves exactly once — its result, or Lost once
    /// <see cref="NetCombat.ResultTimeoutSeconds"/> have passed AND the network has been polled since (a client stall
    /// must not turn results already waiting in the socket into Lost);</item>
    /// <item>resolutions are raised in RequestSeq order: results come back in request order on one reliable-ordered
    /// channel, so when the result for N arrives, every older seq still pending was never answered and resolves Lost
    /// first;</item>
    /// <item>a result for an unknown or already-resolved seq is dropped.</item>
    /// </list>
    /// What the client itself knows — the local target, weapon, part and hit position — comes from the request it kept,
    /// never from the wire.
    ///
    /// <para>Kill credit: the client's existing XP credit point (a host death applied to a puppet the local player hit)
    /// calls <see cref="OnLocalKill"/> once the local death has run. If hits on that target are still unresolved — the
    /// host's death event normally overtakes the result of the hit that caused it — the credit waits until they
    /// resolve, so it fires after the killing hit's resolution and can say whether it was the killing blow. Boss-kind
    /// targets (no XP credit on this end) are credited from a Killed result, only for a unit of the game's Boss type and
    /// at most once per unit per level.</para>
    ///
    /// <para>HR-3: the gameplay effects of those verdicts on this end — lifesteal from a resolved hit, the kill count from
    /// a credit — run through <see cref="ClientHitEffects"/> just before the matching event, whether or not anything
    /// subscribes to it.</para>
    /// </summary>
    internal static class ClientHitTracker
    {
        private sealed class Pending
        {
            public int      Seq;
            public NetHitTargetKind Kind;
            public object?  Target;        // local unit
            public int      TargetId;      // its Unity instance id (0 when none)
            public object?  Weapon;
            public bool     Melee;
            public int      DamageType;
            public ushort   ShapeId;
            public Vector3? HitPosition;
            public NetHitInput Hit;          // HR-5: the killing hit's inputs feed the organ roll
            public float    SentAt;
            public int      OverdueSinceFrame = -1;
        }

        private sealed class KillState
        {
            public object? Target;
            public bool    IsNpc;          // read while the unit exists; it can be gone when the credit fires
            public int     KillingSeq = -1;
            public bool    KillingHeadshot;
            public object? KillingWeapon;
            public NetHitInput KillingHit;
            public Vector3? KillingPosition;
            public bool    BossCredit;     // credited from the killing hit's result (boss kinds), not a local death
            // HR-5: this client's hits resolved TargetDead after the killing hit (the rest of a shotgun volley).
            public List<(NetHitInput Hit, object? Weapon, Vector3? Position)>? VolleyAfterKill;
            public object? LastWeapon;
            public float   DamageDealt;
            public bool    CreditPending;  // the death arrived; waiting for this target's unresolved hits
            public int     Experience;
            public float   TouchedAt;
        }

        // Insertion order = RequestSeq order (seqs are drawn increasing and registered as sent).
        private static readonly LinkedList<Pending> _pending = new LinkedList<Pending>();
        private static readonly Dictionary<int, LinkedListNode<Pending>> _bySeq = new Dictionary<int, LinkedListNode<Pending>>();
        // Unresolved non-corpse hits per target: a kill credit waits for these. Corpse hits are aimed at a body that is
        // already dead and say nothing about who killed it, so they never hold a credit back.
        private static readonly Dictionary<int, int> _pendingByTarget = new Dictionary<int, int>();
        private static readonly Dictionary<int, KillState> _kills = new Dictionary<int, KillState>(); // by target instance id
        // Targets already credited this level — the guard against a second credit for the same unit.
        private static readonly HashSet<int> _credited = new HashSet<int>();
        // Bumped by every Reset: an event handler that tears the session down mid-raise must not let the caller carry on
        // into state that no longer exists.
        private static int _epoch;

        public static int Forwarded, Resolved, Lost, LateDropped, KillsCredited;

        private const float KillStateMaxAgeSeconds = 30f;
        private const int MaxVolleyAfterKill = 16;
        private static readonly IReadOnlyList<SecondaryHit> NoSecondary = new ReadOnlyCollection<SecondaryHit>(Array.Empty<SecondaryHit>());

        /// <summary>Register a hit that was just sent and raise <see cref="NetCombat.LocalHitForwarded"/>.</summary>
        public static void OnForwarded(int seq, NetHitTargetKind kind, object? target, object? weapon, in NetHitInput hit, Vector3? hitPosition)
        {
            try
            {
                if (_bySeq.ContainsKey(seq)) return; // never expected: seqs are unique for the process lifetime
                int targetId = target is UnityEngine.Object uo && uo != null ? uo.GetInstanceID() : 0;
                var p = new Pending
                {
                    Seq = seq, Kind = kind, Target = target, TargetId = targetId, Weapon = weapon, Melee = hit.Melee,
                    DamageType = hit.DamageType, ShapeId = hit.ShapeId, HitPosition = hitPosition, Hit = hit,
                    SentAt = Time.realtimeSinceStartup,
                };
                _bySeq[seq] = _pending.AddLast(p);
                if (CountsForKill(p))
                {
                    _pendingByTarget.TryGetValue(targetId, out int n);
                    _pendingByTarget[targetId] = n + 1;
                    if (weapon != null) GetKill(targetId, target).LastWeapon = weapon;
                }
                Forwarded++;

                NetCombat.RaiseForwarded(new LocalHitForwardedArgs(seq, target, weapon, hit.Melee, hit.DamageType,
                    hit.ShapeId & 0xFF, hit.ShapeId, hitPosition, hit.RawDamage));
            }
            catch (Exception ex) { NetLogger.Warn($"[ClientHit] tracker forward failed seq={seq}: {ex.Message}"); }
        }

        /// <summary>The host's result arrived.</summary>
        public static void OnResult(NetHostHitResult r)
        {
            if (r == null) return;
            if (!_bySeq.TryGetValue(r.RequestSeq, out var node))
            {
                LateDropped++; // already resolved as Lost, or never ours
                return;
            }
            int epoch = _epoch;

            // Anything older still pending was never answered (results arrive in request order) — resolve it first, so
            // resolutions keep RequestSeq order.
            while (_pending.First != null && _pending.First != node)
            {
                var older = _pending.First;
                Remove(older);
                LoseOne(older.Value);
                if (epoch != _epoch) return;
            }

            Remove(node);
            var p = node.Value;
            IReadOnlyList<SecondaryHit> secondary = NoSecondary;
            float dealt = r.Outcome == NetHitOutcome.Applied ? r.HealthRemoved : 0f;
            if (r.Secondary != null && r.Secondary.Count > 0)
            {
                var list = new SecondaryHit[r.Secondary.Count];
                for (int i = 0; i < list.Length; i++)
                {
                    var s = r.Secondary[i];
                    list[i] = new SecondaryHit(s.Damage, s.HealthRemoved, s.DamageType, s.IsCritical, s.Killed);
                    dealt += s.HealthRemoved;
                }
                secondary = new ReadOnlyCollection<SecondaryHit>(list);
            }

            // Kill bookkeeping BEFORE raising, so a handler that tears the session down cannot leave it half done.
            if (CountsForKill(p))
            {
                var k = GetKill(p.TargetId, p.Target);
                k.DamageDealt += dealt;
                if (r.Killed && k.KillingSeq < 0)
                {
                    k.KillingSeq = p.Seq;
                    int part = p.ShapeId & 0xFF;
                    k.KillingHeadshot = part == (int)PerfectRandom.Sulfur.Core.HitboxColliders.Parts.Head
                                     || part == (int)PerfectRandom.Sulfur.Core.HitboxColliders.Parts.Eye;
                    k.KillingWeapon = p.Weapon;
                    k.KillingHit = p.Hit;
                    k.KillingPosition = p.HitPosition;
                    // A boss role, the Emperor worm and spider never reach the puppet death-credit hook (no XP is
                    // credited for them on this end), so their killing blow is the credit — but only for a unit of the
                    // game's Boss type: a Witch illusion, a phase witch or a worm section dying is not "the enemy".
                    if (IsBossKind(p.Kind) && IsBossUnit(p.Target))
                    {
                        k.CreditPending = true;
                        k.Experience = 0;
                        k.BossCredit = true;
                    }
                }
                else if (r.Outcome == NetHitOutcome.TargetDead && k.KillingSeq >= 0 && p.Seq > k.KillingSeq && !k.BossCredit)
                {
                    var list = k.VolleyAfterKill ??= new List<(NetHitInput, object?, Vector3?)>();
                    if (list.Count < MaxVolleyAfterKill) list.Add((p.Hit, p.Weapon, p.HitPosition));
                }
            }

            var outcome = (HitOutcome)(byte)r.Outcome;
            RaiseResolved(p, outcome, r.Damage, r.HealthRemoved, r.IncomingDamage, r.DamageType, r.IsCritical,
                r.HitMultiplier, r.WasAlive, r.Killed, secondary);
            if (epoch != _epoch) return;
            // HR-3: the end-of-hit effect the game gives the attacker (lifesteal), after the seq has its resolution.
            ClientHitEffects.ApplyLifesteal(p.Target is UnityEngine.Object lt && lt == null ? null : p.Target, r);
            if (epoch != _epoch) return;
            TryFlushKill(p.TargetId);
        }

        /// <summary>A puppet the local player hit has died locally and its XP credit ran (<paramref name="experience"/> =
        /// what was distributed, 0 if nothing). Fires <see cref="NetCombat.LocalKillCredited"/> once that target's hits
        /// resolve.</summary>
        public static void OnLocalKill(object? target, int experience)
        {
            try
            {
                int id = target is UnityEngine.Object uo && uo != null ? uo.GetInstanceID() : 0;
                if (id == 0 || _credited.Contains(id)) return;
                var k = GetKill(id, target);
                k.CreditPending = true;
                k.Experience = experience;
                TryFlushKill(id);
            }
            catch (Exception ex) { NetLogger.Warn($"[ClientHit] tracker kill failed: {ex.Message}"); }
        }

        /// <summary>Main-thread tick, called after the frame's network poll: resolve overdue hits as Lost and forget old
        /// kill state. A hit is Lost only once it is overdue AND two further frames (each with its poll and the Steam
        /// relay pump) have passed without its result — after a stall, results already waiting are read first.</summary>
        public static void Tick()
        {
            if (_pending.Count == 0 && _kills.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            int frame = Time.frameCount;
            int epoch = _epoch;
            while (_pending.First != null && now - _pending.First.Value.SentAt >= NetCombat.ResultTimeoutSeconds)
            {
                var head = _pending.First.Value;
                if (head.OverdueSinceFrame < 0) { head.OverdueSinceFrame = frame; break; }
                if (frame < head.OverdueSinceFrame + 2) break;
                var node = _pending.First;
                Remove(node);
                LoseOne(node.Value);
                if (epoch != _epoch) return;
            }
            if (_kills.Count > 0)
            {
                List<int>? stale = null;
                foreach (var kv in _kills)
                {
                    var k = kv.Value;
                    if (now - k.TouchedAt <= KillStateMaxAgeSeconds) continue;
                    if (_pendingByTarget.ContainsKey(kv.Key)) continue;
                    (stale ??= new List<int>()).Add(kv.Key);
                }
                if (stale != null) foreach (int id in stale) _kills.Remove(id);
            }
        }

        /// <summary>Session teardown: every outstanding hit resolves (Lost) — each seq still gets its one resolution —
        /// but no kill is credited from inside a teardown. Then all state is dropped.</summary>
        public static void Reset()
        {
            _kills.Clear();
            _pendingByTarget.Clear();
            _credited.Clear();
            int epoch = ++_epoch;
            while (_pending.First != null)
            {
                var node = _pending.First;
                _bySeq.Remove(node.Value.Seq);
                _pending.Remove(node);
                Lost++;
                RaiseResolved(node.Value, HitOutcome.Lost, 0f, 0f, 0f, node.Value.DamageType, false, 0f, false, false, NoSecondary);
                if (epoch != _epoch) return; // a handler started another teardown; it finishes the job
            }
            _kills.Clear();
        }

        /// <summary>Level change: units are torn down with the level, so the per-unit credit guard can go. Outstanding
        /// hits keep their timeout — their results may still arrive.</summary>
        public static void ClearLevel()
        {
            _credited.Clear();
        }

        public static string FormatSummary()
            => $"forwarded={Forwarded} resolved={Resolved} lost={Lost} lateDropped={LateDropped} killsCredited={KillsCredited} pending={_pending.Count}";

        // ----------------------------------------------------------------------------------------------------------

        private static bool CountsForKill(Pending p) => p.TargetId != 0 && p.Kind != NetHitTargetKind.Corpse;

        private static bool IsBossKind(NetHitTargetKind kind)
            => kind == NetHitTargetKind.BossRole || kind == NetHitTargetKind.EmperorWormTail || kind == NetHitTargetKind.EmperorSpider;

        private static bool IsBossUnit(object? target)
        {
            try { return target is Unit u && u != null && u.unitSO != null && u.unitSO.unitType == UnitType.Boss; }
            catch { return false; }
        }

        private static void LoseOne(Pending p)
        {
            Lost++;
            int epoch = _epoch;
            RaiseResolved(p, HitOutcome.Lost, 0f, 0f, 0f, p.DamageType, false, 0f, false, false, NoSecondary);
            if (epoch != _epoch) return;
            TryFlushKill(p.TargetId);
        }

        private static void RaiseResolved(Pending p, HitOutcome outcome, float damage, float healthRemoved, float incoming,
            int damageType, bool crit, float multiplier, bool wasAlive, bool killed, IReadOnlyList<SecondaryHit> secondary)
        {
            Resolved++;
            object? target = p.Target is UnityEngine.Object uo && uo == null ? null : p.Target;
            NetCombat.RaiseResolved(new LocalHitResolvedArgs(p.Seq, target, outcome, damage, healthRemoved, incoming,
                damageType, crit, p.ShapeId & 0xFF, multiplier, p.Weapon, p.Melee, p.HitPosition, wasAlive, killed, secondary));
        }

        private static void TryFlushKill(int targetId)
        {
            if (targetId == 0 || !_kills.TryGetValue(targetId, out var k)) return;
            if (!k.CreditPending || _credited.Contains(targetId) || _pendingByTarget.ContainsKey(targetId)) return;
            _credited.Add(targetId);
            _kills.Remove(targetId);
            KillsCredited++;
            bool killing = k.KillingSeq >= 0;
            object? target = k.Target is UnityEngine.Object uo && uo == null ? null : k.Target;
            ClientHitEffects.OnKillCredited(k.IsNpc, killing, k.DamageDealt);
            if (killing && !k.BossCredit)
            {
                ClientHitEffects.RollKillingHitOrgans(target, k.KillingHit, k.KillingWeapon, k.KillingPosition);
                if (k.VolleyAfterKill != null)
                    foreach (var v in k.VolleyAfterKill) ClientHitEffects.RollKillingHitOrgans(target, v.Hit, v.Weapon, v.Position);
            }
            NetCombat.RaiseKillCredited(new LocalKillCreditedArgs(target, killing, killing ? k.KillingSeq : -1,
                killing ? k.KillingWeapon : k.LastWeapon, killing && k.KillingHeadshot, k.Experience, k.DamageDealt));
        }

        private static KillState GetKill(int id, object? target)
        {
            if (!_kills.TryGetValue(id, out var k)) { k = new KillState { Target = target, IsNpc = ReadIsNpc(target) }; _kills[id] = k; }
            k.TouchedAt = Time.realtimeSinceStartup;
            return k;
        }

        private static bool ReadIsNpc(object? target)
        {
            try { return target is Unit u && u != null && u.isNpc; } catch { return false; }
        }

        private static void Remove(LinkedListNode<Pending> node)
        {
            var p = node.Value;
            _bySeq.Remove(p.Seq);
            if (node.List != null) _pending.Remove(node);
            if (CountsForKill(p) && _pendingByTarget.TryGetValue(p.TargetId, out int n))
            {
                if (n <= 1) _pendingByTarget.Remove(p.TargetId);
                else _pendingByTarget[p.TargetId] = n - 1;
            }
        }
    }
}
