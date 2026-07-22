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
        // ST-2 — host side: broadcast start/end edges
        // ----------------------------------------------------------------

        private static int _hostEdgeSeq;
        private static int _hostEdgesSent;

        /// <summary>
        /// Host: <c>Unit.OnStatusUpdated</c> fired. Broadcast only the transitions — a start (<paramref name="prevValue"/>
        /// at or below zero, <paramref name="newValue"/> above) or an end (the reverse). Everything in between is the
        /// per-frame decay, which every end runs for itself.
        /// </summary>
        public static void ReportHostUnitStatusEdge(Unit unit, EntityAttributes id, float prevValue, float newValue)
        {
            try
            {
                if (NetConfig.GetMode() != NetMode.Host) return;
                if (unit == null || unit is not Npc) return;

                // Cheapest discriminator first: this callback also carries every health change and every per-frame
                // status decay, and neither is an edge.
                bool started = prevValue <= 0f && newValue > 0f;
                bool ended   = prevValue > 0f && newValue <= 0f;
                if (!started && !ended) return;

                ushort attribute = (ushort)id;
                if (!IsSyncableStatus(attribute)) return;

                if (!NetGameplayProbeManager.TryGetHostEntityBinding(unit, out int spawnIndex, out string unitIdentifier))
                    return; // untracked unit — no client has a puppet bound to it
                if (!NetRunStateBridge.TryGetLocalRunState(out var state) || !state.HasLevel) return;

                float value = started ? Mathf.Clamp(newValue, 0f, MaxStatusValue) : 0f;

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
                    Sequence       = ++_hostEdgeSeq,
                    SentAt         = Time.realtimeSinceStartup,
                });
                _hostEdgesSent++;

                if (Plugin.Cfg.LogUnitStatusSync.Value)
                    NetLogger.Info($"[UnitStatus] host→clients seq={_hostEdgeSeq} hostIdx={spawnIndex} unit={unitIdentifier} {id}={value:F1} ({(started ? "start" : "end")})");
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

                // Absolute write with the owner callback ON: that callback is what raises/removes the vanilla effect
                // (material, VFX, animator, movement speed) — the whole point of mirroring the status at all.
                npc.Stats.SetStatus((EntityAttributes)msg.Attribute, value);
                _clientEdgesApplied++;

                if (Plugin.Cfg.LogUnitStatusSync.Value)
                    NetLogger.Info($"[UnitStatus] client applied seq={msg.Sequence} hostIdx={msg.HostSpawnIndex} {(EntityAttributes)msg.Attribute}={value:F1}");
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[UnitStatus] ApplyHostUnitStatus failed: {ex.GetType().Name}: {ex.Message}");
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
               $"hostEdgesSent={_hostEdgesSent} clientEdgesApplied={_clientEdgesApplied} clientEdgesDropped={_clientEdgesDropped}";
    }
}
