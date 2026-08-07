using System;
using System.Collections.Generic;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Stats;
using PerfectRandom.Sulfur.Core.Units;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// ST-1 / ST-2: host-authoritative <b>enemy status effects</b> (the negative-effect family — Petrified, Burning,
    /// Frozen, Poisoned, Stunned, Charmed, Rooted, ...).
    ///
    /// <para><b>The gap this closes.</b> Applying a status and applying damage are two independent calls:
    /// <c>ProjectileUtilities.ProcessUnitHit</c> runs <c>Unit.ApplyHitModifiers</c> (the weapon-enchantment proc) and
    /// only then <c>Unit.ReceiveDamage</c>. Damage was already host-authoritative, but the host applies a client's hit
    /// with a raw health write that never runs <c>ApplyHitModifiers</c> — so a client's enchantment landed on its local
    /// puppet ONLY. The puppet showed the petrified material and shatter VFX while the host's real NPC, which owns the
    /// movement the puppet mirrors, was never petrified and kept walking (<c>AttributeEffect.UpdateMovementSpeed</c>'s
    /// <c>SetUnitSpeed(0)</c> only ever runs on the machine holding the status). The converse was broken too: a status
    /// the HOST applied stopped the enemy on both ends but was invisible on the client.</para>
    ///
    /// <para><b>ST-1 (client → host)</b> — <see cref="TryInterceptClientPuppetHitModifiers"/>: on a client, an on-hit
    /// modifier list aimed at a host-bound puppet is never applied locally. The client rolls each modifier's
    /// <c>procChance</c> (consuming exactly the RNG draws the suppressed vanilla call would have) and forwards the
    /// entries that passed; the host applies them to the real NPC through the vanilla <c>ModifyStatus</c>, so
    /// resistances, diminishing returns and the status cap stay host-owned.</para>
    ///
    /// <para><b>ST-2 (host → clients)</b> — <see cref="ReportHostUnitStatusEdge"/>: the host broadcasts the START and
    /// END edges of every negative status on a roster-bound enemy, taken from <c>Unit.OnStatusUpdated</c> — the same
    /// canonical callback the game uses to drive the effect's own visuals. Clients write the value with
    /// <c>SetStatus</c> so the vanilla effect plays on the puppet. Only edges travel: the status decays through that
    /// same callback every frame, and the receiving client runs the vanilla decay itself once the status is set.</para>
    ///
    /// <para><b>Ownership.</b> The host is the sole authority for what a status IS; a client's copy is a projection kept
    /// for presentation and re-asserted absolutely on the next edge. Nothing here touches player statuses — the
    /// interception is scoped to host-bound puppet NPCs, and the broadcast to roster-bound NPCs.</para>
    /// </summary>
    internal static class UnitStatusSyncManager
    {
        // ----------------------------------------------------------------
        // Shared: which EntityAttributes this channel is allowed to carry
        // ----------------------------------------------------------------

        // Built from the enum's own naming rather than a hardcoded id list: EntityAttributes is a ushort enum the game
        // appends to between versions, so a literal table would silently start meaning something else after an update.
        private static HashSet<ushort>? _negativeEffectIds;

        private static HashSet<ushort> NegativeEffectIds
        {
            get
            {
                if (_negativeEffectIds != null) return _negativeEffectIds;
                var set = new HashSet<ushort>();
                try
                {
                    foreach (EntityAttributes value in Enum.GetValues(typeof(EntityAttributes)))
                    {
                        string name = value.ToString();
                        if (name.StartsWith("NegativeEffect_", StringComparison.Ordinal))
                            set.Add((ushort)value);
                    }
                }
                catch (Exception ex) { Plugin.Log.Warn($"[UnitStatus] failed to enumerate EntityAttributes: {ex.Message}"); }
                _negativeEffectIds = set;
                return set;
            }
        }

        /// <summary>Only the negative-effect family crosses the wire. <c>OnStatusUpdated</c> also fires for health,
        /// oxygen, luck and every stat — none of which belongs on this channel (health has its own authority).</summary>
        private static bool IsSyncableStatus(ushort attribute) => NegativeEffectIds.Contains(attribute);

        /// <summary>Upper bound for one wire-carried status amount. The real cap is per-attribute and enforced inside
        /// <c>ModifyStatus</c>/<c>SetStatus</c> (<c>GetMaxStatusValue</c>); this only rejects absurd/hostile values
        /// before they reach the game.</summary>
        private const float MaxStatusValue = 1000f;

        private static bool IsSaneValue(float v) => !float.IsNaN(v) && !float.IsInfinity(v) && v > 0f && v <= MaxStatusValue;

        // ----------------------------------------------------------------
        // ST-1 — client side: intercept, roll, forward
        // ----------------------------------------------------------------

        private static int _clientRequestSeq;
        private static int _clientIntercepted;      // modifier lists suppressed on a host-bound puppet
        private static int _clientForwarded;        // requests actually sent (local player attacks that procced)
        // Why an intercepted list did NOT become a request. Without this split, a low forwarded/intercepted ratio is
        // ambiguous: an enchantment's proc chance and a failed attacker identification look identical from outside.
        private static int _clientNoProc;           // rolled, nothing passed procChance
        private static int _clientNotLocalAttacker; // procced, but the attacker is not this client's own player
        private static int _clientLocalOnlyApplied; // non-syncable attributes applied locally, vanilla-style

        /// <summary>
        /// Client: <paramref name="target"/> is about to have on-hit modifiers applied locally. Returns true when the
        /// caller must SKIP the vanilla application.
        /// <para>Suppression is decided by the target alone — any status on a host-driven puppet is host-owned, so a
        /// non-player source (an enemy's own projectile, a local hazard) is suppressed WITHOUT being forwarded: the host
        /// simulates that source itself and the result comes back through ST-2. Forwarding is narrower and fail-closed —
        /// only a positively identified local-player attacker produces a request.</para>
        /// </summary>
        public static bool TryInterceptClientPuppetHitModifiers(Unit target, IList<(ushort Attribute, float Value, float ProcChance)> modifiers, Unit attacker)
        {
            try
            {
                if (NetConfig.GetMode() != NetMode.Client) return false;
                if (target == null || modifiers == null || modifiers.Count == 0) return false;
                if (target is not Npc) return false;

                if (!NetGameplayProbeManager.TryGetClientPuppetBinding(target, out int hostIdx, out string unitIdentifier))
                    return false; // client-only / unbound entity — vanilla local behaviour is correct for it

                _clientIntercepted++;

                // Roll every modifier exactly as the suppressed vanilla ApplyHitModifiers would, unconditionally and in
                // the same order. UnityEngine.Random is a global shared stream, so consuming a different number of draws
                // than vanilla would silently shift unrelated systems that read it.
                List<ushort>? attrs = null;
                List<float>?  values = null;
                List<(ushort Attribute, float Value)>? localOnly = null;
                for (int i = 0; i < modifiers.Count; i++)
                {
                    var m = modifiers[i];
                    if (UnityEngine.Random.Range(0f, 100f) >= m.ProcChance) continue;

                    // This channel owns the negative-effect family and nothing else. ApplyHitModifiers can carry ANY
                    // EntityAttributes (its data comes from ItemAttribute.applyAttributeModifier / an NPC's
                    // modifiersOnHitOverride), and suppressing one we don't forward would delete it outright — so
                    // anything off the whitelist is applied right here, exactly as the suppressed vanilla call would.
                    // Deliberately not widened into the wire format instead: letting a client address arbitrary
                    // attributes on a host unit would hand it Status_CurrentHealth and every stat.
                    if (!IsSyncableStatus(m.Attribute))
                    {
                        (localOnly ??= new List<(ushort, float)>()).Add((m.Attribute, m.Value));
                        continue;
                    }
                    if (!IsSaneValue(m.Value))
                    {
                        Plugin.Log.Warn($"[UnitStatus] dropping out-of-range local modifier attr={(EntityAttributes)m.Attribute} value={m.Value}");
                        continue;
                    }
                    if ((attrs ??= new List<ushort>()).Count >= NetClientUnitStatusRequest.MaxEntries) continue;
                    attrs.Add(m.Attribute);
                    (values ??= new List<float>()).Add(m.Value);
                }

                ApplyLocalOnlyModifiers(target, localOnly, attacker);

                // Fail-closed: only a hit we can positively attribute to this client's own player is forwarded. Anything
                // else (an enemy's own projectile, a local hazard) is host-simulated and comes back through ST-2.
                if (!NetPlayerLifeManager.IsLocalPlayerUnit(attacker))
                {
                    _clientNotLocalAttacker++;
                    return true;
                }

                if (attrs == null || values == null || attrs.Count == 0)
                {
                    _clientNoProc++;
                    return true; // nothing procced — still suppressed, nothing to send
                }

                if (!NetRunStateBridge.TryGetLocalRunState(out var state) || !state.HasLevel)
                    return true;

                NetGameplaySyncBridge.SendClientUnitStatusRequest(new NetClientUnitStatusRequest
                {
                    ChapterName          = state.ChapterName,
                    LevelIndex           = state.LevelIndex,
                    HasLevelSeed         = state.HasLevelSeed,
                    LevelSeed            = state.LevelSeed,
                    RequestSeq           = ++_clientRequestSeq,
                    TargetHostSpawnIndex = hostIdx,
                    TargetUnitIdentifier = unitIdentifier,
                    Attributes           = attrs.ToArray(),
                    Values               = values.ToArray(),
                    SentAt               = Time.realtimeSinceStartup,
                });
                _clientForwarded++;

                if (Plugin.Cfg.LogUnitStatusSync.Value)
                    NetLogger.Info($"[UnitStatus] client→host seq={_clientRequestSeq} hostIdx={hostIdx} unit={unitIdentifier} {DescribeEntries(attrs, values)}");

                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[UnitStatus] TryInterceptClientPuppetHitModifiers failed: {ex.GetType().Name}: {ex.Message}");
                return false; // never swallow a status because OUR code threw
            }
        }

        /// <summary>Reproduce the suppressed vanilla <c>ApplyHitModifiers</c> body for the attributes this channel does
        /// not carry, so intercepting a mixed modifier list never silently deletes the part we don't own.</summary>
        private static void ApplyLocalOnlyModifiers(Unit target, List<(ushort Attribute, float Value)>? localOnly, Unit attacker)
        {
            if (localOnly == null || localOnly.Count == 0 || target.Stats == null) return;
            for (int i = 0; i < localOnly.Count; i++)
            {
                var id = (EntityAttributes)localOnly[i].Attribute;
                if (attacker != null) target.RegisterAppliedStatus(id, attacker);
                target.Stats.ModifyStatus(id, localOnly[i].Value);
                _clientLocalOnlyApplied++;
            }
        }

        // ----------------------------------------------------------------
        // ST-1 — host side: validate and apply to the real NPC
        // ----------------------------------------------------------------

        // Per-peer arrival budget. An on-hit proc is a rare, low-rate event (one roll per landed shot, and most weapons
        // carry no modifiers at all), so a peer flooding this channel is malformed or hostile either way.
        private const int   MaxRequestsPerPeerPerSecond = 40;
        private static readonly Dictionary<string, (float WindowStart, int Count)> _hostPeerBudget = new Dictionary<string, (float, int)>();

        private static int _hostRequestsRecv;
        private static int _hostRequestsApplied;
        private static int _hostRequestsRejected;

        public static void HandleClientStatusRequest(NetClientUnitStatusRequest request, string peerId)
        {
            if (request == null) return;
            try
            {
                if (NetConfig.GetMode() != NetMode.Host) return;
                _hostRequestsRecv++;

                if (!NetRunStateBridge.TryGetLocalRunState(out var hostState) || !request.MatchesScene(hostState))
                {
                    _hostRequestsRejected++;
                    if (Plugin.Cfg.LogUnitStatusSync.Value)
                        NetLogger.Warn($"[UnitStatus] REJECT scene-mismatch peer={peerId} seq={request.RequestSeq} req={request.SceneKey} host={hostState?.ChapterName}:{hostState?.LevelIndex}");
                    return;
                }

                if (!ConsumePeerBudget(peerId))
                {
                    _hostRequestsRejected++;
                    return;
                }

                if (!NetGameplayProbeManager.TryGetRuntimeObjectForSpawnIndex(request.TargetHostSpawnIndex, out object? runtimeObject)
                    || runtimeObject is not Npc npc || npc == null)
                {
                    _hostRequestsRejected++;
                    if (Plugin.Cfg.LogUnitStatusSync.Value)
                        NetLogger.Warn($"[UnitStatus] REJECT no-target peer={peerId} seq={request.RequestSeq} hostIdx={request.TargetHostSpawnIndex}");
                    return;
                }

                // Type guard — the addressed index must still hold the kind of unit the client aimed at.
                if (!string.IsNullOrEmpty(request.TargetUnitIdentifier)
                    && NetGameplayProbeManager.TryGetHostEntityBinding(npc, out _, out string hostUnitId)
                    && !string.IsNullOrEmpty(hostUnitId)
                    && !string.Equals(hostUnitId, request.TargetUnitIdentifier, StringComparison.Ordinal))
                {
                    _hostRequestsRejected++;
                    if (Plugin.Cfg.LogUnitStatusSync.Value)
                        NetLogger.Warn($"[UnitStatus] REJECT type-mismatch peer={peerId} seq={request.RequestSeq} client={request.TargetUnitIdentifier} host={hostUnitId}");
                    return;
                }

                if (!npc.IsAlive || npc.Stats == null) { _hostRequestsRejected++; return; }

                // The host's own player stands in as the applying unit: this end has no Unit for a remote player that the
                // status bookkeeping would accept, and the one vanilla consumer of that attribution — frozen-solid's
                // shatter kill — already falls back to exactly this unit when the source is unknown.
                Unit? source = null;
                try { source = GameManager.Instance != null ? GameManager.Instance.PlayerUnit : null; } catch { }

                var attrs  = request.Attributes ?? Array.Empty<ushort>();
                var values = request.Values     ?? Array.Empty<float>();
                int count = Math.Min(attrs.Length, values.Length);
                if (count > NetClientUnitStatusRequest.MaxEntries) count = NetClientUnitStatusRequest.MaxEntries;

                int applied = 0;
                for (int i = 0; i < count; i++)
                {
                    ushort attribute = attrs[i];
                    float value = values[i];
                    if (!IsSyncableStatus(attribute) || !IsSaneValue(value))
                    {
                        if (Plugin.Cfg.LogUnitStatusSync.Value)
                            NetLogger.Warn($"[UnitStatus] REJECT entry peer={peerId} seq={request.RequestSeq} attr={attribute} value={value}");
                        continue;
                    }

                    var id = (EntityAttributes)attribute;
                    if (source != null) npc.RegisterAppliedStatus(id, source);
                    // Vanilla path: resistances, diminishing returns, the protected-NPC guard and the per-attribute cap
                    // all live inside ModifyStatus, and its owner callback raises the effect + the ST-2 start edge.
                    npc.Stats.ModifyStatus(id, value);
                    applied++;
                }

                if (applied > 0) _hostRequestsApplied++; else _hostRequestsRejected++;

                if (Plugin.Cfg.LogUnitStatusSync.Value)
                    NetLogger.Info($"[UnitStatus] host applied peer={peerId} seq={request.RequestSeq} hostIdx={request.TargetHostSpawnIndex} unit={npc.name} entries={applied}/{count}");
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[UnitStatus] HandleClientStatusRequest failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static bool ConsumePeerBudget(string peerId)
        {
            string key = peerId ?? "";
            float now = Time.realtimeSinceStartup;
            _hostPeerBudget.TryGetValue(key, out var slot);
            if (now - slot.WindowStart >= 1f) slot = (now, 0);
            if (slot.Count >= MaxRequestsPerPeerPerSecond)
            {
                _hostPeerBudget[key] = slot;
                if (Plugin.Cfg.LogUnitStatusSync.Value)
                    NetLogger.Warn($"[UnitStatus] REJECT rate-limit peer={peerId}");
                return false;
            }
            _hostPeerBudget[key] = (slot.WindowStart, slot.Count + 1);
            return true;
        }

        // ----------------------------------------------------------------
        // ST-2 / ST-3 — host side: broadcast start / raise / end
        // ----------------------------------------------------------------

        private static int _hostEdgeSeq;
        private static int _hostEdgesSent;
        private static int _hostEdgesRaise;        // ST-3: the stacking increments the original cut dropped
        private static int _hostEdgesCoalesced;    // raises suppressed by the per-(unit,attribute) interval
        private static int _hostFrozenSolidSent;   // raises carrying FlagFrozenSolid

        /// <summary>Minimum spacing between RAISE messages for one (unit, attribute). Starts, ends and a frozen-solid
        /// raise are never coalesced. A raise is normally one per landed proc, which is a rare event — but a unit
        /// standing in a hazard volume can be topped up every frame, and this channel must not become a per-frame
        /// stream. Safe to drop a raise precisely because every message carries the CURRENT value rather than a delta:
        /// the next one states the truth regardless of what was skipped.</summary>
        private const float RaiseCoalesceSeconds = 0.1f;
        private static readonly Dictionary<long, float> _hostRaiseLastSentAt = new Dictionary<long, float>();

        private static long RaiseKey(int spawnIndex, ushort attribute) => ((long)spawnIndex << 16) | attribute;

        // Frozen-solid arming. Set while the host is inside AttributeEffect.ReApplyEffect for a Frozen status on this
        // unit — the only place vanilla's shatter roll happens. The roll's success is a nested SetStatus(Frozen,100)
        // that re-enters OnStatusUpdated, so an edge reported for this unit while armed, at 100, IS the shatter.
        //
        // It has to be a DEPTH, not a flag. Vanilla re-enters itself: the shatter's SetStatus(Frozen,100) raises
        // OnStatusUpdated, whose `newValue > 0` branch calls ReApplyEffect AGAIN, and only the innermost of those calls
        // breaks out. Our OnStatusUpdated postfix — the thing that reports the edge — runs AFTER the nested
        // ReApplyEffect has already returned and run its own postfix, so a single flag would be cleared by the inner
        // call before the edge carrying the 100 was ever reported, and the shatter would cross the wire unlabelled.
        //
        // Frame- and unit-bounded on top of that: a Harmony postfix does not run when the original throws, so the
        // depth could otherwise leak and label an unrelated later write as a shatter.
        private static Unit? _hostFrozenReapplyUnit;
        private static int   _hostFrozenReapplyFrame = -1;
        private static int   _hostFrozenReapplyDepth;

        public static void BeginHostFrozenReapply(Unit unit)
        {
            int frame = Time.frameCount;
            if (frame != _hostFrozenReapplyFrame || !ReferenceEquals(_hostFrozenReapplyUnit, unit))
            {
                _hostFrozenReapplyUnit  = unit;
                _hostFrozenReapplyFrame = frame;
                _hostFrozenReapplyDepth = 0;
            }
            _hostFrozenReapplyDepth++;
        }

        public static void EndHostFrozenReapply(Unit unit)
        {
            if (!ReferenceEquals(_hostFrozenReapplyUnit, unit)) return;
            if (--_hostFrozenReapplyDepth > 0) return;
            _hostFrozenReapplyUnit  = null;
            _hostFrozenReapplyFrame = -1;
            _hostFrozenReapplyDepth = 0;
        }

        private static bool IsHostFrozenReapplyArmed(Unit unit)
            => _hostFrozenReapplyDepth > 0
            && ReferenceEquals(_hostFrozenReapplyUnit, unit)
            && _hostFrozenReapplyFrame == Time.frameCount;

        /// <summary>
        /// Host: <c>Unit.OnStatusUpdated</c> fired. Broadcast a START, a RAISE or an END. Decay is the one transition
        /// that does NOT travel — it is per-frame, and every end runs the same vanilla decay for itself.
        /// </summary>
        public static void ReportHostUnitStatusEdge(Unit unit, EntityAttributes id, float prevValue, float newValue)
        {
            try
            {
                if (NetConfig.GetMode() != NetMode.Host) return;
                if (unit == null || unit is not Npc) return;

                // Cheapest discriminator first: this callback also carries every health change and every per-frame
                // status decay. Decay is a decrease and is the only common case, so it is rejected before anything
                // else is read.
                bool started = prevValue <= 0f && newValue > 0f;
                bool ended   = prevValue > 0f && newValue <= 0f;
                bool raised  = newValue > prevValue && newValue > 0f;
                if (!started && !ended && !raised) return;

                ushort attribute = (ushort)id;
                if (!IsSyncableStatus(attribute)) return;

                if (!NetGameplayProbeManager.TryGetHostEntityBinding(unit, out int spawnIndex, out string unitIdentifier))
                    return; // untracked unit — no client has a puppet bound to it
                if (!NetRunStateBridge.TryGetLocalRunState(out var state) || !state.HasLevel) return;

                // The value that is TRUE RIGHT NOW, not the one this invocation was handed. See NetHostUnitStatusState:
                // the frozen-solid branch writes 100 from inside this very callback (so the outer invocation's newValue
                // is already stale by the time it reaches us), and SetStatus passes its argument through unclamped.
                float value = Mathf.Clamp(unit.Stats != null ? unit.Stats.GetStatus(id) : newValue, 0f, MaxStatusValue);

                bool frozenSolid = id == EntityAttributes.NegativeEffect_Frozen
                                && value >= 100f
                                && IsHostFrozenReapplyArmed(unit);

                // Coalesce plain raises only. A start, an end and a shatter each carry information no later message
                // reconstructs, so they always go.
                bool plainRaise = raised && !started && value > 0f && !frozenSolid;
                long key = RaiseKey(spawnIndex, attribute);
                float now = Time.realtimeSinceStartup;
                if (plainRaise)
                {
                    if (_hostRaiseLastSentAt.TryGetValue(key, out float lastAt) && now - lastAt < RaiseCoalesceSeconds)
                    {
                        _hostEdgesCoalesced++;
                        return;
                    }
                    _hostRaiseLastSentAt[key] = now;
                }
                else if (value <= 0f)
                {
                    _hostRaiseLastSentAt.Remove(key); // effect is over — don't hold a throttle slot for it
                }

                NetGameplaySyncBridge.BroadcastHostUnitStatus(new NetHostUnitStatusState
                {
                    ChapterName    = state.ChapterName,
                    LevelIndex     = state.LevelIndex,
                    HasLevelSeed   = state.HasLevelSeed,
                    LevelSeed      = state.LevelSeed,
                    HostSpawnIndex = spawnIndex,
                    UnitIdentifier = unitIdentifier,
                    Attribute      = attribute,
                    Value          = value,
                    FrozenSolid    = frozenSolid,
                    Sequence       = ++_hostEdgeSeq,
                    SentAt         = now,
                });
                _hostEdgesSent++;
                if (plainRaise)  _hostEdgesRaise++;
                if (frozenSolid) _hostFrozenSolidSent++;

                if (Plugin.Cfg.LogUnitStatusSync.Value)
                {
                    string kind = value <= 0f ? "end" : started ? "start" : "raise";
                    NetLogger.Info($"[UnitStatus] host→clients seq={_hostEdgeSeq} hostIdx={spawnIndex} unit={unitIdentifier} " +
                                   $"{id}={value:F1} ({kind}{(frozenSolid ? ",FROZEN-SOLID" : "")})");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[UnitStatus] ReportHostUnitStatusEdge failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ----------------------------------------------------------------
        // ST-2 — client side: mirror onto the bound puppet
        // ----------------------------------------------------------------

        private static int _clientEdgesApplied;
        private static int _clientEdgesDropped;
        private static int _clientEdgesDroppedInactive;   // ST-2-INACTIVE

        public static void ApplyHostUnitStatus(NetHostUnitStatusState msg)
        {
            if (msg == null) return;
            try
            {
                if (NetConfig.GetMode() != NetMode.Client) return;
                if (!NetRunStateBridge.TryGetLocalRunState(out var state) || !msg.MatchesScene(state)) { _clientEdgesDropped++; return; }
                if (!IsSyncableStatus(msg.Attribute)) { _clientEdgesDropped++; return; }

                float value = msg.Value;
                if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f || value > MaxStatusValue) { _clientEdgesDropped++; return; }

                if (!NetGameplayProbeManager.TryGetHostBoundRuntimeObject(msg.HostSpawnIndex, out object? runtimeObject)
                    || runtimeObject is not Npc npc || npc == null || npc.Stats == null)
                {
                    _clientEdgesDropped++;
                    // Carry the value: an END edge (0) with no bound puppet is the normal, harmless case — the host's
                    // Npc.Die clears every status, and by then the death mirror has already released the puppet.
                    if (Plugin.Cfg.LogUnitStatusSync.Value)
                        NetLogger.Info($"[UnitStatus] client drop seq={msg.Sequence} hostIdx={msg.HostSpawnIndex} " +
                                       $"{(EntityAttributes)msg.Attribute}={value:F1} ({(value > 0f ? "start" : "end")}) (no bound puppet)");
                    return;
                }

                // ST-2-INACTIVE: never write onto a puppet the NPC LOD has switched off. The owner callback below is
                // the whole point of this mirror, and it ends in `AttributeEffect` starting a per-effect coroutine:
                //     Coroutine item = unit.StartCoroutine(routine);
                //     value.Add(item);            // unconditional
                // `StartCoroutine` returns NULL on an inactive GameObject, and vanilla adds that null to the unit's
                // `effectUpdates` list regardless. Every later `RemoveEffect` then walks the list and logs one
                // "Unit 'X' has a null coroutine in its effect update list" per null — 28 of them on a single unit in
                // Log534, none of which reach the BepInEx log. Each carries a full native stack, and resolving one of
                // those is what cost seconds per frame in the RR-INACTIVE stall, so this is not a cosmetic concern.
                // Skipping loses nothing: a deactivated puppet renders nothing, and the host re-broadcasts the status
                // edges, so the next edge after it comes back reinstates the right state.
                if (!npc.gameObject.activeInHierarchy)
                {
                    _clientEdgesDroppedInactive++;
                    if (Plugin.Cfg.LogUnitStatusSync.Value)
                        NetLogger.Info($"[UnitStatus] client drop seq={msg.Sequence} hostIdx={msg.HostSpawnIndex} " +
                                       $"{(EntityAttributes)msg.Attribute}={value:F1} (puppet GameObject inactive)");
                    return;
                }

                var attributeId = (EntityAttributes)msg.Attribute;

                // Absolute write with the owner callback ON: that callback is what raises/removes the vanilla effect
                // (material, VFX, animator, movement speed) — the whole point of mirroring the status at all.
                if (msg.FrozenSolid && attributeId == EntityAttributes.NegativeEffect_Frozen)
                    ApplyHostFrozenSolid(npc, value);
                else
                    npc.Stats.SetStatus(attributeId, value);
                _clientEdgesApplied++;

                if (Plugin.Cfg.LogUnitStatusSync.Value)
                    NetLogger.Info($"[UnitStatus] client applied seq={msg.Sequence} hostIdx={msg.HostSpawnIndex} {attributeId}={value:F1}" +
                                   (msg.FrozenSolid ? " (FROZEN-SOLID)" : ""));
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[UnitStatus] ApplyHostUnitStatus failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ----------------------------------------------------------------
        // ST-3 — client side: the frozen-solid decision belongs to the host
        // ----------------------------------------------------------------
        //
        // Vanilla rolls for the shatter inside AttributeEffect.ReApplyEffect, which every RAISE now reaches on the
        // client too. Left alone that would make the client decide, for itself, whether a host-owned enemy turns to
        // ice — and the odds are not marginal: frozenSolidChance(80) is ~0.95, so once frost is stacked on a wounded
        // enemy the client would shatter its puppet on very nearly every increment. The consequences are not cosmetic
        // either. The roll stops the status' own decay coroutines and pins it at 100, so a puppet that shattered
        // locally stays an ice block — dragged around by the host's transform, since the real enemy is still walking —
        // until the host's end edge finally arrives. IsFrozenSolid also changes how the unit gibs and bleeds.
        //
        // So the client never rolls (SuppressClientFrozenSolidRoll below) and instead replays the host's decision when
        // the host states it. Nothing is lost by suppressing: the roll's other half, ReceiveDamage(+infinity), was
        // already swallowed on clients by TrySendClientHitRequest's non-finite guard, so the shatter never killed the
        // puppet here anyway — the host's death mirror does that.

        private static int _clientFrozenRollsSuppressed;
        private static int _clientShattersReplayed;
        private static int _clientShatterUnreached;
        private static int _clientShatterUnobservable;

        /// <summary>Set only while this class is driving a host-authorised shatter through vanilla.</summary>
        private static bool _clientShatterAuthorised;

        /// <summary>
        /// Client: reproduce the host's frozen-solid on a bound puppet by letting VANILLA do it, rather than
        /// re-implementing the ice material, the animator bool, the shatter sound and the gib mode by hand.
        /// <para>Two things have to hold for <c>ReApplyEffectImp</c> to reach its shatter branch: the write must be an
        /// INCREASE, and the roll must pass. The increase is guaranteed by nudging a puppet that is somehow already at
        /// the cap back below it (an unobservable write — owner callback off); the roll is made certain by the game's
        /// own <c>GlobalSettings.Debug.MaxFrozenSolidChance</c>, which forces the health term to 1, against a value of
        /// 100 where the value term is already 1. The window is strictly synchronous — <c>ReApplyEffectImp</c> does its
        /// roll before it starts any coroutine — so no other unit, and no player, can be rolled inside it.</para>
        /// </summary>
        private static void ApplyHostFrozenSolid(Npc npc, float value)
        {
            const EntityAttributes frozen = EntityAttributes.NegativeEffect_Frozen;
            float target = Mathf.Max(value, 100f);

            // Vanilla breaks out of the shatter branch unless newValue > prevValue.
            if (npc.Stats.GetStatus(frozen) >= target)
                npc.Stats.SetStatus(frozen, target - 1f, skipOwnerCallback: true);

            // ST-3-PROBE. Whether the branch RAN is taken from the branch's own first synchronous act: before it pins
            // the value and starts DelayedFrozenSolid it stops and CLEARS this status' effect-update coroutines.
            //
            // `IsFrozenSolid` cannot answer this question, and the first cut's use of it was worthless: it is
            // `GetStatus(Frozen) >= 100`, which this method has just written itself, so it reads true whether or not
            // the branch was ever reached. Log541/Log17 therefore reported a full count of replayed shatters while the
            // maintainer was watching the ice fail to appear.
            int coroutinesBefore = CountEffectUpdates(npc, frozen);

            bool previousMaxChance = GlobalSettings.Debug.MaxFrozenSolidChance;
            _clientShatterAuthorised = true;
            try
            {
                GlobalSettings.Debug.MaxFrozenSolidChance = true;
                npc.Stats.SetStatus(frozen, target);
            }
            finally
            {
                GlobalSettings.Debug.MaxFrozenSolidChance = previousMaxChance;
                _clientShatterAuthorised = false;
            }

            int coroutinesAfter = CountEffectUpdates(npc, frozen);

            if (coroutinesBefore > 0 && coroutinesAfter == 0)
            {
                _clientShattersReplayed++;
                return;
            }

            if (coroutinesBefore == 0)
            {
                // Nothing was there to be cleared, so the probe cannot separate "ran" from "refused" — the puppet had
                // no live effect coroutine for this status (it already shattered here, or the effect was raised while
                // the GameObject could not start one). Counted apart so it inflates neither verdict.
                _clientShatterUnobservable++;
                if (Plugin.Cfg.LogUnitStatusSync.Value)
                    NetLogger.Info($"[UnitStatus] client shatter unobservable unit={npc.name} (no live Frozen effect coroutine to clear)");
                return;
            }

            // The branch was reached and refused. The only vanilla guard left is
            // `newValue >= 100 && (health <= 0 || !IsAlive)`, so this is the mirrored death/health landing ahead of the
            // flagged edge. Dump both of its inputs — that is the whole diagnosis.
            _clientShatterUnreached++;
            NetLogger.Info($"[UnitStatus] client SHATTER MISSED unit={npc.name} " +
                           $"hp={npc.Stats.GetStatus(EntityAttributes.Status_CurrentHealth):F1} alive={npc.IsAlive} " +
                           $"state={npc.unitState} frozen={npc.Stats.GetStatus(frozen):F1} coroutines={coroutinesBefore}→{coroutinesAfter}");
        }

        /// <summary>Live effect-update coroutines vanilla is holding for this status on this unit. Public field on
        /// <c>Unit</c>; read defensively because it is the game's own collection.</summary>
        private static int CountEffectUpdates(Unit unit, EntityAttributes id)
        {
            try
            {
                var map = unit.effectUpdates;
                if (map != null && map.TryGetValue(id, out var list) && list != null) return list.Count;
            }
            catch { }
            return 0;
        }

        /// <summary>
        /// Client: true when the caller must SKIP vanilla's <c>AttributeEffect.ReApplyEffect</c> for this unit because
        /// running it would let this end decide the shatter. Scoped as tightly as it can be — client only, Frozen only,
        /// host-bound puppets only, and never while <see cref="ApplyHostFrozenSolid"/> is deliberately driving it.
        /// <para>The caller is responsible for the presentational half that is being skipped along with the roll; see
        /// <c>UnitStatusPatches</c>. Only the Frozen branch is suppressed — the same method also carries Charmed's
        /// faction handover and Bleed's blood puff, which are presentation and must keep running.</para>
        /// </summary>
        public static bool SuppressClientFrozenSolidRoll(Unit unit, EntityAttributes id)
        {
            if (id != EntityAttributes.NegativeEffect_Frozen) return false;
            if (_clientShatterAuthorised) return false;

            // ST-3c: the pre-death assert wants the VALUE and the frost material, never a roll — and it may run on a
            // puppet whose binding was already released, which the ordinary test below would wave through to vanilla.
            if (_clientDeathFrozenAssertInProgress)
            {
                _clientFrozenRollsSuppressed++;
                return true;
            }

            if (NetConfig.GetMode() != NetMode.Client) return false;
            if (unit is not Npc) return false;
            if (!NetGameplayProbeManager.TryGetClientPuppetBinding(unit, out _, out _)) return false;

            _clientFrozenRollsSuppressed++;
            return true;
        }

        // ----------------------------------------------------------------
        // ST-3c — frozen-solid AT DEATH is host-stated too
        // ----------------------------------------------------------------
        //
        // `IsFrozenSolid` is `GetStatus(Frozen) >= 100f` and needs no shatter: an enemy killed while its frost sits at
        // the cap dies as an ice statue, and `Unit.UpdatePhysicsEnabling` then refuses to settle the corpse into a
        // ragdoll. The host meets that threshold by construction — the killing bullet applies the frost and only then
        // the damage, in one call — while a client, receiving the two as separate messages and running the vanilla
        // decay between them, sits a fraction under the cap and loses an exact comparison. See
        // NetGameplayDeathEvent.FlagFrozenSolid.

        private static bool _clientDeathFrozenAssertInProgress;
        private static int  _clientDeathFrozenAsserted;
        private static int  _clientDeathFrozenAlready;

        /// <summary>Host: was this unit frozen-solid as it died? Read at the death-mirror broadcast, which is safe
        /// because <c>Die</c> does not clear the status (no end edge follows a shatter — established in Log541).</summary>
        public static bool IsUnitFrozenSolid(object? runtimeObject)
        {
            try
            {
                return runtimeObject is Npc npc && npc != null && npc.Stats != null && npc.IsFrozenSolid;
            }
            catch { return false; }
        }

        /// <summary>Client: the host says this unit was frozen-solid when it died, so make that true here before the
        /// mirrored <c>Die()</c> reads it. Written through the normal owner callback so vanilla takes the frost
        /// material to full coverage by its own path; the roll that callback could otherwise reach is suppressed for
        /// the duration.</summary>
        public static void AssertFrozenSolidForIncomingDeath(object? runtimeObject)
        {
            try
            {
                if (NetConfig.GetMode() != NetMode.Client) return;
                if (runtimeObject is not Npc npc || npc == null || npc.Stats == null) return;
                if (npc.IsFrozenSolid) { _clientDeathFrozenAlready++; return; }

                // ST-2-INACTIVE: never drive the effect pipeline onto a puppet the NPC LOD switched off.
                if (!npc.gameObject.activeInHierarchy) return;

                _clientDeathFrozenAssertInProgress = true;
                try { npc.Stats.SetStatus(EntityAttributes.NegativeEffect_Frozen, 100f); }
                finally { _clientDeathFrozenAssertInProgress = false; }

                _clientDeathFrozenAsserted++;
                if (Plugin.Cfg.LogUnitStatusSync.Value)
                    NetLogger.Info($"[UnitStatus] client asserted frozen-solid before death unit={npc.name}");
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[UnitStatus] AssertFrozenSolidForIncomingDeath failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ----------------------------------------------------------------

        private static string DescribeEntries(List<ushort> attrs, List<float> values)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < attrs.Count && i < values.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append((EntityAttributes)attrs[i]).Append('=').Append(values[i].ToString("F1"));
            }
            return sb.ToString();
        }

        public static string FormatSummary()
            => $"clientIntercepted={_clientIntercepted} clientForwarded={_clientForwarded} " +
               $"clientNoProc={_clientNoProc} clientNotLocalAttacker={_clientNotLocalAttacker} clientLocalOnly={_clientLocalOnlyApplied} " +
               $"hostRecv={_hostRequestsRecv} hostApplied={_hostRequestsApplied} hostRejected={_hostRequestsRejected} " +
               $"hostEdgesSent={_hostEdgesSent} hostEdgesRaise={_hostEdgesRaise} hostEdgesCoalesced={_hostEdgesCoalesced} hostFrozenSolid={_hostFrozenSolidSent} " +
               $"clientEdgesApplied={_clientEdgesApplied} clientEdgesDropped={_clientEdgesDropped} clientEdgesDroppedInactive={_clientEdgesDroppedInactive} " +
               $"clientFrozenRollsSuppressed={_clientFrozenRollsSuppressed} clientShattersReplayed={_clientShattersReplayed} " +
               $"clientShatterUnreached={_clientShatterUnreached} clientShatterUnobservable={_clientShatterUnobservable} " +
               $"clientDeathFrozenAsserted={_clientDeathFrozenAsserted} clientDeathFrozenAlready={_clientDeathFrozenAlready}";
    }
}
