using System;
using System.Collections.Generic;
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

        public static string FormatSummary()
            => $"hostGibsSent={_hostGibsSent} clientGibsApplied={_clientGibsApplied} " +
               $"clientGibsNoCorpse={_clientGibsNoCorpse} clientGibsDuplicate={_clientGibsDuplicate}";
    }
}
