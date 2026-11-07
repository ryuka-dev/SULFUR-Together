using System;
using System.Collections.Generic;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Effects;
using PerfectRandom.Sulfur.Core.Stats;
using PerfectRandom.Sulfur.Core.Units;
using PerfectRandom.Sulfur.Core.Utilities;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// HR-1 (HOST): settle one damage component of a remote peer's hit with the game's own <c>Unit.ReceiveDamage</c>, so
    /// the crit roll, hitbox multiplier, headshot enchantment, resistances, invulnerability, parry and every
    /// <c>onDamageRecieved</c> consumer (Crusader, Voodoo, boss mechanics, the target dummy's number) behave as they do for
    /// the host's own hits — and report what the game actually did.
    ///
    /// <para><b>The source is not the host's player.</b> The <c>DamageSourceData</c> is built here, not from an
    /// <c>IDamager</c>: <c>isPlayer = false</c> (so the host's kill counter, explosion achievement and weapon-XP
    /// attribution are not credited to the host — and, as a known gap, the game's "a player hit me" reactions keyed on
    /// that flag (AI alert propagation, CrawSentinel / ShavWaWarden blocking, DamageReverser) do not react to a client's
    /// hit, as they did not before either), <c>states.factionId = Player</c> (so faction
    /// checks treat it as a player-side hit), and <c>sourceUnit</c> = that peer's ghost unit on the host
    /// (<see cref="RemotePlayerRegistryManager"/>) — the unit that already stands for the peer, or null when it has none
    /// (downed, between scenes). A player-faction source still makes vanilla set <c>damagedByPlayer</c>, which gates the
    /// host's own <c>GiveExperience</c>; <see cref="OnNpcDie"/> and the scope exit put that flag back.</para>
    ///
    /// <para><b>Scope.</b> While a resolution runs, <see cref="IsResolving"/> is true and <see cref="RemotePeerId"/> names
    /// the peer, including inside every nested ReceiveDamage the game deals from it (Crusader's Holy damage, Voodoo
    /// spread, thorns). Kill attribution and other host hooks read it instead of assuming the host dealt the damage.</para>
    /// </summary>
    internal static class RemoteHitResolver
    {
        // ----------------------------------------------------------------- scope (read by patches and host hooks)

        private static int     _depth;
        private static string? _peerId;

        /// <summary>True while the host is settling a remote peer's hit (including nested damage dealt from it).</summary>
        public static bool IsResolving => _depth > 0;

        /// <summary>The peer whose hit is being settled, or null outside a resolution.</summary>
        public static string? RemotePeerId => _depth > 0 ? _peerId : null;

        // ----------------------------------------------------------------- validation (§12: never trust the wire)

        /// <summary>Anti-forgery ceiling for one damage component and for the Ghost bonus. Not a balance figure — it is
        /// far above anything a weapon reaches; it only stops an absurd value from reaching the game.</summary>
        public const float MaxHitDamage = 100000f;
        private const float MaxHeadshotFactor = 10f;
        private const float MaxEffectScale = 10f;
        private const float MaxHitOffset = 50f;

        /// <summary>Validate and clamp a wire-carried hit in place. Returns false (with a reason) when the request must be
        /// refused outright.</summary>
        public static bool TryValidate(ref NetHitInput h, out string reason)
        {
            reason = "";
            if (!IsFinite(h.RawDamage) || h.RawDamage <= 0f) { reason = "damage"; return false; }
            if (h.RawDamage > MaxHitDamage) h.RawDamage = MaxHitDamage;

            if (!IsAcceptedDamageType(h.DamageType)) { reason = "damageType"; return false; }

            if (float.IsNaN(h.CritChance)) { reason = "critChance"; return false; }
            h.CritChance = Mathf.Clamp01(h.CritChance);

            if (!IsFinite(h.IncreaseHeadshotDamage)) { reason = "headshot"; return false; }
            h.IncreaseHeadshotDamage = Mathf.Clamp(h.IncreaseHeadshotDamage, 0f, MaxHeadshotFactor);

            if (!IsFinite(h.EffectScale)) { reason = "effectScale"; return false; }
            h.EffectScale = Mathf.Clamp(h.EffectScale, 0f, MaxEffectScale);

            h.StateFlags &= (ushort)NetHitStateFlags.All;

            // Cosmetic enum fields fall back to a neutral value instead of refusing the hit.
            if (!Enum.IsDefined(typeof(DecalType), h.ReplacesBloodType)) h.ReplacesBloodType = (int)DecalType.Normal;
            if (!Enum.IsDefined(typeof(CaliberTypes), h.CaliberType)) h.CaliberType = (byte)CaliberTypes.None;
            if (!Enum.IsDefined(typeof(ProjectileEffect), h.ProjectileEffect)) h.ProjectileEffect = (int)ProjectileEffect.None;

            if (!IsFinite(h.GhostBonus) || h.GhostBonus < 0f) h.GhostBonus = 0f;
            if (h.GhostBonus > MaxHitDamage) h.GhostBonus = MaxHitDamage;

            var shape = new HitboxColliders.ShapeId { partAndIndex = h.ShapeId };
            if ((byte)shape.part >= (byte)HitboxColliders.Parts.End) { reason = "shape"; return false; }

            if (h.HasHitOffset && (!IsFinite(h.HitOffset.x) || !IsFinite(h.HitOffset.y) || !IsFinite(h.HitOffset.z)
                                   || h.HitOffset.sqrMagnitude > MaxHitOffset * MaxHitOffset))
                h.HasHitOffset = false;   // an unusable position only loses parry/blood placement, not the hit

            return true;
        }

        public static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        /// <summary>A damage type a player's attack can carry. DamageTypes is <c>: byte</c>: Enum.IsDefined must be
        /// handed a byte (an int THROWS for a byte-backed enum — CG-1b).</summary>
        public static bool IsAcceptedDamageType(int value)
        {
            if (value < 0 || value > byte.MaxValue) return false;
            byte b = (byte)value;
            if (!Enum.IsDefined(typeof(DamageTypes), b)) return false;
            switch ((DamageTypes)b)
            {
                case DamageTypes.None:
                case DamageTypes.End:
                case DamageTypes.Punish:     // environment trap / self-punish damage
                case DamageTypes.Suffocate:  // bypasses the same-faction guard
                case DamageTypes.Sacrifice:  // a weapon's damage to its own owner
                    return false;
            }
            return true;
        }

        // Per-peer arrival budget across every target kind. Generous: a multi-pellet, multi-component weapon at a high
        // fire rate sends hundreds of components a second; this only bounds a flood.
        private const int MaxRequestsPerPeerPerSecond = 1000;
        private static readonly Dictionary<string, (float WindowStart, int Count)> _peerBudget = new Dictionary<string, (float, int)>();

        public static bool ConsumePeerBudget(string peerId)
        {
            string key = peerId ?? "";
            float now = Time.realtimeSinceStartup;
            _peerBudget.TryGetValue(key, out var slot);
            if (now - slot.WindowStart >= 1f) slot = (now, 0);
            if (slot.Count >= MaxRequestsPerPeerPerSecond) { _peerBudget[key] = slot; return false; }
            _peerBudget[key] = (slot.WindowStart, slot.Count + 1);
            return true;
        }

        /// <summary>Level / session teardown. Never touches a resolution that is running — a level change can be set off
        /// from inside one (a death that completes the level), and clearing the scope under it would leave
        /// <see cref="IsResolving"/> false for every later hit.</summary>
        public static void ResetSession()
        {
            _peerBudget.Clear();
            if (_depth > 0) return;
            _frames.Clear();
            _open.Clear();
            _dbpBefore.Clear();
            _peerId = null;
        }

        // ----------------------------------------------------------------- resolution

        /// <summary>Calls the game's damage entry point. A boss adapter can wrap it (e.g. Terrorbaum lifts its eye
        /// invulnerability around the call); everything else uses the virtual <c>ReceiveDamage</c>.</summary>
        public delegate bool ReceiveDamageInvoker(Unit target, float damage, DamageSourceData source, Hitmesh.Data hitbox, Vector3? hitPosition);

        private static readonly ReceiveDamageInvoker DefaultInvoker = (t, d, s, h, p) => t.ReceiveDamage(d, s, h, p);

        public static int Resolved;
        public static int ResolveFailed;
        public static int GhostBonusApplied;
        public static int SynthesizedShape;

        /// <summary>Settle <paramref name="h"/> (already validated) on <paramref name="target"/>. Never throws.</summary>
        public static NetHostHitResult Resolve(Unit target, NetHitInput h, string peerId, int requestSeq, ReceiveDamageInvoker? invoke = null)
        {
            var result = new NetHostHitResult
            {
                RequestSeq = requestSeq,
                DamageType = h.DamageType,
                ShapeId    = h.ShapeId,
                Outcome    = NetHitOutcome.Rejected,
            };
            if (target == null) return result;

            bool wasAlive = target.IsAlive;
            result.WasAlive = wasAlive;

            // --- hitbox: the host's own Hitmesh.Data for that shape (its invulnerable / ignore-resistance state is the
            //     host's). Never the part-less Default for a hit that named a part: Default's 1.0 multiplier beats a
            //     body hit's 0.5. See ResolveHitbox.
            Hitmesh.Data hitbox = Hitmesh.Data.Default;
            var shape = new HitboxColliders.ShapeId { partAndIndex = h.ShapeId };
            if (shape.part != HitboxColliders.Parts.None)
            {
                hitbox = ResolveHitbox(target, shape);
                result.HitMultiplier = Mathf.Max(0f, hitbox.GetShapeMultiplier());
            }

            Vector3? hitPosition = null;
            if (h.HasHitOffset) hitPosition = target.transform.TransformPoint(h.HitOffset);

            Unit? sourceUnit = ResolvePeerUnit(peerId);
            DamageSourceData sd = BuildSource(h, sourceUnit);
            float damage = h.RawDamage;

            // --- Ghost bonus. Vanilla adds weapon.Damage × ExtraDamageGhost after the crit and Oily doubling and before
            //     the hitbox multiplier, reading the weapon object the host does not have. To land on the same final
            //     damage, roll the crit here, pass critChance 0 (Random.Range(0,1) < 0 never holds, so vanilla neither
            //     doubles nor marks it again) and fold the bonus in ahead of the Oily doubling the game still applies.
            if (h.GhostBonus > 0f && target.unitSO != null && target.unitSO.unitType == UnitType.Ghost && wasAlive)
            {
                bool crit = UnityEngine.Random.Range(0f, 1f) < sd.critChance;
                float oily = (sd.damageType == DamageTypes.Fire && target.Stats != null
                              && target.Stats.GetStatus(EntityAttributes.NegativeEffect_Oily) > 0f) ? 2f : 1f;
                damage = h.RawDamage * (crit ? 2f : 1f) + h.GhostBonus / oily;
                sd.critChance = 0f;
                sd.isCritical = crit;
                GhostBonusApplied++;
            }

            if (!wasAlive) result.Outcome = NetHitOutcome.TargetDead;

            bool returned = false;
            bool threw = false;
            float hpBefore = ReadHp(target);
            bool outermost = _depth == 0;
            if (outermost) { _frames.Clear(); _open.Clear(); _dbpBefore.Clear(); }
            string? outerPeer = _peerId;
            _depth++;
            _peerId = peerId;
            try
            {
                try
                {
                    returned = (invoke ?? DefaultInvoker)(target, damage, sd, hitbox, hitPosition);
                    Resolved++;
                }
                catch (Exception ex)
                {
                    threw = true;
                    ResolveFailed++;
                    NetLogger.Warn($"[RemoteHit] ReceiveDamage threw for peer={peerId} seq={requestSeq} target={SafeName(target)}: {ex.GetType().Name}: {ex.Message}");
                    // The game deducts health BEFORE onDamageRecieved and decides death after it, so a throwing handler
                    // can leave a live unit at 0 health that nothing will ever kill. Finish what the game would have
                    // done — inside the scope, so the death is still this peer's and never the host's XP.
                    if (wasAlive && target.IsAlive && ReadHp(target) <= 0f)
                    {
                        try { target.Die(); } catch (Exception dieEx) { NetLogger.Warn($"[RemoteHit] recovery Die failed: {dieEx.Message}"); }
                    }
                }
            }
            finally
            {
                _depth--;
                _peerId = outerPeer;
                if (outermost) RestoreDamagedByPlayer();
            }

            if (wasAlive) result.Outcome = returned ? NetHitOutcome.Applied : NetHitOutcome.Invulnerable;
            result.Killed = wasAlive && !target.IsAlive;
            FillFromFrames(target, result);
            if (threw && wasAlive)
            {
                // The root frame never closed, so its numbers are partial: report what actually left the unit.
                float removed = Mathf.Max(0f, hpBefore - ReadHp(target));
                float secondary = 0f;
                if (result.Secondary != null) foreach (var x in result.Secondary) secondary += x.HealthRemoved;
                result.HealthRemoved = Mathf.Max(0f, removed - secondary);
                if (removed > 0f || result.Killed) result.Outcome = NetHitOutcome.Applied;
            }
            if (outermost) { _frames.Clear(); _open.Clear(); }
            return result;
        }

        /// <summary>The host's <c>Hitmesh.Data</c> for <paramref name="shape"/> on <paramref name="target"/>. Vanilla melee
        /// takes the shape from whichever Hitmesh it struck (<c>foundHitmesh.GetHitShape</c>), which need not be
        /// <c>unit.hitmesh</c>, so every Hitmesh the unit owns is searched. A shape none of them has (a frame-dependent
        /// hitbox, a prefab difference) is not refused — that would drop a real hit — but stands in as a bare
        /// <c>Data</c> carrying only the named part: the part's own multiplier, no organs, not invulnerable unless the
        /// part is Block (as <c>GetHitShape</c> does for a missing Block).</summary>
        private static Hitmesh.Data ResolveHitbox(Unit target, HitboxColliders.ShapeId shape)
        {
            try
            {
                if (target.hitmesh != null)
                {
                    var d = target.hitmesh.GetHitShape(shape);
                    if (d.shapeId.partAndIndex == shape.partAndIndex) return d;
                }
                foreach (var hm in target.GetComponentsInChildren<Hitmesh>(true))
                {
                    if (hm == null || hm == target.hitmesh || hm.owner != target) continue;
                    var d = hm.GetHitShape(shape);
                    if (d.shapeId.partAndIndex == shape.partAndIndex) return d;
                }
            }
            catch { }
            SynthesizedShape++;
            var fallback = Hitmesh.Data.Default;
            fallback.shapeId = shape;
            if (shape.part == HitboxColliders.Parts.Block) fallback.isInvulnerable = true;
            return fallback;
        }

        private static DamageSourceData BuildSource(in NetHitInput h, Unit? sourceUnit)
        {
            var f = (NetHitStateFlags)h.StateFlags;
            var sd = new DamageSourceData
            {
                name         = "CoopRemoteHit",
                damageType   = (DamageTypes)h.DamageType,
                critChance   = h.CritChance,
                melee        = h.Melee,
                isPlayer     = false,
                sourceUnit   = sourceUnit!,
                sourceWeapon = null!,
                sourceEffect = EntityAttributes.None,
                instanceId   = sourceUnit != null ? sourceUnit.GetInstanceID() : 0,
                projectile   = new FiredProjectileState
                {
                    caliberType = (CaliberTypes)h.CaliberType,
                    effect      = (ProjectileEffect)h.ProjectileEffect,
                },
                states = new AttackStates
                {
                    factionId               = FactionIds.Player,
                    replacesBloodType       = (DecalType)h.ReplacesBloodType,
                    effectScale             = h.EffectScale,
                    increaseHeadshotDamage  = h.IncreaseHeadshotDamage,
                    alwaysSpawnOrgans       = (f & NetHitStateFlags.AlwaysSpawnOrgans) != 0,
                    corpseBounceExplosion   = (f & NetHitStateFlags.CorpseBounceExplosion) != 0,
                    foulFateBounceExplosion = (f & NetHitStateFlags.FoulFateBounceExplosion) != 0,
                    isSilenced              = (f & NetHitStateFlags.IsSilenced) != 0,
                    notCreatedByPlayer      = (f & NetHitStateFlags.NotCreatedByPlayer) != 0,
                    dropDisabledMoney       = (f & NetHitStateFlags.DropDisabledMoney) != 0,
                    dropDisabledOrgan       = (f & NetHitStateFlags.DropDisabledOrgan) != 0,
                    disableBloodOnHit       = (f & NetHitStateFlags.DisableBloodOnHit) != 0,
                    isHealingAllyBullet     = (f & NetHitStateFlags.IsHealingAllyBullet) != 0,
                    isPeriodicModifier      = false,
                },
            };
            if (sourceUnit != null)
            {
                sd.sourcePosition = sourceUnit.transform.position;
                sd.alertPosition  = sourceUnit.transform.position;
            }
            return sd;
        }

        /// <summary>The unit that stands for <paramref name="peerId"/> on the host — its Plan B ghost — or null.</summary>
        public static Unit? ResolvePeerUnit(string? peerId)
        {
            if (string.IsNullOrEmpty(peerId)) return null;
            try
            {
                object? ghost;
                lock (RemotePlayerRegistryManager.GhostUnitsByPeer)
                    RemotePlayerRegistryManager.GhostUnitsByPeer.TryGetValue(peerId!, out ghost);
                return ghost is Unit u && u != null ? u : null;
            }
            catch { return null; }
        }

        // ----------------------------------------------------------------- per-call frames (fed by RemoteHitPatches)

        private sealed class Frame
        {
            public Unit   Target = null!;
            public int    Parent = -1;      // nearest open frame with the SAME target, or -1
            public float  HpBefore;
            public bool   AliveBefore;
            public bool   Logged;
            public float  Damage;
            public float  Incoming;
            public byte   Type;
            public bool   Crit;
            public ushort Shape;
            public bool   Closed;
            public bool   Returned;
            public float  HpAfter;
            public bool   AliveAfter;
            public float  ChildRemoved;     // health removed by nested same-target frames (excluded from this frame's own)
        }

        private static readonly List<Frame> _frames = new List<Frame>();
        private static readonly List<int> _open = new List<int>();
        // damagedByPlayer of each Npc as it was the first time this resolution touched it.
        private static readonly Dictionary<Npc, bool> _dbpBefore = new Dictionary<Npc, bool>();

        /// <summary>Unit.ReceiveDamage prefix. Returns the frame index (or -1 outside a resolution).</summary>
        public static int OnReceiveDamageEnter(Unit unit)
        {
            if (_depth <= 0 || unit == null) return -1;
            try
            {
                if (unit is Npc npc && !_dbpBefore.ContainsKey(npc)) _dbpBefore[npc] = npc.damagedByPlayer;

                var f = new Frame { Target = unit, AliveBefore = unit.IsAlive, HpBefore = ReadHp(unit) };
                for (int i = _open.Count - 1; i >= 0; i--)
                    if (ReferenceEquals(_frames[_open[i]].Target, unit)) { f.Parent = _open[i]; break; }
                _frames.Add(f);
                int idx = _frames.Count - 1;
                _open.Add(idx);
                return idx;
            }
            catch { return -1; }
        }

        /// <summary>Unit.CreateDamageLog postfix: the final amount of the innermost open ReceiveDamage on that unit.</summary>
        public static void OnCreateDamageLog(Unit unit, float damage, float incoming, DamageTypes type, ushort shape, bool crit)
        {
            if (_depth <= 0 || _open.Count == 0) return;
            var f = _frames[_open[_open.Count - 1]];
            if (!ReferenceEquals(f.Target, unit) || f.Logged) return;
            f.Logged = true; f.Damage = damage; f.Incoming = incoming; f.Type = (byte)type; f.Shape = shape; f.Crit = crit;
        }

        /// <summary>Unit.ReceiveDamage postfix.</summary>
        public static void OnReceiveDamageExit(int frame, bool returned)
        {
            if (frame < 0 || frame >= _frames.Count) return;
            try
            {
                var f = _frames[frame];
                f.Closed = true; f.Returned = returned; f.HpAfter = ReadHp(f.Target); f.AliveAfter = f.Target != null && f.Target.IsAlive;
                if (f.Parent >= 0) _frames[f.Parent].ChildRemoved += Mathf.Max(0f, f.HpBefore - f.HpAfter);
            }
            catch { }
            TruncateOpen(frame);
        }

        /// <summary>Unit.ReceiveDamage finalizer: drop this frame and everything above it if the call threw.</summary>
        public static void OnReceiveDamageFinalize(int frame)
        {
            if (frame < 0) return;
            TruncateOpen(frame);
        }

        private static void TruncateOpen(int frame)
        {
            int at = _open.LastIndexOf(frame);
            if (at >= 0) _open.RemoveRange(at, _open.Count - at);
        }

        /// <summary>Npc.Die prefix: vanilla's GiveExperience (inside Die) credits the HOST's weapons when
        /// damagedByPlayer is set. A remote peer's hit must not set it for the host — put back what it was.</summary>
        public static void OnNpcDie(Npc npc)
        {
            if (_depth <= 0 || npc == null) return;
            try { if (_dbpBefore.TryGetValue(npc, out bool before) && !before) npc.damagedByPlayer = false; }
            catch { }
        }

        private static void RestoreDamagedByPlayer()
        {
            foreach (var kv in _dbpBefore)
            {
                try { if (!kv.Value && kv.Key != null) kv.Key.damagedByPlayer = false; } catch { }
            }
            _dbpBefore.Clear();
        }

        private static void FillFromFrames(Unit target, NetHostHitResult result)
        {
            int root = -1;
            for (int i = 0; i < _frames.Count; i++)
                if (ReferenceEquals(_frames[i].Target, target) && _frames[i].Parent < 0) { root = i; break; }
            if (root < 0) return;

            var r = _frames[root];
            if (r.Logged)
            {
                result.Damage         = r.Damage;
                result.IncomingDamage = r.Incoming;
                result.IsCritical     = r.Crit;
                result.DamageType     = r.Type;
            }
            if (r.Closed) result.HealthRemoved = Mathf.Max(0f, r.HpBefore - r.HpAfter - r.ChildRemoved);

            for (int i = root + 1; i < _frames.Count; i++)
            {
                var f = _frames[i];
                if (!ReferenceEquals(f.Target, target) || !f.Closed || !f.Logged) continue;
                (result.Secondary ??= new List<NetHostHitSecondary>()).Add(new NetHostHitSecondary
                {
                    Damage        = f.Damage,
                    HealthRemoved = Mathf.Max(0f, f.HpBefore - f.HpAfter - f.ChildRemoved),
                    DamageType    = f.Type,
                    IsCritical    = f.Crit,
                    Killed        = f.AliveBefore && !f.AliveAfter,
                });
                if (result.Secondary.Count >= NetHostHitResult.MaxSecondary) break;
            }
        }

        private static float ReadHp(Unit u)
        {
            try { return u != null && u.Stats != null ? u.Stats.GetStatus(EntityAttributes.Status_CurrentHealth) : 0f; }
            catch { return 0f; }
        }

        private static string SafeName(UnityEngine.Object o)
        {
            try { return o != null ? o.name : "null"; } catch { return "?"; }
        }
    }
}
