using System;
using System.Collections.Generic;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Stats;
using PerfectRandom.Sulfur.Core.Units;
using UnityEngine;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// CG-1a: mirror a corpse bursting. See <see cref="NetHostCorpseGib"/> for what travels and why loot does not.
    ///
    /// <para><b>The identity problem this had to solve first.</b> ST forgets a corpse the moment it dies — the host
    /// death mirror releases the puppet and drops the roster binding both ways, which is correct for a unit that is
    /// gone but leaves the body with no cross-end name. That is the real reason no corpse interaction has ever been
    /// syncable. The binding tombstone the release already writes still carries the local key, so the body is
    /// reachable through it for as long as the tombstone lives, and that is what this uses rather than adding a second
    /// registry alongside it.</para>
    /// </summary>
    internal static class CorpseGibSyncManager
    {
        private static int _hostSeq;
        private static int _hostGibsSent;
        private static int _clientGibsApplied;
        private static int _clientGibsNoCorpse;
        private static int _clientGibsDuplicate;

        /// <summary>Spawn indices already burst on this end, so a re-delivered event cannot gib a body twice (or gib a
        /// fresh unit that later reuses the index — cleared with the rest of the per-level state).</summary>
        private static readonly HashSet<int> _gibbedSpawnIndices = new HashSet<int>();

        public static void Reset()
        {
            _gibbedSpawnIndices.Clear();
        }

        // ----------------------------------------------------------------
        // Host: the dead-unit branch burst a body
        // ----------------------------------------------------------------

        /// <summary>
        /// Host: <c>Npc.ReceiveDamage</c> returned true for a unit that was already dead. That return is exact — the
        /// dead-unit section returns true from its four gib branches and false from every other path — so it is the
        /// canonical "this corpse just burst" signal, and it does not require predicting which branch will fire or
        /// reproducing the <c>frozenDamageInstances</c> counting vanilla does for ranged hits.
        /// </summary>
        public static void ReportHostCorpseGib(object? unit, bool wasFrozenSolid)
        {
            try
            {
                if (NetConfig.GetMode() != NetMode.Host) return;
                if (unit is not Npc npc || npc == null) return;

                if (!NetGameplayProbeManager.TryGetHostEntityBinding(npc, out int spawnIndex, out string unitIdentifier))
                    return; // untracked body — no client has anything bound to it
                if (!_gibbedSpawnIndices.Add(spawnIndex)) return;
                if (!NetRunStateBridge.TryGetLocalRunState(out var state) || !state.HasLevel) return;

                NetGameplaySyncBridge.BroadcastHostCorpseGib(new NetHostCorpseGib
                {
                    ChapterName    = state.ChapterName,
                    LevelIndex     = state.LevelIndex,
                    HasLevelSeed   = state.HasLevelSeed,
                    LevelSeed      = state.LevelSeed,
                    HostSpawnIndex = spawnIndex,
                    UnitIdentifier = unitIdentifier,
                    FrozenSolid    = wasFrozenSolid,
                    Sequence       = ++_hostSeq,
                    SentAt         = Time.realtimeSinceStartup,
                });
                _hostGibsSent++;

                if (Plugin.Cfg.LogCorpseGibSync.Value)
                    NetLogger.Info($"[CorpseGib] host→clients seq={_hostSeq} idx={spawnIndex} unit={unitIdentifier} frozen={wasFrozenSolid}");
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[CorpseGib] ReportHostCorpseGib failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ----------------------------------------------------------------
        // Client: burst the matching body, and nothing else
        // ----------------------------------------------------------------

        public static void ApplyHostCorpseGib(NetHostCorpseGib msg)
        {
            if (msg == null) return;
            try
            {
                if (NetConfig.GetMode() != NetMode.Client) return;
                if (!NetRunStateBridge.TryGetLocalRunState(out var state) || !msg.MatchesScene(state)) return;

                if (!_gibbedSpawnIndices.Add(msg.HostSpawnIndex))
                {
                    _clientGibsDuplicate++;
                    return;
                }

                if (!NetGameplayProbeManager.TryGetClientCorpse(msg.HostSpawnIndex, out Npc? corpse) || corpse == null)
                {
                    _clientGibsNoCorpse++;
                    if (Plugin.Cfg.LogCorpseGibSync.Value)
                        NetLogger.Info($"[CorpseGib] client drop {msg.ToCompact()} (no local body)");
                    return;
                }

                // Vanilla's frozen branches force ice gibs; its explosive branch passes nothing and lets the unit's own
                // state pick. Reproduced exactly, with the flavour taken from the host rather than re-derived here.
                bool? frozen = msg.FrozenSolid ? true : (bool?)null;
                corpse.SpawnGibExplosion(null, null, setSubPartEffects: true, isFrozenSolid: frozen);
                _clientGibsApplied++;

                if (Plugin.Cfg.LogCorpseGibSync.Value)
                    NetLogger.Info($"[CorpseGib] client applied {msg.ToCompact()} unit={corpse.name}");
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[CorpseGib] ApplyHostCorpseGib failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ----------------------------------------------------------------
        // CG-1b — client → host: a hit on a body the host owns
        // ----------------------------------------------------------------

        private static int _clientCorpseHitSeq;
        private static int _clientCorpseHitsSent;
        private static int _hostCorpseHitsRecv;
        private static int _hostCorpseHitsApplied;
        private static int _hostCorpseHitsRejected;
        private static int _hostCorpseHitsRateLimited;   // split out: Log23 could not tell a full budget from a bad packet

        /// <summary>Per-peer arrival budget. Sized off what a multi-pellet weapon actually produces, not off "five
        /// hits": vanilla counts every pellet as its own <c>frozenDamageInstances</c>, so one shotgun blast is already
        /// several arrivals and a dropped one silently stalls a burst at four. Still bounded — a body disappears within
        /// a handful of hits, so a peer sustaining this rate is malformed or hostile either way.</summary>
        private const int MaxCorpseHitsPerPeerPerSecond = 60;
        private static readonly Dictionary<string, (float WindowStart, int Count)> _hostPeerBudget = new Dictionary<string, (float, int)>();

        /// <summary>
        /// Client: the local player damaged a body the host owns. Returns true when the caller must SKIP the vanilla
        /// application — always, when this claims the hit: letting it run would burst this end's copy alone, which is
        /// the desync being fixed.
        /// </summary>
        public static bool TryForwardClientCorpseHit(object? npc, float damage, int damageTypeInt, bool melee)
        {
            try
            {
                if (NetConfig.GetMode() != NetMode.Client) return false;
                if (npc is not Npc corpse || corpse == null) return false;
                if (corpse.IsAlive) return false;                       // living units keep the ordinary hit path
                if (float.IsNaN(damage) || float.IsInfinity(damage)) return false;

                // Only hits that could actually burst the body. Vanilla's dead-unit section can only reach a gib from
                // a frozen-solid corpse (melee, the fifth ranged hit, or a type that explodes corpses) or from
                // Explosive above 75 on any corpse; everything else falls through to local impact effects and returns
                // false. Claiming those too would forward a packet per shot AND silence the feedback on four of the
                // five it takes to shatter a statue, which is a worse experience than the desync being fixed. The
                // frozen test is trustworthy on a client now — ST-3c is what made it so.
                bool couldBurst = corpse.IsFrozenSolid
                               || (damageTypeInt == (int)DamageTypes.Explosive && damage > 75f);
                if (!couldBurst) return false;

                // Only a body the host owns. A client-only corpse is this end's business and vanilla is right for it.
                if (!NetGameplayProbeManager.TryGetClientCorpseSpawnIndex(corpse, out int hostIdx, out string unitIdentifier))
                    return false;

                if (!NetRunStateBridge.TryGetLocalRunState(out var state) || !state.HasLevel)
                    return true;   // claimed but unsendable — still must not burst locally

                NetGameplaySyncBridge.SendClientCorpseHit(new NetClientCorpseHit
                {
                    ChapterName          = state.ChapterName,
                    LevelIndex           = state.LevelIndex,
                    HasLevelSeed         = state.HasLevelSeed,
                    LevelSeed            = state.LevelSeed,
                    RequestSeq           = ++_clientCorpseHitSeq,
                    TargetHostSpawnIndex = hostIdx,
                    TargetUnitIdentifier = unitIdentifier,
                    Damage               = damage,
                    DamageTypeInt        = damageTypeInt,
                    Melee                = melee,
                    SentAt               = Time.realtimeSinceStartup,
                });
                _clientCorpseHitsSent++;

                if (Plugin.Cfg.LogCorpseGibSync.Value)
                    NetLogger.Info($"[CorpseGib] client→host seq={_clientCorpseHitSeq} idx={hostIdx} dmg={damage:F1} type={damageTypeInt} melee={melee}");

                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[CorpseGib] TryForwardClientCorpseHit failed: {ex.GetType().Name}: {ex.Message}");
                return false; // never swallow a hit because OUR code threw
            }
        }

        /// <summary>
        /// Host: replay a client's corpse hit through the REAL <c>Npc.ReceiveDamage</c>, so vanilla's dead-unit section
        /// gets to make the decision with its own <c>frozenDamageInstances</c> counter, its own loot roll and its own
        /// burst. If it bursts, the CG-1a postfix on that same call mirrors it back out to every end.
        /// </summary>
        public static void HandleClientCorpseHit(NetClientCorpseHit msg, string peerId)
        {
            if (msg == null) return;
            try
            {
                if (NetConfig.GetMode() != NetMode.Host) return;
                _hostCorpseHitsRecv++;

                if (!NetRunStateBridge.TryGetLocalRunState(out var hostState) || !msg.MatchesScene(hostState))
                { _hostCorpseHitsRejected++; return; }

                if (!ConsumePeerBudget(peerId)) { _hostCorpseHitsRateLimited++; return; }

                if (!NetGameplayProbeManager.TryGetRuntimeObjectForSpawnIndex(msg.TargetHostSpawnIndex, out object? runtimeObject)
                    || runtimeObject is not Npc corpse || corpse == null)
                { _hostCorpseHitsRejected++; return; }

                // Type guard, same shape as every other addressed-by-index channel here.
                if (!string.IsNullOrEmpty(msg.TargetUnitIdentifier)
                    && NetGameplayProbeManager.TryGetHostEntityBinding(corpse, out _, out string hostUnitId)
                    && !string.IsNullOrEmpty(hostUnitId)
                    && !string.Equals(hostUnitId, msg.TargetUnitIdentifier, StringComparison.Ordinal))
                { _hostCorpseHitsRejected++; return; }

                // The whole point is the dead-unit section. A living unit reached through this channel would take
                // real damage outside the validated hit path, so it is refused rather than clamped.
                if (corpse.IsAlive) { _hostCorpseHitsRejected++; return; }

                float damage = msg.Damage;
                if (float.IsNaN(damage) || float.IsInfinity(damage) || damage < 0f) { _hostCorpseHitsRejected++; return; }
                if (damage > MaxCorpseHitDamage) damage = MaxCorpseHitDamage;

                // DamageTypes is `: byte`. Handing Enum.IsDefined an int for a byte-backed enum does not return false,
                // it THROWS — which is how Log23 lost all 647 hits that got past the rate limit, each one caught by
                // this method's own catch and therefore counted as neither applied nor rejected.
                if (msg.DamageTypeInt < 0 || msg.DamageTypeInt > byte.MaxValue) { _hostCorpseHitsRejected++; return; }
                byte damageTypeByte = (byte)msg.DamageTypeInt;
                if (!Enum.IsDefined(typeof(DamageTypes), damageTypeByte)) { _hostCorpseHitsRejected++; return; }
                var damageType = (DamageTypes)damageTypeByte;

                Unit? source = null;
                try { source = GameManager.Instance != null ? GameManager.Instance.PlayerUnit : null; } catch { }

                var sourceData = new DamageSourceData
                {
                    name       = "CoopCorpseHit",
                    damageType = damageType,
                    melee      = msg.Melee,
                    isPlayer   = true,
                    sourceUnit = source!,
                };

                corpse.ReceiveDamage(damage, sourceData, Hitmesh.Data.Default);
                _hostCorpseHitsApplied++;

                if (Plugin.Cfg.LogCorpseGibSync.Value)
                    NetLogger.Info($"[CorpseGib] host applied peer={peerId} {msg.ToCompact()} unit={corpse.name}");
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[CorpseGib] HandleClientCorpseHit failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Explosive over 75 bursts any body, so the ceiling only has to be above the thresholds vanilla
        /// reads — it is an anti-forgery bound, not a balance figure.</summary>
        private const float MaxCorpseHitDamage = 10000f;

        private static bool ConsumePeerBudget(string peerId)
        {
            string key = peerId ?? "";
            float now = Time.realtimeSinceStartup;
            _hostPeerBudget.TryGetValue(key, out var slot);
            if (now - slot.WindowStart >= 1f) slot = (now, 0);
            if (slot.Count >= MaxCorpseHitsPerPeerPerSecond) { _hostPeerBudget[key] = slot; return false; }
            _hostPeerBudget[key] = (slot.WindowStart, slot.Count + 1);
            return true;
        }

        public static string FormatSummary()
            => $"hostGibsSent={_hostGibsSent} clientGibsApplied={_clientGibsApplied} " +
               $"clientGibsNoCorpse={_clientGibsNoCorpse} clientGibsDuplicate={_clientGibsDuplicate} " +
               $"clientCorpseHitsSent={_clientCorpseHitsSent} hostCorpseHitsRecv={_hostCorpseHitsRecv} " +
               $"hostCorpseHitsApplied={_hostCorpseHitsApplied} hostCorpseHitsRejected={_hostCorpseHitsRejected} " +
               $"hostCorpseHitsRateLimited={_hostCorpseHitsRateLimited}";
    }
}
