using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using SULFURTogether.Networking.Gameplay.Boss;

namespace SULFURTogether.Networking.Gameplay
{
    /// <summary>
    /// IND-1: Independent-mode client→host world-card routing (companion slice). See Docs/EndlessModeSyncPlan.md §5.4.
    ///
    /// <para>In Independent mode each player picks its own cards and spawns world objects locally. That is fine for a shop
    /// / chest / station / plain loot (personal, functional local objects), but a <b>companion</b> is a fighting ally —
    /// and the client's enemies are host-authoritative puppets, so a client-local companion's attacks never register. The
    /// client therefore suppresses its local companion spawn and asks the host to spawn the authoritative companion.</para>
    ///
    /// <list type="bullet">
    /// <item><b>CLIENT</b> (<see cref="ClientRouteCompanion"/>, from the <c>SpawnCompanion</c> prefix): read the resolved
    /// <c>UnitSO</c> id and send <see cref="NetEndlessWorldCard"/>; the local spawn is suppressed.</item>
    /// <item><b>HOST</b> (<see cref="HostHandleWorldCard"/>): spawn the companion via the game's own <c>SpawnCompanion</c>
    /// (so all the stat modifiers / onDeath / bookkeeping happen), with the charm target redirected to the requesting
    /// peer's <b>ghost unit</b> (so it follows the picker, not the host — see <see cref="ConsumeOnBehalfCharmTarget"/>).
    /// The host's <c>SpawnCompanion</c> bracket classifies the spawn for RuntimeSpawn, so it mirrors back to every client
    /// as a puppet; the picker's client re-applies the charmed presentation to its own player (EM-7c).</item>
    /// </list>
    ///
    /// <para>Known limitation: the companion spawns at the host's position and walks to the picker (it is charmed to the
    /// picker's ghost); in a shared Endless arena that is a short, host-consistent traversal. With 3+ players the mirrored
    /// puppet on a non-picker client charms to that client's own player (visual only).</para>
    /// </summary>
    internal static class EndlessWorldCardManager
    {
        private static bool Enabled { get { try { return Plugin.Cfg.EnableEndlessSync.Value; } catch { return false; } } }
        private static bool LogOn  { get { try { return Plugin.Cfg.LogEndlessSync.Value; } catch { return false; } } }

        public static int ClientCompanionRouted;
        public static int HostCompanionSpawnedForPeer;

        // Host: while set, the next Npc.ApplyForcedCharmed(owner, heart) swaps its owner to this ghost unit (consume-once),
        // so a companion the host spawns on behalf of a client follows the picker instead of the host. Set immediately
        // before invoking SpawnCompanion; the flag survives its internal await because it is a static field.
        private static object? _onBehalfCharmTarget;

        /// <summary>HOST: consume the pending on-behalf charm target (used by the ApplyForcedCharmed prefix).</summary>
        public static object? ConsumeOnBehalfCharmTarget()
        {
            var t = _onBehalfCharmTarget;
            _onBehalfCharmTarget = null;
            return t;
        }

        // ---- reflection ----
        private static bool _resolved;
        private static MethodInfo? _spawnCompanion; // FloatingCardManager.SpawnCompanion(UnitSO) : void (async void)

        private static void EnsureResolved()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                var fcmType = AccessTools.TypeByName("FloatingCardManager") ?? AccessTools.TypeByName("PerfectRandom.Sulfur.Core.FloatingCardManager");
                if (fcmType != null)
                    foreach (var m in fcmType.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
                        if (m.Name == "SpawnCompanion" && m.GetParameters().Length == 1) { _spawnCompanion = m; break; }
                Plugin.Log.Info($"[Endless] IND-1 resolved spawnCompanion={_spawnCompanion != null}");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] IND-1 EnsureResolved failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        // ================================================================== CLIENT

        /// <summary>CLIENT (Independent mode): route a locally-picked companion to the host instead of spawning it locally.</summary>
        public static void ClientRouteCompanion(object unitSO)
        {
            try
            {
                if (!Enabled || unitSO == null || NetGameplaySyncBridge.BossMode != NetMode.Client) return;
                int unitId = RuntimeSpawnManager.ReadUnitIdValuePublic(unitSO);
                if (unitId == 0) { if (LogOn) Plugin.Log.Info("[Endless] IND-1 client companion: no unitId, cannot route"); return; }

                if (!NetBossEncounterManager.TryGetRunContext(out string chap, out int lvl, out _, out _)) { chap = ""; lvl = -1; }
                NetGameplaySyncBridge.SendEndlessWorldCard(new NetEndlessWorldCard
                {
                    Kind = NetEndlessWorldCard.KindCompanion,
                    UnitIdValue = unitId,
                    ChapterName = chap,
                    LevelIndex  = lvl,
                });
                ClientCompanionRouted++;
                if (LogOn) Plugin.Log.Info($"[Endless] IND-1 client routed companion unitId={unitId} to host");
            }
            catch (Exception ex) { Plugin.Log.Warn($"[Endless] IND-1 ClientRouteCompanion failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        // ================================================================== HOST

        /// <summary>HOST: a client asked us to spawn a companion for its pick. Spawn it via the game's own SpawnCompanion,
        /// with the charm redirected to the requesting peer's ghost unit so it follows the picker.</summary>
        public static void HostHandleWorldCard(string peerId, NetEndlessWorldCard msg)
        {
            try
            {
                if (!Enabled || msg == null || NetGameplaySyncBridge.BossMode != NetMode.Host) return;
                if (msg.Kind != NetEndlessWorldCard.KindCompanion) return;

                // Run-context guard: ignore a pick for a level the host isn't in.
                if (NetBossEncounterManager.TryGetRunContext(out string chap, out int lvl, out _, out _)
                    && (!string.Equals(chap, msg.ChapterName, StringComparison.Ordinal) || lvl != msg.LevelIndex))
                { if (LogOn) Plugin.Log.Info($"[Endless] IND-1 host drop companion (run mismatch) peer={peerId} {msg.ChapterName}:{msg.LevelIndex} local={chap}:{lvl}"); return; }

                EnsureResolved();
                if (_spawnCompanion == null) { Plugin.Log.Warn("[Endless] IND-1 host: SpawnCompanion unresolved — cannot spawn companion for client"); return; }

                object? fcm = EndlessCardManager.ResolveLocalCardManager();
                object? unitSO = RuntimeSpawnManager.ResolveUnitSOPublic(msg.UnitIdValue);
                if (fcm == null || unitSO == null) { Plugin.Log.Warn($"[Endless] IND-1 host: cannot resolve fcm/unitSO (unitId={msg.UnitIdValue}) for peer={peerId}"); return; }

                // Charm target = the picker's ghost unit (follows the picker). If it isn't available yet, fall back to the
                // host's own charm (companion still exists + fights; it just follows the host until we have the ghost).
                object? ghost = null;
                lock (RemotePlayerRegistryManager.GhostUnitsByPeer)
                    RemotePlayerRegistryManager.GhostUnitsByPeer.TryGetValue(peerId, out ghost);
                _onBehalfCharmTarget = (ghost is UnityEngine.Object go && go != null) ? ghost : null;

                _spawnCompanion.Invoke(fcm, new[] { unitSO }); // vanilla SpawnCompanion: brackets RuntimeSpawn, spawns, charm-redirected
                HostCompanionSpawnedForPeer++;
                if (LogOn) Plugin.Log.Info($"[Endless] IND-1 host spawned companion for peer={peerId} unitId={msg.UnitIdValue} charmTo={(_onBehalfCharmTarget != null || ghost != null ? "picker" : "host(fallback)")}");
            }
            catch (Exception ex) { _onBehalfCharmTarget = null; Plugin.Log.Warn($"[Endless] IND-1 HostHandleWorldCard failed: {ex.GetType().Name}: {ex.Message}"); }
        }
    }
}
